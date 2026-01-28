namespace TaxiSipBridge;

/// <summary>
/// Configuration for SIP login.
/// </summary>
public class SipLoginConfig
{
    public string SipServer { get; set; } = "";
    public int SipPort { get; set; } = 5060;
    public string SipUser { get; set; } = "";
    public string SipPassword { get; set; } = "";
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

        error = string.Empty;
        return true;
    }
}
