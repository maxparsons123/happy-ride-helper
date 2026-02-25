// Last updated: 2026-02-25 (v1.0 — Split-Brain extraction model output)
namespace AdaSdkModel.Core;

/// <summary>
/// Structured output from the extraction pass.
/// This is a pure data class — no workflow logic, no speech, no tools.
/// 
/// In Phase 1, populated from existing OpenAI Realtime tool calls (sync_booking_data args).
/// In Phase 2, can be populated from a separate lightweight extraction model call.
/// </summary>
public sealed class ExtractionResult
{
    /// <summary>Detected user intent from the transcript.</summary>
    public ExtractedIntent Intent { get; set; } = ExtractedIntent.Unknown;
    
    /// <summary>Confidence score (0-1) for the detected intent. Currently unused but reserved for Phase 2.</summary>
    public float Confidence { get; set; } = 1.0f;
    
    // ── Extracted fields (null = not mentioned in this turn) ──
    
    public string? Name { get; set; }
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? VehicleType { get; set; }
    public string? Luggage { get; set; }
    public string? SpecialInstructions { get; set; }
    public string? CallerArea { get; set; }
    public string? PaymentPreference { get; set; }
    
    /// <summary>Ada's interpretation of what the caller meant (disambiguation, clarification).</summary>
    public string? Interpretation { get; set; }
    
    /// <summary>Selected option during disambiguation (e.g. "52A David Road, Coventry").</summary>
    public string? SelectedOption { get; set; }
    
    /// <summary>Target field for disambiguation ("pickup" or "destination").</summary>
    public string? DisambiguationTarget { get; set; }
    
    /// <summary>Reason for cancellation (e.g. "caller_request", "plans_changed").</summary>
    public string? CancelReason { get; set; }
    
    /// <summary>Reason for escalation (e.g. "complaint", "complex_request").</summary>
    public string? EscalationReason { get; set; }

    /// <summary>True if any booking field (name, pickup, dest, passengers, time) was provided.</summary>
    public bool HasAnyBookingData =>
        !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Pickup) ||
        !string.IsNullOrWhiteSpace(Destination) || Passengers.HasValue ||
        !string.IsNullOrWhiteSpace(PickupTime);

    /// <summary>
    /// Create an ExtractionResult from sync_booking_data tool call arguments.
    /// This is the Phase 1 adapter — converts existing tool calls into the extraction format.
    /// </summary>
    public static ExtractionResult FromSyncArgs(Dictionary<string, object?> args)
    {
        var result = new ExtractionResult { Intent = ExtractedIntent.ProvideBookingData };
        
        if (args.TryGetValue("caller_name", out var n) && !string.IsNullOrWhiteSpace(n?.ToString()))
            result.Name = n.ToString();
        if (args.TryGetValue("pickup", out var p) && !string.IsNullOrWhiteSpace(p?.ToString()))
            result.Pickup = p.ToString();
        if (args.TryGetValue("destination", out var d) && !string.IsNullOrWhiteSpace(d?.ToString()))
            result.Destination = d.ToString();
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn))
            result.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var pt) && !string.IsNullOrWhiteSpace(pt?.ToString()))
            result.PickupTime = pt.ToString();
        if (args.TryGetValue("vehicle_type", out var vt) && !string.IsNullOrWhiteSpace(vt?.ToString()))
            result.VehicleType = vt.ToString();
        if (args.TryGetValue("luggage", out var lug) && !string.IsNullOrWhiteSpace(lug?.ToString()))
            result.Luggage = lug.ToString();
        if (args.TryGetValue("special_instructions", out var si) && !string.IsNullOrWhiteSpace(si?.ToString()))
            result.SpecialInstructions = si.ToString();
        if (args.TryGetValue("caller_area", out var ca) && !string.IsNullOrWhiteSpace(ca?.ToString()))
            result.CallerArea = ca.ToString();
        if (args.TryGetValue("interpretation", out var interp) && !string.IsNullOrWhiteSpace(interp?.ToString()))
            result.Interpretation = interp.ToString();
            
        return result;
    }
}

/// <summary>
/// Detected intents from user speech.
/// Phase 1: Mapped from existing tool calls + IntentGuard regex patterns.
/// Phase 2: Will be populated by a separate lightweight extraction model.
/// </summary>
public enum ExtractedIntent
{
    Unknown,
    
    // ── Booking data provision ──
    ProvideBookingData,     // User provided one or more booking fields
    ProvideName,
    ProvidePickup,
    ProvideDestination,
    ProvidePassengers,
    ProvideTime,
    
    // ── Confirmation / rejection ──
    Confirm,                // yes, yeah, sure, go ahead, book it
    Reject,                 // no, change, too expensive
    
    // ── Existing booking management ──
    CancelBooking,
    AmendBooking,
    CheckStatus,
    NewBooking,
    
    // ── Disambiguation ──
    SelectOption,           // User chose from disambiguation options
    
    // ── Special ──
    TransferOperator,
    Goodbye,
    AirportBooking,         // Destination is an airport
    
    // ── Payment ──
    ChooseCard,             // Fixed price / card / payment link
    ChooseMeter,            // Pay on the day / meter
}
