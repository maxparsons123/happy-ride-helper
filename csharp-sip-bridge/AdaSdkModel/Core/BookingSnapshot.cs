// Last updated: 2026-02-25 (v1.0 — Immutable snapshot for deterministic engine decisions)
namespace AdaSdkModel.Core;

/// <summary>
/// Immutable snapshot of current booking state, passed into HandleInput
/// so the engine can deterministically compute the next collection state
/// without sentinels or callbacks.
/// 
/// Created by CallSession from its authoritative BookingState before each engine call.
/// </summary>
public sealed class BookingSnapshot
{
    public bool HasName { get; init; }
    public bool HasPickup { get; init; }
    public bool HasDestination { get; init; }
    public bool HasPassengers { get; init; }
    public bool HasPickupTime { get; init; }
    
    /// <summary>Create from current booking state flags.</summary>
    public static BookingSnapshot From(
        string? name, string? pickup, string? destination, 
        int? passengers, string? pickupTime) => new()
    {
        HasName = !string.IsNullOrWhiteSpace(name),
        HasPickup = !string.IsNullOrWhiteSpace(pickup),
        HasDestination = !string.IsNullOrWhiteSpace(destination),
        HasPassengers = passengers.HasValue && passengers > 0,
        HasPickupTime = !string.IsNullOrWhiteSpace(pickupTime),
    };

    /// <summary>True if all required fields are present for fare calculation.</summary>
    public bool IsComplete => HasName && HasPickup && HasDestination && HasPassengers && HasPickupTime;

    /// <summary>
    /// Return a new snapshot reflecting the extraction that was just applied.
    /// This lets the engine see the "after merge" state without mutating anything.
    /// </summary>
    public BookingSnapshot WithMerged(ExtractionResult extraction) => new()
    {
        HasName = HasName || !string.IsNullOrWhiteSpace(extraction.Name),
        HasPickup = HasPickup || !string.IsNullOrWhiteSpace(extraction.Pickup),
        HasDestination = HasDestination || !string.IsNullOrWhiteSpace(extraction.Destination),
        HasPassengers = HasPassengers || (extraction.Passengers.HasValue && extraction.Passengers > 0),
        HasPickupTime = HasPickupTime || !string.IsNullOrWhiteSpace(extraction.PickupTime),
    };
}
