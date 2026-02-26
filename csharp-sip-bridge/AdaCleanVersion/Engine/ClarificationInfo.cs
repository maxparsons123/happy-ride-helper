namespace AdaCleanVersion.Engine;

/// <summary>
/// Tracks which address field needs clarification and what alternatives are available.
/// </summary>
public sealed class ClarificationInfo
{
    /// <summary>"pickup" or "destination"</summary>
    public required string AmbiguousField { get; init; }

    /// <summary>The clarification message from address-dispatch (e.g., "Which area is that in?")</summary>
    public required string Message { get; init; }

    /// <summary>Optional alternatives from the geocoding result.</summary>
    public List<string>? Alternatives { get; init; }

    /// <summary>How many times we've asked for clarification on this field in this session.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>
    /// The state the engine was in when clarification was triggered.
    /// Used to route back correctly after the caller responds.
    /// </summary>
    public CollectionState OriginState { get; init; } = CollectionState.VerifyingPickup;
}
