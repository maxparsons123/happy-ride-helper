# STT Corrections for Telephony

The `OpenAIRealtimeClient` includes STT corrections for common Whisper mishearings over telephony audio.

## Root Cause Fix: High-Quality Resampling

**Primary Issue:** Poor transcription accuracy was caused by **linear interpolation** when upsampling 8kHz telephony audio to OpenAI's required 24kHz. Linear interpolation introduces spectral artifacts that degrade speech clarity.

**Solution:** The `AudioCodecs.Resample()` function now uses **SpeexDSP (Quality 8)** for 8kHz→24kHz upsampling, which provides proper anti-imaging filtering and significantly clearer audio for Whisper.

### Audio Quality Verification

Check logs for:
```
🎵 AudioCodecs: SpeexDSP available ✓
```

If you see this warning instead, the DLL is missing:
```
⚠️ AudioCodecs: SpeexDSP unavailable, using linear interpolation
```

Ensure `libspeexdsp.dll` (x64) is in the application directory.

## STT Corrections (Backup Layer)

When a user transcript is received from OpenAI's Whisper, the `ApplySttCorrections()` method:

1. Checks for exact matches in the corrections dictionary
2. Applies partial/substring replacements for common mishearings
3. Logs when corrections are applied: `🔧 STT partial fix: "raw" → "fixed"`

### Adding New Corrections

Edit the `SttCorrections` dictionary in `OpenAIRealtimeClient.cs`:

```csharp
private static readonly Dictionary<string, string> SttCorrections = new(StringComparer.OrdinalIgnoreCase)
{
    // Add your corrections here
    { "mishearing pattern", "correct value" },
};
```

For partial/substring corrections (e.g., street names):
```csharp
private static readonly (string Bad, string Good)[] PartialSttCorrections = new[]
{
    ("Waters Street", "Russell Street"),
};
```

## Common Correction Categories

### Address Numbers
- Phonetic mishearings like "52 I ain't dead bro" → "52A David Road"

### Pickup Times
- "for now", "in four now" → "now"

### Confirmations
- "yep", "yeah", "correct" → "yes"

## Testing

1. Watch logs for SpeexDSP availability on startup
2. Test with known addresses that were previously misheard
3. If mishearings persist, add corrections and check audio levels
