using Zaffiqbal247RadioCars.Ai;
using Zaffiqbal247RadioCars.Config;
using Zaffiqbal247RadioCars.Services;
using Microsoft.Extensions.Logging;

namespace Zaffiqbal247RadioCars.Core;

/// <summary>
/// Manages a single call session lifecycle with G.711 A-law passthrough.
/// Simplified flow: sync stores data, book_taxi does quote/confirm.
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly ILogger<CallSession> _logger;
    private readonly AppSettings _settings;
    private readonly IOpenAiClient _aiClient;

    public IOpenAiClient AiClient => _aiClient;
    private readonly IFareCalculator _fareCalculator;
    private readonly IDispatcher _dispatcher;
    private readonly IcabbiBookingService? _icabbi;
    private readonly bool _icabbiEnabled;

    private readonly BookingState _booking = new();
    private readonly Audio.ALawThinningFilter? _thinningFilter;

    private int _disposed;
    private int _active;
    private int _bookTaxiCompleted;
    private long _lastAdaFinishedAt;

    // Audio diagnostics
    private long _inboundFrames;
    private long _outboundFrames;

    public string SessionId { get; }
    public string CallerId { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public bool IsActive => Volatile.Read(ref _active) == 1;

    public event Action<ICallSession, string>? OnEnded;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action<string, string>? OnTranscript;
    public event Action<byte[]>? OnAudioOut;
    public event Action? OnBargeIn;

    public CallSession(
        string sessionId,
        string callerId,
        ILogger<CallSession> logger,
        AppSettings settings,
        IOpenAiClient aiClient,
        IFareCalculator fareCalculator,
        IDispatcher dispatcher,
        IcabbiBookingService? icabbi = null,
        bool icabbiEnabled = false)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _logger = logger;
        _settings = settings;
        _aiClient = aiClient;
        _fareCalculator = fareCalculator;
        _dispatcher = dispatcher;
        _icabbi = icabbi;
        _icabbiEnabled = icabbiEnabled;

        _booking.CallerPhone = callerId;

        // Init thinning filter if configured
        if (settings.Audio.ThinningAlpha > 0.01f)
            _thinningFilter = new Audio.ALawThinningFilter(settings.Audio.ThinningAlpha);

        // Wire up AI client events
        _aiClient.OnAudio += HandleAiAudio;
        _aiClient.OnToolCall += HandleToolCallAsync;
        _aiClient.OnEnded += reason => _ = EndAsync(reason);
        _aiClient.OnTranscript += (role, text) => OnTranscript?.Invoke(role, text);

        if (_aiClient is OpenAiSdkClient sdkClient)
        {
            sdkClient.OnBargeIn += () => OnBargeIn?.Invoke();
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _active, 1) == 1)
            return;

        _logger.LogInformation("[{SessionId}] Starting G.711 session for {CallerId}", SessionId, CallerId);

        // Step 1: Load caller history BEFORE connecting so Ada knows the caller's name
        string? callerHistory = null;
        try
        {
            callerHistory = await LoadCallerHistoryAsync(CallerId);
            if (callerHistory != null)
                _logger.LogInformation("[{SessionId}] 📋 Caller history loaded for {CallerId}", SessionId, CallerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Caller history lookup failed (non-fatal)", SessionId);
        }

        // Step 2: Connect to OpenAI (session configured, event loops started, but NO greeting yet)
        await _aiClient.ConnectAsync(CallerId, ct);

        // Step 3: Inject caller history BEFORE greeting so Ada knows the caller's name
        if (!string.IsNullOrEmpty(callerHistory))
        {
            try
            {
                await _aiClient.InjectSystemMessageAsync(callerHistory);
                _logger.LogInformation("[{SessionId}] 📋 Caller history injected for {CallerId}", SessionId, CallerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{SessionId}] Caller history injection failed (non-fatal)", SessionId);
            }
        }

        // Step 4: NOW send the greeting — pass caller name directly so it's in the greeting instruction
        await _aiClient.SendGreetingAsync(_booking.Name);
    }

    private async Task<string?> LoadCallerHistoryAsync(string phone)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var normalized = phone.Trim().Replace(" ", "");
            var phoneVariants = new[] { phone, normalized, $"+{normalized}" };
            var orFilter = string.Join(",", phoneVariants.Select(p => $"phone_number.eq.{Uri.EscapeDataString(p)}"));
            var url = $"{_settings.Supabase.Url}/rest/v1/callers?or=({orFilter})&select=name,pickup_addresses,dropoff_addresses,last_pickup,last_destination,total_bookings";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _settings.Supabase.AnonKey);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) return null;
            var caller = arr[0];

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[CALLER HISTORY] This is a returning caller. Use this context to speed up the booking:");

            if (caller.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(nameEl.GetString()))
            {
                sb.AppendLine($"  Known name: {nameEl.GetString()}");
                // Pre-fill booking name
                if (string.IsNullOrEmpty(_booking.CallerName))
                    _booking.CallerName = nameEl.GetString();
            }

            if (caller.TryGetProperty("total_bookings", out var tb))
                sb.AppendLine($"  Total previous bookings: {tb.GetInt32()}");

            if (caller.TryGetProperty("last_pickup", out var lp) && lp.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(lp.GetString()))
                sb.AppendLine($"  Last pickup: {lp.GetString()}");

            if (caller.TryGetProperty("last_destination", out var ld) && ld.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(ld.GetString()))
                sb.AppendLine($"  Last destination: {ld.GetString()}");

            var allAddresses = new HashSet<string>();
            if (caller.TryGetProperty("pickup_addresses", out var pickups) && pickups.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var a in pickups.EnumerateArray())
                    if (a.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(a.GetString()))
                        allAddresses.Add(a.GetString()!);

            if (caller.TryGetProperty("dropoff_addresses", out var dropoffs) && dropoffs.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var a in dropoffs.EnumerateArray())
                    if (a.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(a.GetString()))
                        allAddresses.Add(a.GetString()!);

            if (allAddresses.Count > 0)
            {
                sb.AppendLine($"  All known addresses ({allAddresses.Count}):");
                var i = 1;
                foreach (var addr in allAddresses.Take(15))
                    sb.AppendLine($"    {i++}. {addr}");
            }

            sb.AppendLine("  INSTRUCTIONS: If the caller gives a partial address (e.g. 'same place', 'David Road', 'the usual'), try to match it to one of these history addresses. If you're confident (>80% match), use it directly without asking for disambiguation.");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Caller history lookup failed", SessionId);
            return null;
        }
    }

    public async Task EndAsync(string reason)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            return;

        _logger.LogInformation("[{SessionId}] Ending session: {Reason} | Audio stats: inbound={In} frames, outbound={Out} frames",
            SessionId, reason, Interlocked.Read(ref _inboundFrames), Interlocked.Read(ref _outboundFrames));
        await _aiClient.DisconnectAsync();
        OnEnded?.Invoke(this, reason);
    }

    public void ProcessInboundAudio(byte[] alawRtp)
    {
        if (!IsActive || alawRtp.Length == 0) return;
        Interlocked.Increment(ref _inboundFrames);
        _aiClient.SendAudio(alawRtp);
    }

    public byte[]? GetOutboundFrame() => null;

    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_aiClient is OpenAiSdkClient sdkClient)
            sdkClient.NotifyPlayoutComplete();
    }

    private void HandleAiAudio(byte[] alawFrame)
    {
        Interlocked.Increment(ref _outboundFrames);
        // 1. Volume boost
        var gain = (float)_settings.Audio.VolumeBoost;
        if (gain > 1.01f || gain < 0.99f)
            Audio.ALawVolumeBoost.ApplyInPlace(alawFrame, gain);

        // 2. High-pass thinning filter (removes bass mud, crisper telephony voice)
        _thinningFilter?.ApplyInPlace(alawFrame);

        OnAudioOut?.Invoke(alawFrame);
    }

    private async Task<object> HandleToolCallAsync(string name, Dictionary<string, object?> args)
    {
        _logger.LogDebug("[{SessionId}] Tool call: {Name} (args: {ArgCount})", SessionId, name, args.Count);

        return name switch
        {
            "sync_booking_data" => HandleSyncBookingData(args),
            "clarify_address" => HandleClarifyAddress(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
            "create_booking" => await HandleCreateBookingAsync(args),
            "find_local_events" => HandleFindLocalEvents(args),
            "end_call" => HandleEndCall(args),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }

    // =========================
    // SYNC BOOKING DATA
    // =========================
    private static readonly HashSet<string> _rejectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "caller", "anonymous", "user", "customer", "guest", "n/a", "na", "none", ""
    };

    private int _fareAutoTriggered;
    private bool _pickupDisambiguated = true;  // true = no disambiguation needed (set to false when triggered)
    private bool _destDisambiguated = true;    // true = no disambiguation needed (set to false when triggered)
    private string[]? _pendingDestAlternatives;
    private string? _pendingDestClarificationMessage;

    // Fare sanity guard: track retries so legitimate long-distance trips aren't blocked forever
    private int _fareSanityAlertCount;
    private string? _lastSanityAlertDestination;
    private volatile bool _fareSanityActive; // blocks book_taxi while sanity alert is shown

    private object HandleSyncBookingData(Dictionary<string, object?> args)
    {
        // Name validation guard — reject placeholder names
        if (args.TryGetValue("caller_name", out var n))
        {
            var nameVal = n?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(nameVal) && !_rejectedNames.Contains(nameVal))
                _booking.Name = nameVal;
            else if (_rejectedNames.Contains(nameVal ?? ""))
                _logger.LogWarning("[{SessionId}] ⛔ Rejected placeholder name: '{Name}'", SessionId, nameVal);
        }

        // ── HYBRID TRANSCRIPT MISMATCH DETECTION ──
        // Trust Ada's audio interpretation as PRIMARY source of truth.
        // Only flag mismatches when Whisper produces valid, intelligible English text
        // that significantly differs from Ada's interpretation.
        // Skip entirely when Whisper produces garbage (non-Latin, very short, etc.)
        string? mismatchWarning = null;
        if (_aiClient is OpenAiSdkClient sdkTranscript && !string.IsNullOrWhiteSpace(sdkTranscript.LastUserTranscript))
        {
            var sttText = sdkTranscript.LastUserTranscript;
            
            // Only run mismatch detection if Whisper produced intelligible English text
            if (IsIntelligibleEnglish(sttText))
            {
                // Check pickup mismatch
                if (args.TryGetValue("pickup", out var pickupArg) && !string.IsNullOrWhiteSpace(pickupArg?.ToString()))
                {
                    var pickupVal = pickupArg.ToString()!;
                    if (IsSignificantlyDifferent(pickupVal, sttText) && !string.IsNullOrWhiteSpace(_booking.Pickup) 
                        && IsSignificantlyDifferent(pickupVal, _booking.Pickup))
                    {
                        _logger.LogWarning("[{SessionId}] ⚠️ PICKUP MISMATCH: STT='{Stt}' vs Ada='{Ada}'", SessionId, sttText, pickupVal);
                        mismatchWarning = $"SOFT WARNING: The backup transcription heard '{sttText}' but you interpreted the pickup as '{pickupVal}'. " +
                            "You may optionally confirm with the caller if you're unsure, but trust your own interpretation as primary.";
                    }
                }
                
                // Check destination mismatch
                if (args.TryGetValue("destination", out var destArg) && !string.IsNullOrWhiteSpace(destArg?.ToString()))
                {
                    var destVal = destArg.ToString()!;
                    if (IsSignificantlyDifferent(destVal, sttText) && !string.IsNullOrWhiteSpace(_booking.Destination)
                        && IsSignificantlyDifferent(destVal, _booking.Destination))
                    {
                        _logger.LogWarning("[{SessionId}] ⚠️ DEST MISMATCH: STT='{Stt}' vs Ada='{Ada}'", SessionId, sttText, destVal);
                        mismatchWarning = $"SOFT WARNING: The backup transcription heard '{sttText}' but you interpreted the destination as '{destVal}'. " +
                            "You may optionally confirm with the caller if you're unsure, but trust your own interpretation as primary.";
                    }
                }
            }
            else
            {
                _logger.LogDebug("[{SessionId}] 🔇 Whisper STT not intelligible English — skipping mismatch check: '{Stt}'", SessionId, sttText);
            }
        }

        // Get last transcript for street name guard
        var lastTranscriptForGuard = (_aiClient is OpenAiSdkClient sdkGuard ? sdkGuard.LastUserTranscript : null) ?? "";

        if (args.TryGetValue("pickup", out var p))
        {
            var incoming = NormalizeHouseNumber(p?.ToString(), "pickup");
            incoming = ApplyTranscriptStreetGuard(incoming, lastTranscriptForGuard, "pickup");
            if (StreetNameChanged(_booking.Pickup, incoming))
            {
                _booking.PickupLat = _booking.PickupLon = null;
                _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
                _booking.Fare = null;
                _booking.Eta = null;
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                Interlocked.Exchange(ref _bookTaxiCompleted, 0);
            }
            _booking.Pickup = incoming;
        }
        if (args.TryGetValue("destination", out var d))
        {
            var incoming = NormalizeHouseNumber(d?.ToString(), "destination");
            incoming = ApplyTranscriptStreetGuard(incoming, lastTranscriptForGuard, "destination");
            if (StreetNameChanged(_booking.Destination, incoming))
            {
                _booking.DestLat = _booking.DestLon = null;
                _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                _booking.Fare = null;
                _booking.Eta = null;
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                Interlocked.Exchange(ref _bookTaxiCompleted, 0);
            }
            _booking.Destination = incoming;
        }
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn))
        {
            // ── PASSENGER HALLUCINATION GUARD ──
            var lastTranscript = (_aiClient is OpenAiSdkClient sdkPax ? sdkPax.LastUserTranscript : null) ?? "";
            bool transcriptHasNumber = System.Text.RegularExpressions.Regex.IsMatch(
                lastTranscript,
                @"\b(\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (transcriptHasNumber)
            {
                _booking.Passengers = pn;
                if (!args.ContainsKey("vehicle_type"))
                    _booking.VehicleType = BookingState.RecommendVehicle(pn);
            }
            else if (!_booking.Passengers.HasValue && pn > 0)
            {
                _booking.Passengers = pn;
                if (!args.ContainsKey("vehicle_type"))
                    _booking.VehicleType = BookingState.RecommendVehicle(pn);
            }
            else
            {
                _logger.LogWarning("[{SessionId}] 🛡️ PAX GUARD: Rejected passengers={Pax} — transcript '{Transcript}' has no number",
                    SessionId, pn, lastTranscript);
            }
        }
        if (args.TryGetValue("pickup_time", out var pt))
            _booking.PickupTime = pt?.ToString();
        if (args.TryGetValue("vehicle_type", out var vt) && !string.IsNullOrWhiteSpace(vt?.ToString()))
            _booking.VehicleType = vt.ToString()!;

        _logger.LogInformation("[{SessionId}] ⚡ Sync: Name={Name}, Pickup={Pickup}, Dest={Dest}, Pax={Pax}, Vehicle={Vehicle}",
            SessionId, _booking.Name ?? "?", _booking.Pickup ?? "?", _booking.Destination ?? "?", _booking.Passengers, _booking.VehicleType);

        OnBookingUpdated?.Invoke(_booking.Clone());

        // If transcript mismatch was detected, return warning to Ada
        if (mismatchWarning != null)
            return new { success = true, warning = mismatchWarning };

        // If name is still missing, tell Ada to ask for it
        if (string.IsNullOrWhiteSpace(_booking.Name))
            return new { success = true, warning = "Name is required before booking. Ask the caller for their name." };

        // AUTO-TRIGGER: When all 5 fields are filled, automatically calculate fare
        // This matches the v3.9 prompt: "When sync_booking_data is called with all 5 fields filled,
        // the system will AUTOMATICALLY validate the addresses and calculate the fare."
        bool allFieldsFilled = !string.IsNullOrWhiteSpace(_booking.Name)
            && !string.IsNullOrWhiteSpace(_booking.Pickup)
            && !string.IsNullOrWhiteSpace(_booking.Destination)
            && _booking.Passengers > 0
            && !string.IsNullOrWhiteSpace(_booking.PickupTime);

        if (allFieldsFilled && Interlocked.CompareExchange(ref _fareAutoTriggered, 1, 0) == 0)
        {
            _logger.LogInformation("[{SessionId}] 🚀 All fields filled — auto-triggering fare calculation", SessionId);

            var pickup = _booking.Pickup!;
            var destination = _booking.Destination!;
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId,
                        spokenPickupNumber: GetSpokenHouseNumber(pickup),
                        spokenDestNumber: GetSpokenHouseNumber(destination));
                    var completed = await Task.WhenAny(aiTask, Task.Delay(10000));

                    FareResult result;
                    if (completed == aiTask)
                    {
                        result = await aiTask;
                        _logger.LogInformation("[{SessionId}] 📊 Fare result: NeedsClarification={Clarif}, Fare={Fare}, Eta={Eta}, PickupAlts={PAlts}, DestAlts={DAlts}",
                            sessionId, result.NeedsClarification, result.Fare, result.Eta,
                            result.PickupAlternatives != null ? string.Join("|", result.PickupAlternatives) : "none",
                            result.DestAlternatives != null ? string.Join("|", result.DestAlternatives) : "none");
                        if (result.NeedsClarification)
                        {
                            var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                            var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                            // ADDRESS LOCK: Pickup first, stash dest for later
                            if (pickupAlts.Length > 0)
                            {
                                if (destAlts.Length > 0)
                                {
                                    _pendingDestAlternatives = destAlts;
                                    _pendingDestClarificationMessage = result.ClarificationMessage;
                                    _destDisambiguated = false;
                                }

                                _pickupDisambiguated = false;
                                _activePickupAlternatives = pickupAlts;
                                _logger.LogInformation("[{SessionId}] 🔒 Address Lock: PICKUP disambiguation needed: {Alts}", sessionId, string.Join("|", pickupAlts));
                                // Switch to semantic VAD for disambiguation (caller choosing from options)
                                if (_aiClient is OpenAiSdkClient sdkVad1)
                                    await sdkVad1.SetVadModeAsync(useSemantic: true, eagerness: 0.5f);

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=pickup, " +
                                        $"options=[{string.Join(", ", pickupAlts)}]. " +
                                        "Ask the caller ONLY about the PICKUP location. Do NOT mention the destination. " +
                                        "Present the options with numbers, then STOP and WAIT for their answer. " +
                                        "IMPORTANT: If the caller provides a completely DIFFERENT address instead of choosing from the list, " +
                                        "call clarify_address with that new address as 'selected'. " +
                                        "When they choose, call clarify_address(target=\"pickup\", selected=\"[their choice]\").");
                            }
                            else if (destAlts.Length > 0)
                            {
                                _destDisambiguated = false;
                                _activeDestAlternatives = destAlts;
                                _logger.LogInformation("[{SessionId}] 🔒 Address Lock: DESTINATION disambiguation needed: {Alts}", sessionId, string.Join("|", destAlts));

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=destination, " +
                                        $"options=[{string.Join(", ", destAlts)}]. " +
                                        "Pickup is confirmed. Now ask the caller ONLY about the DESTINATION. " +
                                        "Present the options with numbers, then STOP and WAIT for their answer. " +
                                        "IMPORTANT: If the caller provides a completely DIFFERENT address instead of choosing from the list, " +
                                        "call clarify_address with that new address as 'selected'. " +
                                        "When they choose, call clarify_address(target=\"destination\", selected=\"[their choice]\").");
                            }
                            else
                            {
                                if (_fareSanityAlertCount > 0)
                                {
                                    _logger.LogInformation("[{SessionId}] ⚡ Post-sanity re-calc: skipping disambiguation, using Nominatim fallback", sessionId);
                                    result = await _fareCalculator.CalculateAsync(pickup, destination, callerId);
                                }
                                else
                                {
                                    _logger.LogWarning("[{SessionId}] ⚠️ NeedsClarification=true but no alternatives — asking caller for city/area", sessionId);

                                    if (_aiClient is OpenAiSdkClient sdkAskArea)
                                    {
                                        var clarMsg = !string.IsNullOrWhiteSpace(result.ClarificationMessage)
                                            ? result.ClarificationMessage
                                            : "I couldn't pinpoint those addresses. Could you tell me which city or area they're in?";

                                        await sdkAskArea.InjectMessageAndRespondAsync(
                                            $"[ADDRESS CLARIFICATION NEEDED] The addresses could not be verified. " +
                                            $"Ask the caller: \"{clarMsg}\" " +
                                            "Once they provide the city or area, call sync_booking_data again with the updated addresses including the city.");
                                    }

                                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                                    return;
                                }
                            }

                            // Has disambiguation alternatives — already handled above, exit
                            Interlocked.Exchange(ref _fareAutoTriggered, 0);
                            return;
                        }

                        // Check if there are pending destination alternatives from a previous round
                        // (pickup was ambiguous, now resolved, but dest still needs clarification)
                        if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0 && !_destDisambiguated)
                        {
                            _activeDestAlternatives = _pendingDestAlternatives;
                            var destAltsList = string.Join(", ", _pendingDestAlternatives);
                            _logger.LogInformation("[{SessionId}] 🔄 Now resolving pending destination disambiguation: {Alts}", sessionId, destAltsList);

                            if (_aiClient is OpenAiSdkClient sdkDestClarif)
                                await sdkDestClarif.InjectMessageAndRespondAsync(
                                    $"[DESTINATION DISAMBIGUATION] Good, the pickup is confirmed. Now the DESTINATION address is ambiguous. The options are: {destAltsList}. " +
                                    "Ask the caller ONLY about the DESTINATION location. " +
                                    "Present the destination options clearly, then STOP and WAIT for their answer. " +
                                    "IMPORTANT: If the caller provides a completely DIFFERENT address instead of choosing from the list, " +
                                    "call clarify_address with that new address as 'selected'.");

                            _pendingDestAlternatives = null;
                            _pendingDestClarificationMessage = null;
                            Interlocked.Exchange(ref _fareAutoTriggered, 0);
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[{SessionId}] ⚠️ Edge function timed out, using fallback", sessionId);
                        result = await _fareCalculator.CalculateAsync(pickup, destination, callerId);
                    }

                    // Clear any pending disambiguation state
                    _pendingDestAlternatives = null;
                    _pendingDestClarificationMessage = null;
                    _pickupDisambiguated = true;
                    _destDisambiguated = true;

                    // FARE SANITY CHECK: If fare is absurdly high, the destination is likely wrong (STT error)
                    if (!IsFareSane(result))
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED — asking user to verify destination", sessionId);
                        Interlocked.Exchange(ref _fareAutoTriggered, 0);

                        if (_aiClient is OpenAiSdkClient sdkSanity)
                            await sdkSanity.InjectMessageAndRespondAsync(
                                "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard or the city could not be determined. " +
                                "Ask the caller to confirm their DESTINATION address AND which city or area they are in. " +
                                "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going, and which city you're in?\" " +
                                "When they respond, call sync_booking_data with the destination INCLUDING the city name (e.g. '7 Russell Street, Coventry').");
                        return;
                    }

                    // Address discrepancy check
                    var discrepancy = DetectAddressDiscrepancy(result);
                    if (discrepancy != null)
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Address discrepancy detected: {Msg}", sessionId, discrepancy);
                        Interlocked.Exchange(ref _fareAutoTriggered, 0);
                        if (_aiClient is OpenAiSdkClient sdkDisc)
                            await sdkDisc.InjectMessageAndRespondAsync(
                                $"[ADDRESS DISCREPANCY] {discrepancy} " +
                                "Ask the caller to confirm or repeat their address. " +
                                "When they respond, call sync_booking_data with the corrected address.");
                        return;
                    }

                    ApplyFareResult(result);

            if (_aiClient is OpenAiSdkClient sdk)
            {
                sdk.SetAwaitingConfirmation(true);
                // Switch to server VAD for fast yes/no confirmation response
                await sdk.SetVadModeAsync(useSemantic: false);
                _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (fare presented, awaiting yes/no)", sessionId);
            }

            OnBookingUpdated?.Invoke(_booking.Clone());

            var spokenFare = FormatFareForSpeech(_booking.Fare);
            _logger.LogInformation("[{SessionId}] 💰 Auto fare ready: {Fare} ({Spoken}), ETA: {Eta}",
                        sessionId, _booking.Fare, spokenFare, _booking.Eta);

                    var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
                    var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

                    if (_aiClient is OpenAiSdkClient sdkInject)
                        await sdkInject.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] The fare from {pickupAddr} to {destAddr} is {spokenFare}, " +
                            $"estimated time of arrival is {_booking.Eta}. " +
                            $"Read back the VERIFIED addresses and fare to the caller and ask them to confirm the booking.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Auto fare calculation failed", sessionId);
                    _booking.Fare = "£8.00";
                    _booking.Eta = "8 minutes";
                    OnBookingUpdated?.Invoke(_booking.Clone());

                    if (_aiClient is OpenAiSdkClient sdkFallback)
                        await sdkFallback.InjectMessageAndRespondAsync(
                            "[FARE RESULT] The estimated fare is 8 pounds, estimated time of arrival is 8 minutes. " +
                            "Read back the details to the caller and ask them to confirm.");
                }
            });

            return new { success = true, fare_calculating = true, message = "Fare is being calculated. Do NOT repeat any interjection—the system will inject the next step once address validation is complete." };
        }

        // AUTO VAD SWITCH: Determine what we're collecting next and switch mode
        _ = AutoSwitchVadForNextStepAsync();

        return new { success = true, authoritative_state = BuildGroundTruth() };
    }

    private object BuildGroundTruth() => new
    {
        pickup = _booking.Pickup,
        destination = _booking.Destination,
        passengers = _booking.Passengers,
        name = _booking.Name,
        pickup_time = _booking.PickupTime,
        vehicle_type = _booking.VehicleType
    };

    // =========================
    // AUTO VAD SWITCHING
    // =========================
    /// <summary>
    /// Automatically switches between semantic_vad (patient, for addresses) and server_vad (fast, for short replies)
    /// based on what the next required booking field is.
    /// </summary>
    private async Task AutoSwitchVadForNextStepAsync()
    {
        if (_aiClient is not OpenAiSdkClient sdk) return;

        // Determine the next missing field
        bool needsPickup = string.IsNullOrWhiteSpace(_booking.Pickup);
        bool needsDest = string.IsNullOrWhiteSpace(_booking.Destination);
        bool needsName = string.IsNullOrWhiteSpace(_booking.Name);
        bool needsPax = _booking.Passengers <= 0;
        bool needsTime = string.IsNullOrWhiteSpace(_booking.PickupTime);

        // Address fields → semantic VAD (patient, waits for complete thoughts)
        if (needsPickup || needsDest)
        {
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SEMANTIC (collecting address)", SessionId);
            await sdk.SetVadModeAsync(useSemantic: true, eagerness: 0.2f);
        }
        // Short-answer fields (name, passengers, time) → server VAD (fast response)
        else if (needsName || needsPax || needsTime)
        {
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (collecting short answer)", SessionId);
            await sdk.SetVadModeAsync(useSemantic: false);
        }
        // All fields filled → fare calculating, then confirmation → server VAD (yes/no)
        else
        {
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (awaiting confirmation)", SessionId);
            await sdk.SetVadModeAsync(useSemantic: false);
        }
    }

    // =========================
    // CLARIFY ADDRESS
    // =========================
    /// <summary>
    /// Known alternatives currently being presented to the caller.
    /// Used to detect if the caller provides a completely new address instead of choosing from the list.
    /// </summary>
    private string[]? _activePickupAlternatives;
    private string[]? _activeDestAlternatives;

    private object HandleClarifyAddress(Dictionary<string, object?> args)
    {
        var target = args.TryGetValue("target", out var t) ? t?.ToString() : null;
        var selected = args.TryGetValue("selected", out var s) ? s?.ToString() : null;

        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(selected))
            return new { success = false, needs_disambiguation = false, error = "Missing target or selected address." };

        if (target == "pickup")
        {
            // Detect if the user gave a NEW address instead of choosing from alternatives
            if (_activePickupAlternatives != null && _activePickupAlternatives.Length > 0
                && !IsSelectionFromAlternatives(selected, _activePickupAlternatives))
            {
                _logger.LogInformation("[{SessionId}] 🔄 Pickup: user gave NEW address '{New}' instead of choosing from alternatives — updating via sync", SessionId, selected);
                _booking.Pickup = selected;
                _booking.PickupLat = _booking.PickupLon = null;
                _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
                _pickupDisambiguated = true;
                _activePickupAlternatives = null;
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                OnBookingUpdated?.Invoke(_booking.Clone());
                _ = TriggerFareCalculationAsync();
                return new { success = true, needs_disambiguation = false, message = $"Pickup updated to '{selected}'. Fare calculation in progress — wait for [FARE RESULT]." };
            }

            _booking.Pickup = selected;
            _booking.PickupLat = _booking.PickupLon = null;
            _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
            _pickupDisambiguated = true;
            _activePickupAlternatives = null;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);

            _logger.LogInformation("[{SessionId}] 🔒 Pickup LOCKED: {Pickup}", SessionId, selected);

            // Check if dest still needs disambiguation
            if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0)
            {
                var options = _pendingDestAlternatives;
                _activeDestAlternatives = options;
                _pendingDestAlternatives = null;
                _pendingDestClarificationMessage = null;

                OnBookingUpdated?.Invoke(_booking.Clone());

                return new
                {
                    success = false,
                    needs_disambiguation = true,
                    target = "destination",
                    options,
                    instructions = "Pickup is confirmed. Now ask the user to clarify the destination from these options. If the user provides a completely DIFFERENT address instead of choosing from the list, call clarify_address with that new address."
                };
            }

            // Both locked — re-trigger fare
            OnBookingUpdated?.Invoke(_booking.Clone());
            _ = TriggerFareCalculationAsync();
            return new { success = true, needs_disambiguation = false, message = "Pickup locked. Fare calculation in progress — wait for [FARE RESULT]." };
        }

        if (target == "destination")
        {
            // Detect if the user gave a NEW address instead of choosing from alternatives
            if (_activeDestAlternatives != null && _activeDestAlternatives.Length > 0
                && !IsSelectionFromAlternatives(selected, _activeDestAlternatives))
            {
                _logger.LogInformation("[{SessionId}] 🔄 Destination: user gave NEW address '{New}' instead of choosing from alternatives — updating", SessionId, selected);
            }

            _booking.Destination = selected;
            _booking.DestLat = _booking.DestLon = null;
            _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
            _destDisambiguated = true;
            _activeDestAlternatives = null;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);

            _logger.LogInformation("[{SessionId}] 🔒 Destination LOCKED: {Destination}", SessionId, selected);

            OnBookingUpdated?.Invoke(_booking.Clone());
            _ = TriggerFareCalculationAsync();
            return new { success = true, needs_disambiguation = false, message = "Destination locked. Fare calculation in progress — wait for [FARE RESULT]." };
        }

        return new { success = false, needs_disambiguation = false, error = $"Unknown target: {target}" };
    }

    /// <summary>
    /// Check if the selected value fuzzy-matches any of the provided alternatives.
    /// Returns false if it looks like a completely different address.
    /// </summary>
    private static bool IsSelectionFromAlternatives(string selected, string[] alternatives)
    {
        var selNorm = NormalizeForComparison(selected);
        foreach (var alt in alternatives)
        {
            var altNorm = NormalizeForComparison(alt);
            // Exact or substring match
            if (selNorm == altNorm || altNorm.Contains(selNorm) || selNorm.Contains(altNorm))
                return true;
            // Check if the street name from the alternative appears in the selection
            var altStreet = ExtractStreetName(alt);
            var selStreet = ExtractStreetName(selected);
            if (!string.IsNullOrEmpty(altStreet) && !string.IsNullOrEmpty(selStreet)
                && string.Equals(altStreet, selStreet, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeForComparison(string s)
        => s.Trim().ToLowerInvariant().Replace(",", "").Replace(".", "");

    private static string ExtractStreetName(string address)
    {
        // Remove leading house number and trailing city/postcode
        var parts = address.Split(',');
        var first = parts[0].Trim();
        // Strip leading digits (house number)
        var idx = 0;
        while (idx < first.Length && (char.IsDigit(first[idx]) || first[idx] == ' ' || char.IsLetter(first[idx]) && idx < 4 && char.IsDigit(first[Math.Max(0, idx - 1)])))
            idx++;
        return first[idx..].Trim();
    }

    private async Task TriggerFareCalculationAsync()
    {
        if (_booking.Pickup == null || _booking.Destination == null)
            return;

        var pickup = _booking.Pickup;
        var destination = _booking.Destination;
        var callerId = CallerId;
        var sessionId = SessionId;

        try
        {
            _logger.LogInformation("[{SessionId}] 🔄 Fare re-calculation after Address Lock resolution", sessionId);
            var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId,
                spokenPickupNumber: GetSpokenHouseNumber(pickup),
                spokenDestNumber: GetSpokenHouseNumber(destination));

            // Check if re-disambiguation is needed (e.g. clarified address still ambiguous in a different way)
            if (result.NeedsClarification)
            {
                var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                if (pickupAlts.Length > 0)
                {
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    if (_aiClient is OpenAiSdkClient sdk)
                        await sdk.InjectMessageAndRespondAsync(
                            $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=pickup, " +
                            $"options=[{string.Join(", ", pickupAlts)}]. " +
                            "Ask the caller to clarify the PICKUP. Present options with numbers, then WAIT.");
                    return;
                }
                if (destAlts.Length > 0)
                {
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    if (_aiClient is OpenAiSdkClient sdk)
                        await sdk.InjectMessageAndRespondAsync(
                            $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=destination, " +
                            $"options=[{string.Join(", ", destAlts)}]. " +
                            "Ask the caller to clarify the DESTINATION. Present options with numbers, then WAIT.");
                    return;
                }
            }

            // Both addresses resolved — sanity check before applying
            if (!IsFareSane(result))
            {
                _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED after clarification — asking user to verify destination", sessionId);
                Interlocked.Exchange(ref _fareAutoTriggered, 0);

                if (_aiClient is OpenAiSdkClient sdkSanity)
                    await sdkSanity.InjectMessageAndRespondAsync(
                        "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard or the city could not be determined. " +
                        "Ask the caller to confirm their DESTINATION address AND which city or area they are in. " +
                        "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going, and which city you're in?\" " +
                        "When they respond, call sync_booking_data with the destination INCLUDING the city name (e.g. '7 Russell Street, Coventry').");
                return;
            }

            ApplyFareResult(result);

            if (_aiClient is OpenAiSdkClient sdkConf)
            {
                sdkConf.SetAwaitingConfirmation(true);
                await sdkConf.SetVadModeAsync(useSemantic: false);
            }

            OnBookingUpdated?.Invoke(_booking.Clone());

            var spokenFare = FormatFareForSpeech(_booking.Fare);
            var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
            var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

            _logger.LogInformation("[{SessionId}] 💰 Fare ready after clarification: {Fare}, ETA: {Eta}",
                sessionId, _booking.Fare, _booking.Eta);

            if (_aiClient is OpenAiSdkClient sdkInject)
                await sdkInject.InjectMessageAndRespondAsync(
                    $"[FARE RESULT] The fare from {pickupAddr} to {destAddr} is {spokenFare}, " +
                    $"estimated time of arrival is {_booking.Eta}. " +
                    "Read back the VERIFIED addresses and fare to the caller and ask them to confirm.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Fare re-calculation failed after clarification", sessionId);
            _booking.Fare = "£8.00";
            _booking.Eta = "8 minutes";
            OnBookingUpdated?.Invoke(_booking.Clone());

            if (_aiClient is OpenAiSdkClient sdkFallback)
                await sdkFallback.InjectMessageAndRespondAsync(
                    "[FARE RESULT] The estimated fare is 8 pounds, estimated time of arrival is 8 minutes. " +
                    "Read back the details to the caller and ask them to confirm.");
        }
    }

    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

        // SAFETY NET: populate _booking from book_taxi args (with name validation)
        if (args.TryGetValue("caller_name", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString())
            && !_rejectedNames.Contains(bn.ToString()!.Trim()))
            _booking.Name = bn.ToString()!.Trim();

        // Name guard — reject booking without a real name
        if (string.IsNullOrWhiteSpace(_booking.Name) || _rejectedNames.Contains(_booking.Name))
            return new { success = false, error = "Caller name is required. Ask the caller for their name before booking." };
        if (args.TryGetValue("pickup", out var bp) && !string.IsNullOrWhiteSpace(bp?.ToString()))
            _booking.Pickup = bp.ToString();
        if (args.TryGetValue("destination", out var bd) && !string.IsNullOrWhiteSpace(bd?.ToString()))
            _booking.Destination = bd.ToString();
        if (args.TryGetValue("passengers", out var bpax) && int.TryParse(bpax?.ToString(), out var bpn))
            _booking.Passengers = bpn;
        if (args.TryGetValue("pickup_time", out var bpt) && !string.IsNullOrWhiteSpace(bpt?.ToString()))
            _booking.PickupTime = bpt.ToString();

        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Missing pickup or destination." };

            // GUARD: If sync_booking_data already auto-triggered fare calculation, don't spawn a duplicate.
            if (Volatile.Read(ref _fareAutoTriggered) == 1)
            {
                _logger.LogInformation("[{SessionId}] ⏳ book_taxi(request_quote) skipped — fare already in flight from auto-trigger", SessionId);
                return new { success = true, status = "calculating", message = "Fare is already being calculated. Do NOT repeat any interjection — the system will inject the result automatically." };
            }

            // NON-BLOCKING: return immediately so Ada speaks an interjection,
            // then inject the fare result asynchronously when ready.
            var pickup = _booking.Pickup;
            var destination = _booking.Destination;
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("[{SessionId}] 🔄 Background fare calculation starting for {Pickup} → {Dest}",
                        sessionId, pickup, destination);

                    var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId,
                        spokenPickupNumber: GetSpokenHouseNumber(pickup),
                        spokenDestNumber: GetSpokenHouseNumber(destination));
                    var completed = await Task.WhenAny(aiTask, Task.Delay(10000));

                    FareResult result;
                    if (completed == aiTask)
                    {
                        result = await aiTask;
                        if (result.NeedsClarification)
                        {
                            var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                            var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                            // Sequential disambiguation: pickup FIRST, then dropoff
                            if (pickupAlts.Length > 0)
                            {
                                if (destAlts.Length > 0)
                                {
                                    _pendingDestAlternatives = destAlts;
                                    _pendingDestClarificationMessage = result.ClarificationMessage;
                                    _destDisambiguated = false;
                                }

                                var altsList = string.Join(", ", pickupAlts);
                                _pickupDisambiguated = false;

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[PICKUP DISAMBIGUATION] The PICKUP address is ambiguous. The options are: {altsList}. " +
                                        "Ask the caller ONLY about the PICKUP location. Do NOT mention the destination yet. " +
                                        "Present the pickup options clearly, then STOP and WAIT for their answer.");
                            }
                            else if (destAlts.Length > 0)
                            {
                                var altsList = string.Join(", ", destAlts);
                                _destDisambiguated = false;

                                if (_aiClient is OpenAiSdkClient sdkClarif)
                                    await sdkClarif.InjectMessageAndRespondAsync(
                                        $"[DESTINATION DISAMBIGUATION] The DESTINATION address is ambiguous. The options are: {altsList}. " +
                                        "Ask the caller ONLY about the DESTINATION location. " +
                                        "Present the destination options clearly, then STOP and WAIT for their answer.");
                            }

                            return;
                        }

                        // Check for pending destination alternatives (pickup was resolved, dest still needs it)
                        if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0 && !_destDisambiguated)
                        {
                            var destAltsList = string.Join(", ", _pendingDestAlternatives);
                            _logger.LogInformation("[{SessionId}] 🔄 Now resolving pending destination disambiguation: {Alts}", sessionId, destAltsList);

                            if (_aiClient is OpenAiSdkClient sdkDestClarif)
                                await sdkDestClarif.InjectMessageAndRespondAsync(
                                    $"[DESTINATION DISAMBIGUATION] Good, the pickup is confirmed. Now the DESTINATION address is ambiguous. The options are: {destAltsList}. " +
                                    "Ask the caller ONLY about the DESTINATION location. " +
                                    "Present the destination options clearly, then STOP and WAIT for their answer.");

                            _pendingDestAlternatives = null;
                            _pendingDestClarificationMessage = null;
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[{SessionId}] ⚠️ Edge function timed out, using fallback", sessionId);
                        result = await _fareCalculator.CalculateAsync(pickup, destination, callerId);
                    }

                    // Clear pending disambiguation state
                    _pendingDestAlternatives = null;
                    _pendingDestClarificationMessage = null;

                    // FARE SANITY CHECK
                    if (!IsFareSane(result))
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED (book_taxi path) — asking user to verify destination", sessionId);

                        if (_aiClient is OpenAiSdkClient sdkSanity)
                            await sdkSanity.InjectMessageAndRespondAsync(
                                "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard or the city could not be determined. " +
                                "Ask the caller to confirm their DESTINATION address AND which city or area they are in. " +
                                "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going, and which city you're in?\" " +
                                "When they respond, call sync_booking_data with the destination INCLUDING the city name (e.g. '7 Russell Street, Coventry').");
                        return;
                    }

                    ApplyFareResult(result);

                    if (_aiClient is OpenAiSdkClient sdk)
                    {
                        sdk.SetAwaitingConfirmation(true);
                        await sdk.SetVadModeAsync(useSemantic: false);
                    }

                    OnBookingUpdated?.Invoke(_booking.Clone());

                    var spokenFare = FormatFareForSpeech(_booking.Fare);
                    _logger.LogInformation("[{SessionId}] 💰 Fare ready: {Fare} ({Spoken}), ETA: {Eta}",
                        sessionId, _booking.Fare, spokenFare, _booking.Eta);

                    var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
                    var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

                    // Inject fare result into conversation — Ada will read it back
                    if (_aiClient is OpenAiSdkClient sdkInject)
                        await sdkInject.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] The fare from {pickupAddr} to {destAddr} is {spokenFare}, " +
                            $"estimated time of arrival is {_booking.Eta}. " +
                            $"Read back these details to the caller and ask them to confirm the booking.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Background fare calculation failed", sessionId);
                    _booking.Fare = "£8.00";
                    _booking.Eta = "8 minutes";
                    OnBookingUpdated?.Invoke(_booking.Clone());

                    if (_aiClient is OpenAiSdkClient sdkFallback)
                        await sdkFallback.InjectMessageAndRespondAsync(
                            "[FARE RESULT] The estimated fare is 8 pounds, estimated time of arrival is 8 minutes. " +
                            "Read back these details to the caller and ask them to confirm.");
                }
            });

            // Return immediately — Ada will speak "let me check that" while fare calculates
            return new
            {
                success = true,
                status = "calculating",
                message = "I'm checking the fare now. Tell the caller you're just looking up the fare and it will only take a moment."
            };
        }

        if (action == "confirmed")
        {
            // IN-PROGRESS GUARD: Block confirmation while fare calculation is still in flight
            if (Volatile.Read(ref _fareAutoTriggered) == 1 && _booking.Fare == null)
            {
                _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — fare calculation still in progress", SessionId);
                return new { success = false, error = "Cannot confirm yet — the fare is still being calculated. Wait for the fare result before confirming." };
            }

            // Block confirmation while fare sanity alert is active (race condition guard)
            if (_fareSanityActive)
            {
                _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — fare sanity alert is active, waiting for user to re-confirm destination", SessionId);
                return new { success = false, error = "Cannot confirm yet — the fare seems unusually high and the caller needs to verify their destination first. Wait for their response." };
            }

            // Block confirmation while disambiguation is in progress
            if (!_pickupDisambiguated || !_destDisambiguated)
            {
                _logger.LogWarning("[{SessionId}] ⛔ book_taxi(confirmed) BLOCKED — address disambiguation in progress (pickup_resolved={Pickup}, dest_resolved={Dest})",
                    SessionId, _pickupDisambiguated, _destDisambiguated);
                return new { success = false, error = "Cannot confirm yet — address disambiguation is still in progress. Wait for the caller to choose their address before confirming." };
            }

            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Cannot confirm: missing pickup or destination." };

            if (Interlocked.CompareExchange(ref _bookTaxiCompleted, 1, 0) == 1)
                return new { success = true, booking_ref = _booking.BookingRef ?? "already-booked", message = "Already confirmed." };

            // Geocode if needed
            bool needsGeocode = string.IsNullOrWhiteSpace(_booking.PickupStreet)
                || (_booking.PickupLat == 0 && _booking.PickupLon == 0)
                || (_booking.DestLat == 0 && _booking.DestLon == 0);

            if (needsGeocode)
            {
                try
                {
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId,
                        spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                        spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
                    ApplyFareResultNullSafe(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Pre-dispatch geocode failed", SessionId);
                }
            }

            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";

            if (_aiClient is OpenAiSdkClient sdk)
                sdk.SetAwaitingConfirmation(false);

            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] ✅ Booked: {Ref}", SessionId, _booking.BookingRef);

            var bookingSnapshot = _booking.Clone();
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                if (_aiClient is OpenAiSdkClient sdkWait)
                {
                    for (int i = 0; i < 50 && sdkWait.IsResponseActive; i++)
                        await Task.Delay(100);
                }

                await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
                await _dispatcher.SendWhatsAppAsync(callerId);

                // iCabbi dispatch (fire-and-forget)
                if (_icabbiEnabled && _icabbi != null)
                {
                    try
                    {
                        var icabbiResult = await _icabbi.CreateAndDispatchAsync(bookingSnapshot);
                        if (icabbiResult.Success)
                            _logger.LogInformation("[{SessionId}] 🚕 iCabbi booking created: {JourneyId}", sessionId, icabbiResult.JourneyId);
                        else
                            _logger.LogWarning("[{SessionId}] ⚠️ iCabbi booking failed: {Msg}", sessionId, icabbiResult.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{SessionId}] iCabbi dispatch error", sessionId);
                    }
                }
            });

            return new { success = true, booking_ref = _booking.BookingRef, message = $"Taxi booked successfully. Tell the caller: Your booking reference is {_booking.BookingRef}. Then ask if they need anything else. If not, say the FINAL CLOSING script verbatim and call end_call." };
        }

        return new { error = "Invalid action" };
    }

    // =========================
    // CREATE BOOKING (straight-through, with AI extraction)
    // =========================
    private async Task<object> HandleCreateBookingAsync(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("pickup_address", out var pu)) _booking.Pickup = pu?.ToString();
        if (args.TryGetValue("dropoff_address", out var dd)) _booking.Destination = dd?.ToString();
        if (args.TryGetValue("passenger_count", out var pc) && int.TryParse(pc?.ToString(), out var pn))
            _booking.Passengers = pn;

        if (string.IsNullOrWhiteSpace(_booking.Pickup))
            return new { success = false, error = "Missing pickup address" };

        _logger.LogInformation("[{SessionId}] 🚕 create_booking: {Pickup} → {Dest}, {Pax} pax",
            SessionId, _booking.Pickup, _booking.Destination ?? "TBD", _booking.Passengers ?? 1);

        try
        {
            var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(
                _booking.Pickup, _booking.Destination ?? _booking.Pickup, CallerId,
                spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
            var completed = await Task.WhenAny(aiTask, Task.Delay(10000));

            FareResult result;
            if (completed == aiTask)
            {
                result = await aiTask;

                if (result.NeedsClarification)
                {
                    var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                    var destAlts = result.DestAlternatives ?? Array.Empty<string>();
                    var allAlts = pickupAlts.Concat(destAlts).ToArray();
                    var numberedAlts = allAlts.Select((alt, i) => $"{i + 1}. {alt}").ToArray();

                    return new
                    {
                        success = false,
                        needs_clarification = true,
                        pickup_options = pickupAlts,
                        destination_options = destAlts,
                        alternatives_list = numberedAlts,
                        message = $"I found multiple matches. Please ask the caller which one they mean:\n{string.Join("\n", numberedAlts)}"
                    };
                }
            }
            else
            {
                _logger.LogWarning("[{SessionId}] ⏱️ AI extraction timeout, using fallback", SessionId);
                result = await _fareCalculator.CalculateAsync(
                    _booking.Pickup, _booking.Destination ?? _booking.Pickup, CallerId);
            }

            ApplyFareResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Fare error, using fallback", SessionId);
            _booking.Fare = "£12.50";
            _booking.Eta = "6 minutes";
        }

        _booking.Confirmed = true;
        _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";

        OnBookingUpdated?.Invoke(_booking.Clone());
        _logger.LogInformation("[{SessionId}] ✅ Booked: {Ref}, Fare: {Fare}, ETA: {Eta}",
            SessionId, _booking.BookingRef, _booking.Fare, _booking.Eta);

        var bookingSnapshot = _booking.Clone();
        var callerId = CallerId;

        _ = Task.Run(async () =>
        {
            await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
            await _dispatcher.SendWhatsAppAsync(callerId);

            if (_icabbiEnabled && _icabbi != null)
            {
                try
                {
                    var icabbiResult = await _icabbi.CreateAndDispatchAsync(bookingSnapshot);
                    if (icabbiResult.Success)
                        _logger.LogInformation("[{SessionId}] 🚕 iCabbi OK — Journey: {JourneyId}", SessionId, icabbiResult.JourneyId);
                    else
                        _logger.LogWarning("[{SessionId}] ⚠ iCabbi failed: {Message}", SessionId, icabbiResult.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{SessionId}] ❌ iCabbi dispatch error", SessionId);
                }
            }
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(15000);
            if (!IsActive) return;
            if (_aiClient is OpenAiSdkClient sdk)
            {
                if (!sdk.IsConnected) return;
                sdk.CancelDeferredResponse();
                _logger.LogInformation("[{SessionId}] ⏰ Post-booking timeout - requesting farewell", SessionId);
            }
        });

        var fareSpoken = FormatFareForSpeech(_booking.Fare);

        return new
        {
            success = true,
            booking_ref = _booking.BookingRef,
            fare = _booking.Fare,
            fare_spoken = fareSpoken,
            eta = _booking.Eta,
            message = string.IsNullOrWhiteSpace(_booking.Name)
                ? $"Your taxi is booked! Fare is {fareSpoken}, driver arrives in {_booking.Eta}."
                : $"Thanks {_booking.Name.Trim()}, your taxi is booked! Fare is {fareSpoken}, driver arrives in {_booking.Eta}."
        };
    }

    // =========================
    // FIND LOCAL EVENTS
    // =========================
    private object HandleFindLocalEvents(Dictionary<string, object?> args)
    {
        var category = args.TryGetValue("category", out var cat) ? cat?.ToString() ?? "all" : "all";
        var near = args.TryGetValue("near", out var n) ? n?.ToString() : null;
        var date = args.TryGetValue("date", out var dt) ? dt?.ToString() ?? "this weekend" : "this weekend";

        _logger.LogInformation("[{SessionId}] 🎭 Events lookup: {Category} near {Near} on {Date}",
            SessionId, category, near ?? "unknown", date);

        var mockEvents = new[]
        {
            new { name = "Live Music at The Empire", venue = near ?? "city centre", date = "Tonight, 8pm", type = "concert" },
            new { name = "Comedy Night at The Kasbah", venue = near ?? "city centre", date = "Saturday, 9pm", type = "comedy" },
            new { name = "Theatre Royal Show", venue = near ?? "city centre", date = "This weekend", type = "theatre" }
        };

        return new
        {
            success = true,
            events = mockEvents,
            message = $"Found {mockEvents.Length} events near {near ?? "your area"}. Would you like a taxi to any of these?"
        };
    }

    // =========================
    // END CALL
    // =========================
    private object HandleEndCall(Dictionary<string, object?> args)
    {
        // ── END-CALL GUARD: Block premature hangup if booking flow was started but never completed ──
        bool fareWasCalculated = !string.IsNullOrWhiteSpace(_booking.Fare);
        bool bookingCompleted = Volatile.Read(ref _bookTaxiCompleted) == 1;

        if (fareWasCalculated && !bookingCompleted)
        {
            _logger.LogWarning("[{SessionId}] ⛔ END_CALL BLOCKED: fare was quoted but book_taxi(confirmed) never called", SessionId);
            return new
            {
                success = false,
                error = "Cannot end call yet — a fare was quoted but the booking was never confirmed. " +
                        "You MUST read back the fare and ask the caller to confirm before ending. " +
                        "If they already said yes, call book_taxi(action: 'confirmed') first."
            };
        }

        _ = Task.Run(async () =>
        {
            if (_aiClient is OpenAiSdkClient sdk)
            {
                var streamStart = Environment.TickCount64;
                while (sdk.IsResponseActive && Environment.TickCount64 - streamStart < 15000)
                    await Task.Delay(200);

                var enqueueStart = Environment.TickCount64;
                while ((sdk.GetQueuedFrames?.Invoke() ?? 0) == 0 && Environment.TickCount64 - enqueueStart < 5000)
                    await Task.Delay(100);

                await Task.Delay(2000);

                var drainStart = Environment.TickCount64;
                while (Environment.TickCount64 - drainStart < 20000)
                {
                    if ((sdk.GetQueuedFrames?.Invoke() ?? 0) == 0) break;
                    await Task.Delay(100);
                }

                await Task.Delay(1000);
            }
            else
            {
                await Task.Delay(5000);
            }

            await EndAsync("end_call tool");
        });

        return new { success = true };
    }

    // =========================
    // HELPERS
    // =========================
    private static string FormatFareForSpeech(string? fare)
    {
        if (string.IsNullOrEmpty(fare)) return "unknown";
        var currencyWord = fare.Contains("£") ? "pounds" : "euros";
        var clean = fare.Replace("€", "").Replace("£", "").Replace("$", "").Trim();
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            var whole = (int)amount;
            var pence = (int)((amount - whole) * 100);
            return pence > 0 ? $"{whole} {currencyWord} {pence}" : $"{whole} {currencyWord}";
        }
        return fare;
    }

    private static string FormatAddressForReadback(string? number, string? street, string? postalCode, string? city)
    {
        var parts = new List<string>();
        
        if (!string.IsNullOrWhiteSpace(number))
            parts.Add(number);
        if (!string.IsNullOrWhiteSpace(street))
            parts.Add(street);
        if (!string.IsNullOrWhiteSpace(postalCode))
            parts.Add(postalCode);
        if (!string.IsNullOrWhiteSpace(city))
            parts.Add(city);
        
        return parts.Count > 0 ? string.Join(", ", parts) : "the address";
    }

    // =========================
    // FARE SANITY CHECK
    // =========================
    private const decimal MAX_SANE_FARE = 100m;
    private const int MAX_SANE_ETA_MINUTES = 120;

    /// <summary>
    /// Returns true if the fare and ETA are within reasonable bounds.
    /// Catches cases where STT mishears a destination (e.g. "Kochi" instead of "Coventry")
    /// resulting in absurd cross-country/international fares.
    /// </summary>
    private bool IsFareSane(FareResult result)
    {
        var dest = _booking.Destination ?? "";

        // Debug logging: trace exactly what the bypass check sees
        _logger.LogDebug("[{SessionId}] 🔍 IsFareSane: count={Count}, dest='{Dest}', lastDest='{LastDest}'",
            SessionId, _fareSanityAlertCount, dest, _lastSanityAlertDestination ?? "(null)");

        // HARD BYPASS: After 2+ sanity alerts, let it through regardless — user clearly wants this destination
        if (_fareSanityAlertCount >= 2)
        {
            _logger.LogInformation("[{SessionId}] ✅ Fare sanity FORCE BYPASSED — {Count} alerts already shown, allowing through",
                SessionId, _fareSanityAlertCount);
            _fareSanityAlertCount = 0;
            _lastSanityAlertDestination = null;
            _fareSanityActive = false;
            return true;
        }

        // If the user re-confirmed the SAME destination after a sanity alert, allow it through
        // Use fuzzy contains-match — the destination string may change slightly between passes
        // (e.g. "Manchester" vs "Manchester, UK" or geocoded version with postcode)
        if (_fareSanityAlertCount > 0 && !string.IsNullOrWhiteSpace(_lastSanityAlertDestination))
        {
            var d = dest.Trim();
            var last = _lastSanityAlertDestination.Trim();
            if (string.Equals(d, last, StringComparison.OrdinalIgnoreCase)
                || d.Contains(last, StringComparison.OrdinalIgnoreCase)
                || last.Contains(d, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[{SessionId}] ✅ Fare sanity BYPASSED — user re-confirmed destination '{Dest}' ≈ '{Last}' (attempt {Count})",
                    SessionId, dest, _lastSanityAlertDestination, _fareSanityAlertCount + 1);
                _fareSanityAlertCount = 0;
                _lastSanityAlertDestination = null;
                _fareSanityActive = false;
                return true;
            }
        }

        // CITY-LEVEL BYPASS: If destination is a known town/city (no street number), 
        // the user clearly intends a long-distance trip — skip sanity check entirely
        if (IsCityLevelDestination(dest))
        {
            _logger.LogInformation("[{SessionId}] ✅ Fare sanity BYPASSED — city-level destination '{Dest}' (no street-level detail)",
                SessionId, dest);
            _fareSanityAlertCount = 0;
            _lastSanityAlertDestination = null;
            _fareSanityActive = false;
            return true;
        }

        // Parse fare amount
        var fareStr = result.Fare?.Replace("£", "").Replace("€", "").Replace("$", "").Trim();
        if (decimal.TryParse(fareStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var fareAmount))
        {
            if (fareAmount > MAX_SANE_FARE)
            {
                _logger.LogWarning("[{SessionId}] 🚨 INSANE FARE detected: {Fare} (max={Max})", SessionId, result.Fare, MAX_SANE_FARE);
                _fareSanityAlertCount++;
                _lastSanityAlertDestination = dest;
                _fareSanityActive = true;
                return false;
            }
        }

        // Parse ETA minutes
        var etaStr = result.Eta?.Replace("minutes", "").Replace("minute", "").Trim();
        if (int.TryParse(etaStr, out var etaMinutes))
        {
            if (etaMinutes > MAX_SANE_ETA_MINUTES)
            {
                _logger.LogWarning("[{SessionId}] 🚨 INSANE ETA detected: {Eta} (max={Max} min)", SessionId, result.Eta, MAX_SANE_ETA_MINUTES);
                _fareSanityAlertCount++;
                _lastSanityAlertDestination = dest;
                _fareSanityActive = true;
                return false;
            }
        }

        // Fare is sane — reset sanity state
        _fareSanityAlertCount = 0;
        _lastSanityAlertDestination = null;
        _fareSanityActive = false;
        return true;
    }

    /// <summary>
    /// Detects city/town-level destinations (no street number or specific address).
    /// These are intentional long-distance trips and should bypass fare sanity checks.
    /// </summary>
    private static bool IsCityLevelDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;

        var d = destination.Trim();

        // If it contains a street number (digits at start or after comma), it's street-level
        // e.g. "52A David Road" or "David Road, 52A"
        if (System.Text.RegularExpressions.Regex.IsMatch(d, @"\b\d+[A-Za-z]?\b.*\b(road|street|lane|drive|avenue|close|way|crescent|terrace|place|grove|court|gardens|walk|rise|hill|park|row|square|mews|passage|yard)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;

        // Short destination with 1-3 words and no digits = likely a town/city name
        var words = d.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var nonTrivialWords = words.Where(w => !string.Equals(w, "city", StringComparison.OrdinalIgnoreCase) 
            && !string.Equals(w, "centre", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(w, "center", StringComparison.OrdinalIgnoreCase)).ToArray();

        // No digits anywhere = no house number or postcode = city-level
        if (!System.Text.RegularExpressions.Regex.IsMatch(d, @"\d"))
            return true;

        return false;
    }

    private void ApplyFareResult(FareResult result)
    {
        _booking.Fare = result.Fare;
        _booking.Eta = result.Eta;
        _booking.PickupLat = result.PickupLat;
        _booking.PickupLon = result.PickupLon;
        _booking.PickupStreet = result.PickupStreet;
        _booking.PickupNumber = result.PickupNumber;
        _booking.PickupPostalCode = result.PickupPostalCode;
        _booking.PickupCity = result.PickupCity;
        _booking.PickupFormatted = result.PickupFormatted;
        _booking.DestLat = result.DestLat;
        _booking.DestLon = result.DestLon;
        _booking.DestStreet = result.DestStreet;
        _booking.DestNumber = result.DestNumber;
        _booking.DestPostalCode = result.DestPostalCode;
        _booking.DestCity = result.DestCity;
        _booking.DestFormatted = result.DestFormatted;
    }

    private void ApplyFareResultNullSafe(FareResult result)
    {
        _booking.PickupLat ??= result.PickupLat;
        _booking.PickupLon ??= result.PickupLon;
        _booking.PickupStreet ??= result.PickupStreet;
        _booking.PickupNumber ??= result.PickupNumber;
        _booking.PickupPostalCode ??= result.PickupPostalCode;
        _booking.PickupCity ??= result.PickupCity;
        _booking.PickupFormatted ??= result.PickupFormatted;
        _booking.DestLat ??= result.DestLat;
        _booking.DestLon ??= result.DestLon;
        _booking.DestStreet ??= result.DestStreet;
        _booking.DestNumber ??= result.DestNumber;
        _booking.DestPostalCode ??= result.DestPostalCode;
        _booking.DestCity ??= result.DestCity;
        _booking.DestFormatted ??= result.DestFormatted;
        _booking.Fare ??= result.Fare;
        _booking.Eta ??= result.Eta;

        if (_booking.PickupLat == 0 && result.PickupLat != 0) _booking.PickupLat = result.PickupLat;
        if (_booking.PickupLon == 0 && result.PickupLon != 0) _booking.PickupLon = result.PickupLon;
        if (_booking.DestLat == 0 && result.DestLat != 0) _booking.DestLat = result.DestLat;
        if (_booking.DestLon == 0 && result.DestLon != 0) _booking.DestLon = result.DestLon;
    }

    private static bool StreetNameChanged(string? oldAddress, string? newAddress)
    {
        if (string.IsNullOrWhiteSpace(oldAddress) || string.IsNullOrWhiteSpace(newAddress))
            return false;
        string Normalize(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"\d|[^a-z ]", "").Trim();
        return Normalize(oldAddress) != Normalize(newAddress);
    }

    /// <summary>
    /// HARD GUARD: If the AI substituted a street name that differs from the transcript,
    /// replace the AI's version with the transcript version.
    /// If the house number or area/town differs, they are treated as genuinely different addresses.
    /// </summary>
    private string? ApplyTranscriptStreetGuard(string? aiAddress, string transcript, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(aiAddress) || string.IsNullOrWhiteSpace(transcript) || transcript.Length < 5)
            return aiAddress;

        // ── HOUSE NUMBER + AREA COMPARISON ──
        var aiParsed = AddressParser.ParseAddress(aiAddress);
        var transcriptParsed = AddressParser.ParseAddress(transcript);

        if (aiParsed.HasHouseNumber && transcriptParsed.HasHouseNumber
            && !string.Equals(aiParsed.HouseNumber, transcriptParsed.HouseNumber, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "[{SessionId}] 🛡️ STREET GUARD SKIP ({Field}): Different house numbers — AI='{AiNum}' vs transcript='{TNum}'",
                SessionId, fieldName, aiParsed.HouseNumber, transcriptParsed.HouseNumber);
            return aiAddress;
        }

        if (!string.IsNullOrWhiteSpace(aiParsed.TownOrArea) && !string.IsNullOrWhiteSpace(transcriptParsed.TownOrArea)
            && !string.Equals(aiParsed.TownOrArea, transcriptParsed.TownOrArea, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "[{SessionId}] 🛡️ STREET GUARD SKIP ({Field}): Different area — AI='{AiArea}' vs transcript='{TArea}'",
                SessionId, fieldName, aiParsed.TownOrArea, transcriptParsed.TownOrArea);
            return aiAddress;
        }

        // Compare street suffix: if both are street-type but suffixes differ → different address
        if (aiParsed.IsStreetTypeAddress && transcriptParsed.IsStreetTypeAddress
            && !string.IsNullOrWhiteSpace(aiParsed.StreetName) && !string.IsNullOrWhiteSpace(transcriptParsed.StreetName))
        {
            var aiSuffix = aiParsed.StreetName.Split(' ').LastOrDefault() ?? "";
            var tSuffix = transcriptParsed.StreetName.Split(' ').LastOrDefault() ?? "";
            if (!string.Equals(aiSuffix, tSuffix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[{SessionId}] 🛡️ STREET GUARD SKIP ({Field}): Different street suffix — AI='{AiSuffix}' vs transcript='{TSuffix}'",
                    SessionId, fieldName, aiSuffix, tSuffix);
                return aiAddress;
            }
        }

        var skipWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "road", "street", "lane", "drive", "avenue", "close", "way", "crescent",
            "place", "court", "grove", "terrace", "gardens", "hill", "park", "row",
            "the", "and", "from", "to", "for", "birmingham", "coventry", "london",
            "wolverhampton", "solihull", "walsall", "dudley", "sandwell", "warwick"
        };

        var transcriptWords = transcript.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && w.All(c => char.IsLetter(c)))
            .Select(w => w.Trim())
            .Where(w => !skipWords.Contains(w))
            .ToList();

        if (transcriptWords.Count == 0) return aiAddress;

        var aiWords = aiAddress.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && w.All(c => char.IsLetter(c)))
            .Where(w => !skipWords.Contains(w))
            .ToList();

        var transcriptLookup = new HashSet<string>(transcriptWords.Select(w => w.ToLowerInvariant()));

        foreach (var aiWord in aiWords)
        {
            if (transcriptLookup.Contains(aiWord.ToLowerInvariant()))
                continue;

            foreach (var tWord in transcriptWords)
            {
                int dist = LevenshteinDistance(aiWord.ToLowerInvariant(), tWord.ToLowerInvariant());
                if (dist >= 1 && dist <= 2)
                {
                    _logger.LogWarning(
                        "[{SessionId}] 🛡️ STREET GUARD ({Field}): AI sent '{AiWord}' but transcript has '{TranscriptWord}' — using transcript version",
                        SessionId, fieldName, aiWord, tWord);

                    var result = System.Text.RegularExpressions.Regex.Replace(
                        aiAddress,
                        @"\b" + System.Text.RegularExpressions.Regex.Escape(aiWord) + @"\b",
                        tWord,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    return result;
                }
            }
        }

        return aiAddress;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }

        return d[a.Length, b.Length];
    }

    /// <summary>
    /// Extracts the spoken house number from a raw address string using AddressParser.
    /// Returns null if no number found or number is "0" (not found sentinel).
    /// Used as a geocoding guard — passed to Gemini/edge function to prevent silent substitutions.
    /// </summary>
    private static string? GetSpokenHouseNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var num = Zaffiqbal247RadioCars.Services.AddressParser.ParseAddress(address).HouseNumber;
        return string.IsNullOrEmpty(num) || num == "0" ? null : num;
    }

    // NormalizeHouseNumber: corrects HYPHENATED STT mishearings only.
    // "52A" → "52-8", "14B" → "14-3", "7D" → "7-4" are common Whisper errors.
    // Only the hyphenated form is matched — plain trailing digits (e.g. "43") are untouched.
    private static readonly System.Text.RegularExpressions.Regex _sttHyphenFixRegex =
        new(@"^(\d{1,3})-(8|3|4)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Dictionary<string, string> _digitToLetter = new()
    {
        ["8"] = "A",
        ["3"] = "B",
        ["4"] = "D",
    };

    private string? NormalizeHouseNumber(string? address, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;
        var trimmed = address.Trim();
        var match = _sttHyphenFixRegex.Match(trimmed);
        if (!match.Success) return address;
        var baseNum = match.Groups[1].Value;
        var letter  = _digitToLetter[match.Groups[2].Value];
        var corrected = $"{baseNum}{letter}{trimmed[(match.Length)..]}";
        _logger.LogInformation("[{SessionId}] 🔤 STT hyphen fix ({Field}): '{Original}' → '{Corrected}'",
            SessionId, fieldName, match.Value, $"{baseNum}{letter}");
        return corrected;
    }

    /// <summary>
    /// <summary>
    /// Check if Whisper STT output looks like intelligible English text.
    /// Returns false for garbled text, non-Latin scripts, very short fragments, etc.
    /// When false, we skip mismatch detection entirely and trust Ada's interpretation.
    /// </summary>
    private static bool IsIntelligibleEnglish(string sttText)
    {
        if (string.IsNullOrWhiteSpace(sttText)) return false;
        
        var trimmed = sttText.Trim();
        
        // Too short to be meaningful (e.g., single word fragments)
        if (trimmed.Length < 5) return false;
        
        // Check ratio of Latin characters — if less than 60%, it's likely non-English/garbled
        int latinCount = 0, totalLetters = 0;
        foreach (var c in trimmed)
        {
            if (char.IsLetter(c))
            {
                totalLetters++;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                    latinCount++;
            }
        }
        
        // If no letters at all, or mostly non-Latin script, skip
        if (totalLetters == 0) return false;
        var latinRatio = (double)latinCount / totalLetters;
        if (latinRatio < 0.6) return false;
        
        // Check if it has at least 2 recognizable English-like words (3+ chars, Latin)
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var englishLikeWords = words.Count(w => w.Length >= 3 && w.All(c => char.IsLetterOrDigit(c) || c == '\'' || c == '-'));
        if (englishLikeWords < 2) return false;
        
        return true;
    }

    /// <summary>
    /// Compares two strings for significant differences using word-level similarity.
    /// Returns true if the strings differ enough to warrant a soft advisory.
    /// Used for transcript mismatch detection (Whisper STT vs Ada's interpretation).
    /// </summary>
    private static bool IsSignificantlyDifferent(string adaValue, string sttText)
    {
        if (string.IsNullOrWhiteSpace(adaValue) || string.IsNullOrWhiteSpace(sttText))
            return false;

        // Extract the street-name portion (remove numbers, punctuation)
        static string NormalizeForComparison(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z ]", " ").Trim();

        var adaNorm = NormalizeForComparison(adaValue);
        var sttNorm = NormalizeForComparison(sttText);

        // If Ada's value is fully contained in the STT, no mismatch
        if (sttNorm.Contains(adaNorm) || adaNorm.Contains(sttNorm))
            return false;

        // Word-level check: if Ada's street words appear in STT, it's fine
        var adaWords = adaNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToArray(); // Skip tiny words like "a", "to"
        var sttWords = new HashSet<string>(sttNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (adaWords.Length == 0) return false;

        // If fewer than half of Ada's significant words appear in STT, flag it
        var matchCount = adaWords.Count(w => sttWords.Contains(w));
        return matchCount < adaWords.Length / 2.0;
    }

    private string? DetectAddressDiscrepancy(FareResult result)
    {
        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(_booking.Pickup) && !string.IsNullOrWhiteSpace(result.PickupStreet))
        {
            if (!AddressContainsStreet(_booking.Pickup, result.PickupStreet))
                issues.Add($"The pickup was '{_booking.Pickup}' but the system resolved it to '{result.PickupStreet}' which appears to be a different location.");
        }
        if (!string.IsNullOrWhiteSpace(_booking.Destination) && !string.IsNullOrWhiteSpace(result.DestStreet))
        {
            if (!AddressContainsStreet(_booking.Destination, result.DestStreet))
                issues.Add($"The destination was '{_booking.Destination}' but the system resolved it to '{result.DestStreet}' which appears to be a different location.");
        }
        return issues.Count > 0 ? string.Join(" ", issues) : null;
    }

    private static bool AddressContainsStreet(string rawInput, string geocodedStreet)
    {
        static string Norm(string s) => System.Text.RegularExpressions.Regex
            .Replace(s.ToLowerInvariant(), @"[^a-z ]", " ").Trim();
        var rawNorm = Norm(rawInput);
        var streetNorm = Norm(geocodedStreet);
        if (rawNorm.Contains(streetNorm) || streetNorm.Contains(rawNorm)) return true;
        var streetWords = streetNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        var rawWords = new HashSet<string>(rawNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (streetWords.Length == 0) return true;
        var matchCount = streetWords.Count(w => rawWords.Contains(w));
        return matchCount >= Math.Ceiling(streetWords.Length / 2.0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await EndAsync("disposed");

        _aiClient.OnAudio -= HandleAiAudio;
        _aiClient.OnToolCall -= HandleToolCallAsync;

        OnEnded = null;
        OnBookingUpdated = null;
        OnTranscript = null;
        OnAudioOut = null;
        OnBargeIn = null;

        _booking.Reset();
        Interlocked.Exchange(ref _bookTaxiCompleted, 0);

        if (_aiClient is IAsyncDisposable disposableAi)
        {
            try { await disposableAi.DisposeAsync(); }
            catch { }
        }

        _logger.LogInformation("[{SessionId}] 🧹 CallSession fully disposed", SessionId);
    }
}
