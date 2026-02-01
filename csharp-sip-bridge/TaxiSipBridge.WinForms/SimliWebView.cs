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
            text-align: center;
        }
    </style>
</head>
<body>
    <div id="container">
        <video id="video" autoplay playsinline></video>
        <audio id="audio" autoplay></audio>
        <div id="loading">Waiting for connection...</div>
    </div>
    <script>
        let pc = null;
        let dc = null;
        let audioQueue = [];
        let isSending = false;

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
                    queueAudio(cmd.data);
                    break;
                case 'disconnect':
                    disconnect();
                    break;
                case 'clear':
                    audioQueue = [];
                    break;
            }
        }

        async function connect(apiKey, faceId) {
            document.getElementById('loading').textContent = 'Getting session token...';
            
            try {
                // Step 1: Get session token from Simli API
                log('Requesting session token...');
                const sessionResponse = await fetch('https://api.simli.ai/startAudioToVideoSession', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        apiKey: apiKey,
                        faceId: faceId,
                        handleSilence: true,
                        maxSessionLength: 3600,
                        maxIdleTime: 600
                    })
                });
                
                if (!sessionResponse.ok) {
                    const errText = await sessionResponse.text();
                    throw new Error('Session API failed: ' + sessionResponse.status + ' - ' + errText);
                }
                
                const sessionData = await sessionResponse.json();
                log('Session token received: ' + (sessionData.session_token ? 'yes' : 'no'));
                
                if (!sessionData.session_token) {
                    throw new Error('No session token in response: ' + JSON.stringify(sessionData));
                }
                
                // Step 2: Get ICE servers
                document.getElementById('loading').textContent = 'Getting ICE servers...';
                log('Requesting ICE servers with key: ' + apiKey.substring(0, 8) + '...');
                
                const icePayload = JSON.stringify({ apiKey: apiKey });
                log('ICE request payload: ' + icePayload.substring(0, 50) + '...');
                
                const iceResponse = await fetch('https://api.simli.ai/getIceServers', {
                    method: 'POST',
                    headers: { 
                        'Content-Type': 'application/json',
                        'Accept': 'application/json'
                    },
                    body: icePayload
                });
                
                log('ICE response status: ' + iceResponse.status);
                
                if (!iceResponse.ok) {
                    const errBody = await iceResponse.text();
                    log('ICE error body: ' + errBody);
                    // If ICE fails, use fallback STUN servers
                    log('Using fallback STUN servers...');
                }
                
                let iceData = { iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] };
                if (iceResponse.ok) {
                    iceData = await iceResponse.json();
                    log('ICE servers received: ' + JSON.stringify(iceData).substring(0, 100));
                }
                
                // Step 3: Create WebRTC connection
                document.getElementById('loading').textContent = 'Establishing WebRTC...';
                
                const config = {
                    iceServers: iceData.iceServers || [{ urls: 'stun:stun.l.google.com:19302' }]
                };
                log('Using ICE config: ' + JSON.stringify(config));
                
                pc = new RTCPeerConnection(config);
                
                pc.addEventListener('track', (evt) => {
                    log('Track received: ' + evt.track.kind);
                    if (evt.track.kind === 'video') {
                        document.getElementById('video').srcObject = evt.streams[0];
                        document.getElementById('loading').style.display = 'none';
                    } else if (evt.track.kind === 'audio') {
                        document.getElementById('audio').srcObject = evt.streams[0];
                    }
                });

                pc.addEventListener('iceconnectionstatechange', () => {
                    log('ICE state: ' + pc.iceConnectionState);
                    if (pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') {
                        chrome.webview.postMessage({ type: 'connected' });
                    } else if (pc.iceConnectionState === 'disconnected' || pc.iceConnectionState === 'failed') {
                        chrome.webview.postMessage({ type: 'disconnected' });
                    }
                });
                
                pc.addEventListener('connectionstatechange', () => {
                    log('Connection state: ' + pc.connectionState);
                });

                // Create data channel for sending audio
                dc = pc.createDataChannel('audio', { ordered: true });
                dc.binaryType = 'arraybuffer';
                
                dc.addEventListener('open', () => {
                    log('Data channel open');
                    chrome.webview.postMessage({ type: 'connected' });
                    processAudioQueue();
                });
                
                dc.addEventListener('close', () => {
                    log('Data channel closed');
                });
                
                dc.addEventListener('error', (err) => {
                    log('Data channel error: ' + err.message);
                });

                // Create offer
                const offer = await pc.createOffer({
                    offerToReceiveAudio: true,
                    offerToReceiveVideo: true
                });
                await pc.setLocalDescription(offer);
                log('Local description set');

                // Wait for ICE gathering
                await waitForIceGathering();
                log('ICE gathering complete');

                // Step 4: Send offer to Simli with session token
                document.getElementById('loading').textContent = 'Connecting to avatar...';
                const connectResponse = await fetch('https://api.simli.ai/StartWebRTCSession', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        session_token: sessionData.session_token,
                        sdp: pc.localDescription.sdp,
                        type: pc.localDescription.type
                    })
                });
                
                if (!connectResponse.ok) {
                    const errText = await connectResponse.text();
                    throw new Error('WebRTC session failed: ' + connectResponse.status + ' - ' + errText);
                }
                
                const answerData = await connectResponse.json();
                log('Answer received: ' + (answerData.sdp ? 'yes' : 'no'));
                
                if (answerData.sdp) {
                    await pc.setRemoteDescription({
                        type: 'answer',
                        sdp: answerData.sdp
                    });
                    log('Remote description set');
                } else {
                    throw new Error('No SDP in answer: ' + JSON.stringify(answerData));
                }
                
            } catch (err) {
                log('Connection error: ' + err.message);
                document.getElementById('loading').textContent = 'Error: ' + err.message;
                document.getElementById('loading').style.display = 'block';
                chrome.webview.postMessage({ type: 'error', message: err.message });
            }
        }
        
        function waitForIceGathering() {
            return new Promise((resolve) => {
                if (pc.iceGatheringState === 'complete') {
                    resolve();
                } else {
                    const checkState = () => {
                        if (pc.iceGatheringState === 'complete') {
                            resolve();
                        } else {
                            setTimeout(checkState, 100);
                        }
                    };
                    // Also resolve after timeout to prevent hanging
                    setTimeout(() => resolve(), 5000);
                    setTimeout(checkState, 100);
                }
            });
        }

        function queueAudio(base64Data) {
            if (!dc || dc.readyState !== 'open') {
                log('Data channel not ready, state: ' + (dc ? dc.readyState : 'null'));
                return;
            }
            
            try {
                const binary = atob(base64Data);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) {
                    bytes[i] = binary.charCodeAt(i);
                }
                audioQueue.push(bytes);
                processAudioQueue();
            } catch (err) {
                log('Audio queue error: ' + err.message);
            }
        }
        
        function processAudioQueue() {
            if (isSending || !dc || dc.readyState !== 'open') return;
            if (audioQueue.length === 0) return;
            
            isSending = true;
            const chunk = audioQueue.shift();
            
            try {
                dc.send(chunk);
                chrome.webview.postMessage({ type: 'speaking' });
            } catch (err) {
                log('Send error: ' + err.message);
            }
            
            // Throttle to prevent overwhelming
            setTimeout(() => {
                isSending = false;
                if (audioQueue.length > 0) {
                    processAudioQueue();
                } else {
                    chrome.webview.postMessage({ type: 'silent' });
                }
            }, 20);
        }

        function disconnect() {
            if (dc) {
                dc.close();
                dc = null;
            }
            if (pc) {
                pc.close();
                pc = null;
            }
            audioQueue = [];
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
