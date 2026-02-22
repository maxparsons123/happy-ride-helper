import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version",
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

    const { token } = await req.json();
    if (!token) {
      return new Response(
        JSON.stringify({ error: "Missing token" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Fetch the submitted booking
    const { data: booking, error: fetchErr } = await supabase
      .from("airport_booking_links")
      .select("*")
      .eq("token", token)
      .eq("status", "submitted")
      .single();

    if (fetchErr || !booking) {
      console.error("Booking not found:", fetchErr);
      return new Response(
        JSON.stringify({ error: "Booking not found or not submitted" }),
        { status: 404, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    console.log(`📋 Airport booking dispatch: ${booking.pickup} → ${booking.destination}`);

    // Get company iCabbi config
    let icabbiConfig = null;
    if (booking.company_id) {
      const { data: company } = await supabase
        .from("companies")
        .select("icabbi_enabled, icabbi_app_key, icabbi_secret_key, icabbi_tenant_base")
        .eq("id", booking.company_id)
        .single();
      
      if (company?.icabbi_enabled && company.icabbi_app_key && company.icabbi_secret_key) {
        icabbiConfig = company;
      }
    }

    // If no company-specific config, try to find a company with iCabbi enabled
    if (!icabbiConfig) {
      const { data: companies } = await supabase
        .from("companies")
        .select("id, icabbi_enabled, icabbi_app_key, icabbi_secret_key, icabbi_tenant_base")
        .eq("icabbi_enabled", true)
        .limit(1);
      
      if (companies && companies.length > 0 && companies[0].icabbi_app_key) {
        icabbiConfig = companies[0];
      }
    }

    if (!icabbiConfig) {
      console.log("⚠️ No iCabbi config found — skipping dispatch");
      return new Response(
        JSON.stringify({ success: true, dispatched: false, reason: "No iCabbi configuration" }),
        { headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    // Format phone number for iCabbi (E.164)
    let phone = booking.caller_phone || "";
    if (phone && !phone.startsWith("+")) {
      phone = phone.startsWith("0") ? "+44" + phone.slice(1) : "+" + phone;
    }

    // Build scheduled date
    const travelDate = booking.travel_datetime
      ? new Date(booking.travel_datetime).toISOString().replace(/\.\d{3}Z$/, "Z")
      : new Date(Date.now() + 5 * 60000).toISOString().replace(/\.\d{3}Z$/, "Z");

    // Build notes
    const notes: string[] = [];
    if (booking.vehicle_type) notes.push(`Vehicle: ${booking.vehicle_type}`);
    if (booking.flight_number) notes.push(`Flight: ${booking.flight_number}`);
    if (booking.luggage_suitcases) notes.push(`Suitcases: ${booking.luggage_suitcases}`);
    if (booking.luggage_hand) notes.push(`Hand luggage: ${booking.luggage_hand}`);
    if (booking.special_instructions) notes.push(`Instructions: ${booking.special_instructions}`);
    if (booking.return_trip) {
      notes.push("RETURN TRIP REQUESTED");
      if (booking.return_datetime) notes.push(`Return: ${new Date(booking.return_datetime).toLocaleString("en-GB")}`);
      if (booking.return_flight_number) notes.push(`Return flight: ${booking.return_flight_number}`);
    }

    // Get fare from quotes
    const fareQuotes = booking.fare_quotes as Record<string, { fare: string }> | null;
    const vehicleFare = fareQuotes?.[booking.vehicle_type || ""]?.fare;

    const payload = {
      source: "APP",
      date: travelDate,
      name: booking.caller_name || "Airport Customer",
      phone,
      account_id: 9428,
      account_name: "WhatsUrRide",
      address: {
        formatted: booking.pickup || "",
        lat: booking.pickup_lat || 0,
        lng: booking.pickup_lon || 0,
      },
      destination: {
        formatted: booking.destination || "",
        lat: booking.dest_lat || 0,
        lng: booking.dest_lon || 0,
      },
      site_id: 1039,
      status: "NEW",
      notes: notes.join(" | "),
      passengers: booking.passengers || 1,
    };

    const baseUrl = (icabbiConfig.icabbi_tenant_base || "https://api.icabbi.com/uk/").replace(/\/$/, "") + "/";
    const apiUrl = baseUrl.includes("api.icabbi.com") ? baseUrl : "https://api.icabbi.com/uk/";
    
    const authToken = btoa(`${icabbiConfig.icabbi_app_key}:${icabbiConfig.icabbi_secret_key}`);

    console.log(`📤 iCabbi booking payload: ${JSON.stringify(payload).slice(0, 500)}`);

    const icabbiResp = await fetch(`${apiUrl}bookings/add`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Basic ${authToken}`,
      },
      body: JSON.stringify(payload),
    });

    const icabbiBody = await icabbiResp.text();
    console.log(`📨 iCabbi response ${icabbiResp.status}: ${icabbiBody.slice(0, 500)}`);

    if (!icabbiResp.ok) {
      console.error("❌ iCabbi booking failed:", icabbiBody);
      return new Response(
        JSON.stringify({ success: false, error: "iCabbi booking failed", details: icabbiBody }),
        { status: 502, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    let journeyId = null;
    let trackingUrl = null;
    try {
      const parsed = JSON.parse(icabbiBody);
      journeyId = parsed?.body?.booking?.id;
      trackingUrl = parsed?.body?.booking?.tracking_url;
      if (!trackingUrl && journeyId) {
        const tenant = icabbiConfig.icabbi_tenant_base || "https://yourtenant.icabbi.net";
        trackingUrl = `${tenant.replace(/\/$/, "")}/passenger/tracking/${journeyId}`;
      }
    } catch {
      console.warn("Could not parse iCabbi response");
    }

    console.log(`✅ iCabbi booking created — Journey: ${journeyId}`);

    // Also create a booking record in our bookings table
    await supabase.from("bookings").insert({
      call_id: booking.call_id || `airport-${token.slice(0, 8)}`,
      caller_phone: booking.caller_phone || "unknown",
      caller_name: booking.caller_name,
      pickup: booking.pickup || "",
      destination: booking.destination || "",
      passengers: booking.passengers || 1,
      fare: vehicleFare ? `£${vehicleFare}` : null,
      pickup_lat: booking.pickup_lat,
      pickup_lng: booking.pickup_lon,
      dest_lat: booking.dest_lat,
      dest_lng: booking.dest_lon,
      company_id: booking.company_id,
      scheduled_for: booking.travel_datetime,
      booking_details: {
        source: "airport_booking_link",
        vehicle_type: booking.vehicle_type,
        flight_number: booking.flight_number,
        luggage_suitcases: booking.luggage_suitcases,
        luggage_hand: booking.luggage_hand,
        special_instructions: booking.special_instructions,
        return_trip: booking.return_trip,
        return_datetime: booking.return_datetime,
        return_flight_number: booking.return_flight_number,
        icabbi_journey_id: journeyId,
        tracking_url: trackingUrl,
      },
    });

    // Handle return trip if requested
    if (booking.return_trip && booking.return_datetime) {
      const returnNotes = [...notes.filter(n => !n.startsWith("RETURN")), "RETURN LEG of airport booking"];
      if (booking.return_flight_number) returnNotes.push(`Flight: ${booking.return_flight_number}`);

      const returnDate = new Date(booking.return_datetime).toISOString().replace(/\.\d{3}Z$/, "Z");

      const returnPayload = {
        ...payload,
        date: returnDate,
        // Swap pickup and destination for return
        address: payload.destination,
        destination: payload.address,
        notes: returnNotes.join(" | "),
      };

      console.log(`📤 iCabbi RETURN booking: ${JSON.stringify(returnPayload).slice(0, 300)}`);

      const returnResp = await fetch(`${apiUrl}bookings/add`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Basic ${authToken}`,
        },
        body: JSON.stringify(returnPayload),
      });

      const returnBody = await returnResp.text();
      console.log(`📨 iCabbi return response ${returnResp.status}: ${returnBody.slice(0, 300)}`);

      let returnJourneyId = null;
      try {
        const parsed = JSON.parse(returnBody);
        returnJourneyId = parsed?.body?.booking?.id;
      } catch {}

      if (returnResp.ok) {
        console.log(`✅ Return booking created — Journey: ${returnJourneyId}`);

        // Calculate discounted fare
        let returnFare = vehicleFare;
        if (vehicleFare && booking.return_discount_pct) {
          const numFare = parseFloat(vehicleFare);
          const discounted = numFare * (1 - booking.return_discount_pct / 100);
          returnFare = discounted.toFixed(2);
        }

        await supabase.from("bookings").insert({
          call_id: `airport-return-${token.slice(0, 8)}`,
          caller_phone: booking.caller_phone || "unknown",
          caller_name: booking.caller_name,
          pickup: booking.destination || "",
          destination: booking.pickup || "",
          passengers: booking.passengers || 1,
          fare: returnFare ? `£${returnFare}` : null,
          pickup_lat: booking.dest_lat,
          pickup_lng: booking.dest_lon,
          dest_lat: booking.pickup_lat,
          dest_lng: booking.pickup_lon,
          company_id: booking.company_id,
          scheduled_for: booking.return_datetime,
          booking_details: {
            source: "airport_booking_link_return",
            vehicle_type: booking.vehicle_type,
            flight_number: booking.return_flight_number,
            icabbi_journey_id: returnJourneyId,
            return_of: journeyId,
          },
        });
      }
    }

    return new Response(
      JSON.stringify({
        success: true,
        dispatched: true,
        journey_id: journeyId,
        tracking_url: trackingUrl,
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (err) {
    console.error("airport-booking-dispatch error:", err);
    return new Response(
      JSON.stringify({ error: err.message }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
