using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Local Gemini reconciler — a lightweight backup that runs a focused AI call
/// to reconcile Ada's question, user raw speech, and Ada's readback into a
/// clean, geocoded address. Uses the Lovable AI gateway (Gemini 2.5 Flash).
///
/// This is the "Level 5" accuracy piece: by providing a transcript window
/// (question → answer → readback), Gemini can resolve complex POIs and
/// handle STT mishearings with full conversational context.
///
/// Usage: Fallback when address-dispatch is unavailable, or as a parallel
/// pre-processing step for testing/comparison.
/// </summary>
public class LocalGeminiReconciler
{
    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly ILogger _logger;
    private const int TimeoutMs = 3000; // Must be fast for voice flow

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string RECONCILE_PROMPT = """
        You are an expert UK address reconciliation system for a taxi booking service.

        You are given a 3-part "transcript window" from a live phone call:
        1. ADA_QUESTION: What the AI assistant asked the caller
        2. USER_RAW_SPEECH: The caller's raw speech-to-text response (may contain mishearings)
        3. ADA_READBACK: The AI assistant's interpretation/readback of the address

        Your job: Reconcile these three signals into the single most accurate address.

        RULES:
        - The USER_RAW_SPEECH is the ground truth for house numbers — preserve them EXACTLY (e.g., "52A" stays "52A")
        - ADA_READBACK may have corrected STT mishearings (e.g., "Colesie Grove" → "Cosy Club")
        - If USER and ADA disagree on a house number, ALWAYS trust the USER
        - If USER and ADA disagree on a street/POI name, prefer ADA's interpretation (it likely corrected a mishearing)
        - Resolve to a REAL address with coordinates in the specified city
        - For POIs (pubs, stations, hospitals), include the business name + street address
        - Provide a confidence score (0.0-1.0) based on how certain you are

        PRIORITY FOR RESOLUTION:
        1. House numbers → from USER_RAW_SPEECH (verbatim)
        2. Street/POI names → reconcile USER + ADA (prefer whichever matches a real place)
        3. City/area → from CURRENT_CITY context or explicit mentions
        4. Coordinates → must be accurate to the resolved address
        """;

    public LocalGeminiReconciler(
        string supabaseUrl,
        string serviceRoleKey,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        _supabaseUrl = supabaseUrl.TrimEnd('/');
        _serviceRoleKey = serviceRoleKey;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Reconcile a 3-way transcript window into a geocoded address.
    /// Returns null on failure (caller should fall back to raw geocoding).
    /// </summary>
    public async Task<ReconciledLocationResult?> ReconcileAsync(
        ReconcileContextRequest request,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            _logger.LogInformation(
                "[Reconciler] 3-way reconcile ({Context}): question=\"{Q}\", raw=\"{Raw}\", readback=\"{Rb}\", city={City}",
                request.Context, request.AdaQuestion ?? "none",
                request.UserRawSpeech, request.AdaReadback ?? "none",
                request.CurrentCity ?? "unknown");

            var userMessage = $"""
                CONTEXT: {request.Context} address
                CURRENT_CITY: {request.CurrentCity ?? "unknown"}
                CALLER_PHONE: {request.CallerPhone ?? "not provided"}
                
                ADA_QUESTION: "{request.AdaQuestion ?? "What is your address?"}"
                USER_RAW_SPEECH: "{request.UserRawSpeech}"
                ADA_READBACK: "{request.AdaReadback ?? "not available"}"
                """;

            var payload = new
            {
                model = "google/gemini-2.5-flash",
                messages = new[]
                {
                    new { role = "system", content = RECONCILE_PROMPT },
                    new { role = "user", content = userMessage }
                },
                temperature = 0.1,
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "reconcile_address",
                            description = "Return the reconciled address with coordinates and confidence",
                            parameters = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["reconciled_address"] = new { type = "string", description = "Full reconciled address" },
                                    ["lat"] = new { type = "number" },
                                    ["lon"] = new { type = "number" },
                                    ["confidence"] = new { type = "number", description = "0.0-1.0 confidence score" },
                                    ["is_poi"] = new { type = "boolean", description = "True if this is a named place/business" },
                                    ["name"] = new { type = "string", description = "POI/business name if applicable" },
                                    ["street_name"] = new { type = "string" },
                                    ["street_number"] = new { type = "string" },
                                    ["postal_code"] = new { type = "string" },
                                    ["city"] = new { type = "string" },
                                    ["area"] = new { type = "string", description = "District/neighbourhood" },
                                    ["is_ambiguous"] = new { type = "boolean" },
                                    ["alternatives"] = new { type = "array", items = new { type = "string" } },
                                    ["clarification_message"] = new { type = "string" }
                                },
                                required = new[] { "reconciled_address", "lat", "lon", "confidence", "is_poi", "is_ambiguous" },
                                additionalProperties = false
                            }
                        }
                    }
                },
                tool_choice = new { type = "function", function = new { name = "reconcile_address" } }
            };

            // Call Lovable AI gateway via edge function proxy or directly
            var url = "https://ai.gateway.lovable.dev/v1/chat/completions";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("Authorization", $"Bearer {_serviceRoleKey}");
            httpRequest.Content = JsonContent.Create(payload, options: JsonOpts);

            var response = await _httpClient.SendAsync(httpRequest, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Reconciler] Gateway returned {Status}: {Body}",
                    response.StatusCode, responseBody);
                return null;
            }

            // Parse tool call response
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var toolCall = root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("tool_calls")[0]
                .GetProperty("function")
                .GetProperty("arguments");

            var argsJson = toolCall.GetString();
            if (string.IsNullOrEmpty(argsJson))
            {
                _logger.LogWarning("[Reconciler] Empty tool call arguments");
                return null;
            }

            using var argsDoc = JsonDocument.Parse(argsJson);
            var args = argsDoc.RootElement;

            var result = new ReconciledLocationResult
            {
                ReconciledAddress = args.GetProperty("reconciled_address").GetString() ?? "",
                Lat = args.GetProperty("lat").GetDouble(),
                Lon = args.GetProperty("lon").GetDouble(),
                Confidence = args.GetProperty("confidence").GetDouble(),
                IsPOI = args.GetProperty("is_poi").GetBoolean(),
                IsAmbiguous = args.GetProperty("is_ambiguous").GetBoolean(),
                Components = new AddressComponents
                {
                    Name = args.TryGetProperty("name", out var n) ? n.GetString() : null,
                    StreetName = args.TryGetProperty("street_name", out var sn) ? sn.GetString() : null,
                    StreetNumber = args.TryGetProperty("street_number", out var snm) ? snm.GetString() : null,
                    PostalCode = args.TryGetProperty("postal_code", out var pc) ? pc.GetString() : null,
                    City = args.TryGetProperty("city", out var c) ? c.GetString() : null,
                    Area = args.TryGetProperty("area", out var a) ? a.GetString() : null
                }
            };

            if (args.TryGetProperty("alternatives", out var altProp) &&
                altProp.ValueKind == JsonValueKind.Array)
            {
                result.Alternatives = new List<string>();
                foreach (var alt in altProp.EnumerateArray())
                {
                    var s = alt.GetString();
                    if (s != null) result.Alternatives.Add(s);
                }
            }

            if (args.TryGetProperty("clarification_message", out var cm))
                result.ClarificationMessage = cm.GetString();

            _logger.LogInformation(
                "[Reconciler] ✅ Reconciled: \"{Addr}\" (conf={Conf:F2}, POI={IsPOI}, ambig={Ambig})",
                result.ReconciledAddress, result.Confidence, result.IsPOI, result.IsAmbiguous);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Reconciler] Timed out ({Ms}ms)", TimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Reconciler] Reconciliation failed");
            return null;
        }
    }
}
