import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

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
  };

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

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
            }
          }
        }));
      }

      // Session configured - trigger greeting
      if (msg.type === "session.updated") {
        log("🎤 Session configured, triggering greeting");
        currentStep = "pickup"; // After greeting, we're asking for pickup
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
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

        if (currentStep === "pickup") {
          lastUserTruth.pickup = corrected;
          if (matches) log(`🏠 Found Alphanumeric Pickup Number: ${matches.join(', ')}`);
        } else if (currentStep === "destination") {
          lastUserTruth.destination = corrected;
          if (matches) log(`🏠 Found Alphanumeric Destination Number: ${matches.join(', ')}`);
        }
        
        // Log current user truth state
        if (lastUserTruth.pickup || lastUserTruth.destination) {
          log(`📋 User Truth: pickup="${lastUserTruth.pickup}" destination="${lastUserTruth.destination}"`);
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
        
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        log(`📋 Final User Truth: pickup="${lastUserTruth.pickup}" destination="${lastUserTruth.destination}"`);
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
