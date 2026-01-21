/**
 * CallSession - Manages a single call with deterministic booking flow
 */

import { transcribeAudio } from './stt.js';
import { synthesizeSpeech } from './tts.js';
import { extractBookingSlots } from './extract.js';
import { playAudioToChannel, recordFromChannel } from './audio.js';

export class CallSession {
  constructor(client, channel, config) {
    this.client = client;
    this.channel = channel;
    this.config = config;
    this.callId = channel.id;
    this.callerId = channel.caller?.number || 'unknown';
    this.running = true;

    // Booking state - null means not yet collected
    this.booking = {
      pickup: null,
      destination: null,
      passengers: null,
      pickup_time: null,
    };

    // Conversation transcript for context
    this.transcript = [];
  }

  log(msg) {
    console.log(`[${this.callId.slice(0, 8)}] ${msg}`);
  }

  async run() {
    try {
      await this.channel.answer();
      this.log('📞 Call answered');

      // Greeting
      await this.say("Hello, thank you for calling. I'll help you book a taxi.");

      // Collect each field in strict order
      for (const field of this.config.bookingFlow.fields) {
        if (!this.running) break;
        await this.collectField(field);
      }

      if (this.running && this.isBookingComplete()) {
        await this.confirmBooking();
      }

    } finally {
      await this.hangup();
    }
  }

  /**
   * Collect a single field - loops until filled or max retries
   */
  async collectField(fieldName) {
    const { questions, retryPrompt, maxRetries } = this.config.bookingFlow;
    let attempts = 0;

    while (!this.booking[fieldName] && attempts < maxRetries && this.running) {
      attempts++;

      // Ask the question (with retry prefix if needed)
      const prefix = attempts > 1 ? retryPrompt : '';
      const question = prefix + questions[fieldName];
      
      await this.say(question);
      this.transcript.push({ role: 'assistant', text: question });

      // Record user response
      const audioBuffer = await this.listen();
      if (!audioBuffer || audioBuffer.length === 0) {
        this.log(`⚠️ No audio captured for ${fieldName}`);
        continue;
      }

      // Transcribe
      const userText = await transcribeAudio(audioBuffer, this.config);
      if (!userText) {
        this.log(`⚠️ STT returned empty for ${fieldName}`);
        continue;
      }

      this.log(`🎤 User: "${userText}"`);
      this.transcript.push({ role: 'user', text: userText });

      // Extract slots using LLM
      const extracted = await extractBookingSlots(
        this.transcript,
        this.booking,
        this.callerId,
        this.config
      );

      // Merge extracted data (only fill nulls)
      this.mergeExtracted(extracted);

      if (this.booking[fieldName]) {
        this.log(`✅ ${fieldName} = "${this.booking[fieldName]}"`);
      } else {
        this.log(`❓ ${fieldName} still missing after attempt ${attempts}`);
      }
    }

    if (!this.booking[fieldName]) {
      this.log(`❌ Failed to collect ${fieldName} after ${maxRetries} attempts`);
      await this.say("I'm having trouble understanding. Let me transfer you to an operator.");
      this.running = false;
    }
  }

  /**
   * Confirm the complete booking with user
   */
  async confirmBooking() {
    const { pickup, destination, passengers, pickup_time } = this.booking;
    
    const summary = `So that's a taxi from ${pickup} to ${destination}, ` +
      `for ${passengers} passenger${passengers > 1 ? 's' : ''}, ` +
      `at ${pickup_time}. Is that correct?`;

    await this.say(summary);
    this.transcript.push({ role: 'assistant', text: summary });

    const audioBuffer = await this.listen();
    const userText = await transcribeAudio(audioBuffer, this.config);
    
    this.log(`🎤 Confirmation response: "${userText}"`);
    this.transcript.push({ role: 'user', text: userText });

    const isConfirmed = /^(yes|yeah|correct|that'?s? right|yep|sure|ok|okay)/i.test(userText || '');

    if (isConfirmed) {
      await this.say("Your taxi is booked. Thank you for calling, goodbye!");
      this.log(`🎉 Booking confirmed: ${JSON.stringify(this.booking)}`);
      // TODO: Send to dispatch webhook here
    } else {
      await this.say("No problem, let's start again.");
      // Reset booking and restart
      this.booking = { pickup: null, destination: null, passengers: null, pickup_time: null };
      this.transcript = [];
      await this.run(); // Recursive restart
    }
  }

  /**
   * Merge extracted slots into booking (only fill nulls)
   */
  mergeExtracted(extracted) {
    if (!extracted) return;

    for (const key of Object.keys(this.booking)) {
      if (this.booking[key] === null && extracted[key]) {
        this.booking[key] = extracted[key];
      }
    }
  }

  isBookingComplete() {
    return Object.values(this.booking).every(v => v !== null);
  }

  /**
   * Speak text to caller via TTS
   */
  async say(text) {
    this.log(`🔊 Ada: "${text}"`);
    try {
      const audioBuffer = await synthesizeSpeech(text, this.config);
      await playAudioToChannel(this.client, this.channel, audioBuffer, this.config);
    } catch (err) {
      this.log(`⚠️ TTS/playback error: ${err.message}`);
    }
  }

  /**
   * Listen for user speech
   */
  async listen() {
    try {
      return await recordFromChannel(this.client, this.channel, this.config);
    } catch (err) {
      this.log(`⚠️ Recording error: ${err.message}`);
      return null;
    }
  }

  async hangup() {
    try {
      await this.channel.hangup();
    } catch (err) {
      // Channel may already be gone
    }
  }

  stop(reason) {
    this.log(`🛑 Stopping: ${reason}`);
    this.running = false;
  }
}
