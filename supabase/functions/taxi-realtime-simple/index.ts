import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

if (!OPENAI_API_KEY) {
  console.warn("⚠️ OPENAI_API_KEY missing – TTS/STT will fail");
}

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

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

// === STATE MANAGEMENT ===
interface CallState {
  callId: string;
  step: "pickup" | "destination" | "passengers" | "time" | "summary" | "fare_confirm" | "done";
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  time: string | null;
  currentStepValidated: boolean;
  farewellSent: boolean;
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
  let state: CallState | null = null;

  const log = (msg: string) => console.log(`[${state?.callId || "unknown"}] ${msg}`);

  // Connect to OpenAI Realtime
  const connectOpenAI = () => {
    log("🔌 Connecting to OpenAI...");
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("✅ OpenAI connected, configuring session...");
      
      openaiWs?.send(JSON.stringify({
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          instructions: SYSTEM_PROMPT,
          voice: "shimmer",
          input_audio_format: "pcm16",
          output_audio_format: "pcm16",
          input_audio_transcription: { model: "whisper-1" },
          // 🔑 Keep VAD for natural pacing, but enforce validation
          turn_detection: {
            type: "server_vad",
            threshold: 0.5,
            prefix_padding_ms: 300,
            silence_duration_ms: 800
          }
        }
      }));
    };

    openaiWs.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data);

        // Session configured - trigger greeting
        if (msg.type === "session.updated") {
          log("🎤 Session configured, triggering greeting");
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
        }

        // Handle user transcription
        if (msg.type === "conversation.item.input_audio_transcription.completed") {
          const rawText = msg.transcript?.trim();
          if (!rawText || rawText.length < 2) return;

          log(`👤 User: "${rawText}"`);

          // Filter out dispatch echoes
          const lower = rawText.toLowerCase();
          if (/driver will be|price.*is.*\d+|book that/.test(lower)) return;

          if (!state) return;

          // Handle corrections first
          if (/actually|no wait|change|I meant|not .+ it's|sorry, it's|let me correct/i.test(lower)) {
            handleCorrection(rawText, state);
            return;
          }

          // Skip validation if we're in summary/fare_confirm (handled by Ada's logic)
          if (state.step === "summary" || state.step === "fare_confirm") {
            // Let Ada handle these naturally
            return;
          }

          // Validate response for current step
          if (!state.currentStepValidated) {
            const isValid = validateResponseForStep(rawText, state.step);
            
            if (isValid) {
              updateStateWithResponse(rawText, state.step, state);
              state.currentStepValidated = true;
              log(`✅ ${state.step} validated: ${rawText}`);
              
              // Move to next step
              advanceToNextStep(state);
              log(`➡️ Advanced to: ${state.step}`);
            } else {
              // Re-ask the same question
              log(`❌ Invalid response for ${state.step}, re-asking`);
              reAskCurrentQuestion(state);
              return; // Prevent Ada from auto-responding
            }
          }
        }

        // Log Ada's speech
        if (msg.type === "response.audio_transcript.done") {
          log(`🗣️ Ada: ${msg.transcript}`);
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

        // Log errors
        if (msg.type === "error") {
          log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
        }
      } catch (e) {
        log(`⚠️ Error processing OpenAI message: ${e}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI disconnected");
  };

  // === HELPER FUNCTIONS ===
  function parsePassengers(text: string): number {
    const lower = text.toLowerCase().trim();
    
    // Direct numbers
    const numMatch = lower.match(/\d+/);
    if (numMatch) {
      const num = parseInt(numMatch[0]);
      if (num > 0 && num <= 10) return num;
    }
    
    // Number words and homophones
    const words: Record<string, number> = {
      "one": 1, "won": 1,
      "two": 2, "to": 2, "too": 2,
      "three": 3, "tree": 3, "free": 3,
      "four": 4, "for": 4, "fore": 4,
      "five": 5, "hive": 5,
      "six": 6, "sex": 6,
      "seven": 7,
      "eight": 8, "ate": 8,
      "nine": 9,
      "ten": 10
    };
    
    for (const [word, val] of Object.entries(words)) {
      if (lower.includes(word)) return val;
    }
    
    // Check for "just me" or "myself"
    if (/just me|myself|alone|solo/i.test(lower)) return 1;
    
    return 0;
  }

  function validateResponseForStep(text: string, step: CallState["step"]): boolean {
    const lower = text.toLowerCase();
    
    switch (step) {
      case "pickup":
      case "destination":
        // Must be at least 4 chars and not sound like passenger count
        return text.length > 3 && !/how many|passenger|people/i.test(lower);
      case "passengers":
        // Must parse to a valid passenger count
        // Also reject if it looks like an address
        if (/street|road|avenue|lane|drive|way|hotel|airport/i.test(lower)) {
          return false;
        }
        return parsePassengers(text) > 0;
      case "time":
        return true; // Accept anything for time
      default:
        return false;
    }
  }

  function updateStateWithResponse(text: string, step: CallState["step"], state: CallState) {
    switch (step) {
      case "pickup": 
        state.pickup = text; 
        break;
      case "destination": 
        state.destination = text; 
        break;
      case "passengers": 
        state.passengers = parsePassengers(text); 
        break;
      case "time": 
        state.time = text; 
        break;
    }
  }

  function advanceToNextStep(state: CallState) {
    const steps: CallState["step"][] = ["pickup", "destination", "passengers", "time", "summary"];
    const currentIndex = steps.indexOf(state.step);
    if (currentIndex < steps.length - 1) {
      state.step = steps[currentIndex + 1];
      state.currentStepValidated = false;
    }
  }

  function reAskCurrentQuestion(state: CallState) {
    let question = "";
    switch (state.step) {
      case "pickup": question = "Where would you like to be picked up?"; break;
      case "destination": question = "What is your destination?"; break;
      case "passengers": question = "How many people will be travelling?"; break;
      case "time": question = "When do you need the taxi?"; break;
    }
    
    if (question) {
      openaiWs?.send(JSON.stringify({
        type: "conversation.item.create",
        item: { type: "message", role: "user", content: [{ type: "input_text", text: question }] }
      }));
      openaiWs?.send(JSON.stringify({ type: "response.create" }));
    }
  }

  function handleCorrection(text: string, state: CallState) {
    // Simple correction: assume they're correcting the current field
    const lower = text.toLowerCase();
    
    // Extract new value (crude but effective for demo)
    let newValue = text;
    if (lower.includes("it's")) {
      const parts = text.split(/it's\s+/i);
      if (parts.length > 1) newValue = parts[1].trim();
    } else if (lower.includes("actually")) {
      const parts = text.split(/actually\s+/i);
      if (parts.length > 1) newValue = parts[1].trim();
    }
    
    // Update current field
    updateStateWithResponse(newValue, state.step, state);
    state.currentStepValidated = true;
    log(`🔄 Correction applied: ${state.step} = ${newValue}`);
    
    // Acknowledge and move on
    const ackMsg = `Updated to ${newValue}.`;
    openaiWs?.send(JSON.stringify({
      type: "conversation.item.create",
      item: { type: "message", role: "user", content: [{ type: "input_text", text: ackMsg }] }
    }));
    openaiWs?.send(JSON.stringify({ type: "response.create" }));
    
    // Advance to next step
    advanceToNextStep(state);
  }

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
        state = {
          callId: msg.call_id || "unknown",
          step: "pickup",
          pickup: null,
          destination: null,
          passengers: null,
          time: null,
          currentStepValidated: false,
          farewellSent: false,
        };
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
