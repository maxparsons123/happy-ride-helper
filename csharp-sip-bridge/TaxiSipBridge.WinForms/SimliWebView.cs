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
    /// Simli expects chunks around 6000 bytes for optimal performance.
    /// </summary>
    public async Task SendAudioAsync(byte[] pcm16Audio)
    {
        if (!IsConnected) 
        {
            OnLog?.Invoke($"🎭 Audio skipped - not connected (IsConnected={IsConnected})");
            return;
        }

        // Log occasionally to show audio is flowing
        _audioBytesSent += pcm16Audio.Length;
        if (_audioBytesSent % 48000 < pcm16Audio.Length) // Log roughly every 1.5 seconds of audio
        {
            OnLog?.Invoke($"🎭 Audio flowing: {_audioBytesSent / 1000}KB sent");
        }

        var base64 = Convert.ToBase64String(pcm16Audio);
        var cmd = JsonSerializer.Serialize(new
        {
            command = "audio",
            data = base64
        });

        await ExecuteScriptAsync($"handleCommand({cmd})");
    }
    private long _audioBytesSent = 0;

    private int _pcm24CallCount = 0;
    
    /// <summary>
    /// Send audio as PCM24 (24kHz) - will be resampled to 16kHz.
    /// </summary>
    public async Task SendPcm24AudioAsync(byte[] pcm24Audio)
    {
        _pcm24CallCount++;
        
        // Log first few calls and then periodically
        if (_pcm24CallCount <= 3 || _pcm24CallCount % 50 == 0)
        {
            OnLog?.Invoke($"🎭 SendPcm24AudioAsync called #{_pcm24CallCount} ({pcm24Audio.Length} bytes, IsConnected={IsConnected})");
        }
        
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
        let ws = null;
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
            document.getElementById('loading').textContent = 'Creating WebRTC connection...';
            
            try {
                // Step 1: Create peer connection with STUN servers
                const config = {
                    sdpSemantics: 'unified-plan',
                    iceServers: [{ urls: ['stun:stun.l.google.com:19302'] }]
                };
                
                pc = new RTCPeerConnection(config);
                log('PeerConnection created');
                
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
                    // Don't mark connected on ICE state - wait for START message from Simli
                    if (pc.iceConnectionState === 'disconnected' || pc.iceConnectionState === 'failed') {
                        chrome.webview.postMessage({ type: 'disconnected' });
                    }
                });

                // Step 2: Create data channel (for receiving Simli messages, not audio)
                const dc = pc.createDataChannel('datachannel', { ordered: true });
                log('DataChannel created');
                
                dc.addEventListener('open', async () => {
                    log('DataChannel open - getting session token...');
                    
                    // Get session token from Simli API
                    const metadata = {
                        faceId: faceId,
                        apiKey: apiKey,
                        isJPG: false,
                        syncAudio: true,
                        handleSilence: true,
                        maxSessionLength: 3600,
                        maxIdleTime: 600
                    };
                    
                    try {
                        const response = await fetch('https://api.simli.ai/startAudioToVideoSession', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(metadata)
                        });
                        
                        if (!response.ok) {
                            const errText = await response.text();
                            throw new Error('Session API failed: ' + response.status + ' - ' + errText);
                        }
                        
                        const resJSON = await response.json();
                        log('Session token received: ' + (resJSON.session_token ? 'yes' : 'no'));
                        
                        // Send session token over WebSocket
                        if (ws && ws.readyState === WebSocket.OPEN) {
                            ws.send(resJSON.session_token);
                            log('Session token sent to WebSocket - waiting for START...');
                            // DON'T mark connected yet - wait for START message
                        }
                    } catch (err) {
                        log('Session token error: ' + err.message);
                        chrome.webview.postMessage({ type: 'error', message: err.message });
                    }
                });
                
                dc.addEventListener('error', (err) => {
                    log('DataChannel error: ' + (err.message || err));
                });
                
                dc.addEventListener('close', () => {
                    log('DataChannel closed');
                });

                // Step 3: Create offer
                const offer = await pc.createOffer({
                    offerToReceiveAudio: true,
                    offerToReceiveVideo: true
                });
                await pc.setLocalDescription(offer);
                log('Local description set');

                // Wait for ICE candidates
                await waitForIceCandidates();
                log('ICE gathering done');

                // Step 4: Connect via WebSocket to Simli
                document.getElementById('loading').textContent = 'Connecting to Simli...';
                
                ws = new WebSocket('wss://api.simli.ai/startWebRTCSession');
                
                ws.addEventListener('open', () => {
                    log('WebSocket connected - sending SDP offer');
                    ws.send(JSON.stringify({
                        sdp: pc.localDescription.sdp,
                        type: pc.localDescription.type
                    }));
                });
                
                ws.addEventListener('message', async (evt) => {
                    const data = evt.data;
                    log('WS message: ' + (typeof data === 'string' ? data.substring(0, 50) : 'binary'));
                    
                    if (data === 'START') {
                        log('Received START - Simli ready for audio!');
                        // NOW we're connected and ready for audio
                        chrome.webview.postMessage({ type: 'connected' });
                        // Send initial silence to start the session
                        const silence = new Uint8Array(16000);
                        ws.send(silence);
                        log('Initial silence sent');
                        return;
                    }
                    
                    if (data === 'STOP') {
                        log('Received STOP');
                        disconnect();
                        return;
                    }
                    
                    // Try to parse as SDP answer
                    try {
                        const message = JSON.parse(data);
                        if (message.type === 'answer') {
                            log('Received SDP answer');
                            await pc.setRemoteDescription(message);
                            log('Remote description set');
                        }
                    } catch (e) {
                        // Not JSON, might be pong or other message
                    }
                });
                
                ws.addEventListener('close', () => {
                    log('WebSocket closed');
                    chrome.webview.postMessage({ type: 'disconnected' });
                });
                
                ws.addEventListener('error', (err) => {
                    log('WebSocket error');
                    chrome.webview.postMessage({ type: 'error', message: 'WebSocket connection failed' });
                });
                
            } catch (err) {
                log('Connection error: ' + err.message);
                document.getElementById('loading').textContent = 'Error: ' + err.message;
                document.getElementById('loading').style.display = 'block';
                chrome.webview.postMessage({ type: 'error', message: err.message });
            }
        }
        
        function waitForIceCandidates() {
            return new Promise((resolve) => {
                let candidateCount = 0;
                let lastCount = -1;
                
                const checkCandidates = () => {
                    if (pc.iceGatheringState === 'complete' || candidateCount === lastCount) {
                        resolve();
                    } else {
                        lastCount = candidateCount;
                        setTimeout(checkCandidates, 250);
                    }
                };
                
                pc.onicecandidate = (event) => {
                    if (event.candidate) {
                        candidateCount++;
                    }
                };
                
                // Also resolve after timeout
                setTimeout(() => resolve(), 3000);
                setTimeout(checkCandidates, 250);
            });
        }

        let audioBytesSent = 0;
        
        function queueAudio(base64Data) {
            if (!ws || ws.readyState !== WebSocket.OPEN) {
                // Only log occasionally to avoid spam
                if (audioBytesSent === 0) {
                    log('WebSocket not ready for audio, state: ' + (ws ? ws.readyState : 'null'));
                }
                return;
            }
            
            try {
                const binary = atob(base64Data);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) {
                    bytes[i] = binary.charCodeAt(i);
                }
                audioBytesSent += bytes.length;
                
                // Log periodically
                if (audioBytesSent % 32000 < bytes.length) {
                    log('Audio queued: ' + Math.round(audioBytesSent / 1000) + 'KB total, queue: ' + audioQueue.length);
                }
                
                audioQueue.push(bytes);
                processAudioQueue();
            } catch (err) {
                log('Audio queue error: ' + err.message);
            }
        }
        
        function processAudioQueue() {
            if (isSending || !ws || ws.readyState !== WebSocket.OPEN) return;
            if (audioQueue.length === 0) return;
            
            isSending = true;
            const chunk = audioQueue.shift();
            
            try {
                ws.send(chunk);
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
            if (ws) {
                ws.close();
                ws = null;
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
