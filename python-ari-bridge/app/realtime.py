"""
OpenAI Realtime API WebSocket Session

Handles:
- Connection to OpenAI Realtime API
- Session configuration with server VAD
- Audio streaming (send PCM16 @ 24kHz, receive PCM16 @ 24kHz)
- Tool/function calling
- Barge-in handling
"""
import asyncio
import base64
import json
import logging
import time
from dataclasses import dataclass, field
from typing import Optional, Callable, Awaitable, Any
from collections import deque
from math import gcd

import websockets
from websockets.exceptions import ConnectionClosed
import numpy as np
from scipy.signal import resample_poly

from .settings import (
    OPENAI_API_KEY,
    OPENAI_WS_URL,
    OPENAI_VOICE,
    RATE_SLIN16,
    RATE_AI,
    SYSTEM_PROMPT,
    VAD_THRESHOLD,
    VAD_PREFIX_PADDING_MS,
    VAD_SILENCE_DURATION_MS,
    WARMUP_SILENCE_MS,
    VOLUME_BOOST,
    PRE_EMPHASIS_COEFF,
)

logger = logging.getLogger("Realtime")


def resample_audio(audio_bytes: bytes, from_rate: int, to_rate: int) -> bytes:
    """High-quality resampling using polyphase filter"""
    if from_rate == to_rate or not audio_bytes:
        return audio_bytes
    
    samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if samples.size == 0:
        return b""
    
    g = gcd(from_rate, to_rate)
    resampled = resample_poly(samples, up=to_rate // g, down=from_rate // g)
    return np.clip(resampled, -32768, 32767).astype(np.int16).tobytes()


def pre_emphasis(samples: np.ndarray, coeff: float = 0.97) -> np.ndarray:
    """Apply pre-emphasis filter to boost high frequencies"""
    if samples.size == 0:
        return samples
    return np.append(samples[0], samples[1:] - coeff * samples[:-1])


def process_inbound_audio(pcm16_bytes: bytes) -> bytes:
    """
    Process inbound audio for OpenAI:
    - Volume boost for quiet telephony audio
    - Pre-emphasis for clearer consonants
    """
    if not pcm16_bytes or len(pcm16_bytes) < 4:
        return pcm16_bytes
    
    samples = np.frombuffer(pcm16_bytes, dtype=np.int16).astype(np.float32)
    if samples.size == 0:
        return pcm16_bytes
    
    # Volume boost
    if VOLUME_BOOST != 1.0:
        samples *= VOLUME_BOOST
    
    # Pre-emphasis
    if PRE_EMPHASIS_COEFF > 0:
        samples = pre_emphasis(samples, PRE_EMPHASIS_COEFF)
    
    # Soft clip to prevent distortion
    samples = np.tanh(samples / 32000.0) * 32000.0
    
    return np.clip(samples, -32768, 32767).astype(np.int16).tobytes()


@dataclass
class RealtimeSession:
    """State for an OpenAI Realtime session"""
    call_id: str
    caller_phone: str = "unknown"
    ws: Optional[websockets.WebSocketClientProtocol] = None
    connected: bool = False
    session_configured: bool = False
    
    # Audio stats
    audio_sent_bytes: int = 0
    audio_received_bytes: int = 0
    packets_sent: int = 0
    
    # State
    is_speaking: bool = False
    last_activity: float = field(default_factory=time.time)
    
    # Outbound audio queue (PCM16 @ 24kHz from OpenAI)
    audio_queue: deque = field(default_factory=lambda: deque(maxlen=500))


class OpenAIRealtimeClient:
    """
    OpenAI Realtime API client for voice conversations
    
    - Connects to OpenAI Realtime WebSocket
    - Handles session configuration with server VAD
    - Streams audio bidirectionally
    - Processes tool calls
    """
    
    def __init__(
        self,
        call_id: str,
        caller_phone: str = "unknown",
        api_key: str = OPENAI_API_KEY,
    ):
        self.session = RealtimeSession(call_id=call_id, caller_phone=caller_phone)
        self.api_key = api_key
        self._running = False
        
        # Callbacks
        self._on_audio: Optional[Callable[[bytes], Awaitable[None]]] = None
        self._on_transcript: Optional[Callable[[str, str], None]] = None  # (role, text)
        self._on_call_end: Optional[Callable[[], Awaitable[None]]] = None
        self._on_transfer: Optional[Callable[[str], Awaitable[None]]] = None
    
    def on_audio(self, handler: Callable[[bytes], Awaitable[None]]) -> None:
        """Register handler for outbound audio (PCM16 @ 24kHz)"""
        self._on_audio = handler
    
    def on_transcript(self, handler: Callable[[str, str], None]) -> None:
        """Register handler for transcripts (role, text)"""
        self._on_transcript = handler
    
    def on_call_end(self, handler: Callable[[], Awaitable[None]]) -> None:
        """Register handler for call end"""
        self._on_call_end = handler
    
    def on_transfer(self, handler: Callable[[str], Awaitable[None]]) -> None:
        """Register handler for transfer to operator"""
        self._on_transfer = handler
    
    async def connect(self) -> bool:
        """Connect to OpenAI Realtime API"""
        if not self.api_key:
            logger.error("❌ No OpenAI API key configured")
            return False
        
        try:
            headers = {
                "Authorization": f"Bearer {self.api_key}",
                "OpenAI-Beta": "realtime=v1",
            }
            
            logger.info(f"[{self.session.call_id}] Connecting to OpenAI Realtime...")
            
            self.session.ws = await asyncio.wait_for(
                websockets.connect(
                    OPENAI_WS_URL,
                    additional_headers=headers,
                    ping_interval=20,
                    ping_timeout=20,
                    close_timeout=5,
                ),
                timeout=10.0,
            )
            
            self.session.connected = True
            self._running = True
            logger.info(f"[{self.session.call_id}] ✅ Connected to OpenAI Realtime")
            return True
            
        except asyncio.TimeoutError:
            logger.error(f"[{self.session.call_id}] ⏱️ Connection timeout")
            return False
        except Exception as e:
            logger.error(f"[{self.session.call_id}] ❌ Connection failed: {e}")
            return False
    
    async def disconnect(self) -> None:
        """Disconnect from OpenAI"""
        self._running = False
        self.session.connected = False
        
        if self.session.ws:
            try:
                await self.session.ws.close()
            except Exception:
                pass
        
        logger.info(f"[{self.session.call_id}] Disconnected from OpenAI")
    
    async def send_audio(self, pcm16_bytes: bytes) -> None:
        """
        Send audio to OpenAI
        
        Args:
            pcm16_bytes: PCM16 audio at 16kHz (will be resampled to 24kHz)
        """
        if not self.session.ws or not self.session.connected:
            return
        
        # Resample 16kHz → 24kHz
        pcm24 = resample_audio(pcm16_bytes, RATE_SLIN16, RATE_AI)
        
        # Apply DSP processing
        processed = process_inbound_audio(pcm24)
        
        # Encode as base64 and send
        audio_b64 = base64.b64encode(processed).decode("utf-8")
        
        event = {
            "type": "input_audio_buffer.append",
            "audio": audio_b64,
        }
        
        try:
            await self.session.ws.send(json.dumps(event))
            self.session.audio_sent_bytes += len(processed)
            self.session.packets_sent += 1
            self.session.last_activity = time.time()
        except Exception as e:
            logger.warning(f"[{self.session.call_id}] Failed to send audio: {e}")
    
    async def cancel_response(self) -> None:
        """Cancel current AI response (barge-in)"""
        if not self.session.ws or not self.session.connected:
            return
        
        try:
            await self.session.ws.send(json.dumps({"type": "response.cancel"}))
            logger.debug(f"[{self.session.call_id}] 🛑 Response cancelled (barge-in)")
        except Exception as e:
            logger.warning(f"[{self.session.call_id}] Cancel failed: {e}")
    
    async def run(self) -> None:
        """Main receive loop - process events from OpenAI"""
        if not self.session.ws:
            raise RuntimeError("Not connected to OpenAI")
        
        try:
            async for message in self.session.ws:
                if not self._running:
                    break
                
                self.session.last_activity = time.time()
                
                try:
                    event = json.loads(message)
                    await self._handle_event(event)
                except json.JSONDecodeError as e:
                    logger.warning(f"[{self.session.call_id}] Invalid JSON: {e}")
                    
        except ConnectionClosed as e:
            logger.info(f"[{self.session.call_id}] WebSocket closed: {e}")
        except Exception as e:
            logger.error(f"[{self.session.call_id}] Receive error: {e}")
        finally:
            self._running = False
    
    async def _handle_event(self, event: dict) -> None:
        """Handle an event from OpenAI"""
        event_type = event.get("type", "")
        
        if event_type == "session.created":
            logger.info(f"[{self.session.call_id}] 📋 Session created, configuring...")
            await self._configure_session()
        
        elif event_type == "session.updated":
            self.session.session_configured = True
            logger.info(f"[{self.session.call_id}] ✅ Session configured, sending greeting...")
            await self._send_greeting()
        
        elif event_type == "response.audio.delta":
            # Outbound audio from AI
            audio_b64 = event.get("delta", "")
            if audio_b64:
                audio_bytes = base64.b64decode(audio_b64)
                self.session.audio_received_bytes += len(audio_bytes)
                
                if self._on_audio:
                    await self._on_audio(audio_bytes)
        
        elif event_type == "response.audio_transcript.done":
            transcript = event.get("transcript", "")
            if transcript and self._on_transcript:
                self._on_transcript("Ada", transcript)
                logger.info(f"[{self.session.call_id}] 💬 Ada: {transcript}")
        
        elif event_type == "conversation.item.input_audio_transcription.completed":
            transcript = event.get("transcript", "")
            if transcript and self._on_transcript:
                self._on_transcript("User", transcript)
                logger.info(f"[{self.session.call_id}] 💬 User: {transcript}")
        
        elif event_type == "input_audio_buffer.speech_started":
            self.session.is_speaking = True
            logger.debug(f"[{self.session.call_id}] 🎤 User speaking...")
        
        elif event_type == "input_audio_buffer.speech_stopped":
            self.session.is_speaking = False
            logger.debug(f"[{self.session.call_id}] 🔇 User stopped speaking")
        
        elif event_type == "response.function_call_arguments.done":
            await self._handle_tool_call(event)
        
        elif event_type == "error":
            error_msg = event.get("error", {}).get("message", "Unknown error")
            # Ignore benign errors
            if "buffer too small" not in error_msg:
                logger.error(f"[{self.session.call_id}] ❌ OpenAI error: {error_msg}")
        
        elif event_type == "response.done":
            logger.debug(f"[{self.session.call_id}] ✅ Response complete")
    
    async def _configure_session(self) -> None:
        """Configure the OpenAI session"""
        session_update = {
            "type": "session.update",
            "session": {
                "modalities": ["text", "audio"],
                "instructions": SYSTEM_PROMPT,
                "voice": OPENAI_VOICE,
                "input_audio_format": "pcm16",
                "output_audio_format": "pcm16",
                "input_audio_transcription": {
                    "model": "whisper-1",
                },
                "turn_detection": {
                    "type": "server_vad",
                    "threshold": VAD_THRESHOLD,
                    "prefix_padding_ms": VAD_PREFIX_PADDING_MS,
                    "silence_duration_ms": VAD_SILENCE_DURATION_MS,
                },
                "tools": self._get_tools(),
                "tool_choice": "auto",
                "temperature": 0.7,
                "max_response_output_tokens": 1024,
            },
        }
        
        await self.session.ws.send(json.dumps(session_update))
        logger.info(f"[{self.session.call_id}] 🎧 Config: VAD={VAD_THRESHOLD}, "
                    f"prefix={VAD_PREFIX_PADDING_MS}ms, silence={VAD_SILENCE_DURATION_MS}ms")
    
    async def _send_greeting(self) -> None:
        """Trigger AI greeting"""
        # Send warmup silence to stabilize VAD
        if WARMUP_SILENCE_MS > 0:
            silence_samples = int(RATE_AI * WARMUP_SILENCE_MS / 1000)
            silence = bytes(silence_samples * 2)
            silence_b64 = base64.b64encode(silence).decode("utf-8")
            await self.session.ws.send(json.dumps({
                "type": "input_audio_buffer.append",
                "audio": silence_b64,
            }))
        
        # Trigger greeting response
        await asyncio.sleep(0.2)
        await self.session.ws.send(json.dumps({"type": "response.create"}))
        logger.info(f"[{self.session.call_id}] 🎤 Greeting triggered")
    
    def _get_tools(self) -> list:
        """Get tool definitions for OpenAI"""
        return [
            {
                "type": "function",
                "name": "end_call",
                "description": "End the call after saying goodbye",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "reason": {
                            "type": "string",
                            "description": "Reason for ending the call",
                        },
                    },
                    "required": ["reason"],
                },
            },
            {
                "type": "function",
                "name": "transfer_to_operator",
                "description": "Transfer the caller to a human operator",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "reason": {
                            "type": "string",
                            "description": "Reason for transfer",
                        },
                    },
                    "required": ["reason"],
                },
            },
            {
                "type": "function",
                "name": "book_taxi",
                "description": "Book a taxi with collected details",
                "parameters": {
                    "type": "object",
                    "properties": {
                        "pickup": {"type": "string", "description": "Pickup address"},
                        "destination": {"type": "string", "description": "Destination address"},
                        "passengers": {"type": "integer", "description": "Number of passengers"},
                        "pickup_time": {"type": "string", "description": "Requested pickup time"},
                    },
                    "required": ["pickup", "destination"],
                },
            },
        ]
    
    async def _handle_tool_call(self, event: dict) -> None:
        """Handle a tool call from OpenAI"""
        call_id = event.get("call_id", "")
        name = event.get("name", "")
        args_str = event.get("arguments", "{}")
        
        try:
            args = json.loads(args_str)
        except json.JSONDecodeError:
            args = {}
        
        logger.info(f"[{self.session.call_id}] 🔧 Tool call: {name}({args})")
        
        result = {"success": True}
        
        if name == "end_call":
            reason = args.get("reason", "Call completed")
            result = {"success": True, "message": f"Call ended: {reason}"}
            
            # Send tool output first
            await self._send_tool_output(call_id, result)
            
            # Then trigger call end
            if self._on_call_end:
                await self._on_call_end()
            return
        
        elif name == "transfer_to_operator":
            reason = args.get("reason", "Customer request")
            result = {"success": True, "message": f"Transferring: {reason}"}
            
            if self._on_transfer:
                await self._on_transfer(reason)
        
        elif name == "book_taxi":
            # Simulate booking
            result = {
                "success": True,
                "booking_ref": f"TX{int(time.time()) % 100000:05d}",
                "pickup": args.get("pickup"),
                "destination": args.get("destination"),
                "passengers": args.get("passengers", 1),
                "pickup_time": args.get("pickup_time", "ASAP"),
                "estimated_fare": "£8-12",
                "eta_minutes": 5,
            }
            logger.info(f"[{self.session.call_id}] 🚕 Booking: {result}")
        
        await self._send_tool_output(call_id, result)
    
    async def _send_tool_output(self, call_id: str, result: dict) -> None:
        """Send tool output back to OpenAI"""
        if not self.session.ws or not self.session.connected:
            return
        
        # Create conversation item with tool output
        item_event = {
            "type": "conversation.item.create",
            "item": {
                "type": "function_call_output",
                "call_id": call_id,
                "output": json.dumps(result),
            },
        }
        
        await self.session.ws.send(json.dumps(item_event))
        
        # Trigger response
        await self.session.ws.send(json.dumps({"type": "response.create"}))
