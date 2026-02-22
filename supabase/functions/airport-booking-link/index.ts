import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req: Request) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const supabase = createClient(
      Deno.env.get("SUPABASE_URL")!,
      Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!
    );

    const body = await req.json();
    const { action } = body;

    if (action === "create") {
      // Create a new booking link with pre-filled data from the call session
      const {
        caller_name, caller_phone, pickup, destination, passengers,
        pickup_lat, pickup_lon, dest_lat, dest_lon,
        call_id, company_id, fare_quotes
      } = body;

      const { data, error } = await supabase
        .from("airport_booking_links")
        .insert({
          caller_name,
          caller_phone,
          pickup,
          destination,
          passengers: passengers || 1,
          pickup_lat, pickup_lon,
          dest_lat, dest_lon,
          call_id,
          company_id,
          fare_quotes: fare_quotes || {},
        })
        .select("token")
        .single();

      if (error) throw error;

      const baseUrl = Deno.env.get("SUPABASE_URL")!.replace(".supabase.co", "");
      // The booking link uses the frontend app URL
      const bookingUrl = `${req.headers.get("origin") || "https://happy-ride-helper.lovable.app"}/book/${data.token}`;

      return new Response(
        JSON.stringify({ token: data.token, url: bookingUrl }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    if (action === "get") {
      const { token } = body;
      const { data, error } = await supabase
        .from("airport_booking_links")
        .select("*")
        .eq("token", token)
        .single();

      if (error) throw error;

      return new Response(
        JSON.stringify(data),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    return new Response(
      JSON.stringify({ error: "Unknown action" }),
      { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (err) {
    console.error("airport-booking-link error:", err);
    return new Response(
      JSON.stringify({ error: err.message }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
