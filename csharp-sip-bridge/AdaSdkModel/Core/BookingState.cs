// Last updated: 2026-02-21 (v2.8)
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

    /// <summary>Luggage info: "none", "small" (hand luggage), "medium" (1-2 suitcases), "heavy" (3+ suitcases or bulky items).</summary>
    public string? Luggage { get; set; }

    /// <summary>Payment preference chosen by caller: "card" (fixed price via SumUp) or "meter" (pay on the day).</summary>
    public string? PaymentPreference { get; set; }

    /// <summary>SumUp checkout URL generated for card-paying callers. Set before dispatch so it can be included in the BSQD payload.</summary>
    public string? PaymentLink { get; set; }

    /// <summary>UUID of an existing active booking loaded from the database for returning callers. Null = new booking.</summary>
    public string? ExistingBookingId { get; set; }

    /// <summary>iCabbi journey ID returned by CreateAndDispatchAsync. Used for cancellation and status queries.</summary>
    public string? IcabbiJourneyId { get; set; }

    /// <summary>Previous pickup interpretations for safeguarding (most recent first).</summary>
    public List<string> PreviousPickups { get; set; } = new();

    /// <summary>Previous destination interpretations for safeguarding (most recent first).</summary>
    public List<string> PreviousDestinations { get; set; } = new();

    /// <summary>Parsed scheduled pickup DateTime (UTC). Null = ASAP.</summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>Fare as decimal for iCabbi payment payload.</summary>
    public decimal FareDecimal => decimal.TryParse(Fare?.Replace("£", "").Trim(), out var v) ? v : 0m;

    /// <summary>
    /// Parses pickup time from AI tool output into UTC DateTime.
    /// The AI is instructed to output ISO format (YYYY-MM-DD HH:MM) in London local time
    /// or "ASAP"/"now". This method trusts the AI's output and only handles ISO conversion.
    /// </summary>
    public static DateTime? ParsePickupTimeToDateTime(string? pickupTime)
    {
        if (string.IsNullOrWhiteSpace(pickupTime)) return null;
        var lower = pickupTime.Trim().ToLowerInvariant();

        // Immediate
        if (lower is "now" or "asap" or "as soon as possible" or "immediately" or "straight away" or "right now")
            return null;

        // ISO format from AI (London local time) — primary path
        if (DateTime.TryParseExact(pickupTime.Trim(),
            new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var iso))
        {
            var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(iso, DateTimeKind.Unspecified), londonTz);
            return EnsureFuture(utc);
        }

        // Fallback: "in X minutes/hours" (simple relative)
        var inMatch = Regex.Match(lower, @"in\s+(\d+)\s*(min|hour|hr)");
        if (inMatch.Success)
        {
            var amount = int.Parse(inMatch.Groups[1].Value);
            var unit = inMatch.Groups[2].Value;
            if (unit.StartsWith("hour") || unit.StartsWith("hr")) return DateTime.UtcNow.AddHours(amount);
            return DateTime.UtcNow.AddMinutes(amount);
        }

        // Could not parse — treat as ASAP
        return null;
    }

    /// <summary>No-past rule: if computed datetime is in the past, roll forward.</summary>
    private static DateTime EnsureFuture(DateTime dt)
    {
        if (dt <= DateTime.UtcNow) return dt.AddDays(1);
        return dt;
    }


    /// <summary>
    /// Recommends vehicle type based on passenger count and luggage.
    /// Heavy luggage upgrades: Saloon→Estate, Estate→MPV.
    /// </summary>
    public static string RecommendVehicle(int passengers, string? luggage = null)
    {
        var baseType = passengers switch
        {
            <= 4 => "Saloon",
            5 or 6 => "Estate",
            7 => "MPV",
            >= 8 => "Minibus",
        };

        // Heavy luggage upgrades the vehicle one tier
        if (string.Equals(luggage, "heavy", StringComparison.OrdinalIgnoreCase))
        {
            return baseType switch
            {
                "Saloon" => "Estate",
                "Estate" => "MPV",
                _ => baseType // MPV and Minibus stay
            };
        }

        return baseType;
    }

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
        ExistingBookingId = null;
        IcabbiJourneyId = null;
        Passengers = null;
        ScheduledAt = null;
        VehicleType = "Saloon";
        Luggage = null;
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
