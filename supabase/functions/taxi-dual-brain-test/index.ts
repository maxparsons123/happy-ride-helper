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
  pickup_time: string | null;
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

// --- 2. BRAIN 1: THE INTENT EXTRACTOR (Runs BEFORE Ada speaks) ---
// Maps raw transcript to structured BookingState
async function extractIntent(transcript: string, state: BookingState, apiKey: string) {
  console.log(`[BRAIN1] Extracting intent from: "${transcript}"`);
  console.log(`[BRAIN1] Ada's last question: "${state.lastQuestion}"`);

  const systemPrompt = `You are a Taxi Intent Parser.

CONTEXT:
- Ada's Last Question: "${state.lastQuestion}"
- Current Data: Pickup: ${state.pickup || 'null'}, Destination: ${state.destination || 'null'}, Passengers: ${state.passengers || 'null'}, Time: ${state.pickup_time || 'null'}

TASK:
1. Identify if the user is answering the specific question Ada asked.
2. Extract the response into the CORRECT field based on what Ada asked:
   - If Ada asked about pickup/collection → put address in "pickup"
   - If Ada asked about destination/where going → put address in "destination"  
   - If Ada asked about passengers/how many → put number in "passengers"
   - If Ada asked about time/when → put time in "pickup_time" (e.g., "now", "asap", "in 10 minutes", "3pm")
3. Detect 'is_affirmative' (true if user says "Yes", "Correct", "That's right", "Book it", "Yeah", "Ok").
4. Detect 'is_correction' (true if user says "No", "Wait", "Change that", "Actually", "Not quite").

WORD-TO-NUMBER MAPPING:
- "one" = 1, "two" = 2, "three" = 3, "four" = 4, "five" = 5, "six" = 6, "seven" = 7, "eight" = 8

TIME KEYWORDS:
- "now", "asap", "as soon as possible", "straight away" → pickup_time: "now"
- "in X minutes", "at X o'clock", "around X" → pickup_time: as spoken

CRITICAL RULES:
- Only fill the field that Ada was asking about
- If Ada asked "Where to?" and user says "7 Russell Street" → destination: "7 Russell Street" (NOT pickup!)
- If Ada asked "How many people?" and user says "three" → passengers: 3 (NOT an address field!)
- If user provides info out of order (e.g., destination when asked for pickup), still put it in the correct semantic field

Return valid JSON only:
{
  "pickup": null,
  "destination": null,
  "passengers": null,
  "pickup_time": null,
  "is_affirmative": false,
  "is_correction": false
}`;

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
        { role: "user", content: `User just said: "${transcript}"` }
      ],
      max_tokens: 200,
    }),
  });

  if (!res.ok) {
    console.error(`[BRAIN1] API error: ${res.status}`);
    return { pickup: null, destination: null, passengers: null, is_affirmative: false, is_correction: false };
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
    console.error(`[BRAIN1] JSON parse error:`, e, content);
    return { pickup: null, destination: null, passengers: null, is_affirmative: false, is_correction: false };
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
- Pickup Time: ${state.pickup_time || 'NOT SET'}
- Step: ${state.step}

STRICT RULES:
1. ONLY use the data shown above. NEVER use placeholders like [number] or [address].
2. If a field is "NOT SET", you MUST ask for it. Do not skip or assume.
3. Ask ONE question at a time - do not bundle questions.
4. Collection order: pickup → destination → passengers → time
5. If ALL fields are set and step is 'collecting', give a summary and ask "Is that correct?"
6. If user confirms in 'summary' step, say the taxi is booked and will arrive shortly.
7. Be warm and concise. No filler words like "Got it" or "Great".

${forceConfirm ? 'USER HAS CONFIRMED. Book the taxi now.' : ''}`;

  console.log(`[BRAIN2] Generating speech. State:`, { 
    pickup: state.pickup, 
    destination: state.destination, 
    passengers: state.passengers,
    pickup_time: state.pickup_time,
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
      pickup_time: null,
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
  if (extraction.pickup_time && extraction.pickup_time !== "null") {
    state.pickup_time = extraction.pickup_time;
    console.log(`[STATE] Updated pickup_time: ${state.pickup_time}`);
  }
  
  // Check if ready for summary (all 4 fields required)
  if (state.pickup && state.destination && state.passengers && state.pickup_time && state.step === "collecting") {
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
