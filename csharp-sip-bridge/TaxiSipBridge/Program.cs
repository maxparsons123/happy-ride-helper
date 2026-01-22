using Microsoft.Extensions.Logging;
using TaxiSipBridge.Services;

namespace TaxiSipBridge;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("===========================================");
        Console.WriteLine("  Taxi AI SIP Bridge - C# Edition");
        Console.WriteLine("  Multi-user RTP/SIP to WebSocket Bridge");
        Console.WriteLine("===========================================\n");

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddConsole(options =>
                {
                    options.TimestampFormat = "[HH:mm:ss] ";
                });
        });

        var config = new SipAdaBridgeConfig
        {
            SipServer = GetEnvString("SIP_SERVER", "206.189.123.28"),
            SipPort = GetEnvInt("SIP_PORT", 5060),
            SipUser = GetEnvString("SIP_USER", "max201"),
            SipPassword = GetEnvString("SIP_PASSWORD", "qwe70954504118"),
            Transport = GetEnvString("SIP_TRANSPORT", "UDP").ToUpper() == "TCP" 
                ? SipTransportType.TCP 
                : SipTransportType.UDP,
            AudioMode = Enum.TryParse<AudioMode>(GetEnvString("AUDIO_MODE", "Standard"), out var mode) 
                ? mode 
                : AudioMode.Standard,
            JitterBufferMs = GetEnvInt("JITTER_BUFFER_MS", 60),
            MaxConcurrentCalls = GetEnvInt("MAX_CALLS", 50),
            // IMPORTANT: Use .functions.supabase.co subdomain for reliability
            AdaWsUrl = GetEnvString("ADA_WS_URL", "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired")
        };

        // Validate configuration
        if (!config.IsValid(out var error))
        {
            Console.WriteLine($"❌ Configuration error: {error}");
            return;
        }

        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  SIP Server: {config.SipServer}:{config.SipPort} ({config.Transport})");
        Console.WriteLine($"  SIP User: {config.SipUser}");
        Console.WriteLine($"  Audio Mode: {config.AudioMode}");
        Console.WriteLine($"  Jitter Buffer: {config.JitterBufferMs}ms");
        Console.WriteLine($"  Max Concurrent Calls: {config.MaxConcurrentCalls}");
        Console.WriteLine($"  Ada WS URL: {config.AdaWsUrl}");
        Console.WriteLine();

        var bridge = new SipBridgeService(config, loggerFactory);
        
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown requested...");
            cts.Cancel();
        };

        try
        {
            await bridge.StartAsync(cts.Token);
            Console.WriteLine("SIP Bridge is running. Press Ctrl+C to stop.\n");
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Shutting down gracefully...");
        }
        finally
        {
            await bridge.StopAsync();
        }
    }

    static string GetEnvString(string key, string defaultValue) =>
        Environment.GetEnvironmentVariable(key) ?? defaultValue;

    static int GetEnvInt(string key, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : defaultValue;

    static bool GetEnvBool(string key, bool defaultValue) =>
        bool.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : defaultValue;
}
