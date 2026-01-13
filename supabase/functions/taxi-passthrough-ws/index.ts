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
  // Address comparison: STT-extracted vs Ada's interpretation
  address_sources: {
    stt: {
      pickup?: string;
      destination?: string;
    };
    ada: {
      pickup?: string;
      destination?: string;
    };
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
  // Ada's confirmed/interpreted addresses (for comparison tracking)
  ada_pickup?: string;
  ada_destination?: string;
}

// ============================================================================
// AUDIO UTILITIES
// ============================================================================

function int16ToBytes(samples: Int16Array): Uint8Array {
  const bytes = new Uint8Array(samples.length * 2);
  for (let i = 0; i < samples.length; i++) {
    bytes[i * 2] = samples[i] & 0xff;
    bytes[i * 2 + 1] = (samples[i] >> 8) & 0xff;
  }
  return bytes;
}

// ============================================================================
// STREAMING DEEPGRAM STT
// ============================================================================

class DeepgramStreamingSTT {
  private ws: WebSocket | null = null;
  private isConnected = false;
  private isConnecting = false;
  private pendingAudio: Uint8Array[] = [];
  private currentTranscript = "";
  private finalTranscript = "";
  private lastSpeechTime = 0;
  private silenceTimer: number | null = null;
  
  private readonly callId: string;
  private readonly onTranscript: (transcript: string, isFinal: boolean) => void;
  private readonly onError: (error: string) => void;
  
  // Silence detection
  private readonly SILENCE_THRESHOLD_MS = 800; // End of utterance after 800ms silence
  
  constructor(
    callId: string,
    onTranscript: (transcript: string, isFinal: boolean) => void,
    onError: (error: string) => void
  ) {
    this.callId = callId;
    this.onTranscript = onTranscript;
    this.onError = onError;
  }
  
  async connect(): Promise<boolean> {
    if (this.isConnected || this.isConnecting) {
      console.log(`[${this.callId}] [STT] Already connected/connecting`);
      return true;
    }
    
    if (!DEEPGRAM_API_KEY) {
      this.onError("No Deepgram API key configured");
      return false;
    }
    
    this.isConnecting = true;
    const connectStart = Date.now();
    
    return new Promise((resolve) => {
      try {
        // Deepgram streaming WebSocket with keyword boosting
        const keywords = [
          "cancel:2", "book%20it:2", "yes:1.5", "no:1.5",
          "Coventry:1.5", "Birmingham:1.5", "Manchester:1.5",
          "David%20Road:1.5", "Sweet%20Spot:1.8", "airport:1.5",
          "saloon:1.5", "estate:1.5", "passengers:1.5"
        ].join("&keywords=");
        
        const url = `wss://api.deepgram.com/v1/listen?` +
          `model=nova-2-phonecall&` +
          `language=en-GB&` +
          `encoding=mulaw&` +
          `sample_rate=${TELEPHONY_SAMPLE_RATE}&` +
          `channels=1&` +
          `punctuate=true&` +
          `smart_format=true&` +
          `numerals=true&` +
          `interim_results=true&` +
          `utterance_end_ms=1000&` +
          `vad_events=true&` +
          `endpointing=300&` +
          `keywords=${keywords}`;
        
        console.log(`[${this.callId}] [STT] 🔌 Opening Deepgram streaming connection...`);
        
        this.ws = new WebSocket(url, ["token", DEEPGRAM_API_KEY]);
        
        this.ws.onopen = () => {
          const latency = Date.now() - connectStart;
          console.log(`[${this.callId}] [STT] ✅ Deepgram connected in ${latency}ms (WARMED UP)`);
          this.isConnected = true;
          this.isConnecting = false;
          
          // Send any pending audio
          if (this.pendingAudio.length > 0) {
            console.log(`[${this.callId}] [STT] Flushing ${this.pendingAudio.length} pending audio chunks`);
            for (const chunk of this.pendingAudio) {
              this.ws?.send(chunk);
            }
            this.pendingAudio = [];
          }
          
          resolve(true);
        };
        
        this.ws.onmessage = (event) => {
          try {
            const data = JSON.parse(event.data);
            
            if (data.type === "Results") {
              const transcript = data.channel?.alternatives?.[0]?.transcript || "";
              const isFinal = data.is_final === true;
              const speechFinal = data.speech_final === true;
              
              if (transcript) {
                this.lastSpeechTime = Date.now();
                
                if (isFinal) {
                  // Accumulate final transcripts
                  this.finalTranscript += (this.finalTranscript ? " " : "") + transcript;
                  console.log(`[${this.callId}] [STT] 📝 Final segment: "${transcript}"`);
                } else {
                  // Update current partial
                  this.currentTranscript = transcript;
                }
                
                // Reset silence timer
                if (this.silenceTimer) {
                  clearTimeout(this.silenceTimer);
                }
                
                // Start silence detection
                this.silenceTimer = setTimeout(() => {
                  this.handleSilence();
                }, this.SILENCE_THRESHOLD_MS);
              }
              
              // Speech final means end of utterance (Deepgram's VAD)
              if (speechFinal && this.finalTranscript) {
                console.log(`[${this.callId}] [STT] 🎤 Speech final: "${this.finalTranscript}"`);
                this.onTranscript(this.finalTranscript.trim(), true);
                this.finalTranscript = "";
                this.currentTranscript = "";
              }
            } else if (data.type === "UtteranceEnd") {
              // Deepgram detected end of utterance
              if (this.finalTranscript) {
                console.log(`[${this.callId}] [STT] 🛑 Utterance end: "${this.finalTranscript}"`);
                this.onTranscript(this.finalTranscript.trim(), true);
                this.finalTranscript = "";
                this.currentTranscript = "";
              }
            } else if (data.type === "SpeechStarted") {
              console.log(`[${this.callId}] [STT] 🗣️ Speech started`);
            } else if (data.type === "Metadata") {
              console.log(`[${this.callId}] [STT] 📊 Metadata received`);
            }
          } catch (err) {
            console.error(`[${this.callId}] [STT] Parse error:`, err);
          }
        };
        
        this.ws.onerror = (error) => {
          console.error(`[${this.callId}] [STT] ❌ WebSocket error:`, error);
          this.isConnecting = false;
          this.isConnected = false;
          this.onError("Deepgram connection error");
          resolve(false);
        };
        
        this.ws.onclose = (event) => {
          console.log(`[${this.callId}] [STT] 📴 WebSocket closed: ${event.code} ${event.reason}`);
          this.isConnected = false;
          this.isConnecting = false;
        };
        
        // Timeout for connection
        setTimeout(() => {
          if (this.isConnecting) {
            console.error(`[${this.callId}] [STT] Connection timeout`);
            this.isConnecting = false;
            this.ws?.close();
            resolve(false);
          }
        }, 5000);
        
      } catch (error) {
        console.error(`[${this.callId}] [STT] Failed to create WebSocket:`, error);
        this.isConnecting = false;
        resolve(false);
      }
    });
  }
  
  private handleSilence() {
    // If we have accumulated transcript after silence, emit it
    if (this.finalTranscript) {
      console.log(`[${this.callId}] [STT] ⏱️ Silence detected, emitting: "${this.finalTranscript}"`);
      this.onTranscript(this.finalTranscript.trim(), true);
      this.finalTranscript = "";
      this.currentTranscript = "";
    }
  }
  
  sendAudio(audioBytes: Uint8Array) {
    if (!this.isConnected) {
      // Buffer audio until connected
      if (this.isConnecting) {
        this.pendingAudio.push(audioBytes);
      }
      return;
    }
    
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(audioBytes);
    }
  }
  
  close() {
    if (this.silenceTimer) {
      clearTimeout(this.silenceTimer);
    }
    
    // Emit any remaining transcript
    if (this.finalTranscript) {
      this.onTranscript(this.finalTranscript.trim(), true);
    }
    
    if (this.ws) {
      // Send close frame to Deepgram
      try {
        this.ws.send(JSON.stringify({ type: "CloseStream" }));
      } catch {}
      this.ws.close();
      this.ws = null;
    }
    
    this.isConnected = false;
    this.isConnecting = false;
    this.pendingAudio = [];
    this.finalTranscript = "";
    this.currentTranscript = "";
  }
  
  get connected(): boolean {
    return this.isConnected;
  }
}

// ============================================================================
// SEMANTIC COHERENCE CHECK
// ============================================================================

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

function isCoherentResponse(transcript: string, expectedType: string): boolean {
  const t = transcript.trim().toLowerCase();
  const wordCount = t.split(/\s+/).length;
  
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
        return !/\b(apples?|oranges?|bananas?|bring them|can bring)\b/i.test(t);
    }
  }
  
  const nonsensePhrases = ["you can bring", "bring them apples", "bring them oranges", "can bring them"];
  if (nonsensePhrases.some(p => t.includes(p))) {
    console.warn(`[Coherence] Rejected nonsense phrase: "${t}"`);
    return false;
  }
  
  return true;
}

function isValidTranscript(text: string): boolean {
  if (!text || text.trim().length < 2) return false;
  
  const lower = text.toLowerCase().trim();
  const phantomPhrases = [
    "thank you for watching",
    "please like and subscribe",
    "subscribe to my channel",
    "[music]",
    "[applause]",
  ];
  
  if (phantomPhrases.some(p => lower.includes(p))) return false;
  return true;
}

// ============================================================================
// RESPONSE SANITIZATION
// ============================================================================

const FORBIDDEN_PHRASES = [
  "just to double-check", "double-check", "let me confirm", "just to confirm",
  "to confirm", "is that correct", "is that right", "did i get that right",
  "did i get that correct", "shall i confirm", "confirm that", "want me to confirm",
  "confirm your booking", "confirm the booking", "so that's", "so that is",
  "so you're going from", "so i have you going", "let me read that back",
  "i'll read that back", "just to be sure", "i understand you want",
  "if i understand correctly", "let me make sure i have this right", "just to make sure",
];

function sanitizeAssistantResponse(text: string, callId: string): string {
  if (!text) return text;
  
  const lower = text.toLowerCase();
  const violations = FORBIDDEN_PHRASES.filter(phrase => lower.includes(phrase));
  
  if (violations.length > 0) {
    console.warn(`[${callId}] ⚠️ PROMPT VIOLATION: Ada said forbidden phrase(s): ${violations.join(", ")}`);
    const severeViolations = ["double-check", "shall i confirm", "confirm that", "is that correct"];
    if (severeViolations.some(v => lower.includes(v))) {
      console.warn(`[${callId}] 🔄 Replacing response with safe fallback`);
      return "Got it. I'll book that for you now.";
    }
  }
  
  const sentences = text.split(/(?<=[.!?])\s+/);
  if (sentences.length > 2) {
    const truncated = sentences.slice(0, 2).join(" ");
    console.log(`[${callId}] ✂️ Truncated from ${sentences.length} to 2 sentences`);
    return truncated;
  }
  
  return text;
}

// ============================================================================
// TTS (Text-to-Speech)
// ============================================================================

async function synthesizeSpeech(text: string, voice = "aura-asteria-en"): Promise<Uint8Array | null> {
  if (!DEEPGRAM_API_KEY) return null;
  
  try {
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
  
  if (/\b(book|taxi|cab|ride|pick\s*up)\b/i.test(lower)) {
    result.intent = "booking";
  } else if (/\b(cancel)\b/i.test(lower)) {
    result.intent = "cancel";
  } else if (/\b(hello|hi|hey)\b/i.test(lower)) {
    result.intent = "greeting";
  } else if (/\b(bye|goodbye|thanks|cheers)\b/i.test(lower)) {
    result.intent = "goodbye";
  } else {
    result.intent = "booking";
  }
  
  result.confirmed = /\b(yes|yeah|yep|correct|confirm|book it|go ahead)\b/i.test(lower);
  
  const fromMatch = text.match(/\bfrom\s+(.+?)(?:\s+(?:to|going)\b|$)/i);
  if (fromMatch?.[1]) {
    result.pickup = fromMatch[1].trim().replace(/[.,]+$/, "");
  }
  
  const toMatch = text.match(/\b(?:to|going\s+to)\s+(.+?)(?:\s+(?:for|with|please)\b|[.,]|$)/i);
  if (toMatch?.[1]) {
    result.destination = toMatch[1].trim().replace(/[.,]+$/, "");
  }
  
  const wordToNum: Record<string, number> = {
    one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8,
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
    
    let isAiTalking = false;
    let deepgramSTT: DeepgramStreamingSTT | null = null;
    let processingTranscript = false;
    
    // 🔇 ECHO GUARD: Discard audio for 400ms after Ada stops speaking
    const ECHO_GUARD_MS = 400;
    let echoGuardUntil = 0; // Timestamp until which audio should be discarded
    
    console.log(`[${session.call_id}] WebSocket connection opened`);
    
    // Handler for when we get a final transcript from Deepgram
    const handleTranscript = async (transcript: string, isFinal: boolean) => {
      if (!isFinal || processingTranscript || !isValidTranscript(transcript)) return;
      
      processingTranscript = true;
      
      try {
        console.log(`[${session.call_id}] 📝 Processing transcript: "${transcript}"`);
        
        // Semantic coherence check
        const lastAdaResponse = [...session.conversation_history]
          .reverse()
          .find(m => m.role === "assistant")?.text || "";
        const expectedType = getExpectedResponseType(lastAdaResponse);
        
        if (!isCoherentResponse(transcript, expectedType)) {
          console.warn(`[${session.call_id}] ⚠️ Incoherent response rejected: "${transcript}" (expected: ${expectedType})`);
          processingTranscript = false;
          return;
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
          // Extract Ada's addresses from session state (accumulated from previous webhook responses)
          const adaPickup = session.session_state.ada_pickup as string | undefined;
          const adaDestination = session.session_state.ada_destination as string | undefined;
          
          const webhookPayload: WebhookPayload = {
            call_id: session.call_id,
            caller_phone: session.caller_phone,
            caller_name: session.caller_name,
            timestamp: new Date().toISOString(),
            transcript,
            booking,
            address_sources: {
              stt: {
                pickup: booking.pickup,
                destination: booking.destination,
              },
              ada: {
                pickup: adaPickup,
                destination: adaDestination,
              },
            },
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
                  
                  if (webhookResponse.session_state) {
                    session.session_state = {
                      ...session.session_state,
                      ...webhookResponse.session_state,
                    };
                  }
                  
                  // Store Ada's addresses for comparison tracking
                  if (webhookResponse.ada_pickup) {
                    session.session_state.ada_pickup = webhookResponse.ada_pickup;
                  }
                  if (webhookResponse.ada_destination) {
                    session.session_state.ada_destination = webhookResponse.ada_destination;
                  }
                  
                  let adaText = webhookResponse.ada_response || webhookResponse.ada_question || "";
                  
                  if (webhookResponse.end_call && webhookResponse.end_message) {
                    adaText = webhookResponse.end_message;
                  }
                  
                  if (adaText) {
                    adaText = sanitizeAssistantResponse(adaText, session.call_id);
                  }
                  
                  if (adaText) {
                    console.log(`[${session.call_id}] 🗣️ Ada: "${adaText}"`);
                    
                    session.conversation_history.push({
                      role: "assistant",
                      text: adaText,
                      timestamp: new Date().toISOString(),
                    });
                    
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
                      const CHUNK_SIZE = 4800;
                      for (let i = 0; i < audioBytes.length; i += CHUNK_SIZE) {
                        const chunk = audioBytes.slice(i, i + CHUNK_SIZE);
                        socket.send(chunk);
                        await new Promise(r => setTimeout(r, 20));
                      }
                    }
                    
                    isAiTalking = false;
                    socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
                    
                    // 🔇 ECHO GUARD: Start discarding audio for 400ms after Ada stops
                    echoGuardUntil = Date.now() + ECHO_GUARD_MS;
                    console.log(`[${session.call_id}] 🔇 Echo guard active for ${ECHO_GUARD_MS}ms`);
                    
                    // 🔥 WARM UP STT: Open Deepgram connection as Ada finishes speaking
                    if (!webhookResponse.end_call && deepgramSTT && !deepgramSTT.connected) {
                      console.log(`[${session.call_id}] 🔥 Pre-warming Deepgram STT connection...`);
                      deepgramSTT.connect();
                    }
                    
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
          socket.send(JSON.stringify({
            type: "booking_update",
            call_id: session.call_id,
            transcript,
            booking,
            session_state: session.session_state,
          }));
        }
      } finally {
        processingTranscript = false;
      }
    };
    
    socket.onmessage = async (event) => {
      try {
        // Binary audio data
        if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
          const audioBytes = event.data instanceof ArrayBuffer 
            ? new Uint8Array(event.data) 
            : event.data;
          
          // Skip processing if AI is talking (prevent echo)
          if (isAiTalking) return;
          
          // 🔇 ECHO GUARD: Skip audio during the guard period after Ada stops
          const now = Date.now();
          if (now < echoGuardUntil) {
            // Silently discard - this is echo/residual TTS
            return;
          }
          
          // Stream directly to Deepgram (no local VAD needed!)
          if (deepgramSTT) {
            deepgramSTT.sendAudio(audioBytes);
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
          
          // Initialize Deepgram streaming STT
          deepgramSTT = new DeepgramStreamingSTT(
            session.call_id,
            handleTranscript,
            (error) => console.error(`[${session.call_id}] [STT] Error: ${error}`)
          );
          
          // 🔥 PRE-CONNECT: Start warming up Deepgram immediately!
          console.log(`[${session.call_id}] 🔥 Pre-connecting Deepgram STT...`);
          deepgramSTT.connect();
          
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
            
            // 🔇 ECHO GUARD: Start discarding audio for 400ms after Ada stops
            echoGuardUntil = Date.now() + ECHO_GUARD_MS;
          }
        }
        
        if (message.type === "text") {
          const text = message.text;
          console.log(`[${session.call_id}] 📝 Text input: "${text}"`);
          await handleTranscript(text, true);
        }
        
      } catch (error) {
        console.error(`[${session.call_id}] Message error:`, error);
      }
    };
    
    socket.onclose = () => {
      console.log(`[${session.call_id}] 📴 WebSocket closed`);
      deepgramSTT?.close();
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
      description: "WebSocket endpoint for taxi audio streaming with STREAMING STT",
      features: {
        stt: "Deepgram Nova-2 streaming WebSocket (pre-warmed)",
        tts: "Deepgram Aura",
        vad: "Deepgram server-side VAD",
      },
      usage: {
        websocket: "Connect via WebSocket",
        init: {
          type: "init",
          call_id: "unique-call-id",
          caller_phone: "+447123456789",
          webhook_url: "https://your-server.com/webhook",
          webhook_token: "optional-auth-token",
        },
        audio: "Send raw 8kHz µ-law audio as binary WebSocket frames",
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
