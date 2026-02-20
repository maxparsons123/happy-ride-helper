using AdaSdkModel.Ai;
using AdaSdkModel.Config;
using AdaSdkModel.Services;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

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
    private readonly AdaSdkModel.Services.SumUpService? _sumUp;

    private readonly BookingState _booking = new();
    private readonly Audio.ALawThinningFilter? _thinningFilter;

    private int _disposed;
    private int _active;
    private int _bookTaxiCompleted;
    private long _lastAdaFinishedAt;

    // ── STAGE-AWARE INTENT GUARD ──
    private volatile BookingStage _currentStage = BookingStage.Greeting;
    private string? _lastUserTranscript;
    private int _intentGuardFiring; // prevents re-entrant guard execution

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
        bool icabbiEnabled = false,
        AdaSdkModel.Services.SumUpService? sumUp = null)
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
        _sumUp = sumUp;

        _booking.CallerPhone = callerId;

        // Init thinning filter if configured
        if (settings.Audio.ThinningAlpha > 0.01f)
            _thinningFilter = new Audio.ALawThinningFilter(settings.Audio.ThinningAlpha);

        // Wire up AI client events
        _aiClient.OnAudio += HandleAiAudio;
        _aiClient.OnToolCall += HandleToolCallAsync;
        _aiClient.OnEnded += reason => _ = EndAsync(reason);
        _aiClient.OnTranscript += (role, text) =>
        {
            OnTranscript?.Invoke(role, text);

            // Track user transcripts for intent guard
            if (role == "user" && !string.IsNullOrWhiteSpace(text))
            {
                _lastUserTranscript = text;
            }
        };

        _aiClient.OnBargeIn += () => OnBargeIn?.Invoke();

        // ── STAGE-AWARE WATCHDOG: Provide contextual re-prompts instead of generic "[SILENCE]" ──
        _aiClient.NoReplyContextProvider = () =>
        {
            return _currentStage switch
            {
                BookingStage.Greeting => string.IsNullOrWhiteSpace(_booking.Name)
                    ? "The caller has not provided their name yet. Ask again: What is your name?"
                    : $"The caller's name is {_booking.Name}. Ask where they'd like to be picked up from.",
                BookingStage.CollectingPickup => $"The pickup address has not been provided yet. Ask: Where would you like to be picked up from?",
                BookingStage.CollectingDestination => $"The destination has NOT been provided yet. The pickup is '{_booking.Pickup ?? "unknown"}'. Ask: Where would you like to go? Do NOT mention any fare or price — the destination is still missing.",
                BookingStage.CollectingPassengers => "The number of passengers has not been provided yet. Ask: How many passengers will be traveling?",
                BookingStage.CollectingTime => "The pickup time has not been provided yet. Ask: When would you like to be picked up?",
                BookingStage.FareCalculating => "The fare is being calculated. Tell the caller: I'm still checking those addresses, please hold on a moment. Do NOT invent or guess any fare amount.",
                BookingStage.FarePresented => "A fare has been presented. Ask the caller: Would you like me to go ahead and book that?",
                BookingStage.Disambiguation => "We are waiting for the caller to choose from the address options. Repeat the options or ask: Which one was it?",
                _ => null // Use default generic re-prompt
            };
        };

        // ── INTENT GUARD: After AI finishes a response, check if it missed a critical tool call ──
        _aiClient.OnResponseCompleted += () =>
        {
            if (string.IsNullOrWhiteSpace(_lastUserTranscript)) return;

            var intent = IntentGuard.Resolve(_currentStage, _lastUserTranscript);
            if (intent == IntentGuard.ResolvedIntent.None) return;

            // Only fire if the AI didn't already handle it via tool call
            _ = EnforceIntentAsync(intent, _lastUserTranscript);
            _lastUserTranscript = null; // Consume — don't fire twice
        };

        // SAFETY NET: if Ada says goodbye but book_taxi was never called, auto-dispatch
        _aiClient.OnGoodbyeWithoutBooking += () =>
        {
            if (Volatile.Read(ref _bookTaxiCompleted) == 0
                && _booking.Fare != null
                && !string.IsNullOrWhiteSpace(_booking.Pickup)
                && !string.IsNullOrWhiteSpace(_booking.Destination))
            {
                _logger.LogWarning("[{SessionId}] 🚨 SAFETY NET: Goodbye detected but book_taxi never called — auto-dispatching", SessionId);
                _ = AutoDispatchOnGoodbyeAsync();
            }
        };
    }

    private async Task AutoDispatchOnGoodbyeAsync()
    {
        if (Interlocked.CompareExchange(ref _bookTaxiCompleted, 1, 0) == 1)
            return; // Already booked by another path

        _booking.Confirmed = true;
        _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _aiClient.SetAwaitingConfirmation(false);
        OnBookingUpdated?.Invoke(_booking.Clone());

        _logger.LogInformation("[{SessionId}] ✅ SAFETY NET booked: {Ref} ({Pickup} → {Dest})",
            SessionId, _booking.BookingRef, _booking.Pickup, _booking.Destination);

        var bookingSnapshot = _booking.Clone();
        var callerId = CallerId;
        var sessionId = SessionId;

        try
        {
            await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
            await _dispatcher.SendWhatsAppAsync(callerId);
            await SaveCallerHistoryAsync(bookingSnapshot, callerId);

            if (_icabbiEnabled && _icabbi != null)
            {
                var icabbiResult = await _icabbi.CreateAndDispatchAsync(bookingSnapshot, _settings.Icabbi.SiteId, callerPhoneOverride: callerId);
                if (icabbiResult.Success)
                    _logger.LogInformation("[{SessionId}] 🚕 iCabbi (safety net): {JourneyId}", sessionId, icabbiResult.JourneyId);
                else
                    _logger.LogWarning("[{SessionId}] ⚠️ iCabbi (safety net) failed: {Msg}", sessionId, icabbiResult.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Safety net dispatch error", sessionId);
        }
    }

    // =========================
    // INTENT GUARD ENFORCEMENT
    // =========================
    /// <summary>
    /// Called when the AI finishes a response but may have missed a critical tool call.
    /// Checks if the resolved intent requires forced action.
    /// </summary>
    private async Task EnforceIntentAsync(IntentGuard.ResolvedIntent intent, string userText)
    {
        if (Interlocked.CompareExchange(ref _intentGuardFiring, 1, 0) == 1)
            return; // Already firing

        try
        {
            switch (intent)
            {
                case IntentGuard.ResolvedIntent.ConfirmBooking:
                    // Only enforce if book_taxi hasn't been called yet
                    if (Volatile.Read(ref _bookTaxiCompleted) == 0 && _booking.Fare != null)
                    {
                        _logger.LogWarning("[{SessionId}] 🛡️ INTENT GUARD: User confirmed fare but AI didn't call book_taxi — forcing confirmation",
                            SessionId);

                        // Wait briefly in case the AI is about to call book_taxi
                        await Task.Delay(1500);
                        if (Volatile.Read(ref _bookTaxiCompleted) == 1) break; // AI caught up

                        var result = await HandleBookTaxiAsync(new Dictionary<string, object?>
                        {
                            ["action"] = "confirmed",
                            ["caller_name"] = _booking.Name,
                            ["pickup"] = _booking.Pickup,
                            ["destination"] = _booking.Destination,
                            ["passengers"] = _booking.Passengers,
                        });

                        _logger.LogInformation("[{SessionId}] 🛡️ INTENT GUARD: Forced book_taxi result: {Result}",
                            SessionId, System.Text.Json.JsonSerializer.Serialize(result));

                        // Tell Ada the booking is done — speak the closing script
                        if (_booking.BookingRef != null)
                        {
                            _currentStage = BookingStage.AnythingElse;
                            await _aiClient.InjectMessageAndRespondAsync(
                                $"[BOOKING CONFIRMED BY SYSTEM] Reference: {_booking.BookingRef}. " +
                                "Tell the caller their booking reference, then ask if they need anything else. " +
                                "If they say no, say the FINAL CLOSING script and call end_call.");
                        }
                    }
                    break;

                case IntentGuard.ResolvedIntent.RejectFare:
                    _logger.LogInformation("[{SessionId}] 🛡️ INTENT GUARD: User rejected fare — staying in modification flow", SessionId);
                    // AI should handle this naturally — just ensure stage stays at FarePresented
                    // so next affirmative after re-quote triggers correctly
                    break;

                case IntentGuard.ResolvedIntent.EndCall:
                    if (Volatile.Read(ref _bookTaxiCompleted) == 1)
                    {
                        _logger.LogInformation("[{SessionId}] 🛡️ INTENT GUARD: User wants to end call after booking — ensuring goodbye", SessionId);
                        _currentStage = BookingStage.Ending;
                        // Let Ada say goodbye naturally, but if she doesn't call end_call,
                        // the goodbye safety net will catch it
                    }
                    break;

                case IntentGuard.ResolvedIntent.NewBooking:
                    _logger.LogInformation("[{SessionId}] 🛡️ INTENT GUARD: User wants another booking", SessionId);
                    _currentStage = BookingStage.CollectingPickup;
                    // Reset for new booking
                    _booking.Reset();
                    _booking.CallerPhone = CallerId;
                    Interlocked.Exchange(ref _bookTaxiCompleted, 0);
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    _pickupDisambiguated = true;
                    _destDisambiguated = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Intent guard error", SessionId);
        }
        finally
        {
            Interlocked.Exchange(ref _intentGuardFiring, 0);
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _active, 1) == 1)
            return;

        _logger.LogInformation("[{SessionId}] Starting G.711 session for {CallerId}", SessionId, CallerId);

        // Step 1: Load caller history BEFORE connecting so Ada knows the caller's name from the start
        string? callerHistory = null;
        try
        {
            callerHistory = await LoadCallerHistoryAsync(CallerId);
            if (callerHistory != null)
                _logger.LogInformation("[{SessionId}] 📋 Caller history loaded for {CallerId}: name={Name}", SessionId, CallerId, _booking.Name ?? "(none)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Caller history lookup failed (non-fatal)", SessionId);
        }

        // Step 2: Connect to OpenAI (session configured, event loops started, but NO greeting yet)
        await _aiClient.ConnectAsync(CallerId, ct);

        // Step 3: Inject caller history BEFORE greeting so Ada knows the caller's name
        if (callerHistory != null)
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
            sb.AppendLine("[CALLER HISTORY] This is a returning caller. This data is for REFERENCE ONLY.");
            sb.AppendLine("⚠️ DO NOT auto-fill ANY booking fields from this history. ALWAYS ask the caller explicitly for pickup, destination, passengers, and time.");
            sb.AppendLine("⚠️ This history may ONLY be used to resolve vague phrases like 'the usual', 'same place', or 'home'. Otherwise IGNORE it completely.");

            if (caller.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(nameEl.GetString()))
            {
                sb.AppendLine($"  Known name: {nameEl.GetString()}");
                sb.AppendLine($"  You may skip asking for their name and greet them by name.");
                if (string.IsNullOrEmpty(_booking.Name))
                    _booking.Name = nameEl.GetString();
            }

            if (caller.TryGetProperty("total_bookings", out var tb))
                sb.AppendLine($"  Total previous bookings: {tb.GetInt32()}");

            if (caller.TryGetProperty("last_pickup", out var lp) && lp.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(lp.GetString()))
                sb.AppendLine($"  Last pickup (reference only): {lp.GetString()}");

            if (caller.TryGetProperty("last_destination", out var ld) && ld.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(ld.GetString()))
                sb.AppendLine($"  Last destination (reference only): {ld.GetString()}");

            var allAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (caller.TryGetProperty("pickup_addresses", out var pickups) && pickups.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var a in pickups.EnumerateArray())
                    if (a.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(a.GetString()))
                        allAddresses.Add(a.GetString()!);

            if (caller.TryGetProperty("dropoff_addresses", out var dropoffs) && dropoffs.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var a in dropoffs.EnumerateArray())
                    if (a.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(a.GetString()))
                        allAddresses.Add(a.GetString()!);

            // Also add last_pickup / last_destination (already printed above) to the programmatic set
            if (!string.IsNullOrWhiteSpace(lp.GetString())) allAddresses.Add(lp.GetString()!);
            if (!string.IsNullOrWhiteSpace(ld.GetString())) allAddresses.Add(ld.GetString()!);

            // Store for programmatic city-context resolution in the city guard
            _callerKnownAddresses.Clear();
            _callerKnownAddresses.AddRange(allAddresses);

            if (allAddresses.Count > 0)
            {
                sb.AppendLine($"  Known addresses (reference only — {allAddresses.Count}):");
                var i = 1;
                foreach (var addr in allAddresses.Take(15))
                    sb.AppendLine($"    {i++}. {addr}");
            }

            sb.AppendLine("  RULES: ONLY use these addresses when the caller explicitly says 'the usual', 'same place', 'same as last time', or 'home'. In ALL other cases, you MUST collect the address fresh from the caller. NEVER pre-fill or assume addresses.");

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

        _logger.LogInformation("[{SessionId}] Ending session: {Reason}", SessionId, reason);
        await _aiClient.DisconnectAsync();
        OnEnded?.Invoke(this, reason);
    }

    public void ProcessInboundAudio(byte[] alawRtp)
    {
        if (!IsActive || alawRtp.Length == 0) return;
        _aiClient.SendAudio(alawRtp);
    }

    public byte[]? GetOutboundFrame() => null;

    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _aiClient.NotifyPlayoutComplete();
    }

    // =========================
    // BOOKING STATE INJECTION
    // =========================
    private async Task InjectBookingStateAsync(string? interpretation = null, List<string>? sttCorrections = null)
    {
        var sdk = _aiClient;

        var sb = new System.Text.StringBuilder();

        // Prepend any STT house-number corrections FIRST so the AI updates its memory
        // before reading the authoritative booking state below.
        if (sttCorrections != null && sttCorrections.Count > 0)
        {
            sb.AppendLine("[STT CORRECTION — CRITICAL] Speech recognition misheard a house number.");
            sb.AppendLine("You MUST update your internal memory with the corrected values below.");
            sb.AppendLine("Do NOT use the value you originally put in your tool call — it was wrong.");
            foreach (var correction in sttCorrections)
                sb.AppendLine($"  ⚠️ {correction}");
            sb.AppendLine("The corrected values are reflected in the [BOOKING STATE] below. Use ONLY those values in all future tool calls.");
            sb.AppendLine();
        }

        sb.AppendLine("[BOOKING STATE] Current booking data (ground truth):");
        sb.AppendLine($"  Name: {(_booking.Name != null ? $"{_booking.Name} ✓" : "(not yet collected)")}");
        sb.AppendLine($"  Pickup: {(_booking.Pickup != null ? $"{_booking.Pickup} ✓" : "(not yet collected)")}");
        sb.AppendLine($"  Destination: {(_booking.Destination != null ? $"{_booking.Destination} ✓" : "(not yet collected)")}");
        sb.AppendLine($"  Passengers: {(_booking.Passengers.HasValue ? $"{_booking.Passengers} ✓" : "(not yet collected)")}");
        sb.AppendLine($"  Time: {(_booking.PickupTime != null ? $"{_booking.PickupTime} ✓" : "(not yet collected)")}");
        sb.AppendLine($"  Vehicle: {_booking.VehicleType}");
        sb.AppendLine();
        sb.AppendLine("⚠️ ADDRESS CORRECTION RULE: If the caller provides TWO address utterances in a row for the same field (e.g. destination),");
        sb.AppendLine("   ALWAYS use the MOST RECENT one. The first may be a speech recognition error or a self-correction.");
        sb.AppendLine("   When calling sync_booking_data, use whatever the caller said LAST for each field.");

        if (_booking.Fare != null)
            sb.AppendLine($"  Fare: {_booking.Fare}");
        if (_booking.Eta != null)
            sb.AppendLine($"  ETA: {_booking.Eta}");

        if (!string.IsNullOrWhiteSpace(interpretation))
            sb.AppendLine($"  Last interpretation: {interpretation}");

        int missing = 0;
        if (_booking.Name == null) missing++;
        if (_booking.Pickup == null) missing++;
        if (_booking.Destination == null) missing++;
        if (!_booking.Passengers.HasValue) missing++;
        if (_booking.PickupTime == null) missing++;

        if (missing > 0)
            sb.AppendLine($"  ⚠️ {missing} field(s) still needed before fare calculation.");
        else
            sb.AppendLine("  ✅ All fields collected — fare calculation will trigger automatically.");

        sb.AppendLine("IMPORTANT: If the caller corrects ANY field, update it immediately via sync_booking_data. The LATEST value is always the truth.");

        try
        {
            await sdk.InjectSystemMessageAsync(sb.ToString());
            if (sttCorrections?.Count > 0)
                _logger.LogInformation("[{SessionId}] 📋 Booking state + {Count} STT correction(s) injected", SessionId, sttCorrections.Count);
            else
                _logger.LogDebug("[{SessionId}] 📋 Booking state injected", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[{SessionId}] ⚠️ Failed to inject booking state: {Error}", SessionId, ex.Message);
        }
    }

    private void HandleAiAudio(byte[] alawFrame)
    {
        // Pure A-law passthrough — no filters, no gain manipulation on compressed bytes.
        // OpenAI sends native G.711 A-law; any DSP on logarithmic bytes degrades quality.
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

    /// <summary>
    /// Known addresses from the caller's Supabase history (pickup + dropoff combined).
    /// Populated in LoadCallerHistoryAsync and used to infer city context when the AI
    /// provides a city-less destination like "52A David Road".
    /// </summary>
    private readonly List<string> _callerKnownAddresses = new();

    // Fare sanity guard: track retries so legitimate long-distance trips aren't blocked forever
    private int _fareSanityAlertCount;
    private string? _lastSanityAlertDestination;
    private volatile bool _fareSanityActive; // blocks book_taxi while sanity alert is shown
    private volatile bool _disambiguationPerformed; // skip fare sanity after disambiguation resolved

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
        if (!string.IsNullOrWhiteSpace(_aiClient.LastUserTranscript))
        {
            var sttText = _aiClient.LastUserTranscript;
            
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

        // Track STT house-number corrections so we can notify the AI to update its internal memory
        var sttCorrections = new List<string>();

        if (args.TryGetValue("pickup", out var p))
        {
            var raw = p?.ToString();
            var incoming = NormalizeHouseNumber(raw, "pickup");
            // If normalisation changed the value, record the correction for AI context injection
            if (incoming != raw && raw != null)
                sttCorrections.Add($"STT CORRECTION (pickup): you sent '{raw}' but the correct value is '{incoming}'. Update your memory: pickup = '{incoming}'.");
            // Safeguard: store previous interpretation before overwriting
            if (!string.IsNullOrWhiteSpace(_booking.Pickup) && _booking.Pickup != incoming)
            {
                if (!_booking.PreviousPickups.Contains(_booking.Pickup))
                    _booking.PreviousPickups.Insert(0, _booking.Pickup);
                _logger.LogInformation("[{SessionId}] 📝 Pickup history: [{History}] → new: '{New}'",
                    SessionId, string.Join(" | ", _booking.PreviousPickups), incoming);
            }

            // ── HOUSE-NUMBER RESCUE (pickup) ─────────────────────────────────────────
            var incomingPickupComponents = Services.AddressParser.ParseAddress(incoming);
            if (!incomingPickupComponents.HasHouseNumber && !string.IsNullOrWhiteSpace(incoming))
            {
                var allPrevPickups = new List<string>();
                if (!string.IsNullOrWhiteSpace(_booking.Pickup)) allPrevPickups.Add(_booking.Pickup);
                allPrevPickups.AddRange(_booking.PreviousPickups);

                foreach (var prev in allPrevPickups)
                {
                    var prevComponents = Services.AddressParser.ParseAddress(prev);
                    if (prevComponents.HasHouseNumber)
                    {
                        var incomingStreet = incomingPickupComponents.StreetName.ToLowerInvariant().Trim();
                        var prevStreet = prevComponents.StreetName.ToLowerInvariant().Trim();
                        if (!string.IsNullOrWhiteSpace(incomingStreet) && !string.IsNullOrWhiteSpace(prevStreet)
                            && (incomingStreet.Contains(prevStreet) || prevStreet.Contains(incomingStreet)))
                        {
                            var prefix = string.IsNullOrWhiteSpace(prevComponents.FlatOrUnit)
                                ? prevComponents.HouseNumber
                                : $"{prevComponents.FlatOrUnit} {prevComponents.HouseNumber}";
                            var rescued = $"{prefix} {incoming}";
                            _logger.LogInformation("[{SessionId}] 🏠 RESCUE: Re-attached pickup house number '{Num}' → '{Rescued}'",
                                SessionId, prefix, rescued);
                            sttCorrections.Add($"HOUSE NUMBER RESCUE: The pickup was updated to '{incoming}' (no house number) but history shows '{prev}' was previously confirmed. The house number '{prefix}' has been automatically re-attached. Updated pickup = '{rescued}'. Do NOT ask for the house number again.");
                            incoming = rescued;
                            break;
                        }
                    }
                }
            }
            // ─────────────────────────────────────────────────────────────────────────

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
            var raw = d?.ToString();
            var incoming = NormalizeHouseNumber(raw, "destination");
            // If normalisation changed the value, record the correction for AI context injection
            if (incoming != raw && raw != null)
                sttCorrections.Add($"STT CORRECTION (destination): you sent '{raw}' but the correct value is '{incoming}'. Update your memory: destination = '{incoming}'.");
            // Safeguard: store previous interpretation before overwriting
            if (!string.IsNullOrWhiteSpace(_booking.Destination) && _booking.Destination != incoming)
            {
                if (!_booking.PreviousDestinations.Contains(_booking.Destination))
                    _booking.PreviousDestinations.Insert(0, _booking.Destination);
                _logger.LogInformation("[{SessionId}] 📝 Dest history: [{History}] → new: '{New}'",
                    SessionId, string.Join(" | ", _booking.PreviousDestinations), incoming);
            }

            // ── HOUSE-NUMBER RESCUE ──────────────────────────────────────────────────
            // If the AI just dropped a house number that was present in a previous sync
            // (e.g. caller said "Warwick Road, Coventry" to confirm the city after the
            // geocoder couldn't resolve "1214A Warwick Road"), re-attach the number from
            // history so we don't re-trigger the house-number guard unnecessarily.
            var incomingComponents = Services.AddressParser.ParseAddress(incoming);
            if (!incomingComponents.HasHouseNumber && !string.IsNullOrWhiteSpace(incoming))
            {
                // Search current destination and all previous versions for a matching house number
                var allPrevDests = new List<string>();
                if (!string.IsNullOrWhiteSpace(_booking.Destination)) allPrevDests.Add(_booking.Destination);
                allPrevDests.AddRange(_booking.PreviousDestinations);

                foreach (var prev in allPrevDests)
                {
                    var prevComponents = Services.AddressParser.ParseAddress(prev);
                    if (prevComponents.HasHouseNumber)
                    {
                        // Check that the street name is the same or one contains the other
                        var incomingStreet = incomingComponents.StreetName.ToLowerInvariant().Trim();
                        var prevStreet = prevComponents.StreetName.ToLowerInvariant().Trim();
                        if (!string.IsNullOrWhiteSpace(incomingStreet) && !string.IsNullOrWhiteSpace(prevStreet)
                            && (incomingStreet.Contains(prevStreet) || prevStreet.Contains(incomingStreet)))
                        {
                            // Re-attach: prepend the house number (and flat prefix if any) to the incoming address
                            var prefix = string.IsNullOrWhiteSpace(prevComponents.FlatOrUnit)
                                ? prevComponents.HouseNumber
                                : $"{prevComponents.FlatOrUnit} {prevComponents.HouseNumber}";
                            var rescued = $"{prefix} {incoming}";
                            _logger.LogInformation("[{SessionId}] 🏠 RESCUE: Re-attached house number '{Num}' from history → '{Rescued}'",
                                SessionId, prefix, rescued);
                            sttCorrections.Add($"HOUSE NUMBER RESCUE: The destination was updated to '{incoming}' (no house number) but history shows '{prev}' was previously confirmed. The house number '{prefix}' has been automatically re-attached. Updated destination = '{rescued}'. Do NOT ask for the house number again.");
                            incoming = rescued;
                            break;
                        }
                    }
                }
            }
            // ─────────────────────────────────────────────────────────────────────────

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
            _booking.Passengers = pn;
            // Auto-recommend vehicle type based on passenger count (unless explicitly set)
            if (!args.ContainsKey("vehicle_type"))
                _booking.VehicleType = BookingState.RecommendVehicle(pn);
        }
        if (args.TryGetValue("pickup_time", out var pt))
        {
            _booking.PickupTime = pt?.ToString();
            _booking.ScheduledAt = BookingState.ParsePickupTimeToDateTime(_booking.PickupTime);
            _logger.LogInformation("[{SessionId}] ⏰ pickup_time raw='{Raw}' → ScheduledAt={Scheduled}",
                SessionId, _booking.PickupTime, _booking.ScheduledAt?.ToString("o") ?? "ASAP");
        }
        if (args.TryGetValue("vehicle_type", out var vt) && !string.IsNullOrWhiteSpace(vt?.ToString()))
            _booking.VehicleType = vt.ToString()!;

        // Extract interpretation if provided
        string? interpretation = null;
        if (args.TryGetValue("interpretation", out var interp))
            interpretation = interp?.ToString();

        _logger.LogInformation("[{SessionId}] ⚡ Sync: Name={Name}, Pickup={Pickup}, Dest={Dest}, Pax={Pax}, Vehicle={Vehicle}",
            SessionId, _booking.Name ?? "?", _booking.Pickup ?? "?", _booking.Destination ?? "?", _booking.Passengers, _booking.VehicleType);
        if (!string.IsNullOrWhiteSpace(interpretation))
            _logger.LogInformation("[{SessionId}] 💭 Interpretation: {Interpretation}", SessionId, interpretation);

        OnBookingUpdated?.Invoke(_booking.Clone());

        // ── BOOKING STATE INJECTION ──
        // Inject current booking state into conversation so Ada always has ground truth.
        // Also pass any STT corrections so Ada updates its internal memory.
        _ = InjectBookingStateAsync(interpretation, sttCorrections.Count > 0 ? sttCorrections : null);

        // If transcript mismatch was detected, return warning to Ada
        if (mismatchWarning != null)
            return new { success = true, warning = mismatchWarning };

        // If name is still missing, tell Ada to ask for it
        if (string.IsNullOrWhiteSpace(_booking.Name))
            return new { success = true, warning = "Name is required before booking. Ask the caller for their name." };

        // ── SAME-ADDRESS GUARD ──
        // Reject before geocoding if Ada submitted identical raw strings for pickup and destination.
        // This happens when the caller's destination was misheard as the pickup or Ada re-used it.
        if (!string.IsNullOrWhiteSpace(_booking.Pickup) && !string.IsNullOrWhiteSpace(_booking.Destination))
        {
            var normPickup = _booking.Pickup.Trim().ToLowerInvariant();
            var normDest   = _booking.Destination.Trim().ToLowerInvariant();
            if (normPickup == normDest)
            {
                _logger.LogWarning("[{SessionId}] ⚠ Same-address submitted: pickup == destination ('{Addr}'). Rejecting and re-asking destination.", SessionId, _booking.Pickup);
                // Clear destination so it must be re-collected
                _booking.Destination = null;
                _booking.DestLat = _booking.DestLon = null;
                _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                _booking.Fare = null;
                _booking.Eta = null;
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                return new
                {
                    success = false,
                    warning = $"DESTINATION ERROR: The destination you submitted ('{_booking.Pickup}') is the same as the pickup address. " +
                              "You MUST ask the caller: 'Where would you like to go?' and collect a DIFFERENT destination before proceeding."
                };
            }
        }
        // If pickup is a street-type address (Road, Avenue, Close, etc.) but has no house number,
        // instruct Ada to ask the caller for it before proceeding.
        if (!string.IsNullOrWhiteSpace(_booking.Pickup) && Services.AddressParser.RequiresHouseNumber(_booking.Pickup))
        {
            _logger.LogWarning("[{SessionId}] 🏠 Pickup missing house number: '{Pickup}'", SessionId, _booking.Pickup);
            // Clear fare so it recalculates once number is added
            _booking.Fare = null;
            _booking.Eta = null;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);
            return new
            {
                success = false,
                warning = $"HOUSE NUMBER REQUIRED: The pickup '{_booking.Pickup}' is a street address but has no house number. " +
                          "Ask the caller: 'What is the house number on that street?' " +
                          "Do NOT proceed to fare calculation until a house number is provided."
            };
        }

        // Same guard for destination
        if (!string.IsNullOrWhiteSpace(_booking.Destination) && Services.AddressParser.RequiresHouseNumber(_booking.Destination))
        {
            _logger.LogWarning("[{SessionId}] 🏠 Destination missing house number: '{Dest}'", SessionId, _booking.Destination);
            _booking.Fare = null;
            _booking.Eta = null;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);
            return new
            {
                success = false,
                warning = $"HOUSE NUMBER REQUIRED: The destination '{_booking.Destination}' is a street address but has no house number. " +
                          "Ask the caller: 'What is the house number on that street?' " +
                          "Do NOT proceed to fare calculation until a house number is provided."
            };
        }

        // AUTO-TRIGGER: When all 5 fields are filled, automatically calculate fare
        // This matches the v3.9 prompt: "When sync_booking_data is called with all 5 fields filled,
        // the system will AUTOMATICALLY validate the addresses and calculate the fare."
        bool allFieldsFilled = !string.IsNullOrWhiteSpace(_booking.Name)
            && !string.IsNullOrWhiteSpace(_booking.Pickup)
            && !string.IsNullOrWhiteSpace(_booking.Destination)
            && _booking.Passengers > 0
            && !string.IsNullOrWhiteSpace(_booking.PickupTime);

        // ── City Context Guard ──────────────────────────────────────────────────────
        // Before triggering fare calculation, ensure the destination has enough location
        // context so the geocoder can resolve it unambiguously.
        //
        // NEW Resolution order:
        //   1. If the destination already has a house number (e.g. "1214A Warwick Road"),
        //      it is specific enough — pass it bare to the geocoder first. The geocoder can
        //      often resolve a numbered address globally without a city hint. City inference
        //      from caller history is SKIPPED to avoid pinning the wrong locale
        //      (e.g. Birmingham trips wrongly enriched with "Coventry").
        //   2. If there is NO house number (e.g. "Warwick Road"), it's too vague — try
        //      the caller's address history to infer a city and silently enrich.
        //   3. If history has no match either, block and ask Ada to collect the city.
        if (allFieldsFilled && DestinationLacksCityContext(_booking.Destination))
        {
            var destRaw = _booking.Destination!;
            var destComponents = Services.AddressParser.ParseAddress(destRaw);

            if (destComponents.HasHouseNumber)
            {
                // House number present → geocoder can resolve this bare. Skip city inference.
                _logger.LogInformation("[{SessionId}] 🏠 Destination '{Dest}' has house number '{Num}' — skipping city inference, letting geocoder resolve",
                    SessionId, destRaw, destComponents.HouseNumber);
                // Fall through to fare calculation with the bare address
            }
            else
            {
                // No house number → vague street name. Try history, then block.
                var cityFromHistory = TryExtractCityFromHistory(destRaw);

                if (cityFromHistory != null)
                {
                    var enriched = $"{destRaw}, {cityFromHistory}";
                    _logger.LogInformation("[{SessionId}] 🏙️ Inferred city '{City}' from caller history for '{Dest}' → '{Enriched}'",
                        SessionId, cityFromHistory, destRaw, enriched);
                    _booking.Destination = enriched;
                    _ = _aiClient.InjectSystemMessageAsync(
                        $"[CITY CONTEXT RESOLVED] The destination '{destRaw}' matched a known address from the caller's history. " +
                        $"Update your memory: destination = '{enriched}'. Continue normally.");
                    // Fall through to fare calculation
                }
                else
                {
                    _logger.LogWarning("[{SessionId}] 🏙️ Destination '{Dest}' has no house number, no city, and no history match — asking for city", SessionId, destRaw);
                    return new
                    {
                        success = false,
                        warning = $"DESTINATION CITY REQUIRED: The destination '{destRaw}' does not include a city or area. " +
                                  $"Ask the caller: 'What city or area is {destRaw} in?' " +
                                  "Once they provide the city, call sync_booking_data again with the full destination including the city (e.g. 'Warwick Road, Coventry'). " +
                                  "Do NOT guess the city from the pickup address."
                    };
                }
            }
        }

        if (allFieldsFilled && Interlocked.CompareExchange(ref _fareAutoTriggered, 1, 0) == 0)
        {
            _currentStage = BookingStage.FareCalculating;
            _logger.LogInformation("[{SessionId}] 🚀 All fields filled — auto-triggering fare calculation (stage→FareCalculating)", SessionId);

            var (pickup, destination) = GetEnrichedAddresses();
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId, _booking.PickupTime,
                        spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                        spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
                    var completed = await Task.WhenAny(aiTask, Task.Delay(18000));

                    FareResult result;
                    if (completed == aiTask)
                    {
                        result = await aiTask;
                        bool localeRetrySuccess = false; // set true when house-number locale fallback resolves fare
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
                                _currentStage = BookingStage.Disambiguation;
                                // Switch to semantic VAD for disambiguation (caller choosing from options)
                                await _aiClient.SetVadModeAsync(useSemantic: true, eagerness: 0.5f);

                                await _aiClient.InjectMessageAndRespondAsync(
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

                                await _aiClient.InjectMessageAndRespondAsync(
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
                                // NeedsClarification=true but no alternatives provided.
                                // If this is a re-calc after a fare sanity alert, the user already confirmed —
                                // skip clarification and use Nominatim fallback (IsFareSane will bypass the insane fare).
                                if (_fareSanityAlertCount > 0)
                                {
                                    _logger.LogInformation("[{SessionId}] ⚡ Post-sanity re-calc: skipping disambiguation, using Nominatim fallback", sessionId);
                                    result = await _fareCalculator.CalculateAsync(pickup, destination, callerId);
                                    // Fall through to IsFareSane check below (which will bypass)
                                }
                                else
                                {
                                    // ── House-Number Locale Fallback (Pickup + Destination) ──────────────
                                    // Pass 1: Anchor pickup to locale city, keep destination bare.
                                    //         This handles the common case where the pickup lacks city.
                                    // Pass 2: If pass 1 still fails AND the destination also has a house
                                    //         number, anchor BOTH pickup and destination to the locale city.
                                    //         This handles cases like "1214A Warwick Road" where the geocoder
                                    //         resolves to a different city (e.g. Birmingham vs Coventry).
                                    var pickupComponents = Services.AddressParser.ParseAddress(pickup);
                                    var destComponents   = Services.AddressParser.ParseAddress(destination);
                                    var localeCity       = TryExtractCityFromHistory(pickup);

                                    bool retriedWithLocale = false;
                                    if (localeCity != null && pickupComponents.HasHouseNumber)
                                    {
                                        var retryPickup = !pickup.Contains(localeCity, StringComparison.OrdinalIgnoreCase)
                                            ? $"{pickup}, {localeCity}" : pickup;

                                        // ── Pass 1: bare destination ──────────────────────────────────────────
                                        if (retryPickup != pickup)
                                        {
                                            _logger.LogInformation("[{SessionId}] 🔄 Pickup locale fallback (pass 1): pickup='{Pickup}' (locale: {City}), dest stays bare='{Dest}'",
                                                sessionId, retryPickup, localeCity, destination);

                                            var retryTask = _fareCalculator.ExtractAndCalculateWithAiAsync(
                                                retryPickup, destination, callerId, _booking.PickupTime,
                                                spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                                                spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
                                            var retryCompleted = await Task.WhenAny(retryTask, Task.Delay(18000));

                                            if (retryCompleted == retryTask)
                                            {
                                                var retryResult = await retryTask;
                                                _logger.LogInformation("[{SessionId}] 📊 Locale-retry (pass 1): NeedsClarification={Clarif}, Fare={Fare}",
                                                    sessionId, retryResult.NeedsClarification, retryResult.Fare);

                                                if (!retryResult.NeedsClarification && !string.IsNullOrWhiteSpace(retryResult.Fare))
                                                {
                                                    result = retryResult;
                                                    retriedWithLocale = true;
                                                    localeRetrySuccess = true;
                                                    _logger.LogInformation("[{SessionId}] ✅ Locale fallback (pass 1) resolved: {Fare}, ETA: {Eta}",
                                                        sessionId, result.Fare, result.Eta);
                                                }
                                                else if (retryResult.NeedsClarification && (retryResult.PickupAlternatives?.Length > 0 || retryResult.DestAlternatives?.Length > 0))
                                                {
                                                    result = retryResult;
                                                    retriedWithLocale = true;
                                                    _logger.LogInformation("[{SessionId}] ✅ Locale fallback (pass 1) produced disambiguation options", sessionId);
                                                    var rPickupAlts = retryResult.PickupAlternatives ?? Array.Empty<string>();
                                                    var rDestAlts   = retryResult.DestAlternatives   ?? Array.Empty<string>();
                                                    if (rPickupAlts.Length > 0 || rDestAlts.Length > 0)
                                                    {
                                                        Interlocked.Exchange(ref _fareAutoTriggered, 0);
                                                        _booking.Pickup      = retryPickup;
                                                        _booking.Destination = destination;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                _logger.LogWarning("[{SessionId}] ⚠️ Locale fallback (pass 1) timed out", sessionId);
                                            }
                                        }

                                        // Pass 2 removed: anchoring the destination to the pickup's locale city
                                        // is wrong for cross-city trips (e.g. Coventry pickup → Birmingham dest).
                                        // The sanity guard now accepts inter-city trips within 50 miles.

                                    if (!retriedWithLocale || result.NeedsClarification)
                                    {
                                        _logger.LogWarning("[{SessionId}] ⚠️ NeedsClarification=true but no alternatives — asking caller for city/area", sessionId);

                                        var clarMsg = !string.IsNullOrWhiteSpace(result.ClarificationMessage)
                                            ? result.ClarificationMessage
                                            : "I couldn't verify that destination address. Could you repeat the full destination including the street name and city?";

                                        // Build context hints to help Ada ask the right question
                                        var pickupCityHint = !string.IsNullOrWhiteSpace(_booking.PickupCity)
                                            ? $"The pickup is in {_booking.PickupCity}."
                                            : !string.IsNullOrWhiteSpace(pickup) && DestinationLacksCityContext(pickup)
                                                ? ""
                                                : $"The pickup address is '{pickup}'.";

                                        await _aiClient.InjectMessageAndRespondAsync(
                                            $"[ADDRESS CLARIFICATION NEEDED] The destination '{_booking.Destination}' could not be verified by the geocoder — the street name may be wrong or unclear. " +
                                            $"{pickupCityHint} " +
                                            "IMPORTANT: Before asking the caller anything, first check the conversation history — did the caller already provide a DIFFERENT or CORRECTED address for the destination? " +
                                            "If yes, call sync_booking_data immediately with that corrected address (do NOT ask again). " +
                                            $"If no correction is found, ask the caller: \"{clarMsg}\" " +
                                            "Once they confirm or correct it, call sync_booking_data again with the full corrected destination.");

                                        Interlocked.Exchange(ref _fareAutoTriggered, 0);
                                        return;
                                    }
                                }
                            }

                            // Has disambiguation alternatives — already handled above, exit.
                            // BUT: if the locale retry resolved the fare, do NOT return — fall through to fare presentation.
                            if (!localeRetrySuccess)
                            {
                                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                                return;
                            }
                        }

                        // Check if there are pending destination alternatives from a previous round
                        // (pickup was ambiguous, now resolved, but dest still needs clarification)
                        if (_pendingDestAlternatives != null && _pendingDestAlternatives.Length > 0 && !_destDisambiguated)
                        {
                            _activeDestAlternatives = _pendingDestAlternatives;
                            var destAltsList = string.Join(", ", _pendingDestAlternatives);
                            _logger.LogInformation("[{SessionId}] 🔄 Now resolving pending destination disambiguation: {Alts}", sessionId, destAltsList);

                            await _aiClient.InjectMessageAndRespondAsync(
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
                        EnrichFallbackResultStructuredFields(result, pickup, destination);
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

                        await _aiClient.InjectMessageAndRespondAsync(
                                "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard or the city could not be determined. " +
                                "Ask the caller to confirm their DESTINATION address AND which city or area they are in. " +
                                "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going, and which city you're in?\" " +
                                "When they respond, call sync_booking_data with the destination INCLUDING the city name (e.g. '7 Russell Street, Coventry').");
                        return;
                    }

                    // ADDRESS DISCREPANCY CHECK: Verify geocoded result matches raw input
                    var discrepancy = DetectAddressDiscrepancy(result);
                    if (discrepancy != null)
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Address discrepancy detected: {Msg}", sessionId, discrepancy);
                        Interlocked.Exchange(ref _fareAutoTriggered, 0);

                        await _aiClient.InjectMessageAndRespondAsync(
                            $"[ADDRESS DISCREPANCY] {discrepancy} " +
                            "Ask the caller to confirm or repeat their address. " +
                            "When they respond, call sync_booking_data with the corrected address.");
                        return;
                    }

                    ApplyFareResult(result);

                    // ── iCabbi FARE QUOTE (Phase 1 of 2) ──────────────────────────────────────
                    // Call iCabbi quote endpoint → get official fare+ETA → override Gemini estimate.
                    // The quoted fare is stored in _booking.Fare and will be used verbatim in the
                    // confirmed booking payload sent in Phase 2 (CreateAndDispatchAsync).
                    if (_icabbiEnabled && _icabbi != null)
                    {
                        _logger.LogInformation("[{SessionId}] 🚕 [Phase 1] Requesting iCabbi fare quote (siteId={SiteId})", sessionId, _settings.Icabbi.SiteId);
                        var quote = await _icabbi.GetFareQuoteAsync(_booking, _settings.Icabbi.SiteId);
                        if (quote != null)
                        {
                            _logger.LogInformation("[{SessionId}] ✅ iCabbi quote: {OldFare} → {NewFare}, ETA: {Eta}",
                                sessionId, _booking.Fare, quote.FareFormatted, quote.EtaFormatted);
                            _booking.Fare = quote.FareFormatted;
                            _booking.Eta = quote.EtaFormatted;
                        }
                        else
                        {
                            _logger.LogWarning("[{SessionId}] ⚠️ iCabbi quote unavailable — using Gemini estimate ({Fare})", sessionId, _booking.Fare);
                        }
                    }

            _aiClient.SetAwaitingConfirmation(true);
            _currentStage = BookingStage.FarePresented;
            await _aiClient.SetVadModeAsync(useSemantic: false);
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (fare presented, awaiting yes/no) (stage→FarePresented)", sessionId);

            OnBookingUpdated?.Invoke(_booking.Clone());

            var spokenFare = FormatFareForSpeech(_booking.Fare);
            _logger.LogInformation("[{SessionId}] 💰 Auto fare ready: {Fare} ({Spoken}), ETA: {Eta}",
                        sessionId, _booking.Fare, spokenFare, _booking.Eta);

                    var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
                    var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

                    // Guard: geocoder resolved pickup and destination to the same address.
                    // Clear destination and force Ada to re-collect it — do NOT proceed to fare.
                    if (string.Equals(pickupAddr, destAddr, StringComparison.OrdinalIgnoreCase) && pickupAddr != "the address")
                    {
                        _logger.LogWarning("[{SessionId}] ⚠ Post-geocode same-address: pickup == destination ({Addr}). Clearing destination and re-asking.", sessionId, pickupAddr);
                        _booking.Destination = null;
                        _booking.DestLat = _booking.DestLon = null;
                        _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                        _booking.Fare = null; _booking.Eta = null;
                        Interlocked.Exchange(ref _fareAutoTriggered, 0);
                        _aiClient.SetAwaitingConfirmation(false);
                        _currentStage = BookingStage.CollectingDestination;
                        await _aiClient.InjectMessageAndRespondAsync(
                            "[DESTINATION ERROR] The verified pickup and destination resolved to the same location. " +
                            "Ask the caller where they want to go — their destination was not captured correctly. " +
                            "When they answer, call sync_booking_data with the corrected destination.");
                        return;
                    }

                    var timePart1 = FormatScheduledTimePart();
                    await _aiClient.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] Verified pickup: {pickupAddr}. Verified destination: {destAddr}. Fare: {spokenFare}{timePart1}. " +
                            $"Use ONLY these verified addresses when reading back to the caller — do NOT use the caller's raw words. Ask them to confirm the booking.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Auto fare calculation failed", sessionId);
                    _booking.Fare = "£8.00";
                    _booking.Eta = "8 minutes";
                    OnBookingUpdated?.Invoke(_booking.Clone());

                    var fallbackTimePart1 = FormatScheduledTimePart();
                    await _aiClient.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] The estimated fare is 8 pounds{fallbackTimePart1}. " +
                            "Read back the details to the caller and ask them to confirm.");
                }
            });

            return new { success = true, fare_calculating = true, message = "Fare is being calculated. Do NOT repeat any interjection—the system will inject the next step once address validation is complete." };
        }

        // AUTO VAD SWITCH: Determine what we're collecting next and switch mode
        _ = AutoSwitchVadForNextStepAsync();

        return new { success = true };
    }

    // =========================
    // AUTO VAD SWITCHING
    // =========================
    /// <summary>
    /// Automatically switches between semantic_vad (patient, for addresses) and server_vad (fast, for short replies)
    /// based on what the next required booking field is.
    /// </summary>
    private async Task AutoSwitchVadForNextStepAsync()
    {
        if (!_aiClient.IsConnected) return;

        // Determine the next missing field
        bool needsPickup = string.IsNullOrWhiteSpace(_booking.Pickup);
        bool needsDest = string.IsNullOrWhiteSpace(_booking.Destination);
        bool needsName = string.IsNullOrWhiteSpace(_booking.Name);
        bool needsPax = _booking.Passengers <= 0;
        bool needsTime = string.IsNullOrWhiteSpace(_booking.PickupTime);

        // Address fields → semantic VAD (patient, waits for complete thoughts)
        if (needsPickup)
        {
            _currentStage = BookingStage.CollectingPickup;
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SEMANTIC (collecting pickup) (stage→CollectingPickup)", SessionId);
            await _aiClient.SetVadModeAsync(useSemantic: true, eagerness: 0.2f);
        }
        else if (needsDest)
        {
            _currentStage = BookingStage.CollectingDestination;
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SEMANTIC (collecting destination) (stage→CollectingDestination)", SessionId);
            await _aiClient.SetVadModeAsync(useSemantic: true, eagerness: 0.2f);
        }
        // Short-answer fields (name, passengers, time) → server VAD (fast response)
        else if (needsName)
        {
            _currentStage = BookingStage.Greeting;
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (collecting name) (stage→Greeting)", SessionId);
            await _aiClient.SetVadModeAsync(useSemantic: false);
        }
        else if (needsPax)
        {
            _currentStage = BookingStage.CollectingPassengers;
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (collecting passengers) (stage→CollectingPassengers)", SessionId);
            await _aiClient.SetVadModeAsync(useSemantic: false);
        }
        else if (needsTime)
        {
            _currentStage = BookingStage.CollectingTime;
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (collecting time) (stage→CollectingTime)", SessionId);
            await _aiClient.SetVadModeAsync(useSemantic: false);
        }
        // All fields filled → fare calculating, then confirmation → server VAD (yes/no)
        else
        {
            _logger.LogInformation("[{SessionId}] 🔄 Auto-VAD → SERVER (awaiting confirmation)", SessionId);
            await _aiClient.SetVadModeAsync(useSemantic: false);
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
                return new { success = true, needs_disambiguation = false, message = $"Pickup updated to '{selected}'. Fare calculation in progress — wait SILENTLY for [FARE RESULT]. Do NOT ask for confirmation yet." };
            }

            _booking.Pickup = selected;
            _booking.PickupLat = _booking.PickupLon = null;
            _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
            _pickupDisambiguated = true;
            _activePickupAlternatives = null;
            Interlocked.Exchange(ref _fareAutoTriggered, 0);

            _disambiguationPerformed = true;
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
            return new { success = true, needs_disambiguation = false, message = "Pickup locked. Fare calculation in progress — wait SILENTLY for [FARE RESULT]. Do NOT ask for confirmation yet." };
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

            _disambiguationPerformed = true;
            _logger.LogInformation("[{SessionId}] 🔒 Destination LOCKED: {Destination}", SessionId, selected);

            OnBookingUpdated?.Invoke(_booking.Clone());
            _ = TriggerFareCalculationAsync();
            return new { success = true, needs_disambiguation = false, message = "Destination locked. Fare calculation in progress — wait SILENTLY for [FARE RESULT]. Do NOT ask for confirmation yet." };
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

    /// <summary>
    /// Enriches a raw address string with verified city context if geocoded data exists.
    /// Prevents redundant clarification when only one address changes mid-flow.
    /// </summary>
    private string EnrichWithVerifiedCity(string raw, string? verifiedCity)
    {
        if (string.IsNullOrWhiteSpace(verifiedCity)) return raw;
        // Don't append if the city is already in the raw string
        if (raw.Contains(verifiedCity, StringComparison.OrdinalIgnoreCase)) return raw;
        return $"{raw}, {verifiedCity}";
    }

    /// <summary>
    /// Returns pickup/destination strings enriched with verified city context
    /// so re-calculations don't lose locality for unchanged addresses.
    /// </summary>
    private (string pickup, string destination) GetEnrichedAddresses()
    {
        var pickup = _booking.Pickup!;
        var destination = _booking.Destination!;

        // If pickup has verified geocoded city, use it — otherwise fall back to history inference
        if (_booking.PickupLat.HasValue && _booking.PickupLat != 0)
        {
            pickup = EnrichWithVerifiedCity(pickup, _booking.PickupCity);
        }
        else if (DestinationLacksCityContext(pickup))
        {
            // Enrich pickup with caller's history city ONLY if the pickup has no house number.
            // A numbered address (e.g. "52A David Road") is specific enough to resolve globally —
            // the geocoder should resolve it bare first; the locale fallback handles failure.
            var pickupComponents2 = Services.AddressParser.ParseAddress(pickup);
            if (!pickupComponents2.HasHouseNumber)
            {
                var cityFromHistory = TryExtractCityFromHistory(pickup);
                if (cityFromHistory != null)
                {
                    _logger.LogInformation("[{SessionId}] 🏙️ Enriching vague pickup '{Pickup}' with history city '{City}' before geocoding",
                        SessionId, pickup, cityFromHistory);
                    pickup = $"{pickup}, {cityFromHistory}";
                }
            }
            else
            {
                _logger.LogInformation("[{SessionId}] 🏠 Pickup '{Pickup}' has house number — sending bare to geocoder (no city inference)",
                    SessionId, pickup);
            }
        }

        // If destination has verified geocoded city, use it — otherwise conditionally enrich
        if (_booking.DestLat.HasValue && _booking.DestLat != 0)
        {
            destination = EnrichWithVerifiedCity(destination, _booking.DestCity);
        }
        else if (DestinationLacksCityContext(destination))
        {
            // Only infer city from history if there is NO house number.
            // A numbered address (e.g. "1214A Warwick Road") is specific enough to resolve globally —
            // injecting the caller's local city would wrongly anchor it to their area.
            var destComponents = Services.AddressParser.ParseAddress(destination);
            if (!destComponents.HasHouseNumber)
            {
                var cityFromHistory = TryExtractCityFromHistory(destination);
                if (cityFromHistory != null)
                {
                    _logger.LogInformation("[{SessionId}] 🏙️ Enriching destination '{Dest}' with history city '{City}' before geocoding",
                        SessionId, destination, cityFromHistory);
                    destination = $"{destination}, {cityFromHistory}";
                }
                // else: no history match — pass bare, let geocoder try
            }
            else
            {
                _logger.LogInformation("[{SessionId}] 🏠 Destination '{Dest}' has house number — passing bare to geocoder (no city inference)",
                    SessionId, destination);
            }
        }

        return (pickup, destination);
    }

    /// <summary>
    /// Extracts the spoken house number from a raw address string using AddressParser.
    /// Returns null if no number found or if the address is not a street-type address.
    /// </summary>
    private static string? GetSpokenHouseNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var c = Services.AddressParser.ParseAddress(address);
        return c.HasHouseNumber ? c.HouseNumber : null;
    }

    private async Task TriggerFareCalculationAsync()
    {
        if (_booking.Pickup == null || _booking.Destination == null)
            return;

        var (pickup, destination) = GetEnrichedAddresses();
        var callerId = CallerId;
        var sessionId = SessionId;

        try
        {
            _logger.LogInformation("[{SessionId}] 🔄 Fare re-calculation after Address Lock resolution", sessionId);
            var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId, _booking.PickupTime,
                spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
            if (result.NeedsClarification)
            {
                var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                var destAlts = result.DestAlternatives ?? Array.Empty<string>();

                if (pickupAlts.Length > 0)
                {
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    await _aiClient.InjectMessageAndRespondAsync(
                            $"[ADDRESS DISAMBIGUATION] needs_disambiguation=true, target=pickup, " +
                            $"options=[{string.Join(", ", pickupAlts)}]. " +
                            "Ask the caller to clarify the PICKUP. Present options with numbers, then WAIT.");
                    return;
                }
                if (destAlts.Length > 0)
                {
                    Interlocked.Exchange(ref _fareAutoTriggered, 0);
                    await _aiClient.InjectMessageAndRespondAsync(
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

                await _aiClient.InjectMessageAndRespondAsync(
                        "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard or the city could not be determined. " +
                        "Ask the caller to confirm their DESTINATION address AND which city or area they are in. " +
                        "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going, and which city you're in?\" " +
                        "When they respond, call sync_booking_data with the destination INCLUDING the city name (e.g. '7 Russell Street, Coventry').");
                return;
            }

            // Address discrepancy check (post-clarification)
            var discrepancy2 = DetectAddressDiscrepancy(result);
            if (discrepancy2 != null)
            {
                _logger.LogWarning("[{SessionId}] 🚨 Address discrepancy after clarification: {Msg}", sessionId, discrepancy2);
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                await _aiClient.InjectMessageAndRespondAsync(
                    $"[ADDRESS DISCREPANCY] {discrepancy2} " +
                    "Ask the caller to confirm or repeat their address. " +
                    "When they respond, call sync_booking_data with the corrected address.");
                return;
            }

            ApplyFareResult(result);

            _aiClient.SetAwaitingConfirmation(true);
            _currentStage = BookingStage.FarePresented;
            await _aiClient.SetVadModeAsync(useSemantic: false);

            OnBookingUpdated?.Invoke(_booking.Clone());

            var spokenFare = FormatFareForSpeech(_booking.Fare);
            var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
            var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

            // Guard: reject if geocoder resolved pickup and destination to the same address
            if (string.Equals(pickupAddr, destAddr, StringComparison.OrdinalIgnoreCase) && pickupAddr != "the address")
            {
                _logger.LogWarning("[{SessionId}] ⚠ Post-geocode same-address (clarification path): pickup == destination ({Addr}). Clearing destination.", sessionId, pickupAddr);
                _booking.Destination = null;
                _booking.DestLat = _booking.DestLon = null;
                _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                _booking.Fare = null; _booking.Eta = null;
                Interlocked.Exchange(ref _fareAutoTriggered, 0);
                _aiClient.SetAwaitingConfirmation(false);
                _currentStage = BookingStage.CollectingDestination;
                await _aiClient.InjectMessageAndRespondAsync(
                    "[DESTINATION ERROR] The verified pickup and destination resolved to the same location. " +
                    "Ask the caller where they want to go — their destination was not captured correctly. " +
                    "When they answer, call sync_booking_data with the corrected destination.");
                return;
            }

            _logger.LogInformation("[{SessionId}] 💰 Fare ready after clarification: {Fare}, ETA: {Eta}",
                sessionId, _booking.Fare, _booking.Eta);

            var timePart2 = FormatScheduledTimePart();
            await _aiClient.InjectMessageAndRespondAsync(
                    $"[FARE RESULT] Verified pickup: {pickupAddr}. Verified destination: {destAddr}. Fare: {spokenFare}{timePart2}. " +
                    "Use ONLY these verified addresses when reading back to the caller — do NOT use the caller's raw words. Ask them to confirm.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Fare re-calculation failed after clarification", sessionId);
            _booking.Fare = "£8.00";
            _booking.Eta = "8 minutes";
            OnBookingUpdated?.Invoke(_booking.Clone());

            var fallbackTimePart2 = FormatScheduledTimePart();
            await _aiClient.InjectMessageAndRespondAsync(
                    $"[FARE RESULT] The estimated fare is 8 pounds{fallbackTimePart2}. " +
                    "Read back the details to the caller and ask them to confirm.");
        }
    }

    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

        // SAFETY NET: populate _booking from book_taxi args (with name validation)
        // Only overwrite name if we don't already have a valid one (e.g. from caller history)
        if (string.IsNullOrWhiteSpace(_booking.Name) || _rejectedNames.Contains(_booking.Name))
        {
            if (args.TryGetValue("caller_name", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString())
                && !_rejectedNames.Contains(bn.ToString()!.Trim()))
                _booking.Name = bn.ToString()!.Trim();
        }

        // Name guard — reject booking without a real name
        if (string.IsNullOrWhiteSpace(_booking.Name) || _rejectedNames.Contains(_booking.Name))
            return new { success = false, error = "Caller name is required. Ask the caller for their name before booking." };
        if (args.TryGetValue("pickup", out var bp) && !string.IsNullOrWhiteSpace(bp?.ToString()))
            _booking.Pickup = bp.ToString();
        if (args.TryGetValue("destination", out var bd) && !string.IsNullOrWhiteSpace(bd?.ToString()))
            _booking.Destination = bd.ToString();
        // Only update passengers from book_taxi args if not already set, or if the new value is larger
        // (prevents AI sending stale/default value of 1 from overwriting the correctly-collected count)
        if (args.TryGetValue("passengers", out var bpax) && int.TryParse(bpax?.ToString(), out var bpn) && bpn > 0)
        {
            if (!_booking.Passengers.HasValue || _booking.Passengers == 0)
                _booking.Passengers = bpn;
            // If booking already has a passenger count, only update if the new value is different AND > 1
            // (the AI defaults to 1 if unsure — trust the sync_booking_data value instead)
            else if (bpn > 1 && bpn != _booking.Passengers)
                _booking.Passengers = bpn;
        }
        if (args.TryGetValue("pickup_time", out var bpt) && !string.IsNullOrWhiteSpace(bpt?.ToString()))
        {
            _booking.PickupTime = bpt.ToString();
            _booking.ScheduledAt = BookingState.ParsePickupTimeToDateTime(_booking.PickupTime);
        }
        // Capture payment preference (card = fixed price via SumUp, meter = pay on the day)
        if (args.TryGetValue("payment_preference", out var pref) && !string.IsNullOrWhiteSpace(pref?.ToString()))
            _booking.PaymentPreference = pref.ToString()!.Trim().ToLowerInvariant();

        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Missing pickup or destination." };

            // GUARD: If sync_booking_data already auto-triggered fare calculation, don't spawn a duplicate.
            // Return a silent "already calculating" result so Ada doesn't double-speak an interjection.
            if (Volatile.Read(ref _fareAutoTriggered) == 1)
            {
                _logger.LogInformation("[{SessionId}] ⏳ book_taxi(request_quote) skipped — fare already in flight from auto-trigger", SessionId);
                return new { success = true, status = "calculating", message = "I'm checking the fare now. Tell the caller you're just looking into it and will have the details shortly. Do NOT repeat any interjection — one is already in progress." };
            }

            // NON-BLOCKING: return immediately so Ada speaks an interjection,
            // then inject the fare result asynchronously when ready.
            var (pickup, destination) = GetEnrichedAddresses();
            var callerId = CallerId;
            var sessionId = SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("[{SessionId}] 🔄 Background fare calculation starting for {Pickup} → {Dest}",
                        sessionId, pickup, destination);

                    var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(pickup, destination, callerId, _booking.PickupTime,
                        spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                        spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
                    var completed = await Task.WhenAny(aiTask, Task.Delay(18000));

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

                                await _aiClient.InjectMessageAndRespondAsync(
                                        $"[PICKUP DISAMBIGUATION] The PICKUP address is ambiguous. The options are: {altsList}. " +
                                        "Ask the caller ONLY about the PICKUP location. Do NOT mention the destination yet. " +
                                        "Present the pickup options clearly, then STOP and WAIT for their answer.");
                            }
                            else if (destAlts.Length > 0)
                            {
                                var altsList = string.Join(", ", destAlts);
                                _destDisambiguated = false;

                                await _aiClient.InjectMessageAndRespondAsync(
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

                            await _aiClient.InjectMessageAndRespondAsync(
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
                        EnrichFallbackResultStructuredFields(result, pickup, destination);
                    }

                    // Clear pending disambiguation state
                    _pendingDestAlternatives = null;
                    _pendingDestClarificationMessage = null;

                    // FARE SANITY CHECK
                    if (!IsFareSane(result))
                    {
                        _logger.LogWarning("[{SessionId}] 🚨 Fare sanity check FAILED (book_taxi path) — asking user to verify destination", sessionId);

                        await _aiClient.InjectMessageAndRespondAsync(
                                "[FARE SANITY ALERT] The calculated fare seems unusually high, which suggests the destination may have been misheard or the city could not be determined. " +
                                "Ask the caller to confirm their DESTINATION address AND which city or area they are in. " +
                                "Say something like: \"I want to make sure I have the right destination — could you repeat where you're going, and which city you're in?\" " +
                                "When they respond, call sync_booking_data with the destination INCLUDING the city name (e.g. '7 Russell Street, Coventry').");
                        return;
                    }

                    ApplyFareResult(result);

                    // ── iCabbi FARE QUOTE (Phase 1 of 2) ──────────────────────────────────────
                    if (_icabbiEnabled && _icabbi != null)
                    {
                        _logger.LogInformation("[{SessionId}] 🚕 [Phase 1] Requesting iCabbi fare quote (siteId={SiteId})", sessionId, _settings.Icabbi.SiteId);
                        var quote = await _icabbi.GetFareQuoteAsync(_booking, _settings.Icabbi.SiteId);
                        if (quote != null)
                        {
                            _logger.LogInformation("[{SessionId}] ✅ iCabbi quote: {OldFare} → {NewFare}, ETA: {Eta}",
                                sessionId, _booking.Fare, quote.FareFormatted, quote.EtaFormatted);
                            _booking.Fare = quote.FareFormatted;
                            _booking.Eta = quote.EtaFormatted;
                        }
                        else
                        {
                            _logger.LogWarning("[{SessionId}] ⚠️ iCabbi quote unavailable — using Nominatim estimate ({Fare})", sessionId, _booking.Fare);
                        }
                    }

                    _aiClient.SetAwaitingConfirmation(true);
                    _currentStage = BookingStage.FarePresented;
                    await _aiClient.SetVadModeAsync(useSemantic: false);

                    OnBookingUpdated?.Invoke(_booking.Clone());

                    var spokenFare = FormatFareForSpeech(_booking.Fare);
                    _logger.LogInformation("[{SessionId}] 💰 Fare ready: {Fare} ({Spoken}), ETA: {Eta}",
                        sessionId, _booking.Fare, spokenFare, _booking.Eta);

                    var pickupAddr = FormatAddressForReadback(result.PickupNumber, result.PickupStreet, result.PickupPostalCode, result.PickupCity);
                    var destAddr = FormatAddressForReadback(result.DestNumber, result.DestStreet, result.DestPostalCode, result.DestCity);

                    // Guard: reject if geocoder resolved pickup and destination to the same address
                    if (string.Equals(pickupAddr, destAddr, StringComparison.OrdinalIgnoreCase) && pickupAddr != "the address")
                    {
                        _logger.LogWarning("[{SessionId}] ⚠ Post-geocode same-address (sync path): pickup == destination ({Addr}). Clearing destination.", sessionId, pickupAddr);
                        _booking.Destination = null;
                        _booking.DestLat = _booking.DestLon = null;
                        _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                        _booking.Fare = null; _booking.Eta = null;
                        Interlocked.Exchange(ref _fareAutoTriggered, 0);
                        _aiClient.SetAwaitingConfirmation(false);
                        _currentStage = BookingStage.CollectingDestination;
                        await _aiClient.InjectMessageAndRespondAsync(
                            "[ADDRESS ERROR] The pickup and destination appear to be the same address. " +
                            "Ask the caller to confirm their destination again — it may have been misheard.");
                        return;
                    }

                    // Inject fare result into conversation — Ada will read it back
                    var timePart3 = FormatScheduledTimePart();
                    await _aiClient.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] Verified pickup: {pickupAddr}. Verified destination: {destAddr}. Fare: {spokenFare}{timePart3}. " +
                            $"Use ONLY these verified addresses when reading back to the caller — do NOT use the caller's raw words. Ask them to confirm the booking.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Background fare calculation failed", sessionId);
                    _booking.Fare = "£8.00";
                    _booking.Eta = "8 minutes";
                    OnBookingUpdated?.Invoke(_booking.Clone());

                    var fallbackTimePart3 = FormatScheduledTimePart();
                    await _aiClient.InjectMessageAndRespondAsync(
                            $"[FARE RESULT] The estimated fare is 8 pounds{fallbackTimePart3}. " +
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
            // This prevents the race condition where the AI calls book_taxi(confirmed) before
            // the fare/disambiguation result has been processed.
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

            // Geocode if needed — but skip if iCabbi already quoted (coords + fare already set)
            // to prevent the Gemini re-estimate from overwriting the official iCabbi price.
            bool iCabbiAlreadyQuoted = _icabbiEnabled && !string.IsNullOrWhiteSpace(_booking.Fare)
                && (_booking.PickupLat.HasValue && _booking.PickupLat != 0)
                && (_booking.DestLat.HasValue && _booking.DestLat != 0);
            bool needsGeocode = !iCabbiAlreadyQuoted && (string.IsNullOrWhiteSpace(_booking.PickupStreet)
                || (_booking.PickupLat == 0 && _booking.PickupLon == 0)
                || (_booking.DestLat == 0 && _booking.DestLon == 0));

            if (needsGeocode)
            {
                try
                {
                    var (ep, ed) = GetEnrichedAddresses();
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(ep, ed, CallerId, _booking.PickupTime,
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

            _aiClient.SetAwaitingConfirmation(false);

            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] ✅ Booked: {Ref}", SessionId, _booking.BookingRef);

            var bookingSnapshot = _booking.Clone();
            var callerId = CallerId;
            var sessionId = SessionId;

            var sumUpRef = _sumUp;  // capture for closure
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 50 && _aiClient.IsResponseActive; i++)
                    await Task.Delay(100);

                await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
                await _dispatcher.SendWhatsAppAsync(callerId);
                await SaveCallerHistoryAsync(bookingSnapshot, callerId);

                // ── SumUp PAYMENT LINK (card payers only) ────────────────────────────────
                // If the caller chose the fixed price / card option, generate a SumUp
                // checkout link and send it via WhatsApp so they can pay in advance.
                if (bookingSnapshot.PaymentPreference == "card" && sumUpRef != null)
                {
                    try
                    {
                        var fareDecimal = bookingSnapshot.FareDecimal;
                        if (fareDecimal <= 0)
                        {
                            _logger.LogWarning("[{SessionId}] [SumUp] Fare is zero — skipping payment link", sessionId);
                        }
                        else
                        {
                            var description = $"Taxi: {bookingSnapshot.Pickup} → {bookingSnapshot.Destination} (Ref: {bookingSnapshot.BookingRef})";
                            var paymentUrl = await sumUpRef.CreateCheckoutLinkAsync(
                                bookingSnapshot.BookingRef ?? sessionId,
                                fareDecimal,
                                description,
                                callerId);

                            if (!string.IsNullOrWhiteSpace(paymentUrl))
                            {
                                _logger.LogInformation("[{SessionId}] 💳 SumUp payment link generated: {Url}", sessionId, paymentUrl);
                                // Send the payment link via WhatsApp (BSQD webhook carries the URL as custom note)
                                await SendSumUpLinkViaWhatsAppAsync(callerId, paymentUrl, bookingSnapshot, sessionId);
                            }
                            else
                            {
                                _logger.LogWarning("[{SessionId}] [SumUp] No payment URL returned — payment link not sent", sessionId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{SessionId}] SumUp payment link error", sessionId);
                    }
                }

                // ── iCabbi CONFIRMED BOOKING (Phase 2 of 2) ─────────────────────────────────
                // Phase 1 was GetFareQuoteAsync → gave Ada the official price/ETA to read back.
                // Phase 2 (here): caller confirmed → send real booking to iCabbi dispatch.
                // The fare in bookingSnapshot.Fare is already the iCabbi-quoted price.
                if (_icabbiEnabled && _icabbi != null)
                {
                    try
                    {
                        _logger.LogInformation("[{SessionId}] 🚕 [Phase 2] Sending confirmed booking to iCabbi (siteId={SiteId}, fare={Fare})",
                            sessionId, _settings.Icabbi.SiteId, bookingSnapshot.Fare);
                        // Pass callerId explicitly so the correct phone always reaches iCabbi,
                        // even if CallerPhone in the snapshot was cleared by an early clone.
                        var icabbiResult = await _icabbi.CreateAndDispatchAsync(bookingSnapshot, _settings.Icabbi.SiteId, callerPhoneOverride: callerId);
                        if (icabbiResult.Success)
                            _logger.LogInformation("[{SessionId}] ✅ iCabbi booking confirmed — JourneyId: {JourneyId}, Tracking: {TrackingUrl}",
                                sessionId, icabbiResult.JourneyId, icabbiResult.TrackingUrl);
                        else
                            _logger.LogWarning("[{SessionId}] ⚠️ iCabbi booking failed: {Msg}", sessionId, icabbiResult.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{SessionId}] iCabbi dispatch error", sessionId);
                    }
                }
            });

            _currentStage = BookingStage.AnythingElse;
            return new { success = true, booking_ref = _booking.BookingRef, message = $"Taxi booked successfully. Tell the caller: Your booking reference is {_booking.BookingRef}. Then say the FINAL CLOSING script verbatim and immediately call end_call. Do NOT ask if they need anything else." };
        }

        return new { error = "Invalid action" };
    }

    // =========================
    // SUMUP PAYMENT LINK — WhatsApp delivery
    // =========================
    private async Task SendSumUpLinkViaWhatsAppAsync(string callerId, string paymentUrl, BookingState booking, string sessionId)
    {
        try
        {
            // Build a descriptive WhatsApp message with the payment link
            var fare = booking.Fare ?? "the agreed fare";
            var pickup = booking.PickupFormatted ?? booking.Pickup ?? "your pickup";
            var destination = booking.DestFormatted ?? booking.Destination ?? "your destination";
            var bookingRef = booking.BookingRef ?? sessionId;

            _logger.LogInformation("[{SessionId}] 💳 Sending SumUp payment link to {Phone}: {Url}", sessionId, callerId, paymentUrl);

            // Deliver via BSQD WhatsApp webhook — append the payment URL as a note
            // The BSQD webhook already handles WhatsApp delivery; we embed the link in the payload.
            if (!string.IsNullOrEmpty(_settings.Dispatch.WhatsAppWebhookUrl))
            {
                using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.Dispatch.BsqdApiKey);

                var message = $"Hi {booking.Name ?? "there"}! Your taxi from {pickup} to {destination} is confirmed (Ref: {bookingRef}). " +
                              $"To guarantee your fixed price of {fare}, please complete payment here: {paymentUrl} — Thank you, 247 Radio Carz";

                var body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    phoneNumber = FormatE164ForSumUp(callerId),
                    message = message,
                    paymentUrl = paymentUrl,
                    bookingRef = bookingRef
                });

                var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, _settings.Dispatch.WhatsAppWebhookUrl);
                request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.SendAsync(request);
                _logger.LogInformation("[{SessionId}] 💳 SumUp WhatsApp delivery: {Status}", sessionId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] Failed to send SumUp WhatsApp message", sessionId);
        }
    }

    private static string FormatE164ForSumUp(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+441000000000";
        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (clean.StartsWith("00")) clean = "+" + clean[2..];
        if (!clean.StartsWith("+")) clean = "+" + clean;
        return clean;
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
            var enrichedPickup = EnrichWithVerifiedCity(_booking.Pickup!, _booking.PickupLat.HasValue && _booking.PickupLat != 0 ? _booking.PickupCity : null);
            var enrichedDest = _booking.Destination != null 
                ? EnrichWithVerifiedCity(_booking.Destination, _booking.DestLat.HasValue && _booking.DestLat != 0 ? _booking.DestCity : null)
                : enrichedPickup;
            var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(
                enrichedPickup, enrichedDest, CallerId, _booking.PickupTime,
                spokenPickupNumber: GetSpokenHouseNumber(_booking.Pickup),
                spokenDestNumber: GetSpokenHouseNumber(_booking.Destination));
            var completed = await Task.WhenAny(aiTask, Task.Delay(18000));

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
                    enrichedPickup, enrichedDest, CallerId);
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
            await SaveCallerHistoryAsync(bookingSnapshot, callerId);

            if (_icabbiEnabled && _icabbi != null)
            {
                try
                {
                    var icabbiResult = await _icabbi.CreateAndDispatchAsync(bookingSnapshot, _settings.Icabbi.SiteId);
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
            if (!_aiClient.IsConnected) return;
            _aiClient.CancelDeferredResponse();
            _logger.LogInformation("[{SessionId}] ⏰ Post-booking timeout - requesting farewell", SessionId);
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
        _currentStage = BookingStage.Ending;
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
            var streamStart = Environment.TickCount64;
            while (_aiClient.IsResponseActive && Environment.TickCount64 - streamStart < 15000)
                await Task.Delay(200);

            var enqueueStart = Environment.TickCount64;
            while ((_aiClient.GetQueuedFrames?.Invoke() ?? 0) == 0 && Environment.TickCount64 - enqueueStart < 5000)
                await Task.Delay(100);

            await Task.Delay(2000);

            var drainStart = Environment.TickCount64;
            while (Environment.TickCount64 - drainStart < 20000)
            {
                if ((_aiClient.GetQueuedFrames?.Invoke() ?? 0) == 0) break;
                await Task.Delay(100);
            }

            await Task.Delay(1000);

            await EndAsync("end_call tool");
        });

        return new { success = true };
    }

    // =========================
    // HELPERS
    // =========================
    private static bool IsImmediatePickup(string? pickupTime)
    {
        if (string.IsNullOrWhiteSpace(pickupTime)) return true;
        var lower = pickupTime.Trim().ToLowerInvariant();
        return lower is "now" or "asap" or "as soon as possible" or "immediately" or "straight away" or "right now";
    }

    private string FormatScheduledTimePart()
    {
        if (IsImmediatePickup(_booking.PickupTime))
            return $", estimated time of arrival is {_booking.Eta ?? "8 minutes"}";
        
        if (_booking.ScheduledAt.HasValue)
        {
            // Convert UTC to London local for human-readable readback
            var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var local = TimeZoneInfo.ConvertTimeFromUtc(_booking.ScheduledAt.Value, londonTz);
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, londonTz);
            var timeStr = local.ToString("h:mm tt").ToLower();
            var isToday = local.Date == nowLocal.Date;
            var isTomorrow = local.Date == nowLocal.Date.AddDays(1);
            var dayPart = isToday ? "today" : isTomorrow ? "tomorrow" : local.ToString("dddd, MMMM d");
            return $", scheduled for {timeStr} {dayPart}. Do NOT mention any ETA or arrival time — this is an advance booking";
        }
        
        // Fallback: PickupTime is set but ScheduledAt failed to parse — still treat as advance
        if (!string.IsNullOrWhiteSpace(_booking.PickupTime))
            return $", scheduled for {_booking.PickupTime}. Do NOT mention any ETA or arrival time — this is an advance booking";
        
        return "";
    }

    private static string FormatFareForSpeech(string? fare)
    {
        if (string.IsNullOrEmpty(fare)) return "unknown";
        // Detect currency from symbol
        string currencyWord, subunitWord;
        if (fare.Contains("£")) { currencyWord = "pounds"; subunitWord = "pence"; }
        else if (fare.Contains("$")) { currencyWord = "dollars"; subunitWord = "cents"; }
        else { currencyWord = "euros"; subunitWord = "cents"; }

        var clean = fare.Replace("€", "").Replace("£", "").Replace("$", "").Trim();
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            var whole = (int)amount;
            var subunit = (int)((amount - whole) * 100);
            return subunit > 0 ? $"{whole} {currencyWord} {subunit} {subunitWord}" : $"{whole} {currencyWord}";
        }
        return fare;
    }

    private static string FormatAddressForReadback(string? number, string? street, string? postalCode, string? city)
    {
        // Standard UK format: Number Street, City — natural verbal delivery order
        var parts = new List<string>();
        
        // Combine number + street into a single part (e.g. "1214A Warwick Road")
        var streetPart = string.Join(" ", new[] { number, street }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(streetPart))
            parts.Add(streetPart);
        if (!string.IsNullOrWhiteSpace(city))
            parts.Add(city);
        // Postal codes intentionally omitted from verbal readback (stored in backend only)
        
        return parts.Count > 0 ? string.Join(", ", parts) : "the address";
    }

    // =========================
    // FARE SANITY CHECK
    // =========================
    // NOTE: ETA check removed — edge function now returns driver_eta (8-15 min) not trip duration.
    // Only fare amount is checked. Once booking is confirmed, no further sanity checks run.
    private const decimal MAX_SANE_FARE = 100m;

    /// <summary>
    /// Returns true if the fare is within reasonable bounds.
    /// Catches cases where STT mishears a destination (e.g. "Kochi" instead of "Coventry")
    /// resulting in absurd cross-country/international fares.
    /// ETA is no longer checked — the edge function now returns driver arrival time (8-15 min),
    /// not trip duration.
    /// </summary>
    private bool IsFareSane(FareResult result)
    {
        var dest = _booking.Destination ?? "";

        // Debug logging: trace exactly what the bypass check sees
        _logger.LogDebug("[{SessionId}] 🔍 IsFareSane: count={Count}, dest='{Dest}', lastDest='{LastDest}'",
            SessionId, _fareSanityAlertCount, dest, _lastSanityAlertDestination ?? "(null)");

        // FINALIZATION GUARD: Once booking is confirmed, never run sanity checks again
        if (Volatile.Read(ref _bookTaxiCompleted) == 1)
        {
            _logger.LogInformation("[{SessionId}] ✅ Fare sanity SKIPPED — booking already finalized", SessionId);
            return true;
        }

        // BYPASS: If disambiguation was just resolved, the user already confirmed the address — skip sanity
        if (_disambiguationPerformed)
        {
            _logger.LogInformation("[{SessionId}] ✅ Fare sanity BYPASSED — disambiguation was resolved, addresses are user-confirmed", SessionId);
            _disambiguationPerformed = false;
            _fareSanityAlertCount = 0;
            _lastSanityAlertDestination = null;
            _fareSanityActive = false;
            return true;
        }

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

        // Parse fare amount — only check fare, NOT ETA
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

        // ETA check REMOVED — edge function now returns driver arrival time (8-15 min), not trip duration.
        // Old code checked MAX_SANE_ETA_MINUTES (120) against trip duration, causing false positives
        // for legitimate inter-city bookings (e.g. Coventry→Manchester = 246 min trip but 14 min driver ETA).

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

        // If it contains a street number + road type, it's street-level
        if (System.Text.RegularExpressions.Regex.IsMatch(d, @"\b\d+[A-Za-z]?\b.*\b(road|street|lane|drive|avenue|close|way|crescent|terrace|place|grove|court|gardens|walk|rise|hill|park|row|square|mews|passage|yard)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;

        // No digits anywhere = no house number or postcode = city-level
        if (!System.Text.RegularExpressions.Regex.IsMatch(d, @"\d"))
            return true;

        return false;
    }

    /// <summary>
    /// When the Nominatim fallback is used, the FareResult only has lat/lon — no structured
    /// address fields. This method populates PickupStreet, PickupNumber, PickupCity etc. from
    /// AddressParser on the raw address strings, and infers city from caller history when missing.
    /// This ensures the BSQD payload always has correct street_name, street_number, city data.
    /// </summary>
    private void EnrichFallbackResultStructuredFields(FareResult result, string pickup, string destination)
    {
        // Pickup structured fields
        if (string.IsNullOrWhiteSpace(result.PickupStreet))
        {
            var p = Services.AddressParser.ParseAddress(pickup);
            result.PickupStreet  = p.StreetName;
            result.PickupNumber  = p.HasHouseNumber ? p.HouseNumber : null;
            if (string.IsNullOrWhiteSpace(result.PickupCity))
                result.PickupCity = p.TownOrArea.Length > 0 ? p.TownOrArea : TryExtractCityFromHistory(pickup);
            result.PickupFormatted = pickup;
            _logger.LogInformation("[{SessionId}] 🏠 Fallback structured pickup: street={St} num={Num} city={City}",
                SessionId, result.PickupStreet, result.PickupNumber, result.PickupCity);
        }

        // Destination structured fields
        if (string.IsNullOrWhiteSpace(result.DestStreet))
        {
            var d = Services.AddressParser.ParseAddress(destination);
            result.DestStreet  = d.StreetName;
            result.DestNumber  = d.HasHouseNumber ? d.HouseNumber : null;
            if (string.IsNullOrWhiteSpace(result.DestCity))
                result.DestCity = d.TownOrArea.Length > 0 ? d.TownOrArea : TryExtractCityFromHistory(destination);
            result.DestFormatted = destination;
            _logger.LogInformation("[{SessionId}] 🏠 Fallback structured dest: street={St} num={Num} city={City}",
                SessionId, result.DestStreet, result.DestNumber, result.DestCity);
        }
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

        // Apply AI-parsed scheduled time ONLY if the caller explicitly asked for a future time.
        // Never overwrite an ASAP booking (PickupTime = "ASAP"/null) with a geocoder-derived timestamp.
        if (result.ScheduledAt.HasValue)
        {
            var isAsap = string.IsNullOrWhiteSpace(_booking.PickupTime)
                || _booking.PickupTime.Trim().ToLowerInvariant() is "asap" or "now" or "as soon as possible"
                    or "immediately" or "straight away" or "right now";
            if (!isAsap)
                _booking.ScheduledAt = result.ScheduledAt;
        }

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

    // NormalizeHouseNumber: corrects HYPHENATED STT mishearings only.
    // When Whisper hears a letter suffix it sometimes inserts a hyphen+digit:
    //   "52A" → "52-8"   (A sounds like "eight")
    //   "14B" → "14-3"   (B sounds like "three")
    //   "7D"  → "7-4"    (D sounds like "four")
    // Matching ONLY the hyphenated form avoids the false-positive that destroyed
    // "43 Dovey Road" (plain trailing digit, no hyphen → DO NOT touch).
    private static readonly System.Text.RegularExpressions.Regex _sttHyphenFixRegex =
        new(@"^(\d{1,3})-(8|3|4)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Dictionary<string, string> _digitToLetter = new()
    {
        ["8"] = "A",   // 'A' misheard as 'eight'
        ["3"] = "B",   // 'B' misheard as 'three'
        ["4"] = "D",   // 'D' misheard as 'four'
    };

    private string? NormalizeHouseNumber(string? address, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;

        var trimmed = address.Trim();
        var match = _sttHyphenFixRegex.Match(trimmed);
        if (!match.Success) return address;

        var baseNum = match.Groups[1].Value;
        var digit   = match.Groups[2].Value;
        var letter  = _digitToLetter[digit];
        var corrected = $"{baseNum}{letter}{trimmed[(match.Length)..]}";

        _logger.LogInformation(
            "[{SessionId}] 🔤 STT hyphen fix ({Field}): '{Original}' → '{Corrected}'",
            SessionId, fieldName, match.Value, $"{baseNum}{letter}");

        return corrected;
    }

    /// <summary>
    /// Returns true if the destination address appears to be a bare street address with no
    /// city or area context — meaning the geocoder is very likely to fail or return
    /// NeedsClarification=true, potentially suggesting the wrong city from the pickup.
    ///
    /// Blocked examples: "52A David Road", "David Road, 52A"
    /// Allowed examples:  "52A David Road, Coventry", "Birmingham Airport"
    /// </summary>
    // Well-known UK cities/towns — if any appear in the destination string the geocoder
    // can resolve it without additional city context from history.
    private static readonly string[] KnownUkCities =
    [
        "manchester", "london", "birmingham", "coventry", "bristol", "leeds", "liverpool",
        "sheffield", "nottingham", "leicester", "edinburgh", "glasgow", "cardiff", "belfast",
        "newcastle", "sunderland", "brighton", "oxford", "cambridge", "wolverhampton",
        "stoke", "derby", "plymouth", "portsmouth", "southampton", "reading", "luton",
        "milton keynes", "northampton", "peterborough", "hull", "york", "exeter", "bath",
        "hertfordshire", "essex", "kent", "surrey", "hampshire", "warwickshire",
        "heathrow", "gatwick", "stansted", "luton airport", "birmingham airport",
        "manchester airport", "liverpool airport", "edinburgh airport", "glasgow airport",
        "east midlands airport", "bristol airport"
    ];

    private static bool DestinationLacksCityContext(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;

        var dest = destination.Trim();

        // If the destination explicitly contains a known UK city/airport, city context is present —
        // do NOT let history inference overwrite it with the caller's local city.
        var destLower = dest.ToLowerInvariant();
        if (KnownUkCities.Any(c => destLower.Contains(c)))
            return false;

        // No comma at all → almost certainly no city
        if (!dest.Contains(',')) return true;

        var parts = dest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return true;

        var lastPart = parts[^1];

        // Last segment is a house number or flat suffix → city still missing
        if (System.Text.RegularExpressions.Regex.IsMatch(lastPart, @"^\d+[A-Za-z]?$"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(lastPart, @"^(flat|apt|apartment|unit|suite)\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Searches the caller's known address history for any address that contains the same
    /// street name as the bare destination. If found, extracts and returns the city portion.
    ///
    /// Example: destination = "52A David Road" (no city)
    ///          history contains "52A David Road, Coventry"
    ///          → returns "Coventry"
    ///
    /// Matching is case-insensitive on the street-name words (ignores house numbers).
    /// Returns null if no useful match is found.
    /// </summary>
    private string? TryExtractCityFromHistory(string destination)
    {
        if (_callerKnownAddresses.Count == 0) return null;

        // Extract the street-name words from the bare destination (strip leading house number)
        var streetWords = ExtractStreetWords(destination);
        if (streetWords.Length == 0) return null;

        foreach (var known in _callerKnownAddresses)
        {
            if (string.IsNullOrWhiteSpace(known)) continue;

            // The known address must have at least one comma (street, city)
            var parts = known.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            // The last segment is the city candidate
            var cityCandidate = parts[^1];
            if (string.IsNullOrWhiteSpace(cityCandidate)) continue;

            // Skip if city candidate looks like a house number
            if (System.Text.RegularExpressions.Regex.IsMatch(cityCandidate, @"^\d+[A-Za-z]?$"))
                continue;

            // Check if the street words from the destination appear in this known address
            var knownStreet = string.Join(" ", parts[..^1]); // everything except the last segment
            var matchCount = streetWords.Count(w => knownStreet.Contains(w, StringComparison.OrdinalIgnoreCase));

            // Require at least half the meaningful street words to match
            if (matchCount >= Math.Max(1, streetWords.Length / 2))
                return cityCandidate;
        }

        return null;
    }

    /// <summary>
    /// Strips leading house numbers from an address and returns the remaining words.
    /// "52A David Road" → ["David", "Road"]
    /// "The Station, Platform 1" → ["The", "Station", "Platform"]
    /// </summary>
    private static string[] ExtractStreetWords(string address)
    {
        var cleaned = address.Trim();
        // Remove leading house number (e.g. "52A ", "14-16 ", "Flat 3 ")
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^\d+[A-Za-z]?\s+", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(flat|apt|unit|suite)\s*\d+[A-Za-z]?\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Remove commas and split
        return cleaned.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2) // Skip very short words like "A", "in"
            .ToArray();
    }

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

    /// <summary>
    /// Detect if the geocoded address is significantly different from the raw user input.
    /// E.g. user said "Box" but geocoder returned "Burges" — the street names don't match.
    /// Returns a descriptive message if discrepancy found, null if OK.
    /// </summary>
    private string? DetectAddressDiscrepancy(FareResult result)
    {
        var issues = new List<string>();

        // Check pickup street name
        if (!string.IsNullOrWhiteSpace(_booking.Pickup) && !string.IsNullOrWhiteSpace(result.PickupStreet))
        {
            if (!AddressContainsStreet(_booking.Pickup, result.PickupStreet))
            {
                issues.Add($"The pickup was '{_booking.Pickup}' but the system resolved it to '{result.PickupStreet}' which appears to be a different location.");
            }
        }

        // Check pickup house number: geocoded alphanumeric must not silently replace a plain integer
        if (!string.IsNullOrWhiteSpace(_booking.Pickup) && !string.IsNullOrWhiteSpace(result.PickupNumber))
        {
            var numIssue = DetectHouseNumberSubstitution(_booking.Pickup, result.PickupNumber, "pickup");
            if (numIssue != null) issues.Add(numIssue);
        }

        // Check destination street name
        if (!string.IsNullOrWhiteSpace(_booking.Destination) && !string.IsNullOrWhiteSpace(result.DestStreet))
        {
            if (!AddressContainsStreet(_booking.Destination, result.DestStreet))
            {
                issues.Add($"The destination was '{_booking.Destination}' but the system resolved it to '{result.DestStreet}' which appears to be a different location.");
            }
        }

        // Check destination house number: geocoded alphanumeric must not silently replace a plain integer
        if (!string.IsNullOrWhiteSpace(_booking.Destination) && !string.IsNullOrWhiteSpace(result.DestNumber))
        {
            var numIssue = DetectHouseNumberSubstitution(_booking.Destination, result.DestNumber, "destination");
            if (numIssue != null) issues.Add(numIssue);
        }

        return issues.Count > 0 ? string.Join(" ", issues) : null;
    }

    /// <summary>
    /// Detects when the geocoder silently substitutes a plain spoken integer with an
    /// alphanumeric house number (e.g. caller said "43 Dovey Road" → geocoder returned "4B").
    ///
    /// This is the REVERSE of the STT letter→digit confusion handled by NormalizeHouseNumber.
    /// It catches cases where:
    ///   - The caller spoke a pure integer (e.g. "43")
    ///   - The geocoder returned an alphanumeric (e.g. "4B") that starts with a different leading digit
    ///     OR whose numeric prefix does not match the spoken number
    ///
    /// NOTE: We do NOT flag the expected STT correction cases (e.g. "528" → "52A") because
    /// those are intentional normalizations already applied by NormalizeHouseNumber before
    /// the fare call. A mismatch only needs flagging when the geocoded number is materially
    /// different from what was spoken.
    /// </summary>
    private static string? DetectHouseNumberSubstitution(string rawAddress, string geocodedNumber, string field)
    {
        // Only care if geocoded result is alphanumeric (contains a letter suffix)
        if (!System.Text.RegularExpressions.Regex.IsMatch(geocodedNumber, @"^\d+[A-Za-z]$"))
            return null;

        // Extract the leading digits of the geocoded number (e.g. "4B" → "4")
        var geocodedDigits = System.Text.RegularExpressions.Regex.Match(geocodedNumber, @"^\d+").Value;
        if (string.IsNullOrEmpty(geocodedDigits)) return null;

        // Look for a standalone number in the raw address that is NOT the same as the geocoded digit-prefix
        // e.g. raw = "43 Dovey Road", geocodedDigits = "4" → "43" ≠ "4" → flag it
        // e.g. raw = "52A David Road" (already has letter) → skip (not pure integer)
        var rawNumbers = System.Text.RegularExpressions.Regex.Matches(rawAddress, @"\b(\d+)\b");
        foreach (System.Text.RegularExpressions.Match m in rawNumbers)
        {
            var spokenNum = m.Groups[1].Value;

            // Skip if the spoken number IS the geocoded digit-prefix (e.g. "4" → "4B" is fine, it's just the suffix being added)
            if (spokenNum == geocodedDigits) continue;

            // Skip if the geocoded number is just the spoken number with a letter suffix added
            // e.g. spoken "52" → geocoded "52A" — the geocoder is just being more specific
            if (geocodedNumber.StartsWith(spokenNum, StringComparison.OrdinalIgnoreCase) &&
                geocodedNumber.Length == spokenNum.Length + 1 &&
                char.IsLetter(geocodedNumber[^1]))
                continue;

            // The spoken number differs from the geocoded digit prefix in a non-trivial way
            // e.g. "43" vs "4" → the geocoder replaced "43" with "4B"
            return $"The caller said house number '{spokenNum}' for the {field}, but the system resolved it to '{geocodedNumber}'. " +
                   $"Please confirm with the caller: did they mean '{spokenNum} {rawAddress.Split(' ').Skip(1).FirstOrDefault()}' or '{geocodedNumber} {rawAddress.Split(' ').Skip(1).FirstOrDefault()}'?";
        }

        return null;
    }

    /// <summary>
    /// Check if the raw address input contains the geocoded street name (or vice versa).
    /// Uses word-level fuzzy matching to handle minor differences.
    /// </summary>
    private static bool AddressContainsStreet(string rawInput, string geocodedStreet)
    {
        static string Norm(string s) => System.Text.RegularExpressions.Regex
            .Replace(s.ToLowerInvariant(), @"[^a-z ]", " ").Trim();

        var rawNorm = Norm(rawInput);
        var streetNorm = Norm(geocodedStreet);

        // Direct containment
        if (rawNorm.Contains(streetNorm) || streetNorm.Contains(rawNorm))
            return true;

        // Word overlap: if the significant words of the geocoded street appear in the raw input
        var streetWords = streetNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToArray();
        var rawWords = new HashSet<string>(rawNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (streetWords.Length == 0) return true; // Nothing to compare

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

    // =========================
    // CALLER HISTORY SAVE
    // =========================
    /// <summary>
    /// Fire-and-forget save of caller name + addresses to the callers table
    /// via the caller-history-save edge function. This enables Ada to remember
    /// returning callers by name on their next call.
    /// </summary>
    private async Task SaveCallerHistoryAsync(BookingState booking, string callerId)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var payload = new
            {
                phone = callerId,
                name = booking.Name ?? "",
                pickup = booking.Pickup ?? "",
                destination = booking.Destination ?? "",
                call_id = SessionId
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.Supabase.Url}/functions/v1/caller-history-save");
            req.Content = content;
            req.Headers.Add("apikey", _settings.Supabase.AnonKey);
            req.Headers.Add("Authorization", $"Bearer {_settings.Supabase.AnonKey}");

            var response = await http.SendAsync(req);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("[{SessionId}] ✅ Caller history saved for {Phone} (name={Name})",
                    SessionId, callerId, booking.Name ?? "(none)");
            else
                _logger.LogWarning("[{SessionId}] ⚠️ Caller history save failed: HTTP {Status}",
                    SessionId, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{SessionId}] ⚠️ Caller history save error (non-fatal)", SessionId);
        }
    }
}
