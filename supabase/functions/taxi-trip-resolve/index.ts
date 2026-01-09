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
 * Geocode an address using Google Geocoding API with city biasing
 */
async function geocodeAddress(
  rawInput: string,
  cityHint?: string,
  coordsHint?: { lat: number; lng: number },
  country: string = "GB"
): Promise<ResolvedLocation | null> {
  const trimmed = rawInput.trim();
  if (!trimmed || !GOOGLE_API_KEY) return null;
  
  // Check cache
  const cacheKey = `${trimmed.toLowerCase()}|${cityHint || ""}`;
  if (geocodeCache.has(cacheKey)) {
    console.log(`📍 Cache hit for: ${trimmed}`);
    return geocodeCache.get(cacheKey)!;
  }
  
  // Build address with city bias if not already included
  let addressParam = trimmed;
  const textCity = extractCityFromText(trimmed);
  
  if (cityHint && !textCity && !trimmed.toLowerCase().includes(cityHint.toLowerCase())) {
    addressParam = `${trimmed}, ${cityHint}`;
  }
  
  // Try Google Places Text Search first (better for landmarks)
  const textSearchResult = await googleTextSearch(addressParam, coordsHint, country);
  if (textSearchResult) {
    geocodeCache.set(cacheKey, textSearchResult);
    return textSearchResult;
  }
  
  // Fallback to Geocoding API
  const params = new URLSearchParams({
    address: addressParam,
    key: GOOGLE_API_KEY,
    components: `country:${country}`,
  });
  
  const url = `https://maps.googleapis.com/maps/api/geocode/json?${params}`;
  console.log(`🌍 Geocoding: ${addressParam}`);
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return null;
    
    const best = data.results[0];
    const loc = best.geometry?.location;
    if (!loc) return null;
    
    const result: ResolvedLocation = {
      raw_input: rawInput,
      formatted_address: best.formatted_address,
      lat: loc.lat,
      lng: loc.lng,
      city: extractCityFromComponents(best.address_components),
      postcode: extractPostcodeFromComponents(best.address_components),
      country: best.address_components?.find((c: any) => c.types.includes("country"))?.short_name,
    };
    
    geocodeCache.set(cacheKey, result);
    return result;
  } catch (e) {
    console.error("Geocode error:", e);
    return null;
  }
}

/**
 * Google Places Text Search (better for landmarks, businesses)
 * Restricted to UK only
 */
async function googleTextSearch(
  query: string,
  coordsHint?: { lat: number; lng: number },
  country: string = "GB"
): Promise<ResolvedLocation | null> {
  if (!GOOGLE_API_KEY) return null;
  
  // Add UK suffix if not already present to bias results
  const UK_INDICATORS = ["uk", "united kingdom", "england", "scotland", "wales", "northern ireland"];
  const queryLower = query.toLowerCase();
  const hasUkIndicator = UK_INDICATORS.some(ind => queryLower.includes(ind));
  const searchQuery = hasUkIndicator ? query : `${query}, UK`;
  
  const params = new URLSearchParams({
    query: searchQuery,
    key: GOOGLE_API_KEY,
    region: country.toLowerCase(),
  });
  
  // If we have coords, use them for location bias
  if (coordsHint) {
    params.append("location", `${coordsHint.lat},${coordsHint.lng}`);
    params.append("radius", "50000"); // 50km bias for UK-sized searches
  } else {
    // Default to UK center (Birmingham area) for bias
    params.append("location", "52.4862,-1.8904");
    params.append("radius", "300000"); // 300km covers most of UK
  }
  
  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
  console.log(`🔍 Text Search: ${searchQuery}`);
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return null;
    
    // Filter results to UK only
    const ukResults = data.results.filter((r: any) => {
      const addr = (r.formatted_address || "").toLowerCase();
      return addr.includes("uk") || addr.includes("united kingdom") || 
             addr.includes("england") || addr.includes("scotland") || 
             addr.includes("wales") || addr.includes("northern ireland");
    });
    
    const best = ukResults[0] || data.results[0]; // Prefer UK, fallback to first result
    const loc = best.geometry?.location;
    if (!loc) return null;
    
    // Get detailed address components
    const detailsUrl = `https://maps.googleapis.com/maps/api/place/details/json?place_id=${best.place_id}&fields=address_components,formatted_address&key=${GOOGLE_API_KEY}`;
    const detailsResp = await fetch(detailsUrl);
    const detailsData = await detailsResp.json();
    
    const components = detailsData.result?.address_components || [];
    const countryCode = components.find((c: any) => c.types.includes("country"))?.short_name;
    
    // Verify it's actually in UK
    if (countryCode && !["GB", "UK"].includes(countryCode.toUpperCase())) {
      console.warn(`⚠️ Text Search found non-UK result: ${best.formatted_address} (${countryCode})`);
      return null; // Reject non-UK results
    }
    
    return {
      raw_input: query,
      formatted_address: best.formatted_address,
      lat: loc.lat,
      lng: loc.lng,
      city: extractCityFromComponents(components),
      postcode: extractPostcodeFromComponents(components),
      country: countryCode,
    };
  } catch (e) {
    console.error("Text Search error:", e);
    return null;
  }
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
    const [pickup, dropoff] = await Promise.all([
      pickup_input ? geocodeAddress(pickup_input, cityContext, coordsContext, country) : null,
      dropoff_input ? geocodeAddress(dropoff_input, cityContext, coordsContext, country) : null,
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
