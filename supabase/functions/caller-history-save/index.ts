import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  try {
    const { phone, name, pickup, destination } = await req.json();

    if (!phone) {
      return new Response(JSON.stringify({ error: "phone is required" }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const supabase = createClient(
      Deno.env.get("SUPABASE_URL")!,
      Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!
    );

    // Normalize phone for consistent lookup
    const normalizedPhone = phone.replace(/^\+/, "");
    const phoneVariants = [phone, normalizedPhone, `+${normalizedPhone}`];

    // Find existing caller
    const { data: existing } = await supabase
      .from("callers")
      .select("*")
      .or(phoneVariants.map(p => `phone_number.eq.${p}`).join(","))
      .maybeSingle();

    const now = new Date().toISOString();

    if (existing) {
      // Update existing caller — append addresses (deduplicated)
      const pickupAddrs = new Set<string>(existing.pickup_addresses || []);
      const dropoffAddrs = new Set<string>(existing.dropoff_addresses || []);

      if (pickup?.trim()) pickupAddrs.add(pickup.trim());
      if (destination?.trim()) dropoffAddrs.add(destination.trim());

      const updateData: Record<string, unknown> = {
        updated_at: now,
        last_booking_at: now,
        total_bookings: (existing.total_bookings || 0) + 1,
        pickup_addresses: [...pickupAddrs],
        dropoff_addresses: [...dropoffAddrs],
      };

      if (pickup?.trim()) updateData.last_pickup = pickup.trim();
      if (destination?.trim()) updateData.last_destination = destination.trim();
      if (name?.trim() && !existing.name) updateData.name = name.trim();

      const { error } = await supabase
        .from("callers")
        .update(updateData)
        .eq("id", existing.id);

      if (error) {
        console.error("❌ Failed to update caller:", error);
        throw error;
      }

      console.log(`✅ Updated caller ${existing.phone_number}: +${pickupAddrs.size} pickups, +${dropoffAddrs.size} dropoffs, bookings=${updateData.total_bookings}`);
    } else {
      // Create new caller
      const insertData: Record<string, unknown> = {
        phone_number: normalizedPhone,
        total_bookings: 1,
        last_booking_at: now,
        pickup_addresses: pickup?.trim() ? [pickup.trim()] : [],
        dropoff_addresses: destination?.trim() ? [destination.trim()] : [],
      };

      if (pickup?.trim()) insertData.last_pickup = pickup.trim();
      if (destination?.trim()) insertData.last_destination = destination.trim();
      if (name?.trim()) insertData.name = name.trim();

      const { error } = await supabase.from("callers").insert(insertData);

      if (error) {
        console.error("❌ Failed to insert caller:", error);
        throw error;
      }

      console.log(`✅ Created new caller ${normalizedPhone} with ${pickup ? 1 : 0} pickup, ${destination ? 1 : 0} dropoff`);
    }

    return new Response(JSON.stringify({ success: true }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  } catch (error) {
    console.error("Caller history save error:", error);
    return new Response(JSON.stringify({
      error: error instanceof Error ? error.message : "Unknown error",
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
