import "https://deno.land/x/xhr@0.1.0/mod.ts";
import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");

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
• "from X" or "pick up from X" or "at X" → pickup_location = X
• "to Y" or "going to Y" or "heading to Y" or "take me to Y" → dropoff_location = Y
• If 'nearest' or 'closest' is mentioned, include in pickup_location
• If no drop-off given or "as directed" → dropoff_location = "as directed"

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

    return new Response(
      JSON.stringify(extracted),
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
