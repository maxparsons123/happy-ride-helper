"""
RTP Packetizer/Depacketizer for PCM16 @ 16kHz

Handles 20ms frames (320 samples = 640 bytes payload)
"""
import struct
import random
import time
from dataclasses import dataclass, field
from typing import Optional, Tuple
import logging

from .settings import (
    RTP_HEADER_SIZE,
    RTP_PAYLOAD_TYPE_L16,
    SAMPLES_PER_FRAME_16K,
    BYTES_PER_FRAME_16K,
    FRAME_MS,
)

logger = logging.getLogger("RTP")


@dataclass
class RTPPacket:
    """Represents an RTP packet"""
    version: int = 2
    padding: bool = False
    extension: bool = False
    csrc_count: int = 0
    marker: bool = False
    payload_type: int = RTP_PAYLOAD_TYPE_L16
    sequence: int = 0
    timestamp: int = 0
    ssrc: int = 0
    payload: bytes = b""

    def to_bytes(self) -> bytes:
        """Serialize RTP packet to bytes"""
        # First byte: V=2, P, X, CC
        byte0 = (self.version << 6) | (int(self.padding) << 5) | (int(self.extension) << 4) | self.csrc_count
        # Second byte: M, PT
        byte1 = (int(self.marker) << 7) | (self.payload_type & 0x7F)
        
        header = struct.pack(
            ">BBHII",
            byte0,
            byte1,
            self.sequence & 0xFFFF,
            self.timestamp & 0xFFFFFFFF,
            self.ssrc & 0xFFFFFFFF,
        )
        return header + self.payload

    @classmethod
    def from_bytes(cls, data: bytes) -> Optional["RTPPacket"]:
        """Parse RTP packet from bytes"""
        if len(data) < RTP_HEADER_SIZE:
            return None
        
        byte0, byte1, seq, ts, ssrc = struct.unpack(">BBHII", data[:RTP_HEADER_SIZE])
        
        version = (byte0 >> 6) & 0x03
        if version != 2:
            return None
        
        padding = bool((byte0 >> 5) & 0x01)
        extension = bool((byte0 >> 4) & 0x01)
        csrc_count = byte0 & 0x0F
        marker = bool((byte1 >> 7) & 0x01)
        payload_type = byte1 & 0x7F
        
        # Skip CSRC list and extension if present
        header_size = RTP_HEADER_SIZE + (csrc_count * 4)
        if extension:
            if len(data) < header_size + 4:
                return None
            ext_len = struct.unpack(">H", data[header_size + 2:header_size + 4])[0]
            header_size += 4 + (ext_len * 4)
        
        payload = data[header_size:]
        
        # Handle padding
        if padding and payload:
            pad_len = payload[-1]
            payload = payload[:-pad_len]
        
        return cls(
            version=version,
            padding=padding,
            extension=extension,
            csrc_count=csrc_count,
            marker=marker,
            payload_type=payload_type,
            sequence=seq,
            timestamp=ts,
            ssrc=ssrc,
            payload=payload,
        )


class RTPSession:
    """
    Manages RTP session state for sending/receiving audio
    
    - Handles sequence numbers and timestamps
    - Provides packetization for outbound audio
    - Extracts payload from inbound RTP
    """
    
    def __init__(self, ssrc: Optional[int] = None, sample_rate: int = 16000):
        self.ssrc = ssrc or random.randint(1, 0xFFFFFFFF)
        self.sample_rate = sample_rate
        self.sequence = random.randint(0, 0xFFFF)
        self.timestamp = random.randint(0, 0xFFFFFFFF)
        self.samples_per_frame = int(sample_rate * FRAME_MS / 1000)
        self.bytes_per_frame = self.samples_per_frame * 2  # PCM16
        
        # Stats
        self.packets_sent = 0
        self.packets_received = 0
        self.bytes_sent = 0
        self.bytes_received = 0
        
        logger.info(f"RTP session created: SSRC={self.ssrc:08X}, rate={sample_rate}Hz, "
                    f"frame={self.samples_per_frame} samples ({self.bytes_per_frame} bytes)")
    
    def packetize(self, pcm16_data: bytes, marker: bool = False) -> list[bytes]:
        """
        Convert PCM16 audio data into RTP packets (20ms frames)
        
        Args:
            pcm16_data: Raw PCM16 audio bytes
            marker: Set marker bit on first packet (for stream start)
        
        Returns:
            List of RTP packet bytes ready to send
        """
        packets = []
        offset = 0
        first_packet = True
        
        while offset < len(pcm16_data):
            # Extract one frame worth of samples
            frame_end = min(offset + self.bytes_per_frame, len(pcm16_data))
            payload = pcm16_data[offset:frame_end]
            
            # Pad short frames with silence
            if len(payload) < self.bytes_per_frame:
                payload = payload + bytes(self.bytes_per_frame - len(payload))
            
            packet = RTPPacket(
                payload_type=RTP_PAYLOAD_TYPE_L16,
                sequence=self.sequence,
                timestamp=self.timestamp,
                ssrc=self.ssrc,
                marker=marker and first_packet,
                payload=payload,
            )
            
            packets.append(packet.to_bytes())
            
            # Update state
            self.sequence = (self.sequence + 1) & 0xFFFF
            self.timestamp = (self.timestamp + self.samples_per_frame) & 0xFFFFFFFF
            self.packets_sent += 1
            self.bytes_sent += len(payload)
            
            offset = frame_end
            first_packet = False
        
        return packets
    
    def depacketize(self, rtp_data: bytes) -> Optional[bytes]:
        """
        Extract PCM16 payload from RTP packet
        
        Args:
            rtp_data: Raw RTP packet bytes
        
        Returns:
            PCM16 audio payload, or None if invalid
        """
        packet = RTPPacket.from_bytes(rtp_data)
        if packet is None:
            return None
        
        self.packets_received += 1
        self.bytes_received += len(packet.payload)
        
        return packet.payload
    
    def get_stats(self) -> dict:
        """Get session statistics"""
        return {
            "ssrc": f"{self.ssrc:08X}",
            "packets_sent": self.packets_sent,
            "packets_received": self.packets_received,
            "bytes_sent": self.bytes_sent,
            "bytes_received": self.bytes_received,
        }
