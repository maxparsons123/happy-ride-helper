# AdaMain - Standalone Voice AI Taxi Bridge

A clean, modular SIP-to-OpenAI voice bridge for taxi booking.

## Architecture

```
AdaMain/
├── Program.cs              # Entry point with DI setup
├── appsettings.json        # Configuration
├── Config/
│   └── Settings.cs         # Strongly-typed configuration
├── Core/
│   ├── ICallSession.cs     # Session interface
│   ├── CallSession.cs      # Session implementation
│   ├── SessionManager.cs   # Active session management
│   └── BookingState.cs     # Booking data model
├── Audio/
│   ├── IAudioCodec.cs      # Codec interface
│   ├── G711Codec.cs        # μ-law and A-law codecs
│   ├── AudioResampler.cs   # Rate conversion (8k ↔ 24k)
│   └── TelephonyDsp.cs     # Volume boost, pre-emphasis
├── Ai/
│   ├── IOpenAiClient.cs    # AI client interface
│   └── OpenAiRealtimeClient.cs  # OpenAI Realtime API
├── Sip/
│   ├── SipServer.cs        # SIP registration & call handling
│   ├── SipConfig.cs        # Transport configuration
│   └── CallerIdExtractor.cs # Phone extraction from headers
└── Services/
    ├── IFareCalculator.cs  # Fare calculation interface
    ├── FareCalculator.cs   # Geocoding + pricing
    ├── IDispatcher.cs      # Dispatch interface
    └── BsqdDispatcher.cs   # BSQD API + WhatsApp
```

## Key Design Principles

1. **Interface-based**: All major components have interfaces for testability
2. **Dependency Injection**: Full DI container for clean wiring
3. **Single Responsibility**: Each class has one job
4. **Stateless Services**: Business logic services are stateless
5. **Session Isolation**: Each call gets its own session instance

## Configuration

Edit `appsettings.json`:

```json
{
  "Sip": {
    "Server": "sip.example.com",
    "Port": 5060,
    "Username": "1001",
    "Password": "secret"
  },
  "OpenAi": {
    "ApiKey": "sk-..."
  }
}
```

Or use environment variables:
```bash
export OpenAi__ApiKey=sk-...
export Sip__Server=sip.example.com
```

## Building

```bash
cd AdaMain
dotnet build
dotnet run
```

## Call Flow

1. **SIP INVITE** → `SipServer` answers call
2. **Session Created** → `SessionManager` creates `CallSession`
3. **Audio Flow**:
   - Inbound: RTP → G.711 decode → Resample 8k→24k → DSP → OpenAI
   - Outbound: OpenAI → Resample 24k→8k → G.711 encode → RTP
4. **Tool Calls**: `sync_booking_data` → `book_taxi` → `end_call`
5. **Dispatch**: BSQD webhook + WhatsApp notification
6. **Cleanup**: Session disposed, SIP BYE sent

## Adding New Features

### New Tool
1. Add tool definition in `OpenAiRealtimeClient.GetTools()`
2. Add handler in `CallSession.HandleToolCallAsync()`

### New Codec
1. Implement `IAudioCodec`
2. Register in `G711CodecFactory.Create()`

### New Notification Channel
1. Add method to `IDispatcher`
2. Implement in `BsqdDispatcher`
