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

// === TIMING CONSTANTS ===
const SILENCE_COMMIT_MS = 800;      // Commit after 800ms of silence
const MIN_SPEECH_MS = 200;          // Minimum speech duration to commit
const SILENCE_PADDING_MS = 200;     // Silence padding before commit

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

// Calculate RMS energy of PCM samples
function calculateRMS(pcm: Uint8Array): number {
  const samples = new Int16Array(pcm.buffer, pcm.byteOffset, Math.floor(pcm.byteLength / 2));
  if (samples.length === 0) return 0;
  
  let sum = 0;
  for (let i = 0; i < samples.length; i++) {
    sum += samples[i] * samples[i];
  }
  return Math.sqrt(sum / samples.length);
}

// === TURN-BASED STATE ===
interface CallState {
  callId: string;
  step: "pickup" | "destination" | "passengers" | "time" | "summary" | "done";
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  time: string | null;
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
  
  // Manual VAD state
  let lastAudioTime = 0;
  let speechStartTime = 0;
  let isSpeaking = false;
  let silenceTimer: number | null = null;
  let isAdaSpeaking = false;
  let pendingCommit = false;
  const SPEECH_THRESHOLD = 150; // RMS threshold for speech detection

  const log = (msg: string) => console.log(`[${state?.callId || "unknown"}] ${msg}`);

  // Commit audio buffer with silence padding
  const commitAudio = () => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    if (pendingCommit) return;
    
    const speechDuration = Date.now() - speechStartTime;
    if (speechDuration < MIN_SPEECH_MS) {
      log(`⏭️ Skipping commit - speech too short (${speechDuration}ms)`);
      return;
    }
    
    pendingCommit = true;
    log(`📤 Committing audio after ${speechDuration}ms of speech`);
    
    // Add silence padding
    const silenceSamples = Math.floor(24000 * (SILENCE_PADDING_MS / 1000));
    const silence = new Int16Array(silenceSamples);
    const silenceBytes = new Uint8Array(silence.buffer);
    
    openaiWs.send(JSON.stringify({
      type: "input_audio_buffer.append",
      audio: arrayBufferToBase64(silenceBytes)
    }));
    
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
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
      
      // Configure session with VAD DISABLED for manual turn control
      openaiWs?.send(JSON.stringify({
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          instructions: SYSTEM_PROMPT,
          voice: "shimmer",
          input_audio_format: "pcm16",
          output_audio_format: "pcm16",
          input_audio_transcription: { model: "whisper-1" },
          turn_detection: null, // 🔑 DISABLED for manual control
          tools: []
        }
      }));
    };

    openaiWs.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data);

        // Session created
        if (msg.type === "session.created") {
          log("📋 Session created");
        }

        // Session configured - trigger greeting
        if (msg.type === "session.updated") {
          log("🎤 Session configured, triggering greeting");
          isAdaSpeaking = true;
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
        }

        // Track when Ada starts/stops speaking
        if (msg.type === "response.audio.delta") {
          isAdaSpeaking = true;
        }
        
        if (msg.type === "response.audio.done") {
          log("🔊 Ada finished speaking");
          isAdaSpeaking = false;
          pendingCommit = false;
          
          // Clear any stale audio from the buffer
          openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
        }

        // Handle user transcription - THIS is where we trigger Ada's response
        if (msg.type === "conversation.item.input_audio_transcription.completed") {
          pendingCommit = false;
          const rawText = msg.transcript?.trim();
          if (!rawText || rawText.length < 2) {
            log("⏭️ Empty transcript, ignoring");
            return;
          }

          log(`👤 User: "${rawText}"`);

          if (!state) return;

          // Update state based on current step
          const lower = rawText.toLowerCase();
          const currentStep = state.step as string;
          
          if (currentStep === "pickup") {
            state.pickup = rawText;
            state.step = "destination";
          } else if (currentStep === "destination") {
            state.destination = rawText;
            state.step = "passengers";
          } else if (currentStep === "passengers") {
            const pax = parsePassengers(rawText);
            if (pax > 0) {
              state.passengers = pax;
              state.step = "time";
            } else {
              // Not a valid passenger count - re-ask
              log("❓ Invalid passenger count, re-asking");
            }
          } else if (currentStep === "time") {
            state.time = rawText;
            state.step = "summary";
          } else if (currentStep === "summary") {
            if (/yes|yeah|correct|confirm|book/i.test(lower)) {
              state.step = "done";
              log("✅ Booking confirmed!");
            }
          }

          log(`📊 State: step=${state.step}, P=${state.pickup || '?'}, D=${state.destination || '?'}, Pax=${state.passengers || '?'}, T=${state.time || '?'}`);

          // Trigger Ada's response
          isAdaSpeaking = true;
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

        // Log Ada's speech
        if (msg.type === "response.audio_transcript.done") {
          log(`🗣️ Ada: ${msg.transcript}`);
        }

        // Log errors
        if (msg.type === "error") {
          log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
          pendingCommit = false;
        }
      } catch (e) {
        log(`⚠️ Error processing OpenAI message: ${e}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI disconnected");
  };

  // Helper: Parse passenger count with homophones
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

  // Bridge connection
  socket.onopen = () => log("🚀 Bridge connected");

  socket.onmessage = (event) => {
    // Binary audio from bridge (8kHz slin)
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState !== WebSocket.OPEN) return;
      
      const pcm8k = event.data instanceof ArrayBuffer 
        ? new Uint8Array(event.data) 
        : event.data;
      
      // Calculate audio energy for manual VAD
      const rms = calculateRMS(pcm8k);
      const now = Date.now();
      
      // Detect speech start
      if (rms > SPEECH_THRESHOLD && !isSpeaking) {
        isSpeaking = true;
        speechStartTime = now;
        log(`🎤 Speech detected (RMS: ${Math.round(rms)})`);
        
        // Clear any pending silence timer
        if (silenceTimer) {
          clearTimeout(silenceTimer);
          silenceTimer = null;
        }
      }
      
      // Track last audio time
      if (rms > SPEECH_THRESHOLD) {
        lastAudioTime = now;
      }
      
      // Detect silence after speech
      if (isSpeaking && rms < SPEECH_THRESHOLD) {
        const silenceDuration = now - lastAudioTime;
        
        if (silenceDuration > SILENCE_COMMIT_MS && !silenceTimer && !pendingCommit) {
          silenceTimer = setTimeout(() => {
            if (isSpeaking && !isAdaSpeaking) {
              log(`🔇 Silence detected (${silenceDuration}ms), committing`);
              isSpeaking = false;
              commitAudio();
            }
            silenceTimer = null;
          }, 100) as unknown as number;
        }
      }
      
      // Upsample to 24kHz and send to OpenAI
      const pcm24k = pcm8kTo24k(pcm8k);
      openaiWs.send(JSON.stringify({
        type: "input_audio_buffer.append",
        audio: arrayBufferToBase64(pcm24k)
      }));
      
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
        };
        log(`📞 Call initialized`);
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        if (silenceTimer) clearTimeout(silenceTimer);
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
    if (silenceTimer) clearTimeout(silenceTimer);
    openaiWs?.close();
  };

  return response;
});
