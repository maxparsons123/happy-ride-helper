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
  formattedAddress?: string; // Google's formatted address if validated
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

// Validate a complete address using Google Places Autocomplete
// Returns true if the address is found and matches well
async function validateAddressWithAutocomplete({
  fullAddress,
  lat,
  lon,
  googleApiKey
}: {
  fullAddress: string;
  lat: number;
  lon: number;
  googleApiKey: string;
}): Promise<{ valid: boolean; formattedAddress?: string; placeId?: string }> {
  try {
    // Use Google Places Autocomplete for address validation
    const url = `https://maps.googleapis.com/maps/api/place/autocomplete/json` +
      `?input=${encodeURIComponent(fullAddress)}` +
      `&types=address` +
      `&location=${lat},${lon}` +
      `&radius=8000` +
      `&strictbounds=false` +
      `&components=country:gb` +
      `&key=${googleApiKey}`;
    
    console.log(`[street-disambiguate] Validating address with Autocomplete: "${fullAddress}"`);
    
    const response = await fetch(url);
    if (!response.ok) {
      console.error(`[street-disambiguate] Google Autocomplete error: ${response.status}`);
      return { valid: false };
    }
    
    const data = await response.json();
    
    if (data.status !== "OK" || !data.predictions?.length) {
      console.log(`[street-disambiguate] No Autocomplete results for "${fullAddress}"`);
      return { valid: false };
    }
    
    // Check the top prediction
    const topPrediction = data.predictions[0];
    const description = (topPrediction.description || "").toLowerCase();
    const inputNorm = fullAddress.toLowerCase().replace(/[,\s]+/g, ' ').trim();
    
    // Extract house number from input
    const houseNumMatch = fullAddress.match(/^(\d+[a-z]?)\s+/i);
    const houseNumber = houseNumMatch ? houseNumMatch[1].toLowerCase() : null;
    
    // Check if the prediction contains the house number (if provided)
    if (houseNumber) {
      if (!description.includes(houseNumber)) {
        console.log(`[street-disambiguate] ✗ Autocomplete result doesn't contain house number ${houseNumber}: "${description}"`);
        return { valid: false };
      }
    }
    
    console.log(`[street-disambiguate] ✓ Autocomplete validated: "${fullAddress}" → "${topPrediction.description}"`);
    return { 
      valid: true, 
      formattedAddress: topPrediction.description,
      placeId: topPrediction.place_id
    };
  } catch (error) {
    console.error(`[street-disambiguate] Autocomplete validation error:`, error);
    return { valid: false };
  }
}

// Check if a specific house number exists on a street using Google Text Search ONLY
// NOTE: Autocomplete is unreliable for house number validation - it often returns false negatives
// Text Search with location bias is much more accurate for this purpose
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
}): Promise<{ exists: boolean; formattedAddress?: string }> {
  const fullAddress = `${houseNumber} ${streetName}`;
  
  // Use Text Search ONLY - Autocomplete is unreliable for address validation
  // and often returns false negatives for valid addresses
  try {
    const url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(fullAddress)}` +
      `&location=${lat},${lon}` +
      `&radius=2000` + // 2km radius around the street
      `&key=${googleApiKey}`;
    
    console.log(`[street-disambiguate] Checking house number via Text Search: "${fullAddress}"`);
    
    const response = await fetch(url);
    if (!response.ok) {
      console.error(`[street-disambiguate] Google API error: ${response.status}`);
      return { exists: false };
    }
    
    const data = await response.json();
    
    if (data.status !== "OK" || !data.results?.length) {
      console.log(`[street-disambiguate] No results for "${fullAddress}"`);
      return { exists: false };
    }
    
    // Check if any result contains the house number
    const houseNumNorm = houseNumber.toLowerCase().replace(/[^a-z0-9]/g, '');
    
    for (const result of data.results) {
      const formattedAddress = (result.formatted_address || "").toLowerCase();
      const resultNorm = formattedAddress.replace(/[^a-z0-9]/g, '');
      
      // Check if the house number appears at the start of the address
      if (resultNorm.startsWith(houseNumNorm) || formattedAddress.includes(` ${houseNumber.toLowerCase()} `)) {
        console.log(`[street-disambiguate] ✓ House number ${houseNumber} EXISTS at "${result.formatted_address}"`);
        return { exists: true, formattedAddress: result.formatted_address };
      }
    }
    
    console.log(`[street-disambiguate] ✗ House number ${houseNumber} NOT found for "${fullAddress}"`);
    return { exists: false };
  } catch (error) {
    console.error(`[street-disambiguate] House number check error:`, error);
    return { exists: false };
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
    // Log all location fields for debugging
    console.log(`[street-disambiguate] OS result: NAME1=${g.NAME1}, POPULATED_PLACE=${g.POPULATED_PLACE}, SUBURB=${g.SUBURB}, DISTRICT_BOROUGH=${g.DISTRICT_BOROUGH}, COUNTY_UNITARY=${g.COUNTY_UNITARY}`);
    
    // Priority for area: POPULATED_PLACE > SUBURB > first part of DISTRICT_BOROUGH
    // This ensures "Perry Barr" or "Sutton Coldfield" is used instead of just "Birmingham"
    const area = g.POPULATED_PLACE || g.SUBURB || null;
    const borough = g.DISTRICT_BOROUGH || g.COUNTY_UNITARY || null;
    
    return {
      name: g.NAME1,
      area: area,
      borough: borough,
      lat: g.GEOMETRY_Y,
      lon: g.GEOMETRY_X
    };
  });

  // Group by unique area+borough combinations
  // IMPORTANT: Use the most specific area name to distinguish streets within the same city
  const uniqueAreaMap = new Map<string, StreetMatch>();
  for (const match of matches) {
    // Create a unique key that distinguishes streets in different sub-areas
    // If area is null but borough exists, use borough as the key
    const areaKey = match.area || match.borough || "Unknown";
    const key = `${areaKey}|${match.lat.toFixed(3)},${match.lon.toFixed(3)}`; // Include rough coords to catch truly different locations
    
    if (!uniqueAreaMap.has(key)) {
      uniqueAreaMap.set(key, match);
      console.log(`[street-disambiguate] Added unique match: ${match.name} in ${match.area || match.borough || "Unknown"}`);
    }
  }

  let uniqueMatches = Array.from(uniqueAreaMap.values());
  
  // FALLBACK: If all matches have the same area but different boroughs, use borough for disambiguation
  const allSameArea = uniqueMatches.length > 1 && uniqueMatches.every(m => m.area === uniqueMatches[0].area);
  if (allSameArea && uniqueMatches.some(m => m.borough !== uniqueMatches[0].borough)) {
    console.log(`[street-disambiguate] All matches have same area "${uniqueMatches[0].area}" - using borough for disambiguation`);
    // Swap area and borough for display purposes
    uniqueMatches = uniqueMatches.map(m => ({
      ...m,
      area: m.borough || m.area, // Use borough as the display area
    }));
  }
  
  console.log(`[street-disambiguate] Found ${uniqueMatches.length} unique areas for "${streetOnly}"`);


  // HOUSE NUMBER DISAMBIGUATION: If we have multiple matches AND a house number,
  // check which streets actually have that house number
  if (uniqueMatches.length > 1 && houseNumber && googleApiKey) {
    console.log(`[street-disambiguate] 🏠 Checking house number ${houseNumber} on ${uniqueMatches.length} streets...`);
    
    // Check house number existence in parallel for all matches
    const houseNumberChecks = await Promise.all(
      uniqueMatches.map(async (match) => {
        const result = await checkHouseNumberExists({
          houseNumber,
          streetName: match.name,
          lat: match.lat,
          lon: match.lon,
          googleApiKey
        });
        return { match, ...result };
      })
    );
    
    // Filter to only streets that have the house number
    const matchesWithHouse = houseNumberChecks.filter(c => c.exists).map(c => ({
      ...c.match,
      hasHouseNumber: true,
      formattedAddress: c.formattedAddress
    }));
    
    console.log(`[street-disambiguate] 🏠 House number ${houseNumber} found on ${matchesWithHouse.length}/${uniqueMatches.length} streets`);
    
    if (matchesWithHouse.length === 1) {
      // SUCCESS: House number uniquely identifies the street!
      const resolved = matchesWithHouse[0];
      console.log(`[street-disambiguate] ✓ House number ${houseNumber} uniquely resolves to ${resolved.name} in ${resolved.area || resolved.borough}`);
      return {
        found: true,
        street: streetOnly,
        houseNumber,
        multiple: false,
        matches: matchesWithHouse,
        needsClarification: false,
        resolvedMatch: resolved
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
