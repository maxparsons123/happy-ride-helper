import "https://deno.land/x/xhr@0.1.0/mod.ts";
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");

/**
 * UNIFIED BOOKING EXTRACTION
 * 
 * This function takes the FULL conversation transcript and extracts ALL booking
 * details in a single AI pass. This is more reliable than inline regex parsing
 * because the AI can understand context (e.g., "two passengers" vs "two bags").
 * 
 * Returns:
 * {
 *   pickup: string | null,
 *   destination: string | null,
 *   passengers: number | null,
 *   luggage: string | null,
 *   pickup_time: string | null,
 *   vehicle_type: string | null,
 *   special_requests: string | null,
 *   missing_fields: string[],
 *   confidence: "high" | "medium" | "low"
 * }
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

const UNIFIED_EXTRACTION_PROMPT = (now: string, callerName: string | null, callerCity: string | null) => `
You are a BOOKING EXTRACTION AI. Your job is to extract ALL booking details from a taxi conversation transcript.

Current time (London): ${now}
${callerName ? `Caller's name: ${callerName}` : "Caller's name: Unknown"}
${callerCity ? `Caller's city: ${callerCity} (use as default city for addresses without explicit city)` : ""}

==================================================
EXTRACTION RULES
==================================================

You must extract EXACTLY what the CUSTOMER said. Pay careful attention to:

1. PICKUP LOCATION
   - "from X", "pick me up at X", "collect me from X"
   - House numbers with letters: "52A", "1214B" (preserve exactly)
   - If they say "my location" or "here" → pickup = "by_gps"

2. DESTINATION
   - "to Y", "going to Y", "heading to Y", "take me to Y"
   - If not mentioned → destination = null

3. PASSENGERS (number)
   - "just me" / "myself" → 1
   - "two of us" / "couple" → 2
   - "three passengers" → 3
   - IMPORTANT: Only count this when they're answering "how many passengers" OR explicitly stating passenger count
   - DO NOT confuse with luggage count!

4. LUGGAGE (string)
   - "2 bags" / "two suitcases" / "some luggage"
   - "no bags" / "no luggage" → "no luggage"
   - IMPORTANT: Only count this when they're answering "how many bags" OR explicitly stating luggage
   - DO NOT confuse with passenger count!
   - If they mention passengers but not bags → luggage = null (not confirmed)

5. PICKUP TIME
   - "now" / "asap" / "straight away" → "ASAP"
   - "5pm" / "at 3:30" → "YYYY-MM-DD HH:MM"
   - "in 20 minutes" → calculate from ${now}

6. VEHICLE TYPE
   - "6 seater" / "estate" / "MPV" / "minibus" / "saloon"
   - Only if explicitly requested

7. SPECIAL REQUESTS
   - "ring when outside", "wheelchair access", "child seat", "specific driver"

==================================================
CONTEXT DISAMBIGUATION (CRITICAL)
==================================================

When a number is mentioned, determine what it refers to based on the PRECEDING question:

- If Ada just asked "how many passengers?" and customer says "two" → passengers = 2
- If Ada just asked "how many bags?" and customer says "two" → luggage = "2 bags"
- If customer says "two passengers and three bags" → passengers = 2, luggage = "3 bags"
- If customer says "two passengers" (with word "passengers") → passengers = 2, DO NOT set luggage

The last question Ada asked is the CONTEXT for ambiguous single-word answers.

==================================================
MISSING FIELDS
==================================================

Return a list of fields that are still needed to complete the booking:
- If no pickup → "pickup"
- If no destination → "destination"  
- If no passengers → "passengers"
- If trip involves airport/station but no luggage info → "luggage"

==================================================
OUTPUT FORMAT (REQUIRED)
==================================================

Return ONLY valid JSON:

{
  "pickup": string | null,
  "destination": string | null,
  "passengers": number | null,
  "luggage": string | null,
  "pickup_time": string | null,
  "vehicle_type": string | null,
  "special_requests": string | null,
  "missing_fields": ["pickup", "destination", "passengers", "luggage"],
  "confidence": "high" | "medium" | "low",
  "extraction_notes": "Brief note about any ambiguity or assumptions"
}

NO extra text. NO explanations outside the JSON.
`;

interface ExtractionRequest {
  // The full conversation transcript (array of messages)
  conversation: Array<{
    role: "user" | "assistant" | "system";
    text: string;
    timestamp?: string;
  }>;
  // Current known booking state (to understand what's already collected)
  current_booking?: {
    pickup?: string | null;
    destination?: string | null;
    passengers?: number | null;
    luggage?: string | null;
    pickup_time?: string | null;
    vehicle_type?: string | null;
  };
  // Caller context
  caller_name?: string | null;
  caller_city?: string | null;
  // Is this a travel hub trip? (airport/station)
  is_travel_hub_trip?: boolean;
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
      current_booking = {},
      caller_name = null,
      caller_city = null,
      is_travel_hub_trip = false
    } = request;
    
    if (!conversation || conversation.length === 0) {
      return new Response(
        JSON.stringify({ 
          error: "No conversation provided",
          pickup: null,
          destination: null,
          passengers: null,
          luggage: null,
          pickup_time: null,
          vehicle_type: null,
          special_requests: null,
          missing_fields: ["pickup", "destination", "passengers"],
          confidence: "low"
        }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const now = getLondonTime();
    const systemPrompt = UNIFIED_EXTRACTION_PROMPT(now, caller_name, caller_city);

    // Format conversation for the AI
    // Include the current booking state as context
    let conversationText = "";
    
    if (current_booking && Object.keys(current_booking).some(k => current_booking[k as keyof typeof current_booking])) {
      conversationText += "=== CURRENT BOOKING STATE ===\n";
      if (current_booking.pickup) conversationText += `Pickup: ${current_booking.pickup}\n`;
      if (current_booking.destination) conversationText += `Destination: ${current_booking.destination}\n`;
      if (current_booking.passengers) conversationText += `Passengers: ${current_booking.passengers}\n`;
      if (current_booking.luggage) conversationText += `Luggage: ${current_booking.luggage}\n`;
      if (current_booking.pickup_time) conversationText += `Time: ${current_booking.pickup_time}\n`;
      if (current_booking.vehicle_type) conversationText += `Vehicle: ${current_booking.vehicle_type}\n`;
      conversationText += "\n";
    }

    conversationText += "=== CONVERSATION TRANSCRIPT ===\n";
    for (const msg of conversation) {
      const role = msg.role === "user" ? "CUSTOMER" : msg.role === "assistant" ? "ADA" : "SYSTEM";
      conversationText += `${role}: ${msg.text}\n`;
    }

    console.log(`[taxi-extract-unified] Processing ${conversation.length} messages...`);
    console.log(`[taxi-extract-unified] Last customer message: "${conversation.filter(m => m.role === 'user').slice(-1)[0]?.text || 'none'}"`);

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
        temperature: 0.1,
      }),
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[taxi-extract-unified] API error: ${errorText}`);
      throw new Error(`API error: ${response.status}`);
    }

    const data = await response.json();
    const content = data.choices[0]?.message?.content;
    
    if (!content) {
      throw new Error("No content in response");
    }

    // Parse JSON from response
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
    
    // Ensure missing_fields includes luggage for travel hub trips
    if (is_travel_hub_trip && !extracted.luggage && !extracted.missing_fields?.includes("luggage")) {
      extracted.missing_fields = extracted.missing_fields || [];
      extracted.missing_fields.push("luggage");
    }

    const processingTime = Date.now() - startTime;
    console.log(`[taxi-extract-unified] Extracted in ${processingTime}ms:`, extracted);

    return new Response(
      JSON.stringify({
        ...extracted,
        processing_time_ms: processingTime
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
        pickup_time: null,
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
