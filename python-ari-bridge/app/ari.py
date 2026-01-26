"""
Minimal ARI (Asterisk REST Interface) Client

Handles:
- REST API calls for channel/bridge/external media control
- WebSocket event stream for StasisStart/StasisEnd events
"""
import asyncio
import json
import logging
from typing import Optional, Callable, Any, Awaitable
from dataclasses import dataclass
import aiohttp

from .settings import ARI_URL, ARI_USER, ARI_PASSWORD, ARI_APP

logger = logging.getLogger("ARI")


@dataclass
class ARIChannel:
    """Represents an Asterisk channel"""
    id: str
    name: str
    state: str
    caller_number: str
    caller_name: str
    connected_number: str
    dialplan: dict


@dataclass
class ExternalMediaChannel:
    """Represents an ExternalMedia channel"""
    channel_id: str
    local_address: str
    local_port: int


class ARIClient:
    """
    Async ARI client for call control and ExternalMedia management
    """
    
    def __init__(
        self,
        url: str = ARI_URL,
        username: str = ARI_USER,
        password: str = ARI_PASSWORD,
        app: str = ARI_APP,
    ):
        self.base_url = url.rstrip("/")
        self.auth = aiohttp.BasicAuth(username, password)
        self.app = app
        self._session: Optional[aiohttp.ClientSession] = None
        self._ws: Optional[aiohttp.ClientWebSocketResponse] = None
        self._running = False
        
        # Event handlers
        self._on_stasis_start: Optional[Callable[[ARIChannel], Awaitable[None]]] = None
        self._on_stasis_end: Optional[Callable[[str], Awaitable[None]]] = None
    
    async def connect(self) -> None:
        """Connect to ARI REST API and WebSocket event stream"""
        self._session = aiohttp.ClientSession(auth=self.auth)
        
        # Connect to WebSocket for events
        ws_url = f"{self.base_url.replace('http', 'ws')}/ari/events?app={self.app}&subscribeAll=true"
        logger.info(f"Connecting to ARI WebSocket: {ws_url}")
        
        self._ws = await self._session.ws_connect(ws_url)
        self._running = True
        logger.info("✅ Connected to ARI")
    
    async def disconnect(self) -> None:
        """Disconnect from ARI"""
        self._running = False
        if self._ws:
            await self._ws.close()
        if self._session:
            await self._session.close()
        logger.info("Disconnected from ARI")
    
    def on_stasis_start(self, handler: Callable[[ARIChannel], Awaitable[None]]) -> None:
        """Register handler for StasisStart events (incoming calls)"""
        self._on_stasis_start = handler
    
    def on_stasis_end(self, handler: Callable[[str], Awaitable[None]]) -> None:
        """Register handler for StasisEnd events (call ended)"""
        self._on_stasis_end = handler
    
    async def run_event_loop(self) -> None:
        """Process ARI WebSocket events"""
        if not self._ws:
            raise RuntimeError("Not connected to ARI")
        
        logger.info(f"🎧 Listening for calls on app: {self.app}")
        
        async for msg in self._ws:
            if not self._running:
                break
            
            if msg.type == aiohttp.WSMsgType.TEXT:
                try:
                    event = json.loads(msg.data)
                    await self._handle_event(event)
                except json.JSONDecodeError as e:
                    logger.warning(f"Invalid JSON from ARI: {e}")
            elif msg.type == aiohttp.WSMsgType.ERROR:
                logger.error(f"ARI WebSocket error: {self._ws.exception()}")
                break
            elif msg.type == aiohttp.WSMsgType.CLOSED:
                logger.info("ARI WebSocket closed")
                break
    
    async def _handle_event(self, event: dict) -> None:
        """Handle an ARI event"""
        event_type = event.get("type")
        
        if event_type == "StasisStart":
            channel_data = event.get("channel", {})
            caller = channel_data.get("caller", {})
            
            channel = ARIChannel(
                id=channel_data.get("id", ""),
                name=channel_data.get("name", ""),
                state=channel_data.get("state", ""),
                caller_number=caller.get("number", "unknown"),
                caller_name=caller.get("name", ""),
                connected_number=channel_data.get("connected", {}).get("number", ""),
                dialplan=channel_data.get("dialplan", {}),
            )
            
            logger.info(f"📞 StasisStart: {channel.id} from {channel.caller_number}")
            
            if self._on_stasis_start:
                asyncio.create_task(self._on_stasis_start(channel))
        
        elif event_type == "StasisEnd":
            channel_id = event.get("channel", {}).get("id", "")
            logger.info(f"📴 StasisEnd: {channel_id}")
            
            if self._on_stasis_end:
                asyncio.create_task(self._on_stasis_end(channel_id))
        
        elif event_type == "ChannelDestroyed":
            channel_id = event.get("channel", {}).get("id", "")
            logger.debug(f"Channel destroyed: {channel_id}")
        
        elif event_type == "ChannelDtmfReceived":
            channel_id = event.get("channel", {}).get("id", "")
            digit = event.get("digit", "")
            logger.info(f"📱 DTMF: {digit} on {channel_id}")
    
    async def _api_call(
        self,
        method: str,
        endpoint: str,
        params: Optional[dict] = None,
        json_body: Optional[dict] = None,
    ) -> dict:
        """Make an ARI API call"""
        if not self._session:
            raise RuntimeError("Not connected to ARI")
        
        url = f"{self.base_url}/ari/{endpoint.lstrip('/')}"
        
        async with self._session.request(
            method, url, params=params, json=json_body
        ) as resp:
            if resp.status >= 400:
                text = await resp.text()
                raise RuntimeError(f"ARI error {resp.status}: {text}")
            
            if resp.content_length and resp.content_length > 0:
                return await resp.json()
            return {}
    
    async def answer_channel(self, channel_id: str) -> None:
        """Answer a channel"""
        await self._api_call("POST", f"channels/{channel_id}/answer")
        logger.debug(f"Answered channel: {channel_id}")
    
    async def hangup_channel(self, channel_id: str, reason: str = "normal") -> None:
        """Hang up a channel"""
        try:
            await self._api_call("DELETE", f"channels/{channel_id}", params={"reason": reason})
            logger.debug(f"Hung up channel: {channel_id}")
        except Exception as e:
            logger.warning(f"Hangup failed for {channel_id}: {e}")
    
    async def create_bridge(self, bridge_type: str = "mixing") -> str:
        """Create a mixing bridge, returns bridge ID"""
        result = await self._api_call("POST", "bridges", params={"type": bridge_type})
        bridge_id = result.get("id", "")
        logger.debug(f"Created bridge: {bridge_id}")
        return bridge_id
    
    async def add_channel_to_bridge(self, bridge_id: str, channel_id: str) -> None:
        """Add a channel to a bridge"""
        await self._api_call(
            "POST", f"bridges/{bridge_id}/addChannel",
            params={"channel": channel_id}
        )
        logger.debug(f"Added {channel_id} to bridge {bridge_id}")
    
    async def destroy_bridge(self, bridge_id: str) -> None:
        """Destroy a bridge"""
        try:
            await self._api_call("DELETE", f"bridges/{bridge_id}")
            logger.debug(f"Destroyed bridge: {bridge_id}")
        except Exception as e:
            logger.warning(f"Bridge destroy failed: {e}")
    
    async def create_external_media(
        self,
        app: str,
        external_host: str,
        audio_format: str = "slin16",
        direction: str = "both",
    ) -> ExternalMediaChannel:
        """
        Create an ExternalMedia channel
        
        This creates a channel that sends/receives RTP to/from the specified host.
        The channel's UNICASTRTP_LOCAL_* variables tell us where to send audio back.
        
        Args:
            app: Stasis application name
            external_host: Where to send RTP (e.g., "127.0.0.1:30000")
            audio_format: Audio format (slin16 for 16kHz PCM)
            direction: "both", "in", or "out"
        
        Returns:
            ExternalMediaChannel with local address/port for sending audio back
        """
        result = await self._api_call(
            "POST", "channels/externalMedia",
            params={
                "app": app,
                "external_host": external_host,
                "format": audio_format,
                "direction": direction,
            }
        )
        
        channel_id = result.get("id", "")
        
        # Get the channel variables to find where to send audio back
        vars_result = await self._api_call("GET", f"channels/{channel_id}/variable", params={"variable": "UNICASTRTP_LOCAL_ADDRESS"})
        local_address = vars_result.get("value", "127.0.0.1")
        
        vars_result = await self._api_call("GET", f"channels/{channel_id}/variable", params={"variable": "UNICASTRTP_LOCAL_PORT"})
        local_port = int(vars_result.get("value", "0"))
        
        logger.info(f"📡 ExternalMedia created: {channel_id} → {local_address}:{local_port}")
        
        return ExternalMediaChannel(
            channel_id=channel_id,
            local_address=local_address,
            local_port=local_port,
        )
    
    async def continue_channel(self, channel_id: str, context: str, extension: str, priority: int = 1) -> None:
        """Continue channel in dialplan (for transfers)"""
        await self._api_call(
            "POST", f"channels/{channel_id}/continue",
            params={"context": context, "extension": extension, "priority": priority}
        )
        logger.info(f"Continued {channel_id} to {context},{extension},{priority}")
