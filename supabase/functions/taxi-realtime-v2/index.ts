import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

// VERSION: Spoken at start of call for identification
const VERSION = "V2 1.3";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY") || "";
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";

const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === PASSENGER NUMBER TO WORD (for natural TTS) ===
const PASSENGER_WORDS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

function passengersToWord(num: number): string {
  return PASSENGER_WORDS[num] || String(num);
}

// === HOUSE NUMBER PARSING ===
const SPOKEN_NUMBERS: Record<string, string> = {
  "zero": "0", "oh": "0", "o": "0",
  "one": "1", "won": "1",
  "two": "2", "to": "2", "too": "2",
  "three": "3", "tree": "3", "free": "3",
  "four": "4", "for": "4", "fore": "4",
  "five": "5",
  "six": "6", "sex": "6",
  "seven": "7",
  "eight": "8", "ate": "8",
  "nine": "9", "niner": "9",
  "ten": "10",
  "eleven": "11",
  "twelve": "12",
  "thirteen": "13",
  "fourteen": "14",
  "fifteen": "15",
  "sixteen": "16",
  "seventeen": "17",
  "eighteen": "18",
  "nineteen": "19",
  "twenty": "20",
  "thirty": "30",
  "forty": "40",
  "fifty": "50",
  "sixty": "60",
  "seventy": "70",
  "eighty": "80",
  "ninety": "90",
  "hundred": "00",
};

function parseSpokenHouseNumber(text: string): string {
  const words = text.toLowerCase().split(/\s+/);
  let result = "";
  let i = 0;
  
  while (i < words.length) {
    const word = words[i];
    
    if (/^[a-z]$/.test(word) && result.length > 0) {
      result += word.toUpperCase();
      i++;
      continue;
    }
    
    if (/^\d+$/.test(word)) {
      result += word;
      i++;
      continue;
    }
    
    const num = SPOKEN_NUMBERS[word];
    if (num) {
      if (num.length === 2 && num.endsWith("0") && i + 1 < words.length) {
        const nextNum = SPOKEN_NUMBERS[words[i + 1]];
        if (nextNum && nextNum.length === 1) {
          result += String(parseInt(num) + parseInt(nextNum));
          i += 2;
          continue;
        }
      }
      result += num;
      i++;
      continue;
    }
    
    break;
  }
  
  const remaining = words.slice(i).join(" ");
  return result ? `${result} ${remaining}`.trim() : text;
}

function applyAddressCorrections(text: string): string {
  let corrected = text;
  
  // Join separated alphanumeric suffixes: "52 A" → "52A"
  corrected = corrected.replace(/(\d+)\s+([A-Za-z])(?=\s|$)/g, (_, num, letter) => `${num}${letter.toUpperCase()}`);
  
  // Fix hyphenated numbers: "52-8" → "528"
  corrected = corrected.replace(/(\d+)-(\d)(?=\s|$)/g, "$1$2");
  
  // Parse compound spoken numbers at start of address
  const addressMatch = corrected.match(/^([\w\s]+?)\s+(road|street|avenue|lane|drive|close|way|crescent|place|court|grove|gardens|terrace|walk|hill|rise|view|park|green|square|mews)/i);
  if (addressMatch) {
    const [, prefix, roadType] = addressMatch;
    const parsedPrefix = parseSpokenHouseNumber(prefix);
    if (parsedPrefix !== prefix) {
      corrected = parsedPrefix + " " + roadType + corrected.slice(addressMatch[0].length);
    }
  }
  
  return corrected;
}

// === TIME NORMALIZATION HELPERS ===
function normalizeTime(text: string): string {
  const lower = text.toLowerCase().trim();
  
  // ASAP patterns
  if (/\b(now|asap|immediately|straight away|right now|as soon as possible)\b/i.test(lower)) {
    return "ASAP";
  }
  
  // Keep other time expressions as-is for Ada to speak naturally
  return text;
}

// === STEP TRACKING ===
type BookingStep = "greeting" | "pickup" | "destination" | "passengers" | "time" | "summary" | "quote" | "confirmed";

function detectStepFromTranscript(text: string, currentStep: BookingStep): { 
  isAddress: boolean; 
  isPassengerCount: boolean; 
  isTime: boolean; 
  isConfirmation: boolean; 
  isCorrection: boolean;
  isLuggage: boolean;
  isSpecialRequest: boolean;
} {
  const lower = text.toLowerCase();
  
  // Correction detection
  const isCorrection = /\b(actually|no wait|change|i meant|not .+, it's|sorry,? it's|let me correct|amend|update|modify|the pickup is|pickup is|pickup should be|destination is|destination should be|to amend)\b/i.test(text);
  
  // Address patterns
  const addressPatterns = /\b(road|street|avenue|lane|drive|close|way|crescent|place|court|grove|gardens|terrace|walk|hill|rise|view|park|green|square|mews|station|airport|hospital|hotel|pub|supermarket|tesco|asda|sainsbury|morrisons|aldi|lidl|waitrose|mcdonald|costa|starbucks)\b/i;
  const hasHouseNumber = /\b\d+[a-z]?\b/i.test(text);
  const isAddress = addressPatterns.test(text) || (hasHouseNumber && text.length > 5);
  
  // Passenger count patterns
  const passengerPatterns = /^(one|two|three|four|five|six|seven|eight|nine|ten|1|2|3|4|5|6|7|8|9|10|just me|myself|alone|couple|to|too|tree|free|for|fore)$/i;
  const isPassengerCount = passengerPatterns.test(lower.trim()) || /^\d{1,2}$/.test(lower.trim());
  
  // Time patterns
  const timePatterns = /\b(now|asap|immediately|straight away|right now|in \d+ minutes?|at \d|half past|quarter|o'clock|morning|afternoon|evening|tonight|tomorrow|later)\b/i;
  const isTime = timePatterns.test(text);
  
  // Confirmation patterns
  const yesPatterns = /\b(yes|yeah|yep|yup|correct|right|exactly|perfect|sure|ok|okay|book it|go ahead|confirm|that's right|sounds good)\b/i;
  const noPatterns = /\b(no|nope|wrong|incorrect|change|wait|hold on|actually)\b/i;
  const isConfirmation = (yesPatterns.test(text) || noPatterns.test(text)) && currentStep === "summary";
  
  // Luggage patterns (from experimental code)
  const luggagePatterns = /\b(luggage|bags?|suitcases?|cases?|holdall|backpack|rucksack)\b/i;
  const isLuggage = luggagePatterns.test(text);
  
  // Special request patterns (from experimental code)
  const specialRequestPatterns = /\b(driver \d+|wheelchair|lady driver|ring me|call me when|hurry|asap|dog|pet|child seat|baby seat)\b/i;
  const isSpecialRequest = specialRequestPatterns.test(text);
  
  return { isAddress, isPassengerCount, isTime, isConfirmation, isCorrection, isLuggage, isSpecialRequest };
}

function parsePassengers(text: string): number | null {
  const lower = text.toLowerCase().trim();
  
  // Direct digit extraction first
  const digitMatch = lower.match(/\b(\d{1,2})\b/);
  if (digitMatch) {
    const num = parseInt(digitMatch[1]);
    if (num >= 1 && num <= 10) return num;
  }
  
  const numberWords: Record<string, number> = {
    "one": 1, "won": 1, "just me": 1, "myself": 1, "alone": 1,
    "two": 2, "to": 2, "too": 2, "couple": 2,
    "three": 3, "tree": 3, "free": 3,
    "four": 4, "for": 4, "fore": 4,
    "five": 5, "six": 6, "seven": 7, "eight": 8, "ate": 8,
    "nine": 9, "ten": 10
  };
  
  if (numberWords[lower]) return numberWords[lower];
  
  for (const [word, num] of Object.entries(numberWords)) {
    const regex = new RegExp(`\\b${word}\\b`, 'i');
    if (regex.test(lower)) return num;
  }
  
  return null;
}

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
"Version ${VERSION}. Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"

# BOOKING FLOW (Ask ONE at a time, in order - DO NOT SKIP STEPS)
1. Get pickup location → then ask for destination
2. Get destination → then ask for passengers  
3. Get number of passengers → then ask for time
4. Get pickup time (default: now/ASAP) → then give summary
5. Summarize booking and ask for confirmation
6. If confirmed, call the book_taxi tool with action="request_quote" and say "One moment please"
7. Wait for the fare quote, then present it to the user
8. If user accepts, call book_taxi with action="confirmed", thank them and call end_call

# CRITICAL: NEVER SKIP STEPS
- After pickup, you MUST ask for destination
- After destination, you MUST ask for passengers
- After passengers, you MUST ask for time
- Follow system hints about the current step

# HOUSE NUMBERS (CRITICAL)
- Callers may say house numbers as compound words: "twelve fourteen" = 1214, "twenty three" = 23
- Letter suffixes may be spoken separately: "fifty two A" = 52A
- NEVER alter or paraphrase house numbers - use them EXACTLY as understood
- "1214A Warwick Road" is a valid address - the number is "twelve fourteen A"

# PRONUNCIATION (TTS CRITICAL)
When speaking addresses back, say house numbers naturally:
- "1214" say as "twelve fourteen" NOT "one thousand two hundred fourteen"
- "1214A" say as "twelve fourteen A"
- "523" say as "five twenty-three" or "five two three"
- "52A" say as "fifty-two A"
- Always say letter suffixes clearly as separate letters: A, B, C

When speaking passenger counts, use words:
- "1" say as "one passenger"
- "6" say as "six passengers"

# PASSENGERS (ANTI-STUCK RULE)
- Accept digits (1-9) or number words (one, two, three, four, five, six, seven, eight, nine, ten)
- Also accept homophones: "to/too" → two, "for" → four, "tree" → three
- If something sounds like an address while asking for passengers, repeat: "How many passengers?"

# CORRECTIONS & CHANGES (CRITICAL)
When the caller wants to change or correct something they said:
- Listen for: "actually", "no wait", "change", "I meant", "not X, it's Y", "sorry, it's", "let me correct"
- IMMEDIATELY update your understanding with the new information
- Acknowledge briefly: "Updated to [new value]." then continue the flow
- If they correct during the summary, say "Let me update that" and give a NEW summary with the corrected info
- NEVER ignore corrections - always act on them

# SPECIAL REQUESTS & LUGGAGE
If user mentions luggage, wheelchair, specific driver, or other requests:
- Acknowledge briefly
- Continue with booking flow
- Include in final summary

# RULES
- Do NOT say "Got it" or "Great" before asking the next question
- Do NOT repeat or confirm individual answers mid-flow
- After each answer, immediately ask the NEXT question
- Only summarize at the end before confirmation
- Accept business names and landmarks as valid addresses
- NEVER ask for house numbers or postcodes - accept addresses as spoken
`;

// === TOOLS ===
const TOOLS = [
  {
    type: "function",
    name: "book_taxi",
    description: "Request a fare quote or confirm a booking. Use action='request_quote' first, then 'confirmed' after user accepts.",
    parameters: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["request_quote", "confirmed"] },
        pickup: { type: "string", description: "Full pickup address EXACTLY as spoken by user" },
        destination: { type: "string", description: "Full destination address EXACTLY as spoken by user" },
        passengers: { type: "integer", minimum: 1 },
        time: { type: "string", description: "When taxi is needed" },
        luggage: { type: "string", description: "Any luggage mentioned" },
        special_requests: { type: "string", description: "Any special requests" }
      },
      required: ["action"]
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "End the call after saying goodbye",
    parameters: { type: "object", properties: {} }
  }
];

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

function ulawToPcm16(ulaw: Uint8Array): Int16Array {
  const out = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    const u = (~ulaw[i]) & 0xff;
    const sign = u & 0x80;
    const exponent = (u >> 4) & 0x07;
    const mantissa = u & 0x0f;
    let sample = ((mantissa << 3) + 0x84) << exponent;
    sample -= 0x84;
    out[i] = sign ? -sample : sample;
  }
  return out;
}

function int16ToBytes(samples: Int16Array): Uint8Array {
  return new Uint8Array(samples.buffer, samples.byteOffset, samples.byteLength);
}

function arrayBufferToBase64(buffer: Uint8Array): string {
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < buffer.length; i += chunkSize) {
    const chunk = buffer.subarray(i, Math.min(i + chunkSize, buffer.length));
    for (let j = 0; j < chunk.length; j++) {
      binary += String.fromCharCode(chunk[j]);
    }
  }
  return btoa(binary);
}

// === DISPATCH WEBHOOK ===
async function sendDispatchWebhook(
  callId: string,
  callerPhone: string,
  action: string,
  booking: { pickup?: string; destination?: string; passengers?: number; time?: string; luggage?: string; special_requests?: string },
  log: (msg: string) => void
): Promise<{ success: boolean; fare?: string; eta?: string; booking_ref?: string }> {
  if (!DISPATCH_WEBHOOK_URL) {
    log("⚠️ No dispatch webhook, using mock");
    return { success: true, fare: "£12.50", eta: "6 minutes", booking_ref: `DEMO-${Date.now()}` };
  }

  try {
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        job_id: crypto.randomUUID(),
        call_id: callId,
        caller_phone: callerPhone.replace(/^\+/, ""),
        action,
        pickup: booking.pickup || "",
        destination: booking.destination || "",
        passengers: booking.passengers || 1,
        pickup_time: booking.time || "ASAP",
        luggage: booking.luggage || "",
        special_requests: booking.special_requests || "",
        source: "taxi-realtime-v2"
      })
    });

    if (!response.ok) {
      log(`❌ Dispatch failed: ${response.status}`);
      return { success: false };
    }

    const result = await response.json();
    return { success: true, fare: result.fare || result.estimated_fare, eta: result.eta, booking_ref: result.booking_ref };
  } catch (error) {
    log(`❌ Dispatch error: ${error}`);
    return { success: false };
  }
}

// === FALLBACK QUOTE ===
const FALLBACK_TIMEOUT_MS = 4000;
const FALLBACK_QUOTE = { fare: "£12.50", eta: "6 minutes" };

// === MAIN HANDLER ===
serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  const url = new URL(req.url);
  if (url.pathname.endsWith("/health") || req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    if (url.pathname.endsWith("/health")) {
      return new Response(JSON.stringify({ status: "ok", version: "v2-hybrid" }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" }
      });
    }
    return new Response("Expected WebSocket", { status: 426, headers: corsHeaders });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;
  let callId = url.searchParams.get("call_id") || `call-${Date.now()}`;
  let callerPhone = url.searchParams.get("caller_phone") || "";
  
  // State
  const booking: { pickup?: string; destination?: string; passengers?: number; time?: string; luggage?: string; special_requests?: string } = {};
  const transcripts: Array<{ role: string; text: string; timestamp: string }> = [];
  const MAX_TRANSCRIPTS = 50; // Prevent unbounded growth
  let fallbackTimer: ReturnType<typeof setTimeout> | null = null;
  let endCallTimer: ReturnType<typeof setTimeout> | null = null;
  let sessionTimer: ReturnType<typeof setTimeout> | null = null;
  let quoteDelivered = false;
  let inboundFormat: "ulaw" | "slin" | null = null;
  let currentStep: BookingStep = "greeting";
  let isCleanedUp = false;
  
  // Track what the user actually said (for hallucination prevention)
  const userTruth: { pickup?: string; destination?: string; passengers?: number; time?: string } = {};

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

  const cleanup = () => {
    if (isCleanedUp) return;
    isCleanedUp = true;
    
    // Clear all timers
    if (fallbackTimer) { clearTimeout(fallbackTimer); fallbackTimer = null; }
    if (endCallTimer) { clearTimeout(endCallTimer); endCallTimer = null; }
    if (sessionTimer) { clearTimeout(sessionTimer); sessionTimer = null; }
    
    // Close OpenAI WebSocket and clear handlers
    if (openaiWs) {
      openaiWs.onopen = null;
      openaiWs.onmessage = null;
      openaiWs.onerror = null;
      openaiWs.onclose = null;
      if (openaiWs.readyState === WebSocket.OPEN || openaiWs.readyState === WebSocket.CONNECTING) {
        openaiWs.close();
      }
      openaiWs = null;
    }
    
    // Clear transcript array
    transcripts.length = 0;
    
    log("🧹 Cleanup complete");
  };
  
  // Session watchdog - max 5 minutes per call
  const MAX_SESSION_MS = 5 * 60 * 1000;
  sessionTimer = setTimeout(() => {
    log("⏰ Session timeout reached");
    cleanup();
    if (socket.readyState === WebSocket.OPEN) socket.close(1000, "Session timeout");
  }, MAX_SESSION_MS);

  // Tool handler
  const handleToolCall = async (name: string, args: Record<string, unknown>, toolCallId: string) => {
    log(`🔧 Tool: ${name}(${JSON.stringify(args)})`);

    if (name === "book_taxi") {
      const action = String(args.action || "request_quote");
      
      // Use userTruth values preferentially (prevent AI hallucinations)
      if (args.pickup) booking.pickup = userTruth.pickup || String(args.pickup);
      if (args.destination) booking.destination = userTruth.destination || String(args.destination);
      if (args.passengers) booking.passengers = userTruth.passengers || Number(args.passengers);
      if (args.time) booking.time = String(args.time);
      if (args.luggage) booking.luggage = String(args.luggage);
      if (args.special_requests) booking.special_requests = String(args.special_requests);

      if (action === "request_quote") {
        // Start fallback timer
        fallbackTimer = setTimeout(() => {
          if (!quoteDelivered && openaiWs?.readyState === WebSocket.OPEN) {
            log(`⏱️ Fallback quote after ${FALLBACK_TIMEOUT_MS}ms`);
            quoteDelivered = true;
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: { type: "message", role: "system", content: [{ type: "input_text", text: `[QUOTE RECEIVED] Fare: ${FALLBACK_QUOTE.fare}, ETA: ${FALLBACK_QUOTE.eta}. Tell the customer and ask if they'd like to proceed.` }] }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
          }
        }, FALLBACK_TIMEOUT_MS);

        // Try real dispatch
        const result = await sendDispatchWebhook(callId, callerPhone, "request_quote", booking, log);
        
        if (result.success && result.fare && !quoteDelivered) {
          if (fallbackTimer) clearTimeout(fallbackTimer);
          quoteDelivered = true;
          
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: { type: "function_call_output", call_id: toolCallId, output: JSON.stringify({ fare: result.fare, eta: result.eta }) }
          }));
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
          return;
        }
        
        // Acknowledge (fallback will inject quote)
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: { type: "function_call_output", call_id: toolCallId, output: JSON.stringify({ status: "checking" }) }
        }));
        
      } else if (action === "confirmed") {
        await sendDispatchWebhook(callId, callerPhone, "confirmed", booking, log);
        await supabase.from("bookings").insert({
          call_id: callId,
          caller_phone: callerPhone,
          pickup: booking.pickup,
          destination: booking.destination,
          passengers: booking.passengers || 1,
          status: "confirmed"
        });
        
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: { type: "function_call_output", call_id: toolCallId, output: JSON.stringify({ status: "confirmed" }) }
        }));
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }
      
    } else if (name === "end_call") {
      log("👋 End call");
      await supabase.from("live_calls").update({ status: "completed", ended_at: new Date().toISOString() }).eq("call_id", callId);
      
      openaiWs?.send(JSON.stringify({
        type: "conversation.item.create",
        item: { type: "function_call_output", call_id: toolCallId, output: JSON.stringify({ status: "ending" }) }
      }));
      
      // Store timer reference so it can be cleared during cleanup
      endCallTimer = setTimeout(() => { 
        if (socket.readyState === WebSocket.OPEN) socket.close(1000, "Call ended"); 
        cleanup(); 
      }, 3000);
    }
  };

  // Connect to OpenAI
  const connectOpenAI = () => {
    log("🔌 Connecting to OpenAI...");
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => log("✅ OpenAI connected");

    openaiWs.onmessage = async (event) => {
      const msg = JSON.parse(event.data);

      if (msg.type === "session.created") {
        log("📋 Configuring session...");
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            instructions: SYSTEM_PROMPT,
            voice: "shimmer",
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { model: "whisper-1" },
            turn_detection: { type: "server_vad", threshold: 0.5, prefix_padding_ms: 300, silence_duration_ms: 800 },
            tools: TOOLS,
            tool_choice: "auto"
          }
        }));
      }

      if (msg.type === "session.updated") {
        log("🎤 Triggering greeting...");
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }

      // Forward audio
      if (msg.type === "response.audio.delta" && msg.delta) {
        const bytes = Uint8Array.from(atob(msg.delta), c => c.charCodeAt(0));
        if (socket.readyState === WebSocket.OPEN) socket.send(bytes.buffer);
      }

      // Log transcripts with limit
      if (msg.type === "response.audio_transcript.done" && msg.transcript) {
        log(`🗣️ Ada: ${msg.transcript}`);
        if (transcripts.length >= MAX_TRANSCRIPTS) transcripts.shift(); // Remove oldest
        transcripts.push({ role: "assistant", text: msg.transcript, timestamp: new Date().toISOString() });
        supabase.from("live_calls").update({ transcripts, pickup: booking.pickup, destination: booking.destination, passengers: booking.passengers, pickup_time: booking.time, booking_step: currentStep }).eq("call_id", callId);
      }
      
      if (msg.type === "conversation.item.input_audio_transcription.completed" && msg.transcript) {
        const rawTranscript = msg.transcript;
        const correctedTranscript = applyAddressCorrections(rawTranscript);
        
        if (correctedTranscript !== rawTranscript) {
          log(`👤 User: ${rawTranscript} → ${correctedTranscript}`);
        } else {
          log(`👤 User: ${rawTranscript}`);
        }
        
        if (transcripts.length >= MAX_TRANSCRIPTS) transcripts.shift(); // Remove oldest
        transcripts.push({ role: "user", text: correctedTranscript, timestamp: new Date().toISOString() });
        
        // Detect what the user provided
        const detection = detectStepFromTranscript(correctedTranscript, currentStep);
        log(`📊 Step: ${currentStep}, Detection: ${JSON.stringify(detection)}`);
        
        // Handle luggage mentions (capture but don't change flow)
        if (detection.isLuggage) {
          const luggageMatch = correctedTranscript.match(/(\d+)?\s*(luggage|bags?|suitcases?|cases?)/i);
          if (luggageMatch) {
            booking.luggage = luggageMatch[0];
            log(`🧳 Luggage noted: ${booking.luggage}`);
          }
        }
        
        // Handle special requests (capture but don't change flow)
        if (detection.isSpecialRequest) {
          booking.special_requests = (booking.special_requests ? booking.special_requests + "; " : "") + correctedTranscript;
          log(`📝 Special request: ${correctedTranscript}`);
        }
        
        // Handle corrections - more permissive detection
        // Allow updates at any step, not just when explicit correction words are used
        const isPickupIndicator = /\b(pickup|pick up|from|picked up from|collect from|picking up)\b/i.test(correctedTranscript);
        const isDestinationIndicator = /\b(destination|to|going to|drop off|dropoff|drop me|take me|heading to)\b/i.test(correctedTranscript);
        
        // If user explicitly mentions "pickup" or "destination" with an address, treat as update
        // Also allow updates during summary step when data already exists
        const isExplicitUpdate = detection.isAddress && (isPickupIndicator || isDestinationIndicator);
        const isExplicitCorrection = detection.isCorrection && detection.isAddress;
        const isInSummaryWithData = currentStep === "summary" && detection.isAddress;
        
        if (isExplicitCorrection || isExplicitUpdate || isInSummaryWithData) {
          log(`🔄 Update detected: correction=${detection.isCorrection}, explicit=${isExplicitUpdate}, summary=${isInSummaryWithData}`);
          
          // Extract the address - be more careful about what we remove
          // Only remove explicit correction/direction words, keep the actual address
          let addressPart = correctedTranscript;
          
          // Step 1: Remove only the explicit direction/correction prefixes
          const prefixPatterns = [
            /^(actually|no wait|change|i meant|sorry|let me correct|amend|update|modify)\s*/i,
            /^(the\s+)?(pickup|pick up|destination)\s+(is|should be)\s*/i,
            /^(pick up from|picked up from|collect from|drop off at|drop me at|take me to|heading to|going to)\s*/i,
            /^(from|to)\s+/i
          ];
          
          for (const pattern of prefixPatterns) {
            addressPart = addressPart.replace(pattern, '');
          }
          
          // Step 2: Clean up punctuation and whitespace
          addressPart = addressPart.replace(/^[\s,]+|[\s,]+$/g, '').trim();
          
          // Only update if we have a meaningful address left (not empty after cleaning)
          if (addressPart.length > 2) {
            if (isDestinationIndicator && !isPickupIndicator) {
              log(`✅ Destination OVERWRITTEN: "${booking.destination}" → "${addressPart}"`);
              userTruth.destination = addressPart;
              booking.destination = addressPart;
            } else {
              log(`✅ Pickup OVERWRITTEN: "${booking.pickup}" → "${addressPart}"`);
              userTruth.pickup = addressPart;
              booking.pickup = addressPart;
            }
            
            // IMMEDIATELY persist the correction to database
            await supabase.from("live_calls").update({ 
              pickup: booking.pickup, 
              destination: booking.destination, 
              passengers: booking.passengers,
              pickup_time: booking.time,
              booking_step: currentStep,
              updated_at: new Date().toISOString()
            }).eq("call_id", callId);
            log(`💾 Correction persisted to DB: pickup="${booking.pickup}", destination="${booking.destination}", time="${booking.time}"`);
            
            // Always go back to summary after an update if we have all fields
            if (booking.pickup && booking.destination && booking.passengers && booking.time) {
              currentStep = "summary";
              const passengerWord = passengersToWord(booking.passengers);
              
              if (openaiWs?.readyState === WebSocket.OPEN) {
                openaiWs.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: { type: "message", role: "system", content: [{ type: "input_text", text: `[CORRECTION APPLIED] Updated. Give NEW summary with EXACTLY these values:
- Pickup: "${booking.pickup}"
- Destination: "${booking.destination}"
- Passengers: ${passengerWord}
- Time: "${booking.time}"
Say: "Let me update that. So that's from ${booking.pickup} to ${booking.destination}, ${passengerWord} passenger${booking.passengers !== 1 ? 's' : ''}, pickup ${booking.time}. Is that correct?"` }] }
                }));
              }
            } else {
              // Continue collecting missing info
              const nextStep = !booking.pickup ? "pickup" : !booking.destination ? "destination" : !booking.passengers ? "passengers" : "time";
              const nextQuestion = nextStep === "pickup" ? "Where would you like to be picked up from?" :
                                   nextStep === "destination" ? "And where would you like to go?" :
                                   nextStep === "passengers" ? "How many passengers?" : "When would you like to be picked up?";
              
              if (openaiWs?.readyState === WebSocket.OPEN) {
                openaiWs.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: { type: "message", role: "system", content: [{ type: "input_text", text: `[UPDATED] Got it. Now ask: "${nextQuestion}"` }] }
                }));
              }
            }
          } else {
            log(`⚠️ Address extraction failed, addressPart too short: "${addressPart}"`);
          }
        }
        else if (detection.isCorrection && !detection.isAddress) {
          log(`🔄 Correction intent (no address yet)`);
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: { type: "message", role: "system", content: [{ type: "input_text", text: `[CORRECTION] The user wants to change something. Ask: "What would you like to change?"` }] }
            }));
          }
        }
        // Normal step progression
        else if (currentStep === "greeting" || currentStep === "pickup") {
          if (detection.isAddress) {
            userTruth.pickup = correctedTranscript;
            booking.pickup = correctedTranscript;
            currentStep = "destination";
            log(`✅ Pickup saved: ${correctedTranscript}, next: destination`);
            
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: { type: "message", role: "system", content: [{ type: "input_text", text: `[STEP] Pickup received: "${correctedTranscript}". NOW ASK: "And where would you like to go?" - Do NOT ask about passengers yet.` }] }
              }));
            }
          }
        }
        else if (currentStep === "destination") {
          if (detection.isAddress) {
            userTruth.destination = correctedTranscript;
            booking.destination = correctedTranscript;
            currentStep = "passengers";
            log(`✅ Destination saved: ${correctedTranscript}, next: passengers`);
            
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: { type: "message", role: "system", content: [{ type: "input_text", text: `[STEP] Destination received: "${correctedTranscript}". NOW ASK: "How many passengers will be travelling?"` }] }
              }));
            }
          }
        }
        else if (currentStep === "passengers") {
          const parsed = parsePassengers(correctedTranscript);
          if (parsed) {
            userTruth.passengers = parsed;
            booking.passengers = parsed;
            currentStep = "time";
            const passengerWord = passengersToWord(parsed);
            log(`✅ Passengers saved: ${parsed} (${passengerWord}), next: time`);
            
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: { type: "message", role: "system", content: [{ type: "input_text", text: `[STEP] ${passengerWord} passenger(s). NOW ASK: "When would you like to be picked up?" (Accept "now" or "ASAP")` }] }
              }));
            }
          } else if (detection.isAddress) {
            log(`⚠️ Address given during passengers step, repeating question`);
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: { type: "message", role: "system", content: [{ type: "input_text", text: `[REPEAT] That sounded like an address. Ask again: "How many passengers will be travelling?"` }] }
              }));
            }
          }
        }
        else if (currentStep === "time") {
          const timeValue = normalizeTime(correctedTranscript);
          userTruth.time = timeValue;
          booking.time = timeValue;
          currentStep = "summary";
          log(`✅ Time saved: ${timeValue}, next: summary`);
          
          const passengerWord = passengersToWord(booking.passengers || 1);
          
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: { type: "message", role: "system", content: [{ type: "input_text", text: `[STEP] Time: ${timeValue}. NOW GIVE SUMMARY using EXACTLY these values:
- Pickup: "${booking.pickup}"
- Destination: "${booking.destination}"  
- Passengers: ${passengerWord} (say "${passengerWord}" NOT "${booking.passengers}")
- Time: "${booking.time}"
${booking.luggage ? `- Luggage: ${booking.luggage}` : ''}
Say: "So that's from [pickup] to [destination], for ${passengerWord} passenger${booking.passengers !== 1 ? 's' : ''}, pickup ${booking.time}. Is that correct?"` }] }
            }));
          }
        }
        else if (currentStep === "summary") {
          const yesPatterns = /\b(yes|yeah|yep|yup|correct|right|exactly|perfect|sure|ok|okay|book it|go ahead|confirm|that's right|sounds good)\b/i;
          const noPatterns = /\b(no|nope|wrong|incorrect|change|wait|hold on)\b/i;
          
          if (yesPatterns.test(correctedTranscript) && !noPatterns.test(correctedTranscript)) {
            log(`✅ User confirmed booking`);
            currentStep = "quote";
            
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: { type: "message", role: "system", content: [{ type: "input_text", text: `[CONFIRMED] User said YES. Say "One moment please" and call book_taxi with action="request_quote", pickup="${userTruth.pickup}", destination="${userTruth.destination}", passengers=${userTruth.passengers}, time="${userTruth.time}"` }] }
              }));
            }
          } else if (noPatterns.test(correctedTranscript)) {
            log(`🔄 User wants to change something`);
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: { type: "message", role: "system", content: [{ type: "input_text", text: `[CHANGE] User wants to change something. Ask: "What would you like to change?"` }] }
              }));
            }
          }
        }
        
        // Update database with all booking fields
        await supabase.from("live_calls").update({ 
          transcripts, 
          pickup: booking.pickup, 
          destination: booking.destination, 
          passengers: booking.passengers,
          pickup_time: booking.time,
          booking_step: currentStep,
          updated_at: new Date().toISOString()
        }).eq("call_id", callId);
        log(`💾 DB updated: pickup="${booking.pickup}", dest="${booking.destination}", pax=${booking.passengers}, time="${booking.time}", step=${currentStep}`);
      }

      // Handle tool calls
      if (msg.type === "response.function_call_arguments.done") {
        try {
          await handleToolCall(msg.name, JSON.parse(msg.arguments || "{}"), msg.call_id);
        } catch (e) {
          log(`❌ Tool error: ${e}`);
        }
      }

      if (msg.type === "error") log(`❌ OpenAI: ${JSON.stringify(msg.error)}`);
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI disconnected");
  };

  // Bridge handlers
  socket.onopen = () => log("🚀 Bridge connected");

  socket.onmessage = async (event) => {
    // Binary audio
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState !== WebSocket.OPEN) return;
      
      const incoming = event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : event.data;
      
      if (!inboundFormat) {
        inboundFormat = incoming.byteLength <= 200 ? "ulaw" : "slin";
        log(`🎧 Audio format: ${inboundFormat}`);
      }
      
      let pcm24k: Uint8Array;
      if (inboundFormat === "ulaw") {
        const pcm8k = ulawToPcm16(incoming);
        pcm24k = pcm8kTo24k(int16ToBytes(pcm8k));
      } else {
        pcm24k = pcm8kTo24k(incoming);
      }
      
      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.append", audio: arrayBufferToBase64(pcm24k) }));
      return;
    }

    // JSON control
    try {
      const msg = JSON.parse(event.data);
      
      if (msg.type === "init") {
        callId = msg.call_id || callId;
        callerPhone = msg.phone || msg.caller || callerPhone;
        log(`📞 Init: ${callerPhone}`);
        
        await supabase.from("live_calls").upsert({ call_id: callId, caller_phone: callerPhone, status: "active", source: "v2-hybrid", transcripts: [] }, { onConflict: "call_id" });
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup");
        await supabase.from("live_calls").update({ status: "ended", ended_at: new Date().toISOString() }).eq("call_id", callId);
        cleanup();
      }
    } catch { /* ignore */ }
  };

  socket.onclose = () => { log("🔌 Bridge closed"); cleanup(); };
  socket.onerror = (e) => log(`🔴 Bridge error: ${e}`);

  return response;
});
