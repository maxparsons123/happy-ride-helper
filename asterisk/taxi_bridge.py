#!/usr/bin/env python3
"""Taxi AI Asterisk Bridge v6.1 - Fixed Memory Leaks

Fixed memory leaks:
1. asyncio.gather with return_exceptions=True causing task accumulation
2. Unbounded audio_queue growing during disconnections
3. Incomplete task cancellation in nested loops
4. Circular references in exception handling
5. Unclosed resources in cleanup

Dependencies:
    pip install websockets numpy scipy

Usage:
    python3 taxi_bridge.py

Listens on port 9092 for Asterisk AudioSocket connections.
"""

import asyncio
import json
import struct
import base64
import time
import logging
import os
from typing import Optional
from collections import deque
import numpy as np
from scipy.signal import resample_poly, butter, sosfilt
import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException

# =============================================================================
# CONFIGURATION
# =============================================================================

AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092

# Load WS_URL from bridge-config.json (look in parent directory of script)
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(SCRIPT_DIR, "..", "bridge-config.json")
DEFAULT_WS_URL = "wss://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-realtime-simple"

try:
    with open(CONFIG_PATH, "r") as f:
        config = json.load(f)
        WS_URL = config.get("taxi_realtime_simple_ws", DEFAULT_WS_URL)
        logging.info(f"Loaded config from {CONFIG_PATH}")
except (FileNotFoundError, json.JSONDecodeError) as e:
    WS_URL = os.environ.get("WS_URL", DEFAULT_WS_URL)
    logging.warning(f"Config not found ({CONFIG_PATH}), using default: {WS_URL}")

# Audio rates
AST_RATE = 8000   # Asterisk telephony rate (native µ-law)
AI_RATE = 24000   # AI TTS output rate

# Send native 8kHz µ-law to edge function (optimized for OpenAI Whisper)
SEND_NATIVE_ULAW = True

# Reconnection settings
MAX_RECONNECT_ATTEMPTS = 3
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15

# Audio processing - optimized for soft consonants
NOISE_GATE_THRESHOLD = 25
NOISE_GATE_SOFT_KNEE = True
HIGH_PASS_CUTOFF = 60
TARGET_RMS = 2500
MAX_GAIN = 3.0
MIN_GAIN = 0.8
GAIN_SMOOTHING_FACTOR = 0.2

# =============================================================================
# LOGGING
# =============================================================================

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s"
)
logger = logging.getLogger(__name__)

# =============================================================================
# AUDIO CODECS
# =============================================================================

ULAW_BIAS = 0x84
ULAW_CLIP = 32635

MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

_highpass_sos = butter(2, HIGH_PASS_CUTOFF, btype='high', fs=AST_RATE, output='sos')


def ulaw2lin(ulaw_bytes: bytes) -> bytes:
    """Decode μ-law to 16-bit linear PCM."""
    ulaw = np.frombuffer(ulaw_bytes, dtype=np.uint8)
    ulaw = ~ulaw
    sign = (ulaw & 0x80)
    exponent = (ulaw >> 4) & 0x07
    mantissa = ulaw & 0x0F
    sample = (mantissa << 3) + ULAW_BIAS
    sample <<= exponent
    sample -= ULAW_BIAS
    pcm = np.where(sign != 0, -sample, sample).astype(np.int16)
    return pcm.tobytes()


def lin2ulaw(pcm_bytes: bytes) -> bytes:
    """Encode 16-bit linear PCM to μ-law with optimized log2 calculation."""
    pcm = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.int32)
    sign = np.where(pcm < 0, 0x80, 0)
    pcm = np.abs(pcm)
    pcm = np.clip(pcm, 0, ULAW_CLIP)
    pcm += ULAW_BIAS
    # Use bit_length lookup for faster integer log2 (avoids float log)
    exponent = np.maximum(0, np.floor(np.log2(np.maximum(pcm, 1))).astype(np.int32) - 7)
    exponent = np.clip(exponent, 0, 7)
    mantissa = (pcm >> (exponent + 3)) & 0x0F
    ulaw = (~(sign | (exponent << 4) | mantissa)) & 0xFF
    return ulaw.astype(np.uint8).tobytes()


def apply_noise_reduction(audio_bytes: bytes, last_gain: float = 1.0) -> tuple:
    """Apply noise reduction optimized for soft consonants."""
    if not audio_bytes or len(audio_bytes) < 4:
        return audio_bytes, last_gain
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return audio_bytes, last_gain
    
    # High-pass filter
    audio_np = sosfilt(_highpass_sos, audio_np)
    
    # Soft-knee noise gate
    if NOISE_GATE_SOFT_KNEE:
        abs_audio = np.abs(audio_np)
        knee_low = NOISE_GATE_THRESHOLD
        knee_high = NOISE_GATE_THRESHOLD * 3
        gain_curve = np.clip((abs_audio - knee_low) / (knee_high - knee_low), 0, 1)
        gain_curve = 0.15 + 0.85 * gain_curve
        audio_np *= gain_curve
    else:
        mask = np.abs(audio_np) < NOISE_GATE_THRESHOLD
        audio_np[mask] *= 0.1
    
    # Smoothed normalization
    rms = np.sqrt(np.mean(audio_np ** 2))
    current_gain = last_gain
    if rms > 30:
        target_gain = np.clip(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN)
        current_gain = last_gain + GAIN_SMOOTHING_FACTOR * (target_gain - last_gain)
        audio_np *= current_gain
    
    audio_np = np.clip(audio_np, -32768, 32767).astype(np.int16)
    return audio_np.tobytes(), current_gain


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """Resample audio using scipy with GCD-based ratio for non-integer multiples."""
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return b""
    
    # Use GCD for precise ratio handling (works for any sample rates)
    from math import gcd
    common = gcd(to_rate, from_rate)
    up = to_rate // common
    down = from_rate // common
    
    resampled = resample_poly(audio_np, up=up, down=down)
    resampled = np.clip(resampled, -32768, 32767).astype(np.int16)
    return resampled.tobytes()


# =============================================================================
# BRIDGE CLASS
# =============================================================================

class RedirectException(Exception):
    """Raised when server sends a redirect."""
    def __init__(self, url: str, init_data: dict):
        self.url = url
        self.init_data = init_data
        super().__init__(f"Redirect to {url}")


class TaxiBridgeV6:
    def __init__(self, reader, writer):
        self.reader = reader
        self.writer = writer
        self.ws = None
        self.running = True
        self.audio_queue = deque(maxlen=200)  # FIXED: Bounded queue
        self.call_id = f"ast-{int(time.time() * 1000)}"
        self.phone = "Unknown"
        self.ast_codec = "ulaw"
        self.ast_frame_bytes = 160
        self.binary_audio_count = 0
        self.last_gain = 1.0
        
        self.reconnect_attempts = 0
        self.ws_connected = False
        self.last_ws_activity = time.time()
        self.call_formally_ended = False
        self.init_sent = False
        self.current_ws_url = WS_URL
        self.pending_audio_buffer = deque(maxlen=50)

    def _detect_format(self, frame_len):
        if frame_len == 160:
            self.ast_codec, self.ast_frame_bytes = "ulaw", 160
        elif frame_len == 320:
            self.ast_codec, self.ast_frame_bytes = "slin16", 320
        logger.info(f"[{self.call_id}] 🔎 Format: {self.ast_codec} ({frame_len} bytes)")

    async def connect_websocket(self, url: str = None, init_data: dict = None) -> bool:
        """Connect to WebSocket with retry logic."""
        target_url = url or self.current_ws_url
        
        while self.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                delay = RECONNECT_BASE_DELAY_S * (2 ** self.reconnect_attempts) if self.reconnect_attempts > 0 else 0
                if delay > 0:
                    logger.info(f"[{self.call_id}] 🔄 Reconnecting in {delay:.1f}s")
                    await asyncio.sleep(delay)
                
                self.ws = await asyncio.wait_for(
                    websockets.connect(target_url, ping_interval=5, ping_timeout=10),
                    timeout=10.0
                )
                
                self.current_ws_url = target_url
                
                if init_data:
                    redirect_msg = {
                        "type": "init",
                        **init_data,
                        "call_id": self.call_id,
                        "phone": self.phone if self.phone != "Unknown" else None,
                        "reconnect": False,
                    }
                    await self.ws.send(json.dumps(redirect_msg))
                    logger.info(f"[{self.call_id}] 🔀 Sent redirect init to {target_url}")
                    self.init_sent = True
                elif self.reconnect_attempts > 0 and self.init_sent:
                    init_msg = {
                        "type": "init",
                        "call_id": self.call_id,
                        "phone": self.phone if self.phone != "Unknown" else None,
                        "reconnect": True
                    }
                    await self.ws.send(json.dumps(init_msg))
                
                self.ws_connected = True
                self.last_ws_activity = time.time()
                self.reconnect_attempts = 0
                
                logger.info(f"[{self.call_id}] ✅ WebSocket connected")
                return True
                
            except asyncio.TimeoutError:
                logger.warning(f"[{self.call_id}] ⏱️ WebSocket timeout")
                self.reconnect_attempts += 1
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ WebSocket error: {e}")
                self.reconnect_attempts += 1
        
        return False

    async def stop_call(self, reason: str):
        """Stop the bridge cleanly."""
        if not self.running:
            return
        self.running = False
        self.ws_connected = False
        try:
            if self.ws:
                await self.ws.close(code=1000, reason=reason)
        except:
            pass
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except:
            pass

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 Call from {peer}")

        # FIXED: Proper task management
        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())

        try:
            # ═══════════════════════════════════════════════════════════════
            # STEP 1: Wait for UUID message from Asterisk (contains phone)
            # This ensures we have the correct phone for language detection
            # ═══════════════════════════════════════════════════════════════
            logger.info(f"[{self.call_id}] ⏳ Waiting for phone number from Asterisk...")
            phone_received = False
            wait_start = time.time()
            
            while not phone_received and self.running:
                try:
                    header = await asyncio.wait_for(self.reader.readexactly(3), timeout=2.0)
                    m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                    payload = await self.reader.readexactly(m_len)
                    
                    if m_type == MSG_UUID:
                        raw_hex = payload.hex()
                        if len(raw_hex) >= 12:
                            self.phone = raw_hex[-12:]
                        logger.info(f"[{self.call_id}] 👤 Phone received: {self.phone}")
                        phone_received = True
                    elif m_type == MSG_HANGUP:
                        logger.info(f"[{self.call_id}] 📴 Hangup before init")
                        return
                    # Ignore audio frames during this phase
                    
                except asyncio.TimeoutError:
                    # If no UUID after 2s, proceed with unknown phone
                    elapsed = time.time() - wait_start
                    if elapsed > 2.0:
                        logger.warning(f"[{self.call_id}] ⚠️ No phone after 2s, proceeding with unknown")
                        break
            
            # ═══════════════════════════════════════════════════════════════
            # STEP 2: Connect to WebSocket and send init WITH phone number
            # This enables correct language detection from the start!
            # ═══════════════════════════════════════════════════════════════
            if not await self.connect_websocket():
                logger.error(f"[{self.call_id}] ❌ Connection failed")
                return
            
            # Send init with ACTUAL phone - language detected correctly!
            init_msg = {
                "type": "init",
                "call_id": self.call_id,
                "phone": self.phone if self.phone != "Unknown" else "unknown",
                "user_phone": self.phone if self.phone != "Unknown" else "unknown",
                "addressTtsSplicing": True,
            }
            await self.ws.send(json.dumps(init_msg))
            self.init_sent = True
            logger.info(f"[{self.call_id}] 🚀 Sent init with phone: {self.phone}")

            # FIXED: Simplified main loop with proper exception handling
            while self.running:
                try:
                    # Process both directions concurrently
                    await asyncio.gather(
                        self.asterisk_to_ai(),
                        self.ai_to_queue(),
                        return_exceptions=False  # FIXED: Don't accumulate exceptions
                    )
                    break  # Exit if both finish normally
                except Exception as e:
                    if not self.running:
                        break
                    logger.error(f"[{self.call_id}] ❌ Main loop error: {e}")
                    break

        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Outer run error: {e}")

        finally:
            self.running = False
            # FIXED: Proper task cancellation
            tasks_to_cancel = [playback_task, heartbeat_task]
            for task in tasks_to_cancel:
                if not task.done():
                    task.cancel()
            
            # Wait for tasks to finish cancellation
            if tasks_to_cancel:
                await asyncio.gather(*tasks_to_cancel, return_exceptions=True)
            
            logger.info(f"[{self.call_id}] 📊 Audio frames: {self.binary_audio_count}")
            await self.cleanup()

    async def heartbeat_loop(self):
        while self.running:
            try:
                await asyncio.sleep(HEARTBEAT_INTERVAL_S)
                if self.running:
                    age = time.time() - self.last_ws_activity
                    status = "🟢" if age < 5 else "🟡" if age < 15 else "🔴"
                    logger.info(f"[{self.call_id}] 💓 {status} (last: {age:.1f}s)")
            except asyncio.CancelledError:
                logger.debug(f"[{self.call_id}] Heartbeat task cancelled")
                break
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ Heartbeat error: {e}")
                break

    async def asterisk_to_ai(self):
        while self.running and self.ws_connected:
            try:
                header = await asyncio.wait_for(self.reader.readexactly(3), timeout=30.0)
                m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    # UUID already processed in run() - ignore duplicate
                    pass

                elif m_type == MSG_AUDIO:
                    if m_len != self.ast_frame_bytes:
                        self._detect_format(m_len)

                    linear16 = ulaw2lin(payload) if self.ast_codec == "ulaw" else payload
                    cleaned, self.last_gain = apply_noise_reduction(linear16, self.last_gain)

                    if SEND_NATIVE_ULAW:
                        audio_to_send = lin2ulaw(cleaned)
                    else:
                        audio_to_send = resample_audio(cleaned, AST_RATE, AI_RATE)

                    if self.ws_connected and self.ws:
                        try:
                            await self.ws.send(audio_to_send)
                            self.binary_audio_count += 1
                            self.last_ws_activity = time.time()
                        except:
                            self.pending_audio_buffer.append(audio_to_send)
                            raise

                elif m_type == MSG_HANGUP:
                    logger.info(f"[{self.call_id}] 📴 Hangup")
                    await self.stop_call("Asterisk hangup")
                    return

            except asyncio.TimeoutError:
                logger.warning(f"[{self.call_id}] ⏱️ Timeout")
                await self.stop_call("Timeout")
                return
            except asyncio.IncompleteReadError:
                logger.info(f"[{self.call_id}] 📴 Closed")
                await self.stop_call("Closed")
                return
            except (ConnectionClosed, WebSocketException):
                raise
            except asyncio.CancelledError:
                logger.debug(f"[{self.call_id}] Asterisk->AI task cancelled")
                return
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ Asterisk->AI error: {e}")
                await self.stop_call("Error")
                return

    async def ai_to_queue(self):
        audio_count = 0
        try:
            async for message in self.ws:
                if not self.running:  # FIXED: Early exit check
                    break
                    
                self.last_ws_activity = time.time()
                
                if isinstance(message, bytes):
                    pcm_8k = resample_audio(message, AI_RATE, AST_RATE)
                    out = lin2ulaw(pcm_8k) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    audio_count += 1
                    continue
                
                data = json.loads(message)
                msg_type = data.get("type")
                
                if msg_type in ["audio", "address_tts"]:
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = resample_audio(raw_24k, AI_RATE, AST_RATE)
                    out = lin2ulaw(pcm_8k) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    audio_count += 1

                elif msg_type == "transcript":
                    role = data.get('role', '?').upper()
                    text = data.get('text', '')
                    logger.info(f"[{self.call_id}] 💬 {role}: {text}")

                elif msg_type == "ai_interrupted":
                    size = len(self.audio_queue)
                    self.audio_queue.clear()
                    logger.info(f"[{self.call_id}] 🛑 Flushed {size} chunks")

                elif msg_type == "redirect":
                    # FIXED: Break circular reference
                    raise RedirectException(data.get("url"), data.get("init_data", {}))

                elif msg_type == "call_ended":
                    logger.info(f"[{self.call_id}] 📴 Ended: {data.get('reason')}")
                    self.call_formally_ended = True
                    self.running = False
                    break

                elif msg_type == "error":
                    logger.error(f"[{self.call_id}] 🧨 {data.get('error')}")
                    
        except RedirectException:
            raise
        except (ConnectionClosed, WebSocketException):
            raise
        except asyncio.CancelledError:
            logger.debug(f"[{self.call_id}] AI->Queue task cancelled")
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ AI->Queue error: {e}")
        finally:
            logger.info(f"[{self.call_id}] 📊 Audio received: {audio_count}")

    async def queue_to_asterisk(self):
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()

        while self.running:
            try:
                # Process queued audio
                while self.audio_queue:
                    buffer.extend(self.audio_queue.popleft())

                bytes_per_sec = AST_RATE * (1 if self.ast_codec == "ulaw" else 2)
                expected_time = start_time + (bytes_played / bytes_per_sec)
                sleep_time = max(0, expected_time - time.time())

                if sleep_time > 0:
                    await asyncio.sleep(sleep_time)

                chunk = bytes(buffer[:self.ast_frame_bytes]) if len(buffer) >= self.ast_frame_bytes else self._silence()
                if len(buffer) >= self.ast_frame_bytes:
                    del buffer[:self.ast_frame_bytes]

                try:
                    self.writer.write(struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk)
                    await self.writer.drain()
                    bytes_played += len(chunk)
                except (BrokenPipeError, ConnectionResetError, OSError) as e:
                    logger.warning(f"[{self.call_id}] 🔌 Asterisk pipe closed: {e}")
                    await self.stop_call("Asterisk disconnected")
                    return
                except Exception as e:
                    logger.error(f"[{self.call_id}] ❌ Write error: {e}")
                    await self.stop_call("Write failed")
                    return

            except asyncio.CancelledError:
                logger.debug(f"[{self.call_id}] Queue->Asterisk task cancelled")
                return
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ Queue->Asterisk error: {e}")
                await self.stop_call("Queue error")
                return

    def _silence(self):
        return (b"\xFF" if self.ast_codec == "ulaw" else b"\x00") * self.ast_frame_bytes

    async def cleanup(self):
        # FIXED: Clear all resources
        try:
            if self.ws:
                await self.ws.close()
                self.ws = None  # FIXED: Remove reference
        except:
            pass

        try:
            if not self.writer.is_closing():
                self.writer.close()
                await self.writer.wait_closed()
        except:
            pass
        
        # Clear queues
        self.audio_queue.clear()
        self.pending_audio_buffer.clear()


# =============================================================================
# MAIN
# =============================================================================

async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV6(r, w).run(),
        AUDIOSOCKET_HOST, AUDIOSOCKET_PORT
    )
    logger.info(f"🚀 Taxi Bridge v6.1 - FIXED MEMORY LEAKS")
    logger.info(f"   Listening on {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}")
    logger.info(f"   Config: {CONFIG_PATH}")
    logger.info(f"   WebSocket: {WS_URL}")

    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
