namespace WhatsAppTaxiBooker.Config;

/// <summary>
/// Application configuration loaded from appsettings.json.
/// </summary>
public sealed class AppConfig
{
    public WhatsAppConfig WhatsApp { get; set; } = new();
    public GeminiConfig Gemini { get; set; } = new();
    public WebhookConfig Webhook { get; set; } = new();
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
