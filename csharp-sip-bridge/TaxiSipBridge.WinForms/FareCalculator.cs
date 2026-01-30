namespace TaxiSipBridge;

/// <summary>
/// Local fare calculator for taxi bookings.
/// Uses a simple formula: base fare + per-mile rate.
/// </summary>
public static class FareCalculator
{
    /// <summary>Starting fare in pounds (£3.50 default)</summary>
    public const decimal BASE_FARE = 3.50m;

    /// <summary>Rate per mile in pounds (£1.00 default)</summary>
    public const decimal RATE_PER_MILE = 1.00m;

    /// <summary>Minimum fare in pounds</summary>
    public const decimal MIN_FARE = 4.00m;

    /// <summary>
    /// UK city coordinates for distance estimation.
    /// </summary>
    private static readonly Dictionary<string, (double Lat, double Lon)> CityCoordinates = new(StringComparer.OrdinalIgnoreCase)
    {
        // Major UK cities
        { "london", (51.5074, -0.1278) },
        { "birmingham", (52.4862, -1.8904) },
        { "manchester", (53.4808, -2.2426) },
        { "leeds", (53.8008, -1.5491) },
        { "liverpool", (53.4084, -2.9916) },
        { "sheffield", (53.3811, -1.4701) },
        { "bristol", (51.4545, -2.5879) },
        { "newcastle", (54.9783, -1.6178) },
        { "nottingham", (52.9548, -1.1581) },
        { "glasgow", (55.8642, -4.2518) },
        { "edinburgh", (55.9533, -3.1883) },
        
        // Common areas/suburbs
        { "heathrow", (51.4700, -0.4543) },
        { "gatwick", (51.1537, -0.1821) },
        { "stansted", (51.8860, 0.2389) },
        { "luton", (51.8747, -0.3683) },
        { "city airport", (51.5053, 0.0553) },
        
        // Dutch cities (for testing)
        { "amsterdam", (52.3676, 4.9041) },
        { "rotterdam", (51.9244, 4.4777) },
        { "utrecht", (52.0907, 5.1214) },
        { "den haag", (52.0705, 4.3007) },
        { "the hague", (52.0705, 4.3007) },
        { "eindhoven", (51.4416, 5.4697) },
    };

    /// <summary>
    /// Calculate fare based on pickup and destination addresses.
    /// Uses coordinate lookup for known locations, or estimates based on address parsing.
    /// </summary>
    /// <param name="pickup">Pickup address</param>
    /// <param name="destination">Destination address</param>
    /// <returns>Calculated fare as formatted string (e.g., "£12.50")</returns>
    public static string CalculateFare(string? pickup, string? destination)
    {
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
            return FormatFare(MIN_FARE);

        // Try to estimate distance
        var distanceMiles = EstimateDistanceMiles(pickup, destination);
        
        // Calculate fare
        var fare = BASE_FARE + (RATE_PER_MILE * (decimal)distanceMiles);
        
        // Apply minimum fare
        fare = Math.Max(fare, MIN_FARE);
        
        // Round to nearest 50p
        fare = Math.Ceiling(fare * 2) / 2;
        
        return FormatFare(fare);
    }

    /// <summary>
    /// Calculate fare with explicit distance in miles.
    /// </summary>
    /// <param name="distanceMiles">Distance in miles</param>
    /// <returns>Calculated fare as formatted string</returns>
    public static string CalculateFareFromDistance(double distanceMiles)
    {
        var fare = BASE_FARE + (RATE_PER_MILE * (decimal)distanceMiles);
        fare = Math.Max(fare, MIN_FARE);
        fare = Math.Ceiling(fare * 2) / 2; // Round to nearest 50p
        return FormatFare(fare);
    }

    /// <summary>
    /// Get ETA based on distance (assumes average 20mph in urban areas).
    /// </summary>
    /// <param name="distanceMiles">Distance in miles</param>
    /// <returns>ETA as formatted string (e.g., "5 minutes")</returns>
    public static string EstimateEta(double distanceMiles)
    {
        // Base pickup time: 5 minutes
        // Add travel time at average 20mph urban speed
        int pickupMinutes = 5;
        int travelMinutes = (int)Math.Ceiling(distanceMiles / 20.0 * 60);
        
        // Minimum 5 minutes
        int totalMinutes = Math.Max(pickupMinutes, travelMinutes + 2);
        
        return $"{totalMinutes} minutes";
    }

    /// <summary>
    /// Estimate distance in miles between two addresses.
    /// Uses Haversine formula for known coordinates, or keyword-based estimation.
    /// </summary>
    private static double EstimateDistanceMiles(string pickup, string destination)
    {
        // Try coordinate-based calculation
        var pickupCoord = FindCoordinates(pickup);
        var destCoord = FindCoordinates(destination);

        if (pickupCoord.HasValue && destCoord.HasValue)
        {
            return HaversineDistanceMiles(
                pickupCoord.Value.Lat, pickupCoord.Value.Lon,
                destCoord.Value.Lat, destCoord.Value.Lon);
        }

        // Fallback: estimate based on keywords
        return EstimateFromKeywords(pickup, destination);
    }

    /// <summary>
    /// Find coordinates for an address by matching against known locations.
    /// </summary>
    private static (double Lat, double Lon)? FindCoordinates(string address)
    {
        var lower = address.ToLowerInvariant();
        
        foreach (var (name, coords) in CityCoordinates)
        {
            if (lower.Contains(name))
                return coords;
        }
        
        return null;
    }

    /// <summary>
    /// Estimate distance using keyword heuristics when coordinates unavailable.
    /// </summary>
    private static double EstimateFromKeywords(string pickup, string destination)
    {
        var combined = $"{pickup} {destination}".ToLowerInvariant();
        
        // Airport trips are typically longer
        if (combined.Contains("airport") || combined.Contains("heathrow") || 
            combined.Contains("gatwick") || combined.Contains("stansted"))
        {
            return 15.0; // ~15 miles for airport trips
        }
        
        // Train station trips
        if (combined.Contains("station") || combined.Contains("railway"))
        {
            return 5.0; // ~5 miles for station trips
        }
        
        // Hospital trips
        if (combined.Contains("hospital") || combined.Contains("clinic"))
        {
            return 4.0;
        }
        
        // Shopping/retail
        if (combined.Contains("shopping") || combined.Contains("mall") || combined.Contains("centre"))
        {
            return 3.0;
        }
        
        // Default urban trip
        return 4.0; // Average 4 miles
    }

    /// <summary>
    /// Calculate great-circle distance between two coordinates using Haversine formula.
    /// </summary>
    private static double HaversineDistanceMiles(double lat1, double lon1, double lat2, double lon2)
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

    /// <summary>
    /// Format fare as British pounds.
    /// </summary>
    private static string FormatFare(decimal fare)
    {
        return $"£{fare:F2}";
    }

    /// <summary>
    /// Parse fare string back to decimal (for calculations).
    /// </summary>
    public static decimal ParseFare(string fare)
    {
        if (string.IsNullOrWhiteSpace(fare))
            return 0m;
            
        // Remove currency symbol and parse
        var cleaned = fare.Replace("£", "").Replace("$", "").Replace("€", "").Trim();
        return decimal.TryParse(cleaned, out var value) ? value : 0m;
    }
}
