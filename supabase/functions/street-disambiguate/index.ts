import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

export interface StreetMatch {
  name: string;
  area?: string;
  borough?: string;
  lat: number;
  lon: number;
}

export interface StreetDisambiguationResult {
  found: boolean;
  street: string;
  multiple: boolean;
  matches: StreetMatch[];
  needsClarification: boolean;
  clarificationPrompt?: string;
  error?: string;
}

const OS_API_URL = "https://api.os.uk/search/names/v1/find";

async function disambiguateStreet({
  street,
  lat,
  lon,
  radiusMeters = 8000, // ~5 miles
  osApiKey
}: {
  street: string;
  lat: number;
  lon: number;
  radiusMeters?: number;
  osApiKey: string;
}): Promise<StreetDisambiguationResult> {
  if (!street || !osApiKey) {
    return {
      found: false,
      street,
      multiple: false,
      matches: [],
      needsClarification: false,
      error: "Missing street name or OS API key"
    };
  }

  // Extract just the road name without city suffix for searching
  // e.g., "School Road, Birmingham" -> "School Road"
  const streetOnly = street.split(",")[0].trim();

  const url =
    `${OS_API_URL}` +
    `?query=${encodeURIComponent(streetOnly)}` +
    `&fq=LOCAL_TYPE:Street` +
    `&point=${lat},${lon}` +
    `&radius=${radiusMeters}` +
    `&key=${osApiKey}`;

  console.log(`[street-disambiguate] Querying OS API: ${url.replace(osApiKey, "***")}`);

  const res = await fetch(url);
  if (!res.ok) {
    const errorText = await res.text();
    console.error(`[street-disambiguate] OS API error ${res.status}: ${errorText}`);
    return {
      found: false,
      street,
      multiple: false,
      matches: [],
      needsClarification: false,
      error: `OS API error ${res.status}`
    };
  }

  const json = await res.json();
  const results = json?.results ?? [];

  console.log(`[street-disambiguate] OS API returned ${results.length} results for "${streetOnly}"`);

  if (results.length === 0) {
    return {
      found: false,
      street,
      multiple: false,
      matches: [],
      needsClarification: false,
      error: "Street not found"
    };
  }

  // Extract and normalize matches
  const matches: StreetMatch[] = results.map((r: any) => {
    const g = r.GAZETTEER_ENTRY;
    return {
      name: g.NAME1,
      area: g.POPULATED_PLACE || g.SUBURB || undefined,
      borough: g.DISTRICT_BOROUGH || undefined,
      lat: g.GEOMETRY_Y,
      lon: g.GEOMETRY_X
    };
  });

  // Group by unique area+borough combinations
  const uniqueAreaMap = new Map<string, StreetMatch>();
  for (const match of matches) {
    const key = `${match.area || ""}|${match.borough || ""}`;
    if (!uniqueAreaMap.has(key)) {
      uniqueAreaMap.set(key, match);
    }
  }

  const uniqueMatches = Array.from(uniqueAreaMap.values());
  console.log(`[street-disambiguate] Found ${uniqueMatches.length} unique areas for "${streetOnly}"`);

  if (uniqueMatches.length > 1) {
    // Build clarification options - prefer area, fallback to borough
    const options = uniqueMatches
      .slice(0, 4) // Limit to 4 options for clarity
      .map(m => {
        const location = m.area || m.borough || "Unknown";
        return `${m.name} in ${location}`;
      });

    const clarificationPrompt =
      `I've found ${streetOnly} in a few areas nearby — do you mean ` +
      options.slice(0, -1).join(", ") +
      (options.length > 1 ? `, or ${options[options.length - 1]}` : options[0]) +
      `?`;

    console.log(`[street-disambiguate] Needs clarification: ${options.join(" | ")}`);

    return {
      found: true,
      street: streetOnly,
      multiple: true,
      matches: uniqueMatches,
      needsClarification: true,
      clarificationPrompt
    };
  }

  // Single match - no disambiguation needed
  return {
    found: true,
    street: streetOnly,
    multiple: false,
    matches: uniqueMatches,
    needsClarification: false
  };
}

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const osApiKey = Deno.env.get("OS_API_KEY");
    if (!osApiKey) {
      console.error("[street-disambiguate] OS_API_KEY not configured");
      return new Response(
        JSON.stringify({ error: "OS API key not configured" }),
        { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const body = await req.json();
    const { street, lat, lon, radiusMeters } = body;

    if (!street) {
      return new Response(
        JSON.stringify({ error: "Missing 'street' parameter" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Default to Birmingham if no coordinates provided
    const searchLat = lat ?? 52.4862;
    const searchLon = lon ?? -1.8904;

    console.log(`[street-disambiguate] Request: street="${street}", lat=${searchLat}, lon=${searchLon}, radius=${radiusMeters || 8000}`);

    const result = await disambiguateStreet({
      street,
      lat: searchLat,
      lon: searchLon,
      radiusMeters: radiusMeters || 8000,
      osApiKey
    });

    return new Response(
      JSON.stringify(result),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : "Internal server error";
    console.error("[street-disambiguate] Error:", error);
    return new Response(
      JSON.stringify({ error: errorMessage }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
