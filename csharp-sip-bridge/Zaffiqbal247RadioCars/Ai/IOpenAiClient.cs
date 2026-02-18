namespace Zaffiqbal247RadioCars.Ai;

/// <summary>
/// Interface for OpenAI Realtime API client.
/// Supports native G.711 A-law (8kHz) passthrough mode.
/// </summary>
public interface IOpenAiClient
{
    bool IsConnected { get; }
    Task ConnectAsync(string callerId, CancellationToken ct = default);
    Task DisconnectAsync();
    void SendAudio(byte[] audioData);
    Task CancelResponseAsync();
    Task InjectMessageAndRespondAsync(string message);
    Task InjectSystemMessageAsync(string message);
    Task SendGreetingAsync(string? callerName = null);
    Task SetVadModeAsync(bool useSemantic, float eagerness = 0.5f);

    event Action<byte[]>? OnAudio;
    event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    event Action<string>? OnEnded;
    event Action? OnPlayoutComplete;
    event Action<string, string>? OnTranscript;
}
