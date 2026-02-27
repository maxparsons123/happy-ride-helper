// Ported from AdaSdkModel — adapted for StructuredBooking + FareResult
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Config;
using AdaCleanVersion.Models;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace AdaCleanVersion.Services;

/// <summary>
/// Dispatcher interface for AdaCleanVersion.
/// </summary>
public interface IDispatcher
{
    Task<bool> DispatchAsync(StructuredBooking booking, FareResult fare, string phoneNumber,
        string? bookingRef = null, string? paymentLink = null);
    Task<bool> SendWhatsAppAsync(string phoneNumber);
}

/// <summary>
/// Dispatches confirmed bookings via BSQD webhook + MQTT.
/// Adapted from AdaSdkModel to work with StructuredBooking + FareResult.
/// Includes coordinate validation and geocoding fallback.
/// </summary>
public sealed class BsqdDispatcher : IDispatcher
{
    private readonly ILogger<BsqdDispatcher> _logger;
    private readonly DispatchSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _geocodeClient;
    private IMqttClient? _mqttClient;
    private readonly SemaphoreSlim _mqttLock = new(1, 1);

    // Country bounding boxes for coordinate validation
    private static readonly Dictionary<string, (double MinLat, double MaxLat, double MinLng, double MaxLng)> CountryBounds = new()
    {
        ["44"] = (49.5, 61.0, -8.5, 2.0),       // UK
        ["31"] = (50.7, 53.6, 3.3, 7.3),         // Netherlands
        ["32"] = (49.4, 51.6, 2.5, 6.5),         // Belgium
        ["33"] = (41.3, 51.1, -5.2, 9.6),        // France
        ["49"] = (47.2, 55.1, 5.8, 15.1),        // Germany
        ["34"] = (35.9, 43.8, -9.4, 4.4),        // Spain
        ["39"] = (36.6, 47.1, 6.6, 18.5),        // Italy
        ["1"]  = (24.5, 71.5, -168.0, -52.0),    // US/Canada
    };
    private const double FB_MIN_LAT = 35.0, FB_MAX_LAT = 72.0, FB_MIN_LNG = -25.0, FB_MAX_LNG = 45.0;

    public BsqdDispatcher(ILogger<BsqdDispatcher> logger, DispatchSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _geocodeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _geocodeClient.DefaultRequestHeaders.Add("User-Agent", "AdaCleanDispatch/1.0 (dispatch@im-tech.co.uk)");
    }

    public async Task<bool> DispatchAsync(
        StructuredBooking booking, FareResult fare, string phoneNumber,
        string? bookingRef = null, string? paymentLink = null)
    {
        var bsqd = DispatchBsqdAsync(booking, fare, phoneNumber, bookingRef, paymentLink);
        var mqtt = PublishMqttAsync(booking, fare, phoneNumber, bookingRef, paymentLink);
        await Task.WhenAll(bsqd, mqtt);
        return await bsqd || await mqtt;
    }

    private async Task<bool> DispatchBsqdAsync(
        StructuredBooking booking, FareResult fare, string phoneNumber,
        string? bookingRef, string? paymentLink)
    {
        if (string.IsNullOrEmpty(_settings.BsqdWebhookUrl)) return false;
        try
        {
            var priceField = fare.Fare;
            if (!string.IsNullOrWhiteSpace(paymentLink))
                priceField = $"{fare.Fare} | Pay: {paymentLink}";

            var payload = new
            {
                departure_address = new
                {
                    lat = fare.Pickup.Lat, lon = fare.Pickup.Lon,
                    street_name = fare.Pickup.StreetName ?? "",
                    street_number = fare.Pickup.StreetNumber ?? "",
                    postal_code = fare.Pickup.PostalCode ?? "",
                    city = fare.Pickup.City ?? "",
                    formatted_depa_address = fare.Pickup.Address
                },
                destination_address = new
                {
                    lat = fare.Destination.Lat, lon = fare.Destination.Lon,
                    street_name = fare.Destination.StreetName ?? "",
                    street_number = fare.Destination.StreetNumber ?? "",
                    postal_code = fare.Destination.PostalCode ?? "",
                    city = fare.Destination.City ?? "",
                    formatted_dest_address = fare.Destination.Address
                },
                departure_time = booking.IsAsap
                    ? DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : booking.PickupDateTime?.ToString("yyyy-MM-ddTHH:mm:ssZ")
                      ?? DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                formatted_pickup_time = booking.PickupTime,
                booking_type = booking.IsAsap ? "immediate" : "advance",
                first_name = booking.CallerName ?? "Customer",
                total_price = priceField,
                phoneNumber = FormatE164(phoneNumber),
                passengers = booking.Passengers.ToString(),
                eta = fare.DriverEta,
                payment_link = paymentLink
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true });
            _logger.LogInformation("[BSQD] Payload:\n{Json}", json);

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.BsqdWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            _logger.LogInformation("[BSQD] Response: {Status}", (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogError(ex, "[BSQD] Dispatch error"); return false; }
    }

    private async Task<bool> PublishMqttAsync(
        StructuredBooking booking, FareResult fare, string phoneNumber,
        string? bookingRef, string? paymentLink)
    {
        if (string.IsNullOrEmpty(_settings.MqttBrokerUrl)) return false;
        try
        {
            await _mqttLock.WaitAsync();
            try
            {
                if (_mqttClient is not { IsConnected: true })
                {
                    _mqttClient?.Dispose();
                    var factory = new MqttFactory();
                    _mqttClient = factory.CreateMqttClient();
                    var opts = new MqttClientOptionsBuilder()
                        .WithClientId("adaclean-" + Guid.NewGuid().ToString("N")[..8])
                        .WithWebSocketServer(o => o.WithUri(_settings.MqttBrokerUrl))
                        .WithCleanSession().Build();
                    await _mqttClient.ConnectAsync(opts);
                }
            }
            finally { _mqttLock.Release(); }

            var jobId = bookingRef ?? "UNKNOWN_JOB";
            var bounds = GetBoundsForPhone(phoneNumber);

            // Validate coordinates
            var pickupLat = fare.Pickup.Lat;
            var pickupLng = fare.Pickup.Lon;
            if (!IsValidCoordinate(pickupLat, pickupLng, bounds))
            {
                var geo = await GeocodeAddressAsync(fare.Pickup.Address, "pickup");
                if (geo != null) { pickupLat = geo.Value.Lat; pickupLng = geo.Value.Lng; }
                else { pickupLat = 52.4068; pickupLng = -1.5197; }
            }

            var dropoffLat = fare.Destination.Lat;
            var dropoffLng = fare.Destination.Lon;
            if (!IsValidCoordinate(dropoffLat, dropoffLng, bounds))
            {
                var geo = await GeocodeAddressAsync(fare.Destination.Address, "dropoff");
                if (geo != null) { dropoffLat = geo.Value.Lat; dropoffLng = geo.Value.Lng; }
                else { dropoffLat = 52.4531; dropoffLng = -1.7475; }
            }

            var mqttFare = fare.Fare;
            if (!string.IsNullOrWhiteSpace(paymentLink))
                mqttFare = $"{fare.Fare} | Pay: {paymentLink}";

            var payload = JsonSerializer.Serialize(new
            {
                jobId,
                lat = pickupLat, lng = pickupLng,
                pickupAddress = fare.Pickup.Address,
                pickup = fare.Pickup.Address,
                dropoff = fare.Destination.Address,
                dropoffLat, dropoffLng,
                customerName = booking.CallerName ?? "Customer",
                customerPhone = FormatE164(phoneNumber),
                fare = mqttFare,
                eta = fare.DriverEta,
                pickupTime = booking.PickupTime,
                passengers = booking.Passengers.ToString(),
                payment_link = paymentLink,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            var topic = $"pubs/requests/{jobId}";
            var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
            await _mqttClient!.PublishAsync(msg);
            _logger.LogInformation("[MQTT] Dispatched to {Topic}", topic);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "[MQTT] Error"); return false; }
    }

    // ===== COORDINATE VALIDATION =====

    private static (double MinLat, double MaxLat, double MinLng, double MaxLng) GetBoundsForPhone(string? phone)
    {
        var e164 = FormatE164(phone);
        if (e164.StartsWith("+"))
        {
            var digits = e164[1..];
            if (digits.Length >= 2 && CountryBounds.TryGetValue(digits[..2], out var b2)) return b2;
            if (digits.Length >= 1 && CountryBounds.TryGetValue(digits[..1], out var b1)) return b1;
        }
        return (FB_MIN_LAT, FB_MAX_LAT, FB_MIN_LNG, FB_MAX_LNG);
    }

    private static bool IsValidCoordinate(double lat, double lng, (double MinLat, double MaxLat, double MinLng, double MaxLng) bounds)
    {
        if (Math.Abs(lat) < 0.001 && Math.Abs(lng) < 0.001) return false;
        return lat >= bounds.MinLat && lat <= bounds.MaxLat && lng >= bounds.MinLng && lng <= bounds.MaxLng;
    }

    private async Task<(double Lat, double Lng)?> GeocodeAddressAsync(string address, string type)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&format=json&limit=1";
            using var response = await _geocodeClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() == 0) return null;
            var result = doc.RootElement[0];
            if (!result.TryGetProperty("lat", out var latEl) || !result.TryGetProperty("lon", out var lonEl)) return null;
            if (!double.TryParse(latEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lng)) return null;
            return (lat, lng);
        }
        catch (Exception ex) { _logger.LogError(ex, "Geocoding failed for {Type} '{Address}'", type, address); return null; }
    }

    public async Task<bool> SendWhatsAppAsync(string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.WhatsAppWebhookUrl)) return false;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.WhatsAppWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { phoneNumber = FormatE164(phoneNumber) }),
                Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(request);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    internal static string FormatE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+31000000000";
        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (clean.StartsWith("00")) clean = "+" + clean[2..];
        if (clean.StartsWith("+")) return clean;
        if (clean.Length >= 10 && (clean.StartsWith("44") || clean.StartsWith("33") || clean.StartsWith("49") ||
            clean.StartsWith("32") || clean.StartsWith("34") || clean.StartsWith("39") || clean.StartsWith("1")))
            return "+" + clean;
        if (clean.StartsWith("0")) return "+31" + clean[1..];
        return "+" + clean;
    }
}
