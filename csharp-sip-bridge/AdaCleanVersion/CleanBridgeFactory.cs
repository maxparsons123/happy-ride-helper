using AdaCleanVersion.Config;
using AdaCleanVersion.Realtime;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using AdaCleanVersion.Sip;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AdaCleanVersion;

/// <summary>
/// Factory that wires up the full clean architecture stack.
/// Creates CleanSipBridge with StructureOnlyEngine + FareGeocodingService,
/// and auto-spawns OpenAiRealtimeClient per call for bidirectional audio.
/// </summary>
public static class CleanBridgeFactory
{
    /// <summary>
    /// Build a fully-wired CleanSipBridge with auto-spawning Realtime clients.
    /// </summary>
    public static CleanSipBridge Create(CleanAppSettings settings, ILogger logger, HttpClient? sharedClient = null)
    {
        var extractionService = new StructureOnlyEngine(
            openAiApiKey: settings.OpenAi.ApiKey,
            logger: logger,
            httpClient: sharedClient);

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

        var bridge = new CleanSipBridge(logger, settings, extractionService, fareService, callerLookup);

        // Auto-wire OpenAI Realtime client for each connected call
        bridge.OnCallConnected += (callId, rtpSession, session) =>
        {
            _ = SpawnRealtimeClientAsync(callId, rtpSession, session, settings, logger);
        };

        return bridge;
    }

    private static async Task SpawnRealtimeClientAsync(
        string callId,
        RTPSession rtpSession,
        CleanCallSession session,
        CleanAppSettings settings,
        ILogger logger)
    {
        var client = new OpenAiRealtimeClient(
            apiKey: settings.OpenAi.ApiKey,
            model: settings.OpenAi.Model,
            voice: settings.OpenAi.Voice,
            callId: callId,
            rtpSession: rtpSession,
            session: session,
            logger: logger);

        client.OnLog += msg => logger.LogInformation(msg);

        try
        {
            await client.ConnectAsync();

            // Keep alive until RTP session closes
            rtpSession.OnClosed += async (reason) =>
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
}
