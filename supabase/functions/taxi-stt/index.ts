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
    formData.append("response_format", "json");
    formData.append("temperature", "0"); // CRITICAL: Temperature=0 prevents creative hallucinations
    // NO language lock - auto-detect for multilingual support (Urdu, Punjabi, Polish, Romanian, Arabic, etc.)
    // Rich prompt with ALL local entities to bias decoder toward correct phonetics
    formData.append("prompt", `Taxi booking phone call in the West Midlands, England, UK.
CITIES: Coventry, Birmingham, Wolverhampton, Walsall, Dudley, Solihull, Nuneaton, Leamington Spa, Warwick, Rugby, Bedworth, Kenilworth, Stratford-upon-Avon, Tamworth, Lichfield, Sutton Coldfield, Redditch, Bromsgrove, Halesowen, Stourbridge, West Bromwich, Oldbury, Smethwick, Tipton, Bilston, Willenhall, Bloxwich, Cannock.
STREETS: School Road, Station Road, High Street, Church Lane, Park Road, London Road, David Road, Binley Road, Foleshill Road, Stoney Stanton Road, Allesley Old Road, Holyhead Road, Warwick Road, Kenilworth Road, Walsgrave Road, Ansty Road, Longford Road.
LANDMARKS: Birmingham Airport, Coventry Station, Coventry City Centre, Birmingham New Street Station, Manchester Airport, Heathrow Airport, Gatwick Airport, Ricoh Arena, University of Warwick, Coventry University, Birmingham University.
ADDRESS FORMATS: 52A David Road, 14 School Road, house numbers spoken as "fifty-two A", "one-four", UK postcodes CV1, CV2, CV3, CV4, CV5, CV6, B1, B2, WV1, WS1.
COMMANDS: cancel, cancel it, cancel the booking, keep it, book it, yes please, no thanks, that's right, that's correct.
MULTILINGUAL: This call may be in English, Urdu, Punjabi, Polish, Romanian, Arabic, Bengali, Hindi, or other languages.`);

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
