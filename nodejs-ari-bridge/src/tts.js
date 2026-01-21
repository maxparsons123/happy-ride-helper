/**
 * Text-to-Speech using taxi-tts edge function (ElevenLabs)
 */

import fetch from 'node-fetch';

/**
 * Synthesize speech from text
 * @param {string} text - Text to speak
 * @param {object} config - Application config
 * @returns {Promise<Buffer>} Audio buffer (MP3 or PCM)
 */
export async function synthesizeSpeech(text, config) {
  if (!text) {
    throw new Error('No text provided for TTS');
  }

  try {
    const response = await fetch(config.supabase.ttsFunctionUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${config.supabase.anonKey}`,
      },
      body: JSON.stringify({
        text,
        voice: config.tts.voice,
        format: 'pcm16', // Request raw PCM for Asterisk
      }),
    });

    if (!response.ok) {
      const errText = await response.text();
      throw new Error(`TTS error: ${response.status} - ${errText}`);
    }

    // Response is audio binary
    const arrayBuffer = await response.arrayBuffer();
    return Buffer.from(arrayBuffer);

  } catch (err) {
    console.error('TTS request failed:', err.message);
    throw err;
  }
}
