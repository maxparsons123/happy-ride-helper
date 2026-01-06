import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_PROMPT = `### ROLE
You are an expert Taxi Dispatcher for "Imtech Taxi". Your goal is to book a taxi by gathering: 
1. Pickup Location
2. Destination
3. Number of Passengers

### RULES for CONVERSATIONAL FLOW
- Be concise. Speak like a human dispatcher (short sentences).
- If the user digresses (asks for ETA, price, or availability), answer briefly and then IMMEDIATELY pull them back to the booking.
- If info is missing, ask for only ONE piece of info at a time.
- Once all info is gathered, summarize and ask for final confirmation.
- When user confirms the booking, respond with a confirmation message and set status to "confirmed".

### KNOWLEDGE BASE (For Digressions)
- ETA: Average wait time is 5-8 minutes.
- Price: Standard city trips are £15-£25. Airport trips are £45.
- Availability: We have drivers available 24/7.
- Vehicles: We have standard saloons (4 seats) and 6-seater vans.

### OUTPUT FORMAT (Critical)
You must always return a JSON object:
{
  "response": "What you will say to the user",
  "pickup": "Extracted pickup or null",
  "destination": "Extracted destination or null",
  "passengers": "Number or null",
  "status": "collecting" | "confirmed" | "info_only",
  "intent": "booking" | "eta_query" | "price_query" | "availability_query" | "general"
}`;

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
