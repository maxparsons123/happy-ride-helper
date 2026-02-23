using ZaffAdaSystem.Ai;

namespace ZaffAdaSystem.Core;

/// <summary>
/// Represents a single call session lifecycle.
/// </summary>
public interface ICallSession : IAsyncDisposable
{
    string SessionId { get; }
    string CallerId { get; }
    DateTime StartedAt { get; }
    bool IsActive { get; }
    IOpenAiClient AiClient { get; }

    Task StartAsync(CancellationToken ct = default);
    Task EndAsync(string reason);
    void ProcessInboundAudio(byte[] g711Data);
    byte[]? GetOutboundFrame();
    void NotifyPlayoutComplete();

    event Action<ICallSession, string>? OnEnded;
    event Action<BookingState>? OnBookingUpdated;
    event Action<byte[]>? OnAudioOut;
    event Action? OnBargeIn;
}
