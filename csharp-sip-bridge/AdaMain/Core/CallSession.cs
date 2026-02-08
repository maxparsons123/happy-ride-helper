using AdaMain.Ai;
using AdaMain.Ai;
using AdaMain.Config;
using AdaMain.Services;
using Microsoft.Extensions.Logging;

namespace AdaMain.Core;

/// <summary>
/// Manages a single call session lifecycle with G.711 A-law passthrough.
/// No resampling - direct 8kHz A-law in/out.
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
    
    /// <summary>Feed raw G.711 A-law RTP audio from SIP - direct passthrough.</summary>
    public void ProcessInboundAudio(byte[] alawRtp)
    {
        if (!IsActive || alawRtp.Length == 0)
            return;
        
        // Echo guard: skip audio right after AI speaks
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var echoGuardMs = _settings.Audio.EchoGuardMs > 0 ? _settings.Audio.EchoGuardMs : 180;
        if (now - Volatile.Read(ref _lastAdaFinishedAt) < echoGuardMs)
            return;
        
        // Direct passthrough - no resampling!
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
            "sync_booking_data" => HandleSyncBooking(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
            "create_booking" => await HandleCreateBookingAsync(args),
            "end_call" => HandleEndCall(args),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }
    
    private object HandleSyncBooking(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("caller_name", out var n)) _booking.Name = n?.ToString();
        if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p?.ToString();
        if (args.TryGetValue("destination", out var d)) _booking.Destination = d?.ToString();
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn)) 
            _booking.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var pt)) _booking.PickupTime = pt?.ToString();
        
        OnBookingUpdated?.Invoke(_booking.Clone());
        return new { success = true };
    }
    
    private async Task<object> HandleBookTaxiAsync(Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
        
        if (action == "request_quote")
        {
            var result = await _fareCalculator.CalculateAsync(_booking.Pickup, _booking.Destination, CallerId);
            
            // Store results
            _booking.Fare = result.Fare;
            _booking.Eta = result.Eta;
            _booking.PickupLat = result.PickupLat;
            _booking.PickupLon = result.PickupLon;
            _booking.DestLat = result.DestLat;
            _booking.DestLon = result.DestLon;
            _booking.PickupStreet = result.PickupStreet;
            _booking.PickupNumber = result.PickupNumber;
            _booking.PickupPostalCode = result.PickupPostalCode;
            _booking.PickupCity = result.PickupCity;
            _booking.PickupFormatted = result.PickupFormatted;
            _booking.DestStreet = result.DestStreet;
            _booking.DestNumber = result.DestNumber;
            _booking.DestPostalCode = result.DestPostalCode;
            _booking.DestCity = result.DestCity;
            _booking.DestFormatted = result.DestFormatted;
            
            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] Quote: {Fare}", SessionId, result.Fare);
            
            return new
            {
                success = true,
                fare = result.Fare,
                eta = result.Eta,
                message = $"Fare is {result.Fare}, driver arrives in {result.Eta}. Book it?"
            };
        }
        
        if (action == "confirmed")
        {
            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
            
            OnBookingUpdated?.Invoke(_booking.Clone());
            _logger.LogInformation("[{SessionId}] Booked: {Ref}", SessionId, _booking.BookingRef);
            
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
    
    private async Task<object> HandleCreateBookingAsync(Dictionary<string, object?> args)
    {
        // Straight-through booking - no confirmation step
        if (args.TryGetValue("pickup_address", out var pu)) _booking.Pickup = pu?.ToString();
        if (args.TryGetValue("dropoff_address", out var dd)) _booking.Destination = dd?.ToString();
        if (args.TryGetValue("passenger_count", out var pc) && int.TryParse(pc?.ToString(), out var pn))
            _booking.Passengers = pn;
        
        if (string.IsNullOrWhiteSpace(_booking.Pickup))
            return new { success = false, error = "Missing pickup address" };
        
        // Calculate fare
        var result = await _fareCalculator.CalculateAsync(_booking.Pickup, _booking.Destination, CallerId);
        
        _booking.Fare = result.Fare;
        _booking.Eta = result.Eta;
        _booking.PickupLat = result.PickupLat;
        _booking.PickupLon = result.PickupLon;
        _booking.DestLat = result.DestLat;
        _booking.DestLon = result.DestLon;
        _booking.PickupFormatted = result.PickupFormatted;
        _booking.DestFormatted = result.DestFormatted;
        _booking.Confirmed = true;
        _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
        
        OnBookingUpdated?.Invoke(_booking.Clone());
        _logger.LogInformation("[{SessionId}] Booked: {Ref}, Fare: {Fare}, ETA: {Eta}",
            SessionId, _booking.BookingRef, _booking.Fare, _booking.Eta);
        
        // Dispatch (fire-and-forget)
        _ = _dispatcher.DispatchAsync(_booking, CallerId);
        _ = _dispatcher.SendWhatsAppAsync(CallerId);
        
        // Format fare for speech
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
        
        // Strip currency symbols and parse
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
        // End after a grace period to let audio drain
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
