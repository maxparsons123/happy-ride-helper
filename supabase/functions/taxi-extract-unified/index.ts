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

// Build system prompt with alias instructions if available
const buildSystemPrompt = (
  now: string, 
  callerName: string | null, 
  callerCity: string | null,
  aliases: Record<string, string> | null,
  existingBooking: ExistingBooking | null
) => {
  let aliasInstruction = "";
  if (aliases && Object.keys(aliases).length > 0) {
    const aliasJson = JSON.stringify(aliases);
    aliasInstruction = `
The user has saved the following location aliases:
${aliasJson}

Rules:
1. If the pickup or drop-off location matches (exactly or approximately) one of these alias keys, replace it with the corresponding full address.
2. Apply fuzzy matching to handle typos or variations (e.g., 'hme' → 'home', 'my place' → 'home').
3. Always output the full address in the final result, not the alias name.
4. If alias is used with extra text (e.g., 'home station'), still map the alias part and keep the rest.
`;
  }

  let modificationInstruction = "";
  if (existingBooking) {
    modificationInstruction = `
EXISTING BOOKING (for modification):
- Pickup: "${existingBooking.pickup || 'not set'}"
- Destination: "${existingBooking.destination || 'not set'}"
- Passengers: ${existingBooking.passengers || 1}
- Luggage: "${existingBooking.luggage || 'not specified'}"
- Vehicle: "${existingBooking.vehicle_type || 'saloon'}"
- Time: "${existingBooking.pickup_time || 'ASAP'}"

MODIFICATION RULES:
- ONLY update fields that the user explicitly wants to change.
- Keep ALL other fields exactly as they are in the existing booking.
- If user says "change pickup to X" → only update pickup, keep destination/passengers/etc unchanged.
- If user says "add 2 bags" → only update luggage, keep everything else unchanged.
`;
  }

  return `You are an expert multilingual taxi booking assistant.
Current time (London): ${now}
${callerName ? `Caller's name: ${callerName}` : "Caller's name: Unknown"}
${callerCity ? `Caller's city: ${callerCity} (use as default city for addresses without explicit city)` : ""}

${aliasInstruction}
${modificationInstruction}

EXTRACTION RULES:

1. **Location Detection**:
   - Look for 'from', 'pick up from', 'collect from' → pickup_location
   - Look for 'to', 'going to', 'heading to', 'take me to' → dropoff_location
   - If user says 'my location', 'here', 'where I am' → set pickup_location = 'by_gps'
   - If 'as directed' or no destination given → dropoff_location = 'as directed'

2. **House Numbers - CRITICAL**:
   - Preserve EXACT house numbers including letters: "52A", "1214B", "7b"
   - If ambiguous (e.g., "5th to 8th" might be mishearing), flag as uncertain

3. **Time Normalization**:
   - Convert to 'YYYY-MM-DD HH:MM' (24-hour format)
   - 'now', 'asap', 'straight away' → 'ASAP'
   - 'in 20 minutes' → calculate from current time
   - If time is earlier than now → assume tomorrow

4. **Passengers vs Luggage - CONTEXT MATTERS**:
   - "two passengers" → passengers = 2 (NOT luggage)
   - "two bags" / "two suitcases" → luggage = "2 bags" (NOT passengers)
   - If user answers "how many passengers?" with "two" → passengers = 2
   - If user answers "how many bags?" with "two" → luggage = "2 bags"
   - NEVER confuse these based on preceding question context

5. **Vehicle Types**:
   - Detect: saloon, estate, MPV, people carrier, minibus, 6-seater, 8-seater
   - Only set if explicitly requested

6. **Special Requests**:
   - "ring when outside", "wheelchair access", "child seat", "specific driver"
   - Do NOT include phone numbers here

7. **Missing Fields**:
   - Return list of essential fields still needed: pickup, destination, passengers, luggage (if airport/station trip)
`;
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
          description: "Pickup address. Use 'by_gps' if user says 'my location' or 'here'." 
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
      is_modification ? current_booking : null
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
