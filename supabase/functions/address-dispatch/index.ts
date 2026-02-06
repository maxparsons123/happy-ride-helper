import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_PROMPT = `Role: Professional Taxi Dispatch Logic System for UK & Netherlands.

Objective: Extract, validate, and GEOCODE pickup and drop-off addresses from user messages, using phone number patterns to determine geographic bias.

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

2. UK Mobile (+44 7 or 07) → NO geographic clue. Use ONLY:
   - Destination landmarks (airports, stations, hospitals)
   - Neighborhood names mentioned
   - City names in the text
   - If truly ambiguous, return top 3 options by population density

3. Netherlands:
   - +31 or 0031 → Netherlands
   - 020 → Amsterdam, 010 → Rotterdam, 070 → The Hague, 030 → Utrecht

ADDRESS EXTRACTION RULES:
1. Preserve house numbers EXACTLY as spoken (e.g., "52A" stays "52A", "1214A" stays "1214A")
2. Do NOT invent house numbers if not provided
3. Append detected city to addresses for clarity
4. For landmarks, resolve to actual street addresses if known

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

OUTPUT FORMAT (STRICT JSON):
{
  "detected_area": "city name or region",
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
    "alternatives": ["option1", "option2"]
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
    "alternatives": []
  },
  "status": "ready" | "clarification_needed"
}`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { pickup, destination, phone } = await req.json();
    
    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY is not configured");
    }

    // Build the user message
    const userMessage = `User Message: Pickup from "${pickup || 'not provided'}" going to "${destination || 'not provided'}"
User Phone: ${phone || 'not provided'}`;

    console.log(`📍 Address dispatch request: pickup="${pickup}", dest="${destination}", phone="${phone}"`);

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
        temperature: 0.1, // Low temp for consistent structured output
        response_format: { type: "json_object" },
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

    const aiResponse = await response.json();
    const content = aiResponse.choices?.[0]?.message?.content;

    if (!content) {
      throw new Error("No content in AI response");
    }

    // Parse the JSON response from AI
    let parsed;
    try {
      parsed = JSON.parse(content);
    } catch (parseErr) {
      console.error("Failed to parse AI JSON:", content);
      // Return a fallback structure
      parsed = {
        detected_area: "unknown",
        phone_analysis: {
          detected_country: "unknown",
          is_mobile: false,
          landline_city: null
        },
        pickup: { address: pickup || "", is_ambiguous: false, alternatives: [] },
        dropoff: { address: destination || "", is_ambiguous: false, alternatives: [] },
        status: "ready"
      };
    }

    console.log(`✅ Address dispatch result:`, JSON.stringify(parsed, null, 2));

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
