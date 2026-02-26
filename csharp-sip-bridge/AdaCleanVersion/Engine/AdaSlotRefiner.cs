using System.Text.RegularExpressions;

namespace AdaCleanVersion.Engine;

/// <summary>
/// Extracts confirmed slot values from Ada's spoken response.
/// 
/// Ada's interpretation is the source of truth — when she echoes back a value
/// (e.g., "Thank you, Max" or "Got it, 52A David Road"), that confirmed value
/// replaces the raw Whisper STT transcript for the slot.
/// 
/// This eliminates STT artifacts like:
///   - "It's Max" → "Max"
///   - "52A" misheard as "528"
///   - "Warwick Road" misheard as "war road"
/// </summary>
public static class AdaSlotRefiner
{
    // Common patterns Ada uses when confirming values
    private static readonly Regex NamePattern = new(
        @"(?:thank\s+you|thanks|hi|hello|ok|okay|great|got\s+it)[,.]?\s+(\w[\w\s\-']+?)(?:\.|,|\s+(?:where|can|could|what|how|and|i'll|let|now))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AddressConfirmPattern = new(
        @"(?:got\s+it|noted|okay|from|to|picking\s+(?:you\s+)?up\s+(?:from|at)|heading\s+to|destination\s+is|pickup\s+(?:is|at|from))[,.:;\s]+(.+?)(?:\.|,\s*(?:and|where|how|what|can|could|is\s+that)|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PassengersPattern = new(
        @"(\d+)\s*(?:passenger|person|people|pax|of\s+you)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimePattern = new(
        @"(?:for|at|pickup\s+(?:at|time))[,:\s]+(.+?)(?:\.|,\s*(?:and|is|that)|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Try to extract a refined slot value from Ada's response.
    /// Returns null if the value couldn't be confidently extracted.
    /// </summary>
    public static string? ExtractSlotValue(string slotName, string adaResponse)
    {
        if (string.IsNullOrWhiteSpace(adaResponse)) return null;

        return slotName.ToLowerInvariant() switch
        {
            "name" => ExtractName(adaResponse),
            "pickup" => ExtractAddress(adaResponse),
            "destination" => ExtractAddress(adaResponse),
            "passengers" => ExtractPassengers(adaResponse),
            "pickup_time" => ExtractTime(adaResponse),
            _ => null
        };
    }

    private static string? ExtractName(string adaResponse)
    {
        var match = NamePattern.Match(adaResponse);
        if (!match.Success) return null;

        var name = match.Groups[1].Value.Trim();

        // Basic sanity: name should be 2-30 chars, no numbers
        if (name.Length < 2 || name.Length > 30) return null;
        if (Regex.IsMatch(name, @"\d")) return null;

        return name;
    }

    private static string? ExtractAddress(string adaResponse)
    {
        var match = AddressConfirmPattern.Match(adaResponse);
        if (!match.Success) return null;

        var address = match.Groups[1].Value.Trim().TrimEnd('.', ',', '?', '!');

        // Sanity: addresses should be at least 3 chars
        if (address.Length < 3) return null;

        return address;
    }

    private static string? ExtractPassengers(string adaResponse)
    {
        var match = PassengersPattern.Match(adaResponse);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractTime(string adaResponse)
    {
        // Check for ASAP-like phrases
        if (Regex.IsMatch(adaResponse, @"\b(as\s+soon\s+as\s+possible|ASAP|right\s+away|now|immediately)\b", RegexOptions.IgnoreCase))
            return "ASAP";

        var match = TimePattern.Match(adaResponse);
        return match.Success ? match.Groups[1].Value.Trim().TrimEnd('.', ',', '?', '!') : null;
    }
}
