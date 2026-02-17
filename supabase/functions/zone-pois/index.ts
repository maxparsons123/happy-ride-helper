import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers":
    "authorization, x-client-info, apikey, content-type",
};

interface ZonePoint {
  lat: number;
  lng: number;
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { zone_id, points } = await req.json() as {
      zone_id: string;
      points: ZonePoint[];
    };

    if (!zone_id || !points || points.length < 3) {
      return new Response(
        JSON.stringify({ error: "zone_id and at least 3 points required" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Build bounding box for Overpass query
    const lats = points.map((p) => p.lat);
    const lngs = points.map((p) => p.lng);
    const south = Math.min(...lats);
    const north = Math.max(...lats);
    const west = Math.min(...lngs);
    const east = Math.max(...lngs);

    // Build polygon string for Overpass poly filter
    const polyStr = points.map((p) => `${p.lat} ${p.lng}`).join(" ");

    // Query Overpass API for streets and named businesses/POIs within the polygon
    const overpassQuery = `
[out:json][timeout:30];
(
  way["highway"]["name"](poly:"${polyStr}");
  node["name"]["shop"](poly:"${polyStr}");
  node["name"]["amenity"](poly:"${polyStr}");
  node["name"]["tourism"](poly:"${polyStr}");
  node["name"]["leisure"](poly:"${polyStr}");
  node["name"]["office"](poly:"${polyStr}");
  way["name"]["shop"](poly:"${polyStr}");
  way["name"]["amenity"](poly:"${polyStr}");
  way["name"]["building"]["name"](poly:"${polyStr}");
);
out center tags;
`;

    // Also query for area/suburb boundaries to map POIs to areas
    const areaQuery = `
[out:json][timeout:15];
(
  relation["boundary"="administrative"]["admin_level"~"^(8|9|10|11)$"]["name"](${south},${west},${north},${east});
  way["place"~"suburb|neighbourhood|quarter|village"]["name"](${south},${west},${north},${east});
  node["place"~"suburb|neighbourhood|quarter|village"]["name"](${south},${west},${north},${east});
);
out center tags;
`;

    // Fetch areas in parallel
    const areaResPromise = fetch("https://overpass-api.de/api/interpreter", {
      method: "POST",
      body: `data=${encodeURIComponent(areaQuery)}`,
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
    });

    console.log("Querying Overpass API for zone:", zone_id);
    const overpassRes = await fetch("https://overpass-api.de/api/interpreter", {
      method: "POST",
      body: `data=${encodeURIComponent(overpassQuery)}`,
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
    });

    if (!overpassRes.ok) {
      const errText = await overpassRes.text();
      console.error("Overpass error:", errText);
      return new Response(
        JSON.stringify({ error: "Overpass API error", detail: errText }),
        { status: 502, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const overpassData = await overpassRes.json();
    const elements = overpassData.elements || [];

    // Parse area boundaries for locality mapping
    interface Area { name: string; lat: number; lng: number }
    const areas: Area[] = [];
    try {
      const areaRes = await areaResPromise;
      if (areaRes.ok) {
        const areaData = await areaRes.json();
        for (const el of (areaData.elements || [])) {
          const name = el.tags?.name;
          if (!name) continue;
          const lat = el.lat ?? el.center?.lat ?? 0;
          const lng = el.lon ?? el.center?.lon ?? 0;
          if (lat && lng) areas.push({ name, lat, lng });
        }
      }
    } catch (e) {
      console.warn("Area query failed, continuing without areas:", e);
    }
    console.log(`Found ${areas.length} area/suburb boundaries`);

    // Helper: find nearest area for a lat/lng
    function findArea(lat: number, lng: number): string | null {
      if (areas.length === 0 || !lat || !lng) return null;
      let best: Area | null = null;
      let bestDist = Infinity;
      for (const a of areas) {
        const d = (a.lat - lat) ** 2 + (a.lng - lng) ** 2;
        if (d < bestDist) { bestDist = d; best = a; }
      }
      // Only assign if within ~3km (rough threshold)
      return best && bestDist < 0.001 ? best.name : null;
    }

    // Deduplicate streets and businesses
    const streets = new Map<string, { name: string; lat: number; lng: number; osm_id: number; area: string | null }>();
    const businesses = new Map<string, { name: string; lat: number; lng: number; osm_id: number; area: string | null }>();

    for (const el of elements) {
      const name = el.tags?.name;
      if (!name) continue;

      const lat = el.lat ?? el.center?.lat ?? 0;
      const lng = el.lon ?? el.center?.lon ?? 0;
      const osmId = el.id;
      const area = findArea(lat, lng);

      const isStreet = el.tags?.highway != null;

      if (isStreet) {
        // For streets, keep per area so same street in different areas shows separately
        const key = `${name.toLowerCase()}_${area?.toLowerCase() || '_'}`;
        if (!streets.has(key)) {
          streets.set(key, { name, lat, lng, osm_id: osmId, area });
        }
      } else {
        const key = `${name.toLowerCase()}_${osmId}`;
        if (!businesses.has(key)) {
          businesses.set(key, { name, lat, lng, osm_id: osmId, area });
        }
      }
    }

    // Store in Supabase
    const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
    const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
    const sb = createClient(supabaseUrl, serviceKey);

    // Delete existing POIs for this zone first
    await sb.from("zone_pois").delete().eq("zone_id", zone_id);

    // Prepare rows
    const rows: any[] = [];
    for (const s of streets.values()) {
      rows.push({ zone_id, poi_type: "street", name: s.name, lat: s.lat, lng: s.lng, osm_id: s.osm_id, area: s.area });
    }
    for (const b of businesses.values()) {
      rows.push({ zone_id, poi_type: "business", name: b.name, lat: b.lat, lng: b.lng, osm_id: b.osm_id, area: b.area });
    }

    if (rows.length > 0) {
      // Insert in batches of 500
      for (let i = 0; i < rows.length; i += 500) {
        const batch = rows.slice(i, i + 500);
        const { error } = await sb.from("zone_pois").insert(batch);
        if (error) {
          console.error("Insert error:", error);
          return new Response(
            JSON.stringify({ error: "Failed to save POIs", detail: error.message }),
            { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
          );
        }
      }
    }

    const result = {
      success: true,
      streets: streets.size,
      businesses: businesses.size,
      total: rows.length,
    };

    console.log("Zone POIs saved:", result);
    return new Response(JSON.stringify(result), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (err) {
    console.error("Error:", err);
    return new Response(
      JSON.stringify({ error: err instanceof Error ? err.message : "Unknown error" }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
