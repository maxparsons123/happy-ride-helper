import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// UK landline area codes mapped to cities
const ukAreaCodes: Record<string, string> = {
  "20": "London",
  "21": "Birmingham", 
  "22": "Southampton/Portsmouth",
  "23": "Southampton/Portsmouth",
  "24": "Coventry",
  "28": "Northern Ireland",
  "29": "Cardiff",
  "113": "Leeds",
  "114": "Sheffield",
  "115": "Nottingham",
  "116": "Leicester",
  "117": "Bristol",
  "118": "Reading",
  "121": "Birmingham",
  "131": "Edinburgh",
  "141": "Glasgow",
  "151": "Liverpool",
  "161": "Manchester",
  "191": "Newcastle",
  "247": "Coventry",
  "1onal": "Various",
};

// NL landline area codes
const nlAreaCodes: Record<string, string> = {
  "10": "Rotterdam",
  "20": "Amsterdam",
  "30": "Utrecht",
  "40": "Eindhoven",
  "45": "Heerlen",
  "50": "Groningen",
  "70": "Den Haag",
  "71": "Leiden",
  "73": "Den Bosch",
  "74": "Enschede",
  "76": "Breda",
  "77": "Venlo",
  "78": "Dordrecht",
  "79": "Zoetermeer",
};

function detectPhoneRegion(phone: string): { country: string; city: string | null; isMobile: boolean } {
  const cleaned = phone.replace(/[\s\-()]/g, "");
  
  // UK numbers
  if (cleaned.startsWith("+44") || cleaned.startsWith("0044") || cleaned.startsWith("44")) {
    const withoutCountry = cleaned.replace(/^(\+44|0044|44)/, "");
    
    // Mobile check: starts with 7
    if (withoutCountry.startsWith("7")) {
      return { country: "UK", city: null, isMobile: true };
    }
    
    // Landline: check area codes
    for (const [code, city] of Object.entries(ukAreaCodes)) {
      if (withoutCountry.startsWith(code)) {
        return { country: "UK", city, isMobile: false };
      }
    }
    
    return { country: "UK", city: null, isMobile: false };
  }
  
  // NL numbers
  if (cleaned.startsWith("+31") || cleaned.startsWith("0031") || cleaned.startsWith("31")) {
    const withoutCountry = cleaned.replace(/^(\+31|0031|31)/, "");
    
    // Mobile check: starts with 6
    if (withoutCountry.startsWith("6")) {
      return { country: "NL", city: null, isMobile: true };
    }
    
    // Landline: check area codes
    for (const [code, city] of Object.entries(nlAreaCodes)) {
      if (withoutCountry.startsWith(code)) {
        return { country: "NL", city, isMobile: false };
      }
    }
    
    return { country: "NL", city: null, isMobile: false };
  }
  
  // UK without country code (starts with 0)
  if (cleaned.startsWith("0")) {
    const withoutLeadingZero = cleaned.substring(1);
    
    if (withoutLeadingZero.startsWith("7")) {
      return { country: "UK", city: null, isMobile: true };
    }
    
    for (const [code, city] of Object.entries(ukAreaCodes)) {
      if (withoutLeadingZero.startsWith(code)) {
        return { country: "UK", city, isMobile: false };
      }
    }
  }
  
  return { country: "Unknown", city: null, isMobile: true };
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { pickup, destination, phone, conversation } = await req.json();
    
    const phoneRegion = detectPhoneRegion(phone || "");
    
    const systemPrompt = `**Role:** You are a professional, high-intelligence Taxi Dispatch Logic System (V3).

**Objective:** Extract pickup and drop-off addresses. Resolve geographic ambiguity using text context, landline area codes, or mobile-to-destination logic.

**Phone Analysis Result (Pre-Computed):**
- Country: ${phoneRegion.country}
- Detected City (from landline): ${phoneRegion.city || "None - mobile or unknown"}
- Is Mobile Number: ${phoneRegion.isMobile}

**Rules for Geographic Biasing (The Hierarchy of Evidence):**

1. **THE LANDLINE ANCHOR (Highest Priority):**
   If the phone number is a UK landline (e.g., +44 24 = Coventry, +44 121 = Birmingham, +44 161 = Manchester), this is your PRIMARY geographic anchor.
   - The caller is almost certainly IN or NEAR that city
   - Prioritize ALL address matches to that city first
   - Example: "10 High Street" + phone +44 24 76... → "10 High Street, Coventry"

2. **THE MOBILE/INTERNATIONAL ANCHOR (+44 7 or +31 6):**
   If the number is a mobile, it provides NO geographic clue. Apply this fallback hierarchy:
   
   a) **Text Priority:** Look for city names explicitly mentioned in EITHER pickup or destination
   b) **Destination Inference:** If no city in pickup but one is mentioned in destination (e.g., "to Coventry Station"), assume pickup is ALSO in/near that city
   c) **Landmark Inference:** Use unique landmarks to identify cities:
      - "Coventry Station", "Ricoh Arena" → Coventry
      - "Birmingham New Street", "Bullring" → Birmingham  
      - "Manchester Piccadilly", "Trafford Centre" → Manchester
      - "Euston Station", "Heathrow" → London
   d) **Neighborhood Clues:** Recognize unique neighborhoods:
      - "Spon End", "Earlsdon", "Tile Hill", "Chapelfields" → Coventry
      - "Moseley", "Edgbaston", "Digbeth" → Birmingham
      - "Didsbury", "Chorlton" → Manchester

3. **THE TEXTUAL ANCHOR (Last Resort):**
   If still ambiguous, look for ANY geographic indicator in the conversation context.

**Ambiguity Protocol:**
If a street exists in multiple UK locations AND the phone is mobile (+44 7) with NO city mentioned anywhere:
- You MUST set "status": "clarification_required"
- Provide the top 3 most likely cities in the "alternatives" array (ranked by population)
- Set "clarification_message" to ask the user which city they mean

**CRITICAL - House Number Extraction:**
- Extract house numbers EXACTLY as spoken: "52A" → "52A", "52-8" → "52-8", "7" → "7"
- Convert spoken numbers: "fifty-two A" → "52A", "number seven" → "7"
- If user provides "52A David Road" → house_number: "52A", street: "David Road"
- NEVER invent, guess, or add house numbers the user didn't provide
- If no house number provided, leave house_number as empty string ""

**Address Formatting:**
- Format: "[HouseNumber] [Street], [City], [PostalCode]"
- Examples: 
  - "52A David Road, Coventry, CV1 2AB"
  - "7 Russell Street, Coventry, CV1 4GE"  
  - "Russell Street, Coventry" (if no house number provided)

**Output as JSON (STRICT FORMAT):**
{
  "detected_region": "City name from hierarchy or 'Unknown'",
  "region_source": "landline_area_code | destination_landmark | text_mention | neighborhood | ambiguous",
  "pickup": {
    "resolved": true,
    "address": "Full formatted address with city",
    "house_number": "Exact house number or empty string",
    "street": "Street name only (no house number)",
    "city": "City name",
    "postal_code": "Postal code if inferrable, else empty",
    "confidence": 0.95,
    "alternatives": []
  },
  "destination": {
    "resolved": true,
    "address": "Full formatted address with city",
    "house_number": "Exact house number or empty string", 
    "street": "Street name only",
    "city": "City name",
    "postal_code": "Postal code if inferrable, else empty",
    "confidence": 0.95,
    "alternatives": []
  },
  "status": "ready_to_book | clarification_required",
  "clarification_message": null
}`;

    const userMessage = `Phone number: ${phone || "Not provided"}
Pickup address spoken: ${pickup || "Not provided"}
Destination spoken: ${destination || "Not provided"}
${conversation ? `Recent conversation context: ${conversation}` : ""}

Analyze these addresses and resolve any geographic ambiguity.`;

    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY not configured");
    }

    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-3-flash-preview",
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: userMessage },
        ],
        temperature: 0.1, // Low temp for consistent extraction
      }),
    });

    if (!response.ok) {
      const errText = await response.text();
      console.error("Gemini API error:", response.status, errText);
      
      // Fallback response
      return new Response(JSON.stringify({
        detected_region: phoneRegion.city || "Unknown",
        region_source: phoneRegion.city ? "landline_area_code" : "unknown",
        pickup: {
          resolved: !!pickup,
          address: pickup || "",
          city: phoneRegion.city || "",
          confidence: 0.5,
          alternatives: [],
        },
        destination: {
          resolved: !!destination,
          address: destination || "",
          city: phoneRegion.city || "",
          confidence: 0.5,
          alternatives: [],
        },
        status: "ready_to_book",
        clarification_message: null,
        fallback: true,
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const aiResponse = await response.json();
    const content = aiResponse.choices?.[0]?.message?.content || "";
    
    // Parse JSON from response (handle markdown code blocks)
    let parsed;
    try {
      const jsonMatch = content.match(/```(?:json)?\s*([\s\S]*?)```/) || [null, content];
      const jsonStr = jsonMatch[1] || content;
      parsed = JSON.parse(jsonStr.trim());
    } catch (parseErr) {
      console.error("Failed to parse AI response:", content);
      // Return raw response for debugging
      parsed = {
        detected_region: phoneRegion.city || "Unknown",
        region_source: "parse_error",
        raw_response: content,
        pickup: { resolved: !!pickup, address: pickup || "", city: "", confidence: 0.3 },
        destination: { resolved: !!destination, address: destination || "", city: "", confidence: 0.3 },
        status: "ready_to_book",
      };
    }

    // Add phone analysis to response
    parsed.phone_analysis = phoneRegion;

    return new Response(JSON.stringify(parsed), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("Address extraction error:", error);
    return new Response(JSON.stringify({ 
      error: error instanceof Error ? error.message : "Unknown error",
      status: "error"
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
