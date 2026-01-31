using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TaxiSipBridge.Audio;

namespace TaxiSipBridge;

/// <summary>
/// Production-grade OpenAI Realtime API client for SIP-to-AI telephony.
/// Thread-safe, .NET 6+ compatible, with response guards to prevent duplicate responses.
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    public enum OutputCodecMode { Opus, ALaw, MuLaw }

    // Configuration
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);
    private OutputCodecMode _outputCodec = OutputCodecMode.MuLaw;

    // Thread-safe flags (Interlocked)
    private int _disposedFlag;
    private int _responseActiveFlag;
    private int _greetingSentFlag;
    private int _postBookingHangupArmedFlag;
    private int _callEndSignaledFlag;

    // Thread-safe timestamps (Volatile)
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;

    // Mutable state
    private readonly object _lock = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _callerId = "";
    private string _detectedLanguage = "en";
    private readonly BookingState _booking = new();
    private bool _awaitingConfirmation;
    private short[] _opusBuffer = Array.Empty<short>();

    // Audio pipeline
    private readonly ConcurrentQueue<byte[]> _outboundQueue = new();
    private const int MAX_QUEUE_FRAMES = 800;
    private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Create(160, 100);
    private readonly ArrayPool<short> _shortPool = ArrayPool<short>.Create(960, 50);

    // Events
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

    // Properties
    public bool IsConnected => Volatile.Read(ref _disposedFlag) == 0 && _ws?.State == WebSocketState.Open;
    public int PendingFrameCount => _outboundQueue.Count;
    public OutputCodecMode OutputCodec { get => _outputCodec; set => _outputCodec = value; }

    public OpenAIRealtimeClient(
        string apiKey,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17",
        string voice = "shimmer")
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
    }

    public OpenAIRealtimeClient(string apiKey, OutputCodecMode outputCodec,
        string model = "gpt-4o-mini-realtime-preview-2024-12-17", string voice = "shimmer")
        : this(apiKey, model, voice)
    {
        _outputCodec = outputCodec;
    }

    #region Audio Input

    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData.Length == 0) return;

        // Echo guard: ignore audio shortly after AI finishes speaking
        if (!_awaitingConfirmation && GetUnixTimeMs() - Volatile.Read(ref _lastAdaFinishedAt) < 300)
            return;

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        var pcm24k = Resample8kTo24k(pcm8k);
        await SendAudioToOpenAIAsync(pcm24k);
        Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || pcmData.Length == 0) return;

        byte[] pcm24k = sampleRate == 24000
            ? pcmData
            : AudioCodecs.ShortsToBytes(AudioCodecs.Resample(AudioCodecs.BytesToShorts(pcmData), sampleRate, 24000));

        await SendAudioToOpenAIAsync(pcm24k);
        Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
    }

    private async Task SendAudioToOpenAIAsync(byte[] pcm24k)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm24k) });
            await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, _cts?.Token ?? default);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    #endregion

    #region Audio Output

    public byte[]? GetNextMuLawFrame() => _outboundQueue.TryDequeue(out var f) ? f : null;
    public void ClearPendingFrames() { while (_outboundQueue.TryDequeue(out _)) { } }

    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var pcm24k = Convert.FromBase64String(base64);
            OnPcm24Audio?.Invoke(pcm24k);

            var samples = AudioCodecs.BytesToShorts(pcm24k);
            switch (_outputCodec)
            {
                case OutputCodecMode.Opus: ProcessOpusOutput(samples); break;
                case OutputCodecMode.ALaw: ProcessNarrowbandOutput(samples, false); break;
                case OutputCodecMode.MuLaw: ProcessNarrowbandOutput(samples, true); break;
            }
        }
        catch (Exception ex) { Log($"⚠️ Audio error: {ex.Message}"); }
    }

    private void ProcessNarrowbandOutput(short[] pcm24k, bool muLaw)
    {
        var len = pcm24k.Length / 3;
        var pcm8k = _shortPool.Rent(len);

        try
        {
            for (int i = 0; i < len; i++)
            {
                int idx = i * 3;
                pcm8k[i] = (short)((pcm24k[idx] * 2 + pcm24k[idx + 2]) / 3);
            }

            var encoded = muLaw ? AudioCodecs.MuLawEncode(pcm8k, 0, len) : AudioCodecs.ALawEncode(pcm8k, 0, len);
            var silence = muLaw ? (byte)0xFF : (byte)0xD5;

            for (int i = 0; i < encoded.Length; i += 160)
            {
                var frame = _bytePool.Rent(160);
                var count = Math.Min(160, encoded.Length - i);
                Array.Copy(encoded, i, frame, 0, count);
                if (count < 160) Array.Fill(frame, silence, count, 160 - count);
                EnqueueFrame(frame);
            }
        }
        finally { _shortPool.Return(pcm8k); }
    }

    private void ProcessOpusOutput(short[] pcm24k)
    {
        // Upsample 24kHz → 48kHz with linear interpolation
        var pcm48k = new short[pcm24k.Length * 2];
        for (int i = 0; i < pcm24k.Length; i++)
        {
            pcm48k[i * 2] = pcm24k[i];
            pcm48k[i * 2 + 1] = i < pcm24k.Length - 1 ? (short)((pcm24k[i] + pcm24k[i + 1]) / 2) : pcm24k[i];
        }

        lock (_lock)
        {
            var newBuf = new short[_opusBuffer.Length + pcm48k.Length];
            Array.Copy(_opusBuffer, newBuf, _opusBuffer.Length);
            Array.Copy(pcm48k, 0, newBuf, _opusBuffer.Length, pcm48k.Length);
            _opusBuffer = newBuf;
        }

        while (_opusBuffer.Length >= 960)
        {
            var frame = new short[960];
            Array.Copy(_opusBuffer, 0, frame, 0, 960);
            EnqueueFrame(AudioCodecs.OpusEncode(frame));

            var remaining = new short[_opusBuffer.Length - 960];
            Array.Copy(_opusBuffer, 960, remaining, 0, remaining.Length);
            _opusBuffer = remaining;
        }
    }

    private void EnqueueFrame(byte[] frame)
    {
        while (_outboundQueue.Count >= MAX_QUEUE_FRAMES) _outboundQueue.TryDequeue(out _);
        _outboundQueue.Enqueue(frame);
    }

    private byte[] Resample8kTo24k(short[] pcm8k)
    {
        var pcm24k = new short[pcm8k.Length * 3];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            pcm24k[i * 3] = pcm8k[i];
            pcm24k[i * 3 + 1] = pcm8k[i];
            pcm24k[i * 3 + 2] = pcm8k[i];
        }
        return AudioCodecs.ShortsToBytes(pcm24k);
    }

    #endregion

    #region Connection Lifecycle

    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) throw new ObjectDisposedException(nameof(OpenAIRealtimeClient));

        ResetCallState(caller);
        Log($"📞 Connecting (caller: {caller ?? "unknown"}, lang: {_detectedLanguage})");

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        await _ws.ConnectAsync(new Uri($"wss://api.openai.com/v1/realtime?model={_model}"), linked.Token);
        Log($"✅ Connected to OpenAI");

        OnConnected?.Invoke();
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;

        Log("📴 Disconnecting...");
        SignalCallEnded("disconnect");

        try
        {
            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        finally
        {
            _ws?.Dispose();
            _ws = null;
            OnDisconnected?.Invoke();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        while (IsConnected && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    await ProcessMessageAsync(sb.ToString());
                    sb.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"❌ Receive error: {ex.Message}"); break; }
        }

        Log("🔄 Receive loop ended");
        _ws?.Dispose();
        _ws = null;
        OnDisconnected?.Invoke();
    }

    #endregion

    #region Message Processing

    private async Task ProcessMessageAsync(string json)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

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
                    if (doc.RootElement.TryGetProperty("delta", out var d))
                        OnAdaSpeaking?.Invoke(d.GetString() ?? "");
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
                    {
                        Log($"💬 Ada: {t.GetString()}");
                        OnTranscript?.Invoke($"Ada: {t.GetString()}");
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var u) && !string.IsNullOrWhiteSpace(u.GetString()))
                    {
                        var corrected = ApplySttCorrections(u.GetString()!);
                        Log($"👤 User: {corrected}");
                        OnTranscript?.Invoke($"You: {corrected}");
                    }
                    break;

                case "input_audio_buffer.speech_started":
                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, GetUnixTimeMs());
                    break;

                case "response.done":
                    Interlocked.Exchange(ref _responseActiveFlag, 0);
                    Volatile.Write(ref _lastAdaFinishedAt, GetUnixTimeMs());
                    OnResponseCompleted?.Invoke();
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var e) &&
                        e.TryGetProperty("message", out var m) &&
                        !m.GetString()!.Contains("buffer too small"))
                        Log($"❌ OpenAI: {m.GetString()}");
                    break;
            }
        }
        catch (Exception ex) { Log($"⚠️ Parse error: {ex.Message}"); }
    }

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
                turn_detection = new { type = "server_vad", threshold = 0.35, prefix_padding_ms = 600, silence_duration_ms = 1200 },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };

        await SendJsonAsync(config);
        Log("🎧 Session configured");
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(150);

        if (Volatile.Read(ref _responseActiveFlag) == 1) return;

        var greeting = GetLocalizedGreeting(_detectedLanguage);
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Greet the caller warmly in {GetLanguageName(_detectedLanguage)}. Say: \"{greeting}\""
            }
        });
        Log("📢 Greeting triggered");
    }

    #endregion

    #region Tool Handling

    private async Task HandleToolCallAsync(JsonElement root)
    {
        if (GetUnixTimeMs() - Volatile.Read(ref _lastToolCallAt) < 200) return;
        Volatile.Write(ref _lastToolCallAt, GetUnixTimeMs());

        if (!root.TryGetProperty("name", out var n) || !root.TryGetProperty("call_id", out var c)) return;

        var name = n.GetString();
        var callId = c.GetString()!;
        var args = ParseArgs(root);

        Log($"🔧 Tool: {name}");

        switch (name)
        {
            case "sync_booking_data":
                await HandleSyncAsync(callId, args);
                break;
            case "book_taxi":
                await HandleBookTaxiAsync(callId, args);
                break;
            case "end_call":
                await HandleEndCallAsync(callId);
                break;
            default:
                await SendToolResultAsync(callId, new { error = $"Unknown: {name}" });
                break;
        }
    }

    private async Task HandleSyncAsync(string callId, Dictionary<string, object> args)
    {
        lock (_lock)
        {
            if (args.TryGetValue("caller_name", out var nm)) _booking.Name = nm?.ToString();
            if (args.TryGetValue("pickup", out var p)) _booking.Pickup = p?.ToString();
            if (args.TryGetValue("destination", out var d)) _booking.Destination = d?.ToString();
            if (args.TryGetValue("passengers", out var pax) && int.TryParse(pax?.ToString(), out var n)) _booking.Passengers = n;
            if (args.TryGetValue("pickup_time", out var t)) _booking.PickupTime = t?.ToString();
        }

        OnBookingUpdated?.Invoke(_booking);
        await SendToolResultAsync(callId, new { success = true });
    }

    private async Task HandleBookTaxiAsync(string callId, Dictionary<string, object> args)
    {
        var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;

        if (action == "request_quote")
        {
            var (fare, eta, _) = await FareCalculator.CalculateFareAsync(_booking.Pickup, _booking.Destination);
            lock (_lock) { _booking.Fare = fare; _booking.Eta = eta; _awaitingConfirmation = true; }

            OnBookingUpdated?.Invoke(_booking);
            Log($"💰 Quote: {fare}");

            await SendToolResultAsync(callId, new { success = true, fare, eta, message = $"Fare is {fare}, driver arrives in {eta}. Book it?" });
        }
        else if (action == "confirmed")
        {
            lock (_lock)
            {
                _booking.Confirmed = true;
                _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                _awaitingConfirmation = false;
                Interlocked.Exchange(ref _postBookingHangupArmedFlag, 1);
            }

            OnBookingUpdated?.Invoke(_booking);
            Log($"✅ Booked: {_booking.BookingRef}");

            _ = WhatsAppNotifier.SendAsync(_callerId);

            await SendToolResultAsync(callId, new
            {
                success = true,
                booking_ref = _booking.BookingRef,
                message = string.IsNullOrWhiteSpace(_booking.Name)
                    ? "Your taxi is booked!"
                    : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
            });

            // Force hangup after 3s if AI doesn't call end_call
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                for (int i = 0; i < 20 && Volatile.Read(ref _responseActiveFlag) == 1; i++) await Task.Delay(100);
                if (Volatile.Read(ref _callEndSignaledFlag) == 0)
                {
                    await SendJsonAsync(new
                    {
                        type = "conversation.item.create",
                        item = new { type = "message", role = "system", content = new[] { new { type = "input_text", text = "[BOOKING COMPLETE] Say goodbye and call end_call NOW." } } }
                    });
                    await SendJsonAsync(new { type = "response.create" });
                }
            });
        }
    }

    private async Task HandleEndCallAsync(string callId)
    {
        Log("📞 End call requested");
        await SendToolResultAsync(callId, new { success = true });
        SignalCallEnded("end_call");
    }

    private async Task SendToolResultAsync(string callId, object result)
    {
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new { type = "function_call_output", call_id = callId, output = JsonSerializer.Serialize(result) }
        });

        // Only trigger response if none active
        if (Volatile.Read(ref _responseActiveFlag) == 0)
            await SendJsonAsync(new { type = "response.create" });
    }

    #endregion

    #region Utilities

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? default);
        }
        catch { }
    }

    private void ResetCallState(string? caller)
    {
        ClearPendingFrames();
        _opusBuffer = Array.Empty<short>();
        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);
        _booking.Reset();
        _awaitingConfirmation = false;

        Interlocked.Exchange(ref _greetingSentFlag, 0);
        Interlocked.Exchange(ref _responseActiveFlag, 0);
        Interlocked.Exchange(ref _postBookingHangupArmedFlag, 0);
        Interlocked.Exchange(ref _callEndSignaledFlag, 0);
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

    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var clean = phone.Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("06") && clean.Length == 10) return "nl";
        if (clean.StartsWith("+31") || clean.StartsWith("0031")) return "nl";
        if (clean.StartsWith("+33") || clean.StartsWith("0033")) return "fr";
        if (clean.StartsWith("+49") || clean.StartsWith("0049")) return "de";
        return "en";
    }

    private static string ApplySttCorrections(string text)
    {
        var t = text.Trim();
        return t switch
        {
            "ASAP" or "as soon as possible" or "right now" => "now",
            "yep" or "yup" or "yeah" or "yeah please" => "yes",
            "that's right" or "correct" or "go ahead" or "book it" => "yes",
            _ => t
        };
    }

    private string GetSystemPrompt() => $@"You are Ada, a taxi booking assistant. Speak in {GetLanguageName(_detectedLanguage)}.

FLOW: Greet → Ask NAME → PICKUP → DESTINATION → PASSENGERS → TIME → book_taxi(request_quote) → Confirm → book_taxi(confirmed) → Thank → end_call

RULES: One question at a time. Under 20 words. Use £. Call end_call after booking.";

    private static string GetLanguageName(string c) => c switch { "nl" => "Dutch", "fr" => "French", "de" => "German", _ => "English" };

    private static string GetLocalizedGreeting(string lang) => lang switch
    {
        "nl" => "Hallo, welkom bij Taxibot. Ik ben Ada. Wat is uw naam?",
        "fr" => "Bonjour, bienvenue chez Taxibot. Je suis Ada. Comment vous appelez-vous?",
        "de" => "Hallo, willkommen bei Taxibot. Ich bin Ada. Wie heißen Sie?",
        _ => "Hello, welcome to Taxibot. I'm Ada. What's your name?"
    };

    private static long GetUnixTimeMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Dictionary<string, object> ParseArgs(JsonElement root)
    {
        var args = new Dictionary<string, object>();
        if (!root.TryGetProperty("arguments", out var a) || string.IsNullOrEmpty(a.GetString())) return args;
        try
        {
            using var doc = JsonDocument.Parse(a.GetString()!);
            foreach (var p in doc.RootElement.EnumerateObject())
                args[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : p.Value.ToString() ?? "";
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

    private void Log(string msg)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine($"[OpenAI] {line}");
        OnLog?.Invoke(line);
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposedFlag, 1, 0) == 1) return;
        SignalCallEnded("disposed");
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}

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
        Name = Pickup = Destination = PickupTime = Fare = Eta = BookingRef = null;
        Passengers = null;
        Confirmed = false;
    }
}
