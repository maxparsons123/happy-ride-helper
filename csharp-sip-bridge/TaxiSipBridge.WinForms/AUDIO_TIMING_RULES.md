# Audio Timing Rules (CRITICAL - DO NOT VIOLATE)

## Version 3.4 - Audio Lifecycle Contract

These rules are **mandatory** for clean, professional audio quality. Violations cause clipped consonants, "crispy" starts, and overlapping speech.

---

## RULE 1: Never Manually Reset Response State

```csharp
// ❌ FORBIDDEN - causes audio desync
Interlocked.Exchange(ref _responseActive, 0);
Interlocked.Exchange(ref _responseQueued, 0);
_activeResponseId = null;
```

**Why:** OpenAI manages its own response lifecycle. Forcing resets desyncs your state from OpenAI's state, causing:
- Overlapping responses
- Premature `input_audio_buffer.clear`
- Whisper hearing partial speech
- Ada starting mid-phoneme

**Correct approach:** `_responseActive` is **OBSERVED** via OpenAI events (`response.created` sets to 1, `response.done` sets to 0), never **SET** manually.

---

## RULE 2: Gate Response Creation on Transcript Arrival

```csharp
// ✅ CORRECT - waits for Whisper transcript
private bool CanCreateResponse()
{
    return Volatile.Read(ref _responseActive) == 0 &&
           Volatile.Read(ref _responseQueued) == 0 &&
           Volatile.Read(ref _transcriptPending) == 0 &&  // 🔥 REQUIRED
           Volatile.Read(ref _callEnded) == 0 &&
           Volatile.Read(ref _disposed) == 0 &&
           IsConnected &&
           NowMs() - Volatile.Read(ref _lastUserSpeechAt) > 300;
}
```

**Why:** Without `_transcriptPending`, the sequence becomes:
1. `speech_stopped`
2. `response.create` (too early!)
3. `input_audio_buffer.clear`
4. Whisper transcript arrives TOO LATE

Whisper then transcribes truncated consonants, chopped words, and partial syllables.

---

## RULE 3: Clear Input Audio ONLY on `response.created`

```csharp
// ✅ CORRECT - clear happens at the right moment
case "response.created":
    _ = ClearInputAudioBufferAsync();
    break;
```

**Do NOT:**
- Add additional clears elsewhere
- Move the clear to a different event
- Clear on `response.done` or transcript arrival

The gate in Rule 2 ensures `response.created` only fires after transcription completes.

---

## Mental Model

> **Bad timing sounds like bad audio**

95% of "audio problems" in realtime AI systems are actually:
- Lifecycle violations
- Premature buffer clears
- Turn overlap

If audio sounds clipped or crispy, check timing before checking DSP/codec.

---

## Version History

| Version | Changes |
|---------|---------|
| 3.4 | Fixed response lifecycle - no manual state resets, transcript-gated responses |
| 3.3 | Added implicit correction detection, STT corrections for Coventry |
| 3.2 | Turn finalization protocol, buffer clear timing |
| 2.5 | Transcript pending flag introduced |
