import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_PROMPT = `Role: Professional Taxi Dispatch Logic System for UK & Netherlands.

Objective: Extract, validate, and GEOCODE pickup and drop-off addresses from user messages, using phone number patterns to determine geographic bias.

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
     → Set is_ambiguous=true, status="clarification_needed", and provide top 3 city alternatives
   - Common streets like "High Street", "Church Road", "Station Road", "Russell Street", "Park Road" 
     are ALWAYS ambiguous without a city name OR a caller history match
   - NEVER default to London just because a street exists there

3. Netherlands:
   - +31 or 0031 → Netherlands
   - 020 → Amsterdam, 010 → Rotterdam, 070 → The Hague, 030 → Utrecht

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
4. For landmarks, resolve to actual street addresses if known
5. ALWAYS include the postal code in the address field (e.g., "7 Russell Street, Coventry CV1 3BT")
6. The postal_code field MUST be populated separately whenever determinable

INTRA-CITY DISTRICT DISAMBIGUATION (CRITICAL):
Even when the CITY is known (e.g., Birmingham from landline 0121), many street names exist in MULTIPLE districts within that city.
- Example: "School Road" exists in Hall Green, Moseley, Yardley, Kings Heath, and other Birmingham districts.
- Example: "Church Road" exists in Erdington, Aston, Yardley, Sheldon, and others within Birmingham.
- Example: "Park Road" exists in Moseley, Hockley, Aston, Sparkbrook within Birmingham.
- FIRST check CALLER_HISTORY — if the caller has been to "School Road, Hall Green" before and says "School Road", resolve to Hall Green directly.
- If NO history match and a street name exists in 3+ districts within the detected city AND no district/area/postcode is mentioned:
  → Set is_ambiguous=true, status="clarification_needed"
  → Provide alternatives as "Street Name, District" (e.g., "School Road, Hall Green", "School Road, Moseley")
  → Ask the user which area/district they mean
- District-unique streets (only one in the city) can be resolved directly.
- A house number can help: if "School Road, Hall Green" is short (numbers up to ~80) but "School Road, Yardley" is long (numbers up to 200+), a high house number narrows it down.

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

    console.log(`📍 Address dispatch request: pickup="${pickup}", dest="${destination}", phone="${phone}"`);

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

    // Build the user message
    const userMessage = `User Message: Pickup from "${pickup || 'not provided'}" going to "${destination || 'not provided'}"
User Phone: ${phone || 'not provided'}${callerHistory}`;

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
    const content = aiResponse.choices?.[0]?.message?.content;

    if (!content) {
      throw new Error("No content in AI response");
    }

    // Parse the JSON response from AI (strip markdown code blocks if present)
    let parsed;
    try {
      let jsonStr = content.trim();
      // Strip ```json ... ``` wrapper that Gemini often adds
      const codeBlockMatch = jsonStr.match(/```(?:json)?\s*([\s\S]*?)```/);
      if (codeBlockMatch) {
        jsonStr = codeBlockMatch[1].trim();
      }
      parsed = JSON.parse(jsonStr);
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
