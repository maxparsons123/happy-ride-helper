using AdaCleanVersion.Config;
using AdaCleanVersion.Services;
using AdaCleanVersion.Sip;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion;

/// <summary>
/// Factory that wires up the full clean architecture stack.
/// Creates CleanSipBridge with StructureOnlyEngine + FareGeocodingService.
/// </summary>
public static class CleanBridgeFactory
{
    /// <summary>
    /// Build a fully-wired CleanSipBridge.
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

        return new CleanSipBridge(logger, settings, extractionService, fareService, callerLookup);
    }
}
