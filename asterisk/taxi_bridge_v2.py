#!/usr/bin/env python3
"""Taxi AI Asterisk Bridge (v2)

Purpose: make sure you can always hear the bot on the phone by:
- Auto-detecting whether AudioSocket is sending SLIN16 (16-bit) or u-law (8-bit)
- Converting u-law <-> linear when needed
- Pacing outbound audio at real-time speed
- Sending silence frames when the AI buffer underflows (keeps AudioSocket timing stable)

This file is intended to *replace* taxi_bridge.py on your Asterisk server.
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
AI_RATE = 24000   # AI Engine standard

# AudioSocket Message Types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

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


class TaxiBridgeV2:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.running = True

        self.audio_queue = deque()
        self.call_id = f"ast-{int(time.time() * 1000)}"
        self.phone = "Unknown"
        self.caller_name = ""

        # AudioSocket format detection
        # - slin16: 16-bit linear PCM, typical frames are 320 bytes (20ms)
        # - ulaw: 8-bit u-law, typical frames are 160 bytes (20ms)
        self.ast_codec: str = "slin16"  # default assumption
        self.ast_frame_bytes: int = 320  # default assumption

    def _detect_ast_format_from_frame(self, frame_len_bytes: int) -> None:
        if frame_len_bytes == 160:
            self.ast_codec = "ulaw"
            self.ast_frame_bytes = 160
        elif frame_len_bytes == 320:
            self.ast_codec = "slin16"
            self.ast_frame_bytes = 320
        else:
            self.ast_codec = "slin16"
            self.ast_frame_bytes = frame_len_bytes

        logger.info(
            f"[{self.call_id}] 🔎 Detected AudioSocket format: codec={self.ast_codec} frame_bytes={self.ast_frame_bytes}"
        )

    def _ast_in_to_linear16(self, payload: bytes) -> bytes:
        if self.ast_codec == "ulaw":
            return audioop.ulaw2lin(payload, 2)
        return payload

    def _linear16_to_ast_out(self, payload_linear16: bytes) -> bytes:
        if self.ast_codec == "ulaw":
            return audioop.lin2ulaw(payload_linear16, 2)
        return payload_linear16

    def _bytes_per_sec_out(self) -> int:
        return AST_RATE * (1 if self.ast_codec == "ulaw" else 2)

    def _silence_frame(self) -> bytes:
        if self.ast_codec == "ulaw":
            return b"\xFF" * self.ast_frame_bytes
        return b"\x00" * self.ast_frame_bytes

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 New Call from {peer}")

        try:
            async with websockets.connect(WS_URL, ping_interval=5, ping_timeout=10) as ws:
                self.ws = ws

                # Initial handshake (call_id only). Phone gets sent when UUID arrives.
                await self.ws.send(json.dumps({"type": "init", "call_id": self.call_id}))

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
        """Receives audio/UUID from Asterisk and sends to the realtime backend."""
        while self.running:
            try:
                header = await self.reader.readexactly(3)
                m_type = header[0]
                m_len = struct.unpack(">H", header[1:3]
                )[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    uuid_str = payload.decode("utf-8", errors="ignore").strip("\x00")
                    # UUID format: ast-EPOCH-PHONE-NAME or ast-EPOCH-PHONE
                    parts = uuid_str.split("-")
                    if len(parts) >= 3:
                        self.phone = parts[2] if len(parts) > 2 else "Unknown"
                    if len(parts) >= 4:
                        self.caller_name = "-".join(parts[3:])  # Name might have hyphens

                    logger.info(f"[{self.call_id}] 👤 Caller: {self.caller_name or 'Unknown'} ({self.phone})")
                    await self.ws.send(
                        json.dumps(
                            {
                                "type": "init",
                                "call_id": self.call_id,
                                "user_phone": self.phone,
                                "user_name": self.caller_name,
                            }
                        )
                    )

                elif m_type == MSG_AUDIO:
                    # Detect format from the *first* audio frame we see
                    if not hasattr(self, '_format_detected'):
                        self._format_detected = True
                        self._detect_ast_format_from_frame(m_len)

                    linear16 = self._ast_in_to_linear16(payload)
                    upsampled = resample_audio_linear16(linear16, AST_RATE, AI_RATE)

                    await self.ws.send(
                        json.dumps(
                            {
                                "type": "audio",
                                "audio": base64.b64encode(upsampled).decode(),
                            }
                        )
                    )

                elif m_type == MSG_HANGUP:
                    logger.info(f"[{self.call_id}] 👋 Asterisk hung up")
                    break

            except Exception:
                break

        self.running = False

    async def ai_to_queue(self):
        """Receives AI responses and buffers audio for outbound playback."""
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

                    # Ensure we are working in linear16 at AST_RATE
                    linear16_8k = resample_audio_linear16(raw_audio_24k, AI_RATE, AST_RATE)

                    # Convert to whatever Asterisk expects on output (ulaw or slin16)
                    out_bytes = self._linear16_to_ast_out(linear16_8k)
                    if out_bytes:
                        self.audio_queue.append(out_bytes)

                        if audio_chunks_received <= 3:
                            logger.info(
                                f"[{self.call_id}] 📤 Queued for Asterisk: {len(out_bytes)}B "
                                f"(codec={self.ast_codec})"
                            )

                elif data.get("type") == "transcript":
                    logger.info(
                        f"[{self.call_id}] 💬 {data.get('role')}: {data.get('text')}"
                    )
        except Exception:
            pass

    async def queue_to_asterisk(self):
        """Pumps audio to Asterisk at a strict real-time pace.

        Sends silence when we don't have enough AI audio yet to avoid timing collapse.
        """
        bytes_per_sec = self._bytes_per_sec_out()
        start_time = time.time()
        bytes_played = 0

        buffer = bytearray()

        while self.running:
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

            # Timing control
            expected_time = start_time + (bytes_played / bytes_per_sec)
            now = time.time()
            if now < expected_time:
                await asyncio.sleep(expected_time - now)

            # Choose next chunk
            if len(buffer) >= self.ast_frame_bytes:
                chunk = bytes(buffer[: self.ast_frame_bytes])
                del buffer[: self.ast_frame_bytes]
            else:
                chunk = self._silence_frame()

            try:
                header = struct.pack(">BH", MSG_AUDIO, len(chunk))
                self.writer.write(header + chunk)
                await self.writer.drain()
                bytes_played += len(chunk)
            except Exception:
                break

    async def cleanup(self):
        self.running = False
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except Exception:
            pass
        logger.info(f"[{self.call_id}] 📴 Disconnected")


async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV2(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    logger.info(f"🚀 Taxi Bridge v2 Online on port {AUDIOSOCKET_PORT}")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
