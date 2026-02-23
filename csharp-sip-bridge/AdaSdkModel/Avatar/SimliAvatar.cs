using System.Text.Json;

using Microsoft.Extensions.Logging;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AdaSdkModel.Avatar;

/// <summary>
/// Simli avatar integration using WebView2 and the Simli JavaScript SDK.
/// Embeds a web page that handles WebRTC communication with Simli's
/// audio-to-video pipeline. Expects PCM16 audio at 16kHz.
/// </summary>
public sealed class SimliAvatar : UserControl
{
    private readonly WebView2 _webView;
    private readonly Label _statusLabel;
    private readonly ILogger<SimliAvatar> _logger;
    private bool _isInitialized;
    private string? _apiKey;
    private string? _faceId;
    private long _audioBytesSent;

    /// <summary>Whether the avatar is connected and accepting audio.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Whether a connection attempt is in progress.</summary>
    public bool IsConnecting { get; private set; }

    // Buffer audio while WebRTC connection is being established
    private readonly List<byte[]> _pendingAudio = new();
    private const int MAX_PENDING_CHUNKS = 100; // ~2s

    public SimliAvatar(ILogger<SimliAvatar> logger)
    {
        _logger = logger;

        Dock = DockStyle.Fill;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 35);

        _webView = new WebView2 { Dock = DockStyle.Fill };

        _statusLabel = new Label
        {
            Text = "Waiting…",
            Dock = DockStyle.Bottom,
            Height = 22,
            ForeColor = System.Drawing.Color.Gray,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Font = new System.Drawing.Font("Segoe UI", 8F),
            BackColor = System.Drawing.Color.FromArgb(40, 40, 45)
        };

        Controls.Add(_webView);
        Controls.Add(_statusLabel);

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.NavigateToString(GetSimliHtml());
            _isInitialized = true;
            SetStatus("Ready", System.Drawing.Color.Orange);
            _logger.LogInformation("🎭 Simli avatar initialized");
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 failed", System.Drawing.Color.Red);
            _logger.LogError("🎭 WebView2 init failed: {Msg}", ex.Message);
        }
    }

    // ── Public API ──

    /// <summary>Store API credentials (call before ConnectAsync).</summary>
    public void Configure(string apiKey, string faceId)
    {
        _apiKey = apiKey;
        _faceId = faceId;
        SetStatus("Configured", System.Drawing.Color.Orange);
        _logger.LogInformation("🎭 Configured (face: {FaceId})", faceId[..Math.Min(8, faceId.Length)]);
    }

    /// <summary>Establish WebRTC session with Simli.</summary>
    public async Task ConnectAsync()
    {
        if (!_isInitialized || string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_faceId))
        {
            _logger.LogWarning("🎭 Cannot connect: not configured or not initialized");
            return;
        }

        IsConnecting = true;
        _pendingAudio.Clear();
        _audioBytesSent = 0;
        SetStatus("Connecting…", System.Drawing.Color.Yellow);

        var cmd = JsonSerializer.Serialize(new { command = "connect", apiKey = _apiKey, faceId = _faceId });
        await ExecAsync($"handleCommand({cmd})");
    }

    /// <summary>Send PCM16 audio at 16kHz to drive lip-sync.</summary>
    public async Task SendAudioAsync(byte[] pcm16Audio)
    {
        if (IsConnecting && !IsConnected)
        {
            if (_pendingAudio.Count < MAX_PENDING_CHUNKS)
                _pendingAudio.Add(pcm16Audio);
            return;
        }

        if (!IsConnected) return;

        _audioBytesSent += pcm16Audio.Length;

        var b64 = Convert.ToBase64String(pcm16Audio);
        var cmd = JsonSerializer.Serialize(new { command = "audio", data = b64 });
        await ExecAsync($"handleCommand({cmd})");
    }

    /// <summary>Clear the audio queue (barge-in).</summary>
    public async Task ClearBufferAsync()
    {
        _pendingAudio.Clear();
        var cmd = JsonSerializer.Serialize(new { command = "clear" });
        await ExecAsync($"handleCommand({cmd})");
    }

    /// <summary>Tear down WebRTC session.</summary>
    public async Task DisconnectAsync()
    {
        var cmd = JsonSerializer.Serialize(new { command = "disconnect" });
        await ExecAsync($"handleCommand({cmd})");
        IsConnected = false;
        IsConnecting = false;
        _pendingAudio.Clear();
        SetStatus("Disconnected", System.Drawing.Color.Gray);
    }

    // ── Internals ──

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "connected":
                    IsConnected = true;
                    IsConnecting = false;
                    SafeInvoke(() => SetStatus("🟢 Connected", System.Drawing.Color.LightGreen));
                    _logger.LogInformation("🎭 Avatar connected");
                    _ = FlushPendingAsync();
                    break;

                case "disconnected":
                    IsConnected = false;
                    IsConnecting = false;
                    _pendingAudio.Clear();
                    SafeInvoke(() => SetStatus("Disconnected", System.Drawing.Color.Gray));
                    _logger.LogInformation("🎭 Avatar disconnected");
                    break;

                case "speaking":
                    SafeInvoke(() => SetStatus("🔊 Speaking…", System.Drawing.Color.LightGreen));
                    break;

                case "silent":
                    SafeInvoke(() => SetStatus("👂 Listening…", System.Drawing.Color.LightBlue));
                    break;

                case "error":
                    var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "?";
                    SafeInvoke(() => SetStatus("Error", System.Drawing.Color.Red));
                    _logger.LogError("🎭 Simli error: {Msg}", msg);
                    break;

                case "log":
                    var logMsg = doc.RootElement.TryGetProperty("message", out var l) ? l.GetString() : "";
                    _logger.LogDebug("🎭 [JS] {Msg}", logMsg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("🎭 Message parse error: {Msg}", ex.Message);
        }
    }

    private async Task FlushPendingAsync()
    {
        if (_pendingAudio.Count == 0) return;
        _logger.LogInformation("🎭 Flushing {Count} buffered chunks", _pendingAudio.Count);
        var buf = _pendingAudio.ToList();
        _pendingAudio.Clear();
        foreach (var chunk in buf)
        {
            await SendAudioAsync(chunk);
            await Task.Delay(10);
        }
    }

    private async Task ExecAsync(string script)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (!IsDisposed && _webView.CoreWebView2 != null)
                            await _webView.CoreWebView2.ExecuteScriptAsync(script);
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("🎭 Script error: {Msg}", ex.Message);
                        tcs.TrySetResult(true);
                    }
                }));
            }
            catch { tcs.TrySetResult(true); }
            await tcs.Task;
            return;
        }

        if (_webView.CoreWebView2 == null) return;
        try { await _webView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { _logger.LogWarning("🎭 Script error: {Msg}", ex.Message); }
    }

    private void SetStatus(string text, System.Drawing.Color color)
    {
        if (InvokeRequired) { try { Invoke(() => SetStatus(text, color)); } catch { } return; }
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { Invoke(action); } catch { } }
        else action();
    }

    // ── HTML ──

    private static string GetSimliHtml() => """
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <style>
        * { margin:0; padding:0; box-sizing:border-box; }
        body { background:#1e1e23; display:flex; justify-content:center; align-items:center; height:100vh; overflow:hidden; }
        #container { position:relative; width:100%; height:100%; }
        video { width:100%; height:100%; object-fit:cover; background:#000; }
        audio { display:none; }
        #loading { position:absolute; top:50%; left:50%; transform:translate(-50%,-50%); color:#888; font-family:'Segoe UI',sans-serif; font-size:14px; text-align:center; }
    </style>
</head>
<body>
    <div id="container">
        <video id="video" autoplay playsinline></video>
        <audio id="audio" autoplay></audio>
        <div id="loading">Waiting for connection...</div>
    </div>
    <script>
        let pc=null, ws=null, audioQueue=[], isSending=false, audioBytesSent=0;
        function log(msg){ chrome.webview.postMessage({type:'log',message:msg}); }
        function handleCommand(cmd){
            switch(cmd.command){
                case 'connect': connect(cmd.apiKey,cmd.faceId); break;
                case 'audio': queueAudio(cmd.data); break;
                case 'disconnect': disconnect(); break;
                case 'clear': audioQueue=[]; break;
            }
        }
        async function connect(apiKey,faceId){
            document.getElementById('loading').textContent='Creating WebRTC...';
            try{
                pc=new RTCPeerConnection({sdpSemantics:'unified-plan',iceServers:[{urls:['stun:stun.l.google.com:19302']}]});
                pc.addEventListener('track',evt=>{
                    if(evt.track.kind==='video'){document.getElementById('video').srcObject=evt.streams[0];document.getElementById('loading').style.display='none';}
                    else if(evt.track.kind==='audio'){document.getElementById('audio').srcObject=evt.streams[0];}
                });
                pc.addEventListener('iceconnectionstatechange',()=>{
                    if(pc.iceConnectionState==='disconnected'||pc.iceConnectionState==='failed') chrome.webview.postMessage({type:'disconnected'});
                });
                const dc=pc.createDataChannel('datachannel',{ordered:true});
                dc.addEventListener('open',async()=>{
                    log('DataChannel open');
                    try{
                        const resp=await fetch('https://api.simli.ai/startAudioToVideoSession',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({faceId,apiKey,isJPG:false,syncAudio:true,handleSilence:true,maxSessionLength:3600,maxIdleTime:600})});
                        if(!resp.ok) throw new Error('API '+resp.status);
                        const json=await resp.json();
                        if(ws&&ws.readyState===WebSocket.OPEN){ws.send(json.session_token);log('Token sent');}
                    }catch(err){log('Token error: '+err.message);chrome.webview.postMessage({type:'error',message:err.message});}
                });
                const offer=await pc.createOffer({offerToReceiveAudio:true,offerToReceiveVideo:true});
                await pc.setLocalDescription(offer);
                await waitForIce();
                ws=new WebSocket('wss://api.simli.ai/startWebRTCSession');
                ws.addEventListener('open',()=>{ws.send(JSON.stringify({sdp:pc.localDescription.sdp,type:pc.localDescription.type}));});
                ws.addEventListener('message',async evt=>{
                    if(evt.data==='START'){chrome.webview.postMessage({type:'connected'});ws.send(new Uint8Array(16000));return;}
                    if(evt.data==='STOP'){disconnect();return;}
                    try{const m=JSON.parse(evt.data);if(m.type==='answer') await pc.setRemoteDescription(m);}catch(e){}
                });
                ws.addEventListener('close',()=>chrome.webview.postMessage({type:'disconnected'}));
                ws.addEventListener('error',()=>chrome.webview.postMessage({type:'error',message:'WebSocket failed'}));
            }catch(err){
                document.getElementById('loading').textContent='Error: '+err.message;
                document.getElementById('loading').style.display='block';
                chrome.webview.postMessage({type:'error',message:err.message});
            }
        }
        function waitForIce(){return new Promise(r=>{let c=0,l=-1;const chk=()=>{if(pc.iceGatheringState==='complete'||c===l)r();else{l=c;setTimeout(chk,250);}};pc.onicecandidate=e=>{if(e.candidate)c++;};setTimeout(()=>r(),3000);setTimeout(chk,250);});}
        function queueAudio(b64){
            if(!ws||ws.readyState!==WebSocket.OPEN) return;
            try{
                const bin=atob(b64);const bytes=new Uint8Array(bin.length);
                for(let i=0;i<bin.length;i++) bytes[i]=bin.charCodeAt(i);
                audioBytesSent+=bytes.length;
                // Send immediately — Simli server handles its own jitter buffer
                ws.send(bytes);
                if(audioQueue.length===0) chrome.webview.postMessage({type:'speaking'});
            }catch(e){log('send error: '+e.message);}
        }
        // Clear stale state on barge-in
        function clearQueue(){ audioQueue=[]; }
        function disconnect(){if(ws){ws.close();ws=null;}if(pc){pc.close();pc=null;}audioQueue=[];document.getElementById('loading').style.display='block';document.getElementById('loading').textContent='Disconnected';}
    </script>
</body>
</html>
""";
}
