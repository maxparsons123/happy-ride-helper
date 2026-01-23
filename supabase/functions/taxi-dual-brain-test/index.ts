import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// --- 1. STATE INTERFACE (stateless - state passed from client) ---
interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  pickup_time: string | null;
  lastQuestion: string;
  step: "collecting" | "summary" | "confirmed";
  conversationHistory: Array<{ role: string; content: string }>;
}

function createInitialState(): BookingState {
  return {
    pickup: null,
    destination: null,
    passengers: null,
    pickup_time: null,
    lastQuestion: "Where would you like to be picked up?",
    step: "collecting",
    conversationHistory: [],
  };
}

// --- 2. BRAIN 1: THE INTENT EXTRACTOR (Runs BEFORE Ada speaks) ---
// Maps raw transcript to structured BookingState - PRESERVES existing values
// Returns the MERGED state, not just new extractions
async function extractIntent(transcript: string, state: BookingState, apiKey: string) {
  console.log(`[BRAIN1] Extracting intent from: "${transcript}"`);
  console.log(`[BRAIN1] Ada's last question: "${state.lastQuestion}"`);
  console.log(`[BRAIN1] Current state: P=${state.pickup}, D=${state.destination}, Pax=${state.passengers}, T=${state.pickup_time}`);

  // Build current values string for the prompt
  const currentPickup = state.pickup || "NOT SET";
  const currentDestination = state.destination || "NOT SET";
  const currentPassengers = state.passengers !== null ? state.passengers : "NOT SET";
  const currentTime = state.pickup_time || "NOT SET";

  const systemPrompt = `You are a Taxi Intent Parser that PRESERVES existing data.

CURRENT BOOKING STATE (preserve these unless user explicitly changes them):
- Pickup: ${currentPickup}
- Destination: ${currentDestination}
- Passengers: ${currentPassengers}
- Pickup Time: ${currentTime}

ADA'S LAST QUESTION: "${state.lastQuestion}"

TASK:
1. Look at what Ada asked and what the user said.
2. Extract NEW information from the user's response.
3. MERGE new info with existing state - NEVER lose existing data!
4. Return the COMPLETE merged state.

FIELD MAPPING (based on Ada's question):
- If Ada asked about pickup/collection/picked up → user's address goes in "pickup"
- If Ada asked about destination/where going/where to → user's address goes in "destination"
- If Ada asked about passengers/how many/people → number goes in "passengers"
- If Ada asked about time/when → answer goes in "pickup_time" (e.g., "now", "asap", "3pm")

WORD-TO-NUMBER MAPPING:
- "one" = 1, "two" = 2, "three" = 3, "four" = 4, "five" = 5, "six" = 6, "seven" = 7, "eight" = 8

TIME KEYWORDS:
- "now", "asap", "as soon as possible", "straight away" → pickup_time: "now"

SPECIAL FLAGS:
- is_affirmative: true if user confirms ("Yes", "Correct", "That's right", "Book it", "Yeah")
- is_correction: true if user says "Actually...", "No, change...", "I meant..."

CRITICAL RULES:
- PRESERVE existing values! If pickup is "${currentPickup}", keep it unless user provides a new pickup.
- Only update a field if the user explicitly provides new info for that field.
- Return the FULL merged state, not just new data.

Return valid JSON:
{
  "pickup": "${currentPickup === "NOT SET" ? "null or new value" : currentPickup + " or new value"}",
  "destination": "${currentDestination === "NOT SET" ? "null or new value" : currentDestination + " or new value"}",
  "passengers": ${currentPassengers === "NOT SET" ? "null or new number" : currentPassengers + " or new number"},
  "pickup_time": "${currentTime === "NOT SET" ? "null or new value" : currentTime + " or new value"}",
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
    // On error, return current state to preserve data
    return { 
      pickup: state.pickup, 
      destination: state.destination, 
      passengers: state.passengers, 
      pickup_time: state.pickup_time, 
      is_affirmative: false, 
      is_correction: false 
    };
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
    
    // SAFETY: Ensure we never lose existing data due to LLM errors
    const merged = {
      pickup: parsed.pickup || state.pickup,
      destination: parsed.destination || state.destination,
      passengers: typeof parsed.passengers === 'number' ? parsed.passengers : state.passengers,
      pickup_time: parsed.pickup_time || state.pickup_time,
      is_affirmative: parsed.is_affirmative || false,
      is_correction: parsed.is_correction || false,
    };
    
    console.log(`[BRAIN1] Extracted & Merged:`, merged);
    return merged;
  } catch (e) {
    console.error(`[BRAIN1] JSON parse error:`, e, content);
    // On parse error, return current state to preserve data
    return { 
      pickup: state.pickup, 
      destination: state.destination, 
      passengers: state.passengers, 
      pickup_time: state.pickup_time, 
      is_affirmative: false, 
      is_correction: false 
    };
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

// --- 4. THE MAIN PROCESSOR (Stateless - state passed in/out) ---
async function processTurn(state: BookingState, transcript: string, apiKey: string): Promise<{
  speech: string;
  state: BookingState;
  extraction: any;
  end: boolean;
}> {
  // Add user message to conversation history
  state.conversationHistory.push({ role: "user", content: transcript });

  // Step A: Extract Data (Brain 1) - returns MERGED state
  const extraction = await extractIntent(transcript, state, apiKey);
  
  // Log correction detection
  if (extraction.is_correction) {
    console.log(`[STATE] ⚠️ CORRECTION DETECTED`);
  }
  
  // Brain 1 now returns the full merged state - apply it directly
  const pickupChanged = extraction.pickup !== state.pickup;
  const destChanged = extraction.destination !== state.destination;
  const paxChanged = extraction.passengers !== state.passengers;
  const timeChanged = extraction.pickup_time !== state.pickup_time;
  
  if (pickupChanged) {
    console.log(`[STATE] ${state.pickup ? '🔄 UPDATED' : '✅ Set'} pickup: ${extraction.pickup}`);
  }
  if (destChanged) {
    console.log(`[STATE] ${state.destination ? '🔄 UPDATED' : '✅ Set'} destination: ${extraction.destination}`);
  }
  if (paxChanged) {
    console.log(`[STATE] ${state.passengers ? '🔄 UPDATED' : '✅ Set'} passengers: ${extraction.passengers}`);
  }
  if (timeChanged) {
    console.log(`[STATE] ${state.pickup_time ? '🔄 UPDATED' : '✅ Set'} pickup_time: ${extraction.pickup_time}`);
  }
  
  // Apply merged state from Brain 1
  state.pickup = extraction.pickup;
  state.destination = extraction.destination;
  state.passengers = extraction.passengers;
  state.pickup_time = extraction.pickup_time;
  
  // If correction detected during summary, go back to collecting to re-confirm
  if (extraction.is_correction && state.step === "summary") {
    console.log(`[STATE] Correction during summary - resetting to collecting for re-confirmation`);
    state.step = "collecting";
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
    console.log(`[MAIN] Booking confirmed`);
    
    // Add a graceful closing message to Ada's response
    const closingTips = [
      "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
      "Next time, feel free to book your taxi using a WhatsApp voice message.",
      "You can always book again by simply sending us a voice note on WhatsApp."
    ];
    const randomTip = closingTips[Math.floor(Math.random() * closingTips.length)];
    const closingMessage = ` You'll receive the booking details and ride updates via WhatsApp. ${randomTip} Thank you for trying the Taxibot demo, and have a safe journey.`;
    
    // Append closing to Ada's speech if not already there
    if (!adaResponse.content.includes("safe journey")) {
      adaResponse.content += closingMessage;
    }
    
    shouldEnd = true;
    console.log(`[MAIN] 🛡️ Graceful close: end=true with full goodbye message`);
  }

  // Save question for next turn context
  state.lastQuestion = adaResponse.content;
  state.conversationHistory.push({ role: "assistant", content: adaResponse.content });

  return { 
    speech: adaResponse.content, 
    state: { ...state }, // Clone to return
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
      mode: "stateless" 
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Main process endpoint
  if (req.method === "POST") {
    try {
      const body = await req.json();
      const { transcript, state: clientState } = body;
      
      if (!transcript) {
        return new Response(JSON.stringify({ error: "Missing transcript" }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      // Use client state if provided, otherwise create fresh state
      const state: BookingState = clientState || createInitialState();
      
      console.log(`[MAIN] Processing turn. Current state:`, {
        pickup: state.pickup,
        destination: state.destination,
        passengers: state.passengers,
        pickup_time: state.pickup_time,
        step: state.step,
        lastQuestion: state.lastQuestion
      });

      const apiKey = Deno.env.get("LOVABLE_API_KEY");
      if (!apiKey) {
        return new Response(JSON.stringify({ error: "API key not configured" }), {
          status: 500,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      const startTime = Date.now();
      const result = await processTurn(state, transcript, apiKey);
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
            pickup_time: !!result.state.pickup_time,
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
