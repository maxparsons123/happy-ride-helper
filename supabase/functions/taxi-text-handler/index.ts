/**
 * Taxi Text Handler - Edge Function for Local STT Architecture
 * 
 * Receives text transcripts from the Python bridge (which does local STT via OpenAI Realtime),
 * processes booking logic, generates AI response, and returns TTS audio.
 * 
 * Flow:
 *   1. Receive: { call_id, phone, text, transcripts }
 *   2. Process booking logic (extract pickup/destination/passengers)
 *   3. Generate AI response using Lovable AI
 *   4. Synthesize TTS audio
 *   5. Return: { assistant_text, audio (base64 PCM16 @ 24kHz) }
 */

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Environment
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";
const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY") || "";
const DEEPGRAM_API_KEY = Deno.env.get("DEEPGRAM_API_KEY") || "";

// System prompt for taxi booking
const SYSTEM_PROMPT = `You are Ada, a friendly and professional AI assistant for a taxi booking service.

Your job is to help callers book a taxi. You need to collect:
1. Pickup location (where they are now)
2. Destination (where they want to go)
3. Number of passengers

Keep responses SHORT and conversational - this is a phone call, not a chatbot.

Rules:
- Ask ONE question at a time
- Confirm details before booking
- Be warm but efficient
- If unclear, ask for clarification
- Use natural speech patterns

Example responses:
- "Hi there! Where can I pick you up from?"
- "Great, and where are you heading?"
- "How many passengers?"
- "Perfect! So that's from [pickup] to [destination] for [n] passengers. Shall I book that for you?"`;

interface RequestPayload {
  call_id: string;
  phone: string;
  text: string;
  transcripts: Array<{ role: "user" | "assistant"; text: string }>;
}

interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  confirmed: boolean;
}

// Extract booking details from conversation
function extractBookingDetails(transcripts: Array<{ role: string; text: string }>): BookingState {
  const state: BookingState = {
    pickup: null,
    destination: null,
    passengers: null,
    confirmed: false,
  };
  
  // Simple extraction - in production use LLM extraction
  const allText = transcripts.map(t => t.text.toLowerCase()).join(" ");
  
  // Look for passenger count
  const passengerMatch = allText.match(/(\d+)\s*(?:passenger|people|person|of us)/i);
  if (passengerMatch) {
    state.passengers = parseInt(passengerMatch[1], 10);
  }
  
  // Check for confirmation
  if (allText.includes("yes") && allText.includes("book")) {
    state.confirmed = true;
  }
  
  return state;
}

// Generate AI response using Lovable AI
async function generateResponse(
  userText: string,
  transcripts: Array<{ role: string; text: string }>,
  bookingState: BookingState
): Promise<string> {
  if (!LOVABLE_API_KEY) {
    console.error("LOVABLE_API_KEY not configured");
    return "I'm sorry, I'm having trouble right now. Please try again.";
  }
  
  // Build conversation history
  const messages = [
    { role: "system", content: SYSTEM_PROMPT },
    ...transcripts.map(t => ({
      role: t.role as "user" | "assistant",
      content: t.text,
    })),
    { role: "user", content: userText },
  ];
  
  try {
    const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${LOVABLE_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "google/gemini-2.5-flash",
        messages,
        max_tokens: 150, // Keep responses short for phone
        temperature: 0.7,
      }),
    });
    
    if (!response.ok) {
      console.error("Lovable AI error:", response.status);
      return "I'm sorry, could you repeat that?";
    }
    
    const data = await response.json();
    return data.choices?.[0]?.message?.content || "I didn't catch that. Could you say that again?";
    
  } catch (error) {
    console.error("AI generation error:", error);
    return "I'm having trouble understanding. Could you repeat that?";
  }
}

// Synthesize TTS audio using Deepgram
async function synthesizeSpeech(text: string): Promise<Uint8Array | null> {
  if (!DEEPGRAM_API_KEY) {
    console.error("DEEPGRAM_API_KEY not configured");
    return null;
  }
  
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
      console.error("Deepgram TTS error:", response.status);
      return null;
    }
    
    const audioBuffer = await response.arrayBuffer();
    return new Uint8Array(audioBuffer);
    
  } catch (error) {
    console.error("TTS synthesis error:", error);
    return null;
  }
}

// Update live_calls in Supabase
async function updateLiveCall(
  callId: string,
  phone: string,
  transcripts: Array<{ role: string; text: string }>,
  bookingState: BookingState
): Promise<void> {
  if (!SUPABASE_URL || !SUPABASE_SERVICE_ROLE_KEY) return;
  
  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
  
  try {
    await supabase.from("live_calls").upsert({
      call_id: callId,
      caller_phone: phone,
      status: bookingState.confirmed ? "confirmed" : "active",
      pickup: bookingState.pickup,
      destination: bookingState.destination,
      passengers: bookingState.passengers,
      transcripts,
      updated_at: new Date().toISOString(),
      source: "local-stt",
    }, {
      onConflict: "call_id",
    });
  } catch (error) {
    console.error("Failed to update live_calls:", error);
  }
}

// Main handler
Deno.serve(async (req) => {
  // CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  // Health check
  if (req.method === "GET") {
    return new Response(JSON.stringify({ status: "ok", service: "taxi-text-handler" }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
  
  try {
    const payload: RequestPayload = await req.json();
    const { call_id, phone, text, transcripts = [] } = payload;
    
    console.log(`[${call_id}] 🎤 User: ${text}`);
    
    // Extract booking state from conversation
    const bookingState = extractBookingDetails([...transcripts, { role: "user", text }]);
    
    // Generate AI response
    const assistantText = await generateResponse(text, transcripts, bookingState);
    console.log(`[${call_id}] 🤖 Ada: ${assistantText}`);
    
    // Synthesize TTS
    const ttsAudio = await synthesizeSpeech(assistantText);
    
    // Update database
    const updatedTranscripts = [
      ...transcripts,
      { role: "user", text },
      { role: "assistant", text: assistantText },
    ];
    await updateLiveCall(call_id, phone, updatedTranscripts, bookingState);
    
    // Return response
    return new Response(
      JSON.stringify({
        assistant_text: assistantText,
        audio: ttsAudio ? btoa(String.fromCharCode(...ttsAudio)) : null,
        booking_state: bookingState,
      }),
      {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      }
    );
    
  } catch (error) {
    console.error("Handler error:", error);
    return new Response(
      JSON.stringify({ error: "Internal server error" }),
      {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      }
    );
  }
});
