// Last updated: 2026-02-26 (v2.9) — copied from AdaSdkModel with NamedPlaceKeywords
using System.Globalization;
using System.Text.RegularExpressions;

namespace AdaCleanVersion.Services;

/// <summary>
/// Parsed components of a UK address string.
/// </summary>
public class AddressComponents
{
    public string FlatOrUnit { get; set; } = "";
    public string HouseNumber { get; set; } = "";
    public string BuildingName { get; set; } = "";
    public string StreetName { get; set; } = "";
    public string TownOrArea { get; set; } = "";

    /// <summary>True when the street type typically requires a house number (Road, Avenue, Close, etc.)</summary>
    public bool IsStreetTypeAddress { get; set; }

    /// <summary>True when a house number is present (non-empty, non-zero).</summary>
    public bool HasHouseNumber => !string.IsNullOrWhiteSpace(HouseNumber) && HouseNumber != "0";
}

/// <summary>
/// Lightweight UK address parser.
/// Used to extract structured components (house number, street, town) from freeform address strings.
/// </summary>
public static class AddressParser
{
    private static readonly HashSet<string> StreetSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "road", "rd", "street", "st", "lane", "ln", "avenue", "ave",
        "close", "cl", "drive", "dr", "way", "walk", "grove", "row",
        "boulevard", "blvd", "court", "ct", "crescent", "cr", "place", "pl",
        "terrace", "gardens", "garden", "square", "sq", "parade", "rise",
        "mews", "hill", "view", "green", "end"
    };

    private static readonly HashSet<string> NamedPlaceKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "station", "railway", "airport", "terminal", "bus", "metro", "tram", "interchange",
        "supermarket", "aldi", "lidl", "tesco", "asda", "morrisons", "sainsburys", "waitrose",
        "market", "mall", "centre", "center", "retail", "arcade",
        "hotel", "inn", "pub", "bar", "restaurant", "cafe", "cinema", "theatre",
        "stadium", "arena", "park", "museum", "gallery", "library",
        "hospital", "clinic", "surgery", "pharmacy", "school", "college", "university",
        "church", "mosque", "temple", "gurdwara", "synagogue", "chapel",
        "office", "tower", "building", "complex", "plaza"
    };

    private static readonly string[] KnownTowns =
    {
        "birmingham", "coventry", "manchester", "london", "leicester", "derby",
        "solihull", "edgbaston", "erdington", "wolverhampton", "dudley", "walsall",
        "heathrow", "gatwick", "luton", "stansted", "bristol", "oxford", "warwick",
        "nottingham", "sheffield", "leeds", "liverpool", "newcastle", "bradford",
        "stoke", "reading", "southampton", "portsmouth", "swindon"
    };

    /// <summary>
    /// Parse a UK address string into its components.
    /// House number "0" means not found.
    /// </summary>
    public static AddressComponents ParseAddress(string address)
    {
        var components = new AddressComponents();

        if (string.IsNullOrWhiteSpace(address))
            return components;

        address = address.Trim().Replace(",", "").Replace(".", "");

        string flatOrUnit = "";
        string houseNumber = "0";
        string remaining = address;

        var unitMatch = Regex.Match(remaining, @"^(Flat|Unit|Apt)\s+(\w+)\s+", RegexOptions.IgnoreCase);
        if (unitMatch.Success)
        {
            flatOrUnit = $"{unitMatch.Groups[1].Value} {unitMatch.Groups[2].Value}";
            remaining = remaining[unitMatch.Length..];
        }

        var numberMatch = Regex.Match(remaining, @"^(\d+(?:-?[A-Za-z]\d*|-\d+)?)\s+(.+)");
        if (numberMatch.Success)
        {
            houseNumber = numberMatch.Groups[1].Value;
            remaining = numberMatch.Groups[2].Value;
        }

        string detectedTown = KnownTowns.FirstOrDefault(t =>
            remaining.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? "";

        if (!string.IsNullOrEmpty(detectedTown))
        {
            components.TownOrArea = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(detectedTown);
            var lowerRemaining = remaining.ToLowerInvariant();
            var idx = lowerRemaining.IndexOf(detectedTown, StringComparison.Ordinal);
            if (idx >= 0)
                remaining = remaining[..idx].Trim();
        }

        var parts = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allWords = address.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool isNamedPlace = allWords.Any(w => NamedPlaceKeywords.Contains(w));

        int suffixIndex = -1;
        if (!isNamedPlace)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (StreetSuffixes.Contains(parts[i]))
                {
                    suffixIndex = i;
                    break;
                }
            }
        }

        if (suffixIndex != -1)
        {
            components.StreetName = string.Join(" ", parts.Take(suffixIndex + 1));
            components.IsStreetTypeAddress = true;

            if (suffixIndex + 1 < parts.Length)
                components.BuildingName = string.Join(" ", parts.Skip(suffixIndex + 1));
        }
        else
        {
            components.StreetName = remaining;
            components.IsStreetTypeAddress = false;
        }

        components.FlatOrUnit = flatOrUnit;
        components.HouseNumber = houseNumber;

        return components;
    }

    /// <summary>
    /// Returns true if the address is a street-type address (Road, Avenue, Close, etc.)
    /// but contains no house number.
    /// </summary>
    public static bool RequiresHouseNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var c = ParseAddress(address);
        return c.IsStreetTypeAddress && !c.HasHouseNumber;
    }
}
