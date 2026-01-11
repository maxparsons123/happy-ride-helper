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
  hasHouseNumber?: boolean; // True if this street has the specified house number
}

export interface StreetDisambiguationResult {
  found: boolean;
  street: string;
  houseNumber?: string; // If provided
  multiple: boolean;
  matches: StreetMatch[];
  needsClarification: boolean;
  clarificationPrompt?: string;
  resolvedMatch?: StreetMatch; // If house number uniquely identifies the street
  error?: string;
}

const OS_API_URL = "https://api.os.uk/search/names/v1/find";

// Check if a specific house number exists on a street using Google Places
async function checkHouseNumberExists({
  houseNumber,
  streetName,
  lat,
  lon,
  googleApiKey
}: {
  houseNumber: string;
  streetName: string;
  lat: number;
  lon: number;
  googleApiKey: string;
}): Promise<boolean> {
  const fullAddress = `${houseNumber} ${streetName}`;
  
  try {
    // Use Google Places Text Search biased to the street's location
    const url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(fullAddress)}` +
      `&location=${lat},${lon}` +
      `&radius=2000` + // 2km radius around the street
      `&key=${googleApiKey}`;
    
    console.log(`[street-disambiguate] Checking house number: "${fullAddress}" near ${lat},${lon}`);
    
    const response = await fetch(url);
    if (!response.ok) {
      console.error(`[street-disambiguate] Google API error: ${response.status}`);
      return false;
    }
    
    const data = await response.json();
    
    if (data.status !== "OK" || !data.results?.length) {
      console.log(`[street-disambiguate] No results for "${fullAddress}"`);
      return false;
    }
    
    // Check if any result contains the house number
    const houseNumNorm = houseNumber.toLowerCase().replace(/[^a-z0-9]/g, '');
    
    for (const result of data.results) {
      const formattedAddress = (result.formatted_address || "").toLowerCase();
      const resultNorm = formattedAddress.replace(/[^a-z0-9]/g, '');
      
      // Check if the house number appears at the start of the address
      if (resultNorm.startsWith(houseNumNorm) || formattedAddress.includes(` ${houseNumber.toLowerCase()} `)) {
        console.log(`[street-disambiguate] ✓ House number ${houseNumber} EXISTS at "${result.formatted_address}"`);
        return true;
      }
    }
    
    console.log(`[street-disambiguate] ✗ House number ${houseNumber} NOT found for "${fullAddress}"`);
    return false;
  } catch (error) {
    console.error(`[street-disambiguate] House number check error:`, error);
    return false;
  }
}

async function disambiguateStreet({
  street,
  houseNumber,
  lat,
  lon,
  radiusMeters = 8000, // ~5 miles
  osApiKey,
  googleApiKey
}: {
  street: string;
  houseNumber?: string;
  lat: number;
  lon: number;
  radiusMeters?: number;
  osApiKey: string;
  googleApiKey?: string;
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

  let uniqueMatches = Array.from(uniqueAreaMap.values());
  console.log(`[street-disambiguate] Found ${uniqueMatches.length} unique areas for "${streetOnly}"`);

  // HOUSE NUMBER DISAMBIGUATION: If we have multiple matches AND a house number,
  // check which streets actually have that house number
  if (uniqueMatches.length > 1 && houseNumber && googleApiKey) {
    console.log(`[street-disambiguate] 🏠 Checking house number ${houseNumber} on ${uniqueMatches.length} streets...`);
    
    // Check house number existence in parallel for all matches
    const houseNumberChecks = await Promise.all(
      uniqueMatches.map(async (match) => {
        const exists = await checkHouseNumberExists({
          houseNumber,
          streetName: match.name,
          lat: match.lat,
          lon: match.lon,
          googleApiKey
        });
        return { match, exists };
      })
    );
    
    // Filter to only streets that have the house number
    const matchesWithHouse = houseNumberChecks.filter(c => c.exists).map(c => ({
      ...c.match,
      hasHouseNumber: true
    }));
    
    console.log(`[street-disambiguate] 🏠 House number ${houseNumber} found on ${matchesWithHouse.length}/${uniqueMatches.length} streets`);
    
    if (matchesWithHouse.length === 1) {
      // SUCCESS: House number uniquely identifies the street!
      console.log(`[street-disambiguate] ✓ House number ${houseNumber} uniquely resolves to ${matchesWithHouse[0].name} in ${matchesWithHouse[0].area || matchesWithHouse[0].borough}`);
      return {
        found: true,
        street: streetOnly,
        houseNumber,
        multiple: false,
        matches: matchesWithHouse,
        needsClarification: false,
        resolvedMatch: matchesWithHouse[0]
      };
    } else if (matchesWithHouse.length > 1) {
      // Multiple streets have this house number - still need disambiguation but narrower
      uniqueMatches = matchesWithHouse;
      console.log(`[street-disambiguate] House number ${houseNumber} exists on multiple streets, narrowed to ${uniqueMatches.length}`);
    } else if (matchesWithHouse.length === 0) {
      // House number doesn't exist on any of these streets - keep all matches and ask
      console.log(`[street-disambiguate] ⚠️ House number ${houseNumber} not found on any ${streetOnly} variant`);
      // Mark all as not having the house number
      uniqueMatches = uniqueMatches.map(m => ({ ...m, hasHouseNumber: false }));
    }
  }

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
      houseNumber,
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
    houseNumber,
    multiple: false,
    matches: uniqueMatches,
    needsClarification: false,
    resolvedMatch: uniqueMatches[0]
  };
}

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const osApiKey = Deno.env.get("OS_API_KEY");
    const googleApiKey = Deno.env.get("GOOGLE_MAPS_API_KEY");
    
    if (!osApiKey) {
      console.error("[street-disambiguate] OS_API_KEY not configured");
      return new Response(
        JSON.stringify({ error: "OS API key not configured" }),
        { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const body = await req.json();
    const { street, houseNumber, lat, lon, radiusMeters } = body;

    if (!street) {
      return new Response(
        JSON.stringify({ error: "Missing 'street' parameter" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Default to Birmingham if no coordinates provided
    const searchLat = lat ?? 52.4862;
    const searchLon = lon ?? -1.8904;

    console.log(`[street-disambiguate] Request: street="${street}", houseNumber=${houseNumber || "none"}, lat=${searchLat}, lon=${searchLon}, radius=${radiusMeters || 8000}`);

    const result = await disambiguateStreet({
      street,
      houseNumber,
      lat: searchLat,
      lon: searchLon,
      radiusMeters: radiusMeters || 8000,
      osApiKey,
      googleApiKey
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
