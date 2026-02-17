using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaMain.Core;

namespace AdaMain.Services;

/// <summary>
/// Sends bookings to the iCabbi API from AdaMain.
/// Supports booking creation, dispatch, status polling, and passenger lookup.
/// </summary>
public sealed class IcabbiBookingService : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _appKey;
    private readonly string _secretKey;
    private readonly string _tenantBase;
    private readonly HttpClient _client;

    // Remember last status per journey so we only fire events on change
    private readonly Dictionary<string, string> _lastStatus = new();

    // Map iCabbi status codes → human-readable messages
    private static readonly Dictionary<string, string> StatusMessages = new()
    {
        { "DRIVER_ALLOCATED",  "✅ A driver has been assigned to your booking." },
        { "DRIVER_ON_WAY",     "🚗 Your driver is on the way!" },
        { "DRIVER_ARRIVED",    "📍 Your driver has arrived at pickup." },
        { "PASSENGER_ONBOARD", "🧍 You're on your way. Enjoy your trip!" },
        { "JOURNEY_COMPLETED", "🎉 Journey completed. Thank you for riding with us!" }
    };

    public event Action<string>? OnLog;

    /// <summary>Fired when a journey status changes during polling. Args: journeyId, newStatus, statusMessage.</summary>
    public event Action<string, string, string>? OnStatusChanged;

    public IcabbiBookingService(
        string appKey,
        string secretKey,
        string baseUrl = "https://api.icabbi.com/uk/",
        string tenantBase = "https://yourtenant.icabbi.net")
    {
        _appKey = appKey;
        _secretKey = secretKey;
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _tenantBase = tenantBase.TrimEnd('/');

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AdaMain/1.0");
    }

    private void AddAuthHeaders(HttpRequestMessage request, string? customerPhone = null)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_appKey}:{_secretKey}"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {encoded}");
        if (!string.IsNullOrWhiteSpace(customerPhone))
            request.Headers.TryAddWithoutValidation("Phone", customerPhone);
    }

    private string BuildTrackingUrl(string journeyId)
        => $"{_tenantBase}/passenger/tracking/{journeyId}";

    private string BuildStatusUrl(string journeyId)
    {
        // Booking IDs are around 7–8 digits; Request IDs are longer and live in v2
        if (journeyId.Length > 8)
            return $"{_baseUrl}v2/requests/{journeyId}";
        return $"{_baseUrl}bookings/get/{journeyId}";
    }

    // ═══════════════════════════════════════════
    //  CREATE & DISPATCH BOOKING
    // ═══════════════════════════════════════════

    /// <summary>
    /// Creates a booking on iCabbi from a BookingState and optionally dispatches to a driver.
    /// </summary>
    public async Task<IcabbiBookingResult> CreateAndDispatchAsync(
        BookingState booking,
        int? icabbiDriverId = null,
        CancellationToken ct = default)
    {
        try
        {
            Log("📝 Creating iCabbi booking...");

            var payload = new
            {
                source = "APP",
                date = DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                name = booking.Name ?? "Customer",
                phone = FormatE164(booking.CallerPhone),
                account_id = 9428,
                account_name = "WhatsUrRide",
                address = new
                {
                    formatted = booking.PickupFormatted ?? booking.Pickup ?? "",
                    lat = booking.PickupLat ?? 0,
                    lng = booking.PickupLon ?? 0
                },
                destination = new
                {
                    formatted = booking.DestFormatted ?? booking.Destination ?? "",
                    lat = booking.DestLat ?? 0,
                    lng = booking.DestLon ?? 0
                },
                site_id = 1039,
                status = "NEW"
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            Log($"📤 Booking Add Payload: {Truncate(json)}");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}bookings/add")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req);

            var resp = await _client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            Log($"📨 [Booking Response] {resp.StatusCode}: {Truncate(body)}");

            if (!resp.IsSuccessStatusCode)
                return IcabbiBookingResult.Fail($"Booking creation failed: {body}");

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return IcabbiBookingResult.Fail("Invalid JSON in booking response"); }

            // Check for API-level error (body may be [] or contain error info)
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

            Log($"✅ Booking created — Journey: {journeyId}");
            Log($"🔗 Tracking URL: {trackingUrl}");

            // Dispatch to driver if specified
            if (icabbiDriverId.HasValue)
            {
                var dispatch = new { journey_id = journeyId, driver_id = icabbiDriverId.Value, allow_decline = true };
                var dJson = JsonSerializer.Serialize(dispatch, JsonOpts);
                Log($"🚗 Dispatch Payload: {dJson}");

                using var dReq = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}bookings/dispatchjourney")
                {
                    Content = new StringContent(dJson, Encoding.UTF8, "application/json")
                };
                AddAuthHeaders(dReq);

                var dResp = await _client.SendAsync(dReq, ct);
                var dBody = await dResp.Content.ReadAsStringAsync(ct);
                Log($"📨 [Dispatch Response] {dResp.StatusCode}: {Truncate(dBody)}");

                if (!dResp.IsSuccessStatusCode)
                    return new IcabbiBookingResult(false, journeyId, trackingUrl, $"Dispatch failed: {dBody}");

                Log($"🚕 Journey {journeyId} successfully allocated to driver {icabbiDriverId.Value}");
            }

            return new IcabbiBookingResult(true, journeyId, trackingUrl, "Booking created successfully");
        }
        catch (Exception ex)
        {
            Log($"💥 CreateAndDispatchAsync error: {ex.Message}");
            return IcabbiBookingResult.Fail(ex.Message);
        }
    }

    // ═══════════════════════════════════════════
    //  GET BOOKING STATUS
    // ═══════════════════════════════════════════

    /// <summary>
    /// Returns the full JSON response for a journey/booking, or null on error.
    /// </summary>
    public async Task<JsonNode?> GetBookingStatusAsync(string journeyId, CancellationToken ct = default)
    {
        var url = BuildStatusUrl(journeyId);
        Log($"🔗 Fetching booking status: {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeaders(req);

        var resp = await _client.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        Log($"📨 RAW API RESPONSE ({journeyId}): {Truncate(raw)}");

        try { return JsonNode.Parse(raw); }
        catch { return null; }
    }

    // ═══════════════════════════════════════════
    //  POLL JOURNEY STATUS
    // ═══════════════════════════════════════════

    /// <summary>
    /// Continuously polls a journey for status changes. Fires OnStatusChanged when status transitions.
    /// Stops when journey completes or cancellation is requested.
    /// </summary>
    public async Task PollJourneyAsync(string journeyId, int intervalSeconds = 10, CancellationToken ct = default)
    {
        Log($"🛰️ Starting poll for journey {journeyId} (interval {intervalSeconds}s)");

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

                    Log($"📡 Journey {journeyId} status: {status}");

                    // Fire event on status change
                    if (!_lastStatus.TryGetValue(journeyId, out var prev) || prev != status)
                    {
                        _lastStatus[journeyId] = status;
                        var msg = StatusMessages.TryGetValue(status, out var m) ? m : $"Status: {status}";
                        OnStatusChanged?.Invoke(journeyId, status, msg);
                    }

                    // Stop polling on terminal statuses
                    if (status is "JOURNEY_COMPLETED" or "CANCELLED" or "NO_SHOW")
                    {
                        Log($"🏁 Journey {journeyId} reached terminal status: {status}");
                        break;
                    }
                }
                else
                {
                    Log($"⚠️ No JSON parsed for {journeyId}, continuing...");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"💥 Poll error for {journeyId}: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        Log($"🛑 Polling stopped for {journeyId}");
    }

    // ═══════════════════════════════════════════
    //  GET PASSENGERS
    // ═══════════════════════════════════════════

    /// <summary>
    /// Retrieves passenger details for a journey/booking.
    /// </summary>
    public async Task<List<IcabbiPassenger>> GetPassengersAsync(string journeyId, CancellationToken ct = default)
    {
        var passengers = new List<IcabbiPassenger>();

        try
        {
            var data = await GetBookingStatusAsync(journeyId, ct);
            if (data == null)
            {
                Log($"⚠️ No data returned for {journeyId}");
                return passengers;
            }

            // Try all known passenger locations in the response
            var paxNode =
                data["body"]?["booking"]?["passengers"] ??
                data["body"]?["passengers"] ??
                data["booking"]?["passengers"] ??
                data["passengers"];

            if (paxNode is not JsonArray paxArray)
            {
                Log($"⚠️ No passengers found in response for {journeyId}");
                return passengers;
            }

            foreach (var p in paxArray)
            {
                passengers.Add(new IcabbiPassenger
                {
                    Name = p?["name"]?.GetValue<string>() ?? "",
                    Phone = p?["phone"]?.GetValue<string>() ?? "",
                    Email = p?["email"]?.GetValue<string>() ?? "",
                    Count = p?["count"]?.GetValue<int>() ?? 1,
                    Notes = p?["notes"]?.GetValue<string>() ?? ""
                });
            }

            Log($"👥 Loaded {passengers.Count} passengers for {journeyId}");
        }
        catch (Exception ex)
        {
            Log($"💥 GetPassengersAsync({journeyId}) error: {ex.Message}");
        }

        return passengers;
    }

    // ═══════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════

    private void Log(string msg) => OnLog?.Invoke($"[iCabbi] {msg}");

    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Ensures the phone number is in E.164 format (e.g. +447539025332).
    /// </summary>
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

// ═══════════════════════════════════════════
//  MODELS
// ═══════════════════════════════════════════

public record IcabbiBookingResult(bool Success, string? JourneyId, string? TrackingUrl, string Message)
{
    public static IcabbiBookingResult Fail(string message) => new(false, null, null, message);
}

public class IcabbiPassenger
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public int Count { get; set; } = 1;
    public string Notes { get; set; } = "";
}
