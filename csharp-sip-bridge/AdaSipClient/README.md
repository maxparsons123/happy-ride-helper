# Ada SIP Client v1.0

Clean-architecture WinForms SIP client for taxi AI dispatch.

## Project Structure

```
AdaSipClient/
├── Core/
│   ├── AppState.cs          # Centralised state (no UI coupling)
│   └── ILogSink.cs          # Logging abstraction
├── Sip/
│   ├── ISipService.cs       # SIP abstraction interface
│   └── SipService.cs        # SIPSorcery implementation (TODO)
├── Audio/
│   ├── IAudioPipeline.cs    # Pluggable audio pipeline interface
│   └── VolumeControl.cs     # A-law volume gain control
├── UI/
│   ├── Theme.cs             # Colours, fonts, control factories
│   └── Panels/
│       ├── SipLoginPanel.cs     # SIP credentials + connect
│       ├── CallControlPanel.cs  # Mode select + answer/reject/hangup
│       ├── VolumePanel.cs       # Input/output volume sliders
│       ├── AvatarPanel.cs       # Simli viewport placeholder
│       └── LogPanel.cs          # Scrollable log viewer
├── MainForm.cs              # Layout assembly + event wiring
├── Program.cs               # Entry point
└── AdaSipClient.csproj      # Project file
```

## Design Principles

- **Panels own their UI** — each UserControl manages its own layout and events
- **AppState is the single source of truth** — panels read/write state, fire events
- **Services are interface-driven** — ISipService, IAudioPipeline, ILogSink
- **Theme is centralised** — change colours/fonts in one place
- **No business logic in MainForm** — it only assembles panels and wires events

## Call Modes

| Mode | Behaviour |
|------|-----------|
| **Auto Bot** | Auto-answer → route audio to OpenAI G.711 Realtime API |
| **Manual Listen** | Show answer/reject buttons → mic/speaker passthrough |

## TODO

- [ ] Wire SIPSorcery registration + RTP in `SipService.cs`
- [ ] Implement `BotAudioPipeline` (G.711 OpenAI Realtime)
- [ ] Implement `ManualAudioPipeline` (NAudio mic/speaker)
- [ ] Wire Simli avatar via WebView2 or API
- [ ] Persist settings (JSON file or registry)
