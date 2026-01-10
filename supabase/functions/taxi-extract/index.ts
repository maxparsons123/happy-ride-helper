import "https://deno.land/x/xhr@0.1.0/mod.ts";
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");

// ============================================================
// FUZZY ADDRESS MATCHING UTILITIES
// ============================================================

/**
 * Normalize an address for comparison:
 * - lowercase
 * - remove extra whitespace
 * - standardize common abbreviations
 */
function normalizeAddress(addr: string): string {
  if (!addr) return "";
  return addr
    .toLowerCase()
    .replace(/\s+/g, " ")
    .trim()
    .replace(/\bstreet\b/g, "st")
    .replace(/\broad\b/g, "rd")
    .replace(/\bavenue\b/g, "ave")
    .replace(/\bdrive\b/g, "dr")
    .replace(/\blane\b/g, "ln")
    .replace(/\bcourt\b/g, "ct")
    .replace(/\bplace\b/g, "pl");
}

/**
 * Extract house number from an address (e.g., "52A", "1214", "18B")
 */
function extractHouseNumber(addr: string): string | null {
  const match = addr.match(/^(\d+[a-zA-Z]?)\s/);
  return match ? match[1].toUpperCase() : null;
}

/**
 * Extract street name from an address (without house number)
 */
function extractStreetName(addr: string): string {
  // Remove house number prefix
  return addr.replace(/^\d+[a-zA-Z]?\s+/, "").toLowerCase().trim();
}

/**
 * Calculate Levenshtein distance between two strings
 */
function levenshteinDistance(a: string, b: string): number {
  const matrix: number[][] = [];
  
  for (let i = 0; i <= b.length; i++) {
    matrix[i] = [i];
  }
  for (let j = 0; j <= a.length; j++) {
    matrix[0][j] = j;
  }
  
  for (let i = 1; i <= b.length; i++) {
    for (let j = 1; j <= a.length; j++) {
      if (b.charAt(i - 1) === a.charAt(j - 1)) {
        matrix[i][j] = matrix[i - 1][j - 1];
      } else {
        matrix[i][j] = Math.min(
          matrix[i - 1][j - 1] + 1, // substitution
          matrix[i][j - 1] + 1,     // insertion
          matrix[i - 1][j] + 1      // deletion
        );
      }
    }
  }
  
  return matrix[b.length][a.length];
}

/**
 * Generate fuzzy house number variants that could be misheard
 * Examples: "52A" could be heard as "5208", "520A", etc.
 */
function fuzzyHouseNumberVariants(houseNum: string): string[] {
  const variants: string[] = [houseNum.toUpperCase()];
  const upper = houseNum.toUpperCase();
  
  // Handle letter suffixes that could be misheard as numbers
  // "52A" might be heard as "528" or "5208"
  const letterMatch = upper.match(/^(\d+)([A-Z])$/);
  if (letterMatch) {
    const num = letterMatch[1];
    const letter = letterMatch[2];
    
    // A=8, B=3, C=3, D=3, E=3, etc. (phonetic confusion)
    const letterToDigit: Record<string, string[]> = {
      'A': ['8', '08'],
      'B': ['3', '03', '13'],
      'C': ['3', '03'],
      'D': ['3', '03'],
      'E': ['3', '03'],
      'F': ['4', '04'],
      'G': ['3', '03'],
    };
    
    if (letterToDigit[letter]) {
      for (const digit of letterToDigit[letter]) {
        variants.push(num + digit);
      }
    }
  }
  
  // Handle numbers that could be letters
  // "5208" might be "52-oh-8" or "52A" (0 sounds like "oh")
  const numMatch = upper.match(/^(\d+)(0)(\d*)$/);
  if (numMatch) {
    // "520" -> "52O" (letter O)
    variants.push(numMatch[1] + 'O' + numMatch[3]);
  }
  
  // Handle "08" suffix as "A" - "5208" -> "52A"
  const suffixMatch = upper.match(/^(\d+)(08|8)$/);
  if (suffixMatch) {
    variants.push(suffixMatch[1] + 'A');
  }
  
  return [...new Set(variants)];
}

interface FuzzyMatchResult {
  isExactMatch: boolean;
  isFuzzyMatch: boolean;
  matchedAddress: string | null;
  extractedAddress: string;
  needsClarification: boolean;
  clarificationReason: string | null;
  confidence: number;
}

/**
 * Check if an extracted address fuzzy-matches any address in caller history
 */
function checkFuzzyMatch(
  extractedAddr: string,
  addressHistory: string[]
): FuzzyMatchResult {
  if (!extractedAddr || !addressHistory || addressHistory.length === 0) {
    return {
      isExactMatch: false,
      isFuzzyMatch: false,
      matchedAddress: null,
      extractedAddress: extractedAddr,
      needsClarification: false,
      clarificationReason: null,
      confidence: 1.0,
    };
  }

  const normalizedExtracted = normalizeAddress(extractedAddr);
  const extractedHouseNum = extractHouseNumber(extractedAddr);
  const extractedStreet = extractStreetName(extractedAddr);

  for (const historyAddr of addressHistory) {
    const normalizedHistory = normalizeAddress(historyAddr);
    
    // Check for exact match
    if (normalizedExtracted === normalizedHistory) {
      return {
        isExactMatch: true,
        isFuzzyMatch: false,
        matchedAddress: historyAddr,
        extractedAddress: extractedAddr,
        needsClarification: false,
        clarificationReason: null,
        confidence: 1.0,
      };
    }

    // Check for fuzzy house number match on same street
    const historyHouseNum = extractHouseNumber(historyAddr);
    const historyStreet = extractStreetName(historyAddr);
    
    // Same street name (or very close)
    const streetDistance = levenshteinDistance(extractedStreet, historyStreet);
    const streetSimilar = streetDistance <= 2 || 
      extractedStreet.includes(historyStreet) || 
      historyStreet.includes(extractedStreet);

    if (streetSimilar && extractedHouseNum && historyHouseNum) {
      // Check if house numbers are fuzzy variants of each other
      const extractedVariants = fuzzyHouseNumberVariants(extractedHouseNum);
      const historyVariants = fuzzyHouseNumberVariants(historyHouseNum);
      
      // Check if any variants match
      const variantMatch = extractedVariants.some(v => historyVariants.includes(v)) ||
        historyVariants.some(v => extractedVariants.includes(v));
      
      if (variantMatch && extractedHouseNum !== historyHouseNum) {
        // Fuzzy match found - needs clarification
        return {
          isExactMatch: false,
          isFuzzyMatch: true,
          matchedAddress: historyAddr,
          extractedAddress: extractedAddr,
          needsClarification: true,
          clarificationReason: `Possible confusion between "${extractedHouseNum}" and "${historyHouseNum}" on ${historyStreet}`,
          confidence: 0.6,
        };
      }
      
      // Check Levenshtein distance between house numbers
      const houseNumDistance = levenshteinDistance(
        extractedHouseNum.toLowerCase(), 
        historyHouseNum.toLowerCase()
      );
      
      if (houseNumDistance === 1) {
        // Very close house numbers on same street - likely typo or mishearing
        return {
          isExactMatch: false,
          isFuzzyMatch: true,
          matchedAddress: historyAddr,
          extractedAddress: extractedAddr,
          needsClarification: true,
          clarificationReason: `House number "${extractedHouseNum}" is very close to known address "${historyHouseNum}" on same street`,
          confidence: 0.7,
        };
      }
    }
  }

  // No match found
  return {
    isExactMatch: false,
    isFuzzyMatch: false,
    matchedAddress: null,
    extractedAddress: extractedAddr,
    needsClarification: false,
    clarificationReason: null,
    confidence: 1.0,
  };
}

/**
 * Process extracted booking and check addresses against caller history
 */
function enrichWithFuzzyMatches(
  extracted: any,
  pickupHistory: string[],
  dropoffHistory: string[]
): any {
  const result = { ...extracted };
  
  // Check pickup location
  if (extracted.pickup_location && extracted.pickup_location !== "by_gps") {
    const pickupMatch = checkFuzzyMatch(extracted.pickup_location, pickupHistory);
    
    if (pickupMatch.isExactMatch) {
      // Use the stored address (better formatting)
      result.pickup_location = pickupMatch.matchedAddress;
      result.pickup_match_type = "exact";
      console.log(`[taxi-extract] Pickup exact match: "${extracted.pickup_location}" -> "${pickupMatch.matchedAddress}"`);
    } else if (pickupMatch.needsClarification) {
      // Flag for clarification
      result.pickup_needs_clarification = true;
      result.pickup_fuzzy_match = pickupMatch.matchedAddress;
      result.pickup_clarification_reason = pickupMatch.clarificationReason;
      result.pickup_match_type = "fuzzy";
      console.log(`[taxi-extract] Pickup fuzzy match needs clarification: "${extracted.pickup_location}" vs "${pickupMatch.matchedAddress}"`);
    }
  }
  
  // Check dropoff location
  if (extracted.dropoff_location && extracted.dropoff_location !== "as directed") {
    const dropoffMatch = checkFuzzyMatch(extracted.dropoff_location, dropoffHistory);
    
    if (dropoffMatch.isExactMatch) {
      // Use the stored address (better formatting)
      result.dropoff_location = dropoffMatch.matchedAddress;
      result.dropoff_match_type = "exact";
      console.log(`[taxi-extract] Dropoff exact match: "${extracted.dropoff_location}" -> "${dropoffMatch.matchedAddress}"`);
    } else if (dropoffMatch.needsClarification) {
      // Flag for clarification
      result.dropoff_needs_clarification = true;
      result.dropoff_fuzzy_match = dropoffMatch.matchedAddress;
      result.dropoff_clarification_reason = dropoffMatch.clarificationReason;
      result.dropoff_match_type = "fuzzy";
      console.log(`[taxi-extract] Dropoff fuzzy match needs clarification: "${extracted.dropoff_location}" vs "${dropoffMatch.matchedAddress}"`);
    }
  }
  
  return result;
}

// Get current London time in ISO format
const getLondonTime = () => {
  const now = new Date();
  return now.toLocaleString("en-GB", {
    timeZone: "Europe/London",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
};

// Get London datetime as ISO for calculations
const getLondonDateTime = () => {
  return new Date().toLocaleString("en-CA", {
    timeZone: "Europe/London",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).replace(", ", "T").replace(" ", "");
};

const NEW_BOOKING_PROMPT = (now: string) => `
You are a STRICT taxi booking AI handling NEW BOOKINGS ONLY.
Current time (London): ${now}

==================================================
GENERAL RULES (CRITICAL)
==================================================
• You MUST extract ONLY these fields:
  - pickup_location
  - dropoff_location
  - pickup_time
  - number_of_passengers
  - luggage
  - special_requests

• NEVER:
  - guess an address
  - infer missing information
  - add postcode, fix spelling, or normalise formatting
  - reuse ANY details from previous context

• Spoken house numbers should be preserved exactly (e.g., "52A", "1214A", "18B").

==================================================
GPS PICKUP RULE
==================================================
If user says "my location", "here", "where I am", "current location":
• set pickup_location = "by_gps"

If ANY place name, landmark, venue, or address is present:
• DO NOT use "by_gps"
• Return the EXACT text of the place instead

==================================================
ADDRESS RULES (STRICT)
==================================================
You MUST NOT:
• guess or infer any part of addresses
• correct spelling
• add postcode, remove postcode, or change punctuation
• normalise address formatting

You MUST return the EXACT text the user said.

LOCATION EXTRACTION:
• "from X" or "pick up from X" or "pick me up at X" or "collect me from X" → pickup_location = X
• "to Y" or "going to Y" or "heading to Y" or "take me to Y" → dropoff_location = Y
• If 'nearest' or 'closest' is mentioned, include in pickup_location
• If no drop-off given or "as directed" → dropoff_location = "as directed"

CRITICAL - "at X" CLARIFICATION RULE:
• If user ONLY says "at X" or "the X" without "from", "to", "going", etc.:
  - This is usually a CLARIFICATION of the PREVIOUS location mentioned, NOT a new pickup
  - If it sounds like a business name, venue, or landmark (e.g., "at Phonopolis", "the Hilton", "at Tesco"):
    - Return it as a SPECIAL REQUEST for clarification, NOT as pickup_location
    - special_requests = "Location clarification: at X"
  - Only treat "at X" as pickup if combined with pickup words like "pick me up at X", "from X"

LOCATION CLEANING RULE:
• If location begins with "the ", "a ", or "an ", remove ONLY that article
• The rest must remain EXACTLY as spoken

Examples:
"from the high street" → pickup_location = "high street"
"to the station" → dropoff_location = "station"
"from 52A David Road" → pickup_location = "52A David Road"

==================================================
PICKUP + DROPOFF RULE (CRITICAL)
==================================================
If the user message includes BOTH a pickup AND a dropoff,
you MUST return BOTH fields.

Patterns:
• "from X to Y"
• "pickup from X and drop at Y"
• "pick me up at X take me to Y"
• "I am at X going to Y"
• "collect from X, deliver to Y"

RULE:
• pickup_location = EXACT text of X
• dropoff_location = EXACT text of Y

You MUST return BOTH when both are mentioned.

==================================================
POSTCODE RULES
==================================================
User may say postcodes in ANY form:
cv12ew, CV12EW, b276hp, "B27 6HP"

You MUST:
• detect them  
• return EXACTLY what the user said  
• NEVER uppercase, lowercase, add/remove spaces, or normalise  

==================================================
TIME RULES
==================================================
REFERENCE TIME: ${now}

OUTPUT FORMAT: "YYYY-MM-DD HH:MM" (24-hour) or "ASAP"

If no valid time detected → pickup_time = null

BASIC MAP:
• "now", "asap", "straight away" → "ASAP"

If ONLY a time is given ("5pm", "half past 3"):
• Use TODAY at that time
• If that time has passed → use TOMORROW

NATURAL PHRASES:
• "tonight" → today 21:00
• "this evening" → today 19:00
• "afternoon" → today 15:00 (or tomorrow if passed)
• "morning" → today 09:00 (or tomorrow if passed)
• "tomorrow" → tomorrow same time as now
• "tomorrow at 5pm" → tomorrow 17:00

RELATIVE TIME:
• "in X minutes" → now + X minutes
• "in X hours" → now + X hours
• "in half an hour" → now + 30 minutes

DAY-OF-WEEK:
• "Wednesday" → nearest upcoming Wednesday
• "next Wednesday" → Wednesday of next week
• "Monday at 5pm" → next Monday at 17:00
• "Friday morning" → next Friday at 09:00

NO-PAST RULE:
If computed time < current time → move to next valid occurrence

==================================================
PASSENGER EXTRACTION
==================================================
Convert spoken numbers:
• "one" / "just me" / "myself" → 1
• "two" / "couple" / "two of us" → 2
• "three" / "three of us" → 3
• And so on...

==================================================
LUGGAGE RULES (STRONG)
==================================================
These words ALWAYS indicate luggage:
"luggage", "bags", "bag", "suitcase", "suitcases", "cases", 
"holdall", "backpack", "rucksack"

If user mentions luggage with a number:
• luggage = EXACT phrase (e.g., "2 luggage", "3 bags", "one suitcase")

If luggage mentioned without number:
• luggage = exact phrase (e.g., "I have luggage")

"remove luggage" or "no luggage" → luggage = "CLEAR"

IMPORTANT: Luggage phrases MUST NOT go into special_requests.

==================================================
INTERMEDIATE STOP / VIA RULES
==================================================
If user mentions:
• "stop at"
• "via"
• "then go to"
• "drop me at X then Y"
• "wait at X"

You MUST treat this as a VIA STOP.

Rules:
• The FIRST destination after pickup = via_stop (include in special_requests)
• The FINAL destination = dropoff_location
• Any duration (e.g., "10 minutes") MUST be included

Example:
"from home, stop at the shop for 5 minutes, then to the airport"
→ pickup_location = "home"
→ dropoff_location = "airport"
→ special_requests = "Stop at the shop for 5 minutes"

==================================================
SPECIAL REQUESTS (STRICT)
==================================================
ANY user text that:
• instructs the driver
• requests a specific driver
• mentions a driver number
• requests vehicle type
• gives behaviour instructions
• adds preferences or notes
• does NOT modify pickup, dropoff, time, passengers, or luggage

MUST go into special_requests EXACTLY as spoken.

Examples:
• "driver 314 please"
• "same driver again"
• "ring me when outside"
• "send a lady driver"
• "wheelchair access"
• "I have a dog"
• "call me on arrival"

==================================================
OUTPUT FORMAT (REQUIRED)
==================================================
Return ONLY valid JSON:

{
  "intent": "new_booking",
  "pickup_location": string | null,
  "dropoff_location": string | null,
  "pickup_time": string | null,
  "number_of_passengers": number | null,
  "luggage": string | null,
  "special_requests": string | null,
  "confidence": "high" | "medium" | "low"
}

NO extra text. NO explanations. ONLY valid JSON.
`;

const UPDATE_BOOKING_PROMPT = (now: string, existing: any) => `
You are a STRICT taxi booking AI handling UPDATES ONLY.
Current time (London): ${now}

==================================================
EXISTING BOOKING (REFERENCE ONLY — DO NOT COPY)
==================================================
Pickup: ${existing.pickup_location || "not set"}
Dropoff: ${existing.dropoff_location || "not set"}
Time: ${existing.pickup_time || "not set"}
Passengers: ${existing.number_of_passengers || "not set"}
Luggage: ${existing.luggage || "none"}
Special requests: ${existing.special_requests || "none"}

You MUST NOT reuse these values unless the user explicitly changes them.

==================================================
UPDATE RULES (CRITICAL)
==================================================
• Only return fields the user EXPLICITLY changes
• Any field NOT changed must be returned as null
• Preserve EXACT text the user says - no corrections
• "remove luggage" → luggage = "CLEAR"

==================================================
ADDRESS RULES (STRICT)
==================================================
You MUST NOT:
• guess or infer any part of addresses
• correct spelling
• add postcode, remove postcode, or change punctuation
• normalise address formatting

You MUST return the EXACT text the user said.

LOCATION CLEANING RULE:
• If location begins with "the ", "a ", or "an ", remove ONLY that article
• The rest must remain EXACTLY as spoken

==================================================
PICKUP + DROPOFF UPDATE RULE (CRITICAL)
==================================================
If the user message includes BOTH a new pickup AND a new dropoff,
you MUST return BOTH fields.

Patterns:
• "from X to Y"
• "pickup from X and drop at Y"
• "change pickup to X and dropoff to Y"
• "actually from X going to Y"

You MUST return BOTH fields whenever both are updated.

==================================================
POSTCODE RULE (STRICT)
==================================================
Return postcodes EXACTLY as spoken. NO editing.

==================================================
TIME UPDATE RULES
==================================================
If user specifies a time:
• Output: "YYYY-MM-DD HH:MM" (24-hour) or "ASAP"

If no time change mentioned:
• pickup_time = null

Rules:
• "now" / "asap" → "ASAP"
• "change to 5pm" → today at 17:00 (or tomorrow if passed)
• "make it tomorrow" → tomorrow at current time
• "in 30 minutes" → now + 30 minutes

==================================================
LUGGAGE UPDATE RULES
==================================================
• "add 2 bags" → luggage = "2 bags"
• "I have luggage now" → luggage = "luggage"
• "remove luggage" / "no luggage" → luggage = "CLEAR"

If no luggage change → luggage = null

==================================================
INTERMEDIATE STOP / VIA RULES
==================================================
If user adds a stop:
• "stop at X for 10 minutes" → special_requests = "Stop at X for 10 minutes"
• Update dropoff_location ONLY if final destination changes

==================================================
SPECIAL REQUESTS UPDATE
==================================================
Any new instructions for the driver:
• APPEND to special_requests or replace if user says "change to"

Examples:
• "also ring me when outside" → special_requests = "ring me when outside"
• "cancel the special request" → special_requests = "CLEAR"

==================================================
OUTPUT FORMAT (MANDATORY)
==================================================
Return ONLY valid JSON:

{
  "intent": "update_booking",
  "pickup_location": string | null,
  "dropoff_location": string | null,
  "pickup_time": string | null,
  "number_of_passengers": number | null,
  "luggage": string | null,
  "special_requests": string | null,
  "confidence": "high" | "medium" | "low"
}

Return null for ANY field that was NOT explicitly changed.
NO extra text. NO explanations. ONLY valid JSON.
`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { 
      transcript, 
      mode = "new", 
      existing_booking = null,
      pickup_history = [],   // Caller's pickup address history
      dropoff_history = []   // Caller's dropoff address history
    } = await req.json();
    
    if (!transcript) {
      return new Response(
        JSON.stringify({ error: "No transcript provided" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const now = getLondonTime();
    const systemPrompt = mode === "update" && existing_booking 
      ? UPDATE_BOOKING_PROMPT(now, existing_booking)
      : NEW_BOOKING_PROMPT(now);

    console.log(`[taxi-extract] Processing transcript: "${transcript}" (mode: ${mode})`);

    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash",
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: transcript }
        ],
        temperature: 0.1, // Low temperature for consistent extraction
      }),
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[taxi-extract] API error: ${errorText}`);
      throw new Error(`API error: ${response.status}`);
    }

    const data = await response.json();
    const content = data.choices[0]?.message?.content;
    
    if (!content) {
      throw new Error("No content in response");
    }

    // Parse JSON from response (handle markdown code blocks)
    let jsonStr = content;
    const jsonMatch = content.match(/```(?:json)?\s*([\s\S]*?)```/);
    if (jsonMatch) {
      jsonStr = jsonMatch[1].trim();
    }
    const rawJsonMatch = jsonStr.match(/\{[\s\S]*\}/);
    if (rawJsonMatch) {
      jsonStr = rawJsonMatch[0];
    }

    const extracted = JSON.parse(jsonStr);
    console.log(`[taxi-extract] Extracted:`, extracted);

    // Enrich with fuzzy matching against caller history
    const enriched = enrichWithFuzzyMatches(extracted, pickup_history, dropoff_history);
    console.log(`[taxi-extract] Enriched with fuzzy matching:`, enriched);

    return new Response(
      JSON.stringify(enriched),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );

  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    console.error("[taxi-extract] Error:", error);
    return new Response(
      JSON.stringify({ 
        error: errorMessage,
        intent: "new_booking",
        pickup_location: null,
        dropoff_location: null,
        pickup_time: null,
        number_of_passengers: null,
        luggage: null,
        special_requests: null,
        confidence: "low"
      }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
