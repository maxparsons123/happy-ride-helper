namespace AdaSipClient.Core;

/// <summary>
/// Abstraction so any component can log without coupling to WinForms.
/// </summary>
public interface ILogSink
{
    void Log(string message);
    void LogWarning(string message);
    void LogError(string message);
}
