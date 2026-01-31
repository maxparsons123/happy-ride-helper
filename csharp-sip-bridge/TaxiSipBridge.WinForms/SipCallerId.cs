using System;
using System.Text.RegularExpressions;
using SIPSorcery.SIP;

namespace TaxiSipBridge;

/// <summary>
/// Extract a best-effort caller identifier (preferably a phone number) from a SIP INVITE.
/// Many SIP trunks put the E.164 number in headers like P-Asserted-Identity.
/// </summary>
internal static class SipCallerId
{
    public static string Extract(SIPRequest req)
    {
        if (req == null) return "unknown";

        // 1) Try common carrier headers (most reliable)
        var raw = req.ToString() ?? string.Empty;
        var candidates = new[]
        {
            TryGetUserFromHeader(raw, "P-Asserted-Identity"),
            TryGetUserFromHeader(raw, "P-Preferred-Identity"),
            TryGetUserFromHeader(raw, "Remote-Party-ID"),
            TryGetUserFromHeader(raw, "Diversion"),

            // 2) Fallback to From user
            req.Header?.From?.FromURI?.User,
            req.Header?.From?.FromURI?.ToString(),
        };

        foreach (var c in candidates)
        {
            var normalized = NormalizeCaller(c);
            if (!string.IsNullOrWhiteSpace(normalized) && !normalized.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
                return normalized;
        }

        return req.Header?.From?.FromURI?.User ?? "unknown";
    }

    private static string? TryGetUserFromHeader(string rawSip, string headerName)
    {
        if (string.IsNullOrWhiteSpace(rawSip)) return null;

        // Example:
        // P-Asserted-Identity: <sip:+31612345678@carrier.example>
        // Remote-Party-ID: "Caller" <sip:0031612345678@carrier>;party=calling
        var m = Regex.Match(
            rawSip,
            $@"(?im)^{Regex.Escape(headerName)}\s*:\s*<?(?:(?:sip|tel):)?(?<user>[^@>;\s\r\n]+)",
            RegexOptions.CultureInvariant);

        return m.Success ? m.Groups["user"].Value : null;
    }

    private static string? NormalizeCaller(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var s = value.Trim();

        // Strip any URI wrappers/schemes if present
        s = s.Trim('<', '>', '"');
        if (s.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
        if (s.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);

        // Remove URI params and domain
        var at = s.IndexOf('@');
        if (at >= 0) s = s.Substring(0, at);
        var semi = s.IndexOf(';');
        if (semi >= 0) s = s.Substring(0, semi);

        // Keep digits and '+' only
        s = Regex.Replace(s, @"[^0-9+]", "");

        // If it is +XXXXXXXX, keep as-is; if 00XXXXXXXX, keep as-is (language detector handles it)
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
