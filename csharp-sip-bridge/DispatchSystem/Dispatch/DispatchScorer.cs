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
    public double Utility(double distanceKm, int completedJobs, DriverStats stats, double spoofRisk)
    {
        // Proximity: closer is better (cap at 10km)
        double distScore = 1.0 - Math.Min(1.0, distanceKm / 10.0);

        // Fairness: fewer completed jobs is better (cap at 200)
        double fairnessScore = 1.0 - Math.Min(1.0, completedJobs / 200.0);

        // Reliability
        double relScore = Reliability.Score(stats);

        // ETA quality (cap at 30 min)
        var etaMin = _eta.PredictEtaMinutes(distanceKm, DateTime.UtcNow);
        double etaScore = 1.0 - Math.Min(1.0, etaMin / 30.0);

        // Spoof penalty multiplier
        double spoofMult = SpoofDetector.PenaltyMultiplier(spoofRisk);

        // Weighted combination
        double baseScore =
            0.40 * distScore +
            0.20 * fairnessScore +
            0.25 * relScore +
            0.15 * etaScore;

        double final_ = baseScore * spoofMult;

        return Math.Max(0, Math.Min(1, final_));
    }
}
