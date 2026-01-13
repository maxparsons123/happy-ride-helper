import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
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
- Returning caller (no booking): "Hello [NAME]! Where can I take you today?"
- Returning caller (active booking): "Hello [NAME]! I can see you have an active booking. Would you like to keep it, change it, or cancel it?"

RULES:
1. NEVER repeat addresses, fares, or full routes.
2. NEVER say: "Just to double-check", "Shall I book that?", "Is that correct?".
3. When pickup, destination, passengers known → call book_taxi IMMEDIATELY.
4. For airports/stations: ask "How many passengers, and any bags?" before booking.
5. If user corrects name → call save_customer_name immediately.
6. After booking: say ONLY "Booked! [X] minutes, [FARE]. Anything else?"
7. If user says "cancel" → call cancel_booking FIRST, then say "That's cancelled..."
8. GLOBAL service — accept any address.
9. If "usual trip" → summarize last trip, ask "Shall I book that again?" → wait for YES.
10. If asked for places → call find_nearby_places, list 2–3 options.

TOOL USAGE:
- Only call book_taxi when ALL fields provided.
- NEVER invent fares/addresses.
- Call end_call after "Safe travels!".
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
    description: "Book taxi. CALL IMMEDIATELY when details known. Include 'bags' for airport trips.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string" },
        destination: { type: "string" },
        passengers: { type: "integer", minimum: 1 },
        bags: { type: "integer", minimum: 0 },
        pickup_time: { type: "string", description: "ISO timestamp or 'now'" },
        vehicle_type: { type: "string", enum: ["saloon", "estate", "mpv", "minibus"] }
      },
      required: ["pickup", "destination", "passengers"]
    }
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
    name: "find_nearby_places",
    description: "Find venues (hotel, restaurant, etc.).",
    parameters: {
      type: "object",
      properties: {
        category: { type: "string", enum: ["hotel", "restaurant", "bar", "cafe", "pub", "place"] },
        location_hint: { type: "string" }
      },
      required: ["category"]
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

// --- Session State ---
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
  transcripts: Array<{ role: string; text: string; timestamp: string }>;
}

serve(async (req) => {
  // Handle CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  // WebSocket upgrade
  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426, headers: corsHeaders });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  
  let openaiWs: WebSocket | null = null;
  let state: SessionState | null = null;
  let openaiConnected = false;
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

  // --- Connect to OpenAI ---
  const connectToOpenAI = (sessionState: SessionState) => {
    const url = `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`;
    
    openaiWs = new WebSocket(url, {
      headers: {
        "Authorization": `Bearer ${OPENAI_API_KEY}`,
        "OpenAI-Beta": "realtime=v1"
      }
    } as any);

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

  // --- Send Session Update ---
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
        modalities: ["text", "audio"],
        instructions: prompt,
        voice: sessionState.voice,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        input_audio_transcription: { model: "whisper-1" },
        turn_detection: {
          type: "server_vad",
          threshold: 0.7,
          prefix_padding_ms: 400,
          silence_duration_ms: 500
        },
        temperature: 0.6,
        tools: TOOLS,
        tool_choice: "auto"
      }
    };

    openaiWs?.send(JSON.stringify(sessionUpdate));
    console.log(`[${sessionState.callId}] 📝 Session updated`);
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
        // Forward audio to bridge
        socket.send(JSON.stringify({
          type: "audio",
          audio: message.delta
        }));
        break;

      case "response.audio_transcript.delta":
        socket.send(JSON.stringify({
          type: "transcript",
          text: message.delta,
          role: "assistant"
        }));
        break;

      case "response.audio_transcript.done":
        // Log assistant response
        sessionState.transcripts.push({
          role: "assistant",
          text: message.transcript || "",
          timestamp: new Date().toISOString()
        });
        break;

      case "conversation.item.input_audio_transcription.completed":
        // User transcript from Whisper
        const userText = correctTranscript(message.transcript || "");
        console.log(`[${sessionState.callId}] 👤 User: "${userText}"`);
        
        socket.send(JSON.stringify({
          type: "transcript",
          text: userText,
          role: "user"
        }));
        
        sessionState.transcripts.push({
          role: "user",
          text: userText,
          timestamp: new Date().toISOString()
        });
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

        case "book_taxi":
          console.log(`[${sessionState.callId}] 🚕 Booking:`, args);
          sessionState.booking = {
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers,
            bags: args.bags || 0
          };
          
          // Create booking in DB
          const { error: bookingError } = await supabase.from("bookings").insert({
            call_id: sessionState.callId,
            caller_phone: sessionState.phone,
            caller_name: sessionState.customerName,
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers || 1,
            status: "confirmed"
          });
          
          if (bookingError) {
            console.error(`[${sessionState.callId}] Booking DB error:`, bookingError);
          }
          
          result = { 
            success: true, 
            eta_minutes: 8, 
            fare: "£12.50",
            message: "Booking confirmed"
          };
          break;

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

        case "find_nearby_places":
          console.log(`[${sessionState.callId}] 📍 Finding:`, args.category);
          result = {
            places: [
              { name: "The Grand Hotel", rating: 4.5 },
              { name: "Riverside Restaurant", rating: 4.7 }
            ]
          };
          break;

        case "end_call":
          console.log(`[${sessionState.callId}] 👋 Ending call`);
          result = { success: true };
          // Update call status
          await supabase.from("live_calls")
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
    openaiWs?.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "function_call_output",
        call_id: callId,
        output: JSON.stringify(result)
      }
    }));

    // Trigger response continuation
    openaiWs?.send(JSON.stringify({ type: "response.create" }));
  };

  // --- Bridge WebSocket Handlers ---
  socket.onopen = () => {
    console.log("[simple] Client connected");
  };

  socket.onmessage = async (event) => {
    try {
      // Handle binary audio
      if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
        if (openaiConnected && openaiWs) {
          const audioData = event.data instanceof ArrayBuffer 
            ? new Uint8Array(event.data) 
            : event.data;
          
          // Convert to base64 and send
          const base64Audio = btoa(String.fromCharCode(...audioData));
          openaiWs.send(JSON.stringify({
            type: "input_audio_buffer.append",
            audio: base64Audio
          }));
        }
        return;
      }

      // Handle JSON messages
      const message = JSON.parse(event.data);

      if (message.type === "init") {
        const callId = message.call_id || `simple-${Date.now()}`;
        const phone = message.phone || "unknown";
        
        console.log(`[${callId}] 🚀 Initializing simple session`);

        // Initialize state
        state = {
          callId,
          phone,
          companyName: message.company_name || DEFAULT_COMPANY,
          agentName: message.agent_name || DEFAULT_AGENT,
          voice: message.voice || DEFAULT_VOICE,
          customerName: message.customer_name || null,
          hasActiveBooking: message.has_active_booking || false,
          booking: { pickup: null, destination: null, passengers: null, bags: null },
          transcripts: []
        };

        // Create live call record
        await supabase.from("live_calls").upsert({
          call_id: callId,
          caller_phone: phone,
          caller_name: state.customerName,
          status: "active",
          source: "simple",
          transcripts: []
        }, { onConflict: "call_id" });

        // Connect to OpenAI
        connectToOpenAI(state);

        socket.send(JSON.stringify({ 
          type: "ready", 
          call_id: callId,
          mode: "simple"
        }));
      }

      if (message.type === "audio" && openaiConnected && openaiWs) {
        // Base64 audio from bridge
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: message.audio
        }));
      }

    } catch (e) {
      console.error("[simple] Message error:", e);
    }
  };

  socket.onclose = () => {
    console.log(`[${state?.callId || "unknown"}] Client disconnected`);
    openaiWs?.close();
  };

  socket.onerror = (err) => {
    console.error("[simple] Socket error:", err);
  };

  return response;
});
