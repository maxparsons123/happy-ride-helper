import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const url = new URL(req.url);
  const token = url.searchParams.get("token");

  if (!token) {
    console.error("[sip-incoming] No token provided");
    return new Response(JSON.stringify({ error: "Missing token" }), {
      status: 401,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Initialize Supabase
  const supabaseUrl = Deno.env.get("SUPABASE_URL")!;
  const supabaseKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
  const supabase = createClient(supabaseUrl, supabaseKey);

  // Validate token against sip_trunks table
  const { data: trunk, error: trunkError } = await supabase
    .from("sip_trunks")
    .select("*")
    .eq("webhook_token", token)
    .eq("is_active", true)
    .single();

  if (trunkError || !trunk) {
    console.error("[sip-incoming] Invalid or inactive token:", token);
    return new Response(JSON.stringify({ error: "Invalid or inactive token" }), {
      status: 401,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  console.log(`[sip-incoming] Valid trunk: ${trunk.name} (${trunk.id})`);

  // Check if this is a WebSocket upgrade request (for audio streaming)
  const upgradeHeader = req.headers.get("upgrade");
  if (upgradeHeader?.toLowerCase() === "websocket") {
    // Handle WebSocket connection for real-time audio
    const { socket, response } = Deno.upgradeWebSocket(req);
    
    const callId = `sip-${trunk.id.slice(0, 8)}-${Date.now()}`;
    console.log(`[sip-incoming] WebSocket connection for call: ${callId}`);

    socket.onopen = () => {
      console.log(`[${callId}] WebSocket opened`);
      
      // Create live call record (use upsert to handle reconnects)
      supabase.from("live_calls").upsert({
        call_id: callId,
        source: `sip:${trunk.name}`,
        status: "active",
        transcripts: [],
        updated_at: new Date().toISOString(),
      }, { onConflict: "call_id" }).then(({ error }) => {
        if (error) console.error(`[${callId}] Error creating live call:`, error);
      });

      // Send session ready message
      socket.send(JSON.stringify({
        type: "session.ready",
        call_id: callId,
        trunk_name: trunk.name,
      }));
    };

    socket.onmessage = async (event) => {
      try {
        const message = JSON.parse(event.data);
        console.log(`[${callId}] Received:`, message.type);

        // Forward to taxi-realtime function
        // In production, this would connect to the main realtime handler
        if (message.type === "audio") {
          // Handle audio data - forward to processing pipeline
          console.log(`[${callId}] Audio chunk received, length: ${message.data?.length || 0}`);
        }
      } catch (e) {
        console.error(`[${callId}] Error processing message:`, e);
      }
    };

    socket.onclose = () => {
      console.log(`[${callId}] WebSocket closed`);
      
      // Update live call status
      supabase.from("live_calls")
        .update({ 
          status: "ended", 
          ended_at: new Date().toISOString() 
        })
        .eq("call_id", callId)
        .then(({ error }) => {
          if (error) console.error(`[${callId}] Error updating live call:`, error);
        });
    };

    socket.onerror = (e) => {
      console.error(`[${callId}] WebSocket error:`, e);
    };

    return response;
  }

  // Handle HTTP POST for webhook-based providers (Twilio, Telnyx, etc.)
  if (req.method === "POST") {
    try {
      const body = await req.json();
      console.log(`[sip-incoming] Webhook received:`, JSON.stringify(body, null, 2));

      const callId = body.CallSid || body.call_control_id || body.call_id || `sip-${Date.now()}`;
      const callerPhone = body.From || body.from || body.caller_id || "unknown";
      const calledNumber = body.To || body.to || body.destination || trunk.sip_username;

      console.log(`[sip-incoming] Call ${callId} from ${callerPhone} to ${calledNumber}`);

      // Create live call record
      await supabase.from("live_calls").insert({
        call_id: callId,
        caller_phone: callerPhone,
        source: `sip:${trunk.name}`,
        status: "active",
        transcripts: [],
      });

      // Return WebSocket URL for the provider to stream audio to
      const wsUrl = `wss://${url.host}/functions/v1/taxi-realtime?call_id=${callId}&source=sip&trunk=${trunk.id}`;

      // Twilio-style response
      if (body.CallSid) {
        return new Response(
          `<?xml version="1.0" encoding="UTF-8"?>
<Response>
  <Connect>
    <Stream url="${wsUrl}" />
  </Connect>
</Response>`,
          {
            headers: { ...corsHeaders, "Content-Type": "application/xml" },
          }
        );
      }

      // Telnyx-style response
      if (body.call_control_id) {
        return new Response(
          JSON.stringify({
            data: {
              command: "audio_stream",
              audio_url: wsUrl,
              format: "raw",
              encoding: "linear16",
              sample_rate: 16000,
            },
          }),
          {
            headers: { ...corsHeaders, "Content-Type": "application/json" },
          }
        );
      }

      // Generic response with connection info
      return new Response(
        JSON.stringify({
          success: true,
          call_id: callId,
          websocket_url: wsUrl,
          message: "Connect your audio stream to the WebSocket URL",
        }),
        {
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        }
      );
    } catch (e) {
      const errorMessage = e instanceof Error ? e.message : "Unknown error";
      console.error("[sip-incoming] Error processing webhook:", e);
      return new Response(
        JSON.stringify({ error: "Failed to process webhook", details: errorMessage }),
        {
          status: 500,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        }
      );
    }
  }

  // GET request - return trunk info and instructions
  return new Response(
    JSON.stringify({
      trunk_name: trunk.name,
      trunk_id: trunk.id,
      status: "ready",
      instructions: {
        twilio: "Configure your Twilio number's webhook to POST to this URL",
        telnyx: "Set this URL as your TeXML webhook",
        websocket: "Connect via WebSocket with upgrade header for direct audio streaming",
      },
    }),
    {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    }
  );
});
