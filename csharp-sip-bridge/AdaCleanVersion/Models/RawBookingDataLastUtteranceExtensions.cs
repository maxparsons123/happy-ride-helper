using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AdaCleanVersion.Models;

/// <summary>
/// Backward-compatible storage for caller raw STT transcripts per address slot.
/// Uses RawBookingData Pickup/Destination LastUtterance properties when available;
/// falls back to per-instance side storage when building against older RawBookingData shapes.
/// </summary>
public static class RawBookingDataLastUtteranceExtensions
{
    private static readonly ConditionalWeakTable<RawBookingData, Dictionary<string, string>> FallbackStore = new();

    public static string? GetLastUtterance(this RawBookingData rawData, string slotName)
    {
        var normalized = NormalizeSlot(slotName);
        if (normalized == null) return null;

        var property = GetLastUtteranceProperty(normalized);
        if (property != null)
            return property.GetValue(rawData) as string;

        var bag = FallbackStore.GetOrCreateValue(rawData);
        return bag.TryGetValue(normalized, out var value) ? value : null;
    }

    public static void SetLastUtterance(this RawBookingData rawData, string slotName, string? value)
    {
        var normalized = NormalizeSlot(slotName);
        if (normalized == null) return;

        var property = GetLastUtteranceProperty(normalized);
        if (property != null)
        {
            property.SetValue(rawData, value);
            return;
        }

        var bag = FallbackStore.GetOrCreateValue(rawData);
        if (string.IsNullOrWhiteSpace(value))
            bag.Remove(normalized);
        else
            bag[normalized] = value;
    }

    private static System.Reflection.PropertyInfo? GetLastUtteranceProperty(string normalizedSlot)
    {
        var propertyName = normalizedSlot == "pickup" ? "PickupLastUtterance" : "DestinationLastUtterance";
        var property = typeof(RawBookingData).GetProperty(propertyName);
        return property?.PropertyType == typeof(string) && property.CanRead && property.CanWrite
            ? property
            : null;
    }

    private static string? NormalizeSlot(string slotName) =>
        slotName.ToLowerInvariant() switch
        {
            "pickup" => "pickup",
            "destination" => "destination",
            _ => null
        };
}
