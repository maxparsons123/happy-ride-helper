using AdaMain.Ai;
using AdaMain.Config;
using AdaMain.Services;
using Microsoft.Extensions.Logging;

namespace AdaMain.Core;

/// <summary>
/// Manages a single call session lifecycle with G.711 A-law passthrough.
/// Simplified flow matching openrealtimebest: sync stores data, book_taxi does quote/confirm.
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
    private int _bookTaxiCompleted;   // Guard: prevent duplicate book_taxi confirmed calls
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
    
    /// <summary>Feed raw G.711 A-law RTP audio from SIP - direct passthrough.</summary>
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
        _logger.LogDebug("[{SessionId}] Tool call: {Name} (args: {ArgCount})", SessionId, name, args.Count);
        
        return name switch
        {
            "sync_booking_data" => HandleSyncBookingData(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
            "create_booking" => await HandleCreateBookingAsync(args),
            "find_local_events" => HandleFindLocalEvents(args),
            "end_call" => HandleEndCall(args),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }
    
    // =========================
    // SYNC BOOKING DATA (simple store — no guards, no auto-quote)
    // =========================
    
    private object HandleSyncBookingData(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("caller_name", out var n))
            _booking.Name = n?.ToString()?.Trim();
        
        if (args.TryGetValue("pickup", out var p))
        {
            var incoming = NormalizeHouseNumber(p?.ToString(), "pickup");
            if (StreetNameChanged(_booking.Pickup, incoming))
            {
                _booking.PickupLat = _booking.PickupLon = null;
                _booking.PickupStreet = _booking.PickupNumber = _booking.PickupPostalCode = _booking.PickupCity = _booking.PickupFormatted = null;
                _logger.LogInformation("[{SessionId}] 🧹 Pickup street changed — cleared geocoded data", SessionId);
            }
            _booking.Pickup = incoming;
        }
        
        if (args.TryGetValue("destination", out var d))
        {
            var incoming = NormalizeHouseNumber(d?.ToString(), "destination");
            if (StreetNameChanged(_booking.Destination, incoming))
            {
                _booking.DestLat = _booking.DestLon = null;
                _booking.DestStreet = _booking.DestNumber = _booking.DestPostalCode = _booking.DestCity = _booking.DestFormatted = null;
                _logger.LogInformation("[{SessionId}] 🧹 Destination street changed — cleared geocoded data", SessionId);
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
            _booking.PickupTime = pt?.ToString();
        if (args.TryGetValue("vehicle_type", out var vt) && !string.IsNullOrWhiteSpace(vt?.ToString()))
            _booking.VehicleType = vt.ToString()!;
        
        _logger.LogInformation("[{SessionId}] ⚡ Sync: Name={Name}, Pickup={Pickup}, Dest={Dest}, Pax={Pax}, Time={Time}, Vehicle={Vehicle}",
            SessionId, _booking.Name ?? "?", _booking.Pickup ?? "?", _booking.Destination ?? "?",
            _booking.Passengers, _booking.PickupTime ?? "?", _booking.VehicleType);
        
        OnBookingUpdated?.Invoke(_booking.Clone());
        return new { success = true };
    }
    
    // =========================
    // BOOK TAXI (request_quote / confirmed)
    // =========================
    
    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
        
        // DEBUG: Log all args received
        var argsSummary = string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
        _logger.LogInformation("[{SessionId}] 📥 book_taxi args: {Args}", SessionId, argsSummary);
        
        // SAFETY NET: Always populate _booking from book_taxi args (overwrite if provided)
        if (args.TryGetValue("caller_name", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString()))
            _booking.Name = bn.ToString()!.Trim();
        if (args.TryGetValue("pickup", out var bp) && !string.IsNullOrWhiteSpace(bp?.ToString()))
            _booking.Pickup = bp.ToString();
        if (args.TryGetValue("destination", out var bd) && !string.IsNullOrWhiteSpace(bd?.ToString()))
            _booking.Destination = bd.ToString();
        if (args.TryGetValue("passengers", out var bpax) && int.TryParse(bpax?.ToString(), out var bpn))
            _booking.Passengers = bpn;
        if (args.TryGetValue("pickup_time", out var bpt) && !string.IsNullOrWhiteSpace(bpt?.ToString()))
            _booking.PickupTime = bpt.ToString();
        
        _logger.LogInformation("[{SessionId}] 📋 After merge: Pickup={Pickup}, Dest={Dest}, Name={Name}, Pax={Pax}",
            SessionId, _booking.Pickup ?? "NULL", _booking.Destination ?? "NULL", _booking.Name ?? "NULL", _booking.Passengers);
        
        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Missing pickup or destination. Call sync_booking_data first with all collected details, then retry." };
            
            try
            {
                _logger.LogInformation("[{SessionId}] 💰 Starting AI address extraction for quote...", SessionId);
                
                var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
                var completed = await Task.WhenAny(aiTask, Task.Delay(10000));
                
                FareResult result;
                if (completed == aiTask)
                {
                    result = await aiTask;
                    
                    // Handle ambiguous addresses
                    if (result.NeedsClarification)
                    {
                        _logger.LogInformation("[{SessionId}] ⚠️ Ambiguous addresses detected", SessionId);
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
                            message = "I found multiple locations. Please ask the caller which city or area they are in."
                        };
                    }
                }
                else
                {
                    _logger.LogWarning("[{SessionId}] ⏱️ AI extraction timed out, using fallback", SessionId);
                    result = await _fareCalculator.CalculateAsync(_booking.Pickup, _booking.Destination, CallerId);
                }
                
                // Store geocoded results
                ApplyFareResult(result);
                
                if (_aiClient is OpenAiG711Client g711q)
                    g711q.SetAwaitingConfirmation(true);
                
                OnBookingUpdated?.Invoke(_booking.Clone());
                
                var spokenFare = FormatFareForSpeech(_booking.Fare);
                _logger.LogInformation("[{SessionId}] 💰 Quote: {Fare} ({Spoken}), ETA: {Eta}",
                    SessionId, _booking.Fare, spokenFare, _booking.Eta);
                
                return new
                {
                    success = true,
                    fare = _booking.Fare,
                    fare_spoken = spokenFare,
                    eta = _booking.Eta,
                    message = $"The fare is {spokenFare} and ETA is {_booking.Eta}. Ask to confirm."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{SessionId}] Fare calculation failed", SessionId);
                _booking.Fare = "£8.00";
                _booking.Eta = "8 minutes";
                OnBookingUpdated?.Invoke(_booking.Clone());
                return new { success = true, fare = "£8.00", fare_spoken = "8 pounds", eta = "8 minutes" };
            }
        }
        
        if (action == "confirmed")
        {
            // GUARD: Reject if essential booking data is missing
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
            {
                _logger.LogWarning("[{SessionId}] ❌ book_taxi confirmed REJECTED — missing pickup/destination", SessionId);
                return new { success = false, error = "Cannot confirm booking: pickup or destination is missing. Call sync_booking_data first with all details, then request_quote, then confirmed." };
            }
            
            // GUARD: Prevent duplicate confirmed calls
            if (Interlocked.CompareExchange(ref _bookTaxiCompleted, 1, 0) == 1)
            {
                _logger.LogWarning("[{SessionId}] ❌ book_taxi REJECTED — already confirmed (ref: {Ref})", SessionId, _booking.BookingRef);
                return new
                {
                    success = true,
                    booking_ref = _booking.BookingRef ?? "already-booked",
                    message = "The booking has already been confirmed."
                };
            }
            
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
            
            // Snapshot before fire-and-forget (disposal can reset booking)
            var bookingSnapshot = _booking.Clone();
            var callerId = CallerId;
            
            // Fire off dispatch (wait for response to finish first)
            _ = Task.Run(async () =>
            {
                if (_aiClient is OpenAiG711Client g711Wait)
                {
                    for (int i = 0; i < 50 && g711Wait.IsResponseActive; i++)
                        await Task.Delay(100);
                }
                
                await _dispatcher.DispatchAsync(bookingSnapshot, callerId);
                
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
                message = "Taxi booked!"
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
        
        try
        {
            var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(
                _booking.Pickup, _booking.Destination ?? _booking.Pickup, CallerId);
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
        
        _ = _dispatcher.DispatchAsync(_booking, CallerId);
        _ = _dispatcher.SendWhatsAppAsync(CallerId);
        
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
    
    private static bool StreetNameChanged(string? oldAddress, string? newAddress)
    {
        if (string.IsNullOrWhiteSpace(oldAddress) || string.IsNullOrWhiteSpace(newAddress))
            return false;
        
        string Normalize(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"\d|[^a-z ]", "").Trim();
        
        return Normalize(oldAddress) != Normalize(newAddress);
    }

    private static readonly System.Text.RegularExpressions.Regex _houseNumberFixRegex =
        new(@"^(\d{1,3})(8|3|4)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private string? NormalizeHouseNumber(string? address, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;

        var match = _houseNumberFixRegex.Match(address.Trim());
        if (!match.Success) return address;

        var baseNum = int.Parse(match.Groups[1].Value);
        var trailingDigit = match.Groups[2].Value;

        if (baseNum < 1 || baseNum > 199) return address;

        var letter = trailingDigit switch
        {
            "8" => "A",
            "3" => "B",
            "4" => "D",
            _ => null
        };

        if (letter == null) return address;

        var corrected = address.Trim();
        var original = match.Value;
        var replacement = $"{baseNum}{letter}";
        corrected = replacement + corrected[original.Length..];

        _logger.LogInformation("[{SessionId}] 🔤 House number auto-corrected ({Field}): '{Original}' → '{Corrected}'",
            SessionId, fieldName, original, replacement);

        return corrected;
    }
    
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
    
    private object HandleEndCall(Dictionary<string, object?> args)
    {
        _ = Task.Run(async () =>
        {
            if (_aiClient is OpenAiG711Client g711)
            {
                // Phase 1: Wait for OpenAI to finish streaming
                var streamStart = Environment.TickCount64;
                const int MAX_STREAM_WAIT_MS = 15000;
                while (g711.IsResponseActive &&
                       Environment.TickCount64 - streamStart < MAX_STREAM_WAIT_MS)
                {
                    await Task.Delay(200);
                }
                
                // Phase 2: Wait for audio to enter playout buffer
                const int MAX_ENQUEUE_WAIT_MS = 5000;
                var enqueueStart = Environment.TickCount64;
                while ((g711.GetQueuedFrames?.Invoke() ?? 0) == 0 &&
                       Environment.TickCount64 - enqueueStart < MAX_ENQUEUE_WAIT_MS)
                {
                    await Task.Delay(100);
                }
                
                // Phase 3: Settling delay
                await Task.Delay(2000);
                
                // Phase 4: Poll playout queue until drained
                var drainStart = Environment.TickCount64;
                const int MAX_DRAIN_MS = 20000;
                
                while (Environment.TickCount64 - drainStart < MAX_DRAIN_MS)
                {
                    var queued = g711.GetQueuedFrames?.Invoke() ?? 0;
                    if (queued == 0)
                    {
                        _logger.LogInformation("[{SessionId}] ✅ Audio drained, ending call", SessionId);
                        break;
                    }
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
    
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        await EndAsync("disposed");
        
        _aiClient.OnAudio -= HandleAiAudio;
        _aiClient.OnToolCall -= HandleToolCallAsync;
        _aiClient.OnTranscript -= null;
        _aiClient.OnEnded -= null;
        
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
            catch { /* swallow — best effort cleanup */ }
        }
        
        _logger.LogInformation("[{SessionId}] 🧹 CallSession fully disposed — all state cleared", SessionId);
    }
}
