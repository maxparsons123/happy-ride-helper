using AdaCleanVersion.Audio;

namespace AdaCleanVersion.Realtime;

/// <summary>
/// Telephony-optimized mic gate controller for G.711.
/// Deterministic. Stable. No dynamic thresholds.
/// Uses average absolute deviation (not RMS) — more stable in companded domain.
/// v3.1: Buffers audio during gate window instead of discarding — prevents first-syllable clipping.
/// </summary>
public sealed class MicGateController
{
    /// <summary>
    /// Average absolute deviation threshold for barge-in detection.
    /// Tuned for PSTN + OpenAI TTS voice over G.711 A-law.
    /// </summary>
    private const int BargeInThreshold = 18;

    /// <summary>
    /// Double-talk suppression window (ms).
    /// Ignore caller speech within first 180ms of AI speaking.
    /// </summary>
    private const int DoubleTalkGuardMs = 180;

    /// <summary>
    /// Smooth energy over N frames (60ms) before triggering barge-in.
    /// </summary>
    private const int SmoothingFrames = 3;

    /// <summary>
    /// Maximum frames to buffer during gate (1 second at 20ms/frame = 50 frames).
    /// Prevents unbounded memory growth if gate stays open too long.
    /// </summary>
    private const int MaxBufferFrames = 50;

    private readonly int _silenceCenter;

    private volatile bool _gated;
    private long _gatedAtTick;
    private int _consecutiveHighFrames;

    // v3.1: Buffer audio during gate instead of discarding
    private readonly List<byte[]> _buffer = new();
    private readonly object _bufferLock = new();

    public MicGateController(G711CodecType codec = G711CodecType.PCMA)
    {
        _silenceCenter = codec == G711CodecType.PCMA ? 0xD5 : 0xFF;
    }

    /// <summary>True = mic is blocked (AI is speaking).</summary>
    public bool IsGated => _gated;

    /// <summary>Gate mic when AI starts responding.</summary>
    public void Arm()
    {
        _gated = true;
        _gatedAtTick = Environment.TickCount64;
        _consecutiveHighFrames = 0;
        lock (_bufferLock) { _buffer.Clear(); }
    }

    /// <summary>Ungate mic (AI finished speaking or barge-in).</summary>
    public void Ungate()
    {
        _gated = false;
        _consecutiveHighFrames = 0;
    }

    /// <summary>
    /// Should this frame be forwarded to OpenAI?
    /// Returns true if mic is open OR if barge-in energy detected.
    /// When returning false, the frame is buffered (not discarded) so 
    /// first syllables aren't lost when the gate opens.
    /// </summary>
    public bool ShouldSendToOpenAi(byte[] frame, out bool isBargeIn)
    {
        isBargeIn = false;

        if (!_gated)
            return true;

        var elapsed = Environment.TickCount64 - Volatile.Read(ref _gatedAtTick);

        // Prevent echo-trigger in first ~180ms — discard these (true echo)
        if (elapsed < DoubleTalkGuardMs)
            return false;

        // Past the double-talk guard: buffer the frame (might be real speech)
        lock (_bufferLock)
        {
            if (_buffer.Count < MaxBufferFrames)
                _buffer.Add(frame);
        }

        double energy = ComputeAverageAbsDeviation(frame, _silenceCenter);

        if (energy > BargeInThreshold)
        {
            _consecutiveHighFrames++;

            if (_consecutiveHighFrames >= SmoothingFrames)
            {
                isBargeIn = true;
                return true;
            }
        }
        else
        {
            _consecutiveHighFrames = 0;
        }

        return false;
    }

    /// <summary>
    /// Flush buffered audio frames and clear the buffer.
    /// Call this when the gate opens to send any captured speech to OpenAI.
    /// Returns null/empty if nothing was buffered.
    /// </summary>
    public byte[][] FlushBuffer()
    {
        lock (_bufferLock)
        {
            if (_buffer.Count == 0) return Array.Empty<byte[]>();
            var result = _buffer.ToArray();
            _buffer.Clear();
            return result;
        }
    }

    /// <summary>
    /// Average absolute deviation for encoded G.711 bytes.
    /// </summary>
    private static double ComputeAverageAbsDeviation(byte[] frame, int silenceCenter)
    {
        if (frame.Length == 0) return 0;

        double sum = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            sum += Math.Abs(frame[i] - silenceCenter);
        }
        return sum / frame.Length;
    }
}
