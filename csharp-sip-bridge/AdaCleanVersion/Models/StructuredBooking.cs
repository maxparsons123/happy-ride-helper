namespace AdaCleanVersion.Models;

/// <summary>
/// Authoritative structured booking — produced by the single AI extraction pass
/// from RawBookingData. This is immutable once created.
/// </summary>
public sealed class StructuredBooking
{
    public required string CallerName { get; init; }
    public required StructuredAddress Pickup { get; init; }
    public required StructuredAddress Destination { get; init; }
    public required int Passengers { get; init; }
    public required string PickupTime { get; init; }

    /// <summary>ISO pickup time if parsed, null if "ASAP".</summary>
    public DateTime? PickupDateTime { get; init; }
    public bool IsAsap => PickupDateTime == null;
}

/// <summary>
/// Normalized address from AI extraction.
/// </summary>
public sealed class StructuredAddress
{
    public string? HouseNumber { get; init; }
    public string? StreetName { get; init; }
    public string? Area { get; init; }
    public string? City { get; init; }
    public string? Postcode { get; init; }

    /// <summary>Full display string for readback.</summary>
    public string DisplayName =>
        string.Join(", ", new[] { HouseNumber, StreetName, Area, City, Postcode }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}
