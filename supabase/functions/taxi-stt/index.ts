import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");

    if (!OPENAI_API_KEY) {
      throw new Error("OPENAI_API_KEY is not configured");
    }

    const contentType = req.headers.get("content-type") || "";
    let audioBlob: Blob;
    let callId = "unknown";
    let audioFormat = "webm";

    if (contentType.includes("multipart/form-data")) {
      // Handle multipart form data (file upload)
      const formData = await req.formData();
      const audioFile = formData.get("audio") as File;
      callId = (formData.get("call_id") as string) || "unknown";
      audioFormat = (formData.get("format") as string) || "webm";
      
      if (!audioFile) {
        throw new Error("No audio file provided");
      }
      audioBlob = audioFile;
    } else {
      // Handle JSON with base64 audio
      const { audio, call_id, format } = await req.json();
      callId = call_id || "unknown";
      audioFormat = format || "webm";
      
      if (!audio) {
        throw new Error("No audio data provided");
      }

      // Convert base64 to blob
      const binaryString = atob(audio);
      const bytes = new Uint8Array(binaryString.length);
      for (let i = 0; i < binaryString.length; i++) {
        bytes[i] = binaryString.charCodeAt(i);
      }
      
      const mimeType = audioFormat === "wav" ? "audio/wav" : 
                       audioFormat === "mp3" ? "audio/mpeg" :
                       audioFormat === "ulaw" ? "audio/basic" :
                       "audio/webm";
      audioBlob = new Blob([bytes], { type: mimeType });
    }

    console.log(`[${callId}] STT request, audio size: ${audioBlob.size} bytes, format: ${audioFormat}`);

    const startTime = Date.now();

    // Prepare form data for Whisper API
    const formData = new FormData();
    const fileName = `audio.${audioFormat === "ulaw" ? "wav" : audioFormat}`;
    formData.append("file", audioBlob, fileName);
    formData.append("model", "whisper-1");
    formData.append("language", "en"); // Optimize for English
    formData.append("response_format", "json");

    const response = await fetch("https://api.openai.com/v1/audio/transcriptions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${OPENAI_API_KEY}`,
      },
      body: formData,
    });

    const latency = Date.now() - startTime;

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[${callId}] Whisper error:`, response.status, errorText);
      throw new Error(`Whisper API error: ${response.status}`);
    }

    const result = await response.json();
    
    console.log(`[${callId}] STT completed in ${latency}ms: "${result.text}"`);

    return new Response(
      JSON.stringify({
        text: result.text,
        call_id: callId,
        latency_ms: latency,
      }),
      {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      }
    );
  } catch (error) {
    console.error("STT error:", error);
    return new Response(
      JSON.stringify({
        error: error instanceof Error ? error.message : "Unknown error",
        text: "", // Return empty text on error
      }),
      {
        status: 500,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      }
    );
  }
});
