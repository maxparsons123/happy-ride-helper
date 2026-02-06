using System;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Compatibility API for older call flows that referenced static methods on FareCalculator.
/// This keeps existing behavior while the core FareCalculator remains instance-based.
/// </summary>
public partial class FareCalculator
{
    private static readonly object _staticLock = new();
    private static string? _staticGoogleMapsApiKey;
    private static FareCalculator? _staticCalculator;
    private static EdgeAddressExtractor? _staticEdgeExtractor;

    /// <summary>
    /// Configures the Google Maps key used by the default calculator instance.
    /// </summary>
    public static void SetGoogleMapsApiKey(string apiKey)
    {
        lock (_staticLock)
        {
            _staticGoogleMapsApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
            _staticCalculator = null; // rebuild lazily
        }
    }

    /// <summary>
    /// Configures the backend function endpoint credentials used by EdgeAddressExtractor.
    /// </summary>
    public static void SetSupabaseConfig(string backendBaseUrl, string backendAnonKey)
    {
        lock (_staticLock)
        {
            _staticEdgeExtractor = new EdgeAddressExtractor(backendBaseUrl, backendAnonKey);
            EdgeAddressExtractor.OnLog = msg => OnLog?.Invoke(msg);
        }
    }

    private static FareCalculator GetDefaultCalculator()
    {
        lock (_staticLock)
        {
            _staticCalculator ??= new FareCalculator(_staticGoogleMapsApiKey);
            return _staticCalculator;
        }
    }

    private static EdgeAddressExtractor? GetEdgeExtractor()
    {
        lock (_staticLock)
        {
            return _staticEdgeExtractor;
        }
    }
    
    /// <summary>
    /// Quick check if an address likely already contains a city hint.
    /// Avoids unnecessary Edge AI calls for complete addresses like "52A David Road, Coventry".
    /// </summary>
    private static bool ContainsCityHint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        
        var knownCities = new[] 
        { 
            "London", "Birmingham", "Manchester", "Leeds", "Glasgow", "Liverpool", "Bristol", 
            "Sheffield", "Edinburgh", "Leicester", "Coventry", "Bradford", "Cardiff", "Belfast",
            "Nottingham", "Newcastle", "Southampton", "Derby", "Portsmouth", "Brighton", "Plymouth",
            "York", "Bath", "Chester", "Exeter", "Norwich", "Giffnock", "Westminster"
        };
        
        var lower = address.ToLowerInvariant();
        return knownCities.Any(city => lower.Contains(city.ToLowerInvariant()));
    }

    /// <summary>
    /// Legacy: address extraction via backend function.
    /// Returns a shape compatible with older call handlers (pickup/dropoff/status).
    /// </summary>
    public static async Task<EdgeDispatchResponse?> ExtractAddressesWithLovableAiAsync(
        string? pickup,
        string? destination,
        string? phoneNumber = null)
    {
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
            return null;

        var extractor = GetEdgeExtractor();
        if (extractor == null)
        {
            Log("⚠️ [Edge] Extractor not configured. Call SetSupabaseConfig() first.");
            return null;
        }

        var extraction = await extractor.ExtractAsync(pickup, destination, phoneNumber).ConfigureAwait(false);
        return extraction == null ? null : EdgeDispatchResponse.From(extraction);
    }

    /// <summary>
    /// Legacy: verify + enrich address (geocode).
    /// </summary>
    public static async Task<AddressVerifyResult> VerifyAddressAsync(string? address, string? phoneNumber = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address))
                return AddressVerifyResult.Fail("");

            var calc = GetDefaultCalculator();
            var geo = await calc.GeocodeAsync(address, phoneNumber).ConfigureAwait(false);
            if (geo == null) return AddressVerifyResult.Fail(address);

            return AddressVerifyResult.Ok(
                verifiedAddress: geo.FormattedAddress,
                lat: geo.Lat,
                lon: geo.Lon,
                street: geo.StreetName,
                number: geo.StreetNumber,
                postalCode: geo.PostalCode,
                city: geo.City);
        }
        catch (Exception ex)
        {
            Log($"⚠️ VerifyAddressAsync error: {ex.Message}");
            return AddressVerifyResult.Fail(address ?? "");
        }
    }

    /// <summary>
    /// Legacy: geocode pickup & destination and compute fare/ETA.
    /// Uses Edge AI extraction first to get city-biased addresses, then geocodes with that city context.
    /// </summary>
    /// <param name="skipEdgeExtraction">If true, skip Edge AI extraction (already done by caller).</param>
    public static async Task<FareResult> CalculateFareWithCoordsAsync(
        string? pickup,
        string? destination,
        string? phoneNumber = null,
        CancellationToken cancellationToken = default,
        bool skipEdgeExtraction = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
                return new FareResult { Fare = "£12.50", Eta = "6 minutes" };

            var calc = GetDefaultCalculator();

            // Skip Edge AI extraction if:
            // 1. Caller already did extraction (skipEdgeExtraction = true)
            // 2. Addresses already contain a city (v6.2 optimization)
            bool addressesAlreadyComplete = skipEdgeExtraction || 
                (ContainsCityHint(pickup) && ContainsCityHint(destination));
            
            if (!addressesAlreadyComplete)
            {
                var extractor = GetEdgeExtractor();
                if (extractor != null)
                {
                    try
                    {
                        Log("💰 Starting Lovable AI address extraction...");
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(2));
                        
                        var extractionTask = extractor.ExtractAsync(pickup, destination, phoneNumber);
                        
                        // Wait for extraction or cancellation
                        var completedTask = await Task.WhenAny(
                            extractionTask, 
                            Task.Delay(Timeout.Infinite, linkedCts.Token)
                        ).ConfigureAwait(false);

                        linkedCts.Token.ThrowIfCancellationRequested();
                        
                        if (completedTask == extractionTask && extractionTask.Result != null)
                        {
                            var extraction = extractionTask.Result;

                            // Use edge-extracted addresses with city appended for better geocoding
                            var resolvedPickup = extraction.PickupAddress ?? pickup;
                            var resolvedDest = extraction.DestinationAddress ?? destination;

                            Log($"💰 Using Edge addresses: pickup='{resolvedPickup}', dest='{resolvedDest}'");

                            // If Gemini returned valid coordinates, use them directly (skip OSM!)
                            if (extraction.PickupLat.HasValue && extraction.PickupLon.HasValue &&
                                extraction.DestinationLat.HasValue && extraction.DestinationLon.HasValue &&
                                extraction.PickupLat.Value != 0 && extraction.DestinationLat.Value != 0)
                            {
                                Log($"✅ Using Gemini coordinates directly (skip OSM)");
                                var result = calc.CalculateFromCoords(
                                    extraction.PickupLat.Value, extraction.PickupLon.Value,
                                    extraction.DestinationLat.Value, extraction.DestinationLon.Value);

                                // Enrich with Edge-extracted address components
                                result.PickupStreet = extraction.PickupStreet;
                                result.PickupNumber = extraction.PickupHouseNumber;
                                result.PickupPostalCode = extraction.PickupPostalCode;
                                result.PickupCity = extraction.PickupCity ?? extraction.DetectedArea;
                                result.PickupFormatted = resolvedPickup;
                                result.DestStreet = extraction.DestinationStreet;
                                result.DestNumber = extraction.DestinationHouseNumber;
                                result.DestPostalCode = extraction.DestinationPostalCode;
                                result.DestCity = extraction.DestinationCity ?? extraction.DetectedArea;
                                result.DestFormatted = resolvedDest;

                                Log($"💰 Quote: {result.Fare} ({result.DistanceMiles:F2} miles, pickup: {result.PickupCity}, dest: {result.DestCity})");
                                return result;
                            }

                            // Fallback: Gemini didn't return coords, use OSM geocoding with resolved addresses
                            cancellationToken.ThrowIfCancellationRequested();
                            var osmResult = await calc.CalculateAsync(resolvedPickup, resolvedDest, phoneNumber).ConfigureAwait(false);

                            // Enrich with Edge-extracted city info (may be more accurate than geocoding)
                            if (!string.IsNullOrEmpty(extraction.DetectedArea))
                            {
                                if (string.IsNullOrEmpty(osmResult.PickupCity))
                                    osmResult.PickupCity = extraction.DetectedArea;
                                if (string.IsNullOrEmpty(osmResult.DestCity))
                                    osmResult.DestCity = extraction.DetectedArea;
                            }

                            Log($"💰 Quote: {osmResult.Fare} (pickup: {osmResult.PickupCity}, dest: {osmResult.DestCity})");
                            return osmResult;
                        }
                        else
                        {
                            Log("⏱️ Lovable AI extraction timed out — using raw addresses");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("⏱️ Address extraction cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ Edge extraction error: {ex.Message} — using raw addresses");
                    }
                }
            }

            // Fallback: geocode raw addresses
            cancellationToken.ThrowIfCancellationRequested();
            return await calc.CalculateAsync(pickup, destination, phoneNumber).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation to caller
        }
        catch (Exception ex)
        {
            Log($"⚠️ CalculateFareWithCoordsAsync error: {ex.Message}");
            return new FareResult { Fare = "£12.50", Eta = "6 minutes" };
        }
    }

    // ==== Compatibility DTOs (keep old property names used by call handlers) ====

    public class EdgeDispatchResponse
    {
        public string? detected_area { get; set; }
        public PhoneAnalysis? phone_analysis { get; set; }
        public EdgeAddress? pickup { get; set; }
        public EdgeAddress? dropoff { get; set; }
        public string status { get; set; } = "ready";

        public static EdgeDispatchResponse From(AddressExtractionResult extraction)
        {
            var status = extraction.Status;
            if (extraction.NeedsClarification && (status == "ready" || status == "ready_to_book"))
                status = "clarification_needed";

            return new EdgeDispatchResponse
            {
                detected_area = extraction.DetectedArea,
                status = status,
                phone_analysis = new PhoneAnalysis
                {
                    detected_country = extraction.PhoneCountry,
                    is_mobile = extraction.IsMobile,
                    landline_city = extraction.LandlineCity
                },
                pickup = new EdgeAddress
                {
                    address = extraction.PickupAddress,
                    lat = extraction.PickupLat,
                    lon = extraction.PickupLon,
                    street_name = extraction.PickupStreet,
                    street_number = extraction.PickupHouseNumber,
                    postal_code = extraction.PickupPostalCode,
                    city = extraction.PickupCity,
                    is_ambiguous = extraction.PickupAmbiguous,
                    alternatives = extraction.PickupAlternatives
                },
                dropoff = new EdgeAddress
                {
                    address = extraction.DestinationAddress,
                    lat = extraction.DestinationLat,
                    lon = extraction.DestinationLon,
                    street_name = extraction.DestinationStreet,
                    street_number = extraction.DestinationHouseNumber,
                    postal_code = extraction.DestinationPostalCode,
                    city = extraction.DestinationCity,
                    is_ambiguous = extraction.DestinationAmbiguous,
                    alternatives = extraction.DestinationAlternatives
                }
            };
        }
    }

    public class PhoneAnalysis
    {
        public string? detected_country { get; set; }
        public bool is_mobile { get; set; }
        public string? landline_city { get; set; }
    }

    public class EdgeAddress
    {
        public string? address { get; set; }
        public double? lat { get; set; }
        public double? lon { get; set; }
        public string? street_name { get; set; }
        public string? street_number { get; set; }
        public string? postal_code { get; set; }
        public string? city { get; set; }
        public bool is_ambiguous { get; set; }
        public string[]? alternatives { get; set; }
    }
}

/// <summary>
/// Legacy verify result shape used by call handlers.
/// </summary>
public class AddressVerifyResult
{
    public bool Success { get; set; }
    public string VerifiedAddress { get; set; } = "";

    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Street { get; set; } = "";
    public string Number { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";

    public static AddressVerifyResult Ok(
        string verifiedAddress,
        double lat,
        double lon,
        string street,
        string number,
        string postalCode,
        string city) =>
        new()
        {
            Success = true,
            VerifiedAddress = verifiedAddress,
            Lat = lat,
            Lon = lon,
            Street = street,
            Number = number,
            PostalCode = postalCode,
            City = city
        };

    public static AddressVerifyResult Fail(string raw) =>
        new()
        {
            Success = false,
            VerifiedAddress = raw
        };
}
