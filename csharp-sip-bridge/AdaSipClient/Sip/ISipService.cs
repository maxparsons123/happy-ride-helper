namespace AdaSipClient.Sip;

/// <summary>
/// SIP registration and call lifecycle abstraction.
/// </summary>
public interface ISipService : IDisposable
{
    bool IsRegistered { get; }
    bool IsInCall { get; }

    event Action<string>? OnIncomingCall;   // caller number
    event Action? OnCallEnded;
    event Action<byte[]>? OnAudioReceived;  // 8kHz A-law frames from caller

    Task RegisterAsync(string server, int port, string user, string password, string transport);
    void Unregister();
    void AnswerCall();
    void RejectCall();
    void HangUp();
    void SendAudio(byte[] alawFrame);       // 8kHz A-law to caller
}
