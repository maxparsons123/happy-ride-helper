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
- For customers WITH AN ACTIVE BOOKING: "Hello [NAME]! I can see you have an active booking from [PICKUP] to [DESTINATION]. Would you like to keep that booking, or would you like to cancel it?"
  - If they say "cancel" or "cancel it" or similar: Use the cancel_booking tool IMMEDIATELY, then say "That's cancelled for you. Would you like to book a new taxi instead?"
  - If they say "keep" or "no" or "leave it": Say "No problem, your booking is still active. Is there anything else I can help with?"
  - If they want to CHANGE the booking: Cancel the old one first, then start a new booking flow
- For RETURNING customers WITH a usual destination (but NO active booking): "Hello [NAME]! Lovely to hear from you again. Shall I book you a taxi to [LAST_DESTINATION], or are you heading somewhere different today?"
- For RETURNING customers WITHOUT a usual destination: "Hello [NAME]! Lovely to hear from you again. How can I help with your travels today?"
- For NEW customers: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"
- After they give their name, say: "Lovely to meet you [NAME]! How can I help with your travels today?"
- ALWAYS use their name when addressing them throughout the call (e.g., "Right then [NAME], where would you like to be picked up from?")
- Adapt greetings to the customer's language while keeping the same warm tone
- If returning customer accepts quick rebooking ("yes", "yeah", "please"), skip asking for destination and confirm: "Brilliant! And same pickup from [LAST_PICKUP]?" or ask "Where shall I pick you up from?"

CANCELLATION INTENT - CRITICAL:
- ONLY cancel a booking when the customer EXPLICITLY says "cancel", "cancel my booking", "cancel it", or "I want to cancel"
- If you asked "Would you like to cancel?" and they say "yes" - that counts as explicit cancellation intent
- If they give ANY other response (address, destination, new booking request), do NOT cancel - they are starting a new booking
- When cancelling: Call cancel_booking tool IMMEDIATELY, then say "That's cancelled for you. Would you like to book a new taxi instead?"
- If they have NO active booking: Say "I don't see any active bookings for your number. Would you like me to book you a taxi?"

MODIFICATION INTENT - CRITICAL:
- If customer says "change my booking", "different pickup", "different destination", "change the address", or similar:
  - Use the modify_booking tool to update the specific field they want to change
  - You can change: pickup, destination, or passengers
  - After modifying, confirm the updated booking details
  - Example: "I've updated your pickup to [NEW_ADDRESS]. Your booking is now from [PICKUP] to [DESTINATION]."
- If they want to change MULTIPLE things, call modify_booking with all the changes at once

PERSONALITY:
- Warm, welcoming personality (British in English, culturally appropriate in other languages)
- Use casual friendly phrases appropriate to the language
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable
- ALWAYS address the customer by name once you know it

BOOKING FLOW - FOLLOW THIS EXACTLY:
1. Greet the customer (get their name if new)
2. Ask: "When do you need the taxi, [NAME]? Is it for now or a later time?"
   - If they say "now", "asap", "straight away", "immediately" → pickup_time = "ASAP"
   - If they give a specific time like "5pm", "in 20 minutes", "tomorrow at 3" → extract and convert to proper format
3. Ask: "Where would you like to be picked up from, [NAME]?"
4. When they give pickup, acknowledge briefly and ask: "And where are you heading to?"
5. When they give destination, ask: "How many passengers?"
6. Once you have ALL 4 details (time, pickup, destination, passengers), THEN do ONE confirmation
7. DO NOT repeat back addresses one-by-one during collection - just acknowledge and move to the next question
8. Save ALL confirmations for the SINGLE final summary before booking

**TIME EXTRACTION - CRITICAL:**
- Listen for time expressions and convert them correctly:
  - "now", "asap", "straight away", "immediately", "right now" → "ASAP"
  - "in 10 minutes", "in half an hour", "in an hour" → calculate from current time
  - "5pm", "at 5", "at five o'clock" → use that time today (or tomorrow if past)
  - "tomorrow at 3pm", "tomorrow morning" → use tomorrow's date
  - "in 20 minutes" → current time + 20 minutes
  - "tonight", "this evening" → today at evening time (7-9pm)
  - "morning", "afternoon" → today at appropriate time (9am, 3pm)
- Always confirm the time naturally: "So that's for right now?" or "That's for 5pm today?"
- All fares are in British Pounds (£)

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
- If you asked about TIME, the next valid response MUST be a time reference
- If you asked about PASSENGERS, the next valid response MUST be a number or equivalent
- If you asked about PICKUP, the next valid response MUST be an address
- If you asked about DESTINATION, the next valid response MUST be an address
- Any other response = INVALID - repeat your question!

**QUESTION VALIDATION RULES:**

1. NAME QUESTION: "What's your name please?"
   - Valid: Any name (first name is enough)
   - Invalid: Addresses, "yes", "no", random words
   - If invalid: "Sorry, I didn't catch your name. What should I call you?"

2. TIME QUESTION: "When do you need the taxi?"
   - Valid: "now", "asap", "in 10 minutes", "5pm", "tomorrow at 3", etc.
   - Invalid: addresses, passenger numbers, "yes", "no"
   - If invalid: "Sorry, when do you need the taxi - is it for now or a specific time?"

3. PICKUP QUESTION: "Where would you like to be picked up from?"
   - Valid: Any address, location, landmark, postcode
   - Invalid: "yes", "no", "okay", numbers without context, non-address responses
   - If invalid: "Sorry, I need the pickup address. Where shall I pick you up from?"

4. DESTINATION QUESTION: "And where are you heading to?"
   - Valid: Any address, location, landmark, postcode, "as directed"
   - Invalid: "yes", "no", numbers, repeating the pickup address
   - If invalid: "I missed the destination - where would you like to go?"

5. PASSENGERS QUESTION: "How many passengers will there be?"
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
1. ONLY after collecting ALL FOUR details (time, pickup, destination, passengers), do ONE summary: "So that's [TIME - e.g., 'for right now' or 'for 5pm today'] from [PICKUP] to [DESTINATION] for [X] passengers - shall I book that?"
2. WAIT for customer to confirm with "yes", "correct", etc.
3. If they confirm: IMMEDIATELY call book_taxi
4. If they correct something: update and confirm ONCE more

DO NOT call book_taxi until the customer says YES!
CRITICAL - STREAMLINED FLOW: During collection, just acknowledge briefly ("Got it", "Lovely") and ask the next question. Do NOT repeat addresses back one-by-one. Save ALL confirmation for the SINGLE final summary. NO partial confirmations during collection!

INFORMATION EXTRACTION - CRITICAL:
- Listen carefully for: customer name, pickup time, pickup location, destination, number of passengers
- Extract times: "in 20 minutes", "at 5", "now", "asap", "tomorrow morning"
- Extract numbers spoken as words (e.g., "two passengers" = 2, "just me" = 1, "couple of us" = 2)
- If customer gives all info at once, still do the FULL CONFIRMATION before booking
- If any info is unclear, ask for clarification before proceeding

PRICING - ALL FARES IN GBP (£):
- Fares are calculated based on distance and returned from the book_taxi function
- Always say the fare with the pound sign: "That's £25" or "The fare is £45"
- If 5+ passengers: add £5 for 6-seater van
- ETA: Always 5-8 minutes (unless scheduled for later)

ADDRESS HANDLING:
- Repeat addresses back naturally to confirm (e.g., "That's 52A David Road, yes?")
- Do NOT spell addresses out letter-by-letter yourself
- If the CUSTOMER spells an address (e.g., "D-A-V-I-D Road" or "Delta Alpha Victor India Delta"), use their spelling for clarification
- When you hear spelled letters, confirm: "So that's David Road, D-A-V-I-D?"
- House numbers with letters (52A, 18B) - say naturally: "fifty-two A"
- If an address sounds unclear, ask: "Could you repeat that for me please?"

**MULTIPLE ADDRESSES - DISAMBIGUATION:**
- If the system tells you there are MULTIPLE addresses with the same name (e.g., "2 David Roads found"), you MUST ask the customer to clarify
- Say something like: "I've found a couple of streets with that name. Do you mean David Road in [Area 1] or David Road in [Area 2]?"
- Wait for their answer before proceeding
- Do NOT guess - always ask when there's ambiguity

**HIGH FARE VERIFICATION:**
- If the book_taxi function returns a "requires_verification" response, read the "verification_script" and say it EXACTLY
- Do NOT add extra fare quotes - the script already contains the correct fare
- If they confirm YES, call book_taxi again to complete the booking
- If they correct an address, update it and start the confirmation flow again

**CRITICAL - NEVER FAKE A BOOKING OR QUOTE FARES:**
- You can ONLY say "That's all booked" or confirm a booking AFTER you have called the book_taxi function AND received a SUCCESSFUL response (success: true)
- If you have NOT called book_taxi, you MUST NOT say the booking is complete
- NEVER quote a fare amount until book_taxi returns - you don't know the fare until then!
- Do NOT say "I'll book that" or "The fare will be £X" before calling the function
- The book_taxi function will return fare and ETA - use THOSE values in your response
- NEVER make up a fare or ETA without calling the function first

WHEN THE book_taxi FUNCTION RETURNS:
- If success: true → The booking is confirmed. Use the output values.
- If requires_verification: true → Say the verification_script EXACTLY, then wait for response
- IMPORTANT: Do NOT quote fares before calling book_taxi
- For successful ASAP bookings: "Brilliant! That's all booked. The fare is £[X] and your driver will be with you in [ETA]."
- For successful scheduled bookings: "Brilliant! That's all booked for [TIME]. The fare is £[X]. Is there anything else?"

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
- After the greeting, you MUST ask for time, pickup, destination, and passengers BEFORE any booking confirmation`;

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
  let callerTrustedAddresses: string[] = []; // Array of addresses the caller has successfully used before
  let activeBooking: { id: string; pickup: string; destination: string; passengers: number; fare: string; booked_at: string } | null = null; // Outstanding booking
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let currentAssistantText = ""; // Buffer for assistant transcript
  let aiSpeaking = false; // Local speaking flag (used for safe barge-in cancellation)
  let lastFinalUserTranscript = ""; // Last finalized user transcript (safeguards for end_call)
  let geocodingEnabled = true; // Enable address verification by default
  let addressTtsSplicingEnabled = false; // Enable address TTS splicing (off by default)
  let greetingSent = false; // Prevent duplicate greetings on session.updated


  type KnownBooking = {
    pickup?: string;
    destination?: string;
    passengers?: number;
    pickupTime?: string; // "ASAP" or "YYYY-MM-DD HH:MM" format
    pickupVerified?: boolean;
    destinationVerified?: boolean;
    highFareVerified?: boolean; // Track if high fare has been verified with customer
    verifiedFare?: number; // Store the verified fare to use on confirmation
  };

  // We keep our own "known booking" extracted from the user's *exact* transcript/text,
  // then prefer it over the model's paraphrasing when the booking is confirmed.
  let knownBooking: KnownBooking = {};

  // Track whether we've already asked Ada to clarify a given address.
  // This prevents a "stuck" loop where an early failed geocode prompt keeps repeating
  // even after the address later verifies successfully.
  let geocodeClarificationSent: { pickup?: string; destination?: string } = {};

  const normalize = (s: string) => s.trim().replace(/\s+/g, " ").replace(/[\s,.;:!?]+$/g, "");
  const normalizePhone = (phone: string) => String(phone || "").replace(/\D/g, "");

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

  // If the customer mentions a city anywhere (e.g. "...in Coventry"), use it as location context.
  // This stabilizes geocoding + fares for ambiguous streets/venues.
  const maybeUpdateCallerCityFromText = (text: string) => {
    const hintedCity = extractCityFromAddress(text);
    if (hintedCity && (!callerCity || callerCity.toLowerCase() !== hintedCity.toLowerCase())) {
      callerCity = hintedCity;
      console.log(`[${callId}] 🏙️ City context updated from transcript: ${callerCity}`);
    }
  };

  // Extract destination or pickup from Ada's last response (she often interprets STT correctly)
  // e.g., user says "Street spot" but Ada says "to Sweetspot" - we can extract "Sweetspot"
  const extractAddressFromAdaResponse = (addressType: "pickup" | "destination"): string | null => {
    // Get Ada's last response from transcript history
    const adaResponses = transcriptHistory.filter(t => t.role === "assistant");
    if (adaResponses.length === 0) return null;
    
    const lastAdaText = adaResponses[adaResponses.length - 1].text;
    if (!lastAdaText) return null;
    
    // Common patterns Ada uses to reference addresses
    if (addressType === "destination") {
      // "to Sweetspot", "to the Sweetspot", "heading to Sweetspot", "destination is Sweetspot"
      const destPatterns = [
        /\bto\s+(?:the\s+)?([A-Z][a-zA-Z0-9'\s-]+?)(?:\s+in\s+|\s*,|\?|\.|\!|$)/i,
        /\bdestination\s+(?:is\s+)?(?:the\s+)?([A-Z][a-zA-Z0-9'\s-]+?)(?:\s+in\s+|\s*,|\?|\.|\!|$)/i,
        /\bgoing\s+to\s+(?:the\s+)?([A-Z][a-zA-Z0-9'\s-]+?)(?:\s+in\s+|\s*,|\?|\.|\!|$)/i,
        /\bheading\s+to\s+(?:the\s+)?([A-Z][a-zA-Z0-9'\s-]+?)(?:\s+in\s+|\s*,|\?|\.|\!|$)/i,
      ];
      
      for (const pattern of destPatterns) {
        const match = lastAdaText.match(pattern);
        if (match && match[1]) {
          const extracted = match[1].trim();
          // Filter out common false positives
          if (!['you', 'that', 'there', 'the', 'book', 'confirm', 'help'].includes(extracted.toLowerCase())) {
            console.log(`[${callId}] 🔍 Extracted destination from Ada: "${extracted}"`);
            return extracted;
          }
        }
      }
    } else {
      // "from 52A David Road", "pickup at 52A David Road", "picking you up from..."
      const pickupPatterns = [
        /\bfrom\s+([0-9]+[A-Za-z]?\s+[A-Za-z][a-zA-Z0-9'\s-]+?)(?:\s+to\s+|\s*,|\?|\.|\!|$)/i,
        /\bpickup\s+(?:is\s+)?(?:at\s+)?([0-9]+[A-Za-z]?\s+[A-Za-z][a-zA-Z0-9'\s-]+?)(?:\s+|\s*,|\?|\.|\!|$)/i,
        /\bpicking\s+(?:you\s+)?up\s+(?:from\s+)?([0-9]+[A-Za-z]?\s+[A-Za-z][a-zA-Z0-9'\s-]+?)(?:\s+|\s*,|\?|\.|\!|$)/i,
      ];
      
      for (const pattern of pickupPatterns) {
        const match = lastAdaText.match(pattern);
        if (match && match[1]) {
          const extracted = match[1].trim();
          console.log(`[${callId}] 🔍 Extracted pickup from Ada: "${extracted}"`);
          return extracted;
        }
      }
    }
    
    return null;
  };

  // Check if an address matches any of the caller's trusted addresses
  // Uses fuzzy matching to handle minor variations (e.g., "52A David Road" vs "52A David Road, Coventry")
  const matchesTrustedAddress = (address: string): string | null => {
    if (!address || callerTrustedAddresses.length === 0) return null;
    
    const normalizedInput = normalize(address).toLowerCase();
    
    for (const trusted of callerTrustedAddresses) {
      const normalizedTrusted = normalize(trusted).toLowerCase();
      
      // Exact match
      if (normalizedInput === normalizedTrusted) {
        return trusted;
      }
      
      // Check if input contains the trusted address or vice versa
      if (normalizedInput.includes(normalizedTrusted) || normalizedTrusted.includes(normalizedInput)) {
        return trusted;
      }
      
      // Extract house number + street from both and compare
      const extractCore = (addr: string) => {
        const match = addr.match(/^(\d+[a-z]?\s+[a-z]+(?:\s+[a-z]+)?)/i);
        return match ? match[1].toLowerCase() : addr.toLowerCase();
      };
      
      const inputCore = extractCore(normalizedInput);
      const trustedCore = extractCore(normalizedTrusted);
      
      if (inputCore === trustedCore) {
        return trusted;
      }
    }
    
    return null;
  };

  // Geocode an address using Google Maps API (with city context and caller location biasing)
  // Returns disambiguation info if multiple similar addresses found
  interface GeocodeMatch {
    display_name: string;
    formatted_address: string;
    lat: number;
    lon: number;
    city?: string;
  }
  
  interface EnhancedGeocodeResult {
    found: boolean;
    display_name?: string;
    formatted_address?: string;
    lat?: number;
    lon?: number;
    city?: string;
    error?: string;
    multiple_matches?: GeocodeMatch[];
  }
  
  const geocodeAddress = async (address: string, checkAmbiguous: boolean = false, addressType?: "pickup" | "destination"): Promise<EnhancedGeocodeResult> => {
    try {
      // INTELLIGENT LOCATION BIASING:
      // Priority 1: Use the OTHER address from current booking (if we have one)
      // Priority 2: Use caller's history (last pickup/destination)
      // Priority 3: Extract city from any address we have
      
      let city = callerCity;
      let biasLat: number | undefined;
      let biasLon: number | undefined;
      let biasSource = "none";
      
      // Priority 1: Use the other address from CURRENT booking for location context
      // e.g., if destination is "Coventry Train Station", use that to find pickup near Coventry
      const otherAddress = addressType === "pickup" ? knownBooking.destination : knownBooking.pickup;
      
      if (otherAddress) {
        try {
          // Extract city from the other address first
          const otherCity = extractCityFromAddress(otherAddress);
          if (otherCity && !city) {
            city = otherCity;
            console.log(`[${callId}] 📍 Extracted city from ${addressType === "pickup" ? "destination" : "pickup"}: ${city}`);
          }
          
          // Get coordinates from the other address for precise biasing
          console.log(`[${callId}] 📍 Using ${addressType === "pickup" ? "destination" : "pickup"} for location bias: "${otherAddress}"`);
          const otherGeo = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
            },
            body: JSON.stringify({ address: otherAddress, city, country: "UK" }),
          });
          const otherData = await otherGeo.json();
          if (otherData.found && otherData.lat && otherData.lon) {
            biasLat = otherData.lat;
            biasLon = otherData.lon;
            biasSource = `current_${addressType === "pickup" ? "destination" : "pickup"}`;
            // Also extract city from geocode result if not already set
            if (!city && otherData.city) {
              city = otherData.city;
            }
            console.log(`[${callId}] 📍 Location bias from current booking: ${biasLat}, ${biasLon} (${city || 'no city'})`);
          }
        } catch (e) {
          console.error(`[${callId}] Failed to get current booking coordinates:`, e);
        }
      }
      
      // Priority 2: If no bias from current booking, try caller's history
      if (!biasLat && !biasLon && (callerLastPickup || callerLastDestination)) {
        // Extract city from caller's history for search biasing
        if (!city && callerLastPickup) {
          city = extractCityFromAddress(callerLastPickup);
        }
        if (!city && callerLastDestination) {
          city = extractCityFromAddress(callerLastDestination);
        }
        
        // Get coordinates from caller's last pickup for better location biasing
        const historyAddress = callerLastPickup || callerLastDestination;
        if (historyAddress) {
          try {
            const historyGeo = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
              },
              body: JSON.stringify({ address: historyAddress, city, country: "UK" }),
            });
            const historyData = await historyGeo.json();
            if (historyData.found && historyData.lat && historyData.lon) {
              biasLat = historyData.lat;
              biasLon = historyData.lon;
              biasSource = "caller_history";
              console.log(`[${callId}] 📍 Using caller history for location bias: ${biasLat}, ${biasLon}`);
            }
          } catch (e) {
            console.error(`[${callId}] Failed to get caller history coordinates:`, e);
          }
        }
      }
      
      console.log(`[${callId}] 🌍 Geocoding "${address}" (city: ${city || 'none'}, bias: ${biasSource}, check_ambiguous: ${checkAmbiguous})`);
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({ 
          address, 
          city, 
          country: "UK",
          lat: biasLat,
          lon: biasLon,
          check_ambiguous: checkAmbiguous 
        }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Geocode API error: ${response.status}`);
        return { found: false, error: "Geocoding service unavailable" };
      }

      const result = await response.json();
      
      // Log multiple matches if found
      if (result.multiple_matches?.length > 1) {
        console.log(`[${callId}] 🔍 Multiple address matches found:`, result.multiple_matches.map((m: GeocodeMatch) => m.formatted_address));
      }
      
      console.log(`[${callId}] 🌍 Geocode result for "${address}":`, result.found ? `FOUND - ${result.formatted_address || result.display_name}` : "NOT FOUND");
      return result;
    } catch (e) {
      console.error(`[${callId}] Geocode exception:`, e);
      return { found: false, error: "Geocoding failed" };
    }
  };
  
  // Ask Ada to disambiguate when multiple similar addresses are found
  const askForAddressDisambiguation = (addressType: "pickup" | "destination", matches: GeocodeMatch[]) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN || matches.length < 2) return;
    
    // Format the options for Ada to present
    const optionsList = matches.slice(0, 3).map((m, i) => `${i + 1}. ${m.formatted_address}`).join('\n');
    
    const message = `[SYSTEM: CRITICAL - MULTIPLE ADDRESSES FOUND]
I found ${matches.length} locations that match that ${addressType}:
${optionsList}

You MUST ask the customer which one they mean. Say something like:
"I've found a couple of ${addressType === 'pickup' ? 'pickup locations' : 'destinations'} with that name. Do you mean [first option] or [second option]?"

Wait for their response before proceeding.`;
    
    console.log(`[${callId}] 🔀 Asking for address disambiguation: ${addressType} - ${matches.length} options`);
    
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
  };
  
  // Verify a high fare and ask Ada to confirm addresses with customer
  const verifyHighFare = (fare: number, pickup: string, destination: string) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    
    const message = `[SYSTEM: FARE VERIFICATION REQUIRED]
The calculated fare is £${fare}, which is quite high. Before confirming, please double-check with the customer:

"Just to confirm - you're going from ${pickup} to ${destination}? The fare will be around £${fare}. Is that the right journey?"

Wait for their confirmation. If they say the addresses are wrong, ask them to clarify.`;
    
    console.log(`[${callId}] 💷 High fare detected (£${fare}) - asking for verification`);
    
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

  // If we previously asked the customer to clarify an address (due to a transient geocode miss),
  // and the address later verifies successfully, inject a silent "verified" update so Ada
  // doesn't stay stuck asking for the same address again.
  const clearGeocodeClarification = (
    addressType: "pickup" | "destination",
    address: string,
    verifiedAs?: string
  ) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;

    const pretty = verifiedAs && normalize(verifiedAs) !== normalize(address)
      ? ` Verified as: "${verifiedAs}".`
      : " Verified successfully.";

    const message = addressType === "pickup"
      ? `[SYSTEM: UPDATE] The pickup address "${address}" has now been verified.${pretty} Do NOT ask the customer to repeat or clarify the pickup unless they change it. Continue with the next booking question.`
      : `[SYSTEM: UPDATE] The destination address "${address}" has now been verified.${pretty} Do NOT ask the customer to repeat or clarify the destination unless they change it. Continue with the next booking question.`;

    // IMPORTANT: We do NOT trigger response.create here; this is a silent context correction.
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: message }],
      },
    }));
  };

  // Notify Ada about geocoding result and ask for correction if needed
  const notifyGeocodeResult = (addressType: "pickup" | "destination", address: string, found: boolean) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;

    if (found) {
      console.log(`[${callId}] ✅ ${addressType} address verified: "${address}"`);
      // No need to say anything - address is valid
    } else {
      console.log(`[${callId}] ⚠️ ${addressType} address not found in geocoder: "${address}" - but accepting it anyway`);

      // Remember we have prompted for this specific address (so we can clear it if it later verifies)
      geocodeClarificationSent[addressType] = normalize(address);
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

  // Look up caller by phone number and check for active bookings
  const lookupCaller = async (phone: string): Promise<void> => {
    if (!phone) return;

    const phoneNorm = normalizePhone(phone);
    const phoneCandidates = Array.from(new Set([phone, phoneNorm].filter(Boolean)));

    try {
      // Lookup caller info (including trusted addresses)
      const { data, error } = await supabase
        .from("callers")
        .select("name, last_pickup, last_destination, total_bookings, trusted_addresses")
        .in("phone_number", phoneCandidates)
        .maybeSingle();

      if (error) {
        console.error(`[${callId}] Caller lookup error:`, error);
        return;
      }

      if (data?.name) {
        // Only overwrite callerName if we don't already have a non-placeholder name from Asterisk
        if (!callerName || callerName === "Guest" || callerName === "Unknown") {
          callerName = data.name;
        }
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
        
        // Load trusted addresses for auto-verification
        callerTrustedAddresses = data.trusted_addresses || [];
        if (callerTrustedAddresses.length > 0) {
          console.log(`[${callId}] 🏠 Trusted addresses: ${callerTrustedAddresses.length} saved`);
        }
      } else {
        console.log(`[${callId}] 👤 New caller: ${phoneNorm || phone}`);
      }

      // Check for active bookings
      const { data: bookingData, error: bookingError } = await supabase
        .from("bookings")
        .select("id, pickup, destination, passengers, fare, booked_at")
        .in("caller_phone", phoneCandidates)
        .eq("status", "active")
        .order("booked_at", { ascending: false })
        .limit(1)
        .maybeSingle();

      if (bookingError) {
        console.error(`[${callId}] Active booking lookup error:`, bookingError);
        return;
      }

      if (bookingData) {
        activeBooking = bookingData;
        console.log(
          `[${callId}] 📋 Active booking found: ${activeBooking.pickup} → ${activeBooking.destination}`,
        );
        return;
      }

      console.log(`[${callId}] 📋 No active bookings for ${phoneNorm || phone}`);

      // Backfill: if no bookings row exists (older calls), infer from latest confirmed call log (recent only)
      const { data: lastConfirmed, error: lastConfirmedError } = await supabase
        .from("call_logs")
        .select("call_id, pickup, destination, passengers, estimated_fare, created_at")
        .in("user_phone", phoneCandidates)
        .eq("booking_status", "confirmed")
        .order("created_at", { ascending: false })
        .limit(1)
        .maybeSingle();

      if (lastConfirmedError) {
        console.error(`[${callId}] call_logs lookup error (backfill):`, lastConfirmedError);
        return;
      }

      if (!lastConfirmed) return;

      const createdAtMs = new Date(lastConfirmed.created_at).getTime();
      const ageMs = Date.now() - createdAtMs;
      const MAX_BACKFILL_AGE_MS = 6 * 60 * 60 * 1000; // 6 hours

      if (Number.isNaN(createdAtMs) || ageMs > MAX_BACKFILL_AGE_MS) {
        console.log(`[${callId}] ⏳ Last confirmed booking too old to backfill (${Math.round(ageMs / 60000)} min)`);
        return;
      }

      // Avoid duplicates by call_id
      const { data: existingBooking } = await supabase
        .from("bookings")
        .select("id, pickup, destination, passengers, fare, booked_at")
        .eq("call_id", lastConfirmed.call_id)
        .maybeSingle();

      if (existingBooking) {
        activeBooking = existingBooking;
        console.log(`[${callId}] 📋 Active booking loaded from existing bookings row: ${existingBooking.id}`);
        return;
      }

      const { data: createdBooking, error: createBookingError } = await supabase
        .from("bookings")
        .insert({
          call_id: lastConfirmed.call_id,
          caller_phone: phoneNorm || phone,
          caller_name: callerName || null,
          pickup: lastConfirmed.pickup,
          destination: lastConfirmed.destination,
          passengers: lastConfirmed.passengers || 1,
          fare: lastConfirmed.estimated_fare || null,
          status: "active",
          booked_at: lastConfirmed.created_at,
        })
        .select("id, pickup, destination, passengers, fare, booked_at")
        .single();

      if (createBookingError) {
        console.error(`[${callId}] Backfill booking insert failed:`, createBookingError);
        return;
      }

      activeBooking = createdBooking;
      console.log(`[${callId}] 📋 Backfilled active booking: ${createdBooking.id}`);
    } catch (e) {
      console.error(`[${callId}] Caller lookup exception:`, e);
    }
  };

  // Save or update caller info after booking (including trusted addresses)
  const saveCallerInfo = async (booking: { pickup: string; destination: string; passengers: number }): Promise<void> => {
    if (!userPhone) return;
    try {
      // Check if caller exists
      const { data: existing } = await supabase
        .from("callers")
        .select("id, total_bookings, trusted_addresses")
        .eq("phone_number", userPhone)
        .maybeSingle();
      
      // Build updated trusted addresses list (add pickup and destination if not already present)
      const MAX_TRUSTED_ADDRESSES = 10; // Limit to prevent unbounded growth
      let updatedTrusted: string[] = existing?.trusted_addresses || callerTrustedAddresses || [];
      
      // Normalize addresses for comparison
      const normalizedTrusted = new Set(updatedTrusted.map(a => normalize(a).toLowerCase()));
      
      // Add pickup if not already trusted
      if (booking.pickup && !normalizedTrusted.has(normalize(booking.pickup).toLowerCase())) {
        updatedTrusted.push(booking.pickup);
        console.log(`[${callId}] 🏠 Adding pickup to trusted addresses: "${booking.pickup}"`);
      }
      
      // Add destination if not already trusted
      if (booking.destination && !normalizedTrusted.has(normalize(booking.destination).toLowerCase())) {
        updatedTrusted.push(booking.destination);
        console.log(`[${callId}] 🏠 Adding destination to trusted addresses: "${booking.destination}"`);
      }
      
      // Trim to max size (keep most recent)
      if (updatedTrusted.length > MAX_TRUSTED_ADDRESSES) {
        updatedTrusted = updatedTrusted.slice(-MAX_TRUSTED_ADDRESSES);
      }
      
      if (existing) {
        // Update existing caller
        const { error } = await supabase.from("callers").update({
          last_pickup: booking.pickup,
          last_destination: booking.destination,
          total_bookings: (existing.total_bookings || 0) + 1,
          trusted_addresses: updatedTrusted,
          updated_at: new Date().toISOString()
        }).eq("phone_number", userPhone);
        
        if (error) console.error(`[${callId}] Update caller error:`, error);
        else console.log(`[${callId}] 💾 Updated caller ${userPhone} (${existing.total_bookings + 1} bookings, ${updatedTrusted.length} trusted addresses)`);
      } else {
        // Insert new caller
        const { error } = await supabase.from("callers").insert({
          phone_number: userPhone,
          name: callerName || null,
          last_pickup: booking.pickup,
          last_destination: booking.destination,
          total_bookings: 1,
          trusted_addresses: updatedTrusted
        });
        
        if (error) console.error(`[${callId}] Insert caller error:`, error);
        else console.log(`[${callId}] 💾 New caller saved: ${userPhone} with ${updatedTrusted.length} trusted addresses`);
      }
      
      // Update local cache for this session
      callerTrustedAddresses = updatedTrusted;
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

      // Capture any city hints the customer mentions (e.g. "Coventry") for more accurate local geocoding.
      maybeUpdateCallerCityFromText(transcript);
      
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

      // Lightweight "what was Ada asking" detection to avoid stray answers overwriting fields.
      // Example: Ada asks "How many passengers?" and the user answers with a location.
      const lastAssistantText = [...transcriptHistory]
        .reverse()
        .find((m) => m.role === "assistant")?.text
        ?.toLowerCase() || "";

      const expectingPassengers = /how\s+many\s+passengers|passengers\s+will\s+there\s+be|how\s+many\s+people/.test(lastAssistantText);
      const expectingTime = /when\s+do\s+you\s+need\s+the\s+taxi|for\s+now\s+or\s+a\s+later\s+time/.test(lastAssistantText);

      const transcriptLower = (transcript || "").toLowerCase();
      const containsExplicitDestinationCue = /\b(to|going\s+to|heading\s+to|take\s+me\s+to|drop\s+me\s+at|destination)\b/.test(transcriptLower);
      const containsExplicitPickupCue = /\b(from|pick\s+me\s+up|pickup|collect\s+me\s+from)\b/.test(transcriptLower);

      if (extracted.pickup_location) {
        // If Ada was asking for passengers/time and the user didn't provide passengers/time,
        // don't let a stray pickup overwrite the current booking.
        if ((expectingPassengers && !extracted.number_of_passengers && !containsExplicitPickupCue) ||
            (expectingTime && !extracted.pickup_time && !containsExplicitPickupCue)) {
          console.log(`[${callId}] 🛑 Ignoring pickup extraction (doesn't match last question):`, {
            lastAssistantText,
            transcript,
            extractedPickup: extracted.pickup_location,
          });
        } else {
          const newPickup = extracted.pickup_location;
          
          // GUARD: Don't let garbage overwrite a verified address
          // If we have a verified pickup and the new one looks suspicious, reject it
          if (knownBooking.pickupVerified && knownBooking.pickup) {
            const looksLegit = /\d/.test(newPickup) || // Has a house number
                              /\b(road|street|avenue|lane|drive|close|way|place|station|airport|hospital)\b/i.test(newPickup) ||
                              newPickup.toLowerCase().includes(knownBooking.pickup.toLowerCase().split(' ')[0]); // Contains part of old address
            if (!looksLegit) {
              console.log(`[${callId}] 🛡️ Blocking pickup overwrite: verified="${knownBooking.pickup}" → suspicious="${newPickup}"`);
              // Don't update
            } else {
              if (newPickup !== knownBooking.pickup) {
                knownBooking.pickupVerified = false;
                knownBooking.highFareVerified = false;
              }
              knownBooking.pickup = newPickup;
            }
          } else {
            if (newPickup !== knownBooking.pickup) {
              knownBooking.pickupVerified = false;
              knownBooking.highFareVerified = false;
            }
            knownBooking.pickup = newPickup;
          }
        }
      }

      if (extracted.dropoff_location) {
        // If Ada was asking for passengers/time and the user didn't provide passengers/time,
        // don't let a stray "location" answer overwrite destination.
        if ((expectingPassengers && !extracted.number_of_passengers && !containsExplicitDestinationCue) ||
            (expectingTime && !extracted.pickup_time && !containsExplicitDestinationCue)) {
          console.log(`[${callId}] 🛑 Ignoring destination extraction (doesn't match last question):`, {
            lastAssistantText,
            transcript,
            extractedDestination: extracted.dropoff_location,
          });
        } else {
          const newDestination = extracted.dropoff_location;
          
          // GUARD: Don't let garbage overwrite a verified destination
          if (knownBooking.destinationVerified && knownBooking.destination) {
            const looksLegit = /\d/.test(newDestination) || // Has a house number
                              /\b(road|street|avenue|lane|drive|close|way|place|station|airport|hospital|hotel|centre|center)\b/i.test(newDestination) ||
                              newDestination.toLowerCase().includes(knownBooking.destination.toLowerCase().split(' ')[0]); // Contains part of old address
            if (!looksLegit) {
              console.log(`[${callId}] 🛡️ Blocking destination overwrite: verified="${knownBooking.destination}" → suspicious="${newDestination}"`);
              // Don't update
            } else {
              if (newDestination !== knownBooking.destination) {
                knownBooking.destinationVerified = false;
                knownBooking.highFareVerified = false;
              }
              knownBooking.destination = newDestination;
            }
          } else {
            if (newDestination !== knownBooking.destination) {
              knownBooking.destinationVerified = false;
              knownBooking.highFareVerified = false;
            }
            knownBooking.destination = newDestination;
          }
        }
      }
      if (extracted.number_of_passengers) {
        knownBooking.passengers = extracted.number_of_passengers;
      }
      if (extracted.pickup_time) {
        knownBooking.pickupTime = extracted.pickup_time;
        console.log(`[${callId}] ⏰ Pickup time extracted: ${extracted.pickup_time}`);
      }

      // Check if anything changed
      const pickupChanged = before.pickup !== knownBooking.pickup && knownBooking.pickup;
      const destinationChanged = before.destination !== knownBooking.destination && knownBooking.destination;
      const passengersChanged = before.passengers !== knownBooking.passengers && knownBooking.passengers;
      const timeChanged = before.pickupTime !== knownBooking.pickupTime && knownBooking.pickupTime;

      if (pickupChanged || destinationChanged || passengersChanged || timeChanged) {
        console.log(`[${callId}] ✅ Known booking updated via AI extraction:`, knownBooking);
        broadcastLiveCall({
          pickup: knownBooking.pickup,
          destination: knownBooking.destination,
          passengers: knownBooking.passengers
        });

        // GEOCODE NEW ADDRESSES (if geocoding is enabled)
        // Also check for ambiguous addresses (multiple matches) when caller has no history
        if (geocodingEnabled) {
          // Check for ambiguous addresses only if caller has no booking history
          const shouldCheckAmbiguous = callerTotalBookings === 0;
          
          // Geocode pickup if it changed and hasn't been verified yet
          if (pickupChanged && !knownBooking.pickupVerified) {
            // TRUSTED ADDRESS CHECK: Auto-verify if caller has used this pickup before
            const trustedPickup = matchesTrustedAddress(knownBooking.pickup!);
            if (trustedPickup) {
              knownBooking.pickupVerified = true;
              console.log(`[${callId}] 🏠 Pickup auto-verified (trusted): "${knownBooking.pickup}" → "${trustedPickup}"`);
            } else {
              // DUAL-SOURCE GEOCODING: Try both extracted address AND Ada's interpretation
              const extractedPickup = knownBooking.pickup!;
              const adaPickup = extractAddressFromAdaResponse("pickup");
              
              // Try extracted address first
              let pickupResult = await geocodeAddress(extractedPickup, shouldCheckAmbiguous, "pickup");
              let usedAddress = extractedPickup;
              
              // If extracted fails but Ada has a different interpretation, try that
              if (!pickupResult.found && adaPickup && normalize(adaPickup) !== normalize(extractedPickup)) {
                console.log(`[${callId}] 🔄 DUAL-SOURCE: Extracted "${extractedPickup}" failed, trying Ada's interpretation: "${adaPickup}"`);
                
                // Add debug entry to transcript
                transcriptHistory.push({
                  role: "system",
                  text: `🔄 DUAL-SOURCE: Extracted "${extractedPickup}" failed → trying Ada's interpretation "${adaPickup}"`,
                  timestamp: new Date().toISOString()
                });
                broadcastLiveCall({});
                
                const adaResult = await geocodeAddress(adaPickup, shouldCheckAmbiguous, "pickup");
                if (adaResult.found) {
                  pickupResult = adaResult;
                  usedAddress = adaPickup;
                  // Update knownBooking with Ada's corrected version
                  knownBooking.pickup = adaPickup;
                  console.log(`[${callId}] ✅ DUAL-SOURCE: Ada's interpretation "${adaPickup}" succeeded! Updating booking.`);
                  
                  // Add success entry to transcript
                  transcriptHistory.push({
                    role: "system",
                    text: `✅ DUAL-SOURCE SUCCESS: Used Ada's "${adaPickup}" (STT had "${extractedPickup}")`,
                    timestamp: new Date().toISOString()
                  });
                  broadcastLiveCall({});
                }
              }
              
              // If extracted succeeds AND Ada has a different interpretation, also try Ada's
              if (pickupResult.found && adaPickup && normalize(adaPickup) !== normalize(extractedPickup)) {
                console.log(`[${callId}] 🔍 DUAL-SOURCE: Both sources available, checking Ada's version too: "${adaPickup}"`);
                const adaResult = await geocodeAddress(adaPickup, shouldCheckAmbiguous, "pickup");
                if (adaResult.found) {
                  console.log(`[${callId}] ✅ DUAL-SOURCE: Both geocoded! Extracted="${extractedPickup}" Ada="${adaPickup}"`);
                  // If Ada's version is cleaner (has house number, capitalized properly), prefer it
                  if (/^\d/.test(adaPickup) && /^[A-Z]/.test(adaPickup.replace(/^\d+[A-Za-z]?\s+/, ''))) {
                    usedAddress = adaPickup;
                    knownBooking.pickup = adaPickup;
                    pickupResult = adaResult;
                    console.log(`[${callId}] 📝 Using Ada's cleaner version: "${adaPickup}"`);
                  }
                }
              }
              
              if (pickupResult.found) {
                // Check if there are multiple matches and caller has no history AND no other address to use as bias
                const hasLocationContext = knownBooking.destination || callerLastPickup || callerLastDestination;
                if (pickupResult.multiple_matches && pickupResult.multiple_matches.length > 1 && shouldCheckAmbiguous && !hasLocationContext) {
                  console.log(`[${callId}] 🔀 Multiple pickup matches found (no context) - asking for clarification`);
                  askForAddressDisambiguation("pickup", pickupResult.multiple_matches);
                  return; // Wait for customer to clarify
                }
                knownBooking.pickupVerified = true;

                const normalizedPickup = normalize(usedAddress);
                if (geocodeClarificationSent.pickup === normalizedPickup) {
                  geocodeClarificationSent.pickup = undefined;
                  clearGeocodeClarification(
                    "pickup",
                    usedAddress,
                    pickupResult.formatted_address || pickupResult.display_name
                  );
                }

                console.log(`[${callId}] ✅ Pickup verified: ${pickupResult.display_name}`);
              } else {
                // Address not found - ask Ada to request correction
                notifyGeocodeResult("pickup", knownBooking.pickup!, false);
                return; // Don't continue - wait for corrected address
              }
            }
          }
          
          // Geocode destination if it changed and hasn't been verified yet
          if (destinationChanged && !knownBooking.destinationVerified) {
            // TRUSTED ADDRESS CHECK: Auto-verify if caller has used this destination before
            const trustedDestination = matchesTrustedAddress(knownBooking.destination!);
            if (trustedDestination) {
              knownBooking.destinationVerified = true;
              console.log(`[${callId}] 🏠 Destination auto-verified (trusted): "${knownBooking.destination}" → "${trustedDestination}"`);
            } else {
              // DUAL-SOURCE GEOCODING: Try both extracted address AND Ada's interpretation
              const extractedDest = knownBooking.destination!;
              const adaDest = extractAddressFromAdaResponse("destination");
              
              // Try extracted address first
              let destResult = await geocodeAddress(extractedDest, shouldCheckAmbiguous, "destination");
              let usedAddress = extractedDest;
              
              // If extracted fails but Ada has a different interpretation, try that
              if (!destResult.found && adaDest && normalize(adaDest) !== normalize(extractedDest)) {
                console.log(`[${callId}] 🔄 DUAL-SOURCE: Extracted "${extractedDest}" failed, trying Ada's interpretation: "${adaDest}"`);
                
                // Add debug entry to transcript
                transcriptHistory.push({
                  role: "system",
                  text: `🔄 DUAL-SOURCE: Extracted "${extractedDest}" failed → trying Ada's interpretation "${adaDest}"`,
                  timestamp: new Date().toISOString()
                });
                broadcastLiveCall({});
                
                const adaResult = await geocodeAddress(adaDest, shouldCheckAmbiguous, "destination");
                if (adaResult.found) {
                  destResult = adaResult;
                  usedAddress = adaDest;
                  // Update knownBooking with Ada's corrected version
                  knownBooking.destination = adaDest;
                  console.log(`[${callId}] ✅ DUAL-SOURCE: Ada's interpretation "${adaDest}" succeeded! Updating booking.`);
                  
                  // Add success entry to transcript
                  transcriptHistory.push({
                    role: "system",
                    text: `✅ DUAL-SOURCE SUCCESS: Used Ada's "${adaDest}" (STT had "${extractedDest}")`,
                    timestamp: new Date().toISOString()
                  });
                  broadcastLiveCall({});
                }
              }
              
              // If extracted succeeds AND Ada has a different interpretation, also try Ada's
              // If both succeed, prefer the one that matches better (both working = high confidence)
              if (destResult.found && adaDest && normalize(adaDest) !== normalize(extractedDest)) {
                console.log(`[${callId}] 🔍 DUAL-SOURCE: Both sources available, checking Ada's version too: "${adaDest}"`);
                const adaResult = await geocodeAddress(adaDest, shouldCheckAmbiguous, "destination");
                if (adaResult.found) {
                  console.log(`[${callId}] ✅ DUAL-SOURCE: Both geocoded! Extracted="${extractedDest}" Ada="${adaDest}"`);
                  // If Ada's version is cleaner (capitalized properly, etc.), prefer it
                  if (adaDest.length >= extractedDest.length && /^[A-Z]/.test(adaDest)) {
                    usedAddress = adaDest;
                    knownBooking.destination = adaDest;
                    destResult = adaResult;
                    console.log(`[${callId}] 📝 Using Ada's cleaner version: "${adaDest}"`);
                  }
                }
              }
              
              if (destResult.found) {
                // Check if there are multiple matches and no location context to help disambiguate
                const hasLocationContext = knownBooking.pickup || callerLastPickup || callerLastDestination;
                if (destResult.multiple_matches && destResult.multiple_matches.length > 1 && shouldCheckAmbiguous && !hasLocationContext) {
                  console.log(`[${callId}] 🔀 Multiple destination matches found (no context) - asking for clarification`);
                  askForAddressDisambiguation("destination", destResult.multiple_matches);
                  return; // Wait for customer to clarify
                }
                knownBooking.destinationVerified = true;

                const normalizedDestination = normalize(usedAddress);
                if (geocodeClarificationSent.destination === normalizedDestination) {
                  geocodeClarificationSent.destination = undefined;
                  clearGeocodeClarification(
                    "destination",
                    usedAddress,
                    destResult.formatted_address || destResult.display_name
                  );
                }

                console.log(`[${callId}] ✅ Destination verified: ${destResult.display_name}`);
              } else {
                // Address not found - ask Ada to request correction
                notifyGeocodeResult("destination", knownBooking.destination!, false);
                return; // Don't continue - wait for corrected address
              }
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
          if (knownBooking.pickupTime) {
            contextUpdate += `• Confirmed pickup time: ${knownBooking.pickupTime}\n`;
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

    // Time extraction: "now", "asap", "in X minutes", "at 5pm", etc.
    if (/\b(now|asap|straight\s*away|immediately|right\s*now)\b/i.test(t)) {
      knownBooking.pickupTime = "ASAP";
    }
    // "in X minutes/hours"
    const inMinutesMatch = t.match(/\bin\s+(\d+)\s*(minutes?|mins?|hours?|hrs?)\b/i);
    if (inMinutesMatch) {
      const amount = parseInt(inMinutesMatch[1]);
      const unit = inMinutesMatch[2].toLowerCase();
      const now = new Date();
      if (unit.startsWith("h")) {
        now.setHours(now.getHours() + amount);
      } else {
        now.setMinutes(now.getMinutes() + amount);
      }
      const formatted = now.toISOString().slice(0, 16).replace("T", " ");
      knownBooking.pickupTime = formatted;
      console.log(`[${callId}] ⏰ Time extracted (in X): ${formatted}`);
    }

    if (
      before.pickup !== knownBooking.pickup ||
      before.destination !== knownBooking.destination ||
      before.passengers !== knownBooking.passengers ||
      before.pickupTime !== knownBooking.pickupTime
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
                description: "Book a taxi when pickup time, pickup location, destination and number of passengers are all confirmed by the customer",
                parameters: {
                  type: "object",
                  properties: {
                    pickup: { type: "string", description: "Pickup location exactly as customer stated" },
                    destination: { type: "string", description: "Drop-off location exactly as customer stated" },
                    passengers: { type: "integer", description: "Number of passengers" },
                    pickup_time: { type: "string", description: "When the taxi is needed: 'ASAP' for immediate, or 'YYYY-MM-DD HH:MM' format for scheduled bookings" }
                  },
                  required: ["pickup", "destination", "passengers", "pickup_time"]
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
              },
              {
                type: "function",
                name: "cancel_booking",
                description: "Cancel an active booking for the customer. Call this IMMEDIATELY when they say they want to cancel their booking.",
                parameters: {
                  type: "object",
                  properties: {
                    reason: { type: "string", description: "Why the booking is being cancelled: 'customer_request', 'change_plans', etc." }
                  },
                  required: ["reason"]
                }
              },
              {
                type: "function",
                name: "modify_booking",
                description: "Modify an active booking. Use this when the customer wants to change their pickup, destination, or number of passengers without cancelling the entire booking.",
                parameters: {
                  type: "object",
                  properties: {
                    new_pickup: { type: "string", description: "New pickup address (only if customer wants to change it)" },
                    new_destination: { type: "string", description: "New destination address (only if customer wants to change it)" },
                    new_passengers: { type: "integer", description: "New number of passengers (only if customer wants to change it)" }
                  },
                  required: []
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
        
        // If customer has an active booking and we haven't injected the context yet, do it now
        // But DON'T trigger the greeting again - we use greetingSent flag for that
        if (activeBooking && !greetingSent) {
          console.log(`[${callId}] 📋 Injecting active booking context into session...`);
          const activeBookingContext = `

==================================================
ACTIVE BOOKING FOR THIS CUSTOMER (CRITICAL)
==================================================
Booking ID: ${activeBooking.id}
Pickup: ${activeBooking.pickup}
Destination: ${activeBooking.destination}
Passengers: ${activeBooking.passengers}
Fare: ${activeBooking.fare}
Booked at: ${activeBooking.booked_at}

IMPORTANT: This customer has an active booking. When they ask to:
- CANCEL: Call cancel_booking tool IMMEDIATELY - the booking data is already loaded
- MODIFY: Call modify_booking tool with the changes - only include fields being changed
- KEEP: Confirm the booking is still active and ask if they need anything else

You MUST use the cancel_booking or modify_booking tools when requested - they will work because the booking is loaded.
==================================================
`;
          
          // Send updated instructions with active booking context
          // This will trigger another session.updated, but greetingSent will prevent duplicate greetings
          openaiWs?.send(JSON.stringify({
            type: "session.update",
            session: {
              instructions: SYSTEM_INSTRUCTIONS + activeBookingContext
            }
          }));
        }
        
        sessionReady = true;
        socket.send(JSON.stringify({ type: "session_ready" }));

        // Broadcast call started (only once)
        if (!greetingSent) {
          await broadcastLiveCall({ status: "active" });
        }

        // Trigger initial greeting ONLY ONCE
        if (greetingSent) {
          console.log(`[${callId}] ⏭️ Greeting already sent, skipping duplicate`);
          return;
        }
        greetingSent = true;

        // Priority: Active booking > Quick rebooking > Normal greeting
        let greetingPrompt: string;
        
        if (activeBooking) {
          // Customer has an outstanding booking - offer to cancel/keep
          greetingPrompt = `[Call connected - customer has an ACTIVE BOOKING. Their name is ${callerName || 'unknown'}. 
ACTIVE BOOKING DETAILS:
- Booking ID: ${activeBooking.id}
- Pickup: ${activeBooking.pickup}
- Destination: ${activeBooking.destination}
- Passengers: ${activeBooking.passengers}
- Fare: ${activeBooking.fare}

Say EXACTLY: "Hello${callerName ? ` ${callerName}` : ''}! I can see you have an active booking from ${activeBooking.pickup} to ${activeBooking.destination}. Would you like to keep that booking, or would you like to cancel it?"

Then WAIT for the customer to respond. Do NOT cancel until they explicitly say "cancel" or "cancel it".]`;
        } else if (callerName && callerLastDestination) {
          // Returning customer with usual destination - offer quick rebooking
          greetingPrompt = `[Call connected - greet the RETURNING customer by name and OFFER QUICK REBOOKING. Their name is ${callerName}. Their usual destination is ${callerLastDestination}${callerLastPickup ? ` and usual pickup is ${callerLastPickup}` : ''}. Say: "Hello ${callerName}! Lovely to hear from you again. Shall I book you a taxi to ${callerLastDestination}, or are you heading somewhere different today?"]`;
        } else if (callerName) {
          // Returning customer without usual destination
          greetingPrompt = `[Call connected - greet the RETURNING customer by name. Their name is ${callerName}. Say: "Hello ${callerName}! Lovely to hear from you again. How can I help with your travels today?"]`;
        } else {
          // New customer
          greetingPrompt = `[Call connected - greet the NEW customer and ask for their name. Say: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"]`;
        }
        
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
            /transcript by/i,
            /rev\.com/i,
            /transcription\.ca/i,
            /document\s+\w+\.transcription/i,
            /page\s+\d+\s+following/i,
            /msrtn\./i,
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
          
          // Detect nonsense/garbage: random short words with no real meaning
          // "House spread" is unlikely to be a real address if we already have a confirmed one
          const seemsLikeGibberish = (text: string): boolean => {
            const words = text.toLowerCase().split(/\s+/).filter(w => w.length > 0);
            if (words.length < 2 || words.length > 4) return false; // Only check short phrases
            
            // If it's 2-4 random words with no address indicators, could be gibberish
            const addressIndicators = /\d|road|street|avenue|lane|drive|close|way|place|court|station|airport|hospital|hotel|centre|center|park|square|building|house|flat/i;
            if (!addressIndicators.test(text)) {
              // Check if it sounds like a command/action vs. gibberish
              const actionWords = /yes|no|please|cancel|book|taxi|pick|from|to|going|thank|okay|fine|great|right|correct|asap|now|later|three|two|one|four|five|six|passenger|people/i;
              if (!actionWords.test(text)) {
                // Random words with no context
                console.log(`[${callId}] 🚫 Possible gibberish: "${text}" (no address or action indicators)`);
                return true;
              }
            }
            return false;
          };
          
          if (seemsLikeGibberish(t)) {
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

          // CRITICAL FIX: Ada has the full conversation context, so her args should be trusted
          // for the DESTINATION field. The extraction layer can mistakenly pick up stray location
          // mentions (e.g., user saying "Third Street" when Ada asked about passengers).
          //
          // Priority logic:
          // - PICKUP: prefer knownBooking (user's exact words) over Ada's paraphrase
          // - DESTINATION: prefer Ada's args (she confirmed with user) over stray extractions
          // - PASSENGERS: prefer knownBooking (user's exact count)
          // - TIME: prefer knownBooking (user's exact time)
          //
          // This prevents late/out-of-context utterances from overwriting confirmed bookings.
          const finalBooking = {
            pickup: knownBooking.pickup ?? args.pickup,
            destination: args.destination ?? knownBooking.destination, // Ada's confirmed destination takes priority
            passengers: knownBooking.passengers ?? args.passengers,
            pickupTime: knownBooking.pickupTime ?? args.pickup_time ?? "ASAP",
          };

          bookingData = finalBooking;
          console.log(`[${callId}] Booking (final):`, finalBooking);
          
          // Calculate fare and distance using taxi-trip-resolve function
          let distanceMiles = 0;
          let fare = 0;
          let distanceSource = "none";
          let tripResolveResult: any = null;
          
          // If high fare was already verified, use the stored fare - don't recalculate!
          if (knownBooking.highFareVerified && knownBooking.verifiedFare) {
            fare = knownBooking.verifiedFare;
            distanceSource = "verified-cache";
            console.log(`[${callId}] ✅ Using verified fare from cache: £${fare}`);
          }
          
          // Only call trip-resolver if we don't already have a verified fare
          if (fare === 0) {
          try {
            console.log(`[${callId}] 🚕 Calling taxi-trip-resolve for fare calculation...`);
            
            const tripResolveResponse = await fetch(`${SUPABASE_URL}/functions/v1/taxi-trip-resolve`, {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
              },
              body: JSON.stringify({
                pickup_input: finalBooking.pickup,
                dropoff_input: finalBooking.destination,
                caller_city_hint: callerCity || undefined,
                passengers: finalBooking.passengers || 1,
                country: "GB"
              }),
            });
            
            if (tripResolveResponse.ok) {
              tripResolveResult = await tripResolveResponse.json();
              console.log(`[${callId}] 🚕 Trip resolve result:`, JSON.stringify(tripResolveResult, null, 2));
              
              // Check for errors (non-UK addresses, trip too long, etc.)
              if (tripResolveResult.error) {
                console.warn(`[${callId}] ⚠️ Trip resolve error: ${tripResolveResult.error}`);
                
                // Tell Ada about the problem so she can inform the customer
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      error: tripResolveResult.error,
                      message: `Sorry, there's a problem with this booking: ${tripResolveResult.error} Please ask the customer to provide a valid UK address.`
                    })
                  }
                }));
                
                openaiWs?.send(JSON.stringify({
                  type: "response.create",
                  response: { modalities: ["audio", "text"] }
                }));
                
                return; // Don't proceed with booking
              }
              
              if (tripResolveResult.ok) {
                // Use the resolved addresses if available (more accurate geocoding)
                if (tripResolveResult.pickup?.formatted_address) {
                  console.log(`[${callId}] 📍 Pickup resolved: ${tripResolveResult.pickup.formatted_address}`);
                }
                if (tripResolveResult.dropoff?.formatted_address) {
                  console.log(`[${callId}] 📍 Dropoff resolved: ${tripResolveResult.dropoff.formatted_address}`);
                }
                
                // Use trip resolver's distance and fare if available
                if (tripResolveResult.distance) {
                  distanceMiles = tripResolveResult.distance.miles;
                  distanceSource = "trip-resolver";
                  console.log(`[${callId}] 📏 Distance from trip-resolver: ${distanceMiles} miles (${tripResolveResult.distance.duration_text})`);
                }
                
                if (tripResolveResult.fare_estimate) {
                  fare = tripResolveResult.fare_estimate.amount;
                  console.log(`[${callId}] 💷 Fare from trip-resolver: £${fare}`);
                }
                
                // Update city context if inferred
                if (tripResolveResult.inferred_area?.city && !callerCity) {
                  callerCity = tripResolveResult.inferred_area.city;
                  console.log(`[${callId}] 🏙️ City inferred from trip: ${callerCity} (${tripResolveResult.inferred_area.confidence})`);
                }
              }
            } else {
              console.error(`[${callId}] Trip resolve failed: ${tripResolveResponse.status}`);
            }
          } catch (e) {
            console.error(`[${callId}] Trip resolve error:`, e);
          }
          } // End of "if (fare === 0)" block for trip-resolver
          
          // Fallback: Calculate fare manually if trip-resolver didn't return results
          if (fare === 0 && finalBooking.pickup && finalBooking.destination) {
            console.log(`[${callId}] 📏 Trip-resolver didn't return fare, using fallback calculation...`);
            
            const GOOGLE_MAPS_API_KEY = Deno.env.get("GOOGLE_MAPS_API_KEY");
            const BASE_FARE = 3.50;
            const PER_MILE_RATE = 1.80;
            const ROAD_MULTIPLIER = 1.3;
            
            // Haversine formula for straight-line distance
            const haversineDistance = (lat1: number, lon1: number, lat2: number, lon2: number): number => {
              const R = 3958.8; // Earth's radius in miles
              const dLat = (lat2 - lat1) * Math.PI / 180;
              const dLon = (lon2 - lon1) * Math.PI / 180;
              const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
                        Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
                        Math.sin(dLon / 2) * Math.sin(dLon / 2);
              const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
              return R * c;
            };
            
            // Try Google Distance Matrix
            if (GOOGLE_MAPS_API_KEY) {
              try {
                const distanceUrl = `https://maps.googleapis.com/maps/api/distancematrix/json` +
                  `?origins=${encodeURIComponent(finalBooking.pickup + ", UK")}` +
                  `&destinations=${encodeURIComponent(finalBooking.destination + ", UK")}` +
                  `&units=imperial` +
                  `&key=${GOOGLE_MAPS_API_KEY}`;
                
                const distResponse = await fetch(distanceUrl);
                const distData = await distResponse.json();
                
                if (distData.status === "OK" && distData.rows?.[0]?.elements?.[0]?.status === "OK") {
                  const element = distData.rows[0].elements[0];
                  distanceMiles = (element.distance?.value || 0) / 1609.34;
                  distanceSource = "google-fallback";
                  console.log(`[${callId}] 📏 Google fallback: ${distanceMiles.toFixed(2)} miles`);
                }
              } catch (e) {
                console.error(`[${callId}] Google fallback error:`, e);
              }
            }
            
            // Calculate fare from distance
            if (distanceMiles > 0) {
              fare = BASE_FARE + (distanceMiles * PER_MILE_RATE);
              fare = Math.round(fare * 2) / 2; // Round to nearest 50p
              console.log(`[${callId}] 💷 Fallback fare: £${fare}`);
            } else {
              // Final fallback: random estimate
              const isAirport = String(finalBooking.destination || "").toLowerCase().includes("airport");
              fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
              distanceSource = "random";
              console.log(`[${callId}] 💷 Random fallback fare: £${fare}`);
            }
          }
          
          // Add £5 for 6-seater van (5+ passengers)
          const is6Seater = Number(finalBooking.passengers || 0) > 4;
          if (is6Seater) fare += 5;
          
          // HIGH FARE VERIFICATION: If fare exceeds £50 and not yet verified, double-check with customer
          // This helps catch potential address errors that result in unrealistic fares
          const HIGH_FARE_THRESHOLD = 50;
          if (fare > HIGH_FARE_THRESHOLD && distanceSource !== "random" && !knownBooking.highFareVerified) {
            console.log(`[${callId}] ⚠️ High fare detected: £${fare} - requesting verification`);
            
            // Mark as verified so we don't loop forever
            knownBooking.highFareVerified = true;
            
            // Store the calculated fare so we use the SAME value when they confirm
            knownBooking.verifiedFare = fare;
            const verifiedFare = fare;
            
            // Send a verification request back to Ada - DON'T quote another fare, just confirm addresses
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  requires_verification: true,
                  calculated_fare: `£${verifiedFare}`,
                  pickup: finalBooking.pickup,
                  destination: finalBooking.destination,
                  verification_script: `Just to double-check, you're going from ${finalBooking.pickup} to ${finalBooking.destination}? The fare will be £${verifiedFare}. Shall I confirm that booking?`
                })
              }
            }));
            
            // Trigger Ada to verify
            openaiWs?.send(JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] }
            }));
            
            return;
          }
          
          // Format ETA based on pickup time
          const isAsap = !finalBooking.pickupTime || finalBooking.pickupTime === "ASAP";
          const eta = isAsap ? `${Math.floor(Math.random() * 4) + 5} minutes` : null;
          const scheduledTime = !isAsap ? finalBooking.pickupTime : null;
          
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

          // Save booking to bookings table for persistence
          if (userPhone) {
            const phoneKey = normalizePhone(userPhone) || userPhone;
            const { data: newBooking, error: bookingInsertError } = await supabase.from("bookings").insert({
              call_id: callId,
              caller_phone: phoneKey,
              caller_name: callerName || null,
              pickup: finalBooking.pickup,
              destination: finalBooking.destination,
              passengers: finalBooking.passengers,
              fare: `£${fare}`,
              eta: isAsap ? eta : null,
              scheduled_for: scheduledTime,
              status: "active",
              booked_at: new Date().toISOString()
            }).select().single();
            
            if (bookingInsertError) {
              console.error(`[${callId}] Failed to save booking:`, bookingInsertError);
            } else {
              activeBooking = newBooking;
              console.log(`[${callId}] 📋 Booking saved to DB: ${newBooking.id} (${isAsap ? 'ASAP' : `scheduled for ${scheduledTime}`})`);
            }
          }

          // Save/update caller info for future calls
          await saveCallerInfo(finalBooking);

          // Broadcast booking confirmed
          await broadcastLiveCall({
            pickup: finalBooking.pickup,
            destination: finalBooking.destination,
            passengers: finalBooking.passengers,
            booking_confirmed: true,
            fare: `£${fare}`,
            eta: isAsap ? eta : `Scheduled for ${scheduledTime}`
          });
          
          // Build confirmation script based on ASAP vs scheduled
          let confirmationScript: string;
          if (isAsap) {
            confirmationScript = `Brilliant${callerName ? `, ${callerName}` : ""}! That's all booked. The fare is £${fare} and your driver will be with you in ${eta}. Is there anything else I can help you with?`;
          } else {
            // Format scheduled time nicely
            const timeDisplay = scheduledTime || finalBooking.pickupTime;
            confirmationScript = `Brilliant${callerName ? `, ${callerName}` : ""}! That's all booked for ${timeDisplay}. The fare will be £${fare}. Is there anything else I can help you with?`;
          }
          
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
                pickup_time: isAsap ? "ASAP" : scheduledTime,
                fare: `£${fare}`,
                eta: isAsap ? eta : null,
                scheduled_for: scheduledTime,
                confirmation_script: confirmationScript
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
        
        // Handle cancel_booking function
        if (data.name === "cancel_booking") {
          const args = JSON.parse(data.arguments);
          console.log(`[${callId}] 🚫 Cancel booking requested: ${args.reason}`);
          
          // If activeBooking not set but we have phone, try to look it up
          let bookingToCancel = activeBooking;
          if (!bookingToCancel && userPhone) {
            console.log(`[${callId}] 🔍 Looking up active booking for phone: ${userPhone}`);
            const { data: foundBooking, error: lookupError } = await supabase
              .from("bookings")
              .select("id, pickup, destination, passengers, fare, booked_at")
              .eq("caller_phone", userPhone)
              .eq("status", "active")
              .order("booked_at", { ascending: false })
              .limit(1)
              .maybeSingle();
            
            if (lookupError) {
              console.error(`[${callId}] Booking lookup error:`, lookupError);
            } else if (foundBooking) {
              bookingToCancel = foundBooking;
              activeBooking = foundBooking; // Update local state
              console.log(`[${callId}] 📋 Found active booking: ${foundBooking.id}`);
            }
          }
          
          if (bookingToCancel) {
            // Cancel the active booking in database
            const { error: cancelError } = await supabase
              .from("bookings")
              .update({
                status: "cancelled",
                cancelled_at: new Date().toISOString(),
                cancellation_reason: args.reason
              })
              .eq("id", bookingToCancel.id);
            
            if (cancelError) {
              console.error(`[${callId}] Failed to cancel booking:`, cancelError);
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    message: "Sorry, there was an error cancelling the booking. Please try again."
                  })
                }
              }));
            } else {
              console.log(`[${callId}] ✅ Booking ${bookingToCancel.id} cancelled`);
              
              // Clear active booking
              const cancelledBooking = { ...bookingToCancel };
              activeBooking = null;
              
              // Notify client
              socket.send(JSON.stringify({
                type: "booking_cancelled",
                booking: cancelledBooking
              }));
              
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: true,
                    message: "Booking has been cancelled.",
                    cancelled_booking: {
                      pickup: cancelledBooking.pickup,
                      destination: cancelledBooking.destination
                    },
                    next_action: "Ask if they would like to book a new taxi instead."
                  })
                }
              }));
            }
          } else {
            // No active booking to cancel
            console.log(`[${callId}] ⚠️ No active booking found for phone: ${userPhone || 'unknown'}`);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  message: "No active booking found for this customer.",
                  next_action: "Tell the customer you don't see any active bookings for their number and ask if they'd like to book a taxi."
                })
              }
            }));
          }
          
          // Trigger response
          openaiWs?.send(JSON.stringify({
            type: "response.create",
            response: { modalities: ["audio", "text"] }
          }));
        }
        
        // Handle modify_booking function
        if (data.name === "modify_booking") {
          const args = JSON.parse(data.arguments);
          console.log(`[${callId}] ✏️ Modify booking requested:`, args);
          
          // If activeBooking not set but we have phone, try to look it up
          let bookingToModify = activeBooking;
          if (!bookingToModify && userPhone) {
            console.log(`[${callId}] 🔍 Looking up active booking for modification, phone: ${userPhone}`);
            const { data: foundBooking, error: lookupError } = await supabase
              .from("bookings")
              .select("id, pickup, destination, passengers, fare, booked_at")
              .eq("caller_phone", userPhone)
              .eq("status", "active")
              .order("booked_at", { ascending: false })
              .limit(1)
              .maybeSingle();
            
            if (lookupError) {
              console.error(`[${callId}] Booking lookup error:`, lookupError);
            } else if (foundBooking) {
              bookingToModify = foundBooking;
              activeBooking = foundBooking; // Update local state
              console.log(`[${callId}] 📋 Found active booking for modification: ${foundBooking.id}`);
            }
          }
          
          if (bookingToModify) {
            const updates: Record<string, any> = {};
            const changes: string[] = [];
            
            // Apply requested changes
            if (args.new_pickup) {
              updates.pickup = args.new_pickup;
              changes.push(`pickup to "${args.new_pickup}"`);
            }
            if (args.new_destination) {
              updates.destination = args.new_destination;
              changes.push(`destination to "${args.new_destination}"`);
            }
            if (args.new_passengers !== undefined) {
              updates.passengers = args.new_passengers;
              changes.push(`passengers to ${args.new_passengers}`);
            }
            
            if (Object.keys(updates).length === 0) {
              // No actual changes requested
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    message: "No changes specified. Ask the customer what they'd like to change: pickup, destination, or number of passengers?"
                  })
                }
              }));
            } else {
              // Recalculate fare if pickup or destination changed
              let newFare = bookingToModify.fare;
              const finalPickup = updates.pickup || bookingToModify.pickup;
              const finalDestination = updates.destination || bookingToModify.destination;
              
              if (args.new_pickup || args.new_destination) {
                // Recalculate fare
                const GOOGLE_MAPS_API_KEY = Deno.env.get("GOOGLE_MAPS_API_KEY");
                const BASE_FARE = 3.50;
                const PER_MILE_RATE = 1.00;
                let distanceMiles = 0;
                
                if (GOOGLE_MAPS_API_KEY) {
                  try {
                    const distanceUrl = `https://maps.googleapis.com/maps/api/distancematrix/json` +
                      `?origins=${encodeURIComponent(finalPickup + ", UK")}` +
                      `&destinations=${encodeURIComponent(finalDestination + ", UK")}` +
                      `&units=imperial` +
                      `&key=${GOOGLE_MAPS_API_KEY}`;
                    
                    const distResponse = await fetch(distanceUrl);
                    const distData = await distResponse.json();
                    
                    if (distData.status === "OK" && distData.rows?.[0]?.elements?.[0]?.status === "OK") {
                      const element = distData.rows[0].elements[0];
                      const distanceText = element.distance?.text || "";
                      const distanceMatch = distanceText.match(/([\d.]+)\s*mi/);
                      distanceMiles = distanceMatch ? parseFloat(distanceMatch[1]) : (element.distance?.value || 0) / 1609.34;
                    }
                  } catch (e) {
                    console.error(`[${callId}] Distance calculation error:`, e);
                  }
                }
                
                if (distanceMiles > 0) {
                  let fare = BASE_FARE + (distanceMiles * PER_MILE_RATE);
                  const passengers = updates.passengers || bookingToModify.passengers;
                  if (passengers > 4) fare += 5;
                  newFare = `£${Math.round(fare * 100) / 100}`;
                  updates.fare = newFare;
                  changes.push(`fare updated to ${newFare}`);
                }
              }
              
              // Update the booking in database
              const { error: updateError } = await supabase
                .from("bookings")
                .update(updates)
                .eq("id", bookingToModify.id);
              
              if (updateError) {
                console.error(`[${callId}] Failed to modify booking:`, updateError);
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      message: "Sorry, there was an error updating the booking. Please try again."
                    })
                  }
                }));
              } else {
                // Update local activeBooking with modified values
                const updatedBooking = {
                  id: bookingToModify.id,
                  pickup: finalPickup,
                  destination: finalDestination,
                  passengers: updates.passengers || bookingToModify.passengers,
                  fare: newFare,
                  booked_at: bookingToModify.booked_at
                };
                activeBooking = updatedBooking;
                
                console.log(`[${callId}] ✅ Booking modified: ${changes.join(", ")}`);
                
                // Notify client
                socket.send(JSON.stringify({
                  type: "booking_modified",
                  booking: updatedBooking,
                  changes: changes
                }));
                
                openaiWs?.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: true,
                      message: `Booking updated: ${changes.join(", ")}`,
                      updated_booking: {
                        pickup: updatedBooking.pickup,
                        destination: updatedBooking.destination,
                        passengers: updatedBooking.passengers,
                        fare: updatedBooking.fare
                      },
                      confirmation_script: `I've updated your booking. It's now from ${updatedBooking.pickup} to ${updatedBooking.destination} for ${updatedBooking.passengers} passenger${updatedBooking.passengers > 1 ? 's' : ''}, and the fare is ${updatedBooking.fare}. Is there anything else?`
                    })
                  }
                }));
              }
            }
          } else {
            // No active booking to modify
            console.log(`[${callId}] ⚠️ No active booking to modify`);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  message: "No active booking found to modify.",
                  next_action: "Tell the customer you don't see any active bookings. Would they like to book a new taxi?"
                })
              }
            }));
          }
          
          // Trigger response
          openaiWs?.send(JSON.stringify({
            type: "response.create",
            response: { modalities: ["audio", "text"] }
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
          userPhone = normalizePhone(message.user_phone);
          console.log(`[${callId}] User phone: ${userPhone}`);
        }

        // Always load caller profile + active booking when we have a phone number
        if (userPhone && userPhone !== "Unknown") {
          await lookupCaller(userPhone);
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
