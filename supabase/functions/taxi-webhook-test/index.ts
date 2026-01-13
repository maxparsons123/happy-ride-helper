import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Simple in-memory store for pending responses (with timeout cleanup)
const pendingResponses = new Map<string, {
  resolve: (response: any) => void;
  timestamp: number;
}>();

// Cleanup old pending responses every 30 seconds
setInterval(() => {
  const now = Date.now();
  for (const [id, entry] of pendingResponses.entries()) {
    if (now - entry.timestamp > 60000) { // 60 second timeout
      pendingResponses.delete(id);
    }
  }
}, 30000);

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

  try {
    const url = new URL(req.url);
    const pathParts = url.pathname.split("/").filter(Boolean);
    const action = pathParts[pathParts.length - 1];

    // POST /taxi-webhook-test - Receive webhook from taxi-passthrough-ws
    if (req.method === "POST" && (action === "taxi-webhook-test" || !action || action === "")) {
      const payload = await req.json();
      console.log("[Webhook Test] Received request:", JSON.stringify(payload, null, 2));

      const requestId = `${payload.call_id}-${Date.now()}`;

      // Broadcast to connected UI clients
      const channel = supabase.channel("webhook-test");
      await channel.send({
        type: "broadcast",
        event: "webhook_request",
        payload: {
          ...payload,
          request_id: requestId,
        },
      });

      // Wait for response from UI (with 30 second timeout)
      const response = await new Promise<any>((resolve) => {
        const timeout = setTimeout(() => {
          pendingResponses.delete(requestId);
          // Default response if no UI response
          resolve({
            ada_response: "Thank you, I'm processing your request. One moment please.",
          });
        }, 30000);

        pendingResponses.set(requestId, {
          resolve: (response) => {
            clearTimeout(timeout);
            pendingResponses.delete(requestId);
            resolve(response);
          },
          timestamp: Date.now(),
        });

        // Also listen for realtime response
        const responseChannel = supabase.channel(`response-${requestId}`);
        responseChannel
          .on("broadcast", { event: "webhook_response" }, (msg) => {
            if (msg.payload?.call_id === payload.call_id) {
              clearTimeout(timeout);
              pendingResponses.delete(requestId);
              responseChannel.unsubscribe();
              resolve(msg.payload.response);
            }
          })
          .subscribe();
      });

      console.log("[Webhook Test] Sending response:", JSON.stringify(response, null, 2));

      return new Response(JSON.stringify(response), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // POST /taxi-webhook-test/respond - UI sends response
    if (req.method === "POST" && action === "respond") {
      const { request_id, call_id, response } = await req.json();
      console.log("[Webhook Test] UI response for:", request_id || call_id, response);

      // Try to resolve pending request
      if (request_id && pendingResponses.has(request_id)) {
        pendingResponses.get(request_id)!.resolve(response);
      }

      // Also broadcast for any listeners
      const channel = supabase.channel("webhook-test");
      await channel.send({
        type: "broadcast",
        event: "webhook_response",
        payload: { call_id, response },
      });

      return new Response(JSON.stringify({ success: true }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    return new Response(JSON.stringify({ error: "Not found" }), {
      status: 404,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("[Webhook Test] Error:", error);
    const message = error instanceof Error ? error.message : "Unknown error";
    return new Response(JSON.stringify({ error: message }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
