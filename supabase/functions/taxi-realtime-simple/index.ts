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
- Returning caller (no booking): "Hello [NAME]! Where would you like to be picked up from?"
- Returning caller (active booking): "Hello [NAME]! I can see you have an active booking. Would you like to keep it, change it, or cancel it?"

BOOKING FLOW (STRICT ORDER):
1. Get PICKUP address FIRST. Ask: "Where would you like to be picked up from?"
2. Get DESTINATION address SECOND. Ask: "And where are you going to?"
3. Get PASSENGERS if not mentioned. Ask: "How many passengers?"
4. ONLY if destination is an AIRPORT or TRAIN STATION: ask "Any bags?" Otherwise SKIP bags entirely.
5. When ALL details are known → Say "Let me check your booking" then CALL book_taxi function. WAIT for the result before speaking again.

CRITICAL TOOL USAGE - READ CAREFULLY:
- When you have pickup, destination, and passengers → Say "Let me check your booking" then CALL book_taxi function.
- The book_taxi function will return fare and ETA from dispatch. You MUST WAIT for the result.
- If the result contains "ada_message" → SPEAK THAT MESSAGE EXACTLY to the customer. This is dispatch telling you something important (e.g., address issue).
- If the result contains "needs_clarification: true" → Ask the customer the question in ada_message, then wait for their answer.
- If the result contains "rejected: true" → Tell the customer we cannot process their booking using the ada_message.
- If the result contains "success: true" → Say: "Booked! [ETA] minutes, [FARE]. Anything else?"
- DO NOT make up fares or ETAs. You MUST use the values returned by book_taxi.
- If user says "cancel" → CALL cancel_booking function FIRST, then respond.
- If user corrects name → CALL save_customer_name function immediately.
- Call end_call function after saying "Safe travels!".

RULES:
1. ALWAYS ask for PICKUP before DESTINATION. Never assume or swap them.
2. NEVER repeat addresses, fares, or full routes.
3. NEVER say: "Just to double-check", "Shall I book that?", "Is that correct?".
4. GLOBAL service — accept any address.
5. If "usual trip" → summarize last trip, ask "Shall I book that again?" → wait for YES.
6. DO NOT ask about bags for cities. Only ask for airports or train stations.

IMPORTANT: If user says "going TO [address]" that is DESTINATION, not pickup.
If user says "from [address]" or "pick me up at [address]" that is PICKUP.
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
// Phonetic fixes for common telephony mishearings
const STT_CORRECTIONS: Record<string, string> = {
  // Cancel intent variations (telephony noise triggers these)
  "come to sleep": "cancel it",
  "go to sleep": "cancel it",
  "come see it": "cancel it",
  "count to three": "cancel",
  "can't sell it": "cancel it",
  "cancel eat": "cancel it",
  "concert": "cancel",
  "counter": "cancel",
  "counsel": "cancel",
  "council": "cancel",
  "can so": "cancel",
  "can soul": "cancel",
  
  // Airport/Station mishearings
  "heather oh": "Heathrow",
  "heather row": "Heathrow",
  "heath row": "Heathrow",
  "heather o": "Heathrow",
  "heat throw": "Heathrow",
  "gat wick": "Gatwick",
  "got wick": "Gatwick",
  "cat wick": "Gatwick",
  "stand stead": "Stansted",
  "stan stead": "Stansted",
  "luton air port": "Luton Airport",
  "birmingham air port": "Birmingham Airport",
  "man chest her": "Manchester",
  "man chester": "Manchester",
  "lead station": "Leeds station",
  "leads station": "Leeds station",
  "kings cross": "King's Cross",
  "saint pan crass": "St Pancras",
  "saint pancreas": "St Pancras",
  "euston": "Euston",
  "you stone": "Euston",
  "padding ton": "Paddington",
  "victoria coach": "Victoria Coach Station",
  
  // City/Area mishearings
  "click on street": "Coventry",
  "coven tree": "Coventry",
  "cover entry": "Coventry",
  "birming ham": "Birmingham",
  "burming ham": "Birmingham",
  "wolver hampton": "Wolverhampton",
  "wolves hampton": "Wolverhampton",
  "leaming ton": "Leamington",
  "lemington": "Leamington",
  "warrick": "Warwick",
  "war wick": "Warwick",
  "nun eaten": "Nuneaton",
  "new neaten": "Nuneaton",
  "ken ill worth": "Kenilworth",
  "bed worth": "Bedworth",
  "rugby": "Rugby",
  "rug bee": "Rugby",
  
  // Street name mishearings
  "david rose": "David Road",
  "davie road": "David Road",
  "davey road": "David Road",
  "david wrote": "David Road",
  "high street": "High Street",
  "hi street": "High Street",
  "church road": "Church Road",
  "church wrote": "Church Road",
  "station road": "Station Road",
  "station wrote": "Station Road",
  "london road": "London Road",
  "london wrote": "London Road",
  
  // Number mishearings
  "for passengers": "4 passengers",
  "fore passengers": "4 passengers",
  "to passengers": "2 passengers",
  "too passengers": "2 passengers",
  "tree passengers": "3 passengers",
  "free passengers": "3 passengers",
  "one passenger": "1 passenger",
  "won passenger": "1 passenger",
  "wan passenger": "1 passenger",
  "five passengers": "5 passengers",
  "fife passengers": "5 passengers",
  "six passengers": "6 passengers",
  "sicks passengers": "6 passengers",
  
  // Common phrases
  "pick me up": "pick me up",
  "pick up from": "pick up from",
  "going to": "going to",
  "go into": "going to",
  "drop off at": "drop off at",
  "drop of at": "drop off at",
  
  // House number suffixes
  "a high": "A High",
  "b high": "B High",
  "fifty to a": "52A",
  "fifty too a": "52A",
};

// Hallucination patterns - common STT artifacts from telephony noise
// NOTE: Do NOT filter standalone numbers - they are valid passenger counts!
const HALLUCINATION_PATTERNS = [
  /^(um+|uh+|ah+|er+|hmm+)\.?$/i,
  /^\.+$/,
  /^it's raining/i, // Common telephony artifact
  /^nice weather/i,
  /^how are you/i,
  /^good morning\.?$/i,
  /^good afternoon\.?$/i,
];

function isHallucination(text: string): boolean {
  const trimmed = text.trim();
  if (trimmed.length < 2) return true;
  // Very short responses during booking flow are likely noise
  return HALLUCINATION_PATTERNS.some(pattern => pattern.test(trimmed));
}

function correctTranscript(text: string): string {
  let corrected = text.toLowerCase();
  for (const [bad, good] of Object.entries(STT_CORRECTIONS)) {
    if (corrected.includes(bad)) {
      corrected = corrected.replace(new RegExp(bad, "gi"), good);
    }
  }
  // Capitalize first letter and return
  return corrected.charAt(0).toUpperCase() + corrected.slice(1);
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
  language: string; // Language code for Whisper STT (e.g., "en", "es", "fr")
  customerName: string | null;
  hasActiveBooking: boolean;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    bags: number | null;
  };
  transcripts: TranscriptItem[];

  // Caller history from database
  callerLastPickup: string | null;
  callerLastDestination: string | null;
  callerTotalBookings: number;

  // Streaming assistant transcript assembly (OpenAI sends token deltas)
  assistantTranscriptIndex: number | null;

  // Debounced DB flush
  transcriptFlushTimer: number | null;

  // Echo guard: track when Ada is speaking to ignore audio feedback
  isAdaSpeaking: boolean;
  echoGuardUntil: number; // timestamp until which to ignore audio

  // Call ended flag - prevents further processing after end_call
  callEnded: boolean;
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
    
    // Deno WebSocket requires protocols array, not headers object
    // Pass auth via URL params or use subprotocol for auth
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
        input_audio_transcription: { 
          model: "whisper-1",
          // Dynamic language hint from session state
          language: sessionState.language,
          // Prompt hint helps Whisper recognize place names and taxi terminology
          prompt: sessionState.language === "en" 
            ? "Taxi booking in UK. Common places: Heathrow, Gatwick, Stansted, Birmingham, Manchester, Coventry, Warwick, Leamington, Nuneaton, Kenilworth. Street types: Road, Street, Lane, Drive, Avenue, Close. Numbers like 52A, 14B. Passengers, destination, pickup."
            : "Taxi booking. Street numbers, addresses, passenger count, pickup location, destination."
        },
        turn_detection: {
          type: "server_vad",
          // Lower threshold (0.5) detects quieter speech better
          threshold: 0.5,
          // More prefix padding captures speech starts (soft consonants)
          prefix_padding_ms: 600,
          // Longer silence duration prevents cutting off slow speakers
          silence_duration_ms: 1200
        },
        temperature: 0.6,
        tools: TOOLS,
        tool_choice: "auto"
      }
    };

    openaiWs?.send(JSON.stringify(sessionUpdate));
    console.log(`[${sessionState.callId}] 📝 Session updated`);
  };

  // Fire-and-forget DB flush - never await, never block voice flow
  const flushTranscriptsToDb = (sessionState: SessionState) => {
    // Clone transcripts to avoid mutation issues
    const transcriptsCopy = [...sessionState.transcripts];
    
    // Fire and forget - do NOT await
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

  // Aggressive batching - only flush every 5 seconds during conversation
  const FLUSH_INTERVAL_MS = 5000;
  
  const scheduleTranscriptFlush = (sessionState: SessionState) => {
    if (sessionState.transcriptFlushTimer) return; // Already scheduled
    sessionState.transcriptFlushTimer = setTimeout(() => {
      sessionState.transcriptFlushTimer = null;
      flushTranscriptsToDb(sessionState);
    }, FLUSH_INTERVAL_MS) as unknown as number;
  };

  // Immediate flush for critical events (call end, booking)
  const immediateFlush = (sessionState: SessionState) => {
    if (sessionState.transcriptFlushTimer) {
      clearTimeout(sessionState.transcriptFlushTimer);
      sessionState.transcriptFlushTimer = null;
    }
    flushTranscriptsToDb(sessionState);
  };

  // --- Handle OpenAI Messages ---
  const handleOpenAIMessage = (message: any, sessionState: SessionState) => {
    // Log all message types for debugging
    if (!["response.audio.delta", "response.audio_transcript.delta"].includes(message.type)) {
      console.log(`[${sessionState.callId}] 📨 OpenAI event: ${message.type}`);
    }
    
    switch (message.type) {
      case "session.created":
        console.log(`[${sessionState.callId}] 🎉 Session created`);
        // Trigger greeting
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
        break;

      case "response.audio.delta":
        // Mark Ada as speaking (echo guard)
        sessionState.isAdaSpeaking = true;
        // Forward audio to bridge
        socket.send(JSON.stringify({
          type: "audio",
          audio: message.delta
        }));
        break;

      case "response.audio.done":
        // Ada finished speaking - set echo guard window (800ms)
        sessionState.isAdaSpeaking = false;
        sessionState.echoGuardUntil = Date.now() + 800;
        console.log(`[${sessionState.callId}] 🔇 Echo guard active for 800ms`);
        break;

      case "response.audio_transcript.delta": {
        // Stream assistant transcript to bridge
        const delta = String(message.delta || "");
        socket.send(
          JSON.stringify({
            type: "transcript",
            text: delta,
            role: "assistant",
          })
        );

        // Accumulate transcript in memory only - don't flush on every delta
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
          // Schedule batched flush (5s debounce)
          scheduleTranscriptFlush(sessionState);
        }
        break;
      }

      case "response.audio_transcript.done": {
        // Mark assistant transcript as finalized for next turn
        sessionState.assistantTranscriptIndex = null;
        // Don't flush here - let the batched timer handle it
        break;
      }

      case "conversation.item.input_audio_transcription.completed": {
        // User transcript from Whisper
        const rawText = String(message.transcript || "").trim();
        
        // Filter out hallucinations/noise
        if (!rawText || isHallucination(rawText)) {
          console.log(`[${sessionState.callId}] 🔇 Filtered hallucination: "${rawText}"`);
          break;
        }
        
        const userText = correctTranscript(rawText);
        console.log(`[${sessionState.callId}] 👤 User: "${userText}"${rawText !== userText ? ` (corrected from: "${rawText}")` : ""}`);

        socket.send(
          JSON.stringify({
            type: "transcript",
            text: userText,
            role: "user",
          })
        );

        if (userText) {
          sessionState.transcripts.push({
            role: "user",
            text: userText,
            timestamp: new Date().toISOString(),
          });
          // Schedule batched flush - don't block voice flow
          scheduleTranscriptFlush(sessionState);
        }
        // NOTE: Do NOT call response.create here - server VAD handles turn-taking automatically
        break;
      }

      case "input_audio_buffer.speech_stopped": {
        // NOTE: Do NOT call response.create here - server VAD handles turn-taking automatically
        // Manual response.create causes "conversation_already_has_active_response" errors
        break;
      }

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
            bags: args.bags || 0
          };
          
          const jobId = crypto.randomUUID();
          let fare = "£12.50";
          let etaMinutes = 8;
          let bookingRef = "";
          let distance = "";
          
          // Call dispatch webhook if configured
          const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL");
          console.log(`[${sessionState.callId}] 🔗 DISPATCH_WEBHOOK_URL configured: ${DISPATCH_WEBHOOK_URL ? 'YES' : 'NO'}`);
          
          if (DISPATCH_WEBHOOK_URL) {
            try {
              console.log(`[${sessionState.callId}] 📡 Calling dispatch webhook: ${DISPATCH_WEBHOOK_URL}`);
              console.log(`[${sessionState.callId}] ⏳ Waiting for dispatch response (10s timeout)...`);
              // Get recent user transcripts as structured array for comparison
              const userTranscripts = sessionState.transcripts
                .filter(t => t.role === "user")
                .slice(-6) // Last 6 user messages
                .map(t => ({ text: t.text, timestamp: t.timestamp }));
              
              const webhookPayload = {
                job_id: jobId,
                call_id: sessionState.callId,
                caller_phone: sessionState.phone,
                caller_name: sessionState.customerName,
                // Ada's interpreted addresses
                ada_pickup: args.pickup,
                ada_destination: args.destination,
                // Raw STT transcripts from this call - each turn separately
                user_transcripts: userTranscripts,
                // Booking details
                passengers: args.passengers || 1,
                bags: args.bags || 0,
                vehicle_type: args.vehicle_type || "saloon",
                pickup_time: args.pickup_time || "now",
                timestamp: new Date().toISOString()
              };
              
              // 10 second timeout to give dispatch time to respond
              const controller = new AbortController();
              const timeoutId = setTimeout(() => controller.abort(), 10000);
              
              const webhookResponse = await fetch(DISPATCH_WEBHOOK_URL, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(webhookPayload),
                signal: controller.signal
              });
              
              clearTimeout(timeoutId);
              
              if (webhookResponse.ok) {
                const dispatchResult = await webhookResponse.json();
                console.log(`[${sessionState.callId}] ✅ Dispatch response:`, dispatchResult);
                fare = dispatchResult.fare || fare;
                etaMinutes = dispatchResult.eta_minutes || etaMinutes;
                bookingRef = dispatchResult.booking_ref || "";
                distance = dispatchResult.distance || "";
                
                // Check if dispatch wants Ada to say something specific (e.g., address issue)
                if (dispatchResult.ada_message) {
                  console.log(`[${sessionState.callId}] 💬 Dispatch message for Ada: ${dispatchResult.ada_message}`);
                  // Return early with the message - don't confirm booking yet
                  result = {
                    success: false,
                    needs_clarification: true,
                    ada_message: dispatchResult.ada_message,
                    message: "Dispatch needs clarification from customer"
                  };
                  break;
                }
                
                // Check if dispatch rejected the booking
                if (dispatchResult.rejected || dispatchResult.success === false) {
                  console.log(`[${sessionState.callId}] ❌ Dispatch rejected booking: ${dispatchResult.rejection_reason || 'Unknown reason'}`);
                  result = {
                    success: false,
                    rejected: true,
                    ada_message: dispatchResult.ada_message || dispatchResult.rejection_reason || "Sorry, we can't process this booking right now.",
                    message: "Booking rejected by dispatch"
                  };
                  break;
                }
              } else {
                console.error(`[${sessionState.callId}] ⚠️ Dispatch webhook failed: ${webhookResponse.status}`);
              }
            } catch (webhookErr) {
              console.error(`[${sessionState.callId}] ⚠️ Dispatch webhook error:`, webhookErr);
            }
          }
          
          // Create booking in DB (only if we got here - not rejected/clarification)
          const { error: bookingError } = await supabase.from("bookings").insert({
            call_id: sessionState.callId,
            caller_phone: sessionState.phone,
            caller_name: sessionState.customerName,
            pickup: args.pickup,
            destination: args.destination,
            passengers: args.passengers || 1,
            fare: fare,
            eta: `${etaMinutes} minutes`,
            status: "confirmed",
            booking_details: { job_id: jobId, booking_ref: bookingRef, distance: distance }
          });
          
          if (bookingError) {
            console.error(`[${sessionState.callId}] Booking DB error:`, bookingError);
          }
          
          result = { 
            success: true, 
            eta_minutes: etaMinutes, 
            fare: fare,
            booking_ref: bookingRef,
            message: "Booking confirmed"
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
          // Immediate flush on call end - capture all transcripts
          immediateFlush(sessionState);
          // Update call status
          await supabase.from("live_calls")
            .update({ status: "completed", ended_at: new Date().toISOString() })
            .eq("call_id", sessionState.callId);
          
          // Mark call as ended - prevent further processing
          sessionState.callEnded = true;
          
          // Close OpenAI connection after a short delay to let goodbye audio finish
          setTimeout(() => {
            console.log(`[${sessionState.callId}] 🔌 Closing OpenAI WebSocket after end_call`);
            openaiWs?.close();
          }, 4000); // 4 second delay for goodbye audio
          
          // DON'T trigger response.create after end_call - Ada should stop talking
          return;

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
      // Handle binary audio (Deno WebSocket receives Blob or ArrayBuffer)
      const isBinary = event.data instanceof Blob || event.data instanceof ArrayBuffer || event.data instanceof Uint8Array;
      
      if (isBinary) {
        if (openaiConnected && openaiWs && state) {
          // ECHO GUARD: Skip audio while Ada is speaking or in guard window
          if (state.isAdaSpeaking || Date.now() < state.echoGuardUntil) {
            // Silently discard audio that could be Ada's echo
            return;
          }

          let audioData: Uint8Array;
          
          if (event.data instanceof Blob) {
            audioData = new Uint8Array(await event.data.arrayBuffer());
          } else if (event.data instanceof ArrayBuffer) {
            audioData = new Uint8Array(event.data);
          } else {
            audioData = event.data;
          }
          
          // Bridge sends 8kHz µ-law, need to convert to 24kHz PCM16 for OpenAI
          // Step 1: Decode µ-law to 16-bit PCM
          const pcm16_8k = new Int16Array(audioData.length);
          for (let i = 0; i < audioData.length; i++) {
            const ulaw = ~audioData[i] & 0xFF;
            const sign = (ulaw & 0x80) ? -1 : 1;
            const exponent = (ulaw >> 4) & 0x07;
            const mantissa = ulaw & 0x0F;
            let sample = ((mantissa << 3) + 0x84) << exponent;
            sample -= 0x84;
            pcm16_8k[i] = sign * sample;
          }
          
          // Step 2: Upsample 8kHz -> 24kHz (3x linear interpolation)
          const pcm16_24k = new Int16Array(pcm16_8k.length * 3);
          for (let i = 0; i < pcm16_8k.length - 1; i++) {
            const s0 = pcm16_8k[i];
            const s1 = pcm16_8k[i + 1];
            pcm16_24k[i * 3] = s0;
            pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
            pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
          }
          // Handle last sample
          const lastIdx = pcm16_8k.length - 1;
          pcm16_24k[lastIdx * 3] = pcm16_8k[lastIdx];
          pcm16_24k[lastIdx * 3 + 1] = pcm16_8k[lastIdx];
          pcm16_24k[lastIdx * 3 + 2] = pcm16_8k[lastIdx];
          
          // Step 3: Convert to base64
          const bytes = new Uint8Array(pcm16_24k.buffer);
          let binary = "";
          for (let i = 0; i < bytes.length; i++) {
            binary += String.fromCharCode(bytes[i]);
          }
          const base64Audio = btoa(binary);
          
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
        const isReconnect = message.reconnect === true;
        
        console.log(`[${callId}] 🚀 Initializing simple session (reconnect=${isReconnect})`);

        // If this is a reconnect attempt, reject it - simple mode doesn't support resumption
        if (isReconnect) {
          console.log(`[${callId}] ❌ Simple mode does not support reconnection`);
          socket.close(1000, "Session expired");
          return;
        }

        // Lookup caller history from database
        let callerLastPickup: string | null = null;
        let callerLastDestination: string | null = null;
        let callerTotalBookings = 0;
        let callerName: string | null = message.customer_name || null;
        
        if (phone && phone !== "unknown") {
          try {
            const { data: callerData } = await supabase
              .from("callers")
              .select("name, last_pickup, last_destination, total_bookings")
              .eq("phone_number", phone)
              .maybeSingle();
            
            if (callerData) {
              callerLastPickup = callerData.last_pickup || null;
              callerLastDestination = callerData.last_destination || null;
              callerTotalBookings = callerData.total_bookings || 0;
              if (!callerName && callerData.name) {
                callerName = callerData.name;
              }
              console.log(`[${callId}] 👤 Found caller history: ${callerTotalBookings} bookings, last: ${callerLastPickup} → ${callerLastDestination}`);
            }
          } catch (e) {
            console.error(`[${callId}] Failed to lookup caller:`, e);
          }
        }

        // Initialize state (fresh session only)
        state = {
          callId,
          phone,
          companyName: message.company_name || DEFAULT_COMPANY,
          agentName: message.agent_name || DEFAULT_AGENT,
          voice: message.voice || DEFAULT_VOICE,
          language: message.language || "en", // Default to English, but accept from init message
          customerName: callerName,
          hasActiveBooking: message.has_active_booking || false,
          booking: { pickup: null, destination: null, passengers: null, bags: null },
          transcripts: [],
          callerLastPickup,
          callerLastDestination,
          callerTotalBookings,
          assistantTranscriptIndex: null,
          transcriptFlushTimer: null,
          isAdaSpeaking: false,
          echoGuardUntil: 0,
          callEnded: false
        };
        
        console.log(`[${callId}] 🌐 Language set to: ${state.language}`);

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

      if (message.type === "audio" && openaiConnected && openaiWs && state) {
        // ECHO GUARD: Skip audio while Ada is speaking or in guard window
        if (state.isAdaSpeaking || Date.now() < state.echoGuardUntil) {
          return;
        }
        
        // Bridge sends base64-encoded 8kHz µ-law audio via JSON
        // Need to decode and convert to 24kHz PCM16 for OpenAI Whisper
        const binaryStr = atob(message.audio);
        const audioData = new Uint8Array(binaryStr.length);
        for (let i = 0; i < binaryStr.length; i++) {
          audioData[i] = binaryStr.charCodeAt(i);
        }
        
        // Step 1: Decode µ-law to 16-bit PCM (8kHz)
        const pcm16_8k = new Int16Array(audioData.length);
        for (let i = 0; i < audioData.length; i++) {
          const ulaw = ~audioData[i] & 0xFF;
          const sign = (ulaw & 0x80) ? -1 : 1;
          const exponent = (ulaw >> 4) & 0x07;
          const mantissa = ulaw & 0x0F;
          let sample = ((mantissa << 3) + 0x84) << exponent;
          sample -= 0x84;
          pcm16_8k[i] = sign * sample;
        }
        
        // Step 2: Upsample 8kHz -> 24kHz (3x linear interpolation)
        const pcm16_24k = new Int16Array(pcm16_8k.length * 3);
        for (let i = 0; i < pcm16_8k.length - 1; i++) {
          const s0 = pcm16_8k[i];
          const s1 = pcm16_8k[i + 1];
          pcm16_24k[i * 3] = s0;
          pcm16_24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
          pcm16_24k[i * 3 + 2] = Math.round(s0 + (s1 - s0) * 2 / 3);
        }
        // Handle last sample
        const lastIdx = pcm16_8k.length - 1;
        pcm16_24k[lastIdx * 3] = pcm16_8k[lastIdx];
        pcm16_24k[lastIdx * 3 + 1] = pcm16_8k[lastIdx];
        pcm16_24k[lastIdx * 3 + 2] = pcm16_8k[lastIdx];
        
        // Step 3: Convert to base64
        const bytes = new Uint8Array(pcm16_24k.buffer);
        let binary = "";
        for (let i = 0; i < bytes.length; i++) {
          binary += String.fromCharCode(bytes[i]);
        }
        const base64Audio = btoa(binary);
        
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: base64Audio
        }));
      }

    } catch (e) {
      console.error("[simple] Message error:", e);
    }
  };

  socket.onclose = () => {
    console.log(`[${state?.callId || "unknown"}] Client disconnected`);
    // Final flush on disconnect to capture any remaining transcripts
    if (state) {
      immediateFlush(state);
    }
    openaiWs?.close();
  };

  socket.onerror = (err) => {
    console.error("[simple] Socket error:", err);
  };

  return response;
});
