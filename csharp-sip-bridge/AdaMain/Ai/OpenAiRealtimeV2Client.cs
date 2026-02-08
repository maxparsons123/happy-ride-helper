using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AdaMain.Ai;

/// <summary>
/// OpenAI Realtime API client using the NEW session schema (v2).
///
/// Key differences from v1 (OpenAiG711Client):
/// - Nested session config: session.audio.input/output (not flat input_audio_format)
/// - Semantic VAD with eagerness control (not threshold-based server_vad)
/// - Supports both G.711 μ-law (audio/pcmu) and A-law (audio/pcma)
/// - Near-field noise reduction built-in
/// - Model name: "gpt-realtime" (not "gpt-4o-mini-realtime-preview")
/// - No "OpenAI-Beta: realtime=v1" header (connects to new API)
///
/// Audio flow (zero-resampling):
///   SIP (8kHz G.711) → base64 → OpenAI → base64 → G.711 (8kHz) → SIP
///
/// Usage:
///   var client = new OpenAiRealtimeV2Client(logger, config);
///   client.OnAudio += alawBytes => playout.Enqueue(alawBytes);
///   client.OnToolCall += (name, args) => HandleTool(name, args);
///   await client.ConnectAsync("caller-id");
///   client.SendAudio(alawRtpPayload);
/// </summary>
public sealed class OpenAiRealtimeV2Client : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "2.0-v2schema";

    // ── Config ──
    private readonly ILogger _logger;
    private readonly V2Config _config;

    // ── WebSocket ──
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    // ── Thread-safe state ──
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _responseQueued;
    private int _greetingSent;
    private int _transcriptPending;
    private int _deferredResponsePending;
    private int _noReplyWatchdogId;
    private int _noReplyCount;
    private int _ignoreUserAudio;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // ── Events ──
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _ws?.State == WebSocketState.Open;
    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;
    public event Action? OnBargeIn;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Constructor ──

    public OpenAiRealtimeV2Client(ILogger logger, V2Config config)
    {
        _logger = logger;
        _config = config;
    }

    // ── Connect / Disconnect ──

    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAiRealtimeV2Client));

        ResetCallState();

        _ws = new ClientWebSocket();
        // NOTE: No "OpenAI-Beta: realtime=v1" header — new API doesn't use it
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_config.Model}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        _logger.LogInformation("[V2] Connecting (model={Model}, codec={Codec}, caller={Caller})",
            _config.Model, _config.AudioFormat, callerId);

        await _ws.ConnectAsync(uri, linked.Token);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        _logger.LogInformation("[V2] Connected — waiting for session.created");
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        SignalCallEnded("disconnect");
        _cts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { /* best effort */ }
        }

        _ws?.Dispose();
        _ws = null;
        _logger.LogInformation("[V2] Disconnected");
    }

    // ── Audio I/O ──

    /// <summary>
    /// Send G.711 audio bytes (A-law or μ-law, matching <see cref="V2Config.AudioFormat"/>).
    /// No resampling needed — 8kHz passthrough.
    /// </summary>
    public void SendAudio(byte[] g711Frame)
    {
        if (!IsConnected || g711Frame.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        var msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(g711Frame)
        });
        _ = SendBytesAsync(msg);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        await SendJsonAsync(new { type = "response.cancel" });
    }

    /// <summary>Called by playout engine when audio queue drains.</summary>
    public void NotifyPlayoutComplete()
    {
        OnPlayoutComplete?.Invoke();
        StartNoReplyWatchdog();
    }

    // ── Receive Loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (IsConnected && !ct.IsCancellationRequested)
            {
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    await ProcessMessageAsync(sb.ToString());
                    sb.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[V2] Receive error"); }

        OnEnded?.Invoke("connection_closed");
    }

    // ── Message Handler ──

    private async Task ProcessMessageAsync(string json)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == null) return;

            switch (type)
            {
                case "session.created":
                    await ConfigureSessionAsync();
                    break;

                case "session.updated":
                    _logger.LogInformation("[V2] Session configured ({Format}, semantic_vad, v{Version})",
                        _config.AudioFormat, VERSION);
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                        await SendGreetingAsync();
                    break;

                case "response.created":
                    HandleResponseCreated(doc.RootElement);
                    break;

                case "response.done":
                    HandleResponseDone(doc.RootElement);
                    break;

                // Audio delta — raw G.711 bytes, pass through directly
                case "response.audio.delta":
                case "response.output_audio.delta": // new schema event name
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                    {
                        var b64 = delta.GetString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            var audioBytes = Convert.FromBase64String(b64);
                            if (audioBytes.Length > 0)
                                OnAudio?.Invoke(audioBytes);
                        }
                    }
                    break;

                // AI transcript
                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var aiText))
                    {
                        var text = aiText.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            OnTranscript?.Invoke("Ada", text);
                            CheckGoodbye(text);
                        }
                    }
                    break;

                // User transcript
                case "conversation.item.input_audio_transcription.completed":
                    Interlocked.Exchange(ref _transcriptPending, 0);
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var text = userText.GetString();
                        if (!string.IsNullOrEmpty(text))
                            OnTranscript?.Invoke("User", text);
                    }
                    if (Volatile.Read(ref _responseActive) == 0)
                        _ = QueueResponseCreateAsync(delayMs: 50);
                    break;

                // Barge-in
                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId);
                    Interlocked.Exchange(ref _noReplyCount, 0);
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    OnBargeIn?.Invoke();
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    break;

                // Tool calls
                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "?";
                        if (!string.IsNullOrEmpty(msg) &&
                            !msg.Contains("buffer too small", StringComparison.OrdinalIgnoreCase))
                            _logger.LogWarning("[V2] API error: {Error}", msg);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[V2] Parse error: {Error}", ex.Message);
        }
    }

    // ── Session Configuration (NEW SCHEMA) ──

    private async Task ConfigureSessionAsync()
    {
        _logger.LogInformation("[V2] Configuring session: format={Format}, voice={Voice}, eagerness={Eagerness}",
            _config.AudioFormat, _config.Voice, _config.VadEagerness);

        var config = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = _config.Model,
                output_modalities = new[] { "audio", "text" },
                instructions = _config.Instructions,
                audio = new
                {
                    input = new
                    {
                        format = new { type = _config.AudioFormat },
                        transcription = new { model = "whisper-1" },
                        noise_reduction = new { type = "near_field" },
                        turn_detection = new
                        {
                            type = "semantic_vad",
                            create_response = true,
                            eagerness = _config.VadEagerness
                        }
                    },
                    output = new
                    {
                        format = new { type = _config.AudioFormat },
                        voice = _config.Voice
                    }
                },
                tools = _config.Tools,
                tool_choice = "auto",
                temperature = _config.Temperature
            }
        };

        await SendJsonAsync(config);
    }

    private async Task SendGreetingAsync()
    {
        if (string.IsNullOrEmpty(_config.Greeting)) return;

        await Task.Delay(150); // Let session settle

        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                instructions = _config.Greeting,
                conversation = "none",
                output_modalities = new[] { "audio" },
                metadata = new { response_purpose = "greeting" }
            }
        });

        _logger.LogInformation("[V2] Greeting sent");
    }

    // ── Response Lifecycle ──

    private void HandleResponseCreated(JsonElement root)
    {
        var id = TryGetResponseId(root);
        if (id != null && _activeResponseId == id) return;
        _activeResponseId = id;
        Interlocked.Exchange(ref _responseActive, 1);
        _ = SendJsonAsync(new { type = "input_audio_buffer.clear" });
    }

    private void HandleResponseDone(JsonElement root)
    {
        var id = TryGetResponseId(root);
        if (id == null || id == _lastCompletedResponseId) return;
        if (_activeResponseId != null && id != _activeResponseId) return;

        _lastCompletedResponseId = id;
        _activeResponseId = null;
        Interlocked.Exchange(ref _responseActive, 0);

        // Flush deferred response
        if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                if (Volatile.Read(ref _callEnded) == 0 && IsConnected)
                    await SendJsonAsync(new { type = "response.create" });
            });
        }
    }

    // ── Tool Handling ──

    private async Task HandleToolCallAsync(JsonElement root)
    {
        var now = NowMs();
        if (now - Volatile.Read(ref _lastToolCallAt) < 200) return;
        Volatile.Write(ref _lastToolCallAt, now);

        var callId = root.TryGetProperty("call_id", out var c) ? c.GetString() : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
        var argsStr = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";

        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name)) return;

        _logger.LogInformation("[V2] Tool: {Name}", name);

        Dictionary<string, object?> args;
        try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}") ?? new(); }
        catch { args = new(); }

        var result = OnToolCall != null
            ? await OnToolCall.Invoke(name, args)
            : new { error = "No handler" };

        // Send tool result
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(result)
            }
        });

        Interlocked.Exchange(ref _responseActive, 0);

        // State grounding injection
        if (name == "sync_booking_data" && IsConnected)
            await InjectStateGrounding(args);

        // Immediate follow-up response (bypass transcript guard)
        if (IsConnected)
            await QueueResponseCreateAsync(delayMs: 10, bypassTranscriptGuard: true);
    }

    private async Task InjectStateGrounding(Dictionary<string, object?> args)
    {
        var fields = new List<string>();
        if (args.TryGetValue("caller_name", out var cn) && cn != null) fields.Add($"Name: {cn}");
        if (args.TryGetValue("pickup", out var pu) && pu != null) fields.Add($"Pickup: {pu}");
        if (args.TryGetValue("destination", out var dt) && dt != null) fields.Add($"Destination: {dt}");
        if (args.TryGetValue("passengers", out var px) && px != null) fields.Add($"Passengers: {px}");
        if (args.TryGetValue("pickup_time", out var pt) && pt != null) fields.Add($"Time: {pt}");

        if (fields.Count == 0) return;

        var stateText = $"[BOOKING STATE UPDATE] {string.Join(", ", fields)}";
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text = stateText } }
            }
        });
    }

    // ── No-Reply Watchdog ──

    private void StartNoReplyWatchdog()
    {
        var id = Interlocked.Increment(ref _noReplyWatchdogId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(_config.NoReplyTimeoutMs);

            if (Volatile.Read(ref _noReplyWatchdogId) != id) return;
            if (Volatile.Read(ref _callEnded) != 0 || !IsConnected) return;

            var count = Interlocked.Increment(ref _noReplyCount);
            if (count >= _config.MaxNoReplyPrompts)
            {
                SignalCallEnded("no_reply_timeout");
                OnEnded?.Invoke("no_reply_timeout");
                return;
            }

            _logger.LogInformation("[V2] No-reply watchdog ({Count}/{Max})", count, _config.MaxNoReplyPrompts);

            await SendJsonAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                        new { type = "input_text", text = "[SILENCE DETECTED] The user has not responded. Say something brief like 'Are you still there?'" }
                    }
                }
            });
            await Task.Delay(20);
            await SendJsonAsync(new { type = "response.create" });
        });
    }

    // ── Goodbye Detection ──

    private void CheckGoodbye(string text)
    {
        if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("Taxibot", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Thank you for using", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("for calling", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(_config.GoodbyeGraceMs);
                if (Volatile.Read(ref _callEnded) != 0) return;
                Interlocked.Exchange(ref _ignoreUserAudio, 1);
                SignalCallEnded("goodbye_watchdog");
                OnEnded?.Invoke("goodbye_watchdog");
            });
        }
    }

    // ── Response Gate ──

    private async Task QueueResponseCreateAsync(int delayMs = 40, bool bypassTranscriptGuard = false)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1) return;

        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);

            if (Volatile.Read(ref _responseActive) == 1)
            {
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                return;
            }

            var canCreate = Volatile.Read(ref _responseActive) == 0 &&
                            Volatile.Read(ref _callEnded) == 0 &&
                            IsConnected &&
                            (bypassTranscriptGuard || Volatile.Read(ref _transcriptPending) == 0);

            if (!canCreate) return;

            await SendJsonAsync(new { type = "response.create" });
        }
        finally
        {
            Interlocked.Exchange(ref _responseQueued, 0);
        }
    }

    // ── Helpers ──

    private void ResetCallState()
    {
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _noReplyCount, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);
        _activeResponseId = null;
        _lastCompletedResponseId = null;
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 0)
            _logger.LogInformation("[V2] Call ended: {Reason}", reason);
    }

    private static string? TryGetResponseId(JsonElement root)
        => root.TryGetProperty("response", out var r) && r.TryGetProperty("id", out var id)
            ? id.GetString() : null;

    private async Task SendJsonAsync(object obj)
        => await SendBytesAsync(JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts));

    private async Task SendBytesAsync(byte[] bytes)
    {
        if (_ws?.State != WebSocketState.Open) return;
        await _sendMutex.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None); }
        finally { _sendMutex.Release(); }
    }

    // ── Dispose ──

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 0)
            await DisconnectAsync();
        _sendMutex.Dispose();
    }
}

/// <summary>
/// Configuration for <see cref="OpenAiRealtimeV2Client"/>.
/// Separates config from behavior for clean reuse.
/// </summary>
public sealed class V2Config
{
    /// <summary>OpenAI API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Model name. Default: "gpt-realtime" (new API). Can also use "gpt-4o-realtime-preview".</summary>
    public string Model { get; init; } = "gpt-realtime";

    /// <summary>
    /// Audio format for G.711 passthrough.
    /// "audio/pcmu" = μ-law (North America), "audio/pcma" = A-law (Europe).
    /// For old-schema models, use "g711_alaw" or "g711_ulaw" instead.
    /// </summary>
    public string AudioFormat { get; init; } = "audio/pcma";

    /// <summary>Voice name (alloy, shimmer, echo, ash, ballad, coral, sage, verse, marin).</summary>
    public string Voice { get; init; } = "marin";

    /// <summary>System prompt / instructions for the AI.</summary>
    public string Instructions { get; init; } = "You are a helpful assistant.";

    /// <summary>Optional greeting to speak when call connects.</summary>
    public string? Greeting { get; init; }

    /// <summary>
    /// Semantic VAD eagerness: "low", "medium", or "high".
    /// Low = waits longer before detecting end of speech (fewer false cuts).
    /// High = responds faster but may cut off user mid-sentence.
    /// </summary>
    public string VadEagerness { get; init; } = "medium";

    /// <summary>Model temperature (0.0-1.5).</summary>
    public double Temperature { get; init; } = 0.8;

    /// <summary>No-reply timeout in ms.</summary>
    public int NoReplyTimeoutMs { get; init; } = 15000;

    /// <summary>Max no-reply prompts before hangup.</summary>
    public int MaxNoReplyPrompts { get; init; } = 3;

    /// <summary>Goodbye grace period in ms.</summary>
    public int GoodbyeGraceMs { get; init; } = 5000;

    /// <summary>Tool definitions (same format as OpenAI function calling).</summary>
    public object[]? Tools { get; init; }
}
