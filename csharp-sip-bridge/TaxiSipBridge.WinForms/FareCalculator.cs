using System.Net.Http;
using System.Text.Json;

namespace TaxiSipBridge;

/// <summary>
/// Local fare calculator for taxi bookings.
/// Uses real geocoding (OpenStreetMap) + Haversine formula for accurate distance.
/// Fallback to keyword-based estimation if geocoding fails.
/// </summary>
public static class FareCalculator
{
    /// <summary>Starting fare in pounds (£3.50 default)</summary>
    public const decimal BASE_FARE = 3.50m;

    /// <summary>Rate per mile in pounds (£1.00 default)</summary>
    public const decimal RATE_PER_MILE = 1.00m;

    /// <summary>Minimum fare in pounds</summary>
    public const decimal MIN_FARE = 4.00m;

    // Lazy-initialized to avoid static constructor failures (TypeInitializationException)
    private static HttpClient? _httpClientBacking;
    private static HttpClient GetHttpClient()
    {
        if (_httpClientBacking == null)
        {
            _httpClientBacking = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _httpClientBacking.DefaultRequestHeaders.Add("User-Agent", "TaxiSipBridge/1.0");
        }
        return _httpClientBacking;
    }

    /// <summary>
    /// Calculate fare based on pickup and destination addresses using real geocoding.
    /// This is the async version that uses OpenStreetMap for accurate distances.
    /// </summary>
    public static async Task<(string Fare, string Eta, double DistanceMiles)> CalculateFareAsync(string? pickup, string? destination)
    {
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
            return (FormatFare(MIN_FARE), "5 minutes", 0);

        try
        {
            // Geocode both addresses in parallel
            var pickupTask = GeocodeAddressAsync(pickup);
            var destTask = GeocodeAddressAsync(destination);
            
            await Task.WhenAll(pickupTask, destTask);
            
            var pickupCoord = await pickupTask;
            var destCoord = await destTask;

            if (pickupCoord.HasValue && destCoord.HasValue)
            {
                // Calculate real distance using Haversine
                var distanceMiles = HaversineDistanceMiles(
                    pickupCoord.Value.Lat, pickupCoord.Value.Lon,
                    destCoord.Value.Lat, destCoord.Value.Lon);

                var fare = CalculateFareFromDistanceDecimal(distanceMiles);
                var eta = EstimateEta(distanceMiles);

                Console.WriteLine($"[FareCalculator] Geocoded distance: {distanceMiles:F2} miles");
                Console.WriteLine($"[FareCalculator] Pickup: {pickup} → ({pickupCoord.Value.Lat:F4}, {pickupCoord.Value.Lon:F4})");
                Console.WriteLine($"[FareCalculator] Dest: {destination} → ({destCoord.Value.Lat:F4}, {destCoord.Value.Lon:F4})");

                return (FormatFare(fare), eta, distanceMiles);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FareCalculator] Geocoding failed: {ex.Message}");
        }

        // Fallback to keyword-based estimation
        var fallbackDistance = EstimateFromKeywords(pickup, destination);
        var fallbackFare = CalculateFareFromDistanceDecimal(fallbackDistance);
        return (FormatFare(fallbackFare), EstimateEta(fallbackDistance), fallbackDistance);
    }

    /// <summary>
    /// Geocode an address using OpenStreetMap Nominatim API.
    /// Returns lat/lon coordinates or null if not found.
    /// </summary>
    private static async Task<(double Lat, double Lon)?> GeocodeAddressAsync(string address)
    {
        try
        {
            // Add UK bias for better results
            var searchAddress = address;
            if (!address.ToLowerInvariant().Contains("uk") && 
                !address.ToLowerInvariant().Contains("united kingdom") &&
                !address.ToLowerInvariant().Contains("netherlands") &&
                !address.ToLowerInvariant().Contains("nederland"))
            {
                searchAddress = $"{address}, UK";
            }

            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(searchAddress)}&format=json&limit=1";
            
            var response = await GetHttpClient().GetStringAsync(url);
            var results = JsonSerializer.Deserialize<JsonElement[]>(response);

            if (results != null && results.Length > 0)
            {
                var first = results[0];
                var lat = double.Parse(first.GetProperty("lat").GetString()!);
                var lon = double.Parse(first.GetProperty("lon").GetString()!);
                return (lat, lon);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FareCalculator] Geocode error for '{address}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Synchronous fare calculation (uses keyword-based fallback only).
    /// Use CalculateFareAsync for accurate geocoded distances.
    /// </summary>
    public static string CalculateFare(string? pickup, string? destination)
    {
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
            return FormatFare(MIN_FARE);

        var distanceMiles = EstimateFromKeywords(pickup, destination);
        var fare = CalculateFareFromDistanceDecimal(distanceMiles);
        return FormatFare(fare);
    }

    /// <summary>
    /// Calculate fare with explicit distance in miles.
    /// </summary>
    public static string CalculateFareFromDistance(double distanceMiles)
    {
        return FormatFare(CalculateFareFromDistanceDecimal(distanceMiles));
    }

    /// <summary>
    /// Calculate fare decimal value from distance.
    /// </summary>
    private static decimal CalculateFareFromDistanceDecimal(double distanceMiles)
    {
        var fare = BASE_FARE + (RATE_PER_MILE * (decimal)distanceMiles);
        fare = Math.Max(fare, MIN_FARE);
        fare = Math.Ceiling(fare * 2) / 2; // Round to nearest 50p
        return fare;
    }

    /// <summary>
    /// Get ETA based on distance (assumes average 20mph in urban areas).
    /// </summary>
    public static string EstimateEta(double distanceMiles)
    {
        // Base pickup time: 5 minutes minimum
        // Add travel time at average 20mph urban speed
        int travelMinutes = (int)Math.Ceiling(distanceMiles / 20.0 * 60);
        int totalMinutes = Math.Max(5, travelMinutes + 3); // +3 for pickup buffer
        
        return $"{totalMinutes} minutes";
    }

    /// <summary>
    /// Estimate distance using keyword heuristics when geocoding unavailable.
    /// </summary>
    private static double EstimateFromKeywords(string pickup, string destination)
    {
        var combined = $"{pickup} {destination}".ToLowerInvariant();
        
        // Airport trips are typically longer
        if (combined.Contains("airport") || combined.Contains("heathrow") || 
            combined.Contains("gatwick") || combined.Contains("stansted") ||
            combined.Contains("schiphol"))
        {
            return 15.0;
        }
        
        // Train station trips
        if (combined.Contains("station") || combined.Contains("railway") ||
            combined.Contains("centraal"))
        {
            return 5.0;
        }
        
        // Hospital trips
        if (combined.Contains("hospital") || combined.Contains("clinic") ||
            combined.Contains("ziekenhuis"))
        {
            return 4.0;
        }
        
        // Shopping/retail
        if (combined.Contains("shopping") || combined.Contains("mall") || 
            combined.Contains("centre") || combined.Contains("centrum"))
        {
            return 3.0;
        }
        
        // Default urban trip
        return 4.0;
    }

    /// <summary>
    /// Calculate great-circle distance between two coordinates using Haversine formula.
    /// </summary>
    public static double HaversineDistanceMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMiles = 3959.0;
        
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        
        return EarthRadiusMiles * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static string FormatFare(decimal fare) => $"£{fare:F2}";

    /// <summary>
    /// Parse fare string back to decimal.
    /// </summary>
    public static decimal ParseFare(string fare)
    {
        if (string.IsNullOrWhiteSpace(fare))
            return 0m;
            
        var cleaned = fare.Replace("£", "").Replace("$", "").Replace("€", "").Trim();
        return decimal.TryParse(cleaned, out var value) ? value : 0m;
    }
}
