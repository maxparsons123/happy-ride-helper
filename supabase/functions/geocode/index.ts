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
  place_id?: string;
  formatted_address?: string;
  error?: string;
}

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { address, city, country = "UK" } = await req.json();
    
    if (!address) {
      return new Response(
        JSON.stringify({ found: false, address: "", error: "No address provided" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const GOOGLE_MAPS_API_KEY = Deno.env.get("GOOGLE_MAPS_API_KEY");
    
    if (!GOOGLE_MAPS_API_KEY) {
      console.error("[Geocode] GOOGLE_MAPS_API_KEY not configured, falling back to Nominatim");
      return await nominatimFallback(address, city, country);
    }

    // Build search query with city context if provided
    let searchQuery = address;
    if (city) {
      searchQuery = `${address}, ${city}`;
    }
    
    console.log(`[Geocode] Google Places lookup: "${searchQuery}" (city: ${city || 'not specified'})`);

    // Try Google Places Text Search first (better for place names like "Sweet Spot")
    const placesResult = await googlePlacesSearch(searchQuery, city, country, GOOGLE_MAPS_API_KEY);
    
    if (placesResult.found) {
      return new Response(
        JSON.stringify(placesResult),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Fall back to Google Geocoding API for street addresses
    const geocodeResult = await googleGeocode(searchQuery, country, GOOGLE_MAPS_API_KEY);
    
    if (geocodeResult.found) {
      return new Response(
        JSON.stringify(geocodeResult),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // If Google fails, try Nominatim as last resort
    console.log("[Geocode] Google APIs found nothing, trying Nominatim fallback");
    return await nominatimFallback(address, city, country);

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

async function googlePlacesSearch(
  query: string, 
  city: string | undefined, 
  country: string, 
  apiKey: string
): Promise<GeocodeResult> {
  try {
    // Use Places API Text Search for finding businesses/places by name
    const params = new URLSearchParams({
      query: city ? `${query}, ${city}, ${country}` : `${query}, ${country}`,
      key: apiKey,
    });

    // If we have a city, add location bias
    if (city) {
      // Get approximate coordinates for major UK cities
      const cityCoords = getCityCoordinates(city);
      if (cityCoords) {
        params.append("location", `${cityCoords.lat},${cityCoords.lng}`);
        params.append("radius", "20000"); // 20km radius
      }
    }

    const url = `https://maps.googleapis.com/maps/api/place/textsearch/json?${params}`;
    console.log(`[Geocode] Google Places Text Search: ${query}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status === "OK" && data.results && data.results.length > 0) {
      const place = data.results[0];
      console.log(`[Geocode] ✅ Google Places found: "${place.name}" at ${place.formatted_address}`);
      
      return {
        found: true,
        address: query,
        display_name: place.name,
        formatted_address: place.formatted_address,
        lat: place.geometry.location.lat,
        lon: place.geometry.location.lng,
        type: place.types?.[0] || "place",
        place_id: place.place_id,
      };
    }

    console.log(`[Geocode] Google Places found no results for "${query}" (status: ${data.status})`);
    return { found: false, address: query };
    
  } catch (error) {
    console.error("[Geocode] Google Places error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

async function googleGeocode(
  query: string, 
  country: string, 
  apiKey: string
): Promise<GeocodeResult> {
  try {
    const params = new URLSearchParams({
      address: `${query}, ${country}`,
      key: apiKey,
      components: `country:${country === "UK" ? "GB" : country}`,
    });

    const url = `https://maps.googleapis.com/maps/api/geocode/json?${params}`;
    console.log(`[Geocode] Google Geocoding API: ${query}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status === "OK" && data.results && data.results.length > 0) {
      const result = data.results[0];
      console.log(`[Geocode] ✅ Google Geocode found: "${result.formatted_address}"`);
      
      return {
        found: true,
        address: query,
        display_name: result.formatted_address,
        formatted_address: result.formatted_address,
        lat: result.geometry.location.lat,
        lon: result.geometry.location.lng,
        type: result.types?.[0] || "address",
        place_id: result.place_id,
      };
    }

    console.log(`[Geocode] Google Geocode found no results for "${query}" (status: ${data.status})`);
    return { found: false, address: query };
    
  } catch (error) {
    console.error("[Geocode] Google Geocode error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

async function nominatimFallback(
  address: string, 
  city: string | undefined, 
  country: string
): Promise<Response> {
  const searchQuery = city ? `${address}, ${city}, ${country}` : `${address}, ${country}`;
  
  console.log(`[Geocode] Nominatim fallback: "${searchQuery}"`);

  const encodedQuery = encodeURIComponent(searchQuery);
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
  console.log(`[Geocode] Nominatim found ${results.length} results`);

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

  const best = results[0];
  const result: GeocodeResult = {
    found: true,
    address,
    display_name: best.display_name,
    lat: parseFloat(best.lat),
    lon: parseFloat(best.lon),
    type: best.type,
  };

  console.log(`[Geocode] Nominatim best match: "${best.display_name}"`);

  return new Response(
    JSON.stringify(result),
    { headers: { ...corsHeaders, "Content-Type": "application/json" } }
  );
}

// Approximate coordinates for major UK cities for location biasing
function getCityCoordinates(city: string): { lat: number; lng: number } | null {
  const cities: Record<string, { lat: number; lng: number }> = {
    "london": { lat: 51.5074, lng: -0.1278 },
    "birmingham": { lat: 52.4862, lng: -1.8904 },
    "manchester": { lat: 53.4808, lng: -2.2426 },
    "leeds": { lat: 53.8008, lng: -1.5491 },
    "liverpool": { lat: 53.4084, lng: -2.9916 },
    "newcastle": { lat: 54.9783, lng: -1.6178 },
    "sheffield": { lat: 53.3811, lng: -1.4701 },
    "bristol": { lat: 51.4545, lng: -2.5879 },
    "nottingham": { lat: 52.9548, lng: -1.1581 },
    "leicester": { lat: 52.6369, lng: -1.1398 },
    "coventry": { lat: 52.4068, lng: -1.5197 },
    "bradford": { lat: 53.7960, lng: -1.7594 },
    "cardiff": { lat: 51.4816, lng: -3.1791 },
    "edinburgh": { lat: 55.9533, lng: -3.1883 },
    "glasgow": { lat: 55.8642, lng: -4.2518 },
    "belfast": { lat: 54.5973, lng: -5.9301 },
    "cambridge": { lat: 52.2053, lng: 0.1218 },
    "oxford": { lat: 51.7520, lng: -1.2577 },
    "southampton": { lat: 50.9097, lng: -1.4044 },
    "portsmouth": { lat: 50.8198, lng: -1.0880 },
    "brighton": { lat: 50.8225, lng: -0.1372 },
    "reading": { lat: 51.4543, lng: -0.9781 },
    "derby": { lat: 52.9225, lng: -1.4746 },
    "wolverhampton": { lat: 52.5870, lng: -2.1288 },
    "stoke": { lat: 53.0027, lng: -2.1794 },
    "hull": { lat: 53.7676, lng: -0.3274 },
    "york": { lat: 53.9600, lng: -1.0873 },
    "sunderland": { lat: 54.9061, lng: -1.3831 },
    "swansea": { lat: 51.6214, lng: -3.9436 },
    "middlesbrough": { lat: 54.5760, lng: -1.2350 },
    "peterborough": { lat: 52.5695, lng: -0.2405 },
    "luton": { lat: 51.8787, lng: -0.4200 },
    "preston": { lat: 53.7632, lng: -2.7031 },
    "blackpool": { lat: 53.8142, lng: -3.0503 },
    "norwich": { lat: 52.6309, lng: 1.2974 },
    "exeter": { lat: 50.7184, lng: -3.5339 },
    "plymouth": { lat: 50.3755, lng: -4.1427 },
    "aberdeen": { lat: 57.1497, lng: -2.0943 },
    "dundee": { lat: 56.4620, lng: -2.9707 },
  };
  
  const normalized = city.toLowerCase().trim();
  return cities[normalized] || null;
}
