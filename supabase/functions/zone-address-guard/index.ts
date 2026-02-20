/**
 * zone-address-guard — Zone POI-based pickup address validator
 *
 * Accepts a raw address string and returns fuzzy-matched zone POIs
 * so callers can determine:
 *   1. Whether the address falls within a known serviceable area
 *   2. The best-matching canonical street/business name for readback
 *   3. Which zone/company services that location
 *
 * This feature is BUILT BUT DISABLED in production until the ZoneGuard
 * feature flag is enabled in appsettings.json (ZoneGuard.Enabled = true).
 *
 * POST body:
 *   {
 *     address: string          // raw pickup address to validate
 *     company_id?: string      // optional: filter to a specific company's zones
 *     min_similarity?: number  // optional: override minimum score (default 0.25)
 *     limit?: number           // optional: max results (default 10)
 *   }
 *
 * Response:
 *   {
 *     is_serviceable: boolean   // true if top match score >= threshold
 *     top_match: ZonePoi | null // best matching POI
 *     candidates: ZonePoi[]     // all matches above threshold
 *     address_input: string     // echoed back for logging
 *   }
 */

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers":
    "authorization, x-client-info, apikey, content-type",
};

const SERVICEABILITY_THRESHOLD = 0.35; // must score >= this to count as "serviceable"

interface RequestBody {
  address: string;
  company_id?: string;
  min_similarity?: number;
  limit?: number;
}

interface ZonePoiMatch {
  poi_id: string;
  poi_name: string;
  poi_type: string;
  area: string | null;
  zone_id: string;
  zone_name: string;
  company_id: string | null;
  similarity_score: number;
  lat: number | null;
  lng: number | null;
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  if (req.method !== "POST") {
    return new Response(JSON.stringify({ error: "POST required" }), {
      status: 405,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  try {
    const body = (await req.json()) as RequestBody;
    const { address, company_id, min_similarity = 0.25, limit = 10 } = body;

    if (!address || address.trim().length < 3) {
      return new Response(
        JSON.stringify({ error: "address must be at least 3 characters" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
    const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const sb = createClient(supabaseUrl, serviceKey);

    // Strip house number prefix before matching — we're matching street names
    // e.g. "52A David Road" → "David Road" for POI fuzzy matching
    const streetOnly = stripHouseNumber(address.trim());
    console.log(`[zone-address-guard] input="${address}" street_only="${streetOnly}"`);

    // Call word_fuzzy_match_zone_poi — uses GREATEST(similarity, word_similarity)
    // for better partial matches on multi-word street names
    const { data: matches, error } = await sb.rpc("word_fuzzy_match_zone_poi", {
      p_address: streetOnly,
      p_min_similarity: min_similarity,
      p_limit: limit,
    });

    if (error) {
      console.error("[zone-address-guard] RPC error:", error);
      return new Response(
        JSON.stringify({ error: "Database query failed", detail: error.message }),
        { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    let candidates = (matches as ZonePoiMatch[]) || [];

    // Filter to specific company if requested
    if (company_id) {
      candidates = candidates.filter((m) => m.company_id === company_id);
    }

    const topMatch = candidates.length > 0 ? candidates[0] : null;
    const isServiceable =
      topMatch !== null && topMatch.similarity_score >= SERVICEABILITY_THRESHOLD;

    const response = {
      is_serviceable: isServiceable,
      top_match: topMatch,
      candidates,
      address_input: address,
      street_normalized: streetOnly,
      threshold: SERVICEABILITY_THRESHOLD,
    };

    console.log(
      `[zone-address-guard] result: serviceable=${isServiceable} top="${topMatch?.poi_name ?? "none"}" score=${topMatch?.similarity_score?.toFixed(3) ?? "n/a"}`
    );

    return new Response(JSON.stringify(response), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (err) {
    console.error("[zone-address-guard] Unhandled error:", err);
    return new Response(
      JSON.stringify({
        error: err instanceof Error ? err.message : "Unknown error",
      }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});

/**
 * Strip a leading house number from an address so we match the street name
 * against POI names, not the house-number-prefixed full address.
 * Examples:
 *   "52A David Road"        → "David Road"
 *   "Flat 2 52A David Road" → "David Road"
 *   "Manchester Airport"    → "Manchester Airport" (unchanged)
 */
function stripHouseNumber(address: string): string {
  // Remove flat/unit prefix
  let s = address.replace(/^(flat|unit|apt)\s+\S+\s+/i, "").trim();
  // Remove leading house number (digits optionally followed by letters, e.g. 52A, 1-3)
  s = s.replace(/^\d+[-A-Za-z]?\d*\s+/, "").trim();
  return s || address; // fallback to original if stripping left nothing
}
