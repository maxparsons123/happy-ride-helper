#!/usr/bin/env python3
"""
Asterisk AudioSocket to Taxi AI WebSocket Bridge

Bridges Asterisk's AudioSocket protocol (8kHz) to the taxi-realtime 
Supabase Edge Function (24kHz) with real-time resampling.
"""

import asyncio
import json
import struct
import base64
import time
import logging
import numpy as np
import websockets
from typing import Optional
from collections import deque

# --- Configuration ---
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://xsdlzoyaosfbbwzmcinq.supabase.co/functions/v1/taxi-realtime"

# Audio Settings
AST_RATE = 8000   # Asterisk standard
AI_RATE = 24000   # AI Engine standard
CHUNK_SIZE = 320  # 20ms of audio at 8kHz (Asterisk default frame size)

# AudioSocket Message Types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

logging.basicConfig(level=logging.INFO, format='%(asctime)s [%(levelname)s] %(message)s')
logger = logging.getLogger(__name__)


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """Uses linear interpolation to change sample rates."""
    if from_rate == to_rate:
        return audio_bytes
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16)
    new_length = int(len(audio_np) * to_rate / from_rate)
    return np.interp(
        np.linspace(0, len(audio_np) - 1, new_length),
        np.arange(len(audio_np)),
        audio_np
    ).astype(np.int16).tobytes()


class TaxiBridge:
    def __init__(self, reader, writer):
        self.reader = reader
        self.writer = writer
        self.ws = None
        self.running = True
        self.audio_queue = deque()
        self.call_id = f"ast-{int(time.time() * 1000)}"
        self.phone = "Unknown"

    async def run(self):
        peer = self.writer.get_extra_info('peername')
        logger.info(f"[{self.call_id}] 📞 New Call from {peer}")
        
        try:
            async with websockets.connect(WS_URL, ping_interval=5, ping_timeout=10) as ws:
                self.ws = ws
                # Initial handshake
                await self.ws.send(json.dumps({"type": "init", "call_id": self.call_id, "addressTtsSplicing": True}))
                
                # Start concurrent loops
                await asyncio.gather(
                    self.asterisk_to_ai(),
                    self.ai_to_queue(),
                    self.queue_to_asterisk()
                )
        except Exception as e:
            logger.error(f"[{self.call_id}] ❌ Bridge Error: {e}")
        finally:
            await self.cleanup()

    async def asterisk_to_ai(self):
        """Receives audio/UUID from Asterisk and sends to Supabase"""
        while self.running:
            try:
                header = await self.reader.readexactly(3)
                m_type = header[0]
                m_len = struct.unpack('>H', header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    uuid_str = payload.decode('utf-8').strip('\x00')
                    # Extract phone from UUID (e.g., 0000-0000-447466668108)
                    if "-" in uuid_str:
                        self.phone = uuid_str.split("-")[-1]
                    
                    logger.info(f"[{self.call_id}] 👤 Identified Phone: {self.phone}")
                    await self.ws.send(json.dumps({
                        "type": "init", 
                        "call_id": self.call_id, 
                        "user_phone": self.phone,
                        "addressTtsSplicing": True
                    }))

                elif m_type == MSG_AUDIO:
                    upsampled = resample_audio(payload, AST_RATE, AI_RATE)
                    await self.ws.send(json.dumps({
                        "type": "audio", 
                        "audio": base64.b64encode(upsampled).decode()
                    }))

                elif m_type == MSG_HANGUP:
                    logger.info(f"[{self.call_id}] 👋 Asterisk hung up")
                    break

            except Exception:
                break
        self.running = False

    async def ai_to_queue(self):
        """Receives AI response and puts audio in the buffer"""
        try:
            async for message in self.ws:
                data = json.loads(message)
                if data.get("type") == "audio":
                    raw_audio = base64.b64decode(data["audio"])
                    # Downsample AI (24k) to Asterisk (8k)
                    downsampled = resample_audio(raw_audio, AI_RATE, AST_RATE)
                    self.audio_queue.append(downsampled)
                elif data.get("type") == "address_tts":
                    # High-fidelity address audio - prioritize it in the queue
                    raw_audio = base64.b64decode(data["audio"])
                    downsampled = resample_audio(raw_audio, AI_RATE, AST_RATE)
                    self.audio_queue.appendleft(downsampled)
                    logger.info(f"[{self.call_id}] 🔊 Queued address_tts ({len(downsampled)} bytes)")
                elif data.get("type") == "transcript":
                    logger.info(f"[{self.call_id}] 💬 {data.get('role')}: {data.get('text')}")
        except Exception:
            pass

    async def queue_to_asterisk(self):
        """Pumps audio to Asterisk at a strict 8kHz pace.

        If we don't have enough AI audio yet, we send silence frames to keep Asterisk's
        timing stable (prevents dead-air when the buffer underflows).
        """
        bytes_per_sec = AST_RATE * 2  # 16-bit mono PCM
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()
        silence = b"\x00" * CHUNK_SIZE

        while self.running:
            # Drain queued audio into the local buffer
            while self.audio_queue:
                buffer.extend(self.audio_queue.popleft())

            # Timing control
            expected_time = start_time + (bytes_played / bytes_per_sec)
            now = time.time()
            if now < expected_time:
                await asyncio.sleep(expected_time - now)

            # Send real audio if available, otherwise send silence
            if len(buffer) >= CHUNK_SIZE:
                chunk = bytes(buffer[:CHUNK_SIZE])
                del buffer[:CHUNK_SIZE]
            else:
                chunk = silence

            try:
                header = struct.pack('>BH', MSG_AUDIO, len(chunk))
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
        except:
            pass
        logger.info(f"[{self.call_id}] 📴 Disconnected")


async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridge(r, w).run(), 
        AUDIOSOCKET_HOST, 
        AUDIOSOCKET_PORT
    )
    logger.info(f"🚀 Taxi Bridge Online on port {AUDIOSOCKET_PORT}")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
