using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using TaxiSipBridge.Audio;

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
    private DirectRtpPlayout? _playout;
    private IngressAudioProcessor? _ingress;
    private OpenAIRealtimeClient? _aiClient;
    private CancellationTokenSource? _callCts;
    private Action<SIPDialogue>? _currentHungupHandler;
    private SIPUserAgent? _currentUa;
    private int _hangupFired;

    // ===========================================
    // AUDIO PROCESSING STATE
    // ===========================================
    private DateTime _callStartedAt;
    private bool _adaHasStartedSpeaking;
    private bool _needsFadeIn;

    // Remote SDP payload type → codec mapping
    private readonly Dictionary<int, AudioCodecsEnum> _remotePtToCodec = new();

    // Simli avatar integration
    private Func<byte[], Task>? _simliSendAudio;

    // ===========================================
    // CONSTANTS
    // ===========================================
    private const int ECHO_GUARD_MS = 200;
    private const int HANGUP_GRACE_MS = 5000;
    private const int JITTER_FRAMES = 6; // ~120ms jitter buffer

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
        string voice = "shimmer",
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
        _callStartedAt = DateTime.UtcNow;
        _remotePtToCodec.Clear();
        _hangupFired = 0;
        _adaHasStartedSpeaking = false;
        _needsFadeIn = false;
        _isBotSpeaking = false;
        _botStoppedSpeakingAt = DateTime.MinValue;

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

            // Create DirectRtpPlayout
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
            Log($"🎵 [{callId}] DirectRtpPlayout started");

            // Create OpenAI client
            _aiClient = new OpenAIRealtimeClient(_apiKey, _model, _voice, null, _dispatchWebhookUrl);
            WireAiClientEvents(callId, cts);

            // Connect to OpenAI
            Log($"🔌 [{callId}] Connecting to OpenAI Realtime API...");
            await _aiClient.ConnectAsync(caller, cts.Token);
            Log($"🟢 [{callId}] OpenAI connected (language: {_aiClient.DetectedLanguage})");

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

    // ===========================================
    // AI CLIENT EVENT WIRING
    // ===========================================
    private void WireAiClientEvents(string callId, CancellationTokenSource cts)
    {
        if (_aiClient == null) return;

        _aiClient.OnLog += msg => Log(msg);
        _aiClient.OnTranscript += t => OnTranscript?.Invoke(t);
        _aiClient.OnBookingUpdated += state => OnBookingUpdated?.Invoke(state);
        _aiClient.OnCallerAudioMonitor += data => OnCallerAudioMonitor?.Invoke(data);

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

            _playout?.BufferAiAudio(processedBytes);

            // Send to Simli avatar (fire and forget)
            if (_simliSendAudio != null)
            {
                try { await _simliSendAudio(processedBytes); }
                catch { }
            }
        };

        _aiClient.OnResponseStarted += () =>
        {
            _needsFadeIn = true;
            // Log already comes through via OnLog event
        };

        _aiClient.OnResponseCompleted += () =>
        {
            // Log already comes through via OnLog event
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
        if (_currentMediaSession == null || _aiClient == null) return;

        // Create ingress processor with 16kHz output (best for PSTN ASR)
        _ingress = new IngressAudioProcessor(IngressAudioProcessor.TargetRate.Hz16000, JITTER_FRAMES);
        _ingress.SetCodecMap(_remotePtToCodec);
        _ingress.OnLog += msg => Log(msg);

        // Wire output to OpenAI (16kHz PCM16)
        _ingress.OnPcmFrameReady += async pcmBytes =>
        {
            if (cts.Token.IsCancellationRequested) return;

            // Skip while bot is speaking OR during echo guard
            if (_isBotSpeaking) return;

            var msSinceBotStopped = (DateTime.UtcNow - _botStoppedSpeakingAt).TotalMilliseconds;
            if (msSinceBotStopped < ECHO_GUARD_MS) return;

            try
            {
                await _aiClient.SendAudioAsync(pcmBytes, 16000);
            }
            catch { }
        };

        Log($"🎧 [{callId}] IngressAudioProcessor ready (16kHz, jitter={JITTER_FRAMES * 20}ms)");

        // Wire RTP to ingress processor
        _currentMediaSession.OnRtpPacketReceived += (ep, mt, rtp) =>
        {
            if (cts.Token.IsCancellationRequested) return;
            _ingress.PushRtpAudio(mt, rtp);
        };
    }

    // ===========================================
    // CLEANUP
    // ===========================================
    private async Task CleanupAsync(string callId, SIPUserAgent ua)
    {
        Log($"📴 [{callId}] Cleanup...");

        _isInCall = false;

        // Unsubscribe hangup handler
        if (_currentHungupHandler != null && _currentUa != null)
        {
            _currentUa.OnCallHungup -= _currentHungupHandler;
            _currentHungupHandler = null;
        }

        try { ua.Hangup(); } catch { }

        if (_aiClient != null)
        {
            try { await _aiClient.DisconnectAsync(); } catch { }
            try { _aiClient.Dispose(); } catch { }
            _aiClient = null;
        }

        if (_ingress != null)
        {
            try { _ingress.Dispose(); } catch { }
            _ingress = null;
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
        try { _ingress?.Dispose(); } catch { }
        try { _playout?.Dispose(); } catch { }
        try { _aiClient?.Dispose(); } catch { }
        try { _currentMediaSession?.Close("disposed"); } catch { }

        GC.SuppressFinalize(this);
    }
}
