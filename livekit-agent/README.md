# LiveKit Taxi Voice Agent

Voice AI agent for taxi booking using LiveKit and OpenAI. Includes a SIP-enabled agent for A/B testing against the Asterisk bridge.

## Agent Files

| File | Purpose |
|------|---------|
| `taxi_agent.py` | Simple agent for WebRTC browser calls |
| `taxi_agent_sip.py` | Full booking agent with SIP trunk support and dispatch integration |

## Architecture Comparison

| Component | Asterisk Bridge | LiveKit SIP |
|-----------|----------------|-------------|
| SIP Handling | Asterisk → AudioSocket | LiveKit SIP Trunk |
| Audio Transport | WebSocket (PCM16) | WebRTC (Opus) |
| Voice AI | Edge Function → OpenAI | Python Agent → OpenAI |
| Latency | ~300ms | ~150ms |
| Codec Quality | 16kHz slin16 | 48kHz Opus |

## Quick Setup

```bash
# 1. Install dependencies
pip install -r requirements.txt

# 2. Copy and edit .env
cp .env.example .env
nano .env  # Add your keys

# 3. Run the agent (choose one)
python taxi_agent.py dev      # Simple browser agent
python taxi_agent_sip.py dev  # Full SIP agent with booking
```

## Configuration

Edit `.env`:
- `LIVEKIT_URL` - Your LiveKit server (e.g., `ws://YOUR_IP:7880` or `wss://your-cloud.livekit.cloud`)
- `LIVEKIT_API_KEY` - Your LiveKit API key
- `LIVEKIT_API_SECRET` - Your LiveKit API secret
- `OPENAI_API_KEY` - Your OpenAI API key
- `DISPATCH_WEBHOOK_URL` - Your dispatch system endpoint (for taxi_agent_sip.py)

## SIP Trunk Setup (LiveKit Cloud)

### Option A: LiveKit CLI
```bash
livekit-cli sip trunk create --from-file sip_trunk_config.yaml
```

### Option B: LiveKit Cloud Dashboard
1. Go to your LiveKit Cloud project → SIP → Trunks
2. Create an inbound trunk
3. Note the SIP URI: `sip:your-trunk@sip.livekit.cloud`
4. Point your SIP provider (Twilio/Telnyx) to this URI

## Dispatch Integration

The SIP agent sends webhooks matching the Asterisk bridge format:

```json
{
  "job_id": "uuid",
  "call_id": "livekit-room-timestamp",
  "caller_phone": "+44...",
  "action": "request_quote",
  "ada_pickup": "123 Main Street",
  "ada_destination": "456 High Road",
  "passengers": 2,
  "pickup_time": "ASAP"
}
```

## Running as a Service

Create `/etc/systemd/system/livekit-taxi-agent.service`:

```ini
[Unit]
Description=LiveKit Taxi Voice Agent
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/livekit-agent
ExecStart=/usr/bin/python3 -u taxi_agent_sip.py start
Restart=always
RestartSec=5
Environment=PYTHONUNBUFFERED=1

[Install]
WantedBy=multi-user.target
```

Then:
```bash
sudo systemctl daemon-reload
sudo systemctl enable livekit-taxi-agent
sudo systemctl start livekit-taxi-agent
sudo journalctl -u livekit-taxi-agent -f  # View logs
```

## A/B Testing

Route a portion of calls to LiveKit SIP trunk using your SIP provider's routing rules:
- Split by time of day, caller prefix, or random percentage
- Compare latency, booking completion rates, and customer satisfaction

## Testing from Browser

1. Run `python taxi_agent_sip.py dev` on your server
2. Go to the `/livekit` page in the web app
3. Click "Connect to LiveKit"
4. The agent will greet you and start the booking flow
