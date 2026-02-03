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
/// - Tool handling (sync_booking_data, book_taxi, find_local_events, end_call)
/// - BookingState tracking with geocoding
/// - WhatsApp notifications
/// - BSQD dispatch
/// </summary>
public sealed class G711CallFeatures : IDisposable
{
    private readonly OpenAIRealtimeG711Client _client;
    private readonly BookingState _booking = new();
    private readonly string _callId;
    private string? _callerPhone; // For geocoding region detection and WhatsApp
    
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
    private int _ignoreUserAudio;
    private bool _awaitingConfirmation;
    private CancellationTokenSource? _keepaliveCts;
    
    // =========================
    // EVENTS
    // =========================
    public event Action<string>? OnLog;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action? OnCallEnded;
    public event Action<string, object>? OnToolResult; // toolName, result (structured)
    
    private void Log(string msg) => OnLog?.Invoke(msg);
    
    // =========================
    // CONSTRUCTOR
    // =========================
    public G711CallFeatures(OpenAIRealtimeG711Client client, string callId, string? callerPhone = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _callId = callId;
        _callerPhone = callerPhone;
        
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
    public async Task<object> HandleToolCallAsync(string toolName, string toolCallId, JsonElement arguments)
    {
        Log($"🔧 [{_callId}] Tool: {toolName}");

        object result;
        
        switch (toolName)
        {
            case "sync_booking_data":
                result = HandleSyncBookingData(arguments);
                break;
                
            case "book_taxi":
                result = await HandleBookTaxiAsync(arguments);
                break;
                
            case "find_local_events":
                result = HandleFindLocalEvents(arguments);
                break;
                
            case "end_call":
                result = await HandleEndCallAsync(arguments);
                break;
                
            default:
                result = new { error = $"Unknown tool: {toolName}" };
                Log($"⚠️ [{_callId}] Unknown tool: {toolName}");
                break;
        }

        OnToolResult?.Invoke(toolName, result);
        return result;
    }
    
    private object HandleSyncBookingData(JsonElement args)
    {
        // Extract booking fields - map to BookingState (from BookingState.cs)
        if (args.TryGetProperty("caller_name", out var name))
            _booking.Name = name.GetString();
        if (args.TryGetProperty("pickup", out var pickup))
            _booking.Pickup = pickup.GetString();
        if (args.TryGetProperty("destination", out var dest))
            _booking.Destination = dest.GetString();
        if (args.TryGetProperty("passengers", out var pax))
        {
            try
            {
                _booking.Passengers = pax.ValueKind switch
                {
                    JsonValueKind.Number => pax.GetInt32(),
                    JsonValueKind.String => int.TryParse(pax.GetString(), out var v) ? v : _booking.Passengers,
                    _ => _booking.Passengers
                };
            }
            catch { /* ignore */ }
        }
        if (args.TryGetProperty("pickup_time", out var time))
            _booking.PickupTime = time.GetString();
        
        OnBookingUpdated?.Invoke(_booking);
        
        Log($"📋 [{_callId}] Booking synced: {_booking.Name}, {_booking.Pickup} → {_booking.Destination}, {_booking.Passengers} pax");

        return new { success = true };
    }
    
    private async Task<object> HandleBookTaxiAsync(JsonElement args)
    {
        var action = args.TryGetProperty("action", out var a) ? a.GetString() : null;

        if (action == "request_quote")
        {
            _awaitingConfirmation = true;

            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
            {
                Log($"⚠️ [{_callId}] Quote requested but pickup/destination missing (pickup='{_booking.Pickup}', dest='{_booking.Destination}')");
                return new { success = false, error = "Missing pickup or destination" };
            }
            
            // Calculate real fare using FareCalculator with geocoding
            try
            {
                var fareResult = await FareCalculator.CalculateFareWithCoordsAsync(
                    _booking.Pickup, 
                    _booking.Destination,
                    _callerPhone);
                
                // Populate geocoded address details in BookingState
                _booking.Fare = fareResult.Fare;
                _booking.Eta = fareResult.Eta;
                
                // Pickup geocoded data
                _booking.PickupLat = fareResult.PickupLat;
                _booking.PickupLon = fareResult.PickupLon;
                _booking.PickupStreet = fareResult.PickupStreet;
                _booking.PickupNumber = fareResult.PickupNumber?.ToString();
                _booking.PickupPostalCode = fareResult.PickupPostalCode;
                _booking.PickupCity = fareResult.PickupCity;
                _booking.PickupFormatted = fareResult.PickupFormatted;
                
                // Destination geocoded data
                _booking.DestLat = fareResult.DestLat;
                _booking.DestLon = fareResult.DestLon;
                _booking.DestStreet = fareResult.DestStreet;
                _booking.DestNumber = fareResult.DestNumber?.ToString();
                _booking.DestPostalCode = fareResult.DestPostalCode;
                _booking.DestCity = fareResult.DestCity;
                _booking.DestFormatted = fareResult.DestFormatted;
                
                Log($"💰 [{_callId}] Quote: {fareResult.Fare} (pickup: {fareResult.PickupCity}, dest: {fareResult.DestCity})");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{_callId}] Fare calculation failed: {ex.Message}");
                _booking.Fare = "£10.00"; // Fallback
                _booking.Eta = "5 minutes";
            }
            
            OnBookingUpdated?.Invoke(_booking);

            return new
            {
                success = true,
                fare = _booking.Fare,
                eta = _booking.Eta,
                message = $"Fare is {_booking.Fare}, driver arrives in {_booking.Eta}. Confirm the booking?"
            };
        }

        if (action != "confirmed")
        {
            Log($"⚠️ [{_callId}] book_taxi invalid action: '{action}'");
            return new { success = false, error = "Invalid action" };
        }

        // Confirmed booking
        _awaitingConfirmation = false;

        // Ensure we have geocoded components before dispatch (e.g., if user skips quote)
        if ((string.IsNullOrWhiteSpace(_booking.PickupStreet) || string.IsNullOrWhiteSpace(_booking.DestStreet)) &&
            !string.IsNullOrWhiteSpace(_booking.Pickup) &&
            !string.IsNullOrWhiteSpace(_booking.Destination))
        {
            try
            {
                var fareResult = await FareCalculator.CalculateFareWithCoordsAsync(
                    _booking.Pickup,
                    _booking.Destination,
                    _callerPhone);

                _booking.PickupLat = fareResult.PickupLat;
                _booking.PickupLon = fareResult.PickupLon;
                _booking.PickupStreet = fareResult.PickupStreet;
                _booking.PickupNumber = fareResult.PickupNumber?.ToString();
                _booking.PickupPostalCode = fareResult.PickupPostalCode;
                _booking.PickupCity = fareResult.PickupCity;
                _booking.PickupFormatted = fareResult.PickupFormatted;

                _booking.DestLat = fareResult.DestLat;
                _booking.DestLon = fareResult.DestLon;
                _booking.DestStreet = fareResult.DestStreet;
                _booking.DestNumber = fareResult.DestNumber?.ToString();
                _booking.DestPostalCode = fareResult.DestPostalCode;
                _booking.DestCity = fareResult.DestCity;
                _booking.DestFormatted = fareResult.DestFormatted;

                // Preserve existing fare if already set
                if (!string.IsNullOrWhiteSpace(fareResult.Fare)) _booking.Fare ??= fareResult.Fare;
                if (!string.IsNullOrWhiteSpace(fareResult.Eta)) _booking.Eta ??= fareResult.Eta;
            }
            catch (Exception ex)
            {
                Log($"⚠️ [{_callId}] Pre-dispatch geocode failed: {ex.Message}");
            }
        }

        _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _booking.Confirmed = true;
        
        Log($"✅ [{_callId}] Booking confirmed: {_booking.BookingRef} (caller={_callerPhone})");
        OnBookingUpdated?.Invoke(_booking);
        
        // Dispatch to BSQD API with geocoded address components
        if (!string.IsNullOrEmpty(_callerPhone))
        {
            BsqdDispatcher.OnLog += msg => Log(msg);
            BsqdDispatcher.Dispatch(_booking, _callerPhone);
        }
        
        // Send WhatsApp notification (fire-and-forget with logging)
        _ = Task.Run(async () =>
        {
            try
            {
                await SendWhatsAppNotificationAsync();
            }
            catch (Exception ex)
            {
                Log($"❌ [{_callId}] WhatsApp task error: {ex.Message}");
            }
        });
        
        // Extended grace period (15s) for "anything else?" flow, then force end
        _ = Task.Run(async () =>
        {
            await Task.Delay(15000);
            
            if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                return;
            
            // Only force timeout if no active interaction
            if (!_client.IsConnected)
                return;
            
            Log($"⏰ [{_callId}] Post-booking timeout - requesting farewell");
            
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
                            text = "[TIMEOUT] Say 'Thank you for using the Voice Taxibot system. Goodbye!' and call end_call NOW."
                        }
                    }
                }
            });
            
            await Task.Delay(20);
            await SendToOpenAIAsync(new { type = "response.create" });
        });
        
        return new
        {
            success = true,
            booking_ref = _booking.BookingRef,
            message = string.IsNullOrWhiteSpace(_booking.Name)
                ? "Your taxi is booked!"
                : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
        };
    }
    
    /// <summary>
    /// Handle find_local_events tool - returns mock events for now.
    /// </summary>
    private object HandleFindLocalEvents(JsonElement args)
    {
        var category = args.TryGetProperty("category", out var cat) ? cat.GetString() ?? "all" : "all";
        var near = args.TryGetProperty("near", out var n) ? n.GetString() : null;
        var date = args.TryGetProperty("date", out var dt) ? dt.GetString() ?? "this weekend" : "this weekend";
        
        Log($"🎭 [{_callId}] Events lookup: {category} near {near ?? "unknown"} on {date}");
        
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
    
    /// <summary>
    /// Handle end_call tool with buffer drain logic.
    /// </summary>
    private async Task<object> HandleEndCallAsync(JsonElement args)
    {
        var reason = args.TryGetProperty("reason", out var r) ? r.GetString() : "completed";
        
        Log($"📞 [{_callId}] End call requested: {reason}");
        
        // Stop accepting user audio immediately
        Interlocked.Exchange(ref _ignoreUserAudio, 1);
        
        // Wait for audio buffer to drain (max 10s) then signal end
        _ = Task.Run(async () =>
        {
            var waitStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const int MAX_WAIT_MS = 10000;
            const int CHECK_INTERVAL_MS = 100;
            
            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - waitStart < MAX_WAIT_MS)
            {
                // Check if playout queue is empty
                if (_client.PendingFrameCount == 0)
                {
                    Log($"✅ [{_callId}] Audio buffer drained, ending call");
                    break;
                }
                await Task.Delay(CHECK_INTERVAL_MS);
            }
            
            if (_client.PendingFrameCount > 0)
                Log($"⚠️ [{_callId}] Buffer still has {_client.PendingFrameCount} frames, ending anyway");
            
            // Signal call ended
            if (Interlocked.Exchange(ref _callEnded, 1) == 0)
            {
                OnCallEnded?.Invoke();
            }
        });
        
        return new { success = true };
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
    
    /// <summary>
    /// Send WhatsApp notification via WhatsAppNotifier.
    /// </summary>
    private async Task SendWhatsAppNotificationAsync()
    {
        if (string.IsNullOrWhiteSpace(_callerPhone))
        {
            Log($"⚠️ [{_callId}] WhatsApp: No caller phone number");
            return;
        }
        
        var (success, message) = await WhatsAppNotifier.SendAsync(_callerPhone);
        
        if (success)
            Log($"📱 [{_callId}] WhatsApp: {message}");
        else
            Log($"⚠️ [{_callId}] WhatsApp failed: {message}");
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
    public bool IgnoreUserAudio => Volatile.Read(ref _ignoreUserAudio) == 1;
    
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

// BookingState is now imported from BookingState.cs (TaxiSipBridge namespace)
// Removed duplicate definition that was missing geocoded fields
