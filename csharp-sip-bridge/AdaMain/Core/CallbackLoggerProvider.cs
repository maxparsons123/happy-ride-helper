using Microsoft.Extensions.Logging;

namespace AdaMain.Core;

/// <summary>
/// Routes ILogger output to a callback (e.g. MainForm.Log).
/// </summary>
public sealed class CallbackLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _logAction;
    public CallbackLoggerProvider(Action<string> logAction) => _logAction = logAction;
    public ILogger CreateLogger(string categoryName) => new CallbackLogger(categoryName, _logAction);
    public void Dispose() { }

    private sealed class CallbackLogger : ILogger
    {
        private readonly string _category;
        private readonly Action<string> _log;

        public CallbackLogger(string category, Action<string> log)
        {
            // Shorten "AdaMain.Sip.SipServer" → "SipServer"
            _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            _log = log;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var prefix = logLevel switch
            {
                LogLevel.Warning => "⚠",
                LogLevel.Error or LogLevel.Critical => "❌",
                LogLevel.Debug => "🔍",
                _ => "ℹ"
            };
            _log($"{prefix} [{_category}] {formatter(state, exception)}");
            if (exception != null)
                _log($"   {exception.GetType().Name}: {exception.Message}");
        }
    }
}
