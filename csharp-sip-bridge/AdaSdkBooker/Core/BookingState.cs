using System.Text.RegularExpressions;

namespace AdaSdkModel.Core;

/// <summary>
/// Centralized booking state for a call session.
/// Contains raw booking data plus geocoded address components.
/// </summary>
public sealed class BookingState
{
    // Core booking fields
    public string? Name { get; set; }
    public string? CallerName { get; set; }
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

    /// <summary>Payment preference chosen by caller: "card" (fixed price via SumUp) or "meter" (pay on the day).</summary>
    public string? PaymentPreference { get; set; }

    /// <summary>SumUp checkout URL generated for card-paying callers.</summary>
    public string? PaymentLink { get; set; }

    /// <summary>UUID of an existing active booking loaded from the database for returning callers. Null = new booking.</summary>
    public string? ExistingBookingId { get; set; }

    /// <summary>iCabbi journey ID returned by CreateAndDispatchAsync.</summary>
    public string? IcabbiJourneyId { get; set; }

    /// <summary>Previous pickup interpretations for safeguarding (most recent first).</summary>
    public List<string> PreviousPickups { get; set; } = new();

    /// <summary>Previous destination interpretations for safeguarding (most recent first).</summary>
    public List<string> PreviousDestinations { get; set; } = new();

    /// <summary>Parsed scheduled pickup DateTime (UTC). Null = ASAP.</summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>Fare as decimal for iCabbi payment payload.</summary>
    public decimal FareDecimal => decimal.TryParse(Fare?.Replace("£", "").Trim(), out var v) ? v : 0m;

    public static DateTime? ParsePickupTimeToDateTime(string? pickupTime)
    {
        if (string.IsNullOrWhiteSpace(pickupTime)) return null;
        var lower = pickupTime.Trim().ToLowerInvariant();

        if (lower is "now" or "asap" or "as soon as possible" or "immediately" or "straight away" or "right now")
            return null;

        if (DateTime.TryParseExact(pickupTime.Trim(),
            new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var iso))
        {
            var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(iso, DateTimeKind.Unspecified), londonTz);
            return EnsureFuture(utc);
        }

        var inMatch = Regex.Match(lower, @"in\s+(\d+)\s*(min|hour|hr)");
        if (inMatch.Success)
        {
            var amount = int.Parse(inMatch.Groups[1].Value);
            var unit = inMatch.Groups[2].Value;
            if (unit.StartsWith("hour") || unit.StartsWith("hr")) return DateTime.UtcNow.AddHours(amount);
            return DateTime.UtcNow.AddMinutes(amount);
        }

        return null;
    }

    private static DateTime EnsureFuture(DateTime dt)
    {
        if (dt <= DateTime.UtcNow) return dt.AddDays(1);
        return dt;
    }

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
        Name = CallerName = CallerPhone = Pickup = Destination = PickupTime = Fare = Eta = BookingRef = null;
        ExistingBookingId = null;
        IcabbiJourneyId = null;
        Passengers = null;
        ScheduledAt = null;
        VehicleType = "Saloon";
        PaymentPreference = null;
        PaymentLink = null;
        SpecialInstructions = null;
        PickupLat = PickupLon = DestLat = DestLon = null;
        PickupStreet = PickupNumber = PickupPostalCode = PickupCity = PickupFormatted = null;
        DestStreet = DestNumber = DestPostalCode = DestCity = DestFormatted = null;
        PreviousPickups.Clear();
        PreviousDestinations.Clear();
        Confirmed = false;
    }

    public BookingState Clone() => (BookingState)MemberwiseClone();
}
