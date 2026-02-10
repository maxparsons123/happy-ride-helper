using AdaMain.Sip;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaMain.Server;

/// <summary>
/// Background service that runs the SIP server as a long-lived daemon.
/// Handles graceful startup/shutdown for systemd integration.
/// </summary>
public sealed class SipServerWorker : BackgroundService
{
    private readonly SipServer _sipServer;
    private readonly ILogger<SipServerWorker> _logger;

    public SipServerWorker(SipServer sipServer, ILogger<SipServerWorker> logger)
    {
        _sipServer = sipServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("╔══════════════════════════════════════╗");
        _logger.LogInformation("║   Ada Taxi AI — Multi-Call Server    ║");
        _logger.LogInformation("╚══════════════════════════════════════╝");

        // Wire SIP events → structured logging
        _sipServer.OnLog += msg => _logger.LogDebug("{SipLog}", msg);
        _sipServer.OnRegistered += uri => _logger.LogInformation("✅ SIP Registered: {Uri}", uri);
        _sipServer.OnRegistrationFailed += err => _logger.LogError("❌ SIP Registration failed: {Error}", err);
        _sipServer.OnCallStarted += (sessionId, caller) => _logger.LogInformation("📞 Call {SessionId} started: {Caller} (active: {Count})", sessionId, caller, _sipServer.ActiveCallCount);
        _sipServer.OnCallEnded += (sessionId, reason) => _logger.LogInformation("📴 Call {SessionId} ended: {Reason} (active: {Count})", sessionId, reason, _sipServer.ActiveCallCount);
        _sipServer.OnActiveCallCountChanged += count => _logger.LogInformation("📊 Active calls: {Count}", count);

        try
        {
            await _sipServer.StartAsync(stoppingToken);
            _logger.LogInformation("🟢 SIP server started — waiting for calls...");

            // Keep alive until shutdown signal
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("🛑 Shutdown signal received");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "💥 SIP server crashed");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 Stopping SIP server...");
        await _sipServer.StopAsync();
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("🛑 SIP server stopped");
    }
}
