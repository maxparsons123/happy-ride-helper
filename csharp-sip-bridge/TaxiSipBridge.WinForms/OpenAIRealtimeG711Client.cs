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
/// Version 2.4: Aligned with OpenAIRealtimeClient architecture.
/// 
/// Key improvements:
/// - Proper ResetCallState() for clean per-call state
/// - Keepalive loop for connection health monitoring
/// - Deferred response handling (queue response.create if one is active)
/// - Transcript guard (ignores stale transcripts within 400ms of response.created)
/// - No-reply watchdog (prompts user after silence)
/// - CanCreateResponse() gate with multiple conditions
/// - RTP playout completion as source of truth for echo guard
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "2.4-aligned";

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
    private long _lastAdaFinishedAt;    // RTP playout completion time (source of truth for echo guard)
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt;    // For transcript guard

    // Tracks current OpenAI response id to ignore duplicate events
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // =========================
    // CALLER STATE (per-call)
    // =========================
    private string _callerId = "";

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
    private void Log(string msg) => OnLog?.Invoke(msg);

    // =========================
    // RESPONSE GATE
    // =========================
    private bool CanCreateResponse()
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               Volatile.Read(ref _responseQueued) == 0 &&
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300;
    }

    /// <summary>
    /// Queue a response.create. Does NOT set _responseActive manually.
    /// Waits for any active response to complete first, then sends response.create.
    /// OpenAI's response.created event will set _responseActive = 1.
    /// </summary>
    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1)
            return;

        try
        {
            // Wait for any active response to complete (up to 5s)
            if (waitForCurrentResponse)
            {
                for (int i = 0; i < 100 && Volatile.Read(ref _responseActive) == 1; i++)
                    await Task.Delay(50).ConfigureAwait(false);
            }

            await Task.Delay(delayMs).ConfigureAwait(false);

            // If response is still active after waiting, defer to response.done handler
            if (Volatile.Read(ref _responseActive) == 1)
            {
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                Log("⏳ Response still active - deferring response.create");
                return;
            }

            if (!CanCreateResponse())
                return;

            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            Log("🔄 response.create sent");
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
        
        // Reset per-call flags (NOT _disposed - that's object lifetime)
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _sessionCreated, 0);
        Interlocked.Exchange(ref _sessionUpdated, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastCompletedResponseId = null;

        // Reset timestamps
        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
        Volatile.Write(ref _responseCreatedAt, 0);

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

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var codecName = _codec == G711Codec.ALaw ? "A-law" : "μ-law";
        Log($"📞 Connecting (caller: {callerPhone ?? "unknown"}, codec: PCM24→DSP→{codecName})");

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
        Log($"✅ Session configured (PCM16@24kHz → TtsPreConditioner → {codecName}, voice={_voice})");
    }

    // =========================
    // AUDIO INPUT (SIP → OPENAI)
    // =========================
    public async Task SendMuLawAsync(byte[] frame)
    {
        if (!IsConnected) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;
        if (frame.Length != FRAME_BYTES) return;

        // Echo guard: Only check RTP playout completion time
        if (NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 200)
            return;

        var msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(frame)
        });

        await SendBytesAsync(msg);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());

        var count = Interlocked.Increment(ref _audioFramesSent);
        if (count % 50 == 0)
        {
            var codecName = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
            Log($"📤 Sent {count} audio frames ({codecName} native 8kHz)");
        }
    }

    /// <summary>
    /// Send PCM audio. Encodes to G.711 for native passthrough.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 8000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;

        var samples = Audio.AudioCodecs.BytesToShorts(pcmData);

        if (sampleRate != SAMPLE_RATE)
            samples = Audio.AudioCodecs.Resample(samples, sampleRate, SAMPLE_RATE);

        byte[] encoded = _codec == G711Codec.ALaw
            ? Audio.AudioCodecs.ALawEncode(samples)
            : Audio.AudioCodecs.MuLawEncode(samples);

        await SendMuLawAsync(encoded).ConfigureAwait(false);
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

                    // Clear OpenAI's input audio buffer when Ada starts speaking
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
                    Log("🤖 AI response completed");
                    OnResponseCompleted?.Invoke();

                    // Commit audio buffer after response completes
                    await SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (turn finalized)");

                    // Flush deferred response if pending
                    if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
                    {
                        Log("🔄 Flushing deferred response.create");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(50).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) == 0 && Volatile.Read(ref _disposed) == 0)
                            {
                                await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                                Log("🔄 response.create sent (deferred)");
                            }
                        });
                    }

                    // No-reply watchdog: prompt user after silence
                    var watchdogDelayMs = 15000;
                    var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(watchdogDelayMs).ConfigureAwait(false);

                        if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
                        if (Volatile.Read(ref _responseActive) == 1) return;

                        Log($"⏰ No-reply watchdog triggered ({watchdogDelayMs}ms) - prompting user");

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
                                        text = "[SILENCE DETECTED] The user has not responded. Gently ask if they're still there or repeat your last question briefly."
                                    }
                                }
                            }
                        }).ConfigureAwait(false);

                        await QueueResponseCreateAsync(delayMs: 20, waitForCurrentResponse: false).ConfigureAwait(false);
                    });

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
                            var pcm24Bytes = Convert.FromBase64String(b64);

                            OnPcm24Audio?.Invoke(pcm24Bytes);

                            var g711Bytes = ProcessPcm24ToG711(pcm24Bytes);
                            if (g711Bytes.Length > 0)
                            {
                                OnG711Audio?.Invoke(g711Bytes);
                            }

                            var count = Interlocked.Increment(ref _audioChunksReceived);
                            if (count % 10 == 0)
                            {
                                var codecName = _codec == G711Codec.ALaw ? "A-law" : "μ-law";
                                Log($"📢 Received {count} audio chunks (PCM24→DSP→{codecName}), in={pcm24Bytes.Length}B out={g711Bytes.Length}B");
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
                    if (doc.RootElement.TryGetProperty("transcript", out var userTranscript))
                    {
                        var text = userTranscript.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            // TRANSCRIPT GUARD: Ignore stale transcripts within 400ms of response.created
                            var msSinceResponseCreated = NowMs() - Volatile.Read(ref _responseCreatedAt);
                            if (msSinceResponseCreated < 400 && Volatile.Read(ref _responseActive) == 1)
                            {
                                Log($"🚫 Ignoring stale transcript ({msSinceResponseCreated}ms after response.created): {text}");
                                break;
                            }

                            Log($"👤 User: {text}");
                            OnTranscript?.Invoke($"You: {text}");
                        }
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId); // Cancel any pending no-reply watchdog
                    Log("✂️ Barge-in detected");
                    OnBargeIn?.Invoke();
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
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
            await QueueResponseCreateAsync().ConfigureAwait(false);
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
    // PCM24 → G.711 PROCESSING (STREAMING)
    // =========================
    /// <summary>
    /// Process variable-length 24kHz PCM from OpenAI → G.711 for SIP.
    /// Uses streaming DSP that handles any chunk size (not fixed 20ms frames).
    /// </summary>
    private byte[] ProcessPcm24ToG711(byte[] pcm24Bytes)
    {
        if (pcm24Bytes == null || pcm24Bytes.Length == 0)
            return Array.Empty<byte>();

        // Use STREAMING processor for variable-length audio from OpenAI
        short[] samples24k = TtsPreConditioner.BytesToPcm(pcm24Bytes);
        short[] samples8k = TtsPreConditioner.ProcessStreaming(samples24k);

        if (samples8k.Length == 0)
            return Array.Empty<byte>();

        byte[] g711;
        if (_codec == G711Codec.ALaw)
            g711 = AudioCodecs.ALawEncode(samples8k);
        else
            g711 = AudioCodecs.MuLawEncode(samples8k);

        return g711;
    }

    // =========================
    // SESSION CONFIG
    // =========================
    private async Task ConfigureSessionAsync()
    {
        var inputCodec = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
        var outputCodec = "pcm16"; // 24kHz PCM - we apply DSP locally

        Log($"🎧 Configuring session: input={inputCodec}@8kHz, output={outputCodec}@24kHz (DSP→G.711), voice={_voice}");

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
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.35,
                    prefix_padding_ms = 400,
                    silence_duration_ms = 700
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        });
    }

    private static string GetDefaultInstructions() => @"
You are Ada, a friendly taxi booking assistant for Voice Taxibot.

STYLE: Be concise, warm, and professional. Keep responses under 15 words unless reciting details.

## STRICT RULES - FOLLOW EXACTLY

1. You MUST call sync_booking_data IMMEDIATELY after EVERY user response containing booking info
2. You MUST complete the ENTIRE flow - NEVER stop mid-conversation
3. You MUST call book_taxi(action=confirmed) when user confirms the quote
4. You MUST call end_call when user says goodbye or has nothing else
5. NEVER skip steps or leave the call hanging

## MANDATORY BOOKING FLOW (COMPLETE ALL STEPS)

Step 1: Greet → 'Hello, welcome to Voice Taxibot. May I have your name please?'
Step 2: Get name → sync_booking_data(caller_name) → Ask pickup address
Step 3: Get pickup → sync_booking_data(pickup) → Ask destination  
Step 4: Get destination → sync_booking_data(destination) → Ask passenger count
Step 5: Get passengers → sync_booking_data(passengers) → Ask pickup time
Step 6: Get time → sync_booking_data(pickup_time) → Summarize: 'So that's [passengers] from [pickup] to [destination] at [time]. Is that correct?'
Step 7: User confirms details → Ask 'Shall I get you a price?'
Step 8: User says yes → call book_taxi(action=request_quote)
Step 9: Receive fare → Tell user: 'The fare is [fare], driver arrives in [eta]. Shall I confirm this booking?'
Step 10: User confirms → call book_taxi(action=confirmed) IMMEDIATELY
Step 11: Receive booking ref → Say: 'Your booking reference is [ref]. You will receive a WhatsApp confirmation. Is there anything else I can help with?'
Step 12: User says no → Say goodbye and call end_call(reason='booking_complete')

## CRITICAL: CONFIRMATION DETECTION

When user says ANY of these, you MUST proceed to the next action:
- 'yes', 'yeah', 'yep', 'sure', 'ok', 'okay', 'correct', 'that's right', 'go ahead', 'book it', 'please', 'confirm'

After Step 10 confirmation → call book_taxi(action=confirmed) WITHOUT DELAY
After Step 12 'no more' → call end_call(reason='booking_complete') WITHOUT DELAY
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
                    ["action"] = new { type = "string", @enum = new[] { "request_quote", "confirmed" } }
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
}
