import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_INSTRUCTIONS = `You are Ada, a global AI taxi dispatcher for 247 Radio Carz.

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
  - If they want to CHANGE the booking: Use modify_booking tool with the changes
- For RETURNING customers WITH a usual destination (but NO active booking): "Hello [NAME]! Lovely to hear from you again. Shall I book you a taxi to [LAST_DESTINATION], or are you heading somewhere different today?"
- For RETURNING customers WITHOUT a usual destination: "Hello [NAME]! Lovely to hear from you again. How can I help with your travels today?"
- For NEW customers: "Hello and welcome to 247 Radio Carz! My name's Ada. What's your name please?"
- **CRITICAL: When a NEW customer tells you their name, you MUST call the save_customer_name tool IMMEDIATELY with their EXACT name.**
- After they give their name, ask for their area: "Lovely to meet you [NAME]! And what area are you calling from - Coventry, Birmingham, or somewhere else?"

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
BOOKING FLOW
════════════════════════════════════
1. Greet the customer (get their name if new)
2. Ask: "When do you need the taxi? Is it for now or a later time?"
   - "now", "asap", "straight away" → pickup_time = "ASAP"
   - Specific time → convert to YYYY-MM-DD HH:MM format
   - If they give PICKUP + DESTINATION in same sentence, assume "ASAP"
3. Ask: "Where would you like to be picked up from?"
4. When they give pickup, acknowledge briefly: "Got it." Then ask: "And where are you heading to?"
5. When they give destination, ask: "How many passengers?"
6. Once you have ALL 4 details, do ONE confirmation
7. DO NOT repeat addresses one-by-one during collection - save ALL for final summary

════════════════════════════════════
CONFIRMATION (MANDATORY)
════════════════════════════════════
When all required details are available:
"So that's [TIME] from [PICKUP] to [DESTINATION] for [X] passengers — shall I book that?"

- ONLY call book_taxi after a clear confirmation: "yes", "yeah", "please", "go ahead", "book it"
- If they correct something: update and ask for confirmation AGAIN
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
AIRPORT INTELLIGENCE
════════════════════════════════════
If pickup or destination is an airport, train station, or coach station:
- Ask about luggage: "Are you travelling with any luggage today?"
- Suggest a suitable vehicle if luggage or passengers require it.
- If pickup is from an airport, ask for terminal if missing.

════════════════════════════════════
LUGGAGE & VEHICLE RULES
════════════════════════════════════
- If 6+ passengers: "Right, that's 6 passengers - I'll book you a 6-seater."
- If 3+ luggage items AND 2+ passengers: "With 3 bags and 2 passengers, I'll book you an estate for the extra space."
- Include vehicle type in FINAL CONFIRMATION when triggered.
- Vehicle types: saloon (standard), estate (extra boot), MPV, 6-seater, 7-seater, 8-seater, minibus

════════════════════════════════════
RECOMMENDATIONS
════════════════════════════════════
If the customer asks for nearby hotels, restaurants, cafes, or bars:
- Use the find_nearby_places tool with appropriate category
- Context is taken from: pickup → destination → last known location
- Suggest 2–3 options only.
- Ask if they want to go to one of them.

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
SAFETY RULES (CRITICAL)
════════════════════════════════════
- NEVER say "That's all booked" unless book_taxi returns success: true
- NEVER invent fares or ETAs - use values from book_taxi response
- NEVER reuse a fare from a previous booking
- ONLY cancel if customer explicitly says "cancel"
- ⚠️ Saying "booked" without calling book_taxi means NO TAXI COMING - customers will be stranded!

════════════════════════════════════
TOOL CALL SEQUENCE
════════════════════════════════════
1. Customer says confirmation word → IMMEDIATELY call book_taxi
2. DO NOT SPEAK until you receive the book_taxi response!
3. ONLY after success: true → "Brilliant! That's all booked. The fare is £[X] and your driver will be with you in [ETA]."
4. If requires_verification: true → Say the verification_script EXACTLY

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

If customer says "no", "nope", "that's all", "nothing else":
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
  let callerKnownAreas: Record<string, number> = {}; // {"Coventry": 5, "Birmingham": 1} - city mention counts
  let callerTrustedAddresses: string[] = []; // Array of addresses the caller has successfully used before
  let callerAddressAliases: Record<string, string> = {}; // {"home": "52A David Road", "work": "Coventry Train Station"}
  let activeBooking: { id: string; pickup: string; destination: string; passengers: number; fare: string; booked_at: string } | null = null; // Outstanding booking
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let currentAssistantText = ""; // Buffer for assistant transcript
  let aiSpeaking = false; // Local speaking flag (used for safe barge-in cancellation)
  let aiStoppedSpeakingAt = 0; // Timestamp when AI stopped speaking (for echo guard)
  const ECHO_GUARD_MS = 100; // Ignore transcripts within 100ms after AI stops speaking (tight to avoid blocking real speech)
  let lastFinalUserTranscript = ""; // Last finalized user transcript (safeguards for end_call)
  let lastFinalUserTranscriptAt = 0; // ms epoch for race-free end_call checks
  let lastAudioCommitAt = 0; // ms epoch when last user turn was committed
  let geocodingEnabled = true; // Enable address verification by default
  let addressTtsSplicingEnabled = false; // Enable address TTS splicing (off by default)
  let greetingSent = false; // Prevent duplicate greetings on session.updated
  
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
        vad_silence_duration_ms: data.vad_silence_duration_ms ?? 1800,
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

  // --- Call lifecycle + "Ada didn't finish" safeguards ---
  // If Ada asks "Anything else?" and the customer goes silent, end the call reliably.
  const FOLLOWUP_SILENCE_TIMEOUT_MS = 8000;

  // If Ada asks any question and we get no detectable user activity (VAD/transcription),
  // reprompt once or twice so calls don't get "stuck" on the first question.
  const NO_REPLY_REPROMPT_MS = 9000;
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

    followupAskedAt = Date.now();

    console.log(`[${callId}] ⏳ Armed follow-up silence timeout (${FOLLOWUP_SILENCE_TIMEOUT_MS}ms) context=${context}`);

    followupSilenceTimer = setTimeout(() => {
      if (callEnded || endCallInProgress) return;

      // If any user activity happened after we asked, do nothing.
      if (lastUserActivityAt > followupAskedAt) return;

      console.log(`[${callId}] ⏰ Follow-up silence timeout hit - requesting goodbye + end_call`);

      if (sessionReady && openaiWs?.readyState === WebSocket.OPEN) {
        openaiWs.send(JSON.stringify({
          type: "response.create",
          response: {
            modalities: ["audio", "text"],
            instructions:
              "The customer has not replied. Politely say a short goodbye now, then call the end_call tool with reason 'silence_timeout'.",
          },
        }));

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
    }, FOLLOWUP_SILENCE_TIMEOUT_MS);
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

    console.log(`[${callId}] ⏳ Armed no-reply reprompt (${NO_REPLY_REPROMPT_MS}ms) context=${context}`);

    noReplyRepromptTimer = setTimeout(() => {
      if (callEnded || endCallInProgress) return;

      // If the user did anything since we armed, do nothing.
      if (lastUserActivityAt > awaitingReplyAt) return;

      if (!sessionReady || openaiWs?.readyState !== WebSocket.OPEN) return;
      if (aiSpeaking) return; // don't talk over ourselves

      noReplyRepromptCount += 1;
      console.log(`[${callId}] 🔁 No-reply reprompt firing (#${noReplyRepromptCount})`);

      const q = awaitingQuestionText?.trim();
      const instructions = q
        ? `You did not hear a reply from the customer. Repeat this exact question verbatim (no extra words): "${q}" Then STOP speaking and wait for their answer.`
        : "You did not hear a reply from the customer. Briefly repeat your last question, clearly and politely, and wait for their answer.";

      openaiWs.send(
        JSON.stringify({
          type: "response.create",
          response: {
            modalities: ["audio", "text"],
            instructions,
          },
        }),
      );

      // Re-arm for a second attempt if needed
      if (noReplyRepromptCount < MAX_NO_REPLY_REPROMPTS) {
        armNoReplyReprompt("reprompt_again", q || undefined);
      }
    }, NO_REPLY_REPROMPT_MS);
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

  type KnownBooking = {
    pickup?: string;
    destination?: string;
    passengers?: number;
    pickupTime?: string; // "ASAP" or "YYYY-MM-DD HH:MM" format
    pickupVerified?: boolean;
    destinationVerified?: boolean;
    highFareVerified?: boolean; // Track if high fare has been verified with customer
    verifiedFare?: number; // Store the verified fare to use on confirmation
    // Alternative addresses (when STT and Ada differ, store both for Ada to choose)
    pickupAlternative?: string; // Ada's interpretation if different from STT
    destinationAlternative?: string; // Ada's interpretation if different from STT
    vehicleType?: string; // e.g., "6 seater", "saloon", "MPV", "estate" - captured from customer request
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
  const maybeUpdateCallerCityFromText = async (text: string) => {
    const hintedCity = extractCityFromAddress(text);
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
        .select("name, last_pickup, last_destination, total_bookings, trusted_addresses, known_areas, address_aliases")
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
        
        // Load known_areas for reference
        callerKnownAreas = (data.known_areas as Record<string, number>) || {};

        // Load address aliases (e.g., {"home": "52A David Road", "work": "Train Station"})
        callerAddressAliases = (data.address_aliases as Record<string, string>) || {};
        if (Object.keys(callerAddressAliases).length > 0) {
          console.log(`[${callId}] 🏠 Loaded address aliases: ${JSON.stringify(callerAddressAliases)}`);
        }

        // Derive primary city (bias) with better priorities:
        // 1) Any explicit city embedded in saved aliases (most reliable for "home" area)
        // 2) last_pickup city
        // 3) known_areas counts
        // 4) last_destination city
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

        // 2) last_pickup city
        if (!callerCity && callerLastPickup) {
          const pickupCity = extractCityFromAddress(callerLastPickup);
          if (pickupCity) {
            callerCity = pickupCity;
            console.log(`[${callId}] 🏙️ Primary city from last_pickup: ${callerCity}`);
          }
        }

        // 3) known_areas
        if (!callerCity && Object.keys(callerKnownAreas).length > 0) {
          const topCity = Object.entries(callerKnownAreas).sort((a, b) => b[1] - a[1])[0];
          if (topCity) {
            callerCity = topCity[0];
            console.log(`[${callId}] 🏙️ Primary city from known_areas: ${callerCity} (${topCity[1]} mentions)`);
          }
        }

        // 4) last_destination city
        if (!callerCity && callerLastDestination) {
          callerCity = extractCityFromAddress(callerLastDestination);
          if (callerCity) console.log(`[${callId}] 🏙️ Primary city from last_destination: ${callerCity}`);
        }

        
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
      
      // Normalize addresses for comparison (use core address without city for deduplication)
      const normalizedTrusted = new Set(updatedTrusted.map(a => {
        // Extract core address (house number + street) for comparison
        const core = normalize(a).toLowerCase().split(',')[0].trim();
        return core;
      }));
      
      // Helper to enrich address with city if missing
      const ensureAddressHasCity = (addr: string): string => {
        if (!addr) return addr;
        const hasCity = extractCityFromAddress(addr);
        if (hasCity) return addr; // Already has city
        const hasPostcode = /[A-Z]{1,2}\d{1,2}\s*\d[A-Z]{2}/i.test(addr);
        if (hasPostcode) return addr; // Has postcode, good enough
        if (callerCity) {
          return `${addr}, ${callerCity}`;
        }
        return addr;
      };
      
      // Add pickup if not already trusted (with city for future lookups)
      const pickupCore = normalize(booking.pickup || "").toLowerCase().split(',')[0].trim();
      if (booking.pickup && !normalizedTrusted.has(pickupCore)) {
        const enrichedPickup = ensureAddressHasCity(booking.pickup);
        updatedTrusted.push(enrichedPickup);
        console.log(`[${callId}] 🏠 Adding pickup to trusted addresses: "${enrichedPickup}"`);
      }
      
      // Add destination if not already trusted (with city for future lookups)
      const destCore = normalize(booking.destination || "").toLowerCase().split(',')[0].trim();
      if (booking.destination && !normalizedTrusted.has(destCore)) {
        const enrichedDest = ensureAddressHasCity(booking.destination);
        updatedTrusted.push(enrichedDest);
        console.log(`[${callId}] 🏠 Adding destination to trusted addresses: "${enrichedDest}"`);
      }
      
      // Trim to max size (keep most recent)
      if (updatedTrusted.length > MAX_TRUSTED_ADDRESSES) {
        updatedTrusted = updatedTrusted.slice(-MAX_TRUSTED_ADDRESSES);
      }
      
      // Enrich last_pickup and last_destination with city for future lookups
      const enrichedPickup = ensureAddressHasCity(booking.pickup);
      const enrichedDestination = ensureAddressHasCity(booking.destination);
      
      if (existing) {
        // Update existing caller
        const { error } = await supabase.from("callers").update({
          last_pickup: enrichedPickup,
          last_destination: enrichedDestination,
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
          last_pickup: enrichedPickup,
          last_destination: enrichedDestination,
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
        updated_at: new Date().toISOString(),
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
                queueLiveCallBroadcast({});
                
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
                  queueLiveCallBroadcast({});
                }
              }
              
              // If extracted succeeds AND Ada has a different interpretation, check if Ada's is ALSO valid
              // GOOGLE ARBITER: Geocode both and pick based on Google's resolution quality
              if (pickupResult.found && adaPickup && normalize(adaPickup) !== normalize(extractedPickup)) {
                console.log(`[${callId}] 🔍 DUAL-SOURCE: STT succeeded, checking Ada's version too: "${adaPickup}"`);
                const adaResult = await geocodeAddress(adaPickup, shouldCheckAmbiguous, "pickup");
                if (adaResult.found) {
                  console.log(`[${callId}] ✅ DUAL-SOURCE: Both geocoded! STT="${extractedPickup}" Ada="${adaPickup}"`);
                  
                  // GOOGLE ARBITER: Compare what Google resolved each to
                  const sttGoogleName = (pickupResult.display_name || pickupResult.formatted_address || '').toLowerCase();
                  const adaGoogleName = (adaResult.display_name || adaResult.formatted_address || '').toLowerCase();
                  const sttNorm = normalize(extractedPickup);
                  const adaNorm = normalize(adaPickup);
                  
                  // Calculate similarity between input and Google's output
                  // Higher similarity = Google understood the input better
                  const sttMatchesStt = sttGoogleName.includes(sttNorm.split(' ')[0]) || sttNorm.includes(sttGoogleName.split(',')[0].trim());
                  const adaMatchesAda = adaGoogleName.includes(adaNorm.split(' ')[0]) || adaNorm.includes(adaGoogleName.split(',')[0].trim());
                  
                  // Check if they resolve to the SAME place (same Google output)
                  const samePlace = sttGoogleName === adaGoogleName;
                  
                  console.log(`[${callId}] 🔬 GOOGLE ARBITER: STT→"${sttGoogleName}" Ada→"${adaGoogleName}" same=${samePlace}`);
                  
                  if (samePlace) {
                    // Both resolve to same place - prefer STT (what customer actually said)
                    console.log(`[${callId}] 📝 Same place - keeping STT version: "${extractedPickup}"`);
                  } else if (sttMatchesStt && !adaMatchesAda) {
                    // STT's input matched Google better - keep STT
                    console.log(`[${callId}] 📝 STT matched Google better - keeping: "${extractedPickup}"`);
                  } else if (adaMatchesAda && !sttMatchesStt) {
                    // Ada's input matched Google better - use Ada
                    usedAddress = adaPickup;
                    knownBooking.pickup = adaPickup;
                    pickupResult = adaResult;
                    console.log(`[${callId}] 📝 Ada matched Google better - using: "${adaPickup}"`);
                    transcriptHistory.push({
                      role: "system",
                      text: `📝 AUTO-PICK: Used Ada's "${adaPickup}" over STT's "${extractedPickup}" (Google validated)`,
                      timestamp: new Date().toISOString()
                    });
                    queueLiveCallBroadcast({});
                  } else {
                    // Neither clearly better - default to STT (what customer said)
                    console.log(`[${callId}] 📝 Inconclusive - defaulting to STT: "${extractedPickup}"`);
                  }
                } else {
                  // Ada's version failed geocoding - stick with STT (which succeeded)
                  console.log(`[${callId}] ❌ Ada's version "${adaPickup}" failed geocoding, keeping STT's "${extractedPickup}"`);
                }
              }
              // Clear any stale alternatives - we auto-pick now, no need to store
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
                      text: `⚠️ GOOGLE MISMATCH (pickup): "${extractedPickup}" ≠ "${cleanGoogleAddress}" (not auto-applying)`,
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
                queueLiveCallBroadcast({});
                
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
                  queueLiveCallBroadcast({});
                }
              }
              
              // If extracted succeeds AND Ada has a different interpretation, check if Ada's is ALSO valid
              // GOOGLE ARBITER: Geocode both and pick based on Google's resolution quality
              if (destResult.found && adaDest && normalize(adaDest) !== normalize(extractedDest)) {
                console.log(`[${callId}] 🔍 DUAL-SOURCE: STT succeeded, checking Ada's version too: "${adaDest}"`);
                const adaResult = await geocodeAddress(adaDest, shouldCheckAmbiguous, "destination");
                if (adaResult.found) {
                  console.log(`[${callId}] ✅ DUAL-SOURCE: Both geocoded! STT="${extractedDest}" Ada="${adaDest}"`);
                  
                  // GOOGLE ARBITER: Compare what Google resolved each to
                  const sttGoogleName = (destResult.display_name || destResult.formatted_address || '').toLowerCase();
                  const adaGoogleName = (adaResult.display_name || adaResult.formatted_address || '').toLowerCase();
                  const sttNorm = normalize(extractedDest);
                  const adaNorm = normalize(adaDest);
                  
                  // Calculate similarity between input and Google's output
                  // Higher similarity = Google understood the input better
                  const sttMatchesStt = sttGoogleName.includes(sttNorm.split(' ')[0]) || sttNorm.includes(sttGoogleName.split(',')[0].trim());
                  const adaMatchesAda = adaGoogleName.includes(adaNorm.split(' ')[0]) || adaNorm.includes(adaGoogleName.split(',')[0].trim());
                  
                  // Check if they resolve to the SAME place (same Google output)
                  const samePlace = sttGoogleName === adaGoogleName;
                  
                  console.log(`[${callId}] 🔬 GOOGLE ARBITER: STT→"${sttGoogleName}" Ada→"${adaGoogleName}" same=${samePlace}`);
                  
                  if (samePlace) {
                    // Both resolve to same place - prefer STT (what customer actually said)
                    console.log(`[${callId}] 📝 Same place - keeping STT version: "${extractedDest}"`);
                  } else if (sttMatchesStt && !adaMatchesAda) {
                    // STT's input matched Google better - keep STT
                    console.log(`[${callId}] 📝 STT matched Google better - keeping: "${extractedDest}"`);
                  } else if (adaMatchesAda && !sttMatchesStt) {
                    // Ada's input matched Google better - use Ada
                    usedAddress = adaDest;
                    knownBooking.destination = adaDest;
                    destResult = adaResult;
                    console.log(`[${callId}] 📝 Ada matched Google better - using: "${adaDest}"`);
                    transcriptHistory.push({
                      role: "system",
                      text: `📝 AUTO-PICK: Used Ada's "${adaDest}" over STT's "${extractedDest}" (Google validated)`,
                      timestamp: new Date().toISOString()
                    });
                    queueLiveCallBroadcast({});
                  } else {
                    // Neither clearly better - default to STT (what customer said)
                    console.log(`[${callId}] 📝 Inconclusive - defaulting to STT: "${extractedDest}"`);
                  }
                } else {
                  // Ada's version failed geocoding - stick with STT (which succeeded)
                  console.log(`[${callId}] ❌ Ada's version "${adaDest}" failed geocoding, keeping STT's "${extractedDest}"`);
                }
              }
              // Clear any stale alternatives - we auto-pick now, no need to store
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
                      text: `⚠️ GOOGLE MISMATCH (destination): "${extractedDest}" ≠ "${cleanGoogleAddress}" (not auto-applying)`,
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
            voice: agentConfig?.voice || "shimmer", // Use agent's voice
            input_audio_format: "pcm16",
            output_audio_format: "pcm16",
            input_audio_transcription: { 
              model: "whisper-1"
            },
            // Server VAD - tuned for phone audio where pauses can be longer
            // Give user more time to complete their response before committing
            // IMPORTANT: We DO NOT auto-create responses; we trigger response.create only after STT completes.
            // This prevents Ada responding before Whisper has finalized the user's words ("out-of-order" turns).
            turn_detection: {
              type: "server_vad",
              threshold: agentConfig?.vad_threshold ?? 0.45,           // Agent's VAD sensitivity
              prefix_padding_ms: agentConfig?.vad_prefix_padding_ms ?? 650,    // Agent's lead-in capture
              silence_duration_ms: agentConfig?.vad_silence_duration_ms ?? 1800, // Agent's silence wait
              create_response: false,    // Manual response.create after transcription.completed
              interrupt_response: agentConfig?.allow_interruptions ?? true   // Agent's barge-in setting
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
                description: "Modify an active booking. Use this when the customer wants to change their pickup, destination, or number of passengers without cancelling the entire booking.",
                parameters: {
                  type: "object",
                  properties: {
                    new_pickup: { type: "string", description: "New pickup address (only if customer wants to change it)" },
                    new_destination: { type: "string", description: "New destination address (only if customer wants to change it)" },
                    new_passengers: { type: "integer", description: "New number of passengers (only if customer wants to change it)" },
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
                description: "Save the customer's name when they tell you their name. Call this IMMEDIATELY after the customer tells you their name (e.g., 'My name is John', 'I'm Sarah', 'It's Max'). Use the EXACT name they said - do NOT guess or make up names. Only call this once per call when you first learn their name.",
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
                description: "Find nearby hotels, restaurants, cafes, or bars based on known location context. Use when customer asks for recommendations like 'nearest hotel' or 'good restaurant nearby'.",
                parameters: {
                  type: "object",
                  properties: {
                    category: { type: "string", enum: ["hotel", "restaurant", "cafe", "bar"], description: "Type of place to search for" },
                    context_address: { type: "string", description: "Optional: Use pickup or destination as reference location" }
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

          // If Ada asked the "anything else" question, start a silence timeout so calls don't hang forever.
          if (
            a.includes("is there anything else i can help") ||
            a.includes("anything else i can help")
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

          // Only arm reprompt if user hasn't already replied recently (prevents double-fire)
          // If there's been user activity in the last 3 seconds, the user DID reply - don't reprompt
          const recentUserActivity = lastUserActivityAt > 0 && now - lastUserActivityAt < 3000;
          if (looksLikeQuestion && !a.includes("goodbye") && !/\bbye\b/.test(a) && !recentUserActivity) {
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

        // Log empty/very short transcripts for debugging
        if (!rawTranscript || rawTranscript.trim().length < 2) {
          console.log(`[${callId}] ⚠️ Empty or very short transcript received - likely audio quality issue`);
          // Don't let any pending "committed" turn trigger an AI response.
          awaitingResponseAfterCommit = false;
          responseCreatedSinceCommit = true;
          return;
        }

        // If Ada asked "anything else?" and the customer clearly said NO,
        // force a clean goodbye + end_call (prevents looping the follow-up question).
        let forcedResponseInstructions: string | null = null;
        const lastAssistantTextLower = ([...transcriptHistory]
          .reverse()
          .find((m) => m.role === "assistant")?.text
          ?.toLowerCase() || "");

        const adaAskedAnythingElse =
          lastAssistantTextLower.includes("is there anything else i can help") ||
          lastAssistantTextLower.includes("anything else i can help");

        if (adaAskedAnythingElse) {
          const t = rawTranscript.toLowerCase().trim();
          const saidNo =
            /^(no|nope|nah)\b/.test(t) ||
            /\b(no\s+thanks|nothing\s+else|that'?s\s+all|that\s+is\s+all|all\s+good|i'?m\s+good|im\s+good|i'?m\s+fine|im\s+fine)\b/.test(t);
          const saidYes = /\b(yes|yeah|yep|sure|please|ok|okay)\b/.test(t);

          if (saidNo && !saidYes) {
            forcedResponseInstructions =
              "The customer has said they do NOT need anything else. Say a short polite goodbye, then IMMEDIATELY call the end_call tool with reason 'no_further_assistance'. Do NOT ask any further questions.";
          }
        }
        // This is more reliable than time-based filtering on phone lines
        const isEchoOfAda = (transcript: string): boolean => {
          if (!currentAssistantText) return false;
          const t = transcript.toLowerCase().trim();
          const ada = currentAssistantText.toLowerCase();
          
          // Check if user transcript is a substantial substring of Ada's recent speech
          // (at least 20 chars and appears in Ada's text)
          if (t.length >= 20 && ada.includes(t)) {
            console.log(`[${callId}] 🔇 ECHO DETECTED: User transcript matches Ada's speech`);
            return true;
          }
          
          // Check for Ada's signature phrases being echoed back
          const adaPhrases = ['247 radio carz', 'lovely to hear from you', 'shall i book that', 
            'is there anything else', 'have a great journey', 'your driver will be with you'];
          for (const phrase of adaPhrases) {
            if (t.includes(phrase)) {
              console.log(`[${callId}] 🔇 ECHO DETECTED: Ada phrase "${phrase}" in user transcript`);
              return true;
            }
          }
          
          return false;
        };
        
        if (isEchoOfAda(rawTranscript)) {
          console.log(`[${callId}] 🔇 Discarding echo transcript: "${rawTranscript}"`);
          awaitingResponseAfterCommit = false;
          responseCreatedSinceCommit = true;
          return;
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
        
        console.log(`[${callId}] User said: ${rawTranscript}`);
        lastFinalUserTranscript = rawTranscript;
        lastFinalUserTranscriptAt = Date.now();

        // Detect and lock the customer's language from their FIRST valid transcript.
        // This prevents Ada from defaulting to English when the caller speaks Urdu (or other non-English languages).
        if (!languageLocked) {
          const hint = detectLanguageHint(rawTranscript);
          if (hint) applyLanguageLock(hint);
        }

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
          // Broadcast transcript update (queued to preserve order)
          queueLiveCallBroadcast({});
        }
        
        socket.send(
          JSON.stringify({
            type: "transcript",
            text: data.transcript,
            role: "user",
        }),
        );

        // Prefer sending response.create AFTER we have the finalized transcript processed.
        // This helps multilingual language-locking and address/name injections take effect before Ada replies.
        if (awaitingResponseAfterCommit && sessionReady && openaiWs?.readyState === WebSocket.OPEN && !responseCreatedSinceCommit) {
          const response = forcedResponseInstructions
            ? { modalities: ["audio", "text"], instructions: forcedResponseInstructions }
            : { modalities: ["audio", "text"] };

          openaiWs.send(JSON.stringify({
            type: "response.create",
            response,
          }));
          responseCreatedSinceCommit = true;
          console.log(`[${callId}] >>> response.create sent (after transcription.completed)`);
        }

        awaitingResponseAfterCommit = false;
      }

      // Speech started (barge-in detection) - CANCEL AI response for faster interruption
      if (data.type === "input_audio_buffer.speech_started") {
        console.log(`[${callId}] >>> User started speaking (barge-in)`);
        noteUserActivity("speech_started");
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
      // IMPORTANT: Do NOT create a response here.
      // We only create a response AFTER we have a finalized, non-empty transcription.
      // This prevents Ada from speaking/acting (e.g., cancelling) when the line is silent/noisy.
      if (data.type === "input_audio_buffer.committed") {
        console.log(`[${callId}] >>> Audio buffer committed, item_id: ${data.item_id}`);
        lastAudioCommitAt = Date.now();
        awaitingResponseAfterCommit = true;
        responseCreatedSinceCommit = false;
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
            vehicleType: knownBooking.vehicleType ?? args.vehicle_type ?? null, // e.g., "6 seater", "MPV"
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

          // Save booking to bookings table for persistence (always save, even without phone for web tests)
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
          
          console.log(`[${callId}] 💾 Attempting to save booking: ${finalBooking.pickup} → ${finalBooking.destination}`);
          
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
            luggage: null,
            special_requests: null,
            fare: `£${fare}`,
            eta: isAsap ? eta : null,
            status: "active",
            history: [
              { at: new Date().toISOString(), action: "created", by: callSource || "phone" }
            ]
          };
          
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
            booked_at: new Date().toISOString(),
            booking_details: bookingDetails
          }).select().single();
          
          if (bookingInsertError) {
            console.error(`[${callId}] ❌ CRITICAL: Failed to save booking:`, bookingInsertError);
            console.error(`[${callId}] Booking data was:`, { pickup: finalBooking.pickup, destination: finalBooking.destination, passengers: finalBooking.passengers, fare, phoneKey });
          } else {
            activeBooking = newBooking;
            console.log(`[${callId}] ✅ Booking saved successfully: ${bookingRef} (${isAsap ? 'ASAP' : `scheduled for ${scheduledTime}`})`);
            console.log(`[${callId}] 📋 Booking ID: ${newBooking.id}`);
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
              const finalPickup = updates.pickup || bookingToModify.pickup;
              const finalDestination = updates.destination || bookingToModify.destination;
              const finalPassengers = updates.passengers ?? bookingToModify.passengers ?? 1;
              
              if (args.new_pickup || args.new_destination) {
                // Use taxi-trip-resolve for consistent fare calculation (same as book_taxi)
                console.log(`[${callId}] 🔄 Recalculating fare via trip-resolve for modified booking`);
                
                try {
                  const tripResolveUrl = `${SUPABASE_URL}/functions/v1/taxi-trip-resolve`;
                  const tripResponse = await fetch(tripResolveUrl, {
                    method: "POST",
                    headers: {
                      "Content-Type": "application/json",
                      "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`
                    },
                    body: JSON.stringify({
                      pickup_input: finalPickup,
                      dropoff_input: finalDestination,
                      passengers: finalPassengers,
                      city_hint: callerCity || "Coventry",
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
        
        // Handle save_customer_name function
        if (data.name === "save_customer_name") {
          const args = JSON.parse(data.arguments);
          const providedName = (args.name || "").trim();
          console.log(`[${callId}] 👤 save_customer_name called with: "${providedName}"`);
          
          // Validate the name before saving
          if (!providedName || providedName.length < 2 || providedName.length > 30) {
            console.log(`[${callId}] ⚠️ Invalid name rejected: "${providedName}"`);
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
            
            openaiWs?.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({
                  success: true,
                  customer_name: callerName,
                  message: `Customer name saved: ${callerName}. Use this name to address them throughout the call.`,
                  next_action: `Say "Lovely to meet you ${callerName}!" and continue with the booking flow.`
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
            Date.now() - followupAskedAt >= FOLLOWUP_SILENCE_TIMEOUT_MS - 250 &&
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
                      "Customer has not confirmed they're finished. Ask again if they need anything else, then wait.",
                  }),
                },
              }),
            );

            // Nudge the model to continue (otherwise it may stall after the tool rejection)
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
          clearAllCallTimers();

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

          // Delay sending call_ended so Ada's goodbye audio finishes playing
          // Asterisk bridge will hang up immediately on receiving this, so we wait
          setTimeout(() => {
            if (callEnded) return;
            callEnded = true;
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
        aiSpeaking = false;
        aiStoppedSpeakingAt = Date.now(); // Record when AI stopped for echo guard
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

        // Detect Asterisk calls by call_id prefix
        if (callId.startsWith("ast-") || callId.startsWith("asterisk-") || callId.startsWith("call_")) {
          callSource = "asterisk";
        }
        console.log(`[${callId}] Call initialized (source: ${callSource}, phone: ${userPhone}, caller: ${callerName || 'unknown'}, city: ${callerCity || 'unknown'}, geocoding: ${geocodingEnabled}, agent: ${agentConfig?.name || 'default'})`);
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
        socket.close();
      }

    } catch (error) {
      console.error(`[${callId}] Message parse error:`, error);
    }
  };

  socket.onclose = async () => {
    console.log(`[${callId}] Client disconnected`);
    callEnded = true;
    clearAllCallTimers();

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
