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
    const { phone, name, pickup, destination, call_id, booking_ref } = await req.json();

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

    const normalizedPhone = phone.replace(/^\+/, "");
    const phoneVariants = [phone, normalizedPhone, `+${normalizedPhone}`];

    // === 1. Update caller's unique address history ===
    const { data: existing } = await supabase
      .from("callers")
      .select("*")
      .or(phoneVariants.map(p => `phone_number.eq.${p}`).join(","))
      .maybeSingle();

    const now = new Date().toISOString();

    if (existing) {
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
      if (name?.trim()) updateData.name = name.trim();

      const { error } = await supabase.from("callers").update(updateData).eq("id", existing.id);
      if (error) throw error;
      console.log(`✅ Updated caller ${existing.phone_number}: ${pickupAddrs.size} pickups, ${dropoffAddrs.size} dropoffs, bookings=${updateData.total_bookings}`);
    } else {
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
      if (error) throw error;
      console.log(`✅ Created new caller ${normalizedPhone}`);
    }

    // === 2. Save transcript to booking for compliance ===
    if (call_id) {
      try {
        // Fetch transcript from live_calls
        const { data: liveCall } = await supabase
          .from("live_calls")
          .select("transcripts")
          .eq("call_id", call_id)
          .maybeSingle();

        const transcript = liveCall?.transcripts || [];

        // Find the booking to update
        const { data: booking } = await supabase
          .from("bookings")
          .select("id, booking_details")
          .eq("call_id", call_id)
          .order("booked_at", { ascending: false })
          .limit(1)
          .maybeSingle();

        if (booking) {
          const details = (booking.booking_details as Record<string, unknown>) || {};
          details.transcript = transcript;
          details.transcript_saved_at = now;

          const { error } = await supabase
            .from("bookings")
            .update({ booking_details: details })
            .eq("id", booking.id);

          if (error) {
            console.error("⚠️ Failed to save transcript to booking:", error);
          } else {
            console.log(`✅ Transcript saved to booking ${booking.id} (${Array.isArray(transcript) ? transcript.length : 0} turns)`);
          }
        } else {
          console.log(`⚠️ No booking found for call_id=${call_id}, transcript not saved`);
        }
      } catch (txErr) {
        console.warn("⚠️ Transcript save failed (non-fatal):", txErr);
      }
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