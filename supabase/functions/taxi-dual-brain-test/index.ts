import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// --- 1. STATE & MEMORY SETUP ---
interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  lastQuestion: string;
  step: "collecting" | "summary" | "confirmed";
  lastActive: number;
  conversationHistory: Array<{ role: string; content: string }>;
}

const callMemory = new Map<string, BookingState>();
const CALL_TIMEOUT_MS = 10 * 60 * 1000; // 10 mins

// Cleanup reaper
setInterval(() => {
  const now = Date.now();
  for (const [id, s] of callMemory.entries()) {
    if (now - s.lastActive > CALL_TIMEOUT_MS) {
      console.log(`[CLEANUP] Removing stale session: ${id}`);
      callMemory.delete(id);
    }
  }
}, 60000);

// --- 2. BRAIN 1: THE INTENT EXTRACTOR (Context-Aware Routing) ---
async function extractIntent(transcript: string, state: BookingState, apiKey: string) {
  // Determine what field Ada was asking about
  const lastQ = state.lastQuestion.toLowerCase();
  let expectedField: "pickup" | "destination" | "passengers" | "confirmation" | "unknown" = "unknown";
  
  if (lastQ.includes("pick") && lastQ.includes("up") || lastQ.includes("from where") || lastQ.includes("where are you")) {
    expectedField = "pickup";
  } else if (lastQ.includes("destination") || lastQ.includes("going") || lastQ.includes("where to") || lastQ.includes("off to") || lastQ.includes("heading")) {
    expectedField = "destination";
  } else if (lastQ.includes("passenger") || lastQ.includes("people") || lastQ.includes("how many")) {
    expectedField = "passengers";
  } else if (lastQ.includes("correct") || lastQ.includes("confirm") || lastQ.includes("right")) {
    expectedField = "confirmation";
  }

  console.log(`[BRAIN1] Expected field based on last question: ${expectedField}`);
  console.log(`[BRAIN1] Last question was: "${state.lastQuestion}"`);

  const prompt = `
You are extracting booking data from a taxi conversation.

CRITICAL CONTEXT:
- Ada just asked: "${state.lastQuestion}"
- This means Ada was asking for: ${expectedField.toUpperCase()}
- The user responded: "${transcript}"

Current booking state:
- Pickup: ${state.pickup || 'NOT SET'}
- Destination: ${state.destination || 'NOT SET'}
- Passengers: ${state.passengers || 'NOT SET'}

YOUR TASK:
Based on what Ada asked, extract the user's response into the CORRECT field.

ROUTING RULES:
1. If Ada asked about PICKUP, put any address/location in "pickup"
2. If Ada asked about DESTINATION, put any address/location in "destination"  
3. If Ada asked about PASSENGERS, put any number (spoken or digit) in "passengers"
4. If Ada asked for CONFIRMATION, check if user said yes/correct/ok → set is_affirmative=true

EXAMPLES:
- Ada asked "Where to?" + User said "7 Russell Street" → destination: "7 Russell Street"
- Ada asked "How many people?" + User said "3" → passengers: 3
- Ada asked "Is that correct?" + User said "Yes" → is_affirmative: true

Return JSON only:
{
  "pickup": null,
  "destination": null,
  "passengers": null,
  "is_affirmative": false
}

Fill in ONLY the field that matches what Ada was asking about.`;

  console.log(`[BRAIN1] Extracting intent from: "${transcript}"`);
  
  const res = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${apiKey}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      model: "google/gemini-2.5-flash",
      messages: [
        { role: "system", content: "You are a precise data extractor. Return only valid JSON." },
        { role: "user", content: prompt }
      ],
      max_tokens: 200,
    }),
  });

  if (!res.ok) {
    console.error(`[BRAIN1] API error: ${res.status}`);
    return { pickup: null, destination: null, passengers: null, is_affirmative: false };
  }

  const data = await res.json();
  const content = data.choices?.[0]?.message?.content || "{}";
  
  try {
    // Extract JSON from potential markdown
    let jsonStr = content;
    const jsonMatch = content.match(/```(?:json)?\s*([\s\S]*?)```/);
    if (jsonMatch) jsonStr = jsonMatch[1].trim();
    const rawMatch = jsonStr.match(/\{[\s\S]*\}/);
    if (rawMatch) jsonStr = rawMatch[0];
    
    const parsed = JSON.parse(jsonStr);
    console.log(`[BRAIN1] Extracted:`, parsed);
    return parsed;
  } catch (e) {
    console.error(`[BRAIN1] JSON parse error:`, e);
    return { pickup: null, destination: null, passengers: null, is_affirmative: false };
  }
}

// --- 3. BRAIN 2: THE ADA CONTROLLER (Speech Generation) ---
async function getAdaSpeech(transcript: string, state: BookingState, isAffirmative: boolean, apiKey: string) {
  // Logic Enforcement: If state is summary and they said 'Yes', confirm booking
  let userMessage = transcript;
  let forceConfirm = false;
  
  if (isAffirmative && state.step === "summary") {
    userMessage = "The user has confirmed everything is correct. Confirm the booking.";
    forceConfirm = true;
  }

  const systemPrompt = `You are Ada, a friendly London taxi dispatcher.

CURRENT BOOKING STATE:
- Pickup: ${state.pickup || 'NOT SET'}
- Destination: ${state.destination || 'NOT SET'}  
- Passengers: ${state.passengers || 'NOT SET'}
- Step: ${state.step}

STRICT RULES:
1. ONLY use the data shown above. NEVER use placeholders like [number] or [address].
2. If a field is "NOT SET", you MUST ask for it. Do not skip or assume.
3. Ask ONE question at a time - do not bundle questions.
4. If ALL fields are set and step is 'collecting', give a summary and ask "Is that correct?"
5. If user confirms in 'summary' step, say the taxi is booked and will arrive shortly.
6. Be warm and concise. No filler words like "Got it" or "Great".

${forceConfirm ? 'USER HAS CONFIRMED. Book the taxi now.' : ''}`;

  console.log(`[BRAIN2] Generating speech. State:`, { 
    pickup: state.pickup, 
    destination: state.destination, 
    passengers: state.passengers,
    step: state.step,
    isAffirmative 
  });

  const res = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${apiKey}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      model: "google/gemini-2.5-flash",
      messages: [
        { role: "system", content: systemPrompt },
        ...state.conversationHistory.slice(-6), // Last 3 turns for context
        { role: "user", content: userMessage }
      ],
      max_tokens: 150,
    }),
  });

  if (!res.ok) {
    console.error(`[BRAIN2] API error: ${res.status}`);
    return { content: "I'm having trouble. Could you repeat that?", shouldConfirm: false };
  }

  const data = await res.json();
  const content = data.choices?.[0]?.message?.content || "Could you repeat that?";
  
  console.log(`[BRAIN2] Ada says: "${content}"`);
  
  return { 
    content, 
    shouldConfirm: forceConfirm && state.pickup && state.destination && state.passengers 
  };
}

// --- 4. THE MAIN PROCESSOR ---
async function processTurn(callId: string, transcript: string, apiKey: string): Promise<{
  speech: string;
  state: BookingState;
  extraction: any;
  end: boolean;
}> {
  // Get or Init State
  if (!callMemory.has(callId)) {
    console.log(`[MAIN] New session: ${callId}`);
    callMemory.set(callId, {
      pickup: null,
      destination: null,
      passengers: null,
      lastQuestion: "Where would you like to be picked up?",
      step: "collecting",
      lastActive: Date.now(),
      conversationHistory: [],
    });
  }
  
  const state = callMemory.get(callId)!;
  state.lastActive = Date.now();
  state.conversationHistory.push({ role: "user", content: transcript });

  // Step A: Extract Data (Brain 1)
  const extraction = await extractIntent(transcript, state, apiKey);
  
  // Update RAM State - only if values are truthy
  if (extraction.pickup && extraction.pickup !== "null") {
    state.pickup = extraction.pickup;
    console.log(`[STATE] Updated pickup: ${state.pickup}`);
  }
  if (extraction.destination && extraction.destination !== "null") {
    state.destination = extraction.destination;
    console.log(`[STATE] Updated destination: ${state.destination}`);
  }
  if (extraction.passengers && typeof extraction.passengers === 'number') {
    state.passengers = extraction.passengers;
    console.log(`[STATE] Updated passengers: ${state.passengers}`);
  }
  
  // Check if ready for summary
  if (state.pickup && state.destination && state.passengers && state.step === "collecting") {
    console.log(`[STATE] All fields collected, moving to summary`);
    state.step = "summary";
  }

  // Step B: Generate Response (Brain 2)
  const adaResponse = await getAdaSpeech(transcript, state, extraction.is_affirmative || false, apiKey);

  // Step C: Handle Confirmation
  let shouldEnd = false;
  if (adaResponse.shouldConfirm) {
    state.step = "confirmed";
    console.log(`[MAIN] Booking confirmed for ${callId}`);
    shouldEnd = true;
  }

  // Save question for next turn context
  state.lastQuestion = adaResponse.content;
  state.conversationHistory.push({ role: "assistant", content: adaResponse.content });
  callMemory.set(callId, state);

  return { 
    speech: adaResponse.content, 
    state: { ...state }, // Clone to avoid mutation
    extraction,
    end: shouldEnd 
  };
}

// --- 5. HTTP SERVER ---
serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const url = new URL(req.url);
  
  // Health check
  if (url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ 
      status: "ok", 
      activeSessions: callMemory.size 
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Reset session endpoint
  if (req.method === "DELETE") {
    const { callId } = await req.json();
    if (callId && callMemory.has(callId)) {
      callMemory.delete(callId);
      return new Response(JSON.stringify({ success: true, message: "Session cleared" }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
    return new Response(JSON.stringify({ success: false, message: "Session not found" }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Main process endpoint
  if (req.method === "POST") {
    try {
      const { callId, transcript } = await req.json();
      
      if (!callId || !transcript) {
        return new Response(JSON.stringify({ error: "Missing callId or transcript" }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      const apiKey = Deno.env.get("LOVABLE_API_KEY");
      if (!apiKey) {
        return new Response(JSON.stringify({ error: "API key not configured" }), {
          status: 500,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      const startTime = Date.now();
      const result = await processTurn(callId, transcript, apiKey);
      const processingTime = Date.now() - startTime;

      return new Response(JSON.stringify({
        ...result,
        processingTime,
        debug: {
          brain1_extraction: result.extraction,
          currentStep: result.state.step,
          fieldsCollected: {
            pickup: !!result.state.pickup,
            destination: !!result.state.destination,
            passengers: !!result.state.passengers,
          }
        }
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });

    } catch (error) {
      console.error("[ERROR]", error);
      return new Response(JSON.stringify({ 
        error: error instanceof Error ? error.message : "Unknown error" 
      }), {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
  }

  return new Response("Method not allowed", { status: 405, headers: corsHeaders });
});
