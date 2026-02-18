using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Publishes confirmed bookings to the Dispatch System via MQTT (same format as the SIP bridge).
/// Uses raw WebSocket to HiveMQ since System.Net.WebSockets is available in .NET 8.
/// </summary>
public sealed class MqttDispatcher : IDisposable
{
    private readonly MqttConfig _config;
    private ClientWebSocket? _ws;
    private bool _connected;

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public bool IsConnected => _connected;

    public MqttDispatcher(MqttConfig config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        try
        {
            _ws = new ClientWebSocket();
            _ws.Options.AddSubProtocol("mqtt");
            await _ws.ConnectAsync(new Uri(_config.BrokerUrl), CancellationToken.None);

            // Send MQTT CONNECT packet
            var connectPacket = BuildMqttConnect($"wa-booker-{Environment.MachineName[..Math.Min(8, Environment.MachineName.Length)]}");
            await _ws.SendAsync(connectPacket, WebSocketMessageType.Binary, true, CancellationToken.None);

            // Read CONNACK
            var buf = new byte[4];
            var result = await _ws.ReceiveAsync(buf, CancellationToken.None);
            if (result.Count >= 4 && buf[0] == 0x20 && buf[3] == 0x00)
            {
                _connected = true;
                Log("🔌 [MQTT] Connected to broker");
            }
            else
            {
                Log($"⚠️ [MQTT] CONNACK failed: {BitConverter.ToString(buf[..result.Count])}");
            }
        }
        catch (Exception ex)
        {
            Log($"❌ [MQTT] Connect error: {ex.Message}");
        }
    }

    /// <summary>
    /// Publish a confirmed booking to the dispatch system topic.
    /// Uses the same JSON schema as the SIP bridge for compatibility.
    /// </summary>
    public async Task PublishBookingAsync(Booking booking)
    {
        if (!_connected || _ws == null)
        {
            Log("⚠️ [MQTT] Not connected, attempting reconnect...");
            await ConnectAsync();
            if (!_connected) { Log("❌ [MQTT] Reconnect failed"); return; }
        }

        var topic = $"{_config.TopicPrefix}/{booking.Id}";

        // Payload matches the driver app job schema (v2026)
        var payload = new
        {
            job = booking.Id,
            lat = booking.PickupLat ?? 0.0,
            lng = booking.PickupLng ?? 0.0,
            pickupAddress = booking.Pickup,
            dropoff = booking.Destination,
            dropoffLat = booking.DropoffLat ?? 0.0,
            dropoffLng = booking.DropoffLng ?? 0.0,
            biddingWindowSec = 45,
            passengers = booking.Passengers.ToString(),
            customerName = booking.CallerName ?? "WhatsApp Customer",
            customerPhone = booking.Phone,
            fare = booking.Fare ?? "",
            notes = booking.Notes ?? "",
            temp1 = booking.PickupTime ?? "",
            temp2 = "whatsapp",
            temp3 = ""
        };

        var json = JsonSerializer.Serialize(payload);
        var packet = BuildMqttPublish(topic, json);
        await _ws.SendAsync(packet, WebSocketMessageType.Binary, true, CancellationToken.None);
        Log($"📨 [MQTT] Dispatched {booking.Id} to {topic}");

        // Also publish to passengers/ topic for driver app compatibility
        var passengerTopic = $"passengers/{booking.Id}/created";
        var pPayload = new
        {
            jobId = booking.Id,
            customerName = booking.CallerName ?? "WhatsApp Customer",
            customerPhone = booking.Phone,
            customerbooktim = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            pickupLat = booking.PickupLat ?? 0.0,
            pickupLng = booking.PickupLng ?? 0.0,
            pickupAddress = booking.Pickup,
            dropAddress = booking.Destination,
            dropofflat = booking.DropoffLat ?? 0.0,
            dropofflon = booking.DropoffLng ?? 0.0,
            status = "WAITING"
        };
        var pJson = JsonSerializer.Serialize(pPayload);
        var pPacket = BuildMqttPublish(passengerTopic, pJson);
        await _ws.SendAsync(pPacket, WebSocketMessageType.Binary, true, CancellationToken.None);
        Log($"📨 [MQTT] Also published to {passengerTopic}");
    }

    // ── Minimal MQTT packet builders ──

    private static byte[] BuildMqttConnect(string clientId)
    {
        var clientIdBytes = Encoding.UTF8.GetBytes(clientId);
        var remainingLength = 10 + 2 + clientIdBytes.Length; // variable header + client id

        using var ms = new MemoryStream();
        ms.WriteByte(0x10); // CONNECT
        WriteRemainingLength(ms, remainingLength);

        // Protocol name "MQTT"
        ms.WriteByte(0x00); ms.WriteByte(0x04);
        ms.Write(Encoding.UTF8.GetBytes("MQTT"));

        // Protocol level 4 (MQTT 3.1.1)
        ms.WriteByte(0x04);

        // Connect flags: clean session
        ms.WriteByte(0x02);

        // Keep alive: 60s
        ms.WriteByte(0x00); ms.WriteByte(0x3C);

        // Client ID
        ms.WriteByte((byte)(clientIdBytes.Length >> 8));
        ms.WriteByte((byte)(clientIdBytes.Length & 0xFF));
        ms.Write(clientIdBytes);

        return ms.ToArray();
    }

    private static byte[] BuildMqttPublish(string topic, string payload)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var remainingLength = 2 + topicBytes.Length + payloadBytes.Length;

        using var ms = new MemoryStream();
        ms.WriteByte(0x30); // PUBLISH, QoS 0
        WriteRemainingLength(ms, remainingLength);

        // Topic
        ms.WriteByte((byte)(topicBytes.Length >> 8));
        ms.WriteByte((byte)(topicBytes.Length & 0xFF));
        ms.Write(topicBytes);

        // Payload
        ms.Write(payloadBytes);
        return ms.ToArray();
    }

    private static void WriteRemainingLength(MemoryStream ms, int length)
    {
        do
        {
            var encodedByte = length % 128;
            length /= 128;
            if (length > 0) encodedByte |= 0x80;
            ms.WriteByte((byte)encodedByte);
        } while (length > 0);
    }

    public void Dispose()
    {
        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(2000); }
        catch { /* best effort */ }
        _ws?.Dispose();
    }
}
