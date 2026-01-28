using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace TaxiSipBridge;

/// <summary>
/// Interface for call handlers that process incoming SIP calls.
/// Requires SIPTransport to be passed explicitly because SIPUserAgent
/// does not expose its transport publicly, which is needed for sending
/// SIP responses like 180 Ringing or 486 Busy Here.
/// </summary>
public interface ISipCallHandler : IDisposable
{
    event Action<string>? OnLog;
    event Action<string, string>? OnCallStarted;
    event Action<string>? OnCallEnded;
    event Action<string>? OnTranscript;

    bool IsInCall { get; }

    /// <summary>
    /// Handle an incoming SIP call.
    /// </summary>
    /// <param name="transport">SIP transport for sending responses</param>
    /// <param name="ua">SIP user agent managing the call</param>
    /// <param name="req">The incoming INVITE request</param>
    /// <param name="caller">Caller ID string</param>
    Task HandleIncomingCallAsync(SIPTransport transport, SIPUserAgent ua, SIPRequest req, string caller);
}
