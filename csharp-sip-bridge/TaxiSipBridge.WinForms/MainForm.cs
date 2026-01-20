namespace TaxiSipBridge;

public partial class MainForm : Form
{
    private SipAutoAnswer? _sipBridge;
    private AdaAudioClient? _micClient;
    private volatile bool _isRunning = false;
    private volatile bool _isMicMode = false;

    public MainForm()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        txtSipServer.Text = "206.189.123.28";
        txtSipPort.Text = "5060";
        txtSipUser.Text = "max201";
        txtSipPassword.Text = "qwe70954504118";
        txtWebSocketUrl.Text = "wss://oerketnvlmptpfvttysy.supabase.co/functions/v1/taxi-realtime-paired";
        cmbTransport.SelectedIndex = 0; // UDP
        cmbAudioMode.SelectedIndex = 0; // Standard
        cmbResampler.SelectedIndex = 0; // NAudio (default)
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
            // Validate config
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
            _sipBridge.OnRegistered += () => SafeInvoke(() => SetStatus("✓ Registered - Waiting for calls", Color.Green));
            _sipBridge.OnRegistrationFailed += err => SafeInvoke(() => SetStatus($"✗ {err}", Color.Red));
            _sipBridge.OnCallStarted += (id, caller) => SafeInvoke(() => OnCallStarted(id, caller));
            _sipBridge.OnCallEnded += id => SafeInvoke(() => OnCallEnded(id));
            _sipBridge.OnTranscript += t => SafeInvoke(() => AddTranscript(t));

            _sipBridge.Start();

            _isRunning = true;
            btnStartStop.Text = "⏹ Stop SIP";
            btnStartStop.BackColor = Color.FromArgb(220, 53, 69);
            btnMicTest.Enabled = false;
            SetStatus("Starting SIP...", Color.Orange);
            SetConfigEnabled(false);

            AddLog("🚕 SIP Auto-Answer mode started");
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

            _isMicMode = true;
            btnMicTest.Text = "⏹ Stop Mic";
            btnMicTest.BackColor = Color.FromArgb(220, 53, 69);
            btnStartStop.Enabled = false;
            SetConfigEnabled(false);

            AddLog("🎤 Microphone test mode - speak to Ada!");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start mic: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AddLog($"❌ Error: {ex.Message}");
        }
    }

    private void StopMicMode()
    {
        if (_micClient != null)
        {
            _micClient.StopMicrophoneCapture();
            _micClient.Dispose();
            _micClient = null;
        }

        _isMicMode = false;
        btnMicTest.Text = "🎤 Test with Mic";
        btnMicTest.BackColor = Color.FromArgb(0, 123, 255);
        btnStartStop.Enabled = true;
        SetStatus("Ready", Color.Gray);
        SetConfigEnabled(true);
        lblActiveCall.Text = "No active call";
        lblActiveCall.ForeColor = Color.Gray;

        AddLog("🎤 Microphone test stopped");
    }

    private void Stop()
    {
        if (_sipBridge != null)
        {
            _sipBridge.Stop();
            _sipBridge.Dispose();
            _sipBridge = null;
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
        cmbTransport.Enabled = enabled;
        cmbAudioMode.Enabled = enabled;
        cmbResampler.Enabled = enabled;
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
