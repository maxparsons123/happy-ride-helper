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
    const GROQ_API_KEY = Deno.env.get("GROQ_API_KEY");

    if (!GROQ_API_KEY) {
      throw new Error("GROQ_API_KEY is not configured");
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

    console.log(`[${callId}] STT request (Groq), audio size: ${audioBlob.size} bytes, format: ${audioFormat}`);

    const startTime = Date.now();

    // Prepare form data for Groq Whisper API
    const formData = new FormData();
    const fileName = `audio.${audioFormat === "ulaw" ? "wav" : audioFormat}`;
    formData.append("file", audioBlob, fileName);
    formData.append("model", "whisper-large-v3-turbo"); // Groq's fastest Whisper model
    // No language specified - let Whisper auto-detect for multilingual support
    formData.append("response_format", "json");
    // Add prompt for context - helps with taxi/address terminology
    // Enhanced with West Midlands vocabulary for better UK address recognition
    formData.append("prompt", `Taxi booking conversation in the West Midlands, UK. 
Cities: Coventry, Birmingham, Wolverhampton, Walsall, Dudley, Solihull, Nuneaton, Leamington, Warwick, Rugby.
Common streets: David Road, School Road, Station Road, High Street, Church Lane, Park Road, London Road.
Address formats: 52A David Road, 14 School Road, house numbers like fifty-two A, one-four.
Terms: pickup, drop-off, destination, passengers, luggage, bags, estate car, saloon, minibus, MPV.
Locations: Birmingham Airport, Coventry Station, New Street Station, Manchester Airport.
UK postcodes: CV1, CV2, B1, WV1, WS1.`);

    const response = await fetch("https://api.groq.com/openai/v1/audio/transcriptions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${GROQ_API_KEY}`,
      },
      body: formData,
    });

    const latency = Date.now() - startTime;

    if (!response.ok) {
      const errorText = await response.text();
      console.error(`[${callId}] Groq Whisper error:`, response.status, errorText);
      throw new Error(`Groq Whisper API error: ${response.status}`);
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
