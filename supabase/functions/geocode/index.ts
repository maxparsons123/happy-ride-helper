import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

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
    
    // === CACHE LOOKUP (fuzzy matching) ===
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

    console.log(`[Geocode] Searching Google: "${address}" (city: ${city || 'N/A'}, coords: ${searchLat},${searchLon})`);

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
              // Try without city constraint for null city
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

    // Check if user is asking for something "nearby" (e.g., "nearby Tesco", "nearest pharmacy")
    const nearbyKeywords = /\b(nearby|nearest|closest|near me|around here)\b/i;
    const isNearbyQuery = nearbyKeywords.test(address);

    // ═══════════════════════════════════════════════════════════════════════════
    // STRATEGY (matching C# PlaceResolver pattern):
    // ═══════════════════════════════════════════════════════════════════════════
    // 0️⃣ ROAD EXISTENCE CHECK → Verify the road actually exists before searching
    // 1️⃣ TEXT SEARCH (local, 5km) → Place search first (best for most queries)
    // 2️⃣ AUTOCOMPLETE → Fallback for house addresses (types=address)
    // 3️⃣ LOOSE LOCAL (8km) → Wider radius search
    // 4️⃣ GLOBAL SEARCH → For dropoffs that may be far away
    // ═══════════════════════════════════════════════════════════════════════════
    
    // Detect house-number addresses (e.g., "52A David Road", "123 High Street")
    const startsWithNumber = /^\d+[a-z]?\s+/i.test(address.trim());
    
    // 0️⃣ ROAD EXISTENCE CHECK → For house addresses, verify the road exists first
    // This prevents matching wrong streets like "David St" when user says "David Road"
    if (startsWithNumber && searchLat && searchLon) {
      const roadExists = await doesRoadExist(address, GOOGLE_MAPS_API_KEY, searchLat, searchLon, city);
      if (!roadExists) {
        console.log(`[Geocode] ⚠️ ROAD_NOT_FOUND: "${address}" does not exist in ${city || 'local area'} - returning not found`);
        return new Response(
          JSON.stringify({ 
            found: false, 
            address, 
            error: "ROAD_NOT_FOUND", 
            raw_status: "ROAD_NOT_FOUND",
            message: `The road "${extractStreetNameWithType(address)}" could not be found in ${city || 'the local area'}. Please check the street name or provide a postcode.`
          }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
      console.log(`[Geocode] ✅ Road verified: "${extractStreetNameWithType(address)}" exists in ${city || 'local area'}`);
    }
    
    // 1️⃣ TEXT SEARCH (local, 5km biased) → Try place search FIRST
    if (searchLat && searchLon) {
      console.log(`[Geocode] 1️⃣ TEXT SEARCH (local): "${address}" biased to ${searchLat},${searchLon}${city ? ` (city: ${city})` : ''}`);
      const textResult = await googleTextSearchLocal(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY, city);
      if (textResult.found) {
        console.log(`[Geocode] ✅ TEXT_OK: "${textResult.display_name}"`);
        cacheResult(textResult);
        return new Response(
          JSON.stringify({ ...textResult, raw_status: "TEXT_OK" }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
    }

    // 2️⃣ AUTOCOMPLETE → Fallback for house addresses
    if (startsWithNumber && searchLat && searchLon) {
      console.log(`[Geocode] 2️⃣ AUTOCOMPLETE: "${address}" - trying types=address`);
      const autocompleteResult = await googlePlacesAutocomplete(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY, city);
      if (autocompleteResult.found) {
        console.log(`[Geocode] ✅ AUTOCOMPLETE_OK: "${autocompleteResult.display_name}"`);
        cacheResult(autocompleteResult);
        return new Response(
          JSON.stringify({ ...autocompleteResult, raw_status: "AUTOCOMPLETE_OK" }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
    }

    // 3️⃣ LOOSE LOCAL (8km) → Wider radius search
    if (searchLat && searchLon) {
      console.log(`[Geocode] 3️⃣ LOOSE LOCAL: "${address}" 8km radius`);
      const looseResult = await googleLooseLocalSearch(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY, check_ambiguous, city);
      if (looseResult.found) {
        console.log(`[Geocode] ✅ LOCAL_OK: "${looseResult.display_name}"`);
        cacheResult(looseResult);
        return new Response(
          JSON.stringify({ ...looseResult, raw_status: "LOCAL_OK" }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
    }

    // 4️⃣ GLOBAL SEARCH → for dropoffs that may be far away (still include city for bias)
    console.log(`[Geocode] 4️⃣ GLOBAL SEARCH: "${address}"${city ? ` (with city: ${city})` : ''}`);
    const globalResult = await googleTextSearch(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY, check_ambiguous, city);
    if (globalResult.found) {
      console.log(`[Geocode] ✅ GLOBAL_OK: "${globalResult.display_name}"`);
      cacheResult(globalResult);
      return new Response(
        JSON.stringify({ ...globalResult, raw_status: "GLOBAL_OK" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // 5️⃣ NEARBY SEARCH → only for explicit "nearby" keywords
    if (isNearbyQuery && searchLat && searchLon) {
      console.log(`[Geocode] 5️⃣ NEARBY: "${address}"`);
      const nearbyResult = await googleNearbySearch(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY);
      if (nearbyResult.found) {
        console.log(`[Geocode] ✅ NEARBY_OK: "${nearbyResult.display_name}"`);
        cacheResult(nearbyResult);
        return new Response(
          JSON.stringify({ ...nearbyResult, raw_status: "NEARBY_OK" }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
    }

    // ❌ Nothing found
    console.log(`[Geocode] ❌ NO_RESULTS for: "${address}"`);
    return new Response(
      JSON.stringify({ found: false, address, error: "NO_RESULTS", raw_status: "NO_RESULTS" }),
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
// e.g., "52A David Road" → "david", "Davids Rd" → "davids"
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
  if (!queryStreet || !resultStreet) return true; // If we can't detect, allow it
  
  // Normalize: remove apostrophes, trim
  const q = queryStreet.replace(/['']/g, '').trim();
  const r = resultStreet.replace(/['']/g, '').trim();
  
  // Exact match
  if (q === r) return true;
  
  // Allow minor spelling variations (1 character difference) only if both are 6+ chars
  if (q.length >= 6 && r.length >= 6) {
    const shorter = q.length <= r.length ? q : r;
    const longer = q.length > r.length ? q : r;
    
    // Check if one is a prefix of the other (with max 1 char difference)
    if (longer.startsWith(shorter) && (longer.length - shorter.length) <= 1) {
      return true;
    }
  }
  
  return false;
}

// Extract just the street name portion (without house number) for road existence check
// e.g., "52A David Road" → "David Road"
function extractStreetNameWithType(input: string): string {
  // Remove house number at start: "52A David Road" → "David Road"
  const cleaned = input.replace(/^\s*\d+[a-zA-Z]?\s*/i, '');
  return cleaned.trim();
}

// Check if a road actually exists in the given area using Google Text Search
// NOTE: Autocomplete is unreliable and often returns false negatives for valid roads
// Text Search is more reliable for road existence validation
async function doesRoadExist(
  userInput: string,
  apiKey: string,
  lat: number,
  lon: number,
  city?: string
): Promise<boolean> {
  if (!userInput || userInput.trim().length === 0) {
    return false;
  }

  // 1️⃣ Extract street name (remove house number)
  const street = extractStreetNameWithType(userInput);
  if (!street || street.length < 3) {
    console.log(`[Geocode] doesRoadExist: no valid street extracted from "${userInput}"`);
    return false;
  }

  const queryRoadType = extractRoadType(userInput);
  const queryStreetName = extractStreetName(userInput);

  // 2️⃣ Build Text Search query (more reliable than Autocomplete)
  const searchQuery = city ? `${street}, ${city}, UK` : `${street}, UK`;

  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
    `?query=${encodeURIComponent(searchQuery)}` +
    `&location=${lat},${lon}` +
    `&radius=8000` +
    `&key=${apiKey}`;

  console.log(`[Geocode] doesRoadExist (TextSearch): checking "${searchQuery}" near ${lat},${lon}`);

  try {
    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] doesRoadExist: no results found for "${street}"`);
      return false;
    }

    // 3️⃣ Compare street names safely - must match BOTH road type AND street name
    for (const result of data.results) {
      const formattedAddress = result.formatted_address || "";
      if (!formattedAddress) continue;

      const resultRoadType = extractRoadType(formattedAddress);
      const resultStreetName = extractStreetName(formattedAddress);

      // Check road type match (Road ≠ Street)
      const roadTypeOk = roadTypesMatch(queryRoadType, resultRoadType);
      
      // Check street name match (David ≠ Davids)
      const streetNameOk = streetNamesMatch(queryStreetName, resultStreetName);

      if (roadTypeOk && streetNameOk) {
        console.log(`[Geocode] doesRoadExist: ✅ FOUND "${formattedAddress}" matches "${street}" (roadType: ${resultRoadType}, streetName: ${resultStreetName})`);
        return true;
      } else {
        console.log(`[Geocode] doesRoadExist: ❌ REJECTED "${formattedAddress}" - roadType: ${queryRoadType}→${resultRoadType} (${roadTypeOk ? 'OK' : 'MISMATCH'}), streetName: ${queryStreetName}→${resultStreetName} (${streetNameOk ? 'OK' : 'MISMATCH'})`);
      }
    }

    console.log(`[Geocode] doesRoadExist: no matching road found for "${street}"`);
    return false;

  } catch (error) {
    console.error(`[Geocode] doesRoadExist error:`, error);
    return false;
  }
}

// Places Autocomplete - best for residential addresses with house numbers
// This is more accurate than Text Search for "52A David Road" style queries
async function googlePlacesAutocomplete(
  query: string,
  lat: number | undefined,
  lon: number | undefined,
  apiKey: string,
  city?: string
): Promise<GeocodeResult> {
  try {
    // Include city in the input query for better accuracy (like C# ResolveHouseAddress)
    const searchQuery = city ? `${query}, ${city}` : query;
    
    let url = `https://maps.googleapis.com/maps/api/place/autocomplete/json` +
      `?input=${encodeURIComponent(searchQuery)}` +
      `&types=address` +  // Restrict to addresses only
      `&components=country:gb` +  // UK only
      `&key=${apiKey}`;

    // Add location bias if we have coordinates
    if (lat && lon) {
      url += `&location=${lat},${lon}&radius=5000`; // 5km radius (matching C# pattern)
    }

    console.log(`[Geocode] Autocomplete: "${searchQuery}"${lat ? ` biased to ${lat},${lon}` : ''}${city ? ` (city: ${city})` : ''}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.predictions || data.predictions.length === 0) {
      console.log(`[Geocode] Autocomplete: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    // Extract query components for validation
    const queryHouseNumber = query.match(/^(\d+[a-z]?)/i)?.[1]?.toLowerCase();
    const queryRoadType = extractRoadType(query);
    
    console.log(`[Geocode] Query analysis: house="${queryHouseNumber}", roadType="${queryRoadType}"`);
    
    // Find the best match - must match house number AND road type
    let bestPrediction = null;
    
    for (const pred of data.predictions) {
      const predText = (pred.description || "").toLowerCase();
      const predRoadType = extractRoadType(predText);
      
      // Check house number match
      let houseMatches = true;
      if (queryHouseNumber) {
        houseMatches = predText.startsWith(queryHouseNumber) || 
                       predText.includes(` ${queryHouseNumber} `) ||
                       predText.includes(`${queryHouseNumber},`);
      }
      
      // Check road type match (CRITICAL: Road ≠ Street)
      const roadTypeMatches = roadTypesMatch(queryRoadType, predRoadType);
      
      if (houseMatches && roadTypeMatches) {
        bestPrediction = pred;
        console.log(`[Geocode] Autocomplete match: "${pred.description}" (roadType: ${predRoadType})`);
        break;
      } else if (houseMatches && !roadTypeMatches) {
        console.log(`[Geocode] Autocomplete REJECTED: "${pred.description}" - road type mismatch (query: ${queryRoadType}, result: ${predRoadType})`);
      }
    }

    if (!bestPrediction) {
      console.log(`[Geocode] Autocomplete: no valid matches (road type or house number mismatch)`);
      return { found: false, address: query };
    }

    const placeId = bestPrediction.place_id;
    if (!placeId) {
      return { found: false, address: query };
    }

    console.log(`[Geocode] Autocomplete best match: "${bestPrediction.description}"`);

    // Get full place details for coordinates
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Autocomplete error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Nearby Search - finds places closest to the caller's location
// IMPORTANT: Only use for business/POI lookups, NOT for street addresses
async function googleNearbySearch(
  query: string,
  lat: number,
  lon: number,
  apiKey: string
): Promise<GeocodeResult> {
  try {
    // Skip nearby search for street addresses (contain numbers followed by letters)
    // This prevents "52A David Road" from matching random businesses
    const looksLikeStreetAddress = /^\d+[a-z]?\s+/i.test(query.trim());
    if (looksLikeStreetAddress) {
      console.log(`[Geocode] Nearby search: skipping (looks like street address: "${query}")`);
      return { found: false, address: query };
    }

    const url = `https://maps.googleapis.com/maps/api/place/nearbysearch/json` +
      `?location=${lat},${lon}` +
      `&rankby=distance` +
      `&keyword=${encodeURIComponent(query)}` +
      `&key=${apiKey}`;

    console.log(`[Geocode] Nearby search: "${query}" near ${lat},${lon}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] Nearby search: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    // Validate that the result actually matches the query (fuzzy check)
    const bestResult = data.results[0];
    const resultName = (bestResult.name || "").toLowerCase();
    const queryLower = query.toLowerCase();
    
    // Check if the result name contains key words from the query
    const queryWords = queryLower.split(/\s+/).filter(w => w.length > 2);
    const matchingWords = queryWords.filter(w => resultName.includes(w));
    const matchRatio = queryWords.length > 0 ? matchingWords.length / queryWords.length : 0;
    
    if (matchRatio < 0.3) {
      console.log(`[Geocode] Nearby search: rejecting poor match - "${bestResult.name}" doesn't match "${query}" (ratio: ${matchRatio.toFixed(2)})`);
      return { found: false, address: query };
    }

    const placeId = bestResult.place_id;
    if (!placeId) {
      return { found: false, address: query };
    }

    // Get full place details
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Nearby search error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Loose Local Search - location-biased text search with tight 8km radius
// Best for residential addresses like "52A David Road" - more precise than Autocomplete
// STRATEGY: Strip house number, search for STREET only, then verify house number in result
async function googleLooseLocalSearch(
  query: string,
  lat: number,
  lon: number,
  apiKey: string,
  returnMultiple: boolean = false,
  city?: string
): Promise<GeocodeResult> {
  try {
    // Extract house number if present (e.g., "52A" from "52A David Road")
    const houseNumberMatch = query.match(/^(\d+[a-z]?)\s+(.+)$/i);
    const queryHouseNumber = houseNumberMatch?.[1]?.toLowerCase();
    const streetOnly = houseNumberMatch?.[2] || query; // Search without house number
    
    const queryRoadType = extractRoadType(query);
    const queryStreetName = extractStreetName(streetOnly);
    
    console.log(`[Geocode] Loose local: query="${query}", street="${streetOnly}", streetName="${queryStreetName}", house="${queryHouseNumber}", roadType="${queryRoadType}", city="${city}"`);
    
    // If we have a city, include it in the search to help Google find the right location
    // e.g., "David Road, Coventry" instead of just "David Road"
    const streetSearchQuery = city ? `${streetOnly}, ${city}` : streetOnly;
    
    // FIRST: Search for the STREET only (without house number) - prevents fuzzy mismatches
    const streetSearchUrl = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(streetSearchQuery)}` +
      `&location=${lat},${lon}` +
      `&radius=15000` +  // 15km radius when city specified (city name helps constrain)
      `&key=${apiKey}`;

    console.log(`[Geocode] Loose local search (street only): "${streetOnly}" near ${lat},${lon} (8km radius)`);

    const streetResponse = await fetch(streetSearchUrl);
    const streetData = await streetResponse.json();

    if (streetData.status !== "OK" || !streetData.results || streetData.results.length === 0) {
      console.log(`[Geocode] Loose local search: no results for street "${streetOnly}" (status: ${streetData.status})`);
      return { found: false, address: query };
    }

    // Find a valid match with road type AND street name validation
    let validResult = null;
    
    for (const result of streetData.results) {
      const resultAddress = result.formatted_address || result.name || "";
      const resultRoadType = extractRoadType(resultAddress);
      const resultStreetName = extractStreetName(resultAddress);
      
      // Check road type match (CRITICAL: Road ≠ Street)
      if (queryRoadType && resultRoadType && !roadTypesMatch(queryRoadType, resultRoadType)) {
        console.log(`[Geocode] Loose local REJECTED: "${resultAddress}" - road type mismatch (query: ${queryRoadType}, result: ${resultRoadType})`);
        continue;
      }
      
      // Check street name match (CRITICAL: "David" ≠ "Davids")
      if (queryStreetName && resultStreetName && !streetNamesMatch(queryStreetName, resultStreetName)) {
        console.log(`[Geocode] Loose local REJECTED: "${resultAddress}" - street name mismatch (query: "${queryStreetName}", result: "${resultStreetName}")`);
        continue;
      }
      
      // This result has the correct road type AND street name!
      validResult = result;
      console.log(`[Geocode] Loose local ACCEPTED: "${resultAddress}" (street: "${resultStreetName}", roadType: ${resultRoadType})`);
      break;
    }

    if (!validResult) {
      console.log(`[Geocode] Loose local search: no valid matches after street name + road type validation`);
      return { found: false, address: query };
    }

    // NOW: If we had a house number, search for the FULL address using the street's location as bias
    if (queryHouseNumber && validResult.geometry?.location) {
      const streetLat = validResult.geometry.location.lat;
      const streetLon = validResult.geometry.location.lng;
      
      // Search for full address (with house number) biased to the correct street's location
      const fullSearchUrl = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
        `?query=${encodeURIComponent(query)}` +
        `&location=${streetLat},${streetLon}` +
        `&radius=1000` +  // Very tight 1km radius around the correct street
        `&key=${apiKey}`;
      
      console.log(`[Geocode] Loose local: now searching full address "${query}" near street location ${streetLat},${streetLon}`);
      
      const fullResponse = await fetch(fullSearchUrl);
      const fullData = await fullResponse.json();
      
      if (fullData.status === "OK" && fullData.results && fullData.results.length > 0) {
        for (const result of fullData.results) {
          const resultAddress = (result.formatted_address || "").toLowerCase();
          const resultRoadType = extractRoadType(resultAddress);
          
          // Verify road type still matches
          if (queryRoadType && resultRoadType && !roadTypesMatch(queryRoadType, resultRoadType)) {
            continue;
          }
          
          // Verify house number is in the result
          if (resultAddress.includes(queryHouseNumber)) {
            console.log(`[Geocode] Loose local FULL MATCH: "${result.formatted_address}" (house: ${queryHouseNumber})`);
            validResult = result;
            break;
          }
        }
      }
    }

    // Handle multiple matches for disambiguation
    if (returnMultiple && streetData.results.length > 1) {
      const uniqueAreas = new Set<string>();
      const matches: GeocodeMatch[] = [];
      
      for (const result of streetData.results.slice(0, 5)) {
        const resultAddress = result.formatted_address || "";
        const resultRoadType = extractRoadType(resultAddress);
        const resultStreetName = extractStreetName(resultAddress);
        
        // Only include matches with correct road type AND street name
        if (queryRoadType && resultRoadType && !roadTypesMatch(queryRoadType, resultRoadType)) {
          continue;
        }
        if (queryStreetName && resultStreetName && !streetNamesMatch(queryStreetName, resultStreetName)) {
          continue;
        }
        
        const areaKey = resultAddress.split(',').slice(-2).join(',') || result.place_id;
        if (!uniqueAreas.has(areaKey)) {
          uniqueAreas.add(areaKey);
          matches.push({
            display_name: result.name || resultAddress,
            formatted_address: resultAddress,
            lat: result.geometry?.location?.lat,
            lon: result.geometry?.location?.lng,
            place_id: result.place_id,
          });
        }
      }
      
      if (matches.length > 1) {
        console.log(`[Geocode] Loose local: multiple matches found (${matches.length})`);
        const bestMatch = await getPlaceDetails(validResult.place_id, query, apiKey);
        bestMatch.multiple_matches = matches;
        return bestMatch;
      }
    }

    const placeId = validResult.place_id;
    if (!placeId) {
      return { found: false, address: query };
    }

    console.log(`[Geocode] Loose local match: "${validResult.formatted_address || validResult.name}"`);
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Loose local search error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Text Search (Local, 5km) - matches C# ResolveTextSearch pattern
// Includes city in query for better accuracy
async function googleTextSearchLocal(
  query: string,
  lat: number,
  lon: number,
  apiKey: string,
  city?: string
): Promise<GeocodeResult> {
  try {
    // Include city in search query if available (like your C# code)
    const searchQuery = city ? `${query}, ${city}` : query;
    
    const url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(searchQuery)}` +
      `&location=${lat},${lon}` +
      `&radius=5000` +  // 5km radius (matching C# ResolveTextSearch)
      `&key=${apiKey}`;

    console.log(`[Geocode] Text search (local 5km): "${searchQuery}" near ${lat},${lon}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] Text search local: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    const best = data.results[0];
    const placeId = best.place_id;
    
    if (!placeId) {
      return { found: false, address: query };
    }

    // Get full details for complete address info
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Text search local error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Text Search with location bias - returns multiple matches if ambiguous
async function googleTextSearch(
  query: string,
  lat: number | undefined,
  lon: number | undefined,
  apiKey: string,
  returnMultiple: boolean = false,
  city?: string
): Promise<GeocodeResult> {
  try {
    // Include city in query for better results (like C# pattern)
    const searchQuery = city ? `${query}, ${city}` : query;
    
    let url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(searchQuery)}` +
      `&key=${apiKey}`;

    // Add location bias if we have coordinates
    if (lat && lon) {
      url += `&location=${lat},${lon}&radius=10000`; // 10km radius with city bias
    }

    console.log(`[Geocode] Text search: "${searchQuery}"${lat ? ` biased to ${lat},${lon}` : ''}${city ? ` (city: ${city})` : ''}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] Text search: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    // Check for multiple similar matches (e.g., "David Road" in different areas)
    if (returnMultiple && data.results.length > 1) {
      // Check if results are in different areas (different cities/postcodes)
      const uniqueAreas = new Set<string>();
      const matches: GeocodeMatch[] = [];
      
      for (const result of data.results.slice(0, 5)) { // Max 5 matches
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
      
      // If we have multiple matches in different areas, return them for disambiguation
      if (matches.length > 1) {
        console.log(`[Geocode] Multiple matches found: ${matches.length} results in different areas`);
        // Still return the best match, but include alternatives
        const bestMatch = await getPlaceDetails(data.results[0].place_id, query, apiKey);
        bestMatch.multiple_matches = matches;
        return bestMatch;
      }
    }

    const placeId = data.results[0].place_id;
    if (!placeId) {
      return { found: false, address: query };
    }

    // Get full place details for address components
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Text search error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Get detailed place information including address components
async function getPlaceDetails(
  placeId: string,
  originalQuery: string,
  apiKey: string
): Promise<GeocodeResult> {
  try {
    const url = `https://maps.googleapis.com/maps/api/place/details/json` +
      `?place_id=${placeId}` +
      `&fields=name,formatted_address,geometry,address_components` +
      `&key=${apiKey}`;

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.result) {
      return { found: false, address: originalQuery };
    }

    const result = data.result;
    const components = result.address_components || [];

    // Extract address components
    const getComponent = (type: string): string | undefined => {
      const comp = components.find((c: any) => c.types?.includes(type));
      return comp?.long_name;
    };

    const houseNumber = getComponent("street_number");
    const streetName = getComponent("route");
    const postcode = getComponent("postal_code");
    const city = getComponent("locality") || 
                 getComponent("postal_town") || 
                 getComponent("administrative_area_level_2") ||
                 getComponent("administrative_area_level_1");

    const loc = result.geometry?.location;
    if (!loc) {
      return { found: false, address: originalQuery };
    }

    const latitude = loc.lat;
    const longitude = loc.lng;

    // Build display name: prefer place name, fall back to street address
    let displayName = result.name || result.formatted_address;
    
    // If we have a house number and street, check if it's already in the name
    if (houseNumber && streetName) {
      const streetAddress = `${houseNumber} ${streetName}`;
      // Only add address if name doesn't already contain it (avoid "7 Russell St 7 Russell Street")
      const nameContainsAddress = displayName && (
        displayName.toLowerCase().includes(streetName.toLowerCase()) ||
        displayName.match(new RegExp(`^${houseNumber}\\s`, 'i'))
      );
      
      if (!nameContainsAddress) {
        displayName = `${displayName || ''} ${streetAddress}`.trim();
      } else if (!displayName || displayName === result.formatted_address) {
        // If we only have formatted_address, use clean street address
        displayName = streetAddress;
      }
    }

    console.log(`[Geocode] ✅ Found: "${displayName}" (${city}, ${postcode}) at ${latitude},${longitude}`);

    return {
      found: true,
      address: originalQuery,
      display_name: displayName,
      formatted_address: result.formatted_address,
      lat: latitude,
      lon: longitude,
      type: "place",
      place_id: placeId,
      city: city,
      postcode: postcode,
      map_link: `https://maps.google.com/?q=${latitude},${longitude}`,
    };

  } catch (error) {
    console.error("[Geocode] Place details error:", error);
    return { found: false, address: originalQuery, error: String(error) };
  }
}

async function googleGeocode(
  query: string,
  city: string | undefined,
  country: string, 
  apiKey: string
): Promise<GeocodeResult> {
  try {
    const searchQuery = city ? `${query}, ${city}, ${country}` : `${query}, ${country}`;
    
    const params = new URLSearchParams({
      address: searchQuery,
      key: apiKey,
      components: `country:${country === "UK" ? "GB" : country}`,
    });

    const url = `https://maps.googleapis.com/maps/api/geocode/json?${params}`;
    console.log(`[Geocode] Geocoding API: "${searchQuery}"`);

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
      
      // Validate road type match (Road ≠ Street)
      const queryRoadType = extractRoadType(query);
      const resultRoadType = extractRoadType(result.formatted_address || "");
      
      if (queryRoadType && resultRoadType && !roadTypesMatch(queryRoadType, resultRoadType)) {
        console.log(`[Geocode] Geocode REJECTED: road type mismatch - query "${queryRoadType}" vs result "${resultRoadType}" ("${result.formatted_address}")`);
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
