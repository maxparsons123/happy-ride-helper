namespace DispatchSystem.Data;

public enum DriverStatus
{
    Online,
    Break,
    Offline,
    OnJob
}

public enum VehicleType
{
    Saloon,
    Estate,
    MPV,
    Executive,
    Minibus
}

public enum JobStatus
{
    Pending,
    Bidding,
    Allocated,
    Accepted,
    PickedUp,
    Completed,
    Cancelled,
    NoDriver
}

public class Driver
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Registration { get; set; } = "";
    public VehicleType Vehicle { get; set; } = VehicleType.Saloon;
    public DriverStatus Status { get; set; } = DriverStatus.Offline;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTime LastGpsUpdate { get; set; }
    public DateTime? LastJobCompletedAt { get; set; }
    public DateTime StatusChangedAt { get; set; } = DateTime.UtcNow;
    public int TotalJobsCompleted { get; set; }
}

public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Pickup { get; set; } = "";
    public string Dropoff { get; set; } = "";
    public double PickupLat { get; set; }
    public double PickupLng { get; set; }
    public double DropoffLat { get; set; }
    public double DropoffLng { get; set; }
    public int Passengers { get; set; } = 1;
    public VehicleType VehicleRequired { get; set; } = VehicleType.Saloon;
    public string? SpecialRequirements { get; set; }
    public decimal? EstimatedFare { get; set; }
    public string? CallerPhone { get; set; }
    public string? CallerName { get; set; }
    public string? BookingRef { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string? AllocatedDriverId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AllocatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? DriverDistanceKm { get; set; }
    public int? DriverEtaMinutes { get; set; }
}
