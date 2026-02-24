using DispatchSystem.Data;

namespace DispatchSystem.Dispatch;

/// <summary>
/// Computes a unified utility score (0..1) for a (job, driver) pair.
/// Combines proximity, fairness, reliability, ETA, and spoof penalty.
/// </summary>
public sealed class DispatchScorer
{
    private readonly IEtaModel _eta;

    public DispatchScorer(IEtaModel? eta = null)
    {
        _eta = eta ?? new SimpleEtaModel();
    }

    /// <summary>
    /// Compute utility for assigning this driver to this job.
    /// </summary>
    /// <param name="distanceKm">Haversine distance from driver to pickup.</param>
    /// <param name="completedJobs">Driver's total completed jobs (for fairness).</param>
    /// <param name="stats">Driver reliability stats.</param>
    /// <param name="spoofRisk">GPS spoof risk 0..1.</param>
    /// <returns>Utility score 0..1 (higher = better match).</returns>
    public double Utility(double distanceKm, int completedJobs, DriverStats stats, double spoofRisk,
        double gpsAccuracyMeters = 999, double heading = 0, double pickupBearing = -1, string? lastJobCompletedAt = null)
    {
        // Proximity: closer is better (cap at 10km)
        double distScore = 1.0 - Math.Min(1.0, distanceKm / 10.0);

        // Fairness: fewer completed jobs is better (cap at 200)
        double fairnessScore = 1.0 - Math.Min(1.0, completedJobs / 200.0);

        // Idle time bonus: drivers waiting longer get priority
        double idleBonus = 0;
        if (lastJobCompletedAt != null && DateTime.TryParse(lastJobCompletedAt, out var lastCompleted))
        {
            var idleMinutes = (DateTime.UtcNow - lastCompleted).TotalMinutes;
            idleBonus = Math.Min(1.0, idleMinutes / 60.0); // caps at 60 min idle
        }

        // Reliability
        double relScore = Reliability.Score(stats);

        // ETA quality (cap at 30 min)
        var etaMin = _eta.PredictEtaMinutes(distanceKm, DateTime.UtcNow);
        double etaScore = 1.0 - Math.Min(1.0, etaMin / 30.0);

        // Heading bonus: if driver is heading toward pickup, slight boost
        double headingBonus = 0;
        if (pickupBearing >= 0 && heading >= 0)
        {
            var angleDiff = Math.Abs(heading - pickupBearing);
            if (angleDiff > 180) angleDiff = 360 - angleDiff;
            headingBonus = angleDiff < 45 ? 0.05 : (angleDiff < 90 ? 0.02 : 0);
        }

        // GPS accuracy penalty: poor accuracy reduces confidence
        double gpsPenalty = gpsAccuracyMeters > 100 ? 0.95 : (gpsAccuracyMeters > 50 ? 0.98 : 1.0);

        // Spoof penalty multiplier
        double spoofMult = SpoofDetector.PenaltyMultiplier(spoofRisk);

        // Weighted combination
        double baseScore =
            0.35 * distScore +
            0.20 * fairnessScore +
            0.10 * idleBonus +
            0.20 * relScore +
            0.15 * etaScore +
            headingBonus;

        double final_ = baseScore * spoofMult * gpsPenalty;

        return Math.Max(0, Math.Min(1, final_));
    }
}
