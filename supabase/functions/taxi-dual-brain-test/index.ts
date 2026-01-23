import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// ================= BOOKING STATE =================
interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  pickup_time: string | null;
  luggage: string | null;
  special_requests: string | null;
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
    luggage: null,
    special_requests: null,
    lastQuestion: "Where would you like to be picked up?",
    step: "collecting",
    conversationHistory: [],
  };
}

// ================= TOOL DEFINITION (Like C# ToolDefinition) =================
const TAXI_EXTRACTION_TOOL = {
  type: "function",
  function: {
    name: "extract_booking",
    description: "Extract taxi booking data from user message",
    parameters: {
      type: "object",
      properties: {
        intent: {
          type: "string",
          enum: ["new_booking", "update_booking", "confirm_booking", "cancel_booking", "get_status", "other"],
          description: "The user's intent"
        },
        pickup_location: {
          type: "string",
          nullable: true,
          description: "Pickup address exactly as spoken"
        },
        dropoff_location: {
          type: "string",
          nullable: true,
          description: "Destination address exactly as spoken"
        },
        pickup_time: {
          type: "string",
          nullable: true,
          description: "When to pickup: 'now', 'ASAP', or specific time"
        },
        number_of_passengers: {
          type: "integer",
          nullable: true,
          description: "Number of passengers"
        },
        luggage: {
          type: "string",
          nullable: true,
          description: "Luggage details exactly as spoken"
        },
        special_requests: {
          type: "string",
          nullable: true,
          description: "Any driver instructions or special requests"
        },
        is_affirmative: {
          type: "boolean",
          description: "True if user confirms (yes, correct, that's right)"
        },
        is_correction: {
          type: "boolean",
          description: "True if user is correcting info (actually, no change to)"
        },
        fields_extracted: {
          type: "array",
          items: { type: "string" },
          description: "List of fields extracted this turn"
        }
      },
      required: ["intent", "is_affirmative", "is_correction", "fields_extracted"]
    }
  }
};

// ================= GET LONDON TIME =================
function getLondonTime(): string {
  return new Date().toLocaleString("en-GB", {
    timeZone: "Europe/London",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  });
}

// ================= BRAIN 1: EXTRACTION WITH TOOL CALLING =================
async function extractIntent(transcript: string, state: BookingState, isUpdate: boolean, apiKey: string) {
  console.log(`[BRAIN1] Extracting from: "${transcript}"`);
  console.log(`[BRAIN1] Mode: ${isUpdate ? "UPDATE" : "NEW"}, State: P=${state.pickup}, D=${state.destination}, Pax=${state.passengers}`);

  const now = getLondonTime();
  
  // Build the appropriate prompt based on mode
  const systemPrompt = isUpdate 
    ? buildUpdatePrompt(now, state)
    : buildNewBookingPrompt(now, state);

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
        { role: "user", content: transcript }
      ],
      tools: [TAXI_EXTRACTION_TOOL],
      tool_choice: { type: "function", function: { name: "extract_booking" } },
    }),
  });

  if (!res.ok) {
    console.error(`[BRAIN1] API error: ${res.status}`);
    return createEmptyExtraction(state);
  }

  const data = await res.json();
  
  try {
    // Parse tool call arguments (like C# ParseToolArguments)
    const toolCall = data.choices?.[0]?.message?.tool_calls?.[0];
    if (!toolCall?.function?.arguments) {
      console.error(`[BRAIN1] No tool call in response`);
      return createEmptyExtraction(state);
    }

    const args = JSON.parse(toolCall.function.arguments);
    console.log(`[BRAIN1] Tool extracted:`, args);
    
    // Merge with existing state (like C# Mapper.Apply)
    const merged = mergeExtraction(args, state);
    console.log(`[BRAIN1] Merged state:`, merged);
    
    if (merged.fields_extracted.length > 1) {
      console.log(`[BRAIN1] 🚀 FREE-FORM: Extracted ${merged.fields_extracted.length} fields at once!`);
    }
    
    return merged;
  } catch (e) {
    console.error(`[BRAIN1] Parse error:`, e);
    return createEmptyExtraction(state);
  }
}

// ================= NEW BOOKING PROMPT =================
function buildNewBookingPrompt(now: string, state: BookingState): string {
  return `You are a STRICT taxi booking AI for voice calls.
Current time (London): ${now}
Ada's last question: "${state.lastQuestion}"

CURRENT BOOKING STATE:
- Pickup: ${state.pickup || "NOT SET"}
- Destination: ${state.destination || "NOT SET"}
- Passengers: ${state.passengers ?? "NOT SET"}
- Pickup Time: ${state.pickup_time || "NOT SET"}
- Luggage: ${state.luggage || "NOT SET"}
- Special Requests: ${state.special_requests || "NOT SET"}

==================================================
EXTRACTION RULES (CRITICAL)
==================================================
Extract ALL booking fields mentioned. User may provide:
- Just one piece: "52A David Road"
- Multiple fields: "Pick me up from 52A David Road, going to Manchester, 3 passengers"
- Everything at once: "Taxi from 52A David Road to the airport for 2 people at 3pm"

FIELD DETECTION:
• PICKUP: "from", "pick me up from", "at", "collection from", "I'm at"
• DESTINATION: "to", "going to", "destination", "heading to", "drop at"
• PASSENGERS: number + "passengers", "people", "of us", or number alone when asked
• TIME: "now", "asap", "at [time]", "in X minutes", specific times
• LUGGAGE: "bags", "suitcase", "luggage", "cases" - extract EXACTLY as spoken
• SPECIAL: driver instructions, preferences, accessibility needs

CONTEXT-AWARE MAPPING:
If Ada asked about pickup and user says an address → it's pickup_location
If Ada asked about destination and user says an address → it's dropoff_location
If Ada asked about passengers and user says a number → it's passengers
If Ada asked about time and user gives a time → it's pickup_time

WORD-TO-NUMBER: one=1, two=2, three=3, four=4, five=5, six=6, seven=7, eight=8

TIME RULES:
• "now", "asap", "straight away" → pickup_time = "now"
• "tonight" → 21:00, "this evening" → 19:00, "morning" → 09:00
• "in X minutes" → add to current time
• Specific times: "3pm" → "15:00"

AFFIRMATIVE DETECTION:
is_affirmative = true if: "yes", "correct", "that's right", "book it", "confirmed", "yeah"

CORRECTION DETECTION:
is_correction = true if: "actually", "no change", "I meant", "not that", "wait"

ADDRESS RULES:
• Return addresses EXACTLY as spoken - no corrections, no postcodes added
• If user says "my location" or "here" → pickup_location = "by_gps"
• Remove leading articles: "the high street" → "high street"

CRITICAL: 
- Extract EVERYTHING mentioned in one sentence
- fields_extracted must list ALL fields found: ["pickup_location", "dropoff_location", etc.]
- intent = "new_booking" for new data, "confirm_booking" if user confirms`;
}

// ================= UPDATE BOOKING PROMPT =================
function buildUpdatePrompt(now: string, state: BookingState): string {
  return `You are a STRICT taxi booking AI handling UPDATES ONLY.
Current time (London): ${now}
Ada's last question: "${state.lastQuestion}"

EXISTING BOOKING (DO NOT COPY - only update what user changes):
- Pickup: ${state.pickup || "NOT SET"}
- Destination: ${state.destination || "NOT SET"}
- Passengers: ${state.passengers ?? "NOT SET"}
- Pickup Time: ${state.pickup_time || "NOT SET"}
- Luggage: ${state.luggage || "NOT SET"}
- Special Requests: ${state.special_requests || "NOT SET"}

==================================================
UPDATE RULES (CRITICAL)
==================================================
• Only return fields the user EXPLICITLY changes
• Any field NOT changed must be returned as null
• "remove luggage" → luggage = "CLEAR"

PICKUP + DROPOFF UPDATE:
If user says BOTH pickup AND dropoff:
• "from X to Y" → extract BOTH
• "change pickup to X and destination to Y" → extract BOTH

CORRECTION PATTERNS:
• "Actually, change pickup to..." → update pickup, is_correction = true
• "No, I meant..." → update relevant field, is_correction = true
• "Not 52A, it's 52B" → update with new value, is_correction = true

CONTEXT-AWARE (based on Ada's question):
If Ada asked about pickup → user's address is pickup
If Ada asked about destination → user's address is destination
If Ada asked about passengers → user's number is passengers
If Ada asked about time → user's answer is pickup_time

AFFIRMATIVE = true if user confirms booking
intent = "update_booking" for changes, "confirm_booking" for confirmation`;
}

// ================= MERGE EXTRACTION WITH STATE (Like C# Mapper.Apply) =================
function mergeExtraction(extraction: any, state: BookingState) {
  return {
    pickup: extraction.pickup_location || state.pickup,
    destination: extraction.dropoff_location || state.destination,
    passengers: extraction.number_of_passengers ?? state.passengers,
    pickup_time: extraction.pickup_time || state.pickup_time,
    luggage: extraction.luggage === "CLEAR" ? null : (extraction.luggage || state.luggage),
    special_requests: extraction.special_requests || state.special_requests,
    intent: extraction.intent || "new_booking",
    is_affirmative: extraction.is_affirmative || false,
    is_correction: extraction.is_correction || false,
    fields_extracted: extraction.fields_extracted || [],
  };
}

function createEmptyExtraction(state: BookingState) {
  return {
    pickup: state.pickup,
    destination: state.destination,
    passengers: state.passengers,
    pickup_time: state.pickup_time,
    luggage: state.luggage,
    special_requests: state.special_requests,
    intent: "other",
    is_affirmative: false,
    is_correction: false,
    fields_extracted: [],
  };
}

// ================= BRAIN 2: ADA SPEECH GENERATION =================
async function getAdaSpeech(transcript: string, state: BookingState, extraction: any, apiKey: string) {
  // Logic enforcement: If confirmed and all fields set, book the taxi
  let forceConfirm = false;
  let userMessage = transcript;
  
  if (extraction.is_affirmative && state.step === "summary") {
    userMessage = "The user has confirmed everything is correct. Confirm the booking.";
    forceConfirm = true;
  }

  const systemPrompt = `You are Ada, a friendly London taxi dispatcher on a voice call.

CURRENT BOOKING STATE:
- Pickup: ${state.pickup || 'NOT SET'}
- Destination: ${state.destination || 'NOT SET'}
- Passengers: ${state.passengers ?? 'NOT SET'}
- Pickup Time: ${state.pickup_time || 'NOT SET'}
- Luggage: ${state.luggage || 'none'}
- Special Requests: ${state.special_requests || 'none'}
- Step: ${state.step}

FIELDS JUST EXTRACTED: ${extraction.fields_extracted.join(", ") || "none"}

STRICT RULES:
1. ONLY use the data shown above. NEVER use placeholders like [number] or [address].
2. If a required field is "NOT SET", you MUST ask for it.
3. Ask ONE question at a time - do not bundle questions.
4. Required collection order: pickup → destination → passengers → time
5. Luggage and special requests are OPTIONAL - don't ask unless user mentions them.
6. If ALL required fields are set and step is 'collecting', give a summary and ask "Is that correct?"
7. If user confirms in 'summary' step, say the taxi is booked.
8. Acknowledge multiple fields if extracted: "Got it, pickup at X, going to Y with Z passengers."

${forceConfirm ? 'USER HAS CONFIRMED. Book the taxi now and say goodbye.' : ''}

STYLE: Warm, concise, British. No filler words.`;

  console.log(`[BRAIN2] Generating speech. Step: ${state.step}, Affirmative: ${extraction.is_affirmative}`);

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
        ...state.conversationHistory.slice(-6),
        { role: "user", content: userMessage }
      ],
      max_tokens: 200,
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

// ================= MAIN PROCESSOR =================
async function processTurn(state: BookingState, transcript: string, apiKey: string): Promise<{
  speech: string;
  state: BookingState;
  extraction: any;
  end: boolean;
}> {
  // Add user message to history
  state.conversationHistory.push({ role: "user", content: transcript });

  // Determine if this is an update (has existing data)
  const isUpdate = !!(state.pickup || state.destination || state.passengers);

  // Step A: Extract with tool calling
  const extraction = await extractIntent(transcript, state, isUpdate, apiKey);
  
  // Log changes
  const pickupChanged = extraction.pickup !== state.pickup;
  const destChanged = extraction.destination !== state.destination;
  const paxChanged = extraction.passengers !== state.passengers;
  const timeChanged = extraction.pickup_time !== state.pickup_time;
  
  if (pickupChanged) console.log(`[STATE] ${state.pickup ? '🔄 UPDATED' : '✅ Set'} pickup: ${extraction.pickup}`);
  if (destChanged) console.log(`[STATE] ${state.destination ? '🔄 UPDATED' : '✅ Set'} destination: ${extraction.destination}`);
  if (paxChanged) console.log(`[STATE] ${state.passengers ? '🔄 UPDATED' : '✅ Set'} passengers: ${extraction.passengers}`);
  if (timeChanged) console.log(`[STATE] ${state.pickup_time ? '🔄 UPDATED' : '✅ Set'} pickup_time: ${extraction.pickup_time}`);
  
  // Apply merged extraction to state
  state.pickup = extraction.pickup;
  state.destination = extraction.destination;
  state.passengers = extraction.passengers;
  state.pickup_time = extraction.pickup_time;
  state.luggage = extraction.luggage;
  state.special_requests = extraction.special_requests;
  
  // Handle corrections during summary
  if (extraction.is_correction && state.step === "summary") {
    console.log(`[STATE] Correction during summary - back to collecting`);
    state.step = "collecting";
  }
  
  // Check if ready for summary (all 4 required fields)
  if (state.pickup && state.destination && state.passengers && state.pickup_time && state.step === "collecting") {
    console.log(`[STATE] All required fields collected → summary`);
    state.step = "summary";
  }

  // Step B: Generate Ada's response
  const adaResponse = await getAdaSpeech(transcript, state, extraction, apiKey);

  // Step C: Handle confirmation
  let shouldEnd = false;
  if (adaResponse.shouldConfirm) {
    state.step = "confirmed";
    console.log(`[MAIN] ✅ Booking confirmed`);
    shouldEnd = true;
  }

  // Save question for context
  state.lastQuestion = adaResponse.content;
  state.conversationHistory.push({ role: "assistant", content: adaResponse.content });

  return { 
    speech: adaResponse.content, 
    state: { ...state },
    extraction,
    end: shouldEnd 
  };
}

// ================= HTTP SERVER =================
serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const url = new URL(req.url);
  
  if (url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ 
      status: "ok",
      mode: "dual-brain-tool-calling" 
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

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

      const state: BookingState = clientState || createInitialState();
      
      console.log(`[MAIN] Processing: "${transcript}"`);
      console.log(`[MAIN] Current state:`, {
        pickup: state.pickup,
        destination: state.destination,
        passengers: state.passengers,
        pickup_time: state.pickup_time,
        step: state.step,
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
          extraction: result.extraction,
          currentStep: result.state.step,
          fieldsCollected: {
            pickup: !!result.state.pickup,
            destination: !!result.state.destination,
            passengers: !!result.state.passengers,
            pickup_time: !!result.state.pickup_time,
          },
          fields_extracted_this_turn: result.extraction.fields_extracted,
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
