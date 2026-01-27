using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI GPT-4o-mini text completion client for the cheaper pipeline.
/// Maintains conversation history and handles tool calls.
/// </summary>
public class OpenAITextClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _systemPrompt;
    private readonly HttpClient _httpClient;
    private readonly List<ChatMessage> _conversationHistory = new();
    private bool _disposed = false;
    
    public event Action<string>? OnLog;
    public event Action<string, Dictionary<string, object>>? OnToolCall;
    
    private const string DEFAULT_SYSTEM_PROMPT = @"You are Ada, a friendly taxi booking assistant for 247 Radio Carz.

PERSONALITY: Warm, patient, and relaxed. Always speak in 1–2 short, natural sentences. Ask ONLY ONE question at a time.

GREETING: Say ""Hello, welcome to 247 Radio Carz! I'm Ada. What's your name?""

BOOKING FLOW:
1. Get their name
2. Get pickup location
3. Get destination  
4. Get number of passengers
5. Call book_taxi immediately - do NOT ask for confirmation

CRITICAL RULES:
1. NEVER repeat addresses back or ask ""is that correct?""
2. Call book_taxi as soon as you have pickup, destination, and passengers
3. Accept ALL addresses as-is - do NOT ask for clarification
4. After booking, say ONLY: ""Booked! [X] minutes, [FARE]. Anything else?""
5. If they say thank you or bye, say ""Safe travels!"" and call end_call";

    public OpenAITextClient(
        string apiKey,
        string model = "gpt-4o-mini",
        string? systemPrompt = null)
    {
        _apiKey = apiKey;
        _model = model;
        _systemPrompt = systemPrompt ?? DEFAULT_SYSTEM_PROMPT;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        
        // Initialize with system prompt
        _conversationHistory.Add(new ChatMessage("system", _systemPrompt));
    }
    
    /// <summary>
    /// Send a message and get a response (handles tool calls internally).
    /// </summary>
    public async Task<string> SendMessageAsync(string userMessage, CancellationToken ct = default)
    {
        _conversationHistory.Add(new ChatMessage("user", userMessage));
        
        return await GetCompletionAsync(ct);
    }
    
    /// <summary>
    /// Get initial greeting without user input.
    /// </summary>
    public async Task<string> GetGreetingAsync(string? callerName = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(callerName))
        {
            _conversationHistory.Add(new ChatMessage("user", 
                $"[System: Returning caller named {callerName}. Greet them by name.]"));
        }
        else
        {
            _conversationHistory.Add(new ChatMessage("user", 
                "[System: New caller. Give your standard greeting.]"));
        }
        
        return await GetCompletionAsync(ct);
    }
    
    private async Task<string> GetCompletionAsync(CancellationToken ct)
    {
        var tools = GetToolDefinitions();
        
        var requestBody = new
        {
            model = _model,
            messages = _conversationHistory.Select(m => new { role = m.Role, content = m.Content }),
            tools = tools,
            tool_choice = "auto",
            temperature = 0.7,
            max_tokens = 150
        };
        
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        
        try
        {
            var response = await _httpClient.PostAsync(
                "https://api.openai.com/v1/chat/completions",
                content,
                ct);
            
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            
            if (!response.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"[OpenAI Text] Error: {response.StatusCode} - {responseJson}");
                return "I'm sorry, I'm having trouble right now. Please try again.";
            }
            
            using var doc = JsonDocument.Parse(responseJson);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            
            // Check for tool calls
            if (message.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var functionName = toolCall.GetProperty("function").GetProperty("name").GetString()!;
                    var argsJson = toolCall.GetProperty("function").GetProperty("arguments").GetString()!;
                    var args = JsonSerializer.Deserialize<Dictionary<string, object>>(argsJson) ?? new();
                    
                    OnLog?.Invoke($"[OpenAI Text] Tool call: {functionName}");
                    OnToolCall?.Invoke(functionName, args);
                }
            }
            
            // Get the text response
            if (message.TryGetProperty("content", out var contentEl) && 
                contentEl.ValueKind != JsonValueKind.Null)
            {
                var assistantMessage = contentEl.GetString() ?? "";
                _conversationHistory.Add(new ChatMessage("assistant", assistantMessage));
                return assistantMessage;
            }
            
            return "";
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[OpenAI Text] Exception: {ex.Message}");
            return "I'm sorry, something went wrong. Please try again.";
        }
    }
    
    /// <summary>
    /// Add a tool result to the conversation (for multi-turn tool calls).
    /// </summary>
    public void AddToolResult(string toolName, string result)
    {
        _conversationHistory.Add(new ChatMessage("assistant", 
            $"[Tool {toolName} returned: {result}]"));
    }
    
    /// <summary>
    /// Clear conversation history (keep system prompt).
    /// </summary>
    public void Reset()
    {
        _conversationHistory.Clear();
        _conversationHistory.Add(new ChatMessage("system", _systemPrompt));
    }
    
    private object[] GetToolDefinitions()
    {
        return new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "book_taxi",
                    description = "Book a taxi once you have pickup, destination, and passengers.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            pickup = new { type = "string", description = "Pickup address" },
                            destination = new { type = "string", description = "Destination address" },
                            passengers = new { type = "integer", description = "Number of passengers" },
                            luggage = new { type = "integer", description = "Number of bags (optional)" }
                        },
                        required = new[] { "pickup", "destination", "passengers" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "end_call",
                    description = "End the call after saying goodbye.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            }
        };
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
    
    private class ChatMessage
    {
        public string Role { get; }
        public string Content { get; }
        
        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }
}
