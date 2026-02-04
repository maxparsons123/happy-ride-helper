using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaMain.Config;
using Microsoft.Extensions.Logging;

namespace AdaMain.Ai;

/// <summary>
/// OpenAI Realtime API client with proper response lifecycle handling.
/// 
/// v4.4: Added Forced Audio Commit Timer for confirmation mode.
///       - When awaiting confirmation, periodically commits audio buffer (every 3s)
///       - Ensures short affirmatives ("yes", "yes please") get transcribed even if VAD fails
/// 
/// v4.3: All response.create routed through QueueResponseCreateAsync with SIP-safe delays.
///       - Echo guard increased to 500ms
///       - Deferred response flush uses gated queue
///       - Tool result delays: sync=40ms, quote=60ms, confirmed=150ms
///       - Greeting delay increased to 180ms
///       - Transcript pending guard for response creation
///       - No-reply watchdog with speech guard (3s window)
/// </summary>
public sealed class OpenAiRealtimeClient : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "4.5";

    // =========================
    // PERF: Cached JSON serializer options
    // =========================
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<OpenAiRealtimeClient> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;
    
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _keepaliveTask;
    
    private readonly SemaphoreSlim _sendMutex = new(1, 1);
    
    // =========================
    // THREAD-SAFE STATE
    // =========================
    private int _disposed;
    private int _callEnded;
    private int _responseActive;      // OBSERVED only (set only by response.created / response.done)
    private int _responseQueued;
    private int _greetingSent;
    private int _ignoreUserAudio;
    private int _deferredResponsePending;
    private int _noReplyWatchdogId;
    private int _transcriptPending;   // Block response.create until transcript arrives
    private long _lastAdaFinishedAt;
    private long _lastUserSpeechAt;
    
    // Tracks current OpenAI response id to ignore duplicate events
    private string? _activeResponseId;
    private string? _lastCompletedResponseId;
    
    private string _callerId = "";
    private string _detectedLanguage = "en";
    private bool _awaitingConfirmation;
    private int _confirmationCommitTimerId;  // v4.4: Forced audio commit timer ID
    
    public bool IsConnected => 
        Volatile.Read(ref _disposed) == 0 && 
        _ws?.State == WebSocketState.Open;
    
    public event Action<byte[]>? OnAudio;
    public event Func<string, Dictionary<string, object?>, Task<object>>? OnToolCall;
    public event Action<string>? OnEnded;
    public event Action? OnPlayoutComplete;
    public event Action<string, string>? OnTranscript;
    
    public OpenAiRealtimeClient(
        ILogger<OpenAiRealtimeClient> logger,
        OpenAiSettings settings,
        string? systemPrompt = null)
    {
        _logger = logger;
        _settings = settings;
        _systemPrompt = systemPrompt ?? GetDefaultSystemPrompt();
    }
    
    // =========================
    // RESPONSE GATE (v4.5)
    // =========================
    private bool CanCreateResponse(bool bypassTranscriptGuard = false)
    {
        // v4.5: Tool results should bypass transcript pending guard
        var transcriptCheck = bypassTranscriptGuard || Volatile.Read(ref _transcriptPending) == 0;
        
        return Volatile.Read(ref _responseActive) == 0 &&
               Volatile.Read(ref _responseQueued) == 0 &&
               transcriptCheck &&
               Volatile.Read(ref _callEnded) == 0 &&
               Volatile.Read(ref _disposed) == 0 &&
               IsConnected &&
               NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300;
    }

    /// <summary>
    /// Queue a response.create through the gate. All response.create MUST use this method.
    /// </summary>
    private async Task QueueResponseCreateAsync(int delayMs = 40, bool waitForCurrentResponse = true, int maxWaitMs = 1000, bool bypassTranscriptGuard = false)
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

            // If response is still active, defer
            if (Volatile.Read(ref _responseActive) == 1)
            {
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                _logger.LogDebug("Response still active - deferring response.create");
                return;
            }

            // v4.5: Tool results bypass transcript guard
            if (!CanCreateResponse(bypassTranscriptGuard))
            {
                _logger.LogDebug("Gate blocked response.create - deferring");
                Interlocked.Exchange(ref _deferredResponsePending, 1);
                return;
            }

            await SendJsonAsync(new { type = "response.create" }).ConfigureAwait(false);
            _logger.LogDebug("response.create sent");
        }
        finally
        {
            Interlocked.Exchange(ref _responseQueued, 0);
        }
    }
    
    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        ResetCallState(callerId);
        
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        
        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_settings.Model}");
        
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        
        _logger.LogInformation("Connecting to OpenAI v{Version} (caller: {CallerId}, lang: {Lang})", 
            VERSION, callerId, _detectedLanguage);
        await _ws.ConnectAsync(uri, linked.Token);
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _keepaliveTask = Task.Run(KeepaliveLoopAsync);
        
        _logger.LogInformation("Connected to OpenAI");
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
                    _logger.LogWarning("Keepalive: WebSocket disconnected (state: {State})", _ws?.State);
                    break;
                }

                var responseActive = Volatile.Read(ref _responseActive) == 1;
                var callEnded = Volatile.Read(ref _callEnded) == 1;
                _logger.LogDebug("Keepalive: connected (response_active={ResponseActive}, call_ended={CallEnded})", 
                    responseActive, callEnded);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keepalive loop error");
        }
    }
    
    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        _logger.LogInformation("Disconnecting from OpenAI");
        Interlocked.Exchange(ref _callEnded, 1);
        
        _cts?.Cancel();
        
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }
        
        _ws?.Dispose();
        _ws = null;
    }
    
    public void SendAudio(byte[] pcm24k)
    {
        if (!IsConnected || pcm24k.Length == 0)
            return;
        
        if (Volatile.Read(ref _ignoreUserAudio) == 1)
            return;
        
        // v4.3: Echo guard increased to 500ms (was implicit/none)
        if (!_awaitingConfirmation && NowMs() - Volatile.Read(ref _lastAdaFinishedAt) < 500)
            return;
        
        var msg = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm24k)
        }, JsonOptions);
        
        _ = SendTextAsync(msg);
        Volatile.Write(ref _lastUserSpeechAt, NowMs());
    }
    
    public async Task CancelResponseAsync()
    {
        if (!IsConnected) return;
        await SendJsonAsync(new { type = "response.cancel" });
    }
    
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
                _logger.LogError(ex, "Receive error");
                break;
            }
        }
        
        _logger.LogInformation("Receive loop ended");
        OnEnded?.Invoke("connection_closed");
    }
    
    private async Task ProcessMessageAsync(string json)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;
            
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
                    // OBSERVED start (OpenAI controlled) — only place we set _responseActive = 1
                    var responseId = TryGetResponseId(doc.RootElement);
                    if (responseId != null && _activeResponseId == responseId)
                        break; // Ignore duplicate

                    _activeResponseId = responseId;
                    Interlocked.Exchange(ref _responseActive, 1);
                    
                    // v4.3: Clear audio buffer ONLY HERE on response.created
                    _ = ClearInputBufferAsync();
                    
                    _logger.LogDebug("AI response started");
                    break;
                }
                
                case "response.done":
                {
                    // OBSERVED end (OpenAI controlled) — only place we set _responseActive = 0
                    var responseId = TryGetResponseId(doc.RootElement);

                    if (responseId == null || responseId == _lastCompletedResponseId)
                        break;

                    if (_activeResponseId != null && responseId != _activeResponseId)
                        break;

                    _lastCompletedResponseId = responseId;
                    _activeResponseId = null;

                    Interlocked.Exchange(ref _responseActive, 0);
                    Volatile.Write(ref _lastAdaFinishedAt, NowMs());
                    
                    _logger.LogDebug("AI response completed");
                    OnPlayoutComplete?.Invoke();

                    // v4.3: Flush deferred response through gate (not raw SendJsonAsync)
                    if (Interlocked.Exchange(ref _deferredResponsePending, 0) == 1)
                    {
                        _logger.LogDebug("Flushing deferred response.create (via gate)");
                        _ = Task.Run(async () =>
                        {
                            await QueueResponseCreateAsync(
                                delayMs: 80,
                                waitForCurrentResponse: false,
                                maxWaitMs: 0
                            ).ConfigureAwait(false);
                        });
                    }

                    // No-reply watchdog with speech guard
                    var watchdogDelayMs = _awaitingConfirmation ? 20000 : 15000;
                    var watchdogId = Interlocked.Increment(ref _noReplyWatchdogId);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(watchdogDelayMs).ConfigureAwait(false);

                        if (Volatile.Read(ref _noReplyWatchdogId) != watchdogId) return;
                        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
                        if (Volatile.Read(ref _responseActive) == 1) return;
                        
                        // v4.3: Transcript guard
                        if (Volatile.Read(ref _transcriptPending) == 1)
                        {
                            _logger.LogDebug("Watchdog: transcript pending - aborting");
                            return;
                        }
                        
                        // v4.3: Speech guard (3s window)
                        var msSinceLastSpeech = NowMs() - Volatile.Read(ref _lastUserSpeechAt);
                        if (msSinceLastSpeech < 3000)
                        {
                            _logger.LogDebug("Watchdog: recent speech {Ms}ms ago - aborting", msSinceLastSpeech);
                            return;
                        }

                        _logger.LogInformation("No-reply watchdog triggered ({Delay}ms) - prompting user", watchdogDelayMs);

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

                        await QueueResponseCreateAsync(delayMs: 40, waitForCurrentResponse: false).ConfigureAwait(false);
                    });
                    break;
                }
                
                case "response.audio.delta":
                    if (doc.RootElement.TryGetProperty("delta", out var delta))
                    {
                        var audio = Convert.FromBase64String(delta.GetString() ?? "");
                        if (audio.Length > 0)
                            OnAudio?.Invoke(audio);
                    }
                    break;
                
                case "response.audio_transcript.done":
                    if (doc.RootElement.TryGetProperty("transcript", out var adaText))
                        OnTranscript?.Invoke("Ada", adaText.GetString() ?? "");
                    break;
                
                case "input_audio_buffer.speech_started":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    Interlocked.Increment(ref _noReplyWatchdogId);
                    Interlocked.Exchange(ref _transcriptPending, 1);
                    break;

                case "input_audio_buffer.speech_stopped":
                    Volatile.Write(ref _lastUserSpeechAt, NowMs());
                    _ = SendJsonAsync(new { type = "input_audio_buffer.commit" });
                    _logger.LogDebug("Committed audio buffer (awaiting transcript)");
                    break;
                
                case "conversation.item.input_audio_transcription.completed":
                {
                    Interlocked.Exchange(ref _transcriptPending, 0);
                    
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                    {
                        var text = userText.GetString() ?? "";
                        OnTranscript?.Invoke("User", text);
                        
                        // Late confirmation detection
                        if (Volatile.Read(ref _responseActive) == 0 && _awaitingConfirmation)
                        {
                            var lowerText = text.ToLowerInvariant();
                            bool isConfirmation = lowerText.Contains("yes") || 
                                                  lowerText.Contains("yeah") ||
                                                  lowerText.Contains("correct") ||
                                                  lowerText.Contains("go ahead") ||
                                                  lowerText.Contains("confirm") ||
                                                  lowerText.Contains("book it");
                            
                            if (isConfirmation)
                            {
                                _logger.LogInformation("Late confirmation detected - injecting system prompt");
                                _ = HandleLateConfirmationAsync(text);
                            }
                        }
                    }
                    break;
                }
                
                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;
                
                case "error":
                    var errMsg = doc.RootElement.TryGetProperty("error", out var err) &&
                                 err.TryGetProperty("message", out var msg)
                        ? msg.GetString() : "Unknown error";
                    if (!errMsg?.Contains("buffer too small", StringComparison.OrdinalIgnoreCase) ?? false)
                        _logger.LogWarning("OpenAI error: {Error}", errMsg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
    }

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

    /// <summary>
    /// v4.4: Forced Audio Commit Timer - periodically commits audio buffer when awaiting confirmation.
    /// This ensures short affirmatives ("yes", "yes please") get transcribed even if VAD fails.
    /// </summary>
    private void StartConfirmationCommitTimer()
    {
        var timerId = Interlocked.Increment(ref _confirmationCommitTimerId);
        _logger.LogDebug("Started confirmation commit timer (3s interval)");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(3000).ConfigureAwait(false);

                // Stop conditions
                if (Volatile.Read(ref _confirmationCommitTimerId) != timerId) return;
                if (!_awaitingConfirmation) return;
                if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0) return;
                if (Volatile.Read(ref _responseActive) == 1) return;  // Don't commit while Ada is speaking

                // Check if there's pending audio (user might have spoken but VAD didn't trigger)
                var msSinceAdaFinished = NowMs() - Volatile.Read(ref _lastAdaFinishedAt);
                if (msSinceAdaFinished < 1000) continue;  // Wait at least 1s after Ada finishes

                // Force commit to trigger transcription
                _logger.LogDebug("Forced audio commit (confirmation mode)");
                await SendJsonAsync(new { type = "input_audio_buffer.commit" }).ConfigureAwait(false);
            }
        });
    }

    private void StopConfirmationCommitTimer()
    {
        Interlocked.Increment(ref _confirmationCommitTimerId);
    }

    private async Task HandleLateConfirmationAsync(string userText)
    {
        if (Volatile.Read(ref _callEnded) != 0 || Volatile.Read(ref _disposed) != 0)
            return;
        
        Interlocked.Increment(ref _noReplyWatchdogId);
        
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
                        text = $"[USER CONFIRMED BOOKING] The user said \"{userText}\". This is a confirmation. You MUST now call the book_taxi tool with action='request_quote' to get the fare quote. Do NOT speak first - call the tool immediately."
                    }
                }
            }
        }).ConfigureAwait(false);
        
        // v4.3: Route through gate (not raw SendJsonAsync)
        await QueueResponseCreateAsync(
            delayMs: 80,
            waitForCurrentResponse: false,
            maxWaitMs: 0
        ).ConfigureAwait(false);
        
        _logger.LogDebug("response.create queued (late confirmation)");
    }
    
    private async Task HandleToolCallAsync(JsonElement root)
    {
        var callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : "";
        var argsStr = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}";
        
        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
            return;
        
        _logger.LogDebug("Tool call: {Name}", name);
        
        Dictionary<string, object?> args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}", JsonOptions) ?? new();
        }
        catch
        {
            args = new();
        }
        
        object result;
        if (OnToolCall != null)
        {
            result = await OnToolCall.Invoke(name, args);
        }
        else
        {
            result = new { error = "No handler" };
        }
        
        await SendToolResultAsync(callId, result);
        
        // v4.3: Use appropriate delays based on tool type
        var delayMs = name switch
        {
            "sync_booking_data" => 40,
            "book_taxi" when args.TryGetValue("action", out var action) && action?.ToString() == "confirmed" => 150,
            "book_taxi" => 60,  // request_quote
            "end_call" => 150,
            _ => 40
        };
        
        // Update awaiting confirmation state
        if (name == "book_taxi")
        {
            var action = args.TryGetValue("action", out var a2) ? a2?.ToString() : null;
            var wasAwaiting = _awaitingConfirmation;
            _awaitingConfirmation = action == "request_quote";
            
            // v4.4: Manage confirmation commit timer
            if (_awaitingConfirmation && !wasAwaiting)
                StartConfirmationCommitTimer();
            else if (!_awaitingConfirmation && wasAwaiting)
                StopConfirmationCommitTimer();
        }
        
        await QueueResponseCreateAsync(delayMs: delayMs, waitForCurrentResponse: false, maxWaitMs: 0);
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
                output = JsonSerializer.Serialize(result, JsonOptions)
            }
        });
    }
    
    private async Task ClearInputBufferAsync()
    {
        if (_ws?.State != WebSocketState.Open) return;
        
        try
        {
            await SendJsonAsync(new { type = "input_audio_buffer.clear" });
            _logger.LogDebug("Cleared OpenAI input audio buffer");
        }
        catch { }
    }
    
    private async Task ConfigureSessionAsync()
    {
        var config = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = _systemPrompt,
                voice = _settings.Voice,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new 
                { 
                    model = "whisper-1",
                    language = _detectedLanguage
                },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.4,        // v4.3: Matched to WinForms tuning
                    prefix_padding_ms = 450,
                    silence_duration_ms = 900
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };
        
        await SendJsonAsync(config);
        _logger.LogInformation("Session configured (lang={Lang})", _detectedLanguage);
    }
    
    private async Task SendGreetingAsync()
    {
        // v4.3: Increased from 100ms to 180ms to let session + SIP fully stabilize
        await Task.Delay(180);
        
        var greeting = GetLocalizedGreeting(_detectedLanguage);
        
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = $"Greet warmly in {GetLanguageName(_detectedLanguage)}. Say: \"{greeting}\""
            }
        });
        
        _logger.LogInformation("Greeting sent");
    }
    
    private async Task SendJsonAsync(object obj)
    {
        if (!IsConnected) return;
        var json = JsonSerializer.Serialize(obj, JsonOptions);
        await SendTextAsync(json);
    }
    
    private async Task SendTextAsync(string text)
    {
        if (!IsConnected) return;
        
        await _sendMutex.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, 
                _cts?.Token ?? default);
        }
        finally
        {
            _sendMutex.Release();
        }
    }

    private void ResetCallState(string? caller)
    {
        _callerId = caller ?? "";
        _detectedLanguage = DetectLanguage(caller);
        _awaitingConfirmation = false;

        Interlocked.Exchange(ref _callEnded, 0);
        Interlocked.Exchange(ref _responseActive, 0);
        Interlocked.Exchange(ref _responseQueued, 0);
        Interlocked.Exchange(ref _greetingSent, 0);
        Interlocked.Exchange(ref _transcriptPending, 0);
        Interlocked.Exchange(ref _ignoreUserAudio, 0);
        Interlocked.Exchange(ref _deferredResponsePending, 0);
        Interlocked.Increment(ref _noReplyWatchdogId);

        _activeResponseId = null;
        _lastCompletedResponseId = null;

        Volatile.Write(ref _lastAdaFinishedAt, 0);
        Volatile.Write(ref _lastUserSpeechAt, 0);
    }
    
    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "Sync booking data after user provides information",
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
            description = "Request quote or confirm booking",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                    caller_name = new { type = "string" },
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
    
    private static string DetectLanguage(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "en";
        var clean = phone.Replace(" ", "").Replace("-", "");
        if (clean.StartsWith("06") || clean.StartsWith("+31") || clean.StartsWith("0031"))
            return "nl";
        return "en";
    }
    
    private static string GetLocalizedGreeting(string lang) => lang switch
    {
        "nl" => "Hallo, welkom bij Taxibot. Ik ben Ada. Wat is uw naam?",
        _ => "Hello, welcome to Taxibot. I'm Ada. What's your name?"
    };
    
    private static string GetLanguageName(string lang) => lang switch
    {
        "nl" => "Dutch",
        _ => "English"
    };

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    private static string GetDefaultSystemPrompt() => """
        You are Ada, a friendly taxi booking assistant. You help callers book taxis efficiently.
        
        FLOW:
        1. Get caller's name
        2. Get pickup address
        3. Get destination
        4. Ask about passengers (default 1)
        5. Ask about pickup time (default "now")
        6. Call sync_booking_data after each piece of info
        7. Recap and call book_taxi with action="request_quote"
        8. After confirmation, call book_taxi with action="confirmed"
        9. Say goodbye and call end_call
        
        RULES: Keep responses brief (under 20 words). Be warm but efficient. Always quote prices in £ (GBP).
        """;
    
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendMutex.Dispose();
    }
}
