import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type',
};

serve(async (req) => {
  if (req.method === 'OPTIONS') {
    return new Response(null, { headers: corsHeaders });
  }

  const apiKey = Deno.env.get("SUMUP_API_KEY");
  if (!apiKey) {
    return new Response(JSON.stringify({ error: "SUMUP_API_KEY not set" }), {
      status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" }
    });
  }

  try {
    const checkoutRef = `TEST-${Date.now()}`;
    const payload = {
      amount: 1.00,
      currency: "GBP",
      checkout_reference: checkoutRef,
      merchant_code: "MW93CBFR",
      pay_to_email: "MW93CBFR@sumup.com",
      description: "Test checkout from edge function"
    };

    console.log("📤 SumUp test payload:", JSON.stringify(payload));

    const resp = await fetch("https://api.sumup.com/v0.1/checkouts", {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${apiKey}`,
        "Content-Type": "application/json"
      },
      body: JSON.stringify(payload)
    });

    const body = await resp.text();
    console.log(`📨 SumUp response: ${resp.status} ${body}`);

    return new Response(JSON.stringify({
      status: resp.status,
      statusText: resp.statusText,
      body: body,
      keyPrefix: apiKey.substring(0, 10) + "..."
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" }
    });
  } catch (err) {
    console.error("❌ SumUp error:", err);
    return new Response(JSON.stringify({
      error: err.message,
      stack: err.stack
    }), {
      status: 500, headers: { ...corsHeaders, "Content-Type": "application/json" }
    });
  }
});
