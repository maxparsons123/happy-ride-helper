# AdaAudioPipe

Standalone audio pipeline library for OpenAI Realtime → G.711 A-law SIP telephony.

## Architecture

```
OpenAI Realtime API
  │  response.audio.delta (PCM16 24kHz)
  ▼
┌─────────────────────────┐
│   OpenAiToSipPipe       │
│                         │
│  ┌───────────────────┐  │
│  │  IAudioPlugin     │  │ ← Your DSP (de-ess, warm, soft-clip, resample)
│  │  PCM → A-law      │  │
│  └────────┬──────────┘  │
│           ▼             │
│  Channel<byte[]>        │ ← Bounded, DropOldest, SingleReader
│  (max 240 frames/4.8s)  │
│           ▼             │
│  PumpLoop (single)      │ ← Optional ALawGain
│           ▼             │
│  IAlawFrameSink         │ → ALawRtpPlayout.BufferALaw()
│                         │
│  ┌───────────────────┐  │
│  │ ISimliPcmSink     │  │ ← Optional fork: PCM16 16kHz for avatar
│  └───────────────────┘  │
└─────────────────────────┘
```

## Key Guarantees

- **No double speaking**: Single consumer via `Channel<T>` with `SingleReader=true`
- **Bounded memory**: `DropOldest` mode prevents runaway bursts
- **Latency clamp**: Drops oldest frames when overloaded (newest speech wins)
- **Clean lifecycle**: `Start()` / `Stop()` per call, with plugin state reset
- **Event guard**: `Interlocked.Exchange` prevents duplicate subscriptions

## Usage

```csharp
// Create pipe (once per call)
var pipe = new OpenAiToSipPipe(
    plugin: audioPlugin,        // your IAudioPlugin (AudioProcessorPlugin)
    sink: playoutSink,          // IAlawFrameSink wrapper around ALawRtpPlayout
    maxFrames: 240,             // 4.8s buffer
    dropBatch: 20,              // drop 0.4s when overloaded
    alawGain: 1.2f              // optional volume boost
);

pipe.OnLog += msg => logger.LogInformation(msg);
pipe.Start();

// Wire to OpenAI client
aiClient.OnAudioPcm += pipe.PushPcm;

// On barge-in
pipe.Clear();

// On call end
aiClient.OnAudioPcm -= pipe.PushPcm;
pipe.Stop();
pipe.Dispose();
```

## Building as DLL

```bash
dotnet build -c Release
# Output: bin/Release/net8.0/AdaAudioPipe.dll
```
