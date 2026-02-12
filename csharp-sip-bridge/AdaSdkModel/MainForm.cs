using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using AdaSdkModel.Ai;
using AdaSdkModel.Config;
using AdaSdkModel.Core;
using AdaSdkModel.Services;
using AdaSdkModel.Sip;
using Microsoft.Extensions.Logging;

namespace AdaSdkModel;

public sealed class MainForm : Form
{
    private AppSettings _settings;
    private bool _sipConnected;

    // SIP fields
    private readonly TextBox _txtServer;
    private readonly TextBox _txtPort;
    private readonly TextBox _txtUser;
    private readonly TextBox _txtPassword;
    private readonly TextBox _txtAuthId;
    private readonly ComboBox _cmbTransport;

    // OpenAI fields
    private readonly TextBox _txtApiKey;
    private readonly TextBox _txtModel;
    private readonly ComboBox _cmbVoice;

    // Status
    private readonly Label _lblSipStatus;
    private readonly Label _lblActiveCalls;
    private readonly Label _lblCallInfo;
    private readonly RichTextBox _txtLog;

    // Buttons
    private readonly Button _btnConnect;
    private readonly Button _btnDisconnect;
    private readonly Button _btnSettings;

    // Runtime
    private SipServer? _sipServer;
    private ILoggerFactory? _loggerFactory;

    public MainForm()
    {
        Text = "Ada Taxi — SDK Model v2.0";
        Size = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(750, 550);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        _settings = LoadSettings();

        // ═══════════════════════════════════════
        // SIP CONNECTION PANEL (top)
        // ═══════════════════════════════════════
        var pnlSip = new GroupBox
        {
            Text = "📡 SIP Registration",
            ForeColor = Color.White,
            Dock = DockStyle.Top,
            Height = 130,
            Padding = new Padding(10, 5, 10, 5)
        };

        var tblSip = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 2,
            AutoSize = false
        };
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblSip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        // Row 0: Server, Port, Extension, Password
        tblSip.Controls.Add(Lbl("Server:"), 0, 0);
        _txtServer = Txt(_settings.Sip.Server); tblSip.Controls.Add(_txtServer, 1, 0);
        tblSip.Controls.Add(Lbl("Port:"), 2, 0);
        _txtPort = Txt(_settings.Sip.Port.ToString()); tblSip.Controls.Add(_txtPort, 3, 0);
        tblSip.Controls.Add(Lbl("Extension:"), 4, 0);
        _txtUser = Txt(_settings.Sip.Username); tblSip.Controls.Add(_txtUser, 5, 0);
        tblSip.Controls.Add(Lbl("Password:"), 6, 0);
        _txtPassword = Txt(_settings.Sip.Password, true); tblSip.Controls.Add(_txtPassword, 7, 0);

        // Row 1: AuthId, Transport, Connect, Disconnect
        tblSip.Controls.Add(Lbl("Auth ID:"), 0, 1);
        _txtAuthId = Txt(_settings.Sip.AuthId ?? ""); tblSip.Controls.Add(_txtAuthId, 1, 1);
        tblSip.Controls.Add(Lbl("Transport:"), 2, 1);
        _cmbTransport = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _cmbTransport.Items.AddRange(new object[] { "UDP", "TCP" });
        _cmbTransport.SelectedItem = _settings.Sip.Transport.ToUpperInvariant();
        tblSip.Controls.Add(_cmbTransport, 3, 1);

        _btnConnect = Btn("▶ Connect", Color.FromArgb(0, 120, 60));
        _btnConnect.Click += BtnConnect_Click;
        tblSip.Controls.Add(_btnConnect, 5, 1);

        _btnDisconnect = Btn("⏹ Disconnect", Color.FromArgb(180, 40, 40));
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += BtnDisconnect_Click;
        tblSip.Controls.Add(_btnDisconnect, 7, 1);

        pnlSip.Controls.Add(tblSip);

        // ═══════════════════════════════════════
        // OPENAI SETTINGS PANEL
        // ═══════════════════════════════════════
        var pnlAi = new GroupBox
        {
            Text = "🤖 OpenAI Realtime",
            ForeColor = Color.White,
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(10, 3, 10, 3)
        };

        var tblAi = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1
        };
        tblAi.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblAi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        tblAi.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblAi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        tblAi.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tblAi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        tblAi.Controls.Add(Lbl("API Key:"), 0, 0);
        _txtApiKey = Txt(_settings.OpenAi.ApiKey, true); tblAi.Controls.Add(_txtApiKey, 1, 0);
        tblAi.Controls.Add(Lbl("Model:"), 2, 0);
        _txtModel = Txt(_settings.OpenAi.Model); tblAi.Controls.Add(_txtModel, 3, 0);
        tblAi.Controls.Add(Lbl("Voice:"), 4, 0);
        _cmbVoice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _cmbVoice.Items.AddRange(new object[] { "shimmer", "alloy", "echo", "fable", "onyx", "nova", "ash", "ballad", "coral", "sage", "verse" });
        _cmbVoice.SelectedItem = _settings.OpenAi.Voice;
        if (_cmbVoice.SelectedIndex < 0) _cmbVoice.SelectedIndex = 0;
        tblAi.Controls.Add(_cmbVoice, 5, 0);

        pnlAi.Controls.Add(tblAi);

        // ═══════════════════════════════════════
        // STATUS BAR
        // ═══════════════════════════════════════
        var pnlStatus = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 35,
            Padding = new Padding(10, 5, 10, 0),
            FlowDirection = FlowDirection.LeftToRight
        };

        _lblSipStatus = new Label
        {
            Text = "● Disconnected",
            ForeColor = Color.Gray,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Padding = new Padding(0, 3, 20, 0)
        };

        _lblActiveCalls = new Label
        {
            Text = "Calls: 0",
            ForeColor = Color.LightBlue,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F),
            Padding = new Padding(0, 3, 20, 0)
        };

        _lblCallInfo = new Label
        {
            Text = "No active call",
            ForeColor = Color.Gray,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            Padding = new Padding(0, 5, 0, 0)
        };

        pnlStatus.Controls.AddRange(new Control[] { _lblSipStatus, _lblActiveCalls, _lblCallInfo });

        // ═══════════════════════════════════════
        // LOG PANEL (fills remaining space)
        // ═══════════════════════════════════════
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

        // ═══════════════════════════════════════
        // ASSEMBLE (order matters: last added = top)
        // ═══════════════════════════════════════
        Controls.Add(grpLog);
        Controls.Add(pnlStatus);
        Controls.Add(pnlAi);
        Controls.Add(pnlSip);

        Log($"AdaSdkModel v{OpenAiSdkClient.VERSION} ready. Configure SIP and click Connect.");
    }

    // ═══════════════════════════════════════
    // SIP CONNECT / DISCONNECT
    // ═══════════════════════════════════════
    private async void BtnConnect_Click(object? sender, EventArgs e)
    {
        ReadSettingsFromUi();

        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || string.IsNullOrWhiteSpace(_settings.Sip.Username))
        {
            MessageBox.Show("SIP Server and Extension are required.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenAi.ApiKey))
        {
            MessageBox.Show("OpenAI API Key is required.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettings();
        Log($"📞 Connecting to {_settings.Sip.Server}:{_settings.Sip.Port} as {_settings.Sip.Username}…");

        SetSipConnected(true);

        try
        {
            _loggerFactory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Debug);
                b.AddProvider(new CallbackLoggerProvider(Log));
            });

            var sessionManager = new SessionManager(
                _loggerFactory.CreateLogger<SessionManager>(),
                CreateCallSession);

            _sipServer = new SipServer(
                _loggerFactory.CreateLogger<SipServer>(),
                _settings.Sip,
                _settings.Audio,
                sessionManager);

            _sipServer.OnLog += msg => BeginInvoke(() => Log(msg));

            await _sipServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ SIP start failed: {ex.Message}");
            SetSipConnected(false);
            _sipServer = null;
        }
    }

    private ICallSession CreateCallSession(string sessionId, string callerId)
    {
        var factory = _loggerFactory!;

        var aiClient = new OpenAiSdkClient(
            factory.CreateLogger<OpenAiSdkClient>(),
            _settings.OpenAi);

        var fareCalculator = new FareCalculator(
            factory.CreateLogger<FareCalculator>(),
            _settings.GoogleMaps,
            _settings.Supabase);

        var dispatcher = new BsqdDispatcher(
            factory.CreateLogger<BsqdDispatcher>(),
            _settings.Dispatch);

        var session = new CallSession(
            sessionId, callerId,
            factory.CreateLogger<CallSession>(),
            _settings, aiClient, fareCalculator, dispatcher);

        // Wire UI events
        session.OnTranscript += (role, text) => BeginInvoke(() => Log($"💬 {role}: {text}"));
        session.OnBookingUpdated += booking => BeginInvoke(() =>
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(booking.Name)) parts.Add($"Name: {booking.Name}");
            if (!string.IsNullOrEmpty(booking.Pickup)) parts.Add($"From: {booking.Pickup}");
            if (!string.IsNullOrEmpty(booking.Destination)) parts.Add($"To: {booking.Destination}");
            if (!string.IsNullOrEmpty(booking.Fare)) parts.Add($"Fare: {booking.Fare}");
            if (booking.Confirmed) parts.Add($"REF: {booking.BookingRef}");
            _lblCallInfo.Text = parts.Count > 0 ? string.Join(" | ", parts) : "Call in progress";
            _lblCallInfo.ForeColor = booking.Confirmed ? Color.LimeGreen : Color.Cyan;
        });

        session.OnEnded += (s, reason) => BeginInvoke(() =>
        {
            Log($"📴 Call {s.SessionId} ended: {reason}");
            _lblCallInfo.Text = "No active call";
            _lblCallInfo.ForeColor = Color.Gray;
            UpdateCallCount();
        });

        UpdateCallCount();
        return session;
    }

    private async void BtnDisconnect_Click(object? sender, EventArgs e)
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
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        _lblSipStatus.Text = "● Disconnected";
        _lblSipStatus.ForeColor = Color.Gray;
        _lblActiveCalls.Text = "Calls: 0";
        _lblCallInfo.Text = "No active call";
        _lblCallInfo.ForeColor = Color.Gray;
        Log("✅ Disconnected.");
    }

    // ═══════════════════════════════════════
    // UI STATE
    // ═══════════════════════════════════════
    private void SetSipConnected(bool connected)
    {
        _sipConnected = connected;
        _btnConnect.Enabled = !connected;
        _btnDisconnect.Enabled = connected;
        _txtServer.Enabled = !connected;
        _txtPort.Enabled = !connected;
        _txtUser.Enabled = !connected;
        _txtPassword.Enabled = !connected;
        _txtAuthId.Enabled = !connected;
        _cmbTransport.Enabled = !connected;
        _txtApiKey.Enabled = !connected;
        _txtModel.Enabled = !connected;
        _cmbVoice.Enabled = !connected;

        if (connected)
        {
            _lblSipStatus.Text = "● Connecting…";
            _lblSipStatus.ForeColor = Color.Orange;
        }
    }

    private void UpdateCallCount()
    {
        var count = _sipServer?.ActiveCallCount ?? 0;
        _lblActiveCalls.Text = $"Calls: {count}";
    }

    // ═══════════════════════════════════════
    // SETTINGS
    // ═══════════════════════════════════════
    private void ReadSettingsFromUi()
    {
        _settings.Sip.Server = _txtServer.Text.Trim();
        _settings.Sip.Port = int.TryParse(_txtPort.Text, out var p) ? p : 5060;
        _settings.Sip.Username = _txtUser.Text.Trim();
        _settings.Sip.Password = _txtPassword.Text;
        _settings.Sip.AuthId = string.IsNullOrWhiteSpace(_txtAuthId.Text) ? null : _txtAuthId.Text.Trim();
        _settings.Sip.Transport = _cmbTransport.SelectedItem?.ToString() ?? "UDP";

        _settings.OpenAi.ApiKey = _txtApiKey.Text.Trim();
        _settings.OpenAi.Model = _txtModel.Text.Trim();
        _settings.OpenAi.Voice = _cmbVoice.SelectedItem?.ToString() ?? "shimmer";
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdaSdkModel", "appsettings.json");

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
        catch { }
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

    // ═══════════════════════════════════════
    // LOGGING
    // ═══════════════════════════════════════
    private void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }

        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.SelectionColor = message.Contains("❌") || message.Contains("Error") ? Color.Red
            : message.Contains("✅") || message.Contains("Booked") ? Color.LimeGreen
            : message.Contains("⚠") ? Color.Yellow
            : message.Contains("📞") || message.Contains("📲") ? Color.Cyan
            : Color.LightGreen;
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        _txtLog.ScrollToCaret();

        // Trim old lines
        if (_txtLog.Lines.Length > 800)
        {
            _txtLog.SelectionStart = 0;
            _txtLog.SelectionLength = _txtLog.GetFirstCharIndexFromLine(200);
            _txtLog.SelectedText = "";
        }
    }

    // ═══════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════
    private static Label Lbl(string text) => new()
    {
        Text = text,
        ForeColor = Color.LightGray,
        AutoSize = true,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI", 9F)
    };

    private static TextBox Txt(string value, bool password = false) => new()
    {
        Text = value,
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(50, 50, 55),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        UseSystemPasswordChar = password
    };

    private static Button Btn(string text, Color backColor) => new()
    {
        Text = text,
        BackColor = backColor,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        Cursor = Cursors.Hand
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_sipConnected)
        {
            ReadSettingsFromUi();
            SaveSettings();

            try
            {
                _sipServer?.StopAsync().GetAwaiter().GetResult();
                _sipServer = null;
            }
            catch { }
        }

        _loggerFactory?.Dispose();
        base.OnFormClosing(e);
    }
}
