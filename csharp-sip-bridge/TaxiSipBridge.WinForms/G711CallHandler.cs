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
/// - For A-law carriers: Direct transcode A-law ↔ μ-law at 8kHz (no PCM intermediate)
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
    private DirectRtpPlayout? _playout;   // Use DirectRtpPlayout for proper DSP (AGC, DC removal, soft limiting)
    private AudioCodecsEnum _negotiatedCodec = AudioCodecsEnum.PCMA; // Default to A-law for G711 mode
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
    private const int ECHO_GUARD_MS = 120; // Reduced from 200ms for faster turn-taking
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

            // Create OpenAI client - uses PCM16 @ 24kHz for DSP benefits
            var aiCodec = _negotiatedCodec == AudioCodecsEnum.PCMA 
                ? OpenAIRealtimeG711Client.G711Codec.ALaw 
                : OpenAIRealtimeG711Client.G711Codec.MuLaw;
            _aiClient = new OpenAIRealtimeG711Client(_apiKey, _model, _voice, aiCodec);
            
            // Create DirectRtpPlayout for proper DSP (AGC, DC removal, soft limiting, FIR downsampling)
            _playout = new DirectRtpPlayout(_currentMediaSession);
            _playout.OnLog += msg => Log(msg);
            _playout.OnQueueEmpty += () =>
            {
                if (_adaHasStartedSpeaking && _isBotSpeaking)
                {
                    _isBotSpeaking = false;
                    _botStoppedSpeakingAt = DateTime.UtcNow;
                    Log($"🔇 [{callId}] DirectRtpPlayout queue empty - echo guard started");
                }
            };
            _playout.Start();
            Log($"🎵 [{callId}] DirectRtpPlayout started (DSP: AGC + DC removal + soft limiting)");

            // Wire AI client events (including PCM24k audio routing)
            WireAiClientEvents(callId, cts);

            // Connect to OpenAI
            Log($"🔌 [{callId}] Connecting to OpenAI Realtime (PCM16 24kHz → DSP → {aiCodec})...");
            await _aiClient.ConnectAsync(caller, cts.Token);
            Log($"🟢 [{callId}] OpenAI connected");

            // Wire inbound RTP
            WireRtpInput(callId, cts);

            Log($"✅ [{callId}] Call fully established (DSP mode, DirectRtpPlayout)");

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

    // NOTE: PlayoutLoopAsync removed - SipAudioPlayout handles timer-driven RTP transmission

    // ===========================================
    // AI CLIENT EVENT WIRING
    // ===========================================
    private void WireAiClientEvents(string callId, CancellationTokenSource cts)
    {
        if (_aiClient == null) return;

        _aiClient.OnLog += msg => Log(msg);
        _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);

        // Route PCM24k audio to DirectRtpPlayout for DSP processing
        _aiClient.OnPcm24Audio += pcm24kBytes =>
        {
            _isBotSpeaking = true;
            _adaHasStartedSpeaking = true;
            
            // DirectRtpPlayout applies FIR downsampling, DC removal, AGC, soft limiting
            _playout?.BufferAiAudio(pcm24kBytes);
        };

        _aiClient.OnResponseStarted += () =>
        {
            _isBotSpeaking = true;
            Log($"🤖 [{callId}] AI response started (blocking user audio)");
        };

        _aiClient.OnResponseCompleted += () =>
        {
            _isBotSpeaking = false;
            _botStoppedSpeakingAt = DateTime.UtcNow;
            Log($"✅ [{callId}] AI response done - UNBLOCKING user audio (echo guard {ECHO_GUARD_MS}ms)");
        };

        // Barge-in handler
        Action bargeInHandler = () =>
        {
            _playout?.Clear();
            _isBotSpeaking = false;
            _adaHasStartedSpeaking = true;
            Log($"✂️ [{callId}] Barge-in (VAD): cleared DirectRtpPlayout queue");
        };

        try
        {
            var evt = _aiClient.GetType().GetEvent("OnBargeIn");
            if (evt?.EventHandlerType != null)
            {
                var del = Delegate.CreateDelegate(evt.EventHandlerType, bargeInHandler.Target!, bargeInHandler.Method);
                evt.AddEventHandler(_aiClient, del);
                Log($"📌 [{callId}] Event handlers wired: OnPcm24Audio, OnResponseStarted, OnResponseCompleted, OnBargeIn");
            }
            else
            {
                Log($"📌 [{callId}] Event handlers wired: OnPcm24Audio, OnResponseStarted, OnResponseCompleted");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{callId}] Failed to wire OnBargeIn: {ex.Message}");
        }

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
            {
                // Log periodically to diagnose blocking
                if (_inboundPacketCount % 100 == 0)
                    Log($"🚫 [{callId}] Ingress blocked: bot speaking (packet #{_inboundPacketCount})");
                return;
            }

            // Echo guard
            if (_adaHasStartedSpeaking && _botStoppedSpeakingAt != DateTime.MinValue)
            {
                var msSinceBotStopped = (DateTime.UtcNow - _botStoppedSpeakingAt).TotalMilliseconds;
                if (msSinceBotStopped < ECHO_GUARD_MS)
                {
                    if (_inboundPacketCount % 50 == 0)
                        Log($"🛡️ [{callId}] Ingress blocked: echo guard ({msSinceBotStopped:F0}ms < {ECHO_GUARD_MS}ms)");
                    return;
                }
            }

            var ai = _aiClient;
            if (ai == null || !ai.IsConnected) return;

            try
            {
                // Get raw payload
                var payload = rtp.Payload;
                if (payload == null || payload.Length == 0) return;

                // Decode G.711 to PCM16 @ 8kHz, then resample to 24kHz for OpenAI
                short[] pcm8k;
                if (_negotiatedCodec == AudioCodecsEnum.PCMA)
                {
                    pcm8k = AudioCodecs.ALawDecode(payload);
                }
                else if (_negotiatedCodec == AudioCodecsEnum.PCMU)
                {
                    pcm8k = AudioCodecs.MuLawDecode(payload);
                }
                else if (_negotiatedCodec == AudioCodecsEnum.OPUS)
                {
                    var stereo = AudioCodecs.OpusDecode(payload);
                    pcm8k = new short[stereo.Length / 2];
                    for (int i = 0; i < pcm8k.Length; i++)
                        pcm8k[i] = (short)((stereo[i * 2] + stereo[i * 2 + 1]) / 2);
                    pcm8k = AudioCodecs.Resample(pcm8k, 48000, 8000);
                }
                else if (_negotiatedCodec == AudioCodecsEnum.G722)
                {
                    pcm8k = AudioCodecs.G722Decode(payload);
                    pcm8k = AudioCodecs.Resample(pcm8k, 16000, 8000);
                }
                else
                {
                    return; // Unknown codec
                }

                // Resample 8kHz → 24kHz for OpenAI (uses SpeexDSP for high quality)
                var pcm24k = AudioCodecs.ResampleWithSpeex(pcm8k, 8000, 24000);
                var pcm24kBytes = AudioCodecs.ShortsToBytes(pcm24k);

                // Send PCM16 @ 24kHz to OpenAI
                await ai.SendAudioAsync(pcm24kBytes, 24000);

                _framesForwarded++;
                if (_framesForwarded % 50 == 0)
                    Log($"🎙️ [{callId}] Ingress: {_framesForwarded} frames (8kHz {_negotiatedCodec} → 24kHz PCM) → OpenAI");

                // Audio monitor
                OnCallerAudioMonitor?.Invoke(pcm24kBytes);
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
