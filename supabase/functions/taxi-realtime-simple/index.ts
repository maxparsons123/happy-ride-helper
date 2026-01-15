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

PERSONALITY: Warm, patient, relaxed. Speak in 1–2 short sentences. Ask ONLY ONE question at a time.

GREETING (ALWAYS IN THE CURRENT LANGUAGE):
- New caller: Welcome them to {{company_name}}, introduce yourself as {{agent_name}}, and ask their name.
- Returning caller (no booking): Greet [NAME] and ask where they want pickup.
- Returning caller (active booking): Greet [NAME], mention an active booking, ask keep/change/cancel.
Example Dutch (nl) new caller: "Hallo, welkom bij {{company_name}}! Ik ben {{agent_name}}. Hoe heet u?"

LOCATION CHECK (IF GPS REQUIRED):
- If you receive "[SYSTEM: GPS not available]" at the start, you MUST ask: "Where are you right now?" or "What's your current location?"
- When they tell you (e.g., "I'm at the train station", "I'm on High Street near Tesco") → CALL save_location function with their answer.
- Wait for save_location result before proceeding with booking flow.
- If save_location fails, apologize and ask them to try the app with location services enabled.

BOOKING FLOW (STRICT ORDER):
1. Get PICKUP address FIRST. Ask: "Where would you like to be picked up from?"
2. Get DESTINATION address SECOND. Ask: "And where are you going to?"
3. Get PASSENGERS if not mentioned. Ask: "How many passengers?"
4. ONLY if destination is an AIRPORT or TRAIN STATION: ask "Any bags?" Otherwise SKIP bags entirely.
5. When ALL details are known → Say "Let me check your booking" then CALL book_taxi function. WAIT for the result before speaking again.

CRITICAL TOOL USAGE - READ CAREFULLY:
- When you have pickup, destination, and passengers → Say "Let me check your booking" then CALL book_taxi function.
- The book_taxi function will return fare and ETA from dispatch. You MUST WAIT for the result.
- If the result contains "ada_message" → SPEAK THAT MESSAGE EXACTLY to the customer. This is dispatch telling you something important (e.g., address issue).
- If the result contains "needs_clarification: true" → Ask the customer the question in ada_message, then wait for their answer.
- If the result contains "rejected: true" → Tell the customer we cannot process their booking using the ada_message.
- If the result contains "hangup: true" → Say the ada_message EXACTLY then IMMEDIATELY call end_call. Do NOT ask any follow-up questions.
- If the result contains "success: true" → Say: "Booked! [ETA] minutes, [FARE]. Anything else?"
- DO NOT make up fares or ETAs. You MUST use the values returned by book_taxi.
- If user says "cancel" → CALL cancel_booking function FIRST, then respond.
- If user corrects name → CALL save_customer_name function immediately.
- Call end_call function after saying "Safe travels!".

BOOKING MODIFICATIONS - TWO-STEP CONFIRMATION REQUIRED:
⚠️ CHANGES REQUIRE USER CONFIRMATION BEFORE WEBHOOK IS SENT.

STEP 1 - ASK FOR CONFIRMATION:
- If customer wants to change PICKUP → Ask: "Change pickup to [NEW ADDRESS]?" and WAIT for confirmation.
- If customer wants to change DESTINATION → Ask: "Change destination to [NEW ADDRESS]?" and WAIT for confirmation.
- If customer wants to change PASSENGERS → Ask: "Update to [NUMBER] passengers?" and WAIT for confirmation.
- If customer wants to change BAGS → Ask: "Update to [NUMBER] bags?" and WAIT for confirmation.

STEP 2 - AFTER USER CONFIRMS (says yes/yeah/correct/that's right/go ahead):
- ONLY THEN call modify_booking(field_to_change: "[FIELD]", new_value: "[VALUE]").
- This triggers a WEBHOOK to the dispatch system with the confirmed change.
- After calling modify_booking, confirm: "Updated! Your pickup is now [NEW ADDRESS]."

⚠️ CRITICAL RULES:
- NEVER call modify_booking before user confirms the change.
- NEVER skip the confirmation step.
- If user says "no" or rejects the change, ask what they'd like instead.
- If user says "no" AND provides a different address/value (even if they say it like "change destination to ..."), treat that as the new value and ask for confirmation again.
- If you still can’t understand the address after 2 tries, ask them to spell the street name (and the house number if relevant).
- NEVER cancel and rebook. ALWAYS use modify_booking to preserve booking history.
- If booking was already confirmed and user wants changes, confirm first, then call modify_booking, then call book_taxi again to get updated fare.

RULES:
1. ALWAYS ask for PICKUP before DESTINATION. Never assume or swap them.
2. NEVER repeat addresses, fares, or full routes.
3. NEVER say: "Just to double-check", "Shall I book that?", "Is that correct?".
4. GLOBAL service — accept any address.
5. If "usual trip" → summarize last trip, ask "Shall I book that again?" → wait for YES.
6. DO NOT ask about bags for cities. Only ask for airports or train stations.

IMPORTANT: If user says "going TO [address]" that is DESTINATION, not pickup.
If user says "from [address]" or "pick me up at [address]" that is PICKUP.

TURN-TAKING AWARENESS:
When the user finishes speaking, look for:
- Complete sentences ending with punctuation or natural pauses
- Completion phrases like "that's all", "thanks", "bye", "please", "yes", "no", "okay"
- Clear questions or requests that warrant a response
- Pauses after completing a thought

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
    description: "Book taxi. CALL IMMEDIATELY when details known. Include 'bags' for airport trips.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string" },
        destination: { type: "string" },
        passengers: { type: "integer", minimum: 1 },
        bags: { type: "integer", minimum: 0 },
        pickup_time: { type: "string", description: "ISO timestamp or 'now'" },
        vehicle_type: { type: "string", enum: ["saloon", "estate", "mpv", "minibus"] }
      },
      required: ["pickup", "destination", "passengers"]
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

  // Echo guard: track when Ada is speaking to ignore audio feedback
  isAdaSpeaking: boolean;
  echoGuardUntil: number; // timestamp until which to ignore audio

  // Speech timing diagnostics
  speechStartTime: number | null;
  speechStopTime: number | null;

  // Call ended flag - prevents further processing after end_call
  callEnded: boolean;
  
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
        prompt += `\n\nCURRENT CONTEXT: Caller is ${sessionState.customerName} with active booking.`;
      } else {
        prompt += `\n\nCURRENT CONTEXT: Caller is ${sessionState.customerName} (returning).`;
      }
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
          threshold: 0.6,           // Higher threshold to avoid triggering on soft sounds
          prefix_padding_ms: 500,   // More padding before speech starts (captures full words)
          silence_duration_ms: 1500 // Wait 1.5s of silence before responding (was 1200ms)
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

      case "response.audio.delta":
        // Mark Ada as speaking (echo guard)
        sessionState.isAdaSpeaking = true;
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
        console.log(`[${sessionState.callId}] 🔇 Speech stopped after ${speechDuration}ms - VAD will wait 1500ms before responding`);
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
        case "save_customer_name":
          console.log(`[${sessionState.callId}] 👤 Saving name: ${args.name}`);
          sessionState.customerName = args.name;
          result = { success: true };
          break;

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
                // Bias towards UK if we have phone hint
                region: sessionState.phone?.startsWith("+44") ? "uk" : undefined
              })
            });
            
            if (geocodeResponse.ok) {
              const geocodeData = await geocodeResponse.json();
              
              if (geocodeData.lat && geocodeData.lon) {
                sessionState.gpsLat = geocodeData.lat;
                sessionState.gpsLon = geocodeData.lon;
                
                console.log(`[${sessionState.callId}] ✅ Location geocoded: ${geocodeData.lat}, ${geocodeData.lon}`);
                
                // Save to caller_gps table
                await supabase.from("caller_gps").upsert({
                  phone_number: sessionState.phone,
                  lat: geocodeData.lat,
                  lon: geocodeData.lon,
                  expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString() // 1 hour
                }, { onConflict: "phone_number" });
                
                // Update live_calls with GPS
                await supabase.from("live_calls").update({
                  gps_lat: geocodeData.lat,
                  gps_lon: geocodeData.lon,
                  gps_updated_at: new Date().toISOString()
                }).eq("call_id", sessionState.callId);
                
                result = { 
                  success: true, 
                  message: `Location saved: ${geocodeData.formatted_address || args.location}`,
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
          console.log(`[${sessionState.callId}] 🚕 Booking:`, args);
          sessionState.booking = {
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers,
            bags: args.bags || 0,
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
          
          if (DISPATCH_WEBHOOK_URL) {
            try {
              console.log(`[${sessionState.callId}] 📡 Calling dispatch webhook: ${DISPATCH_WEBHOOK_URL}`);
              console.log(`[${sessionState.callId}] ⏳ Sending booking to dispatch, will poll for callback response...`);
              // Get recent user transcripts as structured array for comparison
              const userTranscripts = sessionState.transcripts
                .filter(t => t.role === "user")
                .slice(-6) // Last 6 user messages
                .map(t => ({ text: t.text, timestamp: t.timestamp }));
              
              const webhookPayload = {
                job_id: jobId,
                call_id: sessionState.callId,
                caller_phone: sessionState.phone,
                caller_name: sessionState.customerName,
                // Ada's interpreted addresses
                ada_pickup: args.pickup,
                ada_destination: args.destination,
                // Raw STT transcripts from this call - each turn separately
                user_transcripts: userTranscripts,
                // GPS location (if available)
                gps_lat: sessionState.gpsLat,
                gps_lon: sessionState.gpsLon,
                // Booking details
                passengers: args.passengers || 1,
                bags: args.bags || 0,
                vehicle_type: args.vehicle_type || "saloon",
                pickup_time: args.pickup_time || "now",
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
                
                // Check if dispatch set fare OR eta OR status changed to dispatched (confirm action)
                // More flexible: accept confirmation if ANY confirmation signal is present
                const hasConfirmation = callData?.fare || callData?.eta || callData?.status === "dispatched";
                if (hasConfirmation) {
                  console.log(`[${sessionState.callId}] ✅ Dispatch confirmed: fare=${callData?.fare}, eta=${callData?.eta}, status=${callData?.status}`);
                  dispatchResult = {
                    fare: callData?.fare || null,
                    eta_minutes: parseInt(callData?.eta) || 8,
                    confirmed: true
                  };
                  break;
                }
                
                // Check if dispatch sent a "say" message (look for dispatch transcript)
                const transcripts = callData?.transcripts as any[] || [];
                
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
          
          result = { 
            success: true, 
            eta_minutes: etaMinutes, 
            fare: fare,
            booking_ref: bookingRef,
            message: "Booking confirmed"
          };
          break;
        }

        case "cancel_booking":
          console.log(`[${sessionState.callId}] 🚫 Cancelling booking`);
          sessionState.hasActiveBooking = false;
          sessionState.booking = { pickup: null, destination: null, passengers: null, bags: null, version: 0 };
          result = { success: true };
          break;

        case "modify_booking": {
          console.log(`[${sessionState.callId}] ✏️ Modifying:`, args);
          
          // Capture previous booking state BEFORE making changes
          const previousBooking = {
            pickup: sessionState.booking.pickup,
            destination: sessionState.booking.destination,
            passengers: sessionState.booking.passengers,
            bags: sessionState.booking.bags,
            version: sessionState.booking.version
          };
          
          const oldValue = args.field_to_change === "pickup" ? sessionState.booking.pickup
            : args.field_to_change === "destination" ? sessionState.booking.destination
            : args.field_to_change === "passengers" ? sessionState.booking.passengers
            : args.field_to_change === "bags" ? sessionState.booking.bags
            : null;
          
          // Apply the changes
          if (args.field_to_change === "pickup") sessionState.booking.pickup = args.new_value;
          if (args.field_to_change === "destination") sessionState.booking.destination = args.new_value;
          if (args.field_to_change === "passengers") sessionState.booking.passengers = parseInt(args.new_value);
          if (args.field_to_change === "bags") sessionState.booking.bags = parseInt(args.new_value);
          
          // Send webhook with modification details and poll for updated fare
          const DISPATCH_MODIFY_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
          let updatedFare: string | null = null;
          let updatedEta: string | null = null;
          
          if (DISPATCH_MODIFY_URL) {
            // Increment booking version for each modification
            sessionState.booking.version = (sessionState.booking.version || 1) + 1;
            
            // Get user transcripts for STT reference (like book_taxi does)
            const userTranscripts = sessionState.transcripts
              .filter(t => t.role === "user")
              .slice(-5)
              .map(t => t.text);
            
            const modifyPayload = {
              event: "booking_modified",
              call_id: sessionState.callId,
              caller_phone: sessionState.phone,
              caller_name: sessionState.customerName,
              // What changed (for quick detection)
              field_changed: args.field_to_change,
              old_value: oldValue,
              new_value: args.new_value,
              // Full PREVIOUS booking state (before change)
              previous_booking: previousBooking,
              // Full CURRENT booking state (after change) - matches book_taxi structure
              ada_pickup: sessionState.booking.pickup,
              ada_destination: sessionState.booking.destination,
              passengers: sessionState.booking.passengers,
              bags: sessionState.booking.bags,
              // Raw STT transcripts for reference
              user_transcripts: userTranscripts,
              // Version tracking
              booking_version: sessionState.booking.version,
              timestamp: new Date().toISOString()
            };
            
            console.log(`[${sessionState.callId}] 📡 Sending booking modification webhook:`, modifyPayload);
            
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
            new_value: args.new_value,
            ...(updatedFare && { fare: updatedFare }),
            ...(updatedEta && { eta: updatedEta }),
            message: updatedFare 
              ? `Updated ${args.field_to_change}. New fare: ${updatedFare}`
              : `Updated ${args.field_to_change} to ${args.new_value}`
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
          // ECHO GUARD: Skip audio while Ada is speaking or in guard window
          if (state.isAdaSpeaking || Date.now() < state.echoGuardUntil) {
            // Silently discard audio that could be Ada's echo
            return;
          }

          let audioData: Uint8Array;
          
          if (event.data instanceof Blob) {
            audioData = new Uint8Array(await event.data.arrayBuffer());
          } else if (event.data instanceof ArrayBuffer) {
            audioData = new Uint8Array(event.data);
          } else {
            audioData = event.data;
          }
          
          // Bridge sends 8kHz µ-law, need to convert to 24kHz PCM16 for OpenAI
          // NOTE: OpenAI Realtime API only accepts 24kHz - Rasa mode flag is for logging/metrics only
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
          
          // Step 2: Upsample 8kHz -> 24kHz (3x linear interpolation)
          // OpenAI Realtime API requires 24kHz PCM16 - cannot use 16kHz
          const pcm16_24k = new Int16Array(pcm16_8k.length * 3);
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
          booking: { pickup: null, destination: null, passengers: null, bags: null, version: 0 },
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
          speechStartTime: null,
          speechStopTime: null,
          callEnded: false,
          useRasaAudioProcessing: message.rasa_audio_processing ?? false,
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
        connectToOpenAI(state, false); // Connect but DON'T trigger greeting yet
        
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
            booking: { pickup: null, destination: null, passengers: null, bags: null, version: 0 },
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
            speechStartTime: null,
            speechStopTime: null,
            callEnded: false,
            useRasaAudioProcessing: message.rasa_audio_processing ?? false,
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
        
        // Also update useRasaAudioProcessing from init message if pre-connected
        if (state && preConnected) {
          state.useRasaAudioProcessing = message.rasa_audio_processing ?? false;
        }
        
        console.log(`[${callId}] 🎧 Audio processing: ${state!.useRasaAudioProcessing ? 'Rasa-style (8→16kHz)' : 'Standard (8→24kHz)'}`);
        
        console.log(`[${callId}] 🌐 Phone: ${phone}, Detected: ${detectedLanguage}, Final language: ${state!.language}`);

        // If pre-connected, OpenAI is already ready - just send session update + greeting
        if (preConnected && openaiConnected) {
          console.log(`[${callId}] ⚡ OpenAI already connected - triggering greeting immediately!`);
          sendSessionUpdate(state);
        } else {
          // Connect to OpenAI IMMEDIATELY - don't wait for DB lookup
          pendingGreeting = true;
          connectToOpenAI(state);
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
              } else if (state?.gpsRequired && !state?.gpsLat) {
                // NO GPS and it's required - ask Ada to request location
                console.log(`[${callId}] ⚠️ GPS REQUIRED but not found - Ada will ask for location`);

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
                    type: "gps_required",
                    message: "GPS location not received - Ada will ask for location",
                  })
                );
              }
            }

            if (phone && phone !== "unknown") {
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
                console.log(`[${callId}] 👤 Loaded caller history: ${state.callerTotalBookings} bookings`);
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
        // ECHO GUARD: Skip audio while Ada is speaking or in guard window
        if (state.isAdaSpeaking || Date.now() < state.echoGuardUntil) {
          return;
        }
        
        // Bridge sends base64-encoded 8kHz µ-law audio via JSON
        const binaryStr = atob(message.audio);
        const audioData = new Uint8Array(binaryStr.length);
        for (let i = 0; i < binaryStr.length; i++) {
          audioData[i] = binaryStr.charCodeAt(i);
        }
        
        // Step 1: Decode µ-law to 16-bit PCM (8kHz)
        // This matches Rasa's audioop.ulaw2lin(chunk, 2) conversion
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
        
        let outputBytes: Uint8Array;
        
        if (state.useRasaAudioProcessing) {
          // RASA-STYLE: Upsample 8kHz -> 16kHz (2x linear interpolation)
          // This matches audioop.ratecv(linear_data, 2, 1, 8000, 16000, None)
          // 16kHz is commonly used for speech recognition and may improve STT accuracy
          const pcm16_16k = new Int16Array(pcm16_8k.length * 2);
          for (let i = 0; i < pcm16_8k.length - 1; i++) {
            const s0 = pcm16_8k[i];
            const s1 = pcm16_8k[i + 1];
            pcm16_16k[i * 2] = s0;
            pcm16_16k[i * 2 + 1] = Math.round((s0 + s1) / 2);
          }
          // Handle last sample
          const lastIdx = pcm16_8k.length - 1;
          pcm16_16k[lastIdx * 2] = pcm16_8k[lastIdx];
          pcm16_16k[lastIdx * 2 + 1] = pcm16_8k[lastIdx];
          
          outputBytes = new Uint8Array(pcm16_16k.buffer);
        } else {
          // STANDARD: Upsample 8kHz -> 24kHz (3x linear interpolation)
          // OpenAI Realtime API expects 24kHz PCM16
          const pcm16_24k = new Int16Array(pcm16_8k.length * 3);
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
          
          outputBytes = new Uint8Array(pcm16_24k.buffer);
        }
        
        // Convert to base64
        let binary = "";
        for (let i = 0; i < outputBytes.length; i++) {
          binary += String.fromCharCode(outputBytes[i]);
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
