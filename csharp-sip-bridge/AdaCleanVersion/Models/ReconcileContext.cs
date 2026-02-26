namespace AdaCleanVersion.Models;

/// <summary>
/// Full 3-way context for address reconciliation.
/// Gives Gemini a "transcript window" containing:
///   1. Ada's question (intent — what kind of address was asked for)
///   2. User's raw STT speech (noisy but authentic)
///   3. Ada's readback (AI-cleaned interpretation)
/// This triple-signal approach resolves complex POIs and precise house numbers simultaneously.
/// </summary>
public sealed class ReconcileContextRequest
{
    /// <summary>Ada's question that prompted this address (e.g. "Where are you heading to today?")</summary>
    public string? AdaQuestion { get; set; }

    /// <summary>Raw STT transcript from the caller (e.g. "the flying standard pub please")</summary>
    public required string UserRawSpeech { get; set; }

    /// <summary>Ada's spoken readback of the address (e.g. "Okay, The Flying Standard.")</summary>
    public string? AdaReadback { get; set; }

    /// <summary>Detected or inferred city for locality bias (e.g. "Coventry")</summary>
    public string? CurrentCity { get; set; }

    /// <summary>Context label — "pickup" or "destination"</summary>
    public required string Context { get; set; }

    /// <summary>Caller phone number for history lookup and area code bias</summary>
    public string? CallerPhone { get; set; }
}

/// <summary>
/// Result from the reconciliation + geocoding pipeline.
/// Contains the resolved address, coordinates, and confidence score.
/// </summary>
public sealed class ReconciledLocationResult
{
    /// <summary>The reconciled, verified address string.</summary>
    public required string ReconciledAddress { get; set; }

    /// <summary>Geocoded latitude.</summary>
    public double Lat { get; set; }

    /// <summary>Geocoded longitude.</summary>
    public double Lon { get; set; }

    /// <summary>Confidence score 0.0–1.0 from the AI reconciliation.</summary>
    public double Confidence { get; set; }

    /// <summary>Whether the resolved address is a Point of Interest (pub, station, etc.).</summary>
    public bool IsPOI { get; set; }

    /// <summary>Structured address components.</summary>
    public AddressComponents Components { get; set; } = new();

    /// <summary>Whether the address is ambiguous and needs clarification.</summary>
    public bool IsAmbiguous { get; set; }

    /// <summary>Alternative addresses if ambiguous.</summary>
    public List<string>? Alternatives { get; set; }

    /// <summary>Clarification message to ask the caller.</summary>
    public string? ClarificationMessage { get; set; }

    /// <summary>Convert to GeocodedAddress for the existing pipeline.</summary>
    public GeocodedAddress ToGeocodedAddress() => new()
    {
        Address = ReconciledAddress,
        Lat = Lat,
        Lon = Lon,
        StreetName = Components.StreetName,
        StreetNumber = Components.StreetNumber,
        PostalCode = Components.PostalCode,
        City = Components.City,
        IsAmbiguous = IsAmbiguous,
        Alternatives = Alternatives,
        MatchedFromHistory = false
    };
}

/// <summary>
/// Structured address components extracted during reconciliation.
/// </summary>
public sealed class AddressComponents
{
    public string? Name { get; set; }         // POI name (e.g. "The Flying Standard")
    public string? StreetName { get; set; }
    public string? StreetNumber { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Area { get; set; }         // District/area (e.g. "Earlsdon")
}
