namespace AdaCleanVersion.Engine;

/// <summary>
/// Deterministic states for the booking collection flow.
/// No AI involvement in state transitions — pure engine logic.
/// </summary>
public enum CollectionState
{
    /// <summary>Call just started, greeting the caller.</summary>
    Greeting,

    /// <summary>Collecting caller name.</summary>
    CollectingName,

    /// <summary>Collecting pickup address.</summary>
    CollectingPickup,

    /// <summary>Collecting destination address.</summary>
    CollectingDestination,

    /// <summary>Collecting passenger count.</summary>
    CollectingPassengers,

    /// <summary>Collecting pickup time (ASAP or scheduled).</summary>
    CollectingPickupTime,

    /// <summary>All raw slots filled — ready for AI extraction pass.</summary>
    ReadyForExtraction,

    /// <summary>AI extraction in progress.</summary>
    Extracting,

    /// <summary>Extraction complete — presenting fare / summary to caller.</summary>
    PresentingFare,

    /// <summary>Waiting for payment choice (card/meter).</summary>
    AwaitingPaymentChoice,

    /// <summary>Waiting for final booking confirmation.</summary>
    AwaitingConfirmation,

    /// <summary>Booking dispatched.</summary>
    Dispatched,

    /// <summary>Call ending gracefully.</summary>
    Ending,

    /// <summary>Call ended.</summary>
    Ended
}
