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

    // Controls
    private readonly RichTextBox _logBox;
    private readonly DataGridView _bookingGrid;
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Label _lblStatus;
    private readonly TextBox _txtGeminiKey;
    private readonly TextBox _txtWaToken;
    private readonly TextBox _txtPhoneId;
    private readonly NumericUpDown _nudPort;

    public MainForm()
    {
        Text = "WhatsApp Taxi Booker — Gemini Flash";
        Size = new Size(1100, 750);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Config panel ──
        var configGroup = new GroupBox { Text = "Configuration", Dock = DockStyle.Top, Height = 130, Padding = new Padding(8) };
        var configTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3 };
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _txtGeminiKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _txtWaToken = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _txtPhoneId = new TextBox { Dock = DockStyle.Fill };
        _nudPort = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1024, Maximum = 65535, Value = 5088 };

        configTable.Controls.Add(new Label { Text = "Gemini API Key:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        configTable.Controls.Add(_txtGeminiKey, 1, 0);
        configTable.Controls.Add(new Label { Text = "WhatsApp Token:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        configTable.Controls.Add(_txtWaToken, 3, 0);
        configTable.Controls.Add(new Label { Text = "Phone Number ID:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        configTable.Controls.Add(_txtPhoneId, 1, 1);
        configTable.Controls.Add(new Label { Text = "Webhook Port:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        configTable.Controls.Add(_nudPort, 3, 1);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _btnStart = new Button { Text = "▶ Start", Width = 100, Height = 30 };
        _btnStop = new Button { Text = "⏹ Stop", Width = 100, Height = 30, Enabled = false };
        _lblStatus = new Label { Text = "⏸ Stopped", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(10, 7, 0, 0) };
        _btnStart.Click += BtnStart_Click;
        _btnStop.Click += BtnStop_Click;
        btnPanel.Controls.AddRange(new Control[] { _btnStart, _btnStop, _lblStatus });
        configTable.Controls.Add(btnPanel, 0, 2);
        configTable.SetColumnSpan(btnPanel, 4);

        configGroup.Controls.Add(configTable);
        Controls.Add(configGroup);

        // ── Split panel: bookings grid + log ──
        var splitter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300
        };

        // Booking grid
        _bookingGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        _bookingGrid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Ref", DataPropertyName = "Id", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Phone", DataPropertyName = "Phone", Width = 100 },
            new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "CallerName", Width = 80 },
            new DataGridViewTextBoxColumn { HeaderText = "Pickup", DataPropertyName = "Pickup", Width = 180 },
            new DataGridViewTextBoxColumn { HeaderText = "Destination", DataPropertyName = "Destination", Width = 180 },
            new DataGridViewTextBoxColumn { HeaderText = "Pax", DataPropertyName = "Passengers", Width = 40 },
            new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = "Status", Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Created", DataPropertyName = "CreatedAt", Width = 130 }
        );

        var gridGroup = new GroupBox { Text = "Bookings", Dock = DockStyle.Fill };
        gridGroup.Controls.Add(_bookingGrid);
        splitter.Panel1.Controls.Add(gridGroup);

        // Log box
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

        // Load config
        LoadConfig();
    }

    private void LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new AppConfig();
            }
        }
        catch { /* use defaults */ }

        _txtGeminiKey.Text = _config.Gemini.ApiKey;
        _txtWaToken.Text = _config.WhatsApp.AccessToken;
        _txtPhoneId.Text = _config.WhatsApp.PhoneNumberId;
        _nudPort.Value = _config.Webhook.Port;
    }

    private void SaveConfig()
    {
        _config.Gemini.ApiKey = _txtGeminiKey.Text.Trim();
        _config.WhatsApp.AccessToken = _txtWaToken.Text.Trim();
        _config.WhatsApp.PhoneNumberId = _txtPhoneId.Text.Trim();
        _config.Webhook.Port = (int)_nudPort.Value;

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_txtGeminiKey.Text) ||
            string.IsNullOrWhiteSpace(_txtWaToken.Text) ||
            string.IsNullOrWhiteSpace(_txtPhoneId.Text))
        {
            MessageBox.Show("Please fill in Gemini API Key, WhatsApp Token, and Phone Number ID.",
                "Missing Config", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveConfig();

        _db = new BookingDb();
        _gemini = new GeminiService(_config.Gemini);
        _whatsApp = new WhatsAppService(_config.WhatsApp);
        _webhook = new WebhookListener(_config.Webhook, _config.WhatsApp.VerifyToken);
        _engine = new BookingEngine(_gemini, _whatsApp, _db, _config.WhatsApp);

        // Wire up logging
        _gemini.OnLog += AppendLog;
        _whatsApp.OnLog += AppendLog;
        _webhook.OnLog += AppendLog;
        _engine.OnLog += AppendLog;

        // Wire up message handling
        _webhook.OnMessage += msg => Task.Run(() => _engine.HandleMessageAsync(msg));

        // Wire up booking display
        _engine.OnBookingCreated += booking => Invoke(() => RefreshBookings());

        _webhook.Start();

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _lblStatus.Text = "🟢 Running";
        _lblStatus.ForeColor = Color.LimeGreen;

        AppendLog($"✅ WhatsApp Taxi Booker started on port {_config.Webhook.Port}");
        RefreshBookings();
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        _webhook?.Stop();
        _db?.Dispose();
        _db = null;

        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "⏸ Stopped";
        _lblStatus.ForeColor = Color.Gray;

        AppendLog("🛑 Stopped");
    }

    private void RefreshBookings()
    {
        if (_db == null) return;
        var bookings = _db.GetRecentBookings();
        _bookingGrid.Rows.Clear();
        foreach (var b in bookings)
        {
            _bookingGrid.Rows.Add(b.Id, b.Phone, b.CallerName, b.Pickup, b.Destination,
                b.Passengers, b.Status, b.CreatedAt.ToString("yyyy-MM-dd HH:mm"));
        }
    }

    private void AppendLog(string msg)
    {
        if (InvokeRequired)
        {
            Invoke(() => AppendLog(msg));
            return;
        }
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText($"[{timestamp}] {msg}\n");
        _logBox.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _webhook?.Stop();
        _db?.Dispose();
        base.OnFormClosing(e);
    }
}
