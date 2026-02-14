using System.Media;
using DispatchSystem.Data;
using DispatchSystem.Dispatch;
using DispatchSystem.Mqtt;
using DispatchSystem.Services;
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
    private readonly Button _btnToggleLog;
    private Form _logModal;
    private readonly CheckBox _chkAutoDispatch;
    private readonly CheckBox _chkBiddingMode;
    private readonly CheckBox _chkIcabbi;
    private readonly Label _lblStatus;
    private readonly Label _lblStats;

    // ── Core ──
    private DispatchDb? _db;
    private MqttDispatchClient? _mqtt;
    private AutoDispatcher? _dispatcher;
    private BiddingDispatcher? _bidding;
    private WebhookListener? _webhook;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // ── Settings ──
    private int _webhookPort = 5080;
    private int _autoDispatchSec = 60;
    private int _biddingWindowSec = 20;
    private double _bidRadiusKm = 10.0;
    private bool _soundEnabled = true;

    // ── iCabbi ──
    private IcabbiBookingService? _icabbi;
    private string _icabbiAppKey = "eb64fcbef547aa05336ee68b39a9f931ad3e225c";
    private string _icabbiSecretKey = "c7a01c8156bc9290bc408d206be0039def589504";
    private string _icabbiTenantBase = "https://yourtenant.icabbi.net";
    private bool _icabbiEnabled = false;

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

        _btnToggleLog = MakeButton("📋 Log", Color.FromArgb(60, 60, 90));
        _btnToggleLog.Click += (_, _) =>
        {
            if (_logModal.Visible)
                _logModal.Hide();
            else
            {
                _logModal.Location = new Point(Right - _logModal.Width - 20, Bottom - _logModal.Height - 40);
                _logModal.Show(this);
            }
        };

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

        _chkBiddingMode = new CheckBox
        {
            Text = "🔔 Bidding",
            ForeColor = Color.White,
            Checked = false,
            AutoSize = true,
            Padding = new Padding(6, 8, 0, 0)
        };
        _chkBiddingMode.CheckedChanged += (_, _) =>
        {
            if (_bidding != null) _bidding.Enabled = _chkBiddingMode.Checked;
            _logPanel.AppendLog($"🔔 Bidding mode {(_chkBiddingMode.Checked ? "ENABLED" : "DISABLED")}", Color.Gold);
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
            _btnRunDispatch, _btnSettings, _btnToggleLog, _chkAutoDispatch, _chkBiddingMode, _chkIcabbi, _lblStatus, _lblStats
        });

        // ── Layout ──
        // No more horizontal split for log — log is now a floating modal overlay
        var splitTop = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Color.FromArgb(28, 28, 32)
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

        // Right side: drivers (narrower)
        _driverList = new DriverListPanel { Dock = DockStyle.Fill };

        _logPanel = new LogPanel { Dock = DockStyle.Fill };

        // ── Floating Log Modal ──
        _logModal = new Form
        {
            Text = "📋 Dispatch Log",
            Size = new Size(700, 400),
            StartPosition = FormStartPosition.Manual,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            BackColor = Color.FromArgb(20, 20, 25),
            ForeColor = Color.White,
            TopMost = true,
            ShowInTaskbar = false
        };
        _logModal.Controls.Add(_logPanel);
        _logModal.FormClosing += (_, args) =>
        {
            // Hide instead of close so we can re-show
            args.Cancel = true;
            _logModal.Hide();
        };

        // Context menu on job grid
        SetupJobContextMenu();

        splitMapJobs.Panel1.Controls.Add(_map);
        splitMapJobs.Panel2.Controls.Add(_jobTabs);

        splitTop.Panel1.Controls.Add(splitMapJobs);
        splitTop.Panel2.Controls.Add(_driverList);

        Controls.Add(splitTop);
        Controls.Add(toolbar);

        // ── Refresh timer (stats-only, no driver/job list rebuild) ──
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        _refreshTimer.Tick += (_, _) => RefreshStats();

        Load += (_, _) =>
        {
            // Give map+jobs ~75% width, drivers ~25%
            splitTop.Panel1MinSize = 400;
            splitTop.Panel2MinSize = 160;
            splitTop.SplitterDistance = Math.Max(splitTop.Panel1MinSize, (int)(splitTop.Width * 0.78));

            // Position log modal at bottom-right of main form
            _logModal.Location = new Point(Right - _logModal.Width - 20, Bottom - _logModal.Height - 40);

            InitDatabase();
        };
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

            _bidding = new BiddingDispatcher(_db, _biddingWindowSec * 1000, _bidRadiusKm);
            _bidding.Enabled = _chkBiddingMode.Checked;
            _bidding.OnLog += msg =>
            {
                if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog(msg, Color.Gold));
                else _logPanel.AppendLog(msg, Color.Gold);
            };
            _bidding.OnBidRequestSent += (job, drivers) =>
            {
                if (_mqtt == null) return;
                var ids = drivers.Select(d => d.Id).ToList();
                _ = _mqtt.PublishBidRequest(job, ids);
            };
            _bidding.OnJobAllocated += (job, driver) =>
            {
                if (InvokeRequired) BeginInvoke(() => OnJobAllocated(job, driver));
                else OnJobAllocated(job, driver);
            };
            _bidding.OnBidLost += (job, losingDriverId) =>
            {
                if (_mqtt == null) return;
                _ = _mqtt.PublishBidResult(job.Id, losingDriverId, "lost", job);
                if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog($"📤 Bid lost → {losingDriverId} for {job.Id}", Color.DarkOrange));
                else _logPanel.AppendLog($"📤 Bid lost → {losingDriverId} for {job.Id}", Color.DarkOrange);
            };
            _bidding.OnNoBids += job =>
            {
                if (InvokeRequired) BeginInvoke(() =>
                {
                    _logPanel.AppendLog($"⚠ No bids for {job.Id} — falling back to auto-dispatch", Color.Orange);
                    RefreshUI();
                });
            };

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
            _mqtt.OnDriverBidReceived += (jobId, driverId, lat, lng) =>
            {
                _bidding?.RecordBid(jobId, driverId, lat, lng);
            };

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
        if (!string.IsNullOrEmpty(status))
            ds = MapMqttStatus(status);

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
        var finalReg = existing?.Registration ?? "";
        _ = _map.UpdateDriverMarker(driverId, lat, lng, finalStatus, finalName, finalReg);

        // Refresh driver list immediately so status changes are visible
        _driverList.RefreshDrivers(_db.GetAllDrivers());
    }

    private void OnBookingReceived(Job job)
    {
        if (_db == null) return;

        _db.InsertJob(job);
        _logPanel.AppendLog($"📥 New booking: {job.Pickup} → {job.Dropoff}", Color.Yellow);

        if (job.PickupLat != 0 && job.PickupLng != 0)
            _ = _map.AddJobMarker(job.Id, job.PickupLat, job.PickupLng, job.Pickup, job.CreatedAt);

        // Try bidding first if enabled
        if (_bidding != null && _bidding.Enabled)
        {
            var started = _bidding.StartBidding(job);
            if (started)
            {
                _logPanel.AppendLog($"🔔 Job {job.Id} entered bidding mode ({_biddingWindowSec}s)", Color.Gold);
                RefreshUI();
                return;
            }
            _logPanel.AppendLog($"⚠ No nearby drivers for bidding — job stays pending for auto-dispatch", Color.Orange);
        }

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

        // Send WhatsApp tracking link to passenger
        if (_mqtt != null && !string.IsNullOrEmpty(job.CallerPhone))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var trackingUrl = $"https://coherent-civil-imp.ngrok.app/track?driver={driver.Id}&job={job.Id}&plat={job.PickupLat}&plon={job.PickupLng}";
                    var waPayload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        phone = job.CallerPhone,
                        driverName = driver.Name,
                        driverId = driver.Id,
                        jobId = job.Id,
                        trackingUrl,
                        pickup = job.Pickup,
                        dropoff = job.Dropoff,
                        eta = job.DriverEtaMinutes
                    });
                    await _mqtt.PublishAsync("dispatch/whatsapp", waPayload);
                    BeginInvoke(() => _logPanel.AppendLog($"📱 WhatsApp tracking sent for {job.Id} → {job.CallerPhone}", Color.LimeGreen));
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => _logPanel.AppendLog($"⚠ WhatsApp send failed: {ex.Message}", Color.Orange));
                }
            });
        }

        // Fire-and-forget iCabbi booking if enabled
        if (_icabbiEnabled && _icabbi != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _icabbi.CreateAndDispatchAsync(job);
                    BeginInvoke(() =>
                    {
                        if (result.Success)
                            _logPanel.AppendLog($"🚕 iCabbi OK — Journey: {result.JourneyId} | Track: {result.TrackingUrl}", Color.MediumPurple);
                        else
                            _logPanel.AppendLog($"⚠ iCabbi failed: {result.Message}", Color.Orange);
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke(() => _logPanel.AppendLog($"❌ iCabbi error: {ex.Message}", Color.Red));
                }
            });
        }

        // Marshal to UI thread — this handler fires from background dispatch threads
        if (InvokeRequired)
        {
            BeginInvoke(async () =>
            {
                if (job.PickupLat != 0)
                    await _map.DrawAllocationLine(job.Id, driver.Lat, driver.Lng, job.PickupLat, job.PickupLng);
                RefreshUI();
            });
            return;
        }

        if (job.PickupLat != 0)
            await _map.DrawAllocationLine(job.Id, driver.Lat, driver.Lng, job.PickupLat, job.PickupLng);

        RefreshUI();
    }

    private void EnsureIcabbiClient()
    {
        _icabbi?.Dispose();
        _icabbi = new IcabbiBookingService(_icabbiAppKey, _icabbiSecretKey, tenantBase: _icabbiTenantBase);
        _icabbi.OnLog += msg =>
        {
            if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog(msg, Color.MediumPurple));
            else _logPanel.AppendLog(msg, Color.MediumPurple);
        };
        _icabbi.OnStatusChanged += (journeyId, oldStatus, newStatus) =>
        {
            if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog($"🔄 iCabbi {journeyId}: {oldStatus} → {newStatus}", Color.MediumPurple));
            else _logPanel.AppendLog($"🔄 iCabbi {journeyId}: {oldStatus} → {newStatus}", Color.MediumPurple);
        };
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

        if (driver.Status != DriverStatus.Online)
        {
            MessageBox.Show($"Driver {driver.Name} is {driver.Status} — only Online drivers can be dispatched.",
                "Driver Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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
        using var dlg = new DispatchSettingsDialog(
            _webhookPort, _autoDispatchSec, _soundEnabled, _biddingWindowSec, _bidRadiusKm,
            _icabbiAppKey, _icabbiSecretKey, _icabbiTenantBase);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _soundEnabled = dlg.SoundEnabled;

        // iCabbi keys
        if (dlg.IcabbiAppKey != _icabbiAppKey || dlg.IcabbiSecretKey != _icabbiSecretKey || dlg.IcabbiTenantBase != _icabbiTenantBase)
        {
            _icabbiAppKey = dlg.IcabbiAppKey;
            _icabbiSecretKey = dlg.IcabbiSecretKey;
            _icabbiTenantBase = dlg.IcabbiTenantBase;
            if (_icabbiEnabled) EnsureIcabbiClient();
            _logPanel.AppendLog("⚙ iCabbi API keys updated", Color.MediumPurple);
        }

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
            _dispatcher?.Dispose();
            _dispatcher = new AutoDispatcher(_db!, _autoDispatchSec * 1000);
            _dispatcher.OnLog += msg => _logPanel.AppendLog(msg);
            _dispatcher.OnJobAllocated += OnJobAllocated;
            _dispatcher.Enabled = _chkAutoDispatch.Checked;
        }

        if (dlg.BiddingWindowSec != _biddingWindowSec || dlg.BidRadiusKm != _bidRadiusKm)
        {
            _biddingWindowSec = dlg.BiddingWindowSec;
            _bidRadiusKm = dlg.BidRadiusKm;
            _bidding?.Dispose();
            _bidding = new BiddingDispatcher(_db!, _biddingWindowSec * 1000, _bidRadiusKm);
            _bidding.Enabled = _chkBiddingMode.Checked;
            _bidding.OnLog += msg =>
            {
                if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog(msg, Color.Gold));
                else _logPanel.AppendLog(msg, Color.Gold);
            };
            _bidding.OnBidRequestSent += (job, drivers) =>
            {
                if (_mqtt == null) return;
                _ = _mqtt.PublishBidRequest(job, drivers.Select(d => d.Id).ToList());
            };
            _bidding.OnJobAllocated += (job, driver) =>
            {
                if (InvokeRequired) BeginInvoke(() => OnJobAllocated(job, driver));
                else OnJobAllocated(job, driver);
            };
            _bidding.OnBidLost += (job, losingDriverId) =>
            {
                if (_mqtt == null) return;
                _ = _mqtt.PublishBidResult(job.Id, losingDriverId, "lost", job);
                if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog($"📤 Bid lost → {losingDriverId} for {job.Id}", Color.DarkOrange));
                else _logPanel.AppendLog($"📤 Bid lost → {losingDriverId} for {job.Id}", Color.DarkOrange);
            };
            _bidding.OnNoBids += job =>
            {
                if (InvokeRequired) BeginInvoke(() => _logPanel.AppendLog($"⚠ No bids for {job.Id}", Color.Orange));
            };
            _logPanel.AppendLog($"⚙ Bidding: {_biddingWindowSec}s window, {_bidRadiusKm}km radius", Color.LightBlue);
        }

        _logPanel.AppendLog("⚙ Settings updated", Color.LightBlue);
    }

    // ── Refresh ──

    /// <summary>Full UI refresh — call only when MQTT data changes (bookings, GPS, status).</summary>
    private void RefreshUI()
    {
        if (_db == null) return;
        if (InvokeRequired) { BeginInvoke(RefreshUI); return; }

        var drivers = _db.GetAllDrivers();
        var jobs = _db.GetActiveJobs();

        _driverList.RefreshDrivers(drivers);
        _jobList.RefreshJobs(jobs);

        RefreshStatsFromData(drivers, jobs);
    }

    /// <summary>Lightweight stats-only refresh for the periodic timer (no list rebuild).</summary>
    private void RefreshStats()
    {
        if (_db == null) return;
        if (InvokeRequired) { BeginInvoke(RefreshStats); return; }

        var drivers = _db.GetAllDrivers();
        var jobs = _db.GetActiveJobs();
        RefreshStatsFromData(drivers, jobs);
    }

    private void RefreshStatsFromData(List<Driver> drivers, List<Job> jobs)
    {
        var online = drivers.Count(d => d.Status == DriverStatus.Online);
        var onJob = drivers.Count(d => d.Status == DriverStatus.OnJob);
        var pending = jobs.Count(j => j.Status == JobStatus.Pending);
        var bidding = jobs.Count(j => j.Status == JobStatus.Bidding);

        var (totalToday, completedToday, cancelledToday, _) = _db.GetTodayStats();
        var avgWait = jobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Bidding)
            .Select(j => (DateTime.UtcNow - j.CreatedAt).TotalMinutes)
            .DefaultIfEmpty(0)
            .Average();

        var biddingText = bidding > 0 ? $" Bidding: {bidding}🔔 |" : "";
        _lblStats.Text = $"Drivers: {online}↑ {onJob}🚕 | Pending: {pending} |{biddingText} Today: {totalToday} ({completedToday}✅ {cancelledToday}❌) | Avg wait: {avgWait:F0}m";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _webhook?.Dispose();
        _dispatcher?.Dispose();
        _bidding?.Dispose();
        _icabbi?.Dispose();
        _mqtt?.Dispose();
        _db?.Dispose();

        // Force-close log modal (bypass the cancel in FormClosing)
        if (_logModal != null)
        {
            _logModal.FormClosing -= null!;
            _logModal.Dispose();
        }

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

    /// <summary>
    /// Maps MQTT driver status strings (available, busy, offline, break) to the internal enum.
    /// </summary>
    private static DriverStatus? MapMqttStatus(string mqttStatus) =>
        mqttStatus.ToLowerInvariant() switch
        {
            "available" or "online" or "free" => DriverStatus.Online,
            "busy" or "onjob" or "on_job" => DriverStatus.OnJob,
            "break" or "on_break" => DriverStatus.Break,
            "offline" or "off" => DriverStatus.Offline,
            _ => Enum.TryParse<DriverStatus>(mqttStatus, true, out var p) ? p : null
        };
}