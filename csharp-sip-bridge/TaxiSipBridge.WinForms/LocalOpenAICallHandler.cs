using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Call handler that routes audio directly to OpenAI Realtime API.
/// Uses DirectRtpPlayout for timer-driven RTP delivery with proper G.711 encoding.
/// Thread-safe with single-execution guards for hangup handling.
/// </summary>
public class LocalOpenAICallHandler : ISipCallHandler, IDisposable
{
    // ===========================================
    // CONFIGURATION
    // ===========================================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _dispatchWebhookUrl;

    // ===========================================
    // CALL STATE
    // ===========================================
    private volatile bool _isInCall;
    private volatile bool _disposed;
    private volatile bool _isBotSpeaking;
    private DateTime _botStoppedSpeakingAt = DateTime.MinValue;
    
    private VoIPMediaSession? _currentMediaSession;
    private MultiCodecRtpPlayout? _playout;
    private AudioCodecsEnum _negotiatedCodec = AudioCodecsEnum.PCMA;
    private int _negotiatedPayloadType = 8;
    private OpenAIRealtimeClient? _aiClient;
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
    private bool _needsFadeIn;

    // Diagnostics: confirm ingress frames are actually forwarded to OpenAI
    private int _ingressFramesForwarded;
    private int _ingressBytesForwarded;

    // Remote SDP payload type → codec mapping
    private readonly Dictionary<int, AudioCodecsEnum> _remotePtToCodec = new();

    // High-quality ingress audio processor (jitter buffer, DSP, SpeexDSP resampling)
    private IngressAudioProcessor? _ingressProcessor;

    // Simli avatar integration
    private Func<byte[], Task>? _simliSendAudio;
    private int _simliAudioSendCount;

    // ===========================================
    // CONSTANTS
    // ===========================================
    private const int ECHO_GUARD_MS = 200;
    private const int FLUSH_PACKETS = 20;
    private const int EARLY_PROTECTION_MS = 500;
    private const int HANGUP_GRACE_MS = 5000;

    // ===========================================
    // EVENTS
    // ===========================================
    public event Action<string>? OnLog;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnCallerAudioMonitor;
    public event Action<BookingState>? OnBookingUpdated;

    // ===========================================
    // PROPERTIES
    // ===========================================
    public bool IsInCall => _isInCall;
    public bool IsOpusAvailable => _remotePtToCodec.Values.Contains(AudioCodecsEnum.OPUS);
    public bool IsG722Available => _remotePtToCodec.Values.Contains(AudioCodecsEnum.G722);
    public IReadOnlyDictionary<int, AudioCodecsEnum> NegotiatedCodecs => _remotePtToCodec;

    // ===========================================
    // CONSTRUCTOR
    // ===========================================
    public LocalOpenAICallHandler(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "sage",
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _dispatchWebhookUrl = dispatchWebhookUrl;
    }

    /// <summary>
    /// Configure a sender function for Simli avatar audio (PCM24 24kHz).
    /// </summary>
    public void SetSimliSender(Func<byte[], Task> sendAudio)
    {
        _simliSendAudio = sendAudio;
        Log("🎭 Simli audio sender configured");
    }

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
        _inboundPacketCount = 0;
        _inboundFlushComplete = false;
        _callStartedAt = DateTime.UtcNow;
        _remotePtToCodec.Clear();
        _hangupFired = 0;
        _adaHasStartedSpeaking = false;
        _needsFadeIn = false;
        _isBotSpeaking = false;
        _botStoppedSpeakingAt = DateTime.MinValue;
        _simliAudioSendCount = 0;
        _ingressFramesForwarded = 0;
        _ingressBytesForwarded = 0;

        // Clean up stale handlers from previous call
        if (_currentHungupHandler != null && _currentUa != null)
        {
            _currentUa.OnCallHungup -= _currentHungupHandler;
        }
        _currentHungupHandler = null;
        _currentUa = ua;

        OnCallStarted?.Invoke(callId, caller);

        try
        {
            // Parse remote SDP for codec info
            ParseRemoteSdp(callId, req);

            // Setup media session
            var audioEncoder = new UnifiedAudioEncoder();
            AudioCodecs.ResetAllCodecs();

            // Build codec list with Opus prioritized if available
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

            var audioSource = new AudioExtrasSource(
                audioEncoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None }
            );
            audioSource.RestrictFormats(fmt => preferredFormats.Any(p => p.Codec == fmt.Codec));

            var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
            _currentMediaSession = new VoIPMediaSession(mediaEndPoints);
            _currentMediaSession.AcceptRtpFromAny = true;

            // Track negotiated codec for proper encoding
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

            // Create MultiCodecRtpPlayout (supports Opus, G.722, PCMA, PCMU)
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

            // Create ingress audio processor (jitter buffer + DSP + SpeexDSP 24kHz resampling)
            _ingressProcessor?.Dispose();
            _ingressProcessor = new IngressAudioProcessor(IngressAudioProcessor.TargetRate.Hz24000, jitterFrames: 6);
            _ingressProcessor.SetCodecMap(_remotePtToCodec);
            _ingressProcessor.OnLog += msg => Log(msg);
            Log($"🎧 [{callId}] IngressAudioProcessor ready (24kHz, jitter=6)");

            // Wire ingress output immediately so we can verify frames are emitted.
            // (RTP wiring happens later via WireRtpInput, so this won't receive frames until then.)
            _ingressProcessor.OnPcmFrameReady += async pcmBytes =>
            {
                if (cts.Token.IsCancellationRequested) return;

                // Echo guard: skip audio briefly after bot stops speaking
                if (_adaHasStartedSpeaking && !_isBotSpeaking && _botStoppedSpeakingAt != DateTime.MinValue)
                {
                    var msSinceBotStopped = (DateTime.UtcNow - _botStoppedSpeakingAt).TotalMilliseconds;
                    if (msSinceBotStopped < ECHO_GUARD_MS) return;
                }

                // Audio monitoring (already at 24kHz from processor)
                OnCallerAudioMonitor?.Invoke(pcmBytes);

                var ai = _aiClient;
                if (ai == null || !ai.IsConnected) return;

                try
                {
                    _ingressFramesForwarded++;
                    _ingressBytesForwarded += pcmBytes.Length;
                    if (_ingressFramesForwarded == 1 || _ingressFramesForwarded % 50 == 0)
                    {
                        Log($"🎙️ [{callId}] Ingress→OpenAI: frames={_ingressFramesForwarded}, bytes={_ingressBytesForwarded}");
                    }

                    await ai.SendAudioAsync(pcmBytes, 24000);
                }
                catch { }
            };

            // Create OpenAI client
            _aiClient = new OpenAIRealtimeClient(_apiKey, _model, _voice, null, _dispatchWebhookUrl);
            WireAiClientEvents(callId, cts);

            // Connect to OpenAI
            Log($"🔌 [{callId}] Connecting to OpenAI Realtime API...");
            await _aiClient.ConnectAsync(caller, cts.Token);
            Log($"🟢 [{callId}] OpenAI connected");

            // Wire inbound RTP to processor
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

    // ===========================================
    // AI CLIENT EVENT WIRING
    // ===========================================
    private void WireAiClientEvents(string callId, CancellationTokenSource cts)
    {
        if (_aiClient == null) return;

        _aiClient.OnLog += msg => Log(msg);
        _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);
        _aiClient.OnBookingUpdated += state => OnBookingUpdated?.Invoke(state);

        _aiClient.OnAdaSpeaking += _ => { };

        _aiClient.OnPcm24Audio += async pcmBytes =>
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

            _playout?.BufferAudio(processedBytes);

            // Send to Simli avatar
            if (_simliSendAudio != null)
            {
                try 
                { 
                    await _simliSendAudio(processedBytes);
                    _simliAudioSendCount++;
                    
                    // Log first 5 sends, then every 50th to track ongoing flow
                    if (_simliAudioSendCount <= 5 || _simliAudioSendCount % 50 == 0)
                    {
                        Log($"🎭 [{callId}] Simli audio #{_simliAudioSendCount} ({processedBytes.Length} bytes)");
                    }
                }
                catch (Exception ex)
                {
                    Log($"🎭 [{callId}] Simli send error #{_simliAudioSendCount}: {ex.Message}");
                }
            }
            else if (_simliAudioSendCount == 0)
            {
                Log($"⚠️ [{callId}] _simliSendAudio is null - avatar not wired!");
                _simliAudioSendCount = 1; // Only log once
            }
        };

        _aiClient.OnResponseStarted += () =>
        {
            _needsFadeIn = true;
        };

        _aiClient.OnResponseCompleted += () =>
        {
        };

        // AI-triggered hangup (with single-execution guard)
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

            Log($"📞 [{callId}] AI requested call end, waiting {HANGUP_GRACE_MS}ms for final audio...");
            await Task.Delay(HANGUP_GRACE_MS);
            Log($"📴 [{callId}] Grace period complete, ending call");
            try { cts.Cancel(); } catch { }
        };
        _aiClient.OnCallEnded += endCallHandler;

        // Caller-triggered hangup (with single-execution guard)
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
    // SDP PARSING
    // ===========================================
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

    // ===========================================
    // RTP INPUT HANDLING (via IngressAudioProcessor)
    // ===========================================
    private void WireRtpInput(string callId, CancellationTokenSource cts)
    {
        if (_currentMediaSession == null || _ingressProcessor == null) return;

        _currentMediaSession.OnRtpPacketReceived += (ep, mt, rtp) =>
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

            // Early protection: ignore audio for first N ms
            var msSinceCallStart = (DateTime.UtcNow - _callStartedAt).TotalMilliseconds;
            if (msSinceCallStart < EARLY_PROTECTION_MS)
                return;

            // Push to IngressAudioProcessor - it handles:
            // - Jitter buffering & reordering
            // - Codec decoding (G.711, Opus, G.722)
            // - ASR-tuned DSP (DC blocker, pre-emphasis, noise gate, AGC)
            // - High-quality SpeexDSP upsampling to 24kHz
            // Output is delivered via OnPcmFrameReady event
            _ingressProcessor.PushRtpAudio(mt, rtp);
        };
    }

    // ===========================================
    // CLEANUP
    // ===========================================
    private async Task CleanupAsync(string callId, SIPUserAgent ua)
    {
        Log($"📴 [{callId}] Cleanup starting...");

        // Reset call state FIRST to allow new calls
        _isInCall = false;
        _isBotSpeaking = false;
        _adaHasStartedSpeaking = false;
        _inboundFlushComplete = false;
        _inboundPacketCount = 0;

        // Unsubscribe hangup handler
        if (_currentHungupHandler != null && _currentUa != null)
        {
            try { _currentUa.OnCallHungup -= _currentHungupHandler; } catch { }
            _currentHungupHandler = null;
        }

        // Hangup SIP call
        try { ua.Hangup(); } catch { }

        // Dispose AI client
        if (_aiClient != null)
        {
            try { await _aiClient.DisconnectAsync(); } catch { }
            try { _aiClient.Dispose(); } catch { }
            _aiClient = null;
        }

        // Stop playout
        if (_playout != null)
        {
            try { _playout.Stop(); } catch { }
            try { _playout.Dispose(); } catch { }
            _playout = null;
        }

        // Dispose ingress processor
        if (_ingressProcessor != null)
        {
            try { _ingressProcessor.Dispose(); } catch { }
            _ingressProcessor = null;
        }

        // Close media session
        if (_currentMediaSession != null)
        {
            try { _currentMediaSession.Close("call ended"); } catch { }
            _currentMediaSession = null;
        }

        // Dispose cancellation token
        try { _callCts?.Dispose(); } catch { }
        _callCts = null;

        Log($"📴 [{callId}] Cleanup complete");
        OnCallEnded?.Invoke(callId);
    }

    // ===========================================
    // AUDIO UTILITIES
    // ===========================================
    
    /// <summary>
    /// Apply a fade-in ramp to the first PCM24 audio delta to prevent speaker pop.
    /// Ramps from 0% to 100% over ~2ms (48 samples at 24kHz).
    /// </summary>
    private static byte[] ApplyFadeIn(byte[] pcmBytes)
    {
        short[] samples = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, samples, 0, pcmBytes.Length);

        int fadeLength = Math.Min(samples.Length, 48);
        for (int i = 0; i < fadeLength; i++)
        {
            float multiplier = (float)i / fadeLength;
            samples[i] = (short)(samples[i] * multiplier);
        }

        byte[] fadedBytes = new byte[pcmBytes.Length];
        Buffer.BlockCopy(samples, 0, fadedBytes, 0, pcmBytes.Length);
        return fadedBytes;
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
