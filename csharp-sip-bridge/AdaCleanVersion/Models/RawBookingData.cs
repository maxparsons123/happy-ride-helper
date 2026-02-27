namespace AdaCleanVersion.Models;

/// <summary>
/// Mutable raw slot storage. Holds verbatim caller phrases — no normalization.
/// Corrections simply overwrite. This is NOT the authoritative booking.
/// </summary>
public class RawBookingData
{
    public string? NameRaw { get; set; }
    public string? PickupRaw { get; set; }
    public string? DestinationRaw { get; set; }
    public string? PassengersRaw { get; set; }
    public string? PickupTimeRaw { get; set; }

    /// <summary>
    /// Gemini-cleaned version of pickup address (from burst-dispatch).
    /// Used for Ada readback so she reads the AI-corrected version, not raw STT.
    /// </summary>
    public string? PickupGemini { get; set; }

    /// <summary>
    /// Raw STT transcript (last_utterance) from when the pickup was spoken.
    /// Preserved separately from PickupRaw (which is the AI interpretation).
    /// Used to cross-check against zone_pois for STT-garbled POI names.
    /// </summary>
    public string? PickupLastUtterance { get; set; }

    /// <summary>
    /// Gemini-cleaned version of destination address (from burst-dispatch).
    /// Used for Ada readback so she reads the AI-corrected version, not raw STT.
    /// </summary>
    public string? DestinationGemini { get; set; }

    /// <summary>
    /// Raw STT transcript (last_utterance) from when the destination was spoken.
    /// Preserved separately from DestinationRaw (which is the AI interpretation).
    /// Used to cross-check against zone_pois for STT-garbled POI names.
    /// </summary>
    public string? DestinationLastUtterance { get; set; }

    /// <summary>
    /// True when the caller provided multiple slots in a single utterance.
    /// Used by PromptBuilder to adjust verification instructions.
    /// </summary>
    public bool IsMultiSlotBurst { get; set; }

    /// <summary>
    /// Pre-cached geocoding result from burst-dispatch edge function.
    /// When set, verification states can skip separate geocoding calls.
    /// Contains coordinates, zone info, fare data from address-dispatch.
    /// </summary>
    public System.Text.Json.JsonElement? BurstGeocodedResult { get; set; }

    /// <summary>True when all required raw slots have a non-empty value.</summary>
    public bool AllRequiredPresent =>
        !string.IsNullOrWhiteSpace(NameRaw) &&
        !string.IsNullOrWhiteSpace(PickupRaw) &&
        !string.IsNullOrWhiteSpace(DestinationRaw) &&
        !string.IsNullOrWhiteSpace(PassengersRaw) &&
        !string.IsNullOrWhiteSpace(PickupTimeRaw);

    /// <summary>Which slot is missing next, in collection order.</summary>
    public string? NextMissingSlot()
    {
        if (string.IsNullOrWhiteSpace(NameRaw)) return "name";
        if (string.IsNullOrWhiteSpace(PickupRaw)) return "pickup";
        if (string.IsNullOrWhiteSpace(DestinationRaw)) return "destination";
        if (string.IsNullOrWhiteSpace(PassengersRaw)) return "passengers";
        if (string.IsNullOrWhiteSpace(PickupTimeRaw)) return "pickup_time";
        return null;
    }

    /// <summary>Set a slot by name. Returns true if slot was recognized.</summary>
    public bool SetSlot(string slotName, string value)
    {
        switch (slotName.ToLowerInvariant())
        {
            case "name": NameRaw = value; return true;
            case "pickup": PickupRaw = value; return true;
            case "destination": DestinationRaw = value; return true;
            case "passengers": PassengersRaw = value; return true;
            case "pickup_time": PickupTimeRaw = value; return true;
            default: return false;
        }
    }

    /// <summary>Get current slot value by name.</summary>
    public string? GetSlot(string slotName) => slotName.ToLowerInvariant() switch
    {
        "name" => NameRaw,
        "pickup" => PickupRaw,
        "destination" => DestinationRaw,
        "passengers" => PassengersRaw,
        "pickup_time" => PickupTimeRaw,
        _ => null
    };

    /// <summary>Returns the set of slot names that currently have non-empty values.</summary>
    public IReadOnlySet<string> FilledSlots
    {
        get
        {
            var filled = new HashSet<string>();
            if (!string.IsNullOrWhiteSpace(NameRaw)) filled.Add("name");
            if (!string.IsNullOrWhiteSpace(PickupRaw)) filled.Add("pickup");
            if (!string.IsNullOrWhiteSpace(DestinationRaw)) filled.Add("destination");
            if (!string.IsNullOrWhiteSpace(PassengersRaw)) filled.Add("passengers");
            if (!string.IsNullOrWhiteSpace(PickupTimeRaw)) filled.Add("pickup_time");
            return filled;
        }
    }
}
