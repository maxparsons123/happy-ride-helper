using System.Collections.Concurrent;
using AdaMain.Ai;
using AdaMain.Audio;
using AdaMain.Config;
using AdaMain.Services;
using Microsoft.Extensions.Logging;

namespace AdaMain.Core;

/// <summary>
/// Manages a single call session lifecycle.
/// Coordinates audio flow between SIP and OpenAI.
/// </summary>
public sealed class CallSession : ICallSession
{
    private readonly ILogger<CallSession> _logger;
    private readonly AppSettings _settings;
    private readonly IOpenAiClient _aiClient;
    private readonly IAudioCodec _codec;
    private readonly IFareCalculator _fareCalculator;
    private readonly IDispatcher _dispatcher;
    
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
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
    
    public CallSession(
        string sessionId,
        string callerId,
        ILogger<CallSession> logger,
        AppSettings settings,
        IOpenAiClient aiClient,
        IAudioCodec codec,
        IFareCalculator fareCalculator,
        IDispatcher dispatcher)
    {
        SessionId = sessionId;
        CallerId = callerId;
        _logger = logger;
        _settings = settings;
        _aiClient = aiClient;
        _codec = codec;
        _fareCalculator = fareCalculator;
        _dispatcher = dispatcher;
        
        // Wire up AI client events
        _aiClient.OnAudio += HandleAiAudio;
        _aiClient.OnToolCall += HandleToolCallAsync;
        _aiClient.OnEnded += reason => _ = EndAsync(reason);
        _aiClient.OnPlayoutComplete += () => Volatile.Write(ref _lastAdaFinishedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _active, 1) == 1)
            return;
        
        _logger.LogInformation("[{SessionId}] Starting session for {CallerId}", SessionId, CallerId);
        
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
    
    public void ProcessInboundAudio(byte[] g711Data)
    {
        if (!IsActive || g711Data.Length == 0)
            return;
        
        // Echo guard: skip audio right after AI speaks
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - Volatile.Read(ref _lastAdaFinishedAt) < _settings.Audio.EchoGuardMs)
            return;
        
        // Decode G.711 → PCM16 @ 8kHz
        var pcm8k = _codec.Decode(g711Data);
        
        // Resample 8kHz → 24kHz for OpenAI
        var pcm24k = AudioResampler.Resample8kTo24k(pcm8k);
        
        // Apply telephony DSP (volume boost, pre-emphasis)
        var processed = TelephonyDsp.ProcessInbound(pcm24k, (float)_settings.Audio.VolumeBoost);
        
        // Send to AI
        _aiClient.SendAudio(processed);
    }
    
    public byte[]? GetOutboundFrame()
    {
        return _outboundQueue.TryDequeue(out var frame) ? frame : null;
    }
    
    private void HandleAiAudio(byte[] pcm24k)
    {
        // Resample 24kHz → 8kHz
        var pcm8k = AudioResampler.Resample24kTo8k(pcm24k);
        
        // Encode PCM16 → G.711
        var g711 = _codec.Encode(pcm8k);
        
        // Frame into 20ms chunks (160 bytes for G.711)
        for (int i = 0; i < g711.Length; i += 160)
        {
            var frame = new byte[160];
            var count = Math.Min(160, g711.Length - i);
            Array.Copy(g711, i, frame, 0, count);
            
            // Pad short frames with silence
            if (count < 160)
            {
                var silence = _codec.SilenceByte;
                Array.Fill(frame, silence, count, 160 - count);
            }
            
            // Overflow protection
            while (_outboundQueue.Count >= 800)
                _outboundQueue.TryDequeue(out _);
            
            _outboundQueue.Enqueue(frame);
        }
    }
    
    private async Task<object> HandleToolCallAsync(string name, Dictionary<string, object?> args)
    {
        _logger.LogDebug("[{SessionId}] Tool call: {Name}", SessionId, name);
        
        return name switch
        {
            "sync_booking_data" => HandleSyncBooking(args),
            "book_taxi" => await HandleBookTaxiAsync(args),
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
        
        while (_outboundQueue.TryDequeue(out _)) { }
    }
}
