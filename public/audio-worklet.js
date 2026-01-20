/**
 * AudioWorklet processor for low-latency microphone input
 * Runs off the main thread for glitch-free recording
 */
class RecorderProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    // Batch samples to ~20ms at 24kHz to reduce message spam/overhead.
    this._targetSamples = 480;
    this._buffer = new Int16Array(this._targetSamples);
    this._writeIndex = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || !input[0]) return true;

    const channel = input[0];

    for (let i = 0; i < channel.length; i++) {
      const s = Math.max(-1, Math.min(1, channel[i]));
      this._buffer[this._writeIndex++] = s < 0 ? s * 0x8000 : s * 0x7fff;

      if (this._writeIndex >= this._targetSamples) {
        // Copy out a transfer-owned buffer so we can keep reusing our internal buffer.
        const out = this._buffer.slice(0);
        this.port.postMessage(out.buffer, [out.buffer]);
        this._writeIndex = 0;
      }
    }

    return true;
  }
}

registerProcessor('recorder', RecorderProcessor);
