using System.Collections.Concurrent;
using AdaVaxVoIP.Ai;
using AdaVaxVoIP.Config;
using AdaVaxVoIP.Models;
using AdaVaxVoIP.Services;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP.Core;

/// <summary>
/// Orchestrates the taxi booking flow: VaxVoIP SIP ↔ OpenAI Realtime API.
/// Manages per-call sessions with booking state, fare calculation, and dispatch.
/// </summary>
public sealed class TaxiBookingOrchestrator : IAsyncDisposable
{
    private readonly ILogger<TaxiBookingOrchestrator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Sip.VaxVoIPSipServer _sipServer;
    private readonly OpenAISettings _openAISettings;
    private readonly FareCalculator _fareCalculator;
    private readonly Dispatcher _dispatcher;

    private readonly ConcurrentDictionary<string, CallSession> _sessions = new();

    public TaxiBookingOrchestrator(
        ILogger<TaxiBookingOrchestrator> logger,
        ILoggerFactory loggerFactory,
        Sip.VaxVoIPSipServer sipServer,
        OpenAISettings openAISettings,
        FareCalculator fareCalculator,
        Dispatcher dispatcher)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _sipServer = sipServer;
        _openAISettings = openAISettings;
        _fareCalculator = fareCalculator;
        _dispatcher = dispatcher;

        _sipServer.OnCallStarted += OnCallStarted;
        _sipServer.OnCallEnded += OnCallEnded;
        _sipServer.OnAudioReceived += OnAudioReceived;
    }

    private async void OnCallStarted(string callId, string callerId)
    {
        _logger.LogInformation("🚕 New session: {CallId} from {Caller}", callId, callerId);

        var aiClient = new OpenAIRealtimeClient(
            _loggerFactory.CreateLogger<OpenAIRealtimeClient>(),
            _openAISettings);

        var session = new CallSession
        {
            CallId = callId,
            CallerId = callerId,
            AiClient = aiClient,
            Booking = new BookingState(),
            AutoQuoteInProgress = 0
        };

        // Wire AI audio → SIP
        aiClient.OnAudio += audio => _sipServer.SendAudio(callId, audio);

        // Wire transcripts
        aiClient.OnTranscript += (speaker, text) =>
            _logger.LogInformation("[{Speaker}] {Text}", speaker, text);

        // Wire barge-in
        aiClient.OnBargeIn += () =>
            _logger.LogInformation("⚡ Barge-in on {CallId}", callId);

        // Wire tool calls
        aiClient.OnToolCall += (name, args) => HandleToolCallAsync(session, name, args);

        // Wire call ended
        aiClient.OnEnded += reason =>
        {
            _logger.LogInformation("📴 AI ended {CallId}: {Reason}", callId, reason);
            _sipServer.Hangup(callId);
        };

        // Wire auto-quote check
        aiClient.IsAutoQuoteInProgress = () => Volatile.Read(ref session.AutoQuoteInProgress) == 1;

        _sessions[callId] = session;

        try
        {
            await aiClient.ConnectAsync(callerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect AI for {CallId}", callId);
            _sipServer.Hangup(callId);
        }
    }

    private void OnCallEnded(string callId)
    {
        if (_sessions.TryRemove(callId, out var session))
        {
            _logger.LogInformation("📴 Session ended: {CallId}", callId);
            _ = session.AiClient.DisposeAsync();
        }
    }

    private void OnAudioReceived(string callId, byte[] alawData)
    {
        if (_sessions.TryGetValue(callId, out var session))
            session.AiClient.SendAudio(alawData);
    }

    // =========================
    // TOOL CALL HANDLING (full booking flow from AdaMain)
    // =========================

    private async Task<object> HandleToolCallAsync(CallSession session, string name, Dictionary<string, object?> args)
    {
        return name switch
        {
            "sync_booking_data" => await HandleSyncBookingAsync(session, args),
            "book_taxi" => await HandleBookTaxiAsync(session, args),
            "find_local_events" => HandleFindLocalEvents(args),
            "end_call" => HandleEndCall(session),
            _ => new { error = $"Unknown tool: {name}" }
        };
    }

    private async Task<object> HandleSyncBookingAsync(CallSession session, Dictionary<string, object?> args)
    {
        var b = session.Booking;
        var prevPickup = b.Pickup;
        var prevDest = b.Destination;

        if (args.TryGetValue("caller_name", out var n)) b.Name = n?.ToString();
        if (args.TryGetValue("pickup", out var p)) b.Pickup = p?.ToString();
        if (args.TryGetValue("destination", out var d)) b.Destination = d?.ToString();
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn)) b.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var pt)) b.PickupTime = pt?.ToString();

        // Reset fare if travel fields changed
        bool changed = !string.IsNullOrWhiteSpace(b.Fare) && (
            !string.Equals(prevPickup, b.Pickup, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(prevDest, b.Destination, StringComparison.OrdinalIgnoreCase));
        if (changed)
        {
            b.Fare = null; b.Eta = null;
            session.AiClient.SetAwaitingConfirmation(false);
        }

        _logger.LogInformation("[{CallId}] ⚡ Sync: Name={Name}, Pickup={Pickup}, Dest={Dest}, Pax={Pax}, Time={Time}",
            session.CallId, b.Name, b.Pickup ?? "?", b.Destination ?? "?", b.Passengers, b.PickupTime ?? "?");

        // Auto-quote when all fields filled
        bool allFilled = !string.IsNullOrWhiteSpace(b.Pickup) && !string.IsNullOrWhiteSpace(b.Destination)
            && b.Passengers > 0 && !string.IsNullOrWhiteSpace(b.PickupTime) && string.IsNullOrWhiteSpace(b.Fare);

        if (!allFilled)
            return new { success = true };

        // Fire-and-forget fare calculation
        Interlocked.Exchange(ref session.AutoQuoteInProgress, 1);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(b.Pickup, b.Destination, session.CallerId);

                if (result.NeedsClarification)
                {
                    var msg = "I found multiple possible locations. " +
                        (result.PickupAlternatives?.Length > 0 ? $"Pickup options: {string.Join(", ", result.PickupAlternatives)}. " : "") +
                        (result.DestAlternatives?.Length > 0 ? $"Destination options: {string.Join(", ", result.DestAlternatives)}. " : "") +
                        "Please ask which one they mean.";
                    await session.AiClient.InjectMessageAndRespondAsync($"[SYSTEM] {msg}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(b.Fare)) return; // Already set

                ApplyFareResult(b, result);
                session.AiClient.SetAwaitingConfirmation(true);

                var spoken = FormatFareForSpeech(b.Fare);
                var fareMsg = $"[FARE RESULT] From {b.Pickup} to {b.Destination}, fare is {spoken}, ETA {b.Eta}. " +
                    "Tell the caller and ask if they want to proceed or edit details.";
                await session.AiClient.InjectMessageAndRespondAsync(fareMsg);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-quote failed");
                b.Fare = "£8.00"; b.Eta = "8 minutes";
                await session.AiClient.InjectMessageAndRespondAsync(
                    $"[FARE RESULT] From {b.Pickup} to {b.Destination}, fare is approximately 8 pounds, ETA 8 minutes. Ask if they want to proceed.");
            }
            finally { Interlocked.Exchange(ref session.AutoQuoteInProgress, 0); }
        });

        return new { success = true, message = "Say: 'Let me get you a price on that journey' — then wait for the fare result." };
    }

    private async Task<object> HandleBookTaxiAsync(CallSession session, Dictionary<string, object?> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
        var b = session.Booking;

        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(b.Pickup) || string.IsNullOrWhiteSpace(b.Destination))
                return new { success = false, error = "Missing pickup or destination" };

            // Wait for auto-quote if in progress
            if (Volatile.Read(ref session.AutoQuoteInProgress) == 1)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref session.AutoQuoteInProgress) == 1 && sw.ElapsedMilliseconds < 12000)
                    await Task.Delay(200);
            }

            // Reuse existing fare
            if (!string.IsNullOrWhiteSpace(b.Fare))
            {
                var spoken = FormatFareForSpeech(b.Fare);
                session.AiClient.SetAwaitingConfirmation(true);
                return new
                {
                    success = true, fare = b.Fare, fare_spoken = spoken, eta = b.Eta,
                    message = $"From {b.Pickup} to {b.Destination}: {spoken}, ETA {b.Eta}. Ask to proceed or edit."
                };
            }

            try
            {
                var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(b.Pickup, b.Destination, session.CallerId);
                if (result.NeedsClarification)
                    return new { success = false, needs_clarification = true, pickup_options = result.PickupAlternatives ?? Array.Empty<string>(), destination_options = result.DestAlternatives ?? Array.Empty<string>() };

                ApplyFareResult(b, result);
                session.AiClient.SetAwaitingConfirmation(true);
                var spokenFare = FormatFareForSpeech(b.Fare);
                return new { success = true, fare = b.Fare, fare_spoken = spokenFare, eta = b.Eta, message = $"Fare: {spokenFare}, ETA: {b.Eta}. Ask to proceed." };
            }
            catch
            {
                b.Fare = "£8.00"; b.Eta = "8 minutes";
                return new { success = true, fare = b.Fare, fare_spoken = "8 pounds", eta = b.Eta };
            }
        }

        if (action == "confirmed")
        {
            // Ensure geocoded if missing
            if (string.IsNullOrWhiteSpace(b.PickupStreet) || string.IsNullOrWhiteSpace(b.Fare))
            {
                try
                {
                    var result = await _fareCalculator.ExtractAndCalculateWithAiAsync(b.Pickup, b.Destination, session.CallerId);
                    ApplyFareResult(b, result);
                }
                catch { }
            }

            b.Confirmed = true;
            b.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";

            _logger.LogInformation("[{CallId}] ✅ Booked: {Ref}", session.CallId, b.BookingRef);

            _ = _dispatcher.DispatchAsync(b, session.CallerId);
            _ = _dispatcher.SendWhatsAppAsync(session.CallerId);

            return new
            {
                success = true, booking_ref = b.BookingRef,
                message = string.IsNullOrWhiteSpace(b.Name)
                    ? "Your taxi is booked!" : $"Thanks {b.Name.Trim()}, your taxi is booked!"
            };
        }

        return new { error = "Invalid action" };
    }

    private static object HandleFindLocalEvents(Dictionary<string, object?> args)
    {
        var near = args.TryGetValue("near", out var n) ? n?.ToString() : "city centre";
        return new
        {
            success = true,
            events = new[]
            {
                new { name = "Live Music at The Empire", venue = near, date = "Tonight, 8pm" },
                new { name = "Comedy Night at The Kasbah", venue = near, date = "Saturday, 9pm" },
            },
            message = $"Found events near {near}. Would you like a taxi to any of these?"
        };
    }

    private object HandleEndCall(CallSession session)
    {
        // Drain-aware hangup
        _ = Task.Run(async () =>
        {
            var start = Environment.TickCount64;
            while (session.AiClient.IsResponseActive && Environment.TickCount64 - start < 15000)
                await Task.Delay(200);

            // Wait for playout to drain
            await Task.Delay(3000);

            var drainStart = Environment.TickCount64;
            while (Environment.TickCount64 - drainStart < 20000)
            {
                var queued = session.AiClient.GetQueuedFrames?.Invoke() ?? 0;
                if (queued == 0) break;
                await Task.Delay(100);
            }

            await Task.Delay(1000);
            _sipServer.Hangup(session.CallId);
        });

        return new { success = true };
    }

    private static void ApplyFareResult(BookingState b, FareResult r)
    {
        b.Fare = r.Fare; b.Eta = r.Eta;
        b.PickupLat = r.PickupLat; b.PickupLon = r.PickupLon;
        b.PickupStreet = r.PickupStreet; b.PickupNumber = r.PickupNumber;
        b.PickupPostalCode = r.PickupPostalCode; b.PickupCity = r.PickupCity; b.PickupFormatted = r.PickupFormatted;
        b.DestLat = r.DestLat; b.DestLon = r.DestLon;
        b.DestStreet = r.DestStreet; b.DestNumber = r.DestNumber;
        b.DestPostalCode = r.DestPostalCode; b.DestCity = r.DestCity; b.DestFormatted = r.DestFormatted;
    }

    private static string FormatFareForSpeech(string? fare)
    {
        if (string.IsNullOrEmpty(fare)) return "unknown";
        var currencyWord = fare.Contains("£") ? "pounds" : "euros";
        var clean = fare.Replace("€", "").Replace("£", "").Replace("$", "").Trim();
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            var whole = (int)amount;
            var pence = (int)((amount - whole) * 100);
            return pence > 0 ? $"{whole} {currencyWord} {pence}" : $"{whole} {currencyWord}";
        }
        return fare;
    }

    public async ValueTask DisposeAsync()
    {
        _sipServer.OnCallStarted -= OnCallStarted;
        _sipServer.OnCallEnded -= OnCallEnded;
        _sipServer.OnAudioReceived -= OnAudioReceived;
        foreach (var session in _sessions.Values)
            await session.AiClient.DisposeAsync();
        _sessions.Clear();
    }
}

public class CallSession
{
    public string CallId { get; set; } = "";
    public string CallerId { get; set; } = "";
    public OpenAIRealtimeClient AiClient { get; set; } = null!;
    public BookingState Booking { get; set; } = null!;
    public int AutoQuoteInProgress;
}
