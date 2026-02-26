namespace AdaCleanVersion.Config;

/// <summary>
/// Strongly-typed configuration for AdaCleanVersion.
/// Mirrors AdaSdkModel's AppSettings structure for UI parity.
/// </summary>
public class CleanAppSettings
{
    public SipSettings Sip { get; set; } = new();

    /// <summary>Named SIP accounts for quick switching.</summary>
    public List<SipAccount> SipAccounts { get; set; } = new();

    /// <summary>Index of the currently selected SIP account (-1 = use inline Sip settings).</summary>
    public int SelectedSipAccountIndex { get; set; } = -1;

    public RtpSettings Rtp { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public TaxiSettings Taxi { get; set; } = new();
    public DispatchSettings Dispatch { get; set; } = new();
    public SupabaseSettings Supabase { get; set; } = new();
    public SimliSettings Simli { get; set; } = new();
    public GoogleMapsSettings GoogleMaps { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
    public IcabbiSettings Icabbi { get; set; } = new();
    public ZoneGuardSettings ZoneGuard { get; set; } = new();
    public SumUpSettings SumUp { get; set; } = new();

    // Legacy compat
    public string SupabaseUrl { get => Supabase.Url; set => Supabase.Url = value; }
    public string SupabaseServiceRoleKey { get => Supabase.ServiceRoleKey; set => Supabase.ServiceRoleKey = value; }
}

public class SipSettings
{
    public string Server { get; set; } = "sip.example.com";
    public int Port { get; set; } = 5060;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthUser { get; set; }
    public string? Domain { get; set; }
    public string? DisplayName { get; set; }
    public string Transport { get; set; } = "UDP";
    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string? StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;
    public int ListenPort { get; set; } = 5060;
    public string? OperatorTransferExtension { get; set; }

    public string EffectiveAuthUser => string.IsNullOrWhiteSpace(AuthUser) ? Username : AuthUser;
}

public class RtpSettings
{
    public int PortRangeStart { get; set; } = 10000;
    public int PortRangeEnd { get; set; } = 10100;

    /// <summary>Consecutive RTP send failures before circuit breaker trips and ends the call.</summary>
    public int CircuitBreakerThreshold { get; set; } = 10;

    /// <summary>Maximum Simli reconnect attempts before giving up. 0 = unlimited.</summary>
    public int MaxSimliReconnectAttempts { get; set; } = 5;
}

public class OpenAiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-realtime-preview";
    public string Voice { get; set; } = "alloy";
    public bool UseHighSample { get; set; } = false;
    public double IngressGain { get; set; } = 4.0;
    public double EgressGain { get; set; } = 1.0;
}

public class AudioSettings
{
    public string PreferredCodec { get; set; } = "PCMA";
    public double VolumeBoost { get; set; } = 1.0;
    public double IngressVolumeBoost { get; set; } = 4.0;
    public int EchoGuardMs { get; set; } = 200;
    public float BargeInRmsThreshold { get; set; } = 1500f;
    public bool EnableDiagnostics { get; set; } = true;
    public float ThinningAlpha { get; set; } = 0.0f;
}

public class TaxiSettings
{
    public string CompanyName { get; set; } = "Ada Taxi";
}

public class DispatchSettings
{
    public string BsqdWebhookUrl { get; set; } = "";
    public string BsqdApiKey { get; set; } = "";
    public string WhatsAppWebhookUrl { get; set; } = "";
    public string MqttBrokerUrl { get; set; } = "wss://broker.hivemq.com:8884/mqtt";
}

public class SupabaseSettings
{
    public string Url { get; set; } = "https://oerketnvlmptpfvttysy.supabase.co";
    public string AnonKey { get; set; } = "";
    public string ServiceRoleKey { get; set; } = "";
}

public class SimliSettings
{
    public string ApiKey { get; set; } = "";
    public string FaceId { get; set; } = "";
    public bool Enabled { get; set; } = false;
}

public class GoogleMapsSettings
{
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Settings for direct Google Gemini API calls (address geocoding/fare).
/// </summary>
public class GeminiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.0-flash";
    public bool Enabled { get; set; } = false;
}

public class IcabbiSettings
{
    public bool Enabled { get; set; } = false;
    public string AppKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string TenantBase { get; set; } = "https://yourtenant.icabbi.net";
    public int SiteId { get; set; } = 0;
    public string CompanyId { get; set; } = "";
}

/// <summary>
/// Controls the Zone Guard — pickup address validation against zone POIs.
/// </summary>
public class ZoneGuardSettings
{
    public bool Enabled { get; set; } = false;
    public float MinSimilarity { get; set; } = 0.25f;
    public bool BlockOnNoMatch { get; set; } = false;
    public int MaxCandidates { get; set; } = 10;
    public string CompanyId { get; set; } = "";
}

/// <summary>
/// SumUp payment link generation settings.
/// </summary>
public class SumUpSettings
{
    public bool Enabled { get; set; } = false;
    public string MerchantCode { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Currency { get; set; } = "GBP";
    public string PayToEmail { get; set; } = "";
    public string PaymentPageBaseUrl { get; set; } = "https://happy-ride-helper.lovable.app";
}

/// <summary>
/// A named SIP account that can be saved and recalled from a dropdown.
/// </summary>
public class SipAccount
{
    public string Label { get; set; } = "New Account";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 5060;
    public string Transport { get; set; } = "UDP";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthUser { get; set; }
    public string? Domain { get; set; }
    public string? DisplayName { get; set; }
    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;

    public SipSettings ToSipSettings() => new()
    {
        Server = Server, Port = Port, Transport = Transport,
        Username = Username, Password = Password, AuthUser = AuthUser,
        Domain = Domain, DisplayName = DisplayName, AutoAnswer = AutoAnswer,
        EnableStun = EnableStun, StunServer = StunServer, StunPort = StunPort
    };

    public void FromSipSettings(SipSettings s, string label)
    {
        Label = label; Server = s.Server; Port = s.Port; Transport = s.Transport;
        Username = s.Username; Password = s.Password; AuthUser = s.AuthUser;
        Domain = s.Domain; DisplayName = s.DisplayName; AutoAnswer = s.AutoAnswer;
        EnableStun = s.EnableStun; StunServer = s.StunServer ?? "stun.l.google.com"; StunPort = s.StunPort;
    }

    public override string ToString() => $"{Label} ({Username}@{Server})";
}
