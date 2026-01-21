/**
 * Speech-to-Text using taxi-stt edge function (Groq Whisper)
 */

import fetch from 'node-fetch';

/**
 * Transcribe audio buffer to text
 * @param {Buffer} audioBuffer - Raw PCM16 audio at 16kHz
 * @param {object} config - Application config
 * @returns {Promise<string|null>} Transcribed text or null
 */
export async function transcribeAudio(audioBuffer, config) {
  if (!audioBuffer || audioBuffer.length === 0) {
    return null;
  }

  try {
    const base64Audio = audioBuffer.toString('base64');

    const response = await fetch(config.supabase.sttFunctionUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${config.supabase.anonKey}`,
      },
      body: JSON.stringify({
        audio_base64: base64Audio,
        format: 'pcm16',
        sample_rate: config.audio.sampleRate,
      }),
    });

    if (!response.ok) {
      const errText = await response.text();
      console.error(`STT error: ${response.status} - ${errText}`);
      return null;
    }

    const data = await response.json();
    return data.text || null;

  } catch (err) {
    console.error('STT request failed:', err.message);
    return null;
  }
}
