import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// === CONFIGURATION ===
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

if (!OPENAI_API_KEY) {
  console.error("❌ ERROR: OPENAI_API_KEY environment variable is not set.");
}

// === AUDIO UTILITIES ===

/**
 * Asterisk sends 8000Hz PCM16. OpenAI Realtime prefers 16000Hz or 24000Hz.
 * This doubles the samples to reach 16kHz cleanly.
 */
function upsample8to16(raw8k: Uint8Array): Uint8Array {
  const samples8k = new Int16Array(raw8k.buffer, raw8k.byteOffset, raw8k.byteLength / 2);
  const samples16k = new Int16Array(samples8k.length * 2);
  for (let i = 0; i < samples8k.length; i++) {
    samples16k[i * 2] = samples8k[i];
    samples16k[i * 2 + 1] = samples8k[i];
  }
  return new Uint8Array(samples16k.buffer);
}

/**
 * Downsample 24kHz to 8kHz for Asterisk playback (3:1 ratio)
 */
function downsample24to8(raw24k: Uint8Array): Uint8Array {
  const samples24k = new Int16Array(raw24k.buffer, raw24k.byteOffset, raw24k.byteLength / 2);
  const samples8k = new Int16Array(Math.floor(samples24k.length / 3));
  for (let i = 0; i < samples8k.length; i++) {
    // Average 3 samples for anti-aliasing
    const idx = i * 3;
    const s0 = samples24k[idx] ?? 0;
    const s1 = samples24k[idx + 1] ?? s0;
    const s2 = samples24k[idx + 2] ?? s1;
    samples8k[i] = Math.round((s0 + s1 + s2) / 3);
  }
  return new Uint8Array(samples8k.buffer);
}

/**
 * Converts Uint8Array audio to Base64 for OpenAI
 */
function arrayBufferToBase64(buffer: Uint8Array): string {
  let binary = "";
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.byteLength; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary);
}

// === SERVER HANDLER ===

serve(async (req) => {
  // Upgrade to WebSocket for the Asterisk AudioSocket bridge
  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket Upgrade", { status: 426 });
  }

  const { socket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;
  let callUuid: string | null = null;

  const log = (msg: string) => console.log(`[${callUuid?.slice(0, 8) || "init"}] ${msg}`);

  // Send text to OpenAI for TTS response
  const sendTextForTTS = (text: string) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    log(`🎤 SAY: "${text}"`);
    
    // Create a user message that prompts the assistant to speak
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: { 
        type: "message", 
        role: "user", 
        content: [{ type: "input_text", text: `[SYSTEM: Say this to the caller] ${text}` }] 
      }
    }));
    openaiWs.send(JSON.stringify({ type: "response.create" }));
  };

  // --- 1. OpenAI Connection Logic ---
  const initOpenAI = () => {
    const url = "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17";
    openaiWs = new WebSocket(url, [
      "realtime",
      `openai-insecure-api-key.${OPENAI_API_KEY}`,
      "openai-beta.realtime-v1",
    ]);

    openaiWs.onopen = () => {
      log("🟢 Connected to OpenAI Realtime API");
      
      // Configure Session with instructions for the conversation
      openaiWs?.send(JSON.stringify({
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          instructions: `You are Ada, a friendly taxi dispatcher for the Taxibot demo.

START by greeting the caller and asking for their pickup location.

BOOKING FLOW:
1. First ask for PICKUP location
2. Then ask for DESTINATION  
3. Then ask for PASSENGER COUNT
4. Then ask for TIME (now/ASAP or specific)
5. Read back the summary and ask for confirmation
6. On confirmation, say "Your taxi is booked! Driver will arrive shortly. Goodbye!"

RULES:
- Ask ONE question at a time
- Be concise and friendly
- Speak at a relaxed pace
- When user confirms, end the call politely

Begin by greeting the caller now.`,
          voice: "shimmer",
          input_audio_format: "pcm16",
          output_audio_format: "pcm16",
          input_audio_transcription: { model: "whisper-1" },
          turn_detection: {
            type: "server_vad",
            threshold: 0.5,
            prefix_padding_ms: 300,
            silence_duration_ms: 600
          }
        }
      }));
    };

    openaiWs.onmessage = (event) => {
      const msg = JSON.parse(event.data);

      // Handle audio stream from AI back to Asterisk
      if (msg.type === "response.audio.delta") {
        const audioData = Uint8Array.from(atob(msg.delta), c => c.charCodeAt(0));
        
        // Downsample 24kHz (OpenAI default) to 8kHz for Asterisk
        const audio8k = downsample24to8(audioData);
        
        // --- 320-BYTE CHUNKING FOR ASTERISK ---
        for (let i = 0; i < audio8k.length; i += 320) {
          const chunk = audio8k.slice(i, Math.min(i + 320, audio8k.length));
          
          // Pad final chunk if needed
          let frameData: Uint8Array;
          if (chunk.length < 320) {
            frameData = new Uint8Array(320);
            frameData.set(chunk);
          } else {
            frameData = chunk;
          }
          
          // AudioSocket Header: [Type: 0x10, LengthMSB, LengthLSB]
          const header = new Uint8Array([0x10, (frameData.length >> 8) & 0xFF, frameData.length & 0xFF]);
          const fullFrame = new Uint8Array(header.length + frameData.length);
          fullFrame.set(header);
          fullFrame.set(frameData, header.length);

          if (socket.readyState === WebSocket.OPEN) {
            socket.send(fullFrame);
          }
        }
      }

      // Logging Transcripts for debugging
      if (msg.type === "response.audio_transcript.done") {
        log(`💬 Ada: ${msg.transcript}`);
      }
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        log(`👤 User: ${msg.transcript}`);
      }
      
      // Session created - trigger initial greeting
      if (msg.type === "session.created") {
        log("📋 Session created, triggering greeting");
        // Trigger the AI to start speaking by sending response.create
        setTimeout(() => {
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
        }, 500);
      }
      
      // Response done
      if (msg.type === "response.done") {
        log("🔊 Response complete");
      }
      
      // Error handling
      if (msg.type === "error") {
        log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI Connection Closed");
  };

  // --- 2. Asterisk Bridge Logic ---
  socket.onopen = () => console.log("🚀 Asterisk AudioSocket Bridge Connected");

  socket.onmessage = (event) => {
    if (event.data instanceof ArrayBuffer) {
      const buffer = new Uint8Array(event.data);
      const type = buffer[0];

      // Handle Handshake (UUID) - Type 0x01
      if (type === 0x01) {
        callUuid = Array.from(buffer.slice(3, 19))
          .map(b => b.toString(16).padStart(2, '0'))
          .join('');
        log(`🆔 Call Session UUID: ${callUuid}`);
        initOpenAI();
      }

      // Handle Audio (0x10)
      else if (type === 0x10) {
        if (openaiWs?.readyState === WebSocket.OPEN) {
          const rawPcm8k = buffer.slice(3);
          const pcm16k = upsample8to16(rawPcm8k);
          
          openaiWs.send(JSON.stringify({
            type: "input_audio_buffer.append",
            audio: arrayBufferToBase64(pcm16k)
          }));
        }
      }

      // Handle Hangup (0x00)
      else if (type === 0x00) {
        log("👋 Hangup received from Asterisk");
        openaiWs?.close();
        socket.close();
      }
      
      // Handle DTMF (0x03)
      else if (type === 0x03) {
        const digit = String.fromCharCode(buffer[3]);
        log(`🔢 DTMF: ${digit}`);
      }
    }
  };

  socket.onclose = () => {
    log("🔌 Bridge Closed");
    openaiWs?.close();
  };
  
  socket.onerror = (e) => log(`❌ Bridge Error: ${e}`);

  return response;
});
