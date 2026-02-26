using System.Text.RegularExpressions;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Splits multi-slot caller utterances and detects cross-slot mismatches.
/// 
/// Examples:
///   "52A David Road, going to Manchester" during CollectingPickup
///     → pickup="52A David Road", destination="Manchester"
///   "Three passengers" during CollectingDestination
///     → reroute to passengers slot with value "Three passengers"
/// </summary>
public static class FreeFormSplitter
{
    /// <summary>
    /// Result of free-form splitting — the primary slot value and any overflow slots.
    /// </summary>
    public record SplitResult
    {
        /// <summary>Value for the current (expected) slot. Null if input belongs to a different slot entirely.</summary>
        public string? PrimaryValue { get; init; }

        /// <summary>Additional slot values extracted from the utterance.</summary>
        public Dictionary<string, string> OverflowSlots { get; init; } = new();

        /// <summary>If the input belongs to a different slot entirely, which slot?</summary>
        public string? RerouteToSlot { get; init; }

        /// <summary>True if any splitting/rerouting occurred.</summary>
        public bool WasSplit => OverflowSlots.Count > 0 || RerouteToSlot != null;
    }

    // Destination signal phrases — split pickup from destination
    private static readonly Regex DestinationSignal = new(
        @"[,.]?\s*(?:going\s+to|heading\s+to|dropping?\s+(?:off\s+)?(?:at|to)|and\s+(?:then\s+)?to|to\s+go\s+to)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pickup signal phrases — split destination from pickup  
    private static readonly Regex PickupSignal = new(
        @"[,.]?\s*(?:from|picking?\s+(?:up\s+)?(?:at|from)|coming\s+from|starting\s+(?:at|from))\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Passenger patterns — detect "three passengers", "4 people", "just me" etc.
    private static readonly Regex PassengerPattern = new(
        @"^(?:(\d+|one|two|three|four|five|six|seven|eight)\s+)?(?:passengers?|people|persons?|of\s+us|pax)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Standalone passenger count — "just me", "myself", digit-only
    private static readonly Regex StandalonePassenger = new(
        @"^(?:just\s+me|myself|\d)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Time patterns — detect ASAP/time answers given out of order
    private static readonly Regex TimePattern = new(
        @"^(?:now|asap|as\s+soon\s+as\s+possible|straight\s+away|right\s+away|immediately|in\s+\d+\s+minutes?)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Analyze a transcript for the given slot and split/reroute if needed.
    /// </summary>
    /// <param name="transcript">Raw caller transcript</param>
    /// <param name="currentSlot">The slot we're currently collecting</param>
    public static SplitResult Analyze(string transcript, string currentSlot)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new SplitResult { PrimaryValue = transcript };

        var trimmed = transcript.Trim();

        // ── Cross-slot mismatch detection ──
        // If we're collecting an address slot but the input looks like passengers
        if (currentSlot is "pickup" or "destination")
        {
            if (PassengerPattern.IsMatch(trimmed.TrimEnd('.', ',', '!', '?')) ||
                StandalonePassenger.IsMatch(trimmed.TrimEnd('.', ',', '!', '?')))
            {
                return new SplitResult
                {
                    PrimaryValue = null,
                    RerouteToSlot = "passengers",
                    OverflowSlots = { ["passengers"] = trimmed }
                };
            }

            if (TimePattern.IsMatch(trimmed.TrimEnd('.', ',', '!', '?')))
            {
                return new SplitResult
                {
                    PrimaryValue = null,
                    RerouteToSlot = "pickup_time",
                    OverflowSlots = { ["pickup_time"] = trimmed }
                };
            }
        }

        // If we're collecting passengers but the input looks like an address
        if (currentSlot == "passengers")
        {
            var cleaned = trimmed.TrimEnd('.', ',', '!', '?');
            if (!PassengerPattern.IsMatch(cleaned) &&
                !StandalonePassenger.IsMatch(cleaned) &&
                !Regex.IsMatch(cleaned, @"^\d+$") &&
                !IsSpokenNumber(cleaned))
            {
                // This might be an address — but let the validator handle it
                // Only reroute if it clearly has address markers
                if (HasAddressMarkers(cleaned))
                {
                    // Determine if it's pickup or destination based on which is missing
                    return new SplitResult
                    {
                        PrimaryValue = null,
                        RerouteToSlot = "destination", // Most likely destination at this point
                        OverflowSlots = { ["destination"] = trimmed }
                    };
                }
            }
        }

        // ── Multi-slot splitting for pickup ──
        if (currentSlot == "pickup")
        {
            var destMatch = DestinationSignal.Match(trimmed);
            if (destMatch.Success)
            {
                var pickupPart = trimmed[..destMatch.Index].TrimEnd(',', '.', ' ');
                var destPart = destMatch.Groups[1].Value.TrimEnd('.', ',', '!', '?');

                if (!string.IsNullOrWhiteSpace(pickupPart) && !string.IsNullOrWhiteSpace(destPart))
                {
                    return new SplitResult
                    {
                        PrimaryValue = pickupPart,
                        OverflowSlots = { ["destination"] = destPart }
                    };
                }
            }
        }

        // ── Multi-slot splitting for destination ──
        if (currentSlot == "destination")
        {
            var pickupMatch = PickupSignal.Match(trimmed);
            if (pickupMatch.Success)
            {
                var destPart = trimmed[..pickupMatch.Index].TrimEnd(',', '.', ' ');
                var pickupPart = pickupMatch.Groups[1].Value.TrimEnd('.', ',', '!', '?');

                if (!string.IsNullOrWhiteSpace(destPart) && !string.IsNullOrWhiteSpace(pickupPart))
                {
                    return new SplitResult
                    {
                        PrimaryValue = destPart,
                        OverflowSlots = { ["pickup"] = pickupPart }
                    };
                }
            }
        }

        // No splitting needed
        return new SplitResult { PrimaryValue = trimmed };
    }

    private static bool IsSpokenNumber(string s) =>
        s is "one" or "two" or "three" or "four" or "five" or "six" or "seven" or "eight";

    private static bool HasAddressMarkers(string s)
    {
        // Check for house numbers, street suffixes, or postcodes
        return Regex.IsMatch(s, @"\d+\s*[A-Za-z]?\s+\w+", RegexOptions.IgnoreCase) || // house number pattern
               Regex.IsMatch(s, @"\b(?:road|rd|street|st|lane|avenue|ave|close|drive|way|crescent|terrace|gardens?|place|grove|hill)\b", RegexOptions.IgnoreCase) || // street suffix
               Regex.IsMatch(s, @"[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}", RegexOptions.IgnoreCase); // postcode
    }
}
