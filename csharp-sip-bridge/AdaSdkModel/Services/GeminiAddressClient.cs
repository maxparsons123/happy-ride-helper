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

            // ── Step 4: Full post-processing (matching edge function) ──
            var result = await PostProcessAsync(parsed.Value, pickup, destination, callerHistory, phone);

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

    // ── Known multi-district streets (same as edge function) ──
    private static readonly Dictionary<string, string[]> KnownMultiDistrictStreets = new()
    {
        ["Birmingham"] = new[] { "School Road", "Church Road", "Park Road", "Station Road", "High Street", "Victoria Road", "Albert Road", "Green Lane", "New Road", "Warwick Road", "Church Lane", "Grove Road", "Mill Lane", "King Street", "Queen Street" },
        ["Coventry"] = new[] { "Church Road", "Station Road", "High Street", "Park Road", "School Road", "Victoria Road", "Albert Road", "Green Lane", "New Road" },
        ["London"] = new[] { "Church Road", "Station Road", "High Street", "Park Road", "School Road", "Victoria Road", "Albert Road", "Green Lane", "New Road" },
        ["Manchester"] = new[] { "Church Road", "Station Road", "High Street", "Park Road", "School Road", "Victoria Road", "Albert Road", "Green Lane", "New Road" },
    };

    private static readonly string[] KnownCities = { "Coventry", "Birmingham", "London", "Manchester", "Leeds", "Sheffield", "Nottingham", "Leicester", "Bristol", "Reading", "Liverpool", "Newcastle", "Brighton", "Oxford", "Cambridge", "Wolverhampton", "Derby", "Stoke", "Southampton", "Portsmouth", "Edinburgh", "Glasgow", "Cardiff", "Belfast", "Warwick", "Kenilworth", "Solihull", "Sutton Coldfield", "Leamington", "Rugby", "Nuneaton", "Bedworth", "Stratford", "Redditch", "Amsterdam", "Rotterdam", "The Hague", "Utrecht", "Eindhoven", "Gent", "Ghent", "Brussels", "Antwerp", "Bruges" };

    /// <summary>Extract street key for fuzzy comparison: "52a david road" from "52A David Road, Coventry CV1 2BW"</summary>
    private static string ExtractStreetKey(string input) =>
        System.Text.RegularExpressions.Regex.Replace(input, @"\b[a-z]{1,2}\d{1,2}\s?\d[a-z]{2}\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        .Split(',')[0].Trim().ToLowerInvariant();

    private async Task<JsonElement> PostProcessAsync(JsonElement parsed, string? pickup, string? destination, string callerHistory, string? phone)
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

        // ── Enforce disambiguation for known multi-district streets ──
        foreach (var side in new[] { "pickup", "dropoff" })
        {
            var addr = work[side];
            if (addr == null) continue;
            var isAmbiguous = addr["is_ambiguous"]?.GetValue<bool>() ?? false;
            if (isAmbiguous) continue;
            if (addr["matched_from_history"]?.GetValue<bool>() == true) continue;

            var city = addr["city"]?.GetValue<string>() ?? work["detected_area"]?.GetValue<string>() ?? "";
            var streetName = addr["street_name"]?.GetValue<string>() ?? "";
            if (!KnownMultiDistrictStreets.TryGetValue(city, out var knownStreets)) continue;

            var isKnown = knownStreets.Any(s => string.Equals(s, streetName, StringComparison.OrdinalIgnoreCase));
            if (!isKnown) continue;

            var originalInput = side == "pickup" ? (pickup ?? "") : (destination ?? "");
            var hasDistrict = originalInput.Split(',').Length > 2;
            var hasPostcode = System.Text.RegularExpressions.Regex.IsMatch(originalInput, @"\b[A-Z]{1,2}\d{1,2}\s?\d[A-Z]{2}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Check house number discrimination
            var streetNum = addr["street_number"]?.GetValue<string>() ?? "";
            var numMatch = System.Text.RegularExpressions.Regex.Match(streetNum, @"^(\d+)");
            var houseNumber = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : 0;

            if (!hasDistrict && !hasPostcode)
            {
                if (houseNumber >= 500)
                {
                    _logger.LogDebug("✅ House number {Num} discriminates \"{Street}\" in {City}", houseNumber, streetName, city);
                    continue;
                }

                _logger.LogInformation("⚠️ \"{Street}\" in {City} is multi-district — forcing disambiguation", streetName, city);
                addr["is_ambiguous"] = true;
                work["status"] = "clarification_needed";
                if (work["clarification_message"] == null || string.IsNullOrEmpty(work["clarification_message"]?.GetValue<string>()))
                    work["clarification_message"] = $"There are several {streetName}s in {city}. Which area or district is it in?";
            }
        }

        // ── Detect country for currency ──
        string detectedCountry = "UK";
        if (work["phone_analysis"]?["detected_country"] != null)
            detectedCountry = work["phone_analysis"]!["detected_country"]!.GetValue<string>();

        // ── Calculate fare from coordinates ──
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
                        addr["is_ambiguous"] = false;
                        addr["alternatives"] = new System.Text.Json.Nodes.JsonArray();
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

        var needsSanityCheck = (currentStatus == "clarification_needed" && noAlts) ||
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
Pickup STT Input: ""{pickup ?? ""}""
Pickup Geocoder Result: ""{pickupAddr ?? ""}"" (street: ""{pickupStreet ?? ""}"", city: {pickupCity ?? "unknown"})
Dropoff STT Input: ""{destination ?? ""}""
Dropoff Geocoder Result: ""{dropoffAddr ?? ""}"" (street: ""{dropoffStreet ?? ""}"", city: {dropoffCity ?? "unknown"})
Distance: {distMiles?.ToString("F1") ?? "unknown"} miles
Key question: Does the dropoff street name ""{dropoffStreet ?? ""}"" match what the user said ""{destination ?? ""}""? Are they the SAME street or DIFFERENT streets?";

            var sanitySystemPrompt = $@"You are a ""Taxi Address Sanity Guard."" You compare User Input (STT) vs. Geocoder Results (GEO).

CRITICAL: Compare the STREET NAME the user said vs the STREET NAME the geocoder returned.

Evaluation Criteria:
1. Street Name Phonetic Match: Compare character by character. ""Russell"" vs ""Rossville"" — DIFFERENT streets. ""528"" vs ""52A"" — same address.
2. Regional Bias: User is in {contextCity}. If GEO returns >20 miles away, MISMATCH.
3. STT Artifacts: ""NUX"" might be ""MAX"". But ""Rossville"" is NOT ""Russell"".
4. Fabrication Detection: If geocoder returned a phonetically similar but different street, MISMATCH.

Decision Rules:
- Street name core identity changed (Russell→Rossville): MISMATCH
- Only house number changed (528→52A): MATCH
- City changed to distant city: MISMATCH";

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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
