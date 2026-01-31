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
    
    /// <summary>
    /// Optional separate Authentication ID (for systems like 3CX where auth ID differs from extension).
    /// If empty, SipUser is used for authentication.
    /// </summary>
    public string AuthUser { get; set; } = "";

    /// <summary>
    /// Gets the effective authentication username (AuthUser if set, otherwise SipUser).
    /// </summary>
    public string EffectiveAuthUser => string.IsNullOrWhiteSpace(AuthUser) ? SipUser : AuthUser;

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
