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
/// Multi-call SIP server. Handles unlimited concurrent calls.
/// Each incoming call gets its own SIPUserAgent, VoIPMediaSession,
/// ALawRtpPlayout, and CallSession — fully isolated.
/// 
/// v2.0: Refactored from single-call (_currentSession + lock) to
/// ConcurrentDictionary&lt;ActiveCall&gt; for headless server deployment.
/// </summary>
public sealed class SipServer : IAsyncDisposable
{
    private readonly ILogger<SipServer> _logger;
    private readonly SipSettings _settings;
    private readonly AudioSettings _audioSettings;
    private readonly SessionManager _sessionManager;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;

    /// <summary>
    /// Listener agent — only used to receive OnIncomingCall events.
    /// A NEW SIPUserAgent is created per call for answer + dialog management.
    /// </summary>
    private SIPUserAgent? _listenerAgent;

    private IPAddress? _localIp;
    private IPAddress? _publicIp;

    private volatile bool _isRunning;
    private volatile bool _disposed;

    // ── Per-call tracking (replaces single-call _currentSession + _callLock) ──
    private readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();

    // ── Non-blocking async log queue ──
    private readonly ConcurrentQueue<string> _logQueue = new();
    private int _logDraining;
    private const int MAX_LOG_QUEUE_SIZE = 1000;

    public bool IsRegistered { get; private set; }
    public bool IsConnected => _transport != null && IsRegistered;
    public int ActiveCallCount => _activeCalls.Count;

    /// <summary>When true, calls ring and wait for manual answer with no AI pipeline.</summary>
    public bool OperatorMode { get; set; }

    // ── Pending (unanswered) calls for operator mode ──
    private readonly ConcurrentDictionary<string, PendingCall> _pendingCalls = new();

    // ── Events ──
    public event Action<string>? OnLog;
    public event Action<string>? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    /// <summary>Fires with (sessionId, callerId).</summary>
    public event Action<string, string>? OnCallStarted;
    /// <summary>Fires with (sessionId, reason).</summary>
    public event Action<string, string>? OnCallEnded;
    public event Action<string>? OnServerResolved;
    /// <summary>Fires when active call count changes.</summary>
    public event Action<int>? OnActiveCallCountChanged;
    /// <summary>Fires with (pendingId, callerId) when a call is ringing in operator mode.</summary>
    public event Action<string, string>? OnCallRinging;
    /// <summary>Fires with caller A-law audio during operator calls (for speaker output).</summary>
    public event Action<byte[]>? OnOperatorCallerAudio;
    /// <summary>Fires with caller A-law audio during AI calls (for monitor speakers).</summary>
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

        Log("🚀 SipServer starting (multi-call mode)...");
        Log($"➡ SIP Server: {_settings.Server}:{_settings.Port} ({_settings.Transport})");
        Log($"➡ User: {_settings.Username}");

        var resolvedIp = ResolveDns(_settings.Server);
        if (resolvedIp != _settings.Server)
            OnServerResolved?.Invoke(resolvedIp);

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

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
        InitializeCallListener();

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
            var agent = _regAgent;
            _regAgent = null;
            agent.RegistrationSuccessful -= OnRegistrationSuccess;
            agent.RegistrationFailed -= OnRegistrationFailure;
            try { agent.Stop(); } catch { }
        }

        if (_listenerAgent != null)
        {
            _listenerAgent.OnIncomingCall -= OnIncomingCallAsync;
            _listenerAgent = null;
        }

        // End all active calls
        await HangupAllAsync("server_shutdown");

        try { _transport?.Shutdown(); } catch { }
        _transport = null;

        IsRegistered = false;
        _isRunning = false;
        Log("🛑 SipServer stopped");
    }

    /// <summary>Hang up a specific call by session ID.</summary>
    public async Task HangupAsync(string sessionId, string reason = "admin_hangup")
    {
        if (_activeCalls.TryRemove(sessionId, out var call))
        {
            await CleanupCallAsync(call, reason);
        }
    }

    /// <summary>Hang up all active calls.</summary>
    public async Task HangupAllAsync(string reason = "server_shutdown")
    {
        var sessionIds = _activeCalls.Keys.ToList();
        var tasks = sessionIds.Select(id => HangupAsync(id, reason));
        await Task.WhenAll(tasks);
    }

    /// <summary>Get all active session IDs and their caller info.</summary>
    public IReadOnlyList<(string SessionId, string CallerId, DateTime StartedAt)> GetActiveCalls()
    {
        return _activeCalls.Values
            .Select(c => (c.SessionId, c.Session.CallerId, c.Session.StartedAt))
            .ToList();
    }

    #region Initialization

    private void InitializeSipTransport()
    {
        _transport = new SIPTransport();

        _transport.SIPRequestOutTraceEvent += (ep, dst, req) =>
            Log($"📤 SIP OUT → {dst}: {req.Method} {req.URI}");
        _transport.SIPResponseInTraceEvent += (ep, src, resp) =>
            Log($"📥 SIP IN ← {src}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _transport.SIPResponseOutTraceEvent += (ep, dst, resp) =>
            Log($"📤 SIP RESP → {dst}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _transport.SIPRequestInTraceEvent += (ep, src, req) =>
            Log($"📥 SIP REQ ← {src}: {req.Method} {req.URI}");

        // Respond to OPTIONS keepalives so the PBX knows we're alive
        _transport.SIPTransportRequestReceived += async (localEp, remoteEp, req) =>
        {
            if (req.Method == SIPMethodsEnum.OPTIONS)
            {
                var okResp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null);
                await _transport.SendResponseAsync(okResp);
            }
        };

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
        var resolvedHost = ResolveDns(_settings.Server);
        var registrarHostWithPort = _settings.Port == 5060
            ? resolvedHost
            : $"{resolvedHost}:{_settings.Port}";

        if (authUser != _settings.Username)
        {
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
            var sipAccountAor = new SIPURI(_settings.Username, registrarHostWithPort, null, SIPSchemesEnum.sip, protocol);
            var contactUri = new SIPURI(sipAccountAor.Scheme, IPAddress.Any, 0) { User = _settings.Username };

            Log($"🔐 Auth Debug: AOR={sipAccountAor}, AuthUser={authUser}, PassLen={_settings.Password?.Length ?? 0}");

            _regAgent = new SIPRegistrationUserAgent(
                sipTransport: _transport,
                outboundProxy: outboundProxy,
                sipAccountAOR: sipAccountAor,
                authUsername: authUser,
                password: _settings.Password,
                realm: null,
                registrarHost: registrarHostWithPort,
                contactURI: contactUri,
                expiry: 120,
                customHeaders: null);

            Log($"➡ Using separate Auth ID: {authUser}, Registrar: {registrarHostWithPort} (routed via {resolvedHost})");
        }
        else
        {
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

    private void InitializeCallListener()
    {
        _listenerAgent = new SIPUserAgent(_transport, null);
        _listenerAgent.OnIncomingCall += OnIncomingCallAsync;
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

    private void OnIncomingCallAsync(SIPUserAgent ua, SIPRequest req)
    {
        _ = HandleIncomingCallSafeAsync(req).ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log($"💥 Unhandled call exception: {t.Exception?.InnerException?.Message}");
        }, TaskScheduler.Default);
    }

    private async Task HandleIncomingCallSafeAsync(SIPRequest req)
    {
        var caller = CallerIdExtractor.Extract(req);
        Log($"📞 Incoming call from {caller} (active: {_activeCalls.Count})");

        try
        {
            // Send 180 Ringing
            var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
            await _transport!.SendResponseAsync(ringing);
            Log("🔔 Sent 180 Ringing");

            if (OperatorMode)
            {
                // Operator mode: store pending call and wait for manual answer
                var pendingId = Guid.NewGuid().ToString("N")[..8];
                var callAgent = new SIPUserAgent(_transport, null);
                var pendingUas = callAgent.AcceptCall(req);

                var pending = new PendingCall
                {
                    PendingId = pendingId,
                    Caller = caller,
                    Request = req,
                    CallAgent = callAgent,
                    ServerUa = pendingUas
                };

                _pendingCalls[pendingId] = pending;
                Log($"📞 [{pendingId}] Call from {caller} ringing — waiting for operator answer");
                OnCallRinging?.Invoke(pendingId, caller);
            }
            else if (_settings.AutoAnswer)
            {
                await AnswerCallAsync(req, caller);
            }
            else
            {
                Log("⏳ Waiting for manual answer (auto-answer disabled)...");
            }
        }
        catch (Exception ex)
        {
            Log($"🔥 Error handling incoming call from {caller}: {ex.Message}");
            _logger.LogError(ex, "Error handling call from {Caller}", caller);
        }
    }

    private async Task AnswerCallAsync(SIPRequest req, string caller)
    {
        // Create a NEW SIPUserAgent per call — each manages its own dialog independently
        var callAgent = new SIPUserAgent(_transport, null);

        var audioEncoder = new AudioEncoder();
        var audioSource = new AudioExtrasSource(
            audioEncoder,
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

        audioSource.RestrictFormats(fmt =>
            fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);

        var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
        var rtpSession = new VoIPMediaSession(mediaEndPoints);
        rtpSession.AcceptRtpFromAny = true;

        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats =>
        {
            var fmt = formats.FirstOrDefault();
            negotiatedCodec = fmt.Codec;
            Log($"🎵 Negotiated codec: {fmt.Codec} (PT{fmt.FormatID})");
        };

        var serverUa = callAgent.AcceptCall(req);

        // v7.6: 200ms pre-answer delay — lets SIP signaling settle
        await Task.Delay(200);

        var result = await callAgent.Answer(serverUa, rtpSession);
        if (!result)
        {
            Log("❌ Failed to answer call");
            return;
        }

        await rtpSession.Start();
        Log("✅ Call answered and RTP started");

        // Create AI session
        ICallSession session;
        try
        {
            session = await _sessionManager.CreateSessionAsync(caller);
        }
        catch (Exception ex)
        {
            Log($"❌ AI Session Init Failed: {ex.Message}. Ending call.");
            try { callAgent.Hangup(); } catch { }
            rtpSession.Close("ai_init_failed");
            return;
        }

        // Build per-call tracking
        var activeCall = new ActiveCall
        {
            SessionId = session.SessionId,
            Session = session,
            RtpSession = rtpSession,
            CallAgent = callAgent
        };

        if (!_activeCalls.TryAdd(session.SessionId, activeCall))
        {
            Log($"⚠️ Duplicate session ID {session.SessionId} — ending");
            await session.EndAsync("duplicate_session");
            callAgent.Hangup();
            rtpSession.Close("duplicate_session");
            return;
        }

        NotifyCallCountChanged();
        Log($"📞 Call {session.SessionId} active for {caller} (total: {_activeCalls.Count})");

        // Create ALawRtpPlayout
        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log($"[{session.SessionId}] {msg}");
        activeCall.Playout = playout;

        // Wire playout queue depth for drain-aware goodbye
        if (session.AiClient is OpenAiG711Client g711Wire)
            g711Wire.GetQueuedFrames = () => playout.QueuedFrames;
        else
            Log($"[{session.SessionId}] ⚠️ AI client is not G.711 — drain detection disabled");

        // ── Wire audio: Session → Playout ──
        WireAudioPipeline(activeCall, negotiatedCodec);

        playout.Start();

        // Wire hangup events
        callAgent.OnCallHungup += async (dialog) =>
        {
            Log($"[{session.SessionId}] 📴 Remote party hung up");
            await RemoveAndCleanupAsync(session.SessionId, "remote_hangup");
        };

        session.OnEnded += async (s, reason) =>
        {
            await RemoveAndCleanupAsync(s.SessionId, reason);
        };

        OnCallStarted?.Invoke(session.SessionId, caller);
    }

    /// <summary>
    /// Wire the full-duplex audio pipeline for a single call.
    /// Mirrors the proven v7.5 pattern from single-call SipServer.
    /// </summary>
    private void WireAudioPipeline(ActiveCall call, AudioCodecsEnum negotiatedCodec)
    {
        var session = call.Session;
        var playout = call.Playout!;
        var rtpSession = call.RtpSession;
        var sid = call.SessionId;

        const int FLUSH_PACKETS = 20;
        const int EARLY_PROTECTION_MS = 500;
        const int ECHO_GUARD_MS = 300;
        int inboundPacketCount = 0;
        bool inboundFlushComplete = false;
        DateTime lastBargeInLogAt = DateTime.MinValue;
        var callStartedAt = DateTime.UtcNow;
        int isBotSpeaking = 0;
        int adaHasStartedSpeaking = 0;
        DateTime botStoppedSpeakingAt = DateTime.MinValue;
        int watchdogPending = 0;

        // ── Per-call audio quality diagnostics ──
        int dqFrameCount = 0;
        long dqRmsSum = 0;
        int dqPeakRms = 0;
        int dqMinRms = int.MaxValue;
        int dqSilentFrames = 0;
        int dqClippedFrames = 0;
        const int DQ_LOG_INTERVAL_FRAMES = 100;

        // AI audio out → playout buffer
        session.OnAudioOut += frame =>
        {
            Interlocked.Exchange(ref isBotSpeaking, 1);
            Interlocked.Exchange(ref adaHasStartedSpeaking, 1);
            playout.BufferALaw(frame);
        };

        // Barge-in → clear playout
        session.OnBargeIn += () =>
        {
            if (Volatile.Read(ref isBotSpeaking) == 0 && playout.QueuedFrames == 0)
                return;

            playout.Clear();
            Interlocked.Exchange(ref isBotSpeaking, 0);
            Interlocked.Exchange(ref adaHasStartedSpeaking, 1);
            Log($"[{sid}] ✂️ Barge-in (VAD): cleared playout queue");
        };

        // Response completed → track bot speaking state
        if (session.AiClient is OpenAiG711Client g711Client)
        {
            g711Client.OnResponseCompleted += () =>
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;

                if (playout.QueuedFrames == 0)
                {
                    Log($"[{sid}] 🔔 Queue already empty - starting watchdog now");
                    Task.Run(() => g711Client.NotifyPlayoutComplete());
                }
                else
                {
                    Interlocked.Exchange(ref watchdogPending, 1);
                }
            };
        }

        // Playout queue empty → reset bot state
        playout.OnQueueEmpty += () =>
        {
            if (Volatile.Read(ref adaHasStartedSpeaking) == 1 && Volatile.Read(ref isBotSpeaking) == 1)
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                Log($"[{sid}] 🔇 Playout queue empty - echo guard started");
                Task.Run(() => session.NotifyPlayoutComplete());
            }
            else if (Volatile.Read(ref watchdogPending) == 1)
            {
                Interlocked.Exchange(ref watchdogPending, 0);
                Log($"[{sid}] 🔇 Playout drained post-response - starting watchdog");
                Task.Run(() => session.NotifyPlayoutComplete());
            }
        };

        // ── SIP RTP → AI session (ingress) ──
        int framesForwarded = 0;
        rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;

            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            // Flush first N packets (line noise / codec warm-up)
            if (!inboundFlushComplete)
            {
                inboundPacketCount++;
                if (inboundPacketCount < FLUSH_PACKETS) return;
                inboundFlushComplete = true;
                Log($"[{sid}] 🔇 Inbound flush complete ({FLUSH_PACKETS} packets discarded)");
            }

            // Early protection
            if ((DateTime.UtcNow - callStartedAt).TotalMilliseconds < EARLY_PROTECTION_MS)
                return;

            // Transcode μ-law → A-law if needed
            byte[] g711ToSend;
            if (negotiatedCodec == AudioCodecsEnum.PCMU)
                g711ToSend = Audio.G711.Transcode.MuLawToALaw(payload);
            else
                g711ToSend = payload;

            // Soft gate: during bot speech or echo guard, send silence unless barge-in
            bool applySoftGate = false;

            if (Volatile.Read(ref isBotSpeaking) == 1)
            {
                applySoftGate = true;
            }
            else if (Volatile.Read(ref adaHasStartedSpeaking) == 1 && botStoppedSpeakingAt != DateTime.MinValue)
            {
                var msSinceBotStopped = (DateTime.UtcNow - botStoppedSpeakingAt).TotalMilliseconds;
                if (msSinceBotStopped < ECHO_GUARD_MS)
                    applySoftGate = true;
            }

            if (applySoftGate)
            {
                // Barge-in detection via RMS
                var pcmCheck = new short[g711ToSend.Length];
                double sumSq = 0;
                for (int i = 0; i < g711ToSend.Length; i += 4)
                {
                    pcmCheck[i] = ALawDecode(g711ToSend[i]);
                    sumSq += (double)pcmCheck[i] * pcmCheck[i];
                }
                float rms = (float)Math.Sqrt(sumSq / (g711ToSend.Length / 4));

                var bargeInThreshold = _audioSettings.BargeInRmsThreshold > 0 ? _audioSettings.BargeInRmsThreshold : 1200;
                if (rms >= bargeInThreshold)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastBargeInLogAt).TotalMilliseconds >= 1000)
                    {
                        lastBargeInLogAt = now;
                        Log($"[{sid}] ✂️ Barge-in detected (RMS={rms:F0})");
                    }
                }
                else
                {
                    g711ToSend = new byte[payload.Length];
                    Array.Fill(g711ToSend, (byte)0xD5); // A-law silence
                }
            }
            else if (_audioSettings.EnableDiagnostics)
            {
                // Audio quality diagnostics (per-call)
                double sumSq = 0;
                for (int i = 0; i < g711ToSend.Length; i += 4)
                {
                    short pcm = ALawDecode(g711ToSend[i]);
                    sumSq += pcm * (double)pcm;
                }
                float rms = (float)Math.Sqrt(sumSq / (g711ToSend.Length / 4));

                int rmsFixed = (int)(rms * 1000);
                var frameCount = Interlocked.Increment(ref dqFrameCount);
                Interlocked.Add(ref dqRmsSum, rmsFixed);
                InterlockedMax(ref dqPeakRms, rmsFixed);
                InterlockedMin(ref dqMinRms, rmsFixed);
                if (rms < 50) Interlocked.Increment(ref dqSilentFrames);
                if (rms > 28000) Interlocked.Increment(ref dqClippedFrames);

                if (frameCount >= DQ_LOG_INTERVAL_FRAMES)
                {
                    var totalRms = Interlocked.Exchange(ref dqRmsSum, 0);
                    var fc = Interlocked.Exchange(ref dqFrameCount, 0);
                    var peak = Interlocked.Exchange(ref dqPeakRms, 0);
                    var min = Interlocked.Exchange(ref dqMinRms, int.MaxValue);
                    var silent = Interlocked.Exchange(ref dqSilentFrames, 0);
                    var clipped = Interlocked.Exchange(ref dqClippedFrames, 0);

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
                            Log($"[{sid}] 📊 Audio: avg={avgRms:F0} peak={peakRms:F0} min={minRms:F0} " +
                                $"silent={silPct:F0}% clipped={clipPct:F0}% → {quality}");
                        }
                    }
                }
            }

            // Ingress volume boost
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

            // Fire caller audio for monitor speakers (off the critical path)
            try { OnCallerAudioMonitor?.Invoke(g711ToSend); } catch { }

            framesForwarded++;
            if (framesForwarded % 250 == 0)
                Log($"[{sid}] 🎙️ Ingress: {framesForwarded} frames (passthrough 8kHz)");
        };

        // Cleanup playout when session ends
        session.OnEnded += (s, reason) =>
        {
            try { playout.Dispose(); } catch { }
        };
    }

    #endregion

    #region Operator Call Handling

    /// <summary>
    /// Answer a pending operator call — sets up RTP with no AI, just 2-way audio.
    /// Caller audio fires OnOperatorCallerAudio; operator mic goes via SendOperatorAudio.
    /// </summary>
    public async Task<bool> AnswerOperatorCallAsync(string? pendingId = null)
    {
        PendingCall? pending;

        if (pendingId != null)
        {
            _pendingCalls.TryRemove(pendingId, out pending);
        }
        else
        {
            // Take the first pending call
            var key = _pendingCalls.Keys.FirstOrDefault();
            if (key == null) { Log("⚠️ No pending calls to answer"); return false; }
            _pendingCalls.TryRemove(key, out pending);
        }

        if (pending == null) { Log("⚠️ Pending call not found"); return false; }

        var caller = pending.Caller;
        var callAgent = pending.CallAgent;
        var sid = pending.PendingId;

        try
        {
            var audioEncoder = new AudioEncoder();
            var audioSource = new AudioExtrasSource(
                audioEncoder,
                new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

            audioSource.RestrictFormats(fmt =>
                fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);

            var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
            var rtpSession = new VoIPMediaSession(mediaEndPoints);
            rtpSession.AcceptRtpFromAny = true;

            AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
            rtpSession.OnAudioFormatsNegotiated += formats =>
            {
                var fmt = formats.FirstOrDefault();
                negotiatedCodec = fmt.Codec;
                Log($"🎵 [{sid}] Operator call negotiated: {fmt.Codec} (PT{fmt.FormatID})");
            };

            await Task.Delay(200); // pre-answer settle

            var result = await callAgent.Answer(pending.ServerUa, rtpSession);
            if (!result)
            {
                Log($"❌ [{sid}] Failed to answer operator call");
                return false;
            }

            await rtpSession.Start();
            Log($"✅ [{sid}] Operator call answered — 2-way audio active for {caller}");

            // Create playout for operator mic → caller
            var playout = new ALawRtpPlayout(rtpSession);
            playout.OnLog += msg => Log($"[{sid}] {msg}");
            playout.Start();

            // Track as active call (with a dummy session placeholder)
            var activeCall = new ActiveCall
            {
                SessionId = sid,
                Session = new OperatorCallStub(sid, caller),
                RtpSession = rtpSession,
                CallAgent = callAgent,
                Playout = playout,
                IsOperatorCall = true
            };

            _activeCalls[sid] = activeCall;
            NotifyCallCountChanged();

            // Wire caller RTP → OnOperatorCallerAudio (for speakers)
            rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
            {
                if (mediaType != SDPMediaTypesEnum.audio) return;
                var payload = rtpPacket.Payload;
                if (payload == null || payload.Length == 0) return;

                // Transcode μ-law → A-law if needed
                byte[] alawData;
                if (negotiatedCodec == AudioCodecsEnum.PCMU)
                    alawData = Audio.G711.Transcode.MuLawToALaw(payload);
                else
                    alawData = payload;

                OnOperatorCallerAudio?.Invoke(alawData);
            };

            // Wire hangup
            callAgent.OnCallHungup += async (dialog) =>
            {
                Log($"[{sid}] 📴 Caller hung up (operator call)");
                await RemoveAndCleanupAsync(sid, "remote_hangup");
            };

            OnCallStarted?.Invoke(sid, caller);
            return true;
        }
        catch (Exception ex)
        {
            Log($"❌ [{sid}] Operator answer error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reject a pending (ringing) call.</summary>
    public void RejectPendingCall(string? pendingId = null)
    {
        PendingCall? pending;
        if (pendingId != null)
            _pendingCalls.TryRemove(pendingId, out pending);
        else
        {
            var key = _pendingCalls.Keys.FirstOrDefault();
            if (key != null) _pendingCalls.TryRemove(key, out pending);
            else pending = null;
        }

        if (pending == null) return;
        Log($"📴 [{pending.PendingId}] Operator rejected call from {pending.Caller}");
        try { pending.CallAgent.Hangup(); } catch { }
    }

    #endregion


    /// <summary>Remove from tracking and clean up a call.</summary>
    private async Task RemoveAndCleanupAsync(string sessionId, string reason)
    {
        if (_activeCalls.TryRemove(sessionId, out var call))
        {
            await CleanupCallAsync(call, reason);
            NotifyCallCountChanged();
        }
    }

    /// <summary>Clean up a single call — end session, hangup SIP, close RTP.</summary>
    private async Task CleanupCallAsync(ActiveCall call, string reason)
    {
        // Atomic guard: only first caller cleans up
        if (Interlocked.Exchange(ref call.CleanupDone, 1) == 1)
            return;

        var sid = call.SessionId;

        try { await call.Session.EndAsync(reason); }
        catch (Exception ex) { Log($"[{sid}] ⚠ Session end error: {ex.Message}"); }

        try { call.CallAgent.Hangup(); }
        catch (Exception ex) { Log($"[{sid}] ⚠ SIP hangup error: {ex.Message}"); }

        try { call.RtpSession.Close(reason); }
        catch (Exception ex) { Log($"[{sid}] ⚠ RTP close error: {ex.Message}"); }

        try { call.Playout?.Dispose(); }
        catch { }

        OnCallEnded?.Invoke(sid, reason);
        Log($"[{sid}] 📴 Call cleaned up: {reason} (remaining: {_activeCalls.Count})");
    }

    private void NotifyCallCountChanged()
    {
        OnActiveCallCountChanged?.Invoke(_activeCalls.Count);
    }

    /// <summary>
    /// Sends operator audio to the first active call's RTP stream.
    /// Used by desktop PTT (Push-to-Talk) mode.
    /// </summary>
    public void SendOperatorAudio(byte[] alawData)
    {
        // Pick the first active call (desktop typically has one)
        var call = _activeCalls.Values.FirstOrDefault();
        if (call?.Playout == null) return;

        call.Playout.BufferALaw(alawData);
    }

    /// <summary>
    /// Sends operator audio to a specific active call by session ID.
    /// </summary>
    public void SendOperatorAudio(string sessionId, byte[] alawData)
    {
        if (!_activeCalls.TryGetValue(sessionId, out var call)) return;
        call.Playout?.BufferALaw(alawData);
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

            using var udp = new UdpClient();
            var stunAddr = (await Dns.GetHostAddressesAsync(stunServer))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (stunAddr == null)
            {
                Log("⚠️ Could not resolve STUN server");
                return null;
            }

            var request = new byte[20];
            request[0] = 0x00; request[1] = 0x01;
            request[2] = 0x00; request[3] = 0x00;
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;
            Random.Shared.NextBytes(request.AsSpan(8, 12));

            await udp.SendAsync(request, request.Length, new IPEndPoint(stunAddr, stunPort));

            udp.Client.ReceiveTimeout = 3000;
            var ep = new IPEndPoint(IPAddress.Any, 0);
            var response = udp.Receive(ref ep);

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

            if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8)
            {
                var family = response[offset + 1];
                if (family == 0x01)
                {
                    byte[] ip = new byte[4];
                    if (attrType == 0x0020)
                    {
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

            offset += attrLen;
            if (attrLen % 4 != 0) offset += 4 - (attrLen % 4);
        }

        return null;
    }

    #endregion

    #region Audio Utilities

    private static readonly short[] ALawDecodeTable = BuildALawTable();

    private static short[] BuildALawTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int a = i ^ 0x55;
            int sign = (a & 0x80) != 0 ? -1 : 1;
            int segment = (a >> 4) & 0x07;
            int value = (a & 0x0F) << 4 | 0x08;
            if (segment > 0) value = (value + 0x100) << (segment - 1);
            table[i] = (short)(sign * value);
        }
        return table;
    }

    private static short ALawDecode(byte alaw) => ALawDecodeTable[alaw];

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (value > current && Interlocked.CompareExchange(ref location, value, current) != current);
    }

    private static void InterlockedMin(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (value < current && Interlocked.CompareExchange(ref location, value, current) != current);
    }

    #endregion

    #region Logging

    private void Log(string msg)
    {
        if (_disposed) return;

        while (_logQueue.Count >= MAX_LOG_QUEUE_SIZE)
            _logQueue.TryDequeue(out _);

        _logQueue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {msg}");

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
                catch { }
                finally
                {
                    Volatile.Write(ref _logDraining, 0);
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

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    /// <summary>
    /// Tracks all resources for a single active call.
    /// Fully isolated — no shared mutable state between calls.
    /// </summary>
    private sealed class ActiveCall
    {
        public required string SessionId { get; init; }
        public required ICallSession Session { get; init; }
        public required VoIPMediaSession RtpSession { get; init; }
        public required SIPUserAgent CallAgent { get; init; }
        public ALawRtpPlayout? Playout { get; set; }
        public bool IsOperatorCall { get; init; }

        /// <summary>Atomic cleanup guard: 0=pending, 1=done.</summary>
        public int CleanupDone;
    }

    /// <summary>Tracks a ringing call waiting for operator answer.</summary>
    private sealed class PendingCall
    {
        public required string PendingId { get; init; }
        public required string Caller { get; init; }
        public required SIPRequest Request { get; init; }
        public required SIPUserAgent CallAgent { get; init; }
        public required SIPServerUserAgent ServerUa { get; init; }
    }

    /// <summary>
    /// Stub ICallSession for operator calls (no AI). 
    /// Satisfies the ActiveCall.Session requirement without creating an AI pipeline.
    /// </summary>
    private sealed class OperatorCallStub : ICallSession
    {
        public string SessionId { get; }
        public string CallerId { get; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public bool IsActive { get; private set; } = true;
        public IOpenAiClient AiClient => null!; // No AI in operator mode

        public event Action<byte[]>? OnAudioOut;
        public event Action? OnBargeIn;
        public event Action<BookingState>? OnBookingUpdated;
        public event Action<ICallSession, string>? OnEnded;

        public OperatorCallStub(string sessionId, string callerId)
        {
            SessionId = sessionId;
            CallerId = callerId;
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ProcessInboundAudio(byte[] alawFrame) { }
        public byte[]? GetOutboundFrame() => null;
        public void NotifyPlayoutComplete() { }

        public Task EndAsync(string reason)
        {
            IsActive = false;
            OnEnded?.Invoke(this, reason);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsActive = false;
            return ValueTask.CompletedTask;
        }
    }
}
