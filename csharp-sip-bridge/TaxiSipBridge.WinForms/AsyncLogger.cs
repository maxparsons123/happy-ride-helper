using System.Collections.Concurrent;

namespace TaxiSipBridge;

/// <summary>
/// Non-blocking async logger. Audio threads enqueue messages;
/// a background thread flushes them to subscribers.
/// </summary>
public sealed class AsyncLogger : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly Thread _flushThread;
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _running = true;

    public event Action<string>? OnLog;

    public AsyncLogger()
    {
        _flushThread = new Thread(FlushLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "AsyncLoggerThread"
        };
        _flushThread.Start();
    }

    /// <summary>
    /// Non-blocking log. Safe to call from audio threads.
    /// </summary>
    public void Log(string message)
    {
        _queue.Enqueue(message);
        _signal.Set();
    }

    private void FlushLoop()
    {
        while (_running)
        {
            _signal.WaitOne(100); // Wake on signal or every 100ms

            while (_queue.TryDequeue(out var msg))
            {
                try { OnLog?.Invoke(msg); } catch { }
            }
        }

        // Final drain
        while (_queue.TryDequeue(out var msg))
        {
            try { OnLog?.Invoke(msg); } catch { }
        }
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        try { _flushThread.Join(500); } catch { }
        _signal.Dispose();
    }
}
