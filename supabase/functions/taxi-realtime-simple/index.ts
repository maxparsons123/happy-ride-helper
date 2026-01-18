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
const DEMO_SIMPLE_MODE = true;

const DEFAULT_COMPANY = "247 Radio Carz";
const DEFAULT_AGENT = "Ada";
const DEFAULT_VOICE = "shimmer";

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

  try {
    const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
    if (!LOVABLE_API_KEY) {
      console.warn("⚠️ LOVABLE_API_KEY not set - returning raw time");
      return rawTime;
    }
    
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
    
    if (!response.ok) {
      console.error(`Time normalization API error: ${response.status}`);
      return rawTime;
    }
    
    const data = await response.json();
    const normalizedTime = data.choices?.[0]?.message?.content?.trim();
    
    if (normalizedTime) {
      console.log(`🕐 Time normalized: "${rawTime}" → "${normalizedTime}"`);
      return normalizedTime;
    }
    
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

LANGUAGE: {{language_instruction}}
You are multilingual. If caller asks for a different language, switch immediately.

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
❌ NEVER use 'As directed' or any placeholder - always ask for specifics.
❌ NEVER move to Summary until all 4 checklist items are filled.
❌ NEVER repeat addresses after the summary is confirmed.
❌ NEVER ask "is that where you want to go?" or "is that correct?" after each address - just accept it and move on.
❌ NEVER ask for "more details" or "could you be more specific" - accept the address as given.
✅ Accept business names, landmarks, and place names as valid pickup/destination (e.g., "Sweet Spot", "Tesco", "The Hospital", "Train Station").
✅ Only ask for a house number if it's clearly a residential street address missing a number.
✅ If the user gives a place name or business, accept it immediately and move to the next question.
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
  if (trimmed.length < 2) return true;
  
  // Check regex patterns first
  if (HALLUCINATION_PATTERNS.some(pattern => pattern.test(trimmed))) {
    return true;
  }
  
  // Check phantom phrases with normalized comparison (strip punctuation)
  const clean = trimmed.toLowerCase().replace(/[^\w\s]/g, "");
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

  // Inbound audio format from bridge: "ulaw" (8kHz), "slin" (8kHz), or "slin16" (16kHz)
  inboundAudioFormat: "ulaw" | "slin" | "slin16";
  inboundSampleRate: number; // 8000 or 16000

  // Rasa-style audio processing toggle
  useRasaAudioProcessing: boolean;
  callerTotalBookings: number;

  // GPS location from external system
  gpsLat: number | null;
  gpsLon: number | null;
  gpsRequired: boolean; // If true, reject calls without GPS

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
  
  // DUPLICATE QUESTION PREVENTION: Track the exact text of the last question spoken
  // Used to cancel if Ada tries to ask the same question twice in a row (within 10s)
  lastSpokenQuestion: string | null;
  lastSpokenQuestionAt: number | null;

  // MULTI-QUESTION GUARD: prevents "... destination? ... passengers?" in one assistant turn
  lastMultiQuestionFixAt: number | null;

  // GREETING COMPLETION GUARD: prevents follow-up responses until greeting is fully delivered
  greetingDelivered: boolean;

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

  // Increase idle timeout to reduce abrupt disconnects while waiting for dispatch callbacks
  // (Deno will manage protocol-level ping/pong internally based on this timeout.)
  const { socket, response } = Deno.upgradeWebSocket(req, { idleTimeout: 120_000 });
  
  let openaiWs: WebSocket | null = null;
  let state: SessionState | null = null;
  let openaiConnected = false;
  let preConnected = false; // Track if we pre-connected before init
  let pendingGreeting = false; // Track if greeting should fire when OpenAI ready
  let dispatchChannel: ReturnType<typeof supabase.channel> | null = null; // Track for cleanup
  let isConnectionClosed = false; // Prevent operations after cleanup
  let keepAliveInterval: number | null = null; // Keep-alive ping interval to prevent timeout
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
  
  // --- Keep-alive ping to prevent WebSocket timeout during dispatch callback wait ---
  const startKeepAlive = (callId: string) => {
    if (keepAliveInterval) return; // Already running
    
    // Send ping every 15 seconds to keep connection alive
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
    }, 15000) as unknown as number;
    
    console.log(`[${callId}] 🔄 Keep-alive started (15s interval)`);
  };
  
  const stopKeepAlive = () => {
    if (keepAliveInterval) {
      clearInterval(keepAliveInterval);
      keepAliveInterval = null;
      console.log(`[${state?.callId || "unknown"}] 🛑 Keep-alive stopped`);
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
          // Threshold 0.5 balances sensitivity vs false triggers
          // Silence 1200ms prevents jumping to next question while user thinks
          // Prefix 300ms captures speech start without clipping
          threshold: sessionState.useRasaAudioProcessing ? 0.7 : 0.5,
          prefix_padding_ms: sessionState.useRasaAudioProcessing ? 250 : 300,
          silence_duration_ms: sessionState.useRasaAudioProcessing ? 800 : 1200,
        },
        temperature: 0.6, // OpenAI Realtime API minimum is 0.6
        tools: TOOLS,
        tool_choice: "auto"
      }
    };

    openaiWs?.send(JSON.stringify(sessionUpdate));

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
        
        return supabase
          .from("live_calls")
          .update({ transcripts: mergedTranscripts, updated_at: new Date().toISOString() })
          .eq("call_id", callId);
      })
      .then((result: any) => {
        if (result?.error) console.error(`[${callId}] DB flush error:`, result.error);
      });
  };

  // Aggressive batching - only flush every 5 seconds during conversation
  const FLUSH_INTERVAL_MS = 5000;
  
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
          console.log(`[${sessionState.callId}] 🛑 Cancelling VAD-triggered response - extraction in progress`);
          openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
          sessionState.discardCurrentResponseAudio = true;
          break;
        }

        // ✅ GREETING COMPLETION GUARD: Block all VAD-triggered responses until greeting is fully delivered
        // This prevents the model from firing all questions at once before the greeting finishes
        // EXCEPTION: Allow the first response (the greeting itself) through
        if (!sessionState.greetingDelivered && sessionState.openAiResponseActive) {
          // This is a second response trying to fire before the greeting is done - block it
          console.log(`[${sessionState.callId}] 🛑 Cancelling response - greeting not yet delivered`);
          openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
          sessionState.discardCurrentResponseAudio = true;
          break;
        }

        // ✅ PENDING MODIFICATION GUARD: While waiting for the caller to confirm an update,
        // do NOT allow VAD/noise to trigger extra assistant turns (this causes repeated summaries).
        // We allow exactly ONE response right after we inject the modification prompt.
        if (sessionState.pendingModification) {
          if (sessionState.modificationPromptPending) {
            sessionState.modificationPromptPending = false;
          } else {
            console.log(`[${sessionState.callId}] 🛑 Cancelling VAD-triggered response - awaiting modification confirmation`);
            openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
            sessionState.discardCurrentResponseAudio = true;
            break;
          }
        }
        
        // ✅ AWAITING DISPATCH GUARD: Block VAD responses while waiting for dispatch fare callback
        // This prevents Ada from hallucinating fare amounts before dispatch responds
        if (sessionState.awaitingDispatchCallback) {
          console.log(`[${sessionState.callId}] 🛑 Cancelling VAD-triggered response - awaiting dispatch callback`);
          openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
          sessionState.discardCurrentResponseAudio = true;
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
            console.log(`[${sessionState.callId}] 🛑 Cancelling VAD-triggered response - waiting for user response to "anything else?" (${msSinceAsked}ms / ${waitPeriodMs}ms)`);
            openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
            sessionState.discardCurrentResponseAudio = true;
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
            console.log(`[${sessionState.callId}] 🛑 Cancelling VAD-triggered response after callEnded`);
            openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
          }
        }
        break;

       case "response.done":
         sessionState.openAiResponseActive = false;
         sessionState.discardCurrentResponseAudio = false;

         // ✅ GREETING COMPLETION: Mark greeting as delivered on first response.done
         // This unlocks follow-up responses and prevents the model from firing all questions at once
         if (!sessionState.greetingDelivered) {
           sessionState.greetingDelivered = true;
           console.log(`[${sessionState.callId}] 🎉 Greeting delivered - follow-up responses now allowed`);
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
         break;

      case "response.audio.delta":
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
          break;
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
              socket.send(JSON.stringify({ type: "audio", audio: audioChunk }));
            }
            sessionState.pendingAudioBuffer = [];
            sessionState.audioVerified = true; // Stop buffering
          }
        } else {
          // Forward audio to bridge immediately
          socket.send(JSON.stringify({
            type: "audio",
            audio: message.delta
          }));
        }
        break;

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

          if (!isWithinMultiFixCooldown && currentText.length > 25) {
            const isSummaryLike = /summari[sz]e|summary|let me quickly summarize|alright, let me/i.test(lowerText);
            const isPricingLike = /fare|estimated arrival|eta|would you like me to confirm/i.test(lowerText);

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
                  if (firstQuestion) console.log(`[${sessionState.callId}] ✅ Keeping: "${firstQuestion}"`);

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

                  const forcedQuestion = firstQuestion || "Please ask only one question.";
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
          
          // ✅ SKIP REPETITION DETECTION when Ada is giving the final summary (all fields complete)
          // This prevents cancelling the legitimate booking summary after user provides last field (e.g. time)
          const allFieldsComplete = sessionState.booking.pickup && 
            sessionState.booking.destination && 
            (sessionState.booking.passengers !== null && sessionState.booking.passengers > 0);
          const isSummaryContext = /^so\s+(that'?s|thats)|let me confirm|to confirm|your booking/i.test(lowerText);
          const isLegitSummary = allFieldsComplete && isSummaryContext;
          
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
          const duplicateQuestionWindow = 10000; // 10 seconds
          const isWithinDuplicateWindow = sessionState.lastSpokenQuestionAt && 
            (Date.now() - sessionState.lastSpokenQuestionAt < duplicateQuestionWindow);
          
          if (isWithinDuplicateWindow && sessionState.lastSpokenQuestion && currentText.length > 15) {
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
                socket.send(JSON.stringify({ type: "audio", audio: audioChunk }));
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
        const lastAssistantText = sessionState.transcripts
          .filter(t => t.role === "assistant")
          .slice(-1)[0]?.text || "";
        const lowerAssistantText = lastAssistantText.toLowerCase();
        
        // Check if this is a question (ends with ? or is a known question pattern)
        const isQuestion = /\?$/.test(lastAssistantText.trim()) || 
          /(?:where|how many|when|what|which|would you|do you|shall i|is that)/i.test(lowerAssistantText);
        
        if (/where.*(?:pick\s*(?:ed\s*)?up|from|pickup)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "pickup";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
          console.log(`[${sessionState.callId}] 🎯 Ada asked about: PICKUP`);
        } else if (/(?:destination|where.*(?:going|to\??|drop|travel)|what is your destination)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "destination";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
          console.log(`[${sessionState.callId}] 🎯 Ada asked about: DESTINATION`);
        } else if (/(?:how many|passengers|people|travell)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "passengers";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
          console.log(`[${sessionState.callId}] 🎯 Ada asked about: PASSENGERS`);
        } else if (/(?:when|what time|timing|schedule)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "time";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
          console.log(`[${sessionState.callId}] 🎯 Ada asked about: TIME`);
        } else if (/(?:is that correct|confirm|shall i book|book that|go ahead)/i.test(lowerAssistantText)) {
          sessionState.lastQuestionType = "confirmation";
          sessionState.lastQuestionAt = Date.now();
          if (isQuestion) {
            sessionState.lastSpokenQuestion = lastAssistantText;
            sessionState.lastSpokenQuestionAt = Date.now();
          }
          console.log(`[${sessionState.callId}] 🎯 Ada asked for: CONFIRMATION`);
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
             socket.send(JSON.stringify({ type: "audio", audio: audioChunk }));
           }
           sessionState.pendingAudioBuffer = [];
           sessionState.audioVerified = true;
         }
         break;
      }

      case "input_audio_buffer.speech_started": {
        // Track when user started speaking for timing diagnostics
        sessionState.speechStartTime = Date.now();
        console.log(`[${sessionState.callId}] 🎙️ Speech started`);
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
        
        if (hasActiveBookingContext && isSubstantialSpeech && 
            !sessionState.extractionInProgress && 
            !sessionState.pendingQuote &&
            !sessionState.modificationPromptPending) {
          console.log(`[${sessionState.callId}] 🛡️ PRE-EMPTIVE GUARD: Blocking VAD response until transcript processed (active booking + ${speechDuration}ms speech)`);
          sessionState.extractionInProgress = true;
          
          // Safety timeout: clear the guard after 5s if transcript never arrives
          // This prevents Ada from getting stuck silent
          setTimeout(() => {
            if (sessionState.extractionInProgress && 
                sessionState.speechStopTime && 
                Date.now() - sessionState.speechStopTime > 4500) {
              console.log(`[${sessionState.callId}] ⏰ Pre-emptive guard timeout - clearing extractionInProgress`);
              sessionState.extractionInProgress = false;
            }
          }, 5000);
        }
        
        // NOTE: Do NOT call response.create here - server VAD handles turn-taking automatically
        break;
      }

      case "conversation.item.input_audio_transcription.completed": {
        // User transcript from Whisper
        const rawText = String(message.transcript || "").trim();
        
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
        if (!rawText || isHallucination(rawText)) {
          sessionState.sttMetrics.filteredHallucinations++;
          console.log(`[${sessionState.callId}] 🔇 Filtered hallucination: "${rawText}" (total filtered: ${sessionState.sttMetrics.filteredHallucinations})`);
          break;
        }
        
        // Echo guard: Filter transcripts that are echoes of dispatch TTS fare scripts
        // These happen when Whisper transcribes Ada's voice playing the fare prompt
        const lowerRaw = rawText.toLowerCase();
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
        
        const userText = correctTranscript(rawText);
        const wasCorreced = rawText !== userText;
        
        // Update STT metrics
        sessionState.sttMetrics.totalTranscripts++;
        sessionState.sttMetrics.totalChars += userText.length;
        sessionState.sttMetrics.totalWords += userText.split(/\s+/).filter(w => w.length > 0).length;
        if (wasCorreced) {
          sessionState.sttMetrics.correctedTranscripts++;
        }
        
        // Enhanced logging with audio processing mode context
        const audioMode = sessionState.useRasaAudioProcessing ? "RASA" : "STD";
        const correctionRate = ((sessionState.sttMetrics.correctedTranscripts / sessionState.sttMetrics.totalTranscripts) * 100).toFixed(1);
        console.log(`[${sessionState.callId}] 👤 [${audioMode}] User: "${userText}"${wasCorreced ? ` (corrected from: "${rawText}")` : ""}`);
        console.log(`[${sessionState.callId}] 📊 [${audioMode}] STT Stats: transcripts=${sessionState.sttMetrics.totalTranscripts}, words=${sessionState.sttMetrics.totalWords}, corrected=${correctionRate}%, avgDelay=${sessionState.sttMetrics.avgTranscriptDelayMs.toFixed(0)}ms`);

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
          
          // Prevent double-asking passengers due to mismatch loop
          const recentPassengerMismatchInjection = sessionState.lastPassengerMismatchAt && 
            (Date.now() - sessionState.lastPassengerMismatchAt < 15000); // 15 second cooldown
          
          if (isRecentQuestion && sessionState.lastQuestionType === "passengers" && isAddressLike && !isPassengerCount && !recentPassengerMismatchInjection) {
            console.log(`[${sessionState.callId}] 🔄 ADDRESS-VS-PASSENGER MISMATCH: User gave address "${userText}" when asked about passengers`);
            
            // Store the address as potential destination correction
            const mismatchedAddress = userText;
            
            // Mark that we just injected a mismatch prompt to prevent loop
            sessionState.lastPassengerMismatchAt = Date.now();
            
            // Prevent any auto-VAD response from leaking partial speech while we force the mismatch prompt.
            sessionState.discardCurrentResponseAudio = true;
            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];

            // Cancel anything already in-flight and clear audio to avoid competing responses.
            // (Even if no response is active yet, cancel is safe and keeps the flow deterministic.)
            if (openaiWs && openaiConnected) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
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

          // === EXPLICIT BYE/GOODBYE DETECTION (HIGHEST PRIORITY) ===
          // If user says "bye" (even multiple times), they want to end the call immediately.
          // This takes priority over fare confirmation, booking prompts, etc.
          // 
          // IMPORTANT: "No thanks" / "nothing else" phrases should ONLY trigger goodbye
          // AFTER Ada has asked "Is there anything else I can help you with?" (askedAnythingElse=true).
          // Otherwise, "no thanks" might mean rejecting a fare quote, not ending the call.
          // IMPORTANT: Enforce 3-second grace period after "anything else?" to let users actually respond
          const isHardGoodbye = /\b(bye|goodbye|see ya|see you|cya|i'm done|im done|hang up|end call)\b/i.test(lowerUserText);
          const isSoftGoodbye = /\b(no thank you|no thanks|no that's all|no thats all|nothing else|that's it|thats it|that'll be all|thatll be all|i'm good|im good|all good|all done)\b/i.test(lowerUserText) ||
            // Special case: just "no" or "no." when asked "anything else?" - user is declining
            (sessionState.askedAnythingElse && /^no\.?$/i.test(lowerUserText.trim()));
          
          // Check if grace period has passed since "anything else?" was asked
          // Uses configurable goodbyeGraceMs from agent settings (default 3000ms)
          const gracePeriodMs = sessionState.goodbyeGraceMs || 3000;
          const enoughTimeElapsed = !sessionState.askedAnythingElseAt || 
            (Date.now() - sessionState.askedAnythingElseAt > gracePeriodMs);
          
          // Hard goodbye always works (user explicitly wants to leave).
          // Soft goodbye only works AFTER Ada asked "anything else?" AND 3 seconds have passed.
          const isExplicitGoodbye = (isHardGoodbye || (isSoftGoodbye && sessionState.askedAnythingElse && enoughTimeElapsed)) &&
            // Exclude "bye" in compound phrases like "going to the airport" (unlikely but guard against)
            !/going to|from|pick ?up|drop ?off/i.test(lowerUserText);
          
          if (isExplicitGoodbye && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] 👋 Explicit goodbye detected: "${userText}" (hardGoodbye=${isHardGoodbye}, softGoodbye=${isSoftGoodbye}, gracePeriodPassed=${enoughTimeElapsed}) - ending call`);
            
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

            openaiWs.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: "[SYSTEM: The customer said goodbye. Say 'Safe travels!' and nothing more. Then stay completely silent.]",
                    },
                  ],
                },
              })
            );

            openaiWs.send(JSON.stringify({ type: "response.create" }));
            
            // Close OpenAI connection after delay to let goodbye audio finish
            setTimeout(() => {
              console.log(`[${sessionState.callId}] 🔌 Closing OpenAI WebSocket after explicit goodbye`);
              openaiWs?.close();
            }, 3000); // 3 second delay for goodbye audio
            
            break;
          }

          // === POST-BOOKING GOODBYE SHORTCUT (SECONDARY) ===
          // If a booking was just confirmed and the caller says thanks/that's all, end cleanly.
          // This prevents Ada from accidentally starting a new quote from stale booking context.
          if (
            sessionState.lastBookTaxiSuccessAt &&
            Date.now() - sessionState.lastBookTaxiSuccessAt < 2 * 60 * 1000 &&
            !sessionState.pendingQuote &&
            /\b(thanks|thank you|thx|cheers|that's fine|thats fine|that's all|thats all|no thanks|no thank you)\b/i.test(lowerUserText) &&
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
            
            // Close OpenAI connection after delay to let goodbye audio finish
            setTimeout(() => {
              console.log(`[${sessionState.callId}] 🔌 Closing OpenAI WebSocket after post-booking goodbye`);
              openaiWs?.close();
            }, 8000); // 8 second delay for full goodbye audio
            
            break;
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
          
          // Detect if user just confirmed addresses - start audio buffering
          // This catches "yes", "yeah", "that's correct" etc. that trigger book_taxi
          const isConfirmationPhrase = /\b(yes|yeah|yep|correct|that's right|that's correct|right|go ahead|sure|ok|okay|please|ja|jawel|prima|goed|akkoord|doe maar|naturlich|klar|genau|oui|d'accord|sí|claro|correcto)\b/i.test(lowerUserText);
          
          // If user just confirmed and we don't have a confirmed booking yet, start buffering
          if (isConfirmationPhrase && !sessionState.bookingConfirmedThisTurn && sessionState.booking.version === 0) {
            console.log(`[${sessionState.callId}] 🔒 User confirmed - starting audio buffering until book_taxi succeeds`);
            sessionState.audioVerified = false;
            sessionState.pendingAudioBuffer = [];
          }

          // Existing booking context for modification logic
          const hasExistingBookingContext =
            sessionState.hasActiveBooking || sessionState.booking.version > 0 || !!sessionState.lastBookTaxiSuccessAt;

          // === NEW BOOKING AUTO-DETECTION (AI-BASED) ===
          // When there's NO existing booking and user provides what sounds like pickup AND destination,
          // extract and ask for confirmation before getting fare quote
          
          // Quick check: does this look like a new booking with both addresses?
          const hasPickupPhrase = /\b(from|pick\s*up|pickup|at|outside|near)\b/i.test(lowerUserText);
          const hasDestinationPhrase = /\b(to|going\s+to|destination|drop\s*off|dropoff)\b/i.test(lowerUserText);
          const isSubstantialMessage = lowerUserText.length > 15; // Must be substantial enough to contain addresses
          
          const mightBeNewBooking = !hasExistingBookingContext && 
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
              
              // Only proceed if we have BOTH pickup AND destination
              if (!extractedPickup || !extractedDestination) {
                console.log(`[${sessionState.callId}] AI extraction incomplete - pickup="${extractedPickup}", dest="${extractedDestination}". Letting Ada ask for missing info.`);
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
              
              // Mark new booking as pending confirmation
              sessionState.pendingNewBooking = {
                pickup: extractedPickup,
                destination: extractedDestination,
                passengers: extractedPassengers,
                bags: extractedBags,
                timestamp: Date.now()
              };
              
              // Sync to live_calls
              supabase.from("live_calls").update({
                pickup: extractedPickup,
                destination: extractedDestination,
                passengers: extractedPassengers,
                updated_at: new Date().toISOString()
              }).eq("call_id", sessionState.callId).then(() => {
                console.log(`[${sessionState.callId}] ✅ live_calls updated with new booking extraction`);
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
          const isSimpleResponse = /^(yes|no|yeah|yep|nope|nah|okay|ok|sure|correct|right|that's right|that's correct|fine|good|perfect|great|lovely|cheers|thanks|thank you|bye|goodbye|ta)\.?$/i.test(lowerUserText.trim());
          
          // Detect if user is providing address-like content (anything substantial, not just small talk)
          // Minimum 8 chars to filter out "hi", "hello", "yes", etc.
          const isSubstantialInput = lowerUserText.length >= 8 && !isSimpleResponse && !isConfirmationPhrase;
          
          // AI-FIRST APPROACH: If user has active booking and says something substantial,
          // use AI to extract and compare - no more relying on fragile regex patterns
          // NOTE: We now allow extraction even with pendingQuote, as user might be changing their trip
          // after being asked "keep, change, or cancel?"
          const shouldUseAiExtraction = hasExistingBookingContext && 
            isSubstantialInput &&
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
              const newPickup = correctTranscript(rawNewPickup);
              const newDestination = correctTranscript(rawNewDestination);
              const newPassengers = extracted.passengers || oldPassengers;
              const newBags = extracted.luggage ? parseInt(extracted.luggage) || 0 : oldBags;
              
              if (rawNewPickup !== newPickup || rawNewDestination !== newDestination) {
                console.log(`[${sessionState.callId}] 🔧 Corrected modification: pickup="${rawNewPickup}"→"${newPickup}", dest="${rawNewDestination}"→"${newDestination}"`);
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
              openaiWs?.send(JSON.stringify({ type: "response.create" }));
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

      case "error":
        console.error(`[${sessionState.callId}] 🚨 OpenAI Error:`, message.error);
        break;
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
                sessionState.gpsLat = geocodeData.lat;
                sessionState.gpsLon = geocodeData.lon;
                
                const formattedAddress = geocodeData.formatted_address || args.location;
                const city = geocodeData.city || geocodeData.locality || null;
                
                console.log(`[${sessionState.callId}] ✅ Location geocoded: ${geocodeData.lat}, ${geocodeData.lon} (${formattedAddress})`);
                
                // Save to caller_gps table (temporary, expires in 1 hour)
                await supabase.from("caller_gps").upsert({
                  phone_number: sessionState.phone,
                  lat: geocodeData.lat,
                  lon: geocodeData.lon,
                  expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString()
                }, { onConflict: "phone_number" });
                
                // Update live_calls with GPS
                await supabase.from("live_calls").update({
                  gps_lat: geocodeData.lat,
                  gps_lon: geocodeData.lon,
                  gps_updated_at: new Date().toISOString()
                }).eq("call_id", sessionState.callId);
                
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
            if (pendingQuote.callback_url) {
              try {
                console.log(`[${sessionState.callId}] 📡 POSTing confirmation to callback_url: ${pendingQuote.callback_url}`);
              const confirmPayload = {
                  call_id: sessionState.callId,
                  action: "confirmed",
                  response: "confirmed",  // C# bridge compatibility
                  pickup: finalPickup,
                  destination: finalDestination,
                  fare: pendingQuote.fare,
                  eta: pendingQuote.eta,
                  customer_name: sessionState.customerName,
                  caller_phone: sessionState.phone,
                  timestamp: new Date().toISOString()
                };
                
                const confirmResp = await fetch(pendingQuote.callback_url, {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify(confirmPayload)
                });
                
                console.log(`[${sessionState.callId}] 📬 Callback response: ${confirmResp.status}`);
              } catch (callbackErr) {
                console.error(`[${sessionState.callId}] ⚠️ Callback POST failed:`, callbackErr);
              }
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
          let finalPickup: string;
          let finalDestination: string;
          let sourceDiscrepancy = false;
          
          // Use extraction as primary source if available, fall back to Ada's corrected args
          if (extractedPickup && extractedPickup.length > 2) {
            finalPickup = extractedPickup;
            if (extractedPickup !== adaPickup) {
              console.log(`[${sessionState.callId}] 🧠 Using AI-extracted pickup: "${extractedPickup}" (Ada had: "${adaPickup}")`);
            }
          } else {
            finalPickup = adaPickup;
            console.log(`[${sessionState.callId}] 📝 Using Ada's pickup (no extraction): "${adaPickup}"`);
          }
          
          if (extractedDestination && extractedDestination.length > 2) {
            finalDestination = extractedDestination;
            if (extractedDestination !== adaDestination) {
              console.log(`[${sessionState.callId}] 🧠 Using AI-extracted destination: "${extractedDestination}" (Ada had: "${adaDestination}")`);
            }
          } else {
            finalDestination = adaDestination;
            console.log(`[${sessionState.callId}] 📝 Using Ada's destination (no extraction): "${adaDestination}"`);
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
            try {
              console.log(`[${sessionState.callId}] 📡 Calling dispatch webhook: ${DISPATCH_WEBHOOK_URL}`);
              console.log(`[${sessionState.callId}] ⏳ Sending booking to dispatch, will poll for callback response...`);
              
              // NOTE: Do NOT force Ada to say "Please wait" here.
              // Dispatch typically returns `ask_confirm` quickly; forcing a response here can interrupt
              // or duplicate the fare prompt (price → please-wait → price).
              // If we need a waiting prompt, it should be a delayed fallback only when dispatch is slow.

              // Get recent user transcripts as structured array for comparison
              const userTranscripts = sessionState.transcripts
                .filter(t => t.role === "user")
                .slice(-6) // Last 6 user messages
                .map(t => ({ text: t.text, timestamp: t.timestamp }));
              
              // Format phone number for WhatsApp: strip '+' prefix and leading '0'
              let formattedPhone = sessionState.phone?.replace(/^\+/, '') || '';
              if (formattedPhone.startsWith('0')) {
                formattedPhone = formattedPhone.slice(1);
              }
              
              // Normalize pickup time to YYYY-MM-DD HH:MM format
              // Use finalPickupTime which includes Last-Word-Wins transcript extraction
              const rawPickupTime = finalPickupTime || sessionState.booking.pickup_time || args.pickup_time || "now";
              const normalizedPickupTime = await normalizePickupTime(rawPickupTime);
              console.log(`[${sessionState.callId}] 🕐 Pickup time: raw="${rawPickupTime}" → normalized="${normalizedPickupTime}"`);
              
              const webhookPayload = {
                job_id: jobId,
                call_id: sessionState.callId,
                caller_phone: formattedPhone,
                caller_name: sessionState.customerName,
                // Normalized/validated addresses (after dual-source + modification guard)
                ada_pickup: finalPickup,
                ada_destination: finalDestination,
                // Raw caller addresses (what they actually said)
                callers_pickup: adaPickup !== finalPickup ? adaPickup : null,
                callers_dropoff: adaDestination !== finalDestination ? adaDestination : null,
                // Nearest place flags (if customer requested "nearest hospital", "closest tube station", etc.)
                nearest_pickup: extractedBooking?.nearest_pickup || null,
                nearest_dropoff: extractedBooking?.nearest_dropoff || null,
                // Raw STT transcripts from this call - each turn separately
                user_transcripts: userTranscripts,
                // GPS location (if available)
                gps_lat: sessionState.gpsLat,
                gps_lon: sessionState.gpsLon,
                // Booking details
                passengers: finalPassengers,
                bags: finalBags,
                vehicle_type: finalVehicleType,
                vehicle_request: args.vehicle_request || null,
                pickup_time: normalizedPickupTime,
                special_requests: args.special_requests || null,
                timestamp: new Date().toISOString()
              };
              
              // POST to dispatch webhook and require a 2xx ack.
              // (If this fails, we must NOT let Ada confirm the booking.)

              // Mark the latest quote request so we can ignore stale ask_confirm broadcasts
              sessionState.quoteRequestedAt = Date.now();
              sessionState.quoteTripKey = currentTripKey;

              const controller = new AbortController();
              const timeoutId = setTimeout(() => controller.abort(), 5000);

              const postResp = await fetch(DISPATCH_WEBHOOK_URL, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(webhookPayload),
                signal: controller.signal,
              });

              clearTimeout(timeoutId);
              dispatchPostStatus = postResp.status;
              dispatchPostOk = postResp.ok;

              const respBody = await postResp.text().catch(() => "");
              console.log(
                `[${sessionState.callId}] 📬 Dispatch webhook response: ${postResp.status} ${postResp.statusText}` +
                (respBody ? ` - ${respBody.slice(0, 300)}` : "")
              );

              if (!postResp.ok) {
                throw new Error(`Dispatch webhook POST failed with status ${postResp.status}`);
              }
              
              // DON'T tell Ada to say "please wait" here - the dispatch ask_confirm will come very quickly
              // and we don't want to cause a "conversation_already_has_active_response" error
              // The ask_confirm handler will make Ada speak the fare confirmation message directly
              
              // Now poll live_calls table for dispatch response (fare, eta, or say message)
              // Dispatch will call taxi-dispatch-callback which updates live_calls
              const pollTimeout = 25000; // 25 seconds max wait (callback can take 15-20s)
              const pollInterval = 500; // Check every 500ms
              const pollStart = Date.now();
              let dispatchResult: any = null;
              
              // Block VAD responses while waiting for dispatch
              sessionState.awaitingDispatchCallback = true;
              
              console.log(`[${sessionState.callId}] 🔄 Polling for dispatch callback response (${pollTimeout/1000}s timeout)...`);
              
              while (Date.now() - pollStart < pollTimeout) {
                // Check live_calls for dispatch response
                const { data: callData } = await supabase
                  .from("live_calls")
                  .select("fare, eta, status, transcripts, updated_at")
                  .eq("call_id", sessionState.callId)
                  .single();
                
                const updatedAtMs = callData?.updated_at ? new Date(callData.updated_at).getTime() : 0;
                const hasFreshUpdate = updatedAtMs > pollStart;

                // Check for dispatch_confirm transcript with the confirmation message
                const transcripts = callData?.transcripts as any[] || [];
                const dispatchConfirm = transcripts.find(t => 
                  t.role === "dispatch_confirm" && 
                  new Date(t.timestamp).getTime() > pollStart
                );
                
                if (dispatchConfirm) {
                  console.log(`[${sessionState.callId}] ✅ Dispatch confirmed with message: "${dispatchConfirm.text}"`);
                  dispatchResult = {
                    fare: dispatchConfirm.fare || callData?.fare || null,
                    eta_minutes: parseInt(dispatchConfirm.eta) || parseInt(callData?.eta) || 8,
                    confirmed: true,
                    confirmation_message: dispatchConfirm.text,
                    booking_ref: dispatchConfirm.booking_ref || null,
                    status: dispatchConfirm.status
                  };
                  break;
                }
                
                // Check for ask_confirm (fare confirmation) request FIRST.
                // Dispatch typically sets fare/eta in DB at the same time, so we must not treat
                // a fresh fare/eta update as a confirmed booking if ask_confirm exists.
                const dispatchAskConfirm = transcripts.find(t =>
                  t.role === "dispatch_ask_confirm" &&
                  new Date(t.timestamp).getTime() > pollStart
                );

                 if (dispatchAskConfirm) {
                   console.log(`[${sessionState.callId}] 💰 Dispatch ask_confirm: "${dispatchAskConfirm.text}" (fare=${dispatchAskConfirm.fare}, eta=${dispatchAskConfirm.eta})`);

                   // Build a deterministic prompt so Ada repeats the *exact* fare/ETA (no hallucinated numbers)
                   const fareNum = Number(String(dispatchAskConfirm.fare ?? "").replace(/[^0-9.]/g, ""));
                   const fareText = Number.isFinite(fareNum) && fareNum > 0
                     ? `£${fareNum.toFixed(2)}`
                     : (dispatchAskConfirm.fare ? String(dispatchAskConfirm.fare) : "");

                   const etaRaw = dispatchAskConfirm.eta ?? callData?.eta;
                   const etaNum = Number(String(etaRaw ?? "").replace(/[^0-9]/g, ""));
                   const etaText = etaRaw
                     ? String(etaRaw)
                     : (Number.isFinite(etaNum) && etaNum > 0 ? `${etaNum} minutes` : "");

                    const recapText = (finalPickup && finalDestination)
                      ? `Just to confirm, you're going from ${finalPickup} to ${finalDestination} for ${finalPassengers} passenger${finalPassengers === 1 ? "" : "s"}. `
                      : "";

                    const spokenMessage = (fareText && etaText)
                      ? `${recapText}Hi, your price for the journey is ${fareText} and your driver will be ${etaText}. Do you want me to go ahead and book that?`
                      : (dispatchAskConfirm.text || "");

                   // Store pending quote state for confirmation_state flow
                   sessionState.pendingQuote = {
                     fare: dispatchAskConfirm.fare || null,
                     eta: dispatchAskConfirm.eta || null,
                     pickup: finalPickup,
                     destination: finalDestination,
                     callback_url: dispatchAskConfirm.callback_url || null,
                     timestamp: Date.now(),
                     lastPrompt: spokenMessage || null
                   };

                   // ⚠️ DO NOT set lastQuotePromptAt here! Let the broadcast handler set it AFTER
                   // actually injecting the speech. Otherwise the broadcast handler will think
                   // Ada already spoke and skip the actual speech.
                   // sessionState.lastQuotePromptAt = Date.now();
                   // sessionState.lastQuotePromptText = spokenMessage;
                   
                   // ✅ CRITICAL: Clear pendingModification when fare arrives - we've moved past the modification stage
                   if (sessionState.pendingModification) {
                     console.log(`[${sessionState.callId}] 🧹 Clearing pendingModification (polling) - fare quote received`);
                     sessionState.pendingModification = null;
                   }

                   dispatchResult = {
                     needs_fare_confirm: true,
                     ada_message: spokenMessage,
                     fare: dispatchAskConfirm.fare || null,
                     eta: dispatchAskConfirm.eta || null,
                     quote_ready: true,
                   };
                   break;
                 }
                // Check for hangup instruction
                const dispatchHangup = transcripts.find(t =>
                  t.role === "dispatch_hangup" &&
                  new Date(t.timestamp).getTime() > pollStart
                );

                if (dispatchHangup) {
                  console.log(`[${sessionState.callId}] 📞 Dispatch hangup: ${dispatchHangup.text}`);
                  dispatchResult = {
                    hangup: true,
                    ada_message: dispatchHangup.text
                  };
                  break;
                }

                // Check if dispatch sent a general message
                const dispatchSay = transcripts.find(t =>
                  t.role === "dispatch" &&
                  new Date(t.timestamp).getTime() > pollStart
                );

                if (dispatchSay) {
                  console.log(`[${sessionState.callId}] 💬 Dispatch says: ${dispatchSay.text}`);
                  dispatchResult = {
                    ada_message: dispatchSay.text,
                    needs_clarification: true
                  };
                  break;
                }

                // --- REMOVED AUTO-CONFIRM FALLBACK ---
                // Previously, a fresh fare/eta/status update was treated as an implicit confirm.
                // But the C# dispatcher now sends explicit ask_confirm or dispatch_confirm messages
                // via the broadcast channel — so we NO LONGER auto-confirm based on DB fields.
                // This prevents double-fare-prompt races where both the broadcast and the poll
                // independently triggered Ada to speak.
                //
                // If you still need an auto-confirm fallback for old dispatchers that don't
                // broadcast, enable it only if dispatch_ask_confirm was NOT found in transcripts
                // AND pendingQuote is null (no fare was ever sent).

                // Wait before next poll
                await new Promise(resolve => setTimeout(resolve, pollInterval));
              }
              
              // Dispatch polling complete - allow VAD responses again
              sessionState.awaitingDispatchCallback = false;
              
              if (dispatchResult) {
                // Check if dispatch requested hangup - immediately end call
                if (dispatchResult.hangup) {
                  console.log(`[${sessionState.callId}] 📞 Dispatch requested hangup: ${dispatchResult.ada_message}`);
                  
                  // Cancel any active response before injecting system message
                  if (sessionState.openAiResponseActive) {
                    openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
                  }
                  openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                  
                  // Send the goodbye message to Ada to speak
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{ type: "input_text", text: `[SYSTEM: Dispatch has ended this call. Say exactly: "${dispatchResult.ada_message}" then immediately hang up.]` }]
                    }
                  }));
                  
                  // Return hangup result and trigger end_call after speech
                  result = {
                    success: false,
                    hangup: true,
                    ada_message: dispatchResult.ada_message,
                    message: "Dispatch requested call termination - ending call"
                  };
                  
                  // Mark call as ending and schedule cleanup
                  sessionState.callEnded = true;
                  immediateFlush(sessionState);
                  
                  await supabase.from("live_calls")
                    .update({ status: "completed", ended_at: new Date().toISOString() })
                    .eq("call_id", sessionState.callId);
                  
                  // Close after goodbye plays
                  setTimeout(() => {
                    console.log(`[${sessionState.callId}] 🔌 Closing connection after dispatch hangup`);
                    openaiWs?.close();
                  }, 5000);
                  
                  // Trigger response so Ada speaks the goodbye
                  openaiWs?.send(JSON.stringify({ type: "response.create" }));
                  return; // Exit early - don't continue with booking
                }
                
                // Check for fare confirmation request - DON'T confirm booking yet
                if (dispatchResult.needs_fare_confirm) {
                  console.log(`[${sessionState.callId}] 💰 Dispatch needs fare confirmation - NOT confirming booking yet`);
                  result = {
                    success: false,
                    needs_fare_confirm: true,
                    ada_message: dispatchResult.ada_message,
                    message: "Dispatch awaiting fare confirmation from customer"
                  };
                  // pendingFareConfirm was already set when we found the ask_confirm transcript
                  break; // Exit switch - booking NOT confirmed
                }
                
                if (dispatchResult.ada_message && !dispatchResult.confirmed) {
                  console.log(`[${sessionState.callId}] 💬 Dispatch message for Ada: ${dispatchResult.ada_message}`);
                  // Return early with the message - don't confirm booking yet
                  result = {
                    success: false,
                    needs_clarification: true,
                    ada_message: dispatchResult.ada_message,
                    message: "Dispatch needs clarification from customer"
                  };
                  break; // Exit switch - booking NOT confirmed
                }
                
                // Check if dispatch rejected the booking
                if (dispatchResult.rejected || dispatchResult.success === false) {
                  console.log(`[${sessionState.callId}] ❌ Dispatch rejected booking: ${dispatchResult.rejection_reason || 'Unknown reason'}`);
                  result = {
                    success: false,
                    rejected: true,
                    ada_message: dispatchResult.ada_message || dispatchResult.rejection_reason || "Sorry, we can't process this booking right now.",
                    message: "Booking rejected by dispatch"
                  };
                  break;
                }
                
                // Use confirmed fare/eta from dispatch
                if (dispatchResult.confirmed) {
                  fare = dispatchResult.fare || fare;
                  etaMinutes = dispatchResult.eta_minutes || etaMinutes;
                  if (dispatchResult.booking_ref) {
                    bookingRef = dispatchResult.booking_ref;
                  }
                  
                  // If dispatch provided a custom confirmation message, inject it for Ada to speak
                  if (dispatchResult.confirmation_message) {
                    console.log(`[${sessionState.callId}] 🎤 Injecting dispatch confirmation for Ada: "${dispatchResult.confirmation_message}"`);
                    dispatchConfirmationSent = true;
                    
                    // Build the full message with fare and ETA details
                    const fareText = fare ? `Fare is ${fare}` : "";
                    const etaText = etaMinutes ? `arriving in about ${etaMinutes} minutes` : "";
                    const detailsText = [fareText, etaText].filter(Boolean).join(", ");
                    const bookingDetails = detailsText ? ` ${detailsText}.` : "";
                    
                    // ✅ SET discardCurrentResponseAudio BEFORE cancelling to drop any late audio deltas
                    sessionState.discardCurrentResponseAudio = true;
                    
                    // Cancel any active response before injecting system message
                    if (sessionState.openAiResponseActive) {
                      openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
                    }
                    openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                    
                    // Send system message so Ada speaks the dispatch confirmation + details + follow-up question
                    openaiWs?.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [{ type: "input_text", text: `[SYSTEM: Booking confirmed by dispatch. Say this to the customer: "${dispatchResult.confirmation_message}${bookingDetails} Is there anything else I can help you with?"]` }]
                      }
                    }));
                    
                    // Trigger Ada to respond with the message
                    openaiWs?.send(JSON.stringify({ type: "response.create" }));
                  }
                }
              } else {
              console.log(`[${sessionState.callId}] ⏰ Dispatch callback timeout - NOT confirming booking`);

                // Dispatch failed - tell customer to ring back later
                result = {
                  success: false,
                  needs_clarification: true,
                  ada_message: "I'm sorry, we're having some technical issues at the moment. Please ring back a bit later and we'll get that sorted for you.",
                  message: "Dispatch callback timeout"
                };
              }
            } catch (webhookErr) {
              console.error(`[${sessionState.callId}] ⚠️ Dispatch webhook error:`, webhookErr);
              
              // Clear the dispatch guard on error
              sessionState.awaitingDispatchCallback = false;

              // Dispatch failed - tell customer to ring back later
              result = {
                success: false,
                needs_clarification: true,
                ada_message: "I'm sorry, we're having some technical issues at the moment. Please ring back a bit later and we'll get that sorted for you.",
                message: "Dispatch webhook error"
              };
            }
          }
          
          // CRITICAL: Only create booking and mark confirmed if we didn't break early
          // with a clarification/rejection/fare-confirm response
          if (result && (result.needs_clarification || result.needs_fare_confirm || result.rejected || result.hangup)) {
            console.log(`[${sessionState.callId}] ⏸️ NOT confirming booking - awaiting: clarification=${result.needs_clarification}, fare_confirm=${result.needs_fare_confirm}, rejected=${result.rejected}`);
            // Don't create booking in DB yet, don't set bookingConfirmedThisTurn
            break;
          }
          
          // Create/update booking in DB (only if we got here - not rejected/clarification)
          // First, mark any old confirmed/active bookings for this caller as 'completed'
          const callerPhoneNorm = normalizePhone(sessionState.phone);
          const { error: completeOldError } = await supabase
            .from("bookings")
            .update({ status: "completed", completed_at: new Date().toISOString() })
            .eq("caller_phone", callerPhoneNorm)
            .in("status", ["confirmed", "dispatched", "active", "pending"])
            .neq("call_id", sessionState.callId);
          
          if (completeOldError) {
            console.error(`[${sessionState.callId}] Failed to complete old bookings:`, completeOldError);
          } else {
            console.log(`[${sessionState.callId}] ✅ Marked old bookings as completed for ${callerPhoneNorm}`);
          }
          
          // Now upsert the current booking (update if exists for this call_id, else insert)
          const { error: bookingError } = await supabase.from("bookings").upsert({
            call_id: sessionState.callId,
            caller_phone: callerPhoneNorm,
            caller_name: sessionState.customerName,
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers ?? 1,
            fare: fare,
            eta: `${etaMinutes} minutes`,
            status: "confirmed",
            booking_details: { job_id: jobId, booking_ref: bookingRef, distance: distance },
            updated_at: new Date().toISOString()
          }, { onConflict: "call_id" });
          
          if (bookingError) {
            console.error(`[${sessionState.callId}] Booking DB error:`, bookingError);
          }
          
          // If we already injected dispatch message, return minimal result (Ada already speaking)
          result = { 
            success: true, 
            eta_minutes: etaMinutes, 
            fare: fare,
            booking_ref: bookingRef,
            message: dispatchConfirmationSent ? "Booking confirmed - dispatch message sent" : "Booking confirmed"
          };
          
          // ✅ BOOKING ENFORCEMENT: Mark that book_taxi succeeded this turn
          // This allows Ada to say "Booked!" without being cancelled
          sessionState.bookingConfirmedThisTurn = true;
          sessionState.bookingFullyConfirmed = true; // ✅ PERSISTENT: Booking is complete, Ada can speak freely now
          sessionState.lastBookTaxiSuccessAt = Date.now();
          sessionState.lastConfirmedTripKey = makeTripKey(args.pickup, args.destination);
          console.log(`[${sessionState.callId}] ✅ Booking enforcement: book_taxi succeeded, Ada may now speak freely (bookingFullyConfirmed=true)`);
          
          // RELEASE BUFFERED AUDIO: Now that booking is confirmed, flush any pending audio
          sessionState.audioVerified = true;
          if (sessionState.pendingAudioBuffer.length > 0) {
            console.log(`[${sessionState.callId}] 🔊 Releasing ${sessionState.pendingAudioBuffer.length} buffered audio chunks after book_taxi success`);
            for (const audioChunk of sessionState.pendingAudioBuffer) {
              socket.send(JSON.stringify({ type: "audio", audio: audioChunk }));
            }
            sessionState.pendingAudioBuffer = [];
          }
          
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
          
          // Cancel any active response before injecting goodbye
          if (sessionState.openAiResponseActive) {
            openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
          }
          openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          
          // Inject goodbye instruction so Ada says farewell
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: "[SYSTEM: The customer is done. Say a brief, warm goodbye like 'Safe travels!' or 'Take care, bye!' - nothing more. Then stay silent.]" }]
            }
          }));
          
          // Trigger Ada to speak the goodbye
          safeResponseCreate(sessionState, "end_call_goodbye");
          
          // Close OpenAI connection after delay to let goodbye audio finish
          setTimeout(() => {
            console.log(`[${sessionState.callId}] 🔌 Closing OpenAI WebSocket after end_call`);
            openaiWs?.close();
          }, 4000); // 4 second delay for goodbye audio
          
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

          // FULL-DUPLEX echo suppression + barge-in:
          // - If Ada is speaking and we detect REAL user energy, cancel the AI response (barge-in) and then forward audio.
          // - Otherwise, drop audio while Ada is speaking to prevent VAD/Whisper echo hallucinations.
          const pendingQuoteActive = state.pendingQuote && Date.now() - state.pendingQuote.timestamp < 15000;

          if (state.isAdaSpeaking && !state.halfDuplex) {
            if (pendingQuoteActive) {
              // User should answer after the fare is read; dropping during this window prevents echo loops.
              if (Math.random() < 0.01) {
                console.log(`[${state.callId}] 🔇 Dropping audio while speaking (fare window)`);
              }
              return;
            }

            // Skip barge-in checks briefly at the start of Ada speech (startup echo), but still drop audio.
            const inStartupEchoGuard = Date.now() < (state.bargeInIgnoreUntil || 0);
            if (inStartupEchoGuard || !state.openAiResponseActive) {
              return;
            }
            
            // GREETING PROTECTION: Don't allow barge-in during the first 3 seconds of the call
            // This prevents phone line connection noise from cancelling the greeting
            const callAgeMs = Date.now() - state.callStartedAt;
            if (callAgeMs < 3000) {
              // Drop audio silently during greeting protection window
              return;
            }

            let sumSq = 0;
            for (let i = 0; i < pcmInput.length; i++) {
              const s = pcmInput[i];
              sumSq += s * s;
            }
            const rms = Math.sqrt(sumSq / Math.max(1, pcmInput.length));

            // Real barge-in: moderate RMS (not clipped echo which is >20000, not quiet noise which is <1000)
            // Raised threshold from 650 to 1000 to reduce false barge-ins from phone line noise
            const isRealBargeIn = rms >= 1000 && rms < 20000;

            if (isRealBargeIn) {
              console.log(`[${state.callId}] 🛑 Barge-in detected (rms=${rms.toFixed(0)}) - cancelling AI speech`);
              try {
                openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              } catch (e) {
                console.error(`[${state.callId}] ❌ Failed to cancel on barge-in:`, e);
              }

              // Allow audio to flow immediately after cancelling.
              state.isAdaSpeaking = false;
              state.echoGuardUntil = Date.now() + 200;
              socket.send(JSON.stringify({ type: "ai_interrupted" }));
            } else {
              // Not a real barge-in → drop to prevent echo.
              return;
            }
          }

          // Step 2: Upsample to 24kHz (OpenAI Realtime API requirement)
          // Handle different input sample rates: 8kHz (ulaw/slin) or 16kHz (slin16)
          let pcm16_24k: Int16Array;
          
          if (inputSampleRate === 16000) {
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
          gpsLat: null,
          gpsLon: null,
          gpsRequired: message.gps_required ?? false,
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
          lastSpokenQuestion: null,
          lastSpokenQuestionAt: null,
          lastMultiQuestionFixAt: null,
          greetingDelivered: false,
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
        const isReconnect = message.reconnect === true;
        
        console.log(`[${callId}] 🚀 Initializing simple session (reconnect=${isReconnect}, preConnected=${preConnected})`);

        // If this is a reconnect attempt, reject it - simple mode doesn't support resumption
        if (isReconnect) {
          console.log(`[${callId}] ❌ Simple mode does not support reconnection`);
          socket.close(1000, "Session expired");
          return;
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
            gpsLat: null,
            gpsLon: null,
            gpsRequired: message.gps_required ?? false,
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
            lastSpokenQuestion: null,
            lastSpokenQuestionAt: null,
            lastMultiQuestionFixAt: null,
            greetingDelivered: false,
          };
        }
        
        // Also update settings from init message if pre-connected
        if (state && preConnected) {
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
            
            // Process active booking - THIS MUST HAPPEN BEFORE GREETING
            if (bookingResult.data) {
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

        socket.send(JSON.stringify({ 
          type: "ready", 
          call_id: callId,
          mode: "simple"
        }));
        
        // Start keep-alive pings to prevent WebSocket timeout during dispatch callback wait
        startKeepAlive(callId);

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
          const recapPickup = state?.booking?.pickup || "";
          const recapDest = state?.booking?.destination || "";
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
              callback_url: callback_url || null,
              timestamp: Date.now(),
              lastPrompt: spokenMessage || null
            };

            // Mark that we are actively prompting the fare question (prevents tool handler duplicating it)
            state.lastQuotePromptAt = Date.now();
            state.lastQuotePromptText = spokenMessage;
            
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

                // Keep a copy in session state for context
                state.booking.pickup = bookingData.pickup;
                state.booking.destination = bookingData.destination;
                state.booking.passengers = bookingData.passengers;
                // Mark as modifiable (this came from an existing booking)
                state.booking.version = Math.max(state.booking.version || 0, 1);

                console.log(
                  `[${callId}] ✅ Active booking loaded: ${bookingData.pickup} -> ${bookingData.destination} (${bookingData.passengers})`
                );

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

            // Lookup GPS first - this determines if we can proceed
            if (state?.gpsRequired && phone && phone !== "unknown") {
              // Normalize phone for GPS lookup
              let normalizedPhone = phone.replace(/\s+/g, "").replace(/-/g, "");
              if (!normalizedPhone.startsWith("+") && normalizedPhone.length >= 10) {
                if (normalizedPhone.startsWith("00")) {
                  normalizedPhone = "+" + normalizedPhone.slice(2);
                } else if (/^(44|1|33|49|31)\d+$/.test(normalizedPhone)) {
                  normalizedPhone = "+" + normalizedPhone;
                }
              }

              // Check caller_gps table for pre-submitted GPS
              const { data: gpsData } = await supabase
                .from("caller_gps")
                .select("lat, lon, expires_at")
                .eq("phone_number", normalizedPhone)
                .gte("expires_at", new Date().toISOString())
                .order("created_at", { ascending: false })
                .limit(1)
                .maybeSingle();

              if (gpsData && state) {
                state.gpsLat = gpsData.lat;
                state.gpsLon = gpsData.lon;
                console.log(`[${callId}] 📍 GPS loaded: ${gpsData.lat}, ${gpsData.lon}`);
            } else {
                // NO GPS found - ALWAYS ask Ada to request location (not just when required)
                console.log(`[${callId}] ⚠️ GPS not found - Ada will ask for location`);

                // Send system message to trigger location request flow
                if (openaiWs && openaiConnected) {
                  // Inject a system message that triggers Ada to ask for location
                  openaiWs.send(
                    JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [{ type: "input_text", text: "[SYSTEM: GPS not available]" }],
                      },
                    })
                  );
                  // Note: Don't create response here - let the normal greeting flow handle it
                  // Ada will see the system message and ask for location per the prompt
                }

                // Notify bridge
                socket.send(
                  JSON.stringify({
                    type: "gps_missing",
                    message: "GPS location not received - Ada will ask for location",
                  })
                );
              }
            }
            
            // Also check GPS for non-gps_required calls - ALWAYS try to get location
            if (!state?.gpsRequired && phone && phone !== "unknown" && !state?.gpsLat) {
              // Normalize phone for GPS lookup
              let normalizedPhone = phone.replace(/\s+/g, "").replace(/-/g, "");
              if (!normalizedPhone.startsWith("+") && normalizedPhone.length >= 10) {
                if (normalizedPhone.startsWith("00")) {
                  normalizedPhone = "+" + normalizedPhone.slice(2);
                } else if (/^(44|1|33|49|31)\d+$/.test(normalizedPhone)) {
                  normalizedPhone = "+" + normalizedPhone;
                }
              }

              // Check caller_gps table for pre-submitted GPS
              const { data: gpsData2 } = await supabase
                .from("caller_gps")
                .select("lat, lon, expires_at")
                .eq("phone_number", normalizedPhone)
                .gte("expires_at", new Date().toISOString())
                .order("created_at", { ascending: false })
                .limit(1)
                .maybeSingle();

              if (gpsData2 && state) {
                state.gpsLat = gpsData2.lat;
                state.gpsLon = gpsData2.lon;
                console.log(`[${callId}] 📍 GPS loaded (non-required): ${gpsData2.lat}, ${gpsData2.lon}`);
              } else if (!state?.gpsLat) {
                // NO GPS - ask Ada to request location
                console.log(`[${callId}] ⚠️ GPS not found (non-required) - Ada will ask for location`);

                if (openaiWs && openaiConnected) {
                  openaiWs.send(
                    JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [{ type: "input_text", text: "[SYSTEM: GPS not available]" }],
                      },
                    })
                  );
                }

                socket.send(
                  JSON.stringify({
                    type: "gps_missing",
                    message: "GPS location not received - Ada will ask for location",
                  })
                );
              }
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
                if (!activeBooking) {
                  // Try both phone formats (normalized digits-only + legacy no-plus)
                  const { data: bookingData } = await supabase
                    .from("bookings")
                    .select("call_id, pickup, destination, passengers, fare, eta, status, booked_at, updated_at, caller_name")
                    .or(`caller_phone.eq.${phoneKey},caller_phone.eq.${altPhone}`)
                    .in("status", ["confirmed", "dispatched", "active", "pending"])
                    .order("updated_at", { ascending: false })
                    .order("booked_at", { ascending: false })
                    .limit(1)
                    .maybeSingle();

                  if (bookingData && state) {
                    activeBooking = bookingData;
                    state.hasActiveBooking = true;
                    state.activeBookingCallId = bookingData.call_id; // Store original booking's call_id for updates
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
                  gps_lat: state?.gpsLat,
                  gps_lon: state?.gpsLon,
                  gps_updated_at: state?.gpsLat ? new Date().toISOString() : null,
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
                    gps_lat: state?.gpsLat,
                    gps_lon: state?.gpsLon,
                    gps_updated_at: state?.gpsLat ? new Date().toISOString() : null,

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

      // Handle audio format update from bridge (ulaw, slin, slin16)
      if (message.type === "update_format" && state) {
        const newFormat = message.inbound_format as "ulaw" | "slin" | "slin16";
        const newRate = message.inbound_sample_rate || (newFormat === "slin16" ? 16000 : 8000);
        
        if (newFormat && newFormat !== state.inboundAudioFormat) {
          console.log(`[${state.callId}] 🔊 Audio format update: ${state.inboundAudioFormat}@${state.inboundSampleRate}Hz → ${newFormat}@${newRate}Hz`);
          state.inboundAudioFormat = newFormat;
          state.inboundSampleRate = newRate;
        }
        return;
      }

      // Handle GPS update during call
      if (message.type === "gps_update" && state) {
        const lat = message.lat || message.latitude;
        const lon = message.lon || message.longitude;
        
        if (lat && lon) {
          state.gpsLat = lat;
          state.gpsLon = lon;
          console.log(`[${state.callId}] 📍 GPS updated mid-call: ${lat}, ${lon}`);
          
          // Update live_calls with GPS
          supabase.from("live_calls").update({
            gps_lat: lat,
            gps_lon: lon,
            gps_updated_at: new Date().toISOString()
          }).eq("call_id", state.callId)
            .then(() => console.log(`[${state?.callId}] ✅ GPS saved to DB`));
          
          socket.send(JSON.stringify({
            type: "gps_received",
            lat,
            lon
          }));
        }
        return;
      }

      if (message.type === "audio" && openaiConnected && openaiWs && state) {
        // ECHO GUARD: Always ignore audio for a short window after Ada finishes speaking.
        if (Date.now() < state.echoGuardUntil) {
          return;
        }
        
        // Bridge sends base64-encoded 8kHz µ-law audio via JSON
        const binaryStr = atob(message.audio);
        const audioData = new Uint8Array(binaryStr.length);
        for (let i = 0; i < binaryStr.length; i++) {
          audioData[i] = binaryStr.charCodeAt(i);
        }
        
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

        // BARGE-IN: If Ada is currently speaking and we detect real user energy,
        // cancel the current AI response immediately so Ada can hear the caller.
        // IMPORTANT: We must NEVER drop audio - only decide whether to trigger barge-in.
        if (state.isAdaSpeaking && state.openAiResponseActive) {
          // Skip barge-in check during initial echo guard, but DON'T drop audio
          const inEchoGuard = Date.now() < (state.bargeInIgnoreUntil || 0);
          
          if (!inEchoGuard) {
            let sumSq = 0;
            for (let i = 0; i < pcm16_8k.length; i++) {
              const s = pcm16_8k[i];
              sumSq += s * s;
            }
            const rms = Math.sqrt(sumSq / Math.max(1, pcm16_8k.length));

            // Real barge-in: moderate RMS (not clipped echo which is >20000, not quiet noise which is <650)
            const isRealBargeIn = rms >= 650 && rms < 20000;

            if (isRealBargeIn) {
              console.log(`[${state.callId}] 🛑 Barge-in detected (rms=${rms.toFixed(0)}) - cancelling AI speech`);
              try {
                openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              } catch (e) {
                console.error(`[${state.callId}] ❌ Failed to cancel on barge-in:`, e);
              }

              state.isAdaSpeaking = false;
              state.echoGuardUntil = Date.now() + 200;
              socket.send(JSON.stringify({ type: "ai_interrupted" }));
            }
            // If not real barge-in, we just skip cancellation but STILL forward audio below
          }
        }
        
        // Step 2: Upsample 8kHz -> 24kHz (3x linear interpolation)
        // OpenAI Realtime API requires 24kHz PCM16
        const pcm16_24k_json = new Int16Array(pcm16_8k.length * 3);
        for (let i = 0; i < pcm16_8k.length - 1; i++) {
          const s0 = pcm16_8k[i];
          const s1 = pcm16_8k[i + 1];
          pcm16_24k_json[i * 3] = s0;
          pcm16_24k_json[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
          pcm16_24k_json[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
        }
        // Handle last sample
        const lastIdxJson = pcm16_8k.length - 1;
        pcm16_24k_json[lastIdxJson * 3] = pcm16_8k[lastIdxJson];
        pcm16_24k_json[lastIdxJson * 3 + 1] = pcm16_8k[lastIdxJson];
        pcm16_24k_json[lastIdxJson * 3 + 2] = pcm16_8k[lastIdxJson];
        
        // Step 3: Convert to base64
        const bytesJson = new Uint8Array(pcm16_24k_json.buffer);
        let binaryJson = "";
        for (let i = 0; i < bytesJson.length; i++) {
          binaryJson += String.fromCharCode(bytesJson[i]);
        }
        const base64AudioJson = btoa(binaryJson);
        
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: base64AudioJson
        }));
      }

    } catch (e) {
      console.error("[simple] Message error:", e);
    }
  };

  socket.onclose = async () => {
    console.log(`[${state?.callId || "unknown"}] Client disconnected`);
    
    // Mark connection as closed to prevent further operations
    isConnectionClosed = true;
    
    // Stop keep-alive pings
    stopKeepAlive();
    
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
