using AdaMain.Ai;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Services;
using AdaMain.Sip;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaMain.Server;

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
                // Bind configuration sections
                var settings = ctx.Configuration.Get<AppSettings>() ?? new AppSettings();
                services.AddSingleton(settings);
                services.AddSingleton(settings.Sip);
                services.AddSingleton(settings.OpenAi);
                services.AddSingleton(settings.Audio);
                services.AddSingleton(settings.Dispatch);
                services.AddSingleton(settings.GoogleMaps);
                services.AddSingleton(settings.Supabase);
                services.AddSingleton(settings.Stt);

                // Core services
                services.AddSingleton<SessionManager>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<SessionManager>>();
                    var factory = sp.GetRequiredService<CallSessionFactory>();
                    return new SessionManager(logger, factory.Create);
                });

                services.AddSingleton<CallSessionFactory>();
                services.AddSingleton<SipServer>();

                // Hosted service (the main worker)
                services.AddHostedService<SipServerWorker>();
            })
            .Build();

        await host.RunAsync();
    }
}
