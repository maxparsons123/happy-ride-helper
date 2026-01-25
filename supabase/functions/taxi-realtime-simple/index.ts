import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// === BOOKING STATE ===
interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: string | null;
  time: string | null;
  currentStep: "pickup" | "destination" | "passengers" | "time" | "summary" | "done";
  lastUserTranscript: string | null;
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

# MULTILINGUAL SUPPORT (CRITICAL)
- ALWAYS respond in the SAME LANGUAGE the caller is speaking
- If they speak Spanish, respond entirely in Spanish
- If they speak French, respond entirely in French
- If they speak Polish, respond entirely in Polish
- If they switch languages mid-call, switch with them
- Adapt greetings, questions, and confirmations to match their language
- You are fluent in ALL languages - never say you cannot speak a language

# ONE QUESTION RULE
Ask ONLY ONE question per response. NEVER combine questions.

# GREETING (Say this FIRST when call starts)
Start in English: "Hello, and welcome to the Taxibot demo. I'm ADA, your taxi booking assistant. Where would you like to be picked up?"
If caller responds in another language, IMMEDIATELY switch to that language for all future responses.

# BOOKING FLOW (Ask ONE at a time, in order)
1. Get pickup location
2. Get destination  
3. Get number of passengers
4. Get pickup time (default: now/ASAP)
5. Summarize booking and ask for confirmation
6. If confirmed, say "Your taxi is booked! You'll receive updates via WhatsApp. Have a safe journey!"

# PASSENGERS (ANTI-STUCK RULE)
- Only move past the passengers step if the caller clearly provides a passenger count.
- Accept digits (e.g. "3") or clear number words (one, two, three, four, five, six, seven, eight, nine, ten).
- Also accept common telephony homophones: "to/too" → two, "for" → four, "tree" → three.
- If the caller says something that sounds like an address/place (street/road/avenue/hotel/etc.) while you are asking for passengers, DO NOT advance.
- Instead, repeat exactly: "How many people will be travelling?"

# CORRECTIONS & CHANGES (CRITICAL)
When the caller wants to change or correct something they said:
- Listen for: "actually", "no wait", "change", "I meant", "not X, it's Y", "sorry, it's", "let me correct"
- IMMEDIATELY update your understanding with the new information
- Acknowledge briefly: "Updated to [new value]." then continue the flow
- If they correct during the summary, say "Let me update that" and give a NEW summary with the corrected info
- NEVER ignore corrections - always act on them

# LOCAL EVENTS & INFORMATION
When the caller asks about local events, things to do, attractions, or what's happening:
- Use the get_local_events tool to fetch current events
- Share the information helpfully and enthusiastically
- After sharing, gently guide them back: "Would you like me to book a taxi to any of these?"
- Be knowledgeable and informative about the local area

# RULES
- Do NOT say "Got it" or "Great" before asking the next question
- Do NOT repeat or confirm individual answers mid-flow
- After each answer, immediately ask the NEXT question
- Only summarize at the end before confirmation
- If caller says "no" to the summary, ask "What would you like to change?"
`;

// === AUDIO HELPERS ===

// DSP Settings for inbound telephony audio (user voice → OpenAI)
// NOTE: Gentle settings to avoid distorting audio for Whisper STT
const INBOUND_DSP = {
  volumeBoost: 1.5,        // Gentle boost (was 2.5 - too aggressive)
  preEmphasis: 0.5,        // Light pre-emphasis (was 0.97 - too harsh)
  noiseGateThreshold: 30,  // Lower threshold to preserve quiet speech
};

// DSP Settings for outbound audio (Ada voice → telephony)
const OUTBOUND_DSP = {
  volumeBoost: 1.4,        // Slight boost for telephony clarity
  deEmphasis: 0.95,        // Reduce harshness from AI voice
  highShelfBoost: 1.15,    // Brighten voice for telephony
  softLimitThreshold: 28000, // Prevent clipping on loud passages
};

// Apply volume boost with soft limiting
function applyVolumeBoost(samples: Int16Array, boost: number): void {
  for (let i = 0; i < samples.length; i++) {
    let sample = samples[i] * boost;
    // Soft limiter to prevent clipping
    if (sample > 32767) sample = 32767;
    else if (sample < -32768) sample = -32768;
    samples[i] = Math.round(sample);
  }
}

// Pre-emphasis filter to boost high frequencies (clearer consonants)
function applyPreEmphasis(samples: Int16Array, coefficient: number): void {
  let prev = 0;
  for (let i = 0; i < samples.length; i++) {
    const current = samples[i];
    samples[i] = Math.round(current - coefficient * prev);
    prev = current;
  }
}

// De-emphasis filter (inverse of pre-emphasis) - smooths harsh frequencies
function applyDeEmphasis(samples: Int16Array, coefficient: number): void {
  let prev = 0;
  for (let i = 0; i < samples.length; i++) {
    const current = samples[i] + coefficient * prev;
    samples[i] = Math.round(current);
    prev = current;
  }
}

// High-shelf boost for brightness (simple 1-pole high-pass blend)
function applyHighShelfBoost(samples: Int16Array, boost: number): void {
  let prev = samples[0];
  for (let i = 1; i < samples.length; i++) {
    const current = samples[i];
    const highFreq = current - prev;
    samples[i] = Math.round(current + highFreq * (boost - 1));
    prev = current;
  }
}

// Soft limiter to prevent clipping on loud passages
function applySoftLimiter(samples: Int16Array, threshold: number): void {
  for (let i = 0; i < samples.length; i++) {
    const sample = samples[i];
    const absSample = Math.abs(sample);
    if (absSample > threshold) {
      // Soft knee compression above threshold
      const excess = absSample - threshold;
      const compressed = threshold + excess * 0.3;
      samples[i] = Math.round(sample > 0 ? compressed : -compressed);
    }
  }
}

// 3-tap low-pass filter to prevent aliasing before upsampling
function applyLowPass(samples: Int16Array): void {
  const weights = [0.25, 0.5, 0.25];
  let prev1 = samples[0];
  let prev2 = samples[0];
  
  for (let i = 0; i < samples.length; i++) {
    const current = samples[i];
    const filtered = prev2 * weights[0] + prev1 * weights[1] + current * weights[2];
    prev2 = prev1;
    prev1 = current;
    samples[i] = Math.round(filtered);
  }
}

// Calculate RMS for noise gate
function calculateRMS(samples: Int16Array): number {
  let sum = 0;
  for (let i = 0; i < samples.length; i++) {
    sum += samples[i] * samples[i];
  }
  return Math.sqrt(sum / samples.length);
}

// Apply soft noise gate with fade
function applyNoiseGate(samples: Int16Array, threshold: number): void {
  const rms = calculateRMS(samples);
  if (rms < threshold) {
    // Soft fade to reduce noise without harsh cutoff
    const fadeAmount = rms / threshold;
    for (let i = 0; i < samples.length; i++) {
      samples[i] = Math.round(samples[i] * fadeAmount * fadeAmount);
    }
  }
}

// Inbound DSP pipeline: Volume -> Low-Pass -> Pre-Emphasis -> Noise Gate
function processInboundDSP(samples: Int16Array): void {
  applyVolumeBoost(samples, INBOUND_DSP.volumeBoost);
  applyLowPass(samples);
  applyPreEmphasis(samples, INBOUND_DSP.preEmphasis);
  applyNoiseGate(samples, INBOUND_DSP.noiseGateThreshold);
}

// Outbound DSP pipeline: De-Emphasis -> High-Shelf -> Volume -> Soft Limiter
function processOutboundDSP(samples: Int16Array): void {
  applyDeEmphasis(samples, OUTBOUND_DSP.deEmphasis);
  applyHighShelfBoost(samples, OUTBOUND_DSP.highShelfBoost);
  applyVolumeBoost(samples, OUTBOUND_DSP.volumeBoost);
  applySoftLimiter(samples, OUTBOUND_DSP.softLimitThreshold);
}

// Process outbound audio from OpenAI (24kHz PCM16)
function processAdaAudio(audioBytes: Uint8Array): Uint8Array {
  // Convert to Int16Array for DSP processing
  const samples = new Int16Array(audioBytes.buffer, audioBytes.byteOffset, Math.floor(audioBytes.byteLength / 2));
  
  // Apply outbound DSP
  processOutboundDSP(samples);
  
  return new Uint8Array(samples.buffer);
}

// Upsample 8kHz to 24kHz (clean passthrough - no DSP)
function pcm8kTo24k(pcm8k: Uint8Array): Uint8Array {
  const samples8k = new Int16Array(pcm8k.buffer, pcm8k.byteOffset, Math.floor(pcm8k.byteLength / 2));
  
  // No DSP - pass raw audio to Whisper for best STT accuracy
  
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

// === STATE HELPERS ===

function detectStepFromAdaTranscript(transcript: string): BookingState["currentStep"] | null {
  const lower = transcript.toLowerCase();
  
  if (/where would you like to be picked up|pickup (location|address)|pick you up/i.test(lower)) {
    return "pickup";
  }
  if (/where (would you like to go|are you going|is your destination)|destination/i.test(lower)) {
    return "destination";
  }
  if (/how many (people|passengers)|travelling/i.test(lower)) {
    return "passengers";
  }
  if (/when would you like|pickup time|what time|now or later/i.test(lower)) {
    return "time";
  }
  if (/let me confirm|to confirm|summary|picking you up from/i.test(lower)) {
    return "summary";
  }
  if (/taxi is booked|safe journey|whatsapp/i.test(lower)) {
    return "done";
  }
  return null;
}

function extractPassengerCount(text: string): string | null {
  const lower = text.toLowerCase().trim();
  
  // Number words
  const wordMap: Record<string, string> = {
    "one": "1", "two": "2", "to": "2", "too": "2",
    "three": "3", "tree": "3", "free": "3",
    "four": "4", "for": "4", "five": "5",
    "six": "6", "seven": "7", "eight": "8",
    "nine": "9", "ten": "10"
  };
  
  for (const [word, num] of Object.entries(wordMap)) {
    if (lower === word || lower.startsWith(word + " ")) {
      return num;
    }
  }
  
  // Digits
  const digitMatch = lower.match(/^(\d+)/);
  if (digitMatch) {
    return digitMatch[1];
  }
  
  // "just me" / "myself"
  if (/just me|myself|alone|only me/.test(lower)) {
    return "1";
  }
  
  return null;
}

function extractTime(text: string): string | null {
  const lower = text.toLowerCase().trim();
  
  if (/now|asap|as soon as|right now|straight away|immediately/.test(lower)) {
    return "now";
  }
  
  // Match times like "3pm", "3:30", "15:00"
  const timeMatch = lower.match(/(\d{1,2}(?::\d{2})?\s*(?:am|pm)?)/i);
  if (timeMatch) {
    return timeMatch[1];
  }
  
  // "in X minutes"
  const inMinutes = lower.match(/in (\d+) minutes?/);
  if (inMinutes) {
    return `in ${inMinutes[1]} minutes`;
  }
  
  return null;
}

// Get local events (mock data - could be replaced with real API)
function getLocalEvents(query: string, location: string): object {
  const lowerQuery = query.toLowerCase();
  const today = new Date();
  const dayOfWeek = today.toLocaleDateString('en-GB', { weekday: 'long' });
  
  // Dynamic event suggestions based on query
  const eventCategories: Record<string, object[]> = {
    concerts: [
      { name: "Live Jazz at The Jam House", venue: "The Jam House, St. Pauls Square", time: "8pm tonight", type: "music" },
      { name: "Symphony Hall Classical Evening", venue: "Symphony Hall", time: "7:30pm", type: "music" },
      { name: "Indie Night at O2 Academy", venue: "O2 Academy Birmingham", time: "9pm", type: "music" }
    ],
    football: [
      { name: "Aston Villa vs Manchester City", venue: "Villa Park", time: "3pm Saturday", type: "sport" },
      { name: "Birmingham City FC Home Game", venue: "St Andrew's Stadium", time: "7:45pm Tuesday", type: "sport" }
    ],
    theatre: [
      { name: "The Lion King", venue: "Birmingham Hippodrome", time: "7:30pm", type: "theatre" },
      { name: "Comedy Night", venue: "Glee Club", time: "8pm", type: "comedy" }
    ],
    food: [
      { name: "Street Food Market", venue: "Digbeth", time: "12pm-10pm", type: "food" },
      { name: "Balti Triangle Food Tour", venue: "Sparkbrook", time: "6pm", type: "food" }
    ],
    general: [
      { name: "Live Music at The Jam House", venue: "The Jam House", time: "8pm", type: "music" },
      { name: "Street Food Market", venue: "Digbeth", time: "All day", type: "food" },
      { name: "The Lion King Musical", venue: "Birmingham Hippodrome", time: "7:30pm", type: "theatre" },
      { name: "Bullring Late Night Shopping", venue: "Bullring", time: "Until 9pm", type: "shopping" }
    ]
  };
  
  // Match query to category
  let events = eventCategories.general;
  if (/concert|music|gig|live/.test(lowerQuery)) events = eventCategories.concerts;
  else if (/football|soccer|match|villa|blues/.test(lowerQuery)) events = eventCategories.football;
  else if (/theatre|theater|show|musical|comedy/.test(lowerQuery)) events = eventCategories.theatre;
  else if (/food|restaurant|eat|dinner/.test(lowerQuery)) events = eventCategories.food;
  
  return {
    location: location,
    date: dayOfWeek,
    query: query,
    events: events,
    message: `Here are some ${query} happening in ${location} today and this week.`
  };
}

// Detect country/locale from phone number
interface LocaleInfo {
  country: string;
  language: string;
  greeting: string;
  currency: string;
}

function detectLocaleFromPhone(phoneNumber: string): LocaleInfo {
  const cleaned = phoneNumber.replace(/[\s\-\(\)]/g, '');
  
  // Country code mappings
  const countryMap: Record<string, LocaleInfo> = {
    '+44': { country: 'UK', language: 'British English', greeting: 'Hello', currency: 'GBP' },
    '+1': { country: 'USA/Canada', language: 'American English', greeting: 'Hi there', currency: 'USD' },
    '+33': { country: 'France', language: 'French', greeting: 'Bonjour', currency: 'EUR' },
    '+34': { country: 'Spain', language: 'Spanish', greeting: 'Hola', currency: 'EUR' },
    '+49': { country: 'Germany', language: 'German', greeting: 'Guten Tag', currency: 'EUR' },
    '+39': { country: 'Italy', language: 'Italian', greeting: 'Ciao', currency: 'EUR' },
    '+31': { country: 'Netherlands', language: 'Dutch', greeting: 'Hallo', currency: 'EUR' },
    '+32': { country: 'Belgium', language: 'Dutch/French', greeting: 'Bonjour', currency: 'EUR' },
    '+48': { country: 'Poland', language: 'Polish', greeting: 'Dzień dobry', currency: 'PLN' },
    '+351': { country: 'Portugal', language: 'Portuguese', greeting: 'Olá', currency: 'EUR' },
    '+353': { country: 'Ireland', language: 'English', greeting: 'Hello', currency: 'EUR' },
    '+420': { country: 'Czech Republic', language: 'Czech', greeting: 'Dobrý den', currency: 'CZK' },
    '+91': { country: 'India', language: 'Hindi/English', greeting: 'Namaste', currency: 'INR' },
    '+86': { country: 'China', language: 'Mandarin', greeting: '你好', currency: 'CNY' },
    '+81': { country: 'Japan', language: 'Japanese', greeting: 'こんにちは', currency: 'JPY' },
    '+82': { country: 'South Korea', language: 'Korean', greeting: '안녕하세요', currency: 'KRW' },
    '+971': { country: 'UAE', language: 'Arabic/English', greeting: 'Marhaba', currency: 'AED' },
    '+966': { country: 'Saudi Arabia', language: 'Arabic', greeting: 'مرحبا', currency: 'SAR' },
    '+7': { country: 'Russia', language: 'Russian', greeting: 'Привет', currency: 'RUB' },
    '+380': { country: 'Ukraine', language: 'Ukrainian', greeting: 'Привіт', currency: 'UAH' },
    '+90': { country: 'Turkey', language: 'Turkish', greeting: 'Merhaba', currency: 'TRY' },
    '+61': { country: 'Australia', language: 'Australian English', greeting: "G'day", currency: 'AUD' },
    '+64': { country: 'New Zealand', language: 'New Zealand English', greeting: 'Kia ora', currency: 'NZD' },
    '+27': { country: 'South Africa', language: 'English', greeting: 'Hello', currency: 'ZAR' },
    '+55': { country: 'Brazil', language: 'Portuguese', greeting: 'Olá', currency: 'BRL' },
    '+52': { country: 'Mexico', language: 'Spanish', greeting: 'Hola', currency: 'MXN' },
  };
  
  // Check longest prefixes first (3-digit country codes)
  for (const prefix of ['+971', '+966', '+351', '+353', '+420', '+380']) {
    if (cleaned.startsWith(prefix)) {
      return countryMap[prefix];
    }
  }
  
  // Then 2-digit codes
  for (const prefix of Object.keys(countryMap).filter(k => k.length === 3)) {
    if (cleaned.startsWith(prefix)) {
      return countryMap[prefix];
    }
  }
  
  // Default to UK
  return { country: 'UK', language: 'British English', greeting: 'Hello', currency: 'GBP' };
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
  let callerPhone = "";
  let callerLocale: LocaleInfo = { country: 'UK', language: 'British English', greeting: 'Hello', currency: 'GBP' };
  
  // Booking state tracking
  const bookingState: BookingState = {
    pickup: null,
    destination: null,
    passengers: null,
    time: null,
    currentStep: "pickup",
    lastUserTranscript: null
  };

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

  // Inject verified booking state before summary
  const injectVerifiedState = () => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    
    const stateMsg = `[VERIFIED BOOKING DATA - USE THESE EXACT VALUES]
Pickup: ${bookingState.pickup || "NOT PROVIDED"}
Destination: ${bookingState.destination || "NOT PROVIDED"}
Passengers: ${bookingState.passengers || "NOT PROVIDED"}
Time: ${bookingState.time || "now"}

CRITICAL: When summarizing, use ONLY the values above. Do not invent or hallucinate any addresses.`;

    log(`📋 Injecting verified state: P=${bookingState.pickup}, D=${bookingState.destination}, Pax=${bookingState.passengers}, T=${bookingState.time}`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "system",
        content: [{ type: "input_text", text: stateMsg }]
      }
    }));
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

    openaiWs.onmessage = (event) => {
      const msg = JSON.parse(event.data);

      // Session created - configure and trigger greeting
      if (msg.type === "session.created") {
        log("📋 Session created, sending config with locale: " + callerLocale.country);
        
        // Build locale-aware instructions
        const localeInstructions = `
${SYSTEM_PROMPT}

# CALLER LOCALE (DETECTED FROM PHONE NUMBER)
- Caller country: ${callerLocale.country}
- Preferred language: ${callerLocale.language}
- Native greeting: "${callerLocale.greeting}"
- Currency: ${callerLocale.currency}

# LOCALE-SPECIFIC BEHAVIOR
- Start with the greeting appropriate for ${callerLocale.country}: "${callerLocale.greeting}"
- Use ${callerLocale.language} conventions and phrasing
- If UK caller (+44): Use British English, say "taxi" not "cab", "queue" not "line", "mobile" not "cell phone"
- If US/Canada caller (+1): Use American English, say "cab" or "ride", use "line" not "queue"
- If non-English caller: Start in their language, they may still speak English but adapt to their preference
- Currency references should use ${callerLocale.currency} conventions
`;
        
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            instructions: localeInstructions,
            voice: "shimmer",
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
                name: "get_local_events",
                description: "Get information about local events, attractions, things to do, concerts, shows, sports, festivals, or what's happening in the area. Call this when the user asks about events or activities.",
                parameters: {
                  type: "object",
                  properties: {
                    query: { 
                      type: "string",
                      description: "What the user is looking for (e.g., 'concerts tonight', 'football matches', 'things to do')"
                    },
                    location: {
                      type: "string",
                      description: "The city or area to search in (default: Birmingham)"
                    }
                  },
                  required: ["query"]
                }
              }
            ],
            tool_choice: "auto"
          }
        }));
      }

      // Session configured - trigger greeting
      if (msg.type === "session.updated") {
        log("🎤 Session configured, triggering greeting in " + callerLocale.language);
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }

      // Forward audio to bridge (raw passthrough - bridge handles downsampling)
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

      // Track Ada's questions to know current step
      if (msg.type === "response.audio_transcript.done") {
        const transcript = msg.transcript || "";
        log(`🗣️ Ada: ${transcript}`);
        
        const detectedStep = detectStepFromAdaTranscript(transcript);
        if (detectedStep) {
          const previousStep = bookingState.currentStep;
          bookingState.currentStep = detectedStep;
          log(`📍 Step detected: ${previousStep} → ${detectedStep}`);
          
          // If moving to summary, inject verified state
          if (detectedStep === "summary" && previousStep !== "summary") {
            injectVerifiedState();
          }
        }
      }

      // Capture user responses and map to current step
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        const transcript = msg.transcript || "";
        log(`👤 User: ${transcript}`);
        bookingState.lastUserTranscript = transcript;
        
        // Map response to current step
        switch (bookingState.currentStep) {
          case "pickup":
            if (transcript.trim().length > 2) {
              bookingState.pickup = transcript.trim();
              log(`✅ Saved pickup: ${bookingState.pickup}`);
            }
            break;
            
          case "destination":
            if (transcript.trim().length > 2) {
              bookingState.destination = transcript.trim();
              log(`✅ Saved destination: ${bookingState.destination}`);
            }
            break;
            
          case "passengers":
            const pax = extractPassengerCount(transcript);
            if (pax) {
              bookingState.passengers = pax;
              log(`✅ Saved passengers: ${bookingState.passengers}`);
            }
            break;
            
          case "time":
            const time = extractTime(transcript);
            if (time) {
              bookingState.time = time;
              log(`✅ Saved time: ${bookingState.time}`);
            } else if (transcript.trim().length > 0) {
              // Default to "now" if they say anything
              bookingState.time = "now";
              log(`✅ Saved time (default): now`);
            }
            break;
        }
        
        // Log current state
        log(`📊 State: P=${bookingState.pickup || "?"} | D=${bookingState.destination || "?"} | Pax=${bookingState.passengers || "?"} | T=${bookingState.time || "?"}`);
      }

      // Handle function calls (tools)
      if (msg.type === "response.function_call_arguments.done") {
        const funcName = msg.name;
        const callIdFunc = msg.call_id;
        log(`🔧 Tool called: ${funcName}`);
        
        try {
          const args = JSON.parse(msg.arguments || "{}");
          
          if (funcName === "get_local_events") {
            const query = args.query || "events";
            const location = args.location || "Birmingham";
            log(`🎭 Getting local events: "${query}" in ${location}`);
            
            // Mock local events data - in production, this could call a real events API
            const events = getLocalEvents(query, location);
            
            // Send tool result back to OpenAI
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: callIdFunc,
                output: JSON.stringify(events)
              }
            }));
            
            // Trigger response generation
            openaiWs?.send(JSON.stringify({ type: "response.create" }));
          }
        } catch (e) {
          log(`❌ Tool error: ${e}`);
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
    // Binary audio from bridge (8kHz slin)
    if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
      if (openaiWs?.readyState === WebSocket.OPEN) {
        const pcm8k = event.data instanceof ArrayBuffer 
          ? new Uint8Array(event.data) 
          : event.data;
        
        // Upsample to 24kHz and send to OpenAI
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
        callerPhone = msg.caller_phone || msg.from || msg.caller || "";
        
        // Detect locale from caller ID
        if (callerPhone) {
          callerLocale = detectLocaleFromPhone(callerPhone);
          log(`📞 Call initialized from ${callerPhone} → ${callerLocale.country} (${callerLocale.language})`);
        } else {
          log(`📞 Call initialized (no caller ID, defaulting to UK)`);
        }
        
        connectOpenAI();
        socket.send(JSON.stringify({ type: "ready" }));
      }
      
      if (msg.type === "hangup") {
        log("👋 Hangup received");
        openaiWs?.close();
      }
      
      if (msg.type === "ping") {
        socket.send(JSON.stringify({ type: "pong" }));
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
