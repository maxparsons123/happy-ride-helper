namespace TaxiSipBridge;

public enum SipTransportType 
{ 
    UDP, 
    TCP 
}

public class SipAdaBridgeConfig
{
    public string SipServer { get; set; } = "206.189.123.28";
    public int SipPort { get; set; } = 5060;
    public string SipUser { get; set; } = "max201";
    public string SipPassword { get; set; } = "qwe70954504118";
    public string AdaWsUrl { get; set; } = "wss://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-realtime-paired";
    public SipTransportType Transport { get; set; } = SipTransportType.UDP;

    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(SipServer))
        {
            error = "SIP server is required";
            return false;
        }
        
        if (SipPort <= 0 || SipPort > 65535)
        {
            error = "Invalid SIP port";
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(SipUser))
        {
            error = "SIP username is required";
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(AdaWsUrl))
        {
            error = "Ada WebSocket URL is required";
            return false;
        }
        
        if (!AdaWsUrl.StartsWith("wss://") && !AdaWsUrl.StartsWith("ws://"))
        {
            error = "Ada WebSocket URL must start with ws:// or wss://";
            return false;
        }
        
        error = string.Empty;
        return true;
    }
}
