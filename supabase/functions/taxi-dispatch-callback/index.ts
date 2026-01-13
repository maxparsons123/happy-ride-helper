import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

/**
 * taxi-dispatch-callback
 * 
 * Webhook endpoint for external dispatch systems to send booking confirmations back.
 * When the dispatch system confirms a booking (with ETA, driver info, etc.),
 * this endpoint updates the call state and triggers Ada to speak the confirmation.
 * 
 * Expected POST body:
 * {
 *   "call_id": "abc123",
 *   "status": "dispatched" | "rejected" | "no_cars",
 *   "eta": "5 minutes",           // optional
 *   "driver_name": "John",        // optional
 *   "vehicle_reg": "AB12 CDE",    // optional
 *   "vehicle_type": "saloon",     // optional
 *   "fare": "£15.00",             // optional - dispatch can override
 *   "message": "Custom message"   // optional - for rejections
 * }
 */

interface DispatchCallback {
  call_id: string;
  status: "dispatched" | "rejected" | "no_cars" | "pending";
  eta?: string;
  driver_name?: string;
  vehicle_reg?: string;
  vehicle_type?: string;
  fare?: string;
  message?: string;
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");

    if (!SUPABASE_URL || !SUPABASE_SERVICE_ROLE_KEY) {
      throw new Error("Missing Supabase configuration");
    }

    const callback: DispatchCallback = await req.json();
    const { call_id, status, eta, driver_name, vehicle_reg, vehicle_type, fare, message } = callback;

    console.log(`[${call_id}] 📥 Dispatch callback received: ${status}`);
    console.log(`[${call_id}] Details:`, JSON.stringify({ eta, driver_name, vehicle_reg, fare }));

    if (!call_id || !status) {
      return new Response(JSON.stringify({ 
        error: "Missing required fields: call_id and status" 
      }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

    // Build confirmation message based on status
    let confirmationMessage: string;
    let bookingStatus: string;

    if (status === "dispatched") {
      bookingStatus = "dispatched";
      const parts: string[] = ["Brilliant! Your taxi is confirmed."];
      
      if (driver_name) {
        parts.push(`Your driver ${driver_name} is on the way.`);
      }
      if (eta) {
        parts.push(`They'll be with you in ${eta}.`);
      }
      if (vehicle_reg) {
        parts.push(`Look out for registration ${vehicle_reg}.`);
      }
      if (fare) {
        parts.push(`The fare is ${fare}.`);
      }
      parts.push("Is there anything else I can help you with?");
      
      confirmationMessage = parts.join(" ");
    } else if (status === "no_cars") {
      bookingStatus = "no_cars";
      confirmationMessage = message || "I'm really sorry, but we don't have any cars available at the moment. Would you like me to try again in a few minutes, or can I help with anything else?";
    } else if (status === "rejected") {
      bookingStatus = "rejected";
      confirmationMessage = message || "I'm sorry, but we're unable to take this booking at the moment. Is there anything else I can help you with?";
    } else {
      bookingStatus = "pending";
      confirmationMessage = "Your booking is being processed. Please hold on a moment.";
    }

    // Update live_calls with dispatch response
    const { error: updateError } = await supabase
      .from("live_calls")
      .update({
        status: bookingStatus,
        eta: eta || null,
        fare: fare || null,
        updated_at: new Date().toISOString()
      })
      .eq("call_id", call_id);

    if (updateError) {
      console.error(`[${call_id}] ❌ Failed to update live_calls:`, updateError);
    }

    // Update bookings table if dispatched
    if (status === "dispatched") {
      const bookingUpdate: Record<string, any> = {
        status: "dispatched",
        updated_at: new Date().toISOString()
      };
      if (eta) bookingUpdate.eta = eta;
      if (fare) bookingUpdate.fare = fare;
      if (driver_name || vehicle_reg) {
        bookingUpdate.booking_details = {
          driver_name: driver_name || null,
          vehicle_reg: vehicle_reg || null,
          vehicle_type: vehicle_type || null,
          dispatched_at: new Date().toISOString()
        };
      }

      await supabase
        .from("bookings")
        .update(bookingUpdate)
        .eq("call_id", call_id);
    }

    // Broadcast dispatch update to live calls page
    await supabase.channel(`dispatch_${call_id}`).send({
      type: "broadcast",
      event: "dispatch_callback",
      payload: {
        call_id,
        status,
        eta,
        driver_name,
        vehicle_reg,
        fare,
        confirmation_message: confirmationMessage
      }
    });

    console.log(`[${call_id}] ✅ Dispatch callback processed: ${status}`);

    return new Response(JSON.stringify({
      success: true,
      call_id,
      status: bookingStatus,
      confirmation_message: confirmationMessage
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("Dispatch callback error:", error);
    return new Response(JSON.stringify({ 
      error: error instanceof Error ? error.message : "Unknown error" 
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
