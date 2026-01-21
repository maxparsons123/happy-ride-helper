#!/usr/bin/env python3
# Taxi AI Asterisk Bridge v7.5 - AGGRESSIVE AUDIO BOOST
#
# Key features:
# 1) Direct connection to taxi-realtime-paired (context-pairing architecture)
# 2) Sends init immediately on connect (before phone number arrives)
# 3) Phone number sent later via update_phone once UUID arrives
# 4) Native 8kHz µ-law send-path for faster, smaller audio frames
# 5) Pre-emphasis filter for consonant clarity (prevents 'Russell' → 'Ruffles')
# 6) Improved error handling and connection resilience
# 7) resample_poly for ALL audio paths (no more linear interpolation)
#
# v7.4 Audio Quality Fixes (CRITICAL - voice wasn't reaching Whisper):
# - Added 3x VOLUME BOOST before AGC to amplify quiet telephony lines
# - Added AGC with 15x max gain to normalize to TARGET_RMS=300
# - Pipeline order: Decode → Volume Boost (3x) → AGC → Pre-emphasis → Send
# - This matches the backend DSP pipeline for consistent audio levels
#
# v7.3 Audio Quality Fixes:
# - Replaced linear interpolation with resample_poly for upsampling
# - Added pre-emphasis filter (coeff=0.95) to boost high-frequency consonants
# - Preserves transients for better STT accuracy on 'S', 'T', 'F' sounds
#
# Memory leak fixes (from v7.1):
# - Bounded audio_queue (maxlen=200) to prevent unbounded growth
# - Early exit checks in all async loops
# - Clear ws reference after close to break circular references
# - Proper CancelledError handling in async tasks
# - Await task cancellation to prevent dangling tasks

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
# Load from bridge-config.json if available, otherwise use defaults
import os

# Prefer explicit WS_URL env var, then bridge-config.json, with paired as default.
WS_URL = os.environ.get("WS_URL")
if not WS_URL:
    try:
        with open(os.path.join(os.path.dirname(__file__), "..", "bridge-config.json")) as f:
            _config = json.load(f)
            edge = _config.get("edge_functions", {})
            WS_URL = (
                edge.get("taxi_realtime_paired_ws")
                or edge.get("taxi_realtime_ws")
                or edge.get("taxi_realtime_simple_ws")
                # IMPORTANT: WebSocket routing is more reliable via the .functions subdomain.
                or "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired"
            )
    except (FileNotFoundError, json.JSONDecodeError):
        WS_URL = "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired"

# Normalize WS routing: if an env var still uses the less reliable ".supabase.co/functions/v1" domain,
# rewrite it to the ".functions.supabase.co/functions/v1" subdomain.
if WS_URL and (".supabase.co/functions/v1/" in WS_URL) and (".functions.supabase.co" not in WS_URL):
    WS_URL = WS_URL.replace(
        ".supabase.co/functions/v1/",
        ".functions.supabase.co/functions/v1/",
    )

AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = int(os.environ.get("AUDIOSOCKET_PORT", 9092))

# Audio rates
ULAW_RATE = 8000    # µ-law telephony (8kHz)
SLIN16_RATE = 16000 # slin16 = signed linear 16kHz
AI_RATE = 24000     # OpenAI TTS

# LOCK TO ULAW: Disable slin16 auto-detection - it was corrupting audio
# The format detection was switching mid-call causing garbled audio
PREFER_SLIN16 = False
LOCK_FORMAT_ULAW = True  # NEW: Prevent any format switching

# Pre-emphasis coefficient for boosting high frequencies (consonants)
# Higher values (0.95-0.97) boost more, helping distinguish 'S' vs 'F' sounds
PRE_EMPHASIS_COEFF = float(os.environ.get("PRE_EMPHASIS_COEFF", "0.95"))

# CRITICAL: Send PCM16 not µ-law - re-encoding to µ-law loses the AGC boost!
# The edge function handles resampling to 24kHz anyway
SEND_NATIVE_FORMAT = False  # Changed from True - send PCM16 after processing

# Reconnection settings
MAX_RECONNECT_ATTEMPTS = 5  # Increased for mobile network resilience
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15.0

# Audio processing - ENABLED for quiet telephony lines
# v7.5: AGGRESSIVE boost - avgRMS was still 8-89, needs to be 300+
ENABLE_NOISE_REDUCTION = False  # Keep disabled - causes muting issues
ENABLE_VOLUME_BOOST = True      # Apply fixed boost to quiet lines
ENABLE_AGC = True               # Automatic gain control

VOLUME_BOOST_FACTOR = 6.0       # INCREASED from 3.0 - audio still too quiet
NOISE_GATE_THRESHOLD = 15       # Lower threshold if enabled
NOISE_GATE_SOFT_KNEE = True
HIGH_PASS_CUTOFF = 60
TARGET_RMS = 500                # INCREASED from 300 - target louder output
MAX_GAIN = 20.0                 # INCREASED from 15.0 for very quiet lines
MIN_GAIN = 1.0                  # Never reduce volume
GAIN_SMOOTHING_FACTOR = 0.2     # Faster adaptation

# Debug logging for audio levels
LOG_AUDIO_LEVELS = True         # Log RMS before/after processing

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
    force=True,
)
logger = logging.getLogger("TaxiBridgeV7")


# =============================================================================
# AUDIO HELPERS
# =============================================================================

# Pre-compute high-pass filter coefficients (use 8kHz as base, works for all rates)
_highpass_sos = butter(2, HIGH_PASS_CUTOFF, btype="high", fs=ULAW_RATE, output="sos")


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
    """Noise reduction - can be disabled for cleaner STT."""
    if not ENABLE_NOISE_REDUCTION:
        # Pass through raw audio - Whisper handles noise well
        return audio_bytes, last_gain
    
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
        gain_curve = 0.3 + 0.7 * gain_curve  # Less aggressive floor (was 0.15)
        audio_np *= gain_curve
    else:
        mask = np.abs(audio_np) < NOISE_GATE_THRESHOLD
        audio_np[mask] *= 0.3  # Less aggressive (was 0.1)

    # RMS-based auto-gain with smoothing
    rms = float(np.sqrt(np.mean(audio_np ** 2))) if audio_np.size else 0.0
    current_gain = last_gain
    if rms > 30:
        target_gain = float(np.clip(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN))
        current_gain = last_gain + GAIN_SMOOTHING_FACTOR * (target_gain - last_gain)
        audio_np *= current_gain

    audio_np = np.clip(audio_np, -32768, 32767).astype(np.int16)
    return audio_np.tobytes(), current_gain


def apply_pre_emphasis(audio_np: np.ndarray, coeff: float = 0.95) -> np.ndarray:
    """Pre-emphasis filter to boost high frequencies (consonants like 'S', 'T', 'F').
    
    This is a standard telephony technique to help STT distinguish consonants
    that get attenuated in low-bandwidth audio (e.g., 'Russell' vs 'Ruffles').
    """
    if audio_np.size == 0:
        return audio_np
    return np.append(audio_np[0], audio_np[1:] - coeff * audio_np[:-1])


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int, 
                   apply_pre_emph: bool = False) -> bytes:
    """Resample PCM16 audio using high-quality polyphase filtering.

    v7.3: Now uses resample_poly for ALL directions to preserve high-frequency
    transients (consonants). Linear interpolation was causing 'S' → 'F' errors.
    
    Optional pre-emphasis can boost consonants before STT processing.
    """
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes

    x = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if x.size == 0:
        return b""

    # Apply pre-emphasis if requested (boosts high frequencies for STT)
    if apply_pre_emph:
        x = apply_pre_emphasis(x)

    # Use resample_poly for ALL rate conversions - preserves high-frequency transients
    from math import gcd
    g = gcd(from_rate, to_rate)
    up = to_rate // g
    down = from_rate // g
    
    # resample_poly applies a high-quality FIR anti-aliasing filter
    out = resample_poly(x, up=up, down=down)
    return np.clip(out, -32768, 32767).astype(np.int16).tobytes()


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
    # Default to ulaw (8kHz) for stability - auto-detects actual format from frame size
    # slin16 gives better STT but causes more disconnects under load
    ast_codec: str = "ulaw"
    ast_rate: int = ULAW_RATE
    ast_frame_bytes: int = 160  # 160 bytes = 20ms at 8kHz ulaw
    
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
    
    # Format detection debounce - prevents flip-flopping between formats
    format_locked: bool = False
    format_lock_time: float = 0.0


# =============================================================================
# BRIDGE CLASS
# =============================================================================

class TaxiBridgeV7:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running: bool = True
        
        self.state = CallState(call_id=f"paired-{int(time.time() * 1000)}")
        # 🔥 FIXED: Bounded queue to prevent memory leaks during disconnections
        self.audio_queue: Deque[bytes] = deque(maxlen=200)
        self.pending_audio_buffer: Deque[bytes] = deque(maxlen=100)

    async def _detect_format(self, frame_len: int) -> None:
        # v7.4: LOCK TO ULAW - ignore format changes entirely
        if LOCK_FORMAT_ULAW:
            if frame_len != self.state.ast_frame_bytes:
                logger.warning("[%s] ⚠️ Ignoring frame size change %d→%d (locked to ulaw)",
                              self.state.call_id, self.state.ast_frame_bytes, frame_len)
            # Always treat as ulaw regardless of frame size
            self.state.ast_codec = "ulaw"
            self.state.ast_rate = ULAW_RATE
            self.state.ast_frame_bytes = frame_len
            return

    async def connect_websocket(self, url: Optional[str] = None, init_data: Optional[dict] = None) -> bool:
        target_url = url or self.state.current_ws_url
        while self.state.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                if self.state.reconnect_attempts > 0:
                    delay = RECONNECT_BASE_DELAY_S * (2 ** (self.state.reconnect_attempts - 1))
                    logger.info("[%s] 🔄 Reconnecting in %.1fs", self.state.call_id, delay)
                    await asyncio.sleep(delay)
                self.ws = await asyncio.wait_for(
                    websockets.connect(
                        target_url,
                        ping_interval=20,
                        ping_timeout=20,
                        close_timeout=5,
                        max_queue=32,
                    ),
                    timeout=10.0,
                )
                self.state.current_ws_url = target_url
                if init_data:
                    init_payload = {
                        "type": "init",
                        **init_data,
                        "call_id": self.state.call_id,
                        "phone": self.state.phone,
                        "user_phone": self.state.phone,
                        "reconnect": True,
                    }
                    await self.ws.send(json.dumps(init_payload))
                    logger.info("[%s] 📡 Sent reconnect init", self.state.call_id)
                while self.pending_audio_buffer:
                    try:
                        await self.ws.send(self.pending_audio_buffer.popleft())
                    except Exception:
                        break
                self.state.ws_connected = True
                self.state.reconnect_attempts = 0
                logger.info("[%s] ✅ Connected to %s", self.state.call_id, target_url)
                return True
            except asyncio.TimeoutError:
                logger.warning("[%s] ⏱️ WS connect timeout", self.state.call_id)
            except Exception as e:
                logger.warning("[%s] 🔌 WS connect failed: %s", self.state.call_id, e)
            self.state.reconnect_attempts += 1
        logger.error("[%s] ❌ Max reconnect attempts reached", self.state.call_id)
        return False

    async def stop_call(self, reason: str) -> None:
        if not self.running:
            return
        self.running = False
        self.state.ws_connected = False
        logger.info("[%s] 🛑 Stopping: %s", self.state.call_id, reason)
        if self.ws:
            try:
                await self.ws.send(json.dumps({
                    "type": "hangup",
                    "call_id": self.state.call_id,
                    "reason": reason,
                }))
            except Exception:
                pass
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

    async def heartbeat_loop(self) -> None:
        try:
            while self.running:
                await asyncio.sleep(HEARTBEAT_INTERVAL_S)
                if not self.running:
                    break
                age = time.time() - self.state.last_ws_activity
                status = "🟢" if age < 5 else "🟡" if age < 15 else "🔴"
                logger.info("[%s] 💓 %s (%.1fs)", self.state.call_id, status, age)
        except asyncio.CancelledError:
            logger.debug("[%s] Heartbeat task cancelled", self.state.call_id)

    async def run(self) -> None:
        peer = self.writer.get_extra_info("peername")
        logger.info("[%s] 📞 Call from %s", self.state.call_id, peer)
        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())
        try:
            while self.running:
                if not self.state.ws_connected:
                    if not await self.connect_websocket():
                        logger.error("[%s] ❌ WebSocket (re)connect failed", self.state.call_id)
                        await self.stop_call("WebSocket reconnect failed")
                        break
                    if not self.state.init_sent and self.ws:
                        send_format = "slin" if not SEND_NATIVE_FORMAT else self.state.ast_codec
                        eager_init = {
                            "type": "init",
                            "call_id": self.state.call_id,
                            "phone": "unknown",
                            "user_phone": "unknown",
                            "addressTtsSplicing": True,
                            "eager_init": True,
                            "inbound_format": send_format,
                            "inbound_sample_rate": self.state.ast_rate,
                        }
                        await self.ws.send(json.dumps(eager_init))
                        self.state.init_sent = True
                        logger.info("[%s] 🚀 Sent eager init (format=%s @ %dHz)",
                                   self.state.call_id, send_format, self.state.ast_rate)
                ast_task = asyncio.create_task(self.asterisk_to_ai())
                ai_task = asyncio.create_task(self.ai_to_queue())
                done, pending = await asyncio.wait({ast_task, ai_task}, return_when=asyncio.FIRST_COMPLETED)
                for t in pending:
                    t.cancel()
                await asyncio.gather(*pending, return_exceptions=True)
                exc = next((t.exception() for t in done if t.exception()), None)
                if exc is None:
                    break
                if isinstance(exc, RedirectException):
                    self.state.ws_connected = False
                    try:
                        if self.ws:
                            await self.ws.close(code=1000, reason="Redirecting")
                    except Exception:
                        pass
                    self.state.reconnect_attempts = 0
                    if not await self.connect_websocket(url=exc.url, init_data=exc.init_data):
                        await self.stop_call("Redirect failed")
                        break
                    continue
                if isinstance(exc, CallEndedException):
                    break
                if isinstance(exc, (ConnectionClosed, WebSocketException)):
                    self.state.ws_connected = False
                    self.state.reconnect_attempts = max(self.state.reconnect_attempts, 1)
                    try:
                        if self.ws:
                            await self.ws.close()
                    except Exception:
                        pass
                    self.ws = None
                    continue
                await self.stop_call("Unhandled error")
                break
        finally:
            self.running = False
            tasks_to_cancel = [playback_task, heartbeat_task]
            for task in tasks_to_cancel:
                if not task.done():
                    task.cancel()
            await asyncio.gather(*tasks_to_cancel, return_exceptions=True)
            await self.cleanup()

    async def cleanup(self) -> None:
        self.audio_queue.clear()
        self.pending_audio_buffer.clear()
        try:
            if self.ws:
                await self.ws.close()
        except Exception:
            pass
        self.ws = None
        try:
            if not self.writer.is_closing():
                self.writer.close()
                await self.writer.wait_closed()
        except Exception:
            pass

    async def asterisk_to_ai(self) -> None:
        try:
            while self.running and self.state.ws_connected:
                if not self.running:
                    break
                header = await asyncio.wait_for(self.reader.readexactly(3), timeout=30.0)
                m_type = header[0]
                m_len = struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)
                if m_type == MSG_UUID:
                    raw_hex = payload.hex()
                    if len(raw_hex) >= 12:
                        self.state.phone = raw_hex[-12:]
                    logger.info("[%s] 👤 Phone: %s", self.state.call_id, self.state.phone)
                    if self.ws and self.state.ws_connected:
                        await self.ws.send(json.dumps({
                            "type": "update_phone",
                            "call_id": self.state.call_id,
                            "phone": self.state.phone,
                            "user_phone": self.state.phone,
                        }))
                elif m_type == MSG_AUDIO:
                    if m_len != self.state.ast_frame_bytes:
                        await self._detect_format(m_len)
                    linear = ulaw2lin(payload) if self.state.ast_codec == "ulaw" else payload
                    pcm_array = np.frombuffer(linear, dtype=np.int16).astype(np.float32)
                    input_rms = float(np.sqrt(np.mean(pcm_array ** 2))) if pcm_array.size > 0 else 0
                    if ENABLE_VOLUME_BOOST and pcm_array.size > 0:
                        pcm_array *= VOLUME_BOOST_FACTOR
                    if ENABLE_AGC and pcm_array.size > 0:
                        rms = float(np.sqrt(np.mean(pcm_array ** 2)))
                        if rms > 10:
                            target_gain = float(np.clip(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN))
                            self.state.last_gain = self.state.last_gain + GAIN_SMOOTHING_FACTOR * (target_gain - self.state.last_gain)
                            pcm_array *= self.state.last_gain
                    output_rms = float(np.sqrt(np.mean(pcm_array ** 2))) if pcm_array.size > 0 else 0
                    if LOG_AUDIO_LEVELS and self.state.binary_audio_count % 50 == 0:
                        logger.info("[%s] 🔊 Audio: inRMS=%.0f → outRMS=%.0f (gain=%.1fx, boost=%.0fx)",
                                   self.state.call_id, input_rms, output_rms,
                                   self.state.last_gain, VOLUME_BOOST_FACTOR)
                    emphasized = apply_pre_emphasis(pcm_array, coeff=PRE_EMPHASIS_COEFF)
                    cleaned = np.clip(emphasized, -32768, 32767).astype(np.int16).tobytes()
                    audio_to_send = cleaned
                    if self.state.ws_connected and self.ws:
                        try:
                            await self.ws.send(audio_to_send)
                            self.state.binary_audio_count += 1
                            self.state.last_ws_activity = time.time()
                        except Exception:
                            self.pending_audio_buffer.append(audio_to_send)
                            raise
                elif m_type == MSG_HANGUP:
                    await self.stop_call("Asterisk hangup")
                    return
        except Exception:
            await self.stop_call("asterisk_to_ai error")

    async def ai_to_queue(self) -> None:
        audio_count = 0
        try:
            async for message in self.ws:
                if not self.running:
                    break
                self.state.last_ws_activity = time.time()
                if isinstance(message, bytes):
                    pcm_ast = resample_audio(message, AI_RATE, self.state.ast_rate)
                    out = lin2ulaw(pcm_ast) if self.state.ast_codec == "ulaw" else pcm_ast
                    self.audio_queue.append(out)
                    audio_count += 1
                    continue
                data = json.loads(message)
                msg_type = data.get("type")
                if msg_type in ("audio", "address_tts"):
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_ast = resample_audio(raw_24k, AI_RATE, self.state.ast_rate)
                    out = lin2ulaw(pcm_ast) if self.state.ast_codec == "ulaw" else pcm_ast
                    self.audio_queue.append(out)
                    audio_count += 1
                elif msg_type == "redirect":
                    raise RedirectException(data.get("url", WS_URL), data.get("init_data", {}))
                elif msg_type == "call_ended":
                    raise CallEndedException()
        finally:
            logger.info("[%s] 📊 AI audio received: %d", self.state.call_id, audio_count)

    async def queue_to_asterisk(self) -> None:
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()
        try:
            while self.running:
                bytes_per_sec = self.state.ast_rate * (1 if self.state.ast_codec == "ulaw" else 2)
                while self.audio_queue:
                    buffer.extend(self.audio_queue.popleft())
                elapsed = time.time() - start_time
                expected = int(elapsed * bytes_per_sec)
                if bytes_played < expected and len(buffer) >= self.state.ast_frame_bytes:
                    frames_needed = (expected - bytes_played) // self.state.ast_frame_bytes
                    frames_available = len(buffer) // self.state.ast_frame_bytes
                    frames_to_send = min(frames_needed, frames_available, 10)
                    for _ in range(frames_to_send):
                        if len(buffer) < self.state.ast_frame_bytes:
                            break
                        chunk = bytes(buffer[:self.state.ast_frame_bytes])
                        del buffer[:self.state.ast_frame_bytes]
                        self.writer.write(struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk)
                        await self.writer.drain()
                        bytes_played += len(chunk)
                await asyncio.sleep(0.02)
                if not buffer and bytes_played < expected:
                    chunk = self._silence()
                    self.writer.write(struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk)
                    await self.writer.drain()
                    bytes_played += len(chunk)
        except asyncio.CancelledError:
            pass

    def _silence(self) -> bytes:
        b = 0xFF if self.state.ast_codec == "ulaw" else 0x00
        return bytes([b]) * self.state.ast_frame_bytes


async def main() -> None:
    server = await asyncio.start_server(lambda r, w: TaxiBridgeV7(r, w).run(), AUDIOSOCKET_HOST, AUDIOSOCKET_PORT)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("👋 Exit requested")
