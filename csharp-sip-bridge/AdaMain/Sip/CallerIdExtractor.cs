using System.Text.RegularExpressions;
using SIPSorcery.SIP;

namespace AdaMain.Sip;

/// <summary>
/// Extract caller ID from SIP headers.
/// </summary>
public static partial class CallerIdExtractor
{
    [GeneratedRegex(@"(?:sip:|tel:)?(\+?[\d\-\s\(\)]+)[@>]?", RegexOptions.IgnoreCase)]
    private static partial Regex PhonePattern();
    
    /// <summary>
    /// Extract caller phone from SIP request, checking multiple headers.
    /// </summary>
    public static string Extract(SIPRequest request)
    {
        // Priority order: P-Asserted-Identity > Remote-Party-ID > From
        var headers = new[]
        {
            request.Header.GetUnknownHeaderValue("P-Asserted-Identity"),
            request.Header.GetUnknownHeaderValue("P-Preferred-Identity"),
            request.Header.GetUnknownHeaderValue("Remote-Party-ID"),
            request.Header.From?.FromURI?.User
        };
        
        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header))
            {
                var match = PhonePattern().Match(header);
                if (match.Success)
                {
                    var phone = NormalizePhone(match.Groups[1].Value);
                    if (!string.IsNullOrEmpty(phone) && phone.Length >= 6)
                        return phone;
                }
            }
        }
        
        return request.Header.From?.FromURI?.User ?? "unknown";
    }
    
    private static string NormalizePhone(string raw)
    {
        // Keep only digits and leading +
        var clean = raw.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
        if (clean.StartsWith("+"))
            return "+" + new string(clean[1..].Where(char.IsDigit).ToArray());
        return new string(clean.Where(char.IsDigit).ToArray());
    }
}
