using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdaCleanVersion.Conversation;

/// <summary>
/// Lightweight LLM-based turn reconciliation classifier.
/// Given Ada's last question, the expected response type, and the caller's utterance,
/// classifies the relationship (direct answer, correction, confirmation, etc.)
/// without ever mutating engine state.
/// 
/// This acts as a "conversation referee" — the deterministic engine remains
/// the sole authority for state transitions and dispatch.
/// </summary>
public sealed class TurnAnalyzer
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly double _minConfidence;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public TurnAnalyzer(
        string apiKey,
        string model = "gpt-4o-mini",
        double minConfidence = 0.65,
        HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _model = model;
        _minConfidence = minConfidence;
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    /// <summary>
    /// Analyze a single conversation turn and classify the caller's intent
    /// relative to Ada's last question.
    /// </summary>
    public async Task<TurnAnalysisResult> AnalyzeAsync(
        string? lastAdaQuestion,
        ExpectedResponse expected,
        string callerUtterance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callerUtterance))
        {
            return Fallback("Empty utterance");
        }

        var prompt = BuildPrompt(lastAdaQuestion, expected, callerUtterance);

        var requestBody = new
        {
            model = _model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        try
        {
            Log($"🧠 TurnAnalyzer: expected={expected}, utterance=\"{callerUtterance}\"");

            var response = await _http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                return Fallback("Empty model output");

            var result = ParseModelResponse(content);
            Log($"🧠 TurnAnalyzer result: {result.Relationship} " +
                $"(slot={result.Slot ?? "null"}, value={result.Value ?? "null"}, " +
                $"confidence={result.Confidence:F2})");

            return result;
        }
        catch (TaskCanceledException)
        {
            return Fallback("Timeout");
        }
        catch (Exception ex)
        {
            Log($"⚠ TurnAnalyzer error: {ex.Message}");
            return Fallback($"Exception: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────
    // PROMPT
    // ─────────────────────────────────────────

    private const string SystemPrompt =
"""
You analyze short voice assistant conversation turns for a taxi booking system.

Classify the relationship between:
- Ada's last question (what the assistant just asked)
- Expected response type (what kind of answer is expected)
- The caller's utterance (what the caller actually said)

Return STRICT JSON only:

{
  "relationship": "direct_answer|correction|confirmation_yes|confirmation_no|new_request|irrelevant|unclear",
  "slot": "pickup|destination|passengers|pickup_time|null",
  "value": "extracted value or null",
  "confidence": 0.0-1.0
}

Classification rules:
- "direct_answer": caller answers the question directly (e.g. Ada asks for pickup, caller gives an address)
- "correction": caller changes a previously stated or confirmed detail (e.g. "change the pickup to X", "no, make it Y instead")
- "confirmation_yes": caller confirms (yes, yeah, that's right, correct, go ahead, please)
- "confirmation_no": caller rejects (no, not quite, that's wrong) WITHOUT providing a new value
- "new_request": caller asks for something unrelated to the current question
- "irrelevant": noise, filler words, or unrelated speech
- "unclear": cannot determine intent with reasonable confidence

Important telephony context:
- Speech-to-text is noisy — be forgiving with spelling/grammar
- If caller says "no" followed by a new value, that's a "correction" not "confirmation_no"
- "As soon as possible", "now", "ASAP" are direct answers for pickup_time
- Numbers like "two", "three" are direct answers for passengers
- Be conservative with confidence scores
""";

    private static string BuildPrompt(
        string? lastQuestion,
        ExpectedResponse expected,
        string utterance)
    {
        return
$"""
Ada last asked:
"{lastQuestion ?? "None"}"

Expected response type:
"{expected}"

Caller said:
"{utterance}"
""";
    }

    // ─────────────────────────────────────────
    // PARSING
    // ─────────────────────────────────────────

    private TurnAnalysisResult ParseModelResponse(string content)
    {
        try
        {
            var model = JsonSerializer.Deserialize<ModelResponse>(content);

            if (model == null)
                return Fallback("Deserialization null", content);

            if (model.Confidence < _minConfidence)
            {
                Log($"🧠 TurnAnalyzer: below confidence threshold " +
                    $"({model.Confidence:F2} < {_minConfidence:F2})");
                return new TurnAnalysisResult(
                    TurnRelationship.Unclear,
                    model.Slot,
                    model.Value,
                    model.Confidence,
                    content
                );
            }

            var relationship = model.Relationship switch
            {
                "direct_answer"     => TurnRelationship.DirectAnswer,
                "correction"        => TurnRelationship.Correction,
                "confirmation_yes"  => TurnRelationship.ConfirmationYes,
                "confirmation_no"   => TurnRelationship.ConfirmationNo,
                "new_request"       => TurnRelationship.NewRequest,
                "irrelevant"        => TurnRelationship.Irrelevant,
                _                   => TurnRelationship.Unclear
            };

            return new TurnAnalysisResult(
                relationship,
                model.Slot,
                model.Value,
                model.Confidence,
                content
            );
        }
        catch
        {
            return Fallback("Parse failure", content);
        }
    }

    private TurnAnalysisResult Fallback(string reason, string raw = "")
    {
        Log($"🧠 TurnAnalyzer fallback: {reason}");
        return new TurnAnalysisResult(
            TurnRelationship.Unclear,
            null,
            null,
            0,
            $"Fallback: {reason}. Raw: {raw}"
        );
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    private sealed class ModelResponse
    {
        [JsonPropertyName("relationship")]
        public string Relationship { get; set; } = "unclear";

        [JsonPropertyName("slot")]
        public string? Slot { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
    }
}
