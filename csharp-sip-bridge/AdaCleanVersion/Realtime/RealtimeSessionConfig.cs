using AdaCleanVersion.Audio;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Builds the OpenAI Realtime session configuration including
/// the sync_booking_data tool schema and session parameters.
/// </summary>
public static class RealtimeSessionConfig
{
    /// <summary>
    /// Build the full session.update payload with tools, audio format, VAD, and system prompt.
    /// </summary>
    public static object Build(string systemPrompt, string voice, G711CodecType codec)
    {
        var audioFormat = codec == G711CodecType.PCMU ? "g711_ulaw" : "g711_alaw";

        var tools = new object[]
        {
            new
            {
                type = "function",
                name = "sync_booking_data",
                description = ToolDescription,
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["caller_name"] = new { type = "string", description = "Caller's name if spoken." },
                        ["caller_area"] = new { type = "string", description = "Caller's self-reported area/district (location bias only)." },
                        ["pickup"] = new { type = "string", description = "Pickup address EXACTLY as spoken. VERBATIM." },
                        ["destination"] = new { type = "string", description = "Destination address EXACTLY as spoken. VERBATIM." },
                        ["passengers"] = new { type = "integer", description = "Number of passengers." },
                        ["pickup_time"] = new { type = "string", description = "ASAP or YYYY-MM-DD HH:MM (24h)." },
                        ["intent"] = new { type = "string", description = "One of: update_field, confirm, decline, cancel, amend. Default: update_field.", @enum = new[] { "update_field", "confirm", "decline", "cancel", "amend" } },
                        ["special_instructions"] = new { type = "string", description = "Any special request spoken by the caller." },
                        ["interpretation"] = new { type = "string", description = "Explanation of understanding. Must include correction markers if applicable." },
                        ["last_utterance"] = new { type = "string", description = "Full raw transcript of caller's latest speech. Must match transcript exactly." }
                    },
                    required = new[] { "interpretation", "last_utterance" }
                }
            }
        };

        return new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                voice,
                instructions = systemPrompt,
                input_audio_format = audioFormat,
                output_audio_format = audioFormat,
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.6,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500
                },
                tools,
                tool_choice = "required",
                temperature = 0.8
            }
        };
    }

    /// <summary>
    /// Build a greeting conversation.item.create payload.
    /// </summary>
    public static object BuildGreetingItem(string greetingMessage)
    {
        return new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[]
                {
                    new { type = "input_text", text = greetingMessage }
                }
            }
        };
    }

    /// <summary>
    /// Build a greeting response.create payload.
    /// </summary>
    public static object BuildGreetingResponse()
    {
        return new
        {
            type = "response.create",
            response = new
            {
                modalities = new[] { "text", "audio" },
                instructions = "You must speak exactly one concise greeting question, then stop and wait for caller response. Do not add booking assumptions."
            }
        };
    }

    // ─── Tool Description ───────────────────────────────────

    private const string ToolDescription = @"
MANDATORY BOOKING SYNC TOOL — DISPATCH CRITICAL

This tool is the ONLY mechanism for persisting booking data.

You MUST call this tool BEFORE generating any text/audio response
whenever the caller provides, changes, corrects, clarifies,
or confirms ANY booking detail.

────────────────────────────────────────
SUPPORTED FIELDS
────────────────────────────────────────
- caller_name
- caller_area
- pickup
- destination
- passengers
- pickup_time
- special_instructions

────────────────────────────────────────
FREEFORM BURST RULE (CRITICAL)
────────────────────────────────────────
If the caller provides multiple details in ONE sentence
(e.g. 'from 52A David Road going to Manchester with 3 passengers at 6pm'),
you MUST include ALL mentioned fields in ONE SINGLE tool call.

NEVER split a compound utterance into multiple calls.
NEVER ignore any mentioned field.

────────────────────────────────────────
CORRECTION OVERRIDE RULE (STRICT)
────────────────────────────────────────
If the caller provides ANY new pickup or destination value,
you MUST completely REPLACE the previous value.

You are STRICTLY FORBIDDEN from:
- Reusing a previous address
- Keeping a field unchanged when a new value was spoken
- Partially merging old and new addresses
- Substituting similar or previously confirmed places
- Autocompleting from memory

The new value must reflect ONLY what was spoken
in the most recent utterance.

Failure to overwrite stale values is a SYSTEM ERROR.

────────────────────────────────────────
VERBATIM COPY RULE (ADDRESS INTEGRITY)
────────────────────────────────────────
pickup and destination MUST be copied EXACTLY as spoken
in the transcript.

Do NOT:
- Normalize
- Correct spelling
- Expand abbreviations
- Replace with similar place names
- Substitute previously confirmed addresses
- INSERT HYPHENS into house numbers (e.g. ""1214A"" must NOT become ""12-14A"")

Copy the raw spoken phrase character-for-character.
""1214A"" is a SINGLE house number, NOT a range ""12-14A"".

Even if the phrase sounds incomplete or unusual,
you must copy it exactly.

────────────────────────────────────────
INTERPRETATION FIELD (MANDATORY LOGIC TRACE)
────────────────────────────────────────
The 'interpretation' field MUST:

1) Briefly explain what you understood.
2) If ANY field changed, include:

   [CORRECTION:<field>] Changed from '<old>' to '<new>'

Example:
[CORRECTION:destination] Changed from 'Piccadilly, 14 Argyle Street'
to 'Peak in the Middle on Fargosworth Street'

If no correction occurred, state:
No corrections. Extracted fields: ...

────────────────────────────────────────
LAST UTTERANCE BINDING RULE
────────────────────────────────────────
You MUST include the FULL raw transcript of the caller's
latest speech in the 'last_utterance' parameter.

It must match the transcript exactly.
Do NOT modify or summarize it.

This prevents stale slot reuse.

────────────────────────────────────────
PARTIAL DATA RULE
────────────────────────────────────────
If the caller provides only one field,
include only that field.

DO NOT fabricate missing values.

────────────────────────────────────────
TIME RULE
────────────────────────────────────────
pickup_time must be:
- 'ASAP'
OR
- formatted YYYY-MM-DD HH:MM (24h clock)

Use the REFERENCE_DATETIME provided in system instructions
to resolve relative phrases like 'tomorrow at 6'.

────────────────────────────────────────
SAFETY RULE
────────────────────────────────────────
If uncertain whether a field changed,
treat it as changed and overwrite it.

Never assume previous values remain valid.
";
}
