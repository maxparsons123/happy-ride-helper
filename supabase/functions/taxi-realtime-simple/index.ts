import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

// === AUDIO PROTOCOL HELPERS ===

/**
 * Asterisk AudioSocket requires a 3-byte header:
 * Byte 0: Message Type (0x10 for Audio)
 * Byte 1-2: Payload Length (Big Endian)
 */
function wrapAudioSocketFrame(pcmData: Uint8Array): Uint8Array {
  const header = new Uint8Array(3);
  header[0] = 0x10; // Audio type
  header[1] = (pcmData.length >> 8) & 0xFF;
  header[2] = pcmData.length & 0xFF;

  const frame = new Uint8Array(header.length + pcmData.length);
  frame.set(header);
  frame.set(pcmData, header.length);
  return frame;
}

/**
 * Downsamples OpenAI's 24kHz to Asterisk's 8kHz
 */
function resample24to8(base64Audio: string): Uint8Array {
  const binary = atob(base64Audio);
  const pcm24k = new Int16Array(new Uint8Array(Array.from(binary, c => c.charCodeAt(0))).buffer);
  
  const outputLen = Math.floor(pcm24k.length / 3);
  const pcm8k = new Int16Array(outputLen);
  
  for (let i = 0; i < outputLen; i++) {
    pcm8k[i] = pcm24k[i * 3]; // Simple decimation for performance
  }
  return new Uint8Array(pcm8k.buffer);
}

// === MAIN HANDLER ===

serve(async (req) => {
  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426 });
  }

  const { socket: asteriskSocket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;

  // --- OpenAI Realtime Connection ---
  const connectToOpenAI = () => {
    openaiWs = new WebSocket(
      `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`,
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      console.log("✅ Connected to OpenAI Realtime");
      openaiWs?.send(JSON.stringify({
        type: "session.update",
        session: {
          modalities: ["text", "audio"],
          instructions: `You are Ada, a friendly taxi booking assistant.

START by greeting the caller warmly and asking for their pickup location.

BOOKING FLOW:
1. Ask for PICKUP location
2. Ask for DESTINATION  
3. Ask for PASSENGER COUNT
4. Ask for TIME (now/ASAP or specific)
5. Read back summary and ask for confirmation
6. On confirmation, say "Your taxi is booked! Driver will arrive shortly. Goodbye!"

RULES:
- Ask ONE question at a time
- Be concise and friendly
- Speak at a relaxed pace`,
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

      // CRITICAL: Audio routing to Asterisk
      if (msg.type === "response.audio.delta" && msg.delta) {
        // 1. Downsample from OpenAI (24k) to Asterisk (8k)
        const pcm8k = resample24to8(msg.delta);
        
        // 2. Wrap in AudioSocket Header (0x10)
        const frame = wrapAudioSocketFrame(pcm8k);
        
        // 3. Send to Asterisk
        if (asteriskSocket.readyState === WebSocket.OPEN) {
          asteriskSocket.send(frame);
        }
      }
      
      // Session created - trigger greeting
      if (msg.type === "session.created") {
        console.log("📋 Session created, triggering greeting");
        setTimeout(() => {
          openaiWs?.send(JSON.stringify({ type: "response.create" }));
        }, 500);
      }

      // Logging
      if (msg.type === "response.audio_transcript.done") {
        console.log("💬 Ada:", msg.transcript);
      }
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        console.log("👤 User:", msg.transcript);
      }
      if (msg.type === "error") {
        console.log("❌ OpenAI error:", JSON.stringify(msg.error));
      }
    };

    openaiWs.onerror = (e) => console.error("🔴 OpenAI WS Error:", e);
    openaiWs.onclose = () => console.log("⚪ OpenAI Connection Closed");
  };

  // --- Asterisk (Incoming) Message Handling ---
  asteriskSocket.onopen = () => console.log("🚀 Asterisk AudioSocket Connected");

  asteriskSocket.onmessage = (event) => {
    if (event.data instanceof ArrayBuffer) {
      const data = new Uint8Array(event.data);
      const type = data[0];

      // 0x01 = UUID (Handshake)
      if (type === 0x01) {
        const uuid = Array.from(data.slice(3, 19)).map(b => b.toString(16).padStart(2, '0')).join('');
        console.log("📞 Call Connected, UUID:", uuid);
        connectToOpenAI();
      }

      // 0x10 = Audio (User speaking)
      if (type === 0x10 && openaiWs?.readyState === WebSocket.OPEN) {
        const audioPayload = data.slice(3); // Strip 3-byte header
        
        // OpenAI expects 16kHz or 24kHz. Since Asterisk is 8kHz, 
        // we "upsample" by doubling samples to reach 16kHz.
        const pcm8k = new Int16Array(audioPayload.buffer, audioPayload.byteOffset, audioPayload.byteLength / 2);
        const pcm16k = new Int16Array(pcm8k.length * 2);
        for (let i = 0; i < pcm8k.length; i++) {
          pcm16k[i * 2] = pcm8k[i];
          pcm16k[i * 2 + 1] = pcm8k[i];
        }

        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: btoa(String.fromCharCode(...new Uint8Array(pcm16k.buffer)))
        }));
      }
      
      // 0x00 = Hangup
      if (type === 0x00) {
        console.log("👋 Call Finished");
        openaiWs?.close();
        asteriskSocket.close();
      }
    }
  };

  asteriskSocket.onclose = () => {
    console.log("🔌 Bridge Closed");
    openaiWs?.close();
  };

  return response;
});
