using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AdaMain.Config;
using AdaMain.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using AdaMain.Core;
using Microsoft.Extensions.Logging;

namespace AdaMain.Services;

// =========================
// BSQD API DTOs (matching WinForms format)
// =========================

public class AddressDto
{
    public double lat { get; set; }
    public double lon { get; set; }
    public string street_name { get; set; } = "";
    public string street_number { get; set; } = "";
    public string postal_code { get; set; } = "";
    public string city { get; set; } = "";
    public string? formatted_depa_address { get; set; }   // for departure
    public string? formatted_dest_address { get; set; }   // for destination
}

public class BsqdTaxiBotRequest
{
    public AddressDto departure_address { get; set; } = new();
    public AddressDto destination_address { get; set; } = new();
    public DateTimeOffset departure_time { get; set; }
    public string first_name { get; set; } = "";
    public string total_price { get; set; } = "";
    public string phoneNumber { get; set; } = "";
    public string passengers { get; set; } = "1";
}

/// <summary>
/// BSQD dispatch and WhatsApp notification service.
/// Uses nested AddressDto format matching the WinForms dispatcher.
/// </summary>
public sealed class BsqdDispatcher : IDispatcher
{
    private readonly ILogger<BsqdDispatcher> _logger;
    private readonly DispatchSettings _settings;
    private readonly HttpClient _httpClient;
    private IMqttClient? _mqttClient;
    private readonly SemaphoreSlim _mqttLock = new(1, 1);
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
    public BsqdDispatcher(ILogger<BsqdDispatcher> logger, DispatchSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }
    
    public async Task<bool> DispatchAsync(BookingState booking, string phoneNumber)
    {
        // Fire both BSQD webhook and MQTT in parallel — keep both paths
        var bsqdTask = DispatchBsqdAsync(booking, phoneNumber);
        var mqttTask = PublishBookingMqttAsync(booking, phoneNumber);
        
        await Task.WhenAll(bsqdTask, mqttTask);
        return await bsqdTask || await mqttTask; // success if either worked
    }
    
    private async Task<bool> DispatchBsqdAsync(BookingState booking, string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.BsqdWebhookUrl))
        {
            _logger.LogWarning("BSQD webhook URL not configured");
            return false;
        }
        
        try
        {
            var payload = BuildRequest(booking, phoneNumber);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            
            _logger.LogInformation("🚕 BSQD Dispatch → {Phone}", payload.phoneNumber);
            _logger.LogInformation("📦 Payload: {Json}", json);
            
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.BsqdWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("✅ BSQD Dispatch OK: {Status}", (int)response.StatusCode);
                return true;
            }
            
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("❌ BSQD Dispatch FAIL: {Status} - {Error}", (int)response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ BSQD Dispatch ERROR");
            return false;
        }
    }
    
    /// <summary>
    /// Publish confirmed booking to MQTT topic 'taxi/bookings' for the DispatchSystem.
    /// Uses the same JSON format that MqttDispatchClient expects.
    /// </summary>
    private async Task<bool> PublishBookingMqttAsync(BookingState booking, string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.MqttBrokerUrl))
            return false;
        
        try
        {
            var client = await GetOrCreateMqttClientAsync();
            if (client == null || !client.IsConnected)
            {
                _logger.LogWarning("⚠ MQTT not connected, skipping booking publish");
                return false;
            }
            
            var payload = JsonSerializer.Serialize(new
            {
                pickup = booking.PickupFormatted ?? booking.Pickup ?? "",
                dropoff = booking.DestFormatted ?? booking.Destination ?? "",
                passengers = booking.Passengers ?? 1,
                vehicleType = (booking.Passengers ?? 1) > 4 ? "MPV" : "Saloon",
                estimatedPrice = decimal.TryParse(ParseFare(booking.Fare),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var fare) ? fare : 0m,
                pickupLat = booking.PickupLat ?? 0,
                pickupLng = booking.PickupLon ?? 0,
                dropoffLat = booking.DestLat ?? 0,
                dropoffLng = booking.DestLon ?? 0,
                callerPhone = FormatE164(phoneNumber),
                callerName = booking.Name ?? "Customer",
                bookingRef = booking.BookingRef ?? ""
            });
            
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic("taxi/bookings")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            
            await client.PublishAsync(msg);
            _logger.LogInformation("📡 MQTT booking published to taxi/bookings");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ MQTT booking publish failed");
            return false;
        }
    }
    
    private async Task<IMqttClient?> GetOrCreateMqttClientAsync()
    {
        await _mqttLock.WaitAsync();
        try
        {
            if (_mqttClient is { IsConnected: true })
                return _mqttClient;
            
            _mqttClient?.Dispose();
            
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();
            
            var options = new MqttClientOptionsBuilder()
                .WithClientId("ada-dispatch-" + Guid.NewGuid().ToString("N")[..8])
                .WithWebSocketServer(o => o.WithUri(_settings.MqttBrokerUrl))
                .WithCleanSession()
                .Build();
            
            await _mqttClient.ConnectAsync(options);
            _logger.LogInformation("✅ MQTT dispatch client connected");
            return _mqttClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ MQTT dispatch connect failed");
            return null;
        }
        finally
        {
            _mqttLock.Release();
        }
    }
    
    /// <summary>
    /// Build nested TaxiBotRequest from BookingState (matching WinForms format).
    /// Uses geocoded components when available, falls back to string parsing.
    /// </summary>
    private static BsqdTaxiBotRequest BuildRequest(BookingState booking, string callerId)
    {
        // Parse departure time
        var departureTime = DateTimeOffset.Now.AddMinutes(5);
        if (!string.IsNullOrWhiteSpace(booking.PickupTime))
        {
            var normalized = booking.PickupTime.Trim().ToLowerInvariant();
            if (normalized is "now" or "asap" or "nu" or "direct")
            {
                departureTime = DateTimeOffset.Now.AddMinutes(2);
            }
            else if (DateTimeOffset.TryParse(booking.PickupTime, out var parsed))
            {
                departureTime = parsed;
            }
        }

        return new BsqdTaxiBotRequest
        {
            departure_address = new AddressDto
            {
                lat = booking.PickupLat ?? 0,
                lon = booking.PickupLon ?? 0,
                street_name = booking.PickupStreet ?? ParseStreetName(booking.Pickup),
                street_number = ResolveHouseNumber(booking.PickupNumber, booking.Pickup),
                postal_code = booking.PickupPostalCode ?? "",
                city = booking.PickupCity ?? ParseCity(booking.Pickup),
                formatted_depa_address = booking.PickupFormatted ?? booking.Pickup ?? "Unknown"
            },
            destination_address = new AddressDto
            {
                lat = booking.DestLat ?? 0,
                lon = booking.DestLon ?? 0,
                street_name = booking.DestStreet ?? ParseStreetName(booking.Destination),
                street_number = ResolveHouseNumber(booking.DestNumber, booking.Destination),
                postal_code = booking.DestPostalCode ?? "",
                city = booking.DestCity ?? ParseCity(booking.Destination),
                formatted_dest_address = booking.DestFormatted ?? booking.Destination ?? "Unknown"
            },
            departure_time = departureTime,
            first_name = booking.Name ?? "Customer",
            total_price = ParseFare(booking.Fare),
            phoneNumber = FormatE164(callerId),
            passengers = (booking.Passengers ?? 1).ToString()
        };
    }
    
    /// <summary>
    /// Resolve house number as string, preserving alphanumeric values like "52A".
    /// </summary>
    private static string ResolveHouseNumber(string? stateValue, string? rawAddress)
    {
        if (!string.IsNullOrWhiteSpace(stateValue))
            return stateValue;

        if (!string.IsNullOrWhiteSpace(rawAddress))
        {
            var parts = rawAddress.Split(',');
            if (parts.Length > 0)
            {
                var match = Regex.Match(parts[0].Trim(), @"^(\d+[A-Za-z]?(?:-\d+[A-Za-z]?)?)\s");
                if (match.Success) return match.Groups[1].Value;
                
                match = Regex.Match(parts[0].Trim(), @"\s(\d+[A-Za-z]?(?:-\d+[A-Za-z]?)?)$");
                if (match.Success) return match.Groups[1].Value;
            }
        }

        return "";
    }
    
    public async Task<bool> SendWhatsAppAsync(string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.WhatsAppWebhookUrl) || 
            string.IsNullOrWhiteSpace(phoneNumber))
        {
            _logger.LogDebug("WhatsApp notification skipped");
            return false;
        }
        
        try
        {
            var formatted = FormatE164(phoneNumber);
            
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.WhatsAppWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { phoneNumber = formatted }),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("WhatsApp sent to {Phone}", formatted);
                return true;
            }
            
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("WhatsApp failed: {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp error");
            return false;
        }
    }
    
    /// <summary>
    /// Format phone to E.164 (+447539025332 or +31612345678).
    /// </summary>
    private static string FormatE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "+31000000000";

        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());

        // Convert 00 prefix to +
        if (clean.StartsWith("00"))
            clean = "+" + clean[2..];

        if (clean.StartsWith("+"))
        {
            // Dutch mobile: remove extra 0 after +31
            if (clean.StartsWith("+310") && clean.Length > 11)
                clean = "+31" + clean[4..];
            return clean;
        }

        // Detect existing country code
        if (clean.StartsWith("44") && clean.Length >= 11) return "+" + clean;
        if (clean.StartsWith("31") && clean.Length >= 10) return "+" + clean;
        if (clean.StartsWith("49") && clean.Length >= 10) return "+" + clean;
        if (clean.StartsWith("33") && clean.Length >= 10) return "+" + clean;
        if (clean.StartsWith("32") && clean.Length >= 9) return "+" + clean;
        if (clean.StartsWith("1") && clean.Length == 11) return "+" + clean;

        // Local Dutch number
        if (clean.StartsWith("0"))
            return "+31" + clean[1..];

        return "+31" + clean;
    }
    
    /// <summary>Parse fare string to decimal format "15.50".</summary>
    private static string ParseFare(string? fare)
    {
        if (string.IsNullOrWhiteSpace(fare)) return "0.00";
        var clean = new string(fare.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
        clean = clean.Replace(',', '.');
        if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return "0.00";
    }
    
    /// <summary>Extract street name from address string (fallback).</summary>
    private static string ParseStreetName(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Unknown";
        var parts = address.Split(',');
        if (parts.Length > 0)
        {
            var streetPart = parts[0].Trim();
            var match = Regex.Match(streetPart, @"^(.+?)\s+\d+[A-Za-z\-]*$");
            if (match.Success) return match.Groups[1].Value;
            return streetPart;
        }
        return address;
    }
    
    /// <summary>Extract city (last non-postal-code part).</summary>
    private static string ParseCity(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        var parts = address.Split(',').Select(p => p.Trim()).ToArray();
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (!Regex.IsMatch(parts[i], @"\d{4}\s*[A-Z]{2}", RegexOptions.IgnoreCase))
                return parts[i];
        }
        return "";
    }
}
