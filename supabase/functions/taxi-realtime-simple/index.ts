import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

// --- Configuration ---
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

if (!OPENAI_API_KEY) {
  console.error("❌ OPENAI_API_KEY environment variable is not set.");
}

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

type TransportMode = "unknown" | "audiosocket" | "bridge";

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
 * Decode OpenAI base64 PCM16 into bytes
 */
function decodeBase64ToBytes(base64Audio: string): Uint8Array {
  const binary = atob(base64Audio);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Downsample 24kHz PCM16 to 8kHz PCM16 for AudioSocket (3:1)
 */
function downsamplePcm24to8(pcm24kBytes: Uint8Array): Uint8Array {
  const pcm24k = new Int16Array(
    pcm24kBytes.buffer,
    pcm24kBytes.byteOffset,
    Math.floor(pcm24kBytes.byteLength / 2),
  );

  const outputLen = Math.floor(pcm24k.length / 3);
  const pcm8k = new Int16Array(outputLen);
  for (let i = 0; i < outputLen; i++) {
    // simple decimation; AudioSocket callers typically already band-limited
    pcm8k[i] = pcm24k[i * 3];
  }
  return new Uint8Array(pcm8k.buffer);
}

// === MAIN HANDLER ===

serve(async (req) => {
  // CORS preflight (some WS clients still send OPTIONS)
  if (req.method === "OPTIONS") return new Response(null, { headers: corsHeaders });

  if (req.headers.get("upgrade")?.toLowerCase() !== "websocket") {
    return new Response("Expected WebSocket", { status: 426 });
  }

  const { socket: asteriskSocket, response } = Deno.upgradeWebSocket(req);
  let openaiWs: WebSocket | null = null;
  let mode: TransportMode = "unknown";
  let didInitOpenAi = false;
  let callId = "init";

  const log = (msg: string) => console.log(`[${callId}] ${msg}`);

  // --- OpenAI Realtime Connection ---
  const connectToOpenAI = () => {
    if (didInitOpenAi) return;
    didInitOpenAi = true;

    openaiWs = new WebSocket(
      `wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17`,
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      log("✅ Connected to OpenAI Realtime");
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

      // CRITICAL: Audio routing to client
      if (msg.type === "response.audio.delta" && msg.delta) {
        const pcm24kBytes = decodeBase64ToBytes(msg.delta);

        if (asteriskSocket.readyState !== WebSocket.OPEN) return;

        // If we're talking to the Python bridge (or any WS bridge), it expects raw PCM16 bytes.
        if (mode === "bridge" || mode === "unknown") {
          asteriskSocket.send(pcm24kBytes.buffer);
          return;
        }

        // If we're talking directly to Asterisk AudioSocket, it expects 0x10+len framing + 8kHz PCM16 frames.
        if (mode === "audiosocket") {
          const pcm8k = downsamplePcm24to8(pcm24kBytes);
          const frame = wrapAudioSocketFrame(pcm8k);
          asteriskSocket.send(frame);
        }
      }
      
      // Session created - trigger greeting
      if (msg.type === "session.created") {
        log("📋 Session created, sending greeting");
        // Ensure an initial assistant utterance exists.
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "message",
            role: "assistant",
            content: [{ type: "text", text: "Hello! Where would you like to be picked up?" }],
          },
        }));
        openaiWs?.send(JSON.stringify({ type: "response.create" }));
      }

      // Logging
      if (msg.type === "response.audio_transcript.done") {
        log(`💬 Ada: ${msg.transcript}`);
      }
      if (msg.type === "conversation.item.input_audio_transcription.completed") {
        log(`👤 User: ${msg.transcript}`);
      }
      if (msg.type === "error") {
        log(`❌ OpenAI error: ${JSON.stringify(msg.error)}`);
      }
    };

    openaiWs.onerror = (e) => log(`🔴 OpenAI WS Error: ${e}`);
    openaiWs.onclose = () => log("⚪ OpenAI Connection Closed");
  };

  // --- Asterisk (Incoming) Message Handling ---
  asteriskSocket.onopen = () => log("🚀 Client WebSocket connected");

  asteriskSocket.onmessage = (event) => {
    // Text control (Python bridge typically sends JSON like {type:"init"})
    if (typeof event.data === "string") {
      try {
        const msg = JSON.parse(event.data);
        if (msg?.type === "init") {
          mode = "bridge";
          callId = msg.call_id || callId;
          log("🧩 Detected BRIDGE mode (init JSON)");
          connectToOpenAI();
        }
        if (msg?.type === "hangup") {
          log("👋 Hangup received (bridge)");
          openaiWs?.close();
          asteriskSocket.close();
        }
      } catch {
        // ignore
      }
      return;
    }

    // Binary audio
    if (!(event.data instanceof ArrayBuffer)) return;
    const data = new Uint8Array(event.data);

    // Heuristic: AudioSocket frames always have a 3-byte header with known type.
    const maybeType = data[0];
    const looksLikeAudioSocket =
      (maybeType === 0x01 || maybeType === 0x10 || maybeType === 0x00 || maybeType === 0x03 || maybeType === 0xff) &&
      data.length >= 3;

    if (looksLikeAudioSocket && mode === "unknown") {
      mode = "audiosocket";
      log("🧩 Detected AUDIOSOCKET mode (binary header)");
    }

    // AudioSocket path
    if (mode === "audiosocket") {
      const type = data[0];
      if (type === 0x01) {
        // UUID is 16 raw bytes in many setups; we log it but also connect OpenAI immediately.
        const uuidHex = Array.from(data.slice(3, 19))
          .map((b) => b.toString(16).padStart(2, "0"))
          .join("");
        callId = uuidHex || callId;
        log(`📞 Call Connected (UUID ${uuidHex})`);
        connectToOpenAI();
        return;
      }

      if (type === 0x10 && openaiWs?.readyState === WebSocket.OPEN) {
        const audioPayload = data.slice(3);
        // For now, we forward the 8kHz PCM16 as a crude 16kHz by duplication (same as before).
        const pcm8k = new Int16Array(audioPayload.buffer, audioPayload.byteOffset, Math.floor(audioPayload.byteLength / 2));
        const pcm16k = new Int16Array(pcm8k.length * 2);
        for (let i = 0; i < pcm8k.length; i++) {
          pcm16k[i * 2] = pcm8k[i];
          pcm16k[i * 2 + 1] = pcm8k[i];
        }
        const bytes16k = new Uint8Array(pcm16k.buffer);
        openaiWs.send(
          JSON.stringify({
            type: "input_audio_buffer.append",
            audio: arrayBufferToBase64(bytes16k),
          }),
        );
        return;
      }

      if (type === 0x00) {
        log("👋 Call Finished (audiosocket)");
        openaiWs?.close();
        asteriskSocket.close();
      }

      return;
    }

    // Bridge path (binary = raw PCM16 already, likely 24kHz)
    if (mode === "bridge" || mode === "unknown") {
      // If we didn't get init JSON first, still attempt bridge mode.
      if (mode === "unknown") {
        mode = "bridge";
        log("🧩 Assuming BRIDGE mode (binary audio without AudioSocket header)");
        connectToOpenAI();
      }

      if (openaiWs?.readyState === WebSocket.OPEN) {
        openaiWs.send(
          JSON.stringify({
            type: "input_audio_buffer.append",
            audio: arrayBufferToBase64(data),
          }),
        );
      }
    }
  };

  asteriskSocket.onclose = () => {
    log("🔌 Client disconnected");
    openaiWs?.close();
  };

  return response;
});

/**
 * Converts Uint8Array audio to Base64 for OpenAI
 */
function arrayBufferToBase64(buffer: Uint8Array): string {
  let binary = "";
  const bytes = buffer;
  for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary);
}
