using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Minimal OpenAI Realtime API client with proper response gating.
/// </summary>
public sealed class OpenAIRealtimeClient : IAudioAIClient, IDisposable
{
    public const string VERSION = "1.6";
    // =========================
    // CONFIG
    // =========================
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _systemPrompt;
    private readonly string? _dispatchWebhookUrl;

    // =========================
    // THREAD-SAFE STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;
    private int _responseQueued;
    private int _greetingSent;
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    private long _lastToolCallAt;

    // =========================
    // CALL STATE
    // =========================
    private string _callerId = "";
    private string _detectedLanguage = "en";
    private readonly BookingState _booking = new();
    private bool _awaitingConfirmation;
    private string? _activeResponseId;

    // =========================
    // WS + LIFECYCLE
    // =========================
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

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
        string? systemPrompt = null,
        string? dispatchWebhookUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = model;
        _voice = voice;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
        _dispatchWebhookUrl = dispatchWebhookUrl;
    }

    // =========================
    // RESPONSE GATE (THE FIX)
    // =========================
    private bool CanCreateResponse()
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               Volatile.Read(ref _responseQueued) == 0 &&
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0;
    }

    /// <summary>
    /// Queue a response.create, waiting for any active response to complete first.
    /// This is critical for tool calls - we must wait for response.done before triggering.
    /// </summary>
    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for any active response to complete (up to 5s)
                if (waitForCurrentResponse)
                {
                    for (int i = 0; i < 100 && Volatile.Read(ref _responseActive) == 1; i++)
                        await Task.Delay(50);
                }

                await Task.Delay(delayMs);

                if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
                    return;

                Interlocked.Exchange(ref _responseActive, 1);
                await SendJsonAsync(new { type = "response.create" });
                Log("🔄 response.create sent (post-tool)");
            }
            finally
            {
                Interlocked.Exchange(ref _responseQueued, 0);
            }
        });
    }

    // =========================
    // AUDIO INPUT
    // =========================
    public async Task SendMuLawAsync(byte[] ulawData)
    {
        if (!IsConnected || ulawData.Length == 0) return;

        // Echo guard: skip audio right after AI speaks
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 300)
            return;

        var pcm8k = AudioCodecs.MuLawDecode(ulawData);
        var pcm24k = Resample8kTo24k(pcm8k);
        await SendAudioAsync(pcm24k);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    public async Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
    {
        if (!IsConnected || pcmData.Length == 0) return;

        byte[] pcm24k = sampleRate == 24000
            ? pcmData
            : AudioCodecs.ShortsToBytes(AudioCodecs.Resample(AudioCodecs.BytesToShorts(pcmData), sampleRate, 24000));

        await SendAudioToOpenAIAsync(pcm24k);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    private async Task SendAudioToOpenAIAsync(byte[] pcm24k)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            var msg = JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm24k) });
            await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, _cts?.Token ?? default);
        }
        catch { }
    }

    // =========================
    // AUDIO OUTPUT
    // =========================
    public byte[]? GetNextMuLawFrame() => _outboundQueue.TryDequeue(out var f) ? f : null;
    public void ClearPendingFrames() { while (_outboundQueue.TryDequeue(out _)) { } }

    private void ProcessAudioDelta(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;

        try
        {
            var pcm24k = Convert.FromBase64String(base64);
            OnPcm24Audio?.Invoke(pcm24k);

            // Downsample 24kHz → 8kHz and encode to µ-law
            var samples = AudioCodecs.BytesToShorts(pcm24k);
            var len = samples.Length / 3;
            var pcm8k = new short[len];

            for (int i = 0; i < len; i++)
            {
                int idx = i * 3;
                pcm8k[i] = (short)((samples[idx] * 2 + samples[idx + 2]) / 3);
            }

            var ulaw = AudioCodecs.MuLawEncode(pcm8k);

            // Frame into 160-byte (20ms) chunks
            for (int i = 0; i < ulaw.Length; i += 160)
            {
                var frame = new byte[160];
                var count = Math.Min(160, ulaw.Length - i);
                Array.Copy(ulaw, i, frame, 0, count);
                if (count < 160) Array.Fill(frame, (byte)0xFF, count, 160 - count);

                while (_outboundQueue.Count >= MAX_QUEUE_FRAMES)
                    _outboundQueue.TryDequeue(out _);
                _outboundQueue.Enqueue(frame);
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Audio error: {ex.Message}");
        }
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

    // =========================
    // CONNECT / DISCONNECT
    // =========================
    public async Task ConnectAsync(string? caller = null, CancellationToken ct = default)
    {
        ResetCallState(caller);

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        Log($"📞 Connecting (caller: {caller ?? "unknown"}, lang: {_detectedLanguage})");

        await _ws.ConnectAsync(new Uri($"wss://api.openai.com/v1/realtime?model={_model}"), ct);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Log("✅ Connected to OpenAI");
        OnConnected?.Invoke();

        _ = Task.Run(ReceiveLoopAsync);
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
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    await ProcessMessageAsync(sb.ToString());
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
        _ws?.Dispose();
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
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "session.created":
                    await ConfigureSessionAsync();
                    break;

                case "session.updated":
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                        await SendGreetingAsync();
                    break;

                case "response.created":
                {
                    var responseId = doc.RootElement.GetProperty("response").GetProperty("id").GetString();
                    if (_activeResponseId == responseId) break; // Ignore duplicate
                    _activeResponseId = responseId;
                    Interlocked.Exchange(ref _responseActive, 1);
                    Log("🤖 AI response started");
                    OnResponseStarted?.Invoke();
                    break;
                }

                case "response.audio.delta":
                    var delta = doc.RootElement.GetProperty("delta").GetString();
                    if (!string.IsNullOrEmpty(delta))
                        ProcessAudioDelta(delta);
                    break;

                case "response.audio_transcript.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var d))
                        OnAdaSpeaking?.Invoke(d.GetString() ?? "");
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
                    {
                        var text = t.GetString()!;
                        Log($"💬 Ada: {text}");
                        OnTranscript?.Invoke($"Ada: {text}");
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var u) && !string.IsNullOrWhiteSpace(u.GetString()))
                    {
                        var text = ApplySttCorrections(u.GetString()!);
                        Log($"👤 User: {text}");
                        OnTranscript?.Invoke($"You: {text}");
                    }
                    break;

                case "input_audio_buffer.speech_started":
                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    break;

                case "response.done":
                {
                    var responseId = doc.RootElement.GetProperty("response").GetProperty("id").GetString();
                    if (_activeResponseId != responseId) break; // Ignore stale/duplicate
                    _activeResponseId = null;
                    Interlocked.Exchange(ref _responseActive, 0);
                    Volatile.Write(ref _lastAdaFinishedAt, NowMs());
                    Log("🤖 AI response completed");
                    OnResponseCompleted?.Invoke();
                    break;
                }

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
        catch (Exception ex)
        {
            Log($"⚠️ Parse error: {ex.Message}");
        }
    }

    // =========================
    // TOOL HANDLING
    // =========================
    private async Task HandleToolCallAsync(JsonElement root)
    {
        if (NowMs() - Volatile.Read(ref _lastToolCallAt) < 200)
            return;
        Volatile.Write(ref _lastToolCallAt, NowMs());

        if (!root.TryGetProperty("name", out var n) || !root.TryGetProperty("call_id", out var c))
            return;

        var name = n.GetString();
        var callId = c.GetString()!;
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
                await SendToolResultAsync(callId, new { success = true });
                await QueueResponseCreateAsync();
                break;

            case "book_taxi":
                var action = args.TryGetValue("action", out var a) ? a?.ToString() : null;
                if (action == "request_quote")
                {
                    var (fare, eta, _) = await FareCalculator.CalculateFareAsync(_booking.Pickup, _booking.Destination);
                    _booking.Fare = fare;
                    _booking.Eta = eta;
                    _awaitingConfirmation = true;
                    OnBookingUpdated?.Invoke(_booking);
                    Log($"💰 Quote: {fare}");
                    await SendToolResultAsync(callId, new { success = true, fare, eta, message = $"Fare is {fare}, driver arrives in {eta}. Book it?" });
                    await QueueResponseCreateAsync();
                }
                else if (action == "confirmed")
                {
                    _booking.Confirmed = true;
                    _booking.BookingRef = $"TAXI-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    _awaitingConfirmation = false;
                    OnBookingUpdated?.Invoke(_booking);
                    Log($"✅ Booked: {_booking.BookingRef}");
                    _ = SendWhatsAppNotificationAsync(_callerId);
                    await SendToolResultAsync(callId, new
                    {
                        success = true,
                        booking_ref = _booking.BookingRef,
                        message = string.IsNullOrWhiteSpace(_booking.Name)
                            ? "Your taxi is booked!"
                            : $"Thanks {_booking.Name.Trim()}, your taxi is booked!"
                    });
                    await QueueResponseCreateAsync();

                    // Extended grace period (15s) to allow "anything else?" flow
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(15000);
                        for (int i = 0; i < 20 && Volatile.Read(ref _responseActive) == 1; i++) await Task.Delay(100);
                        if (Volatile.Read(ref _callEnded) == 0 && CanCreateResponse())
                        {
                            await SendJsonAsync(new
                            {
                                type = "conversation.item.create",
                                item = new { type = "message", role = "system", content = new[] { new { type = "input_text", text = "[TIMEOUT] Say 'Thank you for using the Voice Taxibot system. Goodbye!' and call end_call NOW." } } }
                            });
                            Interlocked.Exchange(ref _responseActive, 1);
                            await SendJsonAsync(new { type = "response.create" });
                        }
                    });
                }
                break;

            case "end_call":
                Log("📞 End call requested - waiting for audio buffer to drain...");
                await SendToolResultAsync(callId, new { success = true });
                
                // Wait for audio buffer to drain (max 10 seconds) before signaling end
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
                        await Task.Delay(CHECK_INTERVAL_MS);
                    }
                    
                    if (!_outboundQueue.IsEmpty)
                        Log($"⚠️ Buffer still has {_outboundQueue.Count} frames, ending anyway");
                    
                    SignalCallEnded("end_call");
                });
                break;

            default:
                await SendToolResultAsync(callId, new { error = $"Unknown: {name}" });
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
        });
    }

    // =========================
    // SESSION SETUP
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

        if (!CanCreateResponse())
        {
            Log("⏳ Skipping greeting — response already active");
            return;
        }

        var greeting = GetLocalizedGreeting(_detectedLanguage);
        Interlocked.Exchange(ref _responseActive, 1);

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

    // =========================
    // UTILITIES
    // =========================
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

    private void SignalCallEnded(string reason)
    {
        if (Interlocked.Exchange(ref _callEnded, 1) == 1)
            return;
        Log($"📴 Call ended: {reason}");
        OnCallEnded?.Invoke();
    }

    private async Task SendWhatsAppNotificationAsync(string? phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
        {
            Log("⚠️ WhatsApp notification skipped - no phone number");
            return;
        }
        try
        {
            var cli = FormatPhoneForWhatsApp(phoneNumber);
            var (success, msg) = await WhatsAppNotifier.SendAsync(cli);
            Log($"📱 WhatsApp notification to {cli}: {(success ? "OK" : "FAIL")} {msg}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ WhatsApp notification error: {ex.Message}");
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
            return _sttCorrectionsMap;
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

        Interlocked.Exchange(ref _disposed, 0);
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
        Volatile.Write(ref _lastToolCallAt, 0);
    }

    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var clean = phone.Replace(" ", "").Replace("-", "");
        clean = new string(clean.Where(c => char.IsDigit(c) || c == '+').ToArray());
        
        // Convert + or 00 prefix to just digits
        if (clean.StartsWith("+")) clean = clean.Substring(1);
        if (clean.StartsWith("00")) clean = clean.Substring(2);
        
        // Dutch local format
        if (clean.StartsWith("06") && clean.Length == 10) return "nl";
        if (clean.StartsWith("0") && clean.Length == 10) return "nl";
        
        // Check country codes
        foreach (var kv in CountryCodeToLanguage)
        {
            if (clean.StartsWith(kv.Key)) return kv.Value;
        }
        return "en";
    }

    private static string ApplySttCorrections(string text)
    {
        var t = text.Trim();
        return SttCorrections.TryGetValue(t, out var corrected) ? corrected : t;
    }

    private string GetSystemPrompt() => $@"You are Ada, a taxi booking assistant. Speak in {GetLanguageName(_detectedLanguage)}.

FLOW: Greet → Ask NAME → PICKUP → DESTINATION → PASSENGERS → TIME → SUMMARIZE journey ('So that's [passengers] passenger(s) from [pickup] to [destination] at [time]. Is that correct?') → If user wants changes: update the field and re-summarize → If correct: 'Shall I get you a price?' → book_taxi(request_quote) → Tell fare and ask to confirm → book_taxi(confirmed) → Give booking ID and REPEAT summary ('Your taxi is booked! [passengers] passenger(s) from [pickup] to [destination] at [time]. Reference [ID].') → Ask 'Is there anything else?' → If no: 'Thank you for using the Voice Taxibot system. Goodbye!' → end_call

CORRECTIONS: If user says 'change pickup to X' or 'actually it's Y passengers' or gives a new address/time, update that field immediately and give a new summary. Always re-confirm after any change.

RULES: One question at a time. Under 25 words per response. Use £. ALWAYS recite addresses in summaries. Only call end_call after user says no to 'anything else'.";

    private static string GetDefaultSystemPrompt() => "You are Ada, a professional taxi booking assistant.";

    private static string GetLanguageName(string c) => c switch { "nl" => "Dutch", "fr" => "French", "de" => "German", _ => "English" };

    private static string GetLocalizedGreeting(string lang) => 
        LocalizedGreetings.TryGetValue(lang, out var greeting) ? greeting : LocalizedGreetings["en"];

    private static string FormatPhoneForWhatsApp(string phone) => WhatsAppNotifier.FormatPhone(phone);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        if (Volatile.Read(ref _disposed) != 0) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine($"[OpenAI] {line}");
        OnLog?.Invoke(line);
    }

    // =========================
    // DISPOSAL
    // =========================
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        SignalCallEnded("disposed");
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
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
