using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Add-on features for OpenAIRealtimeG711Client.
/// Keeps the base client simple while adding:
/// - Keepalive loop
/// - No-reply watchdog (triggered from playout completion)
/// - Tool handling (sync_booking_data, book_taxi, end_call)
/// - BookingState tracking
/// </summary>
public sealed class G711CallFeatures : IDisposable
{
    private readonly OpenAIRealtimeG711Client _client;
    private readonly BookingState _booking = new();
    private readonly string _callId;
    
    // =========================
    // WATCHDOG STATE
    // =========================
    private int _noReplyWatchdogId;
    private int _noReplyCount;
    private const int MAX_NO_REPLY_PROMPTS = 3;
    private const int NO_REPLY_TIMEOUT_MS = 15000;
    private const int CONFIRMATION_TIMEOUT_MS = 20000;
    
    // =========================
    // CALL STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private bool _awaitingConfirmation;
    private CancellationTokenSource? _keepaliveCts;
    
    // =========================
    // EVENTS
    // =========================
    public event Action<string>? OnLog;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action? OnCallEnded;
    public event Action<string, string>? OnToolResult; // toolName, result
    
    private void Log(string msg) => OnLog?.Invoke(msg);
    
    // =========================
    // CONSTRUCTOR
    // =========================
    public G711CallFeatures(OpenAIRealtimeG711Client client, string callId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _callId = callId;
        
        // Wire up to base client events
        _client.OnLog += msg => OnLog?.Invoke(msg);
        _client.OnResponseCompleted += HandleResponseCompleted;
    }
    
    /// <summary>
    /// Start the keepalive loop. Call after ConnectAsync.
    /// </summary>
    public void StartKeepalive()
    {
        _keepaliveCts = new CancellationTokenSource();
        _ = Task.Run(() => KeepaliveLoopAsync(_keepaliveCts.Token));
    }
    
    // =========================
    // KEEPALIVE LOOP
    // =========================
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        const int INTERVAL_MS = 20000;
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(INTERVAL_MS, ct);
                
                if (!_client.IsConnected)
                {
                    Log($"⚠️ [{_callId}] Keepalive: WebSocket disconnected");
                    break;
                }
                
                Log($"💓 [{_callId}] Keepalive: connected");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"⚠️ [{_callId}] Keepalive error: {ex.Message}");
        }
    }
    
    // =========================
    // NO-REPLY WATCHDOG
    // =========================
    /// <summary>
    /// Called when RTP playout queue becomes empty.
    /// This is the ONLY place to start the no-reply watchdog.
    /// </summary>
    public void OnPlayoutComplete()
    {
        // First, notify the base client
        _client.NotifyPlayoutComplete();
        
        // Then start our watchdog
        StartNoReplyWatchdog();
    }
    
    private void StartNoReplyWatchdog()
    {
        var timeoutMs = _awaitingConfirmation ? CONFIRMATION_TIMEOUT_MS : NO_REPLY_TIMEOUT_MS;
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
        
        _ = Task.Run(async () =>
        {
            await Task.Delay(timeoutMs);
            
            // Check if this watchdog is still valid
            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _callEnded) != 0) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_client.IsConnected) return;
            
            var count = Interlocked.Increment(ref _noReplyCount);
            
            if (count >= MAX_NO_REPLY_PROMPTS)
            {
                Log($"⏰ [{_callId}] Max no-reply prompts reached - ending call");
                await RequestEndCallAsync("No response from caller");
                return;
            }
            
            Log($"⏰ [{_callId}] No-reply watchdog triggered ({count}/{MAX_NO_REPLY_PROMPTS})");
            await SendNoReplyPromptAsync();
        });
    }
    
    private async Task SendNoReplyPromptAsync()
    {
        // Inject a system message to prompt the user
        await SendToOpenAIAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "system",
                content = new[]
                {
                    new
                    {
                        type = "input_text",
                        text = "[SILENCE DETECTED] The user has not responded. Say something brief like 'Are you still there?' or 'Hello?' - do NOT repeat your previous question."
                    }
                }
            }
        });
        
        await Task.Delay(20);
        await SendToOpenAIAsync(new { type = "response.create" });
        Log($"🔄 [{_callId}] No-reply prompt sent");
    }
    
    /// <summary>
    /// Cancel the current watchdog (e.g., when user speaks).
    /// </summary>
    public void CancelWatchdog()
    {
        Interlocked.Increment(ref _noReplyWatchdogId);
        Interlocked.Exchange(ref _noReplyCount, 0);
    }
    
    // =========================
    // TOOL HANDLING
    // =========================
    /// <summary>
    /// Process a tool call from OpenAI. Returns the result to send back.
    /// </summary>
    public async Task<string> HandleToolCallAsync(string toolName, string toolCallId, JsonElement arguments)
    {
        Log($"🔧 [{_callId}] Tool: {toolName}");
        
        string result;
        
        switch (toolName)
        {
            case "sync_booking_data":
                result = HandleSyncBookingData(arguments);
                break;
                
            case "book_taxi":
                result = await HandleBookTaxiAsync(arguments);
                break;
                
            case "end_call":
                result = HandleEndCall(arguments);
                break;
                
            default:
                result = $"Unknown tool: {toolName}";
                Log($"⚠️ [{_callId}] Unknown tool: {toolName}");
                break;
        }
        
        OnToolResult?.Invoke(toolName, result);
        return result;
    }
    
    private string HandleSyncBookingData(JsonElement args)
    {
        // Extract booking fields
        if (args.TryGetProperty("caller_name", out var name))
            _booking.CallerName = name.GetString();
        if (args.TryGetProperty("pickup", out var pickup))
            _booking.Pickup = pickup.GetString();
        if (args.TryGetProperty("destination", out var dest))
            _booking.Destination = dest.GetString();
        if (args.TryGetProperty("passengers", out var pax))
            _booking.Passengers = pax.GetInt32();
        if (args.TryGetProperty("pickup_time", out var time))
            _booking.PickupTime = time.GetString();
        
        OnBookingUpdated?.Invoke(_booking);
        
        Log($"📋 [{_callId}] Booking synced: {_booking.CallerName}, {_booking.Pickup} → {_booking.Destination}, {_booking.Passengers} pax");
        
        return "Booking data synchronized";
    }
    
    private async Task<string> HandleBookTaxiAsync(JsonElement args)
    {
        var confirmed = args.TryGetProperty("confirmed", out var c) && c.GetBoolean();
        
        if (!confirmed)
        {
            _awaitingConfirmation = true;
            
            // Generate a quote (mock for now)
            var quote = CalculateMockQuote();
            _booking.Quote = quote;
            
            Log($"💰 [{_callId}] Quote: {quote}");
            OnBookingUpdated?.Invoke(_booking);
            
            return $"Quote generated: {quote}. Ask user to confirm.";
        }
        
        // Confirmed booking
        _awaitingConfirmation = false;
        _booking.BookingRef = $"VT{DateTime.Now:yyMMddHHmmss}";
        _booking.Status = "confirmed";
        
        Log($"✅ [{_callId}] Booking confirmed: {_booking.BookingRef}");
        OnBookingUpdated?.Invoke(_booking);
        
        // Schedule call end after goodbye
        _ = Task.Run(async () =>
        {
            await Task.Delay(15000); // Wait for goodbye
            if (Volatile.Read(ref _callEnded) == 0)
            {
                Log($"📞 [{_callId}] Post-booking timeout - ending call");
                OnCallEnded?.Invoke();
            }
        });
        
        return $"Booking confirmed. Reference: {_booking.BookingRef}. Thank the customer and end the call.";
    }
    
    private string HandleEndCall(JsonElement args)
    {
        var reason = args.TryGetProperty("reason", out var r) ? r.GetString() : "completed";
        
        Log($"📞 [{_callId}] End call requested: {reason}");
        
        // Fire call ended after grace period
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // 5s grace for goodbye audio
            if (Interlocked.Exchange(ref _callEnded, 1) == 0)
            {
                OnCallEnded?.Invoke();
            }
        });
        
        return "Call will end after goodbye message";
    }
    
    private async Task RequestEndCallAsync(string reason)
    {
        await SendToOpenAIAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "system",
                content = new[]
                {
                    new
                    {
                        type = "input_text",
                        text = $"[SYSTEM] End the call now. Reason: {reason}. Say a brief goodbye."
                    }
                }
            }
        });
        
        await Task.Delay(20);
        await SendToOpenAIAsync(new { type = "response.create" });
    }
    
    private string CalculateMockQuote()
    {
        // Simple mock quote based on destination
        var random = new Random();
        var base_fare = 5.0 + random.NextDouble() * 20;
        var pax_surcharge = (_booking.Passengers ?? 1) > 4 ? 5.0 : 0;
        var total = base_fare + pax_surcharge;
        return $"£{total:F2}";
    }
    
    // =========================
    // RESPONSE HANDLING
    // =========================
    private void HandleResponseCompleted()
    {
        // Don't restart watchdog here - wait for playout to complete
        // The watchdog is started from OnPlayoutComplete()
    }
    
    // =========================
    // HELPERS
    // =========================
    private async Task SendToOpenAIAsync(object payload)
    {
        // Use reflection to call the private SendJsonAsync method
        // In production, you'd expose this properly
        var method = _client.GetType().GetMethod("SendJsonAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (method != null)
        {
            var task = method.Invoke(_client, new[] { payload }) as Task;
            if (task != null) await task;
        }
    }
    
    // =========================
    // PROPERTIES
    // =========================
    public BookingState Booking => _booking;
    public bool AwaitingConfirmation => _awaitingConfirmation;
    
    // =========================
    // DISPOSE
    // =========================
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        
        _keepaliveCts?.Cancel();
        _keepaliveCts?.Dispose();
        
        // Unwire events
        _client.OnResponseCompleted -= HandleResponseCompleted;
    }
}

/// <summary>
/// Simple booking state container.
/// </summary>
public class BookingState
{
    public string? CallerName { get; set; }
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? Quote { get; set; }
    public string? BookingRef { get; set; }
    public string? Status { get; set; }
    
    public bool IsComplete => 
        !string.IsNullOrEmpty(Pickup) && 
        !string.IsNullOrEmpty(Destination) && 
        Passengers.HasValue;
}
