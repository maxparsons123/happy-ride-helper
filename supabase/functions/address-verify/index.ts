import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

/* ============================================================================
   AddressContextVerifier Edge Function
   ----------------------------------------------------------------------------
   Purpose:
   - Verify pickup / destination addresses safely
   - Detect ambiguity and return smart prompts for Ada
   - Infer user city context from speech (stations, airports, etc.)
   - Bias geocoding without forcing assumptions
   - Prevent dangerous Google "corrections"
   - Designed for taxi dispatch conversational AI
============================================================================ */

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

/* ============================================================================
   TYPES
============================================================================ */

interface GeocodeMatch {
  display_name: string;
  formatted_address: string;
  lat: number;
  lon: number;
  city?: string;
  postcode?: string;
  place_id?: string;
}

interface GeocodeResult {
  found: boolean;
  address: string;
  display_name?: string;
  formatted_address?: string;
  lat?: number;
  lon?: number;
  city?: string;
  postcode?: string;
  place_id?: string;
  map_link?: string;
  cached?: boolean;
  error?: string;
  multiple_matches?: GeocodeMatch[];
}

interface VerificationResult extends GeocodeResult {
  verified: boolean;
  confidence: number; // 0.0 – 1.0
  correctionSafe: boolean;
  correctedAddress?: string;
  needsDisambiguation: boolean;
  disambiguationOptions?: GeocodeMatch[];
  userPrompt?: string; // What Ada should say to the user
  source?: "alias" | "trusted" | "cache" | "geocode";
}

interface VerifyRequest {
  address: string;
  addressType: "pickup" | "destination";
  userUtterance?: string;
  // Caller context
  callerCity?: string;
  trustedAddresses?: string[];
  addressAliases?: Record<string, string>;
  lastPickup?: string;
  lastDestination?: string;
  // Current booking context for biasing
  currentPickup?: string;
  currentDestination?: string;
  // Debug
  callId?: string;
}

/* ============================================================================
   CONSTANTS
============================================================================ */

const ADDRESS_STOP_WORDS = new Set([
  "road", "rd", "street", "st", "avenue", "ave", "drive", "dr", "lane", "ln",
  "close", "crescent", "place", "pl", "the", "a", "an", "of", "in", "uk",
  "united", "kingdom", "way", "court", "ct"
]);

const POI_HINTS = [
  "train station", "railway station", "airport", "bus station",
  "hospital", "university", "college", "city centre", "center"
];

const UK_CITIES = [
  "london", "birmingham", "manchester", "leeds", "liverpool", "newcastle",
  "sheffield", "bristol", "nottingham", "leicester", "coventry", "bradford",
  "cardiff", "edinburgh", "glasgow", "belfast", "cambridge", "oxford",
  "southampton", "portsmouth", "brighton", "reading", "derby", "wolverhampton"
];

/* ============================================================================
   HELPER FUNCTIONS
============================================================================ */

const normalize = (s: string): string => {
  return (s || "").trim().replace(/\s+/g, " ").replace(/[\s,.;:!?]+$/g, "");
};

const tokenize = (s: string): string[] => {
  return normalize(s)
    .toLowerCase()
    .split(/\s+/)
    .filter(t => t && !ADDRESS_STOP_WORDS.has(t));
};

const hasPostcode = (s: string): boolean => {
  return /[A-Z]{1,2}\d{1,2}[A-Z]?\s*\d[A-Z]{2}/i.test(s);
};

const capitalise = (s: string): string => {
  return s.charAt(0).toUpperCase() + s.slice(1);
};

const extractCityFromText = (text: string): string | undefined => {
  const lower = text.toLowerCase();
  for (const city of UK_CITIES) {
    if (lower.includes(city)) {
      return capitalise(city);
    }
  }
  return undefined;
};

/* ============================================================================
   MAIN HANDLER
============================================================================ */

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const request: VerifyRequest = await req.json();
    const {
      address,
      addressType,
      userUtterance,
      callerCity,
      trustedAddresses = [],
      addressAliases = {},
      lastPickup,
      lastDestination,
      currentPickup,
      currentDestination,
      callId = "address-verify"
    } = request;

    if (!address) {
      return new Response(
        JSON.stringify({ found: false, verified: false, error: "No address provided" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");

    const input = normalize(address);
    let inferredCity: string | undefined;
    let inferredCityConfidence = 0;

    // 0️⃣ Infer location context from what user said
    if (userUtterance) {
      const text = userUtterance.toLowerCase();
      
      // Explicit city mention with POI (e.g., "Birmingham station")
      const cityMatch = text.match(/\b([a-z]+)\s+(station|airport|centre|center)/i);
      if (cityMatch) {
        const potentialCity = cityMatch[1].trim();
        if (UK_CITIES.includes(potentialCity.toLowerCase())) {
          inferredCity = capitalise(potentialCity);
          inferredCityConfidence = 0.9;
          console.log(`[${callId}] 🏙️ Inferred city from POI: ${inferredCity}`);
        }
      }

      // POI implies caller's city
      if (!inferredCity && callerCity) {
        for (const poi of POI_HINTS) {
          if (text.includes(poi)) {
            inferredCity = callerCity;
            inferredCityConfidence = Math.max(inferredCityConfidence, 0.6);
            console.log(`[${callId}] 🏙️ Inferred caller city from POI mention: ${inferredCity}`);
            break;
          }
        }
      }
    }

    // 1️⃣ Alias resolution (home, work)
    const resolvedAlias = resolveAlias(input, addressAliases, lastPickup);
    if (resolvedAlias) {
      console.log(`[${callId}] 🏠 ALIAS RESOLVED: "${input}" → "${resolvedAlias}"`);
      return new Response(
        JSON.stringify(verified(resolvedAlias, 0.97, "alias")),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // 2️⃣ Trusted address match
    const trusted = matchTrusted(input, trustedAddresses);
    if (trusted) {
      console.log(`[${callId}] 🏆 TRUSTED ADDRESS: "${input}" → "${trusted}"`);
      return new Response(
        JSON.stringify(verified(trusted, 0.96, "trusted")),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // 3️⃣ Determine bias city
    const biasCity = extractCityFromText(input) || callerCity || inferredCity;
    
    // 4️⃣ Determine bias coordinates from context
    let biasLat: number | undefined;
    let biasLon: number | undefined;
    
    // Use the OTHER address from current booking for location context
    const otherAddress = addressType === "pickup" ? currentDestination : currentPickup;
    
    if (otherAddress && SUPABASE_URL && SUPABASE_SERVICE_ROLE_KEY) {
      try {
        const otherGeo = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
          },
          body: JSON.stringify({ address: otherAddress, city: biasCity, country: "UK" }),
        });
        const otherData = await otherGeo.json();
        if (otherData.found && otherData.lat && otherData.lon) {
          biasLat = otherData.lat;
          biasLon = otherData.lon;
          console.log(`[${callId}] 📍 Location bias from ${addressType === "pickup" ? "destination" : "pickup"}: ${biasLat}, ${biasLon}`);
        }
      } catch (e) {
        console.error(`[${callId}] Failed to get bias coords:`, e);
      }
    }

    // Fall back to caller history for bias
    if (!biasLat && !biasLon && (lastPickup || lastDestination) && SUPABASE_URL && SUPABASE_SERVICE_ROLE_KEY) {
      const historyAddress = lastPickup || lastDestination;
      try {
        const historyGeo = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
          },
          body: JSON.stringify({ address: historyAddress, city: biasCity, country: "UK" }),
        });
        const historyData = await historyGeo.json();
        if (historyData.found && historyData.lat && historyData.lon) {
          biasLat = historyData.lat;
          biasLon = historyData.lon;
          console.log(`[${callId}] 📍 Location bias from caller history: ${biasLat}, ${biasLon}`);
        }
      } catch (e) {
        console.error(`[${callId}] Failed to get history coords:`, e);
      }
    }

    // 5️⃣ Append city if missing for better geocoding
    let searchAddress = input;
    if (biasCity && !extractCityFromText(input) && !hasPostcode(input)) {
      searchAddress = `${input}, ${biasCity}`;
      console.log(`[${callId}] 🏙️ Appending city: "${input}" → "${searchAddress}"`);
    }

    // Should we check for ambiguous addresses?
    const shouldCheckAmbiguous = !hasPostcode(input) && tokenize(input).length <= 4;

    // 6️⃣ Call geocode
    console.log(`[${callId}] 🌍 Geocoding "${searchAddress}" (city: ${biasCity || 'none'}, ambiguous: ${shouldCheckAmbiguous})`);
    
    const geoResponse = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
      },
      body: JSON.stringify({
        address: searchAddress,
        city: biasCity,
        country: "UK",
        lat: biasLat,
        lon: biasLon,
        check_ambiguous: shouldCheckAmbiguous
      }),
    });

    if (!geoResponse.ok) {
      console.error(`[${callId}] Geocode API error: ${geoResponse.status}`);
      return new Response(
        JSON.stringify({
          found: false,
          verified: false,
          confidence: 0,
          correctionSafe: false,
          needsDisambiguation: false,
          address: input,
          error: "Geocoding service unavailable",
          userPrompt: addressType === "pickup"
            ? "I'm having a little trouble finding that pickup address — could you give me the full address or postcode?"
            : "I'm having a little trouble finding that destination — could you give me the full address or postcode?"
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const geo: GeocodeResult = await geoResponse.json();

    // 7️⃣ Not found
    if (!geo.found) {
      console.log(`[${callId}] ❌ Address not found: "${input}"`);
      return new Response(
        JSON.stringify({
          ...geo,
          verified: false,
          confidence: 0,
          correctionSafe: false,
          needsDisambiguation: false,
          userPrompt: addressType === "pickup"
            ? "I'm having a little trouble finding that pickup address — could you give me the full address or postcode?"
            : "I'm having a little trouble finding that destination — could you give me the full address or postcode?"
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // 8️⃣ Multiple matches → ALWAYS disambiguate
    if (geo.multiple_matches && geo.multiple_matches.length >= 2) {
      const options = geo.multiple_matches.slice(0, 3);
      const lines = options
        .map((m, i) => `${i + 1}) ${m.formatted_address || m.display_name}`)
        .join("\n");

      console.log(`[${callId}] 🔀 Multiple matches (${options.length}): disambiguation required`);

      return new Response(
        JSON.stringify({
          ...geo,
          verified: false,
          confidence: 0.5,
          correctionSafe: true,
          needsDisambiguation: true,
          disambiguationOptions: options,
          userPrompt: `I found a few places that match that ${addressType}:\n${lines}\nWhich one do you mean?`
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // 9️⃣ Confidence scoring
    const confidence =
      geo.cached ? 0.95 :
      geo.postcode ? 0.92 :
      geo.city ? 0.85 : 0.8;

    // 🔟 Safe correction check
    const proposed = normalize(geo.formatted_address || geo.display_name || "");

    if (proposed && proposed.toLowerCase() !== input.toLowerCase()) {
      const safe = isSafeCorrection(
        input,
        proposed,
        extractCityFromText(input) || callerCity || inferredCity,
        geo.city
      );

      if (!safe) {
        console.log(`[${callId}] ⚠️ Unsafe correction: "${input}" → "${proposed}" (city mismatch or house number missing)`);
        return new Response(
          JSON.stringify({
            ...geo,
            verified: false,
            confidence: 0.3,
            correctionSafe: false,
            needsDisambiguation: false,
            userPrompt: `I found something similar, but I'm not fully sure it's the right ${addressType}. Could you confirm the town or postcode?`
          }),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }

      console.log(`[${callId}] ✅ Safe correction: "${input}" → "${proposed}"`);
      return new Response(
        JSON.stringify({
          ...geo,
          verified: true,
          confidence,
          correctionSafe: true,
          correctedAddress: proposed,
          needsDisambiguation: false,
          source: geo.cached ? "cache" : "geocode"
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // ✅ Verified as-is
    console.log(`[${callId}] ✅ Verified: "${input}" → "${proposed || input}"`);
    return new Response(
      JSON.stringify({
        ...geo,
        verified: true,
        confidence,
        correctionSafe: true,
        needsDisambiguation: false,
        source: geo.cached ? "cache" : "geocode"
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );

  } catch (error) {
    console.error("[address-verify] Error:", error);
    return new Response(
      JSON.stringify({
        found: false,
        verified: false,
        confidence: 0,
        error: error instanceof Error ? error.message : "Unknown error"
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" }, status: 500 }
    );
  }
});

/* ============================================================================
   HELPER FUNCTIONS
============================================================================ */

function resolveAlias(
  address: string,
  aliases: Record<string, string>,
  lastPickup?: string
): string | null {
  const norm = address.toLowerCase();
  
  // Check saved aliases
  for (const [alias, full] of Object.entries(aliases)) {
    if (norm === alias.toLowerCase() || norm.includes(alias.toLowerCase())) {
      return full;
    }
  }
  
  // "home" fallback to last pickup
  if (/^(my\s+)?home$/i.test(norm) && lastPickup) {
    return lastPickup;
  }
  
  return null;
}

function matchTrusted(address: string, trustedAddresses: string[]): string | null {
  const norm = address.toLowerCase();
  
  for (const t of trustedAddresses) {
    const trustedNorm = normalize(t).toLowerCase();
    
    // Exact or near-exact match
    if (trustedNorm.includes(norm) || norm.includes(trustedNorm.split(',')[0])) {
      return t;
    }
    
    // Token overlap check
    const inputTokens = tokenize(address);
    const trustedTokens = tokenize(t);
    let overlap = 0;
    for (const token of inputTokens) {
      if (trustedTokens.includes(token)) overlap++;
    }
    if (overlap >= 2 || (inputTokens.length <= 2 && overlap >= 1)) {
      return t;
    }
  }
  
  return null;
}

function isSafeCorrection(
  original: string,
  proposed: string,
  originalCity?: string,
  proposedCity?: string
): boolean {
  // Check house number preserved
  const oNum = original.match(/\b\d+[a-z]?\b/i)?.[0];
  if (oNum && !proposed.toLowerCase().includes(oNum.toLowerCase())) {
    return false;
  }

  // Check city consistency
  if (originalCity && proposedCity && 
      originalCity.toLowerCase() !== proposedCity.toLowerCase()) {
    return false;
  }

  // Token overlap check
  const oTokens = tokenize(original);
  const pTokens = new Set(tokenize(proposed));
  let overlap = 0;
  for (const t of oTokens) {
    if (pTokens.has(t)) overlap++;
  }
  
  return overlap >= 2 || oTokens.length <= 2;
}

function verified(
  address: string,
  confidence: number,
  source: "alias" | "trusted" | "cache" | "geocode"
): VerificationResult {
  return {
    found: true,
    verified: true,
    confidence,
    correctionSafe: true,
    needsDisambiguation: false,
    address,
    display_name: address,
    formatted_address: address,
    source
  };
}
