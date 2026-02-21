namespace AdaSdkModel.Config;

/// <summary>
/// Strongly-typed configuration models for AdaSdkModel.
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
    public GeminiSettings Gemini { get; set; } = new();
    public SimliSettings Simli { get; set; } = new();
    public IcabbiSettings Icabbi { get; set; } = new();
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
    public string Transport { get; set; } = "UDP";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthId { get; set; }
    public string? Domain { get; set; }

    /// <summary>SIP display name shown in From header (e.g. "Ai Agent"). Cosmetic only — not used for auth.</summary>
    public string? DisplayName { get; set; }

    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;

    public string EffectiveAuthUser => string.IsNullOrWhiteSpace(AuthId) ? Username : AuthId;
}

public sealed class OpenAiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini-realtime-preview-2024-12-17";
    public string Voice { get; set; } = "alloy";

    /// <summary>Use PCM16 24kHz (HighSample) instead of G.711 A-law passthrough.</summary>
    public bool UseHighSample { get; set; } = false;

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
    /// Set to 0 to disable the filter entirely.
    /// </summary>
    public float ThinningAlpha { get; set; } = 0.88f;
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

/// <summary>
/// Settings for direct Google Gemini API calls (address geocoding/fare).
/// Each desktop instance uses its own API key — no shared gateway limits.
/// </summary>
public sealed class GeminiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.0-flash";

    /// <summary>When true, use local Gemini for address-dispatch instead of the edge function.</summary>
    public bool Enabled { get; set; } = false;
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
    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;

    public SipSettings ToSipSettings() => new()
    {
        Server = Server, Port = Port, Transport = Transport,
        Username = Username, Password = Password, AuthId = AuthId,
        Domain = Domain, DisplayName = DisplayName, AutoAnswer = AutoAnswer,
        EnableStun = EnableStun, StunServer = StunServer, StunPort = StunPort
    };

    public void FromSipSettings(SipSettings s, string label)
    {
        Label = label; Server = s.Server; Port = s.Port; Transport = s.Transport;
        Username = s.Username; Password = s.Password; AuthId = s.AuthId;
        Domain = s.Domain; DisplayName = s.DisplayName; AutoAnswer = s.AutoAnswer;
        EnableStun = s.EnableStun; StunServer = s.StunServer; StunPort = s.StunPort;
    }

    public override string ToString() => $"{Label} ({Username}@{Server})";
}

/// <summary>
/// Controls the Zone Guard — pickup address validation against zone POIs
/// extracted from the Zone Editor (streets + businesses within dispatch zones).
///
/// FEATURE FLAG: Enabled = false by default.
/// To activate: set "ZoneGuard": { "Enabled": true } in appsettings.json
/// and ensure zone POIs have been extracted via the Zone Editor.
///
/// Plug-in point (NOT YET WIRED):
///   CallSession.HandleSyncBookingData() → after house-number guard, before fare calc.
/// </summary>
public sealed class SumUpSettings
{
    /// <summary>Enable SumUp payment link generation for card payments.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>SumUp Merchant Code (used as checkout reference prefix).</summary>
    public string MerchantCode { get; set; } = "";

    /// <summary>SumUp OAuth API key (Personal Token with checkout scope).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Currency for checkouts.</summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>SumUp merchant email for pay_to_email field.</summary>
    public string PayToEmail { get; set; } = "";

    /// <summary>Base URL of the hosted payment page (e.g. https://happy-ride-helper.lovable.app). If empty, defaults to the main app URL.</summary>
    public string PaymentPageBaseUrl { get; set; } = "https://happy-ride-helper.lovable.app";
}

public sealed class ZoneGuardSettings
{
    /// <summary>Master on/off switch. False = service is built but does nothing.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum fuzzy similarity score to consider a POI a match (0-1).
    /// 0.25 = loose (good recall, more false positives)
    /// 0.35 = moderate (recommended starting point)
    /// 0.50 = strict (low false positives, may miss valid addresses)
    /// </summary>
    public float MinSimilarity { get; set; } = 0.25f;

    /// <summary>
    /// When true, reject booking if pickup doesn't match any zone POI.
    /// When false (default), only log a warning — booking still proceeds.
    /// </summary>
    public bool BlockOnNoMatch { get; set; } = false;

    /// <summary>Maximum number of candidate POIs to return per check.</summary>
    public int MaxCandidates { get; set; } = 10;

    /// <summary>
    /// Optional company ID to scope POI matching to a specific company's zones.
    /// Empty = search all active zones across all companies.
    /// </summary>
    public string CompanyId { get; set; } = "";
}
