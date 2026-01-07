import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Voice-optimized prompt - shorter responses, natural speech patterns
const VOICE_SYSTEM_PROMPT = `You are a friendly Taxi Dispatcher for "Imtech Taxi" on a PHONE CALL. Keep responses SHORT and conversational for voice.

VOICE STYLE:
- Max 1-2 short sentences per response
- Use natural speech: "Brilliant!", "Lovely!", "Right then!"
- No bullet points or lists - just speak naturally
- Pause-friendly: use commas for natural speech rhythm

FLOW:
1. PICKUP given → "Brilliant! [pickup], yeah?"
2. Confirmed → "And where to?"
3. DESTINATION given → "Lovely! [destination]. How many passengers?"
4. PASSENGERS given → "Right! [pickup] to [destination], [X] passengers. About [X] minutes, roughly [£X]. Book it?"
5. CONFIRMED → "Sorted! Your cab's on its way. Cheers!"

PRICING: city £15-25, airport £45, 6-seater add £5
ETA: Always 5-8 minutes

CRITICAL: Respond with ONLY valid JSON:
{"response":"your short message","pickup":"value or null","destination":"value or null","passengers":"number or null","status":"collecting or confirmed"}`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { text, currentBooking, call_id } = await req.json();
    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY is not configured");
    }

    console.log(`[${call_id || 'unknown'}] Incoming voice text: "${text}"`);

    // Build context with current booking state
    let contextMessage = "";
    if (currentBooking) {
      contextMessage = `\n\nCurrent state: pickup=${currentBooking.pickup || "?"}, destination=${currentBooking.destination || "?"}, passengers=${currentBooking.passengers || "?"}`;
    }

    const startTime = Date.now();

    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash-lite", // Fastest model for voice
        messages: [
          { role: "system", content: VOICE_SYSTEM_PROMPT + contextMessage },
          { role: "user", content: text },
        ],
        max_tokens: 150, // Very short for voice
      }),
    });

    const latency = Date.now() - startTime;
    console.log(`[${call_id || 'unknown'}] AI response in ${latency}ms`);

    if (!response.ok) {
      const errorText = await response.text();
      console.error("AI gateway error:", response.status, errorText);
      
      // Fallback response for voice
      return new Response(JSON.stringify({ 
        response: "Sorry, having a bit of trouble. Can you say that again?",
        status: "collecting",
        latency_ms: latency
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const data = await response.json();
    const aiContent = data.choices?.[0]?.message?.content || "";
    
    console.log(`[${call_id || 'unknown'}] Raw AI response:`, aiContent);

    // Parse the JSON response
    let parsedResponse;
    try {
      let jsonStr = aiContent;
      const jsonMatch = aiContent.match(/```(?:json)?\s*([\s\S]*?)```/);
      if (jsonMatch) {
        jsonStr = jsonMatch[1].trim();
      }
      const rawJsonMatch = jsonStr.match(/\{[\s\S]*\}/);
      if (rawJsonMatch) {
        jsonStr = rawJsonMatch[0];
      }
      parsedResponse = JSON.parse(jsonStr);
    } catch (parseError) {
      console.error("Failed to parse AI response:", parseError);
      parsedResponse = {
        response: aiContent.replace(/```json|```/g, '').trim() || "Hello! Where can I pick you up?",
        pickup: currentBooking?.pickup || null,
        destination: currentBooking?.destination || null,
        passengers: currentBooking?.passengers || null,
        status: "collecting",
      };
    }

    // Add metadata for Asterisk
    parsedResponse.latency_ms = latency;
    parsedResponse.call_id = call_id;

    console.log(`[${call_id || 'unknown'}] Response:`, parsedResponse.response);

    return new Response(JSON.stringify(parsedResponse), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (error) {
    console.error("Taxi voice error:", error);
    return new Response(JSON.stringify({ 
      response: "Sorry, something went wrong. Please try again.",
      status: "collecting",
      error: error instanceof Error ? error.message : "Unknown error" 
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
