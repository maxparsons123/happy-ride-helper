import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

/**
 * taxi-address-tts: Generate TTS for addresses using the same voice as Ada (shimmer)
 * 
 * This function is used to generate accurate address audio from extracted text,
 * ensuring addresses are spoken correctly even if transcription was wrong.
 * 
 * Input: { address: string, format?: "mp3" | "pcm16" }
 * Output: { audio: string (base64), format: string }
 */
serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
    if (!OPENAI_API_KEY) {
      throw new Error("OPENAI_API_KEY not configured");
    }

    const { address, format = "pcm16" } = await req.json();

    if (!address || typeof address !== "string") {
      throw new Error("Address is required");
    }

    console.log(`[taxi-address-tts] Generating TTS for: "${address}" (format: ${format})`);

    // Generate speech using OpenAI TTS with shimmer voice (same as Ada)
    const response = await fetch("https://api.openai.com/v1/audio/speech", {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${OPENAI_API_KEY}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        model: "tts-1",
        input: address,
        voice: "shimmer", // Same voice as Ada for seamless splicing
        response_format: format === "pcm16" ? "pcm" : "mp3",
        speed: 1.0,
      }),
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[taxi-address-tts] OpenAI API error: ${response.status} - ${errorText}`);
      throw new Error(`TTS API error: ${response.status}`);
    }

    // Get audio as array buffer and convert to base64
    const arrayBuffer = await response.arrayBuffer();
    const uint8Array = new Uint8Array(arrayBuffer);
    
    // Convert to base64
    let binary = "";
    const chunkSize = 0x8000;
    for (let i = 0; i < uint8Array.length; i += chunkSize) {
      const chunk = uint8Array.subarray(i, Math.min(i + chunkSize, uint8Array.length));
      binary += String.fromCharCode.apply(null, Array.from(chunk));
    }
    const audioBase64 = btoa(binary);

    console.log(`[taxi-address-tts] Generated ${uint8Array.length} bytes of audio for: "${address}"`);

    return new Response(
      JSON.stringify({ 
        audio: audioBase64, 
        format: format,
        address: address,
        bytes: uint8Array.length
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    console.error("[taxi-address-tts] Error:", errorMessage);
    return new Response(
      JSON.stringify({ error: errorMessage }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
