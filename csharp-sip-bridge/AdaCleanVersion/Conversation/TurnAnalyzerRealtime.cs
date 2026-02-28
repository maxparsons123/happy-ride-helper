using System.Text.Json;
using AdaCleanVersion.Realtime;

namespace AdaCleanVersion.Conversation;

/// <summary>
/// Turn reconciliation classifier that reuses the existing OpenAI Realtime WebSocket.
/// 
/// Key design:
///   - Sends response.create with conversation:"none" → does NOT pollute main dialogue
///   - Uses modalities:["text"] → no audio output, no TTS cost
///   - Listens for response.text.done on the shared transport
///   - Local heuristics handle trivial turns (yes/no/ASAP) without any API call
///   - Falls back to "unclear" on timeout (never blocks the pipeline)
///
/// This eliminates the separate HTTP/gpt-4o-mini call, saving ~200ms latency
/// and billing overhead per non-trivial turn.
/// </summary>
public sealed class TurnAnalyzerRealtime
{
    private readonly IRealtimeTransport _transport;
    private readonly double _minConfidence;

    /// <summary>Diagnostic logging.</summary>
    public event Action<string>? OnLog;

    public TurnAnalyzerRealtime(
        IRealtimeTransport transport,
        double minConfidence = 0.65)
    {
        _transport = transport;
        _minConfidence = minConfidence;
    }

    /// <summary>
    /// Classify a caller utterance relative to Ada's last question.
    /// Fast local heuristics handle obvious cases; ambiguous ones use
    /// the Realtime session with conversation:"none" for isolation.
    /// </summary>
    public async Task<TurnAnalysisResult> AnalyzeAsync(
        string? lastAdaQuestion,
        ExpectedResponse expected,
        string callerUtterance,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callerUtterance))
            return Fallback("Empty utterance");

        // ── Fast local heuristic: skip API for obvious cases ──
        var local = TryLocalClassify(callerUtterance, expected);
        if (local != null)
        {
            Log($"🧠 TurnAnalyzer (local): {local.Relationship} " +
                $"(confidence={local.Confidence:F2}, value={local.Value ?? "null"})");
            return local;
        }

        // ── Realtime classification via conversation:"none" ──
        var prompt = BuildPrompt(lastAdaQuestion, expected, callerUtterance);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Listen for response.text.done — only fires for text-only modality responses.
        // The main session uses audio+text, so response.text.done is exclusively ours.
        Func<string, Task> handler = json =>
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                if (type == "response.text.done")
                {
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                        tcs.TrySetResult(text);
                }
            }
            catch { /* ignore parse errors on unrelated events */ }
            return Task.CompletedTask;
        };

        _transport.OnMessage += handler;

        try
        {
            Log($"🧠 TurnAnalyzer (realtime): expected={expected}, utterance=\"{callerUtterance}\"");

            await _transport.SendAsync(new
            {
                type = "response.create",
                response = new
                {
                    conversation = "none",       // CRITICAL: don't pollute main convo
                    modalities = new[] { "text" }, // text only — no TTS
                    instructions = prompt
                }
            }, ct);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000, ct));

            if (completed != tcs.Task)
                return Fallback("Timeout");

            var result = Parse(tcs.Task.Result);
            Log($"🧠 TurnAnalyzer result: {result.Relationship} " +
                $"(slot={result.Slot ?? "null"}, value={result.Value ?? "null"}, " +
                $"confidence={result.Confidence:F2})");
            return result;
        }
        catch (TaskCanceledException)
        {
            return Fallback("Cancelled");
        }
        catch (Exception ex)
        {
            Log($"⚠ TurnAnalyzer error: {ex.Message}");
            return Fallback($"Exception: {ex.Message}");
        }
        finally
        {
            _transport.OnMessage -= handler;
        }
    }

    // ─────────────────────────────────────────
    // LOCAL HEURISTICS (skip API for obvious cases)
    // ─────────────────────────────────────────

    private static readonly HashSet<string> YesPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "yep", "yup", "ya", "aye", "correct", "that's right",
        "that's correct", "right", "go ahead", "please", "yes please",
        "absolutely", "sure", "okay", "ok", "yes that's right", "yes it is",
        "yes that's correct", "confirm", "confirmed", "affirmative"
    };

    private static readonly HashSet<string> NoPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "nah", "nope", "no thanks", "not quite", "that's wrong",
        "that's not right", "wrong", "incorrect", "no it's not",
        "no that's wrong", "negative"
    };

    private static readonly HashSet<string> AsapPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "asap", "now", "as soon as possible", "straight away", "right now",
        "immediately", "right away"
    };

    private static TurnAnalysisResult? TryLocalClassify(string utterance, ExpectedResponse expected)
    {
        var trimmed = utterance.Trim().TrimEnd('.', '!', '?', ',');

        if (expected == ExpectedResponse.ConfirmationYesNo)
        {
            if (YesPatterns.Contains(trimmed))
                return new TurnAnalysisResult(TurnRelationship.ConfirmationYes, null, null, 0.98, "local:yes");
            if (NoPatterns.Contains(trimmed))
                return new TurnAnalysisResult(TurnRelationship.ConfirmationNo, null, null, 0.98, "local:no");
        }

        if (expected == ExpectedResponse.Passengers)
        {
            var paxValue = TryParsePassengers(trimmed);
            if (paxValue != null)
                return new TurnAnalysisResult(TurnRelationship.DirectAnswer, "passengers", paxValue, 0.95, "local:passengers");
        }

        if (expected == ExpectedResponse.PickupTime && AsapPatterns.Contains(trimmed))
            return new TurnAnalysisResult(TurnRelationship.DirectAnswer, "pickup_time", "ASAP", 0.95, "local:asap");

        return null;
    }

    private static readonly Dictionary<string, string> WordNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8"
    };

    private static string? TryParsePassengers(string text)
    {
        if (int.TryParse(text, out var n) && n >= 1 && n <= 8)
            return n.ToString();

        var lower = text.ToLowerInvariant().Replace("passengers", "").Replace("passenger", "").Trim();
        if (int.TryParse(lower, out var n2) && n2 >= 1 && n2 <= 8)
            return n2.ToString();

        if (WordNumbers.TryGetValue(lower, out var val))
            return val;

        return null;
    }

    // ─────────────────────────────────────────
    // PROMPT
    // ─────────────────────────────────────────

    private static string BuildPrompt(
        string? lastQuestion,
        ExpectedResponse expected,
        string utterance)
    {
        return
$"""
You analyze short telephony conversation turns for a taxi booking system.

Return STRICT JSON only:

{{
  "relationship": "direct_answer|correction|confirmation_yes|confirmation_no|new_request|irrelevant|unclear",
  "slot": "pickup|destination|passengers|pickup_time|null",
  "value": "extracted value or null",
  "confidence": 0.0-1.0
}}

Classification rules:
- "direct_answer": caller answers the question directly
- "correction": caller changes a previously stated detail (e.g. "change the pickup to X")
- "confirmation_yes": caller confirms (yes, yeah, correct, go ahead)
- "confirmation_no": caller rejects WITHOUT providing a new value
- "new_request": unrelated to the current question
- "irrelevant": noise, filler words
- "unclear": cannot determine intent

Important: Speech-to-text is noisy. If caller says "no" followed by a new value, that's "correction" not "confirmation_no".

Ada last asked:
"{lastQuestion ?? "None"}"

Expected:
"{expected}"

Caller said:
"{utterance}"

Return JSON only.
""";
    }

    // ─────────────────────────────────────────
    // PARSING
    // ─────────────────────────────────────────

    private TurnAnalysisResult Parse(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var confidence = root.GetProperty("confidence").GetDouble();

            if (confidence < _minConfidence)
            {
                Log($"🧠 TurnAnalyzer: below threshold ({confidence:F2} < {_minConfidence:F2})");
                return new TurnAnalysisResult(
                    TurnRelationship.Unclear,
                    root.TryGetProperty("slot", out var ss) ? ss.GetString() : null,
                    root.TryGetProperty("value", out var vv) ? vv.GetString() : null,
                    confidence,
                    content
                );
            }

            var rel = root.GetProperty("relationship").GetString();

            var relationship = rel switch
            {
                "direct_answer" => TurnRelationship.DirectAnswer,
                "correction" => TurnRelationship.Correction,
                "confirmation_yes" => TurnRelationship.ConfirmationYes,
                "confirmation_no" => TurnRelationship.ConfirmationNo,
                "new_request" => TurnRelationship.NewRequest,
                "irrelevant" => TurnRelationship.Irrelevant,
                _ => TurnRelationship.Unclear
            };

            return new TurnAnalysisResult(
                relationship,
                root.TryGetProperty("slot", out var s) ? s.GetString() : null,
                root.TryGetProperty("value", out var v) ? v.GetString() : null,
                confidence,
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
            TurnRelationship.Unclear, null, null, 0,
            $"Fallback: {reason}. Raw: {raw}"
        );
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
