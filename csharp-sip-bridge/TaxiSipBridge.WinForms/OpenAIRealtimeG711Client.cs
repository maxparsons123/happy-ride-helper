using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime client for G.711 telephony with local DSP processing.
/// 
/// v2.7: All response.create calls routed through QueueResponseCreateAsync with SIP-safe delays.
///       Checks _transcriptPending and _lastUserSpeechAt (3s window) before firing.
/// 
/// Key features:
/// - Proper ResetCallState() for clean per-call state
/// - Keepalive loop for connection health monitoring
/// - Deferred response handling (queue response.create if one is active)
/// - Transcript guard (ignores stale transcripts within 400ms of response.created)
/// - No-reply watchdog with speech guard
/// - CanCreateResponse() gate with multiple conditions
/// - RTP playout completion as source of truth for echo guard
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "5.1";

    // =========================
    // G.711 CONFIG
    // =========================
    public enum G711Codec { MuLaw, ALaw }

    public G711Codec NegotiatedCodec => _codec;
    private readonly G711Codec _codec;
    private readonly byte _silenceByte;
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int FRAME_BYTES = 160;

    // =========================
    // OPENAI CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // =========================
    // THREAD-SAFE STATE (per-call, reset by ResetCallState)
    // =========================
    private int _disposed;              // Object lifetime only (NEVER reset)
    private int _callEnded;             // Per-call
    private int _responseActive;        // OBSERVED only (set only by response.created / response.done)
    private int _responseQueued;        // Per-call
    private int _sessionCreated;        // Per-call
    private int _sessionUpdated;        // Per-call
    private int _greetingSent;          // Per-call
    private int _ignoreUserAudio;       // Per-call (set after goodbye starts)
    private int _deferredResponsePending; // Per-call (queued response after response.done)
    private int _noReplyWatchdogId;     // Incremented to cancel stale watchdogs
    private int _transcriptPending;      // v2.5: Block response.create until transcript arrives
    private long _lastAdaFinishedAt;    // RTP playout completion time (source of truth for echo guard)
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt;    // For transcript guard
    private long _speechStoppedAt;      // v2.5: Track when user stopped speaking

    // Tracks current OpenAI response id to ignore duplicate events
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // =========================
    // CALLER STATE (per-call)
    // =========================
    private string _callerId = "";
    private string _detectedLanguage = "en";  // v2.4: Language hint for Whisper

    // =========================
    // WEBSOCKET
    // =========================
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;
    private Task? _keepaliveTask;
    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    // =========================
    // LEGACY QUEUE (for pull-based playout compatibility)
    // =========================
    private const int MAX_OUTBOUND_FRAMES = 2000;
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private int _outboundQueueCount;

    // =========================
    // STATS
    // =========================
    private long _audioFramesSent;
    private long _audioChunksReceived;

    // =========================
    // EVENTS
    // =========================
    public event Action<byte[]>? OnG711Audio;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action<string>? OnTranscript;
    public event Action<string>? OnLog;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnResponseStarted;
    public event Action? OnResponseCompleted;
    public event Action? OnBargeIn;
    public event Action? OnCallEnded;
    public event Action<string>? OnAdaSpeaking;
    public event Action<BookingState>? OnBookingUpdated;

    // Tool calling (handled externally, e.g. by G711CallFeatures)
    public event Func<string, string, JsonElement, Task<object>>? OnToolCall;

    // =========================
    // PROPERTIES
    // =========================
    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 &&
        _ws?.State == WebSocketState.Open;

    public int PendingFrameCount => Math.Max(0, Volatile.Read(ref _outboundQueueCount));

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    // Non-blocking async logger - enqueues to background thread to avoid audio jitter
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly AutoResetEvent _logSignal = new(false);
    private Thread? _logThread;
    
    private void InitializeLogger()
    {
        if (_logThread != null) return;
        _logThread = new Thread(LogFlushLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "G711ClientLogThread"
        };
        _logThread.Start();
    }
    
    private void LogFlushLoop()
    {
        while (!(_disposed != 0))
        {
            _logSignal.WaitOne(100);
            while (_logQueue.TryDequeue(out var msg))
            {
                try { OnLog?.Invoke(msg); } catch { }
            }
        }
        // Final drain
        while (_logQueue.TryDequeue(out var msg))
        {
            try { OnLog?.Invoke(msg); } catch { }
        }
    }
    
    private void Log(string msg)
    {
        _logQueue.Enqueue(msg);
        _logSignal.Set();
    }

    // =========================
    // RESPONSE GATE
    // =========================
    /// <summary>
    /// Check if we can create a new response. Used by QueueResponseCreateAsync.
    /// NOTE: Does NOT check _responseQueued because we're already inside that gate when called.
    /// </summary>
    private bool CanCreateResponse(bool skipQueueCheck = false, bool bypassTranscriptGuard = false)
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               (skipQueueCheck || Volatile.Read(ref _responseQueued) == 0) &&
               (bypassTranscriptGuard || Volatile.Read(ref _transcriptPending) == 0) &&  // v2.6: Wait for transcript (unless tool-driven)
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               (bypassTranscriptGuard || NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300);  // v7.1: Tool responses bypass speech check
    }
    
    /// <summary>
    /// Cancel any pending deferred response to prevent double-responses
    /// when G711CallFeatures watchdog sends its own response.create.
    /// </summary>
    public void CancelDeferredResponse()
    {
        if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
            Log("🔄 Deferred response.create cancelled");
    }

    /// <summary>
    /// Queue a response.create. Does NOT set _responseActive manually.
    /// Waits for any active response to complete first, then sends response.create.
    /// OpenAI's response.created event will set _responseActive = 1.
    /// </summary>
    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true, int maxWaitMs = 1000, bool bypassTranscriptGuard = false)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1)
            return;

        try
        {
            // Wait for any active response to complete (with configurable max wait)
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

            // CRITICAL FIX: If response is still active, ALWAYS defer - regardless of waitForCurrentResponse
            // This handles tool results that complete before response.done arrives
            if (Volatile.Read(ref _responseActive) == 1)
            {
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                Log("⏳ Response still active - deferring response.create");
                return;
            }

            // Skip the _responseQueued check since we're already inside this method
            if (!CanCreateResponse(skipQueueCheck: true, bypassTranscriptGuard: bypassTranscriptGuard))
            {
                // Log which condition blocked
                var reason = Volatile.Read(ref _responseActive) == 1 ? "responseActive" :
                             Volatile.Read(ref _transcriptPending) == 1 ? "transcriptPending" :
                             Volatile.Read(ref _callEnded) != 0 ? "callEnded" :
                             Volatile.Read(ref _disposed) != 0 ? "disposed" :
                             !IsConnected ? "disconnected" :
                             (NowMs() - Volatile.Read(ref _lastUserSpeechAt)) <= 300 ? $"recentSpeech ({NowMs() - Volatile.Read(ref _lastUserSpeechAt)}ms)" :
                             "unknown";
                Log($"⚠️ CanCreateResponse blocked ({reason}) - skipping response.create");
                return;
            }

            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            Log(bypassTranscriptGuard ? "🔄 response.create sent (tool bypass)" : "🔄 response.create sent");
        }
        finally
        {
            Interlocked.Exchange(ref _responseQueued, 0);
        }
    }

    // =========================
    // CONSTRUCTOR
    // =========================
    public OpenAIRealtimeG711Client(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "sage",
        G711Codec codec = G711Codec.MuLaw)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _codec = codec;
        _silenceByte = codec == G711Codec.ALaw ? (byte)0xD5 : (byte)0xFF;
    }

    // =========================
    // RESET CALL STATE
    // =========================
    private void ResetCallState(string? caller)
    {
        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);  // v2.4: Detect language for Whisper hint
        
        // Reset per-call flags (NOT _disposed - that's object lifetime)
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _sessionCreated, 0);
        Interlocked.Exchange(ref _sessionUpdated, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);  // v2.5
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastCompletedResponseId = null;

        // Reset timestamps
        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
        Volatile.Write(ref _responseCreatedAt, 0);
        Volatile.Write(ref _speechStoppedAt, 0);  // v2.5

        // Reset stats
        Interlocked.Exchange(ref _audioFramesSent, 0);
        Interlocked.Exchange(ref _audioChunksReceived, 0);

        // Clear audio queue
        ClearPendingFrames();

        // Reset DSP state
        TtsPreConditioner.Reset();
    }

    // =========================
    // CONNECT
    // =========================
    public async Task ConnectAsync(string? callerPhone = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ResetCallState(callerPhone);
        InitializeLogger();  // Start non-blocking logger

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var codecName = _codec == G711Codec.ALaw ? "A-law" : "μ-law";
        Log($"📞 Connecting (caller: {callerPhone ?? "unknown"}, codec: {codecName} passthrough 8kHz)");

        await _ws.ConnectAsync(
            new Uri($"wss://api.openai.com/v1/realtime?model={_model}"),
            linked.Token);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Log("✅ Connected to OpenAI");
        OnConnected?.Invoke();

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _keepaliveTask = Task.Run(KeepaliveLoopAsync);
    }

    /// <summary>
    /// Keepalive loop monitors connection health every 20 seconds.
    /// </summary>
    private async Task KeepaliveLoopAsync()
    {
        const int KEEPALIVE_INTERVAL_MS = 20000;

        try
        {
            while (!(_cts?.IsCancellationRequested ?? true))
            {
                await Task.Delay(KEEPALIVE_INTERVAL_MS, _cts!.Token).ConfigureAwait(false);

                if (_ws?.State != WebSocketState.Open)
                {
                    Log($"⚠️ Keepalive: WebSocket disconnected (state: {_ws?.State})");
                    break;
                }

                var responseActive = Volatile.Read(ref _responseActive) == 1;
                var callEnded = Volatile.Read(ref _callEnded) == 1;
                Log($"💓 Keepalive: connected (response_active={responseActive}, call_ended={callEnded})");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"⚠️ Keepalive loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Wait for session.created and session.updated, then configure session.
    /// </summary>
    public async Task WaitForSessionAndConfigureAsync()
    {
        // Wait for session.created
        for (int i = 0; i < 100 && _sessionCreated == 0; i++)
            await Task.Delay(50);

        if (_sessionCreated == 0)
            throw new TimeoutException("session.created not received");

        Log("✅ Session created, configuring...");
        await ConfigureSessionAsync();

        // Wait for session.updated
        Log("⏳ Waiting for session.updated...");
        for (int i = 0; i < 100 && _sessionUpdated == 0; i++)
            await Task.Delay(50);

        if (_sessionUpdated == 0)
            throw new TimeoutException("session.updated not received");

        var codecName = _codec == G711Codec.ALaw ? "A-law" : "μ-law";
        Log($"✅ Session configured (call={_callerId}, input={codecName}@8kHz, output={codecName}@8kHz, voice={_voice})");
    }

    // =========================
    // AUDIO INPUT (SIP → OPENAI)
    // =========================
    /// <summary>
    /// v4.0: Send A-law audio directly to OpenAI (no decoding, no resampling).
    /// SIP PCMA (8kHz A-law) → OpenAI g711_alaw passthrough.
    /// </summary>
    public async Task SendALawAsync(byte[] alawFrame)
    {
        if (!IsConnected) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;
        if (alawFrame == null || alawFrame.Length == 0) return;

        // Echo guard: Only check RTP playout completion time
        if (NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 200)
            return;

        var msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(alawFrame)
        });

        await SendBytesAsync(msg);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());

        var count = Interlocked.Increment(ref _audioFramesSent);
        if (count % 50 == 0)
            Log($"📤 Sent {count} audio frames (g711_alaw native 8kHz)");
    }

    /// <summary>
    /// Legacy μ-law support - converts to A-law before sending.
    /// Use SendALawAsync directly if your SIP uses PCMA (payload type 8).
    /// </summary>
    public async Task SendMuLawAsync(byte[] ulawFrame)
    {
        if (!IsConnected || ulawFrame == null || ulawFrame.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // Convert μ-law to A-law (both are 8kHz, 8-bit)
        var alawFrame = ConvertMuLawToALaw(ulawFrame);
        await SendALawAsync(alawFrame).ConfigureAwait(false);
    }

    /// <summary>
    /// Send PCM audio. Encodes to A-law before sending.
    /// For best quality, use SendALawAsync with native PCMA.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 8000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;

        var samples = Audio.AudioCodecs.BytesToShorts(pcmData);

        if (sampleRate != SAMPLE_RATE)
            samples = Audio.AudioCodecs.Resample(samples, sampleRate, SAMPLE_RATE);

        byte[] encoded = Audio.AudioCodecs.ALawEncode(samples);
        await SendALawAsync(encoded).ConfigureAwait(false);
    }

    /// <summary>
    /// Convert μ-law byte to A-law byte (both are 8-bit, 8kHz).
    /// </summary>
    private static byte[] ConvertMuLawToALaw(byte[] ulawData)
    {
        var alawData = new byte[ulawData.Length];
        for (int i = 0; i < ulawData.Length; i++)
        {
            short pcm = Audio.AudioCodecs.MuLawDecode(new[] { ulawData[i] })[0];
            alawData[i] = Audio.AudioCodecs.ALawEncode(new[] { pcm })[0];
        }
        return alawData;
    }

    /// <summary>
    /// Clear OpenAI's input audio buffer. Call this when Ada starts speaking
    /// to prevent stale audio from being transcribed.
    /// </summary>
    private async Task ClearInputAudioBufferAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            await SendJsonAsync(new { type = "input_audio_buffer.clear" }).ConfigureAwait(false);
            Log("🧹 Cleared OpenAI input audio buffer");
        }
        catch { }
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
                var res = await _ws!.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));

                if (res.EndOfMessage)
                {
                    await ProcessMessageAsync(sb.ToString());
                    sb.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ Receive error: {ex.Message}");
        }

        Log("🔄 Receive loop ended");
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        OnDisconnected?.Invoke();
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
                    Log("📋 session.created received");
                    Interlocked.Exchange(ref _sessionCreated, 1);
                    break;

                case "session.updated":
                    Log("📋 session.updated received");
                    Interlocked.Exchange(ref _sessionUpdated, 1);
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                    {
                        await SendGreetingAsync().ConfigureAwait(false);
                    }
                    break;

                case "response.created":
                {
                    var responseId = TryGetResponseId(doc.RootElement);
                    if (responseId != null && _activeResponseId == responseId)
                        break; // Ignore duplicate

                    _activeResponseId = responseId;
                    Interlocked.Exchange(ref _responseActive, 1);

                    // Record timestamp for transcript guard
                    Volatile.Write(ref _responseCreatedAt, NowMs());

                    // v2.6: Clear audio buffer ONLY HERE on response.created
                    // This is the ONLY place buffer should be cleared
                    _ = ClearInputAudioBufferAsync();

                    Log("🤖 AI response started");
                    OnResponseStarted?.Invoke();
                    break;
                }

                case "response.done":
                {
                    var responseId = TryGetResponseId(doc.RootElement);

                    // Ignore duplicate response.done for same ID
                    if (responseId == null || responseId == _lastCompletedResponseId)
                        break;

                    // Ignore stale response.done for different ID
                    if (_activeResponseId != null && responseId != _activeResponseId)
                        break;

                    _lastCompletedResponseId = responseId;
                    _activeResponseId = null;

                    Interlocked.Exchange(ref _responseActive, 0);
                    
                    // v2.8: Check if this response actually addressed the user's input
                    // If transcript is no longer pending, the AI already processed it
                    var transcriptStillPending = Volatile.Read(ref _transcriptPending) == 1;
                    
                    Log("🤖 AI response completed");
                    OnResponseCompleted?.Invoke();

                    // Flush deferred response ONLY if transcript is still pending
                    // (meaning the completed response didn't consume the user input)
                    if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1 && transcriptStillPending)
                    {
                        Log("🔄 Flushing deferred response.create");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(20).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) == 0 && Volatile.Read(ref _disposed) == 0)
                            {
                                await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                                Log("🔄 response.create sent (deferred)");
                            }
                        });
                    }

                    // NOTE: No-reply watchdog is handled by G711CallFeatures.OnPlayoutComplete()
                    // to avoid duplicate watchdogs causing double-responses

                    break;
                }

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                    {
                        var b64 = deltaEl.GetString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            // v4.0: OpenAI sends g711_alaw directly - no PCM conversion, no resampling
                            var alawBytes = Convert.FromBase64String(b64);
                            
                            if (alawBytes.Length > 0)
                            {
                                // Fire raw A-law for SIP
                                OnG711Audio?.Invoke(alawBytes);

                                var count = Interlocked.Increment(ref _audioChunksReceived);
                                if (count % 10 == 0)
                                    Log($"📢 Received {count} native A-law chunks ({alawBytes.Length}B)");
                            }
                        }
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var d))
                        OnAdaSpeaking?.Invoke(d.GetString() ?? "");
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var transcriptEl))
                    {
                        var text = transcriptEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            Log($"💬 Ada: {text}");
                            OnTranscript?.Invoke($"Ada: {text}");

                            // Goodbye detection watchdog
                            if (text.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) &&
                                (text.Contains("Taxibot", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("Thank you for using", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("for calling", StringComparison.OrdinalIgnoreCase)))
                            {
                                Log("👋 Goodbye phrase detected - arming 5s hangup watchdog");
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(5000).ConfigureAwait(false);
                                    if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                        return;

                                    Log("⏰ Goodbye watchdog triggered - forcing call end");
                                    Interlocked.Exchange(ref _ignoreUserAudio, 1);
                                    SignalCallEnded("goodbye_watchdog");
                                });
                            }
                        }
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                {
                    // v2.6: Transcript arrived - clear the pending flag
                    Interlocked.Exchange(ref _transcriptPending, 0);
                    
                    if (doc.RootElement.TryGetProperty("transcript", out var userTranscript))
                    {
                        var text = userTranscript.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            // Calculate timing for logging
                            var msSinceSpeechStopped = NowMs() - Volatile.Read(ref _speechStoppedAt);
                            var msSinceResponseCreated = NowMs() - Volatile.Read(ref _responseCreatedAt);
                            
                            if (Volatile.Read(ref _responseActive) == 1)
                            {
                                Log($"📝 Transcript arrived {msSinceSpeechStopped}ms after speech stopped, {msSinceResponseCreated}ms after response.created: {text}");
                            }

                            Log($"👤 User: {text}");
                            OnTranscript?.Invoke($"You: {text}");
                        }
                    }
                    
                    // v2.6: Now that transcript is available, trigger response if not already active
                    // This ensures OpenAI responds AFTER the transcript settles, not before
                    if (Volatile.Read(ref _responseActive) == 0)
                    {
                        _ = QueueResponseCreateAsync(delayMs: 50, waitForCurrentResponse: false);
                    }
                    break;
                }

                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId); // Cancel any pending no-reply watchdog
                    Interlocked.Exchange(ref _transcriptPending, 1);  // v2.5: Mark transcript pending
                    Log("✂️ Barge-in detected");
                    OnBargeIn?.Invoke();
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Volatile.Write(ref _speechStoppedAt, NowMs());  // v2.5: Track when speech ended
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (awaiting transcript)");
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var errorEl))
                    {
                        var message = errorEl.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString()
                            : "Unknown error";
                        if (!string.IsNullOrEmpty(message) &&
                            !message.Contains("buffer too small", StringComparison.OrdinalIgnoreCase) &&
                            !message.Contains("not found", StringComparison.OrdinalIgnoreCase))
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

        try
        {
            if (!root.TryGetProperty("name", out var n) || !root.TryGetProperty("call_id", out var c))
                return;

            var toolName = n.GetString();
            var toolCallId = c.GetString();
            if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(toolCallId))
                return;

            var argsJson = root.TryGetProperty("arguments", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(argsJson))
                argsJson = "{}";

            Log($"🔧 Tool: {toolName}");

            object result;
            var handler = OnToolCall;
            if (handler == null)
            {
                result = new { error = "No tool handler configured" };
            }
            else
            {
                using var argsDoc = JsonDocument.Parse(argsJson);
                result = await handler(toolName!, toolCallId!, argsDoc.RootElement.Clone()).ConfigureAwait(false);
            }

            await SendToolResultAsync(toolCallId!, result).ConfigureAwait(false);
            
            // CRITICAL: After tool result, the old response is done - clear state immediately
            // The old response won't send response.done, a NEW response will be created
            Interlocked.Exchange(ref _responseActive, 0);
            
            // Trigger new response immediately - BYPASS transcript guard since we have tool result
            await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0, bypassTranscriptGuard: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"⚠️ Tool handler error: {ex.Message}");
        }
    }

    private async Task SendToolResultAsync(string callId, object result)
    {
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(result)
            }
        }).ConfigureAwait(false);
    }


    // =========================
    // SESSION CONFIG
    // =========================
    private async Task ConfigureSessionAsync()
    {
        var inputCodec = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
        var outputCodec = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";

        Log($"🎧 Configuring session: input={inputCodec}@8kHz, output={outputCodec}@8kHz (NATIVE G.711), voice={_voice}");

        await SendJsonAsync(new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                voice = _voice,
                instructions = GetDefaultInstructions(),
                input_audio_format = inputCodec,
                output_audio_format = outputCodec,
                input_audio_transcription = new { model = "whisper-1", language = _detectedLanguage },  // v2.4: Language hint
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.4,          // Slightly higher threshold to reduce false triggers
                    prefix_padding_ms = 450,  // More padding for natural speech
                    silence_duration_ms = 900 // Longer pause before Ada responds (less racy)
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.5  // Lower for more predictable/strict behavior
            }
        });
    }

    private static string GetDefaultInstructions() => @"
You are Ada, a friendly taxi booking assistant for Voice Taxibot.

CURRENCY: Always use Euros (€) for fares.

## VOICE STYLE

Speak naturally, like a friendly professional taxi dispatcher.
- Warm, calm, confident tone
- Clear pronunciation of names and addresses
- Short pauses between phrases
- Never rush or sound robotic
- Patient and relaxed pace

## ABSOLUTE RULES - VIOLATION FORBIDDEN

### RULE 1: NEVER ANNOUNCE A BOOKING WITHOUT CALLING book_taxi FIRST
- You are FORBIDDEN from saying 'booked', 'confirmed', 'your taxi is on the way', or giving a booking reference UNLESS you have FIRST called book_taxi(action=confirmed) and received a booking_ref in the tool response.
- If you say a booking is complete without calling the tool, THE BOOKING DOES NOT EXIST.
- The ONLY valid booking reference format is 'TAXI-YYYYMMDDHHMMSS' - if you invent ANY other format (e.g., TX1234), you have FAILED.

### RULE 2: CALL sync_booking_data AFTER EVERY USER INPUT
- After the user provides name, pickup, destination, passengers, or time → call sync_booking_data IMMEDIATELY
- Include ALL the booking fields you know in the call (not just the new one)

### RULE 3: COMPLETE THE ENTIRE FLOW
- NEVER abandon a call mid-conversation
- ALWAYS end with end_call after goodbye

## MANDATORY BOOKING FLOW

1. Greet: 'Hello, welcome to the Voice Taxibot demo. I'm Ada, your taxi booking assistant. What's your name?'
2. Get name → sync_booking_data(caller_name) → 'Hi [name]! Where would you like to be picked up from?'
3. Get pickup → sync_booking_data(pickup, caller_name) → 'And where would you like to go?'
4. Get destination → sync_booking_data(pickup, destination, caller_name) → 'How many passengers?'
5. Get passengers → sync_booking_data(all fields) → 'And what time would you like to be picked up?'
6. Get time → sync_booking_data(all fields) → Recap: 'So that's [passengers] passengers from [pickup] to [destination], [time]. Is that correct?'
7. User confirms → 'Shall I get you a price?'
8. User says yes → call book_taxi(action=request_quote, pickup=X, destination=Y, passengers=N, pickup_time=T)
9. Tool returns fare/eta → 'The fare is [fare], and the driver will arrive in [eta]. Shall I confirm this booking?'
10. User confirms → call book_taxi(action=confirmed, pickup=X, destination=Y, passengers=N, pickup_time=T) IMMEDIATELY
11. Tool returns booking_ref → 'Your taxi is booked, reference [booking_ref]. You'll receive a WhatsApp confirmation. Is there anything else I can help with?'
12. User says no/thanks → 'Thank you for using Voice Taxibot. Goodbye!' → call end_call(reason='booking_complete')

## CONFIRMATION DETECTION

These phrases mean YES - proceed immediately:
'yes', 'yeah', 'yep', 'sure', 'ok', 'okay', 'correct', 'that's right', 'go ahead', 'book it', 'please do', 'confirm', 'that's fine'
";

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
                properties = new System.Collections.Generic.Dictionary<string, object>
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
            description = "Request quote or confirm booking",
            parameters = new
            {
                type = "object",
                properties = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["action"] = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                    // Booking snapshot (optional but strongly recommended)
                    ["caller_name"] = new { type = "string" },
                    ["pickup"] = new { type = "string" },
                    ["destination"] = new { type = "string" },
                    ["passengers"] = new { type = "integer" },
                    ["pickup_time"] = new { type = "string" }
                },
                required = new[] { "action" }
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
                properties = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["reason"] = new { type = "string" }
                },
                required = new[] { "reason" }
            }
        }
    };

    // =========================
    // GREETING
    // =========================
    public async Task SendGreetingAsync(string greeting = "Hello, welcome to Voice Taxibot. May I have your name?")
    {
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = greeting
            }
        });
        Log("📢 Greeting sent");
    }

    // =========================
    // CALL END
    // =========================
    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 0)
        {
            Log($"📴 Call ended: {reason}");
            OnCallEnded?.Invoke();
        }
    }

    // =========================
    // CALLED BY RTP PLAYOUT
    // =========================
    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());
    }

    // =========================
    // LEGACY QUEUE METHODS
    // =========================
    public byte[]? GetNextMuLawFrame()
    {
        if (_outboundQueue.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _outboundQueueCount);
            return frame;
        }
        return null;
    }

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _outboundQueueCount, 0);
    }

    // =========================
    // HELPERS
    // =========================
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

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(OpenAIRealtimeG711Client));
    }

    // =========================
    // DISCONNECT
    // =========================
    public async Task DisconnectAsync()
    {
        SignalCallEnded("disconnect");

        try
        {
            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        SignalCallEnded("dispose");

        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _sendMutex.Dispose();
    }

    // =========================
    // LANGUAGE DETECTION (v2.4)
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
        if (string.IsNullOrEmpty(phone))
            return "en";

        var norm = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

        // Dutch local mobile format
        if (norm.StartsWith("06") && norm.Length == 10 && norm.All(char.IsDigit))
            return "nl";
        if (norm.StartsWith("0") && !norm.StartsWith("00") && norm.Length == 10)
            return "nl";

        // Strip international prefix
        if (norm.StartsWith("+"))
            norm = norm.Substring(1);
        else if (norm.StartsWith("00") && norm.Length > 4)
            norm = norm.Substring(2);

        // Check country code
        if (norm.Length >= 2)
        {
            var code = norm.Substring(0, 2);
            if (CountryCodeToLanguage.TryGetValue(code, out var lang))
            {
                Log($"🌍 Language: {lang} (country code {code})");
                return lang;
            }
        }

        Log($"🌍 Language: en (default)");
        return "en";
    }
}
