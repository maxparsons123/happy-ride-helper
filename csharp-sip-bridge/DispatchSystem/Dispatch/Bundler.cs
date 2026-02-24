using DispatchSystem.Data;

namespace DispatchSystem.Dispatch;

/// <summary>
/// Job bundling / chaining: finds candidate follow-on jobs near a primary job's dropoff.
/// </summary>
public static class Bundler
{
    /// <summary>
    /// Find pending/bidding jobs whose pickup is within radiusKm of the primary job's dropoff.
    /// </summary>
    public static List<Job> FindConnectors(Job primary, IEnumerable<Job> available, double radiusKm = 3.0)
    {
        return available
            .Where(j => j.Id != primary.Id)
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Bidding)
            .Select(j => new { job = j, km = AutoDispatcher.HaversineKm(primary.DropoffLat, primary.DropoffLng, j.PickupLat, j.PickupLng) })
            .Where(x => x.km <= radiusKm)
            .OrderBy(x => x.km)
            .Select(x => x.job)
            .Take(5)
            .ToList();
    }

    /// <summary>
    /// Utility of chaining: 1.0 = perfect (0km dead miles), 0.0 = max radius.
    /// </summary>
    public static double BundleUtility(Job primary, Job next)
    {
        var deadKm = AutoDispatcher.HaversineKm(primary.DropoffLat, primary.DropoffLng, next.PickupLat, next.PickupLng);
        return 1.0 - Math.Min(1.0, deadKm / 5.0);
    }
}
