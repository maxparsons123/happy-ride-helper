import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

serve(async (req) => {
  // Handle CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);
    const body = await req.json();

    console.log("📍 GPS Update received:", JSON.stringify(body));

    const { phone_number, lat, lon, call_id } = body;

    // Validate required fields
    if (!lat || !lon) {
      return new Response(
        JSON.stringify({ success: false, error: "Missing lat/lon coordinates" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    if (!phone_number && !call_id) {
      return new Response(
        JSON.stringify({ success: false, error: "Must provide phone_number or call_id" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const now = new Date().toISOString();

    // If call_id provided, update live_calls directly
    if (call_id) {
      const { error: updateError } = await supabase
        .from("live_calls")
        .update({
          gps_lat: lat,
          gps_lon: lon,
          gps_updated_at: now,
        })
        .eq("call_id", call_id);

      if (updateError) {
        console.error("❌ Error updating live_calls GPS:", updateError);
        return new Response(
          JSON.stringify({ success: false, error: updateError.message }),
          { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }

      console.log(`✅ GPS updated for call_id ${call_id}: ${lat}, ${lon}`);
      return new Response(
        JSON.stringify({ success: true, message: "GPS updated for call", call_id }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // If phone_number provided, store in caller_gps for pre-call lookup
    if (phone_number) {
      // Normalize phone number
      let normalized = phone_number.replace(/\s+/g, "").replace(/-/g, "");
      if (!normalized.startsWith("+") && normalized.length >= 10) {
        // Try to add + if missing
        if (normalized.startsWith("00")) {
          normalized = "+" + normalized.slice(2);
        } else if (/^(44|1|33|49|31)\d+$/.test(normalized)) {
          normalized = "+" + normalized;
        }
      }

      // Upsert: delete old entry and insert new
      await supabase
        .from("caller_gps")
        .delete()
        .eq("phone_number", normalized);

      const { error: insertError } = await supabase
        .from("caller_gps")
        .insert({
          phone_number: normalized,
          lat,
          lon,
          expires_at: new Date(Date.now() + 10 * 60 * 1000).toISOString(), // 10 min expiry
        });

      if (insertError) {
        console.error("❌ Error inserting caller_gps:", insertError);
        return new Response(
          JSON.stringify({ success: false, error: insertError.message }),
          { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
        );
      }

      console.log(`✅ GPS stored for phone ${normalized}: ${lat}, ${lon}`);
      return new Response(
        JSON.stringify({ success: true, message: "GPS stored for phone", phone_number: normalized }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    return new Response(
      JSON.stringify({ success: false, error: "No action taken" }),
      { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (error) {
    console.error("❌ GPS Update error:", error);
    const message = error instanceof Error ? error.message : "Unknown error";
    return new Response(
      JSON.stringify({ success: false, error: message }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
