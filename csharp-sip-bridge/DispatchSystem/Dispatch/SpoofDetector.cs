namespace DispatchSystem.Dispatch;

/// <summary>
/// A timestamped GPS location sample for spoof detection.
/// </summary>
public sealed class LocationSample
{
    public double Lat { get; init; }
    public double Lng { get; init; }
    public long TsMs { get; init; }
}

/// <summary>
/// Heuristic GPS spoof / bad location detector.
/// Returns a risk score (0..1) and descriptive flags.
/// </summary>
public static class SpoofDetector
{
    /// <summary>
    /// Evaluate a location sample against the previous one.
    /// Returns (risk 0..1, list of flag strings).
    /// </summary>
    public static (double risk, List<string> flags) Evaluate(LocationSample? prev, LocationSample current)
    {
        var flags = new List<string>();
        double risk = 0;

        // Stale location check
        var ageSec = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - current.TsMs) / 1000.0;
        if (ageSec > 20) { risk += 0.25; flags.Add("stale_location"); }

        if (prev != null)
        {
            var dt = Math.Max(1, (current.TsMs - prev.TsMs) / 1000.0);
            var km = AutoDispatcher.HaversineKm(prev.Lat, prev.Lng, current.Lat, current.Lng);
            var kmh = (km / dt) * 3600.0;

            // Unrealistic taxi speed
            if (kmh > 140) { risk += 0.55; flags.Add($"speed_{kmh:F0}kmh"); }
            else if (kmh > 110) { risk += 0.35; flags.Add($"speed_{kmh:F0}kmh"); }

            // Identical coords for too long (possible fake GPS app)
            if (km < 0.005 && dt > 60) { risk += 0.10; flags.Add("static_coords"); }
        }

        risk = Math.Max(0, Math.Min(1, risk));
        return (risk, flags);
    }

    /// <summary>
    /// Convert risk (0..1) into a multiplier for the bid score.
    /// risk 0.0 → 1.0 (no penalty)
    /// risk 1.0 → 0.4 (heavy penalty)
    /// </summary>
    public static double PenaltyMultiplier(double risk)
    {
        return 1.0 - 0.6 * Math.Max(0, Math.Min(1, risk));
    }
}
