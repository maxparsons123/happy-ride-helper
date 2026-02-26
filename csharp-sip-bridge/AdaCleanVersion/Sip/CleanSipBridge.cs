using System.Collections.Concurrent;
using System.Net;
using AdaCleanVersion.Audio;
using AdaCleanVersion.Config;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

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
    private readonly int _circuitBreakerThreshold;

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

    /// <summary>Fires with G.711 audio frames from the AI output (for Simli avatar feeding).</summary>
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
        _circuitBreakerThreshold = Math.Max(1, settings.Rtp.CircuitBreakerThreshold);
    }

    /// <summary>Internal: called by factory to proxy audio events from OpenAiRealtimeClient.</summary>
    internal void RaiseAudioOut(byte[] g711Frame) => OnAudioOut?.Invoke(g711Frame);

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
            try { call.UserAgent?.Hangup(false); }
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
        var uasTx = new UASInviteTransaction(_sipTransport, inviteRequest, null);
        var uas = new SIPServerUserAgent(_sipTransport, null, uasTx, null);

        // Create RTP session with configured G.711 codec
        var codec = G711Codec.Parse(_settings.Audio.PreferredCodec);
        var rtpSession = new RTPSession(false, false, false);
        var audioCodecEnum = codec == G711CodecType.PCMA ? AudioCodecsEnum.PCMA : AudioCodecsEnum.PCMU;
        var payloadId = G711Codec.PayloadType(codec);
        var audioFormat = new AudioFormat(audioCodecEnum, payloadId, 8000);
        var audioTrack = new MediaStreamTrack(audioFormat);
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
            RtpSession = rtpSession,
            Codec = codec
        };
        _activeCalls[callId] = activeCall;

        // Accept the call — sends 200 OK with SDP
        uas.Answer("application/sdp", rtpSession.CreateAnswer(null).ToString(), SIPDialogueTransferModesEnum.Default);

        // Wire RTP lifecycle events
        rtpSession.OnTimeout += () =>
        {
            Log($"⏱ RTP timeout for {callId} — treating as call end");
            if (_activeCalls.TryRemove(callId, out var timedOut))
            {
                timedOut.Session.EndCall();
                timedOut.RtpSession?.Close(null);
                OnCallEnded?.Invoke(callId);
            }
            try { uas.Hangup(false); } catch { }
        };

        // Wire BYE / hangup via RTP session close
        rtpSession.OnRtpClosed += (reason) =>
        {
            if (_activeCalls.TryRemove(callId, out var removed))
            {
                var duration = (DateTime.UtcNow - removed.StartTime).TotalSeconds;
                Log($"📴 Call ended: {removed.CallerId} ({duration:F0}s) — {reason}");
                removed.Session.EndCall();
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

        var server = _settings.Sip.Server;
        var domain = _settings.Sip.Domain ?? server;
        var username = _settings.Sip.Username;
        var authUser = _settings.Sip.EffectiveAuthUser;
        var password = _settings.Sip.Password;

        // Build the proper SIP URIs for registration
        // AOR (Address of Record): user@domain — this is who we ARE
        // Registrar: server — this is WHERE we register
        var aor = SIPURI.ParseSIPURI($"sip:{username}@{domain}");
        var registrarHost = server;

        Log($"📡 Registering trunk: {username}@{domain} → registrar={registrarHost}, authUser={authUser}");

        // Use the full constructor that separates registrar from AOR
        var outboundProxy = SIPEndPoint.ParseSIPEndPoint($"{registrarHost}:{_settings.Sip.Port}");

        _regAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            outboundProxy,        // proxy/registrar endpoint
            aor,                  // AOR (address of record)
            authUser,             // auth username (may differ from SIP username)
            password,
            domain,               // realm for digest auth
            120);                 // expiry seconds

        _regAgent.RegistrationSuccessful += (uri, resp) =>
            Log($"✅ Trunk registered: {uri} (status={resp?.StatusCode})");

        _regAgent.RegistrationTemporaryFailure += (uri, msg) =>
            Log($"⚠ Trunk registration temporary failure: {uri} — {msg}");

        _regAgent.RegistrationFailed += (uri, resp, err) =>
        {
            var statusCode = resp?.StatusCode.ToString() ?? "no-response";
            var reasonPhrase = resp?.ReasonPhrase ?? "unknown";
            var authHeader = resp?.Header?.AuthenticationHeader?.ToString() ?? "none";
            Log($"❌ Trunk registration FAILED: {uri}");
            Log($"   Status: {statusCode} ({reasonPhrase})");
            Log($"   Error: {err}");
            Log($"   Auth challenge: {authHeader}");
            Log($"   Config: user={username}, authUser={authUser}, domain={domain}, server={server}");
        };

        _regAgent.Start();
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
    public void SendAudio(string callId, byte[] g711Payload, uint duration)
    {
        if (!_activeCalls.TryGetValue(callId, out var call) || call.RtpSession == null)
            return;

        try
        {
            call.RtpSession.SendAudio(
                (uint)(duration * 8), // timestamp increment for 8kHz G.711
                g711Payload);

            // Reset on success
            call.ConsecutiveRtpFailures = 0;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref call.ConsecutiveRtpFailures);
            if (failures == 1 || failures % 5 == 0)
                Log($"⚠ RTP send failure #{failures} for {callId}: {ex.Message}");

            if (failures >= _circuitBreakerThreshold)
            {
                Log($"🔌 RTP circuit breaker tripped for {callId} after {failures} consecutive failures — ending call");
                if (_activeCalls.TryRemove(callId, out var tripped))
                {
                    tripped.Session.EndCall();
                    tripped.RtpSession?.Close(null);
                    OnCallEnded?.Invoke(callId);
                }
                try { call.UserAgent?.Hangup(false); } catch { }
            }
        }
    }

    /// <summary>
    /// Hang up a specific call by ID.
    /// </summary>
    public void HangupCall(string callId)
    {
        if (_activeCalls.TryGetValue(callId, out var call))
        {
            call.UserAgent?.Hangup(false);
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
    public G711CodecType Codec { get; init; } = G711CodecType.PCMU;
    public int PayloadType => G711Codec.PayloadType(Codec);

    /// <summary>Tracks consecutive RTP send failures for circuit breaker logic.</summary>
    public int ConsecutiveRtpFailures;
}
