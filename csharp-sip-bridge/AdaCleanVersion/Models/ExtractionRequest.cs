namespace AdaCleanVersion.Models;

/// <summary>
/// Payload sent to AI for the single authoritative extraction pass.
/// Contains raw slot values (refined by Ada's interpretation) and Ada's transcript context.
/// </summary>
public sealed class ExtractionRequest
{
    /// <summary>Caller context (optional, helps AI with disambiguation).</summary>
    public CallerContext? Context { get; init; }

    /// <summary>Raw slot values — refined by Ada's spoken interpretation when available.</summary>
    public required RawSlots Slots { get; init; }

    /// <summary>
    /// Ada's recent spoken responses — the authoritative interpretation of caller input.
    /// Used as additional context during extraction to resolve ambiguity.
    /// </summary>
    public string? AdaTranscriptContext { get; set; }
}

public sealed class RawSlots
{
    public required string Name { get; init; }
    public required string Pickup { get; init; }
    public required string Destination { get; init; }
    public required string Passengers { get; init; }
    public required string PickupTime { get; init; }
}

public sealed class CallerContext
{
    public string? CallerPhone { get; init; }
    public bool IsReturningCaller { get; init; }
    public string? CallerName { get; init; }
    public string? PreferredLanguage { get; init; }
    public string? ServiceArea { get; init; }
    public string? LastPickup { get; init; }
    public string? LastDestination { get; init; }
    public int TotalBookings { get; init; }
    public DateTime? LastBookingAt { get; init; }
    public Dictionary<string, string>? AddressAliases { get; init; }
}
