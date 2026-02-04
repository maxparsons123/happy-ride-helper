using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AdaMain.Config;
using Microsoft.Extensions.Logging;

namespace AdaMain.Ai;

/// <summary>
/// OpenAI Realtime API client with proper response lifecycle handling.
/// Version 2.1 - Strict booking enforcement + natural voice style
/// </summary>
public sealed class OpenAiRealtimeClient : IOpenAiClient, IAsyncDisposable
{
    public const string VERSION = "4.3";
    
    private readonly ILogger<OpenAiRealtimeClient> _logger;
    private readonly OpenAiSettings _settings;
    private readonly string _systemPrompt;
    
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    
    private readonly SemaphoreSlim _sendMutex = new(1, 1);
    
    private int _disposed;
    private int _responseActive;
    private int _greetingSent;
    private string _callerId = "";
    private string _detectedLanguage = "en";
    
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    
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
    
    public async Task ConnectAsync(string callerId, CancellationToken ct = default)
    {
        _callerId = callerId;
        _detectedLanguage = DetectLanguage(callerId);
        
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        
        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={_settings.Model}");
        
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        
        _logger.LogInformation("Connecting to OpenAI v{Version} (caller: {CallerId}, lang: {Lang})", VERSION, callerId, _detectedLanguage);
        await _ws.ConnectAsync(uri, linked.Token);
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(ReceiveLoopAsync);
        
        _logger.LogInformation("Connected to OpenAI");
    }
    
    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        _logger.LogInformation("Disconnecting from OpenAI");
        
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
        
        var msg = JsonSerializer.Serialize(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm24k)
        });
        
        _ = SendTextAsync(msg);
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
                    Interlocked.Exchange(ref _responseActive, 1);
                    _ = ClearInputBufferAsync();
                    break;
                
                case "response.done":
                    Interlocked.Exchange(ref _responseActive, 0);
                    OnPlayoutComplete?.Invoke();
                    break;
                
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
                
                case "conversation.item.input_audio_transcription.completed":
                    if (doc.RootElement.TryGetProperty("transcript", out var userText))
                        OnTranscript?.Invoke("User", userText.GetString() ?? "");
                    break;
                
                case "response.function_call_arguments.done":
                    await HandleToolCallAsync(doc.RootElement);
                    break;
                
                case "error":
                    var errMsg = doc.RootElement.TryGetProperty("error", out var err) &&
                                 err.TryGetProperty("message", out var msg)
                        ? msg.GetString() : "Unknown error";
                    _logger.LogWarning("OpenAI error: {Error}", errMsg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
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
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsStr ?? "{}") ?? new();
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
        await QueueResponseAsync();
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
    
    private async Task QueueResponseAsync()
    {
        // Wait for active response to complete
        for (int i = 0; i < 50 && Volatile.Read(ref _responseActive) == 1; i++)
            await Task.Delay(50);
        
        await SendJsonAsync(new { type = "response.create" });
    }
    
    private async Task ClearInputBufferAsync()
    {
        await SendJsonAsync(new { type = "input_audio_buffer.clear" });
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
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.4,
                    prefix_padding_ms = 450,
                    silence_duration_ms = 900
                },
                tools = GetTools(),
                tool_choice = "auto",
                temperature = 0.6
            }
        };
        
        await SendJsonAsync(config);
        _logger.LogInformation("Session configured (v{Version})", VERSION);
    }
    
    private async Task SendGreetingAsync()
    {
        await Task.Delay(100);
        
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
        var json = JsonSerializer.Serialize(obj);
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
    
    private static object[] GetTools() => new object[]
    {
        new
        {
            type = "function",
            name = "sync_booking_data",
            description = "Sync booking data after user provides information. MUST call after EVERY user response containing booking info.",
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
            description = "Request quote or confirm booking. MUST call with action='confirmed' BEFORE announcing booking success.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "request_quote", "confirmed" } },
                    pickup = new { type = "string", description = "Pickup address" },
                    destination = new { type = "string", description = "Destination address" },
                    passengers = new { type = "integer", description = "Number of passengers" },
                    pickup_time = new { type = "string", description = "Pickup time" }
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
    
    private static string GetDefaultSystemPrompt() => """
        You are Ada, a friendly taxi booking assistant for Voice Taxibot. Version 2.1.

        ## VOICE STYLE
        
        Speak naturally, like a friendly professional taxi dispatcher.
        - Warm, calm, confident tone
        - Clear pronunciation of names and addresses
        - Short pauses between phrases
        - Never rush or sound robotic
        - Patient and relaxed pace
        
        ## BOOKING FLOW
        
        1. Greet and ask for caller's name
        2. Ask for pickup address
        3. Ask for destination
        4. Ask about passengers (default 1)
        5. Ask about pickup time (default "now")
        6. Call sync_booking_data after EVERY piece of info
        7. Recap the booking and call book_taxi with action="request_quote"
        8. Tell price and ask for confirmation
        9. When confirmed, call book_taxi with action="confirmed"
        10. Read the booking reference from the tool result
        11. Say goodbye with the REAL reference and call end_call
        
        ## ABSOLUTE RULES - VIOLATION FORBIDDEN
        
        1. You MUST call sync_booking_data after every user response containing booking info
        2. You MUST call book_taxi(action="confirmed") BEFORE announcing any booking confirmation
        3. You MUST NOT say "your taxi is booked" or give ANY reference number until book_taxi returns success
        4. The booking reference comes ONLY from the book_taxi tool result - NEVER invent one
        5. If book_taxi fails, tell the user and ask if they want to try again
        
        ## RESPONSE STYLE
        
        - Keep responses under 20 words
        - One question at a time
        - Always use British Pounds (£) for fares
        - Preserve addresses exactly as user says them
        
        ## CONFIRMATION DETECTION
        
        These phrases mean YES - proceed immediately:
        'yes', 'yeah', 'yep', 'sure', 'ok', 'okay', 'correct', 'that's right', 'go ahead', 'book it', 'please do', 'confirm', 'that's fine'
        """;
    
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendMutex.Dispose();
    }
}
