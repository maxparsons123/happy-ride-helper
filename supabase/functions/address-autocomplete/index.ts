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
    const { input, phone } = await req.json();

    if (!input || input.trim().length < 2) {
      return new Response(JSON.stringify({ predictions: [] }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const apiKey = Deno.env.get("GOOGLE_MAPS_API_KEY");
    if (!apiKey) {
      return new Response(
        JSON.stringify({ error: "Google Maps API key not configured" }),
        { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Detect country from phone prefix for location biasing
    let locationBias = "";
    const normalizedPhone = (phone || "").replace(/\s/g, "");
    if (normalizedPhone.startsWith("+44") || normalizedPhone.startsWith("07") || normalizedPhone.startsWith("01") || normalizedPhone.startsWith("02")) {
      // UK — bias to central UK
      locationBias = "&location=52.4862,-1.8904&radius=80000&components=country:gb";
    } else if (normalizedPhone.startsWith("+31") || normalizedPhone.startsWith("06")) {
      // NL
      locationBias = "&location=52.1326,5.2913&radius=80000&components=country:nl";
    } else {
      // Default UK
      locationBias = "&components=country:gb";
    }

    const url = `https://maps.googleapis.com/maps/api/place/autocomplete/json?input=${encodeURIComponent(input.trim())}&types=address${locationBias}&key=${apiKey}`;

    console.log(`Autocomplete query: "${input.trim()}"`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" && data.status !== "ZERO_RESULTS") {
      console.error("Places API error:", data.status, data.error_message);
      return new Response(
        JSON.stringify({ predictions: [], error: data.error_message || data.status }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const predictions = (data.predictions || []).map((p: any) => ({
      description: p.description,
      place_id: p.place_id,
      main_text: p.structured_formatting?.main_text || "",
      secondary_text: p.structured_formatting?.secondary_text || "",
    }));

    console.log(`Returning ${predictions.length} predictions`);

    return new Response(JSON.stringify({ predictions }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (error) {
    console.error("Autocomplete error:", error);
    return new Response(
      JSON.stringify({ predictions: [], error: error.message }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
