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

    if (!input || input.trim().length < 3) {
      return new Response(JSON.stringify({ predictions: [] }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Detect country from phone prefix
    const normalizedPhone = (phone || "").replace(/\s/g, "");
    let countryCode = "gb";
    if (normalizedPhone.startsWith("+31") || normalizedPhone.startsWith("06")) {
      countryCode = "nl";
    }

    const query = encodeURIComponent(input.trim());
    const url = `https://nominatim.openstreetmap.org/search?q=${query}&format=json&addressdetails=1&limit=6&countrycodes=${countryCode}`;

    console.log(`Autocomplete query: "${input.trim()}" country=${countryCode}`);

    const response = await fetch(url, {
      headers: {
        "User-Agent": "AdaTaxiBooking/1.0",
        "Accept": "application/json",
      },
    });

    if (!response.ok) {
      console.error("Nominatim error:", response.status);
      return new Response(JSON.stringify({ predictions: [] }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const data = await response.json();

    const predictions = (data || []).map((place: any) => ({
      description: place.display_name,
      lat: place.lat,
      lon: place.lon,
      main_text: [
        place.address?.house_number,
        place.address?.road,
      ].filter(Boolean).join(" ") || place.display_name.split(",")[0],
      secondary_text: [
        place.address?.suburb,
        place.address?.city || place.address?.town || place.address?.village,
        place.address?.postcode,
      ].filter(Boolean).join(", "),
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
