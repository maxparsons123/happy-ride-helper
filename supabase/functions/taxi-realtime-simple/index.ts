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

// Normalize to digits-only for DB lookups (callers/bookings use digits-only phone_number)
const normalizePhone = (phone: string | null | undefined) => String(phone || "").replace(/\D/g, "");

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
- Returning caller (active booking): Greet [NAME], mention they have an active booking, and ask ONLY: "Do you want to keep it, change it, or cancel it?".
  - Do NOT suggest a new pickup/destination yourself.
  - If they say "change", ask what they want to change (pickup, destination, time, passengers) and wait for their answer.
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
   - WAIT for user to say "yes", "yeah", "correct", "that's right" before proceeding.
   - If user says "no" or corrects an address, update it and confirm again.
5. Once user CONFIRMS → CALL book_taxi with confirmation_state: "request_quote" to get fare/ETA.
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

🚨 CRITICAL: FARE CONFIRMATION STATE MACHINE 🚨
You MUST follow this exact 3-step flow:

STEP 1 - REQUEST QUOTE:
- After user confirms addresses → CALL book_taxi with confirmation_state: "request_quote"
- This returns fare and ETA from dispatch (e.g., fare: "£15.00", eta: "7 minutes")
- READ the fare and ETA to the customer: "The price is £15.00 and your driver is 7 minutes away. Would you like me to book that?"
- WAIT for customer response. Do NOT call any tool yet.

STEP 2 - CUSTOMER RESPONDS YES:
- If customer says "yes", "yeah", "go ahead", "book it" → CALL book_taxi with confirmation_state: "confirmed" (same pickup/destination)
- This confirms the booking with dispatch
- Then say: "Booked! Your driver is on the way. Is there anything else?"

STEP 3 - CUSTOMER RESPONDS NO:
- If customer says "no", "cancel", "never mind" → CALL book_taxi with confirmation_state: "rejected"
- Then say: "No problem, I've cancelled that. Is there anything else I can help with?"

🚫 FORBIDDEN - NEVER DO THESE:
- NEVER say fare/price before receiving it from book_taxi result
- NEVER say "Booked!" before calling book_taxi with confirmation_state: "confirmed"
- NEVER skip the quote step - ALWAYS call with "request_quote" first
- NEVER invent or guess fare amounts

If user says "cancel" → CALL cancel_booking function FIRST, then respond.
If user corrects name → CALL save_customer_name function immediately.
Call end_call function after saying "Safe travels!".

🚫 ABSOLUTELY FORBIDDEN - YOU WILL BE CANCELLED IF YOU DO THESE:
- NEVER say "Booked!", "Your taxi is confirmed", "taxi is on its way", "driver is on the way" unless book_taxi succeeded.
- NEVER mention a fare amount (£15, €20, $25) unless book_taxi returned that exact value.
- NEVER mention an ETA (5 minutes, arriving in 8 minutes) unless book_taxi returned that exact value.
- NEVER say "safe travels" or "have a good trip" until AFTER book_taxi succeeded AND user confirms they're done.
- You CANNOT confirm a booking by speaking. You MUST call the book_taxi function tool FIRST.
- If you try to confirm without calling book_taxi, your response will be CANCELLED and you'll be forced to call the tool.

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

⚠️ MODIFICATION REQUESTS - SAME RULE APPLIES:
When user says "change my booking from [A] to [B]" or "change from [A] going to [B]":
- This is a COMPLETE booking statement: Pickup = A, Destination = B
- They are NOT asking to "change pickup FROM A TO something else"
- They ARE giving you the full route: pickup [A], destination [B]
- NEVER interpret this as changing pickup address!

Examples:
- "change my booking from 52a david road going to sweetspot" → Keep Pickup: 52a David Road, Set Destination: Sweet Spot
- "change it from the hotel to the airport" → Pickup: hotel, Destination: airport
- "can i change from home going to the shops" → Pickup: home, Destination: shops

If they ONLY want to change one field, they'll say:
- "change my pickup to [X]" → Only change pickup
- "change my destination to [Y]" → Only change destination
- "change the address" → Ask which one (pickup or destination)

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
    description: "Book taxi with a state machine. First call with 'request_quote' to get fare/ETA. After reading fare to customer and they say YES, call again with 'confirmed'. If they say NO, call with 'rejected'.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
        confirmation_state: { 
          type: "string", 
          enum: ["request_quote", "confirmed", "rejected"],
          description: "Use 'request_quote' first to get fare. After customer hears fare: 'confirmed' if YES, 'rejected' if NO."
        },
        passengers: { type: "integer", minimum: 1, default: 1, description: "Number of passengers (default 1 if not specified)" },
        bags: { type: "integer", minimum: 0, description: "Number of bags (only ask for airport/station trips)" },
        pickup_time: { type: "string", description: "ISO timestamp or 'now'" },
        vehicle_type: { type: "string", enum: ["saloon", "estate", "mpv", "minibus"] }
      },
      required: ["pickup", "destination", "confirmation_state"]
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
  activeBookingCallId: string | null; // Original call_id of the active booking (for updates when resuming)
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
  deferredResponseCreate: boolean; // if true, send response.create right after response.done
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
  lastConfirmedTripKey: string | null;
  
  // Audio buffering for confirmation enforcement
  // When true, audio is forwarded immediately; when false, audio is buffered until transcript verification
  audioVerified: boolean;
  pendingAudioBuffer: string[]; // Base64-encoded audio chunks waiting for transcript verification

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
        bookingContext += `\n\nIMPORTANT RULES (ACTIVE BOOKING):`;
        bookingContext += `\n- Ask ONLY: "Do you want to keep it, change it, or cancel it?"`;
        bookingContext += `\n- Do NOT suggest any new pickup/destination yourself.`;
        bookingContext += `\n- If caller says a DIFFERENT address, treat it as a change request: ask for confirmation, then call modify_booking.`;
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
        
        // ✅ POST-GOODBYE GUARD: If call is ending, cancel any new response immediately
        // This prevents OpenAI's VAD from generating hallucinated responses after goodbye
        if (sessionState.callEnded) {
          console.log(`[${sessionState.callId}] 🛑 Cancelling VAD-triggered response after callEnded`);
          openaiWs?.send(JSON.stringify({ type: "response.cancel" }));
        }
        break;

      case "response.done":
        sessionState.openAiResponseActive = false;

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
          const ignoreMs = sessionState.useRasaAudioProcessing ? 900 : 600;
          sessionState.bargeInIgnoreUntil = Date.now() + ignoreMs;
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
          // AGGRESSIVE: Cancel on ANY phrase that sounds like confirmation
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
            const pendingFareNum = Number(String(sessionState.pendingQuote.fare).replace(/[^0-9.]/g, ""));
            const spokenFareMatch = currentText.match(/(?:£|€|\$)\s*(\d+(?:\.\d{1,2})?)/);
            const spokenFareNum = spokenFareMatch ? Number(spokenFareMatch[1]) : NaN;

            if (Number.isFinite(pendingFareNum) && Number.isFinite(spokenFareNum)) {
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
          const hasHardBlockPhrase = HARD_BLOCK_PHRASES.some((phrase) => lowerText.includes(phrase));

          // If pendingQuote is active, ONLY block hard confirmation phrases, placeholder leaks, and fare mismatches.
          // If no pendingQuote, block all confirmation + fare phrases (original behavior)
          const isDisallowedConfirmationPhrase = hasPendingQuote
            ? (hasHardBlockPhrase || hasPlaceholderInstruction || hasFareMismatch)
            : (hasBookingConfirmationPhrase || hasPlaceholderInstruction || hasPriceOrCurrencyMention);

          // If Ada says a disallowed confirmation phrase but book_taxi wasn't called this turn, CANCEL!
          if (isDisallowedConfirmationPhrase && !sessionState.bookingConfirmedThisTurn) {
            console.log(
              `[${sessionState.callId}] 🚨 BOOKING ENFORCEMENT: Ada tried to confirm without calling book_taxi! Cancelling response.`
            );
            console.log(
              `[${sessionState.callId}] 🚨 Detected in transcript: "${currentText}" (placeholderLeak=${hasPlaceholderInstruction})`
            );

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

          // Inject a system message to recover safely
            // - If we are awaiting fare approval (pendingQuote), guide Ada to proper yes/no handling
            // - Otherwise, force the tool call to obtain the fare
            const systemErrorText = hasPendingQuote
              ? `[SYSTEM ERROR: You said a booking confirmation phrase before the book_taxi tool succeeded. The fare quote (${sessionState.pendingQuote?.fare || 'pending'}) was already given. NOW YOU MUST:
- If the customer said YES/yeah/go ahead → CALL book_taxi NOW with confirmation_state: "confirmed", pickup: "${sessionState.pendingQuote?.pickup || sessionState.booking?.pickup || ''}", destination: "${sessionState.pendingQuote?.destination || sessionState.booking?.destination || ''}"
- If the customer said NO/cancel → CALL book_taxi with confirmation_state: "rejected"
- If you haven't heard their response yet → Ask: "Would you like me to book that?" and WAIT.
Do NOT say 'booked' until the tool returns success.]`
              : "[SYSTEM ERROR: You attempted to confirm a booking without calling the book_taxi function. You MUST call the book_taxi function tool NOW with confirmation_state: 'request_quote' to get the fare first. Do NOT speak about booking confirmation until you receive the tool result.]";
            
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

            openaiWs?.send(JSON.stringify({ type: "response.create" }));
          } else if (!sessionState.bookingConfirmedThisTurn && sessionState.pendingAudioBuffer.length > 0) {
            // Transcript so far doesn't contain confirmation phrases - it's safe to flush buffered audio
            // This releases audio incrementally as we verify each chunk of transcript
            const chunksToFlush = sessionState.pendingAudioBuffer.splice(0, sessionState.pendingAudioBuffer.length);
            for (const audioChunk of chunksToFlush) {
              socket.send(JSON.stringify({ type: "audio", audio: audioChunk }));
            }
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

        // ✅ CALL-ENDED GUARD: Don't process new user input if call is ending
        if (sessionState.callEnded) {
          console.log(`[${sessionState.callId}] 🛑 Ignoring user transcript after call ended: "${userText}"`);
          break;
        }

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

          const lowerUserText = userText.toLowerCase();

          // === EXPLICIT BYE/GOODBYE DETECTION (HIGHEST PRIORITY) ===
          // If user says "bye" (even multiple times), they want to end the call immediately.
          // This takes priority over fare confirmation, booking prompts, etc.
          const isExplicitGoodbye = /\b(bye|goodbye|see ya|see you|cya|i'm done|im done|hang up|end call)\b/i.test(lowerUserText) &&
            // Exclude "bye" in compound phrases like "good bye sweet spot" (unlikely but guard against)
            !/going to|from|pick ?up|drop ?off/i.test(lowerUserText);
          
          if (isExplicitGoodbye && openaiWs && openaiConnected && !sessionState.callEnded) {
            console.log(`[${sessionState.callId}] 👋 Explicit goodbye detected: "${userText}" - ending call immediately`);
            
            // ✅ CRITICAL: Mark call as ending IMMEDIATELY to block all further processing
            sessionState.callEnded = true;
            
            // Clear pendingQuote to prevent any fare prompts from being processed
            sessionState.pendingQuote = null;

            // Cancel any in-flight assistant speech to avoid overlap
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

            openaiWs.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [
                    {
                      type: "input_text",
                      text: "[SYSTEM: The customer is done and is thanking you. Say 'Safe travels!' and nothing more. Then stay completely silent.]",
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
            }, 3000); // 3 second delay for goodbye audio
            
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
              
              // Cancel any in-flight assistant speech (prevents re-reading the quote)
              // Use Cancel-Clear-Inject sequencing to avoid response collisions.
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              }

              setTimeout(() => {
                // Clear input buffer to prevent VAD-triggered competing responses
                openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                setTimeout(() => {
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [
                        {
                          type: "input_text",
                          text:
                            `[SYSTEM: The customer has answered the fare question. You MUST call book_taxi NOW with confirmation_state: "${nextState}" using pickup: "${pickup}" and destination: "${destination}". Do NOT re-read the fare. Do NOT call book_taxi with "request_quote". After the tool succeeds, ask "Is there anything else I can help you with?"]`,
                        },
                      ],
                    },
                  }));

                  // Trigger immediate tool-driven response (we intentionally override the normal "VAD only" rule here)
                  openaiWs?.send(JSON.stringify({ type: "response.create" }));
                }, 350);
              }, 350);

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

          // If caller describes a change using a FULL ROUTE (e.g. "picked up at A going to B"),
          // inject a hard correction so the model doesn't treat B as a pickup and doesn't stall.
          // This also forces the Modification-First policy.
          const routeMatch =
            userText.match(/from\s+(.+?)\s+(?:going to|go to|to)\s+(.+)/i) ||
            userText.match(/pick(?:ed)?\s*up\s*(?:from|at|on)\s+(.+?)\s+(?:going to|go to|to)\s+(.+)/i) ||
            userText.match(/pick\s*up\s*(?:from|at|on)\s+(.+?)\s+(?:going to|go to|to)\s+(.+)/i);

          const hasExistingBookingContext =
            sessionState.hasActiveBooking || sessionState.booking.version > 0 || !!sessionState.lastBookTaxiSuccessAt;

          const isBookingChangeUtterance =
            hasExistingBookingContext &&
            /\b(change|update|modify)\b/i.test(lowerUserText) &&
            !!routeMatch;

          if (isBookingChangeUtterance && openaiWs && openaiConnected) {
            const pickupHint = (routeMatch?.[1] || "").trim();
            const destHint = (routeMatch?.[2] || "").trim();

            console.log(
              `[${sessionState.callId}] 🧭 Route-style change detected → pickup="${pickupHint}", destination="${destHint}"`
            );

            // Cancel-Clear-Inject protocol to avoid response collisions and stalling
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }

            setTimeout(() => {
              openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

              setTimeout(() => {
                openaiWs?.send(
                  JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [
                        {
                      type: "input_text",
                      text: `[SYSTEM: The caller is requesting a BOOKING CHANGE and provided the full updated route.
Interpret it as Pickup="${pickupHint || "(pickup)"}" and Destination="${destHint || "(destination)"}".
Do NOT treat the destination as a pickup.
Ask ONE short confirmation question only: "Just to confirm, change the pickup to ${pickupHint || "(pickup)"} and the destination to ${destHint || "(destination)"}, yeah?" Then WAIT.
If the caller confirms, CALL modify_booking immediately for any fields that changed (pickup and/or destination). If both changed, call modify_booking twice.
Then CALL book_taxi with confirmation_state: "request_quote" to get the updated fare. Speak only after the tools return.]`,
                        },
                      ],
                    },
                  })
                );

                openaiWs?.send(JSON.stringify({ type: "response.create" }));
              }, 350);
            }, 350);
          }
          
          // --- Fare confirmation is now handled by confirmation_state in book_taxi ---
          // User says YES/NO after hearing fare → Ada calls book_taxi with "confirmed" or "rejected"

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
          
          const confirmationState = args.confirmation_state || "request_quote";
          console.log(`[${sessionState.callId}] 📋 confirmation_state: "${confirmationState}"`);
          
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
            const finalPickup = args.pickup || pendingQuote.pickup || sessionState.booking.pickup;
            const finalDestination = args.destination || pendingQuote.destination || sessionState.booking.destination;

            // Persist latest route to session + dashboard (fixes cases where UI still shows the old destination)
            if (finalPickup) sessionState.booking.pickup = finalPickup;
            if (finalDestination) sessionState.booking.destination = finalDestination;

            await supabase.from("live_calls").update({
              pickup: finalPickup,
              destination: finalDestination,
              passengers: sessionState.booking.passengers || 1,
              updated_at: new Date().toISOString(),
            }).eq("call_id", sessionState.callId);
            
            // POST confirmation to callback_url if provided (tells C# to dispatch the driver)
            if (pendingQuote.callback_url) {
              try {
                console.log(`[${sessionState.callId}] 📡 POSTing confirmation to callback_url: ${pendingQuote.callback_url}`);
                const confirmPayload = {
                  call_id: sessionState.callId,
                  action: "confirmed",
                  pickup: finalPickup,
                  destination: finalDestination,
                  fare: pendingQuote.fare,
                  eta: pendingQuote.eta,
                  customer_name: sessionState.customerName,
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
            sessionState.lastBookTaxiSuccessAt = Date.now();
            sessionState.lastConfirmedTripKey = makeTripKey(finalPickup, finalDestination);
            sessionState.pendingQuote = null; // Clear pending quote
            sessionState.quoteRequestedAt = null;
            sessionState.quoteTripKey = null;
            sessionState.lastQuotePromptAt = null; // Reset prompt tracking
            sessionState.lastQuotePromptText = null;
            
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
            if (openaiWs && openaiConnected) {
              // Cancel-Clear-Inject protocol to avoid response collisions
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
                        text: `[SYSTEM: Booking confirmed successfully! Say to the customer: "That's booked for you. Is there anything else I can help you with?" Then WAIT for their response.]`,
                      }],
                    },
                  }));

                  safeResponseCreate(sessionState, "post-confirm-followup");
                }, 350);
              }, 400);
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
                  pickup: pendingQuote.pickup,
                  destination: pendingQuote.destination,
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
          
          // === FIX #1: BLOCK request_quote if a booking was recently confirmed ===
          if (sessionState.lastBookTaxiSuccessAt && Date.now() - sessionState.lastBookTaxiSuccessAt < 60000) {
            console.log(`[${sessionState.callId}] 🔁 Ignoring request_quote within 60s of successful booking (${Date.now() - sessionState.lastBookTaxiSuccessAt}ms ago)`);
            result = {
              success: true,
              already_confirmed: true,
              message: "Booking already confirmed. Ask: 'Is there anything else I can help you with?'"
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
          
          // === MODIFICATION GUARD ===
          // If Ada passed the SAME destination as the existing active booking (stale value),
          // but extraction found a DIFFERENT destination with medium+ confidence, prefer extraction.
          // This catches cases where Ada forgets to update destination in modify flows.
          const activeBookingDest = sessionState.booking?.destination;
          const adaUsedStaleDestination =
            activeBookingDest &&
            normalizeForComparison(adaDestination) === normalizeForComparison(activeBookingDest) &&
            extDestNorm &&
            extDestNorm !== normalizeForComparison(activeBookingDest);

          if (adaUsedStaleDestination) {
            console.log(
              `[${sessionState.callId}] 🔄 MODIFICATION GUARD: Ada used stale destination "${adaDestination}" (same as active booking). Extraction found "${extractedDestination}". Overriding.`
            );
            finalDestination = extractedDestination;
            sourceDiscrepancy = true;
          }
          
          // Same guard for pickup (less common but possible)
          const activeBookingPickup = sessionState.booking?.pickup;
          const adaUsedStalePickup =
            activeBookingPickup &&
            normalizeForComparison(adaPickup) === normalizeForComparison(activeBookingPickup) &&
            extPickupNorm &&
            extPickupNorm !== normalizeForComparison(activeBookingPickup);

          if (adaUsedStalePickup) {
            console.log(
              `[${sessionState.callId}] 🔄 MODIFICATION GUARD: Ada used stale pickup "${adaPickup}" (same as active booking). Extraction found "${extractedPickup}". Overriding.`
            );
            finalPickup = extractedPickup;
            sourceDiscrepancy = true;
          }
          
          // If major discrepancy, prefer extracted (from conversation) over Ada's interpretation
          if (sourceDiscrepancy && (extractedBooking.confidence === "high" || extractedBooking.confidence === "medium")) {
            console.log(`[${sessionState.callId}] 🔄 Using extracted addresses due to discrepancy (confidence: ${extractedBooking.confidence})`);
            if (extractedPickup && !adaUsedStalePickup) finalPickup = extractedPickup;
            if (extractedDestination && !adaUsedStaleDestination) finalDestination = extractedDestination;
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
          
          // Parse bags - extractedBooking.luggage may be a string like "no baggage" or "2 bags"
          let finalBags = 0;
          if (typeof args.bags === 'number') {
            finalBags = args.bags;
          } else if (extractedBooking.luggage) {
            const luggageStr = String(extractedBooking.luggage).toLowerCase();
            if (luggageStr.includes('no') || luggageStr === '0') {
              finalBags = 0;
            } else {
              const match = luggageStr.match(/(\d+)/);
              finalBags = match ? parseInt(match[1], 10) : 0;
            }
          }
          
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
                pickup_time: args.pickup_time || "now",
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

                   const spokenMessage = (fareText && etaText)
                     ? `Hi, your price for the journey is ${fareText} and your driver will be ${etaText}. Do you want me to go ahead and book that?`
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
            passengers: args.passengers || 1,
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
          sessionState.lastBookTaxiSuccessAt = Date.now();
          sessionState.lastConfirmedTripKey = makeTripKey(args.pickup, args.destination);
          console.log(`[${sessionState.callId}] ✅ Booking enforcement: book_taxi succeeded, Ada may now confirm`);
          
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

        case "cancel_booking":
          console.log(`[${sessionState.callId}] 🚫 Cancelling booking`);
          sessionState.hasActiveBooking = false;
          sessionState.booking = { pickup: null, destination: null, passengers: null, bags: null, vehicle_type: null, version: 0 };
          sessionState.pendingQuote = null;
          sessionState.quoteRequestedAt = null;
          sessionState.quoteTripKey = null;
          sessionState.lastQuotePromptAt = null;
          sessionState.lastQuotePromptText = null;
          sessionState.lastConfirmedTripKey = null;
          result = { success: true };
          break;

        case "modify_booking": {
          console.log(`[${sessionState.callId}] ✏️ Modify request from Ada:`, args);
          
          // === GUARD: Allow modify_booking only if there's an existing active booking OR a booking was confirmed ===
          // Active bookings loaded from DB are modifiable even if bookingConfirmedThisTurn is false.
          const hasModifiableBooking =
            sessionState.hasActiveBooking ||
            sessionState.booking.version > 0 ||
            sessionState.lastBookTaxiSuccessAt !== null;

          if (!hasModifiableBooking) {
            console.log(`[${sessionState.callId}] ⚠️ MODIFY BLOCKED: No modifiable booking exists yet (hasActiveBooking=${sessionState.hasActiveBooking}, version=${sessionState.booking.version})`);
            result = {
              success: false,
              error: "no_confirmed_booking",
              message: "There is no active booking to modify yet. Please complete a booking first."
            };
            break;
          }
          
          // If booking state is incomplete (missing pickup or destination), reject modification
          if (!sessionState.booking.pickup || !sessionState.booking.destination) {
            console.log(`[${sessionState.callId}] ⚠️ MODIFY BLOCKED: Booking incomplete - pickup: "${sessionState.booking.pickup}", dest: "${sessionState.booking.destination}"`);
            result = {
              success: false,
              error: "incomplete_booking",
              message: "Please confirm the complete booking with book_taxi before making modifications."
            };
            break;
          }
          
          // Capture previous value BEFORE making changes
          const oldValue = 
            args.field_to_change === "pickup" ? sessionState.booking.pickup :
            args.field_to_change === "destination" ? sessionState.booking.destination :
            args.field_to_change === "passengers" ? sessionState.booking.passengers :
            args.field_to_change === "bags" ? sessionState.booking.bags :
            null;
          
          console.log(`[${sessionState.callId}] 📋 Modifying ${args.field_to_change}: "${oldValue}" → "${args.new_value}"`);
          
          // Apply change to session state IMMEDIATELY (no AI extraction here - book_taxi will validate)
          if (args.field_to_change === "pickup") sessionState.booking.pickup = args.new_value;
          if (args.field_to_change === "destination") sessionState.booking.destination = args.new_value;
          if (args.field_to_change === "passengers") sessionState.booking.passengers = parseInt(args.new_value) || 1;
          if (args.field_to_change === "bags") sessionState.booking.bags = parseInt(args.new_value) || 0;
          
          console.log(`[${sessionState.callId}] ✅ Applied modification. Updated booking:`, sessionState.booking);
          
          // Sync updated pickup/destination to live_calls for dashboard display
          await supabase.from("live_calls").update({
            pickup: sessionState.booking.pickup,
            destination: sessionState.booking.destination,
            passengers: sessionState.booking.passengers || 1,
            updated_at: new Date().toISOString()
          }).eq("call_id", sessionState.callId);
          
          // ALSO update the bookings table with the modification
          // Use activeBookingCallId if available (from resumption), otherwise current call_id, then phone fallback
          const callerPhoneNormMod = normalizePhone(sessionState.phone);
          const bookingCallIdToUpdate = sessionState.activeBookingCallId || sessionState.callId;
          let bookingUpdateError: any = null;
          
          // First try to update by the booking's call_id (most precise)
          const { error: byCallIdError, count: callIdCount } = await supabase
            .from("bookings")
            .update({
              pickup: sessionState.booking.pickup,
              destination: sessionState.booking.destination,
              passengers: sessionState.booking.passengers || 1,
              updated_at: new Date().toISOString()
            })
            .eq("call_id", bookingCallIdToUpdate)
            .in("status", ["confirmed", "dispatched", "active", "pending"]);
          
          // If no rows matched by call_id, fall back to phone lookup (for resumption cases)
          if (!byCallIdError && (callIdCount === 0 || callIdCount === null)) {
            const { error: byPhoneError } = await supabase
              .from("bookings")
              .update({
                pickup: sessionState.booking.pickup,
                destination: sessionState.booking.destination,
                passengers: sessionState.booking.passengers || 1,
                updated_at: new Date().toISOString()
              })
              .eq("caller_phone", callerPhoneNormMod)
              .in("status", ["confirmed", "dispatched", "active", "pending"])
              .order("booked_at", { ascending: false })
              .limit(1);
            
            bookingUpdateError = byPhoneError;
            if (!byPhoneError) {
              console.log(`[${sessionState.callId}] ✅ Bookings table updated (by phone): ${sessionState.booking.pickup} → ${sessionState.booking.destination}`);
            }
          } else {
            bookingUpdateError = byCallIdError;
            if (!byCallIdError) {
              console.log(`[${sessionState.callId}] ✅ Bookings table updated (call_id=${bookingCallIdToUpdate}): ${sessionState.booking.pickup} → ${sessionState.booking.destination}`);
            }
          }
          
          if (bookingUpdateError) {
            console.error(`[${sessionState.callId}] Failed to update bookings table:`, bookingUpdateError);
          }
          
          // ✅ CRITICAL: DO NOT CALL DISPATCH WEBHOOK HERE
          // Instead, force Ada to call book_taxi which sends one clean, up-to-date webhook.
          // This eliminates the double-webhook race condition.
          
          // Increment booking version for each modification
          sessionState.booking.version = (sessionState.booking.version || 1) + 1;
          
          console.log(`[${sessionState.callId}] ✅ Modification applied locally. Ada must now call book_taxi to send webhook.`);
          
          result = { 
            success: true, 
            modified: args.field_to_change, 
            old_value: oldValue,
            new_value: args.new_value,
            current_booking: {
              pickup: sessionState.booking.pickup,
              destination: sessionState.booking.destination,
              passengers: sessionState.booking.passengers,
              bags: sessionState.booking.bags
            },
            message: `Updated ${args.field_to_change} from "${oldValue}" to "${args.new_value}". Now you MUST call book_taxi with confirmation_state: "request_quote" to get the updated fare and ETA for the new route.`
          };
          
          // ✅ Inject system message to force Ada to call book_taxi with updated route
          if (openaiWs) {
            // Cancel any in-flight response before injecting
            if (sessionState.openAiResponseActive) {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
            }
            await new Promise(resolve => setTimeout(resolve, 300));
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: `[SYSTEM: You just modified the booking. The updated route is Pickup="${sessionState.booking.pickup}" Destination="${sessionState.booking.destination}". NOW YOU MUST CALL book_taxi with confirmation_state: "request_quote", pickup: "${sessionState.booking.pickup}", destination: "${sessionState.booking.destination}" TO GET THE UPDATED FARE AND ETA. Do NOT confirm anything until you call this tool and receive the fare from dispatch.]`
                }]
              }
            }));
          }
          
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
          console.log(`[${sessionState.callId}]   Avg transcript delay:   ${m.avgTranscriptDelayMs.toFixed(0)}ms`);
          console.log(`[${sessionState.callId}]   Avg speech duration:    ${m.avgSpeechDurationMs.toFixed(0)}ms`);
          console.log(`[${sessionState.callId}] ═══════════════════════════════════════════════════════════`);
          
          // Immediate flush on call end - capture all transcripts
          immediateFlush(sessionState);
          // Update call status
          await supabase.from("live_calls")
            .update({ status: "completed", ended_at: new Date().toISOString() })
            .eq("call_id", sessionState.callId);
          
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
          // SKIP barge-in when a pending quote is active - we want Ada to finish speaking the fare
          // and let the user's "yes/no" be processed naturally by OpenAI's VAD.
          const pendingQuoteActive = state.pendingQuote && Date.now() - state.pendingQuote.timestamp < 15000;
          
          if (!state.halfDuplex && state.isAdaSpeaking && state.openAiResponseActive && !pendingQuoteActive) {
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
          } else if (pendingQuoteActive && state.isAdaSpeaking) {
            // Log that we're skipping barge-in during fare confirmation window
            // (only log occasionally to avoid spam)
            if (Math.random() < 0.01) {
              console.log(`[${state.callId}] ⏸️ Barge-in disabled during fare confirmation window`);
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
          activeBookingCallId: null,
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
          deferredResponseCreate: false,
          speechStartTime: null,
          speechStopTime: null,
          callEnded: false,
          useRasaAudioProcessing: message.rasa_audio_processing ?? false,
          halfDuplex: message.half_duplex ?? false,
          halfDuplexBuffer: [],
          bookingConfirmedThisTurn: false,
          lastBookTaxiSuccessAt: null,
          lastConfirmedTripKey: null,
          audioVerified: true, // Start verified - only buffer after user confirms addresses
          pendingAudioBuffer: [],
          pendingQuote: null,
          quoteRequestedAt: null,
          quoteTripKey: null,
          lastQuotePromptAt: null,
          lastQuotePromptText: null,
          pendingDispatchEvents: [],
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
            activeBookingCallId: null,
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
            deferredResponseCreate: false,
            speechStartTime: null,
            speechStopTime: null,
            callEnded: false,
            useRasaAudioProcessing: message.rasa_audio_processing ?? false,
            halfDuplex: message.half_duplex ?? false,
            halfDuplexBuffer: [],
            bookingConfirmedThisTurn: false,
            lastBookTaxiSuccessAt: null,
            lastConfirmedTripKey: null,
            audioVerified: true, // Start verified - only buffer after user confirms addresses
            pendingAudioBuffer: [],
            pendingQuote: null,
            quoteRequestedAt: null,
            quoteTripKey: null,
            lastQuotePromptAt: null,
            lastQuotePromptText: null,
            pendingDispatchEvents: [],
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

        // === FAST CALLER LOOKUP (before greeting) ===
        // This is a quick lookup to get the caller's name BEFORE we greet them
        // Full booking/GPS lookup still happens in background
        if (phone && phone !== "unknown" && !state!.customerName) {
          try {
            const phoneKey = normalizePhone(phone);
            console.log(`[${callId}] 🔍 Fast caller lookup for: raw=${phone}, normalized=${phoneKey}`);
            const { data: callerData } = await supabase
              .from("callers")
              .select("name, last_pickup, last_destination, total_bookings")
              .eq("phone_number", phoneKey)
              .maybeSingle();
            
            if (callerData) {
              state!.customerName = callerData.name || null;
              state!.callerLastPickup = callerData.last_pickup || null;
              state!.callerLastDestination = callerData.last_destination || null;
              state!.callerTotalBookings = callerData.total_bookings || 0;
              console.log(`[${callId}] 👤 Fast lookup found: ${callerData.name || 'no name'}, ${state!.callerTotalBookings} bookings`);
            }
          } catch (e) {
            console.error(`[${callId}] Fast caller lookup failed:`, e);
            // Continue without name - will ask for it
          }
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

        // Subscribe to dispatch broadcast channel for ask_confirm, say, etc.
        const dispatchChannel = supabase.channel(`dispatch_${callId}`);
        
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

          const spokenMessage = (fareText && etaText)
            ? `Hi, your price for the journey is ${fareText} and your driver will be ${etaText}. Do you want me to go ahead and book that?`
            : (message ? String(message) : "");

          console.log(`[${callId}] 📥 DISPATCH ask_confirm received: "${message}" → spoken="${spokenMessage}"`);

          if (!spokenMessage || !openaiWs || !openaiConnected) {
            console.log(`[${callId}] ⚠️ Cannot process ask_confirm - no message or OpenAI not connected`);
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

          // GUARD 2: If booking was confirmed recently (within 60s), also ignore
          // Extended from 30s to 60s to match the book_taxi("request_quote") guard
          if (state?.lastBookTaxiSuccessAt && Date.now() - state.lastBookTaxiSuccessAt < 60000) {
            console.log(`[${callId}] ⚠️ Ignoring ask_confirm - booking was confirmed ${Date.now() - state.lastBookTaxiSuccessAt}ms ago`);
            return;
          }

          // GUARD 3: If there's already a pending quote OR a recent prompt was injected, ignore duplicate
          if (state?.pendingQuote) {
            const timeSinceLastAsk = Date.now() - (state.pendingQuote.timestamp || 0);
            if (timeSinceLastAsk < 10000) { // Within 10 seconds = duplicate
              console.log(`[${callId}] ⚠️ Ignoring duplicate ask_confirm - pendingQuote exists (${timeSinceLastAsk}ms ago)`);
              return;
            }
          }

          // GUARD 4: If tool handler already injected a fare prompt very recently (within 3s), skip
          if (state?.lastQuotePromptAt && Date.now() - state.lastQuotePromptAt < 3000) {
            console.log(`[${callId}] ⚠️ Ignoring ask_confirm - fare prompt already injected ${Date.now() - state.lastQuotePromptAt}ms ago by tool handler`);
            return;
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
          }

          // Determine language instruction based on session
          const langCode = state?.language || "en";
          const langName = langCode === "nl" ? "Dutch" : langCode === "de" ? "German" : langCode === "fr" ? "French" : langCode === "es" ? "Spanish" : langCode === "it" ? "Italian" : langCode === "pl" ? "Polish" : "English";

          // HARD BLOCK: even if book_taxi succeeded earlier, do NOT allow Ada to confirm a booking
          // during fare confirmation. She must wait for the customer's yes/no.
          if (state) state.bookingConfirmedThisTurn = false;

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
          
          if (!sayMessage || !openaiWs || !openaiConnected) {
            console.log(`[${callId}] ⚠️ Cannot process dispatch say - no message or OpenAI not connected`);
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
        
        dispatchChannel.on("broadcast", { event: "dispatch_hangup" }, async () => {
          console.log(`[${callId}] 📥 DISPATCH hangup received`);
          if (state) state.callEnded = true;
          socket.send(JSON.stringify({ type: "hangup", reason: "dispatch_requested" }));
        });
        
        // Handle dispatch confirmation after user accepted fare
        dispatchChannel.on("broadcast", { event: "dispatch_confirm" }, async (payload: any) => {
          const { confirmation_message, fare, eta, eta_minutes, booking_ref, driver_name, vehicle_reg, status } = payload.payload || {};
          console.log(`[${callId}] 📥 DISPATCH confirm received: "${confirmation_message}"`);
          
          if (!openaiWs || !openaiConnected) {
            console.log(`[${callId}] ⚠️ Cannot process confirm - OpenAI not connected`);
            return;
          }
          
          // Set booking confirmed state
          if (state) {
            state.bookingConfirmedThisTurn = true;
            state.pendingQuote = null; // Clear quote
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
          
          // Cancel any active response before injecting confirmation
          if (state?.openAiResponseActive) {
            openaiWs.send(JSON.stringify({ type: "response.cancel" }));
          }
          openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          
          // Inject the confirmation for Ada to speak
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: `[DISPATCH CONFIRMATION]: The booking has been confirmed. Say this to the customer IN ${langName.toUpperCase()}: "${messageToSpeak}. Is there anything else I can help you with?" Be natural and brief.` }]
            }
          }));
          
          // Trigger Ada to speak
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: {
              modalities: ["audio", "text"],
              instructions: `IMPORTANT: The dispatch has confirmed the booking. Speak in ${langName}. Say: "${messageToSpeak}. Is there anything else I can help you with?" Do not add extra details.`
            }
          }));
        });
        
        dispatchChannel.subscribe((status) => {
          console.log(`[${callId}] 📡 Dispatch channel status: ${status}`);
        });

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
                console.log(`[${callId}] 👤 Loaded caller: ${callerData.name || 'no name'}, ${state.callerTotalBookings} bookings`);
              }
              
              // Check for active bookings if not already loaded
              if (!activeBooking) {
                // Try both phone formats (normalized digits-only + legacy no-plus)
                const { data: bookingData } = await supabase
                  .from("bookings")
                  .select("call_id, pickup, destination, passengers, fare, eta, status, booked_at, caller_name")
                  .or(`caller_phone.eq.${phoneKey},caller_phone.eq.${altPhone}`)
                  .in("status", ["confirmed", "dispatched", "active", "pending"])
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
              const phoneKey = normalizePhone(phone);
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
