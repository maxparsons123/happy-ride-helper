using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace TaxiSipBridge;

/// <summary>
/// Interface for call handlers that process incoming SIP calls.
/// </summary>
public interface ICallHandler : IDisposable
{
    event Action<string>? OnLog;
    event Action<string, string>? OnCallStarted;
    event Action<string>? OnCallEnded;
    event Action<string>? OnTranscript;

    bool IsInCall { get; }

    Task HandleIncomingCallAsync(SIPTransport transport, SIPUserAgent ua, SIPRequest req, string caller);
}
