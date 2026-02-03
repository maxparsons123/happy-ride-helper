using System.Text.Json;
using AdaMain.Config;
using Microsoft.Extensions.Logging;

namespace AdaMain.Services;

/// <summary>
/// Fare calculator with Google Maps geocoding.
/// </summary>
public sealed class FareCalculator : IFareCalculator
{
    private readonly ILogger<FareCalculator> _logger;
    private readonly GoogleMapsSettings _settings;
    private readonly HttpClient _httpClient;
    
    // Pricing constants
    private const decimal BaseFare = 3.50m;
    private const decimal PerMile = 1.00m;
    private const decimal MinFare = 4.00m;
    private const double AvgSpeedMph = 20.0;
    private const int BufferMinutes = 3;
    
    public FareCalculator(ILogger<FareCalculator> logger, GoogleMapsSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AdaMain/1.0");
    }
    
    public async Task<FareResult> CalculateAsync(string? pickup, string? destination, string? phoneNumber)
    {
        var result = new FareResult();
        
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
        {
            result.Fare = "€4.00";
            result.Eta = "5 minutes";
            return result;
        }
        
        var regionBias = GetRegionBias(phoneNumber);
        
        // Geocode both addresses in parallel
        var pickupTask = GeocodeAsync(pickup, regionBias);
        var destTask = GeocodeAsync(destination, regionBias);
        
        await Task.WhenAll(pickupTask, destTask);
        
        var pickupGeo = await pickupTask;
        var destGeo = await destTask;
        
        // Populate result
        if (pickupGeo != null)
        {
            result.PickupLat = pickupGeo.Lat;
            result.PickupLon = pickupGeo.Lon;
            result.PickupStreet = pickupGeo.Street;
            result.PickupNumber = pickupGeo.Number;
            result.PickupPostalCode = pickupGeo.PostalCode;
            result.PickupCity = pickupGeo.City;
            result.PickupFormatted = pickupGeo.Formatted;
        }
        
        if (destGeo != null)
        {
            result.DestLat = destGeo.Lat;
            result.DestLon = destGeo.Lon;
            result.DestStreet = destGeo.Street;
            result.DestNumber = destGeo.Number;
            result.DestPostalCode = destGeo.PostalCode;
            result.DestCity = destGeo.City;
            result.DestFormatted = destGeo.Formatted;
        }
        
        // Calculate distance and fare
        if (pickupGeo != null && destGeo != null)
        {
            var distanceMiles = HaversineDistance(
                pickupGeo.Lat, pickupGeo.Lon,
                destGeo.Lat, destGeo.Lon);
            
            var fare = Math.Max(MinFare, BaseFare + (decimal)distanceMiles * PerMile);
            fare = Math.Round(fare * 2, MidpointRounding.AwayFromZero) / 2; // Round to nearest 50c
            
            var etaMinutes = (int)Math.Ceiling(distanceMiles / AvgSpeedMph * 60) + BufferMinutes;
            
            result.Fare = $"€{fare:F2}";
            result.Eta = $"{etaMinutes} minutes";
            
            _logger.LogInformation("Fare calculated: {Fare} for {Distance:F1} miles", result.Fare, distanceMiles);
        }
        else
        {
            result.Fare = "€8.00";
            result.Eta = "8 minutes";
            _logger.LogWarning("Using fallback fare - geocoding failed");
        }
        
        return result;
    }
    
    private async Task<GeoResult?> GeocodeAsync(string address, string regionBias)
    {
        try
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey))
            {
                return await GeocodeGoogleAsync(address, regionBias);
            }
            
            return await GeocodeNominatimAsync(address, regionBias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geocoding failed for: {Address}", address);
            return null;
        }
    }
    
    private async Task<GeoResult?> GeocodeGoogleAsync(string address, string regionBias)
    {
        var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                  $"?address={Uri.EscapeDataString(address)}" +
                  $"&region={regionBias}" +
                  $"&key={_settings.ApiKey}";
        
        var response = await _httpClient.GetStringAsync(url);
        using var doc = JsonDocument.Parse(response);
        
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;
        
        var first = results[0];
        var location = first.GetProperty("geometry").GetProperty("location");
        
        var result = new GeoResult
        {
            Lat = location.GetProperty("lat").GetDouble(),
            Lon = location.GetProperty("lng").GetDouble(),
            Formatted = first.GetProperty("formatted_address").GetString()
        };
        
        // Extract address components
        foreach (var component in first.GetProperty("address_components").EnumerateArray())
        {
            var types = component.GetProperty("types").EnumerateArray()
                .Select(t => t.GetString()).ToList();
            var value = component.GetProperty("long_name").GetString();
            
            if (types.Contains("street_number")) result.Number = value;
            else if (types.Contains("route")) result.Street = value;
            else if (types.Contains("locality")) result.City = value;
            else if (types.Contains("postal_code")) result.PostalCode = value;
        }
        
        return result;
    }
    
    private async Task<GeoResult?> GeocodeNominatimAsync(string address, string regionBias)
    {
        var countryCode = regionBias == "nl" ? "NL" : "GB";
        var url = $"https://nominatim.openstreetmap.org/search" +
                  $"?q={Uri.EscapeDataString(address)}" +
                  $"&countrycodes={countryCode}" +
                  $"&format=json&limit=1&addressdetails=1";
        
        var response = await _httpClient.GetStringAsync(url);
        using var doc = JsonDocument.Parse(response);
        
        if (doc.RootElement.GetArrayLength() == 0) return null;
        
        var first = doc.RootElement[0];
        var result = new GeoResult
        {
            Lat = double.Parse(first.GetProperty("lat").GetString()!),
            Lon = double.Parse(first.GetProperty("lon").GetString()!),
            Formatted = first.GetProperty("display_name").GetString()
        };
        
        if (first.TryGetProperty("address", out var addr))
        {
            if (addr.TryGetProperty("house_number", out var hn)) result.Number = hn.GetString();
            if (addr.TryGetProperty("road", out var rd)) result.Street = rd.GetString();
            if (addr.TryGetProperty("city", out var ct)) result.City = ct.GetString();
            if (addr.TryGetProperty("town", out var tw)) result.City ??= tw.GetString();
            if (addr.TryGetProperty("postcode", out var pc)) result.PostalCode = pc.GetString();
        }
        
        return result;
    }
    
    private static string GetRegionBias(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "uk";
        var clean = phone.Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("06") || clean.StartsWith("+31") || clean.StartsWith("0031"))
            return "nl";
        return "uk";
    }
    
    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3959; // Earth radius in miles
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
    
    private static double ToRad(double deg) => deg * Math.PI / 180;
    
    private sealed class GeoResult
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string? Street { get; set; }
        public string? Number { get; set; }
        public string? PostalCode { get; set; }
        public string? City { get; set; }
        public string? Formatted { get; set; }
    }
}
