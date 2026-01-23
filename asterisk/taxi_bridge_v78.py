#!/usr/bin/env python3
"""
Taxi AI Asterisk Bridge v7.8
SLIN8-FREE EDITION

Allowed formats ONLY:
  - ulaw   @ 8000 Hz
  - slin16 @ 16000 Hz
  - opus   @ 48000 Hz

slin / slin8 is COMPLETELY FORBIDDEN
"""

import asyncio
import base64
import json
import logging
import os
import struct
import time
from collections import deque
from dataclasses import dataclass, field
from math import gcd
from typing import Deque, Optional, Tuple

import numpy as np
from scipy.signal import resample_poly
import websockets
from websockets.exceptions import ConnectionClosed, WebSocketException

# =============================================================================
# CONSTANTS
# =============================================================================

RATE_ULAW   = 8000
RATE_SLIN16 = 16000
RATE_OPUS   = 48000
RATE_AI     = 24000

FRAME_MS = 20

AUDIOSOCKET_HOST = "0.0.0.0"
AUDIOSOCKET_PORT = int(os.environ.get("AUDIOSOCKET_PORT", 9092))

MSG_HANGUP = 0x00
MSG_UUID   = 0x01
MSG_AUDIO  = 0x10

AUDIO_QUEUE_MAXLEN = 200

# =============================================================================
# LOGGING
# =============================================================================

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    force=True,
)
log = logging.getLogger("TaxiBridge")

# =============================================================================
# OPUS (optional)
# =============================================================================

try:
    import opuslib
    from opuslib import Encoder as OpusEncoder, Decoder as OpusDecoder
    OPUS_AVAILABLE = True
except ImportError:
    OPUS_AVAILABLE = False
    OpusEncoder = None
    OpusDecoder = None

class OpusCodec:
    def __init__(self):
        self.frame_samples = int(RATE_OPUS * FRAME_MS / 1000)
        self.encoder = OpusEncoder(RATE_OPUS, 1, 2048)
        self.encoder.bitrate = 32000
        self.encoder.vbr = True
        self.decoder = OpusDecoder(RATE_OPUS, 1)

    def decode(self, data: bytes) -> bytes:
        return self.decoder.decode(data, self.frame_samples)

    def encode(self, pcm16: bytes, from_rate: int) -> bytes:
        if from_rate != RATE_OPUS:
            pcm16 = resample(pcm16, from_rate, RATE_OPUS)
        samples = np.frombuffer(pcm16, np.int16)
        samples = samples[:self.frame_samples]
        if len(samples) < self.frame_samples:
            samples = np.pad(samples, (0, self.frame_samples - len(samples)))
        return self.encoder.encode(samples.tobytes(), self.frame_samples)

# =============================================================================
# DSP
# =============================================================================

def resample(pcm: bytes, src: int, dst: int) -> bytes:
    if src == dst:
        return pcm
    samples = np.frombuffer(pcm, np.int16).astype(np.float32)
    g = gcd(src, dst)
    out = resample_poly(samples, dst // g, src // g)
    return np.clip(out, -32768, 32767).astype(np.int16).tobytes()

def ulaw_to_pcm(ulaw: bytes) -> bytes:
    import audioop
    return audioop.ulaw2lin(ulaw, 2)

def pcm_to_ulaw(pcm: bytes) -> bytes:
    import audioop
    return audioop.lin2ulaw(pcm, 2)

# =============================================================================
# STATE
# =============================================================================

@dataclass
class CallState:
    call_id: str
    codec: str = "ulaw"       # ulaw | slin16 | opus
    rate: int = RATE_ULAW
    frame_bytes: int = 160
    last_ast_rx: float = field(default_factory=time.time)

# =============================================================================
# BRIDGE
# =============================================================================

class TaxiBridge:
    def __init__(self, r: asyncio.StreamReader, w: asyncio.StreamWriter):
        self.r = r
        self.w = w
        self.state = CallState(call_id=str(int(time.time() * 1000)))
        self.queue: Deque[bytes] = deque(maxlen=AUDIO_QUEUE_MAXLEN)
        self.opus = OpusCodec() if OPUS_AVAILABLE else None
        self.running = True

    # ---------------- FORMAT DETECTION ----------------

    def detect_format(self, size: int, payload: bytes):
        if size == 160:
            self._set("ulaw", RATE_ULAW, 160)
        elif size in (320, 640):
            self._set("slin16", RATE_SLIN16, 640)
        else:
            if self.opus and size < 400:
                self._set("opus", RATE_OPUS, size)
            else:
                self._set("slin16", RATE_SLIN16, 640)

    def _set(self, codec, rate, frame):
        if codec != self.state.codec:
            log.info("[%s] FORMAT %s @ %dHz", self.state.call_id, codec, rate)
        self.state.codec = codec
        self.state.rate = rate
        self.state.frame_bytes = frame

    # ---------------- ASTERISK → AI ----------------

    async def ast_to_ai(self):
        while self.running:
            hdr = await self.r.readexactly(3)
            typ = hdr[0]
            ln = struct.unpack(">H", hdr[1:])[0]
            data = await self.r.readexactly(ln)

            if typ == MSG_AUDIO:
                self.detect_format(ln, data)

                if self.state.codec == "ulaw":
                    pcm = ulaw_to_pcm(data)
                elif self.state.codec == "opus":
                    pcm = self.opus.decode(data)
                    pcm = resample(pcm, RATE_OPUS, RATE_AI)
                else:
                    pcm = data

                self.queue.append(pcm)

            elif typ == MSG_HANGUP:
                self.running = False
                return

    # ---------------- AI → ASTERISK ----------------

    async def ai_to_ast(self):
        while self.running:
            if not self.queue:
                await asyncio.sleep(0.005)
                continue

            pcm = self.queue.popleft()

            if self.state.codec == "ulaw":
                out = pcm_to_ulaw(resample(pcm, RATE_AI, RATE_ULAW))
            elif self.state.codec == "opus":
                out = self.opus.encode(pcm, RATE_AI)
            else:
                out = resample(pcm, RATE_AI, RATE_SLIN16)

            frame = struct.pack(">BH", MSG_AUDIO, len(out)) + out
            self.w.write(frame)
            await self.w.drain()

    async def run(self):
        await asyncio.gather(self.ast_to_ai(), self.ai_to_ast())

# =============================================================================
# MAIN
# =============================================================================

async def main():
    server = await asyncio.start_server(
        lambda r, w: TaxiBridge(r, w).run(),
        AUDIOSOCKET_HOST,
        AUDIOSOCKET_PORT,
    )
    log.info("TaxiBridge v7.8 (slin8-free) listening on %s:%d",
             AUDIOSOCKET_HOST, AUDIOSOCKET_PORT)
    async with server:
        await server.serve_forever()

if __name__ == "__main__":
    asyncio.run(main())
