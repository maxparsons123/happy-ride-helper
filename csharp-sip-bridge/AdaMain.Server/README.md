# AdaMain.Server — Headless Multi-Call SIP Server

A headless .NET 8 console application for running Ada Taxi AI as a Linux daemon, handling multiple concurrent SIP calls.

## Architecture

```
┌──────────────────────────────────┐
│       AdaMain.Server             │
│  (Generic Host / BackgroundSvc)  │
├──────────────────────────────────┤
│  SipServerWorker                 │  ← BackgroundService (systemd-ready)
│  CallSessionFactory              │  ← Creates sessions without WinForms
│  Program.cs                      │  ← DI + config bootstrap
├──────────────────────────────────┤
│       AdaMain (shared lib)       │
│  SipServer / SessionManager      │  ← Multi-call SIP + AI orchestration
│  CallSession / OpenAiG711Client  │  ← Per-call AI pipeline
│  FareCalculator / BsqdDispatcher │  ← Business logic
└──────────────────────────────────┘
```

## Building

```bash
# From the csharp-sip-bridge directory
dotnet publish AdaMain.Server/AdaMain.Server.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o ./publish/ada-server
```

## Configuration

Edit `appsettings.json` or use environment variables with `ADA_` prefix:

```bash
export ADA_SIP__SERVER=sip.example.com
export ADA_SIP__USERNAME=1000
export ADA_SIP__PASSWORD=secret
export ADA_OPENAI__APIKEY=sk-...
```

## Deployment (Linux VPS)

```bash
# 1. Copy published files
scp -r ./publish/ada-server user@server:/opt/ada-server

# 2. Create service user
sudo useradd -r -s /bin/false ada

# 3. Install systemd service
sudo cp /opt/ada-server/ada-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable ada-server
sudo systemctl start ada-server

# 4. View logs
sudo journalctl -u ada-server -f
```

## Multi-Call Support

The server uses `SessionManager` with `ConcurrentDictionary` to handle unlimited concurrent calls. Each incoming SIP call gets its own:

- `CallSession` — isolated booking state
- `OpenAiG711Client` — dedicated AI WebSocket connection  
- `ALawRtpPlayout` — per-call audio playout engine

## Key Differences from WinForms (AdaMain)

| Feature | AdaMain (WinForms) | AdaMain.Server |
|---------|-------------------|----------------|
| UI | Windows Forms | Headless (logs only) |
| Calls | Single call (locked) | Concurrent (unlimited) |
| Audio Monitor | Local speakers (NAudio) | Disabled |
| Simli Avatar | WebView2 | Disabled |
| Operator Mode | Push-to-talk mic | Not available |
| Config | GUI settings form | appsettings.json + env vars |
| Deployment | Windows desktop | Linux systemd daemon |
| .NET Target | net8.0-windows | net8.0 (cross-platform) |

## TODO

- [ ] Remove single-call lock from SipServer (allow concurrent calls)
- [ ] Add REST API for remote management (health, active calls, config)
- [ ] Add Prometheus metrics endpoint
- [ ] Web admin dashboard (separate Lovable project)
