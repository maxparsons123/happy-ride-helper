// Last updated: 2026-02-21 (v2.8)
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaSdkModel.Config;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  ZoneGuardService — pickup address validator against zone POIs
//
//  FEATURE STATUS: BUILT — DISABLED BY DEFAULT
//  To enable: set ZoneGuard.Enabled = true in appsettings.json
//
//  When enabled, this service calls the zone-address-guard edge function which
//  fuzzy-matches a pickup address against every street and business name
//  extracted from the Zone Editor (Overpass/OSM data stored in zone_pois).
//
//  Three validation levels are possible (controlled via ZoneGuardSettings):
//    1. Inform only  — log a warning, do not block the booking
//    2. Warn AI      — inject a safeguard note into the booking state
//    3. Block        — return a failure result so sync_booking_data rejects it
//
//  Plug-in point (NOT YET WIRED):
//    In CallSession.HandleSyncBookingData(), after house-number guard passes
//    and before fare calculation, call:
//       var guard = await _zoneGuard.CheckPickupAsync(booking.Pickup, cancellationToken);
//       if (!guard.Pass) { ... }
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ZoneGuardResult
{
    /// <summary>True if the address is within the known serviceable area (or guard is disabled/no data).</summary>
    public bool Pass { get; init; }

    /// <summary>Whether the zone guard was actually consulted (false when disabled or no POIs loaded).</summary>
    public bool WasChecked { get; init; }

    /// <summary>Best-matching canonical POI name from zone data (e.g. "David Road").</summary>
    public string? CanonicalName { get; init; }

    /// <summary>Best-matching area/suburb (e.g. "Blackburn").</summary>
    public string? Area { get; init; }

    /// <summary>Zone name the address belongs to (e.g. "Blackburn Central").</summary>
    public string? ZoneName { get; init; }

    /// <summary>Fuzzy similarity score of the top match (0-1).</summary>
    public float Score { get; init; }

    /// <summary>Human-readable reason for the result (for logging / AI injection).</summary>
    public string Reason { get; init; } = "";

    /// <summary>All candidates returned from the edge function, for debugging.</summary>
    public IReadOnlyList<ZonePoiMatch> Candidates { get; init; } = [];

    // ── Convenience factory methods ──

    public static ZoneGuardResult Disabled() => new()
    {
        Pass = true, WasChecked = false, Reason = "Zone guard disabled"
    };

    public static ZoneGuardResult NoData() => new()
    {
        Pass = true, WasChecked = false,
        Reason = "Zone guard skipped — no POIs loaded for this zone yet. Extract streets from Zone Editor first."
    };

    public static ZoneGuardResult Serviceable(ZonePoiMatch top, IReadOnlyList<ZonePoiMatch> all) => new()
    {
        Pass = true, WasChecked = true,
        CanonicalName = top.PoiName, Area = top.Area, ZoneName = top.ZoneName,
        Score = top.SimilarityScore,
        Candidates = all,
        Reason = $"Address matched '{top.PoiName}' ({top.ZoneName}) score={top.SimilarityScore:F2}"
    };

    public static ZoneGuardResult OutOfZone(IReadOnlyList<ZonePoiMatch> all, string input, float threshold) => new()
    {
        Pass = false, WasChecked = true, Score = 0f,
        Candidates = all,
        Reason = $"'{input}' did not match any known serviceable street/business (threshold={threshold:F2})"
    };

    public static ZoneGuardResult Error(string detail) => new()
    {
        Pass = true, WasChecked = false,   // fail-open on error
        Reason = $"Zone guard error (fail-open): {detail}"
    };
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
    {
        PropertyNameCaseInsensitive = true
    };

    public ZoneGuardService(
        ZoneGuardSettings cfg,
        SupabaseSettings  supabase,
        ILogger           log)
    {
        _cfg      = cfg;
        _supabase = supabase;
        _log      = log;
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.Add("apikey", supabase.AnonKey);
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {supabase.AnonKey}");
    }

    /// <summary>
    /// Validate a pickup address against known zone POIs.
    /// Always returns a safe result — never throws.
    /// </summary>
    public async Task<ZoneGuardResult> CheckPickupAsync(
        string?           address,
        string?           companyId       = null,
        CancellationToken cancellationToken = default)
    {
        // ── Feature gate ────────────────────────────────────────────────────
        if (!_cfg.Enabled)
        {
            _log.LogDebug("[ZoneGuard] Disabled — skipping check for '{Address}'", address);
            return ZoneGuardResult.Disabled();
        }

        if (string.IsNullOrWhiteSpace(address))
            return ZoneGuardResult.Error("Empty address");

        // ── Call edge function ───────────────────────────────────────────────
        try
        {
            var edgeUrl = $"{_supabase.Url.TrimEnd('/')}/functions/v1/zone-address-guard";

            var payload = new
            {
                address,
                company_id     = companyId,
                min_similarity = _cfg.MinSimilarity,
                limit          = _cfg.MaxCandidates
            };

            var resp = await _http.PostAsJsonAsync(edgeUrl, payload, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[ZoneGuard] Edge function error {Status}: {Body}", resp.StatusCode, body);
                return ZoneGuardResult.Error($"HTTP {resp.StatusCode}");
            }

            var result = JsonSerializer.Deserialize<GuardApiResponse>(body, _json);
            if (result is null) return ZoneGuardResult.Error("Null response");

            // No POI data yet — treat as pass so the system still works
            if (result.Candidates.Count == 0)
            {
                _log.LogDebug("[ZoneGuard] No POI data for zone — skipping guard");
                return ZoneGuardResult.NoData();
            }

            if (result.IsServiceable && result.TopMatch is not null)
            {
                _log.LogInformation(
                    "[ZoneGuard] ✅ '{Address}' → '{Canonical}' ({Zone}) score={Score:F2}",
                    address, result.TopMatch.PoiName, result.TopMatch.ZoneName, result.TopMatch.SimilarityScore);

                return ZoneGuardResult.Serviceable(result.TopMatch, result.Candidates);
            }
            else
            {
                _log.LogWarning(
                    "[ZoneGuard] ⚠ '{Address}' NOT matched in zone POIs (threshold={Threshold:F2}). " +
                    "Top candidate: {Top} ({Score:F2})",
                    address, result.Threshold,
                    result.TopMatch?.PoiName ?? "none",
                    result.TopMatch?.SimilarityScore ?? 0f);

                // Respect BlockOnNoMatch setting
                if (_cfg.BlockOnNoMatch)
                    return ZoneGuardResult.OutOfZone(result.Candidates, address, result.Threshold);

                // Warn-only mode: pass but surface the result for logging / AI injection
                return new ZoneGuardResult
                {
                    Pass       = true,
                    WasChecked = true,
                    Score      = result.TopMatch?.SimilarityScore ?? 0f,
                    Candidates = result.Candidates,
                    Reason     = $"Warn-only: address not strongly matched in zone POIs " +
                                 $"(best: '{result.TopMatch?.PoiName ?? "none"}' @ {result.TopMatch?.SimilarityScore:F2})"
                };
            }
        }
        catch (OperationCanceledException)
        {
            return ZoneGuardResult.Error("Timeout");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZoneGuard] Unexpected error checking '{Address}'", address);
            return ZoneGuardResult.Error(ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
