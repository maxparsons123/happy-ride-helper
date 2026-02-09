using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AdaMain.Ai;
using AdaMain.Audio;
using AdaMain.Config;
using AdaMain.Core;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace AdaMain.Sip;

/// <summary>
/// Full-featured SIP registration and call management server.
/// Ported from TaxiSipBridge.SipLoginManager with DNS resolution,
/// Auth ID support, STUN/NAT, SIP tracing, and proper transport handling.
/// </summary>
public sealed class SipServer : IAsyncDisposable
{
    private readonly ILogger<SipServer> _logger;
    private readonly SipSettings _settings;
    private readonly AudioSettings _audioSettings;
    private readonly SessionManager _sessionManager;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;
    private ICallSession? _currentSession;
    private VoIPMediaSession? _activeRtpSession;
    private IPAddress? _localIp;
    private IPAddress? _publicIp;

    private readonly object _callLock = new();
    private volatile bool _isRunning;
    private volatile bool _disposed;

    // ── Non-blocking async log queue (prevents I/O from blocking RTP thread) ──
    private readonly ConcurrentQueue<string> _logQueue = new();
    private int _logDraining;

    // ── Audio quality diagnostics (thread-safe via Interlocked) ──
    private int _dqFrameCount;
    private long _dqRmsSum;  // stored as fixed-point (x1000) for Interlocked
    private int _dqPeakRms;  // stored as int (x1000) for Interlocked
    private int _dqMinRms = int.MaxValue;  // stored as int (x1000)
    private int _dqSilentFrames;
    private int _dqClippedFrames;
    private const int DQ_LOG_INTERVAL_FRAMES = 100; // ~2 seconds at 20ms/frame

    // ── Operator RTP timestamp (cumulative for multi-frame sends) ──
    private uint _operatorTimestamp;

    public bool IsRegistered { get; private set; }
    public bool IsConnected => _transport != null && IsRegistered;

    public event Action<string>? OnLog;
    public event Action<string>? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnServerResolved;
    /// <summary>Fires with raw A-law RTP payload for local audio monitoring.</summary>
    public event Action<byte[]>? OnCallerAudioMonitor;

    public SipServer(
        ILogger<SipServer> logger,
        SipSettings settings,
        AudioSettings audioSettings,
        SessionManager sessionManager)
    {
        _logger = logger;
        _settings = settings;
        _audioSettings = audioSettings;
        _sessionManager = sessionManager;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;

        Log("🚀 SipServer starting...");
        Log($"➡ SIP Server: {_settings.Server}:{_settings.Port} ({_settings.Transport})");
        Log($"➡ User: {_settings.Username}");

        // Resolve server hostname → IP and notify UI so it shows the resolved address
        var resolvedIp = ResolveDns(_settings.Server);
        if (resolvedIp != _settings.Server)
            OnServerResolved?.Invoke(resolvedIp);

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

        // STUN discovery
        if (_settings.EnableStun)
        {
            _publicIp = await DiscoverPublicIpAsync();
            if (_publicIp != null)
                Log($"➡ Public IP (STUN): {_publicIp}");
            else
                Log("⚠️ STUN discovery failed; using local IP only");
        }

        InitializeSipTransport();
        InitializeRegistration();
        InitializeUserAgent();

        _regAgent!.Start();
        _isRunning = true;
        Log("🟢 Waiting for SIP registration...");
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        Log("🛑 SipServer stopping...");

        if (_regAgent != null)
        {
            _regAgent.RegistrationSuccessful -= OnRegistrationSuccess;
            _regAgent.RegistrationFailed -= OnRegistrationFailure;
            try { _regAgent.Stop(); } catch { }
            _regAgent = null;
        }

        if (_userAgent != null)
        {
            _userAgent.OnIncomingCall -= OnIncomingCallAsync;
            _userAgent = null;
        }

        await HangupAsync();

        try { _transport?.Shutdown(); } catch { }
        _transport = null;

        IsRegistered = false;
        _isRunning = false;
        Log("🛑 SipServer stopped");
    }

    /// <summary>Hang up any active call.</summary>
    public async Task HangupAsync()
    {
        ICallSession? session;
        lock (_callLock)
        {
            session = _currentSession;
            _currentSession = null;
        }

        if (session != null)
        {
            await session.EndAsync("user_hangup");
            OnCallEnded?.Invoke("user_hangup");
        }
    }

    #region Initialization

    private void InitializeSipTransport()
    {
        _transport = new SIPTransport();

        // SIP trace logging for debugging
        _transport.SIPRequestOutTraceEvent += (ep, dst, req) =>
            Log($"📤 SIP OUT → {dst}: {req.Method} {req.URI}");
        _transport.SIPResponseInTraceEvent += (ep, src, resp) =>
            Log($"📥 SIP IN ← {src}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _transport.SIPResponseOutTraceEvent += (ep, dst, resp) =>
            Log($"📤 SIP RESP → {dst}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _transport.SIPRequestInTraceEvent += (ep, src, req) =>
            Log($"📥 SIP REQ ← {src}: {req.Method} {req.URI}");

        switch (_settings.Transport.ToUpperInvariant())
        {
            case "UDP":
                _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(_localIp!, 0)));
                Log("📡 Using UDP transport");
                break;
            case "TCP":
                _transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(_localIp!, 0)));
                Log("📡 Using TCP transport");
                break;
            case "TLS":
                var tlsChannel = new SIPTLSChannel(new IPEndPoint(_localIp!, 0));
                _transport.AddSIPChannel(tlsChannel);
                Log("🔒 Using TLS transport");
                break;
            default:
                _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(_localIp!, 0)));
                Log("📡 Using UDP transport (default)");
                break;
        }
    }

    private void InitializeRegistration()
    {
        var authUser = string.IsNullOrWhiteSpace(_settings.AuthId) ? _settings.Username : _settings.AuthId;

        // Resolve DNS first - use resolved IP in registrar host (matches SipLoginManager behavior)
        var resolvedHost = ResolveDns(_settings.Server);
        var registrarHostWithPort = _settings.Port == 5060
            ? resolvedHost
            : $"{resolvedHost}:{_settings.Port}";

        if (authUser != _settings.Username)
        {
            // Advanced registration: Auth ID differs from extension (e.g. 3CX)
            if (!IPAddress.TryParse(resolvedHost, out var registrarIp))
            {
                try
                {
                    registrarIp = Dns.GetHostAddresses(resolvedHost)
                        .First(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch
                {
                    Log("⚠️ Could not resolve registrar to IPv4; falling back to simple registration.");
                    _regAgent = new SIPRegistrationUserAgent(
                        _transport,
                        _settings.Username,
                        _settings.Password,
                        registrarHostWithPort,
                        120);
                    _regAgent.RegistrationSuccessful += OnRegistrationSuccess;
                    _regAgent.RegistrationFailed += OnRegistrationFailure;
                    return;
                }
            }

            var protocol = SipConfigHelper.ParseTransport(_settings.Transport);
            var outboundProxy = new SIPEndPoint(protocol, new IPEndPoint(registrarIp, _settings.Port));

            // Use HOSTNAME in AOR and registrar (not IP) - critical for digest auth realm matching
            var sipAccountAor = new SIPURI(_settings.Username, registrarHostWithPort, null, SIPSchemesEnum.sip, protocol);
            var contactUri = new SIPURI(sipAccountAor.Scheme, IPAddress.Any, 0) { User = _settings.Username };

            Log($"🔐 Auth Debug: AOR={sipAccountAor}, AuthUser={authUser}, PassLen={_settings.Password?.Length ?? 0}");

            _regAgent = new SIPRegistrationUserAgent(
                sipTransport: _transport,
                outboundProxy: outboundProxy,
                sipAccountAOR: sipAccountAor,
                authUsername: authUser,
                password: _settings.Password,
                realm: null,  // Let SIPSorcery pick up realm from WWW-Authenticate
                registrarHost: registrarHostWithPort,  // HOSTNAME, not IP
                contactURI: contactUri,
                expiry: 120,
                customHeaders: null);

            Log($"➡ Using separate Auth ID: {authUser}, Registrar: {registrarHostWithPort} (routed via {resolvedHost})");
        }
        else
        {
            // Standard registration: extension and auth username are the same
            _regAgent = new SIPRegistrationUserAgent(
                _transport,
                _settings.Username,
                _settings.Password,
                registrarHostWithPort,
                120);
        }

        _regAgent.RegistrationSuccessful += OnRegistrationSuccess;
        _regAgent.RegistrationFailed += OnRegistrationFailure;
    }

    private void InitializeUserAgent()
    {
        _userAgent = new SIPUserAgent(_transport, null);
        _userAgent.OnIncomingCall += OnIncomingCallAsync;
    }

    #endregion

    #region Registration Events

    private void OnRegistrationSuccess(SIPURI uri, SIPResponse resp)
    {
        Log($"✅ SIP Registered as {_settings.Username}@{_settings.Server}");
        IsRegistered = true;
        OnRegistered?.Invoke(uri.ToString());
    }

    private void OnRegistrationFailure(SIPURI uri, SIPResponse? resp, string err)
    {
        Log($"❌ SIP Registration failed: {err}");
        IsRegistered = false;
        OnRegistrationFailed?.Invoke(err);
    }

    #endregion

    #region Call Handling

    /// <summary>
    /// SIPSorcery requires a sync delegate, so we use a fire-and-forget wrapper
    /// with top-level exception handling to prevent unobserved task crashes.
    /// </summary>
    private void OnIncomingCallAsync(SIPUserAgent ua, SIPRequest req)
    {
        _ = HandleIncomingCallSafeAsync(ua, req);
    }

    private async Task HandleIncomingCallSafeAsync(SIPUserAgent ua, SIPRequest req)
    {
        var caller = CallerIdExtractor.Extract(req);
        Log($"📞 Incoming call from {caller}");

        lock (_callLock)
        {
            if (_currentSession != null)
            {
                Log("📞 Rejecting call - busy");
                var busy = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
                _ = _transport!.SendResponseAsync(busy);
                return;
            }
        }

        try
        {
            // Send 180 Ringing
            var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
            await _transport!.SendResponseAsync(ringing);
            Log("🔔 Sent 180 Ringing");

            // Auto-answer path
            if (_settings.AutoAnswer)
            {
                await AnswerCallAsync(ua, req, caller);
            }
            else
            {
                Log("⏳ Waiting for manual answer (auto-answer disabled)...");
                OnCallStarted?.Invoke(caller);
            }
        }
        catch (Exception ex)
        {
            Log($"🔥 Error handling incoming call: {ex.Message}");
            _logger.LogError(ex, "Error handling call from {Caller}", caller);
        }
    }

    private async Task AnswerCallAsync(SIPUserAgent ua, SIPRequest req, string caller)
    {
        // Match proven G711CallHandler v7.5 pattern: AudioSourcesEnum.None + codec preference ordering
        var audioEncoder = new AudioEncoder();

        var audioSource = new AudioExtrasSource(
            audioEncoder,
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

        // Prefer PCMA (A-law) but also allow PCMU (μ-law) for broader carrier compatibility
        // G711CallHandler orders: PCMA first, then PCMU, then everything else
        audioSource.RestrictFormats(fmt => 
            fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);

        var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
        var rtpSession = new VoIPMediaSession(mediaEndPoints);
        rtpSession.AcceptRtpFromAny = true;

        // Track negotiated codec for transcoding decisions (matches G711CallHandler)
        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats =>
        {
            var fmt = formats.FirstOrDefault();
            negotiatedCodec = fmt.Codec;
            Log($"🎵 Negotiated codec: {fmt.Codec} (PT{fmt.FormatID})");
        };

        var serverUa = ua.AcceptCall(req);

        // v7.6: 200ms pre-answer delay (matches G711CallHandler) — lets SIP signaling settle
        await Task.Delay(200);

        var result = await ua.Answer(serverUa, rtpSession);

        if (!result)
        {
            Log("❌ Failed to answer call");
            return;
        }

        // CRITICAL: Start RTP session (missing in previous version → degraded audio)
        await rtpSession.Start();
        Log("✅ Call answered and RTP started");

        ua.OnCallHungup += async (dialog) =>
        {
            Log("📴 Remote party hung up");
            await CleanupCallAsync("remote_hangup");
        };

        // Create AI session
        var session = await _sessionManager.CreateSessionAsync(caller);
        session.OnEnded += async (s, reason) => await CleanupCallAsync(reason);

        lock (_callLock)
        {
            _currentSession = session;
            _activeRtpSession = rtpSession;
        }

        // Create ALawRtpPlayout FIRST (v7.4 rebuffer engine) — needed for RTP wiring below
        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log(msg);
        
        // Wire playout queue depth query for drain-aware goodbye shutdown
        if (session.AiClient is OpenAiG711Client g711Wire)
            g711Wire.GetQueuedFrames = () => playout.QueuedFrames;
        else
            Log("⚠️ AI client is not G.711 client — drain detection disabled");

        // Wire up audio: SIP → AI (with flush + early protection + soft gate — matches G711CallHandler v7.5)
        const int FLUSH_PACKETS = 20;
        const int EARLY_PROTECTION_MS = 500;
        const int ECHO_GUARD_MS = 120; // v7.5: 120ms reduced echo guard (was 300ms — caused delays)
        int inboundPacketCount = 0;
        bool inboundFlushComplete = false;
        var callStartedAt = DateTime.UtcNow;
        volatile int isBotSpeaking = 0; // 0=false, 1=true
        bool adaHasStartedSpeaking = false;
        DateTime botStoppedSpeakingAt = DateTime.MinValue;
        volatile bool watchdogPending = false;

        // Single OnAudioOut subscription: track bot speaking state AND buffer to playout
        session.OnAudioOut += frame =>
        {
            Interlocked.Exchange(ref isBotSpeaking, 1);
            adaHasStartedSpeaking = true;
            playout.BufferALaw(frame);
        };

        // Wire barge-in → playout clear (only if bot is actually speaking)
        session.OnBargeIn += () =>
        {
            if (Volatile.Read(ref isBotSpeaking) == 0 && playout.QueuedFrames == 0)
                return; // Normal turn start, not a barge-in
            
            playout.Clear();
            Interlocked.Exchange(ref isBotSpeaking, 0);
            adaHasStartedSpeaking = true;
            Log($"✂️ Barge-in (VAD): cleared playout queue");
        };

        // Wire AI response completed → precise bot speaking tracking (matches G711CallHandler v7.4)
        // Listen to the AI client's OnPlayoutComplete which fires on response.done
        if (session.AiClient is OpenAiG711Client g711Client)
        {
            g711Client.OnResponseCompleted += () =>
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                
                // If queue is already empty, notify immediately; otherwise set pending flag
                if (playout.QueuedFrames == 0)
                {
                    Log($"🔔 Queue already empty - starting watchdog now");
                    g711Client.NotifyPlayoutComplete();
                }
                else
                {
                    watchdogPending = true;
                }
            };
        }

        // Wire playout queue empty → reset bot speaking state (matches G711CallHandler v7.4)
        playout.OnQueueEmpty += () =>
        {
            if (adaHasStartedSpeaking && Volatile.Read(ref isBotSpeaking) == 1)
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                Log($"🔇 Playout queue empty - echo guard started");
                session.NotifyPlayoutComplete();
            }
            else if (watchdogPending)
            {
                watchdogPending = false;
                Log($"🔇 Playout drained post-response - starting watchdog");
                session.NotifyPlayoutComplete();
            }
        };

        // Start the playout engine
        playout.Start();

        // ── Wire SIP RTP → AI session (v7.5 pure passthrough with PCMU transcoding) ──
        int framesForwarded = 0;
        rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;

            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            // Fire monitor event for local speaker playback
            OnCallerAudioMonitor?.Invoke(payload);

            // ── Flush: discard first N packets (line noise / codec warm-up) ──
            if (!inboundFlushComplete)
            {
                inboundPacketCount++;
                if (inboundPacketCount < FLUSH_PACKETS) return;
                inboundFlushComplete = true;
                Log($"🔇 Inbound flush complete ({FLUSH_PACKETS} packets discarded)");
            }

            // ── Early protection: ignore audio for first Nms after answer ──
            if ((DateTime.UtcNow - callStartedAt).TotalMilliseconds < EARLY_PROTECTION_MS)
                return;

            // ── v5.2: TRUE passthrough — no decode/DSP/re-encode (eliminates quantization noise) ──
            // Transcode μ-law → A-law if needed (OpenAI expects A-law)
            byte[] g711ToSend;
            if (negotiatedCodec == AudioCodecsEnum.PCMU)
            {
                // Direct μ-law → A-law transcode (no PCM intermediate)
                g711ToSend = Audio.G711.Transcode.MuLawToALaw(payload);
            }
            else
            {
                // Pure A-law passthrough — zero processing
                g711ToSend = payload;
            }

            // ── Soft gate: during bot speaking or echo guard, send silence unless barge-in ──
            bool applySoftGate = false;

            if (Volatile.Read(ref isBotSpeaking) == 1)
            {
                applySoftGate = true;
            }
            else if (adaHasStartedSpeaking && botStoppedSpeakingAt != DateTime.MinValue)
            {
                var msSinceBotStopped = (DateTime.UtcNow - botStoppedSpeakingAt).TotalMilliseconds;
                if (msSinceBotStopped < ECHO_GUARD_MS)
                {
                    applySoftGate = true;
                }
            }

            if (applySoftGate)
            {
                // Check RMS for barge-in detection (decode only for RMS check, then discard)
                var pcmCheck = new short[g711ToSend.Length];
                double sumSq = 0;
                for (int i = 0; i < g711ToSend.Length; i++)
                {
                    pcmCheck[i] = ALawDecode(g711ToSend[i]);
                    sumSq += (double)pcmCheck[i] * pcmCheck[i];
                }
                float rms = (float)Math.Sqrt(sumSq / g711ToSend.Length);

                if (rms >= 1500)
                {
                    // Barge-in detected — let audio through
                    Log($"✂️ Barge-in detected (RMS={rms:F0})");
                }
                else
                {
                    // Not a barge-in — send silence to prevent echo
                    g711ToSend = new byte[payload.Length];
                    Array.Fill(g711ToSend, (byte)0xD5); // A-law silence
                }
            }
            else
            {
                // ── Audio quality diagnostics (only when not gated, gated by setting) ──
                if (_audioSettings.EnableDiagnostics)
                {
                    double sumSq = 0;
                    for (int i = 0; i < g711ToSend.Length; i++)
                    {
                        short pcm = ALawDecode(g711ToSend[i]);
                        sumSq += pcm * (double)pcm;
                    }
                    float rms = (float)Math.Sqrt(sumSq / g711ToSend.Length);

                    int rmsFixed = (int)(rms * 1000);
                    var frameCount = Interlocked.Increment(ref _dqFrameCount);
                    Interlocked.Add(ref _dqRmsSum, rmsFixed);
                    InterlockedMax(ref _dqPeakRms, rmsFixed);
                    InterlockedMin(ref _dqMinRms, rmsFixed);
                    if (rms < 50) Interlocked.Increment(ref _dqSilentFrames);
                    if (rms > 28000) Interlocked.Increment(ref _dqClippedFrames);

                    if (frameCount >= DQ_LOG_INTERVAL_FRAMES)
                    {
                        var totalRms = Interlocked.Exchange(ref _dqRmsSum, 0);
                        var fc = Interlocked.Exchange(ref _dqFrameCount, 0);
                        var peak = Interlocked.Exchange(ref _dqPeakRms, 0);
                        var min = Interlocked.Exchange(ref _dqMinRms, int.MaxValue);
                        var silent = Interlocked.Exchange(ref _dqSilentFrames, 0);
                        var clipped = Interlocked.Exchange(ref _dqClippedFrames, 0);

                        if (fc > 0)
                        {
                            var avgRms = (totalRms / 1000.0) / fc;
                            if (avgRms >= 500)
                            {
                                var peakRms = peak / 1000.0;
                                var minRms = min == int.MaxValue ? 0 : min / 1000.0;
                                var silPct = silent * 100.0 / fc;
                                var clipPct = clipped * 100.0 / fc;
                                var quality = clipPct > 5 ? "⚠️ CLIPPING" : "✅ GOOD";

                                Log($"📊 Audio: avg={avgRms:F0} peak={peakRms:F0} min={minRms:F0} " +
                                    $"silent={silPct:F0}% clipped={clipPct:F0}% → {quality}");
                            }
                        }
                    }
                }
            }

            // ── Ingress volume boost (caller audio often quiet on SIP trunks) ──
            var ingressGain = (float)_audioSettings.IngressVolumeBoost;
            if (ingressGain > 1.01f)
            {
                var boosted = new byte[g711ToSend.Length];
                Buffer.BlockCopy(g711ToSend, 0, boosted, 0, g711ToSend.Length);
                Audio.ALawVolumeBoost.ApplyInPlace(boosted, ingressGain);
                session.ProcessInboundAudio(boosted);
            }
            else
            {
                session.ProcessInboundAudio(g711ToSend);
            }

            framesForwarded++;
            if (framesForwarded % 250 == 0)
                Log($"🎙️ Ingress: {framesForwarded} frames (passthrough 8kHz)");
        };

        // Cleanup: dispose playout when session ends
        session.OnEnded += (s, reason) =>
        {
            playout.Dispose();
        };

        OnCallStarted?.Invoke(caller);
    }

    #endregion

    #region Cleanup

    private async Task CleanupCallAsync(string reason)
    {
        ICallSession? session;

        lock (_callLock)
        {
            session = _currentSession;
            _currentSession = null;
            _activeRtpSession = null;
        }

        if (session != null)
        {
            await session.EndAsync(reason);
        }

        OnCallEnded?.Invoke(reason);
        Log($"📴 Call cleaned up: {reason}");
    }

    /// <summary>
    /// Send operator microphone audio directly into the SIP RTP stream (A-law encoded).
    /// Used in Operator Mode with Push-to-Talk.
    /// Uses cumulative timestamp to handle concatenated/variable-length frames correctly.
    /// </summary>
    public void SendOperatorAudio(byte[] alawData)
    {
        VoIPMediaSession? rtp;
        lock (_callLock)
        {
            rtp = _activeRtpSession;
        }

        if (rtp == null || alawData.Length == 0) return;

        try
        {
            rtp.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                alawData,
                _operatorTimestamp,
                0,  // marker bit
                8); // PCMA payload type
            _operatorTimestamp += (uint)alawData.Length; // cumulative for next packet
        }
        catch { /* ignore send errors during teardown */ }
    }

    #endregion

    #region DNS & Network

    private string ResolveDns(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            Log($"📡 Using IP address directly: {host}");
            return host;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
            {
                Log($"📡 Resolved {host} → {ipv4}");
                return ipv4.ToString();
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ DNS resolution failed: {ex.Message}");
        }

        return host;
    }

    private IPAddress GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(_settings.Server, _settings.Port);
            var localEp = socket.LocalEndPoint as IPEndPoint;
            return localEp?.Address ?? IPAddress.Loopback;
        }
        catch
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip;
            }
            return IPAddress.Loopback;
        }
    }

    private async Task<IPAddress?> DiscoverPublicIpAsync()
    {
        try
        {
            var stunServer = _settings.StunServer ?? "stun.l.google.com";
            var stunPort = _settings.StunPort > 0 ? _settings.StunPort : 19302;

            Log($"🌐 Querying STUN: {stunServer}:{stunPort}...");

            // Simple STUN binding request
            using var udp = new UdpClient();
            var stunAddr = (await Dns.GetHostAddressesAsync(stunServer))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (stunAddr == null)
            {
                Log("⚠️ Could not resolve STUN server");
                return null;
            }

            // STUN Binding Request (RFC 5389)
            var request = new byte[20];
            request[0] = 0x00; request[1] = 0x01; // Binding Request
            request[2] = 0x00; request[3] = 0x00; // Message Length = 0
            // Magic Cookie
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;
            // Transaction ID (12 bytes random)
            Random.Shared.NextBytes(request.AsSpan(8, 12));

            await udp.SendAsync(request, request.Length, new IPEndPoint(stunAddr, stunPort));

            udp.Client.ReceiveTimeout = 3000;
            var ep = new IPEndPoint(IPAddress.Any, 0);
            var response = udp.Receive(ref ep);

            // Parse XOR-MAPPED-ADDRESS or MAPPED-ADDRESS
            return ParseStunResponse(response);
        }
        catch (Exception ex)
        {
            Log($"⚠️ STUN failed: {ex.Message}");
            return null;
        }
    }

    private static IPAddress? ParseStunResponse(byte[] response)
    {
        if (response.Length < 20) return null;

        var msgLen = (response[2] << 8) | response[3];
        var offset = 20;

        while (offset + 4 <= response.Length && offset < 20 + msgLen)
        {
            var attrType = (response[offset] << 8) | response[offset + 1];
            var attrLen = (response[offset + 2] << 8) | response[offset + 3];
            offset += 4;

            // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
            if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8)
            {
                var family = response[offset + 1];
                if (family == 0x01) // IPv4
                {
                    byte[] ip = new byte[4];
                    if (attrType == 0x0020)
                    {
                        // XOR with magic cookie
                        ip[0] = (byte)(response[offset + 4] ^ 0x21);
                        ip[1] = (byte)(response[offset + 5] ^ 0x12);
                        ip[2] = (byte)(response[offset + 6] ^ 0xA4);
                        ip[3] = (byte)(response[offset + 7] ^ 0x42);
                    }
                    else
                    {
                        Array.Copy(response, offset + 4, ip, 0, 4);
                    }
                    return new IPAddress(ip);
                }
            }

            // Align to 4-byte boundary
            offset += attrLen;
            if (attrLen % 4 != 0) offset += 4 - (attrLen % 4);
        }

        return null;
    }

    #endregion

    /// <summary>Quick inline A-law decode for RMS check (ITU-T G.711).</summary>
    private static short ALawDecode(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int segment = (alaw >> 4) & 0x07;
        int value = (alaw & 0x0F) << 4 | 0x08;
        if (segment > 0) value = (value + 0x100) << (segment - 1);
        return (short)(sign * value);
    }

    /// <summary>Thread-safe max update using Interlocked compare-and-swap.</summary>
    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (value > current && Interlocked.CompareExchange(ref location, value, current) != current);
    }

    /// <summary>Thread-safe min update using Interlocked compare-and-swap.</summary>
    private static void InterlockedMin(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (value < current && Interlocked.CompareExchange(ref location, value, current) != current);
    }

    /// <summary>Non-blocking log: enqueues message and drains on ThreadPool.
    /// Safe to call from RTP/playout threads without blocking audio.</summary>
    private void Log(string msg)
    {
        if (_disposed) return;
        _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {msg}");

        // Only one drain loop at a time
        if (Interlocked.CompareExchange(ref _logDraining, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (_logQueue.TryDequeue(out var line))
                    {
                        _logger.LogDebug("{Msg}", line);
                        OnLog?.Invoke(line);
                    }
                }
                catch { /* logging must never crash */ }
                finally
                {
                    Volatile.Write(ref _logDraining, 0);
                    // Re-check in case items were enqueued during finally
                    if (!_logQueue.IsEmpty && Interlocked.CompareExchange(ref _logDraining, 1, 0) == 0)
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { while (_logQueue.TryDequeue(out var l)) { _logger.LogDebug("{Msg}", l); OnLog?.Invoke(l); } }
                            catch { }
                            finally { Volatile.Write(ref _logDraining, 0); }
                        });
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
