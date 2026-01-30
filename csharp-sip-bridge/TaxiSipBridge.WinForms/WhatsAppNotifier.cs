using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TaxiSipBridge;

/// <summary>
/// Static helper for sending WhatsApp notifications via BSQD webhook.
/// </summary>
public static class WhatsAppNotifier
{
    private static readonly HttpClient _httpClient = new();
    private const string WebhookUrl = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/Avaya";
    private const string ApiKey = "sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v";

    /// <summary>
    /// Send WhatsApp booking notification. Returns (success, message).
    /// </summary>
    public static async Task<(bool Success, string Message)> SendAsync(string phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
            return (false, "No phone number provided");

        try
        {
            var formattedPhone = FormatPhone(phoneNumber);
            
            var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            
            var payload = new { phoneNumber = formattedPhone };
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
                return (true, $"Sent to {formattedPhone}");
            
            var errorBody = await response.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Format phone number with 00 international prefix.
    /// Examples: +31612345678 → 0031612345678, 0652328530 → 0031652328530
    /// </summary>
    public static string FormatPhone(string phone)
    {
        var clean = phone.Replace(" ", "").Replace("-", "");
        
        // Keep only digits and +
        clean = new string(clean.Where(c => char.IsDigit(c) || c == '+').ToArray());
        
        // Convert + prefix to 00
        if (clean.StartsWith("+"))
            clean = "00" + clean.Substring(1);
        
        // If no 00 prefix, add Dutch country code for local numbers
        if (!clean.StartsWith("00"))
        {
            if (clean.StartsWith("06") || clean.StartsWith("0"))
                clean = "0031" + clean.Substring(1);
            else
                clean = "00" + clean;
        }
        
        // Dutch: remove leading 0 after country code (00310 → 0031)
        if (clean.StartsWith("00310"))
            clean = "0031" + clean.Substring(5);
        
        return new string(clean.Where(char.IsDigit).ToArray());
    }
}
