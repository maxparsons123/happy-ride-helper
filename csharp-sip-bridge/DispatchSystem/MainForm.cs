using System.Media;
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
    private readonly JobHistoryPanel _jobHistory;
    private readonly DriverListPanel _driverList;
    private readonly LogPanel _logPanel;
    private readonly TabControl _jobTabs;

    // ── Controls ──
    private readonly Button _btnConnect;
    private readonly Button _btnDisconnect;
    private readonly Button _btnManualDispatch;
    private readonly Button _btnAddDriver;
    private readonly Button _btnRunDispatch;
    private readonly Button _btnSettings;
    private readonly CheckBox _chkAutoDispatch;
    private readonly Label _lblStatus;
    private readonly Label _lblStats;

    // ── Core ──
    private DispatchDb? _db;
    private MqttDispatchClient? _mqtt;
    private AutoDispatcher? _dispatcher;
    private WebhookListener? _webhook;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // ── Settings ──
    private int _webhookPort = 5080;
    private int _autoDispatchSec = 60;
    private bool _soundEnabled = true;

    public MainForm()
    {
        Text = "Ada Dispatch System v1.1";
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

        _btnConnect = MakeButton("🔌 Connect", Color.FromArgb(0, 120, 60));
        _btnConnect.Click += async (_, _) => await ConnectAsync();

        _btnDisconnect = MakeButton("⏹ Disconnect", Color.FromArgb(140, 40, 40));
        _btnDisconnect.Enabled = false;
        _btnDisconnect.Click += async (_, _) => await DisconnectAsync();

        _btnAddDriver = MakeButton("➕ Driver", Color.FromArgb(50, 80, 140));
        _btnAddDriver.Click += BtnAddDriver_Click;

        _btnManualDispatch = MakeButton("🎯 Dispatch", Color.FromArgb(120, 60, 10));
        _btnManualDispatch.Click += BtnManualDispatch_Click;

        _btnRunDispatch = MakeButton("⚡ Run Now", Color.FromArgb(80, 50, 130));
        _btnRunDispatch.Click += (_, _) => _dispatcher?.RunCycle();

        _btnSettings = MakeButton("⚙ Settings", Color.FromArgb(70, 70, 80));
        _btnSettings.Click += BtnSettings_Click;

        _chkAutoDispatch = new CheckBox
        {
            Text = "Auto",
            ForeColor = Color.White,
            Checked = true,
            AutoSize = true,
            Padding = new Padding(6, 8, 0, 0)
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
            Padding = new Padding(10, 8, 0, 0)
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
            _btnRunDispatch, _btnSettings, _chkAutoDispatch, _lblStatus, _lblStats
        });

        // ── Layout ──
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
            Orientation = Orientation.Vertical
        };

        // Left side: map on top, job tabs below
        var splitMapJobs = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 400,
            Panel1MinSize = 200,
            Panel2MinSize = 150
        };

        _map = new MapPanel { Dock = DockStyle.Fill };

        // Job tabs: Active + History
        _jobTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F)
        };

        _jobList = new JobListPanel { Dock = DockStyle.Fill };
        _jobHistory = new JobHistoryPanel { Dock = DockStyle.Fill };
        _jobHistory.OnFilterChanged += (from, to) =>
        {
            if (_db == null) return;
            var jobs = _db.GetJobHistory(from, to);
            _jobHistory.RefreshHistory(jobs);
        };

        var tabActive = new TabPage("📋 Active Jobs") { BackColor = Color.FromArgb(28, 28, 32) };
        tabActive.Controls.Add(_jobList);

        var tabHistory = new TabPage("🗂️ History") { BackColor = Color.FromArgb(28, 28, 32) };
        tabHistory.Controls.Add(_jobHistory);

        _jobTabs.TabPages.AddRange(new[] { tabActive, tabHistory });

        // Right side: drivers
        _driverList = new DriverListPanel { Dock = DockStyle.Fill };

        _logPanel = new LogPanel { Dock = DockStyle.Fill };

        // Context menu on job grid
        SetupJobContextMenu();

        splitMapJobs.Panel1.Controls.Add(_map);
        splitMapJobs.Panel2.Controls.Add(_jobTabs);

        splitTop.Panel1.Controls.Add(splitMapJobs);
        splitTop.Panel2.Controls.Add(_driverList);

        splitMain.Panel1.Controls.Add(splitTop);
        splitMain.Panel2.Controls.Add(_logPanel);

        Controls.Add(splitMain);
        Controls.Add(toolbar);

        // ── Refresh timer ──
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (_, _) => RefreshUI();

        Load += (_, _) =>
        {
            splitTop.Panel1MinSize = 300;
            splitTop.Panel2MinSize = 200;
            splitTop.SplitterDistance = Math.Max(splitTop.Panel1MinSize, Math.Min(700, splitTop.Width - splitTop.Panel2MinSize - 1));
        };

        InitDatabase();
    }

    // ── Context Menu ──

    private void SetupJobContextMenu()
    {
        var ctx = new ContextMenuStrip();
        ctx.BackColor = Color.FromArgb(40, 40, 50);
        ctx.ForeColor = Color.White;
        ctx.Font = new Font("Segoe UI", 9F);

        var miAllocate = new ToolStripMenuItem("🎯 Allocate Selected Driver");
        miAllocate.Click += (_, _) => BtnManualDispatch_Click(null, EventArgs.Empty);

        var miCancel = new ToolStripMenuItem("❌ Cancel Job");
        miCancel.Click += (_, _) =>
        {
            var jobId = _jobList.SelectedJobId;
            if (jobId == null || _db == null) return;
            if (MessageBox.Show($"Cancel job {jobId}?", "Cancel", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _db.UpdateJobStatus(jobId, JobStatus.Cancelled);
                _ = _map.RemoveJobMarker(jobId);
                _logPanel.AppendLog($"❌ Job {jobId} cancelled", Color.OrangeRed);
                RefreshUI();
            }
        };

        var miCopy = new ToolStripMenuItem("📋 Copy Details");
        miCopy.Click += (_, _) =>
        {
            var jobId = _jobList.SelectedJobId;
            if (jobId == null || _db == null) return;
            var jobs = _db.GetActiveJobs();
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null) return;
            var text = $"Job: {job.Id}\nPickup: {job.Pickup}\nDropoff: {job.Dropoff}\nPax: {job.Passengers}\nPhone: {job.CallerPhone}\nFare: {job.EstimatedFare:C}";
            Clipboard.SetText(text);
            _logPanel.AppendLog($"📋 Job {jobId} copied to clipboard", Color.LightBlue);
        };

        ctx.Items.AddRange(new ToolStripItem[] { miAllocate, miCancel, new ToolStripSeparator(), miCopy });
        _jobList.ContextMenuStrip = ctx;
    }

    // ── Init ──

    private void InitDatabase()
    {
        try
        {
            _db = new DispatchDb();
            _dispatcher = new AutoDispatcher(_db, _autoDispatchSec * 1000);
            _dispatcher.OnLog += msg => _logPanel.AppendLog(msg);
            _dispatcher.OnJobAllocated += OnJobAllocated;

            _webhook = new WebhookListener(_webhookPort);
            _webhook.OnLog += msg => BeginInvoke(() => _logPanel.AppendLog(msg, Color.MediumPurple));
            _webhook.OnJobReceived += job => BeginInvoke(() =>
            {
                OnBookingReceived(job);
                PlayNewJobSound();
            });
            _webhook.Start();

            _logPanel.AppendLog("💾 SQLite database ready", Color.Cyan);
            RefreshUI();
        }
        catch (Exception ex)
        {
            _logPanel.AppendLog($"❌ DB init failed: {ex.Message}", Color.Red);
        }
    }

    // ── Sound Alert ──

    private void PlayNewJobSound()
    {
        if (!_soundEnabled) return;
        try { SystemSounds.Exclamation.Play(); }
        catch { /* ignore if no audio device */ }
    }

    // ── MQTT Connect / Disconnect ──

    private async Task ConnectAsync()
    {
        try
        {
            _mqtt = new MqttDispatchClient();
            _mqtt.OnLog += msg => _logPanel.AppendLog(msg);
            _mqtt.OnDriverGps += OnDriverGps;
            _mqtt.OnBookingReceived += job =>
            {
                BeginInvoke(() =>
                {
                    OnBookingReceived(job);
                    PlayNewJobSound();
                });
            };
            _mqtt.OnJobStatusUpdate += OnJobStatusUpdate;
            _mqtt.OnDriverJobResponse += OnDriverJobResponse;

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

        // Marshal to UI thread — MQTT fires on background thread
        if (InvokeRequired)
        {
            BeginInvoke(() => OnDriverGps(driverId, lat, lng, status));
            return;
        }

        DriverStatus? ds = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<DriverStatus>(status, true, out var parsed))
            ds = parsed;

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

        var finalStatus = (ds ?? existing?.Status ?? DriverStatus.Online).ToString();
        var finalName = existing?.Name ?? driverId;
        _ = _map.UpdateDriverMarker(driverId, lat, lng, finalStatus, finalName);
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

    private void OnDriverJobResponse(string jobId, string driverId, bool accepted)
    {
        if (_db == null) return;

        if (accepted)
        {
            _db.UpdateJobStatus(jobId, JobStatus.Accepted, driverId);
            _logPanel.AppendLog($"✅ Driver {driverId} ACCEPTED job {jobId}", Color.LimeGreen);
        }
        else
        {
            // Driver rejected — put back to pending so auto-dispatch can try another
            _db.UpdateJobStatus(jobId, JobStatus.Pending);
            var drivers = _db.GetAllDrivers();
            var driver = drivers.FirstOrDefault(d => d.Id == driverId);
            if (driver != null)
            {
                driver.Status = DriverStatus.Online;
                _db.UpsertDriver(driver);
            }
            _logPanel.AppendLog($"⛔ Driver {driverId} REJECTED job {jobId} — reassigning", Color.Orange);
        }
        RefreshUI();
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

    private void BtnSettings_Click(object? sender, EventArgs e)
    {
        using var dlg = new DispatchSettingsDialog(_webhookPort, _autoDispatchSec, _soundEnabled);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _soundEnabled = dlg.SoundEnabled;

        if (dlg.WebhookPort != _webhookPort)
        {
            _webhookPort = dlg.WebhookPort;
            _webhook?.Dispose();
            _webhook = new WebhookListener(_webhookPort);
            _webhook.OnLog += msg => BeginInvoke(() => _logPanel.AppendLog(msg, Color.MediumPurple));
            _webhook.OnJobReceived += job => BeginInvoke(() =>
            {
                OnBookingReceived(job);
                PlayNewJobSound();
            });
            _webhook.Start();
        }

        if (dlg.AutoDispatchIntervalSec != _autoDispatchSec)
        {
            _autoDispatchSec = dlg.AutoDispatchIntervalSec;
            _logPanel.AppendLog($"⚙ Auto-dispatch interval: {_autoDispatchSec}s", Color.LightBlue);
            // Recreate dispatcher with new interval
            _dispatcher?.Dispose();
            _dispatcher = new AutoDispatcher(_db!, _autoDispatchSec * 1000);
            _dispatcher.OnLog += msg => _logPanel.AppendLog(msg);
            _dispatcher.OnJobAllocated += OnJobAllocated;
            _dispatcher.Enabled = _chkAutoDispatch.Checked;
        }

        _logPanel.AppendLog("⚙ Settings updated", Color.LightBlue);
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

        var (totalToday, completedToday, cancelledToday, _) = _db.GetTodayStats();
        var avgWait = jobs.Where(j => j.Status == JobStatus.Pending)
            .Select(j => (DateTime.UtcNow - j.CreatedAt).TotalMinutes)
            .DefaultIfEmpty(0)
            .Average();

        _lblStats.Text = $"Drivers: {online}↑ {onJob}🚕 | Pending: {pending} | Today: {totalToday} ({completedToday}✅ {cancelledToday}❌) | Avg wait: {avgWait:F0}m";
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
        Size = new Size(110, 34),
        Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        Margin = new Padding(0, 0, 4, 0)
    };
}
