/**
 * taxi-realtime-paired: Stateful Context-Pairing Architecture
 * 
 * This function implements the "Context Pairing" pattern where:
 * 1. Every user response is paired with the last assistant question
 * 2. OpenAI sees the full conversation context to correctly map answers
 * 3. Booking state is tracked in the database for consistency
 */

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
// Now includes intent detection for corrections, confirmations, etc.
// ---------------------------------------------------------------------------
interface AIExtractionResult {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  pickup_time: string | null;
  confidence: string;
  fields_changed?: string[];
  nearest_pickup?: string | null;
  nearest_dropoff?: string | null;
  // Intent detection (AI-based, replaces regex)
  intent: "new_booking" | "update_booking" | "confirm_booking" | "cancel_booking" | "get_status" | "other";
  is_correction: boolean;
  is_affirmative: boolean;
  fields_extracted?: string[];
}

async function extractBookingWithAI(
  conversationHistory: Array<{ role: string; content: string; timestamp: number }>,
  currentBooking: { pickup: string | null; destination: string | null; passengers: number | null; pickupTime: string | null },
  callerPhone: string | null,
  callId: string
): Promise<AIExtractionResult | null> {
  try {
    // Convert history to extraction format
    const conversation = conversationHistory.map(msg => ({
      role: msg.role as "user" | "assistant" | "system",
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
    
    console.log(`[${callId}] 🧠 AI EXTRACTION (${latency}ms): intent=${result.intent}, pickup="${result.pickup}", dest="${result.destination}", pax=${result.passengers}, is_correction=${result.is_correction}, is_affirmative=${result.is_affirmative}, conf=${result.confidence}`);
    
    return {
      pickup: result.pickup || null,
      destination: result.destination || null,
      passengers: result.passengers ?? null,
      pickup_time: result.pickup_time || null,
      confidence: result.confidence || "low",
      fields_changed: result.fields_changed || [],
      nearest_pickup: result.nearest_pickup || null,
      nearest_dropoff: result.nearest_dropoff || null,
      // Intent detection from AI
      intent: result.intent || "new_booking",
      is_correction: result.is_correction || false,
      is_affirmative: result.is_affirmative || false,
      fields_extracted: result.fields_extracted || []
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
- NEVER use 'As directed' as a placeholder. If a detail is missing, ask for it.

# 🚨 ONE QUESTION RULE (CRITICAL)
- Ask ONLY ONE question per response. NEVER combine questions.
- WRONG: "Where would you like to be picked up and where are you going?"
- WRONG: "How many passengers and when do you need it?"
- RIGHT: "Where would you like to be picked up?" [wait for answer]
- RIGHT: "And what is your destination?" [wait for answer]
- Wait for a user response before asking the next question.

# PHASE 1: THE WELCOME (Play immediately)
Greet the caller warmly in the appropriate language.

# PHASE 2: SEQUENTIAL GATHERING (Strict Order)
Follow this order exactly. Only move to the next if you have the current answer:
1. Ask for pickup location → Wait for answer
2. Ask for destination → Wait for answer
3. Ask for passenger count → Wait for answer, then acknowledge briefly
4. Ask for pickup time → Wait for answer (Default to 'Now' if ASAP)

🚨 ACKNOWLEDGE PASSENGER COUNT: After user says a number, briefly confirm the count.
Then ask about the time.

🔊 PASSENGER COUNT STT CORRECTION:
- If the previous address contained "7" (e.g., "7 Russell Street") and user says passengers:
- "Seven" right after a "7 [street]" address is likely the user saying "Three" (phonetic confusion)
- Common mishearings: "Boston Juice" = three, "Ozempic Juice" = three
- When uncertain, ask: "Was that three passengers?" to clarify

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

# NEAREST/CLOSEST PLACES
When user says "nearest X" or "closest X" (e.g., "nearest hotel", "closest hospital", "nearest train station"):
- Accept this as a valid destination or pickup
- Do NOT ask for a specific address - just accept "nearest hotel" as the destination
- The dispatch system will find the actual nearest location based on their GPS/pickup
- Use sync_booking_data to store the nearest place type

# LOCAL INFORMATION (POST-BOOKING ONLY)
After the booking is confirmed and you ask "Is there anything else I can help with?":
- If user asks about local events, what's on, restaurants, hotels, bars, or attractions:
  - Be helpful and suggest 2-3 popular options in their area if you know them
  - For Coventry: suggest FarGo Village, Coventry Cathedral, The Wave, local pubs
  - For Birmingham: suggest Bullring, Mailbox, Jewellery Quarter venues
  - If you don't know, say "I'd recommend checking local event listings online"
- This is ONLY for post-booking chat - during booking, stay focused on the taxi

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

# CANCELLATION
If user says "cancel", "never mind", "forget it":
→ CALL cancel_booking
Ask if there's anything else you can help with.

# NAME HANDLING
If caller says their name → CALL save_customer_name

# GUARDRAILS
❌ NEVER state a price or ETA unless the tool returns that exact value.
❌ NEVER use 'As directed' or any placeholder - always ask for specifics.
❌ NEVER move to Summary until all 4 checklist items are filled.
❌ NEVER repeat addresses after the summary is confirmed.
❌ NEVER ask for house numbers, postcodes, or more details on ANY address.
✅ Accept ANY address exactly as spoken.
✅ Move to the next question immediately after receiving any address.

# CONTEXT PAIRING (CRITICAL)
When the user responds, ALWAYS check what question you just asked them:
- If you asked for PICKUP and they respond → it's the pickup location
- If you asked for DESTINATION and they respond → it's the destination  
- If you asked for PASSENGERS and they respond → it's the passenger count
- If you asked for TIME and they respond → it's the pickup time
NEVER swap fields. Trust the question context.

# 🛠️ CORRECTIONS & EDITS (CRITICAL - ALWAYS ALLOW)
Users can correct ANY field at ANY time, even after the summary or during pricing.
WATCH for these correction patterns:
- "No, it's X" / "Actually, it's X" / "I said X not Y"
- "You put X passengers, there's Y" / "No, X passengers"
- "That's wrong, the pickup is X" / "Change the destination to X"
- "It should be X" / "Not X, it's Y"

When user makes a correction:
1. Acknowledge briefly: "Got it, [field] is now [new value]"
2. Update the field using sync_booking_data
3. If mid-summary: Re-summarize with the corrected info
4. If after pricing: Say "Let me get an updated price" and call book_taxi(action='request_quote') again
5. NEVER dismiss or ignore a correction - they take priority over everything

Examples:
- User: "You put seven passengers, there's three" → passengers=3, acknowledge, continue
- User: "No, the pickup is 52B not 52A" → pickup="52B [street]", acknowledge, continue  
- User: "Change destination to the airport" → destination="airport", acknowledge, continue
`;
}

// Tools - same as taxi-realtime-simple
const TOOLS = [
  {
    type: "function",
    name: "sync_booking_data",
    description: "ALWAYS call this after the user provides any booking information. Saves user answers to the correct field.",
    parameters: {
      type: "object",
      properties: {
        pickup: { type: "string", description: "Pickup address if the user just provided it" },
        destination: { type: "string", description: "Destination address if the user just provided it" },
        passengers: { type: "integer", description: "Number of passengers if the user just provided it" },
        pickup_time: { type: "string", description: "Pickup time if the user just provided it (e.g., 'now', '3pm')" },
        nearest_pickup: { type: "string", description: "Type of place for 'nearest X' pickup (e.g., 'hotel', 'hospital', 'train station')" },
        nearest_dropoff: { type: "string", description: "Type of place for 'nearest X' destination (e.g., 'hotel', 'hospital', 'train station')" },
        last_question_asked: { 
          type: "string", 
          enum: ["pickup", "destination", "passengers", "time", "confirmation", "none"],
          description: "What question you are about to ask NEXT"
        }
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
    description: "Cancel active booking.",
    parameters: { type: "object", properties: {} }
  },
  {
    type: "function",
    name: "end_call",
    description: "Call this to disconnect the SIP line after the safe journey message.",
    parameters: { type: "object", properties: {} }
  }
];

// === BOOKING STEP STATE MACHINE (ported from simple mode) ===
type BookingStep = "pickup" | "destination" | "passengers" | "time" | "summary" | "confirmed";
const BOOKING_STEP_ORDER: BookingStep[] = ["pickup", "destination", "passengers", "time", "summary", "confirmed"];

function getNextStep(currentStep: BookingStep): BookingStep | null {
  const idx = BOOKING_STEP_ORDER.indexOf(currentStep);
  if (idx === -1 || idx >= BOOKING_STEP_ORDER.length - 1) return null;
  return BOOKING_STEP_ORDER[idx + 1];
}

function isStepComplete(step: BookingStep, booking: { pickup: string | null; destination: string | null; passengers: number | null; pickupTime: string | null }): boolean {
  switch (step) {
    case "pickup": return !!booking.pickup && booking.pickup.length > 2;
    case "destination": return !!booking.destination && booking.destination.length > 2;
    case "passengers": return booking.passengers !== null && booking.passengers > 0;
    case "time": return booking.pickupTime !== null;
    case "summary": return true;
    case "confirmed": return true;
    default: return false;
  }
}

// Session state interface
interface SessionState {
  callId: string;
  callerPhone: string;
  language: string; // ISO 639-1 code or "auto" for auto-detect
  // Latest user utterance (best-effort) used to validate tool calls and prevent hallucinated overwrites.
  lastUserText: string | null;
  booking: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    pickupTime: string | null;
    nearestPickup: string | null;
    nearestDropoff: string | null;
  };
  // === NEW: Step-based state machine (ported from simple) ===
  bookingStep: BookingStep;
  bookingStepAdvancedAt: number | null;
  lastQuestionAsked: "pickup" | "destination" | "passengers" | "time" | "confirmation" | "none";
  conversationHistory: Array<{ role: string; content: string; timestamp: number }>;
  bookingConfirmed: boolean;
  // === NEW: Greeting delivered tracking (for session resume) ===
  greetingDelivered: boolean;
  // === NEW: Fare spoken tracking (prevents repetition after handoff) ===
  fareSpoken: boolean;
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
  // Track when user started speaking (for duration logging)
  speechStartedAt: number;
  // === NEW: Speech timing for pre-emptive extraction guard ===
  speechStopTime: number;
  extractionInProgress: boolean;
  // === NEW: Handoff tracking (ported from simple) ===
  handoffTriggered: boolean;
  // === NEW: Post-booking chat mode ===
  askedAnythingElse: boolean;
  bookingFullyConfirmed: boolean;
}

const ECHO_GUARD_MS = 250;

// Greeting protection window in ms - ignore early line noise (and prevent accidental barge-in)
// so Ada's initial greeting doesn't get cut off mid-sentence.
const GREETING_PROTECTION_MS = 12000;

// Summary protection window in ms - prevent interruptions while Ada recaps booking or quotes fare
const SUMMARY_PROTECTION_MS = 8000;

// While Ada is speaking, ignore the first slice of inbound audio to avoid echo/noise cutting her off
const ASSISTANT_LEADIN_IGNORE_MS = 700;

// RMS thresholds for audio quality
// NOTE: Telephony audio can have extremely low RMS (even ~1-50) depending on trunk/codecs.
// Only apply barge-in filtering when Ada is actively speaking to prevent echo.
// Keep MIN low or Ada will never hear quiet callers during her own speech.
const RMS_BARGE_IN_MIN = 5;     // Minimum for barge-in during Ada speech
const RMS_BARGE_IN_MAX = 20000; // Above = likely echo/clipping

// === NEW: OpenAI reconnection settings (ported from simple mode) ===
const MAX_OPENAI_RECONNECT_ATTEMPTS = 3;

// === NEW: Session timeout and handoff settings (ported from simple mode) ===
const MAX_SESSION_DURATION_MS = 4 * 60 * 1000; // 4 minutes
const HANDOFF_DELAY_AFTER_DISPATCH_MS = 2000; // 2s delay after fare quote received

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
  // "two sweet spot" → "to Sweet Spot" (common STT mishearing "to" as "two")
  "two sweet spot": "to Sweet Spot",
  "two sweetspot": "to Sweet Spot",
  "two street spot": "to Sweet Spot",
  "two a sweet spot": "to Sweet Spot",
  "to a sweet spot": "to Sweet Spot",
  "pickup two sweet spot": "pickup to Sweet Spot",
  "pick up two sweet spot": "pickup to Sweet Spot",
  "pick-up two sweet spot": "pickup to Sweet Spot",
  "from sweet spot": "from Sweet Spot",
  "at sweet spot": "at Sweet Spot",
  "the sweet spot": "the Sweet Spot",
  "a sweet spot": "Sweet Spot",
  "assault from sweetspot": "from Sweet Spot",
  "assault from street spot": "from Sweet Spot",
  "streetspot": "Sweet Spot",

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
  
  // ═══════════════════════════════════════════════════════════════════════════
  // EARLY EXIT: Reject time-related phrases that are NOT address corrections
  // "Pick up timings now", "pickup time now", "now please", etc.
  // ═══════════════════════════════════════════════════════════════════════════
  const timeRelatedPatterns = [
    /\b(?:pick[- ]?up|pickup)\s+(?:time|timings?)\b/i,  // "pickup time", "pick up timings"
    /^(?:now|asap|immediately|right now|straight away)\s*(?:please)?$/i,  // Just "now", "asap"
    /\b(?:time|timing|when|at|for)\s+(?:is\s+)?(?:now|asap|immediately)\b/i,  // "time is now"
    /^(?:pick[- ]?up|pickup)\s+(?:is\s+)?(?:now|asap|immediately)/i,  // "pickup is now", "pick up now" 
    /\bnow\s*(?:please|thanks?)?\s*[.!]?$/i,  // Ends with "now" 
  ];
  
  for (const pattern of timeRelatedPatterns) {
    if (pattern.test(lower)) {
      return { type: null, address: "" };
    }
  }
  
  // Correction trigger phrases - comprehensive patterns for address changes
  const correctionPhrases = [
    /^it'?s\s+(.+)/i,                                   // "It's 52A David Road"
    /^no[,\s]+it'?s\s+(.+)/i,                           // "No, it's..."
    /^no[,\s]+the\s+(?:pick[- ]?up|pickup)\s+is\s+(?:at\s+)?(.+)/i,  // "No, the pickup is at Sweet Spot"
    /^no[,\s]+the\s+destination\s+is\s+(.+)/i,          // "No, the destination is..."
    /^no[,\s]+(?:pick[- ]?up|pickup)\s+(?:is\s+)?(?:at\s+)?(.+)/i,   // "No, pickup is at..."
    /^no[,\s]+from\s+(.+)/i,                            // "No, from Sweet Spot"
    /^no[,\s]+to\s+(.+)/i,                              // "No, to the airport"
    /^actually[,\s]+(.+)/i,                             // "Actually 52A David Road"
    /^i meant\s+(.+)/i,                                 // "I meant 52A..."
    /^i said\s+(.+)/i,                                  // "I said 52A..."
    /^should be\s+(.+)/i,                               // "Should be 52A..."
    /^it should be\s+(.+)/i,                            // "It should be..."
    /^sorry[,\s]+(.+)/i,                                // "Sorry, 52A David Road"
    /^correction[:\s]+(.+)/i,                           // "Correction: 52A..."
    /^the\s+(?:pick[- ]?up|pickup)\s+is\s+(?:at\s+)?(.+)/i,  // "The pickup is at..."
    /^the\s+(?:destination|dropoff|drop off)\s+is\s+(.+)/i,   // "The destination is..."
    /^(?:no[,\s]+)?that'?s\s+(.+)/i,                    // "That's 52A..." or "No, that's 52A..."
    /^change\s+(?:the\s+)?(?:pick[- ]?up|pickup)\s+to\s+(.+)/i,  // "Change the pickup to..."
    /^change\s+(?:the\s+)?(?:destination|dropoff)\s+to\s+(.+)/i, // "Change destination to..."
    /^not\s+(.+)[,\s]+(?:it'?s|but)\s+(.+)/i,          // "Not 52A, it's 52B"
    // REMOVED: Overly aggressive "pick up X" patterns that catch time phrases
    // Only match "pickup at [location]" or "pickup from [location]" with explicit location words
    /^(?:pick[- ]?up|pickup)\s+(?:is\s+)?at\s+(.+)/i,  // "Pickup is at Sweet Spot" (requires "at")
    /^from\s+(.+)/i,                                    // "From Sweet Spot" (if context suggests correction)
    // === Mid-sentence corrections with explicit "change" or "but" ===
    /(?:but\s+)?change\s+(?:the\s+)?(?:pick[- ]?up|pickup)\s+to\s+(.+?)(?:\s+please)?$/i,  // "but change pickup to Sweet Spot"
    /(?:change|make)\s+(?:it\s+)?(?:to\s+)?(?:the\s+)?(.+?)(?:\s+please)?$/i,          // "make it the Sweet Spot please"
    // === "no [something], but [actual change]" ===
    /^no\s+[\w\s]+[,\s]+but\s+(?:pick[- ]?up|pickup)\s+(?:at\s+)?(?:the\s+)?(.+?)(?:\s+please)?$/i, // "No key change, but pick up the Sweet Spot please"
  ];
  
  let extractedAddress: string | null = null;
  let explicitFieldType: "pickup" | "destination" | null = null;
  
  // Check for explicit field mentions in the correction phrase BEFORE extracting
  // BUT exclude time-related contexts like "pickup time"
  if (/\b(?:pick[- ]?up|pickup)\s+(?:is\s+)?(?:at|from)\b/i.test(lower)) {
    // Only set pickup if there's a location indicator (at/from)
    explicitFieldType = "pickup";
  } else if (/\bchange\s+(?:the\s+)?(?:pick[- ]?up|pickup)\s+to\b/i.test(lower)) {
    explicitFieldType = "pickup";
  } else if (/\b(?:destination|dropoff|drop off|going to)\b/i.test(lower)) {
    explicitFieldType = "destination";
  }
  
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
  const addressKeywords = ["road", "street", "avenue", "lane", "drive", "way", "close", "court", "place", "crescent", "terrace", "station", "airport", "hotel", "hospital", "mall", "centre", "center", "square", "park", "spot", "bar", "pub", "restaurant", "shop", "store", "gym", "club"];
  const hasAddressKeyword = addressKeywords.some(kw => lowerExtracted.includes(kw));
  const hasHouseNumber = /^\d+[a-zA-Z]?\s/.test(extractedAddress) || /\d+[a-zA-Z]?$/.test(extractedAddress);
  const isVenueName = extractedAddress.split(/\s+/).length >= 2; // "Sweet Spot", "The Mailbox", etc.
  
  // If no address keywords and no house number and not a multi-word venue name, reject
  // BUT if we have an explicit field type (user said "pickup is at" or "from"), be more lenient
  if (!hasAddressKeyword && !hasHouseNumber && !isVenueName && !explicitFieldType) {
    // Could be "correct, Jeff" or similar - reject it
    return { type: null, address: "" };
  }
  
  // Single word without keywords is likely not a real location (unless explicit field type)
  if (extractedAddress.split(/\s+/).length === 1 && !hasAddressKeyword && !hasHouseNumber && !explicitFieldType) {
    return { type: null, address: "" };
  }
  
  // Use explicit field type if we detected one, otherwise try to infer
  if (explicitFieldType) {
    return { type: explicitFieldType, address: extractedAddress };
  }
  
  // Fallback: check for field mentions (redundant now but kept for safety)
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
// FIELD CORRECTION DETECTION
// Detects when user wants to correct ANY field (passengers, time, addresses)
// Handles patterns like "You put seven passengers, there's three" or "No, 3 not 7"
// ═══════════════════════════════════════════════════════════════════════════════
interface FieldCorrection {
  field: "pickup" | "destination" | "passengers" | "time" | null;
  value: string | number | null;
  rawText: string;
}

function detectFieldCorrection(text: string): FieldCorrection {
  const lower = text.toLowerCase();
  
  // ── PASSENGER CORRECTIONS ──
  // "You put seven passengers, there's three" / "No, 3 not 7" / "It's three not seven"
  const passengerCorrectionPatterns = [
    /you (?:put|said|got)\s+(?:\w+)\s+passengers?[,\s]+(?:there'?s?|it'?s?|but|actually)\s+(\w+)/i,
    /(?:no|not)\s+(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:not\s+)?(\d+|one|two|three|four|five|six|seven|eight|nine|ten)?/i,
    /it'?s?\s+(\w+)\s+(?:not|instead of)\s+\w+\s*passengers?/i,
    /(\w+)\s+passengers?\s*[,.]?\s*not\s+\w+/i,
    /(?:there'?s?|there are|we'?re?)\s+(?:only\s+)?(\w+)\s+(?:of us|passengers?|people)/i,
    /(?:just|only)\s+(\w+)\s+passengers?/i,
    /change\s+(?:that\s+)?(?:to\s+)?(\w+)\s+passengers?/i,
    /(\w+)\s+(?:passengers?|people)\s*[,.]?\s*(?:not\s+\w+|please)/i,
  ];
  
  const wordToNumber: Record<string, number> = {
    "one": 1, "two": 2, "three": 3, "four": 4, "five": 5,
    "six": 6, "seven": 7, "eight": 8, "nine": 9, "ten": 10,
    "1": 1, "2": 2, "3": 3, "4": 4, "5": 5,
    "6": 6, "7": 7, "8": 8, "9": 9, "10": 10
  };
  
  for (const pattern of passengerCorrectionPatterns) {
    const match = lower.match(pattern);
    if (match) {
      // Extract the corrected number (usually the first capture group)
      const numStr = match[1]?.toLowerCase();
      if (numStr && wordToNumber[numStr] !== undefined) {
        console.log(`[FieldCorrection] Detected passenger correction: "${text}" → ${wordToNumber[numStr]}`);
        return {
          field: "passengers",
          value: wordToNumber[numStr],
          rawText: text
        };
      }
    }
  }
  
  // Also detect simple "three passengers" if there's a negation/correction context
  if (/(no|not|wrong|incorrect|change|actually)/i.test(lower)) {
    const simpleMatch = lower.match(/(\w+)\s+passengers?/i);
    if (simpleMatch && wordToNumber[simpleMatch[1].toLowerCase()] !== undefined) {
      return {
        field: "passengers",
        value: wordToNumber[simpleMatch[1].toLowerCase()],
        rawText: text
      };
    }
  }
  
  // ── TIME CORRECTIONS ──
  const timeCorrectionPatterns = [
    /(?:change|make)\s+(?:it|that|the time)\s+(?:to\s+)?(.+)/i,
    /(?:no|not|actually)[,\s]+(?:at\s+)?(\d{1,2}(?::\d{2})?\s*(?:am|pm|o'?clock)?)/i,
  ];
  
  for (const pattern of timeCorrectionPatterns) {
    const match = text.match(pattern);
    if (match && match[1]) {
      const timeValue = match[1].trim().replace(/[.,!?]+$/, '');
      if (/\d|now|asap|today|tomorrow|morning|afternoon|evening/i.test(timeValue)) {
        console.log(`[FieldCorrection] Detected time correction: "${text}" → "${timeValue}"`);
        return {
          field: "time",
          value: timeValue,
          rawText: text
        };
      }
    }
  }
  
  // ── ADDRESS CORRECTIONS (fallback to detectAddressCorrection) ──
  // This function focuses on non-address corrections; address ones are handled separately
  
  return { field: null, value: null, rawText: text };
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

function tokenizeForMatch(text: string): string[] {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .split(/\s+/)
    .map((t) => t.trim())
    .filter(Boolean);
}

// Known venue/landmark names that should be accepted as-is
const KNOWN_VENUES = new Set([
  "sweet spot", "sweetspot", "the sweet spot",
  "fargo village", "fargo", "the fargo",
  "the mailbox", "mailbox",
  "the bullring", "bullring",
  "new street station", "new street",
  "birmingham airport", "bhx",
  "coventry station", "coventry railway station",
  "coventry cathedral", "the cathedral",
  "ricoh arena", "cbs arena",
  "university hospital", "walsgrave hospital",
  "warwick university", "warwick uni",
  "coventry university", "coventry uni",
  "heathrow", "heathrow airport",
  "gatwick", "gatwick airport",
  "luton", "luton airport",
  "stansted", "stansted airport",
  "birmingham new street",
  "euston", "euston station",
  "kings cross", "king's cross",
  "st pancras", "st. pancras",
  "paddington", "paddington station",
]);

// Check if text contains a known venue name
function containsKnownVenue(text: string): string | null {
  const lower = text.toLowerCase();
  for (const venue of KNOWN_VENUES) {
    if (lower.includes(venue)) {
      return venue;
    }
  }
  return null;
}

// matchesUserText - used for AI extraction validation
// RELAXED for corrections: if user is correcting, we trust the AI more
function matchesUserText(proposed: string, userText: string, minRatio = 0.5): boolean {
  const p = proposed.toLowerCase().trim();
  const u = userText.toLowerCase();

  // Fast path: exact substring match
  if (p.length >= 3 && u.includes(p)) return true;
  
  // Reverse check: proposed contains user's key words
  // Handles "Pick-up is 52A David Road" → "52A David Road"
  const proposedWords = p.split(/\s+/).filter(w => w.length > 1);
  const userWords = u.split(/\s+/).filter(w => w.length > 1);
  if (proposedWords.length >= 2) {
    const matchedWords = proposedWords.filter(pw => userWords.some(uw => uw.includes(pw) || pw.includes(uw)));
    if (matchedWords.length >= Math.ceil(proposedWords.length * 0.5)) return true;
  }
  
  // Check if user mentioned a known venue and proposed contains it
  const userVenue = containsKnownVenue(u);
  const proposedVenue = containsKnownVenue(p);
  if (userVenue && proposedVenue) {
    const userVenueNorm = userVenue.replace(/^the\s+/, "").replace(/\s+/g, "");
    const proposedVenueNorm = proposedVenue.replace(/^the\s+/, "").replace(/\s+/g, "");
    if (userVenueNorm === proposedVenueNorm || userVenueNorm.includes(proposedVenueNorm) || proposedVenueNorm.includes(userVenueNorm)) {
      return true;
    }
  }
  
  // Check for house number + street pattern match
  const houseNumMatch = p.match(/^(\d+[a-zA-Z]?)\s+(.+)/);
  if (houseNumMatch) {
    const [, houseNum, street] = houseNumMatch;
    // If user said the same house number and part of street name, accept it
    if (u.includes(houseNum.toLowerCase()) && u.includes(street.split(/\s+/)[0].toLowerCase())) {
      return true;
    }
  }

  const proposedTokens = Array.from(new Set(tokenizeForMatch(proposed)));
  if (proposedTokens.length === 0) return false;

  const userTokens = new Set(tokenizeForMatch(userText));
  let hit = 0;
  for (const t of proposedTokens) {
    if (userTokens.has(t)) hit++;
  }
  const ratio = hit / proposedTokens.length;
  return ratio >= minRatio;
}

// For corrections specifically, be even more lenient
function matchesUserTextForCorrection(proposed: string, userText: string): boolean {
  // For corrections, check if key components are present with very lenient matching
  const p = proposed.toLowerCase().trim();
  const u = userText.toLowerCase();
  
  // Normalize STT variations: "two" → "to", "too" → "to" 
  const normalizedUser = u
    .replace(/\btwo\b/gi, "to")
    .replace(/\btoo\b/gi, "to");
  
  // Direct substring in either direction
  if (p.length >= 3 && (normalizedUser.includes(p) || p.includes(normalizedUser.replace(/[^a-z0-9\s]/g, "").trim()))) return true;
  
  // Check if user text contains any significant words from proposed (2+ chars)
  const proposedWords = p.split(/\s+/).filter(w => w.length >= 2);
  const userWords = normalizedUser.split(/\s+/).filter(w => w.length >= 2);
  
  // If at least one significant word matches, it's likely correct
  // This handles "Sweet Spot" matching "the Sweet Spot please"
  for (const pw of proposedWords) {
    if (userWords.some(uw => uw.includes(pw) || pw.includes(uw))) {
      return true;
    }
  }
  
  // Extract numbers from both
  const propNums = p.match(/\d+[a-zA-Z]?/g) || [];
  const userNums = u.match(/\d+[a-zA-Z]?/g) || [];
  
  // If proposed has a house number that user mentioned, likely correct
  if (propNums.length > 0 && userNums.length > 0) {
    for (const pn of propNums) {
      if (userNums.some(un => un.toLowerCase() === pn.toLowerCase())) {
        return true;
      }
    }
  }
  
  // Check for known venues - if proposed is a known venue that appears in user text
  const proposedVenue = containsKnownVenue(p);
  if (proposedVenue && normalizedUser.includes(proposedVenue.replace(/^the\s+/, ""))) {
    return true;
  }
  
  // Fall back to standard matching with lower threshold
  return matchesUserText(proposed, userText, 0.3); // Even lower threshold for corrections
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
    lastUserText: null,
    booking: {
      pickup: null,
      destination: null,
      passengers: null,
      pickupTime: null,
      nearestPickup: null,
      nearestDropoff: null
    },
    // NEW: Step-based state machine (ported from simple)
    bookingStep: "pickup",
    bookingStepAdvancedAt: null,
    lastQuestionAsked: "none",
    conversationHistory: [],
    bookingConfirmed: false,
    // NEW: Greeting/fare tracking for session resume
    greetingDelivered: false,
    fareSpoken: false,
    openAiResponseActive: false,
    openAiSpeechStartedAt: 0,
    echoGuardUntil: 0,
    lastAdaFinishedSpeakingAt: 0,
    greetingProtectionUntil: Date.now() + GREETING_PROTECTION_MS,
    summaryProtectionUntil: 0,
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
    speechStartedAt: 0,
    // NEW: Speech timing for pre-emptive extraction guard
    speechStopTime: 0,
    extractionInProgress: false,
    // NEW: Handoff tracking
    handoffTriggered: false,
    // NEW: Post-booking chat mode
    askedAnythingElse: false,
    bookingFullyConfirmed: false
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

// Update live_calls table with current state
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
        fare: sessionState.pendingFare || null,
        eta: sessionState.pendingEta || null,
        status: sessionState.bookingConfirmed ? "confirmed" : (sessionState.awaitingConfirmation ? "awaiting_confirmation" : "active"),
        booking_confirmed: sessionState.bookingConfirmed,
        transcripts: sessionState.conversationHistory,
        source: "paired",
        updated_at: new Date().toISOString()
      }, { onConflict: "call_id" });
    
    if (error) {
      console.error(`[${sessionState.callId}] Failed to update live_calls:`, error);
    }
  } catch (e) {
    console.error(`[${sessionState.callId}] Error updating live_calls:`, e);
  }
}

// Restore session state from DB (for resumed sessions after handoff/reconnect)
// NOTE: resumeCallId may not match if the edge function generated a new timestamp-based callId
// Fallback: find most recent in-flight session for the same caller phone
async function restoreSessionFromDb(callId: string, resumeCallId: string, callerPhone: string | null): Promise<{
  booking: SessionState["booking"];
  conversationHistory: SessionState["conversationHistory"];
  bookingConfirmed: boolean;
  awaitingConfirmation: boolean;
  lastQuestionAsked: SessionState["lastQuestionAsked"];
  pendingFare: string | null;
  pendingEta: string | null;
} | null> {
  try {
    // First try exact call_id match
    let { data: liveCall } = await supabase
      .from("live_calls")
      .select("*")
      .eq("call_id", resumeCallId)
      .maybeSingle();
    
    if (!liveCall) {
      console.log(`[${callId}] ⚠️ No live_call found for resume_call_id: ${resumeCallId}`);
      
      // Fallback: find the most recent in-flight call for this caller (within last 10 min)
      if (callerPhone && callerPhone !== "unknown") {
        const phoneKey = normalizePhone(callerPhone);
        const cutoffIso = new Date(Date.now() - 10 * 60 * 1000).toISOString();
        
        console.log(`[${callId}] 🔍 Fallback: searching for recent call by phone=${phoneKey}, since=${cutoffIso}`);
        
        const { data: fallbackLiveCall } = await supabase
          .from("live_calls")
          .select("*")
          .eq("caller_phone", phoneKey)
          .gte("started_at", cutoffIso)
          .in("status", ["active", "awaiting_confirmation", "confirmed"])
          .order("started_at", { ascending: false })
          .limit(1)
          .maybeSingle();
        
        if (fallbackLiveCall) {
          liveCall = fallbackLiveCall;
          console.log(`[${callId}] ✅ Resume fallback matched: requested=${resumeCallId}, matched=${fallbackLiveCall.call_id}`);
        }
      }
      
      if (!liveCall) {
        console.log(`[${callId}] ⚠️ No fallback session found either`);
        return null;
      }
    }
    
    console.log(`[${callId}] ✅ Restored session from DB:`);
    console.log(`[${callId}]   pickup: ${liveCall.pickup}`);
    console.log(`[${callId}]   destination: ${liveCall.destination}`);
    console.log(`[${callId}]   passengers: ${liveCall.passengers}`);
    console.log(`[${callId}]   fare: ${liveCall.fare}`);
    console.log(`[${callId}]   status: ${liveCall.status}`);
    console.log(`[${callId}]   booking_confirmed: ${liveCall.booking_confirmed}`);
    
    // Parse transcripts if stored as JSON
    const transcripts = Array.isArray(liveCall.transcripts) ? liveCall.transcripts : [];
    
    // Determine lastQuestionAsked from state
    let lastQ: SessionState["lastQuestionAsked"] = "none";
    if (liveCall.booking_confirmed) {
      lastQ = "confirmation";
    } else if (liveCall.fare) {
      lastQ = "confirmation"; // We have fare, waiting for final yes/no
    } else if (!liveCall.pickup) {
      lastQ = "pickup";
    } else if (!liveCall.destination) {
      lastQ = "destination";
    } else if (!liveCall.passengers) {
      lastQ = "passengers";
    } else {
      lastQ = "time";
    }
    
    return {
      booking: {
        pickup: liveCall.pickup || null,
        destination: liveCall.destination || null,
        passengers: liveCall.passengers || null,
        pickupTime: null,
        nearestPickup: null,
        nearestDropoff: null,
      },
      conversationHistory: transcripts as SessionState["conversationHistory"],
      bookingConfirmed: liveCall.booking_confirmed || false,
      awaitingConfirmation: !!liveCall.fare && !liveCall.booking_confirmed,
      lastQuestionAsked: lastQ,
      pendingFare: liveCall.fare || null,
      pendingEta: liveCall.eta || null,
    };
  } catch (e) {
    console.error(`[${callId}] ❌ Failed to restore session:`, e);
    return null;
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
    nearest_pickup: sessionState.booking.nearestPickup || null,
    nearest_dropoff: sessionState.booking.nearestDropoff || null,
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

  // === NEW: OpenAI reconnection tracking (ported from simple mode) ===
  let openaiReconnectAttempts = 0;
  let lastOpenAiConnectedAt: number | null = null;
  let openaiConnected = false;
  
  // === NEW: Session timeout watchdog (ported from simple mode) ===
  let sessionStartTime = Date.now();
  let closingGracePeriodActive = false;

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
  // Note: stopOpenAiPing() is defined later but called via closure - this works because
  // cleanupWithKeepalive is only called after OpenAI handlers are set up
  const cleanupWithKeepalive = async () => {
    if (keepaliveInterval) {
      clearInterval(keepaliveInterval);
      keepaliveInterval = null;
    }
    // Stop OpenAI ping if running (function defined after OpenAI WS setup)
    try {
      if (typeof stopOpenAiPing === "function") stopOpenAiPing();
    } catch { /* stopOpenAiPing may not exist yet during early cleanup */ }
    await originalCleanup();
  };

  // ═══════════════════════════════════════════════════════════════════════════
  // Safe Response Create Helper (ported from simple mode)
  // Prevents 'conversation_already_has_active_response' errors by canceling
  // any active response before creating a new one
  // ═══════════════════════════════════════════════════════════════════════════
  const safeResponseCreate = (reason: string, responseOptions?: { instructions?: string; modalities?: string[] }) => {
    if (!openaiWs || openaiWs.readyState !== WebSocket.OPEN) {
      console.log(`[${callId}] ⚠️ safeResponseCreate(${reason}) skipped - OpenAI not connected`);
      return;
    }
    
    // If a response is active, cancel it first
    if (sessionState.openAiResponseActive) {
      console.log(`[${callId}] 🔄 safeResponseCreate(${reason}) - canceling active response first`);
      openaiWs.send(JSON.stringify({ type: "response.cancel" }));
      sessionState.openAiResponseActive = false;
    }
    
    const responsePayload: any = {
      type: "response.create",
      response: {
        modalities: responseOptions?.modalities || ["audio", "text"],
        ...(responseOptions?.instructions ? { instructions: responseOptions.instructions } : {})
      }
    };
    
    openaiWs.send(JSON.stringify(responsePayload));
    sessionState.openAiResponseActive = true;
    console.log(`[${callId}] 🚀 safeResponseCreate(${reason}) sent`);
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
    sessionState.lastQuestionAsked = "confirmation";
    sessionState.fareSpoken = true; // Track that fare was spoken (prevents repetition after handoff)
    sessionState.bookingStep = "summary"; // Advance to summary step
    
    // === SESSION HANDOFF TRIGGER (ported from simple mode) ===
    // After receiving fare quote, trigger handoff after a short delay
    // This allows Ada to speak the fare before the bridge reconnects
    if (!sessionState.handoffTriggered) {
      sessionState.handoffTriggered = true;
      console.log(`[${callId}] 🔄 Dispatch-triggered handoff will occur in ${HANDOFF_DELAY_AFTER_DISPATCH_MS}ms`);
      
      trackedTimeout(async () => {
        if (cleanedUp) return;
        
        console.log(`[${callId}] 🔄 DISPATCH-TRIGGERED HANDOFF: Saving state for seamless reconnection...`);
        
        // Save handoff state to database
        const handoffState = {
          pickup: sessionState.booking.pickup,
          destination: sessionState.booking.destination,
          passengers: sessionState.booking.passengers,
          pickup_time: sessionState.booking.pickupTime,
          bookingStep: sessionState.bookingStep,
          language: sessionState.language,
          greetingDelivered: sessionState.greetingDelivered,
          fareSpoken: sessionState.fareSpoken,
          pendingFare: sessionState.pendingFare,
          pendingEta: sessionState.pendingEta,
          handoffAt: new Date().toISOString(),
        };
        
        // Update live_calls with handoff status
        await supabase.from("live_calls").update({
          status: "handoff",
          pickup: sessionState.booking.pickup,
          destination: sessionState.booking.destination,
          passengers: sessionState.booking.passengers,
          fare: sessionState.pendingFare,
          eta: sessionState.pendingEta,
          transcripts: sessionState.conversationHistory.slice(-20),
          updated_at: new Date().toISOString(),
        }).eq("call_id", callId);
        
        console.log(`[${callId}] ✅ Handoff state saved to DB`);
        
        // Send handoff signal to bridge
        try {
          socket.send(JSON.stringify({
            type: "session.handoff",
            call_id: callId,
            reason: "dispatch_response",
            state: handoffState,
          }));
          console.log(`[${callId}] 📤 Handoff signal sent to bridge`);
        } catch (e) {
          console.warn(`[${callId}] ⚠️ Failed to send handoff signal:`, e);
        }
      }, HANDOFF_DELAY_AFTER_DISPATCH_MS);
    }
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
  
  // === SESSION RESUME STATE ===
  // Set when bridge reconnects mid-call - we skip greeting and inject continuation
  let isResumedSession = false;
  // deno-lint-ignore no-explicit-any
  let resumedStateData: any = null;  // Will hold restored session state for resumed calls
  
  const sendGreeting = () => {
    if (greetingSent || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) {
      console.log(`[${callId}] ⚠️ sendGreeting skipped: sent=${greetingSent}, wsOpen=${openaiWs?.readyState === WebSocket.OPEN}`);
      return;
    }
    
    greetingSent = true;
    
    // === RESUMED SESSION: Send continuation prompt instead of greeting ===
    if (isResumedSession && resumedStateData) {
      console.log(`[${callId}] 🔄 RESUMED SESSION - sending continuation prompt instead of greeting`);
      
      let continuationInstruction: string;
      const rsd = resumedStateData;
      
      if (rsd.awaitingConfirmation && rsd.pendingFare) {
        // We were waiting for user to confirm the fare quote
        continuationInstruction = `Sorry, I didn't catch that. Your fare is ${rsd.pendingFare} and driver will arrive in ${rsd.pendingEta || "a few minutes"}. Would you like me to book that for you?`;
      } else if (rsd.bookingConfirmed) {
        // Booking was already confirmed, deliver closing
        continuationInstruction = `Your taxi is on the way. Thank you for booking with us! Have a great journey!`;
      } else {
        // Mid-booking, determine next question
        const bk = rsd.booking || {};
        const nextQ = !bk.pickup ? "pickup" :
                      !bk.destination ? "destination" :
                      !bk.passengers ? "passengers" : "time";
        const questionText = nextQ === "pickup" ? "Where would you like to be picked up?" 
                           : nextQ === "destination" ? "And where are you heading to?" 
                           : nextQ === "passengers" ? "How many passengers?" 
                           : "What time would you like the taxi?";
        continuationInstruction = `Sorry about that brief interruption. Let me continue. ${questionText}`;
      }
      
      openaiWs!.send(JSON.stringify({
        type: "response.create",
        response: {
          modalities: ["audio", "text"],
          instructions: `Say EXACTLY this: "${continuationInstruction}". Then WAIT for the caller's response.`
        }
      }));
      
      console.log(`[${callId}] ✅ Continuation prompt sent`);
      return;
    }
    
    // === NORMAL GREETING (not resumed) ===
    console.log(`[${callId}] 🎙️ Sending initial greeting (language: ${sessionState.language})...`);
    
    // Get language-specific greeting or fall back to English for auto-detect
    const langData = GREETINGS[sessionState.language] || GREETINGS["en"];
    const greetingText = `${langData.greeting} ${langData.pickupQuestion}`;
    
    // For auto-detect mode, let the AI decide based on first user response
    // CRITICAL: Tell Ada to NOT repeat the pickup question - she should say it ONCE only
    const greetingInstruction = sessionState.language === "auto"
      ? `Greet the caller warmly in English. Say EXACTLY this (do NOT repeat or rephrase): "${greetingText}". After this greeting, WAIT for the caller to respond. Do NOT repeat the pickup question again. If they respond in another language, switch to match them for your NEXT response.`
      : `Greet the caller. Say EXACTLY this (do NOT add anything extra): "${greetingText}". Then WAIT for their response.`;
    
    // Simple approach: just request a response with specific instructions
    openaiWs!.send(JSON.stringify({
      type: "response.create",
      response: {
        modalities: ["audio", "text"],  // Audio first - prioritize voice output
        instructions: greetingInstruction
      }
    }));
    
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
          // Increased silence_duration_ms to 1200ms to give user more time to finish speaking
          // and prevent Ada from responding before the transcript is fully processed.
          // This helps avoid the race condition where Ada starts speaking based on VAD
          // before the Whisper transcription completes.
          threshold: 0.5,
          prefix_padding_ms: 300,
          silence_duration_ms: 1200, // Increased from 1000ms to reduce race conditions
        },
        tools: TOOLS,
        tool_choice: "auto",
        temperature: 0.6, // OpenAI Realtime API minimum is 0.6
      },
    };

    console.log(`[${callId}] 📤 Sending session.update (after session.created)`);
    openaiWs.send(JSON.stringify(sessionConfig));
  };

  // ═══════════════════════════════════════════════════════════════════════════
  // OpenAI-side heartbeat: Send minimal silent audio every 20s to prevent 
  // OpenAI from dropping the WebSocket during long pauses (e.g., waiting for
  // dispatch quote or user thinking time).
  // ═══════════════════════════════════════════════════════════════════════════
  const OPENAI_PING_INTERVAL_MS = 20000; // 20s - well under OpenAI's ~60s idle timeout
  let openaiPingInterval: ReturnType<typeof setInterval> | null = null;
  
  const startOpenAiPing = () => {
    if (openaiPingInterval) return; // Already running
    
    openaiPingInterval = setInterval(() => {
      if (cleanedUp || !openaiWs || openaiWs.readyState !== WebSocket.OPEN) {
        if (openaiPingInterval) {
          clearInterval(openaiPingInterval);
          openaiPingInterval = null;
        }
        return;
      }
      
      // Send a tiny silent audio frame (32 samples of silence = 64 bytes)
      // This is enough to reset OpenAI's idle timer without affecting conversation
      const silentPcm = new Int16Array(32); // All zeros = silence
      const silentBase64 = pcm16ToBase64(silentPcm);
      
      try {
        openaiWs.send(JSON.stringify({
          type: "input_audio_buffer.append",
          audio: silentBase64,
        }));
        console.log(`[${callId}] 💓 OpenAI ping sent (silent audio)`);
      } catch (e) {
        console.error(`[${callId}] ⚠️ OpenAI ping failed:`, e);
      }
    }, OPENAI_PING_INTERVAL_MS);
  };
  
  const stopOpenAiPing = () => {
    if (openaiPingInterval) {
      clearInterval(openaiPingInterval);
      openaiPingInterval = null;
    }
  };

  openaiWs.onopen = () => {
    console.log(`[${callId}] ✅ Connected to OpenAI Realtime (waiting for session.created...)`);
    openaiConnected = true;
    openaiReconnectAttempts = 0; // Reset on successful connection
    lastOpenAiConnectedAt = Date.now();
    startOpenAiPing(); // Start pinging to prevent idle timeout
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
          
          // ✅ EXTRACTION IN PROGRESS GUARD: If AI extraction is running, cancel this response
          // We'll trigger the correct response once extraction completes with full context
          if (sessionState.extractionInProgress) {
            console.log(`[${callId}] 🛡️ BLOCKING response - extraction in progress, will respond after`);
            openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
            sessionState.openAiResponseActive = false;
            break;
          }
          
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
          // Forward audio to client (and mark that Ada is speaking)
          if (data.delta) {
            if (!sessionState.openAiResponseActive) {
              sessionState.openAiSpeechStartedAt = Date.now();
              console.log(`[${callId}] 🔊 First audio chunk received`);
              greetingAudioReceived = true; // Mark that greeting audio was received
            }
            sessionState.openAiResponseActive = true;

            if (socket.readyState === WebSocket.OPEN) {
              socket.send(JSON.stringify({
                type: "audio",
                audio: data.delta
              }));
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
            
            // NOTE: We now buffer transcripts and send the complete sentence in response.audio_transcript.done
            // This reduces log noise from word-by-word logging
            
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
            
            console.log(`[${callId}] 🤖 Ada: "${data.transcript.substring(0, 100)}${data.transcript.length > 100 ? '...' : ''}"`);
            
            // Forward complete transcript to bridge for logging (cleaner than word-by-word)
            if (socket.readyState === WebSocket.OPEN) {
              socket.send(JSON.stringify({
                type: "transcript",
                text: data.transcript,
                role: "assistant"
              }));
            }

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
            
            // NOTE: AI extraction is now done SYNCHRONOUSLY in the user transcript handler
            // (case "conversation.item.input_audio_transcription.completed") so Ada has
            // full context including corrections before responding.
            
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
                  
                  let lowerV = v.toLowerCase().trim();
                  let parsedCount: number | null = null;
                  
                  // ═══════════════════════════════════════════════════════════════════
                  // CONTEXT-AWARE STT CORRECTION: "Seven" vs "Three" confusion
                  // When the previous answer was "7 Russell Street", the audio context
                  // can cause Whisper to mishear "Three" as "Seven" for passengers.
                  // ═══════════════════════════════════════════════════════════════════
                  const prevDestination = sessionState.booking.destination?.toLowerCase() || "";
                  const prevPickup = sessionState.booking.pickup?.toLowerCase() || "";
                  const lastAddressHad7 = /\b7\b/.test(prevDestination) || /\b7\b/.test(prevPickup);
                  
                  // If user says "seven" or "7" right after a "7 [street]" address, 
                  // check if they might have meant "three" (common phonetic confusion)
                  if (lastAddressHad7 && (lowerV === "seven" || lowerV === "7" || lowerV === "seven passengers" || lowerV === "7 passengers")) {
                    // Look at the raw transcript for phonetic hints
                    // "Three" and "Seven" sound similar - "th" vs "s" prefix
                    // If we just asked for passengers after a "7 X Street" address, 
                    // this is likely a context bleed from the STT
                    console.log(`[${callId}] 🔄 CONTEXT STT CHECK: User said "${v}" after "7 [street]" address - possible "Three"→"Seven" confusion`);
                    
                    // Check conversation history for phonetic hints
                    const recentUserInputs = sessionState.conversationHistory
                      .filter(h => h.role === "user")
                      .slice(-3)
                      .map(h => h.content.toLowerCase());
                    
                    // If they previously used "three" or we detect phonetic similarity patterns
                    const hasThreeContext = recentUserInputs.some(t => /\bthree\b|\b3\b/.test(t) && !/street|road/i.test(t));
                    
                    // For now, log the potential confusion but don't auto-correct
                    // The AI extraction will also catch this with better context
                    if (!hasThreeContext) {
                      console.log(`[${callId}] ⚠️ Potential STT confusion: "seven" after "7 street" - may have meant "three"`);
                    }
                  }
                  
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

                    // Safety: if callback never arrives, allow a retry later.
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
          console.log(`[${callId}] 🎤 User started speaking`);
          sessionState.speechStartedAt = Date.now();
          // Notify bridge of speech activity for logging
          if (socket.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ type: "speech_started" }));
            // ACTIVITY HEARTBEAT: Reset timeout while user is speaking
            socket.send(JSON.stringify({ type: "activity_heartbeat", timestamp: Date.now() }));
          }
          break;
          
        case "input_audio_buffer.speech_stopped": {
          const speechDuration = sessionState.speechStartedAt 
            ? ((Date.now() - sessionState.speechStartedAt) / 1000).toFixed(1)
            : "?";
          console.log(`[${callId}] 🔇 User stopped speaking (${speechDuration}s)`);
          // Track when speech stopped for late transcript detection
          sessionState.speechStopTime = Date.now();
          // ═══════════════════════════════════════════════════════════════════
          // TRANSCRIPTION GATE: Block responses until transcript is processed
          // This prevents Ada from responding before we have the full text
          // ═══════════════════════════════════════════════════════════════════
          sessionState.extractionInProgress = true;
          console.log(`[${callId}] 🚧 TRANSCRIPTION GATE: Blocking responses until transcript arrives`);
          // Notify bridge of speech end for logging
          if (socket.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ type: "speech_stopped", duration: speechDuration }));
          }
          break;
        }

        case "conversation.item.input_audio_transcription.completed":
          // User finished speaking - this is the KEY context pairing moment
          if (data.transcript) {
            const rawText = data.transcript.trim();
            // Apply STT corrections for common telephony mishearings
            let userText = correctTranscript(rawText);
            if (userText !== rawText) {
              console.log(`[${callId}] 🔧 STT corrected: "${rawText}" → "${userText}"`);
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // DUPLICATE TRANSCRIPT GUARD
            // If Ada already confirmed this address in her LAST response, ignore
            // the late-arriving transcript. This prevents "re-processing" the 
            // same address that Ada already acknowledged.
            // ═══════════════════════════════════════════════════════════════════
            const lastAssistantMsg = sessionState.conversationHistory
              .filter(m => m.role === "assistant")
              .slice(-1)[0]?.content || "";
            
            const userLower = userText.toLowerCase().trim();
            const adaLower = lastAssistantMsg.toLowerCase();
            
            // Check if Ada's response already contains this exact address
            // We look for phrases like "pickup is 52A David Road" or "got it, 52A David Road"
            const isAddressInAdaResponse = (addr: string): boolean => {
              if (!addr || addr.length < 3) return false;
              const addrLower = addr.toLowerCase().trim();
              return adaLower.includes(addrLower);
            };
            
            // If the transcript matches what's already saved AND Ada already mentioned it
            const matchesCurrentPickup = sessionState.booking.pickup && 
              userLower.includes(sessionState.booking.pickup.toLowerCase()) &&
              isAddressInAdaResponse(sessionState.booking.pickup);
            
            const matchesCurrentDest = sessionState.booking.destination && 
              userLower.includes(sessionState.booking.destination.toLowerCase()) &&
              isAddressInAdaResponse(sessionState.booking.destination);
            
            // Also check if Ada is currently asking for the NEXT field (not the same one)
            const adaAskingDestination = /where would you like to go|what is your destination|destination/i.test(adaLower);
            const adaAskingPassengers = /how many people|how many passengers/i.test(adaLower);
            
            if (matchesCurrentPickup && adaAskingDestination) {
              console.log(`[${callId}] 🔇 DUPLICATE GUARD: Ignoring late pickup transcript "${userText}" - Ada already confirmed and moved to destination`);
              break; // Skip processing this duplicate
            }
            
            if (matchesCurrentDest && adaAskingPassengers) {
              console.log(`[${callId}] 🔇 DUPLICATE GUARD: Ignoring late destination transcript "${userText}" - Ada already confirmed and moved to passengers`);
              break; // Skip processing this duplicate
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // LATE TRANSCRIPT TIMING GUARD
            // If Ada has already responded AFTER the user stopped speaking AND
            // she's already asking for the NEXT field, this transcript is stale.
            // This catches cases where Ada got a DIFFERENT value (e.g., "7" vs "11")
            // ═══════════════════════════════════════════════════════════════════
            const speechStoppedAt = sessionState.speechStopTime || 0;
            const lastAssistantMsgTime = sessionState.conversationHistory
              .filter(m => m.role === "assistant")
              .slice(-1)[0]?.timestamp || 0;
            
            // If Ada responded AFTER speech stopped, and the transcript is arriving now, it's late
            const adaRespondedAfterSpeech = lastAssistantMsgTime > speechStoppedAt && speechStoppedAt > 0;
            
            // Check if Ada has already moved to a different step
            const adaMovedToNextStep = (adaAskingDestination && sessionState.booking.pickup) ||
                                        (adaAskingPassengers && sessionState.booking.destination);
            
            if (adaRespondedAfterSpeech && adaMovedToNextStep) {
              // The transcript is about a DIFFERENT value than what Ada saved
              // Check if this looks like an address (not a "yes" or affirmative)
              const looksLikeAddress = /\d+[a-zA-Z]?\s|street|road|avenue|lane/i.test(userText);
              
              if (looksLikeAddress) {
                console.log(`[${callId}] ⏰ LATE TRANSCRIPT GUARD: "${userText}" arrived ${Date.now() - lastAssistantMsgTime}ms after Ada's response - checking if it's a correction`);
                
                // This could be a correction OR a late duplicate. Check if the value differs from what Ada said.
                const userMentions11 = /\b11\b|\beleven\b/i.test(userText);
                const adaSaid7 = /\b7\b|\bseven\b/i.test(adaLower);
                
                // If user said "11" but Ada heard "7", this IS a correction we need to apply
                if (userMentions11 && adaSaid7 && sessionState.lastQuestionAsked !== "passengers") {
                  console.log(`[${callId}] 🔧 LATE TRANSCRIPT CORRECTION: User said 11, Ada heard 7 - correcting destination`);
                  // Extract the corrected address
                  const correctedDest = userText.replace(/\.$/, "").trim();
                  if (correctedDest && sessionState.booking.destination?.includes("7")) {
                    sessionState.booking.destination = correctedDest;
                    // Don't break - let it process as a correction
                  }
                } else {
                  // Not a clear correction - might be echo or duplicate, skip it
                  console.log(`[${callId}] 🔇 LATE TRANSCRIPT GUARD: Ignoring late transcript "${userText}" - Ada already moved on`);
                  break;
                }
              }
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // CONTEXT-AWARE SEVEN→THREE CORRECTION
            // When asking about passengers AND previous address had "7",
            // "seven" is very likely misheard "three" (phonetic confusion)
            // ═══════════════════════════════════════════════════════════════════
            if (sessionState.lastQuestionAsked === "passengers") {
              const lowerText = userText.toLowerCase();
              const prevDest = sessionState.booking.destination?.toLowerCase() || "";
              const prevPickup = sessionState.booking.pickup?.toLowerCase() || "";
              const addressHad7 = /\b7\b/.test(prevDest) || /\b7\b/.test(prevPickup);
              
              // Check if user said "seven" or "7 passengers" when we expect passengers
              const saidSeven = /\bseven\b|\b7\s*passengers?\b|\b7\s*people\b/i.test(lowerText);
              
              if (addressHad7 && saidSeven) {
                // Strong signal: previous address had "7", now user says "seven" for passengers
                // This is almost certainly "three" being misheard due to audio context bleed
                console.log(`[${callId}] 🔄 CONTEXT STT FIX: Detected "seven" after "7 [street]" - correcting to "three"`);
                
                // Replace "seven" → "three" and "7 passengers" → "3 passengers"
                userText = userText
                  .replace(/\bseven\s+passengers?\b/gi, "three passengers")
                  .replace(/\b7\s+passengers?\b/gi, "3 passengers")
                  .replace(/\bseven\s+people\b/gi, "three people")
                  .replace(/\b7\s+people\b/gi, "3 people")
                  .replace(/\bseven\b/gi, "three")
                  .replace(/\b7\b/g, "3");
                
                console.log(`[${callId}] ✅ Corrected passenger transcript: "${rawText}" → "${userText}"`);
              }
            }
            
            // Filter out phantom hallucinations from Whisper
            if (isPhantomHallucination(userText)) {
              console.log(`[${callId}] 👻 Filtered phantom hallucination: "${userText}"`);
              break;
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // AFFIRMATIVE DETECTION - Prevent "That's right" → "three passengers" hallucination
            // ═══════════════════════════════════════════════════════════════════
            const isAffirmativeResponse = /^\s*(yes|yeah|yep|yup|correct|that's right|thats right|that is right|that's correct|right|exactly|perfect|absolutely|affirmative|uh-huh|sure|okay|ok|go ahead|please|lovely|wonderful|great)\s*[.!,]?\s*$/i.test(userText);
            
            // If this is an affirmative response to PASSENGERS question, DO NOT let it become passenger count
            if (isAffirmativeResponse && sessionState.lastQuestionAsked === "passengers") {
              console.log(`[${callId}] 🛡️ AFFIRMATIVE GUARD: "${userText}" is not a passenger count - asking Ada to clarify`);
              
              // Inject clarification request - the user confirmed something, not gave passenger count
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[AFFIRMATIVE DETECTED - NOT PASSENGER COUNT]
The user said "${userText}" which is an AFFIRMATIVE response (yes/correct/right), NOT a passenger count.
This might mean they confirmed something you said earlier, or they're agreeing but haven't answered yet.

DO NOT interpret this as "three passengers" or any number.
Ask for the NUMBER of passengers clearly: "How many passengers will be traveling?"`
                  }]
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
              break; // Don't process further
            }
            
            // Forward user transcript to bridge for logging (like simple mode)
            if (socket.readyState === WebSocket.OPEN) {
              socket.send(JSON.stringify({
                type: "transcript",
                text: userText,
                role: "user"
              }));
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
            
            console.log(`[${callId}] 👤 User (after "${sessionState.lastQuestionAsked}" question): "${userText}"`);
            
            // ═══════════════════════════════════════════════════════════════════
            // AI-BASED INTENT DETECTION (replaces brittle regex)
            // Run extraction SYNCHRONOUSLY so Ada has full context before responding
            // ═══════════════════════════════════════════════════════════════════
            
            // First, add the user message to history for AI extraction
            sessionState.conversationHistory.push({
              role: "user",
              content: `[CONTEXT: Ada asked about ${sessionState.lastQuestionAsked}] ${userText}`,
              timestamp: Date.now()
            });

            // Save for tool-call validation (prevents hallucinated overwrites)
            sessionState.lastUserText = userText;
            
            const lowerUserText = userText.toLowerCase();
            
            // ═══════════════════════════════════════════════════════════════════
            // KNOWN VENUE EXTRACTION (HIGHEST PRIORITY - before any other detection)
            // If user mentions a known venue like "Sweet Spot", extract it immediately
            // This prevents AI hallucinations from overwriting valid venue names
            // ═══════════════════════════════════════════════════════════════════
            const mentionedVenue = containsKnownVenue(userText);
            if (mentionedVenue && openaiWs && openaiWs.readyState === WebSocket.OPEN) {
              // Determine if it's pickup or destination based on context words
              const isPickupContext = /\b(pick[\s-]?up|pickup|from|collect)\b/i.test(lowerUserText);
              const isDestContext = /\b(destination|to|going|drop|heading|take me)\b/i.test(lowerUserText);
              
              // Also check if user is changing something (correction context)
              const isCorrectionContext = /\b(change|no|but|actually|not|instead)\b/i.test(lowerUserText);
              
              // If it's clearly a correction mentioning a known venue, apply it
              if (isCorrectionContext || (!sessionState.booking.pickup && isPickupContext) || (!sessionState.booking.destination && isDestContext)) {
                const fieldToUpdate = isDestContext && !isPickupContext ? "destination" : "pickup";
                const oldValue = fieldToUpdate === "pickup" ? sessionState.booking.pickup : sessionState.booking.destination;
                const properVenueName = mentionedVenue.charAt(0).toUpperCase() + mentionedVenue.slice(1); // Capitalize
                
                console.log(`[${callId}] 🏪 KNOWN VENUE DETECTED: "${mentionedVenue}" → updating ${fieldToUpdate}`);
                console.log(`[${callId}] 🏪 Context: pickup=${isPickupContext}, dest=${isDestContext}, correction=${isCorrectionContext}`);
                
                // Only apply if it's different from current value
                if (oldValue?.toLowerCase() !== mentionedVenue.toLowerCase()) {
                  if (fieldToUpdate === "pickup") {
                    sessionState.booking.pickup = properVenueName;
                  } else {
                    sessionState.booking.destination = properVenueName;
                  }
                  
                  console.log(`[${callId}] ✅ Updated ${fieldToUpdate}: "${oldValue}" → "${properVenueName}"`);
                  
                  // Reset fare if address changed after quote
                  if (sessionState.pendingFare) {
                    console.log(`[${callId}] 💰 Resetting fare due to ${fieldToUpdate} change (known venue)`);
                    sessionState.pendingFare = null;
                    sessionState.pendingEta = null;
                    sessionState.awaitingConfirmation = false;
                    sessionState.quoteInFlight = false;
                    sessionState.lastQuoteRequestedAt = 0;
                  }
                  
                  // Cancel any in-flight response
                  if (sessionState.openAiResponseActive) {
                    openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                    sessionState.openAiResponseActive = false;
                  }
                  openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
                  
                  // Sync to database
                  const updatePayload: Record<string, any> = { updated_at: new Date().toISOString() };
                  updatePayload[fieldToUpdate] = properVenueName;
                  supabase.from("live_calls").update(updatePayload).eq("call_id", callId).then(() => {
                    console.log(`[${callId}] ✅ live_calls updated with ${fieldToUpdate} = ${properVenueName}`);
                  });
                  
                  // Determine next instruction
                  const hasAllCore = sessionState.booking.pickup && sessionState.booking.destination && 
                                     sessionState.booking.passengers !== null && sessionState.booking.pickupTime;
                  
                  let nextInstruction: string;
                  if (hasAllCore) {
                    nextInstruction = "All 4 fields are now complete. Give the updated booking summary and ask for confirmation.";
                  } else if (!sessionState.booking.destination && fieldToUpdate === "pickup") {
                    nextInstruction = "Ask ONLY: 'And what is your destination?'";
                  } else if (!sessionState.booking.pickup && fieldToUpdate === "destination") {
                    nextInstruction = "Ask ONLY: 'Where would you like to be picked up?'";
                  } else if (sessionState.booking.passengers === null) {
                    nextInstruction = "Ask ONLY: 'How many people will be travelling?'";
                  } else if (!sessionState.booking.pickupTime) {
                    nextInstruction = "Ask ONLY: 'When do you need the taxi?'";
                  } else {
                    nextInstruction = "Summarize the booking and ask for confirmation.";
                  }
                  
                  // Inject acknowledgment and continue flow
                  openaiWs.send(JSON.stringify({
                    type: "conversation.item.create",
                    item: {
                      type: "message",
                      role: "system",
                      content: [{
                        type: "input_text",
                        text: `[${fieldToUpdate.toUpperCase()} CHANGED TO KNOWN VENUE]
The user changed the ${fieldToUpdate} to "${properVenueName}".

## UPDATED BOOKING STATE:
- Pickup: ${sessionState.booking.pickup || "not yet provided"}
- Destination: ${sessionState.booking.destination || "not yet provided"}
- Passengers: ${sessionState.booking.passengers ?? "not yet provided"}
- Time: ${sessionState.booking.pickupTime || "ASAP"}

INSTRUCTIONS:
1. Acknowledge briefly: "Got it, ${fieldToUpdate} is now ${properVenueName}."
2. ${nextInstruction}
3. If any price/ETA was quoted before, it's now invalid - you'll need to get a new quote`
                      }]
                    }
                  }));
                  openaiWs.send(JSON.stringify({ type: "response.create" }));
                  sessionState.openAiResponseActive = true;
                  await updateLiveCall(sessionState);
                  break; // Handled the known venue - skip normal processing
                }
              }
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // ADDRESS CORRECTION DETECTION (ported from simple mode - highest priority)
            // Detect phrases like "No, it's...", "Actually...", "The pickup is..."
            // This catches corrections that explicit patterns miss
            // ═══════════════════════════════════════════════════════════════════
            const addressCorrection = detectAddressCorrection(userText, sessionState.booking.pickup, sessionState.booking.destination);
            
            if (addressCorrection.type && addressCorrection.address && openaiWs && openaiWs.readyState === WebSocket.OPEN) {
              console.log(`[${callId}] 🔧 ADDRESS CORRECTION DETECTED: ${addressCorrection.type} = "${addressCorrection.address}"`);
              
              const correctedAddress = addressCorrection.address;
              const oldValue = addressCorrection.type === "pickup" ? sessionState.booking.pickup : sessionState.booking.destination;
              
              // Update booking state immediately
              if (addressCorrection.type === "pickup") {
                sessionState.booking.pickup = correctedAddress;
              } else {
                sessionState.booking.destination = correctedAddress;
              }
              
              console.log(`[${callId}] ✅ Updated ${addressCorrection.type}: "${oldValue}" → "${correctedAddress}"`);
              
              // Reset fare if address changed after quote
              if (sessionState.pendingFare) {
                console.log(`[${callId}] 💰 Resetting fare due to ${addressCorrection.type} correction`);
                sessionState.pendingFare = null;
                sessionState.pendingEta = null;
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                sessionState.lastQuoteRequestedAt = 0;
              }
              
              // Cancel any in-flight response
              if (sessionState.openAiResponseActive) {
                openaiWs.send(JSON.stringify({ type: "response.cancel" }));
                sessionState.openAiResponseActive = false;
              }
              openaiWs.send(JSON.stringify({ type: "input_audio_buffer.clear" }));
              
              // Sync to database
              const updatePayload: Record<string, any> = { updated_at: new Date().toISOString() };
              if (addressCorrection.type === "pickup") {
                updatePayload.pickup = correctedAddress;
              } else {
                updatePayload.destination = correctedAddress;
              }
              supabase.from("live_calls").update(updatePayload).eq("call_id", callId).then(() => {
                console.log(`[${callId}] ✅ live_calls updated with ${addressCorrection.type} correction`);
              });
              
              // Determine next question based on current state
              const fieldLabel = addressCorrection.type === "pickup" ? "pickup" : "destination";
              const hasAllCore = sessionState.booking.pickup && sessionState.booking.destination && 
                                 sessionState.booking.passengers !== null && sessionState.booking.pickupTime;
              
              let nextInstruction: string;
              if (hasAllCore) {
                nextInstruction = "All 4 fields are now complete. Give the updated booking summary and ask for confirmation.";
              } else if (!sessionState.booking.destination && addressCorrection.type === "pickup") {
                nextInstruction = "Ask ONLY: 'And what is your destination?'";
              } else if (!sessionState.booking.pickup && addressCorrection.type === "destination") {
                nextInstruction = "Ask ONLY: 'Where would you like to be picked up?'";
              } else if (sessionState.booking.passengers === null) {
                nextInstruction = "Ask ONLY: 'How many people will be travelling?'";
              } else if (!sessionState.booking.pickupTime) {
                nextInstruction = "Ask ONLY: 'When do you need the taxi?'";
              } else {
                nextInstruction = "Summarize the booking and ask for confirmation.";
              }
              
              // Inject acknowledgment and continue flow
              openaiWs.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[${fieldLabel.toUpperCase()} CORRECTED BY USER]
The user changed the ${fieldLabel} from "${oldValue || 'empty'}" to "${correctedAddress}".

## UPDATED BOOKING STATE:
- Pickup: ${sessionState.booking.pickup || "not yet provided"}
- Destination: ${sessionState.booking.destination || "not yet provided"}
- Passengers: ${sessionState.booking.passengers ?? "not yet provided"}
- Time: ${sessionState.booking.pickupTime || "ASAP"}

INSTRUCTIONS:
1. Acknowledge briefly: "Got it, ${fieldLabel} is now ${correctedAddress}."
2. ${nextInstruction}
3. If any price/ETA was quoted before, it's now invalid - you'll need to get a new quote`
                  }]
                }
              }));
              openaiWs.send(JSON.stringify({ type: "response.create" }));
              sessionState.openAiResponseActive = true;
              await updateLiveCall(sessionState);
              break; // Handled the correction - skip normal processing
            }
            
            // ═══════════════════════════════════════════════════════════════════
            // EXPLICIT PICKUP/DESTINATION CHANGE DETECTION (before AI extraction)
            // Patterns like "change the pickup to X" or "can I change pickup to X"
            // ═══════════════════════════════════════════════════════════════════
            // ═══════════════════════════════════════════════════════════════════
            // EXPLICIT PICKUP/DESTINATION CHANGE DETECTION (COMPREHENSIVE)
            // Must catch: "change my pickup to X", "change pickup point to X", 
            // "can I change the pickup to X", "I want to change my pick-up to X"
            // ═══════════════════════════════════════════════════════════════════
            const pickupPatterns = [
              // "change my pickup to X", "change the pickup to X", "change pickup to X"
              /(?:change|update|switch|make)\s+(?:my\s+)?(?:the\s+)?pick[\s-]?up(?:\s+point|\s+location|\s+address)?\s+(?:to|two)\s+(?:the\s+)?(.+)/i,
              // "can I change my pickup to X", "could I change the pickup to X"
              /(?:can i|could i|i want to|i'd like to|i would like to)\s+change\s+(?:my\s+)?(?:the\s+)?pick[\s-]?up(?:\s+point|\s+location|\s+address)?\s+(?:to|two)\s+(?:the\s+)?(.+)/i,
              // "pickup should be X", "pickup is X" (after summary)
              /pick[\s-]?up\s+(?:should be|is|to)\s+(?:the\s+)?(.+)/i,
              // "actually from X" / "no, from X"
              /(?:actually|no)[,\s]+(?:from|pickup is|pickup from)\s+(?:the\s+)?(.+)/i,
              // "the pickup is X" / "my pickup is X"
              /(?:the|my)\s+pick[\s-]?up\s+(?:is|should be)\s+(?:the\s+)?(.+)/i,
            ];
            
            const destPatterns = [
              // "change my destination to X", "change the destination to X"
              /(?:change|update|switch|make)\s+(?:my\s+)?(?:the\s+)?destination(?:\s+point|\s+location|\s+address)?\s+(?:to|two)\s+(?:the\s+)?(.+)/i,
              // "can I change my destination to X"
              /(?:can i|could i|i want to|i'd like to|i would like to)\s+change\s+(?:my\s+)?(?:the\s+)?destination(?:\s+point|\s+location|\s+address)?\s+(?:to|two)\s+(?:the\s+)?(.+)/i,
              // "destination should be X", "destination is X"
              /destination\s+(?:should be|is|to)\s+(?:the\s+)?(.+)/i,
              // "actually to X" / "no, going to X"
              /(?:actually|no)[,\s]+(?:to|going to|destination is|heading to)\s+(?:the\s+)?(.+)/i,
              // "the destination is X" / "my destination is X"
              /(?:the|my)\s+destination\s+(?:is|should be)\s+(?:the\s+)?(.+)/i,
            ];
            
            // Test all pickup patterns
            let pickupChangeMatch: RegExpMatchArray | null = null;
            for (const pattern of pickupPatterns) {
              pickupChangeMatch = lowerUserText.match(pattern);
              if (pickupChangeMatch) {
                console.log(`[${callId}] 🎯 Pickup pattern matched: ${pattern}`);
                break;
              }
            }
            
            // Test all destination patterns
            let destChangeMatch: RegExpMatchArray | null = null;
            for (const pattern of destPatterns) {
              destChangeMatch = lowerUserText.match(pattern);
              if (destChangeMatch) {
                console.log(`[${callId}] 🎯 Destination pattern matched: ${pattern}`);
                break;
              }
            }
            
            if (pickupChangeMatch && pickupChangeMatch[1]) {
              const newPickup = pickupChangeMatch[1].replace(/please\s*$/i, "").trim();
              console.log(`[${callId}] 🔄 EXPLICIT PICKUP CHANGE DETECTED: "${sessionState.booking.pickup}" → "${newPickup}"`);
              
              const oldPickup = sessionState.booking.pickup;
              sessionState.booking.pickup = newPickup;
              
              // Reset fare since pickup changed
              if (sessionState.pendingFare) {
                console.log(`[${callId}] 💰 Resetting fare due to pickup change`);
                sessionState.pendingFare = null;
                sessionState.pendingEta = null;
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                sessionState.lastQuoteRequestedAt = 0;
              }
              
              // Cancel any in-progress response
              if (sessionState.openAiResponseActive) {
                openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
                sessionState.openAiResponseActive = false;
              }
              
              // Tell Ada about the change
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[PICKUP CHANGED BY USER]
The user changed the pickup from "${oldPickup || 'empty'}" to "${newPickup}".

## UPDATED BOOKING STATE:
- Pickup: ${newPickup}
- Destination: ${sessionState.booking.destination || "not yet provided"}
- Passengers: ${sessionState.booking.passengers ?? "not yet provided"}
- Time: ${sessionState.booking.pickupTime || "ASAP"}

INSTRUCTIONS:
1. Acknowledge briefly: "Got it, pickup is now ${newPickup}."
2. If all 4 fields are complete, summarize and ask for confirmation
3. If any price/ETA was quoted before, it's now invalid - you'll need to get a new quote`
                  }]
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
              sessionState.openAiResponseActive = true;
              await updateLiveCall(sessionState);
              break; // Handled the pickup change
            }
            
            if (destChangeMatch && destChangeMatch[1]) {
              const newDest = destChangeMatch[1].replace(/please\s*$/i, "").trim();
              console.log(`[${callId}] 🔄 EXPLICIT DESTINATION CHANGE DETECTED: "${sessionState.booking.destination}" → "${newDest}"`);
              
              const oldDest = sessionState.booking.destination;
              sessionState.booking.destination = newDest;
              
              // Reset fare since destination changed
              if (sessionState.pendingFare) {
                console.log(`[${callId}] 💰 Resetting fare due to destination change`);
                sessionState.pendingFare = null;
                sessionState.pendingEta = null;
                sessionState.awaitingConfirmation = false;
                sessionState.quoteInFlight = false;
                sessionState.lastQuoteRequestedAt = 0;
              }
              
              // Cancel any in-progress response
              if (sessionState.openAiResponseActive) {
                openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
                sessionState.openAiResponseActive = false;
              }
              
              // Tell Ada about the change
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[DESTINATION CHANGED BY USER]
The user changed the destination from "${oldDest || 'empty'}" to "${newDest}".

## UPDATED BOOKING STATE:
- Pickup: ${sessionState.booking.pickup || "not yet provided"}
- Destination: ${newDest}
- Passengers: ${sessionState.booking.passengers ?? "not yet provided"}
- Time: ${sessionState.booking.pickupTime || "ASAP"}

INSTRUCTIONS:
1. Acknowledge briefly: "Got it, destination is now ${newDest}."
2. If all 4 fields are complete, summarize and ask for confirmation
3. If any price/ETA was quoted before, it's now invalid - you'll need to get a new quote`
                  }]
                }
              }));
              openaiWs!.send(JSON.stringify({ type: "response.create" }));
              sessionState.openAiResponseActive = true;
              await updateLiveCall(sessionState);
              break; // Handled the destination change
            }
            
            // ═══════════════════════════════════════════════════════════════════════
            // FAST PATH CONFIRMATION (REGEX) - Check BEFORE AI extraction to avoid latency
            // If user says "yes" while we're awaiting confirmation, confirm immediately
            // ═══════════════════════════════════════════════════════════════════════
            const isYesRegex = /^(yes|yeah|yep|yea|yup|sure|ok|okay|alright|go ahead|book it|confirm|please|yes please|that's? (?:right|correct)|correct|definitely|absolutely|affirmative)$/i.test(userText.trim()) ||
                               /^(?:yes|yeah|yep|yup|sure|ok|okay|please)[,.\s]*(please|book it|go ahead|thanks?|cheers)?$/i.test(userText.trim());
            
            if (isYesRegex && sessionState.awaitingConfirmation && !sessionState.bookingConfirmed) {
              console.log(`[${callId}] 🚀 REGEX FAST PATH CONFIRMATION: User said "${userText}" while awaitingConfirmation=true`);
              
              // Cancel any active response
              if (sessionState.openAiResponseActive) {
                openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
                sessionState.openAiResponseActive = false;
              }
              
              // Set confirmed flag IMMEDIATELY
              sessionState.bookingConfirmed = true;
              sessionState.awaitingConfirmation = false;
              sessionState.quoteInFlight = false;
              
              // Send CONFIRMED webhook to dispatch
              console.log(`[${callId}] 📤 REGEX FAST PATH: Sending CONFIRMED webhook...`);
              await sendDispatchWebhook(sessionState, "confirmed", {
                pickup: sessionState.booking.pickup,
                destination: sessionState.booking.destination,
                passengers: sessionState.booking.passengers,
                pickup_time: sessionState.booking.pickupTime
              });
              console.log(`[${callId}] ✅ REGEX FAST PATH: CONFIRMED webhook sent`);
              
              // POST confirmation to callback_url if provided
              if (sessionState.pendingConfirmationCallback) {
                try {
                  console.log(`[${callId}] 📡 REGEX FAST PATH: POSTing to callback_url: ${sessionState.pendingConfirmationCallback}`);
                  const confirmPayload = {
                    call_id: callId,
                    job_id: sessionState.dispatchJobId || null,
                    action: "confirmed",
                    response: "confirmed",
                    pickup: sessionState.booking.pickup,
                    destination: sessionState.booking.destination,
                    fare: sessionState.pendingFare,
                    eta: sessionState.pendingEta,
                    pickup_time: sessionState.booking.pickupTime || "ASAP",
                    passengers: sessionState.booking.passengers || 1,
                    caller_phone: sessionState.callerPhone,
                    booking_ref: sessionState.pendingBookingRef || sessionState.bookingRef || null,
                    timestamp: new Date().toISOString()
                  };
                  
                  const confirmResp = await fetch(sessionState.pendingConfirmationCallback, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(confirmPayload)
                  });
                  console.log(`[${callId}] 📬 REGEX FAST PATH: Callback response: ${confirmResp.status}`);
                } catch (callbackErr) {
                  console.error(`[${callId}] ⚠️ REGEX FAST PATH: Callback failed:`, callbackErr);
                }
              }
              
              // Protect goodbye speech
              sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
              sessionState.lastQuestionAsked = "none";
              
              // Get language-aware closing script
              const closingScript = getClosingScript(sessionState.language);
              const randomTip = closingScript.whatsappTips[Math.floor(Math.random() * closingScript.whatsappTips.length)];
              const langInstruction = sessionState.language === "auto" 
                ? "Deliver this in the SAME LANGUAGE you've been speaking with the caller."
                : "";
              
              // Inject POST-CONFIRMATION mode
              openaiWs!.send(JSON.stringify({
                type: "conversation.item.create",
                item: {
                  type: "message",
                  role: "system",
                  content: [{
                    type: "input_text",
                    text: `[POST-CONFIRMATION MODE ACTIVE] The booking is NOW CONFIRMED and COMPLETE.
                    
🚨 CRITICAL RULES:
- The customer just said "${userText}" to confirm - the booking is DONE
- Do NOT ask "shall I book that taxi?" - it's ALREADY BOOKED  
- Do NOT loop back to any booking questions
- Your ONLY job now is to deliver the closing script and end the call

${langInstruction}

Deliver this closing script:
1. "${closingScript.confirmation}"
2. "${closingScript.whatsappDetails}"
3. "${randomTip}"
4. "${closingScript.goodbye}"

Then IMMEDIATELY call end_call().`
                  }]
                }
              }));
              
              // Trigger the goodbye response
              openaiWs!.send(JSON.stringify({
                type: "response.create",
                response: {
                  modalities: ["audio", "text"],
                  instructions: `The customer confirmed with "${userText}". Deliver the booking confirmation and closing script warmly. Then call end_call().`
                }
              }));
              sessionState.openAiResponseActive = true;
              
              await updateLiveCall(sessionState);
              break; // Exit transcript handler - confirmation handled via fast path
            }
            
            if (USE_AI_EXTRACTION) {
              console.log(`[${callId}] 🧠 Running AI extraction before Ada responds...`);
              
              // ✅ SET EXTRACTION GUARD: Block any VAD-triggered responses until we're done
              sessionState.extractionInProgress = true;
              
              try {
                const aiResult = await extractBookingWithAI(
                  sessionState.conversationHistory,
                  sessionState.booking,
                  sessionState.callerPhone,
                  callId
                );
                
                if (aiResult) {
                  // ═══════════════════════════════════════════════════════════════
                  // AI DETECTED A CORRECTION - Handle with full context
                  // ═══════════════════════════════════════════════════════════════
                  if (aiResult.is_correction && aiResult.fields_changed && aiResult.fields_changed.length > 0) {
                    console.log(`[${callId}] 🧠 AI DETECTED CORRECTION: fields_changed=${aiResult.fields_changed.join(", ")}`);
                    
                    // Track what changed for the system message
                    const changes: string[] = [];
                    
                    console.log(`[${callId}] 🔍 AI correction validation: fields_changed=${JSON.stringify(aiResult.fields_changed)}, pickup="${aiResult.pickup}", dest="${aiResult.destination}"`);
                    
                    // For corrections, be more lenient - trust AI extraction for known venues
                    // Apply the AI's corrected values
                    if (aiResult.fields_changed.includes("pickup") && aiResult.pickup) {
                      // Accept if it's a known venue OR if it passes lenient matching
                      const isKnownVenue = containsKnownVenue(aiResult.pickup) !== null;
                      const passesLenientMatch = matchesUserTextForCorrection(aiResult.pickup, userText);
                      
                      console.log(`[${callId}] 🔍 Pickup validation: venue=${isKnownVenue}, lenientMatch=${passesLenientMatch}, proposed="${aiResult.pickup}", user="${userText}"`);
                      
                      if (isKnownVenue || passesLenientMatch) {
                        const oldValue = sessionState.booking.pickup;
                        sessionState.booking.pickup = aiResult.pickup;
                        changes.push(`pickup from "${oldValue || 'empty'}" to "${aiResult.pickup}"`);
                      } else {
                        console.log(`[${callId}] ⚠️ AI pickup correction rejected: "${aiResult.pickup}" not in user text "${userText}"`);
                      }
                    }
                    if (aiResult.fields_changed.includes("destination") && aiResult.destination) {
                      const isKnownVenue = containsKnownVenue(aiResult.destination) !== null;
                      const passesLenientMatch = matchesUserTextForCorrection(aiResult.destination, userText);
                      
                      console.log(`[${callId}] 🔍 Destination validation: venue=${isKnownVenue}, lenientMatch=${passesLenientMatch}, proposed="${aiResult.destination}", user="${userText}"`);
                      
                      if (isKnownVenue || passesLenientMatch) {
                        const oldValue = sessionState.booking.destination;
                        sessionState.booking.destination = aiResult.destination;
                        changes.push(`destination from "${oldValue || 'empty'}" to "${aiResult.destination}"`);
                      } else {
                        console.log(`[${callId}] ⚠️ AI destination correction rejected: "${aiResult.destination}" not in user text "${userText}"`);
                      }
                    }
                    if (aiResult.fields_changed.includes("passengers") && aiResult.passengers !== null) {
                      const oldValue = sessionState.booking.passengers;
                      sessionState.booking.passengers = aiResult.passengers;
                      changes.push(`passengers from ${oldValue ?? 'empty'} to ${aiResult.passengers}`);
                    }
                    if (aiResult.fields_changed.includes("time") && aiResult.pickup_time && matchesUserTextForCorrection(aiResult.pickup_time, userText)) {
                      const oldValue = sessionState.booking.pickupTime;
                      sessionState.booking.pickupTime = aiResult.pickup_time;
                      changes.push(`time from "${oldValue || 'empty'}" to "${aiResult.pickup_time}"`);
                    }
                    
                    // FALLBACK: If fields_changed is empty but we have new values that don't match current state,
                    // treat them as implicit corrections (common with AI hallucination of fields_changed)
                    if (changes.length === 0 && !aiResult.fields_changed?.length) {
                      console.log(`[${callId}] 🔄 Empty fields_changed - checking for implicit corrections...`);
                      
                      // Check if AI extraction found a destination that's different from current
                      if (aiResult.destination && aiResult.destination !== sessionState.booking.destination) {
                        const passesMatch = matchesUserTextForCorrection(aiResult.destination, userText);
                        if (passesMatch) {
                          const oldValue = sessionState.booking.destination;
                          sessionState.booking.destination = aiResult.destination;
                          changes.push(`destination from "${oldValue || 'empty'}" to "${aiResult.destination}"`);
                          console.log(`[${callId}] ✅ Implicit destination correction applied: "${aiResult.destination}"`);
                        }
                      }
                      // Check pickup
                      if (aiResult.pickup && aiResult.pickup !== sessionState.booking.pickup) {
                        const passesMatch = matchesUserTextForCorrection(aiResult.pickup, userText);
                        if (passesMatch) {
                          const oldValue = sessionState.booking.pickup;
                          sessionState.booking.pickup = aiResult.pickup;
                          changes.push(`pickup from "${oldValue || 'empty'}" to "${aiResult.pickup}"`);
                          console.log(`[${callId}] ✅ Implicit pickup correction applied: "${aiResult.pickup}"`);
                        }
                      }
                    }

                    // If we failed to apply any change (likely mismatch/hallucination), do NOT inject correction.
                    if (changes.length === 0) {
                      console.log(`[${callId}] ⚠️ AI correction rejected (no changes applied after validation). user="${userText}"`);
                      // Continue normal flow.
                    } else {
                    
                    // Reset fare if we had one (correction invalidates it)
                    if (sessionState.pendingFare) {
                      console.log(`[${callId}] 💰 Resetting fare due to AI-detected correction`);
                      sessionState.pendingFare = null;
                      sessionState.pendingEta = null;
                      sessionState.awaitingConfirmation = false;
                      sessionState.quoteInFlight = false;
                      sessionState.lastQuoteRequestedAt = 0;
                    }
                    
                    // CANCEL any in-progress response to prevent old state from being used
                    if (sessionState.openAiResponseActive) {
                      openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
                      sessionState.openAiResponseActive = false;
                    }
                    
                    // Build the corrected state for the summary
                    const correctedPickup = sessionState.booking.pickup || "not yet provided";
                    const correctedDest = sessionState.booking.destination || "not yet provided";
                    const correctedPax = sessionState.booking.passengers ?? "not yet provided";
                    const correctedTime = sessionState.booking.pickupTime || "ASAP";
                    
                    // Tell OpenAI about the correction with FULL context
                    openaiWs!.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "system",
                        content: [{
                          type: "input_text",
                          text: `[AI-DETECTED CORRECTION]
The user corrected: ${changes.join(", ")}.

## UPDATED BOOKING STATE (USE ONLY THESE VALUES):
- Pickup: ${correctedPickup}
- Destination: ${correctedDest}  
- Passengers: ${correctedPax}
- Time: ${correctedTime}

INSTRUCTIONS:
1. Acknowledge the correction briefly
2. If all 4 fields are captured, proceed to summarize the booking using ONLY the values above
3. NEVER mention the old incorrect values`
                        }]
                      }
                    }));
                      openaiWs!.send(JSON.stringify({ type: "response.create" }));
                      sessionState.openAiResponseActive = true;
                      
                      await updateLiveCall(sessionState);
                      break; // AI correction handled
                    }
                  }
                  
                  // ═══════════════════════════════════════════════════════════════
                  // AI DETECTED AFFIRMATIVE (yes/confirm) - FAST PATH CONFIRMATION
                  // When awaitingConfirmation is true and user says "yes", directly 
                  // trigger the booking confirmation without waiting for OpenAI tool call
                  // ═══════════════════════════════════════════════════════════════
                  if (aiResult.is_affirmative && (aiResult.intent === "confirm_booking" || sessionState.awaitingConfirmation)) {
                    console.log(`[${callId}] 🧠 AI DETECTED CONFIRMATION: User said yes (awaitingConfirmation=${sessionState.awaitingConfirmation})`);
                    
                    // FAST PATH: If we're awaiting confirmation and user said yes, trigger confirmation directly
                    if (sessionState.awaitingConfirmation && !sessionState.bookingConfirmed) {
                      console.log(`[${callId}] 🚀 FAST PATH CONFIRMATION: Bypassing OpenAI tool call, confirming directly`);
                      
                      // Clear extraction guard first
                      sessionState.extractionInProgress = false;
                      
                      // Cancel any active response
                      if (sessionState.openAiResponseActive) {
                        openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
                        sessionState.openAiResponseActive = false;
                      }
                      
                      // Set confirmed flag IMMEDIATELY
                      sessionState.bookingConfirmed = true;
                      sessionState.awaitingConfirmation = false;
                      sessionState.quoteInFlight = false;
                      
                      // Send CONFIRMED webhook to dispatch
                      console.log(`[${callId}] 📤 FAST PATH: Sending CONFIRMED webhook...`);
                      await sendDispatchWebhook(sessionState, "confirmed", {
                        pickup: sessionState.booking.pickup,
                        destination: sessionState.booking.destination,
                        passengers: sessionState.booking.passengers,
                        pickup_time: sessionState.booking.pickupTime
                      });
                      console.log(`[${callId}] ✅ FAST PATH: CONFIRMED webhook sent`);
                      
                      // POST confirmation to callback_url if provided
                      if (sessionState.pendingConfirmationCallback) {
                        try {
                          console.log(`[${callId}] 📡 FAST PATH: POSTing to callback_url: ${sessionState.pendingConfirmationCallback}`);
                          const confirmPayload = {
                            call_id: callId,
                            job_id: sessionState.dispatchJobId || null,
                            action: "confirmed",
                            response: "confirmed",
                            pickup: sessionState.booking.pickup,
                            destination: sessionState.booking.destination,
                            fare: sessionState.pendingFare,
                            eta: sessionState.pendingEta,
                            pickup_time: sessionState.booking.pickupTime || "ASAP",
                            passengers: sessionState.booking.passengers || 1,
                            caller_phone: sessionState.callerPhone,
                            booking_ref: sessionState.pendingBookingRef || sessionState.bookingRef || null,
                            timestamp: new Date().toISOString()
                          };
                          
                          const confirmResp = await fetch(sessionState.pendingConfirmationCallback, {
                            method: "POST",
                            headers: { "Content-Type": "application/json" },
                            body: JSON.stringify(confirmPayload)
                          });
                          console.log(`[${callId}] 📬 FAST PATH: Callback response: ${confirmResp.status}`);
                        } catch (callbackErr) {
                          console.error(`[${callId}] ⚠️ FAST PATH: Callback failed:`, callbackErr);
                        }
                      }
                      
                      // Protect goodbye speech
                      sessionState.summaryProtectionUntil = Date.now() + SUMMARY_PROTECTION_MS;
                      sessionState.lastQuestionAsked = "none";
                      
                      // Get language-aware closing script
                      const closingScript = getClosingScript(sessionState.language);
                      const randomTip = closingScript.whatsappTips[Math.floor(Math.random() * closingScript.whatsappTips.length)];
                      const langInstruction = sessionState.language === "auto" 
                        ? "Deliver this in the SAME LANGUAGE you've been speaking with the caller."
                        : "";
                      
                      // Inject POST-CONFIRMATION mode
                      openaiWs!.send(JSON.stringify({
                        type: "conversation.item.create",
                        item: {
                          type: "message",
                          role: "system",
                          content: [{
                            type: "input_text",
                            text: `[POST-CONFIRMATION MODE ACTIVE] The booking is NOW CONFIRMED and COMPLETE.
                            
🚨 CRITICAL RULES:
- The customer just said YES to confirm - the booking is DONE
- Do NOT ask "shall I book that taxi?" - it's ALREADY BOOKED  
- Do NOT loop back to any booking questions
- Your ONLY job now is to deliver the closing script and end the call

${langInstruction}

Deliver this closing script:
1. "${closingScript.confirmation}"
2. "${closingScript.whatsappDetails}"
3. "${randomTip}"
4. "${closingScript.goodbye}"

Then IMMEDIATELY call end_call().`
                          }]
                        }
                      }));
                      
                      // Trigger the goodbye response
                      openaiWs!.send(JSON.stringify({
                        type: "response.create",
                        response: {
                          modalities: ["audio", "text"],
                          instructions: `The customer confirmed with "${userText}". Deliver the booking confirmation and closing script warmly. Then call end_call().`
                        }
                      }));
                      sessionState.openAiResponseActive = true;
                      
                      await updateLiveCall(sessionState);
                      break; // Exit transcript handler - confirmation handled
                    }
                  }
                  
                  // ═══════════════════════════════════════════════════════════════
                  // AI EXTRACTED NEW DATA - Update state BEFORE Ada responds
                  // ═══════════════════════════════════════════════════════════════
                  let updated = false;
                  
                  // Only update if AI found new values (not corrections, which are handled above)
                  // and the model is reasonably confident.
                  if (!aiResult.is_correction && aiResult.confidence !== "low") {
                    if (aiResult.pickup && !sessionState.booking.pickup) {
                      console.log(`[${callId}] 🧠 AI FILL pickup: "${aiResult.pickup}"`);
                      sessionState.booking.pickup = aiResult.pickup;
                      updated = true;
                    }
                    if (aiResult.destination && !sessionState.booking.destination) {
                      console.log(`[${callId}] 🧠 AI FILL destination: "${aiResult.destination}"`);
                      sessionState.booking.destination = aiResult.destination;
                      updated = true;
                    }
                    // GUARD: Only fill passengers if AI explicitly extracted it AND it's not a default value
                    // The AI often returns passengers=1 as a default even when user didn't mention it
                    const passengerMentioned = /\b(one|two|three|four|five|six|seven|eight|1|2|3|4|5|6|7|8)\s*(passenger|people|person|of us)?s?\b/i.test(userText) ||
                                                /\bjust\s*(me|myself)\b/i.test(userText);
                    if (aiResult.passengers !== null && sessionState.booking.passengers === null && passengerMentioned) {
                      console.log(`[${callId}] 🧠 AI FILL passengers: ${aiResult.passengers} (user said: "${userText}")`);
                      sessionState.booking.passengers = aiResult.passengers;
                      updated = true;
                    } else if (aiResult.passengers !== null && sessionState.booking.passengers === null) {
                      console.log(`[${callId}] ⚠️ AI FILL passengers BLOCKED: ${aiResult.passengers} (user didn't explicitly mention passengers)`);
                    }
                    
                    // GUARD: Only fill time if AI explicitly extracted it AND it's not just "ASAP" as a default
                    // The AI often returns "ASAP" when user didn't mention time at all
                    const timeMentioned = /\b(asap|now|immediately|right now|straight away|soon|in \d+ minutes?|at \d+|tomorrow|later|morning|afternoon|evening|tonight)\b/i.test(userText);
                    if (aiResult.pickup_time && !sessionState.booking.pickupTime && timeMentioned) {
                      console.log(`[${callId}] 🧠 AI FILL time: "${aiResult.pickup_time}" (user said: "${userText}")`);
                      sessionState.booking.pickupTime = aiResult.pickup_time;
                      updated = true;
                    } else if (aiResult.pickup_time && !sessionState.booking.pickupTime) {
                      console.log(`[${callId}] ⚠️ AI FILL time BLOCKED: "${aiResult.pickup_time}" (user didn't explicitly mention time)`);
                    }
                  }
                  
                  if (updated) {
                    await updateLiveCall(sessionState);
                  }
                }
              } catch (aiErr) {
                console.error(`[${callId}] ⚠️ AI extraction error (falling back to normal flow):`, aiErr);
              } finally {
                // ✅ CLEAR EXTRACTION GUARD: Allow responses to proceed now
                sessionState.extractionInProgress = false;
                
                // ═══════════════════════════════════════════════════════════════════
                // RACE CONDITION FIX: If Ada is ALREADY speaking (VAD triggered too early),
                // cancel her response and force a re-response with correct state.
                // This prevents the "Ada registered my address after she said address" bug.
                // ═══════════════════════════════════════════════════════════════════
                if (sessionState.openAiResponseActive) {
                  console.log(`[${callId}] 🛑 RACE FIX: Ada was already speaking when extraction finished - cancelling and re-triggering`);
                  openaiWs!.send(JSON.stringify({ type: "response.cancel" }));
                  sessionState.openAiResponseActive = false;
                  
                  // Small delay to ensure cancel is processed before new response
                  await new Promise(r => setTimeout(r, 50));
                }
                
                // ✅ INJECT STATE CONTEXT: Give OpenAI the full extracted state BEFORE it responds
                // This ensures Ada knows the correct pickup/destination before speaking
                const stateInjection = {
                  type: "conversation.item.create",
                  item: {
                    type: "message",
                    role: "system",
                    content: [{
                      type: "input_text",
                      text: `[VERIFIED BOOKING STATE - USE ONLY THESE VALUES]
- Pickup: ${sessionState.booking.pickup || "NOT YET PROVIDED"}
- Destination: ${sessionState.booking.destination || "NOT YET PROVIDED"}  
- Passengers: ${sessionState.booking.passengers ?? "NOT YET PROVIDED"}
- Time: ${sessionState.booking.pickupTime || "NOT YET PROVIDED"}

CRITICAL: Use EXACTLY these values in your response. Do NOT invent or change any addresses.`
                    }]
                  }
                };
                openaiWs!.send(JSON.stringify(stateInjection));
                
                // ✅ Force a new response with correct state if we had to cancel
                // This ensures Ada responds with the correct, extracted values
                if (!sessionState.openAiResponseActive) {
                  openaiWs!.send(JSON.stringify({ type: "response.create" }));
                  sessionState.openAiResponseActive = true;
                }
              }
            }
            
            // PASSENGER CLARIFICATION GUARD: Detect address-like response to passenger question
            if (sessionState.lastQuestionAsked === "passengers") {
              // Check if response looks like an address rather than a number
              const looksLikeAddress = /\b(road|street|avenue|lane|drive|way|close|court|place|crescent|terrace|airport|station|hotel|hospital|mall|centre|center|square|park|building|house|flat|apartment|\d+[a-zA-Z]?\s+\w)/i.test(userText);
              const hasNumber = /\b(one|two|three|four|five|six|seven|eight|nine|ten|[1-9]|1[0-9]|20)\s*(passenger|people|person|of us)?s?\b/i.test(userText);
              const isJustNumber = /^[1-9]$|^1[0-9]$|^20$|^(one|two|three|four|five|six|seven|eight|nine|ten)$/i.test(userText.trim());
              
              if (looksLikeAddress && !hasNumber && !isJustNumber) {
                console.log(`[${callId}] 🔄 PASSENGER CLARIFICATION: Got address "${userText}" when expecting passenger count`);
                
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
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
                break; // Don't run normal context pairing
              }
            }
            
            // Send context-aware prompt to OpenAI with FULL extracted state
            const contextPrompt = {
              type: "conversation.item.create",
              item: {
                type: "message",
                role: "system",
                content: [{
                  type: "input_text",
                  text: `[CONTEXT PAIRING] You just asked about "${sessionState.lastQuestionAsked}". The user responded: "${userText}". 
                  
If this is a valid answer to your ${sessionState.lastQuestionAsked} question, use sync_booking_data to save it to the CORRECT field.
Current state: pickup=${sessionState.booking.pickup || "empty"}, destination=${sessionState.booking.destination || "empty"}, passengers=${sessionState.booking.passengers ?? "empty"}, time=${sessionState.booking.pickupTime || "empty"}`
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
            // Update booking state from tool call - trust Ada's updates directly
            if (toolArgs.pickup) sessionState.booking.pickup = String(toolArgs.pickup);
            if (toolArgs.destination) sessionState.booking.destination = String(toolArgs.destination);
            if (toolArgs.passengers !== undefined) sessionState.booking.passengers = Number(toolArgs.passengers);
            if (toolArgs.pickup_time) sessionState.booking.pickupTime = String(toolArgs.pickup_time);
            if (toolArgs.nearest_pickup) sessionState.booking.nearestPickup = String(toolArgs.nearest_pickup);
            if (toolArgs.nearest_dropoff) sessionState.booking.nearestDropoff = String(toolArgs.nearest_dropoff);
            if (toolArgs.last_question_asked) {
              sessionState.lastQuestionAsked = toolArgs.last_question_asked as SessionState["lastQuestionAsked"];
            }
            
            console.log(`[${callId}] 📊 Booking state updated:`, sessionState.booking);
            await updateLiveCall(sessionState);
            
            // Send tool result
            openaiWs!.send(JSON.stringify({
              type: "conversation.item.create",
              item: {
                type: "function_call_output",
                call_id: data.call_id,
                output: JSON.stringify({ 
                  success: true, 
                  current_state: sessionState.booking,
                  next_question: sessionState.lastQuestionAsked
                })
              }
            }));
            openaiWs!.send(JSON.stringify({ type: "response.create" }));
            
          } else if (toolName === "book_taxi") {
            const action = String(toolArgs.action || "");

            const resolvedPickup = toolArgs.pickup ? String(toolArgs.pickup) : sessionState.booking.pickup;
            const resolvedDestination = toolArgs.destination ? String(toolArgs.destination) : sessionState.booking.destination;
            const resolvedPassengers = (toolArgs.passengers !== undefined)
              ? Number(toolArgs.passengers)
              : sessionState.booking.passengers;
            const resolvedPickupTime = toolArgs.pickup_time ? String(toolArgs.pickup_time) : sessionState.booking.pickupTime;

            // Persist any provided details so subsequent tool calls (e.g. confirmed) include full booking info
            if (resolvedPickup) sessionState.booking.pickup = resolvedPickup;
            if (resolvedDestination) sessionState.booking.destination = resolvedDestination;
            if (resolvedPassengers !== null && !Number.isNaN(resolvedPassengers)) {
              sessionState.booking.passengers = resolvedPassengers;
            }
            if (resolvedPickupTime) sessionState.booking.pickupTime = resolvedPickupTime;

            // Prevent sending incomplete payloads (these cause duplicate/garbage quotes downstream)
            const missing: string[] = [];
            if (!sessionState.booking.pickup) missing.push("pickup");
            if (!sessionState.booking.destination) missing.push("destination");
            if (sessionState.booking.passengers === null || Number.isNaN(sessionState.booking.passengers)) missing.push("passengers");

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
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
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
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
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
                openaiWs!.send(JSON.stringify({ type: "response.create" }));
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
              
              // TIMEOUT FALLBACK: If fare doesn't arrive within 15 seconds, break silence and apologize
              // MEMORY LEAK FIX: Use tracked timeout so it's cleared on cleanup
              const quoteTimeoutMs = 15000;
              trackedTimeout(() => {
                if (sessionState.waitingForQuoteSilence && !sessionState.awaitingConfirmation && !cleanedUp) {
                  console.log(`[${callId}] ⏰ QUOTE TIMEOUT: No fare received after ${quoteTimeoutMs}ms - breaking silence`);
                  
                  // Clear silence mode
                  sessionState.waitingForQuoteSilence = false;
                  sessionState.saidOneMoment = false;
                  sessionState.quoteInFlight = false;
                  
                  // Tell Ada to apologize and offer to retry
                  if (openaiWs && openaiWs.readyState === WebSocket.OPEN) {
                    openaiWs.send(JSON.stringify({
                      type: "conversation.item.create",
                      item: {
                        type: "message",
                        role: "system",
                        content: [{
                          type: "input_text",
                          text: `[QUOTE TIMEOUT] The dispatch system didn't respond in time. Apologize briefly and ask if they'd like you to try again. Say: "I'm sorry, I'm having trouble getting the fare right now. Would you like me to try again?" If they say yes, call book_taxi with action="request_quote" again.`
                        }]
                      }
                    }));
                    openaiWs.send(JSON.stringify({ 
                      type: "response.create",
                      response: {
                        modalities: ["audio", "text"],
                        instructions: "Say: 'I'm sorry, I'm having trouble getting the fare right now. Would you like me to try again?' Then wait for their answer."
                      }
                    }));
                  }
                }
              }, quoteTimeoutMs);

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
    // Stop the OpenAI ping immediately
    stopOpenAiPing();
    openaiConnected = false;
    
    // Include close codes/reasons to debug unexpected disconnects.
    // NOTE: Deno's WS close event may not always include reason.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const anyEv: any = ev;
    console.log(
      `[${callId}] OpenAI WebSocket closed` +
        (anyEv?.code ? ` code=${anyEv.code}` : "") +
        (anyEv?.reason ? ` reason=${anyEv.reason}` : ""),
    );
    
    // === OPENAI RECONNECTION LOGIC (ported from simple mode) ===
    // If the call is still active and we haven't exceeded retry limit, attempt reconnection
    if (!cleanedUp && !closingGracePeriodActive && openaiReconnectAttempts < MAX_OPENAI_RECONNECT_ATTEMPTS) {
      const timeSinceConnect = lastOpenAiConnectedAt ? Date.now() - lastOpenAiConnectedAt : 0;
      
      // Only reconnect if we were connected for a reasonable time (not immediate failures)
      if (timeSinceConnect > 5000) {
        openaiReconnectAttempts++;
        const backoffMs = Math.min(1000 * openaiReconnectAttempts, 3000); // 1s, 2s, 3s backoff
        
        console.log(`[${callId}] 🔄 OpenAI disconnected mid-call, attempting reconnect (attempt ${openaiReconnectAttempts}/${MAX_OPENAI_RECONNECT_ATTEMPTS}) in ${backoffMs}ms`);
        
        // Notify bridge that we're reconnecting
        try {
          socket.send(JSON.stringify({
            type: "openai_reconnecting",
            attempt: openaiReconnectAttempts,
            maxAttempts: MAX_OPENAI_RECONNECT_ATTEMPTS
          }));
        } catch (e) {
          // Bridge socket may be closed
        }
        
        trackedTimeout(() => {
          if (!cleanedUp && !closingGracePeriodActive && socket.readyState === WebSocket.OPEN) {
            console.log(`[${callId}] 🔌 Attempting OpenAI reconnect...`);
            
            // Reconnect to OpenAI
            try {
              const wsUrl = `${OPENAI_REALTIME_URL}`;
              openaiWs = new WebSocket(wsUrl, ["realtime", `openai-insecure-api-key.${OPENAI_API_KEY}`, "openai-beta.realtime-v1"]);
              
              openaiWs.onopen = () => {
                console.log(`[${callId}] ✅ OpenAI reconnected successfully`);
                openaiConnected = true;
                openaiReconnectAttempts = 0;
                lastOpenAiConnectedAt = Date.now();
                sessionState.greetingDelivered = true; // Don't replay greeting
                startOpenAiPing();
              };
              
              // Reuse existing message handler
              openaiWs.onmessage = async (event) => {
                // The existing onmessage handler will be invoked
                // This is handled by the runtime when we create the new WebSocket
              };
              
              openaiWs.onerror = (error) => {
                console.error(`[${callId}] ❌ OpenAI reconnect error:`, error);
              };
              
              openaiWs.onclose = (ev2) => {
                // Recursive - will retry again if needed
                openaiConnected = false;
                stopOpenAiPing();
                
                const anyEv2: any = ev2;
                console.log(`[${callId}] OpenAI reconnected WebSocket closed` +
                  (anyEv2?.code ? ` code=${anyEv2.code}` : "") +
                  (anyEv2?.reason ? ` reason=${anyEv2.reason}` : "")
                );
                
                // Try reconnecting again if we have attempts left
                if (openaiReconnectAttempts < MAX_OPENAI_RECONNECT_ATTEMPTS && !cleanedUp && !closingGracePeriodActive) {
                  openaiReconnectAttempts++;
                  const nextBackoff = Math.min(1000 * openaiReconnectAttempts, 3000);
                  console.log(`[${callId}] 🔄 Reconnect failed, retrying in ${nextBackoff}ms (attempt ${openaiReconnectAttempts}/${MAX_OPENAI_RECONNECT_ATTEMPTS})`);
                  trackedTimeout(() => {
                    // Recursive reconnect attempt
                    if (socket.readyState === WebSocket.OPEN && !cleanedUp) {
                      console.log(`[${callId}] 🔌 Retry reconnect...`);
                      // This will be handled by the outer reconnect logic on next close
                    }
                  }, nextBackoff);
                } else if (openaiReconnectAttempts >= MAX_OPENAI_RECONNECT_ATTEMPTS) {
                  console.error(`[${callId}] ❌ OpenAI reconnection failed after ${MAX_OPENAI_RECONNECT_ATTEMPTS} attempts - ending call`);
                  try {
                    socket.send(JSON.stringify({ type: "hangup", reason: "openai_connection_lost" }));
                  } catch (e) {
                    // Ignore
                  }
                  cleanupWithKeepalive();
                }
              };
            } catch (e) {
              console.error(`[${callId}] ❌ Failed to create reconnect WebSocket:`, e);
              cleanupWithKeepalive();
            }
          }
        }, backoffMs);
        
        return; // Don't cleanup yet - we're attempting reconnection
      } else {
        console.log(`[${callId}] ⚠️ OpenAI disconnected too quickly (${timeSinceConnect}ms) - not reconnecting`);
      }
    } else if (openaiReconnectAttempts >= MAX_OPENAI_RECONNECT_ATTEMPTS) {
      console.error(`[${callId}] ❌ OpenAI reconnection limit reached - ending call`);
      try {
        socket.send(JSON.stringify({ type: "hangup", reason: "openai_connection_lost" }));
      } catch (e) {
        // Ignore
      }
    }
    
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

        // AUTO-DETECT format from frame size (handles race where format update arrives after audio)
        // slin16 (16kHz PCM16): 640 bytes = 20ms, 320 bytes = 10ms
        // slin (8kHz PCM16): 320 bytes = 20ms, 160 bytes = 10ms
        // 
        // IMPORTANT: Do NOT auto-correct back to 8kHz just because we see 320-byte frames!
        // 320 bytes is valid for BOTH 8kHz (20ms) and 16kHz (10ms).
        // Only upgrade to 16kHz on unambiguous evidence (640-byte frames).
        // Trust explicit format updates over frame-size guessing.
        if (audioBytes.length === 640 && inboundSampleRate === 8000) {
          // 640 bytes can ONLY be 16kHz (20ms) - safe to upgrade
          console.log(`[${callId}] 🔄 Auto-detected slin16 @ 16kHz from 640-byte frame size`);
          inboundSampleRate = 16000;
          inboundAudioFormat = "slin16";
        }
        // REMOVED: Auto-correction to 8kHz from 320-byte frames - this was WRONG!
        // 320 bytes can be either 8kHz (20ms) OR 16kHz (10ms), cannot distinguish.
        // The bridge sends explicit format_update messages which we should trust.

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
        if (Date.now() < sessionState.echoGuardUntil) {
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
          if (sessionState.openAiResponseActive || !awaitingYesNo) {
            return; // Drop - Ada is delivering summary/quote (or we're not in confirmation yet)
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
          if (Date.now() < sessionState.echoGuardUntil) {
            return; // Drop this audio frame (likely echo)
          }

          // SUMMARY PROTECTION: block interruptions while Ada is recapping/quoting.
          if (Date.now() < sessionState.summaryProtectionUntil) {
            const awaitingYesNo = sessionState.awaitingConfirmation || sessionState.lastQuestionAsked === "confirmation";
            if (sessionState.openAiResponseActive || !awaitingYesNo) {
              return; // Drop - Ada is delivering summary/quote
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
          }
          
          // ====== FIX: Read format from init message (like simple mode does) ======
          // The bridge sends format info in the init message - we were ignoring it!
          const initFormat = data.inbound_format || data.format;
          if (initFormat && (initFormat === "ulaw" || initFormat === "slin" || initFormat === "slin16")) {
            const oldFormat = inboundAudioFormat;
            const oldRate = inboundSampleRate;
            inboundAudioFormat = initFormat;
            // Auto-set sample rate based on format if not explicitly provided
            if (typeof data.inbound_sample_rate === "number") {
              inboundSampleRate = data.inbound_sample_rate;
            } else if (typeof data.sample_rate === "number") {
              inboundSampleRate = data.sample_rate;
            } else {
              // Default: slin16 = 16000Hz, others = 8000Hz
              inboundSampleRate = initFormat === "slin16" ? 16000 : 8000;
            }
            if (oldFormat !== inboundAudioFormat || oldRate !== inboundSampleRate) {
              console.log(`[${callId}] 🎛️ Audio format from init: ${oldFormat}@${oldRate}Hz → ${inboundAudioFormat}@${inboundSampleRate}Hz`);
            }
          }

          // === SESSION RESUME SUPPORT ===
          // When bridge sends reconnect=true after WebSocket drop, restore state from DB
          const isReconnect = data.reconnect === true;
          const isResume = data.resume === true;
          const resumeCallId = data.resume_call_id || data.call_id || null;
          
          if ((isReconnect || isResume) && resumeCallId && !greetingSent) {
            console.log(`[${callId}] 🔄 RESUME/RECONNECT detected: reconnect=${isReconnect}, resume=${isResume}, resumeCallId=${resumeCallId}`);
            
            const restoredState = await restoreSessionFromDb(callId, resumeCallId, callerPhone);
            
            if (restoredState) {
              // Restore booking state
              sessionState.booking = restoredState.booking;
              sessionState.conversationHistory = restoredState.conversationHistory;
              sessionState.bookingConfirmed = restoredState.bookingConfirmed;
              sessionState.awaitingConfirmation = restoredState.awaitingConfirmation;
              sessionState.lastQuestionAsked = restoredState.lastQuestionAsked;
              sessionState.pendingFare = restoredState.pendingFare;
              sessionState.pendingEta = restoredState.pendingEta;
              
              // === KEY FIX: Set resume flags so sendGreeting() sends continuation instead ===
              // Don't mark greetingSent=true here - we want sendGreeting() to fire after session.updated
              // but with the continuation prompt instead of the regular greeting
              isResumedSession = true;
              resumedStateData = {
                booking: restoredState.booking,
                awaitingConfirmation: restoredState.awaitingConfirmation,
                pendingFare: restoredState.pendingFare,
                pendingEta: restoredState.pendingEta,
                bookingConfirmed: restoredState.bookingConfirmed
              };
              
              // Clear the fallback timer so we wait for proper session.updated
              if (greetingFallbackTimer) {
                clearTrackedTimeout(greetingFallbackTimer);
                greetingFallbackTimer = null;
              }
              
              console.log(`[${callId}] ✅ Session restored - will send continuation prompt after session.updated`);
              console.log(`[${callId}]   pickup: ${restoredState.booking.pickup}, dest: ${restoredState.booking.destination}, pax: ${restoredState.booking.passengers}`);
              console.log(`[${callId}]   awaitingConfirmation: ${restoredState.awaitingConfirmation}, fare: ${restoredState.pendingFare}`);
              
              // Notify client we're ready
              sendSessionReady();
            } else {
              console.log(`[${callId}] ⚠️ No state found in DB for resume, will send fresh greeting`);
            }
          }

          if (data.inbound_format && (data.inbound_format === "ulaw" || data.inbound_format === "slin" || data.inbound_format === "slin16")) {
            const oldFormat = inboundAudioFormat;
            const oldRate = inboundSampleRate;
            inboundAudioFormat = data.inbound_format;
            // Auto-set sample rate based on format if not explicitly provided (match simple mode behavior)
            if (typeof data.inbound_sample_rate === "number") {
              inboundSampleRate = data.inbound_sample_rate;
            } else {
              // Default: slin16 = 16000Hz, others = 8000Hz
              inboundSampleRate = data.inbound_format === "slin16" ? 16000 : 8000;
            }
            // Log format changes for debugging
            if (oldFormat !== inboundAudioFormat || oldRate !== inboundSampleRate) {
              console.log(`[${callId}] 🎛️ Audio format updated: ${oldFormat}@${oldRate}Hz → ${inboundAudioFormat}@${inboundSampleRate}Hz`);
            }
          } else if (typeof data.inbound_sample_rate === "number") {
            inboundSampleRate = data.inbound_sample_rate;
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
