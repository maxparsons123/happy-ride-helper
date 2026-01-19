/**
 * taxi-realtime-paired: Stateful Context-Pairing Architecture
 * 
 * This function implements the "Context Pairing" pattern where:
 * 1. Every user response is paired with the last assistant question
 * 2. OpenAI sees the full conversation context to correctly map answers
 * 3. Booking state is tracked in the database for consistency
 */

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Environment
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY") || "";
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";

const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

// OpenAI Realtime API config
const OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17";
const VOICE = "shimmer";

// ---------------------------------------------------------------------------
// Audio helpers (mirror taxi-realtime-simple behavior)
// OpenAI Realtime requires PCM16 @ 24kHz for input_audio_buffer.append.
// Our bridge can send: ulaw (8kHz), slin (8kHz PCM16), slin16 (16kHz PCM16)
// ---------------------------------------------------------------------------

type InboundAudioFormat = "ulaw" | "slin" | "slin16";

function ulawToPcm16(ulaw: Uint8Array): Int16Array {
  const out = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    const u = (~ulaw[i]) & 0xff;
    const sign = (u & 0x80) ? -1 : 1;
    const exponent = (u >> 4) & 0x07;
    const mantissa = u & 0x0f;
    let sample = ((mantissa << 3) + 0x84) << exponent;
    sample -= 0x84;
    out[i] = sign * sample;
  }
  return out;
}

function resamplePcm16To24k(pcm: Int16Array, inputSampleRate: number): Int16Array {
  if (inputSampleRate === 24000) return pcm;

  if (inputSampleRate === 16000) {
    // 16kHz → 24kHz (1.5x using 3:2 ratio)
    const outLen = Math.floor((pcm.length * 3) / 2);
    const out = new Int16Array(outLen);
    for (let i = 0; i < outLen; i++) {
      const srcIdx = (i * 2) / 3;
      const idx0 = Math.floor(srcIdx);
      const idx1 = Math.min(idx0 + 1, pcm.length - 1);
      const frac = srcIdx - idx0;
      out[i] = Math.round(pcm[idx0] * (1 - frac) + pcm[idx1] * frac);
    }
    return out;
  }

  // Default: 8kHz → 24kHz (3x linear interpolation)
  const out = new Int16Array(pcm.length * 3);
  for (let i = 0; i < pcm.length - 1; i++) {
    const s0 = pcm[i];
    const s1 = pcm[i + 1];
    out[i * 3] = s0;
    out[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
    out[i * 3 + 2] = Math.round(s0 + ((s1 - s0) * 2) / 3);
  }
  const lastIdx = Math.max(pcm.length - 1, 0);
  out[lastIdx * 3] = pcm[lastIdx] ?? 0;
  out[lastIdx * 3 + 1] = pcm[lastIdx] ?? 0;
  out[lastIdx * 3 + 2] = pcm[lastIdx] ?? 0;
  return out;
}

function bytesToBase64(bytes: Uint8Array): string {
  // Avoid spread operator which can overflow the call stack on larger buffers
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const chunk = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
    for (let j = 0; j < chunk.length; j++) binary += String.fromCharCode(chunk[j]);
  }
  return btoa(binary);
}

function pcm16ToBase64(pcm: Int16Array): string {
  const bytes = new Uint8Array(pcm.buffer, pcm.byteOffset, pcm.byteLength);
  return bytesToBase64(bytes);
}


// System prompt - same as taxi-realtime-simple (NO ADDRESS VERIFICATION)
const SYSTEM_PROMPT = `
# IDENTITY
You are Ada, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual. Speak at a SLOWER, relaxed pace - not rushed.

# LANGUAGE
- You MUST speak in British English at all times.
- Use British spelling and vocabulary: "colour" not "color", "travelling" not "traveling", "centre" not "center".
- Use British phrases: "straightaway" not "right away", "lovely" not "great", "brilliant" not "awesome".
- Currency is pounds (£), not dollars.

# SPEAKING STYLE
- Speak slowly and clearly, with natural pauses between sentences.
- Do not rush through your responses.
- Take your time with each word, especially addresses and numbers.
- Use a calm, measured pace that is easy to understand over the phone.

# 🛑 CRITICAL LOGIC GATE: THE CHECKLIST
You have a mental checklist of 4 items: [Pickup], [Destination], [Passengers], [Time].
- You are FORBIDDEN from moving to the 'Booking Summary' until ALL 4 items are specifically provided by the user.
- NEVER use 'As directed' as a placeholder. If a detail is missing, ask for it.

# 🚨 ONE QUESTION RULE (CRITICAL)
- Ask ONLY ONE question per response. NEVER combine questions.
- WRONG: "Where would you like to be picked up and where are you going?"
- WRONG: "How many passengers and when do you need it?"
- RIGHT: "Where would you like to be picked up?" [wait for answer]
- RIGHT: "And what is your destination?" [wait for answer]
- Wait for a user response before asking the next question.

# PHASE 1: THE WELCOME (Play immediately)
"Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started."

# PHASE 2: SEQUENTIAL GATHERING (Strict Order - NO CONFIRMATIONS)
Follow this order exactly. Only move to the next if you have the current answer:
1. "Where would you like to be picked up?" → Wait for answer, then proceed
2. "And what is your destination?" → Wait for answer, then proceed
3. "How many people will be travelling?" → Wait for answer, then proceed  
4. "When do you need the taxi?" → Wait for answer (Default to 'Now' if ASAP)

🚨 CRITICAL: NEVER ASK USER TO CONFIRM/REPEAT AN ADDRESS 🚨
🚫 DO NOT say "Could you please confirm the pickup address?"
🚫 DO NOT say "Could you confirm the destination?"
🚫 DO NOT say "Is that the correct address?"
🚫 DO NOT say "I need to confirm the pickup location"
🚫 DO NOT confirm or repeat back each answer individually.
🚫 DO NOT say "Got it" or "Great" or "OK" before each question - just ask the question directly.
🚫 DO NOT say "So you want to go to X?" after they give an address.
🚫 DO NOT combine multiple questions into one sentence.
✅ After receiving an answer, immediately ask the NEXT question with no filler words.
✅ Save all confirmations for the Summary phase.
✅ ACCEPT ANY ADDRESS AS-IS - do NOT ask for house numbers, postcodes, or more details.
✅ Accept business names, landmarks, partial addresses, and place names immediately.
✅ If user gives an address, TRUST IT and move on. Do not ask them to repeat it.

# PHASE 3: THE SUMMARY (Gate Keeper)
Only after the checklist is 100% complete, say:
"Alright, let me quickly summarize your booking. You'd like to be picked up at [pickup address], and travel to [destination address]. There will be [number] of passengers, and you'd like to be picked up [time]. Is that correct?"

# PHASE 4: PRICING (State Lock)
🚨🚨🚨 MANDATORY FUNCTION CALL 🚨🚨🚨
When user confirms summary with 'Yes', you MUST:
1. Say EXACTLY: "Great, one moment please while I check the trip price."
2. IMMEDIATELY call the book_taxi function with action='request_quote'
3. You CANNOT check the price without calling book_taxi(action='request_quote')
4. If you don't call the function, you will NEVER get a price

⚠️ THE FUNCTION CALL IS REQUIRED - speaking alone is not enough!
The book_taxi(action='request_quote') function sends the request to dispatch.
Without calling it, there is no way to get a price quote.

After calling book_taxi(action='request_quote'):
→ Say ONLY: "One moment please while I check that for you."
→ Then STOP TALKING COMPLETELY.
→ WAIT IN COMPLETE SILENCE until you receive a [DISPATCH QUOTE RECEIVED] message.
→ Do NOT make up any prices. Do NOT estimate any ETAs. Do NOT guess.

🚨🚨🚨 ABSOLUTE PRICING PROHIBITION 🚨🚨🚨
- You have ZERO knowledge of fares, prices, or costs.
- You CANNOT calculate, estimate, or guess any price.
- You MUST wait for the external dispatch system to provide the price.
- The ONLY way you will know a price is when you receive a [DISPATCH QUOTE RECEIVED] message.
- Until that message arrives, you know NOTHING about the fare.
- If you say ANY number as a price before receiving [DISPATCH QUOTE RECEIVED], YOU ARE WRONG.
- NEVER say "the fare will be", "that will cost", "the price is" until dispatch tells you.
- If user asks about price before dispatch responds, say ONLY: "I'm just checking that for you now, one moment."

⏳ SILENCE IS REQUIRED:
After saying "One moment please while I check that for you", be COMPLETELY SILENT.
Do not speak. Do not guess. Do not estimate. Just wait.
The dispatch system will send you the real price via [DISPATCH QUOTE RECEIVED].

Once you receive [DISPATCH QUOTE RECEIVED] with the ACTUAL price, say ONLY:
"The trip fare will be [EXACT price from dispatch], and the estimated arrival time is [EXACT ETA from dispatch]. Would you like to go ahead and book that?"
🚫 RULE: Do NOT repeat addresses here. Focus only on Price and ETA.
🚫 RULE: The price and ETA MUST be the exact values from the [DISPATCH QUOTE RECEIVED] message.
🚫 RULE: Do NOT call book_taxi(action='confirmed') yet - WAIT for user's explicit YES/NO response.

# PHASE 5: DISPATCH & CLOSE - WAIT FOR EXPLICIT CONFIRMATION
🚨🚨🚨 CRITICAL: YOU MUST WAIT FOR USER TO SAY YES OR NO 🚨🚨🚨

After asking "Would you like to go ahead and book that?", WAIT for user response:

IF USER SAYS YES (yes, yeah, yep, sure, go ahead, book it, confirm, please):
1. Say: "Perfect, I'm booking that for you now. You'll receive the details via WhatsApp."
2. IMMEDIATELY call book_taxi(action='confirmed')
3. Then say goodbye and call end_call()

IF USER SAYS NO (no, nope, not now, forget it, never mind, actually no):
1. IMMEDIATELY call cancel_booking
2. Say: "No problem, I've cancelled that. Is there anything else I can help with?"
3. If user says no again, say goodbye and call end_call()

🚫 NEVER call book_taxi(action='confirmed') until user explicitly says YES to the price/ETA confirmation
🚫 NEVER assume "yes" - wait for their actual response

Choose ONE closing randomly after confirmation:
- "Just so you know, you can also book a taxi by sending us a WhatsApp voice note."
- "Next time, feel free to book your taxi using a WhatsApp voice message."
- "You can always book again by simply sending us a voice note on WhatsApp."

Final Sign-off: "Thank you for trying the Taxibot demo, and have a safe journey."
→ CALL end_call()

# CANCELLATION
If user says "cancel", "never mind", "forget it", or "no" to booking confirmation:
→ CALL cancel_booking
Say: "No problem, I've cancelled that. Is there anything else I can help with?"

# NAME HANDLING
If caller says their name → CALL save_customer_name

# GUARDRAILS
❌ NEVER state a price or ETA unless the tool returns that exact value.
❌ NEVER use 'As directed' or any placeholder - always ask for specifics.
❌ NEVER move to Summary until all 4 checklist items are filled.
❌ NEVER repeat addresses after the summary is confirmed.
❌ NEVER ask for house numbers, postcodes, or more details on ANY address.
❌ NEVER ask "is that where you want to go?" or "is that correct?" after each address.
❌ NEVER ask for "more details" or "could you be more specific".
✅ Accept ANY address exactly as spoken - street names, business names, landmarks, partial addresses.
✅ If user says "High Street" or "Tesco" or "the hospital" - accept it immediately.
✅ Move to the next question immediately after receiving any address.

# CONTEXT PAIRING (CRITICAL)
When the user responds, ALWAYS check what question you just asked them:
- If you asked for PICKUP and they respond → it's the pickup location
- If you asked for DESTINATION and they respond → it's the destination  
- If you asked for PASSENGERS and they respond → it's the passenger count
- If you asked for TIME and they respond → it's the pickup time
NEVER swap fields. Trust the question context.
`;

// Tools - same as taxi-realtime-simple
const TOOLS = [
  {
    type: "function",
    name: "save_customer_name",
    description: "Save customer name when caller provides it.",
    parameters: { 
      type: "object", 
      properties: { name: { type: "string" } }, 
      required: ["name"] 
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Used to get quotes or finalize bookings.",
    parameters: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["request_quote", "confirmed"], description: "Use 'request_quote' first to get fare/ETA, then 'confirmed' after user accepts." },
        pickup: { type: "string", description: "COMPLETE pickup address exactly as the user said it. Include house number, street name, and any other details they provided. E.g. 'Number 1, Lifford Lane' NOT just 'Number 1'." },
        destination: { type: "string", description: "COMPLETE destination address exactly as the user said it. Include all details they provided. E.g. 'Birmingham New Street Station' NOT just 'New Street'." },
        passengers: { type: "integer", minimum: 1, description: "Number of passengers" },
        time: { type: "string", description: "When taxi is needed (e.g., 'now', '3pm')" }
      },
      required: ["action"]
    }
  },
  {
    type: "function",
    name: "cancel_booking",
    description: "Cancel active booking.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "end_call",
    description: "Call this to disconnect the SIP line after the safe journey message.",
    parameters: { type: "object", properties: {} }
  }
];

// Session state interface
interface SessionState {
  callId: string;
  callerPhone: string;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    pickupTime: string | null;
  };
  lastQuestionAsked: "pickup" | "destination" | "passengers" | "time" | "confirmation" | "none";
  conversationHistory: Array<{ role: string; content: string; timestamp: number }>;
  bookingConfirmed: boolean;
  openAiResponseActive: boolean;
  // Track when Ada started speaking (ms since epoch) to prevent echo-triggered barge-in
  openAiSpeechStartedAt: number;
  // Echo guard: block audio forwarding for a short window after Ada finishes speaking
  echoGuardUntil: number;
  // Track when Ada last finished speaking (for echo detection)
  lastAdaFinishedSpeakingAt: number;
  // Greeting protection: ignore inbound audio right after connect
  greetingProtectionUntil: number;
  // Summary protection: block interruptions while Ada is recapping/quoting
  summaryProtectionUntil: number;
  // Quote request de-dupe
  quoteInFlight: boolean;
  lastQuoteRequestedAt: number;
  // Dispatch callback state
  dispatchJobId: string | null;
  pendingBookingRef: string | null;
  pendingConfirmationCallback: string | null;
  pendingFare: string | null;
  pendingEta: string | null;
  awaitingConfirmation: boolean;
  bookingRef: string | null;
  // Flag to silence Ada after she says "one moment" until fare arrives
  waitingForQuoteSilence: boolean;
  // Track if Ada already said "one moment" for this quote request
  saidOneMoment: boolean;
}

const ECHO_GUARD_MS = 250;

// Greeting protection window in ms - ignore early line noise so Ada's first prompt doesn't get cut off
const GREETING_PROTECTION_MS = 3000;

// Summary protection window in ms - prevent interruptions while Ada recaps booking or quotes fare
const SUMMARY_PROTECTION_MS = 8000;

// While Ada is speaking, ignore the first slice of inbound audio to avoid echo/noise cutting her off
const ASSISTANT_LEADIN_IGNORE_MS = 700;

// Barge-in RMS thresholds to distinguish real speech from echo/noise
// (Higher minimum reduces false barge-ins from background/line noise)
const BARGE_IN_RMS_MIN = 1000;
const BARGE_IN_RMS_MAX = 20000;

// Whisper "phantom radio host" hallucinations - triggered by silence/static
const PHANTOM_PHRASES = [
  "thanks for tuning in",
  "thank you for tuning in",
  "i'm your host",
  "im your host",
  "find me on facebook",
  "find me on twitter",
  "follow me on",
  "thank you for watching",
  "thanks for watching",
  "subtitles by",
  "please like and subscribe",
  "like and subscribe",
  "don't forget to subscribe",
  "hit that subscribe button",
  "leave a comment",
  "see you next time",
  "until next time",
  "this has been",
  "you've been listening to",
  "you have been listening to",
  "brought to you by",
  "sponsored by",
  "music playing",
  "silence",
  "inaudible",
  "foreign language",
  "[music]",
  "[applause]",
  "[laughter]",
  // Non-English phantom phrases
  "ondertitels",
  "amara.org",
  "次回へ続く", // Japanese "to be continued"
  "ご視聴ありがとうございました",
];

// --- STT Corrections ---
// Phonetic fixes for common telephony mishearings
// Keys are stored in lowercase for O(1) lookup
const STT_CORRECTIONS: Record<string, string> = {
  // Cancel intent variations (telephony noise triggers these)
  "come to sleep": "cancel it",
  "go to sleep": "cancel it",
  "come see it": "cancel it",
  "count to three": "cancel",
  "can't sell it": "cancel it",
  "cancel eat": "cancel it",
  "concert": "cancel",
  "counter": "cancel",
  "counsel": "cancel",
  "council": "cancel",
  "can so": "cancel",
  "can soul": "cancel",
  
  // Airport/Station mishearings
  "heather oh": "Heathrow",
  "heather row": "Heathrow",
  "heath row": "Heathrow",
  "heather o": "Heathrow",
  "heat throw": "Heathrow",
  "gat wick": "Gatwick",
  "got wick": "Gatwick",
  "cat wick": "Gatwick",
  "stand stead": "Stansted",
  "stan stead": "Stansted",
  "luton air port": "Luton Airport",
  "birmingham air port": "Birmingham Airport",
  "man chest her": "Manchester",
  "man chester": "Manchester",
  "lead station": "Leeds station",
  "leads station": "Leeds station",
  "kings cross": "King's Cross",
  "saint pan crass": "St Pancras",
  "saint pancreas": "St Pancras",
  "euston": "Euston",
  "you stone": "Euston",
  "padding ton": "Paddington",
  "victoria coach": "Victoria Coach Station",
  
  // City/Area mishearings
  "click on street": "Coventry",
  "coven tree": "Coventry",
  "cover entry": "Coventry",
  "birming ham": "Birmingham",
  "burming ham": "Birmingham",
  "wolver hampton": "Wolverhampton",
  "wolves hampton": "Wolverhampton",
  "leaming ton": "Leamington",
  "lemington": "Leamington",
  "warrick": "Warwick",
  "war wick": "Warwick",
  "nun eaten": "Nuneaton",
  "new neaten": "Nuneaton",
  "ken ill worth": "Kenilworth",
  "bed worth": "Bedworth",
  "rugby": "Rugby",
  "rug bee": "Rugby",
  
  // Street name mishearings
  "david rose": "David Road",
  "davie road": "David Road",
  "davey road": "David Road",
  "davery road": "David Road",
  "david wrote": "David Road",
  "david rhoades": "David Road",
  "david rhodes": "David Road",
  "david rodes": "David Road",
  "david roads": "David Road",
  "david row": "David Road",
  "david rowe": "David Road",
  "david rode": "David Road",
  "david roat": "David Road",
  "this is qa, david rhoades": "52A David Road",
  "this is qa david rhoades": "52A David Road",
  "52 qa david": "52A David Road",
  "qa david": "52A David",
  "high street": "High Street",
  "hi street": "High Street",
  "church road": "Church Road",
  "church wrote": "Church Road",
  "station road": "Station Road",
  "station wrote": "Station Road",
  "london road": "London Road",
  "london wrote": "London Road",

  // Russell Street mishearings
  "ruffles street": "Russell Street",
  "ruffles sthreet": "Russell Street",
  "ruffle street": "Russell Street",
  "ruffels street": "Russell Street",
  "ruffals street": "Russell Street",
  "roswell street": "Russell Street",
  "russle street": "Russell Street",
  "russel street": "Russell Street",
  "raffles street": "Russell Street",
  "refills street": "Russell Street",
  
  // Specific mishearings observed in testing
  "exum road": "Exmoor Road",
  "exxon roll": "Exmoor Road",
  "bexham road": "Exmoor Road",
  
  // Sweet Spot venue mishearings
  "sweetbutt": "Sweet Spot",
  "sweet butt": "Sweet Spot",
  "sweetbatz": "Sweet Spot",
  "sweet batz": "Sweet Spot",
  "sweetbats": "Sweet Spot",
  "sweet bats": "Sweet Spot",
  "sweet spots": "Sweet Spot",
  "sweetspots": "Sweet Spot",
  "swee spot": "Sweet Spot",
  "suite spot": "Sweet Spot",
  "sweetbriar court": "Sweet Spot",
  "sweetbriar": "Sweet Spot",
  "sweet briar": "Sweet Spot",
  "sweetbrier": "Sweet Spot",
  "sweet brier": "Sweet Spot",
  "street spots": "Sweet Spot",
  "streetspots": "Sweet Spot",
  "street spot": "Sweet Spot",

  // Number mishearings - standalone numbers
  "free": "three",
  "tree": "three",
  "for": "four",
  "to": "two",
  "too": "two",
  "won": "one",
  "wan": "one",
  "fife": "five",
  "sicks": "six",
  "freight": "eight",
  "fright": "eight",
  "fate": "eight",
  
  // Number mishearings - with "passengers"
  "for passengers": "4 passengers",
  "fore passengers": "4 passengers",
  "to passengers": "2 passengers",
  "too passengers": "2 passengers",
  "tree passengers": "3 passengers",
  "free passengers": "3 passengers",
  "one passenger": "1 passenger",
  "won passenger": "1 passenger",
  "wan passenger": "1 passenger",
  "five passengers": "5 passengers",
  "fife passengers": "5 passengers",
  "six passengers": "6 passengers",
  "sicks passengers": "6 passengers",
  "freight passengers": "8 passengers",
  "eight passengers": "8 passengers",
  
  // Common phrases
  "pick me up": "pick me up",
  "pick up from": "pick up from",
  "going to": "going to",
  "go into": "going to",
  "drop off at": "drop off at",
  "drop of at": "drop off at",
  
  // House number suffixes
  "a high": "A High",
  "b high": "B High",
  "fifty to a": "52A",
  "fifty too a": "52A",
  "fifty two a": "52A",
  "52 a": "52A",
  
  // Common number mishearings in addresses
  "528 david": "52A David",
  "five twenty eight david": "52A David",
  "five two eight david": "52A David",
  "5208 david": "52A David",
  "five two a david": "52A David",
  "fifty two a david": "52A David",
};

// Pre-compiled regex pattern for O(1) STT correction lookups
const STT_CORRECTION_PATTERN = new RegExp(
  `\\b(${Object.keys(STT_CORRECTIONS).map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})\\b`,
  'gi'
);

// Pre-built lowercase lookup map for O(1) correction retrieval
const STT_CORRECTIONS_LOWER = new Map(
  Object.entries(STT_CORRECTIONS).map(([k, v]) => [k.toLowerCase(), v])
);

function correctTranscript(text: string): string {
  if (!text || text.length === 0) return "";
  
  // Single-pass replacement using pre-compiled pattern
  const corrected = text.replace(STT_CORRECTION_PATTERN, (matched) => {
    return STT_CORRECTIONS_LOWER.get(matched.toLowerCase()) || matched;
  });
  
  // Capitalize first letter and return
  return corrected.charAt(0).toUpperCase() + corrected.slice(1);
}

function isPhantomHallucination(text: string): boolean {
  const lower = text.toLowerCase().trim();
  if (lower.length < 2) return true;
  for (const phrase of PHANTOM_PHRASES) {
    if (lower.includes(phrase.toLowerCase())) return true;
  }
  // Detect non-Latin scripts that are unlikely to be real user input for UK taxi booking
  // Allow common accented characters but filter pure non-Latin
  const nonLatinRatio = (text.match(/[^\x00-\x7F\u00C0-\u017F]/g) || []).length / text.length;
  if (nonLatinRatio > 0.5 && text.length > 3) return true;
  return false;
}

// Detect if Ada is hallucinating a price or ETA without having received one from dispatch
function isPriceOrEtaHallucination(text: string, hasPendingFare: boolean): boolean {
  if (hasPendingFare) return false; // We have a real fare, so it's not hallucination
  
  const lower = text.toLowerCase();
  
  // Price patterns (£X, X pounds, fare of, cost of, etc.)
  const pricePatterns = [
    /£\d+/,
    /\d+\s*pounds?/,
    /\d+\s*p\b/,
    /fare\s+(is|will be|of)\s*£?\d+/,
    /cost\s+(is|will be|of)\s*£?\d+/,
    /price\s+(is|will be|of)\s*£?\d+/,
    /that('s|ll be)\s*£?\d+/,
    /around\s*£?\d+/,
    /approximately\s*£?\d+/,
    /about\s*£?\d+/,
  ];
  
  // ETA patterns (X minutes, arrive in X, driver will be X)
  const etaPatterns = [
    /(\d+)\s*minutes?/,
    /arrive\s+(in|within)\s*\d+/,
    /driver\s+(will be|is)\s*\d+/,
    /eta\s+(is|of)\s*\d+/,
    /be there\s+(in|within)\s*\d+/,
    /arrival\s+(time|is)\s*\d+/,
  ];
  
  for (const pattern of pricePatterns) {
    if (pattern.test(lower)) {
      return true;
    }
  }
  
  for (const pattern of etaPatterns) {
    if (pattern.test(lower)) {
      return true;
    }
  }
  
  return false;
}

// Detect address corrections in user speech (e.g., "It's 52A David Road", "No, it should be...", "Actually...")
interface AddressCorrection {
  type: "pickup" | "destination" | null;
  address: string;
}

function detectAddressCorrection(text: string, currentPickup: string | null, currentDestination: string | null): AddressCorrection {
  const lower = text.toLowerCase();
  
  // Correction trigger phrases
  const correctionPhrases = [
    /^it'?s\s+(.+)/i,                          // "It's 52A David Road"
    /^no[,\s]+it'?s\s+(.+)/i,                  // "No, it's..."
    /^actually[,\s]+(.+)/i,                    // "Actually 52A David Road"
    /^i meant\s+(.+)/i,                        // "I meant 52A..."
    /^i said\s+(.+)/i,                         // "I said 52A..."
    /^should be\s+(.+)/i,                      // "Should be 52A..."
    /^it should be\s+(.+)/i,                   // "It should be..."
    /^sorry[,\s]+(.+)/i,                       // "Sorry, 52A David Road"
    /^correction[:\s]+(.+)/i,                  // "Correction: 52A..."
    /^the (?:pickup|address) is\s+(.+)/i,     // "The pickup is..."
    /^(?:no[,\s]+)?that'?s\s+(.+)/i,          // "That's 52A..." or "No, that's 52A..."
  ];
  
  let extractedAddress: string | null = null;
  
  for (const pattern of correctionPhrases) {
    const match = text.match(pattern);
    if (match && match[1]) {
      extractedAddress = match[1].trim();
      // Remove trailing punctuation
      extractedAddress = extractedAddress.replace(/[.,!?]+$/, '').trim();
      break;
    }
  }
  
  if (!extractedAddress || extractedAddress.length < 3) {
    return { type: null, address: "" };
  }
  
  // Determine if this is correcting pickup or destination
  // Check for explicit field mentions
  if (lower.includes("pickup") || lower.includes("pick up") || lower.includes("from")) {
    return { type: "pickup", address: extractedAddress };
  }
  if (lower.includes("destination") || lower.includes("to") || lower.includes("going to") || lower.includes("drop")) {
    return { type: "destination", address: extractedAddress };
  }
  
  // If current pickup exists and the correction seems similar to it, it's likely a pickup correction
  if (currentPickup) {
    const pickupLower = currentPickup.toLowerCase();
    const correctionLower = extractedAddress.toLowerCase();
    // Check if they share common words (street name, etc.)
    const pickupWords = pickupLower.split(/\s+/).filter(w => w.length > 2);
    const correctionWords = correctionLower.split(/\s+/).filter(w => w.length > 2);
    const commonWords = pickupWords.filter(w => correctionWords.some(cw => cw.includes(w) || w.includes(cw)));
    if (commonWords.length > 0) {
      return { type: "pickup", address: extractedAddress };
    }
  }
  
  // Default: if we have a pickup but no destination, assume it's still about pickup
  // If we have both, it's more likely a pickup correction (more common)
  if (currentPickup && !currentDestination) {
    return { type: "pickup", address: extractedAddress };
  }
  
  // Default to pickup correction if uncertain
  return { type: "pickup", address: extractedAddress };
}


function computeRms(pcm: Int16Array): number {
  if (pcm.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < pcm.length; i++) {
    sum += pcm[i] * pcm[i];
  }
  return Math.sqrt(sum / pcm.length);
}

// Create initial session state
function createSessionState(callId: string, callerPhone: string): SessionState {
  return {
    callId,
    callerPhone,
    booking: {
      pickup: null,
      destination: null,
      passengers: null,
      pickupTime: null
    },
    lastQuestionAsked: "none",
    conversationHistory: [],
    bookingConfirmed: false,
    openAiResponseActive: false,
    openAiSpeechStartedAt: 0,
    echoGuardUntil: 0,
    lastAdaFinishedSpeakingAt: 0,
    greetingProtectionUntil: Date.now() + GREETING_PROTECTION_MS,
    summaryProtectionUntil: 0,
    quoteInFlight: false,
    lastQuoteRequestedAt: 0,
    // Dispatch callback state
    dispatchJobId: null,
    pendingBookingRef: null,
    pendingConfirmationCallback: null,
    pendingFare: null,
    pendingEta: null,
    awaitingConfirmation: false,
    bookingRef: null,
    waitingForQuoteSilence: false,
    saidOneMoment: false
  };
}

// Build context-paired messages for OpenAI
function buildContextPairedMessages(sessionState: SessionState): Array<{ role: string; content: string }> {
  const messages: Array<{ role: string; content: string }> = [];
  
  // Add system context about current state
  const stateContext = `
[CURRENT BOOKING STATE]
- Pickup: ${sessionState.booking.pickup || "NOT SET"}
- Destination: ${sessionState.booking.destination || "NOT SET"}
- Passengers: ${sessionState.booking.passengers ?? "NOT SET"}
- Time: ${sessionState.booking.pickupTime || "NOT SET"}
- Last Question Asked: ${sessionState.lastQuestionAsked}

[CONTEXT PAIRING INSTRUCTION]
The last question asked was about "${sessionState.lastQuestionAsked}".
If the user's next response contains location/address/place info and last_question was "pickup" → it's the pickup.
If the user's next response contains location/address/place info and last_question was "destination" → it's the destination.
NEVER swap fields. Trust the question context.
`;

  messages.push({ role: "system", content: SYSTEM_PROMPT + stateContext });
  
  // Add last 6 conversation turns for context (3 exchanges)
  const recentHistory = sessionState.conversationHistory.slice(-6);
  for (const msg of recentHistory) {
    messages.push({ role: msg.role, content: msg.content });
  }
  
  return messages;
}

// Update live_calls table with current state
async function updateLiveCall(sessionState: SessionState) {
  try {
    const { error } = await supabase
      .from("live_calls")
      .upsert({
        call_id: sessionState.callId,
        caller_phone: sessionState.callerPhone,
        pickup: sessionState.booking.pickup,
        destination: sessionState.booking.destination,
        passengers: sessionState.booking.passengers,
        status: sessionState.bookingConfirmed ? "confirmed" : "active",
        booking_confirmed: sessionState.bookingConfirmed,
        transcripts: sessionState.conversationHistory,
        source: "paired",
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
    
    if (error) {
      console.error(`[${sessionState.callId}] Failed to update live_calls:`, error);
    }
  } catch (e) {
    console.error(`[${sessionState.callId}] Error updating live_calls:`, e);
  }
}

// Send dispatch webhook - matches taxi-realtime-simple format exactly
// Simple approach: 5s timeout, no retries, rely on Realtime callback
async function sendDispatchWebhook(
  sessionState: SessionState,
  action: string,
  bookingData: Record<string, unknown>
): Promise<{ success: boolean; fare?: string; eta?: string; error?: string }> {
  const callId = sessionState.callId;
  
  if (!DISPATCH_WEBHOOK_URL) {
    console.log(`[${callId}] ⚠️ No DISPATCH_WEBHOOK_URL configured, simulating response`);
    return {
      success: true,
      fare: "£8.50",
      eta: "5 minutes"
    };
  }

  // Reuse a stable job_id across request_quote → confirmed for the same call
  if (!sessionState.dispatchJobId) {
    sessionState.dispatchJobId = crypto.randomUUID();
  }
  const jobId = sessionState.dispatchJobId;
  
  // Format phone number (remove + prefix if present)
  const formattedPhone = sessionState.callerPhone.replace(/^\+/, "");
  
  // Build user transcripts from conversation history
  const userTranscripts = sessionState.conversationHistory
    .filter(msg => msg.role === "user")
    .map(msg => ({
      text: msg.content.replace(/^\[CONTEXT:.*?\]\s*/i, ""), // Remove context prefix
      timestamp: new Date(msg.timestamp).toISOString()
    }));
  
  // Match the taxi-realtime-simple webhook payload format
  const webhookPayload = {
    job_id: jobId,
    call_id: sessionState.callId,
    caller_phone: formattedPhone,
    caller_name: null,
    action,
    call_action: action,
    confirmation_state: action,
    booking_ref: sessionState.pendingBookingRef || sessionState.bookingRef || null,
    ada_pickup: bookingData.pickup || sessionState.booking.pickup,
    ada_destination: bookingData.destination || sessionState.booking.destination,
    callers_pickup: null,
    callers_dropoff: null,
    nearest_pickup: null,
    nearest_dropoff: null,
    user_transcripts: userTranscripts,
    gps_lat: null,
    gps_lon: null,
    passengers: (bookingData.passengers ?? sessionState.booking.passengers ?? null),
    bags: 0,
    vehicle_type: "standard",
    vehicle_request: null,
    pickup_time: bookingData.pickup_time || sessionState.booking.pickupTime || "ASAP",
    special_requests: null,
    timestamp: new Date().toISOString()
  };
  
  console.log(`[${callId}] 📡 Sending webhook (${action}):`, JSON.stringify(webhookPayload));

  // Retry logic like simple version - more reliable
  const MAX_RETRIES = 2;
  const RETRY_DELAY_MS = 1000;
  // Some dispatch systems can be slow to respond; since the real quote arrives asynchronously via callback,
  // we mainly need the POST to go through reliably.
  const TIMEOUT_MS = 30000;
  
  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), TIMEOUT_MS);

      console.log(`[${callId}] 📡 Webhook attempt ${attempt}/${MAX_RETRIES}...`);
      const response = await fetch(DISPATCH_WEBHOOK_URL, {
        method: "POST",
        headers: { 
          "Content-Type": "application/json",
          "X-Call-ID": callId,
          "X-Job-ID": jobId
        },
        body: JSON.stringify(webhookPayload),
        signal: controller.signal
      });

      clearTimeout(timeoutId);

      const respBody = await response.text().catch(() => "");
      console.log(
        `[${callId}] 📬 Dispatch webhook response (attempt ${attempt}): ${response.status} ${response.statusText}` +
        (respBody ? ` - ${respBody.slice(0, 300)}` : "")
      );

      if (!response.ok) {
        if (attempt < MAX_RETRIES) {
          console.log(`[${callId}] ⚠️ Webhook failed (${response.status}), retrying in ${RETRY_DELAY_MS}ms...`);
          await new Promise(r => setTimeout(r, RETRY_DELAY_MS));
          continue;
        }
        return { success: false, error: `Webhook returned ${response.status}` };
      }

      // Parse JSON response if available
      let data: any = null;
      try {
        if (respBody && respBody.trim().startsWith("{")) {
          data = JSON.parse(respBody);
        }
      } catch (_) {
        // Not JSON, that's fine
      }

      console.log(`[${callId}] ✅ Webhook sent successfully on attempt ${attempt}`);
      return {
        success: true,
        fare: data?.fare || data?.estimated_fare,
        eta: data?.eta || data?.estimated_eta
      };
    } catch (e) {
      const errMsg = String(e);
      const isTimeout = errMsg.includes("abort") || errMsg.includes("timeout");
      console.error(`[${callId}] ❌ Webhook attempt ${attempt} failed: ${errMsg}`);
      
      if (attempt < MAX_RETRIES) {
        console.log(`[${callId}] 🔄 Retrying webhook in ${RETRY_DELAY_MS}ms...`);
        await new Promise(r => setTimeout(r, RETRY_DELAY_MS));
        continue;
      }
      
      return { success: false, error: isTimeout ? `Webhook timeout after ${Math.round(TIMEOUT_MS / 1000)}s` : errMsg };
    }
  }
  
  return { success: false, error: "All webhook attempts failed" };
}

// Handle WebSocket connection
async function handleConnection(socket: WebSocket, callId: string, callerPhone: string) {
  console.log(`[${callId}] 🎯 PAIRED MODE: New connection from ${callerPhone}`);
  
  const sessionState = createSessionState(callId, callerPhone);
  let openaiWs: WebSocket | null = null;
  let cleanedUp = false;
  let dispatchChannel: ReturnType<typeof supabase.channel> | null = null;

  // Audio format negotiated with the bridge (defaults match typical Asterisk ulaw)
  let inboundAudioFormat: InboundAudioFormat = "ulaw";
  let inboundSampleRate = 8000;

  // Cleanup function
  const cleanup = async () => {
    if (cleanedUp) return;
    cleanedUp = true;
    
    console.log(`[${callId}] 🧹 Cleaning up connection`);
    
    // Unsubscribe from dispatch channel
    if (dispatchChannel) {
      try {
        await dispatchChannel.unsubscribe();
      } catch (e) {
        console.error(`[${callId}] Error unsubscribing from dispatch channel:`, e);
      }
    }
    
    // Update final call state
    try {
      await supabase
        .from("live_calls")
        .update({
          status: sessionState.bookingConfirmed ? "completed" : "ended",
          ended_at: new Date().toISOString()
        })
        .eq("call_id", callId);
    } catch (e) {
      console.error(`[${callId}] Error updating final state:`, e);
    }
    
    if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
      openaiWs.close();
    }
  };

  // ═══════════════════════════════════════════════════════════════════════════
  // Subscribe to dispatch callback channel (for fare/ETA responses from backend)
  // ═══════════════════════════════════════════════════════════════════════════
  dispatchChannel = supabase.channel(`dispatch_${callId}`);
  
  dispatchChannel.on("broadcast", { event: "dispatch_ask_confirm" }, async (payload: any) => {
    const { message, fare, eta, eta_minutes, callback_url, booking_ref } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH ask_confirm: fare=${fare}, eta=${eta_minutes || eta}, message="${message}"`);
    
    if (!message || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) {
      console.log(`[${callId}] ⚠️ Cannot process dispatch ask_confirm - OpenAI not connected or cleaned up`);
      return;
    }
    
    // DUPLICATE PROTECTION: Ignore if we already received a quote and are awaiting confirmation
    if (sessionState.awaitingConfirmation) {
      console.log(`[${callId}] ⚠️ Ignoring duplicate dispatch_ask_confirm - already awaiting confirmation`);
      return;
    }
    
    // DUPLICATE PROTECTION: Ignore if booking is already confirmed
    if (sessionState.bookingConfirmed) {
      console.log(`[${callId}] ⚠️ Ignoring dispatch_ask_confirm - booking already confirmed`);
      return;
    }
    
    // Store the callback URL for when customer confirms
    sessionState.pendingConfirmationCallback = callback_url;
    sessionState.pendingFare = fare;
    sessionState.pendingEta = eta_minutes || eta;
    sessionState.pendingBookingRef = booking_ref || null;
    
    // CLEAR THE SILENCE FLAG - we have the fare now, Ada can speak again!
    sessionState.waitingForQuoteSilence = false;
    sessionState.saidOneMoment = false;
    console.log(`[${callId}] 🔊 Silence mode CLEARED - fare received, Ada can speak`);
    
    // Cancel any active response before injecting dispatch message
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    // CRITICAL: Activate summary protection IMMEDIATELY to prevent barge-in during fare quote
    // This protects Ada's speech from being cut off by background noise or premature user input
    sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
    console.log(`[${callId}] 🛡️ Fare quote protection activated for ${SUMMARY_PROTECTION_MS}ms`);
    
    // Inject the fare/ETA message for Ada to speak - with explicit YES/NO handling instructions
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{
          type: "input_text",
          text: `[DISPATCH QUOTE RECEIVED]: Tell the customer EXACTLY: "${message}". IMPORTANT: do NOT repeat pickup/destination/passengers again. Only say the fare/ETA and ask if they want to proceed.

WHEN CUSTOMER RESPONDS:
- If they say YES / yeah / correct / confirm / go ahead / book it / please → IMMEDIATELY CALL book_taxi with action: "confirmed"
- If they say NO / cancel / too expensive / nevermind → Say "No problem, is there anything else I can help you with?"
- If unclear, ask: "Would you like me to book that for you?"

DO NOT say "booked" or "confirmed" until book_taxi with action: "confirmed" returns success.`
        }]
      }
    }));
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        instructions: `Say EXACTLY this quote: "${message}". Do NOT recap the journey details. Ask if they want to proceed. When they say yes, call book_taxi with action="confirmed". When they say no, ask if there's anything else you can help with.`
      }
    }));
    
    // Track that we're waiting for confirmation
    sessionState.quoteInFlight = false;
    sessionState.awaitingConfirmation = true;
    sessionState.lastQuestionAsked = "confirmation";
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_say" }, async (payload: any) => {
    const { message: sayMessage } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH say: "${sayMessage}"`);
    
    if (!sayMessage || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) {
      return;
    }
    
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    // Activate speech protection so Ada doesn't get cut off
    sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
    console.log(`[${callId}] 🛡️ Dispatch say protection activated for ${SUMMARY_PROTECTION_MS}ms`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: `[DISPATCH UPDATE]: Tell the customer: "${sayMessage}"` }]
      }
    }));
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        instructions: `Say this to the customer: "${sayMessage}"`
      }
    }));
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_confirm" }, async (payload: any) => {
    const { message: confirmMessage, booking_ref } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH confirm: ref=${booking_ref}, "${confirmMessage}"`);
    
    if (!confirmMessage || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) {
      return;
    }
    
    sessionState.bookingConfirmed = true;
    sessionState.bookingRef = booking_ref;
    
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    // Activate speech protection for booking confirmation - use longer window for goodbye
    sessionState.summaryProtectionUntil = Date.now() + (SUMMARY_PROTECTION_MS * 1.5);
    console.log(`[${callId}] 🛡️ Booking confirm protection activated for ${SUMMARY_PROTECTION_MS * 1.5}ms`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: `[BOOKING CONFIRMED]: Tell the customer: "${confirmMessage}" Then say goodbye and end the call.` }]
      }
    }));
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        instructions: `The booking is confirmed! Say this to the customer: "${confirmMessage}" Then say goodbye warmly.`
      }
    }));
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_hangup" }, async (payload: any) => {
    const { message: hangupMessage } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH hangup: "${hangupMessage}"`);
    
    if (hangupMessage && openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
      openaiWs.send(JSON.stringify({ type: "response.cancel" }));
      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

      // Protect this final message from being cut off
      sessionState.summaryProtectionUntil = Date.now() + (SUMMARY_PROTECTION_MS * 2);
      console.log(`[${callId}] 🛡️ Dispatch hangup protection activated for ${SUMMARY_PROTECTION_MS * 2}ms`);
      
      openaiWs.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "message",
          role: "user",
          content: [{ type: "input_text", text: `[DISPATCH HANGUP]: Say this and end: "${hangupMessage}"` }]
        }
      }));
      
      openaiWs.send(JSON.stringify({
        type: "response.create",
        response: {
          modalities: ["audio", "text"],
          instructions: `Say this to end the call: "${hangupMessage}"`
        }
      }));
    }
    
    // Schedule cleanup after giving time for goodbye
    setTimeout(() => cleanup(), 12000);
  });
  
  // Subscribe to the channel
  dispatchChannel.subscribe((status) => {
    console.log(`[${callId}] 📡 Dispatch channel status: ${status}`);
  });

  // Connect to OpenAI Realtime
  // Note: Deno WebSocket requires headers as second argument array for protocols,
  // but OpenAI needs Authorization header. Use the URL with query param workaround
  // or rely on the proper Deno fetch-based approach.
  try {
    // For Deno, we need to use a different approach - create WebSocket with protocols
    const wsUrl = `${OPENAI_REALTIME_URL}`;
    openaiWs = new WebSocket(wsUrl, ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]);
  } catch (e) {
    console.error(`[${callId}] Failed to connect to OpenAI:`, e);
    socket.close();
    return;
  }

  // OpenAI WebSocket handlers
  // Flag to prevent duplicate greetings
  let greetingSent = false;
  
  const sendGreeting = () => {
    if (greetingSent || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    greetingSent = true;
    
    console.log(`[${callId}] 🎙️ Sending initial greeting via response.create only...`);
    
    const greetingText = "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started. Where would you like to be picked up?";
    
    // Simple approach: just request a response with specific instructions
    // Don't inject conversation items - let the model generate from session instructions
    openaiWs!.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],  // Audio first - prioritize voice output
        instructions: `Greet the caller. Say exactly: "${greetingText}"`
      }
    }));
    
    sessionState.lastQuestionAsked = "pickup";
  };

  openaiWs.onopen = () => {
    console.log(`[${callId}] ✅ Connected to OpenAI Realtime`);
    
    // Configure session with context-pairing system
    const sessionConfig = {
      type: "session.update",
      session: {
        modalities: ["text", "audio"],
        voice: VOICE,
        instructions: SYSTEM_PROMPT + `\n\n[CALL CONTEXT]\nCall ID: ${callId}\nCaller: ${callerPhone}`,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        input_audio_transcription: { 
          model: "whisper-1",
          // Prompt hint helps Whisper recognize place names and taxi terminology
          prompt: "Taxi booking. Street numbers, addresses, passenger count, pickup location, destination."
        },
        turn_detection: {
          type: "server_vad",
          // Match taxi-realtime-simple settings for consistent quality
          threshold: 0.5,
          prefix_padding_ms: 300,
          silence_duration_ms: 1200
        },
        tools: TOOLS,
        tool_choice: "auto",
        temperature: 0.6 // OpenAI Realtime API minimum is 0.6
      }
    };
    
    openaiWs!.send(JSON.stringify(sessionConfig));
    
    // NOTE: Greeting is now sent on session.created, not here
  };

  openaiWs.onmessage = async (event) => {
    try {
      const data = JSON.parse(event.data);
      
      switch (data.type) {
        case "session.created":
          // Session created - but wait for session.updated before greeting
          console.log(`[${callId}] 📋 Session created - waiting for session.updated`);
          break;
          
        case "session.updated":
          // Session config applied - NOW send the greeting (with tiny delay for stability)
          console.log(`[${callId}] ✅ Session configured - triggering greeting in 200ms`);
          setTimeout(() => sendGreeting(), 200);
          break;
          
        case "response.created":
          // SILENCE MODE GUARD: If we're waiting for a quote, cancel any new responses
          if (sessionState.waitingForQuoteSilence && sessionState.saidOneMoment) {
            console.log(`[${callId}] 🤫 BLOCKING new response - in silence mode waiting for fare`);
            openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
            break;
          }
          console.log(`[${callId}] 🎤 Response started`);
          break;
          
        case "error":
          console.error(`[${callId}] ❌ OpenAI error:`, JSON.stringify(data));
          break;

        case "response.audio.delta":
          // Forward audio to client (and mark that Ada is speaking)
          if (data.delta) {
            if (!sessionState.openAiResponseActive) {
              sessionState.openAiSpeechStartedAt = Date.now();
              console.log(`[${callId}] 🔊 First audio chunk received`);
            }
            sessionState.openAiResponseActive = true;

            if (socket.readyState === WebSocket.OPEN) {
              socket.send(JSON.stringify({
                type: "audio",
                audio: data.delta
              }));
            } else {
              console.log(`[${callId}] ⚠️ Socket not open (state: ${socket.readyState}), can't send audio`);
            }
          }
          break;

        case "response.audio_transcript.delta":
          // Track what Ada is saying and check for price hallucination
          if (data.delta) {
            if (!sessionState.openAiResponseActive) {
              sessionState.openAiSpeechStartedAt = Date.now();
            }
            sessionState.openAiResponseActive = true;
            
            // PRICE/ETA HALLUCINATION GUARD: If Ada mentions a price/ETA but we haven't received one from dispatch, cancel!
            if (isPriceOrEtaHallucination(data.delta, !!sessionState.pendingFare)) {
              console.log(`[${callId}] 🚫 PRICE/ETA HALLUCINATION DETECTED: "${data.delta}" - cancelling response`);
              openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
              openaiWs!.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // If we're already in silence mode, don't inject another "checking" response - just stay silent
              if (sessionState.waitingForQuoteSilence) {
                console.log(`[${callId}] 🤫 Already in silence mode - NOT injecting correction, staying quiet`);
              } else {
                // Inject a correction - be very forceful
                sessionState.waitingForQuoteSilence = true;
                sessionState.saidOneMoment = true;
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{ type: "input_text", text: "[SYSTEM ERROR]: You made up a price or ETA. You do NOT know the fare or arrival time yet. Say ONLY: 'I'm just checking that for you now' and then STOP TALKING completely. Wait in silence for dispatch." }]
                  }
                }));
                openaiWs!.send(JSON.stringify({
                  type: "response.create",
                  response: { modalities: ["audio", "text"], instructions: "Say ONLY: 'I'm just checking that for you now.' Then STOP. Do not say anything else." }
                }));
              }
            }
          }
          break;

        case "response.audio_transcript.done":
          // Ada finished speaking - record in history and set echo guard
          if (data.transcript) {
            sessionState.conversationHistory.push({
              role: "assistant",
              content: data.transcript,
              timestamp: Date.now()
            });
            
            console.log(`[${callId}] 🤖 Ada: "${data.transcript.substring(0, 80)}..."`);
            
            // SILENCE MODE CHECK: If Ada just said "one moment", block any further responses
            const transcriptLower = data.transcript.toLowerCase();
            if (sessionState.waitingForQuoteSilence && 
                (transcriptLower.includes("one moment") || transcriptLower.includes("checking") || transcriptLower.includes("let me check"))) {
              console.log(`[${callId}] 🤫 Ada said "one moment" - entering STRICT SILENCE MODE until fare arrives`);
              // Cancel any pending response to ensure complete silence
              openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
              break; // Don't process further - just stay silent
            }
            // Detect what question was asked based on content
            const lower = data.transcript.toLowerCase();
            
            // SUMMARY PROTECTION: If Ada is summarizing the booking or quoting a price,
            // activate protection window to prevent interruptions
            const isSummary = lower.includes("let me confirm") || 
                              lower.includes("to confirm") || 
                              lower.includes("so that's") ||
                              lower.includes("summarize") ||
                              lower.includes("your booking") ||
                              lower.includes("one moment") ||
                              lower.includes("checking") ||
                              lower.includes("your price") ||
                              lower.includes("fare is") ||
                              lower.includes("driver will be");
            
            if (isSummary) {
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
              console.log(`[${callId}] 🛡️ Summary protection activated for ${SUMMARY_PROTECTION_MS}ms`);
            }
            
            // AUTO-TRIGGER WEBHOOK: If Ada says "check the price" but the mini model didn't call the tool,
            // automatically trigger the webhook. This works around mini model's weak tool calling.
            const isCheckingPrice = (lower.includes("check") && lower.includes("price")) ||
                                    (lower.includes("one moment") && lower.includes("price")) ||
                                    (lower.includes("checking") && (lower.includes("fare") || lower.includes("price") || lower.includes("trip")));
            
            // FALLBACK: If sessionState.booking is empty, try to extract from recent Ada summary
            // Look for pattern: "picked up at X, and travel to Y. There will be N passengers"
            if (!sessionState.booking.pickup || !sessionState.booking.destination || sessionState.booking.passengers === null) {
              // Search recent conversation history for Ada's summary
              const recentAdaMessages = sessionState.conversationHistory
                .filter(m => m.role === "assistant")
                .slice(-3)
                .map(m => m.content)
                .join(" ");
              
              // Extract pickup: "picked up at X" or "pickup at X"
              const pickupMatch = recentAdaMessages.match(/(?:picked up at|pickup at|pick you up at)\s+([^,]+?)(?:,|\.|\s+and)/i);
              if (pickupMatch && !sessionState.booking.pickup) {
                sessionState.booking.pickup = pickupMatch[1].trim();
                console.log(`[${callId}] 📍 Extracted pickup from summary: ${sessionState.booking.pickup}`);
              }
              
              // Extract destination: "travel to X" or "going to X" or "destination X"
              const destMatch = recentAdaMessages.match(/(?:travel to|going to|destination)\s+([^.]+?)(?:\.|There will)/i);
              if (destMatch && !sessionState.booking.destination) {
                sessionState.booking.destination = destMatch[1].trim();
                console.log(`[${callId}] 📍 Extracted destination from summary: ${sessionState.booking.destination}`);
              }
              
              // Extract passengers: "N passengers" or "There will be N"
              const passengersMatch = recentAdaMessages.match(/(?:there will be|there's|there are)\s+(\d+)\s+passenger/i) ||
                                      recentAdaMessages.match(/(\d+)\s+passenger/i);
              if (passengersMatch && sessionState.booking.passengers === null) {
                sessionState.booking.passengers = parseInt(passengersMatch[1], 10);
                console.log(`[${callId}] 📍 Extracted passengers from summary: ${sessionState.booking.passengers}`);
              }
              
              // Extract time: "picked up now" or "at X pm/am"
              const timeMatch = recentAdaMessages.match(/(?:picked up|pickup)\s+(now|immediately|straightaway|right away|\d+(?::\d+)?\s*(?:am|pm)?)/i);
              if (timeMatch && !sessionState.booking.pickupTime) {
                sessionState.booking.pickupTime = timeMatch[1].trim();
                console.log(`[${callId}] 📍 Extracted time from summary: ${sessionState.booking.pickupTime}`);
              }
            }
            
            const hasRequiredFields = sessionState.booking.pickup && 
                                      sessionState.booking.destination && 
                                      sessionState.booking.passengers !== null;
            
            console.log(`[${callId}] 🔍 Auto-trigger check: isCheckingPrice=${isCheckingPrice}, hasRequiredFields=${hasRequiredFields}, pickup=${sessionState.booking.pickup}, dest=${sessionState.booking.destination}, pax=${sessionState.booking.passengers}`);
            
            const QUOTE_DEDUPE_MS = 15000;
            const recentlyRequestedQuote = sessionState.lastQuoteRequestedAt > 0 && 
                                           (Date.now() - sessionState.lastQuoteRequestedAt) < QUOTE_DEDUPE_MS;
            
            if (isCheckingPrice && hasRequiredFields && !sessionState.quoteInFlight && 
                !sessionState.awaitingConfirmation && !recentlyRequestedQuote && !sessionState.bookingConfirmed) {
              console.log(`[${callId}] 🔄 AUTO-TRIGGER: Ada said she's checking price, sending webhook automatically`);
              sessionState.quoteInFlight = true;
              sessionState.lastQuoteRequestedAt = Date.now();
              
              // Send webhook in background
              (async () => {
                const AUTO_QUOTE_TIMEOUT_MS = 30000;

                try {
                  const result = await sendDispatchWebhook(sessionState, "request_quote", {
                    pickup: sessionState.booking.pickup,
                    destination: sessionState.booking.destination,
                    passengers: sessionState.booking.passengers,
                    pickup_time: sessionState.booking.pickupTime || "ASAP",
                    // (kept for compatibility with some downstream dispatch systems)
                    time: sessionState.booking.pickupTime || "ASAP"
                  });

                  console.log(`[${callId}] 📡 Auto-trigger webhook result:`, result);

                  if (!result.success) {
                    console.error(`[${callId}] ❌ Auto-trigger dispatch webhook failed: ${result.error || "unknown_error"}`);
                    sessionState.quoteInFlight = false;
                    return;
                  }

                  // Most dispatch systems respond asynchronously via taxi-dispatch-callback.
                  // Only inject a quote immediately if we actually got fare/eta in the HTTP response.
                  if (result.fare && result.eta) {
                    sessionState.pendingFare = result.fare;
                    sessionState.pendingEta = result.eta;
                    sessionState.awaitingConfirmation = true;
                    sessionState.quoteInFlight = false;
                    // Set lastQuestionAsked to confirmation so we know we're waiting for YES/NO
                    sessionState.lastQuestionAsked = "confirmation";

                    if (openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
                      openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                      sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;

                      const quoteMessage = `The trip fare will be ${result.fare}, and the estimated arrival time is ${result.eta}. Would you like to go ahead and book that?`;

                      openaiWs.send(JSON.stringify({
                        type: "conversation.item.create",
                        item: {
                          type: "message",
                          role: "user",
                          content: [{ type: "input_text", text: `[DISPATCH QUOTE RECEIVED]: Say to the customer: "${quoteMessage}". Then WAIT for their YES or NO answer. Do NOT call book_taxi until they explicitly say yes.` }]
                        }
                      }));

                      openaiWs.send(JSON.stringify({
                        type: "response.create",
                        response: { modalities: ["audio", "text"], instructions: `Say exactly: "${quoteMessage}" - then STOP and WAIT for user response.` }
                      }));
                    }
                  } else {
                    console.log(`[${callId}] ⏳ Quote requested (auto-trigger). Waiting for dispatch callback...`);

                    // Safety: if callback never arrives, allow a retry later.
                    setTimeout(() => {
                      if (cleanedUp) return;
                      if (sessionState.quoteInFlight && !sessionState.awaitingConfirmation && !sessionState.pendingFare) {
                        console.log(`[${callId}] ⏰ Auto-trigger quote timeout (${AUTO_QUOTE_TIMEOUT_MS}ms) - clearing quoteInFlight to allow retry`);
                        sessionState.quoteInFlight = false;
                      }
                    }, AUTO_QUOTE_TIMEOUT_MS);
                  }
                } catch (e) {
                  console.error(`[${callId}] Auto-trigger webhook error:`, e);
                  sessionState.quoteInFlight = false;
                }
              })();
            }
            
            if (lower.includes("where would you like to be picked up") || lower.includes("pickup")) {
              sessionState.lastQuestionAsked = "pickup";
            } else if (lower.includes("where would you like to go") || lower.includes("destination") || lower.includes("where are you going")) {
              sessionState.lastQuestionAsked = "destination";
            } else if (lower.includes("how many") || lower.includes("passengers") || lower.includes("people")) {
              sessionState.lastQuestionAsked = "passengers";
            } else if (lower.includes("when") || lower.includes("what time") || lower.includes("now or later")) {
              sessionState.lastQuestionAsked = "time";
            } else if (lower.includes("would you like to go ahead") || lower.includes("book that") || 
                       lower.includes("confirm this") || lower.includes("like me to book")) {
              sessionState.lastQuestionAsked = "confirmation";
              console.log(`[${callId}] 🎯 Confirmation question detected - waiting for YES/NO`);
            }
            
            console.log(`[${callId}] 📝 Context: lastQuestionAsked = ${sessionState.lastQuestionAsked}`);
            await updateLiveCall(sessionState);
          }
          sessionState.openAiResponseActive = false;
          sessionState.openAiSpeechStartedAt = 0;
          // Set echo guard to block echo from speaker
          sessionState.lastAdaFinishedSpeakingAt = Date.now();
          sessionState.echoGuardUntil = Date.now() + ECHO_GUARD_MS;
          break;

        case "input_audio_buffer.speech_started":
          console.log(`[${callId}] 🎤 User started speaking`);
          break;

        case "conversation.item.input_audio_transcription.completed":
          // User finished speaking - this is the KEY context pairing moment
          if (data.transcript) {
            const rawText = data.transcript.trim();
            // Apply STT corrections for common telephony mishearings
            const userText = correctTranscript(rawText);
            if (userText !== rawText) {
              console.log(`[${callId}] 🔧 STT corrected: "${rawText}" → "${userText}"`);
            }
            
            // Filter out phantom hallucinations from Whisper
            if (isPhantomHallucination(userText)) {
              console.log(`[${callId}] 👻 Filtered phantom hallucination: "${userText}"`);
              break;
            }
            
            // POST-CONFIRMATION GUARD: After booking is confirmed, enter open conversation mode
            // Only respond to specific requests, otherwise let Ada say goodbye
            if (sessionState.bookingConfirmed) {
              const lower = userText.toLowerCase();
              const isNewBookingRequest = lower.includes("new booking") || lower.includes("another taxi") || 
                                          lower.includes("book another") || lower.includes("different pickup");
              const isCancellation = lower.includes("cancel");
              
              if (isNewBookingRequest) {
                // Allow new booking - this will need a reset flow
                console.log(`[${callId}] 🔄 Post-confirmation: new booking request detected`);
              } else if (isCancellation) {
                console.log(`[${callId}] ❌ Post-confirmation: cancellation request detected`);
              } else {
                // Block looping back to booking questions - inject open conversation context
                console.log(`[${callId}] 🛡️ Post-confirmation guard: "${userText}" - sending open conversation response`);
                
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[POST-CONFIRMATION REMINDER] The booking is ALREADY CONFIRMED. User said: "${userText}". 
Do NOT ask "shall I book that taxi?" - it's ALREADY DONE.
Do NOT ask for pickup/destination/passengers - we HAVE all that.
If this sounds like they want something else, ask "Is there anything else I can help with?"
Otherwise, say goodbye warmly and call end_call().`
                    }]
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break; // Don't process further
              }
            }
            
            console.log(`[${callId}] 👤 User (after "${sessionState.lastQuestionAsked}" question): "${userText}"`);
            
            // DETECT ADDRESS CORRECTIONS (e.g., "It's 52A David Road", "No, it should be...")
            const correction = detectAddressCorrection(
              userText, 
              sessionState.booking.pickup, 
              sessionState.booking.destination
            );
            
            if (correction.type && correction.address) {
              const oldValue = correction.type === "pickup" 
                ? sessionState.booking.pickup 
                : sessionState.booking.destination;
              
              console.log(`[${callId}] 🔄 ADDRESS CORRECTION DETECTED: ${correction.type} "${oldValue}" → "${correction.address}"`);
              
              // Update the booking state immediately
              if (correction.type === "pickup") {
                sessionState.booking.pickup = correction.address;
              } else {
                sessionState.booking.destination = correction.address;
              }
              
              // Add to history with correction annotation
              sessionState.conversationHistory.push({
                role: "user",
                content: `[CORRECTION: User corrected ${correction.type} to "${correction.address}"] ${userText}`,
                timestamp: Date.now()
              });
              
              // Tell OpenAI about the correction so Ada acknowledges it
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[ADDRESS CORRECTION] The user just corrected their ${correction.type} address to: "${correction.address}". 
                    
IMPORTANT: Update your understanding. The ${correction.type} is now "${correction.address}" (not the previous value).
DO NOT ask them to confirm this change - just acknowledge briefly and continue to the next step.
Current state: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=${sessionState.booking.passengers ?? "empty"}, time=${sessionState.booking.pickupTime || "empty"}`
                  }]
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
              
              await updateLiveCall(sessionState);
              break; // Correction handled, don't run normal context pairing
            }
            
            // Add to history with context annotation (normal flow)
            sessionState.conversationHistory.push({
              role: "user",
              content: `[CONTEXT: Ada asked about ${sessionState.lastQuestionAsked}] ${userText}`,
              timestamp: Date.now()
            });
            
            // Send context-aware prompt to OpenAI
            const contextPrompt = {
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: `[CONTEXT PAIRING] You just asked about "${sessionState.lastQuestionAsked}". The user responded: "${userText}". 
                  
If this is a valid answer to your ${sessionState.lastQuestionAsked} question, use sync_booking_data to save it to the CORRECT field.
Current state: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=${sessionState.booking.passengers ?? "empty"}, time=${sessionState.booking.pickupTime || "empty"}`
                }]
              }
            };
            openaiWs!.send(JSON.stringify(contextPrompt));
            
            await updateLiveCall(sessionState);
          }
          break;

        case "response.function_call_arguments.done":
          // Handle tool calls
          const toolName = data.name;
          let toolArgs: Record<string, unknown> = {};
          
          try {
            toolArgs = JSON.parse(data.arguments || "{}");
          } catch {
            console.error(`[${callId}] Failed to parse tool args`);
          }
          
          console.log(`[${callId}] 🔧 Tool: ${toolName}`, toolArgs);
          
          if (toolName === "sync_booking_data") {
            // Update booking state from tool call
            if (toolArgs.pickup) sessionState.booking.pickup = String(toolArgs.pickup);
            if (toolArgs.destination) sessionState.booking.destination = String(toolArgs.destination);
            if (toolArgs.passengers !== undefined) sessionState.booking.passengers = Number(toolArgs.passengers);
            if (toolArgs.pickup_time) sessionState.booking.pickupTime = String(toolArgs.pickup_time);
            if (toolArgs.last_question_asked) {
              sessionState.lastQuestionAsked = toolArgs.last_question_asked as SessionState["lastQuestionAsked"];
            }
            
            console.log(`[${callId}] 📊 Booking state updated:`, sessionState.booking);
            await updateLiveCall(sessionState);
            
            // Send tool result
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ 
                  success: true, 
                  current_state: sessionState.booking,
                  next_question: sessionState.lastQuestionAsked
                })
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
          } else if (toolName === "book_taxi") {
            const action = String(toolArgs.action || "");

            const resolvedPickup = toolArgs.pickup ? String(toolArgs.pickup) : sessionState.booking.pickup;
            const resolvedDestination = toolArgs.destination ? String(toolArgs.destination) : sessionState.booking.destination;
            const resolvedPassengers = (toolArgs.passengers !== undefined)
              ? Number(toolArgs.passengers)
              : sessionState.booking.passengers;
            const resolvedPickupTime = toolArgs.pickup_time ? String(toolArgs.pickup_time) : sessionState.booking.pickupTime;

            // Persist any provided details so subsequent tool calls (e.g. confirmed) include full booking info
            if (resolvedPickup) sessionState.booking.pickup = resolvedPickup;
            if (resolvedDestination) sessionState.booking.destination = resolvedDestination;
            if (resolvedPassengers !== null && !Number.isNaN(resolvedPassengers)) {
              sessionState.booking.passengers = resolvedPassengers;
            }
            if (resolvedPickupTime) sessionState.booking.pickupTime = resolvedPickupTime;

            // Prevent sending incomplete payloads (these cause duplicate/garbage quotes downstream)
            const missing: string[] = [];
            if (!sessionState.booking.pickup) missing.push("pickup");
            if (!sessionState.booking.destination) missing.push("destination");
            if (sessionState.booking.passengers === null || Number.isNaN(sessionState.booking.passengers)) missing.push("passengers");

            // De-dupe: once quote requested/in-flight, don't re-send
            const QUOTE_DEDUPE_MS = 15000;
            const recentlyRequestedQuote = sessionState.lastQuoteRequestedAt > 0 && (Date.now() - sessionState.lastQuoteRequestedAt) < QUOTE_DEDUPE_MS;

            if (action === "request_quote") {
              if (sessionState.bookingConfirmed) {
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({ success: false, status: "ignored", reason: "already_confirmed" })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }

              if (sessionState.awaitingConfirmation || sessionState.quoteInFlight || recentlyRequestedQuote) {
                console.log(`[${callId}] ⚠️ Ignoring duplicate request_quote (awaitingConfirmation=${sessionState.awaitingConfirmation}, quoteInFlight=${sessionState.quoteInFlight}, recently=${recentlyRequestedQuote})`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({ success: true, status: "pending", message: "Quote already requested. Please wait." })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }

              if (missing.length > 0) {
                console.log(`[${callId}] ⚠️ Blocking request_quote - missing fields: ${missing.join(", ")}`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "missing_fields",
                      missing,
                      message: `Cannot request a quote yet. Missing: ${missing.join(", ")}. Ask the user for the missing info (one question only).`
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }

              // Send webhook to dispatch system (async - they respond via callback)
              sessionState.quoteInFlight = true;
              sessionState.lastQuoteRequestedAt = Date.now();

              const webhookResult = await sendDispatchWebhook(sessionState, action, {
                pickup: resolvedPickup,
                destination: resolvedDestination,
                passengers: resolvedPassengers,
                pickup_time: resolvedPickupTime
              });

              // If the dispatch system is unreachable, DON'T enter silence mode (otherwise the call sounds "stuck").
              // Let Ada inform the caller once and allow a retry.
              if (!webhookResult.success) {
                console.error(`[${callId}] ❌ Dispatch webhook failed - not entering silence mode: ${webhookResult.error || "unknown_error"}`);
                sessionState.quoteInFlight = false;
                sessionState.waitingForQuoteSilence = false;
                sessionState.saidOneMoment = false;

                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "dispatch_unreachable",
                      error: webhookResult.error || "dispatch_unreachable",
                      message: "Dispatch pricing system did not respond. Apologize briefly and ask if you'd like to try again."
                    })
                  }
                }));

                openaiWs!.send(JSON.stringify({
                  type: "response.create",
                  response: {
                    modalities: ["audio", "text"],
                    instructions: "Say ONLY: 'Sorry, I'm having trouble getting the fare right now. Would you like me to try again?' Then STOP and WAIT for their answer."
                  }
                }));

                break;
              }

              // Set silence mode - Ada must not speak again until fare arrives
              sessionState.waitingForQuoteSilence = true;
              sessionState.saidOneMoment = true;

              // Tell Ada to say "one moment" ONCE and then be completely silent
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: true,
                    status: "waiting_for_dispatch",
                    message: "Say EXACTLY: 'One moment please while I check that for you.' Then STOP. Do NOT speak again. Do NOT guess prices or ETAs. Wait for [DISPATCH QUOTE RECEIVED]."
                  })
                }
              }));
              
              // Create ONE response for "one moment" then enter silence mode
              openaiWs!.send(JSON.stringify({ 
                type: "response.create",
                response: {
                  modalities: ["audio", "text"],
                  instructions: "Say ONLY: 'One moment please while I check that for you.' Then STOP COMPLETELY. Say nothing else."
                }
              }));

            } else if (action === "confirmed") {
              console.log(`[${callId}] ✅ Processing CONFIRMED action (awaitingConfirmation=${sessionState.awaitingConfirmation}, bookingConfirmed=${sessionState.bookingConfirmed}, bookingRef=${sessionState.pendingBookingRef})`);
              
              // GUARD: Prevent duplicate confirmations - check this FIRST before any webhook
              if (sessionState.bookingConfirmed) {
                console.log(`[${callId}] ⚠️ CONFIRMED blocked - already confirmed (duplicate call)`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: true,
                      status: "already_confirmed",
                      message: "Booking already confirmed. Do not confirm again. Say goodbye and end the call."
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
              
              // Only allow confirm after we asked fare confirmation
              if (!sessionState.awaitingConfirmation) {
                console.log(`[${callId}] ⚠️ CONFIRMED blocked - not awaitingConfirmation`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "not_ready",
                      message: "Do not confirm booking yet. You must wait for the quote and ask the customer to confirm."
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
              
              // Set confirmed flag IMMEDIATELY to prevent race conditions
              sessionState.bookingConfirmed = true;

              // Also require the booking details to be present (avoid sending nulls on confirm)
              if (missing.length > 0) {
                console.log(`[${callId}] ⚠️ CONFIRMED blocked - missing fields: ${missing.join(", ")}`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "missing_fields",
                      missing,
                      message: `Cannot confirm booking yet. Missing: ${missing.join(", ")}. Ask for the missing info.`
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }

              console.log(`[${callId}] 📤 Sending CONFIRMED webhook...`);
              await sendDispatchWebhook(sessionState, action, {
                pickup: sessionState.booking.pickup,
                destination: sessionState.booking.destination,
                passengers: sessionState.booking.passengers,
                pickup_time: sessionState.booking.pickupTime
              });
              console.log(`[${callId}] ✅ CONFIRMED webhook sent successfully`);
              
              // POST confirmation to callback_url if provided (tells dispatch to book the driver)
              // This matches taxi-realtime-simple behavior
              if (sessionState.pendingConfirmationCallback) {
                try {
                  console.log(`[${callId}] 📡 POSTing confirmation to callback_url: ${sessionState.pendingConfirmationCallback}`);
                  const confirmPayload = {
                    call_id: callId,
                    job_id: sessionState.dispatchJobId || null,
                    action: "confirmed",
                    response: "confirmed",  // C# bridge compatibility
                    pickup: sessionState.booking.pickup,
                    destination: sessionState.booking.destination,
                    fare: sessionState.pendingFare,
                    eta: sessionState.pendingEta,
                    pickup_time: sessionState.booking.pickupTime || "ASAP",
                    passengers: sessionState.booking.passengers || 1,
                    customer_name: null,  // Not tracked in paired mode
                    caller_phone: sessionState.callerPhone,
                    booking_ref: sessionState.pendingBookingRef || sessionState.bookingRef || null,
                    timestamp: new Date().toISOString()
                  };
                  
                  const confirmResp = await fetch(sessionState.pendingConfirmationCallback, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(confirmPayload)
                  });
                  
                  console.log(`[${callId}] 📬 Callback response: ${confirmResp.status}`);
                } catch (callbackErr) {
                  console.error(`[${callId}] ⚠️ Callback POST failed:`, callbackErr);
                }
              }

              // bookingConfirmed already set at top of this block to prevent race conditions
              sessionState.awaitingConfirmation = false;
              sessionState.quoteInFlight = false;
              
              // Protect goodbye speech from interruption
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
              
              // Set lastQuestionAsked to "none" to prevent looping back to booking questions
              sessionState.lastQuestionAsked = "none";
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ 
                    success: true, 
                    status: "confirmed",
                    message: "Booking confirmed! Say ONLY: 'Perfect, your booking is confirmed. You'll receive details via WhatsApp. Thank you for using Taxibot demo, have a safe journey.' Then IMMEDIATELY call end_call()."
                  })
                }
              }));
              
              // CRITICAL: Inject system message to prevent AI from looping back to booking questions
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[POST-CONFIRMATION MODE ACTIVE] The booking is NOW CONFIRMED and COMPLETE. 
                    
🚨 CRITICAL RULES:
- Do NOT ask "shall I book that taxi?" - it's ALREADY BOOKED
- Do NOT ask for pickup, destination, passengers, or time - we HAVE all that
- Do NOT loop back to any booking questions
- If user speaks, respond with ONLY brief open conversation (e.g., "Is there anything else I can help with?")
- You are now in GOODBYE/OPEN CONVERSATION mode, NOT booking mode
- Your next action should be to say goodbye and call end_call()`
                  }]
                }
              }));
              
              openaiWs!.send(JSON.stringify({ type: "response.create" }));

            } else {
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ success: false, status: "unknown_action" })
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
            }

            await updateLiveCall(sessionState);
            
          } else if (toolName === "end_call") {
            console.log(`[${callId}] 📞 Call ending: ${toolArgs.reason}`);
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ success: true })
              }
            }));
            
            // Protect the goodbye from being cut off by noise/echo
            sessionState.summaryProtectionUntil = Date.now() + (SUMMARY_PROTECTION_MS * 2);
            console.log(`[${callId}] 🛡️ End-call goodbye protection activated for ${SUMMARY_PROTECTION_MS * 2}ms`);

            // Let Ada say goodbye
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: "[SYSTEM: Say a warm goodbye. Mention they'll receive booking details via WhatsApp. Thank them for using Taxibot demo.]"
                }]
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
            // Give extra time so the final message isn't truncated before hangup
            // Must be >= protection window (SUMMARY_PROTECTION_MS * 2 = 16s) + buffer
            setTimeout(() => {
              try {
                socket.send(JSON.stringify({ type: "hangup", reason: toolArgs.reason }));
              } catch { /* ignore */ }
              cleanup();
            }, 18000);
          }
          break;

        case "error":
          console.error(`[${callId}] ❌ OpenAI error:`, data.error);
          break;
          
        default:
          // Log unhandled events for debugging
          if (data.type === "response.done") {
            // Log the full response.done to see why no audio
            console.log(`[${callId}] 📨 response.done:`, JSON.stringify(data.response?.output || data.response?.status_details || "no details"));
          } else if (data.type && !data.type.startsWith("input_audio_buffer") && !data.type.startsWith("rate_limits")) {
            console.log(`[${callId}] 📨 OpenAI event: ${data.type}`);
          }
          break;
      }
    } catch (e) {
      console.error(`[${callId}] Error processing OpenAI message:`, e);
    }
  };

  openaiWs.onerror = (error) => {
    console.error(`[${callId}] OpenAI WebSocket error:`, error);
  };

  openaiWs.onclose = () => {
    console.log(`[${callId}] OpenAI WebSocket closed`);
    cleanup();
  };

  // Client WebSocket handlers
  socket.onmessage = async (event) => {
    try {
      // Handle binary audio data from Python bridge
      if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
        const audioBytes = event.data instanceof ArrayBuffer
          ? new Uint8Array(event.data)
          : event.data;

        // GREETING PROTECTION: ignore early line noise so Ada doesn't get cut off
        if (Date.now() < sessionState.greetingProtectionUntil) {
          return;
        }

        // ECHO GUARD: block audio briefly after Ada finishes speaking
        if (Date.now() < sessionState.echoGuardUntil) {
          return; // Drop this audio frame (likely echo)
        }

        // SUMMARY PROTECTION: block interruptions while Ada is recapping/quoting
        if (Date.now() < sessionState.summaryProtectionUntil) {
          return; // Drop - Ada is delivering summary, do not interrupt
        }

        if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
          let pcmInput: Int16Array;

          if (inboundAudioFormat === "ulaw") {
            pcmInput = ulawToPcm16(audioBytes); // 8kHz PCM16
          } else {
            // slin/slin16: already PCM16
            pcmInput = new Int16Array(audioBytes.buffer, audioBytes.byteOffset, Math.floor(audioBytes.byteLength / 2));
          }

          // RMS-based barge-in detection: only forward audio that sounds like real speech
          // If Ada is speaking, ignore the first slice of inbound audio to avoid echo/noise cutting her off,
          // then require RMS within a sane window to be treated as a real barge-in.
          if (sessionState.openAiResponseActive) {
            const sinceSpeakStart = sessionState.openAiSpeechStartedAt
              ? (Date.now() - sessionState.openAiSpeechStartedAt)
              : 0;
            if (sinceSpeakStart > 0 && sinceSpeakStart < ASSISTANT_LEADIN_IGNORE_MS) {
              return;
            }

            const rms = computeRms(pcmInput);
            if (rms < BARGE_IN_RMS_MIN || rms > BARGE_IN_RMS_MAX) {
              return; // Not real speech, likely echo or noise
            }
          }

          const pcm24k = resamplePcm16To24k(pcmInput, inboundSampleRate);
          const base64Audio = pcm16ToBase64(pcm24k);

          openaiWs.send(JSON.stringify({
            type: "input_audio_buffer.append",
            audio: base64Audio,
          }));
        }
        return;
      }

      // Handle string messages (JSON)
      if (typeof event.data === "string") {
        const data = JSON.parse(event.data);

        if (data.type === "audio" && data.audio) {
          // If we receive base64 audio, assume it's 8kHz ulaw unless told otherwise.
          const binaryStr = atob(data.audio);
          const bytes = new Uint8Array(binaryStr.length);
          for (let i = 0; i < binaryStr.length; i++) bytes[i] = binaryStr.charCodeAt(i);

          const assumedFormat: InboundAudioFormat = (data.format === "slin" || data.format === "slin16" || data.format === "ulaw")
            ? data.format
            : "ulaw";
          const assumedRate = typeof data.sample_rate === "number" ? data.sample_rate : 8000;

          const pcmInput = assumedFormat === "ulaw"
            ? ulawToPcm16(bytes)
            : new Int16Array(bytes.buffer, bytes.byteOffset, Math.floor(bytes.byteLength / 2));

          const pcm24k = resamplePcm16To24k(pcmInput, assumedRate);
          const base64Audio = pcm16ToBase64(pcm24k);

          if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: base64Audio,
            }));
          }
        } else if (data.type === "init" || data.type === "update_phone" || data.type === "update_format") {
          // Handle init/phone/format updates
          if (data.phone && data.phone !== "unknown") {
            callerPhone = data.phone;
            sessionState.callerPhone = data.phone; // Update session state too!
            console.log(`[${callId}] 📱 Phone updated: ${callerPhone}`);
          }

          if (data.inbound_format && (data.inbound_format === "ulaw" || data.inbound_format === "slin" || data.inbound_format === "slin16")) {
            inboundAudioFormat = data.inbound_format;
          }
          if (typeof data.inbound_sample_rate === "number") {
            inboundSampleRate = data.inbound_sample_rate;
          }
        } else if (data.type === "hangup") {
          console.log(`[${callId}] Client requested hangup`);
          cleanup();
        }
      }
    } catch (e) {
      console.error(`[${callId}] Error processing client message:`, e);
    }
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] Client WebSocket error:`, error);
  };

  socket.onclose = () => {
    console.log(`[${callId}] Client WebSocket closed`);
    cleanup();
  };
}

// Main server
Deno.serve(async (req) => {
  const url = new URL(req.url);
  
  // CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  // Health check
  if (url.pathname === "/health" || url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ 
      status: "healthy",
      mode: "paired-context",
      timestamp: new Date().toISOString()
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" }
    });
  }
  
  // WebSocket upgrade
  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() === "websocket") {
    const callId = url.searchParams.get("call_id") || `paired-${Date.now()}`;
    const callerPhone = url.searchParams.get("caller_phone") || "unknown";
    
    const { socket, response } = Deno.upgradeWebSocket(req);
    
    handleConnection(socket, callId, callerPhone);
    
    return response;
  }
  
  return new Response(JSON.stringify({ 
    error: "WebSocket upgrade required",
    mode: "taxi-realtime-paired"
  }), {
    status: 400,
    headers: { ...corsHeaders, "Content-Type": "application/json" }
  });
});
