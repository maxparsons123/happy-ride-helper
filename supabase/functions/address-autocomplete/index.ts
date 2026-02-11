import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
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

    // Detect country from phone prefix for location biasing
    const normalizedPhone = (phone || "").replace(/\s/g, "");
    let lat = 52.4862, lon = -1.8904, lang = "en"; // Default: central UK
    let countryFilter = "";

    if (normalizedPhone.startsWith("+31") || normalizedPhone.startsWith("06")) {
      lat = 52.1326; lon = 5.2913; lang = "nl";
    }

    const query = encodeURIComponent(input.trim());
    const url = `https://photon.komoot.io/api/?q=${query}&lat=${lat}&lon=${lon}&limit=8&lang=${lang}`;

    console.log(`Photon autocomplete: "${input.trim()}" bias=${lat},${lon}`);

    const response = await fetch(url, {
      headers: { "Accept": "application/json" },
    });

    if (!response.ok) {
      console.error("Photon error:", response.status);
      return new Response(JSON.stringify({ predictions: [] }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const data = await response.json();

    const predictions = (data.features || []).map((f: any) => {
      const p = f.properties || {};
      const coords = f.geometry?.coordinates || [];
      const parts = [p.housenumber, p.street].filter(Boolean);
      const mainText = parts.length > 0 ? parts.join(" ") : (p.name || "");
      const secondaryParts = [p.district, p.city || p.town || p.village, p.postcode, p.state].filter(Boolean);

      return {
        description: [mainText, ...secondaryParts].filter(Boolean).join(", "),
        lat: coords[1]?.toString() || "",
        lon: coords[0]?.toString() || "",
        main_text: mainText,
        secondary_text: secondaryParts.join(", "),
      };
    }).filter((p: any) => p.description.length > 0);

    console.log(`Returning ${predictions.length} Photon predictions`);

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
