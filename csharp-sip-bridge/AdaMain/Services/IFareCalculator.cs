namespace AdaMain.Services;

/// <summary>
/// Fare calculation result with geocoded addresses.
/// </summary>
public sealed class FareResult
{
    public string Fare { get; set; } = "";
    public string Eta { get; set; } = "";
    
    public double? PickupLat { get; set; }
    public double? PickupLon { get; set; }
    public string? PickupStreet { get; set; }
    public string? PickupNumber { get; set; }
    public string? PickupPostalCode { get; set; }
    public string? PickupCity { get; set; }
    public string? PickupFormatted { get; set; }
    
    public double? DestLat { get; set; }
    public double? DestLon { get; set; }
    public string? DestStreet { get; set; }
    public string? DestNumber { get; set; }
    public string? DestPostalCode { get; set; }
    public string? DestCity { get; set; }
    public string? DestFormatted { get; set; }
}

/// <summary>
/// Interface for fare calculation service.
/// </summary>
public interface IFareCalculator
{
    /// <summary>
    /// Calculate fare with geocoding.
    /// </summary>
    Task<FareResult> CalculateAsync(string? pickup, string? destination, string? phoneNumber);
}
