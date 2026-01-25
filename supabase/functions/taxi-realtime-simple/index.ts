import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === CONFIG ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const DEMO_MODE = false; // Set to true to force Max's journey

if (!OPENAI_API_KEY) {
  console.warn("⚠️ OPENAI_API_KEY missing – TTS/STT will fail");
}

// === AUDIO PROTOCOL ===
// Asterisk sends: 8kHz µ-law → we convert to 24kHz PCM16 for OpenAI
function ulawToPcm16_24k(ulaw: Uint8Array): Uint8Array {
  const pcm8k = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    const u = ~ulaw[i] & 0xFF;
    const sign = (u & 0x80) ? -1 : 1;
    const exp = (u >> 4) & 0x07;
    const mant = u & 0x0F;
    let sample = ((mant << 3) + 0x84) << exp;
    sample -= 0x84;
    pcm8k[i] = sign * sample;
  }
  // Upsample 8kHz → 24kHz (3x linear interpolation)
  const pcm24k = new Int16Array(pcm8k.length * 3);
  for (let i = 0; i < pcm8k.length - 1; i++) {
    const s0 = pcm8k[i], s1 = pcm8k[i+1];
    pcm24k[i*3] = s0;
    pcm24k[i*3+1] = Math.round(s0 + (s1 - s0)/3);
    pcm24k[i*3+2] = Math.round(s0 + 2*(s1 - s0)/3);
  }
  const last = pcm8k[pcm8k.length-1];
  pcm24k[pcm24k.length-3] = last;
  pcm24k[pcm24k.length-2] = last;
  pcm24k[pcm24k.length-1] = last;
  return new Uint8Array(pcm24k.buffer);
}

// PCM16 24kHz → 8kHz µ-law for Asterisk
function pcm16_24kToUlaw(pcm24k: Uint8Array): Uint8Array {
  const samples24k = new Int16Array(pcm24k.buffer, pcm24k.byteOffset, pcm24k.byteLength / 2);
  // Downsample 24kHz → 8kHz (take every 3rd sample)
  const samples8k = new Int16Array(Math.floor(samples24k.length / 3));
  for (let i = 0; i < samples8k.length; i++) {
    samples8k[i] = samples24k[i * 3];
  }
  // Convert to µ-law
  const ulaw = new Uint8Array(samples8k.length);
  for (let i = 0; i < samples8k.length; i++) {
    let sample = samples8k[i];
    const sign = sample < 0 ? 0x80 : 0;
    if (sample < 0) sample = -sample;
    sample = Math.min(sample, 32635);
    sample += 0x84;
    let exp = 7;
    for (let expMask = 0x4000; (sample & expMask) === 0 && exp > 0; exp--, expMask >>= 1) {}
    const mant = (sample >> (exp + 3)) & 0x0F;
    ulaw[i] = ~(sign | (exp << 4) | mant) & 0xFF;
  }
  return ulaw;
}

// === TURN-BASED STATE ===
interface CallState {
  callId: string;
  callerPhone: string;
  step: "pickup" | "destination" | "passengers" | "time" | "summary" | "fare_confirm" | "done";
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  time: string | null;
  awaitingResponse: boolean;
  isPlayingAudio: boolean;
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

  const log = (msg: string) => console.log(`[${state?.callId?.slice(0,8) || "init"}] ${msg}`);

  // Send audio back to bridge
  const sendAudioToBridge = (ulaw: Uint8Array) => {
    if (socket.readyState === WebSocket.OPEN) {
      socket.send(ulaw);
    }
  };

  // Ask a question via OpenAI TTS
  const askQuestion = (text: string) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    log(`🎤 ASK: "${text}"`);
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: { type: "message", role: "assistant", content: [{ type: "input_text", text }] }
    }));
    openaiWs.send(JSON.stringify({ type: "response.create" }));
    if (state) {
      state.awaitingResponse = true;
      state.isPlayingAudio = true;
    }
  };

  // Connect to OpenAI Realtime API
  const connectOpenAI = () => {
    openaiWs = new WebSocket(
      `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`,
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("🟢 OpenAI connected");
      // Disable VAD → WE control turns
      openaiWs?.send(JSON.stringify({
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          instructions: "You are Ada, a friendly taxi booking assistant. Speak clearly and at a relaxed pace. You ask ONE question at a time and wait for the answer.",
          voice: "shimmer",
          input_audio_format: "pcm16",
          output_audio_format: "pcm16",
          input_audio_transcription: { model: "whisper-1" },
          turn_detection: null, // 🔑 CRITICAL: disable auto-response, WE control turns
        }
      }));
    };

    openaiWs.onmessage = (event) => {
      const msg = JSON.parse(event.data);
      
      // Handle audio output from OpenAI
      if (msg.type === "response.audio.delta" && msg.delta) {
        try {
          const pcm24k = Uint8Array.from(atob(msg.delta), c => c.charCodeAt(0));
          const ulaw = pcm16_24kToUlaw(pcm24k);
          sendAudioToBridge(ulaw);
        } catch (e) {
          log(`⚠️ Audio decode error: ${e}`);
        }
      }

      // Response done - ready for user input
      if (msg.type === "response.done") {
        log("🔊 Response complete, listening...");
        if (state) {
          state.isPlayingAudio = false;
          // Commit any pending audio buffer
          openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
        }
      }

      // Session created - send greeting
      if (msg.type === "session.created") {
        log("📋 Session created, sending greeting");
        setTimeout(() => {
          askQuestion("Hello! I'm Ada, your taxi booking assistant. Where would you like to be picked up?");
        }, 500);
      }

      // User transcript received
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const text = msg.transcript?.trim();
        if (!text || text.length < 2) {
          log(`⚠️ Empty/short transcript ignored`);
          return;
        }

        log(`👤 USER: "${text}"`);

        if (!state || !state.awaitingResponse) {
          log(`⚠️ Not awaiting response, ignoring`);
          return;
        }

        state.awaitingResponse = false;

        // DEMO: Auto-fill Max's journey
        if (DEMO_MODE) {
          state.pickup = "52A David Road";
          state.destination = "The Cozy Club";
          state.passengers = 1;
          state.time = "ASAP";
          state.step = "summary";
          askQuestion(`Great! So that's a pickup from ${state.pickup} to ${state.destination}, for ${state.passengers} passenger, leaving ${state.time}. Is that correct?`);
          return;
        }

        // Update state based on current step
        switch (state.step) {
          case "pickup":
            state.pickup = text;
            state.step = "destination";
            askQuestion("And where would you like to go?");
            break;
            
          case "destination":
            state.destination = text;
            state.step = "passengers";
            askQuestion("How many passengers will be travelling?");
            break;
            
          case "passengers":
            // Parse passenger count
            const numMatch = text.match(/(\d+)/);
            if (numMatch) {
              state.passengers = parseInt(numMatch[1]);
            } else if (/one|1/i.test(text)) {
              state.passengers = 1;
            } else if (/two|2/i.test(text)) {
              state.passengers = 2;
            } else if (/three|3/i.test(text)) {
              state.passengers = 3;
            } else if (/four|4/i.test(text)) {
              state.passengers = 4;
            } else {
              state.passengers = 1; // default
            }
            state.step = "time";
            askQuestion("When do you need the taxi?");
            break;
            
          case "time":
            state.time = text;
            state.step = "summary";
            const summary = `So that's a pickup from ${state.pickup} to ${state.destination}, for ${state.passengers} passenger${state.passengers! > 1 ? 's' : ''}, ${state.time}. Is that correct?`;
            askQuestion(summary);
            break;
            
          case "summary":
            if (/yes|yeah|yep|correct|right|sure/i.test(text)) {
              state.step = "fare_confirm";
              askQuestion("The fare will be around £5.40 and the driver will be with you in about 5 minutes. Shall I confirm the booking?");
            } else if (/no|nope|wrong/i.test(text)) {
              // Restart
              state.step = "pickup";
              state.pickup = null;
              state.destination = null;
              state.passengers = null;
              state.time = null;
              askQuestion("No problem, let's start again. Where would you like to be picked up?");
            } else {
              askQuestion("Sorry, I didn't catch that. Is the booking correct? Please say yes or no.");
            }
            break;
            
          case "fare_confirm":
            if (/yes|yeah|yep|sure|confirm|book/i.test(text)) {
              state.step = "done";
              log(`✅ BOOKING CONFIRMED: ${JSON.stringify({
                pickup: state.pickup,
                destination: state.destination,
                passengers: state.passengers,
                time: state.time
              })}`);
              askQuestion("Your taxi is booked! The driver will be with you shortly. Thank you for using our service. Goodbye!");
              setTimeout(() => {
                socket.close();
              }, 5000);
            } else if (/no|nope|cancel/i.test(text)) {
              askQuestion("No problem, the booking has been cancelled. Is there anything else I can help with?");
              state.step = "pickup";
            } else {
              askQuestion("Sorry, shall I confirm the booking? Please say yes or no.");
            }
            break;
            
          case "done":
            // Already done, ignore
            break;
        }
      }

      // Handle errors
      if (msg.type === "error") {
        log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
      }
    };

    openaiWs.onerror = (e) => log(`❌ OpenAI WS error: ${e}`);
    openaiWs.onclose = () => log("🔴 OpenAI disconnected");
  };

  // Handle bridge messages
  socket.onmessage = async (event) => {
    // Binary audio from Asterisk
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState === WebSocket.OPEN && state && !state.isPlayingAudio) {
        const ulaw = event.data instanceof ArrayBuffer 
          ? new Uint8Array(event.data) 
          : event.data;
        const pcm24k = ulawToPcm16_24k(ulaw);
        
        // Convert to base64
        let binary = "";
        for (let i = 0; i < pcm24k.length; i++) {
          binary += String.fromCharCode(pcm24k[i]);
        }
        openaiWs.send(JSON.stringify({ type: "input_audio_buffer.append", audio: btoa(binary) }));
      }
      return;
    }

    // JSON control messages
    try {
      const msg = JSON.parse(event.data);
      
      if (msg.type === "init") {
        log(`📞 Call init: ${msg.call_id} from ${msg.caller_phone || "unknown"}`);
        state = {
          callId: msg.call_id || crypto.randomUUID(),
          callerPhone: msg.caller_phone || "unknown",
          step: "pickup",
          pickup: null,
          destination: null,
          passengers: null,
          time: null,
          awaitingResponse: false,
          isPlayingAudio: false,
        };
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("📴 Hangup received");
        openaiWs?.close();
        socket.close();
      }
    } catch (e) {
      // Not JSON, ignore
    }
  };

  socket.onclose = () => {
    log("🔌 Bridge disconnected");
    openaiWs?.close();
  };

  socket.onerror = (e) => log(`❌ Bridge error: ${e}`);

  return response;
});
