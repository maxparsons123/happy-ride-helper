using Microsoft.Data.Sqlite;

namespace DispatchSystem.Data;

/// <summary>
/// SQLite database for drivers, jobs, and allocation history.
/// </summary>
public sealed class DispatchDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public DispatchDb(string dbPath = "dispatch.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS drivers (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                phone TEXT,
                registration TEXT DEFAULT '',
                vehicle_type TEXT NOT NULL DEFAULT 'Saloon',
                status TEXT NOT NULL DEFAULT 'Offline',
                lat REAL DEFAULT 0,
                lng REAL DEFAULT 0,
                last_gps_update TEXT,
                last_job_completed_at TEXT,
                status_changed_at TEXT,
                total_jobs_completed INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                pickup TEXT NOT NULL,
                dropoff TEXT NOT NULL,
                pickup_lat REAL DEFAULT 0,
                pickup_lng REAL DEFAULT 0,
                dropoff_lat REAL DEFAULT 0,
                dropoff_lng REAL DEFAULT 0,
                passengers INTEGER DEFAULT 1,
                vehicle_required TEXT DEFAULT 'Saloon',
                special_requirements TEXT,
                estimated_fare REAL,
                caller_phone TEXT,
                caller_name TEXT,
                booking_ref TEXT,
                status TEXT NOT NULL DEFAULT 'Pending',
                allocated_driver_id TEXT,
                created_at TEXT NOT NULL,
                allocated_at TEXT,
                completed_at TEXT,
                driver_distance_km REAL,
                driver_eta_minutes INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_jobs_status ON jobs(status);
            CREATE INDEX IF NOT EXISTS idx_drivers_status ON drivers(status);
        """;
        cmd.ExecuteNonQuery();

        // Add total_jobs_completed column if missing (migration for existing DBs)
        try
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE drivers ADD COLUMN total_jobs_completed INTEGER DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        catch { /* column already exists */ }
        cmd.ExecuteNonQuery();
    }

    // ── Drivers ──

    public void UpsertDriver(Driver d)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drivers (id, name, phone, registration, vehicle_type, status, lat, lng, last_gps_update, last_job_completed_at, status_changed_at, total_jobs_completed)
            VALUES ($id, $name, $phone, $reg, $vt, $status, $lat, $lng, $gps, $ljc, $sc, $tjc)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                phone = excluded.phone,
                registration = excluded.registration,
                vehicle_type = excluded.vehicle_type,
                status = excluded.status,
                lat = excluded.lat,
                lng = excluded.lng,
                last_gps_update = excluded.last_gps_update,
                last_job_completed_at = excluded.last_job_completed_at,
                status_changed_at = excluded.status_changed_at,
                total_jobs_completed = excluded.total_jobs_completed
        """;
        cmd.Parameters.AddWithValue("$id", d.Id);
        cmd.Parameters.AddWithValue("$name", d.Name);
        cmd.Parameters.AddWithValue("$phone", d.Phone ?? "");
        cmd.Parameters.AddWithValue("$reg", d.Registration ?? "");
        cmd.Parameters.AddWithValue("$vt", d.Vehicle.ToString());
        cmd.Parameters.AddWithValue("$status", d.Status.ToString());
        cmd.Parameters.AddWithValue("$lat", d.Lat);
        cmd.Parameters.AddWithValue("$lng", d.Lng);
        cmd.Parameters.AddWithValue("$gps", d.LastGpsUpdate.ToString("o"));
        cmd.Parameters.AddWithValue("$ljc", (object?)d.LastJobCompletedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sc", d.StatusChangedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$tjc", d.TotalJobsCompleted);
        cmd.ExecuteNonQuery();
    }

    public void UpdateDriverLocation(string driverId, double lat, double lng, DriverStatus? status = null)
    {
        using var cmd = _conn.CreateCommand();
        if (status.HasValue)
        {
            cmd.CommandText = """
                UPDATE drivers SET lat = $lat, lng = $lng, last_gps_update = $gps, status = $status, status_changed_at = $sc
                WHERE id = $id
            """;
            cmd.Parameters.AddWithValue("$status", status.Value.ToString());
            cmd.Parameters.AddWithValue("$sc", DateTime.UtcNow.ToString("o"));
        }
        else
        {
            cmd.CommandText = "UPDATE drivers SET lat = $lat, lng = $lng, last_gps_update = $gps WHERE id = $id";
        }
        cmd.Parameters.AddWithValue("$id", driverId);
        cmd.Parameters.AddWithValue("$lat", lat);
        cmd.Parameters.AddWithValue("$lng", lng);
        cmd.Parameters.AddWithValue("$gps", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<Driver> GetAllDrivers()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM drivers ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<Driver>();
        while (r.Read())
        {
            list.Add(new Driver
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Phone = r.IsDBNull(2) ? "" : r.GetString(2),
                Registration = r.IsDBNull(3) ? "" : r.GetString(3),
                Vehicle = Enum.TryParse<VehicleType>(r.GetString(4), out var vt) ? vt : VehicleType.Saloon,
                Status = Enum.TryParse<DriverStatus>(r.GetString(5), out var ds) ? ds : DriverStatus.Offline,
                Lat = r.GetDouble(6),
                Lng = r.GetDouble(7),
                LastGpsUpdate = DateTime.TryParse(r.IsDBNull(8) ? null : r.GetString(8), out var gps) ? gps : DateTime.MinValue,
                LastJobCompletedAt = !r.IsDBNull(9) && DateTime.TryParse(r.GetString(9), out var ljc) ? ljc : null,
                StatusChangedAt = DateTime.TryParse(r.IsDBNull(10) ? null : r.GetString(10), out var sc) ? sc : DateTime.UtcNow,
                TotalJobsCompleted = !r.IsDBNull(11) ? r.GetInt32(11) : 0
            });
        }
        return list;
    }

    public List<Driver> GetAvailableDrivers(VehicleType? vehicleFilter = null)
    {
        return GetAllDrivers()
            .Where(d => d.Status == DriverStatus.Online)
            .Where(d => vehicleFilter == null || d.Vehicle == vehicleFilter)
            .ToList();
    }

    // ── Jobs ──

    public void InsertJob(Job j)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (id, pickup, dropoff, pickup_lat, pickup_lng, dropoff_lat, dropoff_lng,
                passengers, vehicle_required, special_requirements, estimated_fare, caller_phone, caller_name, booking_ref,
                status, allocated_driver_id, created_at, allocated_at, completed_at, driver_distance_km, driver_eta_minutes)
            VALUES ($id, $pu, $do, $plat, $plng, $dlat, $dlng,
                $pax, $vr, $sr, $fare, $phone, $name, $ref,
                $status, $did, $cat, $aat, $coat, $dkm, $eta)
        """;
        cmd.Parameters.AddWithValue("$id", j.Id);
        cmd.Parameters.AddWithValue("$pu", j.Pickup);
        cmd.Parameters.AddWithValue("$do", j.Dropoff);
        cmd.Parameters.AddWithValue("$plat", j.PickupLat);
        cmd.Parameters.AddWithValue("$plng", j.PickupLng);
        cmd.Parameters.AddWithValue("$dlat", j.DropoffLat);
        cmd.Parameters.AddWithValue("$dlng", j.DropoffLng);
        cmd.Parameters.AddWithValue("$pax", j.Passengers);
        cmd.Parameters.AddWithValue("$vr", j.VehicleRequired.ToString());
        cmd.Parameters.AddWithValue("$sr", (object?)j.SpecialRequirements ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fare", (object?)j.EstimatedFare ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$phone", (object?)j.CallerPhone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)j.CallerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ref", (object?)j.BookingRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", j.Status.ToString());
        cmd.Parameters.AddWithValue("$did", (object?)j.AllocatedDriverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cat", j.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$aat", (object?)j.AllocatedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coat", (object?)j.CompletedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dkm", (object?)j.DriverDistanceKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eta", (object?)j.DriverEtaMinutes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdateJobStatus(string jobId, JobStatus status, string? driverId = null, double? distKm = null, int? etaMins = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs SET status = $status, allocated_driver_id = COALESCE($did, allocated_driver_id),
                allocated_at = CASE WHEN $status = 'Allocated' THEN $now ELSE allocated_at END,
                completed_at = CASE WHEN $status IN ('Completed','Cancelled') THEN $now ELSE completed_at END,
                driver_distance_km = COALESCE($dkm, driver_distance_km),
                driver_eta_minutes = COALESCE($eta, driver_eta_minutes)
            WHERE id = $id
        """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$did", (object?)driverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$dkm", (object?)distKm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eta", (object?)etaMins ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<Job> GetPendingJobs()
    {
        return GetJobsByStatus("Pending");
    }

    public List<Job> GetActiveJobs()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE status NOT IN ('Completed','Cancelled') ORDER BY created_at DESC";
        return ReadJobs(cmd);
    }

    public List<Job> GetAllJobs(int limit = 200)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM jobs ORDER BY created_at DESC LIMIT {limit}";
        return ReadJobs(cmd);
    }

    public List<Job> GetJobHistory(DateTime? from = null, DateTime? to = null, int limit = 500)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE status IN ('Completed','Cancelled')";
        if (from.HasValue)
        {
            cmd.CommandText += " AND created_at >= $from";
            cmd.Parameters.AddWithValue("$from", from.Value.ToString("o"));
        }
        if (to.HasValue)
        {
            cmd.CommandText += " AND created_at <= $to";
            cmd.Parameters.AddWithValue("$to", to.Value.ToString("o"));
        }
        cmd.CommandText += $" ORDER BY created_at DESC LIMIT {limit}";
        return ReadJobs(cmd);
    }

    public (int totalToday, int completedToday, int cancelledToday, double avgWaitMins) GetTodayStats()
    {
        var todayStart = DateTime.UtcNow.Date.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN status = 'Completed' THEN 1 ELSE 0 END) as completed,
                SUM(CASE WHEN status = 'Cancelled' THEN 1 ELSE 0 END) as cancelled
            FROM jobs WHERE created_at >= $today
        """;
        cmd.Parameters.AddWithValue("$today", todayStart);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            var total = r.IsDBNull(0) ? 0 : r.GetInt32(0);
            var completed = r.IsDBNull(1) ? 0 : r.GetInt32(1);
            var cancelled = r.IsDBNull(2) ? 0 : r.GetInt32(2);
            return (total, completed, cancelled, 0);
        }
        return (0, 0, 0, 0);
    }

    private List<Job> GetJobsByStatus(string status)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE status = $s ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("$s", status);
        return ReadJobs(cmd);
    }

    private List<Job> ReadJobs(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Job>();
        while (r.Read())
        {
            list.Add(new Job
            {
                Id = r.GetString(0),
                Pickup = r.GetString(1),
                Dropoff = r.GetString(2),
                PickupLat = r.GetDouble(3),
                PickupLng = r.GetDouble(4),
                DropoffLat = r.GetDouble(5),
                DropoffLng = r.GetDouble(6),
                Passengers = r.GetInt32(7),
                VehicleRequired = Enum.TryParse<VehicleType>(r.GetString(8), out var vr) ? vr : VehicleType.Saloon,
                SpecialRequirements = r.IsDBNull(9) ? null : r.GetString(9),
                EstimatedFare = r.IsDBNull(10) ? null : (decimal)r.GetDouble(10),
                CallerPhone = r.IsDBNull(11) ? null : r.GetString(11),
                CallerName = r.IsDBNull(12) ? null : r.GetString(12),
                BookingRef = r.IsDBNull(13) ? null : r.GetString(13),
                Status = Enum.TryParse<JobStatus>(r.GetString(14), out var js) ? js : JobStatus.Pending,
                AllocatedDriverId = r.IsDBNull(15) ? null : r.GetString(15),
                CreatedAt = DateTime.TryParse(r.GetString(16), out var ca) ? ca : DateTime.UtcNow,
                AllocatedAt = !r.IsDBNull(17) && DateTime.TryParse(r.GetString(17), out var aa) ? aa : null,
                CompletedAt = !r.IsDBNull(18) && DateTime.TryParse(r.GetString(18), out var co) ? co : null,
                DriverDistanceKm = r.IsDBNull(19) ? null : r.GetDouble(19),
                DriverEtaMinutes = r.IsDBNull(20) ? null : r.GetInt32(20)
            });
        }
        return list;
    }

    /// <summary>Count completed jobs for a specific driver.</summary>
    public int GetCompletedJobCountForDriver(string driverId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jobs WHERE allocated_driver_id = $did AND status = 'Completed'";
        cmd.Parameters.AddWithValue("$did", driverId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    /// <summary>Increment the total_jobs_completed counter for a driver.</summary>
    public void IncrementDriverJobCount(string driverId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE drivers SET total_jobs_completed = total_jobs_completed + 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", driverId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn?.Dispose();
}
