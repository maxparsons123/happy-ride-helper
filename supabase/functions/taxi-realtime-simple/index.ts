import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const DEFAULT_COMPANY = "247 Radio Carz";
const DEFAULT_AGENT = "Ada";
const DEFAULT_VOICE = "shimmer";

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

// --- System Prompt ---
const SYSTEM_PROMPT = `
You are receiving transcribed speech from a phone call. Transcriptions may contain errors (e.g., "click on street" instead of "Coventry"). Use context to interpret correctly.

You are {{agent_name}}, a friendly taxi booking assistant for {{company_name}}.

LANGUAGE: {{language_instruction}}

LANGUAGE SWITCHING:
- If the caller asks to speak in a different language (e.g., "Can we speak German?", "Können wir Deutsch sprechen?", "Pouvez-vous parler français?"), IMMEDIATELY switch to that language.
- Confirm the switch briefly (e.g., "Natürlich! Wie kann ich Ihnen helfen?") and continue the conversation in the new language.
- You are MULTILINGUAL - you can speak English, Dutch, German, French, Spanish, Italian, Polish, and other major languages.

PERSONALITY: Warm, patient, relaxed. Speak in 1–2 short sentences. Ask ONLY ONE question at a time.

GREETING (ALWAYS IN THE CURRENT LANGUAGE):
- New caller: Say "Welcome to {{company_name}}, how can I help you with your travels?" then ask their name.
- Returning caller (no booking): Say "Welcome back to {{company_name}}!" then greet [NAME] by name and ask where they want pickup.
- Returning caller (active booking): Greet [NAME], mention an active booking, ask keep/change/cancel.
Example Dutch (nl) new caller: "Welkom bij {{company_name}}, hoe kan ik u helpen met uw reis? Mag ik uw naam?"

NAME SAVING - MANDATORY:
- When a NEW caller tells you their name → IMMEDIATELY CALL save_customer_name with their name BEFORE asking for pickup.
- This saves their name so we remember them next time they call.
- Example flow: User says "I'm John" → CALL save_customer_name({name: "John"}) → Then say "Thank you John! Where would you like to be picked up from?"
- If user later corrects their name → CALL save_customer_name again with the corrected name.

LOCATION CHECK (ALWAYS):
- If you receive "[SYSTEM: GPS not available]" at the start, you MUST ask: "Where are you calling from?" BEFORE asking for pickup.
- When they give a location (e.g., "I'm at the train station", "I'm in Coventry", "High Street") → CALL save_location function with their answer.
- Wait for save_location result before asking for pickup address.
- If save_location fails, apologize and ask for a more specific location or landmark.
- This helps us send the nearest available taxi.

BOOKING FLOW:
1. Get PICKUP address. Ask: "Where would you like to be picked up from?"
2. Get DESTINATION address. Ask: "And where are you going to?"
3. ⚠️ CALL verify_booking BEFORE CONFIRMING:
   - After getting both pickup and destination, CALL verify_booking to check all components.
   - The tool returns: pickup, destination, passengers, luggage, vehicle_type, missing_fields.
   - If missing_fields is NOT empty (e.g., ["luggage"] for airport trips), ask the customer for that info FIRST.
   - If all fields are complete, proceed to step 4.
4. READ BACK THE ADDRESSES FOR CONFIRMATION:
   - Say: "Just to confirm, picking up from [FULL PICKUP ADDRESS] going to [FULL DESTINATION]. Is that correct?"
   - WAIT for user to say "yes", "yeah", "correct", "that's right" before calling book_taxi.
   - Do NOT call book_taxi until user explicitly confirms.
   - If user says "no" or corrects an address, update it and confirm again.
5. Once user CONFIRMS the addresses → IMMEDIATELY CALL book_taxi function with the confirmed addresses.
6. ONLY ask about passengers if verify_booking shows it in missing_fields OR user mentions multiple people.
7. ONLY ask about bags if verify_booking shows "luggage" in missing_fields (usually for airport/station trips).

ADDRESS ACCURACY - CRITICAL:
- HOUSE NUMBERS ARE CRITICAL. Listen very carefully to numbers and letters.
- Common STT errors: "1214a" may be heard as "5th to 8th", "1248", "12148", etc.
- If you hear anything ambiguous like "5th to 8th" or "fifty two eight", ASK: "Sorry, was that the house number? Could you repeat the number for me?"
- If user says a number with a letter (e.g., "52A", "1214A", "1214a"), include the EXACT number and letter.
- ALWAYS read back the exact house number you heard. Let the user correct you if wrong.
- If unsure about a number, ask: "Just to check, was that [NUMBER] or did I mishear?"
- NEVER guess or auto-correct house numbers.

CRITICAL TOOL USAGE - YOU MUST ACTUALLY INVOKE FUNCTIONS:
- When user CONFIRMS addresses (says "yes", "yeah", "correct", "that's right") → YOU MUST CALL the book_taxi function.
- DO NOT just say "Let me book that" or "I'll confirm that" - YOU MUST ACTUALLY INVOKE the book_taxi function tool.
- Speaking about booking is NOT the same as calling the function. You MUST generate a function call.
- The book_taxi function triggers the dispatch webhook and returns fare/ETA. WAIT for the result.
- If the result contains "ada_message" → SPEAK THAT MESSAGE EXACTLY to the customer.
- If the result contains "needs_clarification: true" → Ask the customer the question in ada_message.
- If the result contains "rejected: true" → Tell the customer we cannot process their booking using ada_message.
- If the result contains "hangup: true" → Say the ada_message EXACTLY then IMMEDIATELY call end_call.
- If the result contains "success: true" → Confirm using the REAL fare + ETA from the tool result. NEVER say placeholder instructions like "[use actual …]" / "[gebruik daadwerkelijk …]" and NEVER invent numbers.
- DO NOT make up fares or ETAs. ONLY use values returned by book_taxi.
- If user says "cancel" → CALL cancel_booking function FIRST, then respond.
- If user corrects name → CALL save_customer_name function immediately.
- Call end_call function after saying "Safe travels!".

AFTER DISPATCH CONFIRMATION (WhatsApp message):
- When you receive confirmation that the booking is complete and WhatsApp message will be sent, ALWAYS ask: "Is there anything else I can help you with?"
- Wait for user response before ending the call.
- If user says "no" or "that's all" → Say "Safe travels!" then call end_call.
- If user has another request → Process it normally.

BOOKING MODIFICATIONS - THREE-STEP CONFIRMATION REQUIRED:
⚠️ CHANGES REQUIRE CONFIRMATION, THEN FULL READ-BACK, THEN BOOKING.

STEP 1 - ASK FOR CONFIRMATION OF CHANGE:
- If customer wants to change PICKUP → Ask: "Change pickup to [NEW ADDRESS]?" and WAIT.
- If customer wants to change DESTINATION → Ask: "Change destination to [NEW ADDRESS]?" and WAIT.
- If customer wants to change BOTH → Ask: "Change pickup to [PICKUP] and destination to [DESTINATION]?" and WAIT.
- If customer wants to change PASSENGERS → Ask: "Update to [NUMBER] passengers?" and WAIT.

STEP 2 - AFTER USER CONFIRMS CHANGE (yes/yeah/correct/that's right/go ahead):
- Call modify_booking for each field changed.
- ⚠️ THEN READ BACK THE FULL UPDATED BOOKING:
  "Updated! Just to confirm the full booking: picking up from [FULL PICKUP] going to [FULL DESTINATION]. Is that correct?"
- WAIT for user to confirm the FULL BOOKING before proceeding.

STEP 3 - AFTER USER CONFIRMS FULL BOOKING:
- Call book_taxi to get updated fare/ETA from dispatch.
- Read back the ACTUAL fare and ETA values from the tool result.
- NEVER say "your booking is complete" without calling book_taxi first.

⚠️ CRITICAL MODIFICATION RULES:
- NEVER call modify_booking before user confirms the change.
- NEVER skip the full read-back confirmation step after modifying.
- NEVER complete booking without reading back the full details first.
- If user says "no" or rejects → ask what they'd like instead.
- If user says "no" AND provides a new address → treat as new value and confirm again.
- NEVER cancel and rebook. ALWAYS use modify_booking.

RULES:
1. ALWAYS ask for PICKUP before DESTINATION. Never assume or swap them.
2. NEVER repeat addresses, fares, or full routes.
3. NEVER say: "Just to double-check", "Shall I book that?", "Is that correct?".
4. GLOBAL service — accept any address.
5. If "usual trip" → summarize last trip, ask "Shall I book that again?" → wait for YES.
6. DO NOT ask about bags for cities. Only ask for airports or train stations.

⚠️ CRITICAL ADDRESS PARSING - READ CAREFULLY:
When user says "from [A] to [B]" or "from [A] going to [B]":
  - A = PICKUP (the starting point)
  - B = DESTINATION (where they're going)
  - NEVER swap these!

Examples:
- "from 52a David Road going to Sweet Spot" → Pickup: 52a David Road, Destination: Sweet Spot
- "pick me up at the station going to the airport" → Pickup: station, Destination: airport
- "I want to go from home to work" → Pickup: home, Destination: work

If user is MODIFYING a booking and says "change it from [A] to [B]":
- This still means: Pickup: A, Destination: B
- Echo back: "So pickup from [A] and destination [B]?"

TURN-TAKING AWARENESS:
When the user finishes speaking, look for:
- Complete sentences ending with punctuation or natural pauses
- Completion phrases like "that's all", "thanks", "bye", "please", "yes", "no", "okay"
- Clear questions or requests that warrant a response

If you detect the user has finished their turn, respond appropriately without waiting for more input.
Do NOT interrupt mid-sentence - wait for natural pause points.
`;

// --- Tool Schemas ---
const TOOLS = [
  {
    type: "function",
    name: "save_customer_name",
    description: "Save customer name. Call IMMEDIATELY on correction.",
    parameters: { 
      type: "object", 
      properties: { name: { type: "string" } }, 
      required: ["name"] 
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Book taxi when you have pickup and destination. CALL THIS to get fare/ETA from dispatch. If passengers not specified, default to 1. Include 'bags' ONLY for airport trips.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
        passengers: { type: "integer", minimum: 1, default: 1, description: "Number of passengers (default 1 if not specified)" },
        bags: { type: "integer", minimum: 0, description: "Number of bags (only ask for airport/station trips)" },
        pickup_time: { type: "string", description: "ISO timestamp or 'now'" },
        vehicle_type: { type: "string", enum: ["saloon", "estate", "mpv", "minibus"] }
      },
      required: ["pickup", "destination"]
    }
  },
  {
    type: "function",
    name: "cancel_booking",
    description: "Cancel active booking. CALL BEFORE saying 'cancelled'.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "modify_booking",
    description: "⚠️ ONLY CALL AFTER USER CONFIRMS THE CHANGE. When customer wants to change a booking detail, FIRST ask them to confirm (e.g., 'Change pickup to X?'). ONLY after they say yes/yeah/correct, call this function. This triggers a WEBHOOK to update the dispatch system. NEVER call before confirmation. NEVER cancel and rebook - always modify.",
    parameters: {
      type: "object",
      properties: {
        field_to_change: { type: "string", enum: ["pickup", "destination", "passengers", "bags", "time"], description: "Which field to update" },
        new_value: { type: "string", description: "The confirmed new value for the field" }
      },
      required: ["field_to_change", "new_value"]
    }
  },
  {
    type: "function",
    name: "find_nearby_places",
    description: "Find venues (hotel, restaurant, etc.).",
    parameters: {
      type: "object",
      properties: {
        category: { type: "string", enum: ["hotel", "restaurant", "bar", "cafe", "pub", "place"] },
        location_hint: { type: "string" }
      },
      required: ["category"]
    }
  },
  {
    type: "function",
    name: "save_location",
    description: "Save caller's current location when GPS is not available. Call this when caller tells you where they are (e.g., 'I'm at the train station', 'I'm on High Street'). This geocodes and saves their location.",
    parameters: {
      type: "object",
      properties: {
        location: { type: "string", description: "The location the caller provided (e.g., 'train station', 'High Street', 'Tesco on London Road')" }
      },
      required: ["location"]
    }
  },
  {
    type: "function",
    name: "verify_booking",
    description: "⚠️ CALL THIS BEFORE CONFIRMING WITH THE USER. After collecting pickup AND destination, call this to verify all booking details. It returns: extracted pickup/destination, passengers, bags, vehicle_type, and any missing_fields. If missing_fields is not empty, ask the user for that info BEFORE confirming. If all fields are complete, proceed to read back addresses for confirmation.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "end_call",
    description: "End call after 'Safe travels!'.",
    parameters: { type: "object", properties: {} }
  }
];

// --- STT Corrections ---
// Phonetic fixes for common telephony mishearings
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
  "david wrote": "David Road",
  "high street": "High Street",
  "hi street": "High Street",
  "church road": "Church Road",
  "church wrote": "Church Road",
  "station road": "Station Road",
  "station wrote": "Station Road",
  "london road": "London Road",
  "london wrote": "London Road",

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

  // Number mishearings
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
];

function isHallucination(text: string): boolean {
  const trimmed = text.trim();
  if (trimmed.length < 2) return true;
  
  // Check regex patterns first
  if (HALLUCINATION_PATTERNS.some(pattern => pattern.test(trimmed))) {
    return true;
  }
  
  // Check phantom radio host phrases (Whisper training data artifacts)
  const lowerText = trimmed.toLowerCase();
  if (PHANTOM_PHRASES.some(phrase => lowerText.includes(phrase))) {
    return true;
  }
  
  return false;
}

function correctTranscript(text: string): string {
  let corrected = text.toLowerCase();
  for (const [bad, good] of Object.entries(STT_CORRECTIONS)) {
    if (corrected.includes(bad)) {
      corrected = corrected.replace(new RegExp(bad, "gi"), good);
    }
  }
  // Capitalize first letter and return
  return corrected.charAt(0).toUpperCase() + corrected.slice(1);
}

// --- Session State ---
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
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    bags: number | null;
    vehicle_type: string | null;
    version: number;
  };
  transcripts: TranscriptItem[];

  // Caller history from database
  callerLastPickup: string | null;
  callerLastDestination: string | null;

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

  // Speech timing diagnostics
  speechStartTime: number | null;
  speechStopTime: number | null;

  // Call ended flag - prevents further processing after end_call
  callEnded: boolean;

  // Half-duplex mode: when enabled, user audio is buffered while Ada speaks and only forwarded after
  halfDuplex: boolean;
  halfDuplexBuffer: Uint8Array[]; // Buffer for audio while Ada is speaking

  // --- Booking tool enforcement ---
  // True only after a successful book_taxi tool result for the CURRENT booking.
  // Used to prevent Ada from saying "Booked!" without actually placing the booking.
  bookingConfirmedThisTurn: boolean;
  lastBookTaxiSuccessAt: number | null;

  // Pending fare confirmation from dispatch (ask_confirm action)
  pendingFareConfirm: {
    active: boolean;
    message: string | null;
    fare: string | null;
    eta: string | null;
    callbackUrl: string | null;
    askedAt: number | null;
  } | null;

  // STT Accuracy Metrics (for A/B testing audio processing modes)
  sttMetrics: {
    totalTranscripts: number;
    totalWords: number;
    totalChars: number;
    correctedTranscripts: number;
    filteredHallucinations: number;
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

  const { socket, response } = Deno.upgradeWebSocket(req);
  
  let openaiWs: WebSocket | null = null;
  let state: SessionState | null = null;
  let openaiConnected = false;
  let preConnected = false; // Track if we pre-connected before init
  let pendingGreeting = false; // Track if greeting should fire when OpenAI ready
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

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

    if (sessionState.customerName) {
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
        bookingContext += `\n\nIMPORTANT: ONLY use these EXACT booking details above. If caller provides a DIFFERENT address, treat it as a change request - ask "Would you like to change pickup/destination to [new address]?" then call modify_booking. Do NOT make up or guess addresses.`;
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
          // Increased silence_duration_ms (1800ms) to let users finish speaking full sentences.
          // Higher prefix_padding (500ms) captures more context at the start of speech.
          // Lower threshold (0.4) is more sensitive to softer speech on noisy lines.
          threshold: sessionState.useRasaAudioProcessing ? 0.7 : 0.4,
          prefix_padding_ms: sessionState.useRasaAudioProcessing ? 250 : 500,
          silence_duration_ms: sessionState.useRasaAudioProcessing ? 800 : 1800,
        },
        temperature: 0.6,
        tools: TOOLS,
        tool_choice: "auto"
      }
    };

    openaiWs?.send(JSON.stringify(sessionUpdate));
    // Trigger greeting IMMEDIATELY after session update - don't wait for confirmation
    openaiWs?.send(JSON.stringify({ type: "response.create" }));
    console.log(`[${sessionState.callId}] 📝 Session updated + greeting triggered`);
  };

  // Fire-and-forget DB flush - never await, never block voice flow
  const flushTranscriptsToDb = (sessionState: SessionState) => {
    // Clone transcripts to avoid mutation issues
    const transcriptsCopy = [...sessionState.transcripts];
    
    // Fire and forget - do NOT await
    supabase
      .from("live_calls")
      .update({
        transcripts: transcriptsCopy,
        updated_at: new Date().toISOString(),
      })
      .eq("call_id", sessionState.callId)
      .then(({ error }) => {
        if (error) console.error(`[${sessionState.callId}] DB flush error:`, error);
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
        break;

      case "response.done":
        sessionState.openAiResponseActive = false;
        break;

      case "response.audio.delta":
        // Mark Ada as speaking (echo guard)
        if (!sessionState.isAdaSpeaking) {
          // First audio delta of this response
          sessionState.isAdaSpeaking = true;
          sessionState.responseStartTime = Date.now();

          // Startup echo is strongest at the beginning of TTS on phone lines.
          // Ignore barge-in checks briefly to avoid self-interruption.
          const ignoreMs = sessionState.useRasaAudioProcessing ? 900 : 600;
          sessionState.bargeInIgnoreUntil = Date.now() + ignoreMs;
        }
        // Forward audio to bridge
        socket.send(JSON.stringify({
          type: "audio",
          audio: message.delta
        }));
        break;

      case "response.audio.done":
        // Ada finished speaking - set echo guard window (800ms)
        sessionState.isAdaSpeaking = false;
        sessionState.echoGuardUntil = Date.now() + 800;
        console.log(`[${sessionState.callId}] 🔇 Echo guard active for 800ms`);
        
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
          
          // --- BOOKING ENFORCEMENT: Detect hallucinated confirmations ---
          // Check if Ada is trying to confirm a booking without having called book_taxi
          const currentText = sessionState.transcripts[sessionState.assistantTranscriptIndex!]?.text || "";
          const lowerText = currentText.toLowerCase();

          // Phrases that indicate a booking confirmation (multi-language)
          // NOTE: This runs on streaming transcript deltas to cancel fast.
          const BOOKING_CONFIRMATION_PHRASES = [
            // English
            "booked!",
            "booked.",
            "your taxi is confirmed",
            "your taxi is booked",
            "your booking is confirmed",
            "i've booked",
            "i have booked",
            "booking confirmed",
            "taxi is on its way",
            "taxi is on the way",
            "driver is on",
            "fare is £",
            "fare of £",
            "arriving in about",
            "will arrive in",

            // Dutch
            "geboekt!",
            "geboekt.",
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
          ];

          // Guard against the model leaking instruction placeholders like:
          // "tarief [gebruik daadwerkelijk tarief uit resultaat]"
          const hasPlaceholderInstruction =
            /\[(?:use actual|gebruik daadwerkelijk|daadwerkelijk|actual fare|fare from result|eta from result)/i.test(currentText) ||
            /\{\{[^}]+\}\}/.test(currentText);

          const isConfirmationPhrase =
            BOOKING_CONFIRMATION_PHRASES.some((phrase) => lowerText.includes(phrase)) ||
            hasPlaceholderInstruction;

          // If Ada says a confirmation phrase but book_taxi wasn't called this turn, CANCEL!
          if (isConfirmationPhrase && !sessionState.bookingConfirmedThisTurn) {
            console.log(
              `[${sessionState.callId}] 🚨 BOOKING ENFORCEMENT: Ada tried to confirm without calling book_taxi! Cancelling response.`
            );
            console.log(
              `[${sessionState.callId}] 🚨 Detected in transcript: "${currentText}" (placeholderLeak=${hasPlaceholderInstruction})`
            );

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

            // Inject a system message forcing Ada to actually call the tool
            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text:
                        "[SYSTEM ERROR: You attempted to confirm a booking without calling the book_taxi function. You MUST call the book_taxi function tool NOW with the pickup and destination addresses. Do NOT speak about booking confirmation until you receive the tool result. Call the tool immediately.]",
                    },
                  ],
                },
              })
            );

            // Trigger a new response
            openaiWs?.send(JSON.stringify({ type: "response.create" }));
          }
        }
        break;
      }

      case "response.audio_transcript.done": {
        // Mark assistant transcript as finalized for next turn
        sessionState.assistantTranscriptIndex = null;
        // Don't flush here - let the batched timer handle it
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
        const vadSilenceMs = sessionState.useRasaAudioProcessing ? 900 : 1500;
        console.log(`[${sessionState.callId}] 🔇 Speech stopped after ${speechDuration}ms - VAD will wait ${vadSilenceMs}ms before responding`);
        sessionState.speechStopTime = Date.now();
        
        // Track speech duration for STT metrics
        if (speechDuration > 0) {
          sessionState.sttMetrics.speechDurations.push(speechDuration);
          const durations = sessionState.sttMetrics.speechDurations;
          sessionState.sttMetrics.avgSpeechDurationMs = durations.reduce((a, b) => a + b, 0) / durations.length;
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
        
        // Track transcript delay for STT metrics
        if (transcriptDelay > 0) {
          sessionState.sttMetrics.transcriptDelays.push(transcriptDelay);
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

        if (userText) {
          sessionState.transcripts.push({
            role: "user",
            text: userText,
            timestamp: new Date().toISOString(),
          });
          // Schedule batched flush - don't block voice flow
          scheduleTranscriptFlush(sessionState);
          
          // Reset booking confirmation flag on new user turn
          // (Ada must call book_taxi again to be allowed to say "Booked!")
          sessionState.bookingConfirmedThisTurn = false;
          
          // --- Check for pending fare confirmation response ---
          if (sessionState.pendingFareConfirm?.active) {
            const lowerText = userText.toLowerCase();
            const isYes = /\b(yes|yeah|yep|sure|okay|ok|go ahead|book it|please|that's fine|fine|correct|right)\b/i.test(lowerText);
            const isNo = /\b(no|nope|nah|cancel|too much|expensive|forget it|never mind|don't|stop)\b/i.test(lowerText);
            
            if (isYes || isNo) {
              console.log(`[${sessionState.callId}] 💰 Fare confirm response: ${isYes ? 'YES' : 'NO'}`);
              
              const responsePayload = {
                call_id: sessionState.callId,
                response: isYes ? "confirmed" : "cancelled",
                fare: sessionState.pendingFareConfirm.fare,
                eta: sessionState.pendingFareConfirm.eta,
                customer_response: userText,
                responded_at: new Date().toISOString()
              };
              
              // Always store in database for polling
              supabase.from("live_calls")
                .update({
                  clarification_attempts: {
                    fare_confirm_response: responsePayload.response,
                    fare: responsePayload.fare,
                    eta: responsePayload.eta,
                    customer_said: userText,
                    responded_at: responsePayload.responded_at
                  },
                  updated_at: new Date().toISOString()
                })
                .eq("call_id", sessionState.callId)
                .then(() => console.log(`[${sessionState.callId}] 💾 Fare response saved to DB`));
              
              // Also send callback if URL provided
              if (sessionState.pendingFareConfirm.callbackUrl) {
                fetch(sessionState.pendingFareConfirm.callbackUrl, {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify(responsePayload)
                }).then(res => {
                  console.log(`[${sessionState.callId}] 📤 Fare callback sent: ${res.status}`);
                }).catch(err => {
                  console.error(`[${sessionState.callId}] ❌ Fare callback failed:`, err);
                });
              }
              
              // Clear pending state
              sessionState.pendingFareConfirm = null;
              
              // If NO, inject system message to tell Ada to apologize
              if (isNo) {
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{ type: "input_text", text: "[SYSTEM: Customer declined the fare. Apologize briefly and ask if they'd like to try a different time, location, or if there's anything else you can help with.]" }]
                  }
                }));
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
              }
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
              const { error } = await supabase
                .from("callers")
                .upsert({
                  phone_number: sessionState.phone,
                  name: args.name,
                  updated_at: new Date().toISOString()
                }, { onConflict: "phone_number" });
              
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
                    // First get current known_areas
                    const { data: callerData } = await supabase
                      .from("callers")
                      .select("known_areas")
                      .eq("phone_number", sessionState.phone)
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
                        phone_number: sessionState.phone,
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
          
          // === DUAL-SOURCE EXTRACTION & VALIDATION ===
          // 1. Normalize addresses for comparison
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
          
          // 2. Run AI extraction on conversation (both user + assistant turns)
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
          } = {};
          
          if (conversationForExtraction.length > 0) {
            try {
              console.log(`[${sessionState.callId}] 🔍 Running dual-source AI extraction...`);
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
                console.log(`[${sessionState.callId}] 🔍 AI Extraction result:`, extractedBooking);
              }
            } catch (extractErr) {
              console.error(`[${sessionState.callId}] AI extraction failed:`, extractErr);
            }
          }
          
          // 3. Compare Ada's interpretation vs AI extraction
          const adaPickup = args.pickup || "";
          const adaDestination = args.destination || "";
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
          
          // 4. Determine final addresses
          let finalPickup = adaPickup;
          let finalDestination = adaDestination;
          let sourceDiscrepancy = false;
          
          // If Ada's pickup = destination (common error), prefer extraction
          if (adaPickupNorm && adaDestNorm && adaPickupNorm === adaDestNorm) {
            console.log(`[${sessionState.callId}] ⚠️ Ada's pickup = destination, checking extraction...`);
            if (extPickupNorm && extDestNorm && extPickupNorm !== extDestNorm) {
              console.log(`[${sessionState.callId}] ✅ Using extracted addresses (distinct)`);
              finalPickup = extractedPickup;
              finalDestination = extractedDestination;
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
          
          // If sources disagree significantly (< 50% match), flag for clarification
          if (extractedPickup && pickupSourceMatch < 0.5) {
            console.log(`[${sessionState.callId}] ⚠️ Pickup discrepancy detected`);
            sourceDiscrepancy = true;
          }
          if (extractedDestination && destSourceMatch < 0.5) {
            console.log(`[${sessionState.callId}] ⚠️ Destination discrepancy detected`);
            sourceDiscrepancy = true;
          }
          
          // If major discrepancy, prefer extracted (from conversation) over Ada's interpretation
          if (sourceDiscrepancy && extractedBooking.confidence === "high") {
            console.log(`[${sessionState.callId}] 🔄 Using high-confidence extracted addresses due to discrepancy`);
            if (extractedPickup) finalPickup = extractedPickup;
            if (extractedDestination) finalDestination = extractedDestination;
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
          const finalPassengers = args.passengers || extractedBooking.passengers || 1;
          const finalBags = args.bags ?? extractedBooking.luggage ?? 0;
          const finalVehicleType = args.vehicle_type || extractedBooking.vehicle_type || "saloon";
          const finalPickupTime = args.pickup_time || extractedBooking.pickup_time || "now";
          
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
          
          // === END VALIDATION ===
          
          sessionState.booking = {
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers,
            bags: args.bags || 0,
            vehicle_type: args.vehicle_type || "saloon",
            version: 1
          };
          
          const jobId = crypto.randomUUID();
          let fare = "£12.50";
          let etaMinutes = 8;
          let bookingRef = "";
          let distance = "";
          
          // Call dispatch webhook if configured
          const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
          console.log(`[${sessionState.callId}] 🔗 DISPATCH_WEBHOOK_URL configured: ${DISPATCH_WEBHOOK_URL ? 'YES' : 'NO'}`);
          
          let dispatchConfirmationSent = false;
          
          if (DISPATCH_WEBHOOK_URL) {
            try {
              console.log(`[${sessionState.callId}] 📡 Calling dispatch webhook: ${DISPATCH_WEBHOOK_URL}`);
              console.log(`[${sessionState.callId}] ⏳ Sending booking to dispatch, will poll for callback response...`);
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
              
              const webhookPayload = {
                job_id: jobId,
                call_id: sessionState.callId,
                caller_phone: formattedPhone,
                caller_name: sessionState.customerName,
                // Ada's interpreted/normalized addresses
                ada_pickup: args.pickup,
                ada_destination: args.destination,
                // Raw caller addresses (what they actually said)
                callers_pickup: adaPickup !== finalPickup ? adaPickup : null,
                callers_dropoff: adaDestination !== finalDestination ? adaDestination : null,
                // Raw STT transcripts from this call - each turn separately
                user_transcripts: userTranscripts,
                // GPS location (if available)
                gps_lat: sessionState.gpsLat,
                gps_lon: sessionState.gpsLon,
                // Booking details
                passengers: args.passengers || 1,
                bags: args.bags || 0,
                vehicle_type: args.vehicle_type || "saloon",
                vehicle_request: args.vehicle_request || null,
                pickup_time: args.pickup_time || "now",
                special_requests: args.special_requests || null,
                timestamp: new Date().toISOString()
              };
              
              // Fire-and-forget POST to dispatch webhook (acknowledges with OK/200)
              fetch(DISPATCH_WEBHOOK_URL, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(webhookPayload),
              }).catch(err => console.error(`[${sessionState.callId}] Webhook POST failed:`, err));
              
              // Now poll live_calls table for dispatch response (fare, eta, or say message)
              // Dispatch will call taxi-dispatch-callback which updates live_calls
              const pollTimeout = 25000; // 25 seconds max wait (callback can take 15-20s)
              const pollInterval = 500; // Check every 500ms
              const pollStart = Date.now();
              let dispatchResult: any = null;
              
              console.log(`[${sessionState.callId}] 🔄 Polling for dispatch callback response (${pollTimeout/1000}s timeout)...`);
              
              while (Date.now() - pollStart < pollTimeout) {
                // Check live_calls for dispatch response
                const { data: callData } = await supabase
                  .from("live_calls")
                  .select("fare, eta, status, transcripts")
                  .eq("call_id", sessionState.callId)
                  .single();
                
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
                
                // Fallback: Check if dispatch set fare OR eta OR status changed to dispatched
                const hasConfirmation = callData?.fare || callData?.eta || callData?.status === "dispatched";
                if (hasConfirmation) {
                  console.log(`[${sessionState.callId}] ✅ Dispatch confirmed (no message): fare=${callData?.fare}, eta=${callData?.eta}, status=${callData?.status}`);
                  dispatchResult = {
                    fare: callData?.fare || null,
                    eta_minutes: parseInt(callData?.eta) || 8,
                    confirmed: true
                  };
                  break;
                }
                
                // Check if dispatch sent a "say" message (look for dispatch transcript)
                // (using transcripts already declared above)
                
                // Check for hangup instruction first
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
                
                // Check for ask_confirm (fare confirmation) request
                const dispatchAskConfirm = transcripts.find(t => 
                  t.role === "dispatch_ask_confirm" && 
                  new Date(t.timestamp).getTime() > pollStart
                );
                
                if (dispatchAskConfirm) {
                  console.log(`[${sessionState.callId}] 💰 Dispatch ask_confirm: "${dispatchAskConfirm.text}"`);
                  
                  // Store pending fare confirmation state
                  sessionState.pendingFareConfirm = {
                    active: true,
                    message: dispatchAskConfirm.text,
                    fare: dispatchAskConfirm.fare || null,
                    eta: dispatchAskConfirm.eta || null,
                    callbackUrl: dispatchAskConfirm.callback_url || null,
                    askedAt: Date.now()
                  };
                  
                  dispatchResult = {
                    ada_message: dispatchAskConfirm.text,
                    needs_fare_confirm: true
                  };
                  break;
                }
                
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
                
                // Wait before next poll
                await new Promise(resolve => setTimeout(resolve, pollInterval));
              }
              
              if (dispatchResult) {
                // Check if dispatch requested hangup - immediately end call
                if (dispatchResult.hangup) {
                  console.log(`[${sessionState.callId}] 📞 Dispatch requested hangup: ${dispatchResult.ada_message}`);
                  
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
                
                if (dispatchResult.ada_message && !dispatchResult.confirmed) {
                  console.log(`[${sessionState.callId}] 💬 Dispatch message for Ada: ${dispatchResult.ada_message}`);
                  // Return early with the message - don't confirm booking yet
                  result = {
                    success: false,
                    needs_clarification: true,
                    ada_message: dispatchResult.ada_message,
                    message: "Dispatch needs clarification from customer"
                  };
                  break;
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
                console.log(`[${sessionState.callId}] ⏰ Dispatch callback timeout - using defaults`);
              }
            } catch (webhookErr) {
              console.error(`[${sessionState.callId}] ⚠️ Dispatch webhook error:`, webhookErr);
            }
          }
          
          // Create booking in DB (only if we got here - not rejected/clarification)
          const { error: bookingError } = await supabase.from("bookings").insert({
            call_id: sessionState.callId,
            caller_phone: sessionState.phone,
            caller_name: sessionState.customerName,
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers || 1,
            fare: fare,
            eta: `${etaMinutes} minutes`,
            status: "confirmed",
            booking_details: { job_id: jobId, booking_ref: bookingRef, distance: distance }
          });
          
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
          sessionState.lastBookTaxiSuccessAt = Date.now();
          console.log(`[${sessionState.callId}] ✅ Booking enforcement: book_taxi succeeded, Ada may now confirm`);
          
          break;
        }

        case "cancel_booking":
          console.log(`[${sessionState.callId}] 🚫 Cancelling booking`);
          sessionState.hasActiveBooking = false;
          sessionState.booking = { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, version: 0 };
          result = { success: true };
          break;

        case "modify_booking": {
          console.log(`[${sessionState.callId}] ✏️ Modify request from Ada:`, args);
          
          // Capture previous booking state BEFORE making changes
          const previousBooking = {
            pickup: sessionState.booking.pickup,
            destination: sessionState.booking.destination,
            passengers: sessionState.booking.passengers,
            bags: sessionState.booking.bags,
            version: sessionState.booking.version
          };
          
          console.log(`[${sessionState.callId}] 📋 Previous booking state:`, previousBooking);
          
          // === AI EXTRACTION FOR MODIFICATION ===
          // Run AI extraction on recent conversation to verify the modification
          const recentConversation = sessionState.transcripts
            .filter(t => t.role === "user" || t.role === "assistant")
            .slice(-8);
          
          let extractedModification: {
            pickup?: string;
            destination?: string;
            passengers?: number;
            luggage?: number;
            vehicle_type?: string;
            confidence?: string;
          } = {};
          
          if (recentConversation.length > 0) {
            try {
              console.log(`[${sessionState.callId}] 🔍 Running AI extraction for modification (is_modification=true)...`);
              const extractionResponse = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract-unified`, {
                method: "POST",
                headers: {
                  "Content-Type": "application/json",
                  "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`
                },
                body: JSON.stringify({
                  conversation: recentConversation,
                  caller_name: sessionState.customerName,
                  caller_phone: sessionState.phone, // For alias lookup
                  is_modification: true, // Key flag - preserves unchanged fields
                  current_booking: {
                    pickup: previousBooking.pickup,
                    destination: previousBooking.destination,
                    passengers: previousBooking.passengers,
                    luggage: previousBooking.bags ? `${previousBooking.bags} bags` : null,
                    vehicle_type: sessionState.booking.vehicle_type
                  }
                })
              });
              
              if (extractionResponse.ok) {
                extractedModification = await extractionResponse.json();
                console.log(`[${sessionState.callId}] 🔍 AI Extraction for modify:`, extractedModification);
              }
            } catch (extractErr) {
              console.error(`[${sessionState.callId}] AI extraction for modify failed:`, extractErr);
            }
          }
          
          // === MERGE: Previous booking + Ada's change + AI extraction ===
          // Priority: AI extraction (if high confidence) > Ada's new_value > Previous value
          const normalizeForCompare = (addr: string): string => {
            if (!addr) return "";
            return addr.toLowerCase().replace(/[,.\-]/g, " ").replace(/\s+/g, " ").trim();
          };
          
          let finalNewValue = args.new_value;
          
          // For address fields, cross-check with extraction
          if (args.field_to_change === "pickup" || args.field_to_change === "destination") {
            const extractedValue = args.field_to_change === "pickup" 
              ? extractedModification.pickup 
              : extractedModification.destination;
            
            if (extractedValue && extractedModification.confidence === "high") {
              const adaNorm = normalizeForCompare(args.new_value);
              const extNorm = normalizeForCompare(extractedValue);
              
              // If AI found something different and has high confidence, use it
              if (adaNorm !== extNorm) {
                console.log(`[${sessionState.callId}] 🔄 Using AI-extracted value: "${extractedValue}" instead of Ada's "${args.new_value}"`);
                finalNewValue = extractedValue;
              }
            }
          }
          
          // For passengers/bags, use extraction if Ada didn't provide specific value
          if (args.field_to_change === "passengers" && extractedModification.passengers) {
            const adaNum = parseInt(args.new_value);
            if (isNaN(adaNum) || adaNum < 1) {
              finalNewValue = String(extractedModification.passengers);
              console.log(`[${sessionState.callId}] 🔄 Using AI-extracted passengers: ${finalNewValue}`);
            }
          }
          if (args.field_to_change === "bags" && extractedModification.luggage !== undefined) {
            const adaNum = parseInt(args.new_value);
            if (isNaN(adaNum)) {
              finalNewValue = String(extractedModification.luggage);
              console.log(`[${sessionState.callId}] 🔄 Using AI-extracted bags: ${finalNewValue}`);
            }
          }
          
          const oldValue = args.field_to_change === "pickup" ? sessionState.booking.pickup
            : args.field_to_change === "destination" ? sessionState.booking.destination
            : args.field_to_change === "passengers" ? sessionState.booking.passengers
            : args.field_to_change === "bags" ? sessionState.booking.bags
            : null;
          
          // Apply the changes with final verified value
          if (args.field_to_change === "pickup") sessionState.booking.pickup = finalNewValue;
          if (args.field_to_change === "destination") sessionState.booking.destination = finalNewValue;
          if (args.field_to_change === "passengers") sessionState.booking.passengers = parseInt(finalNewValue);
          if (args.field_to_change === "bags") sessionState.booking.bags = parseInt(finalNewValue);
          
          // Also capture any other extracted details for potential use
          if (extractedModification.vehicle_type && !sessionState.booking.vehicle_type) {
            sessionState.booking.vehicle_type = extractedModification.vehicle_type;
          }
          
          console.log(`[${sessionState.callId}] ✅ Applied modification: ${args.field_to_change} = "${finalNewValue}" (was: "${oldValue}")`);
          console.log(`[${sessionState.callId}] 📋 Updated booking state:`, sessionState.booking);
          
          // Send webhook with modification details and poll for updated fare
          const DISPATCH_MODIFY_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
          let updatedFare: string | null = null;
          let updatedEta: string | null = null;
          
          if (DISPATCH_MODIFY_URL) {
            // Increment booking version for each modification
            sessionState.booking.version = (sessionState.booking.version || 1) + 1;
            
            // Get user transcripts for STT reference (same as book_taxi)
            const userTranscripts = sessionState.transcripts
              .filter(t => t.role === "user")
              .slice(-6)
              .map(t => ({ text: t.text, timestamp: t.timestamp }));
            
            // Format phone number for WhatsApp: strip '+' prefix and leading '0'
            let formattedPhone = sessionState.phone?.replace(/^\+/, '') || '';
            if (formattedPhone.startsWith('0')) {
              formattedPhone = formattedPhone.slice(1);
            }
            
            // Use EXACT same payload structure as book_taxi - dispatch can detect update via call_id
            const modifyPayload = {
              job_id: crypto.randomUUID(), // New job_id for this version
              call_id: sessionState.callId,
              caller_phone: formattedPhone,
              caller_name: sessionState.customerName,
              // Ada's interpreted addresses (current state after modification)
              ada_pickup: sessionState.booking.pickup,
              ada_destination: sessionState.booking.destination,
              // Raw STT transcripts from this call - each turn separately
              user_transcripts: userTranscripts,
              // GPS location (if available)
              gps_lat: sessionState.gpsLat,
              gps_lon: sessionState.gpsLon,
              // Booking details
              passengers: sessionState.booking.passengers || 1,
              bags: sessionState.booking.bags || 0,
              vehicle_type: sessionState.booking.vehicle_type || "saloon",
              pickup_time: "now",
              timestamp: new Date().toISOString()
            };
            
            console.log(`[${sessionState.callId}] 📡 Sending booking update (same format as new booking)`);
            
            // Fire webhook
            fetch(DISPATCH_MODIFY_URL, {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify(modifyPayload),
            }).catch(err => console.error(`[${sessionState.callId}] Modify webhook failed:`, err));
            
            // Poll for updated fare/eta (10 second timeout for modifications)
            const pollTimeout = 10000;
            const pollInterval = 500;
            const pollStart = Date.now();
            
            console.log(`[${sessionState.callId}] 🔄 Polling for updated fare after modification...`);
            
            while (Date.now() - pollStart < pollTimeout) {
              const { data: callData } = await supabase
                .from("live_calls")
                .select("fare, eta, updated_at")
                .eq("call_id", sessionState.callId)
                .single();
              
              // Check if fare/eta was updated after our webhook
              if (callData && new Date(callData.updated_at).getTime() > pollStart) {
                if (callData.fare) {
                  updatedFare = callData.fare;
                  updatedEta = callData.eta;
                  console.log(`[${sessionState.callId}] ✅ Got updated fare: ${updatedFare}, eta: ${updatedEta}`);
                  break;
                }
              }
              
              await new Promise(resolve => setTimeout(resolve, pollInterval));
            }
          }
          
          result = { 
            success: true, 
            modified: args.field_to_change, 
            old_value: oldValue,
            new_value: finalNewValue,
            current_booking: {
              pickup: sessionState.booking.pickup,
              destination: sessionState.booking.destination,
              passengers: sessionState.booking.passengers,
              bags: sessionState.booking.bags
            },
            ...(updatedFare && { fare: updatedFare }),
            ...(updatedEta && { eta: updatedEta }),
            message: updatedFare 
              ? `Updated ${args.field_to_change} from "${oldValue}" to "${finalNewValue}". New fare: ${updatedFare}`
              : `Updated ${args.field_to_change} from "${oldValue}" to "${finalNewValue}"`
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
          
          // Run AI extraction on full conversation to get all booking components
          const conversationForVerification = sessionState.transcripts
            .filter(t => t.role === "user" || t.role === "assistant")
            .slice(-15); // More context for thorough extraction
          
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
                  caller_phone: sessionState.phone, // For alias lookup
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
          
          console.log(`[${sessionState.callId}] ✅ Verification result: ${missingFields.length} missing fields`);
          break;
        }

        case "end_call": {
          console.log(`[${sessionState.callId}] 👋 Ending call`);
          result = { success: true };
          
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
          console.log(`[${sessionState.callId}]   Avg transcript delay:   ${m.avgTranscriptDelayMs.toFixed(0)}ms`);
          console.log(`[${sessionState.callId}]   Avg speech duration:    ${m.avgSpeechDurationMs.toFixed(0)}ms`);
          console.log(`[${sessionState.callId}] ═══════════════════════════════════════════════════════════`);
          
          // Immediate flush on call end - capture all transcripts
          immediateFlush(sessionState);
          // Update call status
          await supabase.from("live_calls")
            .update({ status: "completed", ended_at: new Date().toISOString() })
            .eq("call_id", sessionState.callId);
          
          // Mark call as ended - prevent further processing
          sessionState.callEnded = true;
          
          // Close OpenAI connection after a short delay to let goodbye audio finish
          setTimeout(() => {
            console.log(`[${sessionState.callId}] 🔌 Closing OpenAI WebSocket after end_call`);
            openaiWs?.close();
          }, 4000); // 4 second delay for goodbye audio
          
          // DON'T trigger response.create after end_call - Ada should stop talking
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

    // Trigger response continuation
    openaiWs?.send(JSON.stringify({ type: "response.create" }));
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

          // HALF-DUPLEX MODE: Buffer audio while Ada is speaking, flush when she stops
          if (state.halfDuplex && state.isAdaSpeaking) {
            // Buffer the raw audio (we'll process it when Ada stops)
            state.halfDuplexBuffer.push(audioData);
            // Cap buffer to prevent memory issues (keep last ~5 seconds at 8kHz, 20ms chunks = 250 chunks)
            if (state.halfDuplexBuffer.length > 250) {
              state.halfDuplexBuffer.shift();
            }
            return; // Don't forward audio while Ada speaks in half-duplex mode
          }

          // Bridge sends 8kHz µ-law, need to convert to 24kHz PCM16 for OpenAI
          // NOTE: OpenAI Realtime API only accepts 24kHz
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

          // BARGE-IN (only in full-duplex mode): If Ada is currently speaking and we detect real user energy,
          // cancel the current AI response immediately so Ada can hear the caller.
          // IMPORTANT: We must NEVER drop audio - only decide whether to trigger barge-in.
          if (!state.halfDuplex && state.isAdaSpeaking && state.openAiResponseActive) {
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

          // Step 2: Upsample to 24kHz (OpenAI Realtime API requirement)
          // RASA mode: 8kHz → 16kHz → 24kHz (two-stage upsampling for Whisper-like processing)
          // Standard mode: 8kHz → 24kHz (direct 3x interpolation)
          let pcm16_24k: Int16Array;
          
          if (state.useRasaAudioProcessing) {
            // RASA: Two-stage resampling (8kHz → 16kHz → 24kHz)
            // Stage 1: 8kHz → 16kHz (2x)
            const pcm16_16k = new Int16Array(pcm16_8k.length * 2);
            for (let i = 0; i < pcm16_8k.length - 1; i++) {
              const s0 = pcm16_8k[i];
              const s1 = pcm16_8k[i + 1];
              pcm16_16k[i * 2] = s0;
              pcm16_16k[i * 2 + 1] = Math.round((s0 + s1) / 2);
            }
            const last8k = pcm16_8k.length - 1;
            pcm16_16k[last8k * 2] = pcm16_8k[last8k];
            pcm16_16k[last8k * 2 + 1] = pcm16_8k[last8k];
            
            // Stage 2: 16kHz → 24kHz (1.5x)
            // Use ratio 3:2 - for every 2 input samples, output 3 samples
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
            // Standard: Direct 8kHz → 24kHz (3x linear interpolation)
            pcm16_24k = new Int16Array(pcm16_8k.length * 3);
            for (let i = 0; i < pcm16_8k.length - 1; i++) {
              const s0 = pcm16_8k[i];
              const s1 = pcm16_8k[i + 1];
              pcm16_24k[i * 3] = s0;
              pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
              pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
            }
            // Handle last sample
            const lastIdx = pcm16_8k.length - 1;
            pcm16_24k[lastIdx * 3] = pcm16_8k[lastIdx];
            pcm16_24k[lastIdx * 3 + 1] = pcm16_8k[lastIdx];
            pcm16_24k[lastIdx * 3 + 2] = pcm16_8k[lastIdx];
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
          booking: { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, version: 0 },
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
          speechStartTime: null,
          speechStopTime: null,
          callEnded: false,
          useRasaAudioProcessing: message.rasa_audio_processing ?? false,
          halfDuplex: message.half_duplex ?? false,
          halfDuplexBuffer: [],
          bookingConfirmedThisTurn: false,
          lastBookTaxiSuccessAt: null,
          pendingFareConfirm: null,
          sttMetrics: {
            totalTranscripts: 0,
            totalWords: 0,
            totalChars: 0,
            correctedTranscripts: 0,
            filteredHallucinations: 0,
            avgTranscriptDelayMs: 0,
            transcriptDelays: [],
            avgSpeechDurationMs: 0,
            speechDurations: [],
          }
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
            booking: { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, version: 0 },
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
            speechStartTime: null,
            speechStopTime: null,
            callEnded: false,
            useRasaAudioProcessing: message.rasa_audio_processing ?? false,
            halfDuplex: message.half_duplex ?? false,
            halfDuplexBuffer: [],
            bookingConfirmedThisTurn: false,
            lastBookTaxiSuccessAt: null,
            pendingFareConfirm: null,
            sttMetrics: {
              totalTranscripts: 0,
              totalWords: 0,
              totalChars: 0,
              correctedTranscripts: 0,
              filteredHallucinations: 0,
              avgTranscriptDelayMs: 0,
              transcriptDelays: [],
              avgSpeechDurationMs: 0,
              speechDurations: [],
            }
          };
        }
        
        // Also update useRasaAudioProcessing and halfDuplex from init message if pre-connected
        if (state && preConnected) {
          state.useRasaAudioProcessing = message.rasa_audio_processing ?? false;
          state.halfDuplex = message.half_duplex ?? false;
        }
        
        console.log(`[${callId}] 🎧 Audio processing: ${state!.useRasaAudioProcessing ? 'Rasa-style (8→16kHz)' : 'Standard (8→24kHz)'}`);
        
        console.log(`[${callId}] 🌐 Phone: ${phone}, Detected: ${detectedLanguage}, Final language: ${state!.language}`);

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

        // Fire-and-forget: Lookup caller history, GPS, and update live_calls in background
        // This runs in parallel while Ada starts greeting
        (async () => {
          try {
            // If caller has an active booking, load the latest booking details ASAP (non-blocking)
            // This prevents the model from guessing / using caller history as the booking.
            // NOTE: Use a loose type here to avoid Deno edge type inference issues.
            let activeBooking: any = null;

            if (state?.hasActiveBooking && phone && phone !== "unknown") {
              const { data: bookingData, error: bookingError } = await supabase
                .from("bookings")
                .select("pickup, destination, passengers, fare, eta, status, booked_at")
                .eq("caller_phone", phone)
                .in("status", ["confirmed", "dispatched", "active"])
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
              // Format phone for lookups - strip '+' prefix if present
              let lookupPhone = phone;
              const altPhone = phone.replace(/^\+/, ''); // Also try without +
              
              const { data: callerData } = await supabase
                .from("callers")
                .select("name, last_pickup, last_destination, total_bookings")
                .eq("phone_number", phone)
                .maybeSingle();

              if (callerData && state) {
                state.callerLastPickup = callerData.last_pickup || null;
                state.callerLastDestination = callerData.last_destination || null;
                state.callerTotalBookings = callerData.total_bookings || 0;
                if (!state.customerName && callerData.name) {
                  state.customerName = callerData.name;
                }
                console.log(`[${callId}] 👤 Loaded caller: ${callerData.name || 'no name'}, ${state.callerTotalBookings} bookings`);
              }
              
              // Check for active bookings if not already loaded
              if (!activeBooking) {
                // Try both phone formats (with and without +)
                const { data: bookingData } = await supabase
                  .from("bookings")
                  .select("pickup, destination, passengers, fare, eta, status, booked_at, caller_name")
                  .or(`caller_phone.eq.${phone},caller_phone.eq.${altPhone}`)
                  .in("status", ["confirmed", "dispatched", "active", "pending"])
                  .order("booked_at", { ascending: false })
                  .limit(1)
                  .maybeSingle();
                
                if (bookingData && state) {
                  activeBooking = bookingData;
                  state.hasActiveBooking = true;
                  state.booking.pickup = bookingData.pickup;
                  state.booking.destination = bookingData.destination;
                  state.booking.passengers = bookingData.passengers;
                  
                  // Update name from booking if not set
                  if (!state.customerName && bookingData.caller_name) {
                    state.customerName = bookingData.caller_name;
                  }
                  
                  console.log(`[${callId}] 📦 Found active booking: ${bookingData.pickup} → ${bookingData.destination}`);
                  
                  // Inject booking context for Ada
                  if (openaiWs && openaiConnected) {
                    const customerGreeting = state.customerName 
                      ? `The caller is ${state.customerName}.` 
                      : "";
                    
                    openaiWs.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "user",
                        content: [{
                          type: "input_text",
                          text: `[SYSTEM: ${customerGreeting} This caller has an ACTIVE BOOKING. Details: Pickup: "${bookingData.pickup}", Destination: "${bookingData.destination}", Passengers: ${bookingData.passengers}. Greet them by name if known, mention they have an existing booking, and ask if they want to keep it, change it, or cancel it. Do NOT assume they want to book again - acknowledge the existing booking first.]`
                        }]
                      }
                    }));
                    
                    // Trigger Ada to respond with this context
                    openaiWs.send(JSON.stringify({
                      type: "response.create"
                    }));
                  }
                }
              }
            }

            // Create/update live call record (non-blocking)
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
            try {
              const { data: callerData } = await supabase
                .from("callers")
                .select("name, last_pickup, last_destination, total_bookings")
                .eq("phone_number", phone)
                .maybeSingle();
              
              if (callerData && state) {
                state.callerLastPickup = callerData.last_pickup || null;
                state.callerLastDestination = callerData.last_destination || null;
                state.callerTotalBookings = callerData.total_bookings || 0;
                if (!state.customerName && callerData.name) {
                  state.customerName = callerData.name;
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

  socket.onclose = () => {
    console.log(`[${state?.callId || "unknown"}] Client disconnected`);
    // Final flush on disconnect to capture any remaining transcripts
    if (state) {
      immediateFlush(state);
    }
    openaiWs?.close();
  };

  socket.onerror = (err) => {
    console.error("[simple] Socket error:", err);
  };

  return response;
});
