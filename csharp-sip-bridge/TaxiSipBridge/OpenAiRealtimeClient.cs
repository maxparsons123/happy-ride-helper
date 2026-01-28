using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API WebSocket client for voice conversations.
/// Handles bidirectional PCM24 audio streaming.
/// </summary>
public class OpenAiRealtimeClient : IDisposable
{
    private const string OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-10-01";

    private readonly string _apiKey;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _sessionConfigured;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnPcm24Audio;

    public OpenAiRealtimeClient(string apiKey)
    {
        _apiKey = apiKey;
    }

    private void Log(string msg) => OnLog?.Invoke($"[OpenAI] {msg}");

    public async Task ConnectAsync(string caller, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        Log("Connecting to OpenAI Realtime API...");
        await _ws.ConnectAsync(new Uri(OPENAI_REALTIME_URL), _cts.Token);
        Log("WebSocket connected");

        // Start receive loop
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("WebSocket closed by server");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Log($"WebSocket error: {ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "session.created":
                    Log("Session created, configuring...");
                    _ = SendSessionConfigAsync();
                    break;

                case "session.updated":
                    Log("Session configured");
                    _sessionConfigured = true;
                    break;

                case "response.audio.delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        var base64 = delta.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            var pcm24 = Convert.FromBase64String(base64);
                            OnPcm24Audio?.Invoke(pcm24);
                        }
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (root.TryGetProperty("delta", out var transcript))
                    {
                        OnTranscript?.Invoke(transcript.GetString() ?? "");
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Log("User started speaking");
                    break;

                case "input_audio_buffer.speech_stopped":
                    Log("User stopped speaking");
                    break;

                case "error":
                    if (root.TryGetProperty("error", out var error))
                    {
                        var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown";
                        Log($"❌ Error: {msg}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Error processing message: {ex.Message}");
        }
    }

    private async Task SendSessionConfigAsync()
    {
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = @"You are Ada, a friendly AI assistant for a taxi booking service. 
Help callers book taxis by collecting:
1. Pickup location
2. Destination
3. Number of passengers
4. Pickup time (now or scheduled)

Be conversational, confirm details, and keep responses brief for phone calls.",
                voice = "alloy",
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 800
                },
                temperature = 0.8
            }
        };

        await SendJsonAsync(config);
    }

    /// <summary>
    /// Send PCM16 audio (24kHz) to OpenAI input buffer.
    /// </summary>
    public void SendAudioToModel(byte[] pcm24k)
    {
        if (!_sessionConfigured || _ws?.State != WebSocketState.Open)
            return;

        var base64 = Convert.ToBase64String(pcm24k);
        var msg = new { type = "input_audio_buffer.append", audio = base64 };

        _ = SendJsonAsync(msg);
    }

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"Send error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }

        GC.SuppressFinalize(this);
    }
}
