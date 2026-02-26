using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AdaCleanVersion.Models;

/// <summary>
/// Backward-compatible storage for burst geocoding payload.
/// Uses RawBookingData.BurstGeocodedResult when present; otherwise falls back to per-instance side storage.
/// </summary>
public static class RawBookingDataBurstExtensions
{
    private static readonly ConditionalWeakTable<RawBookingData, BurstSideData> FallbackStore = new();

    public static JsonElement? GetBurstGeocodedResult(this RawBookingData rawData)
    {
        var property = typeof(RawBookingData).GetProperty("BurstGeocodedResult");
        if (property?.PropertyType == typeof(JsonElement?) && property.CanRead)
            return (JsonElement?)property.GetValue(rawData);

        return FallbackStore.TryGetValue(rawData, out var side) ? side.Geocoded : null;
    }

    public static void SetBurstGeocodedResult(this RawBookingData rawData, JsonElement? value)
    {
        var property = typeof(RawBookingData).GetProperty("BurstGeocodedResult");
        if (property?.PropertyType == typeof(JsonElement?) && property.CanWrite)
        {
            property.SetValue(rawData, value);
            return;
        }

        var side = FallbackStore.GetOrCreateValue(rawData);
        side.Geocoded = value;
    }

    private sealed class BurstSideData
    {
        public JsonElement? Geocoded { get; set; }
    }
}
