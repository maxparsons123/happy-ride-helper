// Ported from AdaSdkModel — Zone POI pickup address validator
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaCleanVersion.Config;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Services;

public sealed class ZoneGuardResult
{
    public bool Pass { get; init; }
    public bool WasChecked { get; init; }
    public string? CanonicalName { get; init; }
    public string? Area { get; init; }
    public string? ZoneName { get; init; }
    public float Score { get; init; }
    public string Reason { get; init; } = "";
    public IReadOnlyList<ZonePoiMatch> Candidates { get; init; } = [];

    public static ZoneGuardResult Disabled() => new()
        { Pass = true, WasChecked = false, Reason = "Zone guard disabled" };

    public static ZoneGuardResult NoData() => new()
        { Pass = true, WasChecked = false, Reason = "Zone guard skipped — no POIs loaded" };

    public static ZoneGuardResult Serviceable(ZonePoiMatch top, IReadOnlyList<ZonePoiMatch> all) => new()
    {
        Pass = true, WasChecked = true,
        CanonicalName = top.PoiName, Area = top.Area, ZoneName = top.ZoneName,
        Score = top.SimilarityScore, Candidates = all,
        Reason = $"Address matched '{top.PoiName}' ({top.ZoneName}) score={top.SimilarityScore:F2}"
    };

    public static ZoneGuardResult OutOfZone(IReadOnlyList<ZonePoiMatch> all, string input, float threshold) => new()
    {
        Pass = false, WasChecked = true, Score = 0f, Candidates = all,
        Reason = $"'{input}' did not match any known serviceable street/business (threshold={threshold:F2})"
    };

    public static ZoneGuardResult Error(string detail) => new()
        { Pass = true, WasChecked = false, Reason = $"Zone guard error (fail-open): {detail}" };
}

public sealed class ZonePoiMatch
{
    [JsonPropertyName("poi_id")]        public string PoiId { get; init; } = "";
    [JsonPropertyName("poi_name")]      public string PoiName { get; init; } = "";
    [JsonPropertyName("poi_type")]      public string PoiType { get; init; } = "";
    [JsonPropertyName("area")]          public string? Area { get; init; }
    [JsonPropertyName("zone_id")]       public string ZoneId { get; init; } = "";
    [JsonPropertyName("zone_name")]     public string ZoneName { get; init; } = "";
    [JsonPropertyName("company_id")]    public string? CompanyId { get; init; }
    [JsonPropertyName("similarity_score")] public float SimilarityScore { get; init; }
    [JsonPropertyName("lat")]           public double? Lat { get; init; }
    [JsonPropertyName("lng")]           public double? Lng { get; init; }
}

internal sealed class GuardApiResponse
{
    [JsonPropertyName("is_serviceable")] public bool IsServiceable { get; init; }
    [JsonPropertyName("top_match")]      public ZonePoiMatch? TopMatch { get; init; }
    [JsonPropertyName("candidates")]     public List<ZonePoiMatch> Candidates { get; init; } = [];
    [JsonPropertyName("address_input")]  public string AddressInput { get; init; } = "";
    [JsonPropertyName("threshold")]      public float Threshold { get; init; }
}

public sealed class ZoneGuardService : IDisposable
{
    private readonly ZoneGuardSettings _cfg;
    private readonly SupabaseSettings  _supabase;
    private readonly ILogger           _log;
    private readonly HttpClient        _http;

    private static readonly JsonSerializerOptions _json = new()
        { PropertyNameCaseInsensitive = true };

    public ZoneGuardService(ZoneGuardSettings cfg, SupabaseSettings supabase, ILogger log)
    {
        _cfg = cfg;
        _supabase = supabase;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.Add("apikey", supabase.AnonKey);
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {supabase.AnonKey}");
    }

    public async Task<ZoneGuardResult> CheckPickupAsync(
        string? address, string? companyId = null, CancellationToken ct = default)
    {
        if (!_cfg.Enabled) return ZoneGuardResult.Disabled();
        if (string.IsNullOrWhiteSpace(address)) return ZoneGuardResult.Error("Empty address");

        try
        {
            var edgeUrl = $"{_supabase.Url.TrimEnd('/')}/functions/v1/zone-address-guard";
            var payload = new
            {
                address,
                company_id = companyId,
                min_similarity = _cfg.MinSimilarity,
                limit = _cfg.MaxCandidates
            };

            var resp = await _http.PostAsJsonAsync(edgeUrl, payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[ZoneGuard] Edge function error {Status}: {Body}", resp.StatusCode, body);
                return ZoneGuardResult.Error($"HTTP {resp.StatusCode}");
            }

            var result = JsonSerializer.Deserialize<GuardApiResponse>(body, _json);
            if (result is null) return ZoneGuardResult.Error("Null response");

            if (result.Candidates.Count == 0) return ZoneGuardResult.NoData();

            if (result.IsServiceable && result.TopMatch is not null)
            {
                _log.LogInformation("[ZoneGuard] ✅ '{Address}' → '{Canonical}' ({Zone})",
                    address, result.TopMatch.PoiName, result.TopMatch.ZoneName);
                return ZoneGuardResult.Serviceable(result.TopMatch, result.Candidates);
            }
            else
            {
                if (_cfg.BlockOnNoMatch)
                    return ZoneGuardResult.OutOfZone(result.Candidates, address, result.Threshold);

                return new ZoneGuardResult
                {
                    Pass = true, WasChecked = true,
                    Score = result.TopMatch?.SimilarityScore ?? 0f,
                    Candidates = result.Candidates,
                    Reason = $"Warn-only: best match '{result.TopMatch?.PoiName ?? "none"}' @ {result.TopMatch?.SimilarityScore:F2}"
                };
            }
        }
        catch (OperationCanceledException) { return ZoneGuardResult.Error("Timeout"); }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZoneGuard] Error checking '{Address}'", address);
            return ZoneGuardResult.Error(ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
