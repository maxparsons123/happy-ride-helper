#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge (v4 - Production)

- 200ms Jitter Buffer (prevents 'cutting out')
- Forwards ALL user audio (OpenAI VAD handles barge-in)
- Caller name extraction for personalized greetings
- Priority address_tts injection
- Proper error logging
"""

import asyncio
import json
import struct
import base64
import time
import logging
import audioop
from collections import deque

import numpy as np
import websockets

# --- Configuration ---
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://xsdlzoyaosfbbwzmcinq.supabase.co/functions/v1/taxi-realtime"

AST_RATE = 8000
AI_RATE = 24000
JITTER_BUFFER_MS = 200

MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)


class TaxiBridgeV4:
    def __init__(self, reader, writer):
        self.reader = reader
        self.writer = writer
        self.ws = None
        self.running = True
        self.audio_queue = deque()
        self.call_id = f"ast-{int(time.time() * 1000)}"
        self.phone = "Unknown"
        self.caller_name = ""
        self.buffering = True
        self.ast_codec = "slin16"
        self.ast_frame_bytes = 320

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 Call from {peer}")
        try:
            async with websockets.connect(WS_URL, ping_interval=5, ping_timeout=10) as ws:
                self.ws = ws
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
            logger.error(f"[{self.call_id}] ❌ Bridge error: {e}")
        finally:
            await self.cleanup()

    async def asterisk_to_ai(self):
        """Forward ALL user audio to AI. OpenAI VAD handles barge-in."""
        while self.running:
            try:
                header = await self.reader.readexactly(3)
                m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    # Parse: ast-EPOCH-PHONE-NAME or fallback to hex
                    raw_str = payload.decode("utf-8", errors="ignore").strip("\x00")
                    if "-" in raw_str:
                        parts = raw_str.split("-")
                        self.phone = parts[2] if len(parts) >= 3 else parts[-1]
                        if len(parts) >= 4:
                            self.caller_name = "-".join(parts[3:])
                    else:
                        self.phone = payload.hex()[-12:]

                    logger.info(f"[{self.call_id}] 👤 Caller: {self.caller_name or 'New'} ({self.phone})")
                    await self.ws.send(json.dumps({
                        "type": "init",
                        "call_id": self.call_id,
                        "user_phone": self.phone,
                        "user_name": self.caller_name,
                        "addressTtsSplicing": True
                    }))

                elif m_type == MSG_AUDIO:
                    if self.ast_frame_bytes != m_len:
                        self.ast_codec = "ulaw" if m_len == 160 else "slin16"
                        self.ast_frame_bytes = m_len
                        logger.info(f"[{self.call_id}] 🔎 Format: {self.ast_codec} ({m_len}B)")

                    pcm = audioop.ulaw2lin(payload, 2) if self.ast_codec == "ulaw" else payload
                    upsampled = self._resample(pcm, AST_RATE, AI_RATE)
                    await self.ws.send(json.dumps({
                        "type": "audio",
                        "audio": base64.b64encode(upsampled).decode()
                    }))

                elif m_type == MSG_HANGUP:
                    logger.info(f"[{self.call_id}] 👋 Hangup received")
                    break

            except asyncio.IncompleteReadError:
                logger.info(f"[{self.call_id}] 📴 Connection closed")
                break
            except Exception as e:
                logger.error(f"[{self.call_id}] ❌ asterisk_to_ai: {e}")
                break
        self.running = False

    async def ai_to_queue(self):
        """Receive AI audio and queue for playback."""
        try:
            async for message in self.ws:
                data = json.loads(message)

                if data.get("type") == "audio":
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = self._resample(raw_24k, AI_RATE, AST_RATE)
                    out = audioop.lin2ulaw(pcm_8k, 2) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)

                elif data.get("type") == "address_tts":
                    # Priority insert at FRONT of queue (don't clear!)
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = self._resample(raw_24k, AI_RATE, AST_RATE)
                    out = audioop.lin2ulaw(pcm_8k, 2) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.appendleft(out)
                    logger.info(f"[{self.call_id}] 📍 Address TTS injected ({len(out)}B)")

                elif data.get("type") == "transcript":
                    logger.info(f"[{self.call_id}] 💬 {data.get('role')}: {data.get('text')}")

        except websockets.exceptions.ConnectionClosed:
            logger.info(f"[{self.call_id}] 📡 WebSocket closed")
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ ai_to_queue: {e}")

    async def queue_to_asterisk(self):
        """Smooth playback with jitter buffer."""
        bytes_per_sec = AST_RATE * (1 if self.ast_codec == "ulaw" else 2)
        frame_time = self.ast_frame_bytes / bytes_per_sec
        required_frames = max(int(JITTER_BUFFER_MS / (frame_time * 1000)), 5)

        bytes_played = 0
        start_time = time.time()
        underflow_count = 0

        while self.running:
            # Jitter buffer: wait until we have enough frames
            if self.buffering:
                if len(self.audio_queue) >= required_frames:
                    self.buffering = False
                    logger.info(f"[{self.call_id}] ✅ Buffer ready ({len(self.audio_queue)} frames)")
                else:
                    await self._send_frame(self._silence())
                    await asyncio.sleep(frame_time)
                    continue

            if self.audio_queue:
                frame = self.audio_queue.popleft()
                await self._send_frame(frame)
                bytes_played += len(frame)
            else:
                # Underflow - re-buffer
                self.buffering = True
                underflow_count += 1
                logger.warning(f"[{self.call_id}] ⚠️ Underflow #{underflow_count}")
                continue

            # Maintain real-time pace
            expected = start_time + (bytes_played / bytes_per_sec)
            wait = expected - time.time()
            if wait > 0:
                await asyncio.sleep(wait)

    async def _send_frame(self, frame):
        try:
            header = struct.pack(">BH", MSG_AUDIO, len(frame))
            self.writer.write(header + frame)
            await self.writer.drain()
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Send failed: {e}")
            self.running = False

    def _resample(self, data, fin, fout):
        if fin == fout or not data:
            return data
        n = np.frombuffer(data, dtype=np.int16)
        if n.size == 0:
            return b""
        new_len = int(len(n) * fout / fin)
        return np.interp(np.linspace(0, len(n) - 1, new_len), np.arange(len(n)), n).astype(np.int16).tobytes()

    def _silence(self):
        return (b"\xFF" if self.ast_codec == "ulaw" else b"\x00") * self.ast_frame_bytes

    async def cleanup(self):
        self.running = False
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except Exception:
            pass
        logger.info(f"[{self.call_id}] 📴 Call ended ({self.phone})")


async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV4(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    logger.info(f"🚀 Taxi Bridge v4 Online (port {AUDIOSOCKET_PORT})")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
