using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using DispatchSystem.Data;

namespace DispatchSystem.Mqtt;

/// <summary>
/// MQTT client for dispatch: receives driver GPS, bookings, and publishes job allocations.
/// Compatible with the existing MqttTaxiClient topic structure.
/// </summary>
public sealed class MqttDispatchClient : IDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;

    public event Action<string>? OnLog;
    public event Action<string, double, double, string?>? OnDriverGps;      // driverId, lat, lng, status
    public event Action<Job>? OnBookingReceived;
    public event Action<string, string, string>? OnJobStatusUpdate;          // jobId, driverId, status
    public event Action<string, string, bool>? OnDriverJobResponse;          // jobId, driverId, accepted
    public event Action<string, string, double, double>? OnDriverBidReceived; // jobId, driverId, lat, lng

    public bool IsConnected => _client?.IsConnected ?? false;

    public MqttDispatchClient(string brokerUrl = "wss://broker.hivemq.com:8884/mqtt")
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _options = new MqttClientOptionsBuilder()
            .WithClientId("dispatch-" + Guid.NewGuid().ToString("N")[..8])
            .WithWebSocketServer(o => o.WithUri(brokerUrl))
            .WithCleanSession()
            .Build();

        _client.ApplicationMessageReceivedAsync += HandleMessage;

        _client.ConnectedAsync += async _ =>
        {
            OnLog?.Invoke("✅ MQTT connected");
            await Subscribe();
        };

        _client.DisconnectedAsync += async _ =>
        {
            OnLog?.Invoke("⚠ MQTT disconnected — retrying in 3s");
            await Task.Delay(3000);
            try { await _client.ConnectAsync(_options); }
            catch (Exception ex) { OnLog?.Invoke($"❌ Reconnect failed: {ex.Message}"); }
        };
    }

    public async Task ConnectAsync()
    {
        OnLog?.Invoke("🔌 Connecting to MQTT...");
        await _client.ConnectAsync(_options);
    }

    private async Task Subscribe()
    {
        await _client.SubscribeAsync("drivers/+/location");
        await _client.SubscribeAsync("taxi/bookings");
        await _client.SubscribeAsync("jobs/+/status");
        await _client.SubscribeAsync("jobs/+/bidding");
        await _client.SubscribeAsync("jobs/+/response");
        await _client.SubscribeAsync("jobs/+/bid");
        OnLog?.Invoke("📡 Subscribed to dispatch topics");
    }

    private Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var json = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

            if (topic.StartsWith("drivers/") && topic.EndsWith("/location"))
            {
                var driverId = topic.Split('/')[1];
                var loc = JsonSerializer.Deserialize<DriverLocationMsg>(json);
                if (loc != null)
                    OnDriverGps?.Invoke(driverId, loc.lat, loc.lng, loc.status);
            }
            else if (topic == "taxi/bookings")
            {
                OnLog?.Invoke($"📥 MQTT booking received: {json[..Math.Min(json.Length, 120)]}");
                var booking = JsonSerializer.Deserialize<BookingMsg>(json);
                if (booking != null)
                {
                    var job = new Job
                    {
                        Pickup = booking.pickup ?? "",
                        Dropoff = booking.dropoff ?? "",
                        Passengers = booking.passengers > 0 ? booking.passengers : 1,
                        VehicleRequired = Enum.TryParse<VehicleType>(booking.vehicleType, true, out var vt) ? vt : VehicleType.Saloon,
                        SpecialRequirements = booking.specialRequirements,
                        EstimatedFare = booking.estimatedPrice,
                        PickupLat = booking.pickupLat,
                        PickupLng = booking.pickupLng,
                        DropoffLat = booking.dropoffLat,
                        DropoffLng = booking.dropoffLng,
                        CallerPhone = booking.callerPhone ?? "",
                        CallerName = booking.callerName ?? "Customer",
                    };
                    OnLog?.Invoke($"✅ Booking parsed: {job.Pickup} → {job.Dropoff}, {job.Passengers} pax");
                    OnBookingReceived?.Invoke(job);
                }
                else
                {
                    OnLog?.Invoke("⚠ Failed to deserialize booking JSON");
                }
            }
            else if (topic.StartsWith("jobs/") && (topic.EndsWith("/status") || topic.EndsWith("/bidding")))
            {
                var parts = topic.Split('/');
                var jobId = parts[1];
                var statusMsg = JsonSerializer.Deserialize<JobStatusMsg>(json);
                if (statusMsg != null)
                    OnJobStatusUpdate?.Invoke(jobId, statusMsg.driver ?? "", statusMsg.status ?? "");
            }
            else if (topic.StartsWith("jobs/") && topic.EndsWith("/response"))
            {
                var parts = topic.Split('/');
                var jobId = parts[1];
                var resp = JsonSerializer.Deserialize<DriverResponseMsg>(json);
                if (resp != null)
                    OnDriverJobResponse?.Invoke(jobId, resp.driver ?? "", resp.accepted);
            }
            else if (topic.StartsWith("jobs/") && topic.EndsWith("/bid"))
            {
                var parts = topic.Split('/');
                var jobId = parts[1];
                var bid = JsonSerializer.Deserialize<DriverBidMsg>(json);
                if (bid != null)
                    OnDriverBidReceived?.Invoke(jobId, bid.driver ?? "", bid.lat, bid.lng);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"❌ MQTT parse error: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public async Task PublishJobAllocation(string jobId, string driverId, Job job)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jobId,
            driverId,
            pickup = job.Pickup,
            dropoff = job.Dropoff,
            pickupLat = job.PickupLat,
            pickupLng = job.PickupLng,
            passengers = job.Passengers,
            fare = job.EstimatedFare
        });

        await PublishAsync($"drivers/{driverId}/jobs", payload);
        await PublishAsync($"jobs/{jobId}/allocated", payload);
        OnLog?.Invoke($"📤 Job {jobId} dispatched to driver {driverId}");
    }

    /// <summary>Publish bid request to specific drivers for a job.</summary>
    public async Task PublishBidRequest(Job job, List<string> driverIds)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            pickup = job.Pickup,
            dropoff = job.Dropoff,
            pickupLat = job.PickupLat,
            pickupLng = job.PickupLng,
            passengers = job.Passengers,
            fare = job.EstimatedFare,
            biddingWindowSec = 20
        });

        foreach (var driverId in driverIds)
            await PublishAsync($"drivers/{driverId}/bid-request", payload);

        await PublishAsync($"jobs/{job.Id}/bid-request", payload);
        OnLog?.Invoke($"📤 Bid request for {job.Id} sent to {driverIds.Count} drivers");
    }

    /// <summary>Publish bid result (winner/loser) to drivers.</summary>
    public async Task PublishBidResult(string jobId, string driverId, string result)
    {
        var payload = JsonSerializer.Serialize(new
        {
            job = jobId,
            driver = driverId,
            result,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await PublishAsync($"jobs/{jobId}/result/{driverId}", payload);
        await PublishAsync($"jobs/{jobId}/status", payload);
    }

    public async Task PublishAsync(string topic, string payload)
    {
        if (!_client.IsConnected) return;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(msg);
    }

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync();
    }

    public void Dispose() => _client?.Dispose();

    // ── Message DTOs ──

    private class DriverLocationMsg
    {
        public double lat { get; set; }
        public double lng { get; set; }
        public string? status { get; set; }
    }

    private class BookingMsg
    {
        public string? pickup { get; set; }
        public string? dropoff { get; set; }
        public int passengers { get; set; }
        public string? vehicleType { get; set; }
        public string? specialRequirements { get; set; }
        public decimal? estimatedPrice { get; set; }
        public double pickupLat { get; set; }
        public double pickupLng { get; set; }
        public double dropoffLat { get; set; }
        public double dropoffLng { get; set; }
        public string? callerPhone { get; set; }
        public string? callerName { get; set; }
        public string? bookingRef { get; set; }
    }

    private class JobStatusMsg
    {
        public string? driver { get; set; }
        public string? status { get; set; }
    }

    private class DriverResponseMsg
    {
        public string? driver { get; set; }
        public bool accepted { get; set; }
    }

    private class DriverBidMsg
    {
        public string? driver { get; set; }
        public double lat { get; set; }
        public double lng { get; set; }
    }
}
