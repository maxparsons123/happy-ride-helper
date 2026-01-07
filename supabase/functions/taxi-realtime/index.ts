import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_INSTRUCTIONS = `You are Ada, a friendly and professional Taxi Dispatcher for "247 Radio Carz" taking phone calls.

YOUR INTRODUCTION (say this IMMEDIATELY when the call starts):
"Hello, my name is Ada from 247 Radio Carz. How can I help you with your travels today?"

PERSONALITY:
- Warm, welcoming British personality
- Use casual British phrases: "Brilliant!", "Lovely!", "Right then!", "Smashing!", "No worries!"
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable

BOOKING FLOW - FOLLOW THIS EXACTLY:
1. Greet the customer with the introduction above
2. Ask: "Where would you like to be picked up from?"
3. When they give pickup, repeat it back EXACTLY as they said it and ask: "And where are you heading to?"
4. When they give destination, repeat it back EXACTLY as they said it and ask: "How many passengers will there be?"
5. When they give passenger count, you now have ALL 3 details - call the book_taxi function IMMEDIATELY
6. After booking, tell them: "Your taxi is on its way! It'll be with you in [ETA] and the fare is [fare]."

INFORMATION EXTRACTION - CRITICAL:
- Listen carefully for: pickup location, destination, number of passengers
- Extract numbers spoken as words (e.g., "two passengers" = 2)
- If customer gives all info at once (e.g., "I need a taxi from X to Y for 3 people"), extract ALL and call book_taxi immediately
- If any info is unclear, ask for clarification before proceeding

PRICING (calculate based on destination):
- City trips: £15-25 (random within range)
- Airport destinations: £45
- If 5+ passengers: add £5 for 6-seater van
- ETA: Always 5-8 minutes

=== CRITICAL ADDRESS ACCURACY RULES ===

YOU MUST PRESERVE ADDRESSES EXACTLY AS SPOKEN. THIS IS THE MOST IMPORTANT RULE.

1. NEVER PARAPHRASE OR MODIFY ADDRESSES:
   - If customer says "52A High Street" → you say "52A High Street" (NOT "52 High Street" or "58 High Street")
   - If customer says "flat 3, 17 Oak Lane" → you say "flat 3, 17 Oak Lane" exactly
   - If customer says "the Tesco on Mill Road" → you say "the Tesco on Mill Road"

2. COMMON MISHEARD NUMBERS - ASK FOR CLARIFICATION:
   - 15 vs 50 (fifteen vs fifty)
   - 16 vs 60 (sixteen vs sixty)
   - 17 vs 70 (seventeen vs seventy)
   - 18 vs 80 (eighteen vs eighty)
   - 19 vs 90 (nineteen vs ninety)
   - 13 vs 30 (thirteen vs thirty)
   - 14 vs 40 (fourteen vs forty)
   
3. LETTERS IN ADDRESSES:
   - A, B, C after numbers (52A, 18B, 7C) - always include the letter
   - "A" sounds like "8" - if unsure ask "Was that the letter A or the number eight?"
   
4. WHEN IN DOUBT, ALWAYS ASK:
   - "Sorry, was that fifteen or fifty?"
   - "Could you spell that street name for me?"
   - "Was that 52 with the letter A at the end?"

5. WHEN CALLING book_taxi:
   - Use the EXACT address the customer spoke
   - Do NOT "clean up" or standardize addresses
   - Include flats, units, letters, landmarks exactly as stated

WHEN TO CALL book_taxi:
- Call IMMEDIATELY when you have confirmed: pickup + destination + passengers
- Do NOT wait for customer to say "yes" or "confirm" - the function IS the confirmation
- If customer changes details after booking, apologize and take new booking

GENERAL RULES:
- Never ask for information you already have
- If customer mentions extra requirements (wheelchair, child seat), acknowledge but proceed with booking
- Stay focused on completing the booking efficiently`;

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
  let callSource = "web"; // 'web' or 'asterisk'
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let currentAssistantText = ""; // Buffer for assistant transcript

  type KnownBooking = {
    pickup?: string;
    destination?: string;
    passengers?: number;
  };

  // We keep our own "known booking" extracted from the user's *exact* transcript/text,
  // then prefer it over the model's paraphrasing when the booking is confirmed.
  let knownBooking: KnownBooking = {};

  const normalize = (s: string) => s.trim().replace(/\s+/g, " ").replace(/[\s,.;:!?]+$/g, "");

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

  // Broadcast live call updates to the database for monitoring
  const broadcastLiveCall = async (updates: Record<string, any>) => {
    try {
      const { error } = await supabase.from("live_calls").upsert({
        call_id: callId,
        source: callSource,
        started_at: callStartAt,
        ...updates,
        transcripts: transcriptHistory,
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
      if (error) console.error(`[${callId}] Live call broadcast error:`, error);
    } catch (e) {
      console.error(`[${callId}] Live call broadcast exception:`, e);
    }
  };

  const extractFromText = (text: string): Partial<KnownBooking> => {
    const t = text || "";
    const out: Partial<KnownBooking> = {};

    // Pickup: "from X" (stop before "to/going to/heading to" if present)
    const fromMatch = t.match(/\bfrom\s+(.+?)(?:\s+(?:to|going\s+to|heading\s+to)\b|$)/i);
    if (fromMatch?.[1]) out.pickup = normalize(fromMatch[1]);

    // Destination: "to Y" / "going to Y" / "heading to Y"
    const toMatch = t.match(/\b(?:to|going\s+to|heading\s+to)\s+(.+)$/i);
    if (toMatch?.[1]) out.destination = normalize(toMatch[1]);

    // Passengers: "3 passengers" / "three passengers"
    const wordToNum: Record<string, number> = {
      one: 1,
      two: 2,
      three: 3,
      four: 4,
      five: 5,
      six: 6,
      seven: 7,
      eight: 8,
      nine: 9,
      ten: 10,
    };

    const passengersDigit = t.match(/\b(\d+)\s*(?:passengers?|people|persons?)\b/i);
    if (passengersDigit?.[1]) out.passengers = Number(passengersDigit[1]);

    const passengersWord = t.match(/\b(one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:passengers?|people|persons?)\b/i);
    if (passengersWord?.[1]) out.passengers = wordToNum[passengersWord[1].toLowerCase()];

    return out;
  };

  const updateKnownBookingFromText = (text: string, source: "stt" | "text") => {
    const extracted = extractFromText(text);
    const before = { ...knownBooking };

    knownBooking = {
      ...knownBooking,
      ...Object.fromEntries(
        Object.entries(extracted).filter(([, v]) => v !== undefined && v !== null && v !== "")
      ),
    };

    if (
      before.pickup !== knownBooking.pickup ||
      before.destination !== knownBooking.destination ||
      before.passengers !== knownBooking.passengers
    ) {
      console.log(`[${callId}] Known booking updated (${source}):`, knownBooking);
      // Broadcast booking updates
      broadcastLiveCall({
        pickup: knownBooking.pickup,
        destination: knownBooking.destination,
        passengers: knownBooking.passengers
      });
    }
  };

  // When audio is committed (server VAD or manual commit), we expect a response.
  // Some clients still send manual commit; we defensively trigger response.create after STT completes.
  let awaitingResponseAfterCommit = false;
  let responseCreatedSinceCommit = false;

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

        // Broadcast call started
        await broadcastLiveCall({ status: "active" });

        // Trigger initial greeting immediately - inject as system turn for faster response
        console.log(`[${callId}] Triggering initial greeting...`);
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "message",
            role: "user",
            content: [{ type: "input_text", text: "[Call connected - greet the customer]" }]
          }
        }));
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
        currentAssistantText = ""; // Reset buffer

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

      // Forward audio to client AND broadcast to monitoring channel
      if (data.type === "response.audio.delta") {
        console.log(`[${callId}] >>> AUDIO DELTA received, length: ${data.delta?.length || 0}`);
        socket.send(
          JSON.stringify({
            type: "audio",
            audio: data.delta,
          }),
        );
        
        // Broadcast audio to monitoring channel via database insert
        // Monitors will subscribe to this for live audio playback
        try {
          await supabase.from("live_call_audio").insert({
            call_id: callId,
            audio_chunk: data.delta,
            created_at: new Date().toISOString()
          });
        } catch (e) {
          // Ignore audio broadcast errors - monitoring is optional
          console.log(`[${callId}] Audio broadcast skipped`);
        }
      }

      // Log audio done
      if (data.type === "response.audio.done") {
        console.log(`[${callId}] >>> response.audio.done - audio generation complete`);
      }

      // Log audio transcript done - save complete assistant message
      if (data.type === "response.audio_transcript.done") {
        console.log(`[${callId}] >>> response.audio_transcript.done: "${data.transcript}"`);
        if (data.transcript) {
          transcriptHistory.push({
            role: "assistant",
            text: data.transcript,
            timestamp: new Date().toISOString()
          });
          // Broadcast transcript update
          broadcastLiveCall({});
        }
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
        updateKnownBookingFromText(data.transcript, "stt");
        
        // Save user message to history
        if (data.transcript) {
          transcriptHistory.push({
            role: "user",
            text: data.transcript,
            timestamp: new Date().toISOString()
          });
          // Broadcast transcript update
          broadcastLiveCall({});
        }
        
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

          // Prefer exact values we extracted from the user's transcript/text (prevents 52A -> 50A style drift)
          const finalBooking = {
            pickup: knownBooking.pickup ?? args.pickup,
            destination: knownBooking.destination ?? args.destination,
            passengers: knownBooking.passengers ?? args.passengers,
          };

          bookingData = finalBooking;
          console.log(`[${callId}] Booking (final):`, finalBooking);
          
          // Calculate fare
          const isAirport = String(finalBooking.destination || "").toLowerCase().includes("airport");
          const is6Seater = Number(finalBooking.passengers || 0) > 4;
          let fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
          if (is6Seater) fare += 5;
          const eta = `${Math.floor(Math.random() * 4) + 5} minutes`;
          
          // Log to database
          await supabase.from("call_logs").insert({
            call_id: callId,
            pickup: finalBooking.pickup,
            destination: finalBooking.destination,
            passengers: finalBooking.passengers,
            estimated_fare: `£${fare}`,
            booking_status: "confirmed",
            call_start_at: callStartAt
          });

          // Broadcast booking confirmed
          await broadcastLiveCall({
            pickup: finalBooking.pickup,
            destination: finalBooking.destination,
            passengers: finalBooking.passengers,
            booking_confirmed: true,
            fare: `£${fare}`,
            eta: eta
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
                pickup: finalBooking.pickup,
                destination: finalBooking.destination,
                passengers: finalBooking.passengers,
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
            booking: { ...finalBooking, fare: `£${fare}`, eta }
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
        // Detect Asterisk calls by call_id prefix
        if (callId.startsWith("ast-") || callId.startsWith("asterisk-")) {
          callSource = "asterisk";
        }
        console.log(`[${callId}] Call initialized (source: ${callSource})`);
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
        updateKnownBookingFromText(message.text, "text");
        // Save user text to history
        transcriptHistory.push({
          role: "user",
          text: message.text,
          timestamp: new Date().toISOString()
        });
        broadcastLiveCall({});
        
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

      // Hangup from Asterisk
      if (message.type === "hangup") {
        console.log(`[${callId}] Hangup received`);
        socket.close();
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

    // Update live call status
    await broadcastLiveCall({
      status: "completed",
      ended_at: new Date().toISOString()
    });
    
    openaiWs?.close();
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] WebSocket error:`, error);
  };

  return response;
});
