using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaMain.Config;
using AdaMain.Core;
using Microsoft.Extensions.Logging;

namespace AdaMain.Services;

/// <summary>
/// BSQD dispatch and WhatsApp notification service.
/// </summary>
public sealed class BsqdDispatcher : IDispatcher
{
    private readonly ILogger<BsqdDispatcher> _logger;
    private readonly DispatchSettings _settings;
    private readonly HttpClient _httpClient;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    public BsqdDispatcher(ILogger<BsqdDispatcher> logger, DispatchSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }
    
    public async Task<bool> DispatchAsync(BookingState booking, string phoneNumber)
    {
        if (string.IsNullOrEmpty(_settings.BsqdWebhookUrl))
        {
            _logger.LogWarning("BSQD webhook URL not configured");
            return false;
        }
        
        try
        {
            var payload = new TaxiBotRequest
            {
                PhoneNumber = FormatPhone(phoneNumber),
                Name = booking.Name,
                Pickup = booking.PickupFormatted ?? booking.Pickup,
                PickupStraat = booking.PickupStreet,
                PickupHuisnr = ParseInt(booking.PickupNumber),
                PickupPlaats = booking.PickupCity,
                PickupLat = booking.PickupLat,
                PickupLon = booking.PickupLon,
                Destination = booking.DestFormatted ?? booking.Destination,
                DestStraat = booking.DestStreet,
                DestHuisnr = ParseInt(booking.DestNumber),
                DestPlaats = booking.DestCity,
                DestLat = booking.DestLat,
                DestLon = booking.DestLon,
                Passengers = booking.Passengers ?? 1,
                PickupTime = booking.PickupTime,
                Fare = booking.Fare,
                Eta = booking.Eta,
                BookingRef = booking.BookingRef
            };
            
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.BsqdWebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BsqdApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Dispatch sent: {BookingRef}", booking.BookingRef);
                return true;
            }
            
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Dispatch failed: {Status} - {Error}", (int)response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch error");
            return false;
        }
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
            var formatted = FormatPhone(phoneNumber);
            
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
    
    private static string FormatPhone(string phone)
    {
        var clean = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        
        if (clean.StartsWith("+")) clean = "00" + clean[1..];
        if (!clean.StartsWith("00"))
        {
            if (clean.StartsWith("06") || clean.StartsWith("0"))
                clean = "0031" + clean[1..];
            else
                clean = "00" + clean;
        }
        if (clean.StartsWith("00310")) clean = "0031" + clean[5..];
        
        return new string(clean.Where(char.IsDigit).ToArray());
    }
    
    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }
    
    private sealed class TaxiBotRequest
    {
        public string? PhoneNumber { get; set; }
        public string? Name { get; set; }
        public string? Pickup { get; set; }
        public string? PickupStraat { get; set; }
        public int? PickupHuisnr { get; set; }
        public string? PickupPlaats { get; set; }
        public double? PickupLat { get; set; }
        public double? PickupLon { get; set; }
        public string? Destination { get; set; }
        public string? DestStraat { get; set; }
        public int? DestHuisnr { get; set; }
        public string? DestPlaats { get; set; }
        public double? DestLat { get; set; }
        public double? DestLon { get; set; }
        public int? Passengers { get; set; }
        public string? PickupTime { get; set; }
        public string? Fare { get; set; }
        public string? Eta { get; set; }
        public string? BookingRef { get; set; }
    }
}
