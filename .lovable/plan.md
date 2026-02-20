
# Fix: Audio Jitter Reduction

## Problem Summary

The jitter has one core cause: the jitter buffer re-enters a **200ms buffering hold** every time the frame queue briefly touches zero between OpenAI audio bursts. OpenAI does not send audio in perfectly-spaced 20ms packets — it bursts chunks over WebSocket, which means the queue naturally oscillates between "very full" and "empty" between responses. Each time it hits zero, the playout engine halts real audio and forces 200ms of silence before resuming, creating the "stutter" the caller hears.

Evidence from logs:
- `underruns=3–5` per call despite healthy `avgQueue=63–317`
- The underruns happen *mid-sentence*, not at natural pauses

## Root Causes (Code-Level)

**1. Zero-queue rebuffer is too hair-trigger (`ALawRtpPlayout.cs`, line 346)**
The current rule is: "if queue == 0 after dequeue → immediately set `_isBuffering = true` and wait for 10 frames."
This fires on every inter-burst gap, not just genuine stalls.

**2. Rebuffer threshold (10 frames = 200ms) is too high for restart**
Once the buffer re-enters buffering mode, it waits for 200ms of audio to arrive before resuming. For a gap that only lasted 20–40ms, this forces a silence bubble much longer than the original gap.

**3. Double-path `_isBuffering` activation (lines 346–366)**
Both the "queue naturally drained" path and the emergency "nothing to dequeue" path activate rebuffering identically — there is no distinction between a 1-frame gap and a genuine stall.

## Fixes

### Fix 1 — Introduce a short underrun grace window (primary fix)
Instead of immediately triggering rebuffering when the queue hits zero, allow up to **3 consecutive empty dequeues** (60ms) before entering rebuffering mode. This absorbs the inter-burst gaps that OpenAI naturally produces. Only a sustained 60ms+ silence triggers the 200ms rebuffer hold.

Change in `ALawRtpPlayout.cs`:
- Add `int _consecutiveUnderruns` counter field
- In `SendNextFrame()`: on empty dequeue, increment counter; only set `_isBuffering = true` when counter exceeds 3
- On successful dequeue: reset `_consecutiveUnderruns = 0`

### Fix 2 — Reduce rebuffer start threshold from 200ms to 100ms (for restart only)
The initial start of playout still uses 200ms (10 frames) — that is correct and prevents the first-word grumble. But *mid-call restarts* after an underrun should only wait for 5 frames (100ms), since the pipeline is already warm and OpenAI is actively streaming.

Change in `ALawRtpPlayout.cs`:
- Add `bool _hasPlayedAtLeastOneFrame` flag set on first real frame sent
- Use `JITTER_BUFFER_START_THRESHOLD = 10` (200ms) for first start
- Use `JITTER_BUFFER_RESUME_THRESHOLD = 5` (100ms) for all subsequent restarts

### Fix 3 — Suppress `OnQueueEmpty` during grace window
The `OnQueueEmpty` event currently fires immediately when the queue hits zero, which triggers the AI playout-complete logic and can interrupt the mid-sentence echo guard too early.

Change: Only fire `OnQueueEmpty` after the grace window expires (same 3-frame check), so the session's `NotifyPlayoutComplete()` is not triggered for transient inter-burst gaps.

## Files to Change

**`csharp-sip-bridge/AdaSdkModel/Audio/ALawRtpPlayout.cs`**

Fields to add (near existing jitter/state tracking, around line 88):
```
private int _consecutiveUnderruns;
private bool _hasPlayedAtLeastOneFrame;
private const int JITTER_BUFFER_RESUME_THRESHOLD = 5;  // 100ms — warm restart
```

`Start()` method: reset `_consecutiveUnderruns = 0` and `_hasPlayedAtLeastOneFrame = false`.

`Clear()` method: reset `_consecutiveUnderruns = 0`.

`SendNextFrame()` — replace the underrun logic (lines 338–367):

Current flow:
```
if TryDequeue → send → if queue==0 → isBuffering=true, fire OnQueueEmpty
else (emergency) → isBuffering=true, fire OnQueueEmpty
```

New flow:
```
if TryDequeue → send → hasPlayedAtLeastOneFrame=true → reset consecutiveUnderruns=0
  → if queue==0: check grace (no immediate rebuffer, just note it)
else:
  consecutiveUnderruns++
  if consecutiveUnderruns == 1: send silence (absorb gap)
  if consecutiveUnderruns >= 3: isBuffering=true, fire OnQueueEmpty, totalUnderruns++
  else: send silence frame (fill gap without triggering rebuffer)
```

Rebuffer threshold check (line 324):
```csharp
int resumeThreshold = _hasPlayedAtLeastOneFrame 
    ? JITTER_BUFFER_RESUME_THRESHOLD 
    : JITTER_BUFFER_START_THRESHOLD;
if (queueCount < resumeThreshold) { ... }
```

## Technical Notes

- No changes needed to `SipServer.cs` — the fix is entirely within the playout engine
- The typing sound generator is unaffected — it still fills during genuine rebuffer holds
- The 200ms initial start threshold stays, preserving the first-word quality
- `TotalUnderruns` counter in stats will only increment on genuine 60ms+ stalls, giving cleaner diagnostics
- `avgQueue` in the 30s stats log will remain unchanged — useful for ongoing monitoring

## Expected Result

- Underruns per call: from 3–5 down to 0–1 (only genuine stalls)
- No more mid-sentence silence bubbles from inter-burst gaps
- First word of each response still clean (200ms initial buffer unchanged)
- `OnQueueEmpty` / `NotifyPlayoutComplete` only fires at real response end, fixing occasional echo guard timing issues
