using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API WebSocket client for voice conversations.
/// Handles bidirectional PCM16@16kHz audio streaming.
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
    /// <summary>Fired when OpenAI sends PCM16@16kHz audio (short array).</summary>
    public event Action<short[]>? OnPcm16kAudio;

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
                            // OpenAI sends PCM16 @ 24kHz, we need to resample to 16kHz
                            var pcm24kBytes = Convert.FromBase64String(base64);
                            var pcm16k = Resample24kTo16k(pcm24kBytes);
                            OnPcm16kAudio?.Invoke(pcm16k);
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
    /// Send PCM16 audio (16kHz shorts) to OpenAI input buffer.
    /// Resamples to 24kHz before sending.
    /// </summary>
    public void SendPcm16kToModel(short[] pcm16k)
    {
        if (!_sessionConfigured || _ws?.State != WebSocketState.Open)
            return;

        // Upsample 16kHz → 24kHz (1.5x)
        var pcm24k = Resample16kTo24k(pcm16k);

        // Convert to bytes
        var pcm24kBytes = new byte[pcm24k.Length * 2];
        Buffer.BlockCopy(pcm24k, 0, pcm24kBytes, 0, pcm24kBytes.Length);

        var base64 = Convert.ToBase64String(pcm24kBytes);
        var msg = new { type = "input_audio_buffer.append", audio = base64 };

        _ = SendJsonAsync(msg);
    }

    /// <summary>
    /// Simple 16kHz → 24kHz upsampling (1.5x linear interpolation).
    /// </summary>
    private static short[] Resample16kTo24k(short[] pcm16k)
    {
        // 16kHz → 24kHz = 3:2 ratio (output 3 samples for every 2 input)
        int outLen = (pcm16k.Length * 3) / 2;
        var output = new short[outLen];

        for (int i = 0; i < pcm16k.Length - 1; i++)
        {
            int outIdx = (i * 3) / 2;
            if (outIdx >= outLen) break;

            short s0 = pcm16k[i];
            short s1 = pcm16k[i + 1];

            if (i % 2 == 0)
            {
                // Output 2 samples for this input sample
                output[outIdx] = s0;
                if (outIdx + 1 < outLen)
                    output[outIdx + 1] = (short)((s0 + s1) / 2);
            }
            else
            {
                // Output 1 sample
                output[outIdx] = s0;
            }
        }

        return output;
    }

    /// <summary>
    /// Simple 24kHz → 16kHz downsampling (2:3 ratio).
    /// </summary>
    private static short[] Resample24kTo16k(byte[] pcm24kBytes)
    {
        int samples24k = pcm24kBytes.Length / 2;
        var pcm24k = new short[samples24k];
        Buffer.BlockCopy(pcm24kBytes, 0, pcm24k, 0, pcm24kBytes.Length);

        // 24kHz → 16kHz = 2:3 ratio (output 2 samples for every 3 input)
        int outLen = (samples24k * 2) / 3;
        var output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int srcIdx = (i * 3) / 2;
            if (srcIdx < samples24k)
                output[i] = pcm24k[srcIdx];
        }

        return output;
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
