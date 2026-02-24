namespace DispatchSystem.Dispatch;

/// <summary>
/// Pluggable ETA prediction interface.
/// Start with SimpleEtaModel; swap in ML model later.
/// </summary>
public interface IEtaModel
{
    int PredictEtaMinutes(double km, DateTime utcNow, string? zoneId = null);
}

/// <summary>
/// Baseline ETA: distance / speed-by-time-of-day.
/// Accounts for rush-hour congestion.
/// </summary>
public sealed class SimpleEtaModel : IEtaModel
{
    public int PredictEtaMinutes(double km, DateTime utcNow, string? zoneId = null)
    {
        var hour = utcNow.Hour;

        // Rush hours: 7-9, 16-18
        double kmh = (hour >= 7 && hour <= 9) || (hour >= 16 && hour <= 18) ? 22 : 28;

        // Zone penalty (city centre etc.)
        if (zoneId != null) kmh *= 0.9;

        var minutes = (km / Math.Max(5.0, kmh)) * 60.0;
        return (int)Math.Max(2, Math.Ceiling(minutes));
    }
}
