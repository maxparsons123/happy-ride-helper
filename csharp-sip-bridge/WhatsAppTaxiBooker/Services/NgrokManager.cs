using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace WhatsAppTaxiBooker.Services;

/// <summary>
/// Manages an ngrok tunnel to expose the local webhook port.
/// Supports reserved domains. Requires ngrok to be installed.
/// </summary>
public sealed class NgrokManager
{
    private readonly string _ngrokPath;
    private readonly string _port;
    private readonly string? _reservedDomain;
    private Process? _process;

    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public string? PublicUrl { get; private set; }

    public NgrokManager(string ngrokPath, string port, string? reservedDomain = null)
    {
        _ngrokPath = ngrokPath;
        _port = port;
        _reservedDomain = reservedDomain;
    }

    private void KillExistingNgrok()
    {
        foreach (var p in Process.GetProcessesByName("ngrok"))
        {
            try { p.Kill(); } catch { }
        }
    }

    public void Start()
    {
        try
        {
            KillExistingNgrok();

            string args = !string.IsNullOrWhiteSpace(_reservedDomain)
                ? $"http {_port} --domain={_reservedDomain} --log=stdout"
                : $"http {_port} --log=stdout";

            var psi = new ProcessStartInfo
            {
                FileName = _ngrokPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = psi };

            _process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log($"🔵 [ngrok] {e.Data}");
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log($"🔴 [ngrok ERROR] {e.Data}");
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Log("🚀 ngrok started successfully");
        }
        catch (Exception ex)
        {
            Log($"❌ Error starting ngrok: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill();
            KillExistingNgrok();
            PublicUrl = null;
            Log("🛑 ngrok stopped.");
        }
        catch { }
    }

    /// <summary>
    /// Poll the local ngrok API until the HTTPS tunnel URL is available.
    /// </summary>
    public async Task<string?> GetPublicUrlAsync(int retryCount = 5)
    {
        for (int i = 0; i < retryCount; i++)
        {
            try
            {
                using var http = new HttpClient();
                string response = await http.GetStringAsync("http://127.0.0.1:4040/api/tunnels");

                if (response.TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(response);
                    var tunnels = doc.RootElement.GetProperty("tunnels");
                    foreach (var t in tunnels.EnumerateArray())
                    {
                        string? url = t.GetProperty("public_url").GetString();
                        if (url != null && url.StartsWith("https://"))
                        {
                            PublicUrl = url;
                            Log($"🌍 ngrok public URL = {url}");
                            return url;
                        }
                    }
                }
                else if (response.TrimStart().StartsWith("<"))
                {
                    var xml = System.Xml.Linq.XDocument.Parse(response);
                    var url = xml.Root?.Element("Tunnels")?.Element("PublicURL")?.Value;
                    if (!string.IsNullOrEmpty(url))
                    {
                        PublicUrl = url;
                        Log($"🌍 ngrok public URL = {url}");
                        return url;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⌛ Waiting for ngrok... ({i + 1}/{retryCount}) {ex.Message}");
            }
            await Task.Delay(1500);
        }

        Log("⚠️ Could not detect ngrok URL.");
        return null;
    }
}
