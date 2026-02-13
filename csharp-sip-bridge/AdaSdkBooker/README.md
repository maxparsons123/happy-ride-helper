# AdaSdkBooker — Standalone AI Taxi Booking System

Fully self-contained taxi booking application with integrated AI voice assistant.

## Architecture

AdaSdkBooker is a **standalone project** — all SIP, AI, audio, dispatch, and configuration code is included directly. No external project references required.

### Project Structure

```
AdaSdkBooker/
├── Ai/                     # OpenAI Realtime SDK client (G.711 A-law)
│   ├── IOpenAiClient.cs
│   └── OpenAiSdkClient.cs
├── Audio/                  # Telephony audio pipeline
│   ├── ALawRtpPlayout.cs   # Ultra-low-jitter RTP playout engine
│   ├── ALawThinningFilter.cs
│   ├── AlawToSimliResampler.cs
│   └── G711Audio.cs        # Volume boost + G.711 transcoding
├── Avatar/                 # Simli WebRTC avatar
│   └── SimliAvatar.cs
├── Config/                 # Strongly-typed settings
│   └── Settings.cs
├── Core/                   # Session lifecycle
│   ├── BookingState.cs
│   ├── CallSession.cs      # Tool handling + fare flow
│   ├── CallbackLoggerProvider.cs
│   ├── ICallSession.cs
│   └── SessionManager.cs
├── Services/               # Dispatch + Fare
│   ├── BsqdDispatcher.cs
│   ├── FareCalculator.cs
│   ├── IDispatcher.cs
│   ├── IFareCalculator.cs
│   └── IcabbiBookingService.cs
├── Sip/                    # SIP registration + call handling
│   ├── CallerIdExtractor.cs
│   └── SipServer.cs
├── ConfigForm.cs           # Settings dialog
├── MainForm.cs             # UI logic (booking, calls, avatar/map)
├── MainForm.Designer.cs    # WinForms layout
├── Program.cs              # Entry point
├── appsettings.json        # Default configuration
└── AdaSdkBooker.csproj     # Standalone build
```

## Layout

```
┌───────────────────────────────────────────────────────┐
│ ToolStrip: [⚙ Settings] [🤖 Ada ON/OFF] [📋 Log]   │
├──────────────────────────┬────────────────────────────┤
│  📋 BOOKING FORM         │  🤖 ADA / 🗺️ MAP          │
│  Name, Phone, Pickup,   │  (toggle shows avatar      │
│  Dropoff, Pax, Vehicle   │   or Leaflet map with      │
│  [🔍 Quote] [✅ Dispatch]│   pickup/dropoff pins)     │
│──────────────────────────│                            │
│  📊 JOB LIST             │  📞 SIP (compact)          │
│  DataGridView with       │  🎧 CALL CONTROLS          │
│  session bookings        │                            │
├──────────────────────────┴────────────────────────────┤
│ [📋 LOG — toggleable]                                 │
├───────────────────────────────────────────────────────┤
│ StatusBar                                             │
└───────────────────────────────────────────────────────┘
```

## Features

- **Inline booking form** with Photon autocomplete and caller history
- **Auto-populate** from Ada's AI extraction during calls
- **Manual mode** for operator-typed bookings when Ada is off
- **Job grid** tracking all session bookings
- **Ada/Map toggle** — avatar view or Leaflet map with pickup/dropoff markers
- **Compact SIP** registration and call controls
- **Multi-account SIP** with save/switch profiles
- **iCabbi dispatch** integration (configurable)
- **All settings** via ToolStrip menus (clean, panel-free UI)
- **Toggleable log** panel

## Build

```bash
cd csharp-sip-bridge/AdaSdkBooker
dotnet build
dotnet run
```

## Dependencies (NuGet)

- **OpenAI** 2.1.0-beta.4 — Realtime API with G.711 A-law
- **SIPSorcery** 10.0.3 — SIP stack
- **WebView2** — Map + Avatar
- **NAudio** — Operator mic + monitor
- **MQTTnet** — MQTT dispatch
- **Microsoft.Extensions.Hosting** — DI / Logging
