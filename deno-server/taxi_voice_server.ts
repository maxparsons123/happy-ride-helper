/**
 * Taxi Voice AI Server - Standalone Deno Implementation
 * 
 * A complete voice AI server for taxi booking that can run independently.
 * Uses OpenAI Realtime API for conversational AI with voice.
 * 
 * Run with: deno run --allow-net --allow-env taxi_voice_server.ts
 */

// Configuration from environment
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY") || "";
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const COMPANY_NAME = Deno.env.get("COMPANY_NAME") || "ABC Taxis";
const AGENT_NAME = Deno.env.get("AGENT_NAME") || "Ada";
const VOICE = Deno.env.get("VOICE") || "shimmer";
const PORT = parseInt(Deno.env.get("PORT") || "8080");

// System prompt for the AI agent
const SYSTEM_PROMPT = `You are ${AGENT_NAME}, a friendly and efficient phone receptionist for ${COMPANY_NAME}. Your job is to take taxi bookings over the phone.

## Your Personality
- Warm, professional, and naturally conversational
- Use casual confirmations like "lovely", "great", "perfect"
- Keep responses concise - this is a phone call
- Never spell out addresses letter by letter
- Speak numbers naturally (e.g., "twenty-three" not "2-3")

## Booking Flow
1. **Greet** the caller warmly
2. **Get pickup address** - Ask "Where can we pick you up from?"
3. **Get destination** - Ask "And where are you heading to?"
4. **Confirm passengers** - Default to 1 if not mentioned, ask if unclear
5. **Confirm details** - Read back: "So that's from [pickup] to [destination], is that right?"
6. **Get fare quote** - Use book_taxi tool with confirmation_state="request_quote"
7. **Confirm fare** - Tell customer the fare and ETA, ask if they want to book
8. **Complete booking** - Use book_taxi tool with confirmation_state="confirm"

## Important Rules
- ALWAYS confirm addresses before booking
- If an address is ambiguous, ask for clarification (house number, area, postcode)
- For regular customers, you may recognize their usual pickup
- Handle "ASAP" pickups and future bookings
- If customer wants to change something, update and re-confirm

## Tool Usage
- Use save_customer_name when the customer tells you their name
- Use book_taxi with confirmation_state="request_quote" to get a fare quote
- Use book_taxi with confirmation_state="confirm" after customer agrees to the fare

## Response Style
- Keep responses SHORT and natural for phone conversation
- One question at a time
- Don't over-explain or be verbose`;

// Tool definitions for OpenAI
const TOOLS = [
  {
    type: "function",
    name: "save_customer_name",
    description: "Save the customer's name when they provide it",
    parameters: {
      type: "object",
      properties: {
        name: {
          type: "string",
          description: "The customer's name"
        }
      },
      required: ["name"]
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Book a taxi or request a fare quote",
    parameters: {
      type: "object",
      properties: {
        pickup: {
          type: "string",
          description: "Pickup address"
        },
        destination: {
          type: "string",
          description: "Destination address"
        },
        passengers: {
          type: "number",
          description: "Number of passengers (default 1)"
        },
        scheduled_time: {
          type: "string",
          description: "Pickup time - 'ASAP' or ISO datetime"
        },
        confirmation_state: {
          type: "string",
          enum: ["request_quote", "confirm"],
          description: "request_quote to get fare, confirm to complete booking"
        }
      },
      required: ["pickup", "destination", "confirmation_state"]
    }
  }
];

// Session state interface
interface SessionState {
  callId: string;
  callerPhone: string;
  callerName: string | null;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number;
    scheduledTime: string | null;
    fare: string | null;
    eta: string | null;
  };
  conversationHistory: Array<{ role: string; content: string }>;
  pendingQuote: boolean;
  greetingSent: boolean;
}

// Create new session state
function createSessionState(callId: string, callerPhone: string): SessionState {
  return {
    callId,
    callerPhone,
    callerName: null,
    booking: {
      pickup: null,
      destination: null,
      passengers: 1,
      scheduledTime: null,
      fare: null,
      eta: null,
    },
    conversationHistory: [],
    pendingQuote: false,
    greetingSent: false,
  };
}

// Send webhook to dispatch system
async function sendDispatchWebhook(
  action: string,
  sessionState: SessionState,
  additionalData: Record<string, unknown> = {}
): Promise<Record<string, unknown> | null> {
  if (!DISPATCH_WEBHOOK_URL) {
    console.log(`[${sessionState.callId}] No dispatch webhook configured`);
    return null;
  }

  const payload = {
    action,
    call_id: sessionState.callId,
    caller_phone: sessionState.callerPhone,
    caller_name: sessionState.callerName,
    booking: sessionState.booking,
    timestamp: new Date().toISOString(),
    ...additionalData,
  };

  try {
    console.log(`[${sessionState.callId}] Sending webhook: ${action}`);
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    if (response.ok) {
      const data = await response.json();
      console.log(`[${sessionState.callId}] Webhook response:`, data);
      return data;
    } else {
      console.error(`[${sessionState.callId}] Webhook error: ${response.status}`);
      return null;
    }
  } catch (error) {
    console.error(`[${sessionState.callId}] Webhook failed:`, error);
    return null;
  }
}

// Handle save_customer_name tool call
async function handleSaveCustomerName(
  args: { name: string },
  sessionState: SessionState
): Promise<string> {
  sessionState.callerName = args.name;
  console.log(`[${sessionState.callId}] Saved customer name: ${args.name}`);
  
  await sendDispatchWebhook("customer_identified", sessionState);
  
  return JSON.stringify({
    success: true,
    message: `Customer name saved: ${args.name}`,
  });
}

// Handle book_taxi tool call
async function handleBookTaxi(
  args: {
    pickup: string;
    destination: string;
    passengers?: number;
    scheduled_time?: string;
    confirmation_state: string;
  },
  sessionState: SessionState
): Promise<string> {
  // Update session state with booking details
  sessionState.booking.pickup = args.pickup;
  sessionState.booking.destination = args.destination;
  sessionState.booking.passengers = args.passengers || 1;
  sessionState.booking.scheduledTime = args.scheduled_time || "ASAP";

  if (args.confirmation_state === "request_quote") {
    // Request fare quote from dispatch
    sessionState.pendingQuote = true;
    
    const response = await sendDispatchWebhook("request_quote", sessionState);
    
    if (response && response.fare) {
      sessionState.booking.fare = response.fare as string;
      sessionState.booking.eta = (response.eta as string) || "5-10 minutes";
      sessionState.pendingQuote = false;
      
      return JSON.stringify({
        success: true,
        fare: response.fare,
        eta: response.eta || "5-10 minutes",
        message: `Fare quote: ${response.fare}, ETA: ${response.eta || "5-10 minutes"}`,
      });
    } else {
      // Generate estimated fare if no webhook response
      const estimatedFare = "£8-12";
      sessionState.booking.fare = estimatedFare;
      sessionState.booking.eta = "5-10 minutes";
      sessionState.pendingQuote = false;
      
      return JSON.stringify({
        success: true,
        fare: estimatedFare,
        eta: "5-10 minutes",
        message: `Estimated fare: ${estimatedFare}, ETA: 5-10 minutes`,
      });
    }
  } else if (args.confirmation_state === "confirm") {
    // Confirm the booking with dispatch
    const response = await sendDispatchWebhook("confirm_booking", sessionState);
    
    const bookingRef = response?.booking_ref || `ABC${Date.now().toString(36).toUpperCase()}`;
    
    return JSON.stringify({
      success: true,
      booking_ref: bookingRef,
      message: `Booking confirmed! Reference: ${bookingRef}`,
      pickup: sessionState.booking.pickup,
      destination: sessionState.booking.destination,
      fare: sessionState.booking.fare,
      eta: sessionState.booking.eta,
    });
  }

  return JSON.stringify({
    success: false,
    error: "Invalid confirmation_state",
  });
}

// Handle tool calls from OpenAI
async function handleToolCall(
  toolName: string,
  args: Record<string, unknown>,
  sessionState: SessionState
): Promise<string> {
  console.log(`[${sessionState.callId}] Tool call: ${toolName}`, args);
  
  switch (toolName) {
    case "save_customer_name":
      return handleSaveCustomerName(args as { name: string }, sessionState);
    case "book_taxi":
      return handleBookTaxi(
        args as {
          pickup: string;
          destination: string;
          passengers?: number;
          scheduled_time?: string;
          confirmation_state: string;
        },
        sessionState
      );
    default:
      return JSON.stringify({ error: `Unknown tool: ${toolName}` });
  }
}

// Handle WebSocket connection from SIP bridge
async function handleWebSocketConnection(ws: WebSocket, callId: string, callerPhone: string) {
  console.log(`[${callId}] New WebSocket connection from ${callerPhone}`);
  
  const sessionState = createSessionState(callId, callerPhone);
  let openaiWs: WebSocket | null = null;

  // Connect to OpenAI Realtime API
  try {
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`]
    );

    openaiWs.onopen = () => {
      console.log(`[${callId}] Connected to OpenAI Realtime API`);
      
      // Configure the session
      const sessionConfig = {
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          voice: VOICE,
          instructions: SYSTEM_PROMPT,
          input_audio_format: "pcm16",
          output_audio_format: "pcm16",
          input_audio_transcription: {
            model: "whisper-1",
          },
          turn_detection: {
            type: "server_vad",
            threshold: 0.5,
            prefix_padding_ms: 300,
            silence_duration_ms: 500,
          },
          tools: TOOLS,
          tool_choice: "auto",
        },
      };
      
      openaiWs!.send(JSON.stringify(sessionConfig));
      
      // Send greeting after a short delay
      setTimeout(() => {
        if (!sessionState.greetingSent && openaiWs?.readyState === WebSocket.OPEN) {
          sessionState.greetingSent = true;
          const greeting = {
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [
                {
                  type: "input_text",
                  text: "[System: A new caller has connected. Greet them warmly and ask how you can help with their taxi booking.]",
                },
              ],
            },
          };
          openaiWs.send(JSON.stringify(greeting));
          openaiWs.send(JSON.stringify({ type: "response.create" }));
        }
      }, 500);
    };

    openaiWs.onmessage = async (event) => {
      try {
        const message = JSON.parse(event.data.toString());
        
        switch (message.type) {
          case "response.audio.delta":
            // Forward audio to SIP bridge
            if (ws.readyState === WebSocket.OPEN) {
              ws.send(JSON.stringify({
                type: "audio",
                audio: message.delta,
              }));
            }
            break;
            
          case "response.audio_transcript.done":
            // AI finished speaking
            console.log(`[${callId}] AI: ${message.transcript}`);
            sessionState.conversationHistory.push({
              role: "assistant",
              content: message.transcript,
            });
            break;
            
          case "conversation.item.input_audio_transcription.completed":
            // User speech transcribed
            console.log(`[${callId}] User: ${message.transcript}`);
            sessionState.conversationHistory.push({
              role: "user",
              content: message.transcript,
            });
            break;
            
          case "response.function_call_arguments.done":
            // Handle tool call
            const toolResult = await handleToolCall(
              message.name,
              JSON.parse(message.arguments),
              sessionState
            );
            
            // Send tool result back to OpenAI
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: message.call_id,
                  output: toolResult,
                },
              }));
              openaiWs.send(JSON.stringify({ type: "response.create" }));
            }
            break;
            
          case "error":
            console.error(`[${callId}] OpenAI error:`, message.error);
            break;
        }
      } catch (error) {
        console.error(`[${callId}] Error processing OpenAI message:`, error);
      }
    };

    openaiWs.onerror = (error) => {
      console.error(`[${callId}] OpenAI WebSocket error:`, error);
    };

    openaiWs.onclose = () => {
      console.log(`[${callId}] OpenAI WebSocket closed`);
    };

  } catch (error) {
    console.error(`[${callId}] Failed to connect to OpenAI:`, error);
    ws.close();
    return;
  }

  // Handle messages from SIP bridge
  ws.onmessage = (event) => {
    try {
      const message = JSON.parse(event.data.toString());
      
      switch (message.type) {
        case "audio":
          // Forward audio to OpenAI
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: message.audio,
            }));
          }
          break;
          
        case "hangup":
          console.log(`[${callId}] Call ended by caller`);
          sendDispatchWebhook("call_ended", sessionState);
          openaiWs?.close();
          break;
      }
    } catch (error) {
      console.error(`[${callId}] Error processing SIP message:`, error);
    }
  };

  ws.onclose = () => {
    console.log(`[${callId}] SIP WebSocket closed`);
    sendDispatchWebhook("call_ended", sessionState);
    openaiWs?.close();
  };

  ws.onerror = (error) => {
    console.error(`[${callId}] SIP WebSocket error:`, error);
  };
}

// HTTP request handler
async function handleRequest(req: Request): Promise<Response> {
  const url = new URL(req.url);
  
  // CORS headers
  const corsHeaders = {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type, Authorization",
  };

  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  // Health check endpoint
  if (url.pathname === "/health" || url.pathname === "/") {
    return new Response(
      JSON.stringify({
        status: "ok",
        service: "Taxi Voice AI Server",
        agent: AGENT_NAME,
        company: COMPANY_NAME,
        version: "1.0.0",
      }),
      {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      }
    );
  }

  // WebSocket upgrade for voice calls
  if (url.pathname === "/voice" || url.pathname === "/ws") {
    const upgrade = req.headers.get("upgrade") || "";
    
    if (upgrade.toLowerCase() !== "websocket") {
      return new Response(
        JSON.stringify({
          error: "WebSocket upgrade required",
          usage: "Connect via WebSocket to /voice?call_id=xxx&caller_phone=xxx",
        }),
        {
          status: 426,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        }
      );
    }

    const callId = url.searchParams.get("call_id") || `call-${Date.now()}`;
    const callerPhone = url.searchParams.get("caller_phone") || "unknown";

    const { socket, response } = Deno.upgradeWebSocket(req);
    
    handleWebSocketConnection(socket, callId, callerPhone);
    
    return response;
  }

  // Webhook test endpoint
  if (url.pathname === "/webhook-test" && req.method === "POST") {
    try {
      const body = await req.json();
      console.log("Webhook test received:", body);
      
      // Simulate dispatch response
      return new Response(
        JSON.stringify({
          success: true,
          fare: "£12.50",
          eta: "8 minutes",
          booking_ref: `TEST${Date.now().toString(36).toUpperCase()}`,
        }),
        {
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        }
      );
    } catch (error) {
      return new Response(
        JSON.stringify({ error: "Invalid JSON" }),
        {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        }
      );
    }
  }

  // 404 for unknown routes
  return new Response(
    JSON.stringify({
      error: "Not found",
      available_endpoints: [
        "GET / - Health check",
        "GET /health - Health check",
        "WS /voice - Voice call WebSocket",
        "WS /ws - Voice call WebSocket (alias)",
        "POST /webhook-test - Test webhook endpoint",
      ],
    }),
    {
      status: 404,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    }
  );
}

// Start the server
console.log(`
╔════════════════════════════════════════════════════════════╗
║           Taxi Voice AI Server - Deno Edition              ║
╠════════════════════════════════════════════════════════════╣
║  Agent: ${AGENT_NAME.padEnd(49)}║
║  Company: ${COMPANY_NAME.padEnd(47)}║
║  Voice: ${VOICE.padEnd(49)}║
║  Port: ${PORT.toString().padEnd(50)}║
╠════════════════════════════════════════════════════════════╣
║  Endpoints:                                                ║
║    GET  /health     - Health check                         ║
║    WS   /voice      - Voice call WebSocket                 ║
║    POST /webhook-test - Test webhook                       ║
╚════════════════════════════════════════════════════════════╝
`);

if (!OPENAI_API_KEY) {
  console.warn("⚠️  WARNING: OPENAI_API_KEY not set!");
}

if (!DISPATCH_WEBHOOK_URL) {
  console.warn("⚠️  WARNING: DISPATCH_WEBHOOK_URL not set - using mock responses");
}

Deno.serve({ port: PORT }, handleRequest);
