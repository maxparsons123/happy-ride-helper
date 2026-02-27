using System.Text.RegularExpressions;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Validates raw caller input before accepting it as a slot value.
/// Rejects greetings, filler words, and obviously invalid responses.
/// Returns null if valid, or a rejection reason if invalid.
/// </summary>
public static class SlotValidator
{
    // Common greetings / non-name responses
    private static readonly HashSet<string> Greetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "hiya", "yo", "good morning", "good afternoon",
        "good evening", "morning", "afternoon", "evening", "howdy",
        "what's up", "whats up", "how are you", "alright",
        "thanks", "thanks much", "thank you", "thank you much", "cheers", "ta",
        "thanks a lot", "thank you very much", "many thanks", "much appreciated"
    };

    // Filler / hesitation words — not valid for any slot
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "um", "uh", "hmm", "er", "ah", "umm", "uhh", "hmm",
        "well", "so", "like", "right", "okay", "ok"
    };

    // Patterns that are clearly not addresses
    private static readonly Regex NonAddressPattern = new(
        @"^(yes|no|yeah|nah|sure|please|thanks|thank you|cheers|ta|bye|goodbye)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Validate a raw value for a specific slot.
    /// Returns null if valid, or a short reason string if rejected.
    /// </summary>
    public static string? Validate(string slotName, string rawValue)
    {
        var trimmed = rawValue.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Universal: reject empty or pure filler
        if (string.IsNullOrWhiteSpace(trimmed))
            return "empty";

        if (FillerWords.Contains(lower))
            return "filler";

        // Strip trailing punctuation for matching
        var cleaned = lower.TrimEnd('.', '!', '?', ',');

        return slotName.ToLowerInvariant() switch
        {
            "name" => ValidateName(cleaned),
            "pickup" or "destination" => ValidateAddress(cleaned),
            "passengers" => ValidatePassengers(cleaned),
            "pickup_time" => ValidatePickupTime(cleaned),
            _ => null
        };
    }

    // Prefixes to strip from name input (common ways people say their name)
    private static readonly Regex NamePrefixPattern = new(
        @"^(it'?s\s+|that'?s\s+|i'?m\s+|my\s+name\s+is\s+|i\s+am\s+|they\s+call\s+me\s+|call\s+me\s+|this\s+is\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Patterns that are clearly NOT names at all (requests, questions)
    private static readonly Regex NotNamePattern = new(
        @"^(i\s+need|i\s+want|can\s+i|could\s+you|i'?d\s+like|where|when|how|what)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ValidateName(string lower)
    {
        // Reject greetings used as names
        if (Greetings.Contains(lower))
            return "greeting_not_name";

        // Reject if it's a non-address conversational filler
        if (NonAddressPattern.IsMatch(lower))
            return "conversational_not_name";

        // Reject questions/requests (these are not names)
        if (NotNamePattern.IsMatch(lower))
            return "request_not_name";

        // Strip common prefixes — "It's Max" → "Max", "My name is John" → "John"
        // (the actual slot value will be stored with prefix stripped by ProcessCallerResponseAsync)
        var stripped = NamePrefixPattern.Replace(lower, "").Trim();

        // Must be at least 2 chars after stripping
        if (stripped.Length < 2)
            return "too_short";

        return null;
    }

    private static string? ValidateAddress(string lower)
    {
        // Reject pure conversational responses
        if (NonAddressPattern.IsMatch(lower))
            return "conversational_not_address";

        // Must be at least 3 chars (shortest real address: "A1")
        if (lower.Length < 2)
            return "too_short";

        return null;
    }

    // Phonetic homophones that STT commonly mishears for passenger numbers
    private static readonly Dictionary<string, string> PassengerPhoneticMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "for", "four" }, { "fore", "four" }, { "pour", "four" }, { "poor", "four" },
        { "tree", "three" }, { "free", "three" },
        { "to", "two" }, { "too", "two" }, { "tue", "two" },
        { "won", "one" }, { "wan", "one" },
        { "sex", "six" }, { "sax", "six" },
        { "ate", "eight" }, { "ape", "eight" },
        { "fife", "five" }, { "hive", "five" },
    };

    // Goodbye / end-call phrases — reject from any slot
    private static readonly HashSet<string> GoodbyePhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "bye", "goodbye", "good bye", "see you", "see ya", "later", "cheers",
        "ta ta", "ta-ta", "tata", "ciao", "take care", "have a good day"
    };

    // Politeness phrases that should be ignored (not rejected) when collecting passengers
    private static readonly HashSet<string> PolitenessFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thanks", "thanks much", "thank you much", "thank you very much",
        "many thanks", "much appreciated", "thanks a lot", "cheers", "ta",
        "alright", "okay", "ok", "right", "sure", "yes please", "yes", "yeah"
    };

    private static string? ValidatePassengers(string lower)
    {
        // Ignore politeness phrases — don't reject, just treat as non-answer
        if (PolitenessFiller.Contains(lower.TrimEnd('.', '!', '?', ',')))
            return "filler";

        // Reject goodbye phrases before number extraction
        if (GoodbyePhrases.Contains(lower.TrimEnd('.', '!', '?', ',')))
            return "goodbye_not_passengers";

        // Accept digit strings
        if (Regex.IsMatch(lower, @"^\d+$"))
            return null;

        // Accept common spoken numbers
        var numberWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "one", "two", "three", "four", "five", "six", "seven", "eight",
            "1", "2", "3", "4", "5", "6", "7", "8"
        };

        // Extract number words from the input (e.g., "three passengers" → "three")
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            var cleaned = word.TrimEnd('.', ',', '!', '?');
            if (numberWords.Contains(cleaned))
                return null;

            // Check phonetic homophones (e.g., "for passengers" → "four")
            if (PassengerPhoneticMap.ContainsKey(cleaned))
                return null;
        }

        // Also accept patterns like "just me", "myself" = 1
        if (lower.Contains("just me") || lower == "myself" || lower == "me")
            return null;

        // Accept bare "passenger(s)" — Whisper often drops the number word entirely
        // (e.g., caller says "four passengers" but STT captures only "passengers.")
        // This will be resolved via Ada's readback or default to needing Ada interpretation
        var barePassenger = lower.TrimEnd('.', '!', '?', ',');
        if (barePassenger == "passenger" || barePassenger == "passengers" ||
            barePassenger == "passing" || barePassenger == "passing this" ||
            barePassenger == "for passing this")
            return "bare_passengers_no_number";

        return "no_number_found";
    }

    private static string? ValidatePickupTime(string lower)
    {
        // Accept ASAP variants (including common STT garbles)
        if (lower.Contains("now") || lower.Contains("asap") || lower.Contains("soon") ||
            lower.Contains("straight away") || lower.Contains("right away") ||
            lower.Contains("immediately") || lower.Contains("quick") ||
            lower.Contains("ace up") || lower.Contains("as up") || lower.Contains("a sap") ||
            lower.Contains("s.a.p") || lower.Contains("s a p") || lower.Contains("sap.") ||
            lower.Contains("just possible") || lower.Contains("that's just") ||
            lower.Contains("possible"))
            return null;

        // Accept time patterns (digits with optional colon)
        if (Regex.IsMatch(lower, @"\d"))
            return null;

        // Accept time words
        if (lower.Contains("minute") || lower.Contains("hour") || lower.Contains("o'clock") ||
            lower.Contains("oclock") || lower.Contains("half") || lower.Contains("quarter") ||
            lower.Contains("morning") || lower.Contains("afternoon") || lower.Contains("evening") ||
            lower.Contains("tonight") || lower.Contains("tomorrow") || lower.Contains("today"))
            return null;

        return "no_time_found";
    }
}