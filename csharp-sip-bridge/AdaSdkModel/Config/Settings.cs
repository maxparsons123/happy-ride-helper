namespace AdaSdkModel.Config;

/// <summary>
/// Strongly-typed configuration models for AdaSdkModel.
/// </summary>
public sealed class AppSettings
{
    public SipSettings Sip { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public DispatchSettings Dispatch { get; set; } = new();
    public GoogleMapsSettings GoogleMaps { get; set; } = new();
    public SupabaseSettings Supabase { get; set; } = new();
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
    public string Voice { get; set; } = "shimmer";
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
    public string BsqdWebhookUrl { get; set; } = "";
    public string BsqdApiKey { get; set; } = "";
    public string WhatsAppWebhookUrl { get; set; } = "";
    public string MqttBrokerUrl { get; set; } = "wss://broker.hivemq.com:8884/mqtt";
}

public sealed class GoogleMapsSettings
{
    public string ApiKey { get; set; } = "";
}

public sealed class SupabaseSettings
{
    public string Url { get; set; } = "";
    public string AnonKey { get; set; } = "";
}
