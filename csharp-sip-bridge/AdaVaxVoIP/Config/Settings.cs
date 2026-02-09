namespace AdaVaxVoIP.Config;

public sealed class VaxVoIPSettings
{
    public string LicenseKey { get; set; } = "";
    public string DomainRealm { get; set; } = "taxi.local";
    public int SipPort { get; set; } = 5060;
    public int RtpPortMin { get; set; } = 10000;
    public int RtpPortMax { get; set; } = 20000;
    public bool EnableRecording { get; set; } = true;
    public string RecordingsPath { get; set; } = @"C:\Recordings\";
    public int MaxConcurrentCalls { get; set; } = 100;
}

public sealed class OpenAISettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-realtime-preview-2024-10-01";
    public string Voice { get; set; } = "alloy";
    public float Temperature { get; set; } = 0.6f;
}

public sealed class TaxiBookingSettings
{
    public string CompanyName { get; set; } = "Ada Taxi";
    public bool AutoAnswer { get; set; } = true;
}

public sealed class SupabaseSettings
{
    public string Url { get; set; } = "https://oerketnvlmptpfvttysy.supabase.co";
    public string AnonKey { get; set; } = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9lcmtldG52bG1wdHBmdnR0eXN5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Njg2NTg0OTAsImV4cCI6MjA4NDIzNDQ5MH0.QJPKuVmnP6P3RrzDSSBVbHGrduuDqFt7oOZ0E-cGNqU";
}

public sealed class GoogleMapsSettings
{
    public string ApiKey { get; set; } = "";
}

public sealed class DispatchSettings
{
    public string BsqdWebhookUrl { get; set; } = "";
    public string BsqdApiKey { get; set; } = "";
    public string WhatsAppWebhookUrl { get; set; } = "";
}
