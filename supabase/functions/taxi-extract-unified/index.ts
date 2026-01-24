import "https://deno.land/x/xhr@0.1.0/mod.ts";
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

/**
 * UNIFIED BOOKING EXTRACTION (v2 - Tool Calling)
 * 
 * Uses tool calling for reliable structured extraction (like the C# AiBooking function).
 * Supports:
 * - Alias resolution (home → 52a david road)
 * - New bookings AND modifications
 * - Comparison against Ada's interpretation
 */

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

// Build system prompt for NEW bookings
const buildNewBookingPrompt = (
  now: string, 
  callerName: string | null, 
  callerCity: string | null,
  aliases: Record<string, string> | null
) => {
  let aliasInstruction = "";
  if (aliases && Object.keys(aliases).length > 0) {
    const aliasJson = JSON.stringify(aliases);
    aliasInstruction = `
CALLER'S SAVED ALIASES:
${aliasJson}

ALIAS RULES:
• If pickup or dropoff matches an alias key, replace with the full address.
• Apply fuzzy matching (e.g., 'hme' → 'home', 'my place' → 'home').
• Output the full address, not the alias name.
`;
  }

  return `You are an expert taxi booking assistant extracting details from conversation.
Current time (London): ${now}
${callerName ? `Caller name: ${callerName} (NOTE: This is the CALLER'S NAME for greeting purposes ONLY - it is NOT an address. NEVER use this as pickup_location or dropoff_location.)` : ""}
${callerCity ? `Default city: ${callerCity}` : ""}
${aliasInstruction}

==================================================
EXTRACTION RULES (NEW BOOKING)
==================================================
1. **QUESTION-ANSWER FLOW (HIGHEST PRIORITY)**:
   - The transcript includes [CONTEXT: Ada asked: "..."] before each customer response
   - ALWAYS use this context to determine what field the customer is answering
   - When Ada asks "where would you like to be picked up?" → customer's response is PICKUP
   - When Ada asks "what is your destination?" or "where are you going?" → customer's response is DESTINATION
   - When Ada asks "how many passengers/people?" → customer's response is PASSENGERS
   - When Ada repeats a question (e.g., "Sorry, what is your destination again?") → this is a CORRECTION, update that field
   - The Q&A flow OVERRIDES keyword detection. If Ada asks for pickup and user says "18 Exmoor Road", that IS the pickup.

2. **CORRECTIONS (CRITICAL)**:
   - If a customer gives an address after Ada summarizes/confirms → check which field Ada is confirming
   - If customer says "no, it's actually X" or "sorry, I meant X" → UPDATE the relevant field to X
   - If customer corrects after "Is that correct?" → update the corrected field
   - LATEST customer response for a field ALWAYS wins over earlier responses

3. **Location Detection (Secondary - use when no Q&A context)**:
   - 'from', 'pick up from', 'collect from' → pickup_location
   - 'to', 'going to', 'heading to', 'take me to' → dropoff_location
   - 'my location', 'here', 'current location' → leave pickup_location EMPTY (agent will ask)
   - 'nearest X' or 'closest X' for PICKUP → set nearest_pickup = place type, leave pickup_location EMPTY
   - 'nearest X' or 'closest X' for DROPOFF → set nearest_dropoff = place type, leave dropoff_location EMPTY
   - If no destination given → leave dropoff_location EMPTY (agent will ask)

4. **Address Preservation - CRITICAL**:
   - Return EXACT text the user typed
   - DO NOT guess, correct spelling, add/remove postcodes, or change punctuation
   - Preserve house numbers with letters: "52A", "1214B", "7b"

5. **Time Handling**:
   - Convert to 'YYYY-MM-DD HH:MM' (24-hour)
   - 'now', 'asap' → 'ASAP'
   - 'in X minutes' → calculate from current time
   - Time in past → assume tomorrow

6. **Passengers (CRITICAL - DO NOT ASSUME)**:
   - ONLY set number_of_passengers if user EXPLICITLY states a number
   - "two passengers" → passengers = 2
   - If user has NOT mentioned passengers → number_of_passengers = null (NOT 1!)
   - DO NOT default to 1. Leave null if unknown.
   - Context matters: answer to "how many passengers?" → passengers

7. **Luggage**:
   - "two bags/suitcases" → luggage = "2 bags"

7. **Vehicle Types**:
   - saloon, estate, MPV, minibus, 6-seater, 8-seater
   - Only set if explicitly requested

8. **Special Requests**:
   - "ring when outside", "wheelchair access", "child seat", driver requests
   - Do NOT include phone numbers

`;
};

// Build system prompt for BOOKING UPDATES (comprehensive C#-style prompt)
// Returns COMPLETE booking - unchanged fields use existing values
const buildUpdateBookingPrompt = (
  now: string,
  existingBooking: ExistingBooking
) => {
  return `You are a STRICT taxi booking AI handling UPDATES.
Current time (London): ${now}

==================================================
EXISTING BOOKING (USE AS BASE - KEEP UNCHANGED FIELDS)
==================================================
Pickup: ${existingBooking.pickup || 'not set'}
Dropoff: ${existingBooking.destination || 'not set'}
Time: ${existingBooking.pickup_time || 'ASAP'}
Passengers: ${existingBooking.passengers ?? 'not set'}
Luggage: ${existingBooking.luggage || 'not specified'}
Special requests: ${existingBooking.special_requests || 'none'}

==================================================
OUTPUT RULES (CRITICAL - RETURN COMPLETE BOOKING)
==================================================
You MUST return a COMPLETE booking with ALL fields:
• If a field is CHANGED by the user → return the NEW value
• If a field is NOT changed → return the EXISTING value from above
• "remove luggage" → luggage = "CLEAR"

This means your output will ALWAYS have values for:
pickup_location, dropoff_location, pickup_time, number_of_passengers, luggage

You MUST ALWAYS reply in English, even if the input is in another language.
Try to put user message into structured format with good English.

==================================================
CHANGE DETECTION RULES (CRITICAL)
==================================================
⚠️ AMBIGUITY ALERT: "change from X to Y" has TWO meanings:
1. "change the route FROM X TO Y" → new pickup = X, new dropoff = Y
2. "change [field] from X to Y" → changing a field's value FROM X TO Y

DISAMBIGUATION:
• If the user says "change it from X to Y" or "from X to Y please" → This is a ROUTE change:
  - pickup_location = X (the origin)
  - dropoff_location = Y (the destination)
• If the user says "change the pickup from X to Y" → This changes the pickup field:
  - pickup_location = Y (the new value)
• If the user says "change destination from X to Y" → This changes the destination field:
  - dropoff_location = Y (the new value)

DEFAULT RULE: When "from X to Y" appears WITHOUT specifying a field to change, 
treat it as a ROUTE definition: pickup = X, dropoff = Y.

OTHER PATTERNS:
• User says "change pickup to X" → pickup_location = X, keep all other existing values
• User says "going to Y instead" → dropoff_location = Y, keep pickup and other values
• User says "add 2 passengers" → number_of_passengers = existing + 2
• User says "3 passengers" → number_of_passengers = 3

==================================================
ADDRESS RULES (STRICT)
==================================================
You MUST NOT:
• guess or infer any part of addresses
• correct spelling
• add postcode, remove postcode, or change punctuation
• normalise address formatting

1. **Location Extraction**:
   - Detect 'from' and 'to' (or variants in other languages).
   - If only one place + 'pick up' → set as pickup_location.
   - If 'nearest' or 'closest' is mentioned for PICKUP → set nearest_pickup = place type, leave pickup_location EMPTY.
   - If 'nearest' or 'closest' is mentioned for DROPOFF → set nearest_dropoff = place type, leave dropoff_location EMPTY.
   - If no drop-off given, leave dropoff_location EMPTY (agent will ask).
   - If user says 'my location', 'here', etc., leave pickup_location EMPTY (agent will ask for address).

You MUST return the EXACT text the user typed for any NEW addresses.

==================================================
PICKUP + DROPOFF UPDATE RULE (CRITICAL)
==================================================
If the user message includes BOTH a new pickup AND a new dropoff,
you MUST return BOTH new values.

Patterns:
• "from X to Y"
• "pickup from X and drop at Y"
• "pick me up at X take me to Y"
• "I am at X going to Y"
• "collect from X, deliver to Y"

RULE:
• pickup_location = EXACT text of X
• dropoff_location = EXACT text of Y

LOCATION CLEANING RULE:
• If location begins with "the ", "a ", or "an ", remove ONLY that article.
• If "going to" appears, it ALWAYS defines the dropoff_location.
• Rest of text must remain EXACTLY as typed.

==================================================
POSTCODE RULE (STRICT)
==================================================
User may type: cv12ew, CV12EW, b276hp, B27 6HP, "b303jh,1" etc.
You MUST return their postcode EXACTLY as typed. NO editing.

==================================================
TIME UPDATE RULES
==================================================
If the user specifies a NEW time, output pickup_time in:
  "YYYY-MM-DD HH:MM" (24-hour)
If time NOT changed → return existing time: "${existingBooking.pickup_time || 'ASAP'}"

Rules for NEW times:
• "now" / "asap" → "ASAP"
• Time alone ("5pm") → TODAY at 17:00, unless past → TOMORROW
• "in X minutes/hours" → add duration to current time
• "tonight" → 21:00, "this evening" → 19:00, "afternoon" → 15:00, "morning" → 09:00

==================================================
LUGGAGE EXTRACTION RULES (STRONG)
==================================================
Luggage keywords: "luggage", "bags", "bag", "suitcase", "suitcases", "cases", "holdall", "backpack", "rucksack"

If user mentions luggage → update it:
"can I add 2 luggage" → luggage = "2 luggage"
"add 3 bags please"   → luggage = "3 bags"
"I have one suitcase" → luggage = "one suitcase"
"remove luggage"      → luggage = "CLEAR"

If luggage NOT mentioned → keep existing: "${existingBooking.luggage || 'not specified'}"

IMPORTANT: Luggage ALWAYS has priority over special_requests.

==================================================
PASSENGERS RULES
==================================================
If user mentions passengers → update number_of_passengers
If NOT mentioned → keep existing: ${existingBooking.passengers ?? 'null'}

==================================================
INTERMEDIATE STOP / VIA RULES
==================================================
If user mentions: "stop at", "via", "then go to", "drop me at X then Y", "wait at X"

• The FIRST destination after pickup is the via_stop
• The FINAL destination is the dropoff_location
• Duration (e.g. "10 minutes") goes in special_requests

If via stop exists:
• special_requests MUST include: "Stop at <via location> for <duration>"

==================================================
SPECIAL REQUESTS (STRICT)
==================================================
Driver instructions, specific driver requests, vehicle preferences, notes that don't modify booking fields → go into special_requests.

Examples: "driver 314 please", "ring me when outside", "wheelchair access"

If no NEW special requests → keep existing: "${existingBooking.special_requests || ''}"
`;
};

// Build the appropriate system prompt based on whether it's a modification
const buildSystemPrompt = (
  now: string, 
  callerName: string | null, 
  callerCity: string | null,
  aliases: Record<string, string> | null,
  existingBooking: ExistingBooking | null,
  isModification: boolean
) => {
  if (isModification && existingBooking) {
    return buildUpdateBookingPrompt(now, existingBooking);
  }
  return buildNewBookingPrompt(now, callerName, callerCity, aliases);
};

// Tool definition for structured extraction
// For modifications: AI returns COMPLETE booking with all fields populated
const BOOKING_EXTRACTION_TOOL = {
  type: "function",
  function: {
    name: "extract_booking",
    description: "Extract taxi booking details. For updates: return COMPLETE booking with unchanged fields preserved from existing booking.",
    parameters: {
      type: "object",
      properties: {
        pickup_location: { 
          type: "string", 
          description: "Pickup address. For updates: return existing value if not changed by user." 
        },
        dropoff_location: { 
          type: "string", 
          description: "Destination address. For updates: return existing value if not changed." 
        },
        pickup_time: { 
          type: "string", 
          description: "Pickup time in 'YYYY-MM-DD HH:MM' format, or 'ASAP'. For updates: return existing if not changed." 
        },
        number_of_passengers: { 
          type: "integer", 
          description: "Number of passengers (1-8). For updates: return existing if not changed." 
        },
        luggage: { 
          type: "string", 
          description: "Luggage description. 'CLEAR' to remove. For updates: return existing if not changed." 
        },
        vehicle_type: { 
          type: "string", 
          enum: ["saloon", "estate", "mpv", "minibus", "8-seater"],
          description: "Vehicle type. Only set if explicitly requested or derived from passengers/luggage." 
        },
        special_requests: { 
          type: "string", 
          description: "Special requests (driver preferences, accessibility, etc.)" 
        },
        nearest_pickup: { 
          type: "string", 
          description: "If user asks for pickup from 'nearest' or 'closest' something (e.g., 'pick me up from the nearest tube station' → 'tube station')" 
        },
        nearest_dropoff: { 
          type: "string", 
          description: "If user asks to go to 'nearest' or 'closest' something (e.g., 'take me to the nearest hospital' → 'hospital')" 
        },
        fields_changed: {
          type: "array",
          items: { type: "string" },
          description: "List of fields that were CHANGED in this update (e.g., ['pickup', 'destination'])"
        },
        missing_fields: {
          type: "array",
          items: { type: "string" },
          description: "List of essential fields still needed"
        },
        confidence: {
          type: "string",
          enum: ["high", "medium", "low"],
          description: "Confidence in extraction accuracy"
        },
        extraction_notes: {
          type: "string",
          description: "Brief note about changes detected, ambiguity, or alias resolutions"
        }
      },
      required: ["pickup_location", "dropoff_location", "pickup_time", "number_of_passengers", "confidence"]
    }
  }
};

interface ExistingBooking {
  pickup?: string | null;
  destination?: string | null;
  passengers?: number | null;
  luggage?: string | null;
  vehicle_type?: string | null;
  pickup_time?: string | null;
  special_requests?: string | null;
}

interface ExtractionRequest {
  // The full conversation transcript (array of messages)
  conversation: Array<{
    role: "user" | "assistant" | "system";
    text: string;
    timestamp?: string;
  }>;
  // Current known booking state (for modifications)
  current_booking?: ExistingBooking;
  // Caller context
  caller_name?: string | null;
  caller_city?: string | null;
  caller_phone?: string | null;
  // Is this a travel hub trip? (airport/station - needs luggage)
  is_travel_hub_trip?: boolean;
  // Is this a modification request?
  is_modification?: boolean;
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const startTime = Date.now();

  try {
    const request: ExtractionRequest = await req.json();
    
    const { 
      conversation = [],
      current_booking = null,
      caller_name = null,
      caller_city = null,
      caller_phone = null,
      is_travel_hub_trip = false,
      is_modification = false
    } = request;
    
    if (!conversation || conversation.length === 0) {
      return new Response(
        JSON.stringify({ 
          error: "No conversation provided",
          pickup: null,
          destination: null,
          passengers: null,
          luggage: null,
          pickup_time: "ASAP",
          vehicle_type: null,
          special_requests: null,
          missing_fields: ["pickup", "destination", "passengers"],
          confidence: "low"
        }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Fetch user aliases from database if phone provided
    let aliases: Record<string, string> | null = null;
    if (caller_phone) {
      try {
        const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
        const { data: callerData } = await supabase
          .from("callers")
          .select("address_aliases")
          .eq("phone_number", caller_phone)
          .maybeSingle();
        
        if (callerData?.address_aliases) {
          // Handle both formats: {"home": "address"} or {"Aliases": {"home": "address"}}
          const rawAliases = callerData.address_aliases as Record<string, any>;
          if (rawAliases.Aliases) {
            aliases = rawAliases.Aliases;
          } else {
            aliases = rawAliases;
          }
          console.log(`[taxi-extract-unified] Loaded ${Object.keys(aliases || {}).length} aliases for ${caller_phone}`);
        }
      } catch (e) {
        console.error(`[taxi-extract-unified] Failed to load aliases:`, e);
      }
    }

    const now = getLondonTime();
    const systemPrompt = buildSystemPrompt(
      now, 
      caller_name, 
      caller_city, 
      aliases,
      is_modification ? current_booking : null,
      is_modification
    );

    // Format conversation with explicit Q&A pairing for better extraction
    // Each customer message is paired with the preceding Ada question
    let conversationText = "=== CONVERSATION TRANSCRIPT (Q&A PAIRS) ===\n";
    let lastAdaQuestion: string | null = null;
    
    for (let i = 0; i < conversation.length; i++) {
      const msg = conversation[i];
      const role = msg.role === "user" ? "CUSTOMER" : msg.role === "assistant" ? "ADA" : "SYSTEM";
      
      if (msg.role === "assistant") {
        // Track Ada's last question for context
        lastAdaQuestion = msg.text;
        conversationText += `ADA: ${msg.text}\n`;
      } else if (msg.role === "user") {
        // Include the preceding Ada question context for each customer response
        if (lastAdaQuestion) {
          conversationText += `[CONTEXT: Ada asked: "${lastAdaQuestion}"]\n`;
        }
        conversationText += `CUSTOMER RESPONSE: ${msg.text}\n`;
        conversationText += `---\n`;
      } else {
        conversationText += `SYSTEM: ${msg.text}\n`;
      }
    }

    console.log(`[taxi-extract-unified] Processing ${conversation.length} messages, is_modification=${is_modification}`);
    console.log(`[taxi-extract-unified] Last customer message: "${conversation.filter(m => m.role === 'user').slice(-1)[0]?.text || 'none'}"`);

    // Use tool calling for structured extraction (like C# AiBooking)
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
          { role: "user", content: conversationText }
        ],
        tools: [BOOKING_EXTRACTION_TOOL],
        tool_choice: { type: "function", function: { name: "extract_booking" } },
        temperature: 0.1,
      }),
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[taxi-extract-unified] API error: ${errorText}`);
      throw new Error(`API error: ${response.status}`);
    }

    const data = await response.json();
    
    // Extract from tool call response
    const toolCall = data.choices?.[0]?.message?.tool_calls?.[0];
    if (!toolCall || toolCall.function.name !== "extract_booking") {
      throw new Error("No valid tool call in response");
    }

    const argsJson = toolCall.function.arguments;
    const extracted = typeof argsJson === 'string' ? JSON.parse(argsJson) : argsJson;
    
    // Ensure missing_fields includes luggage for travel hub trips
    if (is_travel_hub_trip && !extracted.luggage && !extracted.missing_fields?.includes("luggage")) {
      extracted.missing_fields = extracted.missing_fields || [];
      extracted.missing_fields.push("luggage");
    }

    // AI now returns COMPLETE booking for modifications (unchanged fields preserved)
    // Only need fallback merge if AI returns null for fields that should be preserved
    let finalResult = {
      pickup: extracted.pickup_location || (is_modification && current_booking?.pickup) || null,
      destination: extracted.dropoff_location || (is_modification && current_booking?.destination) || null,
      // Keep passengers as null if not explicitly provided - don't default to 1
      passengers: extracted.number_of_passengers ?? (is_modification ? current_booking?.passengers : null) ?? null,
      luggage: extracted.luggage === "CLEAR" ? null : (extracted.luggage || (is_modification && current_booking?.luggage) || null),
      vehicle_type: extracted.vehicle_type || (is_modification && current_booking?.vehicle_type) || null,
      pickup_time: extracted.pickup_time || (is_modification && current_booking?.pickup_time) || "ASAP",
      special_requests: extracted.special_requests || (is_modification && current_booking?.special_requests) || null,
      nearest_pickup: extracted.nearest_pickup || null,
      nearest_dropoff: extracted.nearest_dropoff || null,
      fields_changed: extracted.fields_changed || [],
      missing_fields: extracted.missing_fields || [],
      confidence: extracted.confidence || "medium",
      extraction_notes: extracted.extraction_notes || null,
      is_modification: is_modification,
    };

    if (is_modification) {
      console.log(`[taxi-extract-unified] Modification result - fields_changed: ${JSON.stringify(extracted.fields_changed || [])}`);
    }

    // Apply alias fallback in TypeScript (in case AI missed it)
    if (aliases) {
      for (const [key, value] of Object.entries(aliases)) {
        if (finalResult.pickup?.toLowerCase().includes(key.toLowerCase())) {
          console.log(`[taxi-extract-unified] Applied alias: pickup "${key}" → "${value}"`);
          finalResult.pickup = value;
        }
        if (finalResult.destination?.toLowerCase().includes(key.toLowerCase())) {
          console.log(`[taxi-extract-unified] Applied alias: destination "${key}" → "${value}"`);
          finalResult.destination = value;
        }
      }
    }

    const processingTime = Date.now() - startTime;
    console.log(`[taxi-extract-unified] Extracted in ${processingTime}ms:`, finalResult);

    return new Response(
      JSON.stringify({
        ...finalResult,
        processing_time_ms: processingTime,
        raw_extraction: extracted // Include raw for debugging
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );

  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    const processingTime = Date.now() - startTime;
    console.error(`[taxi-extract-unified] Error after ${processingTime}ms:`, error);
    
    return new Response(
      JSON.stringify({ 
        error: errorMessage,
        pickup: null,
        destination: null,
        passengers: null,
        luggage: null,
        pickup_time: "ASAP",
        vehicle_type: null,
        special_requests: null,
        missing_fields: ["pickup", "destination", "passengers"],
        confidence: "low",
        processing_time_ms: processingTime
      }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
