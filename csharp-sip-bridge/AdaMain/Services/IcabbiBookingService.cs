using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaMain.Core;

namespace AdaMain.Services;

/// <summary>
/// Sends bookings to the iCabbi API from AdaMain.
/// </summary>
public sealed class IcabbiBookingService : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _appKey;
    private readonly string _secretKey;
    private readonly string _tenantBase;
    private readonly HttpClient _client;

    public event Action<string>? OnLog;

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
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AdaMain/1.0");
    }

    private AuthenticationHeaderValue AuthHeader()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_appKey}:{_secretKey}"));
        return new AuthenticationHeaderValue("Basic", encoded);
    }

    private string BuildTrackingUrl(string journeyId)
        => $"{_tenantBase}/passenger/tracking/{journeyId}";

    /// <summary>
    /// Creates a booking on iCabbi from a BookingState.
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
                phone = "",
                account_id = 9428,
                account_name = "AdaMain",
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
                status = "NEW",
                passengers = booking.Passengers ?? 1,
                notes = ""
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            Log($"📤 Payload: {Truncate(json)}");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}bookings/add")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = AuthHeader();

            var resp = await _client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            Log($"📨 Response [{resp.StatusCode}]: {Truncate(body)}");

            if (!resp.IsSuccessStatusCode)
                return IcabbiBookingResult.Fail($"Booking creation failed: {body}");

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return IcabbiBookingResult.Fail("Invalid JSON in response"); }

            var bookingNode = root?["body"]?["booking"];
            if (bookingNode is null)
                return IcabbiBookingResult.Fail("No body.booking in response");

            var journeyId = bookingNode["id"]?.GetValue<string>();
            var trackingUrl = bookingNode["tracking_url"]?.GetValue<string>()
                              ?? BuildTrackingUrl(journeyId ?? "");

            if (string.IsNullOrEmpty(journeyId))
                return IcabbiBookingResult.Fail("No journey ID in response");

            Log($"✅ Booking created — Journey: {journeyId}");

            // Dispatch to driver if specified
            if (icabbiDriverId.HasValue)
            {
                var dispatch = new { journey_id = journeyId, driver_id = icabbiDriverId.Value, allow_decline = true };
                var dJson = JsonSerializer.Serialize(dispatch, JsonOpts);

                using var dReq = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}bookings/dispatchjourney")
                {
                    Content = new StringContent(dJson, Encoding.UTF8, "application/json")
                };
                dReq.Headers.Authorization = AuthHeader();

                var dResp = await _client.SendAsync(dReq, ct);
                var dBody = await dResp.Content.ReadAsStringAsync(ct);
                Log($"📨 Dispatch [{dResp.StatusCode}]: {Truncate(dBody)}");

                if (!dResp.IsSuccessStatusCode)
                    return new IcabbiBookingResult(false, journeyId, trackingUrl, $"Dispatch failed: {dBody}");
            }

            return new IcabbiBookingResult(true, journeyId, trackingUrl, "Booking created successfully");
        }
        catch (Exception ex)
        {
            Log($"💥 Error: {ex.Message}");
            return IcabbiBookingResult.Fail(ex.Message);
        }
    }

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

public record IcabbiBookingResult(bool Success, string? JourneyId, string? TrackingUrl, string Message)
{
    public static IcabbiBookingResult Fail(string message) => new(false, null, null, message);
}
