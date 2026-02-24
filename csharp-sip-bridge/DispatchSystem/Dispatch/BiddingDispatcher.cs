using System.Text.Json;
using DispatchSystem.Data;
namespace DispatchSystem.Dispatch;

/// <summary>
/// Bidding-based dispatch with global optimal matching.
/// Multiple jobs can be open for bidding simultaneously. Drivers can bid on
/// multiple jobs. After each job's bidding window closes, ALL open windows
/// are checked — once all pending windows have closed, a global assignment
/// runs using a greedy scoring algorithm: 60% proximity + 40% fairness.
/// Each driver is assigned at most one job (the best match across all bids).
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
    // Jobs whose windows have closed but haven't been globally resolved yet
    private readonly Dictionary<string, List<DriverBid>> _closedBids = new();

    public event Action<string>? OnLog;
    /// <summary>Fired when bidding starts — publish bid request to these drivers via MQTT.</summary>
    public event Action<Job, List<Driver>>? OnBidRequestSent;
    /// <summary>Fired when a winner is chosen after bidding window closes.</summary>
    public event Action<Job, Driver>? OnJobAllocated;
    /// <summary>Fired for each losing bidder after winner is chosen. Args: job, losingDriverId.</summary>
    public event Action<Job, string>? OnBidLost;
    /// <summary>Fired when no bids received.</summary>
    public event Action<Job>? OnNoBids;

    public bool Enabled { get; set; } = false;

    /// <param name="biddingWindowMs">How long to wait for bids (default 30s).</param>
    /// <param name="maxBidRadiusKm">Max distance to notify drivers (default 10km).</param>
    public BiddingDispatcher(DispatchDb db, int biddingWindowMs = 30_000, double maxBidRadiusKm = 10.0)
    {
        _db = db;
        _biddingWindowMs = biddingWindowMs;
        _maxBidRadiusKm = maxBidRadiusKm;
    }

    /// <summary>
    /// Start a bidding round for a job. Finds nearby online drivers and opens a bid window.
    /// Drivers can bid on multiple jobs simultaneously.
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
            _db.UpdateJobStatus(job.Id, JobStatus.Bidding);

            var driverList = eligible.Select(x => x.Driver).ToList();
            var driverNames = string.Join(", ", driverList.Select(d => d.Name));
            var windowMs = job.BiddingWindowSec.HasValue ? job.BiddingWindowSec.Value * 1000 : _biddingWindowMs;
            OnLog?.Invoke($"📢 Bidding started for job {job.Id} → {eligible.Count} drivers ({driverNames}) — {windowMs / 1000}s window");

            OnBidRequestSent?.Invoke(job, driverList);

            var timer = new System.Threading.Timer(_ => OnWindowClosed(job.Id), null, windowMs, Timeout.Infinite);
            _timers[job.Id] = timer;

            return true;
        }
    }

    /// <summary>
    /// Record a bid from a driver for a job. A driver can bid on multiple jobs.
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

            if (bids.Any(b => b.DriverId == driverId))
            {
                OnLog?.Invoke($"⚠ Duplicate bid from {driverId} for {jobId} — ignored");
                return;
            }

            var jobs = _db.GetActiveJobs();
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null) return;

            var distKm = AutoDispatcher.HaversineKm(job.PickupLat, job.PickupLng, lat, lng);
            var completedJobs = _db.GetCompletedJobCountForDriver(driverId);

            // Get driver name for the record
            var allDrivers = _db.GetAllDrivers();
            var driverRecord = allDrivers.FirstOrDefault(d => d.Id == driverId);
            var driverName = driverRecord?.Name ?? driverId;

            bids.Add(new DriverBid
            {
                DriverId = driverId,
                DriverName = driverName,
                JobId = jobId,
                Lat = lat,
                Lng = lng,
                DistanceKm = distKm,
                CompletedJobs = completedJobs,
                BidTime = DateTime.UtcNow
            });

            // Persist bids to job record in DB
            PersistBidsToJob(jobId, bids);

            OnLog?.Invoke($"💬 Bid from {driverName} ({driverId}) for {jobId} ({distKm:F1}km, {completedJobs} prior jobs)");
        }
    }

    /// <summary>
    /// Serialize current bid list and save to the job's bids_json column.
    /// </summary>
    private void PersistBidsToJob(string jobId, List<DriverBid> bids)
    {
        try
        {
            var bidRecords = bids.Select(b => new
            {
                b.DriverId,
                b.DriverName,
                b.Lat,
                b.Lng,
                b.DistanceKm,
                b.CompletedJobs,
                BidTime = b.BidTime.ToString("o")
            });
            var json = JsonSerializer.Serialize(bidRecords);
            _db.UpdateJobBids(jobId, json);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Failed to persist bids for {jobId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when a single job's bidding window closes. Moves bids to the closed pool
    /// and triggers global matching if no more active windows remain.
    /// </summary>
    private void OnWindowClosed(string jobId)
    {
        bool shouldRunGlobalMatch = false;

        lock (_lock)
        {
            if (!_activeBids.TryGetValue(jobId, out var bidList))
                return;

            _closedBids[jobId] = bidList.ToList();
            _activeBids.Remove(jobId);

            if (_timers.TryGetValue(jobId, out var timer))
            {
                timer.Dispose();
                _timers.Remove(jobId);
            }

            OnLog?.Invoke($"⏰ Bidding window closed for {jobId} — {_closedBids[jobId].Count} bids. " +
                          $"{_activeBids.Count} jobs still open.");

            // Run global match when all active windows have closed
            shouldRunGlobalMatch = _activeBids.Count == 0 && _closedBids.Count > 0;
        }

        if (shouldRunGlobalMatch)
            RunGlobalOptimalMatch();
    }

    /// <summary>
    /// Global optimal matching across all closed bidding sessions.
    /// Uses greedy assignment: score each (driver, job) pair, pick the best,
    /// assign it, remove that driver and job from the pool, repeat.
    /// Score = 60% proximity (closer is better) + 40% fairness (fewer completed jobs is better).
    /// Each driver gets at most 1 job.
    /// </summary>
    private void RunGlobalOptimalMatch()
    {
        Dictionary<string, List<DriverBid>> closedSnapshot;
        lock (_lock)
        {
            closedSnapshot = new Dictionary<string, List<DriverBid>>(_closedBids);
            _closedBids.Clear();
        }

        try
        {
            // Collect all bids into a flat list with job context
            var allBids = new List<DriverBid>();
            var jobsWithNoBids = new List<string>();

            foreach (var (jobId, bids) in closedSnapshot)
            {
                if (bids.Count == 0)
                    jobsWithNoBids.Add(jobId);
                else
                    allBids.AddRange(bids);
            }

            // Handle jobs with no bids — return to pending
            foreach (var jobId in jobsWithNoBids)
            {
                OnLog?.Invoke($"⏰ No bids received for job {jobId}");
                _db.UpdateJobStatus(jobId, JobStatus.Pending);
                var noJob = _db.GetActiveJobs().FirstOrDefault(j => j.Id == jobId);
                if (noJob != null) OnNoBids?.Invoke(noJob);
            }

            if (allBids.Count == 0) return;

            // Compute global normalization values across ALL bids
            double maxDist = allBids.Max(b => b.DistanceKm);
            double maxJobs = allBids.Max(b => b.CompletedJobs);
            if (maxDist == 0) maxDist = 1;
            if (maxJobs == 0) maxJobs = 1;

            // Score every bid
            var scoredBids = allBids.Select(b => new
            {
                Bid = b,
                Score = 0.6 * (1.0 - b.DistanceKm / maxDist) +
                        0.4 * (1.0 - (double)b.CompletedJobs / maxJobs)
            }).OrderByDescending(x => x.Score).ToList();

            // Persist final bids with scores to each job
            foreach (var (jobId, bids) in closedSnapshot)
            {
                if (bids.Count == 0) continue;
                var bidRecords = bids.Select(b =>
                {
                    var scored = scoredBids.FirstOrDefault(s => s.Bid.DriverId == b.DriverId && s.Bid.JobId == b.JobId);
                    return new
                    {
                        b.DriverId,
                        b.DriverName,
                        b.Lat,
                        b.Lng,
                        b.DistanceKm,
                        b.CompletedJobs,
                        BidTime = b.BidTime.ToString("o"),
                        Score = scored?.Score ?? 0
                    };
                });
                var json = JsonSerializer.Serialize(bidRecords);
                _db.UpdateJobBids(jobId, json);
            }

            var assignedDrivers = new HashSet<string>();
            var assignedJobs = new HashSet<string>();
            var allDrivers = _db.GetAllDrivers();

            OnLog?.Invoke($"🧮 Running global optimal match: {closedSnapshot.Count} jobs, " +
                          $"{allBids.Select(b => b.DriverId).Distinct().Count()} unique drivers, " +
                          $"{allBids.Count} total bids");

            // Greedy assignment: best score first
            foreach (var entry in scoredBids)
            {
                var bid = entry.Bid;
                if (assignedDrivers.Contains(bid.DriverId) || assignedJobs.Contains(bid.JobId))
                    continue;

                var driver = allDrivers.FirstOrDefault(d => d.Id == bid.DriverId);
                if (driver == null) continue;

                // Assign this driver to this job
                assignedDrivers.Add(bid.DriverId);
                assignedJobs.Add(bid.JobId);

                var etaMins = (int)Math.Ceiling(bid.DistanceKm / 0.5);
                _db.UpdateJobStatus(bid.JobId, JobStatus.Allocated, driver.Id, bid.DistanceKm, etaMins);
                driver.Status = DriverStatus.OnJob;
                _db.UpsertDriver(driver);

                OnLog?.Invoke($"🏆 Global match: {bid.JobId} → {driver.Name} " +
                              $"(score={entry.Score:F3}, {bid.DistanceKm:F1}km, {bid.CompletedJobs} prior, ~{etaMins}min ETA)");

                // Fire allocated event
                var activeJobs = _db.GetActiveJobs();
                var allocatedJob = activeJobs.FirstOrDefault(j => j.Id == bid.JobId);
                if (allocatedJob != null)
                {
                    allocatedJob.AllocatedDriverId = driver.Id;
                    allocatedJob.DriverDistanceKm = bid.DistanceKm;
                    allocatedJob.DriverEtaMinutes = etaMins;
                    OnJobAllocated?.Invoke(allocatedJob, driver);
                }
            }

            // Notify losing bidders and handle unassigned jobs
            foreach (var (jobId, bids) in closedSnapshot)
            {
                if (bids.Count == 0) continue;

                if (!assignedJobs.Contains(jobId))
                {
                    // No driver available (all drivers assigned to other jobs)
                    OnLog?.Invoke($"⚠ Job {jobId} unmatched — all bidding drivers assigned elsewhere, returning to pending");
                    _db.UpdateJobStatus(jobId, JobStatus.Pending);
                    var unmatchedJob = _db.GetActiveJobs().FirstOrDefault(j => j.Id == jobId);
                    if (unmatchedJob != null) OnNoBids?.Invoke(unmatchedJob);
                    continue;
                }

                // Find the winning driver for this job
                var winnerBid = scoredBids.FirstOrDefault(s =>
                    s.Bid.JobId == jobId && assignedDrivers.Contains(s.Bid.DriverId) &&
                    assignedJobs.Contains(s.Bid.JobId));

                var activeJobsForLost = _db.GetActiveJobs();
                var jobForLost = activeJobsForLost.FirstOrDefault(j => j.Id == jobId);
                if (jobForLost == null) continue;

                foreach (var loser in bids.Where(b => b.DriverId != winnerBid?.Bid.DriverId))
                {
                    OnLog?.Invoke($"📤 Bid lost → {loser.DriverId} for {jobId}");
                    OnBidLost?.Invoke(jobForLost, loser.DriverId);
                }
            }

            OnLog?.Invoke($"✅ Global matching complete: {assignedJobs.Count} jobs assigned, " +
                          $"{closedSnapshot.Count - assignedJobs.Count - jobsWithNoBids.Count} unmatched");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"❌ Global matching error: {ex.Message}");
            // Return all jobs to pending on error
            foreach (var jobId in closedSnapshot.Keys)
                _db.UpdateJobStatus(jobId, JobStatus.Pending);
        }
    }

    /// <summary>Check if a job is currently in a bidding round.</summary>
    public bool IsJobBidding(string jobId)
    {
        lock (_lock) { return _activeBids.ContainsKey(jobId) || _closedBids.ContainsKey(jobId); }
    }

    /// <summary>Get the number of active bidding sessions.</summary>
    public int ActiveBiddingSessions
    {
        get { lock (_lock) { return _activeBids.Count + _closedBids.Count; } }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
            _activeBids.Clear();
            _closedBids.Clear();
        }
    }
}

/// <summary>
/// A bid from a driver for a specific job.
/// </summary>
public class DriverBid
{
    public string DriverId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string JobId { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double DistanceKm { get; set; }
    public int CompletedJobs { get; set; }
    public DateTime BidTime { get; set; }
}
