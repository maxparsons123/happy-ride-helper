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
    private int _closingScriptSpoken;     // Per-call (set when Ada speaks the farewell)
    private int _deferredResponsePending; // Per-call (queued response after response.done)
    private int _noReplyWatchdogId;       // Incremented to cancel stale watchdogs
    private int _transcriptPending;       // Block response.create until transcript arrives
    private int _noReplyCount;            // Per-call
    private int _toolCalledInResponse;    // Track if tool was called during current response
    private int _responseTriggeredByTool;  // Set when response.create is sent after a tool result
    private int _hasEnqueuedAudio;         // FIX #2: Set when playout queue has received audio this response
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
    
    /// <summary>Fired when AI response stream completes (response.done) — use for precise bot-speaking tracking.</summary>
    public event Action? OnResponseCompleted;
    
    /// <summary>Optional: query playout queue depth for drain-aware shutdown.</summary>
    public Func<int>? GetQueuedFrames { get; set; }
    
    /// <summary>Optional: check if an auto-quote is in progress (prevents safety net race).</summary>
    public Func<bool>? IsAutoQuoteInProgress { get; set; }
    
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
        Interlocked.Exchange(ref _closingScriptSpoken, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _noReplyCount, 0);
        Interlocked.Exchange(ref _toolCalledInResponse, 0);
        Interlocked.Exchange(ref _responseTriggeredByTool, 0);
        Interlocked.Exchange(ref _bookingConfirmed, 0);
        Interlocked.Exchange(ref _hasEnqueuedAudio, 0);
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
    /// Inject a system-level message and immediately trigger an AI response.
    /// Used for interjections like "Let me get you a price" before async operations.
    /// </summary>
    public async Task InjectMessageAndRespondAsync(string message)
    {
        if (!IsConnected) return;
        
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
                    Interlocked.Increment(ref _noReplyWatchdogId);      // Cancel stale watchdog (e.g. fare interjection arriving after tool response)
                    _lastAdaTranscript = null; // Reset per-response
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
                    // FIX #1: Use Response ID tracking — ignore stale response.done from previous turns
                    if (_activeResponseId != null && responseId != _activeResponseId)
                    {
                        Log($"⚠️ Ignoring stale response.done (got {responseId}, expected {_activeResponseId})");
                        break;
                    }

                    _lastCompletedResponseId = responseId;
                    _activeResponseId = null;
                    Interlocked.Exchange(ref _responseActive, 0);
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
                    // PRICE-PROMISE SAFETY NET (ported from WinForms):
                    // If Ada promised to finalize/book but no tool was called, force create_booking
                    // CRITICAL: Skip if booking already confirmed — prevents infinite loop
                    else if (Volatile.Read(ref _bookingConfirmed) == 0 &&
                             Volatile.Read(ref _toolCalledInResponse) == 0 &&
                             // Removed _syncCallCount > 0 guard: if Ada promises a price but NEVER called
                             // sync_booking_data at all, the safety net MUST still fire to force tool usage.
                             !(IsAutoQuoteInProgress?.Invoke() ?? false) && // GUARD: don't race with background fare calc
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
                            // v2.1: Pass raw A-law bytes directly — ALawRtpPlayout handles framing.
                            // Do NOT frame here; double-framing with silence padding causes warble.
                        var alawBytes = Convert.FromBase64String(b64);
                        if (alawBytes.Length > 0)
                        {
                            Interlocked.Exchange(ref _hasEnqueuedAudio, 1); // FIX #2: Mark audio received
                            OnAudio?.Invoke(alawBytes);
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

                            // v4.0: TRANSCRIPT GROUNDING — inject the text transcript into the conversation
                            // so the model can cross-reference what it heard vs what was actually said.
                            // This fixes mishearing issues (e.g. "Dovey" heard as "Dollby") because the
                            // model can see the correct text and self-correct on its next response.
                            if (IsConnected)
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

                    // DO NOT auto-queue response.create here.
                    // With server_vad, OpenAI will auto-respond after speech_stopped + commit.
                    // The tool handler already queues its own response.create after processing.
                    // Auto-queuing here caused deferred responses that made OpenAI hallucinate
                    // answers the user never spoke (e.g., fabricating addresses).
                    break;
                }

                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId); // Cancel pending watchdog
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
        if (name == "sync_booking_data")
            Interlocked.Increment(ref _syncCallCount);
        
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
    /// Detect if Ada's transcript indicates she promised to finalize/book
    /// but no tool was actually called (price-promise safety net).
    /// </summary>
    private static bool HasBookingIntent(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        
        // Exclude greetings, questions, and offers — these are NOT booking promises
        if (lower.Contains("how can i help") || lower.Contains("what can i") ||
            lower.Contains("would you like") || lower.Contains("shall i") ||
            lower.Contains("do you want") || lower.Contains("welcome") ||
            lower.Contains("good morning") || lower.Contains("good afternoon") ||
            lower.Contains("good evening") || lower.Contains("my name is"))
            return false;
        
        // Only match clear action-oriented statements
        return lower.Contains("finalize") || lower.Contains("finalise") ||
               lower.Contains("i'll book") || lower.Contains("i will book") ||
               lower.Contains("booking your") || lower.Contains("booking that") ||
               lower.Contains("moment while") || lower.Contains("one moment") ||
               lower.Contains("let me get") || lower.Contains("i'll get") ||
               lower.Contains("let me calculate") || lower.Contains("let me check") ||
               lower.Contains("getting the price") || lower.Contains("getting the fare") ||
               lower.Contains("just a moment");
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
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await SendBytesAsync(bytes);
    }

    private async Task SendBytesAsync(byte[] bytes)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            // FIX: Add 5s timeout to prevent WebSocket send from hanging indefinitely
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _cts?.Token ?? CancellationToken.None);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
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
    // TOOLS (v3.7 - book_taxi with request_quote/confirmed)
    // =========================
    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "MANDATORY: Save booking data after EVERY user message that provides or corrects info. You MUST call this — your memory alone does NOT persist data. Include ALL known fields each time.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["caller_name"] = new { type = "string", description = "Customer name" },
                    ["pickup"] = new { type = "string", description = "Pickup address (verbatim as spoken)" },
                    ["destination"] = new { type = "string", description = "Destination address (verbatim as spoken)" },
                    ["passengers"] = new { type = "integer", description = "Number of passengers (1-8)" },
                    ["pickup_time"] = new { type = "string", description = "Pickup time (e.g. 'now', 'in 10 minutes', '14:30')" }
                },
                required = new[] { "caller_name" }
            }
        },
        new
        {
            type = "function",
            name = "book_taxi",
            description = "ONLY call this AFTER the user has HEARD the fare and EXPLICITLY said yes/confirm/go ahead. action='confirmed' is the ONLY valid action. NEVER call this during fare calculation or before the user confirms. If you call this before user confirmation, the booking is INVALID.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["action"] = new { type = "string", description = "Either 'request_quote' or 'confirmed'", @enum = new[] { "request_quote", "confirmed" } }
                },
                required = new[] { "action" }
            }
        },
        new
        {
            type = "function",
            name = "find_local_events",
            description = "Look up local events near a location. Use when the caller asks about events, things to do, or what's on nearby.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["category"] = new { type = "string", description = "Event category: all, concert, comedy, theatre, sports, festival" },
                    ["near"] = new { type = "string", description = "Location or city to search near" },
                    ["date"] = new { type = "string", description = "When: 'tonight', 'this weekend', 'tomorrow', or a specific date" }
                },
                required = new[] { "near" }
            }
        },
        new
        {
            type = "function",
            name = "end_call",
            description = "End the call. Call this IMMEDIATELY after the FINAL CLOSING script. Never before.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["reason"] = new { type = "string", description = "Reason for ending call" }
                },
                required = new[] { "reason" }
            }
        }
    };

    // =========================
    // SYSTEM PROMPT (v3.9 — user-supplied + bridge auto-quote integration)
    // =========================
    private static string GetDefaultSystemPrompt() => @"You are Ada, a taxi booking assistant for Voice Taxibot. Version 3.9.

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

After EVERY field, call sync_booking_data with ALL known fields.

When sync_booking_data is called with all 5 fields filled, the system will
AUTOMATICALLY validate the addresses via our address verification system and
calculate the fare. You will receive the result as a [FARE RESULT] message.

The [FARE RESULT] contains VERIFIED addresses (resolved with postcodes/cities)
that may differ from what the user originally said. You MUST use the VERIFIED
addresses when reading back the booking — NOT the user's original words.

STEP-BY-STEP (DO NOT SKIP ANY STEP):

1. After all fields collected, say ONLY: ""Let me check those addresses and get you a price.""
2. WAIT for the [FARE RESULT] message — DO NOT call book_taxi yet
3. When you receive [FARE RESULT], read back the VERIFIED addresses and fare:
   ""Your pickup is [VERIFIED pickup] going to [VERIFIED destination], the fare is [fare] with an estimated arrival in [ETA]. Would you like to confirm or change anything?""
4. WAIT for the user to say YES (""yes"", ""confirm"", ""go ahead"", etc.)
5. ONLY THEN call book_taxi(action=""confirmed"")
6. Give reference ID from the tool result
7. Ask ""Anything else?""

⚠️ CRITICAL: NEVER call book_taxi BEFORE step 4. The user MUST hear the fare AND say yes FIRST.
If you call book_taxi before the user confirms, the booking is INVALID and harmful.
⚠️ ALWAYS use the VERIFIED addresses from [FARE RESULT], never the raw user input.

If the user says NO to ""anything else"":
You MUST perform the FINAL CLOSING and then call end_call.

IMPORTANT: If sync_booking_data returns needs_clarification=true,
you MUST ask the user to clarify which location they mean before continuing.
Present the alternatives naturally (e.g. ""Did you mean School Road in Hall Green
or School Road in Moseley?"").

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

CRITICAL — OUT-OF-ORDER / BATCHED DATA:
Callers often give multiple fields in one turn (e.g. ""52A David Road, going to Leicester"").
Even if these fields are ahead of the strict sequence:
1. Call sync_booking_data IMMEDIATELY with ALL data the user just provided
2. THEN ask for the next missing field in the flow order
NEVER defer syncing data just because you haven't asked for that field yet.

The bridge tracks the real state. Your memory alone does NOT persist data.
If you skip a sync_booking_data call, the booking state will be wrong.

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
HARD ADDRESS OVERRIDE (CRITICAL)
==============================

Addresses are ATOMIC values.

If the user provides an address with a DIFFERENT street name:
- IMMEDIATELY DISCARD the old address entirely
- DO NOT merge any components
- The new address COMPLETELY replaces the old one

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
ALPHANUMERIC ADDRESS VIGILANCE (CRITICAL)
==============================

Many house numbers contain a LETTER SUFFIX (e.g. 52A, 1214A, 7B, 33C).
Speech recognition OFTEN drops or merges the letter, producing wrong numbers.

RULES:
1. If a user says a number that sounds like it MIGHT end with A/B/C/D
   (e.g. ""fifty-two-ay"", ""twelve-fourteen-ay"", ""seven-bee""),
   ALWAYS store the letter: 52A, 1214A, 7B.
2. If the transcript shows a number that seems slightly off from what was
   expected (e.g. ""58"" instead of ""52A"", ""28"" instead of ""2A""),
   and the user then corrects — accept the correction IMMEDIATELY on the
   FIRST attempt. Do NOT require a second correction.
3. When a user corrects ANY part of an address, even just the house number,
   update it IMMEDIATELY via sync_booking_data and acknowledge briefly.
   Do NOT re-ask the same question.
4. If the user says ""no"", ""that's wrong"", or repeats an address with emphasis,
   treat the NEW value as FINAL and move to the next question immediately.

==============================
SPELLING DETECTION (CRITICAL)
==============================

If the user spells out a word letter-by-letter (e.g. ""D-O-V-E-Y"", ""B-A-L-L""),
you MUST:
1. Reconstruct the word from the letters: D-O-V-E-Y → ""Dovey""
2. IMMEDIATELY replace any previous version of that word with the spelled version
3. Call sync_booking_data with the corrected address
4. Acknowledge: ""Got it, Dovey Road"" and move on

NEVER ignore spelled-out corrections. The user is spelling BECAUSE you got it wrong.

==============================
MISHEARING RECOVERY (CRITICAL)
==============================

If your transcript of the user's address does NOT match what you previously stored,
the user is CORRECTING you. Common STT confusions:
- ""Dovey"" → ""Dollby"", ""Dover"", ""Dolby""
- ""Stoney"" → ""Tony"", ""Stone""
- ""Fargo"" → ""Farco"", ""Largo""

RULES:
1. ALWAYS use the user's LATEST wording, even if it sounds different from before
2. If you are unsure, read back what you heard and ask: ""Did you say [X] Road?""
3. NEVER repeat your OLD version after the user has corrected you
4. After ANY correction, call sync_booking_data IMMEDIATELY

==============================
PICKUP TIME HANDLING
==============================

- ""now"", ""right now"", ""ASAP"" → store exactly as ""now""
- NEVER convert ""now"" into a clock time
- Only use exact times if the USER gives one

==============================
INPUT VALIDATION (IMPORTANT)
==============================

Reject nonsense audio or STT artifacts:
- If the transcribed text sounds like gibberish (""Circuits awaiting"", ""Thank you for watching""),
  ignore it and gently ask the user to repeat.
- Passenger count must be 1-8. If outside range, ask again.
- If a field value seems implausible, ask for clarification rather than storing it.

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

- Immediate pickup (""now"") → mention arrival time
- Scheduled pickup → do NOT mention arrival ETA

==============================
CURRENCY
==============================

Use the fare_spoken field for speech — it already contains the correct currency word (pounds, euros, etc.).
NEVER change the currency. NEVER invent a fare.

==============================
ABSOLUTE RULES – VIOLATION FORBIDDEN
==============================

1. You MUST call sync_booking_data after every booking-related user message
2. You MUST NOT call book_taxi until the user has HEARD the fare AND EXPLICITLY confirmed
3. NEVER call book_taxi in the same turn as the fare interjection — the fare hasn't been calculated yet
4. NEVER call book_taxi in the same turn as announcing the fare — wait for user response
5. The booking reference comes ONLY from the book_taxi tool result - NEVER invent one
6. If booking fails, tell the user and ask if they want to try again
7. NEVER call end_call except after the FINAL CLOSING

==============================
CONFIRMATION DETECTION
==============================

These mean YES:
yes, yeah, yep, sure, ok, okay, correct, that's right, go ahead, book it, confirm, that's fine

==============================
NEGATIVE CONFIRMATION HANDLING (CRITICAL)
==============================

These mean NO to a fare quote or booking confirmation:
no, nope, nah, no thanks, too much, too expensive, that's too much, I don't want it, cancel

When the user says NO after a fare quote or confirmation question:
- Do NOT end the call
- Do NOT run the closing script
- Do NOT call end_call
- Ask what they would like to change (pickup, destination, passengers, or time)
- If they want to cancel entirely, acknowledge and THEN do FINAL CLOSING + end_call

""Nope"" or ""No"" to ""Would you like to proceed?"" means they want to EDIT or CANCEL — NOT that they are done.

==============================
RESPONSE STYLE
==============================

- One question at a time
- Under 15 words per response — be concise
- Calm, professional, human
- Acknowledge corrections briefly, then move on
- NEVER say filler phrases: ""just a moment"", ""please hold on"", ""let me check"", ""one moment"", ""please wait""
- When fare is being calculated, say ONLY the interjection (e.g. ""Let me get you a price on that journey."") — do NOT add extra sentences
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
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await DisconnectAsync();
        _sendMutex.Dispose();
        _loggerRunning = false;
        _logSignal.Set();
    }
}
