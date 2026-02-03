using AdaMain.Ai;
using AdaMain.Audio;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Services;
using AdaMain.Sip;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdaMain;

/// <summary>
/// AdaMain - Standalone SIP-to-AI voice bridge.
/// Clean, modular architecture for taxi booking via phone.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║     AdaMain - Voice AI Taxi Bridge     ║");
        Console.WriteLine("║           v1.0 - Standalone            ║");
        Console.WriteLine("╚════════════════════════════════════════╝");
        Console.WriteLine();
        
        // Build configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        
        var settings = config.Get<AppSettings>() ?? new AppSettings();
        
        // Validate required settings
        if (string.IsNullOrEmpty(settings.OpenAi.ApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: OpenAI API key not configured in appsettings.json");
            Console.ResetColor();
            return;
        }
        
        // Build DI container
        var services = new ServiceCollection();
        
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(config.GetSection("Logging"));
            builder.AddConsole();
        });
        
        // Configuration
        services.AddSingleton(settings);
        services.AddSingleton(settings.Sip);
        services.AddSingleton(settings.OpenAi);
        services.AddSingleton(settings.Audio);
        services.AddSingleton(settings.Dispatch);
        services.AddSingleton(settings.GoogleMaps);
        
        // Audio
        services.AddSingleton<IAudioCodec>(_ => G711CodecFactory.Create(settings.Audio.PreferredCodec));
        
        // Services
        services.AddSingleton<IFareCalculator, FareCalculator>();
        services.AddSingleton<IDispatcher, BsqdDispatcher>();
        
        // AI Client factory
        services.AddTransient<IOpenAiClient>(sp => new OpenAiRealtimeClient(
            sp.GetRequiredService<ILogger<OpenAiRealtimeClient>>(),
            settings.OpenAi));
        
        // Session factory
        services.AddSingleton<Func<string, string, ICallSession>>(sp => (sessionId, callerId) =>
        {
            return new CallSession(
                sessionId,
                callerId,
                sp.GetRequiredService<ILogger<CallSession>>(),
                settings,
                sp.GetRequiredService<IOpenAiClient>(),
                sp.GetRequiredService<IAudioCodec>(),
                sp.GetRequiredService<IFareCalculator>(),
                sp.GetRequiredService<IDispatcher>());
        });
        
        // Core
        services.AddSingleton<SessionManager>();
        services.AddSingleton<SipServer>();
        
        var serviceProvider = services.BuildServiceProvider();
        
        // Get services
        var logger = serviceProvider.GetRequiredService<ILogger<SipServer>>();
        var sipServer = serviceProvider.GetRequiredService<SipServer>();
        var sessionManager = serviceProvider.GetRequiredService<SessionManager>();
        
        // Wire up events
        sipServer.OnRegistered += uri => Console.WriteLine($"✅ Registered: {uri}");
        sipServer.OnRegistrationFailed += err => Console.WriteLine($"❌ Registration failed: {err}");
        sipServer.OnCallStarted += caller => Console.WriteLine($"📞 Call started: {caller}");
        sipServer.OnCallEnded += reason => Console.WriteLine($"📴 Call ended: {reason}");
        
        // Cancellation
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n🛑 Shutting down...");
            cts.Cancel();
        };
        
        try
        {
            Console.WriteLine($"🔧 SIP Server: {settings.Sip.Server}:{settings.Sip.Port}");
            Console.WriteLine($"🔧 Transport: {settings.Sip.Transport}");
            Console.WriteLine($"🔧 Username: {settings.Sip.Username}");
            Console.WriteLine($"🔧 Codec: {settings.Audio.PreferredCodec}");
            Console.WriteLine();
            
            await sipServer.StartAsync(cts.Token);
            
            Console.WriteLine();
            Console.WriteLine("🎧 Ready for calls. Press Ctrl+C to exit.");
            Console.WriteLine();
            
            // Wait for shutdown
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            await sipServer.DisposeAsync();
            await sessionManager.DisposeAsync();
            Console.WriteLine("👋 Goodbye!");
        }
    }
}
