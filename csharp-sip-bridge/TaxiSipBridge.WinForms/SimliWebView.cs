using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TaxiSipBridge;

/// <summary>
/// Simli avatar integration using WebView2 and the official Simli JavaScript SDK.
/// This embeds a web page that handles WebRTC communication with Simli.
/// </summary>
public class SimliWebView : UserControl
{
    private readonly WebView2 _webView;
    private readonly Label _statusLabel;
    private readonly Label _titleLabel;
    private bool _isInitialized;
    private string? _apiKey;
    private string? _faceId;

    /// <summary>Fired for log messages.</summary>
    public event Action<string>? OnLog;

    /// <summary>Whether the avatar is currently connected.</summary>
    public bool IsConnected { get; private set; }

    public SimliWebView()
    {
        // Panel setup
        Size = new System.Drawing.Size(320, 280);
        BackColor = System.Drawing.Color.FromArgb(30, 30, 35);

        // Title
        _titleLabel = new Label
        {
            Text = "🎭 Avatar",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold),
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            BackColor = System.Drawing.Color.FromArgb(45, 45, 50)
        };

        // WebView2 for Simli SDK
        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        // Status label
        _statusLabel = new Label
        {
            Text = "Initializing...",
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = System.Drawing.Color.Gray,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Font = new System.Drawing.Font("Segoe UI", 9),
            BackColor = System.Drawing.Color.FromArgb(40, 40, 45)
        };

        Controls.Add(_webView);
        Controls.Add(_statusLabel);
        Controls.Add(_titleLabel);

        InitializeWebViewAsync();
    }

    private async void InitializeWebViewAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            // Load the Simli HTML page
            _webView.CoreWebView2.NavigateToString(GetSimliHtml());
            
            _isInitialized = true;
            SetStatus("Ready to connect", System.Drawing.Color.Orange);
            OnLog?.Invoke("🎭 SimliWebView initialized");
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 failed", System.Drawing.Color.Red);
            OnLog?.Invoke($"🎭 WebView2 init failed: {ex.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                
                switch (type)
                {
                    case "connected":
                        IsConnected = true;
                        SafeInvoke(() => SetStatus("🟢 Connected", System.Drawing.Color.LightGreen));
                        OnLog?.Invoke("🎭 Simli avatar connected");
                        break;
                        
                    case "disconnected":
                        IsConnected = false;
                        SafeInvoke(() => SetStatus("Disconnected", System.Drawing.Color.Gray));
                        OnLog?.Invoke("🎭 Simli avatar disconnected");
                        break;
                        
                    case "speaking":
                        SafeInvoke(() => SetStatus("🔊 Speaking...", System.Drawing.Color.LightGreen));
                        break;
                        
                    case "silent":
                        SafeInvoke(() => SetStatus("👂 Listening...", System.Drawing.Color.LightBlue));
                        break;
                        
                    case "error":
                        var msg = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
                        SafeInvoke(() => SetStatus($"Error", System.Drawing.Color.Red));
                        OnLog?.Invoke($"🎭 Simli error: {msg}");
                        break;
                        
                    case "log":
                        var logMsg = root.TryGetProperty("message", out var logEl) ? logEl.GetString() : "";
                        OnLog?.Invoke($"🎭 [JS] {logMsg}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Message parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Configure the avatar with API credentials.
    /// </summary>
    public void Configure(string apiKey, string faceId)
    {
        _apiKey = apiKey;
        _faceId = faceId;
        SetStatus("Configured", System.Drawing.Color.Orange);
        OnLog?.Invoke($"🎭 Configured (Face: {faceId[..Math.Min(8, faceId.Length)]}...)");
    }

    /// <summary>
    /// Connect to Simli service.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (!_isInitialized || string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_faceId))
        {
            OnLog?.Invoke("🎭 Cannot connect: not configured or not initialized");
            return;
        }

        SetStatus("Connecting...", System.Drawing.Color.Yellow);

        var cmd = JsonSerializer.Serialize(new
        {
            command = "connect",
            apiKey = _apiKey,
            faceId = _faceId
        });

        await ExecuteScriptAsync($"handleCommand({cmd})");
    }

    /// <summary>
    /// Send PCM16 audio (16kHz) to drive lip-sync.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcm16Audio)
    {
        if (!IsConnected) return;

        var base64 = Convert.ToBase64String(pcm16Audio);
        var cmd = JsonSerializer.Serialize(new
        {
            command = "audio",
            data = base64
        });

        await ExecuteScriptAsync($"handleCommand({cmd})");
    }

    /// <summary>
    /// Send audio as PCM24 (24kHz) - will be resampled to 16kHz.
    /// </summary>
    public async Task SendPcm24AudioAsync(byte[] pcm24Audio)
    {
        // Resample 24kHz to 16kHz (3:2 ratio)
        var samples24 = AudioCodecs.BytesToShorts(pcm24Audio);
        var samples16 = AudioCodecs.Resample(samples24, 24000, 16000);
        var pcm16 = AudioCodecs.ShortsToBytes(samples16);

        await SendAudioAsync(pcm16);
    }

    /// <summary>
    /// Disconnect from Simli service.
    /// </summary>
    public async Task DisconnectAsync()
    {
        var cmd = JsonSerializer.Serialize(new { command = "disconnect" });
        await ExecuteScriptAsync($"handleCommand({cmd})");
        IsConnected = false;
        SetStatus("Disconnected", System.Drawing.Color.Gray);
    }

    /// <summary>
    /// Clear the audio buffer (for interruptions).
    /// </summary>
    public async Task ClearBufferAsync()
    {
        var cmd = JsonSerializer.Serialize(new { command = "clear" });
        await ExecuteScriptAsync($"handleCommand({cmd})");
    }

    private async Task ExecuteScriptAsync(string script)
    {
        if (_webView.CoreWebView2 == null) return;
        
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"🎭 Script error: {ex.Message}");
        }
    }

    private void SetStatus(string text, System.Drawing.Color color)
    {
        if (InvokeRequired)
        {
            try { Invoke(new Action(() => SetStatus(text, color))); } catch { }
            return;
        }
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { Invoke(action); } catch { }
        }
        else
        {
            action();
        }
    }

    private static string GetSimliHtml()
    {
        return """
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { 
            background: #1e1e23; 
            display: flex; 
            justify-content: center; 
            align-items: center;
            height: 100vh;
            overflow: hidden;
        }
        #container {
            position: relative;
            width: 100%;
            height: 100%;
        }
        video {
            width: 100%;
            height: 100%;
            object-fit: cover;
            background: #000;
        }
        audio { display: none; }
        #loading {
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            color: #888;
            font-family: 'Segoe UI', sans-serif;
            font-size: 14px;
        }
    </style>
    <script src="https://cdn.jsdelivr.net/npm/simli-client@latest/dist/bundle.js"></script>
</head>
<body>
    <div id="container">
        <video id="simli-video" autoplay playsinline></video>
        <audio id="simli-audio" autoplay></audio>
        <div id="loading">Waiting for connection...</div>
    </div>
    <script>
        let simliClient = null;

        function log(msg) {
            chrome.webview.postMessage({ type: 'log', message: msg });
        }

        function handleCommand(cmd) {
            log('Command: ' + cmd.command);
            
            switch (cmd.command) {
                case 'connect':
                    connect(cmd.apiKey, cmd.faceId);
                    break;
                case 'audio':
                    sendAudio(cmd.data);
                    break;
                case 'disconnect':
                    disconnect();
                    break;
                case 'clear':
                    if (simliClient) simliClient.ClearBuffer();
                    break;
            }
        }

        function connect(apiKey, faceId) {
            document.getElementById('loading').textContent = 'Connecting...';
            
            try {
                simliClient = new SimliClient();
                
                const config = {
                    apiKey: apiKey,
                    faceID: faceId,
                    handleSilence: true,
                    maxSessionLength: 3600,
                    maxIdleTime: 600,
                    videoRef: document.getElementById('simli-video'),
                    audioRef: document.getElementById('simli-audio'),
                    enableConsoleLogs: true
                };
                
                simliClient.Initialize(config);
                
                simliClient.on('connected', () => {
                    document.getElementById('loading').style.display = 'none';
                    chrome.webview.postMessage({ type: 'connected' });
                });
                
                simliClient.on('disconnected', () => {
                    document.getElementById('loading').style.display = 'block';
                    document.getElementById('loading').textContent = 'Disconnected';
                    chrome.webview.postMessage({ type: 'disconnected' });
                });
                
                simliClient.on('failed', () => {
                    document.getElementById('loading').textContent = 'Connection failed';
                    chrome.webview.postMessage({ type: 'error', message: 'WebRTC connection failed' });
                });
                
                simliClient.on('speaking', () => {
                    chrome.webview.postMessage({ type: 'speaking' });
                });
                
                simliClient.on('silent', () => {
                    chrome.webview.postMessage({ type: 'silent' });
                });
                
                simliClient.start();
                log('Simli client started');
                
            } catch (err) {
                document.getElementById('loading').textContent = 'Error: ' + err.message;
                chrome.webview.postMessage({ type: 'error', message: err.message });
            }
        }

        function sendAudio(base64Data) {
            if (!simliClient) return;
            
            try {
                // Decode base64 to Uint8Array
                const binary = atob(base64Data);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) {
                    bytes[i] = binary.charCodeAt(i);
                }
                simliClient.sendAudioData(bytes);
            } catch (err) {
                log('Audio send error: ' + err.message);
            }
        }

        function disconnect() {
            if (simliClient) {
                simliClient.close();
                simliClient = null;
            }
            document.getElementById('loading').style.display = 'block';
            document.getElementById('loading').textContent = 'Disconnected';
        }
    </script>
</body>
</html>
""";
    }

    /// <summary>
    /// Clean up resources.
    /// </summary>
    public void DisposeResources()
    {
        _webView?.Dispose();
    }
}
