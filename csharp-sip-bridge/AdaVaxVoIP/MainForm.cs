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
    private AppSettings _settings;

    // Status
    private readonly Label _lblStatus;
    private readonly Label _lblActiveCalls;
    private readonly RichTextBox _txtLog;

    // Buttons
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Button _btnSettings;

    // Runtime
    private VaxVoIPSipServer? _sipServer;
    private TaxiBookingOrchestrator? _orchestrator;
    private ILoggerFactory? _loggerFactory;
    private CancellationTokenSource? _cts;
    private bool _running;

    public MainForm()
    {
        Text = "Ada Taxi — VaxVoIP Booking System v3.0";
        Size = new Size(820, 580);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 400);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        // Load settings from appsettings.json
        _settings = SettingsStore.Load();

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

        _btnSettings = new Button
        {
            Text = "⚙ Settings",
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(110, 35),
            Font = new Font("Segoe UI", 10F)
        };
        _btnSettings.Click += BtnSettings_Click;

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

        pnlControls.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnSettings, _lblStatus, _lblActiveCalls });

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

        Log($"Settings loaded from appsettings.json (SIP: {_settings.Sip.Server}:{_settings.Sip.Port})", Color.Gray);
    }

    private void BtnSettings_Click(object? sender, EventArgs e)
    {
        using var dlg = new ConfigForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dlg.Settings;
            SettingsStore.Save(_settings);
            Log("✅ Settings saved to appsettings.json", Color.LimeGreen);
        }
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

        if (string.IsNullOrWhiteSpace(_settings.OpenAi.ApiKey))
        {
            MessageBox.Show("OpenAI API Key is required. Open ⚙ Settings to configure.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _btnSettings.Enabled = false;
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
                LicenseKey = _settings.VaxVoIP.LicenseKey,
                DomainRealm = _settings.VaxVoIP.DomainRealm,
                RtpPortMin = _settings.VaxVoIP.RtpPortMin,
                RtpPortMax = _settings.VaxVoIP.RtpPortMax,
                EnableRecording = _settings.VaxVoIP.EnableRecording,
                RecordingsPath = _settings.VaxVoIP.RecordingsPath
            };

            // SIP port comes from the SIP settings (shared with AdaMain)
            var taxiSettings = new TaxiBookingSettings
            {
                CompanyName = _settings.Taxi.CompanyName,
                AutoAnswer = _settings.Sip.AutoAnswer
            };

            var openAISettings = new Config.OpenAiSettings
            {
                ApiKey = _settings.OpenAi.ApiKey,
                Voice = _settings.OpenAi.Voice,
                Model = _settings.OpenAi.Model
            };

            var fareCalculator = new FareCalculator(
                _loggerFactory.CreateLogger<FareCalculator>(),
                new Config.GoogleMapsSettings { ApiKey = _settings.GoogleMaps.ApiKey },
                new Config.SupabaseSettings { Url = _settings.Supabase.Url, AnonKey = _settings.Supabase.AnonKey });

            var dispatcher = new Dispatcher(
                _loggerFactory.CreateLogger<Dispatcher>(),
                new Config.DispatchSettings
                {
                    BsqdWebhookUrl = _settings.Dispatch.BsqdWebhookUrl,
                    BsqdApiKey = _settings.Dispatch.BsqdApiKey,
                    WhatsAppWebhookUrl = _settings.Dispatch.WhatsAppWebhookUrl
                });

            _sipServer = new VaxVoIPSipServer(
                _loggerFactory.CreateLogger<VaxVoIPSipServer>(), vaxSettings, taxiSettings);

            _sipServer.OnCallStarted += (callId, callerId) =>
            {
                Log($"📞 Call started: {callerId} [{callId}]", Color.Cyan);
                BeginInvoke(() => _lblActiveCalls.Text = "Calls: active");
            };
            _sipServer.OnCallEnded += callId =>
            {
                Log($"📴 Call ended: [{callId}]", Color.Orange);
                BeginInvoke(() => _lblActiveCalls.Text = "Calls: 0");
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
            Log($"🚀 Server started — SIP {_settings.Sip.Server}:{_settings.Sip.Port}", Color.LimeGreen);
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to start: {ex.Message}", Color.Red);
            _lblStatus.Text = "● Error";
            _lblStatus.ForeColor = Color.Red;
            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
            _btnSettings.Enabled = true;
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
        _btnSettings.Enabled = true;
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
