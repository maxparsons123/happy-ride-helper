import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// ── Fare calculation constants (matching C# FareCalculator) ──
const BASE_FARE = 3.50;
const PER_MILE = 1.00;
const MIN_FARE = 4.00;
const AVG_SPEED_MPH = 20.0;
const BUFFER_MINUTES = 3;

function haversineDistanceMiles(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const R = 3958.8; // Earth radius in miles
  const dLat = (lat2 - lat1) * Math.PI / 180;
  const dLon = (lon2 - lon1) * Math.PI / 180;
  const a = Math.sin(dLat / 2) ** 2 +
    Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
    Math.sin(dLon / 2) ** 2;
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

/** Extract a normalized street key for fuzzy comparison: "52a david road" from "52A David Road, Coventry CV1 2BW" */
function extractStreetKey(input: string): string {
  return input
    .replace(/\b[a-z]{1,2}\d{1,2}\s?\d[a-z]{2}\b/gi, "") // remove postcodes
    .split(",")[0] // take first segment (street + number)
    .replace(/\s+/g, " ")
    .trim();
}

// Driver ETA constants — how long for a driver to reach the passenger
const DRIVER_ETA_MIN = 8;
const DRIVER_ETA_MAX = 15;
const DRIVER_ETA_DEFAULT = 10;

function calculateFare(distanceMiles: number, detectedCountry?: string): { fare: string; fare_spoken: string; eta: string; driver_eta: string; driver_eta_minutes: number; trip_eta: string; trip_eta_minutes: number; distance_miles: number } {
  const rawFare = Math.max(MIN_FARE, BASE_FARE + distanceMiles * PER_MILE);
  // Round to nearest 0.50
  const fare = Math.round(rawFare * 2) / 2;
  
  // Trip ETA = how long the journey itself takes (for internal use / logging)
  const tripEtaMinutes = Math.ceil(distanceMiles / AVG_SPEED_MPH * 60) + BUFFER_MINUTES;
  
  // Driver ETA = how long for a driver to reach the passenger (what we tell the caller)
  // Scale slightly with distance: short trips get lower ETA, longer trips slightly higher
  const driverEtaMinutes = Math.min(DRIVER_ETA_MAX, Math.max(DRIVER_ETA_MIN, DRIVER_ETA_DEFAULT + Math.floor(distanceMiles / 20)));

  // Currency based on detected country
  const isNL = detectedCountry === "NL";
  const currencySymbol = isNL ? "€" : "£";
  const currencyWord = isNL ? "euros" : "pounds";
  const subunitWord = isNL ? "cents" : "pence";

  const fareStr = `${currencySymbol}${fare.toFixed(2)}`;

  // Generate spoken fare (e.g., "12 pounds 50" or "25 pounds")
  const whole = Math.floor(fare);
  const subunit = Math.round((fare - whole) * 100);
  const fareSpoken = subunit > 0 ? `${whole} ${currencyWord} ${subunit} ${subunitWord}` : `${whole} ${currencyWord}`;

  return {
    fare: fareStr,
    fare_spoken: fareSpoken,
    eta: `${driverEtaMinutes} minutes`,           // ← this is what Ada tells the caller
    driver_eta: `${driverEtaMinutes} minutes`,     // explicit driver arrival time
    driver_eta_minutes: driverEtaMinutes,
    trip_eta: `${tripEtaMinutes} minutes`,         // full journey duration (internal)
    trip_eta_minutes: tripEtaMinutes,
    distance_miles: Math.round(distanceMiles * 100) / 100,
  };
}

const SYSTEM_PROMPT = `Role: Professional Taxi Dispatch Logic System for UK & Netherlands.

Objective: Extract, validate, and GEOCODE pickup and drop-off addresses from user messages, using phone number patterns to determine geographic bias.

LOCALITY AWARENESS (HIGHEST PRIORITY — COMMONSENSE GEOGRAPHY):
A taxi booking is a LOCAL journey. Both pickup and dropoff should be within a reasonable taxi distance (typically under 80 miles / 130 km). Apply these rules:

1. INFER CUSTOMER LOCALITY: Use ALL available signals to determine where the customer is:
   - Phone area code (strongest signal for landlines)
   - Caller history (previous addresses reveal their area)
   - Pickup address (if already resolved, dropoff should be local to it)
   - Explicitly mentioned city/area names
   - GPS coordinates if provided

2. BIAS ALL ADDRESSES TO CUSTOMER LOCALITY:
   - Once you determine the customer's likely area (e.g., Coventry from a 024 number), ALL address lookups should prefer results in or near that area.
   - "High Street" from a Coventry caller → High Street, Coventry. NOT High Street, Edinburgh.
   - "The Railway pub" from a Birmingham caller → search for The Railway in Birmingham area first.
   - If the pickup is resolved to Coventry, the dropoff should default-search Coventry and surrounding areas (Warwickshire, West Midlands) BEFORE considering distant matches.

3. CROSS-COUNTRY / ABSURD DISTANCE REJECTION:
   - If your resolved pickup and dropoff are in DIFFERENT COUNTRIES (e.g., UK pickup, Belgium dropoff), flag this as implausible UNLESS the caller explicitly stated both locations.
   - If the straight-line distance between pickup and dropoff exceeds 100 miles, set a warning: "distance_warning": "Pickup and dropoff are X miles apart — is this correct?"
   - Journeys over 200 miles are almost certainly errors — set is_ambiguous=true on the more uncertain address and ask the caller to confirm.

4. PICKUP-TO-DROPOFF PROXIMITY BIAS:
   - When the pickup is already resolved with coordinates, use those coordinates as a CENTER POINT for dropoff search.
   - Prefer dropoff matches within 30 miles of pickup. Accept up to 80 miles. Flag anything beyond.
   - Example: Pickup at "12 Russell St, Coventry" → dropoff "Park Road" should resolve to Park Road in Coventry/Warwickshire, NOT Park Road in London.

5. SAME-CITY DEFAULT:
   - If only one address mentions a city and the other doesn't, assume both are in the SAME city unless evidence suggests otherwise.
   - If pickup is "52 David Road, Coventry" and dropoff is "the train station", resolve to Coventry Railway Station, not London Euston.

SPEECH-TO-TEXT MISHEARING AWARENESS (CRITICAL):
Inputs come from live speech recognition and are often phonetically garbled. Apply phonetic reasoning:
- If an input doesn't match a real street/venue, check if it's a mishearing of a well-known place (e.g., "Colesie Grove"→"Cosy Club", "The Globe"→"The Cosy Club", "Davies Road"→"David Road").
- Chain/landmark mishearings MUST still trigger chain disambiguation rules below.
- Prefer well-known venues over obscure matches. Never resolve garbled input to an unlikely location.

CALLER HISTORY MATCHING (HIGHEST PRIORITY):
When CALLER_HISTORY is provided, it contains addresses this specific caller has used before.
- ALWAYS fuzzy-match the user's current input against their history FIRST before any other disambiguation.
- If the user says "School Road" and their history contains "School Road, Hall Green, Birmingham" → resolve directly to that address. No clarification needed.
- If the user says "the pub" and their history contains "The Royal Oak, Botts Lane" → resolve to that address.
- Partial matches count: "Russell" matching "7 Russell Street, Coventry" is a valid fuzzy match.
- Name variations count: "Robics" matching "The Robics Club, Botts Lane" is valid.
- If the user's input matches a history entry with >70% confidence, use it directly and set is_ambiguous=false.
- If multiple history entries match equally well, prefer the most recently used one.
- When a history match is used, set region_source to "caller_history" in the output.

PHONE NUMBER BIASING LOGIC (CRITICAL):
1. UK Landline Area Codes - STRONG bias to specific city:
   - 024 or +44 24 → Coventry
   - 020 or +44 20 → London
   - 0121 or +44 121 → Birmingham
   - 0161 or +44 161 → Manchester
   - 0113 or +44 113 → Leeds
   - 0114 or +44 114 → Sheffield
   - 0115 or +44 115 → Nottingham
   - 0116 or +44 116 → Leicester
   - 0117 or +44 117 → Bristol
   - 0118 or +44 118 → Reading

2. UK Mobile (+44 7 or 07) → NO geographic clue. You MUST:
   - First check CALLER_HISTORY for fuzzy matches (this resolves most mobile ambiguity)
   - Then check if a city name is explicitly mentioned in the address text
   - Check if a unique landmark is mentioned (e.g., "Heathrow Airport" → London)
   - If the street name exists in MULTIPLE UK cities and NO city/landmark/area is mentioned AND no history match:
     → You MUST set is_ambiguous=true, status="clarification_needed", and provide top 3 city alternatives
     → You MUST set clarification_message to a human-readable question like "Which city is David Road in? I found it in Coventry, Birmingham, and London."
   - IMPORTANT: Most common UK street names exist in MANY cities. You MUST check this.
     These are ALWAYS ambiguous from a mobile caller without a city/postcode/landmark:
     "High Street", "Church Road", "Station Road", "Russell Street", "Park Road",
     "David Road", "Victoria Road", "Albert Road", "King Street", "Queen Street",
     "School Road", "Mill Lane", "Green Lane", "New Road", "London Road",
     and ANY other street name that exists in 2+ UK cities.
   - NEVER assume a city just because the street exists there — you MUST ask
   - NEVER default to London, Coventry, Birmingham or any other city without evidence
   - When in doubt, flag as ambiguous — it is MUCH better to ask than to guess wrong

3. Netherlands:
   - +31 or 0031 → Netherlands
   - 020 → Amsterdam, 010 → Rotterdam, 070 → The Hague, 030 → Utrecht

4. EXPLICIT CITY NAME OVERRIDES PHONE PREFIX (CRITICAL):
   - If the user SAYS a city name in their address text (e.g., "in Gent", "Coventry", "Amsterdam"),
     that ALWAYS takes priority over any phone-prefix-based geographic bias.
   - Example: Caller with +31 (Netherlands) prefix says "vliegtuiglaan nummer 5 in Gent" 
     → Resolve to Gent/Ghent, BELGIUM — NOT to any Netherlands location.
   - Example: Caller with +44 (UK) prefix says "Rue de la Loi, Brussels"
     → Resolve to Brussels, Belgium — NOT to any UK location.
   - The phone prefix is a HINT for disambiguation when NO city is mentioned. 
     It must NEVER override an explicitly stated city name.
   - Set region_source to "text_mention" when the city was explicitly stated by the caller.

CHAIN BUSINESSES & LANDMARKS WITH MULTIPLE LOCATIONS (CRITICAL):
Many businesses are chains with locations in multiple cities (e.g., "The Cosy Club", "Nando's", "Wetherspoons", "Tesco", "Costa Coffee", "Premier Inn").
- For a MOBILE caller with NO city context, NO caller history match, and NO area/postcode mentioned:
  → You MUST set is_ambiguous=true, status="clarification_needed"
  → Provide the top 3 city locations as alternatives (e.g., ["The Cosy Club, Birmingham", "The Cosy Club, Manchester", "The Cosy Club, Coventry"])
  → Set clarification_message to ask which location (e.g., "The Cosy Club has locations in several cities. Which one do you mean? Birmingham, Manchester, or Coventry?")
- If a LANDLINE caller mentions a chain, bias to their landline city's branch
- If the destination provides city context (e.g., going to "Manchester Airport"), you may use that to infer the pickup city ONLY if it's highly logical (same-city trip), but if ambiguous, still ask
- NEVER assume a specific branch without evidence

PLACE NAME / POI DETECTION (CRITICAL):
When an input has NO house number, treat it as a potential business name, landmark, or Point of Interest (POI) FIRST:
- "Sweet Spot" → search for a business/café/venue called "Sweet Spot" in the detected area, NOT "Sweet Spot Street"
- "Tesco" → resolve to the nearest Tesco store, not a street named Tesco
- "The Railway" → likely a pub called "The Railway", not Railway Road
- "Costa" → Costa Coffee, not a street
- If the input matches a well-known business/venue/landmark, return its actual address and coordinates
- Only fall back to street-level matching if NO POI match is found
- When resolved as a POI, include the business name in the address (e.g., "Sweet Spot, 12 High Street, Coventry CV1 3AB")
- If the POI has multiple locations in the area, apply the same chain disambiguation rules below

"NEAREST X" / RELATIVE POI RESOLUTION (CRITICAL):
When the user says "nearest", "closest", "the local", or similar relative terms before a type of place, you MUST:
- Identify the CATEGORY of place requested (hospital, station, airport, supermarket, pharmacy, etc.)
- Determine the caller's locality from: resolved pickup address > phone area code > caller history > GPS
- Resolve to the ACTUAL NEAREST real-world instance of that category relative to the caller's location
- Examples:
  - Caller in Coventry says "nearest hospital" → University Hospital Coventry, Clifford Bridge Road, CV2 2DX
  - Caller in Tile Hill, Coventry says "nearest station" → Tile Hill Railway Station, not Coventry Station (which is further)
  - Caller in Birmingham says "drop me at the closest A&E" → Queen Elizabeth Hospital Birmingham, Mindelsohn Way, B15 2GW
  - Caller says "take me to the nearest Tesco" with pickup in Earlsdon → Tesco Express, Earlsdon Street, Coventry
- ALWAYS resolve to a REAL, NAMED place with its actual street address and coordinates
- If the pickup is already resolved, use its coordinates as the center point for "nearest" calculations
- If multiple options are roughly equidistant (within 0.5 miles), pick the most prominent/well-known one
- NEVER respond with just a category like "Nearest Hospital" — always resolve to the actual place name and address
- Set region_source to "nearest_poi" when resolving relative POI requests

ABBREVIATED / PARTIAL STREET NAME MATCHING (CRITICAL):
Users frequently shorten or abbreviate street names. You MUST resolve partial names to real streets:
- "Fargo" → "Fargosford Street" (partial prefix match in Coventry — do NOT invent "Fargo Street")
- "Warwick Rd" → "Warwick Road"
- "Kenilworth" → "Kenilworth Road" (when context suggests a street, not the town)
- "Binley Rd" → "Binley Road"
- "Holyhead" → "Holyhead Road"
- "Stoney" → "Stoney Stanton Road"
When a user says a word that is a prefix/abbreviation of a known street in the detected city, resolve to the FULL real street name. NEVER fabricate a street name — if "Fargo Street" does not exist in that city, check for real streets starting with "Fargo" (e.g., "Fargosford Street" in Coventry).

ADDRESS EXTRACTION RULES:
1. Preserve house numbers EXACTLY as spoken (e.g., "52A" stays "52A", "1214A" stays "1214A")
2. Do NOT invent house numbers if not provided
3. Append detected city to addresses for clarity
4. LANDMARK / NAMED PLACE ADDRESS FIELD (CRITICAL):
   - When the input resolves to a named place (station, airport, supermarket, hospital, hotel, pub, school, university, etc.), the "address" field MUST begin with the well-known name of that place, followed by the street and postal code.
   - CORRECT: "Birmingham New Street Station, New Street, Birmingham B2 4QA"
   - WRONG:   "New Street, Birmingham B2 4QA"  ← drops the landmark name
   - CORRECT: "Aldi, Warwick Road, Coventry CV3 6PT"
   - WRONG:   "Warwick Road, Coventry CV3 6PT"  ← drops the business name
   - The caller spoke the name for a reason — always include it in the address field so it can be read back to them accurately.
5. ALWAYS include the postal code in the address field (e.g., "7 Russell Street, Coventry CV1 3BT")
6. The postal_code field MUST be populated separately whenever determinable
7. CRITICAL — INDEPENDENT POSTCODES: Each address (pickup AND dropoff) MUST have its OWN independently determined postal code. Different streets almost ALWAYS have different postcodes. NEVER copy the pickup's postal code to the dropoff or vice versa. If you cannot determine the exact postal code for an address, leave postal_code as an empty string rather than reusing the other address's postcode.

INTRA-CITY DISTRICT DISAMBIGUATION (CRITICAL):
Even when the CITY is known (e.g., Birmingham from landline 0121), many street names exist in MULTIPLE districts within that city.
- Example: "School Road" exists in Hall Green, Moseley, Yardley, Kings Heath, and other Birmingham districts.
- Example: "Church Road" exists in Erdington, Aston, Yardley, Sheldon, and others within Birmingham.
- Example: "Park Road" exists in Moseley, Hockley, Aston, Sparkbrook within Birmingham.
- FIRST check CALLER_HISTORY — if the caller has been to "School Road, Hall Green" before and says "School Road", resolve to Hall Green directly. Set districts_found = [].
- **ALWAYS** populate "districts_found" with every district you know this street appears in within the city — even if you are resolving it confidently. This is used as a safety signal by the post-processor.
  - Example: "3 School Road, Birmingham" → districts_found: ["Hall Green", "Moseley", "Yardley", "Kings Heath", "Handsworth"]
  - If the street only exists in one district in the city → districts_found: []
- If NO history match and districts_found has 2+ entries AND no district/area/postcode is mentioned in the user input:
  → Set is_ambiguous=true, status="clarification_needed"
  → Provide alternatives as "Street Name, District" (e.g., "School Road, Hall Green", "School Road, Moseley")
  → Set clarification_message to ask the user which area/district they mean
- A house number >= 500 narrows candidates significantly — if only one district has numbers that high, resolve directly and set is_ambiguous=false.
- IMPORTANT: Use your world knowledge to enumerate ALL real districts — do not cap at 3.


HOUSE NUMBER DISAMBIGUATION (CRITICAL):
When a user provides a house number with a street name, USE the house number to disambiguate which street it is:
- Example: "1214A Warwick Road" — many cities have a Warwick Road, but very few have numbers as high as 1214. 
  A long road with 1200+ addresses is most likely the A41 Warwick Road in Birmingham/Acocks Green area.
- Example: "52A David Road" with Coventry context — David Road in Coventry is a short residential street where 52A is plausible.
- High house numbers (500+) strongly suggest long arterial roads, which narrows the candidates significantly.
- Low house numbers (1-50) are common on short streets everywhere, so rely more on phone/city bias.
- Alphanumeric suffixes (A, B, C) suggest subdivided properties, common in certain neighborhoods.
This is a powerful signal — a street + house number combination is often unique to ONE specific road in the country.

GEOCODING RULES (CRITICAL):
1. You MUST provide lat/lon coordinates for EVERY resolved address
2. Use your knowledge of real-world geography to provide accurate coordinates
3. For specific street addresses with house numbers, provide coordinates as precisely as possible
4. For landmarks, restaurants, hotels etc., provide their known location coordinates
5. For ambiguous addresses, provide coordinates for the MOST LIKELY match based on phone bias AND house number plausibility
6. Coordinates must be realistic (UK lat ~50-58, lon ~-6 to 2; NL lat ~51-53, lon ~3-7)
7. Extract structured components: street_name, street_number, postal_code, city

PICKUP TIME NORMALISATION (CRITICAL):
When a pickup_time is provided in the user message, parse it into ISO 8601 UTC: "YYYY-MM-DDTHH:MM:SSZ".
The REFERENCE_DATETIME (current UTC) is provided in the user message.

Rules:
- "now", "asap", "immediately", "straight away", "right now" → scheduled_at = null
- "in X minutes" → REFERENCE + X minutes
- "in X hours" → REFERENCE + X hours
- "in X days" → REFERENCE + X days
- Time only ("5pm") → today at that time; if past → tomorrow
- "tonight" → today 21:00; "this evening" → 19:00; "afternoon" → 15:00; "morning" → 09:00
- "tomorrow" → tomorrow same time; "tomorrow at 3pm" → tomorrow 15:00
- "this time tomorrow" → REFERENCE + 24h; "same time next week" → REFERENCE + 7d
- Day-of-week: "this Wed" → nearest Wed; "next Wed" → Wed of next week
- "Friday evening" → next Fri 19:00; "Monday morning" → next Mon 09:00
- NO-PAST RULE: always roll forward if result < REFERENCE_DATETIME
- Not provided or empty → scheduled_at = null

OUTPUT FORMAT (STRICT JSON):
{
  "detected_area": "city name or region",
  "region_source": "caller_history|landline_area_code|text_mention|landmark|unknown",
  "phone_analysis": {
    "detected_country": "UK" | "NL" | "unknown",
    "is_mobile": boolean,
    "landline_city": "city name or null"
  },
  "pickup": {
    "address": "full resolved address with city",
    "lat": number,
    "lon": number,
    "street_name": "street name only",
    "street_number": "house number or empty string",
    "postal_code": "postal code if known or empty string",
    "city": "city name",
    "is_ambiguous": boolean,
    "alternatives": ["option1", "option2"],
    "matched_from_history": boolean
  },
  "dropoff": {
    "address": "full resolved address with city",
    "lat": number,
    "lon": number,
    "street_name": "street name only",
    "street_number": "house number or empty string",
    "postal_code": "postal code if known or empty string",
    "city": "city name",
    "is_ambiguous": boolean,
    "alternatives": [],
    "matched_from_history": boolean
  },
  "scheduled_at": "ISO 8601 UTC datetime string or null if ASAP/immediate",
  "status": "ready" | "clarification_needed",
  "clarification_message": "question for the caller when address is ambiguous"
}`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { pickup, destination, phone, pickup_time, pickup_house_number, destination_house_number } = await req.json();
    
    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY is not configured");
    }

    console.log(`📍 Address dispatch request: pickup="${pickup}", dest="${destination}", phone="${phone}", time="${pickup_time || 'not provided'}", spokenPickupNum="${pickup_house_number || ''}", spokenDestNum="${destination_house_number || ''}"`);

    // Look up caller history from database
    let callerHistory = "";
    if (phone) {
      try {
        const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
        const supabaseKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
        const supabase = createClient(supabaseUrl, supabaseKey);

        // Normalize phone for lookup (strip + prefix)
        const normalizedPhone = phone.replace(/^\+/, "");
        const phoneVariants = [phone, normalizedPhone, `+${normalizedPhone}`];

        const { data: caller } = await supabase
          .from("callers")
          .select("pickup_addresses, dropoff_addresses, trusted_addresses, last_pickup, last_destination, name")
          .or(phoneVariants.map(p => `phone_number.eq.${p}`).join(","))
          .maybeSingle();

        if (caller) {
          const allAddresses = new Set<string>();
          if (caller.last_pickup) allAddresses.add(caller.last_pickup);
          if (caller.last_destination) allAddresses.add(caller.last_destination);
          (caller.pickup_addresses || []).forEach((a: string) => allAddresses.add(a));
          (caller.dropoff_addresses || []).forEach((a: string) => allAddresses.add(a));
          (caller.trusted_addresses || []).forEach((a: string) => allAddresses.add(a));

          if (allAddresses.size > 0) {
            callerHistory = `\n\nCALLER_HISTORY (addresses this caller has used before):\n${[...allAddresses].map((a, i) => `${i + 1}. ${a}`).join("\n")}`;
            console.log(`📋 Found ${allAddresses.size} historical addresses for caller`);
          } else {
            console.log(`📋 Caller found but no address history`);
          }
        } else {
          console.log(`📋 No caller record found for ${phone}`);
        }
      } catch (dbErr) {
        console.warn(`⚠️ Caller history lookup failed (non-fatal):`, dbErr);
      }
    }

    // Build the user message with reference datetime for time parsing
    const refDatetime = new Date().toISOString();
    const timePart = pickup_time ? `\nPickup Time Requested: "${pickup_time}"\nREFERENCE_DATETIME (current UTC): ${refDatetime}` : '';

    // House numbers extracted from caller's speech via AddressParser — used as geocoding filters.
    // Gemini must resolve to a location that actually has this number on the street.
    let houseNumberHints = '';
    if (pickup_house_number) {
      houseNumberHints += `\nPICKUP HOUSE NUMBER (extracted from caller's speech): "${pickup_house_number}" — use this as a GEOCODING FILTER. Only resolve to addresses on that street that actually contain house number ${pickup_house_number}. Set street_number to exactly "${pickup_house_number}". If no such number exists on that street, flag is_ambiguous=true.`;
    }
    if (destination_house_number) {
      houseNumberHints += `\nDESTINATION HOUSE NUMBER (extracted from caller's speech): "${destination_house_number}" — use this as a GEOCODING FILTER. Only resolve to addresses on that street that actually contain house number ${destination_house_number}. Set street_number to exactly "${destination_house_number}". If no such number exists on that street, flag is_ambiguous=true.`;
    }

    const userMessage = `User Message: Pickup from "${pickup || 'not provided'}" going to "${destination || 'not provided'}"
User Phone: ${phone || 'not provided'}${timePart}${houseNumberHints}${callerHistory}`;

    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash",
        messages: [
          { role: "system", content: SYSTEM_PROMPT },
          { role: "user", content: userMessage },
        ],
        temperature: 0.1,
        tools: [{
          type: "function",
          function: {
            name: "resolve_addresses",
            description: "Return the resolved pickup and dropoff addresses with coordinates and disambiguation info",
            parameters: {
              type: "object",
              properties: {
                detected_area: { type: "string" },
                region_source: { type: "string", enum: ["caller_history", "landline_area_code", "text_mention", "landmark", "nearest_poi", "unknown"] },
                phone_analysis: {
                  type: "object",
                  properties: {
                    detected_country: { type: "string" },
                    is_mobile: { type: "boolean" },
                    landline_city: { type: "string" }
                  },
                  required: ["detected_country", "is_mobile"]
                },
                pickup: {
                  type: "object",
                  properties: {
                    address: { type: "string" },
                    lat: { type: "number" },
                    lon: { type: "number" },
                    street_name: { type: "string" },
                    street_number: { type: "string" },
                    postal_code: { type: "string" },
                    city: { type: "string" },
                    is_ambiguous: { type: "boolean" },
                    alternatives: { type: "array", items: { type: "string" } },
                    districts_found: { type: "array", items: { type: "string" }, description: "List of districts/areas this street appears in within the city (e.g. ['Hall Green', 'Moseley', 'Yardley']). Populate when is_ambiguous=true due to multi-district street." },
                    matched_from_history: { type: "boolean" }
                  },
                  required: ["address", "lat", "lon", "city", "is_ambiguous"]
                },
                dropoff: {
                  type: "object",
                  properties: {
                    address: { type: "string" },
                    lat: { type: "number" },
                    lon: { type: "number" },
                    street_name: { type: "string" },
                    street_number: { type: "string" },
                    postal_code: { type: "string" },
                    city: { type: "string" },
                    is_ambiguous: { type: "boolean" },
                    alternatives: { type: "array", items: { type: "string" } },
                    districts_found: { type: "array", items: { type: "string" }, description: "List of districts/areas this street appears in within the city (e.g. ['Hall Green', 'Moseley', 'Yardley']). Populate when is_ambiguous=true due to multi-district street." },
                    matched_from_history: { type: "boolean" }
                  },
                  required: ["address", "lat", "lon", "city", "is_ambiguous"]
                },
                scheduled_at: { type: "string", description: "ISO 8601 UTC datetime for scheduled pickup, or null if ASAP/immediate" },
                status: { type: "string", enum: ["ready", "clarification_needed"] },
                clarification_message: { type: "string" }
              },
              required: ["detected_area", "region_source", "phone_analysis", "pickup", "dropoff", "status"],
              additionalProperties: false
            }
          }
        }],
        tool_choice: { type: "function", function: { name: "resolve_addresses" } },
      }),
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`AI gateway error: ${response.status} - ${errorText}`);
      
      if (response.status === 429) {
        return new Response(JSON.stringify({ 
          error: "Rate limit exceeded",
          status: "error" 
        }), {
          status: 429,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      
      throw new Error(`AI gateway error: ${response.status}`);
    }

    let aiResponse;
    try {
      const responseText = await response.text();
      console.log("AI gateway raw response length:", responseText.length);
      aiResponse = JSON.parse(responseText);
    } catch (parseErr) {
      console.error("Failed to parse AI gateway response:", parseErr);
      throw new Error("AI gateway returned invalid JSON");
    }

    // Parse structured tool call response (primary) or fallback to content parsing
    let parsed;
    const toolCall = aiResponse.choices?.[0]?.message?.tool_calls?.[0];
    if (toolCall?.function?.arguments) {
      try {
        parsed = typeof toolCall.function.arguments === "string" 
          ? JSON.parse(toolCall.function.arguments) 
          : toolCall.function.arguments;
        console.log("✅ Parsed structured tool call response");
      } catch (parseErr) {
        console.error("Failed to parse tool call arguments:", parseErr);
      }
    }
    
    // Fallback: parse from content if tool call didn't work
    if (!parsed) {
      const content = aiResponse.choices?.[0]?.message?.content;
      if (!content) {
        throw new Error("No content or tool call in AI response");
      }
      try {
        let jsonStr = content.trim();
        const codeBlockMatch = jsonStr.match(/```(?:json)?\s*([\s\S]*?)```/);
        if (codeBlockMatch) jsonStr = codeBlockMatch[1].trim();
        parsed = JSON.parse(jsonStr);
        console.log("⚠️ Fell back to content parsing");
      } catch (parseErr) {
        console.error("Failed to parse AI JSON:", content);
        parsed = {
          detected_area: "unknown",
          phone_analysis: { detected_country: "unknown", is_mobile: false, landline_city: null },
          pickup: { address: pickup || "", is_ambiguous: false, alternatives: [] },
          dropoff: { address: destination || "", is_ambiguous: false, alternatives: [] },
          status: "ready"
        };
      }
    }

    // ── Post-processing: CLEAR disambiguation via caller history OR explicit city ──
    // Priority: 1) Caller history match  2) Explicit city in input
    const KNOWN_CITIES = [
      "Coventry", "Birmingham", "London", "Manchester", "Leeds", "Sheffield",
      "Nottingham", "Leicester", "Bristol", "Reading", "Liverpool", "Newcastle",
      "Brighton", "Oxford", "Cambridge", "Wolverhampton", "Derby", "Stoke",
      "Southampton", "Portsmouth", "Edinburgh", "Glasgow", "Cardiff", "Belfast",
      "Warwick", "Kenilworth", "Solihull", "Sutton Coldfield", "Leamington",
      "Rugby", "Nuneaton", "Bedworth", "Stratford", "Redditch",
      "Amsterdam", "Rotterdam", "The Hague", "Utrecht", "Eindhoven",
      "Gent", "Ghent", "Brussels", "Antwerp", "Bruges",
    ];
    
    // Build a flat set of all caller history addresses for fuzzy matching
    const historyAddresses: string[] = [];
    if (callerHistory) {
      const lines = callerHistory.split("\n").filter(l => /^\d+\.\s/.test(l.trim()));
      for (const line of lines) {
        const addr = line.replace(/^\d+\.\s*/, "").trim();
        if (addr) historyAddresses.push(addr);
      }
    }

    for (const side of ["pickup", "dropoff"] as const) {
      const addr = parsed[side];
      if (!addr || !addr.is_ambiguous) continue;
      
      const originalInput = side === "pickup" ? (pickup || "") : (destination || "");
      const inputLower = originalInput.toLowerCase().replace(/\s+/g, " ").trim();
      
      // CHECK 1: Does the input fuzzy-match a caller history address?
      if (historyAddresses.length > 0) {
        const historyMatch = historyAddresses.find(h => {
          const hLower = h.toLowerCase().replace(/\s+/g, " ").trim();
          // Check if history contains the input or input contains key parts of history
          return hLower.includes(inputLower) || inputLower.includes(hLower) ||
            // Fuzzy: extract street+number from both and compare
            extractStreetKey(inputLower) === extractStreetKey(hLower);
        });
        
        if (historyMatch) {
          console.log(`✅ Caller history match: "${originalInput}" → "${historyMatch}" — clearing disambiguation for ${side}`);
          addr.is_ambiguous = false;
          addr.alternatives = [];
          addr.matched_from_history = true;
          // Use the history address if Gemini's resolution looks wrong
          if (!addr.address || addr.address.length < historyMatch.length) {
            addr.address = historyMatch;
          }
          const otherSide = side === "pickup" ? "dropoff" : "pickup";
          if (!parsed[otherSide]?.is_ambiguous) {
            parsed.status = "ready";
            parsed.clarification_message = undefined;
          }
          continue; // Skip city check — history is higher priority
        }
      }
      
      // CHECK 2: Does the input explicitly mention a city?
      // NOTE: Do NOT clear ambiguity here if districts_found has 2+ entries —
      // the multi-district safety net (Case 2 below) must still run.
      const explicitCity = KNOWN_CITIES.find(c => inputLower.includes(c.toLowerCase()));
      const hasMultiDistricts = (addr.districts_found || []).length >= 2;
      if (explicitCity && !hasMultiDistricts) {
        console.log(`✅ User explicitly said "${explicitCity}" in "${originalInput}" — clearing disambiguation for ${side}`);
        addr.is_ambiguous = false;
        addr.alternatives = [];
        const otherSide = side === "pickup" ? "dropoff" : "pickup";
        if (!parsed[otherSide]?.is_ambiguous) {
          parsed.status = "ready";
          parsed.clarification_message = undefined;
        }
      } else if (explicitCity && hasMultiDistricts) {
        console.log(`⚠️ User said "${explicitCity}" but street has ${addr.districts_found.length} districts — deferring to multi-district safety net`);
      }
    }

    // ── Post-processing: enforce disambiguation for multi-district streets ──
    // Gemini populates districts_found[] when it detects a street in multiple areas.
    // This block honours that signal and falls back to the uk_locations DB if Gemini
    // flagged ambiguity without supplying district names.
    const supabaseUrl2 = Deno.env.get("SUPABASE_URL")!;
    const supabaseKey2 = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const supabase2 = createClient(supabaseUrl2, supabaseKey2);

    // Cache: city → district list (avoids duplicate DB calls for pickup+dropoff)
    const districtCache: Record<string, string[]> = {};

    async function getCityDistricts(city: string): Promise<string[]> {
      if (districtCache[city]) return districtCache[city];
      try {
        const { data } = await supabase2
          .from("uk_locations")
          .select("name")
          .ilike("parent_city", city)
          .in("type", ["district", "suburb", "neighbourhood"])
          .limit(50);
        const areas = (data || []).map((r: { name: string }) => r.name);
        districtCache[city] = areas;
        return areas;
      } catch {
        districtCache[city] = [];
        return [];
      }
    }

    for (const side of ["pickup", "dropoff"] as const) {
      const addr = parsed[side];
      if (!addr) continue;

      // Trust caller-history matches — no need to disambiguate
      if (addr.matched_from_history === true) {
        console.log(`✅ "${addr.street_name || addr.address}" matched from caller history — skipping disambiguation`);
        continue;
      }

      const originalInput = side === "pickup" ? (pickup || "") : (destination || "");
      const hasPostcode = /\b[A-Z]{1,2}\d{1,2}\s?\d[A-Z]{2}\b/i.test(originalInput);
      const houseNumberMatch = (addr.street_number || "").match(/^(\d+)/);
      const houseNumber = houseNumberMatch ? parseInt(houseNumberMatch[1], 10) : 0;
      const isHighHouseNumber = houseNumber >= 500;

      const streetName = addr.street_name || "";
      const city = addr.city || parsed.detected_area || "";
      const districtsFound: string[] = addr.districts_found || [];

      // CASE 1: Gemini flagged ambiguous AND provided districts — honour directly
      if (addr.is_ambiguous && districtsFound.length > 0) {
        console.log(`🏙️ Gemini flagged multi-district ambiguity for "${streetName}" in ${city}: [${districtsFound.join(", ")}]`);
        parsed.status = "clarification_needed";
        if (!addr.alternatives || addr.alternatives.length === 0) {
          addr.alternatives = districtsFound.map(d => `${streetName}, ${d}, ${city}`);
        }
        parsed.clarification_message = parsed.clarification_message ||
          `There are several ${streetName}s in ${city}. Is it the one in ${districtsFound.slice(0, 3).join(", ")}?`;
        continue;
      }

      // CASE 2: Gemini resolved confidently BUT still returned districts_found with 2+ entries.
      // This means it "knew" the street is multi-district but picked one anyway — we must intervene
      // unless the caller gave enough discriminating info (postcode, high house number).
      if (!addr.is_ambiguous && districtsFound.length >= 2 && !hasPostcode && !isHighHouseNumber) {
        console.log(`⚠️ Safety net: "${streetName}" in ${city} resolved without ambiguity but Gemini found ${districtsFound.length} districts — forcing clarification`);
        addr.is_ambiguous = true;
        parsed.status = "clarification_needed";
        addr.alternatives = districtsFound.map(d => `${streetName}, ${d}, ${city}`);
        parsed.clarification_message = parsed.clarification_message ||
          `There are several ${streetName}s in ${city}. Is it the one in ${districtsFound.slice(0, 3).join(", ")}?`;
        continue;
      }

      // CASE 3: Gemini flagged ambiguous but gave no districts — DB fallback
      if (addr.is_ambiguous && districtsFound.length === 0 && !hasPostcode && !isHighHouseNumber) {
        const dbDistricts = await getCityDistricts(city);
        console.log(`🔍 DB fallback for "${streetName}" in ${city}: ${dbDistricts.length} districts available`);
        parsed.status = "clarification_needed";
        if (!addr.alternatives || addr.alternatives.length === 0) {
          addr.alternatives = dbDistricts.length > 0 
            ? dbDistricts.map(d => `${streetName}, ${d}, ${city}`)
            : [];
        }
        parsed.clarification_message = parsed.clarification_message ||
          `There are several ${streetName}s in ${city}. Which area or district is it in?`;
      }
    }

    // ── SPOKEN HOUSE NUMBER GUARD (post-resolution safety net) ──
    // Since the house number was passed as a geocoding filter, Gemini should have used it.
    // This guard catches remaining substitutions (e.g. caller said "43" but resolved "4B")
    // and forces the spoken number rather than flagging for clarification.
    for (const [side, spoken] of [["pickup", pickup_house_number], ["dropoff", destination_house_number]] as const) {
      if (!spoken?.trim()) continue;
      const addr = parsed[side as "pickup" | "dropoff"];
      if (!addr) continue;
      const resolvedNum: string = (addr.street_number || "").trim();

      if (resolvedNum && resolvedNum.toLowerCase() !== spoken.trim().toLowerCase()) {
        // Check for geocoder substitution: spoken="43" resolved="4B" (alphanumeric, different prefix)
        const isAlphanumeric = /^\d+[A-Za-z]$/.test(resolvedNum);
        const numericPrefix = resolvedNum.match(/^(\d+)/)?.[1] ?? "";
        const spokenDigits = spoken.trim().match(/^(\d+)/)?.[1] ?? "";

        if (isAlphanumeric && spokenDigits && spokenDigits !== numericPrefix) {
          // Force the spoken number — the caller is the authority
          console.log(`🏠 HOUSE NUMBER GUARD (${side}): caller said "${spoken}" but Gemini resolved "${resolvedNum}" — forcing spoken number`);
          addr.street_number = spoken.trim();
          const streetName = addr.street_name || "";
          const city = addr.city || "";
          const postal = addr.postal_code || "";
          addr.address = postal
            ? `${spoken.trim()} ${streetName}, ${city} ${postal}`
            : `${spoken.trim()} ${streetName}, ${city}`;
          (addr as any).house_number_mismatch = true;
          console.log(`🏠 GUARD: Forced address → ${addr.address}`);
        }
      } else if (!resolvedNum && spoken.trim()) {
        // Gemini returned no street number — inject the spoken one
        addr.street_number = spoken.trim();
        const streetName = addr.street_name || "";
        const city = addr.city || "";
        const postal = addr.postal_code || "";
        if (!addr.address?.startsWith(spoken.trim())) {
          addr.address = postal
            ? `${spoken.trim()} ${streetName}, ${city} ${postal}`
            : `${spoken.trim()} ${streetName}, ${city}`;
        }
        console.log(`🏠 GUARD: Injected missing house number "${spoken}" into ${side} address → ${addr.address}`);
      }
    }

    // ── Calculate fare from Gemini coordinates ──
    const pLat = parsed.pickup?.lat;
    const pLon = parsed.pickup?.lon;
    const dLat = parsed.dropoff?.lat;
    const dLon = parsed.dropoff?.lon;

    // Detect country for currency
    const detectedCountry = parsed.phone_analysis?.detected_country || "UK";

    if (typeof pLat === "number" && typeof pLon === "number" &&
        typeof dLat === "number" && typeof dLon === "number" &&
        pLat !== 0 && dLat !== 0) {
      const distMiles = haversineDistanceMiles(pLat, pLon, dLat, dLon);
      
      // ── LOCALITY SANITY CHECK: flag absurd distances ──
      if (distMiles > 200) {
        console.log(`🚨 ABSURD DISTANCE: ${distMiles.toFixed(1)} miles — almost certainly a resolution error`);
        // Determine which address is less certain and flag it
        const pickupHasHistory = parsed.pickup?.matched_from_history === true;
        const dropoffHasHistory = parsed.dropoff?.matched_from_history === true;
        const uncertainSide = pickupHasHistory ? "dropoff" : "pickup";
        
        parsed[uncertainSide].is_ambiguous = true;
        parsed.status = "clarification_needed";
        parsed.distance_warning = `Pickup and dropoff are ${Math.round(distMiles)} miles apart — this seems too far for a taxi. Please confirm the ${uncertainSide} address.`;
        parsed.clarification_message = parsed.clarification_message || 
          `The ${uncertainSide} address "${parsed[uncertainSide].address}" seems very far from the ${uncertainSide === "pickup" ? "dropoff" : "pickup"}. Could you confirm that's correct, or is it in a different area?`;
        parsed.fare = null;
        console.log(`⚠️ Flagged ${uncertainSide} as ambiguous due to absurd distance`);
      } else if (distMiles > 100) {
        console.log(`⚠️ LONG DISTANCE: ${distMiles.toFixed(1)} miles — adding warning`);
        const fareCalc = calculateFare(distMiles, detectedCountry);
        parsed.fare = fareCalc;
        parsed.distance_warning = `Pickup and dropoff are ${Math.round(distMiles)} miles apart — please confirm this is correct.`;
        console.log(`💰 Fare calculated with warning: ${fareCalc.fare} (${fareCalc.distance_miles} miles)`);
      } else {
        const fareCalc = calculateFare(distMiles, detectedCountry);
        parsed.fare = fareCalc;
        console.log(`💰 Fare calculated: ${fareCalc.fare} (${fareCalc.distance_miles} miles, ETA ${fareCalc.eta})`);
      }
    } else {
      console.log(`⚠️ No valid coordinates for fare calculation`);
      parsed.fare = null;
    }

    // ── DUPLICATE POSTCODE CHECK: different streets should almost never share a postcode ──
    const pickupPostal = (parsed.pickup?.postal_code || "").trim().toUpperCase();
    const dropoffPostal = (parsed.dropoff?.postal_code || "").trim().toUpperCase();
    const pickupStreet = (parsed.pickup?.street_name || "").toLowerCase();
    const dropoffStreet = (parsed.dropoff?.street_name || "").toLowerCase();
    
    if (pickupPostal && dropoffPostal && pickupPostal === dropoffPostal && 
        pickupStreet && dropoffStreet && pickupStreet !== dropoffStreet) {
      console.log(`⚠️ DUPLICATE POSTCODE: pickup "${pickupStreet}" and dropoff "${dropoffStreet}" both have "${pickupPostal}" — clearing dropoff postcode (likely Gemini copy error)`);
      parsed.dropoff.postal_code = "";
      // Also strip the postcode from the dropoff address string if present
      if (parsed.dropoff.address) {
        parsed.dropoff.address = parsed.dropoff.address.replace(new RegExp(pickupPostal.replace(/\s+/g, '\\s*'), 'gi'), '').replace(/,\s*,/g, ',').replace(/,\s*$/, '').trim();
      }
    }

    // ── CROSS-COUNTRY CHECK: different countries is almost always wrong ──
    const pickupCity = (parsed.pickup?.city || "").toLowerCase();
    const dropoffCity = (parsed.dropoff?.city || "").toLowerCase();
    const ukCities = ["london", "birmingham", "coventry", "manchester", "leeds", "sheffield", "nottingham", "leicester", "bristol", "reading"];
    const nlCities = ["amsterdam", "rotterdam", "the hague", "utrecht", "eindhoven"];
    const pickupIsUK = ukCities.some(c => pickupCity.includes(c)) || (typeof pLat === "number" && pLat >= 49.5 && pLat <= 61);
    const dropoffIsUK = ukCities.some(c => dropoffCity.includes(c)) || (typeof dLat === "number" && dLat >= 49.5 && dLat <= 61);
    const pickupIsNL = nlCities.some(c => pickupCity.includes(c)) || (typeof pLat === "number" && pLat >= 51 && pLat <= 53.5 && typeof pLon === "number" && pLon >= 3 && pLon <= 7.5);
    const dropoffIsNL = nlCities.some(c => dropoffCity.includes(c)) || (typeof dLat === "number" && dLat >= 51 && dLat <= 53.5 && typeof dLon === "number" && dLon >= 3 && dLon <= 7.5);
    
    if ((pickupIsUK && dropoffIsNL) || (pickupIsNL && dropoffIsUK)) {
      console.log(`🚨 CROSS-COUNTRY: pickup in ${pickupIsUK ? "UK" : "NL"}, dropoff in ${dropoffIsUK ? "UK" : "NL"} — flagging`);
      parsed.status = "clarification_needed";
      parsed.distance_warning = "Pickup and dropoff appear to be in different countries.";
      parsed.clarification_message = parsed.clarification_message ||
        `It looks like one address is in the UK and the other is in the Netherlands. Could you confirm both addresses are correct?`;
    }

    // ── STREET EXISTENCE CHECK: verify resolved streets exist in our DB ──
    // If Gemini hallucinated a street (e.g., "Rossville Street, Coventry" when it doesn't exist),
    // use fuzzy matching to find the real local street the caller likely meant
    if (supabaseUrl2 && supabaseKey2) {
      
      for (const side of ["pickup", "dropoff"] as const) {
        const addr = parsed[side];
        if (!addr?.street_name || !addr?.city) continue;
        if (addr.matched_from_history) continue;
        
        const originalInput = side === "pickup" ? (pickup || "") : (destination || "");
        const streetName = addr.street_name;
        const city = addr.city;
        
        try {
          const { data: fuzzyMatches } = await supabase2.rpc("fuzzy_match_street", {
            p_street_name: streetName,
            p_city: city,
            p_limit: 5
          });
          
          if (fuzzyMatches && fuzzyMatches.length > 0) {
            const exactMatch = fuzzyMatches.find((m: any) => 
              m.matched_name.toLowerCase().replace(/\s+/g, " ").trim() === streetName.toLowerCase().replace(/\s+/g, " ").trim()
            );
            
            if (exactMatch) {
              console.log(`✅ DB VERIFY: "${streetName}" exists in ${city}`);
            } else {
              const bestMatch = fuzzyMatches[0];
              console.log(`⚠️ DB VERIFY: "${streetName}" NOT found in ${city}. Best match: "${bestMatch.matched_name}" (${(bestMatch.similarity_score * 100).toFixed(0)}%)`);
              
              if (bestMatch.similarity_score > 0.35 && bestMatch.lat && bestMatch.lon) {
                const houseNumMatch = originalInput.match(/^(\d+[A-Za-z]?)\s*,?\s*/);
                const houseNum = houseNumMatch ? houseNumMatch[1] + " " : (addr.street_number ? addr.street_number + " " : "");
                
                console.log(`🔄 DB AUTO-CORRECT: "${streetName}" → "${bestMatch.matched_name}" in ${city} (${(bestMatch.similarity_score * 100).toFixed(0)}% similarity)`);
                addr.address = `${houseNum}${bestMatch.matched_name}, ${city}`;
                addr.street_name = bestMatch.matched_name;
                addr.lat = bestMatch.lat;
                addr.lon = bestMatch.lon;
                addr.is_ambiguous = false;
                addr.alternatives = [];
                parsed.sanity_corrected = parsed.sanity_corrected || {};
                parsed.sanity_corrected[side] = {
                  original_street: streetName,
                  corrected_to: bestMatch.matched_name,
                  similarity: bestMatch.similarity_score,
                  source: "db_fuzzy_match"
                };
              } else if (fuzzyMatches.length > 1) {
                console.log(`⚠️ DB: offering ${fuzzyMatches.length} alternatives for "${streetName}" in ${city}`);
                addr.is_ambiguous = true;
                addr.alternatives = fuzzyMatches
                  .filter((m: any) => m.similarity_score > 0.2)
                  .slice(0, 3)
                  .map((m: any) => `${m.matched_name}, ${m.matched_city || city}`);
                parsed.status = "clarification_needed";
                parsed.clarification_message = `I couldn't find "${streetName}" in ${city}. Did you mean: ${addr.alternatives.join(", or ")}?`;
              }
            }
          } else {
            console.log(`ℹ️ DB VERIFY: no entries for "${streetName}" in ${city} (DB may not cover this area)`);
          }
        } catch (dbErr) {
          console.warn(`⚠️ Street existence check error (non-fatal):`, dbErr);
        }
      }
      
      // Recalculate fare if addresses were auto-corrected
      if (parsed.sanity_corrected) {
        const cPLat = parsed.pickup?.lat;
        const cPLon = parsed.pickup?.lon;
        const cDLat = parsed.dropoff?.lat;
        const cDLon = parsed.dropoff?.lon;
        if (typeof cPLat === "number" && typeof cPLon === "number" && typeof cDLat === "number" && typeof cDLon === "number" && cPLat !== 0 && cDLat !== 0) {
          const correctedDist = haversineDistanceMiles(cPLat, cPLon, cDLat, cDLon);
          if (correctedDist < 100) {
            parsed.fare = calculateFare(correctedDist, detectedCountry);
            if (!parsed.pickup?.is_ambiguous && !parsed.dropoff?.is_ambiguous) {
              parsed.status = "ready";
            }
            console.log(`💰 Fare recalculated after DB correction: ${parsed.fare.fare} (${correctedDist.toFixed(1)} miles)`);
          }
        }
      }
    }

    // ── ADDRESS SANITY GUARD: second-pass Gemini check for impractical results ──
    // Triggers when: clarification_needed with no alternatives, OR distance > 50 miles (after DB check)
    const distMilesPost = (typeof parsed.pickup?.lat === "number" && typeof parsed.pickup?.lon === "number" && typeof parsed.dropoff?.lat === "number" && typeof parsed.dropoff?.lon === "number" && parsed.pickup.lat !== 0 && parsed.dropoff.lat !== 0)
      ? haversineDistanceMiles(parsed.pickup.lat, parsed.pickup.lon, parsed.dropoff.lat, parsed.dropoff.lon) : null;
    
    // Check if STT input street names differ from geocoded results (catches hallucinated local streets)
    const pickupInputStreet = extractStreetKey(pickup || "").toLowerCase();
    const dropoffInputStreet = extractStreetKey(destination || "").toLowerCase();
    const pickupResolvedStreet = (parsed.pickup?.street_name || "").toLowerCase();
    const dropoffResolvedStreet = (parsed.dropoff?.street_name || "").toLowerCase();
    const hasStreetNameMismatch = (
      (dropoffInputStreet && dropoffResolvedStreet && !dropoffResolvedStreet.includes(dropoffInputStreet) && !dropoffInputStreet.includes(dropoffResolvedStreet)) ||
      (pickupInputStreet && pickupResolvedStreet && !pickupResolvedStreet.includes(pickupInputStreet) && !pickupInputStreet.includes(pickupResolvedStreet))
    );
    
    // Skip sanity guard if destination is a city-level location (no street, just a city name)
    // Cities like "Manchester" are unambiguous and don't need a second Gemini verification pass
    const dropoffIsCityLevel = parsed.dropoff?.street_name && !parsed.dropoff?.street_number &&
      parsed.dropoff.street_name.toLowerCase() === (destination || "").trim().toLowerCase();
    
    // Skip sanity guard if BOTH sides were matched from caller history — the caller has used
    // these addresses before, so even if STT garbled the name (Dabridge→David), the match is trusted.
    const bothMatchedFromHistory = parsed.pickup?.matched_from_history && parsed.dropoff?.matched_from_history;
    if (bothMatchedFromHistory) {
      console.log(`✅ Both addresses matched from caller history — skipping sanity guard, clearing disambiguation`);
      if (parsed.pickup) { parsed.pickup.is_ambiguous = false; parsed.pickup.alternatives = []; }
      if (parsed.dropoff) { parsed.dropoff.is_ambiguous = false; parsed.dropoff.alternatives = []; }
      parsed.status = "ready";
      parsed.clarification_message = undefined;
      // Ensure fare is present
      if (!parsed.fare && distMilesPost !== null && distMilesPost < 200) {
        parsed.fare = calculateFare(distMilesPost, detectedCountry);
        console.log(`💰 Fare calculated (history-trusted): ${parsed.fare.fare}`);
      }
    }
    
    const needsSanityCheck = !bothMatchedFromHistory && !dropoffIsCityLevel && (
      (parsed.status === "clarification_needed" && 
      (!parsed.pickup?.alternatives?.length && !parsed.dropoff?.alternatives?.length))
      || (distMilesPost !== null && distMilesPost > 50)
      || hasStreetNameMismatch
    );

    if (needsSanityCheck && LOVABLE_API_KEY) {
      console.log(`🛡️ SANITY GUARD: triggering (dist=${distMilesPost?.toFixed(1) || 'N/A'} miles, status=${parsed.status})`);
      
      try {
        const contextCity = parsed.detected_area || parsed.pickup?.city || "";
        const sanityUserMsg = `Context City: ${contextCity}
Pickup STT Input: "${pickup || ''}"
Pickup Geocoder Result: "${parsed.pickup?.address || ''}" (street: "${parsed.pickup?.street_name || ''}", city: ${parsed.pickup?.city || 'unknown'})
Dropoff STT Input: "${destination || ''}"  
Dropoff Geocoder Result: "${parsed.dropoff?.address || ''}" (street: "${parsed.dropoff?.street_name || ''}", city: ${parsed.dropoff?.city || 'unknown'})
Distance: ${distMilesPost?.toFixed(1) || 'unknown'} miles
Key question: Does the dropoff street name "${parsed.dropoff?.street_name || ''}" match what the user said "${destination || ''}"? Are they the SAME street or DIFFERENT streets?`;

        const sanityResponse = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
          method: "POST",
          headers: {
            Authorization: `Bearer ${LOVABLE_API_KEY}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            model: "google/gemini-2.5-flash",
            messages: [
              { role: "system", content: `You are a "Taxi Address Sanity Guard." You compare User Input (STT) vs. Geocoder Results (GEO).

CRITICAL: Compare the STREET NAME the user said vs the STREET NAME the geocoder returned. They may be in the same city but be DIFFERENT streets.

Evaluation Criteria:
1. Street Name Phonetic Match: Compare the street names character by character. "Russell" vs "Rossville" — these are DIFFERENT streets even though they share "R-s". "David" vs "Davies" — DIFFERENT streets. "528" vs "52A" — same address, just number confusion.
2. Regional Bias: The user is in ${contextCity || 'an unknown city'}. The taxi may be travelling to a DIFFERENT city — this is a valid trip. Only flag a MISMATCH if the geocoder resolved to a city that is MORE THAN 50 miles away from the context city AND the user did not explicitly name that distant city. Nearby cities (e.g. Coventry → Birmingham, ~20 miles) are NORMAL taxi trips and must NOT be flagged as mismatches.
3. STT Artifacts: "NUX" might be "MAX", "Threw up" might be "72". But "Rossville" is NOT a mishearing of "Russell" — they are genuinely different street names.
4. Fabrication Detection: If the geocoder returned a street name that is phonetically similar but NOT identical to the input, AND you're not confident that street actually exists in that city, mark as MISMATCH. The geocoder may have fabricated a local version of a distant street.

Decision Rules:
- If the street name's core identity changed (Russell→Rossville, David→Davies): MISMATCH. Suggest what the user likely meant.
- If only the house number changed slightly (528→52A, letter/digit confusion): MATCH
- If the city changed to a DISTANT city (>50 miles): MISMATCH. Nearby inter-city trips within 50 miles are VALID.
- IMPORTANT: Two streets in the SAME city can still be a MISMATCH if the street NAMES are different.
- LANDMARK/POI RESOLUTION: If the user said a landmark or POI (e.g., "Train Station", "Hospital", "Airport", "University", "Bus Station", "Shopping Centre") and the geocoder returned the STREET where that landmark is located (e.g., "Station Square" for a train station, "Hospital Lane" for a hospital), this is a MATCH — the geocoder correctly resolved the landmark to its physical address. Do NOT flag this as a mismatch.

Evaluate BOTH pickup and dropoff independently.` },
              { role: "user", content: sanityUserMsg },
            ],
            temperature: 0.0,
            tools: [{
              type: "function",
              function: {
                name: "address_sanity_verdict",
                description: "Return the sanity check verdict for the address pair",
                parameters: {
                  type: "object",
                  properties: {
                    pickup_phonetic_similarity: { type: "number", description: "Score 0-1 of how similar the pickup STT input sounds to the geocoder result street name" },
                    dropoff_phonetic_similarity: { type: "number", description: "Score 0-1 of how similar the dropoff STT input sounds to the geocoder result street name" },
                    geographic_leap: { type: "boolean", description: "True if either result jumped to a different region or country from the context city" },
                    reasoning: { type: "string", description: "Explanation of STT artifacts, phonetic analysis, and geographic context" },
                    verdict: { type: "string", enum: ["MATCH", "MISMATCH", "UNCERTAIN"], description: "Overall verdict" },
                    mismatch_side: { type: "string", enum: ["pickup", "dropoff", "both", "none"], description: "Which address has the mismatch" },
                    suggested_correction: { type: "string", description: "If MISMATCH, what the user likely meant (e.g., 'Russell Street' instead of 'Rossville Street')" }
                  },
                  required: ["pickup_phonetic_similarity", "dropoff_phonetic_similarity", "geographic_leap", "reasoning", "verdict", "mismatch_side"],
                  additionalProperties: false
                }
              }
            }],
            tool_choice: { type: "function", function: { name: "address_sanity_verdict" } },
          }),
        });

        if (sanityResponse.ok) {
          const sanityData = await sanityResponse.json();
          const sanityToolCall = sanityData.choices?.[0]?.message?.tool_calls?.[0];
          let sanityResult;
          if (sanityToolCall?.function?.arguments) {
            sanityResult = typeof sanityToolCall.function.arguments === "string"
              ? JSON.parse(sanityToolCall.function.arguments)
              : sanityToolCall.function.arguments;
          }

          if (sanityResult) {
            console.log(`🛡️ SANITY VERDICT: ${sanityResult.verdict} (side: ${sanityResult.mismatch_side}, reasoning: ${sanityResult.reasoning})`);

            // ── MATCH verdict: sanity guard confirmed addresses are correct — clear disambiguation ──
            if (sanityResult.verdict === "MATCH") {
              console.log(`✅ SANITY MATCH: clearing disambiguation — addresses verified as correct`);
              if (parsed.pickup) { parsed.pickup.is_ambiguous = false; parsed.pickup.alternatives = []; }
              if (parsed.dropoff) { parsed.dropoff.is_ambiguous = false; parsed.dropoff.alternatives = []; }
              parsed.status = "ready";
              parsed.clarification_message = undefined;
              
              // ── POI NAME PRESERVATION ──
              // When a landmark/POI resolves to an underlying street (e.g., "Cathedral Lanes" → "Broadgate"),
              // prepend the original POI name so the C# bridge's discrepancy detector won't keep flagging it.
              const preservePoiName = (side: string, originalInput: string) => {
                const sideData = side === "pickup" ? parsed.pickup : parsed.dropoff;
                if (!sideData || !originalInput) return;
                const resolvedAddr = sideData.address || "";
                const streetName = sideData.street_name || "";
                const hasHouseNumber = /^\d+[A-Za-z]?\s/.test(originalInput.trim());
                if (hasHouseNumber) return;
                
                // Check if the user's POI name appears in the street_name (what the C# bridge checks)
                const cleanedInput = originalInput.replace(/\s*(?:in|,)\s*(coventry|birmingham|london|manchester|derby|leicester)\s*$/i, '').trim();
                const inputWords = cleanedInput.toLowerCase().replace(/[^a-z\s]/g, '').trim().split(/\s+/).filter((w: string) => w.length > 2);
                if (inputWords.length === 0) return;
                
                const streetLower = streetName.toLowerCase();
                const inputInStreetName = inputWords.some((w: string) => streetLower.includes(w));
                
                // If the POI name isn't in the street_name, prepend it to the address
                // so the C# bridge's word-level discrepancy detector won't flag it
                if (!inputInStreetName) {
                  // Also check if already prepended to avoid double-prepend
                  if (!resolvedAddr.toLowerCase().startsWith(cleanedInput.toLowerCase())) {
                    sideData.address = `${cleanedInput}, ${resolvedAddr}`;
                  }
                  // Also set street_name to include the POI name for the bridge
                  sideData.street_name = cleanedInput;
                  console.log(`📍 POI preserved: street_name="${cleanedInput}", address="${sideData.address}"`);
                }
              };
              
              preservePoiName("pickup", pickup || "");
              preservePoiName("dropoff", destination || "");
              
              // Ensure fare is present
              if (!parsed.fare && distMilesPost !== null && distMilesPost < 200) {
                parsed.fare = calculateFare(distMilesPost, detectedCountry);
                console.log(`💰 Fare calculated after sanity MATCH: ${parsed.fare.fare}`);
              }
            } else if (sanityResult.verdict === "MISMATCH" && sanityResult.mismatch_side !== "none") {
              const sides = sanityResult.mismatch_side === "both" ? ["pickup", "dropoff"] : [sanityResult.mismatch_side];
              
              for (const side of sides) {
                const originalInput = side === "pickup" ? (pickup || "") : (destination || "");
                const suggestedName = sanityResult.suggested_correction || originalInput;
                const searchCity = contextCity || parsed.pickup?.city || "";
                
                // Query database for fuzzy street matches
                if (searchCity) {
                  try {
                    const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
                    const supabaseKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
                    const supabase = createClient(supabaseUrl, supabaseKey);
                    
                    const { data: fuzzyMatches } = await supabase.rpc("fuzzy_match_street", {
                      p_street_name: suggestedName,
                      p_city: searchCity,
                      p_limit: 5
                    });

                    if (fuzzyMatches && fuzzyMatches.length > 0) {
                      console.log(`🔍 FUZZY MATCHES for "${suggestedName}" in ${searchCity}:`, fuzzyMatches.map((m: any) => `${m.matched_name} (${(m.similarity_score * 100).toFixed(0)}%)`));
                      
                      const bestMatch = fuzzyMatches[0];
                      if (bestMatch.similarity_score > 0.4 && bestMatch.lat && bestMatch.lon) {
                        // Auto-correct with high-confidence local match
                        console.log(`✅ SANITY AUTO-CORRECT: "${originalInput}" → "${bestMatch.matched_name}" in ${searchCity} (${(bestMatch.similarity_score * 100).toFixed(0)}% match)`);
                        
                        // Extract house number from original input
                        const houseNumMatch = originalInput.match(/^(\d+[A-Za-z]?)\s*,?\s*/);
                        const houseNum = houseNumMatch ? houseNumMatch[1] + " " : "";
                        
                        parsed[side].address = `${houseNum}${bestMatch.matched_name}, ${searchCity}`;
                        parsed[side].street_name = bestMatch.matched_name;
                        parsed[side].city = searchCity;
                        parsed[side].lat = bestMatch.lat;
                        parsed[side].lon = bestMatch.lon;
                        parsed[side].is_ambiguous = false;
                        parsed[side].alternatives = [];
                        parsed.sanity_corrected = parsed.sanity_corrected || {};
                        parsed.sanity_corrected[side] = {
                          original: originalInput,
                          corrected_to: bestMatch.matched_name,
                          similarity: bestMatch.similarity_score,
                          reasoning: sanityResult.reasoning
                        };
                      } else {
                        // Low confidence — provide as alternatives
                        console.log(`⚠️ SANITY: fuzzy matches too weak, providing as alternatives`);
                        parsed[side].is_ambiguous = true;
                        parsed[side].alternatives = fuzzyMatches
                          .filter((m: any) => m.similarity_score > 0.25)
                          .map((m: any) => `${m.matched_name}, ${m.matched_city || searchCity}`);
                        parsed.status = "clarification_needed";
                        parsed.clarification_message = `I couldn't verify "${originalInput}" in ${searchCity}. Did you mean: ${parsed[side].alternatives.join(", or ")}?`;
                      }
                    } else {
                      // No DB matches — flag with sanity reasoning
                      console.log(`⚠️ SANITY: no fuzzy matches found for "${suggestedName}" in ${searchCity}`);
                      parsed[side].is_ambiguous = true;
                      parsed.status = "clarification_needed";
                      parsed.clarification_message = `The ${side} address "${originalInput}" doesn't seem right for ${searchCity}. ${sanityResult.suggested_correction ? `Did you mean "${sanityResult.suggested_correction}"?` : "Could you repeat the street name?"}`;
                    }
                  } catch (dbErr) {
                    console.warn(`⚠️ Fuzzy match DB error (non-fatal):`, dbErr);
                  }
                }
              }
              
              // Recalculate fare if addresses were auto-corrected
              if (parsed.sanity_corrected) {
                const newPLat = parsed.pickup?.lat;
                const newPLon = parsed.pickup?.lon;
                const newDLat = parsed.dropoff?.lat;
                const newDLon = parsed.dropoff?.lon;
                if (typeof newPLat === "number" && typeof newPLon === "number" && typeof newDLat === "number" && typeof newDLon === "number" && newPLat !== 0 && newDLat !== 0) {
                  const newDist = haversineDistanceMiles(newPLat, newPLon, newDLat, newDLon);
                  if (newDist < 100) {
                    parsed.fare = calculateFare(newDist, detectedCountry);
                    parsed.status = parsed.pickup?.is_ambiguous || parsed.dropoff?.is_ambiguous ? "clarification_needed" : "ready";
                    console.log(`💰 Fare recalculated after sanity correction: ${parsed.fare.fare} (${newDist.toFixed(1)} miles)`);
                  }
                }
              }
            }
          }
        } else {
          console.warn(`⚠️ Sanity guard API error: ${sanityResponse.status}`);
        }
      } catch (sanityErr) {
        console.warn(`⚠️ Sanity guard error (non-fatal):`, sanityErr);
      }
    }

    // ── Zone lookup: find which company zone the pickup falls in ──
    const pickupLat = parsed.pickup?.lat;
    const pickupLon = parsed.pickup?.lon;
    if (typeof pickupLat === "number" && typeof pickupLon === "number" && pickupLat !== 0) {
      try {
        const zoneSupabaseUrl = Deno.env.get("SUPABASE_URL")!;
        const zoneSupabaseKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
        const zoneClient = createClient(zoneSupabaseUrl, zoneSupabaseKey);
        const { data: zoneHits } = await zoneClient.rpc("find_zone_for_point", {
          p_lat: pickupLat,
          p_lng: pickupLon,
        });
        if (zoneHits && zoneHits.length > 0) {
          const bestZone = zoneHits[0]; // highest priority
          parsed.matched_zone = {
            zone_id: bestZone.zone_id,
            zone_name: bestZone.zone_name,
            company_id: bestZone.company_id,
            priority: bestZone.priority,
          };
          console.log(`🗺️ Zone match: ${bestZone.zone_name} → company ${bestZone.company_id}`);
        } else {
          console.log(`🗺️ No zone match for pickup (${pickupLat}, ${pickupLon})`);
        }
      } catch (zoneErr) {
        console.warn(`⚠️ Zone lookup error (non-fatal):`, zoneErr);
      }
    }

    console.log(`✅ Address dispatch result: area=${parsed.detected_area}, status=${parsed.status}, fare=${parsed.fare?.fare || 'N/A'}, zone=${parsed.matched_zone?.zone_name || 'none'}`);

    return new Response(JSON.stringify(parsed), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("Address dispatch error:", error);
    return new Response(JSON.stringify({ 
      error: error instanceof Error ? error.message : "Unknown error",
      status: "error"
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});