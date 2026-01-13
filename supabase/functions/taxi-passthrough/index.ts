import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY")!;

// ============================================================================
// TYPES
// ============================================================================

interface PassthroughRequest {
  call_id: string;
  caller_phone?: string;
  caller_name?: string;
  
  // Audio/text input
  audio_base64?: string;  // PCM16 audio from caller
  text_input?: string;    // Or direct text for testing
  
  // Webhook configuration
  webhook_url: string;
  webhook_token?: string;
  
  // Session state (sent back from your system)
  session_state?: Record<string, unknown>;
  
  // Ada's forced response (from your webhook callback)
  forced_response?: string;
  
  // TTS config
  tts_voice?: string;
}

interface ExtractedData {
  // Raw transcription
  user_text: string;
  
  // Extracted fields (best-effort, no validation)
  pickup?: string;
  destination?: string;
  passengers?: number;
  pickup_time?: string;
  luggage?: boolean;
  vehicle_type?: string;
  special_requests?: string;
  customer_name?: string;
  
  // Intent detection
  intent?: "booking" | "query" | "cancel" | "update" | "greeting" | "goodbye" | "unclear";
  
  // Confirmation signals
  confirmed?: boolean;
  correction?: string;
}

interface AdaResponse {
  text: string;
  audio_base64?: string;  // TTS audio (PCM16)
}

interface WebhookPayload {
  call_id: string;
  caller_phone?: string;
  caller_name?: string;
  timestamp: string;
  
  // What the user said
  user: ExtractedData;
  
  // What Ada said in response
  ada: AdaResponse;
  
  // Current session state
  session_state: Record<string, unknown>;
  
  // Conversation history for context
  conversation: Array<{
    role: "user" | "assistant";
    text: string;
    timestamp: string;
  }>;
}

interface WebhookResponse {
  // Optional: Force Ada to say something specific
  ada_response?: string;
  
  // Optional: Ask a specific question
  ada_question?: string;
  
  // Optional: End the call
  end_call?: boolean;
  end_message?: string;
  
  // Updated session state (will be sent back on next turn)
  session_state?: Record<string, unknown>;
  
  // Validation results (for logging/display)
  validation?: {
    pickup_valid?: boolean;
    pickup_corrected?: string;
    destination_valid?: boolean;
    destination_corrected?: string;
    error?: string;
  };
}

// ============================================================================
// SIMPLE EXTRACTION (NO GEOCODING)
// ============================================================================

async function extractFromText(text: string): Promise<ExtractedData> {
  const result: ExtractedData = {
    user_text: text,
  };
  
  const lower = text.toLowerCase();
  
  // Intent detection
  if (/\b(book|taxi|cab|ride|pick\s*up|collect)\b/i.test(lower)) {
    result.intent = "booking";
  } else if (/\b(cancel|nevermind|forget it)\b/i.test(lower)) {
    result.intent = "cancel";
  } else if (/\b(change|update|modify|actually)\b/i.test(lower)) {
    result.intent = "update";
  } else if (/\b(hello|hi|hey|good morning|good afternoon)\b/i.test(lower)) {
    result.intent = "greeting";
  } else if (/\b(bye|goodbye|thank you|thanks|cheers)\b/i.test(lower)) {
    result.intent = "goodbye";
  } else if (/\b(how|what|when|where|price|cost|fare)\b/i.test(lower)) {
    result.intent = "query";
  } else {
    result.intent = "unclear";
  }
  
  // Confirmation signals
  result.confirmed = /\b(yes|yeah|yep|correct|that's right|confirm|book it|go ahead)\b/i.test(lower);
  
  // Correction detection
  const correctionMatch = lower.match(/\b(?:no|actually|sorry|wait)[,\s]+(.+)/i);
  if (correctionMatch) {
    result.correction = correctionMatch[1].trim();
  }
  
  // Pickup extraction: "from X" pattern
  const fromMatch = text.match(/\bfrom\s+(.+?)(?:\s+(?:to|going|heading)\b|$)/i);
  if (fromMatch?.[1]) {
    result.pickup = fromMatch[1].trim().replace(/[.,]+$/, "");
  }
  
  // Destination extraction: "to X" / "going to X"
  const toMatch = text.match(/\b(?:to|going\s+to|heading\s+to)\s+(.+?)(?:\s+(?:for|with|please|thanks)\b|[.,]|$)/i);
  if (toMatch?.[1]) {
    result.destination = toMatch[1].trim().replace(/[.,]+$/, "");
  }
  
  // Passengers extraction
  const wordToNum: Record<string, number> = {
    one: 1, two: 2, three: 3, four: 4, five: 5,
    six: 6, seven: 7, eight: 8, nine: 9, ten: 10,
  };
  
  const passDigit = text.match(/\b(\d+)\s*(?:passengers?|people|persons?)\b/i);
  if (passDigit?.[1]) {
    result.passengers = parseInt(passDigit[1]);
  } else {
    const passWord = text.match(/\b(one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:passengers?|people|persons?)\b/i);
    if (passWord?.[1]) {
      result.passengers = wordToNum[passWord[1].toLowerCase()];
    } else if (/\b(just\s+me|myself|just\s+one)\b/i.test(lower)) {
      result.passengers = 1;
    }
  }
  
  // Bare number (when asked "how many passengers?")
  if (!result.passengers) {
    const bareNum = text.match(/^\s*(\d+)\s*$/);
    if (bareNum) {
      result.passengers = parseInt(bareNum[1]);
    } else {
      const bareWord = text.match(/^\s*(one|two|three|four|five|six|seven|eight|nine|ten)\s*$/i);
      if (bareWord) {
        result.passengers = wordToNum[bareWord[1].toLowerCase()];
      }
    }
  }
  
  // Time extraction
  if (/\b(now|asap|straight\s*away|immediately)\b/i.test(lower)) {
    result.pickup_time = "ASAP";
  } else {
    const timeMatch = text.match(/\b(?:at|for|around)\s+(\d{1,2}(?::\d{2})?\s*(?:am|pm)?)/i);
    if (timeMatch) {
      result.pickup_time = timeMatch[1];
    }
    const inMinsMatch = text.match(/\bin\s+(\d+)\s*(minutes?|mins?|hours?)/i);
    if (inMinsMatch) {
      result.pickup_time = `in ${inMinsMatch[1]} ${inMinsMatch[2]}`;
    }
  }
  
  // Luggage detection
  if (/\b(luggage|bags?|suitcases?|cases?)\b/i.test(lower)) {
    result.luggage = !/\b(no\s+luggage|without\s+luggage)\b/i.test(lower);
  }
  
  // Vehicle type
  if (/\b(estate|bigger|larger|saloon|exec|executive)\b/i.test(lower)) {
    const vehicleMatch = lower.match(/\b(estate|saloon|exec|executive)\b/);
    result.vehicle_type = vehicleMatch?.[1];
  }
  
  // Name extraction (when asked "what's your name?")
  const nameMatch = text.match(/\b(?:my name is|i'm|it's|call me)\s+([A-Z][a-z]+)/i);
  if (nameMatch) {
    result.customer_name = nameMatch[1];
  } else {
    // Bare name response
    const bareName = text.match(/^([A-Z][a-z]+)$/);
    if (bareName) {
      result.customer_name = bareName[1];
    }
  }
  
  return result;
}

// ============================================================================
// ADA RESPONSE GENERATION (SIMPLE)
// ============================================================================

async function generateAdaResponse(
  extracted: ExtractedData,
  sessionState: Record<string, unknown>,
  conversationHistory: Array<{ role: string; text: string }>,
): Promise<string> {
  // Build context from session state
  const collected: string[] = [];
  if (sessionState.pickup) collected.push(`pickup: ${sessionState.pickup}`);
  if (sessionState.destination) collected.push(`destination: ${sessionState.destination}`);
  if (sessionState.passengers) collected.push(`passengers: ${sessionState.passengers}`);
  if (sessionState.pickup_time) collected.push(`time: ${sessionState.pickup_time}`);
  
  const prompt = `You are Ada, a friendly taxi booking assistant. Keep responses SHORT (1-2 sentences max).

Current booking details collected:
${collected.length > 0 ? collected.join("\n") : "None yet"}

Customer just said: "${extracted.user_text}"

Extracted data:
- Intent: ${extracted.intent || "unclear"}
- Pickup: ${extracted.pickup || "not provided"}
- Destination: ${extracted.destination || "not provided"}
- Passengers: ${extracted.passengers || "not provided"}
- Time: ${extracted.pickup_time || "not provided"}
- Confirmed: ${extracted.confirmed ? "yes" : "no"}

Instructions:
1. If they gave new info (pickup, destination, passengers), acknowledge briefly and ask for the next missing field
2. If you have pickup, destination, and passengers, summarize and ask for confirmation
3. If they confirmed, say you're booking it now
4. Keep it natural and friendly - don't be robotic
5. NEVER repeat addresses back in full - just acknowledge with "Got it" or "Lovely"

Respond as Ada (1-2 sentences only):`;

  try {
    const response = await fetch("https://api.openai.com/v1/chat/completions", {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${OPENAI_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "gpt-4o-mini",
        messages: [
          { role: "system", content: "You are Ada, a concise and friendly taxi booking assistant." },
          ...conversationHistory.map(m => ({ role: m.role as "user" | "assistant", content: m.text })),
          { role: "user", content: prompt },
        ],
        max_tokens: 100,
        temperature: 0.7,
      }),
    });
    
    const data = await response.json();
    return data.choices?.[0]?.message?.content || "I'm here to help with your taxi booking.";
  } catch (error) {
    console.error("Ada response generation error:", error);
    return "I'm here to help with your taxi booking.";
  }
}

// ============================================================================
// TTS (OPTIONAL)
// ============================================================================

async function synthesizeSpeech(text: string, voice: string = "aura-asteria-en"): Promise<string | null> {
  const DEEPGRAM_API_KEY = Deno.env.get("DEEPGRAM_API_KEY");
  if (!DEEPGRAM_API_KEY) return null;
  
  try {
    const response = await fetch(`https://api.deepgram.com/v1/speak?model=${voice}&encoding=linear16&sample_rate=16000`, {
      method: "POST",
      headers: {
        "Authorization": `Token ${DEEPGRAM_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ text }),
    });
    
    if (!response.ok) return null;
    
    const audioBuffer = await response.arrayBuffer();
    const base64 = btoa(String.fromCharCode(...new Uint8Array(audioBuffer)));
    return base64;
  } catch (error) {
    console.error("TTS error:", error);
    return null;
  }
}

// ============================================================================
// STT - gpt-4o-mini-transcribe (PRIMARY) with Deepgram fallback
// ============================================================================

async function transcribeAudio(audioBase64: string, sampleRate: number = 16000): Promise<string> {
  const DEEPGRAM_API_KEY = Deno.env.get("DEEPGRAM_API_KEY");
  
  // Decode base64 to binary
  const binaryString = atob(audioBase64);
  const bytes = new Uint8Array(binaryString.length);
  for (let i = 0; i < binaryString.length; i++) {
    bytes[i] = binaryString.charCodeAt(i);
  }
  
  // Create WAV file for OpenAI
  const wavHeader = createWavHeader(bytes.length, sampleRate, 1, 16);
  const wavFile = new Uint8Array(wavHeader.length + bytes.length);
  wavFile.set(wavHeader, 0);
  wavFile.set(bytes, wavHeader.length);
  
  // Try gpt-4o-mini-transcribe first (OpenAI)
  try {
    const prompt = `Transcribe speech into clear English.
Preserve UK place names, road names, and business names.
Convert spoken numbers to digits.
Focus on taxi booking details.
Return one clear sentence.`;

    const formData = new FormData();
    formData.append("file", new Blob([wavFile], { type: "audio/wav" }), "audio.wav");
    formData.append("model", "gpt-4o-mini-transcribe");
    formData.append("prompt", prompt);
    formData.append("temperature", "0");
    
    const response = await fetch("https://api.openai.com/v1/audio/transcriptions", {
      method: "POST",
      headers: { "Authorization": `Bearer ${OPENAI_API_KEY}` },
      body: formData,
    });
    
    if (response.ok) {
      const data = await response.json();
      const text = data.text || "";
      console.log(`[STT] gpt-4o-mini-transcribe: "${text}"`);
      
      // Quality gate - reject hallucinations
      if (isValidTranscript(text)) {
        return text;
      }
      console.log(`[STT] Quality gate failed, trying fallback...`);
    } else {
      const errorText = await response.text();
      console.error(`[STT] gpt-4o-mini-transcribe error: ${response.status} - ${errorText}`);
    }
  } catch (error) {
    console.error("[STT] gpt-4o-mini-transcribe exception:", error);
  }
  
  // Fallback to Deepgram Nova-2 Phone Call (telephony-optimized)
  if (DEEPGRAM_API_KEY) {
    try {
      // Keyword boosting for taxi booking context
      const keywords = [
        "cancel:2", "book it:2", "yes:1.5", "no:1.5",
        "Coventry:1.5", "Birmingham:1.5", "Manchester:1.5",
        "David Road:1.5", "Sweet Spot:1.8", "airport:1.5"
      ].join("&keywords=");
      
      const response = await fetch(`https://api.deepgram.com/v1/listen?model=nova-2-phonecall&language=en-GB&punctuate=true&smart_format=true&numerals=true&keywords=${keywords}`, {
        method: "POST",
        headers: {
          "Authorization": `Token ${DEEPGRAM_API_KEY}`,
          "Content-Type": `audio/raw;encoding=linear16;sample_rate=${sampleRate};channels=1`,
        },
        body: bytes,
      });
      
      if (response.ok) {
        const data = await response.json();
        const text = data.results?.channels?.[0]?.alternatives?.[0]?.transcript || "";
        console.log(`[STT] Deepgram fallback: "${text}"`);
        return text;
      }
    } catch (error) {
      console.error("[STT] Deepgram fallback error:", error);
    }
  }
  
  return "";
}

// Quality gate - reject hallucinations and noise
function isValidTranscript(text: string): boolean {
  if (!text || text.trim().length < 3) return false;
  
  const lower = text.toLowerCase().trim();
  
  // Whisper/GPT phantom phrases
  const phantomPhrases = [
    "thank you for watching",
    "thanks for watching",
    "please like and subscribe",
    "subscribe to my channel",
    "subtitles by",
    "[music]",
    "[applause]",
    "[laughter]",
  ];
  
  if (phantomPhrases.some(p => lower.includes(p))) {
    return false;
  }
  
  // Repetition loop detection (e.g., "you you you you")
  const words = lower.split(/\s+/).filter(w => w.length > 0);
  if (words.length >= 3) {
    const uniqueWords = new Set(words);
    const mostCommonCount = Math.max(...[...uniqueWords].map(w => words.filter(x => x === w).length));
    if (mostCommonCount >= words.length * 0.8) {
      return false;
    }
  }
  
  // Must have at least 2 words for meaningful speech
  return words.length >= 2;
}

function createWavHeader(dataLength: number, sampleRate: number, channels: number, bitsPerSample: number): Uint8Array {
  const byteRate = sampleRate * channels * (bitsPerSample / 8);
  const blockAlign = channels * (bitsPerSample / 8);
  const header = new ArrayBuffer(44);
  const view = new DataView(header);
  
  // "RIFF"
  view.setUint8(0, 0x52); view.setUint8(1, 0x49); view.setUint8(2, 0x46); view.setUint8(3, 0x46);
  view.setUint32(4, 36 + dataLength, true);
  // "WAVE"
  view.setUint8(8, 0x57); view.setUint8(9, 0x41); view.setUint8(10, 0x56); view.setUint8(11, 0x45);
  // "fmt "
  view.setUint8(12, 0x66); view.setUint8(13, 0x6D); view.setUint8(14, 0x74); view.setUint8(15, 0x20);
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, byteRate, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, bitsPerSample, true);
  // "data"
  view.setUint8(36, 0x64); view.setUint8(37, 0x61); view.setUint8(38, 0x74); view.setUint8(39, 0x61);
  view.setUint32(40, dataLength, true);
  
  return new Uint8Array(header);
}

// ============================================================================
// MAIN HANDLER
// ============================================================================

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  // Health check
  if (req.method === "GET") {
    return new Response(JSON.stringify({ status: "ok", mode: "passthrough" }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
  
  try {
    const body: PassthroughRequest = await req.json();
    const {
      call_id,
      caller_phone,
      caller_name,
      audio_base64,
      text_input,
      webhook_url,
      webhook_token,
      session_state = {},
      forced_response,
      tts_voice,
    } = body;
    
    console.log(`[${call_id}] Passthrough request received`);
    
    // Get user text (from audio or direct input)
    let userText = text_input || "";
    if (audio_base64 && !text_input) {
      console.log(`[${call_id}] Transcribing audio...`);
      userText = await transcribeAudio(audio_base64);
      console.log(`[${call_id}] Transcribed: "${userText}"`);
    }
    
    if (!userText && !forced_response) {
      return new Response(JSON.stringify({ 
        ok: false, 
        error: "No input provided (audio_base64 or text_input required)" 
      }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
    
    // Extract data from user's speech
    const extracted = userText ? await extractFromText(userText) : {
      user_text: "",
      intent: "unclear" as const,
    };
    
    console.log(`[${call_id}] Extracted:`, JSON.stringify(extracted));
    
    // Update session state with new info
    const updatedState = { ...session_state };
    if (extracted.pickup) updatedState.pickup = extracted.pickup;
    if (extracted.destination) updatedState.destination = extracted.destination;
    if (extracted.passengers) updatedState.passengers = extracted.passengers;
    if (extracted.pickup_time) updatedState.pickup_time = extracted.pickup_time;
    if (extracted.luggage !== undefined) updatedState.luggage = extracted.luggage;
    if (extracted.vehicle_type) updatedState.vehicle_type = extracted.vehicle_type;
    if (extracted.customer_name) updatedState.customer_name = extracted.customer_name;
    
    // Build conversation history from session
    const conversationHistory: Array<{ role: "user" | "assistant"; text: string; timestamp: string }> = 
      (session_state.conversation_history as Array<{ role: "user" | "assistant"; text: string; timestamp: string }>) || [];
    
    if (userText) {
      conversationHistory.push({
        role: "user",
        text: userText,
        timestamp: new Date().toISOString(),
      });
    }
    
    // Generate Ada's response (or use forced response from webhook)
    let adaText = forced_response || "";
    if (!adaText) {
      adaText = await generateAdaResponse(extracted, updatedState, conversationHistory);
    }
    
    console.log(`[${call_id}] Ada: "${adaText}"`);
    
    conversationHistory.push({
      role: "assistant",
      text: adaText,
      timestamp: new Date().toISOString(),
    });
    
    updatedState.conversation_history = conversationHistory;
    
    // Generate TTS if requested
    let audioBase64Output: string | undefined;
    if (tts_voice || audio_base64) {
      audioBase64Output = await synthesizeSpeech(adaText, tts_voice) || undefined;
    }
    
    const adaResponse: AdaResponse = {
      text: adaText,
      audio_base64: audioBase64Output,
    };
    
    // Build webhook payload
    const webhookPayload: WebhookPayload = {
      call_id,
      caller_phone,
      caller_name,
      timestamp: new Date().toISOString(),
      user: extracted,
      ada: adaResponse,
      session_state: updatedState,
      conversation: conversationHistory,
    };
    
    console.log(`[${call_id}] Sending to webhook: ${webhook_url}`);
    
    // Send to external webhook
    let webhookResponse: WebhookResponse = {};
    try {
      const webhookHeaders: Record<string, string> = {
        "Content-Type": "application/json",
      };
      if (webhook_token) {
        webhookHeaders["Authorization"] = `Bearer ${webhook_token}`;
      }
      
      const webhookResult = await fetch(webhook_url, {
        method: "POST",
        headers: webhookHeaders,
        body: JSON.stringify(webhookPayload),
      });
      
      if (webhookResult.ok) {
        const responseText = await webhookResult.text();
        if (responseText) {
          try {
            webhookResponse = JSON.parse(responseText);
            console.log(`[${call_id}] Webhook response:`, JSON.stringify(webhookResponse));
          } catch {
            console.log(`[${call_id}] Webhook returned non-JSON: ${responseText}`);
          }
        }
      } else {
        console.warn(`[${call_id}] Webhook error: ${webhookResult.status}`);
      }
    } catch (error) {
      console.error(`[${call_id}] Webhook call failed:`, error);
    }
    
    // If webhook provided a response override, use it
    let finalAdaText = adaText;
    let finalAudioBase64 = audioBase64Output;
    
    if (webhookResponse.ada_response) {
      finalAdaText = webhookResponse.ada_response;
      finalAudioBase64 = await synthesizeSpeech(finalAdaText, tts_voice) || undefined;
    } else if (webhookResponse.ada_question) {
      finalAdaText = webhookResponse.ada_question;
      finalAudioBase64 = await synthesizeSpeech(finalAdaText, tts_voice) || undefined;
    } else if (webhookResponse.end_call && webhookResponse.end_message) {
      finalAdaText = webhookResponse.end_message;
      finalAudioBase64 = await synthesizeSpeech(finalAdaText, tts_voice) || undefined;
    }
    
    // Merge session state from webhook
    const finalState = { ...updatedState, ...(webhookResponse.session_state || {}) };
    
    // Return response
    return new Response(JSON.stringify({
      ok: true,
      call_id,
      
      // What the user said
      user: extracted,
      
      // What Ada will say
      ada: {
        text: finalAdaText,
        audio_base64: finalAudioBase64,
      },
      
      // Session state for next call
      session_state: finalState,
      
      // Webhook validation results (if any)
      validation: webhookResponse.validation,
      
      // End call signal
      end_call: webhookResponse.end_call,
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
    
  } catch (error) {
    console.error("Passthrough error:", error);
    return new Response(JSON.stringify({ 
      ok: false, 
      error: error instanceof Error ? error.message : "Internal error" 
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
