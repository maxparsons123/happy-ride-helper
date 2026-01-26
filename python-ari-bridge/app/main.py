"""
Python ARI Bridge - Main Entry Point

Architecture:
  Inbound Call → Asterisk ARI → ExternalMedia (slin16) → RTP ←→ This Bridge ←→ OpenAI Realtime

Key Features:
  • Uses ARI for call control (not AudioSocket)
  • ExternalMedia channel for clean RTP injection
  • slin16 format (16kHz) - higher quality than 8kHz µ-law
  • Direct OpenAI Realtime connection (no edge function)
  • Server VAD for turn detection
"""
import asyncio
import logging
import signal
import socket
import time
from dataclasses import dataclass, field
from typing import Dict, Optional
import os

from .settings import (
    ARI_APP,
    RTP_BIND_HOST,
    RTP_PORT_START,
    RTP_PORT_END,
    RATE_SLIN16,
    RATE_AI,
    BYTES_PER_FRAME_16K,
    FRAME_MS,
    LOG_LEVEL,
)
from .ari import ARIClient, ARIChannel, ExternalMediaChannel
from .rtp import RTPSession
from .realtime import OpenAIRealtimeClient, resample_audio
from .observability import (
    MetricsServer,
    record_call_started,
    record_call_ended,
    record_rtp_in,
    record_rtp_out,
    record_openai_audio,
)

# Configure logging
logging.basicConfig(
    level=getattr(logging, LOG_LEVEL, logging.INFO),
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("ARIBridge")


@dataclass
class CallSession:
    """State for an active call"""
    call_id: str
    caller_phone: str
    channel_id: str
    bridge_id: str
    external_media: ExternalMediaChannel
    rtp_session: RTPSession
    openai_client: OpenAIRealtimeClient
    rtp_socket: socket.socket
    asterisk_rtp_address: tuple  # (host, port) to send RTP back
    
    started_at: float = field(default_factory=time.time)
    ended: bool = False
    
    # Stats
    rtp_packets_received: int = 0
    rtp_packets_sent: int = 0


class ARIBridge:
    """
    Main bridge connecting Asterisk ARI to OpenAI Realtime
    """
    VERSION = "1.0.0"
    
    def __init__(self):
        self.ari = ARIClient()
        self.metrics = MetricsServer()
        self.sessions: Dict[str, CallSession] = {}
        self._running = False
        self._rtp_port_counter = RTP_PORT_START
        self._port_lock = asyncio.Lock()
    
    async def start(self) -> None:
        """Start the bridge"""
        logger.info(f"🚀 ARI Bridge v{self.VERSION} starting...")
        
        # Start metrics server
        await self.metrics.start()
        
        # Connect to ARI
        await self.ari.connect()
        
        # Register event handlers
        self.ari.on_stasis_start(self._on_call_start)
        self.ari.on_stasis_end(self._on_call_end)
        
        self._running = True
        
        # Run ARI event loop
        await self.ari.run_event_loop()
    
    async def stop(self) -> None:
        """Stop the bridge"""
        logger.info("🛑 Stopping ARI Bridge...")
        self._running = False
        
        # End all active calls
        for session in list(self.sessions.values()):
            await self._cleanup_session(session)
        
        await self.ari.disconnect()
        await self.metrics.stop()
        
        logger.info("✅ ARI Bridge stopped")
    
    async def _get_next_rtp_port(self) -> int:
        """Get next available RTP port"""
        async with self._port_lock:
            port = self._rtp_port_counter
            self._rtp_port_counter += 2  # RTP uses even ports
            if self._rtp_port_counter > RTP_PORT_END:
                self._rtp_port_counter = RTP_PORT_START
            return port
    
    async def _on_call_start(self, channel: ARIChannel) -> None:
        """Handle incoming call"""
        call_id = f"ari-{channel.id[:8]}"
        logger.info(f"[{call_id}] 📞 Incoming call from {channel.caller_number}")
        
        try:
            record_call_started()
            
            # Answer the call
            await self.ari.answer_channel(channel.id)
            
            # Create mixing bridge
            bridge_id = await self.ari.create_bridge("mixing")
            
            # Add caller to bridge
            await self.ari.add_channel_to_bridge(bridge_id, channel.id)
            
            # Get RTP port for this call
            rtp_port = await self._get_next_rtp_port()
            
            # Create UDP socket for RTP
            rtp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            rtp_socket.setblocking(False)
            rtp_socket.bind((RTP_BIND_HOST, rtp_port))
            
            logger.info(f"[{call_id}] 📡 RTP socket bound to {RTP_BIND_HOST}:{rtp_port}")
            
            # Create ExternalMedia channel pointing at our RTP socket
            external_host = f"{RTP_BIND_HOST}:{rtp_port}"
            external_media = await self.ari.create_external_media(
                app=ARI_APP,
                external_host=external_host,
                audio_format="slin16",
                direction="both",
            )
            
            # Add ExternalMedia to bridge
            await self.ari.add_channel_to_bridge(bridge_id, external_media.channel_id)
            
            # Create RTP session
            rtp_session = RTPSession(sample_rate=RATE_SLIN16)
            
            # Create OpenAI client
            openai_client = OpenAIRealtimeClient(
                call_id=call_id,
                caller_phone=channel.caller_number,
            )
            
            # Create session
            session = CallSession(
                call_id=call_id,
                caller_phone=channel.caller_number,
                channel_id=channel.id,
                bridge_id=bridge_id,
                external_media=external_media,
                rtp_session=rtp_session,
                openai_client=openai_client,
                rtp_socket=rtp_socket,
                asterisk_rtp_address=(external_media.local_address, external_media.local_port),
            )
            
            self.sessions[channel.id] = session
            
            # Set up callbacks
            openai_client.on_audio(lambda audio: self._send_audio_to_asterisk(session, audio))
            openai_client.on_call_end(lambda: self._end_call(session))
            openai_client.on_transfer(lambda reason: self._transfer_call(session, reason))
            
            # Connect to OpenAI
            if not await openai_client.connect():
                logger.error(f"[{call_id}] Failed to connect to OpenAI")
                await self._cleanup_session(session)
                return
            
            # Start audio processing tasks
            asyncio.create_task(self._rtp_receive_loop(session))
            asyncio.create_task(openai_client.run())
            
            logger.info(f"[{call_id}] ✅ Call setup complete")
            
        except Exception as e:
            logger.error(f"[{call_id}] ❌ Call setup failed: {e}")
            if channel.id in self.sessions:
                await self._cleanup_session(self.sessions[channel.id])
    
    async def _on_call_end(self, channel_id: str) -> None:
        """Handle call ended by Asterisk"""
        session = self.sessions.get(channel_id)
        if session:
            logger.info(f"[{session.call_id}] 📴 Call ended by Asterisk")
            await self._cleanup_session(session)
    
    async def _rtp_receive_loop(self, session: CallSession) -> None:
        """Receive RTP from Asterisk and send to OpenAI"""
        loop = asyncio.get_event_loop()
        
        logger.info(f"[{session.call_id}] 🎧 RTP receive loop started")
        
        try:
            while not session.ended and self._running:
                try:
                    # Non-blocking receive with timeout
                    data, addr = await asyncio.wait_for(
                        loop.sock_recvfrom(session.rtp_socket, 2048),
                        timeout=5.0,
                    )
                    
                    # Extract PCM payload from RTP
                    payload = session.rtp_session.depacketize(data)
                    if payload:
                        session.rtp_packets_received += 1
                        record_rtp_in(1, len(payload))
                        
                        # Send to OpenAI (it expects 24kHz, we have 16kHz)
                        await session.openai_client.send_audio(payload)
                        
                except asyncio.TimeoutError:
                    # No data, check if still running
                    continue
                except Exception as e:
                    if not session.ended:
                        logger.warning(f"[{session.call_id}] RTP receive error: {e}")
                    break
                    
        except Exception as e:
            logger.error(f"[{session.call_id}] RTP loop error: {e}")
        
        logger.info(f"[{session.call_id}] 🔇 RTP receive loop ended")
    
    async def _send_audio_to_asterisk(self, session: CallSession, pcm24_audio: bytes) -> None:
        """Send audio from OpenAI to Asterisk via RTP"""
        if session.ended:
            return
        
        try:
            # Resample 24kHz → 16kHz
            pcm16_audio = resample_audio(pcm24_audio, RATE_AI, RATE_SLIN16)
            
            # Packetize into RTP frames
            rtp_packets = session.rtp_session.packetize(pcm16_audio)
            
            loop = asyncio.get_event_loop()
            
            for packet in rtp_packets:
                await loop.sock_sendto(
                    session.rtp_socket,
                    packet,
                    session.asterisk_rtp_address,
                )
                session.rtp_packets_sent += 1
                record_rtp_out(1, len(packet))
                
                # Pace packets at ~20ms intervals
                await asyncio.sleep(FRAME_MS / 1000.0 * 0.9)  # Slightly faster to prevent underrun
                
        except Exception as e:
            if not session.ended:
                logger.warning(f"[{session.call_id}] RTP send error: {e}")
    
    async def _end_call(self, session: CallSession) -> None:
        """End call triggered by AI"""
        if session.ended:
            return
        
        logger.info(f"[{session.call_id}] 🔚 AI requested call end")
        
        # Give time for goodbye audio to play
        await asyncio.sleep(2.0)
        
        await self._cleanup_session(session)
    
    async def _transfer_call(self, session: CallSession, reason: str) -> None:
        """Transfer call to human operator"""
        logger.info(f"[{session.call_id}] 🔄 Transferring to operator: {reason}")
        
        try:
            # Continue to operator extension in dialplan
            await self.ari.continue_channel(session.channel_id, "internal", "1001")
        except Exception as e:
            logger.error(f"[{session.call_id}] Transfer failed: {e}")
    
    async def _cleanup_session(self, session: CallSession) -> None:
        """Clean up call session"""
        if session.ended:
            return
        
        session.ended = True
        duration = time.time() - session.started_at
        
        logger.info(f"[{session.call_id}] 🧹 Cleaning up (duration: {duration:.1f}s)")
        
        # Disconnect OpenAI
        await session.openai_client.disconnect()
        
        # Close RTP socket
        try:
            session.rtp_socket.close()
        except Exception:
            pass
        
        # Destroy bridge
        await self.ari.destroy_bridge(session.bridge_id)
        
        # Hang up caller
        await self.ari.hangup_channel(session.channel_id)
        
        # Remove from sessions
        if session.channel_id in self.sessions:
            del self.sessions[session.channel_id]
        
        # Record metrics
        record_call_ended("completed", duration)
        record_openai_audio(
            session.openai_client.session.audio_sent_bytes,
            session.openai_client.session.audio_received_bytes,
        )
        
        logger.info(f"[{session.call_id}] 📊 Stats: RTP in={session.rtp_packets_received}, "
                    f"RTP out={session.rtp_packets_sent}")


async def main() -> None:
    """Main entry point"""
    bridge = ARIBridge()
    
    # Handle signals
    loop = asyncio.get_event_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, lambda: asyncio.create_task(bridge.stop()))
    
    print(f"""
🚀 ARI Bridge v{ARIBridge.VERSION}
   App:     {ARI_APP}
   RTP:     {RTP_BIND_HOST}:{RTP_PORT_START}-{RTP_PORT_END}
   Format:  slin16 (16kHz PCM16)
   OpenAI:  Direct Realtime API
    """)
    
    try:
        await bridge.start()
    except KeyboardInterrupt:
        pass
    finally:
        await bridge.stop()


if __name__ == "__main__":
    asyncio.run(main())
