using System.Windows.Forms;

namespace TaxiSipBridge;

public partial class MainForm : Form
{
    private SipAutoAnswer? _sipBridge;
    private SipOpenAIBridge? _sipLocalBridge;  // Legacy Local OpenAI mode
    private SipLoginManager? _sipLoginManager;  // NEW: Modular SIP login
    private ISipCallHandler? _callHandler;  // NEW: Call handler with AiSipAudioPlayout
    private AdaAudioClient? _micClient;
    private OpenAIRealtimeClient? _localAiClient;
    private volatile bool _isRunning = false;
    private volatile bool _isMicMode = false;
    private bool _useLocalOpenAI = false;

    public MainForm()
    {
        InitializeComponent();
        LoadSettings();
        
        // Run SpeexDSP diagnostics at startup
        SpeexDspResamplerHelper.LogStartupDiagnostics(msg => AddLog(msg));
    }

    private void LoadSettings()
    {
        txtSipServer.Text = "bellen.dcota.nl";
        txtSipPort.Text = "5060";
        txtSipUser.Text = "1234";
        txtSipPassword.Text = "293183719426";
        txtWebSocketUrl.Text = "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-desktop";
        txtApiKey.Text = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        cmbTransport.SelectedIndex = 0; // UDP
        cmbAudioMode.SelectedIndex = 0; // Standard
        cmbResampler.SelectedIndex = 0; // NAudio (default)
    }

    private void chkLocalOpenAI_CheckedChanged(object? sender, EventArgs e)
    {
        _useLocalOpenAI = chkLocalOpenAI.Checked;

        // Show/hide relevant fields
        lblApiKey.Visible = _useLocalOpenAI;
        txtApiKey.Visible = _useLocalOpenAI;
        lblWs.Visible = !_useLocalOpenAI;
        txtWebSocketUrl.Visible = !_useLocalOpenAI;

        // Update button text
        btnMicTest.Text = _useLocalOpenAI ? "🎤 Test Local AI" : "🎤 Test with Mic";

        AddLog(_useLocalOpenAI
            ? "🔒 Switched to LOCAL OpenAI mode (direct connection)"
            : "☁️ Switched to EDGE FUNCTION mode");
    }

    // Stub handler for Designer controls
    private void chkCheaperPipeline_CheckedChanged(object? sender, EventArgs e) { }

    // Simli Avatar state
    private SimliAvatarClient? _simliClient;
    private SimliAvatarForm? _simliForm;
    private bool _useSimliAvatar = false;

    private void chkSimliAvatar_CheckedChanged(object? sender, EventArgs e) 
    { 
        _useSimliAvatar = chkSimliAvatar.Checked;

        // Show/hide Simli configuration fields
        lblSimliApiKey.Visible = _useSimliAvatar;
        txtSimliApiKey.Visible = _useSimliAvatar;
        lblSimliFaceId.Visible = _useSimliAvatar;
        txtSimliFaceId.Visible = _useSimliAvatar;

        // Adjust button positions when Simli config is visible
        if (_useSimliAvatar)
        {
            btnStartStop.Location = new Point(100, 178);
            btnMicTest.Location = new Point(260, 178);
        }
        else
        {
            btnStartStop.Location = new Point(100, 148);
            btnMicTest.Location = new Point(260, 148);
        }

        AddLog(_useSimliAvatar 
            ? "🎭 Simli Avatar enabled - configure API key and Face ID"
            : "🎭 Simli Avatar disabled");
    }

    private void btnStartStop_Click(object sender, EventArgs e)
    {
        if (_isRunning)
            Stop();
        else
            StartSipMode();
    }

    private void btnMicTest_Click(object sender, EventArgs e)
    {
        if (_isMicMode)
            StopMicMode();
        else
            StartMicMode();
    }

    private void StartSipMode()
    {
        try
        {
            if (_useLocalOpenAI)
            {
                // === NEW: LOCAL OPENAI SIP MODE with AiSipAudioPlayout ===
                var apiKey = txtApiKey.Text.Trim();
                if (string.IsNullOrEmpty(apiKey) || (!apiKey.StartsWith("sk-") && !apiKey.StartsWith("sk-proj-")))
                {
                    MessageBox.Show("Please enter a valid OpenAI API key (starts with sk- or sk-proj-)",
                        "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create SIP login config
                var loginConfig = new SipLoginConfig
                {
                    SipServer = txtSipServer.Text.Trim(),
                    SipPort = int.Parse(txtSipPort.Text.Trim()),
                    SipUser = txtSipUser.Text.Trim(),
                    SipPassword = txtSipPassword.Text.Trim(),
                    Transport = cmbTransport.SelectedIndex == 0 ? SipTransportType.UDP : SipTransportType.TCP
                };

                // Create SIP login manager
                _sipLoginManager = new SipLoginManager(loginConfig);
                _sipLoginManager.OnLog += msg => SafeInvoke(() => AddLog(msg));
                _sipLoginManager.OnRegistered += () => SafeInvoke(() => SetStatus("🔒 LOCAL AI - Waiting for calls", Color.Green));
                _sipLoginManager.OnRegistrationFailed += err => SafeInvoke(() => SetStatus($"✗ {err}", Color.Red));
                _sipLoginManager.OnCallStarted += (id, caller) => SafeInvoke(() => OnCallStarted(id, caller));
                _sipLoginManager.OnCallEnded += id => SafeInvoke(() => OnCallEnded(id));
                _sipLoginManager.OnTranscript += t => SafeInvoke(() => AddTranscript(t));

                // Create the NEW call handler with dispatch webhook for taxi bookings
                const string dispatchWebhook = "https://coherent-civil-imp.ngrok.app/ada";
                _callHandler = new LocalOpenAICallHandler(apiKey, dispatchWebhookUrl: dispatchWebhook);
                
                // Configure Simli avatar if enabled
                if (_useSimliAvatar && _callHandler is LocalOpenAICallHandler localHandler)
                {
                    var simliKey = txtSimliApiKey.Text.Trim();
                    var simliFaceId = txtSimliFaceId.Text.Trim();
                    
                    if (!string.IsNullOrEmpty(simliKey) && !string.IsNullOrEmpty(simliFaceId))
                    {
                        // Create Simli client and form
                        _simliClient = new SimliAvatarClient(simliKey, simliFaceId);
                        _simliClient.OnLog += msg => SafeInvoke(() => AddLog(msg));
                        _simliClient.OnVideoFrame += frame => SafeInvoke(() => _simliForm?.UpdateVideoFrame(frame));
                        
                        _simliForm = new SimliAvatarForm();
                        _simliForm.Show();
                        
                        // Wire up the call handler to send AI audio to Simli
                        localHandler.SetSimliClient(_simliClient, msg => SafeInvoke(() => _simliForm?.SetSpeaking(msg)));
                        
                        AddLog($"🎭 Simli avatar configured (Face: {simliFaceId[..Math.Min(8, simliFaceId.Length)]}...)");
                    }
                    else
                    {
                        AddLog("⚠️ Simli avatar enabled but API key or Face ID missing");
                    }
                }
                
                _sipLoginManager.SetCallHandler(_callHandler);

                _sipLoginManager.Start();
                AddLog("🔒 SIP LOCAL AI mode started - NEW AiSipAudioPlayout (20ms timer-driven RTP)");
            }
            else
            {
                // === EDGE FUNCTION SIP MODE ===
                var config = new SipAdaBridgeConfig
                {
                    SipServer = txtSipServer.Text.Trim(),
                    SipPort = int.Parse(txtSipPort.Text.Trim()),
                    SipUser = txtSipUser.Text.Trim(),
                    SipPassword = txtSipPassword.Text.Trim(),
                    AdaWsUrl = txtWebSocketUrl.Text.Trim(),
                    Transport = cmbTransport.SelectedIndex == 0 ? SipTransportType.UDP : SipTransportType.TCP,
                    AudioMode = (AudioMode)cmbAudioMode.SelectedIndex
                };

                if (!config.IsValid(out var error))
                {
                    MessageBox.Show(error, "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _sipBridge = new SipAutoAnswer(config);
                _sipBridge.OnLog += msg => SafeInvoke(() => AddLog(msg));
                _sipBridge.OnRegistered += () => SafeInvoke(() => SetStatus("☁️ EDGE FUNCTION - Waiting for calls", Color.Green));
                _sipBridge.OnRegistrationFailed += err => SafeInvoke(() => SetStatus($"✗ {err}", Color.Red));
                _sipBridge.OnCallStarted += (id, caller) => SafeInvoke(() => OnCallStarted(id, caller));
                _sipBridge.OnCallEnded += id => SafeInvoke(() => OnCallEnded(id));
                _sipBridge.OnTranscript += t => SafeInvoke(() => AddTranscript(t));

                _sipBridge.Start();
                AddLog("☁️ SIP EDGE FUNCTION mode started - Calls go via Supabase");
            }

            _isRunning = true;
            btnStartStop.Text = "⏹ Stop SIP";
            btnStartStop.BackColor = Color.FromArgb(220, 53, 69);
            btnMicTest.Enabled = false;
            SetStatus("Starting SIP...", Color.Orange);
            SetConfigEnabled(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AddLog($"❌ Error: {ex.Message}");
        }
    }

    private async void StartMicMode()
    {
        try
        {
            if (_useLocalOpenAI)
            {
                // === LOCAL OPENAI MODE ===
                var apiKey = txtApiKey.Text.Trim();
                if (string.IsNullOrEmpty(apiKey) || (!apiKey.StartsWith("sk-") && !apiKey.StartsWith("sk-proj-")))
                {
                    MessageBox.Show("Please enter a valid OpenAI API key (starts with sk- or sk-proj-)",
                        "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _localAiClient = new OpenAIRealtimeClient(
                    apiKey: apiKey,
                    model: "gpt-4o-mini-realtime-preview-2024-12-17",
                    voice: "alloy",
                    systemPrompt: "You are Ada, a friendly UK taxi dispatcher."
                );
                _localAiClient.OnLog += msg => SafeInvoke(() => AddLog(msg));
                _localAiClient.OnTranscript += t => SafeInvoke(() => AddTranscript(t));
                _localAiClient.OnConnected += () => SafeInvoke(() =>
                {
                    SetStatus("🔒 Local OpenAI - Speak to Ada!", Color.Green);
                    lblActiveCall.Text = "🎤 Local AI Active";
                    lblActiveCall.ForeColor = Color.Green;
                });
                _localAiClient.OnDisconnected += () => SafeInvoke(() =>
                {
                    SetStatus("Disconnected", Color.Gray);
                    StopMicMode();
                });
                _localAiClient.OnBookingUpdated += booking => SafeInvoke(() =>
                {
                    AddLog($"📦 Booking: {booking.Pickup} → {booking.Destination}, {booking.Passengers} pax");
                });

                await _localAiClient.ConnectAsync("mic-test");

                // Start microphone capture for local AI
                StartLocalMicCapture();

                AddLog("🔒 LOCAL MODE: Connected directly to OpenAI Realtime API");
            }
            else
            {
                // === EDGE FUNCTION MODE ===
                _micClient = new AdaAudioClient(txtWebSocketUrl.Text.Trim());
                _micClient.OnLog += msg => SafeInvoke(() => AddLog(msg));
                _micClient.OnTranscript += t => SafeInvoke(() => AddTranscript(t));
                _micClient.OnConnected += () => SafeInvoke(() =>
                {
                    SetStatus("🎤 Microphone Test - Speak to Ada!", Color.Green);
                    lblActiveCall.Text = "🎤 Mic Test Active";
                    lblActiveCall.ForeColor = Color.Green;
                });
                _micClient.OnDisconnected += () => SafeInvoke(() =>
                {
                    SetStatus("Disconnected", Color.Gray);
                    StopMicMode();
                });

                await _micClient.ConnectAsync("mic-test");
                _micClient.StartMicrophoneCapture();

                AddLog("☁️ EDGE FUNCTION MODE: Connected via Supabase");
            }

            _isMicMode = true;
            btnMicTest.Text = "⏹ Stop";
            btnMicTest.BackColor = Color.FromArgb(220, 53, 69);
            btnStartStop.Enabled = false;
            SetConfigEnabled(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AddLog($"❌ Error: {ex.Message}");
        }
    }

    private NAudio.Wave.WaveInEvent? _waveIn;
    private System.Timers.Timer? _playbackTimer;

    private void StartLocalMicCapture()
    {
        _waveIn = new NAudio.Wave.WaveInEvent
        {
            WaveFormat = new NAudio.Wave.WaveFormat(24000, 16, 1), // 24kHz PCM16 mono
            BufferMilliseconds = 20
        };

        _waveIn.DataAvailable += async (s, e) =>
        {
            if (_localAiClient?.IsConnected == true && e.BytesRecorded > 0)
            {
                var buffer = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
                await _localAiClient.SendAudioAsync(buffer, 24000);
            }
        };

        _waveIn.StartRecording();

        // Start playback timer for outbound audio
        _playbackTimer = new System.Timers.Timer(20);
        _playbackTimer.Elapsed += PlaybackTimerElapsed;
        _playbackTimer.Start();

        AddLog("🎤 Microphone capture started (24kHz PCM16)");
    }

    private NAudio.Wave.WaveOutEvent? _waveOut;
    private NAudio.Wave.BufferedWaveProvider? _waveProvider;

    private void PlaybackTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_localAiClient == null) return;

        var frame = _localAiClient.GetNextMuLawFrame();
        if (frame == null) return;

        // Decode µ-law to PCM and play
        var pcm8k = AudioCodecs.MuLawDecode(frame);
        var pcm48k = AudioCodecs.Resample(pcm8k, 8000, 48000); // Resample to speaker rate
        var pcmBytes = AudioCodecs.ShortsToBytes(pcm48k);

        if (_waveOut == null)
        {
            _waveProvider = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(48000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true
            };
            _waveOut = new NAudio.Wave.WaveOutEvent();
            _waveOut.Init(_waveProvider);
            _waveOut.Play();
        }

        _waveProvider?.AddSamples(pcmBytes, 0, pcmBytes.Length);
    }

    private void StopMicMode()
    {
        // Stop local AI client
        if (_localAiClient != null)
        {
            _localAiClient.Dispose();
            _localAiClient = null;
        }

        // Stop microphone
        if (_waveIn != null)
        {
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        // Stop playback
        if (_playbackTimer != null)
        {
            _playbackTimer.Stop();
            _playbackTimer.Dispose();
            _playbackTimer = null;
        }

        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
            _waveProvider = null;
        }

        // Stop edge function client
        if (_micClient != null)
        {
            _micClient.StopMicrophoneCapture();
            _micClient.Dispose();
            _micClient = null;
        }

        _isMicMode = false;
        btnMicTest.Text = _useLocalOpenAI ? "🎤 Test Local AI" : "🎤 Test with Mic";
        btnMicTest.BackColor = Color.FromArgb(0, 123, 255);
        btnStartStop.Enabled = true;
        SetStatus("Ready", Color.Gray);
        SetConfigEnabled(true);
        lblActiveCall.Text = "No active call";
        lblActiveCall.ForeColor = Color.Gray;

        AddLog("🎤 Stopped");
    }

    private async void Stop()
    {
        // Stop Edge Function bridge
        if (_sipBridge != null)
        {
            _sipBridge.Stop();
            _sipBridge.Dispose();
            _sipBridge = null;
        }

        // Stop Legacy Local OpenAI bridge
        if (_sipLocalBridge != null)
        {
            _sipLocalBridge.Stop();
            _sipLocalBridge.Dispose();
            _sipLocalBridge = null;
        }

        // Stop NEW modular SIP login + call handler
        if (_sipLoginManager != null)
        {
            _sipLoginManager.Stop();
            _sipLoginManager.Dispose();
            _sipLoginManager = null;
        }
        if (_callHandler != null)
        {
            _callHandler.Dispose();
            _callHandler = null;
        }

        // Stop Simli avatar
        if (_simliClient != null)
        {
            try { await _simliClient.DisconnectAsync(); } catch { }
            _simliClient.Dispose();
            _simliClient = null;
        }
        if (_simliForm != null)
        {
            _simliForm.Close();
            _simliForm.Dispose();
            _simliForm = null;
        }

        _isRunning = false;
        btnStartStop.Text = "▶ Start SIP";
        btnStartStop.BackColor = Color.FromArgb(40, 167, 69);
        btnMicTest.Enabled = true;
        SetStatus("Stopped", Color.Gray);
        SetConfigEnabled(true);

        AddLog("🛑 SIP stopped");
    }

    private void OnCallStarted(string callId, string caller)
    {
        lblActiveCall.Text = $"📞 {caller}";
        lblActiveCall.ForeColor = Color.Green;
        lblCallId.Text = $"ID: {callId}";
        AddLog($"📞 AUTO-ANSWERED: {caller}");
    }

    private void OnCallEnded(string callId)
    {
        lblActiveCall.Text = "Waiting for calls...";
        lblActiveCall.ForeColor = Color.Gray;
        lblCallId.Text = "";
    }

    private void SetStatus(string status, Color color)
    {
        lblStatus.Text = status;
        lblStatus.ForeColor = color;
    }

    private void SetConfigEnabled(bool enabled)
    {
        txtSipServer.Enabled = enabled;
        txtSipPort.Enabled = enabled;
        txtSipUser.Enabled = enabled;
        txtSipPassword.Enabled = enabled;
        txtWebSocketUrl.Enabled = enabled;
        txtApiKey.Enabled = enabled;
        cmbTransport.Enabled = enabled;
        cmbAudioMode.Enabled = enabled;
        cmbResampler.Enabled = enabled;
        chkLocalOpenAI.Enabled = enabled;
    }

    private void AddLog(string message)
    {
        // MEMORY LEAK FIX: Bound log size
        if (lstLogs.Items.Count > 500)
        {
            // Remove oldest 100 items at once for efficiency
            for (int i = 0; i < 100 && lstLogs.Items.Count > 0; i++)
            {
                lstLogs.Items.RemoveAt(0);
            }
        }

        lstLogs.Items.Add(message);
        lstLogs.TopIndex = lstLogs.Items.Count - 1;
    }

    private void AddTranscript(string transcript)
    {
        AddLog($"💬 {transcript}");
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            try { Invoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        else
        {
            action();
        }
    }

    private void btnClearLogs_Click(object sender, EventArgs e)
    {
        lstLogs.Items.Clear();
    }

    private void CopyLogsToClipboard(bool selectedOnly)
    {
        var lines = new System.Text.StringBuilder();
        int count = 0;

        if (selectedOnly)
        {
            foreach (object item in lstLogs.SelectedItems)
            {
                lines.AppendLine(item?.ToString() ?? "");
                count++;
            }
        }
        else
        {
            foreach (object item in lstLogs.Items)
            {
                lines.AppendLine(item?.ToString() ?? "");
                count++;
            }
        }

        if (count == 0)
        {
            MessageBox.Show("No logs to copy.", "Copy Logs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(lines.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy: {ex.Message}", "Copy Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void btnCopyLogs_Click(object sender, EventArgs e)
    {
        CopyLogsToClipboard(selectedOnly: false);
        MessageBox.Show($"Copied {lstLogs.Items.Count} log lines to clipboard!", "Copy Logs", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void mnuCopySelected_Click(object sender, EventArgs e)
    {
        CopyLogsToClipboard(selectedOnly: true);
    }

    private void mnuCopyAll_Click(object sender, EventArgs e)
    {
        CopyLogsToClipboard(selectedOnly: false);
    }

    private void lstLogs_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopyLogsToClipboard(selectedOnly: lstLogs.SelectedItems.Count > 0);
            e.Handled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isRunning) Stop();
        if (_isMicMode) StopMicMode();
        base.OnFormClosing(e);
    }
}
