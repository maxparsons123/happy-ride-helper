import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// ============================================================================
// API COST TRACKING - Monitor usage to prevent billing surprises
// ============================================================================
let apiCallCounts = {
  geocode: 0,      // $5/1000 - CHEAPEST
  textSearch: 0,   // $32/1000 - EXPENSIVE 
  autocomplete: 0, // $2.83/1000 + details
  placeDetails: 0, // $17/1000
  nearby: 0,       // $32/1000 - EXPENSIVE
};

function logApiCall(type: keyof typeof apiCallCounts) {
  apiCallCounts[type]++;
  const costs = {
    geocode: 0.005,
    textSearch: 0.032,
    autocomplete: 0.00283,
    placeDetails: 0.017,
    nearby: 0.032,
  };
  console.log(`[API_COST] ${type}: call #${apiCallCounts[type]} (est. $${(apiCallCounts[type] * costs[type]).toFixed(4)} total)`);
}

// Simple trigram-based similarity calculation
function calculateSimilarity(str1: string, str2: string): number {
  if (!str1 || !str2) return 0;
  if (str1 === str2) return 1;
  
  const s1 = str1.toLowerCase();
  const s2 = str2.toLowerCase();
  
  // Generate trigrams
  const getTrigrams = (s: string): Set<string> => {
    const trigrams = new Set<string>();
    const padded = `  ${s} `;
    for (let i = 0; i < padded.length - 2; i++) {
      trigrams.add(padded.slice(i, i + 3));
    }
    return trigrams;
  };
  
  const t1 = getTrigrams(s1);
  const t2 = getTrigrams(s2);
  
  // Calculate Jaccard similarity
  let intersection = 0;
  for (const t of t1) {
    if (t2.has(t)) intersection++;
  }
  
  const union = t1.size + t2.size - intersection;
  return union > 0 ? intersection / union : 0;
}

interface GeocodeResult {
  found: boolean;
  address: string;
  display_name?: string;
  lat?: number;
  lon?: number;
  type?: string;
  place_id?: string;
  formatted_address?: string;
  city?: string;
  postcode?: string;
  map_link?: string;
  error?: string;
  multiple_matches?: GeocodeMatch[]; // When multiple similar addresses found
  api_calls?: number; // Track API calls for this request
}

interface GeocodeMatch {
  display_name: string;
  formatted_address: string;
  lat: number;
  lon: number;
  city?: string;
  postcode?: string;
  place_id?: string;
}

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  // Reset call counts for this request
  apiCallCounts = { geocode: 0, textSearch: 0, autocomplete: 0, placeDetails: 0, nearby: 0 };

  try {
    const { address, city, country = "UK", lat, lon, check_ambiguous = false, skip_cache = false } = await req.json();
    
    if (!address) {
      return new Response(
        JSON.stringify({ found: false, address: "", error: "No address provided" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const GOOGLE_MAPS_API_KEY = Deno.env.get("GOOGLE_MAPS_API_KEY");
    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
    
    // Initialize Supabase client for cache operations
    const supabase = SUPABASE_URL && SUPABASE_SERVICE_ROLE_KEY 
      ? createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY)
      : null;
    
    // Normalize the address for cache lookup
    const normalizedAddress = address.toLowerCase().trim().replace(/\s+/g, ' ');
    
    // === CACHE LOOKUP (fuzzy matching) - FREE, always check first ===
    if (!skip_cache && supabase) {
      try {
        // Use trigram similarity for fuzzy matching, optionally filter by city
        let cacheQuery = supabase
          .from("address_cache")
          .select("*")
          .order("use_count", { ascending: false })
          .limit(5);
        
        // If we have a city, prefer matches in that city
        if (city) {
          cacheQuery = cacheQuery.eq("city", city);
        }
        
        const { data: cacheResults, error: cacheError } = await cacheQuery;
        
        if (!cacheError && cacheResults && cacheResults.length > 0) {
          // Find best fuzzy match using simple similarity
          for (const cached of cacheResults) {
            const similarity = calculateSimilarity(normalizedAddress, cached.normalized);
            if (similarity >= 0.7) {
              console.log(`[Geocode] ✅ CACHE HIT: "${address}" → "${cached.display_name}" (similarity: ${similarity.toFixed(2)})`);
              
              // Update use_count and last_used_at (fire-and-forget)
              supabase
                .from("address_cache")
                .update({ use_count: cached.use_count + 1, last_used_at: new Date().toISOString() })
                .eq("id", cached.id)
                .then(() => {});
              
              return new Response(
                JSON.stringify({
                  found: true,
                  address: address,
                  display_name: cached.display_name,
                  lat: cached.lat,
                  lon: cached.lon,
                  city: cached.city,
                  cached: true, // Flag indicating this came from cache
                  map_link: `https://www.google.com/maps?q=${cached.lat},${cached.lon}`,
                  api_calls: 0,
                }),
                { headers: { ...corsHeaders, "Content-Type": "application/json" } }
              );
            }
          }
        }
        
        // Also try without city filter if we didn't find a match
        if (city) {
          const { data: globalResults } = await supabase
            .from("address_cache")
            .select("*")
            .order("use_count", { ascending: false })
            .limit(10);
          
          if (globalResults) {
            for (const cached of globalResults) {
              const similarity = calculateSimilarity(normalizedAddress, cached.normalized);
              if (similarity >= 0.8) { // Higher threshold for cross-city matches
                console.log(`[Geocode] ✅ CACHE HIT (cross-city): "${address}" → "${cached.display_name}" (similarity: ${similarity.toFixed(2)})`);
                
                supabase
                  .from("address_cache")
                  .update({ use_count: cached.use_count + 1, last_used_at: new Date().toISOString() })
                  .eq("id", cached.id)
                  .then(() => {});
                
                return new Response(
                  JSON.stringify({
                    found: true,
                    address: address,
                    display_name: cached.display_name,
                    lat: cached.lat,
                    lon: cached.lon,
                    city: cached.city,
                    cached: true,
                    map_link: `https://www.google.com/maps?q=${cached.lat},${cached.lon}`,
                    api_calls: 0,
                  }),
                  { headers: { ...corsHeaders, "Content-Type": "application/json" } }
                );
              }
            }
          }
        }
        
        console.log(`[Geocode] 🔍 Cache miss for: "${address}"`);
      } catch (cacheErr) {
        console.error("[Geocode] Cache lookup error:", cacheErr);
        // Continue to Google lookup
      }
    }
    
    if (!GOOGLE_MAPS_API_KEY) {
      console.error("[Geocode] GOOGLE_MAPS_API_KEY not configured, falling back to Nominatim");
      return await nominatimFallback(address, city, country);
    }

    // Determine search coordinates - use provided lat/lon first, then city coords
    let searchLat: number | undefined = lat;
    let searchLon: number | undefined = lon;
    
    if (!searchLat || !searchLon) {
      if (city) {
        const cityCoords = getCityCoordinates(city);
        if (cityCoords) {
          searchLat = cityCoords.lat;
          searchLon = cityCoords.lng;
        }
      }
    }

    console.log(`[Geocode] Searching: "${address}" (city: ${city || 'N/A'}, coords: ${searchLat},${searchLon})`);

    // Helper to cache a successful result
    const cacheResult = (result: GeocodeResult) => {
      if (supabase && result.found && result.lat && result.lon && result.display_name) {
        supabase
          .from("address_cache")
          .upsert({
            raw_input: address,
            normalized: normalizedAddress,
            display_name: result.display_name,
            city: result.city || city || null,
            lat: result.lat,
            lon: result.lon,
            use_count: 1,
            last_used_at: new Date().toISOString(),
          }, { onConflict: 'normalized,city' })
          .then(({ error }) => {
            if (error) {
              if (error.message?.includes('unique')) {
                console.log(`[Geocode] Address already cached: "${address}"`);
              } else {
                console.error("[Geocode] Cache save error:", error);
              }
            } else {
              console.log(`[Geocode] 💾 Cached: "${address}" → "${result.display_name}"`);
            }
          });
      }
    };

    // Track total API calls
    const getTotalCalls = () => Object.values(apiCallCounts).reduce((a, b) => a + b, 0);

    // ═══════════════════════════════════════════════════════════════════════════
    // OPTIMIZED STRATEGY (reduced from 5+ calls to max 2 calls):
    // ═══════════════════════════════════════════════════════════════════════════
    // 1️⃣ GEOCODING API FIRST → Cheapest ($5/1000 vs $32/1000 for Text Search)
    // 2️⃣ TEXT SEARCH FALLBACK → Only if Geocoding fails (for POIs/businesses)
    // ═══════════════════════════════════════════════════════════════════════════
    // REMOVED: Road existence pre-check (redundant - geocoding validates this)
    // REMOVED: Autocomplete step (geocoding is more reliable for addresses)
    // REMOVED: Loose local 2-step search (wasteful)
    // ═══════════════════════════════════════════════════════════════════════════
    
    const startsWithNumber = /^\d+[a-z]?\s+/i.test(address.trim());
    
    // 1️⃣ GEOCODING API (CHEAPEST) → Use for all address lookups
    console.log(`[Geocode] 1️⃣ GEOCODING API: "${address}"${city ? ` (city: ${city})` : ''}`);
    const geocodeResult = await googleGeocode(address, city, country, GOOGLE_MAPS_API_KEY, searchLat, searchLon);
    if (geocodeResult.found) {
      console.log(`[Geocode] ✅ GEOCODE_OK: "${geocodeResult.display_name}" (API calls: ${getTotalCalls()})`);
      geocodeResult.api_calls = getTotalCalls();
      cacheResult(geocodeResult);
      return new Response(
        JSON.stringify({ ...geocodeResult, raw_status: "GEOCODE_OK" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // 2️⃣ TEXT SEARCH FALLBACK → Only for POIs/businesses that Geocoding can't find
    // Skip for house addresses (geocoding should have found them)
    if (!startsWithNumber) {
      console.log(`[Geocode] 2️⃣ TEXT SEARCH: "${address}" (POI/business lookup)`);
      const textResult = await googleTextSearchOptimized(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY, city, check_ambiguous);
      if (textResult.found) {
        console.log(`[Geocode] ✅ TEXT_OK: "${textResult.display_name}" (API calls: ${getTotalCalls()})`);
        textResult.api_calls = getTotalCalls();
        cacheResult(textResult);
        return new Response(
          JSON.stringify({ ...textResult, raw_status: "TEXT_OK" }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
    }

    // ❌ Nothing found
    console.log(`[Geocode] ❌ NO_RESULTS for: "${address}" (API calls: ${getTotalCalls()})`);
    return new Response(
      JSON.stringify({ found: false, address, error: "NO_RESULTS", raw_status: "NO_RESULTS", api_calls: getTotalCalls() }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );

  } catch (error) {
    console.error("[Geocode] Error:", error);
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    return new Response(
      JSON.stringify({ 
        found: false, 
        address: "", 
        error: errorMessage 
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" }, status: 500 }
    );
  }
});

// Road type patterns for validation
const ROAD_TYPES: Record<string, string[]> = {
  "road": ["road", "rd"],
  "street": ["street", "st"],
  "avenue": ["avenue", "ave"],
  "drive": ["drive", "dr"],
  "lane": ["lane", "ln"],
  "close": ["close", "cl"],
  "crescent": ["crescent", "cres"],
  "way": ["way"],
  "court": ["court", "ct"],
  "place": ["place", "pl"],
  "grove": ["grove", "gr"],
  "terrace": ["terrace", "ter"],
  "gardens": ["gardens", "gdns"],
  "walk": ["walk"],
  "rise": ["rise"],
  "hill": ["hill"],
};

// Extract the road type from an address (e.g., "52A David Road" → "road")
function extractRoadType(address: string): string | null {
  const lower = address.toLowerCase();
  for (const [canonical, variants] of Object.entries(ROAD_TYPES)) {
    for (const variant of variants) {
      // Match as a whole word (e.g., "road" but not "roadside")
      const regex = new RegExp(`\\b${variant}\\b`, 'i');
      if (regex.test(lower)) {
        return canonical;
      }
    }
  }
  return null;
}

// Check if two road types are compatible (same canonical type)
function roadTypesMatch(type1: string | null, type2: string | null): boolean {
  if (!type1 || !type2) return true; // If we can't detect, allow it
  return type1 === type2;
}

// Extract the core street name without house number and road type
function extractStreetName(address: string): string | null {
  // Remove house number at start
  let cleaned = address.replace(/^\d+[a-z]?\s+/i, '').toLowerCase().trim();
  
  // Remove road type suffix
  for (const variants of Object.values(ROAD_TYPES)) {
    for (const variant of variants) {
      const regex = new RegExp(`\\s+${variant}\\b.*$`, 'i');
      cleaned = cleaned.replace(regex, '');
    }
  }
  
  // Remove common suffixes/city info after comma
  cleaned = cleaned.split(',')[0].trim();
  
  return cleaned || null;
}

// Check if street names match (strict - "David" ≠ "Davids")
function streetNamesMatch(queryStreet: string | null, resultStreet: string | null): boolean {
  if (!queryStreet || !resultStreet) return true;
  
  const q = queryStreet.replace(/['']/g, '').trim();
  const r = resultStreet.replace(/['']/g, '').trim();
  
  if (q === r) return true;
  
  // Allow minor spelling variations (1 character difference) only if both are 6+ chars
  if (q.length >= 6 && r.length >= 6) {
    const shorter = q.length <= r.length ? q : r;
    const longer = q.length > r.length ? q : r;
    if (longer.startsWith(shorter) && (longer.length - shorter.length) <= 1) {
      return true;
    }
  }
  
  return false;
}

// ============================================================================
// OPTIMIZED: Geocoding API - $5/1000 calls (6x cheaper than Text Search)
// ============================================================================
async function googleGeocode(
  query: string,
  city: string | undefined,
  country: string, 
  apiKey: string,
  biasLat?: number,
  biasLon?: number
): Promise<GeocodeResult> {
  try {
    logApiCall('geocode');
    
    const searchQuery = city ? `${query}, ${city}, ${country}` : `${query}, ${country}`;
    
    const params = new URLSearchParams({
      address: searchQuery,
      key: apiKey,
      components: `country:${country === "UK" ? "GB" : country}`,
    });
    
    // Add bounds bias if we have coordinates (helps with local results)
    if (biasLat && biasLon) {
      // Create a ~20km bounding box around the coordinates
      const delta = 0.15; // ~15km in degrees
      params.append('bounds', `${biasLat - delta},${biasLon - delta}|${biasLat + delta},${biasLon + delta}`);
    }

    const url = `https://maps.googleapis.com/maps/api/geocode/json?${params}`;
    console.log(`[Geocode] Geocoding API: "${searchQuery}"${biasLat ? ` (biased to ${biasLat},${biasLon})` : ''}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status === "OK" && data.results && data.results.length > 0) {
      const result = data.results[0];
      const components = result.address_components || [];

      const getComponent = (type: string): string | undefined => {
        const comp = components.find((c: any) => c.types?.includes(type));
        return comp?.long_name;
      };

      const resultCity = getComponent("locality") || 
                   getComponent("postal_town") || 
                   getComponent("administrative_area_level_2");
      const postcode = getComponent("postal_code");
      
      // Validate road type match (Road ≠ Street) - CRITICAL for accuracy
      const queryRoadType = extractRoadType(query);
      const resultRoadType = extractRoadType(result.formatted_address || "");
      
      if (queryRoadType && resultRoadType && !roadTypesMatch(queryRoadType, resultRoadType)) {
        console.log(`[Geocode] Geocode REJECTED: road type mismatch - query "${queryRoadType}" vs result "${resultRoadType}" ("${result.formatted_address}")`);
        return { found: false, address: query };
      }
      
      // Validate street name match (David ≠ Davids)
      const queryStreetName = extractStreetName(query);
      const resultStreetName = extractStreetName(result.formatted_address || "");
      
      if (queryStreetName && resultStreetName && !streetNamesMatch(queryStreetName, resultStreetName)) {
        console.log(`[Geocode] Geocode REJECTED: street name mismatch - query "${queryStreetName}" vs result "${resultStreetName}"`);
        return { found: false, address: query };
      }

      console.log(`[Geocode] ✅ Geocode found: "${result.formatted_address}"`);
      
      return {
        found: true,
        address: query,
        display_name: result.formatted_address,
        formatted_address: result.formatted_address,
        lat: result.geometry.location.lat,
        lon: result.geometry.location.lng,
        type: result.types?.[0] || "address",
        place_id: result.place_id,
        city: resultCity,
        postcode: postcode,
        map_link: `https://maps.google.com/?q=${result.geometry.location.lat},${result.geometry.location.lng}`,
      };
    }

    console.log(`[Geocode] Geocode found no results (status: ${data.status})`);
    return { found: false, address: query };
    
  } catch (error) {
    console.error("[Geocode] Geocode error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// ============================================================================
// OPTIMIZED: Text Search - Only use for POIs/businesses, not addresses
// ============================================================================
async function googleTextSearchOptimized(
  query: string,
  lat: number | undefined,
  lon: number | undefined,
  apiKey: string,
  city?: string,
  returnMultiple: boolean = false
): Promise<GeocodeResult> {
  try {
    logApiCall('textSearch');
    
    // Include city in query for better results
    const searchQuery = city ? `${query}, ${city}` : query;
    
    let url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(searchQuery)}` +
      `&key=${apiKey}`;

    // Add location bias if we have coordinates
    if (lat && lon) {
      url += `&location=${lat},${lon}&radius=10000`; // 10km radius
    }

    console.log(`[Geocode] Text search: "${searchQuery}"${lat ? ` biased to ${lat},${lon}` : ''}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] Text search: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    // Handle multiple matches for disambiguation
    if (returnMultiple && data.results.length > 1) {
      const uniqueAreas = new Set<string>();
      const matches: GeocodeMatch[] = [];
      
      for (const result of data.results.slice(0, 5)) {
        const areaKey = result.formatted_address?.split(',').slice(-2).join(',') || result.place_id;
        if (!uniqueAreas.has(areaKey)) {
          uniqueAreas.add(areaKey);
          matches.push({
            display_name: result.name || result.formatted_address,
            formatted_address: result.formatted_address,
            lat: result.geometry?.location?.lat,
            lon: result.geometry?.location?.lng,
            place_id: result.place_id,
          });
        }
      }
      
      if (matches.length > 1) {
        console.log(`[Geocode] Multiple matches found: ${matches.length} results`);
        const best = data.results[0];
        return {
          found: true,
          address: query,
          display_name: best.name || best.formatted_address,
          formatted_address: best.formatted_address,
          lat: best.geometry?.location?.lat,
          lon: best.geometry?.location?.lng,
          place_id: best.place_id,
          multiple_matches: matches,
          map_link: `https://maps.google.com/?q=${best.geometry?.location?.lat},${best.geometry?.location?.lng}`,
        };
      }
    }

    const best = data.results[0];
    
    // For Text Search, we already have coordinates - no need for Place Details call
    // This saves an additional $17/1000 API call!
    console.log(`[Geocode] ✅ Text search found: "${best.name || best.formatted_address}"`);
    
    return {
      found: true,
      address: query,
      display_name: best.name || best.formatted_address,
      formatted_address: best.formatted_address,
      lat: best.geometry?.location?.lat,
      lon: best.geometry?.location?.lng,
      type: best.types?.[0] || "establishment",
      place_id: best.place_id,
      map_link: `https://maps.google.com/?q=${best.geometry?.location?.lat},${best.geometry?.location?.lng}`,
    };

  } catch (error) {
    console.error("[Geocode] Text search error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

async function nominatimFallback(
  address: string, 
  city: string | undefined, 
  country: string
): Promise<Response> {
  const searchQuery = city ? `${address}, ${city}, ${country}` : `${address}, ${country}`;
  
  console.log(`[Geocode] Nominatim fallback: "${searchQuery}"`);

  const encodedQuery = encodeURIComponent(searchQuery);
  const nominatimUrl = `https://nominatim.openstreetmap.org/search?q=${encodedQuery}&format=json&addressdetails=1&limit=3`;
  
  const response = await fetch(nominatimUrl, {
    headers: {
      "User-Agent": "247RadioCarz-TaxiBooking/1.0",
      "Accept": "application/json"
    }
  });

  if (!response.ok) {
    console.error(`[Geocode] Nominatim API error: ${response.status}`);
    return new Response(
      JSON.stringify({ 
        found: false, 
        address, 
        error: `Geocoding service error: ${response.status}` 
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }

  const results = await response.json();
  console.log(`[Geocode] Nominatim found ${results.length} results`);

  if (results.length === 0) {
    return new Response(
      JSON.stringify({ 
        found: false, 
        address,
        error: "Address not found" 
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }

  const best = results[0];
  const result: GeocodeResult = {
    found: true,
    address,
    display_name: best.display_name,
    lat: parseFloat(best.lat),
    lon: parseFloat(best.lon),
    type: best.type,
    city: best.address?.city || best.address?.town || best.address?.village,
    postcode: best.address?.postcode,
    map_link: `https://maps.google.com/?q=${best.lat},${best.lon}`,
    api_calls: 0, // Nominatim is free
  };

  console.log(`[Geocode] Nominatim best match: "${best.display_name}"`);

  return new Response(
    JSON.stringify(result),
    { headers: { ...corsHeaders, "Content-Type": "application/json" } }
  );
}

// Approximate coordinates for major UK cities for location biasing
function getCityCoordinates(city: string): { lat: number; lng: number } | null {
  const cities: Record<string, { lat: number; lng: number }> = {
    "london": { lat: 51.5074, lng: -0.1278 },
    "birmingham": { lat: 52.4862, lng: -1.8904 },
    "manchester": { lat: 53.4808, lng: -2.2426 },
    "leeds": { lat: 53.8008, lng: -1.5491 },
    "liverpool": { lat: 53.4084, lng: -2.9916 },
    "newcastle": { lat: 54.9783, lng: -1.6178 },
    "sheffield": { lat: 53.3811, lng: -1.4701 },
    "bristol": { lat: 51.4545, lng: -2.5879 },
    "nottingham": { lat: 52.9548, lng: -1.1581 },
    "leicester": { lat: 52.6369, lng: -1.1398 },
    "coventry": { lat: 52.4068, lng: -1.5197 },
    "bradford": { lat: 53.7960, lng: -1.7594 },
    "cardiff": { lat: 51.4816, lng: -3.1791 },
    "edinburgh": { lat: 55.9533, lng: -3.1883 },
    "glasgow": { lat: 55.8642, lng: -4.2518 },
    "belfast": { lat: 54.5973, lng: -5.9301 },
    "cambridge": { lat: 52.2053, lng: 0.1218 },
    "oxford": { lat: 51.7520, lng: -1.2577 },
    "southampton": { lat: 50.9097, lng: -1.4044 },
    "portsmouth": { lat: 50.8198, lng: -1.0880 },
    "brighton": { lat: 50.8225, lng: -0.1372 },
    "reading": { lat: 51.4543, lng: -0.9781 },
    "derby": { lat: 52.9225, lng: -1.4746 },
    "wolverhampton": { lat: 52.5870, lng: -2.1288 },
    "stoke": { lat: 53.0027, lng: -2.1794 },
    "hull": { lat: 53.7676, lng: -0.3274 },
    "york": { lat: 53.9600, lng: -1.0873 },
    "sunderland": { lat: 54.9061, lng: -1.3831 },
    "swansea": { lat: 51.6214, lng: -3.9436 },
    "middlesbrough": { lat: 54.5760, lng: -1.2350 },
    "peterborough": { lat: 52.5695, lng: -0.2405 },
    "luton": { lat: 51.8787, lng: -0.4200 },
    "preston": { lat: 53.7632, lng: -2.7031 },
    "blackpool": { lat: 53.8142, lng: -3.0503 },
    "norwich": { lat: 52.6309, lng: 1.2974 },
    "exeter": { lat: 50.7184, lng: -3.5339 },
    "plymouth": { lat: 50.3755, lng: -4.1427 },
    "aberdeen": { lat: 57.1497, lng: -2.0943 },
    "dundee": { lat: 56.4620, lng: -2.9707 },
  };
  
  const normalized = city.toLowerCase().trim();
  return cities[normalized] || null;
}
