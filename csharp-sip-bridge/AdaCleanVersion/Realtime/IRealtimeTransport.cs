namespace AdaCleanVersion.Realtime;

/// <summary>
/// Thin abstraction over the OpenAI Realtime WebSocket transport.
/// Isolates connect/send/receive/dispose so the implementation can be
/// swapped (e.g., raw ClientWebSocket today → future .NET SDK tomorrow)
/// without touching session, audio, or mic-gate logic.
/// </summary>
public interface IRealtimeTransport : IAsyncDisposable
{
    /// <summary>True when the transport is connected and ready to send/receive.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connect to the Realtime API endpoint.
    /// </summary>
    /// <param name="url">Full WebSocket URL including model query param.</param>
    /// <param name="headers">Headers to attach (Authorization, OpenAI-Beta, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken ct = default);

    /// <summary>
    /// Send a JSON-serialized message to the server.
    /// Thread-safe — implementations must serialize concurrent sends.
    /// </summary>
    Task SendAsync(object payload, CancellationToken ct = default);

    /// <summary>
    /// Fired for every complete server event (JSON text message).
    /// Handlers must not block — offload heavy work to background tasks.
    /// </summary>
    event Func<string, Task>? OnMessage;

    /// <summary>
    /// Fired when the transport disconnects (server close or error).
    /// </summary>
    event Action<string>? OnDisconnected;
}
