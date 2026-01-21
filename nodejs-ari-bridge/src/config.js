/**
 * Configuration for the Taxi ARI Bridge
 */

import 'dotenv/config';

export const config = {
  // Asterisk ARI connection
  ari: {
    url: process.env.ARI_URL || 'http://localhost:8088',
    username: process.env.ARI_USER || 'taxiapp',
    password: process.env.ARI_PASS || 'supersecret',
    app: process.env.ARI_APP || 'taxi_ai',
  },

  // Supabase Edge Functions
  supabase: {
    url: process.env.SUPABASE_URL || 'https://oerketnvlmptpfvttysy.supabase.co',
    anonKey: process.env.SUPABASE_ANON_KEY || '',
    extractFunctionUrl: process.env.EXTRACT_FUNCTION_URL || 
      'https://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-extract-unified',
    sttFunctionUrl: process.env.STT_FUNCTION_URL ||
      'https://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-stt',
    ttsFunctionUrl: process.env.TTS_FUNCTION_URL ||
      'https://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-tts',
  },

  // Booking flow - strict sequential order
  bookingFlow: {
    fields: ['pickup', 'destination', 'passengers', 'pickup_time'],
    questions: {
      pickup: "Where should we pick you up?",
      destination: "And where are you going to?",
      passengers: "How many passengers will there be?",
      pickup_time: "What time do you need the taxi?",
    },
    retryPrompt: "Sorry, I didn't quite catch that. ",
    maxRetries: 3,
  },

  // Audio settings
  audio: {
    format: 'slin16',        // 16kHz signed linear
    sampleRate: 16000,
    recordingMaxMs: 10000,   // Max 10 seconds per utterance
    silenceThresholdMs: 1500, // 1.5s silence = end of speech
  },

  // TTS voice
  tts: {
    voice: 'shimmer',
  },
};
