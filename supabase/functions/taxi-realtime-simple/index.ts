import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");

// Validate required environment variables at startup
if (!SUPABASE_URL || !SUPABASE_SERVICE_ROLE_KEY) {
  throw new Error("Missing required environment variables: SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY must be set");
}
if (!OPENAI_API_KEY) {
  console.warn("⚠️ OPENAI_API_KEY not set - voice functionality will fail");
}

// DEMO MODE: force the simple demo script every call (ignore caller history, active bookings, and modifications)
// Set to false for production: enables database sync, session restoration, and caller history lookup
const DEMO_SIMPLE_MODE = false;

const DEFAULT_COMPANY = "247 Radio Carz";
const DEFAULT_AGENT = "Ada";
const DEFAULT_VOICE = "shimmer";

// === PROTECTION WINDOWS (ported from paired mode) ===
// Greeting protection: 12s window to prevent background noise from triggering barge-in during Ada's intro
const GREETING_PROTECTION_MS = 12000;
// Summary protection: 8s window during fare quotes/summaries to prevent interruption
const SUMMARY_PROTECTION_MS = 8000;
// Audio diagnostics logging interval (every N packets)
const AUDIO_DIAGNOSTICS_LOG_INTERVAL = 200;

// --- Binary Audio Helper ---
// Sends audio as raw binary instead of base64 JSON (33% smaller, faster)
function sendBinaryAudio(socket: WebSocket, base64Audio: string): void {
  try {
    const binaryStr = atob(base64Audio);
    const bytes = new Uint8Array(binaryStr.length);
    for (let i = 0; i < binaryStr.length; i++) {
      bytes[i] = binaryStr.charCodeAt(i);
    }
    socket.send(bytes.buffer);
  } catch (e) {
    console.error("❌ Failed to decode audio for binary send:", e);
  }
}

// === DISPATCH-TRIGGERED HANDOFF ===
// Delay after fare quote before triggering reconnect (allows Ada's fare speech to start)
const HANDOFF_AFTER_DISPATCH_DELAY_MS = 2500;

// Language code to name mapping (for prompt injection)
const LANGUAGE_NAMES: Record<string, string> = {
  en: "English",
  nl: "Dutch",
  es: "Spanish",
  fr: "French",
  de: "German",
  it: "Italian",
  pt: "Portuguese",
  pl: "Polish",
  ro: "Romanian",
  ar: "Arabic",
  hi: "Hindi",
  zh: "Chinese",
  ja: "Japanese",
  ko: "Korean",
  tr: "Turkish",
  ru: "Russian",
  el: "Greek",
  cs: "Czech",
  hu: "Hungarian",
  sv: "Swedish",
  no: "Norwegian",
  da: "Danish",
  fi: "Finnish",
};

// --- Phone Number to Language Mapping ---
const COUNTRY_CODE_TO_LANGUAGE: Record<string, string> = {
  // English
  "+44": "en", // UK
  "+1": "en",  // USA/Canada
  "+61": "en", // Australia
  "+64": "en", // New Zealand
  "+353": "en", // Ireland
  
  // Spanish
  "+34": "es", // Spain
  "+52": "es", // Mexico
  "+54": "es", // Argentina
  "+56": "es", // Chile
  "+57": "es", // Colombia
  "+51": "es", // Peru
  
  // French
  "+33": "fr", // France
  "+32": "fr", // Belgium (also Dutch)
  "+41": "fr", // Switzerland (also German/Italian)
  
  // German
  "+49": "de", // Germany
  "+43": "de", // Austria
  
  // Italian
  "+39": "it", // Italy
  
  // Portuguese
  "+351": "pt", // Portugal
  "+55": "pt", // Brazil
  
  // Polish
  "+48": "pl", // Poland
  
  // Romanian
  "+40": "ro", // Romania
  
  // Dutch
  "+31": "nl", // Netherlands
  
  // Arabic
  "+966": "ar", // Saudi Arabia
  "+971": "ar", // UAE
  "+20": "ar",  // Egypt
  
  // Hindi
  "+91": "hi", // India
  
  // Chinese
  "+86": "zh", // China
  "+852": "zh", // Hong Kong
  "+886": "zh", // Taiwan
  
  // Japanese
  "+81": "ja", // Japan
  
  // Korean
  "+82": "ko", // South Korea
  
  // Turkish
  "+90": "tr", // Turkey
  
  // Russian
  "+7": "ru", // Russia
  
  // Greek
  "+30": "el", // Greece
  
  // Czech
  "+420": "cs", // Czech Republic
  
  // Hungarian
  "+36": "hu", // Hungary
  
  // Swedish
  "+46": "sv", // Sweden
  
  // Norwegian
  "+47": "no", // Norway
  
  // Danish
  "+45": "da", // Denmark
  
  // Finnish
  "+358": "fi", // Finland
};

// Detect language from phone number country code
function detectLanguageFromPhone(phone: string | null): string | null {
  if (!phone) return null;

  // Clean the phone number
  let cleaned = phone.replace(/\s+/g, "").replace(/-/g, "");

  // Common normalizations:
  // - 00CC... → +CC...
  if (cleaned.startsWith("00")) {
    cleaned = "+" + cleaned.slice(2);
  }

  // - +0CC... → +CC...
  if (/^\+0\d/.test(cleaned)) {
    cleaned = "+" + cleaned.slice(2);
  }

  // - 0316... (some PBX/CLI formats) → +316... (Netherlands mobiles)
  //   This avoids guessing for all leading-0 national numbers (e.g. UK 07..., 020...).
  if (/^0316\d+/.test(cleaned)) {
    cleaned = "+31" + cleaned.slice(3);
  }

  // If no + prefix but starts with known country code digits, add +
  // Common patterns: 44... (UK), 1... (USA/Canada), 33... (France), etc.
  if (!cleaned.startsWith("+") && /^(44|1|33|49|31|34|39|351|353|61|64|7|81|82|86|91)\d{6,}$/.test(cleaned)) {
    cleaned = "+" + cleaned;
    console.log(`📞 Phone ${phone} missing + prefix, normalized to ${cleaned}`);
  }

  // Only attempt country-code matching on E.164-like numbers
  if (!cleaned.startsWith("+")) {
    console.log(`📞 Phone ${phone} not in E.164 format; skipping country-code language mapping`);
    return null;
  }

  // Try longer codes first (e.g., +353 before +3, +1868 before +1)
  const sortedCodes = Object.keys(COUNTRY_CODE_TO_LANGUAGE).sort((a, b) => b.length - a.length);

  for (const code of sortedCodes) {
    if (cleaned.startsWith(code)) {
      const lang = COUNTRY_CODE_TO_LANGUAGE[code];
      console.log(`📞 Phone ${phone} normalized=${cleaned} matched ${code} → ${lang}`);
      return lang;
    }
  }

  console.log(`📞 Phone ${phone} normalized=${cleaned} - no country code match, using auto-detect`);
  return null;
}

// Normalize to digits-only for DB lookups (callers/bookings use digits-only phone_number)
const normalizePhone = (phone: string | null | undefined) => String(phone || "").replace(/\D/g, "");

// --- Booking Step State Machine Helpers ---
type BookingStep = "pickup" | "destination" | "passengers" | "time" | "summary" | "confirmed";
const BOOKING_STEP_ORDER: BookingStep[] = ["pickup", "destination", "passengers", "time", "summary", "confirmed"];

/**
 * Get the next step in the booking flow
 */
function getNextStep(currentStep: BookingStep): BookingStep | null {
  const idx = BOOKING_STEP_ORDER.indexOf(currentStep);
  if (idx === -1 || idx >= BOOKING_STEP_ORDER.length - 1) return null;
  return BOOKING_STEP_ORDER[idx + 1];
}

/**
 * Check if a step is before another in the flow
 */
function isStepBefore(step: BookingStep, referenceStep: BookingStep): boolean {
  return BOOKING_STEP_ORDER.indexOf(step) < BOOKING_STEP_ORDER.indexOf(referenceStep);
}

/**
 * Check if the booking state has the required field for a step
 */
function isStepComplete(step: BookingStep, booking: { pickup: string | null; destination: string | null; passengers: number | null; pickup_time: string | null }): boolean {
  switch (step) {
    case "pickup": return !!booking.pickup && booking.pickup.length > 2;
    case "destination": return !!booking.destination && booking.destination.length > 2;
    case "passengers": return booking.passengers !== null && booking.passengers > 0;
    case "time": return booking.pickup_time !== null;
    case "summary": return true; // Summary is complete when user confirms
    case "confirmed": return true;
    default: return false;
  }
}

/**
 * Advance to the next step if current step is complete
 * Returns the new step (may be same as current if not complete)
 */
function advanceBookingStep(
  sessionState: { bookingStep: BookingStep; bookingStepAdvancedAt: number | null; booking: any; callId: string }
): BookingStep {
  const current = sessionState.bookingStep;
  if (!isStepComplete(current, sessionState.booking)) {
    return current; // Can't advance - current step not complete
  }
  
  const next = getNextStep(current);
  if (!next) return current; // Already at end
  
  // Don't advance too rapidly (prevents extraction race conditions)
  const now = Date.now();
  if (sessionState.bookingStepAdvancedAt && now - sessionState.bookingStepAdvancedAt < 500) {
    return current;
  }
  
  console.log(`[${sessionState.callId}] 📈 STEP ADVANCE: ${current} → ${next}`);
  sessionState.bookingStep = next;
  sessionState.bookingStepAdvancedAt = now;
  return next;
}

/**
 * Map a question type to its corresponding step
 */
function questionTypeToStep(questionType: "pickup" | "destination" | "passengers" | "time" | "confirmation" | null): BookingStep | null {
  if (!questionType) return null;
  if (questionType === "confirmation") return "summary";
  return questionType as BookingStep;
}

// --- Pickup Time Normalization ---
// Converts natural language time expressions to YYYY-MM-DD HH:MM format using LLM
async function normalizePickupTime(rawTime: string | null | undefined): Promise<string> {
  // Handle ASAP cases immediately
  if (!rawTime || rawTime.toLowerCase() === "now" || rawTime.toLowerCase() === "asap" || rawTime.trim() === "") {
    return "ASAP";
  }
  
  // Get current London time for reference
  const now = new Date();
  const londonFormatter = new Intl.DateTimeFormat("en-GB", {
    timeZone: "Europe/London",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false
  });
  const parts = londonFormatter.formatToParts(now);
  const londonDate = `${parts.find(p => p.type === "year")?.value}-${parts.find(p => p.type === "month")?.value}-${parts.find(p => p.type === "day")?.value}`;
  const londonTime = `${parts.find(p => p.type === "hour")?.value}:${parts.find(p => p.type === "minute")?.value}`;
  const referenceDateTime = `${londonDate} ${londonTime}`;
  
  // Calculate current day of week for context
  const dayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
  const londonNow = new Date(now.toLocaleString("en-US", { timeZone: "Europe/London" }));
  const currentDayName = dayNames[londonNow.getDay()];
  
  const systemPrompt = `You are a Temporal Normalization Specialist. Transform natural language time expressions into strictly formatted 24-hour timestamps.

CONTEXT:
- Reference datetime: ${referenceDateTime}
- Current day: ${currentDayName}
- Location: London/UK
- Timezone: GMT/BST (Europe/London)

OUTPUT FORMAT: YYYY-MM-DD HH:MM

RULES:
1. If calculated time is earlier than ${referenceDateTime}, increment the date (e.g., to tomorrow)
2. Map 'now', 'asap', or empty values to 'ASAP'
3. Return ONLY the formatted time string or 'ASAP'. No prose. No explanations.

NORMALIZATION LOGIC:
- "tonight" → today 21:00
- "evening" → 19:00
- "afternoon" → 15:00  
- "morning" → 09:00
- "tomorrow" → +24 hours from reference
- "in X minutes" → reference + X minutes
- "in X hours" → reference + X hours
- "at Xam/pm" → convert to 24hr format
- Weekday names → nearest upcoming instance (if "next Monday", use following week)

EXAMPLES:
- "in 15 minutes" with ref 14:48 → "${londonDate} 15:03"
- "at 10am tomorrow" → next day 10:00
- "monday evening" → nearest Monday 19:00
- "1600 hours tomorrow" → next day 16:00`;

  console.log(`🕐 normalizePickupTime called with: "${rawTime}"`);
  
  try {
    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      console.warn("⚠️ LOVABLE_API_KEY not set - returning raw time");
      return rawTime;
    }
    
    console.log(`🕐 Calling Lovable API for time normalization...`);
    const response = await fetch("https://api.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${LOVABLE_API_KEY}`
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash-lite",
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: rawTime }
        ],
        temperature: 0,
        max_tokens: 50
      })
    });
    
    console.log(`🕐 Lovable API response status: ${response.status}`);
    
    if (!response.ok) {
      console.error(`Time normalization API error: ${response.status} - ${await response.text()}`);
      return rawTime;
    }
    
    const data = await response.json();
    const normalizedTime = data.choices?.[0]?.message?.content?.trim();
    
    console.log(`🕐 Lovable API result: "${normalizedTime}"`);
    
    if (normalizedTime) {
      console.log(`🕐 Time normalized: "${rawTime}" → "${normalizedTime}"`);
      return normalizedTime;
    }
    
    console.log(`🕐 No normalized time returned, using raw: "${rawTime}"`);
    return rawTime;
  } catch (err) {
    console.error("Time normalization failed:", err);
    return rawTime;
  }
}

// --- System Prompt ---
const SYSTEM_PROMPT = `
# IDENTITY
You are {{agent_name}}, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual.

# 🎙️ SPEECH PACING (CRITICAL)
- Speak at a SLOW, RELAXED pace. Take your time with each word.
- Insert natural pauses between sentences. Don't rush.
- Pronounce addresses and numbers clearly and deliberately.
- Use a calm, unhurried cadence like a friendly customer service agent.
- Pause briefly after asking a question to let it land.

LANGUAGE: {{language_instruction}}
You are multilingual. If caller asks for a different language, switch immediately.

# 🎤 SHORT ANSWER AWARENESS (CRITICAL FOR TELEPHONY)
Users on phone lines often give VERY SHORT responses like:
- "Yes", "No", "Yep", "Nope", "OK", "Sure", "Hi", "Bye"
- Single numbers: "1", "2", "3", "4" (for passengers)
- Quick confirmations: "That's right", "Correct", "Go ahead"
Listen CAREFULLY for these short words. They are valid responses, NOT background noise.
When asking about passengers, expect responses like "4", "four", "just me", "two of us".
When asking for confirmation, expect "yes", "yeah", "yep", "go ahead", "book it", "please".

# 🛑 CRITICAL LOGIC GATE: THE CHECKLIST
You have a mental checklist of 4 items: [Pickup], [Destination], [Passengers], [Time].
- You are FORBIDDEN from moving to the 'Booking Summary' until ALL 4 items are specifically provided by the user.
- If a detail is missing, ask for it.

# 🚨 ONE QUESTION RULE (CRITICAL)
- Ask ONLY ONE question per response. NEVER combine questions.
- WRONG: "Where would you like to be picked up and where are you going?"
- WRONG: "How many passengers and when do you need it?"
- RIGHT: "Where would you like to be picked up?" [wait for answer]
- RIGHT: "And what is your destination?" [wait for answer]
- Wait for a user response before asking the next question.

# PHASE 1: THE WELCOME (Play immediately)
"Hello, and welcome to the Taxibot demo. I'm {{agent_name}}, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. You can switch languages at any time, just say the language you prefer, and we'll remember it for your next booking. So, let's get started."

# PHASE 2: SEQUENTIAL GATHERING (Strict Order - NO CONFIRMATIONS)
Follow this order exactly. Only move to the next if you have the current answer:
1. "Where would you like to be picked up?" → Wait for answer, then proceed
2. "And what is your destination?" → Wait for answer, then proceed
3. "How many people will be travelling?" → Wait for answer, then proceed  
4. "When do you need the taxi?" → Wait for answer (Default to 'Now' if ASAP)

🚫 DO NOT confirm or repeat back each answer individually.
🚫 DO NOT say "Got it" or "Great" or "OK" before each question - just ask the question directly.
🚫 DO NOT say "So you want to go to X?" after they give an address.
🚫 DO NOT combine multiple questions into one sentence.
✅ After receiving an answer, immediately ask the NEXT question with no filler words.
✅ Save all confirmations for the Summary phase.

# PHASE 3: THE SUMMARY (Gate Keeper)
Only after the checklist is 100% complete, say:
"Alright, let me quickly summarize your booking. You'd like to be picked up at [pickup address], and travel to [destination address]. There will be [number] of passengers, and you'd like to be picked up [time]. Is that correct?"

# PHASE 4: PRICING (State Lock)
After 'Yes' to summary, say: "Great, one moment please while I check the trip price and estimated arrival time."
→ CALL book_taxi(action='request_quote')

Once tool returns data, say ONLY:
"The trip fare will be [price], and the estimated arrival time is [ETA]. Would you like me to confirm this booking for you?"
🚫 RULE: Do NOT repeat addresses here. Focus only on Price and ETA.

# PHASE 5: DISPATCH & CLOSE
After 'Yes' to price:
"Perfect, thank you. I'm making the booking now. You'll receive the booking details and ride updates via WhatsApp."
→ CALL book_taxi(action='confirmed')

Choose ONE closing randomly:
- "Just so you know, you can also book a taxi by sending us a WhatsApp voice note."
- "Next time, feel free to book your taxi using a WhatsApp voice message."
- "You can always book again by simply sending us a voice note on WhatsApp."

Final Sign-off: "Thank you for trying the Taxibot demo, and have a safe journey."
→ CALL end_call()

# CANCELLATION
If user says "cancel", "never mind", "forget it":
→ CALL cancel_booking
Say: "No problem, I've cancelled that. Is there anything else?"

# NAME HANDLING
If caller says their name → CALL save_customer_name

# GUARDRAILS
❌ NEVER state a price or ETA unless the tool returns that exact value.
❌ NEVER use placeholders - always ask for specifics.
❌ NEVER move to Summary until all 4 checklist items are filled.
❌ NEVER repeat addresses after the summary is confirmed.
❌ NEVER ask "is that where you want to go?" or "is that correct?" after each address - just accept it and move on.
❌ NEVER ask for "more details" or "could you be more specific" - accept the address as given.
❌ NEVER jump back to ask for pickup/destination if they are already marked ✅ in the booking state.
❌ NEVER re-ask a question that has already been answered - check the booking state first.
❌ NEVER ask "Could you please confirm the pickup address as..." or any variation - this is strictly forbidden.
❌ NEVER say "confirm the pickup" or "confirm the destination" - only confirm the FULL summary, never individual addresses.
✅ Accept business names, landmarks, and place names as valid pickup/destination (e.g., "Sweet Spot", "Tesco", "The Hospital", "Train Station").
✅ Only ask for a house number if it's clearly a residential street address missing a number.
✅ If the user gives a place name or business, accept it immediately and move to the next question.
✅ ALWAYS check the "CURRENT BOOKING STATE" section to see what's already captured before asking questions.

# NEAREST/CLOSEST PLACES
When user says "nearest X" or "closest X" (e.g., "nearest hospital", "closest train station", "nearest tube"):
- Accept this as a valid destination or pickup
- Do NOT ask for a specific address - just accept "nearest hospital" as the destination
- The dispatch system will find the actual nearest location based on their pickup

# VENUE SUGGESTIONS (ANYTHING ELSE)
When you ask "Is there anything else I can help you with?" and user asks about places (hotels, restaurants, bars, pubs, cafes):
- Be helpful and suggest 2-3 popular options if you know them
- Say something like: "There are some great options nearby! Would you like me to book a taxi to any of them?"
- If they pick one, use it as the destination and start a new booking
`;


// --- Tool Schemas ---
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
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
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
  
  // Dovey Road mishearings (observed in testing - STT says "Dogpool" for "Dovey")
  "dogpool road": "Dovey Road",
  "dog pool road": "Dovey Road",
  "dogpole road": "Dovey Road",
  "dog pole road": "Dovey Road",
  "dorkey road": "Dovey Road",
  "dork he road": "Dovey Road",
  "doggie road": "Dovey Road",
  "doki road": "Dovey Road",
  "dopey road": "Dovey Road",
  "dovy road": "Dovey Road",
  "dovie road": "Dovey Road",
  "duvey road": "Dovey Road",
  "duffy road": "Dovey Road",
  "duffey road": "Dovey Road",
  "darvi road": "Dovey Road",
  "darby road": "Dovey Road",
  
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
  "david roark": "David Road",
  "david roak": "David Road",
  "david roach": "David Road",
  
  // "52A David Road" phonetic mishearings (Whisper splits "52A" into various fragments)
  "thank you two a. david roark": "52A David Road",
  "thank you two a. david road": "52A David Road",
  "thank you two a david roark": "52A David Road",
  "thank you two a david road": "52A David Road",
  "thank you to a. david roark": "52A David Road",
  "thank you to a david road": "52A David Road",
  "thank you to a. david road": "52A David Road",
  "thank you 2a david road": "52A David Road",
  "thank you 2 a david road": "52A David Road",
  "think you to a david road": "52A David Road",
  "think you 2 a david road": "52A David Road",
  "two a. david roark": "52A David Road",
  "two a. david road": "52A David Road",
  "two a david roark": "52A David Road",
  "two a david road": "52A David Road",
  "2 a david road": "52A David Road",
  "2a david road": "52A David Road",
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
  
  // Specific mishearings observed in testing (destination corrections)
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
  "freight": "eight",  // Common mishearing for "eight"
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
  // NOTE: More specific patterns first to prevent partial matches
  "528 david": "52A David",       // "52A" misheard as "528"
  "five twenty eight david": "52A David",
  "five two eight david": "52A David",
  "5208 david": "52A David",      // "52A" misheard as "5208"
  "five two a david": "52A David",
  "fifty two a david": "52A David",
};

// Hallucination patterns - common STT artifacts from telephony noise
// NOTE: Do NOT filter standalone numbers - they are valid passenger counts!
const HALLUCINATION_PATTERNS = [
  /^(um+|uh+|ah+|er+|hmm+)\.?$/i,
  /^\.+$/,
  /^it's raining/i, // Common telephony artifact
  /^nice weather/i,
  /^how are you/i,
  /^good morning\.?$/i,
  /^good afternoon\.?$/i,
  // SIP/HTTP protocol messages that leak through audio (e.g., TTS of system logs)
  /^\d{3}\s*(ack|ok|ringing|trying|bye|cancel)/i, // 200 ack, 180 ringing, etc.
  /^(ack|sip|rtp|sdp)\b/i, // Protocol keywords
  /^(two hundred|one hundred|three hundred)\s*(ack|ok)/i, // Spoken versions
];

// Whisper "phantom radio host" hallucinations - triggered by silence/static
// These are memorized phrases from Whisper's training data (YouTube, podcasts, etc.)
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
  "chopper classic", // Specific hallucination observed
  "music playing",
  "silence",
  "inaudible",
  "foreign language",
  "[music]",
  "[applause]",
  "[laughter]",
  // Dutch phantom phrases (Whisper training data from Dutch YouTube/podcasts)
  "ondertitels ingediend door",
  "ondertitels door de amara",
  "amara.org gemeenschap",
  "ondertiteling door",
  "vertaald door",
  "bewerkt door",
  "transcriptie door",
  // SIP/HTTP protocol artifacts (leaked from bridge logs/TTS)
  "200 ack",
  "200 okay",
  "180 ringing",
  "183 session progress",
  "404 not found",
  "408 request timeout",
  "503 service unavailable",
  "invite",
  "bye bye",
  "ack",
  "register",
  "options",
  "sip",
  "rtp",
  "websocket",
  "http",
  "https",
  "tcp",
  "udp",
  "port",
  "localhost",
  "127.0.0.1",
  "deno",
  "asterisk",
  "audio socket",
  "audio socket connected",
  "call connected",
  "call ended",
  "hangup",
  "progress",
  "ringing",
  "answered",
  "two hundred ack",
  "one eighty ringing",
  "four oh four not found",
  "five oh three service unavailable",
];

function isHallucination(text: string): boolean {
  const trimmed = text.trim();
  const lower = trimmed.toLowerCase();
  
  // IMPORTANT: Do NOT filter single-digit transcripts ("4", "3", etc.) or number WORDS.
  // Whisper often returns passenger counts as a single digit or word, and filtering them breaks booking.
  if (trimmed.length < 2) {
    if (/^[0-9]$/.test(trimmed)) return false;
    return true;
  }
  
  // CRITICAL: Allow number words through (one, two, three, four, five, six, seven, eight, nine, ten)
  // These are common passenger count responses that must NOT be filtered
  const numberWords = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", 
                       "yes", "no", "yep", "nope", "ok", "okay", "yeah", "nah", "sure", "correct"];
  if (numberWords.includes(lower)) {
    return false;
  }
  
  // Check regex patterns first
  if (HALLUCINATION_PATTERNS.some(pattern => pattern.test(trimmed))) {
    return true;
  }
  
  // Check phantom phrases with normalized comparison (strip punctuation)
  const clean = lower.replace(/[^\w\s]/g, "");
  if (PHANTOM_PHRASES.some(phrase => 
    clean.includes(phrase.toLowerCase().replace(/[^\w\s]/g, ""))
  )) {
    return true;
  }
  
  return false;
}

// Pre-compiled regex pattern for O(1) STT correction lookups (built once at module load)
// This avoids creating new RegExp objects on every transcript, dramatically reducing latency
const STT_CORRECTION_PATTERN = new RegExp(
  `\\b(${Object.keys(STT_CORRECTIONS).map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})\\b`,
  'gi'
);

// Pre-built lowercase lookup map for O(1) correction retrieval
const STT_CORRECTIONS_LOWER = new Map(
  Object.entries(STT_CORRECTIONS).map(([k, v]) => [k.toLowerCase(), v])
);

// ═══════════════════════════════════════════════════════════════════════════════
// ADDRESS CORRECTION DETECTION (ported from paired mode)
// Detects "No", "Actually", "It's..." patterns and maps to pickup/destination
// ═══════════════════════════════════════════════════════════════════════════════
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
    /^now[,\s]+(?:the\s+)?(?:pickup|address)\s+(?:is\s+)?(.+)/i, // "Now, the pickup is 43 Dovey Road"
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
  
  // Filter out confirmation phrases that are NOT addresses
  const confirmationPhrases = ["correct", "right", "yes", "yeah", "yep", "sure", "fine", "ok", "okay", "good", "great", "perfect", "lovely", "brilliant", "that's right", "that's correct"];
  const lowerExtracted = extractedAddress.toLowerCase();
  
  // Check if it's exactly a confirmation phrase OR starts with one
  for (const phrase of confirmationPhrases) {
    if (lowerExtracted === phrase || 
        lowerExtracted.startsWith(phrase + ",") || 
        lowerExtracted.startsWith(phrase + " ") ||
        lowerExtracted.startsWith(phrase + ".") ||
        lowerExtracted.startsWith(phrase + "!")) {
      return { type: null, address: "" };
    }
  }
  
  // Address keywords indicate a valid address
  const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "station", "airport", "hotel", "hospital", "mall", "centre", "center", "square", "park"];
  const hasAddressKeyword = addressKeywords.some(kw => lowerExtracted.includes(kw));
  const hasHouseNumber = /^\d+[a-zA-Z]?\s/.test(extractedAddress) || /\d+[a-zA-Z]?$/.test(extractedAddress);
  
  // If no address keywords and no house number, likely not a real address
  if (!hasAddressKeyword && !hasHouseNumber && extractedAddress.split(/\s+/).length <= 2) {
    return { type: null, address: "" };
  }
  
  // Determine if correcting pickup or destination
  if (lower.includes("pickup") || lower.includes("pick up") || lower.includes("from")) {
    return { type: "pickup", address: extractedAddress };
  }
  if (lower.includes("destination") || lower.includes("to") || lower.includes("going to") || lower.includes("drop")) {
    return { type: "destination", address: extractedAddress };
  }
  
  // Default: if we have a pickup but no destination, assume pickup correction
  if (currentPickup && !currentDestination) {
    return { type: "pickup", address: extractedAddress };
  }
  
  // Default to pickup correction if uncertain
  return { type: "pickup", address: extractedAddress };
}

// Detect simple negation during summary phase ("No", "No.", "Nope")
function isSummaryNegation(text: string): boolean {
  const lower = text.toLowerCase().trim();
  // Match standalone "no", "nope", "not correct", "wrong", etc.
  return /^(no\.?|nope\.?|not correct|wrong|incorrect|that'?s not right|that'?s wrong)$/i.test(lower);
}

function correctTranscript(text: string): string {
  if (!text || text.length === 0) return "";
  
  // Single-pass replacement using pre-compiled pattern
  const corrected = text.replace(STT_CORRECTION_PATTERN, (matched) => {
    return STT_CORRECTIONS_LOWER.get(matched.toLowerCase()) || matched;
  });
  
  // Capitalize first letter and return
  return corrected.charAt(0).toUpperCase() + corrected.slice(1);
}

// ═══════════════════════════════════════════════════════════════════════════════
// ADA MODULE - Modular AI Assistant Functions
// ═══════════════════════════════════════════════════════════════════════════════

/**
 * Ada - Modular AI Taxi Dispatcher
 * 
 * Submodules:
 * - Ada.Prompts: System prompt generation
 * - Ada.Scripts: Pre-defined response scripts
 * - Ada.Responses: Response injection helpers
 * - Ada.Tools: Tool handler utilities
 */
const Ada = {
  // ─────────────────────────────────────────────────────────────────────────────
  // PROMPTS - System prompt and context generation
  // ─────────────────────────────────────────────────────────────────────────────
  Prompts: {
    /** Get the base system instructions */
    getSystemInstructions: (): string => SYSTEM_PROMPT,
    
    /** Build dynamic context based on caller history */
    buildCallerContext: (params: {
      customerName: string | null;
      lastPickup: string | null;
      lastDestination: string | null;
      totalBookings: number;
    }): string => {
      if (!params.lastPickup && !params.lastDestination) return "";
      
      const namePart = params.customerName ? `${params.customerName}'s` : "Caller's";
      return `\n\n[RETURNING CALLER CONTEXT]\n${namePart} last trip: ${params.lastPickup || "unknown"} → ${params.lastDestination || "unknown"} (${params.totalBookings} total bookings).`;
    },
    
    /** Build active booking context */
    buildBookingContext: (params: {
      pickup: string | null;
      destination: string | null;
      passengers: number | null;
      bags: number | null;
    }): string => {
      if (!params.pickup && !params.destination) return "";
      
      const parts: string[] = [];
      if (params.pickup) parts.push(`Pickup: ${params.pickup}`);
      if (params.destination) parts.push(`Destination: ${params.destination}`);
      if (params.passengers) parts.push(`${params.passengers} passenger(s)`);
      if (params.bags) parts.push(`${params.bags} bag(s)`);
      
      return `\n\n[ACTIVE BOOKING]\n${parts.join(", ")}`;
    },
    
    /** Assemble the full system prompt */
    assembleFullPrompt: (params: {
      baseInstructions?: string;
      companyName: string;
      agentName: string;
      callerContext?: string;
      bookingContext?: string;
    }): string => {
      let prompt = params.baseInstructions || SYSTEM_PROMPT;
      
      // Replace placeholders
      prompt = prompt.replace(/\[COMPANY\]/g, params.companyName);
      prompt = prompt.replace(/\[AGENT\]/g, params.agentName);
      
      // Append contexts
      if (params.callerContext) prompt += params.callerContext;
      if (params.bookingContext) prompt += params.bookingContext;
      
      return prompt;
    }
  },

  // ─────────────────────────────────────────────────────────────────────────────
  // SCRIPTS - Pre-defined response scripts
  // ─────────────────────────────────────────────────────────────────────────────
  Scripts: {
    /** Booking confirmed - taxi on the way */
    bookingConfirmed: (): string => 
      "That's booked for you. Is there anything else I can help you with?",
    
    /** Fare quote prompt - NO booking recap, just fare and ETA */
    fareQuote: (fare: string, eta: string): string => 
      `The price is ${fare} and your driver will be ${eta}. Shall I book that?`,
    
    /** Goodbye message */
    goodbye: (): string => "Safe travels!",
    
    /** Anything else prompt */
    anythingElse: (): string => "Is there anything else I can help you with?",
    
    /** No cars available */
    noCars: (): string => 
      "I'm really sorry, but we don't have any cars available at the moment. Would you like me to try again in a few minutes, or can I help with anything else?",
    
    /** Technical issues */
    technicalIssues: (): string => 
      "I'm sorry, we're having some technical issues at the moment. Please ring back a bit later and we'll get that sorted for you.",
    
    /** Booking cancelled */
    bookingCancelled: (): string => 
      "I've cancelled that booking for you. Is there anything else I can help with?",
    
    /** Change acknowledged */
    changeAcknowledged: (field: string, newValue: string): string => 
      `Got it, ${field} is ${newValue}. Is that correct?`,
    
    /** New fare after change */
    newFare: (fare: string): string => 
      `The fare is ${fare}. Shall I book that?`
  },

  // ─────────────────────────────────────────────────────────────────────────────
  // RESPONSES - Response injection helpers
  // ─────────────────────────────────────────────────────────────────────────────
  Responses: {
    /** Create a system message for OpenAI */
    createSystemMessage: (text: string): object => ({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text }]
      }
    }),
    
    /** Create a response.create trigger */
    createResponseTrigger: (instructions?: string): object => ({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        ...(instructions ? { instructions } : {})
      }
    }),
    
    /** Build fare confirmation injection */
    buildFareConfirmation: (params: {
      fare: string;
      eta: string;
      language: string;
      pickup: string;
      destination: string;
    }): { message: object; trigger: object } => {
      const script = Ada.Scripts.fareQuote(params.fare, params.eta);
      const langName = LANGUAGE_NAMES[params.language] || "English";
      
      return {
        message: Ada.Responses.createSystemMessage(
          `[DISPATCH FARE CONFIRMATION]: Read this EXACTLY to the customer IN ${langName.toUpperCase()}: "${script}"\n\n` +
          `After saying it, WAIT for yes/no.\n` +
          `If YES → CALL book_taxi with confirmation_state: "confirmed", pickup: "${params.pickup}", destination: "${params.destination}"\n` +
          `If NO → CALL book_taxi with confirmation_state: "rejected"`
        ),
        trigger: Ada.Responses.createResponseTrigger(
          `Say EXACTLY: "${script}" Then STOP and wait for yes/no.`
        )
      };
    },
    
    /** Build booking confirmation injection */
    buildBookingConfirmation: (language: string): { message: object; trigger: object } => {
      const script = Ada.Scripts.bookingConfirmed();
      const langName = LANGUAGE_NAMES[language] || "English";
      
      return {
        message: Ada.Responses.createSystemMessage(
          `[DISPATCH CONFIRMATION]: The booking has been confirmed. Say this EXACTLY to the customer IN ${langName.toUpperCase()}: "${script}" Be natural and brief. Do not add any extra details.`
        ),
        trigger: Ada.Responses.createResponseTrigger(
          `IMPORTANT: Say EXACTLY: "${script}" Do not add extra details.`
        )
      };
    },
    
    /** Build goodbye injection */
    buildGoodbye: (language: string): { message: object; trigger: object } => {
      const script = Ada.Scripts.goodbye();
      const langName = LANGUAGE_NAMES[language] || "English";
      
      return {
        message: Ada.Responses.createSystemMessage(
          `[SYSTEM: The customer said goodbye. Say "${script}" IN ${langName.toUpperCase()} and nothing more. Then stay completely silent.]`
        ),
        trigger: Ada.Responses.createResponseTrigger(
          `Say ONLY: "${script}" Then stay silent.`
        )
      };
    }
  },

  // ─────────────────────────────────────────────────────────────────────────────
  // TOOLS - Tool result helpers
  // ─────────────────────────────────────────────────────────────────────────────
  Tools: {
    /** Parse luggage from string to integer */
    parseLuggage: (bagsInput: unknown): number => {
      if (typeof bagsInput === "number") return bagsInput;
      if (typeof bagsInput !== "string") return 0;
      
      const lower = bagsInput.toLowerCase().trim();
      if (lower === "no" || lower === "none" || lower === "no baggage" || lower === "no bags") return 0;
      
      const numMatch = lower.match(/(\d+)/);
      return numMatch ? parseInt(numMatch[1], 10) : 0;
    },
    
    /** Normalize trip key for duplicate detection */
    makeTripKey: (pickup: string | null, destination: string | null): string => {
      const normalize = (s: string | null) => (s || "")
        .toLowerCase()
        .replace(/\s+/g, " ")
        .replace(/\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr|close|cl|way|place|pl)\b/gi, "")
        .trim();
      return `${normalize(pickup)}::${normalize(destination)}`;
    },
    
    /** Check if address is invalid */
    isInvalidAddress: (addr: string | null): boolean => {
      if (!addr) return true;
      const lower = addr.toLowerCase().trim();
      return lower === "" || 
             lower === "[redacted]" || 
             lower === "unknown" ||
             lower === "null";
    },
    
    /** Build webhook payload for dispatch */
    buildWebhookPayload: (params: {
      callId: string;
      phone: string | null;
      action: string;
      pickup: string;
      destination: string;
      passengers?: number;
      bags?: number;
      pickupTime?: string;
      vehicleType?: string;
      customerName?: string | null;
      transcripts?: string[];
    }): object => ({
      job_id: params.callId,
      call_id: params.callId,
      caller_phone: params.phone,
      action: params.action,
      ada_pickup: params.pickup,
      ada_dropoff: params.destination,
      passengers: params.passengers || 1,
      bags: params.bags || 0,
      pickup_time: params.pickupTime || "now",
      vehicle_type: params.vehicleType || "saloon",
      customer_name: params.customerName || null,
      user_transcripts: params.transcripts || []
    })
  },

  // ─────────────────────────────────────────────────────────────────────────────
  // DETECTION - Intent and phrase detection
  // ─────────────────────────────────────────────────────────────────────────────
  Detection: {
    /** Goodbye phrases that end the call */
    GOODBYE_PHRASES: [
      "bye", "goodbye", "see ya", "see you", "cya", "i'm done", "im done", 
      "hang up", "end call", "no thank you", "no thanks", "no that's all", 
      "no thats all", "nothing else", "that's it", "thats it", "that'll be all",
      "thatll be all", "i'm good", "im good", "all good", "all done"
    ],
    
    /** Phrases indicating "yes" to fare */
    YES_PHRASES: [
      "yes", "yeah", "yep", "yup", "go ahead", "book it", "do it", "please do",
      "sure", "okay", "ok", "alright", "sounds good", "correct", "that's right",
      "thats right", "that's correct", "thats correct", "right", "confirm",
      "confirmed", "i confirm", "please", "proceed"
    ],
    
    /** Phrases indicating "no" to fare */
    NO_PHRASES: [
      "no", "nope", "don't", "do not", "dont", "cancel", "stop", "never mind",
      "nevermind", "not now", "nah", "too expensive"
    ],
    
    /** Check if text matches goodbye intent */
    isGoodbye: (text: string): boolean => {
      const lower = text.toLowerCase();
      return Ada.Detection.GOODBYE_PHRASES.some(p => lower.includes(p)) &&
             !/going to|from|pick ?up|drop ?off/i.test(text);
    },
    
    /** Check if text matches "yes" to fare */
    isYesToFare: (text: string): boolean => {
      const lower = text.toLowerCase();
      return Ada.Detection.YES_PHRASES.some(p => new RegExp(`\\b${p}\\b`, "i").test(lower));
    },
    
    /** Check if text matches "no" to fare */
    isNoToFare: (text: string): boolean => {
      const lower = text.toLowerCase();
      return Ada.Detection.NO_PHRASES.some(p => new RegExp(`\\b${p}\\b`, "i").test(lower));
    },
    
    /** Detect address correction intent (negation) */
    detectAddressCorrection: (text: string): { field: "pickup" | "destination" | null; value: string | null } => {
      const pickupMatch = text.match(/(?:no|not|wrong)[,.]?\s*(?:the\s+)?(?:pickup|pick\s*up)\s+(?:is|should be|location is)\s+(.+)/i) ||
                          text.match(/(?:no|not|wrong)[,.]?\s*(?:i(?:'?m| am)\s+at|from)\s+(.+)/i);
      
      const destMatch = text.match(/(?:no|not|wrong)[,.]?\s*(?:the\s+)?(?:destination|drop\s*off|going to)\s+(?:is|should be)\s+(.+)/i) ||
                        text.match(/(?:no|not|wrong)[,.]?\s*(?:to|going to)\s+(.+)/i);
      
      if (pickupMatch) return { field: "pickup", value: pickupMatch[1].trim() };
      if (destMatch) return { field: "destination", value: destMatch[1].trim() };
      return { field: null, value: null };
    },
    
    /**
     * Detect booking modification intent from user speech.
     * Returns the field to change and the new value if detected.
     * This allows auto-applying changes without double confirmation.
     */
    detectBookingModification: (text: string): { 
      field: "pickup" | "destination" | "passengers" | "bags" | null; 
      value: string | null;
      isModification: boolean;
    } => {
      const lower = text.toLowerCase().trim();
      
      // SPECIAL CASE: "change it from X going to Y" or "change from X to Y"
      // This mentions BOTH pickup and destination - extract destination (the new part after "to/going to")
      const fromToMatch = text.match(/(?:change\s+(?:it\s+)?)?from\s+[\w\s]+?\s+(?:going\s+)?to\s+(.+)/i);
      if (fromToMatch && fromToMatch[1]) {
        const value = fromToMatch[1].replace(/\.+$/g, "").trim();
        if (value.length > 2) {
          return { field: "destination", value, isModification: true };
        }
      }
      
      // PICKUP changes
      // "change pickup to X", "pickup is X", "pick me up from X instead", "actually from X"
      const pickupPatterns = [
        /(?:change|update|modify)\s+(?:the\s+)?(?:pickup|pick\s*up)(?:\s+(?:to|address))?\s+(?:to\s+)?(.+)/i,
        /(?:pickup|pick\s*up)\s+(?:is|should be|from)\s+(.+)/i,
        /(?:pick\s+(?:me\s+)?up|collect\s+(?:me|us))\s+(?:from|at)\s+(.+?)(?:\s+instead)?$/i,
        /(?:actually|no)\s+(?:from|at|pick\s*up\s+from)\s+(.+)/i,
        /(?:i(?:'?m| am)|we(?:'?re| are))\s+(?:at|on)\s+(.+?)(?:\s+(?:not|instead))/i,
        /(?:change|switch)\s+(?:it|that)\s+to\s+(.+?)\s+(?:for\s+)?(?:pickup|pick\s*up)/i,
      ];
      
      for (const pattern of pickupPatterns) {
        const match = text.match(pattern);
        if (match && match[1]) {
          let value = match[1]
            .replace(/(?:\s+please|\s+thanks?|\s+instead|\s+now)$/i, "")
            .replace(/\.+$/g, "") // Remove trailing periods
            .trim();
          if (value.length > 2) {
            return { field: "pickup", value, isModification: true };
          }
        }
      }
      
      // DESTINATION changes
      // "change destination to X", "going to X instead", "actually to X", "take me to X instead"
      const destPatterns = [
        /(?:change|update|modify)\s+(?:the\s+)?(?:destination|drop\s*off)(?:\s+(?:to|address))?\s+(?:to\s+)?(.+)/i,
        /(?:destination|drop\s*off)\s+(?:is|should be|to)\s+(.+)/i,
        /(?:going|go)\s+to\s+(.+?)(?:\s+instead)?$/i,
        /(?:actually|no)\s+(?:to|going\s+to)\s+(.+)/i,
        /(?:take|drop)\s+(?:me|us)\s+(?:to|at|off\s+at)\s+(.+?)(?:\s+instead)?$/i,
        /(?:change|switch)\s+(?:it|that)\s+to\s+(.+?)\s+(?:for\s+)?(?:destination|drop\s*off)/i,
        /(?:i\s+)?(?:want|need)\s+to\s+go\s+to\s+(.+?)(?:\s+instead)?$/i,
      ];
      
      for (const pattern of destPatterns) {
        const match = text.match(pattern);
        if (match && match[1]) {
          // Clean extracted value - remove trailing noise and strip leading context phrases
          let value = match[1]
            .replace(/(?:\s+please|\s+thanks?|\s+instead|\s+now)$/i, "")
            .replace(/^(?:change\s+it\s+)?(?:from\s+[\w\s]+?\s+)?(?:going\s+to\s+|to\s+)/i, "") // Strip "from X going to" or "from X to"
            .replace(/\.+$/g, "") // Remove trailing periods
            .trim();
          if (value.length > 2) {
            return { field: "destination", value, isModification: true };
          }
        }
      }
      
      // PASSENGERS changes
      // "change to 3 passengers", "actually 4 people", "there's 2 of us", "make it three passengers"
      // Word-to-number mapping for spoken numbers
      const wordToNum: Record<string, number> = {
        one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8
      };
      
      const passengersPatterns = [
        /(?:change|update|make\s+it)\s+(?:to\s+)?(\d+|one|two|three|four|five|six|seven|eight)\s+(?:passengers?|people|persons?)/i,
        /(?:actually|no)\s+(\d+|one|two|three|four|five|six|seven|eight)\s+(?:passengers?|people|persons?)/i,
        /(?:there(?:'s| is| are)|we(?:'re| are))\s+(\d+|one|two|three|four|five|six|seven|eight)\s+(?:of\s+us|people|passengers?)/i,
        /(\d+|one|two|three|four|five|six|seven|eight)\s+(?:passengers?|people)\s+(?:instead|now)/i,
      ];
      
      for (const pattern of passengersPatterns) {
        const match = text.match(pattern);
        if (match && match[1]) {
          // Convert word to number if needed
          const val = match[1].toLowerCase();
          const num = wordToNum[val] ?? parseInt(val, 10);
          if (num >= 1 && num <= 8) {
            return { field: "passengers", value: String(num), isModification: true };
          }
        }
      }
      
      // BAGS changes
      // "actually 2 bags", "no bags", "3 pieces of luggage"
      const bagsPatterns = [
        /(?:change|update)\s+(?:to\s+)?(\d+)\s+(?:bags?|luggage|suitcases?)/i,
        /(?:actually|no)\s+(\d+)\s+(?:bags?|luggage|suitcases?)/i,
        /(?:i(?:'ve| have)|we(?:'ve| have))\s+(?:got\s+)?(\d+)\s+(?:bags?|pieces?\s+of\s+luggage)/i,
        /(?:no\s+)?(?:bags?|luggage)/i, // "no bags" or "no luggage"
      ];
      
      for (const pattern of bagsPatterns) {
        const match = text.match(pattern);
        if (match) {
          if (match[1]) {
            return { field: "bags", value: match[1], isModification: true };
          } else if (/^no\s+(?:bags?|luggage)/i.test(text)) {
            return { field: "bags", value: "0", isModification: true };
          }
        }
      }
      
      return { field: null, value: null, isModification: false };
    }
  }
};

// ═══════════════════════════════════════════════════════════════════════════════
// END ADA MODULE
// ═══════════════════════════════════════════════════════════════════════════════

// ═══════════════════════════════════════════════════════════════════════════════
// "TRUST ADA'S FIRST ECHO" MODE (ported from paired mode)
// When Ada immediately acknowledges an address (e.g., "Got it, 18 Exmoor Road"),
// extract that as the canonical value. Ada's interpretation is often more accurate
// than raw STT transcripts because she has context and UK address knowledge.
// ═══════════════════════════════════════════════════════════════════════════════
interface AdaEchoExtraction {
  type: "pickup" | "destination" | null;
  address: string;
}

function extractAdaFirstEcho(
  adaTranscript: string,
  lastQuestionType: "pickup" | "destination" | "passengers" | "time" | "confirmation" | null
): AdaEchoExtraction {
  const lower = adaTranscript.toLowerCase();
  
  // SKIP summaries - only trust immediate acknowledgments
  const isSummaryText = lower.includes("let me confirm") ||
                    lower.includes("to confirm") ||
                    lower.includes("so that's") ||
                    lower.includes("summarize") ||
                    lower.includes("your booking") ||
                    lower.includes("booking details") ||
                    lower.includes("quickly summarize") ||
                    lower.includes("picked up at") ||
                    lower.includes("travel to");
  
  if (isSummaryText) {
    return { type: null, address: "" };
  }
  
  // Immediate acknowledgment patterns for PICKUP
  const pickupPatterns = [
    /got it[,.]?\s+(?:picking you up (?:from|at)\s+)?([^,.]+?)(?:\s+for your pickup|\s+as your pickup|\s+for pickup|[,.]|\s+and\s+)/i,
    /(?:your )?pickup (?:is|will be|address is)\s+([^,.]+?)(?:[,.]|\s+and\s+)/i,
    /picking you up (?:from|at)\s+([^,.]+?)(?:[,.]|\s+and\s+)/i,
    /thank you[,.]?\s+(?:picking you up (?:from|at)\s+)?([^,.]+?)(?:\s+for your pickup|\s+as your pickup|[,.]|\s+and\s+)/i,
    /perfect[,.]?\s+([^,.]+?)(?:\s+for your pickup|\s+as pickup|[,.]|\s+and\s+)/i,
    /lovely[,.]?\s+([^,.]+?)(?:\s+for your pickup|\s+as pickup|[,.]|\s+and\s+)/i,
    /great[,.]?\s+([^,.]+?)(?:\s+for your pickup|\s+as pickup|[,.]|\s+and\s+)/i,
  ];
  
  // Immediate acknowledgment patterns for DESTINATION
  const destinationPatterns = [
    /(?:your )?destination (?:is|will be)\s+([^,.]+?)(?:[,.]|\s+and\s+|\s+how many)/i,
    /(?:going|heading|travelling?) to\s+([^,.]+?)(?:[,.]|\s+and\s+|\s+how many)/i,
    /(?:drop(?:ping)? you (?:off )?at|to)\s+([^,.]+?)(?:[,.]|\s+and\s+|\s+how many)/i,
    /thank you[,.]?\s+([^,.]+?)(?:\s+(?:is |as )?(?:your )?destination|[,.]|\s+how many)/i,
    /got it[,.]?\s+([^,.]+?)(?:\s+(?:is |as )?(?:your )?destination|[,.]|\s+how many)/i,
    /perfect[,.]?\s+([^,.]+?)(?:\s+(?:is |as )?(?:your )?destination|[,.]|\s+how many)/i,
    /lovely[,.]?\s+([^,.]+?)(?:\s+(?:is |as )?(?:your )?destination|[,.]|\s+how many)/i,
  ];
  
  // Determine which field to extract based on lastQuestionType context
  let patterns: RegExp[] = [];
  let fieldType: "pickup" | "destination" | null = null;
  
  if (lastQuestionType === "pickup") {
    patterns = pickupPatterns;
    fieldType = "pickup";
  } else if (lastQuestionType === "destination") {
    patterns = destinationPatterns;
    fieldType = "destination";
  } else {
    // Check both if context is unclear
    for (const p of pickupPatterns) {
      const match = adaTranscript.match(p);
      if (match && match[1]) {
        const addr = cleanAdaEchoAddress(match[1]);
        if (isValidEchoAddress(addr)) {
          return { type: "pickup", address: addr };
        }
      }
    }
    for (const p of destinationPatterns) {
      const match = adaTranscript.match(p);
      if (match && match[1]) {
        const addr = cleanAdaEchoAddress(match[1]);
        if (isValidEchoAddress(addr)) {
          return { type: "destination", address: addr };
        }
      }
    }
    return { type: null, address: "" };
  }
  
  // Try each pattern
  for (const p of patterns) {
    const match = adaTranscript.match(p);
    if (match && match[1]) {
      const addr = cleanAdaEchoAddress(match[1]);
      if (isValidEchoAddress(addr)) {
        return { type: fieldType, address: addr };
      }
    }
  }
  
  return { type: null, address: "" };
}

function cleanAdaEchoAddress(raw: string): string {
  return raw
    .replace(/^\s*(?:from|at|to|is|it's|it is)\s+/i, "")
    .replace(/\s*(?:for your|as your|for the|is your).*$/i, "")
    .replace(/[.?!,]+$/g, "")
    .trim();
}

function isValidEchoAddress(addr: string): boolean {
  if (!addr || addr.length < 3) return false;
  const lower = addr.toLowerCase();
  
  // Must have address keyword OR house number
  const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "station", "airport", "hotel", "hospital", "mall", "centre", "center", "square", "park", "green", "hill", "gardens", "grove"];
  const hasKeyword = addressKeywords.some(kw => lower.includes(kw));
  const hasHouseNumber = /^\d+[a-zA-Z]?\s/.test(addr) || /\s\d+[a-zA-Z]?$/.test(addr);
  
  // Filter out question fragments
  if (lower.includes("what") || lower.includes("where") || lower.includes("how many")) {
    return false;
  }
  
  // Filter out common short phrases that aren't addresses
  if (lower === "here" || lower === "there" || lower === "that" || lower === "this") {
    return false;
  }
  
  return hasKeyword || hasHouseNumber;
}


interface TranscriptItem {
  role: "user" | "assistant";
  text: string;
  timestamp: string;
}

interface SessionState {
  callId: string;
  phone: string;
  companyName: string;
  agentName: string;
  voice: string;
  language: string; // Language code for Whisper STT (e.g., "en", "es", "fr")
  customerName: string | null;
  hasActiveBooking: boolean;
  activeBookingCallId: string | null; // Original call_id of the active booking (for updates when resuming)
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    bags: number | null;
    vehicle_type: string | null;
    pickup_time: string | null;
    version: number;
  };
  transcripts: TranscriptItem[];

  // Caller history from database
  callerLastPickup: string | null;
  callerLastDestination: string | null;

  // Inbound audio format from bridge: "ulaw" (8kHz), "slin" (8kHz), "slin16" (16kHz), or "pcm48" (48kHz from Opus decode)
  inboundAudioFormat: "ulaw" | "slin" | "slin16" | "pcm48";
  inboundSampleRate: number; // 8000, 16000, or 48000

  // Rasa-style audio processing toggle
  useRasaAudioProcessing: boolean;
  callerTotalBookings: number;
  
  // 48kHz audio logging flag (to avoid spamming logs)
  _logged48k?: boolean;


  // Streaming assistant transcript assembly (OpenAI sends token deltas)
  assistantTranscriptIndex: number | null;

  // Debounced DB flush
  transcriptFlushTimer: number | null;

  // Echo guard / barge-in: track when Ada is speaking to ignore audio feedback
  isAdaSpeaking: boolean;
  echoGuardUntil: number; // timestamp until which to ignore audio
  responseStartTime: number; // timestamp when current AI response started (for initial echo guard)
  bargeInIgnoreUntil: number; // timestamp until which we ignore barge-in checks (startup echo)
  openAiResponseActive: boolean; // true between response.created and response.done
  deferredResponseCreate: boolean; // if true, send response.create right after response.done
  // Speech timing diagnostics
  speechStartTime: number | null;
  speechStopTime: number | null;
  lastUserTranscriptAt: number | null; // When last user transcript was received (for guard bypass)

  // === PROTECTION WINDOWS (ported from paired mode) ===
  // Greeting protection: ignore inbound audio right after connect
  greetingProtectionUntil: number;
  // Summary protection: block interruptions while Ada is recapping/quoting
  summaryProtectionUntil: number;

  // === AUDIO DIAGNOSTICS (ported from paired mode) ===
  audioDiagnostics: {
    packetsReceived: number;
    packetsForwarded: number;
    packetsSkippedNoise: number;
    packetsSkippedEcho: number;
    packetsSkippedBotSpeaking: number;
    packetsSkippedGreeting: number;
    packetsSkippedSummary: number;
    lastRmsValues: number[];
    avgRms: number;
  };
  
  // === TTS (OUTBOUND) AUDIO DIAGNOSTICS ===
  ttsDiagnostics: {
    chunksSent: number;
    chunksDiscarded: number;
    lastRmsValues: number[];
    avgRms: number;
    peakRms: number;
  };

  // Call ended flag - prevents further processing after end_call
  callEnded: boolean;
  // Timestamp when call started - used for greeting protection window
  callStartedAt: number;
  // Allow exactly one final assistant response (the goodbye) after callEnded=true.
  // Without this, the response.created handler will immediately cancel the goodbye.
  finalGoodbyePending: boolean;

  // Half-duplex mode: when enabled, user audio is buffered while Ada speaks and only forwarded after
  halfDuplex: boolean;
  halfDuplexBuffer: Uint8Array[]; // Buffer for audio while Ada is speaking

  // --- Booking tool enforcement ---
  // True only after a successful book_taxi tool result for the CURRENT booking.
  // Used to prevent Ada from saying "Booked!" without actually placing the booking.
  bookingConfirmedThisTurn: boolean;
  // Persistent flag: Once a booking is fully confirmed (webhook sent), Ada can speak freely
  // This is NOT reset per turn - once confirmed, enforcement is disabled for the rest of the call
  bookingFullyConfirmed: boolean;
  lastBookTaxiSuccessAt: number | null;
  lastConfirmedTripKey: string | null;
  
  // Audio buffering for confirmation enforcement
  // When true, audio is forwarded immediately; when false, audio is buffered until transcript verification
  audioVerified: boolean;
  pendingAudioBuffer: string[]; // Base64-encoded audio chunks waiting for transcript verification
  // When true, drop assistant audio deltas for the current response (used after response.cancel to avoid partial leaks)
  discardCurrentResponseAudio: boolean;

  // Fare quote state - stores fare/ETA from request_quote until confirmed/rejected
  pendingQuote: {
    fare: string | null;
    eta: string | null;
    pickup: string | null;
    destination: string | null;
    pickup_time: string | null; // Normalized pickup time (YYYY-MM-DD HH:MM or ASAP)
    callback_url: string | null;
    timestamp: number;
    lastPrompt: string | null; // The fare prompt text to repeat if duplicate request_quote
  } | null;

  // Track the MOST RECENT quote request we initiated (used to ignore stale dispatch_ask_confirm)
  quoteRequestedAt: number | null;
  quoteTripKey: string | null;

  // Prevent repeated fare prompts (double response.create / duplicate dispatch events)
  lastQuotePromptAt: number | null;
  lastQuotePromptText: string | null;

  // Dispatch events that arrive while OpenAI isn't ready / while a response is active
  pendingDispatchEvents: { event: string; payload: any; receivedAt: number }[];

  // Pending modification awaiting user confirmation before webhook is sent
  pendingModification: {
    field: string;
    oldValue: string;
    newValue: string;
    timestamp: number;
  } | null;

  // AI extraction in progress - blocks Ada from responding until complete
  extractionInProgress: boolean;
  
  // Awaiting dispatch callback - blocks VAD responses while polling for fare quote
  awaitingDispatchCallback: boolean;

  // Prevent repeating the same modification summary (duplicate transcripts / VAD noise)
  lastModificationPromptAt: number | null;
  lastModificationPromptKey: string | null;
  // Allow exactly one assistant response after we inject a modification prompt.
  modificationPromptPending: boolean;
  // Track last spoken confirmation to detect and prevent repetition
  lastSpokenConfirmation: string | null;
  lastSpokenConfirmationAt: number | null;

  // Track when Ada last asked ANY question (for post-booking response wait)
  // If Ada asks "Would you like to book a taxi to one of these events?" we must wait for response
  adaAskedQuestionAt: number | null;

  // Pending NEW booking awaiting user confirmation before fare quote is requested
  pendingNewBooking: {
    pickup: string;
    destination: string;
    passengers: number;
    bags: number;
    timestamp: number;
  } | null;
  
  // Track if we're waiting for new booking confirmation
  newBookingPromptPending: boolean;
  lastNewBookingPromptAt: number | null;

  // Active booking acknowledgement - user must say "keep", "yes", etc. before rebooking
  activeBookingAcknowledged: boolean;

  // "Anything else?" guard - set to true ONLY after Ada asks "Is there anything else I can help you with?"
  // Until this is true, phrases like "no thanks" should NOT trigger goodbye (user might be rejecting fare)
  askedAnythingElse: boolean;
  // Timestamp when "anything else?" was asked - used to enforce grace period for user response
  askedAnythingElseAt: number | null;
  // Configurable grace period (from agent.goodbye_grace_ms) - how long to wait before accepting soft goodbyes
  goodbyeGraceMs: number;
  // Flag to allow confirmation response through the "anything else" guard
  confirmationResponsePending: boolean;

  // Track what Ada last asked about - used to detect question/answer misalignment
  // e.g., user gives address when asked about passengers
  lastQuestionType: "pickup" | "destination" | "passengers" | "time" | "confirmation" | null;
  lastQuestionAt: number | null;
  lastPassengerMismatchAt: number | null; // Prevent double-asking passengers due to mismatch loop
  
  // RACE CONDITION FIX: Snapshot the question type when user STARTS speaking
  // This ensures we map the transcript to the correct question even if Ada advances state
  // before the transcript arrives (e.g., user says destination but Ada already asked passengers)
  questionTypeAtSpeechStart: "pickup" | "destination" | "passengers" | "time" | "confirmation" | null;
  
  // DUPLICATE QUESTION PREVENTION: Track the exact text of the last question spoken
  // Used to cancel if Ada tries to ask the same question twice in a row (within 10s)
  lastSpokenQuestion: string | null;
  lastSpokenQuestionAt: number | null;

  // MULTI-QUESTION GUARD: prevents "... destination? ... passengers?" in one assistant turn
  lastMultiQuestionFixAt: number | null;

  // GREETING COMPLETION GUARD: prevents follow-up responses until greeting is fully delivered
  greetingDelivered: boolean;
  
  // STRICT SEQUENTIAL BOOKING STATE MACHINE
  // Enforces Pickup → Destination → Passengers → Time order
  // Ada cannot advance to the next step until the current field is complete
  // Guards cannot rewind to previous steps - only forward progress allowed
  bookingStep: "pickup" | "destination" | "passengers" | "time" | "summary" | "confirmed";
  // Track when we advanced to current step (prevents rapid step changes from extraction race conditions)
  bookingStepAdvancedAt: number | null;

  // STT Accuracy Metrics (for A/B testing audio processing modes)
  sttMetrics: {
    totalTranscripts: number;
    totalWords: number;
    totalChars: number;
    correctedTranscripts: number;
    filteredHallucinations: number;
    duplicateFarePrompts: number; // Count of ignored duplicate ask_confirm broadcasts
    avgTranscriptDelayMs: number;
    transcriptDelays: number[];
    avgSpeechDurationMs: number;
    speechDurations: number[];
  };
  
  // === AUTHORITATIVE TRANSCRIPT EXTRACTION ===
  // These store what the USER ACTUALLY SAID (from transcripts) - OpenAI tool calls cannot override them.
  // This prevents hallucinations like using caller_name instead of what user spoke.
  transcriptExtractedPickup: string | null;
  transcriptExtractedDestination: string | null;
  transcriptExtractedPassengers: number | null;
  transcriptExtractedTime: string | null;
  
  // === CONFIRMATION FAILSAFE ===
  // If dispatch_confirm broadcast doesn't arrive within timeout, trigger confirmation directly
  confirmationSpoken: boolean; // True once Ada has spoken the confirmation message
  confirmationFailsafeTimerId: ReturnType<typeof setTimeout> | null; // Timer ID for the failsafe
  confirmationAskedAt: number | null; // When Ada asked "Is that correct?" - used for timeout fallback
  
  // === AUDIO BUFFER TRACKING (for safe commits) ===
  // Set true when audio is actually appended, false on commit. Prevents empty buffer commit errors.
  audioBufferedSinceSpeechStart: boolean;
  // Milliseconds of audio buffered since last commit/clear - prevents commit_empty errors
  // OpenAI requires at least 100ms of audio before commit
  audioBufferedMs: number;
  // Accumulated partial transcript from Whisper deltas - used for punctuation detection
  partialTranscript: string;
  // Whether we already committed due to punctuation detection (prevent double-commits)
  punctuationCommitSent: boolean;
  // True once OpenAI has accepted a commit for the current utterance.
  // Prevents double-commit (which causes input_audio_buffer_commit_empty).
  didCommitThisUtterance: boolean;
  // If we had to cancel a VAD-triggered response because we're still waiting for the user transcript,
  // we set this flag so we can explicitly trigger response.create as soon as the transcript arrives.
  pendingTurnResponseCreate: boolean;

  // One-shot bypass: allow a single assistant turn through the turn-based guard.
  // Used for system-driven reprompts (e.g., missing step data guard) so we don't cancel our own reprompt.
  allowOneResponseWhileAwaitingUserAnswer: boolean;
  // Count of recent OpenAI errors - used to detect stuck state and trigger recovery
  recentErrorCount: number;
  lastErrorAt: number | null;
  
  // === STRICT TURN-BASED PROTOCOL ===
  // When true, Ada has asked a data-collection question and is WAITING for a user answer.
  // All VAD-triggered responses are blocked until a user transcript is received.
  // This prevents Ada from advancing to the next question before the user speaks.
  awaitingUserAnswer: boolean;
  // Timestamp when Ada started waiting for user answer - used for timeout handling
  awaitingUserAnswerSince: number | null;
  // The question type Ada is waiting for an answer to (for Q&A logging)
  awaitingAnswerForStep: "pickup" | "destination" | "passengers" | "time" | "confirmation" | null;
}

serve(async (req) => {
  // Handle CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  // WebSocket upgrade
  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426, headers: corsHeaders });
  }

  // MAXIMUM idle timeout (300s = 5 minutes) to prevent disconnects during slow dispatch or long conversations
  // Deno's max supported idle timeout - keeps connection alive through proxy timeouts
  const { socket, response } = Deno.upgradeWebSocket(req, { idleTimeout: 300_000 });
  
  let openaiWs: WebSocket | null = null;
  let state: SessionState | null = null;
  let openaiConnected = false;
  let preConnected = false; // Track if we pre-connected before init
  let pendingGreeting = false; // Track if greeting should fire when OpenAI ready
  let dispatchChannel: ReturnType<typeof supabase.channel> | null = null; // Track for cleanup
  let isConnectionClosed = false; // Prevent operations after cleanup
  let keepAliveInterval: number | null = null; // Keep-alive ping interval to prevent timeout
  let closingGracePeriodActive = false; // Prevent socket.onclose from interrupting goodbye speech
  let closingGraceTimeoutId: number | null = null; // Track the closing grace timeout for cleanup
  let sessionTimeoutId: number | null = null; // Session timeout timer to prevent Supabase timeout
  const sessionStartTime = Date.now(); // Track when the session started
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
  
  // Maximum session duration (4 minutes) - end gracefully before Supabase's ~5 minute limit
  const MAX_SESSION_DURATION_MS = 4 * 60 * 1000;
  
  // --- Keep-alive ping to prevent WebSocket timeout during dispatch callback wait ---
  const startKeepAlive = (callId: string) => {
    if (keepAliveInterval) return; // Already running
    
    // Send ping every 8 seconds (more aggressive) to keep connection alive through all proxies
    keepAliveInterval = setInterval(() => {
      if (isConnectionClosed || !socket) {
        if (keepAliveInterval) {
          clearInterval(keepAliveInterval);
          keepAliveInterval = null;
        }
        return;
      }
      
      try {
        socket.send(JSON.stringify({ type: "keepalive", timestamp: Date.now() }));
        console.log(`[${callId}] 💓 Keep-alive ping sent`);
      } catch (e) {
        console.error(`[${callId}] ❌ Keep-alive ping failed:`, e);
        if (keepAliveInterval) {
          clearInterval(keepAliveInterval);
          keepAliveInterval = null;
        }
      }
    }, 8000) as unknown as number;
    
    console.log(`[${callId}] 🔄 Keep-alive started (8s interval for max stability)`);
  };
  
  const stopKeepAlive = () => {
    if (keepAliveInterval) {
      clearInterval(keepAliveInterval);
      keepAliveInterval = null;
      console.log(`[${state?.callId || "unknown"}] 🛑 Keep-alive stopped`);
    }
  };
  
  // --- Session timeout to gracefully end call before Supabase kills function ---
  const startSessionTimeout = (callId: string) => {
    if (sessionTimeoutId) return; // Already running
    
    sessionTimeoutId = setTimeout(() => {
      if (isConnectionClosed) return;
      
      console.log(`[${callId}] ⏰ Session timeout reached (${MAX_SESSION_DURATION_MS / 1000}s) - ending gracefully`);
      
      // If state exists and OpenAI is connected, send a goodbye message
      if (state && openaiWs && openaiConnected && !state.callEnded) {
        state.callEnded = true;
        closingGracePeriodActive = true;
        
        // Inject a polite session timeout message
        openaiWs.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "message",
            role: "user",
            content: [{ type: "input_text", text: "[SYSTEM: Maximum call duration reached. End the call politely now.]" }]
          }
        }));
        
        openaiWs.send(JSON.stringify({ type: "response.create" }));
        
        // Give Ada 6 seconds to say goodbye, then close
        closingGraceTimeoutId = setTimeout(() => {
          console.log(`[${callId}] 📴 Session timeout grace period expired - closing`);
          isConnectionClosed = true;
          stopKeepAlive();
          try {
            socket.close(1000, "session_timeout");
          } catch (e) {
            console.warn(`[${callId}] ⚠️ Error closing socket on timeout:`, e);
          }
        }, 6000) as unknown as number;
      } else {
        // No state or not connected - just close
        isConnectionClosed = true;
        stopKeepAlive();
        try {
          socket.close(1000, "session_timeout");
        } catch (e) {
          console.warn(`[${callId}] ⚠️ Error closing socket on timeout:`, e);
        }
      }
    }, MAX_SESSION_DURATION_MS) as unknown as number;
    
    console.log(`[${callId}] ⏱️ Session timeout started (${MAX_SESSION_DURATION_MS / 1000}s)`);
  };
  
  const stopSessionTimeout = () => {
    if (sessionTimeoutId) {
      clearTimeout(sessionTimeoutId);
      sessionTimeoutId = null;
    }
  };

  // --- Connect to OpenAI (can be called early for pre-connection) ---
  const connectToOpenAI = (sessionState: SessionState, triggerGreeting: boolean = true) => {
    if (openaiWs) {
      console.log(`[${sessionState.callId}] ⚡ OpenAI already connected (pre-connected)`);
      if (triggerGreeting) {
        sendSessionUpdate(sessionState);
      }
      return;
    }
    
    const url = `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`;
    
    // Deno WebSocket requires protocols array, not headers object
    openaiWs = new WebSocket(url, [
      "realtime",
      `openai-insecure-api-key.${OPENAI_API_KEY}`,
      "openai-beta.realtime-v1"
    ]);

    openaiWs.onopen = () => {
      console.log(`[${sessionState.callId}] ✅ Connected to OpenAI Realtime`);
      openaiConnected = true;
      
      // If this was a pre-connect, don't send greeting yet - wait for init
      if (preConnected && !pendingGreeting) {
        console.log(`[${sessionState.callId}] ⏳ Pre-connected, waiting for init to trigger greeting`);
        return;
      }
      
      sendSessionUpdate(sessionState);
    };

    openaiWs.onmessage = (event) => {
      try {
        const message = JSON.parse(event.data);
        handleOpenAIMessage(message, sessionState);
      } catch (e) {
        console.error(`[${sessionState.callId}] Failed to parse OpenAI message:`, e);
      }
    };

    openaiWs.onclose = () => {
      console.log(`[${sessionState.callId}] 🔌 OpenAI WebSocket closed`);
      openaiConnected = false;
    };

    openaiWs.onerror = (err) => {
      console.error(`[${sessionState.callId}] ❌ OpenAI WebSocket error:`, err);
    };
  };

  // --- Send Session Update ---
  const sendSessionUpdate = (sessionState: SessionState) => {
    // Language instruction based on setting
    const languageNames: Record<string, string> = {
      en: "English",
      nl: "Dutch",
      es: "Spanish",
      fr: "French",
      de: "German",
      it: "Italian",
      pt: "Portuguese",
      pl: "Polish",
      ro: "Romanian",
      ar: "Arabic",
      hi: "Hindi",
      zh: "Chinese",
      ja: "Japanese",
      ko: "Korean",
      tr: "Turkish",
      ru: "Russian",
      el: "Greek",
      cs: "Czech",
      hu: "Hungarian",
      sv: "Swedish",
      no: "Norwegian",
      da: "Danish",
      fi: "Finnish",
    };

    const languageName = languageNames[sessionState.language] ?? sessionState.language;

    const languageInstruction = sessionState.language === "auto"
      ? "Respond in the same language the caller speaks. If they speak Spanish, respond in Spanish. If they speak French, respond in French. Match their language naturally."
      : `Respond ONLY in ${languageName} (code: ${sessionState.language}). Translate ALL fixed phrases in this prompt (greetings, questions, \"Let me check your booking\", \"Safe travels!\") into ${languageName}.`;
    
    let prompt = SYSTEM_PROMPT
      .replace(/\{\{agent_name\}\}/g, sessionState.agentName)
      .replace(/\{\{company_name\}\}/g, sessionState.companyName)
      .replace(/\{\{language_instruction\}\}/g, languageInstruction);

    if (!DEMO_SIMPLE_MODE && sessionState.customerName) {
      if (sessionState.hasActiveBooking) {
        // Include full active booking details so Ada doesn't hallucinate
        let bookingContext = `Caller is ${sessionState.customerName} with an ACTIVE BOOKING.`;
        if (sessionState.booking.pickup) {
          bookingContext += `\n- Pickup: "${sessionState.booking.pickup}"`;
        }
        if (sessionState.booking.destination) {
          bookingContext += `\n- Destination: "${sessionState.booking.destination}"`;
        }
        if (sessionState.booking.passengers) {
          bookingContext += `\n- Passengers: ${sessionState.booking.passengers}`;
        }
        bookingContext += `\n\nIMPORTANT RULES (ACTIVE BOOKING):`;
        bookingContext += `\n- Ask ONLY: "Do you want to keep it, change it, or cancel it?"`;
        bookingContext += `\n- Do NOT suggest any new pickup/destination yourself.`;
        bookingContext += `\n- If caller says a DIFFERENT address, treat it as a change request but DO NOT restate the old booking. Wait for the system's modification prompt ("Updated. From A to B. Happy with that?") and follow it exactly.`;
        bookingContext += `\n- Do NOT make up or guess addresses.`;
        prompt += `\n\nCURRENT BOOKING:\n${bookingContext}`;
      } else {
        // Include last trip details so Ada doesn't hallucinate
        let historyContext = `Caller is ${sessionState.customerName} (returning, ${sessionState.callerTotalBookings || 0} previous bookings).`;
        if (sessionState.callerLastPickup && sessionState.callerLastDestination) {
          historyContext += ` Their last trip was from "${sessionState.callerLastPickup}" to "${sessionState.callerLastDestination}".`;
        } else if (sessionState.callerLastPickup) {
          historyContext += ` Their last pickup was "${sessionState.callerLastPickup}".`;
        }
        prompt += `\n\nCURRENT CONTEXT: ${historyContext} Ask where they want to go today - do NOT assume they want the same trip.`;
      }
    }

    // === INJECT CURRENT BOOKING STATE (CRITICAL - prevents re-asking answered questions) ===
    // This tells Ada exactly what has been captured so she doesn't jump back
    const bookingState = sessionState.booking;
    const capturedFields: string[] = [];
    const missingFields: string[] = [];
    
    if (bookingState.pickup && bookingState.pickup.length > 2) {
      capturedFields.push(`✅ Pickup: "${bookingState.pickup}"`);
    } else {
      missingFields.push("Pickup");
    }
    
    if (bookingState.destination && bookingState.destination.length > 2) {
      capturedFields.push(`✅ Destination: "${bookingState.destination}"`);
    } else {
      missingFields.push("Destination");
    }
    
    if (bookingState.passengers !== null && bookingState.passengers > 0) {
      capturedFields.push(`✅ Passengers: ${bookingState.passengers}`);
    } else {
      missingFields.push("Passengers");
    }
    
    if (bookingState.pickup_time !== null) {
      capturedFields.push(`✅ Time: ${bookingState.pickup_time}`);
    } else {
      missingFields.push("Time");
    }
    
    // Only inject if we have some state
    if (capturedFields.length > 0 || sessionState.bookingStep !== "pickup") {
      prompt += `\n\n# 🔒 CURRENT BOOKING STATE (DO NOT RE-ASK THESE)
${capturedFields.join("\n")}
${missingFields.length > 0 ? `\n❓ Still needed: ${missingFields.join(", ")}` : "\n✅ ALL FIELDS CAPTURED - proceed to summary!"}

🚨 CRITICAL: You MUST NOT ask for fields marked with ✅ - they are already captured.
Current step: ${sessionState.bookingStep.toUpperCase()}
${sessionState.bookingStep === "summary" ? "→ Deliver the booking summary now." : `→ Ask ONLY for: ${missingFields[0] || "confirmation"}`}`;
    }

    // Inject recent transcripts so Ada only responds to what was actually said
    if (sessionState.transcripts && sessionState.transcripts.length > 0) {
      const recentTranscripts = sessionState.transcripts.slice(-5); // Last 5 turns
      const transcriptSummary = recentTranscripts
        .map((t: { role: string; text: string }) => `${t.role}: "${t.text}"`)
        .join("\n");
      prompt += `\n\nRECENT CONVERSATION (respond ONLY based on this - do NOT invent what was said):\n${transcriptSummary}`;
    }

    const sessionUpdate = {
      type: "session.update",
      session: {
        modalities: ["text", "audio"],
        instructions: prompt,
        voice: sessionState.voice,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        input_audio_transcription: { 
          model: "whisper-1",
          // Auto-detect language if not specified or set to "auto"
          ...(sessionState.language && sessionState.language !== "auto" ? { language: sessionState.language } : {}),
          // Prompt hint helps Whisper recognize place names and taxi terminology
          prompt: "Taxi booking. Street numbers, addresses, passenger count, pickup location, destination."
        },
        turn_detection: {
          type: "server_vad",
          // PHONE LINE ULTRA-SENSITIVE:
          // - Threshold 0.3: Very sensitive for quiet "yes please" on phone lines
          // - Prefix 600ms: Extra capture of word beginnings for short utterances
          // - Silence 1800ms: Waits even longer for complete phrases, critical for confirmations
          threshold: sessionState.useRasaAudioProcessing ? 0.5 : 0.3,
          prefix_padding_ms: sessionState.useRasaAudioProcessing ? 400 : 600,
          silence_duration_ms: sessionState.useRasaAudioProcessing ? 1200 : 1800,
        },
        temperature: 0.6, // OpenAI Realtime API minimum is 0.6
        tools: TOOLS,
        tool_choice: "auto"
      }
    };

    openaiWs?.send(JSON.stringify(sessionUpdate));

    // ═══════════════════════════════════════════════════════════════════
    // GREETING / RESUME HANDLING
    // If greetingDelivered=true (from session restoration), skip the greeting
    // and inject a context-aware resume prompt instead
    // ═══════════════════════════════════════════════════════════════════
    if (sessionState.greetingDelivered) {
      // SESSION RESUMED - inject context prompt instead of greeting
      console.log(`[${sessionState.callId}] 🔄 SESSION RESUMED - skipping greeting, injecting context`);
      
      // Build resume prompt based on current booking step
      let resumeInstruction = "";
      let resumeText = "";
      const { pickup, destination, passengers } = sessionState.booking;
      const pendingQuote = sessionState.pendingQuote;
      
      // ✅ CRITICAL: Check if we have a pending fare quote - if so, resume from fare confirmation
      // SILENT RESUME: If fare exists AND status is awaiting_confirmation, this is a dispatch-triggered
      // reconnect where Ada was mid-speech with the fare quote. DON'T speak - just wait for user input.
      if (pendingQuote?.fare && pendingQuote?.eta) {
        // Check if this is a very recent reconnect (within 10s of quote being set)
        // If so, assume the fare was already spoken (or being spoken) and just wait silently
        const quoteFreshness = Date.now() - (pendingQuote.timestamp || 0);
        const isSilentResume = quoteFreshness < 10000; // Within 10 seconds of quote delivery
        
        if (isSilentResume) {
          // === SILENT RESUME: Fare was just delivered, just wait for user's yes/no ===
          console.log(`[${sessionState.callId}] 🔇 SILENT RESUME: Fare quote just delivered ${quoteFreshness}ms ago - waiting for user response`);
          
          // Inject context but don't trigger Ada to speak
          const fareText = pendingQuote.fare.toString().startsWith("£") ? pendingQuote.fare : `£${pendingQuote.fare}`;
          const etaText = pendingQuote.eta.toString().includes("minute") ? pendingQuote.eta : `${pendingQuote.eta} minutes`;
          
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: `[SESSION RESUMED - SILENT MODE] You just quoted the fare: ${fareText}, driver will be ${etaText}. Wait silently for user to say "yes" or "no". Do NOT speak until they respond.` }]
            }
          }));
          
          // DON'T trigger response.create - just wait silently for user VAD input
          console.log(`[${sessionState.callId}] 📝 Silent resume context injected - awaiting user response`);
          
          // === PERIODIC COMMIT FOR SILENT RESUME ===
          // Since we're in silent mode (no Ada speech), VAD may miss short "yes" responses.
          // Schedule periodic forced commits to ensure transcription happens.
          console.log(`[${sessionState.callId}] ⏰ SILENT RESUME: Scheduling periodic commits to catch confirmation response`);
          
          const SILENCE_SAMPLE_RATE = 24000;
          const SILENCE_PADDING_MS = 250;
          const SILENCE_SAMPLES = Math.floor(SILENCE_SAMPLE_RATE * SILENCE_PADDING_MS / 1000);
          const silenceBuffer = new Int16Array(SILENCE_SAMPLES);
          const silenceBytes = new Uint8Array(silenceBuffer.buffer);
          let silenceBinary = "";
          for (let i = 0; i < silenceBytes.length; i++) {
            silenceBinary += String.fromCharCode(silenceBytes[i]);
          }
          const silenceBase64 = btoa(silenceBinary);
          
          // Schedule commits at 2s, 4s, 6s, 8s to catch "yes please" after reconnection
          const commitDelays = [2000, 4000, 6000, 8000];
          commitDelays.forEach((delay) => {
            setTimeout(() => {
              if (openaiWs && openaiConnected && !sessionState.callEnded && !isConnectionClosed) {
                try {
                  console.log(`[${sessionState.callId}] 📤 SILENT RESUME COMMIT: Forcing transcription check (${delay}ms after resume)`);
                  openaiWs.send(JSON.stringify({
                    type: "input_audio_buffer.append",
                    audio: silenceBase64
                  }));
                  openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
                } catch (e) {
                  // Connection may have closed
                }
              }
            }, delay);
          });
          
          return;
        }
        
        // Normal fare resume (older quote, maybe connection dropped earlier)
        const fareText = pendingQuote.fare.toString().startsWith("£") ? pendingQuote.fare : `£${pendingQuote.fare}`;
        const etaText = pendingQuote.eta.toString().includes("minute") ? pendingQuote.eta : `${pendingQuote.eta} minutes`;
        
        resumeInstruction = `CRITICAL: A fare quote was already given. Resume from fare confirmation. The price is ${fareText} and driver ETA is ${etaText}. Ask if they want to book.`;
        resumeText = `Apologies for the brief interruption. So, the price is ${fareText} and your driver will be ${etaText}. Would you like me to book that for you?`;
        
        console.log(`[${sessionState.callId}] 💰 Resuming from fare confirmation: fare=${fareText}, eta=${etaText}`);
      } else if (sessionState.bookingStep === "confirmed") {
        resumeInstruction = "The booking has already been confirmed. Ask if there's anything else you can help with.";
        resumeText = "Apologies for the brief interruption. Your booking is confirmed. Is there anything else I can help you with?";
      } else if (sessionState.bookingStep === "summary") {
        // ═══════════════════════════════════════════════════════════════════════════
        // SILENT RECONNECT AT SUMMARY: User was already asked "Is that correct?"
        // Instead of repeating the full summary, just verify details briefly
        // ═══════════════════════════════════════════════════════════════════════════
        if (pickup && destination && passengers) {
          // Check if confirmation was recently asked (within 60s)
          const confirmationRecentlyAsked = sessionState.confirmationAskedAt && 
            (Date.now() - sessionState.confirmationAskedAt < 60000);
          
          if (confirmationRecentlyAsked) {
            // User already heard the full summary - just verify quickly
            console.log(`[${sessionState.callId}] 🔇 SILENT RECONNECT at confirmation - user already heard summary`);
            resumeInstruction = `CRITICAL: The booking summary was JUST given. Pickup: "${pickup}", Destination: "${destination}", Passengers: ${passengers}. User was asked "Is that correct?". Do NOT repeat the full summary - just verify quickly and wait for yes/no.`;
            resumeText = `Sorry about that brief pause. Just to confirm - from ${pickup} to ${destination}. Is that correct?`;
          } else {
            // Confirmation wasn't recently asked - give the full summary
            resumeInstruction = `Continue with the booking summary. Pickup: "${pickup}", Destination: "${destination}", Passengers: ${passengers}. Ask if the details are correct.`;
            resumeText = `Apologies for the brief interruption. So, you're going from ${pickup} to ${destination} with ${passengers} passenger${passengers === 1 ? '' : 's'}. Is that correct?`;
          }
        } else {
          // Missing data - fall back to asking for it
          console.log(`[${sessionState.callId}] ⚠️ Summary step but missing data - falling back to collect missing fields`);
          if (!pickup) {
            resumeInstruction = "Continue helping the caller book a taxi. Ask where they would like to be picked up.";
            resumeText = `Sorry about that. Where would you like to be picked up?`;
          } else if (!destination) {
            resumeInstruction = `Continue where you left off. You have pickup="${pickup}". Ask for the destination.`;
            resumeText = `Sorry about that brief pause. Your pickup is ${pickup}. Where would you like to go?`;
          } else {
            resumeInstruction = `Continue where you left off. You have pickup="${pickup}" and destination="${destination}". Ask how many passengers.`;
            resumeText = `Sorry about that brief pause. You're going from ${pickup} to ${destination}. How many passengers will there be?`;
          }
        }
      } else if (sessionState.bookingStep === "time") {
        // Validate we have pickup, destination, passengers for this step
        if (pickup && destination && passengers) {
          resumeInstruction = `Continue where you left off. You have pickup="${pickup}" and destination="${destination}" for ${passengers} passengers. Ask when they need the taxi.`;
          resumeText = `Apologies for the brief interruption. So, you're going from ${pickup} to ${destination} with ${passengers} passenger${passengers === 1 ? '' : 's'}. When do you need the taxi?`;
        } else if (pickup && destination) {
          // Missing passengers - ask for it
          resumeInstruction = `Continue where you left off. You have pickup="${pickup}" and destination="${destination}". Ask how many passengers.`;
          resumeText = `Sorry about that brief pause. You're going from ${pickup} to ${destination}. How many passengers will there be?`;
        } else if (pickup) {
          resumeInstruction = `Continue where you left off. You have pickup="${pickup}". Ask for the destination.`;
          resumeText = `Sorry about that brief pause. Your pickup is ${pickup}. Where would you like to go?`;
        } else {
          resumeInstruction = "Continue helping the caller book a taxi. Ask where they would like to be picked up.";
          resumeText = `Sorry about that. Where would you like to be picked up?`;
        }
      } else if (sessionState.bookingStep === "passengers") {
        if (pickup && destination) {
          resumeInstruction = `Continue where you left off. You have pickup="${pickup}" and destination="${destination}". Ask how many passengers.`;
          resumeText = `Sorry about that brief pause. You're going from ${pickup} to ${destination}. How many passengers will there be?`;
        } else if (pickup) {
          resumeInstruction = `Continue where you left off. You have pickup="${pickup}". Ask for the destination.`;
          resumeText = `Sorry about that brief pause. Your pickup is ${pickup}. Where would you like to go?`;
        } else {
          resumeInstruction = "Continue helping the caller book a taxi. Ask where they would like to be picked up.";
          resumeText = `Sorry about that. Where would you like to be picked up?`;
        }
      } else if (sessionState.bookingStep === "destination") {
        if (pickup) {
          resumeInstruction = `Continue where you left off. You have pickup="${pickup}". Ask for the destination.`;
          resumeText = `Sorry about that brief pause. Your pickup is ${pickup}. Where would you like to go?`;
        } else {
          resumeInstruction = "Continue helping the caller book a taxi. Ask where they would like to be picked up.";
          resumeText = `Sorry about that. Where would you like to be picked up?`;
        }
      } else {
        resumeInstruction = "Continue helping the caller book a taxi. Ask where they would like to be picked up.";
        resumeText = `Sorry about that. Where would you like to be picked up?`;
      }
      
      // Inject resume context
      openaiWs?.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "message",
          role: "user",
          content: [{ type: "input_text", text: `[SESSION RESUMED - DO NOT GREET AGAIN] ${resumeInstruction}` }]
        }
      }));
      
      openaiWs?.send(JSON.stringify({
        type: "response.create",
        response: {
          modalities: ["text", "audio"],
          instructions: `Say this EXACTLY (brief reconnection acknowledgment): "${resumeText}" - STOP after asking. Do NOT re-introduce yourself.`
        }
      }));
      
      console.log(`[${sessionState.callId}] 📝 Resume prompt injected (step: ${sessionState.bookingStep}, hasFare: ${!!pendingQuote?.fare})`);
      return;
    }
    
    // === NORMAL GREETING (first connection) ===
    // Inject the exact welcome greeting as a conversation item to force OpenAI to speak it
    // This prevents OpenAI from skipping or paraphrasing the greeting
    const greetingText = sessionState.hasActiveBooking
      ? `Hi ${sessionState.customerName || "there"}, welcome back! I can see you have a booking from ${sessionState.booking.pickup || "your pickup"} to ${sessionState.booking.destination || "your destination"}. Would you like to keep it, change it, or cancel it?`
      : `Hello, and welcome to the Taxibot demo! I'm ${sessionState.agentName || "Ada"}, your taxi booking assistant. I'm here to make booking a taxi quick and easy. Where would you like to be picked up?`;

    openaiWs?.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: "[SYSTEM: Call connected. Say your greeting now.]" }]
      }
    }));

    openaiWs?.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["text", "audio"],
        instructions: `Say this EXACTLY (do not change, shorten, or ADD to it): "${greetingText}" - STOP IMMEDIATELY after the question mark. Do NOT add any more questions. Do NOT continue speaking. Wait for the user's response.`
      }
    }));
    console.log(`[${sessionState.callId}] 📝 Session updated + greeting triggered`);
  };

  // Fire-and-forget DB flush - merges dispatch entries to prevent overwrites
  const flushTranscriptsToDb = (sessionState: SessionState) => {
    // Clone transcripts to avoid mutation issues
    const localTranscripts = [...sessionState.transcripts];
    const callId = sessionState.callId;
    
    // Fire and forget - do NOT await, but merge dispatch entries first
    supabase
      .from("live_calls")
      .select("transcripts")
      .eq("call_id", callId)
      .single()
      .then(({ data, error: fetchError }) => {
        if (fetchError) {
          console.error(`[${callId}] DB fetch for merge failed:`, fetchError);
          // Fall back to overwrite if fetch fails
          return supabase
            .from("live_calls")
            .update({ transcripts: localTranscripts, updated_at: new Date().toISOString() })
            .eq("call_id", callId);
        }
        
        const dbTranscripts = (data?.transcripts as any[]) || [];
        
        // Find dispatch entries in DB that aren't in our local state (by timestamp + role)
        const dispatchRoles = ["dispatch", "dispatch_confirm", "dispatch_ask_confirm", "dispatch_say"];
        const localTimestamps = new Set(localTranscripts.map((t: any) => `${t.role}:${t.timestamp}`));
        
        const missingDispatchEntries = dbTranscripts.filter((t: any) => 
          dispatchRoles.includes(t.role) && !localTimestamps.has(`${t.role}:${t.timestamp}`)
        );
        
        if (missingDispatchEntries.length > 0) {
          console.log(`[${callId}] 🔀 Merging ${missingDispatchEntries.length} dispatch entries from DB`);
          // Add missing dispatch entries to local state so they persist
          sessionState.transcripts.push(...missingDispatchEntries);
        }
        
        // Merge: local transcripts + any dispatch entries we didn't have
        const mergedTranscripts = [...localTranscripts, ...missingDispatchEntries];
        
        // Sort by timestamp to maintain chronological order
        mergedTranscripts.sort((a: any, b: any) => 
          new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
        );
        
        // CRITICAL: Also persist booking state so session restoration works correctly
        // This ensures pickup/destination/passengers are available if disconnect happens before book_taxi call
        const updatePayload: Record<string, any> = {
          transcripts: mergedTranscripts,
          updated_at: new Date().toISOString()
        };
        
        // Helper for address comparison
        const normalizeForCompare = (s: string | null) => (s || "").toLowerCase().replace(/[.,\s]+/g, " ").trim();
        
        // Only include booking fields if they have values (don't overwrite with nulls)
        if (sessionState.booking.pickup) {
          updatePayload.pickup = sessionState.booking.pickup;
        }
        if (sessionState.booking.destination) {
          // === DUPLICATE ADDRESS GUARD ===
          // Prevent saving destination that's identical to pickup
          const normalizedPickup = normalizeForCompare(sessionState.booking.pickup);
          const normalizedDest = normalizeForCompare(sessionState.booking.destination);
          
          if (normalizedPickup && normalizedDest && normalizedPickup === normalizedDest) {
            console.log(`[${callId}] ⚠️ DB FLUSH: Skipping destination - identical to pickup`);
            // Don't include destination in update
          } else {
            updatePayload.destination = sessionState.booking.destination;
          }
        }
        if (sessionState.booking.passengers !== null && sessionState.booking.passengers > 0) {
          updatePayload.passengers = sessionState.booking.passengers;
        }
        
        // ═══════════════════════════════════════════════════════════════════════════
        // CRITICAL: Save bookingStep and pickup_time for accurate session restoration
        // ═══════════════════════════════════════════════════════════════════════════
        updatePayload.booking_step = sessionState.bookingStep;
        if (sessionState.booking.pickup_time) {
          updatePayload.pickup_time = sessionState.booking.pickup_time;
        }
        if (sessionState.lastQuestionType) {
          updatePayload.last_question_type = sessionState.lastQuestionType;
        }
        
        // ═══════════════════════════════════════════════════════════════════════════
        // CRITICAL: Save confirmationAskedAt for silent reconnect during summary phase
        // This allows resume logic to know if user already heard "Is that correct?"
        // ═══════════════════════════════════════════════════════════════════════════
        if (sessionState.confirmationAskedAt) {
          updatePayload.confirmation_asked_at = new Date(sessionState.confirmationAskedAt).toISOString();
        }
        
        // ═══════════════════════════════════════════════════════════════════════════
        // CRITICAL: Save fare/eta for dispatch-triggered handoff resumption
        // This ensures the reconnected session knows a fare was already delivered
        // ═══════════════════════════════════════════════════════════════════════════
        if (sessionState.pendingQuote?.fare) {
          updatePayload.fare = sessionState.pendingQuote.fare;
        }
        if (sessionState.pendingQuote?.eta) {
          updatePayload.eta = sessionState.pendingQuote.eta;
        }
        // Set status to awaiting_confirmation when fare quote is pending
        if (sessionState.pendingQuote?.fare && !sessionState.bookingConfirmedThisTurn) {
          updatePayload.status = "awaiting_confirmation";
        }
        
        return supabase
          .from("live_calls")
          .update(updatePayload)
          .eq("call_id", callId);
      })
      .then((result: any) => {
        if (result?.error) console.error(`[${callId}] DB flush error:`, result.error);
      });
  };

  // Faster batching for better session restoration (2 seconds for aggressive persistence)
  const FLUSH_INTERVAL_MS = 2000;
  
  const scheduleTranscriptFlush = (sessionState: SessionState) => {
    if (sessionState.transcriptFlushTimer) return; // Already scheduled
    sessionState.transcriptFlushTimer = setTimeout(() => {
      sessionState.transcriptFlushTimer = null;
      flushTranscriptsToDb(sessionState);
    }, FLUSH_INTERVAL_MS) as unknown as number;
  };

  // Immediate flush for critical events (call end, booking)
  const immediateFlush = (sessionState: SessionState) => {
    if (sessionState.transcriptFlushTimer) {
      clearTimeout(sessionState.transcriptFlushTimer);
      sessionState.transcriptFlushTimer = null;
    }
    flushTranscriptsToDb(sessionState);
  };

  // Helper: avoid conversation_already_has_active_response by deferring response.create
  const safeResponseCreate = (sessionState: SessionState, reason?: string) => {
    if (!openaiWs || !openaiConnected) return;
    if (sessionState.callEnded) return;

    if (sessionState.openAiResponseActive) {
      sessionState.deferredResponseCreate = true;
      console.log(
        `[${sessionState.callId}] ⏳ Deferring response.create` + (reason ? ` (${reason})` : "")
      );
      return;
    }

    openaiWs.send(JSON.stringify({ type: "response.create" }));
  };

  // Helper: safely cancel response only if one is active (prevents response_cancel_not_active errors)
  const safeCancel = (sessionState: SessionState, reason?: string) => {
    if (!openaiWs || !openaiConnected) return;
    if (!sessionState.openAiResponseActive) {
      console.log(`[${sessionState.callId}] ⏭️ Skipping cancel - no active response` + (reason ? ` (${reason})` : ""));
      return;
    }
    console.log(`[${sessionState.callId}] 🛑 Cancelling response` + (reason ? ` (${reason})` : ""));
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    // IMPORTANT: Do NOT set openAiResponseActive=false here.
    // OpenAI can continue streaming events briefly after cancel; clearing the flag early can cause us to
    // send a new response.create while a response is still active -> conversation_already_has_active_response
    // which manifests as TTS being cut off mid-sentence.
    sessionState.discardCurrentResponseAudio = true;
  };

  // --- Handle OpenAI Messages ---
  const handleOpenAIMessage = (message: any, sessionState: SessionState) => {
    // Log all message types for debugging
    if (!["response.audio.delta", "response.audio_transcript.delta"].includes(message.type)) {
      console.log(`[${sessionState.callId}] 📨 OpenAI event: ${message.type}`);
    }
    
    switch (message.type) {
      case "session.created":
        console.log(`[${sessionState.callId}] 🎉 Session created`);
        // Greeting is now triggered in sendSessionUpdate() for faster response
        break;

      case "response.created":
        // Start-of-response marker (used to avoid response.cancel_not_active)
        sessionState.openAiResponseActive = true;

        // ✅ EXTRACTION IN PROGRESS GUARD: If AI extraction is running, cancel this response
        // We'll trigger the correct response once extraction completes
        // EXCEPTION: Allow final goodbye response through even if extraction is running
        if (sessionState.extractionInProgress && !sessionState.finalGoodbyePending) {
          safeCancel(sessionState, "extraction in progress");
          break;
        }

        // ✅ STRICT TURN-BASED GUARD: If Ada has asked a data-collection question and is
        // waiting for a user answer, block ALL VAD-triggered responses until a user transcript arrives.
        // This prevents Ada from advancing to the next question before the user speaks.
        // Maximum wait time: 30 seconds (after which we allow timeout handling)
        const TURN_BASED_TIMEOUT_MS = 30000;
        if (sessionState.awaitingUserAnswer && sessionState.awaitingUserAnswerSince) {
          // Allow one forced/system reprompt (prevents infinite cancel loops).
          if (sessionState.allowOneResponseWhileAwaitingUserAnswer) {
            console.log(`[${sessionState.callId}] ✅ TURN-BASED BYPASS: allowing one response while awaiting ${sessionState.awaitingAnswerForStep}`);
            sessionState.allowOneResponseWhileAwaitingUserAnswer = false;
          } else {
          const waitTime = Date.now() - sessionState.awaitingUserAnswerSince;
          
          // Allow response if timeout exceeded (user may have hung up or there's an issue)
          if (waitTime < TURN_BASED_TIMEOUT_MS) {
            console.log(`[${sessionState.callId}] ⏳ TURN-BASED BLOCK: Awaiting user answer for ${sessionState.awaitingAnswerForStep?.toUpperCase()} (${(waitTime / 1000).toFixed(1)}s)`);
            // IMPORTANT: This response was VAD-triggered before we had the finalized user transcript.
            // Cancel it, then explicitly re-trigger once the transcript arrives.
            sessionState.pendingTurnResponseCreate = true;
            safeCancel(sessionState, `turn-based: awaiting user answer for ${sessionState.awaitingAnswerForStep}`);
            break;
          } else {
            console.log(`[${sessionState.callId}] ⏰ TURN-BASED TIMEOUT: No user response after ${(waitTime / 1000).toFixed(1)}s - allowing response`);
            sessionState.awaitingUserAnswer = false;
            sessionState.awaitingUserAnswerSince = null;
            sessionState.awaitingAnswerForStep = null;
          }
          }
        }

        // NOTE: Greeting guard removed - was too aggressive and blocked legitimate responses.
        // The "STOP IMMEDIATELY" instruction in the greeting + multi-question guard handles this instead.

        // ✅ PENDING MODIFICATION GUARD: While waiting for the caller to confirm an update,
        // do NOT allow VAD/noise to trigger extra assistant turns (this causes repeated summaries).
        // We allow exactly ONE response right after we inject the modification prompt.
        if (sessionState.pendingModification) {
          if (sessionState.modificationPromptPending) {
            sessionState.modificationPromptPending = false;
          } else {
            safeCancel(sessionState, "awaiting modification confirmation");
            break;
          }
        }
        
        // ✅ AWAITING DISPATCH GUARD: Block VAD responses while waiting for dispatch fare callback
        // This prevents Ada from hallucinating fare amounts before dispatch responds
        if (sessionState.awaitingDispatchCallback) {
          safeCancel(sessionState, "awaiting dispatch callback");
          break;
        }

        // ✅ "ANYTHING ELSE" GUARD: After Ada asks "Is there anything else I can help with?",
        // block VAD-triggered responses during the grace period to let the user respond.
        // This prevents Ada from hallucinating cancellations or other statements.
        // BUT: Allow the confirmation response itself through (confirmationResponsePending flag)
        if (sessionState.askedAnythingElse && sessionState.askedAnythingElseAt && !sessionState.confirmationResponsePending) {
          const msSinceAsked = Date.now() - sessionState.askedAnythingElseAt;
          const waitPeriodMs = sessionState.goodbyeGraceMs || 3000;
          
          // Only block during the grace period - after that, Ada can respond naturally
          if (msSinceAsked < waitPeriodMs) {
            safeCancel(sessionState, `waiting for user response to "anything else?" (${msSinceAsked}ms / ${waitPeriodMs}ms)`);
            break;
          }
        }

        // ✅ QUESTION RESPONSE COOLDOWN GUARD: After Ada asks ANY question during data collection,
        // block VAD-triggered responses for 8 seconds to give users time to answer.
        // This prevents background noise/VAD false-positives from causing Ada to barge ahead with follow-up questions.
        // Skip this guard during summary/confirmed phase where quick responses are expected.
        const QUESTION_COOLDOWN_MS = 8000;
        const isAtDataCollectionStep = !["summary", "confirmed"].includes(sessionState.bookingStep || "");
        if (
          sessionState.adaAskedQuestionAt &&
          isAtDataCollectionStep &&
          sessionState.greetingDelivered  // Only apply after greeting has been delivered
        ) {
          const msSinceQuestion = Date.now() - sessionState.adaAskedQuestionAt;
          if (msSinceQuestion < QUESTION_COOLDOWN_MS) {
            // Check if user actually spoke (speechStartTime was set after the question)
            // ALSO: Ignore speech that happened during greeting protection window (likely noise)
            const now = Date.now();
            const isStillInGreetingProtection = now < sessionState.greetingProtectionUntil;
            const speechAfterGreetingProtection = 
              sessionState.speechStartTime && 
              sessionState.speechStartTime > (sessionState.greetingProtectionUntil - GREETING_PROTECTION_MS);
            const userRespondedAfterQuestion = 
              sessionState.speechStartTime && 
              sessionState.adaAskedQuestionAt < sessionState.speechStartTime &&
              !isStillInGreetingProtection && // Don't count speech during greeting protection
              speechAfterGreetingProtection;
            
            // FIX: Don't cancel if we already have a recent user transcript (OpenAI may skip speech_started on quick replies)
            // This prevents Ada from going silent after user says "three people" and OpenAI's VAD doesn't emit proper events
            const hasRecentTranscript = sessionState.lastUserTranscriptAt && 
              (now - sessionState.lastUserTranscriptAt) < 5000;
            
            if (!userRespondedAfterQuestion && !hasRecentTranscript) {
              safeCancel(sessionState, `waiting for user answer (${msSinceQuestion}ms / ${QUESTION_COOLDOWN_MS}ms, greetProt=${isStillInGreetingProtection})`);
              break;
            }
          }
        }
        
        // ✅ MISSING STEP DATA GUARD: Prevent Ada from advancing if current step's field is empty.
        // This catches cases where VAD triggered a response but no user speech was detected,
        // or when the user's answer wasn't transcribed. Forces Ada to re-ask the current question.
        const currentStep = sessionState.bookingStep;
        const isDataStep = ["pickup", "destination", "passengers", "time"].includes(currentStep || "");
        
        if (isDataStep && sessionState.greetingDelivered) {
          const stepFieldEmpty = (
            (currentStep === "pickup" && !sessionState.booking.pickup) ||
            (currentStep === "destination" && !sessionState.booking.destination) ||
            (currentStep === "passengers" && sessionState.booking.passengers === null) ||
            (currentStep === "time" && !sessionState.booking.pickup_time)
          );
          
          if (stepFieldEmpty) {
            console.log(`[${sessionState.callId}] 🛡️ MISSING STEP DATA GUARD: ${currentStep} field is empty - injecting reprompt`);
            
            // Cancel the current response (which would advance to next question)
            safeCancel(sessionState, `step ${currentStep} incomplete`);
            
            // Inject a reprompt for the current step
            const repromptMap: Record<string, string> = {
              pickup: "Sorry, I didn't catch that. Where would you like to be picked up?",
              destination: "Sorry, I didn't catch that. Where would you like to go?",
              passengers: "Sorry, I didn't catch that. How many people will be travelling?",
              time: "Sorry, I didn't catch that. When do you need the taxi?"
            };
            
            const repromptText = repromptMap[currentStep] || "Sorry, I didn't catch that. Could you please repeat?";
            
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: `[SYSTEM: User's response was not detected. Ask ONLY: "${repromptText}" - NOTHING ELSE. Do not advance to the next question.]`
                }]
              }
            }));
            
            // Re-engage the turn-based lock for this step
            sessionState.awaitingUserAnswer = true;
            sessionState.awaitingUserAnswerSince = Date.now();
            sessionState.awaitingAnswerForStep = currentStep as any;

            // Allow the reprompt response through the turn-based guard.
            sessionState.allowOneResponseWhileAwaitingUserAnswer = true;
            
            // Trigger the reprompt (safely; avoids conversation_already_has_active_response)
            safeResponseCreate(sessionState, "missing-step-reprompt");
            break;
          }
        }
        
        // Clear confirmationResponsePending after allowing one response through
        if (sessionState.confirmationResponsePending) {
          console.log(`[${sessionState.callId}] ✅ Allowing confirmation response through guard`);
          sessionState.confirmationResponsePending = false;
        }

        // ✅ POST-GOODBYE GUARD: If call is ending, cancel any new response immediately
        // EXCEPT for the single final goodbye response we intentionally trigger.
        if (sessionState.callEnded) {
          if (sessionState.finalGoodbyePending) {
            // This is the goodbye we just initiated; allow it to play.
            sessionState.finalGoodbyePending = false;
          } else {
            safeCancel(sessionState, "callEnded - post-goodbye guard");
          }
        }
        break;

       case "response.done":
         sessionState.openAiResponseActive = false;
         sessionState.discardCurrentResponseAudio = false;

         // ✅ GREETING COMPLETION: Mark greeting as delivered on first response.done
         // This unlocks follow-up responses and prevents the model from firing all questions at once
         // CRITICAL: Reset greetingProtectionUntil to start from NOW (when greeting actually ends)
         // This ensures the 12s protection window covers AFTER the greeting, not from connection time
         if (!sessionState.greetingDelivered) {
           sessionState.greetingDelivered = true;
           sessionState.greetingProtectionUntil = Date.now() + GREETING_PROTECTION_MS;
           console.log(`[${sessionState.callId}] 🎉 Greeting delivered - protection window reset for ${GREETING_PROTECTION_MS}ms`);
         }

         // ✅ If we just finished speaking the confirmation message, NOW set the askedAnythingElseAt timestamp
         // This ensures the grace period starts AFTER Ada finishes speaking, not when we sent the response.create
         if (sessionState.askedAnythingElse && !sessionState.askedAnythingElseAt) {
           sessionState.askedAnythingElseAt = Date.now();
           console.log(`[${sessionState.callId}] ⏱️ Started "anything else" grace period (${sessionState.goodbyeGraceMs || 3000}ms)`);
         }

         // If we tried to speak while a response was in-flight, do it now.
         if (sessionState.deferredResponseCreate && !sessionState.callEnded) {
           sessionState.deferredResponseCreate = false;
           safeResponseCreate(sessionState, "deferred-after-response.done");
         }
         
         // ✅ IMMEDIATE HANGUP ON GOODBYE COMPLETE: If we're in closing grace period,
         // the goodbye has finished playing - trigger hangup immediately instead of waiting for timeout
         if (closingGracePeriodActive && sessionState.finalGoodbyePending) {
           console.log(`[${sessionState.callId}] 👋 Goodbye audio finished (response.done) - triggering immediate hangup`);
           
           // Clear the timeout since we're hanging up now
           if (closingGraceTimeoutId) {
             clearTimeout(closingGraceTimeoutId);
             closingGraceTimeoutId = null;
           }
           
           closingGracePeriodActive = false;
           sessionState.finalGoodbyePending = false;
           
           // Stop keep-alives
           try {
             stopKeepAlive();
           } catch {
             // ignore
           }
           
           // Small delay (500ms) to ensure final audio packets are flushed to bridge
           setTimeout(() => {
             // Notify the bridge to hangup
             try {
               socket.send(JSON.stringify({ type: "hangup", reason: "goodbye_complete" }));
               console.log(`[${sessionState.callId}] 📤 Sent hangup to bridge (goodbye complete)`);
             } catch (e) {
               console.warn(`[${sessionState.callId}] ⚠️ Failed to send hangup:`, e);
             }
             
             // Close the bridge WebSocket gracefully
             try {
               isConnectionClosed = true;
               socket.close(1000, "goodbye_complete");
               console.log(`[${sessionState.callId}] 📴 Closed bridge WebSocket (goodbye complete)`);
             } catch (e) {
               console.warn(`[${sessionState.callId}] ⚠️ Failed to close bridge:`, e);
             }
             
             // Close OpenAI WS
             try {
               openaiWs?.close();
             } catch {
               // ignore
             }
           }, 500);
         }
         break;

      case "response.audio.delta": {
        // Mark Ada as speaking (echo guard)
        if (!sessionState.isAdaSpeaking) {
          // First audio delta of this response
          sessionState.isAdaSpeaking = true;
          sessionState.responseStartTime = Date.now();

          // Startup echo is strongest at the beginning of TTS on phone lines.
          // Ignore barge-in checks briefly to avoid self-interruption.
          const ignoreMs = sessionState.useRasaAudioProcessing ? 500 : 250;
          sessionState.bargeInIgnoreUntil = Date.now() + ignoreMs;
        }
        
        // If we cancelled a response, drop any late audio deltas to avoid leaking partial phrases.
        if (sessionState.discardCurrentResponseAudio) {
          sessionState.ttsDiagnostics.chunksDiscarded++;
          break;
        }

        // === TTS DIAGNOSTICS: Compute RMS for outbound audio ===
        // Decode base64 to PCM16 for level measurement
        try {
          const binaryStr = atob(message.delta);
          const audioBytes = new Uint8Array(binaryStr.length);
          for (let i = 0; i < binaryStr.length; i++) {
            audioBytes[i] = binaryStr.charCodeAt(i);
          }
          // PCM16 little-endian
          const pcm16 = new Int16Array(audioBytes.buffer, audioBytes.byteOffset, audioBytes.byteLength / 2);
          
          let sumSq = 0;
          for (let i = 0; i < pcm16.length; i++) {
            const s = pcm16[i];
            sumSq += s * s;
          }
          const ttsRms = Math.sqrt(sumSq / Math.max(1, pcm16.length));
          
          // Track TTS RMS (rolling average of last 50 chunks)
          sessionState.ttsDiagnostics.lastRmsValues.push(ttsRms);
          if (sessionState.ttsDiagnostics.lastRmsValues.length > 50) {
            sessionState.ttsDiagnostics.lastRmsValues.shift();
          }
          sessionState.ttsDiagnostics.avgRms = sessionState.ttsDiagnostics.lastRmsValues.reduce((a, b) => a + b, 0) / sessionState.ttsDiagnostics.lastRmsValues.length;
          if (ttsRms > sessionState.ttsDiagnostics.peakRms) {
            sessionState.ttsDiagnostics.peakRms = ttsRms;
          }
          sessionState.ttsDiagnostics.chunksSent++;
          
          // Log TTS diagnostics every 100 chunks
          if (sessionState.ttsDiagnostics.chunksSent % 100 === 0) {
            console.log(`[${sessionState.callId}] 🔊 TTS: chunks=${sessionState.ttsDiagnostics.chunksSent}, avgRMS=${sessionState.ttsDiagnostics.avgRms.toFixed(0)}, peakRMS=${sessionState.ttsDiagnostics.peakRms.toFixed(0)}`);
          }
        } catch (e) {
          // Silently ignore decode errors - just track as sent
          sessionState.ttsDiagnostics.chunksSent++;
        }

        // === BOOKING CONFIRMATION GUARD ===
        // If booking hasn't been confirmed this turn AND we haven't verified the transcript yet,
        // buffer audio to prevent confirmation phrases from being heard
        if (!sessionState.bookingConfirmedThisTurn && !sessionState.audioVerified) {
          // Buffer audio - will be flushed after transcript verification
          if (!sessionState.pendingAudioBuffer) {
            sessionState.pendingAudioBuffer = [];
          }
          sessionState.pendingAudioBuffer.push(message.delta);
          
          // Safety flush: If we've buffered more than 2 seconds of audio (~50 chunks at 40ms each),
          // release it to prevent long silences (assumes transcript verification failed or was slow)
          if (sessionState.pendingAudioBuffer.length > 50) {
            console.log(`[${sessionState.callId}] ⚠️ Audio buffer safety flush (${sessionState.pendingAudioBuffer.length} chunks)`);
            for (const audioChunk of sessionState.pendingAudioBuffer) {
              sendBinaryAudio(socket, audioChunk);
            }
            sessionState.pendingAudioBuffer = [];
            sessionState.audioVerified = true; // Stop buffering
          }
        } else {
          // Forward audio to bridge immediately as binary (33% smaller, faster)
          sendBinaryAudio(socket, message.delta);
        }
        break;
      }

      case "response.audio.done": {
        // Ada finished speaking - set echo guard window
        // Too-large echo guards clip the first part of the caller's reply (house numbers!),
        // so keep this tight.
        const echoGuardMs = sessionState.useRasaAudioProcessing ? 400 : 250;

        sessionState.isAdaSpeaking = false;
        sessionState.echoGuardUntil = Date.now() + echoGuardMs;
        console.log(`[${sessionState.callId}] 🔇 Echo guard active for ${echoGuardMs}ms`);

        
        // HALF-DUPLEX: Flush buffered audio now that Ada stopped speaking
        if (sessionState.halfDuplex && sessionState.halfDuplexBuffer.length > 0) {
          console.log(`[${sessionState.callId}] 📤 Half-duplex: flushing ${sessionState.halfDuplexBuffer.length} buffered audio chunks`);
          // Process and send each buffered chunk
          for (const audioData of sessionState.halfDuplexBuffer) {
            // Decode µ-law to 16-bit PCM (8kHz)
            const pcm16_8k = new Int16Array(audioData.length);
            for (let i = 0; i < audioData.length; i++) {
              const ulaw = ~audioData[i] & 0xFF;
              const sign = (ulaw & 0x80) ? -1 : 1;
              const exponent = (ulaw >> 4) & 0x07;
              const mantissa = ulaw & 0x0F;
              let sample = ((mantissa << 3) + 0x84) << exponent;
              sample -= 0x84;
              pcm16_8k[i] = sign * sample;
            }
            
            // Upsample to 24kHz
            const pcm16_24k = new Int16Array(pcm16_8k.length * 3);
            for (let i = 0; i < pcm16_8k.length - 1; i++) {
              const s0 = pcm16_8k[i];
              const s1 = pcm16_8k[i + 1];
              pcm16_24k[i * 3] = s0;
              pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
              pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
            }
            const lastIdx = pcm16_8k.length - 1;
            pcm16_24k[lastIdx * 3] = pcm16_8k[lastIdx];
            pcm16_24k[lastIdx * 3 + 1] = pcm16_8k[lastIdx];
            pcm16_24k[lastIdx * 3 + 2] = pcm16_8k[lastIdx];
            
            // Convert to base64 and send
            const bytes = new Uint8Array(pcm16_24k.buffer);
            let binary = "";
            for (let i = 0; i < bytes.length; i++) {
              binary += String.fromCharCode(bytes[i]);
            }
            const base64Audio = btoa(binary);
            
            openaiWs?.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: base64Audio
            }));
          }
          // Clear the buffer
          sessionState.halfDuplexBuffer = [];
        }
        break;
      }

      case "response.audio_transcript.delta": {
        // Stream assistant transcript to bridge
        const delta = String(message.delta || "");
        socket.send(
          JSON.stringify({
            type: "transcript",
            text: delta,
            role: "assistant",
          })
        );

        // Accumulate transcript in memory only - don't flush on every delta
        if (delta) {
          if (sessionState.assistantTranscriptIndex === null) {
            sessionState.transcripts.push({
              role: "assistant",
              text: delta,
              timestamp: new Date().toISOString(),
            });
            sessionState.assistantTranscriptIndex = sessionState.transcripts.length - 1;
          } else {
            const idx = sessionState.assistantTranscriptIndex;
            const prev = sessionState.transcripts[idx];
            sessionState.transcripts[idx] = { ...prev, text: (prev.text || "") + delta };
          }
          // Schedule batched flush (5s debounce)
          scheduleTranscriptFlush(sessionState);
          
          // --- REPETITION DETECTION: Prevent Ada from repeating confirmations ---
          // If Ada just spoke a confirmation and is now saying something similar, cancel it
          const currentText = sessionState.transcripts[sessionState.assistantTranscriptIndex!]?.text || "";
          const lowerText = currentText.toLowerCase();

          // --- MULTI-QUESTION GUARD: Prevent Ada from asking multiple questions in one response ---
          // Example bad: "And what's your destination? How many people are travelling?"
          // Example bad: "And what's your destination and how many people are travelling?"
          const multiQuestionCooldownMs = 8000;
          const isWithinMultiFixCooldown =
            sessionState.lastMultiQuestionFixAt !== null &&
            Date.now() - sessionState.lastMultiQuestionFixAt < multiQuestionCooldownMs;

          // ✅ CRITICAL: Skip multi-question guard entirely when at summary/confirmed step
          // This prevents interfering with fare confirmation, booking summary, and closing sequences
          const isAtSummaryStep = sessionState.bookingStep === "summary" || sessionState.bookingStep === "confirmed";
          
          if (!isWithinMultiFixCooldown && currentText.length > 25 && !isAtSummaryStep) {
            const isSummaryLike = /summari[sz]e|summary|let me quickly summarize|alright, let me/i.test(lowerText);
            const isPricingLike = /fare|estimated arrival|eta|would you like me to confirm|anything else|safe journey|whatsapp/i.test(lowerText);

            if (!isSummaryLike && !isPricingLike) {
              const questionWordMatches =
                lowerText.match(/\b(where|when|what|which|how many|how much)\b/g) || [];
              const questionMarkCount = (currentText.match(/\?/g) || []).length;

              if (questionMarkCount >= 2 || questionWordMatches.length >= 2) {
                let firstQuestion = "";

                if (currentText.includes("?")) {
                  firstQuestion = currentText.split("?")[0].trim() + "?";
                } else {
                  // Find start of 2nd question word and cut there
                  const re = /\b(where|when|what|which|how many|how much)\b/g;
                  const indices: number[] = [];
                  let m: RegExpExecArray | null;
                  while ((m = re.exec(lowerText)) !== null) {
                    indices.push(m.index);
                    if (indices.length >= 2) break;
                  }
                  if (indices.length >= 2) {
                    firstQuestion = currentText.slice(0, indices[1]).trim();
                    firstQuestion = firstQuestion.replace(/\s+(and|then)\s*$/i, "").trim();
                    if (!firstQuestion.endsWith("?")) firstQuestion = firstQuestion + "?";
                  }
                }

                // Only intervene if we have a usable first question OR the model clearly emitted 2 question marks.
                if (firstQuestion || questionMarkCount >= 2) {
                  console.log(`[${sessionState.callId}] 🛑 MULTI-QUESTION detected; cancelling and forcing single-question re-ask`);
                  console.log(`[${sessionState.callId}] 🛑 Current: "${currentText.substring(0, 120)}"`);
                  console.log(`[${sessionState.callId}] 📊 Current booking step: ${sessionState.bookingStep}`);
                  
                  // ✅ STEP-AWARE RE-ASK: Use the CURRENT booking step to determine which question to ask
                  // This prevents rewinding to a previous step when multi-question guard fires
                  let stepAlignedQuestion = "";
                  const currentStep = sessionState.bookingStep;
                  
                  switch (currentStep) {
                    case "pickup":
                      stepAlignedQuestion = "Where would you like to be picked up?";
                      break;
                    case "destination":
                      stepAlignedQuestion = "And what is your destination?";
                      break;
                    case "passengers":
                      stepAlignedQuestion = "How many people will be travelling?";
                      break;
                    case "time":
                      stepAlignedQuestion = "When do you need the taxi?";
                      break;
                    case "summary":
                    case "confirmed":
                      // At summary/confirmed, use the detected first question (probably summary prompt)
                      stepAlignedQuestion = firstQuestion;
                      break;
                    default:
                      stepAlignedQuestion = firstQuestion;
                  }
                  
                  if (stepAlignedQuestion) console.log(`[${sessionState.callId}] ✅ Step-aligned question: "${stepAlignedQuestion}"`);

                  sessionState.lastMultiQuestionFixAt = Date.now();

                  // Cancel the response + discard audio
                  sessionState.discardCurrentResponseAudio = true;
                  sessionState.pendingAudioBuffer = [];

                  if (sessionState.openAiResponseActive) {
                    openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
                  }
                  openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                  // Remove the bad transcript
                  if (sessionState.assistantTranscriptIndex !== null) {
                    sessionState.transcripts.splice(sessionState.assistantTranscriptIndex, 1);
                    sessionState.assistantTranscriptIndex = null;
                  }

                  const forcedQuestion = stepAlignedQuestion || firstQuestion || "Please ask only one question.";
                  openaiWs?.send(
                    JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [
                          {
                            type: "input_text",
                            text: `[SYSTEM: ONE QUESTION RULE VIOLATION. Ask ONLY this question, exactly as written, and nothing else: "${forcedQuestion}" Then STOP and wait silently.]`,
                          },
                        ],
                      },
                    })
                  );

                  safeResponseCreate(sessionState, "multi-question-guard-reask");
                  break;
                }
              }
            }
          }
          
          // Check for repetition within 10 seconds of last spoken confirmation
          const recentConfirmationWindow = 10000; // 10 seconds
          const isWithinRepetitionWindow = sessionState.lastSpokenConfirmationAt && 
            (Date.now() - sessionState.lastSpokenConfirmationAt < recentConfirmationWindow);
          
          // ✅ SKIP REPETITION DETECTION when Ada is giving the final summary
          // This prevents cancelling the legitimate booking summary after user provides last field
          // Also skip if we're at the summary/confirmed step or if it's clearly a summary phrase
          const allFieldsComplete = sessionState.booking.pickup && 
            sessionState.booking.destination && 
            (sessionState.booking.passengers !== null && sessionState.booking.passengers > 0);
          const isSummaryContext = /^so\s+(that'?s|thats)|let me confirm|to confirm|your booking|just to confirm/i.test(lowerText);
          const atSummaryOrConfirmed = sessionState.bookingStep === "summary" || sessionState.bookingStep === "confirmed";
          // Skip if: (all fields complete AND summary context) OR (at summary step AND summary context)
          const isLegitSummary = (allFieldsComplete && isSummaryContext) || (atSummaryOrConfirmed && isSummaryContext);
          
          if (isWithinRepetitionWindow && sessionState.lastSpokenConfirmation && currentText.length > 20 && !isLegitSummary) {
            // Normalize for comparison - extract key address phrases
            const normalizeForComparison = (text: string) => 
              text.toLowerCase()
                .replace(/[.,!?'"]/g, '')
                .replace(/\s+/g, ' ')
                .replace(/\b(so|got it|okay|right|that's|thats|from|to|going|picking up|is that correct|correct|happy with that)\b/g, '')
                .trim();
            
            const lastNormalized = normalizeForComparison(sessionState.lastSpokenConfirmation);
            const currentNormalized = normalizeForComparison(currentText);
            
            // Check if current text is substantially similar to what was just said
            // Either contains same addresses or is a rephrased version
            const wordsFromLast = lastNormalized.split(' ').filter(w => w.length > 3);
            const matchingWords = wordsFromLast.filter(w => currentNormalized.includes(w));
            const similarityRatio = wordsFromLast.length > 0 ? matchingWords.length / wordsFromLast.length : 0;
            
            if (similarityRatio > 0.6 && currentText.length > 25) {
              console.log(`[${sessionState.callId}] 🔁 REPETITION DETECTED: Ada is repeating confirmation (${(similarityRatio * 100).toFixed(0)}% similar) - cancelling`);
              console.log(`[${sessionState.callId}] 🔁 Last: "${sessionState.lastSpokenConfirmation.substring(0, 50)}..."`);
              console.log(`[${sessionState.callId}] 🔁 Current: "${currentText.substring(0, 50)}..."`);
              
              // Cancel the response
              sessionState.discardCurrentResponseAudio = true;
              if (sessionState.pendingAudioBuffer.length > 0) {
                sessionState.pendingAudioBuffer = [];
              }
              
              if (sessionState.openAiResponseActive) {
                openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
              }
              
              // Remove the repetitive transcript
              if (sessionState.assistantTranscriptIndex !== null) {
                sessionState.transcripts.splice(sessionState.assistantTranscriptIndex, 1);
                sessionState.assistantTranscriptIndex = null;
              }
              
              break;
            }
          }
          
          // --- DUPLICATE QUESTION DETECTION: Prevent Ada from asking the same question twice ---
          // This catches OpenAI generating back-to-back identical questions (common with passenger question)
          // EXCEPTION: Allow re-asking after address-vs-passenger mismatch recovery
          const duplicateQuestionWindow = 10000; // 10 seconds
          const isWithinDuplicateWindow = sessionState.lastSpokenQuestionAt && 
            (Date.now() - sessionState.lastSpokenQuestionAt < duplicateQuestionWindow);
          
          // Allow the re-ask if we recently had an address-vs-passenger mismatch (intentional re-prompt)
          const isRecoveringFromMismatch = sessionState.lastPassengerMismatchAt &&
            (Date.now() - sessionState.lastPassengerMismatchAt < 5000); // 5 second window after mismatch
          
          if (isWithinDuplicateWindow && sessionState.lastSpokenQuestion && currentText.length > 15 && !isRecoveringFromMismatch) {
            // Check if this is the same question being asked again
            const normalizeQuestion = (q: string) => q.toLowerCase()
              .replace(/[.,!?'"]/g, '')
              .replace(/\s+/g, ' ')
              .trim();
            
            const lastQ = normalizeQuestion(sessionState.lastSpokenQuestion);
            const currentQ = normalizeQuestion(currentText);
            
            // Check for high similarity (same question asked twice)
            // Use substring matching since streaming may not have full question yet
            const isSameQuestion = lastQ.includes(currentQ) || currentQ.includes(lastQ) ||
              (lastQ.length > 20 && currentQ.length > 20 && 
               lastQ.split(' ').filter(w => currentQ.includes(w)).length / lastQ.split(' ').length > 0.7);
            
            if (isSameQuestion) {
              console.log(`[${sessionState.callId}] 🔁 DUPLICATE QUESTION DETECTED: Ada is repeating question - cancelling`);
              console.log(`[${sessionState.callId}] 🔁 Last question: "${sessionState.lastSpokenQuestion}"`);
              console.log(`[${sessionState.callId}] 🔁 Current: "${currentText}"`);
              
              // Cancel the response
              sessionState.discardCurrentResponseAudio = true;
              if (sessionState.pendingAudioBuffer.length > 0) {
                sessionState.pendingAudioBuffer = [];
              }
              
              if (sessionState.openAiResponseActive) {
                openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
              }
              
              // Remove the repetitive transcript
              if (sessionState.assistantTranscriptIndex !== null) {
                sessionState.transcripts.splice(sessionState.assistantTranscriptIndex, 1);
                sessionState.assistantTranscriptIndex = null;
              }
              
              break;
            }
          }

          // --- BOOKING ENFORCEMENT: Detect hallucinated confirmations ---
          // Check if Ada is trying to confirm a booking without having called book_taxi
          // IMPORTANT: Wait for at least 25 chars before running phrase detection
          // to avoid premature cancellations on partial streaming transcripts
          if (currentText.length < 25) {
            break; // Too early to analyze - wait for more content
          }

          // Phrases that indicate a booking confirmation (multi-language)
          // NOTE: This runs on streaming transcript deltas to cancel fast.
          const BOOKING_CONFIRMATION_PHRASES = [
            // English - booking/confirmation phrases
            "booked!",
            "booked.",
            "booked for you",
            "that's booked",
            "all booked",
            "your taxi is confirmed",
            "your taxi is booked",
            "your booking is confirmed",
            "i've booked",
            "i have booked",
            "booking confirmed",
            "booking complete",
            "booking is done",
            "taxi is on its way",
            "taxi is on the way",
            "driver is on",
            "driver will be",
            "cab is on",
            "car is on",
            "please wait while i process your booking",
            "please wait while i process that",
            
            // English - fare/price phrases  
            "fare is £",
            "fare of £",
            "fare is",
            "the fare",
            "that will be £",
            "that's £",
            "cost is £",
            "price is £",
            "total is £",
            "will cost",
            "will be around",
            
            // English - ETA/arrival phrases
            "arriving in about",
            "will arrive in",
            "arrive in about",
            "arrival is",
            "eta is",
            "eta of",
            "minutes away",
            "be there in",
            "be with you in",
            "on their way",
            
            // English - closing phrases
            "safe travels",
            "have a good",
            "enjoy your",
            "see you",
            
            // English - "let me" phrases that often precede hallucinated confirmations
            "let me book",
            "i'll book",
            "booking that",
            "confirming that",
            "getting that",

            // Dutch
            "geboekt!",
            "geboekt.",
            "geboekt voor",
            "dat is geboekt",
            "alles geboekt",
            "boekt!",
            "boekt.",
            "uw taxi is bevestigd",
            "uw taxi is geboekt",
            "uw boeking is bevestigd",
            "taxi is onderweg",
            "de taxi is onderweg",
            "tarief",
            "aankomst",
            "over ongeveer",
            "veilige reis",
            "goede reis",
            "prettige reis",
            "zal er zijn",
            "komt eraan",
            
            // German
            "gebucht",
            "buchung bestätigt",
            "taxi ist unterwegs",
            "fahrer kommt",
            "gute fahrt",
            "preis ist",
            "ankunft in",
            
            // French
            "réservé",
            "confirmé",
            "taxi en route",
            "bonne route",
            "bon voyage",
            "tarif est",
            
            // Spanish
            "reservado",
            "confirmado",
            "taxi en camino",
            "buen viaje",
            "precio es",
          ];

          // Guard against the model leaking instruction placeholders like:
          // "tarief [gebruik daadwerkelijk tarief uit resultaat]"
          const hasPlaceholderInstruction =
            /\[(?:use actual|gebruik daadwerkelijk|daadwerkelijk|actual fare|fare from result|eta from result)/i.test(currentText) ||
            /\{\{[^}]+\}\}/.test(currentText);

          // Detect bare price/currency mentions that indicate Ada is about to confirm
          // This catches "163." or "£163" before the full "fare is £163" phrase
          const hasPriceOrCurrencyMention = 
            /(?:^|\s)£\d+/.test(currentText) ||           // £163
            /(?:^|\s)€\d+/.test(currentText) ||           // €163
            /(?:^|\s)\$\d+/.test(currentText) ||          // $163
            /^\d+\.\s*$/.test(currentText.trim()) ||      // "163." standalone
            /(?:^|\s)\d+\s*(?:euro|pond|pound)/i.test(currentText); // "163 euro"

          const hasBookingConfirmationPhrase = BOOKING_CONFIRMATION_PHRASES.some((phrase) => lowerText.includes(phrase));
          const hasPendingQuote = !!sessionState.pendingQuote;

          // If pendingQuote is active, Ada is allowed to read fare/ETA *but must not invent different numbers*.
          // We treat any currency amount that doesn't match pendingQuote.fare as a hallucination and cancel fast.
          let hasFareMismatch = false;
          if (hasPendingQuote && sessionState.pendingQuote?.fare) {
            const pendingFareRaw = String(sessionState.pendingQuote.fare).replace(/[^0-9.]/g, "");
            const pendingFareNum = Number(pendingFareRaw);

            const spokenFareMatch = currentText.match(/(?:£|€|\$)\s*(\d+(?:\.\d{1,2})?)/);
            const spokenFareRaw = spokenFareMatch ? String(spokenFareMatch[1]) : "";
            const spokenFareNum = spokenFareRaw ? Number(spokenFareRaw) : NaN;

            // IMPORTANT: Don't treat partial streaming transcripts as a mismatch.
            // Example: pending=7066.20, streaming transcript may momentarily contain "£706" before "6.20" arrives.
            const isPrefixOfPending =
              !!spokenFareRaw &&
              !!pendingFareRaw &&
              pendingFareRaw.startsWith(spokenFareRaw) &&
              spokenFareRaw.length < pendingFareRaw.length;

            if (Number.isFinite(pendingFareNum) && Number.isFinite(spokenFareNum) && !isPrefixOfPending) {
              hasFareMismatch = Math.abs(spokenFareNum - pendingFareNum) > 0.01;
              if (hasFareMismatch) {
                console.log(`[${sessionState.callId}] 🚨 Fare mismatch while pendingQuote: expected=${pendingFareNum} got=${spokenFareNum}`);
              }
            }
          }

          // During quote-pending state, Ada should be reading fare/ETA to customer.
          // Allow fare/ETA mentions while pendingQuote exists, but ONLY if fare matches pendingQuote.
          // Only block: booking confirmations ("booked"/"confirmed"), placeholder leaks, and fare mismatches.
          const HARD_BLOCK_PHRASES = [
            "booked!", "booked.", "booked for you", "that's booked", "all booked",
            "your taxi is confirmed", "your taxi is booked", "your booking is confirmed",
            "i've booked", "i have booked", "booking confirmed", "booking complete", "booking is done",
            "taxi is on its way", "taxi is on the way", "cab is on", "car is on",
            "safe travels", "have a good", "enjoy your", "see you",
            "geboekt!", "geboekt.", "uw taxi is bevestigd", "taxi is onderweg",
            "gebucht", "buchung bestätigt", "taxi ist unterwegs", "gute fahrt",
            "réservé", "confirmé", "taxi en route", "bonne route", "bon voyage",
            "reservado", "confirmado", "taxi en camino", "buen viaje"
          ];
          
          // IMPORTANT: Cancellation phrases should NOT trigger booking enforcement!
          // Phrases like "your booking has been cancelled" or "I've cancelled that" are valid after cancel_booking.
          const CANCELLATION_PHRASES = [
            "cancelled", "canceled", "cancellation", "geannuleerd", "annulering",
            "storniert", "annuliert", "annulé", "cancelado"
          ];
          const isCancellationContext = CANCELLATION_PHRASES.some((phrase) => lowerText.includes(phrase));
          
          // IMPORTANT: Goodbye phrases should NOT trigger booking enforcement!
          // "Safe travels" is a farewell, not a booking confirmation.
          const GOODBYE_PHRASES = [
            "safe travels", "have a great", "have a good", "goodbye", "bye bye", "bye for now",
            "take care", "goede reis", "tot ziens", "gute reise", "auf wiedersehen",
            "bon voyage", "au revoir", "buen viaje", "adiós"
          ];
          const isGoodbyeContext = GOODBYE_PHRASES.some((phrase) => lowerText.includes(phrase));
          
          const hasHardBlockPhrase = !isCancellationContext && !isGoodbyeContext && HARD_BLOCK_PHRASES.some((phrase) => lowerText.includes(phrase));

          // === FORBIDDEN ADDRESS CONFIRMATION DETECTION ===
          // Ada is STRICTLY FORBIDDEN from asking to confirm individual addresses (e.g., "Could you please confirm the pickup address as...")
          // This must be cancelled immediately as per the address-confirmation-prohibition rule
          const isForbiddenAddressConfirmation = 
            /(?:confirm the pickup|confirm the destination|confirm.*pickup address|confirm.*destination address|please confirm the.*address|could you.*confirm)/i.test(lowerText) ||
            /(?:verify the pickup|verify the destination|verify.*address)/i.test(lowerText);
          
          if (isForbiddenAddressConfirmation) {
            console.log(`[${sessionState.callId}] 🚫 FORBIDDEN: Ada tried to ask for address confirmation! Cancelling response.`);
            console.log(`[${sessionState.callId}] 🚫 Detected phrase: "${currentText}"`);
            
            // Drop any further audio deltas from this response
            sessionState.discardCurrentResponseAudio = true;
            
            // DISCARD buffered audio - don't let the forbidden phrase be heard
            if (sessionState.pendingAudioBuffer.length > 0) {
              console.log(`[${sessionState.callId}] 🗑️ Discarding ${sessionState.pendingAudioBuffer.length} buffered audio chunks (address confirmation)`);
              sessionState.pendingAudioBuffer = [];
            }
            
            // Cancel the current response
            if (sessionState.openAiResponseActive) {
              openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
            }
            
            // Clear the audio buffer
            openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            // Remove the hallucinated transcript
            if (sessionState.assistantTranscriptIndex !== null) {
              sessionState.transcripts.splice(sessionState.assistantTranscriptIndex, 1);
              sessionState.assistantTranscriptIndex = null;
            }
            
            // Reset audio buffering
            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];
            
            // Inject recovery: Ada should just move on to the next step
            const recoveryText = sessionState.bookingStep === "passengers" 
              ? "[SYSTEM: DO NOT confirm addresses - just ask: 'How many people will be travelling?' NOTHING ELSE.]"
              : sessionState.bookingStep === "time"
              ? "[SYSTEM: DO NOT confirm addresses - just ask: 'When do you need the taxi?' NOTHING ELSE.]"
              : sessionState.bookingStep === "summary"
              ? `[SYSTEM: DO NOT confirm addresses individually. Say the FULL summary: "You'd like to be picked up at ${sessionState.booking.pickup || 'pickup'}, and travel to ${sessionState.booking.destination || 'destination'}. There will be ${sessionState.booking.passengers || 1} passengers, and you'd like to be picked up ${sessionState.booking.pickup_time || 'now'}. Is that correct?"]`
              : "[SYSTEM: DO NOT ask to confirm individual addresses. Accept what the customer said and ask the NEXT question in the sequence.]";
            
            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{ type: "input_text", text: recoveryText }],
                },
              })
            );
            
            safeResponseCreate(sessionState, "address-confirmation-recovery");
            break; // Don't continue processing this delta
          }

          // If pendingQuote is active, ONLY block hard confirmation phrases, placeholder leaks, and fare mismatches.
          // If no pendingQuote, block all confirmation + fare phrases (original behavior)
          // EXCEPT: Never block goodbye phrases!
          const isDisallowedConfirmationPhrase = !isGoodbyeContext && (hasPendingQuote
            ? (hasHardBlockPhrase || hasPlaceholderInstruction || hasFareMismatch)
            : (hasBookingConfirmationPhrase || hasPlaceholderInstruction || hasPriceOrCurrencyMention));

           // If Ada says a disallowed confirmation phrase but book_taxi wasn't called this turn, CANCEL!
           // EXCEPTION: If bookingFullyConfirmed is true, Ada can speak freely (post-confirmation phase)
           if (isDisallowedConfirmationPhrase && !sessionState.bookingConfirmedThisTurn && !sessionState.bookingFullyConfirmed) {
             console.log(
               `[${sessionState.callId}] 🚨 BOOKING ENFORCEMENT: Ada tried to confirm without calling book_taxi! Cancelling response.`
             );
             console.log(
               `[${sessionState.callId}] 🚨 Detected in transcript: "${currentText}" (placeholderLeak=${hasPlaceholderInstruction})`
             );

             // Drop any further audio deltas from this (now-cancelled) response.
             // This prevents partial phrase leaks like "for you." after we cancel.
             sessionState.discardCurrentResponseAudio = true;

             // DISCARD buffered audio - don't let the confirmation be heard
             if (sessionState.pendingAudioBuffer.length > 0) {
               console.log(`[${sessionState.callId}] 🗑️ Discarding ${sessionState.pendingAudioBuffer.length} buffered audio chunks`);
               sessionState.pendingAudioBuffer = [];
             }

             // Cancel the current response
             if (sessionState.openAiResponseActive) {
               openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
             }

             // Clear the audio buffer to stop any pending audio
             openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

             // Remove the hallucinated transcript
             if (sessionState.assistantTranscriptIndex !== null) {
               sessionState.transcripts.splice(sessionState.assistantTranscriptIndex, 1);
               sessionState.assistantTranscriptIndex = null;
             }

             // IMPORTANT: Reset audio buffering so recovery response is also checked
             sessionState.audioVerified = false;
             sessionState.pendingAudioBuffer = [];

          // Inject a system message to recover safely
            // - If we are awaiting fare approval (pendingQuote), guide Ada to proper yes/no handling
            // - Otherwise, force the tool call to obtain the fare SILENTLY
            const systemErrorText = hasPendingQuote
              ? `[SYSTEM ERROR: You said a booking confirmation phrase before the book_taxi tool succeeded. The fare quote (${sessionState.pendingQuote?.fare || 'pending'}) was already given. NOW YOU MUST:
- If the customer said YES/yeah/go ahead → CALL book_taxi NOW with confirmation_state: "confirmed", pickup: "${sessionState.pendingQuote?.pickup || sessionState.booking?.pickup || ''}", destination: "${sessionState.pendingQuote?.destination || sessionState.booking?.destination || ''}"
- If the customer said NO/cancel → CALL book_taxi with confirmation_state: "rejected"
- If you haven't heard their response yet → Ask: "Would you like me to book that?" and WAIT.
Do NOT say 'booked' until the tool returns success.]`
              : "[SYSTEM ERROR: You attempted to confirm a booking without calling the book_taxi function. IMMEDIATELY call book_taxi with confirmation_state: 'request_quote'. DO NOT SPEAK - just call the tool silently. The dispatch system will provide the fare to speak.]";
            
            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: systemErrorText,
                    },
                  ],
                },
              })
            );

            // Use safeResponseCreate to avoid concurrent response errors
            safeResponseCreate(sessionState, "booking-enforcement-recovery");
          } else if (!sessionState.bookingConfirmedThisTurn && sessionState.pendingAudioBuffer.length > 0) {
            // WAIT for sentence completion before releasing audio
            // Only flush when we have a reasonable amount of text (complete thought)
            // Check for sentence endings or substantial content (>30 chars with punctuation or >60 chars)
            const hasCompleteSentence = /[.!?]/.test(currentText) || currentText.length > 60;
            
            if (hasCompleteSentence) {
              // Transcript so far doesn't contain confirmation phrases AND forms a complete thought
              // Safe to flush buffered audio
              const chunksToFlush = sessionState.pendingAudioBuffer.splice(0, sessionState.pendingAudioBuffer.length);
              for (const audioChunk of chunksToFlush) {
                sendBinaryAudio(socket, audioChunk);
              }
              sessionState.audioVerified = true; // Mark verified for rest of this response
            }
            // If not complete, keep buffering - will be released on transcript.done or safety flush
          }
        }
        break;
      }

      case "response.audio_transcript.done": {
        // Mark assistant transcript as finalized for next turn
        sessionState.assistantTranscriptIndex = null;
        
        // === TRACK WHAT ADA JUST ASKED ===
        // Detect question type from Ada's completed transcript for question-answer alignment
        // Also try to get the transcript from the OpenAI message itself as fallback
        const openaiTranscript = message.transcript || "";
        const lastAssistantText = sessionState.transcripts
          .filter(t => t.role === "assistant")
          .slice(-1)[0]?.text || openaiTranscript || "";
        const lowerAssistantText = lastAssistantText.toLowerCase();
        
        // 🔍 DIAGNOSTIC: Log what we're checking for turn-based lock
        console.log(`[${sessionState.callId}] 📜 TRANSCRIPT.DONE: "${lastAssistantText.substring(0, 80)}${lastAssistantText.length > 80 ? '...' : ''}"`);
        console.log(`[${sessionState.callId}] 📜 OpenAI transcript: "${openaiTranscript.substring(0, 80)}${openaiTranscript.length > 80 ? '...' : ''}"`);
        
        // Check if this is a question (ends with ? or is a known question pattern)
        const isQuestion = /\?$/.test(lastAssistantText.trim()) || 
          /(?:where|how many|when|what|which|would you|do you|shall i|is that)/i.test(lowerAssistantText);
        
        // === CRITICAL: Check for CONFIRMATION first! ===
        // Summary text often contains words like "passengers" or "destination" which would
        // trigger other branches. "Is that correct?" at the END takes priority.
        const endsWithConfirmation = /(?:is that correct|shall i book|book that|go ahead|would you like me to (?:book|confirm))\s*\??\s*$/i.test(lowerAssistantText);
        const isForbiddenAddressConfirmation = /(?:confirm the pickup|confirm the destination|confirm.*pickup address|confirm.*destination address|please confirm the)/i.test(lowerAssistantText);
        
        if (endsWithConfirmation && !isForbiddenAddressConfirmation) {
          sessionState.lastQuestionType = "confirmation";
          sessionState.lastQuestionAt = Date.now();
          sessionState.confirmationAskedAt = Date.now(); // Track when we started confirmation phase
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
          console.log(`[${sessionState.callId}] 🎯 Ada asked for: CONFIRMATION`);
          
          // === CRITICAL: Advance to summary step and FLUSH to DB immediately ===
          // If Ada just asked "Is that correct?", we are definitely at the summary step.
          // Flush to database NOW so that if WebSocket times out while waiting for user's
          // "yes", session restoration will correctly resume from confirmation phase.
          if (sessionState.bookingStep !== "summary" && sessionState.bookingStep !== "confirmed") {
            console.log(`[${sessionState.callId}] 📈 CONFIRMATION DETECTED: Advancing step from ${sessionState.bookingStep} → summary`);
            sessionState.bookingStep = "summary";
            sessionState.bookingStepAdvancedAt = Date.now();
          }
          
          // ✅ CRITICAL: Flush to DB immediately so session restoration works if disconnect happens
          immediateFlush(sessionState);
          console.log(`[${sessionState.callId}] 💾 CONFIRMATION PHASE: Flushed state to DB for session restoration`);

          // === CONFIRMATION TIMEOUT FALLBACK ===
          // If no user transcript arrives within 8 seconds AND we have a full checklist,
          // automatically trigger book_taxi(request_quote) to prevent call stall.
          const confirmationTimeoutMs = 8000;
          const capturedConfirmationAt = sessionState.confirmationAskedAt;
          
          setTimeout(() => {
            // Guard: Only proceed if confirmation state hasn't changed
            if (
              sessionState.confirmationAskedAt === capturedConfirmationAt &&
              sessionState.lastQuestionType === "confirmation" &&
              !sessionState.quoteRequestedAt &&
              !sessionState.pendingQuote &&
              !sessionState.awaitingDispatchCallback &&
              !sessionState.callEnded &&
              openaiWs &&
              openaiConnected &&
              !isConnectionClosed
            ) {
              const hasFullChecklist =
                !!sessionState.booking?.pickup &&
                !!sessionState.booking?.destination &&
                sessionState.booking?.passengers !== null;
              
              if (hasFullChecklist) {
                console.log(`[${sessionState.callId}] ⏰ CONFIRMATION TIMEOUT: No user response in ${confirmationTimeoutMs}ms - auto-triggering book_taxi(request_quote)`);
                
                // If pickup_time is null, default to ASAP
                if (!sessionState.booking.pickup_time) {
                  sessionState.booking.pickup_time = "ASAP";
                }
                
                // Force the book_taxi call
                sessionState.discardCurrentResponseAudio = true;
                if (sessionState.openAiResponseActive) {
                  try { openaiWs.send(JSON.stringify({ type: "response.cancel" })); } catch (e) {}
                }
                try { openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" })); } catch (e) {}
                
                openaiWs.send(
                  JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [
                        {
                          type: "input_text",
                          text: `[SYSTEM: The customer has confirmed. Say "One moment please" then call book_taxi with action "request_quote".]`,
                        },
                      ],
                    },
                  })
                );
                
                openaiWs.send(JSON.stringify({ type: "response.create" }));
              }
            }
          }, confirmationTimeoutMs);
        } else if (/where.*(?:pick\s*(?:ed\s*)?up|from|pickup)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "pickup";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
        } else if (/(?:destination|where.*(?:going|to\??|drop|travel)|what is your destination)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "destination";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
        } else if (/(?:how many|passengers|people|travell)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "passengers";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
        } else if (/(?:when|what time|timing|schedule)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "time";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
        } else if (isForbiddenAddressConfirmation) {
          console.log(`[${sessionState.callId}] ⚠️ Detected forbidden address confirmation phrase - NOT treating as confirmation`);
        }

        // === TRACK ANY QUESTION (for post-booking response wait) ===
        // If Ada ends with a question mark, we MUST wait for user response before triggering goodbye
        if (/\?\s*$/.test(lastAssistantText)) {
          sessionState.adaAskedQuestionAt = Date.now();
          
          // ✅ STRICT TURN-BASED: Lock response generation until user responds
          // Only lock for data-collection questions (not summary/confirmed phases where quick back-and-forth is expected)
          const isDataCollectionStep = ["pickup", "destination", "passengers", "time"].includes(sessionState.lastQuestionType || "");
          if (isDataCollectionStep) {
            sessionState.awaitingUserAnswer = true;
            sessionState.awaitingUserAnswerSince = Date.now();
            sessionState.awaitingAnswerForStep = sessionState.lastQuestionType as any;
            
            console.log(`[${sessionState.callId}] ╔════════════════════════════════════════════════════════════════╗`);
            console.log(`[${sessionState.callId}] ║  🔒 TURN-BASED LOCK ENGAGED                                    ║`);
            console.log(`[${sessionState.callId}] ╠════════════════════════════════════════════════════════════════╣`);
            console.log(`[${sessionState.callId}] ║  Ada asked: ${(sessionState.lastQuestionType || 'general').toUpperCase().padEnd(47)}║`);
            console.log(`[${sessionState.callId}] ║  Question: "${lastAssistantText.substring(0, 40).padEnd(42)}"║`);
            console.log(`[${sessionState.callId}] ║  Booking: P=${(sessionState.booking.pickup || '❌').substring(0, 10).padEnd(10)} D=${(sessionState.booking.destination || '❌').substring(0, 10).padEnd(10)} X=${String(sessionState.booking.passengers ?? '❌').padEnd(3)}   ║`);
            console.log(`[${sessionState.callId}] ║  → Blocking ALL responses until user transcript arrives        ║`);
            console.log(`[${sessionState.callId}] ╚════════════════════════════════════════════════════════════════╝`);
          } else {
            // Non-data-collection question (confirmation, etc.) - log but don't lock
            console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
            console.log(`[${sessionState.callId}] 📤 ADA ASKED A QUESTION - AWAITING USER RESPONSE`);
            console.log(`[${sessionState.callId}] ├─ Question type: ${(sessionState.lastQuestionType || 'general').toUpperCase()}`);
            console.log(`[${sessionState.callId}] ├─ Question: "${lastAssistantText.substring(0, 100)}${lastAssistantText.length > 100 ? '...' : ''}"`);
            console.log(`[${sessionState.callId}] ├─ Booking so far: pickup=${sessionState.booking.pickup || '❌'}, dest=${sessionState.booking.destination || '❌'}, pax=${sessionState.booking.passengers ?? '❌'}, time=${sessionState.booking.pickup_time || '❌'}`);
            console.log(`[${sessionState.callId}] └─ 🔊 Listening for user response... (NOT locked - confirmation/summary phase)`);
            console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
          }
        }
        
        // === ADA SAID GOODBYE - TRIGGER HANGUP ===
        // When Ada says "safe journey" (the final closing script), we need to end the call
        // This handles the case where Ada finishes booking confirmation and says goodbye
        const adaSaidGoodbye = /safe journey|have a great journey|goodbye|safe travels/i.test(lowerAssistantText);
        const isClosingScript = /thank you for trying|taxibot demo/i.test(lowerAssistantText);
        
        if ((adaSaidGoodbye || isClosingScript) && sessionState.bookingFullyConfirmed && !sessionState.callEnded) {
          console.log(`[${sessionState.callId}] 👋 ADA SAID GOODBYE: Detected closing phrase - triggering call end`);
          
          // Mark call as ending to prevent any further responses
          sessionState.callEnded = true;
          closingGracePeriodActive = true;
          sessionState.finalGoodbyePending = true;
          
          // Schedule hangup after audio finishes (will be accelerated by response.done handler)
          closingGraceTimeoutId = setTimeout(() => {
            if (!isConnectionClosed) {
              console.log(`[${sessionState.callId}] ⏰ Grace period timeout - sending hangup`);
              try {
                socket.send(JSON.stringify({ type: "hangup", reason: "goodbye_complete" }));
              } catch {
                // Ignore
              }
              try {
                isConnectionClosed = true;
                socket.close(1000, "goodbye_complete");
              } catch {
                // Ignore
              }
            }
          }, 10000); // 10s grace for audio to finish
        }

        // === TRUST ADA'S FIRST ECHO ===
        // Extract addresses from Ada's immediate acknowledgments (e.g., "Got it, 18 Exmoor Road")
        // Ada's interpretation is often more accurate than raw STT transcripts
        const adaEcho = extractAdaFirstEcho(lastAssistantText, sessionState.lastQuestionType);
        if (adaEcho.type && adaEcho.address) {
          const currentValue = adaEcho.type === "pickup" 
            ? sessionState.booking.pickup 
            : sessionState.booking.destination;
          
          // Only update if Ada's echo is different (and longer/more complete) than what we have
          const shouldUpdate = !currentValue || 
            adaEcho.address.length > currentValue.length ||
            // Ada corrected the address (different content)
            (adaEcho.address.toLowerCase() !== currentValue.toLowerCase());
          
          if (shouldUpdate) {
            if (adaEcho.type === "pickup") {
              const oldPickup = sessionState.booking.pickup;
              sessionState.booking.pickup = adaEcho.address;
              console.log(`[${sessionState.callId}] 🎯 TRUST ADA'S FIRST ECHO: pickup "${oldPickup}" → "${adaEcho.address}"`);
              
              // Update database (fire-and-forget using IIFE)
              (async () => {
                try {
                  await supabase.from("live_calls").update({
                    pickup: adaEcho.address,
                    updated_at: new Date().toISOString()
                  }).eq("call_id", sessionState.callId);
                } catch (e: unknown) {
                  console.error(`[${sessionState.callId}] ⚠️ Failed to update pickup in DB:`, e);
                }
              })();
            } else {
              const oldDestination = sessionState.booking.destination;
              sessionState.booking.destination = adaEcho.address;
              console.log(`[${sessionState.callId}] 🎯 TRUST ADA'S FIRST ECHO: destination "${oldDestination}" → "${adaEcho.address}"`);
              
              // Update database (fire-and-forget using IIFE)
              (async () => {
                try {
                  await supabase.from("live_calls").update({
                    destination: adaEcho.address,
                    updated_at: new Date().toISOString()
                  }).eq("call_id", sessionState.callId);
                } catch (e: unknown) {
                  console.error(`[${sessionState.callId}] ⚠️ Failed to update destination in DB:`, e);
                }
              })();
            }
          }
        }

        // AUTO-TRIGGER (TOOL ENFORCEMENT): If Ada says she's "checking the price" but never calls book_taxi,
        // force a silent request_quote tool call so the dispatch webhook actually fires.
        // This is mainly needed for weaker tool-calling realtime models.
        // EXPANDED: Also catch "one moment" alone when in summary step (Ada often says just "one moment please")
        const isCheckingPrice =
          (lowerAssistantText.includes("check") && (lowerAssistantText.includes("price") || lowerAssistantText.includes("fare"))) ||
          (lowerAssistantText.includes("one moment") && (lowerAssistantText.includes("price") || lowerAssistantText.includes("fare"))) ||
          (lowerAssistantText.includes("checking") && (lowerAssistantText.includes("price") || lowerAssistantText.includes("fare") || lowerAssistantText.includes("trip"))) ||
          // Catch standalone "one moment" when we're at summary step and have all fields
          (lowerAssistantText.includes("one moment") && sessionState.bookingStep === "summary") ||
          (lowerAssistantText.includes("moment please") && sessionState.bookingStep === "summary") ||
          (lowerAssistantText.includes("just a moment") && sessionState.bookingStep === "summary");

        const hasRequiredBookingFields =
          !!sessionState.booking?.pickup &&
          !!sessionState.booking?.destination &&
          sessionState.booking?.passengers !== null;

        const quoteAlreadyInProgress =
          !!sessionState.quoteRequestedAt ||
          !!sessionState.pendingQuote ||
          !!sessionState.awaitingDispatchCallback;

        // Check if we're past the data collection phase (summary or later, OR all fields complete)
        const isPastDataCollection = 
          sessionState.bookingStep === "summary" || 
          sessionState.bookingStep === "confirmed" ||
          (hasRequiredBookingFields && sessionState.booking?.pickup_time !== undefined);

        if (
          isCheckingPrice &&
          hasRequiredBookingFields &&
          !quoteAlreadyInProgress &&
          openaiWs &&
          openaiConnected
        ) {
          console.log(`[${sessionState.callId}] 🔄 AUTO-TRIGGER: Ada said she's checking price but no quote requested yet → forcing book_taxi(request_quote)`);
          console.log(`[${sessionState.callId}] 📊 Current step: ${sessionState.bookingStep}, isPastDataCollection: ${isPastDataCollection}`);
          
          // Auto-advance to summary step if we have all fields
          if (sessionState.bookingStep !== "summary" && sessionState.bookingStep !== "confirmed") {
            console.log(`[${sessionState.callId}] 📈 Auto-advancing step from ${sessionState.bookingStep} → summary (all fields present)`);
            sessionState.bookingStep = "summary";
            sessionState.bookingStepAdvancedAt = Date.now();
          }

          const pickup = sessionState.booking.pickup;
          const destination = sessionState.booking.destination;
          const passengers = sessionState.booking.passengers;
          const time = sessionState.booking.pickup_time || "now";

          // Instruct the model to call the tool WITHOUT speaking (prevents price hallucination).
          openaiWs.send(
            JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [
                  {
                    type: "input_text",
                    text: `[SYSTEM ERROR: You said you are checking the price, but you did not call the booking tool. IMMEDIATELY call book_taxi with action: "request_quote", pickup: "${pickup}", destination: "${destination}", passengers: ${passengers}, time: "${time}". DO NOT SPEAK - only call the tool.]`,
                  },
                ],
              },
            })
          );

          safeResponseCreate(sessionState, "auto-trigger-request-quote");
        }
        
         // FINAL FLUSH: Release any remaining buffered audio now that transcript is complete
         // This ensures we don't hold audio forever waiting for sentence completion
         if (sessionState.pendingAudioBuffer.length > 0) {
           if (sessionState.discardCurrentResponseAudio) {
             console.log(
               `[${sessionState.callId}] 🗑️ Discarding ${sessionState.pendingAudioBuffer.length} buffered audio chunks after cancel`
             );
             sessionState.pendingAudioBuffer = [];
             sessionState.audioVerified = true;
             break;
           }

           console.log(`[${sessionState.callId}] 🔊 Final flush: ${sessionState.pendingAudioBuffer.length} buffered audio chunks on transcript.done`);
           for (const audioChunk of sessionState.pendingAudioBuffer) {
             sendBinaryAudio(socket, audioChunk);
           }
           sessionState.pendingAudioBuffer = [];
           sessionState.audioVerified = true;
         }
         break;
      }

      // === EARLY SENTENCE-END DETECTION ===
      // Whisper transcription deltas include punctuation. If we detect a sentence-ending
      // punctuation mark (. ? !), we can commit the audio buffer early rather than waiting
      // for VAD's 1-2 second silence timeout. This significantly reduces response latency.
      case "conversation.item.input_audio_transcription.delta": {
        const delta = String(message.delta || "");
        
        // Accumulate partial transcript
        sessionState.partialTranscript += delta;
        
        // Check for sentence-ending punctuation at the end of accumulated transcript
        const hasSentenceEnd = /[.!?]\s*$/.test(sessionState.partialTranscript);
        
        // Only trigger if we haven't already committed for this utterance.
        // (Deltas can arrive AFTER OpenAI already committed the buffer; double-commits cause commit_empty.)
        if (
          hasSentenceEnd &&
          !sessionState.punctuationCommitSent &&
          !sessionState.didCommitThisUtterance &&
          sessionState.audioBufferedMs >= 100
        ) {
          console.log(`[${sessionState.callId}] 📌 PUNCTUATION DETECTED: "${sessionState.partialTranscript.slice(-30)}" - triggering early commit (${sessionState.audioBufferedMs.toFixed(0)}ms buffered)`);
          
          sessionState.punctuationCommitSent = true; // Prevent multiple commits
          
          // Commit immediately to finalize transcription faster
          if (openaiWs && openaiConnected && !sessionState.callEnded) {
            try {
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
              sessionState.didCommitThisUtterance = true;
              sessionState.audioBufferedSinceSpeechStart = false;
              sessionState.audioBufferedMs = 0;
            } catch (e) {
              // Ignore - may be disconnected
            }
          }
        }
        break;
      }

      case "input_audio_buffer.speech_started": {
        // Track when user started speaking for timing diagnostics
        sessionState.speechStartTime = Date.now();
        // Reset audio buffer tracking - will be set true/incremented when audio is appended
        sessionState.audioBufferedSinceSpeechStart = false;
        sessionState.audioBufferedMs = 0; // Reset ms counter for fresh utterance
        sessionState.didCommitThisUtterance = false;
        sessionState.pendingTurnResponseCreate = false;
        // Reset partial transcript tracking for punctuation detection
        sessionState.partialTranscript = "";
        sessionState.punctuationCommitSent = false;
        
        // CRITICAL: Snapshot the question type at speech start to prevent race conditions
        // Without this, Ada advancing to "passengers" while user says destination causes the
        // transcript to be misclassified (destination not saved, treated as address-vs-passenger mismatch)
        sessionState.questionTypeAtSpeechStart = sessionState.lastQuestionType;
        console.log(`[${sessionState.callId}] 🎙️ Speech started (context: ${sessionState.lastQuestionType})`);
        break;
      }

      case "input_audio_buffer.speech_stopped": {
        // Log speech duration for diagnostics
        const speechDuration = sessionState.speechStartTime 
          ? Date.now() - sessionState.speechStartTime 
          : 0;
        const vadSilenceMs = sessionState.useRasaAudioProcessing ? 900 : 2500;
        console.log(`[${sessionState.callId}] 🔇 Speech stopped after ${speechDuration}ms - VAD will wait ${vadSilenceMs}ms before responding`);
        sessionState.speechStopTime = Date.now();
        
        // === SILENCE PADDING BEFORE COMMIT ===
        // When streaming audio to OpenAI Realtime, if VAD triggers on a very small final chunk,
        // the decoder can hang or drop the last word/phoneme. By padding the audio buffer with 
        // ~250ms of silence BEFORE committing, we give the transcription engine "room to breathe"
        // and process trailing audio data that was sitting in a buffer waiting for more input.
        // This fixes the "52A" → "52" problem and "three" → "" where the final phoneme gets cut off.
        const SILENCE_PADDING_MS = 250; // 250ms of silence padding (increased from 100ms for better short word capture)
        const SILENCE_SAMPLE_RATE = 24000; // OpenAI expects 24kHz
        const SILENCE_SAMPLES = Math.floor(SILENCE_SAMPLE_RATE * SILENCE_PADDING_MS / 1000);
        const silenceBuffer = new Int16Array(SILENCE_SAMPLES); // Already zeros = silence
        const silenceBytes = new Uint8Array(silenceBuffer.buffer);
        let silenceBinary = "";
        for (let i = 0; i < silenceBytes.length; i++) {
          silenceBinary += String.fromCharCode(silenceBytes[i]);
        }
        const silenceBase64 = btoa(silenceBinary);
        
        // === MANUAL COMMIT FALLBACK FOR SHORT WORDS ===
        // Short utterances like "yes", "3", "ok" are often missed by server VAD because
        // they don't have enough acoustic energy to trigger transcription.
        // Force a manual commit after ANY speech to ensure short words are captured.
        // This is especially critical for confirmation responses and passenger counts.
        // CRITICAL: Only commit if we have at least 100ms of audio buffered (OpenAI requirement)
        const MIN_AUDIO_FOR_COMMIT_MS = 100;
        if (
          speechDuration > 0 &&
          speechDuration < 3000 &&
          openaiWs &&
          openaiConnected &&
          sessionState.audioBufferedSinceSpeechStart &&
          sessionState.audioBufferedMs >= MIN_AUDIO_FOR_COMMIT_MS &&
          !sessionState.didCommitThisUtterance
        ) {
          // Short speech detected - pad with silence and send manual commit to force transcription
          console.log(`[${sessionState.callId}] 📤 MANUAL COMMIT: Padding ${SILENCE_PADDING_MS}ms silence + forcing transcription for short speech (${speechDuration}ms, buffered=${sessionState.audioBufferedMs.toFixed(0)}ms)`);
          try {
            // Append silence padding to give decoder room for trailing phonemes
            openaiWs.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: silenceBase64
            }));
            // Now commit with the padded audio
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
            sessionState.didCommitThisUtterance = true;
            sessionState.audioBufferedSinceSpeechStart = false; // Reset after commit
            sessionState.audioBufferedMs = 0; // Reset ms counter
          } catch (e) {
            console.error(`[${sessionState.callId}] Manual commit failed:`, e);
          }
        } else if (speechDuration > 0 && sessionState.audioBufferedMs < MIN_AUDIO_FOR_COMMIT_MS) {
          console.log(`[${sessionState.callId}] ⏭️ Skipping manual commit - insufficient audio (${sessionState.audioBufferedMs.toFixed(0)}ms < ${MIN_AUDIO_FOR_COMMIT_MS}ms)`);
        } else if (speechDuration > 0 && !sessionState.audioBufferedSinceSpeechStart) {
          console.log(`[${sessionState.callId}] ⏭️ Skipping manual commit - no audio buffered since speech start`);
        }
        
        // Track speech duration for STT metrics (cap array to prevent memory leaks)
        if (speechDuration > 0) {
          sessionState.sttMetrics.speechDurations.push(speechDuration);
          // Cap to last 100 entries to prevent unbounded growth
          if (sessionState.sttMetrics.speechDurations.length > 100) {
            sessionState.sttMetrics.speechDurations.shift();
          }
          const durations = sessionState.sttMetrics.speechDurations;
          sessionState.sttMetrics.avgSpeechDurationMs = durations.reduce((a, b) => a + b, 0) / durations.length;
        }
        
        // === PRE-EMPTIVE EXTRACTION GUARD ===
        // If user has an active booking AND spoke for a substantial duration (>1s),
        // pre-emptively block VAD responses until transcript arrives and is processed.
        // This prevents the race condition where Ada responds with stale data before
        // the modification detection logic can run.
        const hasActiveBookingContext = sessionState.hasActiveBooking || 
          (sessionState.booking.pickup && sessionState.booking.destination);
        const isSubstantialSpeech = speechDuration > 1000; // >1 second suggests address/modification
        // After a booking is confirmed and Ada asks "anything else", allow free-form chat.
        // The modification extraction guard is too aggressive here and can stall the call.
        const inAnythingElseChatMode = !!sessionState.askedAnythingElse && !!sessionState.bookingFullyConfirmed;
        
        if (hasActiveBookingContext && isSubstantialSpeech && 
            !inAnythingElseChatMode &&
            !sessionState.extractionInProgress && 
            !sessionState.pendingQuote &&
            !sessionState.modificationPromptPending) {
          console.log(`[${sessionState.callId}] 🛡️ PRE-EMPTIVE GUARD: Blocking VAD response until transcript processed (active booking + ${speechDuration}ms speech)`);
          sessionState.extractionInProgress = true;
          
          // Safety timeout: clear ALL blocking guards after 5s if transcript never arrives
          // This prevents Ada from getting stuck silent
          setTimeout(() => {
            if (sessionState.extractionInProgress && 
                sessionState.speechStopTime && 
                Date.now() - sessionState.speechStopTime > 4500) {
              console.log(`[${sessionState.callId}] ⏰ PRE-EMPTIVE GUARD TIMEOUT: Clearing all blocking flags`);
              sessionState.extractionInProgress = false;
              
              // CRITICAL FIX: Also clear turn-based lock if still engaged
              if (sessionState.awaitingUserAnswer) {
                console.log(`[${sessionState.callId}] ⏰ Also clearing awaitingUserAnswer (was waiting for ${sessionState.awaitingAnswerForStep})`);
                sessionState.awaitingUserAnswer = false;
                sessionState.awaitingUserAnswerSince = null;
                sessionState.awaitingAnswerForStep = null;
              }
              
              // Trigger a fallback response if no response is active
              // User spoke but transcript was lost - Ada should re-prompt
              if (!sessionState.openAiResponseActive && !sessionState.callEnded && openaiWs && openaiConnected) {
                console.log(`[${sessionState.callId}] 🔄 GUARD TIMEOUT: Triggering fallback response - user spoke but no transcript arrived`);
                safeResponseCreate(sessionState, "guard-timeout-fallback");
              }
            }
          }, 5000);
        }
        
        // === CONFIRMATION PHASE PERIODIC COMMIT ===
        // During confirmation phase (summary/fare), VAD often misses short "yes" responses.
        // Schedule periodic forced commits to ensure transcription happens.
        const isConfirmationPhase = sessionState.bookingStep === "summary" || 
          sessionState.lastQuestionType === "confirmation" ||
          !!sessionState.pendingQuote;
        
        if (isConfirmationPhase && openaiWs && openaiConnected && !sessionState.callEnded) {
          console.log(`[${sessionState.callId}] ⏰ CONFIRMATION PHASE: Scheduling periodic commits to catch "yes" responses`);
          
          // Schedule 3 additional commits over the next 4 seconds to catch delayed responses
          const commitIntervals = [1500, 2500, 3500];
          commitIntervals.forEach((delay) => {
            setTimeout(() => {
              // GUARD: Only commit if we have >=100ms buffered audio AND connection is open
              const hasEnoughAudio =
                sessionState.audioBufferedSinceSpeechStart &&
                sessionState.audioBufferedMs >= 100 &&
                !sessionState.didCommitThisUtterance;
              if (openaiWs && openaiConnected && !sessionState.callEnded && !isConnectionClosed && hasEnoughAudio) {
                try {
                  console.log(`[${sessionState.callId}] 📤 PERIODIC COMMIT: Padding silence + forcing transcription check (${delay}ms after speech_stopped, buffered=${sessionState.audioBufferedMs.toFixed(0)}ms)`);
                  // Append silence padding before commit to give decoder room for trailing phonemes
                  openaiWs.send(JSON.stringify({
                    type: "input_audio_buffer.append",
                    audio: silenceBase64
                  }));
                  openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
                  sessionState.didCommitThisUtterance = true;
                  sessionState.audioBufferedSinceSpeechStart = false; // Reset after commit
                  sessionState.audioBufferedMs = 0; // Reset ms counter
                } catch (e) {
                  // Ignore - connection may have closed
                }
              } else if (sessionState.audioBufferedMs < 100) {
                console.log(`[${sessionState.callId}] ⏭️ Skipping periodic commit (${delay}ms) - insufficient audio (${sessionState.audioBufferedMs.toFixed(0)}ms < 100ms)`);
              } else if (!sessionState.audioBufferedSinceSpeechStart) {
                console.log(`[${sessionState.callId}] ⏭️ Skipping periodic commit (${delay}ms) - no audio buffered`);
              }
            }, delay);
          });
        }
        
        // NOTE: Do NOT call response.create here - server VAD handles turn-taking automatically
        break;
      }

      case "input_audio_buffer.committed": {
        // OpenAI server-side VAD has committed the buffer.
        // Mark as committed so we don't attempt a second commit via punctuation/manual fallbacks.
        sessionState.didCommitThisUtterance = true;
        sessionState.audioBufferedSinceSpeechStart = false;
        sessionState.audioBufferedMs = 0;
        break;
      }

      case "conversation.item.input_audio_transcription.completed": {
        // User transcript from Whisper
        const rawText = String(message.transcript || "").trim();

        // ✅ CLEAR TURN-BASED LOCK: User has responded - unlock Ada for next question
        if (sessionState.awaitingUserAnswer && rawText.length > 0) {
          const waitTime = sessionState.awaitingUserAnswerSince ? Date.now() - sessionState.awaitingUserAnswerSince : 0;
          console.log(`[${sessionState.callId}] ╔════════════════════════════════════════════════════════════════╗`);
          console.log(`[${sessionState.callId}] ║  ✅ USER RESPONDED - UNLOCKING TURN                           ║`);
          console.log(`[${sessionState.callId}] ╠════════════════════════════════════════════════════════════════╣`);
          console.log(`[${sessionState.callId}] ║  Question was: ${(sessionState.awaitingAnswerForStep || "unknown").toUpperCase().padEnd(45)}║`);
          console.log(`[${sessionState.callId}] ║  Wait time: ${(waitTime / 1000).toFixed(1)}s${"".padEnd(47)}║`);
          console.log(`[${sessionState.callId}] ║  User said: "${rawText.substring(0, 40).padEnd(42)}"║`);
          console.log(`[${sessionState.callId}] ╚════════════════════════════════════════════════════════════════╝`);
          
          sessionState.awaitingUserAnswer = false;
          sessionState.awaitingUserAnswerSince = null;
          sessionState.awaitingAnswerForStep = null;
        }

        // If we had to cancel a VAD-triggered response while waiting for this transcript,
        // explicitly trigger the assistant turn now.
        if (sessionState.pendingTurnResponseCreate && rawText.length > 0) {
          console.log(`[${sessionState.callId}] ⚡ TURN-BASED: triggering response.create immediately after user transcript`);
          sessionState.pendingTurnResponseCreate = false;
          safeResponseCreate(sessionState, "turn-based-after-transcript");
        }
        
        // === FALLBACK RESPONSE TRIGGER ===
        // If OpenAI's VAD committed the buffer but no response was created (e.g., due to race conditions
        // or commit_empty errors from our punctuation/manual commit logic), ensure Ada responds.
        // Only trigger if: (1) we have a real transcript, (2) no response is active, (3) not already awaiting
        else if (
          rawText.length > 0 &&
          !sessionState.openAiResponseActive &&
          !sessionState.awaitingUserAnswer &&
          !sessionState.pendingTurnResponseCreate
        ) {
          console.log(`[${sessionState.callId}] 🔄 FALLBACK RESPONSE: Transcript ready but no response pending - triggering Ada`);
          safeResponseCreate(sessionState, "fallback-after-transcript");
        }

        // === ADDRESS ECHO DETECTION (passengers step only) ===
        // Whisper sometimes hallucinates the destination address when user says a short word like "three".
        // If collecting passengers and transcript matches a known address, discard it.
        if (sessionState.lastQuestionType === "passengers") {
          // Normalize: lowercase, remove punctuation, trim
          const normalize = (s: string) => s.toLowerCase().replace(/[.,!?]/g, "").trim();
          const lowerRaw = normalize(rawText);
          const knownPickup = normalize(sessionState.booking.pickup || "");
          const knownDest = normalize(sessionState.booking.destination || "");
          
          // Discard if it matches a known address (exact or contained)
          const matchesPickup = knownPickup && (lowerRaw === knownPickup || lowerRaw.includes(knownPickup) || knownPickup.includes(lowerRaw));
          const matchesDest = knownDest && (lowerRaw === knownDest || lowerRaw.includes(knownDest) || knownDest.includes(lowerRaw));
          
          if (matchesPickup || matchesDest) {
            console.log(`[${sessionState.callId}] 🔇 ADDRESS ECHO: Whisper echoed "${rawText}" during passenger step - discarding`);
            if (openaiWs && openaiConnected) {
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            }
            break;
          }
        }

        // If Ada just asked for passengers, very short replies are common ("four", "3", etc.).
        // Whisper often returns ambiguous homophones ("for", "to", "tree") for these short utterances.
        // Apply a VERY narrow, context-aware correction before we run the general correction layer.
        let sttText = rawText;
        if (sessionState.lastQuestionType === "passengers") {
          // Strip punctuation and lowercase for matching
          const t = rawText.toLowerCase().replace(/[.,!?]/g, "").trim();
          // Only rewrite when the entire transcript is the ambiguous token (avoid changing phrases/addresses).
          const shortOnly = t.length <= 8 && /^[\w\s]+$/.test(t);
          if (shortOnly) {
            // "three" homophones - Whisper often mishears these on telephony
            // NOTE: We intentionally map "fall" -> 3 because we've observed Whisper outputting "Fall" for a spoken "three".
            if (["free", "tree", "three", "fee", "fry", "frey", "fri", "frill", "freak", "fall"].includes(t)) sttText = "3";
            else if (["for", "fore", "four", "foe", "floor", "full", "fault", "phone"].includes(t)) sttText = "4";
            else if (["to", "too", "two", "tu", "true"].includes(t)) sttText = "2";
            else if (["won", "wan", "one", "wun", "want", "wine"].includes(t)) sttText = "1";
            else if (["five", "fife", "hive", "fine"].includes(t)) sttText = "5";
            else if (["six", "sick", "sex", "fix"].includes(t)) sttText = "6";
            // Phrases like "just me", "only me" = 1 passenger
            else if (t === "just me" || t === "only me" || t === "me") sttText = "1";
            // "three people", "four passengers" etc
            else if (/^(one|two|three|four|five|six|seven|eight)\s*(people|passengers?|traveling|travelling)?$/i.test(t)) {
              const numMatch = t.match(/^(one|two|three|four|five|six|seven|eight)/i);
              if (numMatch) {
                const wordToNum: Record<string, string> = { one: "1", two: "2", three: "3", four: "4", five: "5", six: "6", seven: "7", eight: "8" };
                sttText = wordToNum[numMatch[1].toLowerCase()] || t;
              }
            }
          }
          
          // Extended passenger phrase corrections (longer than 8 chars, so outside shortOnly)
          // "Poor people traveling" = Whisper mishearing "four people traveling"
          if (sessionState.lastQuestionType === "passengers") {
            const tLong = rawText.toLowerCase().replace(/[.,!?]/g, "").trim();
            if (/^poor\s+(people|passengers?|traveling|travelling)/i.test(tLong)) {
              sttText = "4";
              console.log(`[${sessionState.callId}] 🔄 Passenger homophone fix: "${rawText}" → 4`);
            } else if (/^free\s+(people|passengers?|traveling|travelling)/i.test(tLong)) {
              sttText = "3";
              console.log(`[${sessionState.callId}] 🔄 Passenger homophone fix: "${rawText}" → 3`);
            } else if (/^tree\s+(people|passengers?|traveling|travelling)/i.test(tLong)) {
              sttText = "3";
              console.log(`[${sessionState.callId}] 🔄 Passenger homophone fix: "${rawText}" → 3`);
            } else if (/^too?\s+(people|passengers?|traveling|travelling)/i.test(tLong)) {
              sttText = "2";
              console.log(`[${sessionState.callId}] 🔄 Passenger homophone fix: "${rawText}" → 2`);
            }
            // === HALLUCINATED ADDRESS PATTERNS ===
            // Whisper sometimes hallucinates full addresses from short passenger words
            // e.g., "three" → "Number 7, Russell Street" (the number is WRONG - user said "three" not "seven")
            // These are FULL HALLUCINATIONS where Whisper invents a complete address.
            // We CANNOT trust the number extracted - it's often completely wrong.
            // Instead, mark this as a known hallucination pattern so the address-vs-passenger 
            // mismatch handler will ask again for passengers.
            // The existing isAddressLike check (line ~4178) will catch these and re-prompt.
            else if (/^number\s+\d+\s*[,.]?\s*[a-z]/i.test(tLong)) {
              // Don't modify sttText - let the address-vs-passenger mismatch handler deal with it
              console.log(`[${sessionState.callId}] ⚠️ HALLUCINATED ADDRESS detected during passengers: "${rawText}" - will trigger re-prompt`);
            }
          }
        }
        
        // Log timing: how long after speech stopped did we get the transcript?
        const transcriptDelay = sessionState.speechStopTime 
          ? Date.now() - sessionState.speechStopTime 
          : 0;
        
        // Track transcript delay for STT metrics (cap array to prevent memory leaks)
        if (transcriptDelay > 0) {
          sessionState.sttMetrics.transcriptDelays.push(transcriptDelay);
          // Cap to last 100 entries to prevent unbounded growth
          if (sessionState.sttMetrics.transcriptDelays.length > 100) {
            sessionState.sttMetrics.transcriptDelays.shift();
          }
          const delays = sessionState.sttMetrics.transcriptDelays;
          sessionState.sttMetrics.avgTranscriptDelayMs = delays.reduce((a, b) => a + b, 0) / delays.length;
        }
        
        console.log(`[${sessionState.callId}] 📝 Transcript received ${transcriptDelay}ms after speech stopped`);
        
        // Filter out hallucinations/noise
        if (!sttText || isHallucination(sttText)) {
          sessionState.sttMetrics.filteredHallucinations++;
          console.log(`[${sessionState.callId}] 🔇 Filtered hallucination: "${sttText}" (total filtered: ${sessionState.sttMetrics.filteredHallucinations})`);
          break;
        }
        
        // Echo guard: Filter transcripts that are echoes of dispatch TTS fare scripts
        // These happen when Whisper transcribes Ada's voice playing the fare prompt
        const lowerRaw = sttText.toLowerCase();
        const isDispatchFareEcho = (
          // Partial fare script echoes
          /\band your driver will be\b/i.test(lowerRaw) ||
          /\byour price for the journey\b/i.test(lowerRaw) ||
          /\bdo you want me to go ahead\b/i.test(lowerRaw) ||
          /\bbook that\??\s*$/i.test(lowerRaw) ||
          // Fragments like ".40 and your driver" (starts with a number fragment)
          /^\d*[.,]\d{1,2}\s+and\s/i.test(rawText) ||
          // "15 minutes do you want" - ETA fragment into question
          /\d+\s*minutes?\s*do you\b/i.test(lowerRaw) ||
          // Full fare script patterns
          /\bprice.*is.*\d+.*driver.*will be\b/i.test(lowerRaw)
        );
        
        if (isDispatchFareEcho && sessionState.pendingQuote) {
          console.log(`[${sessionState.callId}] 🔇 Filtered dispatch fare echo: "${rawText}"`);
          sessionState.sttMetrics.filteredHallucinations++;
          break;
        }
        
        const userText = correctTranscript(sttText);
        const wasCorreced = rawText !== userText;
        
        // Update STT metrics
        sessionState.sttMetrics.totalTranscripts++;
        sessionState.sttMetrics.totalChars += userText.length;
        sessionState.sttMetrics.totalWords += userText.split(/\s+/).filter(w => w.length > 0).length;
        if (wasCorreced) {
          sessionState.sttMetrics.correctedTranscripts++;
        }
        
        // Track when we received this transcript (for guard bypass on quick replies)
        sessionState.lastUserTranscriptAt = Date.now();
        
        // ========================================
        // 🎯 CLEAR Q&A FLOW LOGGING FOR DEBUGGING
        // ========================================
        const lastAdaQuestion = sessionState.lastSpokenQuestion || sessionState.transcripts.filter(t => t.role === "assistant").slice(-1)[0]?.text || "(no question)";
        const questionContext = sessionState.lastQuestionType || "unknown";
        const audioMode = sessionState.useRasaAudioProcessing ? "RASA" : "STD";
        const correctionRate = ((sessionState.sttMetrics.correctedTranscripts / sessionState.sttMetrics.totalTranscripts) * 100).toFixed(1);
        
        console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
        console.log(`[${sessionState.callId}] 📥 USER RESPONSE RECEIVED`);
        console.log(`[${sessionState.callId}] ├─ Question asked: "${lastAdaQuestion.substring(0, 80)}${lastAdaQuestion.length > 80 ? '...' : ''}"`);
        console.log(`[${sessionState.callId}] ├─ Question type: ${questionContext.toUpperCase()}`);
        console.log(`[${sessionState.callId}] ├─ Raw STT: "${rawText}"`);
        console.log(`[${sessionState.callId}] ├─ Corrected: "${userText}"${wasCorreced ? ' ⚡CORRECTED' : ''}`);
        console.log(`[${sessionState.callId}] ├─ Current state: pickup=${sessionState.booking.pickup || 'null'}, dest=${sessionState.booking.destination || 'null'}, pax=${sessionState.booking.passengers ?? 'null'}, time=${sessionState.booking.pickup_time || 'null'}`);
        console.log(`[${sessionState.callId}] └─ STT Stats: [${audioMode}] total=${sessionState.sttMetrics.totalTranscripts}, corrected=${correctionRate}%`);
        console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);

        socket.send(
          JSON.stringify({
            type: "transcript",
            text: userText,
            role: "user",
          })
        );

        // ✅ CALL-ENDED GUARD: Don't process new user input if call is ending
        if (sessionState.callEnded) {
          console.log(`[${sessionState.callId}] 🛑 Ignoring user transcript after call ended: "${userText}"`);
          break;
        }

        if (userText) {
          // Use the actual speech start time (from input_audio_buffer.speech_started) as the timestamp.
          // This prevents the dashboard transcript from looking “out of order” when STT arrives late.
          const userTimestampMs =
            typeof sessionState.speechStartTime === "number" && sessionState.speechStartTime > 0
              ? sessionState.speechStartTime
              : Date.now();

          sessionState.transcripts.push({
            role: "user",
            text: userText,
            timestamp: new Date(userTimestampMs).toISOString(),
          });

          // Clear speech timing markers after we’ve turned them into a transcript timestamp.
          sessionState.speechStartTime = null;
          sessionState.speechStopTime = null;

          // Schedule batched flush - don't block voice flow
          scheduleTranscriptFlush(sessionState);

          // Reset booking confirmation flag on new user turn
          // (Ada must call book_taxi again to be allowed to say "Booked!")
          sessionState.bookingConfirmedThisTurn = false;

          const lowerUserText = userText.toLowerCase();

          // === ADDRESS VS PASSENGER DETECTION ===
          // Detect when user gives an address when Ada asked about passengers
          // This happens when user mishears or answers out of order
          const questionAge = sessionState.lastQuestionAt 
            ? Date.now() - sessionState.lastQuestionAt 
            : Infinity;
          const isRecentQuestion = questionAge < 30000; // Within 30 seconds
          
          // Patterns that indicate an address (not a number)
          const isAddressLike = /\b(street|road|avenue|lane|drive|close|way|place|court|crescent|terrace|park|square|row|gardens|grove|hill|view|rd|st|ave|ln|dr|cl|pl|ct)\b/i.test(lowerUserText) ||
            /^\d+[a-z]?\s+[a-z]/i.test(lowerUserText.trim()) || // "52A David Road" pattern
            /\b(near|opposite|outside|by the|next to|corner of|behind)\b/i.test(lowerUserText);
          
          // Patterns that indicate a passenger count
          const isPassengerCount = /^(one|two|three|four|five|six|seven|eight|1|2|3|4|5|6|7|8)\.?$/i.test(lowerUserText.trim()) ||
            /\b(just me|myself|alone|one person|two people|three people|four people|us|passengers?)\b/i.test(lowerUserText);
          
          // NEW: Detect place names (single capitalized words) that are NOT valid passenger answers
          // e.g., "Wolframpton", "Birmingham", "Heathrow" - these look like destinations, not passenger counts
          const trimmedText = userText.trim();
          const looksLikePlaceName = (
            /^[A-Z][a-z]{3,}(ton|ham|wich|pool|field|ford|bury|bridge|port|mouth|gate|worth|ley|chester|cester|borough|wood)$/i.test(trimmedText) || // UK place name patterns
            /^(the\s+)?[A-Z][a-z]+\s*(airport|station|hospital|centre|center|mall|park|hotel)$/i.test(trimmedText) // "Heathrow Airport", "The Station"
          ) && !isPassengerCount;
          
          // Prevent double-asking passengers due to mismatch loop
          const recentPassengerMismatchInjection = sessionState.lastPassengerMismatchAt && 
            (Date.now() - sessionState.lastPassengerMismatchAt < 15000); // 15 second cooldown
          
          if (isRecentQuestion && sessionState.lastQuestionType === "passengers" && (isAddressLike || looksLikePlaceName) && !isPassengerCount && !recentPassengerMismatchInjection) {
            console.log(`[${sessionState.callId}] 🔄 ADDRESS-VS-PASSENGER MISMATCH: User gave ${looksLikePlaceName ? 'place name' : 'address'} "${userText}" when asked about passengers`);
            
            // Store the address as potential destination correction
            const mismatchedAddress = userText;
            
            // Mark that we just injected a mismatch prompt to prevent loop
            sessionState.lastPassengerMismatchAt = Date.now();
            
            // Prevent any auto-VAD response from leaking partial speech while we force the mismatch prompt.
            // IMPORTANT: Only discard audio if we actually cancelled an in-flight response. Otherwise,
            // discardCurrentResponseAudio could stay stuck "true" (no response.done to reset), causing silence.
            if (sessionState.openAiResponseActive) {
              safeCancel(sessionState, "address-vs-passenger-mismatch");
            } else {
              sessionState.discardCurrentResponseAudio = false;
            }

            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];

            // Clear audio buffer so the next turn starts cleanly.
            if (openaiWs && openaiConnected) {
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            }
            
            // Inject system message to handle the mismatch gracefully
            if (openaiWs && openaiConnected && !sessionState.callEnded) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{
                    type: "input_text",
                    text: `[SYSTEM: The user said "${mismatchedAddress}" but you asked about passengers. Note this info and ask ONLY: "How many people will be travelling?". NO filler words, NO "Got it".]`,
                  }],
                },
              }));
              
              sessionState.lastQuestionType = "passengers"; // Still need passengers
              sessionState.lastQuestionAt = Date.now();
              safeResponseCreate(sessionState, "address-vs-passenger-mismatch");
            }
            
            break; // Skip normal processing for this transcript
          }

          // === CONTEXT-AWARE STATE EXTRACTION (AUTHORITATIVE) ===
          // When Ada asks a question, save the user's answer directly to state.
          // This is the SOURCE OF TRUTH - OpenAI tool calls cannot override it.
          // This fixes issues like "Exmoor Road" (caller name) being used instead of what user actually said.
          // Use the SNAPSHOT from speech_started, not current lastQuestionType
          // This fixes race conditions where Ada advances to next step before transcript arrives
          const questionType = sessionState.questionTypeAtSpeechStart || sessionState.lastQuestionType;
          
          if (isRecentQuestion && questionType && !sessionState.callEnded) {
            
            // === PICKUP EXTRACTION ===
            if (questionType === "pickup" && isAddressLike && !sessionState.booking.pickup) {
              const extractedPickup = correctTranscript(userText);
              console.log(`[${sessionState.callId}] 📍 AUTHORITATIVE PICKUP: "${extractedPickup}" (from transcript, question was pickup)`);
              sessionState.booking.pickup = extractedPickup;
              sessionState.transcriptExtractedPickup = extractedPickup; // Track what transcript said
              
              // Sync to DB immediately
              supabase.from("live_calls").update({ 
                pickup: extractedPickup, 
                updated_at: new Date().toISOString() 
              }).eq("call_id", sessionState.callId).then(() => {
                console.log(`[${sessionState.callId}] ✅ Pickup saved to DB: "${extractedPickup}"`);
              });
              
              // Advance to destination - let OpenAI's natural VAD handle the response timing
              sessionState.lastQuestionType = "destination";
              sessionState.bookingStep = "destination";
              sessionState.lastQuestionAt = Date.now();
              
              // ✅ DO NOT force safeResponseCreate - let OpenAI VAD handle response timing naturally
              // This prevents aggressive prompting where Ada asks next question before user finishes speaking
              console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
              console.log(`[${sessionState.callId}] ✅ ANSWER ACCEPTED: PICKUP`);
              console.log(`[${sessionState.callId}] ├─ Value: "${extractedPickup}"`);
              console.log(`[${sessionState.callId}] ├─ Next question: DESTINATION`);
              console.log(`[${sessionState.callId}] └─ Waiting for VAD to trigger Ada's response...`);
              console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
            }
            
            // === DESTINATION EXTRACTION ===
            else if (questionType === "destination" && isAddressLike && !sessionState.booking.destination) {
              const extractedDest = correctTranscript(userText);
              
              // === DUPLICATE ADDRESS GUARD ===
              // Prevent saving destination that's identical to pickup (common STT/extraction error)
              const normalizeForCompare = (s: string | null) => (s || "").toLowerCase().replace(/[.,\s]+/g, " ").trim();
              const normalizedPickup = normalizeForCompare(sessionState.booking.pickup);
              const normalizedDest = normalizeForCompare(extractedDest);
              
              if (normalizedPickup && normalizedDest && normalizedPickup === normalizedDest) {
                console.log(`[${sessionState.callId}] ⚠️ DUPLICATE ADDRESS BLOCKED: destination "${extractedDest}" === pickup "${sessionState.booking.pickup}"`);
                // Don't save - ask for destination again
                if (openaiWs && openaiConnected && !sessionState.callEnded) {
                  openaiWs.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: The user repeated their pickup address. Ask ONLY: "And where would you like to go to?" Do NOT repeat the pickup address back.]`,
                      }],
                    },
                  }));
                  safeResponseCreate(sessionState, "duplicate-address-guard");
                }
              } else {
                console.log(`[${sessionState.callId}] 📍 AUTHORITATIVE DESTINATION: "${extractedDest}" (from transcript, question was destination)`);
                sessionState.booking.destination = extractedDest;
                sessionState.transcriptExtractedDestination = extractedDest; // Track what transcript said
                
                // Sync to DB immediately (including booking_step for session restoration)
                supabase.from("live_calls").update({ 
                  destination: extractedDest,
                  booking_step: "passengers",
                  updated_at: new Date().toISOString() 
                }).eq("call_id", sessionState.callId).then(() => {
                  console.log(`[${sessionState.callId}] ✅ Destination + step saved to DB: "${extractedDest}", step=passengers`);
                });
                
                // Advance to passengers - let OpenAI's natural VAD handle the response timing
                sessionState.lastQuestionType = "passengers";
                sessionState.bookingStep = "passengers";
                sessionState.lastQuestionAt = Date.now();
                
                // ✅ DO NOT force safeResponseCreate - let OpenAI VAD handle response timing naturally
                // This prevents aggressive prompting where Ada asks next question before user finishes speaking
                console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
                console.log(`[${sessionState.callId}] ✅ ANSWER ACCEPTED: DESTINATION`);
                console.log(`[${sessionState.callId}] ├─ Value: "${extractedDest}"`);
                console.log(`[${sessionState.callId}] ├─ Next question: PASSENGERS`);
                console.log(`[${sessionState.callId}] └─ Waiting for VAD to trigger Ada's response...`);
                console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
              }
            }
            
            // === PASSENGER EXTRACTION ===
            else if (questionType === "passengers" && isPassengerCount) {
              // Parse passenger count from text
              const passengerMap: Record<string, number> = {
                "one": 1, "1": 1, "just me": 1, "myself": 1, "alone": 1, "one person": 1,
                "two": 2, "2": 2, "two people": 2, "us": 2,
                "three": 3, "3": 3, "three people": 3,
                "four": 4, "4": 4, "four people": 4,
                "five": 5, "5": 5, "five people": 5,
                "six": 6, "6": 6, "six people": 6,
                "seven": 7, "7": 7, "eight": 8, "8": 8
              };
              
              const lowerText = userText.toLowerCase().trim();
              let extractedPax: number | null = null;
              
              // Try exact match first
              if (passengerMap[lowerText]) {
                extractedPax = passengerMap[lowerText];
              } else {
                // Try to find any passenger keyword
                for (const [key, val] of Object.entries(passengerMap)) {
                  if (lowerText.includes(key)) {
                    extractedPax = val;
                    break;
                  }
                }
              }
              
              // ✅ Allow updates even if passengers already set (for corrections like "Four now")
              if (extractedPax) {
                const wasUpdate = sessionState.booking.passengers !== null;
                console.log(`[${sessionState.callId}] 👥 AUTHORITATIVE PASSENGERS: ${extractedPax} (from transcript)${wasUpdate ? ` [UPDATE from ${sessionState.booking.passengers}]` : ""}`);
                sessionState.booking.passengers = extractedPax;
                sessionState.transcriptExtractedPassengers = extractedPax;
                
                // Sync to DB (including booking_step for session restoration)
                supabase.from("live_calls").update({ 
                  passengers: extractedPax,
                  booking_step: "time",
                  updated_at: new Date().toISOString() 
                }).eq("call_id", sessionState.callId).then(() => {
                  console.log(`[${sessionState.callId}] ✅ Passengers + step saved to DB: ${extractedPax}, step=time`);
                });
                
                // Advance to time - let OpenAI's natural VAD handle the response timing
                sessionState.lastQuestionType = "time";
                sessionState.bookingStep = "time";
                sessionState.lastQuestionAt = Date.now();
                
                // ✅ DO NOT force safeResponseCreate - let OpenAI VAD handle response timing naturally
                // This prevents aggressive prompting where Ada asks next question before user finishes speaking
                console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
                console.log(`[${sessionState.callId}] ✅ ANSWER ACCEPTED: PASSENGERS`);
                console.log(`[${sessionState.callId}] ├─ Value: ${extractedPax}`);
                console.log(`[${sessionState.callId}] ├─ Next question: TIME`);
                console.log(`[${sessionState.callId}] └─ Waiting for VAD to trigger Ada's response...`);
                console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
              }
            }
            
            // === TIME EXTRACTION ===
            else if (questionType === "time") {
              const lowerText = userText.toLowerCase().trim();
              const isTimeResponse = /\b(now|asap|immediately|right away|straight away|in \d+|at \d+|\d+ ?o'clock|\d+ ?am|\d+ ?pm|morning|afternoon|evening|tonight|tomorrow)\b/i.test(lowerText);
              
              if (isTimeResponse && !sessionState.booking.pickup_time) {
                const extractedTime = lowerText.includes("now") || lowerText.includes("asap") || lowerText.includes("immediately") 
                  ? "ASAP" 
                  : userText;
                console.log(`[${sessionState.callId}] 🕐 AUTHORITATIVE TIME: "${extractedTime}" (from transcript)`);
                sessionState.booking.pickup_time = extractedTime;
                sessionState.transcriptExtractedTime = extractedTime;
                
                // Advance to summary - for summary we DO want to trigger immediately since all fields are collected
                sessionState.lastQuestionType = "confirmation";
                sessionState.bookingStep = "summary";
                sessionState.lastQuestionAt = Date.now();
                
                // ✅ CRITICAL: Flush to DB immediately so session restoration works if disconnect happens
                immediateFlush(sessionState);
                console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
                console.log(`[${sessionState.callId}] ✅ ANSWER ACCEPTED: TIME`);
                console.log(`[${sessionState.callId}] ├─ Value: "${extractedTime}"`);
                console.log(`[${sessionState.callId}] ├─ 🎉 ALL FIELDS COLLECTED!`);
                console.log(`[${sessionState.callId}] ├─ Pickup: ${sessionState.booking.pickup}`);
                console.log(`[${sessionState.callId}] ├─ Destination: ${sessionState.booking.destination}`);
                console.log(`[${sessionState.callId}] ├─ Passengers: ${sessionState.booking.passengers}`);
                console.log(`[${sessionState.callId}] ├─ Time: ${extractedTime}`);
                console.log(`[${sessionState.callId}] └─ Moving to SUMMARY...`);
                console.log(`[${sessionState.callId}] ════════════════════════════════════════════`);
                
                // ✅ For summary, we DO trigger response since all fields are collected and we want to move forward
                // This is different from pickup/destination/passengers where we let VAD handle timing
                if (openaiWs && openaiConnected && !sessionState.callEnded) {
                  const pickup = sessionState.booking.pickup || "pickup";
                  const destination = sessionState.booking.destination || "destination";
                  const passengers = sessionState.booking.passengers || 1;
                  const time = extractedTime;
                  
                  openaiWs.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{ type: "input_text", text: `[ALL DETAILS VERIFIED] Now give the summary EXACTLY: "Alright, let me quickly summarize your booking. You'd like to be picked up at ${pickup}, and travel to ${destination}. There will be ${passengers} passengers, and you'd like to be picked up ${time}. Is that correct?"` }]
                    }
                  }));
                  safeResponseCreate(sessionState, "authoritative-time-trigger-summary");
                }
              }
            }
          }

          // === SUMMARY NEGATION DETECTION (ported from paired mode) ===
          // When user says "No" during summary confirmation, they want to correct something
          // Don't treat it as a goodbye - instead reset the summary and ask what's wrong
          const isSummaryPhase = sessionState.bookingStep === "summary" || sessionState.lastQuestionType === "confirmation";
          const isSimpleNegation = isSummaryNegation(userText);
          
          if (isSummaryPhase && isSimpleNegation && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] ❌ SUMMARY NEGATION: User said "${userText}" during summary - asking what needs correcting`);
            
            // Cancel any in-flight response
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            sessionState.discardCurrentResponseAudio = true;
            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];
            
            // Inject correction prompt
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: `[SYSTEM: The customer said "No" to the summary. Say ONLY: "I'm sorry about that. What would you like me to change?" Then STOP and wait for their answer. Do NOT repeat the booking details.]`,
                }],
              },
            }));
            
            // Stay in summary phase but clear the confirmation question type to allow corrections
            sessionState.lastQuestionType = null;
            safeResponseCreate(sessionState, "summary-negation-correction");
            
            break; // Skip normal processing
          }
          
          // === ADDRESS CORRECTION DETECTION (ported from paired mode) ===
          // Detect phrases like "No, it's...", "Actually...", "The pickup is..."
          const addressCorrection = detectAddressCorrection(userText, sessionState.booking.pickup, sessionState.booking.destination);
          
          if (addressCorrection.type && addressCorrection.address && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] 🔧 ADDRESS CORRECTION DETECTED: ${addressCorrection.type} = "${addressCorrection.address}"`);
            
            // Apply STT correction to the extracted address
            const correctedAddress = correctTranscript(addressCorrection.address);
            
            // Update booking state
            const oldValue = addressCorrection.type === "pickup" ? sessionState.booking.pickup : sessionState.booking.destination;
            if (addressCorrection.type === "pickup") {
              sessionState.booking.pickup = correctedAddress;
            } else {
              sessionState.booking.destination = correctedAddress;
            }
            
            console.log(`[${sessionState.callId}] ✅ Updated ${addressCorrection.type}: "${oldValue}" → "${correctedAddress}"`);
            
            // Sync to database
            const updatePayload: Record<string, any> = { updated_at: new Date().toISOString() };
            if (addressCorrection.type === "pickup") {
              updatePayload.pickup = correctedAddress;
            } else {
              updatePayload.destination = correctedAddress;
            }
            supabase.from("live_calls").update(updatePayload).eq("call_id", sessionState.callId).then(() => {
              console.log(`[${sessionState.callId}] ✅ live_calls updated with ${addressCorrection.type} correction`);
            });
            
            // Cancel any in-flight response
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            sessionState.discardCurrentResponseAudio = true;
            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];
            
            // Inject acknowledgment and continue flow
            const fieldLabel = addressCorrection.type === "pickup" ? "pickup" : "destination";
            const nextQuestion = sessionState.booking.pickup && sessionState.booking.destination 
              ? (sessionState.booking.passengers ? "When do you need the taxi?" : "How many people will be travelling?")
              : (addressCorrection.type === "pickup" ? "And what is your destination?" : "Where would you like to be picked up?");
            
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: `[SYSTEM: The customer corrected the ${fieldLabel} to "${correctedAddress}". Updated ${fieldLabel} is now "${correctedAddress}". Ask ONLY: "${nextQuestion}" No filler words.]`,
                }],
              },
            }));
            
            // Update question tracking
            if (nextQuestion.includes("destination")) {
              sessionState.lastQuestionType = "destination";
            } else if (nextQuestion.includes("people") || nextQuestion.includes("passengers")) {
              sessionState.lastQuestionType = "passengers";
            } else if (nextQuestion.includes("when") || nextQuestion.includes("taxi")) {
              sessionState.lastQuestionType = "time";
            }
            sessionState.lastQuestionAt = Date.now();
            
            safeResponseCreate(sessionState, "address-correction");
            
            break; // Skip normal processing
          }

          // === EXPLICIT BYE/GOODBYE DETECTION (HIGHEST PRIORITY) ===
          // If user says "bye" (even multiple times), they want to end the call immediately.
          // This takes priority over fare confirmation, booking prompts, etc.
          // 
          // IMPORTANT: "No thanks" / "nothing else" phrases should ONLY trigger goodbye
          // AFTER Ada has asked "Is there anything else I can help you with?" (askedAnythingElse=true).
          // Otherwise, "no thanks" might mean rejecting a fare quote, not ending the call.
          // IMPORTANT: Enforce 3-second grace period after "anything else?" to let users actually respond
          // 
          // NEW: Hard goodbye (bye/goodbye) should ONLY end the call if:
          // 1. A booking has been confirmed (bookingConfirmed=true), OR
          // 2. Ada has asked "Is there anything else?" (askedAnythingElse=true)
          // Otherwise, redirect user back to complete the booking.
          const isHardGoodbye = /\b(bye|goodbye|see ya|see you|cya|i'm done|im done|hang up|end call)\b/i.test(lowerUserText);
          const isSoftGoodbye = /\b(no thank you|no thanks|no that's all|no thats all|nothing else|that's it|thats it|that'll be all|thatll be all|i'm good|im good|all good|all done)\b/i.test(lowerUserText) ||
            // Special case: just "no" or "no." when asked "anything else?" - user is declining
            (sessionState.askedAnythingElse && /^no\.?$/i.test(lowerUserText.trim()));
          
          // Check if grace period has passed since "anything else?" was asked
          // Uses configurable goodbyeGraceMs from agent settings (default 3000ms)
          const gracePeriodMs = sessionState.goodbyeGraceMs || 3000;
          const enoughTimeElapsed = !sessionState.askedAnythingElseAt || 
            (Date.now() - sessionState.askedAnythingElseAt > gracePeriodMs);
          
          // === NEW GUARD: Prevent premature goodbye if booking incomplete ===
          // A booking is "complete enough" to end the call if:
          // - bookingConfirmed=true (dispatch confirmed), OR
          // - askedAnythingElse=true (Ada already asked follow-up question)
          const bookingCompleteEnough = sessionState.bookingFullyConfirmed || sessionState.askedAnythingElse;
          
          // Hard goodbye now requires bookingCompleteEnough to prevent premature termination
          // Soft goodbye still requires askedAnythingElse (unchanged)
          const isExplicitGoodbye = ((isHardGoodbye && bookingCompleteEnough) || (isSoftGoodbye && sessionState.askedAnythingElse && enoughTimeElapsed)) &&
            // Exclude "bye" in compound phrases like "going to the airport" (unlikely but guard against)
            !/going to|from|pick ?up|drop ?off/i.test(lowerUserText);
          
          // === NEW: Handle premature goodbye attempt (user says bye before booking complete) ===
          if (isHardGoodbye && !bookingCompleteEnough && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] 👋 Premature goodbye detected: "${userText}" - booking not complete, redirecting user`);
            
            // Cancel any in-flight assistant speech
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            // Ask if they want to continue or cancel
            openaiWs.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: `[SYSTEM: The customer said goodbye but hasn't completed their booking. Say EXACTLY: "Before you go, would you like me to complete your taxi booking? I just need a few more details."]`,
                    },
                  ],
                },
              })
            );
            
            openaiWs.send(JSON.stringify({ type: "response.create" }));
            break; // Skip normal processing
          }
          
          if (isExplicitGoodbye && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] 👋 Explicit goodbye detected: "${userText}" (hardGoodbye=${isHardGoodbye}, softGoodbye=${isSoftGoodbye}, gracePeriodPassed=${enoughTimeElapsed}, bookingConfirmed=${sessionState.bookingFullyConfirmed}) - ending call`);
            
            // ✅ CRITICAL: Mark call as ending IMMEDIATELY to block all further processing
            sessionState.callEnded = true;
            
            // Clear pendingQuote to prevent any fare prompts from being processed
            sessionState.pendingQuote = null;

            // Cancel any in-flight assistant speech to avoid overlap
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

            // Allow exactly one final goodbye response to play even though callEnded=true
            sessionState.finalGoodbyePending = true;
            sessionState.discardCurrentResponseAudio = false;
            sessionState.audioVerified = true;
            sessionState.pendingAudioBuffer = [];

            // Pick a random WhatsApp tip
            const whatsappTips = [
              "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
              "Next time, feel free to book your taxi using a WhatsApp voice message.",
              "You can always book again by simply sending us a voice note on WhatsApp."
            ];
            const randomTip = whatsappTips[Math.floor(Math.random() * whatsappTips.length)];
            
            // Different closing message based on whether a booking was made
            const closingMessage = sessionState.bookingFullyConfirmed 
              ? `You'll receive the booking details and ride updates via WhatsApp. ${randomTip} Thank you for trying the Taxibot demo, and have a safe journey.`
              : `${randomTip} Thank you for trying the Taxibot demo. Goodbye!`;

            openaiWs.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: `[SYSTEM: The customer is done. Say EXACTLY this closing message, then stay silent:
"${closingMessage}"]`,
                    },
                  ],
                },
              })
            );

            openaiWs.send(JSON.stringify({ type: "response.create" }));
            
            // === GRACEFUL CLOSURE: Set flag to prevent socket.onclose from interrupting ===
            closingGracePeriodActive = true;
            console.log(`[${sessionState.callId}] 🛡️ Closing grace period ACTIVE - socket.onclose will wait`);
            
            // Close OpenAI connection after extended delay to let full goodbye audio finish,
            // then tell the bridge to hang up AND close the bridge WebSocket with a proper close frame.
            closingGraceTimeoutId = setTimeout(() => {
              console.log(`[${sessionState.callId}] 🔌 Closing connections after explicit goodbye`);
              closingGracePeriodActive = false;
              
              // Stop keep-alives immediately (we are ending the session)
              try {
                stopKeepAlive();
              } catch {
                // ignore
              }
              
              // Notify the bridge
              try {
                socket.send(JSON.stringify({ type: "hangup", reason: "customer_goodbye" }));
                console.log(`[${sessionState.callId}] 📤 Sent hangup to bridge`);
              } catch (e) {
                console.warn(`[${sessionState.callId}] ⚠️ Failed to send hangup to bridge:`, e);
              }
              
              // Close the bridge WebSocket gracefully (send close frame)
              try {
                isConnectionClosed = true;
                socket.close(1000, "customer_goodbye");
                console.log(`[${sessionState.callId}] 📴 Closed bridge WebSocket (1000 customer_goodbye)`);
              } catch (e) {
                console.warn(`[${sessionState.callId}] ⚠️ Failed to close bridge WebSocket:`, e);
              }
              
              // Close OpenAI WS
              try {
                openaiWs?.close();
              } catch {
                // ignore
              }
            }, 10000) as unknown as number; // 10 second delay for full goodbye audio with WhatsApp tip
            
            break;
          }

          // === POST-BOOKING GOODBYE SHORTCUT (SECONDARY) ===
          // If a booking was just confirmed and the caller says thanks/that's all, end cleanly.
          // This prevents Ada from accidentally starting a new quote from stale booking context.
          // 
          // IMPORTANT: If Ada just asked a question (ends with ?), we must wait for the user's
          // actual response before triggering goodbye. "No" might be the answer to Ada's question
          // (e.g., "Would you like to book a taxi to one of these events?")
          const adaRecentlyAskedQuestion = sessionState.adaAskedQuestionAt && 
            (Date.now() - sessionState.adaAskedQuestionAt < 30000); // 30 second window
          
          if (
            sessionState.lastBookTaxiSuccessAt &&
            Date.now() - sessionState.lastBookTaxiSuccessAt < 2 * 60 * 1000 &&
            !sessionState.pendingQuote &&
            !adaRecentlyAskedQuestion && // Don't shortcut if Ada just asked a question!
            /\b(thanks|thank you|thx|cheers|that's fine|thats fine|that's all|thats all|no thanks|no thank you|no|nope|nothing|nothing else|no i'm good|no im good|i'm good|im good|all good|that's it|thats it)\b/i.test(lowerUserText) &&
            openaiWs &&
            openaiConnected
          ) {
            console.log(`[${sessionState.callId}] 👋 Caller finished after booking - triggering end_call`);
            
            // ✅ CRITICAL: Mark call as ending IMMEDIATELY to block all further processing
            sessionState.callEnded = true;

            // Cancel any in-flight assistant speech to avoid overlap
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

            // Allow exactly one final goodbye response to play even though callEnded=true
            sessionState.finalGoodbyePending = true;
            sessionState.discardCurrentResponseAudio = false;
            sessionState.audioVerified = true;
            sessionState.pendingAudioBuffer = [];

            // Pick a random WhatsApp tip
            const whatsappTips = [
              "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
              "Next time, feel free to book your taxi using a WhatsApp voice message.",
              "You can always book again by simply sending us a voice note on WhatsApp."
            ];
            const randomTip = whatsappTips[Math.floor(Math.random() * whatsappTips.length)];

            openaiWs.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: `[SYSTEM: The customer is done. Say EXACTLY this closing message, then stay silent:
"You'll receive the booking details and ride updates via WhatsApp. ${randomTip} Thank you for trying the Taxibot demo, and have a safe journey."]`,
                    },
                  ],
                },
              })
            );

            openaiWs.send(JSON.stringify({ type: "response.create" }));
            
            // === GRACEFUL CLOSURE: Set flag to prevent socket.onclose from interrupting ===
            closingGracePeriodActive = true;
            console.log(`[${sessionState.callId}] 🛡️ Closing grace period ACTIVE - socket.onclose will wait`);
            
            // Close OpenAI connection after extended delay to let full goodbye audio finish,
            // then tell the bridge to hang up AND close the bridge WebSocket with a proper close frame.
            closingGraceTimeoutId = setTimeout(() => {
              console.log(`[${sessionState.callId}] 🔌 Closing connections after post-booking goodbye`);
              closingGracePeriodActive = false;
              
              // Stop keep-alives immediately (we are ending the session)
              try {
                stopKeepAlive();
              } catch {
                // ignore
              }
              
              // Notify the bridge
              try {
                socket.send(JSON.stringify({ type: "hangup", reason: "post_booking_goodbye" }));
                console.log(`[${sessionState.callId}] 📤 Sent hangup to bridge`);
              } catch (e) {
                console.warn(`[${sessionState.callId}] ⚠️ Failed to send hangup to bridge:`, e);
              }
              
              // Close the bridge WebSocket gracefully (send close frame)
              try {
                isConnectionClosed = true;
                socket.close(1000, "post_booking_goodbye");
                console.log(`[${sessionState.callId}] 📴 Closed bridge WebSocket (1000 post_booking_goodbye)`);
              } catch (e) {
                console.warn(`[${sessionState.callId}] ⚠️ Failed to close bridge WebSocket:`, e);
              }
              
              // Close OpenAI WS
              try {
                openaiWs?.close();
              } catch {
                // ignore
              }
            }, 10000) as unknown as number; // 10 second delay for full goodbye audio with WhatsApp tip
            
            break;
          }
          
          // Log when we skip goodbye shortcut because Ada asked a question
          if (adaRecentlyAskedQuestion && 
              sessionState.lastBookTaxiSuccessAt &&
              /\b(no|nope|nothing|no thanks)\b/i.test(lowerUserText)) {
            console.log(`[${sessionState.callId}] ⏳ Ada asked a question recently - forwarding user response to AI instead of triggering goodbye`);
            // Clear the question tracker so subsequent "no" can trigger goodbye
            sessionState.adaAskedQuestionAt = null;
          }

          // === FARE YES/NO HANDOFF (CRITICAL) ===
          // If dispatch already gave us a fare quote (pendingQuote), we must turn the caller's yes/no
          // into an explicit book_taxi tool call with confirmation_state confirmed/rejected.
          if (sessionState.pendingQuote && openaiWs && openaiConnected) {
            const isYesToFare = /\b(yes|yeah|yep|yup|go ahead|book it|do it|please do|sure|okay|ok|alright|sounds good|correct|that's right|thats right|that's correct|thats correct|right|confirm|confirmed|i confirm|i'll confirm|ill confirm|please|proceed|do it then|go on then|ja|jawel|doe maar|prima|akkoord|oui|d'accord|si|sí|claro|vale)\b/i.test(lowerUserText);
            const isNoToFare = /\b(no|nope|don't|do not|dont|cancel|stop|never mind|nevermind|not now|nah|too expensive|nee|niet|annuleer|non)\b/i.test(lowerUserText);

            if (isYesToFare || isNoToFare) {
              const pq = sessionState.pendingQuote;
              const pickup = pq.pickup || sessionState.booking.pickup;
              const destination = pq.destination || sessionState.booking.destination;
              const nextState = isYesToFare ? "confirmed" : "rejected";

              console.log(`[${sessionState.callId}] ✅ Fare decision detected: ${nextState} (pickup="${pickup}", destination="${destination}")`);

              // CRITICAL: Clear pendingQuote IMMEDIATELY to prevent duplicate ask_confirm broadcasts
              // from being processed while we're waiting for Ada to call book_taxi
              // (The pendingQuote will be temporarily restored by the book_taxi handler if needed)
              const savedQuote = { ...pq };
              // DON'T clear pendingQuote here - the book_taxi("confirmed") handler needs it!
              // Instead, just log that we're processing the decision
              
               // Prevent any auto-VAD response from leaking partial speech while we force the tool call.
               sessionState.discardCurrentResponseAudio = true;
               sessionState.audioVerified = false;
               sessionState.pendingAudioBuffer = [];

               // Cancel anything already in-flight and clear audio to avoid competing responses.
               // (Even if no response is active, cancel is safe and keeps the flow deterministic.)
               openaiWs.send(JSON.stringify({ type: "response.cancel" }));
               openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

               openaiWs.send(JSON.stringify({
                 type: "conversation.item.create",
                 item: {
                   type: "message",
                   role: "user",
                   content: [
                     {
                       type: "input_text",
                       text:
                         `[SYSTEM: The customer has answered the fare question. You MUST call book_taxi NOW with confirmation_state: "${nextState}" using pickup: "${pickup}" and destination: "${destination}". DO NOT say "booked" or confirm anything out loud. Do NOT re-read the fare. After the tool succeeds, ask "Is there anything else I can help you with?" and then WAIT.]`,
                     },
                   ],
                 },
               }));

               safeResponseCreate(sessionState, "fare-decision-handoff");

               break;
            }
          }
          
          // ═══════════════════════════════════════════════════════════════════════════
          // RECONCILIATION ENGINE: CONFIRMATION INTENT DETECTION
          // Scans transcripts for confirmation keywords and forces action if detected
          // ═══════════════════════════════════════════════════════════════════════════
          
          // Comprehensive confirmation phrases (including multi-word)
          const confirmationPhrases = [
            "correct", "that's correct", "that is correct", "thats correct",
            "go ahead", "do it", "book it", "yes", "yeah", "yep", "yup",
            "that's fine", "sounds good", "perfect", "confirm", "confirmed",
            "right", "that's right", "sure", "ok", "okay", "please",
            "straight away", "straightaway", "right away", "now", "asap", "immediately",
            // Dutch
            "ja", "jawel", "prima", "goed", "akkoord", "doe maar",
            // German  
            "naturlich", "klar", "genau",
            // French
            "oui", "d'accord",
            // Spanish
            "sí", "claro", "correcto"
          ];
          
          // Check if user is confirming
          const isConfirmationPhrase = confirmationPhrases.some(phrase => 
            lowerUserText.includes(phrase)
          );
          
          // Log confirmation detection for debugging
          if (isConfirmationPhrase) {
            console.log(`[${sessionState.callId}] 🔍 RECONCILIATION: Confirmation phrase detected in "${userText}"`);
          }

          // If the user says "yes" in response to a CONFIRMATION question (summary confirm or fare confirm),
          // we may need to buffer bridge audio while we force tool calls.
          // IMPORTANT: Do NOT enter buffering on random "yes/ok" answers (e.g., passengers/time),
          // or we can create dead-air and trigger telephony hangups.
          if (
            isConfirmationPhrase &&
            sessionState.lastQuestionType === "confirmation" &&
            !sessionState.bookingConfirmedThisTurn &&
            sessionState.booking.version === 0
          ) {
            console.log(`[${sessionState.callId}] 🔒 Confirmation detected - starting audio buffering until tool handoff completes`);
            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];

            // RELAXED CHECKLIST: Only require pickup, destination, passengers
            // pickup_time defaults to ASAP if missing (most callers want immediate pickup)
            const hasMinimalChecklist =
              !!sessionState.booking?.pickup &&
              !!sessionState.booking?.destination &&
              sessionState.booking?.passengers !== null;

            const quoteAlreadyInProgress =
              !!sessionState.quoteRequestedAt ||
              !!sessionState.pendingQuote ||
              !!sessionState.awaitingDispatchCallback;

            if (
              hasMinimalChecklist &&
              !quoteAlreadyInProgress &&
              openaiWs &&
              openaiConnected &&
              !sessionState.callEnded
            ) {
              // Default pickup_time to ASAP if not set (relaxed checklist)
              if (!sessionState.booking.pickup_time) {
                console.log(`[${sessionState.callId}] ⏰ RECONCILIATION: No pickup_time set, defaulting to ASAP`);
                sessionState.booking.pickup_time = "ASAP";
              }
              
              // Ensure state machine doesn't get stuck pre-summary due to extraction timing.
              if (sessionState.bookingStep !== "summary") {
                console.log(`[${sessionState.callId}] 🧭 Step sync: ${sessionState.bookingStep} → summary (confirmed by user)`);
                sessionState.bookingStep = "summary";
                sessionState.bookingStepAdvancedAt = Date.now();
              }

              console.log(`[${sessionState.callId}] 🔄 AUTO-HANDOFF: User confirmed summary → forcing book_taxi(request_quote)`);
              console.log(`[${sessionState.callId}] 📋 Booking: pickup="${sessionState.booking.pickup}", dest="${sessionState.booking.destination}", pax=${sessionState.booking.passengers}, time="${sessionState.booking.pickup_time}"`);

              // Ensure nothing else is speaking; keep the flow deterministic.
              sessionState.discardCurrentResponseAudio = true;
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              }
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

              openaiWs.send(
                JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [
                      {
                        type: "input_text",
                        text:
                          `[SYSTEM: The customer has confirmed the journey summary. You MUST say EXACTLY: "One moment please." Then you MUST call book_taxi immediately with action: "request_quote". Do not ask any more questions. Do not repeat the summary.]`,
                      },
                    ],
                  },
                })
              );

              safeResponseCreate(sessionState, "summary-confirm-auto-quote");
            }
          }

          // Existing booking context for modification logic
          const hasExistingBookingContext =
            sessionState.hasActiveBooking || sessionState.booking.version > 0 || !!sessionState.lastBookTaxiSuccessAt;

          // === NEW BOOKING AUTO-DETECTION (AI-BASED) ===
          // When there's NO existing booking and user provides what sounds like pickup AND destination,
          // extract and ask for confirmation before getting fare quote
          
          // Quick check: does this look like a new booking with both addresses?
          // NOTE: Be conservative here: false positives (e.g. "at 4.30") can cancel Ada mid-summary.
          const hasPickupPhrase = /\b(from|pick\s*up|pickup|outside|near)\b/i.test(lowerUserText);
          const hasDestinationPhrase = /\b(to|going\s+to|destination|drop\s*off|dropoff)\b/i.test(lowerUserText);
          const isSubstantialMessage = lowerUserText.length > 15; // Must be substantial enough to contain addresses

          // Only allow this auto-interrupt when we are early in the flow.
          // If we're collecting passengers/time or summarizing, treat user speech as an answer — do not cancel Ada.
          const allowAutoNewBookingInterrupt = sessionState.bookingStep === "pickup" || sessionState.bookingStep === "destination";

          const mightBeNewBooking = allowAutoNewBookingInterrupt && !hasExistingBookingContext &&
            !isConfirmationPhrase &&
            !sessionState.newBookingPromptPending &&
            !sessionState.extractionInProgress &&
            !sessionState.pendingQuote &&
            isSubstantialMessage &&
            (hasPickupPhrase || hasDestinationPhrase);
          
          if (mightBeNewBooking && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] 🆕 Potential NEW booking detected: "${userText.substring(0, 50)}..." (pickup=${hasPickupPhrase}, dest=${hasDestinationPhrase})`);
            console.log(`[${sessionState.callId}] 🔍 BLOCKING Ada and calling AI extraction for new booking...`);
            
            // === CRITICAL: BLOCK ADA FROM RESPONDING ===
            sessionState.extractionInProgress = true;
            
            // Cancel any in-flight response so Ada doesn't speak with incomplete data
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              sessionState.discardCurrentResponseAudio = true;
              console.log(`[${sessionState.callId}] ⏸️ Cancelled in-flight response for new booking extraction`);
            }
            
            // Clear audio buffer to prevent stale responses
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            // Build conversation history for AI from transcripts
            const conversationForNewBooking = sessionState.transcripts
              .slice(-10)
              .map((turn: TranscriptItem) => ({
                role: turn.role as "user" | "assistant",
                text: turn.text
              }));
            
            conversationForNewBooking.push({ role: "user" as const, text: userText });
            
            // Call AI extraction
            fetch(
              `${Deno.env.get("SUPABASE_URL")}/functions/v1/taxi-extract-unified`,
              {
                method: "POST",
                headers: {
                  "Content-Type": "application/json",
                  "Authorization": `Bearer ${Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")}`,
                },
                body: JSON.stringify({
                  conversation: conversationForNewBooking,
                  current_booking: null, // No existing booking
                  caller_phone: sessionState.phone,
                  is_modification: false,
                }),
              }
            ).then(async (extractResponse) => {
              if (!extractResponse.ok) {
                console.error(`[${sessionState.callId}] ❌ New booking AI extraction failed: ${extractResponse.status}`);
                sessionState.extractionInProgress = false;
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
                return;
              }
              
              const extracted = await extractResponse.json();
              console.log(`[${sessionState.callId}] 🤖 New booking AI extraction result:`, extracted);
              
              // Apply STT corrections to extracted addresses as safeguard
              const rawPickup = extracted.pickup || "";
              const rawDestination = extracted.destination || "";
              const extractedPickup = correctTranscript(rawPickup);
              const extractedDestination = correctTranscript(rawDestination);
              const extractedPassengers = extracted.passengers ?? null;
              const extractedBags = extracted.luggage ? parseInt(extracted.luggage) || 0 : 0;
              
              if (rawPickup !== extractedPickup || rawDestination !== extractedDestination) {
                console.log(`[${sessionState.callId}] 🔧 Corrected extraction: pickup="${rawPickup}"→"${extractedPickup}", dest="${rawDestination}"→"${extractedDestination}"`);
              }
              
              // Helper to detect placeholder values that should NOT be accepted
              const isPlaceholderAddress = (addr: string | null | undefined): boolean => {
                if (!addr) return true;
                const lower = addr.toLowerCase().trim();
                // Reject known placeholders
                if (!lower || 
                    lower === "not set" || 
                    lower === "not specified" || 
                    lower === "unknown" || 
                    lower === "none" || 
                    lower === "n/a" ||
                    lower === "to be confirmed" ||
                    lower === "tbc" ||
                    lower === "your location" || 
                    lower === "your destination") {
                  return true;
                }
                return false;
              };
              
              // Only proceed if we have BOTH pickup AND destination (real addresses, not placeholders)
              const pickupIsPlaceholder = isPlaceholderAddress(extractedPickup);
              const destIsPlaceholder = isPlaceholderAddress(extractedDestination);
              
              if (!extractedPickup || !extractedDestination || pickupIsPlaceholder || destIsPlaceholder) {
                console.log(`[${sessionState.callId}] AI extraction incomplete - pickup="${extractedPickup}" (placeholder=${pickupIsPlaceholder}), dest="${extractedDestination}" (placeholder=${destIsPlaceholder}). Letting Ada ask for missing info.`);
                sessionState.extractionInProgress = false;
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
                return;
              }
              
              // Check if pickup === destination (invalid)
              const normalizeAddr = (s: string) => s.toLowerCase().replace(/[.,\s]+/g, " ").trim();
              if (normalizeAddr(extractedPickup) === normalizeAddr(extractedDestination)) {
                console.log(`[${sessionState.callId}] ❌ AI extraction returned pickup === destination. Asking for clarification.`);
                sessionState.extractionInProgress = false;
                
                setTimeout(() => {
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: The pickup and destination appear to be the same. Ask the customer: "I just need to check - where would you like to be picked up from, and where are you going to?"]`,
                      }],
                    },
                  }));
                  openaiWs?.send(JSON.stringify({ type: "response.create" }));
                }, 300);
                return;
              }
              
              console.log(`[${sessionState.callId}] ✅ Valid new booking extracted: pickup="${extractedPickup}", dest="${extractedDestination}"`);
              
              // Update sessionState.booking with extracted values
              sessionState.booking.pickup = extractedPickup;
              sessionState.booking.destination = extractedDestination;
              sessionState.booking.passengers = extractedPassengers;
              sessionState.booking.bags = extractedBags;
              
              // ✅ ADVANCE BOOKING STEP based on what we captured
              // Skip to the furthest complete step
              if (extractedPickup) {
                sessionState.bookingStep = "destination";
                sessionState.bookingStepAdvancedAt = Date.now();
              }
              if (extractedDestination) {
                sessionState.bookingStep = "passengers";
                sessionState.bookingStepAdvancedAt = Date.now();
              }
              if (extractedPassengers) {
                sessionState.bookingStep = "time";
                sessionState.bookingStepAdvancedAt = Date.now();
              }
              console.log(`[${sessionState.callId}] 📊 Booking step after extraction: ${sessionState.bookingStep}`);
              
              // Mark new booking as pending confirmation
              sessionState.pendingNewBooking = {
                pickup: extractedPickup,
                destination: extractedDestination,
                passengers: extractedPassengers,
                bags: extractedBags,
                timestamp: Date.now()
              };
              
              // Sync to live_calls (including booking_step for session restoration)
              supabase.from("live_calls").update({
                pickup: extractedPickup,
                destination: extractedDestination,
                passengers: extractedPassengers,
                booking_step: sessionState.bookingStep, // ✅ Include current step
                updated_at: new Date().toISOString()
              }).eq("call_id", sessionState.callId).then(() => {
                console.log(`[${sessionState.callId}] ✅ live_calls updated with new booking extraction, step=${sessionState.bookingStep}`);
              });
              
              // Cancel any in-flight response
              if (sessionState.openAiResponseActive) {
                openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
              }
              openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // Build confirmation message
              const passengersText = extractedPassengers > 1 ? ` for ${extractedPassengers} passengers` : "";
              const confirmationMessage = `So that's picking up from ${extractedPickup}, going to ${extractedDestination}${passengersText}. Is that correct?`;
              
              // Deduplicate: don't announce same confirmation multiple times
              const promptKey = `new|${extractedPickup}|${extractedDestination}|${extractedPassengers}`.toLowerCase();
              const nowMs = Date.now();
              if (
                sessionState.lastNewBookingPromptAt &&
                nowMs - sessionState.lastNewBookingPromptAt < 15000
              ) {
                console.log(`[${sessionState.callId}] 🔁 Skipping duplicate new booking prompt`);
                sessionState.extractionInProgress = false;
                return;
              }
              sessionState.lastNewBookingPromptAt = nowMs;
              
              // === ASK USER TO CONFIRM THE NEW BOOKING ===
              // Track what we're about to say to detect repetition
              sessionState.lastSpokenConfirmation = confirmationMessage;
              sessionState.lastSpokenConfirmationAt = nowMs;
              
              setTimeout(() => {
                // CRITICAL: Only inject ONE instruction source to prevent Ada from repeating
                // Use conversation.item.create ONLY - do NOT add duplicate instructions in response.create
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{
                      type: "input_text",
                      text: `[SYSTEM: NEW BOOKING DETECTED - Say EXACTLY: "${confirmationMessage}" Then STOP COMPLETELY and wait silently for their response. DO NOT call book_taxi yet. DO NOT continue speaking. DO NOT repeat any addresses. DO NOT rephrase. Just wait for yes/no.]`,
                    }],
                  },
                }));
                
                sessionState.newBookingPromptPending = true;

                // Use simple response.create WITHOUT duplicate instructions
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
                
                // Clear extraction flag AFTER we've injected the response
                sessionState.extractionInProgress = false;
              }, 300);
              
            }).catch((extractError) => {
              console.error(`[${sessionState.callId}] ❌ New booking AI extraction error:`, extractError);
              sessionState.extractionInProgress = false;
              openaiWs?.send(JSON.stringify({ type: "response.create" }));
            });
            
            // Don't break - let normal processing continue while AI extraction runs in background
          }

          // NOTE: Legacy routeMatch-based modification injection was removed.
          // It conflicted with the new pendingModification flow and could cause Ada to repeat or use stale addresses.
          // All modification/correction handling is now done by the block below (AUTO-DETECTION + pendingModification).

          
          // === BOOKING MODIFICATION AUTO-DETECTION (AI-BASED) ===
          // Use AI extraction instead of regex for reliable modification detection
          // When there's an existing booking and user says something substantial,
          // call taxi-extract-unified to properly decode what they want
          
          // Detect if this is a simple confirmation/rejection that should NOT trigger extraction
          // NOTE: Time phrases like "straight away" are NOT included here - they're valid time answers
          const isSimpleResponse = /^(yes|no|yeah|yep|nope|nah|okay|ok|sure|correct|right|that's right|that's correct|fine|good|perfect|great|lovely|cheers|thanks|thank you|bye|goodbye|ta)\.?$/i.test(lowerUserText.trim());
          
          // Detect if user is providing address-like content (anything substantial, not just small talk)
          // Minimum 8 chars to filter out "hi", "hello", "yes", etc.
          const isSubstantialInput = lowerUserText.length >= 8 && !isSimpleResponse && !isConfirmationPhrase;
          
          // AI-FIRST APPROACH: If user has active booking and says something substantial,
          // use AI to extract and compare - no more relying on fragile regex patterns
          // NOTE: We now allow extraction even with pendingQuote, as user might be changing their trip
          // after being asked "keep, change, or cancel?"
          // Don't run booking modification extraction during "anything else" chat.
          // It causes unnecessary blocking for general questions (e.g. "what events are on?").
          const shouldUseAiExtraction = hasExistingBookingContext && 
            isSubstantialInput &&
            !sessionState.askedAnythingElse &&
            !sessionState.pendingModification &&
            !sessionState.extractionInProgress &&
            !sessionState.callEnded;
          
          if (shouldUseAiExtraction && openaiWs && openaiConnected) {
            console.log(`[${sessionState.callId}] 🧠 AI-based modification check: "${userText.substring(0, 50)}..." (length=${lowerUserText.length}, substantial=${isSubstantialInput})`);
            console.log(`[${sessionState.callId}] 🔍 BLOCKING Ada and calling AI extraction...`);
            
            // === CRITICAL: BLOCK ADA FROM RESPONDING ===
            // Set flag IMMEDIATELY to prevent OpenAI VAD from triggering a response
            sessionState.extractionInProgress = true;
            
            // Cancel any in-flight response so Ada doesn't speak with old data
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              sessionState.discardCurrentResponseAudio = true;
              console.log(`[${sessionState.callId}] ⏸️ Cancelled in-flight response for modification extraction`);
            }
            
            // Clear audio buffer to prevent stale responses
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            // Build conversation history for AI from transcripts
            const conversationForAi = sessionState.transcripts
              .slice(-10) // Last 10 turns for context
              .map((turn: TranscriptItem) => ({
                role: turn.role as "user" | "assistant",
                text: turn.text
              }));
            
            // Add the current user message
            conversationForAi.push({ role: "user" as const, text: userText });
            
            // Call AI extraction (non-blocking with .then())
            fetch(
              `${Deno.env.get("SUPABASE_URL")}/functions/v1/taxi-extract-unified`,
              {
                method: "POST",
                headers: {
                  "Content-Type": "application/json",
                  "Authorization": `Bearer ${Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")}`,
                },
                body: JSON.stringify({
                  conversation: conversationForAi,
                  current_booking: {
                    pickup: sessionState.booking.pickup,
                    destination: sessionState.booking.destination,
                    passengers: sessionState.booking.passengers,
                    luggage: sessionState.booking.bags ? `${sessionState.booking.bags} bags` : null,
                    vehicle_type: sessionState.booking.vehicle_type,
                  },
                  caller_phone: sessionState.phone,
                  is_modification: true,
                }),
              }
            ).then(async (extractResponse) => {
              if (!extractResponse.ok) {
                console.error(`[${sessionState.callId}] ❌ AI extraction failed: ${extractResponse.status}`);
                return;
              }
              
              const extracted = await extractResponse.json();
              console.log(`[${sessionState.callId}] 🤖 AI extraction result:`, extracted);
              
              // Compare AI extraction with current booking to detect changes
              const oldPickup = sessionState.booking.pickup || "";
              const oldDestination = sessionState.booking.destination || "";
              const oldPassengers = sessionState.booking.passengers || 1;
              const oldBags = sessionState.booking.bags || 0;
              
              // Apply STT corrections to extracted addresses as safeguard
              const rawNewPickup = extracted.pickup || oldPickup;
              const rawNewDestination = extracted.destination || oldDestination;
              let newPickup = correctTranscript(rawNewPickup);
              let newDestination = correctTranscript(rawNewDestination);
              const newPassengers = extracted.passengers || oldPassengers;
              const newBags = extracted.luggage ? parseInt(extracted.luggage) || 0 : oldBags;
              
              if (rawNewPickup !== newPickup || rawNewDestination !== newDestination) {
                console.log(`[${sessionState.callId}] 🔧 Corrected modification: pickup="${rawNewPickup}"→"${newPickup}", dest="${rawNewDestination}"→"${newDestination}"`);
              }
              
              // ⚠️ METADATA POLLUTION GUARD: If extraction matches caller_name, it's corrupted
              // Fall back to old values to prevent customer name from being used as address
              const customerName = sessionState.customerName || "";
              const normalizeForPollutionCheck = (s: string) => s.toLowerCase().replace(/[.,\s]+/g, " ").trim();
              const nameNorm = normalizeForPollutionCheck(customerName);
              
              if (customerName && newPickup) {
                const pickupNorm = normalizeForPollutionCheck(newPickup);
                // Check if significant overlap with customer name
                const pickupWords = pickupNorm.split(" ").filter(w => w.length > 2);
                const nameWords = nameNorm.split(" ").filter(w => w.length > 2);
                const commonWords = pickupWords.filter(w => nameWords.includes(w)).length;
                const overlap = nameWords.length > 0 ? commonWords / nameWords.length : 0;
                if (overlap > 0.6) {
                  console.log(`[${sessionState.callId}] ⚠️ METADATA POLLUTION: Extracted pickup "${newPickup}" matches caller_name "${customerName}" - IGNORING`);
                  newPickup = oldPickup; // Revert to old value
                }
              }
              
              if (customerName && newDestination) {
                const destNorm = normalizeForPollutionCheck(newDestination);
                const destWords = destNorm.split(" ").filter(w => w.length > 2);
                const nameWords = nameNorm.split(" ").filter(w => w.length > 2);
                const commonWords = destWords.filter(w => nameWords.includes(w)).length;
                const overlap = nameWords.length > 0 ? commonWords / nameWords.length : 0;
                if (overlap > 0.6) {
                  console.log(`[${sessionState.callId}] ⚠️ METADATA POLLUTION: Extracted destination "${newDestination}" matches caller_name "${customerName}" - IGNORING`);
                  newDestination = oldDestination; // Revert to old value
                }
              }
              
              // Normalize for comparison - also treat placeholder values as empty
              const normalizeAddr = (s: string) => s.toLowerCase().replace(/[.,\s]+/g, " ").trim();
              const isPlaceholder = (s: string) => {
                const lower = s.toLowerCase().trim();
                return !lower || lower === "not set" || lower === "not specified" || 
                       lower === "unknown" || lower === "none" || lower === "n/a" ||
                       lower === "your location" || lower === "your destination";
              };
              
              // Only count as "changed" if the new value is a real address (not a placeholder)
              const pickupChanged = newPickup && !isPlaceholder(newPickup) && normalizeAddr(newPickup) !== normalizeAddr(oldPickup);
              const destinationChanged = newDestination && !isPlaceholder(newDestination) && normalizeAddr(newDestination) !== normalizeAddr(oldDestination);
              const passengersChanged = newPassengers !== oldPassengers;
              const bagsChanged = newBags !== oldBags;
              
              const hasChanges = pickupChanged || destinationChanged || passengersChanged || bagsChanged;
              
              if (!hasChanges) {
                console.log(`[${sessionState.callId}] 🔄 AI extraction found SAME addresses as current booking - user is restating their trip`);
                sessionState.extractionInProgress = false;
                
                // User restated the same trip they already have booked
                // Inject a system message to remind Ada to acknowledge this
                setTimeout(() => {
                  const pickup = sessionState.booking.pickup || "pickup";
                  const destination = sessionState.booking.destination || "destination";
                  
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: The customer mentioned the same trip they already have booked (${pickup} to ${destination}). Acknowledge their existing booking and ask: "You already have that booking. Would you like me to keep it, change it, or cancel it?"]`,
                      }],
                    },
                  }));
                  
                  openaiWs?.send(JSON.stringify({ type: "response.create" }));
                }, 300);
                return;
              }
              
              console.log(`[${sessionState.callId}] ✅ AI detected changes: pickup=${pickupChanged}, dest=${destinationChanged}, passengers=${passengersChanged}, bags=${bagsChanged}`);
              
              // Apply the changes from AI extraction
              if (pickupChanged) sessionState.booking.pickup = newPickup;
              if (destinationChanged) sessionState.booking.destination = newDestination;
              if (passengersChanged) sessionState.booking.passengers = newPassengers;
              if (bagsChanged) sessionState.booking.bags = newBags;
              
              // Increment version
              sessionState.booking.version = (sessionState.booking.version || 1) + 1;
              
              // Determine primary changed field for confirmation message
              let primaryField: string;
              if (pickupChanged) primaryField = "pickup";
              else if (destinationChanged) primaryField = "destination";
              else if (passengersChanged) primaryField = "passengers";
              else primaryField = "bags";
              
              // Mark pending modification
              sessionState.pendingModification = {
                field: primaryField as any,
                oldValue: primaryField === "pickup" ? oldPickup : 
                          primaryField === "destination" ? oldDestination :
                          primaryField === "passengers" ? String(oldPassengers) : String(oldBags),
                newValue: primaryField === "pickup" ? newPickup :
                          primaryField === "destination" ? newDestination :
                          primaryField === "passengers" ? String(newPassengers) : String(newBags),
                timestamp: Date.now()
              };
              
              // Sync to live_calls
              supabase.from("live_calls").update({
                pickup: sessionState.booking.pickup,
                destination: sessionState.booking.destination,
                passengers: sessionState.booking.passengers,
                updated_at: new Date().toISOString()
              }).eq("call_id", sessionState.callId).then(() => {
                console.log(`[${sessionState.callId}] ✅ live_calls updated with AI-extracted modification`);
              });
              
              // Cancel any in-flight response
              if (sessionState.openAiResponseActive) {
                openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
              }
              openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // Clear pending quote
              sessionState.pendingQuote = null;
              
              // Build confirmation message summarizing the FULL updated booking
              // Use friendly fallbacks for any missing/placeholder values
              const safeAddr = (addr: string | null | undefined, fallback: string) => {
                if (!addr) return fallback;
                const lower = addr.toLowerCase().trim();
                if (!lower || lower === "not set" || lower === "not specified" || 
                    lower === "unknown" || lower === "none" || lower === "n/a") {
                  return fallback;
                }
                return addr;
              };
              const pickup = safeAddr(sessionState.booking.pickup, "your pickup location");
              const destination = safeAddr(sessionState.booking.destination, "your destination");
              const passengers = sessionState.booking.passengers || 1;
              
              // Build a clear summary of what changed
              const changes: string[] = [];
              if (pickupChanged) changes.push(`pickup to ${newPickup}`);
              if (destinationChanged) changes.push(`destination to ${newDestination}`);
              if (passengersChanged) changes.push(`${newPassengers} passengers`);
              if (bagsChanged) changes.push(`${newBags} bags`);
              
              const changesSummary = changes.join(" and ");
              const confirmationMessage = `Got it, ${changesSummary}. So that's from ${pickup} to ${destination}${passengers > 1 ? ` for ${passengers} passengers` : ""}. Is that correct?`;

              // Deduplicate: don't announce the same modification multiple times.
              // (Duplicate STT events or background extraction retries can otherwise re-inject this prompt.)
              const promptKey = `${changesSummary}|${pickup}|${destination}|${passengers}|${newBags}`.toLowerCase();
              const nowMs = Date.now();
              if (
                sessionState.lastModificationPromptKey === promptKey &&
                sessionState.lastModificationPromptAt &&
                nowMs - sessionState.lastModificationPromptAt < 15000
              ) {
                console.log(`[${sessionState.callId}] 🔁 Skipping duplicate modification prompt (already announced)`);
                sessionState.extractionInProgress = false;
                return;
              }
              sessionState.lastModificationPromptKey = promptKey;
              sessionState.lastModificationPromptAt = nowMs;
              
              // === ASK USER TO CONFIRM THE CHANGE ===
              // Track what we're about to say to detect repetition
              sessionState.lastSpokenConfirmation = confirmationMessage;
              sessionState.lastSpokenConfirmationAt = nowMs;
              
              setTimeout(() => {
                // CRITICAL: Only inject ONE instruction source to prevent Ada from repeating
                // Use conversation.item.create ONLY - do NOT add duplicate instructions in response.create
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{
                      type: "input_text",
                      text: `[SYSTEM: MODIFICATION APPLIED - Say EXACTLY: "${confirmationMessage}" Then STOP COMPLETELY and wait silently for their response. DO NOT call any tools. DO NOT continue speaking. DO NOT repeat any addresses. DO NOT rephrase or summarize again. Just wait for yes/no.]`,
                    }],
                  },
                }));
                
                sessionState.modificationPromptPending = true;

                // Use simple response.create WITHOUT duplicate instructions
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
                
                // Clear extraction flag AFTER we've injected the response
                sessionState.extractionInProgress = false;
              }, 300);
              
            }).catch((extractError) => {
              console.error(`[${sessionState.callId}] ❌ AI extraction error:`, extractError);
              // Clear extraction flag on error so Ada can respond
              sessionState.extractionInProgress = false;
              // Use safe wrapper to prevent "already has active response" errors
              safeResponseCreate(sessionState, "extraction-error-fallback");
            });
            
            // Don't break here - let normal processing continue while AI extraction runs in background
            // If AI finds changes, it will inject the confirmation message
          }
          
          // === PENDING MODIFICATION CONFIRMATION ===
          // === PENDING MODIFICATION CONFIRMATION ===
          // Demo mode should NOT do any modification / update flow.
          if (!DEMO_SIMPLE_MODE) {
            // If user says "yes" after a modification was applied, NOW send the webhook
            // Check BOTH pendingModification AND modificationPromptPending (fallback for when AI didn't detect changes)
            const hasPendingMod = sessionState.pendingModification &&
                Date.now() - sessionState.pendingModification.timestamp < 60000;
            const hasModPromptPending = sessionState.modificationPromptPending &&
                sessionState.lastModificationPromptAt &&
                Date.now() - sessionState.lastModificationPromptAt < 60000;

            if ((hasPendingMod || hasModPromptPending) &&
                openaiWs && openaiConnected && !sessionState.callEnded) {

              const isConfirmingModification = /\b(yes|yeah|yep|yup|happy|correct|that's right|thats right|sounds good|perfect|great|fine|ok|okay|sure|please)\b/i.test(lowerUserText);
              const isRejectingModification = /\b(no|nope|wrong|change|not right|incorrect)\b/i.test(lowerUserText);

              if (isConfirmingModification) {
                console.log(`[${sessionState.callId}] ✅ User confirmed modification - NOW sending webhook for fare (pendingMod=${hasPendingMod}, promptPending=${hasModPromptPending})`);

                // Clear both flags
                sessionState.pendingModification = null;
                sessionState.modificationPromptPending = false;

                // Cancel any in-flight response
                if (sessionState.openAiResponseActive) {
                  openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                }
                openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                // NOW trigger book_taxi to send webhook and get fare
                setTimeout(() => {
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: Customer CONFIRMED the modification. NOW call book_taxi with confirmation_state: "request_quote", pickup: "${sessionState.booking.pickup}", destination: "${sessionState.booking.destination}" to get the updated fare. Do NOT speak until you receive the fare from dispatch.]`,
                      }],
                    },
                  }));

                  // Cancel any active response before creating new one (race condition fix)
                  if (sessionState.openAiResponseActive) {
                    openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
                    sessionState.openAiResponseActive = false;
                  }

                  openaiWs?.send(JSON.stringify({
                    type: "response.create",
                    response: {
                      modalities: ["audio", "text"],
                      instructions: `Call book_taxi with confirmation_state: "request_quote" now. Wait for fare before speaking.`
                    }
                  }));
                }, 300);

                break;
              } else if (isRejectingModification) {
                console.log(`[${sessionState.callId}] ❌ User rejected modification - asking what they want instead`);

                // Clear both flags
                sessionState.pendingModification = null;
                sessionState.modificationPromptPending = false;

                // Revert the change (restore old values would require storing them)
                // For now, just ask what they want
                setTimeout(() => {
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: Customer rejected the modification. Ask: "Sorry about that. What would you like to change it to?"]`,
                      }],
                    },
                  }));

                  // Cancel any active response before creating new one (race condition fix)
                  if (sessionState.openAiResponseActive) {
                    openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
                    sessionState.openAiResponseActive = false;
                  }
                  openaiWs?.send(JSON.stringify({ type: "response.create" }));
                }, 300);

                break;
              }
            }
          }

          // === PENDING NEW BOOKING CONFIRMATION ===
          // If user says "yes" after Ada read back the addresses for a NEW booking, trigger fare quote
          const hasNewBookingPending = sessionState.newBookingPromptPending && 
              sessionState.lastNewBookingPromptAt && 
              Date.now() - sessionState.lastNewBookingPromptAt < 60000;
          
          if (hasNewBookingPending && !sessionState.pendingQuote &&
              openaiWs && openaiConnected && !sessionState.callEnded) {
            
            const isConfirmingNewBooking = /\b(yes|yeah|yep|yup|happy|correct|that's right|thats right|sounds good|perfect|great|fine|ok|okay|sure|please)\b/i.test(lowerUserText);
            const isRejectingNewBooking = /\b(no|nope|wrong|change|not right|incorrect|actually)\b/i.test(lowerUserText);
            
            if (isConfirmingNewBooking) {
              console.log(`[${sessionState.callId}] ✅ User confirmed new booking - NOW sending webhook for fare`);
              
              // Clear the flag
              sessionState.newBookingPromptPending = false;
              
              // Cancel any in-flight response
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              }
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // NOW trigger book_taxi to send webhook and get fare
              setTimeout(() => {
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{
                      type: "input_text",
                      text: `[SYSTEM: Customer CONFIRMED the booking details. NOW call book_taxi with confirmation_state: "request_quote", pickup: "${sessionState.booking.pickup}", destination: "${sessionState.booking.destination}", passengers: ${sessionState.booking.passengers || 1} to get the fare. Do NOT speak until you receive the fare from dispatch.]`,
                    }],
                  },
                }));
                
                openaiWs?.send(JSON.stringify({
                  type: "response.create",
                  response: {
                    modalities: ["audio", "text"],
                    instructions: `Call book_taxi with confirmation_state: "request_quote" now. Wait for fare before speaking.`
                  }
                }));
              }, 300);
              
              break;
            } else if (isRejectingNewBooking) {
              console.log(`[${sessionState.callId}] ❌ User rejected new booking details - asking what they want instead`);
              
              // Clear the flag
              sessionState.newBookingPromptPending = false;
              
              setTimeout(() => {
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{
                      type: "input_text",
                      text: `[SYSTEM: Customer rejected the booking details. Ask: "Sorry about that. What would you like to change?"]`,
                    }],
                  },
                }));
                
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
              }, 300);
              
              break;
            }
          }
          // === POST-BOOKING RESPONSE HELPER ===
          // If booking was confirmed and user says something positive (not goodbye/thanks),
          // ensure Ada responds to their new request. This catches "yes I need another taxi" etc.
          const isPostBookingResponse = 
            sessionState.lastBookTaxiSuccessAt && 
            Date.now() - sessionState.lastBookTaxiSuccessAt < 2 * 60 * 1000 &&
            !sessionState.pendingQuote &&
            !sessionState.callEnded;
            
          const isNewRequestAfterBooking = isPostBookingResponse &&
            !/\b(thanks|thank you|thx|cheers|that's fine|thats fine|that's all|thats all|no thanks|no thank you|bye|goodbye)\b/i.test(lowerUserText) &&
            /\b(yes|yeah|yep|sure|another|need|want|can you|book|taxi|pickup|pick up)\b/i.test(lowerUserText);
          
          if (isNewRequestAfterBooking && openaiWs && openaiConnected) {
            console.log(`[${sessionState.callId}] 🆕 New request after booking: "${userText}"`);
            
            // Reset booking state for new request
            sessionState.booking = { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, pickup_time: null, version: 0 };
            sessionState.hasActiveBooking = false;
            // ✅ Reset booking step for new booking flow
            sessionState.bookingStep = "pickup";
            sessionState.bookingStepAdvancedAt = Date.now();
            
            // Ensure OpenAI responds to this new request
            safeResponseCreate(sessionState, "post-booking-new-request");
          }
          
          // --- Fare confirmation is now handled by confirmation_state in book_taxi ---
          // User says YES/NO after hearing fare → Ada calls book_taxi with "confirmed" or "rejected"

          // === SAFETY: Clear pre-emptive extraction guard if no AI extraction was triggered ===
          // The pre-emptive guard in speech_stopped blocks Ada for users with active bookings.
          // If we processed the transcript and didn't need extraction, clear the guard AND trigger response.
          if (sessionState.extractionInProgress && !shouldUseAiExtraction) {
            console.log(`[${sessionState.callId}] 🔓 Clearing pre-emptive extraction guard (no modification/extraction needed) - triggering response`);
            sessionState.extractionInProgress = false;
            
            // CRITICAL: The VAD response was cancelled, so we must manually trigger a response
            // Otherwise Ada stays silent after user spoke
            if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
              // Cancel any active response before creating new one (race condition fix)
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                sessionState.openAiResponseActive = false;
              }
              openaiWs.send(JSON.stringify({ type: "response.create" }));
            }
          }
        }
        break;
      }

      case "response.function_call_arguments.done":
        handleFunctionCall(
          message.name,
          message.arguments,
          message.call_id,
          sessionState
        );
        break;

      case "error": {
        const errorCode = message.error?.code || "unknown";
        const errorMessage = message.error?.message || "Unknown error";
        console.error(`[${sessionState.callId}] 🚨 OpenAI Error [${errorCode}]:`, errorMessage);
        
        // Track error frequency for stuck detection
        sessionState.recentErrorCount++;
        sessionState.lastErrorAt = Date.now();
        
        // Handle specific recoverable errors
        if (errorCode === "input_audio_buffer_commit_empty") {
          // Buffer was empty when we tried to commit - this is benign, just log and continue
          console.log(`[${sessionState.callId}] ℹ️ Empty buffer commit (benign) - continuing`);
        } else if (errorCode === "response_cancel_not_active") {
          // Tried to cancel a response that wasn't active - reset our tracking flag
          console.log(`[${sessionState.callId}] ℹ️ Cancel on inactive response (benign) - resetting openAiResponseActive`);
          sessionState.openAiResponseActive = false;
          // CRITICAL: safeCancel() sets discardCurrentResponseAudio=true. If the cancel fails because
          // there was no active response, we must UN-discard or we'll drop all subsequent TTS audio
          // (looks like "Ada is silent" after reconnect/ask_confirm).
          sessionState.discardCurrentResponseAudio = false;
        } else if (errorCode === "conversation_already_has_active_response") {
          // We got out of sync with OpenAI's internal response lifecycle.
          // Treat as: a response IS active; defer any further response.create until response.done.
          console.log(`[${sessionState.callId}] ℹ️ Active-response error - deferring next response.create until response.done`);
          sessionState.openAiResponseActive = true;
          sessionState.deferredResponseCreate = true;
          // Don't let this benign-sync issue trigger the stuck-recovery spam.
          sessionState.recentErrorCount = 0;
          break;
        }
        
        // STUCK DETECTION: If we get 3+ errors in 10 seconds and no response is active,
        // the model may be stuck. Force a response to unstick it.
        const timeSinceLastError = sessionState.lastErrorAt 
          ? Date.now() - sessionState.lastErrorAt 
          : 10000;
        
        if (sessionState.recentErrorCount >= 3 && timeSinceLastError < 10000) {
          console.log(`[${sessionState.callId}] ⚠️ STUCK DETECTION: ${sessionState.recentErrorCount} errors in ${timeSinceLastError}ms - triggering recovery`);
          
          // Reset error count to prevent repeated recovery
          sessionState.recentErrorCount = 0;
          
          // Clear any extraction blocks
          sessionState.extractionInProgress = false;
          
          // If no response is active and we have context, force a response
          if (!sessionState.openAiResponseActive && openaiWs && openaiConnected) {
            console.log(`[${sessionState.callId}] 🔧 RECOVERY: Forcing response.create to unstick OpenAI`);
            
            // Inject a recovery prompt
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{ type: "input_text", text: "[SYSTEM: Resume the conversation. Ask the next question if waiting for information, or confirm if ready to proceed.]" }]
              }
            }));
            
            // Use safeResponseCreate to avoid creating a response while one is still active.
            safeResponseCreate(sessionState, "stuck-recovery");
          }
        }
        break;
      }
    }
  };

  // --- Handle Function Calls ---
  const handleFunctionCall = async (
    name: string,
    argsJson: string,
    callId: string,
    sessionState: SessionState
  ) => {
    console.log(`[${sessionState.callId}] 🔧 Tool call: ${name}`);
    
    let result: any;
    try {
      const args = JSON.parse(argsJson);

      switch (name) {
        case "save_customer_name": {
          console.log(`[${sessionState.callId}] 👤 Saving name: ${args.name}`);
          const previousName = sessionState.customerName;
          sessionState.customerName = args.name;
          
          // Persist to callers table
          if (sessionState.phone) {
            try {
              const phoneKey = normalizePhone(sessionState.phone);
              const { error } = await supabase
                .from("callers")
                .upsert(
                  {
                    phone_number: phoneKey,
                    name: args.name,
                    updated_at: new Date().toISOString(),
                  },
                  { onConflict: "phone_number" }
                );
              
              if (error) {
                console.error(`[${sessionState.callId}] Failed to save name to DB:`, error);
              } else {
                console.log(`[${sessionState.callId}] ✅ Name saved to callers table: "${previousName || 'none'}" → "${args.name}"`);
              }
              
              // Also update live_calls
              await supabase
                .from("live_calls")
                .update({ caller_name: args.name })
                .eq("call_id", sessionState.callId);
                
            } catch (e) {
              console.error(`[${sessionState.callId}] Error saving name:`, e);
            }
          }
          
          result = { 
            success: true,
            previous_name: previousName,
            new_name: args.name,
            message: previousName 
              ? `Updated name from "${previousName}" to "${args.name}"`
              : `Saved name: ${args.name}`
          };
          break;
        }

        case "save_location": {
          console.log(`[${sessionState.callId}] 📍 Saving location: ${args.location}`);
          
          // DEMO_SIMPLE_MODE: Skip geocoding, desktop app will handle address verification
          if (DEMO_SIMPLE_MODE) {
            console.log(`[${sessionState.callId}] 🎭 DEMO MODE: Skipping geocode, desktop will verify`);
            result = { 
              success: true, 
              message: `Location noted: ${args.location}. Desktop app will handle address verification.`,
              location_raw: args.location
            };
            break;
          }
          
          try {
            // Call geocode function to convert location to coordinates
            const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
            const geocodeResponse = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
              method: "POST",
              headers: { 
                "Content-Type": "application/json",
                "Authorization": `Bearer ${Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")}`
              },
              body: JSON.stringify({ 
                address: args.location,
                // Bias towards caller's likely country based on phone
                region: sessionState.phone?.startsWith("+44") ? "uk" 
                      : sessionState.phone?.startsWith("+31") ? "nl"
                      : sessionState.phone?.startsWith("+49") ? "de"
                      : undefined
              })
            });
            
            if (geocodeResponse.ok) {
              const geocodeData = await geocodeResponse.json();
              
              if (geocodeData.lat && geocodeData.lon) {
                const formattedAddress = geocodeData.formatted_address || args.location;
                const city = geocodeData.city || geocodeData.locality || null;
                
                console.log(`[${sessionState.callId}] ✅ Location geocoded: ${geocodeData.lat}, ${geocodeData.lon} (${formattedAddress})`);
                
                // Also save to callers table for permanent history (known_areas)
                if (sessionState.phone && city) {
                  try {
                    const phoneKey = normalizePhone(sessionState.phone);

                    // First get current known_areas
                    const { data: callerData } = await supabase
                      .from("callers")
                      .select("known_areas")
                      .eq("phone_number", phoneKey)
                      .maybeSingle();
                    
                    const currentAreas = (callerData?.known_areas as Record<string, any>) || {};
                    
                    // Add this location to known_areas if not already there
                    if (!currentAreas[city]) {
                      currentAreas[city] = {
                        lat: geocodeData.lat,
                        lon: geocodeData.lon,
                        address: formattedAddress,
                        added_at: new Date().toISOString()
                      };
                      
                      await supabase.from("callers").upsert({
                        phone_number: phoneKey,
                        known_areas: currentAreas,
                        updated_at: new Date().toISOString()
                      }, { onConflict: "phone_number" });
                      
                      console.log(`[${sessionState.callId}] 📍 Saved ${city} to caller's known_areas`);
                    }
                  } catch (e) {
                    console.error(`[${sessionState.callId}] Failed to save known_areas:`, e);
                  }
                }
                
                result = { 
                  success: true, 
                  message: `Location saved: ${formattedAddress}`,
                  formatted_address: formattedAddress,
                  city: city,
                  lat: geocodeData.lat,
                  lon: geocodeData.lon
                };
              } else {
                result = { 
                  success: false, 
                  error: "Could not find that location. Please try a more specific address or landmark."
                };
              }
            } else {
              result = { 
                success: false, 
                error: "Location lookup failed. Please try again."
              };
            }
          } catch (e) {
            console.error(`[${sessionState.callId}] Geocode error:`, e);
            result = { 
              success: false, 
              error: "Location lookup failed. Please try again."
            };
          }
          break;
        }

        case "book_taxi": {
          console.log(`[${sessionState.callId}] 🚕 Booking request from Ada:`, args);
          
          // Support both 'action' (new spec) and 'confirmation_state' (legacy) params
          const confirmationState = args.action || args.confirmation_state || "request_quote";
          console.log(`[${sessionState.callId}] 📋 action/confirmation_state: "${confirmationState}"`);
          
          
          // Normalize addresses for comparisons & dedupe keys (shared across all confirmation states)
          const normalizeForComparison = (addr: string): string => {
            if (!addr) return "";
            return addr
              .toLowerCase()
              .replace(/[,.\-]/g, " ")
              .replace(/\s+/g, " ")
              .replace(/\b(street|st|road|rd|avenue|ave|lane|ln|drive|dr|court|ct|place|pl)\b/gi, "")
              .replace(/\b(netherlands|uk|united kingdom|england|nederland|the netherlands)\b/gi, "")
              .replace(/\b(in|naar|van|to|from)\b/gi, "")
              .trim();
          };

          const makeTripKey = (
            pickup: string | null | undefined,
            destination: string | null | undefined,
          ) => `${normalizeForComparison(pickup || "")}|||${normalizeForComparison(destination || "")}`;
          
          // === HANDLE CONFIRMATION STATES ===
          
          // STATE: "confirmed" - Customer said YES to fare quote
          if (confirmationState === "confirmed") {
            const pendingQuote = sessionState.pendingQuote;
            // ✅ FIX #2: CLEAR pendingQuote IMMEDIATELY to prevent stale broadcasts from re-triggering
            sessionState.pendingQuote = null;
            
            if (!pendingQuote) {
              console.log(`[${sessionState.callId}] ⚠️ Cannot confirm - no pending quote`);
              result = {
                success: false,
                error: "no_pending_quote",
                message: "No fare quote to confirm. Call with confirmation_state: 'request_quote' first."
              };
              break;
            }
            
            console.log(`[${sessionState.callId}] ✅ Customer CONFIRMED booking: fare=${pendingQuote.fare}, eta=${pendingQuote.eta}`);

            // Final route (prefer current tool args, fallback to pendingQuote/session)
            // Check for invalid pickup values: [REDACTED], empty strings
            const isInvalidPickup = (value: unknown): boolean => {
              if (typeof value !== "string") return true;
              const v = value.trim().toLowerCase();
              return !v || v === "[redacted]" || v.startsWith("[");
            };

            // Build candidate list, skipping invalid values
            const pickupCandidates = [args.pickup, pendingQuote.pickup, sessionState.booking.pickup];
            const finalPickup = pickupCandidates.find(p => !isInvalidPickup(p)) || null;
            
            const finalDestination =
              args.destination || pendingQuote.destination || sessionState.booking.destination;

            // If we somehow reach confirmation with an invalid pickup, force the agent to collect a real address.
            if (!finalPickup) {
              console.log(
                `[${sessionState.callId}] ❌ Confirm blocked: pickup is missing/invalid`
              );
              result = {
                success: false,
                error: "pickup_required",
                needs_clarification: true,
                field: "pickup",
                ada_message:
                  "I’ll need the pickup address first. What’s the full pickup address, please?",
              };
              break;
            }

            // Persist latest route to session + dashboard (fixes cases where UI still shows the old destination)
            sessionState.booking.pickup = finalPickup;
            if (finalDestination) sessionState.booking.destination = finalDestination;

            await supabase
              .from("live_calls")
              .update({
                pickup: finalPickup,
                destination: finalDestination,
                passengers: sessionState.booking.passengers,
                updated_at: new Date().toISOString(),
              })
              .eq("call_id", sessionState.callId);

            // Persist confirmed booking so it resumes correctly on redial
            try {
              const callerPhoneNorm = normalizePhone(sessionState.phone);
              const bookingCallId = sessionState.activeBookingCallId || sessionState.callId;

              // Ensure only ONE active booking per caller
              const { error: completeOldError } = await supabase
                .from("bookings")
                .update({ status: "completed", completed_at: new Date().toISOString() })
                .eq("caller_phone", callerPhoneNorm)
                .in("status", ["confirmed", "dispatched", "active", "pending"])
                .neq("call_id", bookingCallId);

              if (completeOldError) {
                console.error(`[${sessionState.callId}] Failed to complete old bookings:`, completeOldError);
              }

              const { error: upsertError } = await supabase.from("bookings").upsert({
                call_id: bookingCallId,
                caller_phone: callerPhoneNorm,
                caller_name: sessionState.customerName,
                pickup: finalPickup,
                destination: finalDestination,
                passengers: sessionState.booking.passengers ?? 1,
                fare: pendingQuote.fare,
                eta: pendingQuote.eta,
                status: "confirmed",
                updated_at: new Date().toISOString(),
              }, { onConflict: "call_id" });

              if (upsertError) {
                console.error(`[${sessionState.callId}] Booking DB error:`, upsertError);
              } else {
                sessionState.hasActiveBooking = true;
                sessionState.activeBookingCallId = bookingCallId;
              }
            } catch (persistErr) {
              console.error(`[${sessionState.callId}] Booking persistence error:`, persistErr);
            }
            
            // POST confirmation to callback_url if provided (tells C# to dispatch the driver)
            // ✅ CRITICAL: This is fire-and-forget to avoid blocking the post-confirmation speech
            if (pendingQuote.callback_url) {
              const confirmPayload = {
                call_id: sessionState.callId,
                action: "confirmed",
                response: "confirmed",  // C# bridge compatibility
                pickup: finalPickup,
                destination: finalDestination,
                fare: pendingQuote.fare,
                eta: pendingQuote.eta,
                pickup_time: pendingQuote.pickup_time || sessionState.booking.pickup_time || "ASAP",
                passengers: sessionState.booking.passengers || 1,
                customer_name: sessionState.customerName,
                caller_phone: sessionState.phone,
                timestamp: new Date().toISOString()
              };
              
              // Fire-and-forget: Don't block on dispatch confirmation
              console.log(`[${sessionState.callId}] 📡 POSTing confirmation to callback_url (fire-and-forget): ${pendingQuote.callback_url}`);
              fetch(pendingQuote.callback_url, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(confirmPayload)
              }).then(confirmResp => {
                console.log(`[${sessionState.callId}] 📬 Callback response: ${confirmResp.status}`);
              }).catch(callbackErr => {
                console.error(`[${sessionState.callId}] ⚠️ Callback POST failed:`, callbackErr);
              });
            }
            
            // Mark booking as confirmed
            sessionState.bookingConfirmedThisTurn = true;
            sessionState.bookingFullyConfirmed = true; // ✅ PERSISTENT: Booking is complete, Ada can speak freely now
            sessionState.lastBookTaxiSuccessAt = Date.now();
            sessionState.lastConfirmedTripKey = makeTripKey(finalPickup, finalDestination);
            sessionState.pendingQuote = null; // Clear pending quote
            sessionState.quoteRequestedAt = null;
            sessionState.quoteTripKey = null;
            sessionState.lastQuotePromptAt = null; // Reset prompt tracking
            sessionState.lastQuotePromptText = null;
            
            console.log(`[${sessionState.callId}] ✅ Booking enforcement: confirmed state succeeded, Ada may now speak freely (bookingFullyConfirmed=true)`);
            
            result = {
              success: true,
              confirmed: true,
              fare: pendingQuote.fare,
              eta_minutes: pendingQuote.eta,
              suppress_response_create: true,
              message: `Booking confirmed! Fare: ${pendingQuote.fare}, ETA: ${pendingQuote.eta}.`
            };
            
            // ✅ INJECT SYSTEM MESSAGE so Ada says "Is there anything else I can help you with?"
            // The tool result message alone is not enough - we need an explicit prompt.
            // STREAMLINED: Ada can speak immediately after webhook - no delays needed
            if (openaiWs && openaiConnected) {
              // Cancel any in-flight response to avoid overlap
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              }
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // ✅ CRITICAL: Enable audio immediately - booking is fully confirmed
              sessionState.discardCurrentResponseAudio = false;
              sessionState.audioVerified = true;
              
              // ✅ SUMMARY PROTECTION: Block barge-in during booking confirmation speech
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
              console.log(`[${sessionState.callId}] 🛡️ Summary protection activated for ${SUMMARY_PROTECTION_MS}ms (booking confirm)`);

              // ✅ Prepare "anything else" wait-state, but DON'T start grace timer until Ada finishes speaking.
              // If we set askedAnythingElseAt here, the response.created guard can cancel Ada's own prompt.
              sessionState.askedAnythingElse = true;
              sessionState.askedAnythingElseAt = null;
              sessionState.confirmationResponsePending = true;
              
              console.log(`[${sessionState.callId}] 📤 Injecting post-confirm message immediately: "That's booked for you. Is there anything else..."`);
              
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{
                    type: "input_text",
                    text: `[SYSTEM: Booking confirmed successfully! Say EXACTLY: "That's booked for you. Is there anything else I can help you with?" Then WAIT for their response. Do NOT say Safe travels yet.]`,
                  }],
                },
              }));

              // Trigger response - wait for any active response to complete first
              if (!sessionState.callEnded && openaiWs) {
                // If OpenAI has an active response, wait for it to finish before creating new one
                const waitForResponseClear = () => {
                  if (sessionState.openAiResponseActive) {
                    console.log(`[${sessionState.callId}] ⏳ Waiting for active response to finish before post-confirm message...`);
                    setTimeout(waitForResponseClear, 100);
                  } else if (!sessionState.callEnded && openaiWs && openaiWs.readyState === WebSocket.OPEN) {
                    openaiWs.send(JSON.stringify({ type: "response.create" }));
                    console.log(`[${sessionState.callId}] ✅ Triggered response.create for post-confirm followup`);
                  }
                };
                
                // Small initial delay to let any in-flight response finish
                setTimeout(waitForResponseClear, 150);
              }
            }
            
            break;
          }
          
          // STATE: "rejected" - Customer said NO to fare quote
          if (confirmationState === "rejected") {
            const pendingQuote = sessionState.pendingQuote;
            console.log(`[${sessionState.callId}] ❌ Customer REJECTED booking`);
            
            // POST rejection to callback_url if provided
            if (pendingQuote?.callback_url) {
              try {
                console.log(`[${sessionState.callId}] 📡 POSTing rejection to callback_url: ${pendingQuote.callback_url}`);
                const rejectPayload = {
                  call_id: sessionState.callId,
                  action: "rejected",
                  response: "rejected",  // C# bridge compatibility
                  pickup: pendingQuote.pickup,
                  destination: pendingQuote.destination,
                  caller_phone: sessionState.phone,
                  timestamp: new Date().toISOString()
                };
                
                await fetch(pendingQuote.callback_url, {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify(rejectPayload)
                });
              } catch (callbackErr) {
                console.error(`[${sessionState.callId}] ⚠️ Rejection callback POST failed:`, callbackErr);
              }
            }
            
            sessionState.pendingQuote = null; // Clear pending quote
            sessionState.quoteRequestedAt = null;
            sessionState.quoteTripKey = null;
            sessionState.lastQuotePromptAt = null;
            sessionState.lastQuotePromptText = null;
            
            result = {
              success: true,
              rejected: true,
              suppress_response_create: true,
              message: "No problem, I've cancelled that."
            };
            
            // ✅ INJECT SYSTEM MESSAGE so Ada says "Is there anything else I can help you with?"
            if (openaiWs && openaiConnected) {
              // Cancel-Clear-Inject protocol to avoid response collisions
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              }

              setTimeout(() => {
                openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                setTimeout(() => {
                  // ✅ CRITICAL: Mark that Ada is asking "anything else?" so goodbye detection works properly
                  sessionState.askedAnythingElse = true;
                  sessionState.askedAnythingElseAt = Date.now(); // Track when we asked for 3-second grace period
                  
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: The customer rejected the booking. Say: "No problem at all. Is there anything else I can help you with?" Then WAIT for their response.]`,
                      }],
                    },
                  }));

                  safeResponseCreate(sessionState, "post-reject-followup");
                }, 350);
              }, 400);
            }
            break;
          }
          
          // STATE: "request_quote" - Get fare/ETA from dispatch (default)

          // === ACTIVE BOOKING GUARD ===
          // Block request_quote when there's an active booking that hasn't been addressed
          // User must explicitly say "keep", "yes", "book", "same" etc. before we can rebook
          if (sessionState.hasActiveBooking && !sessionState.activeBookingAcknowledged) {
            // Check if user has said something indicating they want to keep/rebook
            const recentUserTranscripts = sessionState.transcripts
              .filter(t => t.role === "user")
              .slice(-3)
              .map(t => (t.text || "").toLowerCase())
              .join(" ");
            
            const userWantsToKeep = /\b(keep|yes|yeah|book|same|rebook|confirm|that'?s? ?(right|correct|fine|good))\b/i.test(recentUserTranscripts);
            const userWantsToChange = /\b(change|modify|update|different|new|cancel)\b/i.test(recentUserTranscripts);
            
            if (!userWantsToKeep && !userWantsToChange) {
              console.log(`[${sessionState.callId}] ⛔ BLOCKING request_quote - active booking not yet addressed by user`);
              result = {
                success: false,
                error: "active_booking_not_addressed",
                needs_clarification: true,
                ada_message: "You have an active booking. Do you want to keep it, change it, or cancel it?",
              };
              break;
            }
            
            // Mark that user has acknowledged the active booking
            if (userWantsToKeep) {
              sessionState.activeBookingAcknowledged = true;
              console.log(`[${sessionState.callId}] ✅ User acknowledged active booking (wants to keep)`);
            }
          }

          // If this is a re-quote in an existing booking context, prefer the server-side booking state.
          // This prevents Ada/tool calls from using stale pickup/destination after a modification.
          const hasModifiableBookingContext =
            sessionState.hasActiveBooking || sessionState.booking.version > 0 || !!sessionState.lastBookTaxiSuccessAt;
          if (hasModifiableBookingContext && sessionState.booking.pickup && sessionState.booking.destination) {
            const argsPickupNorm = normalizeForComparison(String(args.pickup || ""));
            const argsDestNorm = normalizeForComparison(String(args.destination || ""));
            const sessPickupNorm = normalizeForComparison(sessionState.booking.pickup);
            const sessDestNorm = normalizeForComparison(sessionState.booking.destination);

            if (argsPickupNorm && sessPickupNorm && argsPickupNorm !== sessPickupNorm) {
              console.log(`[${sessionState.callId}] 🔁 Overriding stale args.pickup ("${args.pickup}") with session pickup ("${sessionState.booking.pickup}")`);
              args.pickup = sessionState.booking.pickup;
            }
            if (argsDestNorm && sessDestNorm && argsDestNorm !== sessDestNorm) {
              console.log(`[${sessionState.callId}] 🔁 Overriding stale args.destination ("${args.destination}") with session destination ("${sessionState.booking.destination}")`);
              args.destination = sessionState.booking.destination;
            }
          }

          // === FIX #1: BLOCK request_quote shortly after a successful booking ONLY if it's the same trip ===
          // We must allow an immediate re-quote if the caller just changed the route.
          if (sessionState.lastBookTaxiSuccessAt && Date.now() - sessionState.lastBookTaxiSuccessAt < 60000) {
            const requestedTripKey = makeTripKey(
              args.pickup || sessionState.booking.pickup,
              args.destination || sessionState.booking.destination,
            );

            const isSameTripAsLastConfirm =
              !!sessionState.lastConfirmedTripKey && requestedTripKey === sessionState.lastConfirmedTripKey;

            if (isSameTripAsLastConfirm) {
              console.log(
                `[${sessionState.callId}] 🔁 Ignoring request_quote within 60s of successful booking (${Date.now() - sessionState.lastBookTaxiSuccessAt}ms ago)`
              );
              result = {
                success: true,
                already_confirmed: true,
                message: "Booking already confirmed. Ask: 'Is there anything else I can help you with?'",
                suppress_response_create: true, // Prevent Ada from speaking on her own
              };

              // ✅ INJECT SYSTEM MESSAGE to guide Ada's response and prevent stutter loop
              if (openaiWs && openaiConnected) {
                if (sessionState.openAiResponseActive) {
                  openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                }

                setTimeout(() => {
                  openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                  setTimeout(() => {
                    openaiWs?.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [{
                          type: "input_text",
                          text: `[SYSTEM: The booking is already confirmed. Do NOT say "booked" again. Simply ask: "Is there anything else I can help you with?" Then WAIT for their response.]`,
                        }],
                      },
                    }));

                    safeResponseCreate(sessionState, "post-already-confirmed");
                  }, 350);
                }, 400);
              }
              break;
            }

            console.log(
              `[${sessionState.callId}] ✅ Allowing request_quote within 60s because trip changed (last=${sessionState.lastConfirmedTripKey}, now=${requestedTripKey})`
            );
          }
          
          // === HARD GUARD: Block request_quote while a modification is awaiting approval ===
          if (sessionState.pendingModification) {
            console.log(`[${sessionState.callId}] ⛔ Blocking request_quote - pending modification awaiting caller approval`);
            result = {
              success: false,
              error: "pending_modification",
              needs_clarification: true,
              ada_message: "Just checking that change first — are you happy with the update?",
            };
            break;
          }

          // === HARD GUARD: Block request_quote until user confirms summary (DEMO_SIMPLE_MODE) ===
          // In demo mode, Ada must summarize booking and wait for "yes/correct" before sending webhook
          if (DEMO_SIMPLE_MODE && sessionState.newBookingPromptPending) {
            console.log(`[${sessionState.callId}] ⛔ DEMO MODE: Blocking request_quote - waiting for user to confirm summary`);
            result = {
              success: false,
              error: "summary_not_confirmed",
              needs_clarification: true,
              suppress_response_create: true, // Don't let Ada speak
              ada_message: "Please wait for the customer to confirm the booking details before checking the price.",
            };
            break;
          }

          // === EARLY EXIT: Prevent duplicate request_quote while pending ===
          if (sessionState.pendingQuote) {
            const timeSinceLast = Date.now() - sessionState.pendingQuote.timestamp;
            if (timeSinceLast < 15000) { // 15 seconds
              console.log(`[${sessionState.callId}] ⏸️ Ignoring duplicate request_quote - pending for ${timeSinceLast}ms`);
              result = {
                success: false,
                error: "duplicate_request",
                message: "Already requesting fare quote. Please wait."
              };
              break;
            }
          }
          
          // === DUAL-SOURCE EXTRACTION & VALIDATION ===
          // 1. Normalize addresses for comparison (normalizeForComparison is defined above)
          
          // 2. Run AI extraction on conversation (both user + assistant turns)
          // OPTIMIZATION: Only run extraction if pickup != destination (likely error case)
          // For normal bookings with distinct addresses, skip extraction to save 500-2000ms
          const conversationForExtraction = sessionState.transcripts
            .filter(t => t.role === "user" || t.role === "assistant")
            .slice(-12); // Last 12 turns for context
          
          let extractedBooking: {
            pickup?: string;
            destination?: string;
            passengers?: number;
            luggage?: number;
            vehicle_type?: string;
            pickup_time?: string;
            confidence?: string;
            missing_fields?: string[];
            nearest_pickup?: string;
            nearest_dropoff?: string;
          } = {};
          
          // DEMO_SIMPLE_MODE: Skip AI extraction, desktop app will handle address verification
          if (DEMO_SIMPLE_MODE) {
            console.log(`[${sessionState.callId}] 🎭 DEMO MODE: Skipping AI extraction for quote, using Ada's args. Desktop will verify addresses.`);
            // Just use Ada's STT-corrected args directly
          } else if (conversationForExtraction.length > 0) {
            // ALWAYS run AI extraction for accurate address capture
            // AI extraction (Gemini) analyzes the full conversation and produces cleaner addresses
            // than Ada's raw tool arguments which may contain STT errors
            try {
              console.log(`[${sessionState.callId}] 🧠 Running AI extraction for accurate addresses...`);
              const extractionResponse = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract-unified`, {
                method: "POST",
                headers: {
                  "Content-Type": "application/json",
                  "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`
                },
                body: JSON.stringify({
                  conversation: conversationForExtraction,
                  caller_name: sessionState.customerName,
                  current_booking: {
                    pickup: args.pickup,
                    destination: args.destination
                  }
                })
              });
              
              if (extractionResponse.ok) {
                extractedBooking = await extractionResponse.json();
                console.log(`[${sessionState.callId}] 🧠 AI Extraction result:`, extractedBooking);
              }
            } catch (extractErr) {
              console.error(`[${sessionState.callId}] AI extraction failed, using Ada's args:`, extractErr);
            }
          } else {
            console.log(`[${sessionState.callId}] ⚠️ No conversation history for extraction, using Ada's args`);
          }
          
          // 3. Compare Ada's interpretation vs AI extraction
          // IMPORTANT: Apply STT corrections to Ada's addresses too (she may have used raw STT)
          const adaPickupRaw = args.pickup || "";
          const adaDestinationRaw = args.destination || "";
          const adaPickup = correctTranscript(adaPickupRaw);
          const adaDestination = correctTranscript(adaDestinationRaw);
          
          if (adaPickup !== adaPickupRaw) {
            console.log(`[${sessionState.callId}] 🔧 STT correction on Ada pickup: "${adaPickupRaw}" → "${adaPickup}"`);
          }
          if (adaDestination !== adaDestinationRaw) {
            console.log(`[${sessionState.callId}] 🔧 STT correction on Ada destination: "${adaDestinationRaw}" → "${adaDestination}"`);
          }
          
          const extractedPickup = extractedBooking.pickup || "";
          const extractedDestination = extractedBooking.destination || "";
          
          const adaPickupNorm = normalizeForComparison(adaPickup);
          const adaDestNorm = normalizeForComparison(adaDestination);
          const extPickupNorm = normalizeForComparison(extractedPickup);
          const extDestNorm = normalizeForComparison(extractedDestination);
          
          // Calculate similarity between sources
          const similarity = (a: string, b: string): number => {
            if (!a || !b) return 0;
            const wordsA = a.split(" ").filter(w => w.length > 2);
            const wordsB = b.split(" ").filter(w => w.length > 2);
            if (wordsA.length === 0 || wordsB.length === 0) return 0;
            const common = wordsA.filter(w => wordsB.includes(w)).length;
            return common / Math.max(wordsA.length, wordsB.length);
          };
          
          const pickupSourceMatch = similarity(adaPickupNorm, extPickupNorm);
          const destSourceMatch = similarity(adaDestNorm, extDestNorm);
          
          console.log(`[${sessionState.callId}] 📊 Source comparison:`);
          console.log(`[${sessionState.callId}]   Ada pickup: "${adaPickup}" | Extracted: "${extractedPickup}" | Match: ${Math.round(pickupSourceMatch * 100)}%`);
          console.log(`[${sessionState.callId}]   Ada dest: "${adaDestination}" | Extracted: "${extractedDestination}" | Match: ${Math.round(destSourceMatch * 100)}%`);
          
          // 4. Determine final addresses - PREFER AI EXTRACTION as primary source
          // AI extraction (Gemini) analyzes the full conversation including Ada's spoken corrections,
          // making it more accurate than Ada's raw tool arguments which may contain STT errors.
          // 
          // ⚠️ METADATA POLLUTION GUARD: If AI extraction matches the caller_name, it's likely
          // confused the customer's stored name (which may be an old address) with the pickup.
          // In this case, TRUST ADA'S interpretation over the extraction.
          let finalPickup: string;
          let finalDestination: string;
          let sourceDiscrepancy = false;
          
          // Helper to detect if extraction is polluted by caller metadata (name stored as address)
          const isMetadataPollution = (extracted: string, callerName: string | null): boolean => {
            if (!callerName || !extracted) return false;
            const extractedNorm = normalizeForComparison(extracted);
            const nameNorm = normalizeForComparison(callerName);
            // If they match significantly, extraction was likely polluted by metadata
            const overlap = similarity(extractedNorm, nameNorm);
            if (overlap > 0.6) {
              console.log(`[${sessionState.callId}] ⚠️ METADATA POLLUTION DETECTED: Extracted "${extracted}" matches caller_name "${callerName}" (${Math.round(overlap * 100)}% overlap) - IGNORING extraction`);
              return true;
            }
            return false;
          };
          
          // Use extraction as primary source if available and NOT polluted by metadata
          const pickupIsMetadataPollution = isMetadataPollution(extractedPickup, sessionState.customerName);
          const destIsMetadataPollution = isMetadataPollution(extractedDestination, sessionState.customerName);
          
          if (extractedPickup && extractedPickup.length > 2 && !pickupIsMetadataPollution) {
            finalPickup = extractedPickup;
            if (extractedPickup !== adaPickup) {
              console.log(`[${sessionState.callId}] 🧠 Using AI-extracted pickup: "${extractedPickup}" (Ada had: "${adaPickup}")`);
            }
          } else {
            finalPickup = adaPickup;
            if (pickupIsMetadataPollution) {
              console.log(`[${sessionState.callId}] 📝 Using Ada's pickup (extraction was metadata pollution): "${adaPickup}"`);
            } else {
              console.log(`[${sessionState.callId}] 📝 Using Ada's pickup (no extraction): "${adaPickup}"`);
            }
          }
          
          if (extractedDestination && extractedDestination.length > 2 && !destIsMetadataPollution) {
            finalDestination = extractedDestination;
            if (extractedDestination !== adaDestination) {
              console.log(`[${sessionState.callId}] 🧠 Using AI-extracted destination: "${extractedDestination}" (Ada had: "${adaDestination}")`);
            }
          } else {
            finalDestination = adaDestination;
            if (destIsMetadataPollution) {
              console.log(`[${sessionState.callId}] 📝 Using Ada's destination (extraction was metadata pollution): "${adaDestination}"`);
            } else {
              console.log(`[${sessionState.callId}] 📝 Using Ada's destination (no extraction): "${adaDestination}"`);
            }
          }
          
          // Check for same pickup/destination error
          const finalPickupNormCheck = normalizeForComparison(finalPickup);
          const finalDestNormCheck = normalizeForComparison(finalDestination);
          
          if (finalPickupNormCheck && finalDestNormCheck && finalPickupNormCheck === finalDestNormCheck) {
            console.log(`[${sessionState.callId}] ⚠️ Pickup = destination after extraction, checking Ada's args...`);
            // Try Ada's args if extraction gave same address
            if (adaPickupNorm !== adaDestNorm) {
              console.log(`[${sessionState.callId}] ✅ Falling back to Ada's distinct addresses`);
              finalPickup = adaPickup;
              finalDestination = adaDestination;
            } else {
              // Both sources agree they're the same - block booking
              console.log(`[${sessionState.callId}] ❌ BLOCKED: Both sources show same address`);
              result = {
                success: false,
                error: "booking_validation_failed",
                ada_message: "I notice the pickup and destination appear to be the same. Could you please confirm where you'd like to go to?",
                needs_clarification: true,
                field: "destination"
              };
              break;
            }
          }
          
          // Log if there was a significant source discrepancy (for monitoring)
          if (extractedPickup && pickupSourceMatch < 0.5) {
            console.log(`[${sessionState.callId}] 📊 Note: Pickup sources differed significantly (${Math.round(pickupSourceMatch * 100)}% match) - using extraction`);
          }
          if (extractedDestination && destSourceMatch < 0.5) {
            console.log(`[${sessionState.callId}] 📊 Note: Destination sources differed significantly (${Math.round(destSourceMatch * 100)}% match) - using extraction`);
          }
          
          // 5. Final validation - ensure pickup != destination
          const finalPickupNorm = normalizeForComparison(finalPickup);
          const finalDestNorm = normalizeForComparison(finalDestination);
          
          if (finalPickupNorm && finalDestNorm && finalPickupNorm === finalDestNorm) {
            console.log(`[${sessionState.callId}] ❌ BLOCKED: Final pickup equals destination`);
            result = {
              success: false,
              error: "booking_validation_failed", 
              ada_message: "I'm having trouble distinguishing the pickup from the destination. Could you tell me again where you'd like to be picked up from, and where you're going?",
              needs_clarification: true
            };
            break;
          }
          
          // 6. Enrich booking with extracted details
          // PRESERVE existing booking details - priority: args > extraction > existing session > defaults
          const existingPassengers = sessionState.booking?.passengers;
          const existingBags = sessionState.booking?.bags;
          const existingVehicleType = sessionState.booking?.vehicle_type;
          
          const finalPassengers = args.passengers ?? extractedBooking.passengers ?? existingPassengers ?? null;
          
          // Parse bags - extractedBooking.luggage may be a string like "no baggage" or "2 bags"
          let finalBags = existingBags ?? 0;  // Default to existing, fallback to 0
          if (typeof args.bags === 'number') {
            finalBags = args.bags;
          } else if (extractedBooking.luggage) {
            const luggageStr = String(extractedBooking.luggage).toLowerCase();
            if (luggageStr.includes('no') || luggageStr === '0') {
              finalBags = 0;
            } else {
              const match = luggageStr.match(/(\d+)/);
              if (match) finalBags = parseInt(match[1], 10);
            }
          }
          
          const finalVehicleType = args.vehicle_type || extractedBooking.vehicle_type || existingVehicleType || "saloon";
          
          // Handle both 'time' (from tool schema) and 'pickup_time' (legacy)
          // LAST-WORD-WINS: Always scan transcripts for time - user may have updated it
          let finalPickupTime = args.time || args.pickup_time || extractedBooking.pickup_time || sessionState.booking.pickup_time;
          
          // Extract time from transcripts using "Last-Word-Wins" approach
          // Scan ALL user transcripts for time expressions, taking the LAST match
          const userTranscripts = sessionState.transcripts.filter(t => t.role === "user");
          let extractedTimeFromTranscripts: string | null = null;
          
          // Time patterns to detect (ordered by specificity)
          const timePatterns = [
            // "tomorrow at 1230" / "tomorrow at 12:30"
            { pattern: /tomorrow\s+(?:at\s+)?(\d{1,2})[:.:]?(\d{2})?\s*(am|pm|hours?)?/i, hasTomorrow: true },
            // "at 1230 tomorrow" / "12:30 tomorrow"  
            { pattern: /(?:at\s+)?(\d{1,2})[:.:]?(\d{2})?\s*(am|pm|hours?)?\s+tomorrow/i, hasTomorrow: true },
            // "1230" / "12:30" / "1230 hours" (4-digit time)
            { pattern: /\b(\d{2})[:.:]?(\d{2})\s*(?:hours?)?\b/i, hasTomorrow: false },
            // "at 3pm" / "at 3:30pm"
            { pattern: /(?:at\s+)?(\d{1,2})[:.:]?(\d{2})?\s*(am|pm)/i, hasTomorrow: false },
            // "in 15 minutes" / "in 2 hours"
            { pattern: /in\s+(\d+)\s*(minutes?|hours?|mins?|hrs?)/i, hasTomorrow: false },
            // Just "tomorrow" with no time (defaults to morning)
            { pattern: /\btomorrow\b/i, hasTomorrow: true },
          ];
          
          // Scan transcripts in REVERSE order (last mentioned time wins)
          for (let i = userTranscripts.length - 1; i >= 0; i--) {
            const text = userTranscripts[i].text.toLowerCase();
            
            for (const { pattern, hasTomorrow } of timePatterns) {
              const match = text.match(pattern);
              if (match) {
                // Build the time expression
                let timeExpr = match[0].trim();
                
                // If pattern detected "tomorrow", include it
                if (hasTomorrow && !timeExpr.includes("tomorrow")) {
                  timeExpr = "tomorrow " + timeExpr;
                }
                
                extractedTimeFromTranscripts = timeExpr;
                console.log(`[${sessionState.callId}] 🕐 Last-Word-Wins: Found time in transcript "${userTranscripts[i].text}": "${extractedTimeFromTranscripts}"`);
                break;
              }
            }
            if (extractedTimeFromTranscripts) break;
          }
          
          // If we found a time in transcripts, use it (overrides Ada's guess or "now")
          if (extractedTimeFromTranscripts) {
            // Only override if Ada's time is missing or is "now"
            if (!finalPickupTime || finalPickupTime === "now" || finalPickupTime.toLowerCase() === "asap") {
              finalPickupTime = extractedTimeFromTranscripts;
            }
          }
          
          // Default to "now" if still nothing
          if (!finalPickupTime) finalPickupTime = "now";
          
          console.log(`[${sessionState.callId}] 🕐 Time resolution: args.time="${args.time}", transcript="${extractedTimeFromTranscripts}", final="${finalPickupTime}"`);
          
          console.log(`[${sessionState.callId}] ✅ Final booking details:`);
          console.log(`[${sessionState.callId}]   Pickup: "${finalPickup}"`);
          console.log(`[${sessionState.callId}]   Destination: "${finalDestination}"`);
          console.log(`[${sessionState.callId}]   Passengers: ${finalPassengers}, Bags: ${finalBags}, Vehicle: ${finalVehicleType}`);
          
          // Update args with final values
          args.pickup = finalPickup;
          args.destination = finalDestination;
          args.passengers = finalPassengers;
          args.bags = finalBags;
          args.vehicle_type = finalVehicleType;
          args.pickup_time = finalPickupTime;

          // If we overrode Ada's stale route using extraction, inject a context correction.
          // This helps Ada stop "remembering" the old active-booking destination in later turns.
          if (sourceDiscrepancy && openaiWs && openaiConnected) {
            // Cancel any active response before injecting system message
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

            openaiWs.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: `[SYSTEM: IMPORTANT ROUTE UPDATE: The correct trip is Pickup="${finalPickup}" and Destination="${finalDestination}". Use these exact values for all future tool calls in this call, even if you recall an older destination from an active booking.]`,
                    },
                  ],
                },
              })
            );
          }
          
          // === END VALIDATION ===
          
          sessionState.booking = {
            pickup: finalPickup,
            destination: finalDestination,
            passengers: finalPassengers,
            bags: finalBags,
            vehicle_type: finalVehicleType,
            pickup_time: finalPickupTime,
            version: 1,
          };
          
          // ✅ ADVANCE BOOKING STEP: At book_taxi request_quote, all fields are complete → summary
          sessionState.bookingStep = "summary";
          sessionState.bookingStepAdvancedAt = Date.now();
          console.log(`[${sessionState.callId}] 📊 Booking step advanced to: ${sessionState.bookingStep}`);
          
          // Sync pickup/destination to live_calls for dashboard display
          await supabase.from("live_calls").update({
            pickup: finalPickup,
            destination: finalDestination,
            passengers: finalPassengers,
            updated_at: new Date().toISOString()
          }).eq("call_id", sessionState.callId);
          
          // Guard against accidental duplicate re-booking loops right after a successful confirmation.
          // (This can happen if Ada gets pulled into an unrelated chat turn and then calls book_taxi again.)
          const currentTripKey = makeTripKey(finalPickup, finalDestination);
          const isDuplicateRecentTrip =
            !!sessionState.lastConfirmedTripKey &&
            sessionState.lastConfirmedTripKey === currentTripKey &&
            !!sessionState.lastBookTaxiSuccessAt &&
            Date.now() - sessionState.lastBookTaxiSuccessAt < 5 * 60 * 1000 &&
            !sessionState.pendingQuote;

          if (isDuplicateRecentTrip) {
            console.log(`[${sessionState.callId}] 🔁 Ignoring duplicate book_taxi(request_quote) for recently confirmed trip`);
            result = {
              success: true,
              already_confirmed: true,
              message: 'Booking already confirmed. Now ask the customer: "Is there anything else I can help you with?"',
            };
            break;
          }

          const jobId = crypto.randomUUID();
          let fare = "£12.50";
          let etaMinutes = 8;
          let bookingRef = "";
          let distance = "";
          
          // Call dispatch webhook if configured
          const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
          console.log(`[${sessionState.callId}] 🔗 DISPATCH_WEBHOOK_URL configured: ${DISPATCH_WEBHOOK_URL ? 'YES' : 'NO'}`);
          
          let dispatchConfirmationSent = false;
          let dispatchPostOk = false;
          let dispatchPostStatus: number | null = null;
          
          if (DISPATCH_WEBHOOK_URL) {
            // ═══════════════════════════════════════════════════════════════════════════
            // PRE-WEBHOOK HANDOFF: Reconnect BEFORE sending webhook for fresh 90s window
            // This ensures we have ample time for fare delivery + user confirmation
            // ═══════════════════════════════════════════════════════════════════════════
            
            // Normalize pickup time now (before handoff) so it's persisted correctly
            const rawPickupTime = finalPickupTime || sessionState.booking.pickup_time || args.pickup_time || "now";
            const normalizedPickupTime = await normalizePickupTime(rawPickupTime);
            console.log(`[${sessionState.callId}] 🕐 Pickup time: raw="${rawPickupTime}" → normalized="${normalizedPickupTime}"`);
            sessionState.booking.pickup_time = normalizedPickupTime;
            
            // Save state to DB with pre_webhook status
            console.log(`[${sessionState.callId}] 🔄 PRE-WEBHOOK HANDOFF: Saving state and triggering reconnect`);
            
            await supabase.from("live_calls").update({
              status: "pre_webhook",
              pickup: finalPickup,
              destination: finalDestination,
              passengers: finalPassengers,
              pickup_time: normalizedPickupTime,
              booking_step: sessionState.bookingStep,
              updated_at: new Date().toISOString()
            }).eq("call_id", sessionState.callId);
            
            // Tell Ada to say "one moment" while we reconnect
            if (openaiWs && openaiConnected) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{
                    type: "input_text",
                    text: `[SYSTEM: Say EXACTLY: "One moment please while I check the price." Then STOP and wait.]`
                  }]
                }
              }));
              
              openaiWs.send(JSON.stringify({ type: "response.create" }));
            }
            
            // Trigger handoff after brief delay for "one moment" to play
            setTimeout(() => {
              console.log(`[${sessionState.callId}] 🔀 PRE-WEBHOOK HANDOFF: Signaling bridge to reconnect`);
              try {
                socket.send(JSON.stringify({
                  type: "session.handoff",
                  call_id: sessionState.callId,
                  reason: "pre_webhook_handoff",
                  booking_step: sessionState.bookingStep
                }));
              } catch (e) {
                console.error(`[${sessionState.callId}] Pre-webhook handoff send error:`, e);
              }
            }, 1500);
            
            // Return early - webhook will be sent after reconnection
            result = {
              success: true,
              pending_webhook: true,
              message: "Session handoff initiated - webhook will be sent after reconnection"
            };
            break;
          }
          
          // === NO DISPATCH WEBHOOK - USE FALLBACK ===
          console.log(`[${sessionState.callId}] ⚠️ No DISPATCH_WEBHOOK_URL - using fallback fare`);
          const fallbackFare = "£15.00";
          const fallbackEta = "8 minutes";
          
          sessionState.pendingQuote = {
            fare: fallbackFare,
            eta: fallbackEta,
            pickup: finalPickup,
            destination: finalDestination,
            pickup_time: sessionState.booking.pickup_time || null,
            callback_url: null,
            timestamp: Date.now(),
            lastPrompt: null
          };
          
          result = {
            success: true,
            fare: fallbackFare,
            eta: fallbackEta,
            message: `Taxi quote ready: ${fallbackFare}, ETA ${fallbackEta}`
          };
          break;
        }

        case "cancel_booking": {
          console.log(`[${sessionState.callId}] 🚫 Cancelling booking - hasActiveBooking: ${sessionState.hasActiveBooking}, pickup: ${sessionState.booking.pickup}, bookingFullyConfirmed: ${sessionState.bookingFullyConfirmed}`);
          
          // ALWAYS send cancellation webhook when cancel_booking is called
          // The dispatch system should handle if there was no booking
          const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
          if (DISPATCH_WEBHOOK_URL) {
            try {
              console.log(`[${sessionState.callId}] 📡 Sending cancellation webhook to: ${DISPATCH_WEBHOOK_URL}`);
              
              // Format phone number for WhatsApp
              let formattedPhone = sessionState.phone?.replace(/^\+/, '') || '';
              if (formattedPhone.startsWith('0')) {
                formattedPhone = formattedPhone.slice(1);
              }
              
              const bookingCallId = sessionState.activeBookingCallId || sessionState.callId;
              const cancelPayload = {
                call_id: bookingCallId,
                caller_phone: formattedPhone,
                caller_name: sessionState.customerName || "Unknown",
                action: "cancelled",
                response: "cancelled",
                cancelled_pickup: sessionState.booking.pickup || "Not specified",
                cancelled_destination: sessionState.booking.destination || "Not specified",
                cancellation_reason: "customer_request",
                timestamp: new Date().toISOString(),
              };
              
              console.log(`[${sessionState.callId}] 📤 Cancel payload:`, JSON.stringify(cancelPayload));
              
              const controller = new AbortController();
              const timeoutId = setTimeout(() => controller.abort(), 5000);
              
              const cancelResp = await fetch(DISPATCH_WEBHOOK_URL, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(cancelPayload),
                signal: controller.signal,
              });
              
              clearTimeout(timeoutId);
              const respText = await cancelResp.text();
              console.log(`[${sessionState.callId}] 📬 Cancellation webhook response: ${cancelResp.status} - ${respText}`);
            } catch (cancelErr) {
              console.error(`[${sessionState.callId}] ⚠️ Cancellation webhook failed:`, cancelErr);
              // Continue with local cancellation even if webhook fails
            }
          } else {
            console.log(`[${sessionState.callId}] ⚠️ No DISPATCH_WEBHOOK_URL configured - skipping webhook`);
          }
          
          // Mark the active booking as cancelled so it won't load on the next call.
          // IMPORTANT: use the ORIGINAL booking call_id (activeBookingCallId) when available.
          try {
            const bookingCallId = sessionState.activeBookingCallId || sessionState.callId;
            const phoneKey = sessionState.phone ? normalizePhone(sessionState.phone) : null;
            const altPhone = sessionState.phone?.replace(/^\+/, '') || null;

            console.log(`[${sessionState.callId}] 🧾 Cancelling booking in DB (bookingCallId=${bookingCallId}, phoneKey=${phoneKey}, altPhone=${altPhone})`);

            let q = supabase
              .from("bookings")
              .update({
                status: "cancelled",
                cancelled_at: new Date().toISOString(),
                cancellation_reason: "customer_request",
                updated_at: new Date().toISOString(),
              })
              .in("status", ["confirmed", "dispatched", "active", "pending"]);

            // Prefer exact match by booking call_id
            q = q.eq("call_id", bookingCallId);

            // Fallback by phone if we have it (covers call_id reuse changes)
            if (phoneKey || altPhone) {
              const phoneOr = [phoneKey ? `caller_phone.eq.${phoneKey}` : null, altPhone ? `caller_phone.eq.${altPhone}` : null]
                .filter(Boolean)
                .join(",");
              if (phoneOr) q = q.or(phoneOr);
            }

            const { data: cancelledRows, error: cancelDbError } = await q.select("id, call_id, status");

            if (cancelDbError) {
              console.error(`[${sessionState.callId}] ⚠️ Failed to cancel booking in DB:`, cancelDbError);
            } else {
              console.log(`[${sessionState.callId}] ✅ Cancelled bookings in DB: ${cancelledRows?.length || 0}`);
            }
          } catch (dbErr) {
            console.error(`[${sessionState.callId}] ⚠️ DB cancel error:`, dbErr);
          }
          
          // Clear session state
          sessionState.hasActiveBooking = false;
          sessionState.booking = { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, pickup_time: null, version: 0 };
          sessionState.bookingStep = "pickup"; // ✅ Reset to start of booking flow
          sessionState.bookingStepAdvancedAt = Date.now();
          sessionState.pendingQuote = null;
          sessionState.quoteRequestedAt = null;
          sessionState.quoteTripKey = null;
          sessionState.lastQuotePromptAt = null;
          sessionState.lastQuotePromptText = null;
          sessionState.lastConfirmedTripKey = null;
          result = { success: true, message: "Booking cancelled and removed" };
          break;
        }

        case "modify_booking": {
          // Demo mode: booking modifications are intentionally disabled.
          console.log(`[${sessionState.callId}] 🧪 modify_booking called but disabled (DEMO_SIMPLE_MODE=true)`);
          result = {
            success: false,
            error: "disabled_in_demo",
            message: "Booking updates are disabled for the demo. Please start a new booking instead."
          };
          break;
        }

        case "find_nearby_places":
          console.log(`[${sessionState.callId}] 📍 Finding:`, args.category);
          result = {
            places: [
              { name: "The Grand Hotel", rating: 4.5 },
              { name: "Riverside Restaurant", rating: 4.7 }
            ]
          };
          break;

        case "verify_booking": {
          console.log(`[${sessionState.callId}] 🔍 Pre-confirmation booking verification...`);
          
          // DEMO_SIMPLE_MODE: Skip AI extraction, use session state directly (desktop will verify)
          let verifiedBooking: {
            pickup?: string | null;
            destination?: string | null;
            passengers?: number | null;
            luggage?: string | null;
            vehicle_type?: string | null;
            pickup_time?: string | null;
            special_requests?: string | null;
            missing_fields?: string[];
            confidence?: string;
            extraction_notes?: string;
          } = {};
          
          if (DEMO_SIMPLE_MODE) {
            console.log(`[${sessionState.callId}] 🎭 DEMO MODE: Skipping AI extraction, using session state. Desktop will verify addresses.`);
            verifiedBooking = {
              pickup: sessionState.booking.pickup || null,
              destination: sessionState.booking.destination || null,
              passengers: sessionState.booking.passengers ?? null,
              luggage: sessionState.booking.bags ? String(sessionState.booking.bags) : null,
              vehicle_type: sessionState.booking.vehicle_type || "saloon",
              pickup_time: sessionState.booking.pickup_time || "now",
              missing_fields: [],
              confidence: "high",
              extraction_notes: "Demo mode - addresses not verified by AI"
            };
          } else {
            // Run AI extraction on full conversation to get all booking components
            const conversationForVerification = sessionState.transcripts
              .filter(t => t.role === "user" || t.role === "assistant")
              .slice(-15); // More context for thorough extraction
            
            if (conversationForVerification.length > 0) {
              try {
                // Determine if this is a travel hub trip (needs luggage)
                const recentText = conversationForVerification
                  .map(t => t.text.toLowerCase())
                  .join(" ");
                const isTravelHub = /airport|station|terminal|heathrow|gatwick|stansted|luton|birmingham|manchester|king'?s cross|st pancras|euston|paddington|victoria|coach station/i.test(recentText);
                
                console.log(`[${sessionState.callId}] 🔍 Travel hub trip: ${isTravelHub}, phone: ${sessionState.phone}`);
                
                const extractionResponse = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract-unified`, {
                  method: "POST",
                  headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`
                  },
                  body: JSON.stringify({
                    conversation: conversationForVerification,
                    caller_name: sessionState.customerName,
                    caller_phone: normalizePhone(sessionState.phone), // For alias lookup
                    is_travel_hub_trip: isTravelHub,
                    is_modification: false,
                    current_booking: sessionState.booking.pickup ? {
                      pickup: sessionState.booking.pickup,
                      destination: sessionState.booking.destination,
                      passengers: sessionState.booking.passengers,
                      luggage: sessionState.booking.bags
                    } : undefined
                  })
                });
                
                if (extractionResponse.ok) {
                  verifiedBooking = await extractionResponse.json();
                  console.log(`[${sessionState.callId}] 🔍 Verified booking:`, verifiedBooking);
                }
              } catch (extractErr) {
                console.error(`[${sessionState.callId}] Verification extraction failed:`, extractErr);
              }
            }
          }
          
          // Build result with all extracted components
          const missingFields = verifiedBooking.missing_fields || [];
          
          // Check for essential missing fields
          if (!verifiedBooking.pickup) missingFields.push("pickup");
          if (!verifiedBooking.destination) missingFields.push("destination");
          
          // Determine recommended vehicle type based on passengers + luggage
          let recommendedVehicle = verifiedBooking.vehicle_type || "saloon";
          const pax = verifiedBooking.passengers || 1;
          const bags = parseInt(String(verifiedBooking.luggage || "0").replace(/\D/g, "")) || 0;
          
          if (pax >= 7) {
            recommendedVehicle = "8-seater";
          } else if (pax >= 5) {
            recommendedVehicle = "mpv";
          } else if (pax === 4 && bags > 0) {
            recommendedVehicle = "estate";
          } else if (bags >= 4) {
            recommendedVehicle = "estate";
          }
          
          result = {
            success: true,
            pickup: verifiedBooking.pickup || null,
            destination: verifiedBooking.destination || null,
            passengers: pax,
            luggage: verifiedBooking.luggage || null,
            vehicle_type: recommendedVehicle,
            pickup_time: verifiedBooking.pickup_time || "now",
            special_requests: verifiedBooking.special_requests || null,
            missing_fields: [...new Set(missingFields)], // Dedupe
            confidence: verifiedBooking.confidence || "medium",
            extraction_notes: verifiedBooking.extraction_notes || null,
            message: missingFields.length > 0
              ? `Missing information: ${missingFields.join(", ")}. Please ask the customer for these details.`
              : `All booking details verified. Pickup: "${verifiedBooking.pickup}", Destination: "${verifiedBooking.destination}", ${pax} passenger(s), Vehicle: ${recommendedVehicle}. Proceed to confirm with customer.`
          };
          
          // ✅ DEMO_SIMPLE_MODE: Set newBookingPromptPending when Ada is about to summarize
          // This blocks request_quote until user explicitly confirms "yes/correct"
          if (DEMO_SIMPLE_MODE && missingFields.length === 0) {
            sessionState.newBookingPromptPending = true;
            sessionState.lastNewBookingPromptAt = Date.now();
            console.log(`[${sessionState.callId}] 🎭 DEMO MODE: Set newBookingPromptPending=true - waiting for user to confirm summary`);
          }
          
          console.log(`[${sessionState.callId}] ✅ Verification result: ${missingFields.length} missing fields`);
          break;
        }

        case "end_call": {
          console.log(`[${sessionState.callId}] 👋 Ending call`);
          result = { 
            success: true,
            message: "Call ending. Say 'Safe travels!' or a brief goodbye, then the call will end."
          };
          
          // === STT ACCURACY METRICS SUMMARY ===
          const m = sessionState.sttMetrics;
          const audioMode = sessionState.useRasaAudioProcessing ? "RASA (8→16kHz)" : "STANDARD (8→24kHz)";
          const correctionRate = m.totalTranscripts > 0 
            ? ((m.correctedTranscripts / m.totalTranscripts) * 100).toFixed(1) 
            : "0.0";
          const hallucinationRate = (m.totalTranscripts + m.filteredHallucinations) > 0
            ? ((m.filteredHallucinations / (m.totalTranscripts + m.filteredHallucinations)) * 100).toFixed(1)
            : "0.0";
          
          console.log(`[${sessionState.callId}] ═══════════════════════════════════════════════════════════`);
          console.log(`[${sessionState.callId}] 📊 STT ACCURACY METRICS - ${audioMode}`);
          console.log(`[${sessionState.callId}] ───────────────────────────────────────────────────────────`);
          console.log(`[${sessionState.callId}]   Total transcripts:      ${m.totalTranscripts}`);
          console.log(`[${sessionState.callId}]   Total words:            ${m.totalWords}`);
          console.log(`[${sessionState.callId}]   Total characters:       ${m.totalChars}`);
          console.log(`[${sessionState.callId}]   Corrected transcripts:  ${m.correctedTranscripts} (${correctionRate}%)`);
          console.log(`[${sessionState.callId}]   Filtered hallucinations: ${m.filteredHallucinations} (${hallucinationRate}%)`);
          console.log(`[${sessionState.callId}]   Duplicate fare prompts: ${m.duplicateFarePrompts}`);
          console.log(`[${sessionState.callId}]   Avg transcript delay:   ${m.avgTranscriptDelayMs.toFixed(0)}ms`);
          console.log(`[${sessionState.callId}]   Avg speech duration:    ${m.avgSpeechDurationMs.toFixed(0)}ms`);
          console.log(`[${sessionState.callId}] ═══════════════════════════════════════════════════════════`);
          
          // === AUDIO DIAGNOSTICS SUMMARY ===
          const ad = sessionState.audioDiagnostics;
          console.log(`[${sessionState.callId}] 📊 AUDIO DIAGNOSTICS SUMMARY (INBOUND)`);
          console.log(`[${sessionState.callId}] ───────────────────────────────────────────────────────────`);
          console.log(`[${sessionState.callId}]   Packets received:       ${ad.packetsReceived}`);
          console.log(`[${sessionState.callId}]   Packets forwarded:      ${ad.packetsForwarded}`);
          console.log(`[${sessionState.callId}]   Skipped (noise):        ${ad.packetsSkippedNoise}`);
          console.log(`[${sessionState.callId}]   Skipped (echo):         ${ad.packetsSkippedEcho}`);
          console.log(`[${sessionState.callId}]   Skipped (bot speaking): ${ad.packetsSkippedBotSpeaking}`);
          console.log(`[${sessionState.callId}]   Skipped (greeting):     ${ad.packetsSkippedGreeting}`);
          console.log(`[${sessionState.callId}]   Skipped (summary):      ${ad.packetsSkippedSummary}`);
          console.log(`[${sessionState.callId}]   Avg RMS:                ${ad.avgRms.toFixed(0)}`);
          console.log(`[${sessionState.callId}] ───────────────────────────────────────────────────────────`);
          
          // === TTS (OUTBOUND) DIAGNOSTICS SUMMARY ===
          const td = sessionState.ttsDiagnostics;
          console.log(`[${sessionState.callId}] 🔊 TTS DIAGNOSTICS SUMMARY (OUTBOUND)`);
          console.log(`[${sessionState.callId}] ───────────────────────────────────────────────────────────`);
          console.log(`[${sessionState.callId}]   Chunks sent:            ${td.chunksSent}`);
          console.log(`[${sessionState.callId}]   Chunks discarded:       ${td.chunksDiscarded}`);
          console.log(`[${sessionState.callId}]   Avg RMS:                ${td.avgRms.toFixed(0)}`);
          console.log(`[${sessionState.callId}]   Peak RMS:               ${td.peakRms.toFixed(0)}`);
          console.log(`[${sessionState.callId}] ═══════════════════════════════════════════════════════════`);
          
          
          // Immediate flush on call end - capture all transcripts
          immediateFlush(sessionState);
          // Update call status
          await supabase.from("live_calls")
            .update({ status: "completed", ended_at: new Date().toISOString() })
            .eq("call_id", sessionState.callId);
          
          // Save preferred_language to callers table if we have a phone number
          // This ensures the language is remembered for the next call
          if (sessionState.phone && sessionState.language) {
            const phoneKey = normalizePhone(sessionState.phone);
            try {
              await supabase.from("callers").upsert({
                phone_number: phoneKey,
                preferred_language: sessionState.language,
                updated_at: new Date().toISOString()
              }, { onConflict: "phone_number" });
              console.log(`[${sessionState.callId}] 🌐 Saved preferred language: ${sessionState.language}`);
            } catch (e) {
              console.error(`[${sessionState.callId}] Failed to save preferred language:`, e);
            }
          }
          
          // Mark call as ending (but not ended yet - let goodbye play)
          sessionState.callEnded = true;
          
          // Cancel any active response before injecting goodbye using safeCancel
          safeCancel(sessionState, "end_call - preparing goodbye");
          openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          
          // Allow final goodbye to play even though callEnded=true
          sessionState.finalGoodbyePending = true;
          sessionState.discardCurrentResponseAudio = false;
          sessionState.audioVerified = true;
          sessionState.pendingAudioBuffer = [];
          
          // Pick a random WhatsApp tip for the full closing message
          const closingWhatsappTips = [
            "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
            "Next time, feel free to book your taxi using a WhatsApp voice message.",
            "You can always book again by simply sending us a voice note on WhatsApp."
          ];
          const closingRandomTip = closingWhatsappTips[Math.floor(Math.random() * closingWhatsappTips.length)];
          
          // Inject full closing message with WhatsApp tip and demo sign-off
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: `[SYSTEM: The customer is done. Say EXACTLY this closing message, then stay silent:
"You'll receive the booking details and ride updates via WhatsApp. ${closingRandomTip} Thank you for trying the Taxibot demo, and have a safe journey."]` }]
            }
          }));
          
          // Trigger Ada to speak the goodbye
          safeResponseCreate(sessionState, "end_call_goodbye");
          
          // === GRACEFUL CLOSURE: Set flag to prevent socket.onclose from interrupting ===
          closingGracePeriodActive = true;
          console.log(`[${sessionState.callId}] 🛡️ Closing grace period ACTIVE - socket.onclose will wait`);
          
          // Close OpenAI connection after extended delay to let full goodbye audio finish,
          // then tell the bridge to hang up AND close the bridge WebSocket with a proper close frame.
          // (If we only send a hangup message but leave the WS open, the function may be killed by
          // runtime limits and the bridge reports: "no close frame received or sent".)
          closingGraceTimeoutId = setTimeout(() => {
            console.log(`[${sessionState.callId}] 🔌 Closing OpenAI WebSocket after end_call`);
            closingGracePeriodActive = false;

            // Stop keep-alives immediately (we are ending the session)
            try {
              stopKeepAlive();
            } catch {
              // ignore
            }

            // Notify the bridge
            try {
              socket.send(JSON.stringify({ type: "hangup", reason: "end_call" }));
              console.log(`[${sessionState.callId}] 📤 Sent hangup to bridge`);
            } catch (e) {
              console.warn(`[${sessionState.callId}] ⚠️ Failed to send hangup to bridge:`, e);
            }

            // Close the bridge WebSocket gracefully (send close frame)
            try {
              isConnectionClosed = true;
              socket.close(1000, "end_call");
              console.log(`[${sessionState.callId}] 📴 Closed bridge WebSocket (1000 end_call)`);
            } catch (e) {
              console.warn(`[${sessionState.callId}] ⚠️ Failed to close bridge WebSocket:`, e);
            }

            // Close OpenAI WS
            try {
              openaiWs?.close();
            } catch {
              // ignore
            }
          }, 5000) as unknown as number; // 5 second delay - enough for goodbye audio to finish
          
          // Return early - don't trigger another response.create at the end
          return;
        }

        default:
          result = { error: "Unknown function" };
      }
    } catch (err) {
      console.error(`[${sessionState.callId}] Function error:`, err);
      result = { error: "Failed to execute" };
    }

    // Send result back to OpenAI
    openaiWs?.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "function_call_output",
        call_id: callId,
        output: JSON.stringify(result)
      }
    }));

    // === CRITICAL: Only trigger response.create if we want Ada to respond ===
    // If we're awaiting a fare yes/no (pendingQuote / needs_fare_confirm), Ada must WAIT.
    // NOTE: already_confirmed is NOT a wait condition - Ada should speak "Is there anything else?"
    const shouldWaitForCustomer =
      !!sessionState.pendingQuote ||
      result.needs_fare_confirm ||
      result.quote_ready ||
      result.blocked;
    
    if (shouldWaitForCustomer) {
      console.log(
        `[${sessionState.callId}] ⏸️ NOT triggering response.create - waiting for customer input (pendingQuote=${!!sessionState.pendingQuote}, needs_fare_confirm=${result.needs_fare_confirm}, quote_ready=${result.quote_ready}, blocked=${result.blocked})`
      );

      // ✅ CRITICAL: Do NOT inject fare prompt here - let dispatch broadcast handler do it!
      // The dispatch_ask_confirm broadcast already handles fare prompt injection with proper timing.
      // Only inject if there's NO pending quote (meaning dispatch hasn't sent ask_confirm yet).
      // This prevents the triple prompt issue: tool handler + dispatch handler + OpenAI VAD
      if (result.ada_message && !result.blocked && !sessionState.pendingQuote) {
        const now = Date.now();
        const isDuplicatePrompt =
          sessionState.lastQuotePromptAt !== null &&
          now - sessionState.lastQuotePromptAt < 8000;

        if (isDuplicatePrompt) {
          console.log(`[${sessionState.callId}] 🔁 Skipping duplicate fare prompt injection`);
          return;
        }

        console.log(`[${sessionState.callId}] ⚠️ Injecting fare prompt from tool handler (no pendingQuote yet)`);
        sessionState.lastQuotePromptAt = now;
        sessionState.lastQuotePromptText = result.ada_message;

        // CRITICAL: Include explicit YES/NO handling instructions with the fare prompt
        const farePromptWithInstructions = `[SYSTEM: Read this fare quote to the customer: "${result.ada_message}"

After reading it, WAIT SILENTLY for their response.

WHEN CUSTOMER RESPONDS:
- If they say YES, yeah, go ahead, book it, please → CALL book_taxi with confirmation_state: "confirmed" (use same pickup/destination)
- If they say NO, never mind, cancel, too expensive → CALL book_taxi with confirmation_state: "rejected"
- If unclear, ask: "Would you like me to book that?"

DO NOT say "booked" or "confirmed" until the book_taxi tool with confirmation_state: "confirmed" returns success.]`;

        openaiWs?.send(
          JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [
                {
                  type: "input_text",
                  text: farePromptWithInstructions,
                },
              ],
            },
          })
        );
        safeResponseCreate(sessionState, "toolhandler-fareprompt");
      } else if (sessionState.pendingQuote) {
        console.log(`[${sessionState.callId}] ⏳ Skipping tool handler fare prompt - dispatch broadcast will handle it`);
      }

      return;
    }

    // If a tool handler already injected a scripted message and scheduled speech,
    // don't auto-trigger another response.create here.
    if ((result as any)?.suppress_response_create) {
      console.log(`[${sessionState.callId}] ⏭️ Suppressing auto response.create (tool requested manual speech injection)`);
      return;
    }

    // Normal case - trigger response continuation
    safeResponseCreate(sessionState, "handleFunctionCall-normal");
  };

  // --- Bridge WebSocket Handlers ---
  socket.onopen = () => {
    console.log("[simple] Client connected");
  };

  socket.onmessage = async (event) => {
    try {
      // Handle binary audio (Deno WebSocket receives Blob or ArrayBuffer)
      const isBinary = event.data instanceof Blob || event.data instanceof ArrayBuffer || event.data instanceof Uint8Array;
      
      if (isBinary) {
        // ✅ CALL-ENDED GUARD: Don't forward audio if call is ending
        if (state?.callEnded) {
          return;
        }
        
        if (openaiConnected && openaiWs && state) {
          let audioData: Uint8Array;

          if (event.data instanceof Blob) {
            audioData = new Uint8Array(await event.data.arrayBuffer());
          } else if (event.data instanceof ArrayBuffer) {
            audioData = new Uint8Array(event.data);
          } else {
            audioData = event.data;
          }

          // ECHO GUARD: Always ignore audio for a short window after Ada finishes speaking.
          // This prevents immediate TTS echo from being transcribed.
          if (Date.now() < state.echoGuardUntil) {
            return;
          }

          // If we're in half-duplex and Ada is speaking, buffer raw audio and stop.
          // (Half-duplex explicitly disallows barge-in.)
          if (state.isAdaSpeaking && state.halfDuplex) {
            state.halfDuplexBuffer.push(audioData);
            // Cap buffer to prevent memory issues (keep last ~5 seconds at 8kHz, 20ms chunks = 250 chunks)
            if (state.halfDuplexBuffer.length > 250) {
              state.halfDuplexBuffer.shift();
            }
            return;
          }

          // Step 1: Decode audio to PCM16 based on inbound format
          // Bridge can send: ulaw (8kHz), slin (8kHz PCM), or slin16 (16kHz PCM)
          let pcmInput: Int16Array;
          const inputSampleRate = state.inboundSampleRate;

          if (state.inboundAudioFormat === "ulaw") {
            // Decode µ-law to 16-bit PCM (8kHz)
            pcmInput = new Int16Array(audioData.length);
            for (let i = 0; i < audioData.length; i++) {
              const ulaw = ~audioData[i] & 0xff;
              const sign = (ulaw & 0x80) ? -1 : 1;
              const exponent = (ulaw >> 4) & 0x07;
              const mantissa = ulaw & 0x0f;
              let sample = ((mantissa << 3) + 0x84) << exponent;
              sample -= 0x84;
              pcmInput[i] = sign * sample;
            }
          } else {
            // slin or slin16: already PCM16, just read as Int16Array
            pcmInput = new Int16Array(audioData.buffer, audioData.byteOffset, audioData.byteLength / 2);
          }

          // === AUDIO DIAGNOSTICS: Track packet and compute RMS ===
          state.audioDiagnostics.packetsReceived++;
          
          let sumSq = 0;
          for (let i = 0; i < pcmInput.length; i++) {
            const s = pcmInput[i];
            sumSq += s * s;
          }
          const rms = Math.sqrt(sumSq / Math.max(1, pcmInput.length));
          
          // Track RMS for diagnostics (keep last 50 values)
          state.audioDiagnostics.lastRmsValues.push(rms);
          if (state.audioDiagnostics.lastRmsValues.length > 50) {
            state.audioDiagnostics.lastRmsValues.shift();
          }
          state.audioDiagnostics.avgRms = state.audioDiagnostics.lastRmsValues.reduce((a, b) => a + b, 0) / state.audioDiagnostics.lastRmsValues.length;
          
          // Log audio diagnostics every N packets
          if (state.audioDiagnostics.packetsReceived % AUDIO_DIAGNOSTICS_LOG_INTERVAL === 0) {
            const d = state.audioDiagnostics;
            console.log(`[${state.callId}] 📊 Audio: rx=${d.packetsReceived}, fwd=${d.packetsForwarded}, noise=${d.packetsSkippedNoise}, echo=${d.packetsSkippedEcho}, bot=${d.packetsSkippedBotSpeaking}, greet=${d.packetsSkippedGreeting}, summary=${d.packetsSkippedSummary}, avgRMS=${d.avgRms.toFixed(0)}`);
          }

          // === AUDIO FORWARDING POLICY ===
          // CRITICAL FIX: Always forward audio to OpenAI for buffering/STT.
          // Previously we dropped audio when Ada was speaking, causing user responses
          // to be lost if they spoke during or immediately after Ada's questions.
          // Now we ALWAYS forward audio, but track protection states for barge-in decisions.
          
          let skipBargeIn = false;
          
          // === GREETING PROTECTION: Block barge-in during Ada's intro (12 seconds) ===
          if (Date.now() < state.greetingProtectionUntil) {
            state.audioDiagnostics.packetsSkippedGreeting++;
            skipBargeIn = true;
          } else if (Date.now() < state.summaryProtectionUntil) {
            // === SUMMARY PROTECTION: Block barge-in during fare quotes/summaries (8 seconds) ===
            state.audioDiagnostics.packetsSkippedSummary++;
            skipBargeIn = true;
          } else if (state.isAdaSpeaking) {
            // === ADA SPEAKING: Still forward audio but don't allow barge-in ===
            // This is crucial - user may start speaking their answer before Ada finishes.
            // We buffer their audio for STT but don't interrupt Ada.
            state.audioDiagnostics.packetsSkippedBotSpeaking++;
            skipBargeIn = true;
            // NOTE: Do NOT return here - continue to forward audio to OpenAI
          }

          // Track forwarded packets
          state.audioDiagnostics.packetsForwarded++;

          // Step 2: Upsample to 24kHz (OpenAI Realtime API requirement)
          // Handle different input sample rates: 8kHz (ulaw/slin), 16kHz (slin16), or 24kHz (pre-resampled)
          let pcm16_24k: Int16Array;
          
          if (inputSampleRate === 24000) {
            // 24kHz → 24kHz (passthrough - bridge already resampled)
            pcm16_24k = pcmInput;
          } else if (inputSampleRate === 16000) {
            // 16kHz → 24kHz (1.5x using 3:2 ratio)
            const outLen = Math.floor(pcmInput.length * 3 / 2);
            pcm16_24k = new Int16Array(outLen);
            for (let i = 0; i < outLen; i++) {
              const srcIdx = (i * 2) / 3;
              const idx0 = Math.floor(srcIdx);
              const idx1 = Math.min(idx0 + 1, pcmInput.length - 1);
              const frac = srcIdx - idx0;
              pcm16_24k[i] = Math.round(pcmInput[idx0] * (1 - frac) + pcmInput[idx1] * frac);
            }
          } else if (state.useRasaAudioProcessing) {
            // RASA mode: 8kHz → 16kHz → 24kHz (two-stage upsampling)
            // Stage 1: 8kHz → 16kHz (2x)
            const pcm16_16k = new Int16Array(pcmInput.length * 2);
            for (let i = 0; i < pcmInput.length - 1; i++) {
              const s0 = pcmInput[i];
              const s1 = pcmInput[i + 1];
              pcm16_16k[i * 2] = s0;
              pcm16_16k[i * 2 + 1] = Math.round((s0 + s1) / 2);
            }
            const last8k = pcmInput.length - 1;
            pcm16_16k[last8k * 2] = pcmInput[last8k];
            pcm16_16k[last8k * 2 + 1] = pcmInput[last8k];
            
            // Stage 2: 16kHz → 24kHz (1.5x)
            const outLen = Math.floor(pcm16_16k.length * 3 / 2);
            pcm16_24k = new Int16Array(outLen);
            for (let i = 0; i < outLen; i++) {
              const srcIdx = (i * 2) / 3;
              const idx0 = Math.floor(srcIdx);
              const idx1 = Math.min(idx0 + 1, pcm16_16k.length - 1);
              const frac = srcIdx - idx0;
              pcm16_24k[i] = Math.round(pcm16_16k[idx0] * (1 - frac) + pcm16_16k[idx1] * frac);
            }
          } else {
            // Standard: 8kHz → 24kHz (3x linear interpolation)
            pcm16_24k = new Int16Array(pcmInput.length * 3);
            for (let i = 0; i < pcmInput.length - 1; i++) {
              const s0 = pcmInput[i];
              const s1 = pcmInput[i + 1];
              pcm16_24k[i * 3] = s0;
              pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
              pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
            }
            // Handle last sample
            const lastIdx = pcmInput.length - 1;
            pcm16_24k[lastIdx * 3] = pcmInput[lastIdx];
            pcm16_24k[lastIdx * 3 + 1] = pcmInput[lastIdx];
            pcm16_24k[lastIdx * 3 + 2] = pcmInput[lastIdx];
          }

          // Step 3: Convert to base64
          const bytes = new Uint8Array(pcm16_24k.buffer);
          let binary = "";
          for (let i = 0; i < bytes.length; i++) {
            binary += String.fromCharCode(bytes[i]);
          }
          const base64Audio = btoa(binary);

          openaiWs.send(JSON.stringify({
            type: "input_audio_buffer.append",
            audio: base64Audio
          }));
          
          // Track that we have audio buffered (for safe commit logic)
          // Also track milliseconds based on 24kHz sample rate (2 bytes per sample)
          if (state) {
            state.audioBufferedSinceSpeechStart = true;
            // pcm16_24k at 24kHz: bytes / 2 = samples, samples / 24 = ms
            const audioMs = pcm16_24k.length / 24;
            state.audioBufferedMs += audioMs;
          }
        }
        return;
      }

      // Handle JSON messages
      const message = JSON.parse(event.data);

      // PRE-CONNECT: Connect to OpenAI while phone is ringing (before answer)
      if (message.type === "pre_connect") {
        const callId = message.call_id || `simple-${Date.now()}`;
        const phone = message.phone || "unknown";
        
        console.log(`[${callId}] 🔔 Pre-connecting to OpenAI (phone ringing)`);
        
        // Detect language early
        const detectedLanguage = detectLanguageFromPhone(phone);
        const finalLanguage = message.language || detectedLanguage || "auto";
        
        // Create minimal state for pre-connection
        state = {
          callId,
          phone,
          companyName: message.company_name || DEFAULT_COMPANY,
          agentName: message.agent_name || DEFAULT_AGENT,
          voice: message.voice || DEFAULT_VOICE,
          language: finalLanguage,
          customerName: null,
          hasActiveBooking: false,
          activeBookingCallId: null,
          booking: { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, pickup_time: null, version: 0 },
          transcripts: [],
          callerLastPickup: null,
          callerLastDestination: null,
          callerTotalBookings: 0,
          assistantTranscriptIndex: null,
          transcriptFlushTimer: null,
          isAdaSpeaking: false,
          echoGuardUntil: 0,
          responseStartTime: 0,
          bargeInIgnoreUntil: 0,
          openAiResponseActive: false,
          deferredResponseCreate: false,
          speechStartTime: null,
          speechStopTime: null,
          lastUserTranscriptAt: null,
          greetingProtectionUntil: Date.now() + GREETING_PROTECTION_MS,
          summaryProtectionUntil: 0,
          audioDiagnostics: {
            packetsReceived: 0,
            packetsForwarded: 0,
            packetsSkippedNoise: 0,
            packetsSkippedEcho: 0,
            packetsSkippedBotSpeaking: 0,
            packetsSkippedGreeting: 0,
            packetsSkippedSummary: 0,
            lastRmsValues: [],
            avgRms: 0,
          },
          ttsDiagnostics: {
            chunksSent: 0,
            chunksDiscarded: 0,
            lastRmsValues: [],
            avgRms: 0,
            peakRms: 0,
          },
          callEnded: false,
          callStartedAt: Date.now(),
          finalGoodbyePending: false,
          inboundAudioFormat: (message.inbound_format as "ulaw" | "slin" | "slin16") ?? "ulaw",
          inboundSampleRate: message.inbound_sample_rate || (message.inbound_format === "slin16" ? 16000 : 8000),
          useRasaAudioProcessing: message.rasa_audio_processing ?? false,
          halfDuplex: message.half_duplex ?? false,
          halfDuplexBuffer: [],
          bookingConfirmedThisTurn: false,
          bookingFullyConfirmed: false,
          lastBookTaxiSuccessAt: null,
          lastConfirmedTripKey: null,
           audioVerified: true, // Start verified - only buffer after user confirms addresses
           pendingAudioBuffer: [],
           discardCurrentResponseAudio: false,
           pendingQuote: null,
          quoteRequestedAt: null,
          quoteTripKey: null,
          lastQuotePromptAt: null,
           lastQuotePromptText: null,
           pendingDispatchEvents: [],
           pendingModification: null,
           extractionInProgress: false,
           awaitingDispatchCallback: false,
           lastModificationPromptAt: null,
           lastModificationPromptKey: null,
           modificationPromptPending: false,
           lastSpokenConfirmation: null,
           lastSpokenConfirmationAt: null,
           pendingNewBooking: null,
           newBookingPromptPending: false,
           lastNewBookingPromptAt: null,
           activeBookingAcknowledged: false,
           askedAnythingElse: false,
           askedAnythingElseAt: null,
           goodbyeGraceMs: message.goodbye_grace_ms ?? 3000, // From agent config or default 3s
           confirmationResponsePending: false,
           sttMetrics: {
            totalTranscripts: 0,
            totalWords: 0,
            totalChars: 0,
            correctedTranscripts: 0,
            filteredHallucinations: 0,
            duplicateFarePrompts: 0,
            avgTranscriptDelayMs: 0,
            transcriptDelays: [],
            avgSpeechDurationMs: 0,
            speechDurations: [],
          },
          lastQuestionType: null,
          lastQuestionAt: null,
          lastPassengerMismatchAt: null,
          questionTypeAtSpeechStart: null, // Snapshot for race condition fix
          lastSpokenQuestion: null,
          lastSpokenQuestionAt: null,
          lastMultiQuestionFixAt: null,
          greetingDelivered: false,
          bookingStep: "pickup", // Start at pickup step
          bookingStepAdvancedAt: null,
          confirmationSpoken: false,
          confirmationFailsafeTimerId: null,
          confirmationAskedAt: null, // When Ada asked "Is that correct?" - for timeout fallback
          adaAskedQuestionAt: null, // Track when Ada last asked any question
          // Authoritative transcript extraction (prevents hallucinations)
          transcriptExtractedPickup: null,
          transcriptExtractedDestination: null,
          transcriptExtractedPassengers: null,
          transcriptExtractedTime: null,
          // Audio buffer tracking (for safe commits)
          audioBufferedSinceSpeechStart: false,
          audioBufferedMs: 0, // Milliseconds of audio buffered since last commit/clear
          partialTranscript: "", // Accumulated partial transcript for punctuation detection
          punctuationCommitSent: false, // Prevent double-commits from punctuation detection
          didCommitThisUtterance: false,
          pendingTurnResponseCreate: false,
          allowOneResponseWhileAwaitingUserAnswer: false,
          recentErrorCount: 0,
          lastErrorAt: null,
          // Strict turn-based protocol
          awaitingUserAnswer: false,
          awaitingUserAnswerSince: null,
          awaitingAnswerForStep: null,
        };
        
        preConnected = true;
        connectToOpenAI(state!, false); // Connect but DON'T trigger greeting yet
        
        socket.send(JSON.stringify({ 
          type: "pre_connected", 
          call_id: callId
        }));
        return;
      }

      if (message.type === "init") {
        const callId = message.call_id || `simple-${Date.now()}`;
        const phone = message.phone || "unknown";
        const isReconnect = message.reconnect === true || message.resume === true;
        const resumeCallId = message.resume_call_id || null;
        
        console.log(`[${callId}] 🚀 Initializing simple session (reconnect=${isReconnect}, resume_call_id=${resumeCallId}, preConnected=${preConnected})`);

        // ═══════════════════════════════════════════════════════════════════
        // SESSION RESTORATION: If bridge sends reconnect=true or resume=true,
        // restore state from live_calls table instead of starting fresh greeting
        // ═══════════════════════════════════════════════════════════════════
        let restoredSession: any = null;
        let skipGreeting = false;
        
        if (isReconnect) {
          console.log(`[${callId}] 🔄 RESUME: Looking up session ${resumeCallId || callId} or phone ${phone}`);
          
          try {
            // First try by call_id
            if (resumeCallId || callId) {
              const lookupId = resumeCallId || callId;
              const { data: callData } = await supabase
                .from("live_calls")
                .select("*")
                .eq("call_id", lookupId)
                .maybeSingle();
              
              if (callData && !callData.ended_at) {
                restoredSession = callData;
                console.log(`[${callId}] ✅ Found session by call_id: ${lookupId}`);
              }
            }
            
            // Fallback: lookup by phone number (most recent active call in last 10 minutes)
            if (!restoredSession && phone && phone !== "unknown") {
              const phoneKey = normalizePhone(phone);
              const tenMinutesAgo = new Date(Date.now() - 10 * 60 * 1000).toISOString();
              const { data: phoneData } = await supabase
                .from("live_calls")
                .select("*")
                .eq("caller_phone", phoneKey)
                .in("status", ["active", "awaiting_confirmation", "confirmed"])
                .gt("updated_at", tenMinutesAgo)
                .order("updated_at", { ascending: false })
                .limit(1)
                .maybeSingle();
              
              if (phoneData && !phoneData.ended_at) {
                restoredSession = phoneData;
                console.log(`[${callId}] ✅ Found session by phone fallback: ${phoneData.call_id}`);
              }
            }
            
            if (restoredSession) {
              skipGreeting = true;
              console.log(`[${callId}] 📦 RESTORED STATE: pickup=${restoredSession.pickup}, dest=${restoredSession.destination}, pax=${restoredSession.passengers}`);
            } else {
              console.log(`[${callId}] ♻️ No session found to restore - will use fresh greeting`);
            }
          } catch (e) {
            console.error(`[${callId}] Session restore lookup failed:`, e);
            // Continue without restoration
          }
        }

        // Detect language from phone number FIRST (fast, no DB)
        const detectedLanguage = detectLanguageFromPhone(phone);
        const finalLanguage = message.language || detectedLanguage || "auto";
        
        // Update or create state
        if (state && preConnected) {
          // Update existing pre-connected state with full details
          state.phone = phone;
          state.language = finalLanguage;
          state.customerName = message.customer_name || null;
          state.hasActiveBooking = message.has_active_booking || false;
          console.log(`[${callId}] ⚡ Using pre-connected OpenAI session`);
        } else {
          // Initialize state fresh
          state = {
            callId,
            phone,
            companyName: message.company_name || DEFAULT_COMPANY,
            agentName: message.agent_name || DEFAULT_AGENT,
            voice: message.voice || DEFAULT_VOICE,
            language: finalLanguage,
            customerName: message.customer_name || null,
            hasActiveBooking: message.has_active_booking || false,
            activeBookingCallId: null,
            booking: { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, pickup_time: null, version: 0 },
            transcripts: [],
            callerLastPickup: null,
            callerLastDestination: null,
            callerTotalBookings: 0,
            assistantTranscriptIndex: null,
            transcriptFlushTimer: null,
            isAdaSpeaking: false,
            echoGuardUntil: 0,
            responseStartTime: 0,
            bargeInIgnoreUntil: 0,
            openAiResponseActive: false,
            deferredResponseCreate: false,
             speechStartTime: null,
             speechStopTime: null,
             lastUserTranscriptAt: null,
             greetingProtectionUntil: Date.now() + GREETING_PROTECTION_MS,
             summaryProtectionUntil: 0,
             audioDiagnostics: {
               packetsReceived: 0,
               packetsForwarded: 0,
               packetsSkippedNoise: 0,
               packetsSkippedEcho: 0,
               packetsSkippedBotSpeaking: 0,
               packetsSkippedGreeting: 0,
               packetsSkippedSummary: 0,
               lastRmsValues: [],
               avgRms: 0,
             },
             ttsDiagnostics: {
               chunksSent: 0,
               chunksDiscarded: 0,
               lastRmsValues: [],
               avgRms: 0,
               peakRms: 0,
             },
             callEnded: false,
             callStartedAt: Date.now(),
             finalGoodbyePending: false,
             inboundAudioFormat: (message.inbound_format as "ulaw" | "slin" | "slin16") ?? "ulaw",
             inboundSampleRate: message.inbound_sample_rate || (message.inbound_format === "slin16" ? 16000 : 8000),
             useRasaAudioProcessing: message.rasa_audio_processing ?? false,
            halfDuplex: message.half_duplex ?? false,
            halfDuplexBuffer: [],
            bookingConfirmedThisTurn: false,
            bookingFullyConfirmed: false,
            lastBookTaxiSuccessAt: null,
            lastConfirmedTripKey: null,
             audioVerified: true, // Start verified - only buffer after user confirms addresses
             pendingAudioBuffer: [],
             discardCurrentResponseAudio: false,
             pendingQuote: null,
            quoteRequestedAt: null,
            quoteTripKey: null,
            lastQuotePromptAt: null,
             lastQuotePromptText: null,
             pendingDispatchEvents: [],
             pendingModification: null,
             extractionInProgress: false,
             awaitingDispatchCallback: false,
             lastModificationPromptAt: null,
             lastModificationPromptKey: null,
             modificationPromptPending: false,
             lastSpokenConfirmation: null,
             lastSpokenConfirmationAt: null,
             pendingNewBooking: null,
             newBookingPromptPending: false,
             lastNewBookingPromptAt: null,
             activeBookingAcknowledged: false,
             askedAnythingElse: false,
             askedAnythingElseAt: null,
             goodbyeGraceMs: message.goodbye_grace_ms ?? 3000, // From agent config or default 3s
             confirmationResponsePending: false,
             sttMetrics: {
              totalTranscripts: 0,
              totalWords: 0,
              totalChars: 0,
              correctedTranscripts: 0,
              filteredHallucinations: 0,
              duplicateFarePrompts: 0,
              avgTranscriptDelayMs: 0,
              transcriptDelays: [],
              avgSpeechDurationMs: 0,
              speechDurations: [],
            },
            lastQuestionType: null,
            lastQuestionAt: null,
            lastPassengerMismatchAt: null,
            questionTypeAtSpeechStart: null, // Snapshot for race condition fix
            lastSpokenQuestion: null,
            lastSpokenQuestionAt: null,
            lastMultiQuestionFixAt: null,
            greetingDelivered: skipGreeting, // If resuming, mark greeting as already delivered
            bookingStep: "pickup", // Start at pickup step (may be updated below)
            bookingStepAdvancedAt: null,
            confirmationSpoken: false,
            confirmationFailsafeTimerId: null,
            confirmationAskedAt: null, // When Ada asked "Is that correct?" - for timeout fallback
            adaAskedQuestionAt: null, // Track when Ada last asked any question
            // Authoritative transcript extraction (prevents hallucinations)
            transcriptExtractedPickup: null,
            transcriptExtractedDestination: null,
            transcriptExtractedPassengers: null,
            transcriptExtractedTime: null,
            // Audio buffer tracking (for safe commits)
            audioBufferedSinceSpeechStart: false,
            audioBufferedMs: 0, // Milliseconds of audio buffered since last commit/clear
            partialTranscript: "", // Accumulated partial transcript for punctuation detection
            punctuationCommitSent: false, // Prevent double-commits from punctuation detection
            didCommitThisUtterance: false,
            pendingTurnResponseCreate: false,
             allowOneResponseWhileAwaitingUserAnswer: false,
            recentErrorCount: 0,
            lastErrorAt: null,
            // Strict turn-based protocol
            awaitingUserAnswer: false,
            awaitingUserAnswerSince: null,
            awaitingAnswerForStep: null,
          };
          
          // ═══════════════════════════════════════════════════════════════════
          // APPLY RESTORED SESSION DATA (if available)
          // ═══════════════════════════════════════════════════════════════════
          if (state && restoredSession) {
            // Restore booking state
            state.booking.pickup = restoredSession.pickup || null;
            state.booking.destination = restoredSession.destination || null;
            state.booking.passengers = restoredSession.passengers || null;
            state.customerName = restoredSession.caller_name || state.customerName;
            state.callerTotalBookings = restoredSession.caller_total_bookings || 0;
            state.callerLastPickup = restoredSession.caller_last_pickup || null;
            state.callerLastDestination = restoredSession.caller_last_destination || null;
            state.bookingFullyConfirmed = restoredSession.booking_confirmed || false;
            
            // Restore conversation history
            if (Array.isArray(restoredSession.transcripts)) {
              state.transcripts = restoredSession.transcripts.map((t: any) => ({
                role: t.role || "user",
                text: t.text || t.content || "",
                timestamp: t.timestamp || new Date().toISOString()
              }));
            }
            
            // ✅ CRITICAL: Restore fare/eta from live_calls so we can resume from fare confirmation
            if (restoredSession.fare || restoredSession.eta) {
              state.pendingQuote = {
                fare: restoredSession.fare || null,
                eta: restoredSession.eta || null,
                pickup: state.booking.pickup,
                destination: state.booking.destination,
                pickup_time: null, // Will be re-captured if needed
                callback_url: null,
                timestamp: Date.now(),
                lastPrompt: null
              };
              console.log(`[${callId}] 💰 Restored pendingQuote: fare=${restoredSession.fare}, eta=${restoredSession.eta}`);
            }
            
            // Compute what step we're on based on restored state
            // PRIORITY ORDER: booking_step from DB > fare/eta > status > field presence
            if (state.bookingFullyConfirmed) {
              state.bookingStep = "confirmed";
            } else if (restoredSession.booking_step) {
              // ✅ Use saved booking_step from database if available
              state.bookingStep = restoredSession.booking_step as typeof state.bookingStep;
              console.log(`[${callId}] 📋 Restored bookingStep from DB: ${state.bookingStep}`);
            } else if (restoredSession.fare && restoredSession.eta) {
              // Fare was already quoted - resume at summary step (awaiting fare confirmation)
              state.bookingStep = "summary";
              console.log(`[${callId}] 📋 Restored to fare confirmation stage (fare=${restoredSession.fare}, eta=${restoredSession.eta})`);
            } else if (restoredSession.status === "awaiting_confirmation") {
              state.bookingStep = "summary";
            } else if (state.booking.passengers !== null && state.booking.passengers > 0) {
              state.bookingStep = "time";
            } else if (state.booking.destination) {
              state.bookingStep = "passengers";
            } else if (state.booking.pickup) {
              state.bookingStep = "destination";
            } else {
              state.bookingStep = "pickup";
            }
            
            // Also restore lastQuestionType if available
            if (restoredSession.last_question_type) {
              state.lastQuestionType = restoredSession.last_question_type;
              console.log(`[${callId}] 📋 Restored lastQuestionType: ${state.lastQuestionType}`);
            }
            
            // ═══════════════════════════════════════════════════════════════════════════
            // CRITICAL: Restore confirmationAskedAt for silent reconnect during summary
            // This tells resume logic if user already heard "Is that correct?"
            // ═══════════════════════════════════════════════════════════════════════════
            if (restoredSession.confirmation_asked_at) {
              state.confirmationAskedAt = new Date(restoredSession.confirmation_asked_at).getTime();
              console.log(`[${callId}] 📋 Restored confirmationAskedAt: ${restoredSession.confirmation_asked_at}`);
            }
            
            console.log(`[${callId}] 📋 Restored step: ${state.bookingStep}, pickup=${state.booking.pickup}, dest=${state.booking.destination}, pax=${state.booking.passengers}, fare=${restoredSession.fare}, status=${restoredSession.status}`);
            
            // ═══════════════════════════════════════════════════════════════════════════
            // PRE-WEBHOOK RESUME: If we reconnected with pre_webhook status, send the webhook now
            // This happens after the handoff that occurred before the webhook was sent
            // ═══════════════════════════════════════════════════════════════════════════
            if (restoredSession.status === "pre_webhook") {
              console.log(`[${callId}] 📡 PRE-WEBHOOK RESUME: Sending dispatch webhook now`);
              
              // Mark that we're processing the webhook
              state.awaitingDispatchCallback = true;
              state.quoteRequestedAt = Date.now();
              
              // Fire webhook asynchronously so it doesn't block session setup
              const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
              if (DISPATCH_WEBHOOK_URL) {
                // Format phone number
                let formattedPhone = state.phone?.replace(/^\+/, '') || '';
                if (formattedPhone.startsWith('0')) {
                  formattedPhone = formattedPhone.slice(1);
                }
                
                // Get recent user transcripts
                const userTranscripts = state.transcripts
                  .filter(t => t.role === "user")
                  .slice(-6)
                  .map(t => ({ text: t.text, timestamp: t.timestamp }));
                
                const webhookPayload = {
                  action: "request_quote",
                  job_id: crypto.randomUUID(),
                  call_id: state.callId,
                  caller_phone: formattedPhone,
                  caller_name: state.customerName,
                  ada_pickup: state.booking.pickup,
                  ada_destination: state.booking.destination,
                  user_transcripts: userTranscripts,
                  passengers: state.booking.passengers,
                  pickup_time: restoredSession.pickup_time || state.booking.pickup_time || "ASAP",
                  timestamp: new Date().toISOString()
                };
                
                console.log(`[${callId}] 📤 Sending webhook to ${DISPATCH_WEBHOOK_URL}`);
                
                fetch(DISPATCH_WEBHOOK_URL, {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify(webhookPayload)
                })
                  .then(async (resp) => {
                    console.log(`[${callId}] 📬 Dispatch webhook response: ${resp.status} ${resp.statusText}`);
                    if (!resp.ok) {
                      console.error(`[${callId}] ⚠️ Webhook failed - will use fallback`);
                    }
                    // Update status to active (no longer pre_webhook)
                    await supabase.from("live_calls").update({
                      status: "active",
                      updated_at: new Date().toISOString()
                    }).eq("call_id", callId);
                  })
                  .catch((err) => {
                    console.error(`[${callId}] ❌ Webhook error:`, err);
                    // On error, generate fallback quote
                    if (state) {
                      state.awaitingDispatchCallback = false;
                      state.pendingQuote = {
                        fare: "£15.00",
                        eta: "8 minutes",
                        pickup: state.booking.pickup,
                        destination: state.booking.destination,
                        pickup_time: state.booking.pickup_time || null,
                        callback_url: null,
                        timestamp: Date.now(),
                        lastPrompt: null
                      };
                    }
                  });
              }
            }
          }
        }
        
        // Also update settings from init message if pre-connected
        if (state && preConnected) {
          state.inboundAudioFormat = (message.inbound_format as "ulaw" | "slin" | "slin16" | "pcm48") ?? state.inboundAudioFormat;
          // Derive sample rate from format if not explicitly provided
          const formatToRate: Record<string, number> = { "pcm48": 48000, "slin16": 16000, "slin": 8000, "ulaw": 8000 };
          state.inboundSampleRate = message.inbound_sample_rate || formatToRate[message.inbound_format || "ulaw"] || 8000;
          state.useRasaAudioProcessing = message.rasa_audio_processing ?? false;
          state.halfDuplex = message.half_duplex ?? false;
          state.goodbyeGraceMs = message.goodbye_grace_ms ?? 3000;
        }
        
        console.log(`[${callId}] 🔊 Inbound audio: ${state!.inboundAudioFormat} @ ${state!.inboundSampleRate}Hz`);
        console.log(`[${callId}] 🎧 Audio processing: ${state!.useRasaAudioProcessing ? 'Rasa-style (8→16kHz)' : 'Standard (8→24kHz)'}`);
        
        console.log(`[${callId}] 🌐 Phone: ${phone}, Detected: ${detectedLanguage}, Final language: ${state!.language}`);

        // === FAST CALLER + BOOKING LOOKUP (before greeting) ===
        // Query BOTH caller info AND active bookings in parallel so Ada knows the context before greeting
        // DEMO MODE: Skip entirely - force static greeting with no personalization
        if (!DEMO_SIMPLE_MODE && phone && phone !== "unknown") {
          try {
            const phoneKey = normalizePhone(phone);
            // Also try with + prefix stripped (some old records)
            const altPhone = phone.replace(/^\+/, '');
            console.log(`[${callId}] 🔍 Fast caller lookup for: raw=${phone}, normalized=${phoneKey}`);
            
            // Run both queries in parallel for speed
            const [callerResult, bookingResult] = await Promise.all([
              // Query 1: Caller info (including preferred_language)
              supabase
                .from("callers")
                .select("name, last_pickup, last_destination, total_bookings, preferred_language")
                .eq("phone_number", phoneKey)
                .maybeSingle(),
              // Query 2: Active booking
              supabase
                .from("bookings")
                .select("call_id, pickup, destination, passengers, fare, eta, status, booked_at, updated_at, caller_name")
                .or(`caller_phone.eq.${phoneKey},caller_phone.eq.${altPhone}`)
                .in("status", ["confirmed", "dispatched", "active", "pending"])
                .order("updated_at", { ascending: false })
                .limit(1)
                .maybeSingle()
            ]);
            
            // Process caller data
            if (callerResult.data) {
              state!.customerName = callerResult.data.name || null;
              state!.callerLastPickup = callerResult.data.last_pickup || null;
              state!.callerLastDestination = callerResult.data.last_destination || null;
              state!.callerTotalBookings = callerResult.data.total_bookings || 0;
              
              // Use preferred_language from DB if available (overrides phone-based detection)
              if (callerResult.data.preferred_language) {
                state!.language = callerResult.data.preferred_language;
                console.log(`[${callId}] 🌐 Using saved preferred language: ${callerResult.data.preferred_language}`);
              }
              
              console.log(`[${callId}] 👤 Fast lookup found: ${callerResult.data.name || 'no name'}, ${state!.callerTotalBookings} bookings`);
            }
            
            // Process active booking - BUT ONLY IF WE DIDN'T ALREADY RESTORE FROM SESSION
            // CRITICAL FIX: If we restored session from live_calls, DO NOT overwrite with old booking history!
            // The restoredSession already has the current booking state from the active call.
            if (bookingResult.data && !restoredSession) {
              state!.hasActiveBooking = true;
              state!.activeBookingCallId = bookingResult.data.call_id;
              state!.booking.pickup = bookingResult.data.pickup;
              state!.booking.destination = bookingResult.data.destination;
              state!.booking.passengers = bookingResult.data.passengers;
              state!.booking.version = Math.max(state!.booking.version || 0, 1);
              
              if (!state!.customerName && bookingResult.data.caller_name) {
                state!.customerName = bookingResult.data.caller_name;
              }
              
              console.log(`[${callId}] 📦 ACTIVE BOOKING FOUND (before greeting): ${bookingResult.data.pickup} → ${bookingResult.data.destination}`);
            } else if (bookingResult.data && restoredSession) {
              // We have an active booking but already restored from session - just mark flag, don't overwrite
              state!.hasActiveBooking = true;
              state!.activeBookingCallId = bookingResult.data.call_id;
              console.log(`[${callId}] 📦 Active booking exists but PRESERVED session state: ${state!.booking.pickup} → ${state!.booking.destination}`);
            }
          } catch (e) {
            console.error(`[${callId}] Fast caller/booking lookup failed:`, e);
            // Continue without - will ask for info
          }
        } else if (DEMO_SIMPLE_MODE) {
          console.log(`[${callId}] 🎭 Demo mode: skipping fast caller/booking lookup - static greeting only`);
        }

        // If pre-connected, OpenAI is already ready - just send session update + greeting
        if (preConnected && openaiConnected) {
          console.log(`[${callId}] ⚡ OpenAI already connected - triggering greeting immediately!`);
          sendSessionUpdate(state!);
        } else {
          // Connect to OpenAI IMMEDIATELY - don't wait for DB lookup
          pendingGreeting = true;
          connectToOpenAI(state!);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CRITICAL: Create live_calls record SYNCHRONOUSLY before greeting
        // This ensures session restoration works even if WebSocket times out quickly
        // ═══════════════════════════════════════════════════════════════════════════
        if (!restoredSession && phone && phone !== "unknown") {
          const phoneKey = normalizePhone(phone);
          try {
            console.log(`[${callId}] 📝 Creating live_calls record synchronously...`);
            await supabase.from("live_calls").upsert({
              call_id: callId,
              caller_phone: phoneKey,
              caller_name: state!.customerName || null,
              status: "active",
              source: "simple",
              transcripts: [],
              pickup: state!.booking.pickup || null,
              destination: state!.booking.destination || null,
              passengers: state!.booking.passengers || null,
              booking_step: state!.bookingStep || "pickup",
              updated_at: new Date().toISOString(),
            }, { onConflict: "call_id" });
            console.log(`[${callId}] ✅ live_calls record created (sync)`);
          } catch (e) {
            console.error(`[${callId}] ⚠️ Failed to create live_calls (sync):`, e);
          }
        }

        socket.send(JSON.stringify({ 
          type: "ready", 
          call_id: callId,
          mode: "simple"
        }));
        
        // Start keep-alive pings to prevent WebSocket timeout during dispatch callback wait
        startKeepAlive(callId);
        
        // Start session timeout to end call gracefully before Supabase kills the function
        startSessionTimeout(callId);

        // Subscribe to dispatch broadcast channel for ask_confirm, say, etc.
        dispatchChannel = supabase.channel(`dispatch_${callId}`);
        
        dispatchChannel.on("broadcast", { event: "dispatch_ask_confirm" }, async (payload: any) => {
          const { message, fare, eta, eta_minutes, callback_url } = payload.payload || {};

          // Build a deterministic prompt so Ada repeats the *exact* fare/ETA (no hallucinated numbers)
          const fareNum = Number(String(fare ?? "").replace(/[^0-9.]/g, ""));
          const fareText = Number.isFinite(fareNum) && fareNum > 0
            ? `£${fareNum.toFixed(2)}`
            : (fare ? String(fare) : "");

          const etaRaw = eta ?? eta_minutes;
          const etaNum = Number(String(etaRaw ?? "").replace(/[^0-9]/g, ""));
          const etaText = etaRaw
            ? String(etaRaw)
            : (Number.isFinite(etaNum) && etaNum > 0 ? `${etaNum} minutes` : "");

          // Include booking recap so the caller hears the route before deciding on price/ETA
          // TTS-FRIENDLY ADDRESS FORMAT: Add hyphen before letter suffix so "52A" is spoken as "52-A"
          const formatForTTS = (addr: string): string => {
            if (!addr) return "";
            // Match house numbers with letter suffixes like "52A", "1214B" and format as "52-A", "1214-B"
            return addr.replace(/^(\d+)([A-Za-z])\b/, '$1-$2');
          };
          
          const recapPickup = formatForTTS(state?.booking?.pickup || "");
          const recapDest = formatForTTS(state?.booking?.destination || "");
          const recapPassengers = state?.booking?.passengers || 1;

          const recapText = (recapPickup && recapDest)
            ? `Just to confirm, you're going from ${recapPickup} to ${recapDest} for ${recapPassengers} passenger${recapPassengers === 1 ? "" : "s"}. `
            : "";

          const spokenMessage = (fareText && etaText)
            ? `${recapText}The price is ${fareText} and your driver will be ${etaText}. Shall I book that?`
            : (message ? String(message) : "");

          console.log(`[${callId}] 📥 DISPATCH ask_confirm received: "${message}" → spoken="${spokenMessage}"`);

          if (!spokenMessage || !openaiWs || !openaiConnected || isConnectionClosed) {
            console.log(`[${callId}] ⚠️ Cannot process ask_confirm - no message, OpenAI not connected, or connection closed`);
            return;
          }

          // GUARD 0: If call is ending/ended, ignore ALL dispatch messages
          if (state?.callEnded) {
            console.log(`[${callId}] ⚠️ Ignoring ask_confirm - call is ended/ending`);
            return;
          }

          // GUARD 0.5: Ignore stale ask_confirm messages unless we JUST requested a quote
          // This blocks "wrong fare" caused by late/duplicated dispatch events.
          const quoteMaxAgeMs = 45_000;
          if (!state?.quoteRequestedAt || Date.now() - state.quoteRequestedAt > quoteMaxAgeMs) {
            console.log(
              `[${callId}] ⚠️ Ignoring ask_confirm - stale (quoteRequestedAt=${state?.quoteRequestedAt}, age=${state?.quoteRequestedAt ? Date.now() - state.quoteRequestedAt : 'n/a'}ms)`
            );
            return;
          }

          // Use the same normalization as book_taxi to avoid mismatches like "52a david|||wolverhampton" vs "52adavidroad|||wolverhampton"
          const normalizeForTripKey = (addr: string): string => {
            if (!addr) return "";
            return addr
              .toLowerCase()
              .replace(/[,.\-]/g, " ")
              .replace(/\s+/g, " ")
              .replace(/\b(street|st|road|rd|avenue|ave|lane|ln|drive|dr|court|ct|place|pl)\b/gi, "")
              .replace(/\b(netherlands|uk|united kingdom|england|nederland|the netherlands)\b/gi, "")
              .replace(/\b(in|naar|van|to|from)\b/gi, "")
              .trim();
          };
          
          const makeTripKeyLocal = (p: string | null, d: string | null) =>
            `${normalizeForTripKey(p || "")}|||${normalizeForTripKey(d || "")}`;

          const tripKeyNow = makeTripKeyLocal(state?.booking?.pickup || null, state?.booking?.destination || null);
          if (state?.quoteTripKey && tripKeyNow !== state.quoteTripKey) {
            console.log(
              `[${callId}] ⚠️ Ignoring ask_confirm - trip changed (quoteTripKey=${state.quoteTripKey}, now=${tripKeyNow})`
            );
            return;
          }

          // GUARD 1: If booking was just confirmed this turn, IGNORE any new ask_confirm
          // This prevents C# from retriggering the fare prompt after customer said YES
          if (state?.bookingConfirmedThisTurn) {
            console.log(`[${callId}] ⚠️ Ignoring ask_confirm - booking was already confirmed this turn`);
            return;
          }

          // GUARD 2: If booking was confirmed recently (within 60s), ignore ONLY if it's the same trip.
          // If the trip changed, we must allow the new fare prompt.
          if (state?.lastBookTaxiSuccessAt && Date.now() - state.lastBookTaxiSuccessAt < 60000) {
            const tripKeyNow = makeTripKeyLocal(state?.booking?.pickup || null, state?.booking?.destination || null);
            const isSameTripAsLastConfirm = !!state?.lastConfirmedTripKey && tripKeyNow === state.lastConfirmedTripKey;

            if (isSameTripAsLastConfirm) {
              console.log(`[${callId}] ⚠️ Ignoring ask_confirm - booking was confirmed ${Date.now() - state.lastBookTaxiSuccessAt}ms ago`);
              return;
            }

            console.log(`[${callId}] ✅ Allowing ask_confirm within 60s because trip changed (last=${state?.lastConfirmedTripKey}, now=${tripKeyNow})`);
          }

          // GUARD 3: If there's already a pending quote OR a recent prompt was injected, ignore true duplicates
          // IMPORTANT: If fare/eta changed, treat it as an update (do NOT ignore), otherwise booking enforcement
          // will see a mismatch and cancel Ada mid-sentence.
          // CRITICAL: If pendingQuote was set by polling loop but Ada hasn't spoken yet (no lastQuotePromptAt),
          // we MUST allow the broadcast to trigger the speech!
          if (state?.pendingQuote) {
            const timeSinceLastAsk = Date.now() - (state.pendingQuote.timestamp || 0);
            const adaHasSpoken = state.lastQuotePromptAt && (Date.now() - state.lastQuotePromptAt < 10000);

            const normNum = (v: unknown) => {
              const n = Number(String(v ?? "").replace(/[^0-9.]/g, ""));
              return Number.isFinite(n) ? n : NaN;
            };

            const existingFareNum = normNum(state.pendingQuote.fare);
            const incomingFareNum = normNum(fare);
            const existingEtaNum = Number(String(state.pendingQuote.eta ?? "").replace(/[^0-9]/g, ""));
            const incomingEtaNum = Number(String((eta ?? eta_minutes) ?? "").replace(/[^0-9]/g, ""));

            const fareSame = Number.isFinite(existingFareNum) && Number.isFinite(incomingFareNum)
              ? Math.abs(existingFareNum - incomingFareNum) <= 0.01
              : String(state.pendingQuote.fare ?? "") === String(fare ?? "");

            const etaSame = Number.isFinite(existingEtaNum) && Number.isFinite(incomingEtaNum)
              ? existingEtaNum === incomingEtaNum
              : String(state.pendingQuote.eta ?? "") === String(eta ?? eta_minutes ?? "");

            const isTrueDuplicate = fareSame && etaSame;

            // Only block if Ada has ALREADY spoken the fare (lastQuotePromptAt is set)
            // If pendingQuote was set by polling loop but no speech yet, allow the broadcast
            if (timeSinceLastAsk < 10000 && isTrueDuplicate && adaHasSpoken) {
              console.log(`[${callId}] ⚠️ Ignoring duplicate ask_confirm - same fare/eta and Ada already spoke (${timeSinceLastAsk}ms ago)`);
              // ✅ Track duplicate fare prompts in STT metrics
              if (state) state.sttMetrics.duplicateFarePrompts++;
              return;
            }

            if (isTrueDuplicate && !adaHasSpoken) {
              console.log(`[${callId}] ✅ Allowing ask_confirm - pendingQuote exists but Ada hasn't spoken yet`);
            }

            if (!isTrueDuplicate) {
              console.log(`[${callId}] ✅ ask_confirm update detected (fare/eta changed) - will re-prompt despite ${timeSinceLastAsk}ms window`);
            }
          }

          // GUARD 4: If tool handler already injected a fare prompt very recently (within 3s), skip ONLY if identical
          if (state?.lastQuotePromptAt && Date.now() - state.lastQuotePromptAt < 3000) {
            const isSamePrompt = (state.lastQuotePromptText || "") === (spokenMessage || "");
            if (isSamePrompt) {
              console.log(`[${callId}] ⚠️ Ignoring ask_confirm - identical fare prompt already injected ${Date.now() - state.lastQuotePromptAt}ms ago`);
              // ✅ Track duplicate fare prompts in STT metrics
              if (state) state.sttMetrics.duplicateFarePrompts++;
              return;
            }
            console.log(`[${callId}] ✅ Allowing ask_confirm within 3s because prompt changed`);
          }

          // Store pending quote state
          if (state) {
            state.pendingQuote = {
              fare: fare || null,
              eta: eta || null,
              pickup: state.booking.pickup,
              destination: state.booking.destination,
              pickup_time: state.booking.pickup_time || null, // Preserve normalized pickup time
              callback_url: callback_url || null,
              timestamp: Date.now(),
              lastPrompt: spokenMessage || null
            };

            // Mark that we are actively prompting the fare question (prevents tool handler duplicating it)
            state.lastQuotePromptAt = Date.now();
            state.lastQuotePromptText = spokenMessage;
            
            // ✅ CRITICAL: Persist fare/eta to live_calls so session restoration works
            // This ensures that if the connection drops after fare is quoted, we can resume from fare confirmation
            supabase
              .from("live_calls")
              .update({
                fare: fare || null,
                eta: eta || eta_minutes || null,
                status: "awaiting_confirmation",
                updated_at: new Date().toISOString()
              })
              .eq("call_id", callId)
              .then(({ error }) => {
                if (error) console.error(`[${callId}] Failed to persist fare/eta:`, error);
                else console.log(`[${callId}] 💾 Fare/ETA persisted to DB: fare=${fare}, eta=${eta || eta_minutes}`);
              });
            
            // ✅ CRITICAL: Clear pendingModification when fare arrives - we've moved past the modification stage
            // The modification has already been applied, now we're awaiting fare confirmation
            if (state.pendingModification) {
              console.log(`[${callId}] 🧹 Clearing pendingModification - fare quote received, modification flow complete`);
              state.pendingModification = null;
            }
          }

          // Determine language instruction based on session
          const langCode = state?.language || "en";
          const langName = langCode === "nl" ? "Dutch" : langCode === "de" ? "German" : langCode === "fr" ? "French" : langCode === "es" ? "Spanish" : langCode === "it" ? "Italian" : langCode === "pl" ? "Polish" : "English";

          // HARD BLOCK: even if book_taxi succeeded earlier, do NOT allow Ada to confirm a booking
          // during fare confirmation. She must wait for the customer's yes/no.
          // Also force audio buffering for this injected prompt so we never play partial fare sentences.
          if (state) {
            state.bookingConfirmedThisTurn = false;
            state.audioVerified = false;
            state.pendingAudioBuffer = [];
            // ✅ CRITICAL: Clear awaitingDispatchCallback so Ada can speak the fare
            // Without this, the guard in response.created will cancel the fare speech
            state.awaitingDispatchCallback = false;
            console.log(`[${callId}] ✅ Cleared awaitingDispatchCallback - fare received, Ada can speak`);
            // ✅ SUMMARY PROTECTION: Block barge-in during fare quote speech
            state.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
            console.log(`[${callId}] 🛡️ Summary protection activated for ${SUMMARY_PROTECTION_MS}ms (fare quote)`);
          }

          // Wait longer for any in-flight response to complete before cancelling
          await new Promise(resolve => setTimeout(resolve, 400));

          // Cancel any active response first to avoid "conversation_already_has_active_response" error
          openaiWs.send(JSON.stringify({ type: "response.cancel" }));

          // Also clear input audio buffer to prevent VAD from triggering new responses
          openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

          // Wait longer for cancel to take effect
          await new Promise(resolve => setTimeout(resolve, 300));

          // Inject the fare question into Ada's conversation with explicit YES/NO handling
          const farePromptWithInstructions = `[DISPATCH FARE CONFIRMATION]: You MUST repeat the following sentence EXACTLY, including all numbers and currency, IN ${langName.toUpperCase()}:
"${spokenMessage}"

After saying it, WAIT SILENTLY for their response. Do NOT confirm the booking yet.

WHEN CUSTOMER RESPONDS:
- If they say YES / correct / confirm / go ahead / book it → CALL book_taxi with confirmation_state: "confirmed", pickup: "${state?.booking?.pickup || ''}", destination: "${state?.booking?.destination || ''}"
- If they say NO / cancel / too expensive → CALL book_taxi with confirmation_state: "rejected"
- If unclear, ask: "Would you like me to book that?"

DO NOT say "booked" or "confirmed" until book_taxi with confirmation_state: "confirmed" returns success.`;

          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{
                type: "input_text",
                text: farePromptWithInstructions
              }]
            }
          }));

          // Cancel any active response before creating new one (race condition fix)
          if (state?.openAiResponseActive) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            if (state) state.openAiResponseActive = false;
          }
          // Trigger Ada to speak
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: {
              modalities: ["audio", "text"],
              instructions: `Repeat EXACTLY: "${spokenMessage}" Then STOP and wait silently for yes/no. Do not change any numbers.`
            }
          }));
        });
        
        dispatchChannel.on("broadcast", { event: "dispatch_say" }, async (payload: any) => {
          const { message: sayMessage } = payload.payload || {};
          console.log(`[${callId}] 📥 DISPATCH say received: "${sayMessage}"`);
          
          if (!sayMessage || !openaiWs || !openaiConnected || isConnectionClosed) {
            console.log(`[${callId}] ⚠️ Cannot process dispatch say - no message, OpenAI not connected, or connection closed`);
            return;
          }
          
          // Cancel any active response before injecting dispatch say message
          if (state?.openAiResponseActive) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
          }
          openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          
          // Make Ada say the message
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
              instructions: `The dispatch system has an update. Say this naturally to the customer: "${sayMessage}"`
            }
          }));
        });
        
        // === DIRECT TTS HANDLER FOR ASK_CONFIRM - Bypasses AI completely ===
        // When bypass_ai: true is sent with ask_confirm, synthesize speech directly without OpenAI interpretation
        // The message is spoken EXACTLY as provided, then we wait for yes/no response
        dispatchChannel.on("broadcast", { event: "dispatch_ask_confirm_direct" }, async (payload: any) => {
          const { message: confirmMessage, fare, eta, eta_minutes, booking_ref, callback_url } = payload.payload || {};
          console.log(`[${callId}] 📥 DISPATCH ask_confirm_direct (BYPASS AI): "${confirmMessage}"`);
          
          if (!confirmMessage || isConnectionClosed) {
            console.log(`[${callId}] ⚠️ Cannot process direct ask_confirm - no message or connection closed`);
            return;
          }

          // GUARD: If call is ending/ended, ignore
          if (state?.callEnded) {
            console.log(`[${callId}] ⚠️ Ignoring ask_confirm_direct - call is ended/ending`);
            return;
          }
          
          // Cancel any active AI response to prevent overlap
          if (state?.openAiResponseActive && openaiWs && openaiConnected) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            state.discardCurrentResponseAudio = true;
          }
          
          // Store pending quote state for yes/no handling
          if (state) {
            state.pendingQuote = {
              fare: fare || null,
              eta: eta || eta_minutes || null,
              pickup: state.booking.pickup,
              destination: state.booking.destination,
              pickup_time: state.booking.pickup_time || null,
              callback_url: callback_url || null,
              timestamp: Date.now(),
              lastPrompt: confirmMessage || null
            };
            state.lastQuotePromptAt = Date.now();
            state.lastQuotePromptText = confirmMessage;
            
            // Persist fare/eta to DB for session restoration
            supabase
              .from("live_calls")
              .update({
                fare: fare || null,
                eta: eta || eta_minutes || null,
                status: "awaiting_confirmation",
                updated_at: new Date().toISOString()
              })
              .eq("call_id", callId)
              .then(({ error }) => {
                if (error) console.error(`[${callId}] Failed to persist fare/eta:`, error);
                else console.log(`[${callId}] 💾 Fare/ETA persisted (direct): fare=${fare}, eta=${eta || eta_minutes}`);
              });
            
            // Clear any pending modification
            if (state.pendingModification) {
              console.log(`[${callId}] 🧹 Clearing pendingModification - fare quote received (direct)`);
              state.pendingModification = null;
            }
            
            // Summary protection during fare quote
            state.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
            state.bookingConfirmedThisTurn = false;
            state.audioVerified = false;
            state.pendingAudioBuffer = [];
          }
          
          // Mark Ada as speaking (echo guard)
          if (state) {
            state.isAdaSpeaking = true;
            state.responseStartTime = Date.now();
            state.bargeInIgnoreUntil = Date.now() + 500;
          }
          
          try {
            // Use OpenAI TTS API directly for PCM16 audio (matches bridge format)
            const ttsResponse = await fetch("https://api.openai.com/v1/audio/speech", {
              method: "POST",
              headers: {
                "Authorization": `Bearer ${OPENAI_API_KEY}`,
                "Content-Type": "application/json"
              },
              body: JSON.stringify({
                model: "tts-1", // Fast model for realtime
                voice: state?.voice || "shimmer",
                input: confirmMessage,
                response_format: "pcm" // 24kHz 16-bit PCM - matches bridge format
              })
            });
            
            if (!ttsResponse.ok) {
              console.error(`[${callId}] ❌ Direct TTS failed for ask_confirm: ${ttsResponse.status}`);
              // Fallback to AI-based ask_confirm
              if (openaiWs && openaiConnected) {
                openaiWs.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{ type: "input_text", text: `[DISPATCH]: Say exactly: "${confirmMessage}" then wait for yes/no.` }]
                  }
                }));
                openaiWs.send(JSON.stringify({ type: "response.create" }));
              }
              return;
            }
            
            const audioBuffer = await ttsResponse.arrayBuffer();
            const audioData = new Uint8Array(audioBuffer);
            
            console.log(`[${callId}] 🔊 Direct TTS (ask_confirm) generated: ${audioData.length} bytes`);
            
            // Stream audio chunks to client (4800 bytes ≈ 100ms at 24kHz PCM16)
            const chunkSize = 4800;
            for (let i = 0; i < audioData.length; i += chunkSize) {
              if (isConnectionClosed) break;
              
              const chunk = audioData.slice(i, Math.min(i + chunkSize, audioData.length));
              // Convert to base64 for WebSocket JSON transport
              const base64Chunk = btoa(String.fromCharCode(...chunk));
              
              socket.send(JSON.stringify({
                type: "audio",
                audio: base64Chunk
              }));
            }
            
            // Signal audio complete
            socket.send(JSON.stringify({ type: "audio.done" }));
            
            console.log(`[${callId}] ✅ Direct TTS (ask_confirm) playback complete - waiting for yes/no`);
            
            // Add to transcripts for logging
            if (state) {
              state.transcripts.push({
                role: "assistant",
                text: `[DIRECT TTS] ${confirmMessage}`,
                timestamp: new Date().toISOString()
              });
              scheduleTranscriptFlush(state);
            }
            
            // Inject context into OpenAI so it knows we're waiting for yes/no
            // This allows the AI to properly handle the customer's response
            if (openaiWs && openaiConnected) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "assistant",
                  content: [{ type: "input_text", text: confirmMessage }]
                }
              }));
              
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{ 
                    type: "input_text", 
                    text: `[SYSTEM - DO NOT SPEAK]: The fare quote above was just spoken via direct TTS. 
Now WAIT SILENTLY for the customer's yes/no response. DO NOT speak until they respond.

WHEN CUSTOMER RESPONDS:
- If they say YES / correct / confirm / go ahead / book it → CALL book_taxi with confirmation_state: "confirmed", pickup: "${state?.booking?.pickup || ''}", destination: "${state?.booking?.destination || ''}"
- If they say NO / cancel / too expensive → CALL book_taxi with confirmation_state: "rejected"
- If unclear, ask: "Would you like me to book that?"

DO NOT say "booked" or "confirmed" until book_taxi with confirmation_state: "confirmed" returns success.`
                  }]
                }
              }));
            }
            
          } catch (ttsError) {
            console.error(`[${callId}] ❌ Direct TTS error (ask_confirm):`, ttsError);
          } finally {
            // Reset speaking state
            if (state) {
              state.isAdaSpeaking = false;
              state.echoGuardUntil = Date.now() + 400;
              
              // CRITICAL: Enable confirmation mode - bypass echo guard for "yes" responses
              // User's "Yes please" must be captured even if spoken immediately after fare quote
              state.confirmationResponsePending = true;
              console.log(`[${callId}] 🎯 Awaiting confirmation - echo guard bypass enabled for quick 'yes' responses`);
              
              // === PERIODIC COMMIT FOR POST-TTS CONFIRMATION ===
              // Since we used direct TTS and OpenAI is passively listening, VAD may miss short "yes" responses.
              // Schedule periodic forced commits to ensure transcription happens.
              console.log(`[${callId}] ⏰ POST-TTS: Scheduling periodic commits to catch confirmation response`);
              
              const SILENCE_SAMPLE_RATE = 24000;
              const SILENCE_PADDING_MS = 250;
              const SILENCE_SAMPLES = Math.floor(SILENCE_SAMPLE_RATE * SILENCE_PADDING_MS / 1000);
              const silenceBuffer = new Int16Array(SILENCE_SAMPLES);
              const silenceBytes = new Uint8Array(silenceBuffer.buffer);
              let silenceBinary = "";
              for (let i = 0; i < silenceBytes.length; i++) {
                silenceBinary += String.fromCharCode(silenceBytes[i]);
              }
              const silenceBase64 = btoa(silenceBinary);
              
              // Schedule commits at 2s, 4s, 6s to catch "yes please" after fare quote
              const commitDelays = [2000, 4000, 6000];
              const capturedState = state; // Capture state for closure
              commitDelays.forEach((delay) => {
                setTimeout(() => {
                  if (openaiWs && openaiConnected && !capturedState.callEnded && !isConnectionClosed) {
                    try {
                      console.log(`[${callId}] 📤 POST-TTS COMMIT: Forcing transcription check (${delay}ms after TTS)`);
                      openaiWs.send(JSON.stringify({
                        type: "input_audio_buffer.append",
                        audio: silenceBase64
                      }));
                      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
                    } catch (e) {
                      // Connection may have closed
                    }
                  }
                }, delay);
              });
            }
          }
        });
        
        // === DIRECT TTS HANDLER - Bypasses AI completely ===
        // When bypass_ai: true is sent, synthesize speech directly without OpenAI interpretation
        dispatchChannel.on("broadcast", { event: "dispatch_say_direct" }, async (payload: any) => {
          const { message: sayMessage } = payload.payload || {};
          console.log(`[${callId}] 📥 DISPATCH say_direct (BYPASS AI): "${sayMessage}"`);
          
          if (!sayMessage || isConnectionClosed) {
            console.log(`[${callId}] ⚠️ Cannot process direct say - no message or connection closed`);
            return;
          }
          
          // Cancel any active AI response to prevent overlap
          if (state?.openAiResponseActive && openaiWs && openaiConnected) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            state.discardCurrentResponseAudio = true;
          }
          
          // Mark Ada as speaking (echo guard) even though this is direct TTS
          if (state) {
            state.isAdaSpeaking = true;
            state.responseStartTime = Date.now();
            state.bargeInIgnoreUntil = Date.now() + 500;
          }
          
          try {
            // Use OpenAI TTS API directly for PCM16 audio (matches bridge format)
            const ttsResponse = await fetch("https://api.openai.com/v1/audio/speech", {
              method: "POST",
              headers: {
                "Authorization": `Bearer ${OPENAI_API_KEY}`,
                "Content-Type": "application/json"
              },
              body: JSON.stringify({
                model: "tts-1", // Fast model for realtime
                voice: state?.voice || "shimmer",
                input: sayMessage,
                response_format: "pcm" // 24kHz 16-bit PCM - matches bridge format
              })
            });
            
            if (!ttsResponse.ok) {
              console.error(`[${callId}] ❌ Direct TTS failed: ${ttsResponse.status}`);
              // Fallback to AI-based say
              if (openaiWs && openaiConnected) {
                openaiWs.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{ type: "input_text", text: `[DISPATCH]: Say exactly: "${sayMessage}"` }]
                  }
                }));
                openaiWs.send(JSON.stringify({ type: "response.create" }));
              }
              return;
            }
            
            const audioBuffer = await ttsResponse.arrayBuffer();
            const audioData = new Uint8Array(audioBuffer);
            
            console.log(`[${callId}] 🔊 Direct TTS generated: ${audioData.length} bytes`);
            
            // Stream audio chunks to client (4800 bytes ≈ 100ms at 24kHz PCM16)
            const chunkSize = 4800;
            for (let i = 0; i < audioData.length; i += chunkSize) {
              if (isConnectionClosed) break;
              
              const chunk = audioData.slice(i, Math.min(i + chunkSize, audioData.length));
              // Convert to base64 for WebSocket JSON transport
              const base64Chunk = btoa(String.fromCharCode(...chunk));
              
              socket.send(JSON.stringify({
                type: "audio",
                audio: base64Chunk
              }));
            }
            
            // Signal audio complete
            socket.send(JSON.stringify({ type: "audio.done" }));
            
            console.log(`[${callId}] ✅ Direct TTS playback complete`);
            
            // Add to transcripts for logging (text includes source marker)
            if (state) {
              state.transcripts.push({
                role: "assistant",
                text: `[DIRECT TTS] ${sayMessage}`,
                timestamp: new Date().toISOString()
              });
            }
            
          } catch (ttsError) {
            console.error(`[${callId}] ❌ Direct TTS error:`, ttsError);
          } finally {
            // Reset speaking state
            if (state) {
              state.isAdaSpeaking = false;
              state.echoGuardUntil = Date.now() + 400;
            }
          }
        });
        
        dispatchChannel.on("broadcast", { event: "dispatch_hangup" }, async () => {
          console.log(`[${callId}] 📥 DISPATCH hangup received`);
          if (isConnectionClosed) return;
          if (state) state.callEnded = true;
          socket.send(JSON.stringify({ type: "hangup", reason: "dispatch_requested" }));
        });
        
        // Handle dispatch confirmation after user accepted fare
        dispatchChannel.on("broadcast", { event: "dispatch_confirm" }, async (payload: any) => {
          const { confirmation_message, fare, eta, eta_minutes, booking_ref, driver_name, vehicle_reg, status } = payload.payload || {};
          console.log(`[${callId}] 📥 DISPATCH confirm received: "${confirmation_message}"`);
          
          if (!openaiWs || !openaiConnected || isConnectionClosed) {
            console.log(`[${callId}] ⚠️ Cannot process confirm - OpenAI not connected or connection closed`);
            return;
          }
          
          // Set booking confirmed state
          if (state) {
            state.bookingConfirmedThisTurn = true;
            state.pendingQuote = null; // Clear quote
            // ✅ CRITICAL: Set askedAnythingElse so goodbye enforcement waits for user response
            // BUT don't set askedAnythingElseAt yet - that should happen AFTER Ada finishes speaking
            state.askedAnythingElse = true;
            // Mark that we're about to send the confirmation response (bypass the guard)
            state.confirmationResponsePending = true;
            
            // ✅ Mark confirmation as spoken to prevent failsafe from firing
            state.confirmationSpoken = true;
            if (state.confirmationFailsafeTimerId) {
              clearTimeout(state.confirmationFailsafeTimerId);
              state.confirmationFailsafeTimerId = null;
              console.log(`[${callId}] 🔄 dispatch_confirm received - cancelled failsafe timer`);
            }
          }
          
          // Build the confirmation message for Ada to speak
          let messageToSpeak = confirmation_message || "Your booking is confirmed!";
          
          // Add fare and ETA if provided
          if (fare || eta || eta_minutes) {
            const fareText = fare ? `Fare is £${fare}` : "";
            const etaText = eta_minutes ? `arriving in about ${eta_minutes} minutes` : (eta ? `arriving in about ${eta}` : "");
            const driverText = driver_name ? `Your driver is ${driver_name}` : "";
            const vehicleText = vehicle_reg ? `in a ${vehicle_reg}` : "";
            const details = [fareText, etaText, driverText, vehicleText].filter(Boolean).join(", ");
            if (details && !confirmation_message?.includes(fare)) {
              messageToSpeak += `. ${details}`;
            }
          }
          
          // Determine language instruction based on session
          const langCode = state?.language || "en";
          const langName = langCode === "nl" ? "Dutch" : langCode === "de" ? "German" : langCode === "fr" ? "French" : langCode === "es" ? "Spanish" : langCode === "it" ? "Italian" : langCode === "pl" ? "Polish" : "English";
          
          // ✅ SET discardCurrentResponseAudio BEFORE cancelling to drop any late audio deltas (prevents "for you." fragments)
          if (state) {
            state.discardCurrentResponseAudio = true;
          }
          
          // Cancel any active response before injecting confirmation
          if (state?.openAiResponseActive) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
          }
          openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          
          // Inject the confirmation for Ada to speak
          // Script: "That's on the way" + "Is there anything else I can help you with?"
          const confirmationScript = `That's on the way. Is there anything else I can help you with?`;
          
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: `[DISPATCH CONFIRMATION]: The booking has been confirmed. Say this EXACTLY to the customer IN ${langName.toUpperCase()}: "${confirmationScript}" Be natural and brief. Do not add any extra details about fare, ETA, or booking reference.` }]
            }
          }));
          
          // Cancel any active response before creating new one (race condition fix)
          if (state?.openAiResponseActive) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            if (state) state.openAiResponseActive = false;
          }
          // Trigger Ada to speak
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: {
              modalities: ["audio", "text"],
              instructions: `IMPORTANT: The dispatch has confirmed the booking. Speak in ${langName}. Say EXACTLY: "${confirmationScript}" Do not add extra details.`
            }
          }));
        });
        
        dispatchChannel.subscribe((status) => {
          console.log(`[${callId}] 📡 Dispatch channel status: ${status}`);
        });

        // Fire-and-forget: Lookup caller history, GPS, and update live_calls in background
        // This runs in parallel while Ada starts greeting
        (async () => {
          if (isConnectionClosed) return; // Guard against operations after disconnect
          try {
            // If caller has an active booking, load the latest booking details ASAP (non-blocking)
            // This prevents the model from guessing / using caller history as the booking.
            // NOTE: Use a loose type here to avoid Deno edge type inference issues.
            let activeBooking: any = null;

            // DEMO MODE: Skip ALL active booking lookups to force static demo greeting
            if (!DEMO_SIMPLE_MODE && state?.hasActiveBooking && phone && phone !== "unknown") {
              const { data: bookingData, error: bookingError } = await supabase
                .from("bookings")
                .select("pickup, destination, passengers, fare, eta, status, booked_at, updated_at")
                .eq("caller_phone", phone)
                .in("status", ["confirmed", "dispatched", "active"])
                .order("updated_at", { ascending: false })
                .order("booked_at", { ascending: false })
                .limit(1)
                .maybeSingle();

              if (bookingError) {
                console.error(`[${callId}] Active booking lookup failed:`, bookingError);
              } else if (bookingData && state) {
                activeBooking = bookingData;

                // CRITICAL: Only overwrite booking state if we did NOT restore from session
                // The skipGreeting flag indicates we restored from live_calls - preserve that state!
                if (!skipGreeting) {
                  // Keep a copy in session state for context
                  state.booking.pickup = bookingData.pickup;
                  state.booking.destination = bookingData.destination;
                  state.booking.passengers = bookingData.passengers;
                  // Mark as modifiable (this came from an existing booking)
                  state.booking.version = Math.max(state.booking.version || 0, 1);
                  console.log(
                    `[${callId}] ✅ Active booking loaded: ${bookingData.pickup} -> ${bookingData.destination} (${bookingData.passengers})`
                  );
                } else {
                  console.log(
                    `[${callId}] 🛡️ PRESERVED session state (skipGreeting=true): ${state.booking.pickup} -> ${state.booking.destination}`
                  );
                }

                // Inject a short, explicit context note for Ada
                // (Do NOT create a response here; let normal turn-taking handle it.)
                if (openaiWs && openaiConnected) {
                  openaiWs.send(
                    JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [
                          {
                            type: "input_text",
                            text:
                              `[SYSTEM: Active booking details loaded. Pickup: "${bookingData.pickup}". Destination: "${bookingData.destination}". Passengers: ${bookingData.passengers}. If the caller says a different pickup/destination, treat it as a requested change and ask for confirmation ("Change pickup to ...?") before calling modify_booking. Do not insist the old address is correct.]`,
                          },
                        ],
                      },
                    })
                  );
                }
              }
            } else if (DEMO_SIMPLE_MODE) {
              console.log(`[${callId}] 🎭 Demo mode: skipping early active booking lookup`);
            }


            if (phone && phone !== "unknown") {
              const phoneKey = normalizePhone(phone);
              if (!DEMO_SIMPLE_MODE) {
                const altPhone = String(phone || "").replace(/^\+/, "");

                const { data: callerData } = await supabase
                  .from("callers")
                  .select("name, last_pickup, last_destination, total_bookings")
                  .eq("phone_number", phoneKey)
                  .maybeSingle();

                if (callerData && state) {
                  state.callerLastPickup = callerData.last_pickup || null;
                  state.callerLastDestination = callerData.last_destination || null;
                  state.callerTotalBookings = callerData.total_bookings || 0;
                  if (!state.customerName && callerData.name) {
                    state.customerName = callerData.name;
                  }
                  console.log(`[${callId}] 👤 Loaded caller: ${callerData.name || "no name"}, ${state.callerTotalBookings} bookings`);
                }

                // Check for active bookings if not already loaded
                // Only consider bookings from the last 2 hours as "active"
                if (!activeBooking) {
                  const twoHoursAgo = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();
                  // Try both phone formats (normalized digits-only + legacy no-plus)
                  const { data: bookingData } = await supabase
                    .from("bookings")
                    .select("call_id, pickup, destination, passengers, fare, eta, status, booked_at, updated_at, caller_name")
                    .or(`caller_phone.eq.${phoneKey},caller_phone.eq.${altPhone}`)
                    .in("status", ["confirmed", "dispatched", "active", "pending"])
                    .gte("updated_at", twoHoursAgo)
                    .order("updated_at", { ascending: false })
                    .order("booked_at", { ascending: false })
                    .limit(1)
                    .maybeSingle();

                  if (bookingData && state) {
                    activeBooking = bookingData;
                    state.hasActiveBooking = true;
                    state.activeBookingCallId = bookingData.call_id; // Store original booking's call_id for updates
                    
                    // CRITICAL: Only overwrite booking state if we did NOT restore from session
                    if (!skipGreeting) {
                      state.booking.pickup = bookingData.pickup;
                      state.booking.destination = bookingData.destination;
                      state.booking.passengers = bookingData.passengers;
                      // Mark as modifiable (this came from an existing booking)
                      state.booking.version = Math.max(state.booking.version || 0, 1);

                      // Update name from booking if not set
                      if (!state.customerName && bookingData.caller_name) {
                        state.customerName = bookingData.caller_name;
                      }

                      console.log(`[${callId}] 📦 Found active booking (${bookingData.call_id}): ${bookingData.pickup} → ${bookingData.destination}`);
                    } else {
                      // Update name from booking if not set (safe to do even when restoring)
                      if (!state.customerName && bookingData.caller_name) {
                        state.customerName = bookingData.caller_name;
                      }
                      console.log(`[${callId}] 🛡️ PRESERVED session (skipGreeting=true), booking exists but not overwriting: ${state.booking.pickup} → ${state.booking.destination}`);
                    }

                    // Inject booking context for Ada
                    if (openaiWs && openaiConnected) {
                      const customerGreeting = state.customerName
                        ? `The caller is ${state.customerName}.`
                        : "";

                      // Cancel-Clear-Inject protocol to avoid response collisions
                      if (state.openAiResponseActive) {
                        openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                        await new Promise(resolve => setTimeout(resolve, 350));
                      }

                      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                      await new Promise(resolve => setTimeout(resolve, 350));

                      openaiWs.send(JSON.stringify({
                        type: "conversation.item.create",
                        item: {
                          type: "message",
                          role: "user",
                          content: [{
                            type: "input_text",
                            text: `[SYSTEM: ${customerGreeting} This caller has an ACTIVE BOOKING. Details: Pickup: "${bookingData.pickup}", Destination: "${bookingData.destination}", Passengers: ${bookingData.passengers}. Greet them by name if known, mention they have an existing booking, and ask ONLY if they want to keep it, change it, or cancel it. Do NOT suggest a new pickup/destination yourself. If they choose change, ask what they want to change and wait for their answer.]`
                          }]
                        }
                      }));

                      // Trigger Ada to respond with this context
                      openaiWs.send(JSON.stringify({ type: "response.create" }));
                    }
                  }
                }
              } else {
                console.log(`[${callId}] 🎭 Demo mode: skipping caller + active booking lookup`);
                // Ensure we don't accidentally treat the caller as returning / active booking
                if (state) {
                  state.customerName = null;
                  state.hasActiveBooking = false;
                  state.activeBookingCallId = null;
                }
                activeBooking = null;
              }
            }

            // Create/update live call record (non-blocking)
            // ✅ REUSE existing active call card for same phone to avoid creating duplicate cards
            const phoneKey = normalizePhone(phone);
            const { data: existingCall } = await supabase
              .from("live_calls")
              .select("id, call_id")
              .eq("caller_phone", phoneKey)
              .eq("status", "active")
              .order("started_at", { ascending: false })
              .limit(1)
              .maybeSingle();

            if (existingCall) {
              // Update existing active call card with new call_id and reset data
              console.log(`[${callId}] ♻️ Reusing existing live_call card for ${phoneKey} (old call_id: ${existingCall.call_id})`);
              await supabase
                .from("live_calls")
                .update({
                  call_id: callId,
                  caller_name: state?.customerName,
                  status: "active",
                  source: "simple",
                  transcripts: [],
                  started_at: new Date().toISOString(),
                  ended_at: null,
                  pickup: activeBooking?.pickup ?? null,
                  destination: activeBooking?.destination ?? null,
                  passengers: activeBooking?.passengers ?? null,
                  fare: activeBooking?.fare ?? null,
                  eta: activeBooking?.eta ?? null,
                  booking_confirmed:
                    activeBooking?.status === "confirmed" ||
                    activeBooking?.status === "dispatched" ||
                    activeBooking?.status === "active" ||
                    false,
                  updated_at: new Date().toISOString(),
                })
                .eq("id", existingCall.id);
            } else {
              // No existing active card - create new one
              await supabase
                .from("live_calls")
                .upsert(
                  {
                    call_id: callId,
                    caller_phone: phone,
                    caller_name: state?.customerName,
                    status: "active",
                    source: "simple",
                    transcripts: [],

                    // Optional context for the dashboard when an active booking exists
                    pickup: activeBooking?.pickup ?? null,
                    destination: activeBooking?.destination ?? null,
                    passengers: activeBooking?.passengers ?? null,
                    fare: activeBooking?.fare ?? null,
                    eta: activeBooking?.eta ?? null,
                    booking_confirmed:
                      activeBooking?.status === "confirmed" ||
                      activeBooking?.status === "dispatched" ||
                      activeBooking?.status === "active" ||
                      false,
                  },
                  { onConflict: "call_id" }
                );
            }
          } catch (e) {
            console.error(`[${callId}] Background caller lookup failed:`, e);
          }
        })();
      }

      // Handle late phone number update (for eager init flow)
      if (message.type === "update_phone" && state) {
        const phone = message.phone || message.user_phone;
        if (phone && phone !== "unknown") {
          console.log(`[${state.callId}] 📱 Phone update received: ${phone}`);
          state.phone = phone;
          
          // Re-detect language from updated phone number
          const detectedLanguage = detectLanguageFromPhone(phone);
          if (detectedLanguage && detectedLanguage !== "en" && state.language === "auto") {
            console.log(`[${state.callId}] 🌐 Late language detection: ${detectedLanguage} (was auto)`);
            state.language = detectedLanguage;
            // NOTE: Greeting may have already started in English. 
            // For non-English callers with eager init, first words may be English before Ada switches.
          }
          
          // Fire-and-forget: Lookup caller history now that we have the phone
          (async () => {
            if (isConnectionClosed) return; // Guard against operations after disconnect
            try {
              const phoneKey = normalizePhone(phone);
              const { data: callerData } = await supabase
                .from("callers")
                .select("name, last_pickup, last_destination, total_bookings, preferred_language")
                .eq("phone_number", phoneKey)
                .maybeSingle();
              
              if (callerData && state) {
                state.callerLastPickup = callerData.last_pickup || null;
                state.callerLastDestination = callerData.last_destination || null;
                state.callerTotalBookings = callerData.total_bookings || 0;
                if (!state.customerName && callerData.name) {
                  state.customerName = callerData.name;
                }
                // Use preferred_language from DB if available
                if (callerData.preferred_language && state.language !== callerData.preferred_language) {
                  state.language = callerData.preferred_language;
                  console.log(`[${state.callId}] 🌐 Late lookup: using saved language ${callerData.preferred_language}`);
                }
                console.log(`[${state.callId}] 👤 Late caller lookup: ${state.callerTotalBookings} bookings`);
              }
              
              // Update live call with phone number
              await supabase.from("live_calls").update({
                caller_phone: phone,
                caller_name: state?.customerName,
              }).eq("call_id", state?.callId);
            } catch (e) {
              console.error(`[${state?.callId}] Late phone lookup failed:`, e);
            }
          })();
        }
        return;
      }

      // Handle audio format update from bridge (ulaw, slin, slin16, pcm48)
      if (message.type === "update_format" && state) {
        const newFormat = message.inbound_format as "ulaw" | "slin" | "slin16" | "pcm48";
        const formatToRate: Record<string, number> = { "pcm48": 48000, "slin16": 16000, "slin": 8000, "ulaw": 8000 };
        const newRate = message.inbound_sample_rate || formatToRate[newFormat] || 8000;
        
        // ALWAYS apply format updates - the bridge detected the actual format
        const formatChanged = newFormat && newFormat !== state.inboundAudioFormat;
        const rateChanged = newRate !== state.inboundSampleRate;
        
        if (formatChanged || rateChanged) {
          console.log(`[${state.callId}] 🔊 Audio format update: ${state.inboundAudioFormat}@${state.inboundSampleRate}Hz → ${newFormat}@${newRate}Hz`);
          if (newFormat) state.inboundAudioFormat = newFormat;
          state.inboundSampleRate = newRate;
        }
        return;
      }

      // Handle application-level ping from bridge (keeps connection alive)
      if (message.type === "ping") {
        try {
          socket.send(JSON.stringify({ type: "pong", timestamp: Date.now() }));
        } catch (e) {
          // Ignore send errors on pong
        }
        return;
      }

      if (message.type === "audio" && openaiConnected && openaiWs && state) {
        // ECHO GUARD: Ignore audio for a short window after Ada finishes speaking.
        // CRITICAL: Bypass when awaiting user confirmation ("yes please" after fare quote)
        // This ensures "Yes please" is heard even if spoken immediately after Ada finishes.
        if (Date.now() < state.echoGuardUntil && !state.confirmationResponsePending) {
          return;
        }
        
        // Bridge sends base64-encoded audio via JSON
        const binaryStr = atob(message.audio);
        const audioData = new Uint8Array(binaryStr.length);
        for (let i = 0; i < binaryStr.length; i++) {
          audioData[i] = binaryStr.charCodeAt(i);
        }
        
        // === MULTI-FORMAT AUDIO DECODER ===
        // Convert incoming audio to 24kHz PCM16 (required by OpenAI Realtime API)
        let pcm16_24k: Int16Array;
        const format = state.inboundAudioFormat;
        const sampleRate = state.inboundSampleRate;
        
        if (sampleRate === 24000) {
          // === 24kHz PCM (passthrough - bridge already resampled) ===
          pcm16_24k = new Int16Array(audioData.buffer, audioData.byteOffset, audioData.byteLength / 2);
        } else if (format === "pcm48" || sampleRate === 48000) {
          // === 48kHz PCM (from Opus decode) → 24kHz (2:1 downsample) ===
          // This is the HIGHEST QUALITY path - Opus provides 48kHz wideband audio
          // Simple 2:1 decimation with averaging for anti-aliasing
          const pcm16_48k = new Int16Array(audioData.buffer, audioData.byteOffset, audioData.byteLength / 2);
          const outputLen = Math.floor(pcm16_48k.length / 2);
          pcm16_24k = new Int16Array(outputLen);
          for (let i = 0; i < outputLen; i++) {
            // Average two samples for basic anti-aliasing
            const s0 = pcm16_48k[i * 2];
            const s1 = pcm16_48k[i * 2 + 1] ?? s0;
            pcm16_24k[i] = Math.round((s0 + s1) / 2);
          }
          // Log first time we receive 48kHz audio
          if (!state._logged48k) {
            console.log(`[${state.callId}] 🎵 Processing 48kHz PCM (Opus decode) → 24kHz (${pcm16_48k.length} → ${pcm16_24k.length} samples)`);
            state._logged48k = true;
          }
        } else if (format === "slin16" || sampleRate === 16000) {
          // === 16kHz PCM → 24kHz (1.5x upsample) ===
          // Decent quality - 16kHz captures most speech frequencies
          const pcm16_16k = new Int16Array(audioData.buffer, audioData.byteOffset, audioData.byteLength / 2);
          const outputLen = Math.floor(pcm16_16k.length * 1.5);
          pcm16_24k = new Int16Array(outputLen);
          for (let i = 0; i < pcm16_16k.length - 1; i++) {
            const s0 = pcm16_16k[i];
            const s1 = pcm16_16k[i + 1];
            const outIdx = Math.floor(i * 1.5);
            pcm16_24k[outIdx] = s0;
            if (outIdx + 1 < outputLen) {
              pcm16_24k[outIdx + 1] = Math.round(s0 + (s1 - s0) * 0.5);
            }
          }
          // Handle last sample
          const lastIdx = pcm16_16k.length - 1;
          const lastOutIdx = Math.floor(lastIdx * 1.5);
          if (lastOutIdx < outputLen) pcm16_24k[lastOutIdx] = pcm16_16k[lastIdx];
        } else if (format === "slin" || (format !== "ulaw" && sampleRate === 8000)) {
          // === 8kHz signed linear PCM → 24kHz (3x upsample) ===
          const pcm16_8k = new Int16Array(audioData.buffer, audioData.byteOffset, audioData.byteLength / 2);
          pcm16_24k = new Int16Array(pcm16_8k.length * 3);
          for (let i = 0; i < pcm16_8k.length - 1; i++) {
            const s0 = pcm16_8k[i];
            const s1 = pcm16_8k[i + 1];
            pcm16_24k[i * 3] = s0;
            pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
            pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
          }
          const lastIdx = pcm16_8k.length - 1;
          pcm16_24k[lastIdx * 3] = pcm16_8k[lastIdx];
          pcm16_24k[lastIdx * 3 + 1] = pcm16_8k[lastIdx];
          pcm16_24k[lastIdx * 3 + 2] = pcm16_8k[lastIdx];
        } else {
          // === Default: µ-law 8kHz → decode → 24kHz (3x upsample) ===
          // Step 1: Decode µ-law to 16-bit PCM (8kHz)
          const pcm16_8k = new Int16Array(audioData.length);
          for (let i = 0; i < audioData.length; i++) {
            const ulaw = ~audioData[i] & 0xFF;
            const sign = (ulaw & 0x80) ? -1 : 1;
            const exponent = (ulaw >> 4) & 0x07;
            const mantissa = ulaw & 0x0F;
            let sample = ((mantissa << 3) + 0x84) << exponent;
            sample -= 0x84;
            pcm16_8k[i] = sign * sample;
          }
          // Step 2: 3x upsample to 24kHz
          pcm16_24k = new Int16Array(pcm16_8k.length * 3);
          for (let i = 0; i < pcm16_8k.length - 1; i++) {
            const s0 = pcm16_8k[i];
            const s1 = pcm16_8k[i + 1];
            pcm16_24k[i * 3] = s0;
            pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
            pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
          }
          const lastIdx = pcm16_8k.length - 1;
          pcm16_24k[lastIdx * 3] = pcm16_8k[lastIdx];
          pcm16_24k[lastIdx * 3 + 1] = pcm16_8k[lastIdx];
          pcm16_24k[lastIdx * 3 + 2] = pcm16_8k[lastIdx];
        }

        // BARGE-IN: If Ada is currently speaking and we detect real user energy,
        // cancel the current AI response immediately so Ada can hear the caller.
        if (state.isAdaSpeaking && state.openAiResponseActive) {
          const inEchoGuard = Date.now() < (state.bargeInIgnoreUntil || 0);
          
          if (!inEchoGuard) {
            let sumSq = 0;
            for (let i = 0; i < pcm16_24k.length; i++) {
              const s = pcm16_24k[i];
              sumSq += s * s;
            }
            const rms = Math.sqrt(sumSq / Math.max(1, pcm16_24k.length));

            // Real barge-in: moderate RMS (adjust thresholds for 24kHz signal)
            const isRealBargeIn = rms >= 650 && rms < 20000;

            if (isRealBargeIn) {
              console.log(`[${state.callId}] 🛑 Barge-in detected (rms=${rms.toFixed(0)}) - cancelling AI speech`);
              try {
                openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                if (state.openAiResponseActive) {
                  openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                  state.openAiResponseActive = false;
                  state.discardCurrentResponseAudio = true;
                }
              } catch (e) {
                console.error(`[${state.callId}] ❌ Failed to cancel on barge-in:`, e);
              }

              state.isAdaSpeaking = false;
              state.echoGuardUntil = Date.now() + 200;
              socket.send(JSON.stringify({ type: "ai_interrupted" }));
            }
          }
        }
        
        // Convert to base64 and send to OpenAI
        const bytes = new Uint8Array(pcm16_24k.buffer);
        let binary = "";
        for (let i = 0; i < bytes.length; i++) {
          binary += String.fromCharCode(bytes[i]);
        }
        const base64Audio = btoa(binary);
        
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: base64Audio
        }));
      }

    } catch (e) {
      console.error("[simple] Message error:", e);
    }
  };

  socket.onclose = async () => {
    console.log(`[${state?.callId || "unknown"}] Client disconnected`);
    
    // === GRACEFUL CLOSURE GUARD ===
    // If we're in a closing grace period (Ada speaking goodbye), do NOT cleanup yet.
    // The graceful timeout will handle cleanup after the goodbye audio finishes.
    if (closingGracePeriodActive) {
      console.log(`[${state?.callId || "unknown"}] 🛡️ Closing grace period active - deferring cleanup to graceful timeout`);
      // Keep the OpenAI connection alive so Ada can finish speaking
      // The closingGraceTimeoutId will handle the full cleanup
      return;
    }
    
    // Mark connection as closed to prevent further operations
    isConnectionClosed = true;
    
    // Clear any pending closing grace timeout
    if (closingGraceTimeoutId) {
      clearTimeout(closingGraceTimeoutId);
      closingGraceTimeoutId = null;
    }
    
    // Stop keep-alive pings
    stopKeepAlive();
    
    // Stop session timeout
    stopSessionTimeout();
    
    // Clear any pending flush timers to prevent memory leaks
    if (state?.transcriptFlushTimer) {
      clearTimeout(state.transcriptFlushTimer);
      state.transcriptFlushTimer = null;
    }
    
    // Clear audio buffers to free memory
    if (state) {
      state.pendingAudioBuffer = [];
      state.halfDuplexBuffer = [];
      state.pendingDispatchEvents = [];
    }
    
    // Final flush on disconnect to capture any remaining transcripts
    if (state) {
      immediateFlush(state);
      
      // Save preferred_language on disconnect if we have phone and language
      if (state.phone && state.language) {
        const phoneKey = normalizePhone(state.phone);
        try {
          await supabase.from("callers").upsert({
            phone_number: phoneKey,
            preferred_language: state.language,
            updated_at: new Date().toISOString()
          }, { onConflict: "phone_number" });
          console.log(`[${state.callId}] 🌐 Saved preferred language on disconnect: ${state.language}`);
        } catch (e) {
          console.error(`[${state.callId}] Failed to save language on disconnect:`, e);
        }
      }
    }
    
    // Unsubscribe and remove dispatch channel to prevent memory leaks
    if (dispatchChannel) {
      try {
        await dispatchChannel.unsubscribe();
        supabase.removeChannel(dispatchChannel);
        console.log(`[${state?.callId || "unknown"}] 🧹 Dispatch channel cleaned up`);
      } catch (e) {
        console.error(`[${state?.callId || "unknown"}] Failed to cleanup dispatch channel:`, e);
      }
      dispatchChannel = null;
    }
    
    // Close OpenAI WebSocket
    if (openaiWs) {
      try {
        openaiWs.close();
      } catch (e) {
        // Ignore close errors
      }
      openaiWs = null;
    }
    openaiConnected = false;
    
    // Nullify state to allow garbage collection
    state = null;
  };

  socket.onerror = (err) => {
    console.error("[simple] Socket error:", err);
  };

  return response;
});
