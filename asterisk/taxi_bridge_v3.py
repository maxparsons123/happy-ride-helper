#!/usr/bin/env python3
"""Taxi AI Asterisk Bridge (v3 - Production)

Combines the best of v2 with reliability improvements:
- Proper UUID parsing (ast-EPOCH-PHONE-NAME format)
- 200ms jitter buffer to prevent audio cutouts
- address_tts priority injection for accurate addresses
- Caller name extraction for personalized greetings
- Auto-detection of SLIN16 vs u-law codec
- Real-time pacing with silence frame injection on underflow

Does NOT drop user audio during AI speech (that breaks VAD).
For echo issues, use Asterisk-level AEC (DENOISE function).
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
import websockets

# --- Configuration ---
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://xsdlzoyaosfbbwzmcinq.supabase.co/functions/v1/taxi-realtime"

# Audio Settings
AST_RATE = 8000   # Asterisk standard
AI_RATE = 24000   # OpenAI Realtime API standard

# AudioSocket Message Types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

# Jitter buffer settings
JITTER_BUFFER_MS = 200  # Buffer this much audio before starting playback

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)


def resample_audio_linear16(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """Resample little-endian signed 16-bit mono PCM using linear interpolation."""
    if from_rate == to_rate:
        return audio_bytes
    if not audio_bytes:
        return b""

    audio_np = np.frombuffer(audio_bytes, dtype=np.int16)
    if audio_np.size == 0:
        return b""

    new_length = int(len(audio_np) * to_rate / from_rate)
    if new_length <= 0:
        return b""

    return np.interp(
        np.linspace(0, len(audio_np) - 1, new_length),
        np.arange(len(audio_np)),
        audio_np,
    ).astype(np.int16).tobytes()


class TaxiBridgeV3:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running = True

        self.audio_queue = deque()
        self.call_id = f"ast-{int(time.time() * 1000)}"
        self.phone = "Unknown"
        self.caller_name = ""

        # AudioSocket format detection (slin16 vs ulaw)
        self.ast_codec: str = "slin16"
        self.ast_frame_bytes: int = 320
        self._format_detected = False

    def _detect_ast_format_from_frame(self, frame_len_bytes: int) -> None:
        """Detect codec from first audio frame size."""
        if frame_len_bytes == 160:
            self.ast_codec = "ulaw"
            self.ast_frame_bytes = 160
        elif frame_len_bytes == 320:
            self.ast_codec = "slin16"
            self.ast_frame_bytes = 320
        else:
            # Unknown size - assume slin16 and use actual frame size
            self.ast_codec = "slin16"
            self.ast_frame_bytes = frame_len_bytes

        logger.info(
            f"[{self.call_id}] 🔎 Detected AudioSocket format: codec={self.ast_codec} frame_bytes={self.ast_frame_bytes}"
        )

    def _ast_in_to_linear16(self, payload: bytes) -> bytes:
        """Convert incoming Asterisk audio to linear16."""
        if self.ast_codec == "ulaw":
            return audioop.ulaw2lin(payload, 2)
        return payload

    def _linear16_to_ast_out(self, payload_linear16: bytes) -> bytes:
        """Convert linear16 to Asterisk output format."""
        if self.ast_codec == "ulaw":
            return audioop.lin2ulaw(payload_linear16, 2)
        return payload_linear16

    def _bytes_per_sec_out(self) -> int:
        """Bytes per second for output audio."""
        return AST_RATE * (1 if self.ast_codec == "ulaw" else 2)

    def _silence_frame(self) -> bytes:
        """Generate a silence frame in the correct codec."""
        if self.ast_codec == "ulaw":
            return b"\xFF" * self.ast_frame_bytes  # u-law silence
        return b"\x00" * self.ast_frame_bytes  # linear16 silence

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 New Call from {peer}")

        try:
            async with websockets.connect(WS_URL, ping_interval=5, ping_timeout=10) as ws:
                self.ws = ws

                # Initial handshake
                await self.ws.send(json.dumps({
                    "type": "init",
                    "call_id": self.call_id,
                    "addressTtsSplicing": True
                }))

                await asyncio.gather(
                    self.asterisk_to_ai(),
                    self.ai_to_queue(),
                    self.queue_to_asterisk(),
                )
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Bridge Error: {e}")
        finally:
            await self.cleanup()

    async def asterisk_to_ai(self):
        """Read audio/UUID from Asterisk and forward to AI backend."""
        while self.running:
            try:
                header = await self.reader.readexactly(3)
                m_type = header[0]
                m_len = struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    # Parse UUID format: ast-EPOCH-PHONE-NAME or ast-EPOCH-PHONE
                    # The payload is a UTF-8 string from Asterisk dialplan
                    uuid_str = payload.decode("utf-8", errors="ignore").strip("\x00")
                    parts = uuid_str.split("-")
                    
                    if len(parts) >= 3:
                        self.phone = parts[2] if parts[2] else "Unknown"
                    if len(parts) >= 4:
                        # Name might contain hyphens, so join everything after index 3
                        self.caller_name = "-".join(parts[3:])

                    logger.info(
                        f"[{self.call_id}] 👤 Caller: {self.caller_name or 'New'} ({self.phone})"
                    )

                    # Send updated init with caller info
                    await self.ws.send(json.dumps({
                        "type": "init",
                        "call_id": self.call_id,
                        "user_phone": self.phone,
                        "user_name": self.caller_name,
                        "addressTtsSplicing": True,
                    }))

                elif m_type == MSG_AUDIO:
                    # Detect format on first audio frame
                    if not self._format_detected:
                        self._format_detected = True
                        self._detect_ast_format_from_frame(m_len)

                    # Convert to linear16 and upsample to 24kHz for AI
                    linear16 = self._ast_in_to_linear16(payload)
                    upsampled = resample_audio_linear16(linear16, AST_RATE, AI_RATE)

                    # Send to AI (do NOT drop audio during AI speech - breaks VAD)
                    await self.ws.send(json.dumps({
                        "type": "audio",
                        "audio": base64.b64encode(upsampled).decode()
                    }))

                elif m_type == MSG_HANGUP:
                    logger.info(f"[{self.call_id}] 👋 Asterisk hung up")
                    break

            except asyncio.IncompleteReadError:
                logger.info(f"[{self.call_id}] 📴 Connection closed by Asterisk")
                break
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ asterisk_to_ai error: {e}")
                break

        self.running = False

    async def ai_to_queue(self):
        """Receive AI responses and queue audio for playback."""
        audio_chunks_received = 0
        try:
            async for message in self.ws:
                data = json.loads(message)

                if data.get("type") == "audio":
                    raw_audio_24k = base64.b64decode(data["audio"])
                    audio_chunks_received += 1

                    # Log first few chunks for debugging
                    if audio_chunks_received <= 3:
                        logger.info(
                            f"[{self.call_id}] 🔊 AI audio chunk #{audio_chunks_received}: "
                            f"24k={len(raw_audio_24k)}B"
                        )

                    # Downsample to Asterisk rate and convert codec
                    linear16_8k = resample_audio_linear16(raw_audio_24k, AI_RATE, AST_RATE)
                    out_bytes = self._linear16_to_ast_out(linear16_8k)
                    
                    if out_bytes:
                        self.audio_queue.append(out_bytes)

                elif data.get("type") == "address_tts":
                    # High-fidelity address audio - insert at FRONT of queue for priority
                    raw_audio_24k = base64.b64decode(data["audio"])
                    linear16_8k = resample_audio_linear16(raw_audio_24k, AI_RATE, AST_RATE)
                    out_bytes = self._linear16_to_ast_out(linear16_8k)
                    
                    if out_bytes:
                        self.audio_queue.appendleft(out_bytes)
                        logger.info(
                            f"[{self.call_id}] 🎯 Queued address_tts: {len(out_bytes)}B (priority)"
                        )

                elif data.get("type") == "transcript":
                    role = data.get("role", "?")
                    text = data.get("text", "")
                    logger.info(f"[{self.call_id}] 💬 {role}: {text}")

                elif data.get("type") == "session_ready":
                    logger.info(f"[{self.call_id}] ✅ AI session ready")

                elif data.get("type") == "ai_speaking":
                    speaking = data.get("speaking", False)
                    logger.debug(f"[{self.call_id}] 🎤 AI speaking: {speaking}")

        except websockets.exceptions.ConnectionClosed:
            logger.info(f"[{self.call_id}] 📡 WebSocket closed")
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ ai_to_queue error: {e}")

    async def queue_to_asterisk(self):
        """Pump audio to Asterisk at real-time pace with jitter buffer.

        IMPORTANT:
        - Audio from the AI arrives in network bursts.
        - Small network jitter can cause buffer underflows = audio "cutting out".
        
        Solution: Start in "buffering" mode, send silence until we have enough
        audio queued, then start playback. Re-buffer on underflow.
        """
        bytes_per_sec = self._bytes_per_sec_out()
        start_time = time.time()
        bytes_played = 0

        # Calculate minimum buffer size
        min_buffer_bytes = int(bytes_per_sec * (JITTER_BUFFER_MS / 1000.0))
        min_buffer_bytes = max(min_buffer_bytes, self.ast_frame_bytes * 5)

        buffering = True
        underflow_count = 0
        last_stats_log = 0.0

        buffer = bytearray()

        while self.running:
            # Drain queue into local buffer
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

            # Periodic stats logging
            now = time.time()
            if now - last_stats_log >= 10:
                last_stats_log = now
                logger.info(
                    f"[{self.call_id}] 🎧 buffer={len(buffer)}B queue={len(self.audio_queue)} "
                    f"buffering={buffering} underflows={underflow_count}"
                )

            # Exit buffering mode when we have enough audio
            if buffering and len(buffer) >= min_buffer_bytes:
                buffering = False
                logger.info(
                    f"[{self.call_id}] ✅ Jitter buffer filled ({len(buffer)}B). Starting playback."
                )

            # Timing control - maintain real-time pace
            expected_time = start_time + (bytes_played / bytes_per_sec)
            if now < expected_time:
                await asyncio.sleep(expected_time - now)

            # Choose next chunk to send
            if (not buffering) and len(buffer) >= self.ast_frame_bytes:
                chunk = bytes(buffer[:self.ast_frame_bytes])
                del buffer[:self.ast_frame_bytes]
            else:
                # Buffer underflow - send silence and re-enter buffering mode
                if not buffering and len(buffer) < self.ast_frame_bytes:
                    buffering = True
                    underflow_count += 1
                    logger.info(
                        f"[{self.call_id}] ⚠️ Buffer underflow #{underflow_count}. Re-buffering..."
                    )
                chunk = self._silence_frame()

            # Send to Asterisk
            try:
                header = struct.pack(">BH", MSG_AUDIO, len(chunk))
                self.writer.write(header + chunk)
                await self.writer.drain()
                bytes_played += len(chunk)
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ Write to Asterisk failed: {e}")
                break

    async def cleanup(self):
        """Clean up resources."""
        self.running = False
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except Exception:
            pass
        logger.info(f"[{self.call_id}] 📴 Call ended. Stats: phone={self.phone}")


async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV3(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    logger.info(f"🚀 Taxi Bridge v3 Online on port {AUDIOSOCKET_PORT}")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
