/**
 * Audio utilities for ARI - recording and playback
 */

import fs from 'fs';
import path from 'path';
import os from 'os';

const SOUNDS_DIR = '/var/lib/asterisk/sounds/taxi';

/**
 * Play audio buffer to channel
 * For ARI, we need to write to a file then play it
 */
export async function playAudioToChannel(client, channel, audioBuffer, config) {
  // Write audio to temp file
  const tempFile = path.join(os.tmpdir(), `tts_${Date.now()}.slin16`);
  
  try {
    // Ensure sounds directory exists
    if (!fs.existsSync(SOUNDS_DIR)) {
      fs.mkdirSync(SOUNDS_DIR, { recursive: true });
    }

    // Write audio buffer to file
    const soundFile = path.join(SOUNDS_DIR, `tts_${Date.now()}`);
    fs.writeFileSync(`${soundFile}.slin16`, audioBuffer);

    // Play via ARI
    const playback = await channel.play({
      media: `sound:${soundFile}`,
    });

    // Wait for playback to finish
    await new Promise((resolve, reject) => {
      playback.on('PlaybackFinished', resolve);
      playback.on('PlaybackFailed', reject);
      
      // Timeout safety
      setTimeout(resolve, 30000);
    });

    // Cleanup
    try {
      fs.unlinkSync(`${soundFile}.slin16`);
    } catch {}

  } catch (err) {
    console.error('Playback error:', err.message);
    throw err;
  }
}

/**
 * Record audio from channel
 * Returns raw PCM buffer
 */
export async function recordFromChannel(client, channel, config) {
  const recordingName = `rec_${channel.id}_${Date.now()}`;
  const maxDuration = Math.floor(config.audio.recordingMaxMs / 1000);
  const silenceTimeout = Math.floor(config.audio.silenceThresholdMs / 1000);

  try {
    // Start recording via ARI
    const recording = await channel.record({
      name: recordingName,
      format: 'slin16',
      maxDurationSeconds: maxDuration,
      maxSilenceSeconds: silenceTimeout,
      beep: false,
      terminateOn: 'none',
    });

    // Wait for recording to finish
    await new Promise((resolve, reject) => {
      recording.on('RecordingFinished', resolve);
      recording.on('RecordingFailed', reject);
      
      // Timeout safety
      setTimeout(() => {
        try {
          recording.stop();
        } catch {}
        resolve();
      }, config.audio.recordingMaxMs + 1000);
    });

    // Read the recorded file
    const recordingPath = `/var/spool/asterisk/recording/${recordingName}.slin16`;
    
    if (fs.existsSync(recordingPath)) {
      const audioBuffer = fs.readFileSync(recordingPath);
      
      // Cleanup
      try {
        fs.unlinkSync(recordingPath);
      } catch {}
      
      return audioBuffer;
    }

    return null;

  } catch (err) {
    console.error('Recording error:', err.message);
    return null;
  }
}

/**
 * Alternative: Use external media for real-time audio
 * This creates an ExternalMedia channel for RTP streaming
 */
export async function createExternalMediaBridge(client, channel, config) {
  // Create a bridge for mixing
  const bridge = await client.bridges.create({ type: 'mixing' });
  
  // Add the channel to the bridge
  await bridge.addChannel({ channel: channel.id });

  // Create external media channel
  const externalChannel = await client.channels.externalMedia({
    app: config.ari.app,
    external_host: `127.0.0.1:${config.audio.rtpPort || 10000}`,
    format: 'slin16',
  });

  // Add external media to bridge
  await bridge.addChannel({ channel: externalChannel.id });

  return {
    bridge,
    externalChannel,
    cleanup: async () => {
      try {
        await bridge.destroy();
      } catch {}
    },
  };
}
