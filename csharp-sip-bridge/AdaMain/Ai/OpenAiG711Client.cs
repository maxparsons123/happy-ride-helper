using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AdaMain.Config;
using Microsoft.Extensions.Logging;

namespace AdaMain.Ai;

/// <summary>
/// OpenAI Realtime API client with native G.711 A-law passthrough (8k→8k).
/// Merged best-of from WinForms OpenAIRealtimeG711Client v7.5 + G711CallFeatures.
///
/// Features ported from WinForms:
/// - Response gate (CanCreateResponse + QueueResponseCreateAsync)
/// - Deferred response handling (queue if one is active)
/// - Transcript guard (wait for transcript before responding)
/// - Barge-in detection (speech_started/stopped, commit)
/// - State grounding injection after sync_booking_data
/// - Goodbye detection with hangup watchdog
/// - No-reply watchdog (re-prompts after silence)
/// - Keepalive loop for connection health
/// - Response ID deduplication
/// - Non-blocking async logger
/// - Echo guard based on playout completion
/// - Per-call state reset
///
/// Version 2.0 - Merged architecture
/// </summary>
public sealed class OpenAiG711Client : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "2.0-g711";

    // =========================
    // CONFIG
    // =========================
    private readonly ILogger<OpenAiG711Client> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);

    // =========================
    // THREAD-SAFE STATE (per-call, reset by ResetCallState)
    // =========================
    private int _disposed;                // Object lifetime only (NEVER reset)
    private int _callEnded;               // Per-call
    private int _responseActive;          // OBSERVED only (set by response.created/done)
    private int _responseQueued;          // Per-call
    private int _greetingSent;            // Per-call
    private int _ignoreUserAudio;         // Per-call (set after goodbye starts)
    private int _deferredResponsePending; // Per-call (queued response after response.done)
    private int _noReplyWatchdogId;       // Incremented to cancel stale watchdogs
    private int _transcriptPending;       // Block response.create until transcript arrives
    private int _noReplyCount;            // Per-call
    private long _lastAdaFinishedAt;      // RTP playout completion time (echo guard source of truth)
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt;      // For transcript guard
    private long _speechStoppedAt;        // Track when user stopped speaking

    // Response ID deduplication
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // =========================
    // CALLER STATE (per-call)
    // =========================
    private string _callerId = "";
    private string _detectedLanguage = "en";

    // =========================
    // CONSTANTS
    // =========================
    private const int MAX_NO_REPLY_PROMPTS = 3;
    private const int NO_REPLY_TIMEOUT_MS = 15000;

    // =========================
    // WEBSOCKET
    // =========================
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _keepaliveTask;
    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    // =========================
    // NON-BLOCKING LOGGER
    // =========================
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly AutoResetEvent _logSignal = new(false);
    private Thread? _logThread;
    private volatile bool _loggerRunning;

    // =========================
    // EVENTS
    // =========================
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _ws?.State == WebSocketState.Open;

    /// <summary>Fired with raw A-law RTP frames (160 bytes per 20ms).</summary>
    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;
    public event Action? OnBargeIn;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // =========================
    // CONSTRUCTOR
    // =========================
    public OpenAiG711Client(
        ILogger<OpenAiG711Client> logger,
        OpenAiSettings settings,
        string? systemPrompt = null)
    {
        _logger = logger;
        _settings = settings;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
    }

    // =========================
    // NON-BLOCKING LOGGING
    // =========================
    private void InitializeLogger()
    {
        if (_loggerRunning) return;
        _loggerRunning = true;
        _logThread = new Thread(LogFlushLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "G711ClientLog"
        };
        _logThread.Start();
    }

    private void LogFlushLoop()
    {
        while (_loggerRunning || !_logQueue.IsEmpty)
        {
            _logSignal.WaitOne(100);
            while (_logQueue.TryDequeue(out var msg))
            {
                try { _logger.LogInformation("{Msg}", msg); } catch { }
            }
        }
    }

    private void Log(string msg)
    {
        _logQueue.Enqueue(msg);
        _logSignal.Set();
    }

    // =========================
    // PER-CALL STATE RESET
    // =========================
    private void ResetCallState(string? caller)
    {
        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);

        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _noReplyCount, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastCompletedResponseId = null;

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
        Volatile.Write(ref _responseCreatedAt, 0);
        Volatile.Write(ref _speechStoppedAt, 0);
    }

    // =========================
    // RESPONSE GATE
    // =========================
    private bool CanCreateResponse(bool skipQueueCheck = false, bool bypassTranscriptGuard = false)
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               (skipQueueCheck || Volatile.Read(ref _responseQueued) == 0) &&
               (bypassTranscriptGuard || Volatile.Read(ref _transcriptPending) == 0) &&
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               (bypassTranscriptGuard || NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300);
    }

    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true, int maxWaitMs = 1000, bool bypassTranscriptGuard = false)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1)
            return;

        try
        {
            if (waitForCurrentResponse)
            {
                int waited = 0;
                while (Volatile.Read(ref _responseActive) == 1 && waited < maxWaitMs)
                {
                    await Task.Delay(20).ConfigureAwait(false);
                    waited += 20;
                }
            }

            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(false);

            if (Volatile.Read(ref _responseActive) == 1)
            {
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                Log("⏳ Response still active - deferring response.create");
                return;
            }

            if (!CanCreateResponse(skipQueueCheck: true, bypassTranscriptGuard: bypassTranscriptGuard))
                return;

            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            Log(bypassTranscriptGuard ? "🔄 response.create sent (tool bypass)" : "🔄 response.create sent");
        }
        finally
        {
            Interlocked.Exchange(ref _responseQueued, 0);
        }
    }

    // =========================
    // CONNECT
    // =========================
    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAiG711Client));

        ResetCallState(callerId);
        InitializeLogger();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_settings.Model}");

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        Log($"📞 Connecting v{VERSION} (caller: {callerId}, lang: {_detectedLanguage}, codec: A-law 8kHz)");

        await _ws.ConnectAsync(uri, linked.Token);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_cts.Token));

        _logger.LogInformation("Connected to OpenAI (G.711 A-law mode)");
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _logger.LogInformation("Disconnecting from OpenAI");
        SignalCallEnded("disconnect");

        _cts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }

        _ws?.Dispose();
        _ws = null;
        _loggerRunning = false;
        _logSignal.Set();
    }

    // =========================
    // KEEPALIVE
    // =========================
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(20000, ct).ConfigureAwait(false);

                if (_ws?.State != WebSocketState.Open)
                {
                    Log("⚠️ Keepalive: WebSocket disconnected");
                    break;
                }

                var active = Volatile.Read(ref _responseActive) == 1;
                var ended = Volatile.Read(ref _callEnded) == 1;
                Log($"💓 Keepalive: connected (response_active={active}, call_ended={ended})");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"⚠️ Keepalive error: {ex.Message}"); }
    }

    // =========================
    // AUDIO INPUT (SIP → OPENAI)
    // =========================
    public void SendAudio(byte[] alawRtp)
    {
        if (!IsConnected || alawRtp == null || alawRtp.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // NOTE: Echo guard removed — SipServer's soft gate (120ms + RMS barge-in)
        // is the single source of truth for echo suppression, matching G711CallHandler.
        // Double/triple gating caused audio dropouts ("grit").

        var msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(alawRtp)
        });

        _ = SendBytesAsync(msg);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        await SendJsonAsync(new { type = "response.cancel" });
    }

    /// <summary>
    /// Called by SipServer playout thread when audio queue drains.
    /// Sets the echo guard timestamp and starts no-reply watchdog.
    /// </summary>
    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());
        OnPlayoutComplete?.Invoke();
        StartNoReplyWatchdog();
    }

    // =========================
    // NO-REPLY WATCHDOG
    // =========================
    private void StartNoReplyWatchdog()
    {
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(NO_REPLY_TIMEOUT_MS);

            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _callEnded) != 0) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!IsConnected) return;

            var count = Interlocked.Increment(ref _noReplyCount);

            if (count >= MAX_NO_REPLY_PROMPTS)
            {
                Log($"⏰ Max no-reply prompts reached - ending call");
                SignalCallEnded("no_reply_timeout");
                OnEnded?.Invoke("no_reply_timeout");
                return;
            }

            Log($"⏰ No-reply watchdog triggered ({count}/{MAX_NO_REPLY_PROMPTS})");

            // Inject silence prompt
            await SendJsonAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "[SILENCE DETECTED] The user has not responded. Say something brief like 'Are you still there?' or 'Hello?' - do NOT repeat your previous question."
                        }
                    }
                }
            });

            await Task.Delay(20);
            await SendJsonAsync(new { type = "response.create" });
            Log("🔄 No-reply prompt sent");
        });
    }

    // =========================
    // RECEIVE LOOP
    // =========================
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (IsConnected && !ct.IsCancellationRequested)
            {
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    await ProcessMessageAsync(sb.ToString());
                    sb.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive error");
        }

        _logger.LogInformation("Receive loop ended");
        OnEnded?.Invoke("connection_closed");
    }

    // =========================
    // MESSAGE HANDLER
    // =========================
    private async Task ProcessMessageAsync(string json)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;

            var type = typeEl.GetString();
            if (type == null) return;

            switch (type)
            {
                case "session.created":
                    await ConfigureSessionAsync();
                    break;

                case "session.updated":
                    _logger.LogInformation("Session configured (G.711 A-law, v{Version})", VERSION);
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                        await SendGreetingAsync();
                    break;

                case "response.created":
                {
                    var responseId = TryGetResponseId(doc.RootElement);
                    if (responseId != null && _activeResponseId == responseId)
                        break; // Ignore duplicate

                    _activeResponseId = responseId;
                    Interlocked.Exchange(ref _responseActive, 1);
                    Volatile.Write(ref _responseCreatedAt, NowMs());

                    // Clear audio buffer on response start
                    _ = SendJsonAsync(new { type = "input_audio_buffer.clear" });
                    break;
                }

                case "response.done":
                {
                    var responseId = TryGetResponseId(doc.RootElement);

                    if (responseId == null || responseId == _lastCompletedResponseId)
                        break;
                    if (_activeResponseId != null && responseId != _activeResponseId)
                        break;

                    _lastCompletedResponseId = responseId;
                    _activeResponseId = null;
                    Interlocked.Exchange(ref _responseActive, 0);

                    // Always flush deferred response (tool results, etc.)
                    if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
                    {
                        Log("🔄 Flushing deferred response.create");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(20).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) == 0 && IsConnected)
                            {
                                await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                                Log("🔄 response.create sent (deferred)");
                            }
                        });
                    }
                    break;
                }

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                    {
                        var b64 = delta.GetString() ?? "";
                        if (b64.Length > 0)
                        {
                            var alawBytes = Convert.FromBase64String(b64);

                            // Frame into 160-byte RTP packets (20ms @ 8kHz)
                            for (int i = 0; i < alawBytes.Length; i += 160)
                            {
                                var frameSize = Math.Min(160, alawBytes.Length - i);
                                var frame = new byte[160];
                                Buffer.BlockCopy(alawBytes, i, frame, 0, frameSize);

                                if (frameSize < 160)
                                    Array.Fill(frame, (byte)0xD5, frameSize, 160 - frameSize);

                                OnAudio?.Invoke(frame);
                            }
                        }
                    }
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var adaText))
                    {
                        var text = adaText.GetString() ?? "";
                        if (!string.IsNullOrEmpty(text))
                        {
                            OnTranscript?.Invoke("Ada", text);

                            // Goodbye detection → arm 5s hangup watchdog
                            if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) &&
                                (text.Contains("Taxibot", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("Thank you for using", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("for calling", StringComparison.OrdinalIgnoreCase)))
                            {
                                Log("👋 Goodbye detected - arming 5s hangup watchdog");
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(5000);
                                    if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                        return;
                                    Log("⏰ Goodbye watchdog triggered - ending call");
                                    Interlocked.Exchange(ref _ignoreUserAudio, 1);
                                    SignalCallEnded("goodbye_watchdog");
                                    OnEnded?.Invoke("goodbye_watchdog");
                                });
                            }
                        }
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                {
                    Interlocked.Exchange(ref _transcriptPending, 0);

                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var text = userText.GetString();
                        if (!string.IsNullOrEmpty(text))
                            OnTranscript?.Invoke("User", text);
                    }

                    // Trigger response if not already active
                    if (Volatile.Read(ref _responseActive) == 0)
                        _ = QueueResponseCreateAsync(delayMs: 50, waitForCurrentResponse: false);
                    break;
                }

                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId); // Cancel pending watchdog
                    Interlocked.Exchange(ref _noReplyCount, 0);    // Reset no-reply count
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    Log("✂️ Barge-in detected");
                    OnBargeIn?.Invoke();
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Volatile.Write(ref _speechStoppedAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (awaiting transcript)");
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var errorEl))
                    {
                        var message = errorEl.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString() : "Unknown error";
                        if (!string.IsNullOrEmpty(message) &&
                            !message.Contains("buffer too small", StringComparison.OrdinalIgnoreCase))
                            Log($"❌ OpenAI error: {message}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    // =========================
    // TOOL HANDLING
    // =========================
    private async Task HandleToolCallAsync(JsonElement root)
    {
        var now = NowMs();
        if (now - Volatile.Read(ref _lastToolCallAt) < 200)
            return;
        Volatile.Write(ref _lastToolCallAt, now);

        var callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
        var argsStr = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";

        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
            return;

        Log($"🔧 Tool: {name}");

        Dictionary<string, object?> args;
        try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}") ?? new(); }
        catch { args = new(); }

        object result;
        if (OnToolCall != null)
            result = await OnToolCall.Invoke(name, args);
        else
            result = new { error = "No handler" };

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

        // Clear response active since old response is done after tool call
        Interlocked.Exchange(ref _responseActive, 0);

        // STATE GROUNDING: After sync_booking_data, inject state snapshot into conversation
        // This ensures the AI KNOWS what was synced and prevents redundant questions
        if (name == "sync_booking_data" && IsConnected)
        {
            // Build state text from args (we don't have BookingState here, CallSession does)
            var stateFields = new List<string>();
            if (args.TryGetValue("caller_name", out var cn) && cn != null) stateFields.Add($"Name: {cn}");
            if (args.TryGetValue("pickup", out var pu) && pu != null) stateFields.Add($"Pickup: {pu}");
            if (args.TryGetValue("destination", out var dt) && dt != null) stateFields.Add($"Destination: {dt}");
            if (args.TryGetValue("passengers", out var px) && px != null) stateFields.Add($"Passengers: {px}");
            if (args.TryGetValue("pickup_time", out var pt) && pt != null) stateFields.Add($"Time: {pt}");

            if (stateFields.Count > 0)
            {
                var stateText = $"[BOOKING STATE UPDATE] {string.Join(", ", stateFields)}";
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
                Log($"📋 State grounding injected: {stateText}");
            }
        }

        // Trigger new response immediately - bypass transcript guard since we have tool result
        if (IsConnected)
            await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0, bypassTranscriptGuard: true).ConfigureAwait(false);
    }

    // =========================
    // SESSION CONFIG
    // =========================
    private async Task ConfigureSessionAsync()
    {
        Log($"🎧 Configuring: input=g711_alaw@8kHz, output=g711_alaw@8kHz, voice={_settings.Voice}");

        await SendJsonAsync(new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = _systemPrompt,
                voice = _settings.Voice,
                input_audio_format = "g711_alaw",
                output_audio_format = "g711_alaw",
                input_audio_transcription = new { model = "whisper-1", language = _detectedLanguage },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.8
            }
        });
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(150); // Let session settle

        var greeting = _detectedLanguage == "nl"
            ? "Hallo, welkom bij Voice Taxibot. Wat is uw naam?"
            : "Hello, welcome to Voice Taxibot. May I have your name?";

        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = greeting
            }
        });

        _logger.LogInformation("Greeting sent");
    }

    // =========================
    // HELPERS
    // =========================
    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 0)
            Log($"📴 Call ended: {reason}");
    }

    private static string? TryGetResponseId(JsonElement root)
    {
        return root.TryGetProperty("response", out var r) &&
               r.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;
    }

    private async Task SendJsonAsync(object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await SendBytesAsync(bytes);
    }

    private async Task SendBytesAsync(byte[] bytes)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendMutex.WaitAsync();
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    // =========================
    // TOOLS (v7.6 - create_booking, no confirmation)
    // =========================
    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "Save booking data as collected. Call after EACH user input.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["caller_name"] = new { type = "string", description = "Customer name" },
                    ["pickup"] = new { type = "string", description = "Pickup address" },
                    ["destination"] = new { type = "string", description = "Destination address" },
                    ["passengers"] = new { type = "integer", description = "Number of passengers" },
                    ["pickup_time"] = new { type = "string", description = "Pickup time" }
                }
            }
        },
        new
        {
            type = "function",
            name = "create_booking",
            description = "Books a taxi for the user. Call as soon as you have pickup and destination.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["pickup_address"] = new { type = "string", description = "Pickup address" },
                    ["dropoff_address"] = new { type = "string", description = "Dropoff/destination address" },
                    ["passenger_count"] = new { type = "integer", description = "Number of passengers (default 1)" }
                },
                required = new[] { "pickup_address" }
            }
        },
        new
        {
            type = "function",
            name = "end_call",
            description = "End the call after goodbye",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["reason"] = new { type = "string", description = "Reason for ending call" }
                }
            }
        }
    };

    // =========================
    // SYSTEM PROMPT (v7.6)
    // =========================
    private static string GetDefaultSystemPrompt() => """
        You are Ada, a friendly and energetic taxi booking assistant for Voice Taxibot. Version 7.6.

        ## VOICE STYLE
        - Warm, upbeat, and confident — like a friendly radio host
        - Vary your pitch naturally — avoid monotone delivery
        - Short, clear sentences — max 20 words per response
        - Sound genuinely happy to help

        ## BOOKING FLOW (STRICT ORDER)

        1. Greet → ask name
        2. Get name → call sync_booking_data → ask pickup
        3. Get pickup → call sync_booking_data → ask destination
        4. Get destination → call sync_booking_data → ask passengers
        5. Get passengers → call sync_booking_data → call create_booking IMMEDIATELY
        6. Announce: 'Your taxi is [eta] minutes away, fare is [fare]. Anything else?'
        7. If no → say 'Have a great trip! Bye!' → call end_call

        ## MANDATORY DATA SYNC (CRITICAL!)

        You MUST call sync_booking_data after EVERY user message that provides booking info.
        Include ALL known fields every time (not just the new one).

        Example: after user says '52A David Road' for pickup, call:
        sync_booking_data(caller_name='Max', pickup='52A David Road')

        If you do NOT call sync_booking_data, the system will NOT know the data.
        The sync tool is what saves data — your memory alone is NOT enough.

        ## ADDRESS INTEGRITY

        - Store addresses EXACTLY as spoken — verbatim
        - NEVER add, remove, or change house numbers
        - NEVER guess missing parts — ask the user
        - If user corrects an address, the new one COMPLETELY replaces the old one
        - User's latest wording is ALWAYS the source of truth

        ## IMPLICIT CORRECTIONS

        Users often correct without saying 'no' or 'wrong'.
        If the user repeats an address differently, THIS IS A CORRECTION.
        Update immediately via sync_booking_data.

        ## CRITICAL RULES
        - Call sync_booking_data after EACH piece of info — NO EXCEPTIONS
        - Call create_booking as soon as you have pickup + destination + passengers
        - NEVER ask for booking confirmation — just book it
        - NEVER repeat addresses back unless summarizing
        - Keep responses SHORT and ENERGETIC
        - NEVER call end_call except after saying goodbye

        ## CURRENCY
        All prices are in EUROS (€). Use the fare_spoken field for pronunciation.

        ## FINAL CLOSING
        When done: 'Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.'
        Then call end_call immediately.
        """;

    // =========================
    // LANGUAGE DETECTION
    // =========================
    private static readonly Dictionary<string, string> CountryCodeToLanguage = new()
    {
        { "31", "nl" }, { "32", "nl" }, { "33", "fr" }, { "34", "es" },
        { "39", "it" }, { "44", "en" }, { "45", "en" }, { "46", "en" },
        { "47", "en" }, { "48", "pl" }, { "49", "de" }, { "35", "pt" },
        { "30", "en" }, { "41", "de" }, { "43", "de" },
    };

    private string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";

        var norm = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

        // Dutch local mobile
        if (norm.StartsWith("06") && norm.Length == 10 && norm.All(char.IsDigit))
            return "nl";
        if (norm.StartsWith("0") && !norm.StartsWith("00") && norm.Length == 10)
            return "nl";

        // Strip international prefix
        if (norm.StartsWith("+")) norm = norm[1..];
        else if (norm.StartsWith("00") && norm.Length > 4) norm = norm[2..];

        if (norm.Length >= 2)
        {
            var code = norm[..2];
            if (CountryCodeToLanguage.TryGetValue(code, out var lang))
            {
                Log($"🌍 Language: {lang} (country code {code})");
                return lang;
            }
        }

        return "en";
    }

    // =========================
    // DISPOSAL
    // =========================
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await DisconnectAsync();
        _sendMutex.Dispose();
        _loggerRunning = false;
        _logSignal.Set();
    }
}
