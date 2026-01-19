# Taxi AI SIP Bridge - Windows Desktop

A Windows Forms desktop application using SIPSorcery that bridges SIP calls to the Ada AI taxi booking assistant.

## Quick Start

### Prerequisites
- .NET 8.0 SDK ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- Windows 10/11

### Build & Run

```bash
cd csharp-sip-bridge/TaxiSipBridge.WinForms
dotnet restore
dotnet run
```

Or build a standalone executable:
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

The executable will be in `bin/Release/net8.0-windows/win-x64/publish/TaxiSipBridge.exe`

## Features

- **SIP Registration**: Connects to any SIP server (Asterisk, FreeSWITCH, etc.)
- **Real-time Audio**: Streams audio to Ada AI via WebSocket
- **Audio Monitor**: Optional local playback for debugging
- **Live Logs**: See all SIP/RTP/WebSocket activity

## Configuration

| Field | Description | Default |
|-------|-------------|---------|
| SIP Server | Your SIP server IP/hostname | 206.189.123.28 |
| Port | SIP port | 5060 |
| Transport | UDP or TCP | UDP |
| Username | SIP extension/username | max201 |
| Password | SIP password | (configured) |
| WebSocket | Ada AI endpoint | taxi-realtime-paired |

## Architecture

```
┌─────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  SIP Phone  │────▶│ TaxiSipBridge.exe│────▶│ taxi-realtime   │
│  (Zoiper)   │◀────│   (SIPSorcery)   │◀────│ (Ada AI)        │
└─────────────┘     └──────────────────┘     └─────────────────┘
      SIP/RTP              Audio              WebSocket
    (µ-law 8kHz)         Conversion         (PCM16 24kHz)
```

## Audio Flow

1. **Inbound (Caller → AI)**:
   - RTP µ-law 8kHz from SIP
   - Sent as binary to WebSocket
   - Ada processes via OpenAI Realtime API

2. **Outbound (AI → Caller)**:
   - PCM16 24kHz from Ada
   - Resampled to 8kHz
   - Encoded to µ-law
   - Sent as RTP packets

## Troubleshooting

### No Registration
- Check SIP server IP and credentials
- Verify firewall allows UDP/5060
- Try TCP transport if UDP blocked

### No Audio
- Enable "Monitor Audio" checkbox to hear Ada locally
- Check logs for RTP packet flow
- Verify WebSocket connection succeeds

### Call Drops
- Check logs for WebSocket errors
- Ensure stable internet connection
- Verify SIP server allows long calls

## Dependencies

- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) - SIP/RTP stack
- [NAudio](https://github.com/naudio/NAudio) - Audio playback for monitoring
