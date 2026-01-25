import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === STT CORRECTIONS ===
// Fix common Whisper mishearings for alphanumeric addresses
const STT_CORRECTIONS: Record<string, string> = {
  // 52A variations
  "52-8": "52A", "52 8": "52A", "528": "52A", "52 a": "52A",
  "52-a": "52A", "fifty two a": "52A", "fifty-two a": "52A",
  "52 hey": "52A", "52 eh": "52A", "52 age": "52A",
  // 7A variations
  "7-8": "7A", "7 8": "7A", "78": "7A", "7 a": "7A",
  "seven a": "7A", "7-a": "7A",
  // Common road name mishearings
  "david rohn": "David Road", "david rhone": "David Road",
  "roswell": "Russell", "russel": "Russell",
};

function applySTTCorrections(text: string): string {
  let corrected = text;
  for (const [wrong, right] of Object.entries(STT_CORRECTIONS)) {
    const regex = new RegExp(wrong, "gi");
    corrected = corrected.replace(regex, right);
  }
  // Also join numbers with trailing letters: "52 A" → "52A"
  corrected = corrected.replace(/(\d+)\s+([A-Za-z])(?=\s|$)/g, "$1$2");
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

# BOOKING FLOW (Ask ONE at a time, in order)
1. Get pickup location
2. Get destination
3. Get number of passengers
4. Get pickup time (default: now/ASAP)
5. Summarize booking and ask for confirmation
6. If confirmed, say "Your taxi is booked! You'll receive updates via WhatsApp. Have a safe journey!"

# PASSENGERS (ANTI-STUCK RULE)
- When asking for passengers, say: "How many passengers will be travelling?" (encourages fuller response)
- Only move past the passengers step if the caller clearly provides a passenger count.
- Accept digits (e.g. "3") or clear number words (one, two, three, four, five, six, seven, eight, nine, ten).
- Also accept common telephony homophones: "to/too" → two, "for" → four, "tree/free/the/there" → three.
- NUMBER LOGIC: If the user says "tree", "free", "the", or "there" during the passenger count, interpret it as the number 3.
- Always look for phonetic matches for numbers 1-10.
- If the caller says something that sounds like an address/place (street/road/avenue/hotel/etc.) while you are asking for passengers, DO NOT advance.
- Instead, repeat exactly: "How many passengers will be travelling?"

# ADDRESS INTERPRETATION (CRITICAL)
- When you hear a number followed by a letter sound (like "52 A" or "52-8"), treat it as an alphanumeric house number (52A).
- Common mishearings: "52-8" means "52A", "7-8" means "7A", etc.
- Always preserve the full house number including any letter suffix.

# CORRECTIONS & CHANGES (CRITICAL)
When the caller wants to change or correct something they said:
- Listen for: "actually", "no wait", "change", "I meant", "not X, it's Y", "sorry, it's", "let me correct"
- IMMEDIATELY update your understanding with the new information
- Acknowledge briefly: "Updated to [new value]." then continue the flow
- If they correct during the summary, say "Let me update that" and give a NEW summary with the corrected info
- NEVER ignore corrections - always act on them

# RULES
- Do NOT say "Got it" or "Great" before asking the next question
- Do NOT repeat or confirm individual answers mid-flow
- After each answer, immediately ask the NEXT question
- Only summarize at the end before confirmation
- If caller says "no" to the summary, ask "What would you like to change?"
`;

// === AUDIO HELPERS ===

// Upsample 8kHz to 24kHz (linear interpolation) - NO DSP
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

// === STEP-AWARE CONTEXT HINTS ===
type BookingStep = "pickup" | "destination" | "passengers" | "time" | "summary" | "done" | "unknown";

function detectStepFromAdaTranscript(transcript: string): BookingStep {
  const lower = transcript.toLowerCase();
  if (/where would you like to be picked up|pickup (location|address)|pick you up/i.test(lower)) return "pickup";
  if (/where (would you like to go|are you going|is your destination)|heading to/i.test(lower)) return "destination";
  if (/how many (people|passengers)|travelling/i.test(lower)) return "passengers";
  if (/when would you like|pickup time|what time|now or later/i.test(lower)) return "time";
  if (/let me confirm|to confirm|summary|picking you up from/i.test(lower)) return "summary";
  if (/taxi is booked|safe journey|whatsapp/i.test(lower)) return "done";
  return "unknown";
}

function getContextHintForStep(step: BookingStep): string | null {
  switch (step) {
    case "pickup":
      return "[CONTEXT: User is providing their PICKUP ADDRESS. Listen for street names, house numbers, landmarks.]";
    case "destination":
      return "[CONTEXT: User is providing their DESTINATION ADDRESS. Listen for street names, house numbers, landmarks.]";
    case "passengers":
      return "[CONTEXT: User is providing PASSENGER COUNT. Interpret number words: 'tree/free/the/there' = 3, 'to/too' = 2, 'for' = 4. Accept 1-10.]";
    case "time":
      return "[CONTEXT: User is providing PICKUP TIME. 'now/asap/straight away' = immediately. Listen for times like '3pm' or 'in 10 minutes'.]";
    case "summary":
      return "[CONTEXT: User is CONFIRMING or CORRECTING the booking. 'yes/yeah/correct' = confirmed. 'no/change/actually' = needs correction.]";
    default:
      return null;
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
  
  // Step tracking
  let currentStep: BookingStep = "pickup";
  let stepAtSpeechStart: BookingStep = "pickup";
  let contextInjected = false;

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
              threshold: 0.3,          // Lowered to catch quiet "th" sounds
              prefix_padding_ms: 600,  // Extra padding to catch word onset
              silence_duration_ms: 1000 // Wait 1 second before responding
            }
          }
        }));
      }

      // Session configured - trigger greeting
      if (msg.type === "session.updated") {
        log("🎤 Session configured, triggering greeting");
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }

      // Track Ada's question to know what step we're on
      if (msg.type === "response.audio_transcript.done") {
        const adaText = msg.transcript || "";
        log(`🗣️ Ada: ${adaText}`);
        
        const detected = detectStepFromAdaTranscript(adaText);
        if (detected !== "unknown") {
          currentStep = detected;
          log(`📍 Step detected: ${currentStep}`);
        }
        contextInjected = false; // Reset for next user turn
      }

      // User speech started - inject context hint
      if (msg.type === "input_audio_buffer.speech_started") {
        stepAtSpeechStart = currentStep;
        log(`🎙️ Speech started (step: ${stepAtSpeechStart})`);
        
        // Inject context hint for this step
        if (!contextInjected) {
          const hint = getContextHintForStep(stepAtSpeechStart);
          if (hint) {
            log(`💡 Injecting context: ${hint}`);
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

      // Log user transcript with STT corrections
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const raw = msg.transcript;
        const corrected = applySTTCorrections(raw);
        if (raw !== corrected) {
          log(`👤 User: ${raw} → [STT FIX] ${corrected}`);
        } else {
          log(`👤 User: ${raw}`);
        }
      }

      // Forward audio to bridge (raw 24kHz PCM16)
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

        // Upsample to 24kHz and send to OpenAI (no DSP)
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
