using Microsoft.Extensions.Logging;

namespace AdaSdkModel.Core;

/// <summary>
/// Logger provider that forwards all log messages to a callback (e.g. UI log panel).
/// </summary>
public sealed class CallbackLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _callback;

    public CallbackLoggerProvider(Action<string> callback) => _callback = callback;

    public ILogger CreateLogger(string categoryName) => new CallbackLogger(_callback, categoryName);

    public void Dispose() { }

    private sealed class CallbackLogger : ILogger
    {
        private readonly Action<string> _callback;
        private readonly string _category;

        public CallbackLogger(Action<string> callback, string category)
        {
            _callback = callback;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            _callback(msg);
        }
    }
}
