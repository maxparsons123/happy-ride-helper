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
${callerName ? `Caller: ${callerName}` : ""}
${callerCity ? `Default city: ${callerCity}` : ""}
${aliasInstruction}

==================================================
EXTRACTION RULES (NEW BOOKING)
==================================================
1. **Location Detection**:
   - 'from', 'pick up from', 'collect from' → pickup_location
   - 'to', 'going to', 'heading to', 'take me to' → dropoff_location
   - 'my location', 'here', 'current location' → set pickup_location = 'by_gps'
   - 'nearest' or 'closest' → extract as nearest_place
   - 'as directed' or no destination → dropoff_location = 'as directed'

2. **Address Preservation - CRITICAL**:
   - Return EXACT text the user typed
   - DO NOT guess, correct spelling, add/remove postcodes, or change punctuation
   - Preserve house numbers with letters: "52A", "1214B", "7b"

3. **Time Handling**:
   - Convert to 'YYYY-MM-DD HH:MM' (24-hour)
   - 'now', 'asap' → 'ASAP'
   - 'in X minutes' → calculate from current time
   - Time in past → assume tomorrow

4. **Passengers vs Luggage**:
   - "two passengers" → passengers = 2
   - "two bags/suitcases" → luggage = "2 bags"
   - Context matters: answer to "how many passengers?" → passengers

5. **Vehicle Types**:
   - saloon, estate, MPV, minibus, 6-seater, 8-seater
   - Only set if explicitly requested

6. **Special Requests**:
   - "ring when outside", "wheelchair access", "child seat", driver requests
   - Do NOT include phone numbers
`;
};

// Build system prompt for BOOKING UPDATES (comprehensive C#-style prompt)
const buildUpdateBookingPrompt = (
  now: string,
  existingBooking: ExistingBooking
) => {
  return `You are a STRICT taxi booking AI handling UPDATES ONLY.
Current time (London): ${now}

==================================================
EXISTING BOOKING (REFERENCE ONLY — DO NOT COPY)
==================================================
Pickup: ${existingBooking.pickup || 'not set'}
Dropoff: ${existingBooking.destination || 'not set'}
Time: ${existingBooking.pickup_time || 'ASAP'}
Passengers: ${existingBooking.passengers || 1}
Luggage: ${existingBooking.luggage || 'not specified'}
Special requests: ${existingBooking.special_requests || 'none'}

You MUST NOT reuse these values unless the user explicitly changes them.
You MUST ALWAYS reply in English, even if the input is in another language.
Try to put user message into a structured format so it reads good English.
Never reply in Bengali, Hindi, Urdu, or Punjabi.

==================================================
UPDATE RULES (CRITICAL)
==================================================
• Only return fields the user EXPLICITLY changes.
• Any field NOT changed must be returned as null.
• "remove luggage" → luggage = "CLEAR".
• intent MUST be "update_booking".
• If user says 'my location', 'here', etc., set pickup_location = 'by_gps'.

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
   - If 'nearest' or 'closest' is mentioned, extract as 'nearest_place'.
   - If no drop-off given or phrases like 'as directed', set dropoff_location = 'as directed'.

You MUST return the EXACT text the user typed.

==================================================
PICKUP + DROPOFF UPDATE RULE (CRITICAL)
==================================================
If the user message includes BOTH a new pickup AND a new dropoff,
you MUST return BOTH fields, even if the user writes them in a single
sentence, multiple sentences, or mixed with other instructions.

This includes patterns such as:
• "from X to Y"
• "pickup from X and drop at Y"
• "pick me up at X take me to Y"
• "I am at X going to Y"
• "collect from X, deliver to Y"

RULE:
• pickup_location = EXACT text of X
• dropoff_location = EXACT text of Y

You MUST return BOTH fields whenever both are updated.
You MUST NOT ignore one of the locations.
You MUST NOT return null for either if both were updated.

LOCATION CLEANING RULE (IMPORTANT):
When extracting pickup_location or dropoff_location:
• If a location begins with "the ", "a ", or "an ", remove ONLY that article.
• If "going to" appears, it ALWAYS defines the dropoff_location.
• The rest of the text must remain EXACTLY as typed.

Examples:
"from the high street" → pickup_location = "high street"
"to the station" → dropoff_location = "station"

==================================================
POSTCODE RULE (STRICT)
==================================================
User may type: cv12ew, CV12EW, b276hp, B27 6HP, "b303jh,1" etc.
You MUST return their postcode EXACTLY as typed. NO editing.

==================================================
TIME UPDATE RULES
==================================================
If the user specifies a time, output pickup_time in:
  "YYYY-MM-DD HH:MM" (24-hour)
Else → pickup_time = null.

Rules:
• "now" / "asap" → "ASAP".
• Time alone ("5pm") → TODAY at 17:00, unless past → then TOMORROW.
• "in X minutes/hours" → add duration to current time.
• Natural phrases:
    "tonight" → 21:00 today
    "this evening" → 19:00 today
    "afternoon" → 15:00 today
    "morning" → 09:00 today

==================================================
LUGGAGE EXTRACTION RULES (STRONG)
==================================================
You MUST treat ANY of the following words as a luggage update:
"luggage", "bags", "bag", "suitcase", "suitcases", "cases", "holdall", 
"backpack", "rucksack"

If the user message contains a number near one of these words:
luggage = EXACT text the user wrote.

Examples:
"can I add 2 luggage" → luggage = "2 luggage"
"add 3 bags please"   → luggage = "3 bags"
"I have one suitcase" → luggage = "one suitcase"
"remove luggage"      → luggage = "CLEAR"

If luggage is mentioned WITHOUT a number:
return the EXACT phrase as luggage.

IMPORTANT: Luggage ALWAYS has priority over special_requests.

==================================================
INTERMEDIATE STOP / VIA RULES
==================================================
If the user mentions:
• "stop at", "via", "then go to", "drop me at X then Y", "wait at X"

You MUST treat this as a VIA STOP.
• The FIRST destination after pickup is the via_stop.
• The FINAL destination is the dropoff_location.
• Any duration (e.g. "10 minutes") MUST be included in special_requests.

If a via stop exists:
• pickup_location = origin
• dropoff_location = FINAL destination
• special_requests MUST include: "Stop at <via location> for <duration>"

==================================================
SPECIAL REQUESTS (STRICT)
==================================================
ANY user text that:
• instructs the driver
• requests a specific driver (e.g., "driver 314 please")
• requests vehicle type
• gives behaviour instructions
• adds preferences or notes
• does NOT modify pickup, dropoff, time, passengers, or luggage

MUST go into special_requests EXACTLY as written.

Examples: "driver 314 please", "ring me when outside", "wheelchair access"

This content MUST NOT set any booking fields.
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
const BOOKING_EXTRACTION_TOOL = {
  type: "function",
  function: {
    name: "extract_booking",
    description: "Extract all taxi booking details from the conversation transcript.",
    parameters: {
      type: "object",
      properties: {
        pickup_location: { 
          type: "string", 
          description: "Pickup address. Must be a specific street address, landmark, or venue. Do NOT use 'by_gps'." 
        },
        dropoff_location: { 
          type: "string", 
          description: "Destination address. Use 'as directed' if not specified." 
        },
        pickup_time: { 
          type: "string", 
          description: "Pickup time in 'YYYY-MM-DD HH:MM' format, or 'ASAP' for immediate." 
        },
        number_of_passengers: { 
          type: "integer", 
          description: "Number of passengers (1-8). Default 1 if not specified." 
        },
        luggage: { 
          type: "string", 
          description: "Luggage description (e.g., '2 bags', 'no luggage'). Leave empty if not mentioned." 
        },
        vehicle_type: { 
          type: "string", 
          enum: ["saloon", "estate", "mpv", "minibus", "8-seater"],
          description: "Requested vehicle type. Only set if explicitly requested." 
        },
        special_requests: { 
          type: "string", 
          description: "Any special requests (ring on arrival, wheelchair, child seat, etc.)" 
        },
        nearest_place: { 
          type: "string", 
          description: "If user asks for 'nearest' or 'closest' something (e.g., 'nearest hotel')." 
        },
        missing_fields: {
          type: "array",
          items: { type: "string" },
          description: "List of essential fields still needed: pickup, destination, passengers, luggage (if travel hub)"
        },
        confidence: {
          type: "string",
          enum: ["high", "medium", "low"],
          description: "Confidence in extraction accuracy"
        },
        extraction_notes: {
          type: "string",
          description: "Brief note about any ambiguity, assumptions, or alias resolutions applied"
        }
      },
      required: ["pickup_location", "dropoff_location", "pickup_time", "number_of_passengers", "missing_fields", "confidence"]
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
          passengers: 1,
          luggage: null,
          pickup_time: "ASAP",
          vehicle_type: null,
          special_requests: null,
          missing_fields: ["pickup", "destination"],
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

    // Format conversation for the AI
    let conversationText = "=== CONVERSATION TRANSCRIPT ===\n";
    for (const msg of conversation) {
      const role = msg.role === "user" ? "CUSTOMER" : msg.role === "assistant" ? "ADA" : "SYSTEM";
      conversationText += `${role}: ${msg.text}\n`;
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

    // For modifications: merge with existing booking (only changed fields update)
    let finalResult = {
      pickup: extracted.pickup_location || null,
      destination: extracted.dropoff_location || null,
      passengers: extracted.number_of_passengers || 1,
      luggage: extracted.luggage || null,
      vehicle_type: extracted.vehicle_type || null,
      pickup_time: extracted.pickup_time || "ASAP",
      special_requests: extracted.special_requests || null,
      nearest_place: extracted.nearest_place || null,
      missing_fields: extracted.missing_fields || [],
      confidence: extracted.confidence || "medium",
      extraction_notes: extracted.extraction_notes || null,
    };

    if (is_modification && current_booking) {
      // Keep existing values for fields not explicitly changed
      finalResult = {
        pickup: extracted.pickup_location || current_booking.pickup || null,
        destination: extracted.dropoff_location || current_booking.destination || null,
        passengers: extracted.number_of_passengers || current_booking.passengers || 1,
        luggage: extracted.luggage || current_booking.luggage || null,
        vehicle_type: extracted.vehicle_type || current_booking.vehicle_type || null,
        pickup_time: extracted.pickup_time || current_booking.pickup_time || "ASAP",
        special_requests: extracted.special_requests || null,
        nearest_place: extracted.nearest_place || null,
        missing_fields: extracted.missing_fields || [],
        confidence: extracted.confidence || "medium",
        extraction_notes: extracted.extraction_notes || null,
      };
      console.log(`[taxi-extract-unified] Merged with existing booking for modification`);
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
        passengers: 1,
        luggage: null,
        pickup_time: "ASAP",
        vehicle_type: null,
        special_requests: null,
        missing_fields: ["pickup", "destination"],
        confidence: "low",
        processing_time_ms: processingTime
      }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
