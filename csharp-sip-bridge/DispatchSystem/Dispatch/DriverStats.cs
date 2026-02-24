namespace DispatchSystem.Dispatch;

/// <summary>
/// Per-driver reliability and performance statistics.
/// Stored in SQLite driver_stats table and used for bid scoring.
/// </summary>
public sealed class DriverStats
{
    public string DriverId { get; set; } = "";
    public int CompletedJobs { get; set; }
    public int CancelledJobs { get; set; }
    public int NoShowCancels { get; set; }
    public double AcceptRate { get; set; } = 1.0;
    public double AvgRating { get; set; } = 5.0;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Computes a 0..1 reliability score from driver stats.
/// Higher = more trustworthy driver.
/// </summary>
public static class Reliability
{
    public static double Score(DriverStats s)
    {
        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

        var total = Math.Max(1, s.CompletedJobs + s.CancelledJobs);
        var cancelRate = (double)s.CancelledJobs / total;
        var noShowRate = (double)s.NoShowCancels / total;
        var rating = Clamp01((s.AvgRating - 3.5) / 1.5);
        var accept = Clamp01(s.AcceptRate);

        var score = 0.45 * (1.0 - cancelRate)
                  + 0.20 * (1.0 - noShowRate)
                  + 0.20 * accept
                  + 0.15 * rating;

        return Clamp01(score);
    }
}
