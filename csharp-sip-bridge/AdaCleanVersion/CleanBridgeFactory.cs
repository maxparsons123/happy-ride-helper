using AdaCleanVersion.Config;
using AdaCleanVersion.Services;
using AdaCleanVersion.Sip;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion;

/// <summary>
/// Factory that wires up the full clean architecture stack.
/// Creates CleanSipBridge with StructureOnlyEngine as the extraction service.
/// </summary>
public static class CleanBridgeFactory
{
    /// <summary>
    /// Build a fully-wired CleanSipBridge using StructureOnlyEngine for extraction.
    /// </summary>
    public static CleanSipBridge Create(CleanAppSettings settings, ILogger logger, HttpClient? sharedClient = null)
    {
        // StructureOnlyEngine — direct OpenAI structured output normalization
        var extractionService = new StructureOnlyEngine(
            openAiApiKey: settings.OpenAi.ApiKey,
            logger: logger,
            httpClient: sharedClient);

        // Caller lookup — queries callers table for returning caller context
        var callerLookup = new CallerLookupService(
            supabaseUrl: settings.SupabaseUrl,
            serviceRoleKey: settings.SupabaseServiceRoleKey,
            logger: logger,
            httpClient: sharedClient);

        return new CleanSipBridge(logger, settings, extractionService, callerLookup);
    }
}
