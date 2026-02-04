using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API client with A-law output passthrough.
/// 
/// v4.2: Hybrid mode - PCM input (works with existing ingress), A-law output (direct SIP passthrough).
/// Audio path:
///   INPUT:  SIP (PCMA 8kHz) → decode → resample 24kHz → OpenAI (pcm16)
///   OUTPUT: OpenAI (g711_alaw) → raw A-law bytes → SIPSorcery SendRtpRaw → SIP
/// 
/// This eliminates resampling/encoding on the output path for lower latency.
/// 
/// v3.6 features retained:
/// - Late confirmation detection for booking flow
/// - Transcript guard timing
/// - No-reply watchdog
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    public const string VERSION = "4.2";

    // =========================
    // PERF: Cached JSON serializer options
    // =========================
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _dispatchWebhookUrl;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);
    private readonly bool _useALawOutput;  // v4.2: When true, request g711_alaw output from OpenAI

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
    public event Action<byte[]>? OnPcm24Audio;  // Legacy - not used in v4.2 A-law mode
    public event Action<byte[]>? OnALawAudio;   // v4.2: Raw A-law audio for direct RTP passthrough
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
        bool useALawOutput = true,  // v4.2: Default to A-law output passthrough
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _useALawOutput = useALawOutput;
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
    // AUDIO INPUT (A-LAW PASSTHROUGH)
    // =========================
    
    /// <summary>
    /// Send A-law audio directly to OpenAI (no decoding, no resampling).
    /// v4.0: Direct passthrough - SIP PCMA → OpenAI g711_alaw
    /// </summary>
    public async Task SendALawAsync(byte[] alawData)
    {
        if (!IsConnected || alawData == null || alawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // Echo guard: skip audio right after AI speaks (but bypass if awaiting confirmation)
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 300)
            return;

        await SendJsonAsync(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(alawData)
        }).ConfigureAwait(false);

        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    /// <summary>
    /// Legacy μ-law support - converts to A-law before sending.
    /// Use SendALawAsync directly if your SIP uses PCMA (payload type 8).
    /// </summary>
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData == null || ulawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // Convert μ-law to A-law (both are 8kHz, 8-bit)
        var alawData = AudioCodecs.MuLawToALaw(ulawData);
        await SendALawAsync(alawData).ConfigureAwait(false);
    }

    /// <summary>
    /// v4.1: Send 24kHz PCM audio directly to OpenAI.
    /// Resamples from other rates to 24kHz as needed.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        // Echo guard: skip audio right after AI speaks (but bypass if awaiting confirmation)
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 300)
            return;

        // Resample to 24kHz if needed (OpenAI expects 24kHz PCM16)
        byte[] pcm24k = pcmData;
        if (sampleRate != 24000)
        {
            var samples = AudioCodecs.BytesToShorts(pcmData);
            var resampled = AudioCodecs.Resample(samples, sampleRate, 24000);
            pcm24k = AudioCodecs.ShortsToBytes(resampled);
        }

        await SendJsonAsync(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm24k)
        }).ConfigureAwait(false);

        Volatile.Write(ref _lastUserSpeechAt, NowMs());
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

    // =========================
    // AUDIO OUTPUT
    // =========================
    
    /// <summary>
    /// v4.2: Process audio from OpenAI.
    /// When _useALawOutput=true: receives raw A-law bytes, emits via OnALawAudio.
    /// When _useALawOutput=false: receives 24kHz PCM, emits via OnPcm24Audio.
    /// </summary>
    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var audioBytes = Convert.FromBase64String(base64);
            
            if (_useALawOutput)
            {
                // v4.2 A-law mode: emit raw A-law directly - caller handles RTP framing
                OnALawAudio?.Invoke(audioBytes);
            }
            else
            {
                // PCM mode: emit 24kHz PCM for legacy playout engine
                OnPcm24Audio?.Invoke(audioBytes);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio error: {ex.Message}");
        }
    }

    // =========================
    // CONNECT / DISCONNECT
    // =========================
    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ResetCallState(caller);

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
                    // v2.5: Transcript arrived - clear the pending flag
                    Interlocked.Exchange(ref _transcriptPending, 0);
                    
                    if (doc.RootElement.TryGetProperty("transcript", out var u) &&
                        !string.IsNullOrWhiteSpace(u.GetString()))
                    {
                        var text = ApplySttCorrections(u.GetString()!);
                        
                        // Calculate timing for logging
                        var msSinceSpeechStopped = NowMs() - Volatile.Read(ref _speechStoppedAt);
                        var msSinceResponseCreated = NowMs() - Volatile.Read(ref _responseCreatedAt);
                        
                        // Log transcript arrival with timing context
                        if (Volatile.Read(ref _responseActive) == 1)
                        {
                            Log($"📝 Transcript arrived {msSinceSpeechStopped}ms after speech stopped, {msSinceResponseCreated}ms after response.created: {text}");
                        }
                        
                        Log($"👤 User: {text}");
                        OnTranscript?.Invoke($"You: {text}");
                        
                        // v3.6: LATE CONFIRMATION DETECTION
                        // If Ada already finished responding but the user said "yes/correct/confirm",
                        // we need to inject a system message and trigger a new response immediately.
                        // This handles the case where Ada says "Just a moment..." but never calls book_taxi.
                        if (Volatile.Read(ref _responseActive) == 0 && _awaitingConfirmation)
                        {
                            var lowerText = text.ToLowerInvariant();
                            bool isConfirmation = lowerText.Contains("yes") || 
                                                  lowerText.Contains("yeah") ||
                                                  lowerText.Contains("yep") ||
                                                  lowerText.Contains("correct") ||
                                                  lowerText.Contains("that's right") ||
                                                  lowerText.Contains("go ahead") ||
                                                  lowerText.Contains("confirm") ||
                                                  lowerText.Contains("book it") ||
                                                  lowerText.Contains("sure") ||
                                                  lowerText.Contains("please");
                            
                            if (isConfirmation)
                            {
                                Log("✅ Late confirmation detected - injecting system prompt and triggering response");
                                _ = HandleLateConfirmationAsync(text);
                            }
                        }
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

                        // Abort if cancelled, call ended, user spoke, or transcript pending
                        if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
                        if (Volatile.Read(ref _responseActive) == 1) return;
                        if (Volatile.Read(ref _transcriptPending) == 1)
                        {
                            Log("⏰ Watchdog: transcript pending - aborting");
                            return;
                        }
                        
                        // v4.2: Check if there was recent local speech (within 3s) to avoid discarding user audio
                        var msSinceLastSpeech = NowMs() - Volatile.Read(ref _lastUserSpeechAt);
                        if (msSinceLastSpeech < 3000)
                        {
                            Log($"⏰ Watchdog: recent speech {msSinceLastSpeech}ms ago - aborting");
                            return;
                        }

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
                
                // Verify addresses with Google Maps (region-biased) to enrich with geocoded details
                await VerifyAndEnrichAddressesAsync(newPickup, newDest).ConfigureAwait(false);

                OnBookingUpdated?.Invoke(_booking);
                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
                await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                break;
            }

            case "book_taxi":
            {
                // Defensive: make sure booking state is populated even if the model skipped sync_booking_data.
                ApplyBookingSnapshotFromArgs(args);

                var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

                if (action == "request_quote")
                {
                    if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                    {
                        Log($"⚠️ Quote requested but pickup/destination missing (pickup='{_booking.Pickup}', dest='{_booking.Destination}')");
                        await SendToolResultAsync(callId, new
                        {
                            success = false,
                            error = "Missing pickup or destination",
                            message = "Missing pickup or destination. Ask the caller to repeat it."
                        }).ConfigureAwait(false);
                        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                        break;
                    }

                    // Quote timeout guard: never stall the call if external services are slow/unavailable.
                    FareResult fareResult;
                    try
                    {
                        Log("💰 Starting Lovable AI address extraction...");
                        
                        // Step 1: Try Lovable AI extraction for intelligent address resolution
                        string resolvedPickup = _booking.Pickup!;
                        string resolvedDest = _booking.Destination!;
                        bool needsClarification = false;
                        string[]? pickupAlternatives = null;
                        string[]? destAlternatives = null;
                        
                        var aiTask = FareCalculator.ExtractAddressesWithLovableAiAsync(
                            _booking.Pickup,
                            _booking.Destination,
                            _callerId);
                        
                        var aiCompleted = await Task.WhenAny(aiTask, Task.Delay(3000)).ConfigureAwait(false);
                        
                        if (aiCompleted == aiTask)
                        {
                            var aiResult = await aiTask.ConfigureAwait(false);
                            if (aiResult != null)
                            {
                                // Use AI-resolved addresses if available
                                if (!string.IsNullOrEmpty(aiResult.pickup?.address))
                                    resolvedPickup = aiResult.pickup.address;
                                if (!string.IsNullOrEmpty(aiResult.dropoff?.address))
                                    resolvedDest = aiResult.dropoff.address;
                                
                                // Check for ambiguity
                                if (aiResult.status == "clarification_needed")
                                {
                                    needsClarification = true;
                                    pickupAlternatives = aiResult.pickup?.alternatives;
                                    destAlternatives = aiResult.dropoff?.alternatives;
                                }
                                
                                Log($"🤖 AI resolved: '{_booking.Pickup}' → '{resolvedPickup}'");
                                Log($"🤖 AI resolved: '{_booking.Destination}' → '{resolvedDest}'");
                            }
                        }
                        else
                        {
                            Log("⏱️ Lovable AI extraction timed out (3s) — using raw addresses");
                        }
                        
                        // If clarification needed, return that instead of a quote
                        if (needsClarification)
                        {
                            Log("⚠️ Ambiguous addresses detected - requesting clarification");
                            await SendToolResultAsync(callId, new
                            {
                                success = false,
                                needs_clarification = true,
                                pickup_options = pickupAlternatives ?? Array.Empty<string>(),
                                destination_options = destAlternatives ?? Array.Empty<string>(),
                                message = "I found multiple locations with that name. Ask which one they meant."
                            }).ConfigureAwait(false);
                            await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                            break;
                        }
                        
                        // Step 2: Geocode the resolved addresses and calculate fare
                        var fareTask = FareCalculator.CalculateFareWithCoordsAsync(
                            resolvedPickup,
                            resolvedDest,
                            _callerId);

                        var fareCompleted = await Task.WhenAny(fareTask, Task.Delay(3000)).ConfigureAwait(false);
                        if (fareCompleted != fareTask)
                        {
                            Log("⏱️ Geocoding timed out (3s) — using fallback quote");
                            fareResult = new FareResult { Fare = "€12.50", Eta = "6 minutes" };
                        }
                        else
                        {
                            fareResult = await fareTask.ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ Fare calculation failed: {ex.Message} — using fallback quote");
                        fareResult = new FareResult { Fare = "€12.50", Eta = "6 minutes" };
                    }

                    var normalizedFare = NormalizeEuroFare(fareResult.Fare);
                    _booking.Fare = normalizedFare;
                    _booking.Eta = fareResult.Eta;
                    
                    // Populate geocoded pickup details
                    _booking.PickupLat = fareResult.PickupLat;
                    _booking.PickupLon = fareResult.PickupLon;
                    _booking.PickupStreet = fareResult.PickupStreet;
                    _booking.PickupNumber = fareResult.PickupNumber;
                    _booking.PickupPostalCode = fareResult.PickupPostalCode;
                    _booking.PickupCity = fareResult.PickupCity;
                    _booking.PickupFormatted = fareResult.PickupFormatted;
                    
                    // Populate geocoded destination details
                    _booking.DestLat = fareResult.DestLat;
                    _booking.DestLon = fareResult.DestLon;
                    _booking.DestStreet = fareResult.DestStreet;
                    _booking.DestNumber = fareResult.DestNumber;
                    _booking.DestPostalCode = fareResult.DestPostalCode;
                    _booking.DestCity = fareResult.DestCity;
                    _booking.DestFormatted = fareResult.DestFormatted;
                    
                    _awaitingConfirmation = true;

                    OnBookingUpdated?.Invoke(_booking);
                    Log($"💰 Quote: {normalizedFare} (pickup: {fareResult.PickupCity}, dest: {fareResult.DestCity})");

                    // Extract numeric value for spoken fare (e.g., "€12.50" -> "12 euros 50")
                    var spokenFare = FormatFareForSpeech(normalizedFare);
                    
                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        fare = normalizedFare,
                        fare_spoken = spokenFare,
                        eta = fareResult.Eta,
                        message = $"The fare is {spokenFare}, and the driver will arrive in {fareResult.Eta}. Ask if they want to confirm."
                    }).ConfigureAwait(false);

                    await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                }
                else if (action == "confirmed")
                {
                    if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                    {
                        Log($"⚠️ Booking confirmed but pickup/destination missing (pickup='{_booking.Pickup}', dest='{_booking.Destination}')");
                        await SendToolResultAsync(callId, new
                        {
                            success = false,
                            error = "Missing pickup or destination",
                            message = "Missing pickup or destination. Ask the caller to repeat it before booking."
                        }).ConfigureAwait(false);
                        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                        break;
                    }

                    _booking.Confirmed = true;
                    _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    _awaitingConfirmation = false;

                    // Ensure we have geocoded components before dispatch (e.g., if quote step was skipped/failed)
                    if ((string.IsNullOrWhiteSpace(_booking.PickupStreet) || string.IsNullOrWhiteSpace(_booking.DestStreet)) &&
                        !string.IsNullOrWhiteSpace(_booking.Pickup) &&
                        !string.IsNullOrWhiteSpace(_booking.Destination))
                    {
                        try
                        {
                            // Use Lovable AI extraction for intelligent address resolution
                            string resolvedPickup = _booking.Pickup!;
                            string resolvedDest = _booking.Destination!;
                            
                            var aiResult = await FareCalculator.ExtractAddressesWithLovableAiAsync(
                                _booking.Pickup,
                                _booking.Destination,
                                _callerId).ConfigureAwait(false);
                            
                            if (aiResult != null)
                            {
                                if (!string.IsNullOrEmpty(aiResult.pickup?.address))
                                    resolvedPickup = aiResult.pickup.address;
                                if (!string.IsNullOrEmpty(aiResult.dropoff?.address))
                                    resolvedDest = aiResult.dropoff.address;
                            }
                            
                            var fareResult = await FareCalculator.CalculateFareWithCoordsAsync(
                                resolvedPickup,
                                resolvedDest,
                                _callerId).ConfigureAwait(false);

                            _booking.PickupLat = fareResult.PickupLat;
                            _booking.PickupLon = fareResult.PickupLon;
                            _booking.PickupStreet = fareResult.PickupStreet;
                            _booking.PickupNumber = fareResult.PickupNumber;
                            _booking.PickupPostalCode = fareResult.PickupPostalCode;
                            _booking.PickupCity = fareResult.PickupCity;
                            _booking.PickupFormatted = fareResult.PickupFormatted;

                            _booking.DestLat = fareResult.DestLat;
                            _booking.DestLon = fareResult.DestLon;
                            _booking.DestStreet = fareResult.DestStreet;
                            _booking.DestNumber = fareResult.DestNumber;
                            _booking.DestPostalCode = fareResult.DestPostalCode;
                            _booking.DestCity = fareResult.DestCity;
                            _booking.DestFormatted = fareResult.DestFormatted;

                            // Preserve existing fare if already set
                            if (!string.IsNullOrWhiteSpace(fareResult.Fare)) _booking.Fare ??= NormalizeEuroFare(fareResult.Fare);
                            if (!string.IsNullOrWhiteSpace(fareResult.Eta)) _booking.Eta ??= fareResult.Eta;
                        }
                        catch (Exception ex)
                        {
                            Log($"⚠️ Pre-dispatch geocode failed: {ex.Message}");
                        }
                    }

                    OnBookingUpdated?.Invoke(_booking);
                    Log($"✅ Booked: {_booking.BookingRef} (caller={_callerId})");

                    // Dispatch to BSQD API with geocoded address components
                    if (!string.IsNullOrEmpty(_callerId))
                    {
                        BsqdDispatcher.OnLog += msg => Log(msg);
                        BsqdDispatcher.Dispatch(_booking, _callerId);
                    }

                    // Fire-and-forget with explicit error logging
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SendWhatsAppNotificationAsync(_callerId);
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ WhatsApp task error: {ex.Message}");
                        }
                    });

                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        booking_ref = _booking.BookingRef,
                        message = string.IsNullOrWhiteSpace(_booking.Name)
                            ? "Your taxi is booked!"
                            : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
                    }).ConfigureAwait(false);

                    await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);

                    // Extended grace period (15s) for "anything else?" flow, then force end
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(15000).ConfigureAwait(false);

                        // Wait for any current response to finish
                        for (int i = 0; i < 50 && Volatile.Read(ref _responseActive) == 1; i++)
                            await Task.Delay(100).ConfigureAwait(false);

                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                            return;

                        // Only proceed if no response is active/queued
                        if (!CanCreateResponse())
                            return;

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
                                        text = "[POST-BOOKING TIMEOUT] The caller went silent. Say: 'You'll receive a WhatsApp with your booking details. Thank you for using the Voice Taxibot system. Goodbye!' THEN immediately call end_call(reason='booking_complete')."
                                    }
                                }
                            }
                        }).ConfigureAwait(false);

                        // DO NOT set _responseActive = 1 here — let OpenAI do it
                        await QueueResponseCreateAsync(delayMs: 20, waitForCurrentResponse: false).ConfigureAwait(false);

                        // Safety net: if the model doesn't call end_call, force hangup anyway.
                        // Only do this if the caller stays silent (speech_started increments _noReplyWatchdogId).
                        var safetyWatchdogId = Volatile.Read(ref _noReplyWatchdogId);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(12000).ConfigureAwait(false);
                            if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                return;

                            // Cancel safety hangup if the user spoke / watchdog was reset.
                            if (Volatile.Read(ref _noReplyWatchdogId) != safetyWatchdogId)
                                return;

                            // If a response is currently active, don't interrupt.
                            if (Volatile.Read(ref _responseActive) == 1)
                                return;

                            Log("⏰ Post-booking safety hangup (end_call not received)");
                            Interlocked.Exchange(ref _ignoreUserAudio, 1);
                            SignalCallEnded("post_booking_safety");
                        });
                    });
                }
                break;
            }

            case "find_local_events":
            {
                var category = args.TryGetValue("category", out var cat) ? cat?.ToString() ?? "all" : "all";
                var near = args.TryGetValue("near", out var n) ? n?.ToString() : null;
                var date = args.TryGetValue("date", out var dt) ? dt?.ToString() ?? "this weekend" : "this weekend";

                Log($"🎭 Events lookup: {category} near {near ?? "unknown"} on {date}");

                // Mock response - in production this would call an events API
                var mockEvents = new[]
                {
                    new { name = "Live Music at The Empire", venue = near ?? "city centre", date = "Tonight, 8pm", type = "concert" },
                    new { name = "Comedy Night at The Kasbah", venue = near ?? "city centre", date = "Saturday, 9pm", type = "comedy" },
                    new { name = "Theatre Royal Show", venue = near ?? "city centre", date = "This weekend", type = "theatre" }
                };

                await SendToolResultAsync(callId, new
                {
                    success = true,
                    events = mockEvents,
                    message = $"Found {mockEvents.Length} events near {near ?? "your area"}. Would you like a taxi to any of these?"
                }).ConfigureAwait(false);

                await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                break;
            }

            case "end_call":
                Log("📞 End call requested - ignoring further user audio...");
                Interlocked.Exchange(ref _ignoreUserAudio, 1);  // Stop accepting mic input immediately
                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);

                // Wait for audio buffer to drain (max 10s) then end
                _ = Task.Run(async () =>
                {
                    var waitStart = NowMs();
                    const int MAX_WAIT_MS = 10000;
                    const int CHECK_INTERVAL_MS = 100;

                    while (NowMs() - waitStart < MAX_WAIT_MS)
                    {
                        if (_outboundQueue.IsEmpty)
                        {
                            Log("✅ Audio buffer drained, ending call");
                            break;
                        }
                        await Task.Delay(CHECK_INTERVAL_MS).ConfigureAwait(false);
                    }

                    if (!_outboundQueue.IsEmpty)
                        Log($"⚠️ Buffer still has {_outboundQueue.Count} frames, ending anyway");

                    SignalCallEnded("end_call");
                });
                break;

            default:
                await SendToolResultAsync(callId, new { error = $"Unknown: {name}" }).ConfigureAwait(false);
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
        // v4.2: Configurable output mode - A-law passthrough or PCM
        var outputFormat = _useALawOutput ? "g711_alaw" : "pcm16";
        
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetSystemPrompt(),
                voice = _voice,
                input_audio_format = "pcm16",       // Always 24kHz PCM input (ingress pipeline handles decoding/resampling)
                output_audio_format = outputFormat, // v4.2: A-law for direct passthrough, or pcm16 for legacy
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
                    silence_duration_ms = 900     // v2.1: Calmer, less racy (was 700)
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        await SendJsonAsync(config).ConfigureAwait(false);
        Log($"🎧 Session configured (lang={_detectedLanguage}, in=pcm16, out={outputFormat})");
    }

    private async Task SendGreetingAsync()
    {
        // Short delay to let session stabilize
        await Task.Delay(100).ConfigureAwait(false);

        var greeting = GetLocalizedGreeting(_detectedLanguage);

        // v3.3: Do NOT manually reset _responseActive/_responseQueued - let OpenAI lifecycle manage this
        // Forced resets corrupt turn timing and cause clipped/crispy audio
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
            // PERF: Use cached JsonSerializerOptions to avoid per-call allocation
            var json = JsonSerializer.Serialize(obj, JsonOptions);
            await SendTextAsync(json).ConfigureAwait(false);
        }
        catch { }
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
    // LATE CONFIRMATION HANDLING (v3.6)
    // ===========================================
    /// <summary>
    /// Handles confirmations that arrive after Ada's response has already completed.
    /// This occurs when the user says "yes" but Ada already responded with "just a moment"
    /// without calling the book_taxi tool. We inject a system message to force the tool call.
    /// </summary>
    private async Task HandleLateConfirmationAsync(string userText)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;
        
        // Cancel the no-reply watchdog since user DID respond
        Interlocked.Increment(ref _noReplyWatchdogId);
        
        // Inject a system message that forces Ada to call book_taxi(request_quote)
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
                        text = $"[USER CONFIRMED BOOKING] The user said \"{userText}\". This is a confirmation. You MUST now call the book_taxi tool with action='request_quote' to get the fare quote. Do NOT speak first - call the tool immediately."
                    }
                }
            }
        }).ConfigureAwait(false);
        
        // Trigger an immediate response from the AI
        await Task.Delay(20).ConfigureAwait(false);
        
        if (Volatile.Read(ref _callEnded) == 0 && Volatile.Read(ref _disposed) == 0)
        {
            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            Log("🔄 response.create sent (late confirmation)");
        }
    }

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

    private string GetSystemPrompt() => $@"You are Ada, a taxi booking assistant for Voice Taxibot. Version 3.4.

## VOICE STYLE

Speak naturally, like a friendly professional taxi dispatcher.
- Warm, calm, confident tone
- Clear pronunciation of names and addresses
- Short pauses between phrases
- Never rush or sound robotic
- Patient and relaxed pace

## LANGUAGE

Start in {GetLanguageName(_detectedLanguage)} based on caller's phone number. However, CONTINUOUSLY MONITOR the caller's spoken language. If they speak a different language OR explicitly ASK you to speak another language (e.g. 'Can you speak French?', 'Spreek Nederlands'), IMMEDIATELY SWITCH to that language for ALL subsequent responses. Supported: English, Dutch, French, German, Spanish, Italian, Polish, Portuguese. Default to English if uncertain.

## BOOKING FLOW

Greet → NAME → PICKUP → DESTINATION → PASSENGERS → TIME → Confirm details once ('So that's [passengers] from [pickup] to [destination] at [time]. Correct?') → If changes: update and confirm ONCE more → If correct: 'Just a moment while I get the price for you.' → book_taxi(request_quote) → Announce fare → 'Would you like to confirm this booking?' → book_taxi(confirmed) → Give reference ID ONLY (no repeat of journey) → 'Anything else?' → If no: 'You'll receive a WhatsApp with your booking details. Thank you for using Voice Taxibot. Goodbye!' → end_call

## CRITICAL: FRESH SESSION - NO CACHED DATA

THIS IS A NEW CALL. You have NO prior knowledge of this caller.
- NEVER assume addresses, times, or passenger counts from 'memory'
- ONLY use data the user provides IN THIS CONVERSATION
- If your summary doesn't match what the user said THIS CALL, you have a bug - stop and re-ask

## DATA SYNC (CRITICAL)

After EVERY user message that provides or corrects booking info, call sync_booking_data immediately.
- Include ALL fields you know so far (caller_name, pickup, destination, passengers, pickup_time), not just the one you just collected.
- If a user corrects a detail, treat the user's correction as the SOURCE OF TRUTH.
- Keep addresses VERBATIM as spoken (do not 'improve' them). If user says a hyphen (e.g. '52-8'), keep the hyphen.

## IMPLICIT CORRECTIONS (CRITICAL)

Users often correct addresses by simply REPEATING them with the correct detail - WITHOUT saying 'no' or 'wrong'.
- If user repeats an address with a DIFFERENT city/detail than you stored, THIS IS A CORRECTION - update immediately
- Example: You stored 'Russell Street, Coltree' but user says 'Russell Street in Coventry' → UPDATE to 'Russell Street, Coventry'
- Example: You said 'David Road' but user says '52A David Road' → UPDATE to add the house number
- ALWAYS trust the user's latest version over your stored version
- Allow corrections at ANY point in the conversation, even after confirmation
- When user corrects, acknowledge it: 'Got it, [corrected address]' then continue

## ADDRESS INTEGRITY (CRITICAL - NO HALLUCINATION)

NEVER add, invent, or guess address components the user did not say:
- If user says 'Russell Street' without a house number → store 'Russell Street' (NOT '7 Russell Street' or '1214A Russell Street')
- If user says '52A David Road' → store '52A David Road' (exact)
- ONLY include house numbers, postcodes, or cities that the USER explicitly stated
- When confirming, read back EXACTLY what was stored - do not embellish
- HALLUCINATING addresses or house numbers is a CRITICAL ERROR - if unsure, ASK the user

## PICKUP TIME HANDLING

- If user says 'now', 'right now', 'as soon as possible', 'ASAP' → store and confirm as 'now' or 'as soon as possible'
- NEVER convert 'now' to a specific clock time like '3:45 PM'
- Only use specific times if the USER gives a specific time (e.g., '3 o'clock', 'at 5:30')

## SUMMARY CONSISTENCY (CRITICAL)

Your booking summary MUST use the EXACT SAME data you collected and confirmed during THIS conversation:
- Use the EXACT addresses the user gave THIS call (not from any 'memory')
- Use the EXACT time the user gave THIS call
- If user said 'now' → summary says 'now' (NOT '3:45 PM')
- If you said 'Pickup is 52A David Road' → summary MUST say '52A David Road'
- NEVER introduce new details in the summary that weren't spoken THIS call

## ETA HANDLING

- For IMMEDIATE trips (pickup time is 'now'): Say 'The driver will arrive in about [X] minutes'
- For FUTURE trips (pickup time is a specific time): Do NOT mention driver arrival time - just confirm the scheduled pickup time

## CURRENCY

ALL prices are in EUROS (€). When announcing fares, use the 'fare_spoken' field from the tool result (e.g., '12 euros 50'). NEVER say 'dollars' or 'pounds'.

## ABSOLUTE RULES - VIOLATION FORBIDDEN

1. You MUST call sync_booking_data after every user response containing booking info
2. You MUST call book_taxi(action='confirmed') BEFORE announcing any booking confirmation
3. You MUST NOT say 'your taxi is booked' or give ANY reference number until book_taxi returns success
4. The booking reference comes ONLY from the book_taxi tool result - NEVER invent one
5. If book_taxi fails, tell the user and ask if they want to try again

## PRONUNCIATION

- 4-digit house numbers like '1214A' say 'twelve fourteen A' (NOT 'one two one four')
- Hyphenated ranges like '12-14' say 'twelve to fourteen' 
- Suffixes like '52A' say 'fifty-two A'

## CONFIRMATION DETECTION

These phrases mean YES - proceed immediately:
'yes', 'yeah', 'yep', 'sure', 'ok', 'okay', 'correct', 'that's right', 'go ahead', 'book it', 'please do', 'confirm', 'that's fine'

## RESPONSE STYLE

One question at a time. Under 20 words per response. Only call end_call after user says no to 'anything else'.";

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
            newPickup = p?.ToString();
            _booking.Pickup = newPickup;
        }
        if (args.TryGetValue("destination", out var d))
        {
            newDest = d?.ToString();
            _booking.Destination = newDest;
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
            tasks.Add(Task.Run(async () =>
            {
                var result = await FareCalculator.VerifyAddressAsync(newPickup, _callerId);
                return ("pickup", result);
            }));
        }
        
        if (!string.IsNullOrWhiteSpace(newDest))
        {
            tasks.Add(Task.Run(async () =>
            {
                var result = await FareCalculator.VerifyAddressAsync(newDest, _callerId);
                return ("destination", result);
            }));
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
