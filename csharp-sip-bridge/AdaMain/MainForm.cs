using System.Text.Json;
using AdaMain.Ai;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Services;
using AdaMain.Sip;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace AdaMain;

public partial class MainForm : Form
{
    private AppSettings _settings;
    private bool _sipConnected;
    private bool _inCall;
    private bool _muted;
    private bool _operatorMode;
    private bool _pttActive;

    private SipServer? _sipServer;
    private ILoggerFactory? _loggerFactory;
    private ICallSession? _currentSession;

    // Audio monitor (hear raw SIP audio locally)
    private WaveOutEvent? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;

    // Operator microphone (send local mic audio to SIP)
    private WaveInEvent? _micInput;
    private readonly object _micLock = new();

    public MainForm()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ApplySettingsToUi();
        Log("AdaMain v1.0 started. Configure SIP and click Connect.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopMicrophone();
        StopAudioMonitor();
        if (_sipServer != null)
        {
            Log("Shutting down SIP…");
            _sipServer.StopAsync().GetAwaiter().GetResult();
            _sipServer = null;
        }
        _loggerFactory?.Dispose();
        base.OnFormClosing(e);
    }

    // ── Logger factory ──

    private ILoggerFactory GetLoggerFactory()
    {
        return _loggerFactory ??= LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new CallbackLoggerProvider(Log));
        });
    }

    // ── Settings persistence ──

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdaMain", "appsettings.json");

    private static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* fall through */ }
        return new AppSettings();
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { Log($"⚠ Failed to save settings: {ex.Message}"); }
    }

    private void ApplySettingsToUi()
    {
        txtSipServer.Text = _settings.Sip.Server;
        txtSipPort.Text = _settings.Sip.Port.ToString();
        txtSipUser.Text = _settings.Sip.Username;
        txtAuthId.Text = _settings.Sip.AuthId ?? "";
        txtSipPassword.Text = _settings.Sip.Password;
        txtDomain.Text = _settings.Sip.Domain ?? "";
        chkAutoAnswer.Checked = _settings.Sip.AutoAnswer;

        var idx = cmbTransport.Items.IndexOf(_settings.Sip.Transport.ToUpperInvariant());
        cmbTransport.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void ReadSipFromUi()
    {
        _settings.Sip.Server = txtSipServer.Text.Trim();
        _settings.Sip.Port = int.TryParse(txtSipPort.Text, out var p) ? p : 5060;
        _settings.Sip.Username = txtSipUser.Text.Trim();
        _settings.Sip.AuthId = string.IsNullOrWhiteSpace(txtAuthId.Text) ? null : txtAuthId.Text.Trim();
        _settings.Sip.Password = txtSipPassword.Text;
        _settings.Sip.Domain = string.IsNullOrWhiteSpace(txtDomain.Text) ? null : txtDomain.Text.Trim();
        _settings.Sip.Transport = cmbTransport.SelectedItem?.ToString() ?? "UDP";
        _settings.Sip.AutoAnswer = chkAutoAnswer.Checked;
    }

    // ── SIP connection ──

    private async void btnConnect_Click(object? sender, EventArgs e)
    {
        ReadSipFromUi();

        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || string.IsNullOrWhiteSpace(_settings.Sip.Username))
        {
            MessageBox.Show("SIP Server and Extension are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenAi.ApiKey))
        {
            MessageBox.Show("OpenAI API Key is required. Go to File → Settings to configure.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettings();
        Log($"📞 Connecting to {_settings.Sip.Server}:{_settings.Sip.Port} as {_settings.Sip.Username} ({_settings.Sip.Transport})…");

        SetSipConnected(true);  // Disable fields immediately

        try
        {
            var factory = GetLoggerFactory();
            var sipLogger = factory.CreateLogger<SipServer>();
            var smLogger = factory.CreateLogger<SessionManager>();

            // Create session factory with full pipeline wiring
            var sessionManager = new SessionManager(smLogger, CreateCallSession);

            _sipServer = new SipServer(sipLogger, _settings.Sip, _settings.Audio, sessionManager);

            // Wire SipServer events → MainForm
            _sipServer.OnLog += msg => Invoke(() => Log(msg));

            _sipServer.OnServerResolved += ip => Invoke(() =>
            {
                txtSipServer.Text = ip;
                Log($"📡 Server resolved → {ip}");
            });

            _sipServer.OnRegistered += msg => Invoke(() =>
            {
                Log($"✅ SIP Registered: {msg}");
                lblSipStatus.Text = "● Registered";
                lblSipStatus.ForeColor = Color.LimeGreen;
                statusLabel.Text = "SIP Registered";
            });

            _sipServer.OnRegistrationFailed += msg => Invoke(() =>
            {
                Log($"❌ Registration failed: {msg}");
                lblSipStatus.Text = "● Reg Failed";
                lblSipStatus.ForeColor = Color.OrangeRed;
                statusLabel.Text = "Registration Failed";
            });

            _sipServer.OnCallStarted += callerId => Invoke(() =>
            {
                OnIncomingCall(callerId);
                SetInCall(true);
            });

            _sipServer.OnCallEnded += reason => Invoke(() =>
            {
                Log($"📴 Call ended: {reason}");
                SetInCall(false);
                statusCallId.Text = "";
                _currentSession = null;
                StopAudioMonitor();
            });

            // Wire caller audio monitor — hear raw SIP audio through local speakers
            _sipServer.OnCallerAudioMonitor += alawPayload =>
            {
                _monitorBuffer?.AddSamples(alawPayload, 0, alawPayload.Length);
            };

            await _sipServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ SIP start failed: {ex.Message}");
            SetSipConnected(false);
            _sipServer = null;
        }
    }

    /// <summary>Factory method to create fully-wired CallSession.</summary>
    private ICallSession CreateCallSession(string sessionId, string callerId)
    {
        var factory = GetLoggerFactory();
        
        // Create AI client (G.711 passthrough mode)
        var aiClient = new OpenAiG711Client(
            factory.CreateLogger<OpenAiG711Client>(),
            _settings.OpenAi);
        
        // Create fare calculator
        var fareCalculator = new FareCalculator(
            factory.CreateLogger<FareCalculator>(),
            _settings.GoogleMaps,
            _settings.Supabase);
        
        // Create dispatcher
        var dispatcher = new BsqdDispatcher(
            factory.CreateLogger<BsqdDispatcher>(),
            _settings.Dispatch);
        
        // Create session
        var session = new CallSession(
            sessionId,
            callerId,
            factory.CreateLogger<CallSession>(),
            _settings,
            aiClient,
            fareCalculator,
            dispatcher);
        
        // Wire session events → UI
        session.OnTranscript += (role, text) => Invoke(() =>
        {
            Log($"💬 {role}: {text}");
        });
        
        session.OnBookingUpdated += booking => Invoke(() =>
        {
            var info = new List<string>();
            if (!string.IsNullOrEmpty(booking.Name)) info.Add($"Name: {booking.Name}");
            if (!string.IsNullOrEmpty(booking.Pickup)) info.Add($"From: {booking.Pickup}");
            if (!string.IsNullOrEmpty(booking.Destination)) info.Add($"To: {booking.Destination}");
            if (!string.IsNullOrEmpty(booking.Fare)) info.Add($"Fare: {booking.Fare}");
            if (booking.Confirmed) info.Add($"REF: {booking.BookingRef}");
            
            lblCallInfo.Text = info.Count > 0 ? string.Join(" | ", info) : "Call in progress";
        });
        
        _currentSession = session;
        return session;
    }

    private async void btnDisconnect_Click(object? sender, EventArgs e)
    {
        Log("📞 Disconnecting SIP…");
        try
        {
            if (_sipServer != null)
            {
                await _sipServer.StopAsync();
                _sipServer = null;
            }
        }
        catch (Exception ex) { Log($"⚠ Disconnect error: {ex.Message}"); }

        SetSipConnected(false);
        SetInCall(false);
    }

    private void SetSipConnected(bool connected)
    {
        _sipConnected = connected;
        btnConnect.Enabled = !connected;
        btnDisconnect.Enabled = connected;
        SetSipFieldsEnabled(!connected);
        lblSipStatus.Text = connected ? "● Connecting…" : "● Disconnected";
        lblSipStatus.ForeColor = connected ? Color.Yellow : Color.Gray;
        statusLabel.Text = connected ? "Connecting…" : "Ready";
    }

    private void SetSipFieldsEnabled(bool enabled)
    {
        txtSipServer.Enabled = enabled;
        txtSipPort.Enabled = enabled;
        txtSipUser.Enabled = enabled;
        txtAuthId.Enabled = enabled;
        txtSipPassword.Enabled = enabled;
        txtDomain.Enabled = enabled;
        cmbTransport.Enabled = enabled;
    }

    // ── Call controls ──

    private void btnAnswer_Click(object? sender, EventArgs e)
    {
        Log("✅ Answering incoming call…");
        // In auto-answer mode, SipServer handles this automatically
        SetInCall(true);
    }

    private void btnReject_Click(object? sender, EventArgs e)
    {
        Log("❌ Rejecting incoming call.");
        SetInCall(false);
    }

    private async void btnHangUp_Click(object? sender, EventArgs e)
    {
        Log("📴 Hanging up call.");
        if (_sipServer != null)
        {
            try { await _sipServer.HangupAsync(); }
            catch (Exception ex) { Log($"⚠ Hangup error: {ex.Message}"); }
        }
        SetInCall(false);
    }

    private void chkManualMode_CheckedChanged(object? sender, EventArgs e)
    {
        _operatorMode = chkManualMode.Checked;
        btnPtt.Visible = _operatorMode;
        btnPtt.Enabled = _operatorMode && _inCall;

        if (_operatorMode)
        {
            Log("🎤 Operator mode ON – use Push-to-Talk to speak directly to caller");
        }
        else
        {
            Log("🤖 Auto mode – AI will respond to calls");
            StopMicrophone();
        }
    }

    private void btnMute_Click(object? sender, EventArgs e)
    {
        _muted = !_muted;
        btnMute.Text = _muted ? "🔇 Unmute" : "🔊 Mute";
        btnMute.BackColor = _muted ? Color.FromArgb(180, 50, 50) : Color.FromArgb(80, 80, 85);

        if (_muted)
        {
            _monitorOut?.Pause();
            Log("🔇 Audio monitor muted");
        }
        else
        {
            _monitorOut?.Play();
            Log("🔊 Audio monitor unmuted");
        }
    }

    private void btnPtt_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!_inCall || !_operatorMode) return;
        _pttActive = true;
        btnPtt.BackColor = Color.FromArgb(200, 50, 50);
        btnPtt.Text = "🔴 Speaking…";
        StartMicrophone();
        Log("🎙 PTT active – speaking to caller");
    }

    private void btnPtt_MouseUp(object? sender, MouseEventArgs e)
    {
        _pttActive = false;
        btnPtt.BackColor = Color.FromArgb(156, 39, 176);
        btnPtt.Text = "🎙 Push-to-Talk";
        StopMicrophone();
        Log("🎙 PTT released");
    }

    public void SetInCall(bool inCall)
    {
        _inCall = inCall;
        btnAnswer.Enabled = !inCall && _sipConnected;
        btnReject.Enabled = !inCall && _sipConnected;
        btnHangUp.Enabled = inCall;
        btnMute.Enabled = inCall;
        btnPtt.Enabled = inCall && _operatorMode;
        lblCallInfo.Text = inCall ? "Call in progress" : "No active call";
        lblCallInfo.ForeColor = inCall ? Color.LimeGreen : Color.Gray;

        if (!inCall)
        {
            _muted = false;
            btnMute.Text = "🔊 Mute";
            btnMute.BackColor = Color.FromArgb(80, 80, 85);
            StopMicrophone();
        }
    }

    public void OnIncomingCall(string callerId)
    {
        if (InvokeRequired) { Invoke(() => OnIncomingCall(callerId)); return; }

        Log($"📲 Incoming call from {callerId}");
        statusCallId.Text = callerId;
        StartAudioMonitor();

        if (chkAutoAnswer.Checked && !chkManualMode.Checked)
        {
            Log("🤖 Auto-answering…");
            btnAnswer_Click(null, EventArgs.Empty);
        }
        else
        {
            btnAnswer.Enabled = true;
            btnReject.Enabled = true;
        }
    }

    // ── Menu handlers ──

    private void mnuSettings_Click(object? sender, EventArgs e)
    {
        using var dlg = new ConfigForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dlg.Settings;
            SaveSettings();
            Log("⚙ Settings saved.");
        }
    }

    private void mnuAudioTest_Click(object? sender, EventArgs e)
    {
        Log("🎤 Audio test – not yet implemented.");
        MessageBox.Show("Audio test coming soon.", "Audio Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void mnuViewConfig_Click(object? sender, EventArgs e)
    {
        if (File.Exists(SettingsPath))
        {
            try { System.Diagnostics.Process.Start("notepad.exe", SettingsPath); }
            catch { MessageBox.Show($"Config at:\n{SettingsPath}", "Config Path"); }
        }
        else
            MessageBox.Show("No config file yet. Settings will be saved on first Connect or Settings save.", "Config");
    }

    // ── Logging ──

    public void Log(string message)
    {
        if (InvokeRequired) { Invoke(() => Log(message)); return; }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lstLogs.Items.Add(line);
        if (lstLogs.Items.Count > 2000)
            lstLogs.Items.RemoveAt(0);
        lstLogs.TopIndex = lstLogs.Items.Count - 1;
    }

    private void lstLogs_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) CopySelectedLogs();
        if (e.Control && e.KeyCode == Keys.A)
        {
            for (int i = 0; i < lstLogs.Items.Count; i++)
                lstLogs.SetSelected(i, true);
        }
    }

    private void CopySelectedLogs()
    {
        var text = string.Join(Environment.NewLine, lstLogs.SelectedItems.Cast<string>());
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void CopyAllLogs()
    {
        var text = string.Join(Environment.NewLine, lstLogs.Items.Cast<string>());
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    // ── Avatar helpers ──

    public void SetAvatarStatus(string status)
    {
        if (InvokeRequired) { Invoke(() => SetAvatarStatus(status)); return; }
        lblAvatarStatus.Text = status;
    }

    // ── Audio monitor (hear caller audio locally) ──

    private void StartAudioMonitor()
    {
        StopAudioMonitor();
        try
        {
            _monitorBuffer = new BufferedWaveProvider(WaveFormat.CreateALawFormat(8000, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true
            };
            _monitorOut = new WaveOutEvent { DesiredLatency = 100 };
            _monitorOut.Init(_monitorBuffer);
            if (!_muted) _monitorOut.Play();
            Log("🔊 Audio monitor started — hearing raw SIP audio");
        }
        catch (Exception ex)
        {
            Log($"⚠ Audio monitor failed: {ex.Message}");
        }
    }

    private void StopAudioMonitor()
    {
        try
        {
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
        }
        catch { }
        _monitorOut = null;
        _monitorBuffer = null;
    }

    // ── Operator microphone (Push-to-Talk → SIP) ──

    private void StartMicrophone()
    {
        lock (_micLock)
        {
            if (_micInput != null) return;

            try
            {
                _micInput = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(8000, 16, 1), // 8kHz PCM16 mono
                    BufferMilliseconds = 20
                };

                _micInput.DataAvailable += (s, e) =>
                {
                    if (!_pttActive || _currentSession == null) return;

                    // Convert PCM16 → A-law and feed into session as if it were SIP audio
                    var alawData = new byte[e.BytesRecorded / 2];
                    for (int i = 0; i < alawData.Length; i++)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                        alawData[i] = NAudio.Codecs.ALawEncoder.LinearToALawSample(sample);
                    }

                    // Send directly to the SIP RTP stream via SipServer
                    _sipServer?.SendOperatorAudio(alawData);
                };

                _micInput.StartRecording();
            }
            catch (Exception ex)
            {
                Log($"⚠ Microphone failed: {ex.Message}");
            }
        }
    }

    private void StopMicrophone()
    {
        lock (_micLock)
        {
            try
            {
                _micInput?.StopRecording();
                _micInput?.Dispose();
            }
            catch { }
            _micInput = null;
        }
    }
}
