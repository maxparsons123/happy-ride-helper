using System.Text.Json;
using AdaVaxVoIP.Config;
using AdaVaxVoIP.Models;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP.Services;

/// <summary>
/// Fare calculator with address-dispatch edge function integration + Google Maps/Nominatim fallback.
/// Ports the AdaMain FareCalculator pattern.
/// </summary>
public sealed class FareCalculator
{
    private readonly ILogger<FareCalculator> _logger;
    private readonly GoogleMapsSettings _googleSettings;
    private readonly SupabaseSettings _supabaseSettings;
    private readonly HttpClient _httpClient;

    private const decimal BaseFare = 3.50m;
    private const decimal PerMile = 1.00m;
    private const decimal MinFare = 4.00m;
    private const double AvgSpeedMph = 20.0;
    private const int BufferMinutes = 3;

    private string EdgeFunctionUrl => $"{_supabaseSettings.Url}/functions/v1/address-dispatch";

    public FareCalculator(ILogger<FareCalculator> logger, GoogleMapsSettings googleSettings, SupabaseSettings supabaseSettings)
    {
        _logger = logger;
        _googleSettings = googleSettings;
        _supabaseSettings = supabaseSettings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AdaVaxVoIP/1.0");
    }

    public async Task<FareResult> ExtractAndCalculateWithAiAsync(string? pickup, string? destination, string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(pickup) && string.IsNullOrWhiteSpace(destination))
            return new FareResult { Fare = "£4.00", Eta = "5 minutes" };

        try
        {
            _logger.LogInformation("🤖 Calling address-dispatch: pickup='{Pickup}', dest='{Dest}'", pickup, destination);

            var requestBody = JsonSerializer.Serialize(new { pickup = pickup ?? "", destination = destination ?? "", phone = phoneNumber ?? "" });
            var request = new HttpRequestMessage(HttpMethod.Post, EdgeFunctionUrl)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("apikey", _supabaseSettings.AnonKey);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var response = await _httpClient.SendAsync(request, cts.Token);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Edge function error: {Status}", (int)response.StatusCode);
                return await CalculateAsync(pickup, destination, phoneNumber);
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _))
                return await CalculateAsync(pickup, destination, phoneNumber);

            var result = new FareResult();
            string? detectedArea = root.TryGetProperty("detected_area", out var areaEl) ? areaEl.GetString() : null;

            // Parse pickup
            string resolvedPickup = pickup ?? "";
            double? pickupLat = null, pickupLon = null;
            string? pickupStreet = null, pickupNumber = null, pickupPostal = null, pickupCity = null;

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
            string resolvedDest = destination ?? "";
            double? destLat = null, destLon = null;
            string? destStreet = null, destNumber = null, destPostal = null, destCity = null;

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

            // Edge-calculated fare
            string? edgeFare = null, edgeEta = null;
            if (root.TryGetProperty("fare", out var fareEl) && fareEl.ValueKind == JsonValueKind.Object)
            {
                if (fareEl.TryGetProperty("fare", out var fv)) edgeFare = fv.GetString();
                if (fareEl.TryGetProperty("eta", out var ev)) edgeEta = ev.GetString();
            }

            // If Gemini returned valid coordinates, use directly
            if (pickupLat.HasValue && pickupLon.HasValue && destLat.HasValue && destLon.HasValue &&
                pickupLat.Value != 0 && destLat.Value != 0)
            {
                if (!string.IsNullOrWhiteSpace(edgeFare))
                {
                    result.Fare = edgeFare;
                    result.Eta = edgeEta ?? "10 minutes";
                }
                else
                {
                    var distanceMiles = HaversineDistance(pickupLat.Value, pickupLon.Value, destLat.Value, destLon.Value);
                    var fare = Math.Max(MinFare, BaseFare + (decimal)distanceMiles * PerMile);
                    fare = Math.Round(fare * 2, MidpointRounding.AwayFromZero) / 2;
                    var etaMinutes = (int)Math.Ceiling(distanceMiles / AvgSpeedMph * 60) + BufferMinutes;
                    result.Fare = $"£{fare:F2}";
                    result.Eta = $"{etaMinutes} minutes";
                }
                result.PickupLat = pickupLat.Value; result.PickupLon = pickupLon.Value;
                result.PickupStreet = pickupStreet; result.PickupNumber = pickupNumber;
                result.PickupPostalCode = pickupPostal; result.PickupCity = pickupCity ?? detectedArea;
                result.PickupFormatted = resolvedPickup;
                result.DestLat = destLat.Value; result.DestLon = destLon.Value;
                result.DestStreet = destStreet; result.DestNumber = destNumber;
                result.DestPostalCode = destPostal; result.DestCity = destCity ?? detectedArea;
                result.DestFormatted = resolvedDest;
                return result;
            }

            // Fallback to geocoding
            var fareResult = await CalculateAsync(resolvedPickup, resolvedDest, phoneNumber);
            fareResult.NeedsClarification = result.NeedsClarification;
            fareResult.PickupAlternatives = result.PickupAlternatives;
            fareResult.DestAlternatives = result.DestAlternatives;
            return fareResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⏱️ Edge function timeout, falling back to geocoding");
            return await CalculateAsync(pickup, destination, phoneNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edge function error, falling back to geocoding");
            return await CalculateAsync(pickup, destination, phoneNumber);
        }
    }

    public async Task<FareResult> CalculateAsync(string? pickup, string? destination, string? phoneNumber)
    {
        var result = new FareResult();
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
        {
            result.Fare = "£4.00"; result.Eta = "5 minutes"; return result;
        }

        var regionBias = GetRegionBias(phoneNumber);
        var pickupTask = GeocodeNominatimAsync(pickup, regionBias);
        var destTask = GeocodeNominatimAsync(destination, regionBias);
        await Task.WhenAll(pickupTask, destTask);
        var pickupGeo = await pickupTask;
        var destGeo = await destTask;

        if (pickupGeo != null) { result.PickupLat = pickupGeo.Lat; result.PickupLon = pickupGeo.Lon; }
        if (destGeo != null) { result.DestLat = destGeo.Lat; result.DestLon = destGeo.Lon; }

        if (pickupGeo != null && destGeo != null)
        {
            var distanceMiles = HaversineDistance(pickupGeo.Lat, pickupGeo.Lon, destGeo.Lat, destGeo.Lon);
            var fare = Math.Max(MinFare, BaseFare + (decimal)distanceMiles * PerMile);
            fare = Math.Round(fare * 2, MidpointRounding.AwayFromZero) / 2;
            var etaMinutes = (int)Math.Ceiling(distanceMiles / AvgSpeedMph * 60) + BufferMinutes;
            result.Fare = $"£{fare:F2}"; result.Eta = $"{etaMinutes} minutes";
        }
        else
        {
            result.Fare = "£8.00"; result.Eta = "8 minutes";
        }
        return result;
    }

    private async Task<GeoResult?> GeocodeNominatimAsync(string address, string regionBias)
    {
        try
        {
            var countryCode = regionBias == "nl" ? "NL" : "GB";
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&countrycodes={countryCode}&format=json&limit=1&addressdetails=1";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.GetArrayLength() == 0) return null;
            var first = doc.RootElement[0];
            return new GeoResult
            {
                Lat = double.Parse(first.GetProperty("lat").GetString()!),
                Lon = double.Parse(first.GetProperty("lon").GetString()!)
            };
        }
        catch { return null; }
    }

    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3958.8; // miles
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string GetRegionBias(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "gb";
        var clean = phone.Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("06") || clean.StartsWith("+31") || clean.StartsWith("0031")) return "nl";
        return "gb";
    }

    private record GeoResult { public double Lat; public double Lon; }
}

public sealed class FareResult
{
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public bool NeedsClarification { get; set; }
    public string[]? PickupAlternatives { get; set; }
    public string[]? DestAlternatives { get; set; }
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }
}
