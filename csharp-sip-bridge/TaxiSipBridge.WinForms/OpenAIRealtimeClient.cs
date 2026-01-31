using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Production-grade OpenAI Realtime API client with full backward compatibility.
/// Fixed for .NET 6+: No Span/array conversion errors, WebSocketError compatibility, mutable state.
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    // ===========================================
    // PUBLIC ENUM (required by SIP bridge)
    // ===========================================
    public enum OutputCodecMode
    {
        Opus,
        ALaw,
        MuLaw
    }

    // ===========================================
    // CONFIGURATION
    // ===========================================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _systemPrompt;
    private readonly string? _dispatchWebhookUrl;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);
    private OutputCodecMode _outputCodec = OutputCodecMode.MuLaw;

    // ===========================================
    // THREAD-SAFE STATE
    // ===========================================
    private int _disposedFlag;
    private int _responseActiveFlag;
    private int _greetingSentFlag;
    private int _postBookingHangupArmedFlag;
    private int _postBookingFinalSpeechFlag;
    private int _callEndSignaledFlag;
    private int _responseCreateQueuedFlag;

    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;

    // ===========================================
    // MUTABLE STATE
    // ===========================================
    private readonly object _lock = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _callerId = "";
    private string _detectedLanguage = "en";
    private string _lastQuestionAsked = "name";
    private readonly BookingState _booking = new();
    private bool _awaitingConfirmation;
    private short[] _opusResampleBuffer = Array.Empty<short>();

    // ===========================================
    // AUDIO PIPELINE
    // ===========================================
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 800;
    private readonly ArrayPool<byte> _audioBufferPool = ArrayPool<byte>.Create(160, 100);
    private readonly ArrayPool<short> _shortBufferPool = ArrayPool<short>.Create(960, 50);

    // ===========================================
    // EVENTS (FULL COMPATIBILITY)
    // ===========================================
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;
    public event Action<BookingState>? OnBookingUpdated;
    public event Action? OnCallEnded;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action? OnResponseStarted;
    public event Action? OnResponseCompleted;
    public event Action<byte[]>? OnCallerAudioMonitor;
    public event Action<string, Dictionary<string, object>>? OnToolCall;

    // ===========================================
    // PROPERTIES
    // ===========================================
    public bool IsConnected => Volatile.Read(ref _disposedFlag) == 0 &&
                               _ws?.State == WebSocketState.Open;
    public int PendingFrameCount => _outboundQueue.Count;
    public string DetectedLanguage => Volatile.Read(ref _disposedFlag) == 0 ? _detectedLanguage : "en";
    public OutputCodecMode OutputCodec
    {
        get => _outputCodec;
        set => _outputCodec = value;
    }

    // ===========================================
    // CONSTRUCTORS (FULL BACKWARD COMPATIBILITY)
    // ===========================================
    public OpenAIRealtimeClient(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        string? systemPrompt = null,
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
        _dispatchWebhookUrl = dispatchWebhookUrl;
    }

    public OpenAIRealtimeClient(
        string apiKey,
        OutputCodecMode outputCodec,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer",
        string? systemPrompt = null)
        : this(apiKey, model, voice, systemPrompt, null)
    {
        _outputCodec = outputCodec;
    }

    // ===========================================
    // AUDIO INPUT (FULL COMPATIBILITY)
    // ===========================================
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData.Length == 0) return;

        const int echoGuardMs = 300;
        if (!_awaitingConfirmation &&
            GetUnixTimeMs() - Volatile.Read(ref _lastAdaFinishedAt) < echoGuardMs)
        {
            return;
        }

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        OnCallerAudioMonitor?.Invoke(AudioCodecs.ShortsToBytes(pcm8k));

        var pcm24k = Resample8kTo24k(pcm8k);
        await SendAudioToOpenAIAsync(pcm24k);
        Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
    }

    public Task SendPcm8kAsync(byte[] pcm8kBytes)
    {
        if (pcm8kBytes.Length % 2 != 0) throw new ArgumentException("PCM8k must be 16-bit samples");

        var samples = new short[pcm8kBytes.Length / 2];
        Buffer.BlockCopy(pcm8kBytes, 0, samples, 0, pcm8kBytes.Length);
        var ulaw = AudioCodecs.MuLawEncode(samples);
        return SendMuLawAsync(ulaw);
    }

    public Task SendPcm8kNoDspAsync(byte[] pcm8kBytes) => SendPcm8kAsync(pcm8kBytes);

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || pcmData.Length == 0) return;

        byte[] pcm24k;
        if (sampleRate != 24000)
        {
            var samples = AudioCodecs.BytesToShorts(pcmData);
            var resampled = AudioCodecs.Resample(samples, sampleRate, 24000);
            pcm24k = AudioCodecs.ShortsToBytes(resampled);
        }
        else
        {
            pcm24k = pcmData;
        }

        OnCallerAudioMonitor?.Invoke(pcm24k);
        await SendAudioToOpenAIAsync(pcm24k);
        Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
    }

    // ===========================================
    // AUDIO OUTPUT
    // ===========================================
    public byte[]? GetNextAudioFrame() => _outboundQueue.TryDequeue(out var frame) ? frame : null;
    public byte[]? GetNextMuLawFrame() => GetNextAudioFrame();
    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { /* drain */ }
    }

    // ===========================================
    // CONNECTION LIFECYCLE
    // ===========================================
    public async Task ConnectAsync(string? callerId = null, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, new CancellationTokenSource(_connectTimeout).Token);

        ResetCallState(callerId);

        try
        {
            Log($"📞 Connecting call (caller: {callerId ?? "unknown"}, lang: {_detectedLanguage})");
            await ConnectWebSocketAsync(connectCts.Token);
            OnConnected?.Invoke();
            _ = Task.Run(ReceiveLoopAsync, connectCts.Token);
        }
        catch (OperationCanceledException) when (connectCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException("WebSocket connection timed out");
        }
        finally
        {
            connectCts.Dispose();
        }
    }

    private async Task ConnectWebSocketAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_model}");
        await _ws.ConnectAsync(uri, ct);
        Log($"✅ Connected to OpenAI Realtime API (state: {_ws.State})");
    }

    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;

        Log("📴 Disconnecting...");
        SignalCallEnded("client_disconnect");

        try
        {
            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
        }
        catch { }
        finally
        {
            CleanupResources();
            OnDisconnected?.Invoke();
        }
    }

    // ===========================================
    // CORE IMPLEMENTATION
    // ===========================================
    private async Task SendAudioToOpenAIAsync(byte[] pcm24k)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var msg = new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm24k) };
            var json = JsonSerializer.Serialize(msg);

            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch (WebSocketException ex) when (IsTransientWebSocketError(ex))
        {
            Log($"⚠️ Transient send error - will reconnect: {ex.Message}");
            _ = AttemptReconnectAsync();
        }
        catch (OperationCanceledException) { /* Expected during disconnect */ }
    }

    private void ProcessAudioDelta(string base64Delta)
    {
        if (string.IsNullOrEmpty(base64Delta)) return;

        try
        {
            var pcm24k = Convert.FromBase64String(base64Delta);
            OnPcm24Audio?.Invoke(pcm24k);

            var samples24k = AudioCodecs.BytesToShorts(pcm24k);

            switch (_outputCodec)
            {
                case OutputCodecMode.Opus:
                    ProcessOpusOutput(samples24k);
                    break;
                case OutputCodecMode.ALaw:
                    ProcessNarrowbandOutput(samples24k, useMuLaw: false);
                    break;
                case OutputCodecMode.MuLaw:
                    ProcessNarrowbandOutput(samples24k, useMuLaw: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio processing error: {ex.Message}");
        }
    }

    private void ProcessNarrowbandOutput(short[] pcm24k, bool useMuLaw)
    {
        var outputLen = pcm24k.Length / 3;
        var pcm8k = _shortBufferPool.Rent(outputLen);

        try
        {
            for (int i = 0; i < outputLen; i++)
            {
                int srcIdx = i * 3;
                pcm8k[i] = (short)((pcm24k[srcIdx] * 2 + pcm24k[srcIdx + 2]) / 3);
            }

            var encoded = useMuLaw
                ? AudioCodecs.MuLawEncode(pcm8k, 0, outputLen)
                : AudioCodecs.ALawEncode(pcm8k, 0, outputLen);

            for (int i = 0; i < encoded.Length; i += 160)
            {
                var frame = _audioBufferPool.Rent(160);
                var len = Math.Min(160, encoded.Length - i);
                Array.Copy(encoded, i, frame, 0, len);

                if (len < 160)
                    Array.Fill(frame, useMuLaw ? (byte)0xFF : (byte)0xD5, len, 160 - len);

                EnqueueFrame(frame);
            }
        }
        finally
        {
            _shortBufferPool.Return(pcm8k);
        }
    }

    private void ProcessOpusOutput(short[] pcm24k)
    {
        var pcm48k = new short[pcm24k.Length * 2];
        for (int i = 0; i < pcm24k.Length; i++)
        {
            pcm48k[i * 2] = pcm24k[i];
            pcm48k[i * 2 + 1] = i < pcm24k.Length - 1
                ? (short)((pcm24k[i] + pcm24k[i + 1]) / 2)
                : pcm24k[i];
        }

        lock (_lock)
        {
            var newBuffer = new short[_opusResampleBuffer.Length + pcm48k.Length];
            Array.Copy(_opusResampleBuffer, newBuffer, _opusResampleBuffer.Length);
            Array.Copy(pcm48k, 0, newBuffer, _opusResampleBuffer.Length, pcm48k.Length);
            _opusResampleBuffer = newBuffer;
        }

        const int opusFrameSize = 960;
        while (_opusResampleBuffer.Length >= opusFrameSize)
        {
            var frame = new short[opusFrameSize];
            Array.Copy(_opusResampleBuffer, 0, frame, 0, opusFrameSize);

            var opusBytes = AudioCodecs.OpusEncode(frame);
            EnqueueFrame(opusBytes);

            var remaining = new short[_opusResampleBuffer.Length - opusFrameSize];
            Array.Copy(_opusResampleBuffer, opusFrameSize, remaining, 0, remaining.Length);
            _opusResampleBuffer = remaining;
        }
    }

    private void EnqueueFrame(byte[] frame)
    {
        while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
            _outboundQueue.TryDequeue(out _);

        _outboundQueue.Enqueue(frame);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var messageBuilder = new StringBuilder();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token);

        Log("🔄 Receive loop started");

        while (IsConnected && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log($"📴 WebSocket closed by server ({result.CloseStatus}: {result.CloseStatusDescription})");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    await ProcessMessageAsync(messageBuilder.ToString());
                    messageBuilder.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex) when (IsTransientWebSocketError(ex))
            {
                Log($"⚠️ Transient WebSocket error - attempting reconnect: {ex.Message}");
                if (await AttemptReconnectAsync()) continue;
                break;
            }
            catch (Exception ex)
            {
                Log($"❌ Receive loop error: {ex.Message}");
                break;
            }
        }

        Log("🔄 Receive loop ended");
        CleanupResources();
        OnDisconnected?.Invoke();
    }

    private bool IsTransientWebSocketError(WebSocketException ex)
    {
        return ex.WebSocketErrorCode switch
        {
            WebSocketError.NotAWebSocket => true,
            WebSocketError.HeaderError => true,
            WebSocketError.Faulted => true,
            _ => false
        };
    }

    private async Task<bool> AttemptReconnectAsync()
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return false;

        var reconnectCts = new CancellationTokenSource(_connectTimeout);
        try
        {
            await Task.Delay(1000, reconnectCts.Token);
            await ConnectWebSocketAsync(reconnectCts.Token);
            await ConfigureSessionAsync();
            Log("✅ Reconnected successfully");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            reconnectCts.Dispose();
        }
    }

    private async Task ProcessMessageAsync(string json)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();

            switch (type)
            {
                case "session.created":
                    await ConfigureSessionAsync();
                    break;

                case "session.updated":
                    if (Interlocked.CompareExchange(ref _greetingSentFlag, 1, 0) == 0)
                        await SendGreetingAsync();
                    break;

                case "response.created":
                    Interlocked.Exchange(ref _responseActiveFlag, 1);
                    OnResponseStarted?.Invoke();
                    break;

                case "response.audio.delta":
                    ProcessAudioDelta(doc.RootElement.GetProperty("delta").GetString() ?? "");
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var delta) && !string.IsNullOrEmpty(delta.GetString()))
                        OnAdaSpeaking?.Invoke(delta.GetString()!);
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var transcriptEl) &&
                        !string.IsNullOrWhiteSpace(transcriptEl.GetString()))
                    {
                        var text = transcriptEl.GetString()!;
                        Log($"💬 Ada: {text}");
                        OnTranscript?.Invoke($"Ada: {text}");

                        if (_booking.Confirmed &&
                            Volatile.Read(ref _postBookingHangupArmedFlag) == 1)
                        {
                            Volatile.Write(ref _postBookingFinalSpeechFlag, 1);
                            StartPostBookingHangupWatchdog();
                        }
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Log("🎤 User speaking...");
                    Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
                    break;

                case "input_audio_buffer.speech_stopped":
                    Log("🎤 User stopped speaking");
                    Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userTextEl) &&
                        !string.IsNullOrWhiteSpace(userTextEl.GetString()))
                    {
                        var raw = userTextEl.GetString()!;
                        var corrected = ApplySttCorrections(raw);
                        Log($"👤 User: {corrected}");
                        OnTranscript?.Invoke($"You: {corrected}");
                    }
                    break;

                case "response.done":
                    Interlocked.Exchange(ref _responseActiveFlag, 0);
                    Volatile.Write(ref _lastAdaFinishedAt, GetUnixTimeMs());
                    Log("✅ Response complete");
                    OnResponseCompleted?.Invoke();
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("message", out var msgEl) &&
                        !string.IsNullOrEmpty(msgEl.GetString()))
                    {
                        var msg = msgEl.GetString()!;
                        if (!msg.Contains("buffer too small", StringComparison.OrdinalIgnoreCase))
                            Log($"❌ OpenAI error: {msg}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Message processing error: {ex.Message}");
        }
    }

    private async Task ConfigureSessionAsync()
    {
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetLocalizedSystemPrompt(),
                voice = _voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.35,
                    prefix_padding_ms = 600,
                    silence_duration_ms = 1200
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        Log($"🎧 VAD: threshold=0.35, prefix=600ms, silence=1200ms");
        await SendJsonToOpenAIAsync(config);
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(150);

        // Guard: ensure no active response
        if (Volatile.Read(ref _responseActiveFlag) == 1)
        {
            Log("⏳ Skipping greeting — response already in progress");
            return;
        }

        var greeting = GetLocalizedGreeting(_detectedLanguage);
        var langName = GetLanguageName(_detectedLanguage);

        await SendJsonToOpenAIAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Greet the caller warmly in {langName}. Say: \"{greeting}\""
            }
        });

        Log($"📢 Triggering greeting in {_detectedLanguage}");
    }

    private async Task SendJsonToOpenAIAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonSerializer.Serialize(obj);
            await _ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
        }
        catch { /* Silent fail */ }
    }

    private async Task HandleToolCallAsync(JsonElement root)
    {
        var now = GetUnixTimeMs();
        if (now - Volatile.Read(ref _lastToolCallAt) < 200) return;
        Volatile.Write(ref _lastToolCallAt, now);

        if (!root.TryGetProperty("name", out var nameEl) ||
            !root.TryGetProperty("call_id", out var callIdEl))
            return;

        var toolName = nameEl.GetString();
        var callId = callIdEl.GetString();
        var args = ParseToolArguments(root);

        Log($"🔧 Tool: {toolName} {JsonSerializer.Serialize(args)}");
        OnToolCall?.Invoke(toolName ?? "", args);

        try
        {
            switch (toolName)
            {
                case "sync_booking_data":
                    await HandleSyncBookingDataAsync(callId!, args);
                    break;

                case "book_taxi":
                    await HandleBookTaxiAsync(callId!, args);
                    break;

                case "end_call":
                    await HandleEndCallAsync(callId!);
                    break;

                default:
                    await SendToolResultAsync(callId!, new { error = $"Unknown tool: {toolName}" });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"❌ Tool handler error: {ex.Message}");
            await SendToolResultAsync(callId!, new { error = ex.Message });
        }
    }

    private async Task HandleSyncBookingDataAsync(string callId, Dictionary<string, object> args)
    {
        lock (_lock)
        {
            if (args.TryGetValue("caller_name", out var nm)) _booking.Name = nm?.ToString();
            if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p?.ToString();
            if (args.TryGetValue("destination", out var d)) _booking.Destination = d?.ToString();
            if (args.TryGetValue("passengers", out var pax))
                _booking.Passengers = pax switch { int i => i, _ => int.TryParse(pax?.ToString(), out var n) ? n : null };
            if (args.TryGetValue("pickup_time", out var t)) _booking.PickupTime = t?.ToString();
            if (args.TryGetValue("last_question_asked", out var q)) _lastQuestionAsked = q?.ToString() ?? "none";
        }

        OnBookingUpdated?.Invoke(_booking);
        await SendToolResultAsync(callId, new { success = true, state = _booking });
    }

    private async Task HandleBookTaxiAsync(string callId, Dictionary<string, object> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

        if (action == "request_quote")
        {
            var (fare, eta, dist) = await FareCalculator.CalculateFareAsync(_booking.Pickup, _booking.Destination);
            lock (_lock)
            {
                _booking.Fare = fare;
                _booking.Eta = eta;
                _awaitingConfirmation = true;
            }

            OnBookingUpdated?.Invoke(_booking);
            Log($"💰 Quote: {fare} ({dist:F1} miles)");

            await SendToolResultAsync(callId, new
            {
                success = true,
                fare,
                eta,
                distance_miles = Math.Round(dist, 1),
                message = $"Your fare is {fare} and your driver will arrive in {eta}. Would you like me to book that?"
            });

            Log("🎯 Awaiting confirmation - echo guard disabled");
        }
        else if (action == "confirmed")
        {
            lock (_lock)
            {
                _booking.Confirmed = true;
                _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                _awaitingConfirmation = false;
                Volatile.Write(ref _postBookingHangupArmedFlag, 1);
            }

            OnBookingUpdated?.Invoke(_booking);
            Log($"✅ Booking confirmed: {_booking.BookingRef}");

            // Send WhatsApp notification (fire and forget)
            _ = SendWhatsAppNotificationAsync(_callerId);

            var safeName = string.IsNullOrWhiteSpace(_booking.Name) ? null : _booking.Name.Trim();
            var message = safeName is null
                ? "Your taxi is booked! Your driver will arrive shortly."
                : $"Thanks, {safeName}. Your taxi is booked! Your driver will arrive shortly.";

            await SendToolResultAsync(callId, new
            {
                success = true,
                booking_ref = _booking.BookingRef,
                message,
                next_action = "Say thank you and goodbye, then call end_call."
            });

            // Inject instruction to end call (with response guard)
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);

                // Wait for any active response to complete
                var waitCount = 0;
                while (Volatile.Read(ref _responseActiveFlag) == 1 && waitCount < 20)
                {
                    await Task.Delay(100);
                    waitCount++;
                }

                if (Volatile.Read(ref _responseActiveFlag) == 1)
                {
                    Log("⏳ Post-booking hangup prompt skipped — AI still responding");
                    return;
                }

                await SendJsonToOpenAIAsync(new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "system",
                        content = new[] { new { type = "input_text", text = "[BOOKING COMPLETE] Say goodbye and call end_call NOW." } }
                    }
                });
                await SendJsonToOpenAIAsync(new { type = "response.create" });
            });
        }
    }

    private async Task HandleEndCallAsync(string callId)
    {
        Log("📞 End call requested");
        await SendToolResultAsync(callId, new { success = true });
        SignalCallEnded("end_call_tool");
    }

    private async Task SendToolResultAsync(string callId, object result)
    {
        await SendJsonToOpenAIAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(result)
            }
        });

        // Guard: only create response if no response is currently active
        if (Volatile.Read(ref _responseActiveFlag) == 1)
        {
            Log("⏳ Skipping response.create — AI response already in progress");
            return;
        }

        await QueueResponseCreateAsync();
    }

    private async Task QueueResponseCreateAsync(int debounceMs = 40)
    {
        if (Interlocked.CompareExchange(ref _responseCreateQueuedFlag, 1, 0) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceMs);
                if (IsConnected && Volatile.Read(ref _responseActiveFlag) == 0)
                    await SendJsonToOpenAIAsync(new { type = "response.create" });
            }
            finally
            {
                Interlocked.Exchange(ref _responseCreateQueuedFlag, 0);
            }
        });
    }

    private void StartPostBookingHangupWatchdog()
    {
        if (Volatile.Read(ref _disposedFlag) != 0 ||
            Volatile.Read(ref _callEndSignaledFlag) != 0 ||
            Volatile.Read(ref _postBookingHangupArmedFlag) == 0 ||
            Volatile.Read(ref _postBookingFinalSpeechFlag) == 0)
            return;

        _ = Task.Run(async () =>
        {
            const int silenceTimeoutMs = 10_000;
            const int pollIntervalMs = 250;

            await Task.Delay(1500);

            while (IsConnected && Volatile.Read(ref _callEndSignaledFlag) == 0)
            {
                var lastActivity = Math.Max(
                    Volatile.Read(ref _lastAdaFinishedAt),
                    Volatile.Read(ref _lastUserSpeechAt));

                if (GetUnixTimeMs() - lastActivity >= silenceTimeoutMs)
                {
                    Log($"⏱️ Post-booking silence timeout ({silenceTimeoutMs}ms) - ending call");
                    SignalCallEnded("post_booking_silence_timeout");
                    return;
                }

                await Task.Delay(pollIntervalMs);
            }
        });
    }

    // ===========================================
    // AUDIO RESAMPLING (HIGH-QUALITY)
    // ===========================================
    private byte[] Resample8kTo24k(short[] pcm8k)
    {
        var outputLen = pcm8k.Length * 3;
        var pcm24k = new short[outputLen];

        for (int i = 0; i < pcm8k.Length; i++)
        {
            pcm24k[i * 3] = pcm8k[i];
            pcm24k[i * 3 + 1] = pcm8k[i];
            pcm24k[i * 3 + 2] = pcm8k[i];
        }

        return AudioCodecs.ShortsToBytes(pcm24k);
    }

    // ===========================================
    // WHATSAPP NOTIFICATION
    // ===========================================
    private async Task SendWhatsAppNotificationAsync(string? phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
        {
            Log("⚠️ WhatsApp notification skipped - no phone number");
            return;
        }

        try
        {
            var (success, message) = await WhatsAppNotifier.SendAsync(phoneNumber);
            if (success)
                Log($"✅ {message}");
            else
                Log($"⚠️ {message}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ WhatsApp notification error: {ex.Message}");
        }
    }

    // ===========================================
    // UTILITIES
    // ===========================================
    private void ResetCallState(string? callerId)
    {
        ClearPendingFrames();
        _opusResampleBuffer = Array.Empty<short>();

        _callerId = callerId ?? "";
        _detectedLanguage = DetectLanguageFromPhone(callerId);
        _booking.Reset();
        _lastQuestionAsked = "name";
        _awaitingConfirmation = false;

        Interlocked.Exchange(ref _greetingSentFlag, 0);
        Interlocked.Exchange(ref _responseActiveFlag, 0);
        Interlocked.Exchange(ref _postBookingHangupArmedFlag, 0);
        Interlocked.Exchange(ref _postBookingFinalSpeechFlag, 0);
        Interlocked.Exchange(ref _callEndSignaledFlag, 0);
        Interlocked.Exchange(ref _responseCreateQueuedFlag, 0);

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.CompareExchange(ref _callEndSignaledFlag, 1, 0) == 1) return;
        Log($"📴 Call ended: {reason}");
        OnCallEnded?.Invoke();
    }

    private void CleanupResources()
    {
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }

    private static string DetectLanguageFromPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";

        var clean = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

        if (clean.StartsWith("06") && clean.Length == 10 && clean.All(char.IsDigit)) return "nl";
        if (clean.StartsWith("0") && !clean.StartsWith("00") && clean.Length == 10) return "nl";

        if (clean.StartsWith("+")) clean = clean[1..];
        else if (clean.StartsWith("00")) clean = clean[2..];

        if (clean.Length >= 2)
        {
            var code = clean[..2];
            return code switch
            {
                "31" or "32" => "nl",
                "33" => "fr",
                "41" or "43" or "49" => "de",
                "44" => "en",
                _ => "en"
            };
        }

        return "en";
    }

    private static string ApplySttCorrections(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return transcript;

        var trimmed = transcript.Trim();
        return trimmed switch
        {
            "52 I ain't David" => "52A David Road",
            "52 ain't David" => "52A David Road",
            "52 a David" => "52A David Road",
            "ASAP" => "now",
            "as soon as possible" => "now",
            "right now" => "now",
            "for now" => "now",
            "yeah please" => "yes please",
            "yep" => "yes",
            "yup" => "yes",
            "yeah" => "yes",
            "that's right" => "yes",
            "correct" => "yes",
            "go ahead" => "yes",
            "book it" => "yes",
            _ => trimmed
        };
    }

    private string GetLocalizedSystemPrompt()
    {
        var lang = GetLanguageName(_detectedLanguage);
        return $@"You are Ada, a professional taxi booking assistant. Speak in {lang}.

# BOOKING FLOW
1. Greet warmly → Ask for their NAME
2. Ask for PICKUP address
3. Ask for DESTINATION
4. Ask for NUMBER OF PASSENGERS
5. Ask for PICKUP TIME
6. Summarize → call book_taxi(action='request_quote')
7. Tell fare/ETA → Ask for confirmation
8. On 'yes' → book_taxi(action='confirmed') → Thank user by name → end_call

# TOOLS
- sync_booking_data: Save each piece of info (including name)
- book_taxi: Request quote or confirm
- end_call: Hang up after goodbye

# RULES
- ONE question at a time
- Be brief (under 20 words)
- Currency: British pounds (£)
- Use the caller's name SPARINGLY - only at greeting, final confirmation, and goodbye
- Do NOT repeat their name after every response
- Always end call after booking confirmation";
    }

    private static string GetDefaultSystemPrompt() => @"You are Ada, a taxi booking assistant.

# BOOKING FLOW
1. Greet warmly → Ask for their NAME
2. Ask for PICKUP address
3. Ask for DESTINATION
4. Ask for NUMBER OF PASSENGERS
5. Ask for PICKUP TIME
6. Get quote with book_taxi(action='request_quote')
7. Confirm with user
8. On yes → book_taxi(action='confirmed') → Thank user by name → end_call

# RULES
- ONE question at a time
- Be brief
- Currency: £ (GBP)
- Use the caller's name SPARINGLY - only at greeting, final confirmation, and goodbye
- Do NOT repeat their name after every response";

    private static string GetLanguageName(string code) => code switch
    {
        "nl" => "Dutch",
        "fr" => "French",
        "de" => "German",
        _ => "English"
    };

    private static string GetLocalizedGreeting(string lang) => lang switch
    {
        "nl" => "Hallo, en welkom bij de Taxibot demo. Ik ben Ada, uw taxi boekingsassistent. Wat is uw naam?",
        "fr" => "Bonjour et bienvenue à la démo Taxibot. Je suis Ada. Comment vous appelez-vous?",
        "de" => "Hallo und willkommen zur Taxibot-Demo. Ich bin Ada. Wie heißen Sie?",
        _ => "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. What's your name?"
    };

    private static long GetUnixTimeMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private Dictionary<string, object> ParseToolArguments(JsonElement root)
    {
        var args = new Dictionary<string, object>();
        if (!root.TryGetProperty("arguments", out var argsEl) || string.IsNullOrEmpty(argsEl.GetString()))
            return args;

        try
        {
            using var doc = JsonDocument.Parse(argsEl.GetString()!);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString() ?? ""
                };
            }
        }
        catch { }

        return args;
    }

    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "Save booking data as it's collected. Call after each piece of info.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["caller_name"] = new { type = "string", description = "The caller's name" },
                    ["pickup"] = new { type = "string", description = "Pickup address" },
                    ["destination"] = new { type = "string", description = "Destination address" },
                    ["passengers"] = new { type = "integer", description = "Number of passengers" },
                    ["pickup_time"] = new { type = "string", description = "Pickup time" },
                    ["last_question_asked"] = new
                    {
                        type = "string",
                        @enum = new[] { "name", "pickup", "destination", "passengers", "time", "confirmation", "none" },
                        description = "The last question asked"
                    }
                }
            }
        },
        new
        {
            type = "function",
            name = "book_taxi",
            description = "Request a fare quote or confirm booking",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["action"] = new
                    {
                        type = "string",
                        @enum = new[] { "request_quote", "confirmed" },
                        description = "Action to perform"
                    }
                },
                required = new[] { "action" }
            }
        },
        new
        {
            type = "function",
            name = "end_call",
            description = "End the call after saying goodbye. Call this after booking is confirmed and you have thanked the caller.",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["reason"] = new
                    {
                        type = "string",
                        description = "Reason for ending (booking_complete, caller_request, etc.)"
                    }
                },
                required = new[] { "reason" }
            }
        }
    };

    // ===========================================
    // DISPOSAL
    // ===========================================
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposedFlag, 1, 0) == 1) return;

        Log("🔌 Disposing client...");
        SignalCallEnded("disposed");
        _cts?.Cancel();

        CleanupResources();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }

    private void EnsureNotDisposed()
    {
        if (Volatile.Read(ref _disposedFlag) != 0)
            throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));
    }

    private void Log(string message)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;

        var logLine = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.WriteLine($"[OpenAI] {logLine}");
        OnLog?.Invoke(logLine);
    }
}

/// <summary>
/// Mutable booking state with Reset() for call reuse.
/// </summary>
public sealed class BookingState
{
    public string? Name { get; set; }
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int? Passengers { get; set; }
    public string? PickupTime { get; set; }
    public string? Fare { get; set; }
    public string? Eta { get; set; }
    public bool Confirmed { get; set; }
    public string? BookingRef { get; set; }

    public void Reset()
    {
        Name = null;
        Pickup = null;
        Destination = null;
        Passengers = null;
        PickupTime = null;
        Fare = null;
        Eta = null;
        Confirmed = false;
        BookingRef = null;
    }
}
