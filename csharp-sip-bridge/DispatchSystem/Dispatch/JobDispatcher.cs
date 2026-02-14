using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DispatchSystem.Data;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace DispatchSystem.Dispatch;

/// <summary>
/// Job Dispatcher for Black Cab Unite Driver App
/// Sends jobs with proper coordinate validation and fallback geocoding
/// Compatible with driver app v11.9.2+
/// </summary>
public class JobDispatcher : IDisposable
{
    // ===== CONFIGURATION =====
    private const string BROKER_ADDRESS = "broker.hivemq.com";
    private const int BROKER_PORT = 1883;
    private const string MQTT_TOPIC_PREFIX = "pubs/requests/";
    private const int RECONNECT_DELAY_MS = 5000;
    private const int GEOCODING_TIMEOUT_MS = 5000;
    private const string USER_AGENT = "BlackCabUniteDispatch/1.0 (maxparsons123@gmail.com)";

    // UK bounding box validation
    private const double MIN_LAT = 49.5;
    private const double MAX_LAT = 61.0;
    private const double MIN_LNG = -8.5;
    private const double MAX_LNG = 2.0;

    // ===== FIELDS =====
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _mqttOptions;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _dispatcherId;
    private bool _isConnected;
    private int _reconnectAttempts;

    // ===== INTERFACES FOR DEPENDENCY INJECTION =====
    public interface ILogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception? ex = null);
    }

    // Default console logger
    private class ConsoleLogger : ILogger
    {
        public void Info(string message) => Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} | {message}");
        public void Warn(string message) => Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} | {message}");
        public void Error(string message, Exception? ex = null) =>
            Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} | {message}{(ex != null ? $"\n{ex}" : "")}");
    }

    // ===== CONSTRUCTOR =====
    public JobDispatcher(string dispatcherId, ILogger? logger = null)
    {
        _dispatcherId = dispatcherId ?? throw new ArgumentNullException(nameof(dispatcherId));
        _logger = logger ?? new ConsoleLogger();

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(BROKER_ADDRESS, BROKER_PORT)
            .WithClientId($"dispatch_{_dispatcherId}_{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(GEOCODING_TIMEOUT_MS),
            DefaultRequestHeaders = { { "User-Agent", USER_AGENT } }
        };

        _mqttClient.ConnectedAsync += async _ =>
        {
            _isConnected = true;
            _reconnectAttempts = 0;
            _logger.Info($"✅ Connected to MQTT broker: {BROKER_ADDRESS}:{BROKER_PORT}");
        };

        _mqttClient.DisconnectedAsync += async _ =>
        {
            _isConnected = false;
            _reconnectAttempts++;
            _logger.Warn($"⚠️ Disconnected from MQTT broker (attempt {_reconnectAttempts}). Reconnecting...");
            await Task.Delay(RECONNECT_DELAY_MS);
            try { await _mqttClient.ConnectAsync(_mqttOptions); }
            catch (Exception ex) { _logger.Error($"❌ Reconnect failed: {ex.Message}"); }
        };

        _logger.Info($"JobDispatcher initialized for dispatcher: {_dispatcherId}");
    }

    // ===== PUBLIC METHODS =====

    public bool IsConnected => _isConnected;

    public async Task ConnectAsync()
    {
        if (_isConnected) return;

        try
        {
            _logger.Info("🔌 Starting MQTT client connection...");
            await _mqttClient.ConnectAsync(_mqttOptions);

            var timeout = TimeSpan.FromSeconds(10);
            var startTime = DateTime.UtcNow;
            while (!_isConnected && (DateTime.UtcNow - startTime) < timeout)
                await Task.Delay(100);

            if (!_isConnected)
                throw new TimeoutException("Connection timeout after 10 seconds");
        }
        catch (Exception ex)
        {
            _logger.Error($"❌ Failed to connect to MQTT broker: {ex.Message}", ex);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (!_isConnected) return;
        try
        {
            await _mqttClient.DisconnectAsync();
            _isConnected = false;
            _logger.Info("📴 Disconnected from MQTT broker");
        }
        catch (Exception ex)
        {
            _logger.Error($"❌ Error during disconnect: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Dispatch a job to eligible drivers using the existing DispatchSystem.Data.Job model.
    /// </summary>
    public async Task<DispatchResult> DispatchJobAsync(Job job, CancellationToken cancellationToken = default)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        if (string.IsNullOrWhiteSpace(job.Id)) throw new ArgumentException("Job Id is required");

        try
        {
            // Validate and fix coordinates
            var fixedPickupLat = job.PickupLat;
            var fixedPickupLng = job.PickupLng;
            var fixedDropoffLat = job.DropoffLat;
            var fixedDropoffLng = job.DropoffLng;
            bool pickupFixed = false, dropoffFixed = false;

            if (!IsValidCoordinate(fixedPickupLat, fixedPickupLng))
            {
                _logger.Warn($"⚠️ Invalid pickup coordinates ({fixedPickupLat}, {fixedPickupLng}) for job {job.Id}. Attempting geocoding...");
                var coords = await GeocodeAddressAsync(job.Pickup, "pickup", cancellationToken);
                if (coords != null)
                {
                    fixedPickupLat = coords.Value.Latitude;
                    fixedPickupLng = coords.Value.Longitude;
                    _logger.Info($"✅ Fixed pickup coordinates for {job.Id}: {fixedPickupLat}, {fixedPickupLng}");
                }
                else
                {
                    fixedPickupLat = 52.4068;
                    fixedPickupLng = -1.5197;
                    _logger.Warn("⚠️ Could not geocode pickup address. Using Coventry center coordinates.");
                }
                pickupFixed = true;
            }

            if (!IsValidCoordinate(fixedDropoffLat, fixedDropoffLng))
            {
                _logger.Warn($"⚠️ Invalid dropoff coordinates ({fixedDropoffLat}, {fixedDropoffLng}) for job {job.Id}. Attempting geocoding...");
                var coords = await GeocodeAddressAsync(job.Dropoff, "dropoff", cancellationToken);
                if (coords != null)
                {
                    fixedDropoffLat = coords.Value.Latitude;
                    fixedDropoffLng = coords.Value.Longitude;
                    _logger.Info($"✅ Fixed dropoff coordinates for {job.Id}: {fixedDropoffLat}, {fixedDropoffLng}");
                }
                else
                {
                    fixedDropoffLat = 52.4531;
                    fixedDropoffLng = -1.7475;
                    _logger.Warn("⚠️ Could not geocode dropoff address. Using Birmingham Airport coordinates.");
                }
                dropoffFixed = true;
            }

            // Create MQTT payload compatible with driver app v11.9.2+
            var payload = CreateDriverAppPayload(job, fixedPickupLat, fixedPickupLng, fixedDropoffLat, fixedDropoffLng);

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var topic = $"{MQTT_TOPIC_PREFIX}{job.Id}";
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(json))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(msg, cancellationToken);

            _logger.Info($"✅ Job dispatched successfully | ID: {job.Id} | Pickup: {job.Pickup} | Dropoff: {job.Dropoff}");

            return new DispatchResult
            {
                Success = true,
                JobId = job.Id,
                Message = "Job dispatched to eligible drivers",
                PickupCoordinatesFixed = pickupFixed,
                DropoffCoordinatesFixed = dropoffFixed
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"⚠️ Job dispatch cancelled for {job.Id}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"❌ Failed to dispatch job {job.Id}: {ex.Message}", ex);
            return new DispatchResult
            {
                Success = false,
                JobId = job.Id,
                ErrorMessage = ex.Message,
                Exception = ex
            };
        }
    }

    // ===== COORDINATE VALIDATION & GEOCODING =====

    private static bool IsValidCoordinate(double lat, double lng)
    {
        if (Math.Abs(lat) < 0.001 || Math.Abs(lng) < 0.001) return false;
        if (lat < MIN_LAT || lat > MAX_LAT || lng < MIN_LNG || lng > MAX_LNG) return false;
        return true;
    }

    private async Task<(double Latitude, double Longitude)?> GeocodeAddressAsync(string address, string type, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        try
        {
            var encodedAddress = Uri.EscapeDataString(address);
            var url = $"https://nominatim.openstreetmap.org/search?q={encodedAddress}&format=json&limit=1&countrycodes=gb";

            _logger.Info($"📍 Geocoding {type} address: {address}");

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetArrayLength() == 0)
            {
                _logger.Warn($"⚠️ No geocoding results found for {type} address: {address}");
                return null;
            }

            var result = doc.RootElement[0];
            if (!result.TryGetProperty("lat", out var latElement) ||
                !result.TryGetProperty("lon", out var lonElement))
            {
                _logger.Warn($"⚠️ Invalid geocoding response for {type} address: {address}");
                return null;
            }

            if (!double.TryParse(latElement.GetString(), CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonElement.GetString(), CultureInfo.InvariantCulture, out var lon))
            {
                _logger.Warn($"⚠️ Could not parse coordinates for {type} address: {address}");
                return null;
            }

            return (lat, lon);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"⚠️ Geocoding timeout for {type} address: {address}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error($"❌ Geocoding failed for {type} address '{address}': {ex.Message}", ex);
            return null;
        }
    }

    // ===== PAYLOAD CREATION (DRIVER APP COMPATIBLE) =====

    private object CreateDriverAppPayload(Job job, double pickupLat, double pickupLng, double dropoffLat, double dropoffLng)
    {
        var fareStr = job.EstimatedFare?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        var paxStr = job.PassengerDetails ?? job.Passengers.ToString();

        // CRITICAL: Send BOTH new and old field names for maximum compatibility
        return new
        {
            // Core job fields (both formats)
            job = job.Id,
            jobId = job.Id,

            // Pickup coordinates (both formats)
            lat = Math.Round(pickupLat, 6),
            lng = Math.Round(pickupLng, 6),
            pickupLat = Math.Round(pickupLat, 6),
            pickupLng = Math.Round(pickupLng, 6),

            // Pickup address (both formats)
            pickupAddress = job.Pickup,
            pickup = job.Pickup,
            pubName = job.Pickup,

            // Dropoff fields (both formats)
            dropoff = job.Dropoff,
            dropoffName = job.Dropoff,
            dropoffLat = Math.Round(dropoffLat, 6),
            dropoffLng = Math.Round(dropoffLng, 6),

            // Passenger & bidding
            passengers = paxStr,
            biddingWindowSec = job.BiddingWindowSec ?? 30,

            // Customer info (both formats)
            customerName = job.CallerName ?? "Customer",
            customerPhone = job.CallerPhone ?? "",
            callerName = job.CallerName ?? "Customer",
            callerPhone = job.CallerPhone ?? "",

            // Fare & notes (both formats)
            fare = fareStr,
            estimatedFare = fareStr,
            notes = job.SpecialRequirements ?? "None",
            specialRequirements = job.SpecialRequirements ?? "None",

            // Temp fields for future expansion
            temp1 = job.Priority ?? "",
            temp2 = job.VehicleOverride ?? "",
            temp3 = job.PaymentMethod ?? "",

            // Metadata
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            dispatcherId = _dispatcherId,
            version = "11.9.2"
        };
    }

    // ===== DISPOSAL =====
    public void Dispose()
    {
        _httpClient?.Dispose();
        _mqttClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ===== NESTED TYPES =====

    /// <summary>
    /// Result of job dispatch operation
    /// </summary>
    public class DispatchResult
    {
        public bool Success { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public bool PickupCoordinatesFixed { get; set; }
        public bool DropoffCoordinatesFixed { get; set; }
        public Exception? Exception { get; set; }
    }
}
