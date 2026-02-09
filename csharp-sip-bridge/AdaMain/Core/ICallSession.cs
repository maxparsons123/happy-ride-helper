using AdaMain.Ai;

namespace AdaMain.Core;

/// <summary>
/// Represents a single call session lifecycle.
/// </summary>
public interface ICallSession : IAsyncDisposable
{
    /// <summary>Unique session identifier.</summary>
    string SessionId { get; }
    
    /// <summary>Caller phone number (E.164 or raw).</summary>
    string CallerId { get; }
    
    /// <summary>Session start time.</summary>
    DateTime StartedAt { get; }
    
    /// <summary>Whether the session is currently active.</summary>
    bool IsActive { get; }
    
    /// <summary>Expose AI client for wiring (e.g., playout queue depth).</summary>
    IOpenAiClient AiClient { get; }
    
    /// <summary>Start the AI session and begin processing.</summary>
    Task StartAsync(CancellationToken ct = default);
    
    /// <summary>Terminate the session gracefully.</summary>
    Task EndAsync(string reason);
    
    /// <summary>Feed inbound audio from SIP (G.711 encoded).</summary>
    void ProcessInboundAudio(byte[] g711Data);
    
    /// <summary>Get next outbound audio frame for SIP (G.711 encoded).</summary>
    byte[]? GetOutboundFrame();
    
    /// <summary>Notify that playout queue has drained (triggers echo guard + no-reply watchdog).</summary>
    void NotifyPlayoutComplete();
    
    /// <summary>Fired when session ends.</summary>
    event Action<ICallSession, string>? OnEnded;
    
    /// <summary>Fired when booking state changes.</summary>
    event Action<BookingState>? OnBookingUpdated;
    
    /// <summary>Fired when AI audio frame is ready for playout (160-byte A-law).</summary>
    event Action<byte[]>? OnAudioOut;
    
    /// <summary>Fired on barge-in (playout should clear its buffer).</summary>
    event Action? OnBargeIn;
}
