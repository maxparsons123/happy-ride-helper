namespace AdaCleanVersion.Conversation;

public enum TurnRelationship
{
    DirectAnswer,
    Correction,
    ConfirmationYes,
    ConfirmationNo,
    NewRequest,
    Irrelevant,
    Unclear
}

public enum ExpectedResponse
{
    None,
    Pickup,
    Destination,
    Passengers,
    PickupTime,
    ConfirmationYesNo
}

public sealed record TurnAnalysisResult(
    TurnRelationship Relationship,
    string? Slot,
    string? Value,
    double Confidence,
    string RawModelOutput
);
