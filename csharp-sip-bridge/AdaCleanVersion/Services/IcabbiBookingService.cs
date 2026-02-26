// Ported from AdaSdkModel — adapted for StructuredBooking + FareResult
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AdaCleanVersion.Config;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Sends bookings to the iCabbi API.
/// Accepts StructuredBooking + FareResult (native to AdaCleanVersion).
/// Supports booking creation, dispatch, cancellation, status polling, and tracking URLs.
/// </summary>
public sealed class IcabbiBookingService : IDisposable
{
    private readonly ILogger<IcabbiBookingService> _logger;
    private readonly IcabbiSettings _settings;
    private readonly SupabaseSettings _supabase;
    private readonly HttpClient _client;

    private readonly Dictionary<string, string> _lastStatus = new();

    private static readonly Dictionary<string, string> StatusMessages = new()
    {
        { "DRIVER_ALLOCATED",  "✅ A driver has been assigned to your booking." },
        { "DRIVER_ON_WAY",     "🚗 Your driver is on the way!" },
        { "DRIVER_ARRIVED",    "📍 Your driver has arrived at pickup." },
        { "PASSENGER_ONBOARD", "🧍 You're on your way. Enjoy your trip!" },
        { "JOURNEY_COMPLETED", "🎉 Journey completed. Thank you for riding with us!" }
    };

    /// <summary>Fired when a journey status changes during polling.</summary>
    public event Action<string, string, string>? OnStatusChanged;

    public IcabbiBookingService(
        ILogger<IcabbiBookingService> logger,
        IcabbiSettings settings,
        SupabaseSettings supabase)
    {
        _logger = logger;
        _settings = settings;
        _supabase = supabase;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AdaCleanVersion/1.0");
    }

    private string BaseUrl => $"https://api.icabbi.com/uk/";

    private void AddAuthHeaders(HttpRequestMessage request, string? customerPhone = null)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.AppKey}:{_settings.SecretKey}"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {encoded}");
        if (!string.IsNullOrWhiteSpace(customerPhone))
            request.Headers.TryAddWithoutValidation("Phone", customerPhone);
    }

    private string BuildTrackingUrl(string journeyId)
        => $"{_settings.TenantBase.TrimEnd('/')}/passenger/tracking/{journeyId}";

    // ═══════════════════════════════════════════
    //  CREATE & DISPATCH BOOKING
    // ═══════════════════════════════════════════

    /// <summary>
    /// Create an iCabbi booking from StructuredBooking + FareResult.
    /// </summary>
    public async Task<IcabbiBookingResult> CreateAndDispatchAsync(
        StructuredBooking booking,
        FareResult fare,
        string callerPhone,
        string? callerName = null,
        int? icabbiDriverId = null,
        CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[iCabbi] Disabled — skipping booking creation");
            return IcabbiBookingResult.Fail("iCabbi is disabled");
        }

        try
        {
            _logger.LogInformation("[iCabbi] Creating booking...");

            var payload = new
            {
                source = "APP",
                date = booking.IsAsap
                    ? DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : booking.PickupDateTime?.ToString("yyyy-MM-ddTHH:mm:ssZ")
                      ?? DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                name = callerName ?? booking.CallerName ?? "Customer",
                phone = FormatE164(callerPhone),
                account_id = 9428,
                account_name = "WhatsUrRide",
                address = new
                {
                    formatted = fare.Pickup.Address,
                    lat = fare.Pickup.Lat,
                    lng = fare.Pickup.Lon
                },
                destination = new
                {
                    formatted = fare.Destination.Address,
                    lat = fare.Destination.Lat,
                    lng = fare.Destination.Lon
                },
                site_id = _settings.SiteId > 0 ? _settings.SiteId : 1039,
                status = "NEW"
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            _logger.LogInformation("[iCabbi] Payload: {Json}", Truncate(json));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}bookings/add")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req);

            var resp = await _client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[iCabbi] Response {Status}: {Body}", resp.StatusCode, Truncate(body));

            if (!resp.IsSuccessStatusCode)
                return IcabbiBookingResult.Fail($"Booking creation failed: {body}");

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return IcabbiBookingResult.Fail("Invalid JSON in booking response"); }

            var isError = root?["error"]?.GetValue<bool>() ?? false;
            if (isError)
            {
                var errMsg = root?["message"]?.GetValue<string>() ?? "Unknown API error";
                var errCode = root?["code"]?.GetValue<int>() ?? 0;
                return IcabbiBookingResult.Fail($"iCabbi error {errCode}: {errMsg}");
            }

            var bookingNode = root?["body"]?["booking"];
            if (bookingNode is null)
                return IcabbiBookingResult.Fail("No body.booking in response");

            var journeyId = bookingNode["id"]?.GetValue<string>();
            var trackingUrl = bookingNode["tracking_url"]?.GetValue<string>()
                              ?? BuildTrackingUrl(journeyId ?? "");

            if (string.IsNullOrEmpty(journeyId))
                return IcabbiBookingResult.Fail("No journey ID in response");

            _logger.LogInformation("[iCabbi] ✅ Booking created — Journey: {JourneyId}, Tracking: {Url}",
                journeyId, trackingUrl);

            // Dispatch to specific driver if requested
            if (icabbiDriverId.HasValue)
            {
                var dispatch = new { journey_id = journeyId, driver_id = icabbiDriverId.Value, allow_decline = true };
                var dJson = JsonSerializer.Serialize(dispatch, JsonOpts);

                using var dReq = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}bookings/dispatchjourney")
                {
                    Content = new StringContent(dJson, Encoding.UTF8, "application/json")
                };
                AddAuthHeaders(dReq);

                var dResp = await _client.SendAsync(dReq, ct);
                var dBody = await dResp.Content.ReadAsStringAsync(ct);

                if (!dResp.IsSuccessStatusCode)
                    return new IcabbiBookingResult(false, journeyId, trackingUrl, $"Dispatch failed: {dBody}");

                _logger.LogInformation("[iCabbi] Journey {JourneyId} dispatched to driver {DriverId}",
                    journeyId, icabbiDriverId.Value);
            }

            return new IcabbiBookingResult(true, journeyId, trackingUrl, "Booking created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[iCabbi] CreateAndDispatchAsync error");
            return IcabbiBookingResult.Fail(ex.Message);
        }
    }

    // ═══════════════════════════════════════════
    //  CANCEL BOOKING
    // ═══════════════════════════════════════════

    public async Task<bool> CancelBookingAsync(string journeyId, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[iCabbi] Cancelling journey {JourneyId}", journeyId);

            var payload = JsonSerializer.Serialize(new { journey_id = journeyId, status = "CANCELLED" }, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}bookings/update")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req);

            var resp = await _client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[iCabbi] Cancel response {Status}: {Body}", resp.StatusCode, Truncate(body));
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[iCabbi] CancelBookingAsync error for {JourneyId}", journeyId);
            return false;
        }
    }

    // ═══════════════════════════════════════════
    //  GET BOOKING STATUS
    // ═══════════════════════════════════════════

    public async Task<JsonNode?> GetBookingStatusAsync(string journeyId, CancellationToken ct = default)
    {
        var url = journeyId.Length > 8
            ? $"{BaseUrl}v2/requests/{journeyId}"
            : $"{BaseUrl}bookings/get/{journeyId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeaders(req);

        var resp = await _client.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        try { return JsonNode.Parse(raw); }
        catch { return null; }
    }

    // ═══════════════════════════════════════════
    //  POLL JOURNEY STATUS
    // ═══════════════════════════════════════════

    public async Task PollJourneyAsync(string journeyId, int intervalSeconds = 10, CancellationToken ct = default)
    {
        _logger.LogInformation("[iCabbi] Starting poll for journey {JourneyId}", journeyId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var data = await GetBookingStatusAsync(journeyId, ct);
                if (data != null)
                {
                    var status =
                        data["body"]?["booking"]?["status"]?.GetValue<string>() ??
                        data["body"]?["status"]?.GetValue<string>() ??
                        "UNKNOWN";

                    if (!_lastStatus.TryGetValue(journeyId, out var prev) || prev != status)
                    {
                        _lastStatus[journeyId] = status;
                        var msg = StatusMessages.TryGetValue(status, out var m) ? m : $"Status: {status}";
                        OnStatusChanged?.Invoke(journeyId, status, msg);
                    }

                    if (status is "JOURNEY_COMPLETED" or "CANCELLED" or "NO_SHOW")
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[iCabbi] Poll error for {JourneyId}", journeyId); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ═══════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════

    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";

    private static string FormatE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        var digits = phone.Replace(" ", "").Replace("-", "");
        if (digits.StartsWith("+")) return digits;
        return "+" + digits;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public void Dispose() => _client?.Dispose();
}

/// <summary>iCabbi booking result.</summary>
public record IcabbiBookingResult(bool Success, string? JourneyId, string? TrackingUrl, string Message)
{
    public static IcabbiBookingResult Fail(string message) => new(false, null, null, message);
}
