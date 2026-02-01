using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API client using G.711 @ 8kHz (native OpenAI support).
/// Supports both μ-law (PCMU) and A-law (PCMA) codecs.
/// 
/// Key Benefits:
/// - No resampling overhead (8kHz throughout)
/// - Better audio quality (no lossy resampling)
/// - Lower latency and CPU usage
/// - Direct passthrough from SIP → OpenAI → SIP
/// 
/// Audio Flow:
/// - Input: G.711 (μ-law or A-law) → OpenAI
/// - Output: OpenAI → G.711 (μ-law or A-law)
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "1.2"; // Added A-law support

    // =========================
    // G.711 CODEC SELECTION
    // =========================
    public enum G711Codec { MuLaw, ALaw }
    
    private readonly G711Codec _codec;
    private readonly byte _silenceByte;
    private readonly string _openAiFormatString;

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // Audio format: G.711 @ 8kHz
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE_BYTES = SAMPLE_RATE * FRAME_MS / 1000; // 160 samples = 160 bytes
    private const int PCM_FRAME_SIZE_BYTES = FRAME_SIZE_BYTES * 2; // 320 bytes (PCM16)

    // =========================
    // THREAD-SAFE STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _sessionCreated;
    private int _sessionUpdated;
    private int _greetingSent;
    private int _ignoreUserAudio;

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
    private readonly SemaphoreSlim _sendMutex = new(1, 1);

    // =========================
    // AUDIO OUTPUT
    // =========================
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 500;

    // Audio delta accumulator (OpenAI deltas are not guaranteed to be 160-byte aligned).
    // We must accumulate across deltas and only emit full 20ms frames to avoid injecting padding/silence.
    private byte[] _audioAccum = new byte[FRAME_SIZE_BYTES * 20];
    private int _audioAccumOffset;
    private int _audioAccumLength;

    // =========================
    // STATS
    // =========================
    private long _audioFramesSent;
    private long _audioChunksReceived;
    private long _lastToolCallAt;

    // =========================
    // NO-REPLY WATCHDOG
    // =========================
    private Timer? _noReplyWatchdog;
    private long _lastResponseDoneAt;
    private long _lastAudioSentAt; // Track when we last sent audio to OpenAI
    private int _userSpeaking;
    private int _noReplyRepromptCount;
    private const int NO_REPLY_TIMEOUT_MS = 8000; // 8 seconds before re-prompting
    private const int MAX_NO_REPLY_REPROMPTS = 2; // Maximum re-prompt attempts

    // =========================
    // EVENTS
    // =========================
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnPcm24Audio; // Not used in G.711 mode, but required by interface
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
        
        // Set codec-specific constants
        // μ-law silence = 0xFF, A-law silence = 0xD5
        _silenceByte = codec == G711Codec.MuLaw ? (byte)0xFF : (byte)0xD5;
        _openAiFormatString = codec == G711Codec.MuLaw ? "g711_ulaw" : "g711_alaw";
    }

    // =========================
    // IAudioAIClient IMPLEMENTATION
    // =========================

    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // Reset state for new call
        ResetCallState(caller);

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        // Match the header used by the PCM16 client; without this the server may enforce a different schema.
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        Log($"📞 Connecting to OpenAI Realtime (G.711 {_codec} @ 8kHz)...");
        await _ws.ConnectAsync(
            new Uri($"wss://api.openai.com/v1/realtime?model={_model}"),
            linked.Token
        ).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoopTask = Task.Run(ReceiveLoopAsync);

        // Wait for session.created from OpenAI
        Log("⏳ Waiting for session.created...");
        for (int i = 0; i < 100 && Volatile.Read(ref _sessionCreated) == 0; i++)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);

        if (Volatile.Read(ref _sessionCreated) == 0)
            throw new TimeoutException("Timeout waiting for session.created");

        Log("✅ Session created, configuring...");
        await ConfigureSessionAsync().ConfigureAwait(false);

        // Wait for session.updated
        Log("⏳ Waiting for session.updated...");
        for (int i = 0; i < 100 && Volatile.Read(ref _sessionUpdated) == 0; i++)
            await Task.Delay(50, linked.Token).ConfigureAwait(false);

        if (Volatile.Read(ref _sessionUpdated) == 0)
            throw new TimeoutException("Timeout waiting for session.updated");

        Log("✅ Session configured");

        // Send greeting after session.updated
        if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
        {
            await SendGreetingAsync().ConfigureAwait(false);
            Log("📢 Greeting sent");
        }

        OnConnected?.Invoke();
        Log($"✅ Connected to OpenAI Realtime (G.711 {_codec}, voice={_voice})");
    }

    /// <summary>
    /// Send µ-law audio frame to OpenAI. Input: 160 bytes µ-law @ 8kHz (20ms frame).
    /// </summary>
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData == null || ulawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) != 0) return;

        try
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(ulawData)
            });

            await SendTextAsync(msg).ConfigureAwait(false);

            // Track when we last sent audio (for no-reply watchdog)
            Volatile.Write(ref _lastAudioSentAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var count = Interlocked.Increment(ref _audioFramesSent);
            if (count % 50 == 0) // Log every 1 second
                Log($"📤 Sent {count} audio frames to OpenAI");
        }
        catch (Exception ex)
        {
            Log($"⚠️ SendMuLawAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send PCM16 @ 8kHz audio. Converts to µ-law before sending.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 8000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;

        // Convert PCM16 → µ-law
        var samples = AudioCodecs.BytesToShorts(pcmData);
        
        // Resample if needed
        if (sampleRate != SAMPLE_RATE)
        {
            samples = AudioCodecs.Resample(samples, sampleRate, SAMPLE_RATE);
        }

        var ulaw = AudioCodecs.MuLawEncode(samples);
        await SendMuLawAsync(ulaw).ConfigureAwait(false);
    }

    /// <summary>
    /// Get next µ-law frame from outbound queue (160 bytes = 20ms @ 8kHz).
    /// </summary>
    public byte[]? GetNextMuLawFrame() => _outboundQueue.TryDequeue(out var f) ? f : null;

    public void ClearPendingFrames()
    {
        while (_outboundQueue.TryDequeue(out _)) { }
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        Log("📴 Disconnecting...");
        Interlocked.Exchange(ref _callEnded, 1);

        try
        {
            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                         .ConfigureAwait(false);
            }
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
    // SESSION CONFIGURATION
    // =========================

    private async Task ConfigureSessionAsync()
    {
        // Dynamic codec configuration based on constructor setting
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetSystemPrompt(),
                voice = _voice,
                input_audio_format = _openAiFormatString,
                output_audio_format = _openAiFormatString,
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

        Log($"🎧 Configuring session: format={_openAiFormatString}, voice={_voice}, server_vad");
        await SendJsonAsync(config).ConfigureAwait(false);
    }

    private async Task SendGreetingAsync()
    {
        var greeting = GetLocalizedGreeting(_detectedLanguage);
        
        var msg = new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Say exactly: \"{greeting}\""
            }
        };

        await SendJsonAsync(msg).ConfigureAwait(false);
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
                    break;

                case "session.updated":
                    Interlocked.Exchange(ref _sessionUpdated, 1);
                    Log("📋 session.updated received");
                    break;

                case "response.created":
                    Interlocked.Exchange(ref _responseActive, 1);
                    Log("🤖 AI response started");
                    OnResponseStarted?.Invoke();
                    break;

                case "response.done":
                    Interlocked.Exchange(ref _responseActive, 0);
                    Volatile.Write(ref _lastResponseDoneAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Log("🤖 AI response completed");
                    
                    // CRITICAL: Fire event to unblock user audio in call handler
                    try
                    {
                        var hasSubscribers = OnResponseCompleted != null;
                        Log($"📢 Firing OnResponseCompleted (subscribers: {hasSubscribers})");
                        OnResponseCompleted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ OnResponseCompleted error: {ex.Message}");
                    }
                    
                    // Start no-reply watchdog if awaiting confirmation
                    if (_awaitingConfirmation)
                    {
                        StartNoReplyWatchdog();
                    }
                    break;

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                    {
                        var audioBase64 = deltaEl.GetString();
                        if (!string.IsNullOrEmpty(audioBase64))
                        {
                            ProcessAudioDelta(audioBase64);
                        }
                    }
                    break;

                case "response.audio.done":
                    // Flush any leftover partial frame at end-of-audio.
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
                        }
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userTranscriptEl))
                    {
                        var text = userTranscriptEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            Log($"👤 User: {text}");
                            OnTranscript?.Invoke($"User: {text}");
                        }
                    }
                    break;

                case "input_audio_buffer.speech_started":
                    Log("🎤 User speaking...");
                    Interlocked.Exchange(ref _userSpeaking, 1);
                    StopNoReplyWatchdog(); // Cancel watchdog when user speaks
                    break;

                case "input_audio_buffer.speech_stopped":
                    Log("🔇 User stopped speaking");
                    Interlocked.Exchange(ref _userSpeaking, 0);
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
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                await SendResponseCreateAsync().ConfigureAwait(false);
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

                    await SendResponseCreateAsync().ConfigureAwait(false);
                }
                else if (action == "confirmed")
                {
                    _booking.Confirmed = true;
                    _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    _awaitingConfirmation = false;
                    StopNoReplyWatchdog(); // Stop watchdog when booking confirmed

                    OnBookingUpdated?.Invoke(_booking);
                    Log($"✅ Booked: {_booking.BookingRef} (caller={_callerId})");

                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        booking_ref = _booking.BookingRef,
                        message = $"Taxi booked. Reference: {_booking.BookingRef}"
                    }).ConfigureAwait(false);

                    await SendResponseCreateAsync().ConfigureAwait(false);
                }
                break;
            }

            case "end_call":
                Log("📞 End call requested - ignoring further user audio...");
                Interlocked.Exchange(ref _ignoreUserAudio, 1);

                await SendToolResultAsync(callId, new { success = true }).ConfigureAwait(false);

                // Wait for audio to drain before signaling call end
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    
                    for (int i = 0; i < 100 && !_outboundQueue.IsEmpty; i++)
                        await Task.Delay(100).ConfigureAwait(false);

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
        var msg = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = JsonSerializer.Serialize(output)
            }
        };
        await SendJsonAsync(msg).ConfigureAwait(false);
    }

    private async Task SendResponseCreateAsync()
    {
        await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
    }

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 0)
        {
            Log($"📴 Call ended: {reason}");
            OnCallEnded?.Invoke();
        }
    }

    // =========================
    // NO-REPLY WATCHDOG
    // =========================

    private void StartNoReplyWatchdog()
    {
        StopNoReplyWatchdog();

        // Don't start if we've already re-prompted too many times
        var reprompts = Volatile.Read(ref _noReplyRepromptCount);
        if (reprompts >= MAX_NO_REPLY_REPROMPTS)
        {
            Log($"⚠️ Max re-prompts ({MAX_NO_REPLY_REPROMPTS}) reached, not starting watchdog");
            return;
        }

        Log($"⏰ No-reply watchdog started ({NO_REPLY_TIMEOUT_MS}ms timeout, awaiting confirmation, attempt {reprompts + 1}/{MAX_NO_REPLY_REPROMPTS})");

        _noReplyWatchdog = new Timer(async _ =>
        {
            // Check if still awaiting confirmation and user hasn't spoken
            if (!_awaitingConfirmation || Volatile.Read(ref _userSpeaking) != 0)
            {
                StopNoReplyWatchdog();
                return;
            }

            // Check if AI is currently generating a response
            if (Volatile.Read(ref _responseActive) != 0)
            {
                return; // AI is speaking, don't interrupt
            }

            // Check elapsed time since last response.done
            var lastDone = Volatile.Read(ref _lastResponseDoneAt);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var elapsed = now - lastDone;

            if (elapsed >= NO_REPLY_TIMEOUT_MS)
            {
                StopNoReplyWatchdog(); // Stop before re-prompting (will restart after response.done)
                await SendNoReplyRepromptAsync().ConfigureAwait(false);
            }
        }, null, NO_REPLY_TIMEOUT_MS, Timeout.Infinite); // Fire once, not repeating
    }

    private void StopNoReplyWatchdog()
    {
        var watchdog = Interlocked.Exchange(ref _noReplyWatchdog, null);
        if (watchdog != null)
        {
            try { watchdog.Dispose(); } catch { }
        }
    }

    private async Task SendNoReplyRepromptAsync()
    {
        if (!IsConnected || !_awaitingConfirmation) return;

        Interlocked.Increment(ref _noReplyRepromptCount);

        try
        {
            // Check if we have recent audio - if so, try to commit it first
            var lastAudioSent = Volatile.Read(ref _lastAudioSentAt);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var audioAge = now - lastAudioSent;

            if (audioAge < 3000) // Audio was sent within last 3 seconds
            {
                Log("⏰ No-reply timeout - committing recent audio buffer...");
                await SendJsonAsync(new { type = "input_audio_buffer.commit" }).ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);
            }
            else
            {
                Log($"⏰ No-reply timeout - no recent audio (last sent {audioAge}ms ago), sending re-prompt...");
            }

            // Inject a system message asking for clarification, then trigger response
            var repromptMsg = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                        new { type = "input_text", text = "[SILENCE DETECTED - User may not have heard or responded. Politely ask if they'd like to confirm the booking. Do NOT assume they said yes.]" }
                    }
                }
            };

            await SendJsonAsync(repromptMsg).ConfigureAwait(false);
            Log("📤 Injected silence-detected system message");

            await Task.Delay(100).ConfigureAwait(false);
            await SendResponseCreateAsync().ConfigureAwait(false);
            Log("📤 Response.create sent after silence detection");
        }
        catch (Exception ex)
        {
            Log($"⚠️ Re-prompt error: {ex.Message}");
        }
    }

    // =========================
    // AUDIO PROCESSING
    // =========================

    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            // Decode base64 to get G.711 bytes (μ-law or A-law depending on config)
            var g711Bytes = Convert.FromBase64String(base64);
            AppendAndEnqueueG711(g711Bytes);

            var chunks = Interlocked.Increment(ref _audioChunksReceived);
            if (chunks % 10 == 0)
            {
                Log($"📢 Received {chunks} audio chunks (G.711 {_codec}), queue={_outboundQueue.Count}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio processing error: {ex.Message}");
        }
    }

    private void AppendAndEnqueueG711(byte[] bytes)
    {
        if (bytes.Length == 0) return;

        // Ensure capacity and contiguity
        if (_audioAccumOffset + _audioAccumLength + bytes.Length > _audioAccum.Length)
        {
            // Compact if we have a head offset
            if (_audioAccumLength > 0 && _audioAccumOffset > 0)
            {
                Buffer.BlockCopy(_audioAccum, _audioAccumOffset, _audioAccum, 0, _audioAccumLength);
                _audioAccumOffset = 0;
            }

            if (_audioAccumLength + bytes.Length > _audioAccum.Length)
            {
                var newCap = _audioAccum.Length;
                while (newCap < _audioAccumLength + bytes.Length)
                    newCap *= 2;
                var next = new byte[newCap];
                if (_audioAccumLength > 0)
                    Buffer.BlockCopy(_audioAccum, _audioAccumOffset, next, 0, _audioAccumLength);
                _audioAccum = next;
                _audioAccumOffset = 0;
            }
        }

        Buffer.BlockCopy(bytes, 0, _audioAccum, _audioAccumOffset + _audioAccumLength, bytes.Length);
        _audioAccumLength += bytes.Length;

        // Emit full 20ms frames
        while (_audioAccumLength >= FRAME_SIZE_BYTES)
        {
            var frame = new byte[FRAME_SIZE_BYTES];
            Buffer.BlockCopy(_audioAccum, _audioAccumOffset, frame, 0, FRAME_SIZE_BYTES);
            _audioAccumOffset += FRAME_SIZE_BYTES;
            _audioAccumLength -= FRAME_SIZE_BYTES;

            if (_audioAccumLength == 0)
                _audioAccumOffset = 0;

            // Queue management
            while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                _outboundQueue.TryDequeue(out _);
            _outboundQueue.Enqueue(frame);
        }
    }

    private void FlushAudioAccumulator(bool padWithSilence)
    {
        if (_audioAccumLength <= 0) return;

        if (padWithSilence)
        {
            var frame = new byte[FRAME_SIZE_BYTES];
            var count = Math.Min(_audioAccumLength, FRAME_SIZE_BYTES);
            Buffer.BlockCopy(_audioAccum, _audioAccumOffset, frame, 0, count);
            if (count < FRAME_SIZE_BYTES)
                Array.Fill(frame, _silenceByte, count, FRAME_SIZE_BYTES - count);

            while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                _outboundQueue.TryDequeue(out _);
            _outboundQueue.Enqueue(frame);
        }

        _audioAccumOffset = 0;
        _audioAccumLength = 0;
    }

    // =========================
    // SYSTEM PROMPT & TOOLS
    // =========================

    private string GetSystemPrompt() => $@"You are Ada, a taxi booking assistant.

LANGUAGE: Start in {GetLanguageName(_detectedLanguage)} based on caller's phone number. If they speak a different language, switch to that language.

FLOW: Greet → NAME → PICKUP → DESTINATION → PASSENGERS → TIME → CONFIRM details once → book_taxi(request_quote) → Tell fare → Confirm → book_taxi(confirmed) → Give reference ID → 'Anything else?' → If no: 'Thank you for using Voice Taxibot. Goodbye!' → end_call

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
    // HELPERS
    // =========================

    private void ResetCallState(string? caller)
    {
        ClearPendingFrames();
        StopNoReplyWatchdog();

        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);
        _booking.Reset();
        _awaitingConfirmation = false;
        Interlocked.Exchange(ref _userSpeaking, 0);
        Interlocked.Exchange(ref _noReplyRepromptCount, 0);
        Volatile.Write(ref _lastResponseDoneAt, 0);
        Volatile.Write(ref _lastAudioSentAt, 0);

        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _sessionCreated, 0);
        Interlocked.Exchange(ref _sessionUpdated, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
    }

    private string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";

        var norm = phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");

        // Dutch mobile
        if (norm.StartsWith("06") && norm.Length == 10) return "nl";
        if (norm.StartsWith("0") && !norm.StartsWith("00") && norm.Length == 10) return "nl";

        // International prefix
        if (norm.StartsWith("+")) norm = norm.Substring(1);
        else if (norm.StartsWith("00") && norm.Length > 4) norm = norm.Substring(2);

        if (norm.Length >= 2)
        {
            var code = norm.Substring(0, 2);
            return code switch
            {
                "31" => "nl",
                "32" => "nl", // Belgium
                "33" => "fr",
                "34" => "es",
                "39" => "it",
                "48" => "pl",
                "49" => "de",
                "351" => "pt",
                _ => "en"
            };
        }

        return "en";
    }

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

    private async Task SendJsonAsync(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        await SendTextAsync(json).ConfigureAwait(false);
    }

    private async Task SendTextAsync(string text)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _sendMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
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
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(OpenAIRealtimeG711Client));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        SignalCallEnded("disposed");
        StopNoReplyWatchdog();

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendMutex.Dispose();
    }
}
