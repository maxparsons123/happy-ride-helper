import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type, x-supabase-client-platform, x-supabase-client-platform-version, x-supabase-client-runtime, x-supabase-client-runtime-version',
};

serve(async (req) => {
  if (req.method === 'OPTIONS') {
    return new Response(null, { headers: corsHeaders });
  }

  const PERPLEXITY_API_KEY = Deno.env.get('PERPLEXITY_API_KEY');
  if (!PERPLEXITY_API_KEY) {
    return new Response(JSON.stringify({ error: 'PERPLEXITY_API_KEY not configured' }), {
      status: 500,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    });
  }

  try {
    const { category = 'all', near = 'nearby', date = 'tonight' } = await req.json();

    const query = `What events, shows, concerts, comedy nights, sports, theatre, and entertainment are happening ${date} near ${near}?${category !== 'all' ? ` Focus on ${category} events.` : ''} List the top events with venue name, time, date, and event type. Be concise.`;

    const response = await fetch('https://api.perplexity.ai/chat/completions', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${PERPLEXITY_API_KEY}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        model: 'sonar',
        messages: [
          {
            role: 'system',
            content: 'You are a local events assistant for a taxi company. Return event information as a JSON array. Each event should have: name, venue, date, type (concert/comedy/theatre/sports/festival/other). Return ONLY valid JSON array, no markdown, no explanation. Example: [{"name":"Live Band at The Empire","venue":"The Empire","date":"Tonight 8pm","type":"concert"}]'
          },
          { role: 'user', content: query }
        ],
        temperature: 0.1,
      }),
    });

    if (!response.ok) {
      const errText = await response.text();
      console.error(`Perplexity API error [${response.status}]: ${errText}`);
      return new Response(JSON.stringify({ error: `Perplexity API error: ${response.status}` }), {
        status: 502,
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      });
    }

    const data = await response.json();
    const content = data.choices?.[0]?.message?.content ?? '[]';
    const citations = data.citations ?? [];

    // Try to parse the AI response as JSON array
    let events: unknown[];
    try {
      // Strip markdown code fences if present
      const cleaned = content.replace(/```json?\n?/g, '').replace(/```/g, '').trim();
      events = JSON.parse(cleaned);
      if (!Array.isArray(events)) events = [];
    } catch {
      // If parsing fails, return the raw text as a single event
      console.warn('Could not parse Perplexity response as JSON, returning raw text');
      events = [{ name: content, venue: near, date, type: 'info' }];
    }

    return new Response(JSON.stringify({
      success: true,
      events,
      citations,
      message: events.length > 0
        ? `Found ${events.length} events near ${near}. Would you like a taxi to any of these?`
        : `No events found near ${near} for ${date}.`,
    }), {
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    });
  } catch (error) {
    console.error('find-local-events error:', error);
    return new Response(JSON.stringify({ error: error.message }), {
      status: 500,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    });
  }
});
