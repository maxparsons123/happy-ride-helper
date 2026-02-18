namespace WhatsAppTaxiBooker.Helpers;

/// <summary>
/// UK phone number normalisation for WhatsApp Cloud API.
/// Converts all common UK formats to the E.164 "44..." format without '+'.
/// </summary>
public static class PhoneFormatter
{
    public static string ToWhatsAppFormat(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw ?? "";

        // Keep digits only
        string cleaned = new string(raw.Where(char.IsDigit).ToArray());

        // 0044xxxx → 44xxxx
        if (cleaned.StartsWith("0044"))
            cleaned = "44" + cleaned[4..];

        // +44xxxx (after digit strip, just "44...")
        // 0xxxxxxxxx → 44xxxxxxxxx
        else if (cleaned.StartsWith("0") && cleaned.Length > 9)
            cleaned = "44" + cleaned[1..];

        // Already starts with 44 → OK
        if (cleaned.StartsWith("44"))
            return cleaned;

        // Missing country code but valid length → add 44
        if (cleaned.Length >= 10)
            cleaned = "44" + cleaned;

        return cleaned;
    }
}
