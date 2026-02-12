using AdaMain.Ai;
using AdaMain.Config;
using AdaMain.Services;
using Microsoft.Extensions.Logging;

namespace AdaMain.Core;

/// <summary>
/// Manages a single call session lifecycle with G.711 A-law passthrough.
/// Integrates address-dispatch edge function for AI-powered address resolution.
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly ILogger<CallSession> _logger;
    private readonly AppSettings _settings;
    private readonly IOpenAiClient _aiClient;
    
    /// <summary>Expose AI client for wiring (e.g., playout queue depth).</summary>
    public IOpenAiClient AiClient => _aiClient;
    private readonly IFareCalculator _fareCalculator;
    private readonly IDispatcher _dispatcher;
    private readonly IcabbiBookingService? _icabbi;
    private readonly bool _icabbiEnabled;
    
    private readonly BookingState _booking = new();
    
    private int _disposed;
    private int _active;
    private int _autoQuoteInProgress; // Prevents safety net from racing with background fare calc
    private int _bookTaxiCompleted;   // Guard: prevent duplicate book_taxi confirmed calls
    private int _disambiguationPending; // Flag: blocks sync_booking_data/book_taxi until caller confirms address
    private long _lastAdaFinishedAt;
    
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
        
        // Set caller phone in booking state for all dispatch paths (including iCabbi)
        _booking.CallerPhone = callerId;
        
        // Wire up AI client events
        _aiClient.OnAudio += HandleAiAudio;
        _aiClient.OnToolCall += HandleToolCallAsync;
        _aiClient.OnEnded += reason => _ = EndAsync(reason);
        _aiClient.OnTranscript += (role, text) => OnTranscript?.Invoke(role, text);
        
        // Barge-in: notify playout to clear
        if (_aiClient is OpenAiG711Client g711Client)
        {
            g711Client.OnBargeIn += () =>
            {
                OnBargeIn?.Invoke();
            };
            g711Client.IsAutoQuoteInProgress = () => Volatile.Read(ref _autoQuoteInProgress) == 1;
            g711Client._isDisambiguationPending = () => Volatile.Read(ref _disambiguationPending) == 1;
        }
    }
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _active, 1) == 1)
            return;
        
        _logger.LogInformation("[{SessionId}] Starting G.711 session for {CallerId}", SessionId, CallerId);
        
        await _aiClient.ConnectAsync(CallerId, ct);
    }
    
    public async Task EndAsync(string reason)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            return;
        
        _logger.LogInformation("[{SessionId}] Ending session: {Reason}", SessionId, reason);
        
        await _aiClient.DisconnectAsync();
        OnEnded?.Invoke(this, reason);
    }
    
    /// <summary>Feed raw G.711 A-law RTP audio from SIP - direct passthrough.
    /// NOTE: Echo guard is handled by SipServer's soft gate (matching G711CallHandler pattern).
    /// Do NOT add a second echo guard here — triple-gating causes audio dropouts.</summary>
    public void ProcessInboundAudio(byte[] alawRtp)
    {
        if (!IsActive || alawRtp.Length == 0)
            return;
        
        _aiClient.SendAudio(alawRtp);
    }
    
    /// <summary>Get next outbound G.711 A-law frame for SIP (160 bytes = 20ms). Legacy polling API.</summary>
    public byte[]? GetOutboundFrame()
    {
        return null; // Playout now uses OnAudioOut event directly
    }
    
    /// <summary>Notify that playout queue has drained - triggers echo guard + no-reply watchdog.</summary>
    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        
        if (_aiClient is OpenAiG711Client g711Client)
            g711Client.NotifyPlayoutComplete();
    }
    
    /// <summary>Direct A-law frames from OpenAI → playout via event (with volume boost).</summary>
    private void HandleAiAudio(byte[] alawFrame)
    {
        // Apply configurable volume boost in A-law domain (decode→amplify→re-encode)
        var gain = (float)_settings.Audio.VolumeBoost;
        if (gain > 1.01f || gain < 0.99f)
        {
            Audio.ALawVolumeBoost.ApplyInPlace(alawFrame, gain);
        }
        OnAudioOut?.Invoke(alawFrame);
    }
    
    private async Task<object> HandleToolCallAsync(string name, Dictionary<string, object?> args)
    {
        _logger.LogDebug("[{SessionId}] Tool call: {Name}", SessionId, name);
        
        // ═════════════════════════════════════════════════════════════════════════════════
        // DISAMBIGUATION GUARD: Block tool calls if waiting for address clarification
        // ═════════════════════════════════════════════════════════════════════════════════
        if (Volatile.Read(ref _disambiguationPending) == 1)
        {
            // Only allow sync_booking_data if they've provided a city-qualified address
            if (name == "sync_booking_data")
            {
                var pickup = args.TryGetValue("pickup", out var p) ? p?.ToString() : null;
                var destination = args.TryGetValue("destination", out var d) ? d?.ToString() : null;
                
                // Check if addresses now include a city (comma-separated or multipart)
                bool hasQualifiedPickup = !string.IsNullOrWhiteSpace(pickup) && pickup.Contains(",");
                bool hasQualifiedDest = !string.IsNullOrWhiteSpace(destination) && destination.Contains(",");
                
                // If we only had ambiguity on one side, accept if that side is now qualified
                // If both were ambiguous, both must be qualified
                bool needsPickupClarity = !string.IsNullOrWhiteSpace(_booking.Pickup) && !_booking.Pickup.Contains(",");
                bool needsDestClarity = !string.IsNullOrWhiteSpace(_booking.Destination) && !_booking.Destination.Contains(",");
                
                if ((needsPickupClarity && !hasQualifiedPickup) || (needsDestClarity && !hasQualifiedDest))
                {
                    _logger.LogInformation("[{SessionId}] 🚫 Disambiguation pending — rejecting sync without full address qualifier", SessionId);
                    return new
                    {
                        success = false,
                        error = "Please specify which city or area you mean. I need the full location to proceed."
                    };
                }
                
                // Addresses are now disambiguated — clear the flag
                Interlocked.Exchange(ref _disambiguationPending, 0);
                _logger.LogInformation("[{SessionId}] ✅ Address disambiguation confirmed", SessionId);
            }
            else if (name == "book_taxi")
            {
                // Block booking if still awaiting disambiguation
                _logger.LogInformation("[{SessionId}] 🚫 Disambiguation pending — rejecting book_taxi", SessionId);
                return new
                {
                    success = false,
                    error = "Please clarify which location you mean before confirming the booking."
                };
            }
        }
        
        return name switch
        {
            "sync_booking_data" => await HandleSyncBookingAsync(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
            "create_booking" => await HandleCreateBookingAsync(args),
            "find_local_events" => HandleFindLocalEvents(args),
            "end_call" => HandleEndCall(args),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }
    
    // =========================
    // SYNC BOOKING (fast path + auto-quote when all fields filled)
    // =========================
    
    private async Task<object> HandleSyncBookingAsync(Dictionary<string, object?> args)
    {
        // Track previous values to detect corrections
        var prevPickup = _booking.Pickup;
        var prevDest = _booking.Destination;
        var prevPax = _booking.Passengers;
        var prevTime = _booking.PickupTime;
        var prevName = _booking.Name;
        
        if (args.TryGetValue("caller_name", out var n)) _booking.Name = n?.ToString();
        if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p?.ToString();
        if (args.TryGetValue("destination", out var d)) _booking.Destination = d?.ToString();
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn))
            _booking.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var pt)) _booking.PickupTime = pt?.ToString();
        
        // ⚠️ CRITICAL SECURITY CHECK: Name must be collected FIRST
        // Reject any sync_booking_data that contains travel fields (pickup/dest/pax/time) without a name
        bool hasName = !string.IsNullOrWhiteSpace(_booking.Name);
        bool hasTravelField = !string.IsNullOrWhiteSpace(_booking.Pickup) ||
                             !string.IsNullOrWhiteSpace(_booking.Destination) ||
                             _booking.Passengers > 0 ||
                             !string.IsNullOrWhiteSpace(_booking.PickupTime);
        
        if (hasTravelField && !hasName)
        {
            _logger.LogWarning("[{SessionId}] ❌ BLOCKED: sync_booking_data called with travel fields but NO name. Forcing name collection.", SessionId);
            // Clear the travel fields that were just set — force the AI to ask for name first
            _booking.Pickup = prevPickup;
            _booking.Destination = prevDest;
            _booking.Passengers = prevPax;
            _booking.PickupTime = prevTime;
            _booking.Name = prevName;
            
            // Inject a system message to force the AI back on track
            // Build a reminder of what details the caller already mentioned so the AI doesn't re-ask
            var mentionedParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(args.GetValueOrDefault("pickup")?.ToString()))
                mentionedParts.Add($"pickup='{args["pickup"]}'");
            if (!string.IsNullOrWhiteSpace(args.GetValueOrDefault("destination")?.ToString()))
                mentionedParts.Add($"destination='{args["destination"]}'");
            if (args.TryGetValue("passengers", out var paxVal) && paxVal != null)
                mentionedParts.Add($"passengers={paxVal}");
            if (!string.IsNullOrWhiteSpace(args.GetValueOrDefault("pickup_time")?.ToString()))
                mentionedParts.Add($"time='{args["pickup_time"]}'");
            
            var memoryHint = mentionedParts.Count > 0
                ? $" The caller already mentioned: {string.Join(", ", mentionedParts)}. " +
                  "After getting their name, call sync_booking_data IMMEDIATELY with the name AND all these previously mentioned details together — do NOT ask for them again."
                : "";
            
            if (_aiClient is OpenAiG711Client g711)
            {
                await g711.InjectMessageAndRespondAsync(
                    $"[SYSTEM] CRITICAL: You must collect the caller's NAME first before collecting any travel details (pickup/destination/passengers/time). " +
                    $"Ask for their name now.{memoryHint}");
            }
            
            return new { success = true, warning = "name_required_first" };
        }
        
        // If any travel field changed after a fare was already calculated, reset fare to force re-quote
        bool travelFieldChanged = !string.IsNullOrWhiteSpace(_booking.Fare) && (
            !string.Equals(prevPickup, _booking.Pickup, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(prevDest, _booking.Destination, StringComparison.OrdinalIgnoreCase) ||
            prevPax != _booking.Passengers ||
            !string.Equals(prevTime, _booking.PickupTime, StringComparison.OrdinalIgnoreCase));
        
        if (travelFieldChanged)
        {
            _logger.LogInformation("[{SessionId}] 🔄 Travel field changed — resetting fare for re-quote", SessionId);
            _booking.Fare = null;
            _booking.Eta = null;
            _booking.PickupLat = _booking.PickupLon = _booking.DestLat = _booking.DestLon = null;
            _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
            _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
            
            if (_aiClient is OpenAiG711Client g711Reset)
                g711Reset.SetAwaitingConfirmation(false);
        }
        
        // Check if all travel fields are filled → auto-quote (matching WinForms fast-sync logic)
        bool allFieldsFilled = !string.IsNullOrWhiteSpace(_booking.Pickup)
            && !string.IsNullOrWhiteSpace(_booking.Destination)
            && _booking.Passengers > 0
            && !string.IsNullOrWhiteSpace(_booking.PickupTime)
            && string.IsNullOrWhiteSpace(_booking.Fare); // Only auto-quote once
        
        if (!allFieldsFilled)
        {
            // Fast path: just store data, skip geocoding, move on quickly
            _logger.LogInformation("[{SessionId}] ⚡ Fast sync: Name={Name}, Pickup={Pickup}, Dest={Dest}, Pax={Pax}, Time={Time}",
                SessionId, _booking.Name, _booking.Pickup ?? "?", _booking.Destination ?? "?",
                _booking.Passengers, _booking.PickupTime ?? "?");
            
            OnBookingUpdated?.Invoke(_booking.Clone());
            return new { success = true };
        }
        
        // Full path: all travel fields complete — calculate fare asynchronously
        // Return immediately with an interjection so Ada speaks while we calculate
        _logger.LogInformation("[{SessionId}] 💰 All travel fields filled — starting async fare calculation...", SessionId);
        
        // Fire-and-forget: calculate fare in background, then inject result
        Interlocked.Exchange(ref _autoQuoteInProgress, 1);
        _ = Task.Run(async () =>
        {
            try
            {
                var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
                var completed = await Task.WhenAny(aiTask, Task.Delay(10000));
                
                FareResult result;
                if (completed == aiTask)
                {
                    result = await aiTask;
                    
                    if (result.NeedsClarification)
                    {
                        _logger.LogInformation("[{SessionId}] ⚠️ Ambiguous addresses in auto-quote", SessionId);
                        // SET FLAG: block further sync/book calls until address is confirmed
                        Interlocked.Exchange(ref _disambiguationPending, 1);
                        
                        // Reset fare so re-quote happens after clarification
                        _booking.Fare = null;
                        _booking.Eta = null;
                        OnBookingUpdated?.Invoke(_booking.Clone());
                        
                        // Build alternatives list for the AI to present
                        var altsList = new List<string>();
                        if (result.PickupAlternatives?.Length > 0)
                            altsList.AddRange(result.PickupAlternatives.Select((a, i) => $"{i + 1}. {a}"));
                        if (result.DestAlternatives?.Length > 0)
                            altsList.AddRange(result.DestAlternatives.Select((a, i) => $"{i + 1}. {a}"));

                        string clarifMsg;
                        if (altsList.Count > 0)
                        {
                            var which = result.PickupAlternatives?.Length > 0 ? "pickup" : "destination";
                            clarifMsg = $"[SYSTEM] ⚠️ ADDRESS DISAMBIGUATION REQUIRED.\n" +
                                $"The {which} address is ambiguous. Present ONLY these alternatives to the caller and ask which one they mean:\n" +
                                string.Join("\n", altsList) + "\n\n" +
                                "Read each option to the caller and ask them to choose. " +
                                "Do NOT proceed with the booking until the caller picks one. " +
                                "After they answer, call sync_booking_data with the corrected address INCLUDING the city name.";
                        }
                        else if (!string.IsNullOrWhiteSpace(result.ClarificationMessage))
                        {
                            clarifMsg = $"[SYSTEM] ⚠️ ADDRESS DISAMBIGUATION REQUIRED.\n" +
                                $"{result.ClarificationMessage}\n" +
                                "Ask the caller to clarify. Do NOT proceed until they specify which location. " +
                                "After they answer, call sync_booking_data with the corrected address including the city name.";
                        }
                        else
                        {
                            clarifMsg = "[SYSTEM] ⚠️ ADDRESS DISAMBIGUATION REQUIRED. " +
                                "The address could not be uniquely resolved. Ask the caller which city or area they mean. " +
                                "Do NOT guess or assume. Do NOT proceed until they clarify.";
                        }
                        await _aiClient.InjectMessageAndRespondAsync(clarifMsg);
                        return;
                    }
                }
                else
                {
                    _logger.LogWarning("[{SessionId}] ⏱️ Auto-quote AI extraction timed out — skipping (book_taxi will handle)", SessionId);
                    return; // Don't fallback to geocoding here — it gives wrong results for partial addresses
                }
                
                // Guard: if book_taxi already set the fare while we were calculating, skip
                if (!string.IsNullOrWhiteSpace(_booking.Fare))
                {
                    _logger.LogInformation("[{SessionId}] 💰 Auto-quote skipped — fare already set by book_taxi: {Fare}", SessionId, _booking.Fare);
                    return;
                }
                
                // Store all geocoded results
                ApplyFareResult(result);
                
                if (_aiClient is OpenAiG711Client g711)
                    g711.SetAwaitingConfirmation(true);
                
                OnBookingUpdated?.Invoke(_booking.Clone());
                
                var spokenFare = FormatFareForSpeech(_booking.Fare);
                _logger.LogInformation("[{SessionId}] 💰 Auto-quote: {Fare} ({Spoken}), ETA: {Eta}",
                    SessionId, _booking.Fare, spokenFare, _booking.Eta);
                
                // Inject fare result with VERIFIED addresses into conversation
                var verifiedPickup = !string.IsNullOrWhiteSpace(_booking.PickupFormatted) ? _booking.PickupFormatted : _booking.Pickup;
                var verifiedDest = !string.IsNullOrWhiteSpace(_booking.DestFormatted) ? _booking.DestFormatted : _booking.Destination;
                var fareMsg = $"[FARE RESULT] Addresses VERIFIED. Say this EXACTLY:\n" +
                    $"\"Your pickup is {verifiedPickup}, going to {verifiedDest}. The fare is {spokenFare}, with an estimated arrival in {_booking.Eta}. Would you like to confirm this booking or change anything?\"\n" +
                    "You MUST include the VERIFIED addresses with postcodes — do NOT use the caller's original words.";
                await _aiClient.InjectMessageAndRespondAsync(fareMsg);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{SessionId}] Auto-quote failed, using fallback", SessionId);
                _booking.Fare = "£8.00";  // UK default
                _booking.Eta = "8 minutes";
                OnBookingUpdated?.Invoke(_booking.Clone());
                
                var fallbackPickup = !string.IsNullOrWhiteSpace(_booking.PickupFormatted) ? _booking.PickupFormatted : _booking.Pickup;
                var fallbackDest = !string.IsNullOrWhiteSpace(_booking.DestFormatted) ? _booking.DestFormatted : _booking.Destination;
                var fareMsg2 = $"[FARE RESULT] Addresses VERIFIED. Say this EXACTLY:\n" +
                    $"\"Your pickup is {fallbackPickup}, going to {fallbackDest}. The fare is approximately 8 pounds, with an estimated arrival in 8 minutes. Would you like to confirm this booking or change anything?\"\n" +
                    "You MUST include the addresses with postcodes — do NOT use the caller's original words.";
                await _aiClient.InjectMessageAndRespondAsync(fareMsg2);
            }
            finally
            {
                Interlocked.Exchange(ref _autoQuoteInProgress, 0);
            }
        });
        
        // Return immediately — Ada will say "let me get you a price" while fare calculates in background
        OnBookingUpdated?.Invoke(_booking.Clone());
        return new
        {
            success = true,
            message = "Say to the caller: 'Let me get you a price on that journey' — then wait for the fare result which will arrive shortly."
        };
    }
    
    // =========================
    // BOOK TAXI (request_quote / confirmed)
    // =========================
    
    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
        
        // GUARD: Prevent duplicate book_taxi confirmed calls (was firing twice in logs)
        if ((action == "confirmed" || action == null) && Interlocked.CompareExchange(ref _bookTaxiCompleted, 1, 0) == 1)
        {
            _logger.LogWarning("[{SessionId}] ❌ book_taxi REJECTED — already confirmed (ref: {Ref})", SessionId, _booking.BookingRef);
            return new
            {
                success = true,
                booking_ref = _booking.BookingRef ?? "already-booked",
                message = "The booking has already been confirmed. Tell the caller their reference number and ask if there's anything else."
            };
        }
        
        // GUARD: Reject book_taxi if fare recalculation is in progress (address was just corrected)
        if (Volatile.Read(ref _autoQuoteInProgress) == 1 && string.IsNullOrWhiteSpace(_booking.Fare))
        {
            _logger.LogWarning("[{SessionId}] ❌ book_taxi REJECTED — fare recalculation in progress after address correction", SessionId);
            return new
            {
                success = false,
                error = "STOP. A fare recalculation is in progress because the address was just changed. You MUST wait for the [FARE RESULT] message, read back the new verified addresses and fare, and get the user's explicit confirmation BEFORE calling book_taxi again."
            };
        }
        
        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Missing pickup or destination" };
            
            // ── Wait for in-progress auto-quote from sync_booking_data instead of starting a duplicate ──
            if (Volatile.Read(ref _autoQuoteInProgress) == 1)
            {
                _logger.LogInformation("[{SessionId}] 💰 Auto-quote in progress — waiting up to 12s...", SessionId);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref _autoQuoteInProgress) == 1 && sw.ElapsedMilliseconds < 12000)
                    await Task.Delay(200);
                
                if (!string.IsNullOrWhiteSpace(_booking.Fare))
                {
                    _logger.LogInformation("[{SessionId}] 💰 Auto-quote completed while waiting: {Fare}", SessionId, _booking.Fare);
                    // Fall through to reuse block below
                }
                else
                {
                    _logger.LogWarning("[{SessionId}] ⏱️ Auto-quote did not produce fare, proceeding with own calc", SessionId);
                }
            }
            
            // ── REUSE existing auto-quote if fare already calculated by sync_booking_data ──
            if (!string.IsNullOrWhiteSpace(_booking.Fare) && !string.IsNullOrWhiteSpace(_booking.Eta))
            {
                _logger.LogInformation("[{SessionId}] 💰 Reusing existing auto-quote: {Fare}, ETA: {Eta}",
                    SessionId, _booking.Fare, _booking.Eta);
                
                if (_aiClient is OpenAiG711Client g711q)
                    g711q.SetAwaitingConfirmation(true);
                
                var spokenFare = FormatFareForSpeech(_booking.Fare);
                var vpReuse = !string.IsNullOrWhiteSpace(_booking.PickupFormatted) ? _booking.PickupFormatted : _booking.Pickup;
                var vdReuse = !string.IsNullOrWhiteSpace(_booking.DestFormatted) ? _booking.DestFormatted : _booking.Destination;
                return new
                {
                    success = true,
                    fare = _booking.Fare,
                    fare_spoken = spokenFare,
                    eta = _booking.Eta,
                    verified_pickup = vpReuse,
                    verified_destination = vdReuse,
                    message = $"Read back the VERIFIED addresses to the caller: pickup is '{vpReuse}' going to '{vdReuse}'. The fare is {spokenFare}, estimated arrival {_booking.Eta}. Ask if they want to confirm or change anything."
                };
            }
            
            try
            {
                _logger.LogInformation("[{SessionId}] 💰 Starting AI address extraction for quote...", SessionId);
                
                // Use AI-powered extraction with 10s timeout
                var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
                var completed = await Task.WhenAny(aiTask, Task.Delay(10000));
                
                FareResult result;
                if (completed == aiTask)
                {
                    result = await aiTask;
                    
                    // Handle clarification needed
                    if (result.NeedsClarification)
                    {
                        _logger.LogInformation("[{SessionId}] ⚠️ Ambiguous addresses detected", SessionId);
                        // Build numbered alternatives list for the AI to present
                        var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                        var destAlts = result.DestAlternatives ?? Array.Empty<string>();
                        var allAlts = pickupAlts.Concat(destAlts).ToArray();
                        var numberedAlts = allAlts.Select((a, i) => $"{i + 1}. {a}").ToArray();

                        return new
                        {
                            success = false,
                            needs_clarification = true,
                            pickup_options = pickupAlts,
                            destination_options = destAlts,
                            alternatives_list = numberedAlts,
                            clarification_question = result.ClarificationMessage ?? "I found multiple locations with that name. Please confirm which one you meant.",
                            message = $"I found multiple matches for that address. Please ask the caller which one they mean:\n{string.Join("\n", numberedAlts)}"
                        };
                    }
                }
                else
                {
                    _logger.LogWarning("[{SessionId}] ⏱️ AI extraction timed out, using fallback", SessionId);
                    result = await _fareCalculator.CalculateAsync(_booking.Pickup, _booking.Destination, CallerId);
                }
                
                // Store results using helper
                ApplyFareResult(result);
                
                if (_aiClient is OpenAiG711Client g711r)
                    g711r.SetAwaitingConfirmation(true);
                
                OnBookingUpdated?.Invoke(_booking.Clone());
                _logger.LogInformation("[{SessionId}] 💰 Quote: {Fare} (pickup: {PickupCity}, dest: {DestCity})",
                    SessionId, result.Fare, result.PickupCity, result.DestCity);
                
                var spokenFareNew = FormatFareForSpeech(_booking.Fare);
                var vpNew = !string.IsNullOrWhiteSpace(_booking.PickupFormatted) ? _booking.PickupFormatted : _booking.Pickup;
                var vdNew = !string.IsNullOrWhiteSpace(_booking.DestFormatted) ? _booking.DestFormatted : _booking.Destination;
                return new
                {
                    success = true,
                    fare = _booking.Fare,
                    fare_spoken = spokenFareNew,
                    eta = _booking.Eta,
                    verified_pickup = vpNew,
                    verified_destination = vdNew,
                    message = $"Read back the VERIFIED addresses to the caller: pickup is '{vpNew}' going to '{vdNew}'. The fare is {spokenFareNew}, estimated arrival {_booking.Eta}. Ask if they want to confirm or change anything."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{SessionId}] Fare calculation failed", SessionId);
                _booking.Fare = "£8.00";
                _booking.Eta = "8 minutes";
                OnBookingUpdated?.Invoke(_booking.Clone());
                
                return new
                {
                    success = true,
                    fare = _booking.Fare,
                    fare_spoken = "8 pounds",
                    eta = _booking.Eta,
                    verified_pickup = _booking.Pickup,
                    verified_destination = _booking.Destination,
                    message = $"Read back to the caller: pickup is '{_booking.Pickup}' going to '{_booking.Destination}'. The fare is approximately 8 pounds, estimated arrival 8 minutes. Ask if they want to confirm or change anything."
                };
            }
        }
        
        if (action == "confirmed" || action == null)
        {
            // Ensure we have geocoded components before dispatch
            bool needsGeocode = string.IsNullOrWhiteSpace(_booking.PickupStreet)
                || (_booking.PickupLat == 0 && _booking.PickupLon == 0)
                || (_booking.DestLat == 0 && _booking.DestLon == 0);
            
            if (needsGeocode &&
                !string.IsNullOrWhiteSpace(_booking.Pickup) && !string.IsNullOrWhiteSpace(_booking.Destination))
            {
                try
                {
                    _logger.LogInformation("[{SessionId}] 🔄 Confirmed path: resolving addresses via Gemini...", SessionId);
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
                    
                    // Map geocoded data — use ??= to preserve existing values
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
                    
                    // Override zero coords if Gemini returned valid ones
                    if (_booking.PickupLat == 0 && result.PickupLat != 0) _booking.PickupLat = result.PickupLat;
                    if (_booking.PickupLon == 0 && result.PickupLon != 0) _booking.PickupLon = result.PickupLon;
                    if (_booking.DestLat == 0 && result.DestLat != 0) _booking.DestLat = result.DestLat;
                    if (_booking.DestLon == 0 && result.DestLon != 0) _booking.DestLon = result.DestLon;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Pre-dispatch geocode failed", SessionId);
                }
            }
            
            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
            
            if (_aiClient is OpenAiG711Client g711Conf)
                g711Conf.SetAwaitingConfirmation(false);
            
            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] ✅ Booked: {Ref}", SessionId, _booking.BookingRef);
            
            // CRITICAL: Snapshot booking BEFORE fire-and-forget, because DisposeAsync
            // calls _booking.Reset() which clears CallerPhone and all fields.
            // Without this, the iCabbi/BSQD dispatch races with disposal and gets empty data.
            var bookingSnapshot = _booking.Clone();
            var callerId = CallerId;
            
            // Fire off BSQD Dispatch + iCabbi + WhatsApp (wait for response to finish first)
            _ = Task.Run(async () =>
            {
                // Wait for Ada to finish speaking before dispatching
                if (_aiClient is OpenAiG711Client g711Wait)
                {
                    for (int i = 0; i < 50 && g711Wait.IsResponseActive; i++)
                        await Task.Delay(100);
                }
                
                await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
                
                // Fire-and-forget iCabbi if enabled
                if (_icabbiEnabled && _icabbi != null)
                {
                    try
                    {
                        var result = await _icabbi.CreateAndDispatchAsync(bookingSnapshot);
                        if (result.Success)
                            _logger.LogInformation("[{SessionId}] 🚕 iCabbi OK — Journey: {JourneyId}", SessionId, result.JourneyId);
                        else
                            _logger.LogWarning("[{SessionId}] ⚠ iCabbi failed: {Message}", SessionId, result.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{SessionId}] ❌ iCabbi dispatch error", SessionId);
                    }
                }
                
                await _dispatcher.SendWhatsAppAsync(callerId);
            });
            
            return new
            {
                success = true,
                booking_ref = _booking.BookingRef,
                message = $"Booking confirmed. Reference: {_booking.BookingRef}. Now tell the caller their reference number and ask 'Is there anything else I can help with?'. When the user says no or declines, you MUST say EXACTLY: 'Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.' — then call end_call. Do NOT call end_call until AFTER you have spoken the goodbye message."
            };
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
        
        // Use AI-powered extraction with 2s timeout
        try
        {
            var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(
                _booking.Pickup, _booking.Destination ?? _booking.Pickup, CallerId);
            var completed = await Task.WhenAny(aiTask, Task.Delay(10000));
            
            FareResult result;
            if (completed == aiTask)
            {
                result = await aiTask;
                
                // Handle clarification
                if (result.NeedsClarification)
                {
                    var pickupAlts = result.PickupAlternatives ?? Array.Empty<string>();
                    var destAlts = result.DestAlternatives ?? Array.Empty<string>();
                    var allAlts = pickupAlts.Concat(destAlts).ToArray();
                    var numberedAlts = allAlts.Select((a, i) => $"{i + 1}. {a}").ToArray();

                    return new
                    {
                        success = false,
                        needs_clarification = true,
                        pickup_options = pickupAlts,
                        destination_options = destAlts,
                        alternatives_list = numberedAlts,
                        clarification_question = result.ClarificationMessage ?? "I found multiple locations with that name. Which one did you mean?",
                        message = $"I found multiple matches for that address. Please ask the caller which one they mean:\n{string.Join("\n", numberedAlts)}"
                    };
                }
            }
            else
            {
                _logger.LogWarning("[{SessionId}] ⏱️ AI extraction timeout, using fallback", SessionId);
                result = await _fareCalculator.CalculateAsync(
                    _booking.Pickup, _booking.Destination ?? _booking.Pickup, CallerId);
            }
            
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
        
        // Dispatch (fire-and-forget)
        _ = _dispatcher.DispatchAsync(_booking, CallerId);
        _ = _dispatcher.SendWhatsAppAsync(CallerId);
        
        // Post-booking timeout: force farewell after 15s if user doesn't respond
        _ = Task.Run(async () =>
        {
            await Task.Delay(15000);
            if (!IsActive) return;
            if (_aiClient is OpenAiG711Client g711)
            {
                if (!g711.IsConnected) return;
                g711.CancelDeferredResponse();
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
    
    private static string FormatFareForSpeech(string? fare)
    {
        if (string.IsNullOrEmpty(fare)) return "unknown";
        
        // Detect currency from the fare string
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
    
    /// <summary>Apply fare result fields to booking state (DRY helper).</summary>
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
    
    private object HandleFindLocalEvents(Dictionary<string, object?> args)
    {
        var category = args.TryGetValue("category", out var cat) ? cat?.ToString() ?? "all" : "all";
        var near = args.TryGetValue("near", out var n) ? n?.ToString() : null;
        var date = args.TryGetValue("date", out var dt) ? dt?.ToString() ?? "this weekend" : "this weekend";
        
        _logger.LogInformation("[{SessionId}] 🎭 Events lookup: {Category} near {Near} on {Date}",
            SessionId, category, near ?? "unknown", date);
        
        // Mock response - in production this would call an events API
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
    
    private object HandleEndCall(Dictionary<string, object?> args)
    {
        // Drain-aware hangup: wait for playout buffer to empty before ending
        _ = Task.Run(async () =>
        {
            if (_aiClient is OpenAiG711Client g711)
            {
                // Phase 1: Wait for OpenAI to finish streaming (response still active)
                var streamStart = Environment.TickCount64;
                const int MAX_STREAM_WAIT_MS = 15000;
                while (g711.IsResponseActive &&
                       Environment.TickCount64 - streamStart < MAX_STREAM_WAIT_MS)
                {
                    await Task.Delay(200);
                }
                
                // Phase 2: Wait for audio to actually enter the playout buffer
                const int MAX_ENQUEUE_WAIT_MS = 5000;
                var enqueueStart = Environment.TickCount64;
                while ((g711.GetQueuedFrames?.Invoke() ?? 0) == 0 &&
                       Environment.TickCount64 - enqueueStart < MAX_ENQUEUE_WAIT_MS)
                {
                    await Task.Delay(100);
                }
                
                // Phase 3: Settling delay to let buffer fill fully
                await Task.Delay(2000);
                
                // Phase 4: Poll playout queue until drained (max 20s safety)
                var drainStart = Environment.TickCount64;
                const int MAX_DRAIN_MS = 20000;
                const int POLL_MS = 100;
                
                while (Environment.TickCount64 - drainStart < MAX_DRAIN_MS)
                {
                    var queued = g711.GetQueuedFrames?.Invoke() ?? 0;
                    if (queued == 0)
                    {
                        _logger.LogInformation("[{SessionId}] ✅ Audio drained, ending call", SessionId);
                        break;
                    }
                    await Task.Delay(POLL_MS);
                }
                
                // Extra margin so last frames reach the phone
                await Task.Delay(1000);
            }
            else
            {
                // Fallback: fixed delay for non-G711 clients
                await Task.Delay(5000);
            }
            
            await EndAsync("end_call tool");
        });
        
        return new { success = true };
    }
    
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        await EndAsync("disposed");
        
        // ── Unwire ALL event handlers to prevent stale references ──
        _aiClient.OnAudio -= HandleAiAudio;
        _aiClient.OnToolCall -= HandleToolCallAsync;
        _aiClient.OnTranscript -= null; // Lambda — clear via field below
        _aiClient.OnEnded -= null;      // Lambda — cleared via DisposeAsync on AI client
        
        // Clear events on this session so MainForm/SipServer closures don't hold references
        OnEnded = null;
        OnBookingUpdated = null;
        OnTranscript = null;
        OnAudioOut = null;
        OnBargeIn = null;
        
        // Reset booking state fully
        _booking.Reset();
        Interlocked.Exchange(ref _autoQuoteInProgress, 0);
        Interlocked.Exchange(ref _bookTaxiCompleted, 0);
        Interlocked.Exchange(ref _disambiguationPending, 0);
        
        // Dispose AI client (closes WebSocket, stops log thread, disposes CTS)
        if (_aiClient is IAsyncDisposable disposableAi)
        {
            try { await disposableAi.DisposeAsync(); }
            catch { /* swallow — best effort cleanup */ }
        }
        
        _logger.LogInformation("[{SessionId}] 🧹 CallSession fully disposed — all state cleared", SessionId);
    }
}
