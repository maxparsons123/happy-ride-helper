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
    private IMqttClient? _mqttClient;
    private readonly SemaphoreSlim _mqttLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public BsqdDispatcher(ILogger<BsqdDispatcher> logger, DispatchSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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
                departure_address = new { lat = booking.PickupLat ?? 0, lon = booking.PickupLon ?? 0, street_name = booking.PickupStreet ?? "", street_number = booking.PickupNumber ?? "", postal_code = booking.PickupPostalCode ?? "", city = booking.PickupCity ?? "", formatted_depa_address = booking.PickupFormatted ?? booking.Pickup ?? "" },
                destination_address = new { lat = booking.DestLat ?? 0, lon = booking.DestLon ?? 0, street_name = booking.DestStreet ?? "", street_number = booking.DestNumber ?? "", postal_code = booking.DestPostalCode ?? "", city = booking.DestCity ?? "", formatted_dest_address = booking.DestFormatted ?? booking.Destination ?? "" },
                departure_time = DateTimeOffset.Now.AddMinutes(5),
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

            var payload = JsonSerializer.Serialize(new
            {
                pickup = booking.PickupFormatted ?? booking.Pickup ?? "",
                dropoff = booking.DestFormatted ?? booking.Destination ?? "",
                passengers = booking.Passengers ?? 1,
                callerPhone = FormatE164(phoneNumber),
                callerName = booking.Name ?? "Customer",
                bookingRef = booking.BookingRef ?? ""
            });
            var msg = new MqttApplicationMessageBuilder().WithTopic("taxi/bookings").WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
            await _mqttClient!.PublishAsync(msg);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "MQTT error"); return false; }
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
        if (clean.StartsWith("0")) return "+31" + clean[1..];
        return "+31" + clean;
    }

    private static string ParseFare(string? fare)
    {
        if (string.IsNullOrWhiteSpace(fare)) return "0.00";
        var clean = new string(fare.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
        return decimal.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "0.00";
    }
}
