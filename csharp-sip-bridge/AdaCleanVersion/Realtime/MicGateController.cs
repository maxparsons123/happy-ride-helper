namespace AdaCleanVersion.Realtime;

/// <summary>
/// Deterministic mic gate controller (v4.5).
/// Buffer-all while AI speaks, flush-tail on barge-in.
/// Variance-based energy detection prevents ghost transcripts from comfort noise.
/// </summary>
public sealed class MicGateController
{
    /// <summary>Max trailing frames to flush (25 frames = 500ms).</summary>
    private const int MicTailMaxFlush = 25;

    /// <summary>
    /// Min variance score in a 160-byte frame to count as speech.
    /// Variance-based detection catches low-level speech that byte-equality misses
    /// (comfort noise, PBX DSP artifacts, transcoding residue).
    /// </summary>
    private const int SpeechVarianceThreshold = 120;

    private readonly List<byte[]> _buffer = new();
    private readonly object _lock = new();
    private volatile bool _gated;
    private volatile bool _responseCompleted;
    private long _lastBargeInTick;

    /// <summary>True = mic is blocked (AI is speaking).</summary>
    public bool IsGated => _gated;

    /// <summary>True = AI finished sending audio deltas for current response.</summary>
    public bool ResponseCompleted => _responseCompleted;

    /// <summary>Gate mic when AI starts responding.</summary>
    public void Arm()
    {
        _gated = true;
        _responseCompleted = false;
    }

    /// <summary>Mark that response.audio.done was received.</summary>
    public void MarkResponseCompleted()
    {
        _responseCompleted = true;
    }

    /// <summary>
    /// Ungate after playout drains — discard buffer (it's all echo, not caller speech).
    /// Returns true if actually ungated, false if already ungated.
    /// </summary>
    public bool TryRelease()
    {
        if (!_gated) return false;
        _gated = false;
        Clear();
        return true;
    }

    /// <summary>
    /// Barge-in ungate with 250ms debounce.
    /// Returns true if barge-in was processed. Outputs speech frames from tail.
    /// Returns false if debounced or already ungated.
    /// </summary>
    public bool TryBargeIn(out byte[][] speechFrames)
    {
        speechFrames = Array.Empty<byte[]>();

        var now = Environment.TickCount64;
        var elapsed = now - Volatile.Read(ref _lastBargeInTick);
        if (elapsed < 250) return false; // debounce
        Volatile.Write(ref _lastBargeInTick, now);

        if (!_gated) return false; // already ungated

        _gated = false;
        speechFrames = FlushTailSpeechFrames();
        return true;
    }

    /// <summary>Buffer a frame while mic is gated.</summary>
    public void Buffer(byte[] frame)
    {
        lock (_lock)
        {
            var copy = new byte[frame.Length];
            System.Buffer.BlockCopy(frame, 0, copy, 0, frame.Length);
            _buffer.Add(copy);
        }
    }

    /// <summary>Clear the buffer (used on normal ungate — echo discard).</summary>
    public void Clear()
    {
        lock (_lock) _buffer.Clear();
    }

    /// <summary>
    /// Extract trailing speech frames using variance-based energy detection.
    /// Scans the tail region for frames with actual speech energy,
    /// filtering out silence/comfort noise to prevent ghost transcripts.
    /// </summary>
    private byte[][] FlushTailSpeechFrames()
    {
        lock (_lock)
        {
            int count = _buffer.Count;
            if (count == 0) return Array.Empty<byte[]>();

            int tailStart = Math.Max(0, count - MicTailMaxFlush);
            var selected = new List<byte[]>();

            for (int i = tailStart; i < count; i++)
            {
                var frame = _buffer[i];
                // Variance-based energy: measures waveform movement
                int variance = 0;
                for (int j = 1; j < frame.Length; j++)
                    variance += Math.Abs(frame[j] - frame[j - 1]);

                if (variance >= SpeechVarianceThreshold)
                    selected.Add(frame);
            }

            _buffer.Clear();
            return selected.ToArray();
        }
    }
}
