import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY")!;
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const DEFAULT_COMPANY = "Taxibot Demo";
const DEFAULT_AGENT = "Ada";
const DEFAULT_VOICE = "shimmer";

// --- System Prompt ---
const SYSTEM_PROMPT = `
You are {{agent_name}}, a friendly taxi booking assistant for {{company_name}}.

## 🎯 FIRST-TIME CALLER WELCOME
"Hello, and welcome to the Taxibot demo! I'm {{agent_name}}, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. You can switch languages at any time—just say the language you prefer, and we'll remember it for your next booking. So, let's get started!"

## 📝 INFORMATION GATHERING

⚠️ CRITICAL: Ask ONE question at a time. After each question, STOP and WAIT for the user's answer.

Ask in this order:
1. "Where would you like to be picked up?" → WAIT
2. "What is your destination?" → WAIT  
3. "How many people will be travelling?" → WAIT
4. "When do you need the taxi?" → WAIT (default to "now" if not specified)

NEVER chain questions together. NEVER ask a follow-up in the same response.

## ✅ BOOKING SUMMARY (Before pricing)
Once you have ALL details, say:
"Alright, let me quickly summarize your booking:
• You'd like to be picked up at [pickup address],
• and travel to [destination address].
• There will be [# of passengers],
• and you'd like to be picked up [now / time].
Is that correct?"

WAIT for "yes" / "correct" before proceeding.

## 💷 PRICING & ETA CHECK
After user confirms summary:
"Great, one moment please while I check the trip price and estimated arrival time."

→ CALL book_taxi with confirmation_state: "request_quote"

When you receive the result, say:
"The trip fare will be [price], and the estimated arrival time is [ETA]. Would you like me to confirm this booking for you?"

WAIT for response.

## 📲 FINAL CONFIRMATION
If user says "yes":
1. CALL book_taxi with confirmation_state: "confirmed"
2. Say: "Perfect, thank you. I'm making the booking now. You'll receive the booking details and ride updates via WhatsApp."

Then say ONE of these (vary them):
• "Just so you know, you can also book a taxi by sending us a WhatsApp voice note."
• "Next time, feel free to book your taxi using a WhatsApp voice message."
• "You can always book again by simply sending us a voice note on WhatsApp."

Then: "Thank you for trying the Taxibot demo, and have a safe journey!"
Then CALL end_call.

## 🛑 CANCELLATION
If user says "cancel", "never mind", "forget it":
1. CALL cancel_booking
2. Say: "No problem, I've cancelled that. Is there anything else I can help you with?"

## 🚫 CRITICAL RULES
- NEVER invent fares or ETAs - always use book_taxi tool
- NEVER say "booking confirmed" before calling book_taxi with "confirmed"
- ALWAYS confirm addresses in summary before pricing
- If user changes ANY detail, re-summarize ALL fields
- Keep responses SHORT (1-2 sentences max)
- ONE question per response, then STOP
`;

// --- Tool Definitions ---
const TOOLS = [
  {
    type: "function",
    name: "save_customer_name",
    description: "Save the customer's name when they provide it.",
    parameters: {
      type: "object",
      properties: { name: { type: "string" } },
      required: ["name"]
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Handle booking. First call with 'request_quote' to get fare/ETA. After customer confirms price, call with 'confirmed'.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
        passengers: { type: "integer", minimum: 1, default: 1 },
        confirmation_state: {
          type: "string",
          enum: ["request_quote", "confirmed", "rejected"],
          description: "Use 'request_quote' first, then 'confirmed' after user agrees to price"
        }
      },
      required: ["pickup", "destination", "confirmation_state"]
    }
  },
  {
    type: "function",
    name: "cancel_booking",
    description: "Cancel the current booking. Call this when user says cancel/never mind/forget it.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "end_call",
    description: "End the call after saying goodbye.",
    parameters: { type: "object", properties: {} }
  }
];

// --- Session State ---
interface SessionState {
  callId: string;
  phone: string;
  agentName: string;
  companyName: string;
  voice: string;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number;
    fare: string | null;
    eta: string | null;
    status: string;
  };
  transcripts: Array<{ role: string; content: string; timestamp: string }>;
}

// --- Main Handler ---
serve(async (req) => {
  const upgrade = req.headers.get("upgrade") || "";
  const isWebSocket = upgrade.toLowerCase() === "websocket";

  // Only return health responses when this is NOT a WebSocket upgrade.
  // (WebSocket handshakes are GET requests too.)
  if (!isWebSocket) {
    if (req.method === "OPTIONS") {
      return new Response(null, { status: 204 });
    }

    if (req.method === "GET") {
      return new Response("Taxi Realtime V2 OK", { status: 200 });
    }

    return new Response("Expected WebSocket", { status: 426 });
  }

  const url = new URL(req.url);

  // Defaults (will be overridden by the bridge's init message)
  let callId = url.searchParams.get("call_id") || `call-${Date.now()}`;
  let callerPhone = url.searchParams.get("caller_phone") || "";
  const agentSlug = url.searchParams.get("agent") || "ada";

  const { socket, response } = Deno.upgradeWebSocket(req);

  // Initialize Supabase
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

  // Session state (call_id/phone will be updated on init)
  const sessionState: SessionState = {
    callId,
    phone: callerPhone,
    agentName: DEFAULT_AGENT,
    companyName: DEFAULT_COMPANY,
    voice: DEFAULT_VOICE,
    booking: {
      pickup: null,
      destination: null,
      passengers: 1,
      fare: null,
      eta: null,
      status: "collecting",
    },
    transcripts: [],
  };

  let openaiWs: WebSocket | null = null;

  // --- Socket Handlers ---
  let started = false;
  let inboundFormat: "ulaw8k" | "pcm16_24k" | null = null;

  const startCallIfNeeded = async () => {
    if (started) return;
    started = true;

    console.log(`[${callId}] 📞 Starting call (phone=${callerPhone || "unknown"})`);

    // Load agent config
    try {
      const { data: agent } = await supabase
        .from("agents")
        .select("*")
        .eq("slug", agentSlug)
        .eq("is_active", true)
        .single();

      if (agent) {
        sessionState.agentName = agent.name;
        sessionState.companyName = agent.company_name;
        sessionState.voice = agent.voice || DEFAULT_VOICE;
      }
    } catch {
      console.log(`[${callId}] Using default agent config`);
    }

    // Create/Update live_calls record
    await supabase
      .from("live_calls")
      .upsert(
        {
          call_id: callId,
          caller_phone: callerPhone,
          status: "active",
          source: "realtime-v2",
          started_at: new Date().toISOString(),
          transcripts: [],
        },
        { onConflict: "call_id" }
      );

    // Connect to OpenAI
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      console.log(`[${callId}] 🔌 Connected to OpenAI`);
    };

    openaiWs.onmessage = async (event) => {
      const data = JSON.parse(event.data);

      switch (data.type) {
        case "session.created": {
          console.log(`[${callId}] ✅ Session created, configuring...`);

          const prompt = SYSTEM_PROMPT
            .replace(/\{\{agent_name\}\}/g, sessionState.agentName)
            .replace(/\{\{company_name\}\}/g, sessionState.companyName);

          openaiWs!.send(
            JSON.stringify({
              type: "session.update",
              session: {
                modalities: ["text", "audio"],
                instructions: prompt,
                voice: sessionState.voice,
                input_audio_format: "pcm16",
                output_audio_format: "pcm16",
                input_audio_transcription: { model: "whisper-1" },
                turn_detection: {
                  type: "server_vad",
                  threshold: 0.4,
                  prefix_padding_ms: 500,
                  silence_duration_ms: 2500,
                },
                temperature: 0.7,
                tools: TOOLS,
                tool_choice: "auto",
              },
            })
          );
          break;
        }

        case "session.updated":
          console.log(`[${callId}] 📝 Session configured, triggering greeting`);
          openaiWs!.send(JSON.stringify({ type: "response.create" }));
          break;

        case "response.audio.delta":
          if (data.delta) {
            const audioBytes = Uint8Array.from(atob(data.delta), (c) => c.charCodeAt(0));
            // Bridge supports binary PCM16@24kHz messages
            socket.send(audioBytes);
          }
          break;

        case "conversation.item.input_audio_transcription.completed": {
          const userText = data.transcript?.trim();
          if (userText) {
            console.log(`[${callId}] 👤 User: ${userText}`);
            sessionState.transcripts.push({
              role: "user",
              content: userText,
              timestamp: new Date().toISOString(),
            });
            // Also send transcript to the bridge for logging
            socket.send(JSON.stringify({ type: "transcript", role: "user", text: userText }));
            flushTranscripts(supabase, sessionState);
          }
          break;
        }

        case "response.audio_transcript.done": {
          const assistantText = data.transcript?.trim();
          if (assistantText) {
            console.log(`[${callId}] 🤖 Ada: ${assistantText}`);
            sessionState.transcripts.push({
              role: "assistant",
              content: assistantText,
              timestamp: new Date().toISOString(),
            });
            socket.send(JSON.stringify({ type: "transcript", role: "assistant", text: assistantText }));
            flushTranscripts(supabase, sessionState);
          }
          break;
        }

        case "response.function_call_arguments.done":
          await handleToolCall(
            data.name,
            data.arguments,
            data.call_id,
            sessionState,
            openaiWs!,
            supabase,
            socket
          );
          break;

        case "error":
          console.error(`[${callId}] ❌ OpenAI error:`, data.error);
          break;
      }
    };

    openaiWs.onerror = (e) => {
      console.error(`[${callId}] OpenAI WebSocket error:`, e);
    };

    openaiWs.onclose = () => {
      console.log(`[${callId}] OpenAI connection closed`);
    };
  };

  socket.onopen = () => {
    console.log(`[${callId}] 🔌 Bridge WebSocket connected (awaiting init...)`);
  };

  // Handle incoming messages from Asterisk bridge:
  // - string JSON (init)
  // - binary audio frames (u-law@8k or PCM16@24k)
  socket.onmessage = async (event) => {
    try {
      if (typeof event.data === "string") {
        const msg = JSON.parse(event.data);
        if (msg?.type === "init") {
          // The bridge is authoritative for call_id + phone
          callId = String(msg.call_id || callId);
          callerPhone = String(msg.phone || msg.user_phone || callerPhone || "");
          sessionState.callId = callId;
          sessionState.phone = callerPhone;

          console.log(`[${callId}] ✅ Init received (phone=${callerPhone || "unknown"})`);
          socket.send(JSON.stringify({ type: "init_ack", call_id: callId }));

          await startCallIfNeeded();
          return;
        }

        // Ignore other JSON messages for now
        return;
      }

      if (!(event.data instanceof ArrayBuffer)) return;
      if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;

      const incoming = new Uint8Array(event.data);

      // Detect format once (Asterisk bridge default sends 160-byte u-law frames)
      if (!inboundFormat) {
        inboundFormat = incoming.byteLength <= 400 ? "ulaw8k" : "pcm16_24k";
        console.log(`[${callId}] 🎧 Inbound audio format detected: ${inboundFormat} (bytes=${incoming.byteLength})`);
      }

      let pcm24kBytes: Uint8Array;

      if (inboundFormat === "ulaw8k") {
        const pcm8k = ulawToPcm16(incoming);
        const pcm24k = upsamplePcm16(pcm8k, 8000, 24000);
        pcm24kBytes = int16ToBytesLE(pcm24k);
      } else {
        // Assume already PCM16@24k
        pcm24kBytes = incoming;
      }

      const base64Audio = bytesToBase64(pcm24kBytes);
      openaiWs.send(
        JSON.stringify({
          type: "input_audio_buffer.append",
          audio: base64Audio,
        })
      );
    } catch (e) {
      console.error(`[${callId}] ❌ socket.onmessage error:`, e);
    }
  };
  socket.onclose = async () => {
    console.log(`[${callId}] 📴 Call ended`);
    openaiWs?.close();
    
    await supabase.from("live_calls").update({
      status: "ended",
      ended_at: new Date().toISOString(),
      transcripts: sessionState.transcripts
    }).eq("call_id", callId);
  };

  socket.onerror = (e) => {
    console.error(`[${callId}] Socket error:`, e);
  };

  return response;
});

// --- Tool Handler ---
async function handleToolCall(
  name: string,
  argsJson: string,
  callId: string,
  state: SessionState,
  openaiWs: WebSocket,
  supabase: any,
  clientSocket: WebSocket
) {
  const args = JSON.parse(argsJson);
  console.log(`[${state.callId}] 🔧 Tool: ${name}`, args);

  let result: any = { success: true };

  switch (name) {
    case "save_customer_name":
      // Update caller record
      await supabase.from("callers").upsert({
        phone_number: state.phone.replace(/\D/g, ""),
        name: args.name,
        updated_at: new Date().toISOString()
      }, { onConflict: "phone_number" });
      result = { message: `Name saved: ${args.name}` };
      break;

    case "book_taxi":
      state.booking.pickup = args.pickup || state.booking.pickup;
      state.booking.destination = args.destination || state.booking.destination;
      state.booking.passengers = args.passengers || state.booking.passengers;

      if (args.confirmation_state === "request_quote") {
        // Simulate fare calculation (replace with real webhook)
        const distance = Math.floor(Math.random() * 10) + 3;
        const fare = (distance * 2.5 + 3.5).toFixed(2);
        const eta = `${Math.floor(Math.random() * 10) + 5} minutes`;
        
        state.booking.fare = `£${fare}`;
        state.booking.eta = eta;
        state.booking.status = "quoted";
        
        result = {
          fare: state.booking.fare,
          eta: state.booking.eta,
          message: `Fare: ${state.booking.fare}, ETA: ${eta}`
        };
      } 
      else if (args.confirmation_state === "confirmed") {
        state.booking.status = "confirmed";
        
        // Create booking record
        await supabase.from("bookings").insert({
          call_id: state.callId,
          caller_phone: state.phone,
          pickup: state.booking.pickup,
          destination: state.booking.destination,
          passengers: state.booking.passengers,
          fare: state.booking.fare,
          eta: state.booking.eta,
          status: "confirmed"
        });

        result = { message: "Booking confirmed", booking_id: state.callId };
      }
      else if (args.confirmation_state === "rejected") {
        state.booking.status = "rejected";
        result = { message: "Booking cancelled by user" };
      }
      break;

    case "cancel_booking":
      state.booking.status = "cancelled";
      await supabase.from("live_calls").update({
        booking_confirmed: false,
        status: "cancelled"
      }).eq("call_id", state.callId);
      result = { message: "Booking cancelled" };
      break;

    case "end_call":
      result = { message: "Call ending" };
      // Close after a short delay to let final audio play
      setTimeout(() => {
        openaiWs.close();
        clientSocket.close();
      }, 2000);
      break;
  }

  // Send tool result back to OpenAI
  openaiWs.send(JSON.stringify({
    type: "conversation.item.create",
    item: {
      type: "function_call_output",
      call_id: callId,
      output: JSON.stringify(result)
    }
  }));
  openaiWs.send(JSON.stringify({ type: "response.create" }));
}

// --- Flush transcripts to DB ---
function flushTranscripts(supabase: any, state: SessionState) {
  supabase
    .from("live_calls")
    .update({
      transcripts: state.transcripts,
      pickup: state.booking.pickup,
      destination: state.booking.destination,
      passengers: state.booking.passengers,
      fare: state.booking.fare,
      eta: state.booking.eta,
      updated_at: new Date().toISOString(),
    })
    .eq("call_id", state.callId)
    .then(() => {});
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const chunk = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}

function int16ToBytesLE(samples: Int16Array): Uint8Array {
  const out = new Uint8Array(samples.length * 2);
  for (let i = 0; i < samples.length; i++) {
    const v = samples[i];
    out[i * 2] = v & 0xff;
    out[i * 2 + 1] = (v >> 8) & 0xff;
  }
  return out;
}

// μ-law (G.711) → PCM16
function ulawToPcm16(ulaw: Uint8Array): Int16Array {
  const out = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    let u = (~ulaw[i]) & 0xff;
    const sign = u & 0x80;
    const exponent = (u >> 4) & 0x07;
    const mantissa = u & 0x0f;
    let sample = ((mantissa << 3) + 0x84) << exponent;
    sample -= 0x84;
    out[i] = sign ? -sample : sample;
  }
  return out;
}

// Simple linear-resample for PCM16 (sufficient for upsample 8k → 24k)
function upsamplePcm16(input: Int16Array, fromRate: number, toRate: number): Int16Array {
  if (fromRate === toRate) return input;

  const ratio = toRate / fromRate;
  const outLen = Math.max(1, Math.floor(input.length * ratio));
  const out = new Int16Array(outLen);

  for (let i = 0; i < outLen; i++) {
    const pos = i / ratio;
    const idx = Math.floor(pos);
    const frac = pos - idx;

    const s0 = input[Math.min(idx, input.length - 1)];
    const s1 = input[Math.min(idx + 1, input.length - 1)];
    out[i] = (s0 + (s1 - s0) * frac) as unknown as number;
  }

  return out;
}

