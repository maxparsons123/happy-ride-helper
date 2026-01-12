namespace TaxiSipBridge;

public partial class MainForm : Form
{
    private SipAdaBridge? _bridge;
    private bool _isRunning = false;

    public MainForm()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        // Load from app settings or use defaults
        txtSipServer.Text = "206.189.123.28";
        txtSipPort.Text = "5060";
        txtSipUser.Text = "max201";
        txtSipPassword.Text = "qwe70954504118";
        txtWebSocketUrl.Text = "wss://isnqnuveumxiughjuccs.supabase.co/functions/v1/taxi-realtime";
        cmbTransport.SelectedIndex = 0; // UDP
    }

    private void btnStartStop_Click(object sender, EventArgs e)
    {
        if (_isRunning)
        {
            StopBridge();
        }
        else
        {
            StartBridge();
        }
    }

    private void StartBridge()
    {
        try
        {
            var config = new SipAdaBridgeConfig
            {
                SipServer = txtSipServer.Text.Trim(),
                SipPort = int.Parse(txtSipPort.Text.Trim()),
                SipUser = txtSipUser.Text.Trim(),
                SipPassword = txtSipPassword.Text.Trim(),
                AdaWsUrl = txtWebSocketUrl.Text.Trim(),
                Transport = cmbTransport.SelectedIndex == 0 ? SipTransportType.UDP : SipTransportType.TCP
            };

            _bridge = new SipAdaBridge(config);

            // Subscribe to events with UI thread marshaling
            _bridge.OnLog += msg => SafeInvoke(() => AddLog(msg));
            _bridge.OnRegistered += () => SafeInvoke(() => SetStatus("✓ Registered", Color.Green));
            _bridge.OnRegistrationFailed += err => SafeInvoke(() => SetStatus($"✗ Registration Failed: {err}", Color.Red));
            _bridge.OnCallStarted += (id, caller) => SafeInvoke(() => OnCallStarted(id, caller));
            _bridge.OnCallEnded += id => SafeInvoke(() => OnCallEnded(id));

            _bridge.Start();

            _isRunning = true;
            btnStartStop.Text = "⏹ Stop Bridge";
            btnStartStop.BackColor = Color.FromArgb(220, 53, 69);
            SetStatus("Starting...", Color.Orange);
            SetConfigEnabled(false);

            AddLog("🚀 Bridge started");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start bridge: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AddLog($"❌ Error: {ex.Message}");
        }
    }

    private void StopBridge()
    {
        try
        {
            _bridge?.Stop();
            _bridge?.Dispose();
            _bridge = null;

            _isRunning = false;
            btnStartStop.Text = "▶ Start Bridge";
            btnStartStop.BackColor = Color.FromArgb(40, 167, 69);
            SetStatus("Stopped", Color.Gray);
            SetConfigEnabled(true);

            AddLog("🛑 Bridge stopped");
        }
        catch (Exception ex)
        {
            AddLog($"❌ Stop error: {ex.Message}");
        }
    }

    private void OnCallStarted(string callId, string caller)
    {
        lblActiveCall.Text = $"📞 {caller}";
        lblActiveCall.ForeColor = Color.Green;
        lblCallId.Text = $"ID: {callId}";
        AddLog($"📞 Call started: {caller} ({callId})");
    }

    private void OnCallEnded(string callId)
    {
        lblActiveCall.Text = "No active call";
        lblActiveCall.ForeColor = Color.Gray;
        lblCallId.Text = "";
        AddLog($"📴 Call ended: {callId}");
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
    }

    private void AddLog(string message)
    {
        // Limit log entries
        if (lstLogs.Items.Count > 500)
        {
            lstLogs.Items.RemoveAt(0);
        }

        lstLogs.Items.Add(message);
        lstLogs.TopIndex = lstLogs.Items.Count - 1;
    }

    private void SafeInvoke(Action action)
    {
        if (InvokeRequired)
        {
            try
            {
                Invoke(action);
            }
            catch (ObjectDisposedException)
            {
                // Form was closed
            }
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isRunning)
        {
            StopBridge();
        }
        base.OnFormClosing(e);
    }
}
