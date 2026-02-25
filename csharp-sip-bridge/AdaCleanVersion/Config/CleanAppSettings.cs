namespace AdaCleanVersion.Config;

/// <summary>
/// Configuration for the AdaCleanVersion SIP bridge.
/// </summary>
public class CleanAppSettings
{
    public SipSettings Sip { get; set; } = new();
    public VaxVoipSettings VaxVoIP { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
    public TaxiSettings Taxi { get; set; } = new();
    public string SupabaseUrl { get; set; } = "";
    public string SupabaseServiceRoleKey { get; set; } = "";
}

public class SipSettings
{
    public string Server { get; set; } = "sip.example.com";
    public int Port { get; set; } = 5060;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthUser { get; set; }
    public string? Domain { get; set; }
    public string Transport { get; set; } = "UDP";
    public bool EnableStun { get; set; } = true;
    public string? StunServer { get; set; }
    public int StunPort { get; set; } = 19302;

    public string EffectiveAuthUser => string.IsNullOrWhiteSpace(AuthUser) ? Username : AuthUser;
}

public class VaxVoipSettings
{
    public int ListenPort { get; set; } = 5060;
    public int RtpPortMin { get; set; } = 10000;
    public int RtpPortMax { get; set; } = 10100;
}

public class OpenAiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-realtime-preview";
    public string Voice { get; set; } = "alloy";
}

public class TaxiSettings
{
    public string CompanyName { get; set; } = "Ada Taxi";
}
