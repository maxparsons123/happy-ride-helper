// taxi-realtime-paired/index.ts
// Clean Context-Pairing implementation with strict sequential state machine

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

// ═══════════════════════════════════════════════════════════════════════════════
// 1. CONFIGURATION & CONSTANTS
// ═══════════════════════════════════════════════════════════════════════════════

const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY") || "";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";

const VOICE = "shimmer";
const OPENAI_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17";

const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Strict sequential booking steps
const BOOKING_STEPS = ["pickup", "destination", "passengers", "time", "summary", "confirmed"] as const;
type BookingStep = typeof BOOKING_STEPS[number];

interface SessionState {
  callId: string;
  callerPhone: string;
  currentStepIndex: number;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    time: string | null;
  };
  greetingSent: boolean;
  bookingConfirmed: boolean;
}

// ═══════════════════════════════════════════════════════════════════════════════
// 2. STT CORRECTIONS (Essential for telephony)
// ═══════════════════════════════════════════════════════════════════════════════

const STT_CORRECTIONS: Record<string, string> = {
  // Numbers
  "free": "three", "tree": "three", "full": "four",
  "too": "two", "won": "one", "wan": "one",
  "fife": "five", "sicks": "six", "freight": "eight",
  // Telephony mishearings of "three"
  "boston juice": "three", "bossing juice": "three",
  // Airports
  "heather oh": "Heathrow", "heather row": "Heathrow", "heath row": "Heathrow",
  "gat wick": "Gatwick", "got wick": "Gatwick",
  // Common venues
  "sweets puffs": "Sweet Spot", "sweet puffs": "Sweet Spot", "sweetspots": "Sweet Spot",
  // Number words with passengers
  "for passengers": "4 passengers", "to passengers": "2 passengers",
  "tree passengers": "3 passengers", "free passengers": "3 passengers",
};

const STT_PATTERN = new RegExp(
  `\\b(${Object.keys(STT_CORRECTIONS).map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})\\b`,
  'gi'
);

function correctTranscript(text: string): string {
  if (!text) return "";
  let corrected = text.replace(STT_PATTERN, (matched) => 
    STT_CORRECTIONS[matched.toLowerCase()] || matched
  );
  // Join number + letter suffixes: "52 A" → "52A"
  corrected = corrected.replace(/\b(\d+)\s+([ABCabc])\b/g, (_, num, letter) => num + letter.toUpperCase());
  return corrected.charAt(0).toUpperCase() + corrected.slice(1);
}

// Phantom phrases to ignore (Whisper hallucinations)
const PHANTOM_PHRASES = [
  "thank you for watching", "subscribe", "like and subscribe",
  "taxi booking. addresses", "street names, numbers, passenger count",
  "i'm ada", "your taxi booking assistant"
];

function isPhantom(text: string): boolean {
  const lower = text.toLowerCase().trim();
  if (lower.length < 3) return true;
  return PHANTOM_PHRASES.some(p => lower.includes(p));
}

// ═══════════════════════════════════════════════════════════════════════════════
// 3. AUDIO CONVERSION UTILITIES
// ═══════════════════════════════════════════════════════════════════════════════

// μ-law to PCM16 decoding table
const ULAW_TO_PCM16 = new Int16Array(256);
for (let i = 0; i < 256; i++) {
  const u = ~i & 0xFF;
  const sign = u & 0x80;
  const exponent = (u >> 4) & 0x07;
  const mantissa = u & 0x0F;
  let sample = ((mantissa << 3) + 0x84) << exponent;
  sample -= 0x84;
  ULAW_TO_PCM16[i] = sign ? -sample : sample;
}

function ulawToPcm16(ulaw: Uint8Array): Int16Array {
  const pcm = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    pcm[i] = ULAW_TO_PCM16[ulaw[i]];
  }
  return pcm;
}

// Resample 8kHz → 24kHz (3x interpolation)
function resample8kTo24k(pcm8k: Int16Array): Int16Array {
  const pcm24k = new Int16Array(pcm8k.length * 3);
  for (let i = 0; i < pcm8k.length; i++) {
    const curr = pcm8k[i];
    const next = pcm8k[Math.min(i + 1, pcm8k.length - 1)];
    const idx = i * 3;
    pcm24k[idx] = curr;
    pcm24k[idx + 1] = Math.round(curr + (next - curr) / 3);
    pcm24k[idx + 2] = Math.round(curr + (2 * (next - curr)) / 3);
  }
  return pcm24k;
}

// PCM16 → Base64
function pcm16ToBase64(pcm: Int16Array): string {
  const bytes = new Uint8Array(pcm.buffer);
  let binary = "";
  for (let i = 0; i < bytes.length; i += 4096) {
    binary += String.fromCharCode(...bytes.slice(i, i + 4096));
  }
  return btoa(binary);
}

// Raw bytes → Base64 (useful when audio is already PCM16 bytes)
function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 4096) {
    binary += String.fromCharCode(...bytes.slice(i, i + 4096));
  }
  return btoa(binary);
}

// ═══════════════════════════════════════════════════════════════════════════════
// 4. DYNAMIC SYSTEM PROMPT (Context-Pairing Heart)
// ═══════════════════════════════════════════════════════════════════════════════

function buildSystemPrompt(state: SessionState): string {
  const currentStep = BOOKING_STEPS[state.currentStepIndex];
  const { pickup, destination, passengers, time } = state.booking;

  return `# IDENTITY
You are Ada, a friendly taxi booking assistant. You speak naturally and conversationally.

# CURRENT STATE: ${currentStep.toUpperCase()}
${pickup ? `✓ Pickup: ${pickup}` : "○ Pickup: NOT SET"}
${destination ? `✓ Destination: ${destination}` : "○ Destination: NOT SET"}
${passengers !== null ? `✓ Passengers: ${passengers}` : "○ Passengers: NOT SET"}
${time ? `✓ Time: ${time}` : "○ Time: NOT SET"}

# CONTEXT-PAIRING RULES
You are locked to the '${currentStep}' phase. Follow these rules STRICTLY:

Step 0 (pickup): Ask ONLY "Where would you like to be picked up?"
Step 1 (destination): Ask ONLY "And what is your destination?"
Step 2 (passengers): Ask ONLY "How many passengers will be traveling?"
Step 3 (time): Ask ONLY "What time would you like the taxi?"
Step 4 (summary): Recite all details and ask "Would you like me to book this for you?"
Step 5 (confirmed): Say "Your taxi is booked! Goodbye." then call end_call.

# CRITICAL RULES
1. NEVER combine multiple questions in one response.
2. NEVER ask about a later step until the current step is complete.
3. When you receive an answer, IMMEDIATELY call 'sync_booking_data' with the field.
4. Do NOT repeat or confirm addresses - just save and move on.
5. Accept spoken numbers as words (e.g., "three" = 3).
6. If user says "now" or "ASAP" for time, save as "ASAP".
7. Accept business names and landmarks as valid addresses.
8. After summary confirmation, call sync_booking_data with action: "confirm".`;
}

// ═══════════════════════════════════════════════════════════════════════════════
// 5. TOOL DEFINITIONS
// ═══════════════════════════════════════════════════════════════════════════════

const TOOLS = [
  {
    type: "function",
    name: "sync_booking_data",
    description: "Saves booking data and advances the state machine. Call this IMMEDIATELY after receiving any booking information.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "The pickup address" },
        destination: { type: "string", description: "The destination address" },
        passengers: { type: "number", description: "Number of passengers (1-20)" },
        pickup_time: { type: "string", description: "When to pick up (e.g., 'now', 'ASAP', '3:30 PM')" },
        action: { type: "string", enum: ["save", "confirm"], description: "Use 'confirm' after summary is approved" }
      }
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "Ends the phone call gracefully.",
    parameters: { type: "object", properties: {} }
  }
];

// ═══════════════════════════════════════════════════════════════════════════════
// 6. DATABASE PERSISTENCE
// ═══════════════════════════════════════════════════════════════════════════════

async function updateLiveCall(state: SessionState) {
  const currentStep = BOOKING_STEPS[state.currentStepIndex];
  try {
    await supabase.from("live_calls").upsert({
      call_id: state.callId,
      caller_phone: state.callerPhone,
      pickup: state.booking.pickup,
      destination: state.booking.destination,
      passengers: state.booking.passengers,
      status: state.bookingConfirmed ? "confirmed" : "active",
      booking_confirmed: state.bookingConfirmed,
      clarification_attempts: { last_step: currentStep },
      updated_at: new Date().toISOString()
    }, { onConflict: "call_id" });
    console.log(`[${state.callId}] 📊 DB synced: step=${currentStep}`);
  } catch (e) {
    console.error(`[${state.callId}] DB error:`, e);
  }
}

// ═══════════════════════════════════════════════════════════════════════════════
// 7. MAIN ENTRY POINT
// ═══════════════════════════════════════════════════════════════════════════════

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: CORS_HEADERS });
  }

  // Upgrade to WebSocket
  const { socket, response } = Deno.upgradeWebSocket(req);
  
  const url = new URL(req.url);
  const callId = url.searchParams.get("call_id") || `call_${Date.now()}`;
  const callerPhone = url.searchParams.get("caller") || "unknown";
  const source = url.searchParams.get("source") || "unknown";
  const format = url.searchParams.get("format") || "unknown";
  const sampleRate = url.searchParams.get("sample_rate") || "unknown";

  console.log(`[${callId}] 🔌 Bridge connected from ${callerPhone} (source=${source}, format=${format}, rate=${sampleRate})`);

  socket.onopen = () => {
    handleRealtimeSession(socket, callId, callerPhone, { source, format, sampleRate });
  };

  socket.onerror = (e) => console.error(`[${callId}] Socket error:`, e);

  return response;
});

// ═══════════════════════════════════════════════════════════════════════════════
// 8. OPENAI REALTIME SESSION HANDLER
// ═══════════════════════════════════════════════════════════════════════════════

function handleRealtimeSession(
  clientSocket: WebSocket,
  callId: string,
  callerPhone: string,
  meta: { source: string; format: string; sampleRate: string }
) {
  const state: SessionState = {
    callId,
    callerPhone,
    currentStepIndex: 0,
    booking: { pickup: null, destination: null, passengers: null, time: null },
    greetingSent: false,
    bookingConfirmed: false
  };

  let openaiWs: WebSocket | null = null;
  let cleanedUp = false;

  // Lightweight audio diagnostics
  let audioPacketsIn = 0;
  let audioBytesIn = 0;
  let lastAudioLogAt = 0;

  // Initialize live_calls record
  supabase.from("live_calls").upsert({
    call_id: callId,
    caller_phone: callerPhone,
    source: "sip",
    status: "active",
    started_at: new Date().toISOString(),
    transcripts: []
  }, { onConflict: "call_id" }).then(() => {
    console.log(`[${callId}] 📝 Live call record created`);
  });

  const cleanup = () => {
    if (cleanedUp) return;
    cleanedUp = true;
    console.log(`[${callId}] 🧹 Cleaning up`);
    if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
      openaiWs.close();
    }
    supabase.from("live_calls").update({
      status: state.bookingConfirmed ? "completed" : "ended",
      ended_at: new Date().toISOString()
    }).eq("call_id", callId);
  };

  // Connect to OpenAI
  try {
    openaiWs = new WebSocket(OPENAI_URL, [
      "realtime",
      `openai-insecure-api-key.${OPENAI_API_KEY}`,
      "openai-beta.realtime-v1"
    ]);
  } catch (e) {
    console.error(`[${callId}] Failed to connect to OpenAI:`, e);
    clientSocket.close();
    return;
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // OpenAI WebSocket Handlers
  // ═══════════════════════════════════════════════════════════════════════════

  openaiWs.onopen = () => {
    console.log(`[${callId}] ✅ Connected to OpenAI Realtime`);
  };

  openaiWs.onmessage = async (event) => {
    const data = JSON.parse(event.data);

    switch (data.type) {
      case "session.created":
        console.log(`[${callId}] 🎉 Session created, sending config...`);
        
        // Send session configuration
        openaiWs!.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            voice: VOICE,
            instructions: buildSystemPrompt(state),
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            turn_detection: {
              type: "server_vad",
              threshold: 0.5,
              prefix_padding_ms: 300,
              silence_duration_ms: 800
            },
            tools: TOOLS,
            tool_choice: "auto"
          }
        }));
        break;

      case "session.updated":
        // Send greeting on first session update
        if (!state.greetingSent) {
          state.greetingSent = true;
          console.log(`[${callId}] 🎙️ Sending greeting...`);
          
          openaiWs!.send(JSON.stringify({
            type: "response.create",
            response: {
              modalities: ["audio", "text"],
              instructions: "Say EXACTLY: 'Hello! I'm Ada, your taxi booking assistant. Where would you like to be picked up?' Then WAIT for the answer."
            }
          }));
          
          // Notify client session is ready
          clientSocket.send(JSON.stringify({ type: "session_ready", pipeline: "paired" }));
        }
        break;

      case "response.audio.delta":
        // Forward AI audio to bridge
        if (clientSocket.readyState === WebSocket.OPEN) {
          clientSocket.send(JSON.stringify({ type: "audio", audio: data.delta }));
        }
        break;

      case "input_audio_buffer.committed":
        // Barge-in: tell bridge to stop current playback
        if (clientSocket.readyState === WebSocket.OPEN) {
          clientSocket.send(JSON.stringify({ type: "stop_audio" }));
        }
        break;

      case "conversation.item.input_audio_transcription.completed":
        if (data.transcript) {
          const raw = data.transcript.trim();
          const text = correctTranscript(raw);
          
          if (isPhantom(text)) {
            console.log(`[${callId}] 👻 Ignored phantom: "${raw}"`);
            break;
          }
          
          if (text !== raw) {
            console.log(`[${callId}] 🔧 STT: "${raw}" → "${text}"`);
          }
          console.log(`[${callId}] 👤 User: "${text}"`);
        }
        break;

      case "response.function_call_arguments.done":
        await handleToolExecution(data, state, openaiWs!, clientSocket, callId);
        break;

      case "error":
        console.error(`[${callId}] ❌ OpenAI Error:`, data.error);
        break;
    }
  };

  openaiWs.onerror = (e) => console.error(`[${callId}] OpenAI WS error:`, e);
  openaiWs.onclose = () => {
    console.log(`[${callId}] OpenAI WS closed`);
    cleanup();
  };

  // ═══════════════════════════════════════════════════════════════════════════
  // Bridge → OpenAI (Audio Forwarding)
  // ═══════════════════════════════════════════════════════════════════════════

  clientSocket.onmessage = async (event) => {
    if (cleanedUp || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;

    try {
      // Handle binary audio
      if (event.data instanceof Blob) {
        const ab = await event.data.arrayBuffer();
        event = { ...event, data: ab } as MessageEvent;
      }

      if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
        const bytes = event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : event.data;

        audioPacketsIn++;
        audioBytesIn += bytes.length;
        const now = Date.now();
        if (now - lastAudioLogAt > 2000) {
          lastAudioLogAt = now;
          console.log(
            `[${callId}] 🎧 inbound audio: packets=${audioPacketsIn}, bytes=${audioBytesIn} (lastChunk=${bytes.length}, source=${meta.source}, formatHint=${meta.format})`,
          );
        }

        // IMPORTANT: Different bridges send different binary formats:
        // - C# SIP bridge (CallSession.cs) sends raw PCM16 @ 24kHz (little-endian) as binary frames.
        // - Asterisk/Python bridges often send µ-law @ 8kHz.
        // Heuristic: µ-law frames are typically 160 bytes for 20ms @ 8kHz.

        let base64: string;
        const looksLikeUlaw8k = bytes.length === 160 || bytes.length === 320;

        if (!looksLikeUlaw8k && bytes.length % 2 === 0) {
          // Treat as PCM16 bytes already (assumed 24kHz)
          base64 = bytesToBase64(bytes);
        } else {
          // Treat as µ-law @ 8kHz → convert to PCM16 @ 24kHz
          const pcm8k = ulawToPcm16(bytes);
          const pcm24k = resample8kTo24k(pcm8k);
          base64 = pcm16ToBase64(pcm24k);
        }
        
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: base64
        }));
        return;
      }

      // Handle JSON messages
      const msg = JSON.parse(event.data);
      
      if (msg.type === "audio" && msg.audio) {
        audioPacketsIn++;
        if (Date.now() - lastAudioLogAt > 2000) {
          lastAudioLogAt = Date.now();
          console.log(
            `[${callId}] 🎧 inbound audio (json): packets=${audioPacketsIn} (source=${meta.source}, formatHint=${meta.format})`,
          );
        }
        // Base64 audio from bridge
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: msg.audio
        }));
      } else if (msg.type === "hangup") {
        console.log(`[${callId}] 📞 Bridge hangup received`);
        cleanup();
        clientSocket.close();
      } else {
        // Helpful for debugging protocol mismatches
        if (typeof msg?.type === "string") {
          console.log(`[${callId}] ℹ️ Bridge message: type=${msg.type}`);
        } else {
          console.log(`[${callId}] ℹ️ Bridge message (untyped)`);
        }
      }
    } catch (e) {
      console.error(`[${callId}] Error processing bridge message:`, e);
    }
  };

  clientSocket.onclose = () => {
    console.log(`[${callId}] 🔌 Bridge disconnected`);
    cleanup();
  };
}

// ═══════════════════════════════════════════════════════════════════════════════
// 9. TOOL EXECUTION HANDLER (Context-Pairing Logic)
// ═══════════════════════════════════════════════════════════════════════════════

async function handleToolExecution(
  data: any,
  state: SessionState,
  openaiWs: WebSocket,
  clientSocket: WebSocket,
  callId: string
) {
  let args: Record<string, any> = {};
  try {
    args = JSON.parse(data.arguments || "{}");
  } catch {
    console.error(`[${callId}] Failed to parse tool args`);
    return;
  }

  const toolName = data.name;
  console.log(`[${callId}] 🔧 Tool: ${toolName}`, args);

  if (toolName === "sync_booking_data") {
    const currentStep = BOOKING_STEPS[state.currentStepIndex];
    
    // Update state based on provided fields
    if (args.pickup && !state.booking.pickup) {
      state.booking.pickup = args.pickup;
      console.log(`[${callId}] ✅ Pickup saved: "${args.pickup}"`);
    }
    if (args.destination && !state.booking.destination) {
      state.booking.destination = args.destination;
      console.log(`[${callId}] ✅ Destination saved: "${args.destination}"`);
    }
    if (args.passengers !== undefined && state.booking.passengers === null) {
      state.booking.passengers = Number(args.passengers);
      console.log(`[${callId}] ✅ Passengers saved: ${args.passengers}`);
    }
    if (args.pickup_time && !state.booking.time) {
      const time = args.pickup_time.toLowerCase();
      state.booking.time = (time.includes("now") || time.includes("asap")) ? "ASAP" : args.pickup_time;
      console.log(`[${callId}] ✅ Time saved: "${state.booking.time}"`);
    }

    // Handle confirmation
    if (args.action === "confirm") {
      state.bookingConfirmed = true;
      state.currentStepIndex = BOOKING_STEPS.indexOf("confirmed");
      console.log(`[${callId}] 🎉 BOOKING CONFIRMED!`);
    } else {
      // Advance to next step based on what's now complete
      if (state.booking.pickup && state.currentStepIndex === 0) {
        state.currentStepIndex = 1;
      }
      if (state.booking.destination && state.currentStepIndex === 1) {
        state.currentStepIndex = 2;
      }
      if (state.booking.passengers !== null && state.currentStepIndex === 2) {
        state.currentStepIndex = 3;
      }
      if (state.booking.time && state.currentStepIndex === 3) {
        state.currentStepIndex = 4; // Move to summary
      }
    }

    const nextStep = BOOKING_STEPS[state.currentStepIndex];
    console.log(`[${callId}] ➡️ Advanced to step ${state.currentStepIndex}: ${nextStep}`);

    // Sync to database
    await updateLiveCall(state);

    // Update OpenAI with new context (the key to Context-Pairing!)
    openaiWs.send(JSON.stringify({
      type: "session.update",
      session: { instructions: buildSystemPrompt(state) }
    }));

    // Acknowledge tool completion
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "function_call_output",
        call_id: data.call_id,
        output: JSON.stringify({
          status: "success",
          next_step: nextStep,
          current_state: state.booking
        })
      }
    }));

    // Trigger next response
    openaiWs.send(JSON.stringify({ type: "response.create" }));
  }

  if (toolName === "end_call") {
    console.log(`[${callId}] 📞 Ending call...`);
    
    // Acknowledge
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "function_call_output",
        call_id: data.call_id,
        output: JSON.stringify({ status: "call_ended" })
      }
    }));

    // Tell bridge to hang up
    if (clientSocket.readyState === WebSocket.OPEN) {
      clientSocket.send(JSON.stringify({ type: "hangup" }));
    }

    // Update DB
    state.bookingConfirmed = true;
    await updateLiveCall(state);
  }
}
