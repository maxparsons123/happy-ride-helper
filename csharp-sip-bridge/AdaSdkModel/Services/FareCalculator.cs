using System.Text.Json;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Services;

/// <summary>
/// Fare calculator with local Gemini (preferred) + edge function fallback + Nominatim geocoding.
/// </summary>
public sealed class FareCalculator : IFareCalculator
{
    private readonly ILogger<FareCalculator> _logger;
    private readonly GoogleMapsSettings _googleSettings;
    private readonly SupabaseSettings _supabaseSettings;
    private readonly GeminiAddressClient? _geminiClient;
    private readonly HttpClient _httpClient;

    private const decimal BaseFare = 3.50m;
    private const decimal PerMile = 1.00m;
    private const decimal MinFare = 4.00m;
    private const double AvgSpeedMph = 30.0;
    private const int BufferMinutes = 3;

    private string EdgeFunctionUrl => $"{_supabaseSettings.Url}/functions/v1/address-dispatch";

    public FareCalculator(
        ILogger<FareCalculator> logger,
        GoogleMapsSettings googleSettings,
        SupabaseSettings supabaseSettings,
        GeminiAddressClient? geminiClient = null)
    {
        _logger = logger;
        _googleSettings = googleSettings;
        _supabaseSettings = supabaseSettings;
        _geminiClient = geminiClient;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AdaSdkModel/1.0");
    }

    public async Task<FareResult> ExtractAndCalculateWithAiAsync(
        string? pickup,
        string? destination,
        string? phoneNumber,
        string? pickupTime = null,
        string? spokenPickupNumber = null,
        string? spokenDestNumber = null)
    {
        if (string.IsNullOrWhiteSpace(pickup) && string.IsNullOrWhiteSpace(destination))
            return new FareResult { Fare = "£4.00", Eta = "5 minutes" };

        // ── Try local Gemini first (if enabled) ──
        if (_geminiClient != null)
        {
            try
            {
                var geminiResult = await _geminiClient.ResolveAsync(pickup, destination, phoneNumber, pickupTime, spokenPickupNumber, spokenDestNumber);
                if (geminiResult.HasValue)
                {
                    _logger.LogDebug("✅ Using local Gemini for address dispatch");
                    return ParseEdgeResponse(geminiResult.Value, pickup, destination, phoneNumber);
                }
                _logger.LogDebug("⚠️ Local Gemini returned null — falling back to edge function");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local Gemini error — falling back to edge function");
            }
        }

        // ── Fall back to edge function (passes spoken house numbers as extra guard) ──
        try
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                pickup = pickup ?? "",
                destination = destination ?? "",
                phone = phoneNumber ?? "",
                pickup_time = pickupTime ?? "",
                pickup_house_number = spokenPickupNumber ?? "",
                destination_house_number = spokenDestNumber ?? ""
            });
            var request = new HttpRequestMessage(HttpMethod.Post, EdgeFunctionUrl)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("apikey", _supabaseSettings.AnonKey);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return await CalculateAsync(pickup, destination, phoneNumber);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
                return await CalculateAsync(pickup, destination, phoneNumber);

            return ParseEdgeResponse(root, pickup, destination, phoneNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edge function error, falling back");
            return await CalculateAsync(pickup, destination, phoneNumber);
        }
    }

    /// <summary>
    /// Parse the JSON response (from either local Gemini or edge function) into a FareResult.
    /// </summary>
    private FareResult ParseEdgeResponse(JsonElement root, string? pickup, string? destination, string? phoneNumber)
    {
        var result = new FareResult();
        string? detectedArea = root.TryGetProperty("detected_area", out var areaEl) ? areaEl.GetString() : null;

        // Parse pickup
        double? pickupLat = null, pickupLon = null;
        string? pickupStreet = null, pickupNumber = null, pickupPostal = null, pickupCity = null;
        string resolvedPickup = pickup ?? "";

        if (root.TryGetProperty("pickup", out var pickupEl))
        {
            if (pickupEl.TryGetProperty("address", out var addr)) resolvedPickup = addr.GetString() ?? pickup ?? "";
            if (pickupEl.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number) pickupLat = lat.GetDouble();
            if (pickupEl.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number) pickupLon = lon.GetDouble();
            if (pickupEl.TryGetProperty("street_name", out var st)) pickupStreet = st.GetString();
            if (pickupEl.TryGetProperty("street_number", out var num)) pickupNumber = num.GetString();
            if (pickupEl.TryGetProperty("postal_code", out var pc)) pickupPostal = pc.GetString();
            if (pickupEl.TryGetProperty("city", out var city)) pickupCity = city.GetString();
            if (pickupEl.TryGetProperty("is_ambiguous", out var ambig) && ambig.GetBoolean())
            {
                result.NeedsClarification = true;
                if (pickupEl.TryGetProperty("alternatives", out var alts))
                    result.PickupAlternatives = alts.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();
            }
        }

        // Parse dropoff
        double? destLat = null, destLon = null;
        string? destStreet = null, destNumber = null, destPostal = null, destCity = null;
        string resolvedDest = destination ?? "";

        if (root.TryGetProperty("dropoff", out var dropoffEl))
        {
            if (dropoffEl.TryGetProperty("address", out var addr)) resolvedDest = addr.GetString() ?? destination ?? "";
            if (dropoffEl.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number) destLat = lat.GetDouble();
            if (dropoffEl.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number) destLon = lon.GetDouble();
            if (dropoffEl.TryGetProperty("street_name", out var st)) destStreet = st.GetString();
            if (dropoffEl.TryGetProperty("street_number", out var num)) destNumber = num.GetString();
            if (dropoffEl.TryGetProperty("postal_code", out var pc)) destPostal = pc.GetString();
            if (dropoffEl.TryGetProperty("city", out var city)) destCity = city.GetString();
            if (dropoffEl.TryGetProperty("is_ambiguous", out var ambig) && ambig.GetBoolean())
            {
                result.NeedsClarification = true;
                if (dropoffEl.TryGetProperty("alternatives", out var alts))
                    result.DestAlternatives = alts.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();
            }
        }

        if (root.TryGetProperty("status", out var statusEl) && statusEl.GetString() == "clarification_needed")
            result.NeedsClarification = true;
        if (root.TryGetProperty("clarification_message", out var clarifEl) && clarifEl.ValueKind == JsonValueKind.String)
            result.ClarificationMessage = clarifEl.GetString();

        // Parse AI-resolved scheduled time
        if (root.TryGetProperty("scheduled_at", out var schedEl) && schedEl.ValueKind == JsonValueKind.String)
        {
            var schedStr = schedEl.GetString();
            if (!string.IsNullOrWhiteSpace(schedStr) && DateTime.TryParse(schedStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var schedDt))
            {
                result.ScheduledAt = schedDt;
                _logger.LogInformation("⏰ AI parsed pickup time → {ScheduledAt:u}", schedDt);
            }
        }

        // Edge fare (from either Gemini or edge function)
        string? edgeFare = null, edgeEta = null;
        if (root.TryGetProperty("fare", out var fareEl) && fareEl.ValueKind == JsonValueKind.Object)
        {
            if (fareEl.TryGetProperty("fare", out var fv)) edgeFare = fv.GetString();
            if (fareEl.TryGetProperty("eta", out var ev)) edgeEta = ev.GetString();
        }

        // Only calculate fare if disambiguation is NOT needed
        if (!result.NeedsClarification &&
            pickupLat.HasValue && pickupLon.HasValue && destLat.HasValue && destLon.HasValue &&
            pickupLat.Value != 0 && destLat.Value != 0)
        {
            if (!string.IsNullOrWhiteSpace(edgeFare))
            {
                result.Fare = edgeFare;
                result.Eta = edgeEta ?? "10 minutes";
            }
            else
            {
                var dist = HaversineDistance(pickupLat.Value, pickupLon.Value, destLat.Value, destLon.Value);
                var fare = Math.Max(MinFare, BaseFare + (decimal)dist * PerMile);
                fare = Math.Round(fare * 2, MidpointRounding.AwayFromZero) / 2;
                result.Fare = $"£{fare:F2}";
                result.Eta = $"{(int)Math.Ceiling(dist / AvgSpeedMph * 60) + BufferMinutes} minutes";
            }
            result.PickupLat = pickupLat.Value; result.PickupLon = pickupLon.Value;
            result.PickupStreet = pickupStreet; result.PickupNumber = pickupNumber;
            result.PickupPostalCode = pickupPostal; result.PickupCity = pickupCity ?? detectedArea;
            result.PickupFormatted = resolvedPickup;
            result.DestLat = destLat.Value; result.DestLon = destLon.Value;
            result.DestStreet = destStreet; result.DestNumber = destNumber;
            result.DestPostalCode = destPostal; result.DestCity = destCity ?? detectedArea;
            result.DestFormatted = resolvedDest;
        }
        return result;
    }

    /// <param name="localeCity">Optional city to anchor bare addresses that have a house number but no city
    /// component — prevents Nominatim from picking a same-named street in the wrong city (e.g. London vs Coventry).</param>
    public async Task<FareResult> CalculateAsync(string? pickup, string? destination, string? phoneNumber, string? localeCity = null)
    {
        var result = new FareResult();
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
        {
            result.Fare = "£4.00"; result.Eta = "5 minutes"; return result;
        }

        // Anchor bare addresses (has house number, no city) to the locale city to avoid
        // Nominatim picking a same-named street in the wrong city (e.g. London instead of Coventry).
        if (!string.IsNullOrWhiteSpace(localeCity))
        {
            pickup      = AnchorToLocaleCity(pickup,      localeCity);
            destination = AnchorToLocaleCity(destination, localeCity);
        }

        // Nominatim fallback geocoding
        try
        {
            var countryCode = GetRegionBias(phoneNumber) == "nl" ? "NL" : "GB";
            var pTask = GeocodeNominatimAsync(pickup, countryCode);
            var dTask = GeocodeNominatimAsync(destination, countryCode);
            await Task.WhenAll(pTask, dTask);
            var pGeo = await pTask; var dGeo = await dTask;

            if (pGeo != null && dGeo != null)
            {
                result.PickupLat = pGeo.Lat; result.PickupLon = pGeo.Lon;
                result.DestLat = dGeo.Lat; result.DestLon = dGeo.Lon;
                var dist = HaversineDistance(pGeo.Lat, pGeo.Lon, dGeo.Lat, dGeo.Lon);
                var fare = Math.Max(MinFare, BaseFare + (decimal)dist * PerMile);
                fare = Math.Round(fare * 2, MidpointRounding.AwayFromZero) / 2;
                result.Fare = $"£{fare:F2}";
                result.Eta = $"{(int)Math.Ceiling(dist / AvgSpeedMph * 60) + BufferMinutes} minutes";
                return result;
            }
        }
        catch { }

        result.Fare = "£8.00"; result.Eta = "8 minutes";
        return result;
    }

    private async Task<GeoPoint?> GeocodeNominatimAsync(string address, string countryCode)
    {
        var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&countrycodes={countryCode}&format=json&limit=1";
        var resp = await _httpClient.GetStringAsync(url);
        using var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.GetArrayLength() == 0) return null;
        var first = doc.RootElement[0];
        return new GeoPoint
        {
            Lat = double.Parse(first.GetProperty("lat").GetString()!),
            Lon = double.Parse(first.GetProperty("lon").GetString()!)
        };
    }

    /// <summary>
    /// If <paramref name="address"/> has a house number but no city token, appends
    /// <paramref name="localeCity"/> so Nominatim searches in the right city.
    /// Addresses that already contain a comma (already have city/context) are returned unchanged.
    /// </summary>
    private static string AnchorToLocaleCity(string address, string localeCity)
    {
        if (string.IsNullOrWhiteSpace(localeCity)) return address;
        if (address.Contains(',')) return address;      // already has context
        if (!address.Any(char.IsDigit)) return address; // no house number → vague, don't anchor
        return $"{address}, {localeCity}";
    }

    private static string GetRegionBias(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "uk";
        var clean = phone.Replace(" ", "").Replace("-", "");
        return (clean.StartsWith("06") || clean.StartsWith("+31") || clean.StartsWith("0031")) ? "nl" : "uk";
    }

    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3959;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private sealed class GeoPoint { public double Lat { get; set; } public double Lon { get; set; } }
}
