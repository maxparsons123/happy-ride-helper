using System.Text.Json;
using DispatchSystem.Data;
namespace DispatchSystem.Dispatch;

/// <summary>
/// Bidding-based dispatch with global optimal matching via Hungarian algorithm.
/// Phases: (A) Bidding — collect bids + compute rich scores, (B) Allocation — 
/// Hungarian optimal 1:1 assignment, (C) Bundling — suggest follow-on jobs.
/// 
/// Scoring includes: proximity, fairness, reliability, ETA quality, GPS spoof penalty.
/// </summary>
public sealed class BiddingDispatcher : IDisposable
{
    private readonly DispatchDb _db;
    private readonly int _biddingWindowMs;
    private readonly double _maxBidRadiusKm;
    private readonly object _lock = new();
    private readonly DispatchScorer _scorer;

    // Active bidding sessions: jobId → list of bids
    private readonly Dictionary<string, List<DriverBid>> _activeBids = new();
    private readonly Dictionary<string, System.Threading.Timer> _timers = new();
    // Jobs whose windows have closed but haven't been globally resolved yet
    private readonly Dictionary<string, List<DriverBid>> _closedBids = new();
    // Rolling last-known location per driver (for spoof detection)
    private readonly Dictionary<string, LocationSample> _lastLocations = new();

    public event Action<string>? OnLog;
    /// <summary>Fired when bidding starts — publish bid request to these drivers via MQTT.</summary>
    public event Action<Job, List<Driver>>? OnBidRequestSent;
    /// <summary>Fired when a winner is chosen after bidding window closes.</summary>
    public event Action<Job, Driver>? OnJobAllocated;
    /// <summary>Fired for each losing bidder after winner is chosen. Args: job, losingDriverId.</summary>
    public event Action<Job, string>? OnBidLost;
    /// <summary>Fired when no bids received.</summary>
    public event Action<Job>? OnNoBids;
    /// <summary>Fired when a bundle/chain opportunity is detected. Args: primary job, candidate next jobs.</summary>
    public event Action<Job, List<Job>>? OnBundleOpportunity;

    public bool Enabled { get; set; } = false;

    /// <param name="biddingWindowMs">How long to wait for bids (default 30s).</param>
    /// <param name="maxBidRadiusKm">Max distance to notify drivers (default 10km).</param>
    public BiddingDispatcher(DispatchDb db, int biddingWindowMs = 30_000, double maxBidRadiusKm = 10.0)
    {
        _db = db;
        _biddingWindowMs = biddingWindowMs;
        _maxBidRadiusKm = maxBidRadiusKm;
        _scorer = new DispatchScorer(new SimpleEtaModel());
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
    /// Also runs spoof detection against last known location.
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

            // Get driver name
            var allDrivers = _db.GetAllDrivers();
            var driverRecord = allDrivers.FirstOrDefault(d => d.Id == driverId);
            var driverName = driverRecord?.Name ?? driverId;

            // Spoof detection
            var currentSample = new LocationSample { Lat = lat, Lng = lng, TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            _lastLocations.TryGetValue(driverId, out var prevSample);
            var (spoofRisk, spoofFlags) = SpoofDetector.Evaluate(prevSample, currentSample);
            _lastLocations[driverId] = currentSample;

            // Driver stats for reliability
            var stats = _db.GetDriverStats(driverId);
            var reliabilityScore = Reliability.Score(stats);

            bids.Add(new DriverBid
            {
                DriverId = driverId,
                DriverName = driverName,
                JobId = jobId,
                Lat = lat,
                Lng = lng,
                DistanceKm = distKm,
                CompletedJobs = completedJobs,
                BidTime = DateTime.UtcNow,
                SpoofRisk = spoofRisk,
                SpoofFlags = spoofFlags,
                ReliabilityScore = reliabilityScore
            });

            // Persist bids to job record in DB
            PersistBidsToJob(jobId, bids);

            var spoofNote = spoofFlags.Count > 0 ? $" ⚠ spoof:{string.Join(",", spoofFlags)}" : "";
            OnLog?.Invoke($"💬 Bid from {driverName} ({driverId}) for {jobId} ({distKm:F1}km, reliability={reliabilityScore:F2}{spoofNote})");
        }
    }

    /// <summary>
    /// Update the last known location for a driver (called from GPS status updates).
    /// Feeds into spoof detection.
    /// </summary>
    public void UpdateDriverLocation(string driverId, double lat, double lng)
    {
        lock (_lock)
        {
            _lastLocations[driverId] = new LocationSample
            {
                Lat = lat,
                Lng = lng,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
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
                BidTime = b.BidTime.ToString("o"),
                b.ReliabilityScore,
                b.SpoofRisk,
                b.SpoofFlags,
                Score = b.FinalScore
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
    /// Uses Hungarian algorithm for true optimal 1:1 assignment.
    /// Score = f(proximity, fairness, reliability, ETA, spoofPenalty).
    /// Each driver gets at most 1 job.
    /// After allocation, runs bundle detection.
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
            // Separate jobs with/without bids
            var jobsWithBids = new List<(string jobId, List<DriverBid> bids)>();
            var jobsWithNoBids = new List<string>();

            foreach (var (jobId, bids) in closedSnapshot)
            {
                if (bids.Count == 0)
                    jobsWithNoBids.Add(jobId);
                else
                    jobsWithBids.Add((jobId, bids));
            }

            // Handle jobs with no bids — return to pending
            foreach (var jobId in jobsWithNoBids)
            {
                OnLog?.Invoke($"⏰ No bids received for job {jobId}");
                _db.UpdateJobStatus(jobId, JobStatus.Pending);
                var noJob = _db.GetActiveJobs().FirstOrDefault(j => j.Id == jobId);
                if (noJob != null) OnNoBids?.Invoke(noJob);
            }

            if (jobsWithBids.Count == 0) return;

            // Collect unique drivers who bid
            var driverIds = jobsWithBids.SelectMany(j => j.bids).Select(b => b.DriverId).Distinct().ToList();
            var allDrivers = _db.GetAllDrivers();

            OnLog?.Invoke($"🧮 Running Hungarian optimal match: {jobsWithBids.Count} jobs, " +
                          $"{driverIds.Count} unique drivers");

            // Build cost matrix (jobs = rows, drivers = columns)
            int nJobs = jobsWithBids.Count;
            int nDrivers = driverIds.Count;
            int N = Math.Max(nJobs, nDrivers);
            var cost = new double[N, N];

            // Fill with high cost (1.0 = no match possible)
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                    cost[i, j] = 1.0;

            // Compute utility for each (job, driver) pair where bid exists
            for (int ji = 0; ji < nJobs; ji++)
            {
                var (jobId, bids) = jobsWithBids[ji];
                foreach (var bid in bids)
                {
                    int di = driverIds.IndexOf(bid.DriverId);
                    if (di < 0) continue;

                    var stats = _db.GetDriverStats(bid.DriverId);
                    var utility = _scorer.Utility(bid.DistanceKm, bid.CompletedJobs, stats, bid.SpoofRisk);
                    bid.FinalScore = utility;

                    cost[ji, di] = 1.0 - utility;
                }
            }

            // Run Hungarian algorithm
            var assignment = Hungarian.Solve(cost);

            // Process assignments
            var assignedJobs = new HashSet<string>();

            for (int ji = 0; ji < nJobs; ji++)
            {
                var (jobId, bids) = jobsWithBids[ji];
                int di = assignment[ji];

                // Persist scored bids
                PersistBidsToJob(jobId, bids);

                // Check if assignment is valid
                if (di < 0 || di >= nDrivers)
                {
                    OnLog?.Invoke($"⚠ Job {jobId} unmatched by Hungarian — returning to pending");
                    _db.UpdateJobStatus(jobId, JobStatus.Pending);
                    var unmatchedJob = _db.GetActiveJobs().FirstOrDefault(j => j.Id == jobId);
                    if (unmatchedJob != null) OnNoBids?.Invoke(unmatchedJob);
                    continue;
                }

                var winnerId = driverIds[di];
                var winnerBid = bids.FirstOrDefault(b => b.DriverId == winnerId);

                // Must have actually bid on this job
                if (winnerBid == null || (winnerBid.FinalScore ?? 0) < 0.01)
                {
                    OnLog?.Invoke($"⚠ Job {jobId} — assigned driver {winnerId} didn't bid or score too low, returning to pending");
                    _db.UpdateJobStatus(jobId, JobStatus.Pending);
                    var unmatchedJob = _db.GetActiveJobs().FirstOrDefault(j => j.Id == jobId);
                    if (unmatchedJob != null) OnNoBids?.Invoke(unmatchedJob);
                    continue;
                }

                var driver = allDrivers.FirstOrDefault(d => d.Id == winnerId);
                if (driver == null || driver.Status != DriverStatus.Online)
                {
                    OnLog?.Invoke($"⚠ Job {jobId} — winner {winnerId} offline, returning to pending");
                    _db.UpdateJobStatus(jobId, JobStatus.Pending);
                    continue;
                }

                // Allocate
                var etaModel = new SimpleEtaModel();
                var etaMins = etaModel.PredictEtaMinutes(winnerBid.DistanceKm, DateTime.UtcNow);
                _db.UpdateJobStatus(jobId, JobStatus.Allocated, driver.Id, winnerBid.DistanceKm, etaMins);
                driver.Status = DriverStatus.OnJob;
                _db.UpsertDriver(driver);
                assignedJobs.Add(jobId);

                OnLog?.Invoke($"🏆 Hungarian match: {jobId} → {driver.Name} " +
                              $"(score={winnerBid.FinalScore:F3}, dist={winnerBid.DistanceKm:F1}km, " +
                              $"reliability={winnerBid.ReliabilityScore:F2}, spoofRisk={winnerBid.SpoofRisk:F2}, ~{etaMins}min ETA)");

                // Fire allocated event
                var activeJobs = _db.GetActiveJobs();
                var allocatedJob = activeJobs.FirstOrDefault(j => j.Id == jobId);
                if (allocatedJob != null)
                {
                    allocatedJob.AllocatedDriverId = driver.Id;
                    allocatedJob.DriverDistanceKm = winnerBid.DistanceKm;
                    allocatedJob.DriverEtaMinutes = etaMins;
                    OnJobAllocated?.Invoke(allocatedJob, driver);

                    // Bundle detection: find follow-on jobs near this dropoff
                    var pendingJobs = _db.GetPendingJobs();
                    var connectors = Bundler.FindConnectors(allocatedJob, pendingJobs);
                    if (connectors.Count > 0)
                    {
                        OnLog?.Invoke($"🔗 Bundle opportunity: {connectors.Count} jobs near dropoff of {jobId}");
                        OnBundleOpportunity?.Invoke(allocatedJob, connectors);
                    }
                }

                // Notify losers
                var jobForNotify = allocatedJob ?? new Job { Id = jobId };
                foreach (var loser in bids.Where(b => b.DriverId != winnerId))
                {
                    OnLog?.Invoke($"📤 Bid lost → {loser.DriverName} ({loser.DriverId}) for {jobId}");
                    OnBidLost?.Invoke(jobForNotify, loser.DriverId);
                }
            }

            OnLog?.Invoke($"✅ Hungarian matching complete: {assignedJobs.Count}/{jobsWithBids.Count} jobs assigned");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"❌ Global matching error: {ex.Message}");
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
/// A bid from a driver for a specific job — enriched with reliability + spoof data.
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
    // Enhanced fields
    public double SpoofRisk { get; set; }
    public List<string> SpoofFlags { get; set; } = new();
    public double ReliabilityScore { get; set; } = 1.0;
    public double? FinalScore { get; set; }
}
