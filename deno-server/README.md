# Taxi Voice AI Server - Deno Edition

A standalone Deno implementation of the taxi voice AI server that can run independently on your own infrastructure.

## Features

- 🎙️ Real-time voice conversation using OpenAI Realtime API
- 🚕 Complete taxi booking flow with fare quotes
- 🔌 WebSocket interface for SIP bridge integration
- 🌐 Webhook integration for dispatch systems
- ⚡ Single-file, zero-dependency (just Deno)

## Requirements

- [Deno](https://deno.land/) 1.40+ installed
- OpenAI API key with Realtime API access

## Quick Start

```bash
# Set environment variables
export OPENAI_API_KEY="sk-..."
export DISPATCH_WEBHOOK_URL="https://your-dispatch.com/webhook"  # Optional
export COMPANY_NAME="Your Taxi Company"
export AGENT_NAME="Ada"
export VOICE="shimmer"
export PORT=8080

# Run the server
deno run --allow-net --allow-env taxi_voice_server.ts
```

## Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `OPENAI_API_KEY` | Yes | - | Your OpenAI API key |
| `DISPATCH_WEBHOOK_URL` | No | - | Webhook URL for dispatch integration |
| `COMPANY_NAME` | No | "ABC Taxis" | Company name for the AI agent |
| `AGENT_NAME` | No | "Ada" | Name of the AI agent |
| `VOICE` | No | "shimmer" | OpenAI voice (shimmer, alloy, echo, etc.) |
| `PORT` | No | 8080 | HTTP/WebSocket server port |

## API Endpoints

### Health Check
```
GET /
GET /health
```

Returns server status and configuration.

### Voice WebSocket
```
WS /voice?call_id=xxx&caller_phone=+44xxx
WS /ws?call_id=xxx&caller_phone=+44xxx
```

WebSocket endpoint for voice calls. Connect your SIP bridge here.

**Query Parameters:**
- `call_id` - Unique call identifier (optional, auto-generated if not provided)
- `caller_phone` - Caller's phone number

**WebSocket Messages (from client):**
```json
{ "type": "audio", "audio": "<base64 PCM16 audio>" }
{ "type": "hangup" }
```

**WebSocket Messages (to client):**
```json
{ "type": "audio", "audio": "<base64 PCM16 audio>" }
```

### Webhook Test
```
POST /webhook-test
```

Test endpoint that simulates dispatch responses.

## Dispatch Webhook

If `DISPATCH_WEBHOOK_URL` is configured, the server will send webhooks for:

### `request_quote`
Sent when AI requests a fare quote.

**Request:**
```json
{
  "action": "request_quote",
  "call_id": "call-123",
  "caller_phone": "+44123456789",
  "caller_name": "John",
  "booking": {
    "pickup": "123 High Street",
    "destination": "Airport",
    "passengers": 2,
    "scheduledTime": "ASAP"
  },
  "timestamp": "2024-01-15T10:30:00Z"
}
```

**Expected Response:**
```json
{
  "fare": "£15.50",
  "eta": "8 minutes"
}
```

### `confirm_booking`
Sent when customer confirms the booking.

**Expected Response:**
```json
{
  "booking_ref": "ABC123"
}
```

### `call_ended`
Sent when the call ends.

### `customer_identified`
Sent when the customer provides their name.

## Booking Flow

1. **Greeting** - AI greets the caller
2. **Pickup** - AI asks for pickup address
3. **Destination** - AI asks for destination
4. **Confirmation** - AI confirms the addresses
5. **Quote** - AI calls `book_taxi` with `confirmation_state: "request_quote"`
6. **Fare** - AI tells customer the fare and asks for confirmation
7. **Booking** - AI calls `book_taxi` with `confirmation_state: "confirm"`
8. **Complete** - AI confirms booking with reference number

## Docker Deployment

```dockerfile
FROM denoland/deno:1.40.0

WORKDIR /app

COPY taxi_voice_server.ts .

EXPOSE 8080

CMD ["run", "--allow-net", "--allow-env", "taxi_voice_server.ts"]
```

Build and run:
```bash
docker build -t taxi-voice-server .
docker run -p 8080:8080 \
  -e OPENAI_API_KEY="sk-..." \
  -e DISPATCH_WEBHOOK_URL="https://..." \
  -e COMPANY_NAME="My Taxis" \
  taxi-voice-server
```

## Deploy to Deno Deploy

1. Create a project at [dash.deno.com](https://dash.deno.com)
2. Link your repository or upload `taxi_voice_server.ts`
3. Set environment variables in the dashboard
4. Deploy!

## SIP Bridge Integration

Connect your SIP/Asterisk system to the `/voice` WebSocket endpoint:

```typescript
const ws = new WebSocket("wss://your-server.com/voice?call_id=123&caller_phone=+44123");

// Send audio from caller
ws.send(JSON.stringify({
  type: "audio",
  audio: base64EncodedPCM16Audio
}));

// Receive audio for caller
ws.onmessage = (event) => {
  const msg = JSON.parse(event.data);
  if (msg.type === "audio") {
    // Play msg.audio (base64 PCM16) to caller
  }
};

// Signal hangup
ws.send(JSON.stringify({ type: "hangup" }));
```

## Audio Format

- **Input/Output:** PCM16 (16-bit signed, little-endian)
- **Sample Rate:** 24000 Hz (OpenAI Realtime default)
- **Channels:** Mono
- **Encoding:** Base64

## Customization

Edit the `SYSTEM_PROMPT` constant to customize:
- Agent personality and tone
- Booking flow steps
- Company-specific rules
- Language and phrasing

Edit the `TOOLS` array to add custom tool calls for your dispatch integration.

## License

MIT
