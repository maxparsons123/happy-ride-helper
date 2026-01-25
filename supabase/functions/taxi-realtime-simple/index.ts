import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
const DEMO_SIMPLE_MODE = Deno.env.get("DEMO_SIMPLE_MODE") === "true"; // Set to true to force demo journey

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === DISPATCH WEBHOOK ===
interface DispatchPayload {
  call_id: string;
  caller_phone: string;
  action: "request_quote" | "confirmed" | "cancelled";
  pickup: string;
  destination: string;
  passengers?: number;
  pickup_time?: string;
  locale?: string;
  currency?: string;
}

interface DispatchResponse {
  success: boolean;
  fare?: string;
  eta_minutes?: number;
  booking_ref?: string;
  message?: string;
  error?: string;
}

async function sendDispatchWebhook(
  payload: DispatchPayload,
  log: (msg: string) => void
): Promise<DispatchResponse> {
  if (!DISPATCH_WEBHOOK_URL) {
    log("⚠️ DISPATCH_WEBHOOK_URL not configured, skipping webhook");
    return { success: false, error: "Webhook not configured" };
  }

  try {
    log(`📤 Sending dispatch webhook: ${payload.action}`);
    log(`   Payload: ${JSON.stringify(payload)}`);

    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    if (!response.ok) {
      const errorText = await response.text();
      log(`❌ Dispatch webhook failed: ${response.status} - ${errorText}`);
      return { success: false, error: `HTTP ${response.status}` };
    }

    const result = await response.json() as DispatchResponse;
    log(`📥 Dispatch response: ${JSON.stringify(result)}`);
    return result;
  } catch (error) {
    log(`❌ Dispatch webhook error: ${error}`);
    return { success: false, error: String(error) };
  }
}
// === LOCALE DETECTION ===
interface CallerLocale {
  code: string;
  language: string;
  greeting: string;
  taxiWord: string;
  currency: string;
  currencySymbol: string;
}

const LOCALE_MAP: Record<string, CallerLocale> = {
  "+44": { code: "GB", language: "English", greeting: "Hello", taxiWord: "taxi", currency: "GBP", currencySymbol: "£" },
  "+1": { code: "US", language: "English", greeting: "Hello", taxiWord: "cab", currency: "USD", currencySymbol: "$" },
  "+33": { code: "FR", language: "French", greeting: "Bonjour", taxiWord: "taxi", currency: "EUR", currencySymbol: "€" },
  "+34": { code: "ES", language: "Spanish", greeting: "Hola", taxiWord: "taxi", currency: "EUR", currencySymbol: "€" },
  "+49": { code: "DE", language: "German", greeting: "Hallo", taxiWord: "Taxi", currency: "EUR", currencySymbol: "€" },
  "+39": { code: "IT", language: "Italian", greeting: "Ciao", taxiWord: "taxi", currency: "EUR", currencySymbol: "€" },
  "+48": { code: "PL", language: "Polish", greeting: "Cześć", taxiWord: "taksówka", currency: "PLN", currencySymbol: "zł" },
  "+31": { code: "NL", language: "Dutch", greeting: "Hallo", taxiWord: "taxi", currency: "EUR", currencySymbol: "€" },
  "+351": { code: "PT", language: "Portuguese", greeting: "Olá", taxiWord: "táxi", currency: "EUR", currencySymbol: "€" },
  "+353": { code: "IE", language: "English", greeting: "Hello", taxiWord: "taxi", currency: "EUR", currencySymbol: "€" },
  "+46": { code: "SE", language: "Swedish", greeting: "Hej", taxiWord: "taxi", currency: "SEK", currencySymbol: "kr" },
  "+47": { code: "NO", language: "Norwegian", greeting: "Hei", taxiWord: "taxi", currency: "NOK", currencySymbol: "kr" },
  "+45": { code: "DK", language: "Danish", greeting: "Hej", taxiWord: "taxa", currency: "DKK", currencySymbol: "kr" },
  "+358": { code: "FI", language: "Finnish", greeting: "Hei", taxiWord: "taksi", currency: "EUR", currencySymbol: "€" },
  "+61": { code: "AU", language: "English", greeting: "G'day", taxiWord: "taxi", currency: "AUD", currencySymbol: "$" },
  "+64": { code: "NZ", language: "English", greeting: "Hello", taxiWord: "taxi", currency: "NZD", currencySymbol: "$" },
  "+81": { code: "JP", language: "Japanese", greeting: "こんにちは", taxiWord: "タクシー", currency: "JPY", currencySymbol: "¥" },
  "+86": { code: "CN", language: "Chinese", greeting: "你好", taxiWord: "出租车", currency: "CNY", currencySymbol: "¥" },
  "+91": { code: "IN", language: "Hindi", greeting: "नमस्ते", taxiWord: "taxi", currency: "INR", currencySymbol: "₹" },
  "+971": { code: "AE", language: "Arabic", greeting: "مرحبا", taxiWord: "taxi", currency: "AED", currencySymbol: "د.إ" },
};

const DEFAULT_LOCALE: CallerLocale = { 
  code: "GB", language: "English", greeting: "Hello", taxiWord: "taxi", currency: "GBP", currencySymbol: "£" 
};

function detectLocaleFromPhone(phone: string | undefined): CallerLocale {
  if (!phone) return DEFAULT_LOCALE;
  
  // Try longest prefixes first (e.g., +353 before +33)
  const sortedPrefixes = Object.keys(LOCALE_MAP).sort((a, b) => b.length - a.length);
  for (const prefix of sortedPrefixes) {
    if (phone.startsWith(prefix)) {
      return LOCALE_MAP[prefix];
    }
  }
  return DEFAULT_LOCALE;
}

// === STT CORRECTIONS ===
const STT_CORRECTIONS: [RegExp, string][] = [
  // Alphanumeric house numbers - join number + letter
  [/(\d+)\s*[-–]\s*([a-zA-Z])\b/g, "$1$2"],  // "52-A" or "52 - A" → "52A"
  [/(\d+)\s+([a-zA-Z])\b/g, "$1$2"],          // "52 A" → "52A"
  [/(\d+)\s*hey\b/gi, "$1A"],                 // "52 hey" → "52A"
  [/(\d+)\s*a\b/gi, "$1A"],                   // "52 a" → "52A"
  [/(\d+)\s*be\b/gi, "$1B"],                  // "7 be" → "7B"
  [/(\d+)\s*bee\b/gi, "$1B"],                 // "7 bee" → "7B"
  [/(\d+)\s*see\b/gi, "$1C"],                 // "14 see" → "14C"
  
  // Common street name mishearings
  [/\bDavid Rose\b/gi, "David Road"],
  [/\bDavid Rohn\b/gi, "David Road"],
  [/\bRussel\b/gi, "Russell"],
  [/\bRoswell\b/gi, "Russell"],
];

function applySTTCorrections(text: string): string {
  let corrected = text;
  for (const [pattern, replacement] of STT_CORRECTIONS) {
    corrected = corrected.replace(pattern, replacement);
  }
  return corrected;
}

// === PASSENGER PARSING ===
function parsePassengers(text: string): number {
  const lower = text.toLowerCase().trim();
  const num = parseInt(lower);
  if (!isNaN(num) && num > 0 && num <= 10) return num;
  
  const words: Record<string, number> = {
    "one": 1, "two": 2, "to": 2, "too": 2, "three": 3, "tree": 3,
    "four": 4, "for": 4, "five": 5, "six": 6, "seven": 7, "eight": 8,
    "nine": 9, "ten": 10,
    // Spanish
    "uno": 1, "dos": 2, "tres": 3, "cuatro": 4, "cinco": 5,
    // French  
    "un": 1, "deux": 2, "trois": 3, "quatre": 4, "cinq": 5,
    // German
    "eins": 1, "zwei": 2, "drei": 3, "vier": 4, "fünf": 5,
    // Polish
    "jeden": 1, "dwa": 2, "trzy": 3, "cztery": 4, "pięć": 5,
  };
  
  for (const [word, val] of Object.entries(words)) {
    if (lower.includes(word)) return val;
  }
  return 0;
}

// === SYSTEM PROMPT BUILDER ===
function buildSystemPrompt(locale: CallerLocale): string {
  return `
# IDENTITY
You are ADA, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual.

# SPEECH PACING
- Speak at a SLOW, RELAXED pace
- Insert natural pauses between sentences
- Pronounce addresses clearly

# MULTILINGUAL SUPPORT (CRITICAL)
- Your initial greeting is in English, but IMMEDIATELY detect the caller's language from their first response
- If they speak Spanish, switch to Spanish for ALL subsequent responses
- If they speak French, switch to French for ALL subsequent responses
- If they speak Polish, switch to Polish for ALL subsequent responses
- If they speak German, switch to German for ALL subsequent responses
- If they speak any other language, respond in THAT language
- Once you detect their language, ALL questions, confirmations, and summaries must be in their language
- NEVER switch back to English unless they speak English

# CALLER LOCALE INFO
- Detected region: ${locale.code}
- Native greeting: "${locale.greeting}"
- Local word for taxi: "${locale.taxiWord}"
- Currency: ${locale.currency} (${locale.currencySymbol})

# ONE QUESTION RULE
Ask ONLY ONE question per response. NEVER combine questions.

# GREETING (Say this FIRST when call starts)
"Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"

# BOOKING FLOW (Ask ONE at a time, in order - NO mid-flow recaps)
1. Get pickup location → just ask "Where would you like to go?" (or equivalent in their language)
2. Get destination → just ask "How many passengers?" (or equivalent in their language)
3. Get number of passengers → ask "When would you like the taxi?" (or equivalent in their language)
4. Get pickup time (default: now/ASAP)
5. Summarize booking and ask for confirmation (IN THEIR LANGUAGE)
6. If confirmed, say "Your taxi is booked! You'll receive updates via WhatsApp. Have a safe journey!" (IN THEIR LANGUAGE)

# ADDRESS INTERPRETATION (STRICT)
- "52-8", "52 A", or "52 hey" MUST be interpreted as "52A".
- ALPHANUMERIC SUFFIXES ARE PART OF THE NUMBER: 52A, 14B, 7C.
- NEVER strip the letter. If you hear a letter after a number, it is a house suffix.
- If the user says "52A" and your internal transcript says "52", you MUST prioritize the alphanumeric version "52A".
- DO NOT NORMALIZE: "Flat 4, 52A David Road" must not become "52 David Road".

# ZERO-PARAPHRASE RULE (CRITICAL - MOST IMPORTANT)
- When the customer gives you an address - you MUST use it EXACTLY as heard in your summary
- NEVER substitute, invent, or "correct" what they said
- If they say "52A David Road" - your summary says "52A David Road" (NOT "32A", NOT "52", NOT "28")
- House numbers are SACRED - NEVER alter or hallucinate them
- If unsure, ask: "Could you repeat that address for me?" (in their language)

# COMMON WHISPER MISHEARINGS (Important)
These are how the speech recognition often hears addresses - interpret them correctly:
- "52-8" or "52 8" or "52 hey" or "fifty two a" = 52A
- "32-8" or "32 8" or "32 hey" = 32A  
- "7-8" or "7 8" = 7A
- "David Rose" or "David Rohn" = David Road
- "Russel" or "Roswell" = Russell

# PASSENGERS (ANTI-STUCK RULE)
- Only move past the passengers step if the caller clearly provides a passenger count.
- Accept digits (e.g. "3") or clear number words in ANY language (uno, dos, tres, un, deux, trois, eins, zwei, drei, etc.)
- Also accept common telephony homophones: "to/too" → two, "for" → four, "tree" → three.
- If the caller says something that sounds like an address/place while you are asking for passengers, DO NOT advance.
- Instead, repeat exactly: "How many people will be travelling?" (in their language)

# CORRECTIONS & CHANGES
When the caller wants to change or correct something:
- Listen for correction words in any language: "actually", "no wait", "change", "I meant", "sorry, it's", "en realidad", "non", "nein", etc.
- IMMEDIATELY update with the new information
- Acknowledge briefly: "Updated to [new value]." (in their language) then continue
- NEVER ignore corrections

# RULES
- Do NOT say "Got it" or "Great" or repeat addresses before asking the next question
- After each answer, immediately ask the NEXT question (no recaps)
- Only summarize at the end before confirmation
- In the summary, use the EXACT addresses the caller gave you - never invent or alter
- ALWAYS respond in the caller's detected language after their first response
`;
}

// === AUDIO HELPERS ===

// Upsample 8kHz to 24kHz (linear interpolation)
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
  let callerPhone: string | undefined;
  let callerLocale: CallerLocale = DEFAULT_LOCALE;
  
  // === USER TRUTH TRACKING ===
  let currentStep: "greeting" | "pickup" | "destination" | "passengers" | "time" | "summary" | "done" = "greeting";
  const lastUserTruth = {
    pickup: "",
    destination: "",
    passengers: 1,
    time: "ASAP",
  };
  
  // === BOOKING STATE ===
  const bookingState = {
    pickup: "",
    destination: "",
    passengers: 1,
    pickup_time: "now",
    confirmed: false,
    fare: "",
    eta_minutes: 0,
    booking_ref: "",
  };

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);
  
  // === TOOL CALL HANDLER ===
  const handleToolCall = async (toolName: string, args: Record<string, unknown>, toolCallId: string) => {
    log(`🔧 Tool call: ${toolName} with args: ${JSON.stringify(args)}`);
    
    if (toolName === "book_taxi") {
      const action = args.action as string;
      
      // Update booking state from args
      if (args.pickup) bookingState.pickup = String(args.pickup);
      if (args.destination) bookingState.destination = String(args.destination);
      if (args.passengers) bookingState.passengers = Number(args.passengers);
      if (args.pickup_time) bookingState.pickup_time = String(args.pickup_time);
      
      // Use user truth if available (more accurate)
      if (lastUserTruth.pickup) bookingState.pickup = lastUserTruth.pickup;
      if (lastUserTruth.destination) bookingState.destination = lastUserTruth.destination;
      
      log(`📋 Booking state: ${JSON.stringify(bookingState)}`);
      
      if (action === "request_quote") {
        // Send webhook to get fare quote
        const result = await sendDispatchWebhook({
          call_id: callId,
          caller_phone: callerPhone || "",
          action: "request_quote",
          pickup: bookingState.pickup,
          destination: bookingState.destination,
          passengers: bookingState.passengers,
          pickup_time: bookingState.pickup_time,
          locale: callerLocale.code,
          currency: callerLocale.currency,
        }, log);
        
        if (result.success && result.fare) {
          bookingState.fare = result.fare;
          bookingState.eta_minutes = result.eta_minutes || 0;
          
          // Return fare to Ada
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: toolCallId,
              output: JSON.stringify({
                success: true,
                fare: result.fare,
                eta_minutes: result.eta_minutes,
                message: `Fare is ${result.fare}, ETA ${result.eta_minutes} minutes`,
              }),
            },
          }));
        } else {
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: toolCallId,
              output: JSON.stringify({
                success: false,
                message: "Unable to get fare quote at this time",
              }),
            },
          }));
        }
        
        // Trigger Ada to speak the result
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
        
      } else if (action === "confirmed") {
        // Send final booking confirmation
        const result = await sendDispatchWebhook({
          call_id: callId,
          caller_phone: callerPhone || "",
          action: "confirmed",
          pickup: bookingState.pickup,
          destination: bookingState.destination,
          passengers: bookingState.passengers,
          pickup_time: bookingState.pickup_time,
          locale: callerLocale.code,
          currency: callerLocale.currency,
        }, log);
        
        bookingState.confirmed = true;
        if (result.booking_ref) bookingState.booking_ref = result.booking_ref;
        
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "function_call_output",
            call_id: toolCallId,
            output: JSON.stringify({
              success: result.success,
              booking_ref: result.booking_ref || "DEMO-" + Date.now(),
              message: "Booking confirmed",
            }),
          },
        }));
        
        // Trigger Ada to speak confirmation
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }
    }
  };

  // Connect to OpenAI Realtime
  const connectOpenAI = () => {
    log("🔌 Connecting to OpenAI...");
    log(`🌍 Caller locale: ${callerLocale.code} (${callerLocale.language})`);
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("✅ OpenAI connected, configuring session...");
    };

    openaiWs.onmessage = (event) => {
      const msg = JSON.parse(event.data);

      // Session created - configure and trigger greeting
      if (msg.type === "session.created") {
        log("📋 Session created, sending config");
        
        const systemPrompt = buildSystemPrompt(callerLocale);
        
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            instructions: systemPrompt,
            voice: "shimmer",
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            turn_detection: {
              type: "server_vad",
              threshold: 0.5,
              prefix_padding_ms: 300,
              silence_duration_ms: 800
            },
            tools: [
              {
                type: "function",
                name: "book_taxi",
                description: "Book a taxi or request a fare quote. Use action='request_quote' after user confirms the summary to get fare. Use action='confirmed' after user accepts the fare.",
                parameters: {
                  type: "object",
                  properties: {
                    action: {
                      type: "string",
                      enum: ["request_quote", "confirmed"],
                      description: "request_quote = get fare after summary confirmed. confirmed = final booking after fare accepted."
                    },
                    pickup: {
                      type: "string",
                      description: "EXACT pickup address as spoken by user. Include house numbers with letters (e.g., 52A)."
                    },
                    destination: {
                      type: "string",
                      description: "EXACT destination address as spoken by user. Include house numbers with letters."
                    },
                    passengers: {
                      type: "number",
                      description: "Number of passengers"
                    },
                    pickup_time: {
                      type: "string",
                      description: "Pickup time. Default 'now' for ASAP."
                    }
                  },
                  required: ["action", "pickup", "destination"]
                }
              }
            ],
            tool_choice: "auto"
          }
        }));
      }

      // Session configured - trigger greeting or demo summary
      if (msg.type === "session.updated") {
        if (DEMO_SIMPLE_MODE && currentStep === "summary") {
          // Demo mode: Skip to summary with hardcoded journey
          log("🎭 DEMO MODE: Injecting summary");
          const summary = `Alright, let me quickly summarize your booking. You'd like to be picked up at ${lastUserTruth.pickup}, and travel to ${lastUserTruth.destination}. There will be ${lastUserTruth.passengers} person, and you'd like to be picked up ${lastUserTruth.time}. Is that correct?`;
          
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "assistant",
              content: [{ type: "input_text", text: summary }]
            }
          }));
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
        } else {
          log("🎤 Session configured, triggering greeting");
          currentStep = "pickup"; // After greeting, we're asking for pickup
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
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

      // Log Ada's responses and track step progression
      if (msg.type === "response.audio_transcript.done") {
        const adaText = msg.transcript || "";
        log(`🗣️ Ada: ${adaText}`);
        
        // Detect step progression from Ada's questions (multilingual keywords)
        const lower = adaText.toLowerCase();
        // English + Spanish + French + German + Polish destination keywords
        if (lower.includes("where would you like to go") || lower.includes("destination") ||
            lower.includes("adónde") || lower.includes("où voulez-vous aller") || 
            lower.includes("wohin") || lower.includes("dokąd")) {
          currentStep = "destination";
        // Passenger keywords
        } else if (lower.includes("how many") || lower.includes("passengers") ||
                   lower.includes("cuántos") || lower.includes("pasajeros") ||
                   lower.includes("combien") || lower.includes("passagers") ||
                   lower.includes("wie viele") || lower.includes("ilu")) {
          currentStep = "passengers";
        // Time keywords
        } else if (lower.includes("when would you") || lower.includes("pickup time") ||
                   lower.includes("cuándo") || lower.includes("quelle heure") ||
                   lower.includes("wann") || lower.includes("kiedy")) {
          currentStep = "time";
        // Summary keywords
        } else if (lower.includes("confirm") || lower.includes("summary") ||
                   lower.includes("confirmar") || lower.includes("resumen") ||
                   lower.includes("confirmer") || lower.includes("résumé") ||
                   lower.includes("bestätigen") || lower.includes("potwierdzić")) {
          currentStep = "summary";
        // Booking done keywords
        } else if (lower.includes("taxi is booked") || lower.includes("safe journey") ||
                   lower.includes("taxi está reservado") || lower.includes("buen viaje") ||
                   lower.includes("taxi est réservé") || lower.includes("bon voyage") ||
                   lower.includes("taxi ist gebucht") || lower.includes("gute fahrt") ||
                   lower.includes("taksówka jest zamówiona") || lower.includes("szczęśliwej podróży")) {
          currentStep = "done";
        }
      }

      // === ENHANCED USER TRUTH CAPTURE ===
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const raw = msg.transcript || "";
        const corrected = applySTTCorrections(raw);
        
        // Regex to find numbers followed by letters (e.g., 52A, 7B)
        const houseNumberPattern = /(\d+[a-zA-Z])\b/g;
        const matches = corrected.match(houseNumberPattern);

        log(`👤 User (raw): ${raw}`);
        if (corrected !== raw) {
          log(`👤 User (corrected): ${corrected}`);
        }

        // === CONFIRMATION DETECTION ===
        if (currentStep === "summary") {
          const lower = corrected.toLowerCase();
          
          // Lenient affirmative detection (handles typos like "yesy", "okkk")
          const looksLikeYes = /^(y+e+s+|y+e+a+h*|y+u+p+|y+e+p+|sure|correct|right|absolutely|definitely|perfect)/i.test(lower.trim());
          const looksLikeOk = /^(o+k+a*y*|go\s*ahead|book\s*(it|the|taxi)?|please|confirm)/i.test(lower.trim());
          const isAffirmative = looksLikeYes || looksLikeOk || lower.includes("that's correct") || lower.includes("sounds good");
          
          if (isAffirmative) {
            log(`✅ Confirmation detected: "${corrected}"`);
            
            // Inject system message forcing Ada to call book_taxi
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: `[USER CONFIRMED BOOKING] The user has confirmed the booking. You MUST now call the book_taxi tool with action="request_quote" to get the fare. Use these EXACT values:
- pickup: "${lastUserTruth.pickup || bookingState.pickup}"
- destination: "${lastUserTruth.destination || bookingState.destination}"
- passengers: ${lastUserTruth.passengers || bookingState.passengers}
- pickup_time: "${lastUserTruth.time || bookingState.pickup_time}"

Say "One moment please while I get your fare" then call the tool.`
                }]
              }
            }));
            openaiWs?.send(JSON.stringify({ type: "response.create" }));
            return; // Don't process further
          }
          
          // Check for negation
          const looksLikeNo = /^(no+|nope|wrong|incorrect|change|actually|wait)/i.test(lower.trim());
          if (looksLikeNo) {
            log(`❌ Negation detected: "${corrected}"`);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: "[USER WANTS CHANGES] The user said no to the summary. Ask them: 'What would you like to change?' Do NOT repeat the full summary."
                }]
              }
            }));
            openaiWs?.send(JSON.stringify({ type: "response.create" }));
            return;
          }
        }

        // Capture user truth based on current step
        if (currentStep === "pickup") {
          lastUserTruth.pickup = corrected;
          if (matches) log(`🏠 Found Alphanumeric Pickup Number: ${matches.join(', ')}`);
        } else if (currentStep === "destination") {
          lastUserTruth.destination = corrected;
          if (matches) log(`🏠 Found Alphanumeric Destination Number: ${matches.join(', ')}`);
        } else if (currentStep === "passengers") {
          const pax = parsePassengers(corrected);
          if (pax > 0) {
            lastUserTruth.passengers = pax;
            log(`👥 Passengers: ${pax}`);
          }
        } else if (currentStep === "time") {
          lastUserTruth.time = corrected;
          log(`⏰ Time: ${corrected}`);
        }
        
        // Log current user truth state
        log(`📋 User Truth: pickup="${lastUserTruth.pickup}" dest="${lastUserTruth.destination}" pax=${lastUserTruth.passengers} time="${lastUserTruth.time}"`);
      }

      // === TOOL CALL HANDLING ===
      if (msg.type === "response.function_call_arguments.done") {
        const toolName = msg.name;
        const toolCallId = msg.call_id;
        try {
          const args = JSON.parse(msg.arguments || "{}");
          handleToolCall(toolName, args, toolCallId);
        } catch (e) {
          log(`❌ Failed to parse tool arguments: ${e}`);
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
    // Binary audio from bridge (8kHz slin)
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState === WebSocket.OPEN) {
        const pcm8k = event.data instanceof ArrayBuffer 
          ? new Uint8Array(event.data) 
          : event.data;
        
        // Upsample to 24kHz and send to OpenAI
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
        callerPhone = msg.caller_phone || msg.caller_id;
        callerLocale = detectLocaleFromPhone(callerPhone);
        
        log(`📞 Call initialized from ${callerPhone || "unknown"}`);
        log(`🌍 Detected locale: ${callerLocale.code} (${callerLocale.language})`);
        
        // === DEMO MODE: Pre-fill with hardcoded journey ===
        if (DEMO_SIMPLE_MODE) {
          log("🎭 DEMO MODE: Using hardcoded journey for Max");
          lastUserTruth.pickup = "52A David Road";
          lastUserTruth.destination = "The Cozy Club";
          lastUserTruth.passengers = 1;
          lastUserTruth.time = "ASAP";
          bookingState.pickup = lastUserTruth.pickup;
          bookingState.destination = lastUserTruth.destination;
          bookingState.passengers = lastUserTruth.passengers;
          bookingState.pickup_time = lastUserTruth.time;
          currentStep = "summary";
          
          // Connect OpenAI, then after session is ready, inject summary
          connectOpenAI();
          socket.send(JSON.stringify({ type: "ready" }));
          return;
        }
        
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        log(`📋 Final User Truth: pickup="${lastUserTruth.pickup}" dest="${lastUserTruth.destination}" pax=${lastUserTruth.passengers}`);
        openaiWs?.close();
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
