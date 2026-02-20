using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Zaffiqbal247RadioCars.Ai;
using Zaffiqbal247RadioCars.Audio;
using Zaffiqbal247RadioCars.Config;
using Zaffiqbal247RadioCars.Core;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace Zaffiqbal247RadioCars.Sip;

/// <summary>
/// Multi-call SIP server with G.711 A-law passthrough + OpenAI SDK.
/// Supports operator mode (manual answer/reject) and auto-answer mode.
/// </summary>
public sealed class SipServer : IAsyncDisposable
{
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
    public event Action<string, string>? OnCallRinging;         // pendingId, callerId
    public event Action<string, string>? OnCallEnded;           // sessionId, reason
    public event Action<byte[]>? OnOperatorCallerAudio;         // A-law audio from caller for monitor

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

        Log("🚀 SipServer starting (Zaffiqbal247RadioCars)...");
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

        if (_settings.IsGammaTrunk)
        {
            // Gamma TCP trunk: no registration, just listen for inbound INVITEs
            _isRunning = true;
            IsRegistered = true;
            Log("🟢 Gamma TCP trunk mode — listening for inbound INVITEs (no registration)");
            Log($"➡ DDI: {_settings.EffectiveDdi}");
            Log($"➡ Gamma SBC: {_settings.Server}:{_settings.Port}");
            OnRegistered?.Invoke($"Gamma TCP: {_settings.EffectiveDdi}@{_settings.Server}");
            return;
        }

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

        // Reject any pending call
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

        // SIP trace logging
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

        // Transport channel based on settings
        switch (_settings.Transport.ToUpperInvariant())
        {
            case "TCP":
            case "TCP_GAMMA":
                // Fixed port 5060 for stable Contact header — critical for inbound INVITEs
                var tcpChannel = new SIPTCPChannel(IPAddress.Any, 5060);
                _transport.AddSIPChannel(tcpChannel);
                Log("📡 Using TCP transport on port 5060");
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
        var authUser = _settings.EffectiveAuthUser;

        // Resolve the registrar IP — use Domain if set, else Server hostname
        var resolvedHost = ResolveRegistrarIp();
        if (string.IsNullOrEmpty(resolvedHost))
        {
            Log("❌ Could not resolve registrar address — registration aborted.");
            return;
        }

        var registrarHostWithPort = _settings.Port == 5060
            ? resolvedHost
            : $"{resolvedHost}:{_settings.Port}";

        if (authUser != _settings.Username)
        {
            // Advanced registration: Auth ID differs from extension
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
                        _transport, _settings.Username, _settings.Password, registrarHostWithPort, 3600);
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

            // Hostname-preserving: use hostname in AOR, route via resolved IP
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
                expiry: 3600,
                customHeaders: null);

            Log($"➡ Using separate Auth ID: {authUser}, Registrar: {registrarHostWithPort} (routed via {resolvedHost})");
        }
        else
        {
            // Standard registration: extension and auth username are the same
            Log($"📡 Registering {_settings.Username}@{registrarHostWithPort}");
            _regAgent = new SIPRegistrationUserAgent(
                _transport, _settings.Username, _settings.Password, registrarHostWithPort, 3600);
        }

        WireRegistrationEvents();
    }

    /// <summary>
    /// Resolves the registrar IP address, preferring Domain over Server.
    /// Handles both raw IPs and hostnames (via DNS resolution).
    /// </summary>
    private string ResolveRegistrarIp()
    {
        // Try Domain field first
        var domain = _settings.Domain?.Trim();
        if (!string.IsNullOrEmpty(domain))
        {
            if (IPAddress.TryParse(domain, out _))
            {
                Log($"📡 Using Domain IP directly: {domain}");
                return domain;
            }

            // Domain is a hostname — resolve it
            var resolved = ResolveDns(domain);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;

            Log($"⚠️ Could not resolve Domain '{domain}', falling back to Server…");
        }

        // Fall back to Server field
        var server = _settings.Server.Trim();
        if (IPAddress.TryParse(server, out _))
        {
            Log($"📡 Using Server IP directly: {server}");
            return server;
        }

        // Server is a hostname — resolve it
        var resolvedServer = ResolveDns(server);
        if (!string.IsNullOrEmpty(resolvedServer))
            return resolvedServer;

        Log($"❌ Could not resolve Server '{server}' to an IPv4 address.");
        return "";
    }

    /// <summary>
    /// Resolves a hostname to its primary IPv4 address via DNS.
    /// </summary>
    private string ResolveDns(string hostname)
    {
        try
        {
            Log($"🔍 Resolving '{hostname}' via DNS…");
            var ipv4 = Dns.GetHostAddresses(hostname)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
            {
                Log($"✅ Resolved '{hostname}' → {ipv4}");
                return ipv4.ToString();
            }
            Log($"⚠️ No IPv4 address found for '{hostname}'");
        }
        catch (Exception ex)
        {
            Log($"⚠️ DNS resolution failed for '{hostname}': {ex.Message}");
        }
        return "";
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

        var ringing = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ringing, null);
        await _transport!.SendResponseAsync(ringing);

        if (OperatorMode)
        {
            // Hold call for manual answer
            var pendingId = Guid.NewGuid().ToString("N")[..8];
            lock (_pendingLock)
            {
                // Reject any existing pending call
                RejectPendingCallInternal();
                _pendingCall = new PendingCall { PendingId = pendingId, CallerId = caller, Request = req };
            }
            OnCallRinging?.Invoke(pendingId, caller);
            Log($"📲 [{pendingId}] Operator mode — waiting for manual answer");
            return;
        }

        // Auto-answer mode
        if (!_settings.AutoAnswer) return;
        await AnswerAndWireCallAsync(req, caller);
    }

    /// <summary>Answer the pending call in operator mode.</summary>
    public async Task<bool> AnswerOperatorCallAsync()
    {
        PendingCall? pending;
        lock (_pendingLock)
        {
            pending = _pendingCall;
            _pendingCall = null;
        }
        if (pending == null) { Log("⚠ No pending call to answer"); return false; }

        Log($"✅ Answering pending call from {pending.CallerId}…");
        await AnswerAndWireCallAsync(pending.Request, pending.CallerId, isOperator: true);
        return true;
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
        var transportParam = _settings.IsGammaTrunk ? ";transport=tcp" : "";
        var destUri = SIPURI.ParseSIPURI($"sip:{destination}@{serverAddr}{portPart}{transportParam}");

        if (_settings.IsGammaTrunk)
        {
            Log($"📡 Gamma outbound: DDI={_settings.EffectiveDdi}, dest={destination}");
        }

        var callAgent = new SIPUserAgent(_transport, null);
        var audioEncoder = new AudioEncoder();
        var audioSource = new AudioExtrasSource(audioEncoder, new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        audioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);

        var rtpSession = new VoIPMediaSession(new MediaEndPoints { AudioSource = audioSource });
        rtpSession.AcceptRtpFromAny = true;

        AudioCodecsEnum negotiatedCodec = AudioCodecsEnum.PCMA;
        rtpSession.OnAudioFormatsNegotiated += formats => negotiatedCodec = formats.FirstOrDefault().Codec;

        var sessionId = $"out-{Guid.NewGuid().ToString("N")[..8]}";

        Log($"📞 Dialling {destination} via {serverAddr}{portPart}{transportParam}…");
        var fromHeader = _settings.IsGammaTrunk ? $"<sip:{_settings.EffectiveDdi}@{serverAddr}>" : null;
        var result = await callAgent.Call(destUri.ToString(), fromHeader, null, rtpSession);
        if (!result)
        {
            Log($"❌ Outbound call to {destination} failed.");
            rtpSession.Close("call_failed");
            callAgent.Dispose();
            return;
        }

        ICallSession session;
        try { session = await _sessionManager.CreateSessionAsync(destination); }
        catch (Exception ex) { Log($"❌ AI init failed: {ex.Message}"); callAgent.Hangup(); rtpSession.Close("ai_fail"); return; }

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

        if (session.AiClient is OpenAiSdkClient sdk)
            sdk.GetQueuedFrames = () => playout.QueuedFrames;

        // Wire RTP receive — apply ingress gain
        rtpSession.OnRtpPacketReceived += (ep, mt, pkt) =>
        {
            if (mt != SDPMediaTypesEnum.audio) return;
            var payload = pkt.Payload;
            var alawData = negotiatedCodec == AudioCodecsEnum.PCMU
                ? G711Transcode.MuLawToALaw(payload) : payload;

            var ingressGain = (float)_audioSettings.IngressVolumeBoost;
            if (ingressGain > 1.01f)
            {
                var boosted = new byte[alawData.Length];
                Buffer.BlockCopy(alawData, 0, boosted, 0, alawData.Length);
                ALawVolumeBoost.ApplyInPlace(boosted, ingressGain);
                session.ProcessInboundAudio(boosted);
            }
            else
            {
                session.ProcessInboundAudio(alawData);
            }
            OnOperatorCallerAudio?.Invoke(alawData);
        };

        // Wire AI audio out — pre-slice to 160-byte frames to eliminate accumulator lock churn
        session.OnAudioOut += frame =>
        {
            const int frameSize = 160;
            int offset = 0, remaining = frame.Length;
            while (remaining >= frameSize)
            {
                var slice = new byte[frameSize];
                Buffer.BlockCopy(frame, offset, slice, 0, frameSize);
                playout.BufferALaw(slice);
                offset += frameSize;
                remaining -= frameSize;
            }
            if (remaining > 0)
            {
                var tail = new byte[remaining];
                Buffer.BlockCopy(frame, offset, tail, 0, remaining);
                playout.BufferALaw(tail);
            }
        };

        callAgent.OnCallHungup += async (dlg) =>
        {
            if (_activeCalls.TryRemove(sessionId, out var c))
                await CleanupCallAsync(c, "remote_hangup");
        };
        session.OnEnded += async (s, r) => await RemoveAndCleanupAsync(sessionId, r);

        playout.Start();
        OnCallStarted?.Invoke(sessionId, destination);
        Log($"✅ Outbound call connected: {destination}");
    }

    public void SendOperatorAudio(byte[] alawData)
    {
        foreach (var call in _activeCalls.Values)
        {
            try
            {
                call.Playout?.BufferALaw(alawData);
            }
            catch { }
        }
    }

    private async Task AnswerAndWireCallAsync(SIPRequest req, string caller, bool isOperator = false)
    {
        var callAgent = new SIPUserAgent(_transport, null);
        var audioEncoder = new AudioEncoder();
        var audioSource = new AudioExtrasSource(audioEncoder, new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        audioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA || fmt.Codec == AudioCodecsEnum.PCMU);

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
        catch (Exception ex) { Log($"❌ AI init failed: {ex.Message}"); callAgent.Hangup(); rtpSession.Close("ai_fail"); return; }

        var activeCall = new ActiveCall { SessionId = session.SessionId, Session = session, RtpSession = rtpSession, CallAgent = callAgent };

        if (!_activeCalls.TryAdd(session.SessionId, activeCall))
        {
            await session.EndAsync("duplicate"); callAgent.Hangup(); rtpSession.Close("dup"); return;
        }

        var playout = new ALawRtpPlayout(rtpSession);
        playout.OnLog += msg => Log($"[{session.SessionId}] {msg}");
        activeCall.Playout = playout;

        if (session.AiClient is OpenAiSdkClient sdk)
            sdk.GetQueuedFrames = () => playout.QueuedFrames;

        WireAudioPipeline(activeCall, negotiatedCodec, isOperator);
        playout.Start();

        callAgent.OnCallHungup += async (d) => await RemoveAndCleanupAsync(session.SessionId, "remote_hangup");
        session.OnEnded += async (s, r) => await RemoveAndCleanupAsync(s.SessionId, r);

        OnCallStarted?.Invoke(session.SessionId, caller);
        Log($"📞 Call {session.SessionId} active for {caller} (total: {_activeCalls.Count})");
    }

    private void WireAudioPipeline(ActiveCall call, AudioCodecsEnum negotiatedCodec, bool isOperator = false)
    {
        var session = call.Session;
        var playout = call.Playout!;
        var rtpSession = call.RtpSession;
        var sid = call.SessionId;

        int isBotSpeaking = 0, adaHasStartedSpeaking = 0, watchdogPending = 0;
        DateTime botStoppedSpeakingAt = DateTime.MinValue;
        var callStartedAt = DateTime.UtcNow;
        bool inboundFlushComplete = false;
        int inboundPacketCount = 0;

        // Pre-slice OpenAI bursts into exact 160-byte A-law frames before hitting the queue lock.
        // This eliminates accumulator churn on the hot path: each call to BufferALaw is a
        // zero-remainder 160-byte write, so DrainAccumulatorToQueue never loops mid-burst.
        session.OnAudioOut += frame =>
        {
            Interlocked.Exchange(ref isBotSpeaking, 1);
            Interlocked.Exchange(ref adaHasStartedSpeaking, 1);

            const int frameSize = 160;
            int offset = 0;
            int remaining = frame.Length;

            // Fast path: burst is an exact multiple of 160 — feed whole frames directly
            while (remaining >= frameSize)
            {
                var slice = new byte[frameSize];
                Buffer.BlockCopy(frame, offset, slice, 0, frameSize);
                playout.BufferALaw(slice);
                offset += frameSize;
                remaining -= frameSize;
            }

            // Tail: any leftover bytes go through the accumulator as before
            if (remaining > 0)
            {
                var tail = new byte[remaining];
                Buffer.BlockCopy(frame, offset, tail, 0, remaining);
                playout.BufferALaw(tail);
            }
        };

        session.OnBargeIn += () =>
        {
            if (Volatile.Read(ref isBotSpeaking) == 0 && playout.QueuedFrames == 0) return;
            playout.Clear();
            Interlocked.Exchange(ref isBotSpeaking, 0);
        };

        if (session.AiClient is OpenAiSdkClient sdkClient)
        {
            sdkClient.OnResponseCompleted += () =>
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                if (playout.QueuedFrames == 0) sdkClient.NotifyPlayoutComplete();
                else Interlocked.Exchange(ref watchdogPending, 1);
            };
        }

        playout.OnQueueEmpty += () =>
        {
            if (Volatile.Read(ref adaHasStartedSpeaking) == 1 && Volatile.Read(ref isBotSpeaking) == 1)
            {
                Interlocked.Exchange(ref isBotSpeaking, 0);
                botStoppedSpeakingAt = DateTime.UtcNow;
                session.NotifyPlayoutComplete();
            }
            else if (Volatile.Read(ref watchdogPending) == 1)
            {
                Interlocked.Exchange(ref watchdogPending, 0);
                session.NotifyPlayoutComplete();
            }
        };

        rtpSession.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            var payload = rtpPacket.Payload;
            if (payload == null || payload.Length == 0) return;

            if (!inboundFlushComplete) { inboundPacketCount++; if (inboundPacketCount < 20) return; inboundFlushComplete = true; }
            if ((DateTime.UtcNow - callStartedAt).TotalMilliseconds < 500) return;

            byte[] g711ToSend = negotiatedCodec == AudioCodecsEnum.PCMU ? G711Transcode.MuLawToALaw(payload) : payload;

            // In operator mode, forward caller audio to local speaker monitor
            if (isOperator)
                OnOperatorCallerAudio?.Invoke(g711ToSend);

            bool applySoftGate = Volatile.Read(ref isBotSpeaking) == 1;
            if (!applySoftGate && Volatile.Read(ref adaHasStartedSpeaking) == 1 && botStoppedSpeakingAt != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - botStoppedSpeakingAt).TotalMilliseconds < 300) applySoftGate = true;
            }

            if (applySoftGate)
            {
                double sumSq = 0;
                for (int i = 0; i < g711ToSend.Length; i++) { short pcm = ALawDecode(g711ToSend[i]); sumSq += (double)pcm * pcm; }
                float rms = (float)Math.Sqrt(sumSq / g711ToSend.Length);
                if (rms < (_audioSettings.BargeInRmsThreshold > 0 ? _audioSettings.BargeInRmsThreshold : 1200))
                {
                    g711ToSend = new byte[payload.Length];
                    Array.Fill(g711ToSend, (byte)0xD5);
                }
            }

            var ingressGain = (float)_audioSettings.IngressVolumeBoost;
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

        session.OnEnded += (s, reason) => { try { playout.Dispose(); } catch { } };
    }

    private async Task RemoveAndCleanupAsync(string sessionId, string reason)
    {
        if (_activeCalls.TryRemove(sessionId, out var call))
            await CleanupCallAsync(call, reason);
        OnCallEnded?.Invoke(sessionId, reason);
    }

    private async Task CleanupCallAsync(ActiveCall call, string reason)
    {
        if (Interlocked.Exchange(ref call.CleanupDone, 1) == 1) return;
        try { await call.Session.EndAsync(reason); } catch { }
        try { call.CallAgent.Hangup(); } catch { }
        try { call.RtpSession.Close(reason); } catch { }
        try { call.Playout?.Dispose(); } catch { }
        Log($"[{call.SessionId}] 📴 Cleaned up: {reason}");
    }

    // ── Helpers ──

    private static readonly short[] ALawDecodeTable = BuildALawTable();
    private static short[] BuildALawTable()
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int a = i ^ 0x55; int sign = (a & 0x80) != 0 ? -1 : 1;
            int segment = (a >> 4) & 0x07; int value = (a & 0x0F) << 4 | 0x08;
            if (segment > 0) value = (value + 0x100) << (segment - 1);
            table[i] = (short)(sign * value);
        }
        return table;
    }
    private static short ALawDecode(byte alaw) => ALawDecodeTable[alaw];


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

    private void Log(string msg)
    {
        // Only use OnLog — CallbackLoggerProvider already routes ILogger to UI
        OnLog?.Invoke(msg);
    }

    public async ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; await StopAsync(); } }

    private sealed class ActiveCall
    {
        public required string SessionId { get; init; }
        public required ICallSession Session { get; init; }
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
