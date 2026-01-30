# STT Corrections for Telephony

The `OpenAIRealtimeClient` now includes an STT corrections dictionary to fix common Whisper mishearings over telephony audio.

## How It Works

When a user transcript is received from OpenAI's Whisper, the `ApplySttCorrections()` method:

1. Checks for exact matches in the corrections dictionary
2. Checks for partial matches (phrase contained in transcript)
3. Replaces mishearings with the correct value
4. Logs when corrections are applied: `👤 User: "raw" → 🔧 STT corrected: "fixed"`

## Adding New Corrections

Edit the `SttCorrections` dictionary in `OpenAIRealtimeClient.cs`:

```csharp
private static readonly Dictionary<string, string> SttCorrections = new(StringComparer.OrdinalIgnoreCase)
{
    // Add your corrections here
    { "mishearing pattern", "correct value" },
};
```

## Common Correction Categories

### Address Numbers
- Phonetic mishearings like "52 I ain't dead bro" → "52A David Road"
- Letter/number confusion like "62A" → "52A"

### Street Names  
- "Seven Street" → "7 Maple Street"

### Pickup Times
- "for now", "in four now", "ASAP" → "now"

### Passenger Counts
- "to passengers" → "2 passengers"
- "tree passengers" → "3 passengers"

### Confirmations
- "yep", "yeah", "correct" → "yes"

## Testing

Watch the logs for the 🔧 STT corrected marker to see when corrections are being applied.
