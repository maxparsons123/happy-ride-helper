import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const GOOGLE_API_KEY = Deno.env.get("GOOGLE_MAPS_API_KEY");

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// ============================================================================
// TYPES
// ============================================================================

interface ResolvedLocation {
  raw_input: string;
  formatted_address: string;
  lat: number;
  lng: number;
  city?: string;
  postcode?: string;
  country?: string;
}

interface DistanceInfo {
  meters: number;
  miles: number;
  duration_seconds: number;
  duration_text: string;
}

interface FareEstimate {
  amount: number;
  currency: string;
  breakdown: {
    base: number;
    per_mile_rate: number;
    distance_miles: number;
    passenger_surcharge: number;
  };
}

interface InferredArea {
  city?: string;
  confidence: "low" | "medium" | "high";
  reason: string;
}

interface NearbyResult {
  name: string;
  address: string;
  lat: number;
  lng: number;
  distance_meters?: number;
  rating?: number;
  open_now?: boolean;
  types?: string[];
}

interface TripResolveRequest {
  // Trip resolution
  pickup_input?: string;
  dropoff_input?: string;
  caller_city_hint?: string;
  caller_coords?: { lat: number; lng: number };
  country?: string;
  passengers?: number;
  
  // NEW: Flag to indicate if this is a pickup (strict local) vs dropoff (can be global)
  is_pickup?: boolean;
  
  // Nearby lookup mode
  nearby_query?: string;
}

interface TripResolveResponse {
  ok: boolean;
  error?: string;
  
  // Trip resolution results
  pickup?: ResolvedLocation;
  dropoff?: ResolvedLocation;
  inferred_area?: InferredArea;
  distance?: DistanceInfo;
  fare_estimate?: FareEstimate;
  
  // Nearby lookup results
  nearby_results?: NearbyResult[];
  nearby_category?: string;
  suggested_taxi_intent?: "offer_book" | "ask_destination" | "none";
}

// ============================================================================
// UK CITIES & CATEGORIES
// ============================================================================

const UK_CITIES: Record<string, { lat: number; lng: number; aliases: string[] }> = {
  "coventry": { lat: 52.4068, lng: -1.5197, aliases: ["cov"] },
  "birmingham": { lat: 52.4862, lng: -1.8904, aliases: ["brum", "bham"] },
  "solihull": { lat: 52.4130, lng: -1.7780, aliases: [] },
  "warwick": { lat: 52.2820, lng: -1.5849, aliases: [] },
  "leamington": { lat: 52.2852, lng: -1.5200, aliases: ["leamington spa", "royal leamington spa"] },
  "kenilworth": { lat: 52.3420, lng: -1.5660, aliases: [] },
  "nuneaton": { lat: 52.5230, lng: -1.4683, aliases: [] },
  "rugby": { lat: 52.3708, lng: -1.2615, aliases: [] },
  "stratford": { lat: 52.1917, lng: -1.7083, aliases: ["stratford-upon-avon", "stratford upon avon"] },
  "wolverhampton": { lat: 52.5870, lng: -2.1288, aliases: ["wolves"] },
  "dudley": { lat: 52.5086, lng: -2.0890, aliases: [] },
  "walsall": { lat: 52.5860, lng: -1.9829, aliases: [] },
  "london": { lat: 51.5074, lng: -0.1278, aliases: [] },
  "manchester": { lat: 53.4808, lng: -2.2426, aliases: [] },
  "liverpool": { lat: 53.4084, lng: -2.9916, aliases: [] },
  "leeds": { lat: 53.8008, lng: -1.5491, aliases: [] },
  "sheffield": { lat: 53.3811, lng: -1.4701, aliases: [] },
  "bristol": { lat: 51.4545, lng: -2.5879, aliases: [] },
  "nottingham": { lat: 52.9548, lng: -1.1581, aliases: [] },
  "leicester": { lat: 52.6369, lng: -1.1398, aliases: [] },
  "cambridge": { lat: 52.2053, lng: 0.1218, aliases: [] },
  "oxford": { lat: 51.7520, lng: -1.2577, aliases: [] },
};

// Query -> Google Places type mapping
const CATEGORY_MAP: Record<string, { type: string; keywords: string[] }> = {
  "hotel": { type: "lodging", keywords: ["hotel", "inn", "b&b", "guest house", "accommodation"] },
  "hospital": { type: "hospital", keywords: ["hospital", "a&e", "emergency", "urgent care", "nhs"] },
  "pharmacy": { type: "pharmacy", keywords: ["pharmacy", "chemist", "boots", "lloyds"] },
  "train_station": { type: "train_station", keywords: ["station", "railway", "train"] },
  "airport": { type: "airport", keywords: ["airport", "terminal", "heathrow", "gatwick", "birmingham airport"] },
  "bus_station": { type: "bus_station", keywords: ["bus station", "coach station", "national express"] },
  "gas_station": { type: "gas_station", keywords: ["petrol", "fuel", "gas station", "shell", "bp", "esso"] },
  "supermarket": { type: "supermarket", keywords: ["supermarket", "tesco", "sainsbury", "asda", "morrisons", "aldi", "lidl", "waitrose"] },
  "restaurant": { type: "restaurant", keywords: ["restaurant", "food", "eat", "dining", "nandos", "mcdonalds", "kfc", "indian", "chinese", "italian"] },
  "taxi_stand": { type: "taxi_stand", keywords: ["taxi rank", "cab rank", "taxi stand"] },
  "atm": { type: "atm", keywords: ["atm", "cash machine", "cash point", "bank"] },
  "parking": { type: "parking", keywords: ["parking", "car park", "ncp"] },
  "shopping": { type: "shopping_mall", keywords: ["shopping", "mall", "centre", "retail park"] },
};

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

// Simple in-memory cache (per instance)
const geocodeCache = new Map<string, ResolvedLocation>();

/**
 * Extract city from text using known UK cities
 */
function extractCityFromText(text: string): string | undefined {
  const lower = text.toLowerCase();
  
  for (const [city, info] of Object.entries(UK_CITIES)) {
    if (lower.includes(city)) return city;
    for (const alias of info.aliases) {
      if (lower.includes(alias)) return city;
    }
  }
  return undefined;
}

/**
 * Get coordinates for a known city
 */
function getCityCoords(city: string): { lat: number; lng: number } | undefined {
  const lower = city.toLowerCase();
  if (UK_CITIES[lower]) {
    return { lat: UK_CITIES[lower].lat, lng: UK_CITIES[lower].lng };
  }
  // Check aliases
  for (const [name, info] of Object.entries(UK_CITIES)) {
    if (info.aliases.includes(lower)) {
      return { lat: info.lat, lng: info.lng };
    }
  }
  return undefined;
}

/**
 * Extract city from Google address components
 */
function extractCityFromComponents(components: any[]): string | undefined {
  if (!components) return undefined;
  
  const get = (type: string) =>
    components.find((c: any) => c.types.includes(type))?.long_name;
  
  return get("locality") || get("postal_town") || get("administrative_area_level_2");
}

/**
 * Extract postcode from Google address components
 */
function extractPostcodeFromComponents(components: any[]): string | undefined {
  if (!components) return undefined;
  return components.find((c: any) => c.types.includes("postal_code"))?.long_name;
}

/**
 * Infer category from natural language query
 */
function inferCategoryFromQuery(query: string): { type: string; keyword?: string } | undefined {
  const lower = query.toLowerCase();
  
  for (const [category, info] of Object.entries(CATEGORY_MAP)) {
    for (const kw of info.keywords) {
      if (lower.includes(kw)) {
        return { type: info.type, keyword: kw };
      }
    }
  }
  return undefined;
}

/**
 * MAIN RESOLVE FUNCTION - Following the proven C# pattern:
 * 1. House address (starts with number) → Autocomplete with types=address
 * 2. Named place → Text Search with local bias  
 * 3. Strict pickup → Must be local (reject if not found nearby)
 * 4. Dropoff → Can search globally
 */
async function resolveLocation(
  query: string,
  lat: number,
  lng: number,
  cityHint: string,
  strictPickup: boolean,
  country: string = "GB"
): Promise<ResolvedLocation | null> {
  console.log(`[Resolve] START: '${query}', strict=${strictPickup}, city=${cityHint}`);
  
  query = query.trim();
  if (!query || !GOOGLE_API_KEY) return null;
  
  // Check cache
  const cacheKey = `${query.toLowerCase()}|${cityHint}|${strictPickup}`;
  if (geocodeCache.has(cacheKey)) {
    console.log(`📍 Cache hit for: ${query}`);
    return geocodeCache.get(cacheKey)!;
  }
  
  const startsWithNumber = /^\d+/.test(query);
  
  // 1️⃣ HOUSE ADDRESS (best branch) - uses Autocomplete API
  if (startsWithNumber) {
    const r = await resolveHouseAddress(query, lat, lng, country);
    if (r) {
      console.log(`✅ HOUSE_OK: ${r.formatted_address}`);
      geocodeCache.set(cacheKey, r);
      return r;
    }
  }
  
  // 2️⃣ TEXT SEARCH (for named places like "Sweetspot", "Tesco", etc.)
  const textPlace = await resolveTextSearch(query, lat, lng);
  if (textPlace) {
    console.log(`✅ TEXT_OK: ${textPlace.formatted_address}`);
    geocodeCache.set(cacheKey, textPlace);
    return textPlace;
  }
  
  // 3️⃣ STRICT PICKUP → MUST be local (8km radius)
  if (strictPickup) {
    const local = await resolveLooseLocal(query, lat, lng);
    if (local) {
      console.log(`✅ LOCAL_PICKUP_OK: ${local.formatted_address}`);
      geocodeCache.set(cacheKey, local);
      return local;
    }
    // Strict pickup failed - don't fall through to global
    console.warn(`❌ Pickup '${query}' not found locally`);
    return null;
  }
  
  // 4️⃣ DROPOFF → CAN search globally (for airports, destinations outside city)
  const globalRes = await resolveGlobalTextSearch(query, country);
  if (globalRes) {
    console.log(`✅ GLOBAL_OK: ${globalRes.formatted_address}`);
    geocodeCache.set(cacheKey, globalRes);
    return globalRes;
  }
  
  // ❌ Nothing found
  console.warn(`❌ NO_RESULTS for: ${query}`);
  return null;
}

/**
 * HOUSE ADDRESS - Best for "52A David Road" type addresses
 * Uses Place Autocomplete API with types=address
 */
async function resolveHouseAddress(
  query: string,
  lat: number,
  lng: number,
  country: string = "GB"
): Promise<ResolvedLocation | null> {
  console.log(`[HouseAddress] Autocomplete: '${query}'`);
  
  const params = new URLSearchParams({
    input: query,
    types: "address",
    location: `${lat},${lng}`,
    radius: "5000", // 5km - local only
    components: `country:${country.toLowerCase()}`,
    key: GOOGLE_API_KEY!,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/autocomplete/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.predictions?.length) return null;
    
    const placeId = data.predictions[0].place_id;
    return await upgradeWithPlaceDetails(placeId, query);
  } catch (e) {
    console.error("[HouseAddress] Error:", e);
    return null;
  }
}

/**
 * TEXT SEARCH - Good for named places with local bias
 * Uses Text Search API with location bias (5km radius)
 */
async function resolveTextSearch(
  query: string,
  lat: number,
  lng: number
): Promise<ResolvedLocation | null> {
  console.log(`[TextSearch] '${query}'`);
  
  const params = new URLSearchParams({
    query: query,
    location: `${lat},${lng}`,
    radius: "5000", // 5km local bias
    key: GOOGLE_API_KEY!,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return null;
    
    const best = data.results[0];
    const loc = best.geometry?.location;
    if (!loc) return null;
    
    // Upgrade with Place Details for full address components
    if (best.place_id) {
      const detailed = await upgradeWithPlaceDetails(best.place_id, query);
      if (detailed) return detailed;
    }
    
    return {
      raw_input: query,
      formatted_address: best.formatted_address,
      lat: loc.lat,
      lng: loc.lng,
    };
  } catch (e) {
    console.error("[TextSearch] Error:", e);
    return null;
  }
}

/**
 * LOOSE LOCAL SEARCH - For strict pickups (8km radius)
 */
async function resolveLooseLocal(
  query: string,
  lat: number,
  lng: number
): Promise<ResolvedLocation | null> {
  console.log(`[LooseLocal] '${query}'`);
  
  const params = new URLSearchParams({
    query: query,
    location: `${lat},${lng}`,
    radius: "8000", // 8km for pickups
    key: GOOGLE_API_KEY!,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return null;
    
    const best = data.results[0];
    const loc = best.geometry?.location;
    if (!loc) return null;
    
    // Verify it's actually within range (8km)
    const distanceMeters = haversineDistance(lat, lng, loc.lat, loc.lng);
    if (distanceMeters > 10000) { // 10km max for pickups
      console.warn(`[LooseLocal] Result too far: ${distanceMeters}m`);
      return null;
    }
    
    if (best.place_id) {
      const detailed = await upgradeWithPlaceDetails(best.place_id, query);
      if (detailed) return detailed;
    }
    
    return {
      raw_input: query,
      formatted_address: best.formatted_address,
      lat: loc.lat,
      lng: loc.lng,
    };
  } catch (e) {
    console.error("[LooseLocal] Error:", e);
    return null;
  }
}

/**
 * GLOBAL SEARCH - For dropoffs that can be anywhere (airports, etc.)
 * Uses Text Search without location constraint, but biased to UK
 */
async function resolveGlobalTextSearch(
  query: string,
  country: string = "GB"
): Promise<ResolvedLocation | null> {
  console.log(`[GlobalSearch] '${query}'`);
  
  // Add UK suffix for better results
  const searchQuery = query.toLowerCase().includes("uk") ? query : `${query}, UK`;
  
  const params = new URLSearchParams({
    query: searchQuery,
    key: GOOGLE_API_KEY!,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return null;
    
    const best = data.results[0];
    const loc = best.geometry?.location;
    if (!loc) return null;
    
    // Upgrade with Place Details
    if (best.place_id) {
      const detailed = await upgradeWithPlaceDetails(best.place_id, query);
      if (detailed) {
        // Verify it's in UK
        if (detailed.country && !["GB", "UK"].includes(detailed.country.toUpperCase())) {
          console.warn(`[GlobalSearch] Non-UK result rejected: ${detailed.formatted_address}`);
          return null;
        }
        return detailed;
      }
    }
    
    return {
      raw_input: query,
      formatted_address: best.formatted_address,
      lat: loc.lat,
      lng: loc.lng,
    };
  } catch (e) {
    console.error("[GlobalSearch] Error:", e);
    return null;
  }
}

/**
 * UPGRADE WITH PLACE DETAILS - Get full address components from place_id
 */
async function upgradeWithPlaceDetails(
  placeId: string,
  rawInput: string
): Promise<ResolvedLocation | null> {
  if (!placeId) return null;
  
  const params = new URLSearchParams({
    place_id: placeId,
    fields: "name,formatted_address,geometry,address_components",
    key: GOOGLE_API_KEY!,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/details/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.result) return null;
    
    const result = data.result;
    const loc = result.geometry?.location;
    if (!loc) return null;
    
    const components = result.address_components || [];
    
    return {
      raw_input: rawInput,
      formatted_address: result.formatted_address,
      lat: loc.lat,
      lng: loc.lng,
      city: extractCityFromComponents(components),
      postcode: extractPostcodeFromComponents(components),
      country: components.find((c: any) => c.types.includes("country"))?.short_name,
    };
  } catch (e) {
    console.error("[UpgradeDetails] Error:", e);
    return null;
  }
}

/**
 * Legacy geocodeAddress wrapper - calls resolveLocation
 */
async function geocodeAddress(
  rawInput: string,
  cityHint?: string,
  coordsHint?: { lat: number; lng: number },
  country: string = "GB",
  strictPickup: boolean = false
): Promise<ResolvedLocation | null> {
  const coords = coordsHint || getCityCoords(cityHint || "") || { lat: 52.4862, lng: -1.8904 }; // Default to Birmingham
  return resolveLocation(rawInput, coords.lat, coords.lng, cityHint || "", strictPickup, country);
}

/**
 * Google Nearby Search for places
 */
async function googleNearbySearch(
  lat: number,
  lng: number,
  type: string,
  keyword?: string,
  radius: number = 5000
): Promise<NearbyResult[]> {
  if (!GOOGLE_API_KEY) return [];
  
  const params = new URLSearchParams({
    location: `${lat},${lng}`,
    radius: radius.toString(),
    type: type,
    key: GOOGLE_API_KEY,
    rankby: "prominence",
  });
  
  if (keyword) {
    params.append("keyword", keyword);
  }
  
  const url = `https://maps.googleapis.com/maps/api/place/nearbysearch/json?${params}`;
  console.log(`📍 Nearby Search: type=${type}, keyword=${keyword}`);
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return [];
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return [];
    
    return data.results.slice(0, 5).map((place: any) => {
      const placeLoc = place.geometry?.location;
      const distMeters = placeLoc ? haversineDistance(lat, lng, placeLoc.lat, placeLoc.lng) : undefined;
      
      return {
        name: place.name,
        address: place.vicinity || place.formatted_address,
        lat: placeLoc?.lat,
        lng: placeLoc?.lng,
        distance_meters: distMeters ? Math.round(distMeters) : undefined,
        rating: place.rating,
        open_now: place.opening_hours?.open_now,
        types: place.types,
      };
    });
  } catch (e) {
    console.error("Nearby Search error:", e);
    return [];
  }
}

/**
 * Calculate distance using Google Distance Matrix API
 */
async function calculateDistance(
  origin: ResolvedLocation,
  destination: ResolvedLocation
): Promise<DistanceInfo | null> {
  if (!GOOGLE_API_KEY) return null;
  
  const params = new URLSearchParams({
    origins: `${origin.lat},${origin.lng}`,
    destinations: `${destination.lat},${destination.lng}`,
    key: GOOGLE_API_KEY,
    units: "imperial",
    mode: "driving",
  });
  
  const url = `https://maps.googleapis.com/maps/api/distancematrix/json?${params}`;
  console.log(`📏 Distance Matrix: ${origin.formatted_address} → ${destination.formatted_address}`);
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.rows?.[0]?.elements?.[0]) return null;
    
    const element = data.rows[0].elements[0];
    if (element.status !== "OK") return null;
    
    const meters = element.distance.value;
    const miles = meters / 1609.34;
    
    return {
      meters,
      miles: parseFloat(miles.toFixed(2)),
      duration_seconds: element.duration.value,
      duration_text: element.duration.text,
    };
  } catch (e) {
    console.error("Distance Matrix error:", e);
    return null;
  }
}

/**
 * Haversine distance calculation (straight line)
 */
function haversineDistance(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const R = 6371000; // Earth's radius in meters
  const dLat = (lat2 - lat1) * Math.PI / 180;
  const dLon = (lon2 - lon1) * Math.PI / 180;
  const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
            Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
            Math.sin(dLon / 2) * Math.sin(dLon / 2);
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return R * c;
}

/**
 * Calculate fare estimate (UK taxi pricing)
 */
function calculateFare(distanceMiles: number, passengers: number = 1): FareEstimate {
  const BASE = 3.50;
  const PER_MILE = 1.80;
  const PASSENGER_SURCHARGE = passengers > 4 ? 5.00 : 0;
  
  const raw = BASE + (distanceMiles * PER_MILE) + PASSENGER_SURCHARGE;
  const amount = Math.round(raw * 2) / 2; // Round to nearest 50p
  
  return {
    amount,
    currency: "GBP",
    breakdown: {
      base: BASE,
      per_mile_rate: PER_MILE,
      distance_miles: distanceMiles,
      passenger_surcharge: PASSENGER_SURCHARGE,
    },
  };
}

/**
 * Infer caller's area from available context
 */
function inferArea(
  pickup?: ResolvedLocation,
  dropoff?: ResolvedLocation,
  cityHint?: string,
  coordsHint?: { lat: number; lng: number }
): InferredArea {
  const pickupCity = pickup?.city?.toLowerCase();
  const dropoffCity = dropoff?.city?.toLowerCase();
  const hint = cityHint?.toLowerCase();
  
  // High confidence: pickup and dropoff in same city
  if (pickupCity && dropoffCity && pickupCity === dropoffCity) {
    return {
      city: pickup!.city,
      confidence: "high",
      reason: "pickup and dropoff share the same city",
    };
  }
  
  // High confidence: city hint matches one of the locations
  if (hint && (hint === pickupCity || hint === dropoffCity)) {
    return {
      city: cityHint,
      confidence: "high",
      reason: `caller_city_hint matches ${hint === pickupCity ? "pickup" : "dropoff"}`,
    };
  }
  
  // Medium confidence: only one location has city
  if (pickupCity && !dropoffCity) {
    return {
      city: pickup!.city,
      confidence: "medium",
      reason: "only pickup city is known",
    };
  }
  
  if (dropoffCity && !pickupCity) {
    return {
      city: dropoff!.city,
      confidence: "medium",
      reason: "only dropoff city is known",
    };
  }
  
  // Low confidence: use hint or coords
  if (hint) {
    return {
      city: cityHint,
      confidence: "low",
      reason: "only caller_city_hint is available",
    };
  }
  
  if (coordsHint) {
    // Find nearest known city
    let nearestCity = "";
    let nearestDist = Infinity;
    
    for (const [city, info] of Object.entries(UK_CITIES)) {
      const dist = haversineDistance(coordsHint.lat, coordsHint.lng, info.lat, info.lng);
      if (dist < nearestDist) {
        nearestDist = dist;
        nearestCity = city;
      }
    }
    
    if (nearestCity && nearestDist < 30000) { // Within 30km
      return {
        city: nearestCity.charAt(0).toUpperCase() + nearestCity.slice(1),
        confidence: "low",
        reason: `nearest known city to caller coordinates`,
      };
    }
  }
  
  return {
    confidence: "low",
    reason: "no city could be inferred",
  };
}

// ============================================================================
// MAIN HANDLER
// ============================================================================

serve(async (req) => {
  // CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  if (req.method !== "POST") {
    return new Response(
      JSON.stringify({ ok: false, error: "Use POST with JSON body" }),
      { status: 405, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
  
  let body: TripResolveRequest;
  try {
    body = await req.json();
  } catch {
    return new Response(
      JSON.stringify({ ok: false, error: "Invalid JSON" }),
      { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
  
  const {
    pickup_input,
    dropoff_input,
    caller_city_hint,
    caller_coords,
    country = "GB",
    passengers = 1,
    nearby_query,
  } = body;
  
  console.log(`🚕 Trip Resolve Request:`, JSON.stringify(body, null, 2));
  
  try {
    const response: TripResolveResponse = { ok: true };
    
    // Determine city context
    let cityContext = caller_city_hint;
    let coordsContext = caller_coords;
    
    // If nearby_query, do nearby lookup mode
    if (nearby_query) {
      const category = inferCategoryFromQuery(nearby_query);
      response.nearby_category = category?.type;
      
      // Need coordinates for nearby search
      if (!coordsContext && cityContext) {
        coordsContext = getCityCoords(cityContext);
      }
      
      // Try to extract city from the query itself
      if (!coordsContext) {
        const queryCity = extractCityFromText(nearby_query);
        if (queryCity) {
          coordsContext = getCityCoords(queryCity);
          cityContext = queryCity;
        }
      }
      
      if (coordsContext && category) {
        const results = await googleNearbySearch(
          coordsContext.lat,
          coordsContext.lng,
          category.type,
          category.keyword
        );
        response.nearby_results = results;
        response.suggested_taxi_intent = results.length > 0 ? "offer_book" : "none";
      } else {
        // Fallback: try text search
        if (cityContext) {
          const searchQuery = `${nearby_query} in ${cityContext}`;
          const result = await geocodeAddress(searchQuery, cityContext, coordsContext, country);
          if (result) {
            response.nearby_results = [{
              name: result.formatted_address,
              address: result.formatted_address,
              lat: result.lat,
              lng: result.lng,
            }];
            response.suggested_taxi_intent = "ask_destination";
          }
        }
      }
      
      response.inferred_area = inferArea(undefined, undefined, cityContext, coordsContext);
      
      return new Response(JSON.stringify(response), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
    
    // Trip resolution mode
    if (!pickup_input && !dropoff_input) {
      return new Response(
        JSON.stringify({ ok: false, error: "Need pickup_input, dropoff_input, or nearby_query" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }
    
    // Extract city hints from inputs
    if (!cityContext) {
      cityContext = extractCityFromText(pickup_input || "") || 
                    extractCityFromText(dropoff_input || "");
    }
    
    // Get coords from city context if available
    if (!coordsContext && cityContext) {
      coordsContext = getCityCoords(cityContext);
    }
    
    // Geocode both addresses in parallel
    // PICKUP = strictPickup (must be local)
    // DROPOFF = not strict (can be global - airports, destinations outside city)
    const [pickup, dropoff] = await Promise.all([
      pickup_input ? geocodeAddress(pickup_input, cityContext, coordsContext, country, true) : null,  // strictPickup = true
      dropoff_input ? geocodeAddress(dropoff_input, cityContext, coordsContext, country, false) : null, // strictPickup = false (can be global)
    ]);
    
    if (pickup) response.pickup = pickup;
    if (dropoff) response.dropoff = dropoff;
    
    // Update city context from resolved addresses
    if (!cityContext) {
      cityContext = pickup?.city || dropoff?.city;
    }
    
    // Infer area
    response.inferred_area = inferArea(
      pickup || undefined,
      dropoff || undefined,
      cityContext,
      coordsContext
    );
    
    // Validate addresses are in UK (reject international bookings)
    const MAX_TRIP_DISTANCE_MILES = 200; // Maximum reasonable taxi distance
    const UK_COUNTRY_CODES = ["GB", "UK"];
    
    const isUkAddress = (loc: ResolvedLocation | null): boolean => {
      if (!loc) return false;
      // Check country code
      if (loc.country && UK_COUNTRY_CODES.includes(loc.country.toUpperCase())) return true;
      // Check for UK postcode pattern
      if (loc.postcode && /^[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}$/i.test(loc.postcode)) return true;
      // Check for known UK cities
      if (loc.city && UK_CITIES[loc.city.toLowerCase()]) return true;
      return false;
    };
    
    // Check if addresses are in UK
    const pickupIsUk = isUkAddress(pickup);
    const dropoffIsUk = isUkAddress(dropoff);
    
    if (pickup && !pickupIsUk) {
      console.warn(`⚠️ Pickup not in UK: ${pickup.formatted_address} (country: ${pickup.country})`);
      response.error = `Pickup address "${pickup_input}" appears to be outside the UK. We only serve UK locations.`;
      response.pickup = undefined; // Clear invalid address
    }
    
    if (dropoff && !dropoffIsUk) {
      console.warn(`⚠️ Dropoff not in UK: ${dropoff.formatted_address} (country: ${dropoff.country})`);
      response.error = `Destination "${dropoff_input}" appears to be outside the UK. We only serve UK locations.`;
      response.dropoff = undefined; // Clear invalid address
    }
    
    // Calculate distance and fare if we have both valid UK points
    if (pickup && dropoff && pickupIsUk && dropoffIsUk) {
      const distance = await calculateDistance(pickup, dropoff);
      
      if (distance) {
        // Check if distance is reasonable for a taxi
        if (distance.miles > MAX_TRIP_DISTANCE_MILES) {
          console.warn(`⚠️ Trip too long: ${distance.miles} miles exceeds ${MAX_TRIP_DISTANCE_MILES} mile limit`);
          response.distance = distance;
          response.error = `This trip is ${distance.miles.toFixed(0)} miles - too far for a taxi. Maximum is ${MAX_TRIP_DISTANCE_MILES} miles.`;
          response.fare_estimate = undefined;
        } else {
          response.distance = distance;
          response.fare_estimate = calculateFare(distance.miles, passengers);
        }
      } else {
        // Fallback to Haversine distance with road multiplier
        const straightLine = haversineDistance(pickup.lat, pickup.lng, dropoff.lat, dropoff.lng);
        const estimatedMeters = straightLine * 1.3; // Road multiplier
        const estimatedMiles = estimatedMeters / 1609.34;
        
        if (estimatedMiles > MAX_TRIP_DISTANCE_MILES) {
          console.warn(`⚠️ Trip too long (haversine): ${estimatedMiles.toFixed(0)} miles`);
          response.error = `This trip is approximately ${estimatedMiles.toFixed(0)} miles - too far for a taxi.`;
        } else {
          response.distance = {
            meters: Math.round(estimatedMeters),
            miles: parseFloat(estimatedMiles.toFixed(2)),
            duration_seconds: Math.round(estimatedMiles * 120), // ~2 mins per mile estimate
            duration_text: `~${Math.round(estimatedMiles * 2)} mins`,
          };
          response.fare_estimate = calculateFare(estimatedMiles, passengers);
        }
      }
    }
    
    console.log(`✅ Trip Resolve Response:`, JSON.stringify(response, null, 2));
    
    return new Response(JSON.stringify(response), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
    
  } catch (e) {
    console.error("Trip resolve error:", e);
    return new Response(
      JSON.stringify({ ok: false, error: e instanceof Error ? e.message : "Internal error" }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
