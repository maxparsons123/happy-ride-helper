namespace AdaCleanVersion.Realtime;

/// <summary>
/// Typed events from the OpenAI Realtime API.
/// Parsed from raw JSON by RealtimeEventParser.
/// </summary>
public enum RealtimeEventType
{
    AudioDelta,
    ResponseCreated,
    AudioStarted,
    AudioDone,
    ToolCallDone,
    CallerTranscript,
    AdaTranscriptDone,
    SpeechStarted,
    SpeechStopped,
    ResponseCanceled,
    SessionCreated,
    SessionUpdated,
    Error,
    Unknown
}

public sealed record RealtimeEvent
{
    public RealtimeEventType Type { get; init; }
    public string? AudioBase64 { get; init; }
    public string? Transcript { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolArgsJson { get; init; }
    public string? ErrorMessage { get; init; }
}
