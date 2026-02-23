# AdaAudioPipe

Standalone audio pipeline library for OpenAI Realtime → G.711 A-law SIP telephony.

## Architecture

```
OpenAI Realtime API
  │  response.audio.delta
  │  (G.711 A-law native OR PCM16 24kHz)
  ▼
┌─────────────────────────────┐
│   OpenAiToSipPipe           │
│                             │
│  A-law mode (native):       │
│  ┌───────────────────────┐  │
│  │  AlawFrameAccumulator │  │ ← Aligns raw bytes to 160-byte frames
│  └────────┬──────────────┘  │
│           │                 │
│  PCM mode (optional):       │
│  ┌───────────────────────┐  │
│  │  IAudioPlugin         │  │ ← DSP: de-ess, warm, soft-clip, resample
│  │  PCM → A-law          │  │
│  └────────┬──────────────┘  │
│           ▼                 │
│  Channel<byte[]>            │ ← Bounded, DropOldest, SingleReader
│  (max 240 frames/4.8s)      │
│           ▼                 │
│  PumpLoop (single consumer) │ ← Optional ALawGain
│           │                 │
│  OnFrameOut ──→ Simli/monitor fork
│           ▼                 │
│  IAlawFrameSink             │ → ALawRtpPlayout.BufferALaw()
└─────────────────────────────┘
```

## Key Guarantees

- **No double speaking**: Single consumer via `Channel<T>` with `SingleReader=true`
- **Bounded memory**: `DropOldest` mode prevents runaway bursts
- **Latency clamp**: Drops oldest frames when overloaded (newest speech wins)
- **Clean lifecycle**: `Start()` / `Stop()` per call, with plugin state reset
- **Event guard**: `Interlocked.Exchange` prevents duplicate subscriptions
- **Dual mode**: Supports both native A-law (OpenAI G.711) and PCM (with plugin)

## Wiring (AdaSdkModel)

```csharp
// In SipServer.WireAudioPipeline:
var pipe = new OpenAiToSipPipe(
    sink: new PlayoutSink(playout),  // IAlawFrameSink → ALawRtpPlayout
    plugin: null,                    // null = A-law mode (OpenAI G.711 native)
    maxFrames: 240,
    dropBatch: 20,
    alawGain: 1.2f
);
pipe.Start();

// Wire raw A-law from OpenAI (single subscription = no double speaking)
sdkClient.OnAudioRaw += pipe.PushAlaw;

// On barge-in
pipe.Clear();

// On call end
sdkClient.OnAudioRaw -= pipe.PushAlaw;
pipe.Dispose();
```

## Building as DLL

```bash
dotnet build -c Release
# Output: bin/Release/net8.0/AdaAudioPipe.dll
```
