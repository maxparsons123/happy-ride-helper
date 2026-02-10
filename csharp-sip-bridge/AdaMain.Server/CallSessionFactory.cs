using AdaMain.Ai;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Services;
using Microsoft.Extensions.Logging;

namespace AdaMain.Server;

/// <summary>
/// Factory for creating fully-wired CallSession instances.
/// Replaces the WinForms MainForm.CreateCallSession method.
/// No UI dependencies — all events go to structured logging.
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

    /// <summary>
    /// Create a fully-wired CallSession for a new incoming call.
    /// This is the <c>Func&lt;string, string, ICallSession&gt;</c> expected by SessionManager.
    /// </summary>
    public ICallSession Create(string sessionId, string callerId)
    {
        // Create AI client (G.711 passthrough mode — no resampling needed)
        var aiClient = new OpenAiG711Client(
            _loggerFactory.CreateLogger<OpenAiG711Client>(),
            _settings.OpenAi);

        // Create fare calculator
        var fareCalculator = new FareCalculator(
            _loggerFactory.CreateLogger<FareCalculator>(),
            _settings.GoogleMaps,
            _settings.Supabase);

        // Create dispatcher
        var dispatcher = new BsqdDispatcher(
            _loggerFactory.CreateLogger<BsqdDispatcher>(),
            _settings.Dispatch);

        // Create session
        var session = new CallSession(
            sessionId,
            callerId,
            _loggerFactory.CreateLogger<CallSession>(),
            _settings,
            aiClient,
            fareCalculator,
            dispatcher);

        // Wire events → structured logging (no UI)
        var logger = _loggerFactory.CreateLogger($"Call.{sessionId}");

        session.OnTranscript += (role, text) =>
            logger.LogInformation("💬 [{Role}] {Text}", role, text);

        session.OnBookingUpdated += booking =>
        {
            logger.LogInformation("📋 Booking: Name={Name} Pickup={Pickup} Dest={Dest} Fare={Fare} Confirmed={Confirmed}",
                booking.Name, booking.Pickup, booking.Destination, booking.Fare, booking.Confirmed);
        };

        return session;
    }
}
