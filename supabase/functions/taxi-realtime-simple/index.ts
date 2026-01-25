import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === STT CORRECTIONS ===
const STT_CORRECTIONS: Record<string, string> = {
  "52-8": "52A", "52 8": "52A", "528": "52A", "52 a": "52A",
  "52-a": "52A", "fifty two a": "52A", "fifty-two a": "52A",
  "52 hey": "52A", "52 eh": "52A", "52 age": "52A",
  "7-8": "7A", "7 8": "7A", "78": "7A", "7 a": "7A",
  "seven a": "7A", "7-a": "7A",
  "david rohn": "David Road", "david rhone": "David Road",
  "roswell": "Russell", "russel": "Russell",
};

function applySTTCorrections(text: string): string {
  let corrected = text;
  for (const [wrong, right] of Object.entries(STT_CORRECTIONS)) {
    const regex = new RegExp(wrong, "gi");
    corrected = corrected.replace(regex, right);
  }
  corrected = corrected.replace(/(\d+)\s+([A-Za-z])(?=\s|$)/g, "$1$2");
  return corrected;
}

// === TOOLS DEFINITION ===
const TOOLS = [
  {
    type: "function",
    name: "book_taxi",
    description: "Book a taxi after the user confirms the booking summary. Use action='request_quote' to get fare estimate, action='confirmed' after user accepts the fare.",
    parameters: {
      type: "object",
      properties: {
        action: {
          type: "string",
          enum: ["request_quote", "confirmed"],
          description: "request_quote = get fare estimate, confirmed = finalize booking"
        },
        pickup: { type: "string", description: "Full pickup address exactly as spoken" },
        destination: { type: "string", description: "Full destination address exactly as spoken" },
        passengers: { type: "number", description: "Number of passengers (1-10)" },
        time: { type: "string", description: "Pickup time (e.g., 'now', '3pm', 'in 10 minutes')" }
      },
      required: ["action", "pickup", "destination", "passengers"]
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "End the call after booking is complete or if user wants to hang up",
    parameters: {
      type: "object",
      properties: {
        reason: { type: "string", enum: ["booking_complete", "user_cancelled", "user_hangup"] }
      },
      required: ["reason"]
    }
  }
];

// === SYSTEM PROMPT ===
const SYSTEM_PROMPT = `
# IDENTITY
You are ADA, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual.

# SPEECH PACING
- Speak at a SLOW, RELAXED pace
- Insert natural pauses between sentences
- Pronounce addresses clearly

# LANGUAGE
Respond in the same language the caller speaks.

# ONE QUESTION RULE
Ask ONLY ONE question per response. NEVER combine questions.

# GREETING (Say this FIRST when call starts)
"Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"

# BOOKING FLOW (Ask ONE at a time, in order)
1. Get pickup location
2. Get destination
3. Get number of passengers
4. Get pickup time (default: now/ASAP)
5. Summarize booking and ask for confirmation
6. When user confirms, say "One moment please" and call book_taxi(action="request_quote")
7. After receiving fare, tell user the fare and ask them to confirm
8. When user accepts fare, call book_taxi(action="confirmed")
9. Say "Your taxi is booked! You'll receive updates via WhatsApp. Have a safe journey!" then call end_call

# PASSENGERS (ANTI-STUCK RULE)
- When asking for passengers, say: "How many passengers will be travelling?"
- Accept digits or number words (one through ten)
- Accept homophones: "to/too" → two, "for" → four, "tree/free/the/there" → three
- If caller says something like an address, repeat: "How many passengers will be travelling?"

# ADDRESS INTERPRETATION
- "52-8" or "52 A" means "52A" (alphanumeric house number)
- Always preserve full house numbers including letter suffixes

# LOCAL EVENTS & DIRECTIONS
- Help with local events, attractions, and directions
- After sharing info, guide back: "Would you like me to book a taxi there?"

# CORRECTIONS
- Listen for: "actually", "no wait", "change", "I meant"
- Update immediately and acknowledge: "Updated to [new value]."

# CRITICAL RULES
- Do NOT quote fares until you receive them from book_taxi
- After user confirms summary, you MUST call book_taxi(action="request_quote")
- Only call book_taxi(action="confirmed") after user accepts the fare
`;

// === AUDIO HELPERS ===
function pcm8kTo24k(pcm8k: Uint8Array): Uint8Array {
  const samples8k = new Int16Array(pcm8k.buffer, pcm8k.byteOffset, Math.floor(pcm8k.byteLength / 2));
  const len24k = samples8k.length * 3;
  const samples24k = new Int16Array(len24k);

  for (let i = 0; i < samples8k.length - 1; i++) {
    const s0 = samples8k[i];
    const s1 = samples8k[i + 1];
    samples24k[i * 3] = s0;
    samples24k[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
    samples24k[i * 3 + 2] = Math.round(s0 + 2 * (s1 - s0) / 3);
  }

  const last = samples8k[samples8k.length - 1] || 0;
  samples24k[len24k - 3] = last;
  samples24k[len24k - 2] = last;
  samples24k[len24k - 1] = last;

  return new Uint8Array(samples24k.buffer);
}

function arrayBufferToBase64(buffer: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < buffer.length; i++) {
    binary += String.fromCharCode(buffer[i]);
  }
  return btoa(binary);
}

// === STEP DETECTION ===
type BookingStep = "pickup" | "destination" | "passengers" | "time" | "summary" | "awaiting_fare" | "awaiting_final" | "done" | "unknown";

function detectStepFromAdaTranscript(transcript: string): BookingStep {
  const lower = transcript.toLowerCase();
  if (/where would you like to be picked up|pickup (location|address)|pick you up/i.test(lower)) return "pickup";
  if (/where (would you like to go|are you going|is your destination)|heading to/i.test(lower)) return "destination";
  if (/how many (people|passengers)|travelling/i.test(lower)) return "passengers";
  if (/when would you like|pickup time|what time|now or later/i.test(lower)) return "time";
  if (/let me confirm|to confirm|summary|picking you up from/i.test(lower)) return "summary";
  if (/one moment|checking|getting.*fare/i.test(lower)) return "awaiting_fare";
  if (/taxi is booked|safe journey|whatsapp/i.test(lower)) return "done";
  return "unknown";
}

function getContextHintForStep(step: BookingStep): string | null {
  switch (step) {
    case "pickup":
      return "[CONTEXT: User is providing PICKUP ADDRESS. Listen for street names, house numbers, landmarks.]";
    case "destination":
      return "[CONTEXT: User is providing DESTINATION ADDRESS. Listen for street names, house numbers, landmarks.]";
    case "passengers":
      return "[CONTEXT: User is providing PASSENGER COUNT. 'tree/free/the/there' = 3, 'to/too' = 2, 'for' = 4. Accept 1-10.]";
    case "time":
      return "[CONTEXT: User is providing PICKUP TIME. 'now/asap' = immediately. Listen for times like '3pm'.]";
    case "summary":
      return "[CONTEXT: User is CONFIRMING booking. 'yes/yeah/correct' = confirmed. 'no/change' = needs correction.]";
    case "awaiting_final":
      return "[CONTEXT: User is confirming the FARE. 'yes/okay/that's fine' = accept fare.]";
    default:
      return null;
  }
}

// === WEBHOOK ===
async function sendDispatchWebhook(
  callId: string,
  action: string,
  booking: { pickup: string; destination: string; passengers: number; time?: string },
  log: (msg: string) => void
): Promise<{ success: boolean; fare?: string; eta?: string; error?: string }> {
  if (!DISPATCH_WEBHOOK_URL) {
    log("⚠️ No DISPATCH_WEBHOOK_URL configured, using mock response");
    // Mock response for demo
    return { success: true, fare: "£12.50", eta: "5 minutes" };
  }

  try {
    log(`📤 Sending webhook: ${action}`);
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        action,
        call_id: callId,
        pickup: booking.pickup,
        destination: booking.destination,
        passengers: booking.passengers,
        time: booking.time || "now"
      })
    });

    if (!response.ok) {
      throw new Error(`Webhook failed: ${response.status}`);
    }

    const data = await response.json();
    log(`📥 Webhook response: ${JSON.stringify(data)}`);
    return { success: true, fare: data.fare, eta: data.eta };
  } catch (error) {
    log(`❌ Webhook error: ${error}`);
    return { success: false, error: String(error) };
  }
}

// === MAIN HANDLER ===
serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426 });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;
  let callId = "unknown";
  let callerPhone = "unknown";
  
  // Booking state
  let bookingState = {
    pickup: "",
    destination: "",
    passengers: 1,
    time: "now",
    fare: "",
    eta: ""
  };
  
  // Step tracking
  let currentStep: BookingStep = "pickup";
  let stepAtSpeechStart: BookingStep = "pickup";
  let contextInjected = false;
  let pendingToolCallId = "";

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

  // Handle tool calls
  const handleToolCall = async (name: string, args: Record<string, unknown>, toolCallId: string) => {
    log(`🔧 Tool call: ${name}(${JSON.stringify(args)})`);
    
    if (name === "book_taxi") {
      const action = args.action as string;
      
      // Update booking state from tool args
      if (args.pickup) bookingState.pickup = args.pickup as string;
      if (args.destination) bookingState.destination = args.destination as string;
      if (args.passengers) bookingState.passengers = args.passengers as number;
      if (args.time) bookingState.time = args.time as string;
      
      if (action === "request_quote") {
        currentStep = "awaiting_fare";
        const result = await sendDispatchWebhook(callId, "request_quote", bookingState, log);
        
        if (result.success && result.fare) {
          bookingState.fare = result.fare;
          bookingState.eta = result.eta || "";
          
          // Send tool result back to OpenAI
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: toolCallId,
              output: JSON.stringify({
                status: "quote_received",
                fare: result.fare,
                eta: result.eta,
                message: `The fare is ${result.fare}. ETA is ${result.eta}. Ask the user to confirm.`
              })
            }
          }));
          
          currentStep = "awaiting_final";
        } else {
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: toolCallId,
              output: JSON.stringify({
                status: "error",
                message: "Unable to get fare estimate. Please try again."
              })
            }
          }));
        }
        
        // Trigger response
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
        
      } else if (action === "confirmed") {
        await sendDispatchWebhook(callId, "confirmed", bookingState, log);
        
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "function_call_output",
            call_id: toolCallId,
            output: JSON.stringify({
              status: "booking_confirmed",
              message: "Booking confirmed! Tell the user their taxi is booked and they'll receive WhatsApp updates."
            })
          }
        }));
        
        currentStep = "done";
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }
      
    } else if (name === "end_call") {
      log(`📞 End call requested: ${args.reason}`);
      
      openaiWs?.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "function_call_output",
          call_id: toolCallId,
          output: JSON.stringify({ status: "call_ending" })
        }
      }));
      
      // Send hangup to bridge after a short delay
      setTimeout(() => {
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(JSON.stringify({ type: "hangup", reason: args.reason }));
        }
      }, 2000);
    }
  };

  // Connect to OpenAI Realtime
  const connectOpenAI = () => {
    log("🔌 Connecting to OpenAI...");

    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("✅ OpenAI connected, configuring session...");
    };

    openaiWs.onmessage = async (event) => {
      const msg = JSON.parse(event.data);

      // Session created - configure with tools
      if (msg.type === "session.created") {
        log("📋 Session created, sending config with tools");
        
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            instructions: SYSTEM_PROMPT,
            voice: "shimmer",
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            turn_detection: {
              type: "server_vad",
              threshold: 0.3,
              prefix_padding_ms: 600,
              silence_duration_ms: 1000
            },
            tools: TOOLS,
            tool_choice: "auto"
          }
        }));
      }

      // Session configured - trigger greeting
      if (msg.type === "session.updated") {
        log("🎤 Session configured, triggering greeting");
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }

      // Track Ada's speech for step detection
      if (msg.type === "response.audio_transcript.done") {
        const adaText = msg.transcript || "";
        log(`🗣️ Ada: ${adaText}`);
        
        const detected = detectStepFromAdaTranscript(adaText);
        if (detected !== "unknown") {
          currentStep = detected;
          log(`📍 Step: ${currentStep}`);
        }
        contextInjected = false;
      }

      // Handle tool calls
      if (msg.type === "response.function_call_arguments.done") {
        try {
          const args = JSON.parse(msg.arguments || "{}");
          await handleToolCall(msg.name, args, msg.call_id);
        } catch (e) {
          log(`❌ Tool parse error: ${e}`);
        }
      }

      // User speech started - inject context
      if (msg.type === "input_audio_buffer.speech_started") {
        stepAtSpeechStart = currentStep;
        log(`🎙️ Speech started (step: ${stepAtSpeechStart})`);
        
        if (!contextInjected) {
          const hint = getContextHintForStep(stepAtSpeechStart);
          if (hint) {
            log(`💡 Context: ${hint}`);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{ type: "input_text", text: hint }]
              }
            }));
            contextInjected = true;
          }
        }
      }

      // Log user transcript
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const raw = msg.transcript;
        const corrected = applySTTCorrections(raw);
        if (raw !== corrected) {
          log(`👤 User: ${raw} → [STT FIX] ${corrected}`);
        } else {
          log(`👤 User: ${raw}`);
        }
      }

      // Forward audio to bridge
      if (msg.type === "response.audio.delta" && msg.delta) {
        const binaryStr = atob(msg.delta);
        const bytes = new Uint8Array(binaryStr.length);
        for (let i = 0; i < binaryStr.length; i++) {
          bytes[i] = binaryStr.charCodeAt(i);
        }
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(bytes.buffer);
        }
      }

      // Log errors
      if (msg.type === "error") {
        log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI disconnected");
  };

  // Bridge connection
  socket.onopen = () => log("🚀 Bridge connected");

  socket.onmessage = (event) => {
    // Binary audio from bridge
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState === WebSocket.OPEN) {
        const pcm8k = event.data instanceof ArrayBuffer
          ? new Uint8Array(event.data)
          : event.data;

        const pcm24k = pcm8kTo24k(pcm8k);
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: arrayBufferToBase64(pcm24k)
        }));
      }
      return;
    }

    // JSON control messages
    try {
      const msg = JSON.parse(event.data);
      
      if (msg.type === "init") {
        callId = msg.call_id || "unknown";
        callerPhone = msg.caller_phone || msg.caller || "unknown";
        log(`📞 Call initialized (caller: ${callerPhone})`);
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        openaiWs?.close();
      }
    } catch {
      // Ignore non-JSON
    }
  };

  socket.onclose = () => {
    log("🔌 Bridge disconnected");
    openaiWs?.close();
  };

  return response;
});
