using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Call handler that routes audio directly to OpenAI Realtime API using G.711 μ-law @ 8kHz.
/// 
/// Key Benefits:
/// - No resampling overhead (8kHz throughout)
/// - Direct passthrough: SIP (μ-law) → OpenAI (μ-law) → SIP (μ-law)
/// - Lower latency and CPU usage
/// - Simplified audio pipeline
/// 
/// Uses MultiCodecRtpPlayout for RTP output (encodes μ-law frames for transmission).
/// </summary>
public class G711CallHandler : ISipCallHandler, IDisposable
{
    // ===========================================
    // CONFIGURATION
    // ===========================================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _instructions;
    private readonly string? _greeting;

    // ===========================================
    // CALL STATE
    // ===========================================
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private volatile bool _isBotSpeaking;
    private DateTime _botStoppedSpeakingAt = DateTime.MinValue;

    private VoIPMediaSession? _currentMediaSession;
    private MultiCodecRtpPlayout? _playout;
    private AudioCodecsEnum _negotiatedCodec = AudioCodecsEnum.PCMU; // Default to μ-law for G711 mode
    private int _negotiatedPayloadType = 0;
    private OpenAIRealtimeG711Client? _aiClient;
    private CancellationTokenSource? _callCts;
    private Action<SIPDialogue>? _currentHungupHandler;
    private SIPUserAgent? _currentUa;
    private int _hangupFired;

    // ===========================================
    // AUDIO PROCESSING STATE
    // ===========================================
    private int _inboundPacketCount;
    private bool _inboundFlushComplete;
    private DateTime _callStartedAt;
    private bool _adaHasStartedSpeaking;

    // Stats
    private int _framesForwarded;
    private int _framesSent;

    // ===========================================
    // CONSTANTS
    // ===========================================
    private const int ECHO_GUARD_MS = 200;
    private const int FLUSH_PACKETS = 20;
    private const int EARLY_PROTECTION_MS = 500;
    private const int HANGUP_GRACE_MS = 5000;
    private const int FRAME_SIZE_ULAW = 160; // 20ms @ 8kHz = 160 bytes μ-law

    // ===========================================
    // EVENTS
    // ===========================================
    public event Action<string>? OnLog;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnCallerAudioMonitor;

    // ===========================================
    // PROPERTIES
    // ===========================================
    public bool IsInCall => _isInCall;

    // ===========================================
    // CONSTRUCTOR
    // ===========================================
    public G711CallHandler(
        string apiKey,
        string model = "gpt-4o-realtime-preview-2025-06-03",
        string voice = "shimmer",
        string? instructions = null,
        string? greeting = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _instructions = instructions ?? GetDefaultInstructions();
        _greeting = greeting ?? "Hello! How can I help you today?";
    }

    private static string GetDefaultInstructions() => @"
You are Ada, a friendly and efficient AI assistant.
Be concise, warm, and professional.
";

    // ===========================================
    // MAIN CALL HANDLER
    // ===========================================
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

        // Reset state for new call
        ResetCallState();
        _currentUa = ua;

        OnCallStarted?.Invoke(callId, caller);

        try
        {
            // Setup media session (G.711 μ-law preferred)
            var audioEncoder = new UnifiedAudioEncoder();
            AudioCodecs.ResetAllCodecs();

            // Prefer PCMU (μ-law) for G711 mode
            var preferredFormats = audioEncoder.SupportedFormats
                .OrderByDescending(f => f.Codec == AudioCodecsEnum.PCMU)
                .ThenByDescending(f => f.Codec == AudioCodecsEnum.PCMA)
                .ToList();

            var audioSource = new AudioExtrasSource(
                audioEncoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None }
            );
            audioSource.RestrictFormats(fmt => preferredFormats.Any(p => p.Codec == fmt.Codec));

            var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
            _currentMediaSession = new VoIPMediaSession(mediaEndPoints);
            _currentMediaSession.AcceptRtpFromAny = true;

            // Track negotiated codec
            _currentMediaSession.OnAudioFormatsNegotiated += formats =>
            {
                var fmt = formats.FirstOrDefault();
                _negotiatedCodec = fmt.Codec;
                _negotiatedPayloadType = fmt.FormatID;
                Log($"🎵 [{callId}] Negotiated codec: {fmt.Codec} (PT{fmt.FormatID})");
            };

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

            // Create MultiCodecRtpPlayout for output
            _playout = new MultiCodecRtpPlayout(_currentMediaSession);
            _playout.SetCodec(_negotiatedCodec, _negotiatedPayloadType);
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
            Log($"🎵 [{callId}] MultiCodecRtpPlayout started ({_negotiatedCodec})");

            // Create OpenAI G711 client
            _aiClient = new OpenAIRealtimeG711Client(_apiKey, _model, _voice, _instructions, _greeting);
            WireAiClientEvents(callId, cts);

            // Connect to OpenAI
            Log($"🔌 [{callId}] Connecting to OpenAI Realtime (G.711 μ-law @ 8kHz)...");
            await _aiClient.ConnectAsync(caller, cts.Token);
            Log($"🟢 [{callId}] OpenAI connected");

            // Wire inbound RTP
            WireRtpInput(callId, cts);

            Log($"✅ [{callId}] Call fully established (G711 mode)");

            // Playout loop: pull μ-law frames from AI client and buffer to RTP
            _ = Task.Run(async () => await PlayoutLoopAsync(callId, cts.Token));

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

    // ===========================================
    // PLAYOUT LOOP (AI → RTP)
    // ===========================================
    private async Task PlayoutLoopAsync(string callId, CancellationToken ct)
    {
        Log($"▶️ [{callId}] Playout loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ai = _aiClient;
                if (ai == null || !ai.IsConnected) break;

                // Get μ-law frame from AI client
                var ulawFrame = ai.GetNextMuLawFrame();
                if (ulawFrame != null && ulawFrame.Length == FRAME_SIZE_ULAW)
                {
                    _isBotSpeaking = true;
                    _adaHasStartedSpeaking = true;

                    // Convert μ-law → PCM16 @ 8kHz → PCM16 @ 24kHz for playout buffer
                    // (MultiCodecRtpPlayout expects 24kHz input)
                    var pcm8k = AudioCodecs.MuLawDecode(ulawFrame);
                    var pcm24k = AudioCodecs.ResampleWithSpeex(pcm8k, 8000, 24000);
                    var pcm24kBytes = AudioCodecs.ShortsToBytes(pcm24k);

                    _playout?.BufferAudio(pcm24kBytes);
                    _framesSent++;

                    if (_framesSent % 50 == 0)
                        Log($"📤 [{callId}] Playout: {_framesSent} frames sent");
                }
                else
                {
                    // No audio available, short sleep
                    await Task.Delay(5, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"⚠️ [{callId}] Playout error: {ex.Message}");
                await Task.Delay(10, ct);
            }
        }

        Log($"⏹️ [{callId}] Playout loop ended ({_framesSent} frames sent)");
    }

    // ===========================================
    // AI CLIENT EVENT WIRING
    // ===========================================
    private void WireAiClientEvents(string callId, CancellationTokenSource cts)
    {
        if (_aiClient == null) return;

        _aiClient.OnLog += msg => Log(msg);
        _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);

        _aiClient.OnResponseStarted += () =>
        {
            Log($"🤖 [{callId}] AI response started");
        };

        _aiClient.OnAdaSpeaking += _ => { };

        // AI-triggered hangup
        Action? endCallHandler = null;
        endCallHandler = async () =>
        {
            if (Interlocked.Exchange(ref _hangupFired, 1) != 0) return;

            _aiClient!.OnCallEnded -= endCallHandler;
            if (_currentHungupHandler != null && _currentUa != null)
            {
                _currentUa.OnCallHungup -= _currentHungupHandler;
                _currentHungupHandler = null;
            }

            Log($"📞 [{callId}] AI requested call end, waiting {HANGUP_GRACE_MS}ms...");
            await Task.Delay(HANGUP_GRACE_MS);
            Log($"📴 [{callId}] Grace period complete, ending call");
            try { cts.Cancel(); } catch { }
        };
        _aiClient.OnCallEnded += endCallHandler;

        // Caller-triggered hangup
        _currentHungupHandler = _ =>
        {
            if (Interlocked.Exchange(ref _hangupFired, 1) != 0) return;

            if (_currentHungupHandler != null && _currentUa != null)
            {
                _currentUa.OnCallHungup -= _currentHungupHandler;
                _currentHungupHandler = null;
            }
            if (endCallHandler != null) _aiClient!.OnCallEnded -= endCallHandler;

            Log($"📕 [{callId}] Caller hung up");
            try { cts.Cancel(); } catch { }
        };
        _currentUa!.OnCallHungup += _currentHungupHandler;
    }

    // ===========================================
    // RTP INPUT HANDLING (SIP → AI)
    // ===========================================
    private void WireRtpInput(string callId, CancellationTokenSource cts)
    {
        if (_currentMediaSession == null) return;

        _currentMediaSession.OnRtpPacketReceived += async (ep, mt, rtp) =>
        {
            if (mt != SDPMediaTypesEnum.audio) return;
            if (cts.Token.IsCancellationRequested) return;

            _inboundPacketCount++;

            // Flush initial packets
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

            // Early protection
            var msSinceCallStart = (DateTime.UtcNow - _callStartedAt).TotalMilliseconds;
            if (msSinceCallStart < EARLY_PROTECTION_MS)
                return;

            // Don't send audio while bot is speaking
            if (_isBotSpeaking)
                return;

            // Echo guard
            if (_adaHasStartedSpeaking && _botStoppedSpeakingAt != DateTime.MinValue)
            {
                var msSinceBotStopped = (DateTime.UtcNow - _botStoppedSpeakingAt).TotalMilliseconds;
                if (msSinceBotStopped < ECHO_GUARD_MS) return;
            }

            var ai = _aiClient;
            if (ai == null || !ai.IsConnected) return;

            try
            {
                // Get raw payload (should be μ-law for G711 mode)
                var payload = rtp.Payload;
                if (payload == null || payload.Length == 0) return;

                // For PCMU (μ-law), send directly
                // For PCMA (A-law), convert to μ-law first
                byte[] ulawPayload;
                if (_negotiatedCodec == AudioCodecsEnum.PCMU)
                {
                    ulawPayload = payload;
                }
                else if (_negotiatedCodec == AudioCodecsEnum.PCMA)
                {
                    // A-law → PCM → μ-law
                    var pcm = AudioCodecs.ALawDecode(payload);
                    ulawPayload = AudioCodecs.MuLawEncode(pcm);
                }
                else
                {
                    // For other codecs, decode to PCM and encode to μ-law
                    short[] pcm;
                    if (_negotiatedCodec == AudioCodecsEnum.OPUS)
                    {
                        var stereo = AudioCodecs.OpusDecode(payload);
                        // Downmix stereo to mono
                        pcm = new short[stereo.Length / 2];
                        for (int i = 0; i < pcm.Length; i++)
                            pcm[i] = (short)((stereo[i * 2] + stereo[i * 2 + 1]) / 2);
                        // Resample 48kHz → 8kHz
                        pcm = AudioCodecs.Resample(pcm, 48000, 8000);
                    }
                    else if (_negotiatedCodec == AudioCodecsEnum.G722)
                    {
                        pcm = AudioCodecs.G722Decode(payload);
                        pcm = AudioCodecs.Resample(pcm, 16000, 8000);
                    }
                    else
                    {
                        return; // Unknown codec
                    }
                    ulawPayload = AudioCodecs.MuLawEncode(pcm);
                }

                // Send μ-law directly to OpenAI
                await ai.SendMuLawAsync(ulawPayload);

                _framesForwarded++;
                if (_framesForwarded % 50 == 0)
                    Log($"🎙️ [{callId}] Ingress: {_framesForwarded} frames → OpenAI");

                // Audio monitor (convert to PCM for speaker output)
                var monitorPcm = AudioCodecs.MuLawDecode(ulawPayload);
                var monitor24k = AudioCodecs.ResampleWithSpeex(monitorPcm, 8000, 24000);
                OnCallerAudioMonitor?.Invoke(AudioCodecs.ShortsToBytes(monitor24k));
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{callId}] RTP input error: {ex.Message}");
            }
        };
    }

    // ===========================================
    // HELPERS
    // ===========================================
    private void ResetCallState()
    {
        _inboundPacketCount = 0;
        _inboundFlushComplete = false;
        _callStartedAt = DateTime.UtcNow;
        _hangupFired = 0;
        _adaHasStartedSpeaking = false;
        _isBotSpeaking = false;
        _botStoppedSpeakingAt = DateTime.MinValue;
        _framesForwarded = 0;
        _framesSent = 0;
    }

    // ===========================================
    // CLEANUP
    // ===========================================
    private async Task CleanupAsync(string callId, SIPUserAgent ua)
    {
        Log($"📴 [{callId}] Cleanup starting...");

        _isInCall = false;
        _isBotSpeaking = false;
        _adaHasStartedSpeaking = false;

        if (_currentHungupHandler != null && _currentUa != null)
        {
            try { _currentUa.OnCallHungup -= _currentHungupHandler; } catch { }
            _currentHungupHandler = null;
        }

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

        Log($"📴 [{callId}] Cleanup complete (rx={_framesForwarded}, tx={_framesSent})");
        OnCallEnded?.Invoke(callId);
    }

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    // ===========================================
    // DISPOSAL
    // ===========================================
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_currentHungupHandler != null && _currentUa != null)
        {
            try { _currentUa.OnCallHungup -= _currentHungupHandler; } catch { }
            _currentHungupHandler = null;
        }

        try { _callCts?.Cancel(); } catch { }
        try { _playout?.Dispose(); } catch { }
        try { _aiClient?.Dispose(); } catch { }
        try { _currentMediaSession?.Close("disposed"); } catch { }

        GC.SuppressFinalize(this);
    }
}
