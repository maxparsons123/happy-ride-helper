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
  name?: string;
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

// NEW: Area disambiguation for roads that exist in multiple areas
interface AreaMatch {
  road: string;
  area: string;
  city?: string;
  postcode?: string;
  lat: number;
  lng: number;
  formatted_address?: string;
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
  
  // NEW: Area disambiguation
  needs_pickup_disambiguation?: boolean;
  pickup_area_matches?: AreaMatch[];
  needs_dropoff_disambiguation?: boolean;
  dropoff_area_matches?: AreaMatch[];
  
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

// Common STT (speech-to-text) mishearings - normalize before matching
const STT_CORRECTIONS: Record<string, string> = {
  // Road type mishearings
  "rhodes": "road",
  "rode": "road",
  "rowed": "road",
  "rows": "road",
  "treat": "street",
  "treats": "street",
  "strait": "street",
  "straight": "street",
  "streat": "street",
  "stree": "street",
  "avenue you": "avenue",
  "have a new": "avenue",
  "avnew": "avenue",
  "lane's": "lane",
  "lain": "lane",
  "plains": "lane",
  "drive's": "drive",
  "drove": "drive",
  "clothes": "close",
  "closed": "close",
  "crescent's": "crescent",
  "present": "crescent",
  "pleasant": "crescent",
  "grace": "grove",
  "groves": "grove",
  "terris": "terrace",
  "terrorists": "terrace",
  "terrorist": "terrace",
  "garden": "gardens",
  "court's": "court",
  "caught": "court",
  "courts": "court",
  "placed": "place",
  "places": "place",
  // Common name mishearings
  "davie": "david",
  "davy": "david",
  "davies": "david",
  // POI/Venue name mishearings - STT often confuses venue names with place names
  "swaffham": "sweetspot",
  "swaffam": "sweetspot",
  "swap them": "sweetspot",
  "swap him": "sweetspot",
  "swapham": "sweetspot",
  "sweet ham": "sweetspot",
  "sweets bob": "sweetspot",
};

// Normalize address by fixing common STT mishearings
function normalizeSTTAddress(address: string): string {
  let normalized = address.toLowerCase();

  // Targeted fix: "David Rose" (STT) → "David Road" when it appears like a street suffix
  normalized = normalized.replace(/(\b\d+[a-z]?\s+[\w']+\s+)rose\b/gi, "$1road");

  for (const [mishearing, correction] of Object.entries(STT_CORRECTIONS)) {
    // Use word boundary matching to avoid partial replacements
    const regex = new RegExp(`\\b${mishearing}\\b`, "gi");
    normalized = normalized.replace(regex, correction);
  }
  return normalized;
}

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
// Applies STT normalization first to handle mishearings like "Rhodes" → "Road"
function extractRoadType(address: string): string | null {
  // First normalize STT mishearings
  const normalized = normalizeSTTAddress(address);
  
  for (const [canonical, variants] of Object.entries(ROAD_TYPES)) {
    for (const variant of variants) {
      // Match as a whole word (e.g., "road" but not "roadside")
      const regex = new RegExp(`\\b${variant}\\b`, 'i');
      if (regex.test(normalized)) {
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

// Validate that a geocode result matches the user's input for road type and street name
// Applies STT normalization to user input to handle mishearings like "David Rhodes" → "David Road"
function isValidAddressMatch(userInput: string, geocodedAddress: string): boolean {
  // Normalize STT mishearings in user input before comparison
  const normalizedInput = normalizeSTTAddress(userInput);
  
  const queryRoadType = extractRoadType(normalizedInput);
  const resultRoadType = extractRoadType(geocodedAddress);
  const queryStreetName = extractStreetName(normalizedInput);
  const resultStreetName = extractStreetName(geocodedAddress);
  
  console.log(`[AddressValidation] Comparing: input="${userInput}" (normalized="${normalizedInput}") vs result="${geocodedAddress}"`);
  
  // Check road type match (Road vs Street)
  if (!roadTypesMatch(queryRoadType, resultRoadType)) {
    console.warn(`[AddressValidation] Road type mismatch: query="${queryRoadType}" result="${resultRoadType}" (input="${userInput}", result="${geocodedAddress}")`);
    return false;
  }
  
  // Check street name match (David vs Davids)
  if (!streetNamesMatch(queryStreetName, resultStreetName)) {
    console.warn(`[AddressValidation] Street name mismatch: query="${queryStreetName}" result="${resultStreetName}" (input="${userInput}", result="${geocodedAddress}")`);
    return false;
  }
  
  return true;
}

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
 * Uses Text Search API with location bias (no autocomplete)
 * Validates that road type (Road vs Street) and street name match
 */
async function resolveHouseAddress(
  query: string,
  lat: number,
  lng: number,
  country: string = "GB"
): Promise<ResolvedLocation | null> {
  console.log(`[HouseAddress] TextSearch: '${query}'`);
  
  const params = new URLSearchParams({
    query: query,
    location: `${lat},${lng}`,
    radius: "5000", // 5km - local only
    key: GOOGLE_API_KEY!,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) return null;
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) return null;
    
    // Try each result until we find one that validates
    for (const result of data.results) {
      const placeId = result.place_id;
      const detailed = await upgradeWithPlaceDetails(placeId, query);
      
      if (detailed) {
        // Validate road type and street name match
        if (isValidAddressMatch(query, detailed.formatted_address)) {
          console.log(`✅ [HouseAddress] Valid match: "${query}" → "${detailed.formatted_address}"`);
          return detailed;
        } else {
          console.warn(`⚠️ [HouseAddress] Rejected mismatch: "${query}" → "${detailed.formatted_address}"`);
          // Continue to next result
        }
      }
    }
    
    // No valid matches found
    console.warn(`❌ [HouseAddress] No valid matches for: "${query}" (road type/name mismatch)`);
    return null;
  } catch (e) {
    console.error("[HouseAddress] Error:", e);
    return null;
  }
}

function extractKeyTokens(query: string): string[] {
  const stopwords = new Set([
    "the",
    "a",
    "an",
    "in",
    "at",
    "to",
    "from",
    "please",
    "for",
    "on",
    "and",
    "of",
    "uk",
    "united",
    "kingdom",
  ]);

  const cityTokens = new Set<string>();
  for (const [city, info] of Object.entries(UK_CITIES)) {
    cityTokens.add(city.toLowerCase());
    for (const a of info.aliases) cityTokens.add(a.toLowerCase());
  }

  return (query || "")
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .split(/\s+/)
    .filter(Boolean)
    .filter((t) => !stopwords.has(t))
    .filter((t) => !cityTokens.has(t))
    .filter((t) => t.length >= 4)
    .slice(0, 8);
}

function isRelevantTextSearchMatch(
  query: string,
  resultName?: string,
  formattedAddress?: string
): boolean {
  const tokens = extractKeyTokens(query);
  if (tokens.length === 0) return true;

  const hay = `${resultName || ""} ${formattedAddress || ""}`.toLowerCase();
  const hayNoSpaces = hay.replace(/\s+/g, "");

  // Allow matches where the place name has spaces but the query doesn't (e.g. "sweetspot" vs "sweet spot")
  return tokens.some((t) => hay.includes(t) || hayNoSpaces.includes(t));
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

    const bestName = best.name || "";
    const bestAddr = best.formatted_address || "";
    if (!isRelevantTextSearchMatch(query, bestName, bestAddr)) {
      console.warn(`[TextSearch] Low relevance match rejected: query="${query}" best="${bestName}" addr="${bestAddr}"`);
      return null;
    }

    // Upgrade with Place Details for full address components
    if (best.place_id) {
      const detailed = await upgradeWithPlaceDetails(best.place_id, query);
      if (detailed) return detailed;
    }

    return {
      raw_input: query,
      name: bestName || undefined,
      formatted_address: bestAddr,
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

    const bestName = best.name || "";
    const bestAddr = best.formatted_address || "";
    if (!isRelevantTextSearchMatch(query, bestName, bestAddr)) {
      console.warn(`[LooseLocal] Low relevance match rejected: query="${query}" best="${bestName}" addr="${bestAddr}"`);
      return null;
    }

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
      name: bestName || undefined,
      formatted_address: bestAddr,
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

    const bestName = best.name || "";
    const bestAddr = best.formatted_address || "";
    if (!isRelevantTextSearchMatch(query, bestName, bestAddr)) {
      console.warn(`[GlobalSearch] Low relevance match rejected: query="${query}" best="${bestName}" addr="${bestAddr}"`);
      return null;
    }

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
      name: bestName || undefined,
      formatted_address: bestAddr,
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
      name: result.name || undefined,
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
 * AREA DISAMBIGUATION - Finds if same road exists in multiple nearby areas
 * Returns multiple area matches for Ada to ask user to choose
 * 
 * Rules:
 * - If same road exists in multiple nearby areas → return all for disambiguation
 * - Never auto-pick between areas
 * - Never re-ask street name (user already said "School Road")
 */
async function checkAreaDisambiguation(
  address: string,
  biasLat: number,
  biasLng: number,
  radiusMeters: number = 15000,
  requiredCity?: string // If provided, only return matches within this city
): Promise<{ needsDisambiguation: boolean; matches: AreaMatch[]; roadName: string }> {
  if (!GOOGLE_API_KEY) {
    return { needsDisambiguation: false, matches: [], roadName: address };
  }
  
  console.log(`[AreaDisambiguation] Checking: "${address}" near ${biasLat},${biasLng}${requiredCity ? ` (filtering to ${requiredCity})` : ''}`);
  
  // Extract just the road name, stripping any city suffix
  // "School Road, Birmingham" → "School Road"
  // "High Street, Coventry" → "High Street"
  let roadNameOnly = address;
  let extractedCity: string | undefined;
  if (address.includes(",")) {
    const parts = address.split(",").map(p => p.trim());
    // First part should be the road name
    if (extractRoadType(parts[0])) {
      roadNameOnly = parts[0];
      // Second part might be the city
      if (parts[1]) {
        extractedCity = parts[1];
      }
      console.log(`[AreaDisambiguation] Extracted road name: "${roadNameOnly}" from "${address}"${extractedCity ? `, city: "${extractedCity}"` : ''}`);
    }
  }
  
  // Use extracted city if requiredCity not explicitly provided
  const cityFilter = requiredCity || extractedCity;
  
  // Normalize the road name for searching
  const normalizedRoad = normalizeSTTAddress(roadNameOnly);
  
  // Search for just the road name with location bias - this finds ALL instances of the road nearby
  // Don't include city in search to get multiple results within that city
  const params = new URLSearchParams({
    query: `${normalizedRoad}`,
    location: `${biasLat},${biasLng}`,
    radius: radiusMeters.toString(),
    key: GOOGLE_API_KEY,
  });
  
  const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
  
  try {
    const resp = await fetch(url);
    if (!resp.ok) {
      console.error("[AreaDisambiguation] API error:", resp.status);
      return { needsDisambiguation: false, matches: [], roadName: roadNameOnly };
    }
    
    const data = await resp.json();
    if (data.status !== "OK" || !data.results?.length) {
      console.log("[AreaDisambiguation] No results");
      return { needsDisambiguation: false, matches: [], roadName: roadNameOnly };
    }
    
    // Extract road/area info from each result
    const areaMatches: AreaMatch[] = [];
    
    for (const result of data.results.slice(0, 10)) {
      const placeId = result.place_id;
      if (!placeId) continue;
      
      // Get detailed address components
      const detailParams = new URLSearchParams({
        place_id: placeId,
        fields: "name,formatted_address,geometry,address_components",
        key: GOOGLE_API_KEY,
      });
      
      const detailUrl = `https://maps.googleapis.com/maps/api/place/details/json?${detailParams}`;
      
      try {
        const detailResp = await fetch(detailUrl);
        if (!detailResp.ok) continue;
        
        const detailData = await detailResp.json();
        if (detailData.status !== "OK" || !detailData.result) continue;
        
        const r = detailData.result;
        const components = r.address_components || [];
        const loc = r.geometry?.location;
        if (!loc) continue;
        
        // Extract components
        const findComponent = (type: string): string | undefined =>
          components.find((c: any) => c.types?.includes(type))?.long_name;
        
        const route = findComponent("route");
        if (!route) continue;
        
        // Get area (sublocality, neighborhood, postal_town, or locality)
        const area =
          findComponent("sublocality") ||
          findComponent("neighborhood") ||
          findComponent("postal_town") ||
          findComponent("locality") ||
          "Unknown area";
        
        // Get city - prefer administrative_area_level_2 (e.g., "Dudley") over locality
        // This ensures we get the parent city for areas like Netherton, which is IN Dudley
        const adminLevel2 = findComponent("administrative_area_level_2");
        const locality = findComponent("locality");
        
        // If locality equals area, use adminLevel2 as the city (parent area)
        // e.g., area=Netherton, locality=Netherton, adminLevel2=Dudley → city=Dudley
        const city = (locality && locality.toLowerCase() === area.toLowerCase()) 
          ? adminLevel2 || locality
          : locality || adminLevel2;
          
        const postcode = findComponent("postal_code");
        
        // Check distance from bias point
        const distanceMeters = haversineDistance(biasLat, biasLng, loc.lat, loc.lng);
        if (distanceMeters > radiusMeters * 1.5) {
          console.log(`[AreaDisambiguation] Skipping too-far result: ${r.formatted_address} (${Math.round(distanceMeters / 1000)}km away)`);
          continue;
        }
        
        areaMatches.push({
          road: route,
          area: area,
          city: city,
          postcode: postcode,
          lat: loc.lat,
          lng: loc.lng,
          formatted_address: r.formatted_address,
        });
      } catch (e) {
        console.error("[AreaDisambiguation] Detail fetch error:", e);
      }
    }
    
    // Dedupe by area (same road+area = same match)
    let uniqueMatches = dedupeAreaMatches(areaMatches);
    
    // Filter by city if specified (e.g., "School Road, Birmingham" should only show Birmingham areas)
    if (cityFilter) {
      const normalizedCityFilter = cityFilter.toLowerCase().trim();
      const beforeCount = uniqueMatches.length;
      
      // List of distinct cities that should NOT match each other
      // e.g., Solihull is a separate city from Birmingham, not an area within it
      const distinctCities = ['birmingham', 'solihull', 'coventry', 'wolverhampton', 'dudley', 'walsall', 'london', 'manchester'];
      const filterIsDistinctCity = distinctCities.includes(normalizedCityFilter);
      
      uniqueMatches = uniqueMatches.filter(m => {
        const matchCity = (m.city || '').toLowerCase();
        const matchArea = (m.area || '').toLowerCase();
        
        // If the area itself is a distinct city different from the filter, exclude it
        // e.g., if filter is "Birmingham" and area is "Solihull", exclude it
        if (filterIsDistinctCity) {
          const areaIsDistinctCity = distinctCities.includes(matchArea);
          if (areaIsDistinctCity && matchArea !== normalizedCityFilter) {
            console.log(`[AreaDisambiguation] Excluding ${m.road} in ${m.area} - different city from ${cityFilter}`);
            return false;
          }
        }
        
        // Check if city or area matches the filter
        // "Birmingham" should match "School Road in Hockley (Birmingham)" but not "School Road in Solihull"
        return matchCity === normalizedCityFilter || 
               matchArea === normalizedCityFilter ||
               matchCity.includes(normalizedCityFilter) ||
               normalizedCityFilter.includes(matchCity) ||
               (matchCity === '' && matchArea !== '' && !distinctCities.includes(matchArea)); // Allow unknown areas if not distinct cities
      });
      console.log(`[AreaDisambiguation] Filtered by city "${cityFilter}": ${beforeCount} → ${uniqueMatches.length} matches`);
    }
    
    console.log(`[AreaDisambiguation] Found ${uniqueMatches.length} unique area(s) for "${address}":`, 
      uniqueMatches.map(m => `${m.road} in ${m.area}${m.city ? ` (${m.city})` : ''}`).join(", "));
    
    // If multiple areas found for same road → disambiguation needed
    if (uniqueMatches.length > 1) {
      return {
        needsDisambiguation: true,
        matches: uniqueMatches,
        roadName: roadNameOnly,
      };
    }
    
    return {
      needsDisambiguation: false,
      matches: uniqueMatches,
      roadName: roadNameOnly,
    };
    
  } catch (e) {
    console.error("[AreaDisambiguation] Error:", e);
    return { needsDisambiguation: false, matches: [], roadName: roadNameOnly };
  }
}

/**
 * Dedupe area matches by road+area combination
 */
function dedupeAreaMatches(matches: AreaMatch[]): AreaMatch[] {
  const seen = new Set<string>();
  const result: AreaMatch[] = [];
  
  for (const m of matches) {
    const key = `${m.road.toLowerCase()}|${m.area.toLowerCase()}`;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(m);
  }
  
  return result.slice(0, 5); // Safety limit
}

/**
 * Check if address is a "bare" road name (e.g., "School Road" or "School Road, Birmingham")
 * These are candidates for area disambiguation because they lack a house number
 * 
 * Examples that SHOULD trigger disambiguation:
 * - "School Road" (bare road)
 * - "School Road, Birmingham" (road + city, but no house number)
 * - "High Street, Coventry" (road + city, but no house number)
 * 
 * Examples that should NOT trigger disambiguation:
 * - "52 School Road" (has house number - specific)
 * - "52A School Road, Birmingham" (has house number - specific)
 * - "Tesco, Birmingham" (not a road type - it's a place name)
 * - "CV1 2AB" (has postcode - specific)
 */
function isBareRoadAddress(address: string): boolean {
  const trimmed = address.trim();
  
  // Starts with a number = has house number, not bare
  if (/^\d+/.test(trimmed)) return false;
  
  // Has a full UK postcode = specific enough
  if (/\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b/i.test(trimmed)) return false;
  
  // Check if it contains a road type (Road, Street, Lane, etc.)
  const hasRoadType = extractRoadType(trimmed) !== null;
  if (!hasRoadType) return false; // Not a road address at all
  
  // If there's a comma, check if it's just "Road Name, City" format
  // vs something more specific like "Near the shops, School Road, Birmingham"
  if (trimmed.includes(",")) {
    const parts = trimmed.split(",").map(p => p.trim());
    
    // If first part starts with a number, it has a house number
    if (/^\d+/.test(parts[0])) return false;
    
    // If first part contains a road type and nothing else specific, it's bare
    // e.g., "School Road, Birmingham" → first part is "School Road"
    const firstPart = parts[0];
    const firstPartHasRoadType = extractRoadType(firstPart) !== null;
    
    // Count words in first part - bare roads typically have 2-3 words max
    const wordCount = firstPart.split(/\s+/).filter(w => w.length > 0).length;
    
    // If first part is just a road name (2-4 words with road type), it's bare
    if (firstPartHasRoadType && wordCount <= 4) {
      return true;
    }
    
    // Otherwise it might be a complex address
    return false;
  }
  
  // No comma, has road type, no house number = bare road
  return true;
}

/**
 * GLOBAL LOCATION SEARCH (no city bias)
 * Used when caller has no city context - searches UK-wide with strict validation
 * For house addresses, uses Text Search with validation to avoid wrong matches
 */
async function resolveLocationGlobal(
  query: string,
  country: string = "GB",
  strictPickup: boolean = false
): Promise<ResolvedLocation | null> {
  console.log(`[ResolveGlobal] START: '${query}', strict=${strictPickup}`);
  
  query = query.trim();
  if (!query || !GOOGLE_API_KEY) return null;
  
  const startsWithNumber = /^\d+/.test(query);

  // CRITICAL: If this is a PICKUP-style house address with NO city/postcode context,
  // do NOT guess globally (prevents wrong towns like Lymm vs Coventry).
  const hasUkPostcode = /\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b/i.test(query);
  const hasUkOutcode = /\b[A-Z]{1,2}\d[A-Z\d]?\b/i.test(query);
  const hasCommaContext = query.includes(",");
  const hasCityInText = Boolean(extractCityFromText(query));

  if (strictPickup && startsWithNumber && !hasUkPostcode && !hasUkOutcode && !hasCommaContext && !hasCityInText) {
    console.warn(`[ResolveGlobal] Refusing global pickup match without area/postcode: "${query}"`);
    return null;
  }
  
  // 1️⃣ HOUSE ADDRESS - use global Text Search with strict validation
  if (startsWithNumber) {
    console.log(`[ResolveGlobal] House address detected - using global Text Search with validation`);
    
    // Search UK-wide with address validation
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
      if (data.status !== "OK" || !data.results?.length) {
        console.warn(`[ResolveGlobal] No results for: "${query}"`);
        return null;
      }
      
      // Try each result with validation
      for (const result of data.results) {
        const placeId = result.place_id;
        const detailed = await upgradeWithPlaceDetails(placeId, query);
        
        if (detailed) {
          // Validate road type and street name match
          if (isValidAddressMatch(query, detailed.formatted_address)) {
            console.log(`✅ [ResolveGlobal] Valid match: "${query}" → "${detailed.formatted_address}"`);
            return detailed;
          } else {
            console.warn(`⚠️ [ResolveGlobal] Rejected mismatch: "${query}" → "${detailed.formatted_address}"`);
          }
        }
      }
      
      console.warn(`❌ [ResolveGlobal] No valid matches for: "${query}" (all failed validation)`);
      return null;
    } catch (e) {
      console.error("[ResolveGlobal] Error:", e);
      return null;
    }
  }
  
  // 2️⃣ NON-HOUSE ADDRESS - use global text search (for POIs, cities, etc.)
  const globalRes = await resolveGlobalTextSearch(query, country);
  if (globalRes) {
    console.log(`✅ [ResolveGlobal] Global OK: ${globalRes.formatted_address}`);
    return globalRes;
  }
  
  console.warn(`❌ [ResolveGlobal] NO_RESULTS for: ${query}`);
  return null;
}

/**
 * Legacy geocodeAddress wrapper - calls resolveLocation
 * IMPORTANT: No default city bias - if no city hint is provided, use global search with validation
 */
async function geocodeAddress(
  rawInput: string,
  cityHint?: string,
  coordsHint?: { lat: number; lng: number },
  country: string = "GB",
  strictPickup: boolean = false
): Promise<ResolvedLocation | null> {
  // Get coords from hint if available - NO DEFAULT CITY to avoid wrong matches
  const coords = coordsHint || getCityCoords(cityHint || "");
  
  if (coords) {
    // Have city context - use local biased search
    return resolveLocation(rawInput, coords.lat, coords.lng, cityHint || "", strictPickup, country);
  } else {
    // NO city context - use global search with strict validation
    // This avoids defaulting to Birmingham and finding wrong addresses
    console.log(`[geocodeAddress] No city context for "${rawInput}" - using global search`);
    return resolveLocationGlobal(rawInput, country, strictPickup);
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
    pickup_input: rawPickupInput,
    dropoff_input: rawDropoffInput,
    caller_city_hint,
    caller_coords,
    country = "GB",
    passengers = 1,
    nearby_query,
  } = body;
  
  // CRITICAL: Apply STT corrections to fix common mishearings BEFORE geocoding
  // e.g., "Davie Road" → "David Road", "School Rhodes" → "School Road"
  const pickup_input = rawPickupInput ? normalizeSTTAddress(rawPickupInput) : undefined;
  const dropoff_input = rawDropoffInput ? normalizeSTTAddress(rawDropoffInput) : undefined;
  
  console.log(`🚕 Trip Resolve Request:`, JSON.stringify({ 
    ...body, 
    pickup_normalized: pickup_input !== rawPickupInput ? pickup_input : undefined,
    dropoff_normalized: dropoff_input !== rawDropoffInput ? dropoff_input : undefined 
  }, null, 2));
  
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
    
    // Geocode PICKUP first, then use its location to bias DROPOFF search
    // This ensures venues like "Sweet Spot" are found near the pickup, not globally
    let pickup: ResolvedLocation | null = null;
    let dropoff: ResolvedLocation | null = null;
    
    // Resolve pickup first
    if (pickup_input) {
      // Helper: Check if address appears to be foreign (not UK)
      // If foreign, skip UK-specific disambiguation and just geocode globally
      const isForeignAddress = (addr: string): boolean => {
        const lower = addr.toLowerCase();
        const foreignIndicators = [
          'france', 'germany', 'spain', 'italy', 'portugal', 'poland', 'ireland', 'netherlands',
          'belgium', 'switzerland', 'austria', 'greece', 'turkey', 'usa', 'united states', 'america',
          'canada', 'australia', 'india', 'pakistan', 'bangladesh', 'nigeria', 'south africa',
          'china', 'japan', 'korea', 'dubai', 'uae', 'qatar', 'saudi',
          'paris', 'berlin', 'madrid', 'rome', 'barcelona', 'amsterdam', 'brussels', 'dublin',
          'new york', 'los angeles', 'chicago', 'toronto', 'sydney', 'melbourne',
          'strasse', 'straße', 'avenue de', 'rue de', 'calle', 'via ', 'piazza',
        ];
        return foreignIndicators.some(ind => lower.includes(ind));
      };
      
      const pickupIsForeign = isForeignAddress(pickup_input);
      
      // Check if this is a bare road address that might need area disambiguation
      // e.g., "School Road" exists in multiple areas of Birmingham
      // SKIP disambiguation for foreign addresses - just geocode globally
      if (!pickupIsForeign && isBareRoadAddress(pickup_input) && coordsContext) {
        const disambiguationCheck = await checkAreaDisambiguation(
          pickup_input, 
          coordsContext.lat, 
          coordsContext.lng,
          15000 // 15km radius for pickup disambiguation
        );
        
        if (disambiguationCheck.needsDisambiguation) {
          console.log(`[Resolve] Pickup needs area disambiguation: ${disambiguationCheck.matches.length} areas found`);
          response.needs_pickup_disambiguation = true;
          response.pickup_area_matches = disambiguationCheck.matches;
          // Don't continue with normal geocoding - let Ada ask user to choose
        }
      } else if (pickupIsForeign) {
        console.log(`[Resolve] Skipping pickup disambiguation - foreign address detected: "${pickup_input}"`);
      }
      
      // Only geocode if no disambiguation needed
      if (!response.needs_pickup_disambiguation) {
        pickup = await geocodeAddress(pickup_input, cityContext, coordsContext, country, true);  // strictPickup = true
        
        // Use pickup location for dropoff biasing
        if (pickup && pickup.lat && pickup.lng) {
          coordsContext = { lat: pickup.lat, lng: pickup.lng };
          if (!cityContext && pickup.city) {
            cityContext = pickup.city;
          }
          console.log(`[Resolve] Using pickup for dropoff bias: ${pickup.city} (${pickup.lat}, ${pickup.lng})`);
        }
      }
    }
    
    // Now resolve dropoff with pickup location as bias
    if (dropoff_input && !response.needs_pickup_disambiguation) {
      // Reuse foreign address check logic
      const isForeignAddress = (addr: string): boolean => {
        const lower = addr.toLowerCase();
        const foreignIndicators = [
          'france', 'germany', 'spain', 'italy', 'portugal', 'poland', 'ireland', 'netherlands',
          'belgium', 'switzerland', 'austria', 'greece', 'turkey', 'usa', 'united states', 'america',
          'canada', 'australia', 'india', 'pakistan', 'bangladesh', 'nigeria', 'south africa',
          'china', 'japan', 'korea', 'dubai', 'uae', 'qatar', 'saudi',
          'paris', 'berlin', 'madrid', 'rome', 'barcelona', 'amsterdam', 'brussels', 'dublin',
          'new york', 'los angeles', 'chicago', 'toronto', 'sydney', 'melbourne',
          'strasse', 'straße', 'avenue de', 'rue de', 'calle', 'via ', 'piazza',
        ];
        return foreignIndicators.some(ind => lower.includes(ind));
      };
      
      const dropoffIsForeign = isForeignAddress(dropoff_input);
      
      // Check if dropoff is a bare road address that might need area disambiguation
      // SKIP disambiguation for foreign addresses
      if (!dropoffIsForeign && isBareRoadAddress(dropoff_input) && coordsContext) {
        const disambiguationCheck = await checkAreaDisambiguation(
          dropoff_input, 
          coordsContext.lat, 
          coordsContext.lng,
          20000 // 20km radius for dropoff (can be going to nearby town)
        );
        
        if (disambiguationCheck.needsDisambiguation) {
          console.log(`[Resolve] Dropoff needs area disambiguation: ${disambiguationCheck.matches.length} areas found`);
          response.needs_dropoff_disambiguation = true;
          response.dropoff_area_matches = disambiguationCheck.matches;
        }
      } else if (dropoffIsForeign) {
        console.log(`[Resolve] Skipping dropoff disambiguation - foreign address detected: "${dropoff_input}"`);
      }
      
      // Only geocode if no disambiguation needed
      if (!response.needs_dropoff_disambiguation) {
        dropoff = await geocodeAddress(dropoff_input, cityContext, coordsContext, country, false);  // strictPickup = false (can be global)
      }
    }
    
    if (pickup) response.pickup = pickup;
    if (dropoff) response.dropoff = dropoff;
    
    // If disambiguation is needed, return early with the options
    if (response.needs_pickup_disambiguation || response.needs_dropoff_disambiguation) {
      response.ok = true; // Not an error - just needs user input
      response.inferred_area = inferArea(undefined, undefined, cityContext, coordsContext);
      
      console.log(`✅ Trip Resolve Response (disambiguation needed):`, JSON.stringify(response, null, 2));
      
      return new Response(JSON.stringify(response), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // If an input was provided but we couldn't resolve it, surface an error (prevents random/incorrect matches)
    if (pickup_input && !pickup && !response.error) {
      response.ok = false;

      const p = pickup_input.trim();
      const startsWithNumber = /^\d+/.test(p);
      const hasUkPostcode = /\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b/i.test(p);
      const hasUkOutcode = /\b[A-Z]{1,2}\d[A-Z\d]?\b/i.test(p);
      const hasCommaContext = p.includes(",");
      const hasCityInText = Boolean(extractCityFromText(p));

      // If we have no city context and the pickup is a bare house+street, force clarification
      if (!cityContext && startsWithNumber && !hasUkPostcode && !hasUkOutcode && !hasCommaContext && !hasCityInText) {
        response.error = `Pickup "${pickup_input}" needs an area/town or postcode so I don't match the wrong address. What area are you calling from (e.g., Coventry/Birmingham), or what's the postcode?`;
      } else {
        response.error = `Pickup "${pickup_input}" could not be found near ${cityContext || "your area"}. Please provide a house number and street, or a well-known landmark.`;
      }
    }

    if (dropoff_input && !dropoff && !response.error) {
      response.ok = false;
      response.error = `Destination "${dropoff_input}" could not be found. If it's a venue name, please add the area/town (e.g., "${dropoff_input}, Coventry").`;
    }
    
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
