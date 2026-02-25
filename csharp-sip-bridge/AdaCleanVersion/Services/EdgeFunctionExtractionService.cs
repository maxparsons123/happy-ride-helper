using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Calls the taxi-extract-unified edge function with raw slot values
/// packaged as a synthetic conversation. Returns a StructuredBooking.
/// 
/// This is the SINGLE authoritative AI call — no mid-flow extraction.
/// AI acts as a pure normalization/structuring function.
/// </summary>
public class EdgeFunctionExtractionService : IExtractionService
{
    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public EdgeFunctionExtractionService(
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

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[Extraction] Starting single-pass extraction for raw slots");

            // Build a synthetic conversation from raw slots.
            // The edge function expects conversation messages — we present
            // the raw data as a structured Q&A so AI can normalize it.
            var conversation = BuildSyntheticConversation(request);

            var payload = new
            {
                conversation,
                caller_name = request.Context?.CallerPhone, // for alias lookup
                caller_phone = request.Context?.CallerPhone,
                caller_city = request.Context?.ServiceArea,
                is_modification = false
            };

            var url = $"{_supabaseUrl}/functions/v1/taxi-extract-unified";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("Authorization", $"Bearer {_serviceRoleKey}");
            httpRequest.Headers.Add("apikey", _serviceRoleKey);
            httpRequest.Content = JsonContent.Create(payload, options: JsonOpts);

            var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[Extraction] Edge function returned {Status}: {Body}",
                    response.StatusCode, responseBody);
                return new ExtractionResult
                {
                    Success = false,
                    Error = $"Extraction API error: {response.StatusCode}"
                };
            }

            // Parse the edge function response
            var extracted = JsonSerializer.Deserialize<ExtractedResponse>(responseBody, JsonOpts);
            if (extracted == null)
            {
                return new ExtractionResult
                {
                    Success = false,
                    Error = "Failed to parse extraction response"
                };
            }

            _logger.LogInformation(
                "[Extraction] Result: pickup={Pickup}, dest={Dest}, pax={Pax}, time={Time}, confidence={Conf}",
                extracted.Pickup, extracted.Destination, extracted.Passengers,
                extracted.PickupTime, extracted.Confidence);

            // Validate required fields
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(extracted.Pickup))
                warnings.Add("Pickup address could not be normalized");
            if (string.IsNullOrWhiteSpace(extracted.Destination))
                warnings.Add("Destination address could not be normalized");

            if (extracted.Confidence == "low")
                warnings.Add("Low confidence extraction — may need verification");

            // Build the authoritative structured booking
            var booking = new StructuredBooking
            {
                CallerName = request.Slots.Name,
                Pickup = ParseAddress(extracted.Pickup ?? request.Slots.Pickup),
                Destination = ParseAddress(extracted.Destination ?? request.Slots.Destination),
                Passengers = extracted.Passengers ?? ParsePassengers(request.Slots.Passengers),
                PickupTime = extracted.PickupTime ?? "ASAP",
                PickupDateTime = ParsePickupTime(extracted.PickupTime)
            };

            return new ExtractionResult
            {
                Success = true,
                Booking = booking,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Extraction] Failed");
            return new ExtractionResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Build synthetic conversation from raw slots.
    /// Presents raw data as a Q&A flow so the edge function can normalize.
    /// </summary>
    private static List<ConversationMessage> BuildSyntheticConversation(ExtractionRequest request)
    {
        var messages = new List<ConversationMessage>();

        // Name
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Text = "[CONTEXT: Ada asked: \"What is your name?\"]"
        });
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Text = request.Slots.Name
        });

        // Pickup
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Text = "[CONTEXT: Ada asked: \"Where would you like to be picked up?\"]"
        });
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Text = request.Slots.Pickup
        });

        // Destination
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Text = "[CONTEXT: Ada asked: \"What is your destination?\"]"
        });
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Text = request.Slots.Destination
        });

        // Passengers
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Text = "[CONTEXT: Ada asked: \"How many passengers?\"]"
        });
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Text = request.Slots.Passengers
        });

        // Pickup time
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Text = "[CONTEXT: Ada asked: \"When do you need the taxi?\"]"
        });
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Text = request.Slots.PickupTime
        });

        return messages;
    }

    /// <summary>
    /// Parse a raw address string into a StructuredAddress.
    /// Simple heuristic split — the AI already normalized it.
    /// </summary>
    private static StructuredAddress ParseAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new StructuredAddress();

        // Check for UK postcode at end
        string? postcode = null;
        var postcodeMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        var addressPart = raw;
        if (postcodeMatch.Success)
        {
            postcode = postcodeMatch.Value.Trim();
            addressPart = raw[..postcodeMatch.Index].Trim().TrimEnd(',');
        }

        // Try to extract house number from start
        string? houseNumber = null;
        string? streetName = null;
        var houseMatch = System.Text.RegularExpressions.Regex.Match(
            addressPart, @"^(\d+[A-Za-z]?)\s+(.+)$");

        if (houseMatch.Success)
        {
            houseNumber = houseMatch.Groups[1].Value;
            streetName = houseMatch.Groups[2].Value;
        }
        else
        {
            streetName = addressPart;
        }

        // Split remaining by comma for area/city
        string? area = null;
        string? city = null;
        if (streetName != null)
        {
            var parts = streetName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            streetName = parts[0];
            if (parts.Length > 2) { area = parts[1]; city = parts[2]; }
            else if (parts.Length > 1) { city = parts[1]; }
        }

        return new StructuredAddress
        {
            HouseNumber = houseNumber,
            StreetName = streetName,
            Area = area,
            City = city,
            Postcode = postcode
        };
    }

    private static int ParsePassengers(string raw)
    {
        // Try direct parse
        if (int.TryParse(raw, out var n)) return Math.Clamp(n, 1, 8);

        // Word-to-number
        var lower = raw.ToLowerInvariant().Trim();
        return lower switch
        {
            "one" => 1, "two" => 2, "three" => 3, "four" => 4,
            "five" => 5, "six" => 6, "seven" => 7, "eight" => 8,
            _ => 1
        };
    }

    private static DateTime? ParsePickupTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Equals("ASAP", StringComparison.OrdinalIgnoreCase)) return null;
        if (DateTime.TryParse(raw, out var dt)) return dt;
        return null;
    }
}

// ── DTOs for edge function communication ──

internal class ConversationMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal class ExtractedResponse
{
    [JsonPropertyName("pickup")]
    public string? Pickup { get; set; }

    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("passengers")]
    public int? Passengers { get; set; }

    [JsonPropertyName("pickup_time")]
    public string? PickupTime { get; set; }

    [JsonPropertyName("vehicle_type")]
    public string? VehicleType { get; set; }

    [JsonPropertyName("special_requests")]
    public string? SpecialRequests { get; set; }

    [JsonPropertyName("confidence")]
    public string? Confidence { get; set; }

    [JsonPropertyName("missing_fields")]
    public List<string>? MissingFields { get; set; }

    [JsonPropertyName("processing_time_ms")]
    public int? ProcessingTimeMs { get; set; }
}
