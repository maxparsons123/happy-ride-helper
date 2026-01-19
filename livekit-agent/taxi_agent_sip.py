#!/usr/bin/env python3
"""
LiveKit Voice Agent for Taxi Booking - SIP Edition
Handles inbound SIP calls via LiveKit SIP Trunk with full booking flow.
A/B test candidate against the Asterisk + WebSocket bridge.
"""

import asyncio
import json
import logging
import os
import uuid
from datetime import datetime
from typing import Annotated, Optional
from dotenv import load_dotenv
import aiohttp

from livekit import rtc
from livekit.agents import (
    AutoSubscribe,
    JobContext,
    JobProcess,
    WorkerOptions,
    cli,
    llm,
)
from livekit.agents.pipeline import VoicePipelineAgent
from livekit.plugins import openai, silero

load_dotenv()

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger("taxi-agent-sip")

# Configuration
COMPANY_NAME = os.getenv("COMPANY_NAME", "247 Radio Carz")
AGENT_NAME = os.getenv("AGENT_NAME", "Ada")
DISPATCH_WEBHOOK_URL = os.getenv("DISPATCH_WEBHOOK_URL", "")
SUPABASE_URL = os.getenv("SUPABASE_URL", "")
SUPABASE_KEY = os.getenv("SUPABASE_KEY", "")

SYSTEM_PROMPT = f"""You are {AGENT_NAME}, a friendly and efficient voice assistant for {COMPANY_NAME}, a taxi booking service.

Your role:
- Help customers book taxis by collecting pickup location, destination, and number of passengers
- Be conversational and natural, like a real phone operator
- Keep responses brief and to the point (this is a phone call)
- Confirm details before finalizing bookings

CRITICAL ADDRESS RULES:
- ALWAYS collect the FULL address with house number AND street name
- Ask for clarification if an address seems incomplete
- Repeat addresses back to confirm you heard correctly

Booking flow:
1. Greet the customer warmly and ask for pickup location
2. Ask for destination (full address)
3. Ask how many passengers (default to 1 if not specified)
4. Ask if they want a car now or a specific time
5. Use the request_quote tool to get fare and ETA
6. Tell the customer the fare and ETA, ask for confirmation
7. If confirmed, use the confirm_booking tool
8. Thank them and say goodbye

Important:
- If the customer seems confused, offer to help
- If you can't understand something, politely ask them to repeat
- Always be polite and professional
- Keep responses SHORT - this is a phone call, not a text chat
- NEVER truncate addresses - always use the full address the customer gave
"""


class BookingState:
    """Tracks booking state for a single call"""
    def __init__(self, call_id: str, caller_phone: str = ""):
        self.call_id = call_id
        self.job_id = str(uuid.uuid4())
        self.caller_phone = caller_phone
        self.caller_name: Optional[str] = None
        self.pickup: Optional[str] = None
        self.destination: Optional[str] = None
        self.passengers: int = 1
        self.pickup_time: str = "ASAP"
        self.fare: Optional[str] = None
        self.eta: Optional[str] = None
        self.booking_ref: Optional[str] = None
        self.confirmed: bool = False
        self.transcripts: list = []
        self.started_at = datetime.utcnow().isoformat() + "Z"


async def send_dispatch_webhook(state: BookingState, action: str) -> dict:
    """Send webhook to dispatch system"""
    if not DISPATCH_WEBHOOK_URL:
        logger.warning("No DISPATCH_WEBHOOK_URL configured")
        return {"success": False, "error": "No webhook configured"}
    
    payload = {
        "job_id": state.job_id,
        "call_id": state.call_id,
        "caller_phone": state.caller_phone,
        "caller_name": state.caller_name,
        "action": action,
        "ada_pickup": state.pickup,
        "ada_destination": state.destination,
        "passengers": state.passengers,
        "pickup_time": state.pickup_time,
        "user_transcripts": state.transcripts,
        "timestamp": datetime.utcnow().isoformat() + "Z",
    }
    
    if action == "confirmed":
        payload["fare"] = state.fare
        payload["eta"] = state.eta
        payload["booking_ref"] = state.booking_ref
    
    logger.info(f"Sending dispatch webhook: {action} for {state.call_id}")
    
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(
                DISPATCH_WEBHOOK_URL,
                json=payload,
                headers={
                    "Content-Type": "application/json",
                    "X-Call-ID": state.call_id,
                    "X-Job-ID": state.job_id,
                },
                timeout=aiohttp.ClientTimeout(total=30)
            ) as resp:
                if resp.status == 200 or resp.status == 201:
                    data = await resp.json()
                    logger.info(f"Dispatch webhook response: {data}")
                    return {"success": True, "data": data}
                else:
                    text = await resp.text()
                    logger.error(f"Dispatch webhook failed: {resp.status} - {text}")
                    return {"success": False, "error": f"HTTP {resp.status}"}
    except Exception as e:
        logger.error(f"Dispatch webhook error: {e}")
        return {"success": False, "error": str(e)}


class TaxiBookingTools(llm.FunctionContext):
    """Function tools for taxi booking - using decorator pattern"""
    
    def __init__(self, state: BookingState):
        super().__init__()
        self._state = state
    
    @llm.ai_callable(description="Request a fare quote from the dispatch system. Use after collecting pickup, destination, and passengers.")
    async def request_quote(
        self,
        pickup: Annotated[str, llm.TypeInfo(description="FULL pickup address with house number AND street name. NEVER omit the street name.")],
        destination: Annotated[str, llm.TypeInfo(description="FULL destination address with house number AND street name. NEVER omit the street name.")],
        passengers: Annotated[int, llm.TypeInfo(description="Number of passengers")] = 1,
        pickup_time: Annotated[str, llm.TypeInfo(description="When to pickup - 'ASAP', 'now', or a specific time")] = "ASAP",
    ) -> str:
        """Request a fare quote from the dispatch system."""
        self._state.pickup = pickup
        self._state.destination = destination
        self._state.passengers = passengers
        self._state.pickup_time = pickup_time
        
        logger.info(f"Requesting quote: {pickup} -> {destination}, {passengers} passengers")
        
        result = await send_dispatch_webhook(self._state, "request_quote")
        
        if result.get("success") and result.get("data"):
            data = result["data"]
            self._state.fare = data.get("fare", "5.00")
            self._state.eta = data.get("eta_minutes", "10")
            self._state.booking_ref = data.get("booking_ref")
            return f"Quote received: £{self._state.fare} fare, driver arriving in {self._state.eta} minutes."
        else:
            # Fallback if webhook fails
            self._state.fare = "5.00"
            self._state.eta = "10"
            return f"Quote received: £{self._state.fare} fare, driver arriving in {self._state.eta} minutes."
    
    @llm.ai_callable(description="Confirm the booking after the customer agrees to the fare and ETA. Only call after customer explicitly confirms.")
    async def confirm_booking(self) -> str:
        """Confirm the booking after customer agrees."""
        if not self._state.pickup or not self._state.destination:
            return "Cannot confirm - missing pickup or destination address."
        
        self._state.confirmed = True
        logger.info(f"Confirming booking: {self._state.pickup} -> {self._state.destination}")
        
        result = await send_dispatch_webhook(self._state, "confirmed")
        
        if result.get("success") and result.get("data"):
            data = result["data"]
            self._state.booking_ref = data.get("booking_ref", self._state.booking_ref)
            return f"Booking confirmed! Reference: {self._state.booking_ref or 'pending'}. Driver arriving in {self._state.eta} minutes."
        else:
            return f"Booking confirmed! Your driver will arrive in approximately {self._state.eta} minutes."
    
    @llm.ai_callable(description="Save the customer's name if they provide it during the conversation.")
    async def save_customer_name(
        self,
        name: Annotated[str, llm.TypeInfo(description="The customer's name")],
    ) -> str:
        """Save the customer's name."""
        self._state.caller_name = name
        logger.info(f"Customer name saved: {name}")
        return f"Thank you, {name}."
    
    @llm.ai_callable(description="End the call gracefully. Use after booking is confirmed or if customer wants to cancel.")
    async def end_call(
        self,
        reason: Annotated[str, llm.TypeInfo(description="Why the call is ending: completed, cancelled, no_answer, or error")] = "completed",
    ) -> str:
        """End the call gracefully."""
        logger.info(f"Call ending: {reason}")
        await send_dispatch_webhook(self._state, f"call_ended_{reason}")
        return "Goodbye!"


def prewarm(proc: JobProcess):
    """Preload models for faster response"""
    proc.userdata["vad"] = silero.VAD.load()


async def entrypoint(ctx: JobContext):
    """Main entry point for each room/call connection"""
    logger.info(f"Connecting to room: {ctx.room.name}")
    
    # Generate call ID from room name or create new one
    call_id = f"livekit-{ctx.room.name}-{int(datetime.utcnow().timestamp() * 1000)}"
    
    # Extract caller phone from room metadata if available (SIP trunk provides this)
    caller_phone = ""
    if ctx.room.metadata:
        try:
            metadata = json.loads(ctx.room.metadata)
            caller_phone = metadata.get("caller_phone", metadata.get("from", ""))
        except:
            pass
    
    # Create booking state for this call
    state = BookingState(call_id=call_id, caller_phone=caller_phone)
    logger.info(f"Call started: {call_id}, caller: {caller_phone}")
    
    # Connect to the room
    await ctx.connect(auto_subscribe=AutoSubscribe.AUDIO_ONLY)
    
    # Wait for a participant to join
    participant = await ctx.wait_for_participant()
    logger.info(f"Participant joined: {participant.identity}")
    
    # Create booking tools with state
    tools = TaxiBookingTools(state)
    
    # Create the voice pipeline agent
    agent = VoicePipelineAgent(
        vad=ctx.proc.userdata["vad"],
        stt=openai.STT(
            model="whisper-1",
            language="en",
        ),
        llm=openai.LLM(
            model="gpt-4o",
            temperature=0.6,
        ),
        tts=openai.TTS(
            voice="shimmer",
            speed=1.0,
        ),
        chat_ctx=llm.ChatContext().append(
            role="system",
            text=SYSTEM_PROMPT,
        ),
        fnc_ctx=tools,
        # Tuning for telephony
        min_endpointing_delay=0.5,  # Wait 500ms of silence before responding
        allow_interruptions=True,
    )
    
    # Track transcripts
    @agent.on("user_speech_committed")
    def on_user_speech(text: str):
        state.transcripts.append({
            "text": text,
            "timestamp": datetime.utcnow().isoformat() + "Z",
            "role": "user"
        })
        logger.info(f"User: {text}")
    
    @agent.on("agent_speech_committed")  
    def on_agent_speech(text: str):
        state.transcripts.append({
            "text": text,
            "timestamp": datetime.utcnow().isoformat() + "Z",
            "role": "assistant"
        })
        logger.info(f"Agent: {text}")
    
    # Start the agent
    agent.start(ctx.room, participant)
    
    # Initial greeting
    await agent.say(
        f"Hello, thank you for calling {COMPANY_NAME}. "
        f"My name is {AGENT_NAME}. Where would you like to be picked up from?",
        allow_interruptions=True
    )
    
    logger.info("Agent started and greeting sent")
    
    # Keep running until disconnected
    try:
        await asyncio.Future()  # Run forever
    except asyncio.CancelledError:
        logger.info(f"Call ended: {call_id}")
        if not state.confirmed:
            await send_dispatch_webhook(state, "call_ended_incomplete")


if __name__ == "__main__":
    cli.run_app(
        WorkerOptions(
            entrypoint_fnc=entrypoint,
            prewarm_fnc=prewarm,
        ),
    )
