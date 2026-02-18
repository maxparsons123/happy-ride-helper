namespace WhatsAppTaxiBooker.Models;

public sealed class Booking
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12].ToUpper();
    public string Phone { get; set; } = "";
    public string? CallerName { get; set; }
    public string Pickup { get; set; } = "";
    public string Destination { get; set; } = "";
    public int Passengers { get; set; } = 1;
    public string? Notes { get; set; }
    public double? PickupLat { get; set; }
    public double? PickupLng { get; set; }
    public double? DropoffLat { get; set; }
    public double? DropoffLng { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Fare { get; set; }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    });
}

/// <summary>
/// Gemini extraction result from free-form text/voice.
/// </summary>
public sealed class GeminiBookingExtraction
{
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? CallerName { get; set; }
    public string? Notes { get; set; }
    public string? PickupTime { get; set; }
    public bool IsComplete { get; set; }
    public string? MissingFields { get; set; }
}
