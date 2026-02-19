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

    /// <summary>Parsed scheduled pickup DateTime (UTC). Null = ASAP.</summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>Fare as decimal for iCabbi payment payload.</summary>
    public decimal FareDecimal => decimal.TryParse(Fare?.Replace("£", "").Trim(), out var v) ? v : 0m;

    /// <summary>
    /// Parses natural-language pickup time into a UTC DateTime.
    /// Aligned with WinForms TaxiAIEngine time normalisation rules.
    /// Handles: "now", "ASAP", "in X minutes", "at 3pm", "tonight", "this evening",
    /// "tomorrow at 9am", "next Wednesday", "this time tomorrow", day-of-week, etc.
    /// Always returns future datetimes (no-past rule).
    /// </summary>
    public static DateTime? ParsePickupTimeToDateTime(string? pickupTime)
    {
        if (string.IsNullOrWhiteSpace(pickupTime)) return null;
        var lower = pickupTime.Trim().ToLowerInvariant();

        // Immediate
        if (lower is "now" or "asap" or "as soon as possible" or "immediately" or "straight away" or "right now")
            return null;

        // Normalize "p.m." → "pm", "a.m." → "am" and strip extra dots/spaces
        lower = Regex.Replace(lower, @"p\s*\.\s*m\s*\.?", "pm");
        lower = Regex.Replace(lower, @"a\s*\.\s*m\s*\.?", "am");

        // Try ISO format first (from AI tool calls: "2026-02-19 15:30")
        if (DateTime.TryParseExact(lower, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var iso))
        {
            return EnsureFuture(DateTime.SpecifyKind(iso, DateTimeKind.Utc));
        }

        var now = DateTime.UtcNow;

        // "this time tomorrow" / "same time next week"
        if (lower.Contains("this time tomorrow")) return now.AddDays(1);
        if (lower.Contains("same time next week") || lower.Contains("next week at this time")) return now.AddDays(7);

        // "in X minutes/hours/days"
        var inMatch = Regex.Match(lower, @"in\s+(\d+)\s*(min|hour|hr|day)");
        if (inMatch.Success)
        {
            var amount = int.Parse(inMatch.Groups[1].Value);
            var unit = inMatch.Groups[2].Value;
            if (unit.StartsWith("day")) return now.AddDays(amount);
            if (unit.StartsWith("hour") || unit.StartsWith("hr")) return now.AddHours(amount);
            return now.AddMinutes(amount);
        }

        // Natural time phrases (resolve to specific hours)
        int? naturalHour = null;
        if (lower.Contains("tonight")) naturalHour = 21;
        else if (lower.Contains("this evening") || lower.Contains("evening")) naturalHour = 19;
        else if (lower.Contains("afternoon")) naturalHour = 15;
        else if (lower.Contains("morning")) naturalHour = 9;

        // Day-of-week detection
        var dayNames = new[] { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday" };
        DayOfWeek? targetDay = null;
        bool isNextWeek = lower.Contains("next ");

        foreach (var dn in dayNames)
        {
            if (lower.Contains(dn))
            {
                targetDay = Enum.Parse<DayOfWeek>(char.ToUpper(dn[0]) + dn[1..]);
                break;
            }
        }

        // Determine the base date
        DateTime baseDate = now.Date;
        bool hasTomorrow = lower.Contains("tomorrow");

        if (hasTomorrow)
        {
            baseDate = now.Date.AddDays(1);
        }
        else if (targetDay.HasValue)
        {
            int daysAhead = ((int)targetDay.Value - (int)now.DayOfWeek + 7) % 7;
            if (daysAhead == 0) daysAhead = 7; // always next occurrence
            if (isNextWeek)
            {
                daysAhead = ((int)targetDay.Value - (int)now.DayOfWeek + 7) % 7 + 7;
            }
            baseDate = now.Date.AddDays(daysAhead);
        }

        // Extract explicit time — handles "5:30pm", "5.30pm", "5 30 pm", "15:30", "5pm", "5 o'clock"
        var timeMatch = Regex.Match(lower, @"(\d{1,2})\s*[:.\s]\s*(\d{2})\s*(am|pm)?|(\d{1,2})\s*(am|pm)|(\d{1,2})\s*o'?\s*clock");
        int hour = -1, min = 0;
        bool hasAmPm = false;

        if (timeMatch.Success)
        {
            if (timeMatch.Groups[6].Success) // "X o'clock"
            {
                hour = int.Parse(timeMatch.Groups[6].Value);
            }
            else if (timeMatch.Groups[4].Success) // "5pm"
            {
                hour = int.Parse(timeMatch.Groups[4].Value);
                var ap = timeMatch.Groups[5].Value;
                hasAmPm = true;
                if (ap == "pm" && hour < 12) hour += 12;
                if (ap == "am" && hour == 12) hour = 0;
            }
            else if (timeMatch.Groups[1].Success) // "15:30" or "3:30pm" or "5 30 pm"
            {
                hour = int.Parse(timeMatch.Groups[1].Value);
                min = timeMatch.Groups[2].Success ? int.Parse(timeMatch.Groups[2].Value) : 0;
                var ap = timeMatch.Groups[3].Success ? timeMatch.Groups[3].Value : "";
                hasAmPm = !string.IsNullOrEmpty(ap);
                if (ap == "pm" && hour < 12) hour += 12;
                if (ap == "am" && hour == 12) hour = 0;
            }

            // TAXI HEURISTIC: If no am/pm specified and hour is 1-9, assume PM
            // (nobody books a taxi for 5:30 AM without saying "am" explicitly)
            if (!hasAmPm && hour >= 1 && hour <= 9)
            {
                hour += 12;
            }
        }
        else if (naturalHour.HasValue)
        {
            hour = naturalHour.Value;
        }

        // If we resolved a time or day
        if (hour >= 0)
        {
            var result = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, hour, min, 0, DateTimeKind.Utc);
            return EnsureFuture(result);
        }

        // If we only got a day-of-week or "tomorrow" with no time, use same time of day as now
        if (hasTomorrow || targetDay.HasValue)
        {
            var result = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            return EnsureFuture(result);
        }

        // Last resort: try matching just a bare time "3pm", "15:30" without any context words
        var bareTime = Regex.Match(lower, @"^(\d{1,2})\s*[:.\s]?\s*(\d{2})?\s*(am|pm)?$");
        if (bareTime.Success)
        {
            hour = int.Parse(bareTime.Groups[1].Value);
            min = bareTime.Groups[2].Success ? int.Parse(bareTime.Groups[2].Value) : 0;
            var ap = bareTime.Groups[3].Value;
            hasAmPm = !string.IsNullOrEmpty(ap);
            if (ap == "pm" && hour < 12) hour += 12;
            if (ap == "am" && hour == 12) hour = 0;
            if (!hasAmPm && hour >= 1 && hour <= 9) hour += 12;
            var result = new DateTime(now.Year, now.Month, now.Day, hour, min, 0, DateTimeKind.Utc);
            return EnsureFuture(result);
        }

        return null; // Could not parse — treat as ASAP
    }

    /// <summary>No-past rule: if computed datetime is in the past, roll forward to next valid occurrence.</summary>
    private static DateTime EnsureFuture(DateTime dt)
    {
        if (dt <= DateTime.UtcNow) return dt.AddDays(1);
        return dt;
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
