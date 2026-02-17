using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using DispatchSystem.Data;

namespace DispatchSystem.Mqtt;

/// <summary>
/// MQTT client for dispatch: receives driver GPS, bookings, and publishes job allocations.
/// Enhanced with driver app v11.9.2+ compatibility, coordinate validation, and geocoding fallback.
/// </summary>
public sealed class MqttDispatchClient : IDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly Geocoder _geocoder;
    private readonly string _dispatcherId;

    public event Action<string>? OnLog;
    public event Action<string, double, double, string?>? OnDriverGps;      // driverId, lat, lng, status
    public event Action<Job>? OnBookingReceived;
    public event Action<string, string, string>? OnJobStatusUpdate;          // jobId, driverId, status
    public event Action<string, string, bool>? OnDriverJobResponse;          // jobId, driverId, accepted
    public event Action<string, string, double, double>? OnDriverBidReceived; // jobId, driverId, lat, lng

    public bool IsConnected => _client?.IsConnected ?? false;

    public MqttDispatchClient(string dispatcherId = "dispatch-default", string brokerUrl = "wss://broker.hivemq.com:8884/mqtt")
    {
        _dispatcherId = dispatcherId;
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _options = new MqttClientOptionsBuilder()
            .WithClientId($"dispatch-{dispatcherId}-{Guid.NewGuid().ToString("N")[..8]}")
            .WithWebSocketServer(o => o.WithUri(brokerUrl))
            .WithCleanSession()
            .Build();

        // Initialize HTTP client for geocoding with proper Nominatim compliance
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            DefaultRequestHeaders = { { "User-Agent", $"BlackCabUniteDispatch/1.0 ({dispatcherId})" } }
        };

        // Initialize geocoder
        _geocoder = new Geocoder(_httpClient, OnLog);

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

    public async Task Subscribe()
    {
        await _client.SubscribeAsync("drivers/+/location");
        await _client.SubscribeAsync("drivers/+/status");
        await _client.SubscribeAsync("taxi/bookings");
        await _client.SubscribeAsync("pubs/requests/+");  // Receive pub app job submissions
        await _client.SubscribeAsync("jobs/+/status");
        await _client.SubscribeAsync("jobs/+/bidding");
        await _client.SubscribeAsync("jobs/+/response");
        await _client.SubscribeAsync("jobs/+/bid");
        OnLog?.Invoke("📡 Subscribed to dispatch topics (incl. pubs/requests/+)");
    }

    private Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var json = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

            // Log ALL incoming MQTT messages for debugging
            OnLog?.Invoke($"📨 MQTT [{topic}]: {json[..Math.Min(json.Length, 200)]}");

            if (topic.StartsWith("drivers/") && topic.EndsWith("/location"))
            {
                var driverId = topic.Split('/')[1];
                var loc = JsonSerializer.Deserialize<DriverLocationMsg>(json);
                if (loc != null)
                    OnDriverGps?.Invoke(driverId, loc.lat, loc.lng, loc.status);
            }
            else if (topic.StartsWith("drivers/") && topic.EndsWith("/status"))
            {
                var driverId = topic.Split('/')[1];
                var statusMsg = JsonSerializer.Deserialize<DriverStatusMsg>(json);
                if (statusMsg != null)
                {
                    OnLog?.Invoke($"🔄 Driver {driverId} status → {statusMsg.status}");
                    OnDriverGps?.Invoke(driverId, statusMsg.lat, statusMsg.lng, statusMsg.status);
                }
            }
            else if (topic == "taxi/bookings" || topic.StartsWith("pubs/requests/"))
            {
                OnLog?.Invoke($"📥 MQTT booking received [{topic}]: {json[..Math.Min(json.Length, 200)]}");

                var booking = JsonSerializer.Deserialize<BookingMsg>(json);
                if (booking == null)
                {
                    OnLog?.Invoke("⚠ Failed to deserialize booking JSON");
                }
                else
                {
                    // Dispatcher sends "pickupAddress"; fallback to "pickup"
                    var pickupText = booking.pickup ?? booking.pickupAddress ?? "";
                    var dropoffText = booking.dropoff ?? booking.destination ?? "";

                    // Pub app sends pickup coords as "lat"/"lng"
                    var pLat = booking.pickupLat != 0 ? booking.pickupLat : booking.lat;
                    var pLng = booking.pickupLng != 0 ? booking.pickupLng : booking.lng;

                    // Pub app sends dropoff coords as "destinationLat"/"destinationLng"
                    var dLat = booking.dropoffLat != 0 ? booking.dropoffLat : booking.destinationLat;
                    var dLng = booking.dropoffLng != 0 ? booking.dropoffLng : booking.destinationLng;

                    // Pub app sends fare as string "£12.50", dispatch uses decimal
                    decimal? fare = booking.estimatedPrice;
                    if (fare == null && !string.IsNullOrEmpty(booking.fare))
                    {
                        var cleaned = booking.fare.Replace("£", "").Replace("$", "").Trim();
                        if (decimal.TryParse(cleaned, NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var parsed))
                            fare = parsed;
                    }

                    // Parse passengers: can be int OR descriptive string like "2 adults, 1 child with wheelchair"
                    int paxCount = 1;
                    string? paxDetails = null;
                    if (booking.passengers.HasValue)
                    {
                        var paxEl = booking.passengers.Value;
                        if (paxEl.ValueKind == JsonValueKind.Number)
                        {
                            paxCount = paxEl.GetInt32();
                        }
                        else if (paxEl.ValueKind == JsonValueKind.String)
                        {
                            var paxStr = paxEl.GetString() ?? "";
                            if (int.TryParse(paxStr, out var parsedPax))
                                paxCount = parsedPax;
                            else
                            {
                                var match = Regex.Match(paxStr, @"(\d+)");
                                if (match.Success) paxCount = int.Parse(match.Groups[1].Value);
                                paxDetails = paxStr;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(booking.passengersText))
                    {
                        paxDetails = booking.passengersText;
                        var match = Regex.Match(booking.passengersText, @"^(\d+)");
                        if (match.Success)
                            paxCount = int.Parse(match.Groups[1].Value);
                    }

                    // Parse temp expansion fields (key:value format)
                    string? priority = null, vehicleOverride = null, paymentMethod = null;
                    if (!string.IsNullOrEmpty(booking.temp1))
                    {
                        var val = booking.temp1.Contains(':') ? booking.temp1.Split(':')[1].Trim() : booking.temp1;
                        priority = val;
                    }
                    if (!string.IsNullOrEmpty(booking.temp2))
                    {
                        var val = booking.temp2.Contains(':') ? booking.temp2.Split(':')[1].Trim() : booking.temp2;
                        vehicleOverride = val;
                    }
                    if (!string.IsNullOrEmpty(booking.temp3))
                    {
                        var val = booking.temp3.Contains(':') ? booking.temp3.Split(':')[1].Trim() : booking.temp3;
                        paymentMethod = val;
                    }

                    // Vehicle type: prefer explicit vehicleType, then temp2 override
                    var vehicleType = VehicleType.Saloon;
                    if (Enum.TryParse<VehicleType>(booking.vehicleType, true, out var vt))
                        vehicleType = vt;
                    else if (!string.IsNullOrEmpty(vehicleOverride) && Enum.TryParse<VehicleType>(vehicleOverride, true, out var vt2))
                        vehicleType = vt2;

                    var job = new Job
                    {
                        Id = booking.job ?? booking.bookingRef ?? Guid.NewGuid().ToString("N")[..12],
                        Pickup = pickupText,
                        Dropoff = dropoffText,
                        Passengers = paxCount,
                        PassengerDetails = paxDetails,
                        VehicleRequired = vehicleType,
                        VehicleOverride = vehicleOverride,
                        SpecialRequirements = booking.notes ?? booking.specialRequirements,
                        EstimatedFare = fare,
                        PickupLat = pLat,
                        PickupLng = pLng,
                        DropoffLat = dLat,
                        DropoffLng = dLng,
                        CallerPhone = booking.callerPhone ?? booking.customerPhone ?? "",
                        CallerName = booking.callerName ?? booking.customerName ?? "Customer",
                        Priority = priority,
                        PaymentMethod = paymentMethod,
                        BiddingWindowSec = booking.biddingWindowSec > 0 ? booking.biddingWindowSec : null,
                        CreatedAt = booking.timestamp > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(booking.timestamp).UtcDateTime
                            : DateTime.UtcNow
                    };
                    OnLog?.Invoke($"✅ Booking parsed: {job.Pickup} → {job.Dropoff}, {job.Passengers} pax ({paxDetails ?? "n/a"}), fare={fare?.ToString("F2") ?? "n/a"}, priority={priority ?? "normal"}");
                    OnBookingReceived?.Invoke(job);
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

    /// <summary>
    /// Publish bid request to specific drivers for a job with FULL driver app compatibility.
    /// Fixes invalid coordinates and sends payload in BOTH old and new field formats.
    /// </summary>
    public async Task PublishBidRequest(Job job, List<string> driverIds, CancellationToken cancellationToken = default)
    {
        // Validate and fix coordinates BEFORE publishing
        var fixedJob = await FixInvalidCoordinatesAsync(job, cancellationToken);

        // Create driver-compatible payload (with BOTH old and new field names)
        var payload = CreateDriverCompatiblePayload(fixedJob);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        OnLog?.Invoke($"📤 Bid payload for {job.Id}:\n{json}");

        // Publish to individual drivers (OLD FORMAT - for legacy drivers)
        foreach (var driverId in driverIds)
        {
            await PublishAsync($"drivers/{driverId}/bid-request", json, cancellationToken);
            OnLog?.Invoke($"📤 Bid request sent to driver {driverId} (topic: drivers/{driverId}/bid-request)");
        }

        // Publish to broadcast topic (NEW FORMAT - for v11.9.2+ drivers)
        await PublishAsync($"pubs/requests/{job.Id}", json, cancellationToken);
        OnLog?.Invoke($"✅ Bid request for {job.Id} sent to {driverIds.Count} drivers via pubs/requests/{job.Id}");
    }

    /// <summary>
    /// Publish job allocation to a specific driver with FULL driver app compatibility.
    /// </summary>
    public async Task PublishJobAllocation(string jobId, string driverId, Job job, CancellationToken cancellationToken = default)
    {
        // Validate and fix coordinates BEFORE publishing
        var fixedJob = await FixInvalidCoordinatesAsync(job, cancellationToken);

        // Create driver-compatible payload (with BOTH old and new field names)
        var payload = CreateDriverCompatiblePayload(fixedJob, status: "allocated");
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        OnLog?.Invoke($"📤 Allocation payload for {jobId}:\n{json}");

        // Publish to driver-specific topic (OLD FORMAT)
        await PublishAsync($"drivers/{driverId}/jobs", json, cancellationToken);

        // Publish to job status topics (NEW FORMAT)
        await PublishAsync($"jobs/{jobId}/allocated", json, cancellationToken);
        await PublishAsync($"jobs/{jobId}/status", json, cancellationToken);

        // Publish winner result to driver-specific result topic (NEW FORMAT)
        var wonPayload = CreateDriverCompatiblePayload(fixedJob, status: "allocated", result: "won");
        var wonJson = JsonSerializer.Serialize(wonPayload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
        await PublishAsync($"jobs/{jobId}/result/{driverId}", wonJson, cancellationToken);

        OnLog?.Invoke($"✅ Job {jobId} allocated to driver {driverId} | {fixedJob.Pickup} → {fixedJob.Dropoff}");
    }

    /// <summary>
    /// Publish bid result (winner/loser) to drivers with FULL driver app compatibility.
    /// </summary>
    public async Task PublishBidResult(string jobId, string driverId, string result, Job? job = null, CancellationToken cancellationToken = default)
    {
        if (job == null)
        {
            OnLog?.Invoke($"⚠️ Cannot publish bid result for {jobId} - job is null");
            return;
        }

        // Validate and fix coordinates BEFORE publishing
        var fixedJob = await FixInvalidCoordinatesAsync(job, cancellationToken);

        // Create driver-compatible payload (with BOTH old and new field names)
        var payload = CreateDriverCompatiblePayload(fixedJob, status: result, result: result);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        OnLog?.Invoke($"📤 Bid result payload for {jobId} ({result}):\n{json}");

        // Publish to result topic (NEW FORMAT)
        await PublishAsync($"jobs/{jobId}/result/{driverId}", json, cancellationToken);

        // Publish to status topic (for job history)
        await PublishAsync($"jobs/{jobId}/status", json, cancellationToken);

        OnLog?.Invoke($"✅ Bid result '{result}' published for job {jobId} to driver {driverId}");
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
        {
            OnLog?.Invoke($"⚠️ Cannot publish to {topic} - MQTT client not connected");
            return;
        }

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(msg, cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _httpClient?.Dispose();
    }

    // ── CORE ENHANCEMENTS ──

    /// <summary>
    /// Fixes invalid coordinates (0,0 or outside UK bounds) using geocoding fallback.
    /// Returns a new Job object with fixed coordinates.
    /// </summary>
    private async Task<Job> FixInvalidCoordinatesAsync(Job job, CancellationToken cancellationToken)
    {
        var fixedJob = new Job
        {
            Id = job.Id,
            Pickup = job.Pickup,
            Dropoff = job.Dropoff,
            Passengers = job.Passengers,
            PassengerDetails = job.PassengerDetails,
            VehicleRequired = job.VehicleRequired,
            VehicleOverride = job.VehicleOverride,
            SpecialRequirements = job.SpecialRequirements,
            EstimatedFare = job.EstimatedFare,
            CallerPhone = job.CallerPhone,
            CallerName = job.CallerName,
            Priority = job.Priority,
            PaymentMethod = job.PaymentMethod,
            BiddingWindowSec = job.BiddingWindowSec,
            CreatedAt = job.CreatedAt
        };

        // Fix pickup coordinates if invalid
        if (!IsValidCoordinate(job.PickupLat, job.PickupLng))
        {
            OnLog?.Invoke($"⚠️ Invalid pickup coordinates ({job.PickupLat}, {job.PickupLng}) for job {job.Id}. Attempting geocoding...");

            var coords = await _geocoder.GeocodeAddressAsync(job.Pickup, "pickup", cancellationToken);
            if (coords.HasValue)
            {
                fixedJob.PickupLat = coords.Value.Latitude;
                fixedJob.PickupLng = coords.Value.Longitude;
                OnLog?.Invoke($"✅ Fixed pickup coordinates for {job.Id}: {fixedJob.PickupLat}, {fixedJob.PickupLng}");
            }
            else
            {
                // Fallback to Coventry center
                fixedJob.PickupLat = 52.4068;
                fixedJob.PickupLng = -1.5197;
                OnLog?.Invoke($"⚠️ Could not geocode pickup address '{job.Pickup}'. Using Coventry center coordinates.");
            }
        }
        else
        {
            fixedJob.PickupLat = job.PickupLat;
            fixedJob.PickupLng = job.PickupLng;
        }

        // Fix dropoff coordinates if invalid
        if (!IsValidCoordinate(job.DropoffLat, job.DropoffLng))
        {
            OnLog?.Invoke($"⚠️ Invalid dropoff coordinates ({job.DropoffLat}, {job.DropoffLng}) for job {job.Id}. Attempting geocoding...");

            var coords = await _geocoder.GeocodeAddressAsync(job.Dropoff, "dropoff", cancellationToken);
            if (coords.HasValue)
            {
                fixedJob.DropoffLat = coords.Value.Latitude;
                fixedJob.DropoffLng = coords.Value.Longitude;
                OnLog?.Invoke($"✅ Fixed dropoff coordinates for {job.Id}: {fixedJob.DropoffLat}, {fixedJob.DropoffLng}");
            }
            else
            {
                // Fallback to Birmingham Airport
                fixedJob.DropoffLat = 52.4531;
                fixedJob.DropoffLng = -1.7475;
                OnLog?.Invoke($"⚠️ Could not geocode dropoff address '{job.Dropoff}'. Using Birmingham Airport coordinates.");
            }
        }
        else
        {
            fixedJob.DropoffLat = job.DropoffLat;
            fixedJob.DropoffLng = job.DropoffLng;
        }

        return fixedJob;
    }

    private bool IsValidCoordinate(double lat, double lng)
    {
        // Check for zero or near-zero values (invalid)
        if (Math.Abs(lat) < 0.001 || Math.Abs(lng) < 0.001)
            return false;

        // UK bounding box validation (rough)
        if (lat < 49.5 || lat > 61.0 || lng < -8.5 || lng > 2.0)
            return false;

        return true;
    }

    /// <summary>
    /// Creates a payload compatible with driver app v11.9.2+ AND legacy drivers.
    /// Includes BOTH old field names (jobId, pickupLat) AND new field names (job, lat).
    /// </summary>
    private object CreateDriverCompatiblePayload(Job job, string? status = null, string? result = null)
    {
        var fareStr = job.EstimatedFare?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        var passengersStr = job.PassengerDetails ?? job.Passengers.ToString();
        var windowSec = job.BiddingWindowSec ?? 30;

        // CRITICAL: Send BOTH new and old field names for maximum compatibility
        return new
        {
            // ===== CORE JOB FIELDS (BOTH FORMATS) =====
            job = job.Id,                    // NEW: Standard field (driver app v11.9.2+)
            jobId = job.Id,                  // OLD: Legacy field (your current format)

            // ===== PICKUP COORDINATES (BOTH FORMATS) =====
            lat = Math.Round(job.PickupLat, 6), // NEW: Standard field
            lng = Math.Round(job.PickupLng, 6), // NEW: Standard field
            pickupLat = Math.Round(job.PickupLat, 6), // OLD: Your current field
            pickupLng = Math.Round(job.PickupLng, 6), // OLD: Your current field

            // ===== PICKUP ADDRESS (BOTH FORMATS) =====
            pickupAddress = job.Pickup,      // NEW: Standard field
            pickup = job.Pickup,             // OLD: Your current field
            pubName = job.Pickup,            // LEGACY: Very old field

            // ===== DROPOFF FIELDS (BOTH FORMATS) =====
            dropoff = job.Dropoff,           // NEW: Standard field
            dropoffName = job.Dropoff,       // OLD: Your current field
            dropoffLat = Math.Round(job.DropoffLat, 6), // NEW: Standard field
            dropoffLng = Math.Round(job.DropoffLng, 6), // NEW: Standard field

            // ===== PASSENGER & BIDDING =====
            passengers = passengersStr,      // NEW: Standard field
            biddingWindowSec = windowSec,    // NEW: Standard field

            // ===== CUSTOMER INFO =====
            customerName = job.CallerName ?? "Customer", // NEW: Standard field
            customerPhone = job.CallerPhone ?? "",       // NEW: Standard field
            callerName = job.CallerName ?? "Customer",   // OLD: Your current field
            callerPhone = job.CallerPhone ?? "",         // OLD: Your current field

            // ===== FARE & NOTES =====
            fare = fareStr,                  // NEW: Standard field (string)
            estimatedFare = fareStr,         // OLD: Your current field
            notes = job.SpecialRequirements ?? "None", // NEW: Standard field
            specialRequirements = job.SpecialRequirements ?? "None", // OLD: Your current field

            // ===== JOB STATUS =====
            status = status ?? "queued",     // Current job status
            result = result,                 // Result for bid responses ("won", "lost")

            // ===== TEMP FIELDS (FOR FUTURE EXPANSION) =====
            temp1 = job.Priority ?? "",      // Priority field (e.g., "high")
            temp2 = job.VehicleOverride ?? "", // Vehicle override (e.g., "wheelchair")
            temp3 = job.PaymentMethod ?? "", // Payment method (e.g., "corporate")

            // ===== METADATA =====
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            dispatcherId = _dispatcherId,
            version = "11.9.2"
        };
    }

    // ── GEOCODER CLASS (Nominatim Integration) ──
    private class Geocoder
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string>? _logger;
        private const string NOMINATIM_URL = "https://nominatim.openstreetmap.org/search";

        public Geocoder(HttpClient httpClient, Action<string>? logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<(double Latitude, double Longitude)?> GeocodeAddressAsync(string address, string type, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(address))
                return null;

            try
            {
                // URL encode the address and add UK filter
                var encodedAddress = Uri.EscapeDataString(address);
                var url = $"{NOMINATIM_URL}?q={encodedAddress}&format=json&limit=1&countrycodes=gb";

                _logger?.Invoke($"📍 Geocoding {type} address: {address}");

                using var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.GetArrayLength() == 0)
                {
                    _logger?.Invoke($"⚠️ No geocoding results found for {type} address: {address}");
                    return null;
                }

                var resultEl = doc.RootElement[0];
                if (!resultEl.TryGetProperty("lat", out var latElement) ||
                    !resultEl.TryGetProperty("lon", out var lonElement))
                {
                    _logger?.Invoke($"⚠️ Invalid geocoding response for {type} address: {address}");
                    return null;
                }

                if (!double.TryParse(latElement.GetString(), CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(lonElement.GetString(), CultureInfo.InvariantCulture, out var lon))
                {
                    _logger?.Invoke($"⚠️ Could not parse coordinates for {type} address: {address}");
                    return null;
                }

                return (lat, lon);
            }
            catch (OperationCanceledException)
            {
                _logger?.Invoke($"⚠️ Geocoding timeout for {type} address: {address}");
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"❌ Geocoding failed for {type} address '{address}': {ex.Message}");
                return null;
            }
        }
    }

    // ── Message DTOs ──

    private class DriverLocationMsg
    {
        public double lat { get; set; }
        public double lng { get; set; }
        public string? status { get; set; }
    }

    private class DriverStatusMsg
    {
        public string? status { get; set; }
        public double lat { get; set; }
        public double lng { get; set; }
        public string? name { get; set; }
        public string? vehicle { get; set; }
    }

    private class BookingMsg
    {
        // ── Dispatch/AdaMain format ──
        public string? pickup { get; set; }
        public string? dropoff { get; set; }
        public JsonElement? passengers { get; set; }
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

        // ── Pub app / dispatcher / generic format ──
        public string? job { get; set; }            // job ID
        public string? pickupAddress { get; set; }   // dispatcher format: pickup address
        public string? customerName { get; set; }    // maps to CallerName
        public string? customerPhone { get; set; }   // maps to CallerPhone
        public string? destination { get; set; }     // maps to Dropoff
        public double destinationLat { get; set; }   // maps to DropoffLat
        public double destinationLng { get; set; }   // maps to DropoffLng
        public double lat { get; set; }              // pickup lat
        public double lng { get; set; }              // pickup lng
        public string? fare { get; set; }            // string like "£12.50"
        public string? notes { get; set; }           // special requirements / notes
        public long timestamp { get; set; }          // epoch ms

        // ── Generic payload extensions ──
        public string? passengersText { get; set; }  // descriptive string e.g. "2 adults, 1 child"
        public int biddingWindowSec { get; set; }    // bidding window override (seconds)
        public string? temp1 { get; set; }           // expansion: priority:high
        public string? temp2 { get; set; }           // expansion: vehicle:accessible
        public string? temp3 { get; set; }           // expansion: payment:corporate
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
