using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AdaVaxVoIP.Config;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP.Ai;

/// <summary>
/// OpenAI Realtime API client with G.711 A-law passthrough for VaxVoIP integration.
/// Includes response gating, barge-in, no-reply watchdog, goodbye detection.
/// </summary>
public sealed class OpenAIRealtimeClient : IAsyncDisposable
{
    public const string VERSION = "1.0-vaxvoip";

    private readonly ILogger<OpenAIRealtimeClient> _logger;
    private readonly OpenAISettings _settings;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Thread-safe state
    private int _disposed;
    private int _responseActive;
    private int _greetingSent;
    private int _callEnded;
    private int _noReplyCount;
    private int _noReplyWatchdogId;
    private int _responseQueued;
    private int _transcriptPending;
    private int _closingScriptSpoken;
    private int _ignoreUserAudio;
    private long _lastUserSpeechAt;
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;
    private bool _awaitingConfirmation;
    private string _callerId = "";
    private string _detectedLanguage = "en";

    private readonly SemaphoreSlim _sendMutex = new(1, 1);
    private readonly ConcurrentQueue<string> _logQueue = new();

    private const int MAX_NO_REPLY_PROMPTS = 3;
    private const int NO_REPLY_TIMEOUT_MS = 15000;
    private const int CONFIRMATION_TIMEOUT_MS = 30000;

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _ws?.State == WebSocketState.Open;
    public bool IsResponseActive => Volatile.Read(ref _responseActive) == 1;

    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;
    public event Action? OnBargeIn;
    public event Action? OnResponseCompleted;

    public Func<int>? GetQueuedFrames { get; set; }
    public Func<bool>? IsAutoQuoteInProgress { get; set; }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public OpenAIRealtimeClient(ILogger<OpenAIRealtimeClient> logger, OpenAISettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public void SetAwaitingConfirmation(bool value) => _awaitingConfirmation = value;

    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        _callerId = callerId;
        _detectedLanguage = DetectLanguage(callerId);
        ResetCallState();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_settings.Model}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        _logger.LogInformation("📞 Connecting v{Version} (caller: {Caller}, lang: {Lang})", VERSION, callerId, _detectedLanguage);
        await _ws.ConnectAsync(uri, linked.Token);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        _logger.LogInformation("Connected to OpenAI (G.711 A-law mode)");
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        _ws?.Dispose(); _ws = null;
    }

    public void SendAudio(byte[] alawData)
    {
        if (!IsConnected || alawData == null || alawData.Length == 0) return;
        if (Volatile.Read(ref _ignoreUserAudio) == 1) return;

        var msg = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(alawData)
        });
        _ = SendBytesAsync(msg);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }

    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        await SendJsonAsync(new { type = "response.cancel" });
    }

    public async Task InjectMessageAndRespondAsync(string message)
    {
        if (!IsConnected) return;
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text = message } }
            }
        });
        await Task.Delay(20);
        await QueueResponseCreateAsync(delayMs: 10, bypassTranscriptGuard: true);
    }

    public void NotifyPlayoutComplete()
    {
        OnPlayoutComplete?.Invoke();
        StartNoReplyWatchdog();
    }

    public void CancelDeferredResponse() { }

    // =========================
    // INTERNAL
    // =========================

    private void ResetCallState()
    {
        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _closingScriptSpoken, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _noReplyCount, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);
        _activeResponseId = null;
        _lastCompletedResponseId = null;
        _awaitingConfirmation = false;
        Volatile.Write(ref _lastUserSpeechAt, 0);
    }

    private bool CanCreateResponse(bool bypassTranscriptGuard = false)
    {
        return Volatile.Read(ref _responseActive) == 0 &&
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               (bypassTranscriptGuard || Volatile.Read(ref _transcriptPending) == 0) &&
               (bypassTranscriptGuard || NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300);
    }

    private async Task QueueResponseCreateAsync(int delayMs = 40, bool bypassTranscriptGuard = false)
    {
        if (Volatile.Read(ref _callEnded) != 0) return;
        if (Interlocked.CompareExchange(ref _responseQueued, 1, 0) == 1) return;

        try
        {
            int waited = 0;
            while (Volatile.Read(ref _responseActive) == 1 && waited < 2000)
            { await Task.Delay(20); waited += 20; }

            if (delayMs > 0) await Task.Delay(delayMs);
            if (!CanCreateResponse(bypassTranscriptGuard)) return;
            await SendJsonAsync(new { type = "response.create" });
        }
        finally { Interlocked.Exchange(ref _responseQueued, 0); }
    }

    private void StartNoReplyWatchdog()
    {
        var timeoutMs = _awaitingConfirmation ? CONFIRMATION_TIMEOUT_MS : NO_REPLY_TIMEOUT_MS;
        var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(timeoutMs);
            if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
            if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0 || !IsConnected) return;

            if (Volatile.Read(ref _responseActive) == 1)
            {
                _logger.LogInformation("⏰ No-reply watchdog suppressed — response active, re-arming");
                StartNoReplyWatchdog();
                return;
            }

            var count = Interlocked.Increment(ref _noReplyCount);
            if (count >= MAX_NO_REPLY_PROMPTS)
            {
                _logger.LogInformation("⏰ Max no-reply — ending call");
                OnEnded?.Invoke("no_reply_timeout");
                return;
            }

            await SendJsonAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message", role = "system",
                    content = new[] { new { type = "input_text", text = "[SILENCE DETECTED] The user has not responded. Say something brief like 'Are you still there?'" } }
                }
            });
            await Task.Delay(20);
            await SendJsonAsync(new { type = "response.create" });
        });
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (IsConnected && !ct.IsCancellationRequested)
            {
                var result = await _ws!.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage) { await ProcessMessageAsync(sb.ToString()); sb.Clear(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Receive error"); }
        OnEnded?.Invoke("connection_closed");
    }

    private async Task ProcessMessageAsync(string json)
    {
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
                    if (Interlocked.CompareExchange(ref _greetingSent, 1, 0) == 0)
                        await SendGreetingAsync();
                    break;

                case "response.created":
                {
                    var rid = TryGetResponseId(doc.RootElement);
                    if (rid != null && _activeResponseId == rid) break;
                    _activeResponseId = rid;
                    Interlocked.Exchange(ref _responseActive, 1);
                    Interlocked.Increment(ref _noReplyWatchdogId);
                    _ = SendJsonAsync(new { type = "input_audio_buffer.clear" });
                    break;
                }

                case "response.done":
                {
                    var rid = TryGetResponseId(doc.RootElement);
                    if (rid == null || rid == _lastCompletedResponseId) break;
                    if (_activeResponseId != null && rid != _activeResponseId) break;
                    _lastCompletedResponseId = rid;
                    _activeResponseId = null;
                    Interlocked.Exchange(ref _responseActive, 0);
                    OnResponseCompleted?.Invoke();

                    // Check for goodbye
                    if (Volatile.Read(ref _closingScriptSpoken) == 1)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(5000);
                            if (Volatile.Read(ref _callEnded) == 0)
                                OnEnded?.Invoke("goodbye_watchdog");
                        });
                    }
                    break;
                }

                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                    {
                        var audio = Convert.FromBase64String(delta.GetString() ?? "");
                        if (audio.Length > 0) OnAudio?.Invoke(audio);
                    }
                    break;

                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var adaText))
                    {
                        var text = adaText.GetString() ?? "";
                        OnTranscript?.Invoke("Ada", text);

                        // Goodbye detection
                        var lower = text.ToLowerInvariant();
                        if (lower.Contains("goodbye") || lower.Contains("safe journey") ||
                            lower.Contains("booking confirmation over whatsapp"))
                        {
                            Interlocked.Exchange(ref _closingScriptSpoken, 1);
                            Interlocked.Exchange(ref _ignoreUserAudio, 1);
                        }
                    }
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        OnTranscript?.Invoke("User", userText.GetString() ?? "");
                        Interlocked.Exchange(ref _noReplyCount, 0);
                    }
                    Interlocked.Exchange(ref _transcriptPending, 0);
                    break;

                case "input_audio_buffer.speech_started":
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    if (Volatile.Read(ref _responseActive) == 1)
                    {
                        await SendJsonAsync(new { type = "response.cancel" });
                        OnBargeIn?.Invoke();
                    }
                    break;

                case "input_audio_buffer.speech_stopped":
                case "input_audio_buffer.committed":
                    break;

                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;

                case "error":
                    if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                        _logger.LogWarning("OpenAI error: {Error}", msg.GetString());
                    break;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error processing message"); }
    }

    private async Task HandleToolCallAsync(JsonElement root)
    {
        var callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
        var argsStr = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";

        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name)) return;

        Dictionary<string, object?> args;
        try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}") ?? new(); }
        catch { args = new(); }

        object result = OnToolCall != null
            ? await OnToolCall.Invoke(name, args)
            : new { error = "No handler" };

        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new { type = "function_call_output", call_id = callId, output = JsonSerializer.Serialize(result) }
        });

        // Wait then trigger response
        for (int i = 0; i < 50 && Volatile.Read(ref _responseActive) == 1; i++)
            await Task.Delay(50);
        await SendJsonAsync(new { type = "response.create" });
    }

    private async Task ConfigureSessionAsync()
    {
        await SendJsonAsync(new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = GetSystemPrompt(),
                voice = _settings.Voice,
                input_audio_format = "g711_alaw",
                output_audio_format = "g711_alaw",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = _settings.Temperature
            }
        });
    }

    private async Task SendGreetingAsync()
    {
        await Task.Delay(100);
        var langName = _detectedLanguage switch { "nl" => "Dutch", "fr" => "French", "de" => "German", _ => "English" };
        var greeting = _detectedLanguage switch
        {
            "nl" => "Hallo, welkom bij Taxibot. Ik ben Ada. Wat is uw naam?",
            _ => "Hello, welcome to Taxibot. I'm Ada. What's your name?"
        };
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new { modalities = new[] { "text", "audio" }, instructions = $"Greet warmly in {langName}. Say: \"{greeting}\"" }
        });
    }

    private async Task SendJsonAsync(object obj)
    {
        if (!IsConnected) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await SendBytesAsync(bytes);
    }

    private async Task SendBytesAsync(byte[] bytes)
    {
        if (!IsConnected) return;
        await _sendMutex.WaitAsync();
        try { await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? default); }
        finally { _sendMutex.Release(); }
    }

    private static string? TryGetResponseId(JsonElement root)
    {
        return root.TryGetProperty("response", out var r) && r.TryGetProperty("id", out var id)
            ? id.GetString() : null;
    }

    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function", name = "sync_booking_data",
            description = "MANDATORY: Sync booking data after EVERY user response containing booking info. Include ALL known fields.",
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
            type = "function", name = "book_taxi",
            description = "Request quote or confirm booking. MUST call with action='confirmed' BEFORE announcing success.",
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
            type = "function", name = "find_local_events",
            description = "Look up local events near a location.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    category = new { type = "string" },
                    near = new { type = "string" },
                    date = new { type = "string" }
                },
                required = new[] { "near" }
            }
        },
        new
        {
            type = "function", name = "end_call",
            description = "End the call after the FINAL CLOSING script has been spoken.",
            parameters = new { type = "object", properties = new { } }
        }
    };

    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var norm = phone.Replace(" ", "").Replace("-", "");
        if (norm.StartsWith("06") && norm.Length == 10) return "nl";
        if (norm.StartsWith("+31") || norm.StartsWith("0031")) return "nl";
        var clean = norm.StartsWith("+") ? norm[1..] : norm.StartsWith("00") ? norm[2..] : norm;
        if (clean.Length >= 2)
        {
            var code = clean[..2];
            return code switch { "31" or "32" => "nl", "33" => "fr", "49" or "43" or "41" => "de", "34" => "es", _ => "en" };
        }
        return "en";
    }

    private static string GetSystemPrompt() => """
        You are Ada, a taxi booking assistant for Voice Taxibot. Version 3.9.

        ==============================
        VOICE STYLE
        ==============================

        Speak naturally, like a friendly professional taxi dispatcher.
        - Warm, calm, confident tone
        - Clear pronunciation of names and addresses
        - Short pauses between phrases
        - Never rush or sound robotic
        - Patient and relaxed and slower pace

        ==============================
        LANGUAGE
        ==============================

        Start in English based on the caller's phone number.
        CONTINUOUSLY MONITOR the caller's spoken language.
        If they speak another language or ask to switch, IMMEDIATELY switch for all responses.
        Default to English if uncertain.

        ==============================
        BOOKING FLOW (STRICT)
        ==============================

        Follow this order exactly:

        Greet → NAME → PICKUP → DESTINATION → PASSENGERS → TIME

        After EVERY field, call sync_booking_data with ALL known fields.

        When sync_booking_data is called with all 5 fields filled, the system will
        AUTOMATICALLY calculate the fare and return it in the tool result.

        → Confirm details ONCE
        → If changes: update via sync_booking_data and confirm ONCE more
        → If correct: announce the fare using the fare_spoken value from the tool result
        → Ask "Would you like to confirm this booking?"
        → Call book_taxi(action="confirmed")
        → Give reference ID ONLY
        → Ask "Anything else?"

        If the user says NO to "anything else":
        You MUST perform the FINAL CLOSING and then call end_call.

        ==============================
        FINAL CLOSING (MANDATORY – EXACT WORDING)
        ==============================

        When the conversation is complete, say EXACTLY:
        "Thank you for using the TaxiBot system. You will shortly receive your booking confirmation over WhatsApp. Goodbye."
        Then IMMEDIATELY call end_call.

        ==============================
        CRITICAL: FRESH SESSION – NO MEMORY
        ==============================

        THIS IS A NEW CALL. You have NO prior knowledge of this caller.
        The user's MOST RECENT wording is always the source of truth.

        ==============================
        DATA COLLECTION (MANDATORY)
        ==============================

        After EVERY user message that provides OR corrects booking data:
        - Call sync_booking_data immediately with ALL known fields
        - Store addresses EXACTLY as spoken (verbatim)
        - NEVER "improve", "normalize", or "correct" addresses

        ==============================
        ADDRESS INTEGRITY (ABSOLUTE RULE)
        ==============================

        - NEVER add numbers the user did not say
        - NEVER remove numbers the user did say
        - NEVER guess missing parts
        - Read back EXACTLY what was stored

        ==============================
        HOUSE NUMBER HANDLING (CRITICAL)
        ==============================

        House numbers are NOT ranges unless the USER explicitly says so.
        1214A → spoken "twelve fourteen A"
        NEVER insert hyphens or reinterpret.

        ==============================
        ABSOLUTE RULES – VIOLATION FORBIDDEN
        ==============================

        1. You MUST call sync_booking_data after every booking-related user message
        2. You MUST call book_taxi(action="confirmed") BEFORE confirming a booking
        3. NEVER announce booking success before the tool succeeds
        4. NEVER invent a booking reference or fare
        5. If booking fails, explain clearly and ask to retry
        6. NEVER call end_call except after the FINAL CLOSING

        ==============================
        NEGATIVE CONFIRMATION HANDLING
        ==============================

        When the user says NO after a fare quote:
        - Do NOT end the call
        - Ask what they would like to change
        - Only close if they explicitly cancel

        ==============================
        RESPONSE STYLE
        ==============================

        - One question at a time
        - Under 15 words per response
        - Calm, professional, human
        - NEVER say filler phrases like "just a moment" or "please hold on"
        - Use the fare_spoken field for speech — it contains the correct currency word
        """;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendMutex.Dispose();
    }
}
