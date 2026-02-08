using AdaSipClient.Core;

namespace AdaSipClient.Sip;

/// <summary>
/// SIPSorcery-based SIP service. Handles registration, incoming calls,
/// RTP audio send/receive. G.711 A-law (PT8) hardcoded per project convention.
/// </summary>
public sealed class SipService : ISipService
{
    private readonly ILogSink _log;
    private bool _disposed;

    public bool IsRegistered { get; private set; }
    public bool IsInCall { get; private set; }

    public event Action<string>? OnIncomingCall;
    public event Action? OnCallEnded;
    public event Action<byte[]>? OnAudioReceived;

    public SipService(ILogSink log)
    {
        _log = log;
    }

    public async Task RegisterAsync(string server, int port, string user, string password, string transport)
    {
        // TODO: Wire SIPSorcery SIPTransport, SIPRegistrationUserAgent
        _log.Log($"[SIP] Registering {user}@{server}:{port} ({transport})...");
        await Task.Delay(100); // placeholder
        IsRegistered = true;
        _log.Log("[SIP] Registered ✓");
    }

    public void Unregister()
    {
        IsRegistered = false;
        _log.Log("[SIP] Unregistered");
    }

    public void AnswerCall()
    {
        IsInCall = true;
        _log.Log("[SIP] Call answered");
    }

    public void RejectCall()
    {
        IsInCall = false;
        _log.Log("[SIP] Call rejected");
    }

    public void HangUp()
    {
        IsInCall = false;
        OnCallEnded?.Invoke();
        _log.Log("[SIP] Hung up");
    }

    public void SendAudio(byte[] alawFrame)
    {
        // TODO: Send via RTP
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
