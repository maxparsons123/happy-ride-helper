using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaVaxVoIP.Config;
using AdaVaxVoIP.Models;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP.Services;

/// <summary>
/// BSQD dispatch and WhatsApp notification service.
/// </summary>
public sealed class Dispatcher
{
    private readonly ILogger<Dispatcher> _logger;
    private readonly DispatchSettings _settings;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Dispatcher(ILogger<Dispatcher> logger, DispatchSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<bool> DispatchAsync(BookingState booking, string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.BsqdWebhookUrl)) return false;

        try
        {
            var payload = new
            {
                phoneNumber = FormatPhone(phoneNumber),
                name = booking.Name,
                pickup = booking.PickupFormatted ?? booking.Pickup,
                pickupStraat = booking.PickupStreet,
                pickupPlaats = booking.PickupCity,
                pickupLat = booking.PickupLat,
                pickupLon = booking.PickupLon,
                destination = booking.DestFormatted ?? booking.Destination,
                destStraat = booking.DestStreet,
                destPlaats = booking.DestCity,
                destLat = booking.DestLat,
                destLon = booking.DestLon,
                passengers = booking.Passengers ?? 1,
                pickupTime = booking.PickupTime,
                fare = booking.Fare,
                eta = booking.Eta,
                bookingRef = booking.BookingRef
            };

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.BsqdWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode) { _logger.LogInformation("Dispatch sent: {Ref}", booking.BookingRef); return true; }

            _logger.LogWarning("Dispatch failed: {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) { _logger.LogError(ex, "Dispatch error"); return false; }
    }

    public async Task<bool> SendWhatsAppAsync(string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.WhatsAppWebhookUrl) || string.IsNullOrWhiteSpace(phoneNumber)) return false;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.WhatsAppWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new { phoneNumber = FormatPhone(phoneNumber) }), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogError(ex, "WhatsApp error"); return false; }
    }

    private static string FormatPhone(string phone)
    {
        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (clean.StartsWith("+")) clean = "00" + clean[1..];
        if (!clean.StartsWith("00"))
        {
            if (clean.StartsWith("0")) clean = "0031" + clean[1..];
            else clean = "00" + clean;
        }
        return new string(clean.Where(char.IsDigit).ToArray());
    }
}
