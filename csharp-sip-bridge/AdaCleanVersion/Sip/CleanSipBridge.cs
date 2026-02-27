using System.Collections.Concurrent;
using System.Net;
using AdaCleanVersion.Audio;
using AdaCleanVersion.Config;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorcery.Media;
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
    private readonly IcabbiBookingService? _icabbiService;
    private readonly EdgeBurstDispatcher? _burstDispatcher;
    private readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();

    private SIPTransport? _sipTransport;
    private SIPUserAgent? _listenerAgent;
    private SIPRegistrationUserAgent? _regAgent;
    private bool _started;

    public event Action<string>? OnLog;

    /// <summary>
    /// Fired when a new call is connected and RTP session is ready.
    /// The consumer should wire this to their OpenAI Realtime client
    /// for bidirectional audio streaming.
    /// </summary>
    public event Action<string, VoIPMediaSession, CleanCallSession>? OnCallConnected;

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

        // Wire iCabbi if enabled and keys are present
        if (settings.Icabbi.Enabled && !string.IsNullOrWhiteSpace(settings.Icabbi.AppKey))
        {
            var icabbiLogger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug))
                .CreateLogger<IcabbiBookingService>();
            _icabbiService = new IcabbiBookingService(icabbiLogger, settings.Icabbi, settings.Supabase);
            Log("🚕 iCabbi dispatch integration enabled");
        }

        // Wire EdgeBurstDispatcher for single-round-trip split+geocode
        if (!string.IsNullOrWhiteSpace(settings.Supabase.Url) && !string.IsNullOrWhiteSpace(settings.Supabase.AnonKey))
        {
            var burstLogger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug))
                .CreateLogger<EdgeBurstDispatcher>();
            _burstDispatcher = new EdgeBurstDispatcher(
                settings.Supabase.Url,
                settings.Supabase.AnonKey,
                burstLogger);
            Log("🔥 EdgeBurstDispatcher enabled (burst-dispatch edge function)");
        }
    }

    /// <summary>Internal: called by factory to proxy audio events from OpenAiRealtimeClient.</summary>
    internal void RaiseAudioOut(byte[] g711Frame) => OnAudioOut?.Invoke(g711Frame);

    /// <summary>Internal: called by factory to proxy barge-in events from OpenAiRealtimeClient.</summary>
    internal void RaiseBargeIn() => OnBargeIn?.Invoke();

    // ─── Lifecycle ──────────────────────────────────────────

    public bool Start()
    {
        if (_started)
        {
            Log("⚠ CleanSipBridge.Start() called again — ignoring duplicate");
            return true;
        }
        try
        {
            _started = true;
            _sipTransport = new SIPTransport();

            // Resolve local IP (same approach as AdaSdkModel)
            var localIp = GetLocalIp();
            Log($"➡ Local IP: {localIp}");

            var protocol = _settings.Sip.Transport.ToUpperInvariant() switch
            {
                "TCP" => SIPProtocolsEnum.tcp,
                "TLS" => SIPProtocolsEnum.tls,
                _ => SIPProtocolsEnum.udp
            };

            // Bind to local IP with ephemeral port (port 0) — matches AdaSdkModel pattern
            switch (protocol)
            {
                case SIPProtocolsEnum.tcp:
                    _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(localIp, 0)));
                    break;
                default:
                    _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(localIp, 0)));
                    break;
            }

            // Wire incoming INVITE handler via SIPUserAgent
            _listenerAgent = new SIPUserAgent(_sipTransport, null);
            _listenerAgent.OnIncomingCall += (ua, req) =>
            {
                _ = OnIncomingCallSafe(req);
            };

            // Register with SIP trunk if configured
            RegisterTrunk();

            Log($"✅ CleanSipBridge started on {protocol} (local={localIp})");
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
            try { call.CallAgent?.Hangup(); }
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

    // ─── SIP Call Handler ────────────────────────────────────

    private async Task OnIncomingCallSafe(SIPRequest sipRequest)
    {
        var callerId = sipRequest.Header.From.FromURI.User;
        var callerName = sipRequest.Header.From.FromName ?? callerId;
        var dialedNo = sipRequest.Header.To.ToURI.User;

        Log($"📞 Incoming call from {callerId} ({callerName}) → {dialedNo}");

        try
        {
            // Send 180 Ringing first
            var ringing = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ringing, null);
            await _sipTransport!.SendResponseAsync(ringing);
            Log("🔔 Sent 180 Ringing");

            await HandleIncomingCallAsync(sipRequest, callerId, callerName);
        }
        catch (Exception ex)
        {
            Log($"⚠ Call setup failed: {ex.Message}");
        }
    }

    // ─── Call Setup ─────────────────────────────────────────

    private async Task HandleIncomingCallAsync(
        SIPRequest inviteRequest,
        string callerId,
        string callerName)
    {
        // Create a per-call SIPUserAgent (same pattern as AdaSdkModel)
        var callAgent = new SIPUserAgent(_sipTransport, null);

        // Create VoIPMediaSession with audio source
        var audioEncoder = new AudioEncoder();
        var audioSource = new AudioExtrasSource(
            audioEncoder,
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

        audioSource.RestrictFormats(fmt => fmt.Codec == AudioCodecsEnum.PCMA);

        var mediaEndPoints = new MediaEndPoints { AudioSource = audioSource };
        var rtpSession = new VoIPMediaSession(mediaEndPoints);
        rtpSession.AcceptRtpFromAny = true;

        // Track negotiated codec
        var codec = G711CodecType.PCMA;
        var callId = inviteRequest.Header.CallId;
        rtpSession.OnAudioFormatsNegotiated += formats =>
        {
            var fmt = formats.FirstOrDefault();
            var negotiated = fmt.Codec == AudioCodecsEnum.PCMU ? G711CodecType.PCMU : G711CodecType.PCMA;
            codec = negotiated;

            if (_activeCalls.TryGetValue(callId, out var call))
                call.Codec = negotiated;

            Log($"🎵 Negotiated SIP codec: {fmt.Codec} (PT{fmt.FormatID}, mapped={negotiated})");
        };

        // Accept and answer the call
        var serverUa = callAgent.AcceptCall(inviteRequest);

        // Short delay to let SIP signaling settle
        await Task.Delay(200);

        var answered = await callAgent.Answer(serverUa, rtpSession);
        if (!answered)
        {
            Log("❌ Failed to answer call — callAgent.Answer returned false");
            return;
        }

        await rtpSession.Start();

        // Stop the AudioExtrasSource so it doesn't interfere with G711RtpPlayout's 20ms send loop.
        // We only needed it for SDP/codec negotiation — G711RtpPlayout owns all outbound audio now.
        try { audioSource.CloseAudio().Wait(500); } catch { }

        Log("✅ Call answered and RTP started");

        // Look up returning caller (Supabase callers table)
        var context = await _callerLookup.LookupAsync(callerId);

        // Enrich with iCabbi customer data if available
        if (_icabbiService != null)
        {
            try
            {
                var icabbiCustomer = await _icabbiService.GetCustomerByPhoneAsync(callerId);
                if (icabbiCustomer != null)
                {
                    Log($"🚕 iCabbi customer found: {icabbiCustomer.Name} (ID: {icabbiCustomer.CustomerId}, bookings: {icabbiCustomer.BookingCount})");

                    // Merge iCabbi data into caller context — Supabase data takes priority for name/addresses
                    context = new CallerContext
                    {
                        CallerPhone = context.CallerPhone,
                        IsReturningCaller = context.IsReturningCaller || true,
                        CallerName = context.CallerName ?? icabbiCustomer.Name,
                        PreferredLanguage = context.PreferredLanguage,
                        ServiceArea = context.ServiceArea,
                        LastPickup = context.LastPickup,
                        LastDestination = context.LastDestination,
                        TotalBookings = Math.Max(context.TotalBookings, icabbiCustomer.BookingCount),
                        LastBookingAt = context.LastBookingAt,
                        AddressAliases = context.AddressAliases,
                        IcabbiCustomerId = icabbiCustomer.CustomerId,
                        IcabbiDefaultAddress = icabbiCustomer.Address
                    };
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ iCabbi customer lookup failed (non-fatal): {ex.Message}");
            }
        }

        if (context.IsReturningCaller)
        {
            Log($"👤 Returning caller: {context.CallerName} ({context.TotalBookings} bookings, " +
                $"last pickup: {context.LastPickup ?? "none"})");
            if (!string.IsNullOrEmpty(context.IcabbiCustomerId))
                Log($"🚕 iCabbi customer ID: {context.IcabbiCustomerId}");
        }
        else
        {
            Log($"👤 New caller: {callerId}");
        }

        // Warm-up burst-dispatch edge function (fire-and-forget to avoid cold start)
        if (_burstDispatcher != null)
            _ = _burstDispatcher.WarmUpAsync();

        // Check for active bookings for this returning caller
        ActiveBookingInfo? activeBooking = null;
        if (context.IsReturningCaller)
        {
            try
            {
                activeBooking = await LoadActiveBookingAsync(callerId);
                if (activeBooking != null)
                    Log($"📋 Active booking found for {callerId}: {activeBooking.BookingId} ({activeBooking.Pickup} → {activeBooking.Destination})");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Active booking lookup failed (non-fatal): {ex.Message}");
            }
        }

        // Create clean session
        // callId already extracted above for codec negotiation handler
        var session = new CleanCallSession(
            sessionId: callId,
            callerId: callerId,
            companyName: _settings.Taxi.CompanyName,
            extractionService: _extractionService,
            fareService: _fareService,
            callerContext: context,
            burstDispatcher: _burstDispatcher,
            icabbiService: _icabbiService,
            activeBooking: activeBooking
        );

        session.OnLog += msg => Log(msg);

        session.OnAiInstruction += (instruction, isReprompt, isSilent) =>
        {
            Log(isReprompt ? $"🔒 REPROMPT: {instruction}" : $"📋 Instruction: {instruction}");
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

        var activeCall = new ActiveCall
        {
            CallId = callId,
            Session = session,
            CallerId = callerId,
            StartTime = DateTime.UtcNow,
            CallAgent = callAgent,
            RtpSession = rtpSession,
            Codec = codec
        };
        _activeCalls[callId] = activeCall;

        // Wire RTP lifecycle events
        rtpSession.OnTimeout += (mediaType) =>
        {
            Log($"⏱ RTP timeout for {callId} (media={mediaType}) — treating as call end");
            if (_activeCalls.TryRemove(callId, out var timedOut))
            {
                timedOut.Session.EndCall(force: true);
                timedOut.RtpSession?.Close(null);
                OnCallEnded?.Invoke(callId);
            }
            try { callAgent.Hangup(); } catch { }
        };

        rtpSession.OnRtpClosed += (reason) =>
        {
            if (_activeCalls.TryRemove(callId, out var removed))
            {
                var duration = (DateTime.UtcNow - removed.StartTime).TotalSeconds;
                Log($"📴 Call ended: {removed.CallerId} ({duration:F0}s) — {reason}");
                removed.Session.EndCall(force: true);
                OnCallEnded?.Invoke(callId);
            }
        };

        // Start the deterministic booking engine
        session.Start();

        // Notify consumer so they can wire OpenAI Realtime ↔ RTP audio
        Log("✅ Call connected — engine starting");
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

        // Match AdaSdkModel registration target behavior: register to server[:port]
        var resolvedHost = server;
        try
        {
            resolvedHost = Dns.GetHostAddresses(server)
                .First(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToString();
            Log($"➡ Registrar DNS: {server} -> {resolvedHost}");
        }
        catch
        {
            Log($"⚠ Registrar DNS resolve failed for {server}; using hostname directly");
        }

        var registrarHostWithPort = _settings.Sip.Port == 5060
            ? resolvedHost
            : $"{resolvedHost}:{_settings.Sip.Port}";

        // Use SIP username for AOR identity (same as AdaSdkModel).
        _regAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            username,
            _settings.Sip.Password,
            registrarHostWithPort,
            120); // expiry seconds

        _regAgent.RegistrationSuccessful += (uri, resp) =>
            Log($"✅ Trunk registered: {uri} (status={resp?.StatusCode})");

        _regAgent.RegistrationTemporaryFailure += (uri, resp, err) =>
        {
            var statusCode = resp?.StatusCode.ToString() ?? "no-response";
            var reasonPhrase = resp?.ReasonPhrase ?? "unknown";
            Log($"⚠ Trunk registration temporary failure: {uri}");
            Log($"   Status: {statusCode} ({reasonPhrase})");
            Log($"   Error: {err}");
        };

        _regAgent.RegistrationFailed += (uri, resp, err) =>
        {
            var statusCode = resp?.StatusCode.ToString() ?? "no-response";
            var reasonPhrase = resp?.ReasonPhrase ?? "unknown";
            Log($"❌ Trunk registration FAILED: {uri}");
            Log($"   Status: {statusCode} ({reasonPhrase})");
            Log($"   Error: {err}");
            Log($"   Config: user={username}, authUser={authUser}, domain={domain}, server={server}, port={_settings.Sip.Port}");
        };

        _regAgent.Start();
        Log($"📡 Registering trunk: {username}@{domain} via {server}:{_settings.Sip.Port} (authUser={authUser})");
    }

    // ─── Helpers ────────────────────────────────────────────

    private void Log(string msg)
    {
        // Fire-and-forget to avoid blocking SIP/RTP threads on logger I/O
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _logger.LogInformation(msg); } catch { }
        });
        OnLog?.Invoke(msg);
    }

    public int ActiveCallCount => _activeCalls.Count;

    private static IPAddress GetLocalIp()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return IPAddress.Loopback;
        }
    }

    /// <summary>
    /// Send RTP audio to a specific call (for OpenAI TTS output).
    /// </summary>
    public void SendAudio(string callId, byte[] g711Payload)
    {
        if (!_activeCalls.TryGetValue(callId, out var call) || call.RawRtpSession == null)
            return;

        try
        {
            // G.711 @ 8kHz: timestamp increment == number of samples == number of bytes
            call.RawRtpSession.SendAudio((uint)g711Payload.Length, g711Payload);

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
                    tripped.Session.EndCall(force: true);
                    tripped.RtpSession?.Close(null);
                    OnCallEnded?.Invoke(callId);
                }
                try { call.CallAgent?.Hangup(); } catch { }
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
            call.CallAgent?.Hangup();
        }
    }

    /// <summary>
    /// Check the bookings table for any active/confirmed booking for this caller.
    /// Mirrors AdaSdkModel's LoadActiveBookingAsync.
    /// </summary>
    private async Task<ActiveBookingInfo?> LoadActiveBookingAsync(string phone)
    {
        try
        {
            var normalized = phone.Trim().Replace(" ", "");
            var phoneVariants = new[] { phone, normalized, $"+{normalized}" };
            var orFilter = string.Join(",", phoneVariants.Select(p => $"caller_phone.eq.{Uri.EscapeDataString(p)}"));
            var url = $"{_settings.Supabase.Url}/rest/v1/bookings?or=({orFilter})&status=in.(active,confirmed)&order=booked_at.desc&limit=1&select=id,pickup,destination,passengers,fare,eta,status,caller_name,scheduled_for,booking_details";

            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _settings.Supabase.AnonKey);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) return null;
            var b = arr[0];

            string? icabbiJourneyId = null;
            if (b.TryGetProperty("booking_details", out var bd) && bd.ValueKind == System.Text.Json.JsonValueKind.Object
                && bd.TryGetProperty("icabbi_journey_id", out var jid) && jid.ValueKind == System.Text.Json.JsonValueKind.String)
                icabbiJourneyId = jid.GetString();

            return new ActiveBookingInfo
            {
                BookingId = b.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Pickup = b.TryGetProperty("pickup", out var pu) && pu.ValueKind == System.Text.Json.JsonValueKind.String ? pu.GetString() : null,
                Destination = b.TryGetProperty("destination", out var de) && de.ValueKind == System.Text.Json.JsonValueKind.String ? de.GetString() : null,
                Passengers = b.TryGetProperty("passengers", out var px) && px.ValueKind == System.Text.Json.JsonValueKind.Number ? px.GetInt32() : null,
                Fare = b.TryGetProperty("fare", out var fa) && fa.ValueKind == System.Text.Json.JsonValueKind.String ? fa.GetString() : null,
                Eta = b.TryGetProperty("eta", out var et) && et.ValueKind == System.Text.Json.JsonValueKind.String ? et.GetString() : null,
                Status = b.TryGetProperty("status", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String ? st.GetString() : null,
                ScheduledFor = b.TryGetProperty("scheduled_for", out var sf) && sf.ValueKind == System.Text.Json.JsonValueKind.String ? sf.GetString() : null,
                CallerName = b.TryGetProperty("caller_name", out var cn) && cn.ValueKind == System.Text.Json.JsonValueKind.String ? cn.GetString() : null,
                IcabbiJourneyId = icabbiJourneyId
            };
        }
        catch (Exception ex)
        {
            Log($"⚠️ Active booking lookup failed: {ex.Message}");
            return null;
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
    public SIPUserAgent? CallAgent { get; init; }
    public VoIPMediaSession? RtpSession { get; init; }

    /// <summary>VoIPMediaSession IS an RTPSession (inheritance) — direct access for raw RTP.</summary>
    public RTPSession? RawRtpSession => RtpSession;

    public G711CodecType Codec { get; set; } = G711CodecType.PCMA;
    public int PayloadType => G711Codec.PayloadType(Codec);

    /// <summary>Tracks consecutive RTP send failures for circuit breaker logic.</summary>
    public int ConsecutiveRtpFailures;
}
