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
  city?: string;
  postcode?: string;
  map_link?: string;
  error?: string;
}

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { address, city, country = "UK", lat, lon } = await req.json();
    
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

    // Determine search coordinates - use provided lat/lon first, then city coords
    let searchLat: number | undefined = lat;
    let searchLon: number | undefined = lon;
    
    if (!searchLat || !searchLon) {
      if (city) {
        const cityCoords = getCityCoordinates(city);
        if (cityCoords) {
          searchLat = cityCoords.lat;
          searchLon = cityCoords.lng;
        }
      }
    }

    console.log(`[Geocode] Searching: "${address}" (city: ${city || 'N/A'}, coords: ${searchLat},${searchLon})`);

    // If we have caller coordinates, try nearby search first for local places
    if (searchLat && searchLon) {
      const nearbyResult = await googleNearbySearch(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY);
      if (nearbyResult.found) {
        return new Response(
          JSON.stringify(nearbyResult),
          { headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }
    }

    // Try text search with location bias
    const textSearchResult = await googleTextSearch(address, searchLat, searchLon, GOOGLE_MAPS_API_KEY);
    if (textSearchResult.found) {
      return new Response(
        JSON.stringify(textSearchResult),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Fall back to Google Geocoding API for street addresses
    const geocodeResult = await googleGeocode(address, city, country, GOOGLE_MAPS_API_KEY);
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

// Nearby Search - finds places closest to the caller's location
async function googleNearbySearch(
  query: string,
  lat: number,
  lon: number,
  apiKey: string
): Promise<GeocodeResult> {
  try {
    const url = `https://maps.googleapis.com/maps/api/place/nearbysearch/json` +
      `?location=${lat},${lon}` +
      `&rankby=distance` +
      `&keyword=${encodeURIComponent(query)}` +
      `&key=${apiKey}`;

    console.log(`[Geocode] Nearby search: "${query}" near ${lat},${lon}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] Nearby search: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    const placeId = data.results[0].place_id;
    if (!placeId) {
      return { found: false, address: query };
    }

    // Get full place details
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Nearby search error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Text Search with location bias
async function googleTextSearch(
  query: string,
  lat: number | undefined,
  lon: number | undefined,
  apiKey: string
): Promise<GeocodeResult> {
  try {
    let url = `https://maps.googleapis.com/maps/api/place/textsearch/json` +
      `?query=${encodeURIComponent(query)}` +
      `&key=${apiKey}`;

    // Add location bias if we have coordinates
    if (lat && lon) {
      url += `&location=${lat},${lon}&radius=5000`;
    }

    console.log(`[Geocode] Text search: "${query}"${lat ? ` biased to ${lat},${lon}` : ''}`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.results || data.results.length === 0) {
      console.log(`[Geocode] Text search: no results (status: ${data.status})`);
      return { found: false, address: query };
    }

    const placeId = data.results[0].place_id;
    if (!placeId) {
      return { found: false, address: query };
    }

    // Get full place details for address components
    return await getPlaceDetails(placeId, query, apiKey);

  } catch (error) {
    console.error("[Geocode] Text search error:", error);
    return { found: false, address: query, error: String(error) };
  }
}

// Get detailed place information including address components
async function getPlaceDetails(
  placeId: string,
  originalQuery: string,
  apiKey: string
): Promise<GeocodeResult> {
  try {
    const url = `https://maps.googleapis.com/maps/api/place/details/json` +
      `?place_id=${placeId}` +
      `&fields=name,formatted_address,geometry,address_components` +
      `&key=${apiKey}`;

    const response = await fetch(url);
    const data = await response.json();

    if (data.status !== "OK" || !data.result) {
      return { found: false, address: originalQuery };
    }

    const result = data.result;
    const components = result.address_components || [];

    // Extract address components
    const getComponent = (type: string): string | undefined => {
      const comp = components.find((c: any) => c.types?.includes(type));
      return comp?.long_name;
    };

    const houseNumber = getComponent("street_number");
    const streetName = getComponent("route");
    const postcode = getComponent("postal_code");
    const city = getComponent("locality") || 
                 getComponent("postal_town") || 
                 getComponent("administrative_area_level_2") ||
                 getComponent("administrative_area_level_1");

    const loc = result.geometry?.location;
    if (!loc) {
      return { found: false, address: originalQuery };
    }

    const latitude = loc.lat;
    const longitude = loc.lng;

    // Build display name: prefer place name, fall back to street address
    let displayName = result.name || result.formatted_address;
    
    // If we have a house number and street, include it
    if (houseNumber && streetName) {
      displayName = `${result.name || ''} ${houseNumber} ${streetName}`.trim();
    }

    console.log(`[Geocode] ✅ Found: "${displayName}" (${city}, ${postcode}) at ${latitude},${longitude}`);

    return {
      found: true,
      address: originalQuery,
      display_name: displayName,
      formatted_address: result.formatted_address,
      lat: latitude,
      lon: longitude,
      type: "place",
      place_id: placeId,
      city: city,
      postcode: postcode,
      map_link: `https://maps.google.com/?q=${latitude},${longitude}`,
    };

  } catch (error) {
    console.error("[Geocode] Place details error:", error);
    return { found: false, address: originalQuery, error: String(error) };
  }
}

async function googleGeocode(
  query: string,
  city: string | undefined,
  country: string, 
  apiKey: string
): Promise<GeocodeResult> {
  try {
    const searchQuery = city ? `${query}, ${city}, ${country}` : `${query}, ${country}`;
    
    const params = new URLSearchParams({
      address: searchQuery,
      key: apiKey,
      components: `country:${country === "UK" ? "GB" : country}`,
    });

    const url = `https://maps.googleapis.com/maps/api/geocode/json?${params}`;
    console.log(`[Geocode] Geocoding API: "${searchQuery}"`);

    const response = await fetch(url);
    const data = await response.json();

    if (data.status === "OK" && data.results && data.results.length > 0) {
      const result = data.results[0];
      const components = result.address_components || [];

      const getComponent = (type: string): string | undefined => {
        const comp = components.find((c: any) => c.types?.includes(type));
        return comp?.long_name;
      };

      const city = getComponent("locality") || 
                   getComponent("postal_town") || 
                   getComponent("administrative_area_level_2");
      const postcode = getComponent("postal_code");

      console.log(`[Geocode] ✅ Geocode found: "${result.formatted_address}"`);
      
      return {
        found: true,
        address: query,
        display_name: result.formatted_address,
        formatted_address: result.formatted_address,
        lat: result.geometry.location.lat,
        lon: result.geometry.location.lng,
        type: result.types?.[0] || "address",
        place_id: result.place_id,
        city: city,
        postcode: postcode,
        map_link: `https://maps.google.com/?q=${result.geometry.location.lat},${result.geometry.location.lng}`,
      };
    }

    console.log(`[Geocode] Geocode found no results (status: ${data.status})`);
    return { found: false, address: query };
    
  } catch (error) {
    console.error("[Geocode] Geocode error:", error);
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
    city: best.address?.city || best.address?.town || best.address?.village,
    postcode: best.address?.postcode,
    map_link: `https://maps.google.com/?q=${best.lat},${best.lon}`,
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
