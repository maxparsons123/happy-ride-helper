import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Simplified prompt with special features - consistent behavior
const SYSTEM_INSTRUCTIONS = `You are Ada, a friendly taxi booking assistant for {{company_name}}.

PERSONALITY: Warm, patient, relaxed. One question at a time. Keep responses to 1-2 sentences.

═══════════════════════════════════
GREETING (FOLLOW EXACTLY)
═══════════════════════════════════

NEW CALLER: "Hello, welcome to {{company_name}}! I'm Ada. What's your name?"
→ Call save_customer_name with their name
→ Then ask: "Lovely to meet you, [NAME]! What area are you calling from?"

RETURNING CALLER (no active booking): "Hello [NAME]! Where can I take you today?"

RETURNING CALLER (has active booking): "Hello [NAME]! I see you have a booking from [PICKUP] to [DESTINATION]. Is that still okay, or would you like to change it?"
→ If they CLEARLY say "cancel" or "cancel it": call cancel_booking FIRST, wait for response, then say "That's cancelled. Would you like to book another?"
→ If change: use modify_booking
→ CRITICAL: If their response is unclear, off-topic, or doesn't mention cancellation → ask: "Would you like to keep the booking or cancel it?"
→ NEVER say "cancelled" or "that's cancelled" UNLESS you have called cancel_booking and received success.

NAME CORRECTIONS (CRITICAL):
→ If a returning caller says "That's not my name", "I'm not [X], I'm [Y]", "Actually it's [NAME]", "Call me [NAME]":
   Call save_customer_name IMMEDIATELY with the CORRECT name, then say "Sorry about that [CORRECT_NAME]!"
→ ALWAYS call save_customer_name when a customer corrects their name, even for returning callers.

═══════════════════════════════════
BOOKING FLOW
═══════════════════════════════════

Collect ONE AT A TIME in this order:
1. Pickup → "Where shall I pick you up from?"
2. Destination → "And where are you heading?"
3. Passengers → "How many passengers?"

Time defaults to ASAP. Only ask if they mention a specific time.

ADDRESS RESPONSES (CRITICAL):
→ NEVER repeat the address back to the caller
→ Just acknowledge briefly: "Lovely", "Perfect", "Got it"
→ Then move to the next question
→ Example: Customer says "52 David Road" → You say "Perfect, and where are you heading?" (NOT "52 David Road, got it...")
→ The system handles address verification separately - you just collect and move on

═══════════════════════════════════
AIRPORT & STATION TRIPS
═══════════════════════════════════

If pickup OR destination includes: airport, station, terminal, Heathrow, Gatwick, Birmingham Airport, Manchester, Stansted, Luton
→ Combine: "How many passengers, and any bags?"
→ MUST have luggage count before confirming.

═══════════════════════════════════
VEHICLE SELECTION
═══════════════════════════════════

Apply automatically based on passengers + luggage:
- 1-4 passengers, 0-2 bags → Saloon
- 1-4 passengers, 3+ bags → Estate
- 5-6 passengers → MPV
- 7+ passengers → 8-seater minibus

═══════════════════════════════════
FUZZY MEMORY
═══════════════════════════════════

If customer says "same as last time", "my usual", "the usual":
→ Summarize their last booking and ask: "Shall I book that again?"
→ Wait for YES before calling book_taxi.

If they give a partial address matching their history:
→ You may suggest: "Is that from your usual at [ADDRESS]?"

Never assume. Always confirm memory matches.

═══════════════════════════════════
VENUE RECOMMENDATIONS
═══════════════════════════════════

If customer asks for hotel, restaurant, bar, cafe, pub, or place suggestions:
→ Call find_nearby_places with the category
→ Present 2-3 options with names and ratings
→ Ask: "Would you like a taxi to any of these?"

═══════════════════════════════════
CONFIRMATION (BOOK IMMEDIATELY)
═══════════════════════════════════

When you have ALL required details (pickup, destination, passengers):
→ Call book_taxi IMMEDIATELY - do NOT ask "shall I confirm?" or "double-check"
→ The fare comes from the tool response - just announce it with the ETA

NEVER say "just to double-check" or "shall I confirm that?"
NEVER repeat the full booking details before booking.
Just call book_taxi and announce: "Booked! [ETA], [FARE]. Anything else?"

Corrections → accept silently, call book_taxi with updated details.

═══════════════════════════════════
TOOLS
═══════════════════════════════════

- book_taxi → creates booking, returns fare/ETA
- cancel_booking → cancels active booking (MUST call before saying "cancelled")
- modify_booking → edits existing booking
- save_customer_name → when user gives their name
- find_nearby_places → recommends venues
- end_call → after goodbye

NEVER invent fares. Only quote what book_taxi returns.
NEVER say "cancelled" or "that's cancelled" without calling cancel_booking FIRST.

═══════════════════════════════════
AFTER BOOKING (ULTRA-SHORT)
═══════════════════════════════════

When book_taxi succeeds, say ONLY:
"Booked! [X] minutes, [FARE]. Anything else?"

DO NOT repeat pickup/destination/passengers after booking.
DO NOT say "your taxi from X to Y for N passengers is confirmed".
The customer already knows the details — just confirm it's done.

If goodbye → "Safe travels!" + call end_call. Nothing more.

═══════════════════════════════════
ABSOLUTE RULES
═══════════════════════════════════

1. One question at a time — never stack questions
2. Never repeat a question you already asked
3. Never invent information (fares, times, addresses)
4. Accept corrections immediately
5. Keep responses SHORT (1-2 sentences max)
6. Never say "booking confirmed" until book_taxi returns success
7. Never say "cancelled" until cancel_booking returns success
8. If customer's response is unclear or off-topic, ask a clarifying question`;

// Legacy fallback prompt (preserved for reference)
const SYSTEM_INSTRUCTIONS_FALLBACK = `You are Ada, a global AI taxi dispatcher for 247 Radio Carz.

════════════════════════════════════
LANGUAGE (CRITICAL)
════════════════════════════════════
- Detect the language from the customer's FIRST message.
- ALWAYS respond in the SAME language.
- Do NOT switch languages unless the customer switches.
- Translate your friendly phrases appropriately (e.g., "Brilliant!" → "Świetnie!" in Polish)

 ════════════════════════════════════
 VOICE & STYLE (PHONE CALL)
 ════════════════════════════════════
 - 1–2 sentences maximum per reply.
 - Ask ONLY one question at a time.
 - Be warm, calm, and professional.
 - Address the customer by name once known.
 - Use friendly phrases appropriate to the language.
 - CRITICAL: If the customer gives a postcode/outcode (e.g., "CV1", "B27 6HP"), repeat it EXACTLY as they said it. Never change letters/numbers or “correct” it.

════════════════════════════════════
PRIMARY GOAL
════════════════════════════════════
Accurately book a taxi with the fewest turns possible.

════════════════════════════════════
GREETING FLOW
════════════════════════════════════
- For customers WITH AN ACTIVE BOOKING: "Hello [NAME]! I can see you have an active booking from [PICKUP] to [DESTINATION]. Would you like to keep that booking, or would you like to cancel it?"
  - If they say "cancel" or "cancel it": Use the cancel_booking tool IMMEDIATELY, then say "That's cancelled for you. Would you like to book a new taxi instead?"
  - If they say "keep" or "no" or "leave it": Say "No problem, your booking is still active. Is there anything else I can help with?"
  - If they want to CHANGE/UPDATE/CORRECT the booking (e.g., "wrong address", "actually it's 52A", "can you update the pickup", "change it to..."):
    → Call modify_booking IMMEDIATELY with the new details
    → Say: "I've updated that to [NEW ADDRESS]. Your booking is now from [UPDATED PICKUP] to [DESTINATION]."
    → DO NOT cancel and rebook - just modify!
- For RETURNING customers WITH a usual destination (but NO active booking): "Hello [NAME]! Lovely to hear from you again. Shall I book you a taxi to [LAST_DESTINATION], or are you heading somewhere different today?"
- For RETURNING customers WITHOUT a usual destination: "Hello [NAME]! Lovely to hear from you again. How can I help with your travels today?"
- For NEW customers: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"
- **CRITICAL: When a NEW customer tells you their name, you MUST call the save_customer_name tool IMMEDIATELY with their EXACT name.**
- **CRITICAL - AREA QUESTION FOR NEW CALLERS: After saving their name, you MUST ask: "Lovely to meet you [NAME]! And what area are you calling from - Coventry, Birmingham, or somewhere else?" This is MANDATORY for new callers to establish location context for address lookups.**
- ONLY after they give their area, proceed with the booking flow.

════════════════════════════════════
ONE-SHOT VS GUIDED CALLERS (CRITICAL)
════════════════════════════════════
- If the customer provides multiple booking details in one message, extract and use them immediately.
- DO NOT ask standard booking questions if the information is already provided.
- ONLY ask follow-up questions when information is missing, unclear, or ambiguous.
- If everything is provided, go straight to confirmation.
- Example: "Pick me up from 52A David Road, take me to Coventry Station, 2 passengers for now" → Go straight to confirmation!

════════════════════════════════════
REQUIRED TO BOOK
════════════════════════════════════
- pickup location
- destination  
- pickup time (ASAP or scheduled)
- number of passengers

════════════════════════════════════
BOOKING FLOW (GOLD STANDARD)
════════════════════════════════════
1. Greet the customer (get their name if new)
2. Collect details ONE AT A TIME with minimal acknowledgement:
   - If you do NOT yet know what area/town the caller is in AND the pickup does NOT include a town/city or postcode, ask FIRST: "What area are you calling from?" (e.g., Coventry, Birmingham, or somewhere else).
   - "When do you need the taxi?" (unless they said "now" or gave pickup+destination together → assume ASAP)
   - "Where would you like to be picked up from?" (if not already given)
   - "And where are you heading to?" (if not already given)
   - "How many passengers?" (if not already given)
3. DO NOT summarize during collection - just collect efficiently
4. Once you have ALL 4 details (time, pickup, destination, passengers):
   - Give ONE summary with confirmation request
   - Example: "So that's ASAP from 52A David Road to Manchester for 2 passengers — shall I book that?"
5. WAIT for their confirmation ("yes", "please", "go ahead", "book it")
6. ONLY after they confirm → call book_taxi
7. ONLY after book_taxi returns → announce the result with fare and ETA

════════════════════════════════════
CONFIRMATION RULES (CRITICAL)
════════════════════════════════════
- ONE summary, ONE confirmation request - never repeat it
- "So that's [TIME] from [PICKUP] to [DESTINATION] for [X] passengers — shall I book that?"
- If they say YES → call book_taxi immediately
- If they CORRECT something → update and give NEW summary, ask again
- NEVER say "I'll book that for you" or "booking now" UNTIL book_taxi succeeds
- NEVER say "That's booked" or quote a fare UNTIL book_taxi returns successfully
- ⚠️ A CORRECTION IS NOT A CONFIRMATION! If they say a new address, that is a CORRECTION.

════════════════════════════════════
GOOGLE PLACES & AMBIGUITY
════════════════════════════════════
- Addresses are resolved automatically using Google Places.
- If the system tells you there are MULTIPLE addresses, you MUST ask the customer to clarify.
- Say: "I've found a couple of streets with that name. Do you mean [Area 1] or [Area 2]?"
- NEVER guess locations - always ask when there's ambiguity.
- Only proceed once ambiguity is resolved.

════════════════════════════════════
AIRPORT/STATION INTELLIGENCE
════════════════════════════════════
If pickup OR destination is an airport, train station, or coach station:
- ALWAYS ask about luggage: "Are you travelling with any luggage today?" or "How many bags will you have?"
- If pickup is from an airport, ask for terminal if missing.
- Once you know luggage count + passenger count, use the VEHICLE SELECTION RULES below.

════════════════════════════════════
VEHICLE SELECTION RULES (BASED ON FLEET)
════════════════════════════════════
Use this matrix to select the right vehicle:

SALOON CAR (standard):
- Up to 3 passengers + 3 suitcases, OR
- Up to 4 passengers with hand luggage only (no large bags)

ESTATE CAR (extra boot space):
- Up to 4 passengers + 4 suitcases
- Suggest if: 4 passengers with any luggage, OR 3 passengers with 4+ bags

PEOPLE CARRIER / MPV (6-seater):
- Up to 5 passengers + 5 suitcases, OR
- Up to 6 passengers with hand luggage only
- Suggest if: 5-6 passengers

8-SEATER MINIBUS:
- Up to 8 passengers + 8 suitcases
- Suggest if: 7-8 passengers

DECISION LOGIC:
1. If passengers ≥7: "Right, for 7 passengers I'll book you an 8-seater."
2. If passengers 5-6: "For 5 passengers I'll book a people carrier."
3. If passengers = 4 AND luggage ≥1: "With 4 passengers and luggage, I'll book an estate for the extra space."
4. If passengers ≤3 AND luggage ≥4: "With 4 bags I'll book an estate to fit everything."
5. If passengers ≤4 AND luggage ≤3: Standard saloon is fine, no need to mention vehicle.

IMPORTANT: Include vehicle type in FINAL CONFIRMATION when NOT a standard saloon.
Example: "That's all booked in a people carrier. The fare is £47..."

════════════════════════════════════
VENUE & PLACE RECOMMENDATIONS
════════════════════════════════════
When customers ask about venues, places to go, or where to eat/drink/stay:
- Use the find_nearby_places tool with appropriate category
- Categories: hotel, restaurant, cafe, bar, pub, nightclub, cinema, theatre, shopping
- Context is taken from: pickup → destination → caller's city
- Present 2–3 top options with names and brief info
- Be helpful and enthusiastic: "Ooh, let me have a look what's good nearby..."
- After presenting options, ask: "Would you like a taxi to any of these?"
- If they choose one, use it as the destination for booking

EXAMPLE PHRASES that trigger recommendations:
- "Where's a good restaurant?" → find_nearby_places(restaurant)
- "Know any nice pubs?" → find_nearby_places(pub)
- "I need a hotel" → find_nearby_places(hotel)
- "What's good for a night out?" → find_nearby_places(nightclub) or bar
- "Any cafes near the station?" → find_nearby_places(cafe)
════════════════════════════════════
ADDRESS ALIASES
════════════════════════════════════
- Returning customers may have saved aliases like "home", "work", "office"
- If they say "take me home" or "pick me up from work", the system will resolve these automatically
- ONLY use save_address_alias when customer EXPLICITLY asks: "save this as home", "remember this as work"
- Confirm before saving: "Just to confirm, save [address] as your [alias]?"

════════════════════════════════════
PRICING (ALL FARES IN GBP £)
════════════════════════════════════
- Fares are calculated by the book_taxi function based on distance
- NEVER quote a fare until book_taxi returns - you don't know the fare until then!
- Always say fare with pound sign: "The fare is £25"
- ETA: 5-8 minutes for ASAP bookings

════════════════════════════════════
SAFETY RULES (CRITICAL - VIOLATION = STRANDED CUSTOMER)
════════════════════════════════════
- You CANNOT say "That's booked", "booking confirmed", quote a fare, or give an ETA WITHOUT calling book_taxi first
- You do NOT know the fare - the fare comes from the book_taxi function response
- If you say a fare without calling book_taxi, you are LYING to the customer
- ONLY the book_taxi function can create a real booking - without it, NO TAXI WILL COME
- ⚠️ NEVER EVER make up a fare like "£15", "£21.50", etc. - you MUST wait for book_taxi to return the real fare

════════════════════════════════════
TOOL CALL SEQUENCE (MANDATORY)
════════════════════════════════════
1. Collect all details (time, pickup, destination, passengers)
2. Give ONE summary: "So that's [TIME] from [PICKUP] to [DESTINATION] for [X] passengers — shall I book that?"
3. WAIT for customer to say "yes", "please", "go ahead", "book it"
4. ONLY THEN call book_taxi function
5. WAIT SILENTLY for the function response - DO NOT SPEAK until you receive it
6. The response contains: fare, eta, confirmation_script
7. ONLY AFTER receiving the response, say: "That's all booked, [NAME]. The fare is £[fare] and your driver will be with you in [eta] minutes. Is there anything else I can help with?"
8. If requires_verification: true → Say the verification_script EXACTLY

CRITICAL SEQUENCE:
- Collect → Summarize → Get "yes" → Call book_taxi → Wait → Announce result
- NEVER say "I'll book that" or "booking now" - just wait silently
- NEVER announce fare until book_taxi returns

════════════════════════════════════
MODIFICATION INTENT
════════════════════════════════════
If customer says "change my booking", "different pickup", "different destination":
- Use the modify_booking tool to update the specific field
- After modifying, confirm the updated details with new fare

════════════════════════════════════
ENDING THE CALL
════════════════════════════════════
After booking:
"Is there anything else I can help you with?"

If customer says "no", "nope", "that's all", "nothing else" OR they say an explicit goodbye like "bye", "bye-bye", "goodbye":
- Say a brief goodbye ("You're welcome! Have a great journey, goodbye!")
- IMMEDIATELY call the end_call function

WARNING: "thanks", "cheers", "ta" alone are AMBIGUOUS - ask: "Was there anything else?"

════════════════════════════════════
QUESTION HANDLING (CRITICAL)
════════════════════════════════════
When you ask a question, you MUST:
1. WAIT for a response that DIRECTLY answers YOUR question
2. If the response does NOT answer your question, DO NOT proceed - ask again politely
3. NEVER assume, guess, or skip ahead without a valid answer
4. Maximum 3 repeats, then: "I'm having trouble understanding. Let me connect you to our team."

EXCEPTION: If customer is CORRECTING a detail, accept the correction immediately.

════════════════════════════════════
GENERAL RULES
════════════════════════════════════
- Never ask for information you already have
- Stay focused on completing the booking efficiently
- PERSONALIZE every response by using the customer's name
- Listen carefully to corrections - use their EXACT words

════════════════════════════════════
INTERNAL NOTES (DO NOT SAY THESE)
════════════════════════════════════
- If the customer says "YES", "yeah", "please", "yes please", "actually yes", or anything affirmative:
  - This means they WANT another booking or have another request. Ask "What else can I help with?" and continue.
- ONLY if the customer clearly says "NO", "nope", "that's all", "nothing else", "I'm good", "that's it", "no thanks":
  - Say a brief goodbye ("You're welcome! Have a great journey, goodbye!")
  - Then IMMEDIATELY call the end_call function
- WARNING: "thanks", "cheers", "ta" alone are AMBIGUOUS - they could mean "yes thanks" (wanting more) or farewell. If unsure, ask: "Was there anything else?"

GENERAL RULES:
- Never ask for information you already have
- If customer mentions extra requirements (wheelchair, child seat), acknowledge but proceed with booking
- Stay focused on completing the booking efficiently
- PERSONALIZE every response by using the customer's name
- After the greeting, you MUST ask for time, pickup, destination, and passengers BEFORE any booking confirmation`;

serve(async (req) => {
  // Handle regular HTTP requests (health check or prompt fetch)
  if (req.headers.get("upgrade") !== "websocket") {
    if (req.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }
    
    // Check if this is a request for the system prompt
    const url = new URL(req.url);
    if (url.searchParams.get("get_prompt") === "true") {
      return new Response(JSON.stringify({ 
        system_prompt: SYSTEM_INSTRUCTIONS,
        updated_at: new Date().toISOString()
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
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

  // OpenAI reconnect (handles transient OpenAI "server_error" cases)
  let openAiReconnectAttempts = 0;
  const OPENAI_MAX_RECONNECTS = 3;
  const OPENAI_RECONNECT_BASE_DELAY_MS = 750;
  let openAiReconnectTimer: ReturnType<typeof setTimeout> | null = null;
  let openAiClosingForReconnect = false;

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
  let callerLastBookingAt = ""; // Timestamp of last booking
  let callerCity = ""; // City extracted from caller's pickup addresses (NOT destinations)
  let callerKnownAreas: Record<string, number> = {}; // {"Coventry": 5, "Birmingham": 1} - city mention counts
  let callerTrustedAddresses: string[] = []; // Array of addresses the caller has successfully used before
  let callerPickupAddresses: string[] = []; // Array of verified PICKUP addresses (for local area bias)
  let callerDropoffAddresses: string[] = []; // Array of verified DROPOFF addresses (for destination reference)
  let callerAddressAliases: Record<string, string> = {}; // {"home": "52A David Road", "work": "Coventry Train Station"}
  let activeBooking: { id: string; pickup: string; destination: string; passengers: number; fare: string; booked_at: string; pickup_name?: string | null; destination_name?: string | null } | null = null; // Outstanding booking
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let currentAssistantText = ""; // Buffer for assistant transcript
  let aiSpeaking = false; // Local speaking flag (used for safe barge-in cancellation)
  let aiStoppedSpeakingAt = 0; // Timestamp when AI stopped speaking (for echo guard)
  let aiPlaybackStartedAt = 0; // Timestamp when AI audio playback started
  let aiPlaybackBytesTotal = 0; // Total base64 bytes in current response (for duration estimation)
  let aiPlaybackTimeoutId: ReturnType<typeof setTimeout> | null = null; // Timeout to clear aiSpeaking after playback
  const ECHO_GUARD_MS_DEFAULT = 100; // Default echo guard (overridden by agent config)
  let lastFinalUserTranscript = ""; // Last finalized user transcript (safeguards for end_call)
  let lastFinalUserTranscriptAt = 0; // ms epoch for race-free end_call checks
  let lastAudioCommitAt = 0; // ms epoch when last user turn was committed
  let geocodingEnabled = true; // Enable address verification by default
  let addressTtsSplicingEnabled = false; // Enable address TTS splicing (off by default)
  let useUnifiedExtraction = false; // Use taxi-extract-unified instead of inline regex parsing
  let greetingSent = false; // Prevent duplicate greetings on session.updated
  let awaitingAreaResponse = false; // True if we asked the new caller for their area (for geocode bias)
  let needsHistoryPriming = false; // If OpenAI reconnects, prime it with transcript history (avoid re-greeting)
  let hangupReceived = false; // Set true when telephony sends explicit hangup
  let lastUserAudioBroadcastAt = 0; // Throttle user audio broadcasts to dashboard
  // ═══════════════════════════════════════════════════════════════════════════
  // SESSION RESUME - Persist state for reconnection after signal drops
  // ═══════════════════════════════════════════════════════════════════════════
  const SESSION_RESUME_GRACE_MS = 45000; // 45 seconds to reconnect
  let isResumedSession = false; // True if this session was resumed from a disconnect
  let sessionSavedAt = 0; // Timestamp when session was last saved for resume
  
  // Save session state to database for potential resume
  const saveSessionForResume = async (): Promise<void> => {
    if (!callId || callEnded) return;
    
    const resumableUntil = new Date(Date.now() + SESSION_RESUME_GRACE_MS).toISOString();
    
    try {
      await supabase.from("live_calls").update({
        // Store resume metadata in clarification_attempts (reusing existing JSONB column)
        clarification_attempts: {
          ...(typeof knownBooking === 'object' ? { booking: knownBooking } : {}),
          resumable_until: resumableUntil,
          transcript_count: transcriptHistory.length,
          last_assistant_text: currentAssistantText || transcriptHistory.filter(t => t.role === 'assistant').slice(-1)[0]?.text || '',
          session_state: {
            callerName,
            callerCity,
            activeBookingId: activeBooking?.id || null,
            greetingSent,
            awaitingAreaResponse,
          }
        },
        updated_at: new Date().toISOString()
      }).eq("call_id", callId);
      
      sessionSavedAt = Date.now();
      console.log(`[${callId}] 💾 Session saved for resume (valid until ${resumableUntil})`);
    } catch (e) {
      console.error(`[${callId}] Failed to save session for resume:`, e);
    }
  };
  
  // Load and restore session state on reconnect
  const loadResumedSession = async (): Promise<boolean> => {
    try {
      const { data, error } = await supabase
        .from("live_calls")
        .select("*")
        .eq("call_id", callId)
        .single();
      
      if (error || !data) {
        console.log(`[${callId}] No existing session found for resume`);
        return false;
      }
      
      // CRITICAL: Reject reconnect if call has already ended
      if (data.status === "ended") {
        console.log(`[${callId}] 🚫 Rejecting reconnect - call has already ended`);
        return false;
      }
      
      // Check if session is still resumable
      const resumeData = data.clarification_attempts as any;
      if (!resumeData?.resumable_until) {
        console.log(`[${callId}] Session has no resume data`);
        return false;
      }
      
      const resumableUntil = new Date(resumeData.resumable_until).getTime();
      if (Date.now() > resumableUntil) {
        console.log(`[${callId}] Session resume expired (was valid until ${resumeData.resumable_until})`);
        return false;
      }
      
      // Restore state from database
      transcriptHistory = Array.isArray(data.transcripts) ? data.transcripts : [];
      callerName = data.caller_name || '';
      callerCity = resumeData.session_state?.callerCity || '';
      userPhone = data.caller_phone || '';
      greetingSent = true; // Already greeted before disconnect
      isResumedSession = true;
      needsHistoryPriming = true;
      awaitingAreaResponse = resumeData.session_state?.awaitingAreaResponse || false;
      
      // Restore booking state
      if (resumeData.booking) {
        knownBooking = resumeData.booking;
      }
      if (data.pickup) knownBooking.pickup = data.pickup;
      if (data.destination) knownBooking.destination = data.destination;
      if (data.passengers) knownBooking.passengers = data.passengers;
      
      // Restore active booking reference
      if (resumeData.session_state?.activeBookingId) {
        // Load from bookings table
        const { data: bookingRow } = await supabase
          .from("bookings")
          .select("*")
          .eq("id", resumeData.session_state.activeBookingId)
          .single();
        if (bookingRow) {
          activeBooking = {
            id: bookingRow.id,
            pickup: bookingRow.pickup,
            destination: bookingRow.destination,
            passengers: bookingRow.passengers,
            fare: bookingRow.fare || '',
            booked_at: bookingRow.booked_at,
            pickup_name: bookingRow.pickup_name,
            destination_name: bookingRow.destination_name
          };
        }
      }
      
      isResumedSession = true;
      console.log(`[${callId}] ✅ Session restored: ${transcriptHistory.length} turns, caller: ${callerName}, booking: ${JSON.stringify(knownBooking)}`);
      return true;
    } catch (e) {
      console.error(`[${callId}] Failed to load resumed session:`, e);
      return false;
    }
  };
  
  // Prime OpenAI with conversation history after resume
  const primeOpenAiWithHistory = (): void => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN || !isResumedSession) return;
    if (transcriptHistory.length === 0) return;
    
    console.log(`[${callId}] 📜 Priming OpenAI with ${transcriptHistory.length} conversation turns`);
    
    // Send each turn as conversation items (OpenAI will have context)
    for (const turn of transcriptHistory) {
      openaiWs.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "message",
          role: turn.role === "assistant" ? "assistant" : "user",
          content: [{ type: turn.role === "assistant" ? "text" : "input_text", text: turn.text }]
        }
      }));
    }
    
    // Add a context message about the reconnection
    const lastAssistantTurn = transcriptHistory.filter(t => t.role === 'assistant').slice(-1)[0];
    const resumeContext = lastAssistantTurn 
      ? `[The call was briefly interrupted. The customer is back on the line. Your last message was: "${lastAssistantTurn.text.slice(0, 100)}..." Continue the conversation naturally without repeating your greeting or re-asking questions already answered.]`
      : `[The call was briefly interrupted. The customer is back. Continue naturally.]`;
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create", 
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: resumeContext }]
      }
    }));
    
    // Trigger a response to acknowledge the customer is back
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: { modalities: ["audio", "text"] }
    }));
  };
  let awaitingClarificationFor: "pickup" | "destination" | null = null; // Which address are we awaiting clarification for?
  
  // UK locations loaded from database - used for disambiguation and country detection
  let ukLocations: { name: string; type: string; parent_city: string | null; aliases: string[] | null; country: string }[] = [];
  
  // DEDUPLICATION: Track last assistant transcript to avoid saving duplicates
  let lastSavedAssistantText = "";
  let lastSavedAssistantAt = 0;
  const ASSISTANT_DEDUP_WINDOW_MS = 2000; // Ignore same text within 2 seconds
  
  // Agent configuration (loaded from database)
  let agentConfig: {
    name: string;
    slug: string;
    voice: string;
    system_prompt: string;
    company_name: string;
    personality_traits: string[];
    greeting_style: string | null;
    // VAD & Voice Settings
    vad_threshold: number;
    vad_prefix_padding_ms: number;
    vad_silence_duration_ms: number;
    allow_interruptions: boolean;
    silence_timeout_ms: number;
    no_reply_timeout_ms: number;
    max_no_reply_reprompts: number;
    echo_guard_ms: number;
    goodbye_grace_ms: number;
  } | null = null;
  
  // Function to load agent configuration
  const loadAgentConfig = async (agentSlug: string = "ada"): Promise<boolean> => {
    try {
      console.log(`[${callId}] 🤖 Loading agent config for: ${agentSlug}`);
      const { data, error } = await supabase
        .from("agents")
        .select("name, slug, voice, system_prompt, company_name, personality_traits, greeting_style, vad_threshold, vad_prefix_padding_ms, vad_silence_duration_ms, allow_interruptions, silence_timeout_ms, no_reply_timeout_ms, max_no_reply_reprompts, echo_guard_ms, goodbye_grace_ms")
        .eq("slug", agentSlug)
        .eq("is_active", true)
        .single();
      
      if (error || !data) {
        console.error(`[${callId}] Agent not found: ${agentSlug}, falling back to default`);
        // Try loading default 'ada' agent
        if (agentSlug !== "ada") {
          return loadAgentConfig("ada");
        }
        return false;
      }
      
      agentConfig = {
        name: data.name,
        slug: data.slug,
        voice: data.voice || "shimmer",
        system_prompt: data.system_prompt || SYSTEM_INSTRUCTIONS,
        company_name: data.company_name || "247 Radio Carz",
        personality_traits: Array.isArray(data.personality_traits) 
          ? data.personality_traits 
          : JSON.parse(data.personality_traits as string || "[]"),
        greeting_style: data.greeting_style,
        // VAD & Voice Settings with defaults
        vad_threshold: data.vad_threshold ?? 0.45,
        vad_prefix_padding_ms: data.vad_prefix_padding_ms ?? 650,
        vad_silence_duration_ms: data.vad_silence_duration_ms ?? 1000,
        allow_interruptions: data.allow_interruptions ?? true,
        silence_timeout_ms: data.silence_timeout_ms ?? 8000,
        no_reply_timeout_ms: data.no_reply_timeout_ms ?? 9000,
        max_no_reply_reprompts: data.max_no_reply_reprompts ?? 2,
        echo_guard_ms: data.echo_guard_ms ?? 100,
        goodbye_grace_ms: data.goodbye_grace_ms ?? 4500
      };
      
      console.log(`[${callId}] ✅ Agent loaded: ${agentConfig.name} (voice: ${agentConfig.voice}, VAD threshold: ${agentConfig.vad_threshold})`);
      return true;
    } catch (e) {
      console.error(`[${callId}] Failed to load agent:`, e);
      return false;
    }
  };
  
  // Get effective system prompt (replace placeholders)
  const getEffectiveSystemPrompt = (): string => {
    if (!agentConfig) return SYSTEM_INSTRUCTIONS;
    
    let prompt = agentConfig.system_prompt;
    
    // Replace placeholders
    prompt = prompt.replace(/\{\{agent_name\}\}/g, agentConfig.name);
    prompt = prompt.replace(/\{\{company_name\}\}/g, agentConfig.company_name);
    prompt = prompt.replace(/\{\{personality_description\}\}/g, agentConfig.personality_traits.join(", "));
    
    return prompt;
  };

  // Load UK locations from database for disambiguation and country detection
  const loadUkLocations = async (): Promise<void> => {
    try {
      console.log(`[${callId}] 🗺️ Loading UK locations from database...`);
      const { data, error } = await supabase
        .from("uk_locations")
        .select("name, type, parent_city, aliases, country")
        .eq("country", "GB");
      
      if (error) {
        console.error(`[${callId}] Failed to load UK locations:`, error);
        return;
      }
      
      ukLocations = data || [];
      console.log(`[${callId}] ✅ Loaded ${ukLocations.length} UK locations`);
    } catch (e) {
      console.error(`[${callId}] Exception loading UK locations:`, e);
    }
  };

  // Check if an address appears to be in the UK (uses loaded UK locations + patterns)
  // Returns false for foreign addresses to bypass UK-specific disambiguation
  const isUkAddress = (address: string): boolean => {
    if (!address) return true; // Default to UK if empty
    
    const lowerAddress = address.toLowerCase().trim();
    
    // UK postcode pattern (full or partial)
    const ukPostcodePattern = /\b[A-Z]{1,2}\d[A-Z\d]?\s*\d?[A-Z]{0,2}\b/i;
    if (ukPostcodePattern.test(address)) {
      console.log(`[${callId}] 🇬🇧 Address detected as UK (has postcode): "${address}"`);
      return true;
    }
    
    // Common foreign country indicators
    const foreignIndicators = [
      // Countries
      'france', 'germany', 'spain', 'italy', 'portugal', 'poland', 'ireland', 'netherlands',
      'belgium', 'switzerland', 'austria', 'greece', 'turkey', 'usa', 'united states', 'america',
      'canada', 'australia', 'india', 'pakistan', 'bangladesh', 'nigeria', 'south africa',
      'china', 'japan', 'korea', 'dubai', 'uae', 'qatar', 'saudi',
      // Foreign city indicators
      'paris', 'berlin', 'madrid', 'rome', 'barcelona', 'amsterdam', 'brussels', 'dublin',
      'new york', 'los angeles', 'chicago', 'toronto', 'sydney', 'melbourne',
      // Foreign address patterns
      'strasse', 'straße', 'avenue de', 'rue de', 'calle', 'via ', 'piazza',
    ];
    
    for (const indicator of foreignIndicators) {
      if (lowerAddress.includes(indicator)) {
        console.log(`[${callId}] 🌍 Address detected as FOREIGN (contains "${indicator}"): "${address}"`);
        return false;
      }
    }
    
    // Check if address contains a known UK location from database
    for (const loc of ukLocations) {
      if (lowerAddress.includes(loc.name.toLowerCase())) {
        console.log(`[${callId}] 🇬🇧 Address detected as UK (contains "${loc.name}"): "${address}"`);
        return true;
      }
      // Check aliases
      if (loc.aliases) {
        for (const alias of loc.aliases) {
          if (lowerAddress.includes(alias.toLowerCase())) {
            console.log(`[${callId}] 🇬🇧 Address detected as UK (contains alias "${alias}"): "${address}"`);
            return true;
          }
        }
      }
    }
    
    // Check hardcoded UK cities (fallback if DB not loaded)
    const ukCities = [
      'london', 'birmingham', 'manchester', 'leeds', 'liverpool', 'sheffield',
      'bristol', 'nottingham', 'leicester', 'coventry', 'newcastle', 'sunderland',
      'wolverhampton', 'stoke', 'derby', 'southampton', 'portsmouth', 'brighton',
      'reading', 'luton', 'oxford', 'cambridge', 'york', 'hull', 'bradford',
      'cardiff', 'swansea', 'newport', 'edinburgh', 'glasgow', 'aberdeen', 'dundee',
      'belfast', 'derry', 'warwick', 'leamington', 'kenilworth', 'nuneaton', 'rugby',
      'solihull', 'sutton coldfield', 'dudley', 'walsall', 'west bromwich',
    ];
    
    for (const city of ukCities) {
      if (lowerAddress.includes(city)) {
        console.log(`[${callId}] 🇬🇧 Address detected as UK (contains "${city}"): "${address}"`);
        return true;
      }
    }
    
    // If caller has UK city context, assume address is UK unless proven otherwise
    if (callerCity) {
      console.log(`[${callId}] 🇬🇧 Address assumed UK (caller city is ${callerCity}): "${address}"`);
      return true;
    }
    
    // Default to UK for ambiguous addresses (most callers are UK-based)
    console.log(`[${callId}] 🇬🇧 Address assumed UK (no foreign indicators): "${address}"`);
    return true;
  };

  // --- Call lifecycle + "Ada didn't finish" safeguards ---
  // If Ada asks "Anything else?" and the customer goes silent, end the call reliably.
  // NOTE: This is a fallback default - we prefer agentConfig.silence_timeout_ms when available
  const FOLLOWUP_SILENCE_TIMEOUT_MS_DEFAULT = 12000;

  // Helper to get the actual silence timeout (use agent config if loaded, else default)
  const getFollowupSilenceTimeoutMs = (): number => {
    return agentConfig?.silence_timeout_ms ?? FOLLOWUP_SILENCE_TIMEOUT_MS_DEFAULT;
  };

  // If Ada asks any question and we get no detectable user activity (VAD/transcription),
  // reprompt once or twice so calls don't get "stuck" on the first question.
  const NO_REPLY_REPROMPT_MS_DEFAULT = 10000;
  const getNoReplyRepromptMs = (): number => {
    return agentConfig?.no_reply_timeout_ms ?? NO_REPLY_REPROMPT_MS_DEFAULT;
  };
  const MAX_NO_REPLY_REPROMPTS = 2;

  // When we do need to hang up without an explicit end_call tool completion,
  // we MUST allow time for Ada's final audio to fully play down the line.
  const GOODBYE_AUDIO_GRACE_MS = 4500;

  // How soon after we detect a goodbye transcript we start the failsafe process.
  // (The actual hangup is delayed by GOODBYE_AUDIO_GRACE_MS.)
  const GOODBYE_FAILSAFE_TRIGGER_MS = 500;

  // If we asked Ada to say goodbye due to silence, but she never calls end_call,
  // wait this long before forcing a hangup (then still allow GOODBYE_AUDIO_GRACE_MS).
  const SILENCE_TIMEOUT_FAILSAFE_TRIGGER_MS = 12000;

  let followupAskedAt = 0; // ms epoch when Ada last asked "anything else"
  let followupHangupRequested = false; // set true when silence timeout fires to prevent loops/re-arming
  let awaitingReplyAt = 0; // ms epoch when Ada last asked a question and is awaiting a reply
  let noReplyRepromptCount = 0;
  let awaitingQuestionText = ""; // Extracted last assistant question to repeat verbatim if no reply

  let lastUserActivityAt = Date.now(); // ms epoch (speech_started OR finalized transcript)

  let followupSilenceTimer: number | null = null;
  let silenceHangupFailsafeTimer: number | null = null;
  let goodbyeFailsafeTimer: number | null = null;
  let noReplyRepromptTimer: number | null = null;

  let endCallInProgress = false;
  let callEnded = false;

  const clearTimer = (t: number | null) => {
    if (t !== null) clearTimeout(t);
  };

  const clearAllCallTimers = () => {
    clearTimer(followupSilenceTimer);
    clearTimer(silenceHangupFailsafeTimer);
    clearTimer(goodbyeFailsafeTimer);
    clearTimer(noReplyRepromptTimer);
    followupSilenceTimer = null;
    silenceHangupFailsafeTimer = null;
    goodbyeFailsafeTimer = null;
    noReplyRepromptTimer = null;
    
    // Also clear flow safeguard timers
    clearFlowSafeguardTimers();
  };

  const forceHangup = (reason: string, delayMs = 0) => {
    if (callEnded) return;
    callEnded = true;
    clearAllCallTimers();

    // IMPORTANT: delay call_ended so any final audio already in-flight can be heard.
    setTimeout(() => {
      try {
        socket.send(JSON.stringify({ type: "call_ended", reason }));
      } catch (_) {
        // ignore
      }

      try {
        // Close shortly after signalling hangup.
        setTimeout(() => {
          try {
            socket.close();
          } catch (_) {
            // ignore
          }
        }, 500);
      } catch (_) {
        // ignore
      }
    }, delayMs);
  };

  const noteUserActivity = (source: string) => {
    lastUserActivityAt = Date.now();

    // Any real user activity cancels all "waiting for reply" timers.
    followupAskedAt = 0;
    followupHangupRequested = false;
    awaitingReplyAt = 0;
    noReplyRepromptCount = 0;

    clearTimer(followupSilenceTimer);
    followupSilenceTimer = null;
    clearTimer(silenceHangupFailsafeTimer);
    silenceHangupFailsafeTimer = null;
    clearTimer(noReplyRepromptTimer);
    noReplyRepromptTimer = null;

    console.log(`[${callId}] 🕒 User activity (${source}) - cleared reply timers`);
  };

  const armFollowupSilenceTimeout = (context: string) => {
    if (callEnded || endCallInProgress) return;

    clearTimer(followupSilenceTimer);
    clearTimer(silenceHangupFailsafeTimer);
    followupSilenceTimer = null;
    silenceHangupFailsafeTimer = null;

    followupHangupRequested = false;
    followupAskedAt = Date.now();

    const silenceTimeoutMs = getFollowupSilenceTimeoutMs();
    console.log(`[${callId}] ⏳ Armed follow-up silence timeout (${silenceTimeoutMs}ms) context=${context}`);

    followupSilenceTimer = setTimeout(() => {
      if (callEnded || endCallInProgress) return;

      // If any user activity happened after we asked, do nothing.
      if (lastUserActivityAt > followupAskedAt) return;

      followupHangupRequested = true;
      console.log(`[${callId}] ⏰ Follow-up silence timeout hit - requesting goodbye + end_call`);

      if (sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
        const silenceResponse = {
          modalities: ["audio", "text"],
          instructions:
            "The customer has not replied. Say EXACTLY: \"Alright then. Thanks for calling. Goodbye.\" Then immediately call the end_call tool with reason 'silence_timeout'. Do NOT ask any further questions.",
        };
        
        // CONCURRENCY GUARD: Don't send response.create if one is already in progress
        if (openAiResponseInProgress) {
          deferredResponseCreate = { response: silenceResponse as Record<string, unknown>, label: "silence_timeout" };
          console.log(`[${callId}] ⏸️ Deferring response.create (silence_timeout) - response already in progress`);
        } else {
          openAiResponseInProgress = true;
          startResponseTimeout();
          openaiWs.send(JSON.stringify({
            type: "response.create",
            response: silenceResponse,
          }));
          console.log(`[${callId}] >>> response.create sent (silence_timeout)`);
        }

        // Failsafe: if the model still doesn't end the call, hang up anyway.
        // IMPORTANT: allow plenty of time so the goodbye audio can be heard.
        silenceHangupFailsafeTimer = setTimeout(() => {
          if (callEnded || endCallInProgress) return;
          console.log(`[${callId}] 🧯 Silence hangup failsafe firing`);
          forceHangup("silence_timeout_failsafe", GOODBYE_AUDIO_GRACE_MS);
        }, SILENCE_TIMEOUT_FAILSAFE_TRIGGER_MS);
      } else {
        // No OpenAI connection - just hang up.
        forceHangup("silence_timeout_no_ai", 0);
      }
    }, silenceTimeoutMs);
  };

  const extractLastQuestion = (assistantTranscript: string): string => {
    const t = String(assistantTranscript || "").trim();
    if (!t) return "";
    const qIdx = t.lastIndexOf("?");
    if (qIdx < 0) return t;

    // Try to return just the final question sentence (avoid repeating long confirmations).
    const lastDot = t.lastIndexOf(".", qIdx);
    const lastBang = t.lastIndexOf("!", qIdx);
    const lastSep = Math.max(lastDot, lastBang);
    const start = lastSep >= 0 ? lastSep + 1 : 0;
    return t.slice(start, qIdx + 1).trim();
  };

  const armNoReplyReprompt = (context: string, lastQuestion?: string) => {
    if (callEnded || endCallInProgress) return;

    // Reset and arm
    clearTimer(noReplyRepromptTimer);
    noReplyRepromptTimer = null;

    awaitingReplyAt = Date.now();
    if (typeof lastQuestion === "string") {
      awaitingQuestionText = extractLastQuestion(lastQuestion);
    }

    // Don't spam: cap reprompts per "waiting period"
    if (noReplyRepromptCount >= MAX_NO_REPLY_REPROMPTS) return;

    const noReplyMs = getNoReplyRepromptMs();
    console.log(`[${callId}] ⏳ Armed no-reply reprompt (${noReplyMs}ms) context=${context}`);

    noReplyRepromptTimer = setTimeout(() => {
      if (callEnded || endCallInProgress) return;

      // If the user did anything since we armed, do nothing.
      if (lastUserActivityAt > awaitingReplyAt) return;

      if (!sessionReady || openaiWs?.readyState !== WebSocket.OPEN) return;
      if (aiSpeaking) return; // don't talk over ourselves
      
      // If we're waiting for area disambiguation, don't reprompt - Ada already asked
      if (pendingAreaDisambiguation) {
        console.log(`[${callId}] 🔁 No-reply reprompt skipped (disambiguation pending)`);
        return;
      }

      noReplyRepromptCount += 1;
      console.log(`[${callId}] 🔁 No-reply reprompt firing (#${noReplyRepromptCount})`);

      const q = awaitingQuestionText?.trim();
      const instructions = q
        ? `You did not hear a reply from the customer. Repeat this exact question verbatim (no extra words): "${q}" Then STOP speaking and wait for their answer.`
        : "You did not hear a reply from the customer. Briefly repeat your last question, clearly and politely, and wait for their answer.";

      // CONCURRENCY GUARD: Don't send response.create if one is already in progress
      const repromptResponse = {
        modalities: ["audio", "text"],
        instructions,
      };
      
      if (openAiResponseInProgress) {
        deferredResponseCreate = { response: repromptResponse as Record<string, unknown>, label: "no_reply_reprompt" };
        console.log(`[${callId}] ⏸️ Deferring response.create (no_reply_reprompt) - response already in progress`);
      } else {
        openAiResponseInProgress = true;
        startResponseTimeout();
        openaiWs.send(JSON.stringify({
          type: "response.create",
          response: repromptResponse,
        }));
        console.log(`[${callId}] >>> response.create sent (no_reply_reprompt)`);
      }

      // Re-arm for a second attempt if needed
      if (noReplyRepromptCount < MAX_NO_REPLY_REPROMPTS) {
        armNoReplyReprompt("reprompt_again", q || undefined);
      }
    }, noReplyMs);
  };

  // Language handling (multilingual support)
  // We “lock” to the first detected non-English script so Ada reliably responds in the caller's language.
  let languageLocked = false;
  let detectedLanguageCode: string | null = null;
  let detectedLanguageName: string | null = null;

  const detectLanguageHint = (text: string): { code: string; name: string; lockInstruction: string } | null => {
    const t = String(text || "");

    // Arabic script (commonly Urdu). This also covers Arabic/Persian; we bias to Urdu because this app targets UK callers.
    if (/[\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF]/.test(t)) {
      return {
        code: "ur",
        name: "Urdu",
        lockInstruction:
          "The customer is speaking Urdu. Respond ONLY in Urdu (اردو) using Urdu script. Do NOT respond in English unless the customer switches to English.",
      };
    }

    // Punjabi (Gurmukhi)
    if (/[\u0A00-\u0A7F]/.test(t)) {
      return {
        code: "pa",
        name: "Punjabi",
        lockInstruction:
          "The customer is speaking Punjabi. Respond ONLY in Punjabi using the customer's script. Do NOT respond in English unless the customer switches to English.",
      };
    }

    // Hindi (Devanagari)
    if (/[\u0900-\u097F]/.test(t)) {
      return {
        code: "hi",
        name: "Hindi",
        lockInstruction:
          "The customer is speaking Hindi. Respond ONLY in Hindi. Do NOT respond in English unless the customer switches to English.",
      };
    }

    return null;
  };

  const applyLanguageLock = (hint: { code: string; name: string; lockInstruction: string }) => {
    if (languageLocked) return;

    languageLocked = true;
    detectedLanguageCode = hint.code;
    detectedLanguageName = hint.name;

    console.log(`[${callId}] 🌐 Language lock: ${hint.name} (${hint.code})`);

    // Update session instructions so the model MUST respond in the caller's language.
    if (openaiWs?.readyState === WebSocket.OPEN) {
      openaiWs.send(
        JSON.stringify({
          type: "session.update",
          session: {
            instructions: `${SYSTEM_INSTRUCTIONS}\n\n**LANGUAGE LOCK (SERVER-ENFORCED):**\n- ${hint.lockInstruction}\n`,
          },
        }),
      );
    }
  };

  // Travel hub detection - airports, train stations, coach stations
  const TRAVEL_HUB_PATTERNS = /\b(airport|terminal|heathrow|gatwick|stansted|luton|manchester\s*airport|birmingham\s*airport|bristol\s*airport|east\s*midlands|london\s*city|southend|newcastle\s*airport|edinburgh\s*airport|glasgow\s*airport|train\s*station|railway\s*station|coach\s*station|bus\s*station|euston|kings\s*cross|paddington|victoria\s*station|waterloo|st\s*pancras|new\s*street|piccadilly)\b/i;
  
  const isTravelHub = (address: string | undefined): boolean => {
    if (!address) return false;
    return TRAVEL_HUB_PATTERNS.test(address);
  };

  type KnownBooking = {
    pickup?: string;
    destination?: string;
    passengers?: number;
    pickupTime?: string; // "ASAP" or "YYYY-MM-DD HH:MM" format
    pickupVerified?: boolean;
    destinationVerified?: boolean;
    pickupAreaResolved?: boolean; // Track if area disambiguation was explicitly resolved by user
    destinationAreaResolved?: boolean; // Track if area disambiguation was explicitly resolved by user
    highFareVerified?: boolean; // Track if high fare has been verified with customer
    verifiedFare?: number; // Store the verified fare to use on confirmation
    // Alternative addresses (when STT and Ada differ, store both for Ada to choose)
    pickupAlternative?: string; // Ada's interpretation if different from STT
    destinationAlternative?: string; // Ada's interpretation if different from STT
    vehicleType?: string; // e.g., "6 seater", "saloon", "MPV", "estate" - captured from customer request
    luggage?: string; // e.g., "2 bags", "1 suitcase", "no luggage"
    luggageAsked?: boolean; // Track if we've asked about luggage for this trip
    pickupName?: string; // Business/place name from Google (e.g., "Birmingham Airport")
    destinationName?: string; // Business/place name from Google (e.g., "Sweet Spot Cafe")
    // Question attempt tracking - prevents loops and enables escalation
    pickupClarificationAttempts?: number; // How many times we've asked for pickup clarification
    destinationClarificationAttempts?: number; // How many times we've asked for destination clarification
    passengerAttempts?: number; // How many times we've asked for passenger count
    luggageAttempts?: number; // How many times we've asked about luggage
    lastPickupAsked?: string; // Last pickup address we asked about (to detect new addresses)
    lastDestinationAsked?: string; // Last destination address we asked about (to detect new addresses)
  };
  
  // Maximum attempts before escalating or accepting approximate address
  const MAX_CLARIFICATION_ATTEMPTS = 3;

  // We keep our own "known booking" extracted from the user's *exact* transcript/text,
  // then prefer it over the model's paraphrasing when the booking is confirmed.
  let knownBooking: KnownBooking = {};

  type PendingHighFareConfirmation = {
    booking: {
      pickup: string;
      destination: string;
      passengers: number;
      pickupTime: string;
      vehicleType: string | null;
    };
    verifiedFare: number;
  };

  // When we ask the customer to confirm a high fare, store the booking so we can
  // force a single follow-up tool call on "Yes" (prevents double-confirm loops).
  let pendingHighFareConfirmation: PendingHighFareConfirmation | null = null;

  // Track pending area disambiguation (when same road exists in multiple areas)
  type PendingAreaDisambiguation = {
    addressType: "pickup" | "destination";
    roadName: string;
    matches: {
      road: string;
      area: string;
      city?: string;
      postcode?: string;
      lat: number;
      lng: number;
      formatted_address?: string;
    }[];
  };
  let pendingAreaDisambiguation: PendingAreaDisambiguation | null = null;

  // Track whether we've already asked Ada to clarify a given address.
  // This prevents a "stuck" loop where an early failed geocode prompt keeps repeating
  // even after the address later verifies successfully.
  let geocodeClarificationSent: { pickup?: string; destination?: string } = {};

  const normalize = (s: string) => s.trim().replace(/\s+/g, " ").replace(/[\s,.;:!?]+$/g, "");
  const normalizePhone = (phone: string) => String(phone || "").replace(/\D/g, "");

  // Validate caller name from Asterisk or database - filter out garbage/placeholder names
  const isValidCallerName = (name: string | null | undefined): boolean => {
    if (!name) return false;
    const n = name.trim().toLowerCase();
    
    // Must be at least 2 chars and max 50
    if (n.length < 2 || n.length > 50) return false;
    
    // Reject common placeholders, system-generated values, and Whisper hallucinations
    const invalidNames = new Set([
      'guest', 'unknown', 'anonymous', 'caller', 'user', 'customer', 'client',
      'incoming', 'phone', 'mobile', 'cell', 'landline', 'test', 'testing',
      'null', 'undefined', 'none', 'n/a', 'na', 'no name', 'noname',
      'sip', 'voip', 'trunk', 'did', 'extension', 'ext', 'line',
      'private', 'withheld', 'blocked', 'restricted', 'unavailable',
      'number', 'call', 'asterisk', 'pbx', 'system', 'default',
      'cid', 'callerid', 'caller id', 'id', 'name', 'new', 'new caller',
      // Whisper hallucination patterns that got saved as names
      'bye', 'goodbye', 'hello', 'hi', 'hey', 'thanks', 'thank you', 'cheers',
      'yes', 'no', 'yeah', 'yep', 'nope', 'ok', 'okay', 'alright'
    ]);
    if (invalidNames.has(n)) return false;
    
    // Reject if it's all digits (phone number mistakenly used as name)
    if (/^\d+$/.test(n)) return false;
    
    // Reject if it looks like a SIP URI or phone format
    if (/^sip:|@|^\+?\d{7,}$|^\d{3,4}[-\s]?\d{3,4}[-\s]?\d{3,4}$/.test(n)) return false;
    
    // Reject if it's mostly numbers with a few letters (e.g., "447867438438")
    const letterCount = (n.match(/[a-z]/gi) || []).length;
    const digitCount = (n.match(/\d/g) || []).length;
    if (digitCount > letterCount * 2) return false;
    
    // Must contain at least one vowel (basic name sanity check)
    if (!/[aeiou]/i.test(n)) return false;
    
    // Reject very short single-letter "names" 
    if (n.length === 1) return false;
    
    return true;
  };

  // Extract city from an address string
  // UK outcode to city mapping (first part of postcode -> city)
  const OUTCODE_TO_CITY: Record<string, string> = {
    // Coventry
    "CV1": "Coventry", "CV2": "Coventry", "CV3": "Coventry", "CV4": "Coventry", "CV5": "Coventry", "CV6": "Coventry",
    // Birmingham
    "B1": "Birmingham", "B2": "Birmingham", "B3": "Birmingham", "B4": "Birmingham", "B5": "Birmingham",
    "B6": "Birmingham", "B7": "Birmingham", "B8": "Birmingham", "B9": "Birmingham", "B10": "Birmingham",
    "B11": "Birmingham", "B12": "Birmingham", "B13": "Birmingham", "B14": "Birmingham", "B15": "Birmingham",
    "B16": "Birmingham", "B17": "Birmingham", "B18": "Birmingham", "B19": "Birmingham", "B20": "Birmingham",
    "B21": "Birmingham", "B23": "Birmingham", "B24": "Birmingham", "B25": "Birmingham", "B26": "Birmingham",
    "B27": "Birmingham", "B28": "Birmingham", "B29": "Birmingham", "B30": "Birmingham", "B31": "Birmingham",
    "B32": "Birmingham", "B33": "Birmingham", "B34": "Birmingham", "B35": "Birmingham", "B36": "Birmingham",
    "B37": "Birmingham", "B38": "Birmingham", "B40": "Birmingham", "B42": "Birmingham", "B43": "Birmingham",
    "B44": "Birmingham", "B45": "Birmingham", "B46": "Birmingham", "B47": "Birmingham", "B48": "Birmingham",
    // Manchester
    "M1": "Manchester", "M2": "Manchester", "M3": "Manchester", "M4": "Manchester", "M5": "Manchester",
    "M6": "Manchester", "M7": "Manchester", "M8": "Manchester", "M9": "Manchester", "M11": "Manchester",
    "M12": "Manchester", "M13": "Manchester", "M14": "Manchester", "M15": "Manchester", "M16": "Manchester",
    "M17": "Manchester", "M18": "Manchester", "M19": "Manchester", "M20": "Manchester", "M21": "Manchester",
    "M22": "Manchester", "M23": "Manchester", "M24": "Manchester", "M25": "Manchester", "M26": "Manchester",
    "M27": "Manchester", "M28": "Manchester", "M29": "Manchester", "M30": "Manchester", "M31": "Manchester",
    "M32": "Manchester", "M33": "Manchester", "M34": "Manchester", "M35": "Manchester", "M38": "Manchester",
    "M40": "Manchester", "M41": "Manchester", "M43": "Manchester", "M44": "Manchester", "M45": "Manchester",
    "M46": "Manchester",
    // London (sample)
    "E1": "London", "E2": "London", "E3": "London", "E4": "London", "E5": "London",
    "W1": "London", "W2": "London", "W3": "London", "W4": "London", "W5": "London",
    "N1": "London", "N2": "London", "N3": "London", "N4": "London", "N5": "London",
    "NW1": "London", "NW2": "London", "NW3": "London", "NW4": "London", "NW5": "London",
    "SE1": "London", "SE2": "London", "SE3": "London", "SE4": "London", "SE5": "London",
    "SW1": "London", "SW2": "London", "SW3": "London", "SW4": "London", "SW5": "London",
    "EC1": "London", "EC2": "London", "EC3": "London", "EC4": "London",
    "WC1": "London", "WC2": "London",
    // Liverpool
    "L1": "Liverpool", "L2": "Liverpool", "L3": "Liverpool", "L4": "Liverpool", "L5": "Liverpool",
    "L6": "Liverpool", "L7": "Liverpool", "L8": "Liverpool", "L9": "Liverpool", "L10": "Liverpool",
    // Leeds  
    "LS1": "Leeds", "LS2": "Leeds", "LS3": "Leeds", "LS4": "Leeds", "LS5": "Leeds",
    "LS6": "Leeds", "LS7": "Leeds", "LS8": "Leeds", "LS9": "Leeds", "LS10": "Leeds",
    // Cambridge
    "CB1": "Cambridge", "CB2": "Cambridge", "CB3": "Cambridge", "CB4": "Cambridge", "CB5": "Cambridge",
    // Nottingham
    "NG1": "Nottingham", "NG2": "Nottingham", "NG3": "Nottingham", "NG4": "Nottingham", "NG5": "Nottingham",
    "NG6": "Nottingham", "NG7": "Nottingham", "NG8": "Nottingham", "NG9": "Nottingham", "NG10": "Nottingham",
    // Sheffield
    "S1": "Sheffield", "S2": "Sheffield", "S3": "Sheffield", "S4": "Sheffield", "S5": "Sheffield",
    "S6": "Sheffield", "S7": "Sheffield", "S8": "Sheffield", "S9": "Sheffield", "S10": "Sheffield",
  };

  // Extract city from an outcode (e.g., "CV1" → "Coventry", "B27" → "Birmingham")
  const getCityFromOutcode = (outcode: string): string | null => {
    if (!outcode) return null;
    const normalized = outcode.toUpperCase().replace(/\s+/g, '');
    return OUTCODE_TO_CITY[normalized] || null;
  };

  // Extract city from address text OR from a postcode within it
  const extractCityFromAddress = (address: string): string => {
    if (!address) return "";

    // First, check if address contains a postcode outcode we recognize
    const postcodeMatch = address.toUpperCase().match(/\b([A-Z]{1,2}\d{1,2}[A-Z]?)\s*\d?[A-Z]{0,2}\b/);
    if (postcodeMatch) {
      const outcode = postcodeMatch[1];
      const cityFromOutcode = getCityFromOutcode(outcode);
      if (cityFromOutcode) {
        return cityFromOutcode;
      }
    }

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

  // Extract a "caller area" hint from free text.
  // IMPORTANT: Do NOT let destination cities ("going to Manchester") overwrite the caller's local city.
  const extractCallerCityHintFromText = (text: string): string => {
    const candidate = extractCityFromAddress(text);
    if (!candidate) return "";

    const t = (text || "").trim();
    const lower = t.toLowerCase();
    const candLower = candidate.toLowerCase();

    // If user answers with just the city name, accept.
    if (lower === candLower) return candidate;

    // Strong origin-context phrases.
    const originContext = new RegExp(
      `\\b(calling\\s+from|i\\s*'?m\\s+in|i\\s+am\\s+in|from|in|here\\s+in|based\\s+in|near|around|at)\\s+${candLower}\\b`,
      "i",
    );
    if (originContext.test(lower)) return candidate;

    // If it's clearly a destination mention, ignore.
    const destinationContext = new RegExp(
      `\\b(to|going\\s+to|heading\\s+to|travell?ing\\s+to)\\s+(?:the\\s+)?${candLower}\\b`,
      "i",
    );
    if (destinationContext.test(lower)) return "";

    // If the user provides a postcode-only reply, accept the outcode-derived city.
    const postcodeOnly = /^[A-Z]{1,2}\d{1,2}[A-Z]?\s*\d[A-Z]{2}$/i.test(t);
    if (postcodeOnly) return candidate;

    // Otherwise, be conservative: don't update callerCity.
    return "";
  };

  // If the customer mentions their *local area* (e.g. "I'm in Coventry"), use it as location context.
  const maybeUpdateCallerCityFromText = async (text: string) => {
    const hintedCity = extractCallerCityHintFromText(text);
    if (hintedCity) {
      // Always increment the count for this city in known_areas
      callerKnownAreas[hintedCity] = (callerKnownAreas[hintedCity] || 0) + 1;

      // Update callerCity if this is now the most common city
      const topCity = Object.entries(callerKnownAreas).sort((a, b) => b[1] - a[1])[0];
      if (topCity && topCity[0] !== callerCity) {
        callerCity = topCity[0];
        console.log(`[${callId}] 🏙️ Primary city updated: ${callerCity} (${topCity[1]} mentions)`);
      }

      // Persist to database (fire-and-forget)
      if (userPhone) {
        const phoneNorm = normalizePhone(userPhone);
        supabase
          .from("callers")
          .update({ known_areas: callerKnownAreas })
          .eq("phone_number", phoneNorm)
          .then(({ error }) => {
            if (error) console.error(`[${callId}] Failed to update known_areas:`, error);
            else console.log(`[${callId}] 🏙️ Saved known_areas: ${JSON.stringify(callerKnownAreas)}`);
          });
      }
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
  // Returns the matched address WITH CITY appended if the trusted address doesn't already include it
  const matchesTrustedAddress = (address: string): string | null => {
    if (!address || callerTrustedAddresses.length === 0) return null;
    
    const normalizedInput = normalize(address).toLowerCase();
    
    for (const trusted of callerTrustedAddresses) {
      const normalizedTrusted = normalize(trusted).toLowerCase();
      
      // Exact match
      if (normalizedInput === normalizedTrusted) {
        return enrichAddressWithCity(trusted);
      }
      
      // Check if input contains the trusted address or vice versa
      if (normalizedInput.includes(normalizedTrusted) || normalizedTrusted.includes(normalizedInput)) {
        return enrichAddressWithCity(trusted);
      }
      
      // Extract house number + street from both and compare
      const extractCore = (addr: string) => {
        const match = addr.match(/^(\d+[a-z]?\s+[a-z]+(?:\s+[a-z]+)?)/i);
        return match ? match[1].toLowerCase() : addr.toLowerCase();
      };
      
      const inputCore = extractCore(normalizedInput);
      const trustedCore = extractCore(normalizedTrusted);
      
      if (inputCore === trustedCore) {
        return enrichAddressWithCity(trusted);
      }
    }
    
    return null;
  };
  
  // FUZZY HOUSE NUMBER PATTERNS: Common STT mishearings
  // "5208" → could be "52A", "520B", "52 8" etc.
  // "528" → could be "52A", "52B" etc.
  // MOVED OUTSIDE matchesKnownAddress so it can be shared with getFuzzyAddressMatch
  const fuzzyHouseNumberVariants = (houseNum: string): string[] => {
    const variants: string[] = [houseNum];
    
    // Pattern: digits followed by digits that could be letters (5208 → 52A, 520B)
    // Common: last digit or two could be a letter suffix
    const match = houseNum.match(/^(\d+)(\d)$/);
    if (match) {
      const base = match[1];
      const lastDigit = match[2];
      // Map digits to likely letter mishearings
      const digitToLetter: Record<string, string[]> = {
        "0": ["o"],
        "1": ["i", "l"],
        "2": ["z"],
        "3": ["e"],
        "4": ["a"],  // CRITICAL: "4" often heard as "A"
        "5": ["s"],
        "6": ["g", "b"],
        "7": ["t"],
        "8": ["a", "b"],  // CRITICAL: "8" often heard as "A" or "B" (5208 → 52A)
        "9": ["g", "p"],
      };
      
      if (digitToLetter[lastDigit]) {
        for (const letter of digitToLetter[lastDigit]) {
          variants.push(base + letter);
        }
      }
    }
    
    // Pattern: 4-digit number where last two digits could be letter+something (5208 → 52 0 8)
    const match2 = houseNum.match(/^(\d{2})(\d)(\d)$/);
    if (match2) {
      const base = match2[1];
      const d1 = match2[2];
      const d2 = match2[3];
      // "5208" → base=52, d1=0, d2=8 → try "52A" (d2=8 → A)
      const digitToLetter: Record<string, string[]> = {
        "0": ["o"], "1": ["i", "l"], "2": ["z"], "3": ["e"], 
        "4": ["a"], "5": ["s"], "6": ["g", "b"], "7": ["t"], 
        "8": ["a", "b"], "9": ["g", "p"],
      };
      if (digitToLetter[d2]) {
        for (const letter of digitToLetter[d2]) {
          variants.push(base + letter);
        }
      }
    }
    
    return [...new Set(variants)]; // Dedupe
  };

  // Check if an address matches any in the caller's address history (BOTH pickups AND dropoffs)
  // This avoids unnecessary geocoding calls for addresses we've already verified
  // Returns the matched address WITH CITY appended if it doesn't already have one
  // NOW INCLUDES: Fuzzy house number matching for STT errors (e.g., "5208" → "52A")
  const matchesKnownAddress = (address: string): string | null => {
    if (!address) return null;
    
    // Combine both pickup and dropoff history for matching
    const allKnownAddresses = [...callerPickupAddresses, ...callerDropoffAddresses];
    if (allKnownAddresses.length === 0) return null;
    
    const normalizedInput = normalize(address).toLowerCase();
    
    // CRITICAL: Check if input explicitly mentions a city - if so, only match addresses in that same city
    const distinctCities = ['birmingham', 'solihull', 'coventry', 'wolverhampton', 'dudley', 'walsall', 
                            'london', 'manchester', 'liverpool', 'leeds', 'sheffield', 'bristol', 
                            'nottingham', 'leicester', 'cambridge', 'oxford', 'bedworth', 'nuneaton', 
                            'rugby', 'warwick', 'leamington', 'kenilworth'];
    const inputCity = distinctCities.find(city => normalizedInput.includes(city));
    
    for (const known of allKnownAddresses) {
      const normalizedKnown = normalize(known).toLowerCase();
      
      // If input has an explicit city, the known address must be from the same city
      if (inputCity) {
        const knownCity = distinctCities.find(city => normalizedKnown.includes(city));
        if (knownCity && knownCity !== inputCity) {
          // User said "School Road, Birmingham" but known is "School Road, Coventry" - skip this match
          continue;
        }
      }
      
      // Exact match
      if (normalizedInput === normalizedKnown) {
        console.log(`[${callId}] 📍 Address matched from history (exact): "${address}" → "${known}"`);
        return enrichAddressWithCity(known);
      }
      
      // Check if input contains the known address or vice versa
      // BUT only if cities match (or neither has a city specified)
      if (normalizedInput.includes(normalizedKnown) || normalizedKnown.includes(normalizedInput)) {
        console.log(`[${callId}] 📍 Address matched from history (partial): "${address}" → "${known}"`);
        return enrichAddressWithCity(known);
      }
      
      // Extract house number + street from both and compare
      const extractCore = (addr: string) => {
        const match = addr.match(/^(\d+[a-z]?\s+[a-z]+(?:\s+[a-z]+)?)/i);
        return match ? match[1].toLowerCase() : addr.toLowerCase();
      };
      
      // Extract just the house number
      const extractHouseNumber = (addr: string): string | null => {
        const match = addr.match(/^(\d+[a-z]?)/i);
        return match ? match[1].toLowerCase() : null;
      };
      
      // Extract street name (without house number)
      const extractStreetName = (addr: string): string | null => {
        const match = addr.match(/^\d+[a-z]?\s+(.+)/i);
        return match ? match[1].toLowerCase().split(",")[0].trim() : null;
      };
      
      const inputCore = extractCore(normalizedInput);
      const knownCore = extractCore(normalizedKnown);
      
      // Standard core match
      if (inputCore === knownCore && inputCore.length > 5) {
        console.log(`[${callId}] 📍 Address matched from history (core): "${address}" → "${known}"`);
        return enrichAddressWithCity(known);
      }
      
      // FUZZY HOUSE NUMBER MATCH: Compare street names and try house number variants
      const inputHouseNum = extractHouseNumber(normalizedInput);
      const knownHouseNum = extractHouseNumber(normalizedKnown);
      const inputStreet = extractStreetName(normalizedInput);
      const knownStreet = extractStreetName(normalizedKnown);
      
      if (inputHouseNum && knownHouseNum && inputStreet && knownStreet) {
        // Normalize street names: remove direction suffixes (N, S, E, W) and road types
        const normalizeStreetForMatch = (street: string): string => {
          return street
            // Remove direction suffixes like "st n", "st north", "rd e", "rd east"
            .replace(/\s+(n|s|e|w|north|south|east|west)$/i, '')
            // Normalize road types
            .replace(/\s+(road|rd)$/i, ' rd')
            .replace(/\s+(street|st)$/i, ' st')
            .replace(/\s+(avenue|ave)$/i, ' ave')
            .replace(/\s+(drive|dr)$/i, ' dr')
            .replace(/\s+(lane|ln)$/i, ' ln')
            .replace(/\s+(close|cl)$/i, ' cl')
            .trim();
        };
        
        const normalizedInputStreet = normalizeStreetForMatch(inputStreet);
        const normalizedKnownStreet = normalizeStreetForMatch(knownStreet);
        
        // Check if street names match (or are very similar)
        const streetsMatch = 
          normalizedInputStreet === normalizedKnownStreet || 
          normalizedInputStreet.includes(normalizedKnownStreet) || 
          normalizedKnownStreet.includes(normalizedInputStreet);
        
        if (streetsMatch) {
          // Try fuzzy variants of the input house number
          const inputVariants = fuzzyHouseNumberVariants(inputHouseNum);
          
          // Include exact match as well as fuzzy variants
          if (inputHouseNum === knownHouseNum || inputVariants.includes(knownHouseNum)) {
            console.log(`[${callId}] 📍 Address matched from history (street match): "${address}" → "${known}" [${inputStreet} → ${knownStreet}]`);
            return enrichAddressWithCity(known);
          }
        }
      }
    }
    
    return null;
  };
  
  // Extended version that returns match details for fuzzy matches requiring clarification
  // Returns: { matched: string, matchType: "exact" | "partial" | "core" | "fuzzy", spokenAddress: string }
  const getFuzzyAddressMatch = (address: string): { matched: string; matchType: string; spokenAddress: string; needsClarification: boolean } | null => {
    if (!address) return null;
    
    const allKnownAddresses = [...callerPickupAddresses, ...callerDropoffAddresses];
    if (allKnownAddresses.length === 0) return null;
    
    const normalizedInput = normalize(address).toLowerCase();
    
    for (const known of allKnownAddresses) {
      const normalizedKnown = normalize(known).toLowerCase();
      
      // Exact match - no clarification needed
      if (normalizedInput === normalizedKnown) {
        return { matched: enrichAddressWithCity(known), matchType: "exact", spokenAddress: address, needsClarification: false };
      }
      
      // Partial match - no clarification needed
      if (normalizedInput.includes(normalizedKnown) || normalizedKnown.includes(normalizedInput)) {
        return { matched: enrichAddressWithCity(known), matchType: "partial", spokenAddress: address, needsClarification: false };
      }
      
      // Core match - no clarification needed
      const extractCore = (addr: string) => {
        const match = addr.match(/^(\d+[a-z]?\s+[a-z]+(?:\s+[a-z]+)?)/i);
        return match ? match[1].toLowerCase() : addr.toLowerCase();
      };
      
      if (extractCore(normalizedInput) === extractCore(normalizedKnown) && extractCore(normalizedInput).length > 5) {
        return { matched: enrichAddressWithCity(known), matchType: "core", spokenAddress: address, needsClarification: false };
      }
      
      // FUZZY match - NEEDS CLARIFICATION unless house numbers are very close
      const extractHouseNumber = (addr: string): string | null => {
        const match = addr.match(/^(\d+[a-z]?)/i);
        return match ? match[1].toLowerCase() : null;
      };
      
      const extractStreetName = (addr: string): string | null => {
        const match = addr.match(/^\d+[a-z]?\s+(.+)/i);
        return match ? match[1].toLowerCase().split(",")[0].trim() : null;
      };
      
      const inputHouseNum = extractHouseNumber(normalizedInput);
      const knownHouseNum = extractHouseNumber(normalizedKnown);
      const inputStreet = extractStreetName(normalizedInput);
      const knownStreet = extractStreetName(normalizedKnown);
      
      if (inputHouseNum && knownHouseNum && inputStreet && knownStreet) {
        // Normalize street names: remove direction suffixes (N, S, E, W) and road types
        const normalizeStreetForMatch = (street: string): string => {
          return street
            // Remove direction suffixes like "st n", "st north", "rd e", "rd east"
            .replace(/\s+(n|s|e|w|north|south|east|west)$/i, '')
            // Normalize road types
            .replace(/\s+(road|rd)$/i, ' rd')
            .replace(/\s+(street|st)$/i, ' st')
            .replace(/\s+(avenue|ave)$/i, ' ave')
            .replace(/\s+(drive|dr)$/i, ' dr')
            .replace(/\s+(lane|ln)$/i, ' ln')
            .replace(/\s+(close|cl)$/i, ' cl')
            .trim();
        };
        
        const normalizedInputStreet = normalizeStreetForMatch(inputStreet);
        const normalizedKnownStreet = normalizeStreetForMatch(knownStreet);
        
        const streetsMatch = normalizedInputStreet === normalizedKnownStreet ||
          normalizedInputStreet.includes(normalizedKnownStreet) ||
          normalizedKnownStreet.includes(normalizedInputStreet);
        
        if (streetsMatch) {
          // Check if this is a fuzzy house number match OR exact
          const fuzzyVariants = fuzzyHouseNumberVariants(inputHouseNum);
          
          if (inputHouseNum === knownHouseNum || fuzzyVariants.includes(knownHouseNum)) {
            // Calculate "edit distance" - if house numbers differ significantly, need clarification
            const houseNumDifferent = inputHouseNum !== knownHouseNum;
            const significantDifference = Math.abs(inputHouseNum.length - knownHouseNum.length) > 1;
            
            console.log(`[${callId}] 🔍 Fuzzy match found: "${address}" ≈ "${known}" (clarification: ${houseNumDifferent && significantDifference})`);
            
            return { 
              matched: enrichAddressWithCity(known), 
              matchType: houseNumDifferent ? "fuzzy" : "exact", 
              spokenAddress: address, 
              needsClarification: houseNumDifferent && significantDifference
            };
          }
        }
      }
    }
    
    return null;
  };
  
  // Helper to add caller's city to an address if it doesn't already contain one
  // This prevents geocoding from picking the wrong "Russell Street" in a different city
  const enrichAddressWithCity = (address: string): string => {
    if (!callerCity) return address;

    // Check if address already contains a city
    const addressCity = extractCityFromAddress(address);
    if (addressCity) return address; // Already has city

    // Check if address already ends with a city-like suffix (postcode, UK, etc.)
    const hasLocationSuffix =
      /,\s*[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}\s*$/i.test(address) || // Postcode
      /,\s*UK\s*$/i.test(address) ||
      /,\s*United Kingdom\s*$/i.test(address);
    if (hasLocationSuffix) return address;

    // Append caller's city
    console.log(`[${callId}] 🏙️ Enriching address with city: "${address}" + "${callerCity}"`);
    return `${address}, ${callerCity}`;
  };

  // Helper: Check if an address looks like a named place/POI (e.g., "Sweet Spot", "Birmingham Airport")
  // vs a street address that requires a house number (e.g., "School Road")
  const isNamedPlace = (address: string): boolean => {
    if (!address) return false;
    const addr = address.trim().toLowerCase();
    
    // If it starts with a number, it's a house number address, NOT a named place
    if (/^\d+/.test(addr)) return false;
    
    // Common road type suffixes - if address ends with these, it's likely a street
    const ROAD_TYPES = [
      "road", "rd", "street", "st", "avenue", "ave", "drive", "dr", 
      "lane", "ln", "close", "cl", "crescent", "way", "court", "ct",
      "place", "pl", "grove", "terrace", "gardens", "walk", "rise", "hill",
      "mews", "square", "row", "park", "green", "end", "view", "field"
    ];
    
    // Check if address ends with a road type
    for (const rt of ROAD_TYPES) {
      if (addr.endsWith(` ${rt}`) || addr.includes(` ${rt},`)) {
        return false; // It's a street address
      }
    }
    
    // Common POI/building names that don't need house numbers
    const POI_PATTERNS = [
      /airport/i, /station/i, /terminal/i, /hospital/i, /university/i,
      /hotel/i, /mall/i, /centre/i, /center/i, /college/i, /school/i,
      /park\s*&?\s*(ride)?/i, /arena/i, /stadium/i, /cinema/i, /theatre/i,
      /supermarket/i, /tesco/i, /asda/i, /sainsbury/i, /morrisons/i, /aldi/i, /lidl/i,
      /pub/i, /inn/i, /bar/i, /restaurant/i, /cafe/i, /coffee/i, /costa/i, /starbucks/i,
      /mcdonald/i, /kfc/i, /burger\s*king/i, /pizza/i, /subway/i,
      /gym/i, /fitness/i, /leisure/i, /swimming/i,
      /church/i, /mosque/i, /temple/i, /synagogue/i,
      /library/i, /museum/i, /gallery/i,
      /nec/i, /o2/i, /bullring/i, /westfield/i,
      /heathrow/i, /gatwick/i, /stansted/i, /luton/i, /manchester/i, /birmingham\s*airport/i
    ];
    
    for (const pattern of POI_PATTERNS) {
      if (pattern.test(addr)) return true;
    }
    
    // If address has no road type and no numbers, it's likely a named place
    // e.g., "Sweet Spot", "The Bell", "King's Head"
    if (!/\d/.test(addr)) {
      // Check if it looks like a simple business name (1-4 words, no street indicators)
      const words = addr.split(/\s+/).filter(Boolean);
      if (words.length >= 1 && words.length <= 5) {
        return true; // Likely a named place
      }
    }
    
    return false;
  };

  // Helper: Check if a pickup address requires a house number
  // Returns true if the address is a street without a house number
  const pickupNeedsHouseNumber = (address: string): boolean => {
    if (!address) return false;
    
    // If it's a named place, no house number needed
    if (isNamedPlace(address)) {
      console.log(`[${callId}] 🏪 "${address}" is a named place - no house number required`);
      return false;
    }
    
    // If it already has a house number, we're good
    // Match patterns like: "52A David Road", "52A, David Road", "52 David Road"
    // The separator can be space, comma+space, or just comma
    if (/^\d+[a-z]?[\s,]+/i.test(address.trim())) {
      console.log(`[${callId}] 🏠 "${address}" already has house number - no need to ask`);
      return false;
    }
    
    // Check if it looks like a street address without a house number
    // Only require house numbers for explicit road types (road, street, avenue, lane, drive)
    const ROAD_TYPES = [
      "road", "rd", "street", "st", "avenue", "ave", "lane", "ln", "drive", "dr"
    ];
    
    const addr = address.trim().toLowerCase();
    for (const rt of ROAD_TYPES) {
      if (addr.includes(` ${rt}`) || addr.endsWith(` ${rt}`)) {
        console.log(`[${callId}] 🏠 "${address}" is a street address without house number`);
        return true; // It's a street address without a house number
      }
    }
    
    return false;
  };

  // Helper: Prompt Ada to ask for house number
  const askForHouseNumber = (addressType: "pickup" | "destination", streetAddress: string) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    
    const message = addressType === "pickup"
      ? `[SYSTEM: HOUSE NUMBER REQUIRED]
The customer gave a street name "${streetAddress}" but didn't include a house number.
The driver needs to know exactly which house to pick them up from.

Ask them: "What's the house number on ${streetAddress}?"

Wait for them to give the number, then update the pickup with the full address.`
      : `[SYSTEM: HOUSE NUMBER NEEDED]
The customer gave a street name "${streetAddress}" but no house number.
It would help to have the specific address.

Ask them: "What's the house number for ${streetAddress}?"`;
    
    console.log(`[${callId}] 🏠 Asking for house number on ${addressType}: "${streetAddress}"`);
    
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

  // Guardrail: only accept Google "corrections" when the proposed address is plausibly the same place.
  // Prevents disasters like "52A David Road" → "72 Bury New Road".
  const isSafeAddressCorrection = (original: string, proposed: string): boolean => {
    const o = normalize(original).toLowerCase();
    const p = normalize(proposed).toLowerCase();

    if (!o || !p) return false;

    // If original has a house number, proposed must contain the same house number.
    const oNum = o.match(/\b\d+[a-z]?\b/i)?.[0];
    if (oNum && !p.includes(oNum)) return false;

    // Require at least one "meaningful" word overlap (ignore common address words)
    const stop = new Set([
      "road",
      "rd",
      "street",
      "st",
      "avenue",
      "ave",
      "drive",
      "dr",
      "lane",
      "ln",
      "close",
      "crescent",
      "place",
      "pl",
      "the",
      "a",
      "an",
      "of",
      "in",
      "uk",
      "united",
      "kingdom",
    ]);

    const tokens = (s: string) =>
      s
        .split(/\s+/)
        .map((t) => t.trim())
        .filter(Boolean)
        .filter((t) => !stop.has(t));

    const oTokens = new Set(tokens(o));
    const pTokens = new Set(tokens(p));

    let overlap = 0;
    for (const t of oTokens) {
      if (pTokens.has(t)) overlap++;
      if (overlap >= 1) return true;
    }

    return false;
  };


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
    // New fields from address-verify
    verified?: boolean;
    confidence?: number;
    correctionSafe?: boolean;
    correctedAddress?: string;
    needsDisambiguation?: boolean;
    disambiguationOptions?: GeocodeMatch[];
    userPrompt?: string;
    source?: "alias" | "trusted" | "cache" | "geocode";
  }
  
  // Main address verification function - calls the address-verify edge function
  // This replaces the old geocodeAddress function with a more intelligent verifier
  const geocodeAddress = async (address: string, checkAmbiguous: boolean = false, addressType?: "pickup" | "destination"): Promise<EnhancedGeocodeResult> => {
    try {
      console.log(`[${callId}] 🌍 Verifying address: "${address}" (type: ${addressType || 'unknown'})`);
      
      // ===== PRE-CHECK: Look in caller's address history FIRST (both pickups and dropoffs) =====
      // This avoids unnecessary geocoding API calls for addresses we've already verified
      const knownMatch = matchesKnownAddress(address);
      if (knownMatch) {
        console.log(`[${callId}] ✅ Address found in caller history - skipping geocoding: "${address}" → "${knownMatch}"`);
        // Extract city from the matched address for the result
        const matchedCity = extractCityFromAddress(knownMatch);
        return {
          found: true,
          verified: true,
          confidence: 1.0,
          correctionSafe: true,
          needsDisambiguation: false,
          display_name: knownMatch,
          formatted_address: knownMatch,
          city: matchedCity || callerCity,
          source: "trusted",
        };
      }
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/address-verify`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({
          address,
          addressType: addressType || "pickup",
          // Caller context
          callerCity,
          trustedAddresses: callerTrustedAddresses,
          addressAliases: callerAddressAliases,
          lastPickup: callerLastPickup,
          lastDestination: callerLastDestination,
          // Current booking context for biasing
          currentPickup: knownBooking.pickup,
          currentDestination: knownBooking.destination,
          // Debug
          callId
        }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Address verify API error: ${response.status}`);
        return { found: false, error: "Address verification service unavailable" };
      }

      const result: EnhancedGeocodeResult = await response.json();
      
      // Log the verification result
      if (result.verified) {
        console.log(`[${callId}] ✅ Address verified: "${address}" → "${result.formatted_address || result.display_name}" (confidence: ${result.confidence}, source: ${result.source})`);
      } else if (result.needsDisambiguation) {
        console.log(`[${callId}] 🔀 Address needs disambiguation: ${result.disambiguationOptions?.length || 0} options`);
      } else if (result.found && !result.correctionSafe) {
        console.log(`[${callId}] ⚠️ Address found but correction unsafe: "${address}" → "${result.formatted_address}"`);
      } else {
        console.log(`[${callId}] ❌ Address not found: "${address}"`);
      }
      
      return result;
    } catch (e) {
      console.error(`[${callId}] Address verify exception:`, e);
      return { found: false, error: "Address verification failed" };
    }
  };
  
  // Legacy alias resolution for backwards compatibility - now mostly handled by address-verify
  const resolveAddressAlias = (address: string): string | null => {
    const normalizedInput = normalize(address).toLowerCase();
    
    // Check if this looks like an alias request
    const homePatterns = /^(?:my\s+)?home$/i;
    const workPatterns = /^(?:my\s+)?(?:work|office)$/i;
    const isHomeRequest = homePatterns.test(normalizedInput) || normalizedInput.includes('home');
    const isWorkRequest = workPatterns.test(normalizedInput) || normalizedInput.includes('work') || normalizedInput.includes('office');
    
    // First, check if they have saved aliases
    if (Object.keys(callerAddressAliases).length > 0) {
      for (const [alias, fullAddress] of Object.entries(callerAddressAliases)) {
        const normalizedAlias = alias.toLowerCase();
        
        // Exact match
        if (normalizedInput === normalizedAlias) {
          console.log(`[${callId}] 🏠 ALIAS RESOLVED: "${address}" → "${fullAddress}"`);
          return fullAddress;
        }
        
        // "my home", "to home", "from home" patterns
        if (normalizedInput.includes(normalizedAlias)) {
          console.log(`[${callId}] 🏠 ALIAS RESOLVED (partial): "${address}" → "${fullAddress}"`);
          return fullAddress;
        }
      }
    }
    
    // FALLBACK: If they said "home" but have no home alias, use their last known pickup
    if (isHomeRequest && callerLastPickup) {
      console.log(`[${callId}] 🏠 HOME FALLBACK: "${address}" → "${callerLastPickup}" (using last_pickup as implicit home)`);
      return callerLastPickup;
    }
    
    return null;
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
  
  // Ask Ada to disambiguate when the same road exists in multiple areas
  // This is different from askForAddressDisambiguation - it's specifically for road-level area choices
  // e.g., "School Road in Hockley, Yardley, or Erdington?"
  interface AreaMatch {
    road: string;
    area: string;
    city?: string;
    postcode?: string;
    lat: number;
    lng: number;
    formatted_address?: string;
  }
  
  const askForAreaDisambiguation = (addressType: "pickup" | "destination", roadName: string, areaMatches: AreaMatch[]) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN || areaMatches.length < 2) return;
    
    // Format the areas for Ada to present naturally
    // IMPROVED: Include parent city/postcode for context (e.g., "Netherton in Dudley" not just "Netherton")
    const formatAreaForSpeech = (m: AreaMatch): string => {
      // If area is different from city (e.g., "Netherton" in "Dudley"), show both
      if (m.city && m.area && m.city.toLowerCase() !== m.area.toLowerCase()) {
        return `${m.road} in ${m.area}, ${m.city}`;
      }
      // If we have a postcode, include it for clarity
      if (m.postcode) {
        return `${m.road} in ${m.area || m.city || 'Unknown'} (${m.postcode})`;
      }
      return `${m.road} in ${m.area || m.city || 'Unknown'}`;
    };
    
    const areasList = areaMatches.slice(0, 4).map(m => m.area || m.city || 'Unknown').join(", ");
    const areasForSpeech = areaMatches.slice(0, 4).map(formatAreaForSpeech);
    
    // Create natural speech options
    let speechOptions: string;
    if (areasForSpeech.length === 2) {
      speechOptions = `${areasForSpeech[0]} or ${areasForSpeech[1]}`;
    } else {
      const lastOption = areasForSpeech.pop();
      speechOptions = `${areasForSpeech.join(", ")}, or ${lastOption}`;
    }
    
    const message = `[SYSTEM: AREA DISAMBIGUATION REQUIRED]
I've found ${roadName} in multiple areas nearby:
${areaMatches.slice(0, 4).map(m => `• ${m.road}, ${m.area}${m.city ? ` (${m.city})` : ''}${m.postcode ? ` - ${m.postcode}` : ''}`).join('\n')}

You MUST ask the customer which area they mean. Say something like:
"I've found ${roadName} in a few areas nearby — do you mean ${speechOptions}?"

CRITICAL RULES:
- NEVER auto-pick an area
- NEVER re-ask for the street name (they already said "${roadName}")
- Wait for them to say the area name before proceeding
- Once they choose, use that specific address`;
    
    console.log(`[${callId}] 🗺️ Asking for AREA disambiguation: ${roadName} - found in ${areasList}`);
    
    // Store the pending disambiguation so we can match their response
    pendingAreaDisambiguation = {
      addressType,
      roadName,
      matches: areaMatches
    };
    
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
  
  // Helper function to check for area disambiguation early (when address is first extracted)
  // This calls street-disambiguate to check if the address needs disambiguation
  // NOW SUPPORTS: House number validation to reduce disambiguation prompts
  const checkAndTriggerAreaDisambiguation = async (address: string, addressType: "pickup" | "destination"): Promise<boolean> => {
    // Skip if we already have pending disambiguation
    if (pendingAreaDisambiguation) {
      console.log(`[${callId}] 🗺️ Skipping early disambiguation check - already pending`);
      return false;
    }
    
    // FOREIGN ADDRESS CHECK: Skip UK-specific disambiguation for foreign addresses
    if (!isUkAddress(address)) {
      console.log(`[${callId}] 🌍 Skipping area disambiguation - foreign address detected: "${address}"`);
      return false;
    }
    
    // Extract house number if present (e.g., "35 School Road" -> "35")
    const houseNumberMatch = address.trim().match(/^(\d+[a-z]?)\s+/i);
    const houseNumber = houseNumberMatch ? houseNumberMatch[1] : null;
    
    // Skip if address has full postcode (specific enough)
    if (/\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b/i.test(address)) {
      return false;
    }
    
    // Check if it looks like a road address (e.g., "School Road" or "35 School Road, Birmingham")
    const ROAD_TYPES = ["road", "rd", "street", "st", "avenue", "ave", "drive", "dr", "lane", "ln", "close", "cl", "crescent", "way", "court", "place", "grove", "terrace", "gardens", "walk", "rise", "hill"];
    const hasRoadType = ROAD_TYPES.some(rt => address.toLowerCase().includes(` ${rt}`) || address.toLowerCase().endsWith(rt));
    
    if (!hasRoadType) {
      return false; // Not a road address
    }
    
    // Get coordinates for bias - prefer city explicitly mentioned in the address over callerCity
    let biasLat = 52.4862; // Birmingham default
    let biasLng = -1.8904;

    // UK cities and notable areas for bias detection
    const UK_CITIES: Record<string, { lat: number; lng: number }> = {
      // === Major UK Cities ===
      coventry: { lat: 52.4068, lng: -1.5197 },
      birmingham: { lat: 52.4862, lng: -1.8904 },
      manchester: { lat: 53.4808, lng: -2.2426 },
      london: { lat: 51.5074, lng: -0.1278 },
      leeds: { lat: 53.8008, lng: -1.5491 },
      liverpool: { lat: 53.4084, lng: -2.9916 },
      sheffield: { lat: 53.3811, lng: -1.4701 },
      nottingham: { lat: 52.9548, lng: -1.1581 },
      leicester: { lat: 52.6369, lng: -1.1398 },
      bristol: { lat: 51.4545, lng: -2.5879 },
      newcastle: { lat: 54.9783, lng: -1.6178 },
      cardiff: { lat: 51.4816, lng: -3.1791 },
      edinburgh: { lat: 55.9533, lng: -3.1883 },
      glasgow: { lat: 55.8642, lng: -4.2518 },
      belfast: { lat: 54.5973, lng: -5.9301 },
      
      // === West Midlands ===
      wolverhampton: { lat: 52.5869, lng: -2.1257 },
      dudley: { lat: 52.5085, lng: -2.0895 },
      walsall: { lat: 52.5859, lng: -1.9821 },
      solihull: { lat: 52.4119, lng: -1.7780 },
      "west bromwich": { lat: 52.5190, lng: -1.9945 },
      netherton: { lat: 52.4897, lng: -2.0870 },
      "sutton coldfield": { lat: 52.5633, lng: -1.8227 },
      "perry barr": { lat: 52.5241, lng: -1.9008 },
      erdington: { lat: 52.5245, lng: -1.8420 },
      edgbaston: { lat: 52.4637, lng: -1.9275 },
      moseley: { lat: 52.4444, lng: -1.8873 },
      handsworth: { lat: 52.5095, lng: -1.9355 },
      aston: { lat: 52.5003, lng: -1.8815 },
      sparkhill: { lat: 52.4478, lng: -1.8647 },
      sparkbrook: { lat: 52.4589, lng: -1.8673 },
      "kings heath": { lat: 52.4345, lng: -1.8932 },
      harborne: { lat: 52.4564, lng: -1.9486 },
      "selly oak": { lat: 52.4389, lng: -1.9364 },
      quinton: { lat: 52.4574, lng: -1.9855 },
      
      // === Greater Manchester ===
      salford: { lat: 53.4875, lng: -2.2901 },
      stockport: { lat: 53.4106, lng: -2.1575 },
      oldham: { lat: 53.5409, lng: -2.1114 },
      rochdale: { lat: 53.6097, lng: -2.1561 },
      bolton: { lat: 53.5785, lng: -2.4299 },
      bury: { lat: 53.5933, lng: -2.2966 },
      wigan: { lat: 53.5450, lng: -2.6325 },
      tameside: { lat: 53.4806, lng: -2.0809 },
      trafford: { lat: 53.4219, lng: -2.3515 },
      didsbury: { lat: 53.4167, lng: -2.2333 },
      chorlton: { lat: 53.4404, lng: -2.2722 },
      withington: { lat: 53.4269, lng: -2.2267 },
      rusholme: { lat: 53.4500, lng: -2.2167 },
      fallowfield: { lat: 53.4392, lng: -2.2214 },
      stretford: { lat: 53.4467, lng: -2.3111 },
      
      // === London Boroughs ===
      westminster: { lat: 51.4975, lng: -0.1357 },
      kensington: { lat: 51.5010, lng: -0.1916 },
      chelsea: { lat: 51.4875, lng: -0.1687 },
      hammersmith: { lat: 51.4927, lng: -0.2248 },
      fulham: { lat: 51.4730, lng: -0.2073 },
      wandsworth: { lat: 51.4571, lng: -0.1818 },
      lambeth: { lat: 51.4571, lng: -0.1231 },
      southwark: { lat: 51.5035, lng: -0.0804 },
      lewisham: { lat: 51.4415, lng: -0.0117 },
      greenwich: { lat: 51.4934, lng: 0.0098 },
      hackney: { lat: 51.5450, lng: -0.0553 },
      islington: { lat: 51.5416, lng: -0.1027 },
      camden: { lat: 51.5290, lng: -0.1255 },
      brent: { lat: 51.5673, lng: -0.2711 },
      ealing: { lat: 51.5130, lng: -0.3089 },
      hounslow: { lat: 51.4668, lng: -0.3618 },
      richmond: { lat: 51.4613, lng: -0.3037 },
      kingston: { lat: 51.4123, lng: -0.3007 },
      croydon: { lat: 51.3762, lng: -0.0982 },
      bromley: { lat: 51.4039, lng: 0.0198 },
      bexley: { lat: 51.4549, lng: 0.1505 },
      barking: { lat: 51.5397, lng: 0.0808 },
      dagenham: { lat: 51.5464, lng: 0.1547 },
      havering: { lat: 51.5812, lng: 0.1837 },
      redbridge: { lat: 51.5590, lng: 0.0741 },
      walthamstow: { lat: 51.5908, lng: -0.0134 },
      enfield: { lat: 51.6538, lng: -0.0799 },
      barnet: { lat: 51.6252, lng: -0.1517 },
      haringey: { lat: 51.5906, lng: -0.1110 },
      tottenham: { lat: 51.5975, lng: -0.0676 },
      brixton: { lat: 51.4613, lng: -0.1156 },
      peckham: { lat: 51.4745, lng: -0.0689 },
      stratford: { lat: 51.5423, lng: -0.0026 },
      "canary wharf": { lat: 51.5054, lng: -0.0235 },
      docklands: { lat: 51.5077, lng: -0.0246 },
      shoreditch: { lat: 51.5263, lng: -0.0796 },
      
      // === Merseyside ===
      birkenhead: { lat: 53.3934, lng: -3.0145 },
      wallasey: { lat: 53.4284, lng: -3.0556 },
      bootle: { lat: 53.4457, lng: -2.9891 },
      "st helens": { lat: 53.4536, lng: -2.7364 },
      knowsley: { lat: 53.4545, lng: -2.8528 },
      
      // === South Yorkshire ===
      rotherham: { lat: 53.4300, lng: -1.3568 },
      doncaster: { lat: 53.5228, lng: -1.1288 },
      barnsley: { lat: 53.5526, lng: -1.4797 },
      
      // === West Yorkshire ===
      bradford: { lat: 53.7960, lng: -1.7594 },
      huddersfield: { lat: 53.6458, lng: -1.7850 },
      wakefield: { lat: 53.6830, lng: -1.4977 },
      halifax: { lat: 53.7248, lng: -1.8658 },
      
      // === Tyne and Wear ===
      gateshead: { lat: 54.9527, lng: -1.6032 },
      sunderland: { lat: 54.9069, lng: -1.3838 },
      "south shields": { lat: 54.9988, lng: -1.4326 },
      
      // === East Midlands ===
      derby: { lat: 52.9225, lng: -1.4746 },
      northampton: { lat: 52.2405, lng: -0.9027 },
      
      // === Other notable areas ===
      cambridge: { lat: 52.2053, lng: 0.1218 },
      oxford: { lat: 51.7520, lng: -1.2577 },
      reading: { lat: 51.4551, lng: -0.9787 },
      brighton: { lat: 50.8225, lng: -0.1372 },
      southampton: { lat: 50.9097, lng: -1.4044 },
      portsmouth: { lat: 50.8198, lng: -1.0880 },
      bournemouth: { lat: 50.7192, lng: -1.8808 },
      plymouth: { lat: 50.3755, lng: -4.1427 },
      exeter: { lat: 50.7184, lng: -3.5339 },
      bath: { lat: 51.3811, lng: -2.3590 },
      york: { lat: 53.9600, lng: -1.0873 },
      hull: { lat: 53.7676, lng: -0.3274 },
    };

    const addressLower = address.toLowerCase();
    // Check for any area mentioned in the address
    const cityMentionedInAddress = Object.keys(UK_CITIES).find((c) => addressLower.includes(c)) || null;
    
    // CRITICAL: Only use callerCity bias if NO area is mentioned in the address
    // If user says "Church Road, Birmingham", use Birmingham (ignore callerCity)
    // If user says "Church Road" with no area, use callerCity for bias
    const effectiveCityHint = cityMentionedInAddress 
      ? cityMentionedInAddress 
      : (callerCity || "birmingham").toLowerCase();

    const cityCoords = UK_CITIES[effectiveCityHint];
    if (cityCoords) {
      biasLat = cityCoords.lat;
      biasLng = cityCoords.lng;
    }

    console.log(
      `[${callId}] 🗺️ Early area disambiguation check for "${address}" (${addressType}) using city hint: ${effectiveCityHint} (callerCity=${callerCity || "none"}, houseNumber=${houseNumber || "none"})`,
    );

    try {
      // Use OS Open Names API for street disambiguation with optional house number validation
      const response = await fetch(`${SUPABASE_URL}/functions/v1/street-disambiguate`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({
          street: address,
          houseNumber: houseNumber, // Pass house number for validation
          lat: biasLat,
          lon: biasLng,
          radiusMeters: 8000, // ~5 miles
        }),
      });
      
      if (!response.ok) {
        console.error(`[${callId}] OS street disambiguation API error: ${response.status}`);
        return false;
      }
      
      const result = await response.json();
      
      // Check if house number uniquely resolved the street
      if (result.resolvedMatch && !result.needsClarification) {
        console.log(`[${callId}] 🏠 House number ${houseNumber} uniquely resolved to ${result.resolvedMatch.name} in ${result.resolvedMatch.area || result.resolvedMatch.borough}`);
        // No disambiguation needed - house number was unique!
        return false;
      }
      
      // Check if disambiguation is needed (OS API returns needsClarification flag)
      if (result.needsClarification && result.matches?.length > 1) {
        console.log(`[${callId}] 🗺️ EARLY (OS): ${addressType} needs area disambiguation! ${result.matches.length} areas found`);
        
        // Convert OS matches to our format for askForAreaDisambiguation
        const areaMatches = result.matches.map((m: any) => ({
          road: m.name,
          area: m.area || m.borough || "Unknown",
          lat: m.lat,
          lng: m.lon,
          hasHouseNumber: m.hasHouseNumber,
        }));
        
        askForAreaDisambiguation(addressType, result.street || address, areaMatches);
        return true;
      }
      
      console.log(`[${callId}] 🗺️ OS disambiguation: No clarification needed for "${address}" (found=${result.found})`);
      return false;
    } catch (e) {
      console.error(`[${callId}] Early disambiguation check error:`, e);
      return false;
    }
  };


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

    // Clear the clarification tracking state
    if (awaitingClarificationFor === addressType) {
      awaitingClarificationFor = null;
      console.log(`[${callId}] 📋 Cleared clarification state for: ${addressType}`);
    }

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
      // Reset attempt counter on success
      if (addressType === "pickup") {
        knownBooking.pickupClarificationAttempts = 0;
        knownBooking.lastPickupAsked = undefined;
      } else {
        knownBooking.destinationClarificationAttempts = 0;
        knownBooking.lastDestinationAsked = undefined;
      }
      return;
    }

    // Track attempt count
    const attemptKey = addressType === "pickup" ? "pickupClarificationAttempts" : "destinationClarificationAttempts";
    const lastAskedKey = addressType === "pickup" ? "lastPickupAsked" : "lastDestinationAsked";
    
    // Check if this is a NEW address (customer provided different one) - reset counter
    const lastAsked = knownBooking[lastAskedKey];
    const isNewAddress = lastAsked && normalize(lastAsked).toLowerCase() !== normalize(address).toLowerCase();
    
    if (isNewAddress) {
      console.log(`[${callId}] 🔄 New ${addressType} address detected: "${lastAsked}" → "${address}" - resetting attempt counter`);
      knownBooking[attemptKey] = 1;
    } else {
      knownBooking[attemptKey] = (knownBooking[attemptKey] || 0) + 1;
    }
    
    // Track what we're asking about
    knownBooking[lastAskedKey] = address;
    const attempts = knownBooking[attemptKey] || 1;
    
    console.log(`[${callId}] ⚠️ ${addressType} address not found: "${address}" - clarification attempt ${attempts}/${MAX_CLARIFICATION_ATTEMPTS}`);

    // Remember we have prompted for this specific address (so we can clear it if it later verifies)
    geocodeClarificationSent[addressType] = normalize(address);
    
    // CRITICAL: Track which field we're asking about so the next postcode/answer routes correctly
    awaitingClarificationFor = addressType;
    console.log(`[${callId}] 📋 Now awaiting clarification for: ${addressType}`);

    // Check if we've exceeded max attempts - accept address as-is and move on
    if (attempts >= MAX_CLARIFICATION_ATTEMPTS) {
      console.log(`[${callId}] 🛑 Max clarification attempts (${MAX_CLARIFICATION_ATTEMPTS}) reached for ${addressType} - accepting address as-is`);
      
      // Mark as verified (approximately) and continue
      if (addressType === "pickup") {
        knownBooking.pickupVerified = true;
      } else {
        knownBooking.destinationVerified = true;
      }
      
      // Add note to transcript
      transcriptHistory.push({
        role: "system",
        text: `⚠️ Max attempts reached for ${addressType}: "${address}" - accepted without full verification`,
        timestamp: new Date().toISOString()
      });
      queueLiveCallBroadcast({});
      
      // Clear clarification state and tell Ada to continue
      awaitingClarificationFor = null;
      
      openaiWs.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "message",
          role: "user",
          content: [{ type: "input_text", text: `[SYSTEM: We've asked about the ${addressType} address "${address}" multiple times. Accept this address and continue with the booking - do NOT ask about it again. Move on to the next question.]` }],
        },
      }));
      
      openaiWs.send(JSON.stringify({
        type: "response.create",
        response: { modalities: ["audio", "text"] },
      }));
      return;
    }

    // IMPORTANT: Do NOT ask customer to spell out common landmarks like train stations, airports, hospitals, etc.
    // Only ask for clarification if it's a residential address that sounds garbled
    const isLandmark = /\b(station|airport|hospital|university|college|school|shopping|centre|center|mall|supermarket|tesco|asda|sainsbury|morrisons|aldi|lidl|hotel|inn|pub|restaurant|church|mosque|temple|gurdwara|park|library|museum|theatre|theater|cinema|gym|sports|leisure|pool|bus\s*stop|taxi\s*rank)\b/i.test(address);

    if (isLandmark) {
      console.log(`[${callId}] 📍 Landmark detected - accepting without clarification: "${address}"`);
      return; // Don't ask for clarification on landmarks
    }

    // Heuristic: if the customer already provided a specific street address, do NOT ask them to repeat it.
    // Ask ONLY for postcode / area / nearby landmark so dispatch is safe but low-friction.
    const looksLikeStreetAddress = /^\s*\d+[a-z]?\s+.+/i.test(address);
    
    // Vary the message based on attempt number to avoid sounding robotic
    const attemptContext = attempts > 1 ? ` (Attempt ${attempts}/${MAX_CLARIFICATION_ATTEMPTS} - be patient and helpful)` : "";

    const message = (() => {
      if (addressType === "pickup") {
        if (attempts === 1) {
          return looksLikeStreetAddress
            ? `[SYSTEM: The pickup address "${address}" could not be verified. The customer already gave the street name/house number. Ask ONLY for the POSTCODE (or a nearby landmark/area). Say: "Could you tell me the postcode for that pickup address, please?"]`
            : `[SYSTEM: The pickup address "${address}" could not be verified. Politely ask the customer to confirm or provide the correct address. Say something like "I'm having a little trouble finding that address. Could you give me the full street name and postcode please?"]`;
        } else if (attempts === 2) {
          return `[SYSTEM: Second attempt for pickup "${address}". Be MORE specific - ask: "Could you spell out the street name for me, or give me a nearby landmark I can use?"]`;
        }
        return `[SYSTEM: Final attempt for pickup "${address}". Ask clearly: "I'm still having trouble with that address. What's the nearest main road or a business nearby I can look up?"]`;
      }

      // destination
      if (attempts === 1) {
        return `[SYSTEM: The destination address "${address}" could not be verified. Ask for the POSTCODE / area (or a nearby landmark) to confirm it safely. Say something like "Could you tell me the postcode or the area for that destination, please?"]`;
      } else if (attempts === 2) {
        return `[SYSTEM: Second attempt for destination "${address}". Be MORE specific - ask: "Could you spell out the name for me, or is there a landmark nearby I can search for?"]`;
      }
      return `[SYSTEM: Final attempt for destination "${address}". Ask clearly: "What's the nearest main road or a well-known place nearby?"]`;
    })();

    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: message }],
      },
    }));

    // Trigger Ada to respond
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: { modalities: ["audio", "text"] },
    }));
  };

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

  // Look up caller by phone number and check for active bookings
  const lookupCaller = async (phone: string): Promise<void> => {
    if (!phone) {
      console.log(`[${callId}] ⚠️ lookupCaller called with no phone`);
      return;
    }

    const phoneNorm = normalizePhone(phone);
    const phoneCandidates = Array.from(new Set([phone, phoneNorm].filter(Boolean)));
    console.log(`[${callId}] 🔍 Looking up caller: ${phone} (normalized: ${phoneNorm}, candidates: ${phoneCandidates.join(', ')})`);

    try {
      // Lookup caller info (including trusted addresses)
      const { data, error } = await supabase
        .from("callers")
        .select("name, last_pickup, last_destination, last_booking_at, total_bookings, trusted_addresses, known_areas, address_aliases, pickup_addresses, dropoff_addresses")
        .in("phone_number", phoneCandidates)
        .maybeSingle();

      if (error) {
        console.error(`[${callId}] ❌ Caller lookup error:`, error);
        return;
      }

      console.log(`[${callId}] 📋 Caller lookup result:`, data ? `Found: ${data.name || 'no name'}, bookings: ${data.total_bookings}` : 'No match');

      if (data?.name && isValidCallerName(data.name)) {
        // Only overwrite callerName if we don't already have a valid name from Asterisk
        if (!isValidCallerName(callerName)) {
          callerName = data.name;
        }
        callerTotalBookings = data.total_bookings || 0;
        callerLastPickup = data.last_pickup || "";
        callerLastDestination = data.last_destination || "";
        callerLastBookingAt = data.last_booking_at || "";
        
        // Load known_areas for reference
        callerKnownAreas = (data.known_areas as Record<string, number>) || {};

        // Load pickup and dropoff address arrays
        callerPickupAddresses = (data.pickup_addresses as string[]) || [];
        callerDropoffAddresses = (data.dropoff_addresses as string[]) || [];
        if (callerPickupAddresses.length > 0) {
          console.log(`[${callId}] 📍 Loaded ${callerPickupAddresses.length} pickup addresses`);
        }
        if (callerDropoffAddresses.length > 0) {
          console.log(`[${callId}] 🎯 Loaded ${callerDropoffAddresses.length} dropoff addresses`);
        }

        // Load address aliases (e.g., {"home": "52A David Road", "work": "Train Station"})
        callerAddressAliases = (data.address_aliases as Record<string, string>) || {};
        if (Object.keys(callerAddressAliases).length > 0) {
          console.log(`[${callId}] 🏠 Loaded address aliases: ${JSON.stringify(callerAddressAliases)}`);
        }

        // Derive primary city (bias) with better priorities:
        // IMPORTANT: Only use PICKUP addresses for city bias, NOT destinations!
        // 1) Cities from saved pickup_addresses (most reliable for caller's local area)
        // 2) Any explicit city embedded in saved aliases (home/work locations)
        // 3) last_pickup city
        // 4) known_areas counts
        // 5) Default to Coventry (service area)
        
        // 1) Cities from pickup_addresses
        if (!callerCity && callerPickupAddresses.length > 0) {
          const pickupCities = callerPickupAddresses
            .map((a) => extractCityFromAddress(a))
            .filter(Boolean) as string[];
          if (pickupCities.length > 0) {
            const counts = pickupCities.reduce<Record<string, number>>((acc, c) => {
              acc[c] = (acc[c] || 0) + 1;
              return acc;
            }, {});
            const topPickupCity = Object.entries(counts).sort((a, b) => b[1] - a[1])[0]?.[0];
            if (topPickupCity) {
              callerCity = topPickupCity;
              console.log(`[${callId}] 🏙️ Primary city from pickup_addresses: ${callerCity}`);
            }
          }
        }

        // 2) Cities from address aliases (if not already set from pickup_addresses)
        if (!callerCity) {
          const aliasCities = Object.values(callerAddressAliases)
            .map((a) => extractCityFromAddress(a))
            .filter(Boolean) as string[];

          if (aliasCities.length > 0) {
            const counts = aliasCities.reduce<Record<string, number>>((acc, c) => {
              acc[c] = (acc[c] || 0) + 1;
              return acc;
            }, {});
            const topAliasCity = Object.entries(counts).sort((a, b) => b[1] - a[1])[0]?.[0];
            if (topAliasCity) {
              callerCity = topAliasCity;
              console.log(`[${callId}] 🏙️ Primary city from address_aliases: ${callerCity}`);
            }
          }
        }

        // 3) last_pickup city
        if (!callerCity && callerLastPickup) {
          const pickupCity = extractCityFromAddress(callerLastPickup);
          if (pickupCity) {
            callerCity = pickupCity;
            console.log(`[${callId}] 🏙️ Primary city from last_pickup: ${callerCity}`);
          }
        }

        // 4) known_areas (pickup-derived, not destination)
        if (!callerCity && Object.keys(callerKnownAreas).length > 0) {
          const topCity = Object.entries(callerKnownAreas).sort((a, b) => b[1] - a[1])[0];
          if (topCity) {
            callerCity = topCity[0];
            console.log(`[${callId}] 🏙️ Primary city from known_areas: ${callerCity} (${topCity[1]} mentions)`);
          }
        }

        // NOTE: We intentionally do NOT use last_destination for city bias
        // Destinations can be anywhere and shouldn't influence pickup geocoding

        
        // If still no city, geocode the history address to get city from Google
        if (!callerCity && (callerLastPickup || callerLastDestination)) {
          const historyAddr = callerLastPickup || callerLastDestination;
          console.log(`[${callId}] 🏙️ No city in known_areas or address text, geocoding history: "${historyAddr}"`);
          try {
            const geoResp = await fetch(`${SUPABASE_URL}/functions/v1/geocode`, {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
              },
              body: JSON.stringify({ address: historyAddr, country: "UK" }),
            });
            const geoData = await geoResp.json();
            if (geoData.found && geoData.city) {
              callerCity = geoData.city;
              // Also add to known_areas for future calls
              callerKnownAreas[callerCity] = (callerKnownAreas[callerCity] || 0) + 1;
              console.log(`[${callId}] 🏙️ Got caller city from geocoding: ${callerCity}`);
              
              // Persist the discovered city
              const phoneNorm = normalizePhone(userPhone);
              if (phoneNorm) {
                supabase
                  .from("callers")
                  .update({ known_areas: callerKnownAreas })
                  .eq("phone_number", phoneNorm)
                  .then(({ error }) => {
                    if (error) console.error(`[${callId}] Failed to save discovered city:`, error);
                  });
              }
            }
            if (geoData.found && geoData.lat && geoData.lon) {
              console.log(`[${callId}] 📍 Caller history coordinates: ${geoData.lat}, ${geoData.lon}`);
            }
          } catch (e) {
            console.error(`[${callId}] Failed to geocode caller history:`, e);
          }
        }

        // Default service area bias for new/unknown callers.
        // Prevents early destination mentions (e.g. "to Manchester") from hijacking pickup geocoding.
        if (!callerCity) {
          callerCity = "Coventry";
          console.log(`[${callId}] 🏙️ Defaulting caller city to service area: ${callerCity}`);
        }

        console.log(`[${callId}] 👤 Known caller: ${callerName} (${callerTotalBookings} previous bookings)`);
        if (callerLastPickup) {
          console.log(`[${callId}] 📍 Last trip: ${callerLastPickup} → ${callerLastDestination}`);
        }
        if (callerCity) {
          console.log(`[${callId}] 🏙️ Caller city: ${callerCity}`);
        }
        if (Object.keys(callerKnownAreas).length > 0) {
          console.log(`[${callId}] 🗺️ Known areas: ${JSON.stringify(callerKnownAreas)}`);
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
        .select("id, pickup, destination, passengers, fare, booked_at, pickup_name, destination_name")
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

      // Avoid duplicates by call_id - but ONLY load if still ACTIVE (not cancelled)
      const { data: existingBooking } = await supabase
        .from("bookings")
        .select("id, pickup, destination, passengers, fare, booked_at, status")
        .eq("call_id", lastConfirmed.call_id)
        .maybeSingle();

      if (existingBooking) {
        // If booking exists but was cancelled, don't treat it as active
        if (existingBooking.status === "cancelled") {
          console.log(`[${callId}] ⚠️ Found booking ${existingBooking.id} but it was CANCELLED - not loading as active`);
          return;
        }
        activeBooking = existingBooking;
        console.log(`[${callId}] 📋 Active booking loaded from existing bookings row: ${existingBooking.id}`);
        return;
      }
      
      // Before creating a new backfill, check if there's a cancelled booking for this call_id
      // If so, don't re-create it (customer cancelled it intentionally)
      const { data: cancelledBooking } = await supabase
        .from("bookings")
        .select("id")
        .eq("call_id", lastConfirmed.call_id)
        .eq("status", "cancelled")
        .maybeSingle();
      
      if (cancelledBooking) {
        console.log(`[${callId}] ⚠️ Booking for call ${lastConfirmed.call_id} was previously cancelled - not re-creating`);
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
  // Only save VERIFIED addresses to caller history to prevent pollution from incorrect geocodes
  const saveCallerInfo = async (booking: { 
    pickup: string; 
    destination: string; 
    passengers: number;
    pickupVerified?: boolean;
    destinationVerified?: boolean;
  }): Promise<void> => {
    if (!userPhone) return;
    
    // Log verification status for debugging
    console.log(`[${callId}] 💾 saveCallerInfo called - pickup verified: ${booking.pickupVerified}, destination verified: ${booking.destinationVerified}`);
    try {
      // Check if caller exists
      const { data: existing } = await supabase
        .from("callers")
        .select("id, total_bookings, trusted_addresses, pickup_addresses, dropoff_addresses")
        .eq("phone_number", userPhone)
        .maybeSingle();
      
      // Build updated trusted addresses list (add pickup and destination if not already present)
      const MAX_ADDRESSES = 10; // Limit to prevent unbounded growth
      let updatedTrusted: string[] = existing?.trusted_addresses || callerTrustedAddresses || [];
      let updatedPickupAddrs: string[] = existing?.pickup_addresses || callerPickupAddresses || [];
      let updatedDropoffAddrs: string[] = existing?.dropoff_addresses || callerDropoffAddresses || [];
      
      // Normalize addresses for comparison (use core address without city for deduplication)
      const normalizeForComparison = (a: string) => normalize(a).toLowerCase().split(',')[0].trim();
      const normalizedTrusted = new Set(updatedTrusted.map(normalizeForComparison));
      const normalizedPickups = new Set(updatedPickupAddrs.map(normalizeForComparison));
      const normalizedDropoffs = new Set(updatedDropoffAddrs.map(normalizeForComparison));
      
      // Helper to enrich address with city if missing - BUT track if we added it artificially
      const ensureAddressHasCity = (addr: string, isPickup: boolean): { enriched: string; cityFromOriginal: boolean } => {
        if (!addr) return { enriched: addr, cityFromOriginal: false };
        const hasCity = extractCityFromAddress(addr);
        if (hasCity) return { enriched: addr, cityFromOriginal: true }; // City was in the original address
        const hasPostcode = /[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}/i.test(addr);
        if (hasPostcode) return { enriched: addr, cityFromOriginal: false }; // Has postcode, but no city
        // IMPORTANT: Only add callerCity if it came from PICKUP history, not destination
        // This prevents destination cities being used as pickup context
        if (callerCity && isPickup) {
          // Check if callerCity is from pickup_addresses history (legitimate local context)
          const pickupCities = callerPickupAddresses.map(a => extractCityFromAddress(a)).filter(Boolean);
          if (pickupCities.some(c => c?.toLowerCase() === callerCity.toLowerCase())) {
            return { enriched: `${addr}, ${callerCity}`, cityFromOriginal: false };
          }
        }
        return { enriched: addr, cityFromOriginal: false };
      };
      
      const pickupResult = ensureAddressHasCity(booking.pickup, true);
      const enrichedPickup = pickupResult.enriched;
      const destResult = ensureAddressHasCity(booking.destination, false);
      const enrichedDestination = destResult.enriched;
      
      // Add pickup to pickup_addresses ONLY if verified (prevents polluting history with wrong geocodes)
      const pickupCore = normalizeForComparison(booking.pickup || "");
      if (booking.pickup && !normalizedPickups.has(pickupCore) && booking.pickupVerified) {
        updatedPickupAddrs.push(enrichedPickup);
        console.log(`[${callId}] 📍 Adding VERIFIED pickup to pickup_addresses: "${enrichedPickup}"`);
      } else if (booking.pickup && !booking.pickupVerified) {
        console.log(`[${callId}] ⚠️ Skipping unverified pickup (not saving to history): "${booking.pickup}"`);
      }
      
      // Add destination to dropoff_addresses ONLY if verified
      const destCore = normalizeForComparison(booking.destination || "");
      if (booking.destination && !normalizedDropoffs.has(destCore) && booking.destinationVerified) {
        updatedDropoffAddrs.push(enrichedDestination);
        console.log(`[${callId}] 🎯 Adding VERIFIED destination to dropoff_addresses: "${enrichedDestination}"`);
      } else if (booking.destination && !booking.destinationVerified) {
        console.log(`[${callId}] ⚠️ Skipping unverified destination (not saving to history): "${booking.destination}"`);
      }
      
      // Also add to trusted_addresses for compatibility - but only if verified
      if (booking.pickup && !normalizedTrusted.has(pickupCore) && booking.pickupVerified) {
        updatedTrusted.push(enrichedPickup);
      }
      if (booking.destination && !normalizedTrusted.has(destCore) && booking.destinationVerified) {
        updatedTrusted.push(enrichedDestination);
      }
      
      // Trim all arrays to max size (keep most recent)
      if (updatedTrusted.length > MAX_ADDRESSES) {
        updatedTrusted = updatedTrusted.slice(-MAX_ADDRESSES);
      }
      if (updatedPickupAddrs.length > MAX_ADDRESSES) {
        updatedPickupAddrs = updatedPickupAddrs.slice(-MAX_ADDRESSES);
      }
      if (updatedDropoffAddrs.length > MAX_ADDRESSES) {
        updatedDropoffAddrs = updatedDropoffAddrs.slice(-MAX_ADDRESSES);
      }
      
      // Update known_areas with the pickup city ONLY if it was in the original address
      // This prevents artificially enriched cities from polluting the area history
      if (pickupResult.cityFromOriginal) {
        const pickupCity = extractCityFromAddress(enrichedPickup);
        if (pickupCity) {
          callerKnownAreas[pickupCity] = (callerKnownAreas[pickupCity] || 0) + 1;
          console.log(`[${callId}] 🏙️ Updated known_areas with pickup city: ${pickupCity}`);
        }
      } else {
        console.log(`[${callId}] ⚠️ Pickup address has no verified city - not updating known_areas`);
      }
      
      if (existing) {
        // Update existing caller
        const { error } = await supabase.from("callers").update({
          last_pickup: enrichedPickup,
          last_destination: enrichedDestination,
          last_booking_at: new Date().toISOString(),
          total_bookings: (existing.total_bookings || 0) + 1,
          trusted_addresses: updatedTrusted,
          pickup_addresses: updatedPickupAddrs,
          dropoff_addresses: updatedDropoffAddrs,
          known_areas: callerKnownAreas,
          updated_at: new Date().toISOString()
        }).eq("phone_number", userPhone);
        
        if (error) console.error(`[${callId}] Update caller error:`, error);
        else console.log(`[${callId}] 💾 Updated caller ${userPhone} (${existing.total_bookings + 1} bookings, ${updatedPickupAddrs.length} pickups, ${updatedDropoffAddrs.length} dropoffs)`);
      } else {
        // Insert new caller
        const { error } = await supabase.from("callers").insert({
          phone_number: userPhone,
          name: callerName || null,
          last_pickup: enrichedPickup,
          last_destination: enrichedDestination,
          last_booking_at: new Date().toISOString(),
          total_bookings: 1,
          trusted_addresses: updatedTrusted,
          pickup_addresses: updatedPickupAddrs,
          dropoff_addresses: updatedDropoffAddrs,
          known_areas: callerKnownAreas
        });
        
        if (error) console.error(`[${callId}] Insert caller error:`, error);
        else console.log(`[${callId}] 💾 New caller saved: ${userPhone} with ${updatedPickupAddrs.length} pickups, ${updatedDropoffAddrs.length} dropoffs`);
      }
      
      // Update local cache for this session
      callerTrustedAddresses = updatedTrusted;
      callerPickupAddresses = updatedPickupAddrs;
      callerDropoffAddresses = updatedDropoffAddrs;
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
        caller_last_booking_at: callerLastBookingAt || null,
        ...updates,
        transcripts: transcriptHistory,
        updated_at: new Date().toISOString(),
        // Include attempt counters for monitoring/debugging
        clarification_attempts: {
          pickup: knownBooking.pickupClarificationAttempts || 0,
          destination: knownBooking.destinationClarificationAttempts || 0,
          passengers: knownBooking.passengerAttempts || 0,
          luggage: knownBooking.luggageAttempts || 0,
        },
      }, { onConflict: "call_id" });
      if (error) console.error(`[${callId}] Live call broadcast error:`, error);
    } catch (e) {
      console.error(`[${callId}] Live call broadcast exception:`, e);
    }
  };

  // Serialize writes to live_calls to prevent out-of-order transcript arrays.
  // We keep it non-blocking (fire-and-forget) but guarantee ordering.
  let liveCallBroadcastChain: Promise<void> = Promise.resolve();
  const queueLiveCallBroadcast = (updates: Record<string, any>) => {
    liveCallBroadcastChain = liveCallBroadcastChain
      .then(() => broadcastLiveCall(updates))
      .catch((e) => console.error(`[${callId}] Live call broadcast chain error:`, e));
  };

  // Extract customer name from transcript - improved with multi-word names and better patterns
  const extractNameFromTranscript = (transcript: string): string | null => {
    const t = transcript.trim();
    
    // Expanded list of non-name words to filter out - including "not" to prevent "my name is not X" errors
    const nonNames = new Set([
      'yes', 'no', 'yeah', 'yep', 'nope', 'okay', 'ok', 'sure', 'please', 'thanks', 'thank',
      'hello', 'hi', 'hey', 'hiya', 'the', 'from', 'to', 'a', 'an', 'and', 'or', 'but', 'for',
      'taxi', 'cab', 'car', 'booking', 'book', 'need', 'want', 'would', 'like', 'can',
      'could', 'just', 'actually', 'really', 'well', 'um', 'uh', 'er', 'ah', 'oh',
      'good', 'morning', 'afternoon', 'evening', 'night', 'today', 'now', 'soon', 'asap',
      'picking', 'pick', 'up', 'going', 'to', 'heading', 'one', 'two', 'three', 'four',
      'not', 'wrong', 'incorrect', 'correct', 'actually', 'change', 'update', 'fix',
      'you', 'your', 'yours', 'me', 'my', 'mine', 'i', 'we', 'us', 'they', 'them', 'it',
      'bye', 'goodbye', 'cheers', 'ta', 'brilliant', 'lovely', 'great', 'fine', 'alright'
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
    
    // PRIORITY 1: Name CORRECTION patterns - handle "my name is not X, it's Y" etc
    // These MUST be checked first to get the corrected name, not the wrong one
    const correctionPatterns: Array<{ regex: RegExp; group: number }> = [
      // "my name is not X, my name is Y" / "name is not X it's Y"
      { regex: /my name(?:'s| is) not\s+\w+[,.\s]+(?:my name(?:'s| is)|it(?:'s| is)|i(?:'m| am))\s+([A-Za-z]+)/i, group: 1 },
      // "it's not X, it's Y" / "I'm not X, I'm Y"  
      { regex: /(?:it(?:'s| is)|i(?:'m| am)) not\s+\w+[,.\s]+(?:it(?:'s| is)|i(?:'m| am)|my name(?:'s| is))\s+([A-Za-z]+)/i, group: 1 },
      // "not X, my name is Y" / "not X, I'm Y"
      { regex: /not\s+\w+[,.\s]+(?:my name(?:'s| is)|i(?:'m| am)|it(?:'s| is))\s+([A-Za-z]+)/i, group: 1 },
      // "wrong, my name is Y" / "incorrect, it's Y"
      { regex: /(?:wrong|incorrect|no)[,.\s]+(?:my name(?:'s| is)|it(?:'s| is)|i(?:'m| am))\s+([A-Za-z]+)/i, group: 1 },
      // "please correct it, my name is Y" / "correct my name to Y"
      { regex: /(?:correct|change|update|fix)(?:\s+(?:it|my name))?\s*(?:to|,)?\s*(?:my name(?:'s| is)|it(?:'s| is)|i(?:'m| am))?\s*([A-Za-z]+)/i, group: 1 },
      // "call me Y instead" / "it's actually Y"
      { regex: /(?:call me|it(?:'s| is) actually|i(?:'m| am) actually)\s+([A-Za-z]+)/i, group: 1 },
    ];
    
    // Check correction patterns FIRST
    for (const { regex, group } of correctionPatterns) {
      const match = t.match(regex);
      if (match?.[group]) {
        const rawName = match[group].trim();
        const firstName = rawName.split(/\s+/)[0];
        
        if (isValidName(firstName)) {
          const name = firstName.charAt(0).toUpperCase() + firstName.slice(1).toLowerCase();
          console.log(`[${callId}] 🔧 Correction pattern extracted name: "${name}" from "${t}"`);
          return name;
        }
      }
    }
    
    // PRIORITY 2: Standard name introduction patterns
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

  // ═══════════════════════════════════════════════════════════════════════════
  // UNIFIED AI EXTRACTION (calls taxi-extract-unified when enabled)
  // ═══════════════════════════════════════════════════════════════════════════
  const extractWithUnifiedAI = async (transcript: string): Promise<void> => {
    if (!useUnifiedExtraction) return;
    
    try {
      console.log(`[${callId}] 🧪 UNIFIED EXTRACTION: Processing transcript: "${transcript}"`);
      
      // Build full conversation history for context
      const conversationHistory = transcriptHistory.map(t => ({
        role: t.role,
        text: t.text
      }));
      
      const response = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract-unified`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
        },
        body: JSON.stringify({
          transcript: conversationHistory,
          current_booking: knownBooking,
          caller_name: callerName || null,
          caller_city: callerCity || null,
          pickup_history: callerPickupAddresses || [],
          dropoff_history: callerDropoffAddresses || [],
        }),
      });

      if (!response.ok) {
        console.error(`[${callId}] 🧪 Unified extraction API error: ${response.status}`);
        return;
      }

      const extracted = await response.json();
      console.log(`[${callId}] 🧪 UNIFIED EXTRACTED:`, extracted);
      
      // Update knownBooking with extracted fields
      let anyUpdate = false;
      
      if (extracted.pickup && !knownBooking.pickup) {
        knownBooking.pickup = extracted.pickup;
        anyUpdate = true;
        console.log(`[${callId}] 🧪 Updated pickup: ${extracted.pickup}`);
      }
      
      if (extracted.destination && !knownBooking.destination) {
        knownBooking.destination = extracted.destination;
        anyUpdate = true;
        console.log(`[${callId}] 🧪 Updated destination: ${extracted.destination}`);
      }
      
      if (extracted.passengers && !knownBooking.passengers) {
        knownBooking.passengers = extracted.passengers;
        anyUpdate = true;
        console.log(`[${callId}] 🧪 Updated passengers: ${extracted.passengers}`);
      }
      
      if (extracted.luggage && !knownBooking.luggage) {
        knownBooking.luggage = extracted.luggage;
        anyUpdate = true;
        console.log(`[${callId}] 🧪 Updated luggage: ${extracted.luggage}`);
      }
      
      if (extracted.pickup_time && !knownBooking.pickupTime) {
        knownBooking.pickupTime = extracted.pickup_time;
        anyUpdate = true;
        console.log(`[${callId}] 🧪 Updated pickup_time: ${extracted.pickup_time}`);
      }
      
      if (extracted.vehicle_type && !knownBooking.vehicleType) {
        knownBooking.vehicleType = extracted.vehicle_type;
        anyUpdate = true;
        console.log(`[${callId}] 🧪 Updated vehicle_type: ${extracted.vehicle_type}`);
      }
      
      // Log missing fields for debugging
      if (extracted.missing_fields && extracted.missing_fields.length > 0) {
        console.log(`[${callId}] 🧪 Missing fields: ${extracted.missing_fields.join(', ')}`);
      }
      
      if (anyUpdate) {
        // Broadcast updated booking to dashboard
        queueLiveCallBroadcast({
          pickup: knownBooking.pickup,
          destination: knownBooking.destination,
          passengers: knownBooking.passengers,
        });
        
        // Add system note to transcript
        transcriptHistory.push({
          role: "system",
          text: `🧪 UNIFIED: pickup="${knownBooking.pickup || '?'}" dest="${knownBooking.destination || '?'}" pax=${knownBooking.passengers || '?'} bags="${knownBooking.luggage || '?'}"`,
          timestamp: new Date().toISOString()
        });
      }
      
    } catch (e) {
      console.error(`[${callId}] 🧪 Unified extraction error:`, e);
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
        
        if (extractedName && isValidCallerName(extractedName)) {
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
            
            // Area question is now asked in the initial greeting for new callers
            // Just log if we're still awaiting the area (it may come in same response as name)
            if (!callerCity && Object.keys(callerKnownAreas).length === 0 && callerTotalBookings === 0) {
              console.log(`[${callId}] 🏙️ New caller "${callerName}" - checking if area was also provided...`);
              // Try to extract city from the same transcript that had the name
              const mentionedCityInNameResponse = extractCityFromAddress(transcript);
              if (mentionedCityInNameResponse) {
                callerCity = mentionedCityInNameResponse;
                callerKnownAreas[mentionedCityInNameResponse] = 1;
                awaitingAreaResponse = false;
                console.log(`[${callId}] 🏙️ Area captured with name: ${callerCity}`);
              }
            }
          }
        }
      }
      
      // CHECK: If we're awaiting an area response, try to extract the city from this transcript
      if (awaitingAreaResponse && !callerCity) {
        const mentionedCity = extractCityFromAddress(transcript);
        if (mentionedCity) {
          callerCity = mentionedCity;
          callerKnownAreas[mentionedCity] = (callerKnownAreas[mentionedCity] || 0) + 1;
          awaitingAreaResponse = false;
          
          console.log(`[${callId}] 🏙️ New caller's area captured: ${callerCity}`);
          
          // Persist to database
          if (userPhone) {
            const phoneNorm = normalizePhone(userPhone);
            supabase
              .from("callers")
              .update({ known_areas: callerKnownAreas })
              .eq("phone_number", phoneNorm)
              .then(({ error }) => {
                if (error) console.error(`[${callId}] Failed to save area:`, error);
                else console.log(`[${callId}] 💾 Saved area ${callerCity} for ${userPhone}`);
              });
          }
          
          // Inject silent context update so Ada knows the area
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{ type: "input_text", text: `[INTERNAL: Customer is calling from ${callerCity}. Use this to help with address lookups. Do NOT mention this to the customer - just proceed with the booking.]` }]
              }
            }));
          }
        }
      }
      
      // CHECK: If we have pending area disambiguation, try to match the customer's choice
      if (pendingAreaDisambiguation && pendingAreaDisambiguation.matches.length > 0) {
        const lowerTranscript = transcript.toLowerCase();
        
        // Fuzzy matching helper - handles STT mishearings like "Soryo" → "Solihull"
        const fuzzyMatch = (spoken: string, target: string): boolean => {
          const spokenLower = spoken.toLowerCase().trim();
          const targetLower = target.toLowerCase().trim();
          
          if (!spokenLower || !targetLower) return false;
          
          // Exact or substring match
          if (spokenLower === targetLower) return true;
          if (targetLower.includes(spokenLower) && spokenLower.length >= 3) return true;
          if (spokenLower.includes(targetLower)) return true;
          
          // First 3 characters match (catches "sol" matching "solihull")
          if (spokenLower.length >= 3 && targetLower.startsWith(spokenLower.slice(0, 3))) return true;
          
          // Common STT mishearings for these specific areas
          const mishearings: Record<string, string[]> = {
            'solihull': ['solio', 'soryo', 'solia', 'solyo', 'solehill', 'solihill', 'solidhull', 'soul', 'solely', 'sunnyvale', 'sunny', 'sunnyville', 'sonyvale', 'sonny'],
            'bedworth': ['bedford', 'bedward', 'bedsworth', 'badworth'],
            'henley-in-arden': ['henley', 'hendley', 'henleigh', 'arden'],
            'birmingham': ['burning', 'burningham', 'brimingham', 'bermingham', 'birming'],
            'dudley': ['deadly', 'doodley', 'dudly', 'dully'],
          };
          
          for (const [correct, variants] of Object.entries(mishearings)) {
            if (targetLower === correct || targetLower.includes(correct.split('-')[0])) {
              if (variants.some(v => spokenLower.includes(v) || v.includes(spokenLower))) {
                return true;
              }
            }
          }
          
          // Levenshtein distance for close matches (allow 40% error rate for short words)
          const maxDist = Math.max(2, Math.floor(Math.max(spokenLower.length, targetLower.length) * 0.4));
          const m = spokenLower.length, n = targetLower.length;
          const dp: number[][] = Array(m + 1).fill(null).map(() => Array(n + 1).fill(0));
          for (let i = 0; i <= m; i++) dp[i][0] = i;
          for (let j = 0; j <= n; j++) dp[0][j] = j;
          for (let i = 1; i <= m; i++) {
            for (let j = 1; j <= n; j++) {
              dp[i][j] = Math.min(
                dp[i-1][j] + 1,
                dp[i][j-1] + 1,
                dp[i-1][j-1] + (spokenLower[i-1] === targetLower[j-1] ? 0 : 1)
              );
            }
          }
          
          return dp[m][n] <= maxDist;
        };
        
        // Extract candidate words/phrases from transcript to match against areas
        // e.g., "school road in solio" → ["school", "road", "in", "solio", "school road", "in solio"]
        const words = lowerTranscript.replace(/[.,!?]/g, '').split(/\s+/).filter(w => w.length >= 2);
        const candidatePhrases = [...words];
        
        // Also check last word specifically (most likely to be the area)
        const lastWord = words[words.length - 1];
        
        // Try to match the area from the customer's response
        let matchedArea: PendingAreaDisambiguation['matches'][0] | null = null;
        
        for (const match of pendingAreaDisambiguation.matches) {
          const targetArea = match.area.toLowerCase();
          
          // Check last word first (highest priority - "It's in Solio" → "Solio")
          if (lastWord && fuzzyMatch(lastWord, targetArea)) {
            matchedArea = match;
            console.log(`[${callId}] 🗺️ Fuzzy matched last word "${lastWord}" → "${match.area}"`);
            break;
          }
          
          // Check all words
          for (const word of candidatePhrases) {
            if (fuzzyMatch(word, targetArea)) {
              matchedArea = match;
              console.log(`[${callId}] 🗺️ Fuzzy matched word "${word}" → "${match.area}"`);
              break;
            }
          }
          
          if (matchedArea) break;
        }
        
        // If still no match, check if full transcript contains area name or close variant
        if (!matchedArea) {
          for (const match of pendingAreaDisambiguation.matches) {
            if (fuzzyMatch(lowerTranscript, match.area)) {
              matchedArea = match;
              console.log(`[${callId}] 🗺️ Fuzzy matched full transcript → "${match.area}"`);
              break;
            }
          }
        }
        
        if (matchedArea) {
          console.log(`[${callId}] 🗺️ Area disambiguation resolved: ${matchedArea.area} for ${pendingAreaDisambiguation.addressType}`);
          
          // Update the address with the chosen area
          const fullAddress = matchedArea.formatted_address || `${matchedArea.road}, ${matchedArea.area}${matchedArea.city ? `, ${matchedArea.city}` : ''}`;
          
          if (pendingAreaDisambiguation.addressType === "pickup") {
            knownBooking.pickup = fullAddress;
            knownBooking.pickupVerified = true;
            knownBooking.pickupAreaResolved = true; // Prevent re-disambiguation in book_taxi
            console.log(`[${callId}] 📍 Pickup updated to: ${fullAddress} (area resolved)`);
            
            // Inject confirmation into Ada's context
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{ 
                    type: "input_text", 
                    text: `[INTERNAL: Customer chose ${matchedArea.area}. Pickup is now confirmed as "${fullAddress}". Continue with the booking - ask for any missing details or confirm the booking.]` 
                  }]
                }
              }));
            }
          } else {
            knownBooking.destination = fullAddress;
            knownBooking.destinationVerified = true;
            knownBooking.destinationAreaResolved = true; // Prevent re-disambiguation in book_taxi
            console.log(`[${callId}] 📍 Destination updated to: ${fullAddress} (area resolved)`);
            
            // Inject confirmation into Ada's context
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "user",
                  content: [{ 
                    type: "input_text", 
                    text: `[INTERNAL: Customer chose ${matchedArea.area}. Destination is now confirmed as "${fullAddress}". Continue with the booking - confirm or ask any remaining questions.]` 
                  }]
                }
              }));
            }
          }
          
          // Clear the pending disambiguation BEFORE triggering response
          pendingAreaDisambiguation = null;
          clearDisambiguationTimeout(); // SAFEGUARD 3: Clear timeout since resolved
          
          // CRITICAL: Trigger Ada to respond now that disambiguation is resolved
          // CONCURRENCY GUARD: Don't send response.create if one is already in progress
          if (openaiWs?.readyState === WebSocket.OPEN) {
            const disambigResponse = { modalities: ["audio", "text"] };
            
            if (openAiResponseInProgress) {
              deferredResponseCreate = { response: disambigResponse as Record<string, unknown>, label: "disambiguation_resolved" };
              console.log(`[${callId}] ⏸️ Deferring response.create (disambiguation_resolved) - response already in progress`);
            } else {
              openAiResponseInProgress = true;
              startResponseTimeout(); // SAFEGUARD 1
              openaiWs.send(JSON.stringify({
                type: "response.create",
                response: disambigResponse
              }));
              console.log(`[${callId}] >>> response.create sent after disambiguation resolved`);
            }
          }
          
          // Broadcast update to live_calls
          await supabase.from("live_calls").update({
            pickup: knownBooking.pickup || null,
            destination: knownBooking.destination || null,
            updated_at: new Date().toISOString()
          }).eq("call_id", callId);
          
          // Skip normal extraction since we just resolved disambiguation
          return;
        } else {
          // Customer said something but it didn't match any option - re-ask
          console.log(`[${callId}] 🗺️ No fuzzy match found in "${lowerTranscript}" for options: ${pendingAreaDisambiguation.matches.map(m => m.area).join(', ')}`);
          
          // Re-prompt with clearer options
          const areasForSpeech = pendingAreaDisambiguation.matches.slice(0, 4).map(m => m.area);
          let speechOptions: string;
          if (areasForSpeech.length === 2) {
            speechOptions = `${areasForSpeech[0]} or ${areasForSpeech[1]}`;
          } else {
            const lastOption = areasForSpeech.pop();
            speechOptions = `${areasForSpeech.join(", ")}, or ${lastOption}`;
          }
          
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{ 
                  type: "input_text", 
                  text: `[SYSTEM: Customer's response "${transcript}" didn't clearly match an area. Politely ask again: "Sorry, I didn't quite catch that. Was it ${speechOptions}?"]` 
                }]
              }
            }));
            
            openaiWs.send(JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] }
            }));
            console.log(`[${callId}] >>> Re-asking for disambiguation (no clear match)`);
          }
          
          // Skip normal extraction
          return;
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
          // Pass caller's address history for fuzzy matching
          pickup_history: callerPickupAddresses || [],
          dropoff_history: callerDropoffAddresses || [],
        }),
      });

      if (!response.ok) {
        console.error(`[${callId}] Extraction API error: ${response.status}`);
        return;
      }

      let extracted = await response.json();
      console.log(`[${callId}] 📦 AI Extracted:`, extracted);

      // FUZZY MATCH CLARIFICATION: If taxi-extract flagged addresses as needing clarification,
      // inject a prompt for Ada to ask the customer to confirm
      if (extracted.pickup_needs_clarification && extracted.pickup_fuzzy_match) {
        console.log(`[${callId}] 🔍 Pickup needs fuzzy clarification: "${extracted.pickup_location}" vs known "${extracted.pickup_fuzzy_match}"`);
        
        // Store the fuzzy match info for Ada to use
        const spokenAddr = extracted.pickup_location;
        const knownAddr = extracted.pickup_fuzzy_match;
        
        // Inject clarification prompt into Ada's context
        if (openaiWs?.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ 
                type: "input_text", 
                text: `[SYSTEM CLARIFICATION REQUIRED: The customer said "${spokenAddr}" but this looks like a fuzzy match to their known address "${knownAddr}". Ask: "Just to check - did you mean ${knownAddr} (your usual address), or is ${spokenAddr} a different location?" Wait for confirmation before proceeding.]` 
              }]
            }
          }));
          
          // Set state to await clarification
          awaitingClarificationFor = "pickup";
          
          // Don't update knownBooking.pickup yet - wait for clarification
          extracted.pickup_location = null;
        }
      }
      
      if (extracted.dropoff_needs_clarification && extracted.dropoff_fuzzy_match) {
        console.log(`[${callId}] 🔍 Dropoff needs fuzzy clarification: "${extracted.dropoff_location}" vs known "${extracted.dropoff_fuzzy_match}"`);
        
        // Store the fuzzy match info for Ada to use
        const spokenAddr = extracted.dropoff_location;
        const knownAddr = extracted.dropoff_fuzzy_match;
        
        // Inject clarification prompt into Ada's context
        if (openaiWs?.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ 
                type: "input_text", 
                text: `[SYSTEM CLARIFICATION REQUIRED: The customer said "${spokenAddr}" but this looks like a fuzzy match to their known destination "${knownAddr}". Ask: "Just to confirm - did you mean ${knownAddr}, or is ${spokenAddr} somewhere different?" Wait for confirmation before proceeding.]` 
              }]
            }
          }));
          
          // Set state to await clarification (only if not already awaiting pickup)
          if (!awaitingClarificationFor) {
            awaitingClarificationFor = "destination";
          }
          
          // Don't update knownBooking.destination yet - wait for clarification
          extracted.dropoff_location = null;
        }
      }

      // CRITICAL: If we're awaiting clarification for a specific address, route postcode/outcode answers correctly
      // This prevents "CV12BW" from becoming destination when we asked for pickup postcode
      const postcodePattern = /^[A-Z]{1,2}\d{1,2}[A-Z]?\s*\d?[A-Z]*$/i;
      const looksLikePostcodeOnly = postcodePattern.test(transcript.trim());
      
      if (awaitingClarificationFor && looksLikePostcodeOnly) {
        const postcode = transcript.trim().toUpperCase();
        console.log(`[${callId}] 📮 Postcode/outcode detected: "${postcode}" - routing to ${awaitingClarificationFor}`);
        
        // Extract city from the postcode outcode (e.g., CV1 → Coventry)
        const outcodeMatch = postcode.match(/^([A-Z]{1,2}\d{1,2}[A-Z]?)/i);
        if (outcodeMatch) {
          const outcode = outcodeMatch[1].toUpperCase();
          const cityFromPostcode = getCityFromOutcode(outcode);
          if (cityFromPostcode && cityFromPostcode !== callerCity) {
            console.log(`[${callId}] 🏙️ City detected from postcode ${outcode}: ${cityFromPostcode} (was: ${callerCity || 'none'})`);
            callerCity = cityFromPostcode;
            // Update known_areas for this city
            callerKnownAreas[cityFromPostcode] = (callerKnownAreas[cityFromPostcode] || 0) + 1;
          }
        }
        
        if (awaitingClarificationFor === "pickup" && knownBooking.pickup) {
          // Append postcode to pickup address
          const updatedPickup = `${knownBooking.pickup}, ${postcode}`;
          console.log(`[${callId}] 📮 Appending postcode to pickup: "${knownBooking.pickup}" → "${updatedPickup}"`);
          knownBooking.pickup = updatedPickup;
          knownBooking.pickupVerified = false; // Re-verify with full address
          extracted.pickup_location = updatedPickup;
          // Clear any destination extraction that might have been wrong
          if (extracted.dropoff_location && postcodePattern.test(extracted.dropoff_location)) {
            console.log(`[${callId}] 📮 Clearing mis-extracted destination: "${extracted.dropoff_location}"`);
            extracted.dropoff_location = null;
          }
        } else if (awaitingClarificationFor === "destination" && knownBooking.destination) {
          // Append postcode to destination address
          const updatedDest = `${knownBooking.destination}, ${postcode}`;
          console.log(`[${callId}] 📮 Appending postcode to destination: "${knownBooking.destination}" → "${updatedDest}"`);
          knownBooking.destination = updatedDest;
          knownBooking.destinationVerified = false; // Re-verify with full address
          extracted.dropoff_location = updatedDest;
          // Clear any pickup extraction that might have been wrong
          if (extracted.pickup_location && postcodePattern.test(extracted.pickup_location)) {
            console.log(`[${callId}] 📮 Clearing mis-extracted pickup: "${extracted.pickup_location}"`);
            extracted.pickup_location = null;
          }
        }
        
        // Clear the clarification state since we got an answer
        awaitingClarificationFor = null;
      } else if (awaitingClarificationFor && !looksLikePostcodeOnly) {
        // We asked for a postcode but user provided something else (probably repeated the address)
        // Check if user just repeated the same address without adding clarification
        const currentAddress = awaitingClarificationFor === "pickup" ? knownBooking.pickup : knownBooking.destination;
        const newExtractedAddress = awaitingClarificationFor === "pickup" ? extracted.pickup_location : extracted.dropoff_location;
        
        // Normalize for comparison
        const normalizedCurrent = normalize(currentAddress || "");
        const normalizedNew = normalize(newExtractedAddress || "");
        const normalizedTranscript = normalize(transcript || "");
        
        // Check if user is providing something useful
        const hasNewPostcode = /[A-Z]{1,2}\d{1,2}[A-Z]?\s*\d[A-Z]{2}/i.test(transcript);
        const hasLandmarkCue = /\b(near|by|next\s+to|opposite|behind|landmark|postcode|area)\b/i.test(transcript.toLowerCase());
        // Check if transcript contains a city name (indicates they're clarifying the area)
        const containsCityName = /\b(birmingham|coventry|london|manchester|liverpool|leeds|sheffield|bristol|nottingham|leicester|derby|wolverhampton|dudley|walsall|solihull|sandwell|nuneaton|bedworth|rugby)\b/i.test(transcript.toLowerCase());
        
        // Check if user is providing a COMPLETELY DIFFERENT address (correction) vs repeating the same one
        const currentCore = normalizedCurrent.split(',')[0].replace(/\d+[a-z]?\s*/gi, '').trim(); // Strip house number for core comparison
        const transcriptCore = normalizedTranscript.replace(/\d+[a-z]?\s*/gi, '').trim();
        
        // If transcript looks similar to current address (or is very short), user is probably just repeating
        const isSimilarAddress = normalizedCurrent && (
          normalizedTranscript.includes(currentCore) || 
          currentCore.includes(transcriptCore) ||
          transcriptCore.length < 10 // Very short response like "is 52A David Road"
        );
        
        // If user gave a completely different address, let it through for correction
        const isDifferentAddress = normalizedNew && normalizedCurrent && 
          !normalizedNew.includes(currentCore) && !currentCore.includes(normalizedNew.split(',')[0]);
        
        if (isDifferentAddress && !hasNewPostcode && !hasLandmarkCue && !containsCityName) {
          // User is giving a DIFFERENT address entirely - this is a correction, not a clarification response
          // Let it through but keep awaiting clarification for the new address too
          console.log(`[${callId}] 📝 User gave different address "${newExtractedAddress}" - treating as correction, will verify new address`);
          awaitingClarificationFor = null; // Clear for now, geocoder will re-set if needed
        } else if (!hasNewPostcode && !hasLandmarkCue && !containsCityName) {
          // User didn't provide postcode, landmark, city, or new address - re-ask
          console.log(`[${callId}] 🔄 User responded "${transcript}" but didn't provide postcode/landmark/area - re-asking for clarification`);
          console.log(`[${callId}] 🔄 isSimilar=${isSimilarAddress}, isDifferent=${isDifferentAddress}, currentCore="${currentCore}", transcriptCore="${transcriptCore}"`);
          
          // Don't update the booking with the repeated/similar address
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{ 
                  type: "input_text", 
                  text: `[SYSTEM: The customer said "${transcript}" but did NOT provide the postcode or area you asked for. You MUST ask again: "I still need the postcode or area for ${currentAddress} please - could you give me the first part of the postcode, like CV1 or B27?" Do NOT proceed without a postcode or area name.]`
                }]
              }
            }));
            
            // Trigger response
            if (!openAiResponseInProgress) {
              openAiResponseInProgress = true;
              startResponseTimeout();
              openaiWs.send(JSON.stringify({ type: "response.create", response: { modalities: ["audio", "text"] } }));
            } else {
              deferredResponseCreate = { response: { modalities: ["audio", "text"] } as Record<string, unknown>, label: "postcode_re-ask" };
            }
          }
          
          // Keep awaitingClarificationFor set - don't clear it
          // Don't update the booking with the repeated address
          extracted.pickup_location = null;
          extracted.dropoff_location = null;
        } else {
          // User provided something useful (postcode, landmark, or city) - clear clarification state
          console.log(`[${callId}] ✅ User provided clarification: postcode=${hasNewPostcode}, landmark=${hasLandmarkCue}, city=${containsCityName}`);
          awaitingClarificationFor = null;
        }
      }

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

      // Customers often correct an address even if Ada asked a different question.
      // We MUST allow these corrections through or we end up ignoring the fix and confusing the caller.
      const isAddressCorrection = /(address\s+.*wrong|got\s+.*wrong|that's\s+wrong|no\s*,?\s*(it'?s|its)\b|i\s+meant\b|i\s+said\b|actually\b)/i.test(transcriptLower);
      const extractedHasBothAddresses = Boolean(extracted.pickup_location && extracted.dropoff_location);

      if (extracted.pickup_location) {
        // If Ada was asking for passengers/time and the user didn't provide passengers/time,
        // don't let a stray pickup overwrite the current booking.
        // EXCEPTION: if the customer is clearly correcting an address, always accept the correction.
        if (((expectingPassengers && !extracted.number_of_passengers) || (expectingTime && !extracted.pickup_time)) &&
            !containsExplicitPickupCue &&
            !isAddressCorrection &&
            !extractedHasBothAddresses) {
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
            
            // ===== EARLY AREA DISAMBIGUATION CHECK =====
            // Check immediately if this address exists in multiple areas
            await checkAndTriggerAreaDisambiguation(newPickup, "pickup");
          }
        }
      }

      if (extracted.dropoff_location) {
        // If Ada was asking for passengers/time and the user didn't provide passengers/time,
        // don't let a stray "location" answer overwrite destination.
        // EXCEPTION: if the customer is clearly correcting an address, always accept the correction.
        if (((expectingPassengers && !extracted.number_of_passengers) || (expectingTime && !extracted.pickup_time)) &&
            !containsExplicitDestinationCue &&
            !isAddressCorrection &&
            !extractedHasBothAddresses) {
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
            
            // ===== EARLY AREA DISAMBIGUATION CHECK =====
            // Check immediately if this address exists in multiple areas
            await checkAndTriggerAreaDisambiguation(newDestination, "destination");
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
      // Track luggage extraction
      if (extracted.luggage) {
        if (extracted.luggage === "CLEAR") {
          knownBooking.luggage = undefined;
          console.log(`[${callId}] 🧳 Luggage cleared`);
        } else {
          knownBooking.luggage = extracted.luggage;
          console.log(`[${callId}] 🧳 LUGGAGE EXTRACTED: "${extracted.luggage}"`);
          
          // Add to transcript for visibility
          transcriptHistory.push({
            role: "system",
            text: `🧳 Luggage: ${extracted.luggage}`,
            timestamp: new Date().toISOString()
          });
          queueLiveCallBroadcast({
            luggage: extracted.luggage
          });
        }
      }

      // Check if anything changed
      const pickupChanged = before.pickup !== knownBooking.pickup && knownBooking.pickup;
      const destinationChanged = before.destination !== knownBooking.destination && knownBooking.destination;
      const passengersChanged = before.passengers !== knownBooking.passengers && knownBooking.passengers;
      const timeChanged = before.pickupTime !== knownBooking.pickupTime && knownBooking.pickupTime;

      // TRAVEL HUB LUGGAGE CHECK: If destination or pickup is an airport/station, inject mandatory luggage prompt
      const tripHasTravelHub = isTravelHub(knownBooking.pickup) || isTravelHub(knownBooking.destination);
      console.log(`[${callId}] 🔍 Travel hub check: pickup="${knownBooking.pickup}" dest="${knownBooking.destination}" isTravelHub=${tripHasTravelHub} luggage="${knownBooking.luggage}" asked=${knownBooking.luggageAsked}`);
      
      if (tripHasTravelHub && !knownBooking.luggage && !knownBooking.luggageAsked) {
        // Track luggage question attempts
        knownBooking.luggageAttempts = (knownBooking.luggageAttempts || 0) + 1;
        
        // Check if we've asked too many times
        if (knownBooking.luggageAttempts >= MAX_CLARIFICATION_ATTEMPTS) {
          console.log(`[${callId}] 🛑 Max luggage question attempts (${MAX_CLARIFICATION_ATTEMPTS}) reached - assuming no luggage`);
          knownBooking.luggage = "0 bags";
          knownBooking.luggageAsked = true;
          
          transcriptHistory.push({
            role: "system",
            text: `⚠️ Max attempts for luggage - assuming no luggage`,
            timestamp: new Date().toISOString()
          });
          queueLiveCallBroadcast({});
        } else {
          console.log(`[${callId}] ✈️ TRAVEL HUB DETECTED - FORCING luggage question NOW (attempt ${knownBooking.luggageAttempts}/${MAX_CLARIFICATION_ATTEMPTS})`);
          knownBooking.luggageAsked = true;
          
          // Add to transcript for visibility
          transcriptHistory.push({
            role: "system",
            text: `✈️ Travel hub detected - asking about luggage (attempt ${knownBooking.luggageAttempts})`,
            timestamp: new Date().toISOString()
          });
          queueLiveCallBroadcast({});
          
          // FORCE Ada to ask about luggage IMMEDIATELY by injecting STRONG instruction
          // This must happen BEFORE she summarizes/confirms the booking
          if (openaiWs?.readyState === WebSocket.OPEN) {
            // Inject as a high-priority instruction that Ada MUST follow
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{ 
                  type: "input_text", 
                  text: `[STOP - MANDATORY LUGGAGE CHECK] This trip is to/from an airport or station. You MUST ask about luggage RIGHT NOW before doing anything else. Do NOT summarize the booking. Do NOT ask "shall I book?". Simply ask: "How many bags will you have for this trip?" Wait for their answer before proceeding.` 
                }]
              }
            }));
            console.log(`[${callId}] ✈️ Injected mandatory luggage question`);
          }
        }
      }

      if (pickupChanged || destinationChanged || passengersChanged || timeChanged) {
        console.log(`[${callId}] ✅ Known booking updated via AI extraction:`, knownBooking);
        queueLiveCallBroadcast({
          pickup: knownBooking.pickup,
          destination: knownBooking.destination,
          passengers: knownBooking.passengers,
        });

        // GEOCODE NEW ADDRESSES (if geocoding is enabled)
        // Also check for ambiguous addresses (multiple matches) when caller has no history
        if (geocodingEnabled) {
          // Check for ambiguous addresses only if caller has no booking history
          const shouldCheckAmbiguous = callerTotalBookings === 0;
          
          // Geocode pickup if it changed and hasn't been verified yet
          if (pickupChanged && !knownBooking.pickupVerified) {
            // HOUSE NUMBER CHECK: If it's a street address without house number, ask for it
            if (pickupNeedsHouseNumber(knownBooking.pickup!)) {
              askForHouseNumber("pickup", knownBooking.pickup!);
              return; // Wait for house number before proceeding
            }
            
            // KNOWN ADDRESS CHECK: Auto-verify if caller has used this address before (trusted OR pickup/dropoff history)
            const knownPickup = matchesKnownAddress(knownBooking.pickup!) || matchesTrustedAddress(knownBooking.pickup!);
            if (knownPickup) {
              knownBooking.pickup = knownPickup; // Use the enriched version with city
              knownBooking.pickupVerified = true;
              console.log(`[${callId}] 🏠 Pickup auto-verified (known address): "${knownBooking.pickup}" → "${knownPickup}"`);
            } else {
              // DUAL-SOURCE COMPARISON: Capture both STT and Ada's interpretation for comparison
              const extractedPickup = knownBooking.pickup!;
              const adaPickup = extractAddressFromAdaResponse("pickup");
              
              // Log both sources for comparison (but trust STT for geocoding)
              if (adaPickup && normalize(adaPickup) !== normalize(extractedPickup)) {
                console.log(`[${callId}] 📊 DUAL-SOURCE COMPARISON:`);
                console.log(`[${callId}]    STT extracted: "${extractedPickup}"`);
                console.log(`[${callId}]    Ada said: "${adaPickup}"`);
                
                // Add comparison to transcript for UI visibility
                transcriptHistory.push({
                  role: "system",
                  text: `📊 ADDRESS SOURCES - STT: "${extractedPickup}" | Ada: "${adaPickup}"`,
                  timestamp: new Date().toISOString()
                });
                queueLiveCallBroadcast({});
              }
              
              // GEOCODE THE EXTRACTED ADDRESS - trust STT, ask for clarification if it fails
              let pickupResult = await geocodeAddress(extractedPickup, shouldCheckAmbiguous, "pickup");
              let usedAddress = extractedPickup;
              
              // Clear any stale alternatives
              knownBooking.pickupAlternative = undefined;
              
              if (pickupResult.found) {
                // Check if there are multiple matches and caller has no history AND no other address to use as bias
                const hasLocationContext = knownBooking.destination || callerLastPickup || callerLastDestination;
                if (pickupResult.multiple_matches && pickupResult.multiple_matches.length > 1 && shouldCheckAmbiguous && !hasLocationContext) {
                  console.log(`[${callId}] 🔀 Multiple pickup matches found (no context) - asking for clarification`);
                  askForAddressDisambiguation("pickup", pickupResult.multiple_matches);
                  return; // Wait for customer to clarify
                }

                // USE GOOGLE'S CORRECTED ADDRESS - BUT ONLY IF IT'S A SAFE "SPELLING" CORRECTION.
                // We MUST NOT replace a correct customer address with an unrelated Google match.
                const googleAddress = pickupResult.display_name || pickupResult.formatted_address;
                if (googleAddress && googleAddress !== usedAddress) {
                  const cleanGoogleAddress = googleAddress
                    .replace(/,\s*UK$/i, "")
                    .replace(/,\s*United Kingdom$/i, "")
                    .trim();

                  const safe =
                    isSafeAddressCorrection(extractedPickup, cleanGoogleAddress) ||
                    isSafeAddressCorrection(usedAddress, cleanGoogleAddress);

                  if (safe) {
                    console.log(`[${callId}] 🔧 Google fuzzy-corrected pickup (safe): "${usedAddress}" → "${cleanGoogleAddress}"`);
                    knownBooking.pickup = cleanGoogleAddress;
                    usedAddress = cleanGoogleAddress;

                    transcriptHistory.push({
                      role: "system",
                      text: `🔧 GOOGLE CORRECTED: "${extractedPickup}" → "${cleanGoogleAddress}"`,
                      timestamp: new Date().toISOString(),
                    });
                    queueLiveCallBroadcast({ pickup: cleanGoogleAddress });
                  } else {
                    console.log(`[${callId}] ⚠️ Google pickup mismatch (unsafe correction) - keeping customer input`, {
                      customer: extractedPickup,
                      usedAddress,
                      google: cleanGoogleAddress,
                    });

                    transcriptHistory.push({
                      role: "system",
                      text: `⚠️ GOOGLE MISMATCH (pickup): "${extractedPickup}" ≠ "${cleanGoogleAddress}" (not auto-applying). IMPORTANT: The customer already gave the street name — ask ONLY for postcode (or nearby landmark), do NOT ask for the street name again.`,
                      timestamp: new Date().toISOString(),
                    });
                    queueLiveCallBroadcast({});

                    // Treat as NOT verified and ask for postcode / clarification.
                    notifyGeocodeResult("pickup", knownBooking.pickup!, false);
                    return;
                  }
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
            // KNOWN ADDRESS CHECK: Auto-verify if caller has used this address before (trusted OR pickup/dropoff history)
            const knownDestination = matchesKnownAddress(knownBooking.destination!) || matchesTrustedAddress(knownBooking.destination!);
            if (knownDestination) {
              knownBooking.destination = knownDestination; // Use the enriched version with city
              knownBooking.destinationVerified = true;
              console.log(`[${callId}] 🏠 Destination auto-verified (known address): "${knownBooking.destination}" → "${knownDestination}"`);
            } else {
              // DUAL-SOURCE COMPARISON: Capture both STT and Ada's interpretation for comparison
              const extractedDest = knownBooking.destination!;
              const adaDest = extractAddressFromAdaResponse("destination");
              
              // Log both sources for comparison (but trust STT for geocoding)
              if (adaDest && normalize(adaDest) !== normalize(extractedDest)) {
                console.log(`[${callId}] 📊 DUAL-SOURCE COMPARISON:`);
                console.log(`[${callId}]    STT extracted: "${extractedDest}"`);
                console.log(`[${callId}]    Ada said: "${adaDest}"`);
                
                // Add comparison to transcript for UI visibility
                transcriptHistory.push({
                  role: "system",
                  text: `📊 ADDRESS SOURCES - STT: "${extractedDest}" | Ada: "${adaDest}"`,
                  timestamp: new Date().toISOString()
                });
                queueLiveCallBroadcast({});
              }
              
              // GEOCODE THE EXTRACTED ADDRESS - trust STT, ask for clarification if it fails
              let destResult = await geocodeAddress(extractedDest, shouldCheckAmbiguous, "destination");
              let usedAddress = extractedDest;
              
              // Clear any stale alternatives
              knownBooking.destinationAlternative = undefined;
              
              if (destResult.found) {
                // Check if there are multiple matches and no location context to help disambiguate
                const hasLocationContext = knownBooking.pickup || callerLastPickup || callerLastDestination;
                if (destResult.multiple_matches && destResult.multiple_matches.length > 1 && shouldCheckAmbiguous && !hasLocationContext) {
                  console.log(`[${callId}] 🔀 Multiple destination matches found (no context) - asking for clarification`);
                  askForAddressDisambiguation("destination", destResult.multiple_matches);
                  return; // Wait for customer to clarify
                }

                // USE GOOGLE'S CORRECTED ADDRESS - BUT ONLY IF IT'S A SAFE "SPELLING" CORRECTION.
                const googleAddress = destResult.display_name || destResult.formatted_address;
                if (googleAddress && googleAddress !== usedAddress) {
                  const cleanGoogleAddress = googleAddress
                    .replace(/,\s*UK$/i, "")
                    .replace(/,\s*United Kingdom$/i, "")
                    .trim();

                  const safe =
                    isSafeAddressCorrection(extractedDest, cleanGoogleAddress) ||
                    isSafeAddressCorrection(usedAddress, cleanGoogleAddress);

                  if (safe) {
                    console.log(`[${callId}] 🔧 Google fuzzy-corrected destination (safe): "${usedAddress}" → "${cleanGoogleAddress}"`);
                    knownBooking.destination = cleanGoogleAddress;
                    usedAddress = cleanGoogleAddress;

                    transcriptHistory.push({
                      role: "system",
                      text: `🔧 GOOGLE CORRECTED: "${extractedDest}" → "${cleanGoogleAddress}"`,
                      timestamp: new Date().toISOString(),
                    });
                    queueLiveCallBroadcast({ destination: cleanGoogleAddress });
                  } else {
                    console.log(`[${callId}] ⚠️ Google destination mismatch (unsafe correction) - keeping customer input`, {
                      customer: extractedDest,
                      usedAddress,
                      google: cleanGoogleAddress,
                    });

                     transcriptHistory.push({
                       role: "system",
                       text: `⚠️ GOOGLE MISMATCH (destination): "${extractedDest}" ≠ "${cleanGoogleAddress}" (not auto-applying). IMPORTANT: The customer already gave the place name — ask ONLY for postcode / area (or a nearby landmark), do NOT ask them to repeat the name unless they correct it.`,
                       timestamp: new Date().toISOString(),
                     });
                    queueLiveCallBroadcast({});

                    notifyGeocodeResult("destination", knownBooking.destination!, false);
                    return;
                  }
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
        // This ensures Ada uses the EXACT extracted/verified addresses
        // Address conflicts are auto-resolved by geocoding - the valid one wins
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
              role: "assistant", // Using assistant role so it doesn't trigger a new response
              content: [{ type: "text", text: `[Internal note: ${contextUpdate}]` }],
            },
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
      queueLiveCallBroadcast({
        pickup: knownBooking.pickup,
        destination: knownBooking.destination,
        passengers: knownBooking.passengers,
      });
    }
  };

  // When audio is committed (server VAD or manual commit), we expect a response.
  // Some clients still send manual commit; we defensively trigger response.create after STT completes.
  let awaitingResponseAfterCommit = false;
  let responseCreatedSinceCommit = false;

  // OpenAI Realtime can only have ONE active response at a time.
  // If we try to create a new response while one is still in progress, OpenAI returns:
  // "conversation_already_has_active_response".
  // We guard against that by deferring response.create until we see response.done.
  let openAiResponseInProgress = false;
  let openAiResponseStartedAt = 0; // Timestamp when response started (for timeout detection)
  let deferredResponseCreate: { response: Record<string, unknown>; label: string } | null = null;

  // ═══════════════════════════════════════════════════════════════════════════
  // FLOW SAFEGUARDS - Prevent Ada from getting stuck or cutting out
  // ═══════════════════════════════════════════════════════════════════════════

  // Safeguard 1: Response timeout - auto-reset openAiResponseInProgress if stuck
  const RESPONSE_TIMEOUT_MS = 8000; // 8 seconds max for any response
  let responseTimeoutTimer: number | null = null;

  // Safeguard 2: Transcription timeout - handle audio commit with no transcript
  const TRANSCRIPTION_TIMEOUT_MS = 5000; // 5 seconds to get transcript after audio commit
  let transcriptionTimeoutTimer: number | null = null;
  let lastAudioCommitItemId: string | null = null;

  // Safeguard 3: Disambiguation timeout - clear stale disambiguation
  const DISAMBIGUATION_TIMEOUT_MS = 12000; // 12 seconds max to wait for disambiguation response
  let disambiguationTimeoutTimer: number | null = null;

  // Safeguard 4: Tool block response retry - ensure Ada speaks after tool blocks
  const TOOL_BLOCK_RETRY_MS = 4000; // 4 seconds to retry response after tool block
  let toolBlockRetryTimer: number | null = null;

  // Safeguard 5: Heartbeat - periodic status logging for debugging
  const HEARTBEAT_INTERVAL_MS = 30000; // Log status every 30 seconds
  let heartbeatTimer: number | null = null;
  let lastHeartbeatState = "";

  // Clear all flow safeguard timers
  const clearFlowSafeguardTimers = () => {
    if (responseTimeoutTimer !== null) { clearTimeout(responseTimeoutTimer); responseTimeoutTimer = null; }
    if (transcriptionTimeoutTimer !== null) { clearTimeout(transcriptionTimeoutTimer); transcriptionTimeoutTimer = null; }
    if (disambiguationTimeoutTimer !== null) { clearTimeout(disambiguationTimeoutTimer); disambiguationTimeoutTimer = null; }
    if (toolBlockRetryTimer !== null) { clearTimeout(toolBlockRetryTimer); toolBlockRetryTimer = null; }
    if (heartbeatTimer !== null) { clearTimeout(heartbeatTimer); heartbeatTimer = null; }
  };

  // Safeguard 1: Start response timeout when AI begins responding
  const startResponseTimeout = () => {
    if (responseTimeoutTimer !== null) clearTimeout(responseTimeoutTimer);
    openAiResponseStartedAt = Date.now();
    
    responseTimeoutTimer = setTimeout(() => {
      if (openAiResponseInProgress && !callEnded) {
        const elapsed = Date.now() - openAiResponseStartedAt;
        console.log(`[${callId}] ⚠️ SAFEGUARD 1: Response timeout after ${elapsed}ms - resetting openAiResponseInProgress`);
        openAiResponseInProgress = false;
        
        // Flush any deferred response
        if (deferredResponseCreate && sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
          const { response, label } = deferredResponseCreate;
          deferredResponseCreate = null;
          openAiResponseInProgress = true;
          startResponseTimeout(); // Re-arm for the new response
          openaiWs.send(JSON.stringify({ type: "response.create", response }));
          console.log(`[${callId}] >>> response.create sent (timeout recovery: ${label})`);
        }
      }
    }, RESPONSE_TIMEOUT_MS);
  };

  // Safeguard 1: Clear response timeout when AI finishes
  const clearResponseTimeout = () => {
    if (responseTimeoutTimer !== null) {
      clearTimeout(responseTimeoutTimer);
      responseTimeoutTimer = null;
    }
    openAiResponseStartedAt = 0;
  };

  // Safeguard 2: Start transcription timeout after audio commit
  const startTranscriptionTimeout = (itemId: string) => {
    if (transcriptionTimeoutTimer !== null) clearTimeout(transcriptionTimeoutTimer);
    lastAudioCommitItemId = itemId;
    
    transcriptionTimeoutTimer = setTimeout(() => {
      if (awaitingResponseAfterCommit && !responseCreatedSinceCommit && !callEnded) {
        console.log(`[${callId}] ⚠️ SAFEGUARD 2: Transcription timeout for item ${itemId} - prompting "didn't catch that"`);
        
        // Prompt Ada to say she didn't catch that
        if (sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: "[SYSTEM: Audio was received but transcription failed. Say: 'Sorry, I didn\'t quite catch that. Could you repeat that for me?']" }]
            }
          }));
          
          if (!openAiResponseInProgress) {
            openAiResponseInProgress = true;
            startResponseTimeout();
            openaiWs.send(JSON.stringify({ type: "response.create", response: { modalities: ["audio", "text"] } }));
            console.log(`[${callId}] >>> response.create sent (transcription timeout recovery)`);
          }
        }
        
        awaitingResponseAfterCommit = false;
        responseCreatedSinceCommit = true;
      }
    }, TRANSCRIPTION_TIMEOUT_MS);
  };

  // Safeguard 2: Clear transcription timeout when transcript received
  const clearTranscriptionTimeout = () => {
    if (transcriptionTimeoutTimer !== null) {
      clearTimeout(transcriptionTimeoutTimer);
      transcriptionTimeoutTimer = null;
    }
    lastAudioCommitItemId = null;
  };

  // Safeguard 3: Start disambiguation timeout
  const startDisambiguationTimeout = () => {
    if (disambiguationTimeoutTimer !== null) clearTimeout(disambiguationTimeoutTimer);
    
    disambiguationTimeoutTimer = setTimeout(() => {
      if (pendingAreaDisambiguation && !callEnded) {
        console.log(`[${callId}] ⚠️ SAFEGUARD 3: Disambiguation timeout - clearing and continuing`);
        
        const roadName = pendingAreaDisambiguation.roadName;
        pendingAreaDisambiguation = null;
        
        // Tell Ada to continue without disambiguation
        if (sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
          openaiWs.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "message",
              role: "user",
              content: [{ type: "input_text", text: `[SYSTEM: The customer didn't respond to the area question for "${roadName}". Accept the address as-is and continue with the booking. Ask for the next piece of information you need.]` }]
            }
          }));
          
          if (!openAiResponseInProgress) {
            openAiResponseInProgress = true;
            startResponseTimeout();
            openaiWs.send(JSON.stringify({ type: "response.create", response: { modalities: ["audio", "text"] } }));
            console.log(`[${callId}] >>> response.create sent (disambiguation timeout recovery)`);
          }
        }
      }
    }, DISAMBIGUATION_TIMEOUT_MS);
  };

  // Safeguard 3: Clear disambiguation timeout
  const clearDisambiguationTimeout = () => {
    if (disambiguationTimeoutTimer !== null) {
      clearTimeout(disambiguationTimeoutTimer);
      disambiguationTimeoutTimer = null;
    }
  };

  // Safeguard 4: Start tool block retry timer
  const startToolBlockRetry = (context: string) => {
    if (toolBlockRetryTimer !== null) clearTimeout(toolBlockRetryTimer);
    
    toolBlockRetryTimer = setTimeout(() => {
      if (!callEnded && sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
        // Check if Ada has responded since the block
        if (!openAiResponseInProgress && !aiSpeaking) {
          console.log(`[${callId}] ⚠️ SAFEGUARD 4: Tool block retry for ${context} - Ada didn't respond, retriggering`);
          
          openAiResponseInProgress = true;
          startResponseTimeout();
          openaiWs.send(JSON.stringify({ type: "response.create", response: { modalities: ["audio", "text"] } }));
          console.log(`[${callId}] >>> response.create sent (tool block retry: ${context})`);
        }
      }
    }, TOOL_BLOCK_RETRY_MS);
  };

  // Safeguard 5: Start heartbeat logging
  const startHeartbeat = () => {
    if (heartbeatTimer !== null) clearTimeout(heartbeatTimer);
    
    const logHeartbeat = async () => {
      if (callEnded) return;
      
      const state = JSON.stringify({
        aiSpeaking,
        responseInProgress: openAiResponseInProgress,
        awaitingResponse: awaitingResponseAfterCommit,
        disambiguation: pendingAreaDisambiguation ? pendingAreaDisambiguation.roadName : null,
        hasBooking: !!knownBooking.pickup || !!knownBooking.destination,
        passengers: knownBooking.passengers,
        luggage: knownBooking.luggage
      });
      
      // Only log if state changed
      if (state !== lastHeartbeatState) {
        console.log(`[${callId}] 💓 Heartbeat: ${state}`);
        lastHeartbeatState = state;
      }
      
      // Check for stuck states
      if (openAiResponseInProgress && openAiResponseStartedAt > 0) {
        const elapsed = Date.now() - openAiResponseStartedAt;
        if (elapsed > RESPONSE_TIMEOUT_MS * 0.8) {
          console.log(`[${callId}] ⚠️ Heartbeat: Response running long (${elapsed}ms)`);
        }
      }
      
      // CRITICAL: Save session state periodically for reconnect resilience
      // This ensures resume data is always fresh if WebSocket disconnects abruptly
      if (greetingSent && transcriptHistory.length > 0) {
        await saveSessionForResume();
      }
      
      // Re-arm heartbeat
      heartbeatTimer = setTimeout(logHeartbeat, HEARTBEAT_INTERVAL_MS);
    };
    
    heartbeatTimer = setTimeout(logHeartbeat, HEARTBEAT_INTERVAL_MS);
  };

  const scheduleOpenAiReconnect = (reason: string) => {
    if (callEnded) return;
    if (openAiReconnectTimer) return; // already scheduled

    if (openAiReconnectAttempts >= OPENAI_MAX_RECONNECTS) {
      console.error(
        `[${callId}] ❌ OpenAI reconnect failed after ${openAiReconnectAttempts} attempts (reason: ${reason})`,
      );
      try {
        socket.send(
          JSON.stringify({
            type: "error",
            error: {
              type: "openai_reconnect_failed",
              message: `OpenAI reconnect failed: ${reason}`,
            },
            attempts: openAiReconnectAttempts,
          }),
        );
      } catch (_) {
        // ignore
      }
      forceHangup("openai_reconnect_failed");
      return;
    }

    openAiReconnectAttempts += 1;
    const delayMs = OPENAI_RECONNECT_BASE_DELAY_MS * openAiReconnectAttempts;

    console.log(
      `[${callId}] 🔁 Reconnecting to OpenAI in ${delayMs}ms (attempt ${openAiReconnectAttempts}/${OPENAI_MAX_RECONNECTS}) - ${reason}`,
    );

    // Reset readiness so we buffer audio until the new session.update is applied
    sessionReady = false;

    // If we already greeted / have an ongoing conversation, DO NOT re-greet.
    // Instead we will prime the new OpenAI session with transcript history after session.updated.
    if (greetingSent) {
      needsHistoryPriming = true;
    }

    openAiReconnectTimer = setTimeout(() => {
      openAiReconnectTimer = null;
      openAiClosingForReconnect = true;
      try {
        openaiWs?.close();
      } catch (_) {
        // ignore
      }
      connectToOpenAI();
    }, delayMs);
  };

  // Connect to OpenAI Realtime API
  const connectToOpenAI = () => {
    console.log(`[${callId}] Connecting to OpenAI Realtime API...`);
    
    openaiWs = new WebSocket(
      "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17",
      ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]
    );

    openaiWs.onopen = () => {
      openAiReconnectAttempts = 0;
      openAiClosingForReconnect = false;
      console.log(`[${callId}] Connected to OpenAI`);
    };

    openaiWs.onmessage = async (event) => {
      const data = JSON.parse(event.data);
      
      // Session created - send configuration
      if (data.type === "session.created") {
        console.log(`[${callId}] Session created. Server defaults:`, data.session);
        
        // Load UK locations from database (for disambiguation and country detection)
        await loadUkLocations();
        
        console.log(`[${callId}] Sending session.update...`);
        openaiWs?.send(JSON.stringify({
          type: "session.update",
          session: {
            modalities: ["text", "audio"],
            voice: agentConfig?.voice || "shimmer", // Use agent's voice
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { 
              model: "whisper-1"
            },
            // Server VAD - ultra-snappy response (reduced to 500ms)
            turn_detection: {
              type: "server_vad",
              threshold: agentConfig?.vad_threshold ?? 0.35,           // Lower = more sensitive
              prefix_padding_ms: agentConfig?.vad_prefix_padding_ms ?? 300,    // Minimal lead-in
              silence_duration_ms: agentConfig?.vad_silence_duration_ms ?? 500, // Fast turn-taking
              create_response: false,
              interrupt_response: agentConfig?.allow_interruptions ?? true
            },
            tools: [
              {
                type: "function",
                name: "book_taxi",
                description: "MANDATORY: You MUST call this function to book a taxi. You CANNOT confirm a booking or quote a fare without calling this function first. When the customer confirms they want to book (says 'yes', 'please', 'book it', etc.), call this function IMMEDIATELY and wait for the response before speaking. The response will contain the calculated fare and ETA which you MUST use in your confirmation message. DO NOT invent or guess fares - they come from this function.",
                parameters: {
                  type: "object",
                  properties: {
                    pickup: { type: "string", description: "Pickup location exactly as customer stated" },
                    destination: { type: "string", description: "Drop-off location exactly as customer stated" },
                    passengers: { type: "integer", description: "Number of passengers" },
                    pickup_time: { type: "string", description: "When the taxi is needed: 'ASAP' for immediate, or 'YYYY-MM-DD HH:MM' format for scheduled bookings" },
                    vehicle_type: { type: "string", description: "Optional: Vehicle type if customer requested (e.g., '6 seater', 'MPV', 'estate', 'saloon', 'minibus')" }
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
                description: "IMMEDIATELY modify an active booking when the customer wants to correct or update ANY detail. Use this when they say things like: 'wrong address', 'that's not right', 'actually it's...', 'can you update...', 'change the pickup to...', 'it should be 52A not 52'. DO NOT cancel and rebook - use modify_booking to update in place. This preserves booking history and is faster for the customer.",
                parameters: {
                  type: "object",
                  properties: {
                    new_pickup: { type: "string", description: "New pickup address - use when customer corrects the pickup (e.g., '52A David Road' instead of '52 David Road')" },
                    new_destination: { type: "string", description: "New destination address - use when customer corrects the destination" },
                    new_passengers: { type: "integer", description: "New number of passengers - use when customer corrects passenger count" },
                    new_vehicle_type: { type: "string", description: "New vehicle type (e.g., '6 seater', 'MPV', 'estate') - only if customer requests it" }
                  },
                  required: []
                }
              },
              {
                type: "function",
                name: "save_address_alias",
                description: "Save an address alias for the customer (e.g., 'home', 'work', 'office'). ONLY use when customer EXPLICITLY asks to save: 'save this as home', 'call this my work', 'remember this as office'. Do NOT use just because they mentioned 'home' - that's for resolving existing aliases. Always confirm before saving: 'Just to confirm, save [address] as your [alias]?'",
                parameters: {
                  type: "object",
                  properties: {
                    alias: { type: "string", description: "The alias name: 'home', 'work', 'office', 'gym', 'school', etc." },
                    address: { type: "string", description: "The full address to save under this alias" }
                  },
                  required: ["alias", "address"]
                }
              },
              {
                type: "function",
                name: "save_customer_name",
                description: "Save or UPDATE the customer's name. Call this: 1) When a NEW customer tells you their name (e.g., 'My name is John'), 2) When a RETURNING customer CORRECTS their name (e.g., 'Actually it's Sarah', 'My name isn't Max, it's Mike', 'Call me Dave'), 3) When you greet someone by wrong name and they correct you. Use the EXACT name they said - do NOT guess.",
                parameters: {
                  type: "object",
                  properties: {
                    name: { type: "string", description: "The customer's first name EXACTLY as they said it. Do not guess - only use the name they actually spoke." }
                  },
                  required: ["name"]
                }
              },
              {
                type: "function",
                name: "find_nearby_places",
                description: "Find nearby venues when customer asks for recommendations. Use for questions like 'where's a good restaurant?', 'know any nice pubs?', 'I need a hotel', 'what's good for a night out?', etc.",
                parameters: {
                  type: "object",
                  properties: {
                    category: { 
                      type: "string", 
                      enum: ["hotel", "restaurant", "cafe", "bar", "pub", "nightclub", "cinema", "theatre", "shopping"], 
                      description: "Type of venue: hotel, restaurant, cafe, bar, pub, nightclub, cinema, theatre, shopping" 
                    },
                    context_address: { type: "string", description: "Optional: Use pickup/destination as reference, or leave empty to use caller's city" }
                  },
                  required: ["category"]
                }
              }
            ],
            tool_choice: "auto",
            instructions: getEffectiveSystemPrompt()
          }
        }));
      }

      // Session updated - now ready
      if (data.type === "session.updated") {
        console.log(`[${callId}] Session ready! Effective config:`, data.session);
        
        // SAFEGUARD 5: Start heartbeat logging
        startHeartbeat();
        
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
              instructions: getEffectiveSystemPrompt() + activeBookingContext
            }
          }));
        }
        
        sessionReady = true;
        
        // CRITICAL: Clear any remnant audio buffer from bridge connection/previous call
        // This prevents Whisper from transcribing stale audio as phantom utterances like "Bye"
        console.log(`[${callId}] 🧹 Clearing audio buffer on session ready`);
        openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
        
        socket.send(JSON.stringify({ type: "session_ready" }));

        // Broadcast call started (only once)
        if (!greetingSent) {
          await broadcastLiveCall({ status: "active" });
        }

        // Trigger initial greeting ONLY ONCE
        if (greetingSent) {
          console.log(`[${callId}] ⏭️ Greeting already sent, skipping duplicate`);

          // If this session was resumed OR OpenAI reconnected, prime OpenAI with conversation history.
          if ((isResumedSession || needsHistoryPriming) && transcriptHistory.length > 0) {
            console.log(`[${callId}] 🔄 Priming OpenAI with history (resumed=${isResumedSession}, openai_reconnect=${needsHistoryPriming})`);
            needsHistoryPriming = false;
            isResumedSession = false;
            primeOpenAiWithHistory();
          }
          return;
        }
        greetingSent = true;

        // Priority: Active booking > Quick rebooking > Normal greeting
        let greetingPrompt: string;
        
        // Build address history context for AI to reference
        const addressHistoryContext = (callerPickupAddresses.length > 0 || callerDropoffAddresses.length > 0)
          ? `\n\nCALLER'S KNOWN ADDRESSES (use for matching - if they say an address that matches one of these, use the known version):
PICKUPS: ${callerPickupAddresses.length > 0 ? callerPickupAddresses.slice(-5).join('; ') : 'none'}
DROPOFFS: ${callerDropoffAddresses.length > 0 ? callerDropoffAddresses.slice(-5).join('; ') : 'none'}`
          : '';
        
        if (activeBooking) {
          // Use friendly place names if available, otherwise fall back to addresses
          const pickupDisplay = activeBooking.pickup_name || activeBooking.pickup;
          const destDisplay = activeBooking.destination_name || activeBooking.destination;
          
          // Customer has an outstanding booking - offer to cancel/keep
          greetingPrompt = `[Call connected - customer has an ACTIVE BOOKING. Their name is ${callerName || 'unknown'}. 
ACTIVE BOOKING DETAILS:
- Booking ID: ${activeBooking.id}
- Pickup: ${activeBooking.pickup}${activeBooking.pickup_name ? ` (${activeBooking.pickup_name})` : ''}
- Destination: ${activeBooking.destination}${activeBooking.destination_name ? ` (${activeBooking.destination_name})` : ''}
- Passengers: ${activeBooking.passengers}
- Fare: ${activeBooking.fare}
${addressHistoryContext}

Say EXACTLY: "Hello${callerName ? ` ${callerName}` : ''}! I can see you have an active booking from ${pickupDisplay} to ${destDisplay}. Would you like to keep that booking, or would you like to cancel it?"

Then WAIT for the customer to respond. Do NOT cancel until they explicitly say "cancel" or "cancel it".]`;
        } else if (callerName && callerLastDestination) {
          // Returning customer with usual destination - offer quick rebooking
          greetingPrompt = `[Call connected - greet the RETURNING customer by name and OFFER QUICK REBOOKING. Their name is ${callerName}. Their usual destination is ${callerLastDestination}${callerLastPickup ? ` and usual pickup is ${callerLastPickup}` : ''}.${addressHistoryContext}

Say: "Hello ${callerName}! Lovely to hear from you again. Shall I book you a taxi to ${callerLastDestination}, or are you heading somewhere different today?"]`;
        } else if (callerName) {
          // Returning customer without usual destination
          // CRITICAL: If they have NO pickup history, we need to ask for their area for geocoding context
          const hasPickupHistory = callerPickupAddresses.length > 0;
          if (hasPickupHistory) {
            greetingPrompt = `[Call connected - greet the RETURNING customer by name. Their name is ${callerName}.${addressHistoryContext}

Say: "Hello ${callerName}! Lovely to hear from you again. How can I help with your travels today?"]`;
          } else {
            // Caller has name but NO address history - need to get their area for geocoding
            greetingPrompt = `[Call connected - greet the RETURNING customer by name BUT they have NO pickup history so we need their area. Their name is ${callerName}.

Say: "Hello ${callerName}! Lovely to hear from you again. Just so I can find addresses near you, what area are you calling from - Coventry, Birmingham, or somewhere else?"
CRITICAL: Wait for them to answer the area question BEFORE proceeding with any booking details. Their area is needed for accurate address lookups.]`;
          }
        } else {
          // New customer - ask for name AND area to establish geocoding bias
          greetingPrompt = `[Call connected - greet the NEW customer and ask for their name AND area. Say: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please, and whereabouts are you calling from?"
          
IMPORTANT: Listen for BOTH their name AND their area (city/town like Coventry, Birmingham, etc). If they only give their name, follow up with "Lovely to meet you [name]! And whereabouts are you calling from - Coventry, Birmingham, or somewhere else?"]`;
          // Mark that we're awaiting area from new caller
          awaitingAreaResponse = true;
        }
        
        console.log(`[${callId}] Triggering initial greeting... (caller: ${callerName || 'new customer'})`);
        
        // CRITICAL: Send a short silence buffer BEFORE the greeting to prime the audio channel.
        // This ensures the telephony bridge's RTP stream is active and buffered before the AI
        // starts speaking, preventing "Hello" from being clipped to "lo" or "ello".
        // 
        // Audio format: PCM16 at 24kHz = 48000 bytes/sec = 4800 bytes per 100ms
        // We send ~300ms of silence (3 chunks of 4800 bytes each)
        const silenceChunkSize = 4800; // 100ms of silence at 24kHz PCM16
        const silenceChunks = 3; // 300ms total
        
        // Create silence buffer (zeros = silence in PCM16)
        const silenceBytes = new Uint8Array(silenceChunkSize);
        // Convert to base64
        let silenceBase64 = '';
        const chunkSize = 0x8000;
        for (let i = 0; i < silenceBytes.length; i += chunkSize) {
          const chunk = silenceBytes.subarray(i, Math.min(i + chunkSize, silenceBytes.length));
          silenceBase64 += String.fromCharCode.apply(null, Array.from(chunk));
        }
        silenceBase64 = btoa(silenceBase64);
        
        // Send silence chunks to prime the audio channel
        for (let i = 0; i < silenceChunks; i++) {
          socket.send(JSON.stringify({
            type: "audio",
            audio: silenceBase64,
          }));
        }
        console.log(`[${callId}] 🔇 Sent ${silenceChunks * 100}ms of silence to prime audio channel`);
        
        // Small additional delay to let the bridge process the silence
        await new Promise(resolve => setTimeout(resolve, 100));
        
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
        
        // Cancel any pending playback timeout from previous response
        if (aiPlaybackTimeoutId) {
          clearTimeout(aiPlaybackTimeoutId);
          aiPlaybackTimeoutId = null;
        }
        
        // Reset state for new response
        currentAssistantText = "";
        aiSpeaking = true;
        openAiResponseInProgress = true;
        aiPlaybackBytesTotal = 0;
        aiPlaybackStartedAt = 0;

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
        const deltaLen = data.delta?.length || 0;
        console.log(`[${callId}] >>> AUDIO DELTA received, length: ${deltaLen}`);
        
        // Track playback timing: mark start on first delta, accumulate bytes
        if (aiPlaybackBytesTotal === 0) {
          aiPlaybackStartedAt = Date.now();
        }
        aiPlaybackBytesTotal += deltaLen;
        
        socket.send(
          JSON.stringify({
            type: "audio",
            audio: data.delta,
          }),
        );
        
        // NON-BLOCKING: Broadcast AI audio to monitoring channel (no await = no jitter)
        void supabase.from("live_call_audio").insert({
          call_id: callId,
          audio_chunk: data.delta,
          audio_source: "ai",
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
          const now = Date.now();
          const transcriptText = String(data.transcript);
          
          // DEDUPLICATION: Skip if this is the same text we just saved (within 2s)
          // This prevents duplicates when OpenAI sends the same transcript multiple times
          if (transcriptText === lastSavedAssistantText && now - lastSavedAssistantAt < ASSISTANT_DEDUP_WINDOW_MS) {
            console.log(`[${callId}] 🔇 Skipping duplicate assistant transcript: "${transcriptText.substring(0, 50)}..."`);
            return;
          }
          
          lastSavedAssistantText = transcriptText;
          lastSavedAssistantAt = now;
          
          transcriptHistory.push({
            role: "assistant",
            text: transcriptText,
            timestamp: new Date().toISOString(),
          });
          // Broadcast transcript update (queued to preserve order)
          queueLiveCallBroadcast({});

          const a = String(data.transcript).toLowerCase();
          
          // LUGGAGE STATE TRACKING: If Ada asks about luggage, mark it
          if (!knownBooking.luggageAsked && 
              (a.includes("how many bags") || 
               a.includes("any luggage") || 
               a.includes("luggage today") ||
               a.includes("bags will you have"))) {
            knownBooking.luggageAsked = true;
            console.log(`[${callId}] 🧳 Ada asked about luggage - luggageAsked=true`);
          }
          
          // LUGGAGE EXTRACTION FROM ADA: If Ada confirms a luggage count, extract and save it
          // This catches cases where Ada infers/confirms luggage from user's partial response
          if (!knownBooking.luggage) {
            // Match patterns like "with 3 bags", "for 2 bags", "3 pieces of luggage"
            const adaLuggageMatch = a.match(/(?:with|for|have|carrying)\s+(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:bag|bags|piece|pieces|luggage)/i);
            if (adaLuggageMatch) {
              const numMap: { [key: string]: number } = { 
                'one': 1, 'two': 2, 'three': 3, 'four': 4, 'five': 5, 
                'six': 6, 'seven': 7, 'eight': 8, 'nine': 9, 'ten': 10 
              };
              const num = numMap[adaLuggageMatch[1].toLowerCase()] ?? parseInt(adaLuggageMatch[1]);
              if (num >= 0 && num <= 10) {
                knownBooking.luggage = num === 0 ? "no luggage" : `${num} bag${num > 1 ? 's' : ''}`;
                console.log(`[${callId}] 🧳 LUGGAGE EXTRACTED FROM ADA: "${knownBooking.luggage}"`);
                queueLiveCallBroadcast({ luggage: knownBooking.luggage });
              }
            }
            // Also check for "no luggage" confirmation
            if (a.includes("no luggage") || a.includes("no bags") || a.includes("without luggage")) {
              knownBooking.luggage = "no luggage";
              console.log(`[${callId}] 🧳 LUGGAGE EXTRACTED FROM ADA: "no luggage"`);
              queueLiveCallBroadcast({ luggage: knownBooking.luggage });
            }
          }

          // If Ada asked the "anything else" question, start a silence timeout so calls don't hang forever.
          // IMPORTANT: if we've already triggered the silence-timeout hangup sequence, don't re-arm.
          if (
            !followupHangupRequested &&
            (a.includes("is there anything else i can help") ||
              a.includes("anything else i can help"))
          ) {
            armFollowupSilenceTimeout("assistant_anything_else");
          }

          // If Ada asked a question and we get no detectable user activity, reprompt.
          // This fixes cases where VAD/STT doesn't pick up the customer's reply (quiet lines / mic issues).
          const looksLikeQuestion =
            a.includes("?") ||
            a.includes("what's your name") ||
            a.includes("when do you need") ||
            a.includes("where would you like") ||
            a.includes("where are you heading") ||
            a.includes("how many passengers") ||
            a.includes("shall i book");
          
          // Check if this is a question that should NOT be reprompted
          const isConfirmationQuestion = 
            a.includes("shall i book") || 
            a.includes("shall i confirm") ||
            a.includes("book that for you");
          
          // Active booking greeting questions - only ask once
          const isActiveBookingQuestion =
            a.includes("would you like to keep that booking") ||
            a.includes("would you like to cancel it") ||
            a.includes("keep that booking");
          
          // "Anything else?" already has its own dedicated timeout handler - don't double-arm
          const isAnythingElseQuestion = 
            a.includes("is there anything else") || 
            a.includes("anything else i can help");

          // Only arm reprompt if user hasn't already replied recently (prevents double-fire)
          // If there's been user activity in the last 3 seconds, the user DID reply - don't reprompt
          const recentUserActivity = lastUserActivityAt > 0 && now - lastUserActivityAt < 3000;
          
          // CRITICAL: Don't reprompt these questions:
          // - Confirmation questions ("shall I book that?") - asked once only
          // - Active booking questions ("would you like to keep?") - asked once only
          // - "Anything else?" - has its own dedicated timeout handler
          const skipReprompt = isConfirmationQuestion || isActiveBookingQuestion || isAnythingElseQuestion;
          
          if (looksLikeQuestion && !skipReprompt && !a.includes("goodbye") && !/\bbye\b/.test(a) && !recentUserActivity) {
            armNoReplyReprompt("assistant_question", transcriptText);
          }

          // Failsafe: if Ada says goodbye but forgets to call end_call, hang up reliably.
          // IMPORTANT: we delay the actual hangup so the goodbye audio is heard.
          if (/(^|\b)(goodbye|bye)(\b|$)/.test(a) || a.includes("have a great journey")) {
            clearTimer(goodbyeFailsafeTimer);
            goodbyeFailsafeTimer = setTimeout(() => {
              if (callEnded || endCallInProgress) return;
              console.log(`[${callId}] 🧯 Goodbye failsafe firing (no end_call detected)`);
              forceHangup("goodbye_failsafe", GOODBYE_AUDIO_GRACE_MS);
            }, GOODBYE_FAILSAFE_TRIGGER_MS);
          }
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
        console.log(`[${callId}] Raw user transcript: "${rawTranscript}" (length: ${rawTranscript.length})`);
        noteUserActivity("transcription.completed");
        
        // SAFEGUARD 2: Clear transcription timeout - we got a transcript
        clearTranscriptionTimeout();

        // Log empty/very short transcripts for debugging
        if (!rawTranscript || rawTranscript.trim().length < 2) {
          console.log(`[${callId}] ⚠️ Empty or very short transcript received - likely audio quality issue`);
          // Don't let any pending "committed" turn trigger an AI response.
          awaitingResponseAfterCommit = false;
          responseCreatedSinceCommit = true;
          return;
        }
        
        // === ECHO DETECTION (must run BEFORE broadcast to prevent phantom UI updates) ===
        const isEchoOfAda = (transcript: string): boolean => {
          if (!currentAssistantText) return false;
          const tLower = transcript.toLowerCase().trim();
          const ada = currentAssistantText.toLowerCase();
          
          // Check if user transcript is a substantial substring of Ada's recent speech
          // (at least 20 chars and appears in Ada's text)
          if (tLower.length >= 20 && ada.includes(tLower)) {
            console.log(`[${callId}] 🔇 ECHO DETECTED: User transcript matches Ada's speech`);
            return true;
          }
          
          // Check for Ada's signature phrases being echoed back
          const adaPhrases = ['247 radio carz', 'lovely to hear from you', 'shall i book that', 
            'is there anything else', 'have a great journey', 'your driver will be with you'];
          for (const phrase of adaPhrases) {
            if (tLower.includes(phrase)) {
              console.log(`[${callId}] 🔇 ECHO DETECTED: Ada phrase "${phrase}" in user transcript`);
              return true;
            }
          }
          
          return false;
        };
        
        // Check echo FIRST - don't broadcast phantom echoes
        if (isEchoOfAda(rawTranscript)) {
          console.log(`[${callId}] 🔇 Discarding echo transcript (pre-broadcast): "${rawTranscript}"`);
          awaitingResponseAfterCommit = false;
          responseCreatedSinceCommit = true;
          return;
        }

        // If Ada asked "anything else?" and the customer clearly said NO,
        // force a clean goodbye + end_call (prevents looping the follow-up question).
        // ALSO: if the customer explicitly says "bye/bye-bye/goodbye" at any time, end the call immediately.
        let forcedResponseInstructions: string | null = null;
        const lastAssistantTextLower = ([...transcriptHistory]
          .reverse()
          .find((m) => m.role === "assistant")?.text
          ?.toLowerCase() || "");

        const adaAskedAnythingElse =
          lastAssistantTextLower.includes("is there anything else i can help") ||
          lastAssistantTextLower.includes("anything else i can help");

        const t = rawTranscript.toLowerCase().trim();

        // Explicit farewells should always end the call (these are NOT ambiguous like "cheers")
        // IMPORTANT: Include common STT mishearings of "bye" like "you", "by", "buy"
        const explicitFarewellOnly = /^(bye|bye\s+bye|bye-bye|goodbye|see\s+you|see\s+ya|see-ya|by|buy|you)\b[!. ]*$/.test(t);
        
        // Catch "bye bye" anywhere in the transcript (e.g., "Thank you. Bye. Bye." or "bye bye")
        // This is always an intentional goodbye, regardless of booking state
        const containsByeBye = /\bbye[\s.,!]*bye\b/i.test(t) || /\bbye-bye\b/i.test(t);
        
        // Also catch post-booking farewells like "thank you bye", "cheers bye", "thanks bye"
        // Use activeBooking to check if a booking has been completed
        const thankYouBye = /\b(thank\s*you|thanks|cheers|ta)\s*(bye|goodbye|by|you)?\s*[!.]*$/i.test(t) && t.length < 30;
        
        // Catch "thank you + bye" pattern even without activeBooking (customer wants to leave mid-call)
        const thankYouWithBye = /\b(thank\s*you|thanks)\b.*\bbye\b/i.test(t) && t.length < 40;
        
        if (explicitFarewellOnly || containsByeBye || thankYouWithBye || (thankYouBye && activeBooking)) {
          console.log(`[${callId}] 👋 Explicit farewell detected: "${t}" (byeBye=${containsByeBye}, thankYouBye=${thankYouBye}, thankYouWithBye=${thankYouWithBye})`);
          forcedResponseInstructions =
            "The customer has said goodbye. Say a short polite goodbye, then IMMEDIATELY call the end_call tool with reason 'customer_request'. Do NOT ask any further questions.";
        }

        if (!forcedResponseInstructions && adaAskedAnythingElse) {
          // Expanded patterns for "no" responses - catch "no that's brilliant", "no no that's good", etc.
          const saidNo =
            /^no[\s,!.]*/.test(t) ||  // Starts with "no" followed by anything
            /^(nope|nah|na)\b/.test(t) ||
            /\b(no\s+thanks|no\s+thank\s+you|nothing\s+else|that'?s\s+all|that\s+is\s+all|all\s+good|i'?m\s+good|im\s+good|i'?m\s+fine|im\s+fine|that'?s\s+(brilliant|great|fine|good|perfect|lovely))\b/.test(t);
          
          // Only block if they're explicitly asking for something more
          const saidYes = /\b(yes|yeah|yep|actually|one more|another)\b/.test(t) && !/^no/.test(t);

          if (saidNo && !saidYes) {
            console.log(`[${callId}] 👋 Customer said no to "anything else": "${t}"`);
            forcedResponseInstructions =
              "The customer has said they do NOT need anything else. Say ONLY: 'You're welcome! Have a great journey, goodbye!' then IMMEDIATELY call the end_call tool with reason 'no_further_assistance'. Do NOT ask any further questions.";
          }
        }
        
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
          
          // Common Whisper hallucination phrases - expanded to catch video/podcast artifacts
          // IMPORTANT: Don't filter single words like "yes", "no", "ok" - those are valid responses!
          // Only filter obvious YouTube/podcast outros and artifacts
          const hallucinationPhrases = [
            /^silence\.?$/i,  // "Silence." - common Whisper hallucination on quiet audio
            /^\[silence\]$/i,  // "[Silence]" variant
            /^\.\.\.+$/,  // Just ellipsis
            // NOTE: "bye" and "goodbye" are handled contextually below, not as unconditional hallucinations
            /thank you for watching/i,
            /thanks for watching/i,
            /please subscribe/i,
            /like and subscribe/i,
            /see you (all )?(on the|in the|next)/i,  // "See you all on the next one" - common video outro
            /see you next time/i,
            /until next time/i,
            /catch you (later|next|on the)/i,
            /take care.+bye/i,  // "Take care, bye!" - outro phrase
            /\[music\]/i,
            /\[applause\]/i,
            /\[inaudible\]/i,
            /\[laughter\]/i,
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
          
          // CONTEXTUAL BYE/GOODBYE FILTER:
          // Only treat "bye" as hallucination if conversation is young (<2 user turns)
          // If user has spoken substantively, they might genuinely be saying goodbye
          const userTurnCount = transcriptHistory.filter(m => m.role === "user").length;
          const byePatterns = [/^bye\.?$/i, /^goodbye\.?$/i, /^bye[\s-]?bye\.?$/i];
          const isByePhrase = byePatterns.some(p => p.test(t));
          if (isByePhrase && userTurnCount < 2) {
            console.log(`[${callId}] 🚫 Hallucination detected: "${t}" appears too early (only ${userTurnCount} user turns)`);
            return true;
          }
          
          // Detect Welsh hallucinations - ONLY the specific pattern where Whisper
          // outputs Welsh sentence structure mixed with English addresses
          // This is a known Whisper artifact on noisy phone audio
          // Pattern: "gallaf bwcio taxi o [address] i [address]" (I can book taxi from...to...)
          const welshHallucinationPattern = /gallaf\s+bwcio\s+taxi\s+o\s+.+\s+i\s+/i;
          if (welshHallucinationPattern.test(t)) {
            console.log(`[${callId}] 🚫 Welsh hallucination detected (booking pattern): "${t}"`);
            return true;
          }
          
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
            // If the caller is speaking a non-English language/script (e.g., Urdu/Polish), don't discard it as "gibberish".
            // This guard is critical for multilingual support.
            if (/[^ -]/.test(text)) return false;

            const words = text.toLowerCase().split(/\s+/).filter(w => w.length > 0);
            if (words.length < 2 || words.length > 4) return false; // Only check short phrases
            
            // If it's 2-4 short words with no address indicators, it could be gibberish.
            const addressIndicators = /\d|road|street|avenue|lane|drive|close|way|place|court|station|airport|hospital|hotel|centre|center|park|square|building|house|flat/i;
            if (!addressIndicators.test(text)) {
              // Allow common "name" replies (these often look like short phrases and must NOT be discarded)
              const nameLike = /\b(my name|name(?:'s| is)|i'?m|i am|it'?s|this is|call me|speaking)\b/i;
              if (nameLike.test(text)) return false;

              // Check if it sounds like a command/action vs. gibberish
              const actionWords = /yes|no|please|cancel|book|taxi|pick|from|to|going|thank|okay|fine|great|right|correct|asap|now|later|three|two|one|four|five|six|passenger|people|name/i;
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
          // Don't process, don't save, don't forward, and don't trigger a reply.
          awaitingResponseAfterCommit = false;
          responseCreatedSinceCommit = true;
          return;
        }
        
        // CONTEXTUAL PHANTOM FILTER: Filter "Thank you." or "Bye." ONLY if:
        // It appears right after Ada stopped speaking (likely echo/phantom)
        const isContextualPhantom = (text: string): boolean => {
          const t = text.trim().toLowerCase();
          // These short phrases are often phantoms when they appear right after Ada speaks
          const phantomCandidates = ['thank you', 'thanks', 'bye', 'goodbye', 'cheers'];
          
          // Check if it's a short phantom-candidate phrase
          const isShortPhrase = phantomCandidates.some(p => t === p || t === p + '.');
          if (!isShortPhrase) return false;
          
          // Check if Ada just finished speaking (within 800ms) - likely a phantom/echo
          if (aiStoppedSpeakingAt > 0 && Date.now() - aiStoppedSpeakingAt < 800) {
            console.log(`[${callId}] 🔇 Contextual phantom: "${text}" appeared ${Date.now() - aiStoppedSpeakingAt}ms after Ada finished`);
            return true;
          }
          
          return false;
        };
        
        if (isContextualPhantom(rawTranscript)) {
          console.log(`[${callId}] 🔇 Skipping contextual phantom: "${rawTranscript}"`);
          awaitingResponseAfterCommit = false;
          responseCreatedSinceCommit = true;
          return;
        }
        
        // === ALL FILTERS PASSED - NOW BROADCAST ===
        // CRITICAL: We broadcast AFTER echo/hallucination/phantom filters to prevent phantom UI updates
        socket.send(JSON.stringify({
          type: "transcript",
          text: rawTranscript,
          role: "user",
        }));
        
        // Save to history now that we know it's valid
        transcriptHistory.push({
          role: "user",
          text: rawTranscript,
          timestamp: new Date().toISOString()
        });
        queueLiveCallBroadcast({});
        
        console.log(`[${callId}] User said: ${rawTranscript}`);
        lastFinalUserTranscript = rawTranscript;
        lastFinalUserTranscriptAt = Date.now();

        // Detect and lock the customer's language from their FIRST valid transcript.
        // This prevents Ada from defaulting to English when the caller speaks Urdu (or other non-English languages).
        if (!languageLocked) {
          const hint = detectLanguageHint(rawTranscript);
          if (hint) applyLanguageLock(hint);
        }

        // === NAME EXTRACTION & CORRECTION ===
        // Detect name corrections for returning callers (e.g., "I'm not Max, I'm Mike")
        const detectNameCorrection = (text: string): string | null => {
          const t = text.trim();
          const correctionPatterns = [
            // "That's not my name" / "I'm not Max" / "My name isn't Max"
            /(?:that's not my name|i'm not|my name(?:'s| is)n't|i am not|that is not my name)[,.\s]+(?:i'm|it's|my name(?:'s| is)|call me)\s+([A-Za-z]+)/i,
            // "Actually it's Sarah" / "It's actually Mike"
            /(?:actually|no)[,.\s]+(?:i'm|it's|my name(?:'s| is)|call me)\s+([A-Za-z]+)/i,
            // "Call me Dave instead" / "Just call me Sarah"
            /(?:just\s+)?call me\s+([A-Za-z]+)(?:\s+instead)?/i,
            // "No, I'm Sarah" / "No it's Mike"
            /^no[,.\s]+(?:i'm|it's|my name(?:'s| is))\s+([A-Za-z]+)/i,
            // "Wrong name, it's Sarah"
            /wrong(?:\s+name)?[,.\s]+(?:i'm|it's|my name(?:'s| is))\s+([A-Za-z]+)/i,
          ];
          
          const nonNames = new Set([
            'you', 'your', 'me', 'my', 'i', 'we', 'us', 'they', 'it', 'yes', 'no', 'yeah',
            'okay', 'ok', 'sure', 'please', 'thanks', 'hello', 'hi', 'hey', 'bye', 'goodbye',
            'for', 'asap', 'now', 'today', 'taxi', 'cab', 'car', 'booking', 'book'
          ]);
          
          for (const pattern of correctionPatterns) {
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
        
        // Check for name CORRECTION first (works even if we already have a name)
        const correctedName = detectNameCorrection(rawTranscript);
        if (correctedName && correctedName.toLowerCase() !== (callerName || '').toLowerCase()) {
          const previousName = callerName;
          callerName = correctedName;
          console.log(`[${callId}] 👤 Name CORRECTION detected: "${previousName}" → "${callerName}"`);
          
          // Inject the corrected name into Ada's context
          if (openaiWs?.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "assistant",
                content: [{ type: "text", text: `[CRITICAL: Customer CORRECTED their name to "${callerName}". Apologize briefly and use this name going forward.]` }]
              }
            }));
          }
          
          // Update database async
          if (userPhone) {
            (async () => {
              try {
                await supabase.from("callers").upsert({
                  phone_number: userPhone,
                  name: callerName,
                  updated_at: new Date().toISOString()
                }, { onConflict: "phone_number" });
                console.log(`[${callId}] 💾 Updated name ${callerName} (was ${previousName}) for ${userPhone}`);
              } catch (e) {
                console.error(`[${callId}] Failed to update name:`, e);
              }
            })();
          }
        }
        // For NEW callers, also extract name from first response
        else if (!callerName) {
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
              'good', 'morning', 'afternoon', 'evening', 'just', 'actually', 'um', 'uh',
              'you', 'your', 'yours', 'me', 'my', 'mine', 'i', 'we', 'us', 'they', 'it',
              'bye', 'goodbye', 'cheers', 'ta', 'brilliant', 'lovely', 'great', 'fine',
              'for', 'asap', 'now', 'today'
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
        
        // Use AI extraction for accurate booking data (AWAIT geocoding before responding)
        // This prevents Ada from asking passengers before geocoding completes
        // If we're about to end the call (e.g., user said "bye"), skip extraction entirely.
        // OPTIMIZATION: Skip heavy extraction for simple confirmations/short responses
        const transcriptLower = rawTranscript.trim().toLowerCase();

        // If the customer is confirming the high-fare verification, force a single tool call.
        // This prevents Ada from asking for confirmation twice (model uncertainty) on expensive trips.
        if (
          pendingHighFareConfirmation &&
          /^(yes|yeah|yep|yup|please|confirm|go ahead|that's right|that is right|correct|sounds good)\b/i.test(transcriptLower)
        ) {
          console.log(`[${callId}] ✅ High-fare verification confirmed by customer: "${rawTranscript}"`);

          const b = pendingHighFareConfirmation.booking;
          const verifiedFare = pendingHighFareConfirmation.verifiedFare;

          // Clear first to avoid any chance of re-entry
          pendingHighFareConfirmation = null;

          if (openaiWs?.readyState === WebSocket.OPEN) {
            const instruction = `[SYSTEM: CUSTOMER CONFIRMED - PROCEED NOW]
The customer just confirmed they want to proceed with the verified high-fare booking.

IMMEDIATELY call the book_taxi tool now using EXACTLY:
- pickup: "${b.pickup}"
- destination: "${b.destination}"
- passengers: ${b.passengers}
- pickup_time: "${b.pickupTime}"
- vehicle_type: ${b.vehicleType ? `"${b.vehicleType}"` : "null"}

Do NOT ask the customer to confirm again. Use the previously verified fare (£${verifiedFare}).`;

            openaiWs.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{ type: "input_text", text: instruction }],
              },
            }));

            openaiWs.send(JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] },
            }));

            responseCreatedSinceCommit = true;
          }
        }
        
        // Fast-path: Skip extraction for these patterns (no address info to extract)
        const skipExtractionPatterns = [
          // Simple confirmations
          /^(yes|yeah|yep|yup|no|nope|nah|ok|okay|sure|please|right|correct|that's right|that's correct)\b/i,
          // Thank you variations (EXPANDED - catches "that's fine, thank you")
          /\b(thank\s*you|thanks|cheers|ta|lovely|brilliant|perfect|great|fine)\b/i,
          // Simple negatives (with filler words)
          /^no[,.]?\s*(no[,.]?\s*)?(that'?s?\s*(fine|all|it|ok|okay)|i'?m\s*(good|fine|ok|okay)|nothing|thanks)/i,
          // Passenger counts (with optional filler) - EXPANDED to match more natural speech
          /^(just\s*)?(one|two|three|four|five|six|seven|eight|1|2|3|4|5|6|7|8)\s*(passenger|passengers|people|of us)?[!. ]*$/i,
          // Natural passenger responses like "There's two passengers" / "there'll be 2" / "there are 3 of us"
          /^(there'?s|there'll be|there will be|there are|we have|we've got|it's|it'll be|about|around)\s*(just\s*)?(one|two|three|four|five|six|seven|eight|1|2|3|4|5|6|7|8)/i,
          // Luggage counts - "2 bags", "no bags", "just one suitcase"
          /^(just\s*)?(one|two|three|four|five|six|seven|eight|nine|ten|no|none|zero|1|2|3|4|5|6|7|8|9|10|0)\s*(bag|bags|suitcase|suitcases|case|cases|piece|pieces)/i,
          // "we have X bags" / "I've got 2 bags"
          /^(we have|we've got|i have|i've got|got)\s*(one|two|three|four|five|six|seven|eight|nine|ten|no|none|zero|1|2|3|4|5|6|7|8|9|10|0)\s*(bag|bags|suitcase|suitcases)?/i,
          // Goodbye patterns
          /\b(bye|goodbye|see you|take care|have a (good|great|lovely) (day|one|journey))\b/i,
          // Affirmative responses that don't need extraction
          /^(and\s+)?(that'?s?\s*)?(fine|all|it|ok|okay|good|great|perfect|lovely)\b/i,
        ];
        
        const isSimpleResponse = skipExtractionPatterns.some(p => p.test(transcriptLower)) || 
          (transcriptLower.length < 30 && !/\d{1,3}\s*[a-z]/i.test(transcriptLower) && !/road|street|avenue|lane|drive|close|way|court|station|airport/i.test(transcriptLower));
        
        // HYBRID LATENCY OPTIMIZATION:
        // - Simple responses → Send response.create IMMEDIATELY (no waiting)
        // - Address-containing responses → Await extraction first (preserve address flow)
        
        // ═══════════════════════════════════════════════════════════════════════════
        // UNIFIED EXTRACTION MODE: Use AI-first extraction instead of inline regex
        // ═══════════════════════════════════════════════════════════════════════════
        if (useUnifiedExtraction) {
          console.log(`[${callId}] 🧪 UNIFIED MODE: Using taxi-extract-unified for: "${rawTranscript}"`);
          
          // Call unified extraction (runs in parallel, doesn't block response)
          extractWithUnifiedAI(rawTranscript).catch(err => {
            console.error(`[${callId}] 🧪 Unified extraction error:`, err);
          });
          
          // Still send response.create immediately - AI has full context
          if (awaitingResponseAfterCommit && sessionReady && openaiWs?.readyState === WebSocket.OPEN && !responseCreatedSinceCommit) {
            if (openAiResponseInProgress) {
              deferredResponseCreate = { response: { modalities: ["audio", "text"] } as Record<string, unknown>, label: "unified-extraction" };
              responseCreatedSinceCommit = true;
              console.log(`[${callId}] ⏸️ Deferring response.create (unified-extraction) - response already in progress`);
            } else {
              openAiResponseInProgress = true;
              startResponseTimeout();
              openaiWs.send(JSON.stringify({
                type: "response.create",
                response: { modalities: ["audio", "text"] },
              }));
              responseCreatedSinceCommit = true;
              console.log(`[${callId}] >>> response.create sent (unified-extraction)`);
            }
          }
          
          awaitingResponseAfterCommit = false;
          // Skip the inline regex fast-path entirely
        } else if (isSimpleResponse) {
          console.log(`[${callId}] ⚡ FAST-PATH: simple response: "${rawTranscript}" - sending response.create IMMEDIATELY`);
          
          // Number word to digit mapping
          const numMap: { [key: string]: number } = { 
            'none': 0, 'zero': 0, 'one': 1, 'two': 2, 'three': 3, 'four': 4, 
            'five': 5, 'six': 6, 'seven': 7, 'eight': 8, 'nine': 9, 'ten': 10 
          };
          
          // =========== COMBINED PASSENGERS + LUGGAGE PARSING ===========
          // Handles responses like "just me with 2 bags", "3 passengers no luggage", "two of us and three suitcases"
          
          const lowerTranscript = rawTranscript.toLowerCase();
          
          // Patterns for combined responses
          // "just me with 2 bags" / "myself with a bag" / "just one and 2 bags"
          const justMeWithBags = lowerTranscript.match(/\b(just\s+me|myself|just\s+one|only\s+me)\b.*?\b(?:with\s+)?(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:bag|bags|suitcase|suitcases|luggage|case|cases)\b/i);
          if (justMeWithBags) {
            knownBooking.passengers = 1;
            const bagNum = numMap[justMeWithBags[2].toLowerCase()] ?? parseInt(justMeWithBags[2]);
            if (bagNum >= 0 && bagNum <= 10) {
              knownBooking.luggage = bagNum === 0 ? "no luggage" : `${bagNum} bag${bagNum > 1 ? 's' : ''}`;
            }
            console.log(`[${callId}] ⚡ Combined parse: passengers=1 (just me), luggage="${knownBooking.luggage}"`);
          }
          
          // "X passengers with Y bags" / "X people and Y suitcases"
          const passengersWithBags = lowerTranscript.match(/\b(\d+|one|two|three|four|five|six|seven|eight)\s*(?:passengers?|people|persons?|of\s+us)?\s*(?:with|and|,)?\s*(\d+|one|two|three|four|five|six|seven|eight|nine|ten|no|none|zero)\s*(?:bag|bags|suitcase|suitcases|luggage|case|cases)\b/i);
          if (passengersWithBags && !justMeWithBags) {
            const paxNum = numMap[passengersWithBags[1].toLowerCase()] ?? parseInt(passengersWithBags[1]);
            if (paxNum >= 1 && paxNum <= 8) {
              knownBooking.passengers = paxNum;
            }
            const bagCount = passengersWithBags[2].toLowerCase();
            if (bagCount === 'no' || bagCount === 'none' || bagCount === 'zero') {
              knownBooking.luggage = "no luggage";
            } else {
              const bagNum = numMap[bagCount] ?? parseInt(bagCount);
              if (bagNum >= 0 && bagNum <= 10) {
                knownBooking.luggage = bagNum === 0 ? "no luggage" : `${bagNum} bag${bagNum > 1 ? 's' : ''}`;
              }
            }
            console.log(`[${callId}] ⚡ Combined parse: passengers=${knownBooking.passengers}, luggage="${knownBooking.luggage}"`);
          }
          
          // "X passengers no luggage" / "just 2 no bags"
          const passengersNoLuggage = lowerTranscript.match(/\b(\d+|one|two|three|four|five|six|seven|eight|just\s+me|myself)\s*(?:passengers?|people|persons?|of\s+us)?\s*(?:,\s*)?(?:no|without|zero|none)\s*(?:bag|bags|suitcase|suitcases|luggage)\b/i);
          if (passengersNoLuggage && !justMeWithBags && !passengersWithBags) {
            const paxPart = passengersNoLuggage[1].toLowerCase();
            if (paxPart.includes('me') || paxPart.includes('myself')) {
              knownBooking.passengers = 1;
            } else {
              const paxNum = numMap[paxPart] ?? parseInt(paxPart);
              if (paxNum >= 1 && paxNum <= 8) {
                knownBooking.passengers = paxNum;
              }
            }
            knownBooking.luggage = "no luggage";
            console.log(`[${callId}] ⚡ Combined parse: passengers=${knownBooking.passengers}, luggage="no luggage"`);
          }
          
          // "just me no bags" / "myself, no luggage"
          const justMeNoBags = lowerTranscript.match(/\b(just\s+me|myself|just\s+one|only\s+me)\b.*?\b(no|without|zero|none)\s*(?:bag|bags|suitcase|suitcases|luggage)\b/i);
          if (justMeNoBags && !justMeWithBags && !passengersWithBags && !passengersNoLuggage) {
            knownBooking.passengers = 1;
            knownBooking.luggage = "no luggage";
            console.log(`[${callId}] ⚡ Combined parse: passengers=1 (just me), luggage="no luggage"`);
          }
          
          // =========== SINGLE FIELD EXTRACTION (if combined didn't match) ===========
          
          // If we haven't extracted passengers yet from combined patterns
          // CRITICAL: Even if luggageAsked=true, customer may answer with passenger info
          // e.g., "There'll be two passengers and Shelly" when asked about bags
          // We must still capture passenger info from such responses
          if (!knownBooking.passengers) {
            // "just me" / "just one" / "myself"
            if (/\b(just\s+me|myself|just\s+one|only\s+me)\b/i.test(rawTranscript)) {
              knownBooking.passengers = 1;
              console.log(`[${callId}] ⚡ Fast-path: passengers=1 (just me/myself)`);
            } else {
              // Check for explicit passenger mentions even when asking about luggage
              // Pattern: "X passengers", "X people", "X of us"
              const explicitPassengerMatch = lowerTranscript.match(/\b(\d+|one|two|three|four|five|six|seven|eight)\s*(?:passengers?|people|persons?|of\s+us)\b/i);
              if (explicitPassengerMatch) {
                const num = numMap[explicitPassengerMatch[1].toLowerCase()] ?? parseInt(explicitPassengerMatch[1]);
                if (num >= 1 && num <= 8) {
                  knownBooking.passengers = num;
                  console.log(`[${callId}] ⚡ Fast-path: explicit passengers=${num}`);
                }
              } else if (!knownBooking.luggageAsked) {
                // Only do bare number extraction if we're NOT asking about luggage
                // This prevents "2 bags" from being parsed as "2 passengers"
                const passengerMatch = rawTranscript.match(/\b(one|two|three|four|five|six|seven|eight|1|2|3|4|5|6|7|8)\b/i);
                if (passengerMatch) {
                  const num = numMap[passengerMatch[1].toLowerCase()] ?? parseInt(passengerMatch[1]);
                  if (num >= 1 && num <= 8) {
                    knownBooking.passengers = num;
                    console.log(`[${callId}] ⚡ Fast-path: extracted passengers=${num}`);
                  }
                }
              }
            }
          }
          
          // If we haven't extracted luggage yet and Ada asked about it
          if (knownBooking.luggageAsked && !knownBooking.luggage) {
            // CRITICAL: When extracting luggage, exclude numbers that are part of passenger mentions
            // e.g., "two passengers" should NOT set luggage=2
            // First check for explicit luggage mentions (X bags, X suitcases)
            const explicitLuggageMatch = lowerTranscript.match(/\b(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:bag|bags|suitcase|suitcases|case|cases|piece|pieces)\b/i);
            
            // Check if transcript mentions passengers - if so, don't extract bare numbers as luggage
            const mentionsPassengers = /\b(?:passenger|passengers|people|persons?|of\s+us)\b/i.test(lowerTranscript);
            
            const isYesResponse = /\b(yes|yeah|yep|yup|aye)\b/i.test(rawTranscript);
            const isNoResponse = /\b(no|none|nope|nah|nothing)\b/i.test(rawTranscript);
            
            if (isNoResponse && !explicitLuggageMatch) {
              knownBooking.luggage = "no luggage";
              console.log(`[${callId}] ⚡ Fast-path: luggage="no luggage" (no response)`);
            } else if (explicitLuggageMatch) {
              // Explicit luggage mention - trust it
              const num = numMap[explicitLuggageMatch[1].toLowerCase()] ?? parseInt(explicitLuggageMatch[1]);
              if (num === 0) {
                knownBooking.luggage = "no luggage";
                console.log(`[${callId}] ⚡ Fast-path: luggage="no luggage"`);
              } else if (num >= 1 && num <= 10) {
                knownBooking.luggage = `${num} bag${num > 1 ? 's' : ''}`;
                console.log(`[${callId}] ⚡ Fast-path: explicit luggage="${knownBooking.luggage}"`);
              }
            } else if (!mentionsPassengers) {
              // Only do bare number extraction if they didn't mention passengers
              // This prevents "two passengers" from being parsed as "2 bags"
              const luggageMatch = rawTranscript.match(/\b(one|two|three|four|five|six|seven|eight|nine|ten|none|zero|1|2|3|4|5|6|7|8|9|10|0)\b/i);
              if (luggageMatch) {
                const num = numMap[luggageMatch[1].toLowerCase()] ?? parseInt(luggageMatch[1]);
                if (num === 0) {
                  knownBooking.luggage = "no luggage";
                  console.log(`[${callId}] ⚡ Fast-path: luggage="no luggage"`);
                } else if (num >= 1 && num <= 10) {
                  knownBooking.luggage = `${num} bag${num > 1 ? 's' : ''}`;
                  console.log(`[${callId}] ⚡ Fast-path: luggage="${knownBooking.luggage}"`);
                }
              }
            } else if (isYesResponse) {
              console.log(`[${callId}] ⚡ Fast-path: luggage answer "yes" without count, waiting for follow-up`);
            } else if (mentionsPassengers) {
              console.log(`[${callId}] ⚡ Fast-path: response mentions passengers but not bags - will re-ask for luggage`);
            }
          }
          
          // If this is a travel-hub trip and luggage isn't known yet, force Ada to ask about bags now
          if (!forcedResponseInstructions) {
            const tripHasTravelHubNow = isTravelHub(knownBooking.pickup) || isTravelHub(knownBooking.destination);
            if (tripHasTravelHubNow && !knownBooking.luggage) {
              knownBooking.luggageAsked = true;
              forcedResponseInstructions =
                "Before confirming the booking, ask the customer how many bags they will have for this trip. Do not ask 'shall I book that' yet.";
              console.log(`[${callId}] ✈️ Forcing luggage question (fast-path)`);
            }
          }

          // IMMEDIATE response.create - no waiting!
          if (awaitingResponseAfterCommit && sessionReady && openaiWs?.readyState === WebSocket.OPEN && !responseCreatedSinceCommit) {
            const response = forcedResponseInstructions
              ? { modalities: ["audio", "text"], instructions: forcedResponseInstructions }
              : { modalities: ["audio", "text"] };

            const label = forcedResponseInstructions ? "fast-path(forced)" : "fast-path";

            if (openAiResponseInProgress) {
              deferredResponseCreate = { response: response as Record<string, unknown>, label };
              responseCreatedSinceCommit = true;
              console.log(`[${callId}] ⏸️ Deferring response.create (${label}) - response already in progress`);
            } else {
              openAiResponseInProgress = true;
              startResponseTimeout(); // SAFEGUARD 1
              openaiWs.send(JSON.stringify({
                type: "response.create",
                response,
              }));
              responseCreatedSinceCommit = true;
              console.log(`[${callId}] >>> response.create sent IMMEDIATELY (${label})`);
            }
          }

        } else if (!forcedResponseInstructions) {
          // ADDRESS-CONTAINING RESPONSE: Run extraction and WAIT for disambiguation check
          // This ensures we catch ambiguous addresses (like "School Road, Birmingham") before Ada continues
          console.log(`[${callId}] 🔍 Address-path: running extraction (with disambiguation check) for: "${rawTranscript}"`);
          
          // AWAIT extraction so disambiguation can trigger before Ada responds
          try {
            await extractBookingFromTranscript(rawTranscript);
          } catch (err) {
            console.error(`[${callId}] Extraction error:`, err);
          }
          
          // Check if disambiguation was triggered - if so, skip normal response
          if (pendingAreaDisambiguation) {
            console.log(`[${callId}] 🗺️ Disambiguation pending - skipping normal response.create`);
            responseCreatedSinceCommit = true; // Prevent other paths from sending response
            awaitingResponseAfterCommit = false;
            startDisambiguationTimeout(); // SAFEGUARD 3
          } else {
            // Check travel hub for luggage question BEFORE sending response
            const tripHasTravelHubNow = isTravelHub(knownBooking.pickup) || isTravelHub(knownBooking.destination);
            if (tripHasTravelHubNow && !knownBooking.luggage && !knownBooking.luggageAsked) {
              knownBooking.luggageAsked = true;
              forcedResponseInstructions =
                "Before confirming the booking, ask the customer how many bags they will have for this trip. Do not ask 'shall I book that' yet.";
              console.log(`[${callId}] ✈️ Forcing luggage question (address-path)`);
            }
            
            // Send response.create after extraction completes (disambiguation didn't trigger)
            if (awaitingResponseAfterCommit && sessionReady && openaiWs?.readyState === WebSocket.OPEN && !responseCreatedSinceCommit) {
              const response = forcedResponseInstructions
                ? { modalities: ["audio", "text"], instructions: forcedResponseInstructions }
                : { modalities: ["audio", "text"] };

              const label = forcedResponseInstructions ? "address-path(forced)" : "address-path";

              if (openAiResponseInProgress) {
                deferredResponseCreate = { response: response as Record<string, unknown>, label };
                responseCreatedSinceCommit = true;
                console.log(`[${callId}] ⏸️ Deferring response.create (${label}) - response already in progress`);
              } else {
                openAiResponseInProgress = true;
                startResponseTimeout(); // SAFEGUARD 1
                openaiWs.send(JSON.stringify({
                  type: "response.create",
                  response,
                }));
                responseCreatedSinceCommit = true;
                console.log(`[${callId}] >>> response.create sent (${label})`);
              }
            }
          }
        } else {
          console.log(`[${callId}] 📴 Skipping extraction (forcedResponseInstructions set)`);
          
          // Still send response.create for forced instructions
          if (awaitingResponseAfterCommit && sessionReady && openaiWs?.readyState === WebSocket.OPEN && !responseCreatedSinceCommit) {
            const response = { modalities: ["audio", "text"], instructions: forcedResponseInstructions };
            const label = "forced-instructions";

            if (openAiResponseInProgress) {
              deferredResponseCreate = { response: response as Record<string, unknown>, label };
              responseCreatedSinceCommit = true;
              console.log(`[${callId}] ⏸️ Deferring response.create (${label}) - response already in progress`);
            } else {
              openAiResponseInProgress = true;
              startResponseTimeout(); // SAFEGUARD 1
              openaiWs.send(JSON.stringify({
                type: "response.create",
                response,
              }));
              responseCreatedSinceCommit = true;
              console.log(`[${callId}] >>> response.create sent (${label})`);
            }
          }
        }
        
        awaitingResponseAfterCommit = false;
      }

      // Speech started (barge-in detection) - CANCEL AI response for faster interruption
      if (data.type === "input_audio_buffer.speech_started") {
        console.log(`[${callId}] >>> User started speaking (barge-in)`);
        noteUserActivity("speech_started");
        socket.send(JSON.stringify({ type: "user_speaking", speaking: true }));
        
        // If Ada is speaking, cancel the current response AND clear the audio buffer
        // CRITICAL: Clearing the buffer removes any TTS echo/residue that was captured
        // before the barge-in was detected - prevents garbage transcripts like "Can I make that fucking face"
        if (aiSpeaking && openaiWs?.readyState === WebSocket.OPEN) {
          console.log(`[${callId}] 🧹 Barge-in during AI speech - clearing audio buffer to prevent echo contamination`);
          openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          openaiWs.send(JSON.stringify({ type: "response.cancel" }));
        }
      }

      // Speech stopped
      if (data.type === "input_audio_buffer.speech_stopped") {
        console.log(`[${callId}] >>> User stopped speaking`);
        socket.send(JSON.stringify({ type: "user_speaking", speaking: false }));
      }

      // Audio buffer committed (server VAD auto-commits)
      // IMPORTANT: Do NOT create a response here.
      // We only create a response AFTER we have a finalized, non-empty transcription.
      // This prevents Ada from speaking/acting (e.g., cancelling) when the line is silent/noisy.
      if (data.type === "input_audio_buffer.committed") {
        console.log(`[${callId}] >>> Audio buffer committed, item_id: ${data.item_id}`);
        lastAudioCommitAt = Date.now();
        awaitingResponseAfterCommit = true;
        responseCreatedSinceCommit = false;
        
        // SAFEGUARD 2: Start transcription timeout
        startTranscriptionTimeout(data.item_id || "unknown");
      }

      // Handle transcription failures - important for debugging missed responses
      if (data.type === "conversation.item.input_audio_transcription.failed") {
        console.log(`[${callId}] ⚠️ TRANSCRIPTION FAILED:`, JSON.stringify(data.error || data));
        // Notify frontend that transcription failed
        socket.send(JSON.stringify({ type: "transcription_failed", error: data.error }));
      }

      // DEBUG: log response lifecycle events (helps diagnose missing audio)
      if (data.type === "response.done") {
        const status = data.response?.status || "unknown";
        const outputCount = data.response?.output?.length || 0;
        console.log(`[${callId}] >>> response.done - status: ${status}, outputs: ${outputCount}`);
        if (data.response?.status_details) {
          console.log(`[${callId}] >>> status_details:`, JSON.stringify(data.response.status_details));
        }

        // Mark response finished so we don't accidentally create overlapping responses.
        openAiResponseInProgress = false;
        
        // SAFEGUARD 1: Clear response timeout
        clearResponseTimeout();

        // If we deferred a response.create due to an in-progress response, flush it now.
        if (deferredResponseCreate && sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
          const { response, label } = deferredResponseCreate;
          deferredResponseCreate = null;
          openAiResponseInProgress = true; // optimistic guard against race
          startResponseTimeout(); // SAFEGUARD 1: Re-arm for new response
          openaiWs.send(JSON.stringify({ type: "response.create", response }));
          console.log(`[${callId}] >>> response.create sent (flushed deferred: ${label})`);
        }
      }

      // Handle function calls
      if (data.type === "response.function_call_arguments.done") {
        console.log(`[${callId}] Function call: ${data.name}`, data.arguments);
        
        if (data.name === "book_taxi") {
          const args = JSON.parse(data.arguments);

          // TRAVEL HUB LUGGAGE CHECK: Block booking if trip involves airport/station but luggage unknown
          const pickupForCheck = knownBooking.pickup ?? args.pickup;
          const destinationForCheck = args.destination ?? knownBooking.destination;
          const tripHasTravelHub = isTravelHub(pickupForCheck) || isTravelHub(destinationForCheck);
          
          if (tripHasTravelHub && !knownBooking.luggage) {
            console.log(`[${callId}] ⛔ BLOCKING book_taxi: Travel hub trip but luggage unknown`);
            
            // Mark luggage as asked to prevent duplicate questions
            knownBooking.luggageAsked = true;
            
            // Inject system message to Ada instructing her to ask about luggage
            if (openaiWs?.readyState === WebSocket.OPEN) {
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{ 
                    type: "input_text", 
                    text: `[BOOKING BLOCKED: This trip involves a travel hub but luggage info is missing. You MUST ask the customer how many bags they have right now. Say: "Before I confirm that, how many bags will you have?"]` 
                  }]
                }
              }));
              
              // CRITICAL: Trigger Ada to respond to the blocking message
              // Without this, she stays silent after the tool call fails
              // CONCURRENCY GUARD: Don't send response.create if one is already in progress
              const luggageResponse = { modalities: ["audio", "text"] };
              
              if (openAiResponseInProgress) {
                deferredResponseCreate = { response: luggageResponse as Record<string, unknown>, label: "luggage_block" };
                console.log(`[${callId}] ⏸️ Deferring response.create (luggage_block) - response already in progress`);
              } else {
                openAiResponseInProgress = true;
                startResponseTimeout(); // SAFEGUARD 1
                openaiWs.send(JSON.stringify({
                  type: "response.create",
                  response: luggageResponse
                }));
                console.log(`[${callId}] ✈️ Triggered luggage question after book_taxi block`);
              }
              
              // SAFEGUARD 4: Start tool block retry in case Ada doesn't respond
              startToolBlockRetry("luggage_block");
            }
            
            // Return early without processing the booking
            return;
          }

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
            vehicleType: knownBooking.vehicleType ?? args.vehicle_type ?? null, // e.g., "6 seater", "MPV"
            luggage: knownBooking.luggage ?? args.luggage ?? null, // Include luggage in final booking
            pickupName: knownBooking.pickupName ?? null, // Business name from Google (e.g., "Birmingham Airport")
            destinationName: knownBooking.destinationName ?? null, // Business name from Google (e.g., "Sweet Spot Cafe")
          };

          bookingData = finalBooking;
          // If we had a pending high-fare confirmation, it is now being actioned.
          pendingHighFareConfirmation = null;
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
            
            // If we know the caller's city but the pickup is a bare address (no city/postcode), append the city
            let pickupForResolver = finalBooking.pickup;
            let dropoffForResolver = finalBooking.destination;
            
            const hasCityOrPostcode = (addr: string): boolean => {
              if (!addr) return false;
              // Has UK postcode
              if (/\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b/i.test(addr)) return true;
              // Has UK outcode
              if (/\b[A-Z]{1,2}\d[A-Z\d]?\b/i.test(addr)) return true;
              // Has comma (likely includes area/city)
              if (addr.includes(",")) return true;
              // Check for known UK cities
              const cities = ["Birmingham", "Coventry", "Manchester", "Liverpool", "London", "Leeds", "Sheffield", "Bristol", "Nottingham", "Leicester", "Newcastle", "Wolverhampton", "Solihull", "Walsall", "Dudley"];
              for (const city of cities) {
                if (addr.toLowerCase().includes(city.toLowerCase())) return true;
              }
              return false;
            };
            
            // Append callerCity to bare addresses so geocoding is accurate
            if (callerCity && pickupForResolver && !hasCityOrPostcode(pickupForResolver)) {
              pickupForResolver = `${pickupForResolver}, ${callerCity}`;
              console.log(`[${callId}] 🏙️ Appended callerCity to pickup: "${finalBooking.pickup}" → "${pickupForResolver}"`);
            }
            if (callerCity && dropoffForResolver && !hasCityOrPostcode(dropoffForResolver)) {
              dropoffForResolver = `${dropoffForResolver}, ${callerCity}`;
              console.log(`[${callId}] 🏙️ Appended callerCity to destination: "${finalBooking.destination}" → "${dropoffForResolver}"`);
            }
            
            // Extract city/area from pickup address if present (e.g., "School Road, Birmingham" → "Birmingham")
            // This should override callerCity for biasing to ensure disambiguation works correctly
            const extractCityFromAddress = (addr: string): string | null => {
              // Comprehensive UK cities and areas list
              const cities = [
                // Major UK cities
                "Birmingham", "Coventry", "Manchester", "Liverpool", "London", "Leeds", 
                "Sheffield", "Bristol", "Nottingham", "Leicester", "Newcastle", "Cardiff",
                "Edinburgh", "Glasgow", "Belfast",
                // West Midlands
                "Wolverhampton", "Solihull", "Walsall", "Dudley", "West Bromwich",
                "Netherton", "Sutton Coldfield", "Perry Barr", "Erdington", "Edgbaston",
                "Moseley", "Handsworth", "Aston", "Sparkhill", "Sparkbrook", "Kings Heath",
                "Harborne", "Selly Oak", "Quinton",
                // Greater Manchester
                "Salford", "Stockport", "Oldham", "Rochdale", "Bolton", "Bury", "Wigan",
                "Tameside", "Trafford", "Didsbury", "Chorlton", "Withington", "Rusholme",
                "Fallowfield", "Stretford",
                // London boroughs
                "Westminster", "Kensington", "Chelsea", "Hammersmith", "Fulham", "Wandsworth",
                "Lambeth", "Southwark", "Lewisham", "Greenwich", "Hackney", "Islington",
                "Camden", "Brent", "Ealing", "Hounslow", "Richmond", "Kingston", "Croydon",
                "Bromley", "Bexley", "Barking", "Dagenham", "Havering", "Redbridge",
                "Walthamstow", "Enfield", "Barnet", "Haringey", "Tottenham", "Brixton",
                "Peckham", "Stratford", "Canary Wharf", "Docklands", "Shoreditch",
                // Merseyside
                "Birkenhead", "Wallasey", "Bootle", "St Helens", "Knowsley",
                // South Yorkshire
                "Rotherham", "Doncaster", "Barnsley",
                // West Yorkshire
                "Bradford", "Huddersfield", "Wakefield", "Halifax",
                // Tyne and Wear
                "Gateshead", "Sunderland", "South Shields",
                // East Midlands
                "Derby", "Northampton",
                // Other
                "Cambridge", "Oxford", "Reading", "Brighton", "Southampton", "Portsmouth",
                "Bournemouth", "Plymouth", "Exeter", "Bath", "York", "Hull"
              ];
              for (const city of cities) {
                if (addr.toLowerCase().includes(city.toLowerCase())) return city;
              }
              return null;
            };
            
            // CRITICAL: Only use callerCity bias if NO area is mentioned in the pickup address
            // If user says "Church Road, Birmingham", use Birmingham (ignore callerCity)
            // If user says "Church Road" with no area, use callerCity for bias
            const pickupCity = extractCityFromAddress(pickupForResolver);
            const effectiveCityHint = pickupCity || callerCity || undefined;
            
            if (pickupCity) {
              console.log(`[${callId}] 🏙️ Using explicit pickup area "${pickupCity}" for disambiguation (ignoring callerCity=${callerCity || "none"})`);
            } else if (callerCity) {
              console.log(`[${callId}] 🏙️ No area in pickup - using callerCity "${callerCity}" for bias`);
            }
            
            const tripResolveResponse = await fetch(`${SUPABASE_URL}/functions/v1/taxi-trip-resolve`, {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
              },
              body: JSON.stringify({
                pickup_input: pickupForResolver,
                dropoff_input: dropoffForResolver,
                caller_city_hint: effectiveCityHint,
                passengers: finalBooking.passengers || 1,
                country: "GB"
              }),
            });
            
            if (tripResolveResponse.ok) {
              tripResolveResult = await tripResolveResponse.json();
              console.log(`[${callId}] 🚕 Trip resolve result:`, JSON.stringify(tripResolveResult, null, 2));
              
              // ===== Check for area disambiguation (same road in multiple areas) =====
              // SKIP if user already resolved this area via early disambiguation
              if (tripResolveResult.needs_pickup_disambiguation && tripResolveResult.pickup_area_matches?.length > 1) {
                if (knownBooking.pickupAreaResolved) {
                  console.log(`[${callId}] 🗺️ Pickup area already resolved by user - skipping re-disambiguation`);
                } else {
                  console.log(`[${callId}] 🗺️ Pickup needs area disambiguation: ${tripResolveResult.pickup_area_matches.length} areas found`);
                  
                  // Get the road name from the first match
                  const roadName = tripResolveResult.pickup_area_matches[0]?.road || finalBooking.pickup;
                  
                  // Ask Ada to have the customer choose
                  askForAreaDisambiguation("pickup", roadName, tripResolveResult.pickup_area_matches);
                  
                  // Return a helpful error to Ada
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "function_call_output",
                      call_id: data.call_id,
                      output: JSON.stringify({
                        success: false,
                        error: "area_disambiguation_needed",
                        message: `I found ${roadName} in multiple areas. Please ask the customer which area they mean.`
                      })
                    }
                  }));
                  
                  openaiWs?.send(JSON.stringify({
                    type: "response.create",
                    response: { modalities: ["audio", "text"] }
                  }));
                  
                  return; // Don't proceed until customer chooses area
                }
              }
              
              if (tripResolveResult.needs_dropoff_disambiguation && tripResolveResult.dropoff_area_matches?.length > 1) {
                if (knownBooking.destinationAreaResolved) {
                  console.log(`[${callId}] 🗺️ Destination area already resolved by user - skipping re-disambiguation`);
                } else {
                  console.log(`[${callId}] 🗺️ Dropoff needs area disambiguation: ${tripResolveResult.dropoff_area_matches.length} areas found`);
                  
                  // Get the road name from the first match
                  const roadName = tripResolveResult.dropoff_area_matches[0]?.road || finalBooking.destination;
                  
                  // Ask Ada to have the customer choose
                  askForAreaDisambiguation("destination", roadName, tripResolveResult.dropoff_area_matches);
                  
                  // Return a helpful error to Ada
                  openaiWs?.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "function_call_output",
                      call_id: data.call_id,
                      output: JSON.stringify({
                        success: false,
                        error: "area_disambiguation_needed",
                        message: `I found ${roadName} in multiple areas. Please ask the customer which area they mean.`
                      })
                    }
                  }));
                  
                  openaiWs?.send(JSON.stringify({
                    type: "response.create",
                    response: { modalities: ["audio", "text"] }
                  }));
                  
                  return; // Don't proceed until customer chooses area
                }
              }
              
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
                // IMPORTANT: Update finalBooking with the VERIFIED addresses from trip resolver
                // This ensures we save the geocoded/formatted addresses, not raw input
                if (tripResolveResult.pickup?.formatted_address) {
                  console.log(`[${callId}] 📍 Pickup resolved: ${tripResolveResult.pickup.formatted_address}`);
                  finalBooking.pickup = tripResolveResult.pickup.formatted_address;
                  knownBooking.pickupVerified = true;
                  // Extract place name if available (e.g., "Birmingham Airport", "Sweet Spot Cafe")
                  if (tripResolveResult.pickup.name) {
                    finalBooking.pickupName = tripResolveResult.pickup.name;
                    knownBooking.pickupName = tripResolveResult.pickup.name;
                    console.log(`[${callId}] 🏪 Pickup place name: ${tripResolveResult.pickup.name}`);
                  }
                }
                if (tripResolveResult.dropoff?.formatted_address) {
                  console.log(`[${callId}] 📍 Dropoff resolved: ${tripResolveResult.dropoff.formatted_address}`);
                  finalBooking.destination = tripResolveResult.dropoff.formatted_address;
                  knownBooking.destinationVerified = true;
                  // Extract place name if available
                  if (tripResolveResult.dropoff.name) {
                    finalBooking.destinationName = tripResolveResult.dropoff.name;
                    knownBooking.destinationName = tripResolveResult.dropoff.name;
                    console.log(`[${callId}] 🏪 Destination place name: ${tripResolveResult.dropoff.name}`);
                  }
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

            // Store booking details so we can force a tool call on the next "Yes"
            pendingHighFareConfirmation = {
              booking: {
                pickup: finalBooking.pickup,
                destination: finalBooking.destination,
                passengers: Number(finalBooking.passengers || 1),
                pickupTime: finalBooking.pickupTime || "ASAP",
                vehicleType: finalBooking.vehicleType || null,
              },
              verifiedFare,
            };
            
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
          
          // OPTIMIZATION: Parallelize all database operations (they don't depend on each other)
          const phoneKey = userPhone ? (normalizePhone(userPhone) || userPhone) : "web-test";
          
          // Generate a short reference number (e.g., "ABC123")
          const generateReference = () => {
            const letters = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
            const numbers = '0123456789';
            let ref = '';
            for (let i = 0; i < 3; i++) ref += letters[Math.floor(Math.random() * letters.length)];
            for (let i = 0; i < 3; i++) ref += numbers[Math.floor(Math.random() * numbers.length)];
            return ref;
          };
          const bookingRef = generateReference();
          
          // Build complete booking_details JSON
          const bookingDetails = {
            reference: bookingRef,
            pickup: {
              address: finalBooking.pickup,
              time: finalBooking.pickupTime || "ASAP",
              verified: knownBooking.pickupVerified || false
            },
            destination: {
              address: finalBooking.destination,
              verified: knownBooking.destinationVerified || false
            },
            passengers: finalBooking.passengers,
            vehicle_type: finalBooking.vehicleType || null,
            luggage: finalBooking.luggage || null, // Include extracted luggage info
            special_requests: null,
            fare: `£${fare}`,
            eta: isAsap ? eta : null,
            status: "active",
            history: [
              { at: new Date().toISOString(), action: "created", by: callSource || "phone" }
            ]
          };
          
          // Run all database operations in PARALLEL (don't await sequentially)
          const dbPromises = Promise.all([
            // 1. Log to call_logs
            supabase.from("call_logs").insert({
              call_id: callId,
              pickup: finalBooking.pickup,
              destination: finalBooking.destination,
              passengers: finalBooking.passengers,
              estimated_fare: `£${fare}`,
              booking_status: "confirmed",
              call_start_at: callStartAt,
              user_phone: userPhone || null
            }),
            
            // 2. Save booking to bookings table (include place names for friendly display)
            supabase.from("bookings").insert({
              call_id: callId,
              caller_phone: phoneKey,
              caller_name: callerName || null,
              pickup: finalBooking.pickup,
              destination: finalBooking.destination,
              pickup_name: finalBooking.pickupName || null,
              destination_name: finalBooking.destinationName || null,
              passengers: finalBooking.passengers,
              fare: `£${fare}`,
              eta: isAsap ? eta : null,
              scheduled_for: scheduledTime,
              status: "active",
              booked_at: new Date().toISOString(),
              booking_details: bookingDetails
            }).select().single(),
            
            // 3. Save/update caller info - pass verification flags to only save verified addresses
            saveCallerInfo({
              ...finalBooking,
              pickupVerified: knownBooking.pickupVerified || false,
              destinationVerified: knownBooking.destinationVerified || false
            }),
            
            // 4. Broadcast booking confirmed
            broadcastLiveCall({
              pickup: finalBooking.pickup,
              destination: finalBooking.destination,
              passengers: finalBooking.passengers,
              booking_confirmed: true,
              fare: `£${fare}`,
              eta: isAsap ? eta : `Scheduled for ${scheduledTime}`
            })
          ]);
          
          // Don't wait for DB operations - send confirmation to AI immediately
          // This cuts ~200-400ms from user-perceived latency
          console.log(`[${callId}] 🚀 Sending booking confirmation (DB ops running in background)`);
          
          // Handle DB results in background (for logging/error handling)
          dbPromises.then(([callLogResult, bookingResult]) => {
            if (bookingResult.error) {
              console.error(`[${callId}] ❌ Failed to save booking:`, bookingResult.error);
            } else {
              activeBooking = bookingResult.data;
              console.log(`[${callId}] ✅ Booking saved: ${bookingRef}`);
            }
          }).catch(err => {
            console.error(`[${callId}] ❌ DB operations failed:`, err);
          });
          
          // Build confirmation script based on ASAP vs scheduled
          let confirmationScript: string;
          const vehicleNote = finalBooking.vehicleType ? ` in a ${finalBooking.vehicleType}` : "";
          if (isAsap) {
            confirmationScript = `Brilliant${callerName ? `, ${callerName}` : ""}! That's all booked${vehicleNote}. The fare is £${fare} and your driver will be with you in ${eta}. Is there anything else I can help you with?`;
          } else {
            // Format scheduled time nicely
            const timeDisplay = scheduledTime || finalBooking.pickupTime;
            confirmationScript = `Brilliant${callerName ? `, ${callerName}` : ""}! That's all booked for ${timeDisplay}${vehicleNote}. The fare will be £${fare}. Is there anything else I can help you with?`;
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
                vehicle_type: finalBooking.vehicleType || null,
                pickup_time: isAsap ? "ASAP" : scheduledTime,
                fare: `£${fare}`,
                eta: isAsap ? eta : null,
                scheduled_for: scheduledTime,
                confirmation_script: confirmationScript
              })
            }
          }));
          
          // Trigger response - CRITICAL: instruct AI to use EXACT confirmation script, nothing more
          openaiWs?.send(
            JSON.stringify({
              type: "response.create",
              response: { 
                modalities: ["audio", "text"],
                instructions: `Say EXACTLY this confirmation (do not add anything, do not repeat the "anything else" question): "${confirmationScript}"`
              },
            }),
          );
          
          // Arm follow-up silence timeout since confirmation asks "anything else?"
          armFollowupSilenceTimeout("booking_confirmed");
          
          // Notify client
          socket.send(JSON.stringify({
            type: "booking_confirmed",
            booking: { ...finalBooking, fare: `£${fare}`, eta }
          }));
        }
        
        // Handle cancel_booking function
        if (data.name === "cancel_booking") {
          let args: any = {};
          try {
            args = JSON.parse(data.arguments || "{}");
          } catch (e) {
            console.error(`[${callId}] ⚠️ cancel_booking args parse error:`, e);
          }

          // SAFETY GUARD: Only cancel if the LAST finalized transcript contains an explicit cancel intent.
          // Rationale: STT can be wildly wrong on telephony; the model may still *infer* cancel intent and call the tool.
          // We must prevent accidental cancellations when the transcript does not clearly say cancel.
          const guardT = (lastFinalUserTranscript || "").toLowerCase().trim();
          const explicitCancel = /\b(cancel(\s+it|\s+that|\s+the\s+booking)?|call\s+it\s+off|call\s+off|scrap\s+it|cancel\s+booking)\b/.test(guardT);
          const explicitKeep = /\b(keep(\s+it)?|leave\s+it|don'?t\s+cancel|still\s+okay|still\s+ok)\b/.test(guardT);

          console.log(`[${callId}] 🚫 Cancel booking requested: ${args.reason} | transcript="${lastFinalUserTranscript}" | explicitCancel=${explicitCancel} explicitKeep=${explicitKeep}`);

          if (!explicitCancel || explicitKeep) {
            console.log(`[${callId}] 🛑 Cancel blocked (no explicit cancel in transcript)`);

            // Tell the model to ask for a clear confirmation instead of cancelling.
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  blocked: true,
                  message: "Cancellation blocked: I didn't hear a clear request to cancel.",
                  next_action: "Ask the customer to confirm: say 'cancel it' to cancel, or 'keep it' to keep the booking."
                })
              }
            }));

            openaiWs?.send(JSON.stringify({
              type: "response.create",
              response: {
                modalities: ["audio", "text"],
                instructions: "You may have misheard the customer. Ask ONE clear question: 'Did you want me to cancel your booking? Please say cancel it, or keep it.' Do NOT cancel yet."
              }
            }));

            return;
          }

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
            // Mark the booking as CANCELLED (keep for history, but exclude from lookups)
            const { error: cancelError } = await supabase
              .from("bookings")
              .update({
                status: "cancelled",
                cancelled_at: new Date().toISOString(),
                cancellation_reason: args.reason || "customer_request"
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
              console.log(`[${callId}] ✅ Booking ${bookingToCancel.id} marked as CANCELLED`);
              
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
              let finalPickup = updates.pickup || bookingToModify.pickup;
              let finalDestination = updates.destination || bookingToModify.destination;
              const finalPassengers = updates.passengers ?? bookingToModify.passengers ?? 1;
              
              if (args.new_pickup || args.new_destination) {
                // Use taxi-trip-resolve for consistent fare calculation (same as book_taxi)
                console.log(`[${callId}] 🔄 Recalculating fare via trip-resolve for modified booking`);
                
                // If we know the caller's city but the address is bare (no city/postcode), append the city
                const hasCityOrPostcode = (addr: string): boolean => {
                  if (!addr) return false;
                  if (/\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b/i.test(addr)) return true;
                  if (/\b[A-Z]{1,2}\d[A-Z\d]?\b/i.test(addr)) return true;
                  if (addr.includes(",")) return true;
                  const cities = ["Birmingham", "Coventry", "Manchester", "Liverpool", "London", "Leeds", "Sheffield", "Bristol", "Nottingham", "Leicester", "Newcastle", "Wolverhampton", "Solihull", "Walsall", "Dudley"];
                  for (const city of cities) {
                    if (addr.toLowerCase().includes(city.toLowerCase())) return true;
                  }
                  return false;
                };
                
                let pickupForResolver = finalPickup;
                let dropoffForResolver = finalDestination;
                
                if (callerCity && pickupForResolver && !hasCityOrPostcode(pickupForResolver)) {
                  pickupForResolver = `${pickupForResolver}, ${callerCity}`;
                  console.log(`[${callId}] 🏙️ Appended callerCity to pickup: "${finalPickup}" → "${pickupForResolver}"`);
                }
                if (callerCity && dropoffForResolver && !hasCityOrPostcode(dropoffForResolver)) {
                  dropoffForResolver = `${dropoffForResolver}, ${callerCity}`;
                  console.log(`[${callId}] 🏙️ Appended callerCity to destination: "${finalDestination}" → "${dropoffForResolver}"`);
                }
                
                try {
                  const tripResolveUrl = `${SUPABASE_URL}/functions/v1/taxi-trip-resolve`;
                  const tripResponse = await fetch(tripResolveUrl, {
                    method: "POST",
                    headers: {
                      "Content-Type": "application/json",
                      "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`
                    },
                    body: JSON.stringify({
                      pickup_input: pickupForResolver,
                      dropoff_input: dropoffForResolver,
                      passengers: finalPassengers,
                      caller_city_hint: callerCity || undefined,
                      country: "GB"
                    })
                  });
                  
                  if (tripResponse.ok) {
                    const tripData = await tripResponse.json();
                    if (tripData.ok && tripData.fare_estimate?.amount) {
                      newFare = `£${tripData.fare_estimate.amount}`;
                      updates.fare = newFare;
                      changes.push(`fare updated to ${newFare}`);
                      console.log(`[${callId}] ✅ Trip resolve returned fare: ${newFare}`);
                      
                      // Update addresses with resolved versions if better
                      if (tripData.pickup?.formatted_address) {
                        updates.pickup = tripData.pickup.name || finalPickup;
                      }
                      if (tripData.dropoff?.formatted_address) {
                        updates.destination = tripData.dropoff.name || finalDestination;
                      }
                    } else {
                      console.log(`[${callId}] ⚠️ Trip resolve did not return fare, keeping existing: ${newFare}`);
                    }
                  } else {
                    console.error(`[${callId}] Trip resolve failed:`, await tripResponse.text());
                  }
                } catch (e) {
                  console.error(`[${callId}] Trip resolve error:`, e);
                }
              }
              
              // Build booking_details update with history entry
              // We need to fetch current booking_details first to append history
              const { data: currentBooking } = await supabase
                .from("bookings")
                .select("booking_details")
                .eq("id", bookingToModify.id)
                .single();
              
              const existingDetails = (currentBooking?.booking_details as Record<string, any>) || {};
              const existingHistory = Array.isArray(existingDetails.history) ? existingDetails.history : [];
              
              // Build updated booking_details
              const updatedDetails = {
                ...existingDetails,
                pickup: args.new_pickup ? { 
                  address: updates.pickup || finalPickup, 
                  time: existingDetails.pickup?.time || "ASAP",
                  verified: true 
                } : existingDetails.pickup,
                destination: args.new_destination ? { 
                  address: updates.destination || finalDestination, 
                  verified: true 
                } : existingDetails.destination,
                passengers: updates.passengers ?? existingDetails.passengers,
                vehicle_type: args.new_vehicle_type ?? existingDetails.vehicle_type,
                fare: newFare,
                status: "active",
                history: [
                  ...existingHistory,
                  { 
                    at: new Date().toISOString(), 
                    action: "modified", 
                    changes: Object.fromEntries(
                      Object.entries({
                        pickup: args.new_pickup,
                        destination: args.new_destination,
                        passengers: args.new_passengers,
                        vehicle_type: args.new_vehicle_type
                      }).filter(([_, v]) => v !== undefined)
                    )
                  }
                ]
              };
              
              updates.booking_details = updatedDetails;
              
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
                        fare: updatedBooking.fare,
                        vehicle_type: updatedDetails.vehicle_type || null
                      },
                      confirmation_script: `I've updated your booking.${updatedDetails.vehicle_type ? ` Vehicle type changed to ${updatedDetails.vehicle_type}.` : ''} It's now from ${updatedBooking.pickup} to ${updatedBooking.destination} for ${updatedBooking.passengers} passenger${updatedBooking.passengers > 1 ? 's' : ''}, and the fare is ${updatedBooking.fare}. Is there anything else?`
                    })
                  }
                }));
              }
            }
          } else {
            // No active booking to modify.
            // IMPORTANT: OpenAI sometimes calls modify_booking during a NEW booking flow when the customer is correcting an address.
            // In that case, treat it as updating the in-progress (knownBooking) details instead of telling them "no active bookings".
            const hasInProgressBooking = Boolean(knownBooking.pickup || knownBooking.destination || knownBooking.passengers);
            const hasAnyUpdates = Boolean(args.new_pickup || args.new_destination || args.new_passengers !== undefined || args.new_vehicle_type);

            if (hasAnyUpdates && hasInProgressBooking) {
              if (args.new_pickup) {
                knownBooking.pickup = args.new_pickup;
                knownBooking.pickupVerified = false;
                knownBooking.highFareVerified = false;
              }
              if (args.new_destination) {
                knownBooking.destination = args.new_destination;
                knownBooking.destinationVerified = false;
                knownBooking.highFareVerified = false;
              }
              if (args.new_passengers !== undefined) {
                knownBooking.passengers = args.new_passengers;
              }
              if (args.new_vehicle_type) {
                knownBooking.vehicleType = args.new_vehicle_type;
              }

              console.log(`[${callId}] ✅ Applied modify_booking to in-progress booking:`, knownBooking);
              queueLiveCallBroadcast({
                pickup: knownBooking.pickup,
                destination: knownBooking.destination,
                passengers: knownBooking.passengers,
              });

              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                    output: JSON.stringify({
                      success: true,
                      message: `Updated the new booking details.${args.new_vehicle_type ? ` Vehicle type set to ${args.new_vehicle_type}.` : ''}`,
                      updated_booking: {
                        pickup: knownBooking.pickup,
                        destination: knownBooking.destination,
                        passengers: knownBooking.passengers,
                        pickup_time: knownBooking.pickupTime || "ASAP",
                        vehicle_type: knownBooking.vehicleType || null
                      },
                      next_action: "Continue the NEW booking flow. Ask for any missing detail (time, pickup, destination, passengers). Do NOT say there are no active bookings.",
                    }),
                },
              }));
            } else {
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
          }
          
          // Trigger response
          openaiWs?.send(JSON.stringify({
            type: "response.create",
            response: { modalities: ["audio", "text"] }
          }));
        }
        
        // Handle save_address_alias function
        if (data.name === "save_address_alias") {
          const args = JSON.parse(data.arguments);
          console.log(`[${callId}] 🏠 Save alias requested: "${args.alias}" = "${args.address}"`);
          
          if (!userPhone) {
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  message: "Cannot save alias - no phone number associated with this call."
                })
              }
            }));
          } else {
            // Update the aliases in memory and database
            const aliasKey = normalize(args.alias).toLowerCase();
            callerAddressAliases[aliasKey] = args.address;
            
            const phoneKey = normalizePhone(userPhone) || userPhone;
            const { error: aliasError } = await supabase
              .from("callers")
              .update({ address_aliases: callerAddressAliases })
              .eq("phone_number", phoneKey);
            
            if (aliasError) {
              console.error(`[${callId}] Failed to save alias:`, aliasError);
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    message: "Sorry, couldn't save that alias. Please try again."
                  })
                }
              }));
            } else {
              console.log(`[${callId}] ✅ Saved alias: "${aliasKey}" = "${args.address}"`);
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: true,
                    message: `Saved! Next time you can just say "${args.alias}" and I'll know you mean ${args.address}.`,
                    confirmation_script: `Done! I've saved ${args.address} as your ${args.alias}. Next time just say "take me ${args.alias}" or "pick me up from ${args.alias}" and I'll know exactly where you mean.`
                  })
                }
              }));
            }
          }
          
          // Trigger response
          openaiWs?.send(JSON.stringify({
            type: "response.create",
            response: { modalities: ["audio", "text"] }
          }));
        }
        
        // Handle save_customer_name function - supports both NEW names and UPDATES
        if (data.name === "save_customer_name") {
          const args = JSON.parse(data.arguments);
          const providedName = (args.name || "").trim();
          const previousName = callerName;
          const isUpdate = !!previousName && previousName.toLowerCase() !== providedName.toLowerCase();
          console.log(`[${callId}] 👤 save_customer_name called with: "${providedName}"${isUpdate ? ` (updating from "${previousName}")` : ''}`);
          
          // Blocklist for invalid "names" that are actually common words or pronouns
          const invalidNames = new Set([
            'you', 'your', 'yours', 'me', 'my', 'mine', 'i', 'we', 'us', 'they', 'them', 'it',
            'yes', 'no', 'yeah', 'yep', 'okay', 'ok', 'sure', 'please', 'thanks', 'thank',
            'hello', 'hi', 'hey', 'bye', 'goodbye', 'cheers', 'ta', 'brilliant', 'lovely',
            'great', 'fine', 'good', 'morning', 'afternoon', 'evening', 'alright',
            'for', 'asap', 'now', 'today', 'taxi', 'cab', 'car', 'booking', 'book'
          ]);
          
          // Validate the name before saving
          const nameLower = providedName.toLowerCase();
          if (!providedName || providedName.length < 2 || providedName.length > 30 || invalidNames.has(nameLower)) {
            console.log(`[${callId}] ⚠️ Invalid name rejected: "${providedName}" (blocklisted or invalid length)`);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  message: "Invalid name. Ask the customer for their name again."
                })
              }
            }));
          } else {
            // Capitalize properly
            const formattedName = providedName.charAt(0).toUpperCase() + providedName.slice(1).toLowerCase();
            callerName = formattedName;
            
            console.log(`[${callId}] ✅ Customer name set to: "${callerName}"`);
            
            // Save to database if we have their phone
            if (userPhone) {
              const phoneKey = normalizePhone(userPhone) || userPhone;
              const { error: nameError } = await supabase
                .from("callers")
                .upsert({
                  phone_number: phoneKey,
                  name: callerName,
                  updated_at: new Date().toISOString()
                }, { onConflict: "phone_number" });
              
              if (nameError) {
                console.error(`[${callId}] Failed to save name to database:`, nameError);
              } else {
                console.log(`[${callId}] 💾 Saved name "${callerName}" to database for ${phoneKey}`);
              }
            }
            
            // Update live call with caller name
            queueLiveCallBroadcast({});
            
            // Determine appropriate response based on whether this is an update
            const isNameUpdate = previousName && previousName.toLowerCase() !== formattedName.toLowerCase();
            
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: true,
                  customer_name: formattedName,
                  was_update: isNameUpdate,
                  previous_name: isNameUpdate ? previousName : null,
                  message: isNameUpdate 
                    ? `Customer name UPDATED from "${previousName}" to "${formattedName}". Apologize briefly and use the new name.`
                    : `Customer name saved: ${formattedName}. Use this name to address them throughout the call.`,
                  next_action: isNameUpdate
                    ? `Say "Sorry about that ${formattedName}!" and continue with whatever you were doing.`
                    : `Say "Lovely to meet you ${formattedName}!" and continue with the booking flow.`
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
        
        // Handle find_nearby_places function - recommend venues/places to go
        if (data.name === "find_nearby_places") {
          const args = JSON.parse(data.arguments);
          console.log(`[${callId}] 🔍 find_nearby_places called: category=${args.category}, context=${args.context_address || 'auto'}`);
          
          // Determine location context for search (pickup → destination → callerCity)
          let searchCity = callerCity;
          let searchCoords: { lat: number; lng: number } | null = null;
          
          // Use provided context address or infer from booking state
          const contextAddress = args.context_address || knownBooking.pickup || knownBooking.destination;
          
          if (contextAddress) {
            // Try to extract city from the context address
            const cities = [
              "Birmingham", "Coventry", "Manchester", "Liverpool", "London", "Leeds",
              "Sheffield", "Bristol", "Nottingham", "Leicester", "Newcastle", "Cardiff",
              "Wolverhampton", "Solihull", "Walsall", "Dudley", "West Bromwich"
            ];
            for (const city of cities) {
              if (contextAddress.toLowerCase().includes(city.toLowerCase())) {
                searchCity = city;
                break;
              }
            }
          }
          
          // City coordinates for nearby search
          const CITY_COORDS: Record<string, { lat: number; lng: number }> = {
            "birmingham": { lat: 52.4862, lng: -1.8904 },
            "coventry": { lat: 52.4068, lng: -1.5197 },
            "manchester": { lat: 53.4808, lng: -2.2426 },
            "liverpool": { lat: 53.4084, lng: -2.9916 },
            "london": { lat: 51.5074, lng: -0.1278 },
            "leeds": { lat: 53.8008, lng: -1.5491 },
            "sheffield": { lat: 53.3811, lng: -1.4701 },
            "bristol": { lat: 51.4545, lng: -2.5879 },
            "nottingham": { lat: 52.9548, lng: -1.1581 },
            "leicester": { lat: 52.6369, lng: -1.1398 },
            "newcastle": { lat: 54.9783, lng: -1.6178 },
            "cardiff": { lat: 51.4816, lng: -3.1791 },
            "wolverhampton": { lat: 52.5870, lng: -2.1288 },
            "solihull": { lat: 52.4130, lng: -1.7780 },
            "walsall": { lat: 52.5860, lng: -1.9829 },
            "dudley": { lat: 52.5085, lng: -2.0894 },
            "west bromwich": { lat: 52.5186, lng: -1.9945 }
          };
          
          if (searchCity) {
            searchCoords = CITY_COORDS[searchCity.toLowerCase()] || null;
          }
          
          if (!searchCoords) {
            // Default to Birmingham if no context
            searchCoords = CITY_COORDS["birmingham"];
            searchCity = "Birmingham";
          }
          
          // Map category to Google Places type
          const CATEGORY_TO_TYPE: Record<string, { type: string; keyword: string }> = {
            "hotel": { type: "lodging", keyword: "hotel" },
            "restaurant": { type: "restaurant", keyword: "restaurant" },
            "cafe": { type: "cafe", keyword: "cafe coffee" },
            "bar": { type: "bar", keyword: "bar pub" },
            "pub": { type: "bar", keyword: "pub" },
            "nightclub": { type: "night_club", keyword: "nightclub club" },
            "cinema": { type: "movie_theater", keyword: "cinema" },
            "theatre": { type: "theater", keyword: "theatre" },
            "shopping": { type: "shopping_mall", keyword: "shopping centre" }
          };
          
          const categoryInfo = CATEGORY_TO_TYPE[args.category.toLowerCase()] || { type: args.category, keyword: args.category };
          
          try {
            // Call taxi-trip-resolve with nearby_query
            const tripResolveResponse = await fetch(`${SUPABASE_URL}/functions/v1/taxi-trip-resolve`, {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`,
              },
              body: JSON.stringify({
                nearby_query: args.category,
                caller_city_hint: searchCity,
                country: "GB"
              }),
            });
            
            let places: Array<{ name: string; address: string; rating?: number }> = [];
            
            if (tripResolveResponse.ok) {
              const resolveResult = await tripResolveResponse.json();
              console.log(`[${callId}] 📍 Nearby search result:`, JSON.stringify(resolveResult, null, 2));
              
              if (resolveResult.nearby_results?.length > 0) {
                // Take top 3 results
                places = resolveResult.nearby_results.slice(0, 3).map((p: any) => ({
                  name: p.name,
                  address: p.address,
                  rating: p.rating
                }));
              }
            }
            
            if (places.length > 0) {
              const placesList = places.map((p, i) => 
                `${i + 1}. ${p.name}${p.rating ? ` (${p.rating}★)` : ''} - ${p.address}`
              ).join("\n");
              
              console.log(`[${callId}] ✅ Found ${places.length} ${args.category} recommendations`);
              
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: true,
                    category: args.category,
                    location_context: searchCity,
                    places: places,
                    message: `Found ${places.length} ${args.category} options near ${searchCity}:\n${placesList}`,
                    suggestion: "Present these options to the customer and ask if they'd like a taxi to any of them."
                  })
                }
              }));
            } else {
              console.log(`[${callId}] ⚠️ No ${args.category} places found near ${searchCity}`);
              
              openaiWs?.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    category: args.category,
                    message: `Sorry, I couldn't find any ${args.category} recommendations near ${searchCity}. Ask the customer if they have a specific place in mind.`
                  })
                }
              }));
            }
          } catch (err) {
            console.error(`[${callId}] find_nearby_places error:`, err);
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: false,
                  message: "Sorry, I couldn't search for venues right now. Ask the customer if they have a specific destination in mind."
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

          // If OpenAI calls end_call before the transcription for the just-committed user turn arrives,
          // lastFinalUserTranscript will still reflect the *previous* turn. Wait briefly for the new transcript.
          const waitForFreshTranscript = async () => {
            if (!awaitingResponseAfterCommit) return;
            if (lastFinalUserTranscriptAt >= lastAudioCommitAt) return;

            const start = Date.now();
            while (Date.now() - start < 1500) {
              await new Promise((r) => setTimeout(r, 150));
              if (lastFinalUserTranscriptAt >= lastAudioCommitAt) return;
            }
          };

          await waitForFreshTranscript();

          const t = (lastFinalUserTranscript || "").toLowerCase().trim();
          
          // Check if assistant already said goodbye in a previous turn
          const assistantAlreadySaidGoodbye = (() => {
            // Look at all assistant messages, not just the last one
            const assistantMessages = transcriptHistory.filter((m) => m.role === "assistant");
            for (const msg of assistantMessages) {
              const a = (msg.text || "").toLowerCase();
              if (a.includes("goodbye") || a.includes("have a great journey") || /\bbye\b/.test(a)) {
                return true;
              }
            }
            return false;
          })();
          
          // If assistant has already said goodbye, accept any short acknowledgement from customer
          const isFinalAcknowledgement = assistantAlreadySaidGoodbye && (
            t === "ok" ||
            t === "okay" ||
            t === "bye" ||
            t === "bye bye" ||
            t === "cheers" ||
            t === "thanks" ||
            t === "thank you" ||
            t === "ta" ||
            t.length < 15 // Short responses after goodbye are almost always acknowledgements
          );
          
          // Check if customer said YES (they want something else) - these should NEVER end the call
          const customerSaidYes =
            t === "yes" ||
            t === "yeah" ||
            t === "yep" ||
            t === "please" ||
            t === "yes please" ||
            t === "yeah please" ||
            t.includes("yes please") ||
            t.includes("yeah please") ||
            t.includes("actually yes") ||
            t.includes("one more") ||
            t.includes("another") ||
            (t.includes("yeah") && t.includes("please"));
          
          // Only accept end_call if customer clearly declined, NOT if they said yes
          const customerSaidNo =
            !customerSaidYes && (
              isFinalAcknowledgement ||
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
              t.includes("no thank you") ||
              t.includes("that's fine") ||
              t.includes("thats fine") ||
              t.includes("that's alright") ||
              t.includes("thats alright") ||
              t.includes("that's okay") ||
              t.includes("thats ok") ||
              t.includes("that's ok")
            );

          const assistantSaidGoodbye = (() => {
            const lastAssistant = [...transcriptHistory].reverse().find((m) => m.role === "assistant");
            if (!lastAssistant?.text) return false;
            const a = lastAssistant.text.toLowerCase();
            return a.includes("goodbye") || a.includes("have a great journey") || /\bbye\b/.test(a);
          })();

          // Allow a silence-timeout hangup (customer didn't answer after "Anything else?")
          const silenceTimeoutEligible =
            String(args.reason || "") === "silence_timeout" &&
            followupAskedAt > 0 &&
            Date.now() - followupAskedAt >= getFollowupSilenceTimeoutMs() - 250 &&
            lastUserActivityAt <= followupAskedAt;

          if (silenceTimeoutEligible) {
            console.log(`[${callId}] ✅ Allowing end_call due to silence_timeout (no user reply)`);
          }

          // Safety: don't allow hanging up unless customer explicitly declined further help,
          // EXCEPT when we intentionally end due to silence_timeout.
          if (!customerSaidNo && !silenceTimeoutEligible) {
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
                      "Customer has not confirmed they're finished. Do NOT speak. Wait silently for their reply.",
                  }),
                },
              }),
            );

            // IMPORTANT: If we're already in the "Anything else?" follow-up window, DO NOT prompt again.
            // Re-prompting here causes the duplicate "anything else" question.
            if (followupAskedAt === 0) {
              // Nudge the model to continue (otherwise it may stall after the tool rejection)
              setTimeout(() => {
                openaiWs?.send(
                  JSON.stringify({
                    type: "response.create",
                    response: { modalities: ["audio", "text"] },
                  }),
                );
              }, 50);
            }
            return;
          }

          // Enforce: say goodbye first, then call end_call (prevents silent hangups)
          if (!assistantSaidGoodbye) {
            openaiWs?.send(
              JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    message:
                      "Customer has declined further help. Say a brief goodbye now, then call end_call immediately.",
                  }),
                },
              }),
            );

            setTimeout(() => {
              openaiWs?.send(
                JSON.stringify({
                  type: "response.create",
                  response: { modalities: ["audio", "text"] },
                }),
              );
            }, 50);
            return;
          }

          console.log(`[${callId}] 📞 End call requested: ${args.reason}`);
          endCallInProgress = true;
          callEnded = true; // Mark immediately to prevent reconnect from re-greeting
          clearAllCallTimers();

          // Update call status in DB immediately to prevent reconnect attempts
          await broadcastLiveCall({
            status: "ended",
            ended_at: new Date().toISOString(),
          });
          
          // Clear any resume data so reconnects don't restart the call
          await supabase.from("live_calls").update({
            clarification_attempts: null // Clear resume data
          }).eq("call_id", callId);

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

          // Delay sending call_ended so Ada's goodbye audio finishes playing
          // Asterisk bridge will hang up immediately on receiving this, so we wait
          setTimeout(() => {
            console.log(`[${callId}] 📞 Sending call_ended after goodbye audio delay`);
            socket.send(
              JSON.stringify({
                type: "call_ended",
                reason: args.reason,
              }),
            );

            // Close the WebSocket connection shortly after
            setTimeout(() => {
              console.log(`[${callId}] 📞 Closing connection after end_call`);
              socket.close();
            }, 500);
          }, 4000); // 4 seconds for goodbye audio to complete
        }
      }

      // Response completed
      if (data.type === "response.done") {
        // Calculate estimated playback duration from accumulated audio bytes
        // Base64 audio: 4 chars = 3 bytes, 24kHz 16-bit mono = 48000 bytes/sec
        // So: (base64Len * 0.75) / 48000 = seconds of audio
        const estimatedPlaybackMs = aiPlaybackBytesTotal > 0 
          ? Math.ceil((aiPlaybackBytesTotal * 0.75 / 48000) * 1000) 
          : 0;
        const elapsedSinceStart = Date.now() - aiPlaybackStartedAt;
        const remainingPlaybackMs = Math.max(0, estimatedPlaybackMs - elapsedSinceStart);
        const echoGuardMs = agentConfig?.echo_guard_ms ?? ECHO_GUARD_MS_DEFAULT;
        
        console.log(`[${callId}] >>> response.done - playback: ~${estimatedPlaybackMs}ms, elapsed: ${elapsedSinceStart}ms, remaining: ${remainingPlaybackMs}ms, echo_guard: ${echoGuardMs}ms`);
        
        // Clear any existing timeout
        if (aiPlaybackTimeoutId) {
          clearTimeout(aiPlaybackTimeoutId);
          aiPlaybackTimeoutId = null;
        }
        
        // Delay aiSpeaking=false until playback completes + echo guard
        const delayMs = remainingPlaybackMs + echoGuardMs;
        if (delayMs > 50) {
          console.log(`[${callId}] 🔇 Delaying aiSpeaking=false by ${delayMs}ms (playback+echo guard)`);
          aiPlaybackTimeoutId = setTimeout(() => {
            aiSpeaking = false;
            aiStoppedSpeakingAt = Date.now();
            console.log(`[${callId}] 🔇 aiSpeaking=false after playback delay`);
            socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
          }, delayMs);
        } else {
          aiSpeaking = false;
          aiStoppedSpeakingAt = Date.now();
          socket.send(JSON.stringify({ type: "ai_speaking", speaking: false }));
        }
        
        // Reset playback tracking for next response
        aiPlaybackBytesTotal = 0;
        aiPlaybackStartedAt = 0;
        
        // CRITICAL: Save session state after each response for reconnect resilience
        // This ensures resume data is fresh even if heartbeat hasn't fired yet
        if (greetingSent && transcriptHistory.length > 0 && !callEnded) {
          saveSessionForResume().catch(e => console.error(`[${callId}] Session save failed:`, e));
        }
        
        socket.send(JSON.stringify({ type: "response_done" }));
      }

      // Error handling
      if (data.type === "error") {
        console.error(`[${callId}] OpenAI error:`, data.error);

        const errType = (data as any)?.error?.type;

        // OpenAI sometimes returns transient server_error; auto-reconnect instead of killing the call
        if (errType === "server_error") {
          try {
            socket.send(
              JSON.stringify({
                type: "error",
                error: data.error,
                retrying: true,
                next_attempt: openAiReconnectAttempts + 1,
              }),
            );
          } catch (_) {
            // ignore
          }
          scheduleOpenAiReconnect("server_error");
        } else {
          socket.send(JSON.stringify({ type: "error", error: data.error }));
        }
      }
    };

    openaiWs.onerror = (error) => {
      console.error(`[${callId}] OpenAI WebSocket error:`, error);
    };

    openaiWs.onclose = () => {
      console.log(`[${callId}] OpenAI connection closed`);
      if (callEnded) return;

      // If we closed on purpose to reconnect, don't schedule twice.
      if (openAiClosingForReconnect) {
        openAiClosingForReconnect = false;
        return;
      }

      scheduleOpenAiReconnect("onclose");
    };
  };

  socket.onopen = () => {
    console.log(`[${callId}] Client connected`);
    connectToOpenAI();
  };

  socket.onmessage = async (event) => {
    try {
      // BINARY AUDIO PATH: Handle raw PCM bytes directly (no base64 overhead)
      if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
        if (!sessionReady) {
          // Queue binary audio until session is ready
          const audioBytes = event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : event.data;
          const base64Audio = btoa(String.fromCharCode(...audioBytes));
          pendingMessages.push({ type: "audio", audio: base64Audio });
          console.log(`[${callId}] ⏳ Binary audio received before session ready - queueing (${audioBytes.length} bytes)`);
          return;
        }
        
        // Forward audio to OpenAI
        // NOTE: Some OpenAI Realtime sessions reject raw binary frames depending on protocol/version.
        // To keep this robust, we always wrap audio in input_audio_buffer.append.
        if (openaiWs?.readyState === WebSocket.OPEN) {
          const audioBytes = event.data instanceof ArrayBuffer ? new Uint8Array(event.data) : event.data;
          const base64Audio = btoa(String.fromCharCode(...audioBytes));

          openaiWs.send(
            JSON.stringify({
              type: "input_audio_buffer.append",
              audio: base64Audio,
            }),
          );

          // USER AUDIO MONITORING - DISABLED for now (causes too much DB load)
          // To re-enable: uncomment below and set a higher throttle interval
          // const now = Date.now();
          // if (now - lastUserAudioBroadcastAt >= 1000) {
          //   lastUserAudioBroadcastAt = now;
          //   void supabase.from("live_call_audio").insert({
          //     call_id: callId,
          //     audio_chunk: base64Audio,
          //     audio_source: "user",
          //     created_at: new Date().toISOString()
          //   });
          // }

          // Log periodically (not every frame to reduce noise)
          if (Math.random() < 0.01) {
            console.log(`[${callId}] 🔊 Audio appended: ${audioBytes.length} bytes`);
          }
        }
        return;
      }
      
      const message = JSON.parse(event.data);
      
      // Set call ID from client
      if (message.type === "init") {
        const isReconnect = message.reconnect === true;
        callId = message.call_id || callId;
        
        if (isReconnect) {
          console.log(`[${callId}] 🔄 Reconnect init received - attempting session resume...`);
          
          // Check if call has ended BEFORE attempting resume
          const { data: callCheck } = await supabase
            .from("live_calls")
            .select("status")
            .eq("call_id", callId)
            .single();
          
          if (callCheck?.status === "ended") {
            console.log(`[${callId}] 🚫 BLOCKING RECONNECT - call has already ended, closing connection`);
            socket.send(JSON.stringify({ 
              type: "error", 
              message: "Call has already ended" 
            }));
            socket.close();
            return;
          }
          
          // Try to restore session from database
          const resumed = await loadResumedSession();
          
          if (resumed) {
            console.log(`[${callId}] ✅ Session resume successful - will prime OpenAI with history`);
            socket.send(JSON.stringify({ 
              type: "session_resumed", 
              call_id: callId,
              resumed: true,
              transcript_count: transcriptHistory.length 
            }));
            // Note: primeOpenAiWithHistory() will be called after session.updated
            return;
          } else {
            // Resume failed but this IS a reconnect attempt - don't start fresh, just close
            console.log(`[${callId}] ⚠️ Session resume failed (expired/no data) - closing reconnect attempt`);
            socket.send(JSON.stringify({ 
              type: "session_resumed", 
              call_id: callId,
              resumed: false 
            }));
            socket.close();
            return;
          }
        }
        
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
        
        // Enable/disable unified AI extraction (default: false - uses inline regex)
        if (message.useUnifiedExtraction !== undefined) {
          useUnifiedExtraction = message.useUnifiedExtraction;
          console.log(`[${callId}] 🧪 Unified Extraction: ${useUnifiedExtraction ? "ENABLED (taxi-extract-unified)" : "DISABLED (inline regex)"}`);
        }
        
        // Set caller's city for location-biased geocoding
        if (message.city) {
          callerCity = message.city;
          console.log(`[${callId}] 🏙️ Caller city from Asterisk: ${callerCity}`);
        }
        
        // If name provided directly from Asterisk, validate and use it
        if (isValidCallerName(message.user_name)) {
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
        } else if (message.user_name) {
          console.log(`[${callId}] ⚠️ Rejected invalid name from Asterisk: "${message.user_name}"`);
        }
        
        // Load agent configuration (default to 'ada' if not specified)
        const agentSlug = message.agent || "ada";
        await loadAgentConfig(agentSlug);
        
        // Voice override from client (allows testing different voices without changing agent)
        if (message.voice && typeof message.voice === "string") {
          const validVoices = ["shimmer", "alloy", "echo", "fable", "onyx", "nova", "ash", "ballad", "coral", "sage", "verse"];
          if (validVoices.includes(message.voice.toLowerCase())) {
            if (agentConfig) {
              agentConfig.voice = message.voice.toLowerCase();
              console.log(`[${callId}] 🎤 Voice override: ${agentConfig.voice}`);
            }
          } else {
            console.log(`[${callId}] ⚠️ Invalid voice override ignored: ${message.voice}`);
          }
        }

        // Detect Asterisk calls by call_id prefix
        if (callId.startsWith("ast-") || callId.startsWith("asterisk-") || callId.startsWith("call_")) {
          callSource = "asterisk";
        }
        console.log(`[${callId}] Call initialized (source: ${callSource}, phone: ${userPhone}, caller: ${callerName || 'unknown'}, city: ${callerCity || 'unknown'}, geocoding: ${geocodingEnabled}, agent: ${agentConfig?.name || 'default'}, voice: ${agentConfig?.voice || 'shimmer'})`);
        return;
      }
      
      // Forward audio to OpenAI - JSON/base64 path (legacy support)
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
          timestamp: new Date().toISOString(),
        });
        queueLiveCallBroadcast({});
        
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
        hangupReceived = true;
        socket.close();
      }

    } catch (error) {
      console.error(`[${callId}] Message parse error:`, error);
    }
  };

  socket.onclose = async () => {
    console.log(`[${callId}] 📞 Client disconnected - STOPPING Ada immediately`);

    // CRITICAL: Close OpenAI connection FIRST to stop Ada from generating more audio
    // This prevents Ada from continuing to talk after caller hangs up
    // Close in any state (OPEN or CONNECTING)
    if (openaiWs) {
      const wsState = openaiWs.readyState;
      console.log(`[${callId}] 🛑 Closing OpenAI WebSocket (state: ${wsState}) to stop Ada`);
      try {
        openaiWs.close();
      } catch (e) {
        console.log(`[${callId}] OpenAI WebSocket close error (expected):`, e);
      }
    }

    // Clear all timers to prevent any delayed actions
    clearAllCallTimers();

    // If we received an explicit hangup from telephony, the call is OVER and must NOT be resumable.
    if (!callEnded && hangupReceived) {
      console.log(`[${callId}] ✅ Treating disconnect as HANGUP - marking call as ended and clearing resume data`);
      callEnded = true;

      // Mark ended in DB to prevent session resume / re-greeting loops
      await broadcastLiveCall({
        status: "ended",
        ended_at: new Date().toISOString(),
      });

      // Clear any resume data so reconnects don't restart the call
      await supabase
        .from("live_calls")
        .update({ clarification_attempts: null })
        .eq("call_id", callId);

      // Update call end time
      await supabase
        .from("call_logs")
        .update({ call_end_at: new Date().toISOString() })
        .eq("call_id", callId);

      return;
    }

    // Otherwise: save for potential resume (temporary network drop)
    // Only save if call wasn't already formally ended (e.g., via end_call tool)
    if (!callEnded && transcriptHistory.length > 0) {
      console.log(`[${callId}] 💾 Saving session for potential resume...`);
      await saveSessionForResume();

      // Mark the call as "disconnected" (not "completed") so it can be resumed
      await supabase
        .from("live_calls")
        .update({
          status: "disconnected",
          updated_at: new Date().toISOString(),
        })
        .eq("call_id", callId);
    } else {
      // Call was formally ended - mark as completed
      callEnded = true;
    }

    // Update call end time
    await supabase
      .from("call_logs")
      .update({ call_end_at: new Date().toISOString() })
      .eq("call_id", callId);

    // Only broadcast "completed" if the call is truly over
    if (callEnded) {
      await broadcastLiveCall({
        status: "completed",
        ended_at: new Date().toISOString(),
      });
    }
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] WebSocket error:`, error);
  };

  return response;
});
