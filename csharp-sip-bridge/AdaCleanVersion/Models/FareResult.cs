namespace AdaCleanVersion.Models;

/// <summary>
/// Result from the fare/geocoding pipeline (address-dispatch edge function).
/// Contains geocoded coordinates, fare, ETA, and zone information.
/// </summary>
public sealed class FareResult
{
    public required GeocodedAddress Pickup { get; init; }
    public required GeocodedAddress Destination { get; init; }
    public required string Fare { get; init; }
    public required string FareSpoken { get; init; }
    public required double DistanceMiles { get; init; }
    public required string DriverEta { get; init; }
    public required int DriverEtaMinutes { get; init; }
    public required string BusyLevel { get; init; }
    public required string BusyMessage { get; init; }
    public string? TripEta { get; init; }
    public int? TripEtaMinutes { get; init; }
    public string? ScheduledAt { get; init; }
    public string? ZoneName { get; init; }
    public string? CompanyId { get; init; }

    /// <summary>True if either address needs clarification.</summary>
    public bool NeedsClarification =>
        Pickup.IsAmbiguous || Destination.IsAmbiguous;

    /// <summary>Clarification message to present to caller.</summary>
    public string? ClarificationMessage { get; init; }
}

/// <summary>
/// A geocoded address from the dispatch pipeline.
/// </summary>
public sealed class GeocodedAddress
{
    public required string Address { get; init; }
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public string? StreetName { get; init; }
    public string? StreetNumber { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public bool IsAmbiguous { get; init; }
    public List<string>? Alternatives { get; init; }
    public bool MatchedFromHistory { get; init; }
}
