// Last updated: 2026-02-25 (v1.0 — Split-Brain state machine)
namespace AdaSdkModel.Core;

/// <summary>
/// Deterministic call state for the Split-Brain architecture.
/// This replaces prompt-based workflow control with backend-owned state transitions.
/// 
/// Key difference from BookingStage: CallState includes sub-states for every
/// decision point that was previously delegated to the AI model (confirmation gates,
/// payment choice, disambiguation sequencing, etc.).
/// </summary>
public enum CallState
{
    // ── NEW BOOKING FLOW ──
    
    /// <summary>Initial state. Greeting the caller, determining intent.</summary>
    Greeting,
    
    /// <summary>Collecting caller's name (if not known from history).</summary>
    CollectingName,
    
    /// <summary>Collecting pickup address. Semantic VAD, patient listening.</summary>
    CollectingPickup,
    
    /// <summary>Collecting destination address. Semantic VAD, patient listening.</summary>
    CollectingDestination,
    
    /// <summary>Collecting passenger count. Server VAD, fast response.</summary>
    CollectingPassengers,
    
    /// <summary>Collecting pickup time (ASAP or scheduled). Server VAD.</summary>
    CollectingTime,
    
    // ── FARE & CONFIRMATION ──
    
    /// <summary>Fare is being calculated (background task). Do NOT speak about fares.</summary>
    FareCalculating,
    
    /// <summary>Fare has been presented. Awaiting payment preference (card/meter).</summary>
    AwaitingPaymentChoice,
    
    /// <summary>Payment chosen. Awaiting final booking confirmation (yes/no).</summary>
    AwaitingBookingConfirmation,
    
    /// <summary>Booking confirmed. Post-booking tasks running. Ask "anything else?".</summary>
    BookingConfirmed,
    
    // ── ADDRESS DISAMBIGUATION ──
    
    /// <summary>Pickup address is ambiguous. Presenting options to caller.</summary>
    DisambiguatingPickup,
    
    /// <summary>Destination address is ambiguous. Presenting options to caller.</summary>
    DisambiguatingDestination,
    
    // ── HOUSE NUMBER / CITY GUARDS ──
    
    /// <summary>Pickup address is a street type but missing house number.</summary>
    AwaitingPickupHouseNumber,
    
    /// <summary>Destination address is a street type but missing house number.</summary>
    AwaitingDestHouseNumber,
    
    /// <summary>Destination lacks city context. Asking caller for city/area.</summary>
    AwaitingDestCity,
    
    // ── FARE SANITY ──
    
    /// <summary>Fare seems unusually high. Asking caller to re-confirm destination.</summary>
    FareSanityCheck,
    
    /// <summary>Geocoded address doesn't match spoken address. Asking caller to confirm.</summary>
    AddressDiscrepancy,
    
    // ── EXISTING BOOKING MANAGEMENT ──
    
    /// <summary>Returning caller has active booking. Offering cancel/amend/status/new.</summary>
    ManagingExistingBooking,
    
    /// <summary>Caller requested cancellation. Awaiting verbal confirmation.</summary>
    AwaitingCancelConfirmation,
    
    /// <summary>Caller wants to amend booking. Asking what to change.</summary>
    AwaitingAmendmentField,
    
    // ── SPECIAL FLOWS ──
    
    /// <summary>Airport destination detected. Bypassing fare, sending booking link.</summary>
    AirportIntercept,
    
    /// <summary>Booking confirmed, asking "anything else?" (special instructions, flight number).</summary>
    AnythingElse,
    
    /// <summary>Caller requested transfer to human operator.</summary>
    Transferring,
    
    /// <summary>Call ending / goodbye phase.</summary>
    Ending,
    
    /// <summary>Escalated to human operator (complaint, complex request).</summary>
    Escalated
}
