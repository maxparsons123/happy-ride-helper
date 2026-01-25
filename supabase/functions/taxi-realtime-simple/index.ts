import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
let DEMO_SIMPLE_MODE = true; // Force Max's journey (use let to avoid TS narrowing)

if (!OPENAI_API_KEY) {
  console.warn("⚠️ OPENAI_API_KEY missing – TTS/STT will fail");
}

// === SYSTEM PROMPT (YOUR EXACT SCRIPT) ===
const SYSTEM_PROMPT = `
# IDENTITY
You are ADA, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual.

# 🎙️ SPEECH PACING (CRITICAL)
- Speak at a SLOW, RELAXED pace. Take your time with each word.
- Insert natural pauses between sentences. Don't rush.
- Pronounce addresses and numbers clearly and deliberately.
- Use a calm, unhurried cadence like a friendly customer service agent.
- Pause briefly after asking a question to let it land.

# LANGUAGE
Respond in the same language the caller speaks. If they speak Spanish, respond in Spanish. Match their language naturally.

# 🚨 ONE QUESTION RULE (NON-NEGOTIABLE)
- Ask ONLY ONE question per response. NEVER combine questions.
- WRONG: "Where would you like to be picked up and where are you going?"
- RIGHT: "Where would you like to be picked up?" → Wait for answer → Then ask next.

# PHASE 1: WELCOME (Play IMMEDIATELY on answer)
"Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. You can switch languages at any time, just say the language you prefer, and we'll remember it for your next booking. So, let's get started."

# PHASE 2: INFORMATION GATHERING (Strict Order – NO CONFIRMATIONS)
Ask these EXACT questions in sequence. Only move to the next after receiving a valid answer:
1. "Where would you like to be picked up?"
2. "What is your destination?"
3. "How many people will be travelling?"
4. "When do you need the taxi?"

🚫 DO NOT:
- Say "Got it", "Great", or "OK" before the next question.
- Repeat or confirm individual answers.
- Ask multiple questions in one turn.

✅ DO:
- After receiving an answer, immediately ask the NEXT question with no filler.

# PHASE 3: BOOKING SUMMARY (Gate Keeper)
ONLY after all 4 fields are collected, say EXACTLY:
"Alright, let me quickly summarize your booking. You'd like to be picked up at [pickup address], and travel to [destination address]. There will be [# of passengers] people, and you'd like to be picked up [now / time]. Is that correct?"

# PHASE 4: PRICING & ETA
After user says "Yes" (or equivalent):
- Say: "Great, one moment please while I check the trip price and estimated arrival time."
- Once fare/ETA received, say ONLY:
  "The trip fare will be [price], and the estimated arrival time is [ETA]. Would you like me to confirm this booking for you?"

# PHASE 5: FINAL CONFIRMATION & CLOSING
If user says "Yes":
- Say: "Perfect, thank you. I'm making the booking now. You'll receive the booking details and ride updates via WhatsApp."
- Then choose ONE closing message at random:
  - "Just so you know, you can also book a taxi by sending us a WhatsApp voice note."
  - "Next time, feel free to book your taxi using a WhatsApp voice message."
  - "You can always book again by simply sending us a voice note on WhatsApp."
- Final sign-off: "Thank you for trying the Taxibot demo, and have a safe journey."
- → CALL end_call()
`;

// === AUDIO: slin8 → 24kHz upsample ===
function pcm8kToPcm24k(pcm8k: Uint8Array): Uint8Array {
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
  const last = samples8k[samples8k.length - 1];
  samples24k[len24k - 3] = last;
  samples24k[len24k - 2] = last;
  samples24k[len24k - 1] = last;
  return new Uint8Array(samples24k.buffer);
}

// === TURN-BASED STATE ===
interface CallState {
  callId: string;
  step: "welcome" | "pickup" | "destination" | "passengers" | "time" | "summary" | "fare_confirm" | "closing" | "done";
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  time: string | null;
  awaitingResponse: boolean;
  farewellSent: boolean;
}

// === MAIN HANDLER ===
serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });
  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426 });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;
  let state: CallState | null = null;

  const log = (msg: string) => console.log(`[${state?.callId || "init"}] ${msg}`);

  // Connect to OpenAI
  const connectOpenAI = () => {
    log("🔌 Connecting to OpenAI...");
    
    openaiWs = new WebSocket(
      `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`,
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("✅ OpenAI connected");
      
      // Disable VAD → WE control turns
      openaiWs?.send(JSON.stringify({
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          instructions: SYSTEM_PROMPT,
          voice: "shimmer",
          input_audio_format: "pcm16",
          output_audio_format: "pcm16",
          input_audio_transcription: { model: "whisper-1" },
          turn_detection: null, // 🔑 CRITICAL: disable auto-response
          tools: [{ type: "function", name: "end_call", parameters: { type: "object", properties: {} } }]
        }
      }));

      // Inject greeting
      const greeting = "Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. You can switch languages at any time, just say the language you prefer, and we'll remember it for your next booking. So, let's get started. Where would you like to be picked up?";
      openaiWs?.send(JSON.stringify({
        type: "conversation.item.create",
        item: { type: "message", role: "assistant", content: [{ type: "text", text: greeting }] }
      }));
      openaiWs?.send(JSON.stringify({ type: "response.create" }));
      if (state) {
        state.step = "pickup";
        state.awaitingResponse = true;
      }
    };

    openaiWs.onmessage = (event) => {
      const msg = JSON.parse(event.data);
      
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

      // Log transcripts
      if (msg.type === "response.audio_transcript.done") {
        log(`🗣️ Ada: ${msg.transcript}`);
      }

      // User transcript received
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const rawText = msg.transcript?.trim();
        log(`👤 User: ${rawText}`);
        
        if (!rawText || rawText.length < 2) return;

        // Filter dispatch echoes (optional)
        const lower = rawText.toLowerCase();
        if (/driver will be|price.*is.*\d+|book that/.test(lower)) return;

        if (state && !state.awaitingResponse) {
          log("⏸️ Ignoring - not awaiting response");
          return;
        }
        if (!state) return;

        state.awaitingResponse = false;

        // DEMO: Auto-fill Max's journey
        if (DEMO_SIMPLE_MODE) {
          log("🎬 DEMO MODE: Auto-filling journey");
          state.pickup = "52A David Road";
          state.destination = "The Cozy Club";
          state.passengers = 1;
          state.time = "ASAP";
          state.step = "summary";
        } else {
          // Update based on current step
          switch (state.step) {
            case "pickup": state.pickup = rawText; state.step = "destination"; break;
            case "destination": state.destination = rawText; state.step = "passengers"; break;
            case "passengers": state.passengers = parseInt(rawText) || 1; state.step = "time"; break;
            case "time": state.time = rawText; state.step = "summary"; break;
            case "summary":
              if (/yes|yeah|yep|correct|that's right/i.test(lower)) {
                state.step = "fare_confirm";
                const fareMsg = "The trip fare will be £5.40, and the estimated arrival time is 5 minutes. Would you like me to confirm this booking for you?";
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: { type: "message", role: "assistant", content: [{ type: "text", text: fareMsg }] }
                }));
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
                state.awaitingResponse = true;
                return;
              }
              break;
            case "fare_confirm":
              if (/yes|yeah|yep|go ahead|confirm/i.test(lower)) {
                state.step = "closing";
                const confirmMsg = "Perfect, thank you. I'm making the booking now. You'll receive the booking details and ride updates via WhatsApp.";
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: { type: "message", role: "assistant", content: [{ type: "text", text: confirmMsg }] }
                }));
                openaiWs?.send(JSON.stringify({ type: "response.create" }));
                return;
              }
              break;
          }
        }

        // Ask next question
        let nextQ = "";
        const currentStep = state.step as string;
        if (currentStep === "pickup") {
          nextQ = "Where would you like to be picked up?";
        } else if (currentStep === "destination") {
          nextQ = "What is your destination?";
        } else if (currentStep === "passengers") {
          nextQ = "How many people will be travelling?";
        } else if (currentStep === "time") {
          nextQ = "When do you need the taxi?";
        } else if (currentStep === "summary") {
          nextQ = `Alright, let me quickly summarize your booking. You'd like to be picked up at ${state.pickup}, and travel to ${state.destination}. There will be ${state.passengers} people, and you'd like to be picked up ${state.time}. Is that correct?`;
        } else if (currentStep === "closing") {
          const closings = [
            "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
            "Next time, feel free to book your taxi using a WhatsApp voice message.",
            "You can always book again by simply sending us a voice note on WhatsApp."
          ];
          const closing = closings[Math.floor(Math.random() * closings.length)];
          const final = `${closing} Thank you for trying the Taxibot demo, and have a safe journey.`;
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: { type: "message", role: "assistant", content: [{ type: "text", text: final }] }
          }));
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
          setTimeout(() => {
            if (!state?.farewellSent) {
              state!.farewellSent = true;
              log("👋 Ending call");
              socket.close(1000, "demo_complete");
            }
          }, 6000);
          return;
        }
        
        if (nextQ) {
          log(`📤 Next question: ${nextQ}`);
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: { type: "message", role: "assistant", content: [{ type: "text", text: nextQ }] }
          }));
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
          state.awaitingResponse = true;
        }
      }

      // Handle function calls
      if (msg.type === "response.function_call_arguments.done" && msg.name === "end_call") {
        log("📞 end_call triggered");
        socket.close(1000, "demo_complete");
      }
      
      // Log errors
      if (msg.type === "error") {
        log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI Connection Closed");
  };

  // Handle bridge messages
  socket.onopen = () => log("🚀 Bridge connected");
  
  socket.onmessage = async (event) => {
    // Binary audio from bridge
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState === WebSocket.OPEN) {
        const pcm8k = event.data instanceof ArrayBuffer 
          ? new Uint8Array(event.data) 
          : event.data;
        const pcm24k = pcm8kToPcm24k(pcm8k);
        let binary = "";
        for (let i = 0; i < pcm24k.length; i++) {
          binary += String.fromCharCode(pcm24k[i]);
        }
        openaiWs.send(JSON.stringify({ type: "input_audio_buffer.append", audio: btoa(binary) }));
        
        // Since VAD is off, we need to manually commit audio periodically
        // The bridge will send us silence too, which helps detect pauses
      }
      return;
    }

    // JSON control messages
    try {
      const msg = JSON.parse(event.data);
      if (msg.type === "init") {
        log(`📞 Call init: ${msg.call_id}`);
        state = {
          callId: msg.call_id || "unknown",
          step: "welcome",
          pickup: null,
          destination: null,
          passengers: null,
          time: null,
          awaitingResponse: false,
          farewellSent: false,
        };
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        openaiWs?.close();
        socket.close();
      }
    } catch {
      // Not JSON, ignore
    }
  };

  socket.onclose = () => {
    log("🔌 Bridge disconnected");
    openaiWs?.close();
  };

  return response;
});
