/**
 * taxi-realtime-paired: Stateful Context-Pairing Architecture
 * 
 * This function implements the "Context Pairing" pattern where:
 * 1. Every user response is paired with the last assistant question
 * 2. OpenAI sees the full conversation context to correctly map answers
 * 3. Booking state is tracked in the database for consistency
 */

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Environment
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY") || "";
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";

const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

// OpenAI Realtime API config
const OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17";
const VOICE = "shimmer";

// System prompt - focused on context-aware extraction
const SYSTEM_PROMPT = `You are Ada, a friendly and efficient taxi booking assistant for Imtech Taxi.

## CRITICAL: Context-Aware Response Mapping

When the user responds, ALWAYS check what question you just asked them:
- If you asked for PICKUP and they respond → map to pickup field
- If you asked for DESTINATION and they respond → map to destination field  
- If you asked for PASSENGERS and they respond → map to passengers field
- If you asked for TIME and they respond → map to pickup_time field

## Booking Flow (Sequential - ONE field at a time)

1. PICKUP: "Where would you like to be picked up from?"
2. DESTINATION: "And where would you like to go?"
3. PASSENGERS: "How many passengers?"
4. TIME: "When do you need the taxi - is it for now or later?"

## Rules

- Ask ONE question at a time
- Wait for answer before moving to next field
- Use the sync_booking_data tool after EACH user response
- Include "last_question_asked" to track context
- Keep responses SHORT (phone call style)

## Response Style

- Warm but efficient
- Acknowledge what they said before asking next question
- Example: "Great, Sweet Spot. And where would you like to go?"
`;

// Tools for context-aware booking
const TOOLS = [
  {
    type: "function",
    name: "sync_booking_data",
    description: "Sync the current booking state after each user response. ALWAYS include last_question_asked for context pairing.",
    parameters: {
      type: "object",
      properties: {
        pickup: {
          type: "string",
          description: "Pickup location - only set if user answered a pickup question"
        },
        destination: {
          type: "string", 
          description: "Destination - only set if user answered a destination question"
        },
        passengers: {
          type: "number",
          description: "Number of passengers - only set if user answered a passengers question"
        },
        pickup_time: {
          type: "string",
          description: "When they need the taxi (e.g., 'now', '4:30pm', 'in 20 minutes')"
        },
        last_question_asked: {
          type: "string",
          enum: ["pickup", "destination", "passengers", "time", "confirmation", "none"],
          description: "What question was JUST asked to the user - critical for context pairing"
        },
        is_complete: {
          type: "boolean",
          description: "True only when ALL fields are filled"
        }
      },
      required: ["last_question_asked"]
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Submit the final booking when all details are confirmed",
    parameters: {
      type: "object",
      properties: {
        action: {
          type: "string",
          enum: ["request_quote", "confirmed"],
          description: "request_quote to get fare/ETA, confirmed to book"
        },
        pickup: { type: "string" },
        destination: { type: "string" },
        passengers: { type: "number" },
        pickup_time: { type: "string" }
      },
      required: ["action", "pickup", "destination"]
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "End the call gracefully",
    parameters: {
      type: "object",
      properties: {
        reason: {
          type: "string",
          enum: ["booking_complete", "customer_cancelled", "customer_goodbye"]
        }
      },
      required: ["reason"]
    }
  }
];

// Session state interface
interface SessionState {
  callId: string;
  callerPhone: string;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    pickupTime: string | null;
  };
  lastQuestionAsked: "pickup" | "destination" | "passengers" | "time" | "confirmation" | "none";
  conversationHistory: Array<{ role: string; content: string; timestamp: number }>;
  bookingConfirmed: boolean;
  openAiResponseActive: boolean;
}

// Create initial session state
function createSessionState(callId: string, callerPhone: string): SessionState {
  return {
    callId,
    callerPhone,
    booking: {
      pickup: null,
      destination: null,
      passengers: null,
      pickupTime: null
    },
    lastQuestionAsked: "none",
    conversationHistory: [],
    bookingConfirmed: false,
    openAiResponseActive: false
  };
}

// Build context-paired messages for OpenAI
function buildContextPairedMessages(sessionState: SessionState): Array<{ role: string; content: string }> {
  const messages: Array<{ role: string; content: string }> = [];
  
  // Add system context about current state
  const stateContext = `
[CURRENT BOOKING STATE]
- Pickup: ${sessionState.booking.pickup || "NOT SET"}
- Destination: ${sessionState.booking.destination || "NOT SET"}
- Passengers: ${sessionState.booking.passengers ?? "NOT SET"}
- Time: ${sessionState.booking.pickupTime || "NOT SET"}
- Last Question Asked: ${sessionState.lastQuestionAsked}

[CONTEXT PAIRING INSTRUCTION]
The last question asked was about "${sessionState.lastQuestionAsked}".
If the user's next response contains location/address/place info and last_question was "pickup" → it's the pickup.
If the user's next response contains location/address/place info and last_question was "destination" → it's the destination.
NEVER swap fields. Trust the question context.
`;

  messages.push({ role: "system", content: SYSTEM_PROMPT + stateContext });
  
  // Add last 6 conversation turns for context (3 exchanges)
  const recentHistory = sessionState.conversationHistory.slice(-6);
  for (const msg of recentHistory) {
    messages.push({ role: msg.role, content: msg.content });
  }
  
  return messages;
}

// Update live_calls table with current state
async function updateLiveCall(sessionState: SessionState) {
  try {
    const { error } = await supabase
      .from("live_calls")
      .upsert({
        call_id: sessionState.callId,
        caller_phone: sessionState.callerPhone,
        pickup: sessionState.booking.pickup,
        destination: sessionState.booking.destination,
        passengers: sessionState.booking.passengers,
        status: sessionState.bookingConfirmed ? "confirmed" : "active",
        booking_confirmed: sessionState.bookingConfirmed,
        transcripts: sessionState.conversationHistory,
        source: "paired",
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
    
    if (error) {
      console.error(`[${sessionState.callId}] Failed to update live_calls:`, error);
    }
  } catch (e) {
    console.error(`[${sessionState.callId}] Error updating live_calls:`, e);
  }
}

// Send dispatch webhook
async function sendDispatchWebhook(
  sessionState: SessionState,
  action: string,
  bookingData: Record<string, unknown>
): Promise<{ success: boolean; fare?: string; eta?: string; error?: string }> {
  if (!DISPATCH_WEBHOOK_URL) {
    console.log(`[${sessionState.callId}] No dispatch webhook configured, simulating response`);
    return {
      success: true,
      fare: "£8.50",
      eta: "5 minutes"
    };
  }

  try {
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        action,
        call_id: sessionState.callId,
        caller_phone: sessionState.callerPhone,
        ...bookingData
      })
    });

    if (!response.ok) {
      return { success: false, error: `Webhook returned ${response.status}` };
    }

    const data = await response.json();
    return {
      success: true,
      fare: data.fare || data.estimated_fare,
      eta: data.eta || data.estimated_eta
    };
  } catch (e) {
    console.error(`[${sessionState.callId}] Dispatch webhook error:`, e);
    return { success: false, error: String(e) };
  }
}

// Handle WebSocket connection
async function handleConnection(socket: WebSocket, callId: string, callerPhone: string) {
  console.log(`[${callId}] 🎯 PAIRED MODE: New connection from ${callerPhone}`);
  
  const sessionState = createSessionState(callId, callerPhone);
  let openaiWs: WebSocket | null = null;
  let cleanedUp = false;

  // Cleanup function
  const cleanup = async () => {
    if (cleanedUp) return;
    cleanedUp = true;
    
    console.log(`[${callId}] 🧹 Cleaning up connection`);
    
    // Update final call state
    try {
      await supabase
        .from("live_calls")
        .update({
          status: sessionState.bookingConfirmed ? "completed" : "ended",
          ended_at: new Date().toISOString()
        })
        .eq("call_id", callId);
    } catch (e) {
      console.error(`[${callId}] Error updating final state:`, e);
    }
    
    if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
      openaiWs.close();
    }
  };

  // Connect to OpenAI Realtime
  try {
    openaiWs = new WebSocket(OPENAI_REALTIME_URL, {
      headers: {
        "Authorization": `Bearer ${OPENAI_API_KEY}`,
        "OpenAI-Beta": "realtime=v1"
      }
    });
  } catch (e) {
    console.error(`[${callId}] Failed to connect to OpenAI:`, e);
    socket.close();
    return;
  }

  // OpenAI WebSocket handlers
  openaiWs.onopen = () => {
    console.log(`[${callId}] ✅ Connected to OpenAI Realtime`);
    
    // Configure session with context-pairing system
    const sessionConfig = {
      type: "session.update",
      session: {
        modalities: ["text", "audio"],
        voice: VOICE,
        instructions: SYSTEM_PROMPT + `\n\n[CALL CONTEXT]\nCall ID: ${callId}\nCaller: ${callerPhone}`,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        input_audio_transcription: { model: "whisper-1" },
        turn_detection: {
          type: "server_vad",
          threshold: 0.5,
          prefix_padding_ms: 500,
          silence_duration_ms: 800
        },
        tools: TOOLS,
        tool_choice: "auto",
        temperature: 0.7
      }
    };
    
    openaiWs!.send(JSON.stringify(sessionConfig));
    
    // Initial greeting with context setup
    setTimeout(() => {
      const greeting = {
        type: "conversation.item.create",
        item: {
          type: "message",
          role: "user",
          content: [{
            type: "input_text",
            text: "[SYSTEM: New call started. Greet the caller warmly and ask for their pickup location. Remember to track last_question_asked as 'pickup'.]"
          }]
        }
      };
      openaiWs!.send(JSON.stringify(greeting));
      openaiWs!.send(JSON.stringify({ type: "response.create" }));
      
      sessionState.lastQuestionAsked = "pickup";
    }, 500);
  };

  openaiWs.onmessage = async (event) => {
    try {
      const data = JSON.parse(event.data);
      
      switch (data.type) {
        case "response.audio.delta":
          // Forward audio to client
          if (data.delta && socket.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({
              type: "audio",
              audio: data.delta
            }));
          }
          break;

        case "response.audio_transcript.delta":
          // Track what Ada is saying
          if (data.delta) {
            sessionState.openAiResponseActive = true;
          }
          break;

        case "response.audio_transcript.done":
          // Ada finished speaking - record in history
          if (data.transcript) {
            sessionState.conversationHistory.push({
              role: "assistant",
              content: data.transcript,
              timestamp: Date.now()
            });
            
            console.log(`[${callId}] 🤖 Ada: "${data.transcript.substring(0, 80)}..."`);
            
            // Detect what question was asked based on content
            const lower = data.transcript.toLowerCase();
            if (lower.includes("where would you like to be picked up") || lower.includes("pickup")) {
              sessionState.lastQuestionAsked = "pickup";
            } else if (lower.includes("where would you like to go") || lower.includes("destination") || lower.includes("where are you going")) {
              sessionState.lastQuestionAsked = "destination";
            } else if (lower.includes("how many") || lower.includes("passengers") || lower.includes("people")) {
              sessionState.lastQuestionAsked = "passengers";
            } else if (lower.includes("when") || lower.includes("what time") || lower.includes("now or later")) {
              sessionState.lastQuestionAsked = "time";
            } else if (lower.includes("confirm") || lower.includes("book that")) {
              sessionState.lastQuestionAsked = "confirmation";
            }
            
            console.log(`[${callId}] 📝 Context: lastQuestionAsked = ${sessionState.lastQuestionAsked}`);
            await updateLiveCall(sessionState);
          }
          sessionState.openAiResponseActive = false;
          break;

        case "input_audio_buffer.speech_started":
          console.log(`[${callId}] 🎤 User started speaking`);
          break;

        case "conversation.item.input_audio_transcription.completed":
          // User finished speaking - this is the KEY context pairing moment
          if (data.transcript) {
            const userText = data.transcript.trim();
            console.log(`[${callId}] 👤 User (after "${sessionState.lastQuestionAsked}" question): "${userText}"`);
            
            // Add to history with context annotation
            sessionState.conversationHistory.push({
              role: "user",
              content: `[CONTEXT: Ada asked about ${sessionState.lastQuestionAsked}] ${userText}`,
              timestamp: Date.now()
            });
            
            // Send context-aware prompt to OpenAI
            const contextPrompt = {
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: `[CONTEXT PAIRING] You just asked about "${sessionState.lastQuestionAsked}". The user responded: "${userText}". 
                  
If this is a valid answer to your ${sessionState.lastQuestionAsked} question, use sync_booking_data to save it to the CORRECT field.
Current state: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=${sessionState.booking.passengers ?? "empty"}, time=${sessionState.booking.pickupTime || "empty"}`
                }]
              }
            };
            openaiWs!.send(JSON.stringify(contextPrompt));
            
            await updateLiveCall(sessionState);
          }
          break;

        case "response.function_call_arguments.done":
          // Handle tool calls
          const toolName = data.name;
          let toolArgs: Record<string, unknown> = {};
          
          try {
            toolArgs = JSON.parse(data.arguments || "{}");
          } catch {
            console.error(`[${callId}] Failed to parse tool args`);
          }
          
          console.log(`[${callId}] 🔧 Tool: ${toolName}`, toolArgs);
          
          if (toolName === "sync_booking_data") {
            // Update booking state from tool call
            if (toolArgs.pickup) sessionState.booking.pickup = String(toolArgs.pickup);
            if (toolArgs.destination) sessionState.booking.destination = String(toolArgs.destination);
            if (toolArgs.passengers !== undefined) sessionState.booking.passengers = Number(toolArgs.passengers);
            if (toolArgs.pickup_time) sessionState.booking.pickupTime = String(toolArgs.pickup_time);
            if (toolArgs.last_question_asked) {
              sessionState.lastQuestionAsked = toolArgs.last_question_asked as SessionState["lastQuestionAsked"];
            }
            
            console.log(`[${callId}] 📊 Booking state updated:`, sessionState.booking);
            await updateLiveCall(sessionState);
            
            // Send tool result
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ 
                  success: true, 
                  current_state: sessionState.booking,
                  next_question: sessionState.lastQuestionAsked
                })
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
          } else if (toolName === "book_taxi") {
            const action = toolArgs.action as string;
            
            const webhookResult = await sendDispatchWebhook(sessionState, action, {
              pickup: toolArgs.pickup || sessionState.booking.pickup,
              destination: toolArgs.destination || sessionState.booking.destination,
              passengers: toolArgs.passengers || sessionState.booking.passengers,
              pickup_time: toolArgs.pickup_time || sessionState.booking.pickupTime
            });
            
            if (action === "confirmed" && webhookResult.success) {
              sessionState.bookingConfirmed = true;
            }
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify(webhookResult)
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            await updateLiveCall(sessionState);
            
          } else if (toolName === "end_call") {
            console.log(`[${callId}] 📞 Call ending: ${toolArgs.reason}`);
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ success: true })
              }
            }));
            
            // Let Ada say goodbye
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: "[SYSTEM: Say a warm goodbye. Mention they'll receive booking details via WhatsApp. Thank them for using Taxibot demo.]"
                }]
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
            setTimeout(() => {
              try {
                socket.send(JSON.stringify({ type: "hangup", reason: toolArgs.reason }));
              } catch { /* ignore */ }
              cleanup();
            }, 8000);
          }
          break;

        case "error":
          console.error(`[${callId}] ❌ OpenAI error:`, data.error);
          break;
      }
    } catch (e) {
      console.error(`[${callId}] Error processing OpenAI message:`, e);
    }
  };

  openaiWs.onerror = (error) => {
    console.error(`[${callId}] OpenAI WebSocket error:`, error);
  };

  openaiWs.onclose = () => {
    console.log(`[${callId}] OpenAI WebSocket closed`);
    cleanup();
  };

  // Client WebSocket handlers
  socket.onmessage = async (event) => {
    try {
      const data = JSON.parse(event.data);
      
      if (data.type === "audio" && data.audio) {
        // Forward audio to OpenAI
        if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "input_audio_buffer.append",
            audio: data.audio
          }));
        }
      } else if (data.type === "hangup") {
        console.log(`[${callId}] Client requested hangup`);
        cleanup();
      }
    } catch (e) {
      console.error(`[${callId}] Error processing client message:`, e);
    }
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] Client WebSocket error:`, error);
  };

  socket.onclose = () => {
    console.log(`[${callId}] Client WebSocket closed`);
    cleanup();
  };
}

// Main server
Deno.serve(async (req) => {
  const url = new URL(req.url);
  
  // CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  // Health check
  if (url.pathname === "/health" || url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ 
      status: "healthy",
      mode: "paired-context",
      timestamp: new Date().toISOString()
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" }
    });
  }
  
  // WebSocket upgrade
  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() === "websocket") {
    const callId = url.searchParams.get("call_id") || `paired-${Date.now()}`;
    const callerPhone = url.searchParams.get("caller_phone") || "unknown";
    
    const { socket, response } = Deno.upgradeWebSocket(req);
    
    handleConnection(socket, callId, callerPhone);
    
    return response;
  }
  
  return new Response(JSON.stringify({ 
    error: "WebSocket upgrade required",
    mode: "taxi-realtime-paired"
  }), {
    status: 400,
    headers: { ...corsHeaders, "Content-Type": "application/json" }
  });
});
