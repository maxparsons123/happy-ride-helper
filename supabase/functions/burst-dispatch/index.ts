import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

/**
 * burst-dispatch: Single edge function that splits a freeform caller utterance
 * into booking slots (name, pickup, destination, passengers, time) via Gemini,
 * then geocodes both addresses using the existing address-dispatch pipeline.
 *
 * Input:
 *   { transcript, phone?, ada_readback?, caller_area? }
 *
 * Output:
 *   { split: { name, pickup, destination, passengers, pickup_time },
 *     geocoded: <full address-dispatch result or null>,
 *     status: "ready" | "partial" | "split_only" | "error" }
 */

const SPLIT_PROMPT = `You are a data extraction engine for a taxi booking system.
Extract fields from the caller's speech. If a field is not mentioned, do NOT include it.

FIELDS:
- name: Person's name (from "it's Max", "my name is John", "I'm Sarah", "for Max", etc.)
- pickup: Full pickup address or landmark (house number + street, or POI name). Keep house numbers EXACTLY as stated.
- destination: Full destination address or landmark. Keep house numbers EXACTLY as stated.
- passengers: Integer count (extract from "for 4", "three people", "with 2 passengers", "just me" = 1)
- pickup_time: "ASAP" or time expression (extract from "now", "straight away", "in 10 minutes", "at 3:30")

RULES:
- "ASAP", "now", "straight away", "right away", "immediately" → "ASAP"
- Keep house numbers EXACTLY as stated (e.g., "52A" stays "52A", never "52-84" or "528")
- Convert spoken number words to digits for passengers (e.g., "three" → 3)
- "just me" or "myself" = 1 passenger
- Do NOT guess or infer data that isn't explicitly stated
- "from X to Y" or "X going to Y": X = pickup, Y = destination
- "pick up from" / "at" / "from" → pickup
- "going to" / "heading to" / "drop off at" → destination
- Ambiguous single address → set as pickup (most common case)

SPEECH-TO-TEXT AWARENESS:
Inputs come from live speech recognition and may contain phonetic errors.
- Apply phonetic reasoning: "mucks" → "Max", "David" → could be correct
- "Nux" → "Max", "Threw up" → could be address numbers
- Prefer well-known names and places over garbled interpretations

ADA'S READBACK (if provided):
When ada_readback is included, it contains the AI assistant's interpretation of what the caller said.
Ada may have corrected STT errors (e.g., "mucks" → "Max"). 
- For NAMES: trust Ada's readback over raw STT
- For HOUSE NUMBERS: trust raw STT over Ada's readback
- For STREET NAMES / POIs: compare both, prefer the more plausible one`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const body = await req.json();
    
    // Handle warm-up pings
    if (body.ping) {
      return new Response(JSON.stringify({ status: "warm" }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const { transcript, phone, ada_readback, caller_area } = body;

    if (!transcript?.trim()) {
      return new Response(JSON.stringify({ error: "transcript is required", status: "error" }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY is not configured");
    }

    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SUPABASE_ANON_KEY = Deno.env.get("SUPABASE_ANON_KEY");

    console.log(`🔥 [burst-dispatch] transcript="${transcript}", phone="${phone || ''}", ada_readback="${ada_readback || ''}", area="${caller_area || ''}"`);

    // ── STEP 1: Split via Gemini tool-call ──
    const userContent = ada_readback
      ? `Raw caller speech: "${transcript}"\nAda's interpretation: "${ada_readback}"`
      : `Raw caller speech: "${transcript}"`;

    const splitResponse = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash-lite",
        messages: [
          { role: "system", content: SPLIT_PROMPT },
          { role: "user", content: userContent },
        ],
        temperature: 0.0,
        tools: [{
          type: "function",
          function: {
            name: "extract_booking",
            description: "Extract booking fields from caller speech",
            parameters: {
              type: "object",
              properties: {
                name: { type: "string", description: "Caller's name" },
                pickup: { type: "string", description: "Pickup address or landmark" },
                destination: { type: "string", description: "Destination address or landmark" },
                passengers: { type: "integer", description: "Number of passengers" },
                pickup_time: { type: "string", description: "ASAP or time expression" },
              },
              required: [],
              additionalProperties: false,
            },
          },
        }],
        tool_choice: { type: "function", function: { name: "extract_booking" } },
      }),
    });

    if (!splitResponse.ok) {
      const errText = await splitResponse.text();
      console.error(`[burst-dispatch] Split AI error ${splitResponse.status}: ${errText}`);
      
      if (splitResponse.status === 429) {
        return new Response(JSON.stringify({ error: "Rate limit exceeded", status: "error" }), {
          status: 429,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      if (splitResponse.status === 402) {
        return new Response(JSON.stringify({ error: "Payment required", status: "error" }), {
          status: 402,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      
      throw new Error(`Split AI error: ${splitResponse.status}`);
    }

    const splitData = await splitResponse.json();
    const toolCall = splitData.choices?.[0]?.message?.tool_calls?.[0];
    
    if (!toolCall?.function?.arguments) {
      console.error("[burst-dispatch] No tool call in split response");
      return new Response(JSON.stringify({ error: "Split failed - no tool call", status: "error" }), {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const argsJson = typeof toolCall.function.arguments === "string"
      ? toolCall.function.arguments
      : JSON.stringify(toolCall.function.arguments);
    
    const split = JSON.parse(argsJson);
    
    // Clean up empty strings to null
    const cleanSplit = {
      name: split.name?.trim() || null,
      pickup: split.pickup?.trim() || null,
      destination: split.destination?.trim() || null,
      passengers: typeof split.passengers === "number" ? split.passengers : null,
      pickup_time: split.pickup_time?.trim() || null,
    };

    const filledCount = Object.values(cleanSplit).filter(v => v !== null).length;
    console.log(`✅ [burst-dispatch] Split: ${filledCount} fields — name=${cleanSplit.name || '–'}, pickup=${cleanSplit.pickup || '–'}, dest=${cleanSplit.destination || '–'}, pax=${cleanSplit.passengers ?? '–'}, time=${cleanSplit.pickup_time || '–'}`);

    // ── STEP 2: Geocode via address-dispatch (if we have at least one address) ──
    if (!cleanSplit.pickup && !cleanSplit.destination) {
      console.log(`[burst-dispatch] No addresses to geocode — returning split only`);
      return new Response(JSON.stringify({
        split: cleanSplit,
        geocoded: null,
        status: "split_only",
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Call address-dispatch internally
    const dispatchUrl = `${SUPABASE_URL}/functions/v1/address-dispatch`;
    const dispatchPayload: Record<string, unknown> = {
      pickup: cleanSplit.pickup || "",
      destination: cleanSplit.destination || "",
      phone: phone || "",
      pickup_time: cleanSplit.pickup_time || "",
      caller_area: caller_area || "",
    };

    // Extract house numbers from split addresses for the dispatch pipeline
    const pickupHouseMatch = cleanSplit.pickup?.match(/^(\d+[A-Za-z]?)\s/);
    const destHouseMatch = cleanSplit.destination?.match(/^(\d+[A-Za-z]?)\s/);
    if (pickupHouseMatch) dispatchPayload.pickup_house_number = pickupHouseMatch[1];
    if (destHouseMatch) dispatchPayload.destination_house_number = destHouseMatch[1];

    // Pass Ada's readback for reconciliation
    if (ada_readback) dispatchPayload.ada_readback = ada_readback;

    console.log(`📍 [burst-dispatch] Calling address-dispatch: pickup="${dispatchPayload.pickup}", dest="${dispatchPayload.destination}"`);

    const dispatchResponse = await fetch(dispatchUrl, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${SUPABASE_ANON_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(dispatchPayload),
    });

    let geocoded = null;
    if (dispatchResponse.ok) {
      geocoded = await dispatchResponse.json();
      console.log(`✅ [burst-dispatch] Geocoded: status=${geocoded.status}, area=${geocoded.detected_area}, fare=${geocoded.fare?.fare || 'N/A'}`);
    } else {
      const errText = await dispatchResponse.text();
      console.warn(`⚠️ [burst-dispatch] Geocode failed ${dispatchResponse.status}: ${errText}`);
    }

    // Determine overall status
    let status = "partial";
    if (geocoded?.status === "ready" && cleanSplit.pickup && cleanSplit.destination) {
      status = "ready";
    } else if (geocoded?.status === "clarification_needed") {
      status = "clarification_needed";
    }

    // Merge split name into result for convenience
    const result = {
      split: cleanSplit,
      geocoded,
      status,
    };

    console.log(`✅ [burst-dispatch] Final status=${status}`);

    return new Response(JSON.stringify(result), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("[burst-dispatch] Error:", error);
    return new Response(JSON.stringify({
      error: error instanceof Error ? error.message : "Unknown error",
      status: "error",
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
