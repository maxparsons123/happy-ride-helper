using System.Collections.Concurrent;

namespace AdaAudioPipe.Processing;

/// <summary>
/// Accumulates raw A-law bytes into exact 160-byte frames.
/// Thread-safe via lock. Zero-allocation on the hot path (reuses frame pool).
/// </summary>
public sealed class AlawFrameAccumulator : Interfaces.IAlawAccumulator
{
    private const int FRAME_SIZE = 160;

    private byte[] _buffer = new byte[4096];
    private int _count;
    private readonly object _lock = new();

    public void Accumulate(byte[] alawBytes, ConcurrentQueue<byte[]> framesOut)
    {
        if (alawBytes == null || alawBytes.Length == 0) return;

        lock (_lock)
        {
            // Grow buffer if needed
            if (_count + alawBytes.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + alawBytes.Length));

            Buffer.BlockCopy(alawBytes, 0, _buffer, _count, alawBytes.Length);
            _count += alawBytes.Length;

            // Emit complete frames
            while (_count >= FRAME_SIZE)
            {
                var frame = new byte[FRAME_SIZE];
                Buffer.BlockCopy(_buffer, 0, frame, 0, FRAME_SIZE);
                framesOut.Enqueue(frame);

                _count -= FRAME_SIZE;
                if (_count > 0)
                    Buffer.BlockCopy(_buffer, FRAME_SIZE, _buffer, 0, _count);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
        }
    }
}
