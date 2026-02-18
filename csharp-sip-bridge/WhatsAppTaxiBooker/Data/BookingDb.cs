using Microsoft.Data.Sqlite;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Data;

/// <summary>
/// SQLite-backed booking storage, indexed by caller phone number.
/// </summary>
public sealed class BookingDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public BookingDb(string dbPath = "bookings.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS bookings (
                id TEXT PRIMARY KEY,
                phone TEXT NOT NULL,
                caller_name TEXT,
                pickup TEXT NOT NULL DEFAULT '',
                destination TEXT NOT NULL DEFAULT '',
                passengers INTEGER DEFAULT 1,
                notes TEXT,
                pickup_lat REAL,
                pickup_lng REAL,
                dropoff_lat REAL,
                dropoff_lng REAL,
                status TEXT DEFAULT 'collecting',
                fare TEXT,
                pickup_time TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_bookings_phone ON bookings(phone);
            CREATE INDEX IF NOT EXISTS idx_bookings_status ON bookings(status);

            CREATE TABLE IF NOT EXISTS conversations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                phone TEXT NOT NULL,
                role TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_conversations_phone ON conversations(phone);";
        cmd.ExecuteNonQuery();
    }

    public void SaveBooking(Booking b)
    {
        b.UpdatedAt = DateTime.UtcNow;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO bookings 
            (id, phone, caller_name, pickup, destination, passengers, notes, 
             pickup_lat, pickup_lng, dropoff_lat, dropoff_lng, status, fare, pickup_time, created_at, updated_at)
            VALUES 
            ($id, $phone, $name, $pickup, $dest, $pax, $notes,
             $plat, $plng, $dlat, $dlng, $status, $fare, $ptime, $created, $updated)";
        cmd.Parameters.AddWithValue("$id", b.Id);
        cmd.Parameters.AddWithValue("$phone", b.Phone);
        cmd.Parameters.AddWithValue("$name", (object?)b.CallerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pickup", b.Pickup);
        cmd.Parameters.AddWithValue("$dest", b.Destination);
        cmd.Parameters.AddWithValue("$pax", b.Passengers);
        cmd.Parameters.AddWithValue("$notes", (object?)b.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plat", (object?)b.PickupLat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plng", (object?)b.PickupLng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dlat", (object?)b.DropoffLat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dlng", (object?)b.DropoffLng ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", b.Status);
        cmd.Parameters.AddWithValue("$fare", (object?)b.Fare ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ptime", (object?)b.PickupTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", b.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", b.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get the latest active (non-completed/cancelled) booking for a phone number.
    /// </summary>
    public Booking? GetActiveBookingByPhone(string phone)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT * FROM bookings 
                            WHERE phone = $phone AND status IN ('collecting', 'ready', 'pending')
                            ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$phone", phone);
        return ReadSingleBooking(cmd);
    }

    /// <summary>
    /// Get the most recent booking for a phone number regardless of status.
    /// </summary>
    public Booking? GetLatestBookingByPhone(string phone)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM bookings WHERE phone = $phone ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$phone", phone);
        return ReadSingleBooking(cmd);
    }

    public void SaveMessage(string phone, string role, string message)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO conversations (phone, role, message, created_at)
                            VALUES ($phone, $role, $msg, $created)";
        cmd.Parameters.AddWithValue("$phone", phone);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<(string role, string message)> GetConversation(string phone, int limit = 10)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT role, message FROM conversations 
                            WHERE phone = $phone ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$phone", phone);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1)));
        results.Reverse();
        return results;
    }

    public List<Booking> GetRecentBookings(int limit = 100)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM bookings ORDER BY updated_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadBookings(cmd);
    }

    private Booking? ReadSingleBooking(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return MapBooking(reader);
    }

    private List<Booking> ReadBookings(SqliteCommand cmd)
    {
        var bookings = new List<Booking>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            bookings.Add(MapBooking(reader));
        return bookings;
    }

    private static Booking MapBooking(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Phone = r.GetString(r.GetOrdinal("phone")),
        CallerName = r.IsDBNull(r.GetOrdinal("caller_name")) ? null : r.GetString(r.GetOrdinal("caller_name")),
        Pickup = r.GetString(r.GetOrdinal("pickup")),
        Destination = r.GetString(r.GetOrdinal("destination")),
        Passengers = r.GetInt32(r.GetOrdinal("passengers")),
        Notes = r.IsDBNull(r.GetOrdinal("notes")) ? null : r.GetString(r.GetOrdinal("notes")),
        PickupLat = r.IsDBNull(r.GetOrdinal("pickup_lat")) ? null : r.GetDouble(r.GetOrdinal("pickup_lat")),
        PickupLng = r.IsDBNull(r.GetOrdinal("pickup_lng")) ? null : r.GetDouble(r.GetOrdinal("pickup_lng")),
        DropoffLat = r.IsDBNull(r.GetOrdinal("dropoff_lat")) ? null : r.GetDouble(r.GetOrdinal("dropoff_lat")),
        DropoffLng = r.IsDBNull(r.GetOrdinal("dropoff_lng")) ? null : r.GetDouble(r.GetOrdinal("dropoff_lng")),
        Status = r.GetString(r.GetOrdinal("status")),
        Fare = r.IsDBNull(r.GetOrdinal("fare")) ? null : r.GetString(r.GetOrdinal("fare")),
        PickupTime = r.IsDBNull(r.GetOrdinal("pickup_time")) ? null : r.GetString(r.GetOrdinal("pickup_time")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("updated_at")))
    };

    public void Dispose() => _conn.Dispose();
}
