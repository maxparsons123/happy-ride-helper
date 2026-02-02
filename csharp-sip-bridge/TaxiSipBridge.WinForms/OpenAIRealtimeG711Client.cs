using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API client using 24kHz PCM16 internally, G.711 on SIP side.
/// Uses fast linear interpolation resampling for low-latency VAD triggering.
/// 
/// v1.6: Switched from native G.711 to 24kHz PCM for better VAD responsiveness
/// - Upsample 8kHz→24kHz on ingress (linear interpolation)
/// - Downsample 24kHz→8kHz on egress (simple decimation)
/// - Full flow from OpenAIRealtimeClient preserved
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "1.6";

    // =========================
    // G.711 CODEC SELECTION (SIP side)
    // =========================
    public enum G711Codec { MuLaw, ALaw }
    
    private readonly G711Codec _codec;
    private readonly byte _silenceByte;

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // Audio format: 24kHz PCM16 for OpenAI (better VAD), G.711 8kHz for SIP
    private const int OPENAI_SAMPLE_RATE = 24000;
    private const int SIP_SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int SIP_FRAME_SIZE_BYTES = SIP_SAMPLE_RATE * FRAME_MS / 1000; // 160 bytes (G.711)
    private const int PCM24K_FRAME_SAMPLES = OPENAI_SAMPLE_RATE * FRAME_MS / 1000; // 480 samples

    // =========================
    // THREAD-SAFE STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _responseQueued;
    private int _sessionCreated;
    private int _sessionUpdated;
    private int _greetingSent;
    private int _ignoreUserAudio;
    private int _deferredResponsePending;
    private int _noReplyWatchdogId;
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;
    private long _responseCreatedAt; // For transcript guard

    // Response ID guards
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // =========================
    // BOOKING STATE
    // =========================
    private readonly BookingState _booking = new();
    private string _callerId = "";
    private string _detectedLanguage = "en";
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
    // AUDIO OUTPUT
    // =========================
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500;

    // Audio accumulator for egress (G.711 bytes after downsampling)
    private readonly byte[] _audioAccum = new byte[SIP_FRAME_SIZE_BYTES * 10];
    private int _audioAccumOffset;
    private readonly object _audioAccumLock = new();

    // =========================
    // STATS
    // =========================
    private long _audioFramesSent;
    private long _audioChunksReceived;

    // =========================
    // HELPERS
    // =========================
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
    public event Action<string>? OnAdaSpeaking;
    public event Action? OnCallEnded;
    public event Action<BookingState>? OnBookingUpdated;

    // =========================
    // PROPERTIES
    // =========================
    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 &&
        _ws?.State == WebSocketState.Open;

    public int PendingFrameCount => _outboundQueue.Count;

    // =========================
    // CONSTRUCTOR
    // =========================
    public OpenAIRealtimeG711Client(
        string apiKey,
        string model = "gpt-4o-realtime-preview-2025-06-03",
        string voice = "shimmer",
        G711Codec codec = G711Codec.MuLaw)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _codec = codec;
        _silenceByte = codec == G711Codec.MuLaw ? (byte)0xFF : (byte)0xD5;
    }

    // =========================
    // IAudioAIClient IMPLEMENTATION
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

        Log($"📞 Connecting to OpenAI Realtime (24kHz PCM, SIP: G.711 {_codec})...");
        await _ws.ConnectAsync(
            new Uri($"wss://api.openai.com/v1/realtime?model={_model}"),
            linked.Token
        ).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        _keepaliveTask = Task.Run(KeepaliveLoopAsync);

        // Wait for session.created
        Log("⏳ Waiting for session.created...");
        for (int i = 0; i < 100 && Volatile.Read(ref _sessionCreated) == 0; i++)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);

        if (Volatile.Read(ref _sessionCreated) == 0)
            throw new TimeoutException("Timeout waiting for session.created");

        Log("✅ Session created, configuring...");

        // Wait for session.updated
        Log("⏳ Waiting for session.updated...");
        for (int i = 0; i < 100 && Volatile.Read(ref _sessionUpdated) == 0; i++)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);

        if (Volatile.Read(ref _sessionUpdated) == 0)
            throw new TimeoutException("Timeout waiting for session.updated");

        Log("✅ Session configured");

        // Wait for greeting
        for (int i = 0; i < 40 && Volatile.Read(ref _greetingSent) == 0; i++)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);

        OnConnected?.Invoke();
        Log($"✅ Connected to OpenAI Realtime (24kHz PCM, voice={_voice})");
    }

    /// <summary>
    /// Send G.711 audio frame to OpenAI (decodes to PCM, upsamples to 24kHz).
    /// Uses fast linear interpolation for 8kHz→24kHz upsampling.
    /// </summary>
    public async Task SendMuLawAsync(byte[] g711Data)
    {
        if (!IsConnected || g711Data == null || g711Data.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) != 0) return;

        // Echo guard: skip audio right after AI speaks (but bypass if awaiting confirmation)
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 200)
            return;

        try
        {
            // 1. Decode G.711 to PCM16 @ 8kHz
            short[] pcm8k = _codec == G711Codec.ALaw
                ? AudioCodecs.ALawDecode(g711Data)
                : AudioCodecs.MuLawDecode(g711Data);

            // 2. Upsample 8kHz → 24kHz using linear interpolation
            short[] pcm24k = Upsample8kTo24k(pcm8k);

            // 3. Convert to bytes and send to OpenAI
            var pcm24kBytes = new byte[pcm24k.Length * 2];
            Buffer.BlockCopy(pcm24k, 0, pcm24kBytes, 0, pcm24kBytes.Length);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(pcm24kBytes)
            });

            await SendBytesAsync(bytes).ConfigureAwait(false);
            Volatile.Write(ref _lastUserSpeechAt, NowMs());

            var count = Interlocked.Increment(ref _audioFramesSent);
            if (count % 50 == 0)
                Log($"📤 Sent {count} audio frames to OpenAI (24kHz PCM)");
        }
        catch (Exception ex)
        {
            Log($"⚠️ SendMuLawAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Upsample 8kHz PCM to 24kHz PCM (3x) using linear interpolation.
    /// Fast and clean for VAD sensitivity.
    /// </summary>
    private static short[] Upsample8kTo24k(short[] input)
    {
        if (input == null || input.Length == 0) return Array.Empty<short>();

        var output = new short[input.Length * 3];

        for (int i = 0; i < input.Length - 1; i++)
        {
            int outIdx = i * 3;
            short current = input[i];
            short next = input[i + 1];

            output[outIdx] = current;
            // Linear interpolation for 2 intermediate points
            output[outIdx + 1] = (short)(current + (next - current) / 3);
            output[outIdx + 2] = (short)(current + (next - current) * 2 / 3);
        }

        // Handle last sample
        if (input.Length > 0)
        {
            int lastIdx = (input.Length - 1) * 3;
            output[lastIdx] = input[input.Length - 1];
            output[lastIdx + 1] = input[input.Length - 1];
            output[lastIdx + 2] = input[input.Length - 1];
        }

        return output;
    }

    /// <summary>
    /// Downsample 24kHz PCM to 8kHz PCM (1/3x) by taking every 3rd sample.
    /// Simple decimation for minimum latency.
    /// </summary>
    private static short[] Downsample24kTo8k(short[] input)
    {
        if (input == null || input.Length == 0) return Array.Empty<short>();

        int outputLength = input.Length / 3;
        var output = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            output[i] = input[i * 3];
        }

        return output;
    }

    /// <summary>
    /// Send PCM16 audio. Converts to G.711 and upsamples to 24kHz before sending.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 8000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;

        var samples = AudioCodecs.BytesToShorts(pcmData);
        
        // Resample to 8kHz if needed, then encode to G.711
        if (sampleRate != SIP_SAMPLE_RATE)
            samples = AudioCodecs.Resample(samples, sampleRate, SIP_SAMPLE_RATE);

        byte[] encoded = _codec == G711Codec.ALaw
            ? AudioCodecs.ALawEncode(samples)
            : AudioCodecs.MuLawEncode(samples);

        await SendMuLawAsync(encoded).ConfigureAwait(false);
    }

    public byte[]? GetNextMuLawFrame() => _outboundQueue.TryDequeue(out var f) ? f : null;

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

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
    // KEEPALIVE LOOP
    // =========================
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

    // =========================
    // SESSION CONFIGURATION
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
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.35,
                    prefix_padding_ms = 400,      // Reduced from 600 for faster turn detection
                    silence_duration_ms = 1000    // Reduced from 1200 (conservative but faster)
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        Log($"🎧 Configuring session: format=pcm16@24kHz, voice={_voice}, server_vad");
        await SendJsonAsync(config).ConfigureAwait(false);
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(100).ConfigureAwait(false);

        // Force-clear spurious state for first response
        var respActive = Volatile.Read(ref _responseActive);
        var respQueued = Volatile.Read(ref _responseQueued);
        
        if (respActive == 1 || respQueued == 1)
            Log($"⚠️ Greeting: Force-clearing spurious state (active={respActive}, queued={respQueued})");
        
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        _activeResponseId = null;

        var greeting = GetLocalizedGreeting(_detectedLanguage);

        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Greet the caller warmly in {GetLanguageName(_detectedLanguage)}. Say: \"{greeting}\""
            }
        }).ConfigureAwait(false);

        Log("📢 Greeting sent");
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
                    Interlocked.Exchange(ref _sessionCreated, 1);
                    Log("📋 session.created received");
                    _ = Task.Run(async () =>
                    {
                        try { await ConfigureSessionAsync().ConfigureAwait(false); }
                        catch (Exception ex) { Log($"⚠️ ConfigureSession error: {ex.Message}"); }
                    });
                    break;

                case "session.updated":
                    Interlocked.Exchange(ref _sessionUpdated, 1);
                    Log("📋 session.updated received");
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await SendGreetingAsync().ConfigureAwait(false); }
                            catch (Exception ex) { Log($"⚠️ Greeting error: {ex.Message}"); }
                        });
                    }
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

                    // Start 8s no-reply watchdog
                    var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(8000).ConfigureAwait(false);

                        if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
                        if (Volatile.Read(ref _responseActive) == 1) return;

                        Log("⏰ No-reply watchdog triggered - prompting user");

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

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                    {
                        var audioBase64 = deltaEl.GetString();
                        if (!string.IsNullOrEmpty(audioBase64))
                            ProcessAudioDelta(audioBase64);
                    }
                    break;

                case "response.audio.done":
                    FlushAudioAccumulator(padWithSilence: true);
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var transcriptDeltaEl))
                    {
                        var text = transcriptDeltaEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                            OnAdaSpeaking?.Invoke(text);
                    }
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

                                    Log("⏰ Goodbye watchdog triggered - forcing end_call");
                                    Interlocked.Exchange(ref _ignoreUserAudio, 1);
                                    SignalCallEnded("goodbye_watchdog");
                                });
                            }
                        }
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userTranscriptEl))
                    {
                        var text = userTranscriptEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            text = ApplySttCorrections(text);

                            // Transcript guard: ignore stale transcripts
                            var msSinceResponseCreated = NowMs() - Volatile.Read(ref _responseCreatedAt);
                            if (msSinceResponseCreated < 900 && Volatile.Read(ref _responseActive) == 1)
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
                    Interlocked.Increment(ref _noReplyWatchdogId);
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement).ConfigureAwait(false);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var errorEl))
                    {
                        var message = errorEl.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString()
                            : "Unknown error";
                        if (!string.IsNullOrEmpty(message) &&
                            !message.Contains("buffer too small", StringComparison.OrdinalIgnoreCase))
                            Log($"❌ OpenAI error: {message}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ ProcessMessageAsync error: {ex.Message}");
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
                if (args.TryGetValue("caller_name", out var nm)) _booking.Name = nm?.ToString();
                if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p?.ToString();
                if (args.TryGetValue("destination", out var d)) _booking.Destination = d?.ToString();
                if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var pn)) _booking.Passengers = pn;
                if (args.TryGetValue("pickup_time", out var pt)) _booking.PickupTime = pt?.ToString();

                OnBookingUpdated?.Invoke(_booking);
                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);
                // Don't call QueueResponseCreateAsync - OpenAI continues automatically after tool result
                break;

            case "book_taxi":
            {
                var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

                if (action == "request_quote")
                {
                    var (fare, eta, _) = await FareCalculator.CalculateFareAsync(_booking.Pickup, _booking.Destination)
                                                             .ConfigureAwait(false);

                    _booking.Fare = fare;
                    _booking.Eta = eta;
                    _awaitingConfirmation = true;

                    OnBookingUpdated?.Invoke(_booking);
                    Log($"💰 Quote: {fare}");

                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        fare,
                        eta,
                        message = $"Fare is {fare}, driver arrives in {eta}. Book it?"
                    }).ConfigureAwait(false);

                    await QueueResponseCreateAsync().ConfigureAwait(false);
                }
                else if (action == "confirmed")
                {
                    _booking.Confirmed = true;
                    _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    _awaitingConfirmation = false;

                    OnBookingUpdated?.Invoke(_booking);
                    Log($"✅ Booked: {_booking.BookingRef} (caller={_callerId})");

                    // WhatsApp notification
                    _ = Task.Run(async () =>
                    {
                        try { await SendWhatsAppNotificationAsync(_callerId); }
                        catch (Exception ex) { Log($"❌ WhatsApp task error: {ex.Message}"); }
                    });

                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        booking_ref = _booking.BookingRef,
                        message = string.IsNullOrWhiteSpace(_booking.Name)
                            ? "Your taxi is booked!"
                            : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
                    }).ConfigureAwait(false);

                    await QueueResponseCreateAsync().ConfigureAwait(false);

                    // 15s grace period then force end
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
                                        text = "[TIMEOUT] Say 'Thank you for using the Voice Taxibot system. Goodbye!' and call end_call NOW."
                                    }
                                }
                            }
                        }).ConfigureAwait(false);

                        await QueueResponseCreateAsync(delayMs: 20, waitForCurrentResponse: false).ConfigureAwait(false);
                    });
                }
                break;
            }

            case "find_local_events":
            {
                var category = args.TryGetValue("category", out var cat) ? cat?.ToString() ?? "all" : "all";
                var near = args.TryGetValue("near", out var ne) ? ne?.ToString() : null;
                var date = args.TryGetValue("date", out var dt) ? dt?.ToString() ?? "this weekend" : "this weekend";

                Log($"🎭 Events lookup: {category} near {near ?? "unknown"} on {date}");

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

                await QueueResponseCreateAsync().ConfigureAwait(false);
                break;
            }

            case "end_call":
                Log("📞 End call requested - ignoring further user audio...");
                Interlocked.Exchange(ref _ignoreUserAudio, 1);

                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);

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
                Log($"⚠️ Unknown tool: {name}");
                await SendToolResultAsync(callId, new { error = "Unknown tool" }).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendToolResultAsync(string callId, object output)
    {
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(output)
            }
        }).ConfigureAwait(false);
    }

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
               NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 150; // Reduced from 300ms for faster responses
    }

    private async Task QueueResponseCreateAsync(int delayMs = 20, bool waitForCurrentResponse = true)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1)
            return;

        try
        {
            // Wait for any active response to complete (up to 3s) - reduced from 5s for faster turnaround
            if (waitForCurrentResponse)
            {
                for (int i = 0; i < 60 && Volatile.Read(ref _responseActive) == 1; i++)
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
    // AUDIO PROCESSING (24kHz PCM → 8kHz G.711)
    // =========================

    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            // Receive 24kHz PCM16 from OpenAI
            var pcm24kBytes = Convert.FromBase64String(base64);
            
            // Convert bytes to shorts
            var pcm24k = new short[pcm24kBytes.Length / 2];
            Buffer.BlockCopy(pcm24kBytes, 0, pcm24k, 0, pcm24kBytes.Length);
            
            // Downsample 24kHz → 8kHz
            var pcm8k = Downsample24kTo8k(pcm24k);
            
            // Encode to G.711 for SIP
            var g711Bytes = _codec == G711Codec.ALaw
                ? AudioCodecs.ALawEncode(pcm8k)
                : AudioCodecs.MuLawEncode(pcm8k);
            
            AppendAndEnqueueG711(g711Bytes);

            var chunks = Interlocked.Increment(ref _audioChunksReceived);
            if (chunks % 10 == 0)
                Log($"📢 Received {chunks} audio chunks (24kHz→8kHz {_codec}), queue={_outboundQueue.Count}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio processing error: {ex.Message}");
        }
    }

    private void AppendAndEnqueueG711(byte[] bytes)
    {
        if (bytes.Length == 0) return;

        lock (_audioAccumLock)
        {
            int sourceOffset = 0;

            while (sourceOffset < bytes.Length)
            {
                int needed = SIP_FRAME_SIZE_BYTES - _audioAccumOffset;
                int remainingInRaw = bytes.Length - sourceOffset;
                int toCopy = Math.Min(needed, remainingInRaw);

                Buffer.BlockCopy(bytes, sourceOffset, _audioAccum, _audioAccumOffset, toCopy);

                _audioAccumOffset += toCopy;
                sourceOffset += toCopy;

                if (_audioAccumOffset == SIP_FRAME_SIZE_BYTES)
                {
                    if (_outboundQueue.Count < MAX_QUEUE_FRAMES)
                    {
                        var frame = new byte[SIP_FRAME_SIZE_BYTES];
                        Buffer.BlockCopy(_audioAccum, 0, frame, 0, SIP_FRAME_SIZE_BYTES);
                        _outboundQueue.Enqueue(frame);
                    }
                    _audioAccumOffset = 0;
                }
            }
        }
    }

    private void FlushAudioAccumulator(bool padWithSilence)
    {
        lock (_audioAccumLock)
        {
            if (_audioAccumOffset <= 0) return;

            if (padWithSilence)
            {
                var frame = new byte[SIP_FRAME_SIZE_BYTES];
                Buffer.BlockCopy(_audioAccum, 0, frame, 0, _audioAccumOffset);
                
                for (int i = _audioAccumOffset; i < SIP_FRAME_SIZE_BYTES; i++)
                    frame[i] = _silenceByte;

                if (_outboundQueue.Count < MAX_QUEUE_FRAMES)
                    _outboundQueue.Enqueue(frame);
            }

            _audioAccumOffset = 0;
        }
    }

    // =========================
    // HELPERS
    // =========================

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

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 0)
        {
            Log($"📴 Call ended: {reason}");
            OnCallEnded?.Invoke();
        }
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

    private static string FormatPhoneForWhatsApp(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits.Substring(2);
        if (digits.StartsWith("0")) digits = "44" + digits.Substring(1);
        return digits;
    }

    private void ResetCallState(string? caller)
    {
        ClearPendingFrames();

        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);
        _booking.Reset();
        _awaitingConfirmation = false;

        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _sessionCreated, 0);
        Interlocked.Exchange(ref _sessionUpdated, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastCompletedResponseId = null;

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
        Volatile.Write(ref _responseCreatedAt, 0);
        Volatile.Write(ref _audioFramesSent, 0);
        Volatile.Write(ref _audioChunksReceived, 0);

        lock (_audioAccumLock) _audioAccumOffset = 0;
    }

    private string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";

        var norm = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

        if (norm.StartsWith("06") && norm.Length == 10) return "nl";
        if (norm.StartsWith("0") && !norm.StartsWith("00") && norm.Length == 10) return "nl";

        if (norm.StartsWith("+")) norm = norm.Substring(1);
        else if (norm.StartsWith("00") && norm.Length > 4) norm = norm.Substring(2);

        if (norm.Length >= 2)
        {
            var code = norm.Substring(0, 2);
            return code switch
            {
                "31" => "nl",
                "32" => "nl",
                "33" => "fr",
                "34" => "es",
                "39" => "it",
                "48" => "pl",
                "49" => "de",
                _ => "en"
            };
        }

        return "en";
    }

    private static string ApplySttCorrections(string text)
    {
        var t = (text ?? "").Trim().TrimEnd('.', '!', '?', ',', ';', ':');

        // Exact match corrections
        if (SttCorrections.TryGetValue(t, out var exact))
            return exact;

        // Partial corrections
        foreach (var (bad, good) in PartialSttCorrections)
        {
            if (t.Contains(bad, StringComparison.OrdinalIgnoreCase))
                t = t.Replace(bad, good, StringComparison.OrdinalIgnoreCase);
        }

        return t;
    }

    private static readonly Dictionary<string, string> SttCorrections = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Aren't out", "Bernard" },
        { "52 I ain't dead bro", "52A David Road" },
        { "52 I ain't David", "52A David Road" },
        { "52 ain't David", "52A David Road" },
        { "52 a David", "52A David Road" },
        { "for now", "now" },
        { "right now", "now" },
        { "as soon as possible", "now" },
        { "ASAP", "now" },
        { "yeah please", "yes please" },
        { "yep", "yes" },
        { "yup", "yes" },
        { "yeah", "yes" },
        { "that's right", "yes" },
        { "correct", "yes" },
        { "go ahead", "yes" },
        { "book it", "yes" },
    };

    private static readonly (string Bad, string Good)[] PartialSttCorrections =
    {
        ("Waters Street", "Russell Street"),
        ("Water Street", "Russell Street"),
        ("Walters Street", "Russell Street"),
        ("Russell Sweeney", "Russell Street"),
        ("Servant, Russell", "7 Russell"),
        ("Servant Russell", "7 Russell"),
        ("Dave Redroth", "David Road"),
    };

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

    // =========================
    // SYSTEM PROMPT & TOOLS
    // =========================

    private string GetSystemPrompt() => $@"You are Ada, a taxi booking assistant.

LANGUAGE: Start in {GetLanguageName(_detectedLanguage)} based on caller's phone number. If they speak a different language, switch to that language.

FLOW: Greet → NAME → PICKUP → DESTINATION → PASSENGERS → TIME → CONFIRM details once → book_taxi(request_quote) → Tell fare → Ask to confirm → book_taxi(confirmed) → Give reference ID → 'Anything else?' → If no: 'Thank you for using Voice Taxibot. Goodbye!' → end_call

DATA SYNC (CRITICAL): After EVERY user message that provides booking info, call sync_booking_data immediately.

RULES: One question at a time. Under 20 words per response. Use £. Only call end_call after user says no to 'anything else'.";

    private static string GetLanguageName(string c) => c switch
    {
        "nl" => "Dutch",
        "fr" => "French",
        "de" => "German",
        "es" => "Spanish",
        "it" => "Italian",
        "pl" => "Polish",
        "pt" => "Portuguese",
        _ => "English"
    };

    private static string GetLocalizedGreeting(string lang) => lang switch
    {
        "nl" => "Hallo, welkom bij Voice Taxibot. Mag ik uw naam?",
        "fr" => "Bonjour, bienvenue chez Voice Taxibot. Puis-je avoir votre nom?",
        "de" => "Hallo, willkommen bei Voice Taxibot. Darf ich Ihren Namen erfahren?",
        "es" => "Hola, bienvenido a Voice Taxibot. ¿Puedo saber su nombre?",
        _ => "Hello, welcome to Voice Taxibot. May I have your name?"
    };

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
                    ["action"] = new { type = "string", @enum = new[] { "request_quote", "confirmed" } }
                },
                required = new[] { "action" }
            }
        },
        new
        {
            type = "function",
            name = "find_local_events",
            description = "Search for local events",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["category"] = new { type = "string" },
                    ["near"] = new { type = "string" },
                    ["date"] = new { type = "string" }
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
    // JSON SEND UTILITIES
    // =========================

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await SendBytesAsync(bytes).ConfigureAwait(false);
    }

    private async Task SendBytesAsync(byte[] bytes)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                _cts?.Token ?? CancellationToken.None
            ).ConfigureAwait(false);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    private void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"{ts} [G711] {message}");
        OnLog?.Invoke(message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        SignalCallEnded("disposed");
        Interlocked.Increment(ref _noReplyWatchdogId);

        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendMutex.Dispose();
    }
}
