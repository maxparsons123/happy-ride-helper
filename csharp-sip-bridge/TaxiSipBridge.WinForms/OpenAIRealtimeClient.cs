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
    /// <summary>PCM16 @ 24kHz - requires local resampling/encoding to G.711</summary>
    MuLaw,
    
    /// <summary>G.711 A-law @ 8kHz - direct passthrough to RTP</summary>
    ALaw,
}

/// <summary>
/// OpenAI Realtime API client with STT-gated response logic.
/// Version 5.5 - Clean PCM16 path with proper audio routing
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    public const string VERSION = "5.5";

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _dispatchWebhookUrl;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // =========================
    // OUTPUT CODEC
    // =========================
    // Default is MuLaw which maps to output_audio_format="pcm16" (24kHz PCM from OpenAI)
    // ALaw maps to output_audio_format="g711_alaw" (8kHz A-law passthrough)
    private OutputCodecMode _outputCodec = OutputCodecMode.MuLaw;
    public OutputCodecMode OutputCodec => _outputCodec;

    // =========================
    // THREAD-SAFE STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _responseQueued;
    private int _greetingSent;
    private int _ignoreUserAudio;
    private int _deferredResponsePending;
    private int _noReplyWatchdogId;
    private int _transcriptPending;
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt;
    private long _speechStoppedAt;

    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

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

    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    // =========================
    // AUDIO OUTPUT QUEUE
    // =========================
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 800;

    // =========================
    // EVENTS
    // =========================
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action<byte[]>? OnALawAudio;
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
    // CONSTRUCTOR
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
        
        // Apply explicit codec if provided, otherwise keep default (MuLaw = pcm16)
        if (outputCodec.HasValue) 
            _outputCodec = outputCodec.Value;
        
        Log($"🎵 OpenAIRealtimeClient v{VERSION} created (output={_outputCodec})");
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

        // Decode µ-law to PCM16
        var pcm8k = new short[ulawData.Length];
        for (int i = 0; i < ulawData.Length; i++)
            pcm8k[i] = SIPSorcery.Media.MuLawDecoder.MuLawToLinearSample(ulawData[i]);

        // Upsample 8kHz → 24kHz (3x)
        var pcm24k = Resample8kTo24k(pcm8k);
        await SendAudioAsync(pcm24k, 24000).ConfigureAwait(false);
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        Volatile.Write(ref _lastUserSpeechAt, NowMs());

        byte[] pcm24k;
        if (sampleRate == 24000)
        {
            pcm24k = pcmData;
        }
        else if (sampleRate == 8000)
        {
            var samples8k = new short[pcmData.Length / 2];
            Buffer.BlockCopy(pcmData, 0, samples8k, 0, pcmData.Length);
            pcm24k = Resample8kTo24k(samples8k);
        }
        else if (sampleRate == 16000)
        {
            var samples16k = new short[pcmData.Length / 2];
            Buffer.BlockCopy(pcmData, 0, samples16k, 0, pcmData.Length);
            pcm24k = Resample16kTo24k(samples16k);
        }
        else
        {
            pcm24k = pcmData;
        }

        var b64 = Convert.ToBase64String(pcm24k);
        await SendJsonAsync(new { type = "input_audio_buffer.append", audio = b64 }).ConfigureAwait(false);
    }

    public byte[]? GetNextMuLawFrame() => _outboundQueue.TryDequeue(out var f) ? f : null;

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    // =========================
    // AUDIO OUTPUT PROCESSING
    // =========================
    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var audioBytes = Convert.FromBase64String(base64);

            if (_outputCodec == OutputCodecMode.ALaw)
            {
                // A-LAW PASSTHROUGH: OpenAI sends raw G.711 A-law bytes
                OnALawAudio?.Invoke(audioBytes);

                // Split into 20ms frames (160 bytes @ 8kHz)
                for (int i = 0; i < audioBytes.Length; i += 160)
                {
                    int len = Math.Min(160, audioBytes.Length - i);
                    var frame = new byte[160];
                    Buffer.BlockCopy(audioBytes, i, frame, 0, len);
                    if (len < 160) Array.Fill(frame, (byte)0xD5, len, 160 - len);
                    EnqueueFrame(frame);
                }
            }
            else
            {
                // PCM16 PATH: OpenAI sends 24kHz PCM16
                OnPcm24Audio?.Invoke(audioBytes);

                // Resample 24kHz → 8kHz and encode to µ-law for queue
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

    // =========================
    // RESAMPLING
    // =========================
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
        return ShortsToBytes(pcm24k);
    }

    private static byte[] Resample16kTo24k(short[] pcm16k)
    {
        // 16kHz → 24kHz is 2:3 ratio, use linear interpolation
        int outLen = (pcm16k.Length * 3) / 2;
        var pcm24k = new short[outLen];
        for (int i = 0; i < outLen; i++)
        {
            float srcIdx = i * 2.0f / 3.0f;
            int idx0 = (int)srcIdx;
            int idx1 = Math.Min(idx0 + 1, pcm16k.Length - 1);
            float frac = srcIdx - idx0;
            pcm24k[i] = (short)(pcm16k[idx0] * (1 - frac) + pcm16k[idx1] * frac);
        }
        return ShortsToBytes(pcm24k);
    }

    private static byte[] ShortsToBytes(short[] shorts)
    {
        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
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

    private void ResetCallState(string? caller)
    {
        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(_callerId);
        _booking.Reset();
        _awaitingConfirmation = false;

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

        ClearPendingFrames();
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
                    HandleResponseCreated(doc.RootElement);
                    break;

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                    {
                        var delta = deltaEl.GetString();
                        if (!string.IsNullOrEmpty(delta))
                            ProcessAudioDelta(delta);
                    }
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var textDelta))
                        OnAdaSpeaking?.Invoke(textDelta.GetString() ?? "");
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var transcript))
                    {
                        var text = transcript.GetString() ?? "";
                        Log($"💬 Ada: {text}");
                        OnTranscript?.Invoke($"Ada: {text}");
                    }
                    break;

                case "response.done":
                    HandleResponseDone(doc.RootElement);
                    break;

                case "input_audio_buffer.speech_started":
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _speechStoppedAt, NowMs());
                    break;

                case "input_audio_buffer.committed":
                    Log("📝 Committed audio buffer (awaiting transcript)");
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    HandleTranscriptCompleted(doc.RootElement);
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "error":
                    HandleError(doc.RootElement);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"❌ Message processing error: {ex.Message}");
        }
    }

    private void HandleResponseCreated(JsonElement root)
    {
        var responseId = TryGetResponseId(root);
        if (responseId != null && _activeResponseId == responseId)
            return;

        _activeResponseId = responseId;
        Interlocked.Exchange(ref _responseActive, 1);
        Volatile.Write(ref _responseCreatedAt, NowMs());

        _ = ClearInputAudioBufferAsync();

        Log("🤖 AI response started");
        OnResponseStarted?.Invoke();
    }

    private void HandleResponseDone(JsonElement root)
    {
        var responseId = TryGetResponseId(root);
        if (responseId != null && _lastCompletedResponseId == responseId)
            return;

        _lastCompletedResponseId = responseId;
        Interlocked.Exchange(ref _responseActive, 0);
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());

        Log("🤖 AI response completed");
        OnResponseCompleted?.Invoke();

        // Check for deferred response
        if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(50).ConfigureAwait(false);
                if (CanCreateResponse())
                {
                    await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                    Log("🔄 Deferred response.create sent");
                }
            });
        }

        // Start no-reply watchdog
        StartNoReplyWatchdog();
    }

    private void HandleTranscriptCompleted(JsonElement root)
    {
        var speechStopped = Volatile.Read(ref _speechStoppedAt);
        var responseCreated = Volatile.Read(ref _responseCreatedAt);
        var now = NowMs();

        var msSinceSpeech = speechStopped > 0 ? now - speechStopped : -1;
        var msSinceResponse = responseCreated > 0 ? now - responseCreated : -1;

        if (root.TryGetProperty("transcript", out var transcriptEl))
        {
            var text = transcriptEl.GetString() ?? "";
            Log($"📝 Transcript arrived {msSinceSpeech}ms after speech stopped, {msSinceResponse}ms after response.created: {text}");
            Log($"👤 User: {text}");
            OnTranscript?.Invoke($"You: {text}");

            // Check for late confirmation
            if (_awaitingConfirmation && IsAffirmative(text))
            {
                Log("✅ Late confirmation detected - triggering booking");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    if (CanCreateResponse())
                    {
                        await SendJsonAsync(new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "message",
                                role = "user",
                                content = new[] { new { type = "input_text", text = "Yes, please confirm my booking." } }
                            }
                        }).ConfigureAwait(false);
                        await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                    }
                });
            }
        }

        Interlocked.Exchange(ref _transcriptPending, 0);
    }

    private void HandleError(JsonElement root)
    {
        var errMsg = "Unknown error";
        if (root.TryGetProperty("error", out var err))
        {
            if (err.TryGetProperty("message", out var msg))
                errMsg = msg.GetString() ?? errMsg;
        }
        Log($"⚠️ OpenAI error: {errMsg}");
    }

    private static string? TryGetResponseId(JsonElement root)
    {
        if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("id", out var id))
            return id.GetString();
        if (root.TryGetProperty("response_id", out var rid))
            return rid.GetString();
        return null;
    }

    private async Task ClearInputAudioBufferAsync()
    {
        if (Volatile.Read(ref _transcriptPending) == 1)
        {
            Log("⏳ Skipping buffer clear - transcript pending");
            return;
        }
        await SendJsonAsync(new { type = "input_audio_buffer.clear" }).ConfigureAwait(false);
        Log("🧹 Cleared OpenAI input audio buffer");
    }

    private void StartNoReplyWatchdog()
    {
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(15000).ConfigureAwait(false);
            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _callEnded) != 0) return;
            if (Volatile.Read(ref _responseActive) == 1) return;
            if (NowMs() - Volatile.Read(ref _lastUserSpeechAt) < 10000) return;

            Log("⏰ No-reply watchdog triggered (15000ms) - prompting user");
        });
    }

    // =========================
    // TOOL HANDLING
    // =========================
    private async Task HandleToolCallAsync(JsonElement root)
    {
        Volatile.Write(ref _lastToolCallAt, NowMs());

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
                ApplyBookingSnapshotFromArgs(args);
                OnBookingUpdated?.Invoke(_booking);
                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
                await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                break;

            case "book_taxi":
                await HandleBookTaxiAsync(callId, args).ConfigureAwait(false);
                break;

            case "end_call":
                Log("📞 end_call requested");
                Interlocked.Exchange(ref _ignoreUserAudio, 1);
                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000).ConfigureAwait(false);
                    SignalCallEnded("end_call");
                });
                break;

            default:
                await SendToolResultAsync(callId, new { error = $"Unknown tool: {name}" }).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleBookTaxiAsync(string callId, Dictionary<string, object?> args)
    {
        ApplyBookingSnapshotFromArgs(args);
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

        if (action == "request_quote")
        {
            if (string.IsNullOrWhiteSpace(_booking.Pickup) || string.IsNullOrWhiteSpace(_booking.Destination))
            {
                Log($"⚠️ Quote requested but pickup/destination missing");
                await SendToolResultAsync(callId, new
                {
                    success = false,
                    error = "Missing pickup or destination",
                    message = "Missing pickup or destination. Ask the caller to repeat it."
                }).ConfigureAwait(false);
                await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                return;
            }

            // Calculate fare with geocoding (with timeout guard)
            FareResult fareResult;
            try
            {
                Log("💰 Calculating fare...");
                var fareTask = FareCalculator.CalculateFareWithCoordsAsync(
                    _booking.Pickup!,
                    _booking.Destination!,
                    _callerId);

                var completed = await Task.WhenAny(fareTask, Task.Delay(3000)).ConfigureAwait(false);
                if (completed != fareTask)
                {
                    Log("⏱️ Fare calculation timed out - using fallback");
                    fareResult = new FareResult { Fare = "£12.50", Eta = "6 minutes" };
                }
                else
                {
                    fareResult = await fareTask.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Fare calculation failed: {ex.Message}");
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

            Log($"💰 Quote: {_booking.Fare} (pickup: {fareResult.PickupCity}, dest: {fareResult.DestCity})");

            await SendToolResultAsync(callId, new
            {
                success = true,
                fare = _booking.Fare,
                eta = _booking.Eta,
                message = $"The fare is {_booking.Fare}, and the driver will arrive in {_booking.Eta}. Ask if they want to confirm."
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
                    error = "Missing pickup or destination"
                }).ConfigureAwait(false);
                await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
                return;
            }

            _booking.Confirmed = true;
            _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _awaitingConfirmation = false;

            OnBookingUpdated?.Invoke(_booking);
            Log($"✅ Booked: {_booking.BookingRef} (caller={_callerId})");

            // Dispatch to BSQD
            if (!string.IsNullOrEmpty(_callerId))
            {
                BsqdDispatcher.OnLog += msg => Log(msg);
                BsqdDispatcher.Dispatch(_booking, _callerId);
            }

            // WhatsApp notification (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    var (success, message) = await WhatsAppNotifier.SendAsync(_callerId);
                    Log(success ? $"📱 WhatsApp: {message}" : $"📱 WhatsApp failed: {message}");
                }
                catch (Exception ex)
                {
                    Log($"📱 WhatsApp error: {ex.Message}");
                }
            });

            await SendToolResultAsync(callId, new
            {
                success = true,
                booking_ref = _booking.BookingRef,
                message = $"Your taxi is booked! Reference: {_booking.BookingRef}"
            }).ConfigureAwait(false);

            await QueueResponseCreateAsync(delayMs: 10, waitForCurrentResponse: false, maxWaitMs: 0).ConfigureAwait(false);
        }
        else
        {
            await SendToolResultAsync(callId, new { error = $"Unknown action: {action}" }).ConfigureAwait(false);
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
        var result = new Dictionary<string, object?>();
        if (!root.TryGetProperty("arguments", out var argsEl)) return result;

        try
        {
            var argsStr = argsEl.GetString();
            if (string.IsNullOrEmpty(argsStr)) return result;

            using var doc = JsonDocument.Parse(argsStr);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetInt32(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
        }
        catch { }
        return result;
    }

    private void ApplyBookingSnapshotFromArgs(Dictionary<string, object?> args)
    {
        if (args.TryGetValue("caller_name", out var name) && name != null)
            _booking.Name = name.ToString();
        if (args.TryGetValue("pickup", out var pickup) && pickup != null)
            _booking.Pickup = pickup.ToString();
        if (args.TryGetValue("destination", out var dest) && dest != null)
            _booking.Destination = dest.ToString();
        if (args.TryGetValue("passengers", out var pax) && pax != null && int.TryParse(pax.ToString(), out var paxNum))
            _booking.Passengers = paxNum;
        if (args.TryGetValue("pickup_time", out var time) && time != null)
            _booking.PickupTime = time.ToString();
    }

    private static bool IsAffirmative(string text)
    {
        var lower = text.ToLowerInvariant().Trim();
        string[] affirmatives = { "yes", "yeah", "yep", "sure", "ok", "okay", "correct", "that's right", "go ahead", "book it", "please do", "confirm", "that's fine" };
        return affirmatives.Any(a => lower.Contains(a));
    }

    // =========================
    // SESSION CONFIG
    // =========================
    private async Task ConfigureSessionAsync()
    {
        // CRITICAL: Map codec to OpenAI output format
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
    // HELPERS
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

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.CompareExchange(ref _callEnded, 1, 0) == 0)
        {
            Log($"📴 Call ended: {reason}");
            OnCallEnded?.Invoke();
        }
    }

    private async Task SendJsonAsync(object obj)
    {
        if (!IsConnected) return;

        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? default)
                     .ConfigureAwait(false);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var clean = phone.Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("06") || clean.StartsWith("+31") || clean.StartsWith("0031"))
            return "nl";
        return "en";
    }

    private string GetLocalizedGreeting() => _detectedLanguage switch
    {
        "nl" => "Hallo, welkom bij de Taxibot demo. Ik ben Ada, uw taxi assistent. Wat is uw naam?",
        _ => "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. What's your name?"
    };

    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "Sync booking data after user provides information.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    caller_name = new { type = "string" },
                    pickup = new { type = "string" },
                    destination = new { type = "string" },
                    passengers = new { type = "integer" },
                    pickup_time = new { type = "string" }
                }
            }
        },
        new
        {
            type = "function",
            name = "book_taxi",
            description = "Request quote or confirm booking.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                    pickup = new { type = "string" },
                    destination = new { type = "string" },
                    passengers = new { type = "integer" },
                    pickup_time = new { type = "string" }
                },
                required = new[] { "action" }
            }
        },
        new
        {
            type = "function",
            name = "end_call",
            description = "End the call after saying goodbye",
            parameters = new { type = "object", properties = new { } }
        }
    };

    private string GetSystemPrompt() => $"""
        You are Ada, a friendly taxi booking assistant for Voice Taxibot. Version {VERSION}.

        ## VOICE STYLE
        Speak naturally, like a friendly professional taxi dispatcher.
        - Warm, calm, confident tone
        - Clear pronunciation of names and addresses
        - Short pauses between phrases
        - Never rush or sound robotic

        ## BOOKING FLOW
        1. Greet and ask for caller's name
        2. Ask for pickup address
        3. Ask for destination
        4. Ask about passengers (default 1)
        5. Ask about pickup time (default "now")
        6. Call sync_booking_data after EVERY piece of info
        7. Recap the booking and call book_taxi with action="request_quote"
        8. Tell price and ask for confirmation
        9. When confirmed, call book_taxi with action="confirmed"
        10. Read the booking reference from the tool result
        11. Say goodbye with the REAL reference and call end_call

        ## RULES
        - Keep responses under 20 words
        - One question at a time
        - Always use British Pounds (£) for fares
        - Preserve addresses exactly as user says them

        ## CONFIRMATION DETECTION
        These phrases mean YES: 'yes', 'yeah', 'yep', 'sure', 'ok', 'okay', 'correct', 'go ahead', 'book it', 'confirm'
        """;

    // =========================
    // DISPOSAL
    // =========================
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _sendMutex.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
