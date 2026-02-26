using System.Text.RegularExpressions;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Normalizes spoken time expressions into ISO 8601 datetime strings or "ASAP".
/// Uses Europe/London timezone for all calculations.
/// </summary>
public static class TimeNormalizer
{
    private static readonly TimeZoneInfo LondonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static readonly HashSet<string> AsapPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "now", "asap", "straight away", "right away", "right now",
        "immediately", "as soon as possible", "quick as you can",
        "quick", "soon", "soon as possible", "soon as you can",
        "straight away please", "right now please", "as quick as possible"
    };

    /// <summary>
    /// Normalize a raw spoken time string into ISO format (yyyy-MM-dd HH:mm) or "ASAP".
    /// Returns null if the input cannot be parsed.
    /// </summary>
    public static string? Normalize(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput)) return null;

        var cleaned = rawInput.Trim().ToLowerInvariant()
            .TrimEnd('.', '!', '?', ',');

        // Check for ASAP variants
        if (AsapPhrases.Contains(cleaned) || cleaned.Contains("now") || cleaned.Contains("asap") ||
            cleaned.Contains("straight away") || cleaned.Contains("right away") ||
            cleaned.Contains("immediately"))
            return "ASAP";

        var londonNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, LondonTz);

        // Try "in X minutes/hours"
        var offsetResult = TryParseOffset(cleaned, londonNow);
        if (offsetResult != null) return offsetResult;

        // Try "tomorrow at 5pm", "today at 3", etc.
        var dayTimeResult = TryParseDayAndTime(cleaned, londonNow);
        if (dayTimeResult != null) return dayTimeResult;

        // Try standalone time: "5pm", "17:30", "half past 3", "quarter to 5"
        var timeOnly = TryParseTimeExpression(cleaned);
        if (timeOnly != null)
        {
            var targetDate = londonNow.Date;
            var candidate = targetDate.Add(timeOnly.Value);
            // If the time has already passed today, assume tomorrow
            if (candidate <= londonNow)
                candidate = candidate.AddDays(1);
            return FormatIso(candidate);
        }

        // Try day of week: "Monday at 5pm", "next Friday 3pm"
        var dayOfWeekResult = TryParseDayOfWeek(cleaned, londonNow);
        if (dayOfWeekResult != null) return dayOfWeekResult;

        // Fallback: if it looks like an ISO date already, pass through
        if (DateTime.TryParse(rawInput, out var parsed))
            return FormatIso(parsed);

        return null; // Could not normalize
    }

    /// <summary>
    /// Parse "in X minutes", "in an hour", "in 2 hours", "in half an hour"
    /// </summary>
    private static string? TryParseOffset(string input, DateTime now)
    {
        // "in half an hour" / "half an hour"
        if (input.Contains("half an hour") || input.Contains("half hour"))
            return FormatIso(now.AddMinutes(30));

        // "in an hour" / "an hour"
        if (Regex.IsMatch(input, @"\b(in\s+)?an?\s+hour\b"))
            return FormatIso(now.AddHours(1));

        // "in X minutes"
        var minMatch = Regex.Match(input, @"(?:in\s+)?(\d+)\s*(?:min(?:ute)?s?)");
        if (minMatch.Success && int.TryParse(minMatch.Groups[1].Value, out var mins))
            return FormatIso(now.AddMinutes(mins));

        // "in X hours"
        var hourMatch = Regex.Match(input, @"(?:in\s+)?(\d+)\s*(?:hours?)");
        if (hourMatch.Success && int.TryParse(hourMatch.Groups[1].Value, out var hrs))
            return FormatIso(now.AddHours(hrs));

        // "in X and a half hours" / "in 1.5 hours"
        var halfHourMatch = Regex.Match(input, @"(?:in\s+)?(\d+)\s*(?:and\s+a\s+half\s+hours?|\.5\s*hours?)");
        if (halfHourMatch.Success && int.TryParse(halfHourMatch.Groups[1].Value, out var baseHrs))
            return FormatIso(now.AddMinutes(baseHrs * 60 + 30));

        return null;
    }

    /// <summary>
    /// Parse "tomorrow at 5pm", "today at 3:30", "tonight at 8"
    /// </summary>
    private static string? TryParseDayAndTime(string input, DateTime now)
    {
        DateTime? baseDate = null;

        if (input.Contains("tomorrow"))
            baseDate = now.Date.AddDays(1);
        else if (input.Contains("today") || input.Contains("tonight") || input.Contains("this evening") || input.Contains("this afternoon"))
            baseDate = now.Date;
        else if (input.Contains("day after tomorrow"))
            baseDate = now.Date.AddDays(2);

        if (baseDate == null) return null;

        // Extract time portion
        var timeMatch = ExtractTimeFromText(input);
        if (timeMatch != null)
            return FormatIso(baseDate.Value.Add(timeMatch.Value));

        // "tonight" without specific time → default 20:00
        if (input.Contains("tonight") || input.Contains("this evening"))
            return FormatIso(baseDate.Value.AddHours(20));

        // "this afternoon" without specific time → default 14:00
        if (input.Contains("this afternoon"))
            return FormatIso(baseDate.Value.AddHours(14));

        // "tomorrow morning" → 09:00
        if (input.Contains("morning"))
            return FormatIso(baseDate.Value.AddHours(9));

        return null;
    }

    /// <summary>
    /// Parse day-of-week references: "Monday at 5pm", "next Friday 3pm"
    /// </summary>
    private static string? TryParseDayOfWeek(string input, DateTime now)
    {
        var days = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            { "monday", DayOfWeek.Monday }, { "tuesday", DayOfWeek.Tuesday },
            { "wednesday", DayOfWeek.Wednesday }, { "thursday", DayOfWeek.Thursday },
            { "friday", DayOfWeek.Friday }, { "saturday", DayOfWeek.Saturday },
            { "sunday", DayOfWeek.Sunday }
        };

        foreach (var (name, dow) in days)
        {
            if (!input.Contains(name)) continue;

            // Calculate next occurrence of this day
            var daysUntil = ((int)dow - (int)now.DayOfWeek + 7) % 7;
            if (daysUntil == 0) daysUntil = 7; // "Monday" on a Monday = next Monday
            var targetDate = now.Date.AddDays(daysUntil);

            var time = ExtractTimeFromText(input);
            if (time != null)
                return FormatIso(targetDate.Add(time.Value));

            // Default to 09:00 if no time specified
            return FormatIso(targetDate.AddHours(9));
        }

        return null;
    }

    /// <summary>
    /// Try to parse a standalone time expression: "5pm", "17:30", "half past 3", "quarter to 5"
    /// </summary>
    private static TimeSpan? TryParseTimeExpression(string input)
    {
        return ExtractTimeFromText(input);
    }

    /// <summary>
    /// Extract a time-of-day from text. Handles:
    /// - "5pm", "5 pm", "5:30pm", "17:30"
    /// - "half past 3", "quarter past 5", "quarter to 8"
    /// - "3 o'clock", "3 oclock"
    /// </summary>
    private static TimeSpan? ExtractTimeFromText(string input)
    {
        // "half past X"
        var halfPast = Regex.Match(input, @"half\s+past\s+(\d{1,2})");
        if (halfPast.Success && int.TryParse(halfPast.Groups[1].Value, out var hp))
            return new TimeSpan(NormalizeHour(hp, input), 30, 0);

        // "quarter past X"
        var qPast = Regex.Match(input, @"quarter\s+past\s+(\d{1,2})");
        if (qPast.Success && int.TryParse(qPast.Groups[1].Value, out var qp))
            return new TimeSpan(NormalizeHour(qp, input), 15, 0);

        // "quarter to X"
        var qTo = Regex.Match(input, @"quarter\s+to\s+(\d{1,2})");
        if (qTo.Success && int.TryParse(qTo.Groups[1].Value, out var qt))
            return new TimeSpan(NormalizeHour(qt, input) - 1, 45, 0);

        // "X:XX am/pm" or "X:XX"
        var colonTime = Regex.Match(input, @"(\d{1,2}):(\d{2})\s*(am|pm)?");
        if (colonTime.Success)
        {
            var h = int.Parse(colonTime.Groups[1].Value);
            var m = int.Parse(colonTime.Groups[2].Value);
            var ampm = colonTime.Groups[3].Value;
            if (ampm == "pm" && h < 12) h += 12;
            if (ampm == "am" && h == 12) h = 0;
            return new TimeSpan(h, m, 0);
        }

        // "Xam/Xpm" or "X am/X pm"
        var ampmTime = Regex.Match(input, @"(\d{1,2})\s*(am|pm)");
        if (ampmTime.Success)
        {
            var h = int.Parse(ampmTime.Groups[1].Value);
            var ampm = ampmTime.Groups[2].Value;
            if (ampm == "pm" && h < 12) h += 12;
            if (ampm == "am" && h == 12) h = 0;
            return new TimeSpan(h, 0, 0);
        }

        // "X o'clock" / "X oclock"
        var oclock = Regex.Match(input, @"(\d{1,2})\s*o'?clock");
        if (oclock.Success && int.TryParse(oclock.Groups[1].Value, out var oc))
            return new TimeSpan(NormalizeHour(oc, input), 0, 0);

        // Bare 24h time "17:30" already caught above, bare "17" standalone
        var bare24 = Regex.Match(input, @"\b(1[3-9]|2[0-3])\b");
        if (bare24.Success)
            return new TimeSpan(int.Parse(bare24.Value), 0, 0);

        return null;
    }

    /// <summary>
    /// Normalize an ambiguous hour (1-12) to 24h based on context words.
    /// Default: assume PM for taxi bookings unless "am" or "morning" is present.
    /// </summary>
    private static int NormalizeHour(int hour, string context)
    {
        if (hour > 12) return hour; // Already 24h

        var isMorning = context.Contains("am") || context.Contains("morning");
        if (isMorning)
        {
            return hour == 12 ? 0 : hour;
        }

        // Default to PM for taxi bookings (most common use case)
        // Unless it's clearly early morning (midnight-6am edge case)
        return hour < 12 ? hour + 12 : hour;
    }

    private static string FormatIso(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm");
}
