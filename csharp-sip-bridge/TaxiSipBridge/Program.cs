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

        var config = new BridgeConfig
        {
            SipPort = GetEnvInt("SIP_PORT", 5060),
            SipUsername = GetEnvString("SIP_USERNAME", "taxi-bridge"),
            SipPassword = GetEnvString("SIP_PASSWORD", ""),
            WebSocketUrl = GetEnvString("WS_URL", "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime"),
            MaxConcurrentCalls = GetEnvInt("MAX_CALLS", 50),
            EnableTls = GetEnvBool("ENABLE_TLS", false)
        };

        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  SIP Port: {config.SipPort}");
        Console.WriteLine($"  SIP Username: {config.SipUsername}");
        Console.WriteLine($"  WebSocket URL: {config.WebSocketUrl}");
        Console.WriteLine($"  Max Concurrent Calls: {config.MaxConcurrentCalls}");
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
