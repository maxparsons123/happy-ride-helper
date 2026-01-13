import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

/**
 * Company-Aware Passthrough WebSocket
 * 
 * Routes calls to company-specific webhook URLs based on company slug.
 * Each company can have their own dispatch system receiving real-time bookings.
 * 
 * Usage:
 *   wss://xxx.supabase.co/functions/v1/company-passthrough?company=acme-taxis
 */

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const DEEPGRAM_API_KEY = Deno.env.get("DEEPGRAM_API_KEY");

const TELEPHONY_SAMPLE_RATE = 8000;
const TTS_SAMPLE_RATE = 24000;

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// ============================================================================
// TYPES
// ============================================================================

interface Company {
  id: string;
  name: string;
  slug: string;
  webhook_url: string | null;
  is_active: boolean;
}

interface SessionState {
  call_id: string;
  company_id: string;
  company_name: string;
  caller_phone?: string;
  caller_name?: string;
  webhook_url?: string;
  session_state: Record<string, unknown>;
  conversation_history: Array<{ role: "user" | "assistant"; text: string; timestamp: string }>;
}

interface WebhookPayload {
  call_id: string;
  company_id: string;
  company_name: string;
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
  fare?: string;
  eta_minutes?: number;
  booking_ref?: string;
  passengers?: number;
  vehicle_type?: string;
  distance_miles?: number;
  booking_confirmed?: boolean;
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

// Simple µ-law to linear conversion
function ulawToLinear(ulawByte: number): number {
  ulawByte = ~ulawByte & 0xff;
  const sign = ulawByte & 0x80;
  const exponent = (ulawByte >> 4) & 0x07;
  const mantissa = ulawByte & 0x0f;
  let sample = ((mantissa << 3) + 0x84) << exponent;
  sample -= 0x84;
  return sign ? -sample : sample;
}

function convertUlawToLinear16(ulawData: Uint8Array): Int16Array {
  const linear = new Int16Array(ulawData.length);
  for (let i = 0; i < ulawData.length; i++) {
    linear[i] = ulawToLinear(ulawData[i]);
  }
  return linear;
}

// ============================================================================
// DEEPGRAM TTS
// ============================================================================

async function synthesizeSpeech(text: string): Promise<Uint8Array | null> {
  if (!DEEPGRAM_API_KEY || !text.trim()) return null;
  
  try {
    const response = await fetch(
      "https://api.deepgram.com/v1/speak?model=aura-asteria-en&encoding=linear16&sample_rate=24000",
      {
        method: "POST",
        headers: {
          Authorization: `Token ${DEEPGRAM_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ text }),
      }
    );
    
    if (!response.ok) {
      console.error(`TTS error: ${response.status}`);
      return null;
    }
    
    return new Uint8Array(await response.arrayBuffer());
  } catch (error) {
    console.error("TTS error:", error);
    return null;
  }
}

// ============================================================================
// STREAMING DEEPGRAM STT (Simplified for company routing)
// ============================================================================

class DeepgramStreamingSTT {
  private ws: WebSocket | null = null;
  private isConnected = false;
  private pendingAudio: Uint8Array[] = [];
  private currentTranscript = "";
  
  private readonly callId: string;
  private readonly onTranscript: (transcript: string, isFinal: boolean) => void;
  
  constructor(
    callId: string,
    onTranscript: (transcript: string, isFinal: boolean) => void
  ) {
    this.callId = callId;
    this.onTranscript = onTranscript;
  }
  
  async connect(): Promise<boolean> {
    if (this.isConnected) return true;
    if (!DEEPGRAM_API_KEY) return false;
    
    return new Promise((resolve) => {
      try {
        const url = `wss://api.deepgram.com/v1/listen?` +
          `model=nova-2-phonecall&language=en-GB&encoding=mulaw&` +
          `sample_rate=${TELEPHONY_SAMPLE_RATE}&channels=1&` +
          `punctuate=true&interim_results=true&endpointing=300&` +
          `vad_events=true&smart_format=true`;
        
        this.ws = new WebSocket(url, ["token", DEEPGRAM_API_KEY]);
        
        this.ws.onopen = () => {
          console.log(`[${this.callId}] [STT] Connected to Deepgram`);
          this.isConnected = true;
          
          // Flush pending audio
          for (const audio of this.pendingAudio) {
            this.ws?.send(audio);
          }
          this.pendingAudio = [];
          resolve(true);
        };
        
        this.ws.onmessage = (event) => {
          try {
            const data = JSON.parse(event.data);
            
            if (data.type === "Results" && data.channel?.alternatives?.[0]) {
              const alt = data.channel.alternatives[0];
              const transcript = alt.transcript?.trim() || "";
              const isFinal = data.is_final === true;
              
              if (transcript) {
                this.currentTranscript = transcript;
                this.onTranscript(transcript, isFinal);
              }
            }
          } catch (e) {
            // Ignore parse errors
          }
        };
        
        this.ws.onerror = () => {
          console.error(`[${this.callId}] [STT] WebSocket error`);
          resolve(false);
        };
        
        this.ws.onclose = () => {
          this.isConnected = false;
        };
        
      } catch (error) {
        console.error(`[${this.callId}] [STT] Connection error:`, error);
        resolve(false);
      }
    });
  }
  
  sendAudio(audioData: Uint8Array) {
    if (this.isConnected && this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(audioData);
    } else {
      this.pendingAudio.push(audioData);
    }
  }
  
  close() {
    if (this.ws) {
      try {
        this.ws.send(JSON.stringify({ type: "CloseStream" }));
        this.ws.close();
      } catch (e) {
        // Ignore
      }
      this.ws = null;
    }
    this.isConnected = false;
  }
}

// ============================================================================
// MAIN HANDLER
// ============================================================================

serve(async (req) => {
  // CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  const url = new URL(req.url);
  const companySlug = url.searchParams.get("company");
  
  // Check for WebSocket upgrade
  const upgradeHeader = req.headers.get("upgrade") || "";
  
  if (upgradeHeader.toLowerCase() === "websocket") {
    // Fetch company from database
    const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
    
    let company: Company | null = null;
    
    if (companySlug) {
      const { data, error } = await supabase
        .from("companies")
        .select("*")
        .eq("slug", companySlug)
        .eq("is_active", true)
        .single();
      
      if (error || !data) {
        console.error(`Company not found: ${companySlug}`);
        return new Response(JSON.stringify({ error: "Company not found" }), {
          status: 404,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }
      company = data;
    } else {
      // Default to first active company
      const { data } = await supabase
        .from("companies")
        .select("*")
        .eq("is_active", true)
        .order("name")
        .limit(1)
        .single();
      company = data;
    }
    
    if (!company) {
      return new Response(JSON.stringify({ error: "No active company found" }), {
        status: 404,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }
    
    console.log(`[company-passthrough] 🏢 Routing to company: ${company.name} (${company.slug})`);
    
    // Upgrade to WebSocket
    const { socket, response } = Deno.upgradeWebSocket(req);
    
    // Session state
    let session: SessionState = {
      call_id: `company-${Date.now()}`,
      company_id: company.id,
      company_name: company.name,
      webhook_url: company.webhook_url || undefined,
      session_state: {},
      conversation_history: [],
    };
    
    let deepgramSTT: DeepgramStreamingSTT | null = null;
    let isAiTalking = false;
    let lastTranscript = "";
    let pendingTranscriptTimer: number | null = null;
    
    // Send transcript to webhook and get response
    const handleTranscript = async (transcript: string, isFinal: boolean) => {
      if (!isFinal || !transcript.trim()) return;
      
      // Dedupe
      if (transcript === lastTranscript) return;
      lastTranscript = transcript;
      
      console.log(`[${session.call_id}] 👤 User: "${transcript}"`);
      
      // Add to conversation history
      session.conversation_history.push({
        role: "user",
        text: transcript,
        timestamp: new Date().toISOString(),
      });
      
      // Send transcript to client
      socket.send(JSON.stringify({
        type: "transcript",
        role: "user",
        text: transcript,
      }));
      
      // If company has webhook, POST to it
      if (session.webhook_url) {
        try {
          const payload: WebhookPayload = {
            call_id: session.call_id,
            company_id: session.company_id,
            company_name: session.company_name,
            caller_phone: session.caller_phone,
            caller_name: session.caller_name,
            timestamp: new Date().toISOString(),
            transcript,
            booking: {
              ...(session.session_state as any),
            },
            session_state: session.session_state,
            conversation: session.conversation_history.map(c => ({
              role: c.role,
              text: c.text,
            })),
          };
          
          console.log(`[${session.call_id}] 📤 Webhook POST to: ${session.webhook_url}`);
          
          const webhookResponse = await fetch(session.webhook_url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
          });
          
          if (webhookResponse.ok) {
            const responseData: WebhookResponse = await webhookResponse.json();
            console.log(`[${session.call_id}] 📥 Webhook response:`, responseData);
            
            // Update session state
            if (responseData.session_state) {
              session.session_state = { ...session.session_state, ...responseData.session_state };
            }
            
            // Get response text
            const responseText = responseData.ada_response || responseData.ada_question;
            
            if (responseText) {
              // Add to conversation
              session.conversation_history.push({
                role: "assistant",
                text: responseText,
                timestamp: new Date().toISOString(),
              });
              
              // Send transcript
              socket.send(JSON.stringify({
                type: "transcript",
                role: "assistant",
                text: responseText,
              }));
              
              // Synthesize and send audio
              isAiTalking = true;
              socket.send(JSON.stringify({ type: "ai_speaking", speaking: true }));
              
              const audioBytes = await synthesizeSpeech(responseText);
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
            
            // Handle end call
            if (responseData.end_call) {
              if (responseData.end_message) {
                const audioBytes = await synthesizeSpeech(responseData.end_message);
                if (audioBytes) {
                  for (let i = 0; i < audioBytes.length; i += 4800) {
                    socket.send(audioBytes.slice(i, i + 4800));
                    await new Promise(r => setTimeout(r, 20));
                  }
                }
              }
              socket.send(JSON.stringify({ type: "end_call" }));
            }
          }
        } catch (error) {
          console.error(`[${session.call_id}] Webhook error:`, error);
        }
      } else {
        // No webhook - simple echo response for testing
        const echoResponse = `I received: "${transcript}". This is ${session.company_name}.`;
        
        session.conversation_history.push({
          role: "assistant",
          text: echoResponse,
          timestamp: new Date().toISOString(),
        });
        
        socket.send(JSON.stringify({
          type: "transcript",
          role: "assistant",
          text: echoResponse,
        }));
        
        isAiTalking = true;
        socket.send(JSON.stringify({ type: "ai_speaking", speaking: true }));
        
        const audioBytes = await synthesizeSpeech(echoResponse);
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
    };
    
    socket.onopen = async () => {
      console.log(`[${session.call_id}] 🔌 WebSocket opened for ${company!.name}`);
      
      // Pre-connect to Deepgram
      deepgramSTT = new DeepgramStreamingSTT(
        session.call_id,
        (transcript, isFinal) => {
          if (isAiTalking) return;
          
          if (isFinal) {
            if (pendingTranscriptTimer) {
              clearTimeout(pendingTranscriptTimer);
              pendingTranscriptTimer = null;
            }
            handleTranscript(transcript, true);
          }
        }
      );
      
      await deepgramSTT.connect();
    };
    
    socket.onmessage = async (event) => {
      try {
        // Binary audio data
        if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
          const audioData = event.data instanceof ArrayBuffer
            ? new Uint8Array(event.data)
            : event.data;
          
          if (!isAiTalking) {
            deepgramSTT?.sendAudio(audioData);
          }
          return;
        }
        
        // JSON message
        const message = JSON.parse(event.data);
        
        if (message.type === "init") {
          session.call_id = message.call_id || session.call_id;
          session.caller_phone = message.caller_phone || message.user_phone;
          session.caller_name = message.caller_name;
          
          // Override webhook if provided
          if (message.webhook_url) {
            session.webhook_url = message.webhook_url;
          }
          
          console.log(`[${session.call_id}] 📞 Initialized for ${company!.name}, phone: ${session.caller_phone}`);
          
          // Update live_calls with company_id
          await supabase.from("live_calls").upsert({
            call_id: session.call_id,
            company_id: company!.id,
            caller_phone: session.caller_phone,
            caller_name: session.caller_name,
            status: "active",
            source: "company-passthrough",
            transcripts: [],
            started_at: new Date().toISOString(),
            updated_at: new Date().toISOString(),
          }, { onConflict: "call_id" });
          
          // Send greeting
          const greeting = `Hello! You've reached ${company!.name}. How can I help you today?`;
          
          socket.send(JSON.stringify({
            type: "transcript",
            role: "assistant",
            text: greeting,
          }));
          
          session.conversation_history.push({
            role: "assistant",
            text: greeting,
            timestamp: new Date().toISOString(),
          });
          
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
        
        if (message.type === "text") {
          await handleTranscript(message.text, true);
        }
        
      } catch (error) {
        console.error(`[${session.call_id}] Message error:`, error);
      }
    };
    
    socket.onclose = async () => {
      console.log(`[${session.call_id}] 📴 WebSocket closed`);
      deepgramSTT?.close();
      
      // Mark call as ended
      await supabase.from("live_calls").update({
        status: "completed",
        ended_at: new Date().toISOString(),
        updated_at: new Date().toISOString(),
      }).eq("call_id", session.call_id);
    };
    
    socket.onerror = (error) => {
      console.error(`[${session.call_id}] WebSocket error:`, error);
    };
    
    return response;
  }
  
  // Non-WebSocket: Return list of available companies
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
  
  const { data: companies } = await supabase
    .from("companies")
    .select("id, name, slug, webhook_url, is_active")
    .eq("is_active", true)
    .order("name");
  
  return new Response(
    JSON.stringify({
      status: "ok",
      service: "company-passthrough",
      description: "Company-aware WebSocket passthrough for taxi bookings",
      companies: companies || [],
      usage: {
        websocket: "Connect via WebSocket with ?company=<slug>",
        example: "wss://xxx.supabase.co/functions/v1/company-passthrough?company=acme-taxis",
        init: {
          type: "init",
          call_id: "unique-call-id",
          caller_phone: "+447123456789",
        },
        audio: "Send raw 8kHz µ-law audio as binary WebSocket frames",
      },
    }),
    {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    }
  );
});
