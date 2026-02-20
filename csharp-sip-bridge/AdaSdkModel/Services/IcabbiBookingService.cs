using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AdaSdkModel.Core;

namespace AdaSdkModel.Services;

/// <summary>
/// Sends bookings to the iCabbi API from AdaSdkModel.
/// Aligned with the production WinForms ICabbiApiClient payload structure.
/// </summary>
public sealed class IcabbiBookingService : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _appKey;
    private readonly string _secretKey;
    private readonly string _tenantBase;
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
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WhatsURideApp/1.0");
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
        if (journeyId.Length > 8)
            return $"{_baseUrl}v2/requests/{journeyId}";
        return $"{_baseUrl}bookings/get/{journeyId}";
    }

    // ═══════════════════════════════════════════
    //  FARE QUOTE (pre-booking price lookup)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Calls the iCabbi API to get a fare quote for a trip WITHOUT committing a booking.
    /// Uses the same payload structure as CreateAndDispatchAsync but reads back fare/eta only.
    /// Returns null if the API is unreachable or the response doesn't include pricing.
    /// </summary>
    public async Task<IcabbiFareQuote?> GetFareQuoteAsync(
        BookingState booking,
        CancellationToken ct = default)
    {
        try
        {
            Log("💰 Requesting iCabbi fare quote...");

            var phone = "00" + (booking.CallerPhone?.Replace("+", "").Replace(" ", "").Replace("-", "") ?? "");
            var seats = Math.Max(1, booking.Passengers ?? 1);

            var quotePayload = new IcabbiBookingRequest
            {
                date = booking.ScheduledAt ?? DateTime.UtcNow.AddMinutes(1),
                name = booking.Name ?? "Customer",
                phone = phone,
                extras = "WHATSAPP",
                seats = seats,
                source = "APP",
                vehicle_group = "Taxi",
                site_id = 71,
                status = "QUOTE",   // signals intent — quote only, not committed
                address = new IcabbiAddressDto
                {
                    lat = booking.PickupLat ?? 0,
                    lng = booking.PickupLon ?? 0,
                    formatted = booking.PickupFormatted ?? booking.Pickup ?? ""
                },
                destination = new IcabbiAddressDto
                {
                    lat = booking.DestLat ?? 0,
                    lng = booking.DestLon ?? 0,
                    formatted = booking.DestFormatted ?? booking.Destination ?? ""
                }
            };
            quotePayload.AssignVehicleType();

            var json = JsonSerializer.Serialize(quotePayload, JsonOpts);
            Log($"📤 Quote payload:\n{json}");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}bookings/add")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req, phone);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8)); // tight timeout — don't hold up the call

            var resp = await SendWithRetryAsync(req, cts.Token, "FareQuote", maxRetries: 1);
            var body = await resp.Content.ReadAsStringAsync(ct);
            Log($"📨 [Quote Response] {resp.StatusCode}: {Truncate(body)}");

            if (!resp.IsSuccessStatusCode)
            {
                Log($"⚠️ Quote HTTP {resp.StatusCode} — using Gemini estimate");
                return null;
            }

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return null; }

            // iCabbi returns fare in body.booking.payment or body.booking.price
            var bookingNode = root?["body"]?["booking"];
            if (bookingNode is null) return null;

            // Try payment.total first, then price, then cost
            var total = bookingNode["payment"]?["total"]?.GetValue<decimal?>()
                     ?? bookingNode["payment"]?["price"]?.GetValue<decimal?>()
                     ?? bookingNode["price"]?.GetValue<decimal?>()
                     ?? bookingNode["cost"]?.GetValue<decimal?>();

            if (total is null || total == 0) return null;

            var etaMinutes = bookingNode["eta"]?.GetValue<int?>()
                          ?? bookingNode["eta_minutes"]?.GetValue<int?>();

            var journeyId = bookingNode["id"]?.GetValue<int>()?.ToString();

            Log($"✅ Quote received: £{total:F2}, ETA={etaMinutes?.ToString() ?? "unknown"} min");
            return new IcabbiFareQuote(total.Value, etaMinutes, journeyId);
        }
        catch (OperationCanceledException)
        {
            Log("⏱️ Quote request timed out — using Gemini estimate");
            return null;
        }
        catch (Exception ex)
        {
            Log($"⚠️ Quote error (non-fatal): {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════
    //  CREATE & DISPATCH BOOKING
    // ═══════════════════════════════════════════

    public async Task<IcabbiBookingResult> CreateAndDispatchAsync(
        BookingState booking,
        int? icabbiDriverId = null,
        int? icabbiVehicleId = null,
        CancellationToken ct = default)
    {
        try
        {
            Log("📝 Creating iCabbi booking...");

            var phone = "00" + (booking.CallerPhone?.Replace("+", "").Replace(" ", "").Replace("-", "") ?? "");
            var seats = (booking.Passengers ?? 1) > 0 ? (booking.Passengers ?? 1) : 1;
            var fareDecimal = booking.FareDecimal;

            // ── Build payload exactly like WinForms ICabbiApiClient ──
            var icabbiBooking = new IcabbiBookingRequest
            {
                date = booking.ScheduledAt ?? DateTime.UtcNow.AddMinutes(1),
                name = booking.Name ?? "Customer",
                phone = phone,
                extras = "WHATSAPP",
                seats = seats,
                instructions = booking.SpecialInstructions ?? "No special instructions",
                address = new IcabbiAddressDto
                {
                    lat = booking.PickupLat ?? 0,
                    lng = booking.PickupLon ?? 0,
                    formatted = booking.PickupFormatted ?? FormatStandardAddress(booking.PickupCity, booking.PickupStreet, booking.PickupNumber, booking.Pickup)
                },
                destination = new IcabbiAddressDto
                {
                    lat = booking.DestLat ?? 0,
                    lng = booking.DestLon ?? 0,
                    formatted = booking.DestFormatted ?? FormatStandardAddress(booking.DestCity, booking.DestStreet, booking.DestNumber, booking.Destination)
                },
                payment = new IcabbiPaymentDto
                {
                    cost = fareDecimal,
                    price = fareDecimal,
                    total = fareDecimal
                }
            };

            // Assign vehicle type based on seats (same as WinForms AssignVehicleType)
            icabbiBooking.AssignVehicleType();

            // ── Post-assign fields (same as WinForms CreateAndAssignBookingAsync) ──
            var driverId = icabbiDriverId ?? 2222;
            var vehicleId = icabbiVehicleId ?? 2222;
            icabbiBooking.driver_id = driverId;
            icabbiBooking.vehicle_id = vehicleId;
            icabbiBooking.vehicle_ref = $"DRV{driverId}_VEH{vehicleId}";
            icabbiBooking.site_id = 71;
            icabbiBooking.source = "APP";
            icabbiBooking.vehicle_group = "Taxi";
            icabbiBooking.vehicle_type ??= "R4";
            icabbiBooking.status = "NEW";

            icabbiBooking.app_metadata = new IcabbiAppMetadataDto
            {
                extras = "WHATSAPP",
                whatsapp_number = phone,
                source_system = "AdaSdkModel",
                journey_id = Guid.NewGuid().ToString(),
                created_at = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(icabbiBooking, JsonOpts);
            Log($"📤 Booking Add Payload:\n{json}");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}bookings/add")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req, phone);

            var resp = await SendWithRetryAsync(req, ct, "CreateBooking");
            var body = await resp.Content.ReadAsStringAsync(ct);
            Log($"📨 [Booking Response] {resp.StatusCode}: {Truncate(body)}");

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

            var jobId = bookingNode["id"]?.GetValue<int>();
            var journeyId = jobId?.ToString() ?? "";
            var tripId = bookingNode["trip_id"]?.GetValue<string>();
            var permaId = bookingNode["perma_id"]?.GetValue<string>();
            var statusStr = bookingNode["status"]?.GetValue<string>();
            var trackingUrl = bookingNode["tracking_url"]?.GetValue<string>()
                              ?? BuildTrackingUrl(journeyId);

            if (string.IsNullOrEmpty(journeyId))
                return IcabbiBookingResult.Fail("No journey ID in response");

            Log($"✅ Booking created — JobId: {journeyId}, TripId: {tripId}, Status: {statusStr}");
            Log($"🔗 Tracking URL: {trackingUrl}");

            return new IcabbiBookingResult(true, journeyId, trackingUrl, tripId, permaId, "Booking created successfully");
        }
        catch (Exception ex)
        {
            Log($"💥 CreateAndDispatchAsync error: {ex.Message}");
            return IcabbiBookingResult.Fail(ex.Message);
        }
    }

    // ═══════════════════════════════════════════
    //  DISPATCH JOURNEY TO DRIVER
    // ═══════════════════════════════════════════

    public async Task<(bool success, string message)> DispatchJourneyAsync(
        int journeyId, int driverId, bool allowDecline = true, CancellationToken ct = default)
    {
        var payload = new { journey_id = journeyId, driver_id = driverId, allow_decline = allowDecline };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        Log($"🚚 Dispatch Payload: {json}");

        using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}bookings/dispatchjourney")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddAuthHeaders(req);

        var resp = await SendWithRetryAsync(req, ct, "DispatchJourney");
        var body = await resp.Content.ReadAsStringAsync(ct);
        Log($"📨 [Dispatch Response] {resp.StatusCode}: {Truncate(body)}");

        return resp.IsSuccessStatusCode
            ? (true, body)
            : (false, $"HTTP {resp.StatusCode}: {body}");
    }

    // ═══════════════════════════════════════════
    //  CANCEL BOOKING
    // ═══════════════════════════════════════════

    public async Task<(bool success, string message)> CancelBookingAsync(
        string journeyId, string? reason = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(journeyId))
            return (false, "No journeyId provided to cancel.");

        var payload = new { reason = reason ?? "Cancelled via Voice AI" };
        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}bookings/cancel/{journeyId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddAuthHeaders(req);

        Log($"🧨 Cancelling booking {journeyId}");

        var resp = await SendWithRetryAsync(req, ct, "CancelBooking");
        var body = await resp.Content.ReadAsStringAsync(ct);
        Log($"📨 [Cancel Response] {resp.StatusCode}: {Truncate(body)}");

        return resp.IsSuccessStatusCode
            ? (true, body)
            : (false, $"HTTP {resp.StatusCode}: {body}");
    }

    // ═══════════════════════════════════════════
    //  GET BOOKING STATUS
    // ═══════════════════════════════════════════

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

                    if (!_lastStatus.TryGetValue(journeyId, out var prev) || prev != status)
                    {
                        _lastStatus[journeyId] = status;
                        var msg = StatusMessages.TryGetValue(status, out var m) ? m : $"Status: {status}";
                        OnStatusChanged?.Invoke(journeyId, status, msg);
                    }

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
    //  HTTP RETRY LOGIC
    // ═══════════════════════════════════════════

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request, CancellationToken ct, string operationName, int maxRetries = 3)
    {
        HttpResponseMessage? lastResponse = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Clone for retry (can't reuse HttpRequestMessage)
                using var clone = await CloneRequestAsync(request);
                var response = await _client.SendAsync(clone, ct);

                var code = (int)response.StatusCode;
                if ((code >= 500 || code == 408) && attempt < maxRetries)
                {
                    lastResponse = response;
                    Log($"⚠️ [{operationName}] Transient HTTP {response.StatusCode}, retry {attempt}/{maxRetries}...");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }

                return response;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                Log($"⚠️ [{operationName}] Timeout, retry {attempt}/{maxRetries}...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                Log($"⚠️ [{operationName}] Network error, retry {attempt}/{maxRetries}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        return lastResponse ?? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (original.Content != null)
        {
            var ms = new MemoryStream();
            await original.Content.CopyToAsync(ms);
            ms.Position = 0;
            var content = new StreamContent(ms);
            foreach (var header in original.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        return clone;
    }

    // ═══════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════

    private void Log(string msg) => OnLog?.Invoke($"[iCabbi] {msg}");

    private static string FormatStandardAddress(string? city, string? street, string? number, string? rawFallback)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(city)) parts.Add(city);
        if (!string.IsNullOrWhiteSpace(street))
        {
            var streetPart = street;
            if (!string.IsNullOrWhiteSpace(number))
                streetPart += " " + number;
            parts.Add(streetPart);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : rawFallback ?? "";
    }

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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public void Dispose() => _client?.Dispose();
}

// ═══════════════════════════════════════════
//  MODELS (aligned with production ICabbiApiClient)
// ═══════════════════════════════════════════

/// <summary>
/// Lightweight fare quote returned by GetFareQuoteAsync.
/// JourneyId is populated if iCabbi created a draft booking — caller should cancel it after use.
/// </summary>
public record IcabbiFareQuote(decimal FareDecimal, int? EtaMinutes, string? JourneyId)
{
    public string FareFormatted => $"£{FareDecimal:F2}";
    public string EtaFormatted => EtaMinutes.HasValue ? $"{EtaMinutes} minutes" : "10 minutes";
}

public record IcabbiBookingResult(
    bool Success, string? JourneyId, string? TrackingUrl,
    string? TripId, string? PermaId, string Message)
{
    public static IcabbiBookingResult Fail(string message)
        => new(false, null, null, null, null, message);
}

public class IcabbiAddressDto
{
    public double lat { get; set; }
    public double lng { get; set; }
    public string formatted { get; set; } = "";
}

public class IcabbiPaymentDto
{
    [JsonPropertyName("fixed")]
    public int fixed_ { get; set; } = 1;
    public decimal cost { get; set; }
    public decimal price { get; set; }
    public double tip { get; set; } = 0.0;
    public double tolls { get; set; } = 0.0;
    public double extras { get; set; } = 0.0;
    public decimal total { get; set; }
}

public class IcabbiAppMetadataDto
{
    public string? created_by { get; set; }
    public string? whatsapp_number { get; set; }
    public string? source_system { get; set; }
    public string? journey_id { get; set; }
    public string? created_at { get; set; }
    public string? extras { get; set; }
}

public class IcabbiBookingRequest
{
    public IcabbiAppMetadataDto? app_metadata { get; set; }
    public string source { get; set; } = "APP";
    public string source_partner { get; set; } = "";
    public string source_version { get; set; } = "";
    public DateTime date { get; set; }
    public string name { get; set; } = "";
    public string phone { get; set; } = "";
    public string flight_number { get; set; } = "";
    public IcabbiPaymentDto? payment { get; set; }
    public int account_id { get; set; } = 9428;
    public string account_name { get; set; } = "WhatsUrRide";
    public IcabbiAddressDto? address { get; set; }
    public IcabbiAddressDto? destination { get; set; }
    public int seats { get; set; } = 1;
    public string vehicle_type { get; set; } = "R4";
    public string vehicle_group { get; set; } = "Taxi";
    public string instructions { get; set; } = "No special instructions";
    public string notes { get; set; } = "";
    public string id { get; set; } = "";
    public int site_id { get; set; } = 14;
    public string status { get; set; } = "NEW";
    public string extras { get; set; } = "";
    public string language { get; set; } = "en-GB";
    public int driver_id { get; set; }
    public int vehicle_id { get; set; }
    public string? vehicle_ref { get; set; }

    public void AssignVehicleType()
    {
        if (seats <= 4) vehicle_type = "R4";
        else if (seats <= 6) vehicle_type = "R6";
        else vehicle_type = "R7";
    }
}

public class IcabbiPassenger
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public int Count { get; set; } = 1;
    public string Notes { get; set; } = "";
}
