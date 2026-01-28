import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type, webhook-id, webhook-timestamp, webhook-signature',
};

// OpenAI API for accepting/rejecting calls
const OPENAI_API_BASE = "https://api.openai.com/v1/realtime/calls";

// System prompt for Ada taxi booking agent
const SYSTEM_PROMPT = `You are Ada, the friendly and efficient AI receptionist for a taxi booking service. Your job is to help callers book taxis quickly and naturally.

PERSONALITY:
- Warm, professional, and conversational
- Speak naturally like a human receptionist
- Be concise - don't over-explain

BOOKING FLOW:
1. Greet the caller warmly
2. Ask for pickup location
3. Ask for destination
4. Ask for number of passengers (if not mentioned)
5. Confirm the booking details
6. Thank them and end the call

IMPORTANT RULES:
- Always confirm addresses clearly
- If an address is unclear, ask for clarification
- Be patient with callers who are unsure
- Keep responses brief and natural

EXAMPLE GREETING:
"Hello, thanks for calling! Where would you like to be picked up from?"`;

serve(async (req) => {
  // Handle CORS preflight
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: corsHeaders });
  }

  const OPENAI_API_KEY = Deno.env.get('OPENAI_API_KEY');
  if (!OPENAI_API_KEY) {
    console.error('OPENAI_API_KEY not configured');
    return new Response(JSON.stringify({ error: 'Server configuration error' }), {
      status: 500,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    });
  }

  try {
    const body = await req.json();
    console.log('📞 Received webhook:', JSON.stringify(body, null, 2));

    const eventType = body.type;

    if (eventType === 'realtime.call.incoming') {
      const callId = body.data?.call_id;
      const sipHeaders = body.data?.sip_headers || [];
      
      // Extract caller info from SIP headers
      const fromHeader = sipHeaders.find((h: any) => h.name === 'From')?.value || 'unknown';
      const toHeader = sipHeaders.find((h: any) => h.name === 'To')?.value || 'unknown';
      
      console.log(`📱 Incoming call: ${callId}`);
      console.log(`   From: ${fromHeader}`);
      console.log(`   To: ${toHeader}`);

      // Accept the call with our taxi booking configuration
      const acceptResponse = await fetch(`${OPENAI_API_BASE}/${callId}/accept`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${OPENAI_API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          type: 'realtime',
          model: 'gpt-4o-realtime-preview',
          voice: 'shimmer',
          instructions: SYSTEM_PROMPT,
          input_audio_format: 'g711_ulaw',
          output_audio_format: 'g711_ulaw',
          input_audio_transcription: {
            model: 'whisper-1',
          },
          turn_detection: {
            type: 'server_vad',
            threshold: 0.5,
            prefix_padding_ms: 300,
            silence_duration_ms: 800,
          },
          tools: [
            {
              type: 'function',
              name: 'book_taxi',
              description: 'Book a taxi when the caller has confirmed all details',
              parameters: {
                type: 'object',
                properties: {
                  pickup: { type: 'string', description: 'Pickup address' },
                  destination: { type: 'string', description: 'Destination address' },
                  passengers: { type: 'number', description: 'Number of passengers' },
                  caller_phone: { type: 'string', description: 'Caller phone number if known' },
                },
                required: ['pickup', 'destination'],
              },
            },
          ],
          tool_choice: 'auto',
        }),
      });

      if (!acceptResponse.ok) {
        const errorText = await acceptResponse.text();
        console.error(`❌ Failed to accept call: ${acceptResponse.status}`, errorText);
        return new Response(JSON.stringify({ error: 'Failed to accept call', details: errorText }), {
          status: 500,
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
        });
      }

      const acceptData = await acceptResponse.json();
      console.log('✅ Call accepted:', JSON.stringify(acceptData, null, 2));

      // Start monitoring in background (fire and forget)
      // Note: Edge functions have ~30s runtime limit, so WebSocket monitoring
      // is limited. For production, use a persistent server.
      monitorCall(callId, OPENAI_API_KEY, fromHeader).catch(err => {
        console.error('Monitor error:', err);
      });

      return new Response(JSON.stringify({ 
        success: true, 
        call_id: callId,
        message: 'Call accepted and session configured' 
      }), {
        status: 200,
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      });
    }

    // Handle other webhook event types
    console.log(`ℹ️ Unhandled event type: ${eventType}`);
    return new Response(JSON.stringify({ received: true, type: eventType }), {
      status: 200,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    });

  } catch (error) {
    console.error('❌ Webhook error:', error);
    return new Response(JSON.stringify({ error: String(error) }), {
      status: 500,
      headers: { ...corsHeaders, 'Content-Type': 'application/json' },
    });
  }
});

// Monitor call events via WebSocket (limited by edge function runtime)
async function monitorCall(callId: string, apiKey: string, callerPhone: string): Promise<void> {
  console.log(`🔌 Starting WebSocket monitor for call: ${callId}`);
  
  return new Promise((resolve, reject) => {
    try {
      const ws = new WebSocket(`wss://api.openai.com/v1/realtime?call_id=${callId}`, [
        'realtime',
        `openai-insecure-api-key.${apiKey}`,
      ]);

      // Timeout after 25 seconds (edge function limit is ~30s)
      const timeout = setTimeout(() => {
        console.log('⏰ WebSocket timeout, closing connection');
        ws.close();
        resolve();
      }, 25000);

      ws.onopen = () => {
        console.log(`✅ WebSocket connected for call: ${callId}`);
        
        // Send initial greeting trigger
        ws.send(JSON.stringify({
          type: 'response.create',
          response: {
            modalities: ['text', 'audio'],
            instructions: 'Greet the caller warmly and ask where they would like to be picked up from.',
          },
        }));
      };

      ws.onmessage = (event) => {
        try {
          const data = JSON.parse(String(event.data));
          
          // Log key events
          if (data.type === 'response.audio_transcript.done') {
            console.log(`💬 Ada: ${data.transcript}`);
          } else if (data.type === 'conversation.item.input_audio_transcription.completed') {
            console.log(`🎤 Caller: ${data.transcript}`);
          } else if (data.type === 'response.function_call_arguments.done') {
            console.log(`🚕 Function call: ${data.name}`, data.arguments);
            handleFunctionCall(ws, data, callerPhone);
          } else if (data.type === 'error') {
            console.error(`❌ Realtime error:`, data.error);
          } else if (data.type === 'session.created') {
            console.log('✅ Session created');
          }
        } catch (e) {
          console.error('Failed to parse WebSocket message:', e);
        }
      };

      ws.onerror = (error) => {
        console.error(`❌ WebSocket error for call ${callId}:`, error);
        clearTimeout(timeout);
        reject(error);
      };

      ws.onclose = () => {
        console.log(`📴 Call ended: ${callId}`);
        clearTimeout(timeout);
        resolve();
      };

    } catch (error) {
      console.error(`❌ Failed to start WebSocket monitor:`, error);
      reject(error);
    }
  });
}

// Handle function calls from the AI
function handleFunctionCall(ws: WebSocket, data: any, callerPhone: string) {
  const { call_id, name, arguments: argsStr } = data;
  
  try {
    const args = JSON.parse(argsStr);
    
    if (name === 'book_taxi') {
      console.log('🚕 Booking taxi:', args);
      
      // Here you would call your dispatch webhook
      // For now, just confirm the booking
      const result = {
        success: true,
        booking_id: `BK-${Date.now()}`,
        eta: '5 minutes',
        fare_estimate: '£8.50',
      };
      
      // Send function result back to AI
      ws.send(JSON.stringify({
        type: 'conversation.item.create',
        item: {
          type: 'function_call_output',
          call_id: call_id,
          output: JSON.stringify(result),
        },
      }));
      
      // Trigger AI to respond with the result
      ws.send(JSON.stringify({
        type: 'response.create',
      }));
    }
  } catch (e) {
    console.error('Failed to handle function call:', e);
  }
}
