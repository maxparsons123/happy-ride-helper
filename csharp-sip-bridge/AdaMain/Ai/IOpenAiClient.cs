namespace AdaMain.Ai;

/// <summary>
/// Interface for OpenAI Realtime API client.
/// Supports both PCM16 (24kHz) and native G.711 A-law (8kHz) modes.
/// </summary>
public interface IOpenAiClient
{
    /// <summary>Whether connected to OpenAI.</summary>
    bool IsConnected { get; }
    
    /// <summary>Connect and initialize session.</summary>
    Task ConnectAsync(string callerId, CancellationToken ct = default);
    
    /// <summary>Disconnect from OpenAI.</summary>
    Task DisconnectAsync();
    
    /// <summary>
    /// Send audio to OpenAI.
    /// For G.711 mode: raw A-law bytes (8kHz).
    /// For PCM mode: PCM16 bytes (24kHz).
    /// </summary>
    void SendAudio(byte[] audioData);
    
    /// <summary>Cancel current response (barge-in).</summary>
    Task CancelResponseAsync();
    
    /// <summary>Inject a system message into the conversation and trigger a response.</summary>
    Task InjectMessageAndRespondAsync(string message);
    
    /// <summary>Fired when audio received from AI (format matches input mode).</summary>
    event Action<byte[]>? OnAudio;
    
    /// <summary>Fired when tool call received.</summary>
    event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    
    /// <summary>Fired when session ends.</summary>
    event Action<string>? OnEnded;
    
    /// <summary>Fired when AI finishes speaking.</summary>
    event Action? OnPlayoutComplete;
    
    /// <summary>Fired for transcripts (role, text).</summary>
    event Action<string, string>? OnTranscript;
}
