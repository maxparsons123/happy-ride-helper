using AdaCleanVersion.Audio;
using AdaCleanVersion.Config;
using AdaCleanVersion.Engine;
using AdaCleanVersion.Realtime;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using AdaCleanVersion.Sip;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using TaxiBot.Deterministic;

namespace AdaCleanVersion;

/// <summary>
/// Factory that wires up the full clean architecture stack.
/// Creates CleanSipBridge with DirectBookingBuilder + FareGeocodingService,
/// and auto-spawns OpenAiRealtimeClient per call for bidirectional audio.
/// </summary>
public static class CleanBridgeFactory
{
    public static CleanSipBridge Create(CleanAppSettings settings, ILogger logger, HttpClient? sharedClient = null)
    {
        var extractionService = new DirectBookingBuilder(logger);

        var fareService = new FareGeocodingService(
            supabaseUrl: settings.SupabaseUrl,
            serviceRoleKey: settings.SupabaseServiceRoleKey,
            logger: logger,
            httpClient: sharedClient);

        var callerLookup = new CallerLookupService(
            supabaseUrl: settings.SupabaseUrl,
            serviceRoleKey: settings.SupabaseServiceRoleKey,
            logger: logger,
            httpClient: sharedClient);

        // IcabbiBookingService (if configured)
        IcabbiBookingService? icabbiService = null;
        if (!string.IsNullOrWhiteSpace(settings.Icabbi.TenantBase))
        {
            var icabbiLogger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug))
                .CreateLogger<IcabbiBookingService>();
            icabbiService = new IcabbiBookingService(icabbiLogger, settings.Icabbi, settings.Supabase);
        }

        var bridge = new CleanSipBridge(logger, settings, extractionService, fareService, callerLookup);

        bridge.OnCallConnected += (callId, rtpSession, session) =>
        {
            _ = SpawnRealtimeClientAsync(callId, rtpSession, session, settings, logger, bridge, fareService, icabbiService);
        };

        return bridge;
    }

    private static async Task SpawnRealtimeClientAsync(
        string callId,
        VoIPMediaSession mediaSession,
        CleanCallSession session,
        CleanAppSettings settings,
        ILogger logger,
        CleanSipBridge bridge,
        FareGeocodingService fareService,
        IcabbiBookingService? icabbiService)
    {
        var codec = G711CodecType.PCMA;

        // v8: Pure transport bridge — pass systemPrompt + callerPhone, not session
        var client = new OpenAiRealtimeClient(
            apiKey: settings.OpenAi.ApiKey,
            model: settings.OpenAi.Model,
            voice: settings.OpenAi.Voice,
            callId: callId,
            systemPrompt: session.GetSystemPrompt(),
            rtpSession: mediaSession,
            logger: logger,
            fareService: fareService,
            icabbiService: icabbiService,
            callerPhone: session.CallerId,
            codec: codec,
            mediaSession: mediaSession);

        client.OnLog += msg => logger.LogInformation(msg);
        client.OnAudioOut += frame => bridge.RaiseAudioOut(frame);
        client.OnBargeIn += () => bridge.RaiseBargeIn();
        client.OnTransfer += reason => logger.LogWarning($"[RT:{callId}] Transfer requested: {reason}");
        client.OnHangup += reason =>
        {
            logger.LogInformation($"[RT:{callId}] Engine hangup: {reason}");
            // Trigger SIP BYE via bridge if needed
        };
        client.OnStageChanged += stage =>
        {
            // Sync RT engine stage → session engine state
            var mapped = MapStageToCollectionState(stage);
            if (mapped.HasValue)
            {
                session.Engine.ForceState(mapped.Value);
                logger.LogInformation($"[RT:{callId}] Session synced: {stage} → {mapped.Value}");
            }
        };

        try
        {
            await client.ConnectAsync();

            mediaSession.OnRtpClosed += async (reason) =>
            {
                logger.LogInformation($"[RT:{callId}] RTP closed — disposing Realtime client");
                await client.DisposeAsync();
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"[RT:{callId}] Failed to connect OpenAI Realtime");
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Maps DeterministicBookingEngine Stage → CallStateEngine CollectionState.
    /// Returns null for stages that have no meaningful session-level equivalent.
    /// </summary>
    private static CollectionState? MapStageToCollectionState(Stage stage) => stage switch
    {
        Stage.Start => CollectionState.Greeting,
        Stage.CollectPickup => CollectionState.CollectingPickup,
        Stage.CollectDropoff => CollectionState.CollectingDestination,
        Stage.CollectPassengers => CollectionState.CollectingPassengers,
        Stage.CollectTime => CollectionState.CollectingPickupTime,
        Stage.ConfirmDetails => CollectionState.AwaitingConfirmation,
        Stage.Dispatching => CollectionState.Dispatched,
        Stage.Booked => CollectionState.Dispatched,
        Stage.End => CollectionState.Ending,
        Stage.Escalate => CollectionState.Ending,
        _ => null
    };
}
