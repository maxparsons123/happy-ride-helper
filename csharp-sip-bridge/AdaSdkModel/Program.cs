using AdaSdkModel.Ai;
using AdaSdkModel.Config;
using AdaSdkModel.Core;
using AdaSdkModel.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddEnvironmentVariables("ADA_");
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((ctx, services) =>
            {
                var settings = ctx.Configuration.Get<AppSettings>() ?? new AppSettings();
                services.AddSingleton(settings);
                services.AddSingleton(settings.Sip);
                services.AddSingleton(settings.OpenAi);
                services.AddSingleton(settings.Audio);
                services.AddSingleton(settings.Dispatch);
                services.AddSingleton(settings.GoogleMaps);
                services.AddSingleton(settings.Supabase);

                services.AddSingleton<SessionManager>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<SessionManager>>();
                    var factory = sp.GetRequiredService<CallSessionFactory>();
                    return new SessionManager(logger, factory.Create);
                });

                services.AddSingleton<CallSessionFactory>();
                services.AddSingleton<Sip.SipServer>();
                services.AddHostedService<SipServerWorker>();
            })
            .Build();

        await host.RunAsync();
    }
}

/// <summary>
/// Factory for creating fully-wired CallSession instances.
/// </summary>
public sealed class CallSessionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly AppSettings _settings;

    public CallSessionFactory(ILoggerFactory loggerFactory, AppSettings settings)
    {
        _loggerFactory = loggerFactory;
        _settings = settings;
    }

    public ICallSession Create(string sessionId, string callerId)
    {
        var aiClient = new OpenAiSdkClient(
            _loggerFactory.CreateLogger<OpenAiSdkClient>(),
            _settings.OpenAi);

        var fareCalculator = new FareCalculator(
            _loggerFactory.CreateLogger<FareCalculator>(),
            _settings.GoogleMaps,
            _settings.Supabase);

        var dispatcher = new BsqdDispatcher(
            _loggerFactory.CreateLogger<BsqdDispatcher>(),
            _settings.Dispatch);

        var session = new CallSession(
            sessionId, callerId,
            _loggerFactory.CreateLogger<CallSession>(),
            _settings, aiClient, fareCalculator, dispatcher);

        var logger = _loggerFactory.CreateLogger($"Call.{sessionId}");
        session.OnTranscript += (role, text) => logger.LogInformation("💬 [{Role}] {Text}", role, text);
        session.OnBookingUpdated += booking =>
            logger.LogInformation("📋 Booking: Name={Name} Pickup={Pickup} Dest={Dest} Fare={Fare}",
                booking.Name, booking.Pickup, booking.Destination, booking.Fare);

        return session;
    }
}

/// <summary>
/// Hosted service that starts/stops the SIP server.
/// </summary>
public sealed class SipServerWorker : IHostedService
{
    private readonly Sip.SipServer _sipServer;
    private readonly ILogger<SipServerWorker> _logger;

    public SipServerWorker(Sip.SipServer sipServer, ILogger<SipServerWorker> logger)
    {
        _sipServer = sipServer;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("🚀 AdaSdkModel starting...");
        await _sipServer.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("🛑 AdaSdkModel stopping...");
        await _sipServer.StopAsync();
    }
}
