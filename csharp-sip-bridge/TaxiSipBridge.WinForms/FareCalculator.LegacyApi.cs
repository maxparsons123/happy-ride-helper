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
    /// </summary>
    public static async Task<FareResult> CalculateFareWithCoordsAsync(
        string? pickup,
        string? destination,
        string? phoneNumber = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(destination))
                return new FareResult { Fare = "€12.50", Eta = "6 minutes" };

            var calc = GetDefaultCalculator();
            return await calc.CalculateAsync(pickup, destination, phoneNumber).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"⚠️ CalculateFareWithCoordsAsync error: {ex.Message}");
            return new FareResult { Fare = "€12.50", Eta = "6 minutes" };
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
                    is_ambiguous = extraction.PickupAmbiguous,
                    alternatives = extraction.PickupAlternatives
                },
                dropoff = new EdgeAddress
                {
                    address = extraction.DestinationAddress,
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
