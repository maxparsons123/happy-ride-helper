using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WhatsAppTaxiBooker.Config;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// WhatsApp Business Cloud API client for sending messages.
/// </summary>
public sealed class WhatsAppService
{
    private readonly WhatsAppConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string BaseUrl = "https://graph.facebook.com/v21.0";

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public WhatsAppService(WhatsAppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Send a text message to a WhatsApp user.
    /// </summary>
    public async Task<bool> SendTextAsync(string to, string message)
    {
        Log($"📤 [WhatsApp] Sending to {to}: {message[..Math.Min(80, message.Length)]}...");
        try
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = message }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{BaseUrl}/{_config.PhoneNumberId}/messages";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            req.Content = content;

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                Log($"✅ [WhatsApp] Message sent to {to}");
                return true;
            }

            Log($"⚠️ [WhatsApp] Send failed {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"❌ [WhatsApp] Send error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get the download URL for a WhatsApp media file (voice message, image, etc).
    /// </summary>
    public async Task<(string url, string mimeType)?> GetMediaUrlAsync(string mediaId)
    {
        try
        {
            var url = $"{BaseUrl}/{mediaId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Log($"⚠️ [WhatsApp] Media lookup failed: {body[..Math.Min(200, body.Length)]}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var mediaUrl = doc.RootElement.GetProperty("url").GetString()!;
            var mimeType = doc.RootElement.GetProperty("mime_type").GetString() ?? "audio/ogg";
            return (mediaUrl, mimeType);
        }
        catch (Exception ex)
        {
            Log($"❌ [WhatsApp] Media error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Mark a message as read (blue ticks).
    /// </summary>
    public async Task MarkReadAsync(string messageId)
    {
        try
        {
            var payload = new
            {
                messaging_product = "whatsapp",
                status = "read",
                message_id = messageId
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{BaseUrl}/{_config.PhoneNumberId}/messages";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            req.Content = content;
            await _http.SendAsync(req);
        }
        catch { /* best effort */ }
    }
}
