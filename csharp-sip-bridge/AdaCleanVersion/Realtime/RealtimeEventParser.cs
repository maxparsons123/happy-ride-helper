using System.Text.Json;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Parses raw JSON from the OpenAI Realtime WebSocket into typed RealtimeEvent records.
/// Isolated from all business logic — pure protocol parsing.
/// </summary>
public static class RealtimeEventParser
{
    public static RealtimeEvent Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            return type switch
            {
                "response.audio.delta" or "response.output_audio.delta" => new RealtimeEvent
                {
                    Type = RealtimeEventType.AudioDelta,
                    AudioBase64 = root.GetProperty("delta").GetString()
                },

                "response.created" => new RealtimeEvent
                {
                    Type = RealtimeEventType.ResponseCreated
                },

                "response.audio.started" => new RealtimeEvent
                {
                    Type = RealtimeEventType.AudioStarted
                },

                "response.audio.done" => new RealtimeEvent
                {
                    Type = RealtimeEventType.AudioDone
                },

                "response.function_call_arguments.done" => new RealtimeEvent
                {
                    Type = RealtimeEventType.ToolCallDone,
                    ToolCallId = root.TryGetProperty("call_id", out var c) ? c.GetString() : null,
                    ToolName = root.TryGetProperty("name", out var n) ? n.GetString() : null,
                    ToolArgsJson = root.TryGetProperty("arguments", out var a) ? a.GetString() : "{}"
                },

                "conversation.item.input_audio_transcription.completed" => new RealtimeEvent
                {
                    Type = RealtimeEventType.CallerTranscript,
                    Transcript = root.GetProperty("transcript").GetString()
                },

                "response.audio_transcript.done" => new RealtimeEvent
                {
                    Type = RealtimeEventType.AdaTranscriptDone,
                    Transcript = root.GetProperty("transcript").GetString()
                },

                "input_audio_buffer.speech_started" => new RealtimeEvent
                {
                    Type = RealtimeEventType.SpeechStarted
                },

                "input_audio_buffer.speech_stopped" => new RealtimeEvent
                {
                    Type = RealtimeEventType.SpeechStopped
                },

                "response.canceled" => new RealtimeEvent
                {
                    Type = RealtimeEventType.ResponseCanceled
                },

                "session.created" => new RealtimeEvent
                {
                    Type = RealtimeEventType.SessionCreated
                },

                "session.updated" => new RealtimeEvent
                {
                    Type = RealtimeEventType.SessionUpdated
                },

                "error" => ParseError(root),

                _ => new RealtimeEvent { Type = RealtimeEventType.Unknown }
            };
        }
        catch
        {
            return new RealtimeEvent { Type = RealtimeEventType.Unknown };
        }
    }

    private static RealtimeEvent ParseError(JsonElement root)
    {
        string? msg = null;
        if (root.TryGetProperty("error", out var err) &&
            err.TryGetProperty("message", out var m))
            msg = m.GetString();

        return new RealtimeEvent
        {
            Type = RealtimeEventType.Error,
            ErrorMessage = msg
        };
    }
}
