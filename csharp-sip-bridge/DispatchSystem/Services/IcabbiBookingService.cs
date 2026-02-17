using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DispatchSystem.Data;

namespace DispatchSystem.Services;

/// <summary>
/// Sends bookings to the iCabbi API, dispatches to drivers, and polls journey status.
/// Adapted from the WhatsURide BookingService for the DispatchSystem project.
/// </summary>
public sealed class IcabbiBookingService : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _appKey;
    private readonly string _secretKey;
    private readonly string _tenantBase;
    private readonly int _siteId;
    private readonly int _accountId;
    private readonly string _accountName;
    private readonly HttpClient _client;

    /// <summary>Tracks last known status per journey to avoid duplicate notifications.</summary>
    private readonly Dictionary<string, string> _lastStatus = new();

    /// <summary>Fires whenever a log-worthy event occurs.</summary>
    public event Action<string>? OnLog;

    /// <summary>Fires when a polled journey changes status (journeyId, oldStatus, newStatus).</summary>
    public event Action<string, string, string>? OnStatusChanged;

    // Human-readable status messages for notifications
    private static readonly Dictionary<string, string> StatusMessages = new()
    {
        { "DRIVER_ALLOCATED",  "✅ A driver has been assigned to your booking." },
        { "DRIVER_ON_WAY",     "🚗 Your driver is on the way!" },
        { "DRIVER_ARRIVED",    "📍 Your driver has arrived at pickup." },
        { "PASSENGER_ONBOARD", "🧍 You're on your way. Enjoy your trip!" },
        { "JOURNEY_COMPLETED", "🎉 Journey completed. Thank you for riding with us!" },
        { "CANCELLED",         "❌ Booking has been cancelled." }
    };

    public IcabbiBookingService(
        string appKey,
        string secretKey,
        string baseUrl = "https://api.icabbi.com/uk/",
        string tenantBase = "https://yourtenant.icabbi.net",
        int siteId = 1039,
        int accountId = 9428,
        string accountName = "DispatchSystem")
    {
        _appKey = appKey;
        _secretKey = secretKey;
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _tenantBase = tenantBase.TrimEnd('/');
        _siteId = siteId;
        _accountId = accountId;
        _accountName = accountName;

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DispatchSystem/1.0");
    }

    // ── Auth ──

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
        => journeyId.Length > 8
            ? $"{_baseUrl}v2/requests/{journeyId}"
            : $"{_baseUrl}bookings/get/{journeyId}";

    // ── Create & Dispatch ──

    /// <summary>
    /// Creates a booking on iCabbi and optionally dispatches it to a specific driver.
    /// </summary>
    public async Task<IcabbiBookingResult> CreateAndDispatchAsync(
        Job job,
        int? icabbiDriverId = null,
        bool allowDecline = true,
        CancellationToken ct = default)
    {
        try
        {
            Log("📝 Creating iCabbi booking...");

            // 1. Build booking payload
            var booking = new
            {
                source = "APP",
                date = DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                name = job.CallerName ?? "Customer",
                phone = job.CallerPhone ?? "",
                account_id = _accountId,
                account_name = _accountName,
                address = new
                {
                    formatted = job.Pickup,
                    lat = job.PickupLat,
                    lng = job.PickupLng
                },
                destination = new
                {
                    formatted = job.Dropoff,
                    lat = job.DropoffLat,
                    lng = job.DropoffLng
                },
                site_id = _siteId,
                status = "NEW",
                passengers = job.Passengers,
                notes = job.SpecialRequirements ?? ""
            };

            var bookingJson = JsonSerializer.Serialize(booking, JsonOpts);
            Log($"📤 Booking payload: {bookingJson}");

            using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}bookings/add")
            {
                Content = new StringContent(bookingJson, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(createReq);

            var createResp = await _client.SendAsync(createReq, ct);
            var createBody = await createResp.Content.ReadAsStringAsync(ct);
            Log($"📨 Booking response [{createResp.StatusCode}]: {Truncate(createBody)}");

            if (!createResp.IsSuccessStatusCode)
                return IcabbiBookingResult.Fail($"Booking creation failed: {createBody}");

            // 2. Parse response
            JsonNode? root;
            try { root = JsonNode.Parse(createBody); }
            catch (Exception ex)
            {
                Log($"❌ JSON parse error: {ex.Message}");
                return IcabbiBookingResult.Fail("Invalid JSON in booking response");
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
            Log($"🔗 Tracking: {trackingUrl}");

            // 3. Dispatch to driver (if specified)
            if (icabbiDriverId.HasValue)
            {
                var dispatchResult = await DispatchToDriverAsync(journeyId, icabbiDriverId.Value, allowDecline, ct);
                if (!dispatchResult.success)
                    return new IcabbiBookingResult(false, journeyId, trackingUrl, dispatchResult.message);

                Log($"🚕 Dispatched to iCabbi driver {icabbiDriverId.Value}");
            }

            return new IcabbiBookingResult(true, journeyId, trackingUrl, "Booking created successfully");
        }
        catch (Exception ex)
        {
            Log($"💥 CreateAndDispatchAsync error: {ex.Message}");
            return IcabbiBookingResult.Fail(ex.Message);
        }
    }

    /// <summary>Dispatch an existing journey to a specific iCabbi driver.</summary>
    public async Task<(bool success, string message)> DispatchToDriverAsync(
        string journeyId, int driverId, bool allowDecline = true, CancellationToken ct = default)
    {
        var dispatch = new { journey_id = journeyId, driver_id = driverId, allow_decline = allowDecline };
        var json = JsonSerializer.Serialize(dispatch, JsonOpts);
        Log($"🚗 Dispatch payload: {json}");

        using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}bookings/dispatchjourney")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddAuthHeaders(req);

        var resp = await _client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Log($"📨 Dispatch response [{resp.StatusCode}]: {Truncate(body)}");

        return resp.IsSuccessStatusCode
            ? (true, "Dispatched successfully")
            : (false, $"Dispatch failed: {body}");
    }

    // ── Status Polling ──

    /// <summary>Get raw booking/request JSON from iCabbi.</summary>
    public async Task<JsonNode?> GetBookingStatusAsync(string journeyId, CancellationToken ct = default)
    {
        var url = BuildStatusUrl(journeyId);
        Log($"🔍 Fetching status: {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeaders(req);

        var resp = await _client.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        Log($"📨 Status response [{resp.StatusCode}]: {Truncate(raw)}");

        try { return JsonNode.Parse(raw); }
        catch (Exception ex)
        {
            Log($"❌ Status JSON parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Extract the status string from a booking response.</summary>
    public static string ExtractStatus(JsonNode? data)
    {
        return data?["body"]?["booking"]?["status"]?.GetValue<string>()
            ?? data?["body"]?["status"]?.GetValue<string>()
            ?? "UNKNOWN";
    }

    /// <summary>Poll a journey's status at a fixed interval. Raises OnStatusChanged when status changes.</summary>
    public async Task PollJourneyAsync(string journeyId, int intervalSeconds = 10, CancellationToken ct = default)
    {
        Log($"🛰️ Starting poll for {journeyId} every {intervalSeconds}s");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var data = await GetBookingStatusAsync(journeyId, ct);
                var status = ExtractStatus(data);
                Log($"📡 {journeyId} status: {status}");

                if (_lastStatus.TryGetValue(journeyId, out var prev) && prev != status)
                {
                    OnStatusChanged?.Invoke(journeyId, prev, status);
                    if (StatusMessages.TryGetValue(status, out var msg))
                        Log($"📲 Status notification: {msg}");
                }

                _lastStatus[journeyId] = status;

                // Stop polling on terminal states
                if (status is "JOURNEY_COMPLETED" or "CANCELLED")
                {
                    Log($"🏁 Journey {journeyId} reached terminal state: {status}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Log($"⚠ Poll error for {journeyId}: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    // ── Passengers ──

    /// <summary>Retrieve passenger list from a booking.</summary>
    public async Task<List<IcabbiPassenger>> GetPassengersAsync(string journeyId, CancellationToken ct = default)
    {
        var passengers = new List<IcabbiPassenger>();

        try
        {
            var data = await GetBookingStatusAsync(journeyId, ct);
            if (data is null) return passengers;

            var paxNode = data["body"]?["booking"]?["passengers"]
                       ?? data["body"]?["passengers"]
                       ?? data["booking"]?["passengers"]
                       ?? data["passengers"];

            if (paxNode is not JsonArray paxArray) return passengers;

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

            Log($"👥 {passengers.Count} passengers for {journeyId}");
        }
        catch (Exception ex)
        {
            Log($"💥 GetPassengersAsync error: {ex.Message}");
        }

        return passengers;
    }

    // ── Event Listener Registration ──

    /// <summary>Register iCabbi event listener (realtime, if fleet supports it).</summary>
    public async Task<(bool success, string message)> RegisterEventListenerAsync(
        string journeyId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}bookings/register_event_listener/{journeyId}";
            Log($"🌍 Registering event listener: {url}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req);

            var resp = await _client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            Log($"📨 Event listener response [{resp.StatusCode}]: {Truncate(body)}");

            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {body}");

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch (Exception ex) { return (false, $"JSON parse error: {ex.Message}"); }

            var cfg = root?["body"]?["event_listener_config"];
            if (cfg is null)
                return (false, "No event_listener_config — realtime likely not available. Use polling.");

            var provider = cfg["real_time_provider"]?.GetValue<string>();
            var channel = cfg["channel"]?.GetValue<string>();
            var authKey = cfg["auth_key"]?.GetValue<string>();

            Log($"🔧 Listener: provider={provider}, channel={channel}");

            if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(authKey))
                return (false, "Realtime disabled for this fleet — use polling instead.");

            return (true, "Event listener registered successfully");
        }
        catch (Exception ex)
        {
            Log($"💥 RegisterEventListenerAsync error: {ex.Message}");
            return (false, ex.Message);
        }
    }

    // ── Helpers ──

    private void Log(string msg) => OnLog?.Invoke($"[iCabbi] {msg}");

    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public void Dispose() => _client?.Dispose();
}

// ── Result & DTO types ──

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
