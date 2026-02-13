# AdaSdkBooker — AI Taxi Booking System

All-in-one taxi booking application with integrated AI voice assistant, built on the AdaSdkModel engine.

## Architecture

AdaSdkBooker **references AdaSdkModel** as a project dependency — all SIP, AI, audio, and dispatch logic is shared. Only the UI layer is unique.

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
- **All settings** via ToolStrip menus (clean, panel-free UI)
- **Toggleable log** panel

## Build

```bash
cd csharp-sip-bridge/AdaSdkBooker
dotnet build
dotnet run
```

## Dependencies

- **AdaSdkModel** (project reference) — SIP, AI, Audio, Dispatch, Config
- **WebView2** — Map + Avatar
- **NAudio** — Operator mic + monitor
