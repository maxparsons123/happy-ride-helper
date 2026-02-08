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
    private readonly IFareCalculator _fareCalculator;
    private readonly IDispatcher _dispatcher;
    
    
    private readonly BookingState _booking = new();
    
    private int _disposed;
    private int _active;
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
        IDispatcher dispatcher)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _logger = logger;
        _settings = settings;
        _aiClient = aiClient;
        _fareCalculator = fareCalculator;
        _dispatcher = dispatcher;
        
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
    
    /// <summary>Direct A-law frames from OpenAI → playout via event.</summary>
    private void HandleAiAudio(byte[] alawFrame)
    {
        OnAudioOut?.Invoke(alawFrame);
    }
    
    private async Task<object> HandleToolCallAsync(string name, Dictionary<string, object?> args)
    {
        _logger.LogDebug("[{SessionId}] Tool call: {Name}", SessionId, name);
        
        return name switch
        {
            "sync_booking_data" => await HandleSyncBookingAsync(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
            "create_booking" => await HandleCreateBookingAsync(args),
            "end_call" => HandleEndCall(args),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }
    
    // =========================
    // SYNC BOOKING (with address verification via edge function)
    // =========================
    
    private async Task<object> HandleSyncBookingAsync(Dictionary<string, object?> args)
    {
        string? newPickup = null, newDest = null;
        
        if (args.TryGetValue("caller_name", out var n)) _booking.Name = n?.ToString();
        if (args.TryGetValue("pickup", out var p))
        {
            newPickup = p?.ToString();
            _booking.Pickup = newPickup;
        }
        if (args.TryGetValue("destination", out var d))
        {
            newDest = d?.ToString();
            _booking.Destination = newDest;
        }
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn))
            _booking.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var pt)) _booking.PickupTime = pt?.ToString();
        
        // Verify new addresses with edge function in parallel (non-blocking, fire-and-forget enrichment)
        var verifyTasks = new List<Task>();
        
        if (!string.IsNullOrWhiteSpace(newPickup))
        {
            verifyTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(newPickup, null, CallerId);
                    if (result.PickupLat.HasValue && result.PickupLat.Value != 0)
                    {
                        _booking.PickupLat = result.PickupLat;
                        _booking.PickupLon = result.PickupLon;
                        _booking.PickupStreet = result.PickupStreet;
                        _booking.PickupNumber = result.PickupNumber;
                        _booking.PickupPostalCode = result.PickupPostalCode;
                        _booking.PickupCity = result.PickupCity;
                        _booking.PickupFormatted = result.PickupFormatted;
                        _logger.LogInformation("[{SessionId}] 📍 Pickup verified: {Number} {Street}, {City} ({PostalCode})",
                            SessionId, result.PickupNumber, result.PickupStreet, result.PickupCity, result.PickupPostalCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Pickup verification failed", SessionId);
                }
            }));
        }
        
        if (!string.IsNullOrWhiteSpace(newDest))
        {
            verifyTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(null, newDest, CallerId);
                    if (result.DestLat.HasValue && result.DestLat.Value != 0)
                    {
                        _booking.DestLat = result.DestLat;
                        _booking.DestLon = result.DestLon;
                        _booking.DestStreet = result.DestStreet;
                        _booking.DestNumber = result.DestNumber;
                        _booking.DestPostalCode = result.DestPostalCode;
                        _booking.DestCity = result.DestCity;
                        _booking.DestFormatted = result.DestFormatted;
                        _logger.LogInformation("[{SessionId}] 📍 Dest verified: {Number} {Street}, {City} ({PostalCode})",
                            SessionId, result.DestNumber, result.DestStreet, result.DestCity, result.DestPostalCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Dest verification failed", SessionId);
                }
            }));
        }
        
        // Wait for verification (with 2s timeout to not block the conversation)
        if (verifyTasks.Count > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(verifyTasks),
                Task.Delay(2000));
        }
        
        OnBookingUpdated?.Invoke(_booking.Clone());
        return new { success = true };
    }
    
    // =========================
    // BOOK TAXI (request_quote / confirmed)
    // =========================
    
    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
        
        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                return new { success = false, error = "Missing pickup or destination" };
            
            try
            {
                _logger.LogInformation("[{SessionId}] 💰 Starting AI address extraction for quote...", SessionId);
                
                // Use AI-powered extraction with 2s timeout
                var aiTask = _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
                var completed = await Task.WhenAny(aiTask, Task.Delay(2500));
                
                FareResult result;
                if (completed == aiTask)
                {
                    result = await aiTask;
                    
                    // Handle clarification needed
                    if (result.NeedsClarification)
                    {
                        _logger.LogInformation("[{SessionId}] ⚠️ Ambiguous addresses detected", SessionId);
                        return new
                        {
                            success = false,
                            needs_clarification = true,
                            pickup_options = result.PickupAlternatives ?? Array.Empty<string>(),
                            destination_options = result.DestAlternatives ?? Array.Empty<string>(),
                            message = "I found multiple locations with that name. Please confirm which one you meant."
                        };
                    }
                }
                else
                {
                    _logger.LogWarning("[{SessionId}] ⏱️ AI extraction timed out, using fallback", SessionId);
                    result = await _fareCalculator.CalculateAsync(_booking.Pickup, _booking.Destination, CallerId);
                }
                
                // Store results
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
                
                OnBookingUpdated?.Invoke(_booking.Clone());
                _logger.LogInformation("[{SessionId}] 💰 Quote: {Fare} (pickup: {PickupCity}, dest: {DestCity})",
                    SessionId, result.Fare, result.PickupCity, result.DestCity);
                
                var spokenFare = FormatFareForSpeech(_booking.Fare);
                return new
                {
                    success = true,
                    fare = _booking.Fare,
                    fare_spoken = spokenFare,
                    eta = _booking.Eta,
                    message = $"The fare is {spokenFare}, and the driver will arrive in {_booking.Eta}. Ask if they want to confirm."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{SessionId}] Fare calculation failed", SessionId);
                _booking.Fare = "£12.50";
                _booking.Eta = "6 minutes";
                OnBookingUpdated?.Invoke(_booking.Clone());
                
                return new
                {
                    success = true,
                    fare = _booking.Fare,
                    fare_spoken = "12 pounds 50",
                    eta = _booking.Eta,
                    message = $"The fare is approximately 12 pounds 50, driver arrives in 6 minutes."
                };
            }
        }
        
        if (action == "confirmed")
        {
            // Ensure we have geocoded components before dispatch
            if ((string.IsNullOrWhiteSpace(_booking.PickupStreet) || string.IsNullOrWhiteSpace(_booking.DestStreet)) &&
                !string.IsNullOrWhiteSpace(_booking.Pickup) && !string.IsNullOrWhiteSpace(_booking.Destination))
            {
                try
                {
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(_booking.Pickup, _booking.Destination, CallerId);
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
                    _booking.Fare ??= result.Fare;
                    _booking.Eta ??= result.Eta;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{SessionId}] Pre-dispatch geocode failed", SessionId);
                }
            }
            
            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
            
            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] ✅ Booked: {Ref}", SessionId, _booking.BookingRef);
            
            // Dispatch and notify (fire-and-forget)
            _ = _dispatcher.DispatchAsync(_booking, CallerId);
            _ = _dispatcher.SendWhatsAppAsync(CallerId);
            
            return new
            {
                success = true,
                booking_ref = _booking.BookingRef,
                message = string.IsNullOrWhiteSpace(_booking.Name)
                    ? "Your taxi is booked!"
                    : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
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
            var completed = await Task.WhenAny(aiTask, Task.Delay(2000));
            
            FareResult result;
            if (completed == aiTask)
            {
                result = await aiTask;
                
                // Handle clarification
                if (result.NeedsClarification)
                {
                    return new
                    {
                        success = false,
                        needs_clarification = true,
                        pickup_options = result.PickupAlternatives ?? Array.Empty<string>(),
                        destination_options = result.DestAlternatives ?? Array.Empty<string>(),
                        message = "I found multiple locations with that name. Which one did you mean?"
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
        
        var clean = fare.Replace("€", "").Replace("£", "").Replace("$", "").Trim();
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any, 
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            var euros = (int)amount;
            var cents = (int)((amount - euros) * 100);
            return cents > 0 ? $"{euros} euros {cents}" : $"{euros} euros";
        }
        return fare;
    }
    
    private object HandleEndCall(Dictionary<string, object?> args)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
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
    }
}
