using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Gemini Flash service for:
/// 1. Transcribing voice messages (audio → text)
/// 2. Extracting booking details from free-form text → structured JSON
/// </summary>
public sealed class GeminiService
{
    private readonly GeminiConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    private const string BookingExtractionPrompt = @"
You are a taxi booking extraction engine. Extract booking details from the user's message.
The user may speak casually, e.g. 'pick me up from 52a david road going to coventry with 2 passengers'.

Return ONLY valid JSON (no markdown, no explanation):
{
  ""pickup"": ""full pickup address or null"",
  ""destination"": ""full destination address or null"",
  ""passengers"": number or null,
  ""caller_name"": ""name if mentioned or null"",
  ""notes"": ""any special requests or null"",
  ""pickup_time"": ""requested time or 'now' or null"",
  ""is_complete"": true/false (true if pickup AND destination are provided),
  ""missing_fields"": ""comma-separated list of missing required fields or null""
}

Required fields: pickup, destination. Passengers defaults to 1 if not mentioned.
If user sends a greeting or non-booking message, set is_complete=false and missing_fields='pickup,destination'.
If user provides partial info, extract what you can and list what's missing.";

    public GeminiService(GeminiConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Transcribe a WhatsApp voice message using Gemini's multimodal input.
    /// Downloads the audio from WhatsApp, sends to Gemini as inline_data.
    /// </summary>
    public async Task<string?> TranscribeAudioAsync(string mediaUrl, string accessToken, string mimeType = "audio/ogg")
    {
        Log("🎤 [Gemini] Transcribing voice message...");
        try
        {
            // Download audio from WhatsApp
            using var audioReq = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            audioReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var audioResp = await _http.SendAsync(audioReq);
            if (!audioResp.IsSuccessStatusCode)
            {
                Log($"⚠️ Failed to download audio: {audioResp.StatusCode}");
                return null;
            }
            var audioBytes = await audioResp.Content.ReadAsByteArrayAsync();
            var base64Audio = Convert.ToBase64String(audioBytes);

            Log($"🎤 Audio downloaded: {audioBytes.Length} bytes, sending to Gemini...");

            // Send to Gemini for transcription
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe this audio message exactly as spoken. Return ONLY the transcription text, nothing else." },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = mimeType,
                                    data = base64Audio
                                }
                            }
                        }
                    }
                },
                generationConfig = new { temperature = 0.1 }
            };

            var result = await CallGeminiAsync(requestBody);
            if (result != null)
                Log($"🎤 Transcription: \"{result}\"");
            return result;
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Transcription error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract booking details from free-form text using Gemini Flash.
    /// </summary>
    public async Task<GeminiBookingExtraction?> ExtractBookingAsync(string userMessage, List<(string role, string message)>? conversationHistory = null)
    {
        Log($"🤖 [Gemini] Extracting booking from: \"{userMessage}\"");
        try
        {
            var contents = new List<object>();

            // Add system instruction
            contents.Add(new { role = "user", parts = new[] { new { text = BookingExtractionPrompt } } });
            contents.Add(new { role = "model", parts = new[] { new { text = "Understood. I will extract taxi booking details and return JSON." } } });

            // Add conversation history for context
            if (conversationHistory != null)
            {
                foreach (var (role, msg) in conversationHistory)
                {
                    contents.Add(new
                    {
                        role = role == "user" ? "user" : "model",
                        parts = new[] { new { text = msg } }
                    });
                }
            }

            // Add current message
            contents.Add(new { role = "user", parts = new[] { new { text = $"Extract booking from this message: \"{userMessage}\"" } } });

            var requestBody = new
            {
                contents,
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    temperature = 0.1
                }
            };

            var jsonResult = await CallGeminiAsync(requestBody);
            if (jsonResult == null) return null;

            var extraction = JsonSerializer.Deserialize<GeminiBookingExtraction>(jsonResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            });

            if (extraction != null)
            {
                Log($"✅ Extracted: pickup={extraction.Pickup}, dest={extraction.Destination}, pax={extraction.Passengers}, complete={extraction.IsComplete}");
            }
            return extraction;
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Extraction error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generate a friendly confirmation/follow-up message using Gemini.
    /// </summary>
    public async Task<string?> GenerateReplyAsync(string context)
    {
        try
        {
            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = context } }
                    }
                },
                generationConfig = new { temperature = 0.7, maxOutputTokens = 300 }
            };
            return await CallGeminiAsync(requestBody);
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Reply generation error: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> CallGeminiAsync(object requestBody)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_config.Model}:generateContent?key={_config.ApiKey}";
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(url, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log($"⚠️ [Gemini] HTTP {(int)response.StatusCode}: {responseBody[..Math.Min(200, responseBody.Length)]}");
            return null;
        }

        using var doc = JsonDocument.Parse(responseBody);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0) return null;

        return candidates[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
    }
}
