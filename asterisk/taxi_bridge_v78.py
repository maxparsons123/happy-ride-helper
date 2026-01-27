#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge v7.8 - AUDIO QUALITY UPGRADE
Architecture:
Asterisk AudioSocket ←→ Bridge ←→ Edge Function (taxi-realtime-paired)
Key Features:
• Opus @ 64kbps for WhatsApp native 48kHz audio (higher fidelity)
• Noise gate + adaptive pre-emphasis + soft clipping
• Dynamic DSP: skip processing for clean slin16/opus inputs
• High-quality resample_poly for all paths
• Session handoff, bounded queues, diagnostics
"""
import asyncio
import base64
import json
import logging
import os
import struct
import time
from collections import deque
from dataclasses import dataclass, field
from math import gcd
from typing import Deque, Optional, Tuple
import numpy as np
from scipy.signal import resample_poly
import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException

# Opus codec support
try:
    import opuslib
    from opuslib import Encoder as OpusEncoder, Decoder as OpusDecoder
    OPUS_APPLICATION_VOIP = 2048
    OPUS_AVAILABLE = True
except ImportError:
    OPUS_AVAILABLE = False
    OpusEncoder = None
    OpusDecoder = None
    OPUS_APPLICATION_VOIP = 2048

# =============================================================================
# CONFIGURATION
# =============================================================================
def _load_ws_url() -> str:
    if url := os.environ.get("WS_URL"):
        return url
    try:
        config_path = os.path.join(os.path.dirname(__file__), "..", "bridge-config.json")
        with open(config_path) as f:
            cfg = json.load(f).get("edge_functions", {})
            return (
                cfg.get("taxi_realtime_paired_ws")
                or cfg.get("taxi_realtime_simple_ws")
                or "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired"
            )
    except (FileNotFoundError, json.JSONDecodeError, KeyError):
        return "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired"

def _env_bool(name: str, default: bool = False) -> bool:
    val = os.environ.get(name, "").strip().lower()
    return val in ("1", "true", "yes", "on") if val else default

def _env_float(name: str, default: float) -> float:
    try:
        return float(os.environ.get(name, default))
    except (ValueError, TypeError):
        return default

# Network
WS_URL = _load_ws_url()
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = int(os.environ.get("AUDIOSOCKET_PORT", 9092))

# Audio Rates (Hz)
RATE_ULAW = 8000
RATE_SLIN = 8000
RATE_SLIN16 = 16000
RATE_AI = 24000
RATE_OPUS = 48000

# Opus Configuration — **UPGRADED TO 64KBPS**
OPUS_FRAME_MS = 20
OPUS_BITRATE = 64000          # ← WAS 32000
OPUS_COMPLEXITY = 6           # Slightly higher for clarity
OPUS_APPLICATION = "voip"

# Format Detection
LOCK_FORMAT_ULAW = _env_bool("LOCK_FORMAT_ULAW", False)
PREFER_OPUS = _env_bool("PREFER_OPUS", True)  # Default ON for WhatsApp 48kHz
PREFER_SLIN16 = True
# FORCE_SLIN16: When true, treat ALL 320-byte frames as 16kHz (not 8kHz)
# Use this when Asterisk dialplan sets CHANNEL(audioformat)=slin16 but AudioSocket
# still sends 320-byte frames (which normally indicate 8kHz).
# IMPORTANT: Set to True for better STT accuracy on house numbers like "52A"
FORCE_SLIN16 = _env_bool("FORCE_SLIN16", True)  # ON by default for 16kHz quality
# FORCE_OPUS: When true, assume all variable-size frames are Opus (for WhatsApp)
# This is checked BEFORE slin16 detection, so non-standard frame sizes use Opus
FORCE_OPUS = _env_bool("FORCE_OPUS", True)  # ON for WhatsApp compatibility
FORMAT_LOCK_DURATION_S = 60.0  # Lock format for entire call duration
FORMAT_LOCK_FRAME_COUNT = 5    # Fewer frames needed when Opus is clear

# DSP Pipeline — **ENHANCED**
ENABLE_VOLUME_BOOST = True
ENABLE_AGC = True
VOLUME_BOOST_FACTOR = 3.0
TARGET_RMS = 300
AGC_MAX_GAIN = 15.0
AGC_MIN_GAIN = 1.0
AGC_SMOOTHING = 0.15
AGC_FLOOR_RMS = 10
PRE_EMPHASIS_COEFF_DEFAULT = 0.95
ENABLE_NOISE_GATE = True
NOISE_GATE_THRESHOLD = 80      # RMS below this = noise
SOFT_CLIP_THRESHOLD = 32000.0  # Prevent hard clipping

# Connection
MAX_RECONNECT_ATTEMPTS = 5
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15.0
WS_PING_INTERVAL = 20
WS_PING_TIMEOUT = 20
ASTERISK_READ_TIMEOUT_S = 30.0

# Queue Bounds
AUDIO_QUEUE_MAXLEN = 200
PENDING_BUFFER_MAXLEN = 100

# AudioSocket Message Types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# µ-law Constants
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
logger = logging.getLogger("TaxiBridge")

# =============================================================================
# OPUS CODEC WRAPPER
# =============================================================================
class OpusCodec:
    def __init__(self, sample_rate: int = RATE_OPUS, channels: int = 1):
        if not OPUS_AVAILABLE:
            raise RuntimeError("opuslib not installed. Run: pip install opuslib")
        self.sample_rate = sample_rate
        self.channels = channels
        self.frame_size = int(sample_rate * OPUS_FRAME_MS / 1000)
        application = OPUS_APPLICATION_VOIP
        self.encoder = OpusEncoder(sample_rate, channels, application)
        self.encoder.bitrate = OPUS_BITRATE
        self.encoder.complexity = OPUS_COMPLEXITY
        self.encoder.vbr = True
        self.encoder.dtx = False  # ← Disable DTX for consistent quality
        self.decoder = OpusDecoder(sample_rate, channels)
        logger.info("🎵 Opus codec initialized: %dHz, %d ch, %d samples/frame, %dkbps",
                    sample_rate, channels, self.frame_size, OPUS_BITRATE // 1000)

    def encode(self, pcm_bytes: bytes) -> bytes:
        if not pcm_bytes:
            return b""
        samples = np.frombuffer(pcm_bytes, dtype=np.int16)
        if len(samples) < self.frame_size:
            samples = np.pad(samples, (0, self.frame_size - len(samples)))
        elif len(samples) > self.frame_size:
            samples = samples[:self.frame_size]
        return self.encoder.encode(samples.tobytes(), self.frame_size)

    def decode(self, opus_bytes: bytes) -> bytes:
        if not opus_bytes:
            return b""
        try:
            return self.decoder.decode(opus_bytes, self.frame_size)
        except Exception as e:
            logger.warning("Opus decode error: %s", e)
            return bytes(self.frame_size * 2)

    def encode_with_resample(self, pcm_bytes: bytes, from_rate: int) -> bytes:
        if from_rate == self.sample_rate:
            return self.encode(pcm_bytes)
        samples = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return b""
        g = gcd(from_rate, self.sample_rate)
        resampled = resample_poly(samples, up=self.sample_rate // g, down=from_rate // g)
        pcm_resampled = np.clip(resampled, -32768, 32767).astype(np.int16).tobytes()
        return self.encode(pcm_resampled)

    def decode_with_resample(self, opus_bytes: bytes, to_rate: int) -> bytes:
        pcm = self.decode(opus_bytes)
        if to_rate == self.sample_rate:
            return pcm
        samples = np.frombuffer(pcm, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return b""
        g = gcd(self.sample_rate, to_rate)
        resampled = resample_poly(samples, up=to_rate // g, down=self.sample_rate // g)
        return np.clip(resampled, -32768, 32767).astype(np.int16).tobytes()

# =============================================================================
# AUDIO PROCESSING — **FULLY UPGRADED**
# =============================================================================
class AudioProcessor:
    def __init__(self):
        self.last_gain: float = 1.0
        self.opus_codec: Optional[OpusCodec] = None
        if OPUS_AVAILABLE:
            try:
                self.opus_codec = OpusCodec(RATE_OPUS, channels=1)
            except Exception as e:
                logger.warning("⚠️ Opus init failed: %s", e)
                self.opus_codec = None

    @staticmethod
    def ulaw_to_linear(ulaw_bytes: bytes) -> bytes:
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

    @staticmethod
    def linear_to_ulaw(pcm_bytes: bytes) -> bytes:
        if not pcm_bytes:
            return b""
        pcm = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.int32)
        sign = np.where(pcm < 0, 0x80, 0)
        pcm = np.clip(np.abs(pcm), 0, ULAW_CLIP) + ULAW_BIAS
        exponent = np.clip(np.floor(np.log2(np.maximum(pcm, 1))).astype(np.int32) - 7, 0, 7)
        mantissa = (pcm >> (exponent + 3)) & 0x0F
        ulaw = (~(sign | (exponent << 4) | mantissa)) & 0xFF
        return ulaw.astype(np.uint8).tobytes()

    @staticmethod
    def pre_emphasis(samples: np.ndarray, coeff: float = 0.95) -> np.ndarray:
        if samples.size == 0:
            return samples
        return np.append(samples[0], samples[1:] - coeff * samples[:-1])

    @staticmethod
    def resample(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
        if from_rate == to_rate or not audio_bytes:
            return audio_bytes
        samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return b""
        g = gcd(from_rate, to_rate)
        resampled = resample_poly(samples, up=to_rate // g, down=from_rate // g)
        return np.clip(resampled, -32768, 32767).astype(np.int16).tobytes()

    @staticmethod
    def soft_clip(samples: np.ndarray, threshold: float = 32000.0) -> np.ndarray:
        """Gentle saturation instead of hard clipping."""
        return np.tanh(samples / threshold) * threshold

    def process_inbound(self, pcm_bytes: bytes, is_high_quality: bool = False) -> bytes:
        """
        Enhanced DSP pipeline:
        - Noise gate → Volume boost (if needed) → AGC → Adaptive pre-emphasis → Soft clip
        """
        if not pcm_bytes or len(pcm_bytes) < 4:
            return pcm_bytes
        samples = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return pcm_bytes

        rms = float(np.sqrt(np.mean(samples ** 2)))

        # Skip aggressive DSP for already-clean signals
        if is_high_quality and rms > 500:
            return pcm_bytes

        # Step 1: Noise gate (with soft transition to prevent clicks)
        if ENABLE_NOISE_GATE:
            if rms < NOISE_GATE_THRESHOLD:
                gate_gain = rms / NOISE_GATE_THRESHOLD  # Soft fade instead of hard cut
                samples *= gate_gain

        # Step 2: Volume boost (only if still quiet)
        if ENABLE_VOLUME_BOOST and rms < 200:
            samples *= VOLUME_BOOST_FACTOR

        # Recalculate RMS after boost
        rms = float(np.sqrt(np.mean(samples ** 2)))

        # Step 3: AGC
        if ENABLE_AGC and rms > AGC_FLOOR_RMS:
            target_gain = float(np.clip(TARGET_RMS / rms, AGC_MIN_GAIN, AGC_MAX_GAIN))
            self.last_gain += AGC_SMOOTHING * (target_gain - self.last_gain)
            samples *= self.last_gain

        # Step 4: Adaptive pre-emphasis
        spectral_tilt = np.mean(np.diff(samples[:1000])) if len(samples) > 1000 else 0
        pre_emph_coeff = 0.97 if spectral_tilt < 0 else 0.92
        samples = self.pre_emphasis(samples, pre_emph_coeff)

        # Step 5: Soft clipping
        samples = self.soft_clip(samples, SOFT_CLIP_THRESHOLD)

        return np.clip(samples, -32768, 32767).astype(np.int16).tobytes()

    def process_outbound(self, ai_audio: bytes, from_rate: int, to_rate: int, to_codec: str) -> bytes:
        if to_codec == "opus" and self.opus_codec:
            return self.opus_codec.encode_with_resample(ai_audio, from_rate)
        resampled = self.resample(ai_audio, from_rate, to_rate)
        if to_codec == "ulaw":
            return self.linear_to_ulaw(resampled)
        return resampled

# =============================================================================
# EXCEPTIONS
# =============================================================================
class RedirectException(Exception):
    def __init__(self, url: str, init_data: dict):
        self.url = url
        self.init_data = init_data
        super().__init__(f"Redirect to {url}")

class CallEndedException(Exception):
    pass

# =============================================================================
# CALL STATE
# =============================================================================
@dataclass
class CallState:
    call_id: str
    phone: str = "Unknown"
    ast_codec: str = "ulaw"
    ast_rate: int = RATE_ULAW
    ast_frame_bytes: int = 160
    format_locked: bool = False
    format_lock_time: float = 0.0
    first_frame_at: float = 0.0
    frames_observed: int = 0
    seen_160: bool = False
    seen_320: bool = False
    seen_640: bool = False
    ws_connected: bool = False
    reconnect_attempts: int = 0
    last_ws_activity: float = field(default_factory=time.time)
    current_ws_url: str = field(default_factory=lambda: WS_URL)
    init_sent: bool = False
    call_formally_ended: bool = False
    last_asterisk_send: float = field(default_factory=time.time)
    last_asterisk_recv: float = field(default_factory=time.time)
    opus_buffer: bytes = b""
    frames_sent: int = 0
    frames_received: int = 0
    keepalive_count: int = 0
    handoff_count: int = 0

# =============================================================================
# BRIDGE CLASS
# =============================================================================
class TaxiBridge:
    VERSION = "7.8"

    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running: bool = True
        self.state = CallState(call_id=f"paired-{int(time.time() * 1000)}")
        self.audio_processor = AudioProcessor()
        self.audio_queue: Deque[bytes] = deque(maxlen=AUDIO_QUEUE_MAXLEN)
        self.pending_buffer: Deque[bytes] = deque(maxlen=PENDING_BUFFER_MAXLEN)

    def _is_opus_frame(self, payload: bytes) -> bool:
        if len(payload) < 2 or len(payload) in (160, 320, 640):
            return False
        opus_20ms_mono_pcm_size = int(RATE_OPUS * 0.02 * 2)
        return len(payload) < opus_20ms_mono_pcm_size // 4

    async def _detect_format(self, frame_len: int, payload: bytes = b"") -> None:
        CANONICAL_SIZES = {160, 320, 640}
        if (
            payload
            and PREFER_OPUS
            and self.audio_processor.opus_codec
            and frame_len not in CANONICAL_SIZES
            and self._is_opus_frame(payload)
        ):
            if self.state.ast_codec != "opus":
                self.state.ast_codec = "opus"
                self.state.ast_rate = RATE_OPUS
                self.state.format_locked = True
                self.state.format_lock_time = time.time()
                logger.info("[%s] 🎵 Format: Opus @ %dHz", self.state.call_id, RATE_OPUS)
                if self.ws and self.state.ws_connected:
                    await self.ws.send(json.dumps({
                        "type": "update_format",
                        "call_id": self.state.call_id,
                        "inbound_format": "opus",
                        "inbound_sample_rate": RATE_OPUS,
                    }))
            self.state.ast_frame_bytes = frame_len
            return

        # If format is locked, allow different frame sizes that are still compatible
        # with the locked codec (e.g. slin16 can legitimately appear as 320 bytes (10ms)
        # or 640 bytes (20ms), depending on channel framing).
        if self.state.format_locked:
            elapsed = time.time() - self.state.format_lock_time
            if elapsed < FORMAT_LOCK_DURATION_S:
                if self.state.ast_codec == "slin16" and frame_len in {320, 640}:
                    # Same codec, different framing; update frame size and keep going.
                    self.state.ast_frame_bytes = frame_len
                    return
                if self.state.ast_codec == "slin" and frame_len == 320:
                    self.state.ast_frame_bytes = frame_len
                    return
                if self.state.ast_codec == "ulaw" and frame_len == 160:
                    self.state.ast_frame_bytes = frame_len
                    return
                # Otherwise, treat as a mismatch and ignore.
                if frame_len != self.state.ast_frame_bytes:
                    if not hasattr(self.state, '_format_mismatch_logged'):
                        logger.warning(
                            "[%s] ⚠️ Ignoring frame size change %d→%d (format locked to %s)",
                            self.state.call_id,
                            self.state.ast_frame_bytes,
                            frame_len,
                            self.state.ast_codec,
                        )
                        self.state._format_mismatch_logged = True
                return

        if LOCK_FORMAT_ULAW and frame_len not in {320, 640}:
            if frame_len != self.state.ast_frame_bytes:
                logger.warning("[%s] ⚠️ Frame size %d (locked to ulaw)", self.state.call_id, frame_len)
            self.state.ast_frame_bytes = frame_len
            return

        # === OPUS DETECTION ===
        # Opus frames are typically 40-200 bytes for 20ms at 32-64kbps
        # PCM frames are always exact: 160 (ulaw 20ms), 320 (slin 20ms or slin16 10ms), 640 (slin16 20ms)
        # Variable-size frames that don't match PCM patterns are likely Opus
        is_likely_opus = False
        if FORCE_OPUS and OPUS_AVAILABLE:
            # If FORCE_OPUS is on and frame doesn't match known PCM sizes, assume Opus
            if frame_len not in {160, 320, 640}:
                is_likely_opus = True
            # Also check for small frames that could be Opus (Opus @ 64kbps = ~160 bytes/20ms)
            elif frame_len < 320 and len(payload) >= 1:
                # Opus TOC byte check - valid configs are 0-31
                toc = payload[0]
                config = (toc >> 3) & 0x1F
                if config <= 31 and frame_len < 200:
                    is_likely_opus = True

        if is_likely_opus:
            if self.state.ast_codec != "opus":
                logger.info("[%s] 🎵 Detected Opus format (%d bytes/frame)", self.state.call_id, frame_len)
            self.state.ast_codec = "opus"
            self.state.ast_rate = RATE_OPUS
            self.state.ast_frame_bytes = frame_len
            self.state.format_locked = True
            self.state.format_lock_time = time.time()
            # Notify edge function
            if self.ws and self.state.ws_connected:
                await self.ws.send(json.dumps({
                    "type": "update_format",
                    "call_id": self.state.call_id,
                    "inbound_format": "opus",
                    "inbound_sample_rate": RATE_AI,  # We resample to 24kHz
                }))
            return

        # === OBSERVATION WINDOW BEFORE LOCKING (for PCM formats) ===
        if self.state.frames_observed == 0:
            self.state.first_frame_at = time.time()
        self.state.frames_observed += 1
        if frame_len == 160:
            self.state.seen_160 = True
        elif frame_len == 320:
            self.state.seen_320 = True
        elif frame_len == 640:
            self.state.seen_640 = True

        old_codec = self.state.ast_codec

        # Decide best codec/rate based on what we've observed so far.
        # PRIORITY: slin16 (16kHz) > slin (8kHz) > ulaw (8kHz)
        # This ensures best STT quality for house numbers like "52A"
        decided_codec: str
        decided_rate: int
        
        # Force 16kHz if we see 320 or 640 bytes (common for slin16 frames)
        # 640 bytes = 20ms @ 16kHz, 320 bytes = 10ms @ 16kHz (or 20ms @ 8kHz)
        if self.state.seen_640 or self.state.seen_320:
            # Default to 16kHz for both 320 and 640 byte frames
            # This gives Whisper double the frequency data (up to 8kHz instead of 4kHz)
            decided_codec = "slin16"
            decided_rate = RATE_SLIN16
        elif self.state.seen_160:
            # 160 bytes = definitely 8kHz ulaw
            decided_codec = "ulaw"
            decided_rate = RATE_ULAW
        else:
            # Default fallback to high quality
            decided_codec = "slin16"
            decided_rate = RATE_SLIN16

        self.state.ast_codec = decided_codec
        self.state.ast_rate = decided_rate
        self.state.ast_frame_bytes = frame_len

        # Lock once we've seen enough frames, or after ~1s of audio.
        should_lock = (
            self.state.frames_observed >= FORMAT_LOCK_FRAME_COUNT
            or (time.time() - self.state.first_frame_at) >= 1.0
        )
        if should_lock:
            self.state.format_locked = True
            self.state.format_lock_time = time.time()

        if old_codec != self.state.ast_codec:
            logger.info("[%s] 🔊 Format: %s @ %dHz (%d bytes/frame)",
                        self.state.call_id, self.state.ast_codec, self.state.ast_rate, frame_len)
            if self.ws and self.state.ws_connected:
                await self.ws.send(json.dumps({
                    "type": "update_format",
                    "call_id": self.state.call_id,
                    "inbound_format": "slin16",  # We send 16-bit PCM
                    "inbound_sample_rate": RATE_AI,  # Already resampled to 24kHz
                }))

    async def connect_websocket(self, url: Optional[str] = None, init_data: Optional[dict] = None) -> bool:
        target_url = url or self.state.current_ws_url
        while self.state.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                if self.state.reconnect_attempts > 0:
                    delay = RECONNECT_BASE_DELAY_S * (2 ** (self.state.reconnect_attempts - 1))
                    logger.info("[%s] 🔄 Reconnecting in %.1fs (attempt %d/%d)",
                                self.state.call_id, delay, self.state.reconnect_attempts + 1, MAX_RECONNECT_ATTEMPTS)
                    await asyncio.sleep(delay)
                self.ws = await asyncio.wait_for(
                    websockets.connect(
                        target_url,
                        ping_interval=WS_PING_INTERVAL,
                        ping_timeout=WS_PING_TIMEOUT,
                        close_timeout=5,
                        max_queue=32,
                    ),
                    timeout=10.0,
                )
                self.state.current_ws_url = target_url
                if init_data:
                    payload = {"type": "init", **init_data, "call_id": self.state.call_id, "phone": self.state.phone if self.state.phone != "Unknown" else None}
                    await self.ws.send(json.dumps(payload))
                    logger.info("[%s] 🔀 Sent %s init", self.state.call_id, "resume" if init_data.get("resume") else "redirect")
                elif self.state.reconnect_attempts > 0 and self.state.init_sent:
                    # We resample to 24kHz before sending, so report that to edge function
                    payload = {
                        "type": "init",
                        "call_id": self.state.call_id,
                        "phone": self.state.phone if self.state.phone != "Unknown" else None,
                        "reconnect": True,
                        "inbound_format": "slin16",  # 16-bit PCM
                        "inbound_sample_rate": RATE_AI,  # Already resampled to 24kHz
                    }
                    await self.ws.send(json.dumps(payload))
                    logger.info("[%s] 🔁 Sent reconnect init", self.state.call_id)
                self.state.ws_connected = True
                self.state.last_ws_activity = time.time()
                self.state.reconnect_attempts = 0
                logger.info("[%s] ✅ WebSocket connected to %s", self.state.call_id, target_url.split("/")[-1])
                flushed = 0
                while self.pending_buffer:
                    try:
                        await self.ws.send(self.pending_buffer.popleft())
                        flushed += 1
                    except Exception:
                        self.pending_buffer.clear()
                        break
                if flushed:
                    logger.info("[%s] 📤 Flushed %d pending frames", self.state.call_id, flushed)
                return True
            except asyncio.TimeoutError:
                logger.warning("[%s] ⏱️ WebSocket connection timeout", self.state.call_id)
                self.state.reconnect_attempts += 1
            except Exception as e:
                logger.error("[%s] ❌ WebSocket error: %s", self.state.call_id, e)
                self.state.reconnect_attempts += 1
        return False

    async def stop_call(self, reason: str) -> None:
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

    async def heartbeat_loop(self) -> None:
        last_keepalive_log = time.time()
        try:
            while self.running:
                await asyncio.sleep(HEARTBEAT_INTERVAL_S)
                if not self.running:
                    break
                ws_age = time.time() - self.state.last_ws_activity
                ast_age = time.time() - self.state.last_asterisk_recv
                ws_status = "🟢" if ws_age < 5 else "🟡" if ws_age < 15 else "🔴"
                ast_status = "🟢" if ast_age < 5 else "🟡" if ast_age < 15 else "🔴"
                codec_icon = "🎵" if self.state.ast_codec == "opus" else "🔊"
                logger.info("[%s] 💓 WS%s(%.1fs) AST%s(%.1fs) %s%s@%dHz TX:%d RX:%d",
                            self.state.call_id, ws_status, ws_age, ast_status, ast_age,
                            codec_icon, self.state.ast_codec, self.state.ast_rate,
                            self.state.frames_sent, self.state.frames_received)
                if self.state.keepalive_count > 0 and time.time() - last_keepalive_log > 30:
                    logger.debug("[%s] 💤 Silence frames sent: %d", self.state.call_id, self.state.keepalive_count)
                    last_keepalive_log = time.time()
        except asyncio.CancelledError:
            pass

    async def run(self) -> None:
        peer = self.writer.get_extra_info("peername")
        logger.info("[%s] 📞 New call from %s", self.state.call_id, peer)
        
        # Wait for UUID message from Asterisk first (contains phone number)
        # Asterisk sends MSG_UUID as the first message with the call UUID
        try:
            header = await asyncio.wait_for(self.reader.readexactly(3), timeout=2.0)
            msg_type = header[0]
            msg_len = struct.unpack(">H", header[1:3])[0]
            payload = await self.reader.readexactly(msg_len)
            
            if msg_type == MSG_UUID:
                # UUID is sent as raw bytes, convert to hex string
                raw_hex = payload.hex()
                logger.info("[%s] 📋 Received Asterisk UUID: %s", self.state.call_id, raw_hex)
                
                # Extract phone from last 12 hex chars (zero-padded phone number)
                if len(raw_hex) >= 12:
                    phone_digits = raw_hex[-12:]
                    # Strip leading zeros for WhatsApp format
                    trimmed = phone_digits.lstrip("0")
                    if len(trimmed) >= 9:
                        self.state.phone = trimmed
                        logger.info("[%s] 👤 Phone: %s", self.state.call_id, self.state.phone)
        except asyncio.TimeoutError:
            logger.warning("[%s] ⚠️ No UUID message received, continuing without phone", self.state.call_id)
        except Exception as e:
            logger.warning("[%s] ⚠️ Error reading UUID: %s", self.state.call_id, e)
        
        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())
        try:
            while self.running:
                if not self.state.ws_connected:
                    if not await self.connect_websocket():
                        await self.stop_call("WebSocket connection failed")
                        break
                if not self.state.init_sent and self.ws:
                    # Use phone extracted from UUID or "unknown"
                    caller_phone = self.state.phone if self.state.phone != "Unknown" else "unknown"
                    
                    # We resample all audio to 24kHz before sending, so report that
                    payload = {
                        "type": "init",
                        "call_id": self.state.call_id,
                        "phone": caller_phone,
                        "caller_phone": caller_phone,
                        "user_phone": caller_phone,
                        "addressTtsSplicing": True,
                        "eager_init": True,
                        "inbound_format": "slin16",
                        "inbound_sample_rate": RATE_AI,
                    }
                    await self.ws.send(json.dumps(payload))
                    self.state.init_sent = True
                    logger.info("[%s] 🚀 Eager init (slin16 @ %dHz, phone: %s)", self.state.call_id, RATE_AI, caller_phone)
                ast_task = asyncio.create_task(self.asterisk_to_ai())
                ai_task = asyncio.create_task(self.ai_to_queue())
                done, pending = await asyncio.wait({ast_task, ai_task}, return_when=asyncio.FIRST_COMPLETED)
                for t in pending:
                    t.cancel()
                await asyncio.gather(*pending, return_exceptions=True)
                exc: Optional[BaseException] = None
                for t in done:
                    exc = t.exception()
                    if exc:
                        break
                if exc is None:
                    break
                if isinstance(exc, RedirectException):
                    logger.info("[%s] 🔀 Redirect/handoff to %s", self.state.call_id, exc.url)
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
                    logger.info("[%s] 📴 Call formally ended", self.state.call_id)
                    break
                if isinstance(exc, (ConnectionClosed, WebSocketException)):
                    logger.warning("[%s] 🔌 WebSocket closed: %s", self.state.call_id, exc)
                    self.state.ws_connected = False
                    self.state.reconnect_attempts = max(self.state.reconnect_attempts, 1)
                    try:
                        if self.ws:
                            await self.ws.close()
                    except Exception:
                        pass
                    self.ws = None
                    continue
                logger.error("[%s] ❌ Unhandled error: %s", self.state.call_id, exc)
                await self.stop_call("Unhandled error")
                break
        finally:
            self.running = False
            for task in [playback_task, heartbeat_task]:
                if not task.done():
                    task.cancel()
            await asyncio.gather(playback_task, heartbeat_task, return_exceptions=True)
            logger.info("[%s] 📊 Final: %s@%dHz TX=%d RX=%d KA=%d HO=%d",
                        self.state.call_id, self.state.ast_codec, self.state.ast_rate,
                        self.state.frames_sent, self.state.frames_received,
                        self.state.keepalive_count, self.state.handoff_count)
            await self.cleanup()

    async def cleanup(self) -> None:
        self.audio_queue.clear()
        self.pending_buffer.clear()
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
                header = await asyncio.wait_for(self.reader.readexactly(3), timeout=ASTERISK_READ_TIMEOUT_S)
                self.state.last_asterisk_recv = time.time()
                msg_type = header[0]
                msg_len = struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(msg_len)
                if msg_type == MSG_UUID:
                    # UUID might be sent again, update phone if not already set
                    raw_hex = payload.hex()
                    if len(raw_hex) >= 12:
                        phone_digits = raw_hex[-12:]
                        trimmed = phone_digits.lstrip("0")
                        if len(trimmed) >= 9 and self.state.phone == "Unknown":
                            self.state.phone = trimmed
                            logger.info("[%s] 👤 Phone (late): %s", self.state.call_id, self.state.phone)
                            # Send update to edge function
                            if self.ws and self.state.ws_connected:
                                await self.ws.send(json.dumps({
                                    "type": "update_phone",
                                    "call_id": self.state.call_id,
                                    "phone": self.state.phone,
                                    "user_phone": self.state.phone,
                                }))
                elif msg_type == MSG_AUDIO:
                    if msg_len != self.state.ast_frame_bytes or self.state.ast_codec != "opus":
                        await self._detect_format(msg_len, payload)
                    is_high_quality = self.state.ast_codec in ("slin16", "opus")
                    if self.state.ast_codec == "opus":
                        if self.audio_processor.opus_codec:
                            linear = self.audio_processor.opus_codec.decode_with_resample(payload, RATE_AI)
                        else:
                            logger.warning("[%s] ⚠️ Opus frame but no decoder", self.state.call_id)
                            continue
                    elif self.state.ast_codec == "ulaw":
                        linear = self.audio_processor.ulaw_to_linear(payload)
                        # Resample 8kHz → 24kHz for AI
                        linear = self.audio_processor.resample(linear, RATE_ULAW, RATE_AI)
                    elif self.state.ast_codec == "slin":
                        # slin (PCM16 @ 8kHz) → resample to 24kHz
                        linear = self.audio_processor.resample(payload, RATE_SLIN, RATE_AI)
                    else:
                        # slin16 @ 16kHz → resample to 24kHz
                        linear = self.audio_processor.resample(payload, RATE_SLIN16, RATE_AI)
                    
                    processed = self.audio_processor.process_inbound(linear, is_high_quality=is_high_quality)
                    if self.ws and self.state.ws_connected:
                        try:
                            await self.ws.send(processed)
                            self.state.frames_sent += 1
                            self.state.last_ws_activity = time.time()
                        except Exception:
                            self.pending_buffer.append(processed)
                            raise
                elif msg_type == MSG_HANGUP:
                    logger.info("[%s] 📴 Hangup from Asterisk", self.state.call_id)
                    await self.stop_call("Asterisk hangup")
                    return
        except asyncio.TimeoutError:
            logger.warning("[%s] ⏱️ Asterisk read timeout", self.state.call_id)
            await self.stop_call("Asterisk timeout")
        except asyncio.IncompleteReadError:
            logger.info("[%s] 📴 Asterisk connection closed", self.state.call_id)
            await self.stop_call("Asterisk closed")
        except (ConnectionClosed, WebSocketException):
            raise
        except asyncio.CancelledError:
            pass
        except Exception as e:
            logger.error("[%s] ❌ Asterisk read error: %s", self.state.call_id, e)
            await self.stop_call("Read error")

    async def ai_to_queue(self) -> None:
        try:
            async for message in self.ws:
                if not self.running:
                    break
                self.state.last_ws_activity = time.time()
                if isinstance(message, bytes):
                    out = self.audio_processor.process_outbound(
                        message, RATE_AI, self.state.ast_rate, self.state.ast_codec
                    )
                    self.audio_queue.append(out)
                    self.state.frames_received += 1
                    continue
                data = json.loads(message)
                msg_type = data.get("type")
                if msg_type in ("audio", "address_tts"):
                    raw_audio = base64.b64decode(data["audio"])
                    out = self.audio_processor.process_outbound(
                        raw_audio, RATE_AI, self.state.ast_rate, self.state.ast_codec
                    )
                    self.audio_queue.append(out)
                    self.state.frames_received += 1
                elif msg_type == "transcript":
                    role = data.get("role", "?").upper()
                    text = data.get("text", "")
                    logger.info("[%s] 💬 %s: %s", self.state.call_id, role, text)
                elif msg_type in ("speech_started", "speech_stopped"):
                    duration = data.get("duration", "")
                    suffix = f" ({duration}s)" if duration else ""
                    emoji = "🎤" if msg_type == "speech_started" else "🔇"
                    logger.debug("[%s] %s Speech %s%s", self.state.call_id, emoji,
                                 "started" if msg_type == "speech_started" else "stopped", suffix)
                elif msg_type == "ai_interrupted":
                    flushed = len(self.audio_queue)
                    self.audio_queue.clear()
                    logger.info("[%s] 🛑 Barge-in: flushed %d chunks", self.state.call_id, flushed)
                elif msg_type == "redirect":
                    raise RedirectException(data.get("url", WS_URL), data.get("init_data", {}))
                elif msg_type == "session.handoff":
                    self.state.handoff_count += 1
                    logger.info("[%s] 🔄 Session handoff #%d", self.state.call_id, self.state.handoff_count)
                    inbound_fmt = self.state.ast_codec
                    raise RedirectException(
                        url=self.state.current_ws_url,
                        init_data={
                            "resume": True,
                            "resume_call_id": data.get("call_id", self.state.call_id),
                            "phone": self.state.phone,
                            "inbound_format": inbound_fmt,
                            "inbound_sample_rate": self.state.ast_rate,
                        }
                    )
                elif msg_type == "call_ended":
                    logger.info("[%s] 📴 AI ended call: %s", self.state.call_id, data.get("reason", "unknown"))
                    self.state.call_formally_ended = True
                    raise CallEndedException()
                elif msg_type == "keepalive":
                    if self.ws and self.state.ws_connected:
                        await self.ws.send(json.dumps({
                            "type": "keepalive_ack",
                            "timestamp": data.get("timestamp"),
                            "call_id": self.state.call_id,
                        }))
                elif msg_type == "error":
                    logger.error("[%s] 🧨 AI error: %s", self.state.call_id, data.get("error", "unknown"))
        except (RedirectException, CallEndedException):
            raise
        except (ConnectionClosed, WebSocketException):
            raise
        except asyncio.CancelledError:
            pass
        except Exception as e:
            logger.error("[%s] ❌ AI message error: %s", self.state.call_id, e)

    async def queue_to_asterisk(self) -> None:
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()
        try:
            while self.running:
                while self.audio_queue:
                    buffer.extend(self.audio_queue.popleft())
                if self.state.ast_codec == "opus":
                    bytes_per_sec = OPUS_BITRATE // 8
                else:
                    # Derive pacing from the negotiated sample rate instead of assuming 20ms frames.
                    # This prevents "fast Ada" when AudioSocket uses 10ms frames (e.g. 320 bytes at 16kHz).
                    if self.state.ast_codec == "ulaw":
                        bytes_per_sec = max(1, self.state.ast_rate)  # 8k samples/sec * 1 byte/sample
                    else:
                        bytes_per_sec = max(1, self.state.ast_rate * 2)  # PCM16 mono
                expected_time = start_time + (bytes_played / max(1, bytes_per_sec))
                delay = expected_time - time.time()
                if delay > 0:
                    await asyncio.sleep(delay)
                if not self.running:
                    break
                if self.state.ast_codec == "opus":
                    if len(buffer) > 0:
                        chunk = bytes(buffer)
                        buffer.clear()
                    else:
                        if self.audio_processor.opus_codec:
                            silence_pcm = bytes(self.audio_processor.opus_codec.frame_size * 2)
                            chunk = self.audio_processor.opus_codec.encode(silence_pcm)
                        else:
                            chunk = b"\x00" * 80
                        self.state.keepalive_count += 1
                else:
                    if len(buffer) >= self.state.ast_frame_bytes:
                        chunk = bytes(buffer[:self.state.ast_frame_bytes])
                        del buffer[:self.state.ast_frame_bytes]
                    else:
                        chunk = self._silence_frame()
                        self.state.keepalive_count += 1
                try:
                    frame = struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk
                    self.writer.write(frame)
                    await self.writer.drain()
                    bytes_played += len(chunk)
                    self.state.last_asterisk_send = time.time()
                except (BrokenPipeError, ConnectionResetError, OSError) as e:
                    logger.warning("[%s] 🔌 Asterisk pipe closed: %s", self.state.call_id, e)
                    await self.stop_call("Asterisk disconnected")
                    return
                except Exception as e:
                    logger.error("[%s] ❌ Asterisk write error: %s", self.state.call_id, e)
                    await self.stop_call("Write error")
                    return
        except asyncio.CancelledError:
            pass

    def _silence_frame(self) -> bytes:
        silence_byte = 0xFF if self.state.ast_codec == "ulaw" else 0x00
        return bytes([silence_byte]) * self.state.ast_frame_bytes

# =============================================================================
# MAIN
# =============================================================================
async def main() -> None:
    server = await asyncio.start_server(
        lambda r, w: TaxiBridge(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    opus_status = "✅ available" if OPUS_AVAILABLE else "❌ not installed (pip install opuslib)"
    lines = [
        f"🚀 Taxi Bridge v{TaxiBridge.VERSION} - OPUS 48kHz MODE",
        f"   Listening: {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}",
        f"   Endpoint:  {WS_URL.split('/')[-1]}",
        f"   Opus:      {opus_status} @ {OPUS_BITRATE//1000}kbps",
        f"   ForceOpus: {'✅ on' if FORCE_OPUS else 'off'}",
        f"   Force16k:  {'on' if FORCE_SLIN16 else 'off'}",
        f"   DSP:       noise_gate + adaptive pre-emph + soft clip",
        f"   Reconnect: {MAX_RECONNECT_ATTEMPTS} attempts",
    ]
    for line in lines:
        print(line, flush=True)
        logger.info(line)
    if not OPUS_AVAILABLE:
        logger.warning("⚠️ Opus codec not available. Install with: pip install opuslib")
    async with server:
        await server.serve_forever()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("👋 Shutdown requested")
