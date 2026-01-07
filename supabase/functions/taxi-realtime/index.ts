import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_INSTRUCTIONS = `You are Ada, a friendly and professional Taxi Dispatcher for "247 Radio Carz" taking phone calls.

YOUR INTRODUCTION (say this when the call starts):
"Hello, my name is Ada from 247 Radio Carz. How can I help you with your travels today?"

PERSONALITY:
- Warm, welcoming British personality
- Use casual British phrases: "Brilliant!", "Lovely!", "Right then!", "Smashing!", "No worries!"
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable

BOOKING FLOW:
1. After greeting, ask for pickup location
2. Confirm pickup EXACTLY as stated, then ask for destination
3. Confirm destination EXACTLY as stated, then ask for number of passengers
4. When you have all 3 details, use the book_taxi function to confirm
5. Tell the customer their taxi is on the way with ETA and fare

PRICING:
- City trips: £15-25
- Airport: £45
- 6-seater van: add £5
- ETA: Always 5-8 minutes

CRITICAL TRANSCRIPTION RULES:
- NEVER change, correct, or paraphrase addresses - repeat them EXACTLY as the customer said
- If you're unsure of an address, ask the customer to spell it out
- Street names, house numbers, and postcodes must be repeated verbatim
- Do NOT assume or autocorrect street names (e.g., if they say "David Road", say "David Road", not "Aberdeen Road")

GENERAL RULES:
- Always confirm each detail before moving to the next
- If customer changes their mind, be accommodating
- Use the book_taxi function ONLY when you have pickup, destination, AND passengers confirmed`;

serve(async (req) => {
  // Handle regular HTTP requests (health check)
  if (req.headers.get("upgrade") !== "websocket") {
    if (req.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }
    return new Response(JSON.stringify({ 
      status: "ready",
      endpoint: "taxi-realtime",
      protocol: "websocket"
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Upgrade to WebSocket
  const { socket, response } = Deno.upgradeWebSocket(req);
  
  const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
  const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
  const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  
  let openaiWs: WebSocket | null = null;
  let callId = `call-${Date.now()}`;
  let callStartAt = new Date().toISOString();
  let bookingData: any = {};
  let sessionReady = false;
  let pendingMessages: any[] = [];

  // When audio is committed (server VAD or manual commit), we expect a response.
  // Some clients still send manual commit; we defensively trigger response.create after STT completes.
  let awaitingResponseAfterCommit = false;
  let responseCreatedSinceCommit = false;

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

  // Connect to OpenAI Realtime API
  const connectToOpenAI = () => {
    console.log(`[${callId}] Connecting to OpenAI Realtime API...`);
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      console.log(`[${callId}] Connected to OpenAI`);
    };

    openaiWs.onmessage = async (event) => {
      const data = JSON.parse(event.data);
      
      // Session created - send configuration
      if (data.type === "session.created") {
        console.log(`[${callId}] Session created. Server defaults:`, data.session);
        console.log(`[${callId}] Sending session.update...`);
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            voice: "shimmer", // British female voice
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            // Server VAD for <100ms barge-in support
            turn_detection: {
              type: "server_vad",
              threshold: 0.5,
              prefix_padding_ms: 200,
              silence_duration_ms: 500, // Quick response after speech ends
              create_response: true // Auto-create response when speech ends
            },
            tools: [
              {
                type: "function",
                name: "book_taxi",
                description: "Book a taxi when pickup, destination and number of passengers are all confirmed by the customer",
                parameters: {
                  type: "object",
                  properties: {
                    pickup: { type: "string", description: "Pickup location exactly as customer stated" },
                    destination: { type: "string", description: "Drop-off location exactly as customer stated" },
                    passengers: { type: "integer", description: "Number of passengers" }
                  },
                  required: ["pickup", "destination", "passengers"]
                }
              }
            ],
            tool_choice: "auto",
            instructions: SYSTEM_INSTRUCTIONS
          }
        }));
      }

      // Session updated - now ready
      if (data.type === "session.updated") {
        console.log(`[${callId}] Session ready! Effective config:`, data.session);
        sessionReady = true;
        socket.send(JSON.stringify({ type: "session_ready" }));

        // Trigger initial greeting NOW that session is fully configured
        console.log(`[${callId}] Triggering initial greeting...`);
        openaiWs?.send(JSON.stringify({
          type: "response.create",
          response: { modalities: ["audio", "text"] }
        }));

        // Process any pending messages
        while (pendingMessages.length > 0) {
          const msg = pendingMessages.shift();
          console.log(`[${callId}] Processing pending message:`, msg.type);
          if (msg.type === "text") {
            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{ type: "input_text", text: msg.text }],
                },
              }),
            );
            openaiWs?.send(
              JSON.stringify({
                type: "response.create",
                response: { modalities: ["audio", "text"] },
              }),
            );
          }
        }
      }

      // AI response started
      if (data.type === "response.created") {
        console.log(`[${callId}] >>> response.created - AI starting to generate`);

        if (awaitingResponseAfterCommit) {
          responseCreatedSinceCommit = true;
          console.log(`[${callId}] >>> response.created observed for committed audio turn`);
        }

        socket.send(JSON.stringify({ type: "ai_speaking", speaking: true }));
      }

      // Log response output item added (tells us what modalities are being generated)
      if (data.type === "response.output_item.added") {
        console.log(`[${callId}] >>> response.output_item.added:`, JSON.stringify(data.item));
      }

      // Log content part added
      if (data.type === "response.content_part.added") {
        console.log(`[${callId}] >>> response.content_part.added:`, JSON.stringify(data.part));
      }

      // If the model responds in TEXT modality, forward it as assistant transcript
      if (data.type === "response.text.delta" || data.type === "response.output_text.delta") {
        const delta = data.delta || "";
        console.log(`[${callId}] >>> text delta: "${delta}"`);
        if (delta) {
          socket.send(
            JSON.stringify({
              type: "transcript",
              text: delta,
              role: "assistant",
            }),
          );
        }
      }

      // Forward audio to client
      if (data.type === "response.audio.delta") {
        console.log(`[${callId}] >>> AUDIO DELTA received, length: ${data.delta?.length || 0}`);
        socket.send(
          JSON.stringify({
            type: "audio",
            audio: data.delta,
          }),
        );
      }

      // Log audio done
      if (data.type === "response.audio.done") {
        console.log(`[${callId}] >>> response.audio.done - audio generation complete`);
      }

      // Log audio transcript
      if (data.type === "response.audio_transcript.done") {
        console.log(`[${callId}] >>> response.audio_transcript.done: "${data.transcript}"`);
      }

      // Forward transcript for logging
      if (data.type === "response.audio_transcript.delta") {
        socket.send(JSON.stringify({
          type: "transcript",
          text: data.delta,
          role: "assistant"
        }));
      }

      // User transcript
      if (data.type === "conversation.item.input_audio_transcription.completed") {
        console.log(`[${callId}] User said: ${data.transcript}`);
        socket.send(
          JSON.stringify({
            type: "transcript",
            text: data.transcript,
            role: "user",
          }),
        );

        // IMPORTANT: With some clients and/or manual commit, OpenAI may not auto-create a response
        // even when server_vad is enabled. If we have a committed audio turn and no response yet,
        // explicitly request one now.
        if (awaitingResponseAfterCommit && !responseCreatedSinceCommit && openaiWs?.readyState === WebSocket.OPEN) {
          console.log(`[${callId}] No response.created after commit; sending response.create as fallback`);
          openaiWs.send(
            JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] },
            }),
          );
        }

        awaitingResponseAfterCommit = false;
        responseCreatedSinceCommit = false;
      }

      // Speech started (barge-in detection)
      if (data.type === "input_audio_buffer.speech_started") {
        console.log(`[${callId}] >>> User started speaking (barge-in)`);
        socket.send(JSON.stringify({ type: "user_speaking", speaking: true }));
      }

      // Speech stopped
      if (data.type === "input_audio_buffer.speech_stopped") {
        console.log(`[${callId}] >>> User stopped speaking`);
        socket.send(JSON.stringify({ type: "user_speaking", speaking: false }));
      }

      // Audio buffer committed (server VAD auto-commits)
      if (data.type === "input_audio_buffer.committed") {
        console.log(`[${callId}] >>> Audio buffer committed, item_id: ${data.item_id}`);
        awaitingResponseAfterCommit = true;
        responseCreatedSinceCommit = false;
      }

      // DEBUG: log response lifecycle events (helps diagnose missing audio)
      if (data.type === "response.done") {
        const status = data.response?.status || "unknown";
        const outputCount = data.response?.output?.length || 0;
        console.log(`[${callId}] >>> response.done - status: ${status}, outputs: ${outputCount}`);
        if (data.response?.status_details) {
          console.log(`[${callId}] >>> status_details:`, JSON.stringify(data.response.status_details));
        }
      }

      // Handle function calls
      if (data.type === "response.function_call_arguments.done") {
        console.log(`[${callId}] Function call: ${data.name}`, data.arguments);
        
        if (data.name === "book_taxi") {
          const args = JSON.parse(data.arguments);
          bookingData = args;
          
          // Calculate fare
          const isAirport = args.destination?.toLowerCase().includes("airport");
          const is6Seater = args.passengers > 4;
          let fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
          if (is6Seater) fare += 5;
          const eta = `${Math.floor(Math.random() * 4) + 5} minutes`;
          
          // Log to database
          await supabase.from("call_logs").insert({
            call_id: callId,
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers,
            estimated_fare: `£${fare}`,
            booking_status: "confirmed",
            call_start_at: callStartAt
          });
          
          // Send function result back to OpenAI
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: data.call_id,
              output: JSON.stringify({
                success: true,
                booking_id: callId,
                pickup: args.pickup,
                destination: args.destination,
                passengers: args.passengers,
                estimated_fare: `£${fare}`,
                eta: eta
              })
            }
          }));
          
          // Trigger response
          openaiWs?.send(
            JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] },
            }),
          );
          
          // Notify client
          socket.send(JSON.stringify({
            type: "booking_confirmed",
            booking: { ...args, fare: `£${fare}`, eta }
          }));
        }
      }

      // Response completed
      if (data.type === "response.done") {
        socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
        socket.send(JSON.stringify({ type: "response_done" }));
      }

      // Error handling
      if (data.type === "error") {
        console.error(`[${callId}] OpenAI error:`, data.error);
        socket.send(JSON.stringify({ type: "error", error: data.error }));
      }
    };

    openaiWs.onerror = (error) => {
      console.error(`[${callId}] OpenAI WebSocket error:`, error);
    };

    openaiWs.onclose = () => {
      console.log(`[${callId}] OpenAI connection closed`);
    };
  };

  socket.onopen = () => {
    console.log(`[${callId}] Client connected`);
    connectToOpenAI();
  };

  socket.onmessage = (event) => {
    try {
      const message = JSON.parse(event.data);
      
      // Set call ID from client
      if (message.type === "init") {
        callId = message.call_id || callId;
        callStartAt = new Date().toISOString();
        console.log(`[${callId}] Call initialized`);
        return;
      }
      
      // Forward audio to OpenAI
      if (message.type === "audio" && openaiWs?.readyState === WebSocket.OPEN) {
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: message.audio
        }));
      }

      // TEXT MODE: Send text message directly (for testing without audio)
      if (message.type === "text") {
        console.log(`[${callId}] Text mode input: ${message.text}`);
        if (sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: message.text }]
            }
          }));
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: { modalities: ["audio", "text"] }
          }));
        } else {
          console.log(`[${callId}] Session not ready, queuing message`);
          pendingMessages.push(message);
        }
      }

      // Manual commit (for push-to-talk clients)
      // Mark that we should get a response for this turn.
      if (message.type === "commit" && openaiWs?.readyState === WebSocket.OPEN) {
        console.log(`[${callId}] Manual commit received`);
        awaitingResponseAfterCommit = true;
        responseCreatedSinceCommit = false;
        openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
      }

    } catch (error) {
      console.error(`[${callId}] Message parse error:`, error);
    }
  };

  socket.onclose = async () => {
    console.log(`[${callId}] Client disconnected`);
    
    // Update call end time
    await supabase.from("call_logs")
      .update({ call_end_at: new Date().toISOString() })
      .eq("call_id", callId);
    
    openaiWs?.close();
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] WebSocket error:`, error);
  };

  return response;
});
