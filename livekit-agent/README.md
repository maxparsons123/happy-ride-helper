# LiveKit Taxi Voice Agent

Voice AI agent for taxi booking using LiveKit and OpenAI.

## Quick Setup

```bash
# 1. Install dependencies
pip install -r requirements.txt

# 2. Copy and edit .env
cp .env.example .env
nano .env  # Add your keys

# 3. Run the agent
python taxi_agent.py dev
```

## Configuration

Edit `.env`:
- `LIVEKIT_URL` - Your LiveKit server (e.g., `ws://YOUR_IP:7880`)
- `LIVEKIT_API_KEY` - Your LiveKit API key
- `LIVEKIT_API_SECRET` - Your LiveKit API secret
- `OPENAI_API_KEY` - Your OpenAI API key

## Running as a Service

Create `/etc/systemd/system/taxi-agent.service`:

```ini
[Unit]
Description=Taxi LiveKit Voice Agent
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/livekit-agent
ExecStart=/usr/bin/python3 -u taxi_agent.py start
Restart=always
RestartSec=5
Environment=PYTHONUNBUFFERED=1

[Install]
WantedBy=multi-user.target
```

Then:
```bash
sudo systemctl daemon-reload
sudo systemctl enable taxi-agent
sudo systemctl start taxi-agent
sudo journalctl -u taxi-agent -f  # View logs
```

## Testing

1. Run `python taxi_agent.py dev` on your server
2. Go to the `/livekit` page in the web app
3. Click "Connect to LiveKit"
4. The agent should greet you and start the booking flow
