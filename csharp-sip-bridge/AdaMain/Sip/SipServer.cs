using System.Net;
using System.Net.Sockets;
using AdaMain.Config;
using AdaMain.Core;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

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
    private readonly SessionManager _sessionManager;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;
    private ICallSession? _currentSession;
    private IPAddress? _localIp;
    private IPAddress? _publicIp;

    private readonly object _callLock = new();
    private volatile bool _isRunning;
    private volatile bool _disposed;

    public bool IsRegistered { get; private set; }
    public bool IsConnected => _transport != null && IsRegistered;

    public event Action<string>? OnLog;
    public event Action<string>? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnServerResolved;

    public SipServer(
        ILogger<SipServer> logger,
        SipSettings settings,
        SessionManager sessionManager)
    {
        _logger = logger;
        _settings = settings;
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

    private async void OnIncomingCallAsync(SIPUserAgent ua, SIPRequest req)
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
                // Store the UA/request for manual answer later
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
        // Configure media session
        var rtpSession = new SIPSorcery.Media.VoIPMediaSession();

        var serverUa = ua.AcceptCall(req);
        var result = await ua.Answer(serverUa, rtpSession);

        if (!result)
        {
            Log("❌ Failed to answer call");
            return;
        }

        Log("✅ Call answered");

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
        }

        // Wire up audio: SIP → AI
        rtpSession.OnRtpPacketReceived += (ep, mt, pkt) =>
        {
            if (mt == SIPSorcery.Net.SDPMediaTypesEnum.audio)
            {
                session.ProcessInboundAudio(pkt.Payload);
            }
        };

        // Playout task: AI → SIP (20ms frames)
        _ = Task.Run(async () =>
        {
            while (_currentSession == session && session.IsActive)
            {
                var frame = session.GetOutboundFrame();
                if (frame != null)
                {
                    rtpSession.SendAudio((uint)frame.Length * 10, frame);
                }
                await Task.Delay(20);
            }
        });

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
        }

        if (session != null)
        {
            await session.EndAsync(reason);
        }

        OnCallEnded?.Invoke(reason);
        Log($"📴 Call cleaned up: {reason}");
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

    private void Log(string msg)
    {
        if (_disposed) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        _logger.LogDebug("{Msg}", line);
        OnLog?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
