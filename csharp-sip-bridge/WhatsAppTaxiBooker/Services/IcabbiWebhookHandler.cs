using System.Text.Json;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Handles incoming iCabbi webhook events and sends WhatsApp notifications.
/// Listens on the same webhook port as the WhatsApp listener, under /icabbi path.
/// </summary>
public sealed class IcabbiWebhookHandler
{
    private readonly WhatsAppService _whatsApp;

    public event Action<string>? OnLog;
    public event Action<IcabbiEvent>? OnEvent;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public IcabbiWebhookHandler(WhatsAppService whatsApp)
    {
        _whatsApp = whatsApp;
    }

    /// <summary>
    /// Process a raw iCabbi webhook JSON payload.
    /// </summary>
    public async Task HandleEventAsync(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            var evt = new IcabbiEvent
            {
                EventType = root.TryGetProperty("event_type", out var et) ? et.GetString() ?? "" : "",
                BookingId = root.TryGetProperty("booking_id", out var bi) ? bi.GetString() : null,
                TripId = root.TryGetProperty("trip_id", out var ti) ? ti.GetString() : null,
                CustomerPhone = root.TryGetProperty("customer_phone", out var cp) ? cp.GetString() ?? "" : "",
                CustomerName = root.TryGetProperty("customer_name", out var cn) ? cn.GetString() : null,
                DriverFirstName = root.TryGetProperty("driver_first_name", out var df) ? df.GetString() : null,
                DriverLastName = root.TryGetProperty("driver_last_name", out var dl) ? dl.GetString() : null,
                DriverId = root.TryGetProperty("driver_id", out var di) ? di.GetString() : null,
                VehicleRegistration = root.TryGetProperty("vehicle_registration", out var vr) ? vr.GetString() : null,
                PickupAddress = root.TryGetProperty("pickup_address", out var pa) ? pa.GetString() : null,
                DropAddress = root.TryGetProperty("drop_address", out var da) ? da.GetString() : null,
                DriverLat = root.TryGetProperty("driver_lat", out var dlat) && dlat.ValueKind == JsonValueKind.Number ? dlat.GetDouble() : null,
                DriverLng = root.TryGetProperty("driver_lng", out var dlng) && dlng.ValueKind == JsonValueKind.Number ? dlng.GetDouble() : null,
                PickupLat = root.TryGetProperty("pickup_lat", out var plat) && plat.ValueKind == JsonValueKind.Number ? plat.GetDouble() : null,
                PickupLng = root.TryGetProperty("pickup_lng", out var plng) && plng.ValueKind == JsonValueKind.Number ? plng.GetDouble() : null,
            };

            Log($"📡 [iCabbi] Event: {evt.EventType} for {evt.CustomerPhone}");
            OnEvent?.Invoke(evt);

            switch (evt.EventType?.ToUpper())
            {
                case "ALLOCATED":
                    await HandleAllocated(evt);
                    break;
                case "ENROUTE":
                    await HandleEnroute(evt);
                    break;
                case "ARRIVED":
                    await HandleArrived(evt);
                    break;
                case "START":
                    await HandleTripStart(evt);
                    break;
                case "DROPPINGOFF":
                case "COMPLETED":
                    await HandleCompleted(evt);
                    break;
                case "CANCELLED":
                case "DRIVER_CANCELLED":
                case "DISPATCH_CANCELLED":
                    await HandleCancelled(evt);
                    break;
                default:
                    Log($"ℹ️ [iCabbi] Unhandled event: {evt.EventType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"❌ [iCabbi] Parse error: {ex.Message}");
        }
    }

    private async Task HandleAllocated(IcabbiEvent evt)
    {
        var msg = $"🎉 *Driver Allocated!*\n\n" +
                  $"👨‍✈️ *{evt.DriverFirstName} {evt.DriverLastName}*\n" +
                  $"🚗 Reg: {evt.VehicleRegistration}\n" +
                  $"📘 Booking: {evt.BookingId}\n" +
                  $"📍 Pickup: {evt.PickupAddress}";

        await _whatsApp.SendTextAsync(evt.CustomerPhone, msg);
    }

    private async Task HandleEnroute(IcabbiEvent evt)
    {
        var msg = $"🚕 *Your driver is on the way!*\n\n" +
                  $"👨‍✈️ {evt.DriverFirstName} {evt.DriverLastName}\n" +
                  $"🚗 {evt.VehicleRegistration}\n" +
                  $"📍 Pickup: {evt.PickupAddress}";

        await _whatsApp.SendTextAsync(evt.CustomerPhone, msg);
    }

    private async Task HandleArrived(IcabbiEvent evt)
    {
        var msg = $"🚕 *Your driver has arrived!*\n\n" +
                  $"📍 Pickup: {evt.PickupAddress}\n" +
                  $"🎯 Destination: {evt.DropAddress}\n" +
                  $"👨‍✈️ {evt.DriverFirstName} {evt.DriverLastName}\n" +
                  $"🚗 {evt.VehicleRegistration}";

        await _whatsApp.SendTextAsync(evt.CustomerPhone, msg);
    }

    private async Task HandleTripStart(IcabbiEvent evt)
    {
        var msg = $"🚀 *Your trip has started!*\n\n" +
                  $"📍 Pickup: {evt.PickupAddress}\n" +
                  $"🎯 Destination: {evt.DropAddress}\n" +
                  $"🚕 {evt.DriverFirstName} {evt.DriverLastName}\n" +
                  $"🚗 {evt.VehicleRegistration}";

        await _whatsApp.SendTextAsync(evt.CustomerPhone, msg);
    }

    private async Task HandleCompleted(IcabbiEvent evt)
    {
        var msg = $"🎉 *Your trip is complete!*\n\n" +
                  $"Thank you for riding with us. 🚕\n" +
                  $"Booking: {evt.BookingId}";

        await _whatsApp.SendTextAsync(evt.CustomerPhone, msg);
    }

    private async Task HandleCancelled(IcabbiEvent evt)
    {
        var msg = $"❌ *Your booking has been cancelled.*\n\n" +
                  $"Booking: {evt.BookingId}\n" +
                  $"Send a new message anytime to book again!";

        await _whatsApp.SendTextAsync(evt.CustomerPhone, msg);
    }
}

/// <summary>
/// Parsed iCabbi webhook event.
/// </summary>
public sealed class IcabbiEvent
{
    public string EventType { get; set; } = "";
    public string? BookingId { get; set; }
    public string? TripId { get; set; }
    public string CustomerPhone { get; set; } = "";
    public string? CustomerName { get; set; }
    public string? DriverFirstName { get; set; }
    public string? DriverLastName { get; set; }
    public string? DriverId { get; set; }
    public string? VehicleRegistration { get; set; }
    public string? PickupAddress { get; set; }
    public string? DropAddress { get; set; }
    public double? DriverLat { get; set; }
    public double? DriverLng { get; set; }
    public double? PickupLat { get; set; }
    public double? PickupLng { get; set; }
}
