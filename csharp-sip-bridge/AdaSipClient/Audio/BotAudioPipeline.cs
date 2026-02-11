using System.Buffers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AdaSipClient.Core;

namespace AdaSipClient.Audio;

/// <summary>
/// AutoBot pipeline: forwards caller A-law audio to OpenAI Realtime API (G.711 passthrough)
/// and emits AI-generated A-law audio back to the caller.
/// Uses native G.711 A-law codec on the OpenAI side — no transcoding needed.
/// </summary>
public sealed class BotAudioPipeline : IAudioPipeline
{
    private readonly AppState _state;
    private readonly ILogSink _log;
    private readonly VolumeControl _inputGain = new();
    private readonly VolumeControl _outputGain = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _sessionReady;

    public event Action<byte[]>? OnOutputAudio;

    public BotAudioPipeline(AppState state, ILogSink log)
    {
        _state = state;
        _log = log;
        _inputGain.VolumePercent = state.InputVolumePercent;
        _outputGain.VolumePercent = state.OutputVolumePercent;

        state.StateChanged += () =>
        {
            _inputGain.VolumePercent = state.InputVolumePercent;
            _outputGain.VolumePercent = state.OutputVolumePercent;
        };
    }

    public async Task StartAsync()
    {
        var apiKey = _state.OpenAiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogError("[Bot] No OpenAI API key configured");
            return;
        }

        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var model = "gpt-4o-mini-realtime-preview";
        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={model}");

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);
            _log.Log($"[Bot] Connected to OpenAI Realtime ({model})");

            // Configure session for G.711 A-law passthrough
            await SendSessionConfig();

            // Start receive loop
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogError($"[Bot] Connection failed: {ex.Message}");
        }
    }

    private static readonly byte[] s_audioPrefix =
        Encoding.UTF8.GetBytes("{\"type\":\"input_audio_buffer.append\",\"audio\":\"");
    private static readonly byte[] s_audioSuffix =
        Encoding.UTF8.GetBytes("\"}");

    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    public void IngestCallerAudio(byte[] alawFrame)
    {
        if (_disposed || _ws?.State != WebSocketState.Open || !_sessionReady) return;

        // Apply input volume
        var frame = (byte[])alawFrame.Clone();
        _inputGain.ApplyInPlace(frame);

        // Zero-alloc JSON: manually write envelope with pooled buffer
        var b64Len = Base64.GetMaxEncodedToUtf8Length(frame.Length);
        var totalLen = s_audioPrefix.Length + b64Len + s_audioSuffix.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLen);

        s_audioPrefix.CopyTo(buffer, 0);
        Base64.EncodeToUtf8(frame, buffer.AsSpan(s_audioPrefix.Length), out _, out var written);
        s_audioSuffix.CopyTo(buffer, s_audioPrefix.Length + written);

        var finalLen = s_audioPrefix.Length + written + s_audioSuffix.Length;
        _ = SendPooledAsync(buffer, finalLen);
    }

    private async Task SendPooledAsync(byte[] pooledBuffer, int length)
    {
        try
        {
            await SendBytesAsync(pooledBuffer.AsMemory(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None).Wait(2000); }
        catch { /* ignore */ }
        _log.Log("[Bot] Pipeline stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _ws?.Dispose();
        _cts?.Dispose();
        _sendMutex.Dispose();
    }

    // ── Private ──

    private async Task SendSessionConfig()
    {
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "audio", "text" },
                input_audio_format = "g711_alaw",
                output_audio_format = "g711_alaw",
                voice = "alloy",
                turn_detection = new
                {
                    type = "server_vad",
                    silence_duration_ms = 500,
                    prefix_padding_ms = 300,
                    threshold = 0.5
                },
                input_audio_transcription = new
                {
                    model = "whisper-1"
                }
            }
        };

        await SendAsync(JsonSerializer.Serialize(config));
        _log.Log("[Bot] Session configured (G.711 A-law passthrough)");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _log.LogError($"[Bot] Receive error: {ex.Message}");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "session.created":
                case "session.updated":
                    _sessionReady = true;
                    _log.Log($"[Bot] {type}");
                    break;

                case "response.audio.delta":
                    var audioB64 = doc.RootElement.GetProperty("delta").GetString();
                    if (audioB64 != null)
                    {
                        var alawOut = Convert.FromBase64String(audioB64);
                        _outputGain.ApplyInPlace(alawOut);
                        OnOutputAudio?.Invoke(alawOut);
                    }
                    break;

                case "response.audio_transcript.delta":
                    var transcript = doc.RootElement.GetProperty("delta").GetString();
                    if (!string.IsNullOrEmpty(transcript))
                        _log.Log($"[AI] {transcript}");
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var t))
                        _log.Log($"[Caller] {t.GetString()}");
                    break;

                case "error":
                    var errMsg = doc.RootElement.GetProperty("error")
                        .GetProperty("message").GetString();
                    _log.LogError($"[Bot] API error: {errMsg}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[Bot] Parse error: {ex.Message}");
        }
    }

    private async Task SendAsync(string message)
    {
        if (_ws?.State != WebSocketState.Open) return;
        await SendBytesAsync(Encoding.UTF8.GetBytes(message));
    }

    private async Task SendBytesAsync(ReadOnlyMemory<byte> bytes)
    {
        if (_ws?.State != WebSocketState.Open) return;
        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _cts?.Token ?? CancellationToken.None);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogError("[Bot] WebSocket send timed out (5s)");
        }
        finally
        {
            _sendMutex.Release();
        }
    }
}
