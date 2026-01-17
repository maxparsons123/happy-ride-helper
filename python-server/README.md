# Taxi Voice AI Server (Python)

A standalone Python implementation of the taxi booking voice AI using OpenAI Realtime API.

## Features

- Real-time voice AI for taxi bookings
- WebSocket-based communication with SIP bridges
- OpenAI Realtime API integration
- Dispatch webhook integration for fare quotes
- Full booking flow with confirmation

## Requirements

- Python 3.10+
- OpenAI API key with Realtime API access

## Installation

```bash
cd python-server
pip install -r requirements.txt
```

## Configuration

Set these environment variables:

```bash
export OPENAI_API_KEY="sk-..."
export DISPATCH_WEBHOOK_URL="https://your-dispatch-server.com/webhook"
export COMPANY_NAME="247 Radio Carz"  # Optional
export AGENT_NAME="Ada"                # Optional
export VOICE="shimmer"                 # Optional (alloy, echo, fable, onyx, nova, shimmer)
```

Or create a `.env` file:

```env
OPENAI_API_KEY=sk-...
DISPATCH_WEBHOOK_URL=https://your-dispatch-server.com/webhook
COMPANY_NAME=247 Radio Carz
AGENT_NAME=Ada
VOICE=shimmer
```

## Running

```bash
# Development
uvicorn taxi_voice_server:app --host 0.0.0.0 --port 8000 --reload

# Production
uvicorn taxi_voice_server:app --host 0.0.0.0 --port 8000 --workers 4
```

Or run directly:

```bash
python taxi_voice_server.py
```

## API Endpoints

### Health Check
```
GET /health
```

### WebSocket
```
WS /ws
```

## WebSocket Protocol

### Client → Server Messages

#### Initialize Call
```json
{
  "type": "init",
  "phone": "447123456789",
  "customer_name": "John"  // optional
}
```

#### Send Audio (PCM16, 24kHz)
```json
{
  "type": "audio",
  "data": "<base64-encoded-pcm16>"
}
```

Or send as binary WebSocket frame.

#### End Call
```json
{
  "type": "hangup"
}
```

#### Push Dispatch Response (Alternative to webhook polling)
```json
{
  "type": "dispatch_response",
  "fare": "5.90",
  "eta": "10 minutes"
}
```

### Server → Client Messages

#### Audio Response (PCM16, 24kHz)
```json
{
  "type": "audio",
  "data": "<base64-encoded-pcm16>"
}
```

#### Transcript
```json
{
  "type": "transcript",
  "role": "user|assistant",
  "text": "Hello, I need a taxi..."
}
```

## Dispatch Webhook

The server POSTs to `DISPATCH_WEBHOOK_URL` with:

```json
{
  "action": "request_quote|confirmed|rejected",
  "call_id": "call-1234567890",
  "caller_phone": "447123456789",
  "caller_name": "John",
  "pickup": "10 High Street",
  "destination": "Train Station",
  "passengers": 1,
  "bags": 0,
  "vehicle_type": "saloon",
  "timestamp": "2024-01-15T10:30:00Z"
}
```

Expected response for `request_quote`:
```json
{
  "fare": "8.50",
  "eta": "5 minutes"
}
```

## Booking Flow

1. Customer calls → Server sends greeting
2. Customer provides pickup/destination
3. Ada calls `book_taxi(confirmation_state: "request_quote")`
4. Server POSTs to dispatch, receives fare/ETA
5. Ada reads fare: "The fare is £8.50 and your driver will be 5 minutes. Shall I book that?"
6. Customer says "yes" → Ada calls `book_taxi(confirmation_state: "confirmed")`
7. Server POSTs confirmation to dispatch
8. Ada: "That's booked for you. Is there anything else I can help you with?"
9. Customer: "No thanks" → Ada: "Safe travels!"

## Integration with C# SIP Bridge

Update your `SipAdaBridge` to connect to this server:

```csharp
var wsUrl = "ws://localhost:8000/ws";
```

The audio format should be:
- PCM16, 24kHz, mono
- Either base64-encoded JSON or raw binary frames

## Docker

```dockerfile
FROM python:3.11-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY taxi_voice_server.py .

EXPOSE 8000
CMD ["uvicorn", "taxi_voice_server:app", "--host", "0.0.0.0", "--port", "8000"]
```

Build and run:
```bash
docker build -t taxi-voice-ai .
docker run -p 8000:8000 \
  -e OPENAI_API_KEY=sk-... \
  -e DISPATCH_WEBHOOK_URL=https://... \
  taxi-voice-ai
```
