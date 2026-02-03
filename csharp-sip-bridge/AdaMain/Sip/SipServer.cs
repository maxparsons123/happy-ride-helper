using System.Net;
using AdaMain.Config;
using AdaMain.Core;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace AdaMain.Sip;

/// <summary>
/// SIP server handling registration and incoming calls.
/// </summary>
public sealed class SipServer : IAsyncDisposable
{
    private readonly ILogger<SipServer> _logger;
    private readonly SipSettings _settings;
    private readonly SessionManager _sessionManager;
    private readonly Func<string, ICallSession> _createSession;
    
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _activeCall;
    private ICallSession? _currentSession;
    
    private readonly object _callLock = new();
    private bool _isRunning;
    
    public event Action<string>? OnRegistered;
    public event Action<string>? OnRegistrationFailed;
    public event Action<string>? OnCallStarted;
    public event Action<string>? OnCallEnded;
    
    public SipServer(
        ILogger<SipServer> logger,
        SipSettings settings,
        SessionManager sessionManager)
    {
        _logger = logger;
        _settings = settings;
        _sessionManager = sessionManager;
        _createSession = _ => throw new NotImplementedException();
    }
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;
        
        _transport = new SIPTransport();
        
        var protocol = SipConfigHelper.ParseTransport(_settings.Transport);
        var localPort = protocol == SIPProtocolsEnum.tls ? 5061 : 5060;
        
        _transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, localPort)));
        
        _transport.SIPTransportRequestReceived += OnRequestReceived;
        
        // Register with SIP server
        var serverUri = SIPURI.ParseSIPURI($"sip:{_settings.Server}:{_settings.Port}");
        var authId = _settings.AuthId ?? _settings.Username;
        
        _regAgent = new SIPRegistrationUserAgent(
            _transport,
            _settings.Username,
            _settings.Password,
            serverUri.ToString(),
            120); // Expiry seconds
        
        _regAgent.RegistrationSuccessful += (uri) =>
        {
            _logger.LogInformation("Registered: {Uri}", uri);
            OnRegistered?.Invoke(uri.ToString());
        };
        
        _regAgent.RegistrationFailed += (uri, response, message) =>
        {
            _logger.LogWarning("Registration failed: {Message}", message);
            OnRegistrationFailed?.Invoke(message ?? "Unknown error");
        };
        
        _regAgent.Start();
        _isRunning = true;
        
        _logger.LogInformation("SIP server started on port {Port}", localPort);
    }
    
    public async Task StopAsync()
    {
        if (!_isRunning) return;
        
        _regAgent?.Stop();
        _regAgent = null;
        
        if (_activeCall != null)
        {
            _activeCall.Hangup();
            _activeCall = null;
        }
        
        _transport?.Shutdown();
        _transport = null;
        
        _isRunning = false;
        _logger.LogInformation("SIP server stopped");
    }
    
    private async Task OnRequestReceived(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        if (request.Method != SIPMethodsEnum.INVITE)
            return;
        
        lock (_callLock)
        {
            if (_activeCall != null)
            {
                _logger.LogInformation("Rejecting call - busy");
                var busy = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, null);
                _ = _transport!.SendResponseAsync(busy);
                return;
            }
        }
        
        var callerId = CallerIdExtractor.Extract(request);
        _logger.LogInformation("Incoming call from {CallerId}", callerId);
        
        try
        {
            var ua = new SIPUserAgent(_transport, null);
            
            // Configure media
            var rtpSession = new VoIPMediaSession(new MediaEndPoints
            {
                AudioSource = new AudioExtrasSource()
            });
            
            ua.OnCallHungup += async (dialog) =>
            {
                _logger.LogInformation("Call hung up");
                await CleanupCallAsync("hangup");
            };
            
            var result = await ua.Answer(request, rtpSession);
            
            if (!result)
            {
                _logger.LogWarning("Failed to answer call");
                return;
            }
            
            lock (_callLock)
            {
                _activeCall = ua;
            }
            
            // Create AI session
            _currentSession = await _sessionManager.CreateSessionAsync(callerId);
            _currentSession.OnEnded += async (s, reason) => await CleanupCallAsync(reason);
            
            // Wire up audio
            rtpSession.OnRtpPacketReceived += (ep, mt, pkt) =>
            {
                if (mt == SDPMediaTypesEnum.audio)
                {
                    _currentSession?.ProcessInboundAudio(pkt.Payload);
                }
            };
            
            // Start playout task
            _ = Task.Run(async () =>
            {
                while (_activeCall != null && _currentSession?.IsActive == true)
                {
                    var frame = _currentSession.GetOutboundFrame();
                    if (frame != null)
                    {
                        rtpSession.SendAudio((uint)frame.Length * 10, frame);
                    }
                    await Task.Delay(20);
                }
            });
            
            OnCallStarted?.Invoke(callerId);
            _logger.LogInformation("Call answered");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling call");
            await CleanupCallAsync("error");
        }
    }
    
    private async Task CleanupCallAsync(string reason)
    {
        SIPUserAgent? ua;
        ICallSession? session;
        
        lock (_callLock)
        {
            ua = _activeCall;
            session = _currentSession;
            _activeCall = null;
            _currentSession = null;
        }
        
        if (session != null)
        {
            await session.EndAsync(reason);
        }
        
        if (ua != null)
        {
            try { ua.Hangup(); } catch { }
        }
        
        OnCallEnded?.Invoke(reason);
        _logger.LogInformation("Call cleaned up: {Reason}", reason);
    }
    
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
