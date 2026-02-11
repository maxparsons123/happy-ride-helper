using DispatchSystem.Data;
using DispatchSystem.Dispatch;
using DispatchSystem.Mqtt;
using DispatchSystem.UI;
using DispatchSystem.Webhook;

namespace DispatchSystem;

public class MainForm : Form
{
    // ── Panels ──
    private readonly MapPanel _map;
    private readonly JobListPanel _jobList;
    private readonly DriverListPanel _driverList;
    private readonly LogPanel _logPanel;

    // ── Controls ──
    private readonly Button _btnConnect;
    private readonly Button _btnDisconnect;
    private readonly Button _btnManualDispatch;
    private readonly Button _btnAddDriver;
    private readonly Button _btnRunDispatch;
    private readonly CheckBox _chkAutoDispatch;
    private readonly Label _lblStatus;
    private readonly Label _lblStats;

    // ── Core ──
    private DispatchDb? _db;
    private MqttDispatchClient? _mqtt;
    private AutoDispatcher? _dispatcher;
    private WebhookListener? _webhook;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public MainForm()
    {
        Text = "Ada Dispatch System v1.0";
        Size = new Size(1400, 900);
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        // ── Toolbar ──
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.FromArgb(35, 35, 40),
            Padding = new Padding(8, 6, 8, 6),
            FlowDirection = FlowDirection.LeftToRight
        };

        _btnConnect = MakeButton("🔌 Connect MQTT", Color.FromArgb(0, 120, 60));
        _btnConnect.Click += async (_, _) => await ConnectAsync();

        _btnDisconnect = MakeButton("⏹ Disconnect", Color.FromArgb(140, 40, 40));
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += async (_, _) => await DisconnectAsync();

        _btnAddDriver = MakeButton("➕ Add Driver", Color.FromArgb(50, 80, 140));
        _btnAddDriver.Click += BtnAddDriver_Click;

        _btnManualDispatch = MakeButton("🎯 Manual Dispatch", Color.FromArgb(120, 60, 10));
        _btnManualDispatch.Click += BtnManualDispatch_Click;

        _btnRunDispatch = MakeButton("⚡ Run Now", Color.FromArgb(80, 50, 130));
        _btnRunDispatch.Click += (_, _) => _dispatcher?.RunCycle();

        _chkAutoDispatch = new CheckBox
        {
            Text = "Auto-Dispatch (60s)",
            ForeColor = Color.White,
            Checked = true,
            AutoSize = true,
            Padding = new Padding(10, 8, 0, 0)
        };
        _chkAutoDispatch.CheckedChanged += (_, _) =>
        {
            if (_dispatcher != null) _dispatcher.Enabled = _chkAutoDispatch.Checked;
        };

        _lblStatus = new Label
        {
            Text = "● Disconnected",
            ForeColor = Color.Gray,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Padding = new Padding(15, 8, 0, 0)
        };

        _lblStats = new Label
        {
            Text = "",
            ForeColor = Color.LightBlue,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F),
            Padding = new Padding(10, 9, 0, 0)
        };

        toolbar.Controls.AddRange(new Control[]
        {
            _btnConnect, _btnDisconnect, _btnAddDriver, _btnManualDispatch,
            _btnRunDispatch, _chkAutoDispatch, _lblStatus, _lblStats
        });

        // ── Layout ──
        // Left: Map (60%), Right top: Drivers, Right mid: Jobs, Bottom: Log
        var splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 620,
            BackColor = Color.FromArgb(28, 28, 32),
            Panel1MinSize = 300,
            Panel2MinSize = 120
        };

        var splitTop = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 820,
            Panel1MinSize = 400,
            Panel2MinSize = 250
        };

        var splitRight = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 250,
            Panel1MinSize = 120,
            Panel2MinSize = 120
        };

        _map = new MapPanel { Dock = DockStyle.Fill };
        _driverList = new DriverListPanel { Dock = DockStyle.Fill };
        _jobList = new JobListPanel { Dock = DockStyle.Fill };
        _logPanel = new LogPanel { Dock = DockStyle.Fill };

        splitRight.Panel1.Controls.Add(_driverList);
        splitRight.Panel2.Controls.Add(_jobList);

        splitTop.Panel1.Controls.Add(_map);
        splitTop.Panel2.Controls.Add(splitRight);

        splitMain.Panel1.Controls.Add(splitTop);
        splitMain.Panel2.Controls.Add(_logPanel);

        Controls.Add(splitMain);
        Controls.Add(toolbar);

        // ── Refresh timer ──
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (_, _) => RefreshUI();

        // Init DB
        InitDatabase();
    }

    private void InitDatabase()
    {
        try
        {
            _db = new DispatchDb();
            _dispatcher = new AutoDispatcher(_db);
            _dispatcher.OnLog += msg => _logPanel.AppendLog(msg);
            _dispatcher.OnJobAllocated += OnJobAllocated;

            // Start webhook listener on port 5080
            _webhook = new WebhookListener(5080);
            _webhook.OnLog += msg => BeginInvoke(() => _logPanel.AppendLog(msg, Color.MediumPurple));
            _webhook.OnJobReceived += job => BeginInvoke(() => OnBookingReceived(job));
            _webhook.Start();

            _logPanel.AppendLog("💾 SQLite database ready", Color.Cyan);
            RefreshUI();
        }
        catch (Exception ex)
        {
            _logPanel.AppendLog($"❌ DB init failed: {ex.Message}", Color.Red);
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            _mqtt = new MqttDispatchClient();
            _mqtt.OnLog += msg => _logPanel.AppendLog(msg);
            _mqtt.OnDriverGps += OnDriverGps;
            _mqtt.OnBookingReceived += OnBookingReceived;
            _mqtt.OnJobStatusUpdate += OnJobStatusUpdate;

            await _mqtt.ConnectAsync();
            await _map.InitializeAsync();

            _btnConnect.Enabled = false;
            _btnDisconnect.Enabled = true;
            _lblStatus.Text = "● Connected";
            _lblStatus.ForeColor = Color.LimeGreen;
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            _logPanel.AppendLog($"❌ Connect failed: {ex.Message}", Color.Red);
        }
    }

    private async Task DisconnectAsync()
    {
        _refreshTimer.Stop();
        if (_mqtt != null) await _mqtt.DisconnectAsync();
        _btnConnect.Enabled = true;
        _btnDisconnect.Enabled = false;
        _lblStatus.Text = "● Disconnected";
        _lblStatus.ForeColor = Color.Gray;
    }

    // ── MQTT Handlers ──

    private void OnDriverGps(string driverId, double lat, double lng, string? status)
    {
        if (_db == null) return;

        DriverStatus? ds = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<DriverStatus>(status, true, out var parsed))
            ds = parsed;

        // Auto-register driver if new
        var drivers = _db.GetAllDrivers();
        var existing = drivers.FirstOrDefault(d => d.Id == driverId);
        if (existing == null)
        {
            _db.UpsertDriver(new Driver
            {
                Id = driverId,
                Name = driverId,
                Status = ds ?? DriverStatus.Online,
                Lat = lat,
                Lng = lng,
                LastGpsUpdate = DateTime.UtcNow
            });
            _logPanel.AppendLog($"🆕 Auto-registered driver: {driverId}", Color.Cyan);
        }
        else
        {
            _db.UpdateDriverLocation(driverId, lat, lng, ds);
        }

        _ = _map.UpdateDriverMarker(driverId, lat, lng,
            (ds ?? existing?.Status ?? DriverStatus.Online).ToString(),
            existing?.Name ?? driverId);
    }

    private void OnBookingReceived(Job job)
    {
        if (_db == null) return;

        _db.InsertJob(job);
        _logPanel.AppendLog($"📥 New booking: {job.Pickup} → {job.Dropoff}", Color.Yellow);

        if (job.PickupLat != 0 && job.PickupLng != 0)
            _ = _map.AddJobMarker(job.Id, job.PickupLat, job.PickupLng, job.Pickup, job.CreatedAt);

        RefreshUI();
    }

    private void OnJobStatusUpdate(string jobId, string driverId, string status)
    {
        if (_db == null) return;

        if (Enum.TryParse<JobStatus>(status, true, out var js))
        {
            _db.UpdateJobStatus(jobId, js, string.IsNullOrEmpty(driverId) ? null : driverId);
            _logPanel.AppendLog($"🔄 Job {jobId}: {status}", Color.DodgerBlue);

            if (js == JobStatus.Completed || js == JobStatus.Cancelled)
                _ = _map.RemoveJobMarker(jobId);

            RefreshUI();
        }
    }

    private async void OnJobAllocated(Job job, Driver driver)
    {
        if (_mqtt != null)
            await _mqtt.PublishJobAllocation(job.Id, driver.Id, job);

        if (job.PickupLat != 0)
            await _map.DrawAllocationLine(job.Id, driver.Lat, driver.Lng, job.PickupLat, job.PickupLng);

        RefreshUI();
    }

    // ── Manual Actions ──

    private void BtnAddDriver_Click(object? sender, EventArgs e)
    {
        if (_db == null) return;

        using var dlg = new AddDriverDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.NewDriver != null)
        {
            _db.UpsertDriver(dlg.NewDriver);
            _logPanel.AppendLog($"➕ Driver added: {dlg.NewDriver.Name} ({dlg.NewDriver.Vehicle})", Color.LimeGreen);
            RefreshUI();
        }
    }

    private void BtnManualDispatch_Click(object? sender, EventArgs e)
    {
        if (_db == null) return;

        var jobId = _jobList.SelectedJobId;
        var driverId = _driverList.SelectedDriverId;

        if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(driverId))
        {
            MessageBox.Show("Select a job AND a driver first.", "Manual Dispatch",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var drivers = _db.GetAllDrivers();
        var driver = drivers.FirstOrDefault(d => d.Id == driverId);
        if (driver == null) return;

        var jobs = _db.GetActiveJobs();
        var job = jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return;

        var distKm = AutoDispatcher.HaversineKm(job.PickupLat, job.PickupLng, driver.Lat, driver.Lng);
        var eta = (int)Math.Ceiling(distKm / 0.5);

        _db.UpdateJobStatus(jobId, JobStatus.Allocated, driverId, distKm, eta);
        driver.Status = DriverStatus.OnJob;
        _db.UpsertDriver(driver);

        job.AllocatedDriverId = driverId;
        job.DriverDistanceKm = distKm;
        job.DriverEtaMinutes = eta;

        OnJobAllocated(job, driver);
        _logPanel.AppendLog($"🎯 Manual dispatch: Job {jobId} → {driver.Name}", Color.Gold);
    }

    // ── Refresh ──

    private void RefreshUI()
    {
        if (_db == null) return;
        if (InvokeRequired) { BeginInvoke(RefreshUI); return; }

        var drivers = _db.GetAllDrivers();
        var jobs = _db.GetActiveJobs();

        _driverList.RefreshDrivers(drivers);
        _jobList.RefreshJobs(jobs);

        var online = drivers.Count(d => d.Status == DriverStatus.Online);
        var onJob = drivers.Count(d => d.Status == DriverStatus.OnJob);
        var pending = jobs.Count(j => j.Status == JobStatus.Pending);

        _lblStats.Text = $"Drivers: {online} online, {onJob} on job | Pending jobs: {pending}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _webhook?.Dispose();
        _dispatcher?.Dispose();
        _mqtt?.Dispose();
        _db?.Dispose();
        base.OnFormClosing(e);
    }

    private static Button MakeButton(string text, Color bg) => new()
    {
        Text = text,
        BackColor = bg,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Size = new Size(140, 34),
        Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        Margin = new Padding(0, 0, 6, 0)
    };
}
