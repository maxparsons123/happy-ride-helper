#!/usr/bin/env python3
"""Taxi AI Asterisk Bridge - GEMINI PIPELINE

Bridge for the taxi-realtime-gemini edge function.
Uses STT → Gemini LLM → TTS pipeline (stateless, no session timeouts).

Protocol differences from OpenAI Realtime:
- Init: {"type": "session.start", "call_id": "...", "phone": "..."}
- Audio send: {"type": "audio", "audio": "base64_encoded_pcm"}
- Audio receive: {"type": "audio.delta", "delta": "base64_pcm"}
- Transcripts: {"type": "transcript.user"} / {"type": "transcript.assistant"}

Dependencies:
    pip install websockets numpy scipy

Usage:
    python3 taxi_bridge_gemini.py

    # Or via systemd:
    sudo cp taxi-bridge-gemini.service /etc/systemd/system/
    sudo systemctl daemon-reload
    sudo systemctl start taxi-bridge-gemini
"""

import asyncio
import json
import struct
import base64
import time
import logging
import os
import sys
from typing import Optional
from collections import deque

import numpy as np
from scipy.signal import resample_poly, butter, sosfilt

import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException

# =============================================================================
# CONFIGURATION
# =============================================================================

VERSION = "1.0-gemini"

AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = 9092

# Gemini pipeline endpoint
DEFAULT_WS_URL = "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-gemini"
WS_URL = os.environ.get("WS_URL", DEFAULT_WS_URL)

# Audio rates
AST_RATE = 8000   # Asterisk telephony rate (native µ-law)
AI_RATE = 24000   # AI TTS output rate (Gemini pipeline uses 24kHz PCM)

# Reconnection settings
MAX_RECONNECT_ATTEMPTS = 5
RECONNECT_BASE_DELAY_S = 1.0
HEARTBEAT_INTERVAL_S = 15

# Asterisk AudioSocket settings
ASTERISK_READ_TIMEOUT_S = 10.0

# Audio processing
HIGH_PASS_CUTOFF = 80
LOW_PASS_CUTOFF = 3400
TARGET_RMS = 2500
MAX_GAIN = 2.5
MIN_GAIN = 0.8
GAIN_SMOOTHING_FACTOR = 0.15

# =============================================================================
# LOGGING
# =============================================================================

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[logging.StreamHandler(sys.stdout)],
    force=True,
)
logger = logging.getLogger(__name__)

print(f"🚀 Taxi Bridge GEMINI v{VERSION}", flush=True)

# =============================================================================
# AUDIO CODECS
# =============================================================================

ULAW_BIAS = 0x84
ULAW_CLIP = 32635

MSG_HANGUP = 0x00
MSG_UUID = 0x01
MSG_AUDIO = 0x10

_highpass_sos = butter(2, HIGH_PASS_CUTOFF, btype='high', fs=AST_RATE, output='sos')
_lowpass_sos = butter(2, LOW_PASS_CUTOFF, btype='low', fs=AST_RATE, output='sos')


def ulaw2lin(ulaw_bytes: bytes) -> bytes:
    """Decode μ-law to 16-bit linear PCM."""
    ulaw = np.frombuffer(ulaw_bytes, dtype=np.uint8)
    ulaw = ~ulaw
    sign = (ulaw & 0x80)
    exponent = (ulaw >> 4) & 0x07
    mantissa = ulaw & 0x0F
    sample = (mantissa << 3) + ULAW_BIAS
    sample <<= exponent
    sample -= ULAW_BIAS
    pcm = np.where(sign != 0, -sample, sample).astype(np.int16)
    return pcm.tobytes()


def lin2ulaw(pcm_bytes: bytes) -> bytes:
    """Encode 16-bit linear PCM to μ-law."""
    pcm = np.frombuffer(pcm_bytes, dtype=np.int16).astype(np.int32)
    sign = np.where(pcm < 0, 0x80, 0)
    pcm = np.abs(pcm)
    pcm = np.clip(pcm, 0, ULAW_CLIP)
    pcm += ULAW_BIAS
    exponent = np.maximum(0, np.floor(np.log2(np.maximum(pcm, 1))).astype(np.int32) - 7)
    exponent = np.clip(exponent, 0, 7)
    mantissa = (pcm >> (exponent + 3)) & 0x0F
    ulaw = (~(sign | (exponent << 4) | mantissa)) & 0xFF
    return ulaw.astype(np.uint8).tobytes()


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """Resample audio using scipy."""
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return b""
    
    from math import gcd
    common = gcd(to_rate, from_rate)
    up = to_rate // common
    down = from_rate // common
    
    resampled = resample_poly(audio_np, up=up, down=down)
    resampled = np.clip(resampled, -32768, 32767).astype(np.int16)
    return resampled.tobytes()


def apply_filters(audio_bytes: bytes, last_gain: float = 1.0) -> tuple:
    """Apply HPF + LPF + gentle AGC."""
    if not audio_bytes or len(audio_bytes) < 4:
        return audio_bytes, last_gain
    
    audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if audio_np.size == 0:
        return audio_bytes, last_gain
    
    # High-pass filter
    audio_np = sosfilt(_highpass_sos, audio_np)
    # Low-pass filter
    audio_np = sosfilt(_lowpass_sos, audio_np)
    
    # Gentle AGC
    rms = float(np.sqrt(np.mean(audio_np ** 2))) + 1e-6
    target_gain = np.clip(TARGET_RMS / rms, MIN_GAIN, MAX_GAIN)
    current_gain = last_gain + GAIN_SMOOTHING_FACTOR * (target_gain - last_gain)
    audio_np *= current_gain
    
    audio_np = np.clip(audio_np, -32768, 32767).astype(np.int16)
    return audio_np.tobytes(), current_gain


# =============================================================================
# GEMINI BRIDGE CLASS
# =============================================================================

class TaxiBridgeGemini:
    def __init__(self, reader, writer):
        self.reader = reader
        self.writer = writer
        self.ws = None
        self.running = True
        self.audio_queue = deque(maxlen=200)
        self.call_id = f"gemini-{int(time.time() * 1000)}"
        self.phone = "Unknown"
        self.ast_codec = "ulaw"
        self.ast_frame_bytes = 160
        self.ast_rate = 8000  # Actual sample rate (8000 for ulaw, 16000 for slin16)
        self.last_gain = 1.0
        
        self.ws_connected = False
        self.last_ws_activity = time.time()
        self.last_asterisk_recv = time.time()
        self.reconnect_attempts = 0
        
        # Stats
        self.frames_sent = 0
        self.audio_chunks_received = 0
        self.keepalive_count = 0

    def _detect_format(self, frame_len):
        if frame_len == 160:
            self.ast_codec, self.ast_frame_bytes, self.ast_rate = "ulaw", 160, 8000
        elif frame_len == 320:
            self.ast_codec, self.ast_frame_bytes, self.ast_rate = "slin16", 320, 16000
        print(f"[{self.call_id}] 🔎 Format: {self.ast_codec} ({frame_len} bytes, {self.ast_rate}Hz)", flush=True)

    async def connect_websocket(self) -> bool:
        """Connect to Gemini WebSocket endpoint."""
        while self.reconnect_attempts < MAX_RECONNECT_ATTEMPTS and self.running:
            try:
                delay = RECONNECT_BASE_DELAY_S * (2 ** self.reconnect_attempts) if self.reconnect_attempts > 0 else 0
                if delay > 0:
                    print(f"[{self.call_id}] 🔄 Reconnecting in {delay:.1f}s (attempt {self.reconnect_attempts + 1}/{MAX_RECONNECT_ATTEMPTS})", flush=True)
                    await asyncio.sleep(delay)
                
                self.ws = await asyncio.wait_for(
                    websockets.connect(WS_URL, ping_interval=20, ping_timeout=30),
                    timeout=10.0
                )
                
                self.ws_connected = True
                self.last_ws_activity = time.time()
                self.reconnect_attempts = 0
                
                print(f"[{self.call_id}] ✅ WebSocket connected to Gemini pipeline", flush=True)
                return True
                
            except asyncio.TimeoutError:
                print(f"[{self.call_id}] ⏱️ WebSocket connection timeout", flush=True)
                self.reconnect_attempts += 1
            except Exception as e:
                print(f"[{self.call_id}] ❌ WebSocket error: {e}", flush=True)
                self.reconnect_attempts += 1
        
        return False

    async def send_session_start(self):
        """Send session.start message (Gemini protocol)."""
        msg = {
            "type": "session.start",
            "call_id": self.call_id,
            "phone": self.phone if self.phone != "Unknown" else None,
            "source": "asterisk",
            "stt_provider": "deepgram",  # or "groq"
            "tts_provider": "elevenlabs",  # or "deepgram"
        }
        await self.ws.send(json.dumps(msg))
        print(f"[{self.call_id}] 🚀 Sent session.start (Gemini)", flush=True)

    async def send_audio(self, pcm_bytes: bytes):
        """Send audio as JSON-wrapped base64 (Gemini protocol)."""
        if not self.ws_connected or not self.ws:
            return
        
        # Resample from actual Asterisk rate (8kHz or 16kHz) → 24kHz for Gemini
        pcm_24k = resample_audio(pcm_bytes, self.ast_rate, AI_RATE)
        
        msg = {
            "type": "audio",
            "audio": base64.b64encode(pcm_24k).decode("ascii")
        }
        try:
            await self.ws.send(json.dumps(msg))
            self.frames_sent += 1
            self.last_ws_activity = time.time()
        except Exception as e:
            print(f"[{self.call_id}] ❌ Send audio error: {e}", flush=True)
            raise

    async def stop_call(self, reason: str):
        """Stop the bridge cleanly."""
        if not self.running:
            return
        self.running = False
        self.ws_connected = False
        
        try:
            if self.ws:
                await self.ws.close(code=1000, reason=reason)
        except:
            pass
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except:
            pass

    async def run(self):
        peer = self.writer.get_extra_info("peername")
        print(f"[{self.call_id}] 📞 Call from {peer}", flush=True)

        playback_task = asyncio.create_task(self.queue_to_asterisk())
        heartbeat_task = asyncio.create_task(self.heartbeat_loop())

        try:
            # Wait for phone number from Asterisk
            print(f"[{self.call_id}] ⏳ Waiting for UUID from Asterisk...", flush=True)
            phone_received = False
            wait_start = time.time()
            
            while not phone_received and self.running:
                try:
                    header = await asyncio.wait_for(self.reader.readexactly(3), timeout=2.0)
                    m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                    payload = await self.reader.readexactly(m_len)
                    
                    if m_type == MSG_UUID:
                        raw_hex = payload.hex()
                        if len(raw_hex) >= 12:
                            self.phone = raw_hex[-12:]
                        print(f"[{self.call_id}] 👤 Phone: {self.phone}", flush=True)
                        phone_received = True
                    elif m_type == MSG_HANGUP:
                        print(f"[{self.call_id}] 📴 Hangup before init", flush=True)
                        return
                except asyncio.TimeoutError:
                    if time.time() - wait_start > 2.0:
                        print(f"[{self.call_id}] ⚠️ No UUID, proceeding", flush=True)
                        break

            # Connect to Gemini WebSocket
            if not await self.connect_websocket():
                print(f"[{self.call_id}] ❌ Connection failed", flush=True)
                return
            
            # Send session.start (Gemini protocol)
            await self.send_session_start()

            # Main loop
            await asyncio.gather(
                self.asterisk_to_ai(),
                self.ai_to_queue(),
                return_exceptions=False
            )

        except Exception as e:
            print(f"[{self.call_id}] ❌ Run error: {e}", flush=True)
        finally:
            self.running = False
            
            for task in [playback_task, heartbeat_task]:
                if not task.done():
                    task.cancel()
            await asyncio.gather(playback_task, heartbeat_task, return_exceptions=True)
            
            print(f"[{self.call_id}] 📊 Final: TX={self.frames_sent} RX={self.audio_chunks_received} KA={self.keepalive_count}", flush=True)
            await self.cleanup()

    async def heartbeat_loop(self):
        while self.running:
            try:
                await asyncio.sleep(HEARTBEAT_INTERVAL_S)
                if self.running:
                    ws_age = time.time() - self.last_ws_activity
                    ast_age = time.time() - self.last_asterisk_recv
                    ws_status = "🟢" if ws_age < 5 else "🟡" if ws_age < 15 else "🔴"
                    ast_status = "🟢" if ast_age < 5 else "🟡" if ast_age < 15 else "🔴"
                    print(f"[{self.call_id}] 💓 WS{ws_status}({ws_age:.1f}s) AST{ast_status}({ast_age:.1f}s) 🔊{self.ast_codec} TX:{self.frames_sent} RX:{self.audio_chunks_received}", flush=True)
            except asyncio.CancelledError:
                break

    async def asterisk_to_ai(self):
        """Read audio from Asterisk, apply filters, send to Gemini."""
        while self.running and self.ws_connected:
            try:
                header = await asyncio.wait_for(self.reader.readexactly(3), timeout=ASTERISK_READ_TIMEOUT_S)
                self.last_asterisk_recv = time.time()
                
                m_type, m_len = header[0], struct.unpack(">H", header[1:3])[0]
                payload = await self.reader.readexactly(m_len)

                if m_type == MSG_UUID:
                    pass  # Already handled
                elif m_type == MSG_AUDIO:
                    if m_len != self.ast_frame_bytes:
                        self._detect_format(m_len)
                    
                    # Decode µ-law to linear PCM
                    linear16 = ulaw2lin(payload) if self.ast_codec == "ulaw" else payload
                    
                    # Apply filters
                    cleaned, self.last_gain = apply_filters(linear16, self.last_gain)
                    
                    # Send to Gemini (JSON-wrapped base64)
                    await self.send_audio(cleaned)
                    
                elif m_type == MSG_HANGUP:
                    print(f"[{self.call_id}] 📴 Hangup", flush=True)
                    await self.stop_call("Asterisk hangup")
                    return

            except asyncio.TimeoutError:
                last_recv_age = time.time() - self.last_asterisk_recv
                if last_recv_age > ASTERISK_READ_TIMEOUT_S * 2:
                    print(f"[{self.call_id}] ⏱️ Asterisk timeout ({last_recv_age:.1f}s)", flush=True)
                    await self.stop_call("Asterisk timeout")
                    return
                continue
            except asyncio.IncompleteReadError:
                print(f"[{self.call_id}] 📴 Closed", flush=True)
                await self.stop_call("Closed")
                return
            except (ConnectionClosed, WebSocketException) as e:
                print(f"[{self.call_id}] 🔌 WebSocket closed: {e}", flush=True)
                self.ws_connected = False
                # Try to reconnect
                if await self.connect_websocket():
                    await self.send_session_start()
                else:
                    await self.stop_call("Reconnect failed")
                    return
            except asyncio.CancelledError:
                return
            except Exception as e:
                print(f"[{self.call_id}] ❌ Asterisk->AI error: {e}", flush=True)
                await self.stop_call("Error")
                return

    async def ai_to_queue(self):
        """Receive audio/transcripts from Gemini and queue for playback."""
        try:
            async for message in self.ws:
                if not self.running:
                    break
                
                self.last_ws_activity = time.time()
                
                # Gemini sends JSON messages
                try:
                    data = json.loads(message)
                except json.JSONDecodeError:
                    # Might be raw binary (unlikely for Gemini)
                    print(f"[{self.call_id}] ⚠️ Non-JSON message: {len(message)} bytes", flush=True)
                    continue
                
                msg_type = data.get("type", "")
                
                if msg_type == "session_ready":
                    pipeline = data.get("pipeline", "gemini")
                    stt = data.get("stt_provider", "?")
                    tts = data.get("tts_provider", "?")
                    print(f"[{self.call_id}] ✅ Session ready: {pipeline} (STT:{stt}, TTS:{tts})", flush=True)
                
                elif msg_type == "audio.delta":
                    # Base64 PCM16 @ 24kHz
                    audio_b64 = data.get("delta", "")
                    if audio_b64:
                        pcm_24k = base64.b64decode(audio_b64)
                        # Resample 24kHz → actual Asterisk rate (8kHz or 16kHz)
                        pcm_out = resample_audio(pcm_24k, AI_RATE, self.ast_rate)
                        # Convert to µ-law only if codec is ulaw
                        out = lin2ulaw(pcm_out) if self.ast_codec == "ulaw" else pcm_out
                        self.audio_queue.append(out)
                        self.audio_chunks_received += 1
                
                elif msg_type == "audio.done":
                    # End of audio response
                    pass
                
                elif msg_type == "audio.interrupted":
                    # Barge-in detected, clear queue
                    size = len(self.audio_queue)
                    self.audio_queue.clear()
                    print(f"[{self.call_id}] 🛑 Interrupted, flushed {size} chunks", flush=True)
                
                elif msg_type == "transcript.user":
                    text = data.get("text", "")
                    print(f"[{self.call_id}] 💬 USER: {text}", flush=True)
                
                elif msg_type == "transcript.assistant":
                    text = data.get("text", "")
                    print(f"[{self.call_id}] 💬 ASSISTANT: {text}", flush=True)
                
                elif msg_type.startswith("latency."):
                    # Latency metrics from Gemini pipeline
                    latency_type = msg_type.split(".")[1]
                    latency_ms = data.get("latency_ms", 0)
                    print(f"[{self.call_id}] ⏱️ {latency_type.upper()}: {latency_ms}ms", flush=True)
                
                elif msg_type == "session.end":
                    reason = data.get("reason", "unknown")
                    print(f"[{self.call_id}] 📴 Session ended: {reason}", flush=True)
                    self.running = False
                    break
                
                elif msg_type == "error":
                    error = data.get("error", data.get("message", "unknown"))
                    print(f"[{self.call_id}] 🧨 Error: {error}", flush=True)
                
                else:
                    print(f"[{self.call_id}] 📨 {msg_type}: {json.dumps(data)[:100]}", flush=True)

        except (ConnectionClosed, WebSocketException) as e:
            print(f"[{self.call_id}] 🔌 WebSocket closed: {e}", flush=True)
        except asyncio.CancelledError:
            pass
        except Exception as e:
            print(f"[{self.call_id}] ❌ AI->Queue error: {e}", flush=True)
        finally:
            print(f"[{self.call_id}] 📊 Audio chunks received: {self.audio_chunks_received}", flush=True)

    async def queue_to_asterisk(self):
        """Send audio queue to Asterisk with keep-alive."""
        start_time = time.time()
        bytes_played = 0
        buffer = bytearray()

        while self.running:
            try:
                # Drain queue to buffer
                while self.audio_queue:
                    buffer.extend(self.audio_queue.popleft())

                # Timing for smooth playback
                bytes_per_sec = AST_RATE * (1 if self.ast_codec == "ulaw" else 2)
                expected_time = start_time + (bytes_played / bytes_per_sec)
                sleep_time = max(0, expected_time - time.time())
                if sleep_time > 0:
                    await asyncio.sleep(sleep_time)

                # Send audio or silence keep-alive
                if len(buffer) >= self.ast_frame_bytes:
                    chunk = bytes(buffer[:self.ast_frame_bytes])
                    del buffer[:self.ast_frame_bytes]
                else:
                    chunk = self._silence()
                    self.keepalive_count += 1

                try:
                    self.writer.write(struct.pack(">BH", MSG_AUDIO, len(chunk)) + chunk)
                    await self.writer.drain()
                    bytes_played += len(chunk)
                except (BrokenPipeError, ConnectionResetError, OSError) as e:
                    print(f"[{self.call_id}] 🔌 Asterisk pipe closed: {e}", flush=True)
                    await self.stop_call("Asterisk disconnected")
                    return

            except asyncio.CancelledError:
                return
            except Exception as e:
                print(f"[{self.call_id}] ❌ Queue->Asterisk error: {e}", flush=True)
                await self.stop_call("Queue error")
                return

    def _silence(self):
        return (b"\xFF" if self.ast_codec == "ulaw" else b"\x00") * self.ast_frame_bytes

    async def cleanup(self):
        try:
            if self.ws:
                await self.ws.close()
                self.ws = None
        except:
            pass
        try:
            if not self.writer.is_closing():
                self.writer.close()
                await self.writer.wait_closed()
        except:
            pass
        self.audio_queue.clear()


# =============================================================================
# MAIN
# =============================================================================

async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridgeGemini(r, w).run(),
        AUDIOSOCKET_HOST, AUDIOSOCKET_PORT
    )
    
    print(f"🚀 Taxi Bridge GEMINI v{VERSION}", flush=True)
    print(f"   Listening: {AUDIOSOCKET_HOST}:{AUDIOSOCKET_PORT}", flush=True)
    print(f"   Endpoint:  {WS_URL.split('/')[-1]}", flush=True)
    print(f"   Pipeline:  STT → Gemini LLM → TTS (stateless)", flush=True)
    
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
