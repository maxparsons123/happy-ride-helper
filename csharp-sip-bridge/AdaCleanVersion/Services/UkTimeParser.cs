using System.Globalization;
using System.Text.RegularExpressions;

namespace AdaCleanVersion.Services;

/// <summary>
/// Deterministic UK time parser — replaces the LLM-based time normalization
/// previously done by StructureOnlyEngine.
///
/// Handles:
/// - ASAP variants ("now", "straight away", "immediately", "asap", "s.a.p", "in a bit")
/// - Relative offsets ("in 30 minutes", "in an hour", "in 2 hours")
/// - UK colloquialisms ("half seven", "quarter to 5", "half past 7")
/// - Named periods ("tonight", "this evening", "first thing tomorrow", "after work")
/// - Day-of-week ("Monday at 3pm", "this Wednesday", "next Friday at 10")
/// - Explicit times ("tomorrow at 5pm", "5:30pm", "17:00")
/// - No-Past rule: if resolved time is in the past, bump to next valid occurrence
/// </summary>
public static class UkTimeParser
{
    /// <summary>
    /// Parse a raw time string into either "ASAP" or "YYYY-MM-DD HH:MM" format.
    /// Returns null if the input is empty/unparseable.
    /// </summary>
    public static TimeParseResult? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var input = raw.Trim();
        var londonNow = GetLondonNow();

        // ── ASAP detection ──
        if (IsAsap(input))
            return new TimeParseResult { Normalized = "ASAP", IsAsap = true };

        // ── Relative offsets ("in 30 minutes", "in an hour") ──
        var relative = TryParseRelative(input, londonNow);
        if (relative != null)
            return relative;

        // ── UK colloquialisms ("half seven", "quarter to 5") ──
        var colloquial = TryParseColloquial(input, londonNow);
        if (colloquial != null)
            return colloquial;

        // ── Named periods ("tonight", "first thing tomorrow") ──
        var named = TryParseNamedPeriod(input, londonNow);
        if (named != null)
            return named;

        // ── Day-of-week + time ("Monday at 3pm", "this Wednesday") ──
        var dayOfWeek = TryParseDayOfWeek(input, londonNow);
        if (dayOfWeek != null)
            return dayOfWeek;

        // ── "tomorrow at 5pm", "today at 3" ──
        var todayTomorrow = TryParseTodayTomorrow(input, londonNow);
        if (todayTomorrow != null)
            return todayTomorrow;

        // ── Explicit time ("5:30pm", "17:00", "5pm") ──
        var explicit_ = TryParseExplicitTime(input, londonNow);
        if (explicit_ != null)
            return explicit_;

        // ── Fallback: try DateTime.TryParse ──
        if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return MakeResult(EnforceNoPast(parsed, londonNow));

        return null;
    }

    // ─── ASAP Detection ──────────────────────────────────────

    private static readonly HashSet<string> AsapPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "asap", "a.s.a.p", "a.s.a.p.", "s.a.p", "s a p", "sap",
        "now", "right now", "straight away", "right away", "immediately",
        "as soon as possible", "as soon as you can",
        "in a bit", "soon", "whenever", "whenever you can"
    };

    private static bool IsAsap(string input)
    {
        var normalized = Regex.Replace(input.ToLowerInvariant(), @"[.,!?]+$", "").Trim();
        return AsapPhrases.Contains(normalized);
    }

    // ─── Relative Offsets ────────────────────────────────────

    private static TimeParseResult? TryParseRelative(string input, DateTime now)
    {
        // "in 30 minutes", "in an hour", "in 2 hours", "in half an hour"
        var match = Regex.Match(input, @"in\s+(?:(\d+)\s+min(?:ute)?s?|(\d+)\s+hours?|an?\s+hour|half\s+an?\s+hour)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        if (match.Value.Contains("half an hour", StringComparison.OrdinalIgnoreCase)
            || match.Value.Contains("half a hour", StringComparison.OrdinalIgnoreCase))
            return MakeResult(now.AddMinutes(30));

        if (match.Value.Contains("an hour", StringComparison.OrdinalIgnoreCase)
            || match.Value.Contains("a hour", StringComparison.OrdinalIgnoreCase))
            return MakeResult(now.AddHours(1));

        if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var mins))
            return MakeResult(now.AddMinutes(mins));

        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var hrs))
            return MakeResult(now.AddHours(hrs));

        return null;
    }

    // ─── UK Colloquialisms ───────────────────────────────────

    private static TimeParseResult? TryParseColloquial(string input, DateTime now)
    {
        var lower = input.ToLowerInvariant().Trim();

        // "half seven" / "half past seven" / "half past 7"
        var halfMatch = Regex.Match(lower, @"half\s*(?:past\s+)?(\d{1,2}|" + WordNumberPattern + @")",
            RegexOptions.IgnoreCase);
        if (halfMatch.Success)
        {
            var hour = ParseHourValue(halfMatch.Groups[1].Value);
            if (hour > 0) return MakeResult(ResolveAmPm(now, hour, 30));
        }

        // "quarter past 5" / "quarter past five"
        var quarterPastMatch = Regex.Match(lower, @"quarter\s+past\s+(\d{1,2}|" + WordNumberPattern + @")",
            RegexOptions.IgnoreCase);
        if (quarterPastMatch.Success)
        {
            var hour = ParseHourValue(quarterPastMatch.Groups[1].Value);
            if (hour > 0) return MakeResult(ResolveAmPm(now, hour, 15));
        }

        // "quarter to 5" / "quarter to five"
        var quarterToMatch = Regex.Match(lower, @"quarter\s+to\s+(\d{1,2}|" + WordNumberPattern + @")",
            RegexOptions.IgnoreCase);
        if (quarterToMatch.Success)
        {
            var hour = ParseHourValue(quarterToMatch.Groups[1].Value);
            if (hour > 0) return MakeResult(ResolveAmPm(now, hour - 1, 45));
        }

        // "ten past 3", "twenty to 8", "five to 9"
        var minuteToMatch = Regex.Match(lower,
            @"(five|ten|twenty|twenty[\s-]?five)\s+(past|to)\s+(\d{1,2}|" + WordNumberPattern + @")",
            RegexOptions.IgnoreCase);
        if (minuteToMatch.Success)
        {
            var minuteWord = minuteToMatch.Groups[1].Value.ToLowerInvariant();
            var direction = minuteToMatch.Groups[2].Value.ToLowerInvariant();
            var hour = ParseHourValue(minuteToMatch.Groups[3].Value);

            var minuteOffset = minuteWord switch
            {
                "five" => 5,
                "ten" => 10,
                "twenty" => 20,
                _ when minuteWord.Contains("five") => 25,
                _ => 0
            };

            if (hour > 0 && minuteOffset > 0)
            {
                if (direction == "to")
                    return MakeResult(ResolveAmPm(now, hour - 1, 60 - minuteOffset));
                else
                    return MakeResult(ResolveAmPm(now, hour, minuteOffset));
            }
        }

        return null;
    }

    // ─── Named Periods ───────────────────────────────────────

    private static TimeParseResult? TryParseNamedPeriod(string input, DateTime now)
    {
        var lower = input.ToLowerInvariant().Trim();

        // "first thing tomorrow" / "first thing in the morning"
        if (lower.Contains("first thing"))
        {
            var date = lower.Contains("tomorrow") ? now.Date.AddDays(1) : now.Date;
            if (date == now.Date && now.Hour >= 7)
                date = date.AddDays(1);
            return MakeResult(date.AddHours(7));
        }

        // "tonight" / "this evening"
        if (lower is "tonight" or "this evening")
        {
            var target = lower == "tonight"
                ? now.Date.AddHours(20)
                : now.Date.AddHours(19);
            if (now > target) target = target.AddDays(1);
            return MakeResult(target);
        }

        // "this afternoon"
        if (lower is "this afternoon" or "afternoon")
        {
            var target = now.Date.AddHours(15);
            if (now > target) target = target.AddDays(1);
            return MakeResult(target);
        }

        // "this morning" / "morning"
        if (lower is "this morning" or "morning")
        {
            var target = now.Date.AddHours(9);
            if (now > target) target = target.AddDays(1);
            return MakeResult(target);
        }

        // "after work" / "end of day"
        if (lower is "after work" or "end of day")
        {
            var target = now.Date.AddHours(17).AddMinutes(30);
            if (now > target) target = target.AddDays(1);
            return MakeResult(target);
        }

        // "when the pubs close"
        if (lower.Contains("pubs close") || lower.Contains("pub closes"))
        {
            var target = now.Date.AddHours(23).AddMinutes(30);
            if (now > target) target = target.AddDays(1);
            return MakeResult(target);
        }

        return null;
    }

    // ─── Day-of-Week ─────────────────────────────────────────

    private static TimeParseResult? TryParseDayOfWeek(string input, DateTime now)
    {
        // "Monday at 3pm", "this Wednesday", "next Friday at 10am"
        var match = Regex.Match(input,
            @"(?:(?:this|next)\s+)?(monday|tuesday|wednesday|thursday|friday|saturday|sunday)" +
            @"(?:\s+at\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)?",
            RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        var dayName = match.Groups[1].Value;
        var targetDay = Enum.Parse<DayOfWeek>(dayName, true);
        var isNext = input.Contains("next", StringComparison.OrdinalIgnoreCase);

        var daysUntil = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
        if (daysUntil == 0) daysUntil = 7; // same day → next week
        if (isNext && daysUntil <= 7) daysUntil += 7; // "next" → skip this week

        var date = now.Date.AddDays(daysUntil);

        if (match.Groups[2].Success)
        {
            var hour = int.Parse(match.Groups[2].Value);
            var minute = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            var ampm = match.Groups[4].Value.ToLowerInvariant();

            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;
            if (string.IsNullOrEmpty(ampm) && hour is >= 1 and <= 6) hour += 12; // assume PM for taxi

            date = date.AddHours(hour).AddMinutes(minute);
        }
        else
        {
            date = date.AddHours(9); // default 9am if no time specified
        }

        return MakeResult(date);
    }

    // ─── Today/Tomorrow + Time ───────────────────────────────

    private static TimeParseResult? TryParseTodayTomorrow(string input, DateTime now)
    {
        var match = Regex.Match(input,
            @"(today|tomorrow)\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?",
            RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        var date = match.Groups[1].Value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase)
            ? now.Date.AddDays(1)
            : now.Date;

        var hour = int.Parse(match.Groups[2].Value);
        var minute = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        var ampm = match.Groups[4].Value.ToLowerInvariant();

        if (ampm == "pm" && hour < 12) hour += 12;
        if (ampm == "am" && hour == 12) hour = 0;
        if (string.IsNullOrEmpty(ampm) && hour is >= 1 and <= 6) hour += 12; // assume PM for taxi

        var result = date.AddHours(hour).AddMinutes(minute);
        return MakeResult(EnforceNoPast(result, now));
    }

    // ─── Explicit Time ───────────────────────────────────────

    private static TimeParseResult? TryParseExplicitTime(string input, DateTime now)
    {
        // "5pm", "5:30pm", "17:00", "5 pm", "five o'clock"
        var match = Regex.Match(input, @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm|o'?clock)?$",
            RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        var hour = int.Parse(match.Groups[1].Value);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var suffix = match.Groups[3].Value.ToLowerInvariant();

        if (suffix is "pm" && hour < 12) hour += 12;
        if (suffix is "am" && hour == 12) hour = 0;
        if (string.IsNullOrEmpty(suffix) && hour <= 12)
        {
            // Ambiguous — resolve based on current time
            if (now.Hour >= 12 && hour is >= 1 and <= 6) hour += 12;
        }

        var result = now.Date.AddHours(hour).AddMinutes(minute);
        return MakeResult(EnforceNoPast(result, now));
    }

    // ─── Helpers ─────────────────────────────────────────────

    private const string WordNumberPattern =
        @"one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve";

    private static int ParseHourValue(string value)
    {
        if (int.TryParse(value, out var num)) return num;
        return value.ToLowerInvariant() switch
        {
            "one" => 1, "two" => 2, "three" => 3, "four" => 4,
            "five" => 5, "six" => 6, "seven" => 7, "eight" => 8,
            "nine" => 9, "ten" => 10, "eleven" => 11, "twelve" => 12,
            _ => 0
        };
    }

    /// <summary>
    /// Resolve AM/PM ambiguity for colloquial times like "half seven".
    /// If current time is before noon, assume AM; after noon, assume PM.
    /// If the resolved time is in the past, bump to next occurrence.
    /// </summary>
    private static DateTime ResolveAmPm(DateTime now, int hour, int minute)
    {
        // Normalize hour to 1-12 range
        if (hour <= 0) hour += 12;
        if (hour > 12) hour -= 12;

        var amCandidate = now.Date.AddHours(hour).AddMinutes(minute);
        var pmCandidate = now.Date.AddHours(hour + 12).AddMinutes(minute);

        // If before noon, prefer AM; after noon, prefer PM
        var preferred = now.Hour < 12 ? amCandidate : pmCandidate;

        // No-Past rule
        if (preferred <= now)
        {
            // Try the other candidate
            var other = preferred == amCandidate ? pmCandidate : amCandidate;
            if (other > now) return other;
            // Both passed → tomorrow AM
            return amCandidate.AddDays(1);
        }

        return preferred;
    }

    private static DateTime EnforceNoPast(DateTime target, DateTime now)
    {
        if (target > now) return target;
        // Bump by 1 day
        return target.AddDays(1);
    }

    private static DateTime GetLondonNow()
    {
        var londonTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, londonTz);
    }

    private static TimeParseResult MakeResult(DateTime dt) => new()
    {
        Normalized = dt.ToString("yyyy-MM-dd HH:mm"),
        IsAsap = false,
        Resolved = dt
    };
}

/// <summary>
/// Result of deterministic time parsing.
/// </summary>
public class TimeParseResult
{
    public required string Normalized { get; init; }
    public bool IsAsap { get; init; }
    public DateTime? Resolved { get; init; }
}