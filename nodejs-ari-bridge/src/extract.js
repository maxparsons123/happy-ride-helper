/**
 * Slot extraction using taxi-extract-unified edge function
 */

import fetch from 'node-fetch';

/**
 * Extract booking slots from conversation using LLM
 * @param {Array} transcript - Conversation history [{role, text}]
 * @param {object} currentBooking - Current booking state
 * @param {string} callerPhone - Caller phone number
 * @param {object} config - Application config
 * @returns {Promise<object|null>} Extracted slots or null
 */
export async function extractBookingSlots(transcript, currentBooking, callerPhone, config) {
  try {
    // Format conversation for the extraction function
    const conversation = transcript.map(t => ({
      role: t.role,
      content: t.text,
    }));

    const response = await fetch(config.supabase.extractFunctionUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${config.supabase.anonKey}`,
      },
      body: JSON.stringify({
        conversation,
        current_booking: currentBooking,
        caller_phone: callerPhone,
        is_modification: false,
      }),
    });

    if (!response.ok) {
      const errText = await response.text();
      console.error(`Extract error: ${response.status} - ${errText}`);
      return null;
    }

    const data = await response.json();
    
    // Map extracted fields to our booking structure
    return {
      pickup: data.pickup || null,
      destination: data.destination || null,
      passengers: data.passengers ? parseInt(data.passengers, 10) : null,
      pickup_time: data.pickup_time || data.time || null,
    };

  } catch (err) {
    console.error('Extract request failed:', err.message);
    return null;
  }
}
