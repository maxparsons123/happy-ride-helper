import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

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

# GREETING (Say this FIRST when call starts)
"Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"

# BOOKING FLOW (Ask ONE at a time, in order - NO mid-flow recaps)
1. Get pickup location → just ask "Where would you like to go?"
2. Get destination → just ask "How many passengers?"  
3. Get number of passengers → ask "When would you like the taxi?"
4. Get pickup time (default: now/ASAP)
5. Summarize booking and ask for confirmation
6. If confirmed, say "Your taxi is booked! You'll receive updates via WhatsApp. Have a safe journey!"

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
- If unsure, ask: "Could you repeat that address for me?"

# COMMON WHISPER MISHEARINGS (Important)
These are how the speech recognition often hears addresses - interpret them correctly:
- "52-8" or "52 8" or "52 hey" or "fifty two a" = 52A
- "32-8" or "32 8" or "32 hey" = 32A  
- "7-8" or "7 8" = 7A
- "David Rose" or "David Rohn" = David Road
- "Russel" or "Roswell" = Russell

# PASSENGERS (ANTI-STUCK RULE)
- Only move past the passengers step if the caller clearly provides a passenger count.
- Accept digits (e.g. "3") or clear number words (one, two, three, four, five, six, seven, eight, nine, ten).
- Also accept common telephony homophones: "to/too" → two, "for" → four, "tree" → three.
- If the caller says something that sounds like an address/place while you are asking for passengers, DO NOT advance.
- Instead, repeat exactly: "How many people will be travelling?"

# CORRECTIONS & CHANGES
When the caller wants to change or correct something:
- Listen for: "actually", "no wait", "change", "I meant", "sorry, it's"
- IMMEDIATELY update with the new information
- Acknowledge briefly: "Updated to [new value]." then continue
- NEVER ignore corrections

# RULES
- Do NOT say "Got it" or "Great" or repeat addresses before asking the next question
- After each answer, immediately ask the NEXT question (no recaps)
- Only summarize at the end before confirmation
- In the summary, use the EXACT addresses the caller gave you - never invent or alter
`;

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
        
        // Detect step progression from Ada's questions
        const lower = adaText.toLowerCase();
        if (lower.includes("where would you like to go") || lower.includes("destination")) {
          currentStep = "destination";
        } else if (lower.includes("how many") || lower.includes("passengers")) {
          currentStep = "passengers";
        } else if (lower.includes("when would you") || lower.includes("pickup time")) {
          currentStep = "time";
        } else if (lower.includes("confirm") || lower.includes("summary")) {
          currentStep = "summary";
        } else if (lower.includes("taxi is booked") || lower.includes("safe journey")) {
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
        log(`📞 Call initialized`);
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
