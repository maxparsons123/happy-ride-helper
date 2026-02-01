using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Audio format adapter for SIP ↔ AI audio streaming.
/// 
/// Simplified data flow (PCM16 passthrough):
/// - SIP provides PCM16 @ 8kHz (from RTP)
/// - Pass PCM16 directly to AI (AI clients handle their own conversions)
/// - AI returns PCM16 @ 8kHz (all AI clients normalize to this)
/// - Pass through to SIP (RTP output)
/// </summary>
public sealed class AudioAdapter : IDisposable
{
    // =========================
    // CONSTANTS
    // =========================
    private const int SAMPLE_RATE = 8000;
    private const int FRAME_MS = 20;
    private const int PCM16_FRAME_SIZE = SAMPLE_RATE * FRAME_MS / 1000 * 2; // 320 bytes (160 samples × 2 bytes)
    private const int LOG_INTERVAL_FRAMES = 50; // Log every 1 second (50 × 20ms)

    private static readonly byte[] SilenceFrame = new byte[PCM16_FRAME_SIZE]; // All zeros = PCM16 silence

    // =========================
    // STREAM BUFFERS
    // =========================
    private readonly BlockingCollection<byte[]> _uplinkStream;   // SIP → AI
    private readonly BlockingCollection<byte[]> _downlinkStream; // AI → SIP

    // Accumulation buffer for variable-size AI chunks
    private byte[] _pendingBytes = Array.Empty<byte>();
    private readonly object _pendingLock = new();

    // =========================
    // STATS
    // =========================
    private long _framesReceived;
    private long _framesSent;
    private int _disposed;

    // =========================
    // CONSTRUCTOR
    // =========================

    /// <summary>
    /// Initialize audio adapter.
    /// </summary>
    /// <param name="uplinkCapacity">Uplink buffer capacity in frames (default: 100 = 2 seconds)</param>
    /// <param name="downlinkCapacity">Downlink buffer capacity in frames (default: 200 = 4 seconds)</param>
    public AudioAdapter(int uplinkCapacity = 100, int downlinkCapacity = 200)
    {
        _uplinkStream = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), uplinkCapacity);
        _downlinkStream = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), downlinkCapacity);

        Log("AudioAdapter initialized (PCM16 passthrough mode)");
    }

    // =========================
    // SIP → AI (UPLINK)
    // =========================

    /// <summary>
    /// Handle received PCM16 frame from SIP/RTP (called from RTP receive thread).
    /// </summary>
    /// <param name="pcm16Frame">20ms PCM16 frame at 8kHz (320 bytes)</param>
    public void OnRxPcm16_8k(byte[] pcm16Frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            // Pass through PCM16 directly to uplink stream (non-blocking)
            if (_uplinkStream.TryAdd(pcm16Frame))
            {
                var count = Interlocked.Increment(ref _framesReceived);
                if (count % LOG_INTERVAL_FRAMES == 0)
                    Log($"🎙️ Received {count} frames from SIP");
            }
            // else: buffer full, drop frame silently
        }
        catch (Exception ex)
        {
            Log($"⚠️ Error processing RX frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Get audio from uplink for AI (SIP → AI). Blocking call.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>PCM16 audio frame from SIP (320 bytes @ 8kHz)</returns>
    public byte[] GetUplinkAudio(CancellationToken ct = default)
    {
        try
        {
            return _uplinkStream.Take(ct);
        }
        catch (OperationCanceledException)
        {
            return SilenceFrame;
        }
        catch (InvalidOperationException)
        {
            // Collection completed
            return SilenceFrame;
        }
    }

    /// <summary>
    /// Try to get audio from uplink for AI (non-blocking).
    /// </summary>
    /// <param name="frame">Output frame</param>
    /// <returns>True if frame available</returns>
    public bool TryGetUplinkAudio(out byte[] frame)
    {
        return _uplinkStream.TryTake(out frame!);
    }

    /// <summary>
    /// Get uplink queue count.
    /// </summary>
    public int UplinkCount => _uplinkStream.Count;

    // =========================
    // AI → SIP (DOWNLINK)
    // =========================

    /// <summary>
    /// Get next 20ms PCM16 frame for SIP/RTP output (non-blocking).
    /// Called from RTP playout timer thread.
    /// </summary>
    /// <returns>20ms PCM16 frame at 8kHz (320 bytes), or silence if no data</returns>
    public byte[] GetTxPcm16_8k_NoWait()
    {
        try
        {
            if (_downlinkStream.TryTake(out var frame))
            {
                Interlocked.Increment(ref _framesSent);
                return frame;
            }
            return SilenceFrame;
        }
        catch
        {
            return SilenceFrame;
        }
    }

    /// <summary>
    /// Feed audio from AI to downlink with accumulation buffer.
    /// Accumulates variable-size chunks and splits into fixed 320-byte frames.
    /// Incomplete frames are kept in buffer until next chunk arrives.
    /// </summary>
    /// <param name="audioChunk">Audio chunk from AI (PCM16 @ 8kHz, variable size)</param>
    public void FeedAiAudio(byte[] audioChunk)
    {
        if (Volatile.Read(ref _disposed) != 0 || audioChunk == null || audioChunk.Length == 0) return;

        lock (_pendingLock)
        {
            try
            {
                // Append to pending buffer
                var newPending = new byte[_pendingBytes.Length + audioChunk.Length];
                Buffer.BlockCopy(_pendingBytes, 0, newPending, 0, _pendingBytes.Length);
                Buffer.BlockCopy(audioChunk, 0, newPending, _pendingBytes.Length, audioChunk.Length);
                _pendingBytes = newPending;

                // Split into complete frames
                int offset = 0;
                int framesSent = 0;

                while (offset + PCM16_FRAME_SIZE <= _pendingBytes.Length)
                {
                    var frame = new byte[PCM16_FRAME_SIZE];
                    Buffer.BlockCopy(_pendingBytes, offset, frame, 0, PCM16_FRAME_SIZE);

                    if (_downlinkStream.TryAdd(frame))
                        framesSent++;

                    offset += PCM16_FRAME_SIZE;
                }

                // Keep incomplete part for next call (no padding)
                if (offset < _pendingBytes.Length)
                {
                    var remaining = new byte[_pendingBytes.Length - offset];
                    Buffer.BlockCopy(_pendingBytes, offset, remaining, 0, remaining.Length);
                    _pendingBytes = remaining;
                }
                else
                {
                    _pendingBytes = Array.Empty<byte>();
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Error feeding AI audio: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Feed audio from AI asynchronously.
    /// </summary>
    public Task FeedAiAudioAsync(byte[] audioChunk)
    {
        FeedAiAudio(audioChunk);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get downlink queue count.
    /// </summary>
    public int DownlinkCount => _downlinkStream.Count;

    // =========================
    // STATS
    // =========================

    /// <summary>
    /// Get bridge statistics.
    /// </summary>
    public (long FramesReceived, long FramesSent, string Mode) GetStats()
    {
        return (
            Volatile.Read(ref _framesReceived),
            Volatile.Read(ref _framesSent),
            "pcm16_passthrough"
        );
    }

    // =========================
    // CLEANUP
    // =========================

    /// <summary>
    /// Close the audio adapter.
    /// Flushes any pending bytes by padding the final incomplete frame.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        lock (_pendingLock)
        {
            // Flush pending bytes if any
            if (_pendingBytes.Length > 0)
            {
                if (_pendingBytes.Length < PCM16_FRAME_SIZE)
                {
                    // Pad final incomplete frame with silence
                    var paddedFrame = new byte[PCM16_FRAME_SIZE];
                    Buffer.BlockCopy(_pendingBytes, 0, paddedFrame, 0, _pendingBytes.Length);
                    _downlinkStream.TryAdd(paddedFrame);
                    Log($"Flushed final incomplete frame: {_pendingBytes.Length} → {PCM16_FRAME_SIZE} bytes");
                }
                _pendingBytes = Array.Empty<byte>();
            }
        }

        _uplinkStream.CompleteAdding();
        _downlinkStream.CompleteAdding();

        var stats = GetStats();
        Log($"AudioAdapter closed: rx={stats.FramesReceived}, tx={stats.FramesSent}");
    }

    public void Dispose()
    {
        Close();
        _uplinkStream.Dispose();
        _downlinkStream.Dispose();
    }

    // =========================
    // HELPERS
    // =========================

    private static void Log(string message)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [AudioAdapter] {message}");
    }
}
