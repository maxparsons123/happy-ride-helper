"""
Configuration settings for Python ARI Bridge
"""
import os
import json

def _env_bool(name: str, default: bool = False) -> bool:
    val = os.environ.get(name, "").strip().lower()
    return val in ("1", "true", "yes", "on") if val else default

def _env_int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, default))
    except (ValueError, TypeError):
        return default

def _env_float(name: str, default: float) -> float:
    try:
        return float(os.environ.get(name, default))
    except (ValueError, TypeError):
        return default

# OpenAI Configuration
OPENAI_API_KEY = os.environ.get("OPENAI_API_KEY", "")
OPENAI_MODEL = os.environ.get("OPENAI_MODEL", "gpt-4o-mini-realtime-preview-2024-12-17")
OPENAI_VOICE = os.environ.get("OPENAI_VOICE", "shimmer")
OPENAI_WS_URL = f"wss://api.openai.com/v1/realtime?model={OPENAI_MODEL}"

# ARI Configuration
ARI_URL = os.environ.get("ARI_URL", "http://localhost:8088")
ARI_USER = os.environ.get("ARI_USER", "voiceapp")
ARI_PASSWORD = os.environ.get("ARI_PASSWORD", "CHANGE_ME")
ARI_APP = os.environ.get("ARI_APP", "voice-agent")

# RTP Configuration
RTP_BIND_HOST = os.environ.get("RTP_BIND_HOST", "127.0.0.1")
RTP_PORT_START = _env_int("RTP_PORT_START", 30000)
RTP_PORT_END = _env_int("RTP_PORT_END", 30100)

# Audio Rates (Hz)
RATE_SLIN16 = 16000  # ExternalMedia format
RATE_AI = 24000       # OpenAI Realtime API format

# Audio Frame Configuration
FRAME_MS = 20         # 20ms frames (standard)
SAMPLES_PER_FRAME_16K = int(RATE_SLIN16 * FRAME_MS / 1000)  # 320 samples
SAMPLES_PER_FRAME_24K = int(RATE_AI * FRAME_MS / 1000)      # 480 samples
BYTES_PER_FRAME_16K = SAMPLES_PER_FRAME_16K * 2             # 640 bytes (PCM16)
BYTES_PER_FRAME_24K = SAMPLES_PER_FRAME_24K * 2             # 960 bytes (PCM16)

# RTP Constants
RTP_HEADER_SIZE = 12
RTP_PAYLOAD_TYPE_L16 = 11  # L16/16000 mono (RFC 3551)

# VAD Configuration (for OpenAI server VAD)
VAD_THRESHOLD = _env_float("VAD_THRESHOLD", 0.35)
VAD_PREFIX_PADDING_MS = _env_int("VAD_PREFIX_PADDING_MS", 400)
VAD_SILENCE_DURATION_MS = _env_int("VAD_SILENCE_DURATION_MS", 800)

# Warmup Configuration
WARMUP_SILENCE_MS = _env_int("WARMUP_SILENCE_MS", 200)  # Send silence at start to stabilize VAD

# DSP Settings
VOLUME_BOOST = _env_float("VOLUME_BOOST", 2.5)
PRE_EMPHASIS_COEFF = _env_float("PRE_EMPHASIS_COEFF", 0.97)

# Prometheus Metrics
METRICS_PORT = _env_int("METRICS_PORT", 9090)
ENABLE_METRICS = _env_bool("ENABLE_METRICS", True)

# Logging
LOG_LEVEL = os.environ.get("LOG_LEVEL", "INFO")

# System Prompt for OpenAI
SYSTEM_PROMPT = os.environ.get("SYSTEM_PROMPT", """
You are Ada, a friendly and efficient AI receptionist for a taxi company.
Your job is to help callers book taxis by collecting:
1. Pickup location
2. Destination
3. Number of passengers
4. Preferred pickup time

Be concise, warm, and professional. Confirm details before finalizing.
If the caller wants to speak to a human operator, use the transfer_to_operator tool.
When the booking is complete or the caller wants to end the call, use the end_call tool.
""".strip())
