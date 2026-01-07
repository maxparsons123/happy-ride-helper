#!/usr/bin/env python3
"""
Asterisk AudioSocket to Taxi AI WebSocket Bridge

Bridges Asterisk's AudioSocket protocol (8kHz) to the taxi-realtime 
Supabase Edge Function (24kHz) with real-time resampling.

Usage:
    python taxi_bridge.py

Requirements:
    pip install websockets numpy

Asterisk dialplan example:
    exten => 100,1,Answer()
    same => n,AudioSocket(127.0.0.1:9092)
    same => n,Hangup()
"""

import asyncio
import json
import struct
import base64
import time
import logging
import socket as pysocket
from typing import Optional
from collections import deque

try:
    import websockets
except ImportError:
    print("Please install websockets: pip install websockets")
    exit(1)

try:
    import numpy as np
except ImportError:
    print("Please install numpy: pip install numpy")
    exit(1)

# Configuration
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://xsdlzoyaosfbbwzmcinq.supabase.co/functions/v1/taxi-realtime"

# Audio settings
AST_RATE = 8000   # Asterisk sample rate (standard telephony)
AI_RATE = 24000   # OpenAI Realtime API sample rate

# AudioSocket message types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10
MSG_ERROR = 0xFF

# Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s'
)
logger = logging.getLogger(__name__)


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """Resample audio from one sample rate to another using linear interpolation."""
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16)
    
    if from_rate == to_rate:
        return audio_bytes
    
    # Calculate new length
    new_length = int(len(audio_np) * to_rate / from_rate)
    
    # Linear interpolation resampling
    old_indices = np.arange(len(audio_np))
    new_indices = np.linspace(0, len(audio_np) - 1, new_length)
    resampled = np.interp(new_indices, old_indices, audio_np).astype(np.int16)
    
    return resampled.tobytes()


class TaxiBridge:
    """Bridges Asterisk AudioSocket to taxi-realtime WebSocket with resampling."""
    
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.reader = reader
        self.writer = writer
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.call_uuid: Optional[str] = None
        self.running = False
        self.call_start_time = time.time()
        self.client_addr = writer.get_extra_info('peername')

        # Reduce latency for small audio frames
        sock = writer.get_extra_info("socket")
        try:
            if sock is not None:
                sock.setsockopt(pysocket.IPPROTO_TCP, pysocket.TCP_NODELAY, 1)
                sock.setsockopt(pysocket.SOL_SOCKET, pysocket.SO_KEEPALIVE, 1)
        except Exception as e:
            logger.warning(f"Could not set socket options: {e}")

        # Audio playback queue for smoother delivery
        self.audio_queue: deque = deque()
        self.audio_bytes_sent = 0
        self.audio_chunks_received = 0

    async def run(self):
        """Main handler for a single call."""
        logger.info(f"New connection from {self.client_addr}")
        self.running = True
        
        try:
            # Connect to taxi-realtime WebSocket
            logger.info("Connecting to taxi-realtime WebSocket...")
            async with websockets.connect(WS_URL) as ws:
                self.ws = ws
                logger.info("Connected to taxi-realtime")
                
                # Initialize session
                call_id = f"ast-{int(time.time())}"
                await ws.send(json.dumps({
                    "type": "init",
                    "call_id": call_id
                }))
                logger.info(f"Session initialized: {call_id}")
                
                # Run three tasks concurrently:
                # 1. Asterisk -> AI (send caller audio)
                # 2. AI -> Queue (receive AI audio into queue)  
                # 3. Queue -> Asterisk (play audio to caller)
                await asyncio.gather(
                    self.asterisk_to_ai(),
                    self.ai_to_queue(),
                    self.queue_to_asterisk()
                )
                
        except Exception as e:
            logger.error(f"Error in call handler: {e}")
        finally:
            await self.cleanup()
            
    async def asterisk_to_ai(self):
        """Receive audio from Asterisk (8kHz), upsample to 24kHz, send to AI."""
        audio_buffer = bytearray()
        min_bytes = int(AST_RATE * 0.1 * 2)  # 100ms of 8kHz 16-bit audio
        
        while self.running:
            try:
                # Read AudioSocket frame header (3 bytes: type + length)
                header = await self.reader.readexactly(3)
                
                msg_type = header[0]
                payload_len = struct.unpack('>H', header[1:3])[0]
                
                # Read payload
                payload = await self.reader.readexactly(payload_len)
                
                # Handle message types
                if msg_type == MSG_HANGUP:
                    logger.info("Received hangup from Asterisk")
                    self.running = False
                    if self.ws:
                        await self.ws.send(json.dumps({"type": "hangup"}))
                    break
                    
                elif msg_type == MSG_UUID:
                    # AudioSocket UUID is typically 16 raw bytes (binary UUID)
                    self.call_uuid = payload.hex()
                    logger.info(f"Call UUID: {self.call_uuid}")

                elif msg_type == MSG_AUDIO:
                    # Buffer audio and send in chunks
                    audio_buffer.extend(payload)
                    
                    while len(audio_buffer) >= min_bytes:
                        chunk = bytes(audio_buffer[:min_bytes])
                        audio_buffer = audio_buffer[min_bytes:]
                        
                        # Upsample 8kHz -> 24kHz for AI
                        upsampled = resample_audio(chunk, AST_RATE, AI_RATE)
                        audio_b64 = base64.b64encode(upsampled).decode('utf-8')
                        
                        if self.ws:
                            await self.ws.send(json.dumps({
                                "type": "audio",
                                "audio": audio_b64
                            }))
                            
                elif msg_type == MSG_ERROR:
                    logger.error(f"AudioSocket error: {payload}")
                    self.running = False
                    break
                    
            except asyncio.IncompleteReadError:
                logger.info("Asterisk connection closed")
                self.running = False
                break
            except Exception as e:
                logger.error(f"Error receiving from Asterisk: {e}")
                self.running = False
                break
    
    async def ai_to_queue(self):
        """Receive messages from AI WebSocket and queue audio for playback."""
        while self.running and self.ws:
            try:
                message = await self.ws.recv()
                data = json.loads(message)
                
                msg_type = data.get("type")
                
                if msg_type == "audio":
                    # Decode base64 audio (24kHz from AI)
                    audio_b64 = data.get("audio", "")
                    if audio_b64:
                        audio_bytes = base64.b64decode(audio_b64)
                        self.audio_chunks_received += 1
                        
                        # Downsample 24kHz -> 8kHz for Asterisk
                        downsampled = resample_audio(audio_bytes, AI_RATE, AST_RATE)
                        
                        # Add to playback queue
                        self.audio_queue.append(downsampled)
                        logger.debug(f"Queued audio chunk #{self.audio_chunks_received}, size: {len(downsampled)}")
                    
                elif msg_type == "session_ready":
                    logger.info("Taxi AI session ready")
                    
                elif msg_type == "transcript":
                    role = data.get("role", "")
                    text = data.get("text", "")
                    if text.strip():
                        logger.info(f"[{role}] {text}")
                    
                elif msg_type == "booking_confirmed":
                    booking = data.get("booking", {})
                    logger.info(f"BOOKING CONFIRMED: {booking}")
                    
                elif msg_type == "ai_speaking":
                    speaking = data.get("speaking", False)
                    logger.info(f"AI speaking: {speaking}")
                    
                elif msg_type == "response_done":
                    logger.info(f"AI response complete. Chunks received: {self.audio_chunks_received}")
                    
                elif msg_type == "error":
                    logger.error(f"WebSocket error: {data.get('error')}")
                    
            except websockets.ConnectionClosed:
                logger.info("WebSocket connection closed")
                self.running = False
                break
            except Exception as e:
                logger.error(f"Error receiving from WebSocket: {e}")
                continue
                
    async def queue_to_asterisk(self):
        """Send queued audio to Asterisk at real-time playback rate.

        Key behavior:
        - Always pace output at 8kHz (10ms/160 bytes) to avoid jitter.
        - Send SILENCE frames when we have no AI audio ready. Some setups will
          reset/close the AudioSocket if no frames are sent for too long.
        """
        # 8kHz mono 16-bit = 16000 bytes per second
        bytes_per_second = AST_RATE * 2

        # Use 320 bytes (20ms) chunks - typical Asterisk frame size
        chunk_size = 320
        silence_chunk = b"\x00" * chunk_size

        audio_buffer = bytearray()
        bytes_played = 0

        # Initial buffer delay to allow queue to fill (reduces jitter)
        initial_buffer_ms = 100
        buffering = True

        playback_start_time = time.time()
        logger.info("Playback loop started (sending silence when needed)")

        while self.running:
            try:
                # Collect audio from queue into buffer
                while self.audio_queue:
                    audio_buffer.extend(self.audio_queue.popleft())

                # Flip out of buffering mode once we have enough audio queued
                if buffering and len(audio_buffer) >= int(bytes_per_second * initial_buffer_ms / 1000):
                    buffering = False
                    logger.info(f"Starting AI audio playback with {len(audio_buffer)} bytes buffered")

                # Calculate when this 10ms frame should be sent
                expected_time = playback_start_time + (bytes_played / bytes_per_second)
                now = time.time()
                if now < expected_time:
                    await asyncio.sleep(expected_time - now)

                # Choose frame: real audio if available (and not buffering), else silence
                if not buffering and len(audio_buffer) >= chunk_size:
                    chunk = bytes(audio_buffer[:chunk_size])
                    del audio_buffer[:chunk_size]
                else:
                    chunk = silence_chunk

                # Send to Asterisk as AudioSocket frame
                header = struct.pack('>BH', MSG_AUDIO, len(chunk))
                self.writer.write(header + chunk)
                await self.writer.drain()

                bytes_played += len(chunk)
                self.audio_bytes_sent += len(chunk)

            except (ConnectionResetError, BrokenPipeError) as e:
                logger.info(f"Asterisk connection closed/reset: {e}")
                self.running = False
                break
            except Exception as e:
                logger.error(f"Error sending to Asterisk: {e}")
                self.running = False
                break
                
    async def cleanup(self):
        """Clean up resources."""
        self.running = False
        
        call_duration = time.time() - self.call_start_time
        logger.info(f"Call ended. Duration: {call_duration:.1f}s")
        logger.info(f"Audio stats - Chunks received: {self.audio_chunks_received}, Bytes sent: {self.audio_bytes_sent}")
        
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except:
            pass


async def handle_connection(reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
    """Handle a new AudioSocket connection."""
    bridge = TaxiBridge(reader, writer)
    await bridge.run()


async def main():
    """Start the AudioSocket server."""
    print("""
╔══════════════════════════════════════════════════════════════╗
║           Taxi AI - Asterisk AudioSocket Bridge              ║
╠══════════════════════════════════════════════════════════════╣
║  Bridges Asterisk AudioSocket to taxi-realtime WebSocket     ║
║  with automatic resampling (8kHz ↔ 24kHz)                    ║
║                                                              ║
║  AudioSocket Port: 9092                                      ║
║  Asterisk Format:  8kHz 16-bit PCM mono                      ║
║  AI Format:        24kHz 16-bit PCM mono                     ║
╚══════════════════════════════════════════════════════════════╝
    """)
    
    server = await asyncio.start_server(
        handle_connection,
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT
    )
    
    addr = server.sockets[0].getsockname()
    logger.info(f"AudioSocket server listening on {addr[0]}:{addr[1]}")
    logger.info(f"Bridging to: {WS_URL}")
    
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("Shutting down...")
