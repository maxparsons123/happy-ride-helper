import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY")!;
const DEEPGRAM_API_KEY = Deno.env.get("DEEPGRAM_API_KEY");

// ============================================================================
// CONFIGURATION
// ============================================================================

const TELEPHONY_SAMPLE_RATE = 8000; // Native telephony rate (µ-law from SIP)
const TTS_SAMPLE_RATE = 24000; // TTS output rate for Deepgram Aura
const VAD_SILENCE_MS = 800; // Silence threshold for end of speech
const VAD_MIN_SPEECH_MS = 200; // Minimum speech duration
const VAD_ENERGY_THRESHOLD = 0.008; // RMS energy threshold (adjusted for 8kHz)
const MAX_AUDIO_BUFFER_MS = 30000; // Max 30 seconds of audio buffer

// ============================================================================
// TYPES
// ============================================================================

interface SessionState {
  call_id: string;
  caller_phone?: string;
  caller_name?: string;
  webhook_url?: string;
  webhook_token?: string;
  session_state: Record<string, unknown>;
  conversation_history: Array<{ role: "user" | "assistant"; text: string; timestamp: string }>;
}

interface WebhookPayload {
  call_id: string;
  caller_phone?: string;
  caller_name?: string;
  timestamp: string;
  transcript: string;
  booking: {
    pickup?: string;
    destination?: string;
    passengers?: number;
    pickup_time?: string;
    intent?: string;
    confirmed?: boolean;
  };
  session_state: Record<string, unknown>;
  conversation: Array<{ role: string; text: string }>;
}

interface WebhookResponse {
  ada_response?: string;
  ada_question?: string;
  end_call?: boolean;
  end_message?: string;
  session_state?: Record<string, unknown>;
}

// ============================================================================
// AUDIO UTILITIES
// ============================================================================

// µ-law decode table (pre-computed for speed)
const ULAW_DECODE_TABLE = new Int16Array(256);
for (let i = 0; i < 256; i++) {
  const mulaw = ~i;
  const sign = (mulaw & 0x80) !== 0 ? -1 : 1;
  const exponent = (mulaw >> 4) & 0x07;
  const mantissa = mulaw & 0x0F;
  ULAW_DECODE_TABLE[i] = sign * (((mantissa << 3) + 0x84) << exponent) - 0x84;
}

function mulawDecode(ulawData: Uint8Array): Int16Array {
  const pcm = new Int16Array(ulawData.length);
  for (let i = 0; i < ulawData.length; i++) {
    pcm[i] = ULAW_DECODE_TABLE[ulawData[i]];
  }
  return pcm;
}

function calculateRMS(samples: Int16Array): number {
  if (samples.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < samples.length; i++) {
    const normalized = samples[i] / 32768;
    sum += normalized * normalized;
  }
  return Math.sqrt(sum / samples.length);
}

function bytesToInt16(bytes: Uint8Array): Int16Array {
  const samples = new Int16Array(bytes.length / 2);
  for (let i = 0; i < samples.length; i++) {
    samples[i] = bytes[i * 2] | (bytes[i * 2 + 1] << 8);
  }
  return samples;
}

function int16ToBytes(samples: Int16Array): Uint8Array {
  const bytes = new Uint8Array(samples.length * 2);
  for (let i = 0; i < samples.length; i++) {
    bytes[i * 2] = samples[i] & 0xff;
    bytes[i * 2 + 1] = (samples[i] >> 8) & 0xff;
  }
  return bytes;
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
// SIMPLE VAD (Voice Activity Detection)
// ============================================================================

class SimpleVAD {
  private audioBuffer: Uint8Array[] = [];
  private totalBytes = 0;
  private speechStartAt = 0;
  private lastSpeechAt = 0;
  private isSpeaking = false;
  
  private readonly sampleRate: number;
  private readonly silenceMs: number;
  private readonly minSpeechMs: number;
  private readonly energyThreshold: number;
  private readonly maxBufferMs: number;
  
  constructor(
    sampleRate = TELEPHONY_SAMPLE_RATE,
    silenceMs = VAD_SILENCE_MS,
    minSpeechMs = VAD_MIN_SPEECH_MS,
    energyThreshold = VAD_ENERGY_THRESHOLD,
    maxBufferMs = MAX_AUDIO_BUFFER_MS
  ) {
    this.sampleRate = sampleRate;
    this.silenceMs = silenceMs;
    this.minSpeechMs = minSpeechMs;
    this.energyThreshold = energyThreshold;
    this.maxBufferMs = maxBufferMs;
  }
  
  addAudio(chunk: Uint8Array, isUlaw = true): { utteranceComplete: boolean; audio?: Uint8Array } {
    const now = Date.now();
    // Decode µ-law to PCM for energy calculation
    const samples = isUlaw ? mulawDecode(chunk) : bytesToInt16(chunk);
    const rms = calculateRMS(samples);
    const hasSpeech = rms > this.energyThreshold;
    
    // Track speech state
    if (hasSpeech) {
      if (!this.isSpeaking) {
        this.speechStartAt = now;
        this.isSpeaking = true;
      }
      this.lastSpeechAt = now;
    }
    
    // Add to buffer
    this.audioBuffer.push(chunk);
    this.totalBytes += chunk.length;
    
    // Trim buffer if too large
    const maxBytes = (this.sampleRate * 2 * this.maxBufferMs) / 1000;
    while (this.totalBytes > maxBytes && this.audioBuffer.length > 1) {
      const removed = this.audioBuffer.shift()!;
      this.totalBytes -= removed.length;
    }
    
    // Check for end of utterance
    const silenceDuration = now - this.lastSpeechAt;
    const speechDuration = this.lastSpeechAt - this.speechStartAt;
    
    if (this.isSpeaking && silenceDuration >= this.silenceMs && speechDuration >= this.minSpeechMs) {
      // Utterance complete
      const audio = this.flush();
      return { utteranceComplete: true, audio };
    }
    
    return { utteranceComplete: false };
  }
  
  flush(): Uint8Array {
    if (this.audioBuffer.length === 0) {
      return new Uint8Array(0);
    }
    
    const combined = new Uint8Array(this.totalBytes);
    let offset = 0;
    for (const chunk of this.audioBuffer) {
      combined.set(chunk, offset);
      offset += chunk.length;
    }
    
    this.reset();
    return combined;
  }
  
  reset() {
    this.audioBuffer = [];
    this.totalBytes = 0;
    this.speechStartAt = 0;
    this.lastSpeechAt = 0;
    this.isSpeaking = false;
  }
}

// ============================================================================
// STT (Speech-to-Text) - Native 8kHz µ-law telephony mode
// ============================================================================

async function transcribeAudio(audioBytes: Uint8Array, isUlaw = true): Promise<string> {
  // Deepgram Nova-2 Phone Call is optimized for 8kHz telephony audio
  // Send native µ-law directly - no upsampling needed!
  if (DEEPGRAM_API_KEY) {
    try {
      // Keyword boosting for taxi booking context
      const keywords = [
        "cancel:2", "book it:2", "yes:1.5", "no:1.5",
        "Coventry:1.5", "Birmingham:1.5", "Manchester:1.5",
        "David Road:1.5", "Sweet Spot:1.8", "airport:1.5",
        "saloon:1.5", "estate:1.5", "passengers:1.5"
      ].join("&keywords=");
      
      // Use native telephony format - what nova-2-phonecall was trained on
      const encoding = isUlaw ? "mulaw" : "linear16";
      const sampleRate = TELEPHONY_SAMPLE_RATE;
      
      console.log(`[STT] Sending ${audioBytes.length} bytes as ${encoding}@${sampleRate}Hz to Deepgram`);
      
      const response = await fetch(
        `https://api.deepgram.com/v1/listen?model=nova-2-phonecall&language=en-GB&punctuate=true&smart_format=true&numerals=true&keywords=${keywords}`,
        {
          method: "POST",
          headers: {
            "Authorization": `Token ${DEEPGRAM_API_KEY}`,
            "Content-Type": `audio/raw;encoding=${encoding};sample_rate=${sampleRate};channels=1`,
          },
          body: audioBytes as unknown as BodyInit,
        }
      );
      
      if (response.ok) {
        const data = await response.json();
        const text = data.results?.channels?.[0]?.alternatives?.[0]?.transcript || "";
        console.log(`[STT] Deepgram nova-2-phonecall: "${text}"`);
        
        if (isValidTranscript(text)) {
          return text;
        }
      } else {
        console.error(`[STT] Deepgram error: ${response.status}`);
      }
    } catch (error) {
      console.error("[STT] Deepgram error:", error);
    }
  }
  
  // Fallback to OpenAI Whisper - needs WAV with PCM
  try {
    // Convert µ-law to PCM for Whisper
    const pcmSamples = isUlaw ? mulawDecode(audioBytes) : bytesToInt16(audioBytes);
    const pcmBytes = int16ToBytes(pcmSamples);
    
    const wavHeader = createWavHeader(pcmBytes.length, TELEPHONY_SAMPLE_RATE, 1, 16);
    const wavFile = new Uint8Array(wavHeader.length + pcmBytes.length);
    wavFile.set(wavHeader, 0);
    wavFile.set(pcmBytes, wavHeader.length);
    
    const formData = new FormData();
    formData.append("file", new Blob([wavFile], { type: "audio/wav" }), "audio.wav");
    formData.append("model", "gpt-4o-mini-transcribe");
    formData.append("temperature", "0");
    
    const response = await fetch("https://api.openai.com/v1/audio/transcriptions", {
      method: "POST",
      headers: { "Authorization": `Bearer ${OPENAI_API_KEY}` },
      body: formData,
    });
    
    if (response.ok) {
      const data = await response.json();
      const text = data.text || "";
      console.log(`[STT] Whisper fallback: "${text}"`);
      return text;
    }
  } catch (error) {
    console.error("[STT] Whisper error:", error);
  }
  
  return "";
}

function isValidTranscript(text: string): boolean {
  if (!text || text.trim().length < 3) return false;
  
  const lower = text.toLowerCase().trim();
  const phantomPhrases = [
    "thank you for watching",
    "please like and subscribe",
    "subscribe to my channel",
    "[music]",
    "[applause]",
    "you can bring them",
    "bring them apples",
  ];
  
  if (phantomPhrases.some(p => lower.includes(p))) return false;
  
  const words = lower.split(/\s+/).filter(w => w.length > 0);
  return words.length >= 2;
}

// Determine expected response type based on Ada's last statement
function getExpectedResponseType(adaText: string): string {
  const t = adaText.toLowerCase();
  if (/keep.*change.*cancel|keep it.*cancel|would you like to (keep|cancel)|active booking/.test(t)) return "booking_choice";
  if (/would you like to book|book a new|book another|instead\?/.test(t)) return "yes_no";
  if (/where.*pick|pickup.*address|pick you up from|where.*from|where.*going|destination|drop.*off|where to/i.test(t)) return "address";
  if (/how many (passengers|people)|passengers.*there/i.test(t)) return "number";
  if (/when.*need|what time|for now or later|asap or/i.test(t)) return "time";
  if (/postcode|post code|zip|area code/i.test(t)) return "postcode";
  if (/shall i book|want me to book|is that correct|confirm|right\?|ok\?|okay\?/i.test(t)) return "yes_no";
  if (/anything else|else i can help|else for you/i.test(t)) return "farewell_or_request";
  if (/\?$/.test(t.trim())) return "question";
  return "unknown";
}

// Check if transcript is semantically coherent with expected response
function isCoherentResponse(transcript: string, expectedType: string): boolean {
  const t = transcript.trim().toLowerCase();
  const wordCount = t.split(/\s+/).length;
  
  // Short transcripts (1-3 words) need stricter validation
  if (wordCount <= 3) {
    const universallyValid = /^(yes|no|yeah|yep|nope|nah|please|okay|ok|cancel|asap|now|one|two|three|four|five|six|seven|eight|nine|ten|\d+)$/i;
    if (universallyValid.test(t)) return true;
    
    switch (expectedType) {
      case "booking_choice":
        return /\b(keep|cancel|change|yes|no)\b/i.test(t);
      case "yes_no":
        return /\b(yes|no|yeah|yep|nope|nah|please|correct|right|sure|okay|ok|can i|i would|i'd like)\b/i.test(t);
      case "address":
        return /\b(street|road|avenue|drive|lane|close|way|place|court|terrace|station|airport|hospital|hotel|home|work|centre|center|\d+)\b/i.test(t);
      case "number":
        return /\b\d+\b|^(one|two|three|four|five|six|seven|eight|nine|ten|just me|myself)$/i.test(t);
      case "time":
        return /\b(now|asap|later|today|tomorrow|morning|afternoon|evening|\d+)\b/i.test(t);
      default:
        // Filter obvious nonsense
        return !/\b(apples?|oranges?|bananas?|bring them|can bring)\b/i.test(t);
    }
  }
  
  // Longer transcripts - check for obvious nonsense phrases
  const nonsensePhrases = [
    "you can bring",
    "bring them apples",
    "bring them oranges",
    "can bring them",
  ];
  
  if (nonsensePhrases.some(p => t.includes(p))) {
    console.warn(`[Coherence] Rejected nonsense phrase: "${t}"`);
    return false;
  }
  
  return true;
}

// ============================================================================
// TTS (Text-to-Speech)
// ============================================================================

async function synthesizeSpeech(text: string, voice = "aura-asteria-en"): Promise<Uint8Array | null> {
  if (!DEEPGRAM_API_KEY) return null;
  
  try {
    // TTS outputs at 24kHz for quality, bridge will downsample to 8kHz
    const response = await fetch(
      `https://api.deepgram.com/v1/speak?model=${voice}&encoding=linear16&sample_rate=${TTS_SAMPLE_RATE}`,
      {
        method: "POST",
        headers: {
          "Authorization": `Token ${DEEPGRAM_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ text }),
      }
    );
    
    if (!response.ok) {
      console.error("[TTS] Deepgram error:", response.status);
      return null;
    }
    
    const buffer = await response.arrayBuffer();
    return new Uint8Array(buffer);
  } catch (error) {
    console.error("[TTS] Error:", error);
    return null;
  }
}

// ============================================================================
// EXTRACTION
// ============================================================================

function extractFromText(text: string): {
  pickup?: string;
  destination?: string;
  passengers?: number;
  pickup_time?: string;
  intent?: string;
  confirmed?: boolean;
} {
  const lower = text.toLowerCase();
  const result: any = {};
  
  // Intent
  if (/\b(book|taxi|cab|ride|pick\s*up)\b/i.test(lower)) {
    result.intent = "booking";
  } else if (/\b(cancel)\b/i.test(lower)) {
    result.intent = "cancel";
  } else if (/\b(hello|hi|hey)\b/i.test(lower)) {
    result.intent = "greeting";
  } else if (/\b(bye|goodbye|thanks|cheers)\b/i.test(lower)) {
    result.intent = "goodbye";
  } else {
    result.intent = "booking"; // Default to booking
  }
  
  // Confirmation
  result.confirmed = /\b(yes|yeah|yep|correct|confirm|book it|go ahead)\b/i.test(lower);
  
  // Pickup: "from X"
  const fromMatch = text.match(/\bfrom\s+(.+?)(?:\s+(?:to|going)\b|$)/i);
  if (fromMatch?.[1]) {
    result.pickup = fromMatch[1].trim().replace(/[.,]+$/, "");
  }
  
  // Destination: "to X"
  const toMatch = text.match(/\b(?:to|going\s+to)\s+(.+?)(?:\s+(?:for|with|please)\b|[.,]|$)/i);
  if (toMatch?.[1]) {
    result.destination = toMatch[1].trim().replace(/[.,]+$/, "");
  }
  
  // Passengers
  const wordToNum: Record<string, number> = {
    one: 1, two: 2, three: 3, four: 4, five: 5,
    six: 6, seven: 7, eight: 8,
  };
  
  const passMatch = text.match(/\b(\d+)\s*(?:passengers?|people)\b/i);
  if (passMatch) {
    result.passengers = parseInt(passMatch[1]);
  } else {
    const wordMatch = text.match(/\b(one|two|three|four|five|six|seven|eight)\s*(?:passengers?|people)\b/i);
    if (wordMatch) {
      result.passengers = wordToNum[wordMatch[1].toLowerCase()];
    } else if (/\bjust\s+me\b/i.test(lower)) {
      result.passengers = 1;
    }
  }
  
  // Time
  if (/\b(now|asap|immediately)\b/i.test(lower)) {
    result.pickup_time = "ASAP";
  }
  
  return result;
}

// ============================================================================
// MAIN HANDLER
// ============================================================================

serve(async (req) => {
  const { headers } = req;
  const upgradeHeader = headers.get("upgrade") || "";
  
  // WebSocket upgrade
  if (upgradeHeader.toLowerCase() === "websocket") {
    const { socket, response } = Deno.upgradeWebSocket(req);
    
    let session: SessionState = {
      call_id: `ws-${Date.now()}`,
      session_state: {},
      conversation_history: [],
    };
    
    const vad = new SimpleVAD(TELEPHONY_SAMPLE_RATE);
    let isAiTalking = false;
    
    console.log(`[${session.call_id}] WebSocket connection opened`);
    
    socket.onmessage = async (event) => {
      try {
        // Binary audio data
        if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
          const audioBytes = event.data instanceof ArrayBuffer 
            ? new Uint8Array(event.data) 
            : event.data;
          
          // Skip processing if AI is talking (prevent echo)
          if (isAiTalking) return;
          
          // Feed audio to VAD (native 8kHz µ-law)
          const { utteranceComplete, audio } = vad.addAudio(audioBytes, true); // true = µ-law input
          
          if (utteranceComplete && audio && audio.length > 0) {
            console.log(`[${session.call_id}] 🎤 Utterance complete: ${audio.length} bytes (8kHz µ-law)`);
            
            // Transcribe - send native µ-law directly to Deepgram
            const transcript = await transcribeAudio(audio, true); // true = µ-law
            
            if (transcript) {
              console.log(`[${session.call_id}] 📝 Transcript: "${transcript}"`);
              
              // SEMANTIC COHERENCE CHECK: Get Ada's last response and validate
              const lastAdaResponse = [...session.conversation_history]
                .reverse()
                .find(m => m.role === "assistant")?.text || "";
              const expectedType = getExpectedResponseType(lastAdaResponse);
              
              if (!isCoherentResponse(transcript, expectedType)) {
                console.warn(`[${session.call_id}] ⚠️ Incoherent response rejected: "${transcript}" (expected: ${expectedType})`);
                return; // Skip processing this utterance
              }
              
              // Send transcript to client
              socket.send(JSON.stringify({
                type: "transcript",
                role: "user",
                text: transcript,
              }));
              
              // Extract booking info
              const booking = extractFromText(transcript);
              
              // Update session state
              if (booking.pickup) session.session_state.pickup = booking.pickup;
              if (booking.destination) session.session_state.destination = booking.destination;
              if (booking.passengers) session.session_state.passengers = booking.passengers;
              if (booking.pickup_time) session.session_state.pickup_time = booking.pickup_time;
              
              // Add to conversation
              session.conversation_history.push({
                role: "user",
                text: transcript,
                timestamp: new Date().toISOString(),
              });
              
              // Send to webhook
              if (session.webhook_url) {
                const webhookPayload: WebhookPayload = {
                  call_id: session.call_id,
                  caller_phone: session.caller_phone,
                  caller_name: session.caller_name,
                  timestamp: new Date().toISOString(),
                  transcript,
                  booking,
                  session_state: session.session_state,
                  conversation: session.conversation_history.map(c => ({
                    role: c.role,
                    text: c.text,
                  })),
                };
                
                console.log(`[${session.call_id}] 📤 Sending to webhook: ${session.webhook_url}`);
                
                try {
                  const webhookHeaders: Record<string, string> = {
                    "Content-Type": "application/json",
                  };
                  if (session.webhook_token) {
                    webhookHeaders["Authorization"] = `Bearer ${session.webhook_token}`;
                  }
                  
                  const webhookResult = await fetch(session.webhook_url, {
                    method: "POST",
                    headers: webhookHeaders,
                    body: JSON.stringify(webhookPayload),
                  });
                  
                  if (webhookResult.ok) {
                    const responseText = await webhookResult.text();
                    if (responseText) {
                      try {
                        const webhookResponse: WebhookResponse = JSON.parse(responseText);
                        console.log(`[${session.call_id}] 📥 Webhook response:`, webhookResponse);
                        
                        // Merge session state
                        if (webhookResponse.session_state) {
                          session.session_state = {
                            ...session.session_state,
                            ...webhookResponse.session_state,
                          };
                        }
                        
                        // Get Ada's response
                        let adaText = webhookResponse.ada_response || webhookResponse.ada_question || "";
                        
                        if (webhookResponse.end_call && webhookResponse.end_message) {
                          adaText = webhookResponse.end_message;
                        }
                        
                        if (adaText) {
                          console.log(`[${session.call_id}] 🗣️ Ada: "${adaText}"`);
                          
                          // Add to conversation
                          session.conversation_history.push({
                            role: "assistant",
                            text: adaText,
                            timestamp: new Date().toISOString(),
                          });
                          
                          // Send transcript
                          socket.send(JSON.stringify({
                            type: "transcript",
                            role: "assistant",
                            text: adaText,
                          }));
                          
                          // TTS and send audio
                          isAiTalking = true;
                          socket.send(JSON.stringify({ type: "ai_speaking", speaking: true }));
                          
                          const audioBytes = await synthesizeSpeech(adaText);
                          if (audioBytes) {
                            // Send as binary chunks
                            const CHUNK_SIZE = 4800; // 100ms at 24kHz
                            for (let i = 0; i < audioBytes.length; i += CHUNK_SIZE) {
                              const chunk = audioBytes.slice(i, i + CHUNK_SIZE);
                              socket.send(chunk);
                              // Small delay for pacing
                              await new Promise(r => setTimeout(r, 20));
                            }
                          }
                          
                          isAiTalking = false;
                          socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
                          
                          // End call if requested
                          if (webhookResponse.end_call) {
                            socket.send(JSON.stringify({
                              type: "call_ended",
                              reason: "webhook",
                            }));
                            socket.close();
                          }
                        }
                      } catch {
                        console.error(`[${session.call_id}] Webhook response parse error`);
                      }
                    }
                  } else {
                    console.error(`[${session.call_id}] Webhook error: ${webhookResult.status}`);
                  }
                } catch (error) {
                  console.error(`[${session.call_id}] Webhook call failed:`, error);
                }
              } else {
                // No webhook configured - send raw transcript
                socket.send(JSON.stringify({
                  type: "booking_update",
                  call_id: session.call_id,
                  transcript,
                  booking,
                  session_state: session.session_state,
                }));
              }
            }
          }
          return;
        }
        
        // JSON messages
        const message = JSON.parse(event.data);
        
        if (message.type === "init") {
          session.call_id = message.call_id || session.call_id;
          session.caller_phone = message.caller_phone || message.user_phone;
          session.caller_name = message.caller_name;
          session.webhook_url = message.webhook_url;
          session.webhook_token = message.webhook_token;
          
          console.log(`[${session.call_id}] 📞 Session initialized - phone: ${session.caller_phone}, webhook: ${session.webhook_url || "none"}`);
          
          socket.send(JSON.stringify({
            type: "session_ready",
            call_id: session.call_id,
          }));
          
          // Send greeting if no webhook (standalone mode)
          if (!session.webhook_url) {
            const greeting = "Hello, welcome to the taxi booking service. Where would you like to go?";
            
            session.conversation_history.push({
              role: "assistant",
              text: greeting,
              timestamp: new Date().toISOString(),
            });
            
            socket.send(JSON.stringify({
              type: "transcript",
              role: "assistant",
              text: greeting,
            }));
            
            isAiTalking = true;
            socket.send(JSON.stringify({ type: "ai_speaking", speaking: true }));
            
            const audioBytes = await synthesizeSpeech(greeting);
            if (audioBytes) {
              const CHUNK_SIZE = 4800;
              for (let i = 0; i < audioBytes.length; i += CHUNK_SIZE) {
                socket.send(audioBytes.slice(i, i + CHUNK_SIZE));
                await new Promise(r => setTimeout(r, 20));
              }
            }
            
            isAiTalking = false;
            socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
          }
        }
        
        if (message.type === "text") {
          // Direct text input (for testing)
          const text = message.text;
          console.log(`[${session.call_id}] 📝 Text input: "${text}"`);
          
          // Process as if transcribed
          // (Same flow as audio transcription above)
        }
        
      } catch (error) {
        console.error(`[${session.call_id}] Message error:`, error);
      }
    };
    
    socket.onclose = () => {
      console.log(`[${session.call_id}] 📴 WebSocket closed`);
    };
    
    socket.onerror = (error) => {
      console.error(`[${session.call_id}] WebSocket error:`, error);
    };
    
    return response;
  }
  
  // Non-WebSocket: health check
  return new Response(
    JSON.stringify({
      status: "ok",
      service: "taxi-passthrough-ws",
      description: "WebSocket endpoint for taxi audio streaming",
      usage: {
        websocket: "Connect via WebSocket",
        init: {
          type: "init",
          call_id: "unique-call-id",
          caller_phone: "+447123456789",
          webhook_url: "https://your-server.com/webhook",
          webhook_token: "optional-auth-token",
        },
        audio: "Send raw PCM16 24kHz audio as binary WebSocket frames",
        responses: {
          transcript: "{ type: 'transcript', role: 'user'|'assistant', text: '...' }",
          audio: "Binary PCM16 24kHz audio frames",
          ai_speaking: "{ type: 'ai_speaking', speaking: true|false }",
        },
      },
    }),
    {
      headers: { "Content-Type": "application/json" },
    }
  );
});
