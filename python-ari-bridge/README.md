# Python ARI Bridge for OpenAI Realtime

A production-grade voice agent bridge connecting Asterisk PBX to OpenAI's Realtime API using ARI (Asterisk REST Interface) and ExternalMedia channels.

## Architecture

```
┌─────────────┐     ┌───────────────────┐     ┌─────────────────┐
│ SIP Caller  │────▶│   Asterisk PBX    │────▶│   ARI Bridge    │
│  (Phone)    │     │   (ExternalMedia) │     │   (Python)      │
└─────────────┘     └───────────────────┘     └────────┬────────┘
                           ▲                           │
                           │ RTP (slin16 @ 16kHz)      │ WebSocket
                           │                           ▼
                    ┌──────┴──────┐            ┌───────────────┐
                    │   Mixing    │            │    OpenAI     │
                    │   Bridge    │            │   Realtime    │
                    └─────────────┘            └───────────────┘
```

## Key Advantages Over AudioSocket Approach

| Feature | ARI + ExternalMedia | AudioSocket |
|---------|---------------------|-------------|
| **Audio Format** | slin16 (16kHz PCM) | µ-law (8kHz) typically |
| **Resampling** | 16k→24k (1.5x) | 8k→24k→8k (3x round-trip) |
| **Quality** | Higher fidelity | More artifacts |
| **NAT Handling** | Asterisk handles it | You handle it |
| **Call Control** | Full ARI (transfer, DTMF, etc.) | Limited |

## Requirements

- **Ubuntu 22.04/24.04** (or similar)
- **Asterisk 20+** with PJSIP and ARI enabled
- **Python 3.10+**
- **OpenAI API key** with Realtime access

## Installation

### 1. Asterisk Configuration

Add to your `http.conf`:
```ini
[general]
enabled = yes
bindaddr = 0.0.0.0
bindport = 8088
```

Add to your `ari.conf`:
```ini
[general]
enabled = yes
pretty = yes

[voiceapp]
type = user
read_only = no
password = CHANGE_ME
```

Add to your `extensions.conf`:
```ini
[inbound]
exten => _X.,1,NoOp(Incoming call → AI Agent)
 same => n,Answer()
 same => n,Stasis(voice-agent,${CALLERID(num)},${EXTEN})
 same => n,Hangup()

[internal]
; For transfers to human operator
exten => 1001,1,NoOp(Transfer to operator)
 same => n,Dial(PJSIP/1001)
 same => n,Hangup()
```

### 2. Python Environment

```bash
cd python-ari-bridge

# Create virtual environment
python3 -m venv venv
source venv/bin/activate

# Install dependencies
pip install -r requirements.txt

# Configure environment
cp .env.example .env
# Edit .env with your OpenAI API key and ARI credentials
```

### 3. Run the Bridge

```bash
source venv/bin/activate
python -m app.main
```

## Configuration

All settings via environment variables or `.env` file:

| Variable | Default | Description |
|----------|---------|-------------|
| `OPENAI_API_KEY` | - | Your OpenAI API key (required) |
| `OPENAI_MODEL` | gpt-4o-mini-realtime-preview-2024-12-17 | Model to use |
| `OPENAI_VOICE` | shimmer | Voice for TTS |
| `ARI_URL` | http://localhost:8088 | Asterisk ARI URL |
| `ARI_USER` | voiceapp | ARI username |
| `ARI_PASSWORD` | CHANGE_ME | ARI password |
| `ARI_APP` | voice-agent | Stasis application name |
| `RTP_BIND_HOST` | 127.0.0.1 | RTP socket bind address |
| `RTP_PORT_START` | 30000 | RTP port range start |
| `RTP_PORT_END` | 30100 | RTP port range end |
| `VAD_THRESHOLD` | 0.35 | Voice activity detection threshold |
| `VOLUME_BOOST` | 2.5 | Inbound audio volume boost |

## Observability

Prometheus metrics exposed on `:9090/metrics`:

- `ari_bridge_calls_total` - Total calls by status
- `ari_bridge_active_calls` - Current active calls
- `ari_bridge_call_duration_seconds` - Call duration histogram
- `ari_bridge_rtp_packets_in_total` - RTP packets received
- `ari_bridge_rtp_packets_out_total` - RTP packets sent
- `ari_bridge_openai_latency_seconds` - OpenAI response latency

Health endpoints:
- `/healthz` - Liveness probe
- `/readyz` - Readiness probe

## Systemd Service

Create `/etc/systemd/system/ari-bridge.service`:

```ini
[Unit]
Description=ARI Voice Agent Bridge
After=network.target asterisk.service

[Service]
Type=simple
User=asterisk
WorkingDirectory=/opt/python-ari-bridge
ExecStart=/opt/python-ari-bridge/venv/bin/python -m app.main
Restart=always
RestartSec=5
EnvironmentFile=/opt/python-ari-bridge/.env

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable ari-bridge
sudo systemctl start ari-bridge
```

## Troubleshooting

### No audio from OpenAI
- Check `OPENAI_API_KEY` is set correctly
- Verify Asterisk can reach the RTP ports (firewall)
- Check ARI credentials match `ari.conf`

### "No return audio" to caller
- Verify `UNICASTRTP_LOCAL_ADDRESS/PORT` are being read correctly
- Check ExternalMedia channel is added to the mixing bridge
- Ensure RTP port range is open

### Audio quality issues
- This uses 16kHz slin16, quality should be good
- Check `VOLUME_BOOST` if audio is too quiet
- Verify no packet loss with Prometheus metrics

### DTMF not working
- DTMF events come through ARI `ChannelDtmfReceived`
- Currently logged but not fully handled (extend `_handle_event`)

## License

MIT
