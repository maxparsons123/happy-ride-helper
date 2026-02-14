# AdaSdkModel — OpenAI .NET SDK Taxi Booking System

A complete, standalone C# project using the **official OpenAI .NET SDK** (`OpenAI.RealtimeConversation`) for real-time voice AI taxi booking over SIP.

## Architecture

```
SIP Phone ←→ SipServer (SIPSorcery) ←→ ALawRtpPlayout ←→ OpenAiSdkClient (SDK) ←→ OpenAI Realtime API
                                                              ↕
                                                    CallSession (tools)
                                                    ├── sync_booking_data
                                                    ├── book_taxi (quote/confirm)
                                                    └── end_call
                                                              ↕
                                                    FareCalculator + BsqdDispatcher
```

## Key Features

- **OpenAI .NET SDK**: Uses `RealtimeConversationClient` and `RealtimeConversationSession` — no raw WebSocket/JSON plumbing
- **Native G.711 A-law**: Direct 8kHz telephony passthrough, zero resampling
- **Multi-call**: ConcurrentDictionary-based session isolation
- **Production tools**: sync_booking_data, book_taxi (request_quote/confirmed), end_call
- **Deferred responses**: Queues `StartResponseAsync` if one is already active
- **Barge-in**: VAD-triggered playout clearing
- **No-reply watchdog**: 15s silence → re-prompt (30s for confirmations)
- **Echo guard**: 300ms silence window after AI finishes speaking
- **Drain-aware hangup**: Polls playout queue before SIP disconnect
- **BSQD + MQTT dispatch**: Parallel webhook + MQTT publishing
- **Address integrity**: Verbatim transcript copying, no hallucination

## Quick Start

```bash
cd csharp-sip-bridge/AdaSdkModel
dotnet restore
# Edit appsettings.json with your OpenAI API key and SIP credentials
dotnet run
```

## Docker

```bash
cd csharp-sip-bridge
docker build -f AdaSdkModel/Dockerfile -t adasdkmodel .
docker run -e ADA_OpenAi__ApiKey=sk-... -p 5060:5060/udp adasdkmodel
```

## Configuration

All settings in `appsettings.json` or via environment variables prefixed with `ADA_`:

| Section | Key | Description |
|---------|-----|-------------|
| OpenAi | ApiKey | OpenAI API key |
| OpenAi | Model | Realtime model (default: gpt-4o-mini-realtime-preview) |
| OpenAi | Voice | TTS voice (shimmer, alloy, echo, etc.) |
| Sip | Server | SIP registrar hostname |
| Sip | Username/Password | SIP credentials |
| Audio | VolumeBoost | Egress gain (1.0 = none) |
| Audio | IngressVolumeBoost | Ingress gain (4.0 = recommended) |
| Audio | BargeInRmsThreshold | Barge-in sensitivity (1500 default) |
| Dispatch | BsqdWebhookUrl | BSQD dispatch endpoint |
| Dispatch | MqttBrokerUrl | MQTT broker for DispatchSystem |

## Project Structure

```
AdaSdkModel/
├── Ai/
│   ├── IOpenAiClient.cs          # Interface
│   └── OpenAiSdkClient.cs       # SDK implementation (816 lines → clean)
├── Audio/
│   ├── ALawRtpPlayout.cs         # RTP playout engine
│   └── G711Audio.cs              # Volume boost + μ-law↔A-law transcoding
├── Config/
│   └── Settings.cs               # Strongly-typed config
├── Core/
│   ├── BookingState.cs           # Booking model
│   ├── CallSession.cs            # Session lifecycle + tool handling
│   ├── ICallSession.cs           # Interface
│   └── SessionManager.cs         # Concurrent session tracking
├── Services/
│   ├── BsqdDispatcher.cs         # BSQD + MQTT dispatch
│   ├── FareCalculator.cs         # Geocoding + fare calc
│   ├── IDispatcher.cs            # Interface
│   └── IFareCalculator.cs        # Interface
├── Sip/
│   ├── CallerIdExtractor.cs      # SIP header parsing
│   └── SipServer.cs              # Multi-call SIP server
├── Program.cs                    # DI host + factory + worker
├── Dockerfile
├── appsettings.json
└── AdaSdkModel.csproj
```
