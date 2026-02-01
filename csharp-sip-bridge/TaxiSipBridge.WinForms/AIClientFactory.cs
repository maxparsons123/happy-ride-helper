namespace TaxiSipBridge;

/// <summary>
/// Factory for creating AI clients based on configuration.
/// </summary>
public static class AIClientFactory
{
    public enum AIMode
    {
        /// <summary>Use the edge function (default, requires no local API key)</summary>
        EdgeFunction,
        
        /// <summary>Connect directly to OpenAI Realtime API (requires API key)</summary>
        DirectOpenAI
    }

    /// <summary>
    /// Create an AI client based on the specified mode.
    /// </summary>
    /// <param name="mode">Edge function or direct OpenAI mode</param>
    /// <param name="edgeFunctionUrl">URL for edge function mode</param>
    /// <param name="openAiApiKey">OpenAI API key for direct mode</param>
    /// <param name="openAiModel">OpenAI model for direct mode</param>
    /// <param name="openAiVoice">OpenAI voice for direct mode</param>
    /// <param name="systemPrompt">Custom system prompt (null for default)</param>
    /// <returns>An IAudioAIClient instance</returns>
    public static IAudioAIClient Create(
        AIMode mode,
        string? edgeFunctionUrl = null,
        string? openAiApiKey = null,
        string? openAiModel = null,
        string? openAiVoice = null,
        string? systemPrompt = null)
    {
        return mode switch
        {
            AIMode.EdgeFunction => CreateEdgeClient(edgeFunctionUrl 
                ?? "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-desktop"),
            
            AIMode.DirectOpenAI => CreateOpenAIClient(
                openAiApiKey ?? throw new ArgumentException("OpenAI API key required for direct mode"),
                openAiModel ?? "gpt-4o-mini-realtime-preview-2024-12-17",
                openAiVoice ?? "shimmer",
                systemPrompt),
            
            _ => throw new ArgumentException($"Unknown AI mode: {mode}")
        };
    }

    private static IAudioAIClient CreateEdgeClient(string url)
    {
        return new AdaAudioClientWrapper(url);
    }

    private static IAudioAIClient CreateOpenAIClient(string apiKey, string model, string voice, string? systemPrompt)
    {
        return new OpenAIRealtimeClient(apiKey, model, voice, systemPrompt);
    }
}

/// <summary>
/// Wrapper to make AdaAudioClient implement IAudioAIClient.
/// </summary>
internal class AdaAudioClientWrapper : IAudioAIClient
{
    private readonly AdaAudioClient _client;

    public AdaAudioClientWrapper(string wsUrl)
    {
        _client = new AdaAudioClient(wsUrl);
        
        // Forward all events
        _client.OnLog += msg => OnLog?.Invoke(msg);
        _client.OnTranscript += msg => OnTranscript?.Invoke(msg);
        _client.OnConnected += () => OnConnected?.Invoke();
        _client.OnDisconnected += () => OnDisconnected?.Invoke();
        _client.OnAdaSpeaking += msg => OnAdaSpeaking?.Invoke(msg);
        _client.OnPcm24Audio += data => OnPcm24Audio?.Invoke(data);
        _client.OnResponseStarted += () => OnResponseStarted?.Invoke();
    }

    public event Action<string>? OnLog;
    public event Action<string>? OnTranscript;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnAdaSpeaking;
    public event Action<byte[]>? OnPcm24Audio;
    public event Action? OnResponseStarted;
    public event Action? OnResponseCompleted;

    public bool IsConnected => _client.IsConnected;
    public int PendingFrameCount => _client.PendingFrameCount;

    public Task ConnectAsync(string? caller = null, CancellationToken ct = default)
        => _client.ConnectAsync(caller, ct);

    public Task SendMuLawAsync(byte[] ulawData)
        => _client.SendMuLawAsync(ulawData);

    public Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000)
        => _client.SendAudioAsync(pcmData, sampleRate);

    public Task DisconnectAsync()
        => _client.DisconnectAsync();

    public byte[]? GetNextMuLawFrame()
        => _client.GetNextMuLawFrame();

    public void ClearPendingFrames()
        => _client.ClearPendingFrames();

    public void Dispose()
        => _client.Dispose();
}
