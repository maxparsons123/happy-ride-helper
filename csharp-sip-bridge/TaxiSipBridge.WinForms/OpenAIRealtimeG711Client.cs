using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI Realtime API client using G.711 μ-law @ 8kHz (native OpenAI support).
/// 
/// Key Benefits:
/// - No resampling overhead (8kHz throughout)
/// - Better audio quality (no lossy resampling)
/// - Lower latency and CPU usage
/// - Direct passthrough from SIP → OpenAI → SIP
/// 
/// Audio Flow:
/// - Input: PCM16 @ 8kHz → G.711 μ-law → OpenAI
/// - Output: OpenAI → G.711 μ-law → PCM16 @ 8kHz
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "1.0";

    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _instructions;
    private readonly string? _greeting;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);

    // Audio format: G.711 μ-law @ 8kHz
    private const string AUDIO_FORMAT = "audio/pcmu";
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int FRAME_SIZE_BYTES = SAMPLE_RATE * FRAME_MS / 1000; // 160 samples = 160 bytes (μ-law)
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

    // =========================
    // STATS
    // =========================
    private long _audioFramesSent;
    private long _audioChunksReceived;

    // =========================
    // EVENTS
    // =========================
    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action<byte[]>? OnPcm24Audio; // Not used in G.711 mode, but required by interface
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnResponseStarted;
    public event Action<string>? OnAdaSpeaking;
    public event Action? OnCallEnded;

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
        string? instructions = null,
        string? greeting = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _instructions = instructions ?? "You are a helpful assistant.";
        _greeting = greeting;
    }

    // =========================
    // IAudioAIClient IMPLEMENTATION
    // =========================

    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        // Note: Don't use "OpenAI-Beta: realtime=v1" - that connects to old API

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        Log($"📞 Connecting to OpenAI Realtime (G.711 μ-law @ 8kHz)...");
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
        if (!string.IsNullOrEmpty(_greeting) && Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
        {
            await SendGreetingAsync().ConfigureAwait(false);
            Log("📢 Greeting sent");
        }

        OnConnected?.Invoke();
        Log($"✅ Connected to OpenAI Realtime (G.711 μ-law, voice={_voice})");
    }

    /// <summary>
    /// Send µ-law audio frame to OpenAI. Input: 160 bytes µ-law @ 8kHz (20ms frame).
    /// </summary>
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData == null || ulawData.Length == 0) return;

        try
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(ulawData)
            });

            await SendTextAsync(msg).ConfigureAwait(false);

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
    // SESSION CONFIGURATION (New Schema)
    // =========================

    private async Task ConfigureSessionAsync()
    {
        // New OpenAI Realtime API schema
        var config = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = _model,
                output_modalities = new[] { "audio" },
                audio = new
                {
                    input = new
                    {
                        format = new { type = AUDIO_FORMAT },
                        transcription = new { model = "whisper-1" },
                        noise_reduction = new { type = "near_field" },
                        turn_detection = new
                        {
                            type = "semantic_vad",
                            create_response = true,
                            eagerness = "medium"
                        }
                    },
                    output = new
                    {
                        format = new { type = AUDIO_FORMAT },
                        voice = _voice
                    }
                },
                instructions = _instructions
            }
        };

        Log($"🎧 Configuring session: format={AUDIO_FORMAT}, voice={_voice}, semantic_vad");
        await SendJsonAsync(config).ConfigureAwait(false);
    }

    private async Task SendGreetingAsync()
    {
        var greeting = new
        {
            type = "response.create",
            response = new
            {
                instructions = _greeting,
                conversation = "none",
                output_modalities = new[] { "audio" },
                metadata = new { response_purpose = "greeting" }
            }
        };

        await SendJsonAsync(greeting).ConfigureAwait(false);
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
                    Log("🤖 AI response completed");
                    break;

                case "response.output_audio.delta":
                    // Audio chunk from AI (base64 encoded G.711 μ-law @ 8kHz)
                    if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                    {
                        var audioBase64 = deltaEl.GetString();
                        if (!string.IsNullOrEmpty(audioBase64))
                        {
                            ProcessAudioDelta(audioBase64);
                        }
                    }
                    break;

                case "response.audio.delta":
                    // Alternative audio delta format (older API)
                    if (doc.RootElement.TryGetProperty("delta", out var audioDeltaEl))
                    {
                        var audioBase64 = audioDeltaEl.GetString();
                        if (!string.IsNullOrEmpty(audioBase64))
                        {
                            ProcessAudioDelta(audioBase64);
                        }
                    }
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
                    break;

                case "input_audio_buffer.speech_stopped":
                    Log("🔇 User stopped speaking");
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

                default:
                    // Log unhandled events for debugging (debug level)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ ProcessMessageAsync error: {ex.Message}");
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
            // Decode base64 to get G.711 μ-law bytes
            var ulawBytes = Convert.FromBase64String(base64);

            // Frame into 160-byte (20ms) chunks for RTP
            for (int i = 0; i < ulawBytes.Length; i += FRAME_SIZE_BYTES)
            {
                var frame = new byte[FRAME_SIZE_BYTES];
                var count = Math.Min(FRAME_SIZE_BYTES, ulawBytes.Length - i);
                Array.Copy(ulawBytes, i, frame, 0, count);

                // Pad with silence (μ-law silence = 0xFF) if needed
                if (count < FRAME_SIZE_BYTES)
                    Array.Fill(frame, (byte)0xFF, count, FRAME_SIZE_BYTES - count);

                // Queue management
                while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                    _outboundQueue.TryDequeue(out _);

                _outboundQueue.Enqueue(frame);
            }

            var chunks = Interlocked.Increment(ref _audioChunksReceived);
            if (chunks % 10 == 0)
            {
                Log($"📢 Received {chunks} audio chunks (G.711 μ-law), queue={_outboundQueue.Count}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio processing error: {ex.Message}");
        }
    }

    // =========================
    // HELPERS
    // =========================

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
        Console.WriteLine($"{ts} {message}");
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

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendMutex.Dispose();
    }
}
