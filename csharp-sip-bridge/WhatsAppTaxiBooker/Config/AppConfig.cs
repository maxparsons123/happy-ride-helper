namespace WhatsAppTaxiBooker.Config;

/// <summary>
/// Application configuration loaded from appsettings.json.
/// </summary>
public sealed class AppConfig
{
    public WhatsAppConfig WhatsApp { get; set; } = new();
    public GeminiConfig Gemini { get; set; } = new();
    public WebhookConfig Webhook { get; set; } = new();
    public MqttConfig Mqtt { get; set; } = new();
    public NgrokConfig Ngrok { get; set; } = new();
}

public sealed class WhatsAppConfig
{
    /// <summary>Meta Cloud API access token (permanent or temporary).</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Phone Number ID from Meta Business dashboard.</summary>
    public string PhoneNumberId { get; set; } = "";

    /// <summary>Webhook verify token (you choose this, must match Meta config).</summary>
    public string VerifyToken { get; set; } = "taxi-booker-verify-2026";
}

public sealed class GeminiConfig
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.5-flash";
}

public sealed class WebhookConfig
{
    /// <summary>Local port for the HTTP webhook listener.</summary>
    public int Port { get; set; } = 5088;
}

public sealed class MqttConfig
{
    /// <summary>MQTT broker URL (e.g. wss://broker.hivemq.com:8884/mqtt).</summary>
    public string BrokerUrl { get; set; } = "wss://broker.hivemq.com:8884/mqtt";

    /// <summary>Topic prefix for publishing booking requests.</summary>
    public string TopicPrefix { get; set; } = "pubs/requests";
}

public sealed class NgrokConfig
{
    /// <summary>Enable ngrok tunnel on start.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Path to ngrok executable.</summary>
    public string NgrokPath { get; set; } = @"C:\ngrok\ngrok.exe";

    /// <summary>Reserved domain (leave empty for random URL).</summary>
    public string ReservedDomain { get; set; } = "";
}
