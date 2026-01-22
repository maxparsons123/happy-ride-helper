using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace TaxiSipBridge.Services;

public class SipBridgeService
{
    private readonly SipAdaBridgeConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SipBridgeService> _logger;
    private readonly ConcurrentDictionary<string, CallSession> _activeCalls = new();
    private SIPTransport? _sipTransport;
    private SIPUserAgent? _userAgent;

    public SipBridgeService(SipAdaBridgeConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SipBridgeService>();
    }

    public int ActiveCallCount => _activeCalls.Count;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SIP Bridge on {Server}:{Port}", _config.SipServer, _config.SipPort);

        _sipTransport = new SIPTransport();
        
        // Use configured transport type
        if (_config.Transport == SipTransportType.TCP)
        {
            _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, _config.SipPort)));
            _logger.LogInformation("Using TCP transport");
        }
        else
        {
            _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, _config.SipPort)));
            _logger.LogInformation("Using UDP transport");
        }

        _sipTransport.SIPTransportRequestReceived += OnSIPRequestReceived;

        _logger.LogInformation("SIP Transport initialized and listening");
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping SIP Bridge...");

        // End all active calls
        foreach (var call in _activeCalls.Values)
        {
            await call.EndAsync();
        }
        _activeCalls.Clear();

        _sipTransport?.Shutdown();
        _logger.LogInformation("SIP Bridge stopped");
    }

    private async Task OnSIPRequestReceived(
        SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        try
        {
            switch (sipRequest.Method)
            {
                case SIPMethodsEnum.INVITE:
                    await HandleInvite(localSIPEndPoint, remoteEndPoint, sipRequest);
                    break;

                case SIPMethodsEnum.BYE:
                    await HandleBye(sipRequest);
                    break;

                case SIPMethodsEnum.CANCEL:
                    await HandleCancel(sipRequest);
                    break;

                case SIPMethodsEnum.OPTIONS:
                    await HandleOptions(localSIPEndPoint, remoteEndPoint, sipRequest);
                    break;

                case SIPMethodsEnum.ACK:
                    // ACK is handled automatically by the user agent
                    break;

                default:
                    _logger.LogDebug("Unhandled SIP method: {Method}", sipRequest.Method);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SIP request {Method}", sipRequest.Method);
        }
    }

    private async Task HandleInvite(
        SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        var callId = sipRequest.Header.CallId;
        var callerNumber = sipRequest.Header.From.FromURI.User;
        var calledNumber = sipRequest.Header.To.ToURI.User;

        _logger.LogInformation(
            "Incoming call {CallId} from {Caller} to {Called}",
            callId, callerNumber, calledNumber);

        // Check concurrent call limit
        if (_activeCalls.Count >= _config.MaxConcurrentCalls)
        {
            _logger.LogWarning("Max concurrent calls reached, rejecting call {CallId}", callId);
            var busyResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.BusyHere, null);
            await _sipTransport!.SendResponseAsync(busyResponse);
            return;
        }

        // Create new call session
        var session = new CallSession(
            callId,
            callerNumber,
            calledNumber,
            _config,
            _sipTransport!,
            localSIPEndPoint,
            remoteEndPoint,
            sipRequest,
            _loggerFactory.CreateLogger<CallSession>());

        if (_activeCalls.TryAdd(callId, session))
        {
            session.OnCallEnded += (sender, args) =>
            {
                _activeCalls.TryRemove(callId, out _);
                _logger.LogInformation("Call {CallId} removed. Active calls: {Count}", callId, _activeCalls.Count);
            };

            await session.StartAsync();
            _logger.LogInformation("Active calls: {Count}", _activeCalls.Count);
        }
        else
        {
            _logger.LogWarning("Failed to add call {CallId} to active calls", callId);
        }
    }

    private async Task HandleBye(SIPRequest sipRequest)
    {
        var callId = sipRequest.Header.CallId;
        _logger.LogInformation("BYE received for call {CallId}", callId);

        if (_activeCalls.TryRemove(callId, out var session))
        {
            await session.EndAsync();
        }

        // Send OK response
        var okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        await _sipTransport!.SendResponseAsync(okResponse);
    }

    private async Task HandleCancel(SIPRequest sipRequest)
    {
        var callId = sipRequest.Header.CallId;
        _logger.LogInformation("CANCEL received for call {CallId}", callId);

        if (_activeCalls.TryRemove(callId, out var session))
        {
            await session.EndAsync();
        }

        var okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        await _sipTransport!.SendResponseAsync(okResponse);
    }

    private async Task HandleOptions(
        SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        var okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        await _sipTransport!.SendResponseAsync(okResponse);
    }
}
