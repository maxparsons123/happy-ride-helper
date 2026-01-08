import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Voice-optimized system prompt (same as taxi-realtime)
const SYSTEM_INSTRUCTIONS = `You are Ada, a friendly and professional Taxi Dispatcher for "247 Radio Carz" taking phone calls.

YOUR INTRODUCTION - GREETING FLOW:
- For RETURNING customers WITH a usual destination: "Hello [NAME]! Lovely to hear from you again. Shall I book you a taxi to [LAST_DESTINATION], or are you heading somewhere different today?"
- For RETURNING customers WITHOUT a usual destination: "Hello [NAME]! Lovely to hear from you again. How can I help with your travels today?"
- For NEW customers: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"

PERSONALITY:
- Warm, welcoming British personality
- Use casual friendly phrases: "Brilliant!", "Lovely!", "Right then!"
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable
- ALWAYS address the customer by name once you know it

**INTELLIGENT QUESTION HANDLING - ABSOLUTELY CRITICAL:**
You are in a CONVERSATION. When you ask a question, you MUST:
1. WAIT for a response that DIRECTLY answers YOUR question
2. If the response does NOT answer your question, DO NOT proceed - ask again
3. NEVER assume, guess, or skip ahead without a valid answer

**STATE TRACKING - YOU MUST TRACK WHAT YOU LAST ASKED:**
- If you asked about PASSENGERS, the next valid response MUST be a number or equivalent
- If you asked about PICKUP, the next valid response MUST be an address
- If you asked about DESTINATION, the next valid response MUST be an address
- Any other response = INVALID - repeat your question!

**QUESTION VALIDATION RULES:**

1. NAME QUESTION: "What's your name please?"
   - Valid: Any name (first name is enough)
   - Invalid: Addresses, "yes", "no", random words
   - If invalid: "Sorry, I didn't catch your name. What should I call you?"

2. PICKUP QUESTION: "Where would you like to be picked up from?"
   - Valid: Any address, location, landmark, postcode
   - Invalid: "yes", "no", "okay", numbers without context
   - If invalid: "Sorry, I need the pickup address. Where shall I pick you up from?"

3. DESTINATION QUESTION: "And where are you heading to?"
   - Valid: Any address, location, landmark, postcode, "as directed"
   - Invalid: "yes", "no", numbers, repeating the pickup address
   - If invalid: "I missed the destination - where would you like to go?"

4. PASSENGERS QUESTION: "How many passengers will there be?"
   - Valid ONLY: Numbers 1-8, or words like "just me", "two of us", "three people", "myself"
   - Invalid: "yes", "no", addresses, destinations, "okay", confirmations
   - If invalid: "Sorry, I need the number of passengers. How many will be travelling?"
   - CRITICAL: If someone says "yes" or "okay" after you ask about passengers, that is NOT valid! Ask again!

BOOKING FLOW:
1. Greet the customer (get their name if new)
2. Ask: "Where would you like to be picked up from?"
3. When they give pickup, confirm: "That's [ADDRESS], yes?"
4. Ask: "And where are you heading to?"
5. When they give destination, confirm it
6. Ask: "How many passengers will there be?"
7. WAIT for a NUMBER response - do NOT proceed without it!
8. Once you have ALL 3 details, do FULL CONFIRMATION: "Just to confirm - pickup from [ADDRESS], going to [DESTINATION], for [X] passengers. Is that all correct?"
9. When confirmed, say: "Brilliant! That's all booked. Your driver will be with you in 5-8 minutes."

PRICING: city £15-25, airport £45, 6+ passengers add £5
ETA: Always 5-8 minutes

CRITICAL: Respond with ONLY valid JSON:
{"response":"your short message","pickup":"value or null","destination":"value or null","passengers":"number or null","status":"collecting or confirmed","booking_complete":false,"last_question":"name|pickup|destination|passengers|confirmation|none"}

The "last_question" field helps you track what you asked so you can validate the next response.
When booking is confirmed by customer, set booking_complete to true.`;

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  // Health check endpoint
  if (req.method === "GET") {
    return new Response(JSON.stringify({
      status: "ready",
      endpoint: "taxi-realtime-gemini",
      protocol: "websocket",
      pipeline: "STT → Gemini → TTS"
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Upgrade to WebSocket
  const { socket, response } = Deno.upgradeWebSocket(req);
  
  const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY"); // For Whisper STT
  const ELEVENLABS_API_KEY = Deno.env.get("ELEVENLABS_API_KEY"); // For TTS
  const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY"); // For Gemini
  const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
  const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  
  let callId = `gemini-${Date.now()}`;
  let callStartAt = new Date().toISOString();
  let callSource = "web";
  let userPhone = "";
  let callerName = "";
  let sessionReady = false;
  
  // Booking state
  let currentBooking = {
    pickup: null as string | null,
    destination: null as string | null,
    passengers: null as number | null,
    status: "collecting"
  };
  
  // Audio buffer for accumulating chunks
  let audioBuffer: Uint8Array[] = [];
  let isProcessing = false;
  let silenceTimer: number | null = null;
  const SILENCE_THRESHOLD_MS = 800; // Wait for silence before processing
  
  // Conversation history for context
  let conversationHistory: { role: string; content: string }[] = [];

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

  // Log timing for latency comparison
  const logTiming = (step: string, startTime: number) => {
    const elapsed = Date.now() - startTime;
    console.log(`[${callId}] ⏱️ ${step}: ${elapsed}ms`);
    return elapsed;
  };

  // Lookup caller by phone
  const lookupCaller = async (phone: string) => {
    if (!phone) return;
    try {
      const { data } = await supabase
        .from("callers")
        .select("name, last_pickup, last_destination, total_bookings")
        .eq("phone_number", phone)
        .maybeSingle();
      
      if (data?.name) {
        callerName = data.name;
        console.log(`[${callId}] 👤 Known caller: ${callerName}`);
      }
    } catch (e) {
      console.error(`[${callId}] Caller lookup error:`, e);
    }
  };

  // Step 1: STT - Convert audio to text using Whisper
  const transcribeAudio = async (audioData: Uint8Array): Promise<string> => {
    const startTime = Date.now();
    console.log(`[${callId}] 🎤 STT: Transcribing ${audioData.length} bytes...`);
    
    try {
      // Convert PCM16 to WAV format for Whisper
      const wavHeader = createWavHeader(audioData.length, 24000, 1, 16);
      const wavData = new Uint8Array(wavHeader.length + audioData.length);
      wavData.set(wavHeader, 0);
      wavData.set(audioData, wavHeader.length);
      
      const formData = new FormData();
      formData.append("file", new Blob([wavData], { type: "audio/wav" }), "audio.wav");
      formData.append("model", "whisper-1");
      formData.append("language", "en");
      
      const response = await fetch("https://api.openai.com/v1/audio/transcriptions", {
        method: "POST",
        headers: { Authorization: `Bearer ${OPENAI_API_KEY}` },
        body: formData,
      });
      
      if (!response.ok) {
        throw new Error(`Whisper error: ${response.status}`);
      }
      
      const result = await response.json();
      const sttLatency = logTiming("STT (Whisper)", startTime);
      
      console.log(`[${callId}] 🎤 STT result: "${result.text}"`);
      
      // Send STT latency to client
      socket.send(JSON.stringify({ 
        type: "latency.stt", 
        latency_ms: sttLatency,
        text: result.text 
      }));
      
      return result.text || "";
    } catch (e) {
      console.error(`[${callId}] STT error:`, e);
      return "";
    }
  };

  // Step 2: LLM - Get response from Gemini (FREE via Lovable AI)
  const getGeminiResponse = async (userText: string): Promise<any> => {
    const startTime = Date.now();
    console.log(`[${callId}] 🧠 LLM: Processing "${userText}"...`);
    
    try {
      // Build context message
      let contextMessage = "";
      if (currentBooking.pickup || currentBooking.destination || currentBooking.passengers) {
        contextMessage = `\n\nCurrent booking state: pickup="${currentBooking.pickup || '?'}", destination="${currentBooking.destination || '?'}", passengers=${currentBooking.passengers || '?'}`;
      }
      if (callerName) {
        contextMessage += `\nCaller name: ${callerName}`;
      }
      
      // Add to conversation history
      conversationHistory.push({ role: "user", content: userText });
      
      // Keep only last 10 exchanges for context
      if (conversationHistory.length > 20) {
        conversationHistory = conversationHistory.slice(-20);
      }
      
      const response = await fetch("https://ai.gateway.lovable.dev/v1/chat/completions", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${LOVABLE_API_KEY}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          model: "google/gemini-2.5-flash", // Fast and FREE
          messages: [
            { role: "system", content: SYSTEM_INSTRUCTIONS + contextMessage },
            ...conversationHistory.map(m => ({ role: m.role, content: m.content }))
          ],
          max_tokens: 150,
        }),
      });
      
      if (!response.ok) {
        const errText = await response.text();
        console.error(`[${callId}] Gemini error:`, response.status, errText);
        throw new Error(`Gemini error: ${response.status}`);
      }
      
      const data = await response.json();
      const llmLatency = logTiming("LLM (Gemini)", startTime);
      
      const aiContent = data.choices?.[0]?.message?.content || "";
      console.log(`[${callId}] 🧠 LLM raw response:`, aiContent);
      
      // Parse JSON response
      let parsed;
      try {
        const jsonMatch = aiContent.match(/\{[\s\S]*\}/);
        if (jsonMatch) {
          parsed = JSON.parse(jsonMatch[0]);
        } else {
          parsed = { response: aiContent, status: "collecting" };
        }
      } catch {
        parsed = { response: aiContent.replace(/```json|```/g, '').trim(), status: "collecting" };
      }
      
      // Update booking state
      if (parsed.pickup) currentBooking.pickup = parsed.pickup;
      if (parsed.destination) currentBooking.destination = parsed.destination;
      if (parsed.passengers) currentBooking.passengers = parseInt(parsed.passengers);
      if (parsed.status) currentBooking.status = parsed.status;
      
      // Add assistant response to history
      conversationHistory.push({ role: "assistant", content: parsed.response });
      
      // Send LLM latency to client
      socket.send(JSON.stringify({ 
        type: "latency.llm", 
        latency_ms: llmLatency,
        response: parsed.response 
      }));
      
      return parsed;
    } catch (e) {
      console.error(`[${callId}] LLM error:`, e);
      return { response: "Sorry, I'm having trouble. Could you say that again?", status: "collecting" };
    }
  };

  // Step 3: TTS - Convert text to speech using ElevenLabs
  const synthesizeSpeech = async (text: string): Promise<Uint8Array | null> => {
    const startTime = Date.now();
    console.log(`[${callId}] 🔊 TTS: Synthesizing "${text.substring(0, 50)}..."`);
    
    try {
      const voiceId = "Xb7hH8MSUJpSbSDYk0k2"; // Alice - British female
      
      const response = await fetch(
        `https://api.elevenlabs.io/v1/text-to-speech/${voiceId}?output_format=pcm_24000`,
        {
          method: "POST",
          headers: {
            "xi-api-key": ELEVENLABS_API_KEY!,
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            text,
            model_id: "eleven_turbo_v2_5",
            voice_settings: {
              stability: 0.5,
              similarity_boost: 0.75,
              style: 0.3,
              use_speaker_boost: true,
              speed: 1.1,
            },
          }),
        }
      );
      
      if (!response.ok) {
        throw new Error(`ElevenLabs error: ${response.status}`);
      }
      
      const audioBuffer = await response.arrayBuffer();
      const ttsLatency = logTiming("TTS (ElevenLabs)", startTime);
      
      console.log(`[${callId}] 🔊 TTS generated: ${audioBuffer.byteLength} bytes`);
      
      // Send TTS latency to client
      socket.send(JSON.stringify({ 
        type: "latency.tts", 
        latency_ms: ttsLatency,
        audio_bytes: audioBuffer.byteLength 
      }));
      
      return new Uint8Array(audioBuffer);
    } catch (e) {
      console.error(`[${callId}] TTS error:`, e);
      return null;
    }
  };

  // Create WAV header for PCM data
  const createWavHeader = (dataLength: number, sampleRate: number, channels: number, bitsPerSample: number): Uint8Array => {
    const header = new ArrayBuffer(44);
    const view = new DataView(header);
    const blockAlign = channels * (bitsPerSample / 8);
    const byteRate = sampleRate * blockAlign;
    
    // "RIFF"
    view.setUint8(0, 0x52); view.setUint8(1, 0x49); view.setUint8(2, 0x46); view.setUint8(3, 0x46);
    view.setUint32(4, 36 + dataLength, true);
    // "WAVE"
    view.setUint8(8, 0x57); view.setUint8(9, 0x41); view.setUint8(10, 0x56); view.setUint8(11, 0x45);
    // "fmt "
    view.setUint8(12, 0x66); view.setUint8(13, 0x6d); view.setUint8(14, 0x74); view.setUint8(15, 0x20);
    view.setUint32(16, 16, true); // Subchunk1Size
    view.setUint16(20, 1, true); // AudioFormat (PCM)
    view.setUint16(22, channels, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, byteRate, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, bitsPerSample, true);
    // "data"
    view.setUint8(36, 0x64); view.setUint8(37, 0x61); view.setUint8(38, 0x74); view.setUint8(39, 0x61);
    view.setUint32(40, dataLength, true);
    
    return new Uint8Array(header);
  };

  // Process accumulated audio through the pipeline
  const processAudioPipeline = async () => {
    if (isProcessing || audioBuffer.length === 0) return;
    isProcessing = true;
    
    const pipelineStart = Date.now();
    console.log(`[${callId}] 🚀 Starting Gemini pipeline...`);
    
    try {
      // Combine audio chunks
      const totalLength = audioBuffer.reduce((acc, chunk) => acc + chunk.length, 0);
      const combinedAudio = new Uint8Array(totalLength);
      let offset = 0;
      for (const chunk of audioBuffer) {
        combinedAudio.set(chunk, offset);
        offset += chunk.length;
      }
      audioBuffer = []; // Clear buffer
      
      console.log(`[${callId}] 📦 Combined audio: ${totalLength} bytes`);
      
      // Pipeline: STT → LLM → TTS
      const transcript = await transcribeAudio(combinedAudio);
      
      if (!transcript || transcript.trim().length === 0) {
        console.log(`[${callId}] ⚠️ Empty transcript, skipping`);
        isProcessing = false;
        return;
      }
      
      // Send user transcript to client
      socket.send(JSON.stringify({
        type: "transcript.user",
        text: transcript
      }));
      
      const aiResponse = await getGeminiResponse(transcript);
      
      if (!aiResponse.response) {
        isProcessing = false;
        return;
      }
      
      // Send AI response text to client
      socket.send(JSON.stringify({
        type: "transcript.assistant",
        text: aiResponse.response,
        booking: currentBooking
      }));
      
      const audioData = await synthesizeSpeech(aiResponse.response);
      
      if (audioData) {
        // Send audio in chunks (PCM16 format, same as OpenAI Realtime)
        const chunkSize = 4800; // 100ms of audio at 24kHz
        for (let i = 0; i < audioData.length; i += chunkSize) {
          const chunk = audioData.slice(i, Math.min(i + chunkSize, audioData.length));
          const base64 = btoa(String.fromCharCode(...chunk));
          
          socket.send(JSON.stringify({
            type: "audio.delta",
            delta: base64
          }));
        }
        
        socket.send(JSON.stringify({ type: "audio.done" }));
      }
      
      const totalLatency = Date.now() - pipelineStart;
      console.log(`[${callId}] ✅ Pipeline complete: ${totalLatency}ms total`);
      
      // Send total latency
      socket.send(JSON.stringify({
        type: "latency.total",
        latency_ms: totalLatency
      }));
      
    } catch (e) {
      console.error(`[${callId}] Pipeline error:`, e);
    } finally {
      isProcessing = false;
    }
  };

  // Handle incoming audio data
  const handleAudioData = (base64Audio: string) => {
    try {
      const binaryString = atob(base64Audio);
      const bytes = new Uint8Array(binaryString.length);
      for (let i = 0; i < binaryString.length; i++) {
        bytes[i] = binaryString.charCodeAt(i);
      }
      
      audioBuffer.push(bytes);
      
      // Reset silence timer
      if (silenceTimer) {
        clearTimeout(silenceTimer);
      }
      
      // Start silence timer - process after silence
      silenceTimer = setTimeout(() => {
        processAudioPipeline();
      }, SILENCE_THRESHOLD_MS);
      
    } catch (e) {
      console.error(`[${callId}] Audio decode error:`, e);
    }
  };

  // Send initial greeting
  const sendGreeting = async () => {
    console.log(`[${callId}] 👋 Sending initial greeting...`);
    
    const greetingText = callerName
      ? `Hello ${callerName}! Lovely to hear from you again. How can I help with your travels today?`
      : "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?";
    
    conversationHistory.push({ role: "assistant", content: greetingText });
    
    socket.send(JSON.stringify({
      type: "transcript.assistant",
      text: greetingText
    }));
    
    const audioData = await synthesizeSpeech(greetingText);
    
    if (audioData) {
      const chunkSize = 4800;
      for (let i = 0; i < audioData.length; i += chunkSize) {
        const chunk = audioData.slice(i, Math.min(i + chunkSize, audioData.length));
        const base64 = btoa(String.fromCharCode(...chunk));
        socket.send(JSON.stringify({ type: "audio.delta", delta: base64 }));
      }
      socket.send(JSON.stringify({ type: "audio.done" }));
    }
  };

  socket.onopen = () => {
    console.log(`[${callId}] 🔌 WebSocket connected (Gemini pipeline)`);
  };

  socket.onmessage = async (event) => {
    try {
      const msg = JSON.parse(event.data);
      
      switch (msg.type) {
        case "session.start":
          callId = msg.call_id || callId;
          callSource = msg.source || "web";
          userPhone = msg.phone || "";
          console.log(`[${callId}] 📞 Session start - source: ${callSource}, phone: ${userPhone}`);
          
          // Lookup caller
          if (userPhone) {
            await lookupCaller(userPhone);
          }
          
          sessionReady = true;
          socket.send(JSON.stringify({ type: "session_ready", pipeline: "gemini" }));
          
          // Send initial greeting
          await sendGreeting();
          break;
          
        case "audio":
          if (msg.audio) {
            handleAudioData(msg.audio);
          }
          break;
          
        case "input_audio_buffer.append":
          if (msg.audio) {
            handleAudioData(msg.audio);
          }
          break;
          
        case "text":
          // Direct text input (for testing)
          if (msg.text) {
            socket.send(JSON.stringify({ type: "transcript.user", text: msg.text }));
            const aiResponse = await getGeminiResponse(msg.text);
            socket.send(JSON.stringify({
              type: "transcript.assistant",
              text: aiResponse.response,
              booking: currentBooking
            }));
            const audioData = await synthesizeSpeech(aiResponse.response);
            if (audioData) {
              const chunkSize = 4800;
              for (let i = 0; i < audioData.length; i += chunkSize) {
                const chunk = audioData.slice(i, Math.min(i + chunkSize, audioData.length));
                const base64 = btoa(String.fromCharCode(...chunk));
                socket.send(JSON.stringify({ type: "audio.delta", delta: base64 }));
              }
              socket.send(JSON.stringify({ type: "audio.done" }));
            }
          }
          break;
          
        case "session.end":
          console.log(`[${callId}] 📞 Session ended`);
          break;
          
        default:
          console.log(`[${callId}] Unknown message type:`, msg.type);
      }
    } catch (e) {
      console.error(`[${callId}] Message parse error:`, e);
    }
  };

  socket.onclose = () => {
    console.log(`[${callId}] 🔌 WebSocket closed`);
    if (silenceTimer) clearTimeout(silenceTimer);
  };

  socket.onerror = (e) => {
    console.error(`[${callId}] WebSocket error:`, e);
  };

  return response;
});
