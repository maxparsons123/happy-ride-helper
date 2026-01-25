import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === STT CORRECTIONS ===
const STT_CORRECTIONS: Record<string, string> = {
  // House number corrections
  "52-8": "52A", "52 8": "52A", "528": "52A", "52 a": "52A",
  "52-a": "52A", "fifty two a": "52A", "fifty-two a": "52A",
  "52 hey": "52A", "52 eh": "52A", "52 age": "52A",
  "7-8": "7A", "7 8": "7A", "78": "7A", "7 a": "7A",
  "seven a": "7A", "7-a": "7A",
  // Street name corrections
  "david rohn": "David Road", "david rhone": "David Road", "david rose": "David Road",
  "roswell street": "Russell Street", "russel street": "Russell Street",
  // Local place names
  "sweet spots": "Sweetspot", "sweet spot": "Sweetspot",
};

function applySTTCorrections(text: string): string {
  let corrected = text;
  for (const [wrong, right] of Object.entries(STT_CORRECTIONS)) {
    // Use word boundaries to prevent partial matches
    const regex = new RegExp(`\\b${wrong}\\b`, "gi");
    corrected = corrected.replace(regex, right);
  }
  // Join numbers with following single letters (e.g., "52 A" -> "52A")
  corrected = corrected.replace(/(\d+)\s+([A-Za-z])(?=\s|$)/g, "$1$2");
  return corrected;
}

function normalizeForMatch(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function tokenOverlapScore(a: string, b: string): number {
  const na = normalizeForMatch(a);
  const nb = normalizeForMatch(b);
  if (!na || !nb) return 0;
  const ta = new Set(na.split(" ").filter(Boolean));
  const tb = new Set(nb.split(" ").filter(Boolean));
  let overlap = 0;
  for (const t of ta) if (tb.has(t)) overlap++;
  const denom = Math.max(1, Math.min(ta.size, tb.size));
  return overlap / denom;
}

function isLikelyEcho(echo: string, reference: string): boolean {
  const ne = normalizeForMatch(echo);
  const nr = normalizeForMatch(reference);
  if (!ne || !nr) return false;
  // Strong signals: contains the whole reference or vice versa
  if (ne.includes(nr) || nr.includes(ne)) return true;
  // Otherwise require strong token overlap
  return tokenOverlapScore(ne, nr) >= 0.75;
}

// === TOOLS DEFINITION (STRICT MODE) ===
const TOOLS = [
  {
    type: "function",
    name: "book_taxi",
    description: "Book a taxi after the user confirms the booking summary. Use action='request_quote' to get fare estimate, action='confirmed' after user accepts the fare. CRITICAL: You MUST use the EXACT addresses the customer spoke - never alter house numbers.",
    parameters: {
      type: "object",
      properties: {
        action: {
          type: "string",
          enum: ["request_quote", "confirmed"],
          description: "request_quote = get fare estimate, confirmed = finalize booking"
        },
        pickup_house_number: { 
          type: "string", 
          description: "EXACT house number as spoken, including letters. Examples: '52A', '32B', '10-12', '14'. NEVER alter this - if customer said '32A' use '32A' not '28' or '32'." 
        },
        pickup_street: { 
          type: "string", 
          description: "Street name and any additional address parts exactly as spoken. Example: 'David Road'" 
        },
        destination_house_number: { 
          type: "string", 
          description: "EXACT destination house number as spoken, including letters. Use empty string if not applicable (e.g., for landmarks like 'Sweetspot')." 
        },
        destination_street: { 
          type: "string", 
          description: "Destination street/place name exactly as spoken. Example: 'Sweetspot' or 'High Street'" 
        },
        passengers: { 
          type: "integer", 
          description: "Number of passengers as an integer (1-10)" 
        },
        time: { 
          type: "string", 
          description: "Pickup time (e.g., 'now', '3pm', 'in 10 minutes')" 
        },
        luggage: { 
          type: "string", 
          description: "Luggage description (e.g., 'none', '2 suitcases', 'small bag')" 
        },
        vehicle_type: { 
          type: "string", 
          description: "Vehicle preference (e.g., 'standard', 'estate', 'mpv', 'executive')" 
        }
      },
      required: ["action", "pickup_house_number", "pickup_street", "destination_house_number", "destination_street", "passengers", "time", "luggage", "vehicle_type"],
      additionalProperties: false
    },
    strict: true
  },
  {
    type: "function",
    name: "end_call",
    description: "End the call after booking is complete or if user wants to hang up",
    parameters: {
      type: "object",
      properties: {
        reason: { 
          type: "string", 
          enum: ["booking_complete", "user_cancelled", "user_hangup"],
          description: "Reason for ending the call"
        }
      },
      required: ["reason"],
      additionalProperties: false
    },
    strict: true
  }
];

// === SYSTEM PROMPT ===
const SYSTEM_PROMPT = `
# IDENTITY
You are ADA, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual.

# SPEECH PACING
- Speak at a SLOW, RELAXED pace
- Insert natural pauses between sentences
- Pronounce addresses clearly

# LANGUAGE
Respond in the same language the caller speaks.

# ONE QUESTION RULE
Ask ONLY ONE question per response. NEVER combine questions.

# ZERO-PARAPHRASE RULE (CRITICAL - MOST IMPORTANT)
- When the customer gives you an address, house number, or location name - you MUST repeat it EXACTLY as heard
- NEVER substitute, change, or "correct" what they said
- If they say "52A David Road" - say "52A David Road" (NOT "52", NOT "28", NOT "52B")
- If they say "32A David Road" - say "32A David Road" (NOT "28", NOT "32", NOT "32B")
- If unsure, say: "Sorry, could you repeat that address for me?"
- House numbers are SACRED - never alter them

# NO MID-FLOW RECAPS (CRITICAL)
- Do NOT summarize or repeat back booking details until the FINAL summary step
- After pickup is given, DO NOT repeat it back - just ask the next question
- After destination is given, DO NOT repeat it back - just ask the next question
- Example WRONG: "Got it, 52A David Road. Where would you like to go?"
- Example CORRECT: "Where would you like to go?"

# GREETING (Say this FIRST when call starts)
"Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"

# BOOKING FLOW (Ask ONE at a time, in order - NO recaps until step 5)
1. Get pickup → DO NOT REPEAT IT. Just ask: "Where would you like to go?"
2. Get destination → DO NOT REPEAT IT. Just ask: "How many passengers will be travelling?"
3. Get passengers → Ask: "Will you have any luggage?"
4. Get luggage → If large luggage, offer estate. Then ask: "When would you like the taxi - now or later?"
5. Get time → NOW give the ONLY summary: "To confirm: pickup from [EXACT pickup], going to [EXACT destination], for [N] passengers, at [time]. Shall I get a quote?"
6. When user confirms summary → Say "One moment please" and call book_taxi(action="request_quote")
7. After receiving fare → Tell user fare/ETA and ask to confirm
8. When user accepts fare → Call book_taxi(action="confirmed")

# PASSENGERS (ANTI-STUCK RULE)
- Accept digits or number words (one through ten)
- Accept homophones: "to/too" → two, "for" → four, "tree/free/the/there" → three

# LUGGAGE HANDLING
- Accept: "no/none", "just a small bag", "2 suitcases", etc.
- If large luggage mentioned, offer: "Would you like an estate or larger vehicle?"
- If "no luggage" → proceed to time question

# ADDRESS INTERPRETATION (STRICT - ALPHANUMERIC PRESERVATION)
- "52-8", "52 A", "52 hey", or "fifty two a" MUST be interpreted as "52A"
- ALPHANUMERIC SUFFIXES ARE PART OF THE NUMBER: 52A, 14B, 7C, 32A
- NEVER strip the letter. If you hear a letter after a number, it is a house suffix
- If the user says "52A" and your internal transcript says "52", you MUST prioritize "52A"
- DO NOT NORMALIZE: "Flat 4, 52A David Road" must NOT become "52 David Road"
- Examples: "7A" not "7", "32B" not "32", "10-12" stays "10-12"

# CORRECTIONS
- Listen for: "no", "actually", "change", "I meant"
- Say: "Updated to [new value]." then continue
- NEVER repeat the old incorrect value

# CRITICAL RULES
- Do NOT quote fares until you receive them from book_taxi
- After user confirms summary, you MUST call book_taxi(action="request_quote")
- Only call book_taxi(action="confirmed") after user accepts the fare
- NEVER invent or alter house numbers - repeat EXACTLY what customer said
- If AI says "28" but user said "52A" - the user is ALWAYS correct
`;

// === AUDIO HELPERS ===
function pcm8kTo24k(pcm8k: Uint8Array): Uint8Array {
  const samples8k = new Int16Array(pcm8k.buffer, pcm8k.byteOffset, Math.floor(pcm8k.byteLength / 2));
  const len24k = samples8k.length * 3;
  const samples24k = new Int16Array(len24k);

  for (let i = 0; i < samples8k.length - 1; i++) {
    const s0 = samples8k[i];
    const s1 = samples8k[i + 1];
    samples24k[i * 3] = s0;
    samples24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
    samples24k[i * 3 + 2] = Math.round(s0 + 2 * (s1 - s0) / 3);
  }

  const last = samples8k[samples8k.length - 1] || 0;
  samples24k[len24k - 3] = last;
  samples24k[len24k - 2] = last;
  samples24k[len24k - 1] = last;

  return new Uint8Array(samples24k.buffer);
}

function arrayBufferToBase64(buffer: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < buffer.length; i++) {
    binary += String.fromCharCode(buffer[i]);
  }
  return btoa(binary);
}

// === STEP DETECTION ===
type BookingStep = "pickup" | "destination" | "passengers" | "luggage" | "vehicle" | "time" | "summary" | "awaiting_fare" | "awaiting_final" | "done" | "unknown";

function detectStepFromAdaTranscript(transcript: string): BookingStep {
  const lower = transcript.toLowerCase();
  if (/where would you like to be picked up|pickup (location|address)|pick you up/i.test(lower)) return "pickup";
  if (/where (would you like to go|are you going|is your destination)|heading to/i.test(lower)) return "destination";
  if (/how many (people|passengers)|travelling/i.test(lower)) return "passengers";
  if (/luggage|bags?|suitcase|any bags/i.test(lower)) return "luggage";
  if (/estate|mpv|people carrier|vehicle (type|preference)|larger vehicle/i.test(lower)) return "vehicle";
  if (/when would you like|pickup time|what time|now or later/i.test(lower)) return "time";
  if (/let me confirm|to confirm|summary|picking you up from/i.test(lower)) return "summary";
  if (/one moment|checking|getting.*fare/i.test(lower)) return "awaiting_fare";
  if (/taxi is booked|safe journey|whatsapp/i.test(lower)) return "done";
  return "unknown";
}

function getContextHintForStep(step: BookingStep): string | null {
  switch (step) {
    case "pickup":
      return "[CONTEXT: User is providing PICKUP ADDRESS. Listen for street names, house numbers, landmarks.]";
    case "destination":
      return "[CONTEXT: User is providing DESTINATION ADDRESS. Listen for street names, house numbers, landmarks.]";
    case "passengers":
      return "[CONTEXT: User is providing PASSENGER COUNT. 'tree/free/the/there' = 3, 'to/too' = 2, 'for' = 4. Accept 1-10.]";
    case "luggage":
      return "[CONTEXT: User is describing LUGGAGE. Listen for: none, small bag, suitcase(s), pushchair, wheelchair, golf clubs.]";
    case "vehicle":
      return "[CONTEXT: User is choosing VEHICLE TYPE. Options: standard, estate, MPV/people carrier, executive.]";
    case "time":
      return "[CONTEXT: User is providing PICKUP TIME. 'now/asap' = immediately. Listen for times like '3pm'.]";
    case "summary":
      return "[CONTEXT: User is CONFIRMING booking. 'yes/yeah/correct' = confirmed. 'no/change' = needs correction.]";
    case "awaiting_final":
      return "[CONTEXT: User is confirming the FARE. 'yes/okay/that's fine' = accept fare.]";
    default:
      return null;
  }
}

// === NEAREST PLACE DETECTION ===
function extractNearestPlace(destination: string): string {
  const lower = destination.toLowerCase();
  
  // Common "nearest X" patterns
  const nearestPatterns = [
    /nearest\s+(.+)/i,
    /the\s+closest\s+(.+)/i,
    /closest\s+(.+)/i,
    /nearby\s+(.+)/i
  ];
  
  for (const pattern of nearestPatterns) {
    const match = lower.match(pattern);
    if (match) {
      return match[1].trim();
    }
  }
  
  return "";
}

// === WEBHOOK ===
interface BookingPayload {
  pickup: string;
  destination: string;
  passengers: number;
  time?: string;
  luggage?: string;
  special_requests?: string;
  vehicle_type?: string;
}

interface FullBookingState {
  pickup: string;
  destination: string;
  passengers: number;
  time: string;
  fare: string;
  eta: string;
  luggage: string;
  special_requests: string;
  vehicle_type: string;
}

async function sendDispatchWebhook(
  callId: string,
  callerPhone: string,
  action: string,
  booking: FullBookingState,
  callbackUrl: string,
  log: (msg: string) => void
): Promise<{ success: boolean; fare?: string; eta?: string; booking_ref?: string; error?: string }> {
  
  // Detect if destination is a "nearest X" request
  const nearestPlace = extractNearestPlace(booking.destination);
  
  // Build the payload matching C# HandleAdaIncoming expectations
  const payload = {
    // Fields that C# checks for routing (ada_pickup triggers booking webhook)
    ada_pickup: booking.pickup,
    ada_destination: booking.destination,
    
    // Core booking fields
    pickup_location: booking.pickup,
    dropoff_location: booking.destination,
    pickup_time: booking.time || "now",
    number_of_passengers: booking.passengers,
    luggage: booking.luggage || "",
    special_requests: booking.special_requests || "",
    nearest_place: nearestPlace,
    
    // User details
    usertelephone: callerPhone,
    username: "",
    
    // GPS (0 if not available)
    userlat: 0,
    userlon: 0,
    
    // Reference fields
    reference_number: "",
    jobid: "",
    job_id: "",
    
    // Raw JSON for debugging
    Rawjson: JSON.stringify({
      action,
      call_id: callId,
      pickup: booking.pickup,
      destination: booking.destination,
      passengers: booking.passengers,
      time: booking.time
    }),
    
    // Booking message
    bookingmessage: action === "request_quote" 
      ? "Quote requested" 
      : action === "confirmed" 
        ? "Booking confirmed" 
        : "",
    
    // Ada-specific flags
    isFromAda: true,
    isQuoteOnly: action === "request_quote",
    ada_call_id: callId,
    call_id: callId,
    action: action,
    vehicle_type: booking.vehicle_type || "",
    vehicle_request: booking.vehicle_type || "",
    
    // Original addresses as spoken
    callers_pickup: booking.pickup,
    callers_dropoff: booking.destination,
    
    // Verification flags
    pickup_verified: false,
    dropoff_verified: false,
    
    // Fare/ETA (populated on response)
    ada_estimated_fare: booking.fare || "",
    ada_estimated_eta: booking.eta || "",
    
    // Webhook control
    callback_url: callbackUrl
  };
  
  if (!DISPATCH_WEBHOOK_URL) {
    log("⚠️ No DISPATCH_WEBHOOK_URL configured, using mock response");
    log(`📦 Would send payload: ${JSON.stringify(payload, null, 2)}`);
    // Mock response for demo
    return { success: true, fare: "£12.50", eta: "5 minutes", booking_ref: "DEMO-" + Date.now() };
  }

  try {
    log(`📤 Sending webhook: ${action}`);
    log(`📦 Payload: ${JSON.stringify(payload)}`);
    
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    if (!response.ok) {
      throw new Error(`Webhook failed: ${response.status}`);
    }

    const responseText = await response.text();
    log(`📥 Webhook response: ${responseText}`);
    
    // Try to parse as JSON, fallback to text handling
    let data: Record<string, unknown> = {};
    try {
      data = JSON.parse(responseText);
    } catch {
      // C# returns "OK" text - treat as success with fallback fare
      log(`⚠️ Non-JSON response, using fallback fare`);
      if (responseText.toLowerCase().includes("ok") || response.status === 200) {
        return { success: true, fare: "£15.00", eta: "8 minutes", booking_ref: `ADA-${Date.now()}` };
      }
      throw new Error(`Invalid response: ${responseText}`);
    }
    
    log(`📦 Parsed response: ${JSON.stringify(data)}`);
    return { 
      success: data.success !== false, 
      fare: data.fare as string | undefined, 
      eta: data.eta_minutes ? `${data.eta_minutes} minutes` : (data.eta as string | undefined),
      booking_ref: data.booking_ref as string | undefined
    };
  } catch (error) {
    log(`❌ Webhook error: ${error}`);
    return { success: false, error: String(error) };
  }
}

// === MAIN HANDLER ===
serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426 });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;
  let callId = "unknown";
  let callerPhone = "unknown";
  
  // Booking state - full state
  let bookingState: FullBookingState = {
    pickup: "",
    destination: "",
    passengers: 1,
    time: "now",
    fare: "",
    eta: "",
    luggage: "",
    special_requests: "",
    vehicle_type: ""
  };

  // Last known user-provided values (used to validate any "Ada echo" extraction)
  let lastUserTruth: { pickup: string; destination: string; passengers: number | null } = {
    pickup: "",
    destination: "",
    passengers: null,
  };
  
  // Step tracking
  let currentStep: BookingStep = "pickup";
  let stepAtSpeechStart: BookingStep = "pickup";
  let contextInjected = false;
  let pendingToolCallId = "";
  
  // Quote timeout handling
  let awaitingDispatchQuote = false;
  let dispatchQuoteReceived = false;
  let quoteTimeoutId: number | null = null;
  const QUOTE_TIMEOUT_MS = 4000; // 4 seconds to wait for real quote
  
  // Post-booking flow
  let bookingConfirmed = false;
  let askedAnythingElse = false;
  let finalGoodbyePending = false;

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

  // Helper to deliver quote to OpenAI and trigger response
  const deliverQuote = (fare: string, eta: string, toolCallId: string) => {
    bookingState.fare = fare;
    bookingState.eta = eta;
    currentStep = "awaiting_final";
    
    log(`💰 Delivering quote: ${fare}, ETA: ${eta}`);
    
    // Inject the fare as a system instruction so Ada speaks it
    const fareInstruction = `[QUOTE RECEIVED]
Fare: ${fare}
ETA: ${eta}

Tell the customer: "The fare will be ${fare} and your driver will be approximately ${eta}. Shall I go ahead and book that for you?"
Wait for their YES or NO response.`;
    
    openaiWs?.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "system",
        content: [{ type: "input_text", text: fareInstruction }]
      }
    }));
    openaiWs?.send(JSON.stringify({ type: "response.create" }));
  };

  const handleToolCall = async (name: string, args: Record<string, unknown>, toolCallId: string) => {
    log(`🔧 Tool call: ${name}(${JSON.stringify(args)})`);
    
    if (name === "book_taxi") {
      const action = args.action as string;
      
      // Build full addresses from strict schema fields
      let pickupHouseNum = (args.pickup_house_number as string) || "";
      const pickupStreet = (args.pickup_street as string) || "";
      let destHouseNum = (args.destination_house_number as string) || "";
      const destStreet = (args.destination_street as string) || "";
      
      // === FINAL ALPHANUMERIC SANITY CHECK (Last Mile Safety) ===
      // If AI extracted '52' but user actually said '52A', restore the alphanumeric version
      const userPickupTruth = (lastUserTruth.pickup || "").toUpperCase();
      const userDestTruth = (lastUserTruth.destination || "").toUpperCase();
      
      if (pickupHouseNum && /^\d+$/.test(pickupHouseNum)) {
        // AI returned just digits, check if user said alphanumeric
        const pattern = new RegExp(`(${pickupHouseNum}[A-Z])\\b`, "i");
        const match = userPickupTruth.match(pattern);
        if (match) {
          log(`🛡️ Safety Triggered (Pickup): Restoring ${pickupHouseNum} -> ${match[1]}`);
          pickupHouseNum = match[1];
        }
      }
      
      if (destHouseNum && /^\d+$/.test(destHouseNum)) {
        // AI returned just digits, check if user said alphanumeric
        const pattern = new RegExp(`(${destHouseNum}[A-Z])\\b`, "i");
        const match = userDestTruth.match(pattern);
        if (match) {
          log(`🛡️ Safety Triggered (Destination): Restoring ${destHouseNum} -> ${match[1]}`);
          destHouseNum = match[1];
        }
      }
      
      // Combine house number + street for full address
      const fullPickup = pickupHouseNum ? `${pickupHouseNum} ${pickupStreet}`.trim() : pickupStreet;
      const fullDest = destHouseNum ? `${destHouseNum} ${destStreet}`.trim() : destStreet;
      
      log(`📍 Strict schema: Pickup="${fullPickup}" (house: "${pickupHouseNum}"), Dest="${fullDest}" (house: "${destHouseNum}")`);
      
      // Update booking state from strict tool args
      if (fullPickup) bookingState.pickup = fullPickup;
      if (fullDest) bookingState.destination = fullDest;
      if (args.passengers != null) bookingState.passengers = args.passengers as number;
      if (args.time) bookingState.time = args.time as string;
      if (args.luggage) bookingState.luggage = args.luggage as string;
      if (args.vehicle_type) bookingState.vehicle_type = args.vehicle_type as string;
      
      if (action === "request_quote") {
        currentStep = "awaiting_fare";
        awaitingDispatchQuote = true;
        dispatchQuoteReceived = false;
        pendingToolCallId = toolCallId;
        
        // Send webhook to dispatch system (fire and forget - we'll wait for callback)
        sendDispatchWebhook(callId, callerPhone, "request_quote", bookingState, "", log)
          .then((result) => {
            log(`📤 Webhook sent, immediate result: ${JSON.stringify(result)}`);
            // If webhook returns immediate quote (no callback system), use it
            if (result.success && result.fare && !dispatchQuoteReceived) {
              dispatchQuoteReceived = true;
              if (quoteTimeoutId) {
                clearTimeout(quoteTimeoutId);
                quoteTimeoutId = null;
              }
              deliverQuote(result.fare, result.eta || "5 minutes", toolCallId);
            }
          })
          .catch((err) => log(`❌ Webhook error: ${err}`));
        
        // Start 4-second timeout for dispatch callback
        log(`⏳ Waiting up to ${QUOTE_TIMEOUT_MS}ms for dispatch quote...`);
        quoteTimeoutId = setTimeout(() => {
          if (!dispatchQuoteReceived && awaitingDispatchQuote) {
            log(`⏰ Quote timeout - using fallback quote`);
            dispatchQuoteReceived = true;
            awaitingDispatchQuote = false;
            
            // Generate mock quote
            const mockFare = "£12.50";
            const mockEta = "6 minutes";
            deliverQuote(mockFare, mockEta, pendingToolCallId);
          }
        }, QUOTE_TIMEOUT_MS);
        
        // Tell Ada to say "one moment" while we wait
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "function_call_output",
            call_id: toolCallId,
            output: JSON.stringify({
              status: "checking",
              message: "Say 'One moment please, let me check the fare for you.' and wait."
            })
          }
        }));
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
        
      } else if (action === "confirmed") {
        const confirmResult = await sendDispatchWebhook(callId, callerPhone, "confirmed", bookingState, "", log);
        
        bookingConfirmed = true;
        askedAnythingElse = true;
        currentStep = "done";
        
        // Inject post-booking pleasantry + "anything else?" question
        const postBookingScript = `[BOOKING CONFIRMED - POST-BOOKING MODE]
The booking is now confirmed.

Say EXACTLY this (warm and pleasant):
"Wonderful! Your taxi is booked. You'll receive updates via WhatsApp. Is there anything else I can help you with today?"

Wait for the customer's response:
- If they say NO/nothing/that's all/bye → Say "Thank you for using the Taxibot demo. Have a lovely day, goodbye!" then call end_call(reason="booking_complete")
- If they ask something else → Help them, then ask again if there's anything else`;
        
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "function_call_output",
            call_id: toolCallId,
            output: JSON.stringify({
              status: "booking_confirmed",
              message: postBookingScript
            })
          }
        }));
        
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }
      
    } else if (name === "end_call") {
      log(`📞 End call requested: ${args.reason}`);
      
      openaiWs?.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "function_call_output",
          call_id: toolCallId,
          output: JSON.stringify({ status: "call_ending" })
        }
      }));
      
      // Send hangup to bridge after a short delay
      setTimeout(() => {
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(JSON.stringify({ type: "hangup", reason: args.reason }));
        }
      }, 2000);
    }
  };

  // Connect to OpenAI Realtime
  const connectOpenAI = () => {
    log("🔌 Connecting to OpenAI...");

    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("✅ OpenAI connected, configuring session...");
    };

    openaiWs.onmessage = async (event) => {
      const msg = JSON.parse(event.data);

      // Session created - configure with tools
      if (msg.type === "session.created") {
        log("📋 Session created, sending config with tools");
        
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            instructions: SYSTEM_PROMPT,
            voice: "shimmer",
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            turn_detection: {
              type: "server_vad",
              threshold: 0.3,
              prefix_padding_ms: 600,
              silence_duration_ms: 1000
            },
            tools: TOOLS,
            tool_choice: "auto"
          }
        }));
      }

      // Session configured - trigger greeting
      if (msg.type === "session.updated") {
        log("🎤 Session configured, triggering greeting");
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }

      // Track Ada's speech for step detection AND (optionally) extract echoed values
      if (msg.type === "response.audio_transcript.done") {
        const adaText = msg.transcript || "";
        log(`🗣️ Ada: ${adaText}`);
        
        const detected = detectStepFromAdaTranscript(adaText);
        if (detected !== "unknown") {
          currentStep = detected;
          log(`📍 Step: ${currentStep}`);
        }
        contextInjected = false;
        
        // === DETECT GOODBYE FOR GRACEFUL HANGUP ===
        const lowerAda = adaText.toLowerCase();
        const isGoodbye = /\b(goodbye|have a (lovely|great|safe|wonderful) day|safe journey|thank you for (using|trying)|bye)\b/i.test(lowerAda);
        if (isGoodbye && bookingConfirmed) {
          log(`👋 Goodbye detected, preparing graceful hangup`);
          finalGoodbyePending = true;
        }
        
        // === EXTRACT ECHOED VALUES FROM ADA'S TRANSCRIPT ===
        // Ada sometimes repeats what the user said. We ONLY accept Ada "echo" updates when
        // they strongly match something the user already provided (never let Ada invent state).
        const allowEchoExtraction =
          currentStep !== "summary" &&
          currentStep !== "awaiting_fare" &&
          currentStep !== "awaiting_final" &&
          currentStep !== "done";

        if (allowEchoExtraction) {
          // PICKUP echo
          const pickupMatch = adaText.match(
            /(?:picking you up from|pickup (?:is|at)|collect you from)\s+([^,\.]+?)(?:\s*,|\s+and\s+|\s+to\s+|\s*\.|$)/i,
          );
          if (pickupMatch && pickupMatch[1]) {
            const adaPickup = pickupMatch[1].trim();
            const ref = lastUserTruth.pickup || bookingState.pickup;
            if (ref && isLikelyEcho(adaPickup, ref) && adaPickup.length >= bookingState.pickup.length) {
              if (adaPickup !== bookingState.pickup) {
                log(`🎯 Ada echoed PICKUP (accepted): "${adaPickup}" (was: "${bookingState.pickup}")`);
                bookingState.pickup = adaPickup;
              }
            } else {
              log(
                `🚫 Ignored Ada pickup echo: "${adaPickup}" (ref="${ref}")`,
              );
            }
          }

          // DESTINATION echo
          const destMatch = adaText.match(
            /(?:going to|heading to|destination (?:is|at)|dropping (?:you )?off at|taking you to)\s+([^,\.]+?)(?:\s*,|\s+with\s+|\s*\.|$)/i,
          );
          if (destMatch && destMatch[1]) {
            const adaDest = destMatch[1].trim();
            const ref = lastUserTruth.destination || bookingState.destination;
            if (ref && isLikelyEcho(adaDest, ref) && adaDest.length >= bookingState.destination.length) {
              if (adaDest !== bookingState.destination) {
                log(`🎯 Ada echoed DESTINATION (accepted): "${adaDest}" (was: "${bookingState.destination}")`);
                bookingState.destination = adaDest;
              }
            } else {
              log(
                `🚫 Ignored Ada destination echo: "${adaDest}" (ref="${ref}")`,
              );
            }
          }

          // PASSENGERS echo
          const passMatch = adaText.match(
            /(?:for\s+)?(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:passenger|people|person)/i,
          );
          if (passMatch && passMatch[1]) {
            const numWords: Record<string, number> = { one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10 };
            const val = /^\d+$/.test(passMatch[1]) ? parseInt(passMatch[1]) : numWords[passMatch[1].toLowerCase()] || 1;
            const ref = lastUserTruth.passengers ?? bookingState.passengers;
            if (ref != null && val === ref) {
              // nothing to do; we only use Ada to validate, not override
              log(`🎯 Ada echoed PASSENGERS (validated): ${val}`);
            } else {
              log(`🚫 Ignored Ada passengers echo: ${val} (ref=${ref})`);
            }
          }
        }
      }

      // Handle tool calls
      if (msg.type === "response.function_call_arguments.done") {
        try {
          const args = JSON.parse(msg.arguments || "{}");
          await handleToolCall(msg.name, args, msg.call_id);
        } catch (e) {
          log(`❌ Tool parse error: ${e}`);
        }
      }

      // User speech started - inject context
      if (msg.type === "input_audio_buffer.speech_started") {
        stepAtSpeechStart = currentStep;
        log(`🎙️ Speech started (step: ${stepAtSpeechStart})`);
        
        if (!contextInjected) {
          const hint = getContextHintForStep(stepAtSpeechStart);
          if (hint) {
            log(`💡 Context: ${hint}`);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{ type: "input_text", text: hint }]
              }
            }));
            contextInjected = true;
          }
        }
      }

      // Log user transcript, capture addresses, and handle corrections
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const raw = msg.transcript || "";
        const corrected = applySTTCorrections(raw);
        if (raw !== corrected) {
          log(`👤 User: ${raw} → [STT FIX] ${corrected}`);
        } else {
          log(`👤 User: ${raw}`);
        }
        
        // === ENHANCED USER TRUTH CAPTURE WITH ALPHANUMERIC DETECTION ===
        // Regex to find numbers followed by letters (e.g., 52A, 7b, 32B)
        const houseNumberPattern = /(\d+[a-zA-Z])\b/g;
        const alphanumericMatches = corrected.match(houseNumberPattern);
        
        // Based on current step, save what user said as the verified value
        if (stepAtSpeechStart === "pickup" && corrected.length > 2) {
          // Save the raw user input as pickup (if it looks like an address)
          const cleaned = corrected.replace(/^(from|at|it's|my address is|pickup is)\s*/i, "").trim();
          if (cleaned.length > 2 && !/^(yes|no|yeah|ok|sure|please|thanks)/i.test(cleaned)) {
            bookingState.pickup = cleaned;
            lastUserTruth.pickup = cleaned;
            log(`📍 CAPTURED PICKUP: "${cleaned}"`);
            if (alphanumericMatches) {
              log(`🏠 Found Alphanumeric Pickup Number: ${alphanumericMatches.join(', ')}`);
            }
          }
        } else if (stepAtSpeechStart === "destination" && corrected.length > 2) {
          // Save the raw user input as destination
          const cleaned = corrected.replace(/^(to|going to|heading to|destination is)\s*/i, "").trim();
          if (cleaned.length > 2 && !/^(yes|no|yeah|ok|sure|please|thanks)/i.test(cleaned)) {
            bookingState.destination = cleaned;
            lastUserTruth.destination = cleaned;
            log(`📍 CAPTURED DESTINATION: "${cleaned}"`);
            if (alphanumericMatches) {
              log(`🏠 Found Alphanumeric Destination Number: ${alphanumericMatches.join(', ')}`);
            }
          }
        } else if (stepAtSpeechStart === "passengers") {
          // Try to extract passenger count
          const numMatch = corrected.match(/(\d+)|one|two|three|four|five|six|seven|eight|nine|ten/i);
          if (numMatch) {
            const numWords: Record<string, number> = { one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10 };
            const val = numMatch[1] ? parseInt(numMatch[1]) : numWords[numMatch[0].toLowerCase()] || 1;
            bookingState.passengers = val;
            lastUserTruth.passengers = val;
            log(`📍 CAPTURED PASSENGERS: ${val}`);
          }
        }
        
        // === INJECT VERIFIED STATE BEFORE SUMMARY ===
        // When Ada is about to summarize, inject what we actually captured
        if (currentStep === "summary" || currentStep === "time") {
          const stateHint = `[VERIFIED BOOKING STATE - USE THESE EXACT VALUES]
Pickup: "${bookingState.pickup || "NOT SET"}"
Destination: "${bookingState.destination || "NOT SET"}"
Passengers: ${bookingState.passengers}
Time: ${bookingState.time}

When summarizing, say EXACTLY these addresses. DO NOT substitute or change them.`;
          
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "system",
              content: [{ type: "input_text", text: stateHint }]
            }
          }));
          log(`📋 Injected verified state for summary`);
        }
        
        // Detect corrections - user saying "no", "actually", "change", etc.
        const lowerText = corrected.toLowerCase();
        const isCorrection = /^(no[,\s]|actually|i meant|change|wait|not |wrong|correction)/i.test(lowerText) ||
                            /\b(no[,\s]+the|no[,\s]+it's|actually[,\s]+it's|should be|is actually|change .* to)\b/i.test(lowerText);
        
        if (isCorrection) {
          log(`🔄 CORRECTION DETECTED: "${corrected}"`);
          
          // Extract what field and new value
          let correctionHint = `[CORRECTION DETECTED] User said: "${corrected}". `;
          
          // Try to extract the new value from the correction
          const newValueMatch = corrected.match(/(?:to|is|it's)\s+(.+?)(?:\s*please)?$/i);
          const newValue = newValueMatch ? newValueMatch[1].trim() : "";
          
          // Check what they're correcting
          if (/destination|going to|drop|heading/i.test(lowerText)) {
            if (newValue) {
              bookingState.destination = newValue;
              lastUserTruth.destination = newValue;
              log(`📍 CORRECTION - NEW DESTINATION: "${newValue}"`);
            }
            correctionHint += `User is CORRECTING THE DESTINATION to "${newValue || corrected}". Say: "Updated to ${newValue || corrected}."`;
          } else if (/pickup|pick up|from|picked up/i.test(lowerText)) {
            if (newValue) {
              bookingState.pickup = newValue;
              lastUserTruth.pickup = newValue;
              log(`📍 CORRECTION - NEW PICKUP: "${newValue}"`);
            }
            correctionHint += `User is CORRECTING THE PICKUP to "${newValue || corrected}". Say: "Updated to ${newValue || corrected}."`;
          } else if (/passenger/i.test(lowerText)) {
            correctionHint += "User is CORRECTING PASSENGER COUNT. Extract the new count.";
          } else {
            // General correction - try to figure out which field from context
            if (newValue) {
              // Default to destination if unclear
              bookingState.destination = newValue;
              lastUserTruth.destination = newValue;
              log(`📍 CORRECTION - ASSUMED DESTINATION: "${newValue}"`);
            }
            correctionHint += `Update the booking field they are correcting to "${newValue}". Acknowledge with 'Updated to ${newValue}.'`;
          }
          
          // Inject high-priority correction instruction
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "system",
              content: [{ type: "input_text", text: correctionHint }]
            }
          }));
        }
      }

      // Forward audio to bridge
      if (msg.type === "response.audio.delta" && msg.delta) {
        const binaryStr = atob(msg.delta);
        const bytes = new Uint8Array(binaryStr.length);
        for (let i = 0; i < binaryStr.length; i++) {
          bytes[i] = binaryStr.charCodeAt(i);
        }
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(bytes.buffer);
        }
      }

      // Handle response.done - trigger hangup if goodbye pending
      if (msg.type === "response.done") {
        if (finalGoodbyePending) {
          log(`👋 Response done after goodbye, sending hangup in 500ms`);
          setTimeout(() => {
            if (socket.readyState === WebSocket.OPEN) {
              socket.send(JSON.stringify({ type: "hangup", reason: "booking_complete" }));
              log(`📞 Hangup sent to bridge`);
            }
          }, 500);
        }
      }

      // Log errors
      if (msg.type === "error") {
        log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI disconnected");
  };

  // Bridge connection
  socket.onopen = () => log("🚀 Bridge connected");

  socket.onmessage = (event) => {
    // Binary audio from bridge
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState === WebSocket.OPEN) {
        const pcm8k = event.data instanceof ArrayBuffer
          ? new Uint8Array(event.data)
          : event.data;

        const pcm24k = pcm8kTo24k(pcm8k);
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: arrayBufferToBase64(pcm24k)
        }));
      }
      return;
    }

    // JSON control messages
    try {
      const msg = JSON.parse(event.data);
      
      if (msg.type === "init") {
        callId = msg.call_id || "unknown";
        
        // Try to get caller phone from multiple sources:
        // 1. Explicit phone/caller_phone/caller in init message
        // 2. Extract from UUID format: 00000000-0000-0000-0000-XXXXXXXXXXXX
        // 3. Extract from paired format: paired-XXXXXXXXXXX (timestamp + phone embedded)
        let rawPhone = msg.phone || msg.caller_phone || msg.caller || "unknown";
        
        if (rawPhone === "unknown" && callId && callId !== "unknown") {
          // Try UUID format: 00000000-0000-0000-0000-XXXXXXXXXXXX
          const uuidMatch = callId.match(/^00000000-0000-0000-0000-(\d{12})$/);
          if (uuidMatch) {
            const phoneDigits = uuidMatch[1];
            rawPhone = phoneDigits.replace(/^0+/, "");
            if (rawPhone.length >= 9) {
              log(`📱 Phone extracted from UUID: ${rawPhone}`);
            } else {
              rawPhone = "unknown";
            }
          }
          
          // Try paired format: paired-XXXXXXXXXXX (last 9-12 digits are phone)
          if (rawPhone === "unknown") {
            const pairedMatch = callId.match(/^paired-\d+$/);
            if (pairedMatch) {
              // The callId suffix contains timestamp, phone might be in the message
              log(`📱 Paired call detected, phone from init message: ${msg.phone || "not provided"}`);
            }
          }
        }
        
        // Format for WhatsApp: remove + and leading zeros
        callerPhone = rawPhone !== "unknown" 
          ? String(rawPhone).replace(/^\+/, "").replace(/^0+/, "")
          : "unknown";
        
        log(`📞 Call initialized (caller: ${callerPhone})`);
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
        
        // === SUBSCRIBE TO DISPATCH CHANNEL ===
        // Listen for fare callbacks from external dispatch system
        if (SUPABASE_URL && SUPABASE_SERVICE_ROLE_KEY) {
          const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
          const dispatchChannel = supabase.channel(`dispatch_${callId}`);
          
          dispatchChannel.on("broadcast", { event: "dispatch_ask_confirm" }, async (payload: any) => {
            const { message, fare, eta, eta_minutes, callback_url, booking_ref } = payload.payload || {};
            log(`📥 DISPATCH ask_confirm: fare=${fare}, eta=${eta_minutes || eta}, message="${message}"`);
            
            // Mark quote as received - cancel timeout
            if (awaitingDispatchQuote && !dispatchQuoteReceived) {
              dispatchQuoteReceived = true;
              awaitingDispatchQuote = false;
              if (quoteTimeoutId) {
                clearTimeout(quoteTimeoutId);
                quoteTimeoutId = null;
                log(`✅ Dispatch quote received before timeout`);
              }
            } else if (dispatchQuoteReceived) {
              log(`⚠️ Quote already delivered, ignoring late dispatch callback`);
              return;
            }
            
            if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) {
              log("⚠️ Cannot process dispatch - OpenAI not connected");
              return;
            }
            
            // Format fare/eta
            const formattedFare = fare ? (fare.toString().startsWith("£") ? fare : `£${fare}`) : "£12.50";
            const formattedEta = eta_minutes ? `${eta_minutes} minutes` : (eta || "5 minutes");
            
            // Store fare/eta in booking state
            bookingState.fare = formattedFare;
            bookingState.eta = formattedEta;
            currentStep = "awaiting_final";
            
            // Use the provided message or construct one
            const spokenMessage = message || `The fare will be ${formattedFare} and your driver will be approximately ${formattedEta}. Shall I go ahead and book that for you?`;
            
            // Inject the fare message as a system instruction
            const fareInstruction = `[DISPATCH CALLBACK RECEIVED]
Fare: ${formattedFare}
ETA: ${formattedEta}
Booking Ref: ${booking_ref || "pending"}

SAY EXACTLY THIS TO THE CUSTOMER (verbatim):
"${spokenMessage}"

After speaking, wait for them to say YES or NO.
- If YES: call book_taxi(action="confirmed")
- If NO: ask what they'd like to change`;
            
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{ type: "input_text", text: fareInstruction }]
              }
            }));
            
            // Trigger Ada to speak
            openaiWs?.send(JSON.stringify({ type: "response.create" }));
            log("✅ Fare instruction injected, triggering Ada response");
          });
          
          dispatchChannel.subscribe((status) => {
            log(`📡 Dispatch channel status: ${status}`);
          });
          
          log(`📡 Subscribed to dispatch channel: dispatch_${callId}`);
        }
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        openaiWs?.close();
      }
      
      // Handle phone update from bridge (sent after MSG_UUID from Asterisk)
      if (msg.type === "update_phone") {
        const rawPhone = msg.phone || msg.user_phone;
        if (rawPhone && rawPhone !== "unknown") {
          // Format for WhatsApp: remove + and leading zeros
          callerPhone = String(rawPhone).replace(/^\+/, "").replace(/^0+/, "");
          log(`📱 Phone updated: ${callerPhone}`);
        }
      }
    } catch {
      // Ignore non-JSON
    }
  };

  socket.onclose = () => {
    log("🔌 Bridge disconnected");
    openaiWs?.close();
  };

  return response;
});
