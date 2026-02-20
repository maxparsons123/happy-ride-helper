using System.Globalization;
using System.Text.RegularExpressions;

namespace AdaSdkModel.Services;

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
/// Used to detect missing house numbers on street-type addresses
/// (Road, Avenue, Close, Street, Drive, Lane, etc.) before dispatch.
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

    /// <summary>
    /// Named-place keywords. If any word in the address matches one of these,
    /// the address is a landmark/POI and never requires a house number,
    /// even if it also contains a street-suffix word (e.g. "New Street Railway Station").
    /// </summary>
    private static readonly HashSet<string> NamedPlaceKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Transport
        "station", "railway", "airport", "terminal", "bus", "metro", "tram", "interchange",
        // Retail / food
        "supermarket", "aldi", "lidl", "tesco", "asda", "morrisons", "sainsburys", "waitrose",
        "market", "mall", "centre", "center", "retail", "arcade",
        // Hospitality & leisure
        "hotel", "inn", "pub", "bar", "restaurant", "cafe", "cafe", "cinema", "theatre",
        "stadium", "arena", "park", "museum", "gallery", "library",
        // Health & education
        "hospital", "clinic", "surgery", "pharmacy", "school", "college", "university",
        // Religion & community
        "church", "mosque", "temple", "gurdwara", "synagogue", "chapel",
        // Other landmarks
        "office", "tower", "building", "complex", "centre", "center", "plaza"
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
    /// Adjective/qualifier prefixes that form part of a road name rather than a house number.
    /// e.g. "Devon Russell Street", "Old Town Road", "New Street", "Upper Hill Road".
    /// When the first word is one of these AND there is no leading digit, the address
    /// is treated as a named road — house number not required.
    /// </summary>
    private static readonly HashSet<string> RoadNamePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Counties / regions that prefix road names
        "devon", "cornwall", "kent", "essex", "surrey", "sussex", "dorset",
        "suffolk", "norfolk", "lincoln", "york", "durham", "cumbria",
        // Common adjective road prefixes
        "new", "old", "upper", "lower", "north", "south", "east", "west",
        "great", "little", "long", "high", "low", "near", "far", "broad",
        "central", "inner", "outer", "middle", "cross", "back", "front"
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

        // Detect Flat/Unit prefix (e.g. "Flat 2 52A David Road")
        var unitMatch = Regex.Match(remaining, @"^(Flat|Unit|Apt)\s+(\w+)\s+", RegexOptions.IgnoreCase);
        if (unitMatch.Success)
        {
            flatOrUnit = $"{unitMatch.Groups[1].Value} {unitMatch.Groups[2].Value}";
            remaining = remaining[unitMatch.Length..];
        }

        // Detect leading house number:
        // "43 Dovey Road" → "43", "52A David Road" → "52A", "52-8 David Road" → "52-8",
        // "52-A David Road" → "52-A" (hyphen-then-letter suffix, common in UK STT output)
        var numberMatch = Regex.Match(remaining, @"^(\d+(?:-?[A-Za-z]\d*|-\d+)?)\s+(.+)");
        if (numberMatch.Success)
        {
            houseNumber = numberMatch.Groups[1].Value;
            remaining = numberMatch.Groups[2].Value;
        }

        // Detect known town/area
        string detectedTown = KnownTowns.FirstOrDefault(t =>
            remaining.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? "";

        if (!string.IsNullOrEmpty(detectedTown))
        {
            components.TownOrArea = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(detectedTown);
            // Remove town words from remaining
            var lowerRemaining = remaining.ToLowerInvariant();
            var idx = lowerRemaining.IndexOf(detectedTown, StringComparison.Ordinal);
            if (idx >= 0)
                remaining = remaining[..idx].Trim();
        }

        // Split into parts and find street suffix
        var parts = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // If ANY word in the full address is a named-place keyword (station, supermarket, hotel, etc.)
        // then this is a POI/landmark — never requires a house number, regardless of street-suffix words.
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

        // Road-name prefix guard:
        // If the first word is an adjective/county prefix (e.g. "Devon", "Old", "New") and
        // there is NO leading house number, the full phrase is a named road — not a residential
        // address — so a house number must NOT be required.
        // Example: "Devon Russell Street" → named road ✓
        //          "Old Town Road"        → named road ✓
        //          "52 Old Town Road"     → residential (has number) ✓
        bool startsWithRoadPrefix = parts.Length > 0 && RoadNamePrefixes.Contains(parts[0]);
        bool hasLeadingNumber = components.HouseNumber != "0";

        if (suffixIndex != -1)
        {
            components.StreetName = string.Join(" ", parts.Take(suffixIndex + 1));

            // Suppress house-number requirement for named roads (road-prefix with no number)
            components.IsStreetTypeAddress = !(startsWithRoadPrefix && !hasLeadingNumber);

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
    /// but contains no house number. Used to prompt Ada to ask the caller for one.
    /// </summary>
    public static bool RequiresHouseNumber(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var c = ParseAddress(address);
        return c.IsStreetTypeAddress && !c.HasHouseNumber;
    }
}
