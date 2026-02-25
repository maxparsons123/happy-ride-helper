using System.Text.Json;
using AdaCleanVersion.Config;
using AdaCleanVersion.Session;
using AdaCleanVersion.Sip;
using AdaSdkModel.Audio;
using AdaSdkModel.Avatar;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace AdaCleanVersion;

public partial class MainForm : Form
{
    private CleanAppSettings _settings;
    private bool _sipConnected;
    private bool _inCall;
    private bool _muted;

    private CleanSipBridge? _bridge;
    private ILoggerFactory? _loggerFactory;

    // Audio monitor (hear raw SIP audio locally)
    private WaveOutEvent? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;
    private readonly object _monitorLock = new();

    // Simli avatar + dedicated background feeder
    private SimliAvatar? _simliAvatar;
    private System.Threading.Channels.Channel<byte[]>? _simliChannel;
    private CancellationTokenSource? _simliCts;
    private Task? _simliFeederTask;

    public MainForm()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ApplySettingsToUi();
        InitSimliAvatar();
        Log("AdaCleanVersion v1.0 started. Configure SIP and click Connect.");
        Log($"📂 Settings loaded from: {SettingsPath}");
    }

    // ── Logger factory ──

    private ILoggerFactory GetLoggerFactory()
    {
        return _loggerFactory ??= LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new CallbackLoggerProvider(Log));
        });
    }

    // ══════════════════════════════════════
    //  SETTINGS PERSISTENCE
    // ══════════════════════════════════════

    private static string SettingsPath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    private static CleanAppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<CleanAppSettings>(json) ?? new CleanAppSettings();

                // Seed SipAccounts from inline Sip block if array is empty
                if (settings.SipAccounts.Count == 0 && !string.IsNullOrWhiteSpace(settings.Sip.Server)
                    && settings.Sip.Server != "sip.example.com")
                {
                    var acct = new SipAccount();
                    acct.FromSipSettings(settings.Sip, $"{settings.Sip.Username}@{settings.Sip.Server}");
                    settings.SipAccounts.Add(acct);
                    settings.SelectedSipAccountIndex = 0;
                }

                return settings;
            }
        }
        catch { }
        return new CleanAppSettings();
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

    private void ApplySettingsToUi()
    {
        RefreshAccountDropdown();
        ApplySipSettingsToFields(_settings.Sip);
    }

    private void ApplySipSettingsToFields(SipSettings sip)
    {
        txtSipServer.Text = sip.Server;
        txtSipPort.Text = sip.Port.ToString();
        txtSipUser.Text = sip.Username;
        txtAuthId.Text = sip.AuthUser ?? "";
        txtSipPassword.Text = sip.Password;
        txtDomain.Text = sip.Domain ?? "";
        txtDisplayName.Text = sip.DisplayName ?? "";
        chkAutoAnswer.Checked = sip.AutoAnswer;
        var idx = cmbTransport.Items.IndexOf(sip.Transport.ToUpperInvariant());
        cmbTransport.SelectedIndex = idx >= 0 ? idx : 0;
    }

    // ══════════════════════════════════════
    //  SIP ACCOUNT MANAGEMENT
    // ══════════════════════════════════════

    private void RefreshAccountDropdown()
    {
        cmbSipAccount.SelectedIndexChanged -= cmbSipAccount_SelectedIndexChanged;
        cmbSipAccount.Items.Clear();
        foreach (var acct in _settings.SipAccounts)
            cmbSipAccount.Items.Add(acct.ToString());
        if (_settings.SipAccounts.Count > 0 && _settings.SelectedSipAccountIndex >= 0
            && _settings.SelectedSipAccountIndex < _settings.SipAccounts.Count)
            cmbSipAccount.SelectedIndex = _settings.SelectedSipAccountIndex;
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;
    }

    private void cmbSipAccount_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var idx = cmbSipAccount.SelectedIndex;
        if (idx < 0 || idx >= _settings.SipAccounts.Count) return;

        var prevIdx = _settings.SelectedSipAccountIndex;
        if (prevIdx >= 0 && prevIdx < _settings.SipAccounts.Count && prevIdx != idx)
        {
            ReadSipFromUi();
            var prevLabel = _settings.SipAccounts[prevIdx].Label;
            _settings.SipAccounts[prevIdx].FromSipSettings(_settings.Sip, prevLabel);
        }

        var acct = _settings.SipAccounts[idx];
        _settings.Sip = acct.ToSipSettings();
        _settings.SelectedSipAccountIndex = idx;
        ApplySipSettingsToFields(_settings.Sip);
        SaveSettings();
        Log($"📞 Switched to account: {acct.Label}");
    }

    private void btnSaveAccount_Click(object? sender, EventArgs e)
    {
        ReadSipFromUi();
        var idx = cmbSipAccount.SelectedIndex;
        string label;
        if (idx >= 0 && idx < _settings.SipAccounts.Count)
        {
            label = _settings.SipAccounts[idx].Label;
            _settings.SipAccounts[idx].FromSipSettings(_settings.Sip, label);
            Log($"💾 Updated account: {label}");
        }
        else
        {
            label = $"{_settings.Sip.Username}@{_settings.Sip.Server}";
            var newAcct = new SipAccount();
            newAcct.FromSipSettings(_settings.Sip, label);
            _settings.SipAccounts.Add(newAcct);
            _settings.SelectedSipAccountIndex = _settings.SipAccounts.Count - 1;
            Log($"💾 Saved new account: {label}");
        }
        SaveSettings();
        RefreshAccountDropdown();
    }

    private void btnDeleteAccount_Click(object? sender, EventArgs e)
    {
        var idx = cmbSipAccount.SelectedIndex;
        if (idx < 0 || idx >= _settings.SipAccounts.Count)
        {
            MessageBox.Show("Select an account to delete.", "Delete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var acct = _settings.SipAccounts[idx];
        if (MessageBox.Show($"Delete account '{acct.Label}'?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _settings.SipAccounts.RemoveAt(idx);
        _settings.SelectedSipAccountIndex = Math.Min(idx, _settings.SipAccounts.Count - 1);
        SaveSettings();
        RefreshAccountDropdown();
        Log($"🗑 Deleted account: {acct.Label}");
    }

    private void btnNewAccount_Click(object? sender, EventArgs e)
    {
        ApplySipSettingsToFields(new SipSettings());
        cmbSipAccount.SelectedIndex = -1;
        Log("📞 New account — fill in details and click Save");
    }

    private void ReadSipFromUi()
    {
        _settings.Sip.Server = txtSipServer.Text.Trim();
        _settings.Sip.Port = int.TryParse(txtSipPort.Text, out var p) ? p : 5060;
        _settings.Sip.Username = txtSipUser.Text.Trim();
        _settings.Sip.AuthUser = string.IsNullOrWhiteSpace(txtAuthId.Text) ? null : txtAuthId.Text.Trim();
        _settings.Sip.Password = txtSipPassword.Text;
        _settings.Sip.Domain = string.IsNullOrWhiteSpace(txtDomain.Text) ? null : txtDomain.Text.Trim();
        _settings.Sip.DisplayName = string.IsNullOrWhiteSpace(txtDisplayName.Text) ? null : txtDisplayName.Text.Trim();
        _settings.Sip.Transport = cmbTransport.SelectedItem?.ToString() ?? "UDP";
        _settings.Sip.AutoAnswer = chkAutoAnswer.Checked;
    }

    private void SyncSelectedAccountFromSip()
    {
        var idx = _settings.SelectedSipAccountIndex;
        if (idx >= 0 && idx < _settings.SipAccounts.Count)
        {
            var label = _settings.SipAccounts[idx].Label;
            _settings.SipAccounts[idx].FromSipSettings(_settings.Sip, label);
        }
    }

    // ══════════════════════════════════════
    //  SIP CONNECTION
    // ══════════════════════════════════════

    private void btnConnect_Click(object? sender, EventArgs e)
    {
        ReadSipFromUi();
        SyncSelectedAccountFromSip();

        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || string.IsNullOrWhiteSpace(_settings.Sip.Username))
        {
            MessageBox.Show("SIP Server and Extension are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.OpenAi.ApiKey))
        {
            MessageBox.Show("OpenAI API Key is required. Go to File → Settings.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettings();
        Log($"📞 Connecting to {_settings.Sip.Server}:{_settings.Sip.Port} as {_settings.Sip.Username} ({_settings.Sip.Transport})…");
        SetSipConnected(true);

        try
        {
            var factory = GetLoggerFactory();
            var logger = factory.CreateLogger<CleanSipBridge>();

            _bridge = CleanBridgeFactory.Create(_settings, logger);

            _bridge.OnLog += msg => SafeInvoke(() => Log(msg));

            _bridge.OnCallConnected += (callId, rtpSession, session) => SafeInvoke(() =>
            {
                Log($"📞 Call active: {session.CallerId} [{callId}]");
                SetInCall(true);
                statusCallId.Text = $"{session.CallerId} [{callId}]";

                // Wire engine state updates to UI
                session.OnLog += msg => SafeInvoke(() => Log(msg));
                session.OnBookingReady += booking => SafeInvoke(() =>
                {
                    lblCallInfo.Text = $"{booking.CallerName} | {booking.Pickup.DisplayName} → {booking.Destination.DisplayName}";
                    lblCallInfo.ForeColor = Color.Cyan;
                    UpdateEngineDisplay(session);
                });
                session.OnFareReady += fare => SafeInvoke(() =>
                {
                    lblCallInfo.Text += $" | {fare.Fare}";
                    lblCallInfo.ForeColor = Color.LimeGreen;
                    UpdateEngineDisplay(session);
                });
                session.Engine.OnStateChanged += (from, to) => SafeInvoke(() =>
                {
                    lblEngineState.Text = $"State: {to}";
                    UpdateEngineDisplay(session);
                });

                UpdateEngineDisplay(session);
                StartAudioMonitor();

                // Connect Simli avatar on call start
                if (_simliAvatar?.IsConnected != true && _simliAvatar?.IsConnecting != true)
                    _ = ConnectSimliAsync();
            });

            var started = _bridge.Start();
            if (started)
            {
                lblSipStatus.Text = "● Registered";
                lblSipStatus.ForeColor = Color.LimeGreen;
                statusLabel.Text = "SIP Registered";
            }
            else
            {
                Log("❌ SIP start failed");
                SetSipConnected(false);
                _bridge = null;
            }
        }
        catch (Exception ex)
        {
            Log($"❌ SIP start failed: {ex.Message}");
            SetSipConnected(false);
            _bridge = null;
        }
    }

    private void btnDisconnect_Click(object? sender, EventArgs e)
    {
        Log("📞 Disconnecting SIP…");
        try
        {
            _bridge?.Stop();
            _bridge = null;
        }
        catch (Exception ex) { Log($"⚠ Disconnect error: {ex.Message}"); }
        SetSipConnected(false);
        SetInCall(false);
    }

    // ══════════════════════════════════════
    //  CALL CONTROLS
    // ══════════════════════════════════════

    private void btnHangUp_Click(object? sender, EventArgs e)
    {
        Log("📴 Hanging up all calls.");
        // CleanSipBridge doesn't expose HangupAll yet — just stop/restart
        try { _bridge?.Stop(); }
        catch (Exception ex) { Log($"⚠ Hangup error: {ex.Message}"); }
        SetInCall(false);
    }

    private void btnMute_Click(object? sender, EventArgs e)
    {
        _muted = !_muted;
        btnMute.Text = _muted ? "🔇 Unmute" : "🔊 Mute";
        btnMute.BackColor = _muted ? Color.FromArgb(180, 50, 50) : Color.FromArgb(80, 80, 85);
        if (_muted) { _monitorOut?.Pause(); Log("🔇 Audio muted"); }
        else { _monitorOut?.Play(); Log("🔊 Audio unmuted"); }
    }

    // ══════════════════════════════════════
    //  ENGINE STATE DISPLAY
    // ══════════════════════════════════════

    private void UpdateEngineDisplay(CleanCallSession session)
    {
        var engine = session.Engine;
        lblEngineState.Text = $"State: {engine.State}";

        var lines = new List<string>();
        lines.Add($"═══ Raw Slots ═══");
        foreach (var slot in engine.RawData.FilledSlots)
            lines.Add($"  {slot.Key}: {slot.Value}");

        var missing = engine.RawData.NextMissingSlot();
        if (missing != null)
            lines.Add($"  → Next: {missing}");

        if (engine.StructuredResult != null)
        {
            var b = engine.StructuredResult;
            lines.Add("");
            lines.Add($"═══ Extracted ═══");
            lines.Add($"  Name: {b.CallerName}");
            lines.Add($"  Pickup: {b.Pickup.DisplayName}");
            lines.Add($"  Dest: {b.Destination.DisplayName}");
            lines.Add($"  Pax: {b.Passengers}");
            lines.Add($"  Time: {b.PickupTime}");
        }

        if (engine.FareResult != null)
        {
            var f = engine.FareResult;
            lines.Add("");
            lines.Add($"═══ Fare ═══");
            lines.Add($"  Fare: {f.Fare}");
            lines.Add($"  Distance: {f.DistanceMiles:F1}mi");
            lines.Add($"  ETA: {f.DriverEta}");
            lines.Add($"  Zone: {f.ZoneName ?? "—"}");
        }

        txtEngineSlots.Text = string.Join("\n", lines);
    }

    // ══════════════════════════════════════
    //  UI STATE
    // ══════════════════════════════════════

    private void SetSipConnected(bool connected)
    {
        _sipConnected = connected;
        btnConnect.Enabled = !connected;
        btnDisconnect.Enabled = connected;
        SetSipFieldsEnabled(!connected);
        lblSipStatus.Text = connected ? "● Connecting…" : "● Disconnected";
        lblSipStatus.ForeColor = connected ? Color.Yellow : Color.Gray;
        statusLabel.Text = connected ? "Connecting…" : "Ready";
    }

    private void SetSipFieldsEnabled(bool enabled)
    {
        cmbSipAccount.Enabled = enabled;
        btnSaveAccount.Enabled = enabled;
        btnDeleteAccount.Enabled = enabled;
        btnNewAccount.Enabled = enabled;
        txtSipServer.Enabled = enabled;
        txtSipPort.Enabled = enabled;
        txtSipUser.Enabled = enabled;
        txtAuthId.Enabled = enabled;
        txtSipPassword.Enabled = enabled;
        txtDomain.Enabled = enabled;
        txtDisplayName.Enabled = enabled;
        cmbTransport.Enabled = enabled;
    }

    private void SetInCall(bool inCall)
    {
        _inCall = inCall;
        btnHangUp.Enabled = inCall;
        btnMute.Enabled = inCall;
        lblCallInfo.Text = inCall ? "Call in progress" : "No active call";
        lblCallInfo.ForeColor = inCall ? Color.LimeGreen : Color.Gray;
        if (!inCall) { _muted = false; btnMute.Text = "🔊 Mute"; btnMute.BackColor = Color.FromArgb(80, 80, 85); }
    }

    // ══════════════════════════════════════
    //  MENU HANDLERS
    // ══════════════════════════════════════

    private void mnuSettings_Click(object? sender, EventArgs e)
    {
        using var dlg = new ConfigForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dlg.Settings;
            SaveSettings();
            Log("⚙ Settings saved.");
        }
    }

    private void mnuViewConfig_Click(object? sender, EventArgs e)
    {
        if (File.Exists(SettingsPath))
            try { System.Diagnostics.Process.Start("notepad.exe", SettingsPath); }
            catch { MessageBox.Show($"Config at:\n{SettingsPath}", "Config Path"); }
        else
            MessageBox.Show("No config file yet.", "Config");
    }

    // ══════════════════════════════════════
    //  SIMLI AVATAR
    // ══════════════════════════════════════

    private void InitSimliAvatar()
    {
        try
        {
            var apiKey = _settings.Simli.ApiKey;
            var faceId = _settings.Simli.FaceId;

            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = _settings.Simli.ApiKey = "vlw7tr7vxhhs52bi3rum7";
            if (string.IsNullOrWhiteSpace(faceId))
                faceId = _settings.Simli.FaceId = "5fc23ea5-8175-4a82-aaaf-cdd8c88543dc";

            Log($"🎭 InitSimliAvatar: apiKey={apiKey[..Math.Min(6, apiKey.Length)]}..., faceId={faceId[..Math.Min(8, faceId.Length)]}...");

            var factory = GetLoggerFactory();
            _simliAvatar = new SimliAvatar(factory.CreateLogger<SimliAvatar>());
            _simliAvatar.Configure(apiKey, faceId);
            _simliAvatar.Dock = DockStyle.Fill;
            pnlAvatarHost.Controls.Clear();
            pnlAvatarHost.Controls.Add(_simliAvatar);
            lblAvatarStatus.Text = "Ready";
            Log("🎭 Simli avatar initialized successfully");
        }
        catch (Exception ex)
        {
            Log($"🎭 Simli init FAILED: {ex.Message}");
            lblAvatarStatus.Text = $"Init failed: {ex.Message}";
            _simliAvatar = null;
        }
    }

    private async Task ConnectSimliAsync()
    {
        if (!_settings.Simli.Enabled)
        {
            Log("🎭 Simli disabled — skipping avatar connection");
            return;
        }

        if (_simliAvatar == null)
        {
            Log("🎭 Simli was null at call start — retrying init...");
            InitSimliAvatar();
        }

        if (_simliAvatar == null)
        {
            Log("🎭 Simli still null after retry — skipping avatar");
            return;
        }

        try { await _simliAvatar.ConnectAsync(); }
        catch (Exception ex) { Log($"🎭 Simli connect error: {ex.Message}"); }

        StartSimliFeeder();
    }

    private async Task DisconnectSimliAsync()
    {
        StopSimliFeeder();
        if (_simliAvatar == null) return;
        try { await _simliAvatar.DisconnectAsync(); }
        catch (Exception ex) { Log($"🎭 Simli disconnect error: {ex.Message}"); }
    }

    private int _simliReconnectGuard;

    private async Task ReconnectSimliAsync()
    {
        if (Interlocked.CompareExchange(ref _simliReconnectGuard, 1, 0) != 0)
        {
            Log("🎭 Simli reconnect skipped — already in progress");
            return;
        }
        try
        {
            await DisconnectSimliAsync();
            await Task.Delay(800);
            await ConnectSimliAsync();
            Log("🎭 Simli reconnected — ready for next call");
        }
        catch (Exception ex) { Log($"🎭 Simli reconnect error: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _simliReconnectGuard, 0); }
    }

    private void StartSimliFeeder()
    {
        StopSimliFeeder();
        _simliChannel = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(120)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
        _simliCts = new CancellationTokenSource();
        var ct = _simliCts.Token;

        _simliFeederTask = Task.Factory.StartNew(async () =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                Log("🎭 Simli feeder thread started (BelowNormal priority)");

                while (!ct.IsCancellationRequested)
                {
                    var alawFrame = await _simliChannel.Reader.ReadAsync(ct);
                    if (_simliAvatar == null || (!_simliAvatar.IsConnected && !_simliAvatar.IsConnecting))
                        continue;

                    var pcm16at16k = AlawToSimliResampler.Convert(alawFrame);
                    await _simliAvatar.SendAudioAsync(pcm16at16k);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"🎭 Simli feeder error: {ex.Message}"); }
            finally { Log("🎭 Simli feeder thread stopped"); }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private void StopSimliFeeder()
    {
        try { _simliCts?.Cancel(); } catch { }
        try { _simliFeederTask?.Wait(500); } catch { }
        _simliCts?.Dispose();
        _simliCts = null;
        _simliFeederTask = null;
        _simliChannel = null;
    }

    private void ClearSimliBuffer()
    {
        if (_simliAvatar == null || !_simliAvatar.IsConnected) return;
        if (_simliChannel != null)
            while (_simliChannel.Reader.TryRead(out _)) { }
        _ = _simliAvatar.ClearBufferAsync();
    }

    // ══════════════════════════════════════
    //  AUDIO MONITOR
    // ══════════════════════════════════════

    private void StartAudioMonitor()
    {
        StopAudioMonitor();
        try
        {
            _monitorBuffer = new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true
            };
            _monitorOut = new WaveOutEvent { DesiredLatency = 100 };
            _monitorOut.Init(_monitorBuffer);
            if (!_muted) _monitorOut.Play();
            Log("🔊 Audio monitor started");
        }
        catch (Exception ex) { Log($"⚠ Audio monitor failed: {ex.Message}"); }
    }

    private void StopAudioMonitor()
    {
        try { _monitorOut?.Stop(); _monitorOut?.Dispose(); } catch { }
        _monitorOut = null;
        _monitorBuffer = null;
    }

    // ══════════════════════════════════════
    //  SAFE UI INVOKE
    // ══════════════════════════════════════

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // ══════════════════════════════════════
    //  LOGGING
    // ══════════════════════════════════════

    public void Log(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => Log(message)); } catch { } return; }

        txtLog.SelectionStart = txtLog.TextLength;
        txtLog.SelectionColor = message.Contains("❌") || message.Contains("Error") ? Color.Red
            : message.Contains("✅") || message.Contains("Booked") ? Color.LimeGreen
            : message.Contains("⚠") ? Color.Yellow
            : message.Contains("📞") || message.Contains("📲") ? Color.Cyan
            : Color.LightGreen;
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        txtLog.ScrollToCaret();

        if (txtLog.Lines.Length > 800)
        {
            txtLog.SelectionStart = 0;
            txtLog.SelectionLength = txtLog.GetFirstCharIndexFromLine(200);
            txtLog.SelectedText = "";
        }
    }

    // ══════════════════════════════════════
    //  FORM LIFECYCLE
    // ══════════════════════════════════════

    protected override void WndProc(ref Message m)
    {
        try { base.WndProc(ref m); }
        catch (System.IO.IOException) { }
        catch (ObjectDisposedException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopAudioMonitor();
        StopSimliFeeder();
        try
        {
            var avatar = _simliAvatar;
            _simliAvatar = null;
            if (avatar != null)
            {
                try { avatar.DisconnectAsync().Wait(3000); } catch { }
                try { avatar.Dispose(); } catch { }
            }
        }
        catch { }
        try { _bridge?.Dispose(); } catch { }
        _bridge = null;
        try { _loggerFactory?.Dispose(); } catch { }
        _loggerFactory = null;
        base.OnFormClosing(e);
    }
}

// ── Callback logger provider for routing ILogger to UI ──

internal sealed class CallbackLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _callback;
    public CallbackLoggerProvider(Action<string> callback) => _callback = callback;
    public ILogger CreateLogger(string categoryName) => new CallbackLogger(_callback);
    public void Dispose() { }
}

internal sealed class CallbackLogger : ILogger
{
    private readonly Action<string> _callback;
    public CallbackLogger(Action<string> callback) => _callback = callback;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        _callback(formatter(state, exception));
    }
}
