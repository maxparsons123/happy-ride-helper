namespace AdaVaxVoIP.Config;

/// <summary>
/// Unified configuration — mirrors AdaMain's AppSettings for shared appsettings.json.
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
    public SttSettings Stt { get; set; } = new();
    public VaxVoIPSettings VaxVoIP { get; set; } = new();
    public TaxiBookingSettings Taxi { get; set; } = new();
}

public sealed class SipSettings
{
    public string Server { get; set; } = "bellen.dcota.nl";
    public int Port { get; set; } = 5060;
    public string Transport { get; set; } = "UDP";
    public string Username { get; set; } = "1234";
    public string Password { get; set; } = "293183719426";
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
    public string ApiKey { get; set; } = "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";
    public string Model { get; set; } = "gpt-4o-mini-realtime-preview-2024-12-17";
    public string Voice { get; set; } = "shimmer";
}

public sealed class AudioSettings
{
    public string PreferredCodec { get; set; } = "PCMA";
    public double VolumeBoost { get; set; } = 1.0;
    public double IngressVolumeBoost { get; set; } = 2.5;
    public int EchoGuardMs { get; set; } = 200;
    public float BargeInRmsThreshold { get; set; } = 1500f;
    public bool EnableDiagnostics { get; set; } = true;
}

public sealed class DispatchSettings
{
    public string BsqdWebhookUrl { get; set; } = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/voice_AI_taxibot";
    public string BsqdApiKey { get; set; } = "sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v";
    public string WhatsAppWebhookUrl { get; set; } = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/Avaya";
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

public sealed class SimliSettings
{
    public string ApiKey { get; set; } = "vlw7tr7vxhhs52bi3rum7";
    public string FaceId { get; set; } = "5fc23ea5-8175-4a82-aaaf-cdd8c88543dc";
    public bool Enabled { get; set; } = false;
}

public sealed class SttSettings
{
    public string DeepgramApiKey { get; set; } = "";
}

public sealed class VaxVoIPSettings
{
    public string LicenseKey { get; set; } = "";
    public string DomainRealm { get; set; } = "taxi.local";
    public int ListenPort { get; set; } = 8060;
    public int RtpPortMin { get; set; } = 10000;
    public int RtpPortMax { get; set; } = 20000;
    public bool EnableRecording { get; set; } = true;
    public string RecordingsPath { get; set; } = @"C:\Recordings\";
    public int MaxConcurrentCalls { get; set; } = 100;
}

public sealed class TaxiBookingSettings
{
    public string CompanyName { get; set; } = "Ada Taxi";
    public bool AutoAnswer { get; set; } = true;
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
