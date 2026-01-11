namespace TaxiSipBridge;

public class BridgeConfig
{
    public int SipPort { get; set; } = 5060;
    public string SipUsername { get; set; } = "taxi-bridge";
    public string SipPassword { get; set; } = "";
    public string WebSocketUrl { get; set; } = "";
    public int MaxConcurrentCalls { get; set; } = 50;
    public bool EnableTls { get; set; } = false;
    public int RtpPortStart { get; set; } = 10000;
    public int RtpPortEnd { get; set; } = 20000;
}
