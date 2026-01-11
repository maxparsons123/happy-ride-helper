# Taxi AI SIP Bridge - C# Edition

A high-performance C# SIP/RTP bridge using SIPSorcery that handles multiple concurrent calls and bridges them to the Taxi AI WebSocket service.

## Features

- **Multi-user support**: Handle up to 50+ concurrent calls
- **SIP/RTP handling**: Full SIP signaling and RTP media using SIPSorcery
- **Audio conversion**: G.711 μ-law ↔ PCM16 codec
- **Resampling**: 8kHz ↔ 24kHz audio resampling
- **Real-time pacing**: 20ms frame timing with silence injection
- **Graceful shutdown**: Clean call termination and resource cleanup

## Requirements

- .NET 8.0 SDK
- Network access to your SIP provider
- Access to the Taxi AI WebSocket endpoint

## Configuration

Set these environment variables:

```bash
# SIP Configuration
SIP_PORT=5060              # SIP listening port (default: 5060)
SIP_USERNAME=taxi-bridge   # SIP username for registration
SIP_PASSWORD=              # SIP password (if required)

# AI Backend
WS_URL=wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime

# Capacity
MAX_CALLS=50               # Maximum concurrent calls (default: 50)

# TLS (optional)
ENABLE_TLS=false           # Enable TLS for SIP transport
```

## Building

```bash
cd TaxiSipBridge
dotnet restore
dotnet build -c Release
```

## Running

```bash
dotnet run --project TaxiSipBridge
```

Or run the compiled binary:

```bash
./TaxiSipBridge/bin/Release/net8.0/TaxiSipBridge
```

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   SIP Phone     │────▶│  TaxiSipBridge   │────▶│   Taxi AI       │
│   (Asterisk)    │◀────│  (C# + SIPSorcery)│◀────│   WebSocket     │
└─────────────────┘     └──────────────────┘     └─────────────────┘
        │                        │                        │
    SIP/RTP                  Manages                  WebSocket
   (μ-law 8kHz)           Call Sessions             (PCM16 24kHz)
```

### Call Flow

1. **Incoming INVITE**: SIP call received from Asterisk/PBX
2. **Session Creation**: New `CallSession` created with RTP session
3. **WebSocket Connection**: Connect to taxi-realtime edge function
4. **Audio Bridge**: Bidirectional audio streaming:
   - **Inbound**: RTP μ-law → PCM16 → Resample 8k→24k → WebSocket
   - **Outbound**: WebSocket → Resample 24k→8k → PCM16 → μ-law → RTP
5. **Call End**: BYE received, clean up resources

### Components

- **`SipBridgeService`**: Main SIP transport and call management
- **`CallSession`**: Per-call state, RTP/WebSocket handling
- **`G711Codec`**: μ-law encode/decode with lookup tables
- **`AudioResampler`**: Linear interpolation resampling

## Asterisk Integration

Configure your Asterisk dialplan to forward calls:

```ini
[from-whatsapp]
exten => _+X.,1,NoOp(Incoming call from ${CALLERID(num)})
 same => n,Answer()
 same => n,Dial(SIP/taxi-bridge@<bridge-ip>:5060,60)
 same => n,Hangup()
```

Or register as a PJSIP endpoint:

```ini
[taxi-bridge]
type=endpoint
transport=transport-udp
context=from-taxi
disallow=all
allow=ulaw
direct_media=no
aors=taxi-bridge

[taxi-bridge]
type=aor
contact=sip:taxi-bridge@<bridge-ip>:5060

[taxi-bridge]
type=auth
auth_type=userpass
username=taxi-bridge
password=your-password
```

## Performance Notes

- Each call uses ~1-2 MB memory
- CPU usage scales linearly with concurrent calls
- WebSocket connections are pooled per-call
- RTP timing is maintained with 5ms polling loop

## Troubleshooting

### No audio heard
- Check RTP port range is open (UDP 10000-20000)
- Verify `direct_media=no` in Asterisk config
- Check WebSocket connection logs

### Call drops immediately
- Verify SDP negotiation succeeds
- Check firewall allows SIP (UDP 5060)
- Enable debug logging for more details

### High latency
- Check network path to WebSocket endpoint
- Verify audio queue isn't backing up
- Consider adjusting jitter buffer if needed

## License

MIT
