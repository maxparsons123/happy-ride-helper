using System.Text.Json;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Sip;
using Microsoft.Extensions.Logging;

namespace AdaMain;

public partial class MainForm : Form
{
    private AppSettings _settings;
    private bool _sipConnected;
    private bool _inCall;

    private SipServer? _sipServer;
    private ILoggerFactory? _loggerFactory;

    public MainForm()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ApplySettingsToUi();
        Log("AdaMain started. Configure SIP and click Connect.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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

        SaveSettings();
        Log($"📞 Connecting to {_settings.Sip.Server}:{_settings.Sip.Port} as {_settings.Sip.Username} ({_settings.Sip.Transport})…");

        SetSipConnected(true);  // Disable fields immediately

        try
        {
            var factory = GetLoggerFactory();
            var sipLogger = factory.CreateLogger<SipServer>();
            var smLogger = factory.CreateLogger<SessionManager>();

            // Create a placeholder session factory (sessions not yet fully wired)
            var sessionManager = new SessionManager(smLogger, (sid, cid) =>
            {
                Log($"⚠ Session factory not yet wired for {cid}");
                throw new NotImplementedException("CallSession factory needs AI client wiring");
            });

            _sipServer = new SipServer(sipLogger, _settings.Sip, sessionManager);

            // Wire SipServer events → MainForm
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
            });

            await _sipServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ SIP start failed: {ex.Message}");
            SetSipConnected(false);
            _sipServer = null;
        }
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
        // TODO: signal SipServer to accept pending INVITE
        SetInCall(true);
    }

    private void btnReject_Click(object? sender, EventArgs e)
    {
        Log("❌ Rejecting incoming call.");
        // TODO: signal SipServer to reject pending INVITE
        SetInCall(false);
    }

    private async void btnHangUp_Click(object? sender, EventArgs e)
    {
        Log("📴 Hanging up call.");
        // SipServer will fire OnCallEnded → SetInCall(false)
        if (_sipServer != null)
        {
            try { await _sipServer.HangupAsync(); }
            catch (Exception ex) { Log($"⚠ Hangup error: {ex.Message}"); }
        }
        SetInCall(false);
    }

    private void chkManualMode_CheckedChanged(object? sender, EventArgs e)
    {
        var manual = chkManualMode.Checked;
        Log(manual ? "🎤 Manual mode ON – you will speak directly" : "🤖 Auto-answer mode – AI will respond");
    }

    public void SetInCall(bool inCall)
    {
        _inCall = inCall;
        btnAnswer.Enabled = !inCall && _sipConnected;
        btnReject.Enabled = !inCall && _sipConnected;
        btnHangUp.Enabled = inCall;
        lblCallInfo.Text = inCall ? "Call in progress" : "No active call";
        lblCallInfo.ForeColor = inCall ? Color.LimeGreen : Color.Gray;
    }

    public void OnIncomingCall(string callerId)
    {
        if (InvokeRequired) { Invoke(() => OnIncomingCall(callerId)); return; }

        Log($"📲 Incoming call from {callerId}");
        statusCallId.Text = callerId;

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
}
