using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Address extraction via Supabase edge function (Lovable AI Gateway).
/// Uses LOVABLE_API_KEY automatically - no API key needed from caller.
/// </summary>
public class EdgeAddressExtractor
{
    private readonly string _supabaseUrl;
    private readonly string _supabaseAnonKey;
    private readonly HttpClient _httpClient;

    public static Action<string>? OnLog;
    private static void Log(string msg) => OnLog?.Invoke(msg);

    public EdgeAddressExtractor(string supabaseUrl, string supabaseAnonKey)
    {
        _supabaseUrl = supabaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(supabaseUrl));
        _supabaseAnonKey = supabaseAnonKey ?? throw new ArgumentNullException(nameof(supabaseAnonKey));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Extract addresses using the address-dispatch edge function.
    /// </summary>
    public async Task<AddressExtractionResult?> ExtractAsync(
        string pickup,
        string destination,
        string? phoneNumber = null)
    {
        try
        {
            var payload = new { pickup, destination, phone = phoneNumber ?? "" };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/functions/v1/address-dispatch")
            {
                Content = content
            };
            request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

            Log($"🤖 [Edge] Extracting: pickup='{pickup}', dest='{destination}', phone='{phoneNumber}'");

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log($"⚠️ [Edge] HTTP {(int)response.StatusCode}: {responseBody}");
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var result = new AddressExtractionResult
            {
                DetectedArea = root.TryGetProperty("detected_area", out var area) ? area.GetString() : null,
                Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? "ready" : "ready"
            };

            // Parse phone analysis
            if (root.TryGetProperty("phone_analysis", out var phoneEl))
            {
                result.PhoneCountry = phoneEl.TryGetProperty("detected_country", out var c) ? c.GetString() : null;
                result.IsMobile = phoneEl.TryGetProperty("is_mobile", out var m) && m.GetBoolean();
                result.LandlineCity = phoneEl.TryGetProperty("landline_city", out var lc) ? lc.GetString() : null;
            }

            // Parse pickup
            if (root.TryGetProperty("pickup", out var pickupEl))
            {
                result.PickupAddress = pickupEl.TryGetProperty("address", out var a) ? a.GetString() : null;
                result.PickupAmbiguous = pickupEl.TryGetProperty("is_ambiguous", out var amb) && amb.GetBoolean();
                if (pickupEl.TryGetProperty("alternatives", out var alts))
                {
                    result.PickupAlternatives = alts.EnumerateArray()
                        .Select(x => x.GetString() ?? "")
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToArray();
                }
            }

            // Parse dropoff
            if (root.TryGetProperty("dropoff", out var dropoffEl))
            {
                result.DestinationAddress = dropoffEl.TryGetProperty("address", out var a) ? a.GetString() : null;
                result.DestinationAmbiguous = dropoffEl.TryGetProperty("is_ambiguous", out var amb) && amb.GetBoolean();
                if (dropoffEl.TryGetProperty("alternatives", out var alts))
                {
                    result.DestinationAlternatives = alts.EnumerateArray()
                        .Select(x => x.GetString() ?? "")
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToArray();
                }
            }

            LogResult(result);
            return result;
        }
        catch (Exception ex)
        {
            Log($"❌ [Edge] Error: {ex.Message}");
            return null;
        }
    }

    private static void LogResult(AddressExtractionResult result)
    {
        Log($"════════════════════════════════════════════════════");
        Log($"🤖 EDGE AI ADDRESS EXTRACTION RESULTS");
        Log($"────────────────────────────────────────────────────");
        Log($"📱 Phone: {result.PhoneCountry ?? "?"}, Mobile: {result.IsMobile}, City: {result.LandlineCity ?? "(none)"}");
        Log($"🌍 Detected Area: {result.DetectedArea}");
        Log($"📍 Pickup: {result.PickupAddress ?? "(empty)"} {(result.PickupAmbiguous ? "⚠️ AMBIGUOUS" : "")}");
        Log($"🏁 Dropoff: {result.DestinationAddress ?? "(empty)"} {(result.DestinationAmbiguous ? "⚠️ AMBIGUOUS" : "")}");
        Log($"📋 Status: {result.Status}");
        Log($"════════════════════════════════════════════════════");
    }
}

/// <summary>
/// Unified address extraction result - used by both Edge and Gemini extractors.
/// </summary>
public class AddressExtractionResult
{
    // Region detection
    public string? DetectedArea { get; set; }
    public string? RegionSource { get; set; }

    // Phone analysis
    public string? PhoneCountry { get; set; }
    public bool IsMobile { get; set; }
    public string? LandlineCity { get; set; }

    // Pickup
    public string? PickupAddress { get; set; }
    public string? PickupHouseNumber { get; set; }
    public string? PickupStreet { get; set; }
    public string? PickupCity { get; set; }
    public bool PickupAmbiguous { get; set; }
    public string[]? PickupAlternatives { get; set; }
    public double PickupConfidence { get; set; }

    // Destination
    public string? DestinationAddress { get; set; }
    public string? DestinationHouseNumber { get; set; }
    public string? DestinationStreet { get; set; }
    public string? DestinationCity { get; set; }
    public bool DestinationAmbiguous { get; set; }
    public string[]? DestinationAlternatives { get; set; }
    public double DestinationConfidence { get; set; }

    // Status
    public string Status { get; set; } = "ready";
    public string? ClarificationMessage { get; set; }

    /// <summary>
    /// True if status indicates addresses are ready for booking.
    /// </summary>
    public bool IsReady => Status == "ready" || Status == "ready_to_book";

    /// <summary>
    /// True if clarification is needed from the user.
    /// </summary>
    public bool NeedsClarification => Status == "clarification_needed" || Status == "clarification_required";
}
