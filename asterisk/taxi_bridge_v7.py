#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge v7.6 - PRODUCTION READY

Architecture:
  Asterisk AudioSocket ←→ Bridge ←→ Edge Function (taxi-realtime-paired)

Key Features:
  • Context-pairing architecture with eager init (phone sent later via update_phone)
  • Dynamic format detection with smart locking (ulaw/slin/slin16)
  • High-quality DSP pipeline: Volume Boost → AGC → Pre-emphasis
  • resample_poly for all audio paths (preserves consonant transients)
  • Session handoff support for 90s edge function limits
  • Bounded queues to prevent OOM under network stalls
  • Comprehensive diagnostics with WS/Asterisk heartbeat tracking

Changelog:
  v7.6: Refactored DSP into AudioProcessor class, improved format detection,
        added session handoff, enhanced diagnostics, cleaner architecture
  v7.5: Dynamic pacing, format auto-unlock on PCM detection
  v7.4: Volume boost + AGC for quiet telephony lines
  v7.3: resample_poly + pre-emphasis for consonant clarity
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
from typing import Deque, Optional

import numpy as np
from scipy.signal import resample_poly
import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException


# =============================================================================
# CONFIGURATION
# =============================================================================

def _load_ws_url() -> str:
    """Load WebSocket URL from env or config file."""
    if url := os.environ.get("WS_URL"):
        return url
    try:
        config_path = os.path.join(os.path.dirname(__file__), "..", "bridge-config.json")
        with open(config_path) as f:
            cfg = json.load(f).get("edge_functions", {})
            return (
                cfg.get("taxi_realtime_paired_ws")
                or cfg.get("taxi_realtime_simple_ws")
                or cfg.get("taxi_realtime_ws")
                or "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired"
            )
    except (FileNotFoundError, json.JSONDecodeError, KeyError):
        return "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired"


def _env_bool(name: str, default: bool = False) -> bool:
    """Parse boolean from environment variable."""
    val = os.environ.get(name, "").strip().lower()
    return val in ("1", "true", "yes", "on") if val else default


def _env_float(name: str, default: float) -> float:
    """Parse float from environment variable."""
    try:
        return float(os.environ.get(name, default))
    except (ValueError, TypeError):
        return default


# Network Configuration
WS_URL = _load_ws_url()
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = int(os.environ.get("AUDIOSOCKET_PORT", 9092))

# Audio Rates (Hz)
RATE_ULAW = 8000      # µ-law telephony
RATE_SLIN16 = 16000   # Signed linear 16kHz (wideband)
RATE_AI = 24000       # OpenAI TTS output

# Format Detection
LOCK_FORMAT_ULAW = _env_bool("LOCK_FORMAT_ULAW", False)  # False = auto-detect
PREFER_SLIN16 = True  # Prefer wideband when available
FORMAT_LOCK_DURATION_S = 0.75  # Debounce format switching

# DSP Pipeline Configuration
ENABLE_VOLUME_BOOST = True
ENABLE_AGC = True
VOLUME_BOOST_FACTOR = 3.0      # Fixed multiplier (before AGC)
TARGET_RMS = 300               # Target RMS level
AGC_MAX_GAIN = 15.0            # Maximum gain multiplier
AGC_MIN_GAIN = 1.0             # Never reduce volume
AGC_SMOOTHING = 0.15           # Gain adaptation speed
AGC_FLOOR_RMS = 10             # Below this, don't apply AGC
PRE_EMPHASIS_COEFF = _env_float("PRE_EMPHASIS_COEFF", 0.95)

# Connection Settings
MAX_RECONNECT_ATTEMPTS = 5
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15.0
WS_PING_INTERVAL = 20
WS_PING_TIMEOUT = 20
ASTERISK_READ_TIMEOUT_S = 30.0

# Queue Bounds (memory safety)
AUDIO_QUEUE_MAXLEN = 200
PENDING_BUFFER_MAXLEN = 100

# AudioSocket Message Types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# µ-law Encoding Constants
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
# AUDIO PROCESSING
# =============================================================================

class AudioProcessor:
    """Encapsulates all DSP operations for clean separation of concerns."""
    
    def __init__(self):
        self.last_gain: float = 1.0
    
    @staticmethod
    def ulaw_to_linear(ulaw_bytes: bytes) -> bytes:
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
    
    @staticmethod
    def linear_to_ulaw(pcm_bytes: bytes) -> bytes:
        """Encode 16-bit linear PCM to µ-law."""
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
        """Apply pre-emphasis filter to boost high-frequency consonants."""
        if samples.size == 0:
            return samples
        return np.append(samples[0], samples[1:] - coeff * samples[:-1])
    
    @staticmethod
    def resample(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
        """High-quality polyphase resampling."""
        if from_rate == to_rate or not audio_bytes:
            return audio_bytes
        
        samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return b""
        
        g = gcd(from_rate, to_rate)
        resampled = resample_poly(samples, up=to_rate // g, down=from_rate // g)
        return np.clip(resampled, -32768, 32767).astype(np.int16).tobytes()
    
    def process_inbound(self, pcm_bytes: bytes) -> bytes:
        """
        Full DSP pipeline for audio going TO the AI (Asterisk → Edge Function).
        
        Pipeline: Decode → Volume Boost → AGC → Pre-emphasis → Output
        """
        if not pcm_bytes or len(pcm_bytes) < 4:
            return pcm_bytes
        
        samples = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return pcm_bytes
        
        # Step 1: Volume boost for quiet telephony lines
        if ENABLE_VOLUME_BOOST:
            samples *= VOLUME_BOOST_FACTOR
        
        # Step 2: AGC to normalize levels
        if ENABLE_AGC:
            rms = float(np.sqrt(np.mean(samples ** 2)))
            if rms > AGC_FLOOR_RMS:
                target_gain = float(np.clip(TARGET_RMS / rms, AGC_MIN_GAIN, AGC_MAX_GAIN))
                self.last_gain += AGC_SMOOTHING * (target_gain - self.last_gain)
                samples *= self.last_gain
        
        # Step 3: Pre-emphasis for consonant clarity
        samples = self.pre_emphasis(samples, PRE_EMPHASIS_COEFF)
        
        return np.clip(samples, -32768, 32767).astype(np.int16).tobytes()
    
    def process_outbound(self, ai_audio: bytes, from_rate: int, to_rate: int, 
                         to_codec: str) -> bytes:
        """
        Process audio coming FROM the AI (Edge Function → Asterisk).
        
        Pipeline: Resample → Encode (if µ-law)
        """
        # Resample from AI rate to Asterisk rate
        resampled = self.resample(ai_audio, from_rate, to_rate)
        
        # Encode to µ-law if needed
        if to_codec == "ulaw":
            return self.linear_to_ulaw(resampled)
        return resampled


# =============================================================================
# EXCEPTIONS
# =============================================================================

class RedirectException(Exception):
    """Raised when server sends a redirect/handoff."""
    def __init__(self, url: str, init_data: dict):
        self.url = url
        self.init_data = init_data
        super().__init__(f"Redirect to {url}")


class CallEndedException(Exception):
    """Raised when the call has formally ended."""
    pass


# =============================================================================
# CALL STATE
# =============================================================================

@dataclass
class CallState:
    """Tracks all state for a single call session."""
    call_id: str
    phone: str = "Unknown"
    
    # Audio Format (detected from Asterisk frame sizes)
    ast_codec: str = "ulaw"       # ulaw, slin, or slin16
    ast_rate: int = RATE_ULAW     # 8000 or 16000
    ast_frame_bytes: int = 160    # 160 (ulaw), 320 (slin), 640 (slin16)
    
    # Format detection state
    format_locked: bool = False
    format_lock_time: float = 0.0
    
    # WebSocket state
    ws_connected: bool = False
    reconnect_attempts: int = 0
    last_ws_activity: float = field(default_factory=time.time)
    current_ws_url: str = field(default_factory=lambda: WS_URL)
    init_sent: bool = False
    call_formally_ended: bool = False
    
    # Asterisk state
    last_asterisk_send: float = field(default_factory=time.time)
    last_asterisk_recv: float = field(default_factory=time.time)
    
    # Metrics
    frames_sent: int = 0
    frames_received: int = 0
    keepalive_count: int = 0
    handoff_count: int = 0


# =============================================================================
# BRIDGE CLASS
# =============================================================================

class TaxiBridge:
    """Main bridge connecting Asterisk AudioSocket to AI Edge Function."""
    
    VERSION = "7.6"
    
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running: bool = True
        
        self.state = CallState(call_id=f"paired-{int(time.time() * 1000)}")
        self.audio_processor = AudioProcessor()
        
        # Bounded queues for memory safety
        self.audio_queue: Deque[bytes] = deque(maxlen=AUDIO_QUEUE_MAXLEN)
        self.pending_buffer: Deque[bytes] = deque(maxlen=PENDING_BUFFER_MAXLEN)
    
    # -------------------------------------------------------------------------
    # FORMAT DETECTION
    # -------------------------------------------------------------------------
    
    async def _detect_format(self, frame_len: int) -> None:
        """
        Detect Asterisk audio format from frame size.
        
        Frame sizes (20ms):
          • µ-law 8kHz:  160 bytes (1 byte/sample × 8000 × 0.02)
          • slin 8kHz:   320 bytes (2 bytes/sample × 8000 × 0.02)
          • slin16 16kHz: 640 bytes (2 bytes/sample × 16000 × 0.02)
        """
        CANONICAL_SIZES = {160, 320, 640}
        
        # Honor ulaw lock unless we see definitive PCM frame sizes
        if LOCK_FORMAT_ULAW and frame_len not in {320, 640}:
            if frame_len != self.state.ast_frame_bytes:
                logger.warning("[%s] ⚠️ Frame size %d (locked to ulaw)", 
                              self.state.call_id, frame_len)
            self.state.ast_frame_bytes = frame_len
            return
        
        # Debounce unless we see canonical sizes
        if self.state.format_locked:
            elapsed = time.time() - self.state.format_lock_time
            if elapsed < FORMAT_LOCK_DURATION_S and frame_len not in CANONICAL_SIZES:
                self.state.ast_frame_bytes = frame_len
                return
            self.state.format_locked = False
        
        old_codec = self.state.ast_codec
        old_rate = self.state.ast_rate
        
        # Detect format
        if frame_len == 640:
            self.state.ast_codec = "slin16"
            self.state.ast_rate = RATE_SLIN16
        elif frame_len == 320:
            self.state.ast_codec = "slin"
            self.state.ast_rate = RATE_ULAW
        elif frame_len == 160:
            self.state.ast_codec = "ulaw"
            self.state.ast_rate = RATE_ULAW
        else:
            # Heuristic for unusual sizes
            if frame_len > 400:
                self.state.ast_codec = "slin16"
                self.state.ast_rate = RATE_SLIN16
            else:
                self.state.ast_codec = "slin"
                self.state.ast_rate = RATE_ULAW
            logger.warning("[%s] ⚠️ Unusual frame size %d, assuming %s", 
                          self.state.call_id, frame_len, self.state.ast_codec)
        
        self.state.ast_frame_bytes = frame_len
        self.state.format_locked = True
        self.state.format_lock_time = time.time()
        
        # Log format detection
        if old_codec != self.state.ast_codec:
            logger.info("[%s] 🔊 Format: %s @ %dHz (%d bytes/frame)", 
                       self.state.call_id, self.state.ast_codec, 
                       self.state.ast_rate, frame_len)
            
            # Notify edge function of format change
            if self.ws and self.state.ws_connected:
                await self.ws.send(json.dumps({
                    "type": "update_format",
                    "call_id": self.state.call_id,
                    "inbound_format": "slin",  # Always send PCM16 after processing
                    "inbound_sample_rate": self.state.ast_rate,
                }))
    
    # -------------------------------------------------------------------------
    # WEBSOCKET CONNECTION
    # -------------------------------------------------------------------------
    
    async def connect_websocket(self, url: Optional[str] = None,
                                init_data: Optional[dict] = None) -> bool:
        """Connect to WebSocket with exponential backoff."""
        target_url = url or self.state.current_ws_url
        
        while self.state.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                # Exponential backoff
                if self.state.reconnect_attempts > 0:
                    delay = RECONNECT_BASE_DELAY_S * (2 ** (self.state.reconnect_attempts - 1))
                    logger.info("[%s] 🔄 Reconnecting in %.1fs (attempt %d/%d)", 
                               self.state.call_id, delay, 
                               self.state.reconnect_attempts + 1, MAX_RECONNECT_ATTEMPTS)
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
                
                # Send appropriate init message
                if init_data:
                    # Redirect/handoff init
                    payload = {
                        "type": "init",
                        **init_data,
                        "call_id": self.state.call_id,
                        "phone": None if self.state.phone == "Unknown" else self.state.phone,
                    }
                    await self.ws.send(json.dumps(payload))
                    logger.info("[%s] 🔀 Sent %s init", self.state.call_id,
                               "resume" if init_data.get("resume") else "redirect")
                    self.state.init_sent = True
                elif self.state.reconnect_attempts > 0 and self.state.init_sent:
                    # Reconnect init
                    payload = {
                        "type": "init",
                        "call_id": self.state.call_id,
                        "phone": None if self.state.phone == "Unknown" else self.state.phone,
                        "reconnect": True,
                        "inbound_format": "slin",
                        "inbound_sample_rate": self.state.ast_rate,
                    }
                    await self.ws.send(json.dumps(payload))
                    logger.info("[%s] 🔁 Sent reconnect init", self.state.call_id)
                
                self.state.ws_connected = True
                self.state.last_ws_activity = time.time()
                self.state.reconnect_attempts = 0
                
                logger.info("[%s] ✅ WebSocket connected to %s", 
                           self.state.call_id, target_url.split("/")[-1])
                
                # Flush pending audio buffer
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
        """Periodic status logging with WS and Asterisk health."""
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
                
                logger.info("[%s] 💓 WS%s(%.1fs) AST%s(%.1fs) TX:%d RX:%d KA:%d HO:%d",
                           self.state.call_id, ws_status, ws_age, ast_status, ast_age,
                           self.state.frames_sent, self.state.frames_received,
                           self.state.keepalive_count, self.state.handoff_count)
                
                # Log keepalive activity periodically
                if self.state.keepalive_count > 0 and time.time() - last_keepalive_log > 30:
                    logger.debug("[%s] 💤 Silence frames sent: %d", 
                                self.state.call_id, self.state.keepalive_count)
                    last_keepalive_log = time.time()
                    
        except asyncio.CancelledError:
            pass
    
    async def run(self) -> None:
        """Main bridge loop."""
        peer = self.writer.get_extra_info("peername")
        logger.info("[%s] 📞 New call from %s", self.state.call_id, peer)
        
        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())
        
        try:
            while self.running:
                # Connect/reconnect WebSocket
                if not self.state.ws_connected:
                    if not await self.connect_websocket():
                        await self.stop_call("WebSocket connection failed")
                        break
                    
                    # Send eager init on first connection
                    if not self.state.init_sent and self.ws:
                        payload = {
                            "type": "init",
                            "call_id": self.state.call_id,
                            "phone": "unknown",
                            "user_phone": "unknown",
                            "addressTtsSplicing": True,
                            "eager_init": True,
                            "inbound_format": "slin",
                            "inbound_sample_rate": self.state.ast_rate,
                        }
                        await self.ws.send(json.dumps(payload))
                        self.state.init_sent = True
                        logger.info("[%s] 🚀 Eager init (slin @ %dHz)", 
                                   self.state.call_id, self.state.ast_rate)
                
                # Create bidirectional audio tasks
                ast_task = asyncio.create_task(self.asterisk_to_ai())
                ai_task = asyncio.create_task(self.ai_to_queue())
                
                done, pending = await asyncio.wait(
                    {ast_task, ai_task},
                    return_when=asyncio.FIRST_COMPLETED,
                )
                
                # Cancel pending tasks
                for t in pending:
                    t.cancel()
                await asyncio.gather(*pending, return_exceptions=True)
                
                # Handle completed task result
                exc: Optional[BaseException] = None
                for t in done:
                    exc = t.exception()
                    if exc:
                        break
                
                if exc is None:
                    break  # Clean exit
                
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
                    continue  # Reconnect
                
                logger.error("[%s] ❌ Unhandled error: %s", self.state.call_id, exc)
                await self.stop_call("Unhandled error")
                break
        
        finally:
            self.running = False
            
            # Cancel background tasks
            for task in [playback_task, heartbeat_task]:
                if not task.done():
                    task.cancel()
            await asyncio.gather(playback_task, heartbeat_task, return_exceptions=True)
            
            # Log final stats
            logger.info("[%s] 📊 Final: TX=%d RX=%d KA=%d HO=%d",
                       self.state.call_id, self.state.frames_sent, 
                       self.state.frames_received, self.state.keepalive_count,
                       self.state.handoff_count)
            
            await self.cleanup()
    
    async def cleanup(self) -> None:
        """Release all resources."""
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
    
    # -------------------------------------------------------------------------
    # ASTERISK → AI
    # -------------------------------------------------------------------------
    
    async def asterisk_to_ai(self) -> None:
        """Read audio from Asterisk and forward to AI."""
        try:
            while self.running and self.state.ws_connected:
                try:
                    header = await asyncio.wait_for(
                        self.reader.readexactly(3), 
                        timeout=ASTERISK_READ_TIMEOUT_S
                    )
                    self.state.last_asterisk_recv = time.time()
                    
                    msg_type = header[0]
                    msg_len = struct.unpack(">H", header[1:3])[0]
                    payload = await self.reader.readexactly(msg_len)
                    
                    if msg_type == MSG_UUID:
                        # Extract phone number from UUID
                        raw_hex = payload.hex()
                        if len(raw_hex) >= 12:
                            self.state.phone = raw_hex[-12:]
                        logger.info("[%s] 👤 Phone: %s", self.state.call_id, self.state.phone)
                        
                        # Send phone update to edge function
                        if self.ws and self.state.ws_connected:
                            await self.ws.send(json.dumps({
                                "type": "update_phone",
                                "call_id": self.state.call_id,
                                "phone": self.state.phone,
                                "user_phone": self.state.phone,
                            }))
                    
                    elif msg_type == MSG_AUDIO:
                        # Detect format if frame size changed
                        if msg_len != self.state.ast_frame_bytes:
                            await self._detect_format(msg_len)
                        
                        # Decode µ-law if needed
                        if self.state.ast_codec == "ulaw":
                            linear = self.audio_processor.ulaw_to_linear(payload)
                        else:
                            linear = payload
                        
                        # Apply DSP pipeline (volume boost → AGC → pre-emphasis)
                        processed = self.audio_processor.process_inbound(linear)
                        
                        # Send to AI
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
                    return
                except asyncio.IncompleteReadError:
                    logger.info("[%s] 📴 Asterisk connection closed", self.state.call_id)
                    await self.stop_call("Asterisk closed")
                    return
                except (ConnectionClosed, WebSocketException):
                    raise
                except asyncio.CancelledError:
                    return
                except Exception as e:
                    logger.error("[%s] ❌ Asterisk read error: %s", self.state.call_id, e)
                    await self.stop_call("Read error")
                    return
                    
        except asyncio.CancelledError:
            pass
    
    # -------------------------------------------------------------------------
    # AI → QUEUE
    # -------------------------------------------------------------------------
    
    async def ai_to_queue(self) -> None:
        """Receive audio and control messages from AI."""
        try:
            async for message in self.ws:
                if not self.running:
                    break
                
                self.state.last_ws_activity = time.time()
                
                # Binary audio from AI
                if isinstance(message, bytes):
                    out = self.audio_processor.process_outbound(
                        message, RATE_AI, self.state.ast_rate, self.state.ast_codec
                    )
                    self.audio_queue.append(out)
                    self.state.frames_received += 1
                    continue
                
                # JSON control messages
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
                    # Edge function approaching 90s limit
                    self.state.handoff_count += 1
                    logger.info("[%s] 🔄 Session handoff #%d", 
                               self.state.call_id, self.state.handoff_count)
                    raise RedirectException(
                        url=self.state.current_ws_url,
                        init_data={
                            "resume": True,
                            "resume_call_id": data.get("call_id", self.state.call_id),
                            "phone": self.state.phone,
                            "inbound_format": "slin",
                            "inbound_sample_rate": self.state.ast_rate,
                        }
                    )
                
                elif msg_type == "call_ended":
                    logger.info("[%s] 📴 AI ended call: %s", 
                               self.state.call_id, data.get("reason", "unknown"))
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
                    logger.error("[%s] 🧨 AI error: %s", 
                                self.state.call_id, data.get("error", "unknown"))
        
        except (RedirectException, CallEndedException):
            raise
        except (ConnectionClosed, WebSocketException):
            raise
        except asyncio.CancelledError:
            pass
        except Exception as e:
            logger.error("[%s] ❌ AI message error: %s", self.state.call_id, e)
    
    # -------------------------------------------------------------------------
    # QUEUE → ASTERISK
    # -------------------------------------------------------------------------
    
    async def queue_to_asterisk(self) -> None:
        """Stream audio from queue to Asterisk with proper pacing."""
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()
        
        try:
            while self.running:
                # Drain queue to buffer
                while self.audio_queue:
                    buffer.extend(self.audio_queue.popleft())
                
                # Calculate pacing: bytes_per_sec based on 20ms frames
                # 50 frames/sec × frame_size = bytes/sec
                bytes_per_sec = max(1, self.state.ast_frame_bytes * 50)
                
                # Pace to real-time
                expected_time = start_time + (bytes_played / bytes_per_sec)
                delay = expected_time - time.time()
                if delay > 0:
                    await asyncio.sleep(delay)
                
                if not self.running:
                    break
                
                # Get next frame (or silence)
                if len(buffer) >= self.state.ast_frame_bytes:
                    chunk = bytes(buffer[:self.state.ast_frame_bytes])
                    del buffer[:self.state.ast_frame_bytes]
                else:
                    chunk = self._silence_frame()
                    self.state.keepalive_count += 1
                
                # Send to Asterisk
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
        """Generate one frame of silence."""
        silence_byte = 0xFF if self.state.ast_codec == "ulaw" else 0x00
        return bytes([silence_byte]) * self.state.ast_frame_bytes


# =============================================================================
# MAIN
# =============================================================================

async def main() -> None:
    """Start the bridge server."""
    server = await asyncio.start_server(
        lambda r, w: TaxiBridge(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    
    # Startup banner
    lines = [
        f"🚀 Taxi Bridge v{TaxiBridge.VERSION} - PRODUCTION READY",
        f"   Listening: {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}",
        f"   Endpoint:  {WS_URL.split('/')[-1]}",
        f"   Format:    {'ulaw locked' if LOCK_FORMAT_ULAW else 'auto-detect (ulaw/slin/slin16)'}",
        f"   DSP:       boost={VOLUME_BOOST_FACTOR}x AGC={AGC_MAX_GAIN}x pre-emph={PRE_EMPHASIS_COEFF}",
        f"   Reconnect: {MAX_RECONNECT_ATTEMPTS} attempts, {RECONNECT_BASE_DELAY_S}s base delay",
    ]
    for line in lines:
        print(line, flush=True)
        logger.info(line)
    
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("👋 Shutdown requested")
