# Taxi AI SIP Bridge - Windows Desktop

A Windows Forms desktop application using **SIPSorcery** + **NAudio** that auto-answers SIP calls and connects them to Ada AI.

## Features

- **Auto-Answer SIP Calls**: Registers with your SIP server and automatically answers incoming calls
- **NAudio Integration**: Full audio capture/playback using NAudio
- **Microphone Test Mode**: Test Ada directly with your microphone (no SIP phone needed!)
- **Real-time Transcripts**: See what you and Ada are saying
- **Speaker Playback**: Hear Ada's voice through your speakers

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

## Two Modes of Operation

### 1. SIP Auto-Answer Mode (Production)
Click **▶ Start SIP** to:
1. Register with your SIP server (Asterisk, FreeSWITCH, etc.)
2. Wait for incoming calls
3. Auto-answer and connect caller to Ada
4. Bridge audio bidirectionally

```
┌─────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  SIP Phone  │────▶│ TaxiSipBridge.exe│────▶│ taxi-realtime   │
│  (Caller)   │◀────│   Auto-Answer    │◀────│ (Ada AI)        │
└─────────────┘     └──────────────────┘     └─────────────────┘
```

### 2. Microphone Test Mode (Development)
Click **🎤 Test with Mic** to:
1. Connect directly to Ada via WebSocket
2. Capture audio from your microphone
3. Play Ada's responses through speakers
4. No SIP setup required!

```
┌─────────────┐     ┌──────────────────┐     ┌─────────────────┐
│ Microphone  │────▶│ TaxiSipBridge.exe│────▶│ taxi-realtime   │
│  + Speaker  │◀────│   NAudio Client  │◀────│ (Ada AI)        │
└─────────────┘     └──────────────────┘     └─────────────────┘
```

## Configuration

| Field | Description | Default |
|-------|-------------|---------|
| SIP Server | Your SIP server IP/hostname | 206.189.123.28 |
| Port | SIP port | 5060 |
| Transport | UDP or TCP | UDP |
| Username | SIP extension/username | max201 |
| Password | SIP password | (configured) |
| Ada URL | Ada AI WebSocket endpoint | taxi-realtime-paired |

## Audio Flow

### Inbound (Caller → Ada)
```
RTP µ-law 8kHz → Decode → Resample 24kHz → WebSocket → Ada
```

### Outbound (Ada → Caller)
```
Ada → WebSocket PCM 24kHz → Resample 8kHz → Encode µ-law → RTP
```

### Microphone Mode
```
Mic 24kHz → WebSocket → Ada → Speaker 24kHz
```

## Project Structure

```
TaxiSipBridge.WinForms/
├── Program.cs              # Entry point
├── MainForm.cs             # UI logic
├── MainForm.Designer.cs    # UI layout
├── SipAutoAnswer.cs        # SIP handling + auto-answer
├── AdaAudioClient.cs       # WebSocket + NAudio integration
├── SipAdaBridge.cs         # Legacy bridge (alternative)
├── AudioMonitor.cs         # Debug audio playback
└── TaxiSipBridge.WinForms.csproj
```

## Dependencies

- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) - SIP/RTP stack
- [NAudio](https://github.com/naudio/NAudio) - Audio capture/playback

## Troubleshooting

### No Registration
- Check SIP server IP and credentials
- Verify firewall allows UDP/5060
- Try TCP transport

### No Audio in SIP Mode
- Check logs for RTP packet flow
- Verify WebSocket connection succeeds
- Ensure SIP server uses PCMU (G.711 µ-law)

### Microphone Not Working
- Check Windows audio permissions
- Verify default recording device
- Try a different audio device index

### Call Auto-Rejects
- Only one call at a time is supported
- Wait for current call to end

## Example Usage

1. **Start the app** → Click **▶ Start SIP**
2. **Wait for registration** → Status shows "✓ Registered"
3. **Make a call** from Zoiper/Asterisk to your SIP extension
4. **App auto-answers** → You hear Ada's greeting
5. **Talk to Ada** → Book a taxi!
