using AdaCleanVersion.Audio;
using AdaCleanVersion.Config;
using AdaCleanVersion.Realtime;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using AdaCleanVersion.Sip;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
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
            _ = SpawnRealtimeClientAsync(callId, rtpSession, session, settings, logger, bridge);
        };

        return bridge;
    }

    private static async Task SpawnRealtimeClientAsync(
        string callId,
        VoIPMediaSession mediaSession,
        CleanCallSession session,
        CleanAppSettings settings,
        ILogger logger,
        CleanSipBridge bridge)
    {
        // SIP is forced to PCMA-only — always use PCMA for OpenAI too
        var codec = G711CodecType.PCMA;

        // VoIPMediaSession extends RTPSession — cast to base for OpenAiRealtimeClient.
        RTPSession rtpSession = mediaSession;

        var client = new OpenAiRealtimeClient(
            apiKey: settings.OpenAi.ApiKey,
            model: settings.OpenAi.Model,
            voice: settings.OpenAi.Voice,
            callId: callId,
            rtpSession: rtpSession,
            session: session,
            logger: logger,
            codec: codec);

        client.OnLog += msg => logger.LogInformation(msg);

        // Proxy audio/barge-in events to bridge for UI consumers (e.g. Simli avatar)
        client.OnAudioOut += frame => bridge.RaiseAudioOut(frame);
        client.OnBargeIn += () => bridge.RaiseBargeIn();

        try
        {
            await client.ConnectAsync();

            // Keep alive until RTP session closes
            rtpSession.OnRtpClosed += async (reason) =>
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
