namespace AdaCleanVersion.Models;

/// <summary>
/// Data loaded from the bookings table for a returning caller with an active/confirmed booking.
/// Used to offer cancel/amend/status options before starting a new booking.
/// </summary>
public sealed class ActiveBookingInfo
{
    public string BookingId { get; init; } = "";
    public string? Pickup { get; init; }
    public string? Destination { get; init; }
    public int? Passengers { get; init; }
    public string? Fare { get; init; }
    public string? Eta { get; init; }
    public string? Status { get; init; }
    public string? ScheduledFor { get; init; }
    public string? CallerName { get; init; }
    public string? IcabbiJourneyId { get; init; }

    /// <summary>
    /// Build the system injection message describing the active booking for the AI.
    /// </summary>
    public string BuildSystemMessage()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ACTIVE BOOKING] This caller has an existing active booking.");
        sb.AppendLine($"  Booking ID: {BookingId}");
        sb.AppendLine($"  Pickup: {Pickup}");
        sb.AppendLine($"  Destination: {Destination}");
        sb.AppendLine($"  Passengers: {Passengers}");
        sb.AppendLine($"  Fare: {Fare}");
        sb.AppendLine($"  Status: {Status}");
        if (!string.IsNullOrEmpty(ScheduledFor))
            sb.AppendLine($"  Scheduled for: {ScheduledFor}");
        sb.AppendLine();
        sb.AppendLine("The caller can: CANCEL, AMEND (change details), CHECK STATUS, or start a NEW BOOKING.");
        sb.AppendLine("Use sync_booking_data with intent='cancel_booking' for cancel, 'update_field' for amend, or 'provide_info' for new booking.");
        return sb.ToString();
    }
}
