using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Looks up returning callers from the callers table via Supabase REST API.
/// Populates CallerContext with name, aliases, last addresses, booking count.
/// </summary>
public class CallerLookupService
{
    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly ILogger _logger;

    public CallerLookupService(
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

    /// <summary>
    /// Look up caller by phone number. Returns enriched CallerContext.
    /// If not found, returns a minimal context with IsReturningCaller = false.
    /// </summary>
    public async Task<CallerContext> LookupAsync(string phoneNumber, CancellationToken ct = default)
    {
        try
        {
            // Query callers table via PostgREST
            var url = $"{_supabaseUrl}/rest/v1/callers" +
                      $"?phone_number=eq.{Uri.EscapeDataString(phoneNumber)}" +
                      "&select=name,phone_number,last_pickup,last_destination,total_bookings,last_booking_at,preferred_language,address_aliases" +
                      "&limit=1";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _serviceRoleKey);
            request.Headers.Add("Authorization", $"Bearer {_serviceRoleKey}");

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[CallerLookup] API returned {Status} for {Phone}",
                    response.StatusCode, phoneNumber);
                return NewCallerContext(phoneNumber);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var callers = JsonSerializer.Deserialize<List<CallerRecord>>(body, JsonOpts);

            if (callers == null || callers.Count == 0)
            {
                _logger.LogInformation("[CallerLookup] New caller: {Phone}", phoneNumber);
                return NewCallerContext(phoneNumber);
            }

            var caller = callers[0];
            _logger.LogInformation(
                "[CallerLookup] Returning caller: {Name} ({Phone}), {Bookings} bookings, aliases: {Aliases}",
                caller.Name, phoneNumber, caller.TotalBookings,
                caller.AddressAliases != null ? string.Join(", ", ParseAliases(caller.AddressAliases).Keys) : "none");

            var aliases = caller.AddressAliases != null ? ParseAliases(caller.AddressAliases) : null;

            return new CallerContext
            {
                CallerPhone = phoneNumber,
                IsReturningCaller = true,
                CallerName = caller.Name,
                PreferredLanguage = caller.PreferredLanguage,
                LastPickup = caller.LastPickup,
                LastDestination = caller.LastDestination,
                TotalBookings = caller.TotalBookings,
                LastBookingAt = caller.LastBookingAt,
                AddressAliases = aliases
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CallerLookup] Failed for {Phone}", phoneNumber);
            return NewCallerContext(phoneNumber);
        }
    }

    /// <summary>
    /// Parse address_aliases JSON which can be {"home":"addr"} or {"Aliases":{"home":"addr"}}.
    /// </summary>
    private static Dictionary<string, string> ParseAliases(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (element.ValueKind != JsonValueKind.Object)
            return result;

        // Check for nested {"Aliases": {...}} format
        if (element.TryGetProperty("Aliases", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in nested.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString()!;
            }
            return result;
        }

        // Flat format {"home": "address"}
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                result[prop.Name] = prop.Value.GetString()!;
        }

        return result;
    }

    private static CallerContext NewCallerContext(string phone) => new()
    {
        CallerPhone = phone,
        IsReturningCaller = false,
        TotalBookings = 0
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

/// <summary>DTO matching the callers table row.</summary>
internal class CallerRecord
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("phone_number")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("last_pickup")]
    public string? LastPickup { get; set; }

    [JsonPropertyName("last_destination")]
    public string? LastDestination { get; set; }

    [JsonPropertyName("total_bookings")]
    public int TotalBookings { get; set; }

    [JsonPropertyName("last_booking_at")]
    public DateTime? LastBookingAt { get; set; }

    [JsonPropertyName("preferred_language")]
    public string? PreferredLanguage { get; set; }

    [JsonPropertyName("address_aliases")]
    public JsonElement? AddressAliases { get; set; }
}
