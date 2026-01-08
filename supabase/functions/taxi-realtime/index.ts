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
- For RETURNING customers WITH a usual destination: "Hello [NAME]! Lovely to hear from you again. Shall I book you a taxi to [LAST_DESTINATION], or are you heading somewhere different today?"
- For RETURNING customers WITHOUT a usual destination: "Hello [NAME]! Lovely to hear from you again. How can I help with your travels today?"
- For NEW customers: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"
- After they give their name, say: "Lovely to meet you [NAME]! How can I help with your travels today?"
- ALWAYS use their name when addressing them throughout the call (e.g., "Right then [NAME], where would you like to be picked up from?")
- Adapt greetings to the customer's language while keeping the same warm tone
- If returning customer accepts quick rebooking ("yes", "yeah", "please"), skip asking for destination and confirm: "Brilliant! And same pickup from [LAST_PICKUP]?" or ask "Where shall I pick you up from?"

PERSONALITY:
- Warm, welcoming personality (British in English, culturally appropriate in other languages)
- Use casual friendly phrases appropriate to the language
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable
- ALWAYS address the customer by name once you know it

BOOKING FLOW - FOLLOW THIS EXACTLY:
1. Greet the customer (get their name if new)
2. Ask: "Where would you like to be picked up from, [NAME]?"
3. When they give pickup, acknowledge briefly and ask: "And where are you heading to?"
4. When they give destination, ask: "How many passengers?"
5. Once you have ALL 3 details (pickup, destination, passengers), THEN do ONE confirmation
6. DO NOT repeat back addresses one-by-one during collection - just acknowledge and move to the next question
7. Save ALL confirmations for the SINGLE final summary before booking

**CRITICAL - PICKUP VS DESTINATION:**
- PICKUP = where the taxi COLLECTS the customer (they get IN the taxi here)
- DESTINATION = where the taxi TAKES them (they get OUT of the taxi here)
- These are ALWAYS two DIFFERENT addresses - NEVER assume they are the same!
- If customer says "I'm going TO [address]" = that's the DESTINATION, not pickup
- If customer says "Pick me up FROM [address]" = that's the PICKUP
- If you only have ONE address, ASK for the other one specifically!

**CRITICAL - LISTEN TO CORRECTIONS:**
- If customer says "No" or corrects you, LISTEN CAREFULLY to what they say
- Use their EXACT words - if they say "52 David Road" don't add or remove letters
- If they say "52 David Road" (no A), do NOT say "52A David Road"
- Repeat corrections back to confirm you heard correctly

**INTELLIGENT QUESTION HANDLING - ABSOLUTELY CRITICAL:**
You are in a CONVERSATION. When you ask a question, you MUST:
1. WAIT for a response that DIRECTLY answers YOUR question
2. If the response does NOT answer your question, DO NOT proceed - ask again
3. NEVER assume, guess, or skip ahead without a valid answer

**STATE TRACKING - YOU MUST TRACK WHAT YOU LAST ASKED:**
- If you asked about PASSENGERS, the next valid response MUST be a number or equivalent
- If you asked about PICKUP, the next valid response MUST be an address
- If you asked about DESTINATION, the next valid response MUST be an address
- Any other response = INVALID - repeat your question!

**QUESTION VALIDATION RULES:**

1. NAME QUESTION: "What's your name please?"
   - Valid: Any name (first name is enough)
   - Invalid: Addresses, "yes", "no", random words
   - If invalid: "Sorry, I didn't catch your name. What should I call you?"

2. PICKUP QUESTION: "Where would you like to be picked up from?"
   - Valid: Any address, location, landmark, postcode
   - Invalid: "yes", "no", "okay", numbers without context, non-address responses
   - If invalid: "Sorry, I need the pickup address. Where shall I pick you up from?"

3. DESTINATION QUESTION: "And where are you heading to?"
   - Valid: Any address, location, landmark, postcode, "as directed"
   - Invalid: "yes", "no", numbers, repeating the pickup address
   - If invalid: "I missed the destination - where would you like to go?"

4. PASSENGERS QUESTION: "How many passengers will there be?"
   - Valid ONLY: Numbers 1-8, or words like "just me", "two of us", "three people", "myself"
   - Invalid: "yes", "no", addresses, destinations, "okay", confirmations
   - If invalid: "Sorry, I need the number of passengers. How many will be travelling?"
   - CRITICAL: If someone says "yes" or "okay" after you ask about passengers, that is NOT a valid answer! Ask again: "How many passengers will there be?"

**REPEAT UNTIL VALID:**
- NEVER proceed to the next step without a valid answer to your current question
- Politely repeat: "Let me ask again..." or "Sorry, I need to know..."
- Use slightly different phrasing each time
- Maximum 3 repeats, then: "I'm having trouble understanding. Let me connect you to our team."

**MANDATORY CONFIRMATION STEP - CRITICAL:**
Before calling book_taxi, you MUST:
1. ONLY after collecting ALL THREE details (pickup, destination, passengers), do ONE summary: "So that's [PICKUP] to [DESTINATION] for [X] passengers - shall I book that?"
2. WAIT for customer to confirm with "yes", "correct", etc.
3. If they confirm: IMMEDIATELY call book_taxi
4. If they correct something: update and confirm ONCE more

DO NOT call book_taxi until the customer says YES!
CRITICAL - STREAMLINED FLOW: During collection, just acknowledge briefly ("Got it", "Lovely") and ask the next question. Do NOT repeat addresses back one-by-one. Save ALL confirmation for the SINGLE final summary. NO partial confirmations during collection!

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

ADDRESS HANDLING:
- Repeat addresses back naturally to confirm (e.g., "That's 52A David Road, yes?")
- Do NOT spell addresses out letter-by-letter yourself
- If the CUSTOMER spells an address (e.g., "D-A-V-I-D Road" or "Delta Alpha Victor India Delta"), use their spelling for clarification
- When you hear spelled letters, confirm: "So that's David Road, D-A-V-I-D?"
- House numbers with letters (52A, 18B) - say naturally: "fifty-two A"
- If an address sounds unclear, ask: "Could you repeat that for me please?"

**CRITICAL - NEVER FAKE A BOOKING:**
- You can ONLY say "That's all booked" or confirm a booking AFTER you have called the book_taxi function AND received a successful response
- If you have NOT called book_taxi, you MUST NOT say the booking is complete
- The book_taxi function will return fare and ETA - use THOSE values in your response
- NEVER make up a fare or ETA without calling the function first

WHEN THE book_taxi FUNCTION RETURNS:
- The function output contains the VERIFIED pickup, destination, fare, and ETA
- Use the function output values in your response
- IMPORTANT: Do NOT ask "is that correct" again after booking. This is not a second confirmation.
- Keep it short: confirm it's booked, give ETA and fare, and ask if they need anything else.

ENDING THE CALL - CRITICAL:
- After booking is confirmed, ask: "Is there anything else I can help you with?"
- IMPORTANT: STOP speaking after you ask this question. Do NOT say goodbye in the same turn.
- Wait for the customer's NEXT response.
- ONLY if the customer clearly says "no", "that's all", "nothing else", "I'm good", etc:
  - Say a brief goodbye ("You're welcome! Have a great journey, goodbye!")
  - Then IMMEDIATELY call the end_call function

GENERAL RULES:
- Never ask for information you already have
- If customer mentions extra requirements (wheelchair, child seat), acknowledge but proceed with booking
- Stay focused on completing the booking efficiently
- PERSONALIZE every response by using the customer's name
- After the greeting, you MUST ask for pickup, destination, and passengers BEFORE any booking confirmation`;

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
  const LOVABLE_API_KEY = Deno.env.get("LOVABLE_API_KEY");
  
  let openaiWs: WebSocket | null = null;
  let callId = `call-${Date.now()}`;
  let callStartAt = new Date().toISOString();
  let bookingData: any = {};
  let sessionReady = false;
  let pendingMessages: any[] = [];
  let callSource = "web"; // 'web' or 'asterisk'
  let userPhone = ""; // Phone number from Asterisk
  let callerName = ""; // Known caller's name from database
  let callerTotalBookings = 0; // Number of previous bookings
  let callerLastPickup = ""; // Last pickup address
  let callerLastDestination = ""; // Last destination address
  let callerCity = ""; // City extracted from caller's last addresses or phone area
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let currentAssistantText = ""; // Buffer for assistant transcript
  let aiSpeaking = false; // Local speaking flag (used for safe barge-in cancellation)
  let lastFinalUserTranscript = ""; // Last finalized user transcript (safeguards for end_call)
  let geocodingEnabled = true; // Enable address verification by default
  let addressTtsSplicingEnabled = false; // Enable address TTS splicing (off by default)

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

  // Extract city from an address string
  const extractCityFromAddress = (address: string): string => {
    if (!address) return "";
    
    // Common UK city patterns - look for city names in the address
    const ukCities = [
      "london", "birmingham", "manchester", "leeds", "liverpool", "newcastle", 
      "sheffield", "bristol", "nottingham", "leicester", "coventry", "bradford",
      "cardiff", "edinburgh", "glasgow", "belfast", "cambridge", "oxford",
      "southampton", "portsmouth", "brighton", "reading", "derby", "wolverhampton",
      "stoke", "hull", "york", "sunderland", "swansea", "middlesbrough",
      "peterborough", "luton", "preston", "blackpool", "norwich", "exeter",
      "plymouth", "aberdeen", "dundee"
    ];
    
    const lowerAddress = address.toLowerCase();
    for (const city of ukCities) {
      if (lowerAddress.includes(city)) {
        return city.charAt(0).toUpperCase() + city.slice(1);
      }
    }
    return "";
  };

  // Geocode an address using Google Maps API (with city context)
  const geocodeAddress = async (address: string): Promise<{ found: boolean; display_name?: string; formatted_address?: string; error?: string }> => {
    try {
      // Use caller's city if we have one, otherwise try to extract from last addresses
      let city = callerCity;
      if (!city && callerLastPickup) {
        city = extractCityFromAddress(callerLastPickup);
      }
      if (!city && callerLastDestination) {
        city = extractCityFromAddress(callerLastDestination);
      }
      
      console.log(`[${callId}] 🌍 Geocoding address: "${address}" (city context: ${city || 'none'})`);
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({ address, city, country: "UK" }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Geocode API error: ${response.status}`);
        return { found: false, error: "Geocoding service unavailable" };
      }

      const result = await response.json();
      console.log(`[${callId}] 🌍 Geocode result for "${address}":`, result.found ? `FOUND - ${result.formatted_address || result.display_name}` : "NOT FOUND");
      return result;
    } catch (e) {
      console.error(`[${callId}] Geocode exception:`, e);
      return { found: false, error: "Geocoding failed" };
    }
  };

  // Generate TTS audio for an address using the taxi-address-tts function
  const generateAddressTts = async (address: string): Promise<{ audio: string; bytes: number } | null> => {
    if (!addressTtsSplicingEnabled) return null;
    
    try {
      console.log(`[${callId}] 🔊 Generating address TTS for: "${address}"`);
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/taxi-address-tts`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({ address, format: "pcm16" }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Address TTS API error: ${response.status}`);
        return null;
      }

      const result = await response.json();
      console.log(`[${callId}] 🔊 Address TTS generated: ${result.bytes} bytes for "${address}"`);
      return { audio: result.audio, bytes: result.bytes };
    } catch (e) {
      console.error(`[${callId}] Address TTS exception:`, e);
      return null;
    }
  };

  // Notify Ada about geocoding result and ask for correction if needed
  const notifyGeocodeResult = (addressType: "pickup" | "destination", address: string, found: boolean) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;

    if (found) {
      console.log(`[${callId}] ✅ ${addressType} address verified: "${address}"`);
      // No need to say anything - address is valid
    } else {
      console.log(`[${callId}] ⚠️ ${addressType} address not found in geocoder: "${address}" - but accepting it anyway`);
      
      // IMPORTANT: Do NOT ask customer to spell out common landmarks like train stations, airports, hospitals, etc.
      // Only ask for clarification if it's a residential address that sounds garbled
      const isLandmark = /\b(station|airport|hospital|university|college|school|shopping|centre|center|mall|supermarket|tesco|asda|sainsbury|morrisons|aldi|lidl|hotel|inn|pub|restaurant|church|mosque|temple|gurdwara|park|library|museum|theatre|theater|cinema|gym|sports|leisure|pool|bus\s*stop|taxi\s*rank)\b/i.test(address);
      
      if (isLandmark) {
        console.log(`[${callId}] 📍 Landmark detected - accepting without clarification: "${address}"`);
        return; // Don't ask for clarification on landmarks
      }
      
      // Only ask for clarification on unclear residential addresses
      const message = addressType === "pickup"
        ? `[SYSTEM: The pickup address "${address}" could not be verified. Politely ask the customer to confirm or provide the correct address. Say something like "I'm having a little trouble finding that address. Could you give me the full street name and postcode please?"]`
        : `[SYSTEM: The destination address "${address}" could not be verified. Politely ask the customer to confirm or provide the correct address. Say something like "I'm having a little trouble finding that destination. Could you give me the full address or postcode please?"]`;
      
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
        callerTotalBookings = data.total_bookings || 0;
        callerLastPickup = data.last_pickup || "";
        callerLastDestination = data.last_destination || "";
        
        // Extract city from last addresses for location-biased geocoding
        if (callerLastPickup) {
          callerCity = extractCityFromAddress(callerLastPickup);
        }
        if (!callerCity && callerLastDestination) {
          callerCity = extractCityFromAddress(callerLastDestination);
        }
        
        console.log(`[${callId}] 👤 Known caller: ${callerName} (${callerTotalBookings} previous bookings)`);
        if (callerLastPickup) {
          console.log(`[${callId}] 📍 Last trip: ${callerLastPickup} → ${callerLastDestination}`);
        }
        if (callerCity) {
          console.log(`[${callId}] 🏙️ Caller city: ${callerCity}`);
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
        caller_name: callerName || null,
        caller_phone: userPhone || null,
        caller_total_bookings: callerTotalBookings,
        caller_last_pickup: callerLastPickup || null,
        caller_last_destination: callerLastDestination || null,
        ...updates,
        transcripts: transcriptHistory,
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
      if (error) console.error(`[${callId}] Live call broadcast error:`, error);
    } catch (e) {
      console.error(`[${callId}] Live call broadcast exception:`, e);
    }
  };

  // Extract customer name from transcript - improved with multi-word names and better patterns
  const extractNameFromTranscript = (transcript: string): string | null => {
    const t = transcript.trim();
    
    // Expanded list of non-name words to filter out
    const nonNames = new Set([
      'yes', 'no', 'yeah', 'yep', 'nope', 'okay', 'ok', 'sure', 'please', 'thanks', 'thank',
      'hello', 'hi', 'hey', 'hiya', 'the', 'from', 'to', 'a', 'an', 'and', 'or', 'but',
      'taxi', 'cab', 'car', 'booking', 'book', 'need', 'want', 'would', 'like', 'can',
      'could', 'just', 'actually', 'really', 'well', 'um', 'uh', 'er', 'ah', 'oh',
      'good', 'morning', 'afternoon', 'evening', 'night', 'today', 'now', 'soon',
      'picking', 'pick', 'up', 'going', 'to', 'heading', 'one', 'two', 'three', 'four'
    ]);
    
    // Helper to capitalize name properly (handles multi-word names like "Mary Jane")
    const capitalizeName = (name: string): string => {
      const separator = name.includes('-') ? '-' : ' ';
      return name.split(/[\s-]+/)
        .map(part => part.charAt(0).toUpperCase() + part.slice(1).toLowerCase())
        .join(separator);
    };
    
    // Helper to validate a potential name
    const isValidName = (name: string): boolean => {
      if (!name || name.length < 2 || name.length > 30) return false;
      if (nonNames.has(name.toLowerCase())) return false;
      // Must contain at least one vowel (basic name check)
      if (!/[aeiouAEIOU]/.test(name)) return false;
      // Reject if it's all numbers or special chars
      if (/^[\d\s\-]+$/.test(name)) return false;
      return true;
    };
    
    // Patterns ordered by specificity (most specific first)
    // These capture multi-word names like "Mary Jane" or "John Smith"
    const patterns: Array<{ regex: RegExp; group: number }> = [
      // "My name is Mary Jane" / "My name's John Smith"
      { regex: /my name(?:'s| is)\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "I'm Mary" / "I am John Smith"
      { regex: /i(?:'m| am)\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "It's Mary" / "It is John"
      { regex: /it(?:'s| is)\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "This is Mary" / "This is John Smith"
      { regex: /this is\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "Call me Mary" / "You can call me John"
      { regex: /call me\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "The name's Mary" / "Name's John"
      { regex: /(?:the\s+)?name(?:'s| is)\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "Speaking" / "Mary speaking"
      { regex: /^([A-Za-z]+)\s+speaking/i, group: 1 },
      // "Hi, I'm Mary" / "Hello, it's John"
      { regex: /^(?:hi|hello|hey)[,\s]+(?:i'm|it's|this is)\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // "Yeah it's Mary" / "Yes, I'm John" 
      { regex: /^(?:yes|yeah|yep)[,\s]+(?:i'm|it's|this is)\s+([A-Za-z]+(?:\s+[A-Za-z]+)?)/i, group: 1 },
      // Just a single or double word name at start: "Mary" / "John Smith"
      { regex: /^([A-Za-z]+(?:\s+[A-Za-z]+)?)$/i, group: 1 },
      // Name at the start followed by common filler: "Mary here" / "John, hi"
      { regex: /^([A-Za-z]+)(?:\s+here|\s*[,.])/i, group: 1 },
    ];
    
    for (const { regex, group } of patterns) {
      const match = t.match(regex);
      if (match?.[group]) {
        const rawName = match[group].trim();
        // Take first name only if multi-word (more reliable)
        const firstName = rawName.split(/\s+/)[0];
        
        if (isValidName(firstName)) {
          const name = firstName.charAt(0).toUpperCase() + firstName.slice(1).toLowerCase();
          console.log(`[${callId}] 🔍 Regex extracted name: "${name}" from "${t}"`);
          return name;
        }
      }
    }
    
    return null;
  };
  
  // AI-powered name extraction for tricky cases
  const extractNameWithAI = async (transcript: string): Promise<string | null> => {
    try {
      console.log(`[${callId}] 🤖 AI name extraction for: "${transcript}"`);
      
      const response = await fetch("https://api.lovable.dev/v1/chat/completions", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${LOVABLE_API_KEY}`,
        },
        body: JSON.stringify({
          model: "google/gemini-2.5-flash",
          messages: [
            {
              role: "system",
              content: `You are a name extraction assistant. Extract the person's first name from their response to "What's your name?".
              
Rules:
- Return ONLY the first name, nothing else
- Return "NONE" if no name is found or if the text is not a name response
- Handle phonetic/spelled names: "M-A-R-Y" → "Mary", "it's Mary, M-A-R-Y" → "Mary"
- Handle accents/variations: "My name is Seán" → "Seán", "I'm María" → "María"
- Ignore filler words: "Um, it's John" → "John"
- For "Yes it's Mary" or "Yeah Mary" → "Mary"
- Only extract if they're clearly stating their name, not asking for a taxi to "Mary Street" etc.`
            },
            {
              role: "user",
              content: transcript
            }
          ],
          max_tokens: 20,
          temperature: 0
        })
      });
      
      if (!response.ok) {
        console.error(`[${callId}] AI name extraction failed: ${response.status}`);
        return null;
      }
      
      const data = await response.json();
      const aiName = data.choices?.[0]?.message?.content?.trim();
      
      if (aiName && aiName !== "NONE" && aiName.length >= 2 && aiName.length <= 30) {
        console.log(`[${callId}] 🤖 AI extracted name: "${aiName}"`);
        return aiName;
      }
      
      return null;
    } catch (e) {
      console.error(`[${callId}] AI name extraction error:`, e);
      return null;
    }
  };

  // Call the AI extraction function to get structured booking data from transcript
  const extractBookingFromTranscript = async (transcript: string): Promise<void> => {
    try {
      console.log(`[${callId}] 🔍 Extracting booking info from: "${transcript}"`);
      
      // Also try to extract name if we don't have one yet
      if (!callerName) {
        // First try regex extraction (fast)
        let extractedName = extractNameFromTranscript(transcript);
        
        // If regex fails and transcript looks like a name response, try AI
        if (!extractedName && transcript.length < 50) {
          extractedName = await extractNameWithAI(transcript);
        }
        
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

        // NOTE: Address TTS splicing is DISABLED
        // It was causing double-speak because Ada's response already includes the address
        // and the spliced audio plays separately on top of it.
        // If we want address splicing to work, we'd need to either:
        // 1. Have Ada skip saying the address (complex prompt engineering)
        // 2. Or splice DURING her response (requires audio manipulation)

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
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
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
              model: "whisper-1",
              // Auto-detect language (Whisper supports 99+ languages)
              // Prompt with taxi vocabulary and multi-digit house numbers for accuracy
              prompt: "247 Radio Carz taxi booking. House numbers: 52A, 1214A, 18B, 234C, 1567, 2345A. Streets: David Road, Warwick Road, Bradford Road, Coventry, Manchester, Leeds, Birmingham. Passengers: one, two, three, four, five, six. Spelling: Alpha Bravo Charlie Delta Echo Foxtrot."
            },
            // Server VAD - balanced for natural conversation flow
            // Give user more time to respond after Ada asks a question
            turn_detection: {
              type: "server_vad",
              threshold: 0.7,           // Higher threshold = only clear speech triggers barge-in
              prefix_padding_ms: 350,   // Capture lead-in for smoother onset
              silence_duration_ms: 900, // Wait longer (900ms) for user to respond
              create_response: true,    // Auto-create response when speech ends
              interrupt_response: true  // Allow user to interrupt Ada (prevents repetition)
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
              },
              {
                type: "function",
                name: "end_call",
                description: "End the phone call after the customer confirms they don't need anything else. Call this IMMEDIATELY after saying goodbye.",
                parameters: {
                  type: "object",
                  properties: {
                    reason: { type: "string", description: "Reason for ending call: 'booking_complete', 'customer_request', or 'no_further_assistance'" }
                  },
                  required: ["reason"]
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
          ? (callerLastDestination 
            ? `[Call connected - greet the RETURNING customer by name and OFFER QUICK REBOOKING. Their name is ${callerName}. Their usual destination is ${callerLastDestination}${callerLastPickup ? ` and usual pickup is ${callerLastPickup}` : ''}. Say: "Hello ${callerName}! Lovely to hear from you again. Shall I book you a taxi to ${callerLastDestination}, or are you heading somewhere different today?"]`
            : `[Call connected - greet the RETURNING customer by name. Their name is ${callerName}. Say: "Hello ${callerName}! Lovely to hear from you again. How can I help with your travels today?"]`)
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

        // Process any pending messages (including queued audio)
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
          } else if (msg.type === "audio" && msg.audio) {
            // Forward queued audio now that session is ready
            openaiWs?.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: msg.audio
            }));
          }
        }
      }

      // AI response started
      if (data.type === "response.created") {
        console.log(`[${callId}] >>> response.created - AI starting to generate`);
        currentAssistantText = ""; aiSpeaking = true; // Reset buffer

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
        
        // NON-BLOCKING: Broadcast audio to monitoring channel (no await = no jitter)
        void supabase.from("live_call_audio").insert({
          call_id: callId,
          audio_chunk: data.delta,
          created_at: new Date().toISOString()
        }); // Fire and forget - monitoring is optional
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
        const rawTranscript = data.transcript || "";
        console.log(`[${callId}] Raw user transcript: ${rawTranscript}`);
        
        // Filter Whisper hallucinations - common patterns when there's silence/noise
        const isHallucination = (text: string): boolean => {
          const t = text.trim();
          if (!t) return true;
          
          // Too many numbers in sequence (phone numbers, random digits)
          const digitCount = (t.match(/\d/g) || []).length;
          const wordCount = t.split(/\s+/).length;
          if (digitCount > 8 && digitCount > wordCount * 2) {
            console.log(`[${callId}] 🚫 Hallucination detected: too many digits (${digitCount})`);
            return true;
          }
          
          // Contains multiple city names in one utterance (unrealistic)
          const cities = ['london', 'manchester', 'birmingham', 'coventry', 'leeds', 'liverpool', 'sheffield', 'bristol', 'nottingham', 'leicester'];
          const citiesFound = cities.filter(c => t.toLowerCase().includes(c));
          if (citiesFound.length >= 3) {
            console.log(`[${callId}] 🚫 Hallucination detected: multiple cities (${citiesFound.join(', ')})`);
            return true;
          }
          
          // Detect counting sequences (one, two, three, four... OR 1, 2, 3, 4...)
          // These are common Whisper hallucinations when audio is unclear
          const numberWords = ['one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten', 
            'eleven', 'twelve', 'thirteen', 'fourteen', 'fifteen', 'sixteen', 'seventeen', 'eighteen', 'nineteen', 'twenty',
            'twenty-one', 'twenty-two', 'twenty-three', 'twenty-four', 'twenty-five'];
          const wordsLower = t.toLowerCase();
          let numberWordCount = 0;
          for (const nw of numberWords) {
            // Count occurrences of each number word
            const regex = new RegExp(`\\b${nw}\\b`, 'gi');
            const matches = wordsLower.match(regex);
            if (matches) numberWordCount += matches.length;
          }
          // If more than 4 number words, likely a counting hallucination
          if (numberWordCount >= 5) {
            console.log(`[${callId}] 🚫 Hallucination detected: counting sequence (${numberWordCount} number words)`);
            return true;
          }
          
          // Common Whisper hallucination phrases
          const hallucinationPhrases = [
            /thank you for watching/i,
            /please subscribe/i,
            /like and subscribe/i,
            /\[music\]/i,
            /\[applause\]/i,
            /subtitles by/i,
            /transcribed by/i,
            /^\.+$/,  // Just dots
            /^\s*\d+(\s+\d+){10,}\s*$/,  // Long sequences of numbers
          ];
          
          for (const pattern of hallucinationPhrases) {
            if (pattern.test(t)) {
              console.log(`[${callId}] 🚫 Hallucination detected: matches pattern ${pattern}`);
              return true;
            }
          }
          
          // Very long transcript from short audio (likely hallucination)
          // Normal speech is ~150 words per minute, so 3 seconds max = ~7-8 words
          // If we get 50+ words, it's likely a hallucination
          if (wordCount > 50) {
            console.log(`[${callId}] 🚫 Hallucination detected: too long (${wordCount} words)`);
            return true;
          }
          
          return false;
        };
        
        // Skip hallucinated transcripts
        if (isHallucination(rawTranscript)) {
          console.log(`[${callId}] 🚫 Skipping hallucinated transcript: "${rawTranscript.substring(0, 100)}..."`);
          // Don't process, don't save, don't forward
          return;
        }
        
        console.log(`[${callId}] User said: ${rawTranscript}`);
        lastFinalUserTranscript = rawTranscript;
        // IMMEDIATE NAME INJECTION - for new callers, quickly extract and inject name BEFORE AI responds
        // This prevents misheard names from being used in Ada's greeting
        if (!callerName) {
          const quickExtractName = (text: string): string | null => {
            const t = text.trim();
            // Quick patterns for common name responses
            const patterns = [
              /my name(?:'s| is)\s+([A-Za-z]+)/i,
              /i(?:'m| am)\s+([A-Za-z]+)/i,
              /it(?:'s| is)\s+([A-Za-z]+)/i,
              /this is\s+([A-Za-z]+)/i,
              /call me\s+([A-Za-z]+)/i,
              /^(?:yes|yeah)[,\s]+(?:i'm|it's)\s+([A-Za-z]+)/i,
              /^([A-Za-z]+)\s+speaking/i,
              /^([A-Za-z]+)$/i, // Single word
            ];
            
            const nonNames = new Set([
              'yes', 'no', 'yeah', 'yep', 'okay', 'ok', 'sure', 'please', 'thanks',
              'hello', 'hi', 'hey', 'taxi', 'cab', 'booking', 'book', 'need', 'want',
              'good', 'morning', 'afternoon', 'evening', 'just', 'actually', 'um', 'uh'
            ]);
            
            for (const pattern of patterns) {
              const match = t.match(pattern);
              if (match?.[1]) {
                const name = match[1].trim();
                if (name.length >= 2 && name.length <= 20 && !nonNames.has(name.toLowerCase())) {
                  return name.charAt(0).toUpperCase() + name.slice(1).toLowerCase();
                }
              }
            }
            return null;
          };
          
          const quickName = quickExtractName(rawTranscript);
          if (quickName && openaiWs?.readyState === WebSocket.OPEN) {
            callerName = quickName;
            console.log(`[${callId}] 👤 Quick name injection: "${callerName}"`);
            
            // Inject the exact name into Ada's context immediately
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "assistant",
                content: [{ type: "text", text: `[CRITICAL: Customer's name is "${callerName}". Use this EXACT spelling. Say "Lovely to meet you ${callerName}!" and continue with the booking.]` }]
              }
            }));
            
            // Save to database async (don't block)
            if (userPhone) {
              (async () => {
                try {
                  await supabase.from("callers").upsert({
                    phone_number: userPhone,
                    name: callerName,
                    updated_at: new Date().toISOString()
                  }, { onConflict: "phone_number" });
                  console.log(`[${callId}] 💾 Quick-saved name ${callerName} for ${userPhone}`);
                } catch (e) {
                  console.error(`[${callId}] Failed to quick-save name:`, e);
                }
              })();
            }
          }
        }
        
        // IMMEDIATE ADDRESS INJECTION - use regex to quickly find addresses and inject BEFORE AI responds
        // This prevents OpenAI from hallucinating addresses in its response
        const quickExtractAddresses = (text: string) => {
          // Pattern for addresses like "52A David Road" or "1214A Warwick Road"
          const addressPattern = /\b(\d+[A-Za-z]?\s+[A-Za-z]+(?:\s+[A-Za-z]+)?(?:\s+Road|Street|Avenue|Lane|Drive|Close|Way|Place|Court)?)\b/gi;
          const matches = text.match(addressPattern);
          return matches || [];
        };
        
        const addresses = quickExtractAddresses(rawTranscript);
        if (addresses.length > 0 && openaiWs?.readyState === WebSocket.OPEN) {
          // Inject the exact addresses as a USER message so Ada MUST use them in her response
          // Using role: "user" ensures this becomes part of the input context, not a memory note
          const pickupAddr = addresses[0] || null;
          const destAddr = addresses[1] || null;
          
          let addressInstruction = `[SYSTEM: The customer just provided addresses. You MUST use these EXACT spellings:\n`;
          if (pickupAddr) addressInstruction += `- PICKUP: "${pickupAddr}"\n`;
          if (destAddr) addressInstruction += `- DESTINATION: "${destAddr}"\n`;
          addressInstruction += `Repeat these addresses back EXACTLY as written above. Do NOT change any letters.]`;
          
          console.log(`[${callId}] 📢 Quick address injection: pickup="${pickupAddr}", dest="${destAddr}"`);
          
          // Inject as a hidden user message so it becomes part of the context
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: addressInstruction }]
            }
          }));
        }
        
        // Use AI extraction for accurate booking data (async - will update knownBooking)
        extractBookingFromTranscript(rawTranscript);
        
        // Save user message to history
        if (rawTranscript) {
          transcriptHistory.push({
            role: "user",
            text: rawTranscript,
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

        // NOTE: With create_response: true in VAD config, OpenAI auto-creates responses
        // Removed manual fallback to prevent double-speak race condition
        
        awaitingResponseAfterCommit = false;
        responseCreatedSinceCommit = false;
      }

      // Speech started (barge-in detection) - CANCEL AI response for faster interruption
      if (data.type === "input_audio_buffer.speech_started") {
        console.log(`[${callId}] >>> User started speaking (barge-in)`);
        socket.send(JSON.stringify({ type: "user_speaking", speaking: true }));
        // If Ada is speaking, cancel the current response so the caller can answer.
        if (aiSpeaking && openaiWs?.readyState === WebSocket.OPEN) openaiWs.send(JSON.stringify({ type: "response.cancel" }));
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
          
          // Calculate fare based on distance
          const GOOGLE_MAPS_API_KEY = Deno.env.get("GOOGLE_MAPS_API_KEY");
          const BASE_FARE = 3.50;
          const PER_MILE_RATE = 1.00;
          let distanceMiles = 0;
          let fare = 0;
          
          // Try to get actual road distance via Google Distance Matrix API
          if (GOOGLE_MAPS_API_KEY && finalBooking.pickup && finalBooking.destination) {
            try {
              const distanceUrl = `https://maps.googleapis.com/maps/api/distancematrix/json` +
                `?origins=${encodeURIComponent(finalBooking.pickup + ", UK")}` +
                `&destinations=${encodeURIComponent(finalBooking.destination + ", UK")}` +
                `&units=imperial` +
                `&key=${GOOGLE_MAPS_API_KEY}`;
              
              console.log(`[${callId}] 📏 Calculating distance: ${finalBooking.pickup} → ${finalBooking.destination}`);
              const distResponse = await fetch(distanceUrl);
              const distData = await distResponse.json();
              
              if (distData.status === "OK" && distData.rows?.[0]?.elements?.[0]?.status === "OK") {
                const element = distData.rows[0].elements[0];
                // Distance comes in meters when using imperial, but text shows miles
                const distanceText = element.distance?.text || "";
                const distanceMatch = distanceText.match(/([\d.]+)\s*mi/);
                if (distanceMatch) {
                  distanceMiles = parseFloat(distanceMatch[1]);
                } else {
                  // Fallback: convert meters to miles
                  distanceMiles = (element.distance?.value || 0) / 1609.34;
                }
                console.log(`[${callId}] 📏 Distance: ${distanceMiles.toFixed(2)} miles`);
              }
            } catch (e) {
              console.error(`[${callId}] Distance Matrix error:`, e);
            }
          }
          
          // Calculate fare: base + per mile
          if (distanceMiles > 0) {
            fare = BASE_FARE + (distanceMiles * PER_MILE_RATE);
            fare = Math.round(fare * 100) / 100; // Round to 2 decimal places
            console.log(`[${callId}] 💷 Fare: £${fare} (£${BASE_FARE} + ${distanceMiles.toFixed(2)} mi × £${PER_MILE_RATE})`);
          } else {
            // Fallback: estimate if distance calculation failed
            const isAirport = String(finalBooking.destination || "").toLowerCase().includes("airport");
            fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
            console.log(`[${callId}] 💷 Fare (fallback): £${fare}`);
          }
          
          // Add £5 for 6-seater van (5+ passengers)
          const is6Seater = Number(finalBooking.passengers || 0) > 4;
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
        
        // Handle end_call function
        if (data.name === "end_call") {
          const args = JSON.parse(data.arguments);

          const t = (lastFinalUserTranscript || "").toLowerCase().trim();
          const customerSaidNo =
            t === "no" ||
            t === "nope" ||
            t === "nah" ||
            t.includes("nothing else") ||
            t.includes("that's all") ||
            t.includes("thats all") ||
            t.includes("that's it") ||
            t.includes("thats it") ||
            t.includes("all sorted") ||
            t.includes("i'm good") ||
            t.includes("im good") ||
            t.includes("no thanks") ||
            t.includes("no thank you");

          // Safety: don't allow hanging up unless customer explicitly declined further help.
          if (!customerSaidNo) {
            console.log(
              `[${callId}] 🚫 Rejecting end_call (customer hasn't declined further assistance). lastUser="${lastFinalUserTranscript}" reason=${args.reason}`,
            );

            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    message:
                      "Customer has not confirmed they're finished. Ask again if they need anything else, then wait.",
                  }),
                },
              }),
            );
            return;
          }

          console.log(`[${callId}] 📞 End call requested: ${args.reason}`);

          // Update call status
          await broadcastLiveCall({
            status: "ended",
            ended_at: new Date().toISOString(),
          });

          // Send function result
          openaiWs?.send(
            JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ success: true, message: "Call ended" }),
              },
            }),
          );

          // Notify client to end the call
          socket.send(
            JSON.stringify({
              type: "call_ended",
              reason: args.reason,
            }),
          );

          // Close the WebSocket connection after a short delay to allow goodbye audio to finish
          setTimeout(() => {
            console.log(`[${callId}] 📞 Closing connection after end_call`);
            socket.close();
          }, 3000);
        }
      }

      // Response completed
      if (data.type === "response.done") {
        aiSpeaking = false; socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
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
        
        // Enable/disable address TTS splicing from client (default: false)
        if (message.addressTtsSplicing !== undefined) {
          addressTtsSplicingEnabled = message.addressTtsSplicing;
          console.log(`[${callId}] 🔊 Address TTS Splicing: ${addressTtsSplicingEnabled ? "ENABLED" : "DISABLED"}`);
        }
        
        // Set caller's city for location-biased geocoding
        if (message.city) {
          callerCity = message.city;
          console.log(`[${callId}] 🏙️ Caller city from Asterisk: ${callerCity}`);
        }
        
        // If name provided directly from Asterisk, use it (but not "Guest" placeholder)
        if (message.user_name && message.user_name !== "Guest" && message.user_name !== "Unknown") {
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
        console.log(`[${callId}] Call initialized (source: ${callSource}, phone: ${userPhone}, caller: ${callerName || 'unknown'}, city: ${callerCity || 'unknown'}, geocoding: ${geocodingEnabled})`);
        return;
      }
      
      // Forward audio to OpenAI - ONLY if session is fully configured
      if (message.type === "audio" && openaiWs?.readyState === WebSocket.OPEN) {
        if (!sessionReady) {
          // Queue audio until session is ready (prevents responses with default OpenAI instructions)
          console.log(`[${callId}] ⏳ Audio received before session ready - queueing`);
          pendingMessages.push({ type: "audio", audio: message.audio });
          return;
        }
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
