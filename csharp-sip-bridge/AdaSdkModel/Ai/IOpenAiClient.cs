// Last updated: 2026-02-21 (v2.8)
namespace AdaSdkModel.Ai;

/// <summary>
/// Interface for OpenAI Realtime API client.
/// Supports both G.711 A-law (8kHz) and PCM16 (24kHz) modes.
/// </summary>
public interface IOpenAiClient
{
    bool IsConnected { get; }
    bool IsResponseActive { get; }
    Task ConnectAsync(string callerId, CancellationToken ct = default);
    Task DisconnectAsync();
    void SendAudio(byte[] audioData);
    Task CancelResponseAsync();
    Task InjectMessageAndRespondAsync(string message);
    Task InjectSystemMessageAsync(string message);
    Task SendGreetingAsync(string? callerName = null);
    Task SendGreetingWithBookingAsync(string? callerName, AdaSdkModel.Core.BookingState booking);
    Task SetVadModeAsync(bool useSemantic, float eagerness = 0.5f);

    /// <summary>Notify that playout has finished (for echo guard timing).</summary>
    void NotifyPlayoutComplete();

    /// <summary>Set whether the system is awaiting a yes/no confirmation.</summary>
    void SetAwaitingConfirmation(bool awaiting);

    /// <summary>Cancel any pending deferred response.</summary>
    void CancelDeferredResponse();

    /// <summary>Last Whisper STT transcript for mismatch comparison.</summary>
    string? LastUserTranscript { get; }

    /// <summary>Optional: query playout queue depth for drain-aware shutdown.</summary>
    Func<int>? GetQueuedFrames { get; set; }

    /// <summary>Stage-aware context provider for no-reply watchdog re-prompts.</summary>
    Func<string?>? NoReplyContextProvider { get; set; }

    event Action<byte[]>? OnAudio;
    event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    event Action<string>? OnEnded;
    event Action? OnPlayoutComplete;
    event Action<string, string>? OnTranscript;
    event Action? OnBargeIn;
    event Action? OnResponseCompleted;

    /// <summary>Fired when Ada says goodbye but book_taxi was never called — safety net for auto-dispatch.</summary>
    event Action? OnGoodbyeWithoutBooking;
}
