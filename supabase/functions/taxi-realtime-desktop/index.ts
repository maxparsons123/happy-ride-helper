/**
 * taxi-realtime-desktop: Full Paired Mode for C# Desktop Bridge
 * 
 * This is a complete replica of taxi-realtime-paired, optimized for the C# SIP bridge.
 * It handles PCM16 @ 24kHz audio directly from the bridge (no resampling needed).
 * 
 * Use this endpoint for testing the desktop bridge without affecting production.
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
const OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17";
const VOICE = "shimmer";

// ---------------------------------------------------------------------------
// Phone Number to Language Mapping
// ---------------------------------------------------------------------------
const COUNTRY_CODE_TO_LANGUAGE: Record<string, string> = {
  "+44": "en", "+1": "en", "+61": "en", "+64": "en", "+353": "en",
  "+34": "es", "+52": "es", "+54": "es", "+56": "es", "+57": "es", "+51": "es",
  "+33": "fr", "+32": "fr", "+41": "fr",
  "+49": "de", "+43": "de",
  "+39": "it",
  "+351": "pt", "+55": "pt",
  "+48": "pl",
  "+40": "ro",
  "+31": "nl",
  "+966": "ar", "+971": "ar", "+20": "ar",
  "+91": "hi",
  "+92": "ur",
  "+86": "zh", "+852": "zh",
  "+81": "ja",
  "+82": "ko",
  "+90": "tr",
  "+7": "ru",
  "+30": "el",
};

function detectLanguageFromPhone(phone: string | null): string | null {
  if (!phone) return null;
  let cleaned = phone.replace(/\s+/g, "").replace(/-/g, "");
  if (cleaned.startsWith("00")) cleaned = "+" + cleaned.slice(2);
  if (/^\+0\d/.test(cleaned)) cleaned = "+" + cleaned.slice(2);
  if (!cleaned.startsWith("+")) return null;
  const sortedCodes = Object.keys(COUNTRY_CODE_TO_LANGUAGE).sort((a, b) => b.length - a.length);
  for (const code of sortedCodes) {
    if (cleaned.startsWith(code)) return COUNTRY_CODE_TO_LANGUAGE[code];
  }
  return null;
}

function normalizePhone(phone: string): string {
  return phone.replace(/\s+/g, "").replace(/-/g, "").replace(/^\+/, "");
}

// ---------------------------------------------------------------------------
// Audio helpers - DESKTOP OPTIMIZED
// The C# bridge sends PCM16 @ 24kHz directly, so minimal processing needed
// ---------------------------------------------------------------------------

type InboundAudioFormat = "ulaw" | "slin" | "slin16" | "pcm24k";

function ulawToPcm16(ulaw: Uint8Array): Int16Array {
  const out = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    const u = (~ulaw[i]) & 0xff;
    const sign = (u & 0x80) ? -1 : 1;
    const exponent = (u >> 4) & 0x07;
    const mantissa = u & 0x0f;
    let sample = ((mantissa << 3) + 0x84) << exponent;
    sample -= 0x84;
    out[i] = sign * sample;
  }
  return out;
}

function resamplePcm16To24k(pcm: Int16Array, inputSampleRate: number): Int16Array {
  if (inputSampleRate === 24000) return pcm;
  if (inputSampleRate === 16000) {
    const outLen = Math.floor((pcm.length * 3) / 2);
    const out = new Int16Array(outLen);
    for (let i = 0; i < outLen; i++) {
      const srcIdx = (i * 2) / 3;
      const idx0 = Math.floor(srcIdx);
      const idx1 = Math.min(idx0 + 1, pcm.length - 1);
      const frac = srcIdx - idx0;
      out[i] = Math.round(pcm[idx0] * (1 - frac) + pcm[idx1] * frac);
    }
    return out;
  }
  // 8kHz → 24kHz (3x linear interpolation)
  const out = new Int16Array(pcm.length * 3);
  for (let i = 0; i < pcm.length - 1; i++) {
    const s0 = pcm[i];
    const s1 = pcm[i + 1];
    out[i * 3] = s0;
    out[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
    out[i * 3 + 2] = Math.round(s0 + ((s1 - s0) * 2) / 3);
  }
  const lastIdx = Math.max(pcm.length - 1, 0);
  out[lastIdx * 3] = pcm[lastIdx] ?? 0;
  out[lastIdx * 3 + 1] = pcm[lastIdx] ?? 0;
  out[lastIdx * 3 + 2] = pcm[lastIdx] ?? 0;
  return out;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const chunk = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
    for (let j = 0; j < chunk.length; j++) binary += String.fromCharCode(chunk[j]);
  }
  return btoa(binary);
}

function pcm16ToBase64(pcm: Int16Array): string {
  const bytes = new Uint8Array(pcm.buffer, pcm.byteOffset, pcm.byteLength);
  return bytesToBase64(bytes);
}

// Multilingual greetings
const GREETINGS: Record<string, { greeting: string; pickupQuestion: string }> = {
  en: {
    greeting: "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started.",
    pickupQuestion: "Where would you like to be picked up?"
  },
  auto: {
    greeting: "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started.",
    pickupQuestion: "Where would you like to be picked up?"
  }
};

const CLOSING_SCRIPTS: Record<string, { 
  confirmation: string;
  whatsappDetails: string;
  whatsappTips: string[];
  goodbye: string;
}> = {
  en: {
    confirmation: "Perfect, thank you. I'm making the booking now.",
    whatsappDetails: "You'll receive the booking details and ride updates via WhatsApp.",
    whatsappTips: [
      "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
      "Next time, feel free to book your taxi using a WhatsApp voice message.",
    ],
    goodbye: "Thank you for trying the Taxibot demo, and have a safe journey!"
  },
  auto: {
    confirmation: "Perfect, thank you. I'm making the booking now.",
    whatsappDetails: "You'll receive the booking details and ride updates via WhatsApp.",
    whatsappTips: ["You can also book a taxi by sending us a WhatsApp voice note."],
    goodbye: "Thank you for trying the Taxibot demo, and have a safe journey!"
  }
};

function getGreeting(language: string): { greeting: string; pickupQuestion: string } {
  return GREETINGS[language] || GREETINGS["en"];
}

function getClosingScript(language: string) {
  return CLOSING_SCRIPTS[language] || CLOSING_SCRIPTS["en"];
}

// System prompt builder
function buildSystemPrompt(language: string): string {
  const langInstruction = language === "auto"
    ? "Auto-detect the customer's language from their speech and respond in that language."
    : `Speak in ${language === "en" ? "English" : language}.`;

  return `You are Ada, a friendly and efficient taxi booking assistant.
${langInstruction}

# BOOKING FLOW (STRICT ORDER)
1. Ask for PICKUP address → call sync_booking_data(pickup=...)
2. Ask for DESTINATION address → call sync_booking_data(destination=...)
3. Ask for NUMBER OF PASSENGERS → call sync_booking_data(passengers=...)
4. Ask if they need it NOW or schedule → call sync_booking_data(pickup_time=...)
5. Summarize ALL details, then call book_taxi(action="request_quote")
6. Wait for fare/ETA from dispatch, then ask customer to confirm
7. If confirmed → call book_taxi(action="confirmed")
8. Deliver closing script → call end_call()

# ADDRESS HANDLING
✅ Accept ANY address exactly as spoken - NEVER ask for clarification
✅ Move to the next question immediately after receiving any address
❌ NEVER ask for house numbers, postcodes, or more details

# CONTEXT PAIRING (CRITICAL)
When the user responds, check what question you just asked them:
- If you asked for PICKUP → their response is the pickup location
- If you asked for DESTINATION → their response is the destination  
- If you asked for PASSENGERS → their response is the passenger count
NEVER swap fields. Trust the question context.

# TOOLS
- sync_booking_data: Save user answers to the correct field
- book_taxi: Request quote or confirm booking
- end_call: Disconnect after goodbye
`;
}

// Tools
const TOOLS = [
  {
    type: "function",
    name: "sync_booking_data",
    description: "Save user answers to the correct field.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address" },
        destination: { type: "string", description: "Destination address" },
        passengers: { type: "integer", description: "Number of passengers" },
        pickup_time: { type: "string", description: "Pickup time" },
        last_question_asked: { 
          type: "string", 
          enum: ["pickup", "destination", "passengers", "time", "confirmation", "none"],
          description: "What question you are about to ask NEXT"
        }
      }
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Request quote or confirm booking.",
    parameters: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["request_quote", "confirmed"] },
        pickup: { type: "string" },
        destination: { type: "string" },
        passengers: { type: "integer", minimum: 1 },
        time: { type: "string" }
      },
      required: ["action"]
    }
  },
  {
    type: "function",
    name: "end_call",
    description: "Disconnect the call after goodbye.",
    parameters: { type: "object", properties: {} }
  }
];

// Session state
interface SessionState {
  callId: string;
  callerPhone: string;
  language: string;
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
  openAiSpeechStartedAt: number;
  echoGuardUntil: number;
  lastAdaFinishedSpeakingAt: number;
  greetingProtectionUntil: number;
  summaryProtectionUntil: number;
  quoteInFlight: boolean;
  lastQuoteRequestedAt: number;
  dispatchJobId: string | null;
  pendingBookingRef: string | null;
  pendingConfirmationCallback: string | null;
  pendingFare: string | null;
  pendingEta: string | null;
  awaitingConfirmation: boolean;
  bookingRef: string | null;
  waitingForQuoteSilence: boolean;
  saidOneMoment: boolean;
  // Desktop bridge specific
  audioPacketsReceived: number;
  audioPacketsSent: number;
}

// Timing constants - DESKTOP OPTIMIZED (reduced guards for cleaner audio path)
const ECHO_GUARD_MS = 150; // Reduced from 250ms
const GREETING_PROTECTION_MS = 2000; // Reduced from 3000ms
const SUMMARY_PROTECTION_MS = 6000; // Reduced from 8000ms
const ASSISTANT_LEADIN_IGNORE_MS = 500; // Reduced from 700ms
const BARGE_IN_RMS_MIN = 800; // Reduced from 1000
const BARGE_IN_RMS_MAX = 25000; // Increased from 20000

function computeRms(pcm: Int16Array): number {
  if (pcm.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < pcm.length; i++) sum += pcm[i] * pcm[i];
  return Math.sqrt(sum / pcm.length);
}

function createSessionState(callId: string, callerPhone: string, language: string): SessionState {
  return {
    callId,
    callerPhone,
    language,
    booking: { pickup: null, destination: null, passengers: null, pickupTime: null },
    lastQuestionAsked: "none",
    conversationHistory: [],
    bookingConfirmed: false,
    openAiResponseActive: false,
    openAiSpeechStartedAt: 0,
    echoGuardUntil: 0,
    lastAdaFinishedSpeakingAt: 0,
    greetingProtectionUntil: Date.now() + GREETING_PROTECTION_MS,
    summaryProtectionUntil: 0,
    quoteInFlight: false,
    lastQuoteRequestedAt: 0,
    dispatchJobId: null,
    pendingBookingRef: null,
    pendingConfirmationCallback: null,
    pendingFare: null,
    pendingEta: null,
    awaitingConfirmation: false,
    bookingRef: null,
    waitingForQuoteSilence: false,
    saidOneMoment: false,
    audioPacketsReceived: 0,
    audioPacketsSent: 0
  };
}

// Update live_calls table
async function updateLiveCall(sessionState: SessionState) {
  try {
    await supabase
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
        source: "desktop",
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
  } catch (e) {
    console.error(`[${sessionState.callId}] Error updating live_calls:`, e);
  }
}

// Dispatch webhook
async function sendDispatchWebhook(
  sessionState: SessionState,
  action: string,
  bookingData: Record<string, unknown>
): Promise<{ success: boolean; fare?: string; eta?: string; error?: string }> {
  const callId = sessionState.callId;
  
  if (!DISPATCH_WEBHOOK_URL) {
    console.log(`[${callId}] ⚠️ No DISPATCH_WEBHOOK_URL - simulating response`);
    return { success: true, fare: "£8.50", eta: "5 minutes" };
  }

  if (!sessionState.dispatchJobId) {
    sessionState.dispatchJobId = crypto.randomUUID();
  }
  
  const payload = {
    action,
    job_id: sessionState.dispatchJobId,
    call_id: callId,
    phone: sessionState.callerPhone.replace(/^\+/, ""),
    pickup: bookingData.pickup || sessionState.booking.pickup,
    destination: bookingData.destination || sessionState.booking.destination,
    passengers: bookingData.passengers || sessionState.booking.passengers,
    pickup_time: bookingData.pickup_time || sessionState.booking.pickupTime || "ASAP",
    callback_url: `${SUPABASE_URL}/functions/v1/taxi-dispatch-callback`,
    user_transcripts: sessionState.conversationHistory
      .filter(m => m.role === "user")
      .map(m => ({ text: m.content, timestamp: new Date(m.timestamp).toISOString() }))
  };

  try {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 30000);
    
    const response = await fetch(DISPATCH_WEBHOOK_URL, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Call-ID": callId,
        "X-Job-ID": sessionState.dispatchJobId
      },
      body: JSON.stringify(payload),
      signal: controller.signal
    });
    
    clearTimeout(timeoutId);
    
    if (!response.ok) {
      return { success: false, error: `Webhook returned ${response.status}` };
    }

    let data: any = null;
    try {
      const text = await response.text();
      if (text && text.trim().startsWith("{")) {
        data = JSON.parse(text);
      }
    } catch { /* not JSON */ }

    console.log(`[${callId}] ✅ Webhook sent successfully`);
    return {
      success: true,
      fare: data?.fare || data?.estimated_fare,
      eta: data?.eta || data?.estimated_eta
    };
  } catch (e) {
    console.error(`[${callId}] ❌ Webhook failed:`, e);
    return { success: false, error: String(e) };
  }
}

// Handle WebSocket connection
async function handleConnection(socket: WebSocket, callId: string, callerPhone: string, language: string) {
  console.log(`[${callId}] 🖥️ DESKTOP MODE: New connection from ${callerPhone} (language: ${language})`);
  
  let effectiveLanguage = language;
  
  // Quick caller lookup
  if (callerPhone && callerPhone !== "unknown") {
    try {
      const phoneKey = normalizePhone(callerPhone);
      const { data: callerData } = await supabase
        .from("callers")
        .select("preferred_language")
        .eq("phone_number", phoneKey)
        .maybeSingle();
      
      if (callerData?.preferred_language && (language === "auto" || !language)) {
        effectiveLanguage = callerData.preferred_language;
        console.log(`[${callId}] 🌐 Using saved language: ${effectiveLanguage}`);
      }
    } catch { /* ignore */ }
    
    if (effectiveLanguage === "auto" || !effectiveLanguage) {
      const phoneLanguage = detectLanguageFromPhone(callerPhone);
      if (phoneLanguage) {
        effectiveLanguage = phoneLanguage;
        console.log(`[${callId}] 🌐 Detected from phone: ${effectiveLanguage}`);
      } else {
        effectiveLanguage = "en"; // Default to English for desktop
      }
    }
  }
  
  const sessionState = createSessionState(callId, callerPhone, effectiveLanguage);
  let openaiWs: WebSocket | null = null;
  let cleanedUp = false;
  let dispatchChannel: ReturnType<typeof supabase.channel> | null = null;
  
  // Tracked timers
  const activeTimers = new Set<ReturnType<typeof setTimeout>>();
  const trackedTimeout = (fn: () => void, ms: number) => {
    const id = setTimeout(() => { activeTimers.delete(id); fn(); }, ms);
    activeTimers.add(id);
    return id;
  };

  // Cleanup
  const cleanup = async () => {
    if (cleanedUp) return;
    cleanedUp = true;
    console.log(`[${callId}] 🧹 Cleanup - received ${sessionState.audioPacketsReceived} packets, sent ${sessionState.audioPacketsSent}`);
    
    for (const id of activeTimers) clearTimeout(id);
    activeTimers.clear();
    
    if (dispatchChannel) {
      try {
        await dispatchChannel.unsubscribe();
        supabase.removeChannel(dispatchChannel);
      } catch { /* ignore */ }
    }
    
    try {
      await supabase
        .from("live_calls")
        .update({ status: sessionState.bookingConfirmed ? "completed" : "ended", ended_at: new Date().toISOString() })
        .eq("call_id", callId);
    } catch { /* ignore */ }
    
    if (openaiWs?.readyState === WebSocket.OPEN) openaiWs.close();
  };

  // Keep-alive
  const keepaliveInterval = setInterval(() => {
    if (cleanedUp || socket.readyState !== WebSocket.OPEN) {
      clearInterval(keepaliveInterval);
      return;
    }
    try {
      socket.send(JSON.stringify({ type: "keepalive", timestamp: Date.now(), call_id: callId }));
    } catch { /* ignore */ }
  }, 15000);

  // Dispatch channel for fare callbacks
  dispatchChannel = supabase.channel(`dispatch_${callId}`);
  
  dispatchChannel.on("broadcast", { event: "dispatch_ask_confirm" }, async (payload: any) => {
    const { message, fare, eta, eta_minutes, callback_url, booking_ref } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH ask_confirm: fare=${fare}, eta=${eta_minutes || eta}`);
    
    if (!message || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) return;
    if (sessionState.awaitingConfirmation || sessionState.bookingConfirmed) return;
    
    sessionState.pendingConfirmationCallback = callback_url;
    sessionState.pendingFare = fare;
    sessionState.pendingEta = eta_minutes || eta;
    sessionState.pendingBookingRef = booking_ref || null;
    sessionState.waitingForQuoteSilence = false;
    sessionState.saidOneMoment = false;
    
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: `[DISPATCH QUOTE]: "${message}". Ask if they want to proceed. On YES → call book_taxi(action:"confirmed"). On NO → ask if anything else.` }]
      }
    }));
    openaiWs.send(JSON.stringify({ type: "response.create" }));
    
    sessionState.quoteInFlight = false;
    sessionState.awaitingConfirmation = true;
    sessionState.lastQuestionAsked = "confirmation";
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_confirm" }, async (payload: any) => {
    const { message, booking_ref } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH confirm: ref=${booking_ref}`);
    
    if (!message || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) return;
    
    sessionState.bookingConfirmed = true;
    sessionState.bookingRef = booking_ref;
    
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS * 1.5;
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: `[BOOKING CONFIRMED]: "${message}". Say goodbye and call end_call().` }]
      }
    }));
    openaiWs.send(JSON.stringify({ type: "response.create" }));
  });
  
  dispatchChannel.subscribe((status) => {
    console.log(`[${callId}] 📡 Dispatch channel: ${status}`);
  });

  // Connect to OpenAI
  try {
    openaiWs = new WebSocket(OPENAI_REALTIME_URL, ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`]);
  } catch (e) {
    console.error(`[${callId}] Failed to connect to OpenAI:`, e);
    socket.close();
    return;
  }

  let greetingSent = false;
  let greetingFallbackTimer: ReturnType<typeof setTimeout> | null = null;

  const sendGreeting = () => {
    if (greetingSent || cleanedUp || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    greetingSent = true;
    
    const { greeting, pickupQuestion } = getGreeting(sessionState.language);
    const fullGreeting = `${greeting} ${pickupQuestion}`;
    
    console.log(`[${callId}] 🎤 Sending greeting`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "assistant",
        content: [{ type: "output_text", text: fullGreeting }]
      }
    }));
    
    sessionState.lastQuestionAsked = "pickup";
    sessionState.conversationHistory.push({ role: "assistant", content: fullGreeting, timestamp: Date.now() });
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: { instructions: `Say exactly: "${fullGreeting}"` }
    }));
    
    updateLiveCall(sessionState);
  };

  openaiWs.onopen = () => {
    console.log(`[${callId}] ✅ OpenAI connected`);
    greetingFallbackTimer = trackedTimeout(sendGreeting, 3000);
  };

  openaiWs.onmessage = async (event) => {
    try {
      const data = JSON.parse(event.data);
      
      switch (data.type) {
        case "session.created":
          console.log(`[${callId}] 📋 Session created - configuring...`);
          
          openaiWs!.send(JSON.stringify({
            type: "session.update",
            session: {
              modalities: ["text", "audio"],
              instructions: buildSystemPrompt(sessionState.language),
              voice: VOICE,
              input_audio_format: "pcm16",
              output_audio_format: "pcm16",
              input_audio_transcription: { model: "whisper-1" },
              turn_detection: { type: "server_vad", threshold: 0.5, prefix_padding_ms: 300, silence_duration_ms: 1200 },
              tools: TOOLS,
              tool_choice: "auto",
              temperature: 0.6
            }
          }));
          break;
          
        case "session.updated":
          console.log(`[${callId}] ✅ Session configured - sending greeting`);
          if (greetingFallbackTimer) { clearTimeout(greetingFallbackTimer); greetingFallbackTimer = null; }
          trackedTimeout(sendGreeting, 200);
          break;
          
        case "response.audio.delta":
          if (data.delta) {
            if (!sessionState.openAiResponseActive) {
              sessionState.openAiSpeechStartedAt = Date.now();
            }
            sessionState.openAiResponseActive = true;
            sessionState.audioPacketsSent++;
            
            if (socket.readyState === WebSocket.OPEN) {
              socket.send(JSON.stringify({ type: "audio", audio: data.delta }));
            }
          }
          break;

        case "response.audio_transcript.done":
          if (data.transcript) {
            sessionState.conversationHistory.push({ role: "assistant", content: data.transcript, timestamp: Date.now() });
            console.log(`[${callId}] 🤖 Ada: "${data.transcript.substring(0, 60)}..."`);
            
            // Echo guard after Ada finishes
            sessionState.echoGuardUntil = Date.now() + ECHO_GUARD_MS;
            sessionState.lastAdaFinishedSpeakingAt = Date.now();
          }
          break;

        case "input_audio_buffer.speech_started":
          console.log(`[${callId}] 🎤 User started speaking`);
          break;

        case "conversation.item.input_audio_transcription.completed":
          if (data.transcript) {
            const userText = data.transcript.trim();
            if (!userText) break;
            
            console.log(`[${callId}] 👤 User: "${userText}"`);
            
            sessionState.conversationHistory.push({
              role: "user",
              content: `[CONTEXT: Ada asked about ${sessionState.lastQuestionAsked}] ${userText}`,
              timestamp: Date.now()
            });
            
            // Context pairing instruction
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: `[CONTEXT] You asked about "${sessionState.lastQuestionAsked}". User said: "${userText}". 
Save to the CORRECT field using sync_booking_data.
Current: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=${sessionState.booking.passengers ?? "empty"}`
                }]
              }
            }));
            
            await updateLiveCall(sessionState);
          }
          break;

        case "response.function_call_arguments.done":
          const toolName = data.name;
          let toolArgs: Record<string, unknown> = {};
          try { toolArgs = JSON.parse(data.arguments || "{}"); } catch { /* ignore */ }
          
          console.log(`[${callId}] 🔧 Tool: ${toolName}`, toolArgs);
          
          if (toolName === "sync_booking_data") {
            if (toolArgs.pickup) sessionState.booking.pickup = String(toolArgs.pickup);
            if (toolArgs.destination) sessionState.booking.destination = String(toolArgs.destination);
            if (toolArgs.passengers !== undefined) sessionState.booking.passengers = Number(toolArgs.passengers);
            if (toolArgs.pickup_time) sessionState.booking.pickupTime = String(toolArgs.pickup_time);
            if (toolArgs.last_question_asked) {
              sessionState.lastQuestionAsked = toolArgs.last_question_asked as SessionState["lastQuestionAsked"];
            }
            
            console.log(`[${callId}] 📊 State:`, sessionState.booking);
            await updateLiveCall(sessionState);
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: { type: "function_call_output", call_id: data.call_id, output: JSON.stringify({ success: true, state: sessionState.booking }) }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
          } else if (toolName === "book_taxi") {
            const action = String(toolArgs.action || "");
            
            if (action === "request_quote") {
              if (sessionState.awaitingConfirmation || sessionState.quoteInFlight) {
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: { type: "function_call_output", call_id: data.call_id, output: JSON.stringify({ success: true, status: "pending" }) }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
              
              sessionState.quoteInFlight = true;
              sessionState.lastQuoteRequestedAt = Date.now();
              sessionState.waitingForQuoteSilence = true;
              
              const result = await sendDispatchWebhook(sessionState, action, {
                pickup: toolArgs.pickup || sessionState.booking.pickup,
                destination: toolArgs.destination || sessionState.booking.destination,
                passengers: toolArgs.passengers || sessionState.booking.passengers,
                pickup_time: toolArgs.time || sessionState.booking.pickupTime
              });
              
              if (!result.success) {
                sessionState.quoteInFlight = false;
                sessionState.waitingForQuoteSilence = false;
              }
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ success: result.success, status: "waiting_for_dispatch", message: "Say: 'One moment please.' then wait." })
                }
              }));
              openaiWs!.send(JSON.stringify({
                type: "response.create",
                response: { instructions: "Say: 'One moment please while I check that for you.' Then STOP." }
              }));
              
            } else if (action === "confirmed") {
              if (sessionState.bookingConfirmed) {
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: { type: "function_call_output", call_id: data.call_id, output: JSON.stringify({ success: true, status: "already_confirmed" }) }
                }));
                break;
              }
              
              sessionState.bookingConfirmed = true;
              
              // Send confirmation to dispatch
              if (sessionState.pendingConfirmationCallback) {
                try {
                  await fetch(sessionState.pendingConfirmationCallback, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                      action: "confirmed",
                      job_id: sessionState.dispatchJobId,
                      call_id: callId,
                      booking: sessionState.booking
                    })
                  });
                } catch { /* ignore */ }
              }
              
              const closingScript = getClosingScript(sessionState.language);
              const tip = closingScript.whatsappTips[0];
              
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS * 2;
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: true,
                    status: "confirmed",
                    message: `Say: "${closingScript.confirmation}" "${closingScript.whatsappDetails}" "${tip}" "${closingScript.goodbye}" Then call end_call().`
                  })
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
              
              await updateLiveCall(sessionState);
            }
            
          } else if (toolName === "end_call") {
            console.log(`[${callId}] 📞 Call ending`);
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: { type: "function_call_output", call_id: data.call_id, output: JSON.stringify({ success: true }) }
            }));
            
            sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS * 2;
            
            trackedTimeout(() => {
              try { socket.send(JSON.stringify({ type: "hangup" })); } catch { /* ignore */ }
              cleanup();
            }, 15000);
          }
          break;

        case "response.done":
          sessionState.openAiResponseActive = false;
          sessionState.openAiSpeechStartedAt = 0;
          break;
          
        case "error":
          console.error(`[${callId}] ❌ OpenAI error:`, data.error);
          break;
      }
    } catch (e) {
      console.error(`[${callId}] Error processing OpenAI message:`, e);
    }
  };

  openaiWs.onerror = (error) => console.error(`[${callId}] OpenAI error:`, error);
  openaiWs.onclose = () => { console.log(`[${callId}] OpenAI closed`); cleanup(); };

  // Client message handler
  socket.onmessage = async (event) => {
    try {
      if (typeof event.data === "string") {
        const data = JSON.parse(event.data);
        
        // Handle input_audio_buffer.append from C# bridge (already PCM16 @ 24kHz)
        if (data.type === "input_audio_buffer.append" && data.audio) {
          sessionState.audioPacketsReceived++;
          
          // Log first few packets
          if (sessionState.audioPacketsReceived <= 3) {
            console.log(`[${callId}] 🎙️ Audio packet #${sessionState.audioPacketsReceived}: ${data.audio.length} chars`);
          } else if (sessionState.audioPacketsReceived % 100 === 0) {
            console.log(`[${callId}] 📊 Audio stats: received=${sessionState.audioPacketsReceived}, sent=${sessionState.audioPacketsSent}`);
          }
          
          // Greeting protection
          if (Date.now() < sessionState.greetingProtectionUntil) return;
          
          // Echo guard
          if (Date.now() < sessionState.echoGuardUntil) return;
          
          // Summary protection (allow confirmation responses through)
          if (Date.now() < sessionState.summaryProtectionUntil) {
            const awaitingYesNo = sessionState.awaitingConfirmation || sessionState.lastQuestionAsked === "confirmation";
            if (sessionState.openAiResponseActive || !awaitingYesNo) return;
          }
          
          // Forward directly to OpenAI - C# bridge already sends PCM16 @ 24kHz
          if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.append", audio: data.audio }));
          }
          
        } else if (data.type === "audio" && data.audio) {
          // Legacy handler for ulaw audio
          sessionState.audioPacketsReceived++;
          
          const binaryStr = atob(data.audio);
          const bytes = new Uint8Array(binaryStr.length);
          for (let i = 0; i < binaryStr.length; i++) bytes[i] = binaryStr.charCodeAt(i);
          
          if (Date.now() < sessionState.greetingProtectionUntil) return;
          if (Date.now() < sessionState.echoGuardUntil) return;
          
          const pcm8k = ulawToPcm16(bytes);
          const pcm24k = resamplePcm16To24k(pcm8k, 8000);
          const base64Audio = pcm16ToBase64(pcm24k);
          
          if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({ type: "input_audio_buffer.append", audio: base64Audio }));
          }
          
        } else if (data.type === "init" || data.type === "update_phone") {
          if (data.phone && data.phone !== "unknown") {
            sessionState.callerPhone = data.phone;
            console.log(`[${callId}] 📱 Phone updated: ${data.phone}`);
          }
        } else if (data.type === "hangup") {
          console.log(`[${callId}] Client hangup`);
          cleanup();
        }
      }
    } catch (e) {
      console.error(`[${callId}] Error processing client message:`, e);
    }
  };

  socket.onerror = (error) => console.error(`[${callId}] Client error:`, error);
  socket.onclose = () => { console.log(`[${callId}] Client closed`); clearInterval(keepaliveInterval); cleanup(); };
}

// Main server
Deno.serve(async (req) => {
  const url = new URL(req.url);
  
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  if (url.pathname === "/health" || url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ 
      status: "healthy",
      mode: "desktop",
      timestamp: new Date().toISOString()
    }), { headers: { ...corsHeaders, "Content-Type": "application/json" } });
  }
  
  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() === "websocket") {
    // Support both ?caller= (C# bridge) and ?caller_phone= (standard)
    const callId = url.searchParams.get("call_id") || `desktop-${Date.now()}`;
    const callerPhone = url.searchParams.get("caller") || url.searchParams.get("caller_phone") || "unknown";
    const language = url.searchParams.get("language") || "auto";
    
    console.log(`[${callId}] 🖥️ Desktop WebSocket upgrade: caller=${callerPhone}`);
    
    const { socket, response } = Deno.upgradeWebSocket(req);
    handleConnection(socket, callId, callerPhone, language);
    return response;
  }
  
  return new Response(JSON.stringify({ 
    error: "WebSocket upgrade required",
    mode: "taxi-realtime-desktop"
  }), { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } });
});
