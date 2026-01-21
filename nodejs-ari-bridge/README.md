# Taxi ARI Bridge

A **deterministic** taxi booking bridge using Asterisk ARI (Asterisk REST Interface).

Unlike the OpenAI Realtime approach, this uses a strict sequential flow:
- **TTS → Record → STT → LLM Extract** cycle
- AI **never** chooses the next question - the state machine does
- Each field is collected in order: pickup → destination → passengers → time

## Why Use This?

| Feature | OpenAI Realtime | ARI Bridge |
|---------|----------------|------------|
| Latency | ~300ms | ~1-2s per turn |
| AI wandering | Possible | Impossible |
| Barge-in | Yes | No |
| Stability | WebSocket drops | Very stable |
| Cost | Higher (streaming) | Lower (per-turn) |

## Architecture

```
┌─────────────┐     SIP/RTP      ┌───────────────┐
│  Caller     │◄────────────────►│   Asterisk    │
└─────────────┘                  │   (PJSIP)     │
                                 └───────┬───────┘
                                         │ ARI
                                         ▼
                                 ┌───────────────┐
                                 │  Node.js ARI  │
                                 │    Bridge     │
                                 └───────┬───────┘
                                         │ HTTP
                     ┌───────────────────┼───────────────────┐
                     ▼                   ▼                   ▼
              ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
              │  taxi-stt   │    │ taxi-extract│    │  taxi-tts   │
              │  (Whisper)  │    │  (Gemini)   │    │ (ElevenLabs)│
              └─────────────┘    └─────────────┘    └─────────────┘
```

## Setup

### 1. Asterisk Configuration

Add to `/etc/asterisk/ari.conf`:
```ini
[general]
enabled = yes
pretty = yes

[taxiapp]
type = user
read_only = no
password = supersecret
```

Add to `/etc/asterisk/http.conf`:
```ini
[general]
enabled = yes
bindaddr = 127.0.0.1
bindport = 8088
```

Add to `/etc/asterisk/extensions.conf`:
```ini
[from-whatsapp]
exten => _+X.,1,NoOp(Incoming call from ${CALLERID(num)})
 same => n,Answer()
 same => n,Stasis(taxi_ai)
 same => n,Hangup()
```

Reload Asterisk:
```bash
asterisk -rx "module reload res_ari.so"
asterisk -rx "dialplan reload"
```

### 2. Install & Run

```bash
cd nodejs-ari-bridge
cp .env.example .env
# Edit .env with your credentials

npm install
npm start
```

### 3. Test

Make a call to your Asterisk number and the bridge will:
1. Answer and greet
2. Ask "Where should we pick you up?"
3. Record your response
4. Transcribe via Whisper
5. Extract slot via Gemini
6. Move to next question
7. Confirm and book

## Flow Diagram

```
          START
            │
            ▼
    ┌───────────────┐
    │   Greeting    │
    └───────┬───────┘
            │
            ▼
    ┌───────────────┐     No
    │ pickup filled?│◄────────┐
    └───────┬───────┘         │
            │ Yes             │
            ▼                 │
    ┌───────────────┐         │
    │Ask destination│─────────┘
    └───────┬───────┘
            │
            ▼
       ... and so on for each field ...
            │
            ▼
    ┌───────────────┐
    │   Confirm?    │
    └───────┬───────┘
        Yes │ No
            │  └──► Restart
            ▼
    ┌───────────────┐
    │    Booked!    │
    └───────────────┘
```

## Configuration

All settings in `src/config.js`:

```javascript
bookingFlow: {
  fields: ['pickup', 'destination', 'passengers', 'pickup_time'],
  questions: {
    pickup: "Where should we pick you up?",
    destination: "And where are you going to?",
    // ...
  },
  maxRetries: 3,
}
```

## Key Differences from Python Bridge

1. **No WebSocket to maintain** - Uses HTTP calls per turn
2. **No real-time streaming** - Discrete TTS/STT cycles
3. **Asterisk-native** - Uses ARI recording/playback
4. **Deterministic** - Code controls flow, not AI

## Troubleshooting

### "Cannot connect to ARI"
- Check Asterisk is running: `asterisk -rx "core show version"`
- Verify HTTP module: `asterisk -rx "http show status"`
- Check ARI user exists: `asterisk -rx "ari show users"`

### Recording not working
- Ensure `/var/spool/asterisk/recording/` is writable
- Check audio format: should be `slin16`

### TTS audio garbled
- Verify TTS returns PCM16 @ 16kHz
- Check Asterisk codec negotiation
