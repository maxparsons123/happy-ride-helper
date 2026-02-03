using SIPSorcery.SIP;

namespace AdaMain.Sip;

/// <summary>
/// SIP configuration utilities.
/// </summary>
public static class SipConfigHelper
{
    public static SIPProtocolsEnum ParseTransport(string transport) => transport.ToUpperInvariant() switch
    {
        "TCP" => SIPProtocolsEnum.tcp,
        "TLS" => SIPProtocolsEnum.tls,
        "WS" => SIPProtocolsEnum.ws,
        "WSS" => SIPProtocolsEnum.wss,
        _ => SIPProtocolsEnum.udp
    };
}
