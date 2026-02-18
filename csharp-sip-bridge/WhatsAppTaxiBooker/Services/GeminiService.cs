using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Models;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Gemini Flash service: transcription, booking extraction with intent detection, reply generation.
/// </summary>
public sealed class GeminiService
{
    private readonly GeminiConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    private const string BookingExtractionPrompt = @"
You are a taxi booking extraction engine. Extract booking details AND detect user intent from messages.
The user may speak casually, e.g. 'pick me up from 52a david road going to coventry with 2 passengers'.

INTENT DETECTION:
- 'new_booking': User wants to book a new taxi (provides pickup/destination)
- 'update': User wants to change an existing booking (e.g. 'change destination to...', 'actually make it 3 passengers', 'update pickup to...')
- 'confirm': User confirms the booking (e.g. 'yes', 'confirm', 'book it', 'that's correct', 'go ahead', 'send it')
- 'cancel': User wants to cancel (e.g. 'cancel', 'never mind', 'forget it')
- 'query': User is asking about their booking (e.g. 'what's my booking?', 'status?')
- 'greeting': Just a greeting or unrelated message

Return ONLY valid JSON (no markdown, no explanation):
{
  ""intent"": ""new_booking|update|confirm|cancel|query|greeting"",
  ""pickup"": ""full pickup address or null"",
  ""destination"": ""full destination address or null"",
  ""passengers"": number or null,
  ""caller_name"": ""name if mentioned or null"",
  ""notes"": ""any special requests or null"",
  ""pickup_time"": ""requested time or 'now' or null"",
  ""is_complete"": true/false (true if pickup AND destination are provided),
  ""missing_fields"": ""comma-separated list of missing required fields or null"",
  ""update_fields"": ""comma-separated list of fields being updated or null""
}

RULES:
- For 'confirm' intent: set is_complete=true if existing booking has all required fields
- For 'update' intent: only populate the fields being changed, leave others null
- For 'cancel' intent: set is_complete=false
- For 'greeting'/'query': set is_complete=false
- Required fields for a complete booking: pickup, destination
- Passengers defaults to 1 if not mentioned";

    public GeminiService(GeminiConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Transcribe a WhatsApp voice message using Gemini's multimodal input.
    /// </summary>
    public async Task<string?> TranscribeAudioAsync(string mediaUrl, string accessToken, string mimeType = "audio/ogg")
    {
        Log("🎤 [Gemini] Transcribing voice message...");
        try
        {
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

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe this audio message exactly as spoken. Return ONLY the transcription text, nothing else." },
                            new { inline_data = new { mime_type = mimeType, data = base64Audio } }
                        }
                    }
                },
                generationConfig = new { temperature = 0.1 }
            };

            var result = await CallGeminiAsync(requestBody);
            if (result != null) Log($"🎤 Transcription: \"{result}\"");
            return result;
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Transcription error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract booking details + intent from free-form text.
    /// Includes existing booking context so Gemini can detect updates vs new bookings.
    /// </summary>
    public async Task<GeminiBookingExtraction?> ExtractBookingAsync(
        string userMessage,
        List<(string role, string message)>? conversationHistory = null,
        Booking? existingBooking = null)
    {
        Log($"🤖 [Gemini] Extracting from: \"{userMessage}\"");
        try
        {
            var contents = new List<object>();

            // System instruction
            contents.Add(new { role = "user", parts = new[] { new { text = BookingExtractionPrompt } } });
            contents.Add(new { role = "model", parts = new[] { new { text = "Understood. I will extract taxi booking details with intent detection and return JSON." } } });

            // Inject existing booking context
            if (existingBooking != null)
            {
                var ctx = $"[EXISTING BOOKING STATE]\n" +
                          $"Ref: {existingBooking.Id}\n" +
                          $"Pickup: {(string.IsNullOrWhiteSpace(existingBooking.Pickup) ? "(not set)" : existingBooking.Pickup)}\n" +
                          $"Destination: {(string.IsNullOrWhiteSpace(existingBooking.Destination) ? "(not set)" : existingBooking.Destination)}\n" +
                          $"Passengers: {existingBooking.Passengers}\n" +
                          $"Status: {existingBooking.Status}\n" +
                          (existingBooking.Notes != null ? $"Notes: {existingBooking.Notes}\n" : "") +
                          "If the user sends a confirmation, they are confirming THIS booking.";
                contents.Add(new { role = "user", parts = new[] { new { text = ctx } } });
                contents.Add(new { role = "model", parts = new[] { new { text = "I see the existing booking. I will detect if the user wants to update it, confirm it, or start a new one." } } });
            }

            // Conversation history
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

            contents.Add(new { role = "user", parts = new[] { new { text = $"Extract booking from: \"{userMessage}\"" } } });

            var requestBody = new
            {
                contents,
                generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
            };

            var jsonResult = await CallGeminiAsync(requestBody);
            if (jsonResult == null) return null;

            var extraction = JsonSerializer.Deserialize<GeminiBookingExtraction>(jsonResult, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            });

            if (extraction != null)
                Log($"✅ Intent={extraction.Intent}, pickup={extraction.Pickup}, dest={extraction.Destination}, pax={extraction.Passengers}, complete={extraction.IsComplete}");
            return extraction;
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Extraction error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generate a friendly reply using Gemini.
    /// </summary>
    public async Task<string?> GenerateReplyAsync(string context)
    {
        try
        {
            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = context } } } },
                generationConfig = new { temperature = 0.7, maxOutputTokens = 300 }
            };
            return await CallGeminiAsync(requestBody);
        }
        catch (Exception ex)
        {
            Log($"❌ [Gemini] Reply error: {ex.Message}");
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
        return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
    }
}
