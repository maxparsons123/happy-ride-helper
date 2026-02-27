namespace AdaCleanVersion.Realtime;

/// <summary>
/// Simple, deterministic mic gate controller.
/// ONE job: block caller audio to OpenAI while AI is speaking.
/// Energy-based barge-in detection — no dynamic thresholds, no buffers.
/// Telephony audio is predictable. Keep it boring.
/// </summary>
public sealed class MicGateController
{
    /// <summary>
    /// RMS energy threshold for barge-in detection.
    /// Tuned for UK PSTN G.711 lines. Comfort noise sits ~200-800,
    /// speech starts at ~2000+. Set conservatively to avoid echo triggers.
    /// </summary>
    private const double BargeInThreshold = 4000;

    /// <summary>
    /// Double-talk suppression window (ms).
    /// Ignore caller speech within first 150ms of AI speaking
    /// to prevent echo-triggered barge-ins.
    /// </summary>
    private const int DoubleTalkGuardMs = 150;

    /// <summary>
    /// Smooth energy over N frames before triggering barge-in.
    /// Prevents single-frame spikes from false-triggering.
    /// </summary>
    private const int SmoothingFrames = 2;

    private volatile bool _gated;
    private long _gatedAtTick;
    private int _consecutiveHighFrames;

    /// <summary>True = mic is blocked (AI is speaking).</summary>
    public bool IsGated => _gated;

    /// <summary>Gate mic when AI starts responding.</summary>
    public void Arm()
    {
        _gated = true;
        _gatedAtTick = Environment.TickCount64;
        _consecutiveHighFrames = 0;
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
    /// Caller must handle barge-in separately when this returns true while gated.
    /// </summary>
    public bool ShouldSendToOpenAi(byte[] frame, out bool isBargeIn)
    {
        isBargeIn = false;

        if (!_gated)
            return true;

        // Double-talk guard: ignore speech in first 150ms of AI speaking
        var elapsed = Environment.TickCount64 - Volatile.Read(ref _gatedAtTick);
        if (elapsed < DoubleTalkGuardMs)
            return false;

        // Compute RMS energy
        double energy = ComputeRms(frame);

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
    /// Simple RMS energy for G.711 byte frame.
    /// Treats each byte as unsigned 8-bit centered at 128.
    /// </summary>
    private static double ComputeRms(byte[] frame)
    {
        if (frame.Length == 0) return 0;

        double sum = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            int sample = frame[i] - 128;
            sum += sample * sample;
        }
        return sum / frame.Length;
    }
}
