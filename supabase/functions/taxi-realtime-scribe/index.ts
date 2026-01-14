import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const ELEVENLABS_API_KEY = Deno.env.get("ELEVENLABS_API_KEY_1") || Deno.env.get("ELEVENLABS_API_KEY");
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const DEFAULT_COMPANY = "247 Radio Carz";
const DEFAULT_AGENT = "Ada";
const DEFAULT_VOICE = "shimmer";

// --- System Prompt ---
const SYSTEM_PROMPT = `
You are receiving transcribed speech from a phone call. Transcriptions may contain errors (e.g., "click on street" instead of "Coventry"). Use context to interpret correctly.

You are {{agent_name}}, a friendly taxi booking assistant for {{company_name}}.

PERSONALITY: Warm, patient, relaxed. Speak in 1–2 short sentences. Ask ONLY ONE question at a time.

GREETING:
- New caller: "Hello, welcome to {{company_name}}! I'm {{agent_name}}. What's your name?"
- Returning caller (no booking): "Hello [NAME]! Where would you like to be picked up from?"
- Returning caller (active booking): "Hello [NAME]! I can see you have an active booking. Would you like to keep it, change it, or cancel it?"

BOOKING FLOW (STRICT ORDER):
1. Get PICKUP address FIRST. Ask: "Where would you like to be picked up from?"
2. Get DESTINATION address SECOND. Ask: "And where are you going to?"
3. Get PASSENGERS if not mentioned. Ask: "How many passengers?"
4. ONLY for AIRPORTS or TRAIN STATIONS: ask "Any bags?" — otherwise skip bags entirely.
5. When ALL details are known → YOU MUST call book_taxi tool IMMEDIATELY. Do NOT speak a confirmation until the tool returns.

RULES:
1. ALWAYS ask for PICKUP before DESTINATION. Never assume or swap them.
2. NEVER repeat addresses, fares, or full routes.
3. NEVER say: "Just to double-check", "Shall I book that?", "Is that correct?".
4. NEVER ask about bags unless destination is an AIRPORT or TRAIN STATION.
5. If user corrects name → call save_customer_name immediately.
6. After book_taxi returns: say ONLY "Booked! [X] minutes, [FARE]. Anything else?" then WAIT for response.
7. If user says "no" or "that's all" after "Anything else?" → say "Safe travels!" and call end_call.
8. If user says "cancel" → call cancel_booking FIRST, then say "That's cancelled..."
9. GLOBAL service — accept any address.
10. If "usual trip" → summarize last trip, ask "Shall I book that again?" → wait for YES.

IMPORTANT: If user says "going TO [address]" that is DESTINATION, not pickup.
If user says "from [address]" or "pick me up at [address]" that is PICKUP.

CRITICAL TOOL RULES:
- You MUST call book_taxi tool when you have pickup, destination, and passengers. DO NOT just say "Booked" without calling the tool.
- You MUST call end_call tool after saying "Safe travels!". DO NOT keep talking, re-greet, or restart booking after end_call.
- You MUST call cancel_booking tool before saying "cancelled". DO NOT just say it's cancelled without calling the tool.
- NEVER say a booking is confirmed unless you have called the book_taxi tool and received a response.
- NEVER invent fares or ETAs — use the values returned by the book_taxi tool.
`;

// --- Tool Schemas ---
const TOOLS = [
  {
    type: "function",
    name: "save_customer_name",
    description: "Save customer name. Call IMMEDIATELY on correction.",
    parameters: { 
      type: "object", 
      properties: { name: { type: "string" } }, 
      required: ["name"] 
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description:
      "MANDATORY: You MUST call this tool to book a taxi. Do NOT say 'Booked' without calling this tool first. Call when you have pickup, destination, and passengers.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
        passengers: {
          type: "integer",
          minimum: 1,
          description: "Number of passengers",
        },
        bags: {
          type: "integer",
          minimum: 0,
          description: "Number of bags (only for airport/station trips)",
        },
        pickup_time: { type: "string", description: "ISO timestamp or 'now'" },
        vehicle_type: { type: "string", enum: ["saloon", "estate", "mpv", "minibus"] },
      },
      required: ["pickup", "destination", "passengers"],
    },
  },
  {
    type: "function",
    name: "cancel_booking",
    description: "Cancel active booking. CALL BEFORE saying 'cancelled'.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "modify_booking",
    description: "Modify existing booking.",
    parameters: {
      type: "object",
      properties: {
        field_to_change: { type: "string", enum: ["pickup", "destination", "passengers", "bags", "time"] },
        new_value: { type: "string" }
      },
      required: ["field_to_change", "new_value"]
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "End call after 'Safe travels!'.",
    parameters: { type: "object", properties: {} }
  }
];

// --- STT Corrections ---
const STT_CORRECTIONS: Record<string, string> = {
  "come to sleep": "cancel it",
  "click on street": "Coventry",
  "heather oh": "Heathrow",
  "gat wick": "Gatwick",
  "birming ham": "Birmingham",
  "david rose": "david road",
  "count to three": "cancel",
  "can't sell it": "cancel it",
  "concert": "cancel",
  "counter": "cancel",
};

function correctTranscript(text: string): string {
  let corrected = text.toLowerCase();
  for (const [bad, good] of Object.entries(STT_CORRECTIONS)) {
    if (corrected.includes(bad)) {
      return text.replace(new RegExp(bad, "gi"), good);
    }
  }
  return text;
}

// µ-law to PCM16 conversion (for ElevenLabs Scribe which needs PCM)
function ulawToPcm16(ulawData: Uint8Array): Int16Array {
  const pcm = new Int16Array(ulawData.length);
  for (let i = 0; i < ulawData.length; i++) {
    const ulaw = ~ulawData[i] & 0xFF;
    const sign = (ulaw & 0x80) ? -1 : 1;
    const exponent = (ulaw >> 4) & 0x07;
    const mantissa = ulaw & 0x0F;
    let sample = ((mantissa << 3) + 0x84) << exponent;
    sample -= 0x84;
    pcm[i] = sign * sample;
  }
  return pcm;
}

// PCM16 8kHz → 24kHz upsampling (for OpenAI TTS output)
function upsample8kTo24k(pcm8k: Int16Array): Int16Array {
  const pcm24k = new Int16Array(pcm8k.length * 3);
  for (let i = 0; i < pcm8k.length - 1; i++) {
    const s0 = pcm8k[i];
    const s1 = pcm8k[i + 1];
    pcm24k[i * 3] = s0;
    pcm24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
    pcm24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
  }
  const lastIdx = pcm8k.length - 1;
  pcm24k[lastIdx * 3] = pcm8k[lastIdx];
  pcm24k[lastIdx * 3 + 1] = pcm8k[lastIdx];
  pcm24k[lastIdx * 3 + 2] = pcm8k[lastIdx];
  return pcm24k;
}

// --- Session State ---
interface TranscriptItem {
  role: "user" | "assistant";
  text: string;
  timestamp: string;
}

interface SessionState {
  callId: string;
  phone: string;
  companyName: string;
  agentName: string;
  voice: string;
  customerName: string | null;
  hasActiveBooking: boolean;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    bags: number | null;
  };
  transcripts: TranscriptItem[];
  assistantTranscriptIndex: number | null;
  transcriptFlushTimer: number | null;
  isAdaSpeaking: boolean;
  echoGuardUntil: number;
  // ElevenLabs Scribe
  scribeWs: WebSocket | null;
  audioBuffer: Uint8Array[];
  pendingTranscript: string;
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426, headers: corsHeaders });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  
  let openaiWs: WebSocket | null = null;
  let scribeWs: WebSocket | null = null;
  let state: SessionState | null = null;
  let openaiConnected = false;
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

  // --- Connect to ElevenLabs Scribe (Realtime STT) ---
  const connectToScribe = async (sessionState: SessionState) => {
    console.log(`[${sessionState.callId}] 🎤 Connecting to ElevenLabs Scribe...`);
    
    // Get single-use token for Scribe
    const tokenResponse = await fetch(
      "https://api.elevenlabs.io/v1/single-use-token/realtime_scribe",
      {
        method: "POST",
        headers: {
          "xi-api-key": ELEVENLABS_API_KEY!,
        },
      }
    );
    
    const { token } = await tokenResponse.json();
    if (!token) {
      console.error(`[${sessionState.callId}] ❌ Failed to get Scribe token`);
      return;
    }

    // Connect to Scribe WebSocket
    // IMPORTANT: Scribe v2 realtime requires session config via query params (model_id, audio_format, commit_strategy)
    const scribeUrl =
      `wss://api.elevenlabs.io/v1/scribe/v2_realtime` +
      `?token=${token}` +
      `&model_id=scribe_v2_realtime` +
      `&language_code=en` +
      `&audio_format=ulaw_8000` +
      `&commit_strategy=vad`;

    scribeWs = new WebSocket(scribeUrl);

    scribeWs.onopen = () => {
      console.log(`[${sessionState.callId}] ✅ ElevenLabs Scribe connected`);

      // Flush any buffered pre-connect audio (to avoid missing the first utterance)
      if (sessionState.audioBuffer.length > 0) {
        console.log(
          `[${sessionState.callId}] 📤 Flushing ${sessionState.audioBuffer.length} buffered audio chunk(s) to Scribe`
        );
        for (const chunk of sessionState.audioBuffer.splice(0)) {
          try {
            let binary = "";
            for (let i = 0; i < chunk.length; i++) binary += String.fromCharCode(chunk[i]);
            const base64Audio = btoa(binary);
            scribeWs?.send(JSON.stringify({ audio_base_64: base64Audio }));
          } catch (e) {
            console.error(`[${sessionState.callId}] Buffer flush error:`, e);
          }
        }
      }
    };

    scribeWs.onmessage = (event) => {
      try {
        const message = JSON.parse(event.data);
        handleScribeMessage(message, sessionState);
      } catch (e) {
        console.error(`[${sessionState.callId}] Scribe parse error:`, e);
      }
    };

    scribeWs.onclose = () => {
      console.log(`[${sessionState.callId}] 🔌 Scribe disconnected`);
    };

    scribeWs.onerror = (err) => {
      console.error(`[${sessionState.callId}] ❌ Scribe error:`, err);
    };

    sessionState.scribeWs = scribeWs;
  };

  // --- Handle Scribe Messages ---
  const handleScribeMessage = (message: any, sessionState: SessionState) => {
    switch (message.type) {
      case "partial_transcript":
        // Live interim transcript
        sessionState.pendingTranscript = message.text || "";
        socket.send(JSON.stringify({
          type: "transcript",
          text: message.text,
          role: "user",
          partial: true
        }));
        break;

      case "committed_transcript":
        // Finalized transcript - send to OpenAI
        const userText = correctTranscript(message.text || "");
        console.log(`[${sessionState.callId}] 👤 User (Scribe): "${userText}"`);
        
        if (userText && userText.trim()) {
          // Skip if echo guard is active
          if (sessionState.isAdaSpeaking || Date.now() < sessionState.echoGuardUntil) {
            console.log(`[${sessionState.callId}] 🔇 Echo guard: ignoring "${userText}"`);
            return;
          }

          socket.send(JSON.stringify({
            type: "transcript",
            text: userText,
            role: "user",
            partial: false
          }));

          sessionState.transcripts.push({
            role: "user",
            text: userText,
            timestamp: new Date().toISOString(),
          });
          scheduleTranscriptFlush(sessionState);

          // Send text to OpenAI (not audio!)
          sendTextToOpenAI(userText, sessionState);
        }
        sessionState.pendingTranscript = "";
        break;

      case "session_started":
        console.log(`[${sessionState.callId}] 🎤 Scribe session started`);
        break;

      case "error":
        console.error(`[${sessionState.callId}] 🚨 Scribe error:`, message);
        break;
    }
  };

  // --- Send Text to OpenAI (text-in mode) ---
  const sendTextToOpenAI = (text: string, sessionState: SessionState) => {
    if (!openaiWs || !openaiConnected) return;

    // Create a user message item
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text }]
      }
    }));

    // Trigger response
    openaiWs.send(JSON.stringify({ type: "response.create" }));
  };

  // --- Connect to OpenAI Realtime (audio-out only) ---
  const connectToOpenAI = (sessionState: SessionState) => {
    const url = `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`;
    
    openaiWs = new WebSocket(url, [
      "realtime",
      `openai-insecure-api-key.${OPENAI_API_KEY}`,
      "openai-beta.realtime-v1"
    ]);

    openaiWs.onopen = () => {
      console.log(`[${sessionState.callId}] ✅ Connected to OpenAI Realtime`);
      openaiConnected = true;
      sendSessionUpdate(sessionState);
    };

    openaiWs.onmessage = (event) => {
      try {
        const message = JSON.parse(event.data);
        handleOpenAIMessage(message, sessionState);
      } catch (e) {
        console.error(`[${sessionState.callId}] Failed to parse OpenAI message:`, e);
      }
    };

    openaiWs.onclose = () => {
      console.log(`[${sessionState.callId}] 🔌 OpenAI WebSocket closed`);
      openaiConnected = false;
    };

    openaiWs.onerror = (err) => {
      console.error(`[${sessionState.callId}] ❌ OpenAI WebSocket error:`, err);
    };
  };

  // --- Send Session Update (disable audio input - we use Scribe) ---
  const sendSessionUpdate = (sessionState: SessionState) => {
    let prompt = SYSTEM_PROMPT
      .replace(/\{\{agent_name\}\}/g, sessionState.agentName)
      .replace(/\{\{company_name\}\}/g, sessionState.companyName);

    if (sessionState.customerName) {
      if (sessionState.hasActiveBooking) {
        prompt += `\n\nCURRENT CONTEXT: Caller is ${sessionState.customerName} with active booking.`;
      } else {
        prompt += `\n\nCURRENT CONTEXT: Caller is ${sessionState.customerName} (returning).`;
      }
    }

    const sessionUpdate = {
      type: "session.update",
      session: {
        modalities: ["text", "audio"],  // Text input, audio output
        instructions: prompt,
        voice: sessionState.voice,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        // DISABLE built-in STT - we use ElevenLabs Scribe
        input_audio_transcription: null,
        turn_detection: null,  // No VAD - Scribe handles it
        temperature: 0.6,
        tools: TOOLS,
        tool_choice: "auto"
      }
    };

    openaiWs?.send(JSON.stringify(sessionUpdate));
    console.log(`[${sessionState.callId}] 📝 Session updated (Scribe STT mode)`);
  };

  // Fire-and-forget DB flush
  const flushTranscriptsToDb = (sessionState: SessionState) => {
    const transcriptsCopy = [...sessionState.transcripts];
    supabase
      .from("live_calls")
      .update({
        transcripts: transcriptsCopy,
        updated_at: new Date().toISOString(),
      })
      .eq("call_id", sessionState.callId)
      .then(({ error }) => {
        if (error) console.error(`[${sessionState.callId}] DB flush error:`, error);
      });
  };

  const FLUSH_INTERVAL_MS = 5000;
  
  const scheduleTranscriptFlush = (sessionState: SessionState) => {
    if (sessionState.transcriptFlushTimer) return;
    sessionState.transcriptFlushTimer = setTimeout(() => {
      sessionState.transcriptFlushTimer = null;
      flushTranscriptsToDb(sessionState);
    }, FLUSH_INTERVAL_MS) as unknown as number;
  };

  const immediateFlush = (sessionState: SessionState) => {
    if (sessionState.transcriptFlushTimer) {
      clearTimeout(sessionState.transcriptFlushTimer);
      sessionState.transcriptFlushTimer = null;
    }
    flushTranscriptsToDb(sessionState);
  };

  // --- Handle OpenAI Messages ---
  const handleOpenAIMessage = (message: any, sessionState: SessionState) => {
    switch (message.type) {
      case "session.created":
        console.log(`[${sessionState.callId}] 🎉 Session created`);
        // Trigger greeting
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
        break;

      case "response.audio.delta":
        sessionState.isAdaSpeaking = true;
        socket.send(JSON.stringify({
          type: "audio",
          audio: message.delta
        }));
        break;

      case "response.audio.done":
        sessionState.isAdaSpeaking = false;
        sessionState.echoGuardUntil = Date.now() + 800;
        console.log(`[${sessionState.callId}] 🔇 Echo guard active for 800ms`);
        break;

      case "response.audio_transcript.delta": {
        const delta = String(message.delta || "");
        socket.send(JSON.stringify({
          type: "transcript",
          text: delta,
          role: "assistant",
        }));

        if (delta) {
          if (sessionState.assistantTranscriptIndex === null) {
            sessionState.transcripts.push({
              role: "assistant",
              text: delta,
              timestamp: new Date().toISOString(),
            });
            sessionState.assistantTranscriptIndex = sessionState.transcripts.length - 1;
          } else {
            const idx = sessionState.assistantTranscriptIndex;
            const prev = sessionState.transcripts[idx];
            sessionState.transcripts[idx] = { ...prev, text: (prev.text || "") + delta };
          }
          scheduleTranscriptFlush(sessionState);
        }
        break;
      }

      case "response.audio_transcript.done":
        sessionState.assistantTranscriptIndex = null;
        break;

      case "response.function_call_arguments.done":
        handleFunctionCall(
          message.name,
          message.arguments,
          message.call_id,
          sessionState
        );
        break;

      case "error":
        console.error(`[${sessionState.callId}] 🚨 OpenAI Error:`, message.error);
        break;
    }
  };

  // --- Handle Function Calls ---
  const handleFunctionCall = async (
    name: string,
    argsJson: string,
    callId: string,
    sessionState: SessionState
  ) => {
    console.log(`[${sessionState.callId}] 🔧 Tool call: ${name}`);
    
    let result: any;
    try {
      const args = JSON.parse(argsJson);

      switch (name) {
        case "save_customer_name":
          console.log(`[${sessionState.callId}] 👤 Saving name: ${args.name}`);
          sessionState.customerName = args.name;
          result = { success: true };
          break;

        case "book_taxi": {
          console.log(`[${sessionState.callId}] 🚕 Booking:`, args);
          sessionState.booking = {
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers,
            bags: args.bags || 0,
          };

          // Generate job_id upfront for tracking (and for dispatch correlation)
          const jobId = crypto.randomUUID();

          // Default fallback values (if dispatch webhook unavailable)
          let fare = "£12.50";
          let etaMinutes = 8;

          // Call external dispatch webhook for geocoding + fare/ETA
          const webhookUrl = Deno.env.get("DISPATCH_WEBHOOK_URL");
          if (webhookUrl) {
            const webhookPayload = {
              action: "book_taxi",
              job_id: jobId,
              call_id: sessionState.callId,
              caller_phone: sessionState.phone,
              caller_name: sessionState.customerName,
              ada_pickup: args.pickup,
              ada_destination: args.destination,
              pickup: args.pickup,
              destination: args.destination,
              passengers: args.passengers || 1,
              bags: args.bags || 0,
              vehicle_type: args.vehicle_type || "saloon",
              pickup_time: args.pickup_time || "now",
              timestamp: new Date().toISOString(),
            };

            console.log(`[${sessionState.callId}] 📤 Sending dispatch webhook (fire-and-forget): ${webhookUrl}`);
            console.log(`[${sessionState.callId}] 📦 Payload:`, JSON.stringify(webhookPayload));

            (async () => {
              try {
                const resp = await fetch(webhookUrl, {
                  method: "POST",
                  headers: {
                    "Content-Type": "application/json",
                    "X-Job-Id": jobId,
                    "X-Call-Id": sessionState.callId,
                  },
                  body: JSON.stringify(webhookPayload),
                });

                if (resp.ok) {
                  const respText = await resp.text();
                  console.log(
                    `[${sessionState.callId}] ✅ Dispatch webhook success: ${resp.status} - ${respText.substring(0, 200)}`
                  );
                } else {
                  console.error(
                    `[${sessionState.callId}] ❌ Dispatch webhook error: ${resp.status} ${resp.statusText}`
                  );
                }
              } catch (webhookErr) {
                console.error(`[${sessionState.callId}] ❌ Dispatch webhook failed:`, webhookErr);
              }
            })();
          } else {
            console.log(`[${sessionState.callId}] ⚠️ DISPATCH_WEBHOOK_URL not set; skipping dispatch webhook`);
          }

          // Persist booking immediately (don’t wait for webhook)
          const { error: bookingError } = await supabase.from("bookings").insert({
            id: jobId,
            call_id: sessionState.callId,
            caller_phone: sessionState.phone,
            caller_name: sessionState.customerName,
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers || 1,
            fare,
            eta: `${etaMinutes} minutes`,
            status: "confirmed",
            booking_details: { job_id: jobId },
          });

          if (bookingError) {
            console.error(`[${sessionState.callId}] Booking DB error:`, bookingError);
          }

          result = {
            success: true,
            eta_minutes: etaMinutes,
            fare,
            booking_ref: jobId,
            message: "Booking confirmed",
          };
          break;
        }

        case "cancel_booking":
          console.log(`[${sessionState.callId}] 🚫 Cancelling booking`);
          sessionState.hasActiveBooking = false;
          sessionState.booking = { pickup: null, destination: null, passengers: null, bags: null };
          result = { success: true };
          break;

        case "modify_booking":
          console.log(`[${sessionState.callId}] ✏️ Modifying:`, args);
          if (args.field_to_change === "pickup") sessionState.booking.pickup = args.new_value;
          if (args.field_to_change === "destination") sessionState.booking.destination = args.new_value;
          if (args.field_to_change === "passengers") sessionState.booking.passengers = parseInt(args.new_value);
          result = { success: true };
          break;

        case "end_call":
          console.log(`[${sessionState.callId}] 👋 Ending call`);
          result = { success: true };
          immediateFlush(sessionState);
          await supabase
            .from("live_calls")
            .update({ status: "completed", ended_at: new Date().toISOString() })
            .eq("call_id", sessionState.callId);
          break;

        default:
          result = { error: "Unknown function" };
      }
    } catch (err) {
      console.error(`[${sessionState.callId}] Function error:`, err);
      result = { error: "Failed to execute" };
    }

    // Send result back to OpenAI
    openaiWs?.send(
      JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "function_call_output",
          call_id: callId,
          output: JSON.stringify(result),
        },
      })
    );

    // CRITICAL: end_call should TERMINATE the session (no further responses / re-greetings)
    if (name === "end_call") {
      try {
        socket.send(JSON.stringify({ type: "call_ended" }));
      } catch {
        // ignore
      }
      try {
        scribeWs?.close();
      } catch {
        // ignore
      }
      try {
        openaiWs?.close();
      } catch {
        // ignore
      }
      try {
        socket.close(1000, "Call ended");
      } catch {
        // ignore
      }
      return;
    }

    // Trigger response continuation for non-ending tool calls
    openaiWs?.send(JSON.stringify({ type: "response.create" }));
  };

  // --- Bridge WebSocket Handlers ---
  socket.onopen = () => {
    console.log("[scribe] Client connected");
  };

  socket.onmessage = async (event) => {
    try {
      const isBinary = event.data instanceof Blob || event.data instanceof ArrayBuffer || event.data instanceof Uint8Array;
      
       if (isBinary) {
         if (state && scribeWs) {
           // Echo guard check
           if (state.isAdaSpeaking || Date.now() < state.echoGuardUntil) {
             return; // Skip audio during Ada's speech
           }

           let audioData: Uint8Array;

           if (event.data instanceof Blob) {
             audioData = new Uint8Array(await event.data.arrayBuffer());
           } else if (event.data instanceof ArrayBuffer) {
             audioData = new Uint8Array(event.data);
           } else {
             audioData = event.data;
           }

           // If Scribe isn't connected yet, buffer a few chunks so we don't miss the user's first words
           if (scribeWs.readyState !== WebSocket.OPEN) {
             if (state.audioBuffer.length < 60) {
               state.audioBuffer.push(audioData);
             }
             return;
           }

           // Scribe supports native telephony µ-law input (ulaw_8000). Send raw bytes as base64.
           let binary = "";
           for (let i = 0; i < audioData.length; i++) {
             binary += String.fromCharCode(audioData[i]);
           }
           const base64Audio = btoa(binary);

           scribeWs.send(JSON.stringify({ audio_base_64: base64Audio }));
         }
         return;
       }

      // Handle JSON messages
      const message = JSON.parse(event.data);

      if (message.type === "init") {
        const callId = message.call_id || `scribe-${Date.now()}`;
        const phone = message.phone || "unknown";
        
        console.log(`[${callId}] 🚀 Initializing Scribe+OpenAI session`);

        state = {
          callId,
          phone,
          companyName: message.company_name || DEFAULT_COMPANY,
          agentName: message.agent_name || DEFAULT_AGENT,
          voice: message.voice || DEFAULT_VOICE,
          customerName: message.customer_name || null,
          hasActiveBooking: message.has_active_booking || false,
          booking: { pickup: null, destination: null, passengers: null, bags: null },
          transcripts: [],
          assistantTranscriptIndex: null,
          transcriptFlushTimer: null,
          isAdaSpeaking: false,
          echoGuardUntil: 0,
          scribeWs: null,
          audioBuffer: [],
          pendingTranscript: ""
        };

        await supabase.from("live_calls").upsert({
          call_id: callId,
          caller_phone: phone,
          caller_name: state.customerName,
          status: "active",
          source: "scribe",
          transcripts: []
        }, { onConflict: "call_id" });

        // Connect to both services
        await connectToScribe(state);
        connectToOpenAI(state);

        socket.send(JSON.stringify({ 
          type: "ready", 
          call_id: callId,
          mode: "scribe"
        }));
      }

    } catch (e) {
      console.error("[scribe] Message error:", e);
    }
  };

  socket.onclose = () => {
    console.log(`[${state?.callId || "unknown"}] Client disconnected`);
    if (state) {
      immediateFlush(state);
    }
    scribeWs?.close();
    openaiWs?.close();
  };

  socket.onerror = (err) => {
    console.error("[scribe] Socket error:", err);
  };

  return response;
});
