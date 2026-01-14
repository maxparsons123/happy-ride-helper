#!/usr/bin/env python3

"""Taxi AI Asterisk Bridge v6.0 - OPTIMIZED FOR INSTANT GREETING

Key optimizations over v5:
1. Connects DIRECTLY to taxi-realtime-simple (no redirect overhead)
2. Sends init IMMEDIATELY on connect (before phone number arrives)
3. Phone number sent via update_phone when UUID arrives
4. ~500-1000ms faster greeting compared to v5

Dependencies:
    pip install websockets numpy scipy

Usage:
    python3 taxi_bridge_v6.py

Listens on port 9092 for Asterisk AudioSocket connections.
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


# =============================================================================
# CONFIGURATION - EDIT THESE FOR YOUR SETUP
# =============================================================================

AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092

# DIRECT connection to simple mode (skips redirect, saves ~500ms)
WS_URL = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime-simple"

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
    """Encode 16-bit linear PCM to μ-law."""
    pcm = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.int32)
    sign = np.where(pcm < 0, 0x80, 0)
    pcm = np.abs(pcm)
    pcm = np.clip(pcm, 0, ULAW_CLIP)
    pcm += ULAW_BIAS
    exponent = (np.floor(np.log2(np.maximum(pcm, 1)))).astype(np.int32) - 7
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
    """Resample audio using scipy."""
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return b""
    
    ratio = to_rate // from_rate if to_rate > from_rate else 1
    down = from_rate // to_rate if to_rate < from_rate else 1
    
    if to_rate > from_rate:
        resampled = resample_poly(audio_np, up=ratio, down=1)
    else:
        resampled = resample_poly(audio_np, up=1, down=down)
    
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
        self.audio_queue = deque()
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

        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())

        try:
            # ═══════════════════════════════════════════════════════════════
            # OPTIMIZATION: Connect and send init IMMEDIATELY
            # Don't wait for phone number - it will arrive via update_phone
            # This saves ~500-1000ms on greeting latency!
            # ═══════════════════════════════════════════════════════════════
            if not await self.connect_websocket():
                logger.error(f"[{self.call_id}] ❌ Connection failed")
                return
            
            # Send init with placeholder phone - OpenAI starts warming up NOW
            eager_init = {
                "type": "init",
                "call_id": self.call_id,
                "phone": "unknown",
                "user_phone": "unknown",
                "addressTtsSplicing": True,
                "eager_init": True,
            }
            await self.ws.send(json.dumps(eager_init))
            self.init_sent = True
            logger.info(f"[{self.call_id}] 🚀 Sent EAGER init - greeting will start immediately!")

            # Main loop
            while self.running:
                try:
                    ast_task = asyncio.create_task(self.asterisk_to_ai())
                    ai_task = asyncio.create_task(self.ai_to_queue())

                    done, pending = await asyncio.wait(
                        {ast_task, ai_task},
                        return_when=asyncio.FIRST_COMPLETED,
                    )

                    for t in pending:
                        t.cancel()
                    await asyncio.gather(*pending, return_exceptions=True)

                    for t in done:
                        exc = t.exception()
                        if exc:
                            raise exc
                    break

                except RedirectException as e:
                    logger.info(f"[{self.call_id}] 🔀 Redirect to: {e.url}")
                    self.ws_connected = False
                    try:
                        if self.ws:
                            await self.ws.close(code=1000, reason="Redirecting")
                    except:
                        pass
                    
                    self.reconnect_attempts = 0
                    if await self.connect_websocket(url=e.url, init_data=e.init_data):
                        logger.info(f"[{self.call_id}] ✅ Redirect successful")
                    else:
                        logger.error(f"[{self.call_id}] ❌ Redirect failed")
                        break

                except (ConnectionClosed, WebSocketException) as e:
                    if not self.running:
                        break

                    self.ws_connected = False
                    logger.warning(f"[{self.call_id}] 🔌 Disconnected: {e}")

                    close_code = getattr(e, "code", None)
                    close_reason = (getattr(e, "reason", "") or "").lower()
                    if close_code == 1000 and ("ended" in close_reason or "expired" in close_reason):
                        self.call_formally_ended = True
                        break

                    if self.call_formally_ended:
                        break

                    self.reconnect_attempts += 1
                    if await self.connect_websocket():
                        logger.info(f"[{self.call_id}] 🔄 Reconnected")
                    else:
                        break

                except Exception as e:
                    logger.error(f"[{self.call_id}] ❌ Error: {e}")
                    break

        finally:
            self.running = False
            playback_task.cancel()
            heartbeat_task.cancel()
            logger.info(f"[{self.call_id}] 📊 Audio frames: {self.binary_audio_count}")
            await self.cleanup()

    async def heartbeat_loop(self):
        while self.running:
            await asyncio.sleep(HEARTBEAT_INTERVAL_S)
            if self.running:
                age = time.time() - self.last_ws_activity
                status = "🟢" if age < 5 else "🟡" if age < 15 else "🔴"
                logger.info(f"[{self.call_id}] 💓 {status} (last: {age:.1f}s)")

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
                    
                    # ═══════════════════════════════════════════════════════
                    # OPTIMIZATION: Send phone update (init was already sent)
                    # This allows caller lookup to happen in background
                    # ═══════════════════════════════════════════════════════
                    await self.ws.send(json.dumps({
                        "type": "update_phone",
                        "call_id": self.call_id,
                        "phone": self.phone,
                        "user_phone": self.phone,
                    }))
                    logger.info(f"[{self.call_id}] 📱 Sent phone update")

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
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ Error: {e}")
                await self.stop_call("Error")
                return

    async def ai_to_queue(self):
        audio_count = 0
        try:
            async for message in self.ws:
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
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ ai_to_queue: {e}")
        finally:
            logger.info(f"[{self.call_id}] 📊 Audio received: {audio_count}")

    async def queue_to_asterisk(self):
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()

        while self.running:
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

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
                await self.stop_call("Write failed")
                return

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


# =============================================================================
# MAIN
# =============================================================================

async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV6(r, w).run(),
        AUDIOSOCKET_HOST, AUDIOSOCKET_PORT
    )
    logger.info(f"🚀 Taxi Bridge v6.0 - OPTIMIZED FOR INSTANT GREETING")
    logger.info(f"   Listening on {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}")
    logger.info(f"   Connecting to: {WS_URL}")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
