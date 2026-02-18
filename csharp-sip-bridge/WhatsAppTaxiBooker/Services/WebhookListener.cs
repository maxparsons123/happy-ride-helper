using System.Net;
using System.Text;
using System.Text.Json;
using WhatsAppTaxiBooker.Config;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// HTTP listener for WhatsApp Cloud API webhook callbacks.
/// Handles GET (verify) and POST (incoming messages).
/// </summary>
public sealed class WebhookListener : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _verifyToken;
    private CancellationTokenSource? _cts;

    public event Action<string>? OnLog;
    public event Action<IncomingWhatsAppMessage>? OnMessage;

    private void Log(string msg) => OnLog?.Invoke(msg);

    public WebhookListener(WebhookConfig webhookConfig, string verifyToken)
    {
        _verifyToken = verifyToken;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{webhookConfig.Port}/webhook/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        Log($"🌐 Webhook listener started on {_listener.Prefixes.First()}");
        Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
        Log("🛑 Webhook listener stopped");
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { Log($"❌ Listener error: {ex.Message}"); }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET")
            {
                HandleVerification(ctx);
                return;
            }

            if (ctx.Request.HttpMethod == "POST")
            {
                await HandleIncomingMessage(ctx);
                return;
            }

            ctx.Response.StatusCode = 405;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Log($"❌ Request error: {ex.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
        }
    }

    private void HandleVerification(HttpListenerContext ctx)
    {
        var qs = ctx.Request.QueryString;
        var mode = qs["hub.mode"];
        var token = qs["hub.verify_token"];
        var challenge = qs["hub.challenge"];

        if (mode == "subscribe" && token == _verifyToken && challenge != null)
        {
            Log("✅ Webhook verified by Meta");
            var bytes = Encoding.UTF8.GetBytes(challenge);
            ctx.Response.StatusCode = 200;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        else
        {
            Log("⚠️ Webhook verification failed");
            ctx.Response.StatusCode = 403;
        }
        ctx.Response.Close();
    }

    private async Task HandleIncomingMessage(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();

        // Always respond 200 to Meta immediately
        ctx.Response.StatusCode = 200;
        ctx.Response.Close();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Navigate: entry[0].changes[0].value.messages[0]
            if (!root.TryGetProperty("entry", out var entries)) return;
            
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes)) continue;
                
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)) continue;
                    if (!value.TryGetProperty("messages", out var messages)) continue;

                    foreach (var msg in messages.EnumerateArray())
                    {
                        var parsed = ParseMessage(msg, value);
                        if (parsed != null)
                        {
                            Log($"📥 [{parsed.Type}] from {parsed.From}: {parsed.Text?[..Math.Min(80, parsed.Text.Length ?? 0)] ?? "(media)"}");
                            OnMessage?.Invoke(parsed);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"❌ Parse error: {ex.Message}");
        }
    }

    private IncomingWhatsAppMessage? ParseMessage(JsonElement msg, JsonElement value)
    {
        var type = msg.GetProperty("type").GetString() ?? "";
        var from = msg.GetProperty("from").GetString() ?? "";
        var msgId = msg.GetProperty("id").GetString() ?? "";

        // Get contact name if available
        string? contactName = null;
        if (value.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0)
        {
            if (contacts[0].TryGetProperty("profile", out var profile))
                contactName = profile.GetProperty("name").GetString();
        }

        return type switch
        {
            "text" => new IncomingWhatsAppMessage
            {
                MessageId = msgId,
                From = from,
                ContactName = contactName,
                Type = "text",
                Text = msg.GetProperty("text").GetProperty("body").GetString()
            },
            "audio" => new IncomingWhatsAppMessage
            {
                MessageId = msgId,
                From = from,
                ContactName = contactName,
                Type = "audio",
                MediaId = msg.GetProperty("audio").GetProperty("id").GetString(),
                MimeType = msg.GetProperty("audio").TryGetProperty("mime_type", out var mt) ? mt.GetString() : "audio/ogg"
            },
            "location" => new IncomingWhatsAppMessage
            {
                MessageId = msgId,
                From = from,
                ContactName = contactName,
                Type = "location",
                Latitude = msg.GetProperty("location").GetProperty("latitude").GetDouble(),
                Longitude = msg.GetProperty("location").GetProperty("longitude").GetDouble(),
                Text = msg.GetProperty("location").TryGetProperty("name", out var locName) ? locName.GetString() : null
            },
            _ => null
        };
    }

    public void Dispose() => Stop();
}

public sealed class IncomingWhatsAppMessage
{
    public string MessageId { get; set; } = "";
    public string From { get; set; } = "";
    public string? ContactName { get; set; }
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
    public string? MediaId { get; set; }
    public string? MimeType { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}
