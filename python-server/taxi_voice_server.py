#!/usr/bin/env python3
"""
Taxi Voice AI Server - Python Implementation
A real-time voice AI for taxi bookings using OpenAI Realtime API.

Run with: uvicorn taxi_voice_server:app --host 0.0.0.0 --port 8000
"""

import asyncio
import base64
import json
import os
import re
import time
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional, Dict, List, Any
from enum import Enum

import httpx
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
import websockets

# =============================================================================
# CONFIGURATION
# =============================================================================

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY", "")
OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17"
DISPATCH_WEBHOOK_URL = os.getenv("DISPATCH_WEBHOOK_URL", "")
COMPANY_NAME = os.getenv("COMPANY_NAME", "247 Radio Carz")
AGENT_NAME = os.getenv("AGENT_NAME", "Ada")
VOICE = os.getenv("VOICE", "shimmer")

# =============================================================================
# SYSTEM PROMPT
# =============================================================================

SYSTEM_PROMPT = f"""You are {AGENT_NAME}, a friendly and efficient taxi dispatcher for {COMPANY_NAME}.

## Your Personality
- Warm, professional, and helpful
- Speak naturally like a real person on the phone
- Keep responses concise - this is a phone call, not a chat
- Use British English spellings and phrases

## Your Tools
You have access to these tools:
- save_customer_name: Save the customer's name when they tell you
- book_taxi: Request a fare quote or confirm a booking

## Booking Flow - STRICT RULES

### For NEW bookings:
1. Greet the customer warmly
2. Ask where they'd like to be picked up from
3. Ask where they're going to
4. Once you have both addresses, call book_taxi with confirmation_state: "request_quote"
5. Wait for the fare quote from dispatch (you'll receive it as a system message)
6. Read out the EXACT fare and ETA to the customer
7. Ask "Shall I book that for you?"
8. If YES: call book_taxi with confirmation_state: "confirmed"
9. If NO: call book_taxi with confirmation_state: "rejected"
10. After confirmation, say "That's booked for you. Is there anything else I can help you with?"
11. Only say "Safe travels!" after they decline further help

### CRITICAL RULES:
- NEVER make up fares or ETAs - only use values from dispatch
- NEVER say "booked" until you receive confirmation from the book_taxi tool
- ALWAYS ask "Is there anything else I can help you with?" after a booking
- Wait for the customer to respond - don't rush through the flow

## Response Style
- Use filler words occasionally: "Right", "Okay", "Let me just..."
- Acknowledge what customers say before moving on
- Be patient with unclear addresses - ask for clarification
"""

# =============================================================================
# DATA MODELS
# =============================================================================

class ConfirmationState(str, Enum):
    REQUEST_QUOTE = "request_quote"
    CONFIRMED = "confirmed"
    REJECTED = "rejected"


@dataclass
class Booking:
    pickup: Optional[str] = None
    destination: Optional[str] = None
    passengers: int = 1
    bags: int = 0
    vehicle_type: str = "saloon"


@dataclass
class PendingQuote:
    fare: Optional[str] = None
    eta: Optional[str] = None
    pickup: Optional[str] = None
    destination: Optional[str] = None
    timestamp: float = 0


@dataclass
class SessionState:
    call_id: str
    phone: str = "unknown"
    customer_name: Optional[str] = None
    booking: Booking = field(default_factory=Booking)
    pending_quote: Optional[PendingQuote] = None
    booking_confirmed: bool = False
    asked_anything_else: bool = False
    call_ended: bool = False
    transcripts: List[Dict[str, str]] = field(default_factory=list)
    openai_response_active: bool = False


# =============================================================================
# FASTAPI APP
# =============================================================================

app = FastAPI(title="Taxi Voice AI Server")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health")
async def health_check():
    return {"status": "healthy", "timestamp": datetime.utcnow().isoformat()}


# =============================================================================
# TOOL DEFINITIONS FOR OPENAI
# =============================================================================

TOOLS = [
    {
        "type": "function",
        "name": "save_customer_name",
        "description": "Save the customer's name when they tell you their name.",
        "parameters": {
            "type": "object",
            "properties": {
                "name": {
                    "type": "string",
                    "description": "The customer's name"
                }
            },
            "required": ["name"]
        }
    },
    {
        "type": "function",
        "name": "book_taxi",
        "description": "Request a fare quote or confirm/reject a booking. Use confirmation_state: 'request_quote' to get fare, 'confirmed' when customer says yes, 'rejected' when customer says no.",
        "parameters": {
            "type": "object",
            "properties": {
                "confirmation_state": {
                    "type": "string",
                    "enum": ["request_quote", "confirmed", "rejected"],
                    "description": "The state of the booking confirmation"
                },
                "pickup": {
                    "type": "string",
                    "description": "The pickup address"
                },
                "destination": {
                    "type": "string",
                    "description": "The destination address"
                },
                "passengers": {
                    "type": "integer",
                    "description": "Number of passengers",
                    "default": 1
                },
                "bags": {
                    "type": "integer",
                    "description": "Number of bags",
                    "default": 0
                },
                "vehicle_type": {
                    "type": "string",
                    "description": "Type of vehicle requested",
                    "default": "saloon"
                }
            },
            "required": ["confirmation_state", "pickup", "destination"]
        }
    }
]


# =============================================================================
# WEBHOOK HANDLER
# =============================================================================

async def send_dispatch_webhook(
    session: SessionState,
    action: str,
    pickup: str,
    destination: str,
    passengers: int = 1,
    bags: int = 0,
    vehicle_type: str = "saloon"
) -> Dict[str, Any]:
    """Send booking request to dispatch system and wait for response."""
    
    if not DISPATCH_WEBHOOK_URL:
        print(f"[{session.call_id}] ⚠️ No DISPATCH_WEBHOOK_URL configured")
        return {
            "success": False,
            "error": "Dispatch system not configured"
        }
    
    payload = {
        "action": action,
        "call_id": session.call_id,
        "caller_phone": session.phone,
        "caller_name": session.customer_name,
        "pickup": pickup,
        "destination": destination,
        "passengers": passengers,
        "bags": bags,
        "vehicle_type": vehicle_type,
        "timestamp": datetime.utcnow().isoformat()
    }
    
    print(f"[{session.call_id}] 📤 Sending webhook: {action}")
    print(f"[{session.call_id}]    Payload: {json.dumps(payload, indent=2)}")
    
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.post(
                DISPATCH_WEBHOOK_URL,
                json=payload,
                headers={"Content-Type": "application/json"}
            )
            
            if response.status_code == 200:
                data = response.json()
                print(f"[{session.call_id}] ✅ Webhook response: {data}")
                return {"success": True, "data": data}
            else:
                print(f"[{session.call_id}] ❌ Webhook failed: {response.status_code}")
                return {
                    "success": False,
                    "error": f"Dispatch returned {response.status_code}"
                }
                
    except httpx.TimeoutException:
        print(f"[{session.call_id}] ⏱️ Webhook timeout")
        return {"success": False, "error": "Dispatch timeout"}
    except Exception as e:
        print(f"[{session.call_id}] ❌ Webhook error: {e}")
        return {"success": False, "error": str(e)}


# =============================================================================
# TOOL HANDLERS
# =============================================================================

async def handle_save_customer_name(
    session: SessionState,
    args: Dict[str, Any]
) -> Dict[str, Any]:
    """Handle save_customer_name tool call."""
    name = args.get("name", "").strip()
    if name:
        session.customer_name = name
        print(f"[{session.call_id}] 👤 Saved customer name: {name}")
        return {"success": True, "name": name}
    return {"success": False, "error": "No name provided"}


async def handle_book_taxi(
    session: SessionState,
    args: Dict[str, Any],
    openai_ws: websockets.WebSocketClientProtocol
) -> Dict[str, Any]:
    """Handle book_taxi tool call."""
    
    confirmation_state = args.get("confirmation_state", "request_quote")
    pickup = args.get("pickup") or session.booking.pickup
    destination = args.get("destination") or session.booking.destination
    passengers = args.get("passengers", 1)
    bags = args.get("bags", 0)
    vehicle_type = args.get("vehicle_type", "saloon")
    
    print(f"[{session.call_id}] 🚕 book_taxi called: state={confirmation_state}")
    print(f"[{session.call_id}]    pickup={pickup}, dest={destination}")
    
    # Validate addresses
    if not pickup or not destination:
        return {
            "success": False,
            "error": "Missing pickup or destination",
            "ada_message": "I need both a pickup address and a destination. What's the pickup address?"
        }
    
    # Normalize and compare addresses
    def normalize(addr: str) -> str:
        return re.sub(r'[,.\-\s]+', ' ', addr.lower()).strip()
    
    if normalize(pickup) == normalize(destination):
        return {
            "success": False,
            "error": "Pickup equals destination",
            "ada_message": "The pickup and destination seem to be the same. Where would you like to go?"
        }
    
    # Update session booking
    session.booking.pickup = pickup
    session.booking.destination = destination
    session.booking.passengers = passengers
    session.booking.bags = bags
    session.booking.vehicle_type = vehicle_type
    
    # === REQUEST_QUOTE ===
    if confirmation_state == ConfirmationState.REQUEST_QUOTE:
        result = await send_dispatch_webhook(
            session=session,
            action="request_quote",
            pickup=pickup,
            destination=destination,
            passengers=passengers,
            bags=bags,
            vehicle_type=vehicle_type
        )
        
        if result.get("success") and result.get("data"):
            data = result["data"]
            fare = data.get("fare") or data.get("price")
            eta = data.get("eta") or data.get("driver_eta")
            
            if fare and eta:
                session.pending_quote = PendingQuote(
                    fare=str(fare),
                    eta=str(eta),
                    pickup=pickup,
                    destination=destination,
                    timestamp=time.time()
                )
                
                # Inject fare message to Ada
                fare_message = f"The fare is £{fare} and your driver will be {eta}. Shall I book that for you?"
                
                await openai_ws.send(json.dumps({
                    "type": "conversation.item.create",
                    "item": {
                        "type": "message",
                        "role": "user",
                        "content": [{
                            "type": "input_text",
                            "text": f"[SYSTEM: Dispatch returned fare quote. Say EXACTLY: \"{fare_message}\" Then wait for customer's yes/no response.]"
                        }]
                    }
                }))
                
                await openai_ws.send(json.dumps({
                    "type": "response.create",
                    "response": {
                        "modalities": ["audio", "text"],
                        "instructions": f"Say EXACTLY: \"{fare_message}\" Then wait."
                    }
                }))
                
                return {
                    "success": True,
                    "fare": fare,
                    "eta": eta,
                    "message": "Fare quote received. Wait for customer confirmation."
                }
            else:
                return {
                    "success": False,
                    "error": "Dispatch did not return fare/ETA",
                    "ada_message": "I'm having trouble getting a quote. Let me try again."
                }
        else:
            return {
                "success": False,
                "error": result.get("error", "Dispatch failed"),
                "ada_message": "I'm sorry, we're having some technical issues. Please try again in a moment."
            }
    
    # === CONFIRMED ===
    elif confirmation_state == ConfirmationState.CONFIRMED:
        if not session.pending_quote:
            return {
                "success": False,
                "error": "No pending quote to confirm",
                "ada_message": "Let me get a fare quote for you first."
            }
        
        result = await send_dispatch_webhook(
            session=session,
            action="confirmed",
            pickup=pickup,
            destination=destination,
            passengers=passengers,
            bags=bags,
            vehicle_type=vehicle_type
        )
        
        if result.get("success"):
            session.booking_confirmed = True
            session.asked_anything_else = True
            
            # Clear pending quote
            fare = session.pending_quote.fare
            eta = session.pending_quote.eta
            session.pending_quote = None
            
            # Inject confirmation message
            await openai_ws.send(json.dumps({
                "type": "conversation.item.create",
                "item": {
                    "type": "message",
                    "role": "user",
                    "content": [{
                        "type": "input_text",
                        "text": "[SYSTEM: Booking CONFIRMED. Say EXACTLY: \"That's booked for you. Is there anything else I can help you with?\" Then WAIT for response. Do NOT say Safe travels yet.]"
                    }]
                }
            }))
            
            await openai_ws.send(json.dumps({
                "type": "response.create",
                "response": {
                    "modalities": ["audio", "text"]
                }
            }))
            
            return {
                "success": True,
                "confirmed": True,
                "fare": fare,
                "eta": eta,
                "message": "Booking confirmed"
            }
        else:
            return {
                "success": False,
                "error": result.get("error", "Confirmation failed"),
                "ada_message": "I'm sorry, there was an issue confirming your booking. Let me try again."
            }
    
    # === REJECTED ===
    elif confirmation_state == ConfirmationState.REJECTED:
        session.pending_quote = None
        session.asked_anything_else = True
        
        await send_dispatch_webhook(
            session=session,
            action="rejected",
            pickup=pickup,
            destination=destination
        )
        
        await openai_ws.send(json.dumps({
            "type": "conversation.item.create",
            "item": {
                "type": "message",
                "role": "user",
                "content": [{
                    "type": "input_text",
                    "text": "[SYSTEM: Customer rejected the fare. Say: \"No problem. Is there anything else I can help you with?\"]"
                }]
            }
        }))
        
        await openai_ws.send(json.dumps({
            "type": "response.create",
            "response": {
                "modalities": ["audio", "text"]
            }
        }))
        
        return {
            "success": True,
            "rejected": True,
            "message": "Booking rejected by customer"
        }
    
    return {"success": False, "error": "Invalid confirmation_state"}


async def handle_function_call(
    session: SessionState,
    name: str,
    args_json: str,
    call_id: str,
    openai_ws: websockets.WebSocketClientProtocol
):
    """Route function calls to appropriate handlers."""
    
    print(f"[{session.call_id}] 🔧 Tool call: {name}")
    
    try:
        args = json.loads(args_json) if args_json else {}
    except json.JSONDecodeError:
        args = {}
    
    result = {}
    
    if name == "save_customer_name":
        result = await handle_save_customer_name(session, args)
    elif name == "book_taxi":
        result = await handle_book_taxi(session, args, openai_ws)
    else:
        result = {"success": False, "error": f"Unknown function: {name}"}
    
    # Send function result back to OpenAI
    await openai_ws.send(json.dumps({
        "type": "conversation.item.create",
        "item": {
            "type": "function_call_output",
            "call_id": call_id,
            "output": json.dumps(result)
        }
    }))
    
    # Trigger response if needed
    if not result.get("success") and result.get("ada_message"):
        # Ada needs to speak an error message
        await openai_ws.send(json.dumps({
            "type": "response.create",
            "response": {
                "modalities": ["audio", "text"]
            }
        }))


# =============================================================================
# WEBSOCKET HANDLER
# =============================================================================

@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    """Handle incoming WebSocket connections from SIP bridge."""
    
    await websocket.accept()
    call_id = f"call-{int(time.time() * 1000)}"
    print(f"[{call_id}] 🔌 Client connected")
    
    session = SessionState(call_id=call_id)
    openai_ws: Optional[websockets.WebSocketClientProtocol] = None
    
    async def connect_to_openai():
        """Connect to OpenAI Realtime API."""
        nonlocal openai_ws
        
        headers = {
            "Authorization": f"Bearer {OPENAI_API_KEY}",
            "OpenAI-Beta": "realtime=v1"
        }
        
        try:
            openai_ws = await websockets.connect(
                OPENAI_REALTIME_URL,
                extra_headers=headers
            )
            print(f"[{call_id}] ✅ Connected to OpenAI Realtime API")
            
            # Configure session
            await openai_ws.send(json.dumps({
                "type": "session.update",
                "session": {
                    "modalities": ["text", "audio"],
                    "voice": VOICE,
                    "instructions": SYSTEM_PROMPT,
                    "input_audio_format": "pcm16",
                    "output_audio_format": "pcm16",
                    "input_audio_transcription": {
                        "model": "whisper-1"
                    },
                    "turn_detection": {
                        "type": "server_vad",
                        "threshold": 0.5,
                        "prefix_padding_ms": 500,
                        "silence_duration_ms": 1800
                    },
                    "tools": TOOLS,
                    "tool_choice": "auto"
                }
            }))
            
            return True
        except Exception as e:
            print(f"[{call_id}] ❌ Failed to connect to OpenAI: {e}")
            return False
    
    async def handle_openai_messages():
        """Process messages from OpenAI."""
        nonlocal openai_ws
        
        if not openai_ws:
            return
        
        try:
            async for message in openai_ws:
                data = json.loads(message)
                msg_type = data.get("type", "")
                
                # Handle different message types
                if msg_type == "session.created":
                    print(f"[{call_id}] 🎤 Session created")
                
                elif msg_type == "session.updated":
                    print(f"[{call_id}] ⚙️ Session updated")
                    # Send greeting
                    greeting = f"Hello, thanks for calling {COMPANY_NAME}, {AGENT_NAME} speaking. How can I help you today?"
                    await openai_ws.send(json.dumps({
                        "type": "conversation.item.create",
                        "item": {
                            "type": "message",
                            "role": "user",
                            "content": [{
                                "type": "input_text",
                                "text": f"[SYSTEM: Call started. Greet the customer by saying: \"{greeting}\"]"
                            }]
                        }
                    }))
                    await openai_ws.send(json.dumps({
                        "type": "response.create",
                        "response": {
                            "modalities": ["audio", "text"]
                        }
                    }))
                
                elif msg_type == "response.audio.delta":
                    # Forward audio to client
                    audio_data = data.get("delta", "")
                    if audio_data:
                        await websocket.send_json({
                            "type": "audio",
                            "data": audio_data
                        })
                
                elif msg_type == "response.audio_transcript.delta":
                    # Forward transcript to client
                    text = data.get("delta", "")
                    if text:
                        await websocket.send_json({
                            "type": "transcript",
                            "role": "assistant",
                            "text": text
                        })
                
                elif msg_type == "conversation.item.input_audio_transcription.completed":
                    # User's speech transcribed
                    transcript = data.get("transcript", "").strip()
                    if transcript:
                        print(f"[{call_id}] 👤 User: {transcript}")
                        session.transcripts.append({
                            "role": "user",
                            "text": transcript,
                            "timestamp": datetime.utcnow().isoformat()
                        })
                        await websocket.send_json({
                            "type": "transcript",
                            "role": "user",
                            "text": transcript
                        })
                        
                        # Check for goodbye
                        lower_text = transcript.lower()
                        if session.asked_anything_else:
                            if re.search(r'\b(no|nope|nothing|that\'s all|bye|goodbye)\b', lower_text):
                                print(f"[{call_id}] 👋 Goodbye detected")
                
                elif msg_type == "response.function_call_arguments.done":
                    # Tool call completed
                    await handle_function_call(
                        session=session,
                        name=data.get("name", ""),
                        args_json=data.get("arguments", ""),
                        call_id=data.get("call_id", ""),
                        openai_ws=openai_ws
                    )
                
                elif msg_type == "response.done":
                    # Response completed
                    response = data.get("response", {})
                    output = response.get("output", [])
                    for item in output:
                        if item.get("type") == "message":
                            for content in item.get("content", []):
                                if content.get("type") == "audio":
                                    transcript = content.get("transcript", "")
                                    if transcript:
                                        print(f"[{call_id}] 🤖 Ada: {transcript}")
                                        session.transcripts.append({
                                            "role": "assistant",
                                            "text": transcript,
                                            "timestamp": datetime.utcnow().isoformat()
                                        })
                
                elif msg_type == "error":
                    print(f"[{call_id}] ⚠️ OpenAI error: {data.get('error', {})}")
        
        except websockets.exceptions.ConnectionClosed:
            print(f"[{call_id}] 🔌 OpenAI connection closed")
        except Exception as e:
            print(f"[{call_id}] ❌ OpenAI handler error: {e}")
    
    async def handle_client_messages():
        """Process messages from SIP bridge client."""
        nonlocal openai_ws
        
        try:
            while True:
                data = await websocket.receive()
                
                # Handle binary audio data
                if "bytes" in data:
                    audio_bytes = data["bytes"]
                    if openai_ws and not session.call_ended:
                        audio_b64 = base64.b64encode(audio_bytes).decode("utf-8")
                        await openai_ws.send(json.dumps({
                            "type": "input_audio_buffer.append",
                            "audio": audio_b64
                        }))
                    continue
                
                # Handle JSON messages
                if "text" in data:
                    message = json.loads(data["text"])
                    msg_type = message.get("type", "")
                    
                    if msg_type == "init":
                        session.phone = message.get("phone", "unknown")
                        session.customer_name = message.get("customer_name")
                        print(f"[{call_id}] 📞 Call initialized: phone={session.phone}")
                        
                        # Connect to OpenAI
                        if await connect_to_openai():
                            asyncio.create_task(handle_openai_messages())
                    
                    elif msg_type == "audio":
                        # Base64 encoded audio
                        audio_data = message.get("data", "")
                        if audio_data and openai_ws and not session.call_ended:
                            await openai_ws.send(json.dumps({
                                "type": "input_audio_buffer.append",
                                "audio": audio_data
                            }))
                    
                    elif msg_type == "hangup":
                        print(f"[{call_id}] 📴 Call ended by client")
                        session.call_ended = True
                        break
                    
                    elif msg_type == "dispatch_response":
                        # Dispatch system pushing fare quote
                        fare = message.get("fare")
                        eta = message.get("eta")
                        if fare and eta and openai_ws:
                            session.pending_quote = PendingQuote(
                                fare=str(fare),
                                eta=str(eta),
                                pickup=session.booking.pickup,
                                destination=session.booking.destination,
                                timestamp=time.time()
                            )
                            fare_msg = f"The fare is £{fare} and your driver will be {eta}. Shall I book that for you?"
                            await openai_ws.send(json.dumps({
                                "type": "conversation.item.create",
                                "item": {
                                    "type": "message",
                                    "role": "user",
                                    "content": [{
                                        "type": "input_text",
                                        "text": f"[SYSTEM: Dispatch returned fare. Say EXACTLY: \"{fare_msg}\"]"
                                    }]
                                }
                            }))
                            await openai_ws.send(json.dumps({
                                "type": "response.create"
                            }))
        
        except WebSocketDisconnect:
            print(f"[{call_id}] 🔌 Client disconnected")
        except Exception as e:
            print(f"[{call_id}] ❌ Client handler error: {e}")
        finally:
            session.call_ended = True
            if openai_ws:
                await openai_ws.close()
    
    # Run client message handler
    await handle_client_messages()
    
    print(f"[{call_id}] 📞 Call complete. Transcripts: {len(session.transcripts)}")


# =============================================================================
# MAIN
# =============================================================================

if __name__ == "__main__":
    import uvicorn
    
    if not OPENAI_API_KEY:
        print("⚠️  WARNING: OPENAI_API_KEY not set!")
    if not DISPATCH_WEBHOOK_URL:
        print("⚠️  WARNING: DISPATCH_WEBHOOK_URL not set!")
    
    print(f"""
╔══════════════════════════════════════════════════════════════╗
║                   Taxi Voice AI Server                       ║
╠══════════════════════════════════════════════════════════════╣
║  Company: {COMPANY_NAME:50} ║
║  Agent: {AGENT_NAME:52} ║
║  Voice: {VOICE:52} ║
║  Dispatch: {DISPATCH_WEBHOOK_URL or 'Not configured':49} ║
╚══════════════════════════════════════════════════════════════╝
    """)
    
    uvicorn.run(app, host="0.0.0.0", port=8000)
