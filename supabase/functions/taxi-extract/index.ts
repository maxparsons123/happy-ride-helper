import "https://deno.land/x/xhr@0.1.0/mod.ts";
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Use Lovable AI gateway (pre-configured, no API key needed from user)
const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");

// Get current London time
const getLondonTime = () => {
  return new Date().toLocaleString("en-GB", {
    timeZone: "Europe/London",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
};

const NEW_BOOKING_PROMPT = (now: string) => `
You are a STRICT taxi booking AI extracting booking details from speech transcripts.
Current time (London): ${now}

==================================================
EXTRACTION RULES (CRITICAL)
==================================================
Extract ONLY these fields from the user's speech:
- pickup_location
- dropoff_location  
- number_of_passengers
- special_requests

STRICT RULES:
• Return the EXACT text the user said - DO NOT correct, normalise, or guess
• House numbers with letters (52A, 18B) must be preserved exactly
• Street names must be preserved exactly as spoken
• DO NOT add postcodes, fix spelling, or change formatting
• If unclear, return null for that field

==================================================
LOCATION EXTRACTION PATTERNS
==================================================
Pickup indicators: "from", "pick up from", "at", "I'm at", "collect from"
Dropoff indicators: "to", "going to", "heading to", "take me to", "drop at"

Examples:
"from 52A David Road to Manchester" →
  pickup_location: "52A David Road"
  dropoff_location: "Manchester"

"pick me up from the high street going to the station" →
  pickup_location: "high street"
  dropoff_location: "station"

==================================================
PASSENGER EXTRACTION
==================================================
Convert spoken numbers:
"one" → 1, "two" → 2, "three" → 3, etc.
"just me" / "myself" → 1

==================================================
SPECIAL REQUESTS
==================================================
Any additional instructions that are NOT:
- pickup location
- dropoff location  
- passenger count

Examples:
- "ring me when outside"
- "driver 314 please"
- "wheelchair access needed"
- "I have a dog"

==================================================
OUTPUT FORMAT
==================================================
Return ONLY valid JSON:

{
  "pickup_location": string | null,
  "dropoff_location": string | null,
  "number_of_passengers": number | null,
  "special_requests": string | null,
  "confidence": "high" | "medium" | "low"
}

Set confidence based on clarity:
- "high": Clear, unambiguous extraction
- "medium": Some interpretation needed
- "low": Unclear, may need confirmation
`;

const UPDATE_BOOKING_PROMPT = (now: string, existing: any) => `
You are a STRICT taxi booking AI handling UPDATES to an existing booking.
Current time (London): ${now}

==================================================
EXISTING BOOKING (DO NOT COPY - reference only)
==================================================
Pickup: ${existing.pickup_location || "not set"}
Dropoff: ${existing.dropoff_location || "not set"}
Passengers: ${existing.number_of_passengers || "not set"}
Special requests: ${existing.special_requests || "none"}

==================================================
UPDATE RULES (CRITICAL)
==================================================
• Only return fields the user EXPLICITLY changes
• Any field NOT changed must be null
• Preserve EXACT text the user says - no corrections

==================================================
OUTPUT FORMAT
==================================================
Return ONLY valid JSON:

{
  "pickup_location": string | null,
  "dropoff_location": string | null,
  "number_of_passengers": number | null,
  "special_requests": string | null,
  "confidence": "high" | "medium" | "low"
}
`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { transcript, mode = "new", existing_booking = null } = await req.json();
    
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
        response_format: { type: "json_object" }
      }),
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[taxi-extract] OpenAI API error: ${errorText}`);
      throw new Error(`OpenAI API error: ${response.status}`);
    }

    const data = await response.json();
    const content = data.choices[0]?.message?.content;
    
    if (!content) {
      throw new Error("No content in OpenAI response");
    }

    const extracted = JSON.parse(content);
    console.log(`[taxi-extract] Extracted:`, extracted);

    return new Response(
      JSON.stringify(extracted),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );

  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    console.error("[taxi-extract] Error:", error);
    return new Response(
      JSON.stringify({ error: errorMessage }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
