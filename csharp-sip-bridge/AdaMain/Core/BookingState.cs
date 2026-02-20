namespace AdaMain.Core;

/// <summary>
/// Centralized booking state for a call session.
/// Contains raw booking data plus geocoded address components.
/// </summary>
public sealed class BookingState
{
    // Core booking fields
    public string? Name { get; set; }
    public string? CallerPhone { get; set; }
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
    public int BiddingWindowSec { get; set; } = 45;

    public static string RecommendVehicle(int passengers) => passengers switch
    {
        <= 4 => "Saloon",
        5 or 6 => "Estate",
        >= 7 => "Minibus",
    };

    // Geocoded coordinates
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }

    // Geocoded pickup address components
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }

    // Geocoded destination address components
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }

    public void Reset()
    {
        Name = CallerPhone = Pickup = Destination = PickupTime = Fare = Eta = BookingRef = null;
        Passengers = null;
        VehicleType = "Saloon";
        PaymentPreference = null;
        BiddingWindowSec = 45;
        PickupLat = PickupLon = DestLat = DestLon = null;
        PickupStreet = PickupNumber = PickupPostalCode = PickupCity = PickupFormatted = null;
        DestStreet = DestNumber = DestPostalCode = DestCity = DestFormatted = null;
        Confirmed = false;
    }

    public BookingState Clone() => (BookingState)MemberwiseClone();
}
