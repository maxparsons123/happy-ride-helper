#!/usr/bin/env python3

"""Taxi AI Asterisk Bridge (v2.4 - Noise Reduction)

Combines the working v2 auto-detection with:
- Binary UUID fix
- High-quality resample_poly for anti-aliased audio (no hiss)
- Binary WebSocket frames for audio (~33% bandwidth savings)
- NEW: Noise gate, high-pass filter, and audio normalization

"""

import asyncio
import json
import struct
import base64
import time
import logging
import audioop
from typing import Optional
from collections import deque
import numpy as np
from scipy.signal import resample_poly, butter, sosfilt
import websockets

# --- Configuration ---
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime"

AST_RATE = 8000
AI_RATE = 24000
# Ratio: 24000 / 8000 = 3 (integer ratio for resample_poly)
RESAMPLE_RATIO = AI_RATE // AST_RATE  # = 3

MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# Audio processing settings
NOISE_GATE_THRESHOLD = 200  # Samples below this are considered noise (int16 range)
HIGH_PASS_CUTOFF = 80  # Hz - removes low rumble (HVAC, traffic)
TARGET_RMS = 3000  # Target RMS level for normalization (int16 range)
MAX_GAIN = 4.0  # Maximum gain to prevent distortion

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)

# Pre-compute high-pass filter coefficients (Butterworth, 2nd order)
# We compute for 8kHz since we filter before upsampling
_highpass_sos = butter(2, HIGH_PASS_CUTOFF, btype='high', fs=AST_RATE, output='sos')


def apply_noise_reduction(audio_bytes: bytes) -> bytes:
    """
    Apply noise reduction pipeline:
    1. High-pass filter (remove low-frequency rumble)
    2. Noise gate (suppress background noise)
    3. Normalize audio levels
    """
    if not audio_bytes or len(audio_bytes) < 4:
        return audio_bytes
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return audio_bytes
    
    # 1. High-pass filter - removes low-frequency rumble (HVAC, traffic, etc.)
    audio_np = sosfilt(_highpass_sos, audio_np)
    
    # 2. Noise gate - suppress samples below threshold
    # This reduces background hiss and ambient noise
    mask = np.abs(audio_np) < NOISE_GATE_THRESHOLD
    audio_np[mask] *= 0.1  # Reduce noise floor by 90%, don't zero completely
    
    # 3. Normalize - boost quiet audio to consistent level
    rms = np.sqrt(np.mean(audio_np ** 2))
    if rms > 50:  # Only normalize if there's actual signal
        gain = min(TARGET_RMS / rms, MAX_GAIN)
        audio_np *= gain
    
    # Clip to int16 range
    audio_np = np.clip(audio_np, -32768, 32767).astype(np.int16)
    return audio_np.tobytes()


def resample_audio_linear16(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """
    High-quality resampling using scipy.signal.resample_poly.
    Uses polyphase filtering with built-in anti-aliasing - faster than FFT
    for integer ratios like 8kHz <-> 24kHz (3x).
    """
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return b""
    
    # resample_poly(x, up, down) - uses polyphase filter with anti-aliasing
    if to_rate > from_rate:
        # Upsampling: 8kHz -> 24kHz (up=3, down=1)
        resampled = resample_poly(audio_np, up=RESAMPLE_RATIO, down=1)
    else:
        # Downsampling: 24kHz -> 8kHz (up=1, down=3)
        resampled = resample_poly(audio_np, up=1, down=RESAMPLE_RATIO)
    
    # Clip to int16 range and convert back
    resampled = np.clip(resampled, -32768, 32767).astype(np.int16)
    return resampled.tobytes()


class TaxiBridgeV2:
    def __init__(self, reader, writer):
        self.reader = reader
        self.writer = writer
        self.ws = None
        self.running = True
        self.audio_queue = deque()
        self.call_id = f"ast-{int(time.time() * 1000)}"
        self.phone = "Unknown"
        self.ast_codec = "slin16"
        self.ast_frame_bytes = 320
        self.binary_audio_count = 0  # Track binary frames sent

    def _detect_format(self, frame_len):
        if frame_len == 160:
            self.ast_codec, self.ast_frame_bytes = "ulaw", 160
        elif frame_len == 320:
            self.ast_codec, self.ast_frame_bytes = "slin16", 320
        logger.info(f"[{self.call_id}] 🔎 Format: {self.ast_codec} ({frame_len} bytes)")

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 Call from {peer}")

        # Start playback immediately to prevent Asterisk timeout
        playback_task = asyncio.create_task(self.queue_to_asterisk())

        try:
            async with websockets.connect(WS_URL, ping_interval=5, ping_timeout=10) as ws:
                self.ws = ws
                # Send init as JSON (text message)
                await self.ws.send(json.dumps({"type": "init", "call_id": self.call_id, "addressTtsSplicing": True}))
                await asyncio.gather(self.asterisk_to_ai(), self.ai_to_queue())
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Error: {e}")
        finally:
            self.running = False
            playback_task.cancel()
            logger.info(f"[{self.call_id}] 📊 Binary audio frames sent: {self.binary_audio_count}")
            await self.cleanup()

    async def asterisk_to_ai(self):
        while self.running:
            try:
                header = await self.reader.readexactly(3)
                m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    # FIXED: Binary safe UUID handling
                    raw_hex = payload.hex()
                    if len(raw_hex) >= 12:
                        self.phone = raw_hex[-12:]
                    logger.info(f"[{self.call_id}] 👤 Phone: {self.phone}")
                    # Send init with phone as JSON (text message)
                    await self.ws.send(json.dumps({
                        "type": "init", "call_id": self.call_id,
                        "user_phone": self.phone, "addressTtsSplicing": True
                    }))

                elif m_type == MSG_AUDIO:
                    if m_len != self.ast_frame_bytes:
                        self._detect_format(m_len)
                    linear16 = audioop.ulaw2lin(payload, 2) if self.ast_codec == "ulaw" else payload
                    
                    # NEW: Apply noise reduction pipeline (high-pass, noise gate, normalize)
                    cleaned = apply_noise_reduction(linear16)
                    upsampled = resample_audio_linear16(cleaned, AST_RATE, AI_RATE)
                    
                    # BINARY PATH: Send raw PCM bytes directly (no base64 overhead)
                    # This reduces CPU usage and bandwidth by ~33%
                    await self.ws.send(upsampled)  # websockets sends bytes as binary frame
                    self.binary_audio_count += 1

                elif m_type == MSG_HANGUP:
                    break

            except:
                break

    async def ai_to_queue(self):
        try:
            async for message in self.ws:
                # Handle binary audio from AI (future optimization)
                if isinstance(message, bytes):
                    # Binary audio from AI - resample and queue
                    pcm_8k = resample_audio_linear16(message, AI_RATE, AST_RATE)
                    out = audioop.lin2ulaw(pcm_8k, 2) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    continue
                
                # JSON message (text)
                data = json.loads(message)
                if data.get("type") in ["audio", "address_tts"]:
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = resample_audio_linear16(raw_24k, AI_RATE, AST_RATE)
                    out = audioop.lin2ulaw(pcm_8k, 2) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                elif data.get("type") == "transcript":
                    logger.info(f"[{self.call_id}] 💬 {data.get('role').upper()}: {data.get('text')}")
        except:
            pass

    async def queue_to_asterisk(self):
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()

        while self.running:
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

            # Strict Pacing
            bytes_per_sec = AST_RATE * (1 if self.ast_codec == "ulaw" else 2)
            expected_time = start_time + (bytes_played / bytes_per_sec)
            await asyncio.sleep(max(0, expected_time - time.time()))

            chunk = bytes(buffer[:self.ast_frame_bytes]) if len(buffer) >= self.ast_frame_bytes else self._silence()
            if len(buffer) >= self.ast_frame_bytes:
                del buffer[:self.ast_frame_bytes]

            try:
                self.writer.write(struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk)
                await self.writer.drain()
                bytes_played += len(chunk)
            except:
                break

    def _silence(self):
        return (b"\xFF" if self.ast_codec == "ulaw" else b"\x00") * self.ast_frame_bytes

    async def cleanup(self):
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except:
            pass


async def main():
    server = await asyncio.start_server(lambda r, w: TaxiBridgeV2(r, w).run(), AUDIOSOCKET_HOST, AUDIOSOCKET_PORT)
    logger.info(f"🚀 Taxi Bridge v2.3 Online (Binary WebSocket)")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
