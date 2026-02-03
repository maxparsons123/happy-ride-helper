using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaxiSipBridge;

/// <summary>
/// Local fare calculator for taxi bookings (v3.1).
/// Uses Gemini AI for intelligent address extraction with geographic disambiguation:
/// - Landline area codes → city detection (e.g., +44 24 = Coventry)
/// - Mobile numbers → no geographic clue, uses destination context
/// - Text analysis → neighborhood/landmark detection
/// IMPORTANT: Gemini AI output is the SOURCE OF TRUTH for geocoding bias.
/// Fallback to Google Places Search API then Geocoding API then OpenStreetMap.
/// </summary>
public static class FareCalculator
{
    /// <summary>Starting fare in euros (€3.50 default)</summary>
    public const decimal BASE_FARE = 3.50m;

    /// <summary>Rate per mile in euros (€1.00 default)</summary>
    public const decimal RATE_PER_MILE = 1.00m;

    /// <summary>Minimum fare in euros</summary>
    public const decimal MIN_FARE = 4.00m;

    // API keys and URLs loaded from environment
    private static string? _googleMapsApiKey;
    private static string? _supabaseUrl;
    private static string? _supabaseAnonKey;

    // Cached caller location bias (phone → lat/lon)
    private static readonly Dictionary<string, (double Lat, double Lon, DateTime CachedAt)> _callerLocationCache = new();
    private static readonly TimeSpan _locationCacheExpiry = TimeSpan.FromHours(1);

    /// <summary>
    /// Optional log callback - wire this up to your OnLog handler for unified logging.
    /// Example: FareCalculator.OnLog = msg => OnLog?.Invoke(msg);
    /// </summary>
    public static Action<string>? OnLog;
    
    private static void Log(string msg) => OnLog?.Invoke(msg);

    // Known UK cities/towns for detection
    private static readonly HashSet<string> _knownUkCities = new(StringComparer.OrdinalIgnoreCase)
    {
        "London", "Birmingham", "Manchester", "Leeds", "Glasgow", "Liverpool", "Bristol", "Sheffield",
        "Edinburgh", "Leicester", "Coventry", "Bradford", "Cardiff", "Belfast", "Nottingham", "Kingston upon Hull",
        "Newcastle", "Stoke-on-Trent", "Southampton", "Derby", "Portsmouth", "Brighton", "Plymouth", "Wolverhampton",
        "Reading", "Northampton", "Luton", "Bolton", "Aberdeen", "Bournemouth", "Norwich", "Swindon", "Swansea",
        "Milton Keynes", "Watford", "Blackpool", "Dundee", "Ipswich", "Peterborough", "Slough", "Oxford",
        "Cambridge", "Gloucester", "Exeter", "Warrington", "York", "Bath", "Chester", "Blackburn", "Chelmsford",
        "Colchester", "Crawley", "Woking", "Guildford", "Harlow", "Basildon", "Maidstone", "Hastings", "Canterbury",
        "Eastbourne", "High Wycombe", "Aylesbury", "St Albans", "Hemel Hempstead", "Stevenage", "Welwyn"
    };

    // Known NL cities for detection
    private static readonly HashSet<string> _knownNlCities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Amsterdam", "Rotterdam", "Den Haag", "The Hague", "Utrecht", "Eindhoven", "Tilburg", "Groningen",
        "Almere", "Breda", "Nijmegen", "Apeldoorn", "Haarlem", "Arnhem", "Zaanstad", "Amersfoort",
        "Haarlemmermeer", "Hertogenbosch", "'s-Hertogenbosch", "Zoetermeer", "Zwolle", "Maastricht",
        "Leiden", "Dordrecht", "Ede", "Alphen aan den Rijn", "Alkmaar", "Delft", "Deventer", "Hilversum"
    };

    // Lazy-initialized to avoid static constructor failures (TypeInitializationException)
    private static HttpClient? _httpClientBacking;
    private static HttpClient GetHttpClient()
    {
        if (_httpClientBacking == null)
        {
            _httpClientBacking = new HttpClient { Timeout = TimeSpan.FromSeconds(10) }; // Increased for AI calls
            _httpClientBacking.DefaultRequestHeaders.Add("User-Agent", "TaxiSipBridge/3.0");
        }
        return _httpClientBacking;
    }

    /// <summary>
    /// Set the Google Maps API key (call once at startup from config)
    /// </summary>
    public static void SetGoogleMapsApiKey(string apiKey)
    {
        _googleMapsApiKey = apiKey;
        Log("🗺️ Google Maps API key configured (Places Search enabled)");
    }

    /// <summary>
    /// Set the Supabase URL and anon key for AI extraction (call once at startup)
    /// </summary>
    public static void SetSupabaseConfig(string url, string anonKey)
    {
        _supabaseUrl = url.TrimEnd('/');
        _supabaseAnonKey = anonKey;
        Log("🤖 Supabase configured for AI address extraction");
    }

    /// <summary>
    /// Extract and resolve addresses using Gemini AI with intelligent geographic disambiguation.
    /// Uses phone number patterns (landline vs mobile) to detect caller region.
    /// </summary>
    /// <param name="pickup">Raw pickup address spoken by caller</param>
    /// <param name="destination">Raw destination address spoken by caller</param>
    /// <param name="phoneNumber">Caller's phone number for region detection</param>
    /// <param name="conversation">Optional recent conversation context</param>
    /// <returns>AI extraction result with resolved addresses and clarification requests</returns>
    public static async Task<AiAddressExtractionResult> ExtractAddressesWithAiAsync(
        string? pickup,
        string? destination,
        string? phoneNumber,
        string? conversation = null)
    {
        var result = new AiAddressExtractionResult { Success = false };

        if (string.IsNullOrEmpty(_supabaseUrl) || string.IsNullOrEmpty(_supabaseAnonKey))
        {
            result.Status = "error";
            result.RawResponse = "Supabase not configured";
            Log("⚠️ AI extraction failed: Supabase not configured");
            return result;
        }

        try
        {
            var payload = new
            {
                pickup = pickup ?? "",
                destination = destination ?? "",
                phone = phoneNumber ?? "",
                conversation = conversation
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/functions/v1/address-extract")
            {
                Content = content
            };
            request.Headers.Add("Authorization", $"Bearer {_supabaseAnonKey}");

            Log($"🤖 AI extraction: pickup='{pickup}', dest='{destination}', phone='{phoneNumber}'");

            var response = await GetHttpClient().SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                result.Status = "error";
                result.RawResponse = responseBody;
                Log($"⚠️ AI extraction HTTP {(int)response.StatusCode}: {responseBody}");
                return result;
            }

            // Parse the AI response
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            result.Success = true;
            result.RawResponse = responseBody;

            // Extract region detection
            if (root.TryGetProperty("detected_region", out var region))
                result.DetectedRegion = region.GetString() ?? "";
            if (root.TryGetProperty("region_source", out var regionSource))
                result.RegionSource = regionSource.GetString() ?? "";

            // Extract pickup info
            if (root.TryGetProperty("pickup", out var pickupEl))
            {
                if (pickupEl.TryGetProperty("resolved", out var resolved))
                    result.PickupResolved = resolved.GetBoolean();
                if (pickupEl.TryGetProperty("address", out var addr))
                    result.PickupAddress = addr.GetString();
                if (pickupEl.TryGetProperty("house_number", out var houseNum))
                    result.PickupHouseNumber = houseNum.GetString();
                if (pickupEl.TryGetProperty("street", out var street))
                    result.PickupStreet = street.GetString();
                if (pickupEl.TryGetProperty("city", out var city))
                    result.PickupCity = city.GetString();
                if (pickupEl.TryGetProperty("confidence", out var conf))
                    result.PickupConfidence = conf.GetDouble();
                if (pickupEl.TryGetProperty("alternatives", out var alts))
                {
                    foreach (var alt in alts.EnumerateArray())
                    {
                        var altStr = alt.GetString();
                        if (!string.IsNullOrEmpty(altStr))
                            result.PickupAlternatives.Add(altStr);
                    }
                }
            }

            // Extract destination info
            if (root.TryGetProperty("destination", out var destEl))
            {
                if (destEl.TryGetProperty("resolved", out var resolved))
                    result.DestinationResolved = resolved.GetBoolean();
                if (destEl.TryGetProperty("address", out var addr))
                    result.DestinationAddress = addr.GetString();
                if (destEl.TryGetProperty("house_number", out var houseNum))
                    result.DestinationHouseNumber = houseNum.GetString();
                if (destEl.TryGetProperty("street", out var street))
                    result.DestinationStreet = street.GetString();
                if (destEl.TryGetProperty("city", out var city))
                    result.DestinationCity = city.GetString();
                if (destEl.TryGetProperty("confidence", out var conf))
                    result.DestinationConfidence = conf.GetDouble();
                if (destEl.TryGetProperty("alternatives", out var alts))
                {
                    foreach (var alt in alts.EnumerateArray())
                    {
                        var altStr = alt.GetString();
                        if (!string.IsNullOrEmpty(altStr))
                            result.DestinationAlternatives.Add(altStr);
                    }
                }
            }

            // Extract status
            if (root.TryGetProperty("status", out var status))
                result.Status = status.GetString() ?? "ready_to_book";
            if (root.TryGetProperty("clarification_message", out var clarification))
                result.ClarificationMessage = clarification.GetString();

            // Extract phone analysis
            if (root.TryGetProperty("phone_analysis", out var phoneAnalysis))
            {
                if (phoneAnalysis.TryGetProperty("isMobile", out var isMobile))
                    result.IsMobile = isMobile.GetBoolean();
                if (phoneAnalysis.TryGetProperty("country", out var country))
                    result.PhoneCountry = country.GetString();
                if (phoneAnalysis.TryGetProperty("city", out var cityFromPhone))
                    result.PhoneCityFromAreaCode = cityFromPhone.GetString();
            }

            // Detailed logging of AI extraction results
            Log($"════════════════════════════════════════════════════");
            Log($"🤖 AI ADDRESS EXTRACTION RESULTS (V3)");
            Log($"────────────────────────────────────────────────────");
            Log($"📱 Phone Analysis:");
            Log($"   Country: {result.PhoneCountry ?? "Unknown"}");
            Log($"   Is Mobile: {result.IsMobile}");
            Log($"   City from Area Code: {result.PhoneCityFromAreaCode ?? "(none - mobile or unknown)"}");
            Log($"────────────────────────────────────────────────────");
            Log($"🌍 Detected Region: {result.DetectedRegion}");
            Log($"   Source: {result.RegionSource}");
            Log($"────────────────────────────────────────────────────");
            Log($"📍 PICKUP:");
            Log($"   Resolved: {result.PickupResolved}");
            Log($"   Full Address: {result.PickupAddress ?? "(empty)"}");
            Log($"   House Number: '{result.PickupHouseNumber ?? ""}'");
            Log($"   Street: '{result.PickupStreet ?? ""}'");
            Log($"   City: '{result.PickupCity ?? ""}'");
            Log($"   Confidence: {result.PickupConfidence:P0}");
            if (result.PickupAlternatives.Count > 0)
            {
                Log($"   Alternatives: {string.Join(", ", result.PickupAlternatives)}");
            }
            Log($"────────────────────────────────────────────────────");
            Log($"🏁 DESTINATION:");
            Log($"   Resolved: {result.DestinationResolved}");
            Log($"   Full Address: {result.DestinationAddress ?? "(empty)"}");
            Log($"   House Number: '{result.DestinationHouseNumber ?? ""}'");
            Log($"   Street: '{result.DestinationStreet ?? ""}'");
            Log($"   City: '{result.DestinationCity ?? ""}'");
            Log($"   Confidence: {result.DestinationConfidence:P0}");
            if (result.DestinationAlternatives.Count > 0)
            {
                Log($"   Alternatives: {string.Join(", ", result.DestinationAlternatives)}");
            }
            Log($"────────────────────────────────────────────────────");
            Log($"📋 Status: {result.Status}");
            if (result.Status == "clarification_required")
            {
                Log($"⚠️ CLARIFICATION NEEDED: {result.ClarificationMessage}");
            }
            Log($"════════════════════════════════════════════════════");

            return result;
        }
        catch (Exception ex)
        {
            result.Status = "error";
            result.RawResponse = ex.Message;
            Log($"⚠️ AI extraction error: {ex.Message}");
            return result;
        }
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
        Log($"🌍 Detected region: {regionBias.Country} (code: {regionBias.CountryCode})");

        try
        {
            // Geocode both addresses in parallel with phone-based location bias
            var pickupTask = GeocodeAddressAsync(pickup, regionBias, phoneNumber);
            var destTask = GeocodeAddressAsync(destination, regionBias, phoneNumber);
            
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

                Log($"📏 Geocoded distance: {distanceMiles:F2} miles");
                Log($"📍 Pickup: {pickup} → {pickupGeo.FormattedAddress} ({pickupGeo.Lat:F4}, {pickupGeo.Lon:F4})");
                Log($"🏁 Dest: {destination} → {destGeo.FormattedAddress} ({destGeo.Lat:F4}, {destGeo.Lon:F4})");

                return result;
            }
            else
            {
                // Log which geocoding failed
                if (pickupGeo == null)
                    Log($"⚠️ Pickup geocoding FAILED for: '{pickup}'");
                if (destGeo == null)
                    Log($"⚠️ Destination geocoding FAILED for: '{destination}'");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Geocoding failed: {ex.Message}");
        }

        // Fallback to keyword-based estimation
        Log($"🔄 Using keyword fallback for: '{pickup}' → '{destination}'");
        var fallbackDistance = EstimateFromKeywords(pickup, destination);
        var fallbackFare = CalculateFareFromDistanceDecimal(fallbackDistance);
        result.Fare = FormatFare(fallbackFare);
        result.Eta = EstimateEta(fallbackDistance);
        result.DistanceMiles = fallbackDistance;
        return result;
    }

    /// <summary>
    /// Calculate fare using Gemini AI for intelligent address resolution.
    /// This is the PREFERRED method - uses AI-detected region as the source of truth for geocoding bias.
    /// </summary>
    /// <param name="rawPickup">Raw pickup address from user input</param>
    /// <param name="rawDestination">Raw destination address from user input</param>
    /// <param name="phoneNumber">Caller phone number for AI region detection</param>
    public static async Task<FareResult> CalculateFareWithAiAsync(
        string? rawPickup, 
        string? rawDestination,
        string? phoneNumber)
    {
        var result = new FareResult
        {
            Fare = FormatFare(MIN_FARE),
            Eta = "5 minutes",
            DistanceMiles = 0
        };

        if (string.IsNullOrWhiteSpace(rawPickup) || string.IsNullOrWhiteSpace(rawDestination))
            return result;

        // Step 1: Use Gemini AI to extract and resolve addresses with intelligent region detection
        var aiResult = await ExtractAddressesWithAiAsync(rawPickup, rawDestination, phoneNumber);
        
        if (!aiResult.Success)
        {
            Log($"⚠️ AI extraction failed, falling back to phone-based detection");
            return await CalculateFareWithCoordsAsync(rawPickup, rawDestination, phoneNumber);
        }

        // Step 2: Build RegionInfo from AI result (SOURCE OF TRUTH)
        var region = new RegionInfo
        {
            Country = aiResult.PhoneCountry == "NL" ? "Netherlands" : "United Kingdom",
            CountryCode = aiResult.PhoneCountry == "NL" ? "NL" : "GB",
            DefaultCity = aiResult.DetectedRegion ?? aiResult.PickupCity ?? aiResult.DestinationCity ?? "London"
        };

        Log($"🤖 AI region bias: {region.DefaultCity} (source: {aiResult.RegionSource})");

        // Step 3: Get resolved addresses from AI (or fallback to raw)
        var pickupToGeocode = aiResult.PickupAddress ?? rawPickup;
        var destToGeocode = aiResult.DestinationAddress ?? rawDestination;

        try
        {
            // Geocode using AI-detected region as bias
            var pickupTask = GeocodeAddressWithRegionAsync(pickupToGeocode, region);
            var destTask = GeocodeAddressWithRegionAsync(destToGeocode, region);
            
            await Task.WhenAll(pickupTask, destTask);
            
            var pickupGeo = await pickupTask;
            var destGeo = await destTask;

            // Store pickup details with house number priority: AI → Geocoder → Raw extraction
            if (pickupGeo != null)
            {
                result.PickupLat = pickupGeo.Lat;
                result.PickupLon = pickupGeo.Lon;
                result.PickupStreet = !string.IsNullOrEmpty(aiResult.PickupStreet) 
                    ? aiResult.PickupStreet 
                    : pickupGeo.StreetName;
                result.PickupNumber = !string.IsNullOrEmpty(aiResult.PickupHouseNumber) 
                    ? aiResult.PickupHouseNumber  // Priority 1: AI-extracted
                    : !string.IsNullOrEmpty(pickupGeo.StreetNumber) 
                        ? pickupGeo.StreetNumber  // Priority 2: Geocoder
                        : ExtractHouseNumber(rawPickup); // Priority 3: Raw extraction
                result.PickupPostalCode = pickupGeo.PostalCode;
                result.PickupCity = !string.IsNullOrEmpty(aiResult.PickupCity) 
                    ? aiResult.PickupCity 
                    : !string.IsNullOrEmpty(pickupGeo.City) 
                        ? pickupGeo.City 
                        : aiResult.DetectedRegion;
                result.PickupFormatted = pickupGeo.FormattedAddress;
            }

            // Store destination details with house number priority: AI → Geocoder → Raw extraction
            if (destGeo != null)
            {
                result.DestLat = destGeo.Lat;
                result.DestLon = destGeo.Lon;
                result.DestStreet = !string.IsNullOrEmpty(aiResult.DestinationStreet) 
                    ? aiResult.DestinationStreet 
                    : destGeo.StreetName;
                result.DestNumber = !string.IsNullOrEmpty(aiResult.DestinationHouseNumber) 
                    ? aiResult.DestinationHouseNumber  // Priority 1: AI-extracted
                    : !string.IsNullOrEmpty(destGeo.StreetNumber) 
                        ? destGeo.StreetNumber  // Priority 2: Geocoder
                        : ExtractHouseNumber(rawDestination); // Priority 3: Raw extraction
                result.DestPostalCode = destGeo.PostalCode;
                result.DestCity = !string.IsNullOrEmpty(aiResult.DestinationCity) 
                    ? aiResult.DestinationCity 
                    : !string.IsNullOrEmpty(destGeo.City) 
                        ? destGeo.City 
                        : aiResult.DetectedRegion;
                result.DestFormatted = destGeo.FormattedAddress;
            }

            if (pickupGeo != null && destGeo != null)
            {
                var distanceMiles = HaversineDistanceMiles(
                    pickupGeo.Lat, pickupGeo.Lon,
                    destGeo.Lat, destGeo.Lon);

                var fare = CalculateFareFromDistanceDecimal(distanceMiles);
                var eta = EstimateEta(distanceMiles);

                result.Fare = FormatFare(fare);
                result.Eta = eta;
                result.DistanceMiles = distanceMiles;

                Log($"✓ AI-based fare: {distanceMiles:F2} miles = {result.Fare}");
                Log($"✓ Pickup: {result.PickupNumber} {result.PickupStreet}, {result.PickupCity}");
                Log($"✓ Dest: {result.DestNumber} {result.DestStreet}, {result.DestCity}");

                return result;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ AI-based geocoding failed: {ex.Message}");
        }

        // Fallback to keyword estimation
        Log($"🔄 Using keyword fallback");
        var fallbackDistance = EstimateFromKeywords(rawPickup, rawDestination);
        var fallbackFare = CalculateFareFromDistanceDecimal(fallbackDistance);
        result.Fare = FormatFare(fallbackFare);
        result.Eta = EstimateEta(fallbackDistance);
        result.DistanceMiles = fallbackDistance;
        return result;
    }

    /// <summary>
    /// Geocode address using a pre-determined region (from AI).
    /// </summary>
    private static async Task<GeocodedAddress?> GeocodeAddressWithRegionAsync(string address, RegionInfo region)
    {
        // Use the existing geocoding chain but with the AI-determined region
        return await GeocodeAddressAsync(address, region, null);
    }

    /// <summary>
    /// Extract house number from user's raw address input.
    /// Handles formats like "52A", "52-8", "7", etc.
    /// </summary>
    public static string? ExtractHouseNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        // Common patterns:
        // "52A David Road" → "52A"
        // "52-8 David Road" → "52-8"  
        // "7 Russell Street" → "7"
        // "Flat 3, 15 High Street" → "3, 15" or just "15"?
        // "15a-17b Some Road" → "15a-17b"

        // Pattern 1: Leading number with optional letter/hyphen suffix
        var leadingMatch = Regex.Match(address, @"^(\d+[a-zA-Z]?(?:-\d+[a-zA-Z]?)?)\s");
        if (leadingMatch.Success)
        {
            return leadingMatch.Groups[1].Value;
        }

        // Pattern 2: Number after comma (e.g., "Flat 3, 15 High Street" → extract "15")
        var afterCommaMatch = Regex.Match(address, @",\s*(\d+[a-zA-Z]?(?:-\d+[a-zA-Z]?)?)\s");
        if (afterCommaMatch.Success)
        {
            return afterCommaMatch.Groups[1].Value;
        }

        // Pattern 3: Any standalone number before a word that looks like a street name
        var beforeStreetMatch = Regex.Match(address, @"(\d+[a-zA-Z]?(?:-\d+[a-zA-Z]?)?)\s+(?:[A-Z][a-z]+\s*(?:Street|Road|Avenue|Lane|Drive|Way|Close|Court|Place|Terrace|Gardens|Row|Walk|Grove|Square|Crescent|Hill|Rise|Park|View|Mews|Yard|Gate|Passage))", RegexOptions.IgnoreCase);
        if (beforeStreetMatch.Success)
        {
            return beforeStreetMatch.Groups[1].Value;
        }

        return null;
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
        Log($"🔍 Verifying address: '{address}' (region: {region.CountryCode})");

        try
        {
            var geocoded = await GeocodeAddressAsync(address, region, phoneNumber);
            
            if (geocoded == null)
            {
                result.Reason = "Address not found by geocoder";
                Log($"❌ Address verification failed: '{address}'");
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

            Log($"✓ Verified: '{address}' → {result.Number} {result.Street}, {result.City} ({result.PostalCode}) [conf: {result.Confidence:P0}]");
            return result;
        }
        catch (Exception ex)
        {
            result.Reason = $"Geocoding error: {ex.Message}";
            Log($"❌ Address verification error: {ex.Message}");
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
    /// Geocode an address using Google Places Search API (preferred) with intelligent biasing,
    /// then fallback to Geocoding API, then OSM.
    /// </summary>
    private static async Task<GeocodedAddress?> GeocodeAddressAsync(string address, RegionInfo region, string? phoneNumber = null)
    {
        // Try Google Places Search first if API key is available
        if (!string.IsNullOrEmpty(_googleMapsApiKey))
        {
            var placesResult = await GeocodeWithGooglePlacesAsync(address, region, phoneNumber);
            if (placesResult != null) return placesResult;

            // Fallback to standard Geocoding API
            var googleResult = await GeocodeWithGoogleAsync(address, region);
            if (googleResult != null) return googleResult;
        }

        // Final fallback to OpenStreetMap
        return await GeocodeWithOsmAsync(address, region);
    }

    /// <summary>
    /// Get or geocode the caller's approximate location from their phone number.
    /// Uses the phone prefix to determine country/city, then caches the result.
    /// </summary>
    private static async Task<(double Lat, double Lon)?> GetCallerLocationBiasAsync(string? phoneNumber, RegionInfo region)
    {
        if (string.IsNullOrEmpty(phoneNumber))
            return null;

        // Check cache first
        if (_callerLocationCache.TryGetValue(phoneNumber, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < _locationCacheExpiry)
        {
            return (cached.Lat, cached.Lon);
        }

        // Geocode the default city for this region
        try
        {
            var cityQuery = $"{region.DefaultCity}, {region.Country}";
            var url = $"https://maps.googleapis.com/maps/api/geocode/json" +
                      $"?address={Uri.EscapeDataString(cityQuery)}" +
                      $"&key={_googleMapsApiKey}";

            var response = await GetHttpClient().GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "OK" &&
                root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var geometry = results[0].GetProperty("geometry").GetProperty("location");
                var lat = geometry.GetProperty("lat").GetDouble();
                var lon = geometry.GetProperty("lng").GetDouble();

                _callerLocationCache[phoneNumber] = (lat, lon, DateTime.UtcNow);
                Log($"📍 Caller location bias: {region.DefaultCity} ({lat:F4}, {lon:F4})");
                return (lat, lon);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Failed to get caller location bias: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Detect city/area names mentioned in the address text for additional bias.
    /// </summary>
    private static string? DetectCityFromAddress(string address, RegionInfo region)
    {
        var lowerAddress = address.ToLowerInvariant();

        // Check against known cities based on region
        var citiesToCheck = region.CountryCode switch
        {
            "NL" => _knownNlCities,
            _ => _knownUkCities
        };

        foreach (var city in citiesToCheck)
        {
            if (Regex.IsMatch(address, $@"\b{Regex.Escape(city)}\b", RegexOptions.IgnoreCase))
            {
                Log($"🏙️ Detected city in address: {city}");
                return city;
            }
        }

        return null;
    }

    /// <summary>
    /// Geocode using Google Places Text Search API with intelligent biasing.
    /// This is more accurate than Geocoding API for partial addresses and landmarks.
    /// </summary>
    private static async Task<GeocodedAddress?> GeocodeWithGooglePlacesAsync(string address, RegionInfo region, string? phoneNumber)
    {
        try
        {
            // Build search query with region context
            var searchQuery = address;
            var detectedCity = DetectCityFromAddress(address, region);
            
            // If no city detected in address, append the region's default city for context
            if (detectedCity == null && !ContainsRegionContext(address, region))
            {
                searchQuery = $"{address}, {region.DefaultCity}, {region.Country}";
            }
            else if (!ContainsRegionContext(address, region))
            {
                searchQuery = $"{address}, {region.Country}";
            }

            // Get caller location for bias
            var callerLocation = await GetCallerLocationBiasAsync(phoneNumber, region);

            // Build Places Text Search URL
            var url = $"https://maps.googleapis.com/maps/api/place/textsearch/json" +
                      $"?query={Uri.EscapeDataString(searchQuery)}" +
                      $"&region={region.CountryCode.ToLower()}" +
                      $"&key={_googleMapsApiKey}";

            // Add location bias if available (50km radius)
            if (callerLocation.HasValue)
            {
                url += $"&location={callerLocation.Value.Lat},{callerLocation.Value.Lon}";
                url += "&radius=50000"; // 50km bias radius
            }

            Log($"🔍 Places Search: '{searchQuery}'" + 
                (callerLocation.HasValue ? $" (biased to {region.DefaultCity})" : ""));

            var response = await GetHttpClient().GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status))
            {
                var statusStr = status.GetString();
                if (statusStr != "OK" && statusStr != "ZERO_RESULTS")
                {
                    Log($"⚠️ Places API status: {statusStr}");
                }

                if (statusStr == "OK" && root.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
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

                    // Get place name if it's a landmark/business
                    string? placeName = null;
                    if (first.TryGetProperty("name", out var name))
                    {
                        placeName = name.GetString();
                    }

                    // Now get detailed address components using Place Details API
                    if (first.TryGetProperty("place_id", out var placeIdEl))
                    {
                        var placeId = placeIdEl.GetString();
                        if (!string.IsNullOrEmpty(placeId))
                        {
                            var detailsResult = await GetPlaceDetailsAsync(placeId, result);
                            if (detailsResult != null)
                            {
                                result = detailsResult;
                            }
                        }
                    }

                    // If place name is useful, include it
                    if (!string.IsNullOrEmpty(placeName) && 
                        !result.FormattedAddress.Contains(placeName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.FormattedAddress = $"{placeName}, {result.FormattedAddress}";
                    }

                    result.IsVerified = true;
                    result.Confidence = 0.9; // High confidence for Places API matches

                    Log($"✓ Places found: {address} → {result.FormattedAddress}");
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Places Search error for '{address}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Get detailed address components from Place Details API.
    /// </summary>
    private static async Task<GeocodedAddress?> GetPlaceDetailsAsync(string placeId, GeocodedAddress baseResult)
    {
        try
        {
            var url = $"https://maps.googleapis.com/maps/api/place/details/json" +
                      $"?place_id={placeId}" +
                      $"&fields=address_components,formatted_address,geometry" +
                      $"&key={_googleMapsApiKey}";

            var response = await GetHttpClient().GetStringAsync(url);
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

                // Update coords if available
                if (result.TryGetProperty("geometry", out var geometry) &&
                    geometry.TryGetProperty("location", out var location))
                {
                    address.Lat = location.GetProperty("lat").GetDouble();
                    address.Lon = location.GetProperty("lng").GetDouble();
                }

                // Update formatted address
                if (result.TryGetProperty("formatted_address", out var formatted))
                {
                    address.FormattedAddress = formatted.GetString() ?? address.FormattedAddress;
                }

                // Parse address components
                if (result.TryGetProperty("address_components", out var components))
                {
                    foreach (var comp in components.EnumerateArray())
                    {
                        var types = comp.GetProperty("types").EnumerateArray()
                                       .Select(t => t.GetString()).ToList();
                        var longName = comp.GetProperty("long_name").GetString() ?? "";

                        if (types.Contains("street_number"))
                        {
                            address.StreetNumber = longName;
                        }
                        else if (types.Contains("route"))
                        {
                            address.StreetName = longName;
                        }
                        else if (types.Contains("postal_code"))
                        {
                            address.PostalCode = longName;
                        }
                        else if (types.Contains("locality"))
                        {
                            address.City = longName;
                        }
                        else if (types.Contains("postal_town") && string.IsNullOrEmpty(address.City))
                        {
                            address.City = longName;
                        }
                        else if (types.Contains("administrative_area_level_2") && string.IsNullOrEmpty(address.City))
                        {
                            address.City = longName;
                        }
                    }
                }

                return address;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Place Details error: {ex.Message}");
        }

        return baseResult;
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

                    Log($"✓ Google geocoded: {address} → {result.City}, {result.PostalCode}");
                    return result;
                }
            }
            else
            {
                var statusStr = status.GetString();
                Log($"⚠️ Google geocode status: {statusStr}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Google geocode error for '{address}': {ex.Message}");
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

                Log($"✓ OSM geocoded: {address} → {result.City}");
                return result;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ OSM geocode error for '{address}': {ex.Message}");
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

/// <summary>
/// Result from AI-powered address extraction using Gemini.
/// Includes intelligent geographic disambiguation based on phone number patterns.
/// </summary>
public class AiAddressExtractionResult
{
    public bool Success { get; set; }
    public string DetectedRegion { get; set; } = "";
    public string RegionSource { get; set; } = ""; // landline_area_code, destination_landmark, text_mention, unknown
    
    // Pickup resolution
    public bool PickupResolved { get; set; }
    public string? PickupAddress { get; set; }
    public string? PickupHouseNumber { get; set; } // e.g., "52A", "7", "15-17"
    public string? PickupStreet { get; set; } // Street name only
    public string? PickupCity { get; set; }
    public double PickupConfidence { get; set; }
    public List<string> PickupAlternatives { get; set; } = new();
    
    // Destination resolution
    public bool DestinationResolved { get; set; }
    public string? DestinationAddress { get; set; }
    public string? DestinationHouseNumber { get; set; } // e.g., "52A", "7", "15-17"
    public string? DestinationStreet { get; set; } // Street name only
    public string? DestinationCity { get; set; }
    public double DestinationConfidence { get; set; }
    public List<string> DestinationAlternatives { get; set; } = new();
    
    // Status
    public string Status { get; set; } = "ready_to_book"; // ready_to_book, clarification_required, error
    public string? ClarificationMessage { get; set; }
    
    // Phone analysis
    public bool IsMobile { get; set; }
    public string? PhoneCountry { get; set; }
    public string? PhoneCityFromAreaCode { get; set; }
    
    // Raw/debug
    public string? RawResponse { get; set; }
}
