using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API client using native G.711 8kHz for direct SIP passthrough.
/// 
/// v2.0: Native G.711 mode - zero resampling, zero artifacts
/// - OpenAI sends/receives 8kHz G.711 (g711_alaw or g711_ulaw) directly
/// - Ingress: SIP G.711 → OpenAI (direct passthrough)
/// - Egress: OpenAI G.711 → SIP (direct passthrough)
/// - No resampling = no aliasing, no raspiness, minimal latency
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "2.0-native-g711";

    // =========================
    // G.711 CODEC SELECTION (from SIP negotiation)
    // =========================
    public enum G711Codec { MuLaw, ALaw }
    
    private readonly G711Codec _codec;
    private readonly byte _silenceByte;
    
    /// <summary>
    /// The negotiated codec for this session (PCMA = A-law, PCMU = μ-law).
    /// </summary>
    public G711Codec NegotiatedCodec => _codec;
    
    /// <summary>
    /// Helper to create client from SIP codec name (PCMA/PCMU).
    /// </summary>
    public static G711Codec ParseSipCodec(string codecName)
    {
        return codecName?.ToUpperInvariant() switch
        {
            "PCMA" => G711Codec.ALaw,
            "PCMU" => G711Codec.MuLaw,
            _ => G711Codec.MuLaw // Default to μ-law if unknown
        };
    }

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // Audio format: Native 8kHz G.711 for both OpenAI and SIP (zero resampling)
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE_BYTES = SAMPLE_RATE * FRAME_MS / 1000; // 160 bytes (G.711)

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
    private readonly TaxiSipBridge.BookingState _booking = new TaxiSipBridge.BookingState();
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
    // Primary path (new): PCM16 @ 24kHz streamed via OnPcm24Audio → playout engine (DirectRtpPlayout).
    // Compatibility path (legacy): some call flows still poll GetNextMuLawFrame (e.g. SipAudioPlayout
    // or older SIP handlers). For those, we also maintain a small outbound G.711 frame queue.
    private const int MAX_OUTBOUND_FRAMES = 2000; // ~40s @ 20ms frames; prevents burst dropouts
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private int _outboundQueueCount;

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
    public event Action<byte[]>? OnPcm24Audio;  // Legacy: PCM16 @ 24kHz (for DSP path)
    public event Action<byte[]>? OnG711Audio;   // Native: Raw G.711 @ 8kHz (zero DSP)
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnResponseStarted;
    public event Action? OnResponseCompleted;
    public event Action<string>? OnAdaSpeaking;
    public event Action? OnCallEnded;
    public event Action? OnBargeIn; // Fired when user interrupts AI
    public event Action<BookingState>? OnBookingUpdated;

    // =========================
    // PROPERTIES
    // =========================
    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 &&
        _ws?.State == WebSocketState.Open;

    // NOTE: PendingFrameCount is now always 0 - audio flows through DirectRtpPlayout

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

        Log($"📞 Connecting to OpenAI Realtime (PCM16 24kHz with DSP)...");
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
        Log($"✅ Connected to OpenAI Realtime (PCM16 24kHz → DSP → {(_codec == G711Codec.ALaw ? "A-law" : "μ-law")}, voice={_voice})");
    }

    /// <summary>
    /// Send G.711 audio frame to OpenAI (NATIVE G.711 MODE - zero resampling).
    /// Direct passthrough: SIP G.711 → OpenAI G.711 (no decode, no resample).
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
            // NATIVE G.711 MODE: Send raw G.711 bytes directly to OpenAI
            // No decoding, no resampling - just base64 encode and send!
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(g711Data)
            });

            await SendBytesAsync(bytes).ConfigureAwait(false);
            Volatile.Write(ref _lastUserSpeechAt, NowMs());

            var count = Interlocked.Increment(ref _audioFramesSent);
            if (count % 50 == 0)
            {
                var codecName = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
                Log($"📤 Sent {count} audio frames to OpenAI ({codecName} native 8kHz - zero resample)");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ SendMuLawAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Simple 3x upsample fallback when SpeexDSP unavailable.
    /// </summary>
    private static short[] Upsample8kTo24kFallback(short[] pcm8k)
    {
        if (pcm8k.Length == 0) return Array.Empty<short>();
        
        var pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            short current = pcm8k[i];
            short next = (i + 1 < pcm8k.Length) ? pcm8k[i + 1] : current;
            
            // Linear interpolation: 3 output samples per input sample
            pcm24k[i * 3] = current;
            pcm24k[i * 3 + 1] = (short)((current * 2 + next) / 3);
            pcm24k[i * 3 + 2] = (short)((current + next * 2) / 3);
        }
        return pcm24k;
    }

    /// <summary>
    /// Send PCM16 audio. Encodes to G.711 for native passthrough.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 8000)
    {
        if (!IsConnected || pcmData == null || pcmData.Length == 0) return;

        var samples = AudioCodecs.BytesToShorts(pcmData);
        
        // Resample to 8kHz if needed
        if (sampleRate != SAMPLE_RATE)
            samples = AudioCodecs.Resample(samples, sampleRate, SAMPLE_RATE);

        byte[] encoded = _codec == G711Codec.ALaw
            ? AudioCodecs.ALawEncode(samples)
            : AudioCodecs.MuLawEncode(samples);

        await SendMuLawAsync(encoded).ConfigureAwait(false);
    }

    /// <summary>
    /// Legacy pull-based playout.
    /// NOTE: Despite the name, this returns a 20ms G.711 frame matching the configured codec
    /// (A-law or μ-law).
    /// </summary>
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

    public int PendingFrameCount => Math.Max(0, Volatile.Read(ref _outboundQueueCount));

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
        // NATIVE G.711 MODE: OpenAI receives and sends G.711 @ 8kHz directly
        // Zero resampling = maximum audio quality for telephony
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetSystemPrompt(),
                voice = _voice,
                // TRUE NATIVE G.711 MODE - Zero resampling!
                // OpenAI handles 8kHz G.711 directly, no conversion needed
                input_audio_format = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw",
                output_audio_format = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.6,              // Raised from 0.5 to reduce false barge-ins from echo
                    prefix_padding_ms = 400,      // Raised from 300 for more confirmation before speech_started
                    silence_duration_ms = 1000    // 1 second silence = end of turn
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.8
            }
        };

        var codecName = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
        Log($"🎧 Configuring session: format={codecName}@8kHz (NATIVE - zero resampling), voice={_voice}, server_vad");
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

                        // Send response.create directly - bypass CanCreateResponse() since we've already
                        // confirmed 8s of silence, and _lastUserSpeechAt is updated by soft-gated audio
                        await Task.Delay(20).ConfigureAwait(false);
                        await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                        Log("🔄 response.create sent (no-reply watchdog)");
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
                    // Audio accumulator removed - audio flows through OnPcm24Audio → DirectRtpPlayout
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
                            // Native G.711 mode has lower buffering latency; 900ms was too aggressive
                            // and was dropping legitimate fast corrections. Use 400ms instead.
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
                    Interlocked.Increment(ref _noReplyWatchdogId);
                    
                    // Proactive barge-in: Stop OpenAI's server-side generation
                    if (_activeResponseId != null && Volatile.Read(ref _responseActive) == 1)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await SendJsonAsync(new
                                {
                                    type = "conversation.item.truncate",
                                    item_id = _activeResponseId,
                                    content_index = 0,
                                    audio_end_ms = 0
                                }).ConfigureAwait(false);
                                Log("✂️ Truncated server-side response");
                            }
                            catch (Exception ex)
                            {
                                Log($"⚠️ Truncate error: {ex.Message}");
                            }
                        });
                    }
                    
                    // Notify handler to clear local outbound audio
                    OnBargeIn?.Invoke();
                    Log("✂️ Barge-in detected");
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
                // Force OpenAI to continue after tool result - it doesn't auto-continue!
                await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
                break;

            case "book_taxi":
            {
                var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

                if (action == "request_quote")
                {
                    // Pass phone number for region-based geocoding
                    var fareResult = await FareCalculator.CalculateFareWithCoordsAsync(
                        _booking.Pickup, 
                        _booking.Destination,
                        _callerId  // Phone number for region detection
                    ).ConfigureAwait(false);

                    _booking.Fare = fareResult.Fare;
                    _booking.Eta = fareResult.Eta;
                    
                    // Store geocoded coordinates
                    _booking.PickupLat = fareResult.PickupLat;
                    _booking.PickupLon = fareResult.PickupLon;
                    _booking.DestLat = fareResult.DestLat;
                    _booking.DestLon = fareResult.DestLon;
                    
                    // Store parsed address components from geocoding
                    _booking.PickupStreet = fareResult.PickupStreet;
                    _booking.PickupNumber = fareResult.PickupNumber;
                    _booking.PickupPostalCode = fareResult.PickupPostalCode;
                    _booking.PickupCity = fareResult.PickupCity;
                    _booking.PickupFormatted = fareResult.PickupFormatted;
                    
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
                        message = $"Fare is {fareResult.Fare}, driver arrives in {fareResult.Eta}. Book it?"
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

                    // BSQD dispatch (fire-and-forget with logging)
                    BsqdDispatcher.OnLog += msg => Log(msg);
                    BsqdDispatcher.Dispatch(_booking, _callerId);

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
                    // Audio now flows through DirectRtpPlayout - just wait a moment for any remaining audio
                    await Task.Delay(2000).ConfigureAwait(false);
                    Log("✅ Grace period complete, ending call");
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
    // AUDIO PROCESSING (NATIVE G.711 @ 8kHz - zero resampling)
    // =========================

    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            // NATIVE G.711 MODE: OpenAI sends G.711 @ 8kHz directly
            byte[] g711Bytes = Convert.FromBase64String(base64);

            // PRIMARY PATH: Fire OnG711Audio for native zero-DSP passthrough
            OnG711Audio?.Invoke(g711Bytes);

            // LEGACY PATH: Convert to PCM24k for older handlers that expect it
            if (OnPcm24Audio != null)
            {
                short[] pcm8k = _codec == G711Codec.ALaw
                    ? AudioCodecs.ALawDecode(g711Bytes)
                    : AudioCodecs.MuLawDecode(g711Bytes);
                
                // Simple 3x linear upsample for DSP path compatibility
                short[] pcm24k = new short[pcm8k.Length * 3];
                for (int i = 0; i < pcm8k.Length; i++)
                {
                    short current = pcm8k[i];
                    short next = (i + 1 < pcm8k.Length) ? pcm8k[i + 1] : current;
                    pcm24k[i * 3] = current;
                    pcm24k[i * 3 + 1] = (short)((current * 2 + next) / 3);
                    pcm24k[i * 3 + 2] = (short)((current + next * 2) / 3);
                }
                byte[] pcm24kBytes = AudioCodecs.ShortsToBytes(pcm24k);
                OnPcm24Audio.Invoke(pcm24kBytes);
            }

            // Legacy queue for pull-based handlers (GetNextMuLawFrame)
            EnqueueG711FramesDirect(g711Bytes);

            var chunks = Interlocked.Increment(ref _audioChunksReceived);
            if (chunks % 10 == 0)
            {
                var codecName = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
                Log($"📢 Received {chunks} audio chunks ({codecName} native 8kHz - zero resample), bytes={g711Bytes.Length}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ ProcessAudioDelta error: {ex.Message}");
        }
    }

    /// <summary>
    /// Direct G.711 enqueue - zero resampling path for native G.711 mode.
    /// </summary>
    private void EnqueueG711FramesDirect(byte[] g711Bytes)
    {
        if (g711Bytes == null || g711Bytes.Length == 0) return;

        // Frame into exact 20ms chunks (160 bytes @ 8kHz G.711)
        for (int i = 0; i < g711Bytes.Length; i += FRAME_SIZE_BYTES)
        {
            var frame = new byte[FRAME_SIZE_BYTES];
            var count = Math.Min(FRAME_SIZE_BYTES, g711Bytes.Length - i);
            Array.Copy(g711Bytes, i, frame, 0, count);
            if (count < FRAME_SIZE_BYTES)
                Array.Fill(frame, _silenceByte, count, FRAME_SIZE_BYTES - count);

            // Overflow protection: drop oldest frames
            while (Volatile.Read(ref _outboundQueueCount) >= MAX_OUTBOUND_FRAMES)
            {
                if (_outboundQueue.TryDequeue(out _))
                    Interlocked.Decrement(ref _outboundQueueCount);
                else
                    break;
            }

            _outboundQueue.Enqueue(frame);
            Interlocked.Increment(ref _outboundQueueCount);
        }
    }

    private void EnqueueG711FramesFromPcm24k(byte[] pcm24kBytes)
    {
        if (pcm24kBytes == null || pcm24kBytes.Length < 2) return;

        try
        {
            var pcm24k = AudioCodecs.BytesToShorts(pcm24kBytes);

            short[] pcm8k;
            try
            {
                // Prefer SpeexDSP when available (best quality)
                pcm8k = AudioCodecs.ResampleWithSpeex(pcm24k, 24000, 8000);
            }
            catch
            {
                // Fallback: simple 3:1 FIR decimation (0.25, 0.5, 0.25)
                pcm8k = Downsample24kTo8kFallback(pcm24k);
            }

            byte[] g711 = _codec == G711Codec.ALaw
                ? AudioCodecs.ALawEncode(pcm8k)
                : AudioCodecs.MuLawEncode(pcm8k);

            // Frame into exact 20ms chunks (160 bytes).
            for (int i = 0; i < g711.Length; i += FRAME_SIZE_BYTES)
            {
                var frame = new byte[FRAME_SIZE_BYTES];
                var count = Math.Min(FRAME_SIZE_BYTES, g711.Length - i);
                Array.Copy(g711, i, frame, 0, count);
                if (count < FRAME_SIZE_BYTES)
                    Array.Fill(frame, _silenceByte, count, FRAME_SIZE_BYTES - count);

                // Overflow protection: drop oldest frames.
                while (Volatile.Read(ref _outboundQueueCount) >= MAX_OUTBOUND_FRAMES)
                {
                    if (_outboundQueue.TryDequeue(out _))
                        Interlocked.Decrement(ref _outboundQueueCount);
                    else
                        break;
                }

                _outboundQueue.Enqueue(frame);
                Interlocked.Increment(ref _outboundQueueCount);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ EnqueueG711FramesFromPcm24k error: {ex.Message}");
        }
    }

    private static short[] Downsample24kTo8kFallback(short[] pcm24k)
    {
        if (pcm24k.Length < 3) return Array.Empty<short>();

        int outLen = pcm24k.Length / 3;
        var pcm8k = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int idx = i * 3;
            // Weighted average: 0.25, 0.5, 0.25 (anti-alias-ish)
            pcm8k[i] = (short)(pcm24k[idx] * 0.25f + pcm24k[idx + 1] * 0.5f + pcm24k[idx + 2] * 0.25f);
        }

        return pcm8k;
    }

    // NOTE: AppendAndEnqueueG711 and FlushAudioAccumulator removed
    // Audio now flows through OnPcm24Audio → DirectRtpPlayout (DSP) → RTP

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

        // Audio accumulator removed - audio flows through OnPcm24Audio → DirectRtpPlayout
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
        // Name mishearings (first turn)
        { "It's mucks", "It's Max" },
        { "Its mucks", "It's Max" },
        { "mucks", "Max" },
        // Address corrections
        { "52 I ain't dead bro", "52A David Road" },
        { "52 I ain't David", "52A David Road" },
        { "52 ain't David", "52A David Road" },
        { "52 a David", "52A David Road" },
        { "Dalebridge Road", "David Road" },
        { "Dale Bridge Road", "David Road" },
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

LANGUAGE: Start in {GetLanguageName(_detectedLanguage)} based on caller's phone number. However, CONTINUOUSLY MONITOR the caller's spoken language. If they speak a different language OR explicitly ASK you to speak another language (e.g. 'Can you speak French?', 'Spreek Nederlands', 'Parla italiano', 'Habla español'), IMMEDIATELY SWITCH to that language for ALL subsequent responses. Supported: English, Dutch, French, German, Spanish, Italian, Polish, Portuguese. Default to English if uncertain.

FLOW: Greet → NAME → PICKUP → DESTINATION → PASSENGERS → TIME → CONFIRM details once ('So that's [passengers] from [pickup] to [destination] at [time]. Correct?') → If changes: update and confirm ONCE more → If correct: 'Shall I get a price?' → book_taxi(request_quote) → Tell fare → 'Confirm booking?' → book_taxi(confirmed) → Give reference ID ONLY (no repeat of journey) → 'Anything else?' → If no: 'You'll receive a WhatsApp with your booking details. Thank you for using Voice Taxibot. Goodbye!' → end_call

DATA SYNC (CRITICAL): After EVERY user message that provides or corrects booking info, call sync_booking_data immediately.
- Include ALL fields you know so far (caller_name, pickup, destination, passengers, pickup_time), not just the one you just collected.
- If a user corrects a detail, treat the user's correction as the SOURCE OF TRUTH.

NO REPETITION: After booking is confirmed, say the ACTUAL reference ID from the tool response (e.g. 'Your taxi is booked, reference TX12345.'). NEVER say '[ID]' literally. Do NOT repeat pickup, destination, passengers, or time again.

CORRECTIONS: If user corrects any detail, update it and give ONE new summary. Do not repeat the same summary multiple times.
- Keep addresses VERBATIM as spoken (do not 'improve' them). If user says a hyphen (e.g. '52-8'), keep the hyphen.

PRONUNCIATION: 
- 4-digit house numbers like '1214A' say 'twelve fourteen A' (NOT 'one two one four' and NOT hyphenated '12-14A')
- Hyphenated ranges like '12-14' say 'twelve to fourteen' 
- Suffixes like '52A' say 'fifty-two A'
- NEVER insert hyphens into addresses that don't have them

EVENTS: If caller asks about events or 'what's on', call find_local_events. List 2-3 events briefly.

NEAREST: If caller says 'nearest' + place type, set destination to 'Nearest [place type]'.

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

    // Address parsing helpers moved to BsqdDispatcher

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
