#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge - LOCAL STT Architecture

Architecture:
  Asterisk AudioSocket ←→ Bridge ←→ OpenAI Realtime (local STT)
                                  ←→ Edge Function (logic + TTS)

Flow:
  1. Asterisk sends audio to bridge
  2. Bridge sends audio to OpenAI Realtime (connected directly)
  3. OpenAI transcribes speech and returns text
  4. Bridge sends transcript to edge function via HTTP
  5. Edge function processes booking logic, returns TTS audio
  6. Bridge plays TTS audio back to Asterisk

Benefits:
  • Fast STT via OpenAI Realtime (~500ms)
  • Stable HTTP calls to edge function (no WebSocket timeouts)
  • Lower bandwidth (only text + TTS audio over network)

Requirements:
  • OPENAI_API_KEY environment variable
  • Edge function: taxi-text-handler (receives text, returns TTS)
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
import aiohttp

# =============================================================================
# CONFIGURATION
# =============================================================================

# OpenAI Realtime API
OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY", "")
OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17"

# Edge Function for conversation logic + TTS
EDGE_FUNCTION_URL = os.environ.get(
    "EDGE_FUNCTION_URL",
    "https://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-text-handler"
)

# Network Configuration
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = int(os.environ.get("AUDIOSOCKET_PORT", 9093))  # Different port

# Audio Rates (Hz)
RATE_ULAW = 8000      # µ-law telephony
RATE_SLIN16 = 16000   # Signed linear 16kHz
RATE_AI = 24000       # OpenAI audio rate

# Connection Settings
MAX_RECONNECT_ATTEMPTS = 5
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15.0
ASTERISK_READ_TIMEOUT_S = 30.0

# Queue Bounds
AUDIO_QUEUE_MAXLEN = 200

# AudioSocket Message Types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# µ-law Constants
ULAW_BIAS = 0x84
ULAW_CLIP = 32635

# DSP Pipeline
VOLUME_BOOST_FACTOR = 3.0
TARGET_RMS = 300
AGC_MAX_GAIN = 15.0
AGC_MIN_GAIN = 1.0
AGC_SMOOTHING = 0.15
AGC_FLOOR_RMS = 10


# =============================================================================
# LOGGING
# =============================================================================

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    force=True,
)
logger = logging.getLogger("TaxiBridgeLocalSTT")


# =============================================================================
# AUDIO PROCESSING
# =============================================================================

class AudioProcessor:
    """Audio DSP operations."""
    
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
        """DSP pipeline for audio going TO OpenAI STT."""
        if not pcm_bytes or len(pcm_bytes) < 4:
            return pcm_bytes
        
        samples = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.float32)
        if samples.size == 0:
            return pcm_bytes
        
        # Volume boost
        samples *= VOLUME_BOOST_FACTOR
        
        # AGC
        rms = float(np.sqrt(np.mean(samples ** 2)))
        if rms > AGC_FLOOR_RMS:
            target_gain = float(np.clip(TARGET_RMS / rms, AGC_MIN_GAIN, AGC_MAX_GAIN))
            self.last_gain += AGC_SMOOTHING * (target_gain - self.last_gain)
            samples *= self.last_gain
        
        return np.clip(samples, -32768, 32767).astype(np.int16).tobytes()
    
    def process_outbound(self, ai_audio: bytes, from_rate: int, to_rate: int, 
                         to_codec: str) -> bytes:
        """Process audio coming FROM edge function TTS."""
        resampled = self.resample(ai_audio, from_rate, to_rate)
        
        if to_codec == "ulaw":
            return self.linear_to_ulaw(resampled)
        
        return resampled


# =============================================================================
# CALL STATE
# =============================================================================

@dataclass
class CallState:
    """Tracks all state for a single call session."""
    call_id: str
    phone: str = "Unknown"
    
    # Audio Format
    ast_codec: str = "ulaw"
    ast_rate: int = RATE_ULAW
    ast_frame_bytes: int = 160
    
    # OpenAI connection state
    openai_connected: bool = False
    openai_session_ready: bool = False
    
    # Conversation state
    transcripts: list = field(default_factory=list)
    pending_transcript: str = ""
    last_transcript_time: float = 0.0
    
    # Metrics
    frames_sent: int = 0
    frames_received: int = 0
    transcripts_processed: int = 0


# =============================================================================
# BRIDGE CLASS
# =============================================================================

class TaxiBridgeLocalSTT:
    """Bridge with local OpenAI STT and HTTP edge function calls."""
    
    VERSION = "1.0"
    
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.openai_ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running: bool = True
        
        self.state = CallState(call_id=f"local-stt-{int(time.time() * 1000)}")
        self.audio_processor = AudioProcessor()
        
        # Audio queue for TTS playback
        self.audio_queue: Deque[bytes] = deque(maxlen=AUDIO_QUEUE_MAXLEN)
        
        # HTTP session for edge function calls
        self.http_session: Optional[aiohttp.ClientSession] = None
    
    # -------------------------------------------------------------------------
    # OPENAI REALTIME CONNECTION
    # -------------------------------------------------------------------------
    
    async def connect_openai(self) -> bool:
        """Connect to OpenAI Realtime API for STT only."""
        if not OPENAI_API_KEY:
            logger.error("[%s] ❌ OPENAI_API_KEY not set", self.state.call_id)
            return False
        
        try:
            headers = {
                "Authorization": f"Bearer {OPENAI_API_KEY}",
                "OpenAI-Beta": "realtime=v1",
            }
            
            self.openai_ws = await asyncio.wait_for(
                websockets.connect(
                    OPENAI_REALTIME_URL,
                    additional_headers=headers,
                    ping_interval=20,
                    ping_timeout=20,
                ),
                timeout=10.0,
            )
            
            self.state.openai_connected = True
            logger.info("[%s] ✅ Connected to OpenAI Realtime", self.state.call_id)
            
            # Wait for session.created, then configure
            return True
            
        except Exception as e:
            logger.error("[%s] ❌ OpenAI connection failed: %s", self.state.call_id, e)
            return False
    
    async def configure_openai_session(self) -> None:
        """Configure OpenAI session for STT-only mode."""
        if not self.openai_ws:
            return
        
        # Configure for transcription only (no AI responses)
        config = {
            "type": "session.update",
            "session": {
                "modalities": ["text", "audio"],
                "instructions": "You are a transcription service. Listen to audio and transcribe it accurately. Do not respond or engage in conversation.",
                "voice": "shimmer",
                "input_audio_format": "pcm16",
                "output_audio_format": "pcm16",
                "input_audio_transcription": {
                    "model": "whisper-1"
                },
                "turn_detection": {
                    "type": "server_vad",
                    "threshold": 0.4,            # Lower = more sensitive to quiet speech
                    "prefix_padding_ms": 500,    # Capture more audio before speech starts
                    "silence_duration_ms": 1000, # Wait longer for complete sentences
                },
                # No tools - we're just transcribing
                "tools": [],
            }
        }
        
        await self.openai_ws.send(json.dumps(config))
        logger.info("[%s] 📋 OpenAI session configured for STT", self.state.call_id)
    
    # -------------------------------------------------------------------------
    # EDGE FUNCTION CALLS
    # -------------------------------------------------------------------------
    
    async def call_edge_function(self, transcript: str) -> Optional[bytes]:
        """
        Send transcript to edge function, receive TTS audio response.
        
        Returns:
            TTS audio as PCM16 @ 24kHz, or None on error
        """
        if not self.http_session:
            self.http_session = aiohttp.ClientSession()
        
        payload = {
            "call_id": self.state.call_id,
            "phone": self.state.phone,
            "text": transcript,
            "transcripts": self.state.transcripts,
        }
        
        try:
            async with self.http_session.post(
                EDGE_FUNCTION_URL,
                json=payload,
                timeout=aiohttp.ClientTimeout(total=30),
            ) as response:
                if response.status != 200:
                    logger.error("[%s] ❌ Edge function error: %d", 
                                self.state.call_id, response.status)
                    return None
                
                data = await response.json()
                
                # Store assistant response in transcripts
                if data.get("assistant_text"):
                    self.state.transcripts.append({
                        "role": "assistant",
                        "text": data["assistant_text"],
                    })
                    logger.info("[%s] 🤖 Ada: %s", self.state.call_id, data["assistant_text"])
                
                # Decode TTS audio
                if data.get("audio"):
                    return base64.b64decode(data["audio"])
                
                return None
                
        except Exception as e:
            logger.error("[%s] ❌ Edge function call failed: %s", self.state.call_id, e)
            return None
    
    # -------------------------------------------------------------------------
    # MAIN RUN LOOP
    # -------------------------------------------------------------------------
    
    async def run(self) -> None:
        """Main bridge loop."""
        peer = self.writer.get_extra_info("peername")
        logger.info("[%s] 📞 New call from %s", self.state.call_id, peer)
        
        # Connect to OpenAI
        if not await self.connect_openai():
            logger.error("[%s] ❌ Failed to connect to OpenAI", self.state.call_id)
            return
        
        # Start background tasks
        playback_task = asyncio.create_task(self.queue_to_asterisk())
        openai_task = asyncio.create_task(self.openai_to_edge())
        asterisk_task = asyncio.create_task(self.asterisk_to_openai())
        
        try:
            done, pending = await asyncio.wait(
                {playback_task, openai_task, asterisk_task},
                return_when=asyncio.FIRST_COMPLETED,
            )
            
            for t in pending:
                t.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
            
        finally:
            self.running = False
            await self.cleanup()
    
    async def cleanup(self) -> None:
        """Release all resources."""
        self.audio_queue.clear()
        
        if self.openai_ws:
            try:
                await self.openai_ws.close()
            except Exception:
                pass
        
        if self.http_session:
            await self.http_session.close()
        
        try:
            if not self.writer.is_closing():
                self.writer.close()
                await self.writer.wait_closed()
        except Exception:
            pass
        
        logger.info("[%s] 📊 Final: TX=%d RX=%d Transcripts=%d",
                   self.state.call_id, self.state.frames_sent, 
                   self.state.frames_received, self.state.transcripts_processed)
    
    # -------------------------------------------------------------------------
    # ASTERISK → OPENAI (audio)
    # -------------------------------------------------------------------------
    
    async def asterisk_to_openai(self) -> None:
        """Read audio from Asterisk and send to OpenAI for STT."""
        try:
            while self.running and self.state.openai_connected:
                try:
                    header = await asyncio.wait_for(
                        self.reader.readexactly(3),
                        timeout=ASTERISK_READ_TIMEOUT_S
                    )
                    
                    msg_type = header[0]
                    msg_len = struct.unpack(">H", header[1:3])[0]
                    payload = await self.reader.readexactly(msg_len)
                    
                    if msg_type == MSG_UUID:
                        raw_hex = payload.hex()
                        if len(raw_hex) >= 12:
                            self.state.phone = raw_hex[-12:]
                        logger.info("[%s] 👤 Phone: %s", self.state.call_id, self.state.phone)
                    
                    elif msg_type == MSG_AUDIO:
                        # Detect format
                        if msg_len == 640:
                            self.state.ast_codec = "slin16"
                            self.state.ast_rate = RATE_SLIN16
                        elif msg_len == 320:
                            self.state.ast_codec = "slin"
                            self.state.ast_rate = RATE_ULAW
                        elif msg_len == 160:
                            self.state.ast_codec = "ulaw"
                            self.state.ast_rate = RATE_ULAW
                        self.state.ast_frame_bytes = msg_len
                        
                        # Decode audio
                        if self.state.ast_codec == "ulaw":
                            linear = self.audio_processor.ulaw_to_linear(payload)
                        else:
                            linear = payload
                        
                        # Apply DSP and resample to 24kHz
                        processed = self.audio_processor.process_inbound(linear)
                        resampled = self.audio_processor.resample(
                            processed, self.state.ast_rate, RATE_AI
                        )
                        
                        # Send to OpenAI as base64
                        if self.openai_ws and self.state.openai_session_ready:
                            audio_b64 = base64.b64encode(resampled).decode()
                            await self.openai_ws.send(json.dumps({
                                "type": "input_audio_buffer.append",
                                "audio": audio_b64,
                            }))
                            self.state.frames_sent += 1
                    
                    elif msg_type == MSG_HANGUP:
                        logger.info("[%s] 📴 Hangup from Asterisk", self.state.call_id)
                        self.running = False
                        return
                
                except asyncio.TimeoutError:
                    logger.warning("[%s] ⏱️ Asterisk read timeout", self.state.call_id)
                    self.running = False
                    return
                except asyncio.IncompleteReadError:
                    logger.info("[%s] 📴 Asterisk connection closed", self.state.call_id)
                    self.running = False
                    return
                    
        except asyncio.CancelledError:
            pass
    
    # -------------------------------------------------------------------------
    # OPENAI → EDGE FUNCTION (transcript → TTS)
    # -------------------------------------------------------------------------
    
    async def openai_to_edge(self) -> None:
        """Receive transcripts from OpenAI and send to edge function."""
        try:
            async for message in self.openai_ws:
                if not self.running:
                    break
                
                data = json.loads(message)
                msg_type = data.get("type", "")
                
                # Session created - configure it
                if msg_type == "session.created":
                    await self.configure_openai_session()
                
                elif msg_type == "session.updated":
                    self.state.openai_session_ready = True
                    logger.info("[%s] ✅ OpenAI session ready", self.state.call_id)
                
                # Transcript from Whisper
                elif msg_type == "conversation.item.input_audio_transcription.completed":
                    transcript = data.get("transcript", "").strip()
                    if transcript:
                        logger.info("[%s] 🎤 User: %s", self.state.call_id, transcript)
                        
                        # Store user transcript
                        self.state.transcripts.append({
                            "role": "user",
                            "text": transcript,
                        })
                        self.state.transcripts_processed += 1
                        
                        # Call edge function to get AI response + TTS
                        tts_audio = await self.call_edge_function(transcript)
                        
                        if tts_audio:
                            # Process and queue TTS audio for playback
                            processed = self.audio_processor.process_outbound(
                                tts_audio, RATE_AI, self.state.ast_rate, self.state.ast_codec
                            )
                            
                            # Chunk into frames
                            frame_size = self.state.ast_frame_bytes
                            for i in range(0, len(processed), frame_size):
                                chunk = processed[i:i + frame_size]
                                if len(chunk) == frame_size:
                                    self.audio_queue.append(chunk)
                                    self.state.frames_received += 1
                
                elif msg_type == "error":
                    logger.error("[%s] 🧨 OpenAI error: %s", 
                                self.state.call_id, data.get("error", {}))
        
        except (ConnectionClosed, WebSocketException) as e:
            logger.warning("[%s] 🔌 OpenAI connection closed: %s", self.state.call_id, e)
            self.running = False
        except asyncio.CancelledError:
            pass
    
    # -------------------------------------------------------------------------
    # QUEUE → ASTERISK (TTS playback)
    # -------------------------------------------------------------------------
    
    async def queue_to_asterisk(self) -> None:
        """Stream TTS audio from queue to Asterisk."""
        start_time = time.time()
        bytes_played = 0
        
        try:
            while self.running:
                # Calculate pacing
                bytes_per_sec = max(1, self.state.ast_frame_bytes * 50)
                expected_time = start_time + (bytes_played / bytes_per_sec)
                delay = expected_time - time.time()
                if delay > 0:
                    await asyncio.sleep(delay)
                
                if not self.running:
                    break
                
                # Get next frame
                if self.audio_queue:
                    chunk = self.audio_queue.popleft()
                else:
                    # Silence frame
                    silence_byte = 0xFF if self.state.ast_codec == "ulaw" else 0x00
                    chunk = bytes([silence_byte]) * self.state.ast_frame_bytes
                
                # Send to Asterisk
                try:
                    frame = struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk
                    self.writer.write(frame)
                    await self.writer.drain()
                    bytes_played += len(chunk)
                except (BrokenPipeError, ConnectionResetError, OSError):
                    self.running = False
                    return
                    
        except asyncio.CancelledError:
            pass


# =============================================================================
# MAIN
# =============================================================================

async def main() -> None:
    """Start the bridge server."""
    if not OPENAI_API_KEY:
        logger.error("❌ OPENAI_API_KEY environment variable not set!")
        logger.error("   Set it with: export OPENAI_API_KEY=sk-...")
        return
    
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeLocalSTT(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    
    # Startup banner
    lines = [
        f"🚀 Taxi Bridge Local STT v{TaxiBridgeLocalSTT.VERSION}",
        f"   Listening: {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}",
        f"   OpenAI:    Realtime API (local STT)",
        f"   Edge:      {EDGE_FUNCTION_URL.split('/')[-1]}",
        f"   Flow:      Audio → OpenAI STT → Text → Edge → TTS → Audio",
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
