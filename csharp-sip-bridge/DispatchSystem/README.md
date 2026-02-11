# Ada Dispatch System

Standalone WinForms (.NET 8) taxi dispatch application with SQLite, MQTT, and live mapping.

## Features

- **SQLite Database** — Local persistent storage for drivers, jobs, and allocation history
- **MQTT Integration** — Receives driver GPS (`drivers/+/location`), bookings (`taxi/bookings`), and job status updates via HiveMQ broker
- **Live Map** — WebView2 + Leaflet/OpenStreetMap showing real-time driver positions and passenger pickup markers
- **Auto-Dispatch Algorithm** — Runs every 60 seconds, allocates pending jobs using a weighted score:
  - 60% proximity (Haversine distance to pickup)
  - 40% driver wait time (longest idle gets priority)
  - Filters by vehicle type compatibility
- **Manual Dispatch** — Select a job + driver from the lists and click "Manual Dispatch"
- **Driver Management** — Add drivers manually or auto-register from MQTT GPS messages

## Architecture

```
DispatchSystem/
├── Program.cs                    # Entry point
├── MainForm.cs                   # Main window with layout
├── Data/
│   ├── Models.cs                 # Driver, Job, enums
│   └── DispatchDb.cs             # SQLite CRUD
├── Dispatch/
│   └── AutoDispatcher.cs         # Allocation algorithm
├── Mqtt/
│   └── MqttDispatchClient.cs     # MQTT pub/sub
└── UI/
    ├── MapPanel.cs               # WebView2 Leaflet map
    ├── JobListPanel.cs           # Active jobs list
    ├── DriverListPanel.cs        # Driver status list
    ├── LogPanel.cs               # Scrolling log
    └── AddDriverDialog.cs        # Add driver form
```

## Requirements

- .NET 8 SDK (Windows)
- WebView2 Runtime (usually pre-installed on Windows 10/11)

## Build & Run

```bash
cd csharp-sip-bridge/DispatchSystem
dotnet restore
dotnet run
```

## MQTT Topics

| Topic | Direction | Description |
|-------|-----------|-------------|
| `drivers/{id}/location` | Subscribe | Driver GPS + status updates |
| `taxi/bookings` | Subscribe | New booking requests |
| `jobs/{id}/status` | Subscribe | Job status changes |
| `drivers/{id}/jobs` | Publish | Job allocation to driver |
| `jobs/{id}/allocated` | Publish | Allocation confirmation |

## Auto-Dispatch Scoring

```
Score = 0.6 × (1 - normalizedDistance) + 0.4 × normalizedWaitTime
```

- **normalizedDistance**: driver distance to pickup / max distance in pool
- **normalizedWaitTime**: time since last job completion / max wait in pool
- Vehicle type hierarchy: Saloon < Estate < MPV < Executive < Minibus
