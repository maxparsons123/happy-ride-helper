namespace AdaSdkModel.Core;

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
    public string? SpecialInstructions { get; set; }

    /// <summary>Parsed scheduled pickup DateTime (UTC). Null = ASAP.</summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>Fare as decimal for iCabbi payment payload.</summary>
    public decimal FareDecimal => decimal.TryParse(Fare?.Replace("£", "").Trim(), out var v) ? v : 0m;

    /// <summary>
    /// Parses natural-language pickup time into a UTC DateTime.
    /// Handles: "now", "ASAP", "in 30 minutes", "at 3pm", "at 15:30", "tomorrow at 9am", etc.
    /// </summary>
    public static DateTime? ParsePickupTimeToDateTime(string? pickupTime)
    {
        if (string.IsNullOrWhiteSpace(pickupTime)) return null;
        var lower = pickupTime.Trim().ToLowerInvariant();

        // Immediate
        if (lower is "now" or "asap" or "as soon as possible" or "immediately" or "straight away" or "right now")
            return null;

        var now = DateTime.UtcNow;

        // "in X minutes/hours"
        var inMatch = System.Text.RegularExpressions.Regex.Match(lower, @"in\s+(\d+)\s*(min|hour|hr)");
        if (inMatch.Success)
        {
            var amount = int.Parse(inMatch.Groups[1].Value);
            return inMatch.Groups[2].Value.StartsWith("hour") || inMatch.Groups[2].Value.StartsWith("hr")
                ? now.AddHours(amount) : now.AddMinutes(amount);
        }

        // "at HH:MM" or "at Hpm/am"
        var atMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(?:at\s+)?(\d{1,2})[:.]?(\d{2})?\s*(am|pm)?");
        if (atMatch.Success)
        {
            var hour = int.Parse(atMatch.Groups[1].Value);
            var min = atMatch.Groups[2].Success ? int.Parse(atMatch.Groups[2].Value) : 0;
            var ampm = atMatch.Groups[3].Value;

            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;

            var scheduled = new DateTime(now.Year, now.Month, now.Day, hour, min, 0, DateTimeKind.Utc);

            // If the caller said "tomorrow"
            if (lower.Contains("tomorrow")) scheduled = scheduled.AddDays(1);
            // If time already passed today, assume tomorrow
            else if (scheduled <= now) scheduled = scheduled.AddDays(1);

            return scheduled;
        }

        return null; // Could not parse — treat as ASAP
    }

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
        ScheduledAt = null;
        VehicleType = "Saloon";
        PickupLat = PickupLon = DestLat = DestLon = null;
        PickupStreet = PickupNumber = PickupPostalCode = PickupCity = PickupFormatted = null;
        DestStreet = DestNumber = DestPostalCode = DestCity = DestFormatted = null;
        Confirmed = false;
    }

    public BookingState Clone() => (BookingState)MemberwiseClone();
}
