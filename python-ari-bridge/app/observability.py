"""
Prometheus metrics and health endpoints
"""
import asyncio
import logging
from typing import Optional
from aiohttp import web

try:
    from prometheus_client import Counter, Gauge, Histogram, generate_latest, CONTENT_TYPE_LATEST
    PROMETHEUS_AVAILABLE = True
except ImportError:
    PROMETHEUS_AVAILABLE = False

from .settings import METRICS_PORT, ENABLE_METRICS

logger = logging.getLogger("Metrics")


# Define metrics if Prometheus is available
if PROMETHEUS_AVAILABLE:
    # Counters
    CALLS_TOTAL = Counter(
        "ari_bridge_calls_total",
        "Total number of calls handled",
        ["status"],  # completed, failed, transferred
    )
    
    RTP_PACKETS_IN = Counter(
        "ari_bridge_rtp_packets_in_total",
        "Total RTP packets received from Asterisk",
    )
    
    RTP_PACKETS_OUT = Counter(
        "ari_bridge_rtp_packets_out_total",
        "Total RTP packets sent to Asterisk",
    )
    
    RTP_BYTES_IN = Counter(
        "ari_bridge_rtp_bytes_in_total",
        "Total RTP bytes received from Asterisk",
    )
    
    RTP_BYTES_OUT = Counter(
        "ari_bridge_rtp_bytes_out_total",
        "Total RTP bytes sent to Asterisk",
    )
    
    OPENAI_AUDIO_BYTES_IN = Counter(
        "ari_bridge_openai_audio_bytes_in_total",
        "Total audio bytes sent to OpenAI",
    )
    
    OPENAI_AUDIO_BYTES_OUT = Counter(
        "ari_bridge_openai_audio_bytes_out_total",
        "Total audio bytes received from OpenAI",
    )
    
    # Gauges
    ACTIVE_CALLS = Gauge(
        "ari_bridge_active_calls",
        "Current number of active calls",
    )
    
    # Histograms
    CALL_DURATION = Histogram(
        "ari_bridge_call_duration_seconds",
        "Call duration in seconds",
        buckets=[5, 15, 30, 60, 120, 300, 600],
    )
    
    OPENAI_LATENCY = Histogram(
        "ari_bridge_openai_latency_seconds",
        "OpenAI response latency",
        buckets=[0.1, 0.25, 0.5, 1.0, 2.0, 5.0],
    )


class MetricsServer:
    """Simple HTTP server for Prometheus metrics and health checks"""
    
    def __init__(self, port: int = METRICS_PORT):
        self.port = port
        self._app: Optional[web.Application] = None
        self._runner: Optional[web.AppRunner] = None
        self._site: Optional[web.TCPSite] = None
    
    async def start(self) -> None:
        """Start the metrics server"""
        if not ENABLE_METRICS:
            logger.info("Metrics disabled")
            return
        
        self._app = web.Application()
        self._app.router.add_get("/metrics", self._metrics_handler)
        self._app.router.add_get("/healthz", self._health_handler)
        self._app.router.add_get("/readyz", self._ready_handler)
        
        self._runner = web.AppRunner(self._app)
        await self._runner.setup()
        
        self._site = web.TCPSite(self._runner, "0.0.0.0", self.port)
        await self._site.start()
        
        logger.info(f"📊 Metrics server started on :{self.port}")
    
    async def stop(self) -> None:
        """Stop the metrics server"""
        if self._runner:
            await self._runner.cleanup()
    
    async def _metrics_handler(self, request: web.Request) -> web.Response:
        """Handle /metrics endpoint"""
        if PROMETHEUS_AVAILABLE:
            return web.Response(
                body=generate_latest(),
                content_type=CONTENT_TYPE_LATEST,
            )
        return web.Response(text="Prometheus not available", status=501)
    
    async def _health_handler(self, request: web.Request) -> web.Response:
        """Handle /healthz endpoint (liveness)"""
        return web.Response(text="OK")
    
    async def _ready_handler(self, request: web.Request) -> web.Response:
        """Handle /readyz endpoint (readiness)"""
        # Could add checks for ARI connection, etc.
        return web.Response(text="OK")


# Helper functions to record metrics
def record_call_started():
    if PROMETHEUS_AVAILABLE:
        ACTIVE_CALLS.inc()


def record_call_ended(status: str, duration_seconds: float):
    if PROMETHEUS_AVAILABLE:
        ACTIVE_CALLS.dec()
        CALLS_TOTAL.labels(status=status).inc()
        CALL_DURATION.observe(duration_seconds)


def record_rtp_in(packets: int, bytes_count: int):
    if PROMETHEUS_AVAILABLE:
        RTP_PACKETS_IN.inc(packets)
        RTP_BYTES_IN.inc(bytes_count)


def record_rtp_out(packets: int, bytes_count: int):
    if PROMETHEUS_AVAILABLE:
        RTP_PACKETS_OUT.inc(packets)
        RTP_BYTES_OUT.inc(bytes_count)


def record_openai_audio(bytes_in: int, bytes_out: int):
    if PROMETHEUS_AVAILABLE:
        OPENAI_AUDIO_BYTES_IN.inc(bytes_in)
        OPENAI_AUDIO_BYTES_OUT.inc(bytes_out)


def record_openai_latency(latency_seconds: float):
    if PROMETHEUS_AVAILABLE:
        OPENAI_LATENCY.observe(latency_seconds)
