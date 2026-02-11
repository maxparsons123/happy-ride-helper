using DispatchSystem.Data;

namespace DispatchSystem.Dispatch;

/// <summary>
/// Auto-dispatch algorithm: runs every 60s, allocates pending jobs to the best available driver.
/// Scoring: 60% proximity + 40% wait time. Filters by vehicle type.
/// </summary>
public sealed class AutoDispatcher : IDisposable
{
    private readonly DispatchDb _db;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private bool _enabled = true;

    public event Action<string>? OnLog;
    public event Action<Job, Driver>? OnJobAllocated;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnLog?.Invoke($"Auto-dispatch {(_enabled ? "enabled" : "disabled")}"); }
    }

    public AutoDispatcher(DispatchDb db, int intervalMs = 60_000)
    {
        _db = db;
        _timer = new System.Threading.Timer(_ => RunCycle(), null, 5_000, intervalMs);
    }

    public void RunCycle()
    {
        if (!_enabled) return;

        lock (_lock)
        {
            try
            {
                var pending = _db.GetPendingJobs();
                if (pending.Count == 0) return;

                var allDrivers = _db.GetAllDrivers();

                foreach (var job in pending)
                {
                    var available = allDrivers
                        .Where(d => d.Status == DriverStatus.Online)
                        .Where(d => CanServeVehicle(d.Vehicle, job.VehicleRequired))
                        .ToList();

                    if (available.Count == 0)
                    {
                        OnLog?.Invoke($"⚠ No drivers for job {job.Id} ({job.VehicleRequired})");
                        continue;
                    }

                    var best = ScoreAndRank(available, job);
                    if (best == null) continue;

                    var distKm = HaversineKm(job.PickupLat, job.PickupLng, best.Lat, best.Lng);
                    var etaMins = (int)Math.Ceiling(distKm / 0.5); // ~30 km/h avg

                    _db.UpdateJobStatus(job.Id, JobStatus.Allocated, best.Id, distKm, etaMins);
                    best.Status = DriverStatus.OnJob;
                    _db.UpsertDriver(best);

                    job.Status = JobStatus.Allocated;
                    job.AllocatedDriverId = best.Id;
                    job.DriverDistanceKm = distKm;
                    job.DriverEtaMinutes = etaMins;

                    OnLog?.Invoke($"✅ Job {job.Id} → Driver {best.Name} ({distKm:F1} km, ~{etaMins} min)");
                    OnJobAllocated?.Invoke(job, best);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Auto-dispatch error: {ex.Message}");
            }
        }
    }

    private Driver? ScoreAndRank(List<Driver> drivers, Job job)
    {
        double maxDist = 0;
        double maxWait = 0;

        var scored = new List<(Driver d, double dist, double waitMins)>();

        foreach (var d in drivers)
        {
            var dist = HaversineKm(job.PickupLat, job.PickupLng, d.Lat, d.Lng);
            var waitMins = (DateTime.UtcNow - (d.LastJobCompletedAt ?? d.StatusChangedAt)).TotalMinutes;

            scored.Add((d, dist, waitMins));
            if (dist > maxDist) maxDist = dist;
            if (waitMins > maxWait) maxWait = waitMins;
        }

        if (maxDist == 0) maxDist = 1;
        if (maxWait == 0) maxWait = 1;

        // Lower distance = better (invert), higher wait = better
        // Score = 0.6 * (1 - normDist) + 0.4 * normWait
        return scored
            .OrderByDescending(s =>
                0.6 * (1.0 - s.dist / maxDist) +
                0.4 * (s.waitMins / maxWait))
            .FirstOrDefault().d;
    }

    private static bool CanServeVehicle(VehicleType driverVehicle, VehicleType required)
    {
        // Larger vehicles can serve smaller requests
        return (int)driverVehicle >= (int)required;
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    public void Dispose() => _timer?.Dispose();
}
