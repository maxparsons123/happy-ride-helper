using AdaVaxVoIP.Config;
using AdaVaxVoIP.Core;
using AdaVaxVoIP.Services;
using AdaVaxVoIP.Sip;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP;

public class Program
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation(@"
╔══════════════════════════════════════════════════════════╗
║           ADA TAXI — VAXVOIP BOOKING SYSTEM             ║
║                    Version 3.0                          ║
╚══════════════════════════════════════════════════════════╝
        ");

        var vaxSettings = new VaxVoIPSettings
        {
            LicenseKey = Environment.GetEnvironmentVariable("VAXVOIP_LICENSE") ?? "TRIAL",
            DomainRealm = "taxi.local",
            SipPort = int.TryParse(Environment.GetEnvironmentVariable("SIP_PORT"), out var sp) ? sp : 5060,
            EnableRecording = true,
            RecordingsPath = Environment.GetEnvironmentVariable("RECORDINGS_PATH") ?? @"C:\TaxiRecordings\",
            MaxConcurrentCalls = 50
        };

        var openAISettings = new OpenAISettings
        {
            ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-realtime-preview-2024-10-01",
            Voice = Environment.GetEnvironmentVariable("OPENAI_VOICE") ?? "alloy"
        };

        var taxiSettings = new TaxiBookingSettings
        {
            CompanyName = "Ada Taxi",
            AutoAnswer = true
        };

        var supabaseSettings = new SupabaseSettings
        {
            Url = Environment.GetEnvironmentVariable("SUPABASE_URL") ?? "https://oerketnvlmptpfvttysy.supabase.co",
            AnonKey = Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY") ?? "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9lcmtldG52bG1wdHBmdnR0eXN5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Njg2NTg0OTAsImV4cCI6MjA4NDIzNDQ5MH0.QJPKuVmnP6P3RrzDSSBVbHGrduuDqFt7oOZ0E-cGNqU"
        };

        var googleSettings = new GoogleMapsSettings
        {
            ApiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? ""
        };

        var dispatchSettings = new DispatchSettings
        {
            BsqdWebhookUrl = Environment.GetEnvironmentVariable("BSQD_WEBHOOK_URL") ?? "",
            BsqdApiKey = Environment.GetEnvironmentVariable("BSQD_API_KEY") ?? "",
            WhatsAppWebhookUrl = Environment.GetEnvironmentVariable("WHATSAPP_WEBHOOK_URL") ?? ""
        };

        if (string.IsNullOrWhiteSpace(openAISettings.ApiKey))
        {
            logger.LogError("❌ OPENAI_API_KEY environment variable is required");
            return;
        }

        var fareCalculator = new FareCalculator(
            loggerFactory.CreateLogger<FareCalculator>(), googleSettings, supabaseSettings);

        var dispatcher = new Dispatcher(
            loggerFactory.CreateLogger<Dispatcher>(), dispatchSettings);

        var sipServer = new VaxVoIPSipServer(
            loggerFactory.CreateLogger<VaxVoIPSipServer>(), vaxSettings, taxiSettings);

        var orchestrator = new TaxiBookingOrchestrator(
            loggerFactory.CreateLogger<TaxiBookingOrchestrator>(),
            loggerFactory,
            sipServer,
            openAISettings,
            fareCalculator,
            dispatcher);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await sipServer.StartAsync(cts.Token);
            logger.LogInformation("🚕 System ready. Press Ctrl+C to exit.");
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Shutting down...");
        }
        finally
        {
            await orchestrator.DisposeAsync();
            await sipServer.DisposeAsync();
        }
    }
}
