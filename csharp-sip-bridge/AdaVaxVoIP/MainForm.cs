using System.Drawing;
using System.Windows.Forms;
using AdaVaxVoIP.Config;
using AdaVaxVoIP.Core;
using AdaVaxVoIP.Services;
using AdaVaxVoIP.Sip;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP;

public class MainForm : Form
{
    // Settings fields
    private readonly TextBox _txtOpenAiKey;
    private readonly TextBox _txtSipPort;
    private readonly TextBox _txtVaxLicense;
    private readonly TextBox _txtRecordingsPath;
    private readonly TextBox _txtSupabaseUrl;
    private readonly TextBox _txtVoice;
    private readonly TextBox _txtSipServer;
    private readonly TextBox _txtSipUser;
    private readonly TextBox _txtSipPassword;

    // Status
    private readonly Label _lblStatus;
    private readonly Label _lblActiveCalls;
    private readonly RichTextBox _txtLog;

    // Buttons
    private readonly Button _btnStart;
    private readonly Button _btnStop;

    // Runtime
    private VaxVoIPSipServer? _sipServer;
    private TaxiBookingOrchestrator? _orchestrator;
    private ILoggerFactory? _loggerFactory;
    private CancellationTokenSource? _cts;
    private bool _running;

    public MainForm()
    {
        Text = "Ada Taxi — VaxVoIP Booking System v3.0";
        Size = new Size(820, 680);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 500);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        // === Settings Panel ===
        var grpSettings = new GroupBox
        {
            Text = "⚙️ Settings",
            ForeColor = Color.White,
            Dock = DockStyle.Top,
            Height = 260,
            Padding = new Padding(10)
        };

        var settingsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            AutoSize = false
        };
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settingsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _txtOpenAiKey = AddSettingRow(settingsTable, "OpenAI Key:", 0, 0, true);
        _txtSipPort = AddSettingRow(settingsTable, "SIP Port:", 0, 2);
        _txtSipPort.Text = "5060";

        _txtVaxLicense = AddSettingRow(settingsTable, "Vax License:", 1, 0);
        _txtVaxLicense.Text = "TRIAL";
        _txtVoice = AddSettingRow(settingsTable, "Voice:", 1, 2);
        _txtVoice.Text = "alloy";

        _txtRecordingsPath = AddSettingRow(settingsTable, "Recordings:", 2, 0);
        _txtRecordingsPath.Text = @"C:\TaxiRecordings\";
        _txtSupabaseUrl = AddSettingRow(settingsTable, "Supabase URL:", 2, 2);
        _txtSupabaseUrl.Text = "https://oerketnvlmptpfvttysy.supabase.co";

        _txtSipServer = AddSettingRow(settingsTable, "SIP Server:", 3, 0);
        _txtSipUser = AddSettingRow(settingsTable, "SIP User:", 3, 2);
        _txtSipPassword = AddSettingRow(settingsTable, "SIP Password:", 4, 0, true);

        grpSettings.Controls.Add(settingsTable);

        // === Control Panel ===
        var pnlControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(10, 5, 10, 5),
            FlowDirection = FlowDirection.LeftToRight
        };

        _btnStart = new Button
        {
            Text = "▶ Start Server",
            BackColor = Color.FromArgb(0, 120, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(130, 35),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        _btnStart.Click += BtnStart_Click;

        _btnStop = new Button
        {
            Text = "⏹ Stop",
            BackColor = Color.FromArgb(180, 40, 40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(100, 35),
            Enabled = false,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };
        _btnStop.Click += BtnStop_Click;

        _lblStatus = new Label
        {
            Text = "● Stopped",
            ForeColor = Color.Gray,
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Padding = new Padding(15, 8, 0, 0)
        };

        _lblActiveCalls = new Label
        {
            Text = "Calls: 0",
            ForeColor = Color.LightBlue,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F),
            Padding = new Padding(15, 10, 0, 0)
        };

        pnlControls.Controls.AddRange(new Control[] { _btnStart, _btnStop, _lblStatus, _lblActiveCalls });

        // === Log Panel ===
        var grpLog = new GroupBox
        {
            Text = "📋 Log",
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };

        _txtLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.LightGreen,
            Font = new Font("Cascadia Mono", 9F),
            BorderStyle = BorderStyle.None
        };
        grpLog.Controls.Add(_txtLog);

        // Add controls in reverse dock order
        Controls.Add(grpLog);
        Controls.Add(pnlControls);
        Controls.Add(grpSettings);

        // Load env vars
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(envKey)) _txtOpenAiKey.Text = envKey;
    }

    private TextBox AddSettingRow(TableLayoutPanel table, string label, int row, int col, bool password = false)
    {
        var lbl = new Label
        {
            Text = label,
            ForeColor = Color.LightGray,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 5, 0, 0)
        };
        var txt = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        if (password) txt.UseSystemPasswordChar = true;

        table.Controls.Add(lbl, col, row);
        table.Controls.Add(txt, col + 1, row);
        return txt;
    }

    private void Log(string message, Color? color = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message, color));
            return;
        }

        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.SelectionColor = color ?? Color.LightGreen;
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        _txtLog.ScrollToCaret();

        // Keep log manageable
        if (_txtLog.Lines.Length > 500)
        {
            _txtLog.SelectionStart = 0;
            _txtLog.SelectionLength = _txtLog.GetFirstCharIndexFromLine(100);
            _txtLog.SelectedText = "";
        }
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        if (_running) return;

        if (string.IsNullOrWhiteSpace(_txtOpenAiKey.Text))
        {
            MessageBox.Show("OpenAI API Key is required.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _lblStatus.Text = "● Starting...";
        _lblStatus.ForeColor = Color.Orange;

        try
        {
            _loggerFactory = LoggerFactory.Create(b =>
            {
                b.AddConsole();
                b.SetMinimumLevel(LogLevel.Information);
            });

            var vaxSettings = new VaxVoIPSettings
            {
                LicenseKey = _txtVaxLicense.Text,
                DomainRealm = "taxi.local",
                SipPort = int.TryParse(_txtSipPort.Text, out var sp) ? sp : 5060,
                EnableRecording = true,
                RecordingsPath = _txtRecordingsPath.Text
            };

            var openAISettings = new OpenAISettings
            {
                ApiKey = _txtOpenAiKey.Text,
                Voice = _txtVoice.Text
            };

            var taxiSettings = new TaxiBookingSettings { CompanyName = "Ada Taxi", AutoAnswer = true };
            var supabaseSettings = new SupabaseSettings { Url = _txtSupabaseUrl.Text };
            var googleSettings = new GoogleMapsSettings();
            var dispatchSettings = new DispatchSettings();

            var fareCalculator = new FareCalculator(
                _loggerFactory.CreateLogger<FareCalculator>(), googleSettings, supabaseSettings);
            var dispatcher = new Dispatcher(
                _loggerFactory.CreateLogger<Dispatcher>(), dispatchSettings);

            _sipServer = new VaxVoIPSipServer(
                _loggerFactory.CreateLogger<VaxVoIPSipServer>(), vaxSettings, taxiSettings);

            _sipServer.OnCallStarted += (callId, callerId) =>
            {
                Log($"📞 Call started: {callerId} [{callId}]", Color.Cyan);
                BeginInvoke(() => _lblActiveCalls.Text = $"Calls: active");
            };
            _sipServer.OnCallEnded += callId =>
            {
                Log($"📴 Call ended: [{callId}]", Color.Orange);
                BeginInvoke(() => _lblActiveCalls.Text = $"Calls: 0");
            };

            _orchestrator = new TaxiBookingOrchestrator(
                _loggerFactory.CreateLogger<TaxiBookingOrchestrator>(),
                _loggerFactory,
                _sipServer,
                openAISettings,
                fareCalculator,
                dispatcher);

            _cts = new CancellationTokenSource();
            await _sipServer.StartAsync(_cts.Token);

            _running = true;
            _lblStatus.Text = "● Running";
            _lblStatus.ForeColor = Color.LimeGreen;
            Log("🚀 Server started successfully!", Color.LimeGreen);
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to start: {ex.Message}", Color.Red);
            _lblStatus.Text = "● Error";
            _lblStatus.ForeColor = Color.Red;
            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
        }
    }

    private async void BtnStop_Click(object? sender, EventArgs e)
    {
        if (!_running) return;

        _lblStatus.Text = "● Stopping...";
        _lblStatus.ForeColor = Color.Orange;

        try
        {
            _cts?.Cancel();
            if (_orchestrator != null) await _orchestrator.DisposeAsync();
            if (_sipServer != null) await _sipServer.DisposeAsync();
            _loggerFactory?.Dispose();
        }
        catch (Exception ex)
        {
            Log($"⚠ Error stopping: {ex.Message}", Color.Yellow);
        }

        _running = false;
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "● Stopped";
        _lblStatus.ForeColor = Color.Gray;
        Log("Server stopped.", Color.Gray);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running) BtnStop_Click(null, EventArgs.Empty);
        base.OnFormClosing(e);
    }
}
