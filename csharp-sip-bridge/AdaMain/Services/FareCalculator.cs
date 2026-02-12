using System.Text.Json;
using AdaMain.Config;
using Microsoft.Extensions.Logging;

namespace AdaMain.Services;

/// <summary>
/// Fare calculator with Google Maps geocoding + address-dispatch edge function integration.
/// Ports the WinForms FareCalculator.ExtractAndCalculateWithAiAsync pattern.
/// </summary>
public sealed class FareCalculator : IFareCalculator
{
    private readonly ILogger<FareCalculator> _logger;
    private readonly GoogleMapsSettings _googleSettings;
    private readonly SupabaseSettings _supabaseSettings;
    private readonly HttpClient _httpClient;
    
    // Pricing constants
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
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AdaMain/1.0");
    }
    
    // =========================
    // AI-POWERED ADDRESS RESOLUTION (edge function)
    // =========================
    
    /// <summary>
    /// Resolve addresses via address-dispatch edge function (Gemini AI) + calculate fare.
    /// Returns coordinates, structured address components, and handles disambiguation.
    /// Falls back to basic geocoding on timeout/error (2s timeout).
    /// </summary>
    public async Task<FareResult> ExtractAndCalculateWithAiAsync(string? pickup, string? destination, string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(pickup) && string.IsNullOrWhiteSpace(destination))
            return new FareResult { Fare = "£4.00", Eta = "5 minutes" };

        try
        {
            _logger.LogInformation("🤖 Calling address-dispatch edge function: pickup='{Pickup}', dest='{Dest}'", pickup, destination);

            var requestBody = JsonSerializer.Serialize(new
            {
                pickup = pickup ?? "",
                destination = destination ?? "",
                phone = phoneNumber ?? ""
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
            {
                _logger.LogWarning("Edge function error: {Status} - {Body}", (int)response.StatusCode, responseJson);
                return await CalculateAsync(pickup, destination, phoneNumber);
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
            {
                _logger.LogWarning("Edge function returned error, falling back to geocoding");
                return await CalculateAsync(pickup, destination, phoneNumber);
            }

            var result = new FareResult();

            // Extract detected area
            string? detectedArea = null;
            if (root.TryGetProperty("detected_area", out var areaEl))
                detectedArea = areaEl.GetString();
            _logger.LogInformation("🌍 Detected area: {Area}", detectedArea ?? "unknown");

            // Parse pickup
            string resolvedPickup = pickup ?? "";
            double? pickupLat = null, pickupLon = null;
            string? pickupStreet = null, pickupNumber = null, pickupPostal = null, pickupCity = null;

            if (root.TryGetProperty("pickup", out var pickupEl))
            {
                if (pickupEl.TryGetProperty("address", out var addr))
                    resolvedPickup = addr.GetString() ?? pickup ?? "";
                if (pickupEl.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number)
                    pickupLat = lat.GetDouble();
                if (pickupEl.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number)
                    pickupLon = lon.GetDouble();
                if (pickupEl.TryGetProperty("street_name", out var st)) pickupStreet = st.GetString();
                if (pickupEl.TryGetProperty("street_number", out var num)) pickupNumber = num.GetString();
                if (pickupEl.TryGetProperty("postal_code", out var pc)) pickupPostal = pc.GetString();
                if (pickupEl.TryGetProperty("city", out var city)) pickupCity = city.GetString();

                if (pickupEl.TryGetProperty("is_ambiguous", out var ambig) && ambig.GetBoolean())
                {
                    result.NeedsClarification = true;
                    if (pickupEl.TryGetProperty("alternatives", out var alts))
                        result.PickupAlternatives = alts.EnumerateArray()
                            .Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();
                }
            }

            // Parse dropoff
            string resolvedDest = destination ?? "";
            double? destLat = null, destLon = null;
            string? destStreet = null, destNumber = null, destPostal = null, destCity = null;

            if (root.TryGetProperty("dropoff", out var dropoffEl))
            {
                if (dropoffEl.TryGetProperty("address", out var addr))
                    resolvedDest = addr.GetString() ?? destination ?? "";
                if (dropoffEl.TryGetProperty("lat", out var lat) && lat.ValueKind == JsonValueKind.Number)
                    destLat = lat.GetDouble();
                if (dropoffEl.TryGetProperty("lon", out var lon) && lon.ValueKind == JsonValueKind.Number)
                    destLon = lon.GetDouble();
                if (dropoffEl.TryGetProperty("street_name", out var st)) destStreet = st.GetString();
                if (dropoffEl.TryGetProperty("street_number", out var num)) destNumber = num.GetString();
                if (dropoffEl.TryGetProperty("postal_code", out var pc)) destPostal = pc.GetString();
                if (dropoffEl.TryGetProperty("city", out var city)) destCity = city.GetString();

                if (dropoffEl.TryGetProperty("is_ambiguous", out var ambig) && ambig.GetBoolean())
                {
                    result.NeedsClarification = true;
                    if (dropoffEl.TryGetProperty("alternatives", out var alts))
                        result.DestAlternatives = alts.EnumerateArray()
                            .Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();
                }
            }

            // Check global status
            if (root.TryGetProperty("status", out var statusEl) && statusEl.GetString() == "clarification_needed")
                result.NeedsClarification = true;
            
            // Extract the AI-generated clarification message (natural language question)
            if (root.TryGetProperty("clarification_message", out var clarifEl) && clarifEl.ValueKind == JsonValueKind.String)
                result.ClarificationMessage = clarifEl.GetString();

            _logger.LogInformation("📍 Resolved: pickup='{Pickup}' ({PLat},{PLon}), dest='{Dest}' ({DLat},{DLon})",
                resolvedPickup, pickupLat, pickupLon, resolvedDest, destLat, destLon);

            // Try to use edge-calculated fare first (single source of truth)
            string? edgeFare = null, edgeFareSpoken = null, edgeEta = null;
            if (root.TryGetProperty("fare", out var fareEl) && fareEl.ValueKind == JsonValueKind.Object)
            {
                if (fareEl.TryGetProperty("fare", out var fv)) edgeFare = fv.GetString();
                if (fareEl.TryGetProperty("fare_spoken", out var fs)) edgeFareSpoken = fs.GetString();
                if (fareEl.TryGetProperty("eta", out var ev)) edgeEta = ev.GetString();
                _logger.LogInformation("💰 Edge function returned fare: {Fare}, ETA: {Eta}", edgeFare, edgeEta);
            }

            // If Gemini returned valid coordinates for both, use them directly (skip geocoding!)
            if (pickupLat.HasValue && pickupLon.HasValue && destLat.HasValue && destLon.HasValue &&
                pickupLat.Value != 0 && destLat.Value != 0)
            {
                // Prefer edge-calculated fare, fall back to local calculation
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
                result.PickupLat = pickupLat.Value;
                result.PickupLon = pickupLon.Value;
                result.PickupStreet = pickupStreet;
                result.PickupNumber = pickupNumber;
                result.PickupPostalCode = pickupPostal;
                result.PickupCity = pickupCity ?? detectedArea;
                result.PickupFormatted = resolvedPickup;
                result.DestLat = destLat.Value;
                result.DestLon = destLon.Value;
                result.DestStreet = destStreet;
                result.DestNumber = destNumber;
                result.DestPostalCode = destPostal;
                result.DestCity = destCity ?? detectedArea;
                result.DestFormatted = resolvedDest;

                _logger.LogInformation("✅ Using Gemini coordinates directly — {Distance:F2} miles → {Fare}", distanceMiles, result.Fare);
                return result;
            }

            // Fallback: Gemini didn't return coords, geocode resolved addresses
            _logger.LogWarning("Gemini returned no coordinates, falling back to geocoding");
            var fareResult = await CalculateAsync(resolvedPickup, resolvedDest, phoneNumber);

            // Merge clarification info + AI-extracted components
            fareResult.NeedsClarification = result.NeedsClarification;
            fareResult.PickupAlternatives = result.PickupAlternatives;
            fareResult.DestAlternatives = result.DestAlternatives;
            fareResult.ClarificationMessage = result.ClarificationMessage;

            // Override with AI-extracted components where available (higher accuracy)
            if (!string.IsNullOrEmpty(pickupStreet)) fareResult.PickupStreet = pickupStreet;
            if (!string.IsNullOrEmpty(pickupNumber)) fareResult.PickupNumber = pickupNumber;
            if (!string.IsNullOrEmpty(pickupPostal)) fareResult.PickupPostalCode = pickupPostal;
            if (!string.IsNullOrEmpty(pickupCity)) fareResult.PickupCity = pickupCity;
            if (!string.IsNullOrEmpty(destStreet)) fareResult.DestStreet = destStreet;
            if (!string.IsNullOrEmpty(destNumber)) fareResult.DestNumber = destNumber;
            if (!string.IsNullOrEmpty(destPostal)) fareResult.DestPostalCode = destPostal;
            if (!string.IsNullOrEmpty(destCity)) fareResult.DestCity = destCity;
            if (!string.IsNullOrEmpty(detectedArea))
            {
                fareResult.PickupCity ??= detectedArea;
                fareResult.DestCity ??= detectedArea;
            }

            return fareResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⏱️ Edge function timeout (8s), falling back to geocoding");
            return await CalculateAsync(pickup, destination, phoneNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edge function error, falling back to geocoding");
            return await CalculateAsync(pickup, destination, phoneNumber);
        }
    }
    
    // =========================
    // BASIC GEOCODING (existing)
    // =========================
    
    public async Task<FareResult> CalculateAsync(string? pickup, string? destination, string? phoneNumber)
    {
        var result = new FareResult();
        
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
        {
            result.Fare = "£4.00";
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
            fare = Math.Round(fare * 2, MidpointRounding.AwayFromZero) / 2;
            
            var etaMinutes = (int)Math.Ceiling(distanceMiles / AvgSpeedMph * 60) + BufferMinutes;
            
            result.Fare = $"£{fare:F2}";
            result.Eta = $"{etaMinutes} minutes";
            
            _logger.LogInformation("Fare calculated: {Fare} for {Distance:F1} miles", result.Fare, distanceMiles);
        }
        else
        {
            result.Fare = "£8.00";
            result.Eta = "8 minutes";
            _logger.LogWarning("Using fallback fare - geocoding failed");
        }
        
        return result;
    }
    
    private async Task<GeoResult?> GeocodeAsync(string address, string regionBias)
    {
        try
        {
            if (!string.IsNullOrEmpty(_googleSettings.ApiKey))
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
                  $"&key={_googleSettings.ApiKey}";
        
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
