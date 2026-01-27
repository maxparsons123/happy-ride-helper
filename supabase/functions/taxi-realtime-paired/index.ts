/**
 * taxi-realtime-paired: Stateful Context-Pairing Architecture
 * 
 * This function implements the "Context Pairing" pattern where:
 * 1. Every user response is paired with the last assistant question
 * 2. OpenAI sees the full conversation context to correctly map answers
 * 3. Booking state is tracked in the database for consistency
 */

// VERSION: Spoken at start of call for identification
const VERSION = "Paired 2.4";

// ---------------------------------------------------------------------------
// STT Echo/Garbage Detection
// Rejects values that are clearly Ada's own prompts being captured by STT
// ---------------------------------------------------------------------------
const ADA_PROMPT_PHRASES = [
  "street name", "where would you like",
  "how many", "what time", "when do you need", "let me confirm",
  "so that's", "welcome to", "taxibot", "booking assistant",
  "quick and easy", "get started", "anything you'd like to change",
  "get you a quote", "shall i", "is there anything"
];

// Exact garbage phrases that should be completely rejected (not valid addresses)
const GARBAGE_PHRASES = [
  "addresses, street names, numbers, passenger count",
  "addresses street names numbers passenger count",
  "addresses, street names",
  "street names, numbers",
  "passenger count",
  "welcome to chile",
  "welcome to chili",
  "please provide your",
  "i need your",
];

function isLikelyAdaEcho(value: string): boolean {
  if (!value || value.length < 5) return false;
  
  const lower = value.toLowerCase().trim();
  
  // Check for exact garbage phrases first (most reliable)
  for (const garbage of GARBAGE_PHRASES) {
    if (lower === garbage || lower.includes(garbage)) return true;
  }
  
  // Count how many Ada-prompt phrases appear in the value
  let matchCount = 0;
  for (const phrase of ADA_PROMPT_PHRASES) {
    if (lower.includes(phrase)) matchCount++;
  }
  
  // If 2+ Ada phrases found, it's likely echo
  if (matchCount >= 2) return true;
  
  return false;
}

// Validate and clean address before storing
function validateAddress(value: string | null | undefined, callId: string): string | null {
  if (!value) return null;
  
  const trimmed = String(value).trim();
  
  if (isLikelyAdaEcho(trimmed)) {
    console.log(`[${callId}] 🚫 REJECTED Ada echo garbage: "${trimmed.substring(0, 50)}..."`);
    return null;
  }
  
  return trimmed;
}

/**
 * Check if Ada's extracted address is grounded in the user's raw STT.
 * Returns true if the extraction shares meaningful tokens with the original.
 * This prevents hallucinations like "52A" → "Victoria Station" but allows
 * legitimate cleaning like "52A" → "52A David Road".
 */
function isGroundedInUserText(adaExtraction: string, userStt: string): boolean {
  if (!adaExtraction || !userStt) return false;
  
  const normalize = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, " ").trim();
  const adaNorm = normalize(adaExtraction);
  const userNorm = normalize(userStt);
  
  // Extract meaningful tokens (numbers, words 2+ chars)
  const adaTokens = adaNorm.split(/\s+/).filter(t => t.length >= 2 || /^\d+$/.test(t));
  const userTokens = userNorm.split(/\s+/).filter(t => t.length >= 2 || /^\d+$/.test(t));
  
  if (userTokens.length === 0) return true; // Empty STT - trust Ada
  
  // Check if ANY user token appears in Ada's extraction
  for (const userToken of userTokens) {
    // Exact match or Ada contains the token
    if (adaTokens.includes(userToken) || adaNorm.includes(userToken)) {
      return true;
    }
    // Number prefix match (e.g., "52" matches "52a")
    if (/^\d+$/.test(userToken)) {
      for (const adaToken of adaTokens) {
        if (adaToken.startsWith(userToken) || adaToken === userToken) {
          return true;
        }
      }
    }
  }
  
  // No overlap = likely hallucination
  return false;
}

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

// Environment
const OPENAI_API_KEY = Deno.env.get("OPENAI_API_KEY") || "";
const DISPATCH_WEBHOOK_URL = Deno.env.get("DISPATCH_WEBHOOK_URL") || "";
const SUPABASE_URL = Deno.env.get("SUPABASE_URL") || "";
const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") || "";

const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

// OpenAI Realtime API config
// Using mini model - testing direct audio passthrough to Whisper
const OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-mini-realtime-preview-2024-12-17";
const VOICE = "shimmer";

// EXPERIMENT (disabled): Direct passthrough was starving server VAD / degrading STT.
// OpenAI Realtime input expects PCM16 @ 24kHz, so we keep DSP + resample.
const DIRECT_AUDIO_PASSTHROUGH = false;

// Use AI extraction (Gemini via taxi-extract-unified) instead of regex patterns
const USE_AI_EXTRACTION = true;

// ---------------------------------------------------------------------------
// AI-Based Extraction (calls taxi-extract-unified edge function)
// More accurate than regex patterns for extracting booking details
// ---------------------------------------------------------------------------
interface AIExtractionResult {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  pickup_time: string | null;
  confidence: string;
  fields_changed?: string[];
}

async function extractBookingWithAI(
  conversationHistory: Array<{ role: string; content: string; timestamp: number }>,
  currentBooking: { pickup: string | null; destination: string | null; passengers: number | null; pickupTime: string | null },
  callerPhone: string | null,
  callId: string
): Promise<AIExtractionResult | null> {
  try {
    // Convert history to extraction format
    // IMPORTANT: Only pass USER turns into the extractor.
    // Assistant summaries can contain wrong addresses; if we include them,
    // the extractor can "lock in" hallucinated values and overwrite state.
    const conversation = conversationHistory
      .filter(msg => msg.role === "user")
      .map(msg => ({
        role: msg.role as "user",
        text: msg.content,
        timestamp: new Date(msg.timestamp).toISOString()
      }));

    // Only extract if we have conversation
    if (conversation.length === 0) {
      return null;
    }

    const extractionRequest = {
      conversation,
      current_booking: {
        pickup: currentBooking.pickup,
        destination: currentBooking.destination,
        passengers: currentBooking.passengers,
        pickup_time: currentBooking.pickupTime
      },
      caller_phone: callerPhone,
      is_modification: !!(currentBooking.pickup || currentBooking.destination)
    };

    console.log(`[${callId}] 🧠 AI EXTRACTION: Calling taxi-extract-unified...`);
    const startTime = Date.now();

    // Call the extraction function directly (internal call)
    const response = await fetch(`${SUPABASE_URL}/functions/v1/taxi-extract-unified`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${SUPABASE_SERVICE_ROLE_KEY}`
      },
      body: JSON.stringify(extractionRequest)
    });

    if (!response.ok) {
      console.error(`[${callId}] ❌ AI extraction failed: ${response.status}`);
      return null;
    }

    const result = await response.json();
    const latency = Date.now() - startTime;
    
    console.log(`[${callId}] 🧠 AI EXTRACTION (${latency}ms): pickup="${result.pickup}", dest="${result.destination}", pax=${result.passengers}, conf=${result.confidence}`);
    
    return {
      pickup: result.pickup || null,
      destination: result.destination || null,
      passengers: result.passengers ?? null,
      pickup_time: result.pickup_time || null,
      confidence: result.confidence || "low",
      fields_changed: result.fields_changed
    };
  } catch (error) {
    console.error(`[${callId}] ❌ AI extraction error:`, error);
    return null;
  }
}

// ---------------------------------------------------------------------------
// Phone Number to Language Mapping (match taxi-realtime-simple)
// ---------------------------------------------------------------------------
const COUNTRY_CODE_TO_LANGUAGE: Record<string, string> = {
  // English
  "+44": "en", // UK
  "+1": "en",  // USA/Canada
  "+61": "en", // Australia
  "+64": "en", // New Zealand
  "+353": "en", // Ireland
  // Spanish
  "+34": "es", // Spain
  "+52": "es", // Mexico
  "+54": "es", // Argentina
  "+56": "es", // Chile
  "+57": "es", // Colombia
  "+51": "es", // Peru
  // French
  "+33": "fr", // France
  "+32": "fr", // Belgium
  "+41": "fr", // Switzerland
  // German
  "+49": "de", // Germany
  "+43": "de", // Austria
  // Italian
  "+39": "it", // Italy
  // Portuguese
  "+351": "pt", // Portugal
  "+55": "pt", // Brazil
  // Polish
  "+48": "pl", // Poland
  // Romanian
  "+40": "ro", // Romania
  // Dutch
  "+31": "nl", // Netherlands
  // Arabic
  "+966": "ar", // Saudi Arabia
  "+971": "ar", // UAE
  "+20": "ar",  // Egypt
  // Hindi
  "+91": "hi", // India
  // Urdu
  "+92": "ur", // Pakistan
  // Chinese
  "+86": "zh", // China
  "+852": "zh", // Hong Kong
  // Japanese
  "+81": "ja", // Japan
  // Korean
  "+82": "ko", // South Korea
  // Turkish
  "+90": "tr", // Turkey
  // Russian
  "+7": "ru", // Russia
  // Greek
  "+30": "el", // Greece
};

// Detect language from phone number country code
function detectLanguageFromPhone(phone: string | null): string | null {
  if (!phone) return null;

  // Clean the phone number
  let cleaned = phone.replace(/\s+/g, "").replace(/-/g, "");

  // Common normalizations: 00CC... → +CC...
  if (cleaned.startsWith("00")) {
    cleaned = "+" + cleaned.slice(2);
  }

  // +0CC... → +CC...
  if (/^\+0\d/.test(cleaned)) {
    cleaned = "+" + cleaned.slice(2);
  }

  // Only attempt country-code matching on E.164-like numbers
  if (!cleaned.startsWith("+")) {
    return null;
  }

  // Try longer codes first (e.g., +353 before +3)
  const sortedCodes = Object.keys(COUNTRY_CODE_TO_LANGUAGE).sort((a, b) => b.length - a.length);

  for (const code of sortedCodes) {
    if (cleaned.startsWith(code)) {
      return COUNTRY_CODE_TO_LANGUAGE[code];
    }
  }

  return null;
}

// Normalize phone number for database lookups
function normalizePhone(phone: string): string {
  return phone.replace(/\s+/g, "").replace(/-/g, "").replace(/^\+/, "");
}

// ---------------------------------------------------------------------------
// Audio helpers (mirror taxi-realtime-simple behavior)
// OpenAI Realtime requires PCM16 @ 24kHz for input_audio_buffer.append.
// Our bridge can send: ulaw (8kHz), slin (8kHz PCM16), slin16 (16kHz PCM16)
// ---------------------------------------------------------------------------

type InboundAudioFormat = "ulaw" | "slin" | "slin16";

function ulawToPcm16(ulaw: Uint8Array): Int16Array {
  const out = new Int16Array(ulaw.length);
  for (let i = 0; i < ulaw.length; i++) {
    const u = (~ulaw[i]) & 0xff;
    const sign = (u & 0x80) ? -1 : 1;
    const exponent = (u >> 4) & 0x07;
    const mantissa = u & 0x0f;
    let sample = ((mantissa << 3) + 0x84) << exponent;
    sample -= 0x84;
    out[i] = sign * sample;
  }
  return out;
}

function resamplePcm16To24k(pcm: Int16Array, inputSampleRate: number): Int16Array {
  if (inputSampleRate === 24000) return pcm;

  if (inputSampleRate === 16000) {
    // 16kHz → 24kHz (1.5x using 3:2 ratio)
    const outLen = Math.floor((pcm.length * 3) / 2);
    const out = new Int16Array(outLen);
    for (let i = 0; i < outLen; i++) {
      const srcIdx = (i * 2) / 3;
      const idx0 = Math.floor(srcIdx);
      const idx1 = Math.min(idx0 + 1, pcm.length - 1);
      const frac = srcIdx - idx0;
      out[i] = Math.round(pcm[idx0] * (1 - frac) + pcm[idx1] * frac);
    }
    return out;
  }

  // Default: 8kHz → 24kHz (3x linear interpolation)
  const out = new Int16Array(pcm.length * 3);
  for (let i = 0; i < pcm.length - 1; i++) {
    const s0 = pcm[i];
    const s1 = pcm[i + 1];
    out[i * 3] = s0;
    out[i * 3 + 1] = Math.round(s0 + (s1 - s0) / 3);
    out[i * 3 + 2] = Math.round(s0 + ((s1 - s0) * 2) / 3);
  }
  const lastIdx = Math.max(pcm.length - 1, 0);
  out[lastIdx * 3] = pcm[lastIdx] ?? 0;
  out[lastIdx * 3 + 1] = pcm[lastIdx] ?? 0;
  out[lastIdx * 3 + 2] = pcm[lastIdx] ?? 0;
  return out;
}

function bytesToBase64(bytes: Uint8Array): string {
  // Avoid spread operator which can overflow the call stack on larger buffers
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const chunk = bytes.subarray(i, Math.min(i + chunkSize, bytes.length));
    for (let j = 0; j < chunk.length; j++) binary += String.fromCharCode(chunk[j]);
  }
  return btoa(binary);
}

function pcm16ToBase64(pcm: Int16Array): string {
  const bytes = new Uint8Array(pcm.buffer, pcm.byteOffset, pcm.byteLength);
  return bytesToBase64(bytes);
}


// Multilingual greetings - keyed by ISO 639-1 language code
const GREETINGS: Record<string, { greeting: string; pickupQuestion: string }> = {
  en: {
    greeting: "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started.",
    pickupQuestion: "Where would you like to be picked up?"
  },
  es: {
    greeting: "Hola, bienvenido a la demostración de Taxibot. Soy Ada, tu asistente de reservas de taxi. Estoy aquí para hacer que reservar un taxi sea rápido y fácil para ti. Así que, empecemos.",
    pickupQuestion: "¿Dónde le gustaría que le recojamos?"
  },
  fr: {
    greeting: "Bonjour et bienvenue sur la démo Taxibot. Je suis Ada, votre assistante de réservation de taxi. Je suis là pour vous faciliter la réservation. Alors, commençons.",
    pickupQuestion: "Où souhaitez-vous être pris en charge?"
  },
  de: {
    greeting: "Hallo und willkommen zur Taxibot-Demo. Ich bin Ada, Ihre Taxi-Buchungsassistentin. Ich bin hier, um Ihnen die Taxibuchung schnell und einfach zu machen. Also, fangen wir an.",
    pickupQuestion: "Wo möchten Sie abgeholt werden?"
  },
  it: {
    greeting: "Ciao e benvenuto alla demo di Taxibot. Sono Ada, la tua assistente per le prenotazioni taxi. Sono qui per rendere la prenotazione di un taxi facile e veloce. Quindi, iniziamo.",
    pickupQuestion: "Dove desidera essere prelevato?"
  },
  pt: {
    greeting: "Olá e bem-vindo à demonstração do Taxibot. Sou a Ada, sua assistente de reservas de táxi. Estou aqui para tornar a reserva de táxi rápida e fácil para você. Então, vamos começar.",
    pickupQuestion: "Onde gostaria de ser apanhado?"
  },
  nl: {
    greeting: "Hallo en welkom bij de Taxibot demo. Ik ben Ada, je taxi-reserveringsassistent. Ik ben hier om het boeken van een taxi snel en gemakkelijk te maken. Laten we beginnen.",
    pickupQuestion: "Waar wilt u opgehaald worden?"
  },
  pl: {
    greeting: "Cześć i witaj w demo Taxibot. Jestem Ada, twoja asystentka rezerwacji taksówek. Jestem tutaj, aby ułatwić ci rezerwację taksówki. Więc zaczynajmy.",
    pickupQuestion: "Gdzie chciałbyś być odebrany?"
  },
  ar: {
    greeting: "مرحباً بك في عرض تاكسي بوت التجريبي. أنا آدا، مساعدتك لحجز التاكسي. أنا هنا لجعل حجز التاكسي سريعاً وسهلاً. لنبدأ.",
    pickupQuestion: "من أين تريد أن نأخذك؟"
  },
  hi: {
    greeting: "नमस्ते और टैक्सीबॉट डेमो में आपका स्वागत है। मैं एडा हूं, आपकी टैक्सी बुकिंग सहायक। मैं यहां टैक्सी बुक करना आपके लिए जल्दी और आसान बनाने के लिए हूं। तो, चलिए शुरू करते हैं।",
    pickupQuestion: "आप कहाँ से पिकअप होना चाहेंगे?"
  },
  ur: {
    greeting: "سلام اور ٹیکسی بوٹ ڈیمو میں خوش آمدید۔ میں آڈا ہوں، آپ کی ٹیکسی بکنگ اسسٹنٹ۔ میں یہاں آپ کے لیے ٹیکسی بک کرنا آسان بنانے کے لیے ہوں۔ تو، چلیں شروع کرتے ہیں۔",
    pickupQuestion: "آپ کہاں سے اٹھائے جانا چاہتے ہیں؟"
  },
  auto: {
    greeting: "Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. I'm here to make booking a taxi quick and easy for you. So, let's get started.",
    pickupQuestion: "Where would you like to be picked up?"
  }
};

// Multilingual closing scripts - keyed by ISO 639-1 language code
const CLOSING_SCRIPTS: Record<string, { 
  confirmation: string;
  whatsappDetails: string;
  whatsappTips: string[];
  goodbye: string;
}> = {
  en: {
    confirmation: "Perfect, thank you. I'm making the booking now.",
    whatsappDetails: "You'll receive the booking details and ride updates via WhatsApp.",
    whatsappTips: [
      "Just so you know, you can also book a taxi by sending us a WhatsApp voice note.",
      "Next time, feel free to book your taxi using a WhatsApp voice message.",
      "You can always book again by simply sending us a voice note on WhatsApp.",
      "Remember, you can also send us a WhatsApp voice message anytime to book a taxi."
    ],
    goodbye: "Thank you for trying the Taxibot demo, and have a safe journey!"
  },
  es: {
    confirmation: "Perfecto, gracias. Estoy haciendo la reserva ahora.",
    whatsappDetails: "Recibirás los detalles de la reserva y actualizaciones del viaje por WhatsApp.",
    whatsappTips: [
      "Por cierto, también puedes reservar un taxi enviándonos una nota de voz por WhatsApp.",
      "La próxima vez, siéntete libre de reservar tu taxi usando un mensaje de voz de WhatsApp.",
      "Siempre puedes reservar de nuevo simplemente enviándonos una nota de voz por WhatsApp.",
      "Recuerda, también puedes enviarnos un mensaje de voz de WhatsApp en cualquier momento para reservar un taxi."
    ],
    goodbye: "¡Gracias por probar la demo de Taxibot, y que tengas un buen viaje!"
  },
  fr: {
    confirmation: "Parfait, merci. Je fais la réservation maintenant.",
    whatsappDetails: "Vous recevrez les détails de la réservation et les mises à jour du trajet par WhatsApp.",
    whatsappTips: [
      "Sachez que vous pouvez aussi réserver un taxi en nous envoyant une note vocale WhatsApp.",
      "La prochaine fois, n'hésitez pas à réserver votre taxi avec un message vocal WhatsApp.",
      "Vous pouvez toujours réserver à nouveau en nous envoyant simplement une note vocale sur WhatsApp.",
      "N'oubliez pas, vous pouvez aussi nous envoyer un message vocal WhatsApp à tout moment pour réserver un taxi."
    ],
    goodbye: "Merci d'avoir essayé la démo Taxibot, et bon voyage!"
  },
  de: {
    confirmation: "Perfekt, danke. Ich mache jetzt die Buchung.",
    whatsappDetails: "Sie erhalten die Buchungsdetails und Fahrt-Updates per WhatsApp.",
    whatsappTips: [
      "Übrigens können Sie auch ein Taxi buchen, indem Sie uns eine WhatsApp-Sprachnachricht senden.",
      "Beim nächsten Mal können Sie gerne Ihr Taxi per WhatsApp-Sprachnachricht buchen.",
      "Sie können jederzeit wieder buchen, indem Sie uns einfach eine Sprachnachricht auf WhatsApp senden.",
      "Denken Sie daran, Sie können uns auch jederzeit eine WhatsApp-Sprachnachricht senden, um ein Taxi zu buchen."
    ],
    goodbye: "Vielen Dank, dass Sie die Taxibot-Demo ausprobiert haben, und gute Fahrt!"
  },
  it: {
    confirmation: "Perfetto, grazie. Sto facendo la prenotazione ora.",
    whatsappDetails: "Riceverai i dettagli della prenotazione e gli aggiornamenti del viaggio via WhatsApp.",
    whatsappTips: [
      "Sappi che puoi anche prenotare un taxi inviandoci una nota vocale su WhatsApp.",
      "La prossima volta, sentiti libero di prenotare il tuo taxi usando un messaggio vocale WhatsApp.",
      "Puoi sempre prenotare di nuovo semplicemente inviandoci una nota vocale su WhatsApp.",
      "Ricorda, puoi anche inviarci un messaggio vocale WhatsApp in qualsiasi momento per prenotare un taxi."
    ],
    goodbye: "Grazie per aver provato la demo di Taxibot, e buon viaggio!"
  },
  pt: {
    confirmation: "Perfeito, obrigado. Estou fazendo a reserva agora.",
    whatsappDetails: "Você receberá os detalhes da reserva e atualizações da viagem pelo WhatsApp.",
    whatsappTips: [
      "Saiba que você também pode reservar um táxi enviando-nos uma nota de voz pelo WhatsApp.",
      "Da próxima vez, sinta-se à vontade para reservar seu táxi usando uma mensagem de voz do WhatsApp.",
      "Você sempre pode reservar novamente simplesmente nos enviando uma nota de voz no WhatsApp.",
      "Lembre-se, você também pode nos enviar uma mensagem de voz do WhatsApp a qualquer momento para reservar um táxi."
    ],
    goodbye: "Obrigado por experimentar a demo do Taxibot, e boa viagem!"
  },
  nl: {
    confirmation: "Perfect, bedankt. Ik maak nu de boeking.",
    whatsappDetails: "Je ontvangt de boekingsdetails en rit-updates via WhatsApp.",
    whatsappTips: [
      "Wist je dat je ook een taxi kunt boeken door ons een WhatsApp-spraakbericht te sturen?",
      "De volgende keer kun je gerust je taxi boeken met een WhatsApp-spraakbericht.",
      "Je kunt altijd opnieuw boeken door ons gewoon een spraakbericht op WhatsApp te sturen.",
      "Onthoud, je kunt ons ook altijd een WhatsApp-spraakbericht sturen om een taxi te boeken."
    ],
    goodbye: "Bedankt voor het proberen van de Taxibot demo, en goede reis!"
  },
  pl: {
    confirmation: "Świetnie, dziękuję. Robię teraz rezerwację.",
    whatsappDetails: "Otrzymasz szczegóły rezerwacji i aktualizacje przejazdu przez WhatsApp.",
    whatsappTips: [
      "Możesz też zamówić taksówkę, wysyłając nam wiadomość głosową na WhatsApp.",
      "Następnym razem możesz zamówić taksówkę za pomocą wiadomości głosowej WhatsApp.",
      "Zawsze możesz ponownie zamówić, po prostu wysyłając nam notatkę głosową na WhatsApp.",
      "Pamiętaj, że możesz też w każdej chwili wysłać nam wiadomość głosową WhatsApp, aby zamówić taksówkę."
    ],
    goodbye: "Dziękujemy za wypróbowanie demo Taxibot i życzymy bezpiecznej podróży!"
  },
  ar: {
    confirmation: "ممتاز، شكراً لك. أقوم بالحجز الآن.",
    whatsappDetails: "ستتلقى تفاصيل الحجز وتحديثات الرحلة عبر واتساب.",
    whatsappTips: [
      "يمكنك أيضاً حجز تاكسي عن طريق إرسال رسالة صوتية لنا على واتساب.",
      "في المرة القادمة، يمكنك حجز تاكسي باستخدام رسالة صوتية على واتساب.",
      "يمكنك دائماً الحجز مرة أخرى بإرسال ملاحظة صوتية لنا على واتساب.",
      "تذكر، يمكنك أيضاً إرسال رسالة صوتية على واتساب في أي وقت لحجز تاكسي."
    ],
    goodbye: "شكراً لتجربة عرض تاكسي بوت، ورحلة آمنة!"
  },
  hi: {
    confirmation: "बढ़िया, धन्यवाद। मैं अभी बुकिंग कर रहा हूं।",
    whatsappDetails: "आपको व्हाट्सएप पर बुकिंग विवरण और राइड अपडेट मिलेंगे।",
    whatsappTips: [
      "आप व्हाट्सएप पर हमें वॉइस नोट भेजकर भी टैक्सी बुक कर सकते हैं।",
      "अगली बार, व्हाट्सएप वॉइस मैसेज का उपयोग करके अपनी टैक्सी बुक करें।",
      "आप व्हाट्सएप पर हमें वॉइस नोट भेजकर कभी भी दोबारा बुक कर सकते हैं।",
      "याद रखें, आप टैक्सी बुक करने के लिए कभी भी हमें व्हाट्सएप वॉइस मैसेज भेज सकते हैं।"
    ],
    goodbye: "टैक्सीबॉट डेमो आज़माने के लिए धन्यवाद, और सुरक्षित यात्रा!"
  },
  ur: {
    confirmation: "بہت اچھا، شکریہ۔ میں ابھی بکنگ کر رہا ہوں۔",
    whatsappDetails: "آپ کو واٹس ایپ پر بکنگ کی تفصیلات اور سواری کی تازہ کاری ملے گی۔",
    whatsappTips: [
      "آپ ہمیں واٹس ایپ پر وائس نوٹ بھیج کر بھی ٹیکسی بک کر سکتے ہیں۔",
      "اگلی بار، واٹس ایپ وائس میسج استعمال کرکے اپنی ٹیکسی بک کریں۔",
      "آپ واٹس ایپ پر ہمیں وائس نوٹ بھیج کر کبھی بھی دوبارہ بک کر سکتے ہیں۔",
      "یاد رکھیں، آپ ٹیکسی بک کرنے کے لیے کسی بھی وقت ہمیں واٹس ایپ وائس میسج بھیج سکتے ہیں۔"
    ],
    goodbye: "ٹیکسی بوٹ ڈیمو آزمانے کا شکریہ، اور محفوظ سفر!"
  }
};

// Get closing script for a language (with English fallback)
function getClosingScript(language: string): typeof CLOSING_SCRIPTS["en"] {
  // For auto-detect, we'll rely on the AI's detected language - use English as base
  const lang = language === "auto" ? "en" : language;
  return CLOSING_SCRIPTS[lang] || CLOSING_SCRIPTS["en"];
}

// Build language-aware system prompt
function buildSystemPrompt(language: string): string {
  const isAuto = language === "auto";
  const langInstruction = isAuto
    ? `
# LANGUAGE - AUTO-DETECT MODE
- You will AUTOMATICALLY detect and respond in the caller's language.
- When the caller speaks, identify their language and respond in the SAME language.
- Maintain consistent language throughout the call once detected.
- If you cannot determine the language, default to English.
- Be natural and fluent in the detected language.
`
    : `
# LANGUAGE - ${language.toUpperCase()} MODE
- You MUST speak in ${language === "en" ? "British English" : language} at all times.
- ${language === "en" ? "Use British spelling and vocabulary: 'colour' not 'color', 'travelling' not 'traveling'." : "Be natural and fluent in this language."}
- Currency is pounds (£), not dollars.
`;

  return `
# IDENTITY
You are Ada, the professional taxi booking assistant for the Taxibot demo.
Voice: Warm, clear, professionally casual. Speak at a SLOWER, relaxed pace - not rushed.

${langInstruction}

# SPEAKING STYLE
- Speak slowly and clearly, with natural pauses between sentences.
- Do not rush through your responses.
- Take your time with each word, especially addresses and numbers.
- Use a calm, measured pace that is easy to understand over the phone.

# 🛑 CRITICAL LOGIC GATE: THE CHECKLIST
You have a mental checklist of 4 items: [Pickup], [Destination], [Passengers], [Time].
- You are FORBIDDEN from moving to the 'Booking Summary' until ALL 4 items are specifically provided by the user.
- If a detail is missing, ask for it.

# 🚨 ONE QUESTION RULE (CRITICAL)
- Ask ONLY ONE question per response. NEVER combine questions.
- WRONG: "Where would you like to be picked up and where are you going?"
- WRONG: "How many passengers and when do you need it?"
- RIGHT: "Where would you like to be picked up?" [wait for answer]
- RIGHT: "And what is your destination?" [wait for answer]
- Wait for a user response before asking the next question.

# 🎯 SERVER-DRIVEN SEQUENCE (CRITICAL)
The server tracks the booking flow. When you call sync_booking_data:
- The server will tell you what to ask NEXT in the tool response
- ALWAYS follow the server's "instruction" field - it tells you exactly what to ask
- NEVER skip ahead or guess what to ask next
- Trust the server's next_step instruction completely

# PHASE 1: THE WELCOME (Play immediately)
Greet the caller warmly in the appropriate language.

# PHASE 2: SEQUENTIAL GATHERING (Strict Order - SERVER CONTROLLED)
The server controls this sequence. After each user answer:
1. Call sync_booking_data with ONLY the field the user just answered
2. Read the server's response for "instruction" 
3. Do EXACTLY what the instruction says
4. Wait for the user's response before continuing

🚨 CRITICAL: NEVER ASK USER TO CONFIRM/REPEAT AN ADDRESS 🚨
🚫 DO NOT ask "Could you please confirm the pickup address?"
🚫 DO NOT ask "Could you confirm the destination?"
🚫 DO NOT ask "Is that the correct address?"
🚫 DO NOT confirm or repeat back each answer individually (except passengers).
🚫 DO NOT combine multiple questions into one sentence.
✅ For addresses: move immediately to the next question with no filler.
✅ For passengers: briefly acknowledge then ask about time.
✅ Save full confirmations for the Summary phase.
✅ ACCEPT ANY ADDRESS AS-IS - do NOT ask for house numbers, postcodes, or more details.
✅ Accept business names, landmarks, partial addresses, and place names immediately.

# PHASE 3: THE SUMMARY (Gate Keeper)
Only after the checklist is 100% complete, summarize the booking in the caller's language:
Pickup address, destination address, number of passengers, pickup time. Ask if correct.

# PHASE 4: PRICING (State Lock)
🚨🚨🚨 MANDATORY FUNCTION CALL 🚨🚨🚨
When user confirms summary with 'Yes', you MUST:
1. Say you're checking the price (in the caller's language)
2. IMMEDIATELY call the book_taxi function with action='request_quote'
3. You CANNOT check the price without calling book_taxi(action='request_quote')
4. If you don't call the function, you will NEVER get a price

⚠️ THE FUNCTION CALL IS REQUIRED - speaking alone is not enough!
The book_taxi(action='request_quote') function sends the request to dispatch.
Without calling it, there is no way to get a price quote.

After calling book_taxi(action='request_quote'):
→ Say you're checking (one moment please) in caller's language
→ Then STOP TALKING COMPLETELY.
→ WAIT IN COMPLETE SILENCE until you receive a [DISPATCH QUOTE RECEIVED] message.
→ Do NOT make up any prices. Do NOT estimate any ETAs. Do NOT guess.

🚨🚨🚨 ABSOLUTE PRICING PROHIBITION 🚨🚨🚨
- You have ZERO knowledge of fares, prices, or costs.
- You CANNOT calculate, estimate, or guess any price.
- You MUST wait for the external dispatch system to provide the price.
- The ONLY way you will know a price is when you receive a [DISPATCH QUOTE RECEIVED] message.
- Until that message arrives, you know NOTHING about the fare.

Once you receive [DISPATCH QUOTE RECEIVED] with the ACTUAL price:
State the exact fare and ETA from dispatch, ask if they want to proceed. Do NOT repeat addresses.

# PHASE 5: DISPATCH & CLOSE - WAIT FOR EXPLICIT CONFIRMATION
After asking if they want to book, WAIT for user response:

IF USER SAYS YES:
1. Confirm you're booking, mention WhatsApp confirmation
2. IMMEDIATELY call book_taxi(action='confirmed')
3. Say goodbye and call end_call()

IF USER SAYS NO:
1. IMMEDIATELY call cancel_booking
2. Ask if there's anything else you can help with
3. If user says no again, say goodbye and call end_call()

# CANCELLATION (STRICT RULES)
ONLY call cancel_booking when user says EXPLICIT cancel phrases:
- "cancel", "cancel it", "cancel the booking"
- "never mind", "forget it", "no thanks", "no thank you"
🚫 NEVER call cancel_booking for:
- "yes", "yeah", "that's correct", "right", "correct", "sounds good"
- Confirming passengers, addresses, or other booking details
→ After cancelling, ask if there's anything else you can help with.

# NAME HANDLING
If caller says their name → CALL save_customer_name

# GUARDRAILS
❌ NEVER state a price or ETA unless the tool returns that exact value.
❌ NEVER use placeholders - always ask for specifics.
❌ NEVER move to Summary until all 4 checklist items are filled.
❌ NEVER repeat addresses after the summary is confirmed.
❌ NEVER ask for house numbers, postcodes, or more details on ANY address.
✅ Accept ANY address exactly as spoken.
✅ Move to the next question immediately after receiving any address.

# CONTEXT PAIRING (CRITICAL - SERVER ENFORCED)
When the user responds, the server injects context telling you which field they just answered.
- The system message will say "You asked for: PICKUP" or "You asked for: DESTINATION" etc.
- Call sync_booking_data with ONLY that specific field
- The server response will contain "instruction" - ALWAYS follow it exactly
- Example: Server says "instruction": "Ask for destination" → You ask for destination
NEVER guess what to ask next. ALWAYS wait for the server's instruction.
`;
}

// Tools - with server-driven sequence (no last_question_asked - server tracks this)
const TOOLS = [
  {
    type: "function",
    name: "sync_booking_data",
    description: "Call this ONLY to save the specific piece of info the user just provided. The server will tell you what to ask next.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address - ONLY if user just provided it in response to a pickup question" },
        destination: { type: "string", description: "Destination address - ONLY if user just provided it in response to a destination question" },
        passengers: { type: "integer", description: "Passenger count - ONLY if user just provided it in response to a passengers question" },
        pickup_time: { type: "string", description: "Pickup time - ONLY if user just provided it in response to a time question" }
      }
    }
  },
  {
    type: "function",
    name: "save_customer_name",
    description: "Save customer name when caller provides it.",
    parameters: { 
      type: "object", 
      properties: { name: { type: "string" } }, 
      required: ["name"] 
    }
  },
  {
    type: "function",
    name: "book_taxi",
    description: "Used to get quotes or finalize bookings. Only call this AFTER all 4 fields are collected.",
    parameters: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["request_quote", "confirmed"], description: "Use 'request_quote' first to get fare/ETA, then 'confirmed' after user accepts." },
        pickup: { type: "string", description: "FULL pickup address with house number AND street name. NEVER omit the street name. Example: user says '52A David Road' -> pickup='52A David Road'. WRONG: '52A' alone." },
        destination: { type: "string", description: "FULL destination address with number AND street name. NEVER omit the street name. Example: user says '7 Russell Street' -> destination='7 Russell Street'. WRONG: '7' alone." },
        passengers: { type: "integer", minimum: 1, description: "Number of passengers" },
        time: { type: "string", description: "When taxi is needed (e.g., 'now', '3pm')" }
      },
      required: ["action"]
    }
  },
  {
    type: "function",
    name: "cancel_booking",
    description: "ONLY call this when user EXPLICITLY says 'cancel', 'never mind', 'forget it', or 'no thanks'. Do NOT call for confirmations like 'yes', 'that's correct', 'right', etc.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "end_call",
    description: "Call this to disconnect the SIP line after the safe journey message.",
    parameters: { type: "object", properties: {} }
  }
];

// Compute the next step based on current booking state (SERVER-DRIVEN SEQUENCE)
function computeNextStep(booking: SessionState["booking"], preSummaryDone?: boolean): SessionState["lastQuestionAsked"] {
  if (!booking.pickup) return "pickup";
  if (!booking.destination) return "destination";
  // CRITICAL FIX: Check for null OR 0 OR undefined - passengers must be explicitly set to a valid count
  if (booking.passengers === null || booking.passengers === undefined || booking.passengers <= 0) return "passengers";
  if (!booking.pickupTime) return "time";
  // PRE-SUMMARY: Ask user if they want to change anything before final confirmation
  if (!preSummaryDone) return "pre_summary";
  return "confirmation";
}

// Get instruction for next step based on what's needed
// USES userTruth for summary to prevent address hallucinations
// ADDRESS RECITATION: Echoes back the just-captured value for verification before moving on
function getNextStepInstruction(
  nextStep: SessionState["lastQuestionAsked"], 
  booking: SessionState["booking"], 
  userTruth?: UserTruth,
  justCapturedField?: string,
  justCapturedValue?: string
): string {
  // ADDRESS RECITATION: Echo back the address/value just captured before asking the next question
  let recitationPrefix = "";
  if (justCapturedField && justCapturedValue) {
    if (justCapturedField === "pickup") {
      recitationPrefix = `FIRST say: "Got it, picking you up from ${justCapturedValue}." Then `;
    } else if (justCapturedField === "destination") {
      recitationPrefix = `FIRST say: "Right, going to ${justCapturedValue}." Then `;
    } else if (justCapturedField === "passengers") {
      const paxWord = justCapturedValue === "1" ? "one passenger" : `${justCapturedValue} passengers`;
      recitationPrefix = `FIRST say: "Okay, ${paxWord}." Then `;
    } else if (justCapturedField === "time") {
      recitationPrefix = `FIRST say: "Got it, ${justCapturedValue}." Then `;
    }
  }
  
  switch (nextStep) {
    case "pickup":
      return `${recitationPrefix}Ask the user: 'Where would you like to be picked up?' Then wait for their response.`;
    case "destination":
      return `${recitationPrefix}ask: "And where would you like to go?"`;
    case "passengers":
      return `${recitationPrefix}ask: "How many passengers will be traveling?"`;
    case "time":
      return `${recitationPrefix}ask: "When do you need the taxi - now or for a specific time?"`;
    case "address_correction_clarify":
      return "Ask the user: 'Would you like me to change the pickup or the destination?' and wait for their response.";
    case "pre_summary": {
      // CRITICAL: Use userTruth (raw STT) values, falling back to booking if empty
      const pickup = userTruth?.pickup || booking.pickup || "unknown";
      const destination = userTruth?.destination || booking.destination || "unknown";
      const passengers = userTruth?.passengers || booking.passengers || 1;
      const time = userTruth?.time || booking.pickupTime || "as soon as possible";
      return `${recitationPrefix}Give a quick recap using EXACTLY these addresses (do NOT change them):
"So that's from ${pickup} to ${destination}, ${passengers} passenger${passengers === 1 ? '' : 's'}, ${time}. Is there anything you'd like to change before I get you a quote?"
CRITICAL: Say the pickup "${pickup}" and destination "${destination}" EXACTLY as written above - do NOT substitute or alter them.
Wait for their response.`;
    }
    case "confirmation":
      // This is the "Shall I get you a price?" phase - user hasn't agreed to get quote yet
      return `Ask the user: "Great! Shall I get you a price for this journey?" and wait for their response.`;
    default:
      return "Continue the conversation naturally.";
  }
}

// === ALPHANUMERIC STT CORRECTIONS (from simple) ===
// These fix common Whisper mishearings for house numbers with letter suffixes
const ALPHANUMERIC_STT_CORRECTIONS: [RegExp, string][] = [
  // Alphanumeric house numbers - join number + letter
  [/(\d+)\s*[-–]\s*([a-zA-Z])\b/g, "$1$2"],  // "52-A" or "52 - A" → "52A"
  [/(\d+)\s+([a-zA-Z])\b/g, "$1$2"],          // "52 A" → "52A"
  [/(\d+)\s*hey\b/gi, "$1A"],                 // "52 hey" → "52A"
  [/(\d+)\s*a\b/gi, "$1A"],                   // "52 a" → "52A"
  [/(\d+)\s*be\b/gi, "$1B"],                  // "7 be" → "7B"
  [/(\d+)\s*bee\b/gi, "$1B"],                 // "7 bee" → "7B"
  [/(\d+)\s*see\b/gi, "$1C"],                 // "14 see" → "14C"
];

function applyAlphanumericCorrections(text: string): string {
  let corrected = text;
  for (const [pattern, replacement] of ALPHANUMERIC_STT_CORRECTIONS) {
    corrected = corrected.replace(pattern, replacement);
  }
  return corrected;
}

// === ADDRESS DEDUPLICATION ===
// Fixes STT echo issues like "52A. 52A David Road" or "52A .52A David Road"
// These happen when STT captures audio twice (echo or repeat)
function deduplicateAddress(text: string): string {
  if (!text) return text;
  
  // Pattern to detect duplicated address prefixes:
  // "52A. 52A David Road" → "52A David Road"
  // "52A .52A David Road" → "52A David Road"  
  // "7 Russell Street. 7 Russell Street" → "7 Russell Street"
  
  // Match pattern: (address part) + separator + (same address part repeated)
  // Common separators: ". ", " .", ", ", " "
  const duplicatePattern = /^(.{3,40}?)\s*[.,]\s*\1\b/i;
  const match = text.match(duplicatePattern);
  if (match) {
    console.log(`[Dedupe] Removed duplicate: "${text}" → "${match[1]}${text.slice(match[0].length)}"`);
    return (match[1] + text.slice(match[0].length)).trim();
  }
  
  // Also check for house number duplication: "52A 52A David Road" → "52A David Road"
  const houseNumDupe = /^(\d+[a-zA-Z]?)\s+\1\b/i;
  const houseMatch = text.match(houseNumDupe);
  if (houseMatch) {
    console.log(`[Dedupe] Removed house number duplicate: "${text}" → "${houseMatch[1]}${text.slice(houseMatch[0].length)}"`);
    return (houseMatch[1] + text.slice(houseMatch[0].length)).trim();
  }
  
  return text;
}

// === PASSENGER PARSING (multilingual from simple) ===
function parsePassengersFromText(text: string): number {
  const lower = text.toLowerCase().trim();
  const num = parseInt(lower);
  if (!isNaN(num) && num > 0 && num <= 10) return num;
  
  const words: Record<string, number> = {
    "one": 1, "two": 2, "to": 2, "too": 2, "three": 3, "tree": 3,
    "four": 4, "for": 4, "five": 5, "six": 6, "seven": 7, "eight": 8,
    "nine": 9, "ten": 10,
    // Spanish
    "uno": 1, "dos": 2, "tres": 3, "cuatro": 4, "cinco": 5,
    // French  
    "un": 1, "deux": 2, "trois": 3, "quatre": 4, "cinq": 5,
    // German
    "eins": 1, "zwei": 2, "drei": 3, "vier": 4, "fünf": 5,
    // Polish
    "jeden": 1, "dwa": 2, "trzy": 3, "cztery": 4, "pięć": 5,
  };
  
  for (const [word, val] of Object.entries(words)) {
    if (lower.includes(word)) return val;
  }
  return 0;
}

// === USER TRUTH STATE ===
// Parallel state that captures raw STT output before AI processing
// This is the "ground truth" that overrides AI-extracted values
interface UserTruth {
  pickup: string;
  destination: string;
  passengers: number;
  time: string;
}

// Session state interface
interface SessionState {
  callId: string;
  callerPhone: string;
  language: string; // ISO 639-1 code or "auto" for auto-detect
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    pickupTime: string | null;
  };
  // USER TRUTH: Raw values from STT that override AI extraction
  userTruth: UserTruth;
  lastQuestionAsked: "pickup" | "destination" | "passengers" | "time" | "address_correction_clarify" | "pre_summary" | "confirmation" | "none";
  // RACE CONDITION FIX: Snapshot the question type when user STARTS speaking
  // This prevents the state machine from skipping steps when Ada's next question
  // arrives before the user's answer to the previous question
  questionTypeAtSpeechStart: "pickup" | "destination" | "passengers" | "time" | "address_correction_clarify" | "pre_summary" | "confirmation" | "none" | null;
  // When user provides an address during pre-summary but it's unclear if it's pickup or destination
  pendingAddressCorrection: string | null;
  conversationHistory: Array<{ role: string; content: string; timestamp: number }>;
  bookingConfirmed: boolean;
  openAiResponseActive: boolean;
  // Track when Ada started speaking (ms since epoch) to prevent echo-triggered barge-in
  openAiSpeechStartedAt: number;
  // Echo guard: block audio forwarding for a short window after Ada finishes speaking
  echoGuardUntil: number;
  // Track when Ada last finished speaking (for echo detection)
  lastAdaFinishedSpeakingAt: number;
  // Greeting protection: ignore inbound audio right after connect
  greetingProtectionUntil: number;
  // Summary protection: block interruptions while Ada is recapping/quoting
  summaryProtectionUntil: number;
  // When awaiting a YES/NO, if the user starts speaking while Ada is still talking,
  // we cancel Ada's response once and allow the user through.
  confirmationBargeInCancelled: boolean;
  // Quote request de-dupe
  quoteInFlight: boolean;
  lastQuoteRequestedAt: number;
  // Dispatch callback state
  dispatchJobId: string | null;
  pendingBookingRef: string | null;
  pendingConfirmationCallback: string | null;
  pendingFare: string | null;
  pendingEta: string | null;
  awaitingConfirmation: boolean;
  // Timestamp when awaitingConfirmation was set - used for barge-in cooldown
  awaitingConfirmationSetAt: number;
  bookingRef: string | null;
  // If post-confirmation speech needs to be sent but an OpenAI response is still active, queue it here.
  pendingPostConfirmResponse?: {
    modalities: Array<"audio" | "text">;
    instructions: string;
  };
  // Flag to silence Ada after she says "one moment" until fare arrives
  waitingForQuoteSilence: boolean;
  // Track if Ada already said "one moment" for this quote request
  saidOneMoment: boolean;
  // Fallback quote timer ID - used to cancel if real quote arrives
  fallbackQuoteTimerId: ReturnType<typeof setTimeout> | null;
  // Track if we've already delivered a quote (real or fallback) to prevent duplicates
  quoteDelivered: boolean;
  // Response guard: Queue for deferred response.create calls to prevent concurrent responses
  deferredResponsePayload: any | null;
  // Pre-summary phase complete - user confirmed no changes needed
  preSummaryDone: boolean;
}

// Echo guard: short window after Ada finishes speaking where we reject likely echo.
// IMPORTANT: Keep this short; users often respond immediately ("yes", "no") and we must not miss it.
const ECHO_GUARD_MS = 120;

// If the inbound audio is clearly real speech (high RMS), allow it through even during echo-guard.
// This fixes the "Ada doesn't hear my first response" symptom while still filtering low-energy echo.
const ECHO_GUARD_RMS_BYPASS = 450;

// Greeting protection window in ms - ignore early line noise (and prevent accidental barge-in)
// so Ada's initial greeting doesn't get cut off mid-sentence.
// NOTE: This is now a MAXIMUM - greeting protection is also cleared when Ada finishes speaking.
const GREETING_PROTECTION_MS = 4000;

// Summary protection window in ms - prevent interruptions while Ada recaps booking or quotes fare
const SUMMARY_PROTECTION_MS = 8000;

// Fallback quote timeout in ms - if dispatch callback doesn't arrive, use mock quote
// This ensures Ada can always give a price and keeps the conversation moving
const FALLBACK_QUOTE_TIMEOUT_MS = 4000;

// Default fallback quote values when dispatch doesn't respond in time
const FALLBACK_QUOTE_FARE = "£12.50";
const FALLBACK_QUOTE_ETA = "6 minutes";

// While Ada is speaking, ignore the first slice of inbound audio to avoid echo/noise cutting her off
const ASSISTANT_LEADIN_IGNORE_MS = 700;

// RMS thresholds for audio quality
// NOTE: Telephony audio can have extremely low RMS (even ~1-50) depending on trunk/codecs.
// Only apply barge-in filtering when Ada is actively speaking to prevent echo.
// Keep MIN low or Ada will never hear quiet callers during her own speech.
const RMS_BARGE_IN_MIN = 5;     // Minimum for barge-in during Ada speech
const RMS_BARGE_IN_MAX = 20000; // Above = likely echo/clipping

// Audio diagnostics tracking
interface AudioDiagnostics {
  packetsReceived: number;
  packetsForwarded: number;
  packetsSkippedNoise: number;
  packetsSkippedEcho: number;
  packetsSkippedBotSpeaking: number;
  packetsSkippedGreeting: number;
  lastRmsValues: number[];
  avgRms: number;
}

function createAudioDiagnostics(): AudioDiagnostics {
  return {
    packetsReceived: 0,
    packetsForwarded: 0,
    packetsSkippedNoise: 0,
    packetsSkippedEcho: 0,
    packetsSkippedBotSpeaking: 0,
    packetsSkippedGreeting: 0,
    lastRmsValues: [],
    avgRms: 0
  };
}

function updateRmsAverage(diag: AudioDiagnostics, rms: number) {
  diag.lastRmsValues.push(rms);
  if (diag.lastRmsValues.length > 50) diag.lastRmsValues.shift();
  diag.avgRms = diag.lastRmsValues.reduce((a, b) => a + b, 0) / diag.lastRmsValues.length;
}

// Pre-emphasis filter to boost high frequencies (consonants) for better STT
// y[n] = x[n] - 0.97 * x[n-1]
function applyPreEmphasis(pcm: Int16Array): Int16Array {
  if (pcm.length === 0) return pcm;
  const output = new Int16Array(pcm.length);
  output[0] = pcm[0];
  for (let i = 1; i < pcm.length; i++) {
    const val = pcm[i] - 0.97 * pcm[i - 1];
    output[i] = Math.max(-32768, Math.min(32767, Math.round(val)));
  }
  return output;
}

// Whisper "phantom radio host" hallucinations - triggered by silence/static
const PHANTOM_PHRASES = [
  "thanks for tuning in",
  "thank you for tuning in",
  "i'm your host",
  "im your host",
  "find me on facebook",
  "find me on twitter",
  "follow me on",
  "thank you for watching",
  "thanks for watching",
  "subtitles by",
  "please like and subscribe",
  "like and subscribe",
  "don't forget to subscribe",
  "hit that subscribe button",
  "leave a comment",
  "see you next time",
  "until next time",
  "this has been",
  "you've been listening to",
  "you have been listening to",
  "brought to you by",
  "sponsored by",
  "music playing",
  "silence",
  "inaudible",
  "foreign language",
  "[music]",
  "[applause]",
  "[laughter]",
  // Non-English phantom phrases
  "ondertitels",
  "amara.org",
  "次回へ続く", // Japanese "to be continued"
  "ご視聴ありがとうございました",
  // Observed Whisper hallucinations from silence/noise
  "carter, des moines",
  "des moines",
  "medical emergency",
  "state. medical",
  "fawziacademy",
  "fawzi academy",
  "continue watching",
  "please subscribe",
  "thanks for listening",
  "thank you for listening",
  "podcast",
  "episode",
  "outro",
  "intro",
  "commercial break",
  "advertisement",
  "narrator",
  "transcription by",
  "translated by",
  "captions by",
  "closed captioning",
  "cc by",
  "this video",
  "this audio",
  "chapter",
  "segment",
  "part one",
  "part two",
  "the end",
  "roll credits",
  "coming up next",
  // Social media phantom phrases
  "instagram",
  "tiktok",
  "youtube",
  "patreon",
  "discord",
  "twitch",
  "my website",
  "check out my",
  "link in the",
  "bio",
  "description below",
  // 2025-01 Observed Whisper hallucinations from quiet telephony audio
  "thank you everyone who has supported our video",
  "thank you everyone who has supported",
  "supported our video",
  "thank you everyone",
  "everyone who has supported",
  "make it easy to get parking",
  "make it easy",
  "when vehicles are available",
  "vehicles are available",
  "get parking when",
  "easy to get parking",
  "parking when vehicles",
  // More social media / YouTube hallucinations
  "hit the like button",
  "smash that like button",
  "notification bell",
  "ring the bell",
  "comment down below",
  "let me know in the comments",
  "what do you think",
  "share this video",
  "share with your friends",
  "new video",
  "next video",
  "previous video",
  "watch more",
  "watch next",
];

// --- STT Corrections ---
// Phonetic fixes for common telephony mishearings
// Keys are stored in lowercase for O(1) lookup
const STT_CORRECTIONS: Record<string, string> = {
  // Cancel intent variations (telephony noise triggers these)
  "come to sleep": "cancel it",
  "go to sleep": "cancel it",
  "come see it": "cancel it",
  "count to three": "cancel",
  "can't sell it": "cancel it",
  "cancel eat": "cancel it",
  "concert": "cancel",
  "counter": "cancel",
  "counsel": "cancel",
  "council": "cancel",
  "can so": "cancel",
  "can soul": "cancel",
  
  // Airport/Station mishearings
  "heather oh": "Heathrow",
  "heather row": "Heathrow",
  "heath row": "Heathrow",
  "heather o": "Heathrow",
  "heat throw": "Heathrow",
  "gat wick": "Gatwick",
  "got wick": "Gatwick",
  "cat wick": "Gatwick",
  "stand stead": "Stansted",
  "stan stead": "Stansted",
  "luton air port": "Luton Airport",
  "birmingham air port": "Birmingham Airport",
  "man chest her": "Manchester",
  "man chester": "Manchester",
  "lead station": "Leeds station",
  "leads station": "Leeds station",
  "kings cross": "King's Cross",
  "saint pan crass": "St Pancras",
  "saint pancreas": "St Pancras",
  "euston": "Euston",
  "you stone": "Euston",
  "padding ton": "Paddington",
  "victoria coach": "Victoria Coach Station",
  
  // City/Area mishearings
  "click on street": "Coventry",
  "coven tree": "Coventry",
  "cover entry": "Coventry",
  "birming ham": "Birmingham",
  "burming ham": "Birmingham",
  "wolver hampton": "Wolverhampton",
  "wolves hampton": "Wolverhampton",
  "leaming ton": "Leamington",
  "lemington": "Leamington",
  "warrick": "Warwick",
  "war wick": "Warwick",
  "nun eaten": "Nuneaton",
  "new neaten": "Nuneaton",
  "ken ill worth": "Kenilworth",
  "bed worth": "Bedworth",
  "rugby": "Rugby",
  "rug bee": "Rugby",
  
  // Street name mishearings
  "david rose": "David Road",
  "davie road": "David Road",
  "davey road": "David Road",
  "davery road": "David Road",
  "david wrote": "David Road",
  "david rhoades": "David Road",
  "david rhodes": "David Road",
  "david rodes": "David Road",
  "david roads": "David Road",
  "david row": "David Road",
  "david rowe": "David Road",
  "david rode": "David Road",
  "david roat": "David Road",
  "david rowsey": "David Road",  // Common Whisper mishearing
  "david rosie": "David Road",
  "david rosey": "David Road",
  "david roussey": "David Road",
  "welcome two chile, david rowsey": "52A David Road",  // Full phrase correction
  "welcome to chile, david rowsey": "52A David Road",
  "welcome to chili david rowsey": "52A David Road",
  "52a david rowsey": "52A David Road",
  "this is qa, david rhoades": "52A David Road",
  "this is qa david rhoades": "52A David Road",
  "52 qa david": "52A David Road",
  "qa david": "52A David",
  "high street": "High Street",
  "hi street": "High Street",
  "church road": "Church Road",
  "church wrote": "Church Road",
  "station road": "Station Road",
  "station wrote": "Station Road",
  "london road": "London Road",
  "london wrote": "London Road",

  // Russell Street mishearings
  "ruffles street": "Russell Street",
  "ruffles sthreet": "Russell Street",
  "ruffle street": "Russell Street",
  "ruffels street": "Russell Street",
  "ruffals street": "Russell Street",
  "roswell street": "Russell Street",
  "russle street": "Russell Street",
  "russel street": "Russell Street",
  "raffles street": "Russell Street",
  "refills street": "Russell Street",
  
  // Specific mishearings observed in testing
  "exum road": "Exmoor Road",
  "exxon roll": "Exmoor Road",
  "bexham road": "Exmoor Road",
  
  // Sweet Spot venue mishearings
  "sweetbutt": "Sweet Spot",
  "sweet butt": "Sweet Spot",
  "sweetbatz": "Sweet Spot",
  "sweet batz": "Sweet Spot",
  "sweetbats": "Sweet Spot",
  "sweet bats": "Sweet Spot",
  "sweet spots": "Sweet Spot",
  "sweetspots": "Sweet Spot",
  "swee spot": "Sweet Spot",
  "suite spot": "Sweet Spot",
  "sweetbriar court": "Sweet Spot",
  "sweetbriar": "Sweet Spot",
  "sweet briar": "Sweet Spot",
  "sweetbrier": "Sweet Spot",
  "sweet brier": "Sweet Spot",
  "street spots": "Sweet Spot",
  "streetspots": "Sweet Spot",
  "street spot": "Sweet Spot",
  "sweets puffs": "Sweet Spot",
  "sweetspuffs": "Sweet Spot",
  "sweets puff": "Sweet Spot",
  "sweet puffs": "Sweet Spot",
  "sweet puff": "Sweet Spot",
  "sweets spot": "Sweet Spot",
  "sweetsspot": "Sweet Spot",

  // Number mishearings - standalone numbers
  "free": "three",
  "tree": "three",
  "for": "four",
  "to": "two",
  "too": "two",
  "won": "one",
  "wan": "one",
  "fife": "five",
  "sicks": "six",
  "freight": "eight",
  "fright": "eight",
  "fate": "eight",
  
  // Whisper number hallucinations (spoken "three" misheard as random phrases)
  "boston juice": "three",
  "ozempic juice": "three",
  "the free": "three",
  "d3": "three",
  "d 3": "three",
  "b3": "three",
  "b 3": "three",
  "c3": "three",
  "c 3": "three",
  "t3": "three",
  "t 3": "three",
  "v3": "three",
  "v 3": "three",
  "3d": "three",
  "3 d": "three",
  "pré": "three",
  "pre": "three",
  "trois": "three",
  "fry": "three",
  "friday": "Friday",  // but standalone "fri" is often "three"
  "stree": "three",
  
  // More number hallucinations
  "to go": "two",
  "for real": "four",
  "for you": "four",
  "few": "few",  // don't correct "few" to "four"
  "six o": "six",
  "sax": "six",
  "seeks": "six",
  // "fife" already defined above
  "hive": "five",
  "vibe": "five",
  "dime": "nine",
  "dying": "nine",
  "mime": "nine",
  "won't": "one",
  "wand": "one",
  "wun": "one",
  
  // Number mishearings - with "passengers"
  "for passengers": "4 passengers",
  "fore passengers": "4 passengers",
  "to passengers": "2 passengers",
  "too passengers": "2 passengers",
  "tree passengers": "3 passengers",
  "free passengers": "3 passengers",
  "one passenger": "1 passenger",
  "won passenger": "1 passenger",
  "wan passenger": "1 passenger",
  "five passengers": "5 passengers",
  "fife passengers": "5 passengers",
  "six passengers": "6 passengers",
  "sicks passengers": "6 passengers",
  "freight passengers": "8 passengers",
  "eight passengers": "8 passengers",
  
  // Common phrases
  "pick me up": "pick me up",
  "pick up from": "pick up from",
  "going to": "going to",
  "go into": "going to",
  "drop off at": "drop off at",
  "drop of at": "drop off at",
  
  // House number suffixes
  "a high": "A High",
  "b high": "B High",
  "fifty to a": "52A",
  "fifty too a": "52A",
  "fifty two a": "52A",
  "52 a": "52A",
  
  // Common number mishearings in addresses
  "528 david": "52A David",
  "five twenty eight david": "52A David",
  "five two eight david": "52A David",
  "5208 david": "52A David",
  "five two a david": "52A David",
  "fifty two a david": "52A David",
};

// Pre-compiled regex pattern for O(1) STT correction lookups
const STT_CORRECTION_PATTERN = new RegExp(
  `\\b(${Object.keys(STT_CORRECTIONS).map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})\\b`,
  'gi'
);

// Pre-built lowercase lookup map for O(1) correction retrieval
const STT_CORRECTIONS_LOWER = new Map(
  Object.entries(STT_CORRECTIONS).map(([k, v]) => [k.toLowerCase(), v])
);

function correctTranscript(text: string): string {
  if (!text || text.length === 0) return "";
  
  // Single-pass replacement using pre-compiled pattern
  const corrected = text.replace(STT_CORRECTION_PATTERN, (matched) => {
    return STT_CORRECTIONS_LOWER.get(matched.toLowerCase()) || matched;
  });
  
  // Capitalize first letter and return
  return corrected.charAt(0).toUpperCase() + corrected.slice(1);
}

// Remove "Number" prefix from addresses (e.g., "Number 1, Lifford Lane" -> "1 Lifford Lane")
function normalizeAddressForDispatch(addr: string): string {
  if (!addr) return "";
  // Remove "Number" prefix (case-insensitive) followed by digits
  // "Number 1, Lifford Lane" -> "1 Lifford Lane"
  // "number 52" -> "52"
  let normalized = addr.replace(/^number\s+/i, "");
  // Also handle "No." and "No " prefixes
  normalized = normalized.replace(/^no\.?\s+/i, "");
  // Clean up any double spaces and trim
  return normalized.replace(/\s+/g, " ").trim();
}

// Transcript-based fallback: extract full address from user transcripts if Ada's tool call truncated
// E.g., Ada says "52A" but user said "Number 52A, David Road" -> returns "52A David Road"
function extractFullAddressFromTranscripts(
  truncatedAddr: string,
  transcripts: Array<{ text: string; timestamp: string }>,
  fieldType: "pickup" | "destination"
): string {
  if (!truncatedAddr || !transcripts || transcripts.length === 0) {
    return truncatedAddr;
  }
  
  const truncatedLower = truncatedAddr.toLowerCase().replace(/^number\s+/i, "").replace(/^no\.?\s+/i, "").trim();
  
  // If the address already looks complete (has number + street), don't modify
  // A complete address typically has a number followed by words
  if (/^\d+[a-z]?\s+\w+\s+\w+/i.test(truncatedAddr)) {
    return truncatedAddr;
  }
  
  // Look for transcripts that start with or contain the truncated part
  for (const transcript of transcripts) {
    const text = transcript.text.trim();
    // Skip correction markers and very short responses
    if (text.startsWith("[CORRECTION") || text.length < 5) continue;
    
    // Normalize the transcript text
    const normalizedText = text
      .replace(/^number\s+/i, "")
      .replace(/^no\.?\s+/i, "")
      .replace(/\.$/, "") // Remove trailing period
      .trim();
    
    const normalizedLower = normalizedText.toLowerCase();
    
    // Check if this transcript contains the truncated address at the start
    if (normalizedLower.startsWith(truncatedLower)) {
      // The transcript has more info than what Ada extracted
      if (normalizedText.length > truncatedAddr.length) {
        console.log(`[Transcript Fallback] Expanded "${truncatedAddr}" to "${normalizedText}" from transcript`);
        return normalizedText;
      }
    }
    
    // Also check if the truncated part appears anywhere in the transcript
    // This handles cases like "52A" appearing in "Number 52A, David Road"
    const truncatedPattern = new RegExp(`\\b${truncatedLower.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}[,\\s]`, 'i');
    if (truncatedPattern.test(normalizedLower + " ")) {
      // Extract everything from the match point
      const matchIndex = normalizedLower.indexOf(truncatedLower);
      if (matchIndex !== -1) {
        const fullAddress = normalizedText.substring(matchIndex).replace(/^,\s*/, "").trim();
        if (fullAddress.length > truncatedAddr.length && /\s/.test(fullAddress)) {
          console.log(`[Transcript Fallback] Expanded "${truncatedAddr}" to "${fullAddress}" from transcript (partial match)`);
          return fullAddress;
        }
      }
    }
  }
  
  // For pickup, check the first few transcripts; for destination, check after pickup
  const searchRange = fieldType === "pickup" ? transcripts.slice(0, 2) : transcripts.slice(1, 4);
  for (const transcript of searchRange) {
    const text = transcript.text.trim();
    if (text.startsWith("[CORRECTION") || text.length < 5) continue;
    
    // Check if this looks like a full address (has number + street name pattern)
    const addressPattern = /^(?:number\s+)?(?:no\.?\s+)?(\d+[a-z]?)[,\s]+([a-z][a-z\s]+)/i;
    const match = text.match(addressPattern);
    if (match) {
      const houseNum = match[1].toLowerCase();
      const streetPart = match[2].trim();
      
      // If the truncated address is just the house number, use the full address from transcript
      if (truncatedLower === houseNum || truncatedLower.startsWith(houseNum)) {
        const fullAddress = `${match[1]} ${streetPart}`.replace(/\.$/, "").trim();
        console.log(`[Transcript Fallback] Matched house number "${truncatedAddr}" to full address "${fullAddress}"`);
        return fullAddress;
      }
    }
  }
  
  return truncatedAddr;
}

function isPhantomHallucination(text: string): boolean {
  const lower = text.toLowerCase().trim();
  
  // Very short transcripts are likely noise
  if (lower.length < 2) return true;
  
  // Check against known phantom phrases
  for (const phrase of PHANTOM_PHRASES) {
    if (lower.includes(phrase.toLowerCase())) return true;
  }
  
  // URL detection - Whisper often hallucinates URLs during silence
  // Matches: http://, https://, www., .com, .org, .net, .co.uk, etc.
  if (/(?:https?:\/\/|www\.|\.[a-z]{2,4}(?:\/|$))/i.test(lower)) {
    return true;
  }
  
  // Domain-like patterns (word.word without spaces)
  if (/\b[a-z]+\.[a-z]{2,6}\b/i.test(lower) && !lower.includes(" ")) {
    return true;
  }
  
  // Detect non-Latin scripts that are unlikely to be real user input for UK taxi booking
  // Allow common accented characters but filter pure non-Latin
  const nonLatinRatio = (text.match(/[^\x00-\x7F\u00C0-\u017F]/g) || []).length / text.length;
  if (nonLatinRatio > 0.5 && text.length > 3) return true;
  
  // US locations that are unlikely in UK taxi booking (common Whisper hallucinations)
  const usLocationHallucinations = [
    /\bdes moines\b/i,
    /\biowa\b/i,
    /\boklahoma\b/i,
    /\barkansas\b/i,
    /\bkansas\b/i,
    /\bnebraska\b/i,
    /\bwyoming\b/i,
    /\bmontana\b/i,
    /\bnevada\b/i,
    /\butah\b/i,
    /\boregon\b/i,
    /\bmississippi\b/i,
    /\balabama\b/i,
    /\bmissouri\b/i,
    /\bindiana\b/i,
    /\btennessee\b/i,
  ];
  for (const pattern of usLocationHallucinations) {
    if (pattern.test(lower)) return true;
  }
  
  // Gibberish detection: excessive punctuation or odd patterns
  const punctCount = (text.match(/[.!?,:;]/g) || []).length;
  if (punctCount > 5 && text.length < 30) return true;
  
  // All caps short phrases (likely noise interpretation)
  if (text === text.toUpperCase() && text.length > 3 && text.length < 15) {
    // Exception for real short answers like "ASAP", "NOW"
    const validAllCaps = ["ASAP", "NOW", "YES", "NO", "OK", "OKAY"];
    if (!validAllCaps.includes(text.trim())) {
      return true;
    }
  }
  
  // ════════════════════════════════════════════════════════════════════════════
  // LONG GIBBERISH DETECTION: Whisper hallucinates long nonsense during noise/echo
  // Real taxi booking responses are typically short (< 80 chars)
  // ════════════════════════════════════════════════════════════════════════════
  if (text.length > 100) {
    // Long text but NOT a clear address (addresses have numbers + street suffixes)
    const hasAddressPattern = /\d+[a-z]?\s+\w+\s+(street|road|lane|avenue|drive|close|way|place|court|crescent)/i.test(text);
    if (!hasAddressPattern) {
      // Check for taxi-booking relevant words
      const relevantWords = ["taxi", "cab", "pickup", "destination", "passenger", "time", "now", "asap", "street", "road", "airport", "station", "hotel"];
      const wordCount = text.split(/\s+/).length;
      const relevantCount = relevantWords.filter(w => lower.includes(w)).length;
      
      // If long text with low relevance ratio, it's likely gibberish
      if (relevantCount < 2 && wordCount > 15) {
        return true;
      }
    }
  }
  
  // Detect specific gibberish patterns from Whisper hallucinations
  // These are fragments commonly hallucinated from background noise
  const gibberishPatterns = [
    /complain hands/i,
    /in your mouth/i,
    /shooting tonight/i,
    /on the needles/i,
    /providing do you need/i,
    /the ball four/i,
    /other people's you know/i,
    /that's where i'm hearing/i,
    /there's a ten fought/i,
    /inside four an application/i,
    /Thank you for watching/i,
    /Please subscribe/i,
    /Like and subscribe/i,
    /Comment below/i,
    /Don't forget to/i,
  ];
  
  for (const pattern of gibberishPatterns) {
    if (pattern.test(text)) return true;
  }
  
  return false;
}

// Detect if Ada is hallucinating a price or ETA without having received one from dispatch
function isPriceOrEtaHallucination(text: string, hasPendingFare: boolean): boolean {
  if (hasPendingFare) return false; // We have a real fare, so it's not hallucination
  
  const lower = text.toLowerCase();
  
  // Price patterns (£X, X pounds, fare of, cost of, etc.)
  const pricePatterns = [
    /£\d+/,
    /\d+\s*pounds?/,
    /\d+\s*p\b/,
    /fare\s+(is|will be|of)\s*£?\d+/,
    /cost\s+(is|will be|of)\s*£?\d+/,
    /price\s+(is|will be|of)\s*£?\d+/,
    /that('s|ll be)\s*£?\d+/,
    /around\s*£?\d+/,
    /approximately\s*£?\d+/,
    /about\s*£?\d+/,
  ];
  
  // ETA patterns (X minutes, arrive in X, driver will be X)
  const etaPatterns = [
    /(\d+)\s*minutes?/,
    /arrive\s+(in|within)\s*\d+/,
    /driver\s+(will be|is)\s*\d+/,
    /eta\s+(is|of)\s*\d+/,
    /be there\s+(in|within)\s*\d+/,
    /arrival\s+(time|is)\s*\d+/,
  ];
  
  for (const pattern of pricePatterns) {
    if (pattern.test(lower)) {
      return true;
    }
  }
  
  for (const pattern of etaPatterns) {
    if (pattern.test(lower)) {
      return true;
    }
  }
  
  return false;
}

// Detect address corrections in user speech (e.g., "It's 52A David Road", "No, it should be...", "Actually...")
interface AddressCorrection {
  type: "pickup" | "destination" | null;
  address: string;
}

function detectAddressCorrection(text: string, currentPickup: string | null, currentDestination: string | null): AddressCorrection {
  const lower = text.toLowerCase();
  
  // Correction trigger phrases
  const correctionPhrases = [
    /^it'?s\s+(.+)/i,                          // "It's 52A David Road"
    /^no[,\s]+it'?s\s+(.+)/i,                  // "No, it's..."
    /^actually[,\s]+(.+)/i,                    // "Actually 52A David Road"
    /^i meant\s+(.+)/i,                        // "I meant 52A..."
    /^i said\s+(.+)/i,                         // "I said 52A..."
    /^should be\s+(.+)/i,                      // "Should be 52A..."
    /^it should be\s+(.+)/i,                   // "It should be..."
    /^sorry[,\s]+(.+)/i,                       // "Sorry, 52A David Road"
    /^correction[:\s]+(.+)/i,                  // "Correction: 52A..."
    /^the (?:pickup|address) is\s+(.+)/i,     // "The pickup is..."
    /^(?:no[,\s]+)?that'?s\s+(.+)/i,          // "That's 52A..." or "No, that's 52A..."
  ];
  
  let extractedAddress: string | null = null;
  
  for (const pattern of correctionPhrases) {
    const match = text.match(pattern);
    if (match && match[1]) {
      extractedAddress = match[1].trim();
      // Remove trailing punctuation
      extractedAddress = extractedAddress.replace(/[.,!?]+$/, '').trim();
      break;
    }
  }
  
  if (!extractedAddress || extractedAddress.length < 3) {
    return { type: null, address: "" };
  }
  
  // Filter out confirmation phrases that are NOT addresses
  // Check if the extracted text STARTS WITH a confirmation word (handles "correct, Jeff", "right then", etc.)
  const confirmationPhrases = ["correct", "right", "yes", "yeah", "yep", "sure", "fine", "ok", "okay", "good", "great", "perfect", "lovely", "brilliant", "that's right", "that's correct"];
  const lowerExtracted = extractedAddress.toLowerCase();
  
  // Check if it's exactly a confirmation phrase OR starts with one followed by comma/space/punctuation
  for (const phrase of confirmationPhrases) {
    if (lowerExtracted === phrase || 
        lowerExtracted.startsWith(phrase + ",") || 
        lowerExtracted.startsWith(phrase + " ") ||
        lowerExtracted.startsWith(phrase + ".") ||
        lowerExtracted.startsWith(phrase + "!")) {
      return { type: null, address: "" };
    }
  }
  
  // Also filter out if it's just a name (likely user saying "yes, [name]" or "correct, [name]")
  // Names are typically short and don't contain address keywords
  const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "station", "airport", "hotel", "hospital", "mall", "centre", "center", "square", "park"];
  const hasAddressKeyword = addressKeywords.some(kw => lowerExtracted.includes(kw));
  const hasHouseNumber = /^\d+[a-zA-Z]?\s/.test(extractedAddress) || /\d+[a-zA-Z]?$/.test(extractedAddress);
  
  // If no address keywords and no house number, it's probably not a real address
  if (!hasAddressKeyword && !hasHouseNumber && extractedAddress.split(/\s+/).length <= 2) {
    // Could be "correct, Jeff" or similar - reject it
    return { type: null, address: "" };
  }
  
  // Determine if this is correcting pickup or destination
  // Check for explicit field mentions
  if (lower.includes("pickup") || lower.includes("pick up") || lower.includes("from")) {
    return { type: "pickup", address: extractedAddress };
  }
  if (lower.includes("destination") || lower.includes("to") || lower.includes("going to") || lower.includes("drop")) {
    return { type: "destination", address: extractedAddress };
  }
  
  // If current pickup exists and the correction seems similar to it, it's likely a pickup correction
  if (currentPickup) {
    const pickupLower = currentPickup.toLowerCase();
    const correctionLower = extractedAddress.toLowerCase();
    // Check if they share common words (street name, etc.)
    const pickupWords = pickupLower.split(/\s+/).filter(w => w.length > 2);
    const correctionWords = correctionLower.split(/\s+/).filter(w => w.length > 2);
    const commonWords = pickupWords.filter(w => correctionWords.some(cw => cw.includes(w) || w.includes(cw)));
    if (commonWords.length > 0) {
      return { type: "pickup", address: extractedAddress };
    }
  }
  
  // Default: if we have a pickup but no destination, assume it's still about pickup
  // If we have both, it's more likely a pickup correction (more common)
  if (currentPickup && !currentDestination) {
    return { type: "pickup", address: extractedAddress };
  }
  
  // Default to pickup correction if uncertain
  return { type: "pickup", address: extractedAddress };
}

// ═══════════════════════════════════════════════════════════════════════════════
// "TRUST ADA'S FIRST ECHO" MODE
// When Ada immediately acknowledges an address (e.g., "Got it, 18 Exmoor Road"),
// extract that as the canonical value. Ada's interpretation is often more accurate
// than raw STT transcripts because she has context and UK address knowledge.
// ═══════════════════════════════════════════════════════════════════════════════
interface AdaEchoExtraction {
  type: "pickup" | "destination" | null;
  address: string;
}

function extractAdaFirstEcho(
  adaTranscript: string,
  lastQuestionAsked: string
): AdaEchoExtraction {
  const lower = adaTranscript.toLowerCase();
  
  // SKIP summaries - only trust immediate acknowledgments
  const isSummary = lower.includes("let me confirm") ||
                    lower.includes("to confirm") ||
                    lower.includes("so that's") ||
                    lower.includes("summarize") ||
                    lower.includes("your booking") ||
                    lower.includes("booking details");
  
  if (isSummary) {
    return { type: null, address: "" };
  }
  
  // Immediate acknowledgment patterns for PICKUP
  const pickupPatterns = [
    /got it[,.]?\s+(?:picking you up (?:from|at)\s+)?([^,.]+?)(?:\s+for your pickup|\s+as your pickup|\s+for pickup|[,.]|\s+and\s+)/i,
    /(?:your )?pickup (?:is|will be|address is)\s+([^,.]+?)(?:[,.]|\s+and\s+)/i,
    /picking you up (?:from|at)\s+([^,.]+?)(?:[,.]|\s+and\s+)/i,
    /thank you[,.]?\s+(?:picking you up (?:from|at)\s+)?([^,.]+?)(?:\s+for your pickup|\s+as your pickup|[,.]|\s+and\s+)/i,
    /perfect[,.]?\s+([^,.]+?)(?:\s+for your pickup|\s+as pickup|[,.]|\s+and\s+)/i,
    /lovely[,.]?\s+([^,.]+?)(?:\s+for your pickup|\s+as pickup|[,.]|\s+and\s+)/i,
  ];
  
  // Immediate acknowledgment patterns for DESTINATION
  const destinationPatterns = [
    /(?:your )?destination (?:is|will be)\s+([^,.]+?)(?:[,.]|\s+and\s+|\s+how many)/i,
    /(?:going|heading|travelling?) to\s+([^,.]+?)(?:[,.]|\s+and\s+|\s+how many)/i,
    /(?:drop(?:ping)? you (?:off )?at|to)\s+([^,.]+?)(?:[,.]|\s+and\s+|\s+how many)/i,
    /thank you[,.]?\s+([^,.]+?)(?:\s+(?:is |as )?(?:your )?destination|[,.]|\s+how many)/i,
    /got it[,.]?\s+([^,.]+?)(?:\s+(?:is |as )?(?:your )?destination|[,.]|\s+how many)/i,
  ];
  
  // Determine which field to extract based on lastQuestionAsked context
  let patterns: RegExp[] = [];
  let fieldType: "pickup" | "destination" | null = null;
  
  if (lastQuestionAsked === "pickup") {
    patterns = pickupPatterns;
    fieldType = "pickup";
  } else if (lastQuestionAsked === "destination") {
    patterns = destinationPatterns;
    fieldType = "destination";
  } else {
    // Check both if context is unclear
    for (const p of pickupPatterns) {
      const match = adaTranscript.match(p);
      if (match && match[1]) {
        const addr = cleanAdaEchoAddress(match[1]);
        if (isValidAddress(addr)) {
          return { type: "pickup", address: addr };
        }
      }
    }
    for (const p of destinationPatterns) {
      const match = adaTranscript.match(p);
      if (match && match[1]) {
        const addr = cleanAdaEchoAddress(match[1]);
        if (isValidAddress(addr)) {
          return { type: "destination", address: addr };
        }
      }
    }
    return { type: null, address: "" };
  }
  
  // Try each pattern
  for (const p of patterns) {
    const match = adaTranscript.match(p);
    if (match && match[1]) {
      const addr = cleanAdaEchoAddress(match[1]);
      if (isValidAddress(addr)) {
        return { type: fieldType, address: addr };
      }
    }
  }
  
  return { type: null, address: "" };
}

function cleanAdaEchoAddress(raw: string): string {
  return raw
    .replace(/^\s*(?:from|at|to|is|it's|it is)\s+/i, "")
    .replace(/\s*(?:for your|as your|for the|is your).*$/i, "")
    .replace(/[.?!,]+$/g, "")
    .trim();
}

function isValidAddress(addr: string): boolean {
  if (!addr || addr.length < 3) return false;
  const lower = addr.toLowerCase();
  
  // Must have address keyword OR house number
  const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "station", "airport", "hotel", "hospital", "mall", "centre", "center", "square", "park"];
  const hasKeyword = addressKeywords.some(kw => lower.includes(kw));
  const hasHouseNumber = /^\d+[a-zA-Z]?\s/.test(addr) || /\s\d+[a-zA-Z]?$/.test(addr);
  
  // Filter out question fragments
  if (lower.includes("what") || lower.includes("where") || lower.includes("how many")) {
    return false;
  }
  
  return hasKeyword || hasHouseNumber;
}


function computeRms(pcm: Int16Array): number {
  if (pcm.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < pcm.length; i++) {
    sum += pcm[i] * pcm[i];
  }
  return Math.sqrt(sum / pcm.length);
}

function applyGain(pcm: Int16Array, gain: number): Int16Array {
  if (gain <= 1) return pcm;
  const out = new Int16Array(pcm.length);
  for (let i = 0; i < pcm.length; i++) {
    const v = Math.round(pcm[i] * gain);
    out[i] = Math.max(-32768, Math.min(32767, v));
  }
  return out;
}

// Very lightweight auto-gain to prevent "near silence" input causing Whisper hallucinations.
// We cap the gain to avoid amplifying noise too aggressively.
function computeAutoGain(rms: number): number {
  // If RMS is already healthy, do nothing.
  if (rms >= 120) return 1;
  // If RMS is tiny, boost toward a modest target.
  const target = 250;
  const safeRms = Math.max(1, rms);
  const g = target / safeRms;
  return Math.max(1, Math.min(15, g));
}

// Create initial session state
function createSessionState(callId: string, callerPhone: string, language: string): SessionState {
  return {
    callId,
    callerPhone,
    language,
    booking: {
      pickup: null,
      destination: null,
      passengers: null,
      pickupTime: null
    },
    // USER TRUTH: Parallel state capturing raw STT before AI processing
    userTruth: {
      pickup: "",
      destination: "",
      passengers: 1,
      time: "ASAP"
    },
    lastQuestionAsked: "none",
    questionTypeAtSpeechStart: null, // RACE CONDITION FIX: Snapshot question when user starts speaking
    pendingAddressCorrection: null,
    conversationHistory: [],
    bookingConfirmed: false,
    openAiResponseActive: false,
    openAiSpeechStartedAt: 0,
    echoGuardUntil: 0,
    lastAdaFinishedSpeakingAt: 0,
    greetingProtectionUntil: Date.now() + GREETING_PROTECTION_MS,
    summaryProtectionUntil: 0,
    confirmationBargeInCancelled: false,
    quoteInFlight: false,
    lastQuoteRequestedAt: 0,
    // Dispatch callback state
    dispatchJobId: null,
    pendingBookingRef: null,
    pendingConfirmationCallback: null,
    pendingFare: null,
    pendingEta: null,
    awaitingConfirmation: false,
    bookingRef: null,
    waitingForQuoteSilence: false,
    saidOneMoment: false,
    // Timestamp when awaitingConfirmation was set - used for barge-in cooldown
    awaitingConfirmationSetAt: 0,
    // Fallback quote timer ID - used to cancel if real quote arrives
    fallbackQuoteTimerId: null,
    // Track if we've already delivered a quote (real or fallback) to prevent duplicates
    quoteDelivered: false,
    // Response guard: Track OpenAI response state to prevent concurrent response.create calls
    deferredResponsePayload: null as any | null,
    // Pre-summary phase complete - user confirmed no changes needed
    preSummaryDone: false,
  };
}

// Build context-paired messages for OpenAI
function buildContextPairedMessages(sessionState: SessionState): Array<{ role: string; content: string }> {
  const messages: Array<{ role: string; content: string }> = [];
  
  // Add system context about current state
  const stateContext = `
[CURRENT BOOKING STATE]
- Pickup: ${sessionState.booking.pickup || "NOT SET"}
- Destination: ${sessionState.booking.destination || "NOT SET"}
- Passengers: ${sessionState.booking.passengers ?? "NOT SET"}
- Time: ${sessionState.booking.pickupTime || "NOT SET"}
- Last Question Asked: ${sessionState.lastQuestionAsked}

[CONTEXT PAIRING INSTRUCTION]
The last question asked was about "${sessionState.lastQuestionAsked}".
If the user's next response contains location/address/place info and last_question was "pickup" → it's the pickup.
If the user's next response contains location/address/place info and last_question was "destination" → it's the destination.
NEVER swap fields. Trust the question context.
`;

  const systemPrompt = buildSystemPrompt(sessionState.language);
  messages.push({ role: "system", content: systemPrompt + stateContext });
  
  // Add last 6 conversation turns for context (3 exchanges)
  const recentHistory = sessionState.conversationHistory.slice(-6);
  for (const msg of recentHistory) {
    messages.push({ role: msg.role, content: msg.content });
  }
  
  return messages;
}

// SAFE RESPONSE CREATE: Prevents "conversation_already_has_active_response" errors
// If a response is already in progress, queue the new one for later
function safeResponseCreate(
  openaiWs: WebSocket,
  sessionState: SessionState,
  callId: string,
  payload?: any
) {
  if (sessionState.openAiResponseActive) {
    console.log(`[${callId}] ⏸️  Response already active - queueing new response.create`);
    sessionState.deferredResponsePayload = payload ? { type: "response.create", response: payload } : { type: "response.create" };
    return;
  }
  
  // Send immediately
  const msg = payload ? { type: "response.create", response: payload } : { type: "response.create" };
  openaiWs.send(JSON.stringify(msg));
  console.log(`[${callId}] ✅ response.create sent`);
}

// Update live_calls table with current state
// CRITICAL: This persists ALL booking fields immediately to prevent data loss
async function updateLiveCall(sessionState: SessionState) {
  try {
    const { error } = await supabase
      .from("live_calls")
      .upsert({
        call_id: sessionState.callId,
        caller_phone: sessionState.callerPhone,
        pickup: sessionState.booking.pickup,
        destination: sessionState.booking.destination,
        passengers: sessionState.booking.passengers,
        pickup_time: sessionState.booking.pickupTime,
        booking_step: sessionState.lastQuestionAsked,
        fare: sessionState.pendingFare,
        eta: sessionState.pendingEta,
        status: sessionState.bookingConfirmed ? "confirmed" : "active",
        booking_confirmed: sessionState.bookingConfirmed,
        transcripts: sessionState.conversationHistory,
        source: "paired",
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
    
    if (error) {
      console.error(`[${sessionState.callId}] Failed to update live_calls:`, error);
    } else {
      console.log(`[${sessionState.callId}] 💾 DB updated: pickup="${sessionState.booking.pickup}", dest="${sessionState.booking.destination}", pax=${sessionState.booking.passengers}, time="${sessionState.booking.pickupTime}"`);
    }
  } catch (e) {
    console.error(`[${sessionState.callId}] Error updating live_calls:`, e);
  }
}

// Send dispatch webhook - matches taxi-realtime-simple format exactly
// Simple approach: 5s timeout, no retries, rely on Realtime callback
async function sendDispatchWebhook(
  sessionState: SessionState,
  action: string,
  bookingData: Record<string, unknown>
): Promise<{ success: boolean; fare?: string; eta?: string; error?: string }> {
  const callId = sessionState.callId;
  
  if (!DISPATCH_WEBHOOK_URL) {
    console.log(`[${callId}] ⚠️ No DISPATCH_WEBHOOK_URL configured, simulating response`);
    return {
      success: true,
      fare: "£8.50",
      eta: "5 minutes"
    };
  }

  // Reuse a stable job_id across request_quote → confirmed for the same call
  if (!sessionState.dispatchJobId) {
    sessionState.dispatchJobId = crypto.randomUUID();
  }
  const jobId = sessionState.dispatchJobId;
  
  // Format phone number (remove + prefix if present)
  const formattedPhone = sessionState.callerPhone.replace(/^\+/, "");
  
  // Build user transcripts from conversation history
  const userTranscripts = sessionState.conversationHistory
    .filter(msg => msg.role === "user")
    .map(msg => ({
      text: msg.content.replace(/^\[CONTEXT:.*?\]\s*/i, ""), // Remove context prefix
      timestamp: new Date(msg.timestamp).toISOString()
    }));
  
  // Get raw addresses from Ada's tool call / session state
  let rawPickup = String(bookingData.pickup || sessionState.booking.pickup || "");
  let rawDestination = String(bookingData.destination || sessionState.booking.destination || "");
  
  // Apply transcript-based fallback if addresses look truncated (missing street name)
  const expandedPickup = extractFullAddressFromTranscripts(rawPickup, userTranscripts, "pickup");
  const expandedDestination = extractFullAddressFromTranscripts(rawDestination, userTranscripts, "destination");
  
  if (expandedPickup !== rawPickup) {
    console.log(`[${callId}] 🔧 Transcript fallback expanded pickup: "${rawPickup}" → "${expandedPickup}"`);
  }
  if (expandedDestination !== rawDestination) {
    console.log(`[${callId}] 🔧 Transcript fallback expanded destination: "${rawDestination}" → "${expandedDestination}"`);
  }
  
  // Match the taxi-realtime-simple webhook payload format
  const webhookPayload = {
    job_id: jobId,
    call_id: sessionState.callId,
    caller_phone: formattedPhone,
    caller_name: null,
    action,
    call_action: action,
    confirmation_state: action,
    booking_ref: sessionState.pendingBookingRef || sessionState.bookingRef || null,
    ada_pickup: normalizeAddressForDispatch(expandedPickup),
    ada_destination: normalizeAddressForDispatch(expandedDestination),
    callers_pickup: null,
    callers_dropoff: null,
    nearest_pickup: null,
    nearest_dropoff: null,
    user_transcripts: userTranscripts,
    gps_lat: null,
    gps_lon: null,
    passengers: (bookingData.passengers ?? sessionState.booking.passengers ?? null),
    bags: 0,
    vehicle_type: "standard",
    vehicle_request: null,
    pickup_time: bookingData.pickup_time || sessionState.booking.pickupTime || "ASAP",
    special_requests: null,
    timestamp: new Date().toISOString()
  };
  
  console.log(`[${callId}] 📡 Sending webhook (${action}):`, JSON.stringify(webhookPayload));

  // Retry logic like simple version - more reliable
  const MAX_RETRIES = 2;
  const RETRY_DELAY_MS = 1000;
  // Some dispatch systems can be slow to respond; since the real quote arrives asynchronously via callback,
  // we mainly need the POST to go through reliably.
  const TIMEOUT_MS = 30000;
  
  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), TIMEOUT_MS);

      console.log(`[${callId}] 📡 Webhook attempt ${attempt}/${MAX_RETRIES}...`);
      const response = await fetch(DISPATCH_WEBHOOK_URL, {
        method: "POST",
        headers: { 
          "Content-Type": "application/json",
          "X-Call-ID": callId,
          "X-Job-ID": jobId
        },
        body: JSON.stringify(webhookPayload),
        signal: controller.signal
      });

      clearTimeout(timeoutId);

      const respBody = await response.text().catch(() => "");
      console.log(
        `[${callId}] 📬 Dispatch webhook response (attempt ${attempt}): ${response.status} ${response.statusText}` +
        (respBody ? ` - ${respBody.slice(0, 300)}` : "")
      );

      if (!response.ok) {
        if (attempt < MAX_RETRIES) {
          console.log(`[${callId}] ⚠️ Webhook failed (${response.status}), retrying in ${RETRY_DELAY_MS}ms...`);
          await new Promise(r => setTimeout(r, RETRY_DELAY_MS));
          continue;
        }
        return { success: false, error: `Webhook returned ${response.status}` };
      }

      // Parse JSON response if available
      let data: any = null;
      try {
        if (respBody && respBody.trim().startsWith("{")) {
          data = JSON.parse(respBody);
        }
      } catch (_) {
        // Not JSON, that's fine
      }

      console.log(`[${callId}] ✅ Webhook sent successfully on attempt ${attempt}`);
      return {
        success: true,
        fare: data?.fare || data?.estimated_fare,
        eta: data?.eta || data?.estimated_eta
      };
    } catch (e) {
      const errMsg = String(e);
      const isTimeout = errMsg.includes("abort") || errMsg.includes("timeout");
      console.error(`[${callId}] ❌ Webhook attempt ${attempt} failed: ${errMsg}`);
      
      if (attempt < MAX_RETRIES) {
        console.log(`[${callId}] 🔄 Retrying webhook in ${RETRY_DELAY_MS}ms...`);
        await new Promise(r => setTimeout(r, RETRY_DELAY_MS));
        continue;
      }
      
      return { success: false, error: isTimeout ? `Webhook timeout after ${Math.round(TIMEOUT_MS / 1000)}s` : errMsg };
    }
  }
  
  return { success: false, error: "All webhook attempts failed" };
}

// Handle WebSocket connection
async function handleConnection(socket: WebSocket, callId: string, callerPhone: string, language: string) {
  console.log(`[${callId}] 🎯 PAIRED MODE: New connection from ${callerPhone} (language: ${language})`);
  
  // Determine effective language:
  // 1. If explicit language passed (not "auto"), use it
  // 2. Otherwise, look up caller's preferred_language from database
  // 3. Fall back to phone-based country code detection
  // 4. Default to "auto" (auto-detect from speech)
  let effectiveLanguage = language;
  let callerName: string | null = null;
  let callerLastPickup: string | null = null;
  let callerLastDestination: string | null = null;
  let callerTotalBookings = 0;
  
  if (callerPhone && callerPhone !== "unknown") {
    try {
      const phoneKey = normalizePhone(callerPhone);
      console.log(`[${callId}] 🔍 Fast caller lookup for: ${phoneKey}`);
      
      const { data: callerData } = await supabase
        .from("callers")
        .select("name, last_pickup, last_destination, total_bookings, preferred_language")
        .eq("phone_number", phoneKey)
        .maybeSingle();
      
      if (callerData) {
        callerName = callerData.name || null;
        callerLastPickup = callerData.last_pickup || null;
        callerLastDestination = callerData.last_destination || null;
        callerTotalBookings = callerData.total_bookings || 0;
        
        // Use preferred_language from DB if available (overrides phone-based detection)
        if (callerData.preferred_language && (language === "auto" || !language)) {
          effectiveLanguage = callerData.preferred_language;
          console.log(`[${callId}] 🌐 Using saved preferred language: ${callerData.preferred_language}`);
        }
        
        console.log(`[${callId}] 👤 Fast lookup found: ${callerName || 'no name'}, ${callerTotalBookings} bookings`);
      }
    } catch (e) {
      console.error(`[${callId}] Failed to lookup caller:`, e);
    }
    
    // If still "auto" and no saved preference, try phone-based detection
    if (effectiveLanguage === "auto" || !effectiveLanguage) {
      const phoneLanguage = detectLanguageFromPhone(callerPhone);
      if (phoneLanguage) {
        effectiveLanguage = phoneLanguage;
        console.log(`[${callId}] 🌐 Detected language from phone: ${phoneLanguage}`);
      } else {
        effectiveLanguage = "auto"; // Fall back to auto-detect
      }
    }
  }
  
  console.log(`[${callId}] 🌐 Effective language: ${effectiveLanguage}`);
  
  const sessionState = createSessionState(callId, callerPhone, effectiveLanguage);
  let openaiWs: WebSocket | null = null;
  let cleanedUp = false;
  let dispatchChannel: ReturnType<typeof supabase.channel> | null = null;

  // Audio format negotiated with the bridge (defaults match typical Asterisk ulaw)
  let inboundAudioFormat: InboundAudioFormat = "ulaw";
  let inboundSampleRate = 8000;
  
  // MEMORY LEAK FIX: Track all active timers for cleanup
  const activeTimers = new Set<ReturnType<typeof setTimeout>>();
  
  // Tracked setTimeout that auto-clears on cleanup
  const trackedTimeout = (fn: () => void, ms: number): ReturnType<typeof setTimeout> => {
    const id = setTimeout(() => {
      activeTimers.delete(id);
      fn();
    }, ms);
    activeTimers.add(id);
    return id;
  };
  
  // Clear a tracked timer early if needed
  const clearTrackedTimeout = (id: ReturnType<typeof setTimeout> | null) => {
    if (id) {
      clearTimeout(id);
      activeTimers.delete(id);
    }
  };

  // Cleanup function
  const cleanup = async () => {
    if (cleanedUp) return;
    cleanedUp = true;
    
    console.log(`[${callId}] 🧹 Cleaning up connection`);
    
    // MEMORY LEAK FIX: Clear ALL tracked timers first
    console.log(`[${callId}] 🧹 Clearing ${activeTimers.size} tracked timers`);
    for (const timerId of activeTimers) {
      clearTimeout(timerId);
    }
    activeTimers.clear();
    
    // Clear greeting timers
    if (greetingFallbackTimer) {
      clearTimeout(greetingFallbackTimer);
      greetingFallbackTimer = null;
    }
    
    // Clear fallback quote timer if active
    if (sessionState.fallbackQuoteTimerId) {
      clearTrackedTimeout(sessionState.fallbackQuoteTimerId);
      sessionState.fallbackQuoteTimerId = null;
    }
    
    // Save preferred_language to callers table
    if (callerPhone && callerPhone !== "unknown" && sessionState.language && sessionState.language !== "auto") {
      const phoneKey = normalizePhone(callerPhone);
      try {
        await supabase.from("callers").upsert({
          phone_number: phoneKey,
          preferred_language: sessionState.language,
          updated_at: new Date().toISOString()
        }, { onConflict: "phone_number" });
        console.log(`[${callId}] 🌐 Saved preferred language: ${sessionState.language}`);
      } catch (e) {
        console.error(`[${callId}] Failed to save preferred language:`, e);
      }
    }
    
    // Unsubscribe AND REMOVE dispatch channel to prevent memory leaks
    // CRITICAL: removeChannel() is required to fully purge the channel from Supabase's
    // internal tracking. Without it, channels accumulate and cause server flooding.
    if (dispatchChannel) {
      try {
        await dispatchChannel.unsubscribe();
        supabase.removeChannel(dispatchChannel);
        console.log(`[${callId}] 🧹 Dispatch channel cleaned up`);
      } catch (e) {
        console.error(`[${callId}] Error cleaning up dispatch channel:`, e);
      }
      dispatchChannel = null;
    }
    
    // Update final call state
    try {
      await supabase
        .from("live_calls")
        .update({
          status: sessionState.bookingConfirmed ? "completed" : "ended",
          ended_at: new Date().toISOString()
        })
        .eq("call_id", callId);
    } catch (e) {
      console.error(`[${callId}] Error updating final state:`, e);
    }
    
    if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
      openaiWs.close();
    }
  };

  // ═══════════════════════════════════════════════════════════════════════════
  // Keep-alive heartbeat to prevent WebSocket idle timeout during long silences
  // (e.g., waiting for user confirmation after fare quote)
  //
  // NOTE: Some network paths/proxies drop idle WebSockets aggressively.
  // A shorter interval helps prevent Ada audio from being cut off mid-session.
  // ═══════════════════════════════════════════════════════════════════════════
  const KEEPALIVE_INTERVAL_MS = 8000; // 8s heartbeat (was 15s)
  let keepaliveInterval: ReturnType<typeof setInterval> | null = null;

  keepaliveInterval = setInterval(() => {
    if (cleanedUp || socket.readyState !== WebSocket.OPEN) {
      if (keepaliveInterval) {
        clearInterval(keepaliveInterval);
        keepaliveInterval = null;
      }
      return;
    }

    try {
      socket.send(
        JSON.stringify({
          type: "keepalive",
          timestamp: Date.now(),
          call_id: callId,
        }),
      );
    } catch (e) {
      console.error(`[${callId}] ⚠️ Keepalive send failed:`, e);
    }
  }, KEEPALIVE_INTERVAL_MS);
  
  // Clear keepalive on cleanup
  const originalCleanup = cleanup;
  const cleanupWithKeepalive = async () => {
    if (keepaliveInterval) {
      clearInterval(keepaliveInterval);
      keepaliveInterval = null;
    }
    await originalCleanup();
  };

  // ═══════════════════════════════════════════════════════════════════════════
  // Subscribe to dispatch callback channel (for fare/ETA responses from backend)
  // ═══════════════════════════════════════════════════════════════════════════
  dispatchChannel = supabase.channel(`dispatch_${callId}`);
  
  dispatchChannel.on("broadcast", { event: "dispatch_ask_confirm" }, async (payload: any) => {
    const { message, fare, eta, eta_minutes, callback_url, booking_ref } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH ask_confirm: fare=${fare}, eta=${eta_minutes || eta}, message="${message}"`);
    
    if (!message || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) {
      console.log(`[${callId}] ⚠️ Cannot process dispatch ask_confirm - OpenAI not connected or cleaned up`);
      return;
    }
    
    // FALLBACK QUOTE PROTECTION: Cancel fallback timer since real quote arrived
    if (sessionState.fallbackQuoteTimerId) {
      clearTrackedTimeout(sessionState.fallbackQuoteTimerId);
      sessionState.fallbackQuoteTimerId = null;
      console.log(`[${callId}] ⏰ Cancelled fallback quote timer - real quote arrived`);
    }
    
    // DUPLICATE PROTECTION: Ignore if we already delivered a quote (real or fallback)
    if (sessionState.quoteDelivered) {
      console.log(`[${callId}] ⚠️ Ignoring dispatch_ask_confirm - quote already delivered`);
      return;
    }
    
    // DUPLICATE PROTECTION: Ignore if we already received a quote and are awaiting confirmation
    if (sessionState.awaitingConfirmation) {
      console.log(`[${callId}] ⚠️ Ignoring duplicate dispatch_ask_confirm - already awaiting confirmation`);
      return;
    }
    
    // DUPLICATE PROTECTION: Ignore if booking is already confirmed
    if (sessionState.bookingConfirmed) {
      console.log(`[${callId}] ⚠️ Ignoring dispatch_ask_confirm - booking already confirmed`);
      return;
    }
    
    // Mark quote as delivered to prevent duplicates
    sessionState.quoteDelivered = true;
    
    // Store the callback URL for when customer confirms
    sessionState.pendingConfirmationCallback = callback_url;
    sessionState.pendingFare = fare;
    sessionState.pendingEta = eta_minutes || eta;
    sessionState.pendingBookingRef = booking_ref || null;
    
    // CLEAR THE SILENCE FLAG - we have the fare now, Ada can speak again!
    sessionState.waitingForQuoteSilence = false;
    sessionState.saidOneMoment = false;
    console.log(`[${callId}] 🔊 Silence mode CLEARED - fare received, Ada can speak`);
    
    // Cancel any active response before injecting dispatch message
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    // CRITICAL: Activate summary protection IMMEDIATELY to prevent barge-in during fare quote
    // This protects Ada's speech from being cut off by background noise or premature user input
    sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
    console.log(`[${callId}] 🛡️ Fare quote protection activated for ${SUMMARY_PROTECTION_MS}ms`);
    
    // Inject the fare/ETA message for Ada to speak - with explicit YES/NO handling instructions
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{
          type: "input_text",
          text: `[DISPATCH QUOTE RECEIVED]: Tell the customer EXACTLY: "${message}". IMPORTANT: do NOT repeat pickup/destination/passengers again. Only say the fare/ETA and ask if they want to proceed.

WHEN CUSTOMER RESPONDS:
- If they say YES / yeah / correct / confirm / go ahead / book it / please → IMMEDIATELY CALL book_taxi with action: "confirmed"
- If they say NO / cancel / too expensive / nevermind → Say "No problem, is there anything else I can help you with?"
- If unclear, ask: "Would you like me to book that for you?"

DO NOT say "booked" or "confirmed" until book_taxi with action: "confirmed" returns success.`
        }]
      }
    }));
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        instructions: `Say EXACTLY this quote: "${message}". Do NOT recap the journey details. Ask if they want to proceed. When they say yes, call book_taxi with action="confirmed". When they say no, ask if there's anything else you can help with.`
      }
    }));
    
    // Track that we're waiting for confirmation
    sessionState.quoteInFlight = false;
    sessionState.awaitingConfirmation = true;
    sessionState.awaitingConfirmationSetAt = Date.now(); // For barge-in cooldown
    sessionState.lastQuestionAsked = "confirmation";
    sessionState.confirmationBargeInCancelled = false;
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_say" }, async (payload: any) => {
    const { message: sayMessage } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH say: "${sayMessage}"`);
    
    if (!sayMessage || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) {
      return;
    }
    
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    // Activate speech protection so Ada doesn't get cut off
    sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
    console.log(`[${callId}] 🛡️ Dispatch say protection activated for ${SUMMARY_PROTECTION_MS}ms`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: `[DISPATCH UPDATE]: Tell the customer: "${sayMessage}"` }]
      }
    }));
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        instructions: `Say this to the customer: "${sayMessage}"`
      }
    }));
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_confirm" }, async (payload: any) => {
    const { message: confirmMessage, booking_ref } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH confirm: ref=${booking_ref}, "${confirmMessage}"`);
    
    if (!confirmMessage || !openaiWs || openaiWs.readyState !== WebSocket.OPEN || cleanedUp) {
      return;
    }
    
    sessionState.bookingConfirmed = true;
    sessionState.bookingRef = booking_ref;
    
    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
    openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
    
    // Activate speech protection for booking confirmation - use longer window for goodbye
    sessionState.summaryProtectionUntil = Date.now() + (SUMMARY_PROTECTION_MS * 1.5);
    console.log(`[${callId}] 🛡️ Booking confirm protection activated for ${SUMMARY_PROTECTION_MS * 1.5}ms`);
    
    openaiWs.send(JSON.stringify({
      type: "conversation.item.create",
      item: {
        type: "message",
        role: "user",
        content: [{ type: "input_text", text: `[BOOKING CONFIRMED]: Tell the customer: "${confirmMessage}" Then say goodbye and end the call.` }]
      }
    }));
    
    openaiWs.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],
        instructions: `The booking is confirmed! Say this to the customer: "${confirmMessage}" Then say goodbye warmly.`
      }
    }));
  });
  
  dispatchChannel.on("broadcast", { event: "dispatch_hangup" }, async (payload: any) => {
    const { message: hangupMessage } = payload.payload || {};
    console.log(`[${callId}] 📥 DISPATCH hangup: "${hangupMessage}"`);
    
    if (hangupMessage && openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
      openaiWs.send(JSON.stringify({ type: "response.cancel" }));
      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

      // Protect this final message from being cut off
      sessionState.summaryProtectionUntil = Date.now() + (SUMMARY_PROTECTION_MS * 2);
      console.log(`[${callId}] 🛡️ Dispatch hangup protection activated for ${SUMMARY_PROTECTION_MS * 2}ms`);
      
      openaiWs.send(JSON.stringify({
        type: "conversation.item.create",
        item: {
          type: "message",
          role: "user",
          content: [{ type: "input_text", text: `[DISPATCH HANGUP]: Say this and end: "${hangupMessage}"` }]
        }
      }));
      
      openaiWs.send(JSON.stringify({
        type: "response.create",
        response: {
          modalities: ["audio", "text"],
          instructions: `Say this to end the call: "${hangupMessage}"`
        }
      }));
    }
    
    // Schedule cleanup after giving time for goodbye
    // MEMORY LEAK FIX: Use tracked timeout
    trackedTimeout(() => cleanupWithKeepalive(), 12000);
  });
  
  // Subscribe to the channel
  dispatchChannel.subscribe((status) => {
    console.log(`[${callId}] 📡 Dispatch channel status: ${status}`);
  });

  // Connect to OpenAI Realtime
  // Note: Deno WebSocket requires headers as second argument array for protocols,
  // but OpenAI needs Authorization header. Use the URL with query param workaround
  // or rely on the proper Deno fetch-based approach.
  try {
    // For Deno, we need to use a different approach - create WebSocket with protocols
    const wsUrl = `${OPENAI_REALTIME_URL}`;
    openaiWs = new WebSocket(wsUrl, ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]);
  } catch (e) {
    console.error(`[${callId}] Failed to connect to OpenAI:`, e);
    socket.close();
    return;
  }

  // OpenAI WebSocket handlers
  // Flag to prevent duplicate greetings
  let greetingSent = false;
  let greetingAudioReceived = false; // Track if we actually got audio for the greeting
  let greetingFallbackTimer: ReturnType<typeof setTimeout> | null = null;
  // Monitoring: throttle DB inserts for audio playback in the LiveCalls panel
  let monitorAiChunkCount = 0;
  
  const sendGreeting = () => {
    if (greetingSent || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) {
      console.log(`[${callId}] ⚠️ sendGreeting skipped: sent=${greetingSent}, wsOpen=${openaiWs?.readyState === WebSocket.OPEN}`);
      return;
    }
    
    greetingSent = true;
    
    console.log(`[${callId}] 🎙️ Sending initial greeting (language: ${sessionState.language})...`);
    
    // Get language-specific greeting or fall back to English for auto-detect
    const langData = GREETINGS[sessionState.language] || GREETINGS["en"];
    // Include version number at the start for identification - MUST be spoken
    const greetingText = `${langData.greeting} ${langData.pickupQuestion}`;
    
    // For auto-detect mode, let the AI decide based on first user response
    // CRITICAL: Version MUST be spoken first, then the greeting
    const greetingInstruction = sessionState.language === "auto"
      ? `FIRST say "Version ${VERSION}" clearly, then say: "${greetingText}". After this greeting, WAIT for the caller to respond. Do NOT repeat the pickup question again.`
      : `FIRST say "Version ${VERSION}" clearly, then say: "${greetingText}". Then WAIT for their response.`;
    
    console.log(`[${callId}] 📢 Version: ${VERSION}, Greeting: ${greetingText}`);
    
    // Simple approach: just request a response with specific instructions
    safeResponseCreate(openaiWs!, sessionState, callId, {
      modalities: ["audio", "text"],  // Audio first - prioritize voice output
      instructions: greetingInstruction
    });
    
    sessionState.lastQuestionAsked = "pickup";
    console.log(`[${callId}] ✅ Greeting sent - will NOT retry`);
  };
  
  // Fallback: if session.updated never arrives, send greeting after 2 seconds
  // MEMORY LEAK FIX: Use tracked timeout and store reference for cleanup
  greetingFallbackTimer = trackedTimeout(() => {
    if (!greetingSent && openaiWs && openaiWs.readyState === WebSocket.OPEN) {
      console.log(`[${callId}] ⏰ Fallback: session.updated not received, sending greeting anyway`);
      // Even if session.updated doesn't arrive, unblock the client UI.
      sendSessionReady();
      sendGreeting();
    }
  }, 2000);

  // Ensure we follow the Realtime protocol correctly:
  // send session.update ONLY AFTER receiving session.created.
  let sessionUpdateSent = false;

  // VoiceTest (browser) expects a "session_ready" message to flip from connecting → connected.
  let sessionReadySent = false;
  const sendSessionReady = () => {
    if (sessionReadySent || cleanedUp) return;
    if (socket.readyState !== WebSocket.OPEN) return;
    sessionReadySent = true;
    try {
      socket.send(JSON.stringify({ type: "session_ready", pipeline: "paired" }));
      console.log(`[${callId}] ✅ session_ready sent to client`);
    } catch (e) {
      console.error(`[${callId}] Failed to send session_ready:`, e);
    }
  };

  const sendSessionUpdate = () => {
    if (sessionUpdateSent || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) return;
    sessionUpdateSent = true;

    // Configure session with context-pairing system
    const systemPrompt = buildSystemPrompt(sessionState.language);

    // Build Whisper prompt - only add English-specific hints if not auto-detect
    const whisperPrompt = sessionState.language === "auto"
      ? "Taxi booking. Addresses, street names, numbers, passenger count."
      : sessionState.language === "en"
        ? "Taxi booking. Street numbers, addresses, passenger count, pickup location, destination. UK addresses."
        : "Taxi booking. Addresses, street names, numbers, passenger count.";

    const sessionConfig = {
      type: "session.update",
      session: {
        modalities: ["text", "audio"],
        voice: VOICE,
        instructions:
          systemPrompt +
          `\n\n[CALL CONTEXT]\nCall ID: ${callId}\nCaller: ${callerPhone}\nLanguage: ${sessionState.language}`,
        input_audio_format: "pcm16",
        output_audio_format: "pcm16",
        input_audio_transcription: {
          model: "whisper-1",
          prompt: whisperPrompt,
        },
        turn_detection: {
          type: "server_vad",
          // Optimized for taxi calls: 1000ms balances snappy responses with road noise tolerance
          threshold: 0.5,
          prefix_padding_ms: 300,
          silence_duration_ms: 1000, // Reduced from 1200ms for faster responses
        },
        tools: TOOLS,
        tool_choice: "auto",
        temperature: 0.6, // OpenAI Realtime API minimum is 0.6
      },
    };

    console.log(`[${callId}] 📤 Sending session.update (after session.created)`);
    openaiWs.send(JSON.stringify(sessionConfig));
  };

  openaiWs.onopen = () => {
    console.log(`[${callId}] ✅ Connected to OpenAI Realtime (waiting for session.created...)`);
  };

  openaiWs.onmessage = async (event) => {
    try {
      const data = JSON.parse(event.data);
      
      switch (data.type) {
        case "session.created":
          console.log(`[${callId}] 📋 Session created`);
          // Now we can safely configure the session.
          sendSessionUpdate();
          break;
          
        case "session.updated":
          // Session config applied - NOW send the greeting (with tiny delay for stability)
          console.log(`[${callId}] ✅ Session configured - triggering greeting in 200ms`);

          // Tell the client it's safe to start recording / showing "connected".
          // Also clear stale audio to reduce phantom Whisper transcriptions.
          try {
            openaiWs?.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
          } catch {
            // ignore
          }
          sendSessionReady();

          // Clear the fallback timer since we received session.updated properly
          if (greetingFallbackTimer) {
            clearTimeout(greetingFallbackTimer);
            greetingFallbackTimer = null;
          }
          // MEMORY LEAK FIX: Use tracked timeout for greeting stabilization
          trackedTimeout(() => sendGreeting(), 200);
          break;
          
        case "response.created":
          // Mark response as active immediately (before any audio)
          sessionState.openAiResponseActive = true;
          
          // SILENCE MODE GUARD: If we're waiting for a quote, cancel any new responses
          if (sessionState.waitingForQuoteSilence && sessionState.saidOneMoment) {
            console.log(`[${callId}] 🤫 BLOCKING new response - in silence mode waiting for fare`);
            openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
            sessionState.openAiResponseActive = false; // We cancelled it
            break;
          }
          console.log(`[${callId}] 🎤 Response started`);
          break;
          
        case "input_audio_buffer.committed":
          // User started speaking - cancel any ongoing AI response for natural interruption
          if (sessionState.openAiResponseActive) {
            console.log(`[${callId}] 🗣️ User interrupted (buffer committed) - cancelling AI response`);
            openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
          }
          break;

        case "error":
          // Suppress expected "response_cancel_not_active" error - this is normal when we send
          // fire-and-forget cancels during dispatch events or silence mode transitions
          const errorCode = data.error?.code;
          if (errorCode === "response_cancel_not_active") {
            // Harmless - we tried to cancel but there was no active response
            console.log(`[${callId}] ℹ️ Cancel skipped (no active response)`);
            break;
          }
          
          console.error(`[${callId}] ❌ OpenAI error:`, JSON.stringify(data));
          // Forward real errors to client so bridge can handle appropriately
          if (socket.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ type: "error", message: data.error?.message || "Unknown OpenAI error" }));
          }
          break;

        case "response.audio.delta":
          // Forward audio to client as BINARY (more efficient than base64 JSON)
          if (data.delta) {
            if (!sessionState.openAiResponseActive) {
              sessionState.openAiSpeechStartedAt = Date.now();
              console.log(`[${callId}] 🔊 First audio chunk received`);
              greetingAudioReceived = true; // Mark that greeting audio was received
            }
            sessionState.openAiResponseActive = true;

            if (socket.readyState === WebSocket.OPEN) {
              // Decode base64 and send as raw binary - 33% smaller, no JSON parsing needed
              try {
                const binaryString = atob(data.delta);
                const bytes = new Uint8Array(binaryString.length);
                for (let i = 0; i < binaryString.length; i++) {
                  bytes[i] = binaryString.charCodeAt(i);
                }
                socket.send(bytes.buffer);
              } catch (e) {
                console.error(`[${callId}] ❌ Failed to decode audio delta:`, e);
              }
            } else {
              console.log(`[${callId}] ⚠️ Socket not open (state: ${socket.readyState}), can't send audio`);
            }
            
            // NON-BLOCKING: Stream AI audio to monitoring panel (fire and forget - no jitter)
            // IMPORTANT: Throttle inserts to avoid slowing the realtime loop.
            monitorAiChunkCount++;
            if (monitorAiChunkCount % 5 === 0) {
              void supabase.from("live_call_audio").insert({
                call_id: callId,
                audio_chunk: data.delta,
                audio_source: "ai",
                created_at: new Date().toISOString(),
              });
            }
          }
          break;

        case "response.audio_transcript.delta":
          // Track what Ada is saying and check for price hallucination
          if (data.delta) {
            if (!sessionState.openAiResponseActive) {
              sessionState.openAiSpeechStartedAt = Date.now();
            }
            sessionState.openAiResponseActive = true;
            
            // PRICE/ETA HALLUCINATION GUARD: If Ada mentions a price/ETA but we haven't received one from dispatch, cancel!
            if (isPriceOrEtaHallucination(data.delta, !!sessionState.pendingFare)) {
              console.log(`[${callId}] 🚫 PRICE/ETA HALLUCINATION DETECTED: "${data.delta}" - cancelling response`);
              openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
              openaiWs!.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // If we're already in silence mode, don't inject another "checking" response - just stay silent
              if (sessionState.waitingForQuoteSilence) {
                console.log(`[${callId}] 🤫 Already in silence mode - NOT injecting correction, staying quiet`);
              } else {
                // Inject a correction - be very forceful
                sessionState.waitingForQuoteSilence = true;
                sessionState.saidOneMoment = true;
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "user",
                    content: [{ type: "input_text", text: "[SYSTEM ERROR]: You made up a price or ETA. You do NOT know the fare or arrival time yet. Say ONLY: 'I'm just checking that for you now' and then STOP TALKING completely. Wait in silence for dispatch." }]
                  }
                }));
                openaiWs!.send(JSON.stringify({
                  type: "response.create",
                  response: { modalities: ["audio", "text"], instructions: "Say ONLY: 'I'm just checking that for you now.' Then STOP. Do not say anything else." }
                }));
              }
            }
          }
          break;

        case "response.audio_transcript.done":
          // Ada finished speaking - record in history and set echo guard
          if (data.transcript) {
            sessionState.conversationHistory.push({
              role: "assistant",
              content: data.transcript,
              timestamp: Date.now()
            });
            
            console.log(`[${callId}] 🤖 Ada: "${data.transcript.substring(0, 80)}..."`);
            
            // SILENCE MODE CHECK: If Ada just said "one moment", block any further responses
            const transcriptLower = data.transcript.toLowerCase();
            if (sessionState.waitingForQuoteSilence && 
                (transcriptLower.includes("one moment") || transcriptLower.includes("checking") || transcriptLower.includes("let me check"))) {
              console.log(`[${callId}] 🤫 Ada said "one moment" - entering STRICT SILENCE MODE until fare arrives`);
              // Cancel any pending response to ensure complete silence
              openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
              break; // Don't process further - just stay silent
            }
            // Detect what question was asked based on content
            const lower = data.transcript.toLowerCase();
            
            // SUMMARY PROTECTION: If Ada is summarizing the booking or quoting a price,
            // activate protection window to prevent interruptions
            const isSummary = lower.includes("let me confirm") || 
                              lower.includes("to confirm") || 
                              lower.includes("so that's") ||
                              lower.includes("summarize") ||
                              lower.includes("your booking") ||
                              lower.includes("one moment") ||
                              lower.includes("checking") ||
                              lower.includes("your price") ||
                              lower.includes("fare is") ||
                              lower.includes("driver will be");
            
            if (isSummary) {
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
              console.log(`[${callId}] 🛡️ Summary protection activated for ${SUMMARY_PROTECTION_MS}ms`);
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // AI-BASED EXTRACTION (replaces buggy regex "Ada First Echo" mode)
            // Uses Gemini to accurately extract booking details from transcripts
            // ═══════════════════════════════════════════════════════════════════
            if (!isSummary && USE_AI_EXTRACTION && sessionState.conversationHistory.length > 0) {
              // Run AI extraction in the background (non-blocking)
              extractBookingWithAI(
                sessionState.conversationHistory,
                sessionState.booking,
                sessionState.callerPhone,
                callId
              ).then(async (aiResult) => {
                if (!aiResult || aiResult.confidence === "low") return;

                // Grounding guard: never let extraction overwrite addresses unless
                // the extracted value is supported by what the user actually said.
                const extractUserSpokenTextForGrounding = (raw: string): string => {
                  const s = String(raw || "");
                  // If we have a [CONTEXT: ...] prefix, keep only the actual user payload after it.
                  const ctx = s.match(/\]\s*(.+)$/);
                  if (ctx?.[1]) return ctx[1].trim();
                  // Strip other leading tags like [NO CHANGES NEEDED]
                  return s.replace(/^\s*\[[^\]]+\]\s*/g, "").trim();
                };

                const normalizeForCompare = (s: string): string =>
                  s
                    .toLowerCase()
                    .replace(/[^a-z0-9\s]/g, " ")
                    .replace(/\s+/g, " ")
                    .trim();

                const tokenise = (s: string): string[] => {
                  const stop = new Set(["the", "a", "an", "to", "at", "from", "in", "on", "of", "and", "is", "its", "it", "my"]);
                  return normalizeForCompare(s)
                    .split(" ")
                    .filter(t => t && !stop.has(t) && (t.length >= 3 || /^\d+[a-z]?$/.test(t)));
                };

                const isGroundedInUserText = (extracted: string, userText: string): boolean => {
                  const e = normalizeForCompare(extracted);
                  const u = normalizeForCompare(userText);
                  if (!e || !u) return false;
                  if (e === u) return true;
                  if (e.includes(u) || u.includes(e)) return true;

                  const eTokens = new Set(tokenise(extracted));
                  const uTokens = tokenise(userText);
                  if (uTokens.length === 0) return false;

                  let overlap = 0;
                  for (const t of uTokens) {
                    if (eTokens.has(t)) overlap++;
                  }
                  // For short landmark-like phrases, require at least 1 shared token.
                  // For longer phrases, require at least 2.
                  return overlap >= (uTokens.length <= 3 ? 1 : 2);
                };

                const lastUserMsg = (() => {
                  for (let i = sessionState.conversationHistory.length - 1; i >= 0; i--) {
                    const m = sessionState.conversationHistory[i];
                    if (m.role === "user") return extractUserSpokenTextForGrounding(String(m.content || ""));
                  }
                  return "";
                })();
                
                let updated = false;
                
                // Update pickup if AI found one and it's different
                if (aiResult.pickup && aiResult.pickup !== sessionState.booking.pickup) {
                  if (isGroundedInUserText(aiResult.pickup, lastUserMsg)) {
                    console.log(`[${callId}] 🧠 AI UPDATE pickup: "${sessionState.booking.pickup}" → "${aiResult.pickup}"`);
                    sessionState.booking.pickup = aiResult.pickup;
                    updated = true;
                  } else {
                    console.log(`[${callId}] 🧱 Ignored AI pickup overwrite (not grounded in user text). user="${lastUserMsg}" ai="${aiResult.pickup}"`);
                  }
                }
                
                // Update destination if AI found one and it's different
                if (aiResult.destination && aiResult.destination !== sessionState.booking.destination) {
                  if (isGroundedInUserText(aiResult.destination, lastUserMsg)) {
                    console.log(`[${callId}] 🧠 AI UPDATE destination: "${sessionState.booking.destination}" → "${aiResult.destination}"`);
                    sessionState.booking.destination = aiResult.destination;
                    updated = true;
                  } else {
                    console.log(`[${callId}] 🧱 Ignored AI destination overwrite (not grounded in user text). user="${lastUserMsg}" ai="${aiResult.destination}"`);
                  }
                }
                
                // Update passengers if AI found them and they're different
                if (aiResult.passengers !== null && aiResult.passengers !== sessionState.booking.passengers) {
                  console.log(`[${callId}] 🧠 AI UPDATE passengers: ${sessionState.booking.passengers} → ${aiResult.passengers}`);
                  sessionState.booking.passengers = aiResult.passengers;
                  updated = true;
                }
                
                if (updated) {
                  await updateLiveCall(sessionState);
                }
              }).catch(err => {
                console.error(`[${callId}] AI extraction background error:`, err);
              });
            }
            
            // AUTO-TRIGGER WEBHOOK: If Ada says "check the price" but the mini model didn't call the tool,
            // automatically trigger the webhook. This works around mini model's weak tool calling.
            const isCheckingPrice = (lower.includes("check") && lower.includes("price")) ||
                                    (lower.includes("one moment") && lower.includes("price")) ||
                                    (lower.includes("checking") && (lower.includes("fare") || lower.includes("price") || lower.includes("trip")));
            
            // FALLBACK: If booking state is still missing, backfill ONLY from user-provided context.
            // Never parse Ada's own summaries (they can be wrong and then "stick" into state).
            if (!sessionState.booking.pickup || !sessionState.booking.destination || sessionState.booking.passengers === null) {
              const findLatestUserValue = (
                field: "pickup" | "destination" | "passengers" | "time"
              ): string | null => {
                for (let i = sessionState.conversationHistory.length - 1; i >= 0; i--) {
                  const m = sessionState.conversationHistory[i];
                  if (m.role !== "user") continue;

                  const c = String(m.content || "");

                  // Correction annotation has highest priority
                  const corr = c.match(/\[CORRECTION: User corrected (pickup|destination) to \"([^\"]+)\"\]/i);
                  if (corr && corr[1].toLowerCase() === field) {
                    return corr[2].trim();
                  }

                  // Context paired user answer
                  const ctx = c.match(/\[CONTEXT: Ada asked about ([^\]]+)\]\s*(.+)$/i);
                  if (ctx) {
                    const ctxField = ctx[1].trim().toLowerCase();
                    const answer = ctx[2].trim();
                    if (ctxField === field) return answer;
                  }
                }

                return null;
              };

              const cleanFieldValue = (s: string): string => {
                return s
                  .replace(
                    /^\s*(?:from|pickup(?: address)? is|pickup is|pickup|destination(?: address)? is|destination is|destination|going to|to|it's|it is|is|at)\b[\s,:-]+/i,
                    ""
                  )
                  .replace(/[.?!,]+$/g, "")
                  .trim();
              };

              if (!sessionState.booking.pickup) {
                const v = findLatestUserValue("pickup");
                if (v) {
                  sessionState.booking.pickup = cleanFieldValue(v);
                  console.log(`[${callId}] 📍 Backfilled pickup from user context: ${sessionState.booking.pickup}`);
                }
              }

              if (!sessionState.booking.destination) {
                const v = findLatestUserValue("destination");
                if (v) {
                  sessionState.booking.destination = cleanFieldValue(v);
                  console.log(`[${callId}] 📍 Backfilled destination from user context: ${sessionState.booking.destination}`);
                }
              }

              if (sessionState.booking.passengers === null) {
                const v = findLatestUserValue("passengers");
                if (v) {
                  // Word-to-number mapping for spoken passenger counts
                  const wordToNum: Record<string, number> = {
                    one: 1, two: 2, three: 3, four: 4, five: 5,
                    six: 6, seven: 7, eight: 8, nine: 9, ten: 10
                  };
                  
                  const lowerV = v.toLowerCase().trim();
                  let parsedCount: number | null = null;
                  
                  // IMPORTANT: Only accept passenger counts that LOOK like just a number.
                  // Reject if transcript looks like an address (contains street/road keywords or is too long).
                  const looksLikeAddress = /\b(street|road|lane|avenue|drive|place|close|way|court|crescent|grove|terrace|gardens|park|square|hill|view|row)\b/i.test(lowerV) ||
                                           lowerV.length > 30;
                  
                  if (!looksLikeAddress) {
                    // First try exact digit-only match (e.g., "4", "4 passengers", "four")
                    // Only match if the entire transcript is essentially just a number
                    const digitOnlyMatch = v.match(/^\s*(\d+)\s*(?:passengers?|people|pax)?\s*$/i);
                    if (digitOnlyMatch) {
                      parsedCount = parseInt(digitOnlyMatch[1], 10);
                    } else {
                      // Try word match (e.g., "three", "three passengers")
                      for (const [word, num] of Object.entries(wordToNum)) {
                        const wordPattern = new RegExp(`^\\s*${word}\\s*(?:passengers?|people|pax)?\\s*$`, "i");
                        if (wordPattern.test(lowerV) || lowerV === word) {
                          parsedCount = num;
                          break;
                        }
                      }
                    }
                  }
                  
                  if (parsedCount !== null && parsedCount > 0 && parsedCount <= 20) {
                    sessionState.booking.passengers = parsedCount;
                    console.log(`[${callId}] 📍 Backfilled passengers from user context: ${sessionState.booking.passengers} (raw: "${v}")`);
                  } else if (v && v.length > 0) {
                    console.log(`[${callId}] ⚠️ Passenger backfill rejected (looks like address or invalid): "${v}"`);
                  }
                }
              }

              if (!sessionState.booking.pickupTime) {
                const v = findLatestUserValue("time");
                if (v) {
                  const lowerTime = v.toLowerCase();
                  if (
                    lowerTime.includes("now") ||
                    lowerTime.includes("asap") ||
                    lowerTime.includes("right away") ||
                    lowerTime.includes("immediately") ||
                    lowerTime.includes("straightaway")
                  ) {
                    sessionState.booking.pickupTime = "ASAP";
                  } else {
                    sessionState.booking.pickupTime = cleanFieldValue(v);
                  }
                  console.log(`[${callId}] 📍 Backfilled time from user context: ${sessionState.booking.pickupTime}`);
                }
              }
            }
            
            const hasRequiredFields = Boolean(
              sessionState.booking.pickup &&
              sessionState.booking.destination &&
              sessionState.booking.passengers !== null &&
              !Number.isNaN(sessionState.booking.passengers)
            );
            
            console.log(`[${callId}] 🔍 Auto-trigger check: isCheckingPrice=${isCheckingPrice}, hasRequiredFields=${hasRequiredFields}, pickup=${sessionState.booking.pickup}, dest=${sessionState.booking.destination}, pax=${sessionState.booking.passengers}`);
            
            const QUOTE_DEDUPE_MS = 15000;
            const recentlyRequestedQuote = sessionState.lastQuoteRequestedAt > 0 && 
                                           (Date.now() - sessionState.lastQuoteRequestedAt) < QUOTE_DEDUPE_MS;
            
            if (isCheckingPrice && hasRequiredFields && !sessionState.quoteInFlight && 
                !sessionState.awaitingConfirmation && !recentlyRequestedQuote && !sessionState.bookingConfirmed) {
              console.log(`[${callId}] 🔄 AUTO-TRIGGER: Ada said she's checking price, sending webhook automatically`);
              sessionState.quoteInFlight = true;
              sessionState.lastQuoteRequestedAt = Date.now();
              
              // Send webhook in background
              (async () => {
                const AUTO_QUOTE_TIMEOUT_MS = 30000;

                try {
                  const result = await sendDispatchWebhook(sessionState, "request_quote", {
                    pickup: sessionState.booking.pickup,
                    destination: sessionState.booking.destination,
                    passengers: sessionState.booking.passengers,
                    pickup_time: sessionState.booking.pickupTime || "ASAP",
                    // (kept for compatibility with some downstream dispatch systems)
                    time: sessionState.booking.pickupTime || "ASAP"
                  });

                  console.log(`[${callId}] 📡 Auto-trigger webhook result:`, result);

                  if (!result.success) {
                    console.error(`[${callId}] ❌ Auto-trigger dispatch webhook failed: ${result.error || "unknown_error"}`);
                    sessionState.quoteInFlight = false;
                    return;
                  }

                  // Most dispatch systems respond asynchronously via taxi-dispatch-callback.
                  // Only inject a quote immediately if we actually got fare/eta in the HTTP response.
                  if (result.fare && result.eta) {
                    // Mark quote as delivered and cancel any fallback timer
                    sessionState.quoteDelivered = true;
                    if (sessionState.fallbackQuoteTimerId) {
                      clearTrackedTimeout(sessionState.fallbackQuoteTimerId);
                      sessionState.fallbackQuoteTimerId = null;
                    }
                    
                    sessionState.pendingFare = result.fare;
                    sessionState.pendingEta = result.eta;
                    sessionState.awaitingConfirmation = true;
                    sessionState.quoteInFlight = false;
                    // Set lastQuestionAsked to confirmation so we know we're waiting for YES/NO
                    sessionState.lastQuestionAsked = "confirmation";

                    if (openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
                      openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                      openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));

                      sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;

                      const quoteMessage = `The trip fare will be ${result.fare}, and the estimated arrival time is ${result.eta}. Would you like to go ahead and book that?`;

                      openaiWs.send(JSON.stringify({
                        type: "conversation.item.create",
                        item: {
                          type: "message",
                          role: "user",
                          content: [{ type: "input_text", text: `[DISPATCH QUOTE RECEIVED]: Say to the customer: "${quoteMessage}". Then WAIT for their YES or NO answer. Do NOT call book_taxi until they explicitly say yes.` }]
                        }
                      }));

                      openaiWs.send(JSON.stringify({
                        type: "response.create",
                        response: { modalities: ["audio", "text"], instructions: `Say exactly: "${quoteMessage}" - then STOP and WAIT for user response.` }
                      }));
                    }
                  } else {
                    console.log(`[${callId}] ⏳ Quote requested (auto-trigger). Waiting for dispatch callback...`);

                    // ═══════════════════════════════════════════════════════════════════════════
                    // FALLBACK QUOTE TIMER (4 seconds)
                    // If dispatch doesn't respond within FALLBACK_QUOTE_TIMEOUT_MS, inject a mock
                    // quote so Ada can keep the conversation moving. This prevents long silences.
                    // ═══════════════════════════════════════════════════════════════════════════
                    sessionState.fallbackQuoteTimerId = trackedTimeout(() => {
                      if (cleanedUp) return;
                      
                      // Skip if we already got a real quote or delivered a fallback
                      if (sessionState.quoteDelivered || sessionState.awaitingConfirmation || sessionState.bookingConfirmed) {
                        console.log(`[${callId}] ⏰ Fallback timer fired but quote already delivered - skipping`);
                        return;
                      }
                      
                      console.log(`[${callId}] ⏰ FALLBACK QUOTE: Dispatch didn't respond in ${FALLBACK_QUOTE_TIMEOUT_MS}ms, using mock quote`);
                      
                      // Mark quote as delivered to prevent duplicates
                      sessionState.quoteDelivered = true;
                      sessionState.pendingFare = FALLBACK_QUOTE_FARE;
                      sessionState.pendingEta = FALLBACK_QUOTE_ETA;
                      sessionState.awaitingConfirmation = true;
                      sessionState.quoteInFlight = false;
                      sessionState.lastQuestionAsked = "confirmation";
                      sessionState.awaitingConfirmationSetAt = Date.now();
                      
                      // Clear silence mode so Ada can speak
                      sessionState.waitingForQuoteSilence = false;
                      sessionState.saidOneMoment = false;
                      
                      if (openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
                        openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                        openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                        
                        sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
                        
                        const fallbackMessage = `The trip fare will be ${FALLBACK_QUOTE_FARE}, and the estimated arrival time is ${FALLBACK_QUOTE_ETA}. Would you like to go ahead and book that?`;
                        
                        openaiWs.send(JSON.stringify({
                          type: "conversation.item.create",
                          item: {
                            type: "message",
                            role: "user",
                            content: [{ type: "input_text", text: `[DISPATCH QUOTE RECEIVED]: Say to the customer: "${fallbackMessage}". Then WAIT for their YES or NO answer. Do NOT call book_taxi until they explicitly say yes.

WHEN CUSTOMER RESPONDS:
- If they say YES / yeah / correct / confirm / go ahead / book it / please → IMMEDIATELY CALL book_taxi with action: "confirmed"
- If they say NO / cancel / too expensive / nevermind → Say "No problem, is there anything else I can help you with?"
- If unclear, ask: "Would you like me to book that for you?"

DO NOT say "booked" or "confirmed" until book_taxi with action: "confirmed" returns success.` }]
                          }
                        }));
                        
                        openaiWs.send(JSON.stringify({
                          type: "response.create",
                          response: { modalities: ["audio", "text"], instructions: `Say exactly: "${fallbackMessage}" - then STOP and WAIT for user response.` }
                        }));
                        
                        console.log(`[${callId}] 🎤 Fallback quote injected - Ada should speak now`);
                      }
                    }, FALLBACK_QUOTE_TIMEOUT_MS);
                    
                    console.log(`[${callId}] ⏰ Fallback quote timer started (${FALLBACK_QUOTE_TIMEOUT_MS}ms)`);

                    // Safety: if callback never arrives AND fallback already delivered, allow a retry later.
                    // MEMORY LEAK FIX: Use tracked timeout
                    trackedTimeout(() => {
                      if (cleanedUp) return;
                      if (sessionState.quoteInFlight && !sessionState.awaitingConfirmation && !sessionState.pendingFare) {
                        console.log(`[${callId}] ⏰ Auto-trigger quote timeout (${AUTO_QUOTE_TIMEOUT_MS}ms) - clearing quoteInFlight to allow retry`);
                        sessionState.quoteInFlight = false;
                      }
                    }, AUTO_QUOTE_TIMEOUT_MS);
                  }
                } catch (e) {
                  console.error(`[${callId}] Auto-trigger webhook error:`, e);
                  sessionState.quoteInFlight = false;
                }
              })();
            }
            
            // Detect what question Ada asked - look for QUESTION PATTERNS, not just keywords
            // This ensures we track the actual question, not just mentions of words
            const questionPatterns = {
              pickup: [
                /where would you like to be picked up/i,
                /where are you\?/i,
                /pickup location/i,
                /where shall I pick you up/i,
                /what.s your pickup/i
              ],
              destination: [
                /what is your destination/i,
                /where would you like to go/i,
                /where are you going/i,
                /where to\?/i,
                /what.s your destination/i,
                /and your destination/i
              ],
              passengers: [
                /how many people/i,
                /how many passengers/i,
                /how many will be travelling/i,
                /number of passengers/i
              ],
              time: [
                /when do you need/i,
                /what time/i,
                /when would you like/i,
                /now or later/i
              ],
              confirmation: [
                /would you like to go ahead/i,
                /like me to book/i,
                /shall I book/i,
                /confirm this/i,
                /is that correct/i
              ]
            };
            
            // Find the LAST question asked in the transcript
            let lastMatchedQuestion: SessionState["lastQuestionAsked"] = sessionState.lastQuestionAsked;
            let lastMatchIndex = -1;
            
            for (const [questionType, patterns] of Object.entries(questionPatterns)) {
              for (const pattern of patterns) {
                const match = lower.match(pattern);
                if (match && match.index !== undefined && match.index > lastMatchIndex) {
                  lastMatchIndex = match.index;
                  lastMatchedQuestion = questionType as SessionState["lastQuestionAsked"];
                }
              }
            }
            
            if (lastMatchIndex >= 0 && lastMatchedQuestion !== sessionState.lastQuestionAsked) {
              sessionState.lastQuestionAsked = lastMatchedQuestion;
              if (lastMatchedQuestion === "confirmation") {
                console.log(`[${callId}] 🎯 Confirmation question detected - waiting for YES/NO`);
              }
            }
            
            console.log(`[${callId}] 📝 Context: lastQuestionAsked = ${sessionState.lastQuestionAsked}`);
            await updateLiveCall(sessionState);
          }
          sessionState.openAiResponseActive = false;
          sessionState.openAiSpeechStartedAt = 0;
          // Set echo guard to block echo from speaker
          sessionState.lastAdaFinishedSpeakingAt = Date.now();
          sessionState.echoGuardUntil = Date.now() + ECHO_GUARD_MS;
          
          // CLEAR GREETING PROTECTION when Ada finishes speaking
          // This ensures user can respond immediately after the greeting ends
          if (sessionState.greetingProtectionUntil > 0) {
            console.log(`[${callId}] ✅ Greeting complete - clearing protection, user can now speak`);
            sessionState.greetingProtectionUntil = 0;
          }

          // If we queued a post-confirmation goodbye response (because OpenAI was still mid-response), send it now.
          if (
            sessionState.pendingPostConfirmResponse &&
            sessionState.bookingConfirmed &&
            openaiWs &&
            openaiWs.readyState === WebSocket.OPEN &&
            !cleanedUp
          ) {
            const queued = sessionState.pendingPostConfirmResponse;
            sessionState.pendingPostConfirmResponse = undefined;
            console.log(`[${callId}] 🚀 Flushing queued post-confirmation goodbye response`);
            openaiWs.send(JSON.stringify({ type: "response.create", response: queued }));
          }
          break;

        case "input_audio_buffer.speech_started":
          // RACE CONDITION FIX: Snapshot the current question type when user STARTS speaking
          // This ensures their answer is mapped to the question that was active when they began,
          // not the next question Ada may have already moved to by the time the transcript arrives
          sessionState.questionTypeAtSpeechStart = sessionState.lastQuestionAsked;
          console.log(`[${callId}] 🎤 User started speaking (snapshotted question: ${sessionState.questionTypeAtSpeechStart})`);
          break;

        case "conversation.item.input_audio_transcription.completed":
          // User finished speaking - this is the KEY context pairing moment
          if (data.transcript) {
            const rawText = data.transcript.trim();
            // Apply alphanumeric corrections for house numbers (e.g., "52 A" → "52A")
            const alphanumericCorrected = applyAlphanumericCorrections(rawText);
            // Apply STT corrections for common telephony mishearings
            const sttCorrected = correctTranscript(alphanumericCorrected);
            // Apply deduplication to fix STT echo issues (e.g., "52A. 52A David Road" → "52A David Road")
            const userText = deduplicateAddress(sttCorrected);
            if (userText !== rawText) {
              console.log(`[${callId}] 🔧 STT corrected: "${rawText}" → "${userText}"`);
            }
            
            // Filter out phantom hallucinations from Whisper
            if (isPhantomHallucination(userText)) {
              console.log(`[${callId}] 👻 Filtered phantom hallucination: "${userText}"`);
              break;
            }
            
            // RACE CONDITION FIX: Use the snapshotted question type instead of current state
            // This handles cases where Ada has already asked the next question before user's answer arrived
            const effectiveQuestionType = sessionState.questionTypeAtSpeechStart || sessionState.lastQuestionAsked;
            console.log(`[${callId}] 👤 User (effective question: "${effectiveQuestionType}", current: "${sessionState.lastQuestionAsked}"): "${userText}"`);
            
            // Clear the snapshot after using it
            sessionState.questionTypeAtSpeechStart = null;
            
            // === USER TRUTH CAPTURE (from simple) ===
            // Capture raw corrected STT output BEFORE any AI processing
            // This provides "ground truth" that overrides AI-extracted values
            // CRITICAL: Also update booking state AND persist immediately to prevent data loss
            
            // FILTER: Reject Ada's own voice being captured as user speech
            if (isLikelyAdaEcho(userText)) {
              console.log(`[${callId}] 🚫 REJECTED Ada echo as user input: "${userText.substring(0, 50)}..."`);
              break; // Don't store garbage as user truth
            }
            
            let userTruthUpdated = false;
            if (effectiveQuestionType === "pickup") {
              sessionState.userTruth.pickup = userText;
              sessionState.booking.pickup = userText; // Sync to booking
              console.log(`[${callId}] 📌 User Truth: pickup = "${userText}"`);
              userTruthUpdated = true;
            } else if (effectiveQuestionType === "destination") {
              sessionState.userTruth.destination = userText;
              sessionState.booking.destination = userText; // Sync to booking
              console.log(`[${callId}] 📌 User Truth: destination = "${userText}"`);
              userTruthUpdated = true;
            } else if (effectiveQuestionType === "passengers") {
              const pax = parsePassengersFromText(userText);
              if (pax > 0) {
                sessionState.userTruth.passengers = pax;
                sessionState.booking.passengers = pax; // Sync to booking
                console.log(`[${callId}] 📌 User Truth: passengers = ${pax}`);
                userTruthUpdated = true;
              }
            } else if (effectiveQuestionType === "time") {
              // GUARD: Check if user gave an ADDRESS instead of a time
              // This happens when they misheard or are correcting a previous answer
              const looksLikeAddress = /\b(road|street|avenue|lane|drive|way|close|court|place|crescent|terrace|airport|station|hotel|hospital|\d+[a-zA-Z]?\s+\w)/i.test(userText);
              const looksLikeTime = /\b(now|asap|soon|minute|hour|today|tomorrow|morning|afternoon|evening|night|pm|am|\d{1,2}[:.]\d{2}|\d{1,2}\s*(o'?clock|pm|am)?)\b/i.test(userText.toLowerCase());
              
              if (looksLikeAddress && !looksLikeTime) {
                // User gave an address when asked about time - this is likely a correction
                console.log(`[${callId}] ⚠️ TIME MISMATCH: User said "${userText}" which looks like an address, not a time. Treating as correction.`);
                // DON'T store as time - let the correction detection handle it
              } else {
                // Normal time response
                sessionState.userTruth.time = userText;
                sessionState.booking.pickupTime = userText; // Sync to booking
                console.log(`[${callId}] 📌 User Truth: time = "${userText}"`);
                userTruthUpdated = true;
              }
            }
            
            // IMMEDIATELY persist User Truth to database to prevent data loss
            if (userTruthUpdated) {
              await updateLiveCall(sessionState);
              console.log(`[${callId}] 💾 User Truth persisted immediately`);
            }
            
            // === SUMMARY PHASE CONFIRMATION DETECTION (from simple) ===
            // Enhanced detection that forces system message injection when user confirms
            if (effectiveQuestionType === "confirmation" || sessionState.awaitingConfirmation || sessionState.lastQuestionAsked === "confirmation") {
              const lower = userText.toLowerCase();
              
              // Lenient affirmative detection (handles typos like "yesy", "okkk")
              const looksLikeYes = /^(y+e+s+|y+e+a+h*|y+u+p+|y+e+p+|sure|correct|right|absolutely|definitely|perfect)/i.test(lower.trim());
              const looksLikeOk = /^(o+k+a*y*|go\s*ahead|book\s*(it|the|taxi)?|please|confirm)/i.test(lower.trim());
              const isAffirmative = looksLikeYes || looksLikeOk || lower.includes("that's correct") || lower.includes("sounds good");
              
              // Check for negation
              const looksLikeNo = /^(no+|nope|wrong|incorrect|change|actually|wait)/i.test(lower.trim());
              
              if (isAffirmative && !looksLikeNo) {
                console.log(`[${callId}] ✅ FORCED CONFIRMATION detected: "${userText}"`);
                
                // Use User Truth values if available, fall back to booking state
                const pickup = sessionState.userTruth.pickup || sessionState.booking.pickup || "";
                const destination = sessionState.userTruth.destination || sessionState.booking.destination || "";
                const passengers = sessionState.userTruth.passengers || sessionState.booking.passengers || 1;
                const time = sessionState.userTruth.time || sessionState.booking.pickupTime || "ASAP";
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[BOOKING CONFIRMATION] ${userText}`,
                  timestamp: Date.now()
                });
                
                // Inject FORCED system message to call book_taxi
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[USER CONFIRMED BOOKING] The user said "${userText}" which is a clear YES.

🚨 IMMEDIATELY call book_taxi with action="${sessionState.awaitingConfirmation ? "confirmed" : "request_quote"}" to ${sessionState.awaitingConfirmation ? "complete the booking" : "get the fare"}.
Use these EXACT values from User Truth:
- pickup: "${pickup}"
- destination: "${destination}"
- passengers: ${passengers}
- pickup_time: "${time}"

${sessionState.awaitingConfirmation ? 
  "Say 'Perfect, booking your taxi now' then call book_taxi({ action: 'confirmed' })" : 
  "Say 'One moment please while I get your fare' then call book_taxi({ action: 'request_quote' })"}`
                    }]
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break; // Don't process further
              }
              
              if (looksLikeNo && !isAffirmative) {
                console.log(`[${callId}] ❌ Negation detected: "${userText}"`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: "[USER WANTS CHANGES] The user said no to the summary. Ask them: 'What would you like to change?' Do NOT repeat the full summary."
                    }]
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
            }
            
            // POST-CONFIRMATION GUARD: After booking is confirmed, enter open conversation mode
            // Only respond to specific requests, otherwise let Ada say goodbye
            if (sessionState.bookingConfirmed) {
              const lower = userText.toLowerCase();
              const isNewBookingRequest = lower.includes("new booking") || lower.includes("another taxi") || 
                                          lower.includes("book another") || lower.includes("different pickup");
              const isCancellation = lower.includes("cancel");
              
              if (isNewBookingRequest) {
                // Allow new booking - this will need a reset flow
                console.log(`[${callId}] 🔄 Post-confirmation: new booking request detected`);
              } else if (isCancellation) {
                console.log(`[${callId}] ❌ Post-confirmation: cancellation request detected`);
              } else {
                // Block looping back to booking questions - inject open conversation context
                console.log(`[${callId}] 🛡️ Post-confirmation guard: "${userText}" - sending open conversation response`);
                
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[POST-CONFIRMATION REMINDER] The booking is ALREADY CONFIRMED. User said: "${userText}". 
Do NOT ask "shall I book that taxi?" - it's ALREADY DONE.
Do NOT ask for pickup/destination/passengers - we HAVE all that.
If this sounds like they want something else, ask "Is there anything else I can help with?"
Otherwise, say goodbye warmly and call end_call().`
                    }]
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break; // Don't process further
              }
            }
            
            // DETECT ADDRESS CORRECTIONS (e.g., "It's 52A David Road", "No, it should be...")
            const correction = detectAddressCorrection(
              userText, 
              sessionState.booking.pickup, 
              sessionState.booking.destination
            );
            
            if (correction.type && correction.address) {
              const oldValue = correction.type === "pickup" 
                ? sessionState.booking.pickup 
                : sessionState.booking.destination;
              
              console.log(`[${callId}] 🔄 ADDRESS CORRECTION DETECTED: ${correction.type} "${oldValue}" → "${correction.address}"`);
              
              // Update the booking state immediately
              if (correction.type === "pickup") {
                sessionState.booking.pickup = correction.address;
              } else {
                sessionState.booking.destination = correction.address;
              }
              
              // RESET CONFIRMATION FLOW: User made a change, so we need to re-confirm before getting fare
              sessionState.preSummaryDone = false;
              sessionState.lastQuestionAsked = "pre_summary"; // Go back to pre-summary
              sessionState.awaitingConfirmation = false;
              sessionState.quoteInFlight = false;
              console.log(`[${callId}] 🔄 Resetting confirmation flow after correction`);
              
              // Add to history with correction annotation
              sessionState.conversationHistory.push({
                role: "user",
                content: `[CORRECTION: User corrected ${correction.type} to "${correction.address}"] ${userText}`,
                timestamp: Date.now()
              });
              
              // Tell OpenAI about the correction so Ada acknowledges it and re-confirms
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[ADDRESS CORRECTION] The user just corrected their ${correction.type} address to: "${correction.address}". 
                    
IMPORTANT: Update your understanding. The ${correction.type} is now "${correction.address}" (not the previous value).
Acknowledge the change briefly, then give an updated summary and ask: "Is there anything else you'd like to change?"
Current state: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=${sessionState.booking.passengers ?? "empty"}, time=${sessionState.booking.pickupTime || "empty"}`
                  }]
                }
              }));
              safeResponseCreate(openaiWs!, sessionState, callId);
              
              await updateLiveCall(sessionState);
              break; // Correction handled, don't run normal context pairing
            }

            // ADDRESS CORRECTION CLARIFICATION: User previously provided an address during pre-summary,
            // and we asked: "pickup or destination?". Handle the user's answer here.
            if (sessionState.lastQuestionAsked === "address_correction_clarify" && sessionState.pendingAddressCorrection) {
              const pendingAddress = sessionState.pendingAddressCorrection;
              const lowerText = userText.toLowerCase().trim();
              const isPickupMention = /\b(pickup|pick up|from|collect|start)\b/i.test(lowerText);
              const isDestMention = /\b(destination|to|going to|drop|end|finish)\b/i.test(lowerText);
              const looksLikeAddress = /\b(road|street|avenue|lane|drive|way|close|court|place|crescent|terrace|airport|station|hotel|hospital|\d+[a-zA-Z]?\s+\w)/i.test(userText);

              console.log(`[${callId}] 🧭 ADDRESS CORRECTION CLARIFY: pending="${pendingAddress}", reply="${userText}" pickup=${isPickupMention} dest=${isDestMention} address=${looksLikeAddress}`);

              // If user says another address instead of clarifying, treat that as the new pending correction address.
              if (looksLikeAddress && !isPickupMention && !isDestMention) {
                sessionState.pendingAddressCorrection = userText;

                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[ADDRESS CORRECTION STILL UNCLEAR] User gave another address: "${userText}".

Ask clearly: "Do you want me to change the pickup or the destination to ${userText}?"`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                updateLiveCall(sessionState).catch(() => {});
                break;
              }

              if (isPickupMention && !isDestMention) {
                sessionState.booking.pickup = pendingAddress;
                sessionState.userTruth.pickup = pendingAddress;
                sessionState.pendingAddressCorrection = null;

                // Reset confirmation flow
                sessionState.preSummaryDone = false;
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                sessionState.lastQuestionAsked = "pre_summary";

                const instruction = getNextStepInstruction("pre_summary", sessionState.booking, sessionState.userTruth);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[PICKUP UPDATED] Pickup changed to "${pendingAddress}". Now re-confirm the journey details.

${instruction}`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                updateLiveCall(sessionState).catch(() => {});
                break;
              }

              if (isDestMention && !isPickupMention) {
                sessionState.booking.destination = pendingAddress;
                sessionState.userTruth.destination = pendingAddress;
                sessionState.pendingAddressCorrection = null;

                // Reset confirmation flow
                sessionState.preSummaryDone = false;
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                sessionState.lastQuestionAsked = "pre_summary";

                const instruction = getNextStepInstruction("pre_summary", sessionState.booking, sessionState.userTruth);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[DESTINATION UPDATED] Destination changed to "${pendingAddress}". Now re-confirm the journey details.

${instruction}`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                updateLiveCall(sessionState).catch(() => {});
                break;
              }

              // Still unclear: ask again
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[CLARIFICATION NEEDED] The user replied "${userText}", but I still need to know whether to change pickup or destination.

Ask: "Should I change the pickup or the destination to ${pendingAddress}?"`
                  }]
                }
              }));
              safeResponseCreate(openaiWs!, sessionState, callId);
              updateLiveCall(sessionState).catch(() => {});
              break;
            }
            
            // PRE-SUMMARY PHASE: Handle user response to "Is there anything you'd like to change?"
            if (sessionState.lastQuestionAsked === "pre_summary" && !sessionState.preSummaryDone) {
              const lowerText = userText.toLowerCase().trim();
              const normalized = lowerText.replace(/[^a-z\s]/g, " ").replace(/\s+/g, " ").trim();
              
              // FIRST: Check if this is garbage/echo text that should be ignored
              if (isLikelyAdaEcho(userText)) {
                console.log(`[${callId}] 🔊 PRE-SUMMARY: Ignoring garbage/echo: "${userText}"`);
                // Just ignore - don't respond or change state
                break;
              }
              
              // Check for "no changes" responses (meaning they're happy with booking)
              // NOTE: We normalize punctuation away (e.g. "that's" -> "that s"), so include variants.
              const noChangesPatterns = /^(no|nope|nah|no thanks|thats fine|that s fine|thats good|that s good|all good|looks good|sounds good|perfect|great|correct|right|yes|yeah|yep|yup|ok|okay|fine|good)\b/i;
              const wantsNoChanges = noChangesPatterns.test(normalized);
              
              // Check for explicit change requests
              const wantsChanges = /change|amend|actually|wait|hold on|wrong|incorrect/i.test(lowerText);
              const explicitFieldMention = /\b(pickup|pick up|destination|going to|drop off|from|to)\b/i.test(userText);
              // NOTE: landmarks like "Cozy Club" may not match this regex, so we also use explicitFieldMention above.
              // Also check for explicit house numbers (like "52A David Road") as addresses
              const looksLikeAddress = /\b(road|street|avenue|lane|drive|way|close|court|place|crescent|terrace|airport|station|hotel|hospital|\d+[a-zA-Z]?\s+\w)/i.test(userText);
              const hasExplicitHouseNumber = /^\d+[a-zA-Z]?\s+/i.test(userText.trim());
              
              console.log(`[${callId}] 🔄 PRE-SUMMARY CHECK: "${userText}" → noChanges=${wantsNoChanges}, wantsChanges=${wantsChanges}, explicitField=${explicitFieldMention}, address=${looksLikeAddress}, houseNumber=${hasExplicitHouseNumber}`);
              
              if (wantsNoChanges && !wantsChanges && !looksLikeAddress) {
                // User confirmed no changes - NOW ask if they want a quote
                console.log(`[${callId}] ✅ PRE-SUMMARY: No changes needed - asking for quote confirmation`);
                sessionState.preSummaryDone = true;
                sessionState.lastQuestionAsked = "confirmation"; // Move to confirmation step
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[NO CHANGES NEEDED] ${userText}`,
                  timestamp: Date.now()
                });
                
                // Ask user if they want to proceed with getting a quote - DON'T auto-request yet
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[USER CONFIRMED NO CHANGES] The user said "${userText}" - they're happy with the details.

Now ask them: "Great! Shall I get you a price for this journey?"
Wait for their confirmation before calling book_taxi(request_quote).
Do NOT request the quote yet - wait for them to say yes.`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }
              
              // Skip processing if this is garbage text
              const isGarbage = isLikelyAdaEcho(userText);
              const hasRealAddress = (looksLikeAddress || hasExplicitHouseNumber) && !isGarbage;
              
              if ((wantsChanges || hasRealAddress || explicitFieldMention) && !isGarbage) {
                // User wants to change something - reset confirmation flow
                console.log(`[${callId}] 🔄 PRE-SUMMARY: User wants to change something - resetting flow`);
                sessionState.preSummaryDone = false;
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                
                // FIX: We need to actually process the address change, not just reset flags
                // Determine if this is a pickup or destination correction based on context
                if (hasRealAddress || explicitFieldMention) {
                  const currentPickup = sessionState.booking.pickup?.toLowerCase() || "";
                  const currentDest = sessionState.booking.destination?.toLowerCase() || "";
                  const newAddressLower = userText.toLowerCase();
                  
                  // Check if user explicitly mentions which field they're changing
                  const isPickupMention = /\b(pickup|pick up|from|collect|start)\b/i.test(userText);
                  const isDestMention = /\b(destination|to|going to|drop|end|finish)\b/i.test(userText);
                  
                  // Store in userTruth for verbatim playback
                  sessionState.userTruth = sessionState.userTruth || {};
                  
                  console.log(`[${callId}] 🏠 PRE-SUMMARY ADDRESS CORRECTION: "${userText}" isPickup=${isPickupMention}, isDest=${isDestMention}`);
                  
                  // If they mention a specific field, update that one
                  // Otherwise, ask them which one they want to change
                  sessionState.conversationHistory.push({
                    role: "user",
                    content: `[CONTEXT: User provided address "${userText}" during pre-summary to make a correction]`,
                    timestamp: Date.now()
                  });
                  
                  if (isPickupMention && !isDestMention) {
                    // Extract just the address part
                    const cleanAddress = userText.replace(/\b(pickup|pick up|from|collect|start)\s*(is|to|at|:)?\s*/i, "").trim();
                    sessionState.booking.pickup = cleanAddress || userText;
                    sessionState.userTruth.pickup = cleanAddress || userText;
                    console.log(`[${callId}] ✏️ PICKUP UPDATED to: "${sessionState.booking.pickup}"`);
                    
                    // Reset to pre_summary to re-confirm
                    sessionState.lastQuestionAsked = "pre_summary";
                    
                    openaiWs!.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "system",
                        content: [{
                          type: "input_text",
                          text: `[PICKUP CORRECTED] User changed pickup to: "${sessionState.booking.pickup}"
                          
Updated booking: pickup="${sessionState.booking.pickup}", destination="${sessionState.booking.destination}", passengers=${sessionState.booking.passengers}, time=${sessionState.booking.pickupTime || "now"}

Acknowledge the change and read back the UPDATED summary:
"So that's from ${sessionState.booking.pickup} to ${sessionState.booking.destination}, ${sessionState.booking.passengers} passengers, ${sessionState.booking.pickupTime || "as soon as possible"}. Is there anything else you'd like to change?"`
                        }]
                      }
                    }));
                    safeResponseCreate(openaiWs!, sessionState, callId);
                    // Persist the update
                    updateLiveCall(sessionState).catch(() => {});
                    break;
                  } else if (isDestMention && !isPickupMention) {
                    const cleanAddress = userText.replace(/\b(destination|to|going to|drop|end|finish)\s*(is|at|:)?\s*/i, "").trim();
                    sessionState.booking.destination = cleanAddress || userText;
                    sessionState.userTruth.destination = cleanAddress || userText;
                    console.log(`[${callId}] ✏️ DESTINATION UPDATED to: "${sessionState.booking.destination}"`);
                    
                    sessionState.lastQuestionAsked = "pre_summary";
                    
                    openaiWs!.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "system",
                        content: [{
                          type: "input_text",
                          text: `[DESTINATION CORRECTED] User changed destination to: "${sessionState.booking.destination}"
                          
Updated booking: pickup="${sessionState.booking.pickup}", destination="${sessionState.booking.destination}", passengers=${sessionState.booking.passengers}, time=${sessionState.booking.pickupTime || "now"}

Acknowledge the change and read back the UPDATED summary:
"So that's from ${sessionState.booking.pickup} to ${sessionState.booking.destination}, ${sessionState.booking.passengers} passengers, ${sessionState.booking.pickupTime || "as soon as possible"}. Is there anything else you'd like to change?"`
                        }]
                      }
                    }));
                    safeResponseCreate(openaiWs!, sessionState, callId);
                    updateLiveCall(sessionState).catch(() => {});
                    break;
                  } else {
                    // Not clear which field - ask the user
                    console.log(`[${callId}] ❓ UNCLEAR WHICH FIELD: Asking user to clarify pickup or destination`);

                    // Transition to an explicit clarify step so the next user response is interpreted correctly.
                    sessionState.pendingAddressCorrection = userText;
                    sessionState.lastQuestionAsked = "address_correction_clarify";
                    
                    openaiWs!.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "system",
                        content: [{
                          type: "input_text",
                          text: `[ADDRESS CORRECTION UNCLEAR] User said "${userText}" but it's unclear if they want to change pickup or destination.

Current booking: pickup="${sessionState.booking.pickup}", destination="${sessionState.booking.destination}"

Ask them: "Would you like me to change the pickup or the destination to ${userText}?"

IMPORTANT: Wait for their answer. They can reply with "pickup" or "destination".

Do NOT guess - explicitly ask which field they want to update.`
                        }]
                      }
                    }));
                    safeResponseCreate(openaiWs!, sessionState, callId);
                    break;
                  }
                }
                
                // If wantsChanges but not an address, let Ada ask what they want to change
                if (wantsChanges && !looksLikeAddress) {
                  openaiWs!.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "system",
                      content: [{
                        type: "input_text",
                        text: `[USER WANTS CHANGES] The user indicated they want to change something by saying: "${userText}"

Ask them: "Of course, what would you like to change - the pickup, destination, number of passengers, or the time?"

Current booking: pickup="${sessionState.booking.pickup}", destination="${sessionState.booking.destination}", passengers=${sessionState.booking.passengers}, time=${sessionState.booking.pickupTime || "as soon as possible"}`
                      }]
                    }
                  }));
                  safeResponseCreate(openaiWs!, sessionState, callId);
                  break;
                }
              }
            }
            
            // QUOTE REQUEST PHASE: Handle user response to "Shall I get you a price?"
            // This is BEFORE the quote is requested - user must say "yes" to trigger quote
            if (sessionState.lastQuestionAsked === "confirmation" && sessionState.preSummaryDone && !sessionState.awaitingConfirmation && !sessionState.quoteInFlight) {
              const lowerText = userText.toLowerCase().trim();
              const normalized = lowerText.replace(/[^a-z\s]/g, " ").replace(/\s+/g, " ").trim();
              
              const affirmativePatterns = /^(yes|yeah|yep|yup|sure|ok|okay|go ahead|please|that's fine|perfect|great|do it|yes please|absolutely|definitely)\b/i;
              const isAffirmative = affirmativePatterns.test(normalized);
              
              const negativePatterns = /^(no|nope|nah|cancel|nevermind|never mind|forget it)\b/i;
              const isNegative = negativePatterns.test(normalized);
              
              console.log(`[${callId}] 💰 QUOTE REQUEST CHECK: "${userText}" → affirmative=${isAffirmative}, negative=${isNegative}`);
              
              if (isAffirmative) {
                // User said YES to getting a quote - NOW request the fare
                console.log(`[${callId}] ✅ USER AGREED TO GET QUOTE - calling request_quote`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[AGREED TO QUOTE] ${userText}`,
                  timestamp: Date.now()
                });
                
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[USER AGREED TO GET QUOTE] The user said "${userText}" - they want a price.

Now call book_taxi with action='request_quote' to get the fare estimate.
Say "One moment while I check that for you" then call the tool.
Booking: pickup=${sessionState.booking.pickup}, destination=${sessionState.booking.destination}, passengers=${sessionState.booking.passengers}, time=${sessionState.booking.pickupTime || "now"}`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }
              
              if (isNegative) {
                // User said NO to getting quote
                console.log(`[${callId}] ❌ USER DECLINED QUOTE with: "${userText}"`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[DECLINED QUOTE] ${userText}`,
                  timestamp: Date.now()
                });
                
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[USER DECLINED QUOTE] The user said "${userText}" - they don't want to proceed.
                      
Say: "No problem. Is there anything else I can help you with today?"
Be polite and offer further assistance.`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }
            }
            
            // CONFIRMATION PHASE: Handle user response to fare quote
            if (sessionState.awaitingConfirmation) {
              const lowerText = userText.toLowerCase().trim();
              const normalized = lowerText
                .replace(/[^a-z\s]/g, " ")
                .replace(/\s+/g, " ")
                .trim();
              const compact = normalized.replace(/\s+/g, "");
              
              // Check for EXPLICIT affirmative confirmation (go ahead, yes, book it, etc.)
              // NOTE: We allow small transcription typos like "yesy"/"yess" to avoid Ada seeming unresponsive.
              const affirmativePatterns = /^(yes|yeah|yep|yup|sure|ok|okay|go ahead|book it|confirm|please|that's fine|that's good|perfect|great|lovely|brilliant|sounds good|do it|make the booking|yes please|absolutely|definitely)\b/i;
              const looksLikeYesTypo = compact.startsWith("yes") && compact.length <= 5; // yesy, yess
              const looksLikeOkTypo = compact.startsWith("ok") && compact.length <= 6;   // okk, okayy
              const isAffirmative = affirmativePatterns.test(normalized) || looksLikeYesTypo || looksLikeOkTypo;
              
              // Check for EXPLICIT negative response
              const negativePatterns = /^(no|nope|nah|cancel|nevermind|never mind|forget it|too expensive|too much|no thanks|no thank you)\b/i;
              const isNegative = negativePatterns.test(normalized);
              
              // Check if it looks like an address (correction attempt)
              const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "airport", "station", "hotel", "hospital"];
              const hasAddressKeyword = addressKeywords.some(kw => lowerText.includes(kw));
              const hasHouseNumber = /\d+[a-zA-Z]?\s/.test(userText);
              const looksLikeAddress = hasAddressKeyword || hasHouseNumber;
              
              console.log(`[${callId}] 🎯 CONFIRMATION CHECK: "${userText}" → affirmative=${isAffirmative}, negative=${isNegative}, address=${looksLikeAddress}`);
              
              if (isAffirmative && !looksLikeAddress) {
                // USER CONFIRMED - trigger book_taxi(confirmed) directly!
                console.log(`[${callId}] ✅ USER CONFIRMED BOOKING with: "${userText}"`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[BOOKING CONFIRMATION] ${userText}`,
                  timestamp: Date.now()
                });
                
                // Inject system message to force Ada to call book_taxi(confirmed)
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[USER CONFIRMED BOOKING] The user said "${userText}" which is a clear YES.

🚨 IMMEDIATELY call book_taxi with action="confirmed" to complete the booking.
Do NOT ask any more questions. The booking is confirmed.
Call: book_taxi({ action: "confirmed" })`
                    }]
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
              
              if (isNegative && !looksLikeAddress) {
                // USER DECLINED
                console.log(`[${callId}] ❌ USER DECLINED BOOKING with: "${userText}"`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[BOOKING DECLINED] ${userText}`,
                  timestamp: Date.now()
                });
                
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[USER DECLINED BOOKING] The user said "${userText}" which means NO.
                      
Say: "No problem. Is there anything else I can help you with?"
Do NOT cancel abruptly - be polite and offer further assistance.`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                break;
              }
              
              if (looksLikeAddress) {
                console.log(`[${callId}] 🔄 CONFIRMATION PHASE ADDRESS: "${userText}" - treating as correction request`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[CONTEXT: User said address during confirmation phase] ${userText}`,
                  timestamp: Date.now()
                });
                
                // User is providing an address during confirmation - they want to change something
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[ADDRESS DURING CONFIRMATION] The user said: "${userText}" when you asked for confirmation.
                      
This is NOT a yes/no - they are providing an address. They likely want to CHANGE something in the booking.

DO NOT cancel the booking. Instead ask: "Would you like me to change the destination to ${userText}?" or "Would you like me to change the pickup to ${userText}?"

Current booking: pickup="${sessionState.booking.pickup}", destination="${sessionState.booking.destination}"`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }
              
              // Unclear response - ask for clarification
              console.log(`[${callId}] ❓ UNCLEAR CONFIRMATION: "${userText}"`);
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[UNCLEAR RESPONSE] The user said: "${userText}" but I'm not sure if that's a yes or no.
                    
Ask clearly: "Would you like me to book that taxi for you?"`
                  }]
                }
              }));
              safeResponseCreate(openaiWs!, sessionState, callId);
              break;
            }

            // PASSENGER CLARIFICATION GUARD: Detect address-like response to passenger question
            // Use effectiveQuestionType (snapshotted at speech start) for accurate context
            if (effectiveQuestionType === "passengers") {
              // Check if response looks like an address rather than a number
              const looksLikeAddress = /\b(road|street|avenue|lane|drive|way|close|court|place|crescent|terrace|airport|station|hotel|hospital|mall|centre|center|square|park|building|house|flat|apartment|\d+[a-zA-Z]?\s+\w)/i.test(userText);
              const hasNumber = /\b(one|two|three|four|five|six|seven|eight|nine|ten|[1-9]|1[0-9]|20)\s*(passenger|people|person|of us)?s?\b/i.test(userText);
              const isJustNumber = /^[1-9]$|^1[0-9]$|^20$|^(one|two|three|four|five|six|seven|eight|nine|ten)$/i.test(userText.trim());
              
              // ECHO DETECTION: Check if transcribed text contains recently confirmed addresses
              // This happens when Ada's voice bleeds back into the mic
              const destLower = (sessionState.booking.destination || "").toLowerCase();
              const pickupLower = (sessionState.booking.pickup || "").toLowerCase();
              const userLower = userText.toLowerCase();
              
              // Extract key words from addresses (e.g., "Russell" from "7 Russell Street")
              const destWords = destLower.split(/\s+/).filter(w => w.length > 3 && !/\d/.test(w));
              const pickupWords = pickupLower.split(/\s+/).filter(w => w.length > 3 && !/\d/.test(w));
              
              const isEchoOfDestination = destWords.some(w => userLower.includes(w));
              const isEchoOfPickup = pickupWords.some(w => userLower.includes(w));
              const isEcho = isEchoOfDestination || isEchoOfPickup;
              
              if (isEcho && !hasNumber && !isJustNumber) {
                // This is Ada's voice echoing back - just ignore and wait for real input
                console.log(`[${callId}] 🔊 ECHO DETECTED: "${userText}" contains address words from booking - ignoring`);
                // Don't reprompt, just wait for the next audio
                break;
              }
              
              if (looksLikeAddress && !hasNumber && !isJustNumber && !isEcho) {
                console.log(`[${callId}] 🔄 PASSENGER CLARIFICATION: Got address "${userText}" when expecting passenger count`);
                
                // Store the address for later (might be a correction they want to make)
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[CONTEXT: Ada asked about passengers but user said an address] ${userText}`,
                  timestamp: Date.now()
                });
                
                // Force immediate re-prompt for passengers
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[PASSENGER CLARIFICATION NEEDED] You asked for the number of passengers, but the user said: "${userText}" which sounds like an address.

DO NOT interpret this as passenger count. Politely clarify:
- Acknowledge you might have misheard
- Ask specifically for the NUMBER of passengers traveling
- Keep it brief: "Sorry, I missed that. How many passengers will be traveling?"

Current booking: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=NOT YET SET`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break; // Don't run normal context pairing
              }
            }
            
            // RACE CONDITION RECOVERY: If user answered a PREVIOUS question (e.g., destination) but
            // Ada has already moved to the next question (e.g., passengers), route the answer correctly
            if (effectiveQuestionType !== sessionState.lastQuestionAsked) {
              const looksLikeAddress = /\b(road|street|avenue|lane|drive|way|close|court|place|crescent|terrace|airport|station|hotel|hospital|mall|centre|center|square|park|building|house|flat|apartment|\d+[a-zA-Z]?\s+\w)/i.test(userText);
              const isJustNumber = /^[1-9]$|^1[0-9]$|^20$|^(one|two|three|four|five|six|seven|eight|nine|ten)$/i.test(userText.trim());
              
              // If we expected destination and got an address, route it correctly
              if (effectiveQuestionType === "destination" && looksLikeAddress) {
                console.log(`[${callId}] 🔄 RACE RECOVERY: User answered destination ("${userText}") but current question is "${sessionState.lastQuestionAsked}" - routing to destination`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[CONTEXT: Ada asked about destination] ${userText}`,
                  timestamp: Date.now()
                });
                
                // DUPLICATE QUESTION FIX: Check if Ada already asked the next question
                const willAskNext = computeNextStep({ ...sessionState.booking, destination: userText }, sessionState.preSummaryDone);
                if (willAskNext === sessionState.lastQuestionAsked) {
                  console.log(`[${callId}] ⏭️ SKIPPING response.create - Ada already asked "${willAskNext}"`);
                  openaiWs!.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "system",
                      content: [{
                        type: "input_text",
                        text: `[DESTINATION CAPTURED] User said "${userText}" as destination. EXTRACT the actual address from their speech (remove "going to", "I want to go to", "please", etc.) and call sync_booking_data with the extracted destination. Do NOT re-ask - you already asked the next question.`
                      }]
                    }
                  }));
                  break;
                }
                
                // Update destination directly and ask Ada to continue to passengers
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[LATE DESTINATION ANSWER] The user just said "${userText}" which is their DESTINATION (they were answering your previous question).

EXTRACT the actual address from their speech (remove conversational phrases like "going to", "I want to go to", "please", etc.) and call sync_booking_data with the extracted destination.
Then ask: "How many passengers will be traveling?" (since we now need passengers).

Current state: pickup=${sessionState.booking.pickup}, destination=EXTRACTING, passengers=NOT SET`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }
              
              // If we expected pickup and got an address, route it correctly  
              if (effectiveQuestionType === "pickup" && looksLikeAddress) {
                console.log(`[${callId}] 🔄 RACE RECOVERY: User answered pickup ("${userText}") but current question is "${sessionState.lastQuestionAsked}" - routing to pickup`);
                
                sessionState.conversationHistory.push({
                  role: "user",
                  content: `[CONTEXT: Ada asked about pickup] ${userText}`,
                  timestamp: Date.now()
                });
                
                // DUPLICATE QUESTION FIX: If Ada just asked for destination and we're routing pickup,
                // DON'T trigger a new response - Ada already asked the right question (destination)
                // Just let the tool call flow naturally without re-asking
                const willAskNext = computeNextStep({ ...sessionState.booking, pickup: userText }, sessionState.preSummaryDone);
                if (willAskNext === sessionState.lastQuestionAsked) {
                  console.log(`[${callId}] ⏭️ SKIPPING response.create - Ada already asked "${willAskNext}"`);
                  // Just inject the system message for context, but don't trigger a new response
                  openaiWs!.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "system",
                      content: [{
                        type: "input_text",
                        text: `[PICKUP CAPTURED] User said "${userText}" as pickup. EXTRACT the actual address from their speech (remove "pick me up from", "I'm at", "please", etc.) and call sync_booking_data with the extracted pickup. Do NOT re-ask for destination - you already asked.`
                      }]
                    }
                  }));
                  break;
                }
                
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[LATE PICKUP ANSWER] The user just said "${userText}" which is their PICKUP address.

EXTRACT the actual address from their speech (remove conversational phrases like "pick me up from", "I'm at", "from", "please", etc.) and call sync_booking_data with the extracted pickup.
Then continue with the next question based on what's missing.

Current state: pickup=EXTRACTING`
                    }]
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }
            }
            
            // Add to history with context annotation (normal flow)
            // Use effectiveQuestionType for accurate context
            sessionState.conversationHistory.push({
              role: "user",
              content: `[CONTEXT: Ada asked about ${effectiveQuestionType}] ${userText}`,
              timestamp: Date.now()
            });
            
            // Send STRICT context-aware prompt to OpenAI with explicit field mapping
            // Use effectiveQuestionType (snapshotted) for accurate field mapping
            const expectedField = effectiveQuestionType;
            const fieldMapping: Record<string, string> = {
              pickup: "pickup",
              destination: "destination", 
              passengers: "passengers",
              time: "pickup_time"
            };
            const toolField = fieldMapping[expectedField] || expectedField;
            
            // Extract the actual address/value from user's natural speech
            // e.g. "pick me up from 52A David Road please" → "52A David Road"
            const extractionHint = expectedField === "pickup" || expectedField === "destination"
              ? `EXTRACT the actual address from the user's speech. Remove conversational phrases like "pick me up from", "I'm at", "going to", "please", "thanks", etc.
For example:
- "pick me up from 52A David Road please" → extract "52A David Road"
- "I'm at the train station" → extract "train station" or "the train station"
- "going to Heathrow Airport terminal 5" → extract "Heathrow Airport terminal 5"
- "from my house at 7 Russell Street" → extract "7 Russell Street"`
              : expectedField === "passengers"
              ? `EXTRACT the number from the user's speech.
For example:
- "there's three of us" → extract 3
- "just me" → extract 1
- "two passengers" → extract 2`
              : `EXTRACT the time from the user's speech.
For example:
- "in about 10 minutes" → extract "in 10 minutes" or "10 minutes"
- "as soon as possible" → extract "ASAP" or "now"
- "at 3pm" → extract "3pm"`;
            
            const contextPrompt = {
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: `[STRICT CONTEXT PAIRING] 
You asked for: ${expectedField.toUpperCase()}
User said: "${userText}"

🚨 REQUIRED ACTION:
${extractionHint}

Call sync_booking_data with ONLY the "${toolField}" field set to the EXTRACTED value (not the raw speech).

⚠️ DO NOT put this in any other field. The server will tell you what to ask next.

Current booking: pickup=${sessionState.booking.pickup || "NOT SET"}, destination=${sessionState.booking.destination || "NOT SET"}, passengers=${sessionState.booking.passengers ?? "NOT SET"}, time=${sessionState.booking.pickupTime || "NOT SET"}`
                }]
              }
            };
            openaiWs!.send(JSON.stringify(contextPrompt));
            
            await updateLiveCall(sessionState);
          }
          break;

        case "response.function_call_arguments.done":
          // Handle tool calls
          const toolName = data.name;
          let toolArgs: Record<string, unknown> = {};
          
          try {
            toolArgs = JSON.parse(data.arguments || "{}");
          } catch {
            console.error(`[${callId}] Failed to parse tool args`);
          }
          
          console.log(`[${callId}] 🔧 Tool: ${toolName}`, toolArgs);
          
          if (toolName === "sync_booking_data") {
            // SEQUENCE VALIDATION: Only update the field that matches lastQuestionAsked
            // This prevents the AI from putting data in the wrong field
            const expectedField = sessionState.lastQuestionAsked;
            let fieldUpdated: string | null = null;
            
            // Validate and update ONLY the expected field
            // STRATEGY: Use Ada's extraction if it's GROUNDED in user STT (shares tokens)
            // This allows cleaning like "52A" → "52A David Road" but rejects 
            // hallucinations like "52A" → "Victoria Station"
            if (expectedField === "pickup" && toolArgs.pickup) {
              const extractedPickup = validateAddress(String(toolArgs.pickup), callId);
              if (extractedPickup) {
                const userStt = sessionState.userTruth.pickup || "";
                const isGrounded = isGroundedInUserText(extractedPickup, userStt);

                // If we don't have any user transcript to ground against, never accept a guessed address.
                // This is the root cause of "Victoria Station" being accepted when STT was empty.
                if (!userStt) {
                  console.log(
                    `[${callId}] 🚫 Rejecting Ada pickup (no STT to ground against): "${extractedPickup}"`
                  );
                } else if (isGrounded) {
                  // Ada's extraction is valid - use it (allows cleaning like "52A" → "52A David Road")
                  sessionState.booking.pickup = extractedPickup;
                  sessionState.userTruth.pickup = extractedPickup; // Safe to update since it's grounded
                  console.log(`[${callId}] 📌 Ada pickup GROUNDED: "${extractedPickup}" (from STT: "${userStt}")`);
                  fieldUpdated = "pickup";
                } else {
                  // Ada hallucinated - use raw STT instead
                  sessionState.booking.pickup = userStt;
                  console.log(`[${callId}] 🚫 Ada pickup HALLUCINATION rejected: "${extractedPickup}" vs STT: "${userStt}"`);
                  fieldUpdated = "pickup";
                }
              } else {
                console.log(`[${callId}] ⚠️ Pickup rejected as garbage, asking again`);
              }
            } else if (expectedField === "destination" && toolArgs.destination) {
              const extractedDest = validateAddress(String(toolArgs.destination), callId);
              if (extractedDest) {
                const userStt = sessionState.userTruth.destination || "";
                const isGrounded = isGroundedInUserText(extractedDest, userStt);

                if (!userStt) {
                  console.log(
                    `[${callId}] 🚫 Rejecting Ada destination (no STT to ground against): "${extractedDest}"`
                  );
                } else if (isGrounded) {
                  // Ada's extraction is valid - use it
                  sessionState.booking.destination = extractedDest;
                  sessionState.userTruth.destination = extractedDest;
                  console.log(`[${callId}] 📌 Ada destination GROUNDED: "${extractedDest}" (from STT: "${userStt}")`);
                  fieldUpdated = "destination";
                } else {
                  // Ada hallucinated - use raw STT instead
                  sessionState.booking.destination = userStt;
                  console.log(`[${callId}] 🚫 Ada destination HALLUCINATION rejected: "${extractedDest}" vs STT: "${userStt}"`);
                  fieldUpdated = "destination";
                }
              } else {
                console.log(`[${callId}] ⚠️ Destination rejected as garbage, asking again`);
              }
            } else if (expectedField === "passengers" && toolArgs.passengers !== undefined) {
              const extractedPax = Number(toolArgs.passengers);
              sessionState.booking.passengers = extractedPax;
              // Passengers are numeric so less likely to hallucinate - can update userTruth
              if (sessionState.userTruth.passengers <= 0) {
                sessionState.userTruth.passengers = extractedPax;
              }
              fieldUpdated = "passengers";
            } else if (expectedField === "time" && toolArgs.pickup_time) {
              const extractedTime = String(toolArgs.pickup_time);
              sessionState.booking.pickupTime = extractedTime;
              // Time is less likely to hallucinate - can update userTruth if empty
              if (!sessionState.userTruth.time) {
                sessionState.userTruth.time = extractedTime;
              }
              fieldUpdated = "time";
            } else {
              // AI tried to update wrong field - still accept but log warning
              console.log(`[${callId}] ⚠️ sync_booking_data: expected ${expectedField} but got`, toolArgs);
              // Fall back to accepting whatever field was provided (with grounding validation)
              if (toolArgs.pickup) { 
                const val = validateAddress(String(toolArgs.pickup), callId);
                if (val) {
                  const userStt = sessionState.userTruth.pickup || "";
                  const isGrounded = isGroundedInUserText(val, userStt);

                  if (!userStt) {
                    console.log(`[${callId}] 🚫 Rejecting Ada pickup (no STT to ground against): "${val}"`);
                  } else {
                    sessionState.booking.pickup = isGrounded ? val : userStt;
                    if (isGrounded) sessionState.userTruth.pickup = val;
                    fieldUpdated = "pickup";
                  }
                }
              }
              else if (toolArgs.destination) { 
                const val = validateAddress(String(toolArgs.destination), callId);
                if (val) {
                  const userStt = sessionState.userTruth.destination || "";
                  const isGrounded = isGroundedInUserText(val, userStt);

                  if (!userStt) {
                    console.log(`[${callId}] 🚫 Rejecting Ada destination (no STT to ground against): "${val}"`);
                  } else {
                    sessionState.booking.destination = isGrounded ? val : userStt;
                    if (isGrounded) sessionState.userTruth.destination = val;
                    fieldUpdated = "destination";
                  }
                }
              }
              else if (toolArgs.passengers !== undefined) { 
                const val = Number(toolArgs.passengers);
                sessionState.booking.passengers = val; 
                sessionState.userTruth.passengers = val;
                fieldUpdated = "passengers"; 
              }
              else if (toolArgs.pickup_time) { 
                const val = String(toolArgs.pickup_time);
                sessionState.booking.pickupTime = val; 
                sessionState.userTruth.time = val;
                fieldUpdated = "time"; 
              }
            }
            
            // If validation rejected the value (garbage), ask the same question again
            if (!fieldUpdated && expectedField && ["pickup", "destination"].includes(expectedField)) {
              console.log(`[${callId}] 🔄 Re-asking for ${expectedField} (garbage was rejected)`);
              
              const retryInstruction = expectedField === "pickup"
                ? "I didn't quite catch that. Where would you like to be picked up from?"
                : "Sorry, could you repeat that? Where would you like to go to?";
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ 
                    success: false, 
                    error: "Invalid input - please ask again",
                    instruction: retryInstruction
                  })
                }
              }));
              safeResponseCreate(openaiWs!, sessionState, callId);
            } else {
              // COMPUTE NEXT STEP from state (server-driven, not AI-driven)
              const nextStep = computeNextStep(sessionState.booking, sessionState.preSummaryDone);
              sessionState.lastQuestionAsked = nextStep;
              
              // Get the value that was just captured for recitation
              // CRITICAL: Use userTruth (raw STT after corrections) NOT Ada's extraction
              // This prevents hallucinations like "Dover Street" when user said "David Road"
              let justCapturedValue: string | undefined;
              if (fieldUpdated === "pickup") justCapturedValue = sessionState.userTruth.pickup || sessionState.booking.pickup || undefined;
              else if (fieldUpdated === "destination") justCapturedValue = sessionState.userTruth.destination || sessionState.booking.destination || undefined;
              else if (fieldUpdated === "passengers") justCapturedValue = String(sessionState.userTruth.passengers || sessionState.booking.passengers);
              else if (fieldUpdated === "time") justCapturedValue = sessionState.userTruth.time || sessionState.booking.pickupTime || undefined;
              
              // Pass userTruth for accurate summary (prevents address hallucinations)
              // Also pass just-captured field/value for ADDRESS RECITATION
              const nextInstruction = getNextStepInstruction(
                nextStep, 
                sessionState.booking, 
                sessionState.userTruth,
                fieldUpdated || undefined,
                justCapturedValue
              );
              
              console.log(`[${callId}] 📊 Booking updated (${fieldUpdated}):`, sessionState.booking, `| Next: ${nextStep}`);
              await updateLiveCall(sessionState);
              
              // Send tool result with EXPLICIT next step instruction
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ 
                    success: true, 
                    field_saved: fieldUpdated,
                    current_state: sessionState.booking,
                    next_step: nextStep,
                    instruction: nextInstruction
                  })
                }
              }));
              safeResponseCreate(openaiWs!, sessionState, callId);
            }
            
          } else if (toolName === "book_taxi") {
            const action = String(toolArgs.action || "");

            // USER TRUTH PRIORITY: Use captured STT values over AI-extracted values
            // This prevents hallucinations from corrupting addresses
            // FIXED: Correct precedence - userTruth first, then toolArgs, then booking state
            const resolvedPickup = sessionState.userTruth.pickup 
              ? sessionState.userTruth.pickup 
              : (toolArgs.pickup ? String(toolArgs.pickup) : sessionState.booking.pickup);
            const resolvedDestination = sessionState.userTruth.destination 
              ? sessionState.userTruth.destination 
              : (toolArgs.destination ? String(toolArgs.destination) : sessionState.booking.destination);
            const resolvedPassengers = sessionState.userTruth.passengers > 0 
              ? sessionState.userTruth.passengers 
              : (toolArgs.passengers !== undefined ? Number(toolArgs.passengers) : sessionState.booking.passengers);
            const resolvedPickupTime = sessionState.userTruth.time 
              ? sessionState.userTruth.time 
              : (toolArgs.pickup_time ? String(toolArgs.pickup_time) : sessionState.booking.pickupTime);

            // Log the resolution chain for debugging
            console.log(`[${callId}] 📊 Variable resolution: 
              pickup: userTruth="${sessionState.userTruth.pickup}" toolArgs="${toolArgs.pickup}" booking="${sessionState.booking.pickup}" → resolved="${resolvedPickup}"
              dest: userTruth="${sessionState.userTruth.destination}" toolArgs="${toolArgs.destination}" booking="${sessionState.booking.destination}" → resolved="${resolvedDestination}"
              pax: userTruth=${sessionState.userTruth.passengers} toolArgs=${toolArgs.passengers} booking=${sessionState.booking.passengers} → resolved=${resolvedPassengers}
              time: userTruth="${sessionState.userTruth.time}" toolArgs="${toolArgs.pickup_time}" booking="${sessionState.booking.pickupTime}" → resolved="${resolvedPickupTime}"`);

            // Final values after resolution (no additional || chains needed)
            const finalPickup = resolvedPickup;
            const finalDestination = resolvedDestination;
            const finalPassengers = resolvedPassengers;
            const finalTime = resolvedPickupTime || "ASAP";

            // Persist any provided details so subsequent tool calls (e.g. confirmed) include full booking info
            if (finalPickup) sessionState.booking.pickup = finalPickup;
            if (finalDestination) sessionState.booking.destination = finalDestination;
            if (finalPassengers !== null && !Number.isNaN(finalPassengers)) {
              sessionState.booking.passengers = finalPassengers;
            }
            if (finalTime) sessionState.booking.pickupTime = finalTime;

            // Prevent sending incomplete payloads (these cause duplicate/garbage quotes downstream)
            const missing: string[] = [];
            if (!sessionState.booking.pickup) missing.push("pickup");
            if (!sessionState.booking.destination) missing.push("destination");
            // CRITICAL FIX: Block if passengers is null, undefined, NaN, OR 0 (must be at least 1)
            if (sessionState.booking.passengers === null || 
                sessionState.booking.passengers === undefined ||
                Number.isNaN(sessionState.booking.passengers) || 
                sessionState.booking.passengers <= 0) {
              missing.push("passengers");
            }

            // De-dupe: once quote requested/in-flight, don't re-send
            const QUOTE_DEDUPE_MS = 15000;
            const recentlyRequestedQuote = sessionState.lastQuoteRequestedAt > 0 && (Date.now() - sessionState.lastQuoteRequestedAt) < QUOTE_DEDUPE_MS;

            if (action === "request_quote") {
              if (sessionState.bookingConfirmed) {
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({ success: false, status: "ignored", reason: "already_confirmed" })
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }

              if (sessionState.awaitingConfirmation || sessionState.quoteInFlight || recentlyRequestedQuote) {
                console.log(`[${callId}] ⚠️ Ignoring duplicate request_quote (awaitingConfirmation=${sessionState.awaitingConfirmation}, quoteInFlight=${sessionState.quoteInFlight}, recently=${recentlyRequestedQuote})`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({ success: true, status: "pending", message: "Quote already requested. Please wait." })
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }

              if (missing.length > 0) {
                console.log(`[${callId}] ⚠️ Blocking request_quote - missing fields: ${missing.join(", ")}`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "missing_fields",
                      missing,
                      message: `Cannot request a quote yet. Missing: ${missing.join(", ")}. Ask the user for the missing info (one question only).`
                    })
                  }
                }));
                safeResponseCreate(openaiWs!, sessionState, callId);
                break;
              }

              // Send webhook to dispatch system (async - they respond via callback)
              sessionState.quoteInFlight = true;
              sessionState.lastQuoteRequestedAt = Date.now();

              const webhookResult = await sendDispatchWebhook(sessionState, action, {
                pickup: resolvedPickup,
                destination: resolvedDestination,
                passengers: resolvedPassengers,
                pickup_time: resolvedPickupTime
              });

              // If the dispatch system is unreachable, DON'T enter silence mode (otherwise the call sounds "stuck").
              // Let Ada inform the caller once and allow a retry.
              if (!webhookResult.success) {
                console.error(`[${callId}] ❌ Dispatch webhook failed - not entering silence mode: ${webhookResult.error || "unknown_error"}`);
                sessionState.quoteInFlight = false;
                sessionState.waitingForQuoteSilence = false;
                sessionState.saidOneMoment = false;

                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "dispatch_unreachable",
                      error: webhookResult.error || "dispatch_unreachable",
                      message: "Dispatch pricing system did not respond. Apologize briefly and ask if you'd like to try again."
                    })
                  }
                }));

                openaiWs!.send(JSON.stringify({
                  type: "response.create",
                  response: {
                    modalities: ["audio", "text"],
                    instructions: "Say ONLY: 'Sorry, I'm having trouble getting the fare right now. Would you like me to try again?' Then STOP and WAIT for their answer."
                  }
                }));

                break;
              }

              // Set silence mode - Ada must not speak again until fare arrives
              sessionState.waitingForQuoteSilence = true;
              sessionState.saidOneMoment = true;

              // Tell Ada to say "one moment" ONCE and then be completely silent
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: true,
                    status: "waiting_for_dispatch",
                    message: "Say EXACTLY: 'One moment please while I check that for you.' Then STOP. Do NOT speak again. Do NOT guess prices or ETAs. Wait for [DISPATCH QUOTE RECEIVED]."
                  })
                }
              }));
              
              // Create ONE response for "one moment" then enter silence mode
              openaiWs!.send(JSON.stringify({ 
                type: "response.create",
                response: {
                  modalities: ["audio", "text"],
                  instructions: "Say ONLY: 'One moment please while I check that for you.' Then STOP COMPLETELY. Say nothing else."
                }
              }));
              
              // ═══════════════════════════════════════════════════════════════════════════
              // FALLBACK QUOTE TIMER (4 seconds) - like simple mode
              // If dispatch doesn't respond within FALLBACK_QUOTE_TIMEOUT_MS, inject a mock
              // quote so Ada can keep the conversation moving. This prevents long silences.
              // ═══════════════════════════════════════════════════════════════════════════
              sessionState.fallbackQuoteTimerId = trackedTimeout(() => {
                if (cleanedUp) return;
                
                // Skip if we already got a real quote or delivered a fallback
                if (sessionState.quoteDelivered || sessionState.awaitingConfirmation || sessionState.bookingConfirmed) {
                  console.log(`[${callId}] ⏰ Fallback timer (tool call) fired but quote already delivered - skipping`);
                  return;
                }
                
                console.log(`[${callId}] ⏰ FALLBACK QUOTE (tool call): Dispatch didn't respond in ${FALLBACK_QUOTE_TIMEOUT_MS}ms, using mock quote`);
                
                // Mark quote as delivered to prevent duplicates
                sessionState.quoteDelivered = true;
                sessionState.pendingFare = FALLBACK_QUOTE_FARE;
                sessionState.pendingEta = FALLBACK_QUOTE_ETA;
                sessionState.awaitingConfirmation = true;
                sessionState.quoteInFlight = false;
                sessionState.lastQuestionAsked = "confirmation";
                sessionState.awaitingConfirmationSetAt = Date.now();
                
                // Clear silence mode so Ada can speak
                sessionState.waitingForQuoteSilence = false;
                sessionState.saidOneMoment = false;
                
                if (openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
                  openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                  openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                  
                  sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
                  
                  const fallbackMessage = `The trip fare will be ${FALLBACK_QUOTE_FARE}, and the estimated arrival time is ${FALLBACK_QUOTE_ETA}. Would you like to go ahead and book that?`;
                  
                  openaiWs.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{ type: "input_text", text: `[DISPATCH QUOTE RECEIVED]: Say to the customer: "${fallbackMessage}". Then WAIT for their YES or NO answer. Do NOT call book_taxi until they explicitly say yes.

WHEN CUSTOMER RESPONDS:
- If they say YES / yeah / correct / confirm / go ahead / book it / please → IMMEDIATELY CALL book_taxi with action: "confirmed"
- If they say NO / cancel / too expensive / nevermind → Say "No problem, is there anything else I can help you with?"
- If unclear, ask: "Would you like me to book that for you?"

DO NOT say "booked" or "confirmed" until book_taxi with action: "confirmed" returns success.` }]
                    }
                  }));
                  
                  openaiWs.send(JSON.stringify({
                    type: "response.create",
                    response: { modalities: ["audio", "text"], instructions: `Say exactly: "${fallbackMessage}" - then STOP and WAIT for user response.` }
                  }));
                  
                  console.log(`[${callId}] 🎤 Fallback quote injected (tool call) - Ada should speak now`);
                }
              }, FALLBACK_QUOTE_TIMEOUT_MS);
              
              console.log(`[${callId}] ⏰ Fallback quote timer started (tool call path, ${FALLBACK_QUOTE_TIMEOUT_MS}ms)`);

            } else if (action === "confirmed") {
              console.log(`[${callId}] ✅ Processing CONFIRMED action (awaitingConfirmation=${sessionState.awaitingConfirmation}, bookingConfirmed=${sessionState.bookingConfirmed}, bookingRef=${sessionState.pendingBookingRef})`);
              
              // GUARD: Prevent duplicate confirmations - check this FIRST before any webhook
              if (sessionState.bookingConfirmed) {
                console.log(`[${callId}] ⚠️ CONFIRMED blocked - already confirmed (duplicate call)`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: true,
                      status: "already_confirmed",
                      message: "Booking already confirmed. Do not confirm again. Say goodbye and end the call."
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
              
              // Only allow confirm after we asked fare confirmation
              if (!sessionState.awaitingConfirmation) {
                console.log(`[${callId}] ⚠️ CONFIRMED blocked - not awaitingConfirmation`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "not_ready",
                      message: "Do not confirm booking yet. You must wait for the quote and ask the customer to confirm."
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }
              
              // Set confirmed flag IMMEDIATELY to prevent race conditions
              sessionState.bookingConfirmed = true;

              // Also require the booking details to be present (avoid sending nulls on confirm)
              if (missing.length > 0) {
                console.log(`[${callId}] ⚠️ CONFIRMED blocked - missing fields: ${missing.join(", ")}`);
                openaiWs!.send(JSON.stringify({
                  type: "conversation.item.create",
                  item: {
                    type: "function_call_output",
                    call_id: data.call_id,
                    output: JSON.stringify({
                      success: false,
                      status: "missing_fields",
                      missing,
                      message: `Cannot confirm booking yet. Missing: ${missing.join(", ")}. Ask for the missing info.`
                    })
                  }
                }));
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break;
              }

              console.log(`[${callId}] 📤 Sending CONFIRMED webhook...`);
              await sendDispatchWebhook(sessionState, action, {
                pickup: sessionState.booking.pickup,
                destination: sessionState.booking.destination,
                passengers: sessionState.booking.passengers,
                pickup_time: sessionState.booking.pickupTime
              });
              console.log(`[${callId}] ✅ CONFIRMED webhook sent successfully`);
              
              // POST confirmation to callback_url if provided (tells dispatch to book the driver)
              // This matches taxi-realtime-simple behavior
              if (sessionState.pendingConfirmationCallback) {
                try {
                  console.log(`[${callId}] 📡 POSTing confirmation to callback_url: ${sessionState.pendingConfirmationCallback}`);
                  const confirmPayload = {
                    call_id: callId,
                    job_id: sessionState.dispatchJobId || null,
                    action: "confirmed",
                    response: "confirmed",  // C# bridge compatibility
                    pickup: sessionState.booking.pickup,
                    destination: sessionState.booking.destination,
                    fare: sessionState.pendingFare,
                    eta: sessionState.pendingEta,
                    pickup_time: sessionState.booking.pickupTime || "ASAP",
                    passengers: sessionState.booking.passengers || 1,
                    customer_name: null,  // Not tracked in paired mode
                    caller_phone: sessionState.callerPhone,
                    booking_ref: sessionState.pendingBookingRef || sessionState.bookingRef || null,
                    timestamp: new Date().toISOString()
                  };
                  
                  const confirmResp = await fetch(sessionState.pendingConfirmationCallback, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(confirmPayload)
                  });
                  
                  console.log(`[${callId}] 📬 Callback response: ${confirmResp.status}`);
                } catch (callbackErr) {
                  console.error(`[${callId}] ⚠️ Callback POST failed:`, callbackErr);
                }
              }

              // bookingConfirmed already set at top of this block to prevent race conditions
              sessionState.awaitingConfirmation = false;
              sessionState.quoteInFlight = false;
              
              // Protect goodbye speech from interruption
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
              
              // Set lastQuestionAsked to "none" to prevent looping back to booking questions
              sessionState.lastQuestionAsked = "none";
              
              // Get language-aware closing script
              const closingScript = getClosingScript(sessionState.language);
              const randomTip = closingScript.whatsappTips[Math.floor(Math.random() * closingScript.whatsappTips.length)];
              
              // For auto-detect mode, instruct AI to use the language it detected during the call
              const langInstruction = sessionState.language === "auto" 
                ? "Deliver this in the SAME LANGUAGE you've been speaking with the caller. Translate naturally if needed."
                : "";
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ 
                    success: true, 
                    status: "confirmed",
                    message: `Booking confirmed! ${langInstruction} Deliver the FULL closing script in order:
1. "${closingScript.confirmation}"
2. "${closingScript.whatsappDetails}"
3. "${randomTip}"
4. "${closingScript.goodbye}"
Then IMMEDIATELY call end_call().`
                  })
                }
              }));
              
              // CRITICAL: Inject system message to prevent AI from looping back to booking questions
              console.log(`[${callId}] 📢 Injecting POST-CONFIRMATION mode and triggering goodbye response`);
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[POST-CONFIRMATION MODE ACTIVE] The booking is NOW CONFIRMED and COMPLETE. 
                    
🚨 CRITICAL RULES:
- Do NOT ask "shall I book that taxi?" - it's ALREADY BOOKED
- Do NOT ask for pickup, destination, passengers, or time - we HAVE all that
- Do NOT loop back to any booking questions
- If user speaks, respond with ONLY brief open conversation (e.g., "Is there anything else I can help with?")
- You are now in GOODBYE/OPEN CONVERSATION mode, NOT booking mode
- Your next action should be to say goodbye and call end_call()`
                  }]
                }
              }));
              
              const postConfirmResponse = {
                modalities: ["audio", "text"] as Array<"audio" | "text">,
                instructions: `You MUST now say the full closing script warmly:
 1. "${closingScript.confirmation}"
 2. "${closingScript.whatsappDetails}"
 3. "${randomTip}"
 4. "${closingScript.goodbye}"
Then call end_call() with reason="booking_complete".`
              };

              if (sessionState.openAiResponseActive) {
                console.log(`[${callId}] ⏳ OpenAI response active - queueing post-confirmation goodbye response`);
                sessionState.pendingPostConfirmResponse = postConfirmResponse;
              } else {
                // CRITICAL: Request response with explicit audio modalities to ensure Ada speaks
                console.log(`[${callId}] 🎙️ Requesting goodbye audio response from OpenAI`);
                openaiWs!.send(JSON.stringify({
                  type: "response.create",
                  response: postConfirmResponse
                }));
              }

            } else {
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ success: false, status: "unknown_action" })
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
            }

            await updateLiveCall(sessionState);
            
          } else if (toolName === "cancel_booking") {
            // SAFETY GUARD: Verify user explicitly said cancel/no - prevent STT mishearings from cancelling
            const recentTranscripts = sessionState.conversationHistory
              .filter(m => m.role === "user")
              .slice(-3)
              .map(m => m.content.toLowerCase());
            
            const lastTranscript = recentTranscripts[recentTranscripts.length - 1] || "";
            const hasCancelIntent = 
              lastTranscript.includes("cancel") ||
              lastTranscript.includes("never mind") ||
              lastTranscript.includes("forget it") ||
              lastTranscript.includes("no thanks") ||
              lastTranscript.includes("no thank you") ||
              /^no[,.\s]*$/.test(lastTranscript.trim()) ||
              lastTranscript === "no";
            
            // Check if user is providing an address correction instead (contains address keywords)
            const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "station", "airport", "hotel", "hospital"];
            const hasAddressKeyword = addressKeywords.some(kw => lastTranscript.includes(kw));
            const hasHouseNumber = /\d+[a-zA-Z]?\s/.test(lastTranscript);
            const looksLikeAddress = hasAddressKeyword || hasHouseNumber;
            
            if (!hasCancelIntent || looksLikeAddress) {
              // BLOCKED: User didn't explicitly cancel, or it looks like an address correction
              console.log(`[${callId}] ⚠️ cancel_booking BLOCKED - no explicit cancel intent in transcript: "${lastTranscript}" (looksLikeAddress=${looksLikeAddress})`);
              
              // Inject correction context instead
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({
                    success: false,
                    status: "not_cancel_intent",
                    message: "The user did not explicitly ask to cancel. They may be providing an address correction. Ask them to clarify what they want to change."
                  })
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
            } else {
              // Real cancel - proceed
              console.log(`[${callId}] ❌ Cancelling booking (explicit intent detected)`);
              
              sessionState.bookingConfirmed = false;
              sessionState.awaitingConfirmation = false;
              sessionState.quoteInFlight = false;
              
              // Update live_calls status
              await supabase.from("live_calls").update({
                booking_confirmed: false,
                status: "cancelled"
              }).eq("call_id", callId);
              
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "function_call_output",
                  call_id: data.call_id,
                  output: JSON.stringify({ success: true, message: "Booking cancelled" })
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
            }
            
          } else if (toolName === "end_call") {
            console.log(`[${callId}] 📞 Call ending: ${toolArgs.reason}`);
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ success: true })
              }
            }));
            
            // Protect the goodbye from being cut off by noise/echo
            sessionState.summaryProtectionUntil = Date.now() + (SUMMARY_PROTECTION_MS * 2);
            console.log(`[${callId}] 🛡️ End-call goodbye protection activated for ${SUMMARY_PROTECTION_MS * 2}ms`);

            // Let Ada say the full closing script in the caller's language
            const endClosingScript = getClosingScript(sessionState.language);
            const endClosingTip = endClosingScript.whatsappTips[Math.floor(Math.random() * endClosingScript.whatsappTips.length)];
            
            // For auto-detect mode, instruct AI to use the language it detected during the call
            const endLangInstruction = sessionState.language === "auto" 
              ? "Deliver this in the SAME LANGUAGE you've been speaking with the caller. Translate naturally if needed."
              : "";
            
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "user",
                content: [{
                  type: "input_text",
                  text: `[SYSTEM: ${endLangInstruction} Deliver the FULL closing script in this exact order:
1. "${endClosingScript.whatsappDetails}"
2. "${endClosingTip}"
3. "${endClosingScript.goodbye}"
Do NOT skip any part. Say ALL of it warmly.]`
                }]
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
            // Give extra time so the final message isn't truncated before hangup
            // Must be >= protection window (SUMMARY_PROTECTION_MS * 2 = 16s) + buffer
            // MEMORY LEAK FIX: Use tracked timeout so it's cleared on cleanup
            trackedTimeout(() => {
              try {
                socket.send(JSON.stringify({ type: "hangup", reason: toolArgs.reason }));
              } catch { /* ignore */ }
              cleanupWithKeepalive();
            }, 18000);
          }
          break;

        case "response.done": {
          // A response finished (text-only or otherwise). Clear active flag and flush any queued goodbye.
          sessionState.openAiResponseActive = false;
          sessionState.openAiSpeechStartedAt = 0;
          
          // Flush any deferred response.create that was queued during the active response
          if (sessionState.deferredResponsePayload && openaiWs && openaiWs.readyState === WebSocket.OPEN && !cleanedUp) {
            const queued = sessionState.deferredResponsePayload;
            sessionState.deferredResponsePayload = null;
            console.log(`[${callId}] 🚀 Flushing deferred response.create (on response.done)`);
            openaiWs.send(JSON.stringify(queued));
            sessionState.openAiResponseActive = true; // New response now active
            break; // Don't process pendingPostConfirmResponse in same cycle
          }

          if (
            sessionState.pendingPostConfirmResponse &&
            sessionState.bookingConfirmed &&
            openaiWs &&
            openaiWs.readyState === WebSocket.OPEN &&
            !cleanedUp
          ) {
            const queued = sessionState.pendingPostConfirmResponse;
            sessionState.pendingPostConfirmResponse = undefined;
            console.log(`[${callId}] 🚀 Flushing queued post-confirmation goodbye response (on response.done)`);
            openaiWs.send(JSON.stringify({ type: "response.create", response: queued }));
          }
          break;
        }

        case "error":
          console.error(`[${callId}] ❌ OpenAI error:`, data.error);
          break;
          
        default:
          // Log unhandled events for debugging
          if (data.type === "response.done") {
            // Log the full response.done to see why no audio
            console.log(`[${callId}] 📨 response.done:`, JSON.stringify(data.response?.output || data.response?.status_details || "no details"));
          } else if (data.type && !data.type.startsWith("input_audio_buffer") && !data.type.startsWith("rate_limits")) {
            console.log(`[${callId}] 📨 OpenAI event: ${data.type}`);
          }
          break;
      }
    } catch (e) {
      console.error(`[${callId}] Error processing OpenAI message:`, e);
    }
  };

  openaiWs.onerror = (error) => {
    console.error(`[${callId}] OpenAI WebSocket error:`, error);
  };

  openaiWs.onclose = (ev) => {
    // Include close codes/reasons to debug unexpected disconnects.
    // NOTE: Deno's WS close event may not always include reason.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const anyEv: any = ev;
    console.log(
      `[${callId}] OpenAI WebSocket closed` +
        (anyEv?.code ? ` code=${anyEv.code}` : "") +
        (anyEv?.reason ? ` reason=${anyEv.reason}` : ""),
    );
    cleanupWithKeepalive();
  };

  // Audio diagnostics for this session
  const audioDiag = createAudioDiagnostics();
  // Monitoring: throttle user audio inserts for the C# bridge path.
  let monitorUserChunkCount = 0;

  // Client WebSocket handlers
  socket.onmessage = async (event) => {
    try {
      // Handle binary audio data from Python bridge
      if (event.data instanceof ArrayBuffer || event.data instanceof Uint8Array) {
        const audioBytes = event.data instanceof ArrayBuffer
          ? new Uint8Array(event.data)
          : event.data;

        audioDiag.packetsReceived++;

        // Decode to PCM for RMS calculation
        let pcmInput: Int16Array;
        if (inboundAudioFormat === "ulaw") {
          pcmInput = ulawToPcm16(audioBytes); // 8kHz PCM16
        } else {
          // slin/slin16: already PCM16
          pcmInput = new Int16Array(audioBytes.buffer, audioBytes.byteOffset, Math.floor(audioBytes.byteLength / 2));
        }

        const rmsRaw = computeRms(pcmInput);
        const gain = computeAutoGain(rmsRaw);
        if (gain > 1) {
          pcmInput = applyGain(pcmInput, gain);
        }
        const rms = gain > 1 ? computeRms(pcmInput) : rmsRaw;
        updateRmsAverage(audioDiag, rms);

        // Log audio stats periodically
        if (audioDiag.packetsReceived <= 3) {
          console.log(
            `[${callId}] 🎙️ Audio #${audioDiag.packetsReceived}: ${audioBytes.length}b, RMS=${rms.toFixed(0)}, gain=${gain.toFixed(1)}, fmt=${inboundAudioFormat}@${inboundSampleRate}`,
          );
        } else if (audioDiag.packetsReceived % 200 === 0) {
          console.log(`[${callId}] 📊 Audio: rx=${audioDiag.packetsReceived}, fwd=${audioDiag.packetsForwarded}, noise=${audioDiag.packetsSkippedNoise}, echo=${audioDiag.packetsSkippedEcho}, avgRMS=${audioDiag.avgRms.toFixed(0)}`);
        }

        // GREETING PROTECTION: ignore early line noise so Ada doesn't get cut off
        if (Date.now() < sessionState.greetingProtectionUntil) {
          audioDiag.packetsSkippedGreeting++;
          return;
        }

        // ECHO GUARD: block audio briefly after Ada finishes speaking
        // BUT: if the user replies quickly and loudly, let it through (common for short confirmations).
        if (Date.now() < sessionState.echoGuardUntil && rms < ECHO_GUARD_RMS_BYPASS) {
          audioDiag.packetsSkippedEcho++;
          return; // Drop this audio frame (likely echo)
        }

        // NOTE: Removed RMS noise gate - telephony audio has very low RMS values (1-200)
        // and filtering was blocking ALL audio. We only filter during barge-in detection.

        // SUMMARY PROTECTION: block interruptions while Ada is recapping/quoting.
        // IMPORTANT: once Ada has FINISHED speaking the fare quote and we're awaiting a YES/NO,
        // we must allow user audio through immediately; otherwise the user's "yes" gets dropped
        // and Ada appears unresponsive.
        if (Date.now() < sessionState.summaryProtectionUntil) {
          const awaitingYesNo = sessionState.awaitingConfirmation || sessionState.lastQuestionAsked === "confirmation";
          if (!awaitingYesNo) {
            return; // Drop - Ada is delivering summary/quote (and we're not in confirmation yet)
          }
        }

        if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
          // RMS-based barge-in detection: only forward audio that sounds like real speech
          // If Ada is speaking, ignore the first slice of inbound audio to avoid echo/noise cutting her off,
          // then require RMS within a sane window to be treated as a real barge-in.
          if (sessionState.openAiResponseActive) {
            const sinceSpeakStart = sessionState.openAiSpeechStartedAt
              ? (Date.now() - sessionState.openAiSpeechStartedAt)
              : 0;
            if (sinceSpeakStart > 0 && sinceSpeakStart < ASSISTANT_LEADIN_IGNORE_MS) {
              audioDiag.packetsSkippedBotSpeaking++;
              return;
            }

            if (rms < RMS_BARGE_IN_MIN || rms > RMS_BARGE_IN_MAX) {
              audioDiag.packetsSkippedEcho++;
              return; // Not real speech, likely echo or noise
            }
          }

          // CONFIRMATION BARGE-IN: if we're awaiting a YES/NO and the user starts speaking while
          // Ada is still delivering the quote prompt, cancel Ada once so the user's "yes" isn't ignored.
          // CRITICAL FIX: Only allow barge-in if awaitingConfirmation is TRUE (meaning quote was received).
          // Do NOT barge-in just because lastQuestionAsked === "confirmation" - that's set BEFORE Ada
          // speaks the summary, so background noise would cancel the summary prematurely.
          // ADDITIONAL FIX: Add 2000ms cooldown after awaitingConfirmation is set to let Ada speak
          // the fare quote intro ("Hi, your price is...") before allowing user barge-in
          const bargeInCooldownMs = 2000;
          const bargeInCooldownPassed = Date.now() - sessionState.awaitingConfirmationSetAt > bargeInCooldownMs;
          
          if (
            sessionState.awaitingConfirmation &&  // MUST have received quote - not just "confirmation" step
            bargeInCooldownPassed &&  // Wait for stale audio to drain before allowing barge-in
            Date.now() < sessionState.summaryProtectionUntil &&
            sessionState.openAiResponseActive &&
            !sessionState.confirmationBargeInCancelled
          ) {
            sessionState.confirmationBargeInCancelled = true;
            sessionState.summaryProtectionUntil = 0;
            console.log(`[${callId}] 🛑 Confirmation barge-in detected - cancelling Ada response`);
            try {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            } catch (_) {
              // ignore
            }
            try {
              socket.send(JSON.stringify({ type: "ai_interrupted" }));
            } catch (_) {
              // ignore
            }
          }

          // EXPERIMENT: Direct passthrough vs processed audio
          let base64Audio: string;
          if (DIRECT_AUDIO_PASSTHROUGH) {
            // Send raw PCM directly - OpenAI/Whisper handles format conversion internally
            // Note: OpenAI expects 24kHz PCM16, but testing if their internal processing is better than ours
            base64Audio = pcm16ToBase64(pcmInput);
            if (audioDiag.packetsForwarded === 0) {
              console.log(`[${callId}] 🔊 DIRECT PASSTHROUGH MODE: Sending raw ${inboundSampleRate}Hz PCM to OpenAI (no pre-emphasis/resample)`);
            }
          } else {
            // Original flow: pre-emphasis for better STT consonant clarity, then resample to 24kHz
            const pcmEmph = applyPreEmphasis(pcmInput);
            const pcm24k = resamplePcm16To24k(pcmEmph, inboundSampleRate);
            base64Audio = pcm16ToBase64(pcm24k);
          }

          audioDiag.packetsForwarded++;
          openaiWs.send(JSON.stringify({
            type: "input_audio_buffer.append",
            audio: base64Audio,
          }));
          
          // NON-BLOCKING: Stream user audio to monitoring panel (fire and forget)
          // Downsample every Nth packet to reduce DB load (1 in 5 = ~200ms chunks)
          if (audioDiag.packetsForwarded % 5 === 0) {
            void supabase.from("live_call_audio").insert({
              call_id: callId,
              audio_chunk: base64Audio,
              audio_source: "user",
              created_at: new Date().toISOString(),
            });
          }
        }
        return;
      }

      // Handle string messages (JSON)
      if (typeof event.data === "string") {
        const data = JSON.parse(event.data);

        // Handle input_audio_buffer.append from C# bridge (already PCM16 @ 24kHz)
        if (data.type === "input_audio_buffer.append" && data.audio) {
          // GREETING PROTECTION: ignore early line noise so Ada doesn't get cut off
          if (Date.now() < sessionState.greetingProtectionUntil) {
            return;
          }

          // ECHO GUARD: block audio briefly after Ada finishes speaking
          // (For JSON audio we don't have RMS here; keep the reduced time-based guard only.)
          if (Date.now() < sessionState.echoGuardUntil) {
            return; // Drop this audio frame (likely echo)
          }

          // SUMMARY PROTECTION: block interruptions while Ada is recapping/quoting.
          if (Date.now() < sessionState.summaryProtectionUntil) {
            const awaitingYesNo = sessionState.awaitingConfirmation || sessionState.lastQuestionAsked === "confirmation";
            if (!awaitingYesNo) {
              return; // Drop - Ada is delivering summary/quote
            }
          }

          // CONFIRMATION BARGE-IN (C# path): we don't have RMS here, so if we're in the confirmation
          // window and Ada is still speaking, cancel once to avoid dropping the user's "yes".
          if (
            (sessionState.awaitingConfirmation || sessionState.lastQuestionAsked === "confirmation") &&
            Date.now() < sessionState.summaryProtectionUntil &&
            sessionState.openAiResponseActive &&
            !sessionState.confirmationBargeInCancelled
          ) {
            sessionState.confirmationBargeInCancelled = true;
            sessionState.summaryProtectionUntil = 0;
            console.log(`[${callId}] 🛑 Confirmation barge-in (C#) - cancelling Ada response`);
            try {
              openaiWs.send(JSON.stringify({ type: "response.cancel" }));
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
            } catch (_) {
              // ignore
            }
            try {
              socket.send(JSON.stringify({ type: "ai_interrupted" }));
            } catch (_) {
              // ignore
            }
          }

          // Forward directly to OpenAI - C# bridge already sends PCM16 @ 24kHz
          if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: data.audio,
            }));
            
            // NON-BLOCKING: Stream user audio to monitoring panel (fire and forget)
            monitorUserChunkCount++;
            if (monitorUserChunkCount % 5 === 0) {
              void supabase.from("live_call_audio").insert({
                call_id: callId,
                audio_chunk: data.audio,
                audio_source: "user",
                created_at: new Date().toISOString(),
              });
            }
          }
        } else if (data.type === "audio" && data.audio) {
          // Legacy handler: receive base64 audio, assume 8kHz ulaw unless told otherwise.
          const binaryStr = atob(data.audio);
          const bytes = new Uint8Array(binaryStr.length);
          for (let i = 0; i < binaryStr.length; i++) bytes[i] = binaryStr.charCodeAt(i);

          const assumedFormat: InboundAudioFormat = (data.format === "slin" || data.format === "slin16" || data.format === "ulaw")
            ? data.format
            : "ulaw";
          const assumedRate = typeof data.sample_rate === "number" ? data.sample_rate : 8000;

          const pcmInput = assumedFormat === "ulaw"
            ? ulawToPcm16(bytes)
            : new Int16Array(bytes.buffer, bytes.byteOffset, Math.floor(bytes.byteLength / 2));

          const pcm24k = resamplePcm16To24k(pcmInput, assumedRate);
          const base64Audio = pcm16ToBase64(pcm24k);

          if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
            openaiWs.send(JSON.stringify({
              type: "input_audio_buffer.append",
              audio: base64Audio,
            }));
          }
        } else if (data.type === "init" || data.type === "update_phone" || data.type === "update_format") {
          // Handle init/phone/format updates
          if (data.phone && data.phone !== "unknown") {
            callerPhone = data.phone;
            sessionState.callerPhone = data.phone; // Update session state too!
            console.log(`[${callId}] 📱 Phone updated: ${callerPhone}`);
          } else if (data.caller_phone && data.caller_phone !== "unknown") {
            // Also accept caller_phone field
            callerPhone = data.caller_phone;
            sessionState.callerPhone = data.caller_phone;
            console.log(`[${callId}] 📱 Phone from caller_phone: ${callerPhone}`);
          } else if (callerPhone === "unknown" && callId) {
            // Fallback: extract phone from Asterisk UUID format
            // Format: 00000000-0000-0000-0000-XXXXXXXXXXXX (last 12 digits = padded phone)
            const uuidMatch = callId.match(/^00000000-0000-0000-0000-(\d{12})$/);
            if (uuidMatch) {
              const phoneDigits = uuidMatch[1];
              // Strip leading zeros - different countries have different lengths
              // UK: 11-12 digits, NL: 10-11 digits, etc.
              const trimmed = phoneDigits.replace(/^0+/, "");
              if (trimmed.length >= 9) {
                callerPhone = "+" + trimmed;
                sessionState.callerPhone = callerPhone;
                console.log(`[${callId}] 📱 Phone extracted from UUID: ${callerPhone}`);
              }
            }
          }

          if (data.inbound_format && (data.inbound_format === "ulaw" || data.inbound_format === "slin" || data.inbound_format === "slin16")) {
            inboundAudioFormat = data.inbound_format;
          }
          if (typeof data.inbound_sample_rate === "number") {
            inboundSampleRate = data.inbound_sample_rate;
          }
          
          // ═══════════════════════════════════════════════════════════════════
          // SESSION RESTORATION: If bridge sends resume=true, restore state
          // from live_calls table instead of starting fresh greeting
          // ═══════════════════════════════════════════════════════════════════
          const isResume = data.resume === true || data.reconnect === true;
          const resumeCallId = data.resume_call_id || null;
          
          console.log(`[${callId}] 📋 Init flags: resume=${isResume}, resume_call_id=${resumeCallId}`);
          
          if (isResume) {
            // Try to restore session from database
            const lookupId = resumeCallId || callId;
            const lookupPhone = data.phone || callerPhone;
            
            console.log(`[${callId}] 🔄 RESUME: Looking up session ${lookupId} or phone ${lookupPhone}`);
            
            try {
              // First try by call_id
              let restoredSession = null;
              
              if (lookupId) {
                const { data: callData } = await supabase
                  .from("live_calls")
                  .select("*")
                  .eq("call_id", lookupId)
                  .maybeSingle();
                
                if (callData && !callData.ended_at) {
                  restoredSession = callData;
                  console.log(`[${callId}] ✅ Found session by call_id: ${lookupId}`);
                }
              }
              
              // Fallback: lookup by phone number (most recent active call in last 10 minutes)
              if (!restoredSession && lookupPhone && lookupPhone !== "unknown") {
                const tenMinutesAgo = new Date(Date.now() - 10 * 60 * 1000).toISOString();
                const { data: phoneData } = await supabase
                  .from("live_calls")
                  .select("*")
                  .eq("caller_phone", lookupPhone)
                  .in("status", ["active", "awaiting_confirmation", "confirmed"])
                  .gt("updated_at", tenMinutesAgo)
                  .order("updated_at", { ascending: false })
                  .limit(1)
                  .maybeSingle();
                
                if (phoneData && !phoneData.ended_at) {
                  restoredSession = phoneData;
                  console.log(`[${callId}] ✅ Found session by phone fallback: ${phoneData.call_id}`);
                }
              }
              
              if (restoredSession) {
                // Restore booking state
                sessionState.booking.pickup = restoredSession.pickup || null;
                sessionState.booking.destination = restoredSession.destination || null;
                sessionState.booking.passengers = restoredSession.passengers || null;
                sessionState.bookingConfirmed = restoredSession.booking_confirmed || false;
                sessionState.awaitingConfirmation = restoredSession.status === "awaiting_confirmation";
                
                // Restore conversation history
                if (Array.isArray(restoredSession.transcripts)) {
                  sessionState.conversationHistory = restoredSession.transcripts.map((t: any) => ({
                    role: t.role || "user",
                    content: t.content || t.text || "",
                    timestamp: t.timestamp || Date.now()
                  }));
                }
                
                // Compute what step we're on based on restored state
                if (sessionState.awaitingConfirmation || sessionState.bookingConfirmed) {
                  sessionState.lastQuestionAsked = "confirmation";
                } else if (sessionState.booking.passengers !== null) {
                  sessionState.lastQuestionAsked = "time";
                } else if (sessionState.booking.destination) {
                  sessionState.lastQuestionAsked = "passengers";
                } else if (sessionState.booking.pickup) {
                  sessionState.lastQuestionAsked = "destination";
                } else {
                  sessionState.lastQuestionAsked = "pickup";
                }
                
                // Mark greeting as already sent so we don't repeat it
                greetingSent = true;
                
                console.log(`[${callId}] 🔄 RESTORED SESSION: pickup=${sessionState.booking.pickup}, dest=${sessionState.booking.destination}, pax=${sessionState.booking.passengers}, step=${sessionState.lastQuestionAsked}`);
                
                // Skip greeting protection since user is returning
                sessionState.greetingProtectionUntil = 0;
                
                // Inject context for resumed session
                if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
                  const contextMsg = `[SESSION RESUMED - DO NOT GREET AGAIN]
Previous booking state:
- Pickup: ${sessionState.booking.pickup || "NOT SET"}
- Destination: ${sessionState.booking.destination || "NOT SET"}  
- Passengers: ${sessionState.booking.passengers ?? "NOT SET"}
- Last step: ${sessionState.lastQuestionAsked}
${sessionState.awaitingConfirmation ? "- AWAITING CONFIRMATION (fare quote was delivered)" : ""}

Continue the conversation where you left off. Do NOT say hello or introduce yourself again. 
${sessionState.awaitingConfirmation 
  ? "Ask if they want to confirm the booking."
  : `Ask for the ${sessionState.lastQuestionAsked === "pickup" ? "pickup address" : sessionState.lastQuestionAsked}.`
}`;
                  
                  openaiWs.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "user",
                      content: [{ type: "input_text", text: contextMsg }]
                    }
                  }));
                  
                  openaiWs.send(JSON.stringify({
                    type: "response.create",
                    response: {
                      modalities: ["audio", "text"],
                      instructions: sessionState.awaitingConfirmation 
                        ? "The session resumed. Ask briefly if they want to go ahead with the booking - don't repeat all the details."
                        : `The session resumed. Ask briefly for the ${sessionState.lastQuestionAsked} - one short sentence only.`
                    }
                  }));
                }
              } else {
                console.log(`[${callId}] ⚠️ No session found to restore - starting fresh`);
              }
            } catch (e) {
              console.error(`[${callId}] ❌ Error restoring session:`, e);
            }
          }
        } else if (data.type === "keepalive_ack") {
          // Bridge acknowledged our keepalive - connection is alive
          // (no action needed, just confirms the connection is healthy)
        } else if (data.type === "hangup") {
          console.log(`[${callId}] Client hangup - audio stats: fwd=${audioDiag.packetsForwarded}/${audioDiag.packetsReceived}, noise=${audioDiag.packetsSkippedNoise}, echo=${audioDiag.packetsSkippedEcho}, avgRMS=${audioDiag.avgRms.toFixed(0)}`);
          cleanupWithKeepalive();
        }
      }
    } catch (e) {
      console.error(`[${callId}] Error processing client message:`, e);
    }
  };

  socket.onerror = (error) => {
    console.error(`[${callId}] Client WebSocket error:`, error);
  };

  socket.onclose = (ev) => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const anyEv: any = ev;
    console.log(
      `[${callId}] Client closed` +
        (anyEv?.code ? ` code=${anyEv.code}` : "") +
        (anyEv?.reason ? ` reason=${anyEv.reason}` : "") +
        ` - audio: fwd=${audioDiag.packetsForwarded}/${audioDiag.packetsReceived}, noise=${audioDiag.packetsSkippedNoise}`,
    );
    cleanupWithKeepalive();
  };
}

// Main server
Deno.serve(async (req) => {
  const url = new URL(req.url);
  
  // CORS
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }
  
  // Health check
  if (url.pathname === "/health" || url.pathname.endsWith("/health")) {
    return new Response(JSON.stringify({ 
      status: "healthy",
      mode: "paired-context",
      timestamp: new Date().toISOString()
    }), {
      headers: { ...corsHeaders, "Content-Type": "application/json" }
    });
  }
  
  // WebSocket upgrade
  const upgrade = req.headers.get("upgrade") || "";
  if (upgrade.toLowerCase() === "websocket") {
    const callId = url.searchParams.get("call_id") || `paired-${Date.now()}`;
    const callerPhone = url.searchParams.get("caller_phone") || "unknown";
    // Language: "auto" for auto-detect, or ISO 639-1 code (en, es, fr, de, etc.)
    const language = url.searchParams.get("language") || "auto";
    
    const { socket, response } = Deno.upgradeWebSocket(req);
    
    handleConnection(socket, callId, callerPhone, language);
    
    return response;
  }
  
  return new Response(JSON.stringify({ 
    error: "WebSocket upgrade required",
    mode: "taxi-realtime-paired"
  }), {
    status: 400,
    headers: { ...corsHeaders, "Content-Type": "application/json" }
  });
});
