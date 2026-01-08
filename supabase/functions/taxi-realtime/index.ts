import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_INSTRUCTIONS = `You are Ada, a friendly and professional Taxi Dispatcher for "247 Radio Carz" taking phone calls.

**MULTILINGUAL SUPPORT - CRITICAL:**
- ALWAYS respond in the SAME LANGUAGE the customer speaks
- If customer speaks Polish, respond in Polish
- If customer speaks Urdu, respond in Urdu
- If customer speaks Punjabi, respond in Punjabi
- If customer speaks any other language, respond in that language
- Detect the language from their FIRST message and use it throughout
- Keep your warm, friendly personality in ALL languages
- Translate your standard phrases appropriately (e.g., "Brilliant!" → "Świetnie!" in Polish)

YOUR INTRODUCTION - GREETING FLOW:
- For RETURNING customers (when you're told their name): "Hello [NAME]! Lovely to hear from you again. How can I help with your travels today?"
- For NEW customers: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"
- After they give their name, say: "Lovely to meet you [NAME]! How can I help with your travels today?"
- ALWAYS use their name when addressing them throughout the call (e.g., "Right then [NAME], where would you like to be picked up from?")
- Adapt greetings to the customer's language while keeping the same warm tone

PERSONALITY:
- Warm, welcoming personality (British in English, culturally appropriate in other languages)
- Use casual friendly phrases appropriate to the language
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable
- ALWAYS address the customer by name once you know it

BOOKING FLOW - FOLLOW THIS EXACTLY:
1. Greet the customer (get their name if new)
2. Ask: "Where would you like to be picked up from, [NAME]?"
3. When they give pickup, SPELL IT BACK: "Was that 5-2-A David Road? D-A-V-I-D?"
4. When confirmed, ask: "And where are you heading to, [NAME]?"
5. When they give destination, confirm it the same way
6. Ask: "How many passengers will there be?"
7. Once you have ALL 3 details, you MUST do a FULL CONFIRMATION before booking

**INTELLIGENT QUESTION HANDLING - CRITICAL:**
When you ask a question, you MUST wait for a VALID answer before proceeding:

1. NAME QUESTION: "What's your name please?"
   - Valid answers: Any name (first name is enough)
   - If unclear: "Sorry, I didn't catch that. What name should I call you?"

2. PICKUP QUESTION: "Where would you like to be picked up from?"
   - Valid answers: Any address, location, landmark, postcode
   - Invalid: "yes", "no", "okay", random words, or answering a different question
   - If invalid: "Sorry, I didn't catch the pickup address. Where would you like to be picked up from?"

3. DESTINATION QUESTION: "And where are you heading to?"
   - Valid answers: Any address, location, landmark, postcode, "as directed"
   - Invalid: "yes", "no", silence, or repeating the pickup
   - If invalid: "I missed that - where would you like to go to?"

4. PASSENGERS QUESTION: "How many passengers will there be?"
   - Valid answers: A number (1-8), or words like "just me", "two of us", "three people"
   - Invalid: "yes", addresses, destinations, or non-numeric responses
   - If invalid: "Sorry, I need to know the number of passengers. How many will be travelling?"

**REPEAT QUESTIONS WHEN NEEDED:**
- If the customer's response does NOT answer your question, politely repeat it
- Don't assume or guess - always get explicit answers
- Use different phrasing when repeating: "Let me ask again..." or "Sorry, I need to know..."
- Maximum 2 repeats, then say: "I'm having trouble understanding. Let me connect you to our team."

**MANDATORY CONFIRMATION STEP - CRITICAL:**
Before calling book_taxi, you MUST:
1. Summarize ALL details back to the customer ONCE: "Just to confirm [NAME] - pickup from [ADDRESS], going to [DESTINATION], for [X] passengers. Is that all correct?"
2. WAIT for the customer to explicitly confirm with "yes", "correct", "that's right", "yeah", etc.
3. If they confirm YES: IMMEDIATELY call book_taxi (do NOT speak any extra words first)
4. If they say "no" or correct something, update the detail and do a NEW full confirmation

DO NOT call book_taxi until the customer says YES to your confirmation summary!
DO NOT repeat the confirmation question twice. If you heard a clear YES, proceed straight to booking.

INFORMATION EXTRACTION - CRITICAL:
- Listen carefully for: customer name, pickup location, destination, number of passengers
- Extract numbers spoken as words (e.g., "two passengers" = 2, "just me" = 1, "couple of us" = 2)
- If customer gives all info at once, still do the FULL CONFIRMATION before booking
- If any info is unclear, ask for clarification before proceeding

PRICING (calculate based on destination):
- City trips: £15-25 (random within range)
- Airport destinations: £45
- If 5+ passengers: add £5 for 6-seater van
- ETA: Always 5-8 minutes

CRITICAL ADDRESS ACCURACY RULES:
- Street names that sound similar are OFTEN MISHEARD. Common confusions:
  * David / Davy / Davey / Dewsbury / Derby
  * Main / Mane / Maine
  * Park / Bark / Mark
- When the customer gives an address, ALWAYS spell back the street name letter by letter
- Example: "Was that D-A-V-I-D Road, David?"
- If the customer corrects you, apologize and repeat the correction back
- House numbers with letters (52A, 18B) - say each character: "five two A"
- NEVER assume you heard correctly - always verify by spelling

WHEN THE book_taxi FUNCTION RETURNS:
- The function output contains the VERIFIED pickup and destination
- Use the function output to respond.
- IMPORTANT: Do NOT ask "is that correct" again after booking. This is not a second confirmation.
- Keep it short: confirm it's booked, give ETA and fare, and ask if they need anything else.

GENERAL RULES:
- Never ask for information you already have
- If customer mentions extra requirements (wheelchair, child seat), acknowledge but proceed with booking
- Stay focused on completing the booking efficiently
- PERSONALIZE every response by using the customer's name`;

serve(async (req) => {
  // Handle regular HTTP requests (health check)
  if (req.headers.get("upgrade") !== "websocket") {
    if (req.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }
    return new Response(JSON.stringify({ 
      status: "ready",
      endpoint: "taxi-realtime",
      protocol: "websocket"
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }

  // Upgrade to WebSocket
  const { socket, response } = Deno.upgradeWebSocket(req);
  
  const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY");
  const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
  const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  
  let openaiWs: WebSocket | null = null;
  let callId = `call-${Date.now()}`;
  let callStartAt = new Date().toISOString();
  let bookingData: any = {};
  let sessionReady = false;
  let pendingMessages: any[] = [];
  let callSource = "web"; // 'web' or 'asterisk'
  let userPhone = ""; // Phone number from Asterisk
  let callerName = ""; // Known caller's name from database
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let currentAssistantText = ""; // Buffer for assistant transcript
  let geocodingEnabled = true; // Enable address verification by default

  type KnownBooking = {
    pickup?: string;
    destination?: string;
    passengers?: number;
    pickupVerified?: boolean;
    destinationVerified?: boolean;
  };

  // We keep our own "known booking" extracted from the user's *exact* transcript/text,
  // then prefer it over the model's paraphrasing when the booking is confirmed.
  let knownBooking: KnownBooking = {};

  const normalize = (s: string) => s.trim().replace(/\s+/g, " ").replace(/[\s,.;:!?]+$/g, "");

  // Geocode an address using OSM Nominatim
  const geocodeAddress = async (address: string): Promise<{ found: boolean; display_name?: string; error?: string }> => {
    try {
      console.log(`[${callId}] 🌍 Geocoding address: "${address}"`);
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({ address, country: "UK" }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Geocode API error: ${response.status}`);
        return { found: false, error: "Geocoding service unavailable" };
      }

      const result = await response.json();
      console.log(`[${callId}] 🌍 Geocode result for "${address}":`, result.found ? "FOUND" : "NOT FOUND");
      return result;
    } catch (e) {
      console.error(`[${callId}] Geocode exception:`, e);
      return { found: false, error: "Geocoding failed" };
    }
  };

  // Notify Ada about geocoding result and ask for correction if needed
  const notifyGeocodeResult = (addressType: "pickup" | "destination", address: string, found: boolean) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;

    if (found) {
      console.log(`[${callId}] ✅ ${addressType} address verified: "${address}"`);
      // No need to say anything - address is valid
    } else {
      console.log(`[${callId}] ❌ ${addressType} address NOT FOUND: "${address}" - asking for correction`);
      
      // Inject a message to Ada to ask for address correction
      const message = addressType === "pickup"
        ? `[SYSTEM: The pickup address "${address}" could not be verified. Politely ask the customer to confirm or provide the correct address. Say something like "I'm having a little trouble finding that address. Could you give me the full street name and number please?"]`
        : `[SYSTEM: The destination address "${address}" could not be verified. Politely ask the customer to confirm or provide the correct address. Say something like "I'm not quite finding that destination. Could you spell out the street name for me please?"]`;
      
      openaiWs.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "message",
          role: "user",
          content: [{ type: "input_text", text: message }]
        }
      }));
      
      // Trigger Ada to respond
      openaiWs.send(JSON.stringify({
        type: "response.create",
        response: { modalities: ["audio", "text"] }
      }));
    }
  };

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

  // Look up caller by phone number
  const lookupCaller = async (phone: string): Promise<void> => {
    if (!phone) return;
    try {
      const { data, error } = await supabase
        .from("callers")
        .select("name, last_pickup, last_destination, total_bookings")
        .eq("phone_number", phone)
        .maybeSingle();
      
      if (error) {
        console.error(`[${callId}] Caller lookup error:`, error);
        return;
      }
      
      if (data?.name) {
        callerName = data.name;
        console.log(`[${callId}] 👤 Known caller: ${callerName} (${data.total_bookings} previous bookings)`);
        if (data.last_pickup) {
          console.log(`[${callId}] 📍 Last trip: ${data.last_pickup} → ${data.last_destination}`);
        }
      } else {
        console.log(`[${callId}] 👤 New caller: ${phone}`);
      }
    } catch (e) {
      console.error(`[${callId}] Caller lookup exception:`, e);
    }
  };

  // Save or update caller info after booking
  const saveCallerInfo = async (booking: { pickup: string; destination: string; passengers: number }): Promise<void> => {
    if (!userPhone) return;
    try {
      // Check if caller exists
      const { data: existing } = await supabase
        .from("callers")
        .select("id, total_bookings")
        .eq("phone_number", userPhone)
        .maybeSingle();
      
      if (existing) {
        // Update existing caller
        const { error } = await supabase.from("callers").update({
          last_pickup: booking.pickup,
          last_destination: booking.destination,
          total_bookings: (existing.total_bookings || 0) + 1,
          updated_at: new Date().toISOString()
        }).eq("phone_number", userPhone);
        
        if (error) console.error(`[${callId}] Update caller error:`, error);
        else console.log(`[${callId}] 💾 Updated caller ${userPhone} (${existing.total_bookings + 1} bookings)`);
      } else {
        // Insert new caller
        const { error } = await supabase.from("callers").insert({
          phone_number: userPhone,
          last_pickup: booking.pickup,
          last_destination: booking.destination,
          total_bookings: 1
        });
        
        if (error) console.error(`[${callId}] Insert caller error:`, error);
        else console.log(`[${callId}] 💾 New caller saved: ${userPhone}`);
      }
    } catch (e) {
      console.error(`[${callId}] Save caller exception:`, e);
    }
  };

  // Broadcast live call updates to the database for monitoring
  const broadcastLiveCall = async (updates: Record<string, any>) => {
    try {
      const { error } = await supabase.from("live_calls").upsert({
        call_id: callId,
        source: callSource,
        started_at: callStartAt,
        ...updates,
        transcripts: transcriptHistory,
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
      if (error) console.error(`[${callId}] Live call broadcast error:`, error);
    } catch (e) {
      console.error(`[${callId}] Live call broadcast exception:`, e);
    }
  };

  // Extract customer name from transcript
  const extractNameFromTranscript = (transcript: string): string | null => {
    const t = transcript.trim();
    
    // Common patterns for name responses
    // "My name is John" / "I'm John" / "It's John" / "John" / "Call me John"
    const patterns = [
      /my name(?:'s| is)\s+(\w+)/i,
      /i(?:'m| am)\s+(\w+)/i,
      /it(?:'s| is)\s+(\w+)/i,
      /call me\s+(\w+)/i,
      /^(\w+)$/i, // Just a single word (likely a name)
      /^(?:hi|hello|hey)?\s*(?:it's|i'm|this is)?\s*(\w+)/i
    ];
    
    for (const pattern of patterns) {
      const match = t.match(pattern);
      if (match?.[1]) {
        // Capitalize first letter
        const name = match[1].charAt(0).toUpperCase() + match[1].slice(1).toLowerCase();
        // Filter out common non-name words
        const nonNames = ['yes', 'no', 'yeah', 'okay', 'ok', 'sure', 'please', 'thanks', 'hello', 'hi', 'hey', 'the', 'from', 'to'];
        if (!nonNames.includes(name.toLowerCase())) {
          return name;
        }
      }
    }
    return null;
  };

  // Call the AI extraction function to get structured booking data from transcript
  const extractBookingFromTranscript = async (transcript: string): Promise<void> => {
    try {
      console.log(`[${callId}] 🔍 Extracting booking info from: "${transcript}"`);
      
      // Also try to extract name if we don't have one yet
      if (!callerName) {
        const extractedName = extractNameFromTranscript(transcript);
        if (extractedName) {
          callerName = extractedName;
          console.log(`[${callId}] 👤 Extracted customer name: ${callerName}`);
          
          // Update caller record with name if we have their phone
          if (userPhone) {
            try {
              await supabase.from("callers").upsert({
                phone_number: userPhone,
                name: callerName,
                updated_at: new Date().toISOString()
              }, { onConflict: "phone_number" });
              console.log(`[${callId}] 💾 Saved name ${callerName} for ${userPhone}`);
            } catch (e) {
              console.error(`[${callId}] Failed to save name:`, e);
            }
          }
          
          // Inject name into Ada's context
          if (openaiWs?.readyState === WebSocket.OPEN) {
            console.log(`[${callId}] 📢 Injecting customer name into Ada's context: ${callerName}`);
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "assistant",
                content: [{ type: "text", text: `[Internal note: Customer's name is ${callerName}. Address them by name from now on.]` }]
              }
            }));
          }
        }
      }
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({
          transcript,
          mode: knownBooking.pickup || knownBooking.destination ? "update" : "new",
          existing_booking: knownBooking,
        }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Extraction API error: ${response.status}`);
        return;
      }

      const extracted = await response.json();
      console.log(`[${callId}] 📦 AI Extracted:`, extracted);

      // Only update fields that were extracted (non-null)
      const before = { ...knownBooking };
      
      if (extracted.pickup_location) {
        knownBooking.pickup = extracted.pickup_location;
      }
      if (extracted.dropoff_location) {
        knownBooking.destination = extracted.dropoff_location;
      }
      if (extracted.number_of_passengers) {
        knownBooking.passengers = extracted.number_of_passengers;
      }

      // Check if anything changed
      const pickupChanged = before.pickup !== knownBooking.pickup && knownBooking.pickup;
      const destinationChanged = before.destination !== knownBooking.destination && knownBooking.destination;
      const passengersChanged = before.passengers !== knownBooking.passengers && knownBooking.passengers;

      if (pickupChanged || destinationChanged || passengersChanged) {
        console.log(`[${callId}] ✅ Known booking updated via AI extraction:`, knownBooking);
        broadcastLiveCall({
          pickup: knownBooking.pickup,
          destination: knownBooking.destination,
          passengers: knownBooking.passengers
        });

        // GEOCODE NEW ADDRESSES (if geocoding is enabled)
        if (geocodingEnabled) {
          // Geocode pickup if it changed and hasn't been verified yet
          if (pickupChanged && !knownBooking.pickupVerified) {
            const pickupResult = await geocodeAddress(knownBooking.pickup!);
            if (pickupResult.found) {
              knownBooking.pickupVerified = true;
              console.log(`[${callId}] ✅ Pickup verified: ${pickupResult.display_name}`);
            } else {
              // Address not found - ask Ada to request correction
              notifyGeocodeResult("pickup", knownBooking.pickup!, false);
              return; // Don't continue - wait for corrected address
            }
          }
          
          // Geocode destination if it changed and hasn't been verified yet
          if (destinationChanged && !knownBooking.destinationVerified) {
            const destResult = await geocodeAddress(knownBooking.destination!);
            if (destResult.found) {
              knownBooking.destinationVerified = true;
              console.log(`[${callId}] ✅ Destination verified: ${destResult.display_name}`);
            } else {
              // Address not found - ask Ada to request correction
              notifyGeocodeResult("destination", knownBooking.destination!, false);
              return; // Don't continue - wait for corrected address
            }
          }
        }

        // INJECT CORRECT DATA INTO ADA'S CONTEXT (silently - no response triggered)
        // This ensures Ada uses the EXACT extracted addresses, not her hallucinated versions
        if (openaiWs?.readyState === WebSocket.OPEN) {
          let contextUpdate = "INTERNAL MEMORY UPDATE (DO NOT RESPOND TO THIS MESSAGE - continue with your normal flow):\n";
          
          if (knownBooking.pickup) {
            contextUpdate += `• Confirmed pickup: "${knownBooking.pickup}"${knownBooking.pickupVerified ? " ✓ VERIFIED" : ""}\n`;
          }
          if (knownBooking.destination) {
            contextUpdate += `• Confirmed destination: "${knownBooking.destination}"${knownBooking.destinationVerified ? " ✓ VERIFIED" : ""}\n`;
          }
          if (knownBooking.passengers) {
            contextUpdate += `• Confirmed passengers: ${knownBooking.passengers}\n`;
          }
          contextUpdate += "Use these EXACT values when speaking. DO NOT acknowledge this message.";
          
          console.log(`[${callId}] 📢 Injecting correct data into Ada's context (silent)`);
          
          // Add as a system-style context update that won't trigger a response
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "assistant",  // Using assistant role so it doesn't trigger a new response
              content: [{ type: "text", text: `[Internal note: ${contextUpdate}]` }]
            }
          }));
        }
      }
    } catch (e) {
      console.error(`[${callId}] Extraction error:`, e);
      // Fallback to regex extraction if AI extraction fails
      fallbackExtractFromText(transcript);
    }
  };

  // Fallback regex extraction (used if AI extraction fails)
  const fallbackExtractFromText = (text: string): void => {
    const t = text || "";
    const before = { ...knownBooking };

    // Pickup: "from X" (stop before "to/going to/heading to" if present)
    const fromMatch = t.match(/\bfrom\s+(.+?)(?:\s+(?:to|going\s+to|heading\s+to)\b|$)/i);
    if (fromMatch?.[1]) knownBooking.pickup = normalize(fromMatch[1]);

    // Destination: "to Y" / "going to Y" / "heading to Y"
    const toMatch = t.match(/\b(?:to|going\s+to|heading\s+to)\s+(.+)$/i);
    if (toMatch?.[1]) knownBooking.destination = normalize(toMatch[1]);

    // Passengers: "3 passengers" / "three passengers"
    const wordToNum: Record<string, number> = {
      one: 1, two: 2, three: 3, four: 4, five: 5,
      six: 6, seven: 7, eight: 8, nine: 9, ten: 10,
    };

    const passengersDigit = t.match(/\b(\d+)\s*(?:passengers?|people|persons?)\b/i);
    if (passengersDigit?.[1]) knownBooking.passengers = Number(passengersDigit[1]);

    const passengersWord = t.match(/\b(one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:passengers?|people|persons?)\b/i);
    if (passengersWord?.[1]) knownBooking.passengers = wordToNum[passengersWord[1].toLowerCase()];

    // "just me" / "just one" / "myself"
    if (/\b(just\s+me|myself|just\s+one)\b/i.test(t)) {
      knownBooking.passengers = 1;
    }

    if (
      before.pickup !== knownBooking.pickup ||
      before.destination !== knownBooking.destination ||
      before.passengers !== knownBooking.passengers
    ) {
      console.log(`[${callId}] Known booking updated (fallback regex):`, knownBooking);
      broadcastLiveCall({
        pickup: knownBooking.pickup,
        destination: knownBooking.destination,
        passengers: knownBooking.passengers
      });
    }
  };

  // When audio is committed (server VAD or manual commit), we expect a response.
  // Some clients still send manual commit; we defensively trigger response.create after STT completes.
  let awaitingResponseAfterCommit = false;
  let responseCreatedSinceCommit = false;

  // Connect to OpenAI Realtime API
  const connectToOpenAI = () => {
    console.log(`[${callId}] Connecting to OpenAI Realtime API...`);
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      console.log(`[${callId}] Connected to OpenAI`);
    };

    openaiWs.onmessage = async (event) => {
      const data = JSON.parse(event.data);
      
      // Session created - send configuration
      if (data.type === "session.created") {
        console.log(`[${callId}] Session created. Server defaults:`, data.session);
        console.log(`[${callId}] Sending session.update...`);
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            voice: "shimmer", // British female voice
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { 
              model: "whisper-1"
              // No language specified = auto-detect any language
              // Whisper supports 99+ languages including Polish, Urdu, Punjabi, Hindi, Arabic, etc.
            },
            // Server VAD - tuned to reduce audio artifacts (hiss at word endings)
            // Higher prefix captures more lead-in, longer silence prevents premature cutoff
            turn_detection: {
              type: "server_vad",
              threshold: 0.5,           // Slightly lower = more sensitive to quiet speech
              prefix_padding_ms: 300,   // Capture 300ms before speech starts (smoother onset)
              silence_duration_ms: 800, // 800ms silence before triggering response
              create_response: true     // Auto-create response when speech ends
            },
            tools: [
              {
                type: "function",
                name: "book_taxi",
                description: "Book a taxi when pickup, destination and number of passengers are all confirmed by the customer",
                parameters: {
                  type: "object",
                  properties: {
                    pickup: { type: "string", description: "Pickup location exactly as customer stated" },
                    destination: { type: "string", description: "Drop-off location exactly as customer stated" },
                    passengers: { type: "integer", description: "Number of passengers" }
                  },
                  required: ["pickup", "destination", "passengers"]
                }
              }
            ],
            tool_choice: "auto",
            instructions: SYSTEM_INSTRUCTIONS
          }
        }));
      }

      // Session updated - now ready
      if (data.type === "session.updated") {
        console.log(`[${callId}] Session ready! Effective config:`, data.session);
        sessionReady = true;
        socket.send(JSON.stringify({ type: "session_ready" }));

        // Broadcast call started
        await broadcastLiveCall({ status: "active" });

        // Trigger initial greeting immediately - inject as system turn for faster response
        // Personalize greeting if we know the caller's name (returning customer)
        // For new customers, ask for their name first
        const greetingPrompt = callerName 
          ? `[Call connected - greet the RETURNING customer by name. Their name is ${callerName}. Say: "Hello ${callerName}! Lovely to hear from you again. How can I help with your travels today?"]`
          : `[Call connected - greet the NEW customer and ask for their name. Say: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"]`;
        
        console.log(`[${callId}] Triggering initial greeting... (caller: ${callerName || 'new customer'})`);
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "message",
            role: "user",
            content: [{ type: "input_text", text: greetingPrompt }]
          }
        }));
        openaiWs?.send(JSON.stringify({
          type: "response.create",
          response: { modalities: ["audio", "text"] }
        }));

        // Process any pending messages
        while (pendingMessages.length > 0) {
          const msg = pendingMessages.shift();
          console.log(`[${callId}] Processing pending message:`, msg.type);
          if (msg.type === "text") {
            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{ type: "input_text", text: msg.text }],
                },
              }),
            );
            openaiWs?.send(
              JSON.stringify({
                type: "response.create",
                response: { modalities: ["audio", "text"] },
              }),
            );
          }
        }
      }

      // AI response started
      if (data.type === "response.created") {
        console.log(`[${callId}] >>> response.created - AI starting to generate`);
        currentAssistantText = ""; // Reset buffer

        if (awaitingResponseAfterCommit) {
          responseCreatedSinceCommit = true;
          console.log(`[${callId}] >>> response.created observed for committed audio turn`);
        }

        socket.send(JSON.stringify({ type: "ai_speaking", speaking: true }));
      }

      // Log response output item added (tells us what modalities are being generated)
      if (data.type === "response.output_item.added") {
        console.log(`[${callId}] >>> response.output_item.added:`, JSON.stringify(data.item));
      }

      // Log content part added
      if (data.type === "response.content_part.added") {
        console.log(`[${callId}] >>> response.content_part.added:`, JSON.stringify(data.part));
      }

      // If the model responds in TEXT modality, forward it as assistant transcript
      if (data.type === "response.text.delta" || data.type === "response.output_text.delta") {
        const delta = data.delta || "";
        console.log(`[${callId}] >>> text delta: "${delta}"`);
        if (delta) {
          socket.send(
            JSON.stringify({
              type: "transcript",
              text: delta,
              role: "assistant",
            }),
          );
        }
      }

      // Forward audio to client AND broadcast to monitoring channel
      if (data.type === "response.audio.delta") {
        console.log(`[${callId}] >>> AUDIO DELTA received, length: ${data.delta?.length || 0}`);
        socket.send(
          JSON.stringify({
            type: "audio",
            audio: data.delta,
          }),
        );
        
        // Broadcast audio to monitoring channel via database insert
        // Monitors will subscribe to this for live audio playback
        try {
          await supabase.from("live_call_audio").insert({
            call_id: callId,
            audio_chunk: data.delta,
            created_at: new Date().toISOString()
          });
        } catch (e) {
          // Ignore audio broadcast errors - monitoring is optional
          console.log(`[${callId}] Audio broadcast skipped`);
        }
      }

      // Log audio done
      if (data.type === "response.audio.done") {
        console.log(`[${callId}] >>> response.audio.done - audio generation complete`);
      }

      // Log audio transcript done - save complete assistant message
      if (data.type === "response.audio_transcript.done") {
        console.log(`[${callId}] >>> response.audio_transcript.done: "${data.transcript}"`);
        if (data.transcript) {
          transcriptHistory.push({
            role: "assistant",
            text: data.transcript,
            timestamp: new Date().toISOString()
          });
          // Broadcast transcript update
          broadcastLiveCall({});
        }
      }

      // Forward transcript for logging
      if (data.type === "response.audio_transcript.delta") {
        socket.send(JSON.stringify({
          type: "transcript",
          text: data.delta,
          role: "assistant"
        }));
      }

      // User transcript - extract booking info using AI
      if (data.type === "conversation.item.input_audio_transcription.completed") {
        console.log(`[${callId}] User said: ${data.transcript}`);
        
        // Use AI extraction for accurate booking data
        extractBookingFromTranscript(data.transcript);
        
        // Save user message to history
        if (data.transcript) {
          transcriptHistory.push({
            role: "user",
            text: data.transcript,
            timestamp: new Date().toISOString()
          });
          // Broadcast transcript update
          broadcastLiveCall({});
        }
        
        socket.send(
          JSON.stringify({
            type: "transcript",
            text: data.transcript,
            role: "user",
          }),
        );

        // IMPORTANT: With some clients and/or manual commit, OpenAI may not auto-create a response
        // even when server_vad is enabled. If we have a committed audio turn and no response yet,
        // explicitly request one now.
        if (awaitingResponseAfterCommit && !responseCreatedSinceCommit && openaiWs?.readyState === WebSocket.OPEN) {
          console.log(`[${callId}] No response.created after commit; sending response.create as fallback`);
          openaiWs.send(
            JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] },
            }),
          );
        }

        awaitingResponseAfterCommit = false;
        responseCreatedSinceCommit = false;
      }

      // Speech started (barge-in detection)
      if (data.type === "input_audio_buffer.speech_started") {
        console.log(`[${callId}] >>> User started speaking (barge-in)`);
        socket.send(JSON.stringify({ type: "user_speaking", speaking: true }));
      }

      // Speech stopped
      if (data.type === "input_audio_buffer.speech_stopped") {
        console.log(`[${callId}] >>> User stopped speaking`);
        socket.send(JSON.stringify({ type: "user_speaking", speaking: false }));
      }

      // Audio buffer committed (server VAD auto-commits)
      if (data.type === "input_audio_buffer.committed") {
        console.log(`[${callId}] >>> Audio buffer committed, item_id: ${data.item_id}`);
        awaitingResponseAfterCommit = true;
        responseCreatedSinceCommit = false;
      }

      // DEBUG: log response lifecycle events (helps diagnose missing audio)
      if (data.type === "response.done") {
        const status = data.response?.status || "unknown";
        const outputCount = data.response?.output?.length || 0;
        console.log(`[${callId}] >>> response.done - status: ${status}, outputs: ${outputCount}`);
        if (data.response?.status_details) {
          console.log(`[${callId}] >>> status_details:`, JSON.stringify(data.response.status_details));
        }
      }

      // Handle function calls
      if (data.type === "response.function_call_arguments.done") {
        console.log(`[${callId}] Function call: ${data.name}`, data.arguments);
        
        if (data.name === "book_taxi") {
          const args = JSON.parse(data.arguments);

          // Prefer exact values we extracted from the user's transcript/text (prevents 52A -> 50A style drift)
          const finalBooking = {
            pickup: knownBooking.pickup ?? args.pickup,
            destination: knownBooking.destination ?? args.destination,
            passengers: knownBooking.passengers ?? args.passengers,
          };

          bookingData = finalBooking;
          console.log(`[${callId}] Booking (final):`, finalBooking);
          
          // Calculate fare
          const isAirport = String(finalBooking.destination || "").toLowerCase().includes("airport");
          const is6Seater = Number(finalBooking.passengers || 0) > 4;
          let fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
          if (is6Seater) fare += 5;
          const eta = `${Math.floor(Math.random() * 4) + 5} minutes`;
          
          // Log to database with phone number
          await supabase.from("call_logs").insert({
            call_id: callId,
            pickup: finalBooking.pickup,
            destination: finalBooking.destination,
            passengers: finalBooking.passengers,
            estimated_fare: `£${fare}`,
            booking_status: "confirmed",
            call_start_at: callStartAt,
            user_phone: userPhone || null
          });

          // Save/update caller info for future calls
          await saveCallerInfo(finalBooking);

          // Broadcast booking confirmed
          await broadcastLiveCall({
            pickup: finalBooking.pickup,
            destination: finalBooking.destination,
            passengers: finalBooking.passengers,
            booking_confirmed: true,
            fare: `£${fare}`,
            eta: eta
          });
          
          // Send function result back to OpenAI with EXACT addresses to use in response
          // The AI MUST use these exact addresses in its confirmation message
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: data.call_id,
              output: JSON.stringify({
                success: true,
                booking_id: callId,
                pickup_address: finalBooking.pickup,
                destination_address: finalBooking.destination,
                passenger_count: finalBooking.passengers,
                fare: `£${fare}`,
                eta: eta,
                confirmation_script: `Brilliant${callerName ? `, ${callerName}` : ""}! That's all booked. The fare is £${fare} and your driver will be with you in ${eta}. Do you need anything else today?`
              })
            }
          }));
          
          // Trigger response
          openaiWs?.send(
            JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] },
            }),
          );
          
          // Notify client
          socket.send(JSON.stringify({
            type: "booking_confirmed",
            booking: { ...finalBooking, fare: `£${fare}`, eta }
          }));
        }
      }

      // Response completed
      if (data.type === "response.done") {
        socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
        socket.send(JSON.stringify({ type: "response_done" }));
      }

      // Error handling
      if (data.type === "error") {
        console.error(`[${callId}] OpenAI error:`, data.error);
        socket.send(JSON.stringify({ type: "error", error: data.error }));
      }
    };

    openaiWs.onerror = (error) => {
      console.error(`[${callId}] OpenAI WebSocket error:`, error);
    };

    openaiWs.onclose = () => {
      console.log(`[${callId}] OpenAI connection closed`);
    };
  };

  socket.onopen = () => {
    console.log(`[${callId}] Client connected`);
    connectToOpenAI();
  };

  socket.onmessage = async (event) => {
    try {
      const message = JSON.parse(event.data);
      
      // Set call ID from client
      if (message.type === "init") {
        callId = message.call_id || callId;
        callStartAt = new Date().toISOString();
        
        // Extract phone number from Asterisk
        if (message.user_phone) {
          userPhone = message.user_phone;
          console.log(`[${callId}] User phone: ${userPhone}`);
        }
        
        // Enable/disable geocoding from client (default: true)
        if (message.geocoding !== undefined) {
          geocodingEnabled = message.geocoding;
          console.log(`[${callId}] 🌍 Geocoding: ${geocodingEnabled ? "ENABLED" : "DISABLED"}`);
        }
        
        // If name provided directly from Asterisk, use it
        if (message.user_name) {
          callerName = message.user_name;
          console.log(`[${callId}] 👤 Caller name from Asterisk: ${callerName}`);
          
          // Save/update caller with provided name
          if (userPhone && userPhone !== "Unknown") {
            await supabase.from("callers").upsert({
              phone_number: userPhone,
              name: callerName,
              updated_at: new Date().toISOString()
            }, { onConflict: "phone_number" });
          }
        } else if (userPhone && userPhone !== "Unknown") {
          // Look up caller in database if no name provided
          await lookupCaller(userPhone);
        }
        
        // Detect Asterisk calls by call_id prefix
        if (callId.startsWith("ast-") || callId.startsWith("asterisk-") || callId.startsWith("call_")) {
          callSource = "asterisk";
        }
        console.log(`[${callId}] Call initialized (source: ${callSource}, phone: ${userPhone}, caller: ${callerName || 'unknown'}, geocoding: ${geocodingEnabled})`);
        return;
      }
      
      // Forward audio to OpenAI
      if (message.type === "audio" && openaiWs?.readyState === WebSocket.OPEN) {
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: message.audio
        }));
      }

      // TEXT MODE: Send text message directly (for testing without audio)
      if (message.type === "text") {
        // Use AI extraction for accurate booking data
        extractBookingFromTranscript(message.text);
        
        // Save user text to history
        transcriptHistory.push({
          role: "user",
          text: message.text,
          timestamp: new Date().toISOString()
        });
        broadcastLiveCall({});
        
        console.log(`[${callId}] Text mode input: ${message.text}`);
        if (sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: message.text }]
            }
          }));
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: { modalities: ["audio", "text"] }
          }));
        } else {
          console.log(`[${callId}] Session not ready, queuing message`);
          pendingMessages.push(message);
        }
      }

      // Manual commit (for push-to-talk clients)
      // Mark that we should get a response for this turn.
      if (message.type === "commit" && openaiWs?.readyState === WebSocket.OPEN) {
        console.log(`[${callId}] Manual commit received`);
        awaitingResponseAfterCommit = true;
        responseCreatedSinceCommit = false;
        openaiWs.send(JSON.stringify({ type: "input_audio_buffer.commit" }));
      }

      // Hangup from Asterisk
      if (message.type === "hangup") {
        console.log(`[${callId}] Hangup received`);
        socket.close();
      }

    } catch (error) {
      console.error(`[${callId}] Message parse error:`, error);
    }
  };

  socket.onclose = async () => {
    console.log(`[${callId}] Client disconnected`);
    
    // Update call end time
    await supabase.from("call_logs")
      .update({ call_end_at: new Date().toISOString() })
      .eq("call_id", callId);

    // Update live call status
    await broadcastLiveCall({
      status: "completed",
      ended_at: new Date().toISOString()
    });
    
    openaiWs?.close();
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] WebSocket error:`, error);
  };

  return response;
});
