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

    // Geocoded coordinates for dispatch
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    
    // Geocoded address components for accurate dispatch
    public string? PickupStreet { get; set; }
    public int? PickupNumber { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }
    
    public string? DestStreet { get; set; }
    public int? DestNumber { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }

    public void Reset()
    {
        Name = Pickup = Destination = PickupTime = Fare = Eta = BookingRef = null;
        Passengers = null;
        PickupLat = PickupLon = DestLat = DestLon = null;
        PickupStreet = PickupPostalCode = PickupCity = PickupFormatted = null;
        DestStreet = DestPostalCode = DestCity = DestFormatted = null;
        PickupNumber = DestNumber = null;
        Confirmed = false;
    }
}
