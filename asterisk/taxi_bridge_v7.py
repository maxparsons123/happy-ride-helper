#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge v7.0 - OPTIMIZED FOR INSTANT GREETING

Key features:
1. Direct connection to taxi-realtime-simple (no redirect function hop).
2. Sends init IMMEDIATELY on connect (before phone number arrives).
3. Phone number sent later via update_phone once UUID arrives.
4. Native 8kHz µ-law send-path for faster, smaller audio frames.
5. Smoother noise-gate + normalization tuned for soft consonants.
6. Improved error handling and connection resilience.
7. GCD-based resampling for precise audio conversion.

Changes from v6:
- Uses dataclass for cleaner state management
- Improved exception handling with specific error types
- Better reconnect delay calculation
- Enhanced logging with consistent formatting
"""

import asyncio
import base64
import json
import logging
import struct
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Deque, Optional, Tuple

import numpy as np
from scipy.signal import butter, resample_poly, sosfilt
import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException


# =============================================================================
# CONFIGURATION
# =============================================================================

AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092

# DIRECT connection to realtime function
WS_URL = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime-simple"

# Audio rates
AST_RATE = 8000   # Asterisk telephony
AI_RATE = 24000   # OpenAI TTS

# Send native 8kHz µ-law to edge function (avoids resample artifacts)
SEND_NATIVE_ULAW = True

# Reconnection settings
MAX_RECONNECT_ATTEMPTS = 3
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15.0

# Audio processing - tuned for soft consonants
NOISE_GATE_THRESHOLD = 25
NOISE_GATE_SOFT_KNEE = True
HIGH_PASS_CUTOFF = 60
TARGET_RMS = 2500
MAX_GAIN = 3.0
MIN_GAIN = 0.8
GAIN_SMOOTHING_FACTOR = 0.2

# Asterisk message types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# µ-law constants
ULAW_BIAS = 0x84
ULAW_CLIP = 32635


# =============================================================================
# LOGGING
# =============================================================================

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
logger = logging.getLogger("TaxiBridgeV7")


# =============================================================================
# AUDIO HELPERS
# =============================================================================

# Pre-compute high-pass filter coefficients
_highpass_sos = butter(2, HIGH_PASS_CUTOFF, btype="high", fs=AST_RATE, output="sos")


def ulaw2lin(ulaw_bytes: bytes) -> bytes:
    """Decode µ-law to 16-bit linear PCM."""
    if not ulaw_bytes:
        return b""
    ulaw = np.frombuffer(ulaw_bytes, dtype=np.uint8)
    ulaw = ~ulaw
    sign = ulaw & 0x80
    exponent = (ulaw >> 4) & 0x07
    mantissa = ulaw & 0x0F

    sample = (mantissa.astype(np.int32) << 3) + ULAW_BIAS
    sample <<= exponent
    sample -= ULAW_BIAS
    pcm = np.where(sign != 0, -sample, sample).astype(np.int16)
    return pcm.tobytes()


def lin2ulaw(pcm_bytes: bytes) -> bytes:
    """Encode 16-bit linear PCM to µ-law."""
    if not pcm_bytes:
        return b""
    pcm = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.int32)
    sign = np.where(pcm < 0, 0x80, 0)
    pcm = np.abs(pcm)
    pcm = np.clip(pcm, 0, ULAW_CLIP)
    pcm += ULAW_BIAS

    # Safe log2 calculation
    pcm_safe = np.maximum(pcm, 1)
    exponent = np.floor(np.log2(pcm_safe)).astype(np.int32) - 7
    exponent = np.clip(exponent, 0, 7)
    mantissa = (pcm >> (exponent + 3)) & 0x0F
    ulaw = (~(sign | (exponent << 4) | mantissa)) & 0xFF
    return ulaw.astype(np.uint8).tobytes()


def apply_noise_reduction(audio_bytes: bytes, last_gain: float = 1.0) -> Tuple[bytes, float]:
    """Noise reduction tuned for soft consonants."""
    if not audio_bytes or len(audio_bytes) < 4:
        return audio_bytes, last_gain

    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return audio_bytes, last_gain

    # High-pass filter (remove rumble / DC offset)
    audio_np = sosfilt(_highpass_sos, audio_np)

    # Soft-knee noise gate
    if NOISE_GATE_SOFT_KNEE:
        abs_audio = np.abs(audio_np)
        knee_low = NOISE_GATE_THRESHOLD
        knee_high = NOISE_GATE_THRESHOLD * 3
        gain_curve = np.clip((abs_audio - knee_low) / (knee_high - knee_low), 0.0, 1.0)
        gain_curve = 0.15 + 0.85 * gain_curve
        audio_np *= gain_curve
    else:
        mask = np.abs(audio_np) < NOISE_GATE_THRESHOLD
        audio_np[mask] *= 0.1

    # RMS-based auto-gain with smoothing
    rms = float(np.sqrt(np.mean(audio_np ** 2))) if audio_np.size else 0.0
    current_gain = last_gain
    if rms > 30:
        target_gain = float(np.clip(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN))
        current_gain = last_gain + GAIN_SMOOTHING_FACTOR * (target_gain - last_gain)
        audio_np *= current_gain

    audio_np = np.clip(audio_np, -32768, 32767).astype(np.int16)
    return audio_np.tobytes(), current_gain


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """High-quality resampling using scipy.signal.resample_poly.
    
    Uses fixed 3:1 ratio for 8kHz<->24kHz conversions (proven to work in v5).
    """
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return b""
    
    # Fixed ratio for telephony <-> AI audio (same as v5 which works)
    RESAMPLE_RATIO = 3  # 24000 / 8000 = 3
    
    if to_rate > from_rate:
        # Upsampling: 8kHz -> 24kHz
        resampled = resample_poly(audio_np, up=RESAMPLE_RATIO, down=1)
    else:
        # Downsampling: 24kHz -> 8kHz
        resampled = resample_poly(audio_np, up=1, down=RESAMPLE_RATIO)
    
    resampled = np.clip(resampled, -32768, 32767).astype(np.int16)
    return resampled.tobytes()


# =============================================================================
# EXCEPTIONS
# =============================================================================

class RedirectException(Exception):
    """Raised when server sends a redirect."""
    def __init__(self, url: str, init_data: dict):
        self.url = url
        self.init_data = init_data
        super().__init__(f"Redirect to {url}")


class CallEndedException(Exception):
    """Raised when the call has formally ended."""
    pass


# =============================================================================
# STATE DATACLASS
# =============================================================================

@dataclass
class CallState:
    call_id: str
    phone: str = "Unknown"
    ast_codec: str = "ulaw"
    ast_frame_bytes: int = 160  # 160 bytes = 20ms at 8k µ-law
    
    # Processing state
    last_gain: float = 1.0
    binary_audio_count: int = 0
    
    # WebSocket state
    ws_connected: bool = False
    reconnect_attempts: int = 0
    last_ws_activity: float = field(default_factory=time.time)
    call_formally_ended: bool = False
    init_sent: bool = False
    current_ws_url: str = WS_URL


# =============================================================================
# BRIDGE CLASS
# =============================================================================

class TaxiBridgeV7:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running: bool = True
        
        self.state = CallState(call_id=f"ast-{int(time.time() * 1000)}")
        self.audio_queue: Deque[bytes] = deque()
        self.pending_audio_buffer: Deque[bytes] = deque(maxlen=100)

    # -------------------------------------------------------------------------
    # FORMAT DETECTION
    # -------------------------------------------------------------------------

    def _detect_format(self, frame_len: int) -> None:
        """Detect Asterisk audio format from frame size."""
        if frame_len == 160:
            self.state.ast_codec, self.state.ast_frame_bytes = "ulaw", 160
        elif frame_len == 320:
            self.state.ast_codec, self.state.ast_frame_bytes = "slin16", 320
        else:
            # Fallback: assume µ-law
            self.state.ast_codec, self.state.ast_frame_bytes = "ulaw", frame_len

        logger.info("[%s] 🔎 Detected: %s (%d bytes)", 
                   self.state.call_id, self.state.ast_codec, frame_len)

    # -------------------------------------------------------------------------
    # WEBSOCKET CONNECTION
    # -------------------------------------------------------------------------

    async def connect_websocket(self, url: Optional[str] = None, 
                                init_data: Optional[dict] = None) -> bool:
        """Connect to WebSocket with exponential backoff."""
        target_url = url or self.state.current_ws_url

        while self.state.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                # Exponential backoff delay
                if self.state.reconnect_attempts > 0:
                    delay = RECONNECT_BASE_DELAY_S * (2 ** (self.state.reconnect_attempts - 1))
                    logger.info("[%s] 🔄 Reconnecting in %.1fs", self.state.call_id, delay)
                    await asyncio.sleep(delay)

                self.ws = await asyncio.wait_for(
                    websockets.connect(target_url, ping_interval=5, ping_timeout=10),
                    timeout=10.0,
                )
                self.state.current_ws_url = target_url

                # Send init if redirected or reconnecting
                if init_data:
                    init_payload = {
                        "type": "init",
                        **init_data,
                        "call_id": self.state.call_id,
                        "phone": None if self.state.phone == "Unknown" else self.state.phone,
                        "reconnect": False,
                    }
                    await self.ws.send(json.dumps(init_payload))
                    logger.info("[%s] 🔀 Sent redirect init", self.state.call_id)
                    self.state.init_sent = True
                elif self.state.reconnect_attempts > 0 and self.state.init_sent:
                    reinit_payload = {
                        "type": "init",
                        "call_id": self.state.call_id,
                        "phone": None if self.state.phone == "Unknown" else self.state.phone,
                        "reconnect": True,
                    }
                    await self.ws.send(json.dumps(reinit_payload))
                    logger.info("[%s] 🔁 Sent reconnect init", self.state.call_id)

                self.state.ws_connected = True
                self.state.last_ws_activity = time.time()
                self.state.reconnect_attempts = 0

                logger.info("[%s] ✅ WebSocket connected", self.state.call_id)

                # Flush pending audio from disconnect
                while self.pending_audio_buffer:
                    chunk = self.pending_audio_buffer.popleft()
                    try:
                        await self.ws.send(chunk)
                        self.state.binary_audio_count += 1
                    except Exception as e:
                        logger.warning("[%s] ⚠️ Flush failed: %s", self.state.call_id, e)
                        self.pending_audio_buffer.clear()
                        break

                return True

            except asyncio.TimeoutError:
                logger.warning("[%s] ⏱️ WebSocket timeout", self.state.call_id)
                self.state.reconnect_attempts += 1
            except Exception as e:
                logger.error("[%s] ❌ WebSocket error: %s", self.state.call_id, e)
                self.state.reconnect_attempts += 1

        return False

    async def stop_call(self, reason: str) -> None:
        """Stop the bridge cleanly."""
        if not self.running:
            return
        logger.info("[%s] 🧹 Stopping: %s", self.state.call_id, reason)
        self.running = False
        self.state.ws_connected = False

        try:
            if self.ws:
                await self.ws.close(code=1000, reason=reason)
        except Exception:
            pass

        try:
            self.writer.close()
            await self.writer.wait_closed()
        except Exception:
            pass

    # -------------------------------------------------------------------------
    # MAIN RUN LOOP
    # -------------------------------------------------------------------------

    async def heartbeat_loop(self) -> None:
        """Periodic heartbeat logging."""
        while self.running:
            await asyncio.sleep(HEARTBEAT_INTERVAL_S)
            age = time.time() - self.state.last_ws_activity
            status = "🟢" if age < 5 else "🟡" if age < 15 else "🔴"
            logger.info("[%s] 💓 %s (%.1fs)", self.state.call_id, status, age)

    async def run(self) -> None:
        """Main bridge loop."""
        peer = self.writer.get_extra_info("peername")
        logger.info("[%s] 📞 Call from %s", self.state.call_id, peer)

        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())

        try:
            # Connect to WebSocket
            if not await self.connect_websocket():
                logger.error("[%s] ❌ Initial connection failed", self.state.call_id)
                return

            # EAGER INIT: start AI immediately
            eager_init = {
                "type": "init",
                "call_id": self.state.call_id,
                "phone": "unknown",
                "user_phone": "unknown",
                "addressTtsSplicing": True,
                "eager_init": True,
            }
            await self.ws.send(json.dumps(eager_init))
            self.state.init_sent = True
            logger.info("[%s] 🚀 Sent eager init", self.state.call_id)

            # Main processing loop
            while self.running:
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
            logger.info("[%s] 🔀 Redirect to %s", self.state.call_id, e.url)
            self.state.ws_connected = False
            try:
                if self.ws:
                    await self.ws.close(code=1000, reason="Redirecting")
            except Exception:
                pass

            self.state.reconnect_attempts = 0
            if await self.connect_websocket(url=e.url, init_data=e.init_data):
                logger.info("[%s] ✅ Redirect successful", self.state.call_id)
                await self.run()  # Recurse with new connection
            else:
                logger.error("[%s] ❌ Redirect failed", self.state.call_id)

        except CallEndedException:
            logger.info("[%s] 📴 Call formally ended", self.state.call_id)
        except (ConnectionClosed, WebSocketException) as e:
            logger.warning("[%s] 🔌 WebSocket closed: %s", self.state.call_id, e)
        except Exception as e:
            logger.error("[%s] ❌ Error: %s", self.state.call_id, e)
        finally:
            self.running = False
            playback_task.cancel()
            heartbeat_task.cancel()
            logger.info("[%s] 📊 Frames sent: %d", 
                       self.state.call_id, self.state.binary_audio_count)
            await self.cleanup()

    async def cleanup(self) -> None:
        """Clean up resources."""
        try:
            if self.ws:
                await self.ws.close()
        except Exception:
            pass
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except Exception:
            pass

    # -------------------------------------------------------------------------
    # ASTERISK → AI
    # -------------------------------------------------------------------------

    async def asterisk_to_ai(self) -> None:
        """Read AudioSocket frames from Asterisk and forward to AI."""
        while self.running and self.state.ws_connected:
            try:
                header = await asyncio.wait_for(self.reader.readexactly(3), timeout=30.0)
                m_type = header[0]
                m_len = struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    raw_hex = payload.hex()
                    if len(raw_hex) >= 12:
                        self.state.phone = raw_hex[-12:]
                    logger.info("[%s] 👤 Phone: %s", self.state.call_id, self.state.phone)

                    # Send phone update
                    if self.ws and self.state.ws_connected:
                        await self.ws.send(json.dumps({
                            "type": "update_phone",
                            "call_id": self.state.call_id,
                            "phone": self.state.phone,
                            "user_phone": self.state.phone,
                        }))
                        logger.info("[%s] 📱 Phone update sent", self.state.call_id)

                elif m_type == MSG_AUDIO:
                    if m_len != self.state.ast_frame_bytes:
                        self._detect_format(m_len)

                    # Decode and process audio
                    linear = ulaw2lin(payload) if self.state.ast_codec == "ulaw" else payload
                    cleaned, self.state.last_gain = apply_noise_reduction(
                        linear, self.state.last_gain
                    )

                    # Prepare for sending
                    if SEND_NATIVE_ULAW:
                        audio_to_send = lin2ulaw(cleaned)
                    else:
                        audio_to_send = resample_audio(cleaned, AST_RATE, AI_RATE)

                    # Send to AI
                    if self.state.ws_connected and self.ws:
                        try:
                            await self.ws.send(audio_to_send)
                            self.state.binary_audio_count += 1
                            self.state.last_ws_activity = time.time()
                        except Exception:
                            self.pending_audio_buffer.append(audio_to_send)
                            raise

                elif m_type == MSG_HANGUP:
                    logger.info("[%s] 📴 Hangup from Asterisk", self.state.call_id)
                    await self.stop_call("Asterisk hangup")
                    return

            except asyncio.TimeoutError:
                logger.warning("[%s] ⏱️ Asterisk timeout", self.state.call_id)
                await self.stop_call("Asterisk timeout")
                return
            except asyncio.IncompleteReadError:
                logger.info("[%s] 📴 Asterisk closed", self.state.call_id)
                await self.stop_call("Asterisk closed")
                return
            except (ConnectionClosed, WebSocketException):
                raise
            except Exception as e:
                logger.error("[%s] ❌ asterisk_to_ai error: %s", self.state.call_id, e)
                await self.stop_call("asterisk_to_ai error")
                return

    # -------------------------------------------------------------------------
    # AI → QUEUE
    # -------------------------------------------------------------------------

    async def ai_to_queue(self) -> None:
        """Receive audio + control messages from AI."""
        audio_count = 0
        try:
            async for message in self.ws:
                self.state.last_ws_activity = time.time()

                # Binary audio (TTS from AI - use quality-preserving downsample)
                if isinstance(message, bytes):
                    pcm_8k = resample_audio(message, AI_RATE, AST_RATE)
                    out = lin2ulaw(pcm_8k) if self.state.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    audio_count += 1
                    continue

                # JSON messages
                data = json.loads(message)
                msg_type = data.get("type")

                if msg_type in ("audio", "address_tts"):
                    # TTS audio - use quality-preserving downsample
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = resample_audio(raw_24k, AI_RATE, AST_RATE)
                    out = lin2ulaw(pcm_8k) if self.state.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                    audio_count += 1

                elif msg_type == "transcript":
                    role = data.get("role", "?").upper()
                    text = data.get("text", "")
                    logger.info("[%s] 💬 %s: %s", self.state.call_id, role, text)

                elif msg_type == "ai_interrupted":
                    flushed = len(self.audio_queue)
                    self.audio_queue.clear()
                    logger.info("[%s] 🛑 Flushed %d chunks (barge-in)", 
                               self.state.call_id, flushed)

                elif msg_type == "redirect":
                    raise RedirectException(data.get("url", WS_URL), data.get("init_data", {}))

                elif msg_type == "call_ended":
                    logger.info("[%s] 📴 AI ended: %s", 
                               self.state.call_id, data.get("reason"))
                    self.state.call_formally_ended = True
                    self.running = False
                    raise CallEndedException()

                elif msg_type == "error":
                    logger.error("[%s] 🧨 AI error: %s", 
                                self.state.call_id, data.get("error"))

        except (RedirectException, CallEndedException):
            raise
        except (ConnectionClosed, WebSocketException):
            raise
        except Exception as e:
            logger.error("[%s] ❌ ai_to_queue error: %s", self.state.call_id, e)
        finally:
            logger.info("[%s] 📊 AI audio received: %d", self.state.call_id, audio_count)

    # -------------------------------------------------------------------------
    # QUEUE → ASTERISK
    # -------------------------------------------------------------------------

    async def queue_to_asterisk(self) -> None:
        """Stream audio from queue to Asterisk at correct pacing."""
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()

        bytes_per_sec = AST_RATE * (1 if self.state.ast_codec == "ulaw" else 2)

        while self.running:
            # Drain queue to buffer
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

            # Pace output to real-time
            expected_time = start_time + (bytes_played / max(bytes_per_sec, 1))
            delay = expected_time - time.time()
            if delay > 0:
                await asyncio.sleep(delay)

            # Get next frame
            if len(buffer) >= self.state.ast_frame_bytes:
                chunk = bytes(buffer[:self.state.ast_frame_bytes])
                del buffer[:self.state.ast_frame_bytes]
            else:
                chunk = self._silence()

            # Send to Asterisk
            try:
                self.writer.write(struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk)
                await self.writer.drain()
                bytes_played += len(chunk)
            except (BrokenPipeError, ConnectionResetError, OSError) as e:
                logger.warning("[%s] 🔌 Pipe closed: %s", self.state.call_id, e)
                await self.stop_call("Asterisk disconnected")
                return
            except Exception as e:
                logger.error("[%s] ❌ Write error: %s", self.state.call_id, e)
                await self.stop_call("Write failed")
                return

    def _silence(self) -> bytes:
        """Generate one frame of silence."""
        b = 0xFF if self.state.ast_codec == "ulaw" else 0x00
        return bytes([b]) * self.state.ast_frame_bytes


# =============================================================================
# MAIN
# =============================================================================

async def main() -> None:
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV7(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    logger.info("🚀 Taxi Bridge v7.0 - instant greeting ready")
    logger.info("   Listening on %s:%d", AUDIOSOCKET_HOST, AUDIOSOCKET_PORT)
    logger.info("   Connecting to: %s", WS_URL)

    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("👋 Exit requested")
