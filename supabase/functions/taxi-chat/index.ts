import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_PROMPT = `You are a Taxi Dispatcher for "Imtech Taxi". Gather: pickup, destination, passengers.

RULES:
- Be concise and natural
- Answer questions directly without forcing confirmation
- Only ask for confirmation ONCE when ALL 3 details (pickup, destination, passengers) are complete
- ONLY set status="confirmed" AFTER user explicitly says yes/correct/confirm
- Keep status="collecting" until user confirms

INFO: ETA 5-8min, city trips £15-25, airport £45, 24/7, 4-seater saloons & 6-seater vans

CRITICAL: Respond with ONLY valid JSON:
{"response":"your message","pickup":"location or null","destination":"location or null","passengers":"number or null","status":"collecting or confirmed","intent":"booking"}`;

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { messages, currentBooking } = await req.json();
    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    
    if (!LOVABLE_API_KEY) {
      throw new Error("LOVABLE_API_KEY is not configured");
    }

    // Build context with current booking state
    let contextMessage = "";
    if (currentBooking) {
      contextMessage = `\n\nCurrent booking state:
- Pickup: ${currentBooking.pickup || "Not provided"}
- Destination: ${currentBooking.destination || "Not provided"}
- Passengers: ${currentBooking.passengers || "Not provided"}

Use this information to maintain context and don't ask for info already provided.`;
    }

    console.log("Sending request to AI gateway...");
    const startTime = Date.now();

    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash", // Fast and balanced
        messages: [
          { role: "system", content: SYSTEM_PROMPT + contextMessage },
          ...messages,
        ],
        max_tokens: 300, // Reduced for faster response
      }),
    });

    console.log(`AI response received in ${Date.now() - startTime}ms`);

    if (!response.ok) {
      const errorText = await response.text();
      console.error("AI gateway error:", response.status, errorText);
      
      if (response.status === 429) {
        return new Response(JSON.stringify({ error: "Rate limit exceeded. Please try again in a moment." }), {
          status: 429,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      if (response.status === 402) {
        return new Response(JSON.stringify({ error: "Service unavailable. Please try again later." }), {
          status: 402,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      return new Response(JSON.stringify({ error: "Failed to get AI response" }), {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const data = await response.json();
    const aiContent = data.choices?.[0]?.message?.content || "";
    
    console.log("Raw AI response:", aiContent);

    // Parse the JSON response from the AI
    let parsedResponse;
    try {
      // Extract JSON from the response (handle markdown code blocks)
      let jsonStr = aiContent;
      const jsonMatch = aiContent.match(/```(?:json)?\s*([\s\S]*?)```/);
      if (jsonMatch) {
        jsonStr = jsonMatch[1].trim();
      }
      // Also try to find raw JSON object
      const rawJsonMatch = jsonStr.match(/\{[\s\S]*\}/);
      if (rawJsonMatch) {
        jsonStr = rawJsonMatch[0];
      }
      parsedResponse = JSON.parse(jsonStr);
    } catch (parseError) {
      // If parsing fails, return the raw response as a message
      console.error("Failed to parse AI response as JSON:", aiContent, parseError);
      parsedResponse = {
        response: aiContent.replace(/```json|```/g, '').trim() || "I'm here to help you book a taxi. Where would you like to be picked up?",
        pickup: null,
        destination: null,
        passengers: null,
        status: "collecting",
        intent: "general",
      };
    }

    return new Response(JSON.stringify(parsedResponse), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (error) {
    console.error("Taxi chat error:", error);
    return new Response(JSON.stringify({ 
      error: error instanceof Error ? error.message : "Unknown error" 
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
