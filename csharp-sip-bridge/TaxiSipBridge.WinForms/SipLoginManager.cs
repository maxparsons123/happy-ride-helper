using System.Net;
using System.Net.Sockets;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace TaxiSipBridge;

/// <summary>
/// Unified SIP login/registration manager.
/// Handles SIP transport setup, DNS resolution, and registration.
/// Delegates incoming calls to the configured call handler.
/// </summary>
public class SipLoginManager : IDisposable
{
    private readonly SipLoginConfig _config;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regUserAgent;
    private SIPUserAgent? _userAgent;
    private string? _resolvedHost;
    private IPAddress? _localIp;
    private volatile bool _disposed;

    // Call handler - receives incoming calls after registration
    private ISipCallHandler? _callHandler;

    #region Events

    public event Action<string>? OnLog;
    public event Action? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string, string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    public event Action<string>? OnTranscript;

    #endregion

    #region Properties

    public bool IsRegistered { get; private set; }
    public bool IsConnected => _sipTransport != null && IsRegistered;
    public SIPTransport? Transport => _sipTransport;
    public SIPUserAgent? UserAgent => _userAgent;

    #endregion

    public SipLoginManager(SipLoginConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Set the call handler that will process incoming calls.
    /// </summary>
    public void SetCallHandler(ISipCallHandler handler)
    {
        _callHandler = handler;

        // Wire up events from handler
        _callHandler.OnLog += msg => OnLog?.Invoke(msg);
        _callHandler.OnCallStarted += (id, caller) => OnCallStarted?.Invoke(id, caller);
        _callHandler.OnCallEnded += id => OnCallEnded?.Invoke(id);
        _callHandler.OnTranscript += t => OnTranscript?.Invoke(t);
    }

    /// <summary>
    /// Start SIP transport and registration.
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SipLoginManager));

        Log("🚀 SipLoginManager starting...");
        Log($"➡ SIP Server: {_config.SipServer}:{_config.SipPort} ({_config.Transport})");
        Log($"➡ User: {_config.SipUser}");

        _localIp = GetLocalIp();
        Log($"➡ Local IP: {_localIp}");

        // Resolve DNS once and cache it
        _resolvedHost = ResolveDns(_config.SipServer);

        InitializeSipTransport();
        InitializeRegistration();
        InitializeUserAgent();

        _regUserAgent!.Start();
        Log("🟢 Waiting for SIP registration...");
    }

    /// <summary>
    /// Stop SIP transport and unregister.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;

        Log("🛑 SipLoginManager stopping...");

        try { _callHandler?.Dispose(); } catch { }

        if (_regUserAgent != null)
        {
            _regUserAgent.RegistrationSuccessful -= OnRegistrationSuccess;
            _regUserAgent.RegistrationFailed -= OnRegistrationFailure;
            try { _regUserAgent.Stop(); } catch { }
        }

        if (_userAgent != null)
        {
            _userAgent.OnIncomingCall -= OnIncomingCallAsync;
        }

        try { _sipTransport?.Shutdown(); } catch { }

        IsRegistered = false;
        Log("🛑 SipLoginManager stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        GC.SuppressFinalize(this);
    }

    #region Initialization

    private void InitializeSipTransport()
    {
        _sipTransport = new SIPTransport();

        switch (_config.Transport)
        {
            case SipTransportType.UDP:
                _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(_localIp!, 0)));
                Log("📡 Using UDP transport");
                break;
            case SipTransportType.TCP:
                _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(_localIp!, 0)));
                Log("📡 Using TCP transport");
                break;
        }
    }

    private void InitializeRegistration()
    {
        _regUserAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            _config.SipUser,
            _config.SipPassword,
            _resolvedHost!,
            120);

        _regUserAgent.RegistrationSuccessful += OnRegistrationSuccess;
        _regUserAgent.RegistrationFailed += OnRegistrationFailure;
    }

    private void InitializeUserAgent()
    {
        _userAgent = new SIPUserAgent(_sipTransport, null);
        _userAgent.OnIncomingCall += OnIncomingCallAsync;
    }

    private string ResolveDns(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
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
            socket.Connect(_config.SipServer, _config.SipPort);
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

    #endregion

    #region Registration Events

    private void OnRegistrationSuccess(SIPURI uri, SIPResponse resp)
    {
        Log($"✅ SIP Registered as {_config.SipUser}@{_config.SipServer}");
        IsRegistered = true;
        OnRegistered?.Invoke();
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
        var caller = SipCallerId.Extract(req);
        Log($"📞 Incoming call from {caller}");

        if (_callHandler == null)
        {
            Log("❌ No call handler configured - rejecting call");
            var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, null);
            await _sipTransport!.SendResponseAsync(busyResponse);
            return;
        }

        // Pass transport so handler can send SIP responses (Ringing, etc.)
        await _callHandler.HandleIncomingCallAsync(_sipTransport!, ua, req, caller);
    }

    #endregion

    private void Log(string msg)
    {
        if (_disposed) return;
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }
}
