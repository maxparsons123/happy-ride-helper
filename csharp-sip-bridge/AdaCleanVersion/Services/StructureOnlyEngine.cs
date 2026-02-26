using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Structure-only normalization engine.
/// 
/// Calls OpenAI with JSON structured output to normalize raw slot values
/// into a clean StructuredBooking. Supports both new bookings and updates.
///
/// Key principles:
/// - NEVER decides intent (new/update/cancel) — CallStateEngine does that
/// - NEVER controls workflow — just returns normalized data
/// - Preserves verbatim house numbers and addresses
/// - Only normalizes time format and passenger count
/// </summary>
public class StructureOnlyEngine : IExtractionService
{
    private const string Model = "gpt-4o-mini";
    private const int TimeoutMs = 8000;

    private static readonly Uri Endpoint = new("https://api.openai.com/v1/chat/completions");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string _openAiApiKey;
    private readonly ILogger _logger;

    public StructureOnlyEngine(
        string openAiApiKey,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        _openAiApiKey = openAiApiKey;
        _logger = logger;
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    // ─── IExtractionService: New Booking ──────────────────────

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("[StructureOnly] Normalizing new booking");

        var rawInput = new RawBookingInput
        {
            PickupRaw = request.Slots.Pickup,
            DropoffRaw = request.Slots.Destination,
            PassengersRaw = request.Slots.Passengers,
            PickupTimeRaw = request.Slots.PickupTime,
            SpecialRaw = null,
            LuggageRaw = null
        };

        var result = await NormalizeAsync(rawInput, existing: null, request.AdaTranscriptContext, ct);
        return ToExtractionResult(result, request.Slots.Name);
    }

    // ─── IExtractionService: Update Booking ───────────────────

    public async Task<ExtractionResult> ExtractUpdateAsync(
        ExtractionRequest request,
        StructuredBooking existingBooking,
        IReadOnlySet<string> changedSlots,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[StructureOnly] Normalizing update for slots: {Slots}",
            string.Join(", ", changedSlots));

        // Build raw input with ONLY the changed fields
        var rawInput = new RawBookingInput
        {
            PickupRaw = changedSlots.Contains("pickup") ? request.Slots.Pickup : null,
            DropoffRaw = changedSlots.Contains("destination") ? request.Slots.Destination : null,
            PassengersRaw = changedSlots.Contains("passengers") ? request.Slots.Passengers : null,
            PickupTimeRaw = changedSlots.Contains("pickup_time") ? request.Slots.PickupTime : null
        };

        // Build existing context from the current booking
        var existing = new ExistingBookingContext
        {
            PickupLocation = existingBooking.Pickup.DisplayName,
            DropoffLocation = existingBooking.Destination.DisplayName,
            PickupTime = existingBooking.PickupTime,
            NumberOfPassengers = existingBooking.Passengers
        };

        var result = await NormalizeAsync(rawInput, existing, request.AdaTranscriptContext, ct);
        return MergeUpdateResult(result, existingBooking, request.Slots.Name);
    }

    // ─── Core Normalization ───────────────────────────────────

    private async Task<NormalizedBooking?> NormalizeAsync(
        RawBookingInput raw,
        ExistingBookingContext? existing,
        string? adaContext,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        var isUpdate = existing != null;
        var systemPrompt = BuildSystemPrompt(isUpdate);
        var userPayload = BuildUserPayload(raw, existing, adaContext);

        // Serialize schema with default naming (camelCase) to preserve "additionalProperties" exactly.
        // The snake_case policy in JsonOpts would corrupt it to "additional_properties".
        var schemaJson = JsonSerializer.Serialize(NormalizedBookingSchema);
        var schemaElement = JsonSerializer.Deserialize<JsonElement>(schemaJson);

        var payload = new
        {
            model = Model,
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPayload }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "normalized_booking",
                    strict = true,
                    schema = schemaElement
                }
            }
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOpts),
                    Encoding.UTF8,
                    "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey);

            using var resp = await _httpClient.SendAsync(req, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(cts.Token);
                _logger.LogError("[StructureOnly] API error {Status}: {Body}",
                    resp.StatusCode, errBody);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                return null;

            var result = JsonSerializer.Deserialize<NormalizedBooking>(content, JsonOpts);

            _logger.LogInformation(
                "[StructureOnly] Normalized: pickup={Pickup}, dest={Dest}, pax={Pax}, time={Time}",
                result?.PickupLocation, result?.DropoffLocation,
                result?.NumberOfPassengers, result?.PickupTime);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[StructureOnly] Normalization timed out ({Ms}ms)", TimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StructureOnly] Normalization failed");
            return null;
        }
    }

    // ─── Prompt Construction ──────────────────────────────────

    private static string BuildSystemPrompt(bool isUpdate)
    {
        // Inject London time so the AI can resolve relative time expressions
        var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var londonNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, londonTz);
        var referenceDateTime = londonNow.ToString("dddd, dd MMMM yyyy HH:mm");

        if (!isUpdate)
        {
            return $"""
                You are a STRICT booking data normalizer for a UK taxi company.

                REFERENCE_DATETIME (London): {referenceDateTime}

                You receive TWO inputs:
                1. RAW INPUT: Slot values from caller speech-to-text (may contain STT errors)
                2. ADA'S CONFIRMED INTERPRETATION: What the AI agent said back to the caller (source of truth)

                PRIORITY: If Ada's interpretation differs from raw input, ALWAYS prefer Ada's version.
                Ada has already corrected STT errors (e.g., "528" → "52A", "war road" → "Warwick Road").

                You MUST:
                • Keep addresses VERBATIM from Ada's interpretation — preserve exact house numbers, flat numbers, spelling
                • Extract passenger count as integer (1-8)
                • Normalize time into: "ASAP" or "YYYY-MM-DD HH:MM" (24h format, UK timezone)
                  - "now", "straight away", "right away", "immediately", "asap", "in a bit" → "ASAP"
                  - "tomorrow at 5pm" → calculate from REFERENCE_DATETIME → e.g. "2026-02-27 17:00"
                  - "in 30 minutes", "in an hour" → add offset to REFERENCE_DATETIME
                  - "half seven", "half past 7" → 07:30 or 19:30 — if REFERENCE_DATETIME is before 12:00, assume AM; if after 12:00, assume PM. If that time has already passed today, use tomorrow.
                  - "quarter to 5" → 16:45 (PM default for taxi bookings unless "am"/"morning" specified)
                  - "Monday at 3pm" → calculate next Monday from REFERENCE_DATETIME
                  - "first thing tomorrow", "first thing in the morning" → next day 07:00
                  - "tonight", "this evening" → today 20:00 (or next day if already past 20:00)
                  - "after work", "end of day" → today 17:30 (or tomorrow if past)
                  - "when the pubs close" → today/tomorrow 23:30
                  - ALWAYS use the REFERENCE_DATETIME above to compute absolute dates/times
                • Set time_warning if the resolved time is in the past or ambiguous (e.g. "Did you mean 7:30 AM or 7:30 PM?")
                • Extract luggage info if present
                • Extract special requests if present

                You MUST NOT:
                • Guess missing data — return null for missing fields
                • Infer or add city names not explicitly stated
                • Modify address spelling or house numbers
                • Add postcodes not explicitly provided
                • Strip or normalize house numbers (keep "52A" as "52A", "8th" as "8th")
                • Return raw time phrases like "tomorrow" — ALWAYS resolve to absolute datetime

                Return ONLY valid JSON matching the schema.
                """;
        }

        return $"""
            You are a STRICT booking UPDATE normalizer for a UK taxi company.

            REFERENCE_DATETIME (London): {referenceDateTime}

            You will receive:
            • Changed raw fields (the user wants to update these)
            • Existing booking context (current booking state)

            You MUST:
            • Only normalize the fields that have raw values provided
            • Return null for fields that were NOT changed
            • Keep addresses VERBATIM — preserve exact house numbers, spelling
            • Normalize time into: "ASAP" or "YYYY-MM-DD HH:MM"
              - Use REFERENCE_DATETIME above to compute absolute dates/times
              - "now", "straight away", "immediately", "in a bit" → "ASAP"
              - Relative phrases ("in 30 minutes", "tomorrow at 5") → resolved absolute datetime
              - UK colloquialisms: "half seven" → 07:30/19:30 based on time of day, "first thing" → 07:00
            • Set time_warning if the resolved time is in the past or ambiguous

            You MUST NOT:
            • Re-copy existing fields into the output
            • Guess missing values
            • Modify unchanged fields
            • Infer information not explicitly stated

            Return ONLY valid JSON matching the schema.
            """;
    }

    private static string BuildUserPayload(
        RawBookingInput raw, ExistingBookingContext? existing, string? adaContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RAW INPUT:");
        sb.AppendLine(JsonSerializer.Serialize(raw, JsonOpts));

        if (!string.IsNullOrWhiteSpace(adaContext))
        {
            sb.AppendLine();
            sb.AppendLine("ADA'S CONFIRMED INTERPRETATION (source of truth — prefer these values over raw input if they differ):");
            sb.AppendLine(adaContext);
        }

        if (existing != null)
        {
            sb.AppendLine();
            sb.AppendLine("EXISTING BOOKING:");
            sb.AppendLine(JsonSerializer.Serialize(existing, JsonOpts));
        }

        return sb.ToString();
    }

    // ─── Result Mapping ───────────────────────────────────────

    private ExtractionResult ToExtractionResult(NormalizedBooking? normalized, string callerName)
    {
        if (normalized == null)
        {
            return new ExtractionResult
            {
                Success = false,
                Error = "Normalization returned no result"
            };
        }

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(normalized.PickupLocation))
            warnings.Add("Pickup address could not be normalized");
        if (string.IsNullOrWhiteSpace(normalized.DropoffLocation))
            warnings.Add("Destination address could not be normalized");
        if (!string.IsNullOrWhiteSpace(normalized.TimeWarning))
            warnings.Add($"Time: {normalized.TimeWarning}");

        var booking = new StructuredBooking
        {
            CallerName = callerName,
            Pickup = ParseAddress(normalized.PickupLocation ?? ""),
            Destination = ParseAddress(normalized.DropoffLocation ?? ""),
            Passengers = normalized.NumberOfPassengers ?? 1,
            PickupTime = normalized.PickupTime ?? "ASAP",
            PickupDateTime = ParsePickupTime(normalized.PickupTime)
        };

        return new ExtractionResult
        {
            Success = true,
            Booking = booking,
            Warnings = warnings
        };
    }

    private ExtractionResult MergeUpdateResult(
        NormalizedBooking? normalized,
        StructuredBooking existingBooking,
        string callerName)
    {
        if (normalized == null)
        {
            return new ExtractionResult
            {
                Success = false,
                Error = "Update normalization returned no result"
            };
        }

        // Merge: only overwrite fields that came back non-null
        var booking = new StructuredBooking
        {
            CallerName = callerName,
            Pickup = !string.IsNullOrWhiteSpace(normalized.PickupLocation)
                ? ParseAddress(normalized.PickupLocation)
                : existingBooking.Pickup,
            Destination = !string.IsNullOrWhiteSpace(normalized.DropoffLocation)
                ? ParseAddress(normalized.DropoffLocation)
                : existingBooking.Destination,
            Passengers = normalized.NumberOfPassengers ?? existingBooking.Passengers,
            PickupTime = normalized.PickupTime ?? existingBooking.PickupTime,
            PickupDateTime = normalized.PickupTime != null
                ? ParsePickupTime(normalized.PickupTime)
                : existingBooking.PickupDateTime
        };

        return new ExtractionResult
        {
            Success = true,
            Booking = booking,
            Warnings = new List<string>()
        };
    }

    // ─── Address / Time Parsing ───────────────────────────────

    private static StructuredAddress ParseAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new StructuredAddress();

        // Extract UK postcode at end
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

        // Extract house number from start
        string? houseNumber = null;
        string? streetName;
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

        // Split by comma for area/city
        string? area = null;
        string? city = null;
        var parts = streetName.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        streetName = parts[0];
        if (parts.Length > 2) { area = parts[1]; city = parts[2]; }
        else if (parts.Length > 1) { city = parts[1]; }

        return new StructuredAddress
        {
            HouseNumber = houseNumber,
            StreetName = streetName,
            Area = area,
            City = city,
            Postcode = postcode
        };
    }

    private static DateTime? ParsePickupTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Equals("ASAP", StringComparison.OrdinalIgnoreCase)) return null;
        if (DateTime.TryParse(raw, out var dt)) return dt;
        return null;
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    // ─── JSON Schema for Structured Output ────────────────────

    private static readonly object NormalizedBookingSchema = new
    {
        type = "object",
        properties = new
        {
            pickup_location = new { type = new[] { "string", "null" } },
            dropoff_location = new { type = new[] { "string", "null" } },
            pickup_time = new { type = new[] { "string", "null" } },
            time_warning = new { type = new[] { "string", "null" } },
            number_of_passengers = new { type = new[] { "integer", "null" } },
            luggage = new { type = new[] { "string", "null" } },
            special_requests = new { type = new[] { "string", "null" } }
        },
        required = new[]
        {
            "pickup_location", "dropoff_location", "pickup_time", "time_warning",
            "number_of_passengers", "luggage", "special_requests"
        },
        additionalProperties = false
    };
}

// ─── DTOs ─────────────────────────────────────────────────

/// <summary>Raw verbatim slot values from the conversation.</summary>
public class RawBookingInput
{
    [JsonPropertyName("pickup_raw")]
    public string? PickupRaw { get; set; }

    [JsonPropertyName("dropoff_raw")]
    public string? DropoffRaw { get; set; }

    [JsonPropertyName("pickup_time_raw")]
    public string? PickupTimeRaw { get; set; }

    [JsonPropertyName("passengers_raw")]
    public string? PassengersRaw { get; set; }

    [JsonPropertyName("luggage_raw")]
    public string? LuggageRaw { get; set; }

    [JsonPropertyName("special_raw")]
    public string? SpecialRaw { get; set; }
}

/// <summary>Current booking state passed for update operations.</summary>
public class ExistingBookingContext
{
    [JsonPropertyName("pickup_location")]
    public string? PickupLocation { get; set; }

    [JsonPropertyName("dropoff_location")]
    public string? DropoffLocation { get; set; }

    [JsonPropertyName("pickup_time")]
    public string? PickupTime { get; set; }

    [JsonPropertyName("number_of_passengers")]
    public int? NumberOfPassengers { get; set; }

    [JsonPropertyName("luggage")]
    public string? Luggage { get; set; }

    [JsonPropertyName("special_requests")]
    public string? SpecialRequests { get; set; }
}

/// <summary>AI-normalized booking output (matches JSON schema).</summary>
internal class NormalizedBooking
{
    [JsonPropertyName("pickup_location")]
    public string? PickupLocation { get; set; }

    [JsonPropertyName("dropoff_location")]
    public string? DropoffLocation { get; set; }

    [JsonPropertyName("pickup_time")]
    public string? PickupTime { get; set; }

    [JsonPropertyName("time_warning")]
    public string? TimeWarning { get; set; }

    [JsonPropertyName("number_of_passengers")]
    public int? NumberOfPassengers { get; set; }

    [JsonPropertyName("luggage")]
    public string? Luggage { get; set; }

    [JsonPropertyName("special_requests")]
    public string? SpecialRequests { get; set; }
}
