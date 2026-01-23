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
// Extracts ALL booking fields from transcript - supports free-form input
// Returns the MERGED state, preserving existing values
async function extractIntent(transcript: string, state: BookingState, apiKey: string) {
  console.log(`[BRAIN1] Extracting intent from: "${transcript}"`);
  console.log(`[BRAIN1] Current state: P=${state.pickup}, D=${state.destination}, Pax=${state.passengers}, T=${state.pickup_time}`);

  // Build current values for the prompt
  const currentPickup = state.pickup || "NOT SET";
  const currentDestination = state.destination || "NOT SET";
  const currentPassengers = state.passengers !== null ? String(state.passengers) : "NOT SET";
  const currentTime = state.pickup_time || "NOT SET";

  const systemPrompt = `You are a Taxi Booking Data Extractor. Extract ALL booking information from the user's message.

CURRENT BOOKING STATE (preserve unless user provides new data):
- Pickup: ${currentPickup}
- Destination: ${currentDestination}  
- Passengers: ${currentPassengers}
- Pickup Time: ${currentTime}

ADA'S LAST QUESTION: "${state.lastQuestion}"

YOUR TASK:
Extract ALL booking fields mentioned in the user's message. Users may provide:
- Just one piece of info: "52A David Road"
- Multiple fields: "Pick me up from 52A David Road, going to Manchester, 3 passengers"
- Everything at once: "I need a taxi from 52A David Road to the airport for 2 people at 3pm"

EXTRACTION RULES:
1. Look for PICKUP indicators: "from", "pick me up from", "at", "collection from", "I'm at"
2. Look for DESTINATION indicators: "to", "going to", "destination", "heading to", "drop at"
3. Look for PASSENGERS: any number + "passengers", "people", "of us", or just a number when asked
4. Look for TIME: "now", "asap", "at [time]", "in [X] minutes", specific times like "3pm"
5. Look for AFFIRMATIVE: "yes", "correct", "that's right", "book it", "confirmed"
6. Look for CORRECTIONS: "actually", "no change", "I meant", "not that"

WORD-TO-NUMBER MAP: one=1, two=2, three=3, four=4, five=5, six=6, seven=7, eight=8

CONTEXT-AWARE EXTRACTION:
- If Ada asked about pickup and user says an address → it's the pickup
- If Ada asked about destination and user says an address → it's the destination
- If Ada asked about passengers and user says a number → it's passengers
- If Ada asked about time and user gives a time → it's pickup_time

CRITICAL: 
- PRESERVE existing values! Only update fields the user explicitly mentions.
- Extract EVERYTHING mentioned - don't ignore extra info.
- If user provides pickup AND destination in one sentence, extract BOTH.

Return JSON (preserve existing values, update with new info):
{
  "pickup": ${currentPickup === "NOT SET" ? "null" : `"${currentPickup}"`},
  "destination": ${currentDestination === "NOT SET" ? "null" : `"${currentDestination}"`},
  "passengers": ${currentPassengers === "NOT SET" ? "null" : currentPassengers},
  "pickup_time": ${currentTime === "NOT SET" ? "null" : `"${currentTime}"`},
  "is_affirmative": false,
  "is_correction": false,
  "fields_extracted": []
}

The fields_extracted array should list which fields you found: ["pickup", "destination", "passengers", "pickup_time"]`;

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
        { role: "user", content: `User said: "${transcript}"` }
      ],
      max_tokens: 300,
    }),
  });

  if (!res.ok) {
    console.error(`[BRAIN1] API error: ${res.status}`);
    return { 
      pickup: state.pickup, 
      destination: state.destination, 
      passengers: state.passengers, 
      pickup_time: state.pickup_time, 
      is_affirmative: false, 
      is_correction: false,
      fields_extracted: []
    };
  }

  const data = await res.json();
  const content = data.choices?.[0]?.message?.content || "{}";
  
  try {
    let jsonStr = content;
    const jsonMatch = content.match(/```(?:json)?\s*([\s\S]*?)```/);
    if (jsonMatch) jsonStr = jsonMatch[1].trim();
    const rawMatch = jsonStr.match(/\{[\s\S]*\}/);
    if (rawMatch) jsonStr = rawMatch[0];
    
    const parsed = JSON.parse(jsonStr);
    
    // SAFETY: Merge with existing state - never lose data
    const merged = {
      pickup: parsed.pickup || state.pickup,
      destination: parsed.destination || state.destination,
      passengers: typeof parsed.passengers === 'number' ? parsed.passengers : state.passengers,
      pickup_time: parsed.pickup_time || state.pickup_time,
      is_affirmative: parsed.is_affirmative || false,
      is_correction: parsed.is_correction || false,
      fields_extracted: parsed.fields_extracted || [],
    };
    
    console.log(`[BRAIN1] Extracted & Merged:`, merged);
    if (merged.fields_extracted.length > 1) {
      console.log(`[BRAIN1] 🚀 FREE-FORM: Extracted ${merged.fields_extracted.length} fields at once!`);
    }
    return merged;
  } catch (e) {
    console.error(`[BRAIN1] JSON parse error:`, e, content);
    return { 
      pickup: state.pickup, 
      destination: state.destination, 
      passengers: state.passengers, 
      pickup_time: state.pickup_time, 
      is_affirmative: false, 
      is_correction: false,
      fields_extracted: []
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
