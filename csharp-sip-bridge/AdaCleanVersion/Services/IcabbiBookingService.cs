// Ported from AdaSdkModel v2.8 — adapted for StructuredBooking + FareResult
// Full iCabbi integration: fare quotes, create/dispatch, cancel, update, status polling
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
/// Full iCabbi API client — ported from AdaSdkModel production.
/// Accepts StructuredBooking + FareResult (native to AdaCleanVersion).
/// Supports: fare quotes, booking creation, dispatch, cancel, update, status polling.
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
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AdaCleanVersion/2.0");
    }

    private string BaseUrl => "https://api.icabbi.com/uk/";

    private void AddAuthHeaders(HttpRequestMessage request, string? customerPhone = null)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.AppKey}:{_settings.SecretKey}"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {encoded}");
        if (!string.IsNullOrWhiteSpace(customerPhone))
            request.Headers.TryAddWithoutValidation("Phone", customerPhone);
    }

    private string BuildTrackingUrl(string journeyId)
        => $"{_settings.TenantBase.TrimEnd('/')}/passenger/tracking/{journeyId}";

    private string BuildStatusUrl(string journeyId)
    {
        if (journeyId.Length > 8)
            return $"{BaseUrl}v2/requests/{journeyId}";
        return $"{BaseUrl}bookings/get/{journeyId}";
    }

    // ═══════════════════════════════════════════
    //  FARE QUOTE (dedicated /bookings/quote endpoint)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Calls the iCabbi dedicated quote endpoint to get a fare estimate WITHOUT creating a booking.
    /// Uses the /bookings/quote payload with locations array, multi-quote flag, and prebooking flag.
    /// Returns null if the API is unreachable or the response doesn't include pricing.
    /// </summary>
    public async Task<IcabbiFareQuote?> GetFareQuoteAsync(
        FareResult fareData,
        int passengers = 1,
        DateTime? scheduledAt = null,
        CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("[iCabbi] Disabled — skipping fare quote");
            return null;
        }

        try
        {
            _logger.LogInformation("[iCabbi] Requesting fare quote...");

            var seats = Math.Max(1, passengers);
            var vehicleType = seats <= 4 ? "R4" : seats <= 6 ? "R6" : seats <= 7 ? "R7" : "R8";
            var siteId = _settings.SiteId > 0 ? _settings.SiteId : 1039;

            // Scheduled prebooking = 1, ASAP = 0
            var isPrebooking = scheduledAt.HasValue ? 1 : 0;
            var pickupDate = (scheduledAt ?? DateTime.UtcNow.AddMinutes(2))
                                 .ToString("yyyy-MM-dd HH:mm:ss");

            var quotePayload = new IcabbiQuoteRequest
            {
                src = "APP",
                site_id = siteId,
                prebooking = isPrebooking,
                passengers = seats,
                vehicle_type = vehicleType,
                vehicle_group = "ANY VEHICLE",
                multi_quote = 1,
                date = pickupDate,
                locations = new List<string>
                {
                    $"{fareData.Pickup.Lat},{fareData.Pickup.Lon}",
                    $"{fareData.Destination.Lat},{fareData.Destination.Lon}"
                },
                postcode = fareData.Pickup.PostalCode?.Replace(" ", "") ?? "",
                destination_postcode = fareData.Destination.PostalCode?.Replace(" ", "") ?? ""
            };

            var json = JsonSerializer.Serialize(quotePayload, JsonOpts);
            _logger.LogInformation("[iCabbi] Quote payload: {Json}", Truncate(json, 500));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/quote")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8)); // tight timeout

            var resp = await SendWithRetryAsync(req, cts.Token, "FareQuote", maxRetries: 1);
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[iCabbi] Quote response {Status}: {Body}", resp.StatusCode, Truncate(body));

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[iCabbi] Quote HTTP {Status} — using local estimate", resp.StatusCode);
                return null;
            }

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return null; }

            _logger.LogInformation("[iCabbi] Full quote body: {Body}", Truncate(body, 600));

            // iCabbi returns HTTP 200 but with error:true for bad paths
            var errorNode = root?["error"];
            var isApiError = errorNode != null &&
                             (errorNode.ToString() == "true" ||
                              (int.TryParse(root?["code"]?.ToString(), out var c) && c != 200));
            if (isApiError)
            {
                var errCode = root?["code"]?.ToString() ?? "?";
                var errMsg  = root?["message"]?.ToString() ?? "unknown";
                _logger.LogWarning("[iCabbi] Quote API error {Code}: {Msg} — using local estimate", errCode, errMsg);
                return null;
            }

            // Resolve multi-quote response to single quote object
            JsonObject? quoteObj = ResolveQuoteObject(root, passengers);
            if (quoteObj is null)
            {
                _logger.LogWarning("[iCabbi] No quote object found in response — using local estimate");
                return null;
            }

            // Drill into nested "price" object if present
            var priceObj = quoteObj["price"] as JsonObject ?? quoteObj;

            var total = TryGetDecimal(priceObj, "price")
                     ?? TryGetDecimal(priceObj, "cost")
                     ?? TryGetDecimal(priceObj, "total")
                     ?? TryGetDecimal(priceObj, "fare")
                     ?? TryGetDecimal(priceObj, "amount")
                     ?? TryGetDecimal(priceObj, "fixed_price")
                     ?? TryGetDecimal(priceObj, "base_fare");

            if (total is null || total == 0)
            {
                _logger.LogWarning("[iCabbi] Quote returned zero/null fare — using local estimate");
                return null;
            }

            // ETA from iCabbi is in seconds — convert to minutes
            var etaSeconds = TryGetDecimal(priceObj, "eta_seconds");
            var etaMinutes = etaSeconds.HasValue
                ? (int)Math.Ceiling(etaSeconds.Value / 60m)
                : SafeGetInt(priceObj, "eta")
                  ?? SafeGetInt(priceObj, "eta_minutes")
                  ?? SafeGetInt(priceObj, "pickup_eta")
                  ?? SafeGetInt(quoteObj, "eta_minutes")
                  ?? SafeGetInt(quoteObj, "wait_time");

            _logger.LogInformation("[iCabbi] ✅ Quote: £{Fare:F2}, ETA={Eta}min", total, etaMinutes?.ToString() ?? "unknown");
            return new IcabbiFareQuote(total.Value, etaMinutes, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[iCabbi] Quote request timed out — using local estimate");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[iCabbi] Quote error (non-fatal)");
            return null;
        }
    }

    /// <summary>
    /// Resolves iCabbi multi-quote array to a single JsonObject matching vehicle type.
    /// </summary>
    private static JsonObject? ResolveQuoteObject(JsonNode? root, int passengers)
    {
        var vehicleType = passengers <= 4 ? "R4" : passengers <= 6 ? "R6" : passengers <= 7 ? "R7" : "R8";

        var bodyNode = root?["body"];
        var bodyObj  = bodyNode as JsonObject;

        var candidates = new JsonNode?[]
        {
            bodyObj?["quotes"],
            bodyObj?["quote"],
            root?["quotes"],
            root?["quote"],
            bodyObj
        };

        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;

            if (candidate is JsonArray arr)
            {
                if (arr.Count == 0) continue;
                foreach (var item in arr)
                {
                    if (item is not JsonObject obj) continue;
                    var vt = obj["vehicle_type"]?.GetValue<string>()
                          ?? obj["vehicle_group"]?.GetValue<string>()
                          ?? obj["type"]?.GetValue<string>() ?? "";
                    if (vt.Contains(vehicleType, StringComparison.OrdinalIgnoreCase))
                        return obj;
                }
                return arr[0] as JsonObject;
            }

            if (candidate is JsonObject singleObj)
                return singleObj;
        }

        return null;
    }

    // ═══════════════════════════════════════════
    //  CREATE & DISPATCH BOOKING
    // ═══════════════════════════════════════════

    /// <summary>
    /// Create an iCabbi booking from StructuredBooking + FareResult.
    /// Full production payload aligned with AdaSdkModel's ICabbiApiClient.
    /// </summary>
    public async Task<IcabbiBookingResult> CreateAndDispatchAsync(
        StructuredBooking booking,
        FareResult fare,
        string callerPhone,
        string? callerName = null,
        int? icabbiDriverId = null,
        int? icabbiVehicleId = null,
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

            var rawPhone = callerPhone ?? "";
            var phone = "00" + rawPhone.Replace("+", "").Replace(" ", "").Replace("-", "");
            var seats = booking.Passengers > 0 ? booking.Passengers : 1;

            // Parse fare decimal from fare string (e.g. "£4.50" → 4.50)
            var fareDecimal = 0m;
            if (!string.IsNullOrEmpty(fare.Fare))
            {
                var fareStr = fare.Fare.Replace("£", "").Replace("$", "").Trim();
                decimal.TryParse(fareStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out fareDecimal);
            }

            var scheduledDate = booking.IsAsap
                ? DateTime.UtcNow.AddMinutes(1)
                : booking.PickupDateTime ?? DateTime.UtcNow.AddMinutes(1);

            var siteId = _settings.SiteId > 0 ? _settings.SiteId : 1039;

            // Build instructions
            var instructions = new List<string>();
            if (!string.IsNullOrWhiteSpace(fare.ZoneName))
                instructions.Add($"Zone: {fare.ZoneName}");
            instructions.Add("Source: Voice AI (AdaCleanVersion)");
            var instructionStr = instructions.Count > 0 ? string.Join(" | ", instructions) : "No special instructions";

            var icabbiBooking = new IcabbiBookingRequest
            {
                date = scheduledDate,
                name = callerName ?? booking.CallerName ?? "Customer",
                phone = phone,
                extras = "WHATSAPP",
                seats = seats,
                instructions = instructionStr,
                address = new IcabbiAddressDto
                {
                    lat = fare.Pickup.Lat,
                    lng = fare.Pickup.Lon,
                    formatted = fare.Pickup.Address
                },
                destination = new IcabbiAddressDto
                {
                    lat = fare.Destination.Lat,
                    lng = fare.Destination.Lon,
                    formatted = fare.Destination.Address
                },
                payment = new IcabbiPaymentDto
                {
                    cost = fareDecimal,
                    price = fareDecimal,
                    total = fareDecimal
                },
                site_id = siteId,
                source = "APP",
                vehicle_group = "Taxi",
                status = "NEW",
                app_metadata = new IcabbiAppMetadataDto
                {
                    extras = "WHATSAPP",
                    whatsapp_number = phone,
                    source_system = "AdaCleanVersion",
                    journey_id = Guid.NewGuid().ToString(),
                    created_at = DateTime.UtcNow.ToString("o")
                }
            };

            // Assign vehicle type based on seats
            icabbiBooking.AssignVehicleType();

            // Assign driver/vehicle IDs
            var driverId = icabbiDriverId ?? 2222;
            var vehicleId = icabbiVehicleId ?? 2222;
            icabbiBooking.driver_id = driverId;
            icabbiBooking.vehicle_id = vehicleId;
            icabbiBooking.vehicle_ref = $"DRV{driverId}_VEH{vehicleId}";

            var json = JsonSerializer.Serialize(icabbiBooking, JsonOpts);
            _logger.LogInformation("[iCabbi] Payload: {Json}", Truncate(json, 500));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}bookings/add")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddAuthHeaders(req, phone);

            var resp = await SendWithRetryAsync(req, ct, "CreateBooking");
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[iCabbi] Response {Status}: {Body}", resp.StatusCode, Truncate(body));

            if (!resp.IsSuccessStatusCode)
                return IcabbiBookingResult.Fail($"Booking creation failed: {body}");

            JsonNode? root;
            try { root = JsonNode.Parse(body); }
            catch { return IcabbiBookingResult.Fail("Invalid JSON in booking response"); }

            var isError = root?["error"]?.ToString() == "true";
            if (isError)
            {
                var errMsg  = root?["message"]?.ToString() ?? "Unknown API error";
                var errCode = root?["code"]?.ToString() ?? "0";
                return IcabbiBookingResult.Fail($"iCabbi error {errCode}: {errMsg}");
            }

            var bookingNode = root?["body"] is JsonObject bodyObj ? bodyObj["booking"] : null;
            if (bookingNode is null)
                return IcabbiBookingResult.Fail("No body.booking in response");

            var jobId     = int.TryParse(bookingNode["id"]?.ToString(), out var jid) ? jid : (int?)null;
            var journeyId = jobId?.ToString() ?? "";
            var tripId    = bookingNode["trip_id"]?.ToString();
            var permaId   = bookingNode["perma_id"]?.ToString();
            var statusStr = bookingNode["status"]?.ToString();
            var trackingUrl = bookingNode["tracking_url"]?.ToString()
                              ?? BuildTrackingUrl(journeyId);

            if (string.IsNullOrEmpty(journeyId))
                return IcabbiBookingResult.Fail("No journey ID in response");

            _logger.LogInformation("[iCabbi] ✅ Booking created — JobId: {JourneyId}, TripId: {TripId}, Status: {Status}",
                journeyId, tripId, statusStr);
            _logger.LogInformation("[iCabbi] 🔗 Tracking URL: {Url}", trackingUrl);

            return new IcabbiBookingResult(true, journeyId, trackingUrl, tripId, permaId, "Booking created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[iCabbi] CreateAndDispatchAsync error");
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
        _logger.LogInformation("[iCabbi] Dispatch payload: {Json}", json);

        using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}bookings/dispatchjourney")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddAuthHeaders(req);

        var resp = await SendWithRetryAsync(req, ct, "DispatchJourney");
        var body = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[iCabbi] Dispatch response {Status}: {Body}", resp.StatusCode, Truncate(body));

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

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}bookings/cancel/{journeyId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddAuthHeaders(req);

        _logger.LogInformation("[iCabbi] 🧨 Cancelling booking {JourneyId}", journeyId);

        var resp = await SendWithRetryAsync(req, ct, "CancelBooking");
        var body = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[iCabbi] Cancel response {Status}: {Body}", resp.StatusCode, Truncate(body));

        return resp.IsSuccessStatusCode
            ? (true, body)
            : (false, $"HTTP {resp.StatusCode}: {body}");
    }

    // ═══════════════════════════════════════════
    //  UPDATE BOOKING
    // ═══════════════════════════════════════════

    /// <summary>
    /// Updates an existing iCabbi booking via POST /v2/bookings/update/{journeyId}.
    /// Only non-null fields are sent. Fields that cannot be updated are returned in 'ignored_fields'.
    /// </summary>
    public async Task<(bool success, string message, JsonNode? response)> UpdateBookingAsync(
        string journeyId, IcabbiBookingUpdate update, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(journeyId))
            return (false, "No journeyId provided to update.", null);

        var v2Base = BaseUrl.Replace("/uk/", "/v2/");
        if (!v2Base.Contains("/v2/"))
            v2Base = BaseUrl.TrimEnd('/') + "/../v2/";

        var url = $"{v2Base.TrimEnd('/')}/bookings/update/{journeyId}";

        var payload = new Dictionary<string, object?>();
        if (update.Phone != null) payload["phone"] = update.Phone;
        if (update.Name != null) payload["name"] = update.Name;
        if (update.Instructions != null) payload["instructions"] = update.Instructions;
        if (update.FlightNumber != null) payload["flight_number"] = update.FlightNumber;
        if (update.Payment != null) payload["payment"] = update.Payment;
        if (update.Eta != null) payload["eta"] = update.Eta;
        if (update.VehicleType != null) payload["vehicle_type"] = update.VehicleType;
        if (update.VehicleGroup != null) payload["vehicle_group"] = update.VehicleGroup;
        if (update.Passengers != null) payload["passengers"] = update.Passengers;
        if (update.PlannedDate != null) payload["planned_date"] = update.PlannedDate;
        if (update.PlannedDropoffDate != null) payload["planned_dropoff_date"] = update.PlannedDropoffDate;
        if (update.AppointmentDate != null) payload["appointment_date"] = update.AppointmentDate;
        if (update.RouteBy != null) payload["route_by"] = update.RouteBy;
        if (update.ExternalBookingId != null) payload["external_booking_id"] = update.ExternalBookingId;
        if (update.Destination != null) payload["destination"] = update.Destination;
        if (update.Address != null) payload["address"] = update.Address;
        if (update.Metadata != null) payload["metadata"] = update.Metadata;

        if (payload.Count == 0)
            return (false, "No fields provided to update.", null);

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddAuthHeaders(req);

        _logger.LogInformation("[iCabbi] ✏️ Updating booking {JourneyId}: {Json}", journeyId, json);

        var resp = await SendWithRetryAsync(req, ct, "UpdateBooking");
        var body = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[iCabbi] Update response {Status}: {Body}", resp.StatusCode, Truncate(body));

        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(body); } catch { }

        if (resp.IsSuccessStatusCode)
        {
            var ignored = parsed?["ignored_fields"];
            if (ignored != null)
                _logger.LogWarning("[iCabbi] Ignored fields: {Ignored}", ignored.ToJsonString());
            return (true, "Booking updated successfully.", parsed);
        }

        return (false, $"HTTP {(int)resp.StatusCode}: {body}", parsed);
    }

    // ═══════════════════════════════════════════
    //  CUSTOMER LOOKUP BY PHONE
    // ═══════════════════════════════════════════

    /// <summary>
    /// Searches iCabbi for a customer by phone number.
    /// Returns name, email, address, and iCabbi customer ID if found.
    /// Uses GET /passengers/search?phone={phone} with Basic auth.
    /// </summary>
    public async Task<IcabbiCustomerResult?> GetCustomerByPhoneAsync(
        string phoneNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return null;

        var e164 = FormatE164(phoneNumber);
        _logger.LogInformation("[iCabbi] 🔍 Looking up customer by phone: {Phone}", e164);

        try
        {
            // iCabbi passenger search endpoint
            var url = $"{BaseUrl}passengers/search?phone={Uri.EscapeDataString(e164)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req, e164);

            var resp = await _client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("[iCabbi] Customer lookup {Status}: {Body}", resp.StatusCode, Truncate(body));

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[iCabbi] Customer lookup failed: HTTP {Status}", resp.StatusCode);
                return null;
            }

            var root = JsonNode.Parse(body);
            if (root is null) return null;

            // Response can be { body: { passengers: [...] } } or { passengers: [...] } or array directly
            JsonArray? passengers = null;

            if (root is JsonArray arr)
                passengers = arr;
            else if (root is JsonObject obj)
            {
                passengers = (obj["body"]?["passengers"] as JsonArray)
                          ?? (obj["passengers"] as JsonArray)
                          ?? (obj["body"]?["results"] as JsonArray)
                          ?? (obj["results"] as JsonArray);
            }

            if (passengers is null || passengers.Count == 0)
            {
                _logger.LogInformation("[iCabbi] No customer found for {Phone}", e164);
                return null;
            }

            // Take the first (best) match
            var pax = passengers[0]!.AsObject();
            var result = new IcabbiCustomerResult
            {
                CustomerId = pax["id"]?.ToString() ?? pax["customer_id"]?.ToString(),
                Name = pax["name"]?.ToString() ?? pax["passenger_name"]?.ToString(),
                Phone = pax["phone"]?.ToString() ?? e164,
                Email = pax["email"]?.ToString(),
                Address = pax["address"]?.ToString() ?? pax["default_address"]?.ToString(),
                AddressLat = TryGetDouble(pax, "lat") ?? TryGetDouble(pax, "address_lat"),
                AddressLon = TryGetDouble(pax, "lng") ?? TryGetDouble(pax, "address_lng"),
                BookingCount = SafeGetInt(pax, "booking_count") ?? SafeGetInt(pax, "total_bookings") ?? 0,
                Notes = pax["notes"]?.ToString()
            };

            _logger.LogInformation(
                "[iCabbi] ✅ Found customer: {Name} (ID: {Id}, bookings: {Count})",
                result.Name, result.CustomerId, result.BookingCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[iCabbi] Customer lookup error for {Phone}", e164);
            return null;
        }
    }

    private static double? TryGetDouble(JsonObject obj, string key)
    {
        try
        {
            if (!obj.TryGetPropertyValue(key, out var val) || val is null) return null;
            if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════
    //  GET BOOKING STATUS
    // ═══════════════════════════════════════════

    public async Task<JsonNode?> GetBookingStatusAsync(string journeyId, CancellationToken ct = default)
    {
        var url = BuildStatusUrl(journeyId);
        _logger.LogInformation("[iCabbi] Fetching booking status: {Url}", url);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthHeaders(req);

        var resp = await _client.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("[iCabbi] Status response ({JourneyId}): {Body}", journeyId, Truncate(raw));

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

                    _logger.LogInformation("[iCabbi] Journey {JourneyId} status: {Status}", journeyId, status);

                    if (!_lastStatus.TryGetValue(journeyId, out var prev) || prev != status)
                    {
                        _lastStatus[journeyId] = status;
                        var msg = StatusMessages.TryGetValue(status, out var m) ? m : $"Status: {status}";
                        OnStatusChanged?.Invoke(journeyId, status, msg);
                    }

                    if (status is "JOURNEY_COMPLETED" or "CANCELLED" or "NO_SHOW")
                    {
                        _logger.LogInformation("[iCabbi] Journey {JourneyId} terminal: {Status}", journeyId, status);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[iCabbi] Poll error for {JourneyId}", journeyId); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
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
                using var clone = await CloneRequestAsync(request);
                var response = await _client.SendAsync(clone, ct);

                var code = (int)response.StatusCode;
                if ((code >= 500 || code == 408) && attempt < maxRetries)
                {
                    lastResponse = response;
                    _logger.LogWarning("[iCabbi] [{Op}] Transient HTTP {Code}, retry {Attempt}/{Max}...",
                        operationName, response.StatusCode, attempt, maxRetries);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }

                return response;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                _logger.LogWarning("[iCabbi] [{Op}] Timeout, retry {Attempt}/{Max}...",
                    operationName, attempt, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("[iCabbi] [{Op}] Network error, retry {Attempt}/{Max}: {Msg}",
                    operationName, attempt, maxRetries, ex.Message);
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

    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";

    private static decimal? TryGetDecimal(JsonObject obj, string key)
    {
        try
        {
            if (!obj.TryGetPropertyValue(key, out var val) || val is null) return null;
            if (val is not JsonValue) return null;
            if (decimal.TryParse(val.ToString(), System.Globalization.NumberStyles.Any,
                                 System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                return d;
        }
        catch { }
        return null;
    }

    private static int? SafeGetInt(JsonObject obj, string key)
    {
        try
        {
            if (!obj.TryGetPropertyValue(key, out var val) || val is null) return null;
            if (val is not JsonValue) return null;
            if (int.TryParse(val.ToString(), out var i)) return i;
        }
        catch { }
        return null;
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
//  MODELS (aligned with AdaSdkModel production)
// ═══════════════════════════════════════════

/// <summary>
/// Lightweight fare quote returned by GetFareQuoteAsync.
/// JourneyId is populated if iCabbi created a draft booking — caller should cancel it.
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
        else if (seats <= 7) vehicle_type = "R7";
        else vehicle_type = "R8";
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

/// <summary>
/// Result from iCabbi customer/passenger lookup by phone.
/// </summary>
public class IcabbiCustomerResult
{
    public string? CustomerId { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public double? AddressLat { get; set; }
    public double? AddressLon { get; set; }
    public int BookingCount { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Payload for the iCabbi dedicated fare quote endpoint (POST /bookings/quote).
/// Does NOT create a booking. Returns fare + ETA only.
/// multi-quote = 1 requests all available vehicle class prices.
/// </summary>
public class IcabbiQuoteRequest
{
    public string src { get; set; } = "APP";
    public int site_id { get; set; }
    public int prebooking { get; set; } = 0;
    public int passengers { get; set; } = 1;
    public string vehicle_type { get; set; } = "R4";
    public string vehicle_group { get; set; } = "ANY VEHICLE";

    [JsonPropertyName("multi-quote")]
    public int multi_quote { get; set; } = 1;

    public string date { get; set; } = "";

    /// <summary>["lat,lon", "lat,lon"] — pickup first, destination second.</summary>
    public List<string> locations { get; set; } = new();

    /// <summary>Pickup postcode (no spaces), e.g. "CV12BW".</summary>
    public string? postcode { get; set; }

    /// <summary>Destination postcode (no spaces), e.g. "CV12BE".</summary>
    public string? destination_postcode { get; set; }

    public string? via1_postcode { get; set; }
    public string? via2_postcode { get; set; }
}

/// <summary>
/// Patch model for updating an iCabbi booking.
/// Only non-null fields will be sent.
/// </summary>
public sealed class IcabbiBookingUpdate
{
    public string? Phone { get; set; }
    public string? Name { get; set; }
    public string? Instructions { get; set; }
    public string? FlightNumber { get; set; }
    public string? Payment { get; set; }
    public string? Eta { get; set; }
    public string? VehicleType { get; set; }
    public string? VehicleGroup { get; set; }
    public int? Passengers { get; set; }
    public string? PlannedDate { get; set; }
    public string? PlannedDropoffDate { get; set; }
    public string? AppointmentDate { get; set; }
    public string? RouteBy { get; set; }
    public string? ExternalBookingId { get; set; }
    public IcabbiAddressPatch? Destination { get; set; }
    public IcabbiAddressPatch? Address { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Address object for iCabbi update requests matching v2 API format.
/// </summary>
public sealed class IcabbiAddressPatch
{
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public string? Formatted { get; set; }
}
