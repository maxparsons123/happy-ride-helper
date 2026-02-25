// Last updated: 2026-02-22 (v2.9)
// Changes from v2.8:
//   - NotifyPlayoutComplete dispatched via Task.Run() — prevents playout thread blocking on 20ms tick
//   - Soft-gate RMS loop samples every 4th byte (8000 → 2000 ops/sec, statistically equivalent)
//   - Debounce timestamps use Stopwatch ticks throughout (no DateTimeOffset.UtcNow in hot paths)
//   - OnBargeIn guard: playout.Clear() called before latch resets (correct ordering)
//   - Minor: inboundFlushComplete early-return moved before codec conversion (saves alloc)
//   - Minor: CleanupCallAsync playout.Dispose() called first to stop RTP before session end
//   - Caller audio monitor fires for ALL calls (AI + operator), not just operator mode

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AdaSdkModel.Ai;
using AdaSdkModel.Audio;
using AdaSdkModel.Config;
using AdaSdkModel.Core;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace AdaSdkModel.Sip;

/// <summary>
/// Multi-call SIP server with G.711 A-law passthrough + OpenAI SDK.
/// Supports operator mode (manual answer/reject) and auto-answer mode.
///
/// Audio Pipeline: v11 — playout handles accumulator + ConcurrentBag frame pooling internally.
/// SipServer passes raw frames; no pre-slicing needed.
/// </summary>
public sealed class SipServer : IAsyncDisposable
{
    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ILogger<SipServer> _logger;
    private readonly SipSettings _settings;
    private readonly AudioSettings _audioSettings;
    private readonly SessionManager _sessionManager;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _listenerAgent;
    private IPAddress? _localIp;

    private volatile bool _isRunning;
    private volatile bool _disposed;

    private readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();

    // Operator mode: pending call waiting for manual answer
    private PendingCall? _pendingCall;
    private readonly object _pendingLock = new();

    public bool IsRegistered { get; private set; }
    public int ActiveCallCount => _activeCalls.Count;

    /// <summary>When true, incoming calls ring and wait for manual answer.</summary>
    public bool OperatorMode { get; set; }

    // Events
    public event Action<string>? OnLog;
    public event Action<string>? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string, string>? OnCallStarted;        // sessionId, callerId
    public event Action<string, string>? OnCallRinging;        // pendingId, callerId
    public event Action<string, string>? OnCallEnded;          // sessionId, reason
    public event Action<byte[]>? OnOperatorCallerAudio;        // A-law audio from caller for monitor

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

        Log("🚀 SipServer starting (AdaSdkModel)...");
        Log($"➡ SIP: {_settings.Server}:{_settings.Port} ({_settings.Transport})");

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

        InitializeSipTransport();

        _listenerAgent = new SIPUserAgent(_transport, null);
        _listenerAgent.OnIncomingCall += (ua, req) =>
        {
            _ = HandleIncomingCallAsync(req).ContinueWith(t =>
            {
                if (t.IsFaulted) Log($"💥 Call error: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        };

        InitializeRegistration();

        if (_regAgent == null)
        {
            Log("❌ Registration agent was not created — check server address (trim whitespace, ensure valid IP or hostname).");
            return;
        }
        _regAgent.Start();
        _isRunning = true;
        Log("🟢 Waiting for SIP registration...");
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        Log("🛑 Stopping...");

        _regAgent?.Stop();
        _listenerAgent = null;

        RejectPendingCall();

        foreach (var sid in _activeCalls.Keys.ToList())
        {
            if (_activeCalls.TryRemove(sid, out var call))
                await CleanupCallAsync(call, "shutdown");
        }

        try { _transport?.Shutdown(); } catch { }
        _transport = null;
        IsRegistered = false;
        _isRunning = false;
    }

    private void InitializeSipTransport()
    {
        _transport = new SIPTransport();

        _transport.SIPRequestOutTraceEvent += (ep, dst, req) => Log($"📤 SIP OUT → {dst}: {req.Method} {req.URI}");
        _transport.SIPResponseInTraceEvent += (ep, src, resp) => Log($"📥 SIP IN ← {src}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _transport.SIPResponseOutTraceEvent += (ep, dst, resp) => Log($"📤 SIP RESP → {dst}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        _transport.SIPRequestInTraceEvent += (ep, src, req) => Log($"📥 SIP REQ ← {src}: {req.Method} {req.URI}");

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
            case "TCP":
                _transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(_localIp!, 0)));
                Log("📡 Using TCP transport");
                break;
            case "TLS":
                _transport.AddSIPChannel(new SIPTLSChannel(new IPEndPoint(_localIp!, 0)));
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
        var authUser = _settings.EffectiveAuthUser;
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
                        _transport, _settings.Username, _settings.Password, registrarHostWithPort, 120);
                    WireRegistrationEvents();
                    return;
                }
            }

            var protocol = _settings.Transport.ToUpperInvariant() switch
            {
                "TCP" => SIPProtocolsEnum.tcp,
                "TLS" => SIPProtocolsEnum.tls,
                _ => SIPProtocolsEnum.udp
            };
            var outboundProxy = new SIPEndPoint(protocol, new IPEndPoint(registrarIp, _settings.Port));
            var sipAccountAor = new SIPURI(_settings.Username, registrarHostWithPort, null, SIPSchemesEnum.sip, protocol);
            var contactUri = new SIPURI(sipAccountAor.Scheme, IPAddress.Any, 0) { User = _settings.Username };

            Log($"🔐 Auth: AOR={sipAccountAor}, AuthUser={authUser}");

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
                _transport, _settings.Username, _settings.Password, registrarHostWithPort, 120);
            Log($"📡 Registration: {_settings.Username}@{registrarHostWithPort}");
        }

        WireRegistrationEvents();
    }

    private void WireRegistrationEvents()
    {
        _regAgent!.RegistrationSuccessful += (uri, resp) =>
        {
            Log($"✅ SIP Registered as {_settings.Username}@{_settings.Server}");
            IsRegistered = true;
            OnRegistered?.Invoke($"{_settings.Username}@{_settings.Server}");
        };
        _regAgent.RegistrationFailed += (uri, resp, err) =>
        {
            Log($"❌ SIP Registration failed: {err}");
            IsRegistered = false;
            OnRegistrationFailed?.Invoke(err ?? "Unknown error");
        };
    }

    private async Task HandleIncomingCallAsync(SIPRequest req)
    {
        var caller = CallerIdExtractor.Extract(req);
        Log($"📞 Incoming call from {caller} (active: {_activeCalls.Count})");

        // ── Auto-terminate existing sessions for the same caller ──
        var existingForCaller = _activeCalls.Values
            .Where(c => c.Session != null && 
                   string.Equals(c.Session.CallerId, caller, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var existing in existingForCaller)
        {
            Log($"⚠️ Terminating stale session {existing.SessionId} for {caller} (replaced by new INVITE)");
            await RemoveAndCleanupAsync(existing.SessionId, "replaced_by_new_call");
        }

        var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
        await _transport!.SendResponseAsync(ringing);

        if (OperatorMode)
        {
            var pendingId = Guid.NewGuid().ToString("N")[..8];
            lock (_pendingLock)
            {
                RejectPendingCallInternal();
                _pendingCall = new PendingCall { PendingId = pendingId, CallerId = caller, Request = req };
            }
            OnCallRinging?.Invoke(pendingId, caller);
            Log($"📲 [{pendingId}] Operator mode — waiting for manual answer");
            return;
        }

        if (!_settings.AutoAnswer) return;
        await AnswerAndWireCallAsync(req, caller);
    }

    /// <summary>Answer the pending call in operator mode — NO AI, pure two-way audio.</summary>
    public async Task<bool> AnswerOperatorCallAsync()
    {
        PendingCall? pending;
        lock (_pendingLock)
        {
            pending = _pendingCall;
            _pendingCall = null;
        }
        if (pending == null) { Log("⚠ No pending call to answer"); return false; }

        Log($"✅ Answering pending call from {pending.CallerId} (operator mode — no AI)…");
        await AnswerOperatorCallDirectAsync(pending.Request, pending.CallerId);
        return true;
    }

    /// <summary>
    /// Answer a call in pure operator mode: SIP + RTP only, no AI session.
    /// Caller audio → local speakers, operator mic → caller via playout.
    /// </summary>
    private async Task AnswerOperatorCallDirectAsync(SIPRequest req, string caller)
    {
        var callAgent = new SIPUserAgent(_transport, null);
        var audioSource = BuildAudioSource();
        var rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = audioSource });
        rtpSession.AcceptRtpFromAny = true;

        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats => negotiatedCodec = formats.FirstOrDefault().Codec;

        var serverUa = callAgent.AcceptCall(req);
        await Task.Delay(200);

        if (!await callAgent.Answer(serverUa, rtpSession))
        {
            Log("❌ Failed to answer operator call"); return;
        }

        await rtpSession.Start();

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var activeCall = new ActiveCall
        {
            SessionId = sessionId,
            Session = null,
            RtpSession = rtpSession,
            CallAgent = callAgent
        };

        if (!_activeCalls.TryAdd(sessionId, activeCall))
        {
            callAgent.Hangup(); rtpSession.Close("dup"); return;
        }

        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log($"[{sessionId}] {msg}");
        activeCall.Playout = playout;

        // Caller audio → local speaker monitor only (no AI)
        rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            byte[] g711 = negotiatedCodec == AudioCodecsEnum.PCMU
                ? G711Transcode.MuLawToALaw(payload) : payload;
            OnOperatorCallerAudio?.Invoke(g711);
        };

        playout.Start();

        callAgent.OnCallHungup += async (_) =>
        {
            if (_activeCalls.TryRemove(sessionId, out var c))
                await CleanupCallAsync(c, "remote_hangup");
            OnCallEnded?.Invoke(sessionId, "remote_hangup");
        };

        OnCallStarted?.Invoke(sessionId, caller);
        Log($"📞 Operator call {sessionId} active for {caller} (no AI, total: {_activeCalls.Count})");
    }

    /// <summary>Reject the pending call.</summary>
    public void RejectPendingCall()
    {
        lock (_pendingLock) { RejectPendingCallInternal(); }
    }

    private void RejectPendingCallInternal()
    {
        if (_pendingCall == null) return;
        try
        {
            var busy = SIPResponse.GetResponse(_pendingCall.Request, SIPResponseStatusCodesEnum.BusyHere, null);
            _transport?.SendResponseAsync(busy).GetAwaiter().GetResult();
        }
        catch { }
        Log($"❌ Rejected pending call from {_pendingCall.CallerId}");
        _pendingCall = null;
    }

    /// <summary>Hang up all active calls.</summary>
    public async Task HangupAllAsync(string reason)
    {
        foreach (var sid in _activeCalls.Keys.ToList())
        {
            if (_activeCalls.TryRemove(sid, out var call))
                await CleanupCallAsync(call, reason);
        }
    }

    /// <summary>Initiate an outbound call to the given number/SIP URI.</summary>
    public async Task MakeCallAsync(string destination)
    {
        if (_transport == null) throw new InvalidOperationException("SIP transport not started");

        var serverAddr = _settings.Server.Trim();
        if (!IPAddress.TryParse(serverAddr, out _))
        {
            var resolved = Dns.GetHostAddresses(serverAddr)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (resolved != null) serverAddr = resolved.ToString();
        }
        var portPart = _settings.Port == 5060 ? "" : $":{_settings.Port}";
        var destUri = SIPURI.ParseSIPURI($"sip:{destination}@{serverAddr}{portPart}");

        var callAgent = new SIPUserAgent(_transport, null);
        var audioSource = BuildAudioSource();
        var rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = audioSource });
        rtpSession.AcceptRtpFromAny = true;

        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats => negotiatedCodec = formats.FirstOrDefault().Codec;

        var sessionId = $"out-{Guid.NewGuid().ToString("N")[..8]}";

        Log($"📞 Dialling {destination} via {serverAddr}{portPart}…");
        var result = await callAgent.Call(destUri.ToString(), null, null, rtpSession);
        if (!result)
        {
            Log($"❌ Outbound call to {destination} failed.");
            rtpSession.Close("call_failed");
            callAgent.Dispose();
            return;
        }

        ICallSession session;
        try { session = await _sessionManager.CreateSessionAsync(destination); }
        catch (Exception ex)
        {
            Log($"❌ AI init failed: {ex.Message}");
            callAgent.Hangup(); rtpSession.Close("ai_fail"); return;
        }

        var activeCall = new ActiveCall
        {
            SessionId = session.SessionId,
            Session = session,
            RtpSession = rtpSession,
            CallAgent = callAgent
        };
        _activeCalls[sessionId] = activeCall;

        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log($"[{sessionId}] {msg}");
        activeCall.Playout = playout;

        // GetQueuedFrames is now wired inside WireAudioPipeline

        WireAudioPipeline(activeCall, negotiatedCodec);

        callAgent.OnCallHungup += async (_) =>
        {
            if (_activeCalls.TryRemove(sessionId, out var c))
                await CleanupCallAsync(c, "remote_hangup");
        };
        session.OnEnded += async (s, r) => await RemoveAndCleanupAsync(sessionId, r);
        session.OnEscalate += async (s, reason) => await HandleEscalationTransferAsync(sessionId, reason);

        playout.Start();
        OnCallStarted?.Invoke(sessionId, destination);
        Log($"✅ Outbound call connected: {destination}");
    }

    public void SendOperatorAudio(byte[] alawData)
    {
        foreach (var call in _activeCalls.Values)
        {
            try { call.Playout?.BufferALaw(alawData); }
            catch { }
        }
    }

    private async Task AnswerAndWireCallAsync(SIPRequest req, string caller, bool isOperator = false)
    {
        var callAgent = new SIPUserAgent(_transport, null);
        var audioSource = BuildAudioSource();
        var rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = audioSource });
        rtpSession.AcceptRtpFromAny = true;

        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats => negotiatedCodec = formats.FirstOrDefault().Codec;

        var serverUa = callAgent.AcceptCall(req);
        await Task.Delay(200);

        if (!await callAgent.Answer(serverUa, rtpSession))
        {
            Log("❌ Failed to answer"); return;
        }

        await rtpSession.Start();

        ICallSession session;
        try { session = await _sessionManager.CreateSessionAsync(caller); }
        catch (Exception ex)
        {
            Log($"❌ AI init failed: {ex.Message}");
            callAgent.Hangup(); rtpSession.Close("ai_fail"); return;
        }

        var activeCall = new ActiveCall
        {
            SessionId = session.SessionId,
            Session = session,
            RtpSession = rtpSession,
            CallAgent = callAgent
        };

        if (!_activeCalls.TryAdd(session.SessionId, activeCall))
        {
            await session.EndAsync("duplicate"); callAgent.Hangup(); rtpSession.Close("dup"); return;
        }

        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log($"[{session.SessionId}] {msg}");
        activeCall.Playout = playout;

        // GetQueuedFrames is now wired inside WireAudioPipeline

        WireAudioPipeline(activeCall, negotiatedCodec);

        callAgent.OnCallHungup += async (_) => await RemoveAndCleanupAsync(session.SessionId, "remote_hangup");
        session.OnEnded += async (s, r) => await RemoveAndCleanupAsync(s.SessionId, r);
        session.OnEscalate += async (s, reason) => await HandleEscalationTransferAsync(s.SessionId, reason);

        playout.Start();

        OnCallStarted?.Invoke(session.SessionId, caller);
        Log($"📞 Call {session.SessionId} active for {caller} (total: {_activeCalls.Count})");
    }

    private void WireAudioPipeline(ActiveCall call, AudioCodecsEnum negotiatedCodec)
    {
        var session = call.Session!;
        var playout = call.Playout!;
        var rtpSession = call.RtpSession;
        var sid = call.SessionId;

        int isBotSpeaking = 0, adaHasStartedSpeaking = 0, watchdogPending = 0;
        long botStoppedSpeakingTick = 0;
        var callStartTick = Stopwatch.GetTimestamp();
        bool inboundFlushComplete = false;
        int inboundPacketCount = 0;

        // Debounce: Stopwatch ticks (no DateTimeOffset.UtcNow in hot path)
        long lastNotifyPlayoutCompleteTick = 0;
        long debounceThresholdTicks = (long)(200_000_000.0 / NsPerTick); // 200ms in ticks

        int responseCompletedLatch = 0;

        // ── DIRECT WIRING: OpenAI A-law → playout (no intermediate pipe = lowest latency) ──
        var gain = (float)_audioSettings.VolumeBoost;
        bool applyGain = Math.Abs(gain - 1.0f) > 0.001f;

        // Inline handler: apply gain, fork to listeners, then feed playout
        Action<byte[]> feedPlayout = (byte[] alawBytes) =>
        {
            if (alawBytes == null || alawBytes.Length == 0) return;
            if (applyGain) Audio.Processing.ALawGain.ApplyInPlace(alawBytes, gain);

            Interlocked.Exchange(ref isBotSpeaking, 1);
            Interlocked.Exchange(ref adaHasStartedSpeaking, 1);

            playout.BufferALaw(alawBytes);
        };

        // Wire the appropriate AI client
        if (session.AiClient is OpenAiSdkClient sdkClient)
        {
            sdkClient.OnAudioRaw += feedPlayout;
            sdkClient.GetQueuedFrames = () => playout.QueuedFrames;
        }
        else if (session.AiClient is OpenAiSdkClientHighSample highSample)
        {
            highSample.OnAudio += feedPlayout;
            highSample.GetQueuedFrames = () => playout.QueuedFrames;
        }

        // ── Barge-in: clear playout, then reset latches ──
        session.OnBargeIn += () =>
        {
            if (Volatile.Read(ref isBotSpeaking) == 0 && playout.QueuedFrames == 0) return;

            playout.Clear();

            Interlocked.Exchange(ref watchdogPending, 0);
            Interlocked.Exchange(ref responseCompletedLatch, 0);
            Interlocked.Exchange(ref isBotSpeaking, 0);
        };

        // ── Response completed: flush tail, set latches ──
        session.AiClient.OnResponseCompleted += () =>
        {
            // Direct wiring — no pipe to flush
            Interlocked.Exchange(ref responseCompletedLatch, 1);
            Interlocked.Exchange(ref isBotSpeaking, 0);
            Interlocked.Exchange(ref botStoppedSpeakingTick, Stopwatch.GetTimestamp());

            if (playout.QueuedFrames == 0)
            {
                long nowTick = Stopwatch.GetTimestamp();
                if (nowTick - Interlocked.Read(ref lastNotifyPlayoutCompleteTick) >= debounceThresholdTicks)
                {
                    Interlocked.Exchange(ref lastNotifyPlayoutCompleteTick, nowTick);
                    Interlocked.Exchange(ref responseCompletedLatch, 0);
                    Task.Run(() => session.NotifyPlayoutComplete());
                }
            }
            else
            {
                Interlocked.Exchange(ref watchdogPending, 1);
            }
        };

        // ── Queue empty: notify completion once both latches are set ──
        playout.OnQueueEmpty += () =>
        {
            if (Volatile.Read(ref watchdogPending) != 1 ||
                Volatile.Read(ref responseCompletedLatch) != 1)
                return;

            long nowTick = Stopwatch.GetTimestamp();
            if (nowTick - Interlocked.Read(ref lastNotifyPlayoutCompleteTick) < debounceThresholdTicks)
                return;

            Interlocked.Exchange(ref lastNotifyPlayoutCompleteTick, nowTick);
            Interlocked.Exchange(ref watchdogPending, 0);
            Interlocked.Exchange(ref responseCompletedLatch, 0);

            Task.Run(() => session.NotifyPlayoutComplete());
        };

        // ── Inbound RTP ──
        rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            if (!inboundFlushComplete)
            {
                if (++inboundPacketCount < 20) return;
                inboundFlushComplete = true;
            }

            long elapsedNs = (long)((Stopwatch.GetTimestamp() - callStartTick) * NsPerTick);
            if (elapsedNs < 500_000_000L) return;

            byte[] g711ToSend = negotiatedCodec == AudioCodecsEnum.PCMU
                ? G711Transcode.MuLawToALaw(payload) : payload;

            try { OnOperatorCallerAudio?.Invoke(g711ToSend); } catch { }

            // ── Soft gate: suppress echo while bot is speaking / just stopped ──
            bool applySoftGate = Volatile.Read(ref isBotSpeaking) == 1;
            if (!applySoftGate && Volatile.Read(ref adaHasStartedSpeaking) == 1)
            {
                long stoppedTick = Interlocked.Read(ref botStoppedSpeakingTick);
                if (stoppedTick != 0)
                {
                    long sinceStopNs = (long)((Stopwatch.GetTimestamp() - stoppedTick) * NsPerTick);
                    if (sinceStopNs < 300_000_000L)
                        applySoftGate = true;
                }
            }

            if (applySoftGate)
            {
                double sumSq = 0;
                for (int i = 0; i < g711ToSend.Length; i += 4)
                {
                    short pcm = ALawDecode(g711ToSend[i]);
                    sumSq += (double)pcm * pcm;
                }
                float rms = (float)Math.Sqrt(sumSq / Math.Max(1, g711ToSend.Length / 4));
                float thresh = _audioSettings.BargeInRmsThreshold > 0
                    ? _audioSettings.BargeInRmsThreshold : 1200f;

                if (rms < thresh) return;
            }

            float ingressGain = (float)_audioSettings.IngressVolumeBoost;
            if (ingressGain > 1.01f)
            {
                var boosted = new byte[g711ToSend.Length];
                Buffer.BlockCopy(g711ToSend, 0, boosted, 0, g711ToSend.Length);
                ALawVolumeBoost.ApplyInPlace(boosted, ingressGain);
                session.ProcessInboundAudio(boosted);
            }
            else
            {
                session.ProcessInboundAudio(g711ToSend);
            }
        };

        // ── Cleanup: unhook audio + dispose playout when session ends ──
        session.OnEnded += (_, _) =>
        {
            // Unhook raw audio first to prevent double-speaking on next call
            if (session.AiClient is OpenAiSdkClient sdk)
                sdk.OnAudioRaw -= feedPlayout;
            else if (session.AiClient is OpenAiSdkClientHighSample hs)
                hs.OnAudio -= feedPlayout;

            try { playout.Dispose(); } catch { }
        };
    }

    private async Task RemoveAndCleanupAsync(string sessionId, string reason)
    {
        if (_activeCalls.TryRemove(sessionId, out var call))
            await CleanupCallAsync(call, reason);
        OnCallEnded?.Invoke(sessionId, reason);
    }

    /// <summary>
    /// Handle escalation: SIP REFER transfer to operator extension, then clean up AI session.
    /// </summary>
    private async Task HandleEscalationTransferAsync(string sessionId, string reason)
    {
        if (!_activeCalls.TryGetValue(sessionId, out var call))
        {
            Log($"⚠ Escalation for {sessionId} but call not found");
            return;
        }

        var transferExt = _settings.OperatorTransferExtension;
        if (string.IsNullOrWhiteSpace(transferExt))
        {
            Log($"⚠ No OperatorTransferExtension configured — cannot transfer {sessionId}");
            await RemoveAndCleanupAsync(sessionId, $"escalation_no_ext:{reason}");
            return;
        }

        try
        {
            var serverAddr = ResolveDns(_settings.Server);
            var portPart = _settings.Port == 5060 ? "" : $":{_settings.Port}";
            var targetUri = SIPURI.ParseSIPURI($"sip:{transferExt}@{serverAddr}{portPart}");

            Log($"🔀 [{sessionId}] Transferring to operator ext {transferExt} (reason: {reason})...");

            // Wait for Ada's "transferring you now" speech to drain from playout queue
            // (drain-wait with 5s timeout cap, same pattern as NotifyPlayoutComplete coordinator)
            if (_activeCalls.TryGetValue(sessionId, out var drainCall) && drainCall.Playout != null)
            {
                var drainStart = Stopwatch.GetTimestamp();
                while (drainCall.Playout.QueuedFrames > 0)
                {
                    long elapsedMs = (long)((Stopwatch.GetTimestamp() - drainStart) * NsPerTick / 1_000_000.0);
                    if (elapsedMs > 5000) break; // 5s timeout cap
                    await Task.Delay(50);
                }
                // Small tail pause after drain for natural feel
                await Task.Delay(300);
            }
            else
            {
                // Fallback if playout not available
                await Task.Delay(2500);
            }

            bool transferred = await call.CallAgent.BlindTransfer(targetUri, TimeSpan.FromSeconds(15), default);

            if (transferred)
            {
                Log($"✅ [{sessionId}] SIP REFER transfer successful to {transferExt}");
                try { call.Playout?.Dispose(); } catch { }
                if (call.Session != null)
                    try { await call.Session.EndAsync($"escalated:{reason}"); } catch { }
                _activeCalls.TryRemove(sessionId, out _);
                OnCallEnded?.Invoke(sessionId, $"escalated:{reason}");
            }
            else
            {
                Log($"❌ [{sessionId}] SIP REFER failed — hanging up");
                await RemoveAndCleanupAsync(sessionId, $"escalation_transfer_failed:{reason}");
            }
        }
        catch (Exception ex)
        {
            Log($"❌ [{sessionId}] Escalation transfer error: {ex.Message}");
            await RemoveAndCleanupAsync(sessionId, $"escalation_error:{reason}");
        }
    }

    private async Task CleanupCallAsync(ActiveCall call, string reason)
    {
        if (Interlocked.Exchange(ref call.CleanupDone, 1) == 1) return;

        // Stop playout first — ensures no more RTP frames are sent during teardown

        // Stop playout — ensures no more RTP frames are sent during teardown
        try { call.Playout?.Dispose(); } catch { }

        if (call.Session != null)
            try { await call.Session.EndAsync(reason); } catch { }

        try { call.CallAgent.Hangup(); } catch { }
        try { call.RtpSession.Close(reason); } catch { }

        Log($"[{call.SessionId}] 📴 Cleaned up: {reason}");
    }

    // ── Helpers ──

    private static AudioExtrasSource BuildAudioSource()
    {
        var encoder = new AudioEncoder();
        var source = new AudioExtrasSource(encoder, new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        source.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);
        return source;
    }

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

    private string ResolveDns(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try { return Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork).ToString(); }
        catch { return host; }
    }

    private IPAddress GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(_settings.Server, _settings.Port);
            return (s.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        }
        catch { return IPAddress.Loopback; }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public async ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; await StopAsync(); }
    }

    private sealed class ActiveCall
    {
        public required string SessionId { get; init; }
        public ICallSession? Session { get; init; }   // null in operator mode
        public required VoIPMediaSession RtpSession { get; init; }
        public required SIPUserAgent CallAgent { get; init; }
        public ALawRtpPlayout? Playout { get; set; }
        public int CleanupDone;
    }

    private sealed class PendingCall
    {
        public required string PendingId { get; init; }
        public required string CallerId { get; init; }
        public required SIPRequest Request { get; init; }
    }
}
