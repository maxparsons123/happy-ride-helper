using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TaxiSipBridge;

/// <summary>
/// OpenAI TTS client for generating speech from text.
/// Uses Shimmer voice by default, outputs PCM 24kHz for telephony.
/// </summary>
public class OpenAITtsClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _voice;
    private readonly HttpClient _httpClient;
    private bool _disposed = false;
    
    public event Action<string>? OnLog;
    
    // Available voices: alloy, echo, fable, onyx, nova, shimmer
    public OpenAITtsClient(string apiKey, string voice = "shimmer")
    {
        _apiKey = apiKey;
        _voice = voice;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }
    
    /// <summary>
    /// Generate speech from text, returns PCM 24kHz audio.
    /// </summary>
    public async Task<byte[]?> GenerateSpeechAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        
        var requestBody = new
        {
            model = "tts-1", // Use tts-1 for speed, tts-1-hd for quality
            input = text,
            voice = _voice,
            response_format = "pcm", // Raw PCM 24kHz
            speed = 1.0
        };
        
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        
        try
        {
            var response = await _httpClient.PostAsync(
                "https://api.openai.com/v1/audio/speech",
                content,
                ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                OnLog?.Invoke($"[OpenAI TTS] Error: {response.StatusCode} - {error}");
                return null;
            }
            
            var audioData = await response.Content.ReadAsByteArrayAsync(ct);
            OnLog?.Invoke($"[OpenAI TTS] Generated {audioData.Length} bytes for: \"{TruncateText(text, 50)}\"");
            
            return audioData;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[OpenAI TTS] Exception: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Generate speech and stream chunks as they become available.
    /// Note: OpenAI TTS doesn't support true streaming, but we can chunk the response.
    /// </summary>
    public async IAsyncEnumerable<byte[]> GenerateSpeechStreamingAsync(
        string text, 
        int chunkSize = 4800, // 100ms at 24kHz
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var fullAudio = await GenerateSpeechAsync(text, ct);
        if (fullAudio == null) yield break;
        
        // Chunk the audio for streaming playback
        for (int i = 0; i < fullAudio.Length; i += chunkSize)
        {
            var remaining = Math.Min(chunkSize, fullAudio.Length - i);
            var chunk = new byte[remaining];
            Array.Copy(fullAudio, i, chunk, 0, remaining);
            yield return chunk;
        }
    }
    
    private static string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "...";
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}
