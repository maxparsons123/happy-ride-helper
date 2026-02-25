// Last updated: 2026-02-21 (v2.8)
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
    public async Task<JsonElement?> ResolveAsync(
        string? pickup,
        string? destination,
        string? phone,
        string? pickupTime = null,
        string? spokenPickupNumber = null,
        string? spokenDestNumber = null)
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
            var timePart = !string.IsNullOrWhiteSpace(pickupTime) ? $"\nPickup Time Requested: \"{pickupTime}\"\nREFERENCE_DATETIME (current UTC): {DateTime.UtcNow:o}" : "";

            // House numbers extracted from caller's speech via AddressParser — used as geocoding filters.
            // Gemini must resolve to a location that actually has this number on the street,
            // not just the midpoint or any random point on the road.
            var houseNumberHints = "";
            if (!string.IsNullOrWhiteSpace(spokenPickupNumber))
                houseNumberHints += $"\nPICKUP HOUSE NUMBER (extracted from caller's speech): \"{spokenPickupNumber}\" — use this as a GEOCODING FILTER. Only resolve to addresses on that street that actually contain house number {spokenPickupNumber}. Set street_number to exactly \"{spokenPickupNumber}\". If the street has no such number, flag is_ambiguous=true.";
            if (!string.IsNullOrWhiteSpace(spokenDestNumber))
                houseNumberHints += $"\nDESTINATION HOUSE NUMBER (extracted from caller's speech): \"{spokenDestNumber}\" — use this as a GEOCODING FILTER. Only resolve to addresses on that street that actually contain house number {spokenDestNumber}. Set street_number to exactly \"{spokenDestNumber}\". If the street has no such number, flag is_ambiguous=true.";

            var userMessage = $"User Message: Pickup from \"{pickup ?? "not provided"}\" going to \"{destination ?? "not provided"}\"\nUser Phone: {phone ?? "not provided"}{timePart}{houseNumberHints}{callerHistory}";

            var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            var requestBody = BuildGeminiRequest(userMessage);
            var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("📍 Gemini address dispatch: pickup=\"{Pickup}\", dest=\"{Dest}\", phone=\"{Phone}\"", pickup, destination, phone);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
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

            // ── Step 4: Full post-processing (matching edge function) ──
            var result = await PostProcessAsync(parsed.Value, pickup, destination, callerHistory, phone, spokenPickupNumber, spokenDestNumber);

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
                                    ["clarification_message"] = new { type = "STRING" },
                                    ["scheduled_at"] = new { type = "STRING", description = "ISO 8601 UTC datetime for scheduled pickup, or null if ASAP" }
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
            ["districts_found"] = new
            {
                type = "ARRAY",
                items = new { type = "STRING" },
                description = "If this street exists in multiple districts of the city, list each distinct district/area name here (e.g. [\"Moseley\", \"Handsworth\", \"Yardley\"]). Leave empty if the street is unambiguous."
            },
            ["alternatives"] = new
            {
                type = "ARRAY",
                items = new { type = "STRING" },
                description = "Formatted full addresses for each district version found (e.g. [\"School Road, Moseley, Birmingham\", \"School Road, Handsworth, Birmingham\"])."
            },
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

    // Multi-district street detection is now handled dynamically by Gemini itself via the
    // districts_found field in BuildAddressSchema(). No hardcoded street lists needed.

    // ── In-memory cache: city → list of district/suburb names fetched from uk_locations ──
    // Areas are stored in the database (uk_locations, type='district'/'suburb') and can be
    // updated there without recompiling. Cache TTL = 60 min.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[] Areas, DateTime ExpiresAt)> _cityAreaCache = new();
    private static readonly TimeSpan _cityAreaCacheTtl = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Fetch distinct area/district names for a city from uk_locations.
    /// Results are cached 60 min. Returns empty array on failure (fail-open).
    /// </summary>
    private async Task<string[]> GetCityAreasAsync(string city)
    {
        if (_cityAreaCache.TryGetValue(city, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Areas;

        try
        {
            var encoded = Uri.EscapeDataString(city);
            var url = $"{_supabase.Url}/rest/v1/uk_locations?select=name&parent_city=ilike.{encoded}&type=in.(district,suburb,neighbourhood,area,ward)&order=name&limit=50";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("apikey", _supabase.AnonKey);
            req.Headers.Add("Authorization", $"Bearer {_supabase.AnonKey}");

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement;
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var areas = arr.EnumerateArray()
                        .Select(e => e.GetProperty("name").GetString() ?? "")
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToArray();
                    _cityAreaCache[city] = (areas, DateTime.UtcNow.Add(_cityAreaCacheTtl));
                    _logger.LogDebug("🗺️ Loaded {Count} districts for {City} from uk_locations", areas.Length, city);
                    return areas;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetCityAreasAsync failed for {City} (non-fatal)", city);
        }

        return Array.Empty<string>();
    }

    private static readonly string[] KnownCities = { "Coventry", "Birmingham", "London", "Manchester", "Leeds", "Sheffield", "Nottingham", "Leicester", "Bristol", "Reading", "Liverpool", "Newcastle", "Brighton", "Oxford", "Cambridge", "Wolverhampton", "Derby", "Stoke", "Southampton", "Portsmouth", "Edinburgh", "Glasgow", "Cardiff", "Belfast", "Warwick", "Kenilworth", "Solihull", "Sutton Coldfield", "Leamington", "Rugby", "Nuneaton", "Bedworth", "Stratford", "Redditch", "Amsterdam", "Rotterdam", "The Hague", "Utrecht", "Eindhoven", "Gent", "Ghent", "Brussels", "Antwerp", "Bruges" };

    /// <summary>Extract street key for fuzzy comparison: "52a david road" from "52A David Road, Coventry CV1 2BW"</summary>
    private static string ExtractStreetKey(string input) =>
        System.Text.RegularExpressions.Regex.Replace(input, @"\b[a-z]{1,2}\d{1,2}\s?\d[a-z]{2}\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        .Split(',')[0].Trim().ToLowerInvariant();

    private async Task<JsonElement> PostProcessAsync(JsonElement parsed, string? pickup, string? destination, string callerHistory, string? phone, string? spokenPickupNumber = null, string? spokenDestNumber = null)
    {
        // Convert to mutable JSON using a writable stream
        var mutable = JsonSerializer.Deserialize<Dictionary<string, object>>(parsed.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        // We'll work with raw JSON manipulation via a JsonNode approach for safety
        var jsonStr = parsed.GetRawText();
        using var workDoc = JsonDocument.Parse(jsonStr);
        var work = System.Text.Json.Nodes.JsonNode.Parse(jsonStr)!;

        // ── Caller history addresses for fuzzy matching ──
        var historyAddresses = new List<string>();
        if (!string.IsNullOrEmpty(callerHistory))
        {
            foreach (var line in callerHistory.Split('\n'))
            {
                var trimmed = line.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s"))
                {
                    var addr = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\d+\.\s*", "").Trim();
                    if (!string.IsNullOrEmpty(addr)) historyAddresses.Add(addr);
                }
            }
        }

        // ── Post-processing: clear disambiguation via caller history OR explicit city ──
        foreach (var side in new[] { "pickup", "dropoff" })
        {
            var addr = work[side];
            if (addr == null) continue;
            var isAmbiguous = addr["is_ambiguous"]?.GetValue<bool>() ?? false;
            if (!isAmbiguous) continue;

            var originalInput = side == "pickup" ? (pickup ?? "") : (destination ?? "");
            var inputLower = originalInput.ToLowerInvariant().Trim();

            // CHECK 1: Caller history fuzzy match
            if (historyAddresses.Count > 0)
            {
                var historyMatch = historyAddresses.FirstOrDefault(h =>
                {
                    var hLower = h.ToLowerInvariant().Trim();
                    return hLower.Contains(inputLower) || inputLower.Contains(hLower) ||
                           ExtractStreetKey(inputLower) == ExtractStreetKey(hLower);
                });

                if (historyMatch != null)
                {
                    _logger.LogInformation("✅ Caller history match: \"{Input}\" → \"{Match}\" — clearing disambiguation for {Side}", originalInput, historyMatch, side);
                    addr["is_ambiguous"] = false;
                    addr["alternatives"] = new System.Text.Json.Nodes.JsonArray();
                    addr["matched_from_history"] = true;
                    var addrVal = addr["address"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(addrVal) || addrVal.Length < historyMatch.Length)
                        addr["address"] = historyMatch;

                    var otherSide = side == "pickup" ? "dropoff" : "pickup";
                    var otherAmbig = work[otherSide]?["is_ambiguous"]?.GetValue<bool>() ?? false;
                    if (!otherAmbig)
                    {
                        work["status"] = "ready";
                        work["clarification_message"] = null;
                    }
                    continue;
                }
            }

            // CHECK 2: Explicit city in input
            var explicitCity = KnownCities.FirstOrDefault(c => inputLower.Contains(c.ToLowerInvariant()));
            if (explicitCity != null)
            {
                _logger.LogInformation("✅ Explicit city \"{City}\" in \"{Input}\" — clearing disambiguation for {Side}", explicitCity, originalInput, side);
                addr["is_ambiguous"] = false;
                addr["alternatives"] = new System.Text.Json.Nodes.JsonArray();
                var otherSide = side == "pickup" ? "dropoff" : "pickup";
                var otherAmbig = work[otherSide]?["is_ambiguous"]?.GetValue<bool>() ?? false;
                if (!otherAmbig)
                {
                    work["status"] = "ready";
                    work["clarification_message"] = null;
                }
            }
        }

        // ── Enforce disambiguation for multi-district streets ──
        // Gemini detects these dynamically via its world knowledge and populates districts_found.
        // We trust Gemini's detection; our job here is to ensure state is consistent and
        // fall back to uk_locations if Gemini didn't provide district names.
        // Track force-flagged sides so the DB auto-correct step cannot clear them.
        var forceAmbiguousSides = new HashSet<string>();
        foreach (var side in new[] { "pickup", "dropoff" })
        {
            var addr = work[side];
            if (addr == null) continue;
            if (addr["matched_from_history"]?.GetValue<bool>() == true) continue;

            var isAmbiguous = addr["is_ambiguous"]?.GetValue<bool>() ?? false;

            // Read districts_found from Gemini's response
            var districtsNode = addr["districts_found"];
            var districts = districtsNode?.AsArray()
                .Select(d => d?.GetValue<string>() ?? "")
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToArray() ?? Array.Empty<string>();

            // If Gemini flagged multiple districts → enforce clarification
            if (districts.Length > 1 && !isAmbiguous)
            {
                var streetName = addr["street_name"]?.GetValue<string>() ?? "";
                var city = addr["city"]?.GetValue<string>() ?? work["detected_area"]?.GetValue<string>() ?? "";
                var originalInput = side == "pickup" ? (pickup ?? "") : (destination ?? "");
                var hasPostcode = System.Text.RegularExpressions.Regex.IsMatch(originalInput, @"\b[A-Z]{1,2}\d{1,2}\s?\d[A-Z]{2}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // High house numbers discriminate (e.g. 1214A Warwick Road)
                var streetNum = addr["street_number"]?.GetValue<string>() ?? "";
                var numMatch = System.Text.RegularExpressions.Regex.Match(streetNum, @"^(\d+)");
                var houseNumber = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : 0;
                var userNamedDistrict = districts.Any(d => originalInput.Contains(d, StringComparison.OrdinalIgnoreCase));

                if (!hasPostcode && !userNamedDistrict && houseNumber < 500)
                {
                    _logger.LogInformation("⚠️ Gemini detected \"{Street}\" in {City} across {Count} districts: {Districts} — forcing clarification",
                        streetName, city, districts.Length, string.Join(", ", districts));
                    addr["is_ambiguous"] = true;
                    work["status"] = "clarification_needed";
                    forceAmbiguousSides.Add(side);

                    // Build alternatives from Gemini's districts_found
                    var altsArr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var d in districts.Take(6))
                        altsArr.Add($"{streetName}, {d}, {city}");
                    addr["alternatives"] = altsArr;
                    work["clarification_message"] = $"There are several {streetName}s in {city}. Which area is it in? For example: {string.Join(", or ", districts.Take(3))}.";
                }
                else if (userNamedDistrict || hasPostcode || houseNumber >= 500)
                {
                    _logger.LogDebug("✅ District/postcode/high house number discriminates \"{Street}\" in {City} — no clarification needed", streetName, city);
                }
            }
            else if (isAmbiguous && districts.Length > 0)
            {
                // Gemini already set is_ambiguous=true AND provided districts — ensure clarification message uses them
                var streetName = addr["street_name"]?.GetValue<string>() ?? "";
                var city = addr["city"]?.GetValue<string>() ?? work["detected_area"]?.GetValue<string>() ?? "";
                forceAmbiguousSides.Add(side);
                work["status"] = "clarification_needed";
                var existingMsg = work["clarification_message"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(existingMsg))
                    work["clarification_message"] = $"There are several {streetName}s in {city}. Which area is it in? For example: {string.Join(", or ", districts.Take(3))}.";
            }
            else if (isAmbiguous)
            {
                // Gemini flagged ambiguous but gave no districts — fall back to uk_locations
                var streetName = addr["street_name"]?.GetValue<string>() ?? "";
                var city = addr["city"]?.GetValue<string>() ?? work["detected_area"]?.GetValue<string>() ?? "";
                forceAmbiguousSides.Add(side);
                work["status"] = "clarification_needed";
                var dbAreas = await GetCityAreasAsync(city);
                if (dbAreas.Length > 0)
                {
                    var altsArr = new System.Text.Json.Nodes.JsonArray();
                    foreach (var area in dbAreas.Take(6))
                        altsArr.Add($"{streetName}, {area}, {city}");
                    addr["alternatives"] = altsArr;
                    work["clarification_message"] = $"There are several {streetName}s in {city}. Which area is it in? For example: {string.Join(", or ", dbAreas.Take(3))}.";
                }
                else if (string.IsNullOrWhiteSpace(work["clarification_message"]?.GetValue<string>()))
                {
                    work["clarification_message"] = $"There are several {streetName}s in {city}. Which area or district is it in?";
                }
            }
        }

        // ── Detect country for currency ──
        string detectedCountry = "UK";
        if (work["phone_analysis"]?["detected_country"] != null)
            detectedCountry = work["phone_analysis"]!["detected_country"]!.GetValue<string>();

        // ── NOMINATIM RE-GEOCODE: verify/correct Gemini's lat/lon ──
        // Gemini's coordinates are NOT reliable — use the resolved address text to geocode independently.
        var countryCode = detectedCountry switch
        {
            "NL" => "NL", "BE" => "BE", "FR" => "FR", "DE" => "DE", "ES" => "ES",
            "IT" => "IT", "IE" => "IE", "AT" => "AT", "PT" => "PT", "US" => "US", "CA" => "CA",
            _ => "GB"
        };

        foreach (var side in new[] { "pickup", "dropoff" })
        {
            var addr = work[side];
            if (addr == null) continue;
            var isAmbiguous = addr["is_ambiguous"]?.GetValue<bool>() ?? false;
            if (isAmbiguous) continue; // don't re-geocode ambiguous addresses

            var resolvedAddress = addr["address"]?.GetValue<string>();
            var postalCode = addr["postal_code"]?.GetValue<string>();
            var streetName = addr["street_name"]?.GetValue<string>();
            var city = addr["city"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(resolvedAddress)) continue;

            try
            {
                // Prefer postcode-based geocoding if available (most accurate)
                var geocodeQuery = !string.IsNullOrWhiteSpace(postalCode)
                    ? $"{addr["street_number"]?.GetValue<string>()} {streetName}, {postalCode}, {city}".Trim().TrimStart(',').Trim()
                    : resolvedAddress;

                var nominatimResult = await GeocodeNominatimAsync(geocodeQuery, countryCode);
                if (nominatimResult != null)
                {
                    var geminiLat = GetDoubleNode(work, side, "lat") ?? 0;
                    var geminiLon = GetDoubleNode(work, side, "lon") ?? 0;
                    var drift = HaversineDistance(geminiLat, geminiLon, nominatimResult.Lat, nominatimResult.Lon);

                    if (drift > 0.5) // more than 0.5 miles drift
                    {
                        _logger.LogInformation("📍 NOMINATIM CORRECTION ({Side}): Gemini lat/lon drifted {Drift:F1} miles — using Nominatim coords", side, drift);
                    }
                    else
                    {
                        _logger.LogDebug("✅ NOMINATIM VERIFY ({Side}): Gemini coords within {Drift:F2} miles — OK", side, drift);
                    }

                    addr["lat"] = nominatimResult.Lat;
                    addr["lon"] = nominatimResult.Lon;
                }
                else
                {
                    _logger.LogDebug("ℹ️ Nominatim returned no result for {Side} \"{Query}\" — keeping Gemini coords", side, geocodeQuery);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nominatim re-geocode failed for {Side} (non-fatal, keeping Gemini coords)", side);
            }
        }

        // ── Calculate fare from coordinates (now using verified coords) ──
        double? pLat = GetDoubleNode(work, "pickup", "lat");
        double? pLon = GetDoubleNode(work, "pickup", "lon");
        double? dLat = GetDoubleNode(work, "dropoff", "lat");
        double? dLon = GetDoubleNode(work, "dropoff", "lon");

        if (pLat.HasValue && pLon.HasValue && dLat.HasValue && dLon.HasValue && pLat.Value != 0 && dLat.Value != 0)
        {
            var distMiles = HaversineDistance(pLat.Value, pLon.Value, dLat.Value, dLon.Value);

            if (distMiles > 200)
            {
                _logger.LogWarning("🚨 ABSURD DISTANCE: {Dist:F1} miles", distMiles);
                var pickupHasHistory = work["pickup"]?["matched_from_history"]?.GetValue<bool>() == true;
                var uncertainSide = pickupHasHistory ? "dropoff" : "pickup";
                work[uncertainSide]!["is_ambiguous"] = true;
                work["status"] = "clarification_needed";
                work["distance_warning"] = $"Pickup and dropoff are {Math.Round(distMiles)} miles apart — this seems too far for a taxi.";
                work["fare"] = null;
            }
            else if (distMiles > 100)
            {
                var fare = CalculateFare(distMiles, detectedCountry);
                work["fare"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(fare));
                work["distance_warning"] = $"Pickup and dropoff are {Math.Round(distMiles)} miles apart — please confirm.";
                _logger.LogDebug("💰 Fare with warning: {Fare} ({Dist:F1} miles)", fare.Fare, distMiles);
            }
            else
            {
                var fare = CalculateFare(distMiles, detectedCountry);
                work["fare"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(fare));
                _logger.LogDebug("💰 Fare: {Fare} ({Dist:F1} miles)", fare.Fare, distMiles);
            }
        }

        // ── Duplicate postcode check ──
        var pickupPostal = (work["pickup"]?["postal_code"]?.GetValue<string>() ?? "").Trim().ToUpperInvariant();
        var dropoffPostal = (work["dropoff"]?["postal_code"]?.GetValue<string>() ?? "").Trim().ToUpperInvariant();
        var pickupStreetName = (work["pickup"]?["street_name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var dropoffStreetName = (work["dropoff"]?["street_name"]?.GetValue<string>() ?? "").ToLowerInvariant();

        if (!string.IsNullOrEmpty(pickupPostal) && pickupPostal == dropoffPostal &&
            !string.IsNullOrEmpty(pickupStreetName) && !string.IsNullOrEmpty(dropoffStreetName) && pickupStreetName != dropoffStreetName)
        {
            _logger.LogWarning("⚠️ DUPLICATE POSTCODE: \"{PStreet}\" and \"{DStreet}\" both have \"{PC}\" — clearing dropoff", pickupStreetName, dropoffStreetName, pickupPostal);
            work["dropoff"]!["postal_code"] = "";
        }

        // ── DB fuzzy street matching (fuzzy_match_street RPC) ──
        var sanityCorrected = false;
        foreach (var side in new[] { "pickup", "dropoff" })
        {
            var addr = work[side];
            if (addr == null) continue;
            var streetN = addr["street_name"]?.GetValue<string>();
            var cityN = addr["city"]?.GetValue<string>();
            if (string.IsNullOrEmpty(streetN) || string.IsNullOrEmpty(cityN)) continue;
            if (addr["matched_from_history"]?.GetValue<bool>() == true) continue;

            var originalInput = side == "pickup" ? (pickup ?? "") : (destination ?? "");

            try
            {
                var fuzzyMatches = await FuzzyMatchStreetAsync(streetN, cityN);
                if (fuzzyMatches == null || fuzzyMatches.Count == 0)
                {
                    _logger.LogDebug("ℹ️ DB VERIFY: no entries for \"{Street}\" in {City}", streetN, cityN);
                    continue;
                }

                var exactMatch = fuzzyMatches.FirstOrDefault(m =>
                    string.Equals(m.MatchedName, streetN, StringComparison.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    _logger.LogDebug("✅ DB VERIFY: \"{Street}\" exists in {City}", streetN, cityN);
                }
                else
                {
                    var best = fuzzyMatches[0];
                    _logger.LogInformation("⚠️ DB VERIFY: \"{Street}\" NOT found in {City}. Best: \"{Best}\" ({Score}%)", streetN, cityN, best.MatchedName, (int)(best.Similarity * 100));

                    if (best.Similarity > 0.35 && best.Lat.HasValue && best.Lon.HasValue)
                    {
                        // Extract house number from original input
                        var houseNumMatch = System.Text.RegularExpressions.Regex.Match(originalInput, @"^(\d+[A-Za-z]?)\s*,?\s*");
                        var houseNum = houseNumMatch.Success ? houseNumMatch.Groups[1].Value + " " : (addr["street_number"]?.GetValue<string>() is string sn && !string.IsNullOrEmpty(sn) ? sn + " " : "");

                        _logger.LogInformation("🔄 DB AUTO-CORRECT: \"{From}\" → \"{To}\" in {City} ({Score}%)", streetN, best.MatchedName, cityN, (int)(best.Similarity * 100));
                        addr["address"] = $"{houseNum}{best.MatchedName}, {cityN}";
                        addr["street_name"] = best.MatchedName;
                        addr["lat"] = best.Lat.Value;
                        addr["lon"] = best.Lon.Value;
                        // CRITICAL: do NOT clear is_ambiguous if the multi-district guard forced it —
                        // the street existing in the DB does NOT mean it's unambiguous (School Road
                        // exists in many Birmingham districts). Only clear if we didn't force it.
                        if (!forceAmbiguousSides.Contains(side))
                        {
                            addr["is_ambiguous"] = false;
                            addr["alternatives"] = new System.Text.Json.Nodes.JsonArray();
                        }
                        sanityCorrected = true;
                    }
                    else if (fuzzyMatches.Count > 1)
                    {
                        _logger.LogInformation("⚠️ DB: offering {Count} alternatives for \"{Street}\" in {City}", fuzzyMatches.Count, streetN, cityN);
                        addr["is_ambiguous"] = true;
                        var altsArr = new System.Text.Json.Nodes.JsonArray();
                        foreach (var m in fuzzyMatches.Where(m => m.Similarity > 0.2).Take(3))
                            altsArr.Add($"{m.MatchedName}, {m.MatchedCity ?? cityN}");
                        addr["alternatives"] = altsArr;
                        work["status"] = "clarification_needed";
                        work["clarification_message"] = $"I couldn't find \"{streetN}\" in {cityN}. Did you mean: {string.Join(", or ", fuzzyMatches.Where(m => m.Similarity > 0.2).Take(3).Select(m => m.MatchedName))}?";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Street existence check error (non-fatal)");
            }
        }

        // Recalculate fare if DB corrected addresses
        if (sanityCorrected)
        {
            var cPLat = GetDoubleNode(work, "pickup", "lat");
            var cPLon = GetDoubleNode(work, "pickup", "lon");
            var cDLat = GetDoubleNode(work, "dropoff", "lat");
            var cDLon = GetDoubleNode(work, "dropoff", "lon");
            if (cPLat.HasValue && cPLon.HasValue && cDLat.HasValue && cDLon.HasValue && cPLat.Value != 0 && cDLat.Value != 0)
            {
                var correctedDist = HaversineDistance(cPLat.Value, cPLon.Value, cDLat.Value, cDLon.Value);
                if (correctedDist < 100)
                {
                    var fare = CalculateFare(correctedDist, detectedCountry);
                    work["fare"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(fare));
                    var pAmb = work["pickup"]?["is_ambiguous"]?.GetValue<bool>() ?? false;
                    var dAmb = work["dropoff"]?["is_ambiguous"]?.GetValue<bool>() ?? false;
                    if (!pAmb && !dAmb) work["status"] = "ready";
                    _logger.LogDebug("💰 Fare recalculated after DB correction: {Fare} ({Dist:F1} miles)", fare.Fare, correctedDist);
                }
            }
        }

        // ── SPOKEN HOUSE NUMBER GUARD (post-resolution safety net) ──
        // Since the spoken number was already passed as a geocoding filter above,
        // Gemini should have used it. This guard catches any remaining substitution
        // where Gemini resolved to an alphanumeric (e.g. "4B") despite being told "43".
        var guardChecks = new[]
        {
            (side: "pickup",  spoken: spokenPickupNumber),
            (side: "dropoff", spoken: spokenDestNumber),
        };

        foreach (var (side, spokenNum) in guardChecks)
        {
            if (string.IsNullOrWhiteSpace(spokenNum)) continue;
            var addr = work[side];
            if (addr == null) continue;

            var resolvedNum = addr["street_number"]?.GetValue<string>()?.Trim() ?? "";

            // If Gemini did not use the spoken number, force it in
            if (!string.IsNullOrEmpty(resolvedNum) && !string.Equals(resolvedNum, spokenNum, StringComparison.OrdinalIgnoreCase))
            {
                var numericPrefix = System.Text.RegularExpressions.Regex.Match(resolvedNum, @"^\d+").Value;
                var hasLetterSuffix = System.Text.RegularExpressions.Regex.IsMatch(resolvedNum, @"^\d+[A-Za-z]$");
                var spokenDigits = System.Text.RegularExpressions.Regex.Match(spokenNum.Trim(), @"^\d+").Value;

                // Geocoder substitution: spoken="43" but resolved="4B" (alphanumeric, different prefix)
                if (hasLetterSuffix && !string.IsNullOrEmpty(spokenDigits) && spokenDigits != numericPrefix)
                {
                    _logger.LogWarning("🏠 HOUSE NUMBER GUARD ({Side}): caller said '{Spoken}' but Gemini resolved '{Resolved}' — forcing spoken number",
                        side, spokenNum, resolvedNum);

                    // Force the spoken number — the caller is the authority
                    addr["street_number"] = spokenNum;
                    var streetName = addr["street_name"]?.GetValue<string>() ?? "";
                    var city = addr["city"]?.GetValue<string>() ?? "";
                    var postal = addr["postal_code"]?.GetValue<string>() ?? "";
                    addr["address"] = string.IsNullOrEmpty(postal)
                        ? $"{spokenNum} {streetName}, {city}"
                        : $"{spokenNum} {streetName}, {city} {postal}";
                    addr["house_number_mismatch"] = true;
                    _logger.LogInformation("🏠 GUARD: Forced address → {Addr}", addr["address"]?.GetValue<string>());
                }
            }
            else if (string.IsNullOrEmpty(resolvedNum) && !string.IsNullOrEmpty(spokenNum))
            {
                // Gemini returned no street number — inject the spoken one
                addr["street_number"] = spokenNum;
                var streetName = addr["street_name"]?.GetValue<string>() ?? "";
                var city = addr["city"]?.GetValue<string>() ?? "";
                var postal = addr["postal_code"]?.GetValue<string>() ?? "";
                var currentAddr = addr["address"]?.GetValue<string>() ?? "";
                if (!currentAddr.StartsWith(spokenNum))
                {
                    addr["address"] = string.IsNullOrEmpty(postal)
                        ? $"{spokenNum} {streetName}, {city}"
                        : $"{spokenNum} {streetName}, {city} {postal}";
                }
                _logger.LogInformation("🏠 GUARD: Injected missing house number '{Num}' into {Side} address", spokenNum, side);
            }
        }

        // ── ADDRESS SANITY GUARD: second-pass Gemini check ──
        var distMilesPost = (pLat.HasValue && pLon.HasValue && dLat.HasValue && dLon.HasValue && pLat.Value != 0 && dLat.Value != 0)
            ? HaversineDistance(
                GetDoubleNode(work, "pickup", "lat") ?? 0, GetDoubleNode(work, "pickup", "lon") ?? 0,
                GetDoubleNode(work, "dropoff", "lat") ?? 0, GetDoubleNode(work, "dropoff", "lon") ?? 0)
            : (double?)null;

        var pickupInputStreet = ExtractStreetKey(pickup ?? "");
        var dropoffInputStreet = ExtractStreetKey(destination ?? "");
        var pickupResolved = (work["pickup"]?["street_name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var dropoffResolved = (work["dropoff"]?["street_name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var hasStreetMismatch =
            (!string.IsNullOrEmpty(dropoffInputStreet) && !string.IsNullOrEmpty(dropoffResolved) && !dropoffResolved.Contains(dropoffInputStreet) && !dropoffInputStreet.Contains(dropoffResolved)) ||
            (!string.IsNullOrEmpty(pickupInputStreet) && !string.IsNullOrEmpty(pickupResolved) && !pickupResolved.Contains(pickupInputStreet) && !pickupInputStreet.Contains(pickupResolved));

        var currentStatus = work["status"]?.GetValue<string>() ?? "ready";
        var pickupAlts = work["pickup"]?["alternatives"];
        var dropoffAlts = work["dropoff"]?["alternatives"];
        var noAlts = (pickupAlts == null || pickupAlts.AsArray().Count == 0) && (dropoffAlts == null || dropoffAlts.AsArray().Count == 0);

        // ALWAYS run dual-perspective sanity check when both addresses are resolved
        var hasBothAddresses = work["pickup"]?["street_name"] != null && work["dropoff"]?["street_name"] != null;
        var needsSanityCheck = hasBothAddresses ||
                               (currentStatus == "clarification_needed" && noAlts) ||
                               (distMilesPost.HasValue && distMilesPost.Value > 50) ||
                               hasStreetMismatch;

        if (needsSanityCheck)
        {
            _logger.LogInformation("🛡️ SANITY GUARD: triggering (dist={Dist}, status={Status}, mismatch={Mismatch})",
                distMilesPost?.ToString("F1") ?? "N/A", currentStatus, hasStreetMismatch);

            try
            {
                var contextCity = work["detected_area"]?.GetValue<string>() ?? work["pickup"]?["city"]?.GetValue<string>() ?? "";
                var sanityResult = await RunSanityGuardAsync(
                    contextCity, pickup, destination,
                    work["pickup"]?["address"]?.GetValue<string>(), work["pickup"]?["street_name"]?.GetValue<string>(), work["pickup"]?["city"]?.GetValue<string>(),
                    work["dropoff"]?["address"]?.GetValue<string>(), work["dropoff"]?["street_name"]?.GetValue<string>(), work["dropoff"]?["city"]?.GetValue<string>(),
                    distMilesPost);

                if (sanityResult != null)
                {
                    _logger.LogInformation("🛡️ SANITY VERDICT: {Verdict} (side: {Side})", sanityResult.Verdict, sanityResult.MismatchSide);

                    if (sanityResult.Verdict == "MATCH")
                    {
                        _logger.LogInformation("✅ SANITY MATCH: clearing disambiguation");
                        if (work["pickup"] != null) { work["pickup"]!["is_ambiguous"] = false; work["pickup"]!["alternatives"] = new System.Text.Json.Nodes.JsonArray(); }
                        if (work["dropoff"] != null) { work["dropoff"]!["is_ambiguous"] = false; work["dropoff"]!["alternatives"] = new System.Text.Json.Nodes.JsonArray(); }
                        work["status"] = "ready";
                        work["clarification_message"] = null;

                        // POI name preservation
                        PreservePoiName(work, "pickup", pickup ?? "");
                        PreservePoiName(work, "dropoff", destination ?? "");

                        // Ensure fare is present
                        if (work["fare"] == null && distMilesPost.HasValue && distMilesPost.Value < 200)
                        {
                            var fare = CalculateFare(distMilesPost.Value, detectedCountry);
                            work["fare"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(fare));
                        }
                    }
                    else if (sanityResult.Verdict == "MISMATCH" && sanityResult.MismatchSide != "none")
                    {
                        var sides = sanityResult.MismatchSide == "both" ? new[] { "pickup", "dropoff" } : new[] { sanityResult.MismatchSide };

                        foreach (var side in sides)
                        {
                            var origInput = side == "pickup" ? (pickup ?? "") : (destination ?? "");
                            var suggestedName = sanityResult.SuggestedCorrection ?? origInput;
                            var searchCity = contextCity;

                            if (!string.IsNullOrEmpty(searchCity))
                            {
                                try
                                {
                                    var fuzzyMatches = await FuzzyMatchStreetAsync(suggestedName, searchCity);
                                    if (fuzzyMatches != null && fuzzyMatches.Count > 0)
                                    {
                                        var best = fuzzyMatches[0];
                                        if (best.Similarity > 0.4 && best.Lat.HasValue && best.Lon.HasValue)
                                        {
                                            var hnMatch = System.Text.RegularExpressions.Regex.Match(origInput, @"^(\d+[A-Za-z]?)\s*,?\s*");
                                            var hn = hnMatch.Success ? hnMatch.Groups[1].Value + " " : "";

                                            _logger.LogInformation("✅ SANITY AUTO-CORRECT: \"{From}\" → \"{To}\" ({Score}%)", origInput, best.MatchedName, (int)(best.Similarity * 100));
                                            work[side]!["address"] = $"{hn}{best.MatchedName}, {searchCity}";
                                            work[side]!["street_name"] = best.MatchedName;
                                            work[side]!["city"] = searchCity;
                                            work[side]!["lat"] = best.Lat.Value;
                                            work[side]!["lon"] = best.Lon.Value;
                                            work[side]!["is_ambiguous"] = false;
                                            work[side]!["alternatives"] = new System.Text.Json.Nodes.JsonArray();
                                            sanityCorrected = true;
                                        }
                                        else
                                        {
                                            work[side]!["is_ambiguous"] = true;
                                            var altsArr = new System.Text.Json.Nodes.JsonArray();
                                            foreach (var m in fuzzyMatches.Where(m => m.Similarity > 0.25).Take(3))
                                                altsArr.Add($"{m.MatchedName}, {m.MatchedCity ?? searchCity}");
                                            work[side]!["alternatives"] = altsArr;
                                            work["status"] = "clarification_needed";
                                            work["clarification_message"] = $"I couldn't verify \"{origInput}\" in {searchCity}. Did you mean: {string.Join(", or ", fuzzyMatches.Where(m => m.Similarity > 0.25).Take(3).Select(m => m.MatchedName))}?";
                                        }
                                    }
                                    else
                                    {
                                        work[side]!["is_ambiguous"] = true;
                                        work["status"] = "clarification_needed";
                                        work["clarification_message"] = $"The {side} address \"{origInput}\" doesn't seem right for {searchCity}. {(sanityResult.SuggestedCorrection != null ? $"Did you mean \"{sanityResult.SuggestedCorrection}\"?" : "Could you repeat the street name?")}";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Sanity fuzzy match error (non-fatal)");
                                }
                            }
                        }

                        // Recalculate fare after sanity corrections
                        if (sanityCorrected)
                        {
                            var nPLat = GetDoubleNode(work, "pickup", "lat");
                            var nPLon = GetDoubleNode(work, "pickup", "lon");
                            var nDLat = GetDoubleNode(work, "dropoff", "lat");
                            var nDLon = GetDoubleNode(work, "dropoff", "lon");
                            if (nPLat.HasValue && nPLon.HasValue && nDLat.HasValue && nDLon.HasValue && nPLat.Value != 0 && nDLat.Value != 0)
                            {
                                var newDist = HaversineDistance(nPLat.Value, nPLon.Value, nDLat.Value, nDLon.Value);
                                if (newDist < 100)
                                {
                                    var fare = CalculateFare(newDist, detectedCountry);
                                    work["fare"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(fare));
                                    var pAmb2 = work["pickup"]?["is_ambiguous"]?.GetValue<bool>() ?? false;
                                    var dAmb2 = work["dropoff"]?["is_ambiguous"]?.GetValue<bool>() ?? false;
                                    work["status"] = (pAmb2 || dAmb2) ? "clarification_needed" : "ready";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sanity guard error (non-fatal)");
            }
        }

        // Return final result as JsonElement
        var finalJson = work.ToJsonString();
        return JsonDocument.Parse(finalJson).RootElement.Clone();
    }

    // ── POI name preservation (same as edge function) ──
    private static void PreservePoiName(System.Text.Json.Nodes.JsonNode work, string side, string originalInput)
    {
        var sideData = work[side];
        if (sideData == null || string.IsNullOrEmpty(originalInput)) return;
        var hasHouseNumber = System.Text.RegularExpressions.Regex.IsMatch(originalInput.Trim(), @"^\d+[A-Za-z]?\s");
        if (hasHouseNumber) return;

        var cleanedInput = System.Text.RegularExpressions.Regex.Replace(originalInput, @"\s*(?:in|,)\s*(coventry|birmingham|london|manchester|derby|leicester)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        var inputWords = System.Text.RegularExpressions.Regex.Replace(cleanedInput.ToLowerInvariant(), @"[^a-z\s]", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        if (inputWords.Length == 0) return;

        var streetLower = (sideData["street_name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        var inputInStreetName = inputWords.Any(w => streetLower.Contains(w));

        if (!inputInStreetName)
        {
            var resolvedAddr = sideData["address"]?.GetValue<string>() ?? "";
            if (!resolvedAddr.ToLowerInvariant().StartsWith(cleanedInput.ToLowerInvariant()))
                sideData["address"] = $"{cleanedInput}, {resolvedAddr}";
            sideData["street_name"] = cleanedInput;
            // Logger not available in static context — caller logs if needed
        }
    }

    private static double? GetDoubleNode(System.Text.Json.Nodes.JsonNode work, string obj, string prop)
    {
        var node = work[obj]?[prop];
        if (node == null) return null;
        try { return node.GetValue<double>(); } catch { return null; }
    }

    // ── Supabase RPC: fuzzy_match_street ──
    private async Task<List<FuzzyMatchResult>?> FuzzyMatchStreetAsync(string streetName, string city)
    {
        try
        {
            var url = $"{_supabase.Url}/rest/v1/rpc/fuzzy_match_street";
            var body = JsonSerializer.Serialize(new { p_street_name = streetName, p_city = city, p_limit = 5 });
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("apikey", _supabase.AnonKey);
            request.Headers.Add("Authorization", $"Bearer {_supabase.AnonKey}");

            var resp = await _http.SendAsync(request);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;

            var results = new List<FuzzyMatchResult>();
            foreach (var item in arr.EnumerateArray())
            {
                results.Add(new FuzzyMatchResult
                {
                    MatchedName = item.GetProperty("matched_name").GetString() ?? "",
                    MatchedCity = item.TryGetProperty("matched_city", out var mc) ? mc.GetString() : null,
                    Similarity = item.GetProperty("similarity_score").GetDouble(),
                    Lat = item.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number ? lat.GetDouble() : null,
                    Lon = item.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number ? lon.GetDouble() : null,
                });
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "fuzzy_match_street RPC error");
            return null;
        }
    }

    // ── Sanity guard: second-pass Gemini call ──
    private async Task<SanityVerdict?> RunSanityGuardAsync(
        string contextCity, string? pickup, string? destination,
        string? pickupAddr, string? pickupStreet, string? pickupCity,
        string? dropoffAddr, string? dropoffStreet, string? dropoffCity,
        double? distMiles)
    {
        try
        {
            var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

            var sanityUserMsg = $@"Context City: {contextCity}

PERSPECTIVE 1 — CALLER'S RAW SPEECH (from Speech-to-Text):
  Pickup: ""{pickup ?? ""}""
  Dropoff: ""{destination ?? ""}""

PERSPECTIVE 2 — ADA'S GEOCODED INTERPRETATION:
  Pickup: ""{pickupAddr ?? ""}"" (street: ""{pickupStreet ?? ""}"", city: {pickupCity ?? "unknown"})
  Dropoff: ""{dropoffAddr ?? ""}"" (street: ""{dropoffStreet ?? ""}"", city: {dropoffCity ?? "unknown"})

Distance between geocoded points: {distMiles?.ToString("F1") ?? "unknown"} miles

Your task: Compare BOTH perspectives. Determine which version is more likely to be the correct real-world address.
- If both match or are very similar → MATCH (high confidence)
- If they differ but Ada's geocoded version is plausible for the area → MATCH (trust geocoder)
- If the caller said something completely different from what the geocoder resolved → MISMATCH
- For each side (pickup/dropoff), indicate which perspective is more trustworthy.";

            var sanitySystemPrompt = $@"You are a Dual-Perspective Taxi Address Verifier. You receive TWO versions of each address:
1. CALLER'S RAW SPEECH — what the Speech-to-Text system heard (may contain mishearings)
2. ADA'S GEOCODED VERSION — what the geocoding system resolved (may have picked wrong location)

Your job: Compare both perspectives and determine the BEST real-world address for each side.

DECISION RULES:
- If both say essentially the same street → MATCH. The geocoded version is authoritative.
- If geocoder corrected a minor STT error (e.g., ""Daventry"" → ""Davenport"") and the geocoded street EXISTS in {contextCity} → MATCH. Trust the geocoder.
- If geocoder returned a completely different street in a distant city → MISMATCH. The caller's intent was different.
- If the caller said a landmark/POI name and geocoder resolved it to a street address → MATCH. The geocoder correctly resolved the POI.
- House number variations (528 vs 52A) → MATCH.

IMPORTANT: When in doubt, prefer Ada's geocoded version if the street exists in the local area. STT mishearings are far more common than geocoder errors for local addresses.";


            var requestBody = new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = sanityUserMsg } } } },
                systemInstruction = new { parts = new[] { new { text = sanitySystemPrompt } } },
                tools = new[]
                {
                    new
                    {
                        functionDeclarations = new[]
                        {
                            new
                            {
                                name = "address_sanity_verdict",
                                description = "Return the sanity check verdict",
                                parameters = new
                                {
                                    type = "OBJECT",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["pickup_phonetic_similarity"] = new { type = "NUMBER" },
                                        ["dropoff_phonetic_similarity"] = new { type = "NUMBER" },
                                        ["geographic_leap"] = new { type = "BOOLEAN" },
                                        ["reasoning"] = new { type = "STRING" },
                                        ["verdict"] = new { type = "STRING", @enum = new[] { "MATCH", "MISMATCH", "UNCERTAIN" } },
                                        ["mismatch_side"] = new { type = "STRING", @enum = new[] { "pickup", "dropoff", "both", "none" } },
                                        ["suggested_correction"] = new { type = "STRING" }
                                    },
                                    required = new[] { "reasoning", "verdict", "mismatch_side" }
                                }
                            }
                        }
                    }
                },
                toolConfig = new { functionCallingConfig = new { mode = "ANY", allowedFunctionNames = new[] { "address_sanity_verdict" } } },
                generationConfig = new { temperature = 0.0 }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _http.PostAsync(geminiUrl, content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sanity guard API error: {Status}", (int)response.StatusCode);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var args = ParseGeminiFunctionCall(responseJson);
            if (args == null) return null;

            return new SanityVerdict
            {
                Verdict = args.Value.TryGetProperty("verdict", out var v) ? v.GetString() ?? "UNCERTAIN" : "UNCERTAIN",
                MismatchSide = args.Value.TryGetProperty("mismatch_side", out var ms) ? ms.GetString() ?? "none" : "none",
                SuggestedCorrection = args.Value.TryGetProperty("suggested_correction", out var sc) ? sc.GetString() : null,
                Reasoning = args.Value.TryGetProperty("reasoning", out var r) ? r.GetString() : null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sanity guard call failed");
            return null;
        }
    }

    private sealed class FuzzyMatchResult
    {
        public string MatchedName { get; set; } = "";
        public string? MatchedCity { get; set; }
        public double Similarity { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
    }

    private sealed class SanityVerdict
    {
        public string Verdict { get; set; } = "UNCERTAIN";
        public string MismatchSide { get; set; } = "none";
        public string? SuggestedCorrection { get; set; }
        public string? Reasoning { get; set; }
    }

    // ── Nominatim geocoding for coordinate verification ──
    private async Task<NominatimGeoPoint?> GeocodeNominatimAsync(string address, string countryCode)
    {
        try
        {
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&countrycodes={countryCode}&format=json&limit=1";
            var resp = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.GetArrayLength() == 0) return null;
            var first = doc.RootElement[0];
            return new NominatimGeoPoint
            {
                Lat = double.Parse(first.GetProperty("lat").GetString()!),
                Lon = double.Parse(first.GetProperty("lon").GetString()!)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nominatim geocode failed for \"{Address}\"", address);
            return null;
        }
    }

    private sealed class NominatimGeoPoint { public double Lat { get; set; } public double Lon { get; set; } }

    // Country → currency mapping based on phone-detected country
    private static readonly Dictionary<string, (string Symbol, string CurrencyWord, string SubunitWord)> CountryCurrency = new()
    {
        ["UK"] = ("£", "pounds", "pence"),
        ["US"] = ("$", "dollars", "cents"),
        ["CA"] = ("$", "dollars", "cents"),
        ["NL"] = ("€", "euros", "cents"),
        ["BE"] = ("€", "euros", "cents"),
        ["FR"] = ("€", "euros", "centimes"),
        ["DE"] = ("€", "euros", "cent"),
        ["ES"] = ("€", "euros", "céntimos"),
        ["IT"] = ("€", "euros", "centesimi"),
        ["IE"] = ("€", "euros", "cents"),
        ["AT"] = ("€", "euros", "cent"),
        ["PT"] = ("€", "euros", "cêntimos"),
        ["LU"] = ("€", "euros", "cents"),
        ["FI"] = ("€", "euros", "cents"),
        ["GR"] = ("€", "euros", "cents"),
    };

    private static (string Symbol, string CurrencyWord, string SubunitWord) GetCurrencyForCountry(string detectedCountry)
    {
        if (CountryCurrency.TryGetValue(detectedCountry, out var c)) return c;
        // Default to EUR for unknown European countries, GBP for UK-prefix
        return ("€", "euros", "cents");
    }

    private FareCalcResult CalculateFare(double distanceMiles, string detectedCountry)
    {
        var rawFare = Math.Max((double)MinFare, (double)BaseFare + distanceMiles * (double)PerMile);
        var fare = Math.Round(rawFare * 2, MidpointRounding.AwayFromZero) / 2;

        var tripEtaMinutes = (int)Math.Ceiling(distanceMiles / AvgSpeedMph * 60) + BufferMinutes;
        var driverEtaMinutes = Math.Min(DriverEtaMax, Math.Max(DriverEtaMin, DriverEtaDefault + (int)(distanceMiles / 20)));

        var (symbol, currencyWord, subunitWord) = GetCurrencyForCountry(detectedCountry);

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
    private const string SystemPrompt = @"Role: Professional Taxi Dispatch Logic System for Europe and North America.

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
1. UK: +44. Landline area codes give strong city bias (024→Coventry, 020→London, 0121→Birmingham). Mobile +44 7 → no geographic clue.
2. Netherlands: +31. 020→Amsterdam, 010→Rotterdam, 070→The Hague, 030→Utrecht.
3. Belgium: +32. 02→Brussels, 03→Antwerp, 09→Ghent.
4. France: +33. 01→Paris/Île-de-France.
5. Germany: +49. 030→Berlin, 089→Munich, 040→Hamburg.
6. Spain: +34. Italy: +39. Ireland: +353. Austria: +43. Portugal: +351.
7. US/Canada: +1.
8. EXPLICIT CITY NAME OVERRIDES PHONE PREFIX.

PHONE → COUNTRY MAPPING for detected_country field:
+44 → UK, +31 → NL, +32 → BE, +33 → FR, +49 → DE, +34 → ES, +39 → IT, +353 → IE, +43 → AT, +351 → PT, +352 → LU, +358 → FI, +30 → GR, +1 → US.
Always set detected_country to the TWO-LETTER ISO code based on the phone prefix.

CHAIN BUSINESSES & LANDMARKS WITH MULTIPLE LOCATIONS (CRITICAL):
For chains with no city context from a mobile caller, set is_ambiguous=true with alternatives.

PLACE NAME / POI DETECTION (CRITICAL):
When no house number, treat as potential business/landmark FIRST.
When the input is a POI, business name, landmark, or place name:
- You MUST resolve it to a FULL POSTAL STREET ADDRESS (e.g. ""Tesco Extra"" in Coventry → ""Phoenix Way, Coventry CV6 6GX"")
- Set street_name to the actual street the POI is located on
- Set postal_code to the real postcode of that location
- The ""address"" field must contain the full postal address, not just the POI name

POSTCODE-ONLY INPUT (CRITICAL):
When the input is ONLY a postcode (e.g. ""CV6 6GX"" or ""B13 9NT""):
- You MUST resolve it to the FULL POSTAL STREET ADDRESS for that postcode
- Set street_name to the primary street for that postcode
- Set city to the city/town for that postcode
- The ""address"" field must be a full street address, not just the postcode

ABBREVIATED / PARTIAL STREET NAME MATCHING (CRITICAL):
Resolve partial names to real streets (e.g., ""Fargo"" → ""Fargosford Street"" in Coventry).

ADDRESS EXTRACTION RULES:
1. Preserve house numbers EXACTLY as spoken
2. Do NOT invent house numbers
3. Append detected city for clarity
4. ALWAYS include postal code in address field
5. INDEPENDENT POSTCODES: each address must have its own postal code

DISTRICT DISAMBIGUATION RULE — MULTI-DISTRICT STREETS (CRITICAL):
Many streets in dense UK cities (Birmingham, Coventry, Manchester, London, etc.) exist in 5+ different districts with completely different postcodes and GPS coordinates.
When a user provides a common street name WITHOUT a district, postcode, or high house number (500+):

1. Use your world knowledge to SEARCH ALL OCCURRENCES of that street in the detected city.
2. If MORE THAN ONE distinct location exists:
   - Set is_ambiguous = true
   - Set status = ""clarification_needed""
   - Populate districts_found with the SPECIFIC district/area names where this street exists (e.g. [""Moseley"", ""Handsworth"", ""Yardley Wood"", ""Acocks Green""])
   - Populate alternatives with the full formatted addresses for each district version
   - Set clarification_message to a natural question, e.g.: ""There are several School Roads in Birmingham. Is it the one in Moseley, Handsworth, or Yardley Wood?""
3. EXAMPLES of streets that ALWAYS need disambiguation in Birmingham: School Road, Church Road, Park Road, Station Road, High Street, Victoria Road, Albert Road, Green Lane, New Road, Church Lane, Grove Road, Mill Lane, Bristol Road, Pershore Road, Stratford Road
4. EXAMPLES of streets that ALWAYS need disambiguation in Coventry: Church Road, Station Road, High Street, Park Road, School Road, Victoria Road
5. If the caller provides a POSTCODE, DISTRICT NAME, or HIGH HOUSE NUMBER (500+) → resolve directly without asking.
6. If the caller's history contains a matching address → use it without asking.

HOUSE NUMBER DISAMBIGUATION: High house numbers (500+) strongly suggest long arterial roads.

GEOCODING RULES:
1. MUST provide lat/lon for EVERY resolved address (best-effort — coordinates will be independently verified via geocoding services)
2. Coordinates must be realistic (UK lat ~50-58, lon ~-6 to 2; NL lat ~51-53, lon ~3-7)
3. Extract structured components: street_name, street_number, postal_code, city
4. FOCUS on returning ACCURATE street names, postal codes, and city — these are MORE IMPORTANT than lat/lon as they are used for independent verification geocoding

""NEAREST X"" / RELATIVE POI RESOLUTION:
Resolve to the ACTUAL NEAREST real-world instance relative to the caller's location.

PICKUP TIME NORMALISATION:
When a pickup_time is provided, parse it into ISO 8601 UTC: ""YYYY-MM-DDTHH:MM:SSZ"".
Rules:
- ""now"", ""asap"", ""immediately"" → scheduled_at = null
- ""in X minutes/hours/days"" → REFERENCE + X
- Time only (""5pm"") → today; if past → tomorrow
- ""tonight"" → 21:00; ""this evening"" → 19:00; ""afternoon"" → 15:00; ""morning"" → 09:00
- ""tomorrow at 3pm"" → tomorrow 15:00
- ""this time tomorrow"" → +24h; ""same time next week"" → +7d
- Day-of-week: ""this Wed"" → nearest; ""next Wed"" → next week's Wed
- NO-PAST RULE: always roll forward if result < REFERENCE_DATETIME
- Not provided → scheduled_at = null";
}
