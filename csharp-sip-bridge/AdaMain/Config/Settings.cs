namespace AdaMain.Config;

/// <summary>
/// Strongly-typed configuration models.
/// </summary>
public sealed class AppSettings
{
    public SipSettings Sip { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public DispatchSettings Dispatch { get; set; } = new();
    public GoogleMapsSettings GoogleMaps { get; set; } = new();
    public SupabaseSettings Supabase { get; set; } = new();
    public SimliSettings Simli { get; set; } = new();
    public SttSettings Stt { get; set; } = new();
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

    // STUN / NAT settings
    public bool EnableStun { get; set; } = true;
    public string StunServer { get; set; } = "stun.l.google.com";
    public int StunPort { get; set; } = 19302;

    /// <summary>Gets the effective auth username (AuthId if set, otherwise Username).</summary>
    public string EffectiveAuthUser => string.IsNullOrWhiteSpace(AuthId) ? Username : AuthId;
}

public sealed class OpenAiSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini-realtime-preview-2024-12-17";
    public string Voice { get; set; } = "shimmer";
}

public sealed class AudioSettings
{
    public string PreferredCodec { get; set; } = "PCMA";
    public double VolumeBoost { get; set; } = 2.5;
    public int EchoGuardMs { get; set; } = 200;

    /// <summary>
    /// RMS threshold for barge-in detection during soft gate.
    /// Lower = more sensitive (user can interrupt more easily).
    /// Higher = less sensitive (better noise rejection).
    /// Recommended: 1200-2000. Default: 1500.
    /// </summary>
    public float BargeInRmsThreshold { get; set; } = 1500f;

    /// <summary>Enable periodic audio quality diagnostics logging on the RTP thread. Disable to reduce jitter.</summary>
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

    /// <summary>Enable Simli avatar audio feeding during calls. Disable to eliminate potential jitter from upsampling/WebRTC.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class SttSettings
{
    public string DeepgramApiKey { get; set; } = "";
}
