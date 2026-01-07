#!/usr/bin/env python3
"""
Asterisk AudioSocket to Taxi AI WebSocket Bridge

This script bridges Asterisk's AudioSocket protocol to the taxi-realtime 
Supabase Edge Function via WebSocket for real-time voice AI interactions.

Usage:
    python taxi_bridge.py

Requirements:
    pip install websockets

Asterisk dialplan example:
    exten => 100,1,Answer()
    same => n,AudioSocket(127.0.0.1:9092)
    same => n,Hangup()
"""

import asyncio
import json
import socket
import struct
import time
import logging
from typing import Optional

try:
    import websockets
except ImportError:
    print("Please install websockets: pip install websockets")
    exit(1)

# Configuration
AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092
WS_URL = "wss://xsdlzoyaosfbbwzmcinq.supabase.co/functions/v1/taxi-realtime"

# AudioSocket message types
MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10
MSG_ERROR = 0xFF

# Audio settings (must match OpenAI Realtime API requirements)
SAMPLE_RATE = 24000  # Hz
SAMPLE_SIZE = 2      # 16-bit PCM = 2 bytes
CHANNELS = 1
FRAME_SIZE = 4800    # 200ms of audio at 24kHz (24000 * 0.2)

# Logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s'
)
logger = logging.getLogger(__name__)


class AudioSocketBridge:
    """Bridges Asterisk AudioSocket to taxi-realtime WebSocket."""
    
    def __init__(self, client_socket: socket.socket, client_addr: tuple):
        self.client_socket = client_socket
        self.client_addr = client_addr
        self.ws: Optional[websockets.WebSocketClientProtocol] = None
        self.call_uuid: Optional[str] = None
        self.running = False
        self.audio_buffer = bytearray()
        self.call_start_time = time.time()
        
    async def handle_call(self):
        """Main handler for a single call."""
        logger.info(f"New connection from {self.client_addr}")
        self.running = True
        
        try:
            # Connect to taxi-realtime WebSocket
            logger.info("Connecting to taxi-realtime WebSocket...")
            self.ws = await websockets.connect(WS_URL)
            logger.info("Connected to taxi-realtime")
            
            # Initialize session
            call_id = f"asterisk-{int(time.time() * 1000)}"
            await self.ws.send(json.dumps({
                "type": "init",
                "call_id": call_id
            }))
            logger.info(f"Session initialized: {call_id}")
            
            # Run both directions concurrently
            await asyncio.gather(
                self.receive_from_asterisk(),
                self.receive_from_websocket()
            )
            
        except Exception as e:
            logger.error(f"Error in call handler: {e}")
        finally:
            await self.cleanup()
            
    async def receive_from_asterisk(self):
        """Receive audio from Asterisk AudioSocket and forward to WebSocket."""
        loop = asyncio.get_event_loop()
        
        while self.running:
            try:
                # Read AudioSocket frame header (3 bytes: type + length)
                header = await loop.run_in_executor(
                    None, lambda: self.client_socket.recv(3)
                )
                
                if not header or len(header) < 3:
                    logger.info("Asterisk connection closed")
                    self.running = False
                    break
                    
                msg_type = header[0]
                payload_len = struct.unpack('>H', header[1:3])[0]
                
                # Read payload
                payload = b''
                while len(payload) < payload_len:
                    chunk = await loop.run_in_executor(
                        None, lambda: self.client_socket.recv(payload_len - len(payload))
                    )
                    if not chunk:
                        break
                    payload += chunk
                
                # Handle message types
                if msg_type == MSG_HANGUP:
                    logger.info("Received hangup from Asterisk")
                    self.running = False
                    # Notify backend of call end
                    if self.ws:
                        await self.ws.send(json.dumps({"type": "hangup"}))
                    break
                    
                elif msg_type == MSG_UUID:
                    self.call_uuid = payload.decode('utf-8').strip('\x00')
                    logger.info(f"Call UUID: {self.call_uuid}")
                    
                elif msg_type == MSG_AUDIO:
                    # Buffer audio and send in chunks
                    self.audio_buffer.extend(payload)
                    
                    # Send when we have enough audio (100ms minimum)
                    min_bytes = int(SAMPLE_RATE * 0.1 * SAMPLE_SIZE)  # 100ms
                    while len(self.audio_buffer) >= min_bytes:
                        chunk = bytes(self.audio_buffer[:min_bytes])
                        self.audio_buffer = self.audio_buffer[min_bytes:]
                        
                        # Convert to base64 and send
                        import base64
                        audio_b64 = base64.b64encode(chunk).decode('utf-8')
                        
                        if self.ws:
                            await self.ws.send(json.dumps({
                                "type": "audio",
                                "audio": audio_b64
                            }))
                            
                elif msg_type == MSG_ERROR:
                    logger.error(f"AudioSocket error: {payload}")
                    self.running = False
                    break
                    
            except Exception as e:
                logger.error(f"Error receiving from Asterisk: {e}")
                self.running = False
                break
                
    async def receive_from_websocket(self):
        """Receive audio from WebSocket and forward to Asterisk."""
        import base64
        
        while self.running and self.ws:
            try:
                message = await asyncio.wait_for(self.ws.recv(), timeout=0.1)
                data = json.loads(message)
                
                msg_type = data.get("type")
                
                if msg_type == "audio":
                    # Decode base64 audio
                    audio_b64 = data.get("audio", "")
                    audio_bytes = base64.b64decode(audio_b64)
                    
                    # Send to Asterisk as AudioSocket frame
                    await self.send_audio_to_asterisk(audio_bytes)
                    
                elif msg_type == "session_ready":
                    logger.info("Taxi AI session ready")
                    
                elif msg_type == "transcript":
                    role = data.get("role", "")
                    text = data.get("text", "")
                    logger.info(f"[{role}] {text}")
                    
                elif msg_type == "booking_confirmed":
                    booking = data.get("booking", {})
                    logger.info(f"BOOKING CONFIRMED: {booking}")
                    
                elif msg_type == "response_done":
                    logger.info("AI response complete")
                    
                elif msg_type == "error":
                    logger.error(f"WebSocket error: {data.get('error')}")
                    
            except asyncio.TimeoutError:
                continue
            except websockets.ConnectionClosed:
                logger.info("WebSocket connection closed")
                self.running = False
                break
            except Exception as e:
                logger.error(f"Error receiving from WebSocket: {e}")
                continue
                
    async def send_audio_to_asterisk(self, audio_bytes: bytes):
        """Send audio frame to Asterisk via AudioSocket."""
        loop = asyncio.get_event_loop()
        
        try:
            # AudioSocket frame: type (1 byte) + length (2 bytes big-endian) + payload
            header = struct.pack('>BH', MSG_AUDIO, len(audio_bytes))
            frame = header + audio_bytes
            
            await loop.run_in_executor(
                None, lambda: self.client_socket.sendall(frame)
            )
        except Exception as e:
            logger.error(f"Error sending to Asterisk: {e}")
            self.running = False
            
    async def cleanup(self):
        """Clean up resources."""
        self.running = False
        
        call_duration = time.time() - self.call_start_time
        logger.info(f"Call ended. Duration: {call_duration:.1f}s")
        
        if self.ws:
            try:
                await self.ws.close()
            except:
                pass
                
        try:
            self.client_socket.close()
        except:
            pass


async def start_server():
    """Start the AudioSocket server."""
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_socket.bind((AUDIOSOCKET_HOST, AUDIOSOCKET_PORT))
    server_socket.listen(5)
    server_socket.setblocking(False)
    
    logger.info(f"AudioSocket server listening on {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}")
    logger.info(f"Bridging to: {WS_URL}")
    
    loop = asyncio.get_event_loop()
    
    while True:
        try:
            client_socket, client_addr = await loop.run_in_executor(
                None, server_socket.accept
            )
            
            # Handle each call in a new task
            bridge = AudioSocketBridge(client_socket, client_addr)
            asyncio.create_task(bridge.handle_call())
            
        except Exception as e:
            logger.error(f"Error accepting connection: {e}")
            await asyncio.sleep(1)


def main():
    """Main entry point."""
    print("""
╔══════════════════════════════════════════════════════════════╗
║           Taxi AI - Asterisk AudioSocket Bridge              ║
╠══════════════════════════════════════════════════════════════╣
║  Bridges Asterisk AudioSocket to taxi-realtime WebSocket     ║
║                                                              ║
║  AudioSocket Port: 9092                                      ║
║  Audio Format: 24kHz 16-bit PCM mono                         ║
╚══════════════════════════════════════════════════════════════╝
    """)
    
    try:
        asyncio.run(start_server())
    except KeyboardInterrupt:
        logger.info("Shutting down...")


if __name__ == "__main__":
    main()
