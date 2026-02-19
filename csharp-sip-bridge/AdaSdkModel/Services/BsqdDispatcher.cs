using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AdaSdkModel.Config;
using AdaSdkModel.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace AdaSdkModel.Services;

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
    // Fallback: wide Western Europe box
    private const double FB_MIN_LAT = 35.0;
    private const double FB_MAX_LAT = 72.0;
    private const double FB_MIN_LNG = -25.0;
    private const double FB_MAX_LNG = 45.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public BsqdDispatcher(ILogger<BsqdDispatcher> logger, DispatchSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _geocodeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _geocodeClient.DefaultRequestHeaders.Add("User-Agent", "AdaSdkDispatch/1.0 (maxparsons123@gmail.com)");
    }

    public async Task<bool> DispatchAsync(BookingState booking, string phoneNumber)
    {
        var bsqd = DispatchBsqdAsync(booking, phoneNumber);
        var mqtt = PublishMqttAsync(booking, phoneNumber);
        await Task.WhenAll(bsqd, mqtt);
        return await bsqd || await mqtt;
    }

    private async Task<bool> DispatchBsqdAsync(BookingState booking, string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.BsqdWebhookUrl)) return false;
        try
        {
            var payload = new
            {
                departure_address = new { lat = booking.PickupLat ?? 0, lon = booking.PickupLon ?? 0, street_name = booking.PickupStreet ?? "", street_number = booking.PickupNumber ?? "", postal_code = booking.PickupPostalCode ?? "", city = booking.PickupCity ?? "", formatted_depa_address = booking.PickupFormatted ?? FormatStandardAddress(booking.PickupCity, booking.PickupStreet, booking.PickupNumber, booking.Pickup) },
                destination_address = new { lat = booking.DestLat ?? 0, lon = booking.DestLon ?? 0, street_name = booking.DestStreet ?? "", street_number = booking.DestNumber ?? "", postal_code = booking.DestPostalCode ?? "", city = booking.DestCity ?? "", formatted_dest_address = booking.DestFormatted ?? FormatStandardAddress(booking.DestCity, booking.DestStreet, booking.DestNumber, booking.Destination) },
                departure_time = booking.ScheduledAt.HasValue ? booking.ScheduledAt.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                formatted_pickup_time = FormatPickupTime(booking),
                booking_type = booking.ScheduledAt.HasValue ? "advance" : "immediate",
                first_name = booking.Name ?? "Customer",
                total_price = ParseFare(booking.Fare),
                phoneNumber = FormatE164(phoneNumber),
                passengers = (booking.Passengers ?? 1).ToString()
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.BsqdWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(request);
            _logger.LogInformation("BSQD dispatch: {Status}", (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogError(ex, "BSQD dispatch error"); return false; }
    }

    private async Task<bool> PublishMqttAsync(BookingState booking, string phoneNumber)
    {
        var bounds = GetBoundsForPhone(phoneNumber);
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
                        .WithClientId("adasdk-" + Guid.NewGuid().ToString("N")[..8])
                        .WithWebSocketServer(o => o.WithUri(_settings.MqttBrokerUrl))
                        .WithCleanSession().Build();
                    await _mqttClient.ConnectAsync(opts);
                }
            }
            finally { _mqttLock.Release(); }

            var jobId = booking.BookingRef ?? "UNKNOWN_JOB";
            var pickupAddress = booking.PickupFormatted ?? booking.Pickup ?? "Unknown Location";
            var dropoffAddress = booking.DestFormatted ?? booking.Destination ?? "Not specified";

            // Validate and fix pickup coordinates
            var pickupLat = booking.PickupLat ?? 0;
            var pickupLng = booking.PickupLon ?? 0;
            if (!IsValidCoordinate(pickupLat, pickupLng, bounds))
            {
                _logger.LogWarning("Invalid pickup coordinates ({PLat}, {PLng}) for job {JobId}. Attempting geocoding...", pickupLat, pickupLng, jobId);
                var pickupCoords = await GeocodeAddressAsync(pickupAddress, "pickup");
                if (pickupCoords != null)
                {
                    pickupLat = pickupCoords.Value.Lat;
                    pickupLng = pickupCoords.Value.Lng;
                    _logger.LogInformation("Fixed pickup coordinates for {JobId}: {Lat}, {Lng}", jobId, pickupLat, pickupLng);
                }
                else
                {
                    pickupLat = 52.4068;
                    pickupLng = -1.5197;
                    _logger.LogWarning("Could not geocode pickup address. Using Coventry center coordinates.");
                }
            }

            // Validate and fix dropoff coordinates
            var dropoffLat = booking.DestLat ?? 0;
            var dropoffLng = booking.DestLon ?? 0;
            if (!IsValidCoordinate(dropoffLat, dropoffLng, bounds))
            {
                _logger.LogWarning("Invalid dropoff coordinates ({DLat}, {DLng}) for job {JobId}. Attempting geocoding...", dropoffLat, dropoffLng, jobId);
                var dropoffCoords = await GeocodeAddressAsync(dropoffAddress, "dropoff");
                if (dropoffCoords != null)
                {
                    dropoffLat = dropoffCoords.Value.Lat;
                    dropoffLng = dropoffCoords.Value.Lng;
                    _logger.LogInformation("Fixed dropoff coordinates for {JobId}: {Lat}, {Lng}", jobId, dropoffLat, dropoffLng);
                }
                else
                {
                    dropoffLat = 52.4531;
                    dropoffLng = -1.7475;
                    _logger.LogWarning("Could not geocode dropoff address. Using Birmingham Airport coordinates.");
                }
            }

            _logger.LogInformation("MQTT payload coords: PickupLat={PLat}, PickupLng={PLng}, DropoffLat={DLat}, DropoffLng={DLng}, Pickup={PF}, Dropoff={DF}",
                pickupLat, pickupLng, dropoffLat, dropoffLng, pickupAddress, dropoffAddress);

            var fareStr = ParseFare(booking.Fare);

            var payload = JsonSerializer.Serialize(new
            {
                jobId = jobId,
                lat = pickupLat,
                lng = pickupLng,
                pickupAddress = pickupAddress,
                pickup = pickupAddress,
                dropoff = dropoffAddress,
                dropoffName = dropoffAddress,
                dropoffLat = dropoffLat,
                dropoffLng = dropoffLng,
                customerName = booking.Name ?? "Customer",
                customerPhone = FormatE164(phoneNumber),
                callerName = booking.Name ?? "Customer",
                callerPhone = FormatE164(phoneNumber),
                fare = fareStr,
                estimatedFare = fareStr,
                notes = booking.ScheduledAt.HasValue ? "Advance booking" : (booking.PickupTime ?? "None"),
                specialRequirements = booking.ScheduledAt.HasValue ? "Advance booking" : (booking.PickupTime ?? "None"),
                scheduledTime = booking.ScheduledAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                pickupTime = booking.PickupTime ?? "ASAP",
                formattedPickupTime = FormatPickupTime(booking),
                biddingWindowSec = booking.BiddingWindowSec ?? 30,
                passengers = booking.Passengers?.ToString() ?? "1",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            var topic = $"pubs/requests/{jobId}";
            var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
            await _mqttClient!.PublishAsync(msg);
            _logger.LogInformation("MQTT dispatched to {Topic}", topic);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "MQTT error"); return false; }
    }

    // ===== COORDINATE VALIDATION =====
    private static (double MinLat, double MaxLat, double MinLng, double MaxLng) GetBoundsForPhone(string? phone)
    {
        var e164 = FormatE164(phone);
        if (e164.StartsWith("+"))
        {
            var digits = e164[1..];
            // Try 2-digit prefix first, then 1-digit
            if (digits.Length >= 2 && CountryBounds.TryGetValue(digits[..2], out var b2)) return b2;
            if (digits.Length >= 1 && CountryBounds.TryGetValue(digits[..1], out var b1)) return b1;
        }
        return (FB_MIN_LAT, FB_MAX_LAT, FB_MIN_LNG, FB_MAX_LNG);
    }

    private static bool IsValidCoordinate(double lat, double lng, (double MinLat, double MaxLat, double MinLng, double MaxLng) bounds)
    {
        if (Math.Abs(lat) < 0.001 && Math.Abs(lng) < 0.001) return false;
        if (lat < bounds.MinLat || lat > bounds.MaxLat || lng < bounds.MinLng || lng > bounds.MaxLng) return false;
        return true;
    }

    private async Task<(double Lat, double Lng)?> GeocodeAddressAsync(string address, string type)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var encoded = Uri.EscapeDataString(address);
            var url = $"https://nominatim.openstreetmap.org/search?q={encoded}&format=json&limit=1";
            _logger.LogInformation("Geocoding {Type} address: {Address}", type, address);

            using var response = await _geocodeClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() == 0) { _logger.LogWarning("No geocoding results for {Type}: {Address}", type, address); return null; }

            var result = doc.RootElement[0];
            if (!result.TryGetProperty("lat", out var latEl) || !result.TryGetProperty("lon", out var lonEl)) return null;
            if (!double.TryParse(latEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lng)) return null;
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
            request.Content = new StringContent(JsonSerializer.Serialize(new { phoneNumber = FormatE164(phoneNumber) }), Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(request);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string FormatE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+31000000000";
        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (clean.StartsWith("00")) clean = "+" + clean[2..];
        if (clean.StartsWith("+")) return clean;
        if (clean.Length >= 10 && (clean.StartsWith("44") || clean.StartsWith("33") || clean.StartsWith("49") || clean.StartsWith("32") || clean.StartsWith("34") || clean.StartsWith("39") || clean.StartsWith("1")))
            return "+" + clean;
        if (clean.StartsWith("0")) return "+31" + clean[1..];
        return "+" + clean;
    }

    /// <summary>
    /// Formats an address in standardized EU format (City, Street Number) for WhatsApp/dispatch.
    /// Falls back to raw input if geocoded components are unavailable.
    /// </summary>
    private static string FormatStandardAddress(string? city, string? street, string? number, string? rawFallback)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(city)) parts.Add(city);
        if (!string.IsNullOrWhiteSpace(street))
        {
            var streetPart = street;
            if (!string.IsNullOrWhiteSpace(number))
                streetPart += " " + number;
            parts.Add(streetPart);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : rawFallback ?? "";
    }

    /// <summary>
    /// Formats pickup time as human-readable string for webhook consumers.
    /// e.g. "Tomorrow at 4:30 PM", "Today at 6:00 PM", "Wed 25 Feb at 3:00 PM", or "ASAP".
    /// </summary>
    private static string FormatPickupTime(BookingState booking)
    {
        if (!booking.ScheduledAt.HasValue)
            return booking.PickupTime ?? "ASAP";

        var uk = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var local = TimeZoneInfo.ConvertTimeFromUtc(booking.ScheduledAt.Value, uk);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, uk);
        var timeStr = local.ToString("h:mm tt");

        if (local.Date == nowLocal.Date)
            return $"Today at {timeStr}";
        if (local.Date == nowLocal.Date.AddDays(1))
            return $"Tomorrow at {timeStr}";
        return $"{local:dddd dd MMMM yyyy} at {timeStr}";
    }

    private static string ParseFare(string? fare)
    {
        if (string.IsNullOrWhiteSpace(fare)) return "0.00";
        var clean = new string(fare.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
        return decimal.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "0.00";
    }
}
