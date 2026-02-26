using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

/// <summary>
/// Result from burst-dispatch edge function — contains extracted slots + geocoded data.
/// </summary>
public record BurstResult(
    string? Name,
    string? Pickup,
    string? Destination,
    int? Passengers,
    string? PickupTime,
    JsonElement? Geocoded,
    string Status // "ready" | "partial" | "split_only" | "error" | "clarification_needed"
);

/// <summary>
/// Calls the burst-dispatch edge function to split a freeform utterance
/// into booking slots AND geocode addresses in a single round-trip.
/// 
/// Replaces the local AiFreeFormSplitter + separate geocoding calls,
/// collapsing 3+ HTTP round-trips into one.
/// 
/// Falls back gracefully (returns null) on timeout/error so the caller can
/// use the deterministic regex-based FreeFormSplitter as a safety net.
/// </summary>
public class EdgeBurstDispatcher
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly string _functionUrl;
    private readonly string _anonKey;
    private const int TimeoutMs = 8000; // 8s — covers split + geocode

    public EdgeBurstDispatcher(
        string supabaseUrl,
        string anonKey,
        ILogger logger,
        HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _log = logger;
        _anonKey = anonKey;
        _functionUrl = $"{supabaseUrl}/functions/v1/burst-dispatch";
    }

    /// <summary>
    /// Reuse the same burst-detection heuristic from AiFreeFormSplitter.
    /// </summary>
    public static bool LooksLikeBurst(string transcript) =>
        AiFreeFormSplitter.LooksLikeBurst(transcript);

    /// <summary>
    /// Send the raw transcript (+ optional Ada readback) to burst-dispatch.
    /// Returns extracted slots + geocoded result in one call.
    /// Returns null on failure — caller should fall back to regex FreeFormSplitter.
    /// </summary>
    public async Task<BurstResult?> DispatchAsync(
        string transcript,
        string? phone = null,
        string? adaReadback = null,
        string? callerArea = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        _log.LogInformation(
            "[BurstDispatch] Sending transcript=\"{T}\", phone={P}, area={A}",
            transcript.Trim(), phone ?? "–", callerArea ?? "–");

        var payload = new
        {
            transcript = transcript.Trim(),
            phone = phone ?? "",
            ada_readback = adaReadback ?? "",
            caller_area = callerArea ?? ""
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _functionUrl);
        request.Headers.Add("Authorization", $"Bearer {_anonKey}");
        request.Content = JsonContent.Create(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            var response = await _http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _log.LogWarning("[BurstDispatch] HTTP {S}: {B}",
                    (int)response.StatusCode, errBody);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            // Parse the split object
            var split = json.GetProperty("split");
            var name = TryGetString(split, "name");
            var pickup = TryGetString(split, "pickup");
            var destination = TryGetString(split, "destination");
            var passengers = TryGetInt(split, "passengers");
            var pickupTime = TryGetString(split, "pickup_time");

            // Geocoded may be null
            JsonElement? geocoded = json.TryGetProperty("geocoded", out var g) && g.ValueKind != JsonValueKind.Null
                ? g : null;

            var status = json.GetProperty("status").GetString() ?? "error";

            _log.LogInformation(
                "[BurstDispatch] ✅ Result: status={S}, name={N}, pickup={P}, dest={D}, pax={X}, time={T}",
                status, name ?? "–", pickup ?? "–", destination ?? "–",
                passengers?.ToString() ?? "–", pickupTime ?? "–");

            return new BurstResult(name, pickup, destination, passengers, pickupTime, geocoded, status);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[BurstDispatch] Timed out after {Ms}ms", TimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BurstDispatch] Unexpected error");
            return null;
        }
    }

    /// <summary>
    /// Fire a warm-up ping on call start to avoid cold-start latency.
    /// Non-blocking, swallows errors.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _functionUrl);
            request.Headers.Add("Authorization", $"Bearer {_anonKey}");
            request.Content = JsonContent.Create(new { ping = true });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(3000);

            await _http.SendAsync(request, cts.Token);
            _log.LogDebug("[BurstDispatch] Warm-up sent");
        }
        catch
        {
            // Non-critical — swallow
        }
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        if (val.ValueKind != JsonValueKind.String) return null;
        var s = val.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int? TryGetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetInt32() : null;
    }
}
