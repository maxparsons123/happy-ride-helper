#!/usr/bin/env python3

"""Taxi AI Asterisk Bridge (v2.5 - Resilient Reconnect)

Combines v2.4 features with:
- WebSocket reconnection with exponential backoff (survives network blips)
- Graceful reconnect to same call_id (session resume)
- Heartbeat logging for connection health monitoring

"""

import asyncio
import json
import struct
import base64
import time
import logging
from typing import Optional
from collections import deque
import numpy as np
from scipy.signal import resample_poly, butter, sosfilt
import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException

# μ-law codec using numpy (replaces deprecated audioop)
# ITU-T G.711 μ-law encoding/decoding tables
ULAW_BIAS = 0x84
ULAW_CLIP = 32635

def ulaw2lin(ulaw_bytes: bytes) -> bytes:
    """Decode μ-law to 16-bit linear PCM using numpy."""
    ulaw = np.frombuffer(ulaw_bytes, dtype=np.uint8)
    ulaw = ~ulaw  # Complement
    sign = (ulaw & 0x80)
    exponent = (ulaw >> 4) & 0x07
    mantissa = ulaw & 0x0F
    sample = (mantissa << 3) + ULAW_BIAS
    sample <<= exponent
    sample -= ULAW_BIAS
    pcm = np.where(sign != 0, -sample, sample).astype(np.int16)
    return pcm.tobytes()

def lin2ulaw(pcm_bytes: bytes) -> bytes:
    """Encode 16-bit linear PCM to μ-law using numpy (ITU-T G.711 compliant)."""
    pcm = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.int32)
    sign = np.where(pcm < 0, 0x80, 0)
    pcm = np.abs(pcm)
    pcm = np.clip(pcm, 0, ULAW_CLIP)
    pcm += ULAW_BIAS
    
    # Find segment (exponent) - use log2 safely (pcm is always >= ULAW_BIAS after +BIAS)
    exponent = (np.floor(np.log2(np.maximum(pcm, 1)))).astype(np.int32) - 7
    exponent = np.clip(exponent, 0, 7)
    
    # Extract mantissa
    mantissa = (pcm >> (exponent + 3)) & 0x0F
    
    # Combine and complement
    ulaw = (~(sign | (exponent << 4) | mantissa)) & 0xFF
    return ulaw.astype(np.uint8).tobytes()

# --- Configuration ---
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-passthrough-ws"

# Audio rates
AST_RATE = 8000   # Asterisk telephony rate (native µ-law)
AI_RATE = 24000   # AI TTS output rate (for playback)
RESAMPLE_RATIO = AI_RATE // AST_RATE  # = 3

# NEW: Send native 8kHz µ-law to edge function (Deepgram nova-2-phonecall optimized)
SEND_NATIVE_ULAW = True

MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# Reconnection settings
MAX_RECONNECT_ATTEMPTS = 3
RECONNECT_BASE_DELAY_S = 1.0  # 1s, 2s, 4s exponential backoff
HEARTBEAT_INTERVAL_S = 15  # Log connection health every 15 seconds

# Audio processing settings v3 - OPTIMIZED FOR SOFT CONSONANTS (c, t, s, f, th)
# Problem: "cancel" being heard as "council" or "thank you" - soft 'c' was being gated
# Solution: Much gentler DSP - preserve quiet sounds, only remove actual noise floor
NOISE_GATE_THRESHOLD = 25   # Very low - only gate true silence (was 50)
NOISE_GATE_SOFT_KNEE = True # Soft knee instead of hard gate - preserves transients
HIGH_PASS_CUTOFF = 60       # Lower cutoff to preserve more speech fundamentals (was 80)
TARGET_RMS = 2500           # Slightly lower target to avoid over-compression (was 3000)
MAX_GAIN = 3.0              # Moderate gain for quiet speakers (was 2.5)
MIN_GAIN = 0.8              # Prevent gain reduction that would clip soft sounds

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)

# Pre-compute high-pass filter coefficients (gentler 60Hz cutoff)
_highpass_sos = butter(2, HIGH_PASS_CUTOFF, btype='high', fs=AST_RATE, output='sos')


def apply_noise_reduction(audio_bytes: bytes) -> bytes:
    """Apply noise reduction pipeline optimized for soft consonant preservation.
    
    Key improvements for STT accuracy:
    - Soft knee gating preserves attack transients (c, t, p, k sounds)
    - Lower threshold only gates true noise floor
    - Gentler normalization prevents over-compression
    """
    if not audio_bytes or len(audio_bytes) < 4:
        return audio_bytes
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return audio_bytes
    
    # 1. Gentle high-pass filter (60Hz) - remove DC and very low rumble only
    audio_np = sosfilt(_highpass_sos, audio_np)
    
    # 2. Soft-knee noise gate - CRITICAL for soft consonants
    # Instead of hard gating, use smooth transition to preserve transients
    if NOISE_GATE_SOFT_KNEE:
        # Soft knee: gradual attenuation from threshold to 2x threshold
        abs_audio = np.abs(audio_np)
        # Below threshold: attenuate smoothly (not to zero!)
        # Above 2x threshold: full volume
        # Between: smooth transition
        knee_low = NOISE_GATE_THRESHOLD
        knee_high = NOISE_GATE_THRESHOLD * 3  # Wide knee for gentle transition
        
        # Calculate gain curve: 0.15 at silence, 1.0 above knee_high
        gain_curve = np.clip((abs_audio - knee_low) / (knee_high - knee_low), 0, 1)
        gain_curve = 0.15 + 0.85 * gain_curve  # Never fully mute (0.15 floor)
        audio_np *= gain_curve
    else:
        # Hard gate (legacy behavior)
        mask = np.abs(audio_np) < NOISE_GATE_THRESHOLD
        audio_np[mask] *= 0.1
    
    # 3. Gentle normalization - don't over-compress quiet speech
    rms = np.sqrt(np.mean(audio_np ** 2))
    if rms > 30:  # Lower threshold to catch quieter speech (was 50)
        gain = np.clip(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN)
        audio_np *= gain
    
    audio_np = np.clip(audio_np, -32768, 32767).astype(np.int16)
    return audio_np.tobytes()


def resample_audio_linear16(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """High-quality resampling using scipy.signal.resample_poly."""
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return b""
    
    if to_rate > from_rate:
        resampled = resample_poly(audio_np, up=RESAMPLE_RATIO, down=1)
    else:
        resampled = resample_poly(audio_np, up=1, down=RESAMPLE_RATIO)
    
    resampled = np.clip(resampled, -32768, 32767).astype(np.int16)
    return resampled.tobytes()


class TaxiBridgeV25:
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
        self.binary_audio_count = 0
        
        # Reconnection tracking
        self.reconnect_attempts = 0
        self.ws_connected = False
        self.last_ws_activity = time.time()
        self.call_formally_ended = False  # Set when call_ended received from server
        self.init_sent = False  # Track if init with phone has been sent
        
        # Audio buffer for reconnect (keep recent audio to resend after reconnect)
        self.pending_audio_buffer = deque(maxlen=50)  # ~1 second of audio frames

    def _detect_format(self, frame_len):
        if frame_len == 160:
            self.ast_codec, self.ast_frame_bytes = "ulaw", 160
        elif frame_len == 320:
            self.ast_codec, self.ast_frame_bytes = "slin16", 320
        logger.info(f"[{self.call_id}] 🔎 Format: {self.ast_codec} ({frame_len} bytes)")

    async def connect_websocket(self) -> bool:
        """Connect to WebSocket with retry logic."""
        while self.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                delay = RECONNECT_BASE_DELAY_S * (2 ** self.reconnect_attempts) if self.reconnect_attempts > 0 else 0
                if delay > 0:
                    logger.info(f"[{self.call_id}] 🔄 Reconnecting in {delay:.1f}s (attempt {self.reconnect_attempts + 1}/{MAX_RECONNECT_ATTEMPTS})")
                    await asyncio.sleep(delay)
                
                self.ws = await asyncio.wait_for(
                    websockets.connect(WS_URL, ping_interval=5, ping_timeout=10),
                    timeout=10.0
                )
                
                # Only send init here if reconnecting (we already have phone)
                # For fresh connections, wait for UUID message to get phone first
                if self.reconnect_attempts > 0 or self.init_sent:
                    init_msg = {
                        "type": "init",
                        "call_id": self.call_id,
                        "user_phone": self.phone if self.phone != "Unknown" else None,
                        "addressTtsSplicing": True,
                        "reconnect": True
                    }
                    await self.ws.send(json.dumps(init_msg))
                
                self.ws_connected = True
                self.last_ws_activity = time.time()
                is_reconnect = self.reconnect_attempts > 0 or self.init_sent
                self.reconnect_attempts = 0  # Reset on successful connect
                
                logger.info(f"[{self.call_id}] ✅ WebSocket connected" + (" (reconnected)" if is_reconnect else ""))
                return True
                
            except asyncio.TimeoutError:
                logger.warning(f"[{self.call_id}] ⏱️ WebSocket connect timeout")
                self.reconnect_attempts += 1
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ WebSocket connect error: {e}")
                self.reconnect_attempts += 1
        
        logger.error(f"[{self.call_id}] ❌ Failed to connect after {MAX_RECONNECT_ATTEMPTS} attempts")
        return False

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 Call from {peer}")

        # Start playback immediately
        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())

        try:
            # Initial connection
            if not await self.connect_websocket():
                logger.error(f"[{self.call_id}] ❌ Initial connection failed, ending call")
                return
            
            # Main loop with reconnection support
            while self.running:
                try:
                    # Run both directions concurrently
                    await asyncio.gather(
                        self.asterisk_to_ai(),
                        self.ai_to_queue()
                    )
                    # If gather returns normally, connection was closed cleanly
                    break
                    
                except (ConnectionClosed, WebSocketException) as e:
                    if not self.running:
                        break
                    
                    self.ws_connected = False
                    logger.warning(f"[{self.call_id}] 🔌 WebSocket disconnected: {e}")
                    
                    # DON'T reconnect if call was formally ended
                    if self.call_formally_ended:
                        logger.info(f"[{self.call_id}] ⏹️ Skipping reconnect - call was formally ended")
                        break
                    
                    # Try to reconnect
                    self.reconnect_attempts += 1
                    if await self.connect_websocket():
                        logger.info(f"[{self.call_id}] 🔄 Resuming after reconnect")
                        # Resend any buffered audio
                        if self.pending_audio_buffer:
                            logger.info(f"[{self.call_id}] 📤 Resending {len(self.pending_audio_buffer)} buffered audio frames")
                            for audio_data in self.pending_audio_buffer:
                                try:
                                    await self.ws.send(audio_data)
                                except:
                                    break
                            self.pending_audio_buffer.clear()
                    else:
                        logger.error(f"[{self.call_id}] ❌ Reconnect failed, ending call")
                        break
                        
                except Exception as e:
                    logger.error(f"[{self.call_id}] ❌ Unexpected error: {e}")
                    break
                    
        finally:
            self.running = False
            playback_task.cancel()
            heartbeat_task.cancel()
            logger.info(f"[{self.call_id}] 📊 Binary audio frames sent: {self.binary_audio_count}")
            await self.cleanup()

    async def heartbeat_loop(self):
        """Log connection health periodically."""
        while self.running:
            await asyncio.sleep(HEARTBEAT_INTERVAL_S)
            if self.running:
                age = time.time() - self.last_ws_activity
                status = "🟢 healthy" if age < 5 else "🟡 quiet" if age < 15 else "🔴 stale"
                logger.info(f"[{self.call_id}] 💓 Heartbeat: WS={status} (last activity {age:.1f}s ago), queue={len(self.audio_queue)}")

    async def asterisk_to_ai(self):
        while self.running and self.ws_connected:
            try:
                header = await asyncio.wait_for(self.reader.readexactly(3), timeout=30.0)
                m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    raw_hex = payload.hex()
                    if len(raw_hex) >= 12:
                        self.phone = raw_hex[-12:]
                    logger.info(f"[{self.call_id}] 👤 Phone: {self.phone}")
                    # Only send init once per connection
                    if not self.init_sent:
                        await self.ws.send(json.dumps({
                            "type": "init", "call_id": self.call_id,
                            "user_phone": self.phone, "addressTtsSplicing": True
                        }))
                        self.init_sent = True

                elif m_type == MSG_AUDIO:
                    if m_len != self.ast_frame_bytes:
                        self._detect_format(m_len)
                    
                    # Convert to linear16 for noise reduction
                    linear16 = ulaw2lin(payload) if self.ast_codec == "ulaw" else payload
                    cleaned = apply_noise_reduction(linear16)
                    
                    # NEW: Send native 8kHz format (Deepgram nova-2-phonecall optimized)
                    if SEND_NATIVE_ULAW:
                        # Convert cleaned PCM back to µ-law and send as-is (8kHz)
                        out_ulaw = lin2ulaw(cleaned)
                        audio_to_send = out_ulaw
                    else:
                        # Legacy: upsample to 24kHz PCM16
                        audio_to_send = resample_audio_linear16(cleaned, AST_RATE, AI_RATE)
                    
                    # Send as binary frame
                    if self.ws_connected and self.ws:
                        try:
                            await self.ws.send(audio_to_send)
                            self.binary_audio_count += 1
                            self.last_ws_activity = time.time()
                        except:
                            # Buffer audio during disconnect for potential resend
                            self.pending_audio_buffer.append(audio_to_send)
                            raise

                elif m_type == MSG_HANGUP:
                    logger.info(f"[{self.call_id}] 📴 Hangup received from Asterisk")
                    break

            except asyncio.TimeoutError:
                # No data from Asterisk for 30s - might be call ended
                logger.warning(f"[{self.call_id}] ⏱️ Asterisk read timeout")
                break
            except asyncio.IncompleteReadError:
                logger.info(f"[{self.call_id}] 📴 Asterisk connection closed")
                break
            except (ConnectionClosed, WebSocketException):
                raise  # Let the main loop handle reconnection
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ asterisk_to_ai error: {e}")
                break

    async def ai_to_queue(self):
        audio_chunks_received = 0
        try:
            async for message in self.ws:
                self.last_ws_activity = time.time()
                
                # Handle binary audio from AI
                if isinstance(message, bytes):
                    pcm_8k = resample_audio_linear16(message, AI_RATE, AST_RATE)
                    out = lin2ulaw(pcm_8k) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    audio_chunks_received += 1
                    if audio_chunks_received % 50 == 1:
                        logger.info(f"[{self.call_id}] 🔊 Binary audio chunk #{audio_chunks_received}")
                    continue
                
                # JSON message
                data = json.loads(message)
                msg_type = data.get("type")
                
                if msg_type in ["audio", "address_tts"]:
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = resample_audio_linear16(raw_24k, AI_RATE, AST_RATE)
                    out = lin2ulaw(pcm_8k) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    audio_chunks_received += 1
                    if audio_chunks_received % 50 == 1:
                        logger.info(f"[{self.call_id}] 🔊 JSON audio chunk #{audio_chunks_received}")
                elif msg_type == "transcript":
                    logger.info(f"[{self.call_id}] 💬 {data.get('role', 'unknown').upper()}: {data.get('text', '')}")
                elif msg_type == "user_speaking":
                    speaking = data.get("speaking")
                    if speaking is True:
                        logger.info(f"[{self.call_id}] 🗣️ User speaking (start)")
                    elif speaking is False:
                        logger.info(f"[{self.call_id}] 🗣️ User speaking (stop)")
                    else:
                        logger.info(f"[{self.call_id}] 🗣️ User speaking: {speaking}")
                elif msg_type == "session_resumed":
                    resumed = data.get("resumed", False)
                    turn_count = data.get("transcript_count", 0)
                    if resumed:
                        logger.info(f"[{self.call_id}] ✅ Session resumed with {turn_count} conversation turns")
                    else:
                        logger.info(f"[{self.call_id}] ✅ Session resumed (fresh start)")
                elif msg_type == "call_ended":
                    # Call was formally ended - stop reconnection attempts
                    logger.info(f"[{self.call_id}] 📴 Call ended by server: {data.get('reason', 'unknown')}")
                    self.call_formally_ended = True
                    self.running = False
                    break
                elif msg_type == "error":
                    err = data.get("error")
                    retrying = data.get("retrying")
                    if retrying:
                        logger.error(f"[{self.call_id}] 🧨 Backend error (will retry): {err}")
                    else:
                        logger.error(f"[{self.call_id}] 🧨 Backend error: {err}")
                elif msg_type not in ["heartbeat", "session_update"]:
                    logger.info(f"[{self.call_id}] 📨 Received: {msg_type}")
                    
        except (ConnectionClosed, WebSocketException):
            raise  # Let main loop handle reconnection
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ ai_to_queue error: {e}")
        finally:
            logger.info(f"[{self.call_id}] 📊 Total audio chunks received: {audio_chunks_received}")

    async def queue_to_asterisk(self):
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()

        while self.running:
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

            # Strict pacing
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
            if self.ws:
                await self.ws.close()
        except:
            pass
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except:
            pass


async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV25(r, w).run(),
        AUDIOSOCKET_HOST, AUDIOSOCKET_PORT
    )
    logger.info(f"🚀 Taxi Bridge v2.5 Online (Resilient Reconnect)")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
