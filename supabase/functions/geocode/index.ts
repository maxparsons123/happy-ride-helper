import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

interface GeocodeResult {
  found: boolean;
  address: string;
  display_name?: string;
  lat?: number;
  lon?: number;
  type?: string;
  importance?: number;
  error?: string;
}

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { address, city = "Bradford", country = "UK" } = await req.json();
    
    if (!address) {
      return new Response(
        JSON.stringify({ found: false, address: "", error: "No address provided" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    console.log(`[Geocode] Looking up: "${address}" in ${city}, ${country}`);

    // Build search query - append city/country for better results
    const searchQuery = `${address}, ${city}, ${country}`;
    const encodedQuery = encodeURIComponent(searchQuery);
    
    // Use OSM Nominatim API (free, no API key required)
    // IMPORTANT: Nominatim requires a User-Agent header
    const nominatimUrl = `https://nominatim.openstreetmap.org/search?q=${encodedQuery}&format=json&addressdetails=1&limit=3`;
    
    const response = await fetch(nominatimUrl, {
      headers: {
        "User-Agent": "247RadioCarz-TaxiBooking/1.0",
        "Accept": "application/json"
      }
    });

    if (!response.ok) {
      console.error(`[Geocode] Nominatim API error: ${response.status}`);
      return new Response(
        JSON.stringify({ 
          found: false, 
          address, 
          error: `Geocoding service error: ${response.status}` 
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const results = await response.json();
    console.log(`[Geocode] Found ${results.length} results for "${address}"`);

    if (results.length === 0) {
      return new Response(
        JSON.stringify({ 
          found: false, 
          address,
          error: "Address not found" 
        }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Return the best match
    const best = results[0];
    const result: GeocodeResult = {
      found: true,
      address,
      display_name: best.display_name,
      lat: parseFloat(best.lat),
      lon: parseFloat(best.lon),
      type: best.type,
      importance: best.importance
    };

    console.log(`[Geocode] Best match: "${best.display_name}" (importance: ${best.importance})`);

    return new Response(
      JSON.stringify(result),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );

  } catch (error) {
    console.error("[Geocode] Error:", error);
    const errorMessage = error instanceof Error ? error.message : "Unknown error";
    return new Response(
      JSON.stringify({ 
        found: false, 
        address: "", 
        error: errorMessage 
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" }, status: 500 }
    );
  }
});
