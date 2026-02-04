using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using taxibridgemain;

namespace TaxiSipBridge;

/// <summary>
/// Output codec mode for AI audio output.
/// </summary>
public enum OutputCodecMode
{
    MuLaw,   // G.711 µ-law @ 8kHz (narrowband)
    ALaw,    // G.711 A-law @ 8kHz (narrowband)
}

/// <summary>
/// OpenAI Realtime API client with correct response lifecycle handling.
/// Key rule: DO NOT set _responseActive manually. Only OpenAI events control it.
/// Version 3.4 + A-law passthrough
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    public const string VERSION = "3.4-alaw";

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _dispatchWebhookUrl;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // =========================
    // OUTPUT CODEC (session output format selector)
    // =========================
    // Default MUST be the safe PCM16 path (OpenAI output_audio_format="pcm16").
    // ALaw is only valid when the SIP playout is in direct A-law passthrough mode.
    private OutputCodecMode _outputCodec = OutputCodecMode.MuLaw;
    public OutputCodecMode OutputCodec => _outputCodec;
    
    public void SetOutputCodec(OutputCodecMode codec)
    {
        _outputCodec = codec;
        Log($"🎵 Output codec: {codec}");
    }

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
    public event Action<byte[]>? OnPcm24Audio;
    public event Action<byte[]>? OnALawAudio;  // A-law passthrough event
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
    public bool IsResponseActive => Volatile.Read(ref _responseActive) == 1;
    public string DetectedLanguage => _detectedLanguage;

    // =========================
    // CONSTRUCTORS
    // =========================
    public OpenAIRealtimeClient(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        OutputCodecMode? outputCodec = null,
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _dispatchWebhookUrl = dispatchWebhookUrl;
        if (outputCodec.HasValue) _outputCodec = outputCodec.Value;
    }

    // =========================
    // RESPONSE GATE
    // =========================
    private bool CanCreateResponse()
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               Volatile.Read(ref _responseQueued) == 0 &&
               Volatile.Read(ref _transcriptPending) == 0 &&
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300;
    }

    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true, int maxWaitMs = 1000)
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

            if (!CanCreateResponse())
                return;

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

        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 300)
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
        catch { }
    }

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
            var audioBytes = Convert.FromBase64String(base64);

            // A-LAW PASSTHROUGH: When using A-law mode, OpenAI sends raw G.711 A-law bytes directly
            if (_outputCodec == OutputCodecMode.ALaw)
            {
                // Fire the A-law event for direct SIP passthrough
                OnALawAudio?.Invoke(audioBytes);

                // Split into 20ms frames (160 bytes @ 8kHz)
                for (int i = 0; i < audioBytes.Length; i += 160)
                {
                    int len = Math.Min(160, audioBytes.Length - i);
                    var frame = new byte[160];
                    Buffer.BlockCopy(audioBytes, i, frame, 0, len);
                    if (len < 160) Array.Fill(frame, (byte)0xD5, len, 160 - len); // A-law silence
                    EnqueueFrame(frame);
                }
                return;
            }

            // MuLaw path: PCM16 @ 24kHz from OpenAI
            OnPcm24Audio?.Invoke(audioBytes);

            var samples = AudioCodecs.BytesToShorts(audioBytes);
            var len8k = samples.Length / 3;
            var pcm8k = new short[len8k];

            for (int i = 0; i < len8k; i++)
            {
                int idx = i * 3;
                pcm8k[i] = (short)(samples[idx] * 0.25f + samples[idx + 1] * 0.5f + samples[idx + 2] * 0.25f);
            }

            var ulaw = AudioCodecs.MuLawEncode(pcm8k);

            for (int i = 0; i < ulaw.Length; i += 160)
            {
                var frame = new byte[160];
                var count = Math.Min(160, ulaw.Length - i);
                Array.Copy(ulaw, i, frame, 0, count);
                if (count < 160) Array.Fill(frame, (byte)0xFF, count, 160 - count);
                EnqueueFrame(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio error: {ex.Message}");
        }
    }

    private void EnqueueFrame(byte[] frame)
    {
        while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
            _outboundQueue.TryDequeue(out _);
        _outboundQueue.Enqueue(frame);
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
                        var responseId = TryGetResponseId(doc.RootElement);
                        if (responseId != null && _activeResponseId == responseId)
                            break;

                        _activeResponseId = responseId;
                        Interlocked.Exchange(ref _responseActive, 1);
                        Volatile.Write(ref _responseCreatedAt, NowMs());

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
                        Interlocked.Exchange(ref _transcriptPending, 0);

                        if (doc.RootElement.TryGetProperty("transcript", out var u) &&
                            !string.IsNullOrWhiteSpace(u.GetString()))
                        {
                            var text = ApplySttCorrections(u.GetString()!);
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
                    Interlocked.Increment(ref _noReplyWatchdogId);
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Volatile.Write(ref _speechStoppedAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (awaiting transcript)");
                    break;

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
                        Volatile.Write(ref _lastAdaFinishedAt, NowMs());

                        Log("🤖 AI response completed");
                        OnResponseCompleted?.Invoke();

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

                        var watchdogDelayMs = _awaitingConfirmation ? 20000 : 15000;
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
                    await VerifyAndEnrichAddressesAsync(newPickup, newDest).ConfigureAwait(false);
                    OnBookingUpdated?.Invoke(_booking);
                    await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
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
                            await SendToolResultAsync(callId, new
                            {
                                success = false,
                                error = "Missing pickup or destination",
                                message = "Missing pickup or destination. Ask the caller to repeat it."
                            }).ConfigureAwait(false);
                            await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                            break;
                        }

                        FareResult fareResult;
                        try
                        {
                            Log("💰 Starting Lovable AI address extraction...");

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
                                    if (!string.IsNullOrEmpty(aiResult.pickup?.address))
                                        resolvedPickup = aiResult.pickup.address;
                                    if (!string.IsNullOrEmpty(aiResult.dropoff?.address))
                                        resolvedDest = aiResult.dropoff.address;

                                    if (aiResult.status == "clarification_needed")
                                    {
                                        needsClarification = true;
                                        pickupAlternatives = aiResult.pickup?.alternatives;
                                        destAlternatives = aiResult.dropoff?.alternatives;
                                    }
                                }
                            }
                            else
                            {
                                Log("⏱️ Lovable AI extraction timed out (3s) — using raw addresses");
                            }

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

                            var fareTask = FareCalculator.CalculateFareWithCoordsAsync(resolvedPickup, resolvedDest, _callerId);
                            var fareCompleted = await Task.WhenAny(fareTask, Task.Delay(3000)).ConfigureAwait(false);
                            
                            if (fareCompleted != fareTask)
                            {
                                Log("⏱️ Geocoding timed out (3s) — using fallback quote");
                                fareResult = new FareResult { Fare = "£12.50", Eta = "6 minutes" };
                            }
                            else
                            {
                                fareResult = await fareTask.ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"⚠️ Fare calculation failed: {ex.Message} — using fallback quote");
                            fareResult = new FareResult { Fare = "£12.50", Eta = "6 minutes" };
                        }

                        _booking.Fare = fareResult.Fare;
                        _booking.Eta = fareResult.Eta;
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

                        _awaitingConfirmation = true;
                        OnBookingUpdated?.Invoke(_booking);
                        Log($"💰 Quote: {fareResult.Fare} (pickup: {fareResult.PickupCity}, dest: {fareResult.DestCity})");

                        await SendToolResultAsync(callId, new
                        {
                            success = true,
                            fare = fareResult.Fare,
                            eta = fareResult.Eta,
                            message = $"The fare is {fareResult.Fare}, and the driver will arrive in {fareResult.Eta}. Ask if they want to confirm."
                        }).ConfigureAwait(false);

                        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                    }
                    else if (action == "confirmed")
                    {
                        if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
                        {
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

                        OnBookingUpdated?.Invoke(_booking);
                        Log($"✅ Booked: {_booking.BookingRef} (caller={_callerId})");

                        if (!string.IsNullOrEmpty(_callerId))
                        {
                            BsqdDispatcher.OnLog += msg => Log(msg);
                            BsqdDispatcher.Dispatch(_booking, _callerId);
                        }

                        await SendToolResultAsync(callId, new
                        {
                            success = true,
                            booking_ref = _booking.BookingRef,
                            message = string.IsNullOrWhiteSpace(_booking.Name)
                                ? "Your taxi is booked!"
                                : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
                        }).ConfigureAwait(false);

                        await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);

                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(15000).ConfigureAwait(false);
                            for (int i = 0; i < 50 && Volatile.Read(ref _responseActive) == 1; i++)
                                await Task.Delay(100).ConfigureAwait(false);

                            if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                                return;
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

                            await QueueResponseCreateAsync(delayMs: 20, waitForCurrentResponse: false).ConfigureAwait(false);

                            var safetyWatchdogId = Volatile.Read(ref _noReplyWatchdogId);
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(12000).ConfigureAwait(false);
                                if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
                                if (Volatile.Read(ref _noReplyWatchdogId) != safetyWatchdogId) return;
                                if (Volatile.Read(ref _responseActive) == 1) return;
                                Log("⏰ Post-booking safety hangup (end_call not received)");
                                Interlocked.Exchange(ref _ignoreUserAudio, 1);
                                SignalCallEnded("post_booking_safety");
                            });
                        });
                    }
                    break;
                }

            case "end_call":
                {
                    var reason = args.TryGetValue("reason", out var r) ? r?.ToString() ?? "completed" : "completed";
                    Log($"📞 end_call requested: {reason}");
                    Interlocked.Exchange(ref _ignoreUserAudio, 1);
                    await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000).ConfigureAwait(false);
                        SignalCallEnded($"end_call:{reason}");
                    });
                    break;
                }
        }
    }

    // =========================
    // SESSION CONFIG (A-LAW SUPPORT)
    // =========================
    private async Task ConfigureSessionAsync()
    {
        // CRITICAL: Request A-law output from OpenAI when in A-law mode
        var outputFormat = _outputCodec == OutputCodecMode.ALaw ? "g711_alaw" : "pcm16";

        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetSystemPrompt(),
                voice = _voice,
                input_audio_format = "pcm16",
                output_audio_format = outputFormat,
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.35,
                    prefix_padding_ms = 350,
                    silence_duration_ms = 800
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        Log($"🎧 Session configured (lang={_detectedLanguage}, output={outputFormat})");
        await SendJsonAsync(config).ConfigureAwait(false);
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(200).ConfigureAwait(false);

        var greeting = GetLocalizedGreeting();
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Say exactly: \"{greeting}\""
            }
        }).ConfigureAwait(false);

        Log("📢 Greeting sent → Ada should speak now");
    }

    // =========================
    // HELPER METHODS
    // =========================
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Log(string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        OnLog?.Invoke($"{ts} {msg}");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));
    }

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
        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
        Volatile.Write(ref _responseCreatedAt, 0);
        Volatile.Write(ref _speechStoppedAt, 0);
        _activeResponseId = null;
        _lastCompletedResponseId = null;
        _awaitingConfirmation = false;
        _booking.Reset();
        ClearPendingFrames();
    }

    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var norm = phone.TrimStart('+', '0');
        if (norm.StartsWith("31")) return "nl";
        if (norm.StartsWith("32")) return "nl";
        if (norm.StartsWith("33")) return "fr";
        if (norm.StartsWith("49")) return "de";
        if (norm.StartsWith("43")) return "de";
        if (norm.StartsWith("41")) return "de";
        return "en";
    }

    private string GetLocalizedGreeting()
    {
        return _detectedLanguage switch
        {
            "nl" => "Hallo, en welkom bij de Taxibot demo. Ik ben Ada, uw taxi boekingsassistent. Wat is uw naam?",
            "fr" => "Bonjour et bienvenue à la démo Taxibot. Je suis Ada. Comment vous appelez-vous?",
            "de" => "Hallo und willkommen zur Taxibot-Demo. Ich bin Ada. Wie heißen Sie?",
            _ => "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. What's your name?"
        };
    }

    private string GetSystemPrompt()
    {
        return @"You are Ada, a friendly and efficient taxi booking assistant for a demo system. You speak in the caller's language.

BOOKING FLOW:
1. Greet and ask for name
2. Ask for pickup location
3. Ask for destination
4. Ask for number of passengers
5. Ask when they need the taxi (now or specific time)
6. Call sync_booking_data to save the details
7. Call book_taxi(action='request_quote') to get fare
8. Confirm details and price with caller
9. If confirmed, call book_taxi(action='confirmed')
10. Thank them and call end_call

RULES:
- Be concise (2-3 sentences max per turn)
- Always call sync_booking_data after collecting each piece of info
- Quote prices in £ (GBP)
- After booking confirmed, say goodbye and call end_call
- If the user is silent, gently prompt them";
    }

    private static object[] GetTools()
    {
        return new object[]
        {
            new
            {
                type = "function",
                name = "sync_booking_data",
                description = "Save collected booking information",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Caller's name" },
                        pickup = new { type = "string", description = "Pickup address" },
                        destination = new { type = "string", description = "Destination address" },
                        passengers = new { type = "integer", description = "Number of passengers" },
                        pickup_time = new { type = "string", description = "When (now or specific time)" }
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
                    properties = new
                    {
                        action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } }
                    },
                    required = new[] { "action" }
                }
            },
            new
            {
                type = "function",
                name = "end_call",
                description = "End the call after booking is complete or user wants to hang up",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        reason = new { type = "string", description = "Reason for ending call" }
                    }
                }
            }
        };
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 1)
            return;
        Log($"📴 Call ended: {reason}");
        OnCallEnded?.Invoke();
    }

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

    private async Task SendTextAsync(string text)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendMutex.Release();
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

    private static Dictionary<string, object?> ParseArgs(JsonElement root)
    {
        var args = new Dictionary<string, object?>();
        if (root.TryGetProperty("arguments", out var argsEl))
        {
            try
            {
                var argsJson = argsEl.GetString();
                if (!string.IsNullOrEmpty(argsJson))
                {
                    using var doc = JsonDocument.Parse(argsJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString()
                        };
                    }
                }
            }
            catch { }
        }
        return args;
    }

    private void ApplyBookingSnapshotFromArgs(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("name", out var n) && n != null) _booking.Name = n.ToString();
        if (args.TryGetValue("pickup", out var p) && p != null) _booking.Pickup = p.ToString();
        if (args.TryGetValue("destination", out var d) && d != null) _booking.Destination = d.ToString();
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn)) _booking.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var t) && t != null) _booking.PickupTime = t.ToString();
    }

    private (bool newPickup, bool newDest) ApplyBookingSnapshotFromArgsWithTracking(Dictionary<string, object?> args)
    {
        bool newPickup = false, newDest = false;
        if (args.TryGetValue("name", out var n) && n != null) _booking.Name = n.ToString();
        if (args.TryGetValue("pickup", out var p) && p != null)
        {
            var pStr = p.ToString();
            if (pStr != _booking.Pickup) newPickup = true;
            _booking.Pickup = pStr;
        }
        if (args.TryGetValue("destination", out var d) && d != null)
        {
            var dStr = d.ToString();
            if (dStr != _booking.Destination) newDest = true;
            _booking.Destination = dStr;
        }
        if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn)) _booking.Passengers = pn;
        if (args.TryGetValue("pickup_time", out var t) && t != null) _booking.PickupTime = t.ToString();
        return (newPickup, newDest);
    }

    private async Task VerifyAndEnrichAddressesAsync(bool newPickup, bool newDest)
    {
        if (!newPickup && !newDest) return;
        // Address verification would go here
        await Task.CompletedTask;
    }

    private static string ApplySttCorrections(string text)
    {
        var corrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "yeah please", "yes please" },
            { "yep", "yes" },
            { "yup", "yes" },
            { "yeah", "yes" },
            { "that's right", "yes" },
            { "correct", "yes" },
            { "go ahead", "yes" },
            { "book it", "yes" },
        };

        foreach (var (from, to) in corrections)
        {
            if (text.Equals(from, StringComparison.OrdinalIgnoreCase))
                return to;
        }
        return text;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendMutex.Dispose();
        ClearPendingFrames();
    }
}
