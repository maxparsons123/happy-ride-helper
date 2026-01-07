import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Good voices for a friendly taxi dispatcher
const VOICE_OPTIONS: Record<string, string> = {
  "george": "JBFqnCBsd6RMkjVDRZzb", // British male - friendly
  "brian": "nPczCjzI2devNBz1zQrb",  // British male - warm
  "charlie": "IKne3meq5aSn9XLyUdCD", // Australian male - casual
  "alice": "Xb7hH8MSUJpSbSDYk0k2",  // British female - friendly
  "lily": "pFZP5JQG7iQjIQuC4Bku",   // British female - warm
};

const DEFAULT_VOICE = "george"; // British male, friendly tone

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { text, voice, call_id, format } = await req.json();
    const ELEVENLABS_API_KEY = Deno.env.get("ELEVENLABS_API_KEY");

    if (!ELEVENLABS_API_KEY) {
      throw new Error("ELEVENLABS_API_KEY is not configured");
    }

    if (!text) {
      throw new Error("Text is required");
    }

    console.log(`[${call_id || 'unknown'}] TTS request: "${text.substring(0, 50)}..."`);

    const voiceId = VOICE_OPTIONS[voice || DEFAULT_VOICE] || VOICE_OPTIONS[DEFAULT_VOICE];
    
    // Use format suitable for Asterisk (ulaw for telephony, or mp3)
    const outputFormat = format === "ulaw" ? "ulaw_8000" : "mp3_22050_32";

    const startTime = Date.now();

    const response = await fetch(
      `https://api.elevenlabs.io/v1/text-to-speech/${voiceId}?output_format=${outputFormat}`,
      {
        method: "POST",
        headers: {
          "xi-api-key": ELEVENLABS_API_KEY,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          text,
          model_id: "eleven_turbo_v2_5", // Fastest model for real-time
          voice_settings: {
            stability: 0.5,
            similarity_boost: 0.75,
            style: 0.3,
            use_speaker_boost: true,
            speed: 1.1, // Slightly faster for natural phone conversation
          },
        }),
      }
    );

    const latency = Date.now() - startTime;
    console.log(`[${call_id || 'unknown'}] TTS generated in ${latency}ms`);

    if (!response.ok) {
      const errorText = await response.text();
      console.error("ElevenLabs error:", response.status, errorText);
      throw new Error(`ElevenLabs API error: ${response.status}`);
    }

    const audioBuffer = await response.arrayBuffer();
    
    console.log(`[${call_id || 'unknown'}] Audio size: ${audioBuffer.byteLength} bytes`);

    // Return raw audio for Asterisk playback
    const contentType = format === "ulaw" ? "audio/basic" : "audio/mpeg";
    
    return new Response(audioBuffer, {
      headers: {
        ...corsHeaders,
        "Content-Type": contentType,
        "X-TTS-Latency-Ms": latency.toString(),
        "X-Call-Id": call_id || "unknown",
      },
    });
  } catch (error) {
    console.error("TTS error:", error);
    return new Response(
      JSON.stringify({
        error: error instanceof Error ? error.message : "Unknown error",
      }),
      {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      }
    );
  }
});
