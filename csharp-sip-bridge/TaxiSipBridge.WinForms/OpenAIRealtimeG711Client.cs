using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Simplified OpenAI Realtime client for native G.711 audio.
/// Version 2.2-final-g711: Uses RTP playout completion as single source of truth.
/// 
/// Key design principles:
/// - RTP playout completion (NotifyPlayoutComplete) is the ONLY source of truth for when Ada finishes speaking
/// - No watchdog restarts from OpenAI events
/// - 200ms echo guard based on real audio playout, not API events
/// </summary>
public sealed class OpenAIRealtimeG711Client : IAudioAIClient, IDisposable
{
    public const string VERSION = "2.2-final-g711";

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

    // =========================
    // STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _responseQueued;
    private int _sessionCreated;
    private int _sessionUpdated;
    private int _ignoreUserAudio;
    private long _lastUserSpeechAt;
    private long _lastAdaFinishedAt;
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;

    // =========================
    // WEBSOCKET
    // =========================
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
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
    public event Action<byte[]>? OnPcm24Audio;  // Not used in G.711 mode
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
    // toolName, toolCallId, argumentsJson
    public event Func<string, string, JsonElement, Task<object>>? OnToolCall;

    // =========================
    // PROPERTIES
    // =========================
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public int PendingFrameCount => Math.Max(0, Volatile.Read(ref _outboundQueueCount));

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void Log(string msg) => OnLog?.Invoke(msg);

    // =========================
    // RESPONSE GATE (for tool continuation)
    // =========================
    private async Task QueueResponseCreateAsync(int delayMs = 40)
    {
        if (_disposed != 0 || _callEnded != 0)
            return;

        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1)
            return;

        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);

            // Only create a new response when OpenAI has ended the previous one
            if (Volatile.Read(ref _responseActive) == 1)
                return;

            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            Log("🔄 response.create sent");
        }
        catch { }
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
        _apiKey = apiKey;
        _model = model;
        _voice = voice;
        _codec = codec;
        _silenceByte = codec == G711Codec.ALaw ? (byte)0xD5 : (byte)0xFF;
    }

    // =========================
    // CONNECT
    // =========================
    public async Task ConnectAsync(string? callerPhone = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        Log("📞 Connecting to OpenAI Realtime (PCM16 24kHz with DSP)...");

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        await _ws.ConnectAsync(
            new Uri($"wss://api.openai.com/v1/realtime?model={_model}"),
            ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        Log("⏳ Waiting for session.created...");
        await WaitForSessionAsync();

        OnConnected?.Invoke();
    }

    private async Task WaitForSessionAsync()
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
        Log($"✅ Connected to OpenAI Realtime (PCM16 24kHz → DSP → {codecName}, voice={_voice})");
    }

    // =========================
    // SEND AUDIO (SIP → OPENAI)
    // =========================
    public async Task SendMuLawAsync(byte[] frame)
    {
        if (_disposed != 0 || _ignoreUserAudio != 0)
            return;

        // Echo guard: Only check RTP playout completion time
        // This is the ONLY source of truth for when Ada finished speaking
        if (NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 200)
            return;

        if (frame.Length != FRAME_BYTES)
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
            Log($"📤 Sent {count} audio frames to OpenAI ({codecName} native 8kHz - zero resample)");
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

    // =========================
    // RECEIVE LOOP
    // =========================
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await _ws.ReceiveAsync(buffer, ct);
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
            Log($"⚠️ Receive loop error: {ex.Message}");
        }

        OnDisconnected?.Invoke();
    }

    // =========================
    // MESSAGE HANDLER
    // =========================
    private async Task ProcessMessageAsync(string json)
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
                _sessionCreated = 1;
                break;

            case "session.updated":
                Log("📋 session.updated received");
                _sessionUpdated = 1;
                Log("✅ Session configured");
                break;

            case "response.created":
                _activeResponseId = TryGetResponseId(doc.RootElement);
                Interlocked.Exchange(ref _responseActive, 1);
                Log("🤖 AI response started");
                OnResponseStarted?.Invoke();
                break;

            case "response.done":
                var doneId = TryGetResponseId(doc.RootElement);
                if (_activeResponseId == doneId)
                {
                    _lastCompletedResponseId = doneId;
                    _activeResponseId = null;
                    Interlocked.Exchange(ref _responseActive, 0);
                    Log("🤖 AI response completed");
                    OnResponseCompleted?.Invoke();

                    // Commit audio buffer after response completes
                    await SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    Log("📝 Committed audio buffer (turn finalized)");
                }
                break;

            case "response.function_call_arguments.done":
                await HandleToolCallAsync(doc.RootElement).ConfigureAwait(false);
                break;

            case "response.audio.delta":
                if (doc.RootElement.TryGetProperty("delta", out var deltaEl))
                {
                    var b64 = deltaEl.GetString();
                    if (!string.IsNullOrEmpty(b64))
                    {
                        var bytes = Convert.FromBase64String(b64);
                        OnG711Audio?.Invoke(bytes);
                        
                        var count = Interlocked.Increment(ref _audioChunksReceived);
                        if (count % 10 == 0)
                        {
                            var codecName = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
                            Log($"📢 Received {count} audio chunks ({codecName} native 8kHz - zero resample), bytes={bytes.Length}");
                        }
                    }
                }
                break;

            case "response.audio_transcript.done":
                if (doc.RootElement.TryGetProperty("transcript", out var transcriptEl))
                {
                    var text = transcriptEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        Log($"💬 Ada: {text}");
                        OnAdaSpeaking?.Invoke(text);
                    }
                }
                break;

            case "input_audio_buffer.speech_started":
                Volatile.Write(ref _lastUserSpeechAt, NowMs());
                Log("✂️ Barge-in detected");
                OnBargeIn?.Invoke();
                break;

            case "input_audio_buffer.cleared":
                Log("🧹 Cleared OpenAI input audio buffer");
                break;

            case "conversation.item.input_audio_transcription.completed":
                if (doc.RootElement.TryGetProperty("transcript", out var userTranscript))
                {
                    var text = userTranscript.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        Log($"👤 User: {text}");
                        OnTranscript?.Invoke(text);
                    }
                }
                break;

            case "error":
                if (doc.RootElement.TryGetProperty("error", out var errorEl))
                {
                    var message = errorEl.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString()
                        : "Unknown error";
                    // Suppress benign errors
                    if (!string.IsNullOrEmpty(message) &&
                        !message.Contains("buffer too small", StringComparison.OrdinalIgnoreCase) &&
                        !message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                        Log($"❌ OpenAI error: {message}");
                }
                break;
        }
    }

    // =========================
    // TOOL HANDLING
    // =========================
    private async Task HandleToolCallAsync(JsonElement root)
    {
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
    // SESSION CONFIG
    // =========================
    private async Task ConfigureSessionAsync()
    {
        var codecName = _codec == G711Codec.ALaw ? "g711_alaw" : "g711_ulaw";
        Log($"🎧 Configuring session: format={codecName}@8kHz (NATIVE - zero resampling), voice={_voice}, server_vad");

        await SendJsonAsync(new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                voice = _voice,
                instructions = GetDefaultInstructions(),
                input_audio_format = codecName,
                output_audio_format = codecName,
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

STYLE: Be concise, warm, and professional.

GOAL: Collect (in this order): name, pickup address, destination, passengers, pickup time.

## MANDATORY TOOL USAGE

After EVERY user response that contains booking information, you MUST call sync_booking_data IMMEDIATELY with ALL fields you have collected so far.

Examples:
- User says '52A David Road' → call sync_booking_data with pickup='52A David Road'
- User says '7 Russell Street' → call sync_booking_data with destination='7 Russell Street'  
- User says 'three passengers' → call sync_booking_data with passengers=3
- User says 'now' or 'as soon as possible' → call sync_booking_data with pickup_time='ASAP'
- User gives their name → call sync_booking_data with caller_name='...'

CRITICAL: When you confirm the booking summary, call sync_booking_data with ALL collected fields BEFORE calling book_taxi.

## BOOKING FLOW

1. Greet and ask for name
2. Ask for pickup address → sync_booking_data
3. Ask for destination → sync_booking_data  
4. Ask for passengers → sync_booking_data
5. Ask for pickup time → sync_booking_data
6. Read back summary: 'So that's [passengers] from [pickup] to [destination] at [time]. Correct?'
7. On confirmation → call sync_booking_data with ALL fields, then ask 'Shall I get a price?'
8. On 'yes' → book_taxi(action=request_quote)
9. Tell fare/ETA → 'Confirm booking?'
10. On confirmation → book_taxi(action=confirmed)
11. Give reference → 'Anything else?'
12. If no → end_call
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

    /// <summary>
    /// Send initial greeting to start the conversation.
    /// </summary>
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
    // CALLED BY RTP PLAYOUT
    // =========================
    /// <summary>
    /// Called by DirectG711RtpPlayout when audio queue becomes empty.
    /// This is the ONLY source of truth for when Ada finished speaking.
    /// Do NOT call this from OpenAI events!
    /// </summary>
    public void NotifyPlayoutComplete()
    {
        Volatile.Write(ref _lastAdaFinishedAt, NowMs());
    }

    // =========================
    // LEGACY QUEUE METHODS (for pull-based playout compatibility)
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
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _sendMutex.Dispose();

        if (Interlocked.Exchange(ref _callEnded, 1) == 0)
            OnCallEnded?.Invoke();
    }
}
