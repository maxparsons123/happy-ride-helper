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
}

public sealed class DispatchSettings
{
    public string BsqdWebhookUrl { get; set; } = "";
    public string BsqdApiKey { get; set; } = "";
    public string WhatsAppWebhookUrl { get; set; } = "";
}

public sealed class GoogleMapsSettings
{
    public string ApiKey { get; set; } = "";
}

public sealed class SimliSettings
{
    public string ApiKey { get; set; } = "";
    public string FaceId { get; set; } = "";
}

public sealed class SttSettings
{
    public string DeepgramApiKey { get; set; } = "";
}
