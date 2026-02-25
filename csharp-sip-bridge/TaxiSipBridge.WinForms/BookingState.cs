namespace TaxiSipBridge;

/// <summary>
/// Centralized booking state shared across all AI clients.
/// Contains raw booking data plus geocoded address components for dispatch.
/// </summary>
public sealed class BookingState
{
    // Core booking fields
    public string? Name { get; set; }
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public bool Confirmed { get; set; }
    public string? BookingRef { get; set; }
    public string VehicleType { get; set; } = "Saloon";
    public string? PaymentPreference { get; set; }
    public string? Luggage { get; set; }
    public string? SpecialInstructions { get; set; }

    /// <summary>Resolved area/district within the city for pickup (e.g. "Foleshill", "Earlsdon").</summary>
    public string? PickupArea { get; set; }

    /// <summary>Resolved area/district within the city for destination.</summary>
    public string? DestArea { get; set; }
    public int BiddingWindowSec { get; set; } = 45;

    /// <summary>
    /// Recommends vehicle type based on passenger count and luggage. Can be overridden by explicit request.
    /// </summary>
    public static string RecommendVehicle(int passengers, string? luggage = null)
    {
        var baseType = passengers switch
        {
            <= 4 => "Saloon",
            5 or 6 => "Estate",
            >= 7 => "Minibus",
        };

        // Heavy luggage triggers a tier upgrade
        if (!string.IsNullOrWhiteSpace(luggage) &&
            luggage.Contains("heavy", StringComparison.OrdinalIgnoreCase))
        {
            return baseType switch
            {
                "Saloon" => "Estate",
                "Estate" => "Minibus",
                _ => baseType,
            };
        }

        return baseType;
    }

    // Geocoded coordinates for dispatch
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    
    // Geocoded address components for accurate dispatch
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }
    
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }

    public void Reset()
    {
        Name = Pickup = Destination = PickupTime = Fare = Eta = BookingRef = null;
        Passengers = null;
        VehicleType = "Saloon";
        Luggage = SpecialInstructions = null;
        PickupArea = DestArea = null;
        PickupLat = PickupLon = DestLat = DestLon = null;
        PickupStreet = PickupNumber = PickupPostalCode = PickupCity = PickupFormatted = null;
        DestStreet = DestNumber = DestPostalCode = DestCity = DestFormatted = null;
        Confirmed = false;
    }
}
