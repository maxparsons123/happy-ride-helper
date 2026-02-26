using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Calls the address-dispatch edge function to geocode pickup/destination
/// and calculate fare from haversine distance.
///
/// Pipeline: StructuredBooking → address-dispatch → FareResult
///
/// This runs AFTER extraction (StructureOnlyEngine) and BEFORE fare presentation.
/// The edge function handles: Gemini geocoding, zone_pois matching, caller history
/// bypass, fare calculation, zone dispatch matching, and ambiguity detection.
/// </summary>
public class FareGeocodingService
{
    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly ILogger _logger;
    private const int TimeoutMs = 15000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FareGeocodingService(
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
    /// Geocode a single address inline (for pickup/destination verification).
    /// Uses the address-dispatch edge function with only one address populated.
    /// Returns the geocoded address, or null on failure.
    /// </summary>
    public async Task<GeocodedAddress?> GeocodeAddressAsync(
        string address,
        string field, // "pickup" or "destination"
        string? callerPhone = null,
        CancellationToken ct = default,
        string? adaReadback = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000); // 10s timeout for single address

        try
        {
            _logger.LogInformation("[FareGeo] Inline geocode ({Field}): raw=\"{Addr}\", adaReadback=\"{Readback}\"",
                field, address, adaReadback ?? "none");

            // Build payload with both raw STT and Ada's readback for Gemini reconciliation
            var payload = field == "pickup"
                ? new { pickup = address, destination = "PLACEHOLDER_SKIP", phone = callerPhone, pickup_time = (string?)null, pickup_house_number = (string?)null, destination_house_number = (string?)null, ada_readback = adaReadback }
                : new { pickup = "PLACEHOLDER_SKIP", destination = address, phone = callerPhone, pickup_time = (string?)null, pickup_house_number = (string?)null, destination_house_number = (string?)null, ada_readback = adaReadback };

            var url = $"{_supabaseUrl}/functions/v1/address-dispatch";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {_serviceRoleKey}");
            request.Headers.Add("apikey", _serviceRoleKey);
            request.Content = JsonContent.Create(payload, options: JsonOpts);

            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FareGeo] Inline geocode failed ({Status}): {Body}",
                    response.StatusCode, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Check status
            var status = root.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString() : "ready";

            var key = field == "pickup" ? "pickup" : "dropoff";
            var geocoded = ParseGeocodedAddress(root, key);

            if (status == "clarification_needed" || geocoded.IsAmbiguous)
            {
                _logger.LogInformation("[FareGeo] Inline geocode needs clarification for {Field}", field);
                return geocoded; // Return with IsAmbiguous=true so caller can handle
            }

            _logger.LogInformation("[FareGeo] ✅ Inline verified: \"{Addr}\" → ({Lat},{Lon})",
                geocoded.Address, geocoded.Lat, geocoded.Lon);

            return geocoded;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[FareGeo] Inline geocode timed out for {Field}", field);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FareGeo] Inline geocode error for {Field}", field);
            return null;
        }
    }

    /// <summary>
    /// Geocode both addresses and calculate fare.
    /// Returns null on failure (caller should retry or fall back).
    /// </summary>
    public async Task<FareResult?> CalculateAsync(
        StructuredBooking booking,
        string? callerPhone = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            _logger.LogInformation(
                "[FareGeo] Dispatching: pickup=\"{Pickup}\", dest=\"{Dest}\", phone={Phone}, time={Time}",
                booking.Pickup.DisplayName, booking.Destination.DisplayName,
                callerPhone ?? "none", booking.PickupTime);

            var payload = new
            {
                pickup = booking.Pickup.DisplayName,
                destination = booking.Destination.DisplayName,
                phone = callerPhone,
                pickup_time = booking.IsAsap ? null : booking.PickupTime,
                pickup_house_number = booking.Pickup.HouseNumber,
                destination_house_number = booking.Destination.HouseNumber
            };

            var url = $"{_supabaseUrl}/functions/v1/address-dispatch";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {_serviceRoleKey}");
            request.Headers.Add("apikey", _serviceRoleKey);
            request.Content = JsonContent.Create(payload, options: JsonOpts);

            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[FareGeo] Edge function returned {Status}: {Body}",
                    response.StatusCode, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Check if dispatch needs clarification
            var status = root.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString() : "ready";

            if (status == "clarification_needed")
            {
                var clarMsg = root.TryGetProperty("clarification_message", out var clarProp)
                    ? clarProp.GetString() : null;

                _logger.LogInformation("[FareGeo] Clarification needed: {Msg}", clarMsg);

                return new FareResult
                {
                    Pickup = ParseGeocodedAddress(root, "pickup"),
                    Destination = ParseGeocodedAddress(root, "dropoff"),
                    Fare = "TBD",
                    FareSpoken = "to be determined",
                    DistanceMiles = 0,
                    DriverEta = "",
                    DriverEtaMinutes = 0,
                    BusyLevel = "unknown",
                    BusyMessage = "",
                    ClarificationMessage = clarMsg
                };
            }

            // Parse fare data
            var fareElement = root.GetProperty("fare");
            var fare = fareElement.GetProperty("fare").GetString() ?? "£0.00";
            var fareSpoken = fareElement.GetProperty("fare_spoken").GetString() ?? fare;
            var distMiles = fareElement.GetProperty("distance_miles").GetDouble();
            var driverEta = fareElement.GetProperty("driver_eta").GetString() ?? "";
            var driverEtaMin = fareElement.GetProperty("driver_eta_minutes").GetInt32();
            var busyLevel = fareElement.GetProperty("busy_level").GetString() ?? "low";
            var busyMessage = fareElement.GetProperty("busy_message").GetString() ?? "";
            var tripEta = fareElement.TryGetProperty("trip_eta", out var te) ? te.GetString() : null;
            var tripEtaMin = fareElement.TryGetProperty("trip_eta_minutes", out var tem) ? tem.GetInt32() : (int?)null;

            // Parse zone info
            string? zoneName = null;
            string? companyId = null;
            if (root.TryGetProperty("matched_zone", out var zoneProp) &&
                zoneProp.ValueKind == JsonValueKind.Object)
            {
                zoneName = zoneProp.TryGetProperty("zone_name", out var zn) ? zn.GetString() : null;
                companyId = zoneProp.TryGetProperty("company_id", out var ci) ? ci.GetString() : null;
            }

            // Parse scheduled time
            var scheduledAt = root.TryGetProperty("scheduled_at", out var saProp) &&
                              saProp.ValueKind == JsonValueKind.String
                ? saProp.GetString() : null;

            var result = new FareResult
            {
                Pickup = ParseGeocodedAddress(root, "pickup"),
                Destination = ParseGeocodedAddress(root, "dropoff"),
                Fare = fare,
                FareSpoken = fareSpoken,
                DistanceMiles = distMiles,
                DriverEta = driverEta,
                DriverEtaMinutes = driverEtaMin,
                BusyLevel = busyLevel,
                BusyMessage = busyMessage,
                TripEta = tripEta,
                TripEtaMinutes = tripEtaMin,
                ScheduledAt = scheduledAt,
                ZoneName = zoneName,
                CompanyId = companyId
            };

            _logger.LogInformation(
                "[FareGeo] ✅ Fare={Fare} ({Miles}mi), ETA={Eta}, zone={Zone}",
                fare, distMiles, driverEta, zoneName ?? "none");

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[FareGeo] Timed out ({Ms}ms)", TimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FareGeo] Pipeline failed");
            return null;
        }
    }

    private static GeocodedAddress ParseGeocodedAddress(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var addr))
        {
            return new GeocodedAddress
            {
                Address = "",
                Lat = 0,
                Lon = 0,
                IsAmbiguous = true
            };
        }

        var alternatives = new List<string>();
        if (addr.TryGetProperty("alternatives", out var altProp) &&
            altProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var alt in altProp.EnumerateArray())
            {
                var s = alt.GetString();
                if (s != null) alternatives.Add(s);
            }
        }

        return new GeocodedAddress
        {
            Address = addr.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "",
            Lat = addr.TryGetProperty("lat", out var lat) ? lat.GetDouble() : 0,
            Lon = addr.TryGetProperty("lon", out var lon) ? lon.GetDouble() : 0,
            StreetName = addr.TryGetProperty("street_name", out var sn) ? sn.GetString() : null,
            StreetNumber = addr.TryGetProperty("street_number", out var snm) ? snm.GetString() : null,
            PostalCode = addr.TryGetProperty("postal_code", out var pc) ? pc.GetString() : null,
            City = addr.TryGetProperty("city", out var c) ? c.GetString() : null,
            IsAmbiguous = addr.TryGetProperty("is_ambiguous", out var amb) && amb.GetBoolean(),
            Alternatives = alternatives.Count > 0 ? alternatives : null,
            MatchedFromHistory = addr.TryGetProperty("matched_from_history", out var mfh) && mfh.GetBoolean()
        };
    }
}
