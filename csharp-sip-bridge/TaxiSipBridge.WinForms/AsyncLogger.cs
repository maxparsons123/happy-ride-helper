using System.Collections.Concurrent;

namespace TaxiSipBridge;

/// <summary>
/// Non-blocking async logger. Audio threads enqueue messages;
/// a background thread flushes them to subscribers.
/// v6.3: Added callback overload for flexible routing.
/// </summary>
public sealed class AsyncLogger : IDisposable
{
    private readonly ConcurrentQueue<(string msg, Action<string>? callback)> _queue = new();
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
    /// Non-blocking log to OnLog event. Safe to call from audio threads.
    /// </summary>
    public void Log(string message)
    {
        _queue.Enqueue((message, null));
        _signal.Set();
    }

    /// <summary>
    /// Non-blocking log with custom callback. Safe to call from audio threads.
    /// </summary>
    public void Log(string message, Action<string>? callback)
    {
        _queue.Enqueue((message, callback));
        _signal.Set();
    }

    private void FlushLoop()
    {
        while (_running)
        {
            _signal.WaitOne(100); // Wake on signal or every 100ms

            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    if (item.callback != null)
                        item.callback(item.msg);
                    else
                        OnLog?.Invoke(item.msg);
                }
                catch { }
            }
        }

        // Final drain
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                if (item.callback != null)
                    item.callback(item.msg);
                else
                    OnLog?.Invoke(item.msg);
            }
            catch { }
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
