import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_INSTRUCTIONS = `You are a friendly, cheerful Taxi Dispatcher for "Imtech Taxi" taking phone calls.

PERSONALITY:
- Warm and welcoming like a friendly local
- Use casual British phrases: "Brilliant!", "Lovely!", "Right then!", "Smashing!"
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Add light banter when appropriate

BOOKING FLOW:
1. Greet warmly and ask for pickup location
2. Confirm pickup, then ask for destination
3. Confirm destination, then ask for number of passengers
4. When you have all 3 details, use the book_taxi function to confirm
5. Tell the customer their taxi is on the way with ETA and fare

PRICING:
- City trips: £15-25
- Airport: £45
- 6-seater van: add £5
- ETA: Always 5-8 minutes

RULES:
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

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

  // Connect to OpenAI Realtime API
  const connectToOpenAI = () => {
    console.log(`[${callId}] Connecting to OpenAI Realtime API...`);
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-10-01",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      console.log(`[${callId}] Connected to OpenAI`);
    };

    openaiWs.onmessage = async (event) => {
      const data = JSON.parse(event.data);
      
      // Session created - send configuration
      if (data.type === "session.created") {
        console.log(`[${callId}] Session created, sending config...`);
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            voice: "alloy",
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            turn_detection: {
              type: "server_vad",
              threshold: 0.5,
              prefix_padding_ms: 300,
              silence_duration_ms: 800
            },
            tools: [
              {
                type: "function",
                name: "book_taxi",
                description: "Book a taxi when pickup, destination and number of passengers are all confirmed by the customer",
                parameters: {
                  type: "object",
                  properties: {
                    pickup: { type: "string", description: "Pickup location" },
                    destination: { type: "string", description: "Drop-off location" },
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

      // Forward audio to Asterisk bridge
      if (data.type === "response.audio.delta") {
        socket.send(JSON.stringify({
          type: "audio",
          audio: data.delta
        }));
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
        socket.send(JSON.stringify({
          type: "transcript",
          text: data.transcript,
          role: "user"
        }));
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
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
          
          // Notify client
          socket.send(JSON.stringify({
            type: "booking_confirmed",
            booking: { ...args, fare: `£${fare}`, eta }
          }));
        }
      }

      // Response completed
      if (data.type === "response.done") {
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

      // Commit audio buffer (end of speech)
      if (message.type === "commit" && openaiWs?.readyState === WebSocket.OPEN) {
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.commit"
        }));
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
