using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// G711CallHandler v7.4 - Watchdog fix + True A-law end-to-end passthrough @ 8kHz with non-blocking logging.
/// 
/// ═══════════════════════════════════════════════════════════════════════════════
/// AUDIO ARCHITECTURE (v6.0 - Zero-Processing Passthrough)
/// ═══════════════════════════════════════════════════════════════════════════════
/// 
/// INGRESS (SIP → OpenAI):
///   SIP PCMA (A-law 8kHz) → [NO DECODE] → [NO DSP] → OpenAI g711_alaw (8kHz)
///   - Raw A-law bytes forwarded directly (or μ-law→A-law transcoded if SIP uses PCMU)
///   - NO decode/encode cycle = NO quantization noise = NO crispy audio
///   - Barge-in detection: decode only for RMS check, then discard
/// 
/// EGRESS (OpenAI → SIP):
///   OpenAI g711_alaw (8kHz) → SipSorceryAudioBridge → SIP PCMA (A-law 8kHz)
///   - Direct A-law passthrough via OnG711Audio event
///   - NAudio pipeline handles jitter buffering (400ms)
///   - High-precision 20ms timing via Windows multimedia timer (1ms granularity)
/// 
/// CRITICAL CHANGES FROM v5.1:
///   - Removed IngressDsp.ApplyForStt() which caused decode→DSP→re-encode cycle
///   - Eliminated quantization noise from double G.711 encoding
///   - Soft gate now sends silence bytes instead of processed audio
/// 
/// ═══════════════════════════════════════════════════════════════════════════════
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
    
    // v6.3: Non-blocking async logger to prevent UI thread from causing audio jitter
    private readonly AsyncLogger _asyncLog = new();

    private VoIPMediaSession? _currentMediaSession;
    private ALawRtpPlayout? _alawPlayout;  // v6.1: Direct A-law RTP playout (better audio than NAudio pipeline)
    private AudioCodecsEnum _negotiatedCodec = AudioCodecsEnum.PCMA;
    private int _negotiatedPayloadType = 0;
    private OpenAIRealtimeG711Client? _aiClient;
    private G711CallFeatures? _features;
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
    private volatile bool _watchdogPending;  // v7.4: Track if watchdog should fire on queue empty
    // Stats
    private int _framesForwarded;
    private int _framesSent;

    // ===========================================
    // CONSTANTS
    // ===========================================
    private const int ECHO_GUARD_MS = 120; // 120ms reduced echo guard for A-law passthrough
    private const int FLUSH_PACKETS = 20;
    private const int EARLY_PROTECTION_MS = 500;
    private const int HANGUP_GRACE_MS = 5000;
    private const int FRAME_SIZE_ALAW = 160; // 20ms @ 8kHz = 160 bytes A-law

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
        string voice = "coral",  // v6.0: Changed from shimmer to coral for more energetic voice
        string? instructions = null,
        string? greeting = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _instructions = instructions ?? GetDefaultInstructions();
        _greeting = greeting ?? "Hi there! Welcome to Voice Taxibot!";  // v6.0: More energetic greeting
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
            // Setup media session (A-law preferred)
            var audioEncoder = new UnifiedAudioEncoder();
            AudioCodecs.ResetAllCodecs();

            // Prefer PCMA (A-law) for v5.1 pure passthrough mode
            var preferredFormats = audioEncoder.SupportedFormats
                .OrderByDescending(f => f.Codec == AudioCodecsEnum.PCMA)
                .ThenByDescending(f => f.Codec == AudioCodecsEnum.PCMU)
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

            // v5.1: Force A-law for pure passthrough consistency
            var sipCodec = OpenAIRealtimeG711Client.G711Codec.ALaw;
            
            Log($"🎵 [{callId}] FORCED A-law (v5.1 pure passthrough)");
            _aiClient = new OpenAIRealtimeG711Client(_apiKey, _model, _voice, sipCodec);

            // Feature add-ons: tool handling + booking state + dispatch
            _features?.Dispose();
            _features = new G711CallFeatures(_aiClient, callId, callerPhone: caller);
            _features.OnLog += Log;
            _features.OnCallEnded += () =>
            {
                Log($"📞 [{callId}] Features requested call end");
                try { cts.Cancel(); } catch { }
            };

            // Route OpenAI tool calls to feature handler
            _aiClient.OnToolCall += (toolName, toolCallId, args) =>
                _features.HandleToolCallAsync(toolName, toolCallId, args);
            
            // v6.1: Use ALawRtpPlayout for direct A-law passthrough (better audio quality)
            _alawPlayout = new ALawRtpPlayout(_currentMediaSession);
            _alawPlayout.OnQueueEmpty += () =>
            {
                // v7.4: Track when playout genuinely empties after speech
                if (_adaHasStartedSpeaking && _isBotSpeaking)
                {
                    _isBotSpeaking = false;
                    _botStoppedSpeakingAt = DateTime.UtcNow;
                    Log($"🔇 [{callId}] Playout queue empty - echo guard started");
                    
                    // Notify AI client + start watchdog from playout completion
                    if (_features != null) _features.OnPlayoutComplete();
                    else _aiClient?.NotifyPlayoutComplete();
                }
                // v7.4: Also trigger watchdog if response completed but queue was still draining
                else if (_watchdogPending)
                {
                    _watchdogPending = false;
                    Log($"🔇 [{callId}] Playout drained post-response - starting watchdog");
                    if (_features != null) _features.OnPlayoutComplete();
                    else _aiClient?.NotifyPlayoutComplete();
                }
            };
            _alawPlayout.Start();
            Log($"🎵 [{callId}] ALawRtpPlayout started (direct A-law passthrough)");

            // Wire AI client events (using v5.1 pure A-law output path)
            WireAiClientEvents(callId, cts);

            // v5.1: Direct A-law passthrough (no PCM conversion, no DSP)
            var codecName = sipCodec == OpenAIRealtimeG711Client.G711Codec.ALaw ? "A-law" : "μ-law";
            Log($"🔌 [{callId}] Connecting to OpenAI Realtime ({codecName} passthrough 8kHz)...");
            await _aiClient.ConnectAsync(caller, cts.Token);
            Log($"🟢 [{callId}] OpenAI connected");

            // CRITICAL: Configure session with voice, instructions, tools BEFORE sending greeting
            Log($"⚙️ [{callId}] Configuring session...");
            await _aiClient.WaitForSessionAndConfigureAsync();
            Log($"✅ [{callId}] Session configured");

            // Wire inbound RTP
            WireRtpInput(callId, cts);

            Log($"✅ [{callId}] Call established: SIP ({_negotiatedCodec}) ↔ OpenAI ({codecName} passthrough)");
            
            // Start keepalive loop
            _features?.StartKeepalive();

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
    // AI CLIENT EVENT WIRING
    // ===========================================
    private void WireAiClientEvents(string callId, CancellationTokenSource cts)
    {
        if (_aiClient == null) return;

        _aiClient.OnLog += msg => Log(msg);
        _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);

        // v6.1: Native G.711 audio PATH - push to ALawRtpPlayout
        _aiClient.OnG711Audio += g711Bytes =>
        {
            _isBotSpeaking = true;
            _adaHasStartedSpeaking = true;
            _alawPlayout?.BufferALaw(g711Bytes);
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
            
            // v7.4: If queue is already empty, start watchdog immediately
            // Otherwise, set pending flag for OnQueueEmpty to pick up
            if (_alawPlayout == null || _alawPlayout.QueuedFrames == 0)
            {
                Log($"🔔 [{callId}] Queue already empty - starting watchdog now");
                if (_features != null) _features.OnPlayoutComplete();
                else _aiClient?.NotifyPlayoutComplete();
            }
            else
            {
                _watchdogPending = true;
            }
        };

        // Barge-in handler - only act if Ada is actually speaking or queue has audio
        Action bargeInHandler = () =>
        {
            // If Ada isn't speaking and queue is empty, this is just normal speech starting a new turn
            if (!_isBotSpeaking && (_alawPlayout == null || _alawPlayout.QueuedFrames == 0))
                return;
            
            _alawPlayout?.Clear();  // Immediately stop Ada speaking
            _isBotSpeaking = false;
            _adaHasStartedSpeaking = true;
            _features?.CancelWatchdog();
            Log($"✂️ [{callId}] Barge-in (VAD): cleared playout queue");
        };

        try
        {
            var evt = _aiClient.GetType().GetEvent("OnBargeIn");
            if (evt?.EventHandlerType != null)
            {
                var del = Delegate.CreateDelegate(evt.EventHandlerType, bargeInHandler.Target!, bargeInHandler.Method);
                evt.AddEventHandler(_aiClient, del);
                Log($"📌 [{callId}] Event handlers wired: OnG711Audio→ALawRtpPlayout, OnResponseStarted, OnResponseCompleted, OnBargeIn");
            }
            else
            {
                Log($"📌 [{callId}] Event handlers wired: OnG711Audio→ALawRtpPlayout, OnResponseStarted, OnResponseCompleted");
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

            // Determine if we should apply soft gate (bot speaking or echo guard)
            bool applySoftGate = false;
            
            if (_isBotSpeaking)
            {
                applySoftGate = true;
            }
            else if (_adaHasStartedSpeaking && _botStoppedSpeakingAt != DateTime.MinValue)
            {
                var msSinceBotStopped = (DateTime.UtcNow - _botStoppedSpeakingAt).TotalMilliseconds;
                if (msSinceBotStopped < ECHO_GUARD_MS)
                {
                    applySoftGate = true;
                }
            }

            var ai = _aiClient;
            if (ai == null || !ai.IsConnected) return;

            try
            {
                // Get raw payload
                var payload = rtp.Payload;
                if (payload == null || payload.Length == 0) return;

                // v5.2: TRUE A-law passthrough - no decode/DSP/re-encode (eliminates quantization noise)
                if (_negotiatedCodec == AudioCodecsEnum.PCMA || _negotiatedCodec == AudioCodecsEnum.PCMU)
                {
                    // Transcode μ-law to A-law if needed (OpenAI expects A-law)
                    byte[] g711ToSend;
                    if (_negotiatedCodec == AudioCodecsEnum.PCMU)
                    {
                        // Direct μ-law → A-law transcode (no PCM intermediate)
                        g711ToSend = AudioCodecs.MuLawToALaw(payload);
                    }
                    else
                    {
                        // Pure A-law passthrough - zero processing
                        g711ToSend = payload;
                    }
                    
                    // Soft gate: during bot speaking, send silence instead of echo
                    if (applySoftGate)
                    {
                        // Check RMS of decoded audio for barge-in detection
                        var pcmCheck = AudioCodecs.ALawDecode(g711ToSend);
                        double sumSq = 0;
                        for (int i = 0; i < pcmCheck.Length; i++)
                            sumSq += (double)pcmCheck[i] * pcmCheck[i];
                        float rms = (float)Math.Sqrt(sumSq / pcmCheck.Length);
                        
                        bool isBargeIn = rms >= 1500; // Barge-in threshold
                        
                        if (isBargeIn)
                        {
                            _features?.CancelWatchdog();
                            Log($"✂️ [{callId}] Barge-in detected (RMS={rms:F0})");
                        }
                        else
                        {
                            // Not a barge-in - send silence to prevent echo
                            Array.Fill(g711ToSend, (byte)0xD5); // A-law silence
                        }
                    }
                    
                    await ai.SendALawAsync(g711ToSend);

                    _framesForwarded++;
                    if (_framesForwarded % 50 == 0)
                        Log($"🎙️ [{callId}] Ingress: {_framesForwarded} frames (passthrough 8kHz){(applySoftGate ? " [gated]" : "")}");
                }

                // Audio monitor (decode for monitoring if needed)
                if (OnCallerAudioMonitor != null)
                {
                    short[] monitorPcm;
                    if (_negotiatedCodec == AudioCodecsEnum.PCMA)
                        monitorPcm = AudioCodecs.ALawDecode(payload);
                    else
                        monitorPcm = AudioCodecs.MuLawDecode(payload);
                    OnCallerAudioMonitor?.Invoke(AudioCodecs.ShortsToBytes(monitorPcm));
                }
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

        try { _features?.Dispose(); } catch { }
        _features = null;
        
        // Reset ingress DSP state for new call
        IngressDsp.Reset();
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

        if (_features != null)
        {
            try { _features.Dispose(); } catch { }
            _features = null;
        }

        if (_alawPlayout != null)
        {
            try { _alawPlayout.Stop(); } catch { }
            try { _alawPlayout.Dispose(); } catch { }
            _alawPlayout = null;
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
        // v6.3: Use async logger to prevent blocking audio threads
        _asyncLog.Log($"{DateTime.Now:HH:mm:ss.fff} {msg}", s => OnLog?.Invoke(s));
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
        try { _alawPlayout?.Dispose(); } catch { }
        try { _aiClient?.Dispose(); } catch { }
        try { _features?.Dispose(); } catch { }
        try { _currentMediaSession?.Close("disposed"); } catch { }
        try { _asyncLog.Dispose(); } catch { }  // v6.3: Dispose async logger

        GC.SuppressFinalize(this);
    }
}
