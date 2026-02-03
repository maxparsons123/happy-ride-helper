using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Standalone Gemini-based address extraction and dispatch service.
/// Uses the Google Gemini API directly for intelligent geographic disambiguation.
/// 
/// Biasing Logic:
/// 1. Landline area codes (e.g., +44 24 for Coventry) → strong geographic anchor
/// 2. Mobile numbers (+44 7) → no geographic clue, uses text context + destination inference
/// 3. Neighborhood/landmark detection → secondary anchor
/// </summary>
public class GeminiDispatchService
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private const string GeminiApiUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent";

    /// <summary>
    /// Optional log callback for debugging.
    /// </summary>
    public static Action<string>? OnLog;
    private static void Log(string msg) => OnLog?.Invoke(msg);

    // The System Instruction defines the "Brain" of the dispatcher (V3 Smart Dispatcher)
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
  - Set status: 'clarification_needed'

HOUSE NUMBER EXTRACTION:
- Extract house numbers EXACTLY as spoken (including letters like '52A', '15-17', '1/2')
- If no house number is provided, leave house_number as null - do NOT invent one
- Store house_number separately from street name

OUTPUT FORMAT (STRICT JSON - no markdown, no explanation):
{
  ""detected_region"": ""string (city name or 'Unknown')"",
  ""region_source"": ""landline_area_code|destination_landmark|neighborhood|text_mention|unknown"",
  ""phone_analysis"": {
    ""country"": ""UK|NL|Unknown"",
    ""is_mobile"": true/false,
    ""area_code"": ""string or null"",
    ""inferred_city"": ""string or null""
  },
  ""pickup"": {
    ""resolved"": true/false,
    ""address"": ""full address string"",
    ""house_number"": ""string or null"",
    ""street"": ""street name only"",
    ""city"": ""string or null"",
    ""confidence"": 0.0-1.0,
    ""is_ambiguous"": true/false,
    ""alternatives"": [""option1"", ""option2"", ""option3""]
  },
  ""destination"": {
    ""resolved"": true/false,
    ""address"": ""full address string"",
    ""house_number"": ""string or null"",
    ""street"": ""street name only"",
    ""city"": ""string or null"",
    ""confidence"": 0.0-1.0,
    ""is_ambiguous"": true/false,
    ""alternatives"": [""option1"", ""option2"", ""option3""]
  },
  ""status"": ""ready_to_book|clarification_required"",
  ""clarification_message"": ""string or null (question to ask user if clarification needed)""
}";

    public GeminiDispatchService(string apiKey)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Extract and resolve addresses using Gemini AI with intelligent geographic disambiguation.
    /// </summary>
    /// <param name="userMessage">The user's spoken message containing address info</param>
    /// <param name="phoneNumber">Caller's phone number for region detection (E.164 format preferred)</param>
    /// <returns>Structured dispatch response with resolved addresses</returns>
    public async Task<GeminiDispatchResponse?> GetDispatchDetailsAsync(string userMessage, string phoneNumber)
    {
        Log($"🤖 [Gemini] Extracting: message='{userMessage}', phone='{phoneNumber}'");

        try
        {
            // Build the request payload
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = $"User Message: {userMessage}\nUser Phone: {phoneNumber}" }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = SystemPrompt }
                    }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    temperature = 0.1  // Low temperature for consistent structured output
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
            var result = JsonSerializer.Deserialize<GeminiDispatchResponse>(textContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result != null)
            {
                LogResult(result);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Error: {ex.Message}");
            return null;
        }
    }

    private static void LogResult(GeminiDispatchResponse result)
    {
        Log($"════════════════════════════════════════════════════");
        Log($"🤖 GEMINI DISPATCH RESULTS (V3)");
        Log($"────────────────────────────────────────────────────");
        Log($"📱 Phone Analysis:");
        Log($"   Country: {result.PhoneAnalysis?.Country ?? "Unknown"}");
        Log($"   Is Mobile: {result.PhoneAnalysis?.IsMobile}");
        Log($"   Area Code: {result.PhoneAnalysis?.AreaCode ?? "(none)"}");
        Log($"   Inferred City: {result.PhoneAnalysis?.InferredCity ?? "(none)"}");
        Log($"────────────────────────────────────────────────────");
        Log($"🌍 Detected Region: {result.DetectedRegion}");
        Log($"   Source: {result.RegionSource}");
        Log($"────────────────────────────────────────────────────");
        Log($"📍 PICKUP:");
        Log($"   Resolved: {result.Pickup?.Resolved}");
        Log($"   Address: {result.Pickup?.Address ?? "(empty)"}");
        Log($"   House Number: '{result.Pickup?.HouseNumber ?? ""}'");
        Log($"   Street: '{result.Pickup?.Street ?? ""}'");
        Log($"   City: '{result.Pickup?.City ?? ""}'");
        Log($"   Confidence: {result.Pickup?.Confidence:P0}");
        Log($"   Ambiguous: {result.Pickup?.IsAmbiguous}");
        if (result.Pickup?.Alternatives?.Length > 0)
        {
            Log($"   Alternatives: {string.Join(", ", result.Pickup.Alternatives)}");
        }
        Log($"────────────────────────────────────────────────────");
        Log($"🏁 DESTINATION:");
        Log($"   Resolved: {result.Destination?.Resolved}");
        Log($"   Address: {result.Destination?.Address ?? "(empty)"}");
        Log($"   House Number: '{result.Destination?.HouseNumber ?? ""}'");
        Log($"   Street: '{result.Destination?.Street ?? ""}'");
        Log($"   City: '{result.Destination?.City ?? ""}'");
        Log($"   Confidence: {result.Destination?.Confidence:P0}");
        Log($"   Ambiguous: {result.Destination?.IsAmbiguous}");
        if (result.Destination?.Alternatives?.Length > 0)
        {
            Log($"   Alternatives: {string.Join(", ", result.Destination.Alternatives)}");
        }
        Log($"────────────────────────────────────────────────────");
        Log($"📋 Status: {result.Status}");
        if (result.Status == "clarification_required")
        {
            Log($"⚠️ CLARIFICATION NEEDED: {result.ClarificationMessage}");
        }
        Log($"════════════════════════════════════════════════════");
    }
}

// ============================================
// DATA MODELS
// ============================================

/// <summary>
/// Complete response from Gemini dispatch service.
/// </summary>
public class GeminiDispatchResponse
{
    [JsonPropertyName("detected_region")]
    public string? DetectedRegion { get; set; }

    [JsonPropertyName("region_source")]
    public string? RegionSource { get; set; }

    [JsonPropertyName("phone_analysis")]
    public PhoneAnalysis? PhoneAnalysis { get; set; }

    [JsonPropertyName("pickup")]
    public AddressDetail? Pickup { get; set; }

    [JsonPropertyName("destination")]
    public AddressDetail? Destination { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("clarification_message")]
    public string? ClarificationMessage { get; set; }
}

/// <summary>
/// Phone number analysis results.
/// </summary>
public class PhoneAnalysis
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("is_mobile")]
    public bool IsMobile { get; set; }

    [JsonPropertyName("area_code")]
    public string? AreaCode { get; set; }

    [JsonPropertyName("inferred_city")]
    public string? InferredCity { get; set; }
}

/// <summary>
/// Detailed address extraction result.
/// </summary>
public class AddressDetail
{
    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }

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
