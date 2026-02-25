using System.Text.RegularExpressions;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Detects correction intent in caller transcripts using keyword/pattern matching.
/// No AI needed — deterministic parsing of phrases like "actually change my pickup",
/// "no wait, the destination is...", "my name is actually..." etc.
/// </summary>
public static class CorrectionDetector
{
    /// <summary>
    /// Result of correction detection — which slot to correct and the new value.
    /// </summary>
    public record CorrectionMatch(string SlotName, string NewValue);

    // Correction trigger phrases — caller is signaling they want to change something
    private static readonly string[] CorrectionPrefixes =
    [
        "actually", "no wait", "no no", "sorry", "hang on", "hold on",
        "i meant", "i mean", "not that", "wrong", "let me change",
        "can you change", "change my", "change the", "update my", "update the",
        "correct the", "correct my", "instead of", "it's not", "that's not right",
        "that's wrong", "no it's", "no its"
    ];

    // Slot-specific keywords — maps spoken phrases to slot names
    private static readonly Dictionary<string, string[]> SlotKeywords = new()
    {
        ["pickup"] = ["pickup", "pick up", "pick-up", "collection", "collect me", "from address", "starting point", "from"],
        ["destination"] = ["destination", "drop off", "dropoff", "drop-off", "going to", "heading to", "to address", "where i'm going", "where im going", "to"],
        ["name"] = ["name", "my name", "called", "i'm called", "im called"],
        ["passengers"] = ["passengers", "passenger", "people", "of us", "pax", "seats"],
        ["pickup_time"] = ["time", "pickup time", "pick up time", "when", "schedule", "book for", "at"],
    };

    // Regex to extract the new value after slot keyword
    // e.g. "change my pickup to 14 oak road" → "14 oak road"
    private static readonly Regex ValueAfterTo = new(
        @"\b(?:to|is|should be|it's|its)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Regex for "actually [value]" pattern without explicit slot mention
    // e.g. "actually 14 oak road" when currently collecting pickup
    private static readonly Regex ActuallyDirect = new(
        @"^(?:actually|no wait|sorry|i meant?)\s*,?\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Try to detect a correction intent in the transcript.
    /// Returns a CorrectionMatch if found, null otherwise.
    /// </summary>
    /// <param name="transcript">Raw caller transcript</param>
    /// <param name="currentSlot">The slot currently being collected (fallback target)</param>
    /// <param name="filledSlots">Which slots already have values (corrections only apply to filled slots)</param>
    public static CorrectionMatch? Detect(string transcript, string? currentSlot, IReadOnlySet<string> filledSlots)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        var lower = transcript.ToLowerInvariant().Trim();

        // Step 1: Check if transcript contains a correction trigger
        var hasCorrection = CorrectionPrefixes.Any(p => lower.Contains(p));
        if (!hasCorrection)
            return null;

        // Step 2: Try to identify which slot is being corrected
        var targetSlot = DetectTargetSlot(lower, filledSlots);

        // Step 3: Extract the new value
        var newValue = ExtractNewValue(lower, targetSlot);

        if (string.IsNullOrWhiteSpace(newValue))
            return null;

        // Step 4: If no explicit slot target, fall back to current or most recent slot
        targetSlot ??= FallbackSlot(currentSlot, filledSlots);

        if (targetSlot == null)
            return null;

        return new CorrectionMatch(targetSlot, newValue.Trim());
    }

    /// <summary>
    /// Detect which slot the caller is referring to based on keywords.
    /// Only matches against already-filled slots (can't correct what hasn't been given).
    /// </summary>
    private static string? DetectTargetSlot(string lower, IReadOnlySet<string> filledSlots)
    {
        foreach (var (slot, keywords) in SlotKeywords)
        {
            if (!filledSlots.Contains(slot))
                continue;

            if (keywords.Any(kw => lower.Contains(kw)))
                return slot;
        }

        return null;
    }

    /// <summary>
    /// Extract the new value from the correction transcript.
    /// Tries several patterns:
    /// 1. "change X to [VALUE]" / "X should be [VALUE]" / "X is [VALUE]"
    /// 2. "actually [VALUE]" (direct replacement)
    /// 3. Everything after the correction phrase + slot keyword
    /// </summary>
    private static string ExtractNewValue(string lower, string? targetSlot)
    {
        // Pattern 1: "to [value]", "is [value]", "should be [value]"
        var toMatch = ValueAfterTo.Match(lower);
        if (toMatch.Success)
            return toMatch.Groups[1].Value;

        // Pattern 2: "actually [value]" (no explicit slot)
        var directMatch = ActuallyDirect.Match(lower);
        if (directMatch.Success)
        {
            var value = directMatch.Groups[1].Value;
            // Strip any slot keywords from the beginning
            if (targetSlot != null && SlotKeywords.TryGetValue(targetSlot, out var kws))
            {
                foreach (var kw in kws.OrderByDescending(k => k.Length))
                {
                    if (value.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        value = value[kw.Length..].TrimStart(' ', ',');
                        break;
                    }
                }
            }

            return value;
        }

        // Pattern 3: Take everything after the last slot keyword
        if (targetSlot != null && SlotKeywords.TryGetValue(targetSlot, out var slotKws))
        {
            foreach (var kw in slotKws.OrderByDescending(k => k.Length))
            {
                var idx = lower.LastIndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var after = lower[(idx + kw.Length)..].TrimStart(' ', ',');
                    // Strip leading "to", "is", etc.
                    after = Regex.Replace(after, @"^(?:to|is|should be)\s+", "", RegexOptions.IgnoreCase);
                    if (!string.IsNullOrWhiteSpace(after))
                        return after;
                }
            }
        }

        return "";
    }

    /// <summary>
    /// If no explicit slot was mentioned, fall back to:
    /// 1. The current slot being collected (if already filled — re-correction)
    /// 2. The most recently filled slot (caller correcting what they just said)
    /// </summary>
    private static string? FallbackSlot(string? currentSlot, IReadOnlySet<string> filledSlots)
    {
        // If currently collecting a slot and it's filled, correct it
        if (currentSlot != null && filledSlots.Contains(currentSlot))
            return currentSlot;

        // Fall back to the last slot in collection order that has a value
        var order = new[] { "pickup_time", "passengers", "destination", "pickup", "name" };
        return order.FirstOrDefault(s => filledSlots.Contains(s));
    }
}
