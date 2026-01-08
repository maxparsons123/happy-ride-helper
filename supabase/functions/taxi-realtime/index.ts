import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

const SYSTEM_INSTRUCTIONS = `You are Ada, a friendly and professional Taxi Dispatcher for "247 Radio Carz" taking phone calls.

YOUR INTRODUCTION (say this IMMEDIATELY when the call starts):
"Hello, my name is Ada from 247 Radio Carz. How can I help you with your travels today?"

PERSONALITY:
- Warm, welcoming British personality
- Use casual British phrases: "Brilliant!", "Lovely!", "Right then!", "Smashing!", "No worries!"
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable

BOOKING FLOW - FOLLOW THIS EXACTLY:
1. Greet the customer with the introduction above
2. Ask: "Where would you like to be picked up from?"
3. When they give pickup, repeat it back EXACTLY as they said it and ask: "And where are you heading to?"
4. When they give destination, repeat it back EXACTLY as they said it and ask: "How many passengers will there be?"
5. When they give passenger count, you now have ALL 3 details - call the book_taxi function IMMEDIATELY
6. After booking, tell them: "Your taxi is on its way! It'll be with you in [ETA] and the fare is [fare]."

INFORMATION EXTRACTION - CRITICAL:
- Listen carefully for: pickup location, destination, number of passengers
- Extract numbers spoken as words (e.g., "two passengers" = 2)
- If customer gives all info at once (e.g., "I need a taxi from X to Y for 3 people"), extract ALL and call book_taxi immediately
- If any info is unclear, ask for clarification before proceeding

PRICING (calculate based on destination):
- City trips: £15-25 (random within range)
- Airport destinations: £45
- If 5+ passengers: add £5 for 6-seater van
- ETA: Always 5-8 minutes

=== CRITICAL ADDRESS ACCURACY RULES ===

YOU MUST PRESERVE ADDRESSES EXACTLY AS SPOKEN. THIS IS THE MOST IMPORTANT RULE.

1. NEVER PARAPHRASE OR MODIFY ADDRESSES:
   - If customer says "52A High Street" → you say "52A High Street" (NOT "52 High Street" or "58 High Street")
   - If customer says "flat 3, 17 Oak Lane" → you say "flat 3, 17 Oak Lane" exactly
   - If customer says "the Tesco on Mill Road" → you say "the Tesco on Mill Road"

2. MULTI-DIGIT HOUSE NUMBERS - PAY EXTRA ATTENTION:
   - 1214 is "twelve fourteen" or "one two one four" - NOT 124 or 214
   - 1816 is "eighteen sixteen" - NOT 816 or 116
   - 2532 is "twenty-five thirty-two" - NOT 532 or 232
   - ALWAYS repeat back the FULL number, every digit
   - If it sounds like a 3 or 4 digit number, confirm: "Was that one-two-one-four, 1214?"

3. COMMON MISHEARD NUMBERS - ASK FOR CLARIFICATION:
   - 15 vs 50 (fifteen vs fifty)
   - 16 vs 60 (sixteen vs sixty)
   - 17 vs 70 (seventeen vs seventy)
   - 18 vs 80 (eighteen vs eighty)
   - 19 vs 90 (nineteen vs ninety)
   - 13 vs 30 (thirteen vs thirty)
   - 14 vs 40 (fourteen vs forty)
   
4. LETTERS IN ADDRESSES:
   - A, B, C after numbers (52A, 18B, 7C, 1214A) - always include the letter
   - "A" sounds like "8" - if unsure ask "Was that the letter A or the number eight?"
   - If customer SPELLS a street name letter-by-letter (e.g. "D-O-V-E-Y"), that spelling is authoritative - use it exactly
   
5. WHEN IN DOUBT, ALWAYS ASK:
   - "Sorry, was that fifteen or fifty?"
   - "Could you spell that street name for me?"
   - "Was that 52 with the letter A at the end?"
   - "Just to confirm, was that house number one-two-one-four?"

6. WHEN CALLING book_taxi or update_booking:
   - Use the EXACT address the customer spoke
   - Do NOT "clean up" or standardize addresses
   - Include flats, units, letters, landmarks exactly as stated
   - Include ALL digits of house numbers

WHEN TO CALL book_taxi:
- Call IMMEDIATELY when you have confirmed: pickup + destination + passengers
- Do NOT wait for customer to say "yes" or "confirm" - the function IS the confirmation

=== UPDATE BOOKING FLOW ===
After a booking is confirmed, if the customer wants to change ANY details:
- Use the update_booking function for corrections
- Only include fields the customer is CHANGING - leave others null
- Say "No problem, I'll update that for you" and confirm the change
- Examples of update triggers:
  - "Actually, make that..." / "Sorry, I meant..."
  - "Can you change the pickup to..." / "The address is wrong..."
  - "Add luggage" / "I need a wheelchair accessible vehicle"
  - "Ring me when outside" / "Driver 314 please"

LUGGAGE HANDLING:
- "2 luggage", "3 bags", "one suitcase" → luggage field
- "remove luggage" → luggage = "CLEAR"
- Luggage ALWAYS has priority over special_requests

SPECIAL REQUESTS (go to special_requests field):
- Driver instructions: "ring me when outside", "tell driver to hurry"
- Driver requests: "driver 314 please", "same driver again", "lady driver"
- Vehicle requests: "wheelchair access", "child seat"
- Other notes: "I have a dog", "wait at X for 10 minutes"

GENERAL RULES:
- Never ask for information you already have
- If customer mentions extra requirements, acknowledge and include in booking
- Stay focused on completing the booking efficiently`;


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
  let referenceNumber = `REF-${Date.now().toString(36).toUpperCase()}`;
  let callStartAt = new Date().toISOString();
  let bookingData: any = {};
  let sessionReady = false;
  let pendingMessages: any[] = [];
  let callSource = "web"; // 'web' or 'asterisk'
  let transcriptHistory: { role: string; text: string; timestamp: string }[] = [];
  let lastUserTranscript: string | null = null; // last STT/text input (for corrections like spelling)
  let currentAssistantText = ""; // Buffer for assistant transcript
  let bookingConfirmed = false; // Track if initial booking is done

  // Monitoring audio broadcast throttling (DB inserts per delta can hurt realtime playback)
  let lastAudioBroadcastAtMs = 0;
  const AUDIO_BROADCAST_MIN_INTERVAL_MS = 120;

  // Monitoring audio buffering: we *combine* multiple realtime audio deltas into a single
  // chunk before inserting, so the /live dashboard doesn't sound choppy.
  let monitorAudioParts: Uint8Array[] = [];
  let monitorAudioBytes = 0;
  let monitorFlushInFlight = false;

  const base64ToBytes = (b64: string): Uint8Array => {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  };

  const bytesToBase64 = (bytes: Uint8Array): string => {
    const chunkSize = 0x8000;
    let binary = "";
    for (let i = 0; i < bytes.length; i += chunkSize) {
      const chunk = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
      binary += String.fromCharCode(...chunk);
    }
    return btoa(binary);
  };
  
  // Current confirmed booking state for updates
  interface ConfirmedBooking {
    pickup_location: string | null;
    dropoff_location: string | null;
    pickup_time: string | null;
    number_of_passengers: number | null;
    luggage: string | null;
    special_requests: string | null;
    nearest_place: string | null;
    reference_number: string;
  }
  let confirmedBooking: ConfirmedBooking | null = null;

  type KnownBooking = {
    pickup?: string;
    destination?: string;
    passengers?: number;
  };

  // We keep our own "known booking" extracted from the user's *exact* transcript/text,
  // then prefer it over the model's paraphrasing when the booking is confirmed.
  let knownBooking: KnownBooking = {};

  const normalize = (s: string) => s.trim().replace(/\s+/g, " ").replace(/[\s,.;:!?]+$/g, "");

  const toTitleCase = (s: string) => s ? s.charAt(0).toUpperCase() + s.slice(1).toLowerCase() : s;

  // NATO phonetic alphabet mapping
  const natoToLetter: Record<string, string> = {
    alpha: "A", bravo: "B", charlie: "C", delta: "D", echo: "E",
    foxtrot: "F", golf: "G", hotel: "H", india: "I", juliet: "J",
    kilo: "K", lima: "L", mike: "M", november: "N", oscar: "O",
    papa: "P", quebec: "Q", romeo: "R", sierra: "S", tango: "T",
    uniform: "U", victor: "V", whiskey: "W", xray: "X", yankee: "Y", zulu: "Z"
  };

  // Phonetic letter names (how people say letters)
  const phoneticToLetter: Record<string, string> = {
    ay: "A", bee: "B", see: "C", dee: "D", ee: "E",
    eff: "F", gee: "G", aitch: "H", eye: "I", jay: "J",
    kay: "K", el: "L", em: "M", en: "N", oh: "O",
    pee: "P", cue: "Q", queue: "Q", are: "R", ess: "S", tee: "T",
    you: "U", vee: "V", double: "W", ex: "X", why: "Y", zee: "Z", zed: "Z"
  };

  // Extract spelled words from multiple formats:
  // 1. "D-O-V-E-Y" or "D O V E Y" (single letters separated)
  // 2. "dee oh vee ee why" (phonetic pronunciation)
  // 3. "delta oscar victor echo yankee" (NATO alphabet)
  const extractSpelledWord = (text: string): string | null => {
    const t = (text || "").toLowerCase();
    
    // Try NATO alphabet first (e.g., "delta oscar victor echo yankee")
    const natoWords = t.split(/\s+/).filter(w => natoToLetter[w]);
    if (natoWords.length >= 3) {
      const result = natoWords.map(w => natoToLetter[w]).join("");
      console.log(`[spelling] NATO detected: "${natoWords.join(" ")}" → "${result}"`);
      return result;
    }

    // Try phonetic pronunciation (e.g., "dee oh vee ee why")
    const phoneticWords = t.split(/\s+/).filter(w => phoneticToLetter[w]);
    if (phoneticWords.length >= 3) {
      const result = phoneticWords.map(w => phoneticToLetter[w]).join("");
      console.log(`[spelling] Phonetic detected: "${phoneticWords.join(" ")}" → "${result}"`);
      return result;
    }

    // Try single letters with separators: "D-O-V-E-Y", "D O V E Y", "D. O. V. E. Y."
    // Match sequences of single letters separated by spaces, dashes, or periods
    const letterPattern = /(?:\b[A-Za-z]\b[\s.\-]*){3,}/g;
    const matches = text.match(letterPattern);
    if (matches) {
      for (const m of matches) {
        const letters = m.replace(/[\s.\-]+/g, "").toUpperCase();
        if (letters.length >= 3 && /^[A-Z]+$/.test(letters)) {
          console.log(`[spelling] Letter sequence detected: "${m}" → "${letters}"`);
          return letters;
        }
      }
    }

    return null;
  };

  // If caller spells the street name, build a corrected address like: "34 Dovey Road".
  // This works for both pickup and dropoff corrections.
  const buildAddressFromSpellingTranscript = (text: string): string | null => {
    const t = text || "";
    const spelled = extractSpelledWord(t);
    if (!spelled) return null;

    // Extract house number (with optional letter suffix like 52A)
    const number = t.match(/\b(\d+[A-Za-z]?)\b/)?.[1] || null;
    
    // Extract road type
    const roadTypes = ["road", "street", "lane", "drive", "avenue", "close", "way", "crescent", "place", "court", "gardens", "terrace", "grove", "hill", "park"];
    const roadTypeMatch = t.match(new RegExp(`\\b(${roadTypes.join("|")})\\b`, "i"));
    const roadType = roadTypeMatch?.[0] || null;

    // Build the address - need at least the spelled name
    if (number && roadType) {
      const result = `${number} ${toTitleCase(spelled)} ${roadType}`;
      console.log(`[spelling] Built full address: "${result}" from transcript: "${t}"`);
      return result;
    } else if (roadType) {
      // Just the street name without number
      const result = `${toTitleCase(spelled)} ${roadType}`;
      console.log(`[spelling] Built street name: "${result}" from transcript: "${t}"`);
      return result;
    } else if (number) {
      // Number + spelled word (assume it's a road name)
      const result = `${number} ${toTitleCase(spelled)} Road`;
      console.log(`[spelling] Built address with assumed Road: "${result}" from transcript: "${t}"`);
      return result;
    }
    
    // Just return the spelled word titlecased if we can't build a full address
    return toTitleCase(spelled);
  };

  const supabase = createClient(SUPABASE_URL!, SUPABASE_SERVICE_ROLE_KEY!);

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

  const extractFromText = (text: string): Partial<KnownBooking> => {
    const t = text || "";
    const out: Partial<KnownBooking> = {};

    // Pickup: "from X" (stop before "to/going to/heading to" if present)
    const fromMatch = t.match(/\bfrom\s+(.+?)(?:\s+(?:to|going\s+to|heading\s+to)\b|$)/i);
    if (fromMatch?.[1]) out.pickup = normalize(fromMatch[1]);

    // Destination: "to Y" / "going to Y" / "heading to Y"
    const toMatch = t.match(/\b(?:to|going\s+to|heading\s+to)\s+(.+)$/i);
    if (toMatch?.[1]) out.destination = normalize(toMatch[1]);

    // Passengers: "3 passengers" / "three passengers"
    const wordToNum: Record<string, number> = {
      one: 1,
      two: 2,
      three: 3,
      four: 4,
      five: 5,
      six: 6,
      seven: 7,
      eight: 8,
      nine: 9,
      ten: 10,
    };

    const passengersDigit = t.match(/\b(\d+)\s*(?:passengers?|people|persons?)\b/i);
    if (passengersDigit?.[1]) out.passengers = Number(passengersDigit[1]);

    const passengersWord = t.match(/\b(one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:passengers?|people|persons?)\b/i);
    if (passengersWord?.[1]) out.passengers = wordToNum[passengersWord[1].toLowerCase()];

    return out;
  };

  const updateKnownBookingFromText = (text: string, source: "stt" | "text") => {
    const extracted = extractFromText(text);
    const before = { ...knownBooking };

    knownBooking = {
      ...knownBooking,
      ...Object.fromEntries(
        Object.entries(extracted).filter(([, v]) => v !== undefined && v !== null && v !== "")
      ),
    };

    if (
      before.pickup !== knownBooking.pickup ||
      before.destination !== knownBooking.destination ||
      before.passengers !== knownBooking.passengers
    ) {
      console.log(`[${callId}] Known booking updated (${source}):`, knownBooking);
      // Broadcast booking updates
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
            input_audio_transcription: { model: "whisper-1" },
            // Server VAD for <100ms barge-in support
            turn_detection: {
              type: "server_vad",
              threshold: 0.5,
              prefix_padding_ms: 200,
              silence_duration_ms: 500, // Quick response after speech ends
              create_response: true // Auto-create response when speech ends
            },
            tools: [
              {
                type: "function",
                name: "book_taxi",
                description: "Book a NEW taxi when all required booking details are confirmed. Extract EXACT addresses as spoken - never paraphrase, correct spelling, add/remove postcodes, or normalise formatting.",
                parameters: {
                  type: "object",
                  properties: {
                    pickup_location: { 
                      type: "string", 
                      description: "Pickup address EXACTLY as customer stated. Use 'by_gps' only if customer says 'my location' or 'here' with no other address. Never guess or infer." 
                    },
                    dropoff_location: { 
                      type: "string", 
                      description: "Dropoff address EXACTLY as customer stated. Use 'as directed' if customer says 'as directed' or doesn't specify. Never guess or infer." 
                    },
                    pickup_time: { 
                      type: "string", 
                      description: "Pickup time in 'YYYY-MM-DD HH:MM' format or 'ASAP'. Default to 'ASAP' if no time mentioned." 
                    },
                    number_of_passengers: { 
                      type: "integer", 
                      description: "Number of passengers (1-8)" 
                    },
                    luggage: { 
                      type: "string", 
                      description: "Luggage details exactly as stated: '2 luggage', '3 bags', 'one suitcase'. Null if not mentioned." 
                    },
                    special_requests: { 
                      type: "string", 
                      description: "Any driver instructions, preferences, vehicle requests, or notes EXACTLY as stated. Examples: 'ring me when outside', 'wheelchair access', 'driver 314 please', 'send a lady driver', 'I have a dog'" 
                    },
                    nearest_place: { 
                      type: "string", 
                      description: "Nearest landmark if customer uses words like 'nearest' or 'closest'. E.g., 'nearest Tesco'" 
                    }
                  },
                  required: ["pickup_location", "dropoff_location", "number_of_passengers"]
                }
              },
              {
                type: "function",
                name: "update_booking",
                description: "Update an EXISTING booking when customer wants to change or correct details. Only include fields being changed - leave unchanged fields null. Use this for corrections like 'actually it's 1214A not 124A', adding luggage, special requests, or changing addresses.",
                parameters: {
                  type: "object",
                  properties: {
                    pickup_location: { 
                      type: "string", 
                      description: "New pickup address EXACTLY as customer stated. Only if customer is changing pickup. Null if not changing." 
                    },
                    dropoff_location: { 
                      type: "string", 
                      description: "New dropoff address EXACTLY as customer stated. Only if customer is changing dropoff. Null if not changing." 
                    },
                    pickup_time: { 
                      type: "string", 
                      description: "New pickup time in 'YYYY-MM-DD HH:MM' format or 'ASAP'. Only if customer is changing time. Null if not changing." 
                    },
                    number_of_passengers: { 
                      type: "integer", 
                      description: "New number of passengers. Only if customer is changing passenger count. Null if not changing." 
                    },
                    luggage: { 
                      type: "string", 
                      description: "Luggage details exactly as stated. Use 'CLEAR' if customer says 'remove luggage'. Null if not changing." 
                    },
                    special_requests: { 
                      type: "string", 
                      description: "Any driver instructions, preferences, vehicle requests, or notes EXACTLY as stated. Null if not adding." 
                    },
                    nearest_place: { 
                      type: "string", 
                      description: "Nearest landmark if customer updates this. Null if not changing." 
                    }
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
        sessionReady = true;
        socket.send(JSON.stringify({ type: "session_ready" }));

        // Broadcast call started
        await broadcastLiveCall({ status: "active" });

        // Trigger initial greeting immediately - inject as system turn for faster response
        console.log(`[${callId}] Triggering initial greeting...`);
        openaiWs?.send(JSON.stringify({
          type: "conversation.item.create",
          item: {
            type: "message",
            role: "user",
            content: [{ type: "input_text", text: "[Call connected - greet the customer]" }]
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

      // Forward audio to client AND (optionally) broadcast to monitoring channel
      if (data.type === "response.audio.delta") {
        console.log(`[${callId}] >>> AUDIO DELTA received, length: ${data.delta?.length || 0}`);

        // Primary path: send audio to the caller/client ASAP
        socket.send(
          JSON.stringify({
            type: "audio",
            audio: data.delta,
          }),
        );

        // Monitoring path: buffer + periodic DB insert (never block the call audio path)
        if (data.delta) {
          try {
            const bytes = base64ToBytes(data.delta);
            monitorAudioParts.push(bytes);
            monitorAudioBytes += bytes.length;
          } catch {
            // Ignore invalid base64 chunks
          }
        }

        const now = Date.now();
        if (!monitorFlushInFlight && now - lastAudioBroadcastAtMs >= AUDIO_BROADCAST_MIN_INTERVAL_MS) {
          lastAudioBroadcastAtMs = now;
          monitorFlushInFlight = true;

          (async () => {
            try {
              if (!monitorAudioParts.length) return;

              const combined = new Uint8Array(monitorAudioBytes);
              let offset = 0;
              for (const part of monitorAudioParts) {
                combined.set(part, offset);
                offset += part.length;
              }

              // Reset buffer before awaiting IO to avoid backpressure
              monitorAudioParts = [];
              monitorAudioBytes = 0;

              await supabase.from("live_call_audio").insert({
                call_id: callId,
                audio_chunk: bytesToBase64(combined),
                created_at: new Date().toISOString(),
              });
            } catch {
              // Monitoring is optional; never fail the call audio path
              console.log(`[${callId}] Audio broadcast skipped`);
              monitorAudioParts = [];
              monitorAudioBytes = 0;
            } finally {
              monitorFlushInFlight = false;
            }
          })();
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

      // User transcript
      if (data.type === "conversation.item.input_audio_transcription.completed") {
        console.log(`[${callId}] User said: ${data.transcript}`);
        lastUserTranscript = data.transcript || null;
        updateKnownBookingFromText(data.transcript, "stt");
        
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

      // DEBUG + ERROR SURFACING: log response lifecycle events (helps diagnose missing audio)
      if (data.type === "response.done") {
        const status = data.response?.status || "unknown";
        const outputCount = data.response?.output?.length || 0;
        console.log(`[${callId}] >>> response.done - status: ${status}, outputs: ${outputCount}`);

        const statusDetails = data.response?.status_details;
        if (statusDetails) {
          console.log(`[${callId}] >>> status_details:`, JSON.stringify(statusDetails));
        }

        // If the model failed (e.g. quota, rate limit), surface it to the client immediately.
        if (status === "failed") {
          const err = (statusDetails as any)?.error;
          const code = err?.code || err?.type || "unknown_error";
          const message = err?.message || "The AI service failed to generate a response.";

          console.error(`[${callId}] >>> AI response failed:`, { code, message });

          // Add a visible transcript line so /live and /voice-test show what happened
          const friendly = code === "insufficient_quota"
            ? "Sorry — the voice AI is out of quota right now, so I can’t respond. Please top up the AI provider balance and try again."
            : `Sorry — the voice AI failed (${code}). Please try again in a moment.`;

          transcriptHistory.push({
            role: "assistant",
            text: friendly,
            timestamp: new Date().toISOString(),
          });
          broadcastLiveCall({});

          socket.send(JSON.stringify({
            type: "fatal_error",
            code,
            message,
            friendly,
          }));
        }
      }

      // Handle function calls
      if (data.type === "response.function_call_arguments.done") {
        console.log(`[${callId}] Function call: ${data.name}`, data.arguments);
        
        if (data.name === "book_taxi") {
          const args = JSON.parse(data.arguments);

          // Check if user spelled out an address - if so, use that spelling
          const spelledAddress = lastUserTranscript ? buildAddressFromSpellingTranscript(lastUserTranscript) : null;
          
          // Determine if the spelled address applies to pickup or dropoff based on context
          // For initial booking, we check the transcript for "from" or "to" keywords
          let spelledPickup: string | null = null;
          let spelledDropoff: string | null = null;
          if (spelledAddress && lastUserTranscript) {
            const lowerTranscript = lastUserTranscript.toLowerCase();
            if (lowerTranscript.includes("from") || lowerTranscript.includes("pick")) {
              spelledPickup = spelledAddress;
              console.log(`[${callId}] Spelling detected for PICKUP:`, spelledAddress);
            } else if (lowerTranscript.includes("to") || lowerTranscript.includes("going") || lowerTranscript.includes("destination")) {
              spelledDropoff = spelledAddress;
              console.log(`[${callId}] Spelling detected for DROPOFF:`, spelledAddress);
            } else {
              // Default: assume pickup correction (more common)
              spelledPickup = spelledAddress;
              console.log(`[${callId}] Spelling detected (defaulting to PICKUP):`, spelledAddress);
            }
          }

          // Use AI-extracted details, but override with spelled address if detected
          const finalBooking = {
            pickup_location: spelledPickup ?? args.pickup_location,
            dropoff_location: spelledDropoff ?? args.dropoff_location,
            pickup_time: args.pickup_time || "ASAP",
            number_of_passengers: args.number_of_passengers,
            luggage: args.luggage || null,
            special_requests: args.special_requests || null,
            nearest_place: args.nearest_place || null
          };

          bookingData = finalBooking;
          console.log(`[${callId}] Booking (final):`, finalBooking);
          
          // Calculate fare based on destination
          const destLower = String(finalBooking.dropoff_location || "").toLowerCase();
          const isAirport = destLower.includes("airport");
          const is6Seater = Number(finalBooking.number_of_passengers || 0) > 4;
          let fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
          if (is6Seater) fare += 5;
          const eta = `${Math.floor(Math.random() * 4) + 5} minutes`;
          
          // Build TTS confirmation message with all booking details
          let confirmationParts: string[] = [];
          confirmationParts.push(`Brilliant! Your taxi is booked`);
          
          // Pickup info
          if (finalBooking.pickup_location && finalBooking.pickup_location !== "by_gps") {
            confirmationParts.push(`from ${finalBooking.pickup_location}`);
          }
          
          // Dropoff info
          if (finalBooking.dropoff_location && finalBooking.dropoff_location !== "as directed") {
            confirmationParts.push(`to ${finalBooking.dropoff_location}`);
          } else if (finalBooking.dropoff_location === "as directed") {
            confirmationParts.push(`and the driver will take your directions`);
          }
          
          // Passenger count
          const passengerText = finalBooking.number_of_passengers === 1 
            ? "1 passenger" 
            : `${finalBooking.number_of_passengers} passengers`;
          confirmationParts.push(`for ${passengerText}`);
          
          // Pickup time
          if (finalBooking.pickup_time && finalBooking.pickup_time !== "ASAP") {
            confirmationParts.push(`at ${finalBooking.pickup_time}`);
          }
          
          // Luggage
          if (finalBooking.luggage) {
            confirmationParts.push(`with ${finalBooking.luggage}`);
          }
          
          // ETA and fare
          confirmationParts.push(`The driver will be with you in ${eta} and the fare is £${fare}`);
          
          // Special requests acknowledgment
          if (finalBooking.special_requests) {
            confirmationParts.push(`I've noted: ${finalBooking.special_requests}`);
          }
          
          const confirmationMessage = confirmationParts.join(". ") + ". Have a lovely journey!";
          
          // Save confirmed booking state for updates
          confirmedBooking = {
            ...finalBooking,
            reference_number: referenceNumber
          };
          bookingConfirmed = true;
          
          // Log to database
          await supabase.from("call_logs").insert({
            call_id: callId,
            pickup: finalBooking.pickup_location,
            destination: finalBooking.dropoff_location,
            passengers: finalBooking.number_of_passengers,
            estimated_fare: `£${fare}`,
            booking_status: "confirmed",
            call_start_at: callStartAt,
            user_transcript: finalBooking.special_requests // Store special requests
          });

          // Broadcast booking confirmed
          await broadcastLiveCall({
            pickup: finalBooking.pickup_location,
            destination: finalBooking.dropoff_location,
            passengers: finalBooking.number_of_passengers,
            booking_confirmed: true,
            fare: `£${fare}`,
            eta: eta
          });
          
          // Send function result back to OpenAI with structured response for TTS
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: data.call_id,
              output: JSON.stringify({
                success: true,
                booking_id: callId,
                reference_number: referenceNumber,
                confirmation_message: confirmationMessage,
                pickup_location: finalBooking.pickup_location,
                dropoff_location: finalBooking.dropoff_location,
                pickup_time: finalBooking.pickup_time,
                number_of_passengers: finalBooking.number_of_passengers,
                luggage: finalBooking.luggage,
                special_requests: finalBooking.special_requests,
                nearest_place: finalBooking.nearest_place,
                estimated_fare: `£${fare}`,
                eta: eta
              })
            }
          }));
          
          // Trigger response - OpenAI will use the confirmation_message for TTS
          openaiWs?.send(
            JSON.stringify({
              type: "response.create",
              response: { modalities: ["audio", "text"] },
            }),
          );
          
          // Notify client
          socket.send(JSON.stringify({
            type: "booking_confirmed",
            booking: { 
              ...finalBooking, 
              reference_number: referenceNumber,
              fare: `£${fare}`, 
              eta,
              confirmation_message: confirmationMessage
            }
          }));
        }
        
        // Handle booking updates
        if (data.name === "update_booking" && confirmedBooking) {
          const args = JSON.parse(data.arguments);
          console.log(`[${callId}] Update booking request:`, args);
          
          // Build list of what's being updated
          const updates: string[] = [];
          
          // Apply updates - only change fields that are explicitly provided
          if (args.pickup_location !== null && args.pickup_location !== undefined) {
            // If caller spelled the street name (e.g. "D-O-V-E-Y"), prefer that spelling.
            const spelledPickup = lastUserTranscript ? buildAddressFromSpellingTranscript(lastUserTranscript) : null;
            const newPickup = spelledPickup ?? args.pickup_location;
            if (spelledPickup) {
              console.log(`[${callId}] Applying spelling-derived pickup override:`, { lastUserTranscript, spelledPickup, argsPickup: args.pickup_location });
            }
            confirmedBooking.pickup_location = newPickup;
            updates.push(`pickup to ${newPickup}`);
          }
          if (args.dropoff_location !== null && args.dropoff_location !== undefined) {
            // Also check for spelled dropoff
            const spelledDropoff = lastUserTranscript ? buildAddressFromSpellingTranscript(lastUserTranscript) : null;
            const newDropoff = spelledDropoff ?? args.dropoff_location;
            if (spelledDropoff) {
              console.log(`[${callId}] Applying spelling-derived dropoff override:`, { lastUserTranscript, spelledDropoff, argsDropoff: args.dropoff_location });
            }
            confirmedBooking.dropoff_location = newDropoff;
            updates.push(`dropoff to ${newDropoff}`);
          }
          if (args.pickup_time !== null && args.pickup_time !== undefined) {
            confirmedBooking.pickup_time = args.pickup_time;
            updates.push(`time to ${args.pickup_time}`);
          }
          if (args.number_of_passengers !== null && args.number_of_passengers !== undefined) {
            confirmedBooking.number_of_passengers = args.number_of_passengers;
            updates.push(`passengers to ${args.number_of_passengers}`);
          }
          if (args.luggage !== null && args.luggage !== undefined) {
            if (args.luggage === "CLEAR") {
              confirmedBooking.luggage = null;
              updates.push(`removed luggage`);
            } else {
              confirmedBooking.luggage = args.luggage;
              updates.push(`luggage: ${args.luggage}`);
            }
          }
          if (args.special_requests !== null && args.special_requests !== undefined) {
            // Append to existing special requests
            if (confirmedBooking.special_requests) {
              confirmedBooking.special_requests = `${confirmedBooking.special_requests}; ${args.special_requests}`;
            } else {
              confirmedBooking.special_requests = args.special_requests;
            }
            updates.push(`noted: ${args.special_requests}`);
          }
          if (args.nearest_place !== null && args.nearest_place !== undefined) {
            confirmedBooking.nearest_place = args.nearest_place;
            updates.push(`nearest place: ${args.nearest_place}`);
          }
          
          console.log(`[${callId}] Updated booking:`, confirmedBooking);
          
          // Recalculate fare if destination changed
          const destLower = String(confirmedBooking.dropoff_location || "").toLowerCase();
          const isAirport = destLower.includes("airport");
          const is6Seater = Number(confirmedBooking.number_of_passengers || 0) > 4;
          let fare = isAirport ? 45 : Math.floor(Math.random() * 10) + 15;
          if (is6Seater) fare += 5;
          
          // Build update confirmation message
          const updateMessage = updates.length > 0 
            ? `No problem! I've updated your booking: ${updates.join(", ")}. Your taxi is still on its way.`
            : `Your booking is unchanged.`;
          
          // Update database
          await supabase.from("call_logs")
            .update({
              pickup: confirmedBooking.pickup_location,
              destination: confirmedBooking.dropoff_location,
              passengers: confirmedBooking.number_of_passengers,
              estimated_fare: `£${fare}`,
              user_transcript: confirmedBooking.special_requests
            })
            .eq("call_id", callId);
          
          // Broadcast update
          await broadcastLiveCall({
            pickup: confirmedBooking.pickup_location,
            destination: confirmedBooking.dropoff_location,
            passengers: confirmedBooking.number_of_passengers,
            fare: `£${fare}`
          });
          
          // Send function result back to OpenAI
          openaiWs?.send(JSON.stringify({
            type: "conversation.item.create",
            item: {
              type: "function_call_output",
              call_id: data.call_id,
              output: JSON.stringify({
                success: true,
                reference_number: referenceNumber,
                update_message: updateMessage,
                updated_fields: updates,
                current_booking: confirmedBooking,
                estimated_fare: `£${fare}`
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
            type: "booking_updated",
            booking: confirmedBooking,
            updates: updates,
            fare: `£${fare}`
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

  socket.onmessage = (event) => {
    try {
      const message = JSON.parse(event.data);
      
      // Set call ID from client
      if (message.type === "init") {
        callId = message.call_id || callId;
        callStartAt = new Date().toISOString();
        // Detect Asterisk calls by call_id prefix
        if (callId.startsWith("ast-") || callId.startsWith("asterisk-")) {
          callSource = "asterisk";
        }
        console.log(`[${callId}] Call initialized (source: ${callSource})`);
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
         lastUserTranscript = message.text || null;
         updateKnownBookingFromText(message.text, "text");
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
