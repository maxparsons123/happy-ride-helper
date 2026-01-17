import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Configuration
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY")!;
const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const COMPANY_NAME = Deno.env.get("COMPANY_NAME") || "ABC Taxis";
const AGENT_NAME = Deno.env.get("AGENT_NAME") || "Ada";
const VOICE = Deno.env.get("VOICE") || "shimmer";

const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

// System prompt for Ada
const SYSTEM_PROMPT = `
You are ${AGENT_NAME}, a friendly taxi booking assistant for ${COMPANY_NAME}.

## GREETING (FIRST MESSAGE)
"Hello, and welcome to ${COMPANY_NAME}! I'm ${AGENT_NAME}, your taxi booking assistant. Let's get you booked. Where would you like to be picked up?"

## INFORMATION GATHERING - ONE QUESTION AT A TIME
After each answer, ask the NEXT question. WAIT for each answer before proceeding.

1. "Where would you like to be picked up?" → WAIT
2. "What is your destination?" → WAIT  
3. "How many people will be travelling?" → WAIT
4. "When do you need the taxi?" → WAIT

## BOOKING SUMMARY
Once you have ALL details, summarize:
"Let me confirm: pickup from [pickup], going to [destination], [passengers] passengers, [time]. Is that correct?"

WAIT for confirmation.

## AFTER CONFIRMATION
If user says yes/correct:
"Great, one moment while I check the price."
→ CALL book_taxi with confirmation_state="request_quote"

## AFTER RECEIVING QUOTE
The system will provide fare and ETA. Then ask:
"The fare is [fare] and the taxi will arrive in [ETA]. Shall I confirm this booking?"

## FINAL BOOKING
If user confirms:
→ CALL book_taxi with confirmation_state="confirmed"

After booking is confirmed, say: "That's booked for you. Is there anything else I can help you with?"
Then STOP and WAIT for their response.

## AFTER "ANYTHING ELSE?" - CRITICAL
When user responds to "Is there anything else I can help you with?":

If they say NO (e.g., "no", "no thanks", "nothing else", "that's all", "I'm good"):
→ Say: "You're welcome! Have a safe journey. Goodbye!"
→ CALL end_call immediately

If they say YES or have another request:
→ Help them with their new request

## HANDLING CORRECTIONS
If user corrects ANY detail:
- Update ONLY the field they mention
- Re-summarize ALL booking details
- Ask for confirmation again

## CRITICAL RULES
- Ask ONE question at a time, then STOP and WAIT
- NEVER invent fares or ETAs - use tool calls
- If user changes details after summary, re-summarize everything
- Keep responses SHORT (phone call pacing)
- After asking "Is there anything else?", you MUST wait for user response before saying goodbye
- NEVER say "Safe travels" or goodbye until AFTER user responds to "anything else?"
`;

// Tool definitions
const TOOLS = [
  {
    type: "function",
    name: "book_taxi",
    description: "Handle taxi booking. Use request_quote to get fare/ETA, use confirmed to finalize booking.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
        passengers: { type: "integer", description: "Number of passengers", default: 1 },
        pickup_time: { type: "string", description: "When taxi is needed (ASAP or datetime)" },
        confirmation_state: {
          type: "string",
          enum: ["request_quote", "confirmed"],
          description: "request_quote to get price, confirmed to book"
        }
      },
      required: ["pickup", "destination", "confirmation_state"]
    }
  },
  {
    type: "function", 
    name: "save_customer_name",
    description: "Save customer's name when they provide it",
    parameters: {
      type: "object",
      properties: {
        name: { type: "string", description: "Customer's name" }
      },
      required: ["name"]
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "End the call gracefully after user confirms they don't need anything else. ONLY call this AFTER user has responded to 'Is there anything else I can help you with?' with 'no', 'no thanks', 'nothing else', etc.",
    parameters: {
      type: "object",
      properties: {
        reason: { type: "string", description: "Reason for ending call (e.g., 'user_finished', 'booking_complete')" }
      },
      required: ["reason"]
    }
  }
];

// Session state interface
interface SessionState {
  callId: string;
  phone: string;
  callerName: string | null;
  transcripts: Array<{ role: "user" | "assistant"; text: string }>;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number;
    pickup_time: string;
  };
  pendingQuote: {
    fare: string;
    eta: string;
  } | null;
  sessionCreated: boolean;
  cleanedUp: boolean; // Prevent double cleanup
  timeoutId: number | null; // Session timeout
  // Post-booking state
  bookingConfirmed: boolean;
  askedAnythingElse: boolean;
  askedAnythingElseAt: number | null;
  lastAssistantText: string; // Track Ada's last response for Q&A context
}

// Active sessions - with periodic cleanup
const sessions = new Map<string, SessionState>();
const MAX_SESSION_DURATION_MS = 10 * 60 * 1000; // 10 minutes max per call
const STALE_SESSION_CHECK_MS = 60 * 1000; // Check every minute

// Periodic stale session cleanup
setInterval(() => {
  const now = Date.now();
  for (const [callId, state] of sessions.entries()) {
    if (state.cleanedUp) {
      sessions.delete(callId);
      console.log(`🧹 [${callId}] Removed stale session`);
    }
  }
  console.log(`📊 Active sessions: ${sessions.size}`);
}, STALE_SESSION_CHECK_MS);

// Build conversation with Q&A context pairing for extraction
// This adds [CONTEXT: Ada asked: "..."] prefixes so extraction knows what field each answer belongs to
function buildContextPairedConversation(
  transcripts: Array<{ role: "user" | "assistant"; text: string }>
): Array<{ role: "user" | "assistant"; text: string }> {
  const result: Array<{ role: "user" | "assistant"; text: string }> = [];
  let lastAdaQuestion: string | null = null;
  
  for (const turn of transcripts) {
    if (turn.role === "assistant") {
      // Track Ada's question for context pairing
      const text = turn.text.toLowerCase();
      if (text.includes("where would you like to be picked up") || 
          text.includes("what's the pickup") ||
          text.includes("pickup address")) {
        lastAdaQuestion = "Where would you like to be picked up?";
      } else if (text.includes("destination") || 
                 text.includes("where are you going") ||
                 text.includes("going to") ||
                 text.includes("drop") ||
                 text.includes("where to")) {
        lastAdaQuestion = "What is your destination?";
      } else if (text.includes("how many") && (text.includes("passenger") || text.includes("people") || text.includes("travelling"))) {
        lastAdaQuestion = "How many passengers?";
      } else if (text.includes("when") && (text.includes("taxi") || text.includes("need") || text.includes("pickup"))) {
        lastAdaQuestion = "When do you need the taxi?";
      } else if (text.includes("is that correct") || text.includes("is that right")) {
        lastAdaQuestion = "Is that correct?";
      }
      result.push(turn);
    } else if (turn.role === "user") {
      // Add context prefix if we know what Ada just asked
      if (lastAdaQuestion) {
        result.push({
          role: "user",
          text: `[CONTEXT: Ada asked: "${lastAdaQuestion}"] ${turn.text}`
        });
      } else {
        result.push(turn);
      }
      lastAdaQuestion = null; // Reset after user responds
    }
  }
  
  return result;
}

// Call taxi-extract-unified to extract booking details from conversation
async function extractBookingDetails(
  conversation: Array<{ role: "user" | "assistant"; text: string }>,
  callerPhone: string,
  currentBooking: SessionState["booking"]
): Promise<{
  pickup: string | null;
  destination: string | null;
  passengers: number;
  pickup_time: string;
  fields_changed: string[];
}> {
  try {
    // Build context-paired conversation for better extraction accuracy
    const contextPairedConversation = buildContextPairedConversation(conversation);
    console.log(`[extract] Context-paired conversation:`, JSON.stringify(contextPairedConversation.slice(-4)));
    
    const response = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract-unified`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
      },
      body: JSON.stringify({
        conversation: contextPairedConversation,
        caller_phone: callerPhone,
        current_booking: {
          pickup: currentBooking.pickup,
          destination: currentBooking.destination,
          passengers: currentBooking.passengers,
          pickup_time: currentBooking.pickup_time,
        },
        is_modification: currentBooking.pickup !== null || currentBooking.destination !== null,
      }),
    });

    if (!response.ok) {
      console.error(`[extract] Error: ${response.status}`);
      return { ...currentBooking, fields_changed: [] };
    }

    const result = await response.json();
    console.log(`[extract] Result:`, JSON.stringify(result));

    return {
      pickup: result.pickup || currentBooking.pickup,
      destination: result.destination || currentBooking.destination,
      passengers: result.passengers || currentBooking.passengers,
      pickup_time: result.pickup_time || currentBooking.pickup_time,
      fields_changed: result.fields_changed || [],
    };
  } catch (error) {
    console.error(`[extract] Failed:`, error);
    return { ...currentBooking, fields_changed: [] };
  }
}

// Send webhook to dispatch system
async function sendDispatchWebhook(
  action: string, 
  data: Record<string, unknown>
): Promise<{ fare?: string; eta?: string; success?: boolean } | null> {
  if (!DISPATCH_WEBHOOK_URL) {
    console.log(`[webhook] No dispatch URL configured`);
    return null;
  }

  try {
    console.log(`[webhook] ${action}:`, JSON.stringify(data));
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ action, ...data }),
    });
    const result = await response.json();
    console.log(`[webhook] Response:`, JSON.stringify(result));
    return result;
  } catch (error) {
    console.error(`[webhook] Error:`, error);
    return null;
  }
}

// Update live_calls table
async function updateLiveCall(callId: string, state: SessionState) {
  try {
    await supabase.from("live_calls").upsert({
      call_id: callId,
      caller_phone: state.phone,
      caller_name: state.callerName,
      pickup: state.booking.pickup,
      destination: state.booking.destination,
      passengers: state.booking.passengers,
      transcripts: state.transcripts,
      status: "active",
      updated_at: new Date().toISOString(),
    }, { onConflict: "call_id" });
  } catch (error) {
    console.error(`[db] Error updating live_calls:`, error);
  }
}

// Base64 encoding for audio
function encodeBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (let i = 0; i < bytes.byteLength; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary);
}

// Base64 decoding for audio
function decodeBase64(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

// Handle WebSocket connection
async function handleConnection(clientWs: WebSocket, callId: string, callerPhone: string) {
  console.log(`📞 [${callId}] New call from ${callerPhone}`);

  // Initialize session state
  const state: SessionState = {
    callId,
    phone: callerPhone,
    callerName: null,
    transcripts: [],
    booking: {
      pickup: null,
      destination: null,
      passengers: 1,
      pickup_time: "ASAP",
    },
    pendingQuote: null,
    sessionCreated: false,
    cleanedUp: false,
    timeoutId: null,
    // Post-booking state
    bookingConfirmed: false,
    askedAnythingElse: false,
    askedAnythingElseAt: null,
    lastAssistantText: "",
  };
  sessions.set(callId, state);

  // Set max session timeout to prevent hanging connections
  state.timeoutId = setTimeout(() => {
    console.log(`⏰ [${callId}] Session timeout - forcing cleanup`);
    cleanup();
  }, MAX_SESSION_DURATION_MS) as unknown as number;

  // Create live call record
  await supabase.from("live_calls").insert({
    call_id: callId,
    caller_phone: callerPhone,
    status: "connecting",
    source: "openai-realtime",
  });

  // Connect to OpenAI Realtime API
  const openaiWs = new WebSocket(
    "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17",
    ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
  );

  // Handle OpenAI connection open
  openaiWs.onopen = () => {
    console.log(`🔗 [${callId}] Connected to OpenAI`);
  };

  // Handle OpenAI messages
  openaiWs.onmessage = async (event) => {
    try {
      const message = JSON.parse(event.data.toString());
      
      switch (message.type) {
        // Session created - NOW send session.update
        case "session.created":
          console.log(`✅ [${callId}] Session created, configuring...`);
          state.sessionCreated = true;
          
          openaiWs.send(JSON.stringify({
            type: "session.update",
            session: {
              modalities: ["text", "audio"],
              voice: VOICE,
              instructions: SYSTEM_PROMPT,
              input_audio_format: "pcm16",
              output_audio_format: "pcm16",
              input_audio_transcription: { model: "whisper-1" },
              turn_detection: {
                type: "server_vad",
                threshold: 0.5,
                prefix_padding_ms: 300,
                silence_duration_ms: 800,
              },
              tools: TOOLS,
              tool_choice: "auto",
              temperature: 0.7,
            },
          }));
          break;

        // Session configured - trigger greeting
        case "session.updated":
          console.log(`⚙️ [${callId}] Session configured, sending greeting`);
          
          // Trigger initial greeting
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: {
              modalities: ["text", "audio"],
            },
          }));
          break;

        // Response started - guard against VAD-triggered hallucinations
        case "response.created":
          // ✅ "ANYTHING ELSE" GUARD: After Ada asks "Is there anything else I can help with?",
          // block VAD-triggered responses during the grace period to let the user respond.
          // This prevents Ada from hallucinating cancellations or other statements.
          if (state.askedAnythingElse && state.askedAnythingElseAt) {
            const msSinceAsked = Date.now() - state.askedAnythingElseAt;
            const gracePeriodMs = 5000; // 5 seconds for user to respond
            
            if (msSinceAsked < gracePeriodMs) {
              console.log(`🛑 [${callId}] Cancelling VAD-triggered response - waiting for user response to "anything else?" (${msSinceAsked}ms / ${gracePeriodMs}ms)`);
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              break;
            }
          }
          break;

        // Audio output from OpenAI -> send to client
        case "response.audio.delta":
          if (message.delta) {
            const audioBytes = decodeBase64(message.delta);
            clientWs.send(audioBytes.buffer);
          }
          break;

        // Transcript of what Ada said
        case "response.audio_transcript.done":
          if (message.transcript) {
            console.log(`🤖 [${callId}] ADA: ${message.transcript}`);
            state.transcripts.push({ role: "assistant", text: message.transcript });
            state.lastAssistantText = message.transcript; // Track for Q&A context
            await updateLiveCall(callId, state);
          }
          break;

        // User speech transcription completed
        case "conversation.item.input_audio_transcription.completed":
          if (message.transcript) {
            const userText = message.transcript.trim();
            console.log(`👤 [${callId}] USER: ${userText}`);
            state.transcripts.push({ role: "user", text: userText });
            
            // Extract booking details using AI
            const extracted = await extractBookingDetails(
              state.transcripts,
              state.phone,
              state.booking
            );
            
            // Update booking state with extracted info
            if (extracted.pickup) state.booking.pickup = extracted.pickup;
            if (extracted.destination) state.booking.destination = extracted.destination;
            if (extracted.passengers) state.booking.passengers = extracted.passengers;
            if (extracted.pickup_time) state.booking.pickup_time = extracted.pickup_time;
            
            if (extracted.fields_changed.length > 0) {
              console.log(`📝 [${callId}] Fields updated: ${extracted.fields_changed.join(", ")}`);
            }
            
            await updateLiveCall(callId, state);
          }
          break;

        // Tool call completed - handle it
        case "response.function_call_arguments.done":
          const toolName = message.name;
          const toolArgs = JSON.parse(message.arguments);
          const toolCallId = message.call_id;
          
          console.log(`🔧 [${callId}] Tool: ${toolName}`, JSON.stringify(toolArgs));
          
          let toolResult: Record<string, unknown> = {};
          
          if (toolName === "book_taxi") {
            // Use extracted values, but prefer tool args for explicit mentions
            const pickup = toolArgs.pickup || state.booking.pickup;
            const destination = toolArgs.destination || state.booking.destination;
            const passengers = toolArgs.passengers || state.booking.passengers;
            const pickupTime = toolArgs.pickup_time || state.booking.pickup_time;
            
            if (toolArgs.confirmation_state === "request_quote") {
              // Get quote from dispatch
              const quote = await sendDispatchWebhook("request_quote", {
                call_id: callId,
                pickup,
                destination,
                passengers,
                pickup_time: pickupTime,
                caller_phone: state.phone,
              });
              
              if (quote?.fare && quote?.eta) {
                state.pendingQuote = { fare: quote.fare, eta: quote.eta };
                toolResult = {
                  success: true,
                  fare: quote.fare,
                  eta: quote.eta,
                  message: `The fare will be ${quote.fare} and the taxi will arrive in ${quote.eta}.`,
                };
              } else {
                toolResult = {
                  success: false,
                  message: "Unable to get quote. Please try again.",
                };
              }
            } else if (toolArgs.confirmation_state === "confirmed") {
              // Capture quote values BEFORE clearing
              const confirmedFare = state.pendingQuote?.fare;
              const confirmedEta = state.pendingQuote?.eta;
              
              // Confirm booking
              const result = await sendDispatchWebhook("confirmed", {
                call_id: callId,
                pickup,
                destination,
                passengers,
                pickup_time: pickupTime,
                caller_phone: state.phone,
                fare: confirmedFare,
                eta: confirmedEta,
              });
              
              // Update DB with confirmed booking
              await supabase.from("bookings").insert({
                call_id: callId,
                caller_phone: state.phone,
                caller_name: state.callerName,
                pickup,
                destination,
                passengers,
                fare: confirmedFare,
                eta: confirmedEta,
                status: "confirmed",
              });
              
              // Mark booking as confirmed and clear quote
              state.bookingConfirmed = true;
              state.askedAnythingElse = true;
              state.askedAnythingElseAt = Date.now();
              state.pendingQuote = null;
              
              toolResult = {
                success: true,
                message: "Booking confirmed! Say: 'That's booked for you. Is there anything else I can help you with?' Then WAIT for their response.",
              };
              
              console.log(`✅ [${callId}] Booking confirmed - injecting "anything else" prompt`);
              
              // After sending tool result, inject explicit system message for "anything else"
              setTimeout(() => {
                if (openaiWs.readyState === WebSocket.OPEN) {
                  openaiWs.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{
                        type: "input_text",
                        text: `[SYSTEM: Booking confirmed! Say EXACTLY: "That's booked for you. Is there anything else I can help you with?" Then WAIT for their response. Do NOT say goodbye yet.]`,
                      }],
                    },
                  }));
                  openaiWs.send(JSON.stringify({ type: "response.create" }));
                }
              }, 100);
            }
          } else if (toolName === "save_customer_name") {
            state.callerName = toolArgs.name;
            toolResult = { success: true, message: `Saved name: ${toolArgs.name}` };
            await updateLiveCall(callId, state);
          } else if (toolName === "end_call") {
            console.log(`📴 [${callId}] end_call tool triggered: ${toolArgs.reason}`);
            toolResult = { success: true, message: "Call ended gracefully." };
            
            // Give Ada time to say goodbye, then close connections
            setTimeout(async () => {
              console.log(`📴 [${callId}] Closing call after goodbye`);
              await cleanup();
            }, 3000); // 3 seconds for Ada to say goodbye
          }
          // Send tool result back to OpenAI
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: toolCallId,
              output: JSON.stringify(toolResult),
            },
          }));
          
          // Trigger next response
          openaiWs.send(JSON.stringify({ type: "response.create" }));
          break;

        // Error handling
        case "error":
          console.error(`❌ [${callId}] OpenAI error:`, message.error);
          break;
      }
    } catch (error) {
      console.error(`❌ [${callId}] Message processing error:`, error);
    }
  };

  // Handle client audio -> forward to OpenAI
  clientWs.onmessage = (event) => {
    if (event.data instanceof ArrayBuffer) {
      // PCM audio from SIP bridge -> send to OpenAI as base64
      const base64Audio = encodeBase64(event.data);
      
      if (openaiWs.readyState === WebSocket.OPEN) {
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: base64Audio,
        }));
      }
    } else if (typeof event.data === "string") {
      // JSON message from client
      try {
        const data = JSON.parse(event.data);
        console.log(`📨 [${callId}] Client message:`, data.type);
      } catch {
        // Ignore non-JSON strings
      }
    }
  };

  // Cleanup on close - with double-cleanup protection
  const cleanup = async () => {
    if (state.cleanedUp) {
      console.log(`⚠️ [${callId}] Cleanup already done, skipping`);
      return;
    }
    state.cleanedUp = true;
    
    // Clear session timeout
    if (state.timeoutId) {
      clearTimeout(state.timeoutId);
      state.timeoutId = null;
    }
    
    console.log(`📴 [${callId}] Call ended - cleaning up`);
    
    // Update DB
    try {
      await supabase.from("live_calls").update({
        status: "ended",
        ended_at: new Date().toISOString(),
      }).eq("call_id", callId);
    } catch (e) {
      console.error(`[${callId}] DB cleanup error:`, e);
    }
    
    // Remove from sessions map
    sessions.delete(callId);
    
    // Close WebSockets
    try {
      if (openaiWs.readyState === WebSocket.OPEN || openaiWs.readyState === WebSocket.CONNECTING) {
        openaiWs.close();
      }
    } catch (e) {
      console.error(`[${callId}] OpenAI WS close error:`, e);
    }
    
    try {
      if (clientWs.readyState === WebSocket.OPEN || clientWs.readyState === WebSocket.CONNECTING) {
        clientWs.close();
      }
    } catch (e) {
      console.error(`[${callId}] Client WS close error:`, e);
    }
    
    console.log(`✅ [${callId}] Cleanup complete. Active sessions: ${sessions.size}`);
  };

  clientWs.onclose = cleanup;
  clientWs.onerror = (e) => {
    console.error(`❌ [${callId}] Client WS error:`, e);
    cleanup();
  };
  
  openaiWs.onclose = () => {
    console.log(`🔌 [${callId}] OpenAI disconnected`);
    cleanup();
  };
  
  openaiWs.onerror = (e) => {
    console.error(`❌ [${callId}] OpenAI WS error:`, e);
    cleanup();
  };
}

// Main server
serve(async (req) => {
  const url = new URL(req.url);
  
  // CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  // Health check
  if (url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ status: "ok" }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
  
  // WebSocket upgrade for voice calls
  const upgrade = req.headers.get("upgrade");
  if (upgrade?.toLowerCase() === "websocket") {
    const callId = url.searchParams.get("call_id") || `call-${Date.now()}`;
    const callerPhone = url.searchParams.get("phone") || url.searchParams.get("caller_phone") || "unknown";
    
    const { socket, response } = Deno.upgradeWebSocket(req);
    handleConnection(socket, callId, callerPhone);
    return response;
  }
  
  return new Response(JSON.stringify({ error: "Expected WebSocket connection" }), {
    status: 400,
    headers: { ...corsHeaders, "Content-Type": "application/json" },
  });
});

console.log(`🚀 taxi-realtime-openai edge function ready`);
