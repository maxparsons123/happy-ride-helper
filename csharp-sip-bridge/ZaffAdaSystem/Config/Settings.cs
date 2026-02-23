namespace ZaffAdaSystem.Config;

/// <summary>
/// Strongly-typed configuration models for ZaffAdaSystem.
/// </summary>
public sealed class AppSettings
{
    public SipSettings Sip { get; set; } = new();

    /// <summary>Named SIP accounts for quick switching.</summary>
    public List<SipAccount> SipAccounts { get; set; } = new();

    /// <summary>Index of the currently selected SIP account (-1 = use inline Sip settings).</summary>
    public int SelectedSipAccountIndex { get; set; } = -1;

    public OpenAiSettings OpenAi { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public DispatchSettings Dispatch { get; set; } = new();
    public GoogleMapsSettings GoogleMaps { get; set; } = new();
    public SupabaseSettings Supabase { get; set; } = new();
    public SimliSettings Simli { get; set; } = new();
    public IcabbiSettings Icabbi { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
    public ZoneGuardSettings ZoneGuard { get; set; } = new();
    public SumUpSettings SumUp { get; set; } = new();
}

public sealed class SimliSettings
{
    public string ApiKey { get; set; } = "vlw7tr7vxhhs52bi3rum7";
    public string FaceId { get; set; } = "5fc23ea5-8175-4a82-aaaf-cdd8c88543dc";

    /// <summary>Enable Simli avatar audio feeding during calls. Disable to eliminate potential jitter from upsampling/WebRTC.</summary>
    public bool Enabled { get; set; } = false;
}

public sealed class IcabbiSettings
{
    public bool Enabled { get; set; } = false;
    public string AppKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string TenantBase { get; set; } = "https://yourtenant.icabbi.net";
    public int SiteId { get; set; } = 0;
    public string CompanyId { get; set; } = "";
}

public sealed class SipSettings
{
    public string Server { get; set; } = "sip.example.com";
    public int Port { get; set; } = 5060;

    /// <summary>
    /// Transport mode: "UDP", "TCP", or "TCP_GAMMA".
    /// TCP_GAMMA uses IP-authenticated TCP (no REGISTER) for Gamma SIP Trunks.
    /// </summary>
    public string Transport { get; set; } = "UDP";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthId { get; set; }
    public string? Domain { get; set; }

    /// <summary>SIP display name shown in From header (e.g. "Ai Agent"). Cosmetic only — not used for auth.</summary>
    public string? DisplayName { get; set; }

    /// <summary>DDI (Direct Dial-In) number in E.164 format for Gamma trunks (e.g. "+441onal234567890").</summary>
    public string? Ddi { get; set; }

    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;

    /// <summary>Extension to blind-transfer to when caller requests a human operator.</summary>
    public string? OperatorTransferExtension { get; set; }

    public bool IsGammaTrunk => Transport.Equals("TCP_GAMMA", StringComparison.OrdinalIgnoreCase);
    public string EffectiveAuthUser => string.IsNullOrWhiteSpace(AuthId) ? Username : AuthId;
    public string EffectiveDdi => string.IsNullOrWhiteSpace(Ddi) ? Username : Ddi;
}

public sealed class OpenAiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-realtime-preview";
    public string Voice { get; set; } = "alloy";

    /// <summary>Use PCM16 24kHz (HighSample) instead of G.711 A-law passthrough.</summary>
    public bool UseHighSample { get; set; } = true;

    /// <summary>Ingress gain (caller→AI). Applied in PCM domain when UseHighSample=true.</summary>
    public double IngressGain { get; set; } = 4.0;

    /// <summary>Egress gain (AI→caller). Applied in PCM domain when UseHighSample=true.</summary>
    public double EgressGain { get; set; } = 1.0;
}

public sealed class AudioSettings
{
    public string PreferredCodec { get; set; } = "PCMA";
    public double VolumeBoost { get; set; } = 1.0;
    public double IngressVolumeBoost { get; set; } = 4.0;
    public int EchoGuardMs { get; set; } = 200;
    public float BargeInRmsThreshold { get; set; } = 1500f;
    public bool EnableDiagnostics { get; set; } = true;

    /// <summary>
    /// High-pass thinning filter alpha (0.80 = very thin, 0.88 = crisp, 0.95 = natural).
    /// Set to 0 to disable the filter entirely. Disabled by default for HighSample mode.
    /// </summary>
    public float ThinningAlpha { get; set; } = 0.0f;
}

public sealed class DispatchSettings
{
    public string BsqdWebhookUrl { get; set; } = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/voice_AI_taxibot";
    public string BsqdApiKey { get; set; } = "sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v";
    public string WhatsAppWebhookUrl { get; set; } = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/Avaya";
    public string MqttBrokerUrl { get; set; } = "wss://broker.hivemq.com:8884/mqtt";
}

public sealed class GoogleMapsSettings
{
    public string ApiKey { get; set; } = "";
}

public sealed class SupabaseSettings
{
    public string Url { get; set; } = "https://oerketnvlmptpfvttysy.supabase.co";
    public string AnonKey { get; set; } = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9lcmtldG52bG1wdHBmdnR0eXN5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Njg2NTg0OTAsImV4cCI6MjA4NDIzNDQ5MH0.QJPKuVmnP6P3RrzDSSBVbHGrduuDqFt7oOZ0E-cGNqU";
}

/// <summary>
/// A named SIP account that can be saved and recalled from a dropdown.
/// </summary>
public sealed class SipAccount
{
    public string Label { get; set; } = "New Account";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 5060;
    public string Transport { get; set; } = "UDP";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthId { get; set; }
    public string? Domain { get; set; }
    public string? DisplayName { get; set; }
    public string? Ddi { get; set; }
    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;
    public string? OperatorTransferExtension { get; set; }

    public SipSettings ToSipSettings() => new()
    {
        Server = Server, Port = Port, Transport = Transport,
        Username = Username, Password = Password, AuthId = AuthId,
        Domain = Domain, DisplayName = DisplayName, Ddi = Ddi, AutoAnswer = AutoAnswer,
        EnableStun = EnableStun, StunServer = StunServer, StunPort = StunPort,
        OperatorTransferExtension = OperatorTransferExtension
    };

    public void FromSipSettings(SipSettings s, string label)
    {
        Label = label; Server = s.Server; Port = s.Port; Transport = s.Transport;
        Username = s.Username; Password = s.Password; AuthId = s.AuthId;
        Domain = s.Domain; DisplayName = s.DisplayName; Ddi = s.Ddi; AutoAnswer = s.AutoAnswer;
        EnableStun = s.EnableStun; StunServer = s.StunServer; StunPort = s.StunPort;
        OperatorTransferExtension = s.OperatorTransferExtension;
    }

    public override string ToString() => $"{Label} ({Username}@{Server})";
}

public sealed class GeminiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.0-flash";
    public bool Enabled { get; set; } = false;
}

public sealed class ZoneGuardSettings
{
    public bool Enabled { get; set; } = false;
    public double MinSimilarity { get; set; } = 0.25;
    public bool BlockOnNoMatch { get; set; } = false;
    public int MaxCandidates { get; set; } = 10;
    public string CompanyId { get; set; } = "";
}

public sealed class SumUpSettings
{
    public bool Enabled { get; set; } = false;
    public string MerchantCode { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Currency { get; set; } = "GBP";
    public string PayToEmail { get; set; } = "";
    public string PaymentPageBaseUrl { get; set; } = "";
}
