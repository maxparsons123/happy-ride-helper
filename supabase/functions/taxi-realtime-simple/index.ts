import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === BOOKING STATE ===
interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: string | null;
  time: string | null;
  currentStep: "pickup" | "destination" | "passengers" | "time" | "summary" | "done";
  lastUserTranscript: string | null;
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

# MULTILINGUAL SUPPORT (CRITICAL)
- ALWAYS respond in the SAME LANGUAGE the caller is speaking
- If they speak Spanish, respond entirely in Spanish
- If they speak French, respond entirely in French
- If they speak Polish, respond entirely in Polish
- If they switch languages mid-call, switch with them
- Adapt greetings, questions, and confirmations to match their language
- You are fluent in ALL languages - never say you cannot speak a language

# ONE QUESTION RULE
Ask ONLY ONE question per response. NEVER combine questions.

# GREETING (Say this FIRST when call starts)
Start in English: "Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"
If caller responds in another language, IMMEDIATELY switch to that language for all future responses.

# BOOKING FLOW (Ask ONE at a time, in order)
1. Get pickup location
2. Get destination  
3. Get number of passengers
4. Get pickup time (default: now/ASAP)
5. Summarize booking and ask for confirmation
6. If confirmed, say "Your taxi is booked! You'll receive updates via WhatsApp. Have a safe journey!"

# PASSENGERS (ANTI-STUCK RULE)
- Only move past the passengers step if the caller clearly provides a passenger count.
- Accept digits (e.g. "3") or clear number words (one, two, three, four, five, six, seven, eight, nine, ten).
- Also accept common telephony homophones: "to/too" → two, "for" → four, "tree" → three.
- If the caller says something that sounds like an address/place (street/road/avenue/hotel/etc.) while you are asking for passengers, DO NOT advance.
- Instead, repeat exactly: "How many people will be travelling?"

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

// === STATE HELPERS ===

function detectStepFromAdaTranscript(transcript: string): BookingState["currentStep"] | null {
  const lower = transcript.toLowerCase();
  
  if (/where would you like to be picked up|pickup (location|address)|pick you up/i.test(lower)) {
    return "pickup";
  }
  if (/where (would you like to go|are you going|is your destination)|destination/i.test(lower)) {
    return "destination";
  }
  if (/how many (people|passengers)|travelling/i.test(lower)) {
    return "passengers";
  }
  if (/when would you like|pickup time|what time|now or later/i.test(lower)) {
    return "time";
  }
  if (/let me confirm|to confirm|summary|picking you up from/i.test(lower)) {
    return "summary";
  }
  if (/taxi is booked|safe journey|whatsapp/i.test(lower)) {
    return "done";
  }
  return null;
}

function extractPassengerCount(text: string): string | null {
  const lower = text.toLowerCase().trim();
  
  // Number words
  const wordMap: Record<string, string> = {
    "one": "1", "two": "2", "to": "2", "too": "2",
    "three": "3", "tree": "3", "free": "3",
    "four": "4", "for": "4", "five": "5",
    "six": "6", "seven": "7", "eight": "8",
    "nine": "9", "ten": "10"
  };
  
  for (const [word, num] of Object.entries(wordMap)) {
    if (lower === word || lower.startsWith(word + " ")) {
      return num;
    }
  }
  
  // Digits
  const digitMatch = lower.match(/^(\d+)/);
  if (digitMatch) {
    return digitMatch[1];
  }
  
  // "just me" / "myself"
  if (/just me|myself|alone|only me/.test(lower)) {
    return "1";
  }
  
  return null;
}

function extractTime(text: string): string | null {
  const lower = text.toLowerCase().trim();
  
  if (/now|asap|as soon as|right now|straight away|immediately/.test(lower)) {
    return "now";
  }
  
  // Match times like "3pm", "3:30", "15:00"
  const timeMatch = lower.match(/(\d{1,2}(?::\d{2})?\s*(?:am|pm)?)/i);
  if (timeMatch) {
    return timeMatch[1];
  }
  
  // "in X minutes"
  const inMinutes = lower.match(/in (\d+) minutes?/);
  if (inMinutes) {
    return `in ${inMinutes[1]} minutes`;
  }
  
  return null;
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
  
  // Booking state tracking
  const bookingState: BookingState = {
    pickup: null,
    destination: null,
    passengers: null,
    time: null,
    currentStep: "pickup",
    lastUserTranscript: null
  };

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

  // Inject verified booking state before summary
  const injectVerifiedState = () => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    
    const stateMsg = `[VERIFIED BOOKING DATA - USE THESE EXACT VALUES]
Pickup: ${bookingState.pickup || "NOT PROVIDED"}
Destination: ${bookingState.destination || "NOT PROVIDED"}
Passengers: ${bookingState.passengers || "NOT PROVIDED"}
Time: ${bookingState.time || "now"}

CRITICAL: When summarizing, use ONLY the values above. Do not invent or hallucinate any addresses.`;

    log(`📋 Injecting verified state: P=${bookingState.pickup}, D=${bookingState.destination}, Pax=${bookingState.passengers}, T=${bookingState.time}`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "system",
        content: [{ type: "input_text", text: stateMsg }]
      }
    }));
  };

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

      // Track Ada's questions to know current step
      if (msg.type === "response.audio_transcript.done") {
        const transcript = msg.transcript || "";
        log(`🗣️ Ada: ${transcript}`);
        
        const detectedStep = detectStepFromAdaTranscript(transcript);
        if (detectedStep) {
          const previousStep = bookingState.currentStep;
          bookingState.currentStep = detectedStep;
          log(`📍 Step detected: ${previousStep} → ${detectedStep}`);
          
          // If moving to summary, inject verified state
          if (detectedStep === "summary" && previousStep !== "summary") {
            injectVerifiedState();
          }
        }
      }

      // Capture user responses and map to current step
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const transcript = msg.transcript || "";
        log(`👤 User: ${transcript}`);
        bookingState.lastUserTranscript = transcript;
        
        // Map response to current step
        switch (bookingState.currentStep) {
          case "pickup":
            if (transcript.trim().length > 2) {
              bookingState.pickup = transcript.trim();
              log(`✅ Saved pickup: ${bookingState.pickup}`);
            }
            break;
            
          case "destination":
            if (transcript.trim().length > 2) {
              bookingState.destination = transcript.trim();
              log(`✅ Saved destination: ${bookingState.destination}`);
            }
            break;
            
          case "passengers":
            const pax = extractPassengerCount(transcript);
            if (pax) {
              bookingState.passengers = pax;
              log(`✅ Saved passengers: ${bookingState.passengers}`);
            }
            break;
            
          case "time":
            const time = extractTime(transcript);
            if (time) {
              bookingState.time = time;
              log(`✅ Saved time: ${bookingState.time}`);
            } else if (transcript.trim().length > 0) {
              // Default to "now" if they say anything
              bookingState.time = "now";
              log(`✅ Saved time (default): now`);
            }
            break;
        }
        
        // Log current state
        log(`📊 State: P=${bookingState.pickup || "?"} | D=${bookingState.destination || "?"} | Pax=${bookingState.passengers || "?"} | T=${bookingState.time || "?"}`);
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
        openaiWs?.close();
      }
      
      if (msg.type === "ping") {
        socket.send(JSON.stringify({ type: "pong" }));
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
