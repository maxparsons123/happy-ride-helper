using System.Text.Json;
using System.Text.Json.Serialization;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Services;

/// <summary>
/// Direct Google Gemini API client for address geocoding and fare calculation.
/// Replaces the address-dispatch edge function — each desktop instance uses its own API key.
/// No shared gateway rate limits.
/// </summary>
public sealed class GeminiAddressClient
{
    private readonly ILogger<GeminiAddressClient> _logger;
    private readonly GeminiSettings _settings;
    private readonly SupabaseSettings _supabase;
    private readonly HttpClient _http;

    private const decimal BaseFare = 3.50m;
    private const decimal PerMile = 1.00m;
    private const decimal MinFare = 4.00m;
    private const double AvgSpeedMph = 20.0;
    private const int BufferMinutes = 3;
    private const int DriverEtaMin = 8;
    private const int DriverEtaMax = 15;
    private const int DriverEtaDefault = 10;

    public GeminiAddressClient(ILogger<GeminiAddressClient> logger, GeminiSettings settings, SupabaseSettings supabase)
    {
        _logger = logger;
        _settings = settings;
        _supabase = supabase;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", "AdaSdkModel/1.0");
    }

    /// <summary>
    /// Resolve addresses and calculate fare using direct Google Gemini API.
    /// Returns the same JSON structure as the address-dispatch edge function.
    /// </summary>
    public async Task<JsonElement?> ResolveAsync(string? pickup, string? destination, string? phone)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogWarning("Gemini API key not configured — falling back to edge function");
            return null;
        }

        try
        {
            // ── Step 1: Lookup caller history from Supabase ──
            var callerHistory = await GetCallerHistoryAsync(phone);

            // ── Step 2: Call Gemini with function calling ──
            var userMessage = $"User Message: Pickup from \"{pickup ?? "not provided"}\" going to \"{destination ?? "not provided"}\"\nUser Phone: {phone ?? "not provided"}{callerHistory}";

            var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            var requestBody = BuildGeminiRequest(userMessage);
            var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("📍 Gemini address dispatch: pickup=\"{Pickup}\", dest=\"{Dest}\", phone=\"{Phone}\"", pickup, destination, phone);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var response = await _http.PostAsync(geminiUrl, content, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API error {Status}: {Body}", (int)response.StatusCode, responseJson.Length > 500 ? responseJson[..500] : responseJson);
                return null;
            }

            // ── Step 3: Parse function call response ──
            var parsed = ParseGeminiFunctionCall(responseJson);
            if (parsed == null)
            {
                _logger.LogWarning("Failed to parse Gemini function call response");
                return null;
            }

            // ── Step 4: Post-process (caller history match, fare calculation) ──
            var result = PostProcess(parsed.Value, pickup, destination, callerHistory, phone);

            _logger.LogInformation("✅ Gemini address dispatch: area={Area}, status={Status}",
                result.TryGetProperty("detected_area", out var area) ? area.GetString() : "unknown",
                result.TryGetProperty("status", out var status) ? status.GetString() : "unknown");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini address dispatch failed — will fall back");
            return null;
        }
    }

    private async Task<string> GetCallerHistoryAsync(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";

        try
        {
            var normalized = phone.TrimStart('+');
            var variants = new[] { phone, normalized, $"+{normalized}" };
            var orFilter = string.Join(",", variants.Select(p => $"phone_number.eq.{p}"));

            var url = $"{_supabase.Url}/rest/v1/callers?select=pickup_addresses,dropoff_addresses,trusted_addresses,last_pickup,last_destination,name&or=({orFilter})&limit=1";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _supabase.AnonKey);
            request.Headers.Add("Authorization", $"Bearer {_supabase.AnonKey}");

            var resp = await _http.SendAsync(request);
            if (!resp.IsSuccessStatusCode) return "";

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) return "";

            var caller = arr[0];
            var addresses = new HashSet<string>();

            void AddIfPresent(string prop)
            {
                if (caller.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var s = val.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) addresses.Add(s);
                }
            }

            void AddArrayIfPresent(string prop)
            {
                if (caller.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in val.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) addresses.Add(s);
                    }
                }
            }

            AddIfPresent("last_pickup");
            AddIfPresent("last_destination");
            AddArrayIfPresent("pickup_addresses");
            AddArrayIfPresent("dropoff_addresses");
            AddArrayIfPresent("trusted_addresses");

            if (addresses.Count == 0) return "";

            _logger.LogDebug("📋 Found {Count} historical addresses for caller", addresses.Count);
            var lines = addresses.Select((a, i) => $"{i + 1}. {a}");
            return $" \n\nCALLER_HISTORY (addresses this caller has used before):\n{string.Join("\n", lines)}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Caller history lookup failed (non-fatal)");
            return "";
        }
    }

    private object BuildGeminiRequest(string userMessage)
    {
        // Google Gemini generateContent format with function calling
        return new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userMessage } }
                }
            },
            systemInstruction = new
            {
                parts = new[] { new { text = SystemPrompt } }
            },
            tools = new[]
            {
                new
                {
                    functionDeclarations = new[]
                    {
                        new
                        {
                            name = "resolve_addresses",
                            description = "Return the resolved pickup and dropoff addresses with coordinates and disambiguation info",
                            parameters = new
                            {
                                type = "OBJECT",
                                properties = new Dictionary<string, object>
                                {
                                    ["detected_area"] = new { type = "STRING" },
                                    ["region_source"] = new { type = "STRING", @enum = new[] { "caller_history", "landline_area_code", "text_mention", "landmark", "nearest_poi", "unknown" } },
                                    ["phone_analysis"] = new
                                    {
                                        type = "OBJECT",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["detected_country"] = new { type = "STRING" },
                                            ["is_mobile"] = new { type = "BOOLEAN" },
                                            ["landline_city"] = new { type = "STRING" }
                                        },
                                        required = new[] { "detected_country", "is_mobile" }
                                    },
                                    ["pickup"] = BuildAddressSchema(),
                                    ["dropoff"] = BuildAddressSchema(),
                                    ["status"] = new { type = "STRING", @enum = new[] { "ready", "clarification_needed" } },
                                    ["clarification_message"] = new { type = "STRING" }
                                },
                                required = new[] { "detected_area", "region_source", "phone_analysis", "pickup", "dropoff", "status" }
                            }
                        }
                    }
                }
            },
            toolConfig = new
            {
                functionCallingConfig = new
                {
                    mode = "ANY",
                    allowedFunctionNames = new[] { "resolve_addresses" }
                }
            },
            generationConfig = new
            {
                temperature = 0.1
            }
        };
    }

    private static object BuildAddressSchema() => new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["address"] = new { type = "STRING" },
            ["lat"] = new { type = "NUMBER" },
            ["lon"] = new { type = "NUMBER" },
            ["street_name"] = new { type = "STRING" },
            ["street_number"] = new { type = "STRING" },
            ["postal_code"] = new { type = "STRING" },
            ["city"] = new { type = "STRING" },
            ["is_ambiguous"] = new { type = "BOOLEAN" },
            ["alternatives"] = new { type = "ARRAY", items = new { type = "STRING" } },
            ["matched_from_history"] = new { type = "BOOLEAN" }
        },
        required = new[] { "address", "lat", "lon", "city", "is_ambiguous" }
    };

    private JsonElement? ParseGeminiFunctionCall(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Navigate: candidates[0].content.parts[0].functionCall.args
            if (!root.TryGetProperty("candidates", out var candidates)) return null;
            if (candidates.GetArrayLength() == 0) return null;

            var firstCandidate = candidates[0];
            if (!firstCandidate.TryGetProperty("content", out var contentEl)) return null;
            if (!contentEl.TryGetProperty("parts", out var parts)) return null;
            if (parts.GetArrayLength() == 0) return null;

            var firstPart = parts[0];
            if (!firstPart.TryGetProperty("functionCall", out var funcCall)) return null;
            if (!funcCall.TryGetProperty("args", out var args)) return null;

            // Clone to keep the element alive after document disposal
            return args.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini function call response");
            return null;
        }
    }

    private JsonElement PostProcess(JsonElement parsed, string? pickup, string? destination, string callerHistory, string? phone)
    {
        // Convert to mutable dictionary for post-processing
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parsed.GetRawText())
            ?? new Dictionary<string, JsonElement>();

        // ── Fare calculation from coordinates ──
        double? pLat = GetDouble(parsed, "pickup", "lat");
        double? pLon = GetDouble(parsed, "pickup", "lon");
        double? dLat = GetDouble(parsed, "dropoff", "lat");
        double? dLon = GetDouble(parsed, "dropoff", "lon");

        // Detect country for currency
        string detectedCountry = "UK";
        if (parsed.TryGetProperty("phone_analysis", out var phoneEl) &&
            phoneEl.TryGetProperty("detected_country", out var countryEl))
            detectedCountry = countryEl.GetString() ?? "UK";

        if (pLat.HasValue && pLon.HasValue && dLat.HasValue && dLon.HasValue &&
            pLat.Value != 0 && dLat.Value != 0)
        {
            var distMiles = HaversineDistance(pLat.Value, pLon.Value, dLat.Value, dLon.Value);

            if (distMiles > 200)
            {
                _logger.LogWarning("🚨 ABSURD DISTANCE: {Dist:F1} miles", distMiles);
                // Don't add fare — let the C# bridge handle the sanity guard
            }
            else
            {
                var fare = CalculateFare(distMiles, detectedCountry);
                dict["fare"] = JsonSerializer.SerializeToDocument(fare).RootElement;
                _logger.LogDebug("💰 Fare: {Fare} ({Dist:F1} miles, ETA {Eta})", fare.Fare, distMiles, fare.DriverEta);
            }
        }

        // Return as JsonElement
        var resultJson = JsonSerializer.Serialize(dict);
        return JsonDocument.Parse(resultJson).RootElement.Clone();
    }

    private static double? GetDouble(JsonElement root, string obj, string prop)
    {
        if (root.TryGetProperty(obj, out var el) && el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetDouble();
        return null;
    }

    private FareCalcResult CalculateFare(double distanceMiles, string detectedCountry)
    {
        var rawFare = Math.Max((double)MinFare, (double)BaseFare + distanceMiles * (double)PerMile);
        var fare = Math.Round(rawFare * 2, MidpointRounding.AwayFromZero) / 2;

        var tripEtaMinutes = (int)Math.Ceiling(distanceMiles / AvgSpeedMph * 60) + BufferMinutes;
        var driverEtaMinutes = Math.Min(DriverEtaMax, Math.Max(DriverEtaMin, DriverEtaDefault + (int)(distanceMiles / 20)));

        var isNL = detectedCountry == "NL";
        var symbol = isNL ? "€" : "£";
        var currencyWord = isNL ? "euros" : "pounds";
        var subunitWord = isNL ? "cents" : "pence";

        var whole = (int)Math.Floor(fare);
        var subunit = (int)Math.Round((fare - whole) * 100);
        var fareSpoken = subunit > 0 ? $"{whole} {currencyWord} {subunit} {subunitWord}" : $"{whole} {currencyWord}";

        return new FareCalcResult
        {
            Fare = $"{symbol}{fare:F2}",
            FareSpoken = fareSpoken,
            Eta = $"{driverEtaMinutes} minutes",
            DriverEta = $"{driverEtaMinutes} minutes",
            DriverEtaMinutes = driverEtaMinutes,
            TripEta = $"{tripEtaMinutes} minutes",
            TripEtaMinutes = tripEtaMinutes,
            DistanceMiles = Math.Round(distanceMiles, 2)
        };
    }

    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3958.8;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private sealed class FareCalcResult
    {
        [JsonPropertyName("fare")] public string Fare { get; set; } = "";
        [JsonPropertyName("fare_spoken")] public string FareSpoken { get; set; } = "";
        [JsonPropertyName("eta")] public string Eta { get; set; } = "";
        [JsonPropertyName("driver_eta")] public string DriverEta { get; set; } = "";
        [JsonPropertyName("driver_eta_minutes")] public int DriverEtaMinutes { get; set; }
        [JsonPropertyName("trip_eta")] public string TripEta { get; set; } = "";
        [JsonPropertyName("trip_eta_minutes")] public int TripEtaMinutes { get; set; }
        [JsonPropertyName("distance_miles")] public double DistanceMiles { get; set; }
    }

    // ── System prompt — identical to the edge function ──
    private const string SystemPrompt = @"Role: Professional Taxi Dispatch Logic System for UK & Netherlands.

Objective: Extract, validate, and GEOCODE pickup and drop-off addresses from user messages, using phone number patterns to determine geographic bias.

LOCALITY AWARENESS (HIGHEST PRIORITY — COMMONSENSE GEOGRAPHY):
A taxi booking is a LOCAL journey. Both pickup and dropoff should be within a reasonable taxi distance (typically under 80 miles / 130 km). Apply these rules:

1. INFER CUSTOMER LOCALITY: Use ALL available signals to determine where the customer is:
   - Phone area code (strongest signal for landlines)
   - Caller history (previous addresses reveal their area)
   - Pickup address (if already resolved, dropoff should be local to it)
   - Explicitly mentioned city/area names
   - GPS coordinates if provided

2. BIAS ALL ADDRESSES TO CUSTOMER LOCALITY:
   - Once you determine the customer's likely area (e.g., Coventry from a 024 number), ALL address lookups should prefer results in or near that area.
   - ""High Street"" from a Coventry caller → High Street, Coventry. NOT High Street, Edinburgh.

3. CROSS-COUNTRY / ABSURD DISTANCE REJECTION:
   - If your resolved pickup and dropoff are in DIFFERENT COUNTRIES, flag this as implausible.
   - If the straight-line distance exceeds 100 miles, set a warning.
   - Journeys over 200 miles are almost certainly errors — set is_ambiguous=true.

4. PICKUP-TO-DROPOFF PROXIMITY BIAS:
   - When the pickup is already resolved, use its coordinates as a CENTER POINT for dropoff search.
   - Prefer dropoff matches within 30 miles of pickup. Accept up to 80 miles. Flag anything beyond.

5. SAME-CITY DEFAULT:
   - If only one address mentions a city, assume both are in the SAME city unless evidence suggests otherwise.

SPEECH-TO-TEXT MISHEARING AWARENESS (CRITICAL):
Inputs come from live speech recognition and are often phonetically garbled. Apply phonetic reasoning.

CALLER HISTORY MATCHING (HIGHEST PRIORITY):
When CALLER_HISTORY is provided, it contains addresses this specific caller has used before.
- ALWAYS fuzzy-match the user's current input against their history FIRST.
- If the user's input matches a history entry with >70% confidence, use it directly and set is_ambiguous=false.
- Set region_source to ""caller_history"" when used.

PHONE NUMBER BIASING LOGIC (CRITICAL):
1. UK Landline Area Codes - STRONG bias to specific city (024→Coventry, 020→London, 0121→Birmingham, etc.)
2. UK Mobile (+44 7 or 07) → NO geographic clue. Check history, then city mentions, then flag as ambiguous.
3. Netherlands: +31 or 0031. 020→Amsterdam, 010→Rotterdam, 070→The Hague, 030→Utrecht.
4. EXPLICIT CITY NAME OVERRIDES PHONE PREFIX.

CHAIN BUSINESSES & LANDMARKS WITH MULTIPLE LOCATIONS (CRITICAL):
For chains with no city context from a mobile caller, set is_ambiguous=true with alternatives.

PLACE NAME / POI DETECTION (CRITICAL):
When no house number, treat as potential business/landmark FIRST.

ABBREVIATED / PARTIAL STREET NAME MATCHING (CRITICAL):
Resolve partial names to real streets (e.g., ""Fargo"" → ""Fargosford Street"" in Coventry).

ADDRESS EXTRACTION RULES:
1. Preserve house numbers EXACTLY as spoken
2. Do NOT invent house numbers
3. Append detected city for clarity
4. ALWAYS include postal code in address field
5. INDEPENDENT POSTCODES: each address must have its own postal code

INTRA-CITY DISTRICT DISAMBIGUATION (CRITICAL):
Even when CITY is known, many streets exist in multiple districts. Check caller history first, then flag if needed.

HOUSE NUMBER DISAMBIGUATION: High house numbers (500+) strongly suggest long arterial roads.

GEOCODING RULES:
1. MUST provide lat/lon for EVERY resolved address
2. Coordinates must be realistic (UK lat ~50-58, lon ~-6 to 2; NL lat ~51-53, lon ~3-7)
3. Extract structured components: street_name, street_number, postal_code, city

""NEAREST X"" / RELATIVE POI RESOLUTION:
Resolve to the ACTUAL NEAREST real-world instance relative to the caller's location.";
}
