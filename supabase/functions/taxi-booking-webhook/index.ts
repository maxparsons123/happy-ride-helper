import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
    
    const { 
      action,
      call_id,
      pickup,
      destination,
      passengers,
      caller_number,
      call_start_at,
      call_end_at,
      total_turns
    } = await req.json();

    console.log(`[${call_id}] Webhook received: ${action}`);

    // Initialize Supabase client
    const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

    if (action === "book_taxi") {
      // Calculate estimated fare
      const isAirport = destination?.toLowerCase().includes("airport");
      const is6Seater = passengers && parseInt(passengers) > 4;
      let baseFare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15; // £15-25 for city
      if (is6Seater) baseFare += 5;
      const estimatedFare = `£${baseFare}`;
      const eta = `${Math.floor(Math.random() * 4) + 5} minutes`; // 5-8 minutes

      // Log the confirmed booking
      const { error: logError } = await supabase.from("call_logs").insert({
        call_id,
        pickup,
        destination,
        passengers: passengers ? parseInt(passengers) : null,
        estimated_fare: estimatedFare,
        booking_status: "confirmed",
        call_start_at: call_start_at || new Date().toISOString(),
      });

      if (logError) {
        console.error(`[${call_id}] Failed to log booking:`, logError);
      }

      console.log(`[${call_id}] Booking confirmed: ${pickup} → ${destination}`);

      // Return confirmation for OpenAI to speak
      return new Response(JSON.stringify({
        success: true,
        booking_id: call_id,
        pickup,
        destination,
        passengers,
        estimated_fare: estimatedFare,
        eta,
        message: `Taxi booked from ${pickup} to ${destination}. ${eta} away. Fare approximately ${estimatedFare}.`
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    if (action === "call_ended") {
      // Update call log with end time and total turns
      const { error: updateError } = await supabase
        .from("call_logs")
        .update({
          call_end_at: call_end_at || new Date().toISOString(),
        })
        .eq("call_id", call_id);

      if (updateError) {
        console.error(`[${call_id}] Failed to update call end:`, updateError);
      }

      console.log(`[${call_id}] Call ended, ${total_turns} turns`);

      return new Response(JSON.stringify({ success: true }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    if (action === "check_availability") {
      // Mock availability check - always available
      return new Response(JSON.stringify({
        available: true,
        vehicle_types: ["saloon", "estate", "6-seater"],
        eta_range: "5-8 minutes"
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    return new Response(JSON.stringify({ error: "Unknown action" }), {
      status: 400,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("Booking webhook error:", error);
    return new Response(JSON.stringify({ 
      error: error instanceof Error ? error.message : "Unknown error" 
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
