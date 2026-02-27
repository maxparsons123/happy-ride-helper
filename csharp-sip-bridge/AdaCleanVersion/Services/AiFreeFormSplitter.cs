using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// AI-powered freeform utterance splitter — takes messy caller speech and
/// "explodes" it into clean booking slot JSON using a fast LLM.
/// 
/// Uses burst detection heuristics to avoid unnecessary AI calls on simple inputs.
/// Falls back gracefully (returns null) on timeout/error so the caller can
/// skip AI splitting and handle the input slot-by-slot.
/// 
/// Examples:
///   "It's Max at 52A David Road going to the station for 4"
///     → { Name: "Max", Pickup: "52A David Road", Destination: "the station", Passengers: 4 }
///   "7 Russell Street with three passengers"
///     → { Destination: null, Pickup: null, ... depends on context }
///     → Actually: { Pickup: "7 Russell Street", Passengers: 3 } (AI figures it out)
/// </summary>
public class AiFreeFormSplitter
{
    private readonly HttpClient _httpClient;
    private readonly string _serviceRoleKey;
    private readonly ILogger _logger;
    private const int TimeoutMs = 2000; // Must be very fast for voice flow

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string SPLIT_PROMPT = """
        You are a data extraction engine for a taxi booking system.
        Extract fields from the user's speech.
        If a field is not mentioned, do NOT include it.

        FIELDS:
        - name: Person's name (from "it's Max", "my name is John", "I'm Sarah", etc.)
        - pickup: Full pickup address or landmark (house number + street, or POI name)
        - destination: Full destination address or landmark
        - passengers: Integer count (extract from "for 4", "three people", "with 2 passengers", "just me" = 1)
        - pickup_time: "ASAP" or time expression (extract from "now", "straight away", "in 10 minutes", "at 3:30")

        RULES:
        - Treat "ASAP", "now", "straight away", "right away", "immediately" as "ASAP"
        - Keep house numbers EXACTLY as stated (e.g., "52A" stays "52A", never "52-84" or "528")
        - Convert spoken number words to digits for passengers (e.g., "three" → 3)
        - "just me" or "myself" = 1 passenger
        - Do NOT guess or infer data that isn't explicitly stated
        - If the sentence has "from X to Y" or "X going to Y", X = pickup, Y = destination
        - If only one address is given with no directional keyword, use context:
          * With "pick up from" / "at" / "from" → it's pickup
          * With "going to" / "heading to" / "drop off at" → it's destination
          * Ambiguous single address → set as pickup (most common case)
        """;

    // ── Burst Detection Heuristics ──
    // Only call the AI if the transcript looks "dense" enough to contain multiple fields.
    private static readonly Regex BurstKeywords = new(
        @"\b(?:to|going|heading|from|picking?\s*up|drop(?:ping)?\s*(?:off)?|for|with|passenger|people|person|asap|now|straight\s+away)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MinBurstLength = 25; // Short phrases rarely contain multiple fields

    public AiFreeFormSplitter(
        string serviceRoleKey,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        _serviceRoleKey = serviceRoleKey;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Result of AI-based splitting. Null fields mean "not present in utterance".
    /// </summary>
    public class AiSplitResult
    {
        public string? Name { get; set; }
        public string? Pickup { get; set; }
        public string? Destination { get; set; }
        public int? Passengers { get; set; }
        public string? PickupTime { get; set; }

        /// <summary>Count of non-null fields extracted.</summary>
        public int FilledCount
        {
            get
            {
                int count = 0;
                if (Name != null) count++;
                if (Pickup != null) count++;
                if (Destination != null) count++;
                if (Passengers.HasValue) count++;
                if (PickupTime != null) count++;
                return count;
            }
        }

        /// <summary>True if more than one field was extracted (i.e., it's a genuine burst).</summary>
        public bool IsBurst => FilledCount > 1;
    }

    /// <summary>
    /// Quick heuristic check: does this transcript look like it might contain multiple booking fields?
    /// Avoids expensive AI calls for simple inputs like "yes" or "52A David Road".
    /// </summary>
    public static bool LooksLikeBurst(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        var trimmed = transcript.Trim();

        // Long utterances are more likely to be bursts
        if (trimmed.Length >= MinBurstLength) return true;

        // Check for burst keywords (directional/quantity markers)
        var matches = BurstKeywords.Matches(trimmed);
        return matches.Count >= 2; // Need at least 2 keyword signals
    }

    /// <summary>
    /// Analyze a transcript using AI. Returns null on failure (caller should fall back to regex).
    /// Call LooksLikeBurst() first to avoid unnecessary AI calls.
    /// </summary>
    public async Task<AiSplitResult?> SplitAsync(string transcript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            _logger.LogInformation("[AiSplitter] Splitting: \"{T}\"", transcript.Trim());

            var payload = new
            {
                model = "google/gemini-2.5-flash-lite",
                messages = new[]
                {
                    new { role = "system", content = SPLIT_PROMPT },
                    new { role = "user", content = transcript.Trim() }
                },
                temperature = 0.0,
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "extract_booking",
                            description = "Extract booking fields from caller speech",
                            parameters = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["name"] = new { type = "string", description = "Caller's name" },
                                    ["pickup"] = new { type = "string", description = "Pickup address or landmark" },
                                    ["destination"] = new { type = "string", description = "Destination address or landmark" },
                                    ["passengers"] = new { type = "integer", description = "Number of passengers" },
                                    ["pickup_time"] = new { type = "string", description = "ASAP or time expression" }
                                },
                                required = System.Array.Empty<string>(),
                                additionalProperties = false
                            }
                        }
                    }
                },
                tool_choice = new { type = "function", function = new { name = "extract_booking" } }
            };

            var url = "https://ai.gateway.lovable.dev/v1/chat/completions";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("Authorization", $"Bearer {_serviceRoleKey}");
            httpRequest.Content = JsonContent.Create(payload, options: JsonOpts);

            var response = await _httpClient.SendAsync(httpRequest, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AiSplitter] Gateway returned {Status}: {Body}",
                    response.StatusCode, responseBody);
                return null;
            }

            // Parse tool call response
            using var doc = JsonDocument.Parse(responseBody);
            var toolCall = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("tool_calls")[0]
                .GetProperty("function")
                .GetProperty("arguments");

            var argsJson = toolCall.GetString();
            if (string.IsNullOrEmpty(argsJson))
            {
                _logger.LogWarning("[AiSplitter] Empty tool call arguments");
                return null;
            }

            using var argsDoc = JsonDocument.Parse(argsJson);
            var args = argsDoc.RootElement;

            var result = new AiSplitResult
            {
                Name = TryGetNonEmpty(args, "name"),
                Pickup = TryGetNonEmpty(args, "pickup"),
                Destination = TryGetNonEmpty(args, "destination"),
                Passengers = TryGetInt(args, "passengers"),
                PickupTime = TryGetNonEmpty(args, "pickup_time")
            };

            _logger.LogInformation(
                "[AiSplitter] ✅ Extracted {Count} fields: name={N}, pickup={P}, dest={D}, pax={Pax}, time={T}",
                result.FilledCount,
                result.Name ?? "–", result.Pickup ?? "–", result.Destination ?? "–",
                result.Passengers?.ToString() ?? "–", result.PickupTime ?? "–");

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[AiSplitter] Timed out ({Ms}ms)", TimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AiSplitter] Split failed");
            return null;
        }
    }

    private static string? TryGetNonEmpty(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Null) return null;
        var s = val.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int? TryGetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Null) return null;
        if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
        // Handle string numbers (e.g., "3")
        if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var n)) return n;
        return null;
    }
}
