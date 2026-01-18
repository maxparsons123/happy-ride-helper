import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { encode as base64Encode } from "https://deno.land/std@0.168.0/encoding/base64.ts";
import { create, getNumericDate } from "https://deno.land/x/djwt@v3.0.2/mod.ts";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const { roomName, participantName } = await req.json();

    if (!roomName || !participantName) {
      return new Response(
        JSON.stringify({ error: "roomName and participantName are required" }),
        { status: 400, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    const apiKey = Deno.env.get("LIVEKIT_API_KEY");
    const apiSecret = Deno.env.get("LIVEKIT_API_SECRET");
    const livekitUrl = Deno.env.get("LIVEKIT_URL");

    if (!apiKey || !apiSecret || !livekitUrl) {
      console.error("Missing LiveKit configuration");
      return new Response(
        JSON.stringify({ error: "LiveKit not configured" }),
        { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
      );
    }

    console.log(`Creating token for room: ${roomName}, participant: ${participantName}`);

    // Create LiveKit access token manually using JWT
    const now = Math.floor(Date.now() / 1000);
    const exp = now + 3600; // 1 hour

    // LiveKit video grant
    const videoGrant = {
      room: roomName,
      roomJoin: true,
      canPublish: true,
      canSubscribe: true,
      canPublishData: true,
    };

    // JWT payload for LiveKit
    const payload = {
      iss: apiKey,
      sub: participantName,
      iat: now,
      exp: exp,
      nbf: now,
      jti: `${participantName}-${now}`,
      video: videoGrant,
    };

    // Create crypto key from secret
    const encoder = new TextEncoder();
    const keyData = encoder.encode(apiSecret);
    const cryptoKey = await crypto.subtle.importKey(
      "raw",
      keyData,
      { name: "HMAC", hash: "SHA-256" },
      false,
      ["sign"]
    );

    // Create JWT token
    const jwt = await create({ alg: "HS256", typ: "JWT" }, payload, cryptoKey);

    console.log(`Token created successfully for ${participantName}`);

    return new Response(
      JSON.stringify({ 
        token: jwt, 
        url: livekitUrl,
        roomName,
        participantName
      }),
      { headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  } catch (error: unknown) {
    console.error("Error creating LiveKit token:", error);
    const message = error instanceof Error ? error.message : "Failed to create token";
    return new Response(
      JSON.stringify({ error: message }),
      { status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" } }
    );
  }
});
