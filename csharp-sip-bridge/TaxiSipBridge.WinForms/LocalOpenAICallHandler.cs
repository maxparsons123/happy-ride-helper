using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

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
    private readonly string? _dispatchWebhookUrl;

    private volatile bool _isInCall;
    private volatile bool _disposed;
    private volatile bool _isBotSpeaking;
    private DateTime _botStoppedSpeakingAt = DateTime.MinValue;
    private const int ECHO_GUARD_MS = 200; // Suppress inbound audio for 200ms after Ada stops (reduced for faster response)

    private VoIPMediaSession? _currentMediaSession;
    private DirectRtpPlayout? _playout;
    private OpenAIRealtimeClient? _aiClient;
    private CancellationTokenSource? _callCts;

    private const int FLUSH_PACKETS = 20;       // Flush first ~400ms of audio (carrier junk)
    private const int EARLY_PROTECTION_MS = 500;  // Ignore inbound for 500ms after call starts
    private const int HANGUP_GRACE_MS = 4000;     // Wait for final audio to play before hangup (4 seconds for goodbye)
    private int _inboundPacketCount;
    private bool _inboundFlushComplete;
    private DateTime _callStartedAt;
    private bool _adaHasStartedSpeaking; // Track if we've received any AI audio
    private bool _needsFadeIn; // Apply fade-in to first audio delta of each response

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

    /// <summary>
    /// Create a local OpenAI call handler with optional dispatch webhook.
    /// </summary>
    /// <param name="apiKey">OpenAI API key</param>
    /// <param name="model">Model name</param>
    /// <param name="voice">Voice name</param>
    /// <param name="dispatchWebhookUrl">Optional webhook URL for taxi dispatch (e.g., Supabase edge function)</param>
    public LocalOpenAICallHandler(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
        _dispatchWebhookUrl = dispatchWebhookUrl;
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

            // Setup media session with UnifiedAudioEncoder (Opus + G.722 + G.711)
            var audioEncoder = new UnifiedAudioEncoder();
            AudioCodecs.ResetAllCodecs(); // Reset codec state for new call

            // Build codec list with Opus prioritized if remote offers it
            List<AudioFormat> preferredFormats;
            if (IsOpusAvailable)
            {
                var opusPt = _remotePtToCodec.FirstOrDefault(kv => kv.Value == AudioCodecsEnum.OPUS).Key;
                var opusFormat = new AudioFormat(AudioCodecsEnum.OPUS, opusPt, 48000, 2, "opus");
                preferredFormats = new List<AudioFormat> { opusFormat };
                preferredFormats.AddRange(audioEncoder.SupportedFormats.Where(f => f.Codec != AudioCodecsEnum.OPUS));
                Log($"🎧 [{callId}] Opus prioritized (PT{opusPt})");
            }
            else
            {
                preferredFormats = audioEncoder.SupportedFormats.ToList();
            }

            // Create audio source with prioritized codecs
            var audioSource = new AudioExtrasSource(
                audioEncoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None }
            );

            // Restrict to our preferred format order
            audioSource.RestrictFormats(fmt => preferredFormats.Any(p => p.Codec == fmt.Codec));

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

            // Create DirectRtpPlayout (bypasses SendAudio, sends raw RTP packets)
            // VoIPMediaSession inherits from RTPSession, so this works directly
            _playout = new DirectRtpPlayout(_currentMediaSession);
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
            Log($"🎵 [{callId}] DirectRtpPlayout started (raw RTP, A-law encoded)");

            // Create OpenAI client with dispatch webhook support
            _aiClient = new OpenAIRealtimeClient(_apiKey, _model, _voice, null, _dispatchWebhookUrl);
            _aiClient.OnLog += msg => Log(msg);
            _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);
            _aiClient.OnAdaSpeaking += msg => { };
            _aiClient.OnPcm24Audio += pcmBytes =>
            {
                _isBotSpeaking = true;
                _adaHasStartedSpeaking = true;

                // Apply fade-in to first audio delta to prevent speaker pop
                byte[] processedBytes = pcmBytes;
                if (_needsFadeIn)
                {
                    processedBytes = ApplyFadeIn(pcmBytes);
                    _needsFadeIn = false;
                    Log($"🔊 [{callId}] Applied anti-glitch fade-in");
                }

                _playout?.BufferAiAudio(processedBytes);
            };
            _aiClient.OnResponseStarted += () =>
            {
                // Mark for fade-in on new response (don't clear buffer to avoid cutting previous audio)
                _needsFadeIn = true;
            };
            _aiClient.OnCallEnded += async () =>
            {
                Log($"📞 [{callId}] AI requested call end, waiting {HANGUP_GRACE_MS}ms for final audio...");
                // Wait for final audio to finish playing before hangup
                await Task.Delay(HANGUP_GRACE_MS);
                Log($"📴 [{callId}] Grace period complete, ending call");
                try { cts.Cancel(); } catch { }
            };
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
            if (string.IsNullOrEmpty(sdpBody))
            {
                Log($"⚠️ [{callId}] No SDP body in INVITE");
                return;
            }

            var sdp = SDP.ParseSDPDescription(sdpBody);
            var audioMedia = sdp.Media.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (audioMedia == null)
            {
                Log($"⚠️ [{callId}] No audio media in SDP");
                return;
            }

            // Parse all codecs from remote offer
            var codecList = new List<string>();
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

                codecList.Add($"{name}(PT{pt})");
            }

            // Log all available codecs prominently
            Log($"════════════════════════════════════════════════════");
            Log($"🎧 [{callId}] AVAILABLE CODECS: {string.Join(", ", codecList)}");
            Log($"   Opus: {(IsOpusAvailable ? "✓ YES" : "✗ NO")}");
            Log($"   G.722: {(IsG722Available ? "✓ YES" : "✗ NO")}");
            Log($"   PCMA/PCMU: {(_remotePtToCodec.Values.Any(c => c == AudioCodecsEnum.PCMA || c == AudioCodecsEnum.PCMU) ? "✓ YES" : "✗ NO")}");
            Log($"════════════════════════════════════════════════════");
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
            int sampleRate = 8000; // Default for G.711

            switch (codec)
            {
                case AudioCodecsEnum.PCMU:
                    pcm16 = payload.Select(b => SIPSorcery.Media.MuLawDecoder.MuLawToLinearSample(b)).ToArray();
                    break;
                case AudioCodecsEnum.PCMA:
                    pcm16 = payload.Select(b => SIPSorcery.Media.ALawDecoder.ALawToLinearSample(b)).ToArray();
                    break;
                case AudioCodecsEnum.OPUS:
                    try
                    {
                        var pcm48 = AudioCodecs.OpusDecode(payload);

                        // Downmix stereo to mono if needed
                        short[] mono;
                        if (AudioCodecs.OPUS_DECODE_CHANNELS == 2 && pcm48.Length % 2 == 0)
                        {
                            mono = new short[pcm48.Length / 2];
                            for (int j = 0; j < mono.Length; j++)
                                mono[j] = (short)((pcm48[j * 2] + pcm48[j * 2 + 1]) / 2);
                        }
                        else
                        {
                            mono = pcm48;
                        }

                        // Decimate 48kHz → 24kHz (2:1) for OpenAI
                        pcm16 = new short[mono.Length / 2];
                        for (int j = 0; j < pcm16.Length; j++)
                            pcm16[j] = mono[j * 2];

                        sampleRate = 24000; // Already at 24kHz after decimation
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [{callId}] Opus decode error: {ex.Message}");
                        return;
                    }
                    break;
                case AudioCodecsEnum.G722:
                    try
                    {
                        pcm16 = AudioCodecs.G722Decode(payload);
                        sampleRate = 16000; // G.722 is 16kHz
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [{callId}] G.722 decode error: {ex.Message}");
                        return;
                    }
                    break;
                default:
                    // Unsupported codec
                    return;
            }

            // Convert to bytes
            var pcmBytes = new byte[pcm16.Length * 2];
            Buffer.BlockCopy(pcm16, 0, pcmBytes, 0, pcmBytes.Length);

            // Send to OpenAI with correct sample rate
            try
            {
                await _aiClient.SendAudioAsync(pcmBytes, sampleRate);
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

    /// <summary>
    /// Apply a fade-in ramp to the first PCM24 audio delta to prevent speaker pop.
    /// Ramps from 0% to 100% over ~2ms (48 samples at 24kHz).
    /// </summary>
    private byte[] ApplyFadeIn(byte[] pcmBytes)
    {
        // Convert bytes to shorts (PCM16 24kHz)
        short[] samples = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, samples, 0, pcmBytes.Length);

        // Fade over 48 samples (~2ms at 24kHz)
        int fadeLength = Math.Min(samples.Length, 48);
        for (int i = 0; i < fadeLength; i++)
        {
            float multiplier = (float)i / fadeLength;
            samples[i] = (short)(samples[i] * multiplier);
        }

        // Convert back to bytes
        byte[] fadedBytes = new byte[pcmBytes.Length];
        Buffer.BlockCopy(samples, 0, fadedBytes, 0, pcmBytes.Length);
        return fadedBytes;
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
