#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge (v4.4 - Binary UUID & Immediate Playback Fix)
- Fix: Uses binary hex parsing to match Asterisk's internal 16-byte UUID.
- Stability: Starts playback task immediately to prevent Asterisk timeout.
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
WS_URL = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime"

AST_RATE = 8000
AI_RATE = 24000
JITTER_BUFFER_MS = 300

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
        self.caller_name = "Guest"
        self.buffering = True
        self.ast_codec = "slin16"
        self.ast_frame_bytes = 320

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        logger.info(f"[{self.call_id}] 📞 Socket connected from {peer}")

        # FIX: Start the playback loop immediately to keep Asterisk alive
        playback_task = asyncio.create_task(self.queue_to_asterisk())

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
                )
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Error: {e}")
        finally:
            self.running = False
            playback_task.cancel()
            await self.cleanup()

    async def asterisk_to_ai(self):
        while self.running:
            try:
                header = await self.reader.readexactly(3)
                m_type = header[0]
                m_len = struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    # FIX: Handle raw 16-byte binary UUID
                    raw_uuid_hex = payload.hex()
                    logger.info(f"[{self.call_id}] 🔑 UUID Received (Hex): {raw_uuid_hex}")

                    if len(raw_uuid_hex) >= 12:
                        self.phone = raw_uuid_hex[-12:]

                    logger.info(f"[{self.call_id}] 👤 Phone Extracted: {self.phone}")

                    await self.ws.send(json.dumps({
                        "type": "init",
                        "call_id": self.call_id,
                        "user_phone": self.phone,
                        "addressTtsSplicing": True
                    }))

                elif m_type == MSG_AUDIO:
                    if self.ast_frame_bytes != m_len:
                        self.ast_codec = "ulaw" if m_len == 160 else "slin16"
                        self.ast_frame_bytes = m_len

                    pcm = audioop.ulaw2lin(payload, 2) if self.ast_codec == "ulaw" else payload
                    upsampled = self._resample(pcm, AST_RATE, AI_RATE)

                    if self.ws and self.ws.open:
                        await self.ws.send(json.dumps({
                            "type": "audio",
                            "audio": base64.b64encode(upsampled).decode()
                        }))

                elif m_type == MSG_HANGUP:
                    break

            except Exception:
                break

    async def ai_to_queue(self):
        try:
            async for message in self.ws:
                data = json.loads(message)
                if data.get("type") in ["audio", "address_tts"]:
                    raw_24k = base64.b64decode(data["audio"])
                    pcm_8k = self._resample(raw_24k, AI_RATE, AST_RATE)
                    out = audioop.lin2ulaw(pcm_8k, 2) if self.ast_codec == "ulaw" else pcm_8k
                    self.audio_queue.append(out)
                elif data.get("type") == "transcript":
                    logger.info(f"[{self.call_id}] 💬 {data.get('role').upper()}: {data.get('text')}")
                elif data.get("type") == "call_ended":
                    logger.info(f"[{self.call_id}] 📴 AI requested hangup: {data.get('reason', 'unknown')}")
                    # Send hangup message to Asterisk
                    await self._send_hangup()
                    self.running = False
                    break
        except Exception:
            pass

    async def _send_hangup(self):
        """Send hangup message to Asterisk"""
        try:
            # MSG_HANGUP = 0x00, with 0 length payload
            header = struct.pack(">BH", MSG_HANGUP, 0)
            self.writer.write(header)
            await self.writer.drain()
            logger.info(f"[{self.call_id}] 📴 Hangup sent to Asterisk")
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Failed to send hangup: {e}")

    async def queue_to_asterisk(self):
        bytes_per_sec = AST_RATE * (1 if self.ast_codec == "ulaw" else 2)
        frame_time = self.ast_frame_bytes / bytes_per_sec
        bytes_played = 0
        start_time = time.time()

        while self.running:
            if self.buffering and len(self.audio_queue) < 5:
                frame = self._silence()
            else:
                if self.buffering:
                    self.buffering = False
                    logger.info(f"[{self.call_id}] 🔊 Playing AI Audio")

                if self.audio_queue:
                    frame = self.audio_queue.popleft()
                else:
                    frame = self._silence()

            await self._send_frame(frame)
            bytes_played += len(frame)

            # Pacing
            expected_time = start_time + (bytes_played / bytes_per_sec)
            wait_time = expected_time - time.time()
            if wait_time > 0:
                await asyncio.sleep(wait_time)

    async def _send_frame(self, frame):
        try:
            header = struct.pack(">BH", MSG_AUDIO, len(frame))
            self.writer.write(header + frame)
            await self.writer.drain()
        except:
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
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except:
            pass
        logger.info(f"[{self.call_id}] 📴 Call Finished")


async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeV4(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT
    )
    logger.info(f"🚀 Taxi Bridge v4.4 Online")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
