using System.Text.Json;
using AdaMain.Ai;
using AdaMain.Audio;
using AdaMain.Avatar;
using AdaMain.Config;
using AdaMain.Core;
using AdaMain.Services;
using AdaMain.Sip;

using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace AdaMain;

public partial class MainForm : Form
{
    private AppSettings _settings;
    private bool _sipConnected;
    private bool _inCall;
    private bool _muted;
    private bool _operatorMode;
    private volatile bool _monitorAdaEnabled = true;
    private volatile bool _monitorCallerEnabled = true;
    private bool _pttActive;
    private float _operatorMicGain = 2.0f; // Default 2x boost for operator mic output

    private SipServer? _sipServer;
    private ILoggerFactory? _loggerFactory;
    private ICallSession? _currentSession;

    // Audio monitor (hear raw SIP audio locally)
    private WaveOutEvent? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;

    // Operator microphone (send local mic audio to SIP)
    private WaveInEvent? _micInput;
    private readonly object _micLock = new();

    // Simli avatar
    private SimliAvatar? _simliAvatar;
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _simliQueue = new(200);
    private Thread? _simliThread;

    public MainForm()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ApplySettingsToUi();
        InitSimliAvatar();
        Log("AdaMain v1.0 started. Configure SIP and click Connect.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Log("🛑 Shutting down…");

        // Stop operator mic
        StopMicrophone();

        // Stop local audio monitor
        StopAudioMonitor();

        // End active call session
        try { (_currentSession as IDisposable)?.Dispose(); }
        catch { }
        _currentSession = null;

        // Disconnect and dispose Simli avatar
        try
        {
            if (_simliAvatar != null)
            {
                _simliAvatar.DisconnectAsync().GetAwaiter().GetResult();
                _simliAvatar.Dispose();
                _simliAvatar = null;
            }
        }
        catch { }

        // Shut down SIP server
        if (_sipServer != null)
        {
            try { _sipServer.StopAsync().GetAwaiter().GetResult(); }
            catch { }
            _sipServer = null;
        }

        // Dispose logger factory
        _loggerFactory?.Dispose();
        _loggerFactory = null;

        base.OnFormClosing(e);
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

    // ── Settings persistence ──

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdaMain", "appsettings.json");

    private static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                
                // Restore defaults for critical fields that may be empty in older config files
                var defaults = new DispatchSettings();
                if (string.IsNullOrWhiteSpace(settings.Dispatch.BsqdWebhookUrl))
                    settings.Dispatch.BsqdWebhookUrl = defaults.BsqdWebhookUrl;
                if (string.IsNullOrWhiteSpace(settings.Dispatch.BsqdApiKey))
                    settings.Dispatch.BsqdApiKey = defaults.BsqdApiKey;
                if (string.IsNullOrWhiteSpace(settings.Dispatch.WhatsAppWebhookUrl))
                    settings.Dispatch.WhatsAppWebhookUrl = defaults.WhatsAppWebhookUrl;
                
                return settings;
            }
        }
        catch { /* fall through */ }
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

    private void ApplySettingsToUi()
    {
        // Populate account dropdown
        RefreshAccountDropdown();
        
        // Apply current SIP settings to fields
        ApplySipSettingsToFields(_settings.Sip);
    }
    
    private void ApplySipSettingsToFields(SipSettings sip)
    {
        txtSipServer.Text = sip.Server;
        txtSipPort.Text = sip.Port.ToString();
        txtSipUser.Text = sip.Username;
        txtAuthId.Text = sip.AuthId ?? "";
        txtSipPassword.Text = sip.Password;
        txtDomain.Text = sip.Domain ?? "";
        txtDisplayName.Text = sip.DisplayName ?? "";
        chkAutoAnswer.Checked = sip.AutoAnswer;

        var idx = cmbTransport.Items.IndexOf(sip.Transport.ToUpperInvariant());
        cmbTransport.SelectedIndex = idx >= 0 ? idx : 0;
    }
    
    private void RefreshAccountDropdown()
    {
        cmbSipAccount.SelectedIndexChanged -= cmbSipAccount_SelectedIndexChanged;
        cmbSipAccount.Items.Clear();
        
        foreach (var acct in _settings.SipAccounts)
            cmbSipAccount.Items.Add(acct.ToString());
        
        if (_settings.SipAccounts.Count > 0 && _settings.SelectedSipAccountIndex >= 0 
            && _settings.SelectedSipAccountIndex < _settings.SipAccounts.Count)
        {
            cmbSipAccount.SelectedIndex = _settings.SelectedSipAccountIndex;
        }
        
        cmbSipAccount.SelectedIndexChanged += cmbSipAccount_SelectedIndexChanged;
    }
    
    private void cmbSipAccount_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var idx = cmbSipAccount.SelectedIndex;
        if (idx < 0 || idx >= _settings.SipAccounts.Count) return;
        
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
            // Update existing
            label = _settings.SipAccounts[idx].Label;
            _settings.SipAccounts[idx].FromSipSettings(_settings.Sip, label);
            Log($"💾 Updated account: {label}");
        }
        else
        {
            // Create new from current fields
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
        if (MessageBox.Show($"Delete account '{acct.Label}'?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        
        _settings.SipAccounts.RemoveAt(idx);
        _settings.SelectedSipAccountIndex = Math.Min(idx, _settings.SipAccounts.Count - 1);
        SaveSettings();
        RefreshAccountDropdown();
        Log($"🗑 Deleted account: {acct.Label}");
    }
    
    private void btnNewAccount_Click(object? sender, EventArgs e)
    {
        // Clear fields for a new account
        var blank = new SipSettings();
        ApplySipSettingsToFields(blank);
        cmbSipAccount.SelectedIndex = -1;
        Log("📞 New account — fill in details and click Save");
    }

    private void ReadSipFromUi()
    {
        _settings.Sip.Server = txtSipServer.Text.Trim();
        _settings.Sip.Port = int.TryParse(txtSipPort.Text, out var p) ? p : 5060;
        _settings.Sip.Username = txtSipUser.Text.Trim();
        _settings.Sip.AuthId = string.IsNullOrWhiteSpace(txtAuthId.Text) ? null : txtAuthId.Text.Trim();
        _settings.Sip.Password = txtSipPassword.Text;
        _settings.Sip.Domain = string.IsNullOrWhiteSpace(txtDomain.Text) ? null : txtDomain.Text.Trim();
        _settings.Sip.DisplayName = string.IsNullOrWhiteSpace(txtDisplayName.Text) ? null : txtDisplayName.Text.Trim();
        _settings.Sip.Transport = cmbTransport.SelectedItem?.ToString() ?? "UDP";
        _settings.Sip.AutoAnswer = chkAutoAnswer.Checked;
    }

    // ── SIP connection ──

    private async void btnConnect_Click(object? sender, EventArgs e)
    {
        ReadSipFromUi();

        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || string.IsNullOrWhiteSpace(_settings.Sip.Username))
        {
            MessageBox.Show("SIP Server and Extension are required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.OpenAi.ApiKey))
        {
            MessageBox.Show("OpenAI API Key is required. Go to File → Settings to configure.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettings();
        Log($"📞 Connecting to {_settings.Sip.Server}:{_settings.Sip.Port} as {_settings.Sip.Username} ({_settings.Sip.Transport})…");

        SetSipConnected(true);  // Disable fields immediately

        try
        {
            var factory = GetLoggerFactory();
            var sipLogger = factory.CreateLogger<SipServer>();
            var smLogger = factory.CreateLogger<SessionManager>();

            // Create session factory with full pipeline wiring
            var sessionManager = new SessionManager(smLogger, CreateCallSession);

            _sipServer = new SipServer(sipLogger, _settings.Sip, _settings.Audio, sessionManager);
            _sipServer.OperatorMode = _operatorMode; // Sync operator mode at startup

            // Wire SipServer events → MainForm
            _sipServer.OnLog += msg => Invoke(() => Log(msg));

            _sipServer.OnServerResolved += ip => Invoke(() =>
            {
                txtSipServer.Text = ip;
                Log($"📡 Server resolved → {ip}");
            });

            _sipServer.OnRegistered += msg => Invoke(() =>
            {
                Log($"✅ SIP Registered: {msg}");
                lblSipStatus.Text = "● Registered";
                lblSipStatus.ForeColor = Color.LimeGreen;
                statusLabel.Text = "SIP Registered";
            });

            _sipServer.OnRegistrationFailed += msg => Invoke(() =>
            {
                Log($"❌ Registration failed: {msg}");
                lblSipStatus.Text = "● Reg Failed";
                lblSipStatus.ForeColor = Color.OrangeRed;
                statusLabel.Text = "Registration Failed";
            });

            _sipServer.OnCallStarted += (sessionId, callerId) => Invoke(() =>
            {
                OnCallAnswered(callerId, sessionId);
                SetInCall(true);
                statusCallId.Text = $"{callerId} [{sessionId}]";
                _ = ConnectSimliAsync();
            });

            _sipServer.OnCallRinging += (pendingId, callerId) => Invoke(() =>
            {
                Log($"📲 [{pendingId}] Call from {callerId} — RINGING (click Answer)");
                statusCallId.Text = $"📞 {callerId} (ringing)";
                lblCallInfo.Text = $"Ringing: {callerId}";
                lblCallInfo.ForeColor = Color.Orange;
                btnAnswer.Enabled = true;
                btnReject.Enabled = true;
            });

            _sipServer.OnOperatorCallerAudio += alawFrame =>
            {
                // Feed caller audio to local speakers (operator mode)
                var copy = new byte[alawFrame.Length];
                Buffer.BlockCopy(alawFrame, 0, copy, 0, alawFrame.Length);
                _monitorBuffer?.AddSamples(copy, 0, copy.Length);
            };

            // Feed caller audio to monitor speakers during AI calls
            _sipServer.OnCallerAudioMonitor += alawFrame =>
            {
                if (!_monitorCallerEnabled) return;
                var copy = new byte[alawFrame.Length];
                Buffer.BlockCopy(alawFrame, 0, copy, 0, alawFrame.Length);
                ThreadPool.QueueUserWorkItem(_ => _monitorBuffer?.AddSamples(copy, 0, copy.Length));
            };

            _sipServer.OnCallEnded += (sessionId, reason) => Invoke(() =>
            {
                Log($"📴 Call {sessionId} ended: {reason}");
                
                // Always clear reference if the ended session matches current
                if (_currentSession?.SessionId == sessionId)
                {
                    _currentSession = null;
                    Log($"🧹 Cleared _currentSession reference for {sessionId}");
                }
                
                // Clear UI if no more active calls
                if (_sipServer?.ActiveCallCount == 0)
                {
                    SetInCall(false);
                    statusCallId.Text = "";
                    lblCallInfo.Text = "No active call";
                    lblCallInfo.ForeColor = Color.Gray;
                    StopAudioMonitor();
                    _ = DisconnectSimliAsync();
                }
            });

            _sipServer.OnActiveCallCountChanged += count => Invoke(() =>
            {
                Log($"📊 Active calls: {count}");
            });

            await _sipServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ SIP start failed: {ex.Message}");
            SetSipConnected(false);
            _sipServer = null;
        }
    }

    /// <summary>Factory method to create fully-wired CallSession.</summary>
    private ICallSession CreateCallSession(string sessionId, string callerId)
    {
        var factory = GetLoggerFactory();
        
        // Create AI client (G.711 passthrough mode)
        var aiClient = new OpenAiG711Client(
            factory.CreateLogger<OpenAiG711Client>(),
            _settings.OpenAi);
        
        // Create fare calculator
        var fareCalculator = new FareCalculator(
            factory.CreateLogger<FareCalculator>(),
            _settings.GoogleMaps,
            _settings.Supabase);
        
        // Create dispatcher
        var dispatcher = new BsqdDispatcher(
            factory.CreateLogger<BsqdDispatcher>(),
            _settings.Dispatch);
        
        // Optional iCabbi
        IcabbiBookingService? icabbi = null;
        var icabbiEnabled = _settings.Icabbi.Enabled;
        Log($"ℹ [iCabbi] enabled={icabbiEnabled}, appKey={(_settings.Icabbi.AppKey.Length > 0 ? _settings.Icabbi.AppKey[..Math.Min(6, _settings.Icabbi.AppKey.Length)] + "…" : "(empty)")}, secretKey={((_settings.Icabbi.SecretKey?.Length ?? 0) > 0 ? "set" : "(empty)")}");
        if (icabbiEnabled && !string.IsNullOrEmpty(_settings.Icabbi.AppKey) && !string.IsNullOrEmpty(_settings.Icabbi.SecretKey))
        {
            icabbi = new IcabbiBookingService(_settings.Icabbi.AppKey, _settings.Icabbi.SecretKey, tenantBase: _settings.Icabbi.TenantBase);
            icabbi.OnLog += msg => Invoke(() => Log($"🚕 {msg}"));
            Log("✅ iCabbi service created for voice call path");
        }
        else if (icabbiEnabled)
        {
            Log("⚠ iCabbi enabled but AppKey or SecretKey is empty — skipping");
        }
        
        // Create session
        var session = new CallSession(
            sessionId,
            callerId,
            factory.CreateLogger<CallSession>(),
            _settings,
            aiClient,
            fareCalculator,
            dispatcher,
            icabbi,
            icabbiEnabled);
        
        // Wire session events → UI
        session.OnTranscript += (role, text) => Invoke(() =>
        {
            Log($"💬 {role}: {text}");
        });
        
        // Wire Ada's audio output → Simli avatar OR monitor speakers
        // IMPORTANT: Dispatch monitor playback off the RTP-critical thread to prevent jitter
        session.OnAudioOut += alawFrame =>
        {
            if (_simliAvatar?.IsConnected == true)
            {
                FeedSimliAudio(alawFrame);
            }
            else if (_monitorAdaEnabled)
            {
                var copy = new byte[alawFrame.Length];
                Buffer.BlockCopy(alawFrame, 0, copy, 0, alawFrame.Length);
                ThreadPool.QueueUserWorkItem(_ => _monitorBuffer?.AddSamples(copy, 0, copy.Length));
            }
        };
        
        // Wire barge-in → clear Simli buffer
        session.OnBargeIn += () => ClearSimliBuffer();
        
        session.OnBookingUpdated += booking => Invoke(() =>
        {
            var info = new List<string>();
            if (!string.IsNullOrEmpty(booking.Name)) info.Add($"Name: {booking.Name}");
            if (!string.IsNullOrEmpty(booking.Pickup)) info.Add($"From: {booking.Pickup}");
            if (!string.IsNullOrEmpty(booking.Destination)) info.Add($"To: {booking.Destination}");
            if (!string.IsNullOrEmpty(booking.Fare)) info.Add($"Fare: {booking.Fare}");
            if (booking.Confirmed) info.Add($"REF: {booking.BookingRef}");
            
            lblCallInfo.Text = info.Count > 0 ? string.Join(" | ", info) : "Call in progress";
        });
        
        _currentSession = session;
        return session;
    }

    private async void btnDisconnect_Click(object? sender, EventArgs e)
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
        SetInCall(false);
    }

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

    // ── Call controls ──

    private async void btnAnswer_Click(object? sender, EventArgs e)
    {
        if (_operatorMode && _sipServer != null)
        {
            // Capture caller info before answering (from pending call)
            var callerPhone = statusCallId.Text;
            // Extract just the phone from "📞 +447539025332 (ringing)"
            var phoneMatch = System.Text.RegularExpressions.Regex.Match(callerPhone, @"[\+\d]{6,}");
            var phone = phoneMatch.Success ? phoneMatch.Value : null;

            Log("✅ Answering call in operator mode…");
            var answered = await _sipServer.AnswerOperatorCallAsync();
            if (answered)
            {
                SetInCall(true);
                StartAudioMonitor();
                StartMicrophone(); // Always-hot mic for operator
                Log("🎤 Operator mic active — speak normally");

                // Auto-open booking form for this caller
                _ = Task.Run(() => Invoke(() => OpenBookingForm(phone, null)));
            }
        }
        else
        {
            Log("✅ Answering incoming call…");
            SetInCall(true);
        }
    }

    private void btnReject_Click(object? sender, EventArgs e)
    {
        if (_operatorMode && _sipServer != null)
        {
            _sipServer.RejectPendingCall();
        }
        Log("❌ Rejecting incoming call.");
        SetInCall(false);
        lblCallInfo.Text = "No active call";
        lblCallInfo.ForeColor = Color.Gray;
    }

    private async void btnHangUp_Click(object? sender, EventArgs e)
    {
        Log("📴 Hanging up all calls.");
        if (_sipServer != null)
        {
            try { await _sipServer.HangupAllAsync("operator_hangup"); }
            catch (Exception ex) { Log($"⚠ Hangup error: {ex.Message}"); }
        }
        SetInCall(false);
    }

    private void chkManualMode_CheckedChanged(object? sender, EventArgs e)
    {
        _operatorMode = chkManualMode.Checked;

        // Sync to SipServer
        if (_sipServer != null)
            _sipServer.OperatorMode = _operatorMode;

        // In operator mode, PTT is not needed — mic is always hot when call is active
        btnPtt.Visible = !_operatorMode && false; // Hide PTT entirely in operator mode
        btnPtt.Enabled = false;

        if (_operatorMode)
        {
            Log("🎤 Operator mode ON – calls will ring and wait for you to answer");
            Log("    Mic will be always active during calls (no PTT needed)");
        }
        else
        {
            Log("🤖 Auto mode – AI will respond to calls");
            StopMicrophone();
        }
    }

    private void btnMute_Click(object? sender, EventArgs e)
    {
        _muted = !_muted;
        btnMute.Text = _muted ? "🔇 Unmute" : "🔊 Mute";
        btnMute.BackColor = _muted ? Color.FromArgb(180, 50, 50) : Color.FromArgb(80, 80, 85);

        if (_muted)
        {
            _monitorOut?.Pause();
            Log("🔇 Audio monitor muted");
        }
        else
        {
            _monitorOut?.Play();
            Log("🔊 Audio monitor unmuted");
        }
    }

    private void btnPtt_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!_inCall || !_operatorMode) return;
        _pttActive = true;
        btnPtt.BackColor = Color.FromArgb(200, 50, 50);
        btnPtt.Text = "🔴 Speaking…";
        StartMicrophone();
        Log("🎙 PTT active – speaking to caller");
    }

    private void btnPtt_MouseUp(object? sender, MouseEventArgs e)
    {
        _pttActive = false;
        btnPtt.BackColor = Color.FromArgb(156, 39, 176);
        btnPtt.Text = "🎙 Push-to-Talk";
        StopMicrophone();
        Log("🎙 PTT released");
    }

    public void SetInCall(bool inCall)
    {
        _inCall = inCall;
        btnAnswer.Enabled = !inCall && _sipConnected;
        btnReject.Enabled = !inCall && _sipConnected;
        btnHangUp.Enabled = inCall;
        btnMute.Enabled = inCall;
        btnPtt.Enabled = inCall && _operatorMode;
        lblCallInfo.Text = inCall ? "Call in progress" : "No active call";
        lblCallInfo.ForeColor = inCall ? Color.LimeGreen : Color.Gray;

        if (!inCall)
        {
            _muted = false;
            btnMute.Text = "🔊 Mute";
            btnMute.BackColor = Color.FromArgb(80, 80, 85);
            StopMicrophone();
        }
    }

    public void OnCallAnswered(string callerId, string sessionId)
    {
        if (InvokeRequired) { Invoke(() => OnCallAnswered(callerId, sessionId)); return; }

        Log($"📞 Call active: {callerId} [{sessionId}]");
        statusCallId.Text = $"{callerId} [{sessionId}]";
        StartAudioMonitor();
    }

    // ── Menu handlers ──

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

    private void mnuAudioTest_Click(object? sender, EventArgs e)
    {
        Log("🎤 Audio test – not yet implemented.");
        MessageBox.Show("Audio test coming soon.", "Audio Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void mnuNewBooking_Click(object? sender, EventArgs e)
    {
        OpenBookingForm(null, null);
    }

    private void OpenBookingForm(string? callerPhone, string? callerName)
    {
        var factory = GetLoggerFactory();
        var fareCalc = new FareCalculator(factory.CreateLogger<FareCalculator>(), _settings.GoogleMaps, _settings.Supabase);
        var dispatcher = new BsqdDispatcher(factory.CreateLogger<BsqdDispatcher>(), _settings.Dispatch);

        // Optional iCabbi
        IcabbiBookingService? icabbi = null;
        var icabbiEnabled = _settings.Icabbi.Enabled;
        if (icabbiEnabled && !string.IsNullOrEmpty(_settings.Icabbi.AppKey) && !string.IsNullOrEmpty(_settings.Icabbi.SecretKey))
        {
            icabbi = new IcabbiBookingService(_settings.Icabbi.AppKey, _settings.Icabbi.SecretKey, tenantBase: _settings.Icabbi.TenantBase);
            icabbi.OnLog += msg => Log($"🚕 {msg}");
        }

        using var dlg = new BookingForm(fareCalc, dispatcher, factory.CreateLogger<BookingForm>(), _settings.Supabase, callerPhone, callerName, icabbi, icabbiEnabled);
        var result = dlg.ShowDialog(this);

        if (result == DialogResult.OK && dlg.CompletedBooking != null)
        {
            var b = dlg.CompletedBooking;
            Log($"📋 Booking confirmed: {b.BookingRef} — {b.Pickup} → {b.Destination} ({b.Passengers} pax, {b.Fare})");
        }

        icabbi?.Dispose();
    }

    private void mnuViewConfig_Click(object? sender, EventArgs e)
    {
        if (File.Exists(SettingsPath))
        {
            try { System.Diagnostics.Process.Start("notepad.exe", SettingsPath); }
            catch { MessageBox.Show($"Config at:\n{SettingsPath}", "Config Path"); }
        }
        else
            MessageBox.Show("No config file yet. Settings will be saved on first Connect or Settings save.", "Config");
    }

    // ── Logging ──

    public void Log(string message)
    {
        if (InvokeRequired) { Invoke(() => Log(message)); return; }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lstLogs.Items.Add(line);
        if (lstLogs.Items.Count > 2000)
            lstLogs.Items.RemoveAt(0);
        lstLogs.TopIndex = lstLogs.Items.Count - 1;
    }

    private void lstLogs_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) CopySelectedLogs();
        if (e.Control && e.KeyCode == Keys.A)
        {
            for (int i = 0; i < lstLogs.Items.Count; i++)
                lstLogs.SetSelected(i, true);
        }
    }

    private void CopySelectedLogs()
    {
        var text = string.Join(Environment.NewLine, lstLogs.SelectedItems.Cast<string>());
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void CopyAllLogs()
    {
        var text = string.Join(Environment.NewLine, lstLogs.Items.Cast<string>());
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    // ── Simli avatar ──

    private void InitSimliAvatar()
    {
        try
        {
            // Fall back to hardcoded defaults if saved settings are empty
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

    /// <summary>Ensure Simli is initialized, then connect when a call starts.</summary>
    private async Task ConnectSimliAsync()
    {
        if (!_settings.Simli.Enabled)
        {
            Log("🎭 Simli disabled — skipping avatar connection");
            return;
        }

        // Safety net: if InitSimliAvatar failed at startup, retry now
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
    }

    private async Task DisconnectSimliAsync()
    {
        if (_simliAvatar == null) return;
        try { await _simliAvatar.DisconnectAsync(); }
        catch (Exception ex) { Log($"🎭 Simli disconnect error: {ex.Message}"); }
    }

    /// <summary>Feed A-law audio from Ada's TTS output to Simli (decode + upsample).
    /// Offloaded to ThreadPool to prevent resampling + WebRTC send from blocking the RTP audio path.</summary>
    private void FeedSimliAudio(byte[] alawFrame)
    {
        if (!_settings.Simli.Enabled) return;
        if (_simliAvatar == null || (!_simliAvatar.IsConnected && !_simliAvatar.IsConnecting))
            return;

        var frameCopy = new byte[alawFrame.Length];
        Buffer.BlockCopy(alawFrame, 0, frameCopy, 0, alawFrame.Length);

        _simliQueue.TryAdd(frameCopy);

        if (_simliThread == null || !_simliThread.IsAlive)
        {
            _simliThread = new Thread(SimliConsumerLoop) { IsBackground = true, Name = "SimliAudio" };
            _simliThread.Start();
        }
    }

    private void SimliConsumerLoop()
    {
        foreach (var frame in _simliQueue.GetConsumingEnumerable())
        {
            try
            {
                var pcm16at16k = AlawToSimliResampler.Convert(frame);
                _simliAvatar?.SendAudioAsync(pcm16at16k).GetAwaiter().GetResult();
            }
            catch { }
        }
    }

    /// <summary>Clear Simli buffer on barge-in.</summary>
    private void ClearSimliBuffer()
    {
        while (_simliQueue.TryTake(out _)) { }
        if (_simliAvatar == null || !_simliAvatar.IsConnected) return;
        _ = _simliAvatar.ClearBufferAsync();
    }

    public void SetAvatarStatus(string status)
    {
        if (InvokeRequired) { Invoke(() => SetAvatarStatus(status)); return; }
        lblAvatarStatus.Text = status;
    }

    // ── Audio monitor (hear caller audio locally) ──

    private void StartAudioMonitor()
    {
        StopAudioMonitor();
        try
        {
        _monitorBuffer = new BufferedWaveProvider(WaveFormat.CreateALawFormat(8000, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };
            _monitorOut = new WaveOutEvent { DesiredLatency = 50 };
            _monitorOut.Init(_monitorBuffer);
            if (!_muted) _monitorOut.Play();
            Log("🔊 Audio monitor started — hearing raw SIP audio");
        }
        catch (Exception ex)
        {
            Log($"⚠ Audio monitor failed: {ex.Message}");
        }
    }

    private void StopAudioMonitor()
    {
        try
        {
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
        }
        catch { }
        _monitorOut = null;
        _monitorBuffer = null;
    }

    // ── Operator microphone (Push-to-Talk → SIP) ──

    private void StartMicrophone()
    {
        lock (_micLock)
        {
            if (_micInput != null) return;

            try
            {
                _micInput = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(8000, 16, 1), // 8kHz PCM16 mono
                    BufferMilliseconds = 20
                };

                _micInput.DataAvailable += (s, e) =>
                {
                    // In operator mode: always send. In PTT mode: only when PTT active.
                    if (_operatorMode)
                    {
                        if (!_inCall) return;
                    }
                    else
                    {
                        if (!_pttActive || _currentSession == null) return;
                    }

                    // Convert PCM16 → A-law and send to SIP RTP stream
                    var alawData = new byte[e.BytesRecorded / 2];
                    for (int i = 0; i < alawData.Length; i++)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                        alawData[i] = NAudio.Codecs.ALawEncoder.LinearToALawSample(sample);
                    }

                    // Apply operator mic volume boost so caller can hear clearly
                    ALawVolumeBoost.ApplyInPlace(alawData, _operatorMicGain);

                    _sipServer?.SendOperatorAudio(alawData);
                };

                _micInput.StartRecording();
            }
            catch (Exception ex)
            {
                Log($"⚠ Microphone failed: {ex.Message}");
            }
        }
    }

    private void StopMicrophone()
    {
        lock (_micLock)
        {
            try
            {
                _micInput?.StopRecording();
                _micInput?.Dispose();
            }
            catch { }
            _micInput = null;
        }
    }
}
