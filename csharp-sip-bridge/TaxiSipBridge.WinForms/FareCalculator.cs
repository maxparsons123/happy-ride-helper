using System.Net.Http;
using System.Text.Json;

namespace TaxiSipBridge;

/// <summary>
/// Local fare calculator for taxi bookings.
/// Uses Google Maps Geocoding for accurate address resolution with phone-based region detection.
/// Fallback to OpenStreetMap then keyword-based estimation if geocoding fails.
/// </summary>
public static class FareCalculator
{
    /// <summary>Starting fare in euros (€3.50 default)</summary>
    public const decimal BASE_FARE = 3.50m;

    /// <summary>Rate per mile in euros (€1.00 default)</summary>
    public const decimal RATE_PER_MILE = 1.00m;

    /// <summary>Minimum fare in pounds</summary>
    public const decimal MIN_FARE = 4.00m;

    // API keys loaded from environment
    private static string? _googleMapsApiKey;

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
    /// Set the Google Maps API key (call once at startup from config)
    /// </summary>
    public static void SetGoogleMapsApiKey(string apiKey)
    {
        _googleMapsApiKey = apiKey;
        Console.WriteLine("[FareCalculator] Google Maps API key configured");
    }

    /// <summary>
    /// Calculate fare based on pickup and destination addresses using real geocoding.
    /// Uses phone number to detect region for better geocoding accuracy.
    /// Also returns geocoded coordinates and parsed address components for dispatch.
    /// </summary>
    public static async Task<FareResult> CalculateFareWithCoordsAsync(
        string? pickup, 
        string? destination,
        string? phoneNumber = null)
    {
        var result = new FareResult
        {
            Fare = FormatFare(MIN_FARE),
            Eta = "5 minutes",
            DistanceMiles = 0
        };

        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
            return result;

        // Detect region from phone number
        var regionBias = DetectRegionFromPhone(phoneNumber);
        Console.WriteLine($"[FareCalculator] Detected region: {regionBias.Country} (code: {regionBias.CountryCode})");

        try
        {
            // Geocode both addresses in parallel
            var pickupTask = GeocodeAddressAsync(pickup, regionBias);
            var destTask = GeocodeAddressAsync(destination, regionBias);
            
            await Task.WhenAll(pickupTask, destTask);
            
            var pickupGeo = await pickupTask;
            var destGeo = await destTask;

            // Store pickup details
            if (pickupGeo != null)
            {
                result.PickupLat = pickupGeo.Lat;
                result.PickupLon = pickupGeo.Lon;
                result.PickupStreet = pickupGeo.StreetName;
                result.PickupNumber = pickupGeo.StreetNumber;
                result.PickupPostalCode = pickupGeo.PostalCode;
                result.PickupCity = pickupGeo.City;
                result.PickupFormatted = pickupGeo.FormattedAddress;
            }

            // Store destination details  
            if (destGeo != null)
            {
                result.DestLat = destGeo.Lat;
                result.DestLon = destGeo.Lon;
                result.DestStreet = destGeo.StreetName;
                result.DestNumber = destGeo.StreetNumber;
                result.DestPostalCode = destGeo.PostalCode;
                result.DestCity = destGeo.City;
                result.DestFormatted = destGeo.FormattedAddress;
            }

            if (pickupGeo != null && destGeo != null)
            {
                // Calculate real distance using Haversine
                var distanceMiles = HaversineDistanceMiles(
                    pickupGeo.Lat, pickupGeo.Lon,
                    destGeo.Lat, destGeo.Lon);

                var fare = CalculateFareFromDistanceDecimal(distanceMiles);
                var eta = EstimateEta(distanceMiles);

                result.Fare = FormatFare(fare);
                result.Eta = eta;
                result.DistanceMiles = distanceMiles;

                Console.WriteLine($"[FareCalculator] Geocoded distance: {distanceMiles:F2} miles");
                Console.WriteLine($"[FareCalculator] Pickup: {pickup} → {pickupGeo.FormattedAddress} ({pickupGeo.Lat:F4}, {pickupGeo.Lon:F4})");
                Console.WriteLine($"[FareCalculator] Dest: {destination} → {destGeo.FormattedAddress} ({destGeo.Lat:F4}, {destGeo.Lon:F4})");

                return result;
            }
            else
            {
                // Log which geocoding failed
                if (pickupGeo == null)
                    Console.WriteLine($"[FareCalculator] ⚠️ Pickup geocoding FAILED for: '{pickup}'");
                if (destGeo == null)
                    Console.WriteLine($"[FareCalculator] ⚠️ Destination geocoding FAILED for: '{destination}'");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FareCalculator] Geocoding failed: {ex.Message}");
        }

        // Fallback to keyword-based estimation
        Console.WriteLine($"[FareCalculator] Using keyword fallback for: '{pickup}' → '{destination}'");
        var fallbackDistance = EstimateFromKeywords(pickup, destination);
        var fallbackFare = CalculateFareFromDistanceDecimal(fallbackDistance);
        result.Fare = FormatFare(fallbackFare);
        result.Eta = EstimateEta(fallbackDistance);
        result.DistanceMiles = fallbackDistance;
        return result;
    }

    /// <summary>
    /// Verify and enrich an address using Google Maps geocoding with phone-based region bias.
    /// Use this during sync_booking_data to get accurate address components for dispatch.
    /// </summary>
    public static async Task<AddressVerifyResult> VerifyAddressAsync(string? address, string? phoneNumber = null)
    {
        var result = new AddressVerifyResult
        {
            OriginalInput = address ?? "",
            Success = false,
            Confidence = 0
        };

        if (string.IsNullOrWhiteSpace(address))
        {
            result.Reason = "Empty address";
            return result;
        }

        var region = DetectRegionFromPhone(phoneNumber);
        Console.WriteLine($"[FareCalculator] Verifying address: '{address}' (region: {region.CountryCode})");

        try
        {
            var geocoded = await GeocodeAddressAsync(address, region);
            
            if (geocoded == null)
            {
                result.Reason = "Address not found by geocoder";
                Console.WriteLine($"[FareCalculator] ❌ Address verification failed: '{address}'");
                return result;
            }

            // Calculate confidence based on completeness
            double confidence = 0.5; // Base for finding any match
            if (!string.IsNullOrEmpty(geocoded.StreetName)) confidence += 0.15;
            if (!string.IsNullOrEmpty(geocoded.StreetNumber)) confidence += 0.15;
            if (!string.IsNullOrEmpty(geocoded.City)) confidence += 0.1;
            if (!string.IsNullOrEmpty(geocoded.PostalCode)) confidence += 0.1;

            result.Success = true;
            result.VerifiedAddress = geocoded.FormattedAddress;
            result.Street = geocoded.StreetName;
            result.Number = geocoded.StreetNumber;
            result.City = geocoded.City;
            result.PostalCode = geocoded.PostalCode;
            result.Lat = geocoded.Lat;
            result.Lon = geocoded.Lon;
            result.Confidence = Math.Min(1.0, confidence);
            result.Reason = confidence >= 0.8 ? "High confidence match" : "Partial match";

            Console.WriteLine($"[FareCalculator] ✓ Verified: '{address}' → {result.Number} {result.Street}, {result.City} ({result.PostalCode}) [conf: {result.Confidence:P0}]");
            return result;
        }
        catch (Exception ex)
        {
            result.Reason = $"Geocoding error: {ex.Message}";
            Console.WriteLine($"[FareCalculator] ❌ Address verification error: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Legacy method for backwards compatibility.
    /// </summary>
    public static async Task<(string Fare, string Eta, double DistanceMiles)> CalculateFareAsync(string? pickup, string? destination)
    {
        var result = await CalculateFareWithCoordsAsync(pickup, destination);
        return (result.Fare, result.Eta, result.DistanceMiles);
    }

    /// <summary>
    /// Detect region/country from phone number prefix for geocoding bias.
    /// </summary>
    private static RegionInfo DetectRegionFromPhone(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };

        // Clean the phone number
        var clean = new string(phoneNumber.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (clean.StartsWith("00")) clean = "+" + clean.Substring(2);
        if (!clean.StartsWith("+") && clean.Length > 0) clean = "+" + clean;

        // Match country codes
        if (clean.StartsWith("+31")) // Netherlands
            return new RegionInfo { Country = "Netherlands", CountryCode = "NL", DefaultCity = "Amsterdam" };
        if (clean.StartsWith("+44")) // UK
            return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };
        if (clean.StartsWith("+1")) // US/Canada
            return new RegionInfo { Country = "United States", CountryCode = "US", DefaultCity = "New York" };
        if (clean.StartsWith("+33")) // France
            return new RegionInfo { Country = "France", CountryCode = "FR", DefaultCity = "Paris" };
        if (clean.StartsWith("+49")) // Germany
            return new RegionInfo { Country = "Germany", CountryCode = "DE", DefaultCity = "Berlin" };
        if (clean.StartsWith("+32")) // Belgium
            return new RegionInfo { Country = "Belgium", CountryCode = "BE", DefaultCity = "Brussels" };
        if (clean.StartsWith("+34")) // Spain
            return new RegionInfo { Country = "Spain", CountryCode = "ES", DefaultCity = "Madrid" };
        if (clean.StartsWith("+39")) // Italy
            return new RegionInfo { Country = "Italy", CountryCode = "IT", DefaultCity = "Rome" };
        if (clean.StartsWith("+48")) // Poland
            return new RegionInfo { Country = "Poland", CountryCode = "PL", DefaultCity = "Warsaw" };
        if (clean.StartsWith("+351")) // Portugal
            return new RegionInfo { Country = "Portugal", CountryCode = "PT", DefaultCity = "Lisbon" };
        if (clean.StartsWith("+353")) // Ireland
            return new RegionInfo { Country = "Ireland", CountryCode = "IE", DefaultCity = "Dublin" };

        // Default to UK
        return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };
    }

    /// <summary>
    /// Geocode an address using Google Maps Geocoding API (preferred) or OSM fallback.
    /// Returns full address components for dispatch integration.
    /// </summary>
    private static async Task<GeocodedAddress?> GeocodeAddressAsync(string address, RegionInfo region)
    {
        // Try Google Maps first if API key is available
        if (!string.IsNullOrEmpty(_googleMapsApiKey))
        {
            var googleResult = await GeocodeWithGoogleAsync(address, region);
            if (googleResult != null) return googleResult;
        }

        // Fallback to OpenStreetMap
        return await GeocodeWithOsmAsync(address, region);
    }

    /// <summary>
    /// Geocode using Google Maps Geocoding API with region bias.
    /// </summary>
    private static async Task<GeocodedAddress?> GeocodeWithGoogleAsync(string address, RegionInfo region)
    {
        try
        {
            // Add region context if not already present
            var searchAddress = address;
            if (!ContainsRegionContext(address, region))
            {
                searchAddress = $"{address}, {region.Country}";
            }

            var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                      $"?address={Uri.EscapeDataString(searchAddress)}" +
                      $"&region={region.CountryCode.ToLower()}" +
                      $"&key={_googleMapsApiKey}";

            var response = await GetHttpClient().GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "OK")
            {
                if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var first = results[0];
                    var result = new GeocodedAddress();

                    // Get coordinates
                    if (first.TryGetProperty("geometry", out var geometry) &&
                        geometry.TryGetProperty("location", out var location))
                    {
                        result.Lat = location.GetProperty("lat").GetDouble();
                        result.Lon = location.GetProperty("lng").GetDouble();
                    }

                    // Get formatted address
                    if (first.TryGetProperty("formatted_address", out var formatted))
                    {
                        result.FormattedAddress = formatted.GetString() ?? address;
                    }

                    // Parse address components
                    if (first.TryGetProperty("address_components", out var components))
                    {
                        foreach (var comp in components.EnumerateArray())
                        {
                            var types = comp.GetProperty("types").EnumerateArray()
                                           .Select(t => t.GetString()).ToList();
                            var longName = comp.GetProperty("long_name").GetString() ?? "";
                            var shortName = comp.TryGetProperty("short_name", out var sn) 
                                           ? sn.GetString() ?? "" : longName;

                            if (types.Contains("street_number"))
                            {
                                // Keep full string to preserve "52A" format
                                result.StreetNumber = longName;
                            }
                            else if (types.Contains("route"))
                            {
                                result.StreetName = longName;
                            }
                            else if (types.Contains("postal_code"))
                            {
                                result.PostalCode = longName;
                            }
                            else if (types.Contains("locality"))
                            {
                                result.City = longName;
                            }
                            else if (types.Contains("postal_town") && string.IsNullOrEmpty(result.City))
                            {
                                result.City = longName;
                            }
                            else if (types.Contains("administrative_area_level_2") && string.IsNullOrEmpty(result.City))
                            {
                                result.City = longName;
                            }
                        }
                    }

                    Console.WriteLine($"[FareCalculator] Google geocoded: {address} → {result.City}, {result.PostalCode}");
                    return result;
                }
            }
            else
            {
                var statusStr = status.GetString();
                Console.WriteLine($"[FareCalculator] Google geocode status: {statusStr}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FareCalculator] Google geocode error for '{address}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Geocode using OpenStreetMap Nominatim API as fallback.
    /// </summary>
    private static async Task<GeocodedAddress?> GeocodeWithOsmAsync(string address, RegionInfo region)
    {
        try
        {
            var searchAddress = address;
            if (!ContainsRegionContext(address, region))
            {
                searchAddress = $"{address}, {region.Country}";
            }

            var url = $"https://nominatim.openstreetmap.org/search" +
                      $"?q={Uri.EscapeDataString(searchAddress)}" +
                      $"&format=json&limit=1&addressdetails=1" +
                      $"&countrycodes={region.CountryCode.ToLower()}";

            var response = await GetHttpClient().GetStringAsync(url);
            var results = JsonSerializer.Deserialize<JsonElement[]>(response);

            if (results != null && results.Length > 0)
            {
                var first = results[0];
                var result = new GeocodedAddress
                {
                    Lat = double.Parse(first.GetProperty("lat").GetString()!),
                    Lon = double.Parse(first.GetProperty("lon").GetString()!),
                    FormattedAddress = first.TryGetProperty("display_name", out var dn) 
                                       ? dn.GetString() ?? address : address
                };

                // Parse address details if available
                if (first.TryGetProperty("address", out var addr))
                {
                    if (addr.TryGetProperty("road", out var road))
                        result.StreetName = road.GetString() ?? "";
                    if (addr.TryGetProperty("house_number", out var houseNum))
                    {
                        // Keep full string to preserve "52A" format
                        result.StreetNumber = houseNum.GetString() ?? "";
                    }
                    if (addr.TryGetProperty("postcode", out var pc))
                        result.PostalCode = pc.GetString() ?? "";
                    if (addr.TryGetProperty("city", out var city))
                        result.City = city.GetString() ?? "";
                    else if (addr.TryGetProperty("town", out var town))
                        result.City = town.GetString() ?? "";
                    else if (addr.TryGetProperty("village", out var village))
                        result.City = village.GetString() ?? "";
                }

                Console.WriteLine($"[FareCalculator] OSM geocoded: {address} → {result.City}");
                return result;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FareCalculator] OSM geocode error for '{address}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Check if address already contains region/country context.
    /// </summary>
    private static bool ContainsRegionContext(string address, RegionInfo region)
    {
        var lower = address.ToLowerInvariant();
        return lower.Contains(region.Country.ToLower()) ||
               lower.Contains(region.CountryCode.ToLower()) ||
               lower.Contains(region.DefaultCity.ToLower()) ||
               System.Text.RegularExpressions.Regex.IsMatch(address, @"\d{4}\s*[A-Z]{2}") || // NL postcode
               System.Text.RegularExpressions.Regex.IsMatch(address, @"[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}"); // UK postcode
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
        fare = Math.Ceiling(fare * 2) / 2; // Round to nearest 50c
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
            combined.Contains("schiphol") || combined.Contains("luton"))
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

    private static string FormatFare(decimal fare) => $"€{fare:F2}";

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

/// <summary>
/// Region information detected from phone number.
/// </summary>
public class RegionInfo
{
    public string Country { get; set; } = "United Kingdom";
    public string CountryCode { get; set; } = "GB";
    public string DefaultCity { get; set; } = "London";
}

/// <summary>
/// Geocoded address with full components.
/// </summary>
public class GeocodedAddress
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string StreetName { get; set; } = "";
    public string StreetNumber { get; set; } = ""; // String to support "52A"
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string FormattedAddress { get; set; } = "";
    public bool IsVerified { get; set; } // True if geocoding succeeded
    public double Confidence { get; set; } // 0-1 based on match quality
}

/// <summary>
/// Result from fare calculation including coordinates and address components for dispatch.
/// </summary>
public class FareResult
{
    public string Fare { get; set; } = "";
    public string Eta { get; set; } = "";
    public double DistanceMiles { get; set; }
    
    // Pickup details
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; } // String for "52A"
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }
    
    // Destination details
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; } // String for "52A"
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }
}

/// <summary>
/// Result from address verification - includes match confidence.
/// </summary>
public class AddressVerifyResult
{
    public bool Success { get; set; }
    public string OriginalInput { get; set; } = "";
    public string? VerifiedAddress { get; set; }
    public string? Street { get; set; }
    public string? Number { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double Confidence { get; set; } // 0-1
    public string? Reason { get; set; } // Why verification failed/succeeded
}
