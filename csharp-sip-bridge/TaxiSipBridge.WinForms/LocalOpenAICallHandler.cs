using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Call handler that routes audio directly to OpenAI Realtime API.
/// Uses G711AiSipPlayout for timer-driven RTP delivery with proper G.711 encoding.
/// </summary>
public class LocalOpenAICallHandler : ISipCallHandler
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private volatile bool _isBotSpeaking;
    private DateTime _botStoppedSpeakingAt = DateTime.MinValue;
    private const int ECHO_GUARD_MS = 400; // Suppress inbound audio for 400ms after Ada stops
    
    private VoIPMediaSession? _currentMediaSession;
    private G711AiSipPlayout? _playout;
    private OpenAIRealtimeClient? _aiClient;
    private CancellationTokenSource? _callCts;

    private const int FLUSH_PACKETS = 150;     // Flush first 3 seconds of audio (carrier/PBX junk)
    private const int EARLY_PROTECTION_MS = 6000; // Ignore inbound for 6s after call starts (PBX hold music)
    private int _inboundPacketCount;
    private bool _inboundFlushComplete;
    private DateTime _callStartedAt;
    private bool _adaHasStartedSpeaking; // Track if we've received any AI audio

    // Remote SDP payload type → codec mapping
    private readonly Dictionary<int, AudioCodecsEnum> _remotePtToCodec = new();

    public event Action<string>? OnLog;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnCallerAudioMonitor;

    public bool IsInCall => _isInCall;
    public bool IsOpusAvailable => _remotePtToCodec.Values.Contains(AudioCodecsEnum.OPUS);
    public bool IsG722Available => _remotePtToCodec.Values.Contains(AudioCodecsEnum.G722);
    public IReadOnlyDictionary<int, AudioCodecsEnum> NegotiatedCodecs => _remotePtToCodec;

    public LocalOpenAICallHandler(
        string apiKey, 
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer")
    {
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
    }

    public async Task HandleIncomingCallAsync(SIPTransport transport, SIPUserAgent ua, SIPRequest req, string caller)
    {
        if (_disposed) return;

        if (_isInCall)
        {
            Log("⚠️ Already in a call, rejecting");
            var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
            await transport.SendResponseAsync(busyResponse);
            return;
        }

        _isInCall = true;
        var callId = Guid.NewGuid().ToString("N")[..8];
        _callCts = new CancellationTokenSource();
        var cts = _callCts;
        _inboundPacketCount = 0;
        _inboundFlushComplete = false;
        _callStartedAt = DateTime.UtcNow;
        _remotePtToCodec.Clear();

        OnCallStarted?.Invoke(callId, caller);

        try
        {
            // Parse remote SDP for codec info
            ParseRemoteSdp(callId, req);

            // Setup media session with default audio source (for RTP playout)
            var audioSource = new AudioExtrasSource(
                new AudioEncoder(),
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None }
            );

            var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
            _currentMediaSession = new VoIPMediaSession(mediaEndPoints);
            _currentMediaSession.AcceptRtpFromAny = true;

            // Send ringing
            Log($"☎️ [{callId}] Sending 180 Ringing...");
            var uas = ua.AcceptCall(req);

            try
            {
                var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
                await transport.SendResponseAsync(ringing);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{callId}] Ringing send failed: {ex.Message}");
            }

            await Task.Delay(200, cts.Token);

            // Answer call
            Log($"📞 [{callId}] Answering call...");
            bool answered = await ua.Answer(uas, _currentMediaSession);
            if (!answered)
            {
                Log($"❌ [{callId}] Failed to answer");
                return;
            }

            await _currentMediaSession.Start();
            Log($"📗 [{callId}] Call answered and RTP started");

            // Create G711AiSipPlayout (24kHz PCM → 8kHz → G.711 encoding)
            _playout = new G711AiSipPlayout(_currentMediaSession, ua);
            _playout.OnLog += msg => Log(msg);
            _playout.OnQueueEmpty += () =>
            {
                if (_adaHasStartedSpeaking)
                {
                    _isBotSpeaking = false;
                    _botStoppedSpeakingAt = DateTime.UtcNow;
                    Log($"🔇 [{callId}] Ada finished speaking (echo guard {ECHO_GUARD_MS}ms)");
                }
            };
            _playout.Start();

            _adaHasStartedSpeaking = false;
            Log($"🎵 [{callId}] G711AiSipPlayout started (ITU-T G.711 encoding)");

            // Create OpenAI client
            _aiClient = new OpenAIRealtimeClient(_apiKey, _model, _voice);
            _aiClient.OnLog += msg => Log(msg);
            _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);
            _aiClient.OnAdaSpeaking += msg => { };
            _aiClient.OnPcm24Audio += pcmBytes =>
            {
                _isBotSpeaking = true;
                _adaHasStartedSpeaking = true;
                _playout?.BufferAiAudio(pcmBytes);
            };
            _aiClient.OnResponseStarted += () => { }; // No fade-in needed with raw PCM
            if (_aiClient is OpenAIRealtimeClient rtc)
                rtc.OnCallerAudioMonitor += data => OnCallerAudioMonitor?.Invoke(data);

            // Wire hangup
            ua.OnCallHungup += dialogue =>
            {
                Log($"📕 [{callId}] Caller hung up");
                try { cts.Cancel(); } catch { }
            };

            // Connect to OpenAI
            Log($"🔌 [{callId}] Connecting to OpenAI Realtime API...");
            await _aiClient.ConnectAsync(caller, cts.Token);
            Log($"🟢 [{callId}] OpenAI connected");

            // Wire inbound RTP
            WireRtpInput(callId, cts);

            Log($"✅ [{callId}] Call fully established");

            // Keep call alive
            while (!cts.IsCancellationRequested && _aiClient.IsConnected && !_disposed)
                await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ [{callId}] Error: {ex.Message}");
        }
        finally
        {
            await CleanupAsync(callId, ua);
        }
    }

    private void ParseRemoteSdp(string callId, SIPRequest req)
    {
        try
        {
            var sdpBody = req.Body;
            if (string.IsNullOrEmpty(sdpBody)) return;

            var sdp = SDP.ParseSDPDescription(sdpBody);
            var audioMedia = sdp.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (audioMedia == null) return;

            foreach (var f in audioMedia.MediaFormats)
            {
                var pt = f.Key;
                var name = f.Value.Name();
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (name.Equals("PCMA", StringComparison.OrdinalIgnoreCase))
                    _remotePtToCodec[pt] = AudioCodecsEnum.PCMA;
                else if (name.Equals("PCMU", StringComparison.OrdinalIgnoreCase))
                    _remotePtToCodec[pt] = AudioCodecsEnum.PCMU;
                else if (name.Equals("G722", StringComparison.OrdinalIgnoreCase))
                    _remotePtToCodec[pt] = AudioCodecsEnum.G722;
                else if (name.Equals("opus", StringComparison.OrdinalIgnoreCase))
                    _remotePtToCodec[pt] = AudioCodecsEnum.OPUS;
            }

            var codecSummary = audioMedia.MediaFormats
                .Select(f => $"{f.Value.Name()}(PT{f.Key})")
                .ToList();
            Log($"📥 [{callId}] Remote codecs: {string.Join(", ", codecSummary)}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] SDP parse error: {ex.Message}");
        }
    }

    private void WireRtpInput(string callId, CancellationTokenSource cts)
    {
        if (_currentMediaSession == null || _aiClient == null) return;

        _currentMediaSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
        {
            if (mt != SDPMediaTypesEnum.audio) return;
            if (cts.Token.IsCancellationRequested) return;

            _inboundPacketCount++;

            // Flush initial packets (carrier ringback/hold audio)
            if (!_inboundFlushComplete)
            {
                if (_inboundPacketCount <= FLUSH_PACKETS)
                {
                    if (_inboundPacketCount == 1)
                        Log($"🧹 [{callId}] Flushing inbound audio ({FLUSH_PACKETS} packets)...");
                    return;
                }
                _inboundFlushComplete = true;
                Log($"✅ [{callId}] Inbound flush complete");
            }

            // Early protection: ignore audio for first N seconds (carrier/PBX junk)
            var msSinceCallStart = (DateTime.UtcNow - _callStartedAt).TotalMilliseconds;
            if (msSinceCallStart < EARLY_PROTECTION_MS)
                return;

            // Skip while bot is speaking OR during echo guard period
            if (_isBotSpeaking) return;
            
            var msSinceBotStopped = (DateTime.UtcNow - _botStoppedSpeakingAt).TotalMilliseconds;
            if (msSinceBotStopped < ECHO_GUARD_MS) return;

            var payload = rtp.Payload;
            if (payload == null || payload.Length == 0) return;

            // Decode based on payload type
            var pt = rtp.Header.PayloadType;
            if (!_remotePtToCodec.TryGetValue(pt, out var codec))
            {
                // Default to PCMU for unknown PT
                codec = pt == 8 ? AudioCodecsEnum.PCMA : AudioCodecsEnum.PCMU;
            }

            // Decode to PCM16
            short[] pcm16;
            switch (codec)
            {
                case AudioCodecsEnum.PCMU:
                    pcm16 = payload.Select(b => SIPSorcery.Media.MuLawDecoder.MuLawToLinearSample(b)).ToArray();
                    break;
                case AudioCodecsEnum.PCMA:
                    pcm16 = payload.Select(b => SIPSorcery.Media.ALawDecoder.ALawToLinearSample(b)).ToArray();
                    break;
                default:
                    // For now, only support G.711
                    return;
            }

            // Convert to bytes
            var pcmBytes = new byte[pcm16.Length * 2];
            Buffer.BlockCopy(pcm16, 0, pcmBytes, 0, pcmBytes.Length);

            // Send to OpenAI (8kHz PCM16)
            try
            {
                await _aiClient.SendAudioAsync(pcmBytes, 8000);
            }
            catch { }
        };
    }

    private async Task CleanupAsync(string callId, SIPUserAgent ua)
    {
        Log($"📴 [{callId}] Cleanup...");
        _isInCall = false;

        try { ua.Hangup(); } catch { }

        if (_aiClient != null)
        {
            try { await _aiClient.DisconnectAsync(); } catch { }
            try { _aiClient.Dispose(); } catch { }
            _aiClient = null;
        }

        if (_playout != null)
        {
            try { _playout.Stop(); } catch { }
            try { _playout.Dispose(); } catch { }
            _playout = null;
        }

        if (_currentMediaSession != null)
        {
            try { _currentMediaSession.Close("call ended"); } catch { }
            _currentMediaSession = null;
        }

        try { _callCts?.Dispose(); } catch { }
        _callCts = null;

        OnCallEnded?.Invoke(callId);
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _callCts?.Cancel(); } catch { }
        try { _playout?.Dispose(); } catch { }
        try { _aiClient?.Dispose(); } catch { }
        try { _currentMediaSession?.Close("disposed"); } catch { }

        GC.SuppressFinalize(this);
    }
}
