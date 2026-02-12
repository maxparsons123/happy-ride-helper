using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    private int _closingScriptSpoken;     // Per-call (set when Ada speaks the farewell)
    private int _deferredResponsePending; // Per-call (queued response after response.done)
    private int _noReplyWatchdogId;       // Incremented to cancel stale watchdogs
    private int _transcriptPending;       // Block response.create until transcript arrives
    private int _noReplyCount;            // Per-call
    private int _toolCalledInResponse;    // Track if tool was called during current response
    private int _responseTriggeredByTool;  // Set when response.create is sent after a tool result
    private int _hasEnqueuedAudio;         // FIX #2: Set when playout queue has received audio this response
    private int _vadRespondedThisTurn;    // v4.3: Set when server_vad auto-created a response this speech turn
    private int _toolInFlight;            // Suppress watchdogs/transcript responses while tool is executing
    private long _lastAdaFinishedAt;      // RTP playout completion time (echo guard source of truth)
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt;      // For transcript guard
    private long _speechStoppedAt;        // Track when user stopped speaking

    // Response ID deduplication
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // Price-promise safety net: track Ada's last transcript per response
    private string? _lastAdaTranscript;
    private bool _awaitingConfirmation;    // Longer watchdog timeout when waiting for confirmation
    private int _bookingConfirmed;         // Set once book_taxi succeeds — disables safety net
    private int _syncCallCount;            // How many sync_booking_data calls have been made — safety net requires ≥1

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
    private const int CONFIRMATION_TIMEOUT_MS = 30000;
    private const int DISAMBIGUATION_TIMEOUT_MS = 30000;  // Extended timeout for address selection

    // =========================
    // WEBSOCKET
    // =========================
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _keepaliveTask;
    private readonly SemaphoreSlim _sendMutex = new(1, 1);
    private readonly SemaphoreSlim _responseGate = new(0, 1); // Event-based response wait
    // =========================
    // NON-BLOCKING LOGGER (Channel-based — lighter than Thread+AutoResetEvent)
    // =========================
    private readonly Channel<string> _logChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _logTask;
    private volatile bool _loggerRunning;

    // =========================
    // ZERO-ALLOC AUDIO SEND (pre-built JSON envelope)
    // =========================
    private static readonly byte[] s_audioPrefix =
        Encoding.UTF8.GetBytes("{\"type\":\"input_audio_buffer.append\",\"audio\":\"");
    private static readonly byte[] s_audioSuffix =
        Encoding.UTF8.GetBytes("\"}");

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
    
    /// <summary>Fired when AI response stream completes (response.done) — use for precise bot-speaking tracking.</summary>
    public event Action? OnResponseCompleted;
    
    /// <summary>Optional: query playout queue depth for drain-aware shutdown.</summary>
    public Func<int>? GetQueuedFrames { get; set; }
    
    // Removed: IsAutoQuoteInProgress, _isDisambiguationPending — simplified flow has no guards
    
    /// <summary>Whether OpenAI is currently streaming a response.</summary>
    public bool IsResponseActive => Volatile.Read(ref _responseActive) == 1;

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
    // NON-BLOCKING LOGGING (Channel-based)
    // =========================
    private void InitializeLogger()
    {
        if (_loggerRunning) return;
        _loggerRunning = true;
        _logTask = Task.Run(LogLoopAsync);
    }

    private async Task LogLoopAsync()
    {
        try
        {
            await foreach (var msg in _logChannel.Reader.ReadAllAsync(_cts?.Token ?? CancellationToken.None))
            {
                try { _logger.LogInformation("{Msg}", msg); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Log(string msg)
    {
        _logChannel.Writer.TryWrite(msg);
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
        Interlocked.Exchange(ref _closingScriptSpoken, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _noReplyCount, 0);
        Interlocked.Exchange(ref _toolCalledInResponse, 0);
        Interlocked.Exchange(ref _responseTriggeredByTool, 0);
        Interlocked.Exchange(ref _bookingConfirmed, 0);
        Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
        Interlocked.Exchange(ref _vadRespondedThisTurn, 0);
        Interlocked.Exchange(ref _toolInFlight, 0);
        Interlocked.Exchange(ref _syncCallCount, 0);   // FIX: Reset sync counter between calls
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastCompletedResponseId = null;
        _lastAdaTranscript = null;
        _awaitingConfirmation = false;

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
            if (waitForCurrentResponse && Volatile.Read(ref _responseActive) == 1)
            {
                // Event-based wait — no polling, fewer wakeups
                await _responseGate.WaitAsync(maxWaitMs).ConfigureAwait(false);
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
        
        // ── Stop Channel-based logger ──
        _loggerRunning = false;
        _logChannel.Writer.TryComplete();
        try { if (_logTask != null) await _logTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _logTask = null;
        
        // ── Dispose CTS ──
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        
        // ── Clear all event delegates to break reference chains ──
        OnAudio = null;
        OnToolCall = null;
        OnEnded = null;
        OnPlayoutComplete = null;
        OnTranscript = null;
        OnBargeIn = null;
        OnResponseCompleted = null;
        GetQueuedFrames = null;
        // Removed: IsAutoQuoteInProgress, _isDisambiguationPending
        
        // ── Reset all per-call state (belt-and-suspenders — object shouldn't be reused) ──
        ResetCallState(null);
        
        _logger.LogInformation("OpenAiG711Client fully disposed — all state cleared");
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

        // Zero-alloc JSON: manually write the envelope with pooled buffer + in-place base64
        var b64Len = Base64.GetMaxEncodedToUtf8Length(alawRtp.Length);
        var totalLen = s_audioPrefix.Length + b64Len + s_audioSuffix.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLen);

        s_audioPrefix.CopyTo(buffer, 0);
        Base64.EncodeToUtf8(alawRtp, buffer.AsSpan(s_audioPrefix.Length), out _, out var written);
        s_audioSuffix.CopyTo(buffer, s_audioPrefix.Length + written);

        var finalLen = s_audioPrefix.Length + written + s_audioSuffix.Length;
        // Fire-and-forget: SendPooledAsync owns buffer lifetime (returns to pool after send)
        _ = SendPooledAsync(buffer, finalLen);

        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    /// <summary>
    /// Sends a pooled buffer slice and returns it to the pool after the WebSocket send completes.
    /// This eliminates the per-frame byte[] copy that was previously needed for fire-and-forget safety.
    /// </summary>
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

    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        await SendJsonAsync(new { type = "response.cancel" });
    }

    /// <summary>
    /// Inject a system-level message and immediately trigger an AI response.
    /// Used for interjections like "Let me get you a price" before async operations.
    /// </summary>
    public async Task InjectMessageAndRespondAsync(string message)
    {
        if (!IsConnected) return;
        
        // CRITICAL: If a fare result arrives while the AI is mid-response,
        // cancel the active response first to prevent stale/hallucinated fare announcements.
        // The AI will then generate a clean response using the injected fare data.
        bool isFareResult = message.Contains("[FARE RESULT]") || message.Contains("ADDRESS DISAMBIGUATION");
        if (isFareResult && Volatile.Read(ref _responseActive) == 1)
        {
            Log("🛑 Cancelling active response before fare/disambiguation injection");
            await SendJsonAsync(new { type = "response.cancel" });
            // Brief wait for cancel to take effect
            await Task.Delay(50);
        }
        
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text = message } }
            }
        });
        
        await Task.Delay(20);
        Interlocked.Exchange(ref _responseTriggeredByTool, 1);
        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0, bypassTranscriptGuard: true);
        Log($"💬 Interjection injected: {message}");
    }

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
        var timeoutMs = _awaitingConfirmation ? CONFIRMATION_TIMEOUT_MS : NO_REPLY_TIMEOUT_MS;
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(timeoutMs);

            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _callEnded) != 0) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!IsConnected) return;
            if (Volatile.Read(ref _toolInFlight) == 1) return; // Suppress during tool execution
            
            // Don't fire if a response is currently active (e.g. fare still being spoken)
            if (Volatile.Read(ref _responseActive) == 1)
            {
                Log("⏰ No-reply watchdog suppressed — response still active, re-arming");
                StartNoReplyWatchdog(); // Re-arm with fresh timeout
                return;
            }

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

    /// <summary>
    /// Fallback for false barge-ins: if VAD fires speech_started but never speech_stopped,
    /// the normal no-reply watchdog is cancelled and never re-armed. This ensures a watchdog
    /// still fires so the call doesn't hang indefinitely.
    /// </summary>
    private void ArmBargeInFallbackWatchdog()
    {
        const int FALLBACK_MS = 20000; // 20s — generous since user may still be speaking
        var fallbackId = Volatile.Read(ref _noReplyWatchdogId); // Capture current ID

        _ = Task.Run(async () =>
        {
            await Task.Delay(FALLBACK_MS);

            // If watchdog ID changed, a real speech_stopped or NotifyPlayoutComplete happened → abort
            if (Volatile.Read(ref _noReplyWatchdogId) != fallbackId) return;
            if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
            if (!IsConnected) return;

            // FIX #3: Clear transcript guard — without this, AI is gate-blocked forever
            Interlocked.Exchange(ref _transcriptPending, 0);
            
            // Still stuck — re-arm the normal no-reply watchdog
            Log("⏰ Barge-in fallback: no VAD commit detected — clearing transcript guard & re-arming watchdog");
            StartNoReplyWatchdog();
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
                    Interlocked.Exchange(ref _toolCalledInResponse, 0); // Reset per-response
                    Interlocked.Exchange(ref _hasEnqueuedAudio, 0);     // FIX #2: Reset per-response
                    Interlocked.Increment(ref _noReplyWatchdogId);      // Cancel stale watchdog
                    _lastAdaTranscript = null; // Reset per-response
                    Volatile.Write(ref _responseCreatedAt, NowMs());
                    
                    // v4.3: Mark that a response has been created for this speech turn.
                    // This prevents the transcript safety mechanism from firing a duplicate.
                    Interlocked.Exchange(ref _vadRespondedThisTurn, 1);

                    // Do NOT clear input_audio_buffer — per AUDIO_TIMING_RULES,
                    // buffer persistence prevents clipping user speech starts.
                    break;
                }

                case "response.done":
                {
                    var responseId = TryGetResponseId(doc.RootElement);

                    if (responseId == null || responseId == _lastCompletedResponseId)
                        break;
                    // FIX #1: Use Response ID tracking — ignore stale response.done from previous turns
                    if (_activeResponseId != null && responseId != _activeResponseId)
                    {
                        Log($"⚠️ Ignoring stale response.done (got {responseId}, expected {_activeResponseId})");
                        break;
                    }

                    _lastCompletedResponseId = responseId;
                    _activeResponseId = null;
                    Interlocked.Exchange(ref _responseActive, 0);
                    // Signal response gate (wakes QueueResponseCreateAsync without polling)
                    try { _responseGate.Release(); } catch (SemaphoreFullException) { }
                    try { OnResponseCompleted?.Invoke(); } catch { }

                    // Check if this response was triggered by a tool result
                    var wasToolTriggered = Interlocked.Exchange(ref _responseTriggeredByTool, 0) == 1;

                    // Flush deferred response ONLY if:
                    // 1. No tool was called in this response (tool handler queues its own response.create)
                    // 2. This response was NOT triggered by a tool result (prevents hallucination loop)
                    // Without check #2, the sequence is: tool → response.create → Ada speaks →
                    // response.done → deferred flush → ANOTHER response.create → OpenAI hallucinates answer
                    if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1 &&
                        Volatile.Read(ref _toolCalledInResponse) == 0 &&
                        !wasToolTriggered)
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
                    // FALLBACK WATCHDOG: If NotifyPlayoutComplete doesn't fire within 10s
                    // of response.done, arm the no-reply watchdog ourselves.
                    // This prevents calls from hanging when the playout engine's OnQueueEmpty
                    // doesn't trigger (race between response.done and queue drain).
                    if (Volatile.Read(ref _toolCalledInResponse) == 0 && !wasToolTriggered)
                    {
                        var fallbackWatchdogId = Volatile.Read(ref _noReplyWatchdogId);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(10000).ConfigureAwait(false); // 10s covers max playout + margin
                            if (Volatile.Read(ref _noReplyWatchdogId) == fallbackWatchdogId &&
                                Volatile.Read(ref _callEnded) == 0 &&
                                Volatile.Read(ref _disposed) == 0 &&
                                IsConnected)
                            {
                                Log("⏰ Response.done fallback: playout didn't fire watchdog — starting no-reply timer");
                                StartNoReplyWatchdog();
                            }
                        });
                    }

                    // PRICE-PROMISE SAFETY NET (ported from WinForms):
                    // If Ada explicitly promised to finalize/book but no tool was called, force action.
                    // CRITICAL: Skip if:
                    //   1) Booking already confirmed — prevents infinite loop
                    //   2) Auto-quote is in progress — prevents race with background fare calc
                    //   3) Awaiting confirmation — we're already waiting for user response (no safety net needed)
                    if (Volatile.Read(ref _bookingConfirmed) == 0 &&
                             Volatile.Read(ref _toolCalledInResponse) == 0 &&
                             !_awaitingConfirmation &&
                             !string.IsNullOrEmpty(_lastAdaTranscript) &&
                             HasBookingIntent(_lastAdaTranscript))
                    {
                        var hasSyncData = Volatile.Read(ref _syncCallCount) > 0;
                        Log($"⚠️ Price-promise safety net triggered: Ada promised booking but no tool called (syncCount={_syncCallCount})");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(40).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) != 0 || !IsConnected) return;

                            // If sync was never called, force sync first; otherwise force book_taxi
                            var overrideText = hasSyncData
                                ? "[SYSTEM OVERRIDE] You just told the customer you would get the price or finalize their booking but FAILED to call book_taxi. Call book_taxi(action=\"request_quote\") NOW if you haven't quoted yet, or book_taxi(action=\"confirmed\") if the user already agreed to the fare. Do NOT ask any more questions."
                                : "[SYSTEM OVERRIDE] You have been collecting booking data but NEVER called sync_booking_data. You MUST call sync_booking_data NOW with ALL the details you have collected (caller_name, pickup, destination, passengers, pickup_time). Do NOT speak — just call the tool immediately.";

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
                                            text = overrideText
                                        }
                                    }
                                }
                            });

                            await Task.Delay(20).ConfigureAwait(false);
                            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                            Log($"🔄 Safety net: forced {(hasSyncData ? "book_taxi" : "sync_booking_data")} response");
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
                            // Pooled base64 decode — avoids per-frame allocation
                            var maxBytes = (b64.Length * 3 + 3) / 4;
                            var poolBuf = ArrayPool<byte>.Shared.Rent(maxBytes);
                            try
                            {
                                if (Convert.TryFromBase64String(b64, poolBuf, out var bytesWritten) && bytesWritten > 0)
                                {
                                    var alawBytes = new byte[bytesWritten];
                                    Buffer.BlockCopy(poolBuf, 0, alawBytes, 0, bytesWritten);
                                    Interlocked.Exchange(ref _hasEnqueuedAudio, 1);
                                    OnAudio?.Invoke(alawBytes);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(poolBuf);
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
                            _lastAdaTranscript = text; // Track for price-promise safety net

                            // Goodbye detection → arm drain-aware hangup
                            if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) &&
                                (text.Contains("Taxibot", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("Thank you for using", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("for calling", StringComparison.OrdinalIgnoreCase)))
                            {
                                Interlocked.Exchange(ref _closingScriptSpoken, 1);
                                Log("👋 Goodbye detected - will end call after audio drains");
                                
                                // Wait for OpenAI to finish sending audio, THEN wait for playout drain
                                _ = Task.Run(async () =>
                                {
                                    // Phase 1: Wait for OpenAI to finish streaming (response.done)
                                    var waitStart = Environment.TickCount64;
                                    const int MAX_STREAM_WAIT_MS = 15000;
                                    
                                    while (Volatile.Read(ref _responseActive) == 1 &&
                                           Environment.TickCount64 - waitStart < MAX_STREAM_WAIT_MS)
                                    {
                                        await Task.Delay(200);
                                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                            return;
                                    }
                                    
                                    // FIX #2: Wait until audio has actually been enqueued before polling drain.
                                    // Without this, GetQueuedFrames returns 0 because buffer hasn't filled yet,
                                    // causing premature hangup mid-sentence on high-latency connections.
                                    const int MAX_ENQUEUE_WAIT_MS = 5000;
                                    var enqueueStart = Environment.TickCount64;
                                    while (Volatile.Read(ref _hasEnqueuedAudio) == 0 &&
                                           Environment.TickCount64 - enqueueStart < MAX_ENQUEUE_WAIT_MS)
                                    {
                                        await Task.Delay(100);
                                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                            return;
                                    }
                                    
                                    // Phase 2: Settling delay after audio started arriving
                                    await Task.Delay(1500);
                                    if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                        return;
                                    
                                    // Phase 3: Poll playout queue until drained (or fallback timeout)
                                    const int MAX_DRAIN_WAIT_MS = 20000;
                                    var drainStart = Environment.TickCount64;
                                    while (Environment.TickCount64 - drainStart < MAX_DRAIN_WAIT_MS)
                                    {
                                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                            return;
                                        
                                        var queued = GetQueuedFrames?.Invoke() ?? 0;
                                        if (queued == 0)
                                        {
                                            Log($"✅ Playout queue drained — waiting 1s before hangup");
                                            break;
                                        }
                                        
                                        await Task.Delay(200);
                                    }
                                    
                                    // Phase 4: Final margin for last RTP packets to reach the phone
                                    await Task.Delay(1000);
                                    
                                    if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                        return;
                                    Log("⏰ Goodbye watchdog triggered - ending call (audio drained)");
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
                    // v3.4: Delay clearing transcriptPending to let Whisper settle (ported from WinForms)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(120).ConfigureAwait(false);
                        Interlocked.Exchange(ref _transcriptPending, 0);
                    });

                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var text = ApplySttCorrections(userText.GetString() ?? "");
                        if (!string.IsNullOrEmpty(text))
                        {
                            var msSinceSpeechStopped = NowMs() - Volatile.Read(ref _speechStoppedAt);
                            if (Volatile.Read(ref _responseActive) == 1)
                                Log($"📝 Transcript arrived {msSinceSpeechStopped}ms after speech stopped: {text}");

                            OnTranscript?.Invoke("User", text);

                            // v4.0: CONDITIONAL TRANSCRIPT GROUNDING — only inject when the
                            // transcript contains correction signals or alphanumeric patterns.
                            // Saves 30-50% tokens on clean calls where grounding adds no value.
                            if (IsConnected && NeedsGrounding(text))
                            {
                                await SendJsonAsync(new
                                {
                                    type = "conversation.item.create",
                                    item = new
                                    {
                                        type = "message",
                                        role = "user",
                                        content = new[] { new { type = "input_text", text = $"[TRANSCRIPT] The caller just said: \"{text}\"" } }
                                    }
                                });
                                Log($"📋 Transcript grounding injected: {text}");
                            }
                        }
                    }

                    // v4.1: Safety response — if OpenAI's server_vad didn't auto-create a response
                    // after speech_stopped + commit, queue one ourselves. This matches V2Client behavior
                    // and prevents the call from hanging when VAD gets stuck after tool-call turns.
                    // Use a delay to give server_vad time to fire first; only queue if still idle.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800).ConfigureAwait(false); // Give server_vad time to auto-respond
                        // v4.3: Only fire if server_vad did NOT already create a response for this turn
                        if (Volatile.Read(ref _vadRespondedThisTurn) == 0 &&
                            Volatile.Read(ref _toolInFlight) == 0 &&
                            Volatile.Read(ref _responseActive) == 0 &&
                            Volatile.Read(ref _callEnded) == 0 &&
                            Volatile.Read(ref _disposed) == 0 &&
                            IsConnected)
                        {
                            Log("🔄 Transcript safety: server_vad didn't auto-respond, queuing response.create");
                            await QueueResponseCreateAsync(delayMs: 10, bypassTranscriptGuard: true).ConfigureAwait(false);
                        }
                    });
                    break;
                }

                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId); // Cancel pending watchdog
                    Interlocked.Exchange(ref _vadRespondedThisTurn, 0); // v4.3: Reset per speech turn
                    Interlocked.Exchange(ref _noReplyCount, 0);    // Reset no-reply count
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    
                    // Only treat as barge-in if Ada is actively responding with audio
                    if (Volatile.Read(ref _responseActive) == 1 && Volatile.Read(ref _hasEnqueuedAudio) == 1)
                    {
                        Log("✂️ Barge-in detected (interrupting active response)");
                        OnBargeIn?.Invoke();
                        // Cancel active response so OpenAI stops generating
                        _ = SendJsonAsync(new { type = "response.cancel" });
                    }
                    else
                    {
                        Log("🎤 User speech detected (no active response — normal turn)");
                    }
                    
                    // ARM FALLBACK: If this barge-in is a false positive (VAD never commits),
                    // re-arm the no-reply watchdog after a generous timeout so the call doesn't hang.
                    ArmBargeInFallbackWatchdog();
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Volatile.Write(ref _speechStoppedAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (awaiting transcript)");
                    
                    // Hard timeout: if transcript never arrives (noise/silence), unblock after 2s
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        if (Interlocked.CompareExchange(ref _transcriptPending, 0, 1) == 1)
                            Log("⏰ Transcript hard timeout (2s) — clearing _transcriptPending");
                    });
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
        // NOTE: Debounce removed (was 200ms). Sequential tool calls (sync_booking_data → create_booking)
        // were being silently dropped, causing Ada to say "I'll finalize" without actually booking.
        Volatile.Write(ref _lastToolCallAt, NowMs());
        Interlocked.Exchange(ref _toolCalledInResponse, 1); // Mark tool called for price-promise safety net

        var callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
        var argsStr = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";

        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
            return;

        Log($"🔧 Tool: {name}");

        // Mark booking as confirmed to disable price-promise safety net
        if (name == "book_taxi")
            Interlocked.Exchange(ref _bookingConfirmed, 1);

        // Suppress watchdogs and transcript-triggered responses while tool executes
        Interlocked.Exchange(ref _toolInFlight, 1);

        Dictionary<string, object?> args;
        try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}") ?? new(); }
        catch { args = new(); }

        object result;
        try
        {
            if (OnToolCall != null)
                result = await OnToolCall.Invoke(name, args);
            else
                result = new { error = "No handler" };
        }
        finally
        {
            Interlocked.Exchange(ref _toolInFlight, 0);
        }

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

        // Clear _responseActive: after response.function_call_arguments.done, OpenAI won't
        // send more audio for this response, so it's safe to unblock the response gate.
        Interlocked.Exchange(ref _responseActive, 0);

        // Simplified flow: no state grounding injection — AI uses tool results directly
        if (name == "sync_booking_data")
            Interlocked.Increment(ref _syncCallCount);

        // Trigger new response immediately - bypass transcript guard since we have tool result
        // Mark as tool-triggered so response.done won't flush deferred responses after this
        if (IsConnected)
        {
            Interlocked.Exchange(ref _responseTriggeredByTool, 1);
            await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0, bypassTranscriptGuard: true).ConfigureAwait(false);
        }
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
                input_audio_transcription = new { model = "whisper-1" }, // No language constraint — let Whisper auto-detect for language switching
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.3,        // v2.1: lower threshold for short quiet words like "three"
                    prefix_padding_ms = 600, // v2.1: 600ms captures more pre-speech (was 450)
                    silence_duration_ms = 900
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6  // Was 0.8 — more focused responses like Realtime client
            }
        });
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(150); // Let session settle

        var greeting = _detectedLanguage switch
        {
            "nl" => "Hallo, welkom bij Ada Taxibot. Wat is uw naam alstublieft?",
            "fr" => "Bonjour, bienvenue chez Ada Taxibot. Quel est votre nom s'il vous plaît?",
            "de" => "Hallo, willkommen bei Ada Taxibot. Wie ist Ihr Name bitte?",
            "es" => "Hola, bienvenido a Ada Taxibot. ¿Cuál es su nombre por favor?",
            "it" => "Ciao, benvenuto a Ada Taxibot. Come si chiama per favore?",
            "pl" => "Witam, witamy w Ada Taxibot. Jak się Pan/Pani nazywa?",
            "pt" => "Olá, bem-vindo ao Ada Taxibot. Qual é o seu nome por favor?",
            _ => "Hello, welcome to Ada Taxibot. May I have your name please?"
        };

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

    /// <summary>
    /// Apply common STT corrections for telephony speech recognition (ported from WinForms).
    /// </summary>
    // Pre-compiled regex patterns for STT corrections (avoids recompilation per call)
    private static readonly System.Text.RegularExpressions.Regex s_thankYouRegex = new(@"\bThank you for watching\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex s_subscribeRegex = new(@"\bPlease subscribe\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex s_likeSubscribeRegex = new(@"\bLike and subscribe\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex s_circuitsRegex = new(@"\bCircuits awaiting\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex s_alphanumericHouseRegex = new(@"(\d+)\s*[-,\s]\s*([A-Da-d])\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string ApplySttCorrections(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Common Whisper misrecognitions on telephony audio
        text = s_thankYouRegex.Replace(text, "");
        text = s_subscribeRegex.Replace(text, "");
        text = s_likeSubscribeRegex.Replace(text, "");
        text = s_circuitsRegex.Replace(text, "");

        // ── Alphanumeric house number recovery (aggressive) ──
        // Catches "52 A", "52 - A", "52, A" → "52A"
        text = s_alphanumericHouseRegex.Replace(text, "$1$2");

        // "to A" / "2 A" after digits context — e.g. "5 to A" → preserve as-is (ambiguous)
        // But "It's 2A" should stay "2A"
        
        // Common Whisper number→number mishearings for addresses with letter suffixes
        // "fifty-two A" → sometimes heard as "58" (5+2+A≈8), fix when followed by road context
        // We can't blindly replace "58" with "52A" but we flag it for Ada's attention
        // Instead, add partial corrections for KNOWN local addresses:
        // (Add your local corrections below as needed)
        
        return text.Trim();
    }

    /// <summary>
    /// Determines if a user transcript needs grounding injection.
    /// Only injects when corrections, negations, or alphanumeric patterns are detected,
    /// saving 30-50% tokens on clean calls.
    /// </summary>
    private static bool NeedsGrounding(string text)
    {
        return text.Contains("no", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("wrong", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("actually", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("meant", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("spell", StringComparison.OrdinalIgnoreCase) ||
               (text.Any(char.IsLetter) && text.Any(char.IsDigit));
    }

     /// <summary>
     /// Detect if Ada's transcript indicates she promised to FINALIZE/BOOK.
     /// CRITICAL: "let me check"/"let me get a price" are INTERJECTIONS while fare calculates.
     /// They are NOT booking promises — only phrases promising to FINALIZE or CONFIRM are.
     /// </summary>
     private static bool HasBookingIntent(string transcript)
     {
         var lower = transcript.ToLowerInvariant();
         
         // Exclude interjections, questions, and offers — these are NOT booking promises
         if (lower.Contains("how can i help") || lower.Contains("what can i") ||
             lower.Contains("would you like") || lower.Contains("shall i") ||
             lower.Contains("do you want") || lower.Contains("welcome") ||
             lower.Contains("good morning") || lower.Contains("good afternoon") ||
             lower.Contains("good evening") || lower.Contains("my name is") ||
             lower.Contains("let me check") || lower.Contains("let me get") ||
             lower.Contains("let me calculate") || lower.Contains("let me look") ||
             lower.Contains("one moment") || lower.Contains("just a moment") ||
             lower.Contains("moment please"))
             return false;
         
         // Only match EXPLICIT finalization/confirmation statements
         return lower.Contains("finalize") || lower.Contains("finalise") ||
                lower.Contains("i'll book") || lower.Contains("i will book") ||
                lower.Contains("i'll confirm") || lower.Contains("i will confirm") ||
                lower.Contains("booking confirmed") || lower.Contains("confirming your") ||
                lower.Contains("booking is complete");
     }

    /// <summary>
    /// Called by CallSession when awaiting booking confirmation (longer watchdog timeout).
    /// </summary>
    public void SetAwaitingConfirmation(bool awaiting)
    {
        _awaitingConfirmation = awaiting;
    }

    /// <summary>Cancel any pending deferred response.</summary>
    public void CancelDeferredResponse()
    {
        if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
            Log("🔄 Deferred response.create cancelled");
    }

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
        => await SendBytesAsync(JsonSerializer.SerializeToUtf8Bytes(obj));

    private async Task SendBytesAsync(ReadOnlyMemory<byte> bytes)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            // Standalone CTS — a timeout here won't poison the shared _cts
            using var sendCts = new CancellationTokenSource(5_000);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, sendCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("⚠️ WebSocket send timed out (5s)");
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    // =========================
    // TOOLS (simplified — sync stores data, book_taxi does quote/confirm)
    // =========================
    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "Save booking data as collected",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["caller_name"] = new { type = "string" },
                    ["pickup"] = new { type = "string" },
                    ["destination"] = new { type = "string" },
                    ["passengers"] = new { type = "integer" },
                    ["pickup_time"] = new { type = "string" }
                }
            }
        },
        new
        {
            type = "function",
            name = "book_taxi",
            description = "Request quote or confirm booking. ALWAYS include pickup and destination addresses.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["action"] = new { type = "string", @enum = new[] { "request_quote", "confirmed" }, description = "request_quote to get fare, confirmed to finalize" },
                    ["caller_name"] = new { type = "string", description = "Caller's name" },
                    ["pickup"] = new { type = "string", description = "Pickup address exactly as spoken" },
                    ["destination"] = new { type = "string", description = "Destination address exactly as spoken" },
                    ["passengers"] = new { type = "integer", description = "Number of passengers" },
                    ["pickup_time"] = new { type = "string", description = "Pickup time as spoken" }
                },
                required = new[] { "action", "pickup", "destination" }
            }
        },
        new
        {
            type = "function",
            name = "find_local_events",
            description = "Find concerts, shows, festivals, and events happening in an area",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["category"] = new { type = "string", description = "Event type: concert, show, festival, sports, theatre, comedy, or all" },
                    ["near"] = new { type = "string", description = "Location to search near" },
                    ["date"] = new { type = "string", description = "Date: tonight, tomorrow, this weekend, etc." }
                }
            }
        },
        new
        {
            type = "function",
            name = "end_call",
            description = "End call after goodbye",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["reason"] = new { type = "string" }
                },
                required = new[] { "reason" }
            }
        }
    };

    // =========================
    // SYSTEM PROMPT (v4.0 — simplified, matching openrealtimebest)
    // =========================
    private static string GetDefaultSystemPrompt() => @"You are Ada, a taxi booking assistant for Voice Taxibot. Version 4.0.

==============================
VOICE STYLE
==============================

Speak naturally, like a friendly professional taxi dispatcher.
- Warm, calm, confident tone
- Clear pronunciation of names and addresses
- Short pauses between phrases
- Never rush or sound robotic
- Patient and relaxed and slower pace

==============================
LANGUAGE
==============================

Start in English based on the caller's phone number.
CONTINUOUSLY MONITOR the caller's spoken language.
If they speak another language or ask to switch, IMMEDIATELY switch for all responses.

Supported languages:
English, Dutch, French, German, Spanish, Italian, Polish, Portuguese.

Default to English if uncertain.

==============================
BOOKING FLOW (STRICT)
==============================

Follow this order exactly:

Greet  
→ NAME  
→ PICKUP  
→ DESTINATION  
→ PASSENGERS  
→ TIME  

→ Confirm details ONCE  
→ If changes: update and confirm ONCE more  
→ If correct: say ""Just a moment while I get the price for you.""  
→ Call book_taxi(action=""request_quote"")  
→ Announce fare  
→ Ask ""Would you like to confirm this booking?""  
→ Call book_taxi(action=""confirmed"")  
→ Give reference ID ONLY  
→ Ask ""Anything else?""

If the user says NO to ""anything else"":
You MUST perform the FINAL CLOSING and then call end_call.

==============================
FINAL CLOSING (MANDATORY – EXACT WORDING)
==============================

When the conversation is complete, say EXACTLY this sentence and nothing else:

""Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.""

Then IMMEDIATELY call end_call.

DO NOT:
- Rephrase
- Add extra sentences
- Mention the journey
- Mention prices or addresses

==============================
CRITICAL: FRESH SESSION – NO MEMORY
==============================

THIS IS A NEW CALL.
- You have NO prior knowledge of this caller
- NEVER reuse data from earlier turns if the user corrects it
- The user's MOST RECENT wording is always the source of truth

==============================
DATA COLLECTION (MANDATORY)
==============================

After EVERY user message that provides OR corrects booking data:
- Call sync_booking_data immediately
- Include ALL known fields every time
- If the user repeats an address differently, THIS IS A CORRECTION
- Store addresses EXACTLY as spoken (verbatim)

==============================
IMPLICIT CORRECTIONS (VERY IMPORTANT)
==============================

Users often correct information without saying ""no"" or ""wrong"".

Examples:
Stored: ""Russell Street, Coltree""  
User: ""Russell Street in Coventry""  
→ UPDATE to ""Russell Street, Coventry""

Stored: ""David Road""  
User: ""52A David Road""  
→ UPDATE to ""52A David Road""

ALWAYS trust the user's latest wording.

==============================
USER CORRECTION OVERRIDES (CRITICAL)
==============================

If the user corrects an address, YOU MUST assume the user is right.

This applies EVEN IF:
- The address sounds unusual
- The address conflicts with earlier data
- The address conflicts with any verification or prior confirmation

Once the user corrects a street name:
- NEVER revert to the old street
- NEVER offer alternatives unless the user asks
- NEVER ""double check"" unless explicitly requested

If the user repeats or insists on an address:
THAT ADDRESS IS FINAL.

==============================
REPETITION RULE (VERY IMPORTANT)
==============================

If the user repeats the same address again, especially with emphasis
(e.g. ""no"", ""no no"", ""I said"", ""my destination is""):

- Treat this as a STRONG correction
- Do NOT restate the old address
- Acknowledge and move forward immediately

==============================
ADDRESS INTEGRITY (ABSOLUTE RULE)
==============================

Addresses are IDENTIFIERS, not descriptions.

YOU MUST:
- NEVER add numbers the user did not say
- NEVER remove numbers the user did say
- NEVER guess missing parts
- NEVER ""improve"", ""normalize"", or ""correct"" addresses
- Read back EXACTLY what was stored

If unsure, ASK the user.

IMPORTANT:
You are NOT allowed to ""correct"" addresses.
Your job is to COLLECT, not to VALIDATE.

==============================
HOUSE NUMBER HANDLING (CRITICAL)
==============================

House numbers are NOT ranges unless the USER explicitly says so.

- NEVER insert hyphens
- NEVER convert numbers into ranges
- NEVER reinterpret numeric meaning
- NEVER rewrite digits

Examples:
1214A → spoken ""twelve fourteen A""
12-14 → spoken ""twelve to fourteen"" (ONLY if user said dash/to)

==============================
PICKUP TIME HANDLING
==============================

- ""now"", ""right now"", ""ASAP"" → store exactly as ""now""
- NEVER convert ""now"" into a clock time
- Only use exact times if the USER gives one

==============================
SUMMARY CONSISTENCY (MANDATORY)
==============================

Your confirmation MUST EXACTLY match:
- Addresses as spoken
- Times as spoken
- Passenger count as spoken

DO NOT introduce new details.

==============================
ETA HANDLING
==============================

- Immediate pickup → mention arrival time
- Scheduled pickup → do NOT mention arrival ETA

==============================
CURRENCY
==============================

ALL prices use the fare_spoken field for speech.
Use the fare_spoken field verbatim. NEVER change the currency. NEVER invent a fare.

==============================
ABSOLUTE RULES – VIOLATION FORBIDDEN
==============================

1. You MUST call sync_booking_data after every booking-related user message
2. You MUST call book_taxi(action=""confirmed"") BEFORE confirming a booking
3. NEVER announce booking success before the tool succeeds
4. NEVER invent a booking reference
5. If booking fails, explain clearly and ask to retry

==============================
HARD ADDRESS OVERRIDE (CRITICAL)
==============================

Addresses are ATOMIC values.

If the user provides an address with a DIFFERENT street name than the one currently stored:
- IMMEDIATELY DISCARD the old address entirely
- DO NOT merge any components
- The new address COMPLETELY replaces the old one

==============================
CONFIRMATION DETECTION
==============================

These mean YES:
yes, yeah, yep, sure, ok, okay, correct, that's right, go ahead, book it, confirm, that's fine

==============================
NEGATIVE CONFIRMATION HANDLING (CRITICAL)
==============================

These mean NO to a fare quote or booking confirmation:
no, nope, nah, no thanks, too much, too expensive, cancel

When the user says NO after a fare quote:
- Do NOT end the call
- Ask what they would like to change
- If they want to cancel entirely, acknowledge and THEN do FINAL CLOSING + end_call

==============================
RESPONSE STYLE
==============================

- One question at a time
- Under 20 words per response
- Calm, professional, human
- Acknowledge corrections briefly, then move on
- NEVER call end_call except after the FINAL CLOSING
    ";

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
        // DisconnectAsync has its own _disposed guard — call it first
        await DisconnectAsync();
        
        // Dispose semaphores (only safe after all send operations stop)
        try { _sendMutex.Dispose(); } catch { }
        try { _responseGate.Dispose(); } catch { }
        
        // Belt-and-suspenders: ensure logger is stopped
        _loggerRunning = false;
        _logChannel.Writer.TryComplete();
    }
}
