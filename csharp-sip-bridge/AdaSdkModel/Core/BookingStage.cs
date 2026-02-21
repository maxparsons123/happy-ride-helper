namespace AdaSdkModel.Core;

/// <summary>
/// Tracks the current stage of the booking conversation.
/// Used by IntentGuard to deterministically resolve user intent
/// based on stage + transcript keywords.
/// </summary>
public enum BookingStage
{
    /// <summary>Initial greeting, collecting caller name.</summary>
    Greeting,

    /// <summary>Collecting pickup address.</summary>
    CollectingPickup,

    /// <summary>Collecting destination address.</summary>
    CollectingDestination,

    /// <summary>Collecting passenger count.</summary>
    CollectingPassengers,

    /// <summary>Collecting pickup time.</summary>
    CollectingTime,

    /// <summary>Fare is being calculated (background task).</summary>
    FareCalculating,

    /// <summary>Fare has been presented, awaiting user confirmation.</summary>
    FarePresented,

    /// <summary>Address disambiguation — user choosing from options.</summary>
    Disambiguation,

    /// <summary>Booking confirmed, asking "anything else?".</summary>
    AnythingElse,

    /// <summary>Returning caller has an active booking — deciding cancel/amend/status.</summary>
    ManagingExistingBooking,

    /// <summary>Call ending / goodbye phase.</summary>
    Ending
}
