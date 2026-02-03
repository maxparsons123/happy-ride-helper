using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Direct Gemini API address extraction with intelligent geographic disambiguation.
/// Uses phone number patterns (landline vs mobile) to detect caller region.
/// </summary>
public class GeminiAddressExtractor
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private const string GeminiApiUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent";

    public static Action<string>? OnLog;
    private static void Log(string msg) => OnLog?.Invoke(msg);

    private const string SystemPrompt = @"
Role: You are a professional, high-intelligence Taxi Dispatch Logic System.

Objective: Extract pickup and drop-off addresses from user speech. Resolve geographic ambiguity using text context, landline area codes, or mobile-to-destination logic.

RULES FOR GEOGRAPHIC BIASING (Hierarchy of Evidence):

1. THE LANDLINE ANCHOR: If the phone number is a UK landline, use the area code to determine the city:
   - +44 24 or 024 = Coventry
   - +44 121 or 0121 = Birmingham  
   - +44 20 or 020 = London
   - +44 161 or 0161 = Manchester
   - +44 113 or 0113 = Leeds
   Prioritize results in that city when a landline is detected.

2. THE MOBILE/INTERNATIONAL ANCHOR (+44 7 or 07): Mobile numbers provide NO geographic clue. In this case:
   - Prioritize the city mentioned in the user's text
   - If no city is mentioned in the pickup, but one is mentioned in the drop-off (e.g., 'to Coventry Station'), assume the pickup is also in or near that city
   - Look for neighborhood names (e.g., 'Spon End', 'Earlsdon', 'Tile Hill') that are unique to certain cities

3. THE TEXTUAL ANCHOR: Always scan for:
   - Explicit city names (Coventry, Birmingham, London, etc.)
   - Unique neighborhood/area names that lock the location
   - Landmarks (train stations, airports, hospitals, universities)

AMBIGUITY PROTOCOL:
- If a street exists in multiple locations AND the phone is mobile (+44 7) with no city mentioned, you MUST:
  - Set is_ambiguous: true
  - Provide the top 3 most likely options based on population density
  - Set status: 'clarification_required'

HOUSE NUMBER EXTRACTION:
- Extract house numbers EXACTLY as spoken (including letters like '52A', '15-17', '1/2')
- If no house number is provided, leave house_number as null - do NOT invent one
- Store house_number separately from street name

OUTPUT FORMAT (STRICT JSON - no markdown, no explanation):
{
  ""detected_area"": ""string (city name or 'Unknown')"",
  ""region_source"": ""landline_area_code|destination_landmark|neighborhood|text_mention|unknown"",
  ""phone_analysis"": {
    ""detected_country"": ""UK|NL|Unknown"",
    ""is_mobile"": true/false,
    ""landline_city"": ""string or null""
  },
  ""pickup"": {
    ""address"": ""full address string"",
    ""house_number"": ""string or null"",
    ""street"": ""street name only"",
    ""city"": ""string or null"",
    ""confidence"": 0.0-1.0,
    ""is_ambiguous"": true/false,
    ""alternatives"": [""option1"", ""option2"", ""option3""]
  },
  ""dropoff"": {
    ""address"": ""full address string"",
    ""house_number"": ""string or null"",
    ""street"": ""street name only"",
    ""city"": ""string or null"",
    ""confidence"": 0.0-1.0,
    ""is_ambiguous"": true/false,
    ""alternatives"": [""option1"", ""option2"", ""option3""]
  },
  ""status"": ""ready|clarification_required"",
  ""clarification_message"": ""string or null (question to ask user if clarification needed)""
}";

    public GeminiAddressExtractor(string apiKey)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Extract addresses using Gemini AI with intelligent geographic disambiguation.
    /// </summary>
    public async Task<AddressExtractionResult?> ExtractAsync(
        string pickup,
        string destination,
        string? phoneNumber = null)
    {
        Log($"🤖 [Gemini] Extracting: pickup='{pickup}', dest='{destination}', phone='{phoneNumber}'");

        try
        {
            var userMessage = $"Pickup: {pickup}\nDestination: {destination}";
            
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = $"User Message: {userMessage}\nUser Phone: {phoneNumber ?? ""}" } }
                    }
                },
                systemInstruction = new
                {
                    parts = new[] { new { text = SystemPrompt } }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    temperature = 0.1
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{GeminiApiUrl}?key={_apiKey}";
            var response = await _httpClient.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log($"⚠️ [Gemini] HTTP {(int)response.StatusCode}: {responseBody}");
                return null;
            }

            // Parse Gemini response
            using var doc = JsonDocument.Parse(responseBody);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0)
            {
                Log("⚠️ [Gemini] No candidates returned");
                return null;
            }

            var textContent = candidates[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrEmpty(textContent))
            {
                Log("⚠️ [Gemini] Empty response text");
                return null;
            }

            // Parse the structured JSON response
            var geminiResult = JsonSerializer.Deserialize<GeminiRawResponse>(textContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (geminiResult == null) return null;

            // Convert to unified result format
            var result = new AddressExtractionResult
            {
                DetectedArea = geminiResult.DetectedArea,
                RegionSource = geminiResult.RegionSource,
                Status = geminiResult.Status ?? "ready"
            };

            // Phone analysis
            if (geminiResult.PhoneAnalysis != null)
            {
                result.PhoneCountry = geminiResult.PhoneAnalysis.DetectedCountry;
                result.IsMobile = geminiResult.PhoneAnalysis.IsMobile;
                result.LandlineCity = geminiResult.PhoneAnalysis.LandlineCity;
            }

            // Pickup
            if (geminiResult.Pickup != null)
            {
                result.PickupAddress = geminiResult.Pickup.Address;
                result.PickupHouseNumber = geminiResult.Pickup.HouseNumber;
                result.PickupStreet = geminiResult.Pickup.Street;
                result.PickupCity = geminiResult.Pickup.City;
                result.PickupAmbiguous = geminiResult.Pickup.IsAmbiguous;
                result.PickupAlternatives = geminiResult.Pickup.Alternatives;
                result.PickupConfidence = geminiResult.Pickup.Confidence;
            }

            // Dropoff
            if (geminiResult.Dropoff != null)
            {
                result.DestinationAddress = geminiResult.Dropoff.Address;
                result.DestinationHouseNumber = geminiResult.Dropoff.HouseNumber;
                result.DestinationStreet = geminiResult.Dropoff.Street;
                result.DestinationCity = geminiResult.Dropoff.City;
                result.DestinationAmbiguous = geminiResult.Dropoff.IsAmbiguous;
                result.DestinationAlternatives = geminiResult.Dropoff.Alternatives;
                result.DestinationConfidence = geminiResult.Dropoff.Confidence;
            }

            result.ClarificationMessage = geminiResult.ClarificationMessage;

            LogResult(result);
            return result;
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Error: {ex.Message}");
            return null;
        }
    }

    private static void LogResult(AddressExtractionResult result)
    {
        Log($"════════════════════════════════════════════════════");
        Log($"🤖 GEMINI AI ADDRESS EXTRACTION RESULTS");
        Log($"────────────────────────────────────────────────────");
        Log($"📱 Phone: {result.PhoneCountry ?? "?"}, Mobile: {result.IsMobile}, City: {result.LandlineCity ?? "(none)"}");
        Log($"🌍 Detected Area: {result.DetectedArea} (source: {result.RegionSource})");
        Log($"────────────────────────────────────────────────────");
        Log($"📍 PICKUP:");
        Log($"   Address: {result.PickupAddress ?? "(empty)"}");
        Log($"   House Number: '{result.PickupHouseNumber ?? ""}'");
        Log($"   Street: '{result.PickupStreet ?? ""}'");
        Log($"   City: '{result.PickupCity ?? ""}'");
        Log($"   Confidence: {result.PickupConfidence:P0}");
        Log($"   Ambiguous: {result.PickupAmbiguous}");
        if (result.PickupAlternatives?.Length > 0)
            Log($"   Alternatives: {string.Join(", ", result.PickupAlternatives)}");
        Log($"────────────────────────────────────────────────────");
        Log($"🏁 DESTINATION:");
        Log($"   Address: {result.DestinationAddress ?? "(empty)"}");
        Log($"   House Number: '{result.DestinationHouseNumber ?? ""}'");
        Log($"   Street: '{result.DestinationStreet ?? ""}'");
        Log($"   City: '{result.DestinationCity ?? ""}'");
        Log($"   Confidence: {result.DestinationConfidence:P0}");
        Log($"   Ambiguous: {result.DestinationAmbiguous}");
        if (result.DestinationAlternatives?.Length > 0)
            Log($"   Alternatives: {string.Join(", ", result.DestinationAlternatives)}");
        Log($"────────────────────────────────────────────────────");
        Log($"📋 Status: {result.Status}");
        if (result.NeedsClarification)
            Log($"⚠️ CLARIFICATION NEEDED: {result.ClarificationMessage}");
        Log($"════════════════════════════════════════════════════");
    }

    // Internal models for Gemini JSON parsing
    private class GeminiRawResponse
    {
        [JsonPropertyName("detected_area")]
        public string? DetectedArea { get; set; }

        [JsonPropertyName("region_source")]
        public string? RegionSource { get; set; }

        [JsonPropertyName("phone_analysis")]
        public GeminiPhoneAnalysis? PhoneAnalysis { get; set; }

        [JsonPropertyName("pickup")]
        public GeminiAddressDetail? Pickup { get; set; }

        [JsonPropertyName("dropoff")]
        public GeminiAddressDetail? Dropoff { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("clarification_message")]
        public string? ClarificationMessage { get; set; }
    }

    private class GeminiPhoneAnalysis
    {
        [JsonPropertyName("detected_country")]
        public string? DetectedCountry { get; set; }

        [JsonPropertyName("is_mobile")]
        public bool IsMobile { get; set; }

        [JsonPropertyName("landline_city")]
        public string? LandlineCity { get; set; }
    }

    private class GeminiAddressDetail
    {
        [JsonPropertyName("address")]
        public string? Address { get; set; }

        [JsonPropertyName("house_number")]
        public string? HouseNumber { get; set; }

        [JsonPropertyName("street")]
        public string? Street { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("is_ambiguous")]
        public bool IsAmbiguous { get; set; }

        [JsonPropertyName("alternatives")]
        public string[]? Alternatives { get; set; }
    }
}
