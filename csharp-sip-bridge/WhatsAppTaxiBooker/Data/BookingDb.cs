using Microsoft.Data.Sqlite;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Data;

/// <summary>
/// SQLite-backed booking storage.
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
                pickup TEXT NOT NULL,
                destination TEXT NOT NULL,
                passengers INTEGER DEFAULT 1,
                notes TEXT,
                pickup_lat REAL,
                pickup_lng REAL,
                dropoff_lat REAL,
                dropoff_lng REAL,
                status TEXT DEFAULT 'pending',
                fare TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                phone TEXT NOT NULL,
                role TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    public void SaveBooking(Booking b)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO bookings 
            (id, phone, caller_name, pickup, destination, passengers, notes, 
             pickup_lat, pickup_lng, dropoff_lat, dropoff_lng, status, fare, created_at)
            VALUES 
            ($id, $phone, $name, $pickup, $dest, $pax, $notes,
             $plat, $plng, $dlat, $dlng, $status, $fare, $created)";
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
        cmd.Parameters.AddWithValue("$created", b.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
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

    public List<Booking> GetRecentBookings(int limit = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM bookings ORDER BY created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var bookings = new List<Booking>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bookings.Add(new Booking
            {
                Id = reader.GetString(0),
                Phone = reader.GetString(1),
                CallerName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Pickup = reader.GetString(3),
                Destination = reader.GetString(4),
                Passengers = reader.GetInt32(5),
                Notes = reader.IsDBNull(6) ? null : reader.GetString(6),
                PickupLat = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                PickupLng = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                DropoffLat = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                DropoffLng = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                Status = reader.GetString(11),
                Fare = reader.IsDBNull(12) ? null : reader.GetString(12),
                CreatedAt = DateTime.Parse(reader.GetString(13))
            });
        }
        return bookings;
    }

    public void Dispose() => _conn.Dispose();
}
