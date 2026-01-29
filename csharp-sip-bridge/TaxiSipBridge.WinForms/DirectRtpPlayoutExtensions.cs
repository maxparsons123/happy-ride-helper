using System;
using System.Reflection;

namespace TaxiSipBridge;

/// <summary>
/// Compatibility extension: some older DirectRtpPlayout versions did not expose Clear().
/// If DirectRtpPlayout already has an instance Clear(), that will be used instead.
/// </summary>
public static class DirectRtpPlayoutExtensions
{
    /// <summary>
    /// Best-effort buffer flush using reflection against common private fields.
    /// Safe no-op if fields are missing.
    /// </summary>
    public static void Clear(this DirectRtpPlayout playout)
    {
        if (playout == null) return;

        try
        {
            var t = playout.GetType();

            // Clear ConcurrentQueue<short> _sampleBuffer
            var sampleBufferField = t.GetField("_sampleBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
            var sampleBuffer = sampleBufferField?.GetValue(playout);
            if (sampleBuffer != null)
            {
                // TryDequeue(out short) loop via reflection to avoid taking a hard dependency
                var tryDequeue = sampleBuffer.GetType().GetMethod("TryDequeue");
                if (tryDequeue != null)
                {
                    var args = new object?[] { (short)0 };
                    while ((bool)tryDequeue.Invoke(sampleBuffer, args)!) { }
                }
            }

            // Reset common state fields
            t.GetField("_isCurrentlySpeaking", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(playout, false);

            t.GetField("_filterState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(playout, 0f);

            t.GetField("_lastSample", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(playout, (short)0);
        }
        catch
        {
            // intentionally swallow: this is a compatibility helper
        }
    }
}