import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Voice-optimized system prompt - CONDENSED FLOW
const SYSTEM_INSTRUCTIONS = `You are Ada, a British taxi dispatcher for "247 Radio Carz".

STYLE: Brief, warm. Say "Lovely" or "Got it" — NEVER repeat back addresses. Move to next question immediately.

RULES:
1. NEVER invent info the customer didn't say
2. If they say "yes/okay" for passengers — ask again: "How many passengers?"
3. If unsure, ask them to repeat

COLLECT (in order):
1. Pickup → acknowledge briefly, ask destination
2. Destination → acknowledge briefly, ask passengers
3. Passengers → book immediately (NO confirmation question)

For AIRPORT/STATION: also ask luggage count before booking.

CRITICAL - DO NOT:
- Repeat addresses back ("So that's 52 David Road..." ❌)
- Ask "shall I book?" — just book when you have all details
- Summarize at the end — customer knows what they said

AFTER BOOKING: "Booked! [X] mins, [FARE]." Nothing more. If goodbye: "Safe travels!"

JSON: {"response":"msg","pickup":"addr|null","destination":"addr|null","passengers":num|null,"bags":num|null,"booking_complete":false}`;

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
  
  const GROQ_API_KEY = Deno.env.get("GROQ_API_KEY"); // For Groq Whisper STT
  const DEEPGRAM_API_KEY = Deno.env.get("DEEPGRAM_API_KEY"); // For Deepgram Nova STT & Aura TTS
  const ELEVENLABS_API_KEY = Deno.env.get("ELEVENLABS_API_KEY"); // For ElevenLabs TTS
  const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY"); // For Gemini
  const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
  const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  
  let callId = `gemini-${Date.now()}`;
  let callStartAt = new Date().toISOString();
  let callSource = "web";
  let userPhone = "";
  let callerName = "";
  let sessionReady = false;
  let sttProvider = "groq"; // Default to Groq, can be "groq" or "deepgram"
  let ttsProvider = "elevenlabs"; // Default to ElevenLabs, can be "elevenlabs" or "deepgram"
  
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
  let isAiTalking = false; // Track if AI is currently speaking
  
  // VAD Configuration - can be updated via session.update
  let vadConfig = {
    type: "server_vad",
    threshold: 0.5,           // Sensitivity (0.0 to 1.0) - lower = more sensitive
    prefix_padding_ms: 300,   // Keep audio before speech start
    silence_duration_ms: 600, // Silence before considering turn complete
    semantic_endpointing: true // Use grammar-based sentence completion
  };
  
  // Semantic endpointing patterns - detect complete sentences
  const SENTENCE_END_PATTERNS = [
    /\b(please|thanks|thank you|cheers|ta)\s*[.!?]?\s*$/i,
    /\b(that's (it|all|right|correct)|yes|no|yeah|nah|okay|ok)\s*[.!?]?\s*$/i,
    /\d+\s+(passengers?|people|bags?|luggage|minutes?)\s*[.!?]?\s*$/i,
    /\b(road|street|lane|avenue|drive|way|close|court|place|crescent)\s*[.!?]?\s*$/i,
    /\b(station|airport|hospital|centre|center|mall|park|school)\s*[.!?]?\s*$/i,
    /[.!?]\s*$/,  // Ends with punctuation
  ];
  
  // Incomplete sentence patterns - wait for more
  const INCOMPLETE_PATTERNS = [
    /\b(I'm at|I am at|from|to|going to|heading to|need to go|pick me up)\s*$/i,
    /\b(number|house|flat|apartment)\s*$/i,
    /\b(and|but|or|so|then|also|wait|um|uh|er)\s*$/i,
    /^\s*\d+\s*$/,  // Just a number, might be part of address
  ];
  
  // Conversation history for context
  let conversationHistory: { role: string; content: string }[] = [];
  
  // Last assistant response for Whisper prompt conditioning
  let lastAssistantResponse = "";

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

  // Step 1: STT - Convert audio to text using Groq Whisper or Deepgram Nova
  const transcribeAudio = async (audioData: Uint8Array): Promise<string> => {
    const startTime = Date.now();
    const provider = sttProvider === "deepgram" && DEEPGRAM_API_KEY ? "Deepgram" : "Groq";
    const hasContext = !!lastAssistantResponse;
    console.log(`[${callId}] 🎤 STT (${provider}${hasContext ? '+ctx' : ''}): Transcribing ${audioData.length} bytes...`);
    
    try {
      // Convert PCM16 to WAV format
      const wavHeader = createWavHeader(audioData.length, 24000, 1, 16);
      const wavData = new Uint8Array(wavHeader.length + audioData.length);
      wavData.set(wavHeader, 0);
      wavData.set(audioData, wavHeader.length);
      
      let resultText = "";
      
      if (sttProvider === "deepgram" && DEEPGRAM_API_KEY) {
        // Deepgram Nova-2 - optimized for telephony and UK accents
        // CRITICAL: Add keyword boosting for taxi commands and UK locations
        // Also add dynamic keywords from Ada's last response for context
        const baseKeywords = [
          "cancel:2", "cancel it:2", "cancel the booking:2", "keep it:2", "book it:2",
          "yes please:1.5", "no thanks:1.5", "that's right:1.5", "that's correct:1.5", "yes:1.5", "no:1.5", "yeah:1.5",
          "Coventry:1.5", "Birmingham:1.5", "Solihull:1.5", "Wolverhampton:1.5", "Manchester:1.5",
          "School Road:1.5", "David Road:1.5", "Station Road:1.5", "High Street:1.5",
          "Birmingham Airport:1.5", "Heathrow:1.5", "Gatwick:1.5", "Manchester Airport:1.5"
        ];
        
        // DYNAMIC CONTEXT: Extract key terms from Ada's last response to boost recognition
        if (lastAssistantResponse) {
          // Boost postcodes mentioned by Ada (e.g., "M18 7RH" -> "M18:2", "7RH:2")
          const postcodeMatches = lastAssistantResponse.match(/[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}/gi);
          if (postcodeMatches) {
            postcodeMatches.forEach(pc => {
              baseKeywords.push(`${pc}:2`);
              // Also add parts separately
              const parts = pc.split(/\s+/);
              parts.forEach(part => baseKeywords.push(`${part}:1.8`));
            });
          }
          // Boost addresses/places mentioned (basic extraction)
          const addressMatch = lastAssistantResponse.match(/\d+[A-Z]?\s+\w+\s+(?:Road|Street|Lane|Avenue|Drive)/gi);
          if (addressMatch) {
            addressMatch.forEach(addr => baseKeywords.push(`${addr}:1.8`));
          }
        }
        
        const keywords = baseKeywords.join("&keywords=");
        
        // Remove language lock for multilingual support, add keywords
        const response = await fetch(`https://api.deepgram.com/v1/listen?model=nova-2&punctuate=true&smart_format=true&keywords=${keywords}`, {
          method: "POST",
          headers: {
            Authorization: `Token ${DEEPGRAM_API_KEY}`,
            "Content-Type": "audio/wav",
          },
          body: wavData,
        });
        
        if (!response.ok) {
          const errorText = await response.text();
          console.error(`[${callId}] Deepgram error:`, response.status, errorText);
          throw new Error(`Deepgram error: ${response.status}`);
        }
        
        const result = await response.json();
        resultText = result.results?.channels?.[0]?.alternatives?.[0]?.transcript || "";
        
      } else {
        // Groq Whisper (default)
        const formData = new FormData();
        formData.append("file", new Blob([wavData], { type: "audio/wav" }), "audio.wav");
        formData.append("model", "whisper-large-v3-turbo");
        formData.append("response_format", "json");
        formData.append("temperature", "0"); // CRITICAL: Temperature=0 prevents creative hallucinations
        // NO language lock - auto-detect for multilingual support
        // NO prompt conditioning - let Whisper transcribe naturally without context bias
        
        const response = await fetch("https://api.groq.com/openai/v1/audio/transcriptions", {
          method: "POST",
          headers: { Authorization: `Bearer ${GROQ_API_KEY}` },
          body: formData,
        });
        
        if (!response.ok) {
          const errorText = await response.text();
          console.error(`[${callId}] Groq Whisper error:`, response.status, errorText);
          throw new Error(`Groq Whisper error: ${response.status}`);
        }
        
        const result = await response.json();
        resultText = result.text || "";
      }
      
      const sttLatency = logTiming(`STT (${provider})`, startTime);
      console.log(`[${callId}] 🎤 STT result: "${resultText}"`);
      
      // Send STT latency to client
      socket.send(JSON.stringify({ 
        type: "latency.stt", 
        latency_ms: sttLatency,
        provider: provider.toLowerCase(),
        text: resultText 
      }));
      
      return resultText;
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
          temperature: 0.5, // Lower = more focused, less hallucination
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
      
      // Store for Whisper prompt conditioning (dynamic context for next STT)
      lastAssistantResponse = parsed.response;
      
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

  // Step 3: TTS - Convert text to speech using ElevenLabs or Deepgram Aura
  const synthesizeSpeech = async (text: string): Promise<Uint8Array | null> => {
    const startTime = Date.now();
    const provider = ttsProvider === "deepgram" && DEEPGRAM_API_KEY ? "Deepgram" : "ElevenLabs";
    console.log(`[${callId}] 🔊 TTS (${provider}): Synthesizing "${text.substring(0, 50)}..."`);
    
    try {
      let audioBuffer: ArrayBuffer;
      
      if (ttsProvider === "deepgram" && DEEPGRAM_API_KEY) {
        // Deepgram Aura TTS - British female voice (Athena), linear16 PCM output
        const response = await fetch(
          "https://api.deepgram.com/v1/speak?model=aura-athena-en&encoding=linear16&sample_rate=24000",
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
          const errorText = await response.text();
          console.error(`[${callId}] Deepgram Aura error:`, response.status, errorText);
          throw new Error(`Deepgram Aura error: ${response.status}`);
        }
        
        audioBuffer = await response.arrayBuffer();
      } else {
        // ElevenLabs TTS (default)
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
        
        audioBuffer = await response.arrayBuffer();
      }
      
      const ttsLatency = logTiming(`TTS (${provider})`, startTime);
      
      console.log(`[${callId}] 🔊 TTS generated: ${audioBuffer.byteLength} bytes`);
      
      // Send TTS latency to client
      socket.send(JSON.stringify({ 
        type: "latency.tts", 
        latency_ms: ttsLatency,
        provider: provider.toLowerCase(),
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
      
      // Whisper needs ~0.5-1s of audio for reliable transcription
      // At 24kHz PCM16 (2 bytes/sample), 1s = 48000 bytes
      // For short utterances like "yes", "hello", "no" - pad with silence
      const MIN_AUDIO_BYTES = 48000; // 1 second minimum for reliable Whisper
      let audioToProcess = combinedAudio;
      
      if (totalLength < MIN_AUDIO_BYTES) {
        console.log(`[${callId}] 🔇 Audio short (${totalLength}), padding with silence to ${MIN_AUDIO_BYTES} bytes`);
        // Create padded audio with silence before and after
        const paddedAudio = new Uint8Array(MIN_AUDIO_BYTES);
        // Center the actual audio in the middle, silence on both sides
        const startOffset = Math.floor((MIN_AUDIO_BYTES - totalLength) / 2);
        paddedAudio.set(combinedAudio, startOffset);
        audioToProcess = paddedAudio;
      }
      
      // Pipeline: STT → LLM → TTS
      const transcript = await transcribeAudio(audioToProcess);
      
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
        isAiTalking = true; // Mark AI as speaking
        
        // Send audio in chunks (PCM16 format, same as OpenAI Realtime)
        const chunkSize = 4800; // 100ms of audio at 24kHz
        for (let i = 0; i < audioData.length; i += chunkSize) {
          // Check if interrupted during playback
          if (!isAiTalking) {
            console.log(`[${callId}] 🛑 Audio playback interrupted at chunk ${i/chunkSize}`);
            break;
          }
          
          const chunk = audioData.slice(i, Math.min(i + chunkSize, audioData.length));
          const base64 = btoa(String.fromCharCode(...chunk));
          
          socket.send(JSON.stringify({
            type: "audio.delta",
            delta: base64
          }));
        }
        
        isAiTalking = false; // Mark AI as done speaking
        socket.send(JSON.stringify({ type: "audio.done" }));
        
        // Check if Ada said goodbye - end the call
        const goodbyePatterns = /\b(goodbye|bye|take care|have a (great|lovely|good) (day|journey|trip))\b/i;
        if (goodbyePatterns.test(aiResponse.response)) {
          console.log(`[${callId}] 👋 Ada said goodbye, ending call...`);
          // Give time for audio to play, then signal call end
          setTimeout(() => {
            socket.send(JSON.stringify({ type: "session.end", reason: "goodbye" }));
          }, 3000); // 3 second delay for audio playback
        }
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

  // Calculate RMS energy from PCM audio (for VAD)
  const calculateEnergy = (pcmData: Uint8Array): number => {
    const int16 = new Int16Array(pcmData.buffer, pcmData.byteOffset, pcmData.length / 2);
    let sum = 0;
    for (let i = 0; i < int16.length; i++) {
      sum += int16[i] * int16[i];
    }
    return Math.sqrt(sum / int16.length) / 32768; // Normalize to 0-1
  };
  
  // Check if transcript appears to be a complete sentence
  const isSemanticallyCom = (text: string): boolean => {
    if (!vadConfig.semantic_endpointing) return true; // Skip if disabled
    
    const trimmed = text.trim();
    if (trimmed.length < 2) return false;
    
    // Check for incomplete patterns first
    for (const pattern of INCOMPLETE_PATTERNS) {
      if (pattern.test(trimmed)) {
        console.log(`[${callId}] 🔄 Incomplete sentence detected: "${trimmed}"`);
        return false;
      }
    }
    
    // Check for complete sentence patterns
    for (const pattern of SENTENCE_END_PATTERNS) {
      if (pattern.test(trimmed)) {
        return true;
      }
    }
    
    // Default: if text is > 15 chars, assume complete
    return trimmed.length > 15;
  };

  // Handle incoming audio data with VAD
  const handleAudioData = (base64Audio: string) => {
    try {
      const binaryString = atob(base64Audio);
      const bytes = new Uint8Array(binaryString.length);
      for (let i = 0; i < binaryString.length; i++) {
        bytes[i] = binaryString.charCodeAt(i);
      }
      
      // Calculate energy for VAD
      const energy = calculateEnergy(bytes);
      const isSpeech = energy > vadConfig.threshold * 0.1; // Scale threshold
      
      // Barge-in detection: if user speaks while AI is talking
      if (isSpeech && isAiTalking) {
        console.log(`[${callId}] 🛑 Barge-in detected! Interrupting AI...`);
        isAiTalking = false;
        socket.send(JSON.stringify({ type: "audio.interrupted" }));
        // Clear any pending audio
        audioBuffer = [];
        if (silenceTimer) clearTimeout(silenceTimer);
      }
      
      audioBuffer.push(bytes);
      
      // Reset silence timer
      if (silenceTimer) {
        clearTimeout(silenceTimer);
      }
      
      // Start silence timer - process after configured silence duration
      silenceTimer = setTimeout(() => {
        processAudioPipeline();
      }, vadConfig.silence_duration_ms);
      
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
          sttProvider = msg.stt_provider || "groq"; // "groq" or "deepgram"
          ttsProvider = msg.tts_provider || "elevenlabs"; // "elevenlabs" or "deepgram"
          console.log(`[${callId}] 📞 Session start - source: ${callSource}, phone: ${userPhone}, STT: ${sttProvider}, TTS: ${ttsProvider}`);
          
          // Lookup caller
          if (userPhone) {
            await lookupCaller(userPhone);
          }
          
          sessionReady = true;
          socket.send(JSON.stringify({ type: "session_ready", pipeline: "gemini", stt_provider: sttProvider, tts_provider: ttsProvider }));
          
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
          
        case "session.update":
          // Update VAD/turn detection config dynamically
          if (msg.session?.turn_detection) {
            const td = msg.session.turn_detection;
            if (td.type) vadConfig.type = td.type;
            if (td.threshold !== undefined) vadConfig.threshold = td.threshold;
            if (td.prefix_padding_ms !== undefined) vadConfig.prefix_padding_ms = td.prefix_padding_ms;
            if (td.silence_duration_ms !== undefined) vadConfig.silence_duration_ms = td.silence_duration_ms;
            if (td.semantic_endpointing !== undefined) vadConfig.semantic_endpointing = td.semantic_endpointing;
            
            console.log(`[${callId}] ⚙️ VAD config updated:`, vadConfig);
          }
          
          // Acknowledge the update
          socket.send(JSON.stringify({
            type: "session.updated",
            session: {
              turn_detection: vadConfig,
              input_audio_format: "pcm16",
              output_audio_format: "pcm16"
            }
          }));
          break;
          
        case "conversation.item.truncate":
          // Barge-in: stop AI speech immediately
          console.log(`[${callId}] 🛑 Truncate request - stopping AI speech`);
          isAiTalking = false;
          audioBuffer = [];
          socket.send(JSON.stringify({ type: "audio.interrupted" }));
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
