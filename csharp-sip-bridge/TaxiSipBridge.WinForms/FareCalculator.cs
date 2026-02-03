using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaxiSipBridge;

/// <summary>
/// Pure fare calculator with geocoding support.
/// Takes addresses (optionally from extractors) and calculates fare/ETA.
/// Uses Google Places/Geocoding API with phone-based region bias.
/// </summary>
public class FareCalculator
{
    public const decimal BASE_FARE = 3.50m;
    public const decimal RATE_PER_MILE = 1.00m;
    public const decimal MIN_FARE = 4.00m;

    private readonly string? _googleMapsApiKey;
    private readonly HttpClient _httpClient;

    // Known cities for text detection
    private static readonly HashSet<string> _knownUkCities = new(StringComparer.OrdinalIgnoreCase)
    {
        "London", "Birmingham", "Manchester", "Leeds", "Glasgow", "Liverpool", "Bristol", "Sheffield",
        "Edinburgh", "Leicester", "Coventry", "Bradford", "Cardiff", "Belfast", "Nottingham", "Newcastle",
        "Southampton", "Derby", "Portsmouth", "Brighton", "Plymouth", "Wolverhampton", "Reading", "Northampton",
        "Milton Keynes", "Oxford", "Cambridge", "York", "Bath", "Chester", "Exeter", "Norwich"
    };

    private static readonly HashSet<string> _knownNlCities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Amsterdam", "Rotterdam", "Den Haag", "The Hague", "Utrecht", "Eindhoven", "Tilburg", "Groningen",
        "Almere", "Breda", "Nijmegen", "Apeldoorn", "Haarlem", "Arnhem", "Maastricht", "Leiden", "Delft"
    };

    public static Action<string>? OnLog;
    private static void Log(string msg) => OnLog?.Invoke(msg);

    public FareCalculator(string? googleMapsApiKey = null)
    {
        _googleMapsApiKey = googleMapsApiKey;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "TaxiSipBridge/3.0");
    }

    /// <summary>
    /// Calculate fare from addresses with geocoding.
    /// </summary>
    public async Task<FareResult> CalculateAsync(
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

        var region = DetectRegionFromPhone(phoneNumber);
        Log($"🌍 Region: {region.Country} ({region.CountryCode}), default city: {region.DefaultCity}");

        try
        {
            var pickupTask = GeocodeAddressAsync(pickup, region);
            var destTask = GeocodeAddressAsync(destination, region);
            await Task.WhenAll(pickupTask, destTask);

            var pickupGeo = await pickupTask;
            var destGeo = await destTask;

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
                var distanceMiles = HaversineDistanceMiles(
                    pickupGeo.Lat, pickupGeo.Lon,
                    destGeo.Lat, destGeo.Lon);

                var fare = CalculateFareFromDistance(distanceMiles);
                result.Fare = FormatFare(fare);
                result.Eta = EstimateEta(distanceMiles);
                result.DistanceMiles = distanceMiles;

                Log($"📏 Distance: {distanceMiles:F2} miles → {result.Fare}");
                return result;
            }
            else
            {
                if (pickupGeo == null) Log($"⚠️ Pickup geocoding failed: '{pickup}'");
                if (destGeo == null) Log($"⚠️ Dest geocoding failed: '{destination}'");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Geocoding error: {ex.Message}");
        }

        // Fallback to keyword estimation
        Log($"🔄 Using keyword fallback");
        var fallbackDistance = EstimateFromKeywords(pickup, destination);
        result.Fare = FormatFare(CalculateFareFromDistance(fallbackDistance));
        result.Eta = EstimateEta(fallbackDistance);
        result.DistanceMiles = fallbackDistance;
        return result;
    }

    /// <summary>
    /// Calculate fare with pre-extracted addresses from AI.
    /// </summary>
    public async Task<FareResult> CalculateWithExtractionAsync(
        AddressExtractionResult extraction,
        string? rawPickup,
        string? rawDestination,
        string? phoneNumber = null)
    {
        // Use AI-resolved addresses if available
        var pickup = extraction.PickupAddress ?? rawPickup;
        var destination = extraction.DestinationAddress ?? rawDestination;

        var result = await CalculateAsync(pickup, destination, phoneNumber);

        // Enrich with AI-extracted components (AI has higher priority for house numbers)
        if (!string.IsNullOrEmpty(extraction.PickupHouseNumber))
            result.PickupNumber = extraction.PickupHouseNumber;
        if (!string.IsNullOrEmpty(extraction.PickupStreet))
            result.PickupStreet = extraction.PickupStreet;
        if (!string.IsNullOrEmpty(extraction.PickupCity))
            result.PickupCity = extraction.PickupCity;

        if (!string.IsNullOrEmpty(extraction.DestinationHouseNumber))
            result.DestNumber = extraction.DestinationHouseNumber;
        if (!string.IsNullOrEmpty(extraction.DestinationStreet))
            result.DestStreet = extraction.DestinationStreet;
        if (!string.IsNullOrEmpty(extraction.DestinationCity))
            result.DestCity = extraction.DestinationCity;

        // Pass through clarification info
        if (extraction.NeedsClarification)
        {
            result.NeedsClarification = true;
            result.PickupAlternatives = extraction.PickupAlternatives;
            result.DestAlternatives = extraction.DestinationAlternatives;
        }

        return result;
    }

    /// <summary>
    /// Calculate fare from coordinates directly (no geocoding needed).
    /// </summary>
    public FareResult CalculateFromCoords(
        double pickupLat, double pickupLon,
        double destLat, double destLon)
    {
        var distanceMiles = HaversineDistanceMiles(pickupLat, pickupLon, destLat, destLon);
        var fare = CalculateFareFromDistance(distanceMiles);

        return new FareResult
        {
            Fare = FormatFare(fare),
            Eta = EstimateEta(distanceMiles),
            DistanceMiles = distanceMiles,
            PickupLat = pickupLat,
            PickupLon = pickupLon,
            DestLat = destLat,
            DestLon = destLon
        };
    }

    /// <summary>
    /// Verify and geocode an address.
    /// </summary>
    public async Task<GeocodedAddress?> GeocodeAsync(string address, string? phoneNumber = null)
    {
        var region = DetectRegionFromPhone(phoneNumber);
        return await GeocodeAddressAsync(address, region);
    }

    #region Geocoding

    private async Task<GeocodedAddress?> GeocodeAddressAsync(string address, RegionInfo region)
    {
        if (!string.IsNullOrEmpty(_googleMapsApiKey))
        {
            var placesResult = await GeocodeWithGooglePlacesAsync(address, region);
            if (placesResult != null) return placesResult;

            var googleResult = await GeocodeWithGoogleAsync(address, region);
            if (googleResult != null) return googleResult;
        }

        return await GeocodeWithOsmAsync(address, region);
    }

    private async Task<GeocodedAddress?> GeocodeWithGooglePlacesAsync(string address, RegionInfo region)
    {
        try
        {
            var searchQuery = address;
            var detectedCity = DetectCityFromAddress(address, region);

            if (detectedCity == null && !ContainsRegionContext(address, region))
                searchQuery = $"{address}, {region.DefaultCity}, {region.Country}";
            else if (!ContainsRegionContext(address, region))
                searchQuery = $"{address}, {region.Country}";

            var url = $"https://maps.googleapis.com/maps/api/place/textsearch/json" +
                      $"?query={Uri.EscapeDataString(searchQuery)}" +
                      $"&region={region.CountryCode.ToLower()}" +
                      $"&key={_googleMapsApiKey}";

            var response = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "OK" &&
                root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                var result = new GeocodedAddress();

                if (first.TryGetProperty("geometry", out var geometry) &&
                    geometry.TryGetProperty("location", out var location))
                {
                    result.Lat = location.GetProperty("lat").GetDouble();
                    result.Lon = location.GetProperty("lng").GetDouble();
                }

                if (first.TryGetProperty("formatted_address", out var formatted))
                    result.FormattedAddress = formatted.GetString() ?? address;

                // Get details via Place Details API
                if (first.TryGetProperty("place_id", out var placeIdEl))
                {
                    var placeId = placeIdEl.GetString();
                    if (!string.IsNullOrEmpty(placeId))
                    {
                        var details = await GetPlaceDetailsAsync(placeId, result);
                        if (details != null) result = details;
                    }
                }

                Log($"✓ Places: {address} → {result.FormattedAddress}");
                return result;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Places error: {ex.Message}");
        }

        return null;
    }

    private async Task<GeocodedAddress?> GetPlaceDetailsAsync(string placeId, GeocodedAddress baseResult)
    {
        try
        {
            var url = $"https://maps.googleapis.com/maps/api/place/details/json" +
                      $"?place_id={placeId}" +
                      $"&fields=address_components,formatted_address,geometry" +
                      $"&key={_googleMapsApiKey}";

            var response = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "OK" &&
                root.TryGetProperty("result", out var result))
            {
                var address = new GeocodedAddress
                {
                    Lat = baseResult.Lat,
                    Lon = baseResult.Lon,
                    FormattedAddress = baseResult.FormattedAddress
                };

                if (result.TryGetProperty("geometry", out var geometry) &&
                    geometry.TryGetProperty("location", out var location))
                {
                    address.Lat = location.GetProperty("lat").GetDouble();
                    address.Lon = location.GetProperty("lng").GetDouble();
                }

                if (result.TryGetProperty("formatted_address", out var formatted))
                    address.FormattedAddress = formatted.GetString() ?? address.FormattedAddress;

                if (result.TryGetProperty("address_components", out var components))
                {
                    foreach (var comp in components.EnumerateArray())
                    {
                        var types = comp.GetProperty("types").EnumerateArray()
                            .Select(t => t.GetString()).ToList();
                        var longName = comp.GetProperty("long_name").GetString() ?? "";

                        if (types.Contains("street_number"))
                            address.StreetNumber = longName;
                        else if (types.Contains("route"))
                            address.StreetName = longName;
                        else if (types.Contains("postal_code"))
                            address.PostalCode = longName;
                        else if (types.Contains("locality"))
                            address.City = longName;
                        else if (types.Contains("postal_town") && string.IsNullOrEmpty(address.City))
                            address.City = longName;
                    }
                }

                return address;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Place details error: {ex.Message}");
        }

        return baseResult;
    }

    private async Task<GeocodedAddress?> GeocodeWithGoogleAsync(string address, RegionInfo region)
    {
        try
        {
            var searchAddress = ContainsRegionContext(address, region) ? address : $"{address}, {region.Country}";

            var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                      $"?address={Uri.EscapeDataString(searchAddress)}" +
                      $"&region={region.CountryCode.ToLower()}" +
                      $"&key={_googleMapsApiKey}";

            var response = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "OK" &&
                root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                var result = new GeocodedAddress();

                if (first.TryGetProperty("geometry", out var geometry) &&
                    geometry.TryGetProperty("location", out var location))
                {
                    result.Lat = location.GetProperty("lat").GetDouble();
                    result.Lon = location.GetProperty("lng").GetDouble();
                }

                if (first.TryGetProperty("formatted_address", out var formatted))
                    result.FormattedAddress = formatted.GetString() ?? address;

                if (first.TryGetProperty("address_components", out var components))
                {
                    foreach (var comp in components.EnumerateArray())
                    {
                        var types = comp.GetProperty("types").EnumerateArray()
                            .Select(t => t.GetString()).ToList();
                        var longName = comp.GetProperty("long_name").GetString() ?? "";

                        if (types.Contains("street_number"))
                            result.StreetNumber = longName;
                        else if (types.Contains("route"))
                            result.StreetName = longName;
                        else if (types.Contains("postal_code"))
                            result.PostalCode = longName;
                        else if (types.Contains("locality"))
                            result.City = longName;
                        else if (types.Contains("postal_town") && string.IsNullOrEmpty(result.City))
                            result.City = longName;
                    }
                }

                Log($"✓ Google: {address} → {result.City}");
                return result;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Google geocode error: {ex.Message}");
        }

        return null;
    }

    private async Task<GeocodedAddress?> GeocodeWithOsmAsync(string address, RegionInfo region)
    {
        try
        {
            var searchAddress = ContainsRegionContext(address, region) ? address : $"{address}, {region.Country}";

            var url = $"https://nominatim.openstreetmap.org/search" +
                      $"?q={Uri.EscapeDataString(searchAddress)}" +
                      $"&format=json&limit=1&addressdetails=1" +
                      $"&countrycodes={region.CountryCode.ToLower()}";

            var response = await _httpClient.GetStringAsync(url);
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

                if (first.TryGetProperty("address", out var addr))
                {
                    if (addr.TryGetProperty("road", out var road))
                        result.StreetName = road.GetString() ?? "";
                    if (addr.TryGetProperty("house_number", out var houseNum))
                        result.StreetNumber = houseNum.GetString() ?? "";
                    if (addr.TryGetProperty("postcode", out var pc))
                        result.PostalCode = pc.GetString() ?? "";
                    if (addr.TryGetProperty("city", out var city))
                        result.City = city.GetString() ?? "";
                    else if (addr.TryGetProperty("town", out var town))
                        result.City = town.GetString() ?? "";
                }

                Log($"✓ OSM: {address} → {result.City}");
                return result;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ OSM geocode error: {ex.Message}");
        }

        return null;
    }

    #endregion

    #region Helpers

    private static RegionInfo DetectRegionFromPhone(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };

        var clean = new string(phoneNumber.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (clean.StartsWith("00")) clean = "+" + clean.Substring(2);
        if (!clean.StartsWith("+") && clean.Length > 0) clean = "+" + clean;

        if (clean.StartsWith("+31"))
            return new RegionInfo { Country = "Netherlands", CountryCode = "NL", DefaultCity = "Amsterdam" };
        if (clean.StartsWith("+44"))
        {
            // UK landline area codes for city detection
            if (clean.StartsWith("+4424") || clean.StartsWith("+44024"))
                return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "Coventry" };
            if (clean.StartsWith("+44121") || clean.StartsWith("+440121"))
                return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "Birmingham" };
            if (clean.StartsWith("+4420") || clean.StartsWith("+44020"))
                return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };
            if (clean.StartsWith("+44161") || clean.StartsWith("+440161"))
                return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "Manchester" };
            if (clean.StartsWith("+44113") || clean.StartsWith("+440113"))
                return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "Leeds" };
            return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };
        }

        return new RegionInfo { Country = "United Kingdom", CountryCode = "GB", DefaultCity = "London" };
    }

    private static string? DetectCityFromAddress(string address, RegionInfo region)
    {
        var citiesToCheck = region.CountryCode == "NL" ? _knownNlCities : _knownUkCities;

        foreach (var city in citiesToCheck)
        {
            if (Regex.IsMatch(address, $@"\b{Regex.Escape(city)}\b", RegexOptions.IgnoreCase))
                return city;
        }

        return null;
    }

    private static bool ContainsRegionContext(string address, RegionInfo region)
    {
        var lower = address.ToLowerInvariant();
        return lower.Contains(region.Country.ToLower()) ||
               lower.Contains(region.CountryCode.ToLower()) ||
               lower.Contains(region.DefaultCity.ToLower()) ||
               Regex.IsMatch(address, @"\d{4}\s*[A-Z]{2}") || // NL postcode
               Regex.IsMatch(address, @"[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}"); // UK postcode
    }

    private static decimal CalculateFareFromDistance(double distanceMiles)
    {
        var fare = BASE_FARE + (RATE_PER_MILE * (decimal)distanceMiles);
        fare = Math.Max(fare, MIN_FARE);
        fare = Math.Ceiling(fare * 2) / 2; // Round to nearest 50c
        return fare;
    }

    public static string EstimateEta(double distanceMiles)
    {
        int travelMinutes = (int)Math.Ceiling(distanceMiles / 20.0 * 60);
        int totalMinutes = Math.Max(5, travelMinutes + 3);
        return $"{totalMinutes} minutes";
    }

    private static double EstimateFromKeywords(string pickup, string destination)
    {
        var combined = $"{pickup} {destination}".ToLowerInvariant();

        if (combined.Contains("airport") || combined.Contains("heathrow") ||
            combined.Contains("gatwick") || combined.Contains("schiphol"))
            return 15.0;
        if (combined.Contains("station") || combined.Contains("railway"))
            return 5.0;
        if (combined.Contains("hospital") || combined.Contains("clinic"))
            return 4.0;
        if (combined.Contains("shopping") || combined.Contains("mall"))
            return 3.0;

        return 4.0;
    }

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

    public static decimal ParseFare(string fare)
    {
        if (string.IsNullOrWhiteSpace(fare)) return 0m;
        var cleaned = fare.Replace("£", "").Replace("$", "").Replace("€", "").Trim();
        return decimal.TryParse(cleaned, out var value) ? value : 0m;
    }

    /// <summary>
    /// Extract house number from raw address text.
    /// </summary>
    public static string? ExtractHouseNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        var match = Regex.Match(address, @"^(\d+[a-zA-Z]?(?:-\d+[a-zA-Z]?)?)\s");
        if (match.Success) return match.Groups[1].Value;

        match = Regex.Match(address, @",\s*(\d+[a-zA-Z]?(?:-\d+[a-zA-Z]?)?)\s");
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    #endregion
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
    public string StreetNumber { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string FormattedAddress { get; set; } = "";
}

/// <summary>
/// Result from fare calculation including coordinates and address components.
/// </summary>
public class FareResult
{
    public string Fare { get; set; } = "";
    public string Eta { get; set; } = "";
    public double DistanceMiles { get; set; }

    // Pickup
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }

    // Destination
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }

    // Clarification
    public bool NeedsClarification { get; set; }
    public string[]? PickupAlternatives { get; set; }
    public string[]? DestAlternatives { get; set; }
}
