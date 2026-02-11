using DispatchSystem.Data;

namespace DispatchSystem.Dispatch;

/// <summary>
/// Bidding-based dispatch: when a job arrives, notifies nearby drivers who can bid.
/// After a configurable window (default 20s), selects the best driver based on
/// 60% proximity + 40% fewest completed jobs (fairness).
/// </summary>
public sealed class BiddingDispatcher : IDisposable
{
    private readonly DispatchDb _db;
    private readonly int _biddingWindowMs;
    private readonly double _maxBidRadiusKm;
    private readonly object _lock = new();

    // Active bidding sessions: jobId → list of bids
    private readonly Dictionary<string, List<DriverBid>> _activeBids = new();
    private readonly Dictionary<string, System.Threading.Timer> _timers = new();

    public event Action<string>? OnLog;
    /// <summary>Fired when bidding starts — publish bid request to these drivers via MQTT.</summary>
    public event Action<Job, List<Driver>>? OnBidRequestSent;
    /// <summary>Fired when a winner is chosen after bidding window closes.</summary>
    public event Action<Job, Driver>? OnJobAllocated;
    /// <summary>Fired when no bids received.</summary>
    public event Action<Job>? OnNoBids;

    public bool Enabled { get; set; } = false;

    /// <param name="biddingWindowMs">How long to wait for bids (default 20s).</param>
    /// <param name="maxBidRadiusKm">Max distance to notify drivers (default 10km).</param>
    public BiddingDispatcher(DispatchDb db, int biddingWindowMs = 20_000, double maxBidRadiusKm = 10.0)
    {
        _db = db;
        _biddingWindowMs = biddingWindowMs;
        _maxBidRadiusKm = maxBidRadiusKm;
    }

    /// <summary>
    /// Start a bidding round for a job. Finds nearby online drivers and opens a bid window.
    /// Returns true if bidding started (drivers found), false if no eligible drivers.
    /// </summary>
    public bool StartBidding(Job job)
    {
        if (!Enabled) return false;

        lock (_lock)
        {
            if (_activeBids.ContainsKey(job.Id))
            {
                OnLog?.Invoke($"⚠ Bidding already active for job {job.Id}");
                return false;
            }

            var allDrivers = _db.GetAllDrivers();
            var eligible = allDrivers
                .Where(d => d.Status == DriverStatus.Online)
                .Where(d => (int)d.Vehicle >= (int)job.VehicleRequired)
                .Select(d => new
                {
                    Driver = d,
                    DistKm = AutoDispatcher.HaversineKm(job.PickupLat, job.PickupLng, d.Lat, d.Lng)
                })
                .Where(x => x.DistKm <= _maxBidRadiusKm)
                .OrderBy(x => x.DistKm)
                .ToList();

            if (eligible.Count == 0)
            {
                OnLog?.Invoke($"⚠ No drivers within {_maxBidRadiusKm}km for job {job.Id}");
                return false;
            }

            _activeBids[job.Id] = new List<DriverBid>();

            // Update job status to Bidding
            _db.UpdateJobStatus(job.Id, JobStatus.Bidding);

            var driverList = eligible.Select(x => x.Driver).ToList();
            var driverNames = string.Join(", ", driverList.Select(d => d.Name));
            OnLog?.Invoke($"📢 Bidding started for job {job.Id} → {eligible.Count} drivers ({driverNames}) — {_biddingWindowMs / 1000}s window");

            // Notify via event so MainForm can publish MQTT
            OnBidRequestSent?.Invoke(job, driverList);

            // Start countdown timer
            var timer = new System.Threading.Timer(_ => FinalizeBidding(job.Id), null, _biddingWindowMs, Timeout.Infinite);
            _timers[job.Id] = timer;

            return true;
        }
    }

    /// <summary>
    /// Record a bid from a driver. Called when MQTT receives a bid response.
    /// </summary>
    public void RecordBid(string jobId, string driverId, double lat, double lng)
    {
        lock (_lock)
        {
            if (!_activeBids.TryGetValue(jobId, out var bids))
            {
                OnLog?.Invoke($"⚠ Bid from {driverId} for {jobId} — no active bidding session");
                return;
            }

            // Avoid duplicate bids
            if (bids.Any(b => b.DriverId == driverId))
            {
                OnLog?.Invoke($"⚠ Duplicate bid from {driverId} for {jobId} — ignored");
                return;
            }

            // Get job for distance calc
            var jobs = _db.GetActiveJobs();
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null) return;

            var distKm = AutoDispatcher.HaversineKm(job.PickupLat, job.PickupLng, lat, lng);
            var completedJobs = _db.GetCompletedJobCountForDriver(driverId);

            bids.Add(new DriverBid
            {
                DriverId = driverId,
                Lat = lat,
                Lng = lng,
                DistanceKm = distKm,
                CompletedJobs = completedJobs,
                BidTime = DateTime.UtcNow
            });

            OnLog?.Invoke($"💬 Bid from {driverId} for {jobId} ({distKm:F1}km, {completedJobs} prior jobs)");
        }
    }

    /// <summary>
    /// Called when the bidding timer expires. Selects the best driver.
    /// Scoring: 60% closest distance + 40% fewest completed jobs (fairness).
    /// </summary>
    private void FinalizeBidding(string jobId)
    {
        List<DriverBid> bids;
        lock (_lock)
        {
            if (!_activeBids.TryGetValue(jobId, out var bidList))
                return;

            bids = bidList.ToList();
            _activeBids.Remove(jobId);

            if (_timers.TryGetValue(jobId, out var timer))
            {
                timer.Dispose();
                _timers.Remove(jobId);
            }
        }

        try
        {
            if (bids.Count == 0)
            {
                OnLog?.Invoke($"⏰ No bids received for job {jobId}");
                _db.UpdateJobStatus(jobId, JobStatus.Pending); // Back to pending for auto-dispatch fallback
                var jobs = _db.GetActiveJobs();
                var noJob = jobs.FirstOrDefault(j => j.Id == jobId);
                if (noJob != null) OnNoBids?.Invoke(noJob);
                return;
            }

            // Score: 60% proximity (lower = better) + 40% fewest jobs (lower = better)
            double maxDist = bids.Max(b => b.DistanceKm);
            double maxJobs = bids.Max(b => b.CompletedJobs);
            if (maxDist == 0) maxDist = 1;
            if (maxJobs == 0) maxJobs = 1;

            var winner = bids
                .OrderByDescending(b =>
                    0.6 * (1.0 - b.DistanceKm / maxDist) +
                    0.4 * (1.0 - b.CompletedJobs / maxJobs))
                .First();

            // Get full driver record
            var allDrivers = _db.GetAllDrivers();
            var driver = allDrivers.FirstOrDefault(d => d.Id == winner.DriverId);
            if (driver == null)
            {
                OnLog?.Invoke($"❌ Winner driver {winner.DriverId} not found in DB");
                _db.UpdateJobStatus(jobId, JobStatus.Pending);
                return;
            }

            var etaMins = (int)Math.Ceiling(winner.DistanceKm / 0.5);

            _db.UpdateJobStatus(jobId, JobStatus.Allocated, driver.Id, winner.DistanceKm, etaMins);
            driver.Status = DriverStatus.OnJob;
            _db.UpsertDriver(driver);

            OnLog?.Invoke($"🏆 Bidding winner for {jobId} → {driver.Name} ({winner.DistanceKm:F1}km, {winner.CompletedJobs} prior jobs, ~{etaMins}min ETA)");

            // Get updated job
            var activeJobs = _db.GetActiveJobs();
            var allocatedJob = activeJobs.FirstOrDefault(j => j.Id == jobId);
            if (allocatedJob != null)
            {
                allocatedJob.AllocatedDriverId = driver.Id;
                allocatedJob.DriverDistanceKm = winner.DistanceKm;
                allocatedJob.DriverEtaMinutes = etaMins;
                OnJobAllocated?.Invoke(allocatedJob, driver);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"❌ Bidding finalize error: {ex.Message}");
            _db.UpdateJobStatus(jobId, JobStatus.Pending);
        }
    }

    /// <summary>Check if a job is currently in a bidding round.</summary>
    public bool IsJobBidding(string jobId)
    {
        lock (_lock) { return _activeBids.ContainsKey(jobId); }
    }

    /// <summary>Get the number of active bidding sessions.</summary>
    public int ActiveBiddingSessions
    {
        get { lock (_lock) { return _activeBids.Count; } }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
            _activeBids.Clear();
        }
    }
}

/// <summary>
/// A bid from a driver for a specific job.
/// </summary>
public class DriverBid
{
    public string DriverId { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double DistanceKm { get; set; }
    public int CompletedJobs { get; set; }
    public DateTime BidTime { get; set; }
}
