using System.Text.Json;
using WhatsAppTaxiBooker.Config;
using WhatsAppTaxiBooker.Data;
using WhatsAppTaxiBooker.Models;
using WhatsAppTaxiBooker.Services;

namespace WhatsAppTaxiBooker;

public partial class MainForm : Form
{
    private AppConfig _config = new();
    private BookingDb? _db;
    private GeminiService? _gemini;
    private WhatsAppService? _whatsApp;
    private WebhookListener? _webhook;
    private BookingEngine? _engine;
    private MqttDispatcher? _mqtt;
    private NgrokManager? _ngrok;
    private IcabbiService? _icabbi;
    private IcabbiWebhookHandler? _icabbiHandler;

    // Controls
    private readonly RichTextBox _logBox;
    private readonly DataGridView _bookingGrid;
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Button _btnRefresh;
    private readonly Label _lblStatus;
    private readonly Label _lblMqttStatus;
    private readonly Label _lblNgrokStatus;
    private readonly Label _lblIcabbiStatus;
    private readonly TextBox _txtGeminiKey;
    private readonly TextBox _txtWaToken;
    private readonly TextBox _txtPhoneId;
    private readonly TextBox _txtMqttBroker;
    private readonly NumericUpDown _nudPort;
    private readonly CheckBox _chkNgrok;
    private readonly TextBox _txtNgrokPath;
    private readonly TextBox _txtNgrokDomain;
    private readonly CheckBox _chkIcabbi;
    private readonly TextBox _txtIcabbiAppKey;
    private readonly TextBox _txtIcabbiSecret;
    private readonly TextBox _txtIcabbiTenant;

    public MainForm()
    {
        Text = "WhatsApp Taxi Booker — Gemini Flash + MQTT + iCabbi";
        Size = new Size(1280, 900);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Config panel ──
        var configGroup = new GroupBox { Text = "Configuration", Dock = DockStyle.Top, Height = 235, Padding = new Padding(8) };
        var configTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 8 };
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _txtGeminiKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _txtWaToken = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _txtPhoneId = new TextBox { Dock = DockStyle.Fill };
        _txtMqttBroker = new TextBox { Dock = DockStyle.Fill };
        _nudPort = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1024, Maximum = 65535, Value = 5088 };
        _chkNgrok = new CheckBox { Text = "Enable ngrok", AutoSize = true, Anchor = AnchorStyles.Left };
        _txtNgrokPath = new TextBox { Dock = DockStyle.Fill, PlaceholderText = @"C:\ngrok\ngrok.exe" };
        _txtNgrokDomain = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "your-reserved.ngrok-free.app (optional)" };
        _chkIcabbi = new CheckBox { Text = "Enable iCabbi", AutoSize = true, Anchor = AnchorStyles.Left };
        _txtIcabbiAppKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "App Key" };
        _txtIcabbiSecret = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "Secret Key" };
        _txtIcabbiTenant = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "https://yourtenant.icabbi.net" };

        // Row 0
        configTable.Controls.Add(new Label { Text = "Gemini API Key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        configTable.Controls.Add(_txtGeminiKey, 1, 0);
        configTable.Controls.Add(new Label { Text = "WhatsApp Token:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        configTable.Controls.Add(_txtWaToken, 3, 0);

        // Row 1
        configTable.Controls.Add(new Label { Text = "Phone Number ID:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        configTable.Controls.Add(_txtPhoneId, 1, 1);
        configTable.Controls.Add(new Label { Text = "Webhook Port:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        configTable.Controls.Add(_nudPort, 3, 1);

        // Row 2
        configTable.Controls.Add(new Label { Text = "MQTT Broker:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        configTable.Controls.Add(_txtMqttBroker, 1, 2);
        _lblMqttStatus = new Label { Text = "⚪ MQTT: Disconnected", AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 0, 0) };
        configTable.Controls.Add(_lblMqttStatus, 2, 2);
        configTable.SetColumnSpan(_lblMqttStatus, 2);

        // Row 3 — ngrok
        configTable.Controls.Add(_chkNgrok, 0, 3);
        configTable.Controls.Add(_txtNgrokPath, 1, 3);
        configTable.Controls.Add(new Label { Text = "Reserved Domain:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 3);
        configTable.Controls.Add(_txtNgrokDomain, 3, 3);

        // Row 4 — ngrok status
        _lblNgrokStatus = new Label { Text = "⚪ ngrok: Off", AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left, Padding = new Padding(0, 3, 0, 0) };
        configTable.Controls.Add(_lblNgrokStatus, 0, 4);
        configTable.SetColumnSpan(_lblNgrokStatus, 4);

        // Row 5 — iCabbi
        configTable.Controls.Add(_chkIcabbi, 0, 5);
        configTable.Controls.Add(_txtIcabbiAppKey, 1, 5);
        configTable.Controls.Add(new Label { Text = "iCabbi Secret:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 5);
        configTable.Controls.Add(_txtIcabbiSecret, 3, 5);

        // Row 6 — iCabbi tenant + status
        configTable.Controls.Add(new Label { Text = "iCabbi Tenant:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        configTable.Controls.Add(_txtIcabbiTenant, 1, 6);
        _lblIcabbiStatus = new Label { Text = "⚪ iCabbi: Off", AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 0, 0) };
        configTable.Controls.Add(_lblIcabbiStatus, 2, 6);
        configTable.SetColumnSpan(_lblIcabbiStatus, 2);

        // Row 7 — buttons
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _btnStart = new Button { Text = "▶ Start", Width = 100, Height = 30 };
        _btnStop = new Button { Text = "⏹ Stop", Width = 100, Height = 30, Enabled = false };
        _btnRefresh = new Button { Text = "🔄 Refresh", Width = 100, Height = 30 };
        _lblStatus = new Label { Text = "⏸ Stopped", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(10, 7, 0, 0) };
        _btnStart.Click += BtnStart_Click;
        _btnStop.Click += BtnStop_Click;
        _btnRefresh.Click += (_, _) => RefreshBookings();
        btnPanel.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnRefresh, _lblStatus });
        configTable.Controls.Add(btnPanel, 0, 7);
        configTable.SetColumnSpan(btnPanel, 4);

        configGroup.Controls.Add(configTable);
        Controls.Add(configGroup);

        // ── Split panel: bookings grid + log ──
        var splitter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 350
        };

        _bookingGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AllowUserToResizeColumns = true,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            GridColor = Color.LightGray,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Segoe UI", 9f) },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(45, 45, 48)
            },
            EnableHeadersVisualStyles = false
        };

        _bookingGrid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Ref", Name = "Id", Width = 160 },
            new DataGridViewTextBoxColumn { HeaderText = "Phone", Name = "Phone", Width = 120 },
            new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "CallerName", Width = 90 },
            new DataGridViewTextBoxColumn { HeaderText = "Pickup", Name = "Pickup", Width = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "Destination", Name = "Destination", Width = 200 },
            new DataGridViewTextBoxColumn { HeaderText = "Pax", Name = "Passengers", Width = 40 },
            new DataGridViewTextBoxColumn { HeaderText = "Time", Name = "PickupTime", Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Notes", Name = "Notes", Width = 120 },
            new DataGridViewTextBoxColumn { HeaderText = "Fare", Name = "Fare", Width = 60 },
            new DataGridViewTextBoxColumn { HeaderText = "Status", Name = "Status", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Created", Name = "CreatedAt", Width = 130 },
            new DataGridViewTextBoxColumn { HeaderText = "Updated", Name = "UpdatedAt", Width = 130 }
        );

        _bookingGrid.CellFormatting += BookingGrid_CellFormatting;

        var gridGroup = new GroupBox { Text = "Bookings (indexed by caller phone)", Dock = DockStyle.Fill };
        gridGroup.Controls.Add(_bookingGrid);
        splitter.Panel1.Controls.Add(gridGroup);

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 9f)
        };
        var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        logGroup.Controls.Add(_logBox);
        splitter.Panel2.Controls.Add(logGroup);

        Controls.Add(splitter);
        LoadConfig();
    }

    private void BookingGrid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_bookingGrid.Columns[e.ColumnIndex].Name != "Status" || e.Value == null) return;

        var status = e.Value.ToString()?.ToLowerInvariant();
        e.CellStyle!.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

        switch (status)
        {
            case "confirmed":
                e.CellStyle.BackColor = Color.FromArgb(200, 255, 200);
                e.CellStyle.ForeColor = Color.DarkGreen;
                break;
            case "ready":
                e.CellStyle.BackColor = Color.FromArgb(200, 220, 255);
                e.CellStyle.ForeColor = Color.DarkBlue;
                break;
            case "collecting":
                e.CellStyle.BackColor = Color.FromArgb(255, 255, 200);
                e.CellStyle.ForeColor = Color.DarkGoldenrod;
                break;
            case "cancelled":
                e.CellStyle.BackColor = Color.FromArgb(255, 200, 200);
                e.CellStyle.ForeColor = Color.DarkRed;
                break;
        }
    }

    private void LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
            }
        }
        catch { /* defaults */ }

        _txtGeminiKey.Text = _config.Gemini.ApiKey;
        _txtWaToken.Text = _config.WhatsApp.AccessToken;
        _txtPhoneId.Text = _config.WhatsApp.PhoneNumberId;
        _nudPort.Value = _config.Webhook.Port;
        _txtMqttBroker.Text = _config.Mqtt.BrokerUrl;
        _chkNgrok.Checked = _config.Ngrok.Enabled;
        _txtNgrokPath.Text = _config.Ngrok.NgrokPath;
        _txtNgrokDomain.Text = _config.Ngrok.ReservedDomain;
        _chkIcabbi.Checked = _config.Icabbi.Enabled;
        _txtIcabbiAppKey.Text = _config.Icabbi.AppKey;
        _txtIcabbiSecret.Text = _config.Icabbi.SecretKey;
        _txtIcabbiTenant.Text = _config.Icabbi.TenantBase;
    }

    private void SaveConfig()
    {
        _config.Gemini.ApiKey = _txtGeminiKey.Text.Trim();
        _config.WhatsApp.AccessToken = _txtWaToken.Text.Trim();
        _config.WhatsApp.PhoneNumberId = _txtPhoneId.Text.Trim();
        _config.Webhook.Port = (int)_nudPort.Value;
        _config.Mqtt.BrokerUrl = _txtMqttBroker.Text.Trim();
        _config.Ngrok.Enabled = _chkNgrok.Checked;
        _config.Ngrok.NgrokPath = _txtNgrokPath.Text.Trim();
        _config.Ngrok.ReservedDomain = _txtNgrokDomain.Text.Trim();
        _config.Icabbi.Enabled = _chkIcabbi.Checked;
        _config.Icabbi.AppKey = _txtIcabbiAppKey.Text.Trim();
        _config.Icabbi.SecretKey = _txtIcabbiSecret.Text.Trim();
        _config.Icabbi.TenantBase = _txtIcabbiTenant.Text.Trim();

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_txtGeminiKey.Text) ||
            string.IsNullOrWhiteSpace(_txtWaToken.Text) ||
            string.IsNullOrWhiteSpace(_txtPhoneId.Text))
        {
            MessageBox.Show("Please fill in Gemini API Key, WhatsApp Token, and Phone Number ID.", "Missing Config", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveConfig();
        _btnStart.Enabled = false;
        _lblStatus.Text = "⏳ Starting...";
        _lblStatus.ForeColor = Color.Orange;

        // ── Start ngrok if enabled ──
        if (_config.Ngrok.Enabled)
        {
            if (!File.Exists(_config.Ngrok.NgrokPath))
            {
                MessageBox.Show($"ngrok not found at:\n{_config.Ngrok.NgrokPath}", "ngrok Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnStart.Enabled = true;
                _lblStatus.Text = "⏸ Stopped";
                _lblStatus.ForeColor = Color.Gray;
                return;
            }

            _lblNgrokStatus.Text = "🟡 ngrok: Starting...";
            _lblNgrokStatus.ForeColor = Color.Orange;

            var domain = string.IsNullOrWhiteSpace(_config.Ngrok.ReservedDomain) ? null : _config.Ngrok.ReservedDomain;
            _ngrok = new NgrokManager(_config.Ngrok.NgrokPath, _config.Webhook.Port.ToString(), domain);
            _ngrok.OnLog += AppendLog;
            _ngrok.Start();

            var url = await _ngrok.GetPublicUrlAsync();
            if (url != null)
            {
                _lblNgrokStatus.Text = $"🟢 ngrok: {url}/webhook/";
                _lblNgrokStatus.ForeColor = Color.LimeGreen;
                AppendLog($"🌍 WhatsApp webhook: {url}/webhook/");
                AppendLog($"🌍 iCabbi webhook: {url}/icabbi/");
            }
            else
            {
                _lblNgrokStatus.Text = "🔴 ngrok: Failed to get URL";
                _lblNgrokStatus.ForeColor = Color.Red;
            }
        }

        // ── Start services ──
        _db = new BookingDb();
        _gemini = new GeminiService(_config.Gemini);
        _whatsApp = new WhatsAppService(_config.WhatsApp);
        _mqtt = new MqttDispatcher(_config.Mqtt);
        _webhook = new WebhookListener(_config.Webhook, _config.WhatsApp.VerifyToken);

        // iCabbi
        IcabbiService? icabbiSvc = null;
        if (_config.Icabbi.Enabled && !string.IsNullOrWhiteSpace(_config.Icabbi.AppKey))
        {
            icabbiSvc = new IcabbiService(_config.Icabbi);
            icabbiSvc.OnLog += AppendLog;
            _icabbi = icabbiSvc;

            _icabbiHandler = new IcabbiWebhookHandler(_whatsApp);
            _icabbiHandler.OnLog += AppendLog;
            _icabbiHandler.OnEvent += evt => Invoke(() => AppendLog($"📡 iCabbi: {evt.EventType} → {evt.CustomerPhone}"));

            // Wire iCabbi webhook events
            _webhook.OnIcabbiEvent += body => Task.Run(() => _icabbiHandler.HandleEventAsync(body));

            _lblIcabbiStatus.Text = "🟢 iCabbi: Enabled";
            _lblIcabbiStatus.ForeColor = Color.LimeGreen;
            AppendLog("✅ iCabbi integration enabled — bookings will be sent on confirm");
        }
        else
        {
            _lblIcabbiStatus.Text = "⚪ iCabbi: Off";
            _lblIcabbiStatus.ForeColor = Color.Gray;
        }

        _engine = new BookingEngine(_gemini, _whatsApp, _db, _config.WhatsApp, _mqtt, icabbiSvc);

        // Wire logging
        _gemini.OnLog += AppendLog;
        _whatsApp.OnLog += AppendLog;
        _webhook.OnLog += AppendLog;
        _engine.OnLog += AppendLog;
        _mqtt.OnLog += AppendLog;

        // Wire events
        _webhook.OnMessage += msg => Task.Run(() => _engine.HandleMessageAsync(msg));
        _engine.OnBookingCreated += _ => Invoke(RefreshBookings);
        _engine.OnBookingUpdated += _ => Invoke(RefreshBookings);
        _engine.OnBookingDispatched += _ => Invoke(RefreshBookings);

        // Connect MQTT
        await _mqtt.ConnectAsync();
        Invoke(() =>
        {
            _lblMqttStatus.Text = _mqtt.IsConnected ? "🟢 MQTT: Connected" : "🔴 MQTT: Failed";
            _lblMqttStatus.ForeColor = _mqtt.IsConnected ? Color.LimeGreen : Color.Red;
        });

        _webhook.Start();

        _btnStop.Enabled = true;
        _lblStatus.Text = "🟢 Running";
        _lblStatus.ForeColor = Color.LimeGreen;

        AppendLog($"✅ WhatsApp Taxi Booker started — port {_config.Webhook.Port}, MQTT {(_mqtt.IsConnected ? "✓" : "✗")}, iCabbi {(_config.Icabbi.Enabled ? "✓" : "✗")}");
        RefreshBookings();
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        _webhook?.Stop();
        _mqtt?.Dispose();
        _ngrok?.Stop();
        _ngrok = null;
        _icabbi = null;
        _icabbiHandler = null;
        _db?.Dispose();
        _db = null;

        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "⏸ Stopped";
        _lblStatus.ForeColor = Color.Gray;
        _lblMqttStatus.Text = "⚪ MQTT: Disconnected";
        _lblMqttStatus.ForeColor = Color.Gray;
        _lblNgrokStatus.Text = "⚪ ngrok: Off";
        _lblNgrokStatus.ForeColor = Color.Gray;
        _lblIcabbiStatus.Text = "⚪ iCabbi: Off";
        _lblIcabbiStatus.ForeColor = Color.Gray;

        AppendLog("🛑 Stopped");
    }

    private void RefreshBookings()
    {
        if (InvokeRequired) { Invoke(RefreshBookings); return; }
        if (_db == null) return;

        var bookings = _db.GetRecentBookings();
        _bookingGrid.Rows.Clear();
        foreach (var b in bookings)
        {
            _bookingGrid.Rows.Add(
                b.Id, b.Phone, b.CallerName ?? "",
                b.Pickup, b.Destination,
                b.Passengers, b.PickupTime ?? "",
                b.Notes ?? "", b.Fare ?? "",
                b.Status,
                b.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                b.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
        }
    }

    private void AppendLog(string msg)
    {
        if (InvokeRequired) { Invoke(() => AppendLog(msg)); return; }
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText($"[{timestamp}] {msg}\n");
        _logBox.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _webhook?.Stop();
        _mqtt?.Dispose();
        _ngrok?.Stop();
        _db?.Dispose();
        base.OnFormClosing(e);
    }
}
