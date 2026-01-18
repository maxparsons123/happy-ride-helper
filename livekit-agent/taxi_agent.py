#!/usr/bin/env python3
"""
LiveKit Voice Agent for Taxi Booking
Connects to LiveKit rooms and uses OpenAI Realtime API for voice AI
"""

import asyncio
import logging
import os
from dotenv import load_dotenv

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
logger = logging.getLogger("taxi-agent")

# Configuration
COMPANY_NAME = os.getenv("COMPANY_NAME", "247 Radio Carz")
AGENT_NAME = os.getenv("AGENT_NAME", "Ada")

SYSTEM_PROMPT = f"""You are {AGENT_NAME}, a friendly and efficient voice assistant for {COMPANY_NAME}, a taxi booking service.

Your role:
- Help customers book taxis by collecting pickup location, destination, and number of passengers
- Be conversational and natural, like a real phone operator
- Keep responses brief and to the point (this is a phone call)
- Confirm details before finalizing bookings

Booking flow:
1. Greet the customer warmly
2. Ask for pickup location (be specific - ask for house number and street)
3. Ask for destination
4. Ask how many passengers
5. Confirm the booking details
6. Let them know a car will be dispatched

Important:
- If the customer seems confused, offer to help
- If you can't understand something, politely ask them to repeat
- Always be polite and professional
- Keep responses SHORT - this is a phone call, not a text chat
"""


def prewarm(proc: JobProcess):
    """Preload models for faster response"""
    proc.userdata["vad"] = silero.VAD.load()


async def entrypoint(ctx: JobContext):
    """Main entry point for each room connection"""
    logger.info(f"Connecting to room: {ctx.room.name}")
    
    # Connect to the room
    await ctx.connect(auto_subscribe=AutoSubscribe.AUDIO_ONLY)
    
    # Wait for a participant to join
    participant = await ctx.wait_for_participant()
    logger.info(f"Participant joined: {participant.identity}")
    
    # Create the voice pipeline agent
    agent = VoicePipelineAgent(
        vad=ctx.proc.userdata["vad"],
        stt=openai.STT(),
        llm=openai.LLM(model="gpt-4o"),
        tts=openai.TTS(voice="shimmer"),
        chat_ctx=llm.ChatContext().append(
            role="system",
            text=SYSTEM_PROMPT,
        ),
    )
    
    # Start the agent
    agent.start(ctx.room, participant)
    
    # Initial greeting
    await agent.say(
        f"Hello, thank you for calling {COMPANY_NAME}. "
        f"My name is {AGENT_NAME}. How can I help you today?",
        allow_interruptions=True
    )
    
    logger.info("Agent started and greeting sent")


if __name__ == "__main__":
    cli.run_app(
        WorkerOptions(
            entrypoint_fnc=entrypoint,
            prewarm_fnc=prewarm,
        ),
    )
