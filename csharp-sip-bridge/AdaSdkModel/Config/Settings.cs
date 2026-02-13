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
    public IcabbiSettings Icabbi { get; set; } = new();
}

public sealed class IcabbiSettings
{
    public bool Enabled { get; set; } = false;
    public string AppKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string TenantBase { get; set; } = "https://yourtenant.icabbi.net";
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
}

public sealed class AudioSettings
{
    public string PreferredCodec { get; set; } = "PCMA";
    public double VolumeBoost { get; set; } = 1.0;
    public double IngressVolumeBoost { get; set; } = 4.0;
    public int EchoGuardMs { get; set; } = 200;
    public float BargeInRmsThreshold { get; set; } = 1500f;
    public bool EnableDiagnostics { get; set; } = true;
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
    public bool AutoAnswer { get; set; } = true;
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;

    public SipSettings ToSipSettings() => new()
    {
        Server = Server, Port = Port, Transport = Transport,
        Username = Username, Password = Password, AuthId = AuthId,
        Domain = Domain, AutoAnswer = AutoAnswer,
        EnableStun = EnableStun, StunServer = StunServer, StunPort = StunPort
    };

    public void FromSipSettings(SipSettings s, string label)
    {
        Label = label; Server = s.Server; Port = s.Port; Transport = s.Transport;
        Username = s.Username; Password = s.Password; AuthId = s.AuthId;
        Domain = s.Domain; AutoAnswer = s.AutoAnswer;
        EnableStun = s.EnableStun; StunServer = s.StunServer; StunPort = s.StunPort;
    }

    public override string ToString() => $"{Label} ({Username}@{Server})";
}
