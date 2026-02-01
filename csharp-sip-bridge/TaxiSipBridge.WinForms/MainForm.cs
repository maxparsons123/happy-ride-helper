using System.Windows.Forms;
using TaxiSipBridge.Audio;

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
    private bool _useManualAnswer = false;
    private ManualCallHandler? _manualCallHandler;

    // === Audio Monitor (local speaker playback) ===
    private NAudio.Wave.WaveOutEvent? _monitorWaveOut;
    private NAudio.Wave.BufferedWaveProvider? _monitorWaveProvider;
    private volatile bool _audioMonitorEnabled = false;

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
        txtApiKey.Text = "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";
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

    // Manual Answer mode checkbox handler
    private void chkManualAnswer_CheckedChanged(object? sender, EventArgs e)
    {
        _useManualAnswer = chkManualAnswer.Checked;
        
        // Manual mode disables Local OpenAI and vice versa
        if (_useManualAnswer)
        {
            chkLocalOpenAI.Checked = false;
            chkLocalOpenAI.Enabled = false;
            chkSimliAvatar.Checked = false;
            chkSimliAvatar.Enabled = false;
            AddLog("🎤 Switched to MANUAL ANSWER mode - you will answer calls yourself");
        }
        else
        {
            chkLocalOpenAI.Enabled = true;
            chkSimliAvatar.Enabled = true;
            AddLog("🤖 Switched back to AI mode");
        }
    }

    // Button handlers for manual call control
    private async void btnAnswerCall_Click(object? sender, EventArgs e)
    {
        if (_manualCallHandler != null)
        {
            btnAnswerCall.Visible = false;
            btnRejectCall.Visible = false;
            btnHangUp.Visible = true;
            await _manualCallHandler.AnswerCallAsync();
        }
    }

    private void btnRejectCall_Click(object? sender, EventArgs e)
    {
        if (_manualCallHandler != null)
        {
            _manualCallHandler.RejectCall();
            btnAnswerCall.Visible = false;
            btnRejectCall.Visible = false;
        }
    }

    private void btnHangUp_Click(object? sender, EventArgs e)
    {
        if (_manualCallHandler != null)
        {
            _manualCallHandler.HangUp();
            btnHangUp.Visible = false;
        }
    }

    // Stub handler for cheaper pipeline
    private void chkCheaperPipeline_CheckedChanged(object? sender, EventArgs e) { }

    // Audio monitor checkbox handler
    private void chkMonitorAudio_CheckedChanged(object? sender, EventArgs e)
    {
        _audioMonitorEnabled = chkMonitorAudio.Checked;
        AddLog(_audioMonitorEnabled ? "🔊 Audio monitor enabled (caller → speaker)" : "🔇 Audio monitor disabled");
    }

    /// <summary>
    /// Start local speaker playback for monitoring caller audio.
    /// </summary>
    private void StartAudioMonitor()
    {
        if (!_audioMonitorEnabled) return;

        try
        {
            _monitorWaveProvider = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(48000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true
            };

            _monitorWaveOut = new NAudio.Wave.WaveOutEvent();
            _monitorWaveOut.Init(_monitorWaveProvider);
            _monitorWaveOut.Play();

            AddLog("🔊 Audio monitor started (48kHz speaker output)");
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ Audio monitor failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop local speaker playback.
    /// </summary>
    private void StopAudioMonitor()
    {
        if (_monitorWaveOut != null)
        {
            _monitorWaveOut.Stop();
            _monitorWaveOut.Dispose();
            _monitorWaveOut = null;
        }
        _monitorWaveProvider = null;
    }

    /// <summary>
    /// Feed caller audio to local speaker (called from call handler).
    /// Expects PCM16 at 24kHz, resamples to 48kHz for speaker.
    /// </summary>
    private void PlayCallerAudioLocally(byte[] pcm24kHz)
    {
        if (!_audioMonitorEnabled || _monitorWaveProvider == null) return;

        try
        {
            // Resample 24kHz → 48kHz (2x interpolation)
            var samples24 = AudioCodecs.BytesToShorts(pcm24kHz);
            var samples48 = AudioCodecs.Resample(samples24, 24000, 48000);
            var pcm48 = AudioCodecs.ShortsToBytes(samples48);

            _monitorWaveProvider.AddSamples(pcm48, 0, pcm48.Length);
        }
        catch { }
    }

    // Simli avatar integration
    private SimliWebView? _simliView;

    private void chkSimliAvatar_CheckedChanged(object? sender, EventArgs e)
    {
        bool enabled = chkSimliAvatar.Checked;

        // Show/hide Simli config fields
        lblSimliApiKey.Visible = enabled;
        txtSimliApiKey.Visible = enabled;
        lblSimliFaceId.Visible = enabled;
        txtSimliFaceId.Visible = enabled;
        grpAvatar.Visible = enabled;

        // Move buttons down when Simli config is visible
        btnStartStop.Location = enabled ? new Point(100, 178) : new Point(100, 148);
        btnMicTest.Location = enabled ? new Point(260, 178) : new Point(260, 148);

        if (enabled)
        {
            // Create SimliWebView if not exists
            if (_simliView == null)
            {
                _simliView = new SimliWebView
                {
                    Location = new Point(6, 20),
                    Size = new Size(180, 175)
                };
                _simliView.OnLog += msg => SafeInvoke(() => AddLog(msg));
                grpAvatar.Controls.Clear();
                grpAvatar.Controls.Add(_simliView);
            }

            // Load default values if empty
            if (string.IsNullOrEmpty(txtSimliApiKey.Text))
                txtSimliApiKey.Text = "vlw7tr7vxhhs52bi3rum7";
            if (string.IsNullOrEmpty(txtSimliFaceId.Text))
                txtSimliFaceId.Text = "5fc23ea5-8175-4a82-aaaf-cdd8c88543dc";

            AddLog("🎭 Simli avatar enabled");
        }
        else
        {
            // Clean up SimliWebView
            if (_simliView != null)
            {
                _simliView.DisconnectAsync().ConfigureAwait(false);
                grpAvatar.Controls.Remove(_simliView);
                _simliView.Dispose();
                _simliView = null;
            }
            AddLog("🎭 Simli avatar disabled");
        }
    }

    private async Task ConfigureSimliForCallAsync()
    {
        if (!chkSimliAvatar.Checked || _simliView == null) return;

        var apiKey = txtSimliApiKey.Text.Trim();
        var faceId = txtSimliFaceId.Text.Trim();

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(faceId))
        {
            AddLog("⚠️ Simli API key or Face ID missing");
            return;
        }

        _simliView.Configure(apiKey, faceId);
        await _simliView.ConnectAsync();
    }

    private async Task DisconnectSimliAsync()
    {
        if (_simliView != null)
        {
            await _simliView.DisconnectAsync();
        }
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
            // Create SIP login config (shared by all modes)
            var loginConfig = new SipLoginConfig
            {
                SipServer = txtSipServer.Text.Trim(),
                SipPort = int.Parse(txtSipPort.Text.Trim()),
                SipUser = txtSipUser.Text.Trim(),
                AuthUser = txtAuthUser.Text.Trim(),
                SipPassword = txtSipPassword.Text.Trim(),
                Transport = (SipTransportType)cmbTransport.SelectedIndex
            };

            if (_useManualAnswer)
            {
                // === MANUAL ANSWER MODE ===
                _sipLoginManager = new SipLoginManager(loginConfig);
                _sipLoginManager.OnLog += msg => SafeInvoke(() => AddLog(msg));
                _sipLoginManager.OnRegistered += () => SafeInvoke(() => SetStatus("🎤 MANUAL - Waiting for calls", Color.Green));
                _sipLoginManager.OnRegistrationFailed += err => SafeInvoke(() => SetStatus($"✗ {err}", Color.Red));
                _sipLoginManager.OnCallStarted += (id, caller) => SafeInvoke(() => OnManualCallRinging(id, caller));
                _sipLoginManager.OnCallEnded += id => SafeInvoke(() => OnManualCallEnded(id));
                _sipLoginManager.OnTranscript += t => SafeInvoke(() => AddTranscript(t));

                // Create manual call handler
                _manualCallHandler = new ManualCallHandler();
                _manualCallHandler.OnRinging += caller => SafeInvoke(() => 
                {
                    lblActiveCall.Text = $"📞 RINGING: {caller}";
                    lblActiveCall.ForeColor = Color.Orange;
                    btnAnswerCall.Visible = true;
                    btnRejectCall.Visible = true;
                    // Flash the form or beep
                    System.Media.SystemSounds.Asterisk.Play();
                });
                _manualCallHandler.OnAnswered += () => SafeInvoke(() => 
                {
                    lblActiveCall.Text = $"📞 CONNECTED: {_manualCallHandler.CurrentCaller}";
                    lblActiveCall.ForeColor = Color.Green;
                    StartAudioMonitor();
                });

                // Configure audio monitor if enabled (listen to caller through speakers)
                if (_audioMonitorEnabled)
                {
                    _manualCallHandler.OnCallerAudioMonitor += pcm24 => SafeInvoke(() => PlayCallerAudioLocally(pcm24));
                    AddLog("🔊 Audio monitor connected to call handler");
                }

                _callHandler = _manualCallHandler;
                _sipLoginManager.SetCallHandler(_callHandler);
                _sipLoginManager.Start();
                AddLog("🎤 SIP MANUAL ANSWER mode started - YOU will answer incoming calls");
            }
            else if (_useLocalOpenAI)
            {
                // === LOCAL OPENAI SIP MODE with AiSipAudioPlayout ===
                var apiKey = txtApiKey.Text.Trim();
                if (string.IsNullOrEmpty(apiKey) || (!apiKey.StartsWith("sk-") && !apiKey.StartsWith("sk-proj-")))
                {
                    MessageBox.Show("Please enter a valid OpenAI API key (starts with sk- or sk-proj-)",
                        "API Key Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _sipLoginManager = new SipLoginManager(loginConfig);
                _sipLoginManager.OnLog += msg => SafeInvoke(() => AddLog(msg));
                _sipLoginManager.OnRegistered += () => SafeInvoke(() => SetStatus("🔒 LOCAL AI - Waiting for calls", Color.Green));
                _sipLoginManager.OnRegistrationFailed += err => SafeInvoke(() => SetStatus($"✗ {err}", Color.Red));
                _sipLoginManager.OnCallStarted += (id, caller) => SafeInvoke(() => OnCallStarted(id, caller));
                _sipLoginManager.OnCallEnded += id => SafeInvoke(() => OnCallEnded(id));
                _sipLoginManager.OnTranscript += t => SafeInvoke(() => AddTranscript(t));

                // Create the NEW call handler with dispatch webhook for taxi bookings
                const string dispatchWebhook = "https://coherent-civil-imp.ngrok.app/ada";
                var localHandler = new LocalOpenAICallHandler(apiKey, dispatchWebhookUrl: dispatchWebhook);
                _callHandler = localHandler;

                // Configure Simli avatar if enabled
                if (chkSimliAvatar.Checked && _simliView != null)
                {
                    localHandler.SetSimliSender(async pcm24 => await _simliView.SendPcm24AudioAsync(pcm24));
                    AddLog("🎭 Simli audio sender connected to call handler");
                }

                // Configure audio monitor if enabled (listen to caller through speakers)
                if (_audioMonitorEnabled)
                {
                    localHandler.OnCallerAudioMonitor += pcm24 => SafeInvoke(() => PlayCallerAudioLocally(pcm24));
                    AddLog("🔊 Audio monitor connected to call handler");
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
                    Transport = (SipTransportType)cmbTransport.SelectedIndex,
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

    private void Stop()
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

        // Clear manual handler reference
        _manualCallHandler = null;

        // Hide manual call buttons
        btnAnswerCall.Visible = false;
        btnRejectCall.Visible = false;
        btnHangUp.Visible = false;

        _isRunning = false;
        btnStartStop.Text = "▶ Start SIP";
        btnStartStop.BackColor = Color.FromArgb(40, 167, 69);
        btnMicTest.Enabled = true;
        SetStatus("Stopped", Color.Gray);
        SetConfigEnabled(true);
        AddLog("🛑 SIP stopped");
    }

    // Manual call event handlers
    private void OnManualCallRinging(string callId, string caller)
    {
        lblActiveCall.Text = $"📞 RINGING: {caller}";
        lblActiveCall.ForeColor = Color.Orange;
        lblCallId.Text = $"ID: {callId}";
        AddLog($"📞 RINGING: {caller} - click Answer or Reject");
    }

    private void OnManualCallEnded(string callId)
    {
        lblActiveCall.Text = "Waiting for calls...";
        lblActiveCall.ForeColor = Color.Gray;
        lblCallId.Text = "";
        btnAnswerCall.Visible = false;
        btnRejectCall.Visible = false;
        btnHangUp.Visible = false;
        StopAudioMonitor();
        AddLog($"📴 Call ended: {callId}");
    }

    private async void OnCallStarted(string callId, string caller)
    {
        lblActiveCall.Text = $"📞 {caller}";
        lblActiveCall.ForeColor = Color.Green;
        lblCallId.Text = $"ID: {callId}";
        AddLog($"📞 AUTO-ANSWERED: {caller}");

        // Start audio monitor for this call
        StartAudioMonitor();

        // Connect Simli avatar for this call
        await ConfigureSimliForCallAsync();
    }

    private async void OnCallEnded(string callId)
    {
        lblActiveCall.Text = "Waiting for calls...";
        lblActiveCall.ForeColor = Color.Gray;
        lblCallId.Text = "";

        // Stop audio monitor
        StopAudioMonitor();

        // Disconnect Simli avatar
        await DisconnectSimliAsync();
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
        txtAuthUser.Enabled = enabled;
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
        _simliView?.Dispose();
        base.OnFormClosing(e);
    }
}
