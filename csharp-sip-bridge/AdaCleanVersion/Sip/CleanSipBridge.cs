using System.Collections.Concurrent;
using System.Net;
using AdaCleanVersion.Config;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;

namespace AdaCleanVersion.Sip;

/// <summary>
/// SIP bridge using SIPSorcery for SIP signaling and RTP transport.
/// 
/// Architecture:
/// - Incoming INVITE → creates CleanCallSession
/// - Caller transcript → feeds ProcessCallerResponseAsync (raw slot storage)
/// - OnAiInstruction → sends instruction to OpenAI Realtime as system message
/// - No AI tools registered — AI is voice-only
/// - Single extraction pass when all slots collected
///
/// NOTE: OpenAI Realtime audio integration is handled externally.
///       This bridge manages SIP lifecycle + maps events to CleanCallSession.
///       You must wire your OpenAiSdkClient to send/receive RTP audio.
/// </summary>
public class CleanSipBridge : IDisposable
{
    private readonly ILogger _logger;
    private readonly CleanAppSettings _settings;
    private readonly IExtractionService _extractionService;
    private readonly FareGeocodingService _fareService;
    private readonly CallerLookupService _callerLookup;
    private readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();

    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regAgent;

    public event Action<string>? OnLog;

    /// <summary>
    /// Fired when a new call is connected and RTP session is ready.
    /// The consumer should wire this to their OpenAI Realtime client
    /// for bidirectional audio streaming.
    /// </summary>
    public event Action<string, RTPSession, CleanCallSession>? OnCallConnected;

    /// <summary>Fires with µ-law audio frames from the AI output (for Simli avatar feeding).</summary>
    public event Action<byte[]>? OnAudioOut;

    /// <summary>Fires on barge-in (caller speech started).</summary>
    public event Action? OnBargeIn;

    /// <summary>Fires when a call ends (BYE received or RTP timeout). Carries the callId.</summary>
    public event Action<string>? OnCallEnded;

    public CleanSipBridge(
        ILogger logger,
        CleanAppSettings settings,
        IExtractionService extractionService,
        FareGeocodingService fareService,
        CallerLookupService callerLookup)
    {
        _logger = logger;
        _settings = settings;
        _extractionService = extractionService;
        _fareService = fareService;
        _callerLookup = callerLookup;
    }

    /// <summary>Internal: called by factory to proxy audio events from OpenAiRealtimeClient.</summary>
    internal void RaiseAudioOut(byte[] mulawFrame) => OnAudioOut?.Invoke(mulawFrame);

    /// <summary>Internal: called by factory to proxy barge-in events from OpenAiRealtimeClient.</summary>
    internal void RaiseBargeIn() => OnBargeIn?.Invoke();

    // ─── Lifecycle ──────────────────────────────────────────

    public bool Start()
    {
        try
        {
            _sipTransport = new SIPTransport();

            // Listen on configured port
            var listenPort = _settings.Sip.ListenPort;
            var protocol = _settings.Sip.Transport.ToUpperInvariant() switch
            {
                "TCP" => SIPProtocolsEnum.tcp,
                "TLS" => SIPProtocolsEnum.tls,
                _ => SIPProtocolsEnum.udp
            };

            _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, listenPort)));

            if (protocol == SIPProtocolsEnum.tcp)
                _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, listenPort)));

            // Wire incoming INVITE handler
            _sipTransport.SIPTransportRequestReceived += OnSipRequestReceived;

            // Register with SIP trunk if configured
            RegisterTrunk();

            Log($"✅ CleanSipBridge started on {protocol} :{listenPort}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to start SIP bridge: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        _regAgent?.Stop();

        foreach (var call in _activeCalls.Values)
        {
            try { call.UserAgent?.Hangup(); }
            catch { /* best effort */ }
        }
        _activeCalls.Clear();

        _sipTransport?.Shutdown();
        _sipTransport = null;

        Log("🛑 CleanSipBridge stopped");
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // ─── SIP Request Handler ────────────────────────────────

    private async Task OnSipRequestReceived(
        SIPEndPoint localSipEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        if (sipRequest.Method != SIPMethodsEnum.INVITE)
        {
            // Auto-respond to non-INVITE (OPTIONS, etc.)
            if (sipRequest.Method == SIPMethodsEnum.OPTIONS)
            {
                var optResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                await _sipTransport!.SendResponseAsync(optResp);
            }
            return;
        }

        var callerId = sipRequest.Header.From.FromURI.User;
        var callerName = sipRequest.Header.From.FromName ?? callerId;
        var dialedNo = sipRequest.Header.To.ToURI.User;

        Log($"📞 Incoming INVITE: {callerId} ({callerName}) → {dialedNo}");

        try
        {
            await HandleIncomingCallAsync(sipRequest, localSipEndPoint, remoteEndPoint, callerId, callerName);
        }
        catch (Exception ex)
        {
            Log($"⚠ Call setup failed: {ex.Message}");
        }
    }

    // ─── Call Setup ─────────────────────────────────────────

    private async Task HandleIncomingCallAsync(
        SIPRequest inviteRequest,
        SIPEndPoint localEp,
        SIPEndPoint remoteEp,
        string callerId,
        string callerName)
    {
        // Create SIPSorcery user agent for this call
        var uas = new SIPServerUserAgent(_sipTransport, null, inviteRequest, null, null);

        // Create RTP session with G.711 µ-law (PCMU)
        var rtpSession = new RTPSession(false, false, false);
        var audioFormat = new SDPAudioVideoMediaFormat(
            new AudioFormat(AudioCodecsEnum.PCMU, 0, 8000));
        var audioTrack = new MediaStreamTrack(audioFormat.ToSdpFormat());
        rtpSession.addTrack(audioTrack);

        // Look up returning caller
        var context = await _callerLookup.LookupAsync(callerId);

        if (context.IsReturningCaller)
        {
            Log($"👤 Returning caller: {context.CallerName} ({context.TotalBookings} bookings, " +
                $"last pickup: {context.LastPickup ?? "none"})");
        }
        else
        {
            Log($"👤 New caller: {callerId}");
        }

        // Create clean session
        var session = new CleanCallSession(
            sessionId: inviteRequest.Header.CallId,
            callerId: callerId,
            companyName: _settings.Taxi.CompanyName,
            extractionService: _extractionService,
            fareService: _fareService,
            callerContext: context
        );

        session.OnLog += msg => Log(msg);

        session.OnAiInstruction += instruction =>
        {
            Log($"📋 Instruction: {instruction}");
            // Consumer wires this to OpenAI Realtime session.update
        };

        session.OnBookingReady += booking =>
        {
            Log($"🚕 BOOKING: {booking.CallerName} | " +
                $"{booking.Pickup.DisplayName} → {booking.Destination.DisplayName} | " +
                $"{booking.Passengers} pax | {booking.PickupTime}");
        };

        session.OnFareReady += fare =>
        {
            Log($"💰 FARE: {fare.Fare} ({fare.DistanceMiles:F1}mi) | " +
                $"ETA: {fare.DriverEta} | Zone: {fare.ZoneName ?? "none"}");
        };

        var callId = inviteRequest.Header.CallId;
        var activeCall = new ActiveCall
        {
            CallId = callId,
            Session = session,
            CallerId = callerId,
            StartTime = DateTime.UtcNow,
            UserAgent = uas,
            RtpSession = rtpSession
        };
        _activeCalls[callId] = activeCall;

        // Accept the call — sends 200 OK with SDP
        uas.Answer(SIPResponseStatusCodesEnum.Ok, null, null, rtpSession.CreateAnswer(null));

        // Wire RTP lifecycle events
        rtpSession.OnTimeout += (mediaType) =>
        {
            Log($"⏱ RTP timeout for {callId}");
            uas.Hangup();
        };

        // Wire BYE / hangup
        uas.OnCallHungup += (dialogue) =>
        {
            if (_activeCalls.TryRemove(callId, out var removed))
            {
                var duration = (DateTime.UtcNow - removed.StartTime).TotalSeconds;
                Log($"📴 Call ended: {removed.CallerId} ({duration:F0}s)");
                removed.Session.EndCall();
                removed.RtpSession?.Close(null);
                OnCallEnded?.Invoke(callId);
            }
        };

        Log("✅ Call connected — engine starting");

        // Start the deterministic booking engine
        session.Start();

        // Notify consumer so they can wire OpenAI Realtime ↔ RTP audio
        OnCallConnected?.Invoke(callId, rtpSession, session);
    }

    // ─── SIP Trunk Registration ─────────────────────────────

    private void RegisterTrunk()
    {
        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) ||
            _settings.Sip.Server == "sip.example.com")
            return;

        var domain = _settings.Sip.Domain ?? _settings.Sip.Server;
        var regUri = SIPURI.ParseSIPURI($"sip:{_settings.Sip.Username}@{domain}");

        _regAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            _settings.Sip.EffectiveAuthUser,
            _settings.Sip.Password,
            domain,
            120); // expiry seconds

        _regAgent.RegistrationSuccessful += (uri, resp) =>
            Log($"✅ Trunk registered: {uri}");

        _regAgent.RegistrationFailed += (uri, resp, err) =>
            Log($"❌ Trunk registration failed: {uri} — {err}");

        _regAgent.Start();

        Log($"📡 Registering trunk: {_settings.Sip.Username}@{domain}");
    }

    // ─── Helpers ────────────────────────────────────────────

    private void Log(string msg)
    {
        _logger.LogInformation(msg);
        OnLog?.Invoke(msg);
    }

    public int ActiveCallCount => _activeCalls.Count;

    /// <summary>
    /// Send RTP audio to a specific call (for OpenAI TTS output).
    /// </summary>
    public void SendAudio(string callId, byte[] pcmuPayload, uint duration)
    {
        if (_activeCalls.TryGetValue(callId, out var call) && call.RtpSession != null)
        {
            call.RtpSession.SendAudioFrame(
                (uint)(duration * 8), // timestamp increment for 8kHz PCMU
                (int)SDPWellKnownMediaFormatsEnum.PCMU,
                pcmuPayload);
        }
    }

    /// <summary>
    /// Hang up a specific call by ID.
    /// </summary>
    public void HangupCall(string callId)
    {
        if (_activeCalls.TryGetValue(callId, out var call))
        {
            call.UserAgent?.Hangup();
        }
    }
}

/// <summary>
/// Tracks a live call — maps SIPSorcery call to CleanCallSession.
/// </summary>
internal class ActiveCall
{
    public required string CallId { get; init; }
    public required CleanCallSession Session { get; init; }
    public string CallerId { get; init; } = "";
    public DateTime StartTime { get; init; }
    public SIPServerUserAgent? UserAgent { get; init; }
    public RTPSession? RtpSession { get; init; }
}
