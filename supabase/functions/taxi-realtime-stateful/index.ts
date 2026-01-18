// ═══════════════════════════════════════════════════════════════════════════════
// ADA TAXI DISPATCH v1 - STATE-MANAGED BOOKING MODULE
// ═══════════════════════════════════════════════════════════════════════════════
// 
// This module treats the conversation like a database transaction:
// - AI cannot move to the next question until the current one is "saved" to the Booking_Struct
// - sync_booking_data acts as a "Digital Notepad" that tracks field completion
// - Summary is GATE-LOCKED until all 4 fields are complete
//
// ═══════════════════════════════════════════════════════════════════════════════

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

const DEFAULT_COMPANY = "Taxibot Demo";
const DEFAULT_AGENT = "ADA";
const DEFAULT_VOICE = "alloy"; // Per user specification

// ═══════════════════════════════════════════════════════════════════════════════
// STATE-LOCKED SYSTEM PROMPT
// The AI MUST call sync_booking_data after every user response
// ═══════════════════════════════════════════════════════════════════════════════
const SYSTEM_PROMPT = `
# IDENTITY
You are ADA, the professional taxi assistant for Taxibot. 
Your goal is to fill the 'Booking_Struct' sequentially.
Voice: Warm, clear, professionally casual.

# STATE RULES (CRITICAL - MUST FOLLOW)
- You MUST call 'sync_booking_data' after every user response to save what they said.
- Do NOT ask for 'Destination' until 'Pickup' is in the Struct (sync_booking_data was called with field="pickup" and is_field_complete=true).
- Do NOT ask for 'Passengers' until 'Destination' is in the Struct.
- Do NOT ask for 'Time' until 'Passengers' is in the Struct.
- NEVER use 'As directed', 'Unknown', or any placeholder as a value.
- If the user's answer is vague or unclear, call sync_booking_data with is_field_complete=false and ask a clarifying question.

# PHASE 1: THE WELCOME (Play immediately on call connect)
Say EXACTLY: "Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. You can switch languages at any time, just say the language you prefer, and we'll remember it for your next booking. So, let's get started."

Then ask: "Where would you like to be picked up?"

# PHASE 2: SEQUENTIAL GATHERING (Strict Order)
After each user response, call sync_booking_data with the appropriate field.

1. PICKUP: "Where would you like to be picked up?" 
   - Wait for specific address → sync_booking_data(field="pickup", value="...", is_field_complete=true/false)
   - If unclear (e.g., "the pub"): set is_field_complete=false and ask "Which pub are you at exactly?"

2. DESTINATION: "And what is your destination?" (Only ask AFTER pickup is complete)
   - Wait for specific address → sync_booking_data(field="destination", value="...", is_field_complete=true/false)
   - If unclear: set is_field_complete=false and ask for clarification

3. PASSENGERS: "How many people will be travelling?" (Only ask AFTER destination is complete)
   - Wait for number → sync_booking_data(field="passengers", value="...", is_field_complete=true/false)
   - If unclear: ask "Just to confirm, how many passengers?"

4. TIME: "When do you need the taxi?" (Only ask AFTER passengers is complete)
   - Default to "now" if they say "ASAP" or "as soon as possible"
   - → sync_booking_data(field="pickup_time", value="...", is_field_complete=true/false)

# PHASE 3: THE SUMMARY (Gate Keeper - Only when ALL 4 fields are complete)
Once sync_booking_data has been called successfully (is_field_complete=true) for ALL of: pickup, destination, passengers, pickup_time:

Say: "Alright, let me quickly summarize your booking. You'd like to be picked up at [pickup], and travel to [destination]. There will be [passengers] passengers, and you'd like to be picked up [pickup_time]. Is that correct?"

Wait for "yes" or "no".
- If "no": Ask which part they'd like to change.
- If "yes": Proceed to Phase 4.

# PHASE 4: PRICING (State Lock)
After user confirms summary with "Yes", say: "Great, one moment please while I check the trip price and estimated arrival time."
→ CALL get_dispatch_quote()

Once the tool returns data, say ONLY:
"The trip fare will be [price], and the estimated arrival time is [ETA]. Would you like me to confirm this booking for you?"

🚫 RULE: Do NOT repeat addresses here. Focus only on Price and ETA.

# PHASE 5: DISPATCH & CLOSE
After user confirms pricing with "Yes":
Say: "Perfect, thank you. I'm making the booking now. You'll receive the booking details and ride updates via WhatsApp."
→ CALL confirm_and_close()

Then choose ONE closing randomly:
- "Just so you know, you can also book a taxi by sending us a WhatsApp voice note."
- "Next time, feel free to book your taxi using a WhatsApp voice message."
- "You can always book again by simply sending us a voice note on WhatsApp."

Final Sign-off: "Thank you for trying the Taxibot demo, and have a safe journey."

# GUARDRAILS
❌ NEVER state a price or ETA unless the tool returns that exact value.
❌ NEVER use 'As directed' or any placeholder - always ask for specifics.
❌ NEVER move to Summary until all 4 fields have is_field_complete=true.
❌ NEVER repeat addresses after the summary is confirmed.
✅ If address is unclear, call sync_booking_data with is_field_complete=false and ask for clarification.
✅ House numbers are critical. If unclear: "Could you repeat that number?"
`;

// ═══════════════════════════════════════════════════════════════════════════════
// TOOL DEFINITIONS - State-Managed Functions
// ═══════════════════════════════════════════════════════════════════════════════
const TOOLS = [
  {
    type: "function",
    name: "sync_booking_data",
    description: "The 'Digital Notepad'. MUST be called after every user response to save their answer into the booking structure. This tracks which fields are complete and controls the conversation flow.",
    parameters: {
      type: "object",
      properties: {
        field: {
          type: "string",
          enum: ["pickup", "destination", "passengers", "pickup_time"],
          description: "The specific field being updated right now."
        },
        value: {
          type: "string",
          description: "The exact data provided by the user (e.g., '52A David Road', '3', 'now'). Never use placeholders like 'As directed'."
        },
        is_field_complete: {
          type: "boolean",
          description: "Set to true if the info is clear and specific; false if you need to ask a clarifying question."
        }
      },
      required: ["field", "value", "is_field_complete"]
    }
  },
  {
    type: "function",
    name: "get_dispatch_quote",
    description: "Calls the pricing engine to get fare and ETA. Only call this AFTER the user confirms the booking summary with 'Yes'.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "confirm_and_close",
    description: "Finalizes the booking and triggers the WhatsApp notification. Only call this AFTER the user confirms the price with 'Yes'.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "end_call",
    description: "Disconnect the SIP line after the 'safe journey' message.",
    parameters: { type: "object", properties: {} }
  }
];

// ═══════════════════════════════════════════════════════════════════════════════
// BOOKING STRUCT - The "Digital Notepad" State
// ═══════════════════════════════════════════════════════════════════════════════
interface BookingStruct {
  pickup: string | null;
  pickup_complete: boolean;
  destination: string | null;
  destination_complete: boolean;
  passengers: string | null;
  passengers_complete: boolean;
  pickup_time: string | null;
  pickup_time_complete: boolean;
}

interface TranscriptItem {
  role: "user" | "assistant" | "system";
  text: string;
  timestamp: string;
}

interface SessionState {
  callId: string;
  phone: string;
  companyName: string;
  agentName: string;
  voice: string;
  
  // The state-managed booking struct
  bookingStruct: BookingStruct;
  
  // Transcripts for logging
  transcripts: TranscriptItem[];
  
  // Quote data from dispatch
  pendingQuote: { fare: string; eta: string } | null;
  
  // Call state
  callEnded: boolean;
  bookingConfirmed: boolean;
  summaryConfirmed: boolean;
  
  // OpenAI response tracking
  openAiResponseActive: boolean;
  assistantTranscriptBuffer: string;
  
  // Inbound audio format
  inboundAudioFormat: "ulaw" | "slin" | "slin16";
  inboundSampleRate: number;
  
  // Keep-alive and timeout tracking
  lastActivityAt: number;
  keepAliveTimer: number | null;
  sessionTimeoutTimer: number | null;
}

// Check if all 4 fields are complete - THE GATE
function isBookingStructComplete(struct: BookingStruct): boolean {
  return struct.pickup_complete && 
         struct.destination_complete && 
         struct.passengers_complete && 
         struct.pickup_time_complete;
}

// Get the next field to ask about (enforces sequential order)
function getNextIncompleteField(struct: BookingStruct): string | null {
  if (!struct.pickup_complete) return "pickup";
  if (!struct.destination_complete) return "destination";
  if (!struct.passengers_complete) return "passengers";
  if (!struct.pickup_time_complete) return "pickup_time";
  return null; // All complete!
}

// ═══════════════════════════════════════════════════════════════════════════════
// MAIN SERVER
// ═══════════════════════════════════════════════════════════════════════════════

serve(async (req: Request) => {
  // CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  // WebSocket upgrade check
  const upgradeHeader = req.headers.get("Upgrade");
  if (!upgradeHeader || upgradeHeader.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket upgrade", { status: 426, headers: corsHeaders });
  }

  // Increase idle timeout to 10 minutes (600 seconds)
  const { socket, response } = Deno.upgradeWebSocket(req, { idleTimeout: 600 });
  
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
  
  // Keep-alive configuration
  const KEEPALIVE_INTERVAL_MS = 15000; // Send ping every 15 seconds
  const SESSION_TIMEOUT_MS = 10 * 60 * 1000; // 10 minute max session
  
  // Initialize session state with empty booking struct
  const sessionState: SessionState = {
    callId: `stateful-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
    phone: "",
    companyName: DEFAULT_COMPANY,
    agentName: DEFAULT_AGENT,
    voice: DEFAULT_VOICE,
    bookingStruct: {
      pickup: null,
      pickup_complete: false,
      destination: null,
      destination_complete: false,
      passengers: null,
      passengers_complete: false,
      pickup_time: null,
      pickup_time_complete: false,
    },
    transcripts: [],
    pendingQuote: null,
    callEnded: false,
    bookingConfirmed: false,
    summaryConfirmed: false,
    openAiResponseActive: false,
    assistantTranscriptBuffer: "",
    inboundAudioFormat: "slin16",
    inboundSampleRate: 16000,
    lastActivityAt: Date.now(),
    keepAliveTimer: null,
    sessionTimeoutTimer: null,
  };

  let openaiWs: WebSocket | null = null;
  let openaiConnected = false;

  // ═══════════════════════════════════════════════════════════════════════════════
  // TOOL HANDLERS
  // ═══════════════════════════════════════════════════════════════════════════════
  
  const handleSyncBookingData = (args: { field: string; value: string; is_field_complete: boolean }): string => {
    const { field, value, is_field_complete } = args;
    
    console.log(`[${sessionState.callId}] 📝 sync_booking_data: field=${field}, value="${value}", complete=${is_field_complete}`);
    
    // Update the booking struct
    switch (field) {
      case "pickup":
        sessionState.bookingStruct.pickup = value;
        sessionState.bookingStruct.pickup_complete = is_field_complete;
        break;
      case "destination":
        sessionState.bookingStruct.destination = value;
        sessionState.bookingStruct.destination_complete = is_field_complete;
        break;
      case "passengers":
        sessionState.bookingStruct.passengers = value;
        sessionState.bookingStruct.passengers_complete = is_field_complete;
        break;
      case "pickup_time":
        sessionState.bookingStruct.pickup_time = value;
        sessionState.bookingStruct.pickup_time_complete = is_field_complete;
        break;
      default:
        return JSON.stringify({ success: false, error: `Unknown field: ${field}` });
    }
    
    // Log current state
    const nextField = getNextIncompleteField(sessionState.bookingStruct);
    const isComplete = isBookingStructComplete(sessionState.bookingStruct);
    
    console.log(`[${sessionState.callId}] 📊 Booking Struct State:`);
    console.log(`    pickup: ${sessionState.bookingStruct.pickup || "(empty)"} [${sessionState.bookingStruct.pickup_complete ? "✓" : "✗"}]`);
    console.log(`    destination: ${sessionState.bookingStruct.destination || "(empty)"} [${sessionState.bookingStruct.destination_complete ? "✓" : "✗"}]`);
    console.log(`    passengers: ${sessionState.bookingStruct.passengers || "(empty)"} [${sessionState.bookingStruct.passengers_complete ? "✓" : "✗"}]`);
    console.log(`    pickup_time: ${sessionState.bookingStruct.pickup_time || "(empty)"} [${sessionState.bookingStruct.pickup_time_complete ? "✓" : "✗"}]`);
    console.log(`    → All complete: ${isComplete}, Next field: ${nextField || "READY FOR SUMMARY"}`);
    
    // Build response with state guidance
    const response: any = {
      success: true,
      field_saved: field,
      value_saved: value,
      is_complete: is_field_complete,
      booking_struct_complete: isComplete,
    };
    
    if (!is_field_complete) {
      response.instruction = `The value "${value}" for ${field} is unclear. Ask a clarifying question to get a specific value.`;
    } else if (!isComplete) {
      response.next_field = nextField;
      response.instruction = `Field "${field}" is saved. Now ask about "${nextField}".`;
    } else {
      response.instruction = "All 4 fields are complete! You may now proceed to the Summary phase.";
    }
    
    return JSON.stringify(response);
  };
  
  const handleGetDispatchQuote = (): string => {
    // Check gate: only allow if summary was confirmed
    if (!isBookingStructComplete(sessionState.bookingStruct)) {
      console.log(`[${sessionState.callId}] ⚠️ get_dispatch_quote called before booking struct complete!`);
      return JSON.stringify({
        success: false,
        error: "Cannot get quote - booking details are incomplete. Complete all 4 fields first."
      });
    }
    
    // Generate mock fare/ETA (in production, this would call a real dispatch API)
    const baseFare = Math.floor(Math.random() * 15) + 8; // £8-23
    const pence = Math.random() < 0.5 ? 0 : 50;
    const fare = pence > 0 ? `£${baseFare}.${pence}` : `£${baseFare}`;
    const etaMinutes = Math.floor(Math.random() * 10) + 3; // 3-12 minutes
    const eta = `${etaMinutes} minutes`;
    
    console.log(`[${sessionState.callId}] 💰 Quote generated: fare=${fare}, ETA=${eta}`);
    
    sessionState.pendingQuote = { fare, eta };
    
    return JSON.stringify({
      success: true,
      fare,
      eta,
      pickup: sessionState.bookingStruct.pickup,
      destination: sessionState.bookingStruct.destination,
      passengers: sessionState.bookingStruct.passengers,
      pickup_time: sessionState.bookingStruct.pickup_time,
    });
  };
  
  const handleConfirmAndClose = (): string => {
    if (!sessionState.pendingQuote) {
      console.log(`[${sessionState.callId}] ⚠️ confirm_and_close called without quote!`);
      return JSON.stringify({
        success: false,
        error: "Cannot confirm booking - no quote has been provided yet."
      });
    }
    
    console.log(`[${sessionState.callId}] ✅ Booking CONFIRMED!`);
    console.log(`    Pickup: ${sessionState.bookingStruct.pickup}`);
    console.log(`    Destination: ${sessionState.bookingStruct.destination}`);
    console.log(`    Passengers: ${sessionState.bookingStruct.passengers}`);
    console.log(`    Time: ${sessionState.bookingStruct.pickup_time}`);
    console.log(`    Fare: ${sessionState.pendingQuote.fare}`);
    console.log(`    ETA: ${sessionState.pendingQuote.eta}`);
    
    sessionState.bookingConfirmed = true;
    
    // Store booking in database (fire and forget)
    supabase.from("bookings").insert({
      call_id: sessionState.callId,
      caller_phone: sessionState.phone || "unknown",
      pickup: sessionState.bookingStruct.pickup,
      destination: sessionState.bookingStruct.destination,
      passengers: parseInt(sessionState.bookingStruct.passengers || "1", 10),
      fare: sessionState.pendingQuote.fare,
      eta: sessionState.pendingQuote.eta,
      status: "confirmed",
    }).then(({ error }) => {
      if (error) console.error(`[${sessionState.callId}] DB insert error:`, error);
    });
    
    return JSON.stringify({
      success: true,
      message: "Booking confirmed! WhatsApp notification will be sent.",
      booking: {
        pickup: sessionState.bookingStruct.pickup,
        destination: sessionState.bookingStruct.destination,
        passengers: sessionState.bookingStruct.passengers,
        pickup_time: sessionState.bookingStruct.pickup_time,
        fare: sessionState.pendingQuote.fare,
        eta: sessionState.pendingQuote.eta,
      }
    });
  };
  
  const handleEndCall = (): string => {
    console.log(`[${sessionState.callId}] 📞 end_call triggered - closing connections`);
    sessionState.callEnded = true;
    
    // Close OpenAI connection after short delay
    setTimeout(() => {
      openaiWs?.close();
    }, 2000);
    
    // Send hangup to bridge
    try {
      socket.send(JSON.stringify({ type: "hangup", reason: "end_call_tool" }));
    } catch (e) {
      console.error(`[${sessionState.callId}] Error sending hangup:`, e);
    }
    
    return JSON.stringify({ success: true, message: "Call ending" });
  };

  // ═══════════════════════════════════════════════════════════════════════════════
  // OPENAI MESSAGE HANDLER
  // ═══════════════════════════════════════════════════════════════════════════════
  
  const handleOpenAIMessage = (message: any) => {
    // Log non-audio events
    if (!["response.audio.delta", "response.audio_transcript.delta"].includes(message.type)) {
      console.log(`[${sessionState.callId}] 📨 OpenAI: ${message.type}`);
    }
    
    switch (message.type) {
      case "session.created":
        console.log(`[${sessionState.callId}] 🎉 Session created - sending config`);
        sendSessionUpdate();
        break;
        
      case "response.created":
        sessionState.openAiResponseActive = true;
        break;
        
      case "response.done":
        sessionState.openAiResponseActive = false;
        
        // Log completed assistant response
        if (sessionState.assistantTranscriptBuffer) {
          console.log(`[${sessionState.callId}] 🤖 ADA: "${sessionState.assistantTranscriptBuffer}"`);
          sessionState.transcripts.push({
            role: "assistant",
            text: sessionState.assistantTranscriptBuffer,
            timestamp: new Date().toISOString(),
          });
          sessionState.assistantTranscriptBuffer = "";
        }
        break;
        
      case "response.audio.delta":
        // Forward audio to bridge
        if (message.delta && !sessionState.callEnded) {
          socket.send(JSON.stringify({
            type: "audio",
            audio: message.delta,
          }));
        }
        break;
        
      case "response.audio_transcript.delta":
        // Accumulate assistant transcript
        if (message.delta) {
          sessionState.assistantTranscriptBuffer += message.delta;
        }
        break;
        
      case "conversation.item.input_audio_transcription.completed":
        // User speech transcript
        const userText = String(message.transcript || "").trim();
        if (userText) {
          console.log(`[${sessionState.callId}] 👤 User: "${userText}"`);
          sessionState.transcripts.push({
            role: "user",
            text: userText,
            timestamp: new Date().toISOString(),
          });
          
          // Send to bridge for logging
          socket.send(JSON.stringify({
            type: "transcript",
            text: userText,
            role: "user",
          }));
        }
        break;
        
      case "response.function_call_arguments.done":
        handleToolCall(message);
        break;
        
      case "error":
        console.error(`[${sessionState.callId}] ❌ OpenAI error:`, message.error);
        break;
    }
  };
  
  // ═══════════════════════════════════════════════════════════════════════════════
  // TOOL CALL ROUTER
  // ═══════════════════════════════════════════════════════════════════════════════
  
  const handleToolCall = (message: any) => {
    const toolName = message.name;
    const callId = message.call_id;
    let args: any = {};
    
    try {
      args = JSON.parse(message.arguments || "{}");
    } catch (e) {
      console.error(`[${sessionState.callId}] Failed to parse tool args:`, e);
    }
    
    console.log(`[${sessionState.callId}] 🔧 Tool call: ${toolName}`, args);
    
    let result: string;
    
    switch (toolName) {
      case "sync_booking_data":
        result = handleSyncBookingData(args);
        break;
      case "get_dispatch_quote":
        result = handleGetDispatchQuote();
        break;
      case "confirm_and_close":
        result = handleConfirmAndClose();
        break;
      case "end_call":
        result = handleEndCall();
        break;
      default:
        result = JSON.stringify({ error: `Unknown tool: ${toolName}` });
    }
    
    // Send tool result back to OpenAI
    if (openaiWs && openaiConnected) {
      openaiWs.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "function_call_output",
          call_id: callId,
          output: result,
        }
      }));
      
      // Trigger response generation after tool result
      openaiWs.send(JSON.stringify({ type: "response.create" }));
    }
  };

  // ═══════════════════════════════════════════════════════════════════════════════
  // SESSION UPDATE - Configure OpenAI with our state-managed prompt
  // ═══════════════════════════════════════════════════════════════════════════════
  
  const sendSessionUpdate = () => {
    const prompt = SYSTEM_PROMPT
      .replace(/{{agent_name}}/g, sessionState.agentName)
      .replace(/{{company_name}}/g, sessionState.companyName);
    
    const sessionUpdate = {
      type: "session.update",
      session: {
        modalities: ["text", "audio"],
        instructions: prompt,
        voice: sessionState.voice,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        input_audio_transcription: {
          model: "whisper-1",
          prompt: "Taxi booking. Street numbers, addresses, passenger count, pickup location, destination."
        },
        turn_detection: {
          type: "server_vad",
          threshold: 0.5,
          prefix_padding_ms: 300,
          silence_duration_ms: 1200, // Increased to prevent premature turn-taking
        },
        temperature: 0.6,
        tools: TOOLS,
        tool_choice: "auto"
      }
    };
    
    openaiWs?.send(JSON.stringify(sessionUpdate));
    console.log(`[${sessionState.callId}] 📝 Session config sent with state-managed tools`);
    
    // Trigger the welcome greeting
    const greetingText = "Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. You can switch languages at any time, just say the language you prefer, and we'll remember it for your next booking. So, let's get started. Where would you like to be picked up?";
    
    openaiWs?.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: "[SYSTEM: Call connected. Say your greeting now.]" }]
      }
    }));
    
    openaiWs?.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["text", "audio"],
        instructions: `Say this EXACTLY (do not change or shorten it): "${greetingText}"`
      }
    }));
    
    console.log(`[${sessionState.callId}] 🎤 Greeting triggered`);
  };

  // ═══════════════════════════════════════════════════════════════════════════════
  // CONNECT TO OPENAI
  // ═══════════════════════════════════════════════════════════════════════════════
  
  const connectToOpenAI = () => {
    console.log(`[${sessionState.callId}] 🔌 Connecting to OpenAI Realtime API...`);
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-10-01",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );
    
    openaiWs.onopen = () => {
      console.log(`[${sessionState.callId}] ✅ OpenAI WebSocket connected`);
      openaiConnected = true;
    };
    
    openaiWs.onmessage = (event) => {
      try {
        const message = JSON.parse(event.data);
        handleOpenAIMessage(message);
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

  // ═══════════════════════════════════════════════════════════════════════════════
  // BRIDGE SOCKET HANDLERS
  // ═══════════════════════════════════════════════════════════════════════════════
  
  // Keep-alive helper: sends ping to bridge every 15 seconds
  const startKeepAlive = () => {
    if (sessionState.keepAliveTimer) return;
    
    sessionState.keepAliveTimer = setInterval(() => {
      if (sessionState.callEnded) {
        clearInterval(sessionState.keepAliveTimer!);
        sessionState.keepAliveTimer = null;
        return;
      }
      
      try {
        socket.send(JSON.stringify({ type: "keepalive", timestamp: Date.now() }));
        console.log(`[${sessionState.callId}] 💓 Keep-alive ping sent`);
      } catch (e) {
        console.error(`[${sessionState.callId}] Keep-alive error:`, e);
      }
    }, KEEPALIVE_INTERVAL_MS) as unknown as number;
  };
  
  // Session timeout: gracefully end call after 10 minutes max
  const startSessionTimeout = () => {
    sessionState.sessionTimeoutTimer = setTimeout(() => {
      if (sessionState.callEnded) return;
      
      console.log(`[${sessionState.callId}] ⏰ Session timeout (${SESSION_TIMEOUT_MS / 60000} minutes) - ending call gracefully`);
      
      // Inject goodbye message before closing
      if (openaiWs && openaiConnected) {
        openaiWs.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "message",
            role: "user",
            content: [{ 
              type: "input_text", 
              text: "[SYSTEM: Session timeout reached. Say 'I apologize, but we need to end this call now due to time limits. Please call back to complete your booking. Goodbye!' Then end the call.]" 
            }]
          }
        }));
        openaiWs.send(JSON.stringify({ type: "response.create" }));
      }
      
      // Close after delay to let goodbye play
      setTimeout(() => {
        sessionState.callEnded = true;
        openaiWs?.close();
        socket.close();
      }, 5000);
    }, SESSION_TIMEOUT_MS) as unknown as number;
  };
  
  // Activity tracker: reset timeout on user activity
  const updateActivity = () => {
    sessionState.lastActivityAt = Date.now();
  };
  
  // Cleanup function
  const cleanup = () => {
    if (sessionState.keepAliveTimer) {
      clearInterval(sessionState.keepAliveTimer);
      sessionState.keepAliveTimer = null;
    }
    if (sessionState.sessionTimeoutTimer) {
      clearTimeout(sessionState.sessionTimeoutTimer);
      sessionState.sessionTimeoutTimer = null;
    }
  };
  
  socket.onopen = () => {
    console.log(`[${sessionState.callId}] 📞 Bridge WebSocket connected`);
    startKeepAlive();
    startSessionTimeout();
  };
  
  socket.onmessage = (event) => {
    try {
      const data = JSON.parse(event.data);
      
      // Update activity on any message
      updateActivity();
      
      switch (data.type) {
        case "init":
          // Call initialization from bridge
          sessionState.callId = data.call_id || sessionState.callId;
          sessionState.phone = data.caller_phone || "";
          console.log(`[${sessionState.callId}] 📞 Call init: phone=${sessionState.phone}`);
          
          // Connect to OpenAI
          connectToOpenAI();
          
          // Create live_calls record
          supabase.from("live_calls").insert({
            call_id: sessionState.callId,
            caller_phone: sessionState.phone,
            status: "active",
            source: "stateful",
            transcripts: [],
          }).then(({ error }) => {
            if (error) console.error(`[${sessionState.callId}] DB insert error:`, error);
          });
          break;
          
        case "audio":
          // Forward audio to OpenAI
          if (data.audio && openaiWs && openaiConnected && !sessionState.callEnded) {
            openaiWs.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: data.audio,
            }));
          }
          break;
          
        case "keepalive_ack":
          // Bridge responded to our keep-alive
          console.log(`[${sessionState.callId}] 💓 Keep-alive ack received`);
          break;
          
        case "hangup":
          console.log(`[${sessionState.callId}] 📞 Call ended by bridge`);
          sessionState.callEnded = true;
          cleanup();
          openaiWs?.close();
          break;
      }
    } catch (e) {
      console.error(`[${sessionState.callId}] Failed to parse bridge message:`, e);
    }
  };
  
  socket.onclose = () => {
    console.log(`[${sessionState.callId}] 🔌 Bridge WebSocket closed`);
    sessionState.callEnded = true;
    cleanup();
    openaiWs?.close();
    
    // Update live_calls with final state
    supabase.from("live_calls").update({
      status: "ended",
      ended_at: new Date().toISOString(),
      transcripts: sessionState.transcripts,
      pickup: sessionState.bookingStruct.pickup,
      destination: sessionState.bookingStruct.destination,
      passengers: sessionState.bookingStruct.passengers ? parseInt(sessionState.bookingStruct.passengers, 10) : null,
      booking_confirmed: sessionState.bookingConfirmed,
    }).eq("call_id", sessionState.callId).then(({ error }) => {
      if (error) console.error(`[${sessionState.callId}] DB update error:`, error);
    });
  };
  
  socket.onerror = (err) => {
    console.error(`[${sessionState.callId}] ❌ Bridge WebSocket error:`, err);
  };

  return response;
});
