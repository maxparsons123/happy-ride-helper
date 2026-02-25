# AdaCleanVersion — Deterministic Collection + Single AI Extraction

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                  CleanCallSession                    │
│                                                     │
│  ┌──────────────┐    ┌─────────────────────────┐   │
│  │ CallState    │    │ AI Voice Interface       │   │
│  │ Engine       │───▶│ (response to INSTRUCTION)│   │
│  │ (deterministic)   │ No tools, no state      │   │
│  └──────┬───────┘    └─────────────────────────┘   │
│         │                                           │
│  ┌──────▼───────┐                                  │
│  │ RawBooking   │  Mutable raw slots               │
│  │ Data         │  Verbatim caller phrases          │
│  └──────┬───────┘                                  │
│         │ (all slots filled)                        │
│  ┌──────▼───────┐                                  │
│  │ Extraction   │  Single AI pass                  │
│  │ Service      │  Pure normalization              │
│  └──────┬───────┘                                  │
│         │                                           │
│  ┌──────▼───────┐                                  │
│  │ Structured   │  Authoritative booking           │
│  │ Booking      │  Immutable result                │
│  └──────────────┘                                  │
└─────────────────────────────────────────────────────┘
```

## Key Principles

1. **AI never controls state** — Engine decides transitions
2. **Raw slots are mutable** — Corrections overwrite, no re-interpretation  
3. **Single extraction pass** — AI normalizes once when all data collected
4. **No mid-flow hallucination** — AI cannot trigger booking/cancellation
5. **Structured payload to AI** — Raw slots only, never full transcript

## Flow

1. Greeting → Collect Name → Collect Pickup → Collect Destination → Collect Passengers → Collect Time
2. All slots filled → Single AI extraction call
3. Extraction result → Validate → Geocode → Calculate fare
4. Present fare → Payment choice → Final confirmation → Dispatch

## Files

- `Models/RawBookingData.cs` — Mutable raw slot storage
- `Models/StructuredBooking.cs` — Immutable extraction result  
- `Models/ExtractionRequest.cs` — Payload for AI extraction
- `Engine/CollectionState.cs` — State enum
- `Engine/CallStateEngine.cs` — Deterministic state machine
- `Engine/PromptBuilder.cs` — AI instruction builder
- `Session/CleanCallSession.cs` — Session orchestrator
- `Services/IExtractionService.cs` — Extraction interface
