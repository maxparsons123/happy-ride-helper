namespace AdaMain.Ai;

/// <summary>
/// Interface for OpenAI Realtime API client.
/// </summary>
public interface IOpenAiClient
{
    /// <summary>Whether connected to OpenAI.</summary>
    bool IsConnected { get; }
    
    /// <summary>Connect and initialize session.</summary>
    Task ConnectAsync(string callerId, CancellationToken ct = default);
    
    /// <summary>Disconnect from OpenAI.</summary>
    Task DisconnectAsync();
    
    /// <summary>Send audio to OpenAI (PCM16 @ 24kHz).</summary>
    void SendAudio(byte[] pcm24k);
    
    /// <summary>Cancel current response (barge-in).</summary>
    Task CancelResponseAsync();
    
    /// <summary>Fired when audio received from AI.</summary>
    event Action<byte[]>? OnAudio;
    
    /// <summary>Fired when tool call received.</summary>
    event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    
    /// <summary>Fired when session ends.</summary>
    event Action<string>? OnEnded;
    
    /// <summary>Fired when AI finishes speaking.</summary>
    event Action? OnPlayoutComplete;
    
    /// <summary>Fired for transcripts.</summary>
    event Action<string, string>? OnTranscript; // (role, text)
}
