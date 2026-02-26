using System.Runtime.CompilerServices;

namespace AdaCleanVersion.Models;

/// <summary>
/// Backward-compatible storage for Gemini-cleaned address slots.
/// Uses RawBookingData properties when available; falls back to per-instance side storage.
/// </summary>
public static class RawBookingDataGeminiExtensions
{
    private static readonly ConditionalWeakTable<RawBookingData, Dictionary<string, string>> FallbackStore = new();

    public static string? GetGeminiSlot(this RawBookingData rawData, string slotName)
    {
        var normalized = NormalizeSlot(slotName);
        if (normalized == null) return null;

        var property = GetGeminiProperty(normalized);
        if (property != null)
            return property.GetValue(rawData) as string;

        var bag = FallbackStore.GetOrCreateValue(rawData);
        return bag.TryGetValue(normalized, out var value) ? value : null;
    }

    public static void SetGeminiSlot(this RawBookingData rawData, string slotName, string? value)
    {
        var normalized = NormalizeSlot(slotName);
        if (normalized == null) return;

        var property = GetGeminiProperty(normalized);
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

    private static System.Reflection.PropertyInfo? GetGeminiProperty(string normalizedSlot)
    {
        var propertyName = normalizedSlot == "pickup" ? "PickupGemini" : "DestinationGemini";
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
