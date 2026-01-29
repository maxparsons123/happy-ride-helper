import { serve } from "https://deno.land/std@0.168.0/http/server.ts";

const corsHeaders = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type, webhook-id, webhook-timestamp, webhook-signature',
};

// OpenAI API for accepting/rejecting calls
const OPENAI_API_BASE = "https://api.openai.com/v1/realtime/calls";

// WhatsApp webhook URL template
const WHATSAPP_WEBHOOK_URL = "https://bsqd.me/api/bot/c443ed53-9769-48c3-a777-2f290bd9ba07/master/event/Avaya?api_key=sriifvfedn5ktsbw4for7noulxtapb2ff6wf326v&phoneNumber=";

// Country code to language mapping
const COUNTRY_CODE_TO_LANGUAGE: Record<string, string> = {
  "+31": "nl",
  "+32": "nl",
  "+33": "fr",
  "+41": "de",
  "+43": "de",
  "+49": "de",
};

// System prompts by language
const SYSTEM_PROMPTS: Record<string, string> = {
  en: `You are Ada, the friendly and efficient AI receptionist for a taxi booking service. Your job is to help callers book taxis quickly and naturally.

LANGUAGE: Speak ONLY in English.

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
6. Thank them and say goodbye

IMPORTANT RULES:
- Always confirm addresses clearly
- If an address is unclear, ask for clarification
- Be patient with callers who are unsure
- Keep responses brief and natural
- After confirming the booking, say a warm goodbye and call the end_call function

EXAMPLE GREETING:
"Hello, thanks for calling! Where would you like to be picked up from?"`,

  nl: `Je bent Ada, de vriendelijke en efficiënte AI-receptioniste voor een taxibookingservice. Je helpt bellers om snel en natuurlijk taxi's te boeken.

TAAL: Spreek ALLEEN in het Nederlands.

PERSOONLIJKHEID:
- Warm, professioneel en conversationeel
- Spreek natuurlijk zoals een menselijke receptioniste
- Wees beknopt - niet te veel uitleggen

BOEKINGSPROCES:
1. Begroet de beller hartelijk
2. Vraag naar de ophaallocatie
3. Vraag naar de bestemming
4. Vraag naar het aantal passagiers (indien niet vermeld)
5. Bevestig de boekingsdetails
6. Bedank hen en neem afscheid

BELANGRIJKE REGELS:
- Bevestig adressen altijd duidelijk
- Bij onduidelijke adressen, vraag om verduidelijking
- Wees geduldig met bellers die onzeker zijn
- Houd antwoorden kort en natuurlijk
- Roep na bevestiging van de boeking de end_call functie aan

VOORBEELD BEGROETING:
"Hallo, bedankt voor uw telefoontje! Waar mag ik u ophalen?"`,

  de: `Sie sind Ada, die freundliche und effiziente KI-Rezeptionistin für einen Taxi-Buchungsservice. Ihre Aufgabe ist es, Anrufern zu helfen, schnell und natürlich Taxis zu buchen.

SPRACHE: Sprechen Sie NUR auf Deutsch.

PERSÖNLICHKEIT:
- Warm, professionell und gesprächig
- Sprechen Sie natürlich wie eine menschliche Rezeptionistin
- Seien Sie prägnant - nicht zu viel erklären

BUCHUNGSABLAUF:
1. Begrüßen Sie den Anrufer herzlich
2. Fragen Sie nach dem Abholort
3. Fragen Sie nach dem Ziel
4. Fragen Sie nach der Anzahl der Passagiere (falls nicht erwähnt)
5. Bestätigen Sie die Buchungsdetails
6. Danken Sie ihnen und verabschieden Sie sich

WICHTIGE REGELN:
- Bestätigen Sie Adressen immer deutlich
- Bei unklaren Adressen, fragen Sie nach Klärung
- Seien Sie geduldig mit unsicheren Anrufern
- Halten Sie Antworten kurz und natürlich
- Rufen Sie nach Bestätigung der Buchung die end_call Funktion auf

BEISPIEL-BEGRÜSSUNG:
"Hallo, danke für Ihren Anruf! Wo darf ich Sie abholen?"`,

  fr: `Vous êtes Ada, la réceptionniste IA amicale et efficace pour un service de réservation de taxi. Votre travail est d'aider les appelants à réserver des taxis rapidement et naturellement.

LANGUE: Parlez UNIQUEMENT en français.

PERSONNALITÉ:
- Chaleureuse, professionnelle et conversationnelle
- Parlez naturellement comme une réceptionniste humaine
- Soyez concise - n'expliquez pas trop

PROCESSUS DE RÉSERVATION:
1. Saluez chaleureusement l'appelant
2. Demandez le lieu de prise en charge
3. Demandez la destination
4. Demandez le nombre de passagers (si non mentionné)
5. Confirmez les détails de la réservation
6. Remerciez-les et dites au revoir

RÈGLES IMPORTANTES:
- Confirmez toujours les adresses clairement
- Si une adresse n'est pas claire, demandez des précisions
- Soyez patiente avec les appelants incertains
- Gardez les réponses brèves et naturelles
- Après confirmation de la réservation, appelez la fonction end_call

EXEMPLE DE SALUTATION:
"Bonjour, merci d'avoir appelé! Où souhaitez-vous être pris en charge?"`,
};

// Language-specific greetings for the initial response
const GREETINGS: Record<string, string> = {
  en: "Greet the caller warmly in English and ask where they would like to be picked up from.",
  nl: "Begroet de beller hartelijk in het Nederlands en vraag waar ze opgehaald willen worden.",
  de: "Begrüßen Sie den Anrufer herzlich auf Deutsch und fragen Sie, wo er abgeholt werden möchte.",
  fr: "Saluez chaleureusement l'appelant en français et demandez où il souhaite être pris en charge.",
};

/**
 * Extract phone number from SIP From header
 * Format: "Name" <sip:+31612345678@domain> or sip:+31612345678@domain
 */
function extractPhoneNumber(fromHeader: string): string {
  // Try to extract number from SIP URI
  const sipMatch = fromHeader.match(/sip:([+\d]+)@/);
  if (sipMatch) return sipMatch[1];
  
  // Try to extract from tel: URI
  const telMatch = fromHeader.match(/tel:([+\d]+)/);
  if (telMatch) return telMatch[1];
  
  // Try to find any phone number pattern
  const phoneMatch = fromHeader.match(/([+]?\d{10,15})/);
  if (phoneMatch) return phoneMatch[1];
  
  return fromHeader;
}

/**
 * Detect language from phone number country code
 */
function detectLanguage(phoneNumber: string): string {
  for (const [countryCode, language] of Object.entries(COUNTRY_CODE_TO_LANGUAGE)) {
    if (phoneNumber.startsWith(countryCode)) {
      console.log(`🌍 Detected country code ${countryCode} → language: ${language}`);
      return language;
    }
  }
  console.log(`🌍 No matching country code, defaulting to English`);
  return "en";
}

/**
 * Format phone number for WhatsApp (remove + prefix, clean)
 */
function formatPhoneForWhatsApp(phone: string): string {
  return phone.replace(/^\+/, '').replace(/[^0-9]/g, '');
}

/**
 * Send WhatsApp marketing template to caller
 */
async function sendWhatsAppNotification(phoneNumber: string): Promise<void> {
  try {
    const cleanPhone = formatPhoneForWhatsApp(phoneNumber);
    const webhookUrl = `${WHATSAPP_WEBHOOK_URL}${encodeURIComponent(cleanPhone)}`;
    
    console.log(`📱 Sending WhatsApp notification to: ${cleanPhone}`);
    
    const response = await fetch(webhookUrl, {
      method: 'GET', // Appears to be a GET webhook based on URL structure
    });
    
    if (response.ok) {
      console.log(`✅ WhatsApp notification sent successfully`);
    } else {
      console.error(`❌ WhatsApp notification failed: ${response.status}`);
    }
  } catch (error) {
    console.error(`❌ WhatsApp notification error:`, error);
  }
}

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
      
      // Extract phone number and detect language
      const callerPhone = extractPhoneNumber(fromHeader);
      const language = detectLanguage(callerPhone);
      const systemPrompt = SYSTEM_PROMPTS[language] || SYSTEM_PROMPTS.en;
      
      console.log(`📱 Incoming call: ${callId}`);
      console.log(`   From: ${fromHeader}`);
      console.log(`   Phone: ${callerPhone}`);
      console.log(`   Language: ${language}`);
      console.log(`   To: ${toHeader}`);

      // Accept the call with language-specific configuration
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
          instructions: systemPrompt,
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
            {
              type: 'function',
              name: 'end_call',
              description: 'End the call after saying goodbye. Call this after the booking is confirmed and you have thanked the caller.',
              parameters: {
                type: 'object',
                properties: {
                  reason: { type: 'string', description: 'Reason for ending the call (e.g., booking_complete, caller_request)' },
                },
                required: ['reason'],
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
      monitorCall(callId, OPENAI_API_KEY, callerPhone, language).catch(err => {
        console.error('Monitor error:', err);
      });

      return new Response(JSON.stringify({ 
        success: true, 
        call_id: callId,
        language: language,
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
async function monitorCall(callId: string, apiKey: string, callerPhone: string, language: string): Promise<void> {
  console.log(`🔌 Starting WebSocket monitor for call: ${callId} (language: ${language})`);
  
  return new Promise((resolve, reject) => {
    try {
      const ws = new WebSocket(`wss://api.openai.com/v1/realtime?call_id=${callId}`, [
        'realtime',
        `openai-insecure-api-key.${apiKey}`,
      ]);

      let bookingCompleted = false;

      // Timeout after 25 seconds (edge function limit is ~30s)
      const timeout = setTimeout(() => {
        console.log('⏰ WebSocket timeout, closing connection');
        ws.close();
        resolve();
      }, 25000);

      ws.onopen = () => {
        console.log(`✅ WebSocket connected for call: ${callId}`);
        
        // Send initial greeting trigger in the detected language
        const greeting = GREETINGS[language] || GREETINGS.en;
        ws.send(JSON.stringify({
          type: 'response.create',
          response: {
            modalities: ['text', 'audio'],
            instructions: greeting,
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
            handleFunctionCall(ws, data, callerPhone, callId, apiKey, () => {
              bookingCompleted = true;
            });
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
function handleFunctionCall(
  ws: WebSocket, 
  data: any, 
  callerPhone: string, 
  callId: string, 
  apiKey: string,
  onBookingComplete: () => void
) {
  const { call_id: functionCallId, name, arguments: argsStr } = data;
  
  try {
    const args = JSON.parse(argsStr);
    
    if (name === 'book_taxi') {
      console.log('🚕 Booking taxi:', args);
      
      // Here you would call your dispatch webhook
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
          call_id: functionCallId,
          output: JSON.stringify(result),
        },
      }));
      
      // Trigger AI to respond with the result and say goodbye
      ws.send(JSON.stringify({
        type: 'response.create',
      }));
      
      onBookingComplete();
      
    } else if (name === 'end_call') {
      console.log('📴 End call requested:', args);
      
      // Send function result
      ws.send(JSON.stringify({
        type: 'conversation.item.create',
        item: {
          type: 'function_call_output',
          call_id: functionCallId,
          output: JSON.stringify({ success: true }),
        },
      }));
      
      // Wait a moment for any final audio to be sent, then hangup
      setTimeout(async () => {
        try {
          // Send WhatsApp notification
          await sendWhatsAppNotification(callerPhone);
          
          // Hangup the call via OpenAI API
          console.log(`📞 Hanging up call: ${callId}`);
          const hangupResponse = await fetch(`${OPENAI_API_BASE}/${callId}/hangup`, {
            method: 'POST',
            headers: {
              'Authorization': `Bearer ${apiKey}`,
              'Content-Type': 'application/json',
            },
          });
          
          if (hangupResponse.ok) {
            console.log(`✅ Call ${callId} hung up successfully`);
          } else {
            const errorText = await hangupResponse.text();
            console.error(`❌ Hangup failed: ${hangupResponse.status}`, errorText);
          }
          
          ws.close();
        } catch (error) {
          console.error(`❌ Hangup error:`, error);
        }
      }, 2000); // Wait 2 seconds for goodbye audio to complete
    }
  } catch (e) {
    console.error('Failed to handle function call:', e);
  }
}
