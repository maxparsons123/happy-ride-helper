using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using taxibridgemain;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API client with correct response lifecycle handling.
/// Key rule: DO NOT set _responseActive manually. Only OpenAI events control it.
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    public const string VERSION = "10.3";

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _dispatchWebhookUrl;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);
    private int _bsqdLoggerAttached;

    // =========================
    // THREAD-SAFE STATE
    // =========================
    private int _disposed;         // Object lifetime only (NEVER reset)
    private int _callEnded;        // Per-call
    private int _responseActive;   // OBSERVED only (set only by response.created / response.done)
    private int _responseQueued;   // Per-call
    private int _greetingSent;     // Per-call
    private int _ignoreUserAudio;  // Per-call (set after goodbye starts)
    private int _deferredResponsePending; // Per-call (queued response after response.done)
    private int _noReplyWatchdogId;      // Incremented to cancel stale watchdogs
    private int _transcriptPending;      // v2.5: Block response.create until transcript arrives
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt;     // For transcript guard (ignore stale transcripts)
    private long _speechStoppedAt;       // v2.5: Track when user stopped speaking for settle timer

    // Tracks current OpenAI response id to ignore duplicate events
    private string? _activeResponseId;
    private string? _lastCompletedResponseId; // Prevents duplicate response.done logs

    // =========================
    // CALL STATE
    // =========================
    private string _callerId = "";
    private string _detectedLanguage = "en";
    private readonly BookingState _booking = new();
    private bool _awaitingConfirmation;
    private string _lastAdaTranscript = "";       // v10.4: Track Ada's last spoken text
    private volatile bool _toolCalledInResponse;   // v10.4: Whether a tool was called in the current response

    // =========================
    // WS + LIFECYCLE
    // =========================
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;
    private Task? _keepaliveTask;

    // CRITICAL: ClientWebSocket.SendAsync must be single-flight to avoid interleaving frames
    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    // =========================
    // AUDIO OUTPUT
    // =========================
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 800;

    // =========================
    // EVENTS
    // =========================
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnResponseStarted;
    public event Action? OnResponseCompleted;
    public event Action? OnCallEnded;
    public event Action<string>? OnAdaSpeaking;
    public event Action<BookingState>? OnBookingUpdated;

    // =========================
    // PROPERTIES
    // =========================
    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 &&
        _ws?.State == WebSocketState.Open;

    public int PendingFrameCount => _outboundQueue.Count;

    // =========================
    // CONSTRUCTORS
    // =========================
    public OpenAIRealtimeClient(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _dispatchWebhookUrl = dispatchWebhookUrl;
    }

    // =========================
    // RESPONSE GATE
    // =========================
    private bool CanCreateResponse()
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               Volatile.Read(ref _responseQueued) == 0 &&
               Volatile.Read(ref _transcriptPending) == 0 &&  // v3.3: Wait for transcript before responding
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300; // Don't respond while user is still talking
    }

    /// <summary>
    /// Queue a response.create. Does NOT set _responseActive manually.
    /// Waits for any active response to complete first, then sends response.create.
    /// OpenAI's response.created event will set _responseActive = 1.
    /// </summary>
    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true, int maxWaitMs = 1000)
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

            if (!CanCreateResponse())
                return;

            // DO NOT set _responseActive = 1 here — let OpenAI's response.created do it
            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            Log(waitForCurrentResponse ? "🔄 response.create sent" : "🔄 response.create sent (forced after tool)");
        }
        finally
        {
            Interlocked.Exchange(ref _responseQueued, 0);
        }
    }

    // =========================
    // AUDIO INPUT
    // =========================
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData == null || ulawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // Echo guard: skip audio right after AI speaks (but bypass if awaiting confirmation)
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 180)
            return;

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        var pcm24k = Resample8kTo24k(pcm8k);
        await SendAudioAsync(pcm24k).ConfigureAwait(false);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        byte[] pcm24k = sampleRate == 24000
            ? pcmData
            : AudioCodecs.ShortsToBytes(
                AudioCodecs.Resample(AudioCodecs.BytesToShorts(pcmData), sampleRate, 24000));

        await SendAudioToOpenAIAsync(pcm24k).ConfigureAwait(false);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    private async Task SendAudioToOpenAIAsync(byte[] pcm24k)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(pcm24k)
            });

            await SendTextAsync(msg).ConfigureAwait(false);
        }
        catch { /* keep call alive */ }
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
    // AUDIO OUTPUT
    // =========================
    public byte[]? GetNextMuLawFrame() => _outboundQueue.TryDequeue(out var f) ? f : null;

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var pcm24k = Convert.FromBase64String(base64);
            OnPcm24Audio?.Invoke(pcm24k);

            // Downsample 24kHz → 8kHz with 3-tap FIR filter (smoother, less aliasing)
            var samples = AudioCodecs.BytesToShorts(pcm24k);
            var len = samples.Length / 3;
            var pcm8k = new short[len];

            for (int i = 0; i < len; i++)
            {
                int idx = i * 3;
                // Weighted average: 0.25, 0.5, 0.25 for smoother telephony audio
                pcm8k[i] = (short)(samples[idx] * 0.25f + samples[idx + 1] * 0.5f + samples[idx + 2] * 0.25f);
            }

            var ulaw = AudioCodecs.MuLawEncode(pcm8k);

            // Frame into 160-byte (20ms) chunks
            for (int i = 0; i < ulaw.Length; i += 160)
            {
                var frame = new byte[160];
                var count = Math.Min(160, ulaw.Length - i);
                Array.Copy(ulaw, i, frame, 0, count);
                if (count < 160) Array.Fill(frame, (byte)0xFF, count, 160 - count);

                while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                    _outboundQueue.TryDequeue(out _);

                _outboundQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio error: {ex.Message}");
        }
    }

    private static byte[] Resample8kTo24k(short[] pcm8k)
    {
        var pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            var s = pcm8k[i];
            int o = i * 3;
            pcm24k[o] = s;
            pcm24k[o + 1] = s;
            pcm24k[o + 2] = s;
        }
        return AudioCodecs.ShortsToBytes(pcm24k);
    }

    // =========================
    // CONNECT / DISCONNECT
    // =========================
    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ResetCallState(caller);
        EnsureBsqdLogger(); // ✅ ADD THIS LINE
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        Log($"📞 Connecting (caller: {caller ?? "unknown"}, lang: {_detectedLanguage})");
        await _ws.ConnectAsync(new Uri($"wss://api.openai.com/v1/realtime?model={_model}"), linked.Token)
                 .ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Log("✅ Connected to OpenAI");
        OnConnected?.Invoke();

        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        _keepaliveTask = Task.Run(KeepaliveLoopAsync);
    }

    /// <summary>
    /// Keepalive loop monitors connection health every 15 seconds.
    /// Uses WebSocket ping frames (handled internally by ClientWebSocket) for connection keepalive.
    /// Logs connection status periodically to detect silent disconnects.
    /// </summary>
    private async Task KeepaliveLoopAsync()
    {
        const int KEEPALIVE_INTERVAL_MS = 20000;

        try
        {
            while (!(_cts?.IsCancellationRequested ?? true))
            {
                await Task.Delay(KEEPALIVE_INTERVAL_MS, _cts!.Token).ConfigureAwait(false);

                // Check if WebSocket is still connected
                if (_ws?.State != WebSocketState.Open)
                {
                    Log($"⚠️ Keepalive: WebSocket disconnected (state: {_ws?.State})");
                    break;
                }

                // Log connection status (no actual message sent to avoid interference)
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

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        Log("📴 Disconnecting...");
        SignalCallEnded("disconnect");

        try
        {
            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            OnDisconnected?.Invoke();
        }
    }

    // =========================
    // RECEIVE LOOP
    // =========================
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (IsConnected && !(_cts?.IsCancellationRequested ?? true))
        {
            try
            {
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token)
                                       .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    await ProcessMessageAsync(sb.ToString()).ConfigureAwait(false);
                    sb.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"❌ Receive error: {ex.Message}");
                break;
            }
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
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;

            var type = typeEl.GetString();
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                case "session.created":
                    await ConfigureSessionAsync().ConfigureAwait(false);
                    break;

                case "session.updated":
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                        await SendGreetingAsync().ConfigureAwait(false);
                    break;

                case "response.created":
                    {
                        // OBSERVED start (OpenAI controlled) — this is the ONLY place we set _responseActive = 1
                        var responseId = TryGetResponseId(doc.RootElement);
                        if (responseId != null && _activeResponseId == responseId)
                            break; // Ignore duplicate

                        _activeResponseId = responseId;
                        _toolCalledInResponse = false;  // v10.4: Reset tool tracking
                        Interlocked.Exchange(ref _responseActive, 1);

                        // CRITICAL: Record timestamp for transcript guard.
                        Volatile.Write(ref _responseCreatedAt, NowMs());

                        // v2.6: Clear audio buffer ONLY HERE on response.created
                        // This is the ONLY place buffer should be cleared - prevents Whisper from
                        // transcribing stale audio while Ada is speaking
                        _ = ClearInputAudioBufferAsync();

                        Log("🤖 AI response started");
                        OnResponseStarted?.Invoke();
                        break;
                    }

                case "response.audio.delta":
                    {
                        if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                        {
                            var delta = deltaEl.GetString();
                            if (!string.IsNullOrEmpty(delta))
                                ProcessAudioDelta(delta);
                        }
                        break;
                    }

                case "response.audio_transcript.delta":
                    {
                        if (doc.RootElement.TryGetProperty("delta", out var d))
                            OnAdaSpeaking?.Invoke(d.GetString() ?? "");
                        break;
                    }

                case "response.audio_transcript.done":
                    {
                        if (doc.RootElement.TryGetProperty("transcript", out var t) &&
                            !string.IsNullOrWhiteSpace(t.GetString()))
                        {
                            var text = t.GetString()!;
                            _lastAdaTranscript = text;  // v10.4: Track for price-promise detection
                            Log($"💬 Ada: {text}");
                            OnTranscript?.Invoke($"Ada: {text}");

                            // Goodbye detection watchdog: if Ada says goodbye, force end_call after 5s
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

                                    Log("⏰ Goodbye watchdog triggered - forcing end_call");
                                    Interlocked.Exchange(ref _ignoreUserAudio, 1);
                                    SignalCallEnded("goodbye_watchdog");
                                });
                            }
                        }
                        break;
                    }

                case "conversation.item.input_audio_transcription.completed":
                    {
                        // v3.4 FIX: don't clear transcriptPending immediately.
                        // Clearing immediately can let a response start before Whisper has fully "settled",
                        // which correlates with clipped / crispy audio on some calls.
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(120).ConfigureAwait(false);
                            Interlocked.Exchange(ref _transcriptPending, 0);
                        });

                        if (doc.RootElement.TryGetProperty("transcript", out var u) &&
                            !string.IsNullOrWhiteSpace(u.GetString()))
                        {
                            var text = ApplySttCorrections(u.GetString()!);

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
                        break;
                    }


                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId); // Cancel any pending no-reply watchdog
                    // v2.5: Mark that we're expecting a transcript
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Volatile.Write(ref _speechStoppedAt, NowMs());  // v2.5: Track when speech ended
                    // v2.5: Commit audio buffer - OpenAI will transcribe and respond after settle time
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (awaiting transcript)");
                    break;

                case "response.done":
                    {
                        // OBSERVED end (OpenAI controlled) — this is the ONLY place we set _responseActive = 0
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
                        Volatile.Write(ref _lastAdaFinishedAt, NowMs());

                        // v2.6: DO NOT clear buffer here - only clear on response.created
                        // Clearing here cuts off words mid-turn

                        Log("🤖 AI response completed");
                        OnResponseCompleted?.Invoke();

                        // v10.4: Price-promise safety net
                        // If Ada said "get the price" / "moment while I" but no tool was called,
                        // force-inject a system instruction to call sync_booking_data
                        if (!_toolCalledInResponse && !string.IsNullOrEmpty(_lastAdaTranscript) &&
                            (_lastAdaTranscript.Contains("price", StringComparison.OrdinalIgnoreCase) ||
                             _lastAdaTranscript.Contains("moment while", StringComparison.OrdinalIgnoreCase) ||
                             _lastAdaTranscript.Contains("just a moment", StringComparison.OrdinalIgnoreCase) ||
                             _lastAdaTranscript.Contains("get the fare", StringComparison.OrdinalIgnoreCase)))
                        {
                            Log("⚠️ Price promise detected but no tool called - forcing sync_booking_data");
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(40).ConfigureAwait(false);
                                if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;

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
                                                text = "[SYSTEM] You promised the user a price but did not call any tool. You MUST call sync_booking_data or book_taxi NOW to get the fare. Do NOT speak again without calling the tool first."
                                            }
                                        }
                                    }
                                }).ConfigureAwait(false);

                                await QueueResponseCreateAsync(delayMs: 40, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                            });
                        }

                        // Flush deferred response if pending
                        if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
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

                        // No-reply watchdog: give the caller enough time to respond AFTER Ada finishes.
                        // NOTE: response.done can arrive slightly before the final audio finishes playout,
                        // so we intentionally use a more patient timeout.
                        var watchdogDelayMs = _awaitingConfirmation ? 20000 : 15000;
                        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(watchdogDelayMs).ConfigureAwait(false);

                            // Abort if cancelled, call ended, or user spoke
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
                    _toolCalledInResponse = true;  // v10.4: Mark tool was called
                    await HandleToolCallAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "error":
                    {
                        if (doc.RootElement.TryGetProperty("error", out var e) &&
                            e.TryGetProperty("message", out var m))
                        {
                            var msg = m.GetString() ?? "";
                            if (!msg.Contains("buffer too small", StringComparison.OrdinalIgnoreCase))
                                Log($"❌ OpenAI: {msg}");
                        }
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    private static string? TryGetResponseId(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("response", out var resp) &&
                resp.TryGetProperty("id", out var idEl))
                return idEl.GetString();
        }
        catch { }
        return null;
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

        if (!root.TryGetProperty("name", out var n) || !root.TryGetProperty("call_id", out var c))
            return;

        var name = n.GetString();
        var callId = c.GetString();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(callId))
            return;

        var args = ParseArgs(root);
        Log($"🔧 Tool: {name}");

        switch (name)
        {
            case "sync_booking_data":
                {
                    var (newPickup, newDest) = ApplyBookingSnapshotFromArgsWithTracking(args);

                    // v10.5: Fast-path for name-only syncs — no geocoding or fare calc needed
                    bool nameOnlySync = newPickup == null && newDest == null
                        && !args.ContainsKey("passengers") && !args.ContainsKey("pickup_time");

                    if (nameOnlySync)
                    {
                        Log($"⚡ Name-only sync (fast path): {_booking.Name}");
                        OnBookingUpdated?.Invoke(_booking);
                        await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);

                        // Inject state grounding
                        if (IsConnected)
                        {
                            var stateText = $"[BRIDGE STATE] Name={_booking.Name ?? "?"}, Pickup={_booking.Pickup ?? "?"}, Destination={_booking.Destination ?? "?"}, Passengers={_booking.Passengers?.ToString() ?? "?"}, Time={_booking.PickupTime ?? "?"}";
                            await SendJsonAsync(new
                            {
                                type = "conversation.item.create",
                                item = new
                                {
                                    type = "message",
                                    role = "system",
                                    content = new[] { new { type = "input_text", text = stateText } }
                                }
                            }).ConfigureAwait(false);
                            Log($"📋 State grounding injected: {stateText}");
                        }

                        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                        break;
                    }

                    await VerifyAndEnrichAddressesAsync(newPickup, newDest).ConfigureAwait(false);
                    OnBookingUpdated?.Invoke(_booking);

                    // v10.3: Auto-quote — trigger fare calculation when travel fields are filled
                    // Name is NOT required for fare calculation
                    bool travelFieldsFilled = !string.IsNullOrWhiteSpace(_booking.Pickup)
                        && !string.IsNullOrWhiteSpace(_booking.Destination)
                        && _booking.Passengers > 0
                        && !string.IsNullOrWhiteSpace(_booking.PickupTime);

                    object toolResult;
                    if (travelFieldsFilled && string.IsNullOrWhiteSpace(_booking.Fare))
                    {
                        try
                        {
                            Log("💰 Auto-quote: all travel fields filled, calculating fare...");
                            var aiResult = await FareCalculator.ExtractAddressesWithLovableAiAsync(
                                _booking.Pickup, _booking.Destination, _callerId).ConfigureAwait(false);

                            if (aiResult?.status == "clarification_needed")
                            {
                                Log("⚠️ Auto-quote: ambiguous addresses, asking user");
                                toolResult = new
                                {
                                    success = false,
                                    needs_clarification = true,
                                    pickup_options = aiResult.pickup?.alternatives ?? Array.Empty<string>(),
                                    destination_options = aiResult.dropoff?.alternatives ?? Array.Empty<string>(),
                                    message = "I found multiple locations. Please ask the caller which city or area they are in."
                                };
                            }
                            else
                            {
                                string resolvedP = aiResult?.pickup?.address ?? _booking.Pickup;
                                string resolvedD = aiResult?.dropoff?.address ?? _booking.Destination;

                                // v10.4: Use Gemini coordinates directly when available (skip OSM)
                                FareResult fareResult;
                                if (aiResult?.pickup?.lat != null && aiResult?.pickup?.lon != null &&
                                    aiResult?.dropoff?.lat != null && aiResult?.dropoff?.lon != null &&
                                    aiResult.pickup.lat.Value != 0 && aiResult.dropoff.lat.Value != 0)
                                {
                                    Log("✅ Using Gemini coordinates directly (skip OSM)");
                                    fareResult = FareCalculator.GetDefaultCalc().CalculateFromCoords(
                                        aiResult.pickup.lat.Value, aiResult.pickup.lon.Value,
                                        aiResult.dropoff.lat.Value, aiResult.dropoff.lon.Value);
                                    fareResult.PickupStreet = aiResult.pickup.street_name;
                                    fareResult.PickupNumber = aiResult.pickup.street_number;
                                    fareResult.PickupPostalCode = aiResult.pickup.postal_code;
                                    fareResult.PickupCity = aiResult.pickup.city ?? aiResult.detected_area;
                                    fareResult.PickupFormatted = resolvedP;
                                    fareResult.DestStreet = aiResult.dropoff.street_name;
                                    fareResult.DestNumber = aiResult.dropoff.street_number;
                                    fareResult.DestPostalCode = aiResult.dropoff.postal_code;
                                    fareResult.DestCity = aiResult.dropoff.city ?? aiResult.detected_area;
                                    fareResult.DestFormatted = resolvedD;
                                }
                                else
                                {
                                    fareResult = await FareCalculator.CalculateFareWithCoordsAsync(
                                        resolvedP, resolvedD, _callerId, skipEdgeExtraction: true).ConfigureAwait(false);
                                }

                                _booking.Fare = NormalizeEuroFare(fareResult.Fare);
                                _booking.Eta = fareResult.Eta;
                                _booking.PickupFormatted = fareResult.PickupFormatted;
                                _booking.DestFormatted = fareResult.DestFormatted;
                                _booking.PickupLat = fareResult.PickupLat;
                                _booking.PickupLon = fareResult.PickupLon;
                                _booking.DestLat = fareResult.DestLat;
                                _booking.DestLon = fareResult.DestLon;
                                _booking.PickupStreet ??= fareResult.PickupStreet;
                                _booking.PickupNumber ??= fareResult.PickupNumber ?? FareCalculator.ExtractHouseNumber(_booking.Pickup);
                                _booking.PickupPostalCode ??= fareResult.PickupPostalCode;
                                _booking.PickupCity ??= fareResult.PickupCity;
                                _booking.DestStreet ??= fareResult.DestStreet;
                                _booking.DestNumber ??= fareResult.DestNumber ?? FareCalculator.ExtractHouseNumber(_booking.Destination);
                                _booking.DestPostalCode ??= fareResult.DestPostalCode;
                                _booking.DestCity ??= fareResult.DestCity;

                                _awaitingConfirmation = true;
                                OnBookingUpdated?.Invoke(_booking);

                                var spokenFare = FormatFareForSpeech(_booking.Fare);
                                Log($"💰 Auto-quote result: {_booking.Fare} ({spokenFare}), ETA: {_booking.Eta}");
                                toolResult = new
                                {
                                    success = true,
                                    fare = _booking.Fare,
                                    fare_spoken = spokenFare,
                                    eta = _booking.Eta,
                                    message = $"ANNOUNCE THE FARE AND ASK FOR CONFIRMATION. The fare is {spokenFare} and ETA is {_booking.Eta}."
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"⚠️ Auto-quote failed: {ex.Message}");
                            toolResult = new { success = true, fare = "€12.50", fare_spoken = "12 euros 50", eta = "5 mins", message = "ANNOUNCE THE FARE AND ASK FOR CONFIRMATION." };
                        }
                    }
                    else
                    {
                        toolResult = new { success = true };
                    }

                    await SendToolResultAsync(callId, toolResult).ConfigureAwait(false);

                    // v7.6: State grounding — inject current state into conversation context
                    if (IsConnected)
                    {
                        var stateText = $"[BRIDGE STATE] Name={_booking.Name ?? "?"}, Pickup={_booking.Pickup ?? "?"}, Destination={_booking.Destination ?? "?"}, Passengers={_booking.Passengers?.ToString() ?? "?"}, Time={_booking.PickupTime ?? "?"}";
                        await SendJsonAsync(new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "message",
                                role = "system",
                                content = new[] { new { type = "input_text", text = stateText } }
                            }
                        }).ConfigureAwait(false);
                        Log($"📋 State grounding injected: {stateText}");
                    }

                    await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                    break;
                }

            case "book_taxi":
                {
                    ApplyBookingSnapshotFromArgs(args);
                    var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

                    if (action == "request_quote")
                    {
                        if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                        {
                            await SendToolResultAsync(callId, new { success = false, error = "Missing addresses" }).ConfigureAwait(false);
                            return;
                        }

                        try
                        {
                            Log("💰 Starting Lovable AI address extraction...");
                            var aiResult = await FareCalculator.ExtractAddressesWithLovableAiAsync(_booking.Pickup, _booking.Destination, _callerId).ConfigureAwait(false);

                            // --- FEATURE RESTORED: AMBIGUITY CHECK ---
                            if (aiResult?.status == "clarification_needed")
                            {
                                Log("⚠️ Ambiguous addresses detected - stopping quote to ask user.");
                                await SendToolResultAsync(callId, new
                                {
                                    success = false,
                                    needs_clarification = true,
                                    pickup_options = aiResult.pickup?.alternatives ?? Array.Empty<string>(),
                                    destination_options = aiResult.dropoff?.alternatives ?? Array.Empty<string>(),
                                    message = "I found multiple locations. Please ask the caller which city or area they are in."
                                }).ConfigureAwait(false);
                                await QueueResponseCreateAsync(delayMs: 10).ConfigureAwait(false);
                                break; // STOPS here so no wrong price is given
                            }

                            // Use AI resolved addresses
                            string resolvedP = aiResult?.pickup?.address ?? _booking.Pickup;
                            string resolvedD = aiResult?.dropoff?.address ?? _booking.Destination;

                            // v10.4: Use Gemini coordinates directly when available (skip OSM)
                            FareResult fareResult;
                            if (aiResult?.pickup?.lat != null && aiResult?.pickup?.lon != null &&
                                aiResult?.dropoff?.lat != null && aiResult?.dropoff?.lon != null &&
                                aiResult.pickup.lat.Value != 0 && aiResult.dropoff.lat.Value != 0)
                            {
                                Log("✅ Using Gemini coordinates directly (skip OSM)");
                                fareResult = FareCalculator.GetDefaultCalc().CalculateFromCoords(
                                    aiResult.pickup.lat.Value, aiResult.pickup.lon.Value,
                                    aiResult.dropoff.lat.Value, aiResult.dropoff.lon.Value);
                                fareResult.PickupStreet = aiResult.pickup.street_name;
                                fareResult.PickupNumber = aiResult.pickup.street_number;
                                fareResult.PickupPostalCode = aiResult.pickup.postal_code;
                                fareResult.PickupCity = aiResult.pickup.city ?? aiResult.detected_area;
                                fareResult.PickupFormatted = resolvedP;
                                fareResult.DestStreet = aiResult.dropoff.street_name;
                                fareResult.DestNumber = aiResult.dropoff.street_number;
                                fareResult.DestPostalCode = aiResult.dropoff.postal_code;
                                fareResult.DestCity = aiResult.dropoff.city ?? aiResult.detected_area;
                                fareResult.DestFormatted = resolvedD;
                            }
                            else
                            {
                                fareResult = await FareCalculator.CalculateFareWithCoordsAsync(resolvedP, resolvedD, _callerId, skipEdgeExtraction: true).ConfigureAwait(false);
                            }

                            // Map all geocoded data to state
                            _booking.Fare = NormalizeEuroFare(fareResult.Fare);
                            _booking.Eta = fareResult.Eta;
                            _booking.PickupFormatted = fareResult.PickupFormatted;
                            _booking.DestFormatted = fareResult.DestFormatted;
                            _booking.PickupLat = fareResult.PickupLat;
                            _booking.PickupLon = fareResult.PickupLon;
                            _booking.DestLat = fareResult.DestLat;
                            _booking.DestLon = fareResult.DestLon;
                            _booking.PickupStreet ??= fareResult.PickupStreet;
                            _booking.PickupNumber ??= fareResult.PickupNumber ?? FareCalculator.ExtractHouseNumber(_booking.Pickup);
                            _booking.PickupPostalCode ??= fareResult.PickupPostalCode;
                            _booking.PickupCity ??= fareResult.PickupCity;
                            _booking.DestStreet ??= fareResult.DestStreet;
                            _booking.DestNumber ??= fareResult.DestNumber ?? FareCalculator.ExtractHouseNumber(_booking.Destination);
                            _booking.DestPostalCode ??= fareResult.DestPostalCode;
                            _booking.DestCity ??= fareResult.DestCity;

                            _awaitingConfirmation = true;
                            OnBookingUpdated?.Invoke(_booking);

                            var spokenFare = FormatFareForSpeech(_booking.Fare);
                            await SendToolResultAsync(callId, new
                            {
                                success = true,
                                fare = _booking.Fare,
                                fare_spoken = spokenFare,
                                eta = _booking.Eta,
                                message = $"The fare is {spokenFare} and ETA is {_booking.Eta}. Ask to confirm."
                            }).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"⚠️ Quote failed: {ex.Message}");
                            await SendToolResultAsync(callId, new { success = true, fare = "€12.50", eta = "5 mins" }).ConfigureAwait(false);
                        }
                        await QueueResponseCreateAsync(delayMs: 10).ConfigureAwait(false);
                    }
                    else if (action == "confirmed")
                    {
                        _booking.Confirmed = true;
                        _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                        _awaitingConfirmation = false;

                        // v10.3: Ensure geocoding before dispatch — check for missing street OR zero coords
                        bool needsGeocode = string.IsNullOrWhiteSpace(_booking.PickupStreet)
                            || (_booking.PickupLat == 0 && _booking.PickupLon == 0)
                            || (_booking.DestLat == 0 && _booking.DestLon == 0);

                        if (needsGeocode)
                        {
                            Log("🔄 Confirmed path: resolving addresses via Gemini...");
                            var aiResult = await FareCalculator.ExtractAddressesWithLovableAiAsync(_booking.Pickup, _booking.Destination, _callerId).ConfigureAwait(false);
                            
                            // v10.4: Use Gemini coordinates directly when available
                            FareResult fareResult;
                            if (aiResult?.pickup?.lat != null && aiResult?.pickup?.lon != null &&
                                aiResult?.dropoff?.lat != null && aiResult?.dropoff?.lon != null &&
                                aiResult.pickup.lat.Value != 0 && aiResult.dropoff.lat.Value != 0)
                            {
                                Log("✅ Confirmed path: using Gemini coordinates directly");
                                fareResult = FareCalculator.GetDefaultCalc().CalculateFromCoords(
                                    aiResult.pickup.lat.Value, aiResult.pickup.lon.Value,
                                    aiResult.dropoff.lat.Value, aiResult.dropoff.lon.Value);
                                fareResult.PickupStreet = aiResult.pickup.street_name;
                                fareResult.PickupNumber = aiResult.pickup.street_number;
                                fareResult.PickupPostalCode = aiResult.pickup.postal_code;
                                fareResult.PickupCity = aiResult.pickup.city ?? aiResult.detected_area;
                                fareResult.PickupFormatted = aiResult.pickup.address ?? _booking.Pickup;
                                fareResult.DestStreet = aiResult.dropoff.street_name;
                                fareResult.DestNumber = aiResult.dropoff.street_number;
                                fareResult.DestPostalCode = aiResult.dropoff.postal_code;
                                fareResult.DestCity = aiResult.dropoff.city ?? aiResult.detected_area;
                                fareResult.DestFormatted = aiResult.dropoff.address ?? _booking.Destination;
                            }
                            else
                            {
                                fareResult = await FareCalculator.CalculateFareWithCoordsAsync(aiResult?.pickup?.address ?? _booking.Pickup, aiResult?.dropoff?.address ?? _booking.Destination, _callerId, skipEdgeExtraction: true).ConfigureAwait(false);
                            }

                            // Map geocoded data to booking state for BSQD dispatch
                            _booking.PickupLat ??= fareResult.PickupLat;
                            _booking.PickupLon ??= fareResult.PickupLon;
                            _booking.PickupStreet ??= fareResult.PickupStreet;
                            _booking.PickupNumber ??= fareResult.PickupNumber ?? FareCalculator.ExtractHouseNumber(_booking.Pickup);
                            _booking.PickupPostalCode ??= fareResult.PickupPostalCode;
                            _booking.PickupCity ??= fareResult.PickupCity;
                            _booking.PickupFormatted ??= fareResult.PickupFormatted;

                            _booking.DestLat ??= fareResult.DestLat;
                            _booking.DestLon ??= fareResult.DestLon;
                            _booking.DestStreet ??= fareResult.DestStreet;
                            _booking.DestNumber ??= fareResult.DestNumber ?? FareCalculator.ExtractHouseNumber(_booking.Destination);
                            _booking.DestPostalCode ??= fareResult.DestPostalCode;
                            _booking.DestCity ??= fareResult.DestCity;
                            _booking.DestFormatted ??= fareResult.DestFormatted;

                            // Override zero coords if Gemini/OSM returned valid ones
                            if (_booking.PickupLat == 0 && fareResult.PickupLat != 0) _booking.PickupLat = fareResult.PickupLat;
                            if (_booking.PickupLon == 0 && fareResult.PickupLon != 0) _booking.PickupLon = fareResult.PickupLon;
                            if (_booking.DestLat == 0 && fareResult.DestLat != 0) _booking.DestLat = fareResult.DestLat;
                            if (_booking.DestLon == 0 && fareResult.DestLon != 0) _booking.DestLon = fareResult.DestLon;
                        }

                        OnBookingUpdated?.Invoke(_booking);
                        Log($"✅ Booked: {_booking.BookingRef}");

                        // Fire off BSQD Dispatch
                        _ = Task.Run(async () => {
                            for (int i = 0; i < 50 && Volatile.Read(ref _responseActive) == 1; i++) await Task.Delay(100);
                            BsqdDispatcher.Dispatch(_booking, _callerId);
                        });

                        await SendToolResultAsync(callId, new
                        {
                            success = true,
                            booking_ref = _booking.BookingRef,
                            message = $"Booking confirmed. Reference: {_booking.BookingRef}. Now tell the caller their reference number and ask 'Is there anything else I can help with?'. When the user says no or declines, you MUST say EXACTLY: 'Thank you for using the TaxiBot demo. You will shortly receive your booking confirmation via WhatsApp. Goodbye.' — then call end_call. Do NOT call end_call until AFTER you have spoken the goodbye message."
                        }).ConfigureAwait(false);
                        await QueueResponseCreateAsync(delayMs: 10).ConfigureAwait(false);

                        // Safety net: if AI hasn't ended the call within 15s, force the goodbye
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(15_000).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) == 0 && IsConnected)
                            {
                                Log("⏰ Forced goodbye injection (15s timeout)");
                                await SendJsonAsync(new
                                {
                                    type = "conversation.item.create",
                                    item = new
                                    {
                                        type = "message",
                                        role = "system",
                                        content = new[] { new { type = "input_text", text = "[BOOKING COMPLETE] The booking is done. Say EXACTLY: 'Thank you for using the TaxiBot demo. You will shortly receive your booking confirmation via WhatsApp. Goodbye.' Then IMMEDIATELY call end_call." } }
                                    }
                                }).ConfigureAwait(false);
                                await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                            }
                        });
                    }
                    break;
                }

            case "find_local_events":
                {
                    var category = args.TryGetValue("category", out var cat) ? cat?.ToString() ?? "all" : "all";
                    var near = args.TryGetValue("near", out var loc) ? loc?.ToString() ?? "nearby" : "nearby";
                    var date = args.TryGetValue("date", out var dt) ? dt?.ToString() ?? "tonight" : "tonight";
                    Log($"🎭 find_local_events: category={category}, near={near}, date={date}");

                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        events = new[]
                        {
                            new { name = "Live Jazz Night", venue = $"The Blue Note, {near}", time = "8:00 PM", description = $"A {category} evening of smooth jazz" },
                            new { name = "Comedy Open Mic", venue = $"Laugh Factory, {near}", time = "9:00 PM", description = "Stand-up comedy showcase" }
                        },
                        message = $"I found some events {near} for {date}. Would you like a taxi to any of these?"
                    }).ConfigureAwait(false);
                    await QueueResponseCreateAsync(delayMs: 10).ConfigureAwait(false);
                    break;
                }

            case "end_call":
                {
                    Log("📞 End call requested");
                    Interlocked.Exchange(ref _ignoreUserAudio, 1);

                    // v10.4: Check if mandatory closing script was already spoken
                    const string CLOSING_SCRIPT = "Thank you for using the TaxiBot demo. You will shortly receive your booking confirmation via WhatsApp. Goodbye.";
                    bool alreadySpoken = !string.IsNullOrEmpty(_lastAdaTranscript) &&
                        (_lastAdaTranscript.Contains("Thank you for using the TaxiBot", StringComparison.OrdinalIgnoreCase) ||
                         _lastAdaTranscript.Contains("booking confirmation via WhatsApp", StringComparison.OrdinalIgnoreCase));

                    if (alreadySpoken)
                    {
                        Log("✅ Closing script already spoken — ending call");
                        await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
                        SignalCallEnded("end_call");
                    }
                    else
                    {
                        Log("⚠️ Closing script NOT spoken — injecting before hangup");
                        await SendToolResultAsync(callId, new
                        {
                            success = true,
                            instruction = $"You MUST say EXACTLY: '{CLOSING_SCRIPT}' NOW. Do not skip it."
                        }).ConfigureAwait(false);
                        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);

                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(12_000).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) == 0)
                            {
                                Log("📴 Delayed hangup after closing script");
                                SignalCallEnded("end_call_delayed");
                            }
                        });
                    }
                    break;
                }

            default:
                Log($"⚠️ Unknown tool: {name}");
                await SendToolResultAsync(callId, new { error = $"Unknown tool: {name}" }).ConfigureAwait(false);
                await QueueResponseCreateAsync(delayMs: 10).ConfigureAwait(false);
                break;
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
    // SESSION SETUP
    // =========================
    private async Task ConfigureSessionAsync()
    {
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetSystemPrompt(),
                voice = _voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new
                {
                    model = "whisper-1",
                    language = _detectedLanguage  // v2.4: Add language hint for better transcription accuracy
                },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.4,
                    prefix_padding_ms = 450,      // v2.1: Slightly longer for natural pace
                    silence_duration_ms = 1200     // v2.1: Calmer, less racy (was 700)
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.8
            }
        };

        await SendJsonAsync(config).ConfigureAwait(false);
        Log($"🎧 Session configured (lang={_detectedLanguage})");
    }

    private async Task SendGreetingAsync()
    {
        // Short delay to let session stabilize
        await Task.Delay(100).ConfigureAwait(false);

        var greeting = GetLocalizedGreeting(_detectedLanguage);

        // IMPORTANT: Do NOT manually reset _responseActive/_responseQueued here.
        // Let OpenAI lifecycle events manage response state to avoid clipped/crispy audio.
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Greet the caller warmly in {GetLanguageName(_detectedLanguage)}. Say: \"{greeting}\""
            }
        }).ConfigureAwait(false);

        Log("📢 Greeting sent → Ada should speak now");
    }

    // =========================
    // UTILITIES (THREAD-SAFE WS SEND)
    // =========================
    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonSerializer.Serialize(obj);
            await SendTextAsync(json).ConfigureAwait(false);
        }
        catch { }
    }
    private void EnsureBsqdLogger()
    {
        if (Interlocked.CompareExchange(ref _bsqdLoggerAttached, 1, 0) == 0)
        {
            BsqdDispatcher.OnLog += msg => Log(msg);
        }
    }
    private async Task SendTextAsync(string json)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? default)
                     .ConfigureAwait(false);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 1)
            return;

        Log($"📴 Call ended: {reason}");
        OnCallEnded?.Invoke();
    }

    private async Task SendWhatsAppNotificationAsync(string? phoneNumber)
    {
        Log($"📲 WhatsApp notification starting... (phone={phoneNumber ?? "null"})");

        if (string.IsNullOrWhiteSpace(phoneNumber) || phoneNumber == "unknown")
        {
            Log("⚠️ WhatsApp notification skipped - no valid phone number");
            return;
        }

        try
        {
            var formatted = FormatPhoneForWhatsApp(phoneNumber);

            if (string.IsNullOrWhiteSpace(formatted) || formatted.Length < 8)
            {
                Log($"⚠️ WhatsApp notification skipped - formatted number too short: {formatted}");
                return;
            }

            Log($"📲 Sending WhatsApp webhook: original={phoneNumber} → formatted={formatted}");
            var (success, msg) = await WhatsAppNotifier.SendAsync(formatted).ConfigureAwait(false);
            Log($"📱 WhatsApp webhook result: {(success ? "✅ OK" : "❌ FAIL")} {msg}");
        }
        catch (Exception ex)
        {
            Log($"❌ WhatsApp notification error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ===========================================
    // LAZY-INITIALIZED DICTIONARIES
    // ===========================================
    private static Dictionary<string, string>? _countryCodeMap;
    private static Dictionary<string, string> CountryCodeToLanguage
    {
        get
        {
            _countryCodeMap ??= new Dictionary<string, string>
            {
                { "31", "nl" }, // Netherlands
                { "32", "nl" }, // Belgium (Dutch)
                { "33", "fr" }, // France
                { "41", "de" }, // Switzerland
                { "43", "de" }, // Austria
                { "44", "en" }, // UK
                { "49", "de" }, // Germany
            };
            return _countryCodeMap;
        }
    }

    private static Dictionary<string, string>? _greetingsMap;
    private static Dictionary<string, string> LocalizedGreetings
    {
        get
        {
            _greetingsMap ??= new Dictionary<string, string>
            {
                { "en", "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. What's your name?" },
                { "nl", "Hallo, en welkom bij de Taxibot demo. Ik ben Ada, uw taxi boekingsassistent. Wat is uw naam?" },
                { "fr", "Bonjour et bienvenue à la démo Taxibot. Je suis Ada. Quel est votre prénom?" },
                { "de", "Hallo und willkommen zur Taxibot-Demo. Ich bin Ada. Wie ist Ihr Name?" },
            };
            return _greetingsMap;
        }
    }

    private static Dictionary<string, string>? _sttCorrectionsMap;
    private static Dictionary<string, string> SttCorrections
    {
        get
        {
            _sttCorrectionsMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Name corrections (case-insensitive, so only one entry needed)
                { "Aren't out", "Bernard" },
                // Address corrections (exact matches)
                { "52 I ain't dead bro", "52A David Road" },
                { "52 I ain't David", "52A David Road" },
                { "52 ain't David", "52A David Road" },
                { "52 a David", "52A David Road" },
                // Time corrections
                { "for now", "now" },
                { "right now", "now" },
                { "as soon as possible", "now" },
                { "ASAP", "now" },
                // Confirmation corrections
                { "yeah please", "yes please" },
                { "yep", "yes" },
                { "yup", "yes" },
                { "yeah", "yes" },
                { "that's right", "yes" },
                { "correct", "yes" },
                { "go ahead", "yes" },
                { "book it", "yes" },
            };
            return _sttCorrectionsMap;
        }
    }

    // Partial/substring corrections for common mishearings (backup if audio quality degrades)
    private static readonly (string Bad, string Good)[] PartialSttCorrections = new[]
    {
        // Street name mishearings - only add patterns that persist after SpeexDSP fix
        ("Waters Street", "Russell Street"),
        ("Water Street", "Russell Street"),
        ("Walters Street", "Russell Street"),

        // Observed mishearings in recent logs (keep patterns specific to avoid over-correcting)
        ("Russell Sweeney", "Russell Street"),
        ("Servant, Russell", "7 Russell"),
        ("Servant Russell", "7 Russell"),
        ("Dave Redroth", "David Road"),
        
        // v2.4: New corrections from logs - David Road mishearings
        ("I hate baby girls", "52A David Road"),
        ("I hate David girls", "52A David Road"),
        ("I hate Davey Girls", "52A David Road"),
        ("eight David girls", "52A David Road"),
        ("baby girl", "David Road"),  // v3.3: partial match
        
        // v2.4: Name mishearings
        ("It's ours", "It's Max"),
        ("It's ours.", "It's Max"),
        
        // v2.4: Passenger count mishearings  
        ("See you passengers", "Three passengers"),
        ("see you passengers", "three passengers"),
        
        // v3.3: City mishearings
        ("Coltree", "Coventry"),
        ("Coltre", "Coventry"),
        ("Coltry", "Coventry"),
        ("Coal Tree", "Coventry"),
        ("in Country", "in Coventry"),
    };

    // ===========================================
    // UTILITIES
    // ===========================================
    private void ResetCallState(string? caller)
    {
        ClearPendingFrames();

        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);
        _booking.Reset();
        _awaitingConfirmation = false;

        // IMPORTANT: Do NOT reset _disposed here — it's object lifetime, not call lifetime
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);  // v2.5

        _activeResponseId = null;
        _lastCompletedResponseId = null;

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
        Volatile.Write(ref _speechStoppedAt, 0);  // v2.5
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Increment(ref _noReplyWatchdogId); // Cancel any stale watchdogs
    }

    private string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
        {
            Log($"🌍 Language: en (no phone)");
            return "en";
        }

        // Normalize: remove spaces, dashes, parentheses
        var norm = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
        Log($"🌍 Phone: {phone} → norm: {norm}");

        // Check Dutch local format BEFORE stripping international prefix
        if (norm.StartsWith("06") && norm.Length == 10 && norm.All(char.IsDigit))
        {
            Log($"🌍 Language: nl (Dutch mobile 06)");
            return "nl";
        }
        if (norm.StartsWith("0") && !norm.StartsWith("00") && norm.Length == 10)
        {
            Log($"🌍 Language: nl (Dutch landline)");
            return "nl";
        }

        // Strip international prefix
        if (norm.StartsWith("+"))
            norm = norm.Substring(1);
        else if (norm.StartsWith("00") && norm.Length > 4)
            norm = norm.Substring(2);

        // Check first 2 digits for country code
        if (norm.Length >= 2)
        {
            var code = norm.Substring(0, 2);
            if (CountryCodeToLanguage.TryGetValue(code, out var lang))
            {
                Log($"🌍 Language: {lang} (country code {code})");
                return lang;
            }
            Log($"🌍 Language: en (default, no match for code {code})");
        }
        else
        {
            Log($"🌍 Language: en (default, number too short)");
        }

        return "en";
    }

    private static string ApplySttCorrections(string text)
    {
        var t = (text ?? "").Trim();

        // Normalize common telephony transcript artifacts so exact-match corrections work
        // even when Whisper adds punctuation.
        t = t.TrimEnd('.', '!', '?', ',', ';', ':');
        while (t.Contains("  ", StringComparison.Ordinal))
            t = t.Replace("  ", " ", StringComparison.Ordinal);

        // First try exact match
        if (SttCorrections.TryGetValue(t, out var corrected))
            return corrected;

        // Contextual fixes (avoid global replacements that could damage arbitrary addresses)
        // Example from logs: "52A Little Road" should be "52A David Road".
        if (t.Contains("Little Road", StringComparison.OrdinalIgnoreCase) &&
            (t.Contains("52A", StringComparison.OrdinalIgnoreCase) || t.Contains("52 A", StringComparison.OrdinalIgnoreCase)))
        {
            var before = t;
            t = t.Replace("Little Road", "David Road", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"🔧 STT contextual fix: \"{before}\" → \"{t}\"");
        }

        // Then apply partial/substring corrections
        foreach (var (bad, good) in PartialSttCorrections)
        {
            if (t.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                var result = t.Replace(bad, good, StringComparison.OrdinalIgnoreCase);
                // Log when correction is applied (visible in console)
                Console.WriteLine($"🔧 STT partial fix: \"{t}\" → \"{result}\"");
                t = result;
            }
        }

        return t;
    }

    private string GetSystemPrompt() => $@"You are Ada, a taxi booking assistant for Voice Taxibot. Version 3.7.

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

Start in English based on the caller’s phone number.
CONTINUOUSLY MONITOR the caller’s spoken language.
If they speak another language or ask to switch, IMMEDIATELY switch for all responses.

Supported languages:
English, Dutch, French, German, Spanish, Italian, Polish, Portuguese.

Default to English if uncertain.

==============================
BOOKING FLOW (STRICT)
==============================

Follow this order exactly:

Greet  
→ NAME (call sync_booking_data IMMEDIATELY with caller_name after user provides their name)  
→ PICKUP  
→ DESTINATION  
→ PASSENGERS  
→ TIME  

After EACH step, call sync_booking_data with ALL known fields.
CRITICAL: This includes NAME. When the user says their name, call sync_booking_data with caller_name set.
The system will AUTO-CALCULATE the fare once pickup, destination, passengers and time are filled.

→ After TIME: call sync_booking_data (the fare is returned instantly in the tool result)  
→ Announce the fare using the fare_spoken field from the tool result  
→ Ask ""Would you like to confirm this booking?""  
→ Call book_taxi(action=""confirmed"")  
→ Give reference ID ONLY  
→ Ask ""Anything else?""

IMPORTANT: Do NOT say ""let me get the price"" or ""just a moment"".
The fare is returned INSTANTLY by sync_booking_data. Announce it immediately.

If the user says NO to ""anything else"":
You MUST perform the FINAL CLOSING and then call end_call.

==============================
FINAL CLOSING (MANDATORY – EXACT WORDING)
==============================

When the conversation is complete, say EXACTLY this sentence and nothing else:

“Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye.”

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
- The user’s MOST RECENT wording is always the source of truth

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

Users often correct information without saying “no” or “wrong”.

Examples:
Stored: “Russell Street, Coltree”  
User: “Russell Street in Coventry”  
→ UPDATE to “Russell Street, Coventry”

Stored: “David Road”  
User: “52A David Road”  
→ UPDATE to “52A David Road”

ALWAYS trust the user’s latest wording.

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
- NEVER “double check” unless explicitly requested

If the user repeats or insists on an address:
THAT ADDRESS IS FINAL.

==============================
REPETITION RULE (VERY IMPORTANT)
==============================

If the user repeats the same address again, especially with emphasis
(e.g. “no”, “no no”, “I said”, “my destination is”):

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
- NEVER “improve”, “normalize”, or “correct” addresses
- Read back EXACTLY what was stored

If unsure, ASK the user.

IMPORTANT:
You are NOT allowed to “correct” addresses.
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
1214A → spoken “twelve fourteen A”
12-14 → spoken “twelve to fourteen” (ONLY if user said dash/to)

==============================
PICKUP TIME HANDLING
==============================

- “now”, “right now”, “ASAP” → store exactly as “now”
- NEVER convert “now” into a clock time
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

ALL prices are in EUROS (€).
Use the fare_spoken field for speech.

==============================
ABSOLUTE RULES – VIOLATION = SYSTEM FAILURE
==============================

1. You MUST call sync_booking_data after EVERY user turn that provides or changes ANY booking detail (name, pickup, destination, passengers, time). NO EXCEPTIONS.
2. You MUST NOT quote any fare or ETA unless it was returned by the sync_booking_data tool result. If sync_booking_data does not return a fare, you MUST NOT invent one.
3. You MUST call book_taxi(action=""confirmed"") BEFORE confirming a booking. The tool returns the booking reference — you MUST NOT invent a reference number.
4. NEVER announce booking success before the book_taxi tool succeeds.
5. If a tool call fails, explain clearly and ask to retry.
6. If you skip calling sync_booking_data, the booking state will be WRONG and the dispatch will FAIL. Always call it.

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
yes, yeah, yep, sure, ok, okay, correct, that’s right, go ahead, book it, confirm, that’s fine

==============================
RESPONSE STYLE
==============================

- One question at a time
- Under 20 words per response
- Calm, professional, human
- Acknowledge corrections briefly, then move on
- NEVER call end_call except after the FINAL CLOSING
";


    private static string GetLanguageName(string c) => c switch { "nl" => "Dutch", "fr" => "French", "de" => "German", "es" => "Spanish", "it" => "Italian", "pl" => "Polish", "pt" => "Portuguese", _ => "English" };

    private static string GetLocalizedGreeting(string lang) =>
        LocalizedGreetings.TryGetValue(lang, out var greeting) ? greeting : LocalizedGreetings["en"];

    private static string FormatPhoneForWhatsApp(string phone) => WhatsAppNotifier.FormatPhone(phone);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Dictionary<string, object> ParseArgs(JsonElement root)
    {
        var args = new Dictionary<string, object>();

        if (!root.TryGetProperty("arguments", out var a) || string.IsNullOrEmpty(a.GetString()))
            return args;

        try
        {
            using var doc = JsonDocument.Parse(a.GetString()!);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                args[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Number => p.Value.TryGetInt32(out var i) ? i : p.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    _ => p.Value.ToString() ?? ""
                };
            }
        }
        catch { }

        return args;
    }

    private void ApplyBookingSnapshotFromArgs(Dictionary<string, object> args)
    {
        if (args.TryGetValue("caller_name", out var nm)) _booking.Name = nm?.ToString();
        if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p?.ToString();
        if (args.TryGetValue("destination", out var d)) _booking.Destination = d?.ToString();
        if (args.TryGetValue("passengers", out var pax))
        {
            if (pax is int i) _booking.Passengers = i;
            else if (int.TryParse(pax?.ToString(), out var pn)) _booking.Passengers = pn;
        }
        if (args.TryGetValue("pickup_time", out var pt)) _booking.PickupTime = pt?.ToString();
    }

    /// <summary>
    /// Apply booking snapshot and return which address fields were newly set.
    /// </summary>
    private (string? newPickup, string? newDest) ApplyBookingSnapshotFromArgsWithTracking(Dictionary<string, object> args)
    {
        string? newPickup = null, newDest = null;

        if (args.TryGetValue("caller_name", out var nm)) _booking.Name = nm?.ToString();
        if (args.TryGetValue("pickup", out var p))
        {
            var incoming = p?.ToString();

            if (StreetNameChanged(_booking.Pickup, incoming))
            {
                // HARD RESET — user corrected the address
                _booking.Pickup = incoming;
                _booking.PickupLat = null;
                _booking.PickupLon = null;
                _booking.PickupStreet = null;
                _booking.PickupNumber = null;
                _booking.PickupPostalCode = null;
                _booking.PickupCity = null;
                _booking.PickupFormatted = null;

                Log("🧹 Pickup street changed — cleared geocoded data");
            }
            else
            {
                _booking.Pickup = incoming;
            }

            newPickup = incoming;
        }
        if (args.TryGetValue("destination", out var d))
        {
            var incoming = d?.ToString();

            if (StreetNameChanged(_booking.Destination, incoming))
            {
                // HARD RESET — user corrected destination address
                _booking.Destination = incoming;
                _booking.DestLat = null;
                _booking.DestLon = null;
                _booking.DestStreet = null;
                _booking.DestNumber = null;
                _booking.DestPostalCode = null;
                _booking.DestCity = null;
                _booking.DestFormatted = null;

                Log("🧹 Destination street changed — cleared geocoded data");
            }
            else
            {
                _booking.Destination = incoming;
            }

            newDest = incoming;
        }
        if (args.TryGetValue("passengers", out var pax))
        {
            if (pax is int i) _booking.Passengers = i;
            else if (int.TryParse(pax?.ToString(), out var pn)) _booking.Passengers = pn;
        }
        if (args.TryGetValue("pickup_time", out var pt)) _booking.PickupTime = pt?.ToString();

        return (newPickup, newDest);
    }

    /// <summary>
    /// Verify addresses with Google Maps and enrich BookingState with geocoded details.
    /// </summary>
    private async Task VerifyAndEnrichAddressesAsync(string? newPickup, string? newDest)
    {
        var tasks = new List<Task<(string type, AddressVerifyResult result)>>();
        if (!string.IsNullOrWhiteSpace(newPickup))
        {
            // DO NOT verify if user just corrected the street name
            if (!StreetNameChanged(_booking.PickupFormatted ?? _booking.Pickup, newPickup))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var result = await FareCalculator.VerifyAddressAsync(newPickup, _callerId);
                    return ("pickup", result);
                }));
            }
            else
            {
                Log("🚫 Skipping pickup verification — user correction detected");
            }
        }

        if (!string.IsNullOrWhiteSpace(newDest))
        {
            // DO NOT verify if user just corrected the street name
            if (!StreetNameChanged(_booking.DestFormatted ?? _booking.Destination, newDest))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var result = await FareCalculator.VerifyAddressAsync(newDest, _callerId);
                    return ("destination", result);
                }));
            }
            else
            {
                Log("🚫 Skipping destination verification — user correction detected");
            }
        }

        if (tasks.Count == 0) return;

        var verifications = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (type, vResult) in verifications)
        {
            if (!vResult.Success) continue;

            if (type == "pickup")
            {
                _booking.PickupLat = vResult.Lat;
                _booking.PickupLon = vResult.Lon;
                _booking.PickupStreet = vResult.Street;
                _booking.PickupNumber = vResult.Number;
                _booking.PickupPostalCode = vResult.PostalCode;
                _booking.PickupCity = vResult.City;
                _booking.PickupFormatted = vResult.VerifiedAddress;
                Log($"📍 Pickup verified: {vResult.Number} {vResult.Street}, {vResult.City} ({vResult.PostalCode})");
            }
            else if (type == "destination")
            {
                _booking.DestLat = vResult.Lat;
                _booking.DestLon = vResult.Lon;
                _booking.DestStreet = vResult.Street;
                _booking.DestNumber = vResult.Number;
                _booking.DestPostalCode = vResult.PostalCode;
                _booking.DestCity = vResult.City;
                _booking.DestFormatted = vResult.VerifiedAddress;
                Log($"📍 Dest verified: {vResult.Number} {vResult.Street}, {vResult.City} ({vResult.PostalCode})");
            }
        }
    }

    private static string NormalizeEuroFare(string? fare)
    {
        var f = (fare ?? "").Trim();
        if (string.IsNullOrEmpty(f)) return f;
        if (f.StartsWith("£")) return "€" + f.Substring(1);
        if (f.StartsWith("$")) return "€" + f.Substring(1);
        return f;
    }

    /// <summary>
    /// Convert "€12.50" to "12 euros 50" for TTS pronunciation.
    /// </summary>
    private static string FormatFareForSpeech(string fare)
    {
        var clean = fare.Replace("€", "").Replace("£", "").Replace("$", "").Trim();
        if (decimal.TryParse(clean, out var amount))
        {
            var euros = (int)amount;
            var cents = (int)((amount - euros) * 100);
            if (cents > 0)
                return $"{euros} euros {cents}";
            return $"{euros} euros";
        }
        return fare; // Fallback to original
    }


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
            description = "Request quote or confirm booking",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
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

    private void Log(string msg)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine($"[OpenAI] {line}");
        OnLog?.Invoke(line);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));
    }

    // =========================
    // DISPOSAL
    // =========================

    private static bool StreetNameChanged(string? oldAddress, string? newAddress)
    {
        if (string.IsNullOrWhiteSpace(oldAddress) || string.IsNullOrWhiteSpace(newAddress))
            return false;

        string Normalize(string s) =>
            Regex.Replace(s.ToLowerInvariant(), @"\d|[^a-z ]", "").Trim();

        return Normalize(oldAddress) != Normalize(newAddress);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        SignalCallEnded("disposed");

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _sendMutex.Dispose(); } catch { }

        GC.SuppressFinalize(this);
    }
}

// BookingState moved to BookingState.cs for centralized access

