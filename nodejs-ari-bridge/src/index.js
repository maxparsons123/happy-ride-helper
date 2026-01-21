/**
 * Taxi ARI Bridge - Deterministic Booking Flow
 * 
 * Uses Asterisk ARI for call control with strict sequential question flow.
 * Integrates with taxi-extract-unified for slot extraction.
 */

import AriClient from 'ari-client';
import { config } from './config.js';
import { CallSession } from './call-session.js';

console.log('🚕 Taxi ARI Bridge starting...');
console.log(`   ARI URL: ${config.ari.url}`);
console.log(`   Stasis App: ${config.ari.app}`);

async function main() {
  try {
    const client = await AriClient.connect(
      config.ari.url,
      config.ari.username,
      config.ari.password
    );

    console.log('✅ Connected to Asterisk ARI');

    // Track active sessions
    const sessions = new Map();

    client.on('StasisStart', async (event, channel) => {
      const callerId = channel.caller?.number || 'unknown';
      console.log(`\n📞 [${channel.id}] New call from ${callerId}`);

      // Create new session
      const session = new CallSession(client, channel, config);
      sessions.set(channel.id, session);

      try {
        await session.run();
      } catch (err) {
        console.error(`❌ [${channel.id}] Session error:`, err.message);
      } finally {
        sessions.delete(channel.id);
        console.log(`📴 [${channel.id}] Session ended`);
      }
    });

    client.on('StasisEnd', (event, channel) => {
      const session = sessions.get(channel.id);
      if (session) {
        session.stop('StasisEnd');
        sessions.delete(channel.id);
      }
    });

    // Start the Stasis application
    await client.start(config.ari.app);
    console.log(`🎧 Listening for calls on app: ${config.ari.app}`);

    // Graceful shutdown
    process.on('SIGINT', async () => {
      console.log('\n🛑 Shutting down...');
      for (const [id, session] of sessions) {
        await session.stop('shutdown');
      }
      process.exit(0);
    });

  } catch (err) {
    console.error('❌ Failed to connect to ARI:', err.message);
    process.exit(1);
  }
}

main();
