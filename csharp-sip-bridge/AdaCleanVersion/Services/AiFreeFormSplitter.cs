using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// AI-powered utterance splitter — uses a fast Gemini model via the Lovable AI gateway
/// to decompose compound caller utterances into structured slot JSON.
/// 
/// Falls back gracefully (returns null) on timeout/error so the caller can
/// use the deterministic regex-based FreeFormSplitter as a safety net.
/// 
/// Example:
///   Input:  "7 Russell Street with three passengers"  (currentSlot: "destination")
///   Output: { destination: "7 Russell Street", passengers: "3" }
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
        You are a taxi booking utterance parser. You receive a caller's raw speech and the slot 
        the system is currently collecting. Your job is to split the utterance into the correct 
        booking fields.

        BOOKING FIELDS:
        - name: Caller's name
        - pickup: Pickup address (house number + street, or POI name)
        - destination: Destination address (house number + street, or POI name)  
        - passengers: Number of passengers (as a digit string, e.g. "3")
        - pickup_time: When they want pickup (e.g. "ASAP", "in 10 minutes", "3:30pm")

        RULES:
        - Extract ONLY fields that are explicitly stated in the utterance
        - Preserve house numbers EXACTLY as spoken (e.g., "52A" stays "52A")
        - Convert spoken numbers to digits for passengers (e.g., "three" → "3")
        - If the utterance contains ONLY data for the current slot, put it all in that slot
        - If trailing info belongs to another slot, split it out
        - Keywords: "with N passengers/people", "for N", "going to", "heading to", "from"
        - Do NOT hallucinate fields that aren't in the utterance
        - The primary_slot field should contain the value for the current_slot being collected
        """;

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
        public string? Passengers { get; set; }
        public string? PickupTime { get; set; }

        /// <summary>True if more than one field was extracted.</summary>
        public bool HasOverflow
        {
            get
            {
                int count = 0;
                if (Name != null) count++;
                if (Pickup != null) count++;
                if (Destination != null) count++;
                if (Passengers != null) count++;
                if (PickupTime != null) count++;
                return count > 1;
            }
        }

        /// <summary>Get the value for a specific slot, or null.</summary>
        public string? GetSlot(string slotName) => slotName switch
        {
            "name" => Name,
            "pickup" => Pickup,
            "destination" => Destination,
            "passengers" => Passengers,
            "pickup_time" => PickupTime,
            _ => null
        };

        /// <summary>Returns all non-null slots as a dictionary, excluding the given primary slot.</summary>
        public Dictionary<string, string> GetOverflowSlots(string primarySlot)
        {
            var overflow = new Dictionary<string, string>();
            if (Name != null && primarySlot != "name") overflow["name"] = Name;
            if (Pickup != null && primarySlot != "pickup") overflow["pickup"] = Pickup;
            if (Destination != null && primarySlot != "destination") overflow["destination"] = Destination;
            if (Passengers != null && primarySlot != "passengers") overflow["passengers"] = Passengers;
            if (PickupTime != null && primarySlot != "pickup_time") overflow["pickup_time"] = PickupTime;
            return overflow;
        }
    }

    /// <summary>
    /// Analyze a transcript using AI. Returns null on failure (caller should fall back to regex).
    /// </summary>
    public async Task<AiSplitResult?> AnalyzeAsync(
        string transcript, string currentSlot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            _logger.LogInformation(
                "[AiSplitter] Analyzing: slot={Slot}, transcript=\"{T}\"",
                currentSlot, transcript);

            var userMessage = $"""
                CURRENT_SLOT: {currentSlot}
                TRANSCRIPT: "{transcript.Trim()}"
                """;

            var payload = new
            {
                model = "google/gemini-2.5-flash-lite",
                messages = new[]
                {
                    new { role = "system", content = SPLIT_PROMPT },
                    new { role = "user", content = userMessage }
                },
                temperature = 0.0,
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "split_utterance",
                            description = "Split a caller utterance into booking slot fields",
                            parameters = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["name"] = new { type = "string", description = "Caller name if stated" },
                                    ["pickup"] = new { type = "string", description = "Pickup address if stated" },
                                    ["destination"] = new { type = "string", description = "Destination address if stated" },
                                    ["passengers"] = new { type = "string", description = "Passenger count as digit string (e.g. '3')" },
                                    ["pickup_time"] = new { type = "string", description = "Pickup time if stated (e.g. 'ASAP', '3:30pm')" }
                                },
                                required = System.Array.Empty<string>(),
                                additionalProperties = false
                            }
                        }
                    }
                },
                tool_choice = new { type = "function", function = new { name = "split_utterance" } }
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
                Passengers = TryGetNonEmpty(args, "passengers"),
                PickupTime = TryGetNonEmpty(args, "pickup_time")
            };

            _logger.LogInformation(
                "[AiSplitter] ✅ Split: name={N}, pickup={P}, dest={D}, pax={Pax}, time={T}",
                result.Name ?? "–", result.Pickup ?? "–", result.Destination ?? "–",
                result.Passengers ?? "–", result.PickupTime ?? "–");

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[AiSplitter] Timed out ({Ms}ms)", TimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AiSplitter] Analysis failed");
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
}
