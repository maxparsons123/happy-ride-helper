// Last updated: 2026-03-02 (v1.0 — Deterministic Session Driver)
namespace AdaSdkModel.Core.Deterministic;

/// <summary>
/// Simplified stage enum for the deterministic driver.
/// Unlike CallState (30+ states), this has ~12 core stages.
/// Sub-states like VerifyingPickup/ValidatingPickup are handled
/// internally by the driver without being separate enum values.
/// </summary>
public enum DeterministicStage
{
    /// <summary>Initial greeting. Determine intent (new/existing booking).</summary>
    Greeting,

    /// <summary>Collecting caller name.</summary>
    CollectingName,

    /// <summary>Collecting pickup address (semantic VAD, patient).</summary>
    CollectingPickup,

    /// <summary>Reading back pickup for caller verification.</summary>
    VerifyingPickup,

    /// <summary>Pickup address being geocoded — Ada says filler, waits for result.</summary>
    ValidatingPickup,

    /// <summary>Pickup is a POI/business — asking caller whereabouts they'll be waiting.</summary>
    CollectingPickupLocation,

    /// <summary>Collecting destination address (semantic VAD, patient).</summary>
    CollectingDestination,

    /// <summary>Reading back destination for caller verification.</summary>
    VerifyingDestination,

    /// <summary>Destination address being geocoded — Ada says filler, waits for result.</summary>
    ValidatingDestination,

    /// <summary>Collecting passenger count (server VAD, fast).</summary>
    CollectingPassengers,

    /// <summary>Collecting pickup time (server VAD, fast).</summary>
    CollectingTime,

    /// <summary>Collecting optional driver notes (e.g. "I'm outside", "wheelchair access", "knock front door").</summary>
    CollectingNotes,

    /// <summary>Reading back the driver note for caller verification.</summary>
    VerifyingNotes,

    /// <summary>Fare being calculated. Model is SILENT.</summary>
    FareCalculating,

    /// <summary>Fare presented, awaiting caller approval to proceed.</summary>
    AwaitingConfirmation,

    /// <summary>Booking dispatched. Informing caller about WhatsApp payment link.</summary>
    PostBooking,

    /// <summary>Managing an existing active booking (cancel/amend/status).</summary>
    ManagingExisting,

    /// <summary>Awaiting cancel confirmation.</summary>
    AwaitingCancelConfirm,

    /// <summary>Disambiguation — caller choosing from options.</summary>
    Disambiguating,

    /// <summary>Call ending / goodbye.</summary>
    Ending,

    /// <summary>Escalated to human operator.</summary>
    Escalated
}
