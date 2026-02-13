using System.Text.Json;
using System.Text.RegularExpressions;
using AdaSdkModel.Ai;
using AdaSdkModel.Audio;
using AdaSdkModel.Avatar;
using AdaSdkModel.Config;
using AdaSdkModel.Core;
using AdaSdkModel.Services;
using AdaSdkModel.Sip;
using Microsoft.Extensions.Logging;
using NAudio.Codecs;
using NAudio.Wave;

namespace AdaSdkModel;

public partial class MainForm : Form
{
    private AppSettings _settings;
    private bool _sipConnected;
    private bool _inCall;
    private bool _muted;
    private bool _operatorMode;
    private float _operatorMicGain = 2.0f;

    private SipServer? _sipServer;
    private ILoggerFactory? _loggerFactory;
    private ICallSession? _currentSession;

    // Audio monitor (hear raw SIP audio locally)
    private WaveOutEvent? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;

    // Operator microphone
    private WaveInEvent? _micInput;
    private readonly object _micLock = new();

    // Simli avatar
    private SimliAvatar? _simliAvatar;
    public MainForm()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ApplySettingsToUi();
        InitSimliAvatar();
        Log($"AdaSdkModel v{OpenAiSdkClient.VERSION} started. Configure SIP and click Connect.");
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdaSdkModel", "appsettings.json");

    private static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                var defaults = new DispatchSettings();
                if (string.IsNullOrWhiteSpace(settings.Dispatch.BsqdWebhookUrl))
                    settings.Dispatch.BsqdWebhookUrl = defaults.BsqdWebhookUrl;
                if (string.IsNullOrWhiteSpace(settings.Dispatch.BsqdApiKey))
                    settings.Dispatch.BsqdApiKey = defaults.BsqdApiKey;
                return settings;
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
        txtAuthId.Text = sip.AuthId ?? "";
        txtSipPassword.Text = sip.Password;
        txtDomain.Text = sip.Domain ?? "";
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

        // Sync the PREVIOUS account's UI edits before switching away
        var prevIdx = _settings.SelectedSipAccountIndex;
        if (prevIdx >= 0 && prevIdx < _settings.SipAccounts.Count && prevIdx != idx)
        {
            ReadSipFromUi();
            var prevLabel = _settings.SipAccounts[prevIdx].Label;
            _settings.SipAccounts[prevIdx].FromSipSettings(_settings.Sip, prevLabel);
        }

        // Now load the newly selected account
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
        _settings.Sip.AuthId = string.IsNullOrWhiteSpace(txtAuthId.Text) ? null : txtAuthId.Text.Trim();
        _settings.Sip.Password = txtSipPassword.Text;
        _settings.Sip.Domain = string.IsNullOrWhiteSpace(txtDomain.Text) ? null : txtDomain.Text.Trim();
        _settings.Sip.Transport = cmbTransport.SelectedItem?.ToString() ?? "UDP";
        _settings.Sip.AutoAnswer = chkAutoAnswer.Checked;
    }

    /// <summary>Keep the selected SipAccount in sync with the inline Sip fields so accounts don't get lost on save.</summary>
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

    private async void btnConnect_Click(object? sender, EventArgs e)
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
            var sessionManager = new SessionManager(factory.CreateLogger<SessionManager>(), CreateCallSession);

            _sipServer = new SipServer(factory.CreateLogger<SipServer>(), _settings.Sip, _settings.Audio, sessionManager);
            _sipServer.OperatorMode = _operatorMode;

            _sipServer.OnLog += msg => Invoke(() => Log(msg));

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
                Log($"📞 Call active: {callerId} [{sessionId}]");
                SetInCall(true);
                statusCallId.Text = $"{callerId} [{sessionId}]";
                StartAudioMonitor();
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
                _monitorBuffer?.AddSamples(alawFrame, 0, alawFrame.Length);
            };

            _sipServer.OnCallEnded += (sessionId, reason) => Invoke(() =>
            {
                Log($"📴 Call {sessionId} ended: {reason}");
                if (_currentSession?.SessionId == sessionId)
                    _currentSession = null;
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
        var factory = GetLoggerFactory();

        var aiClient = new OpenAiSdkClient(factory.CreateLogger<OpenAiSdkClient>(), _settings.OpenAi);
        var fareCalculator = new FareCalculator(factory.CreateLogger<FareCalculator>(), _settings.GoogleMaps, _settings.Supabase);
        var dispatcher = new BsqdDispatcher(factory.CreateLogger<BsqdDispatcher>(), _settings.Dispatch);

        // iCabbi integration
        IcabbiBookingService? icabbi = null;
        var icabbiEnabled = _settings.Icabbi.Enabled;
        if (icabbiEnabled && !string.IsNullOrWhiteSpace(_settings.Icabbi.AppKey))
        {
            icabbi = new IcabbiBookingService(
                _settings.Icabbi.AppKey,
                _settings.Icabbi.SecretKey,
                tenantBase: _settings.Icabbi.TenantBase);
            icabbi.OnLog += msg => Invoke(() => Log(msg));
            Log($"🚕 iCabbi enabled (tenant: {_settings.Icabbi.TenantBase})");
        }
        else if (icabbiEnabled)
        {
            Log("⚠️ iCabbi enabled but AppKey is empty — skipping");
            icabbiEnabled = false;
        }

        var session = new CallSession(sessionId, callerId,
            factory.CreateLogger<CallSession>(), _settings, aiClient, fareCalculator, dispatcher,
            icabbi, icabbiEnabled);

        // Wire session events → UI
        session.OnTranscript += (role, text) => Invoke(() => Log($"💬 {role}: {text}"));

        // Wire audio → Simli avatar OR monitor speakers (never both to avoid double audio)
        session.OnAudioOut += alawFrame =>
        {
            if (_simliAvatar?.IsConnected == true)
            {
                // Avatar is active – route audio to avatar only (it has its own speaker)
                FeedSimliAudio(alawFrame);
            }
            else
            {
                // No avatar – fall back to local monitor speakers
                _monitorBuffer?.AddSamples(alawFrame, 0, alawFrame.Length);
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
            lblCallInfo.ForeColor = booking.Confirmed ? Color.LimeGreen : Color.Cyan;
        });

        session.OnEnded += (s, reason) => Invoke(() =>
        {
            Log($"📴 Call {s.SessionId} ended: {reason}");
            lblCallInfo.Text = "No active call";
            lblCallInfo.ForeColor = Color.Gray;
        });

        _currentSession = session;
        return session;
    }

    private async void btnDisconnect_Click(object? sender, EventArgs e)
    {
        Log("📞 Disconnecting SIP…");
        try
        {
            if (_sipServer != null) { await _sipServer.StopAsync(); _sipServer = null; }
        }
        catch (Exception ex) { Log($"⚠ Disconnect error: {ex.Message}"); }
        SetSipConnected(false);
        SetInCall(false);
    }

    // ══════════════════════════════════════
    //  CALL CONTROLS
    // ══════════════════════════════════════

    private async void btnAnswer_Click(object? sender, EventArgs e)
    {
        if (_operatorMode && _sipServer != null)
        {
            var callerPhone = statusCallId.Text;
            var phoneMatch = Regex.Match(callerPhone, @"[\+\d]{6,}");
            var phone = phoneMatch.Success ? phoneMatch.Value : null;

            Log("✅ Answering call in operator mode…");
            var answered = await _sipServer.AnswerOperatorCallAsync();
            if (answered)
            {
                SetInCall(true);
                StartAudioMonitor();
                StartMicrophone();
                Log("🎤 Operator mic active — speak normally");
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
            _sipServer.RejectPendingCall();
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
        if (_sipServer != null) _sipServer.OperatorMode = _operatorMode;
        if (_operatorMode)
        {
            Log("🎤 Operator mode ON – calls ring and wait for you to answer");
            Log("    Mic will be always active during calls");
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
        if (_muted) { _monitorOut?.Pause(); Log("🔇 Audio muted"); }
        else { _monitorOut?.Play(); Log("🔊 Audio unmuted"); }
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
        cmbTransport.Enabled = enabled;
    }

    private void SetInCall(bool inCall)
    {
        _inCall = inCall;
        btnAnswer.Enabled = !inCall && _sipConnected;
        btnReject.Enabled = !inCall && _sipConnected;
        btnHangUp.Enabled = inCall;
        btnMute.Enabled = inCall;
        lblCallInfo.Text = inCall ? "Call in progress" : "No active call";
        lblCallInfo.ForeColor = inCall ? Color.LimeGreen : Color.Gray;
        if (!inCall) { _muted = false; btnMute.Text = "🔊 Mute"; btnMute.BackColor = Color.FromArgb(80, 80, 85); StopMicrophone(); }
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

    private void mnuNewBooking_Click(object? sender, EventArgs e) => OpenBookingForm(null, null);

    private void OpenBookingForm(string? callerPhone, string? callerName)
    {
        var factory = GetLoggerFactory();
        var fareCalc = new FareCalculator(factory.CreateLogger<FareCalculator>(), _settings.GoogleMaps, _settings.Supabase);
        var dispatcher = new BsqdDispatcher(factory.CreateLogger<BsqdDispatcher>(), _settings.Dispatch);

        using var dlg = new BookingForm(fareCalc, dispatcher, factory.CreateLogger<BookingForm>(), _settings.Supabase, callerPhone, callerName);
        var result = dlg.ShowDialog(this);

        if (result == DialogResult.OK && dlg.CompletedBooking != null)
        {
            var b = dlg.CompletedBooking;
            Log($"📋 Booking confirmed: {b.BookingRef} — {b.Pickup} → {b.Destination} ({b.Passengers} pax, {b.Fare})");
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
    }

    private async Task DisconnectSimliAsync()
    {
        if (_simliAvatar == null) return;
        try { await _simliAvatar.DisconnectAsync(); }
        catch (Exception ex) { Log($"🎭 Simli disconnect error: {ex.Message}"); }
    }

    private void FeedSimliAudio(byte[] alawFrame)
    {
        if (!_settings.Simli.Enabled) return;
        if (_simliAvatar == null || (!_simliAvatar.IsConnected && !_simliAvatar.IsConnecting))
            return;

        var frameCopy = new byte[alawFrame.Length];
        Buffer.BlockCopy(alawFrame, 0, frameCopy, 0, alawFrame.Length);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var pcm16at16k = AlawToSimliResampler.Convert(frameCopy);
                _ = _simliAvatar?.SendAudioAsync(pcm16at16k);
            }
            catch { /* Simli errors must never affect call audio */ }
        });
    }

    private void ClearSimliBuffer()
    {
        if (_simliAvatar == null || !_simliAvatar.IsConnected) return;
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
            _monitorBuffer = new BufferedWaveProvider(WaveFormat.CreateALawFormat(8000, 1))
            { BufferDuration = TimeSpan.FromSeconds(5), DiscardOnBufferOverflow = true };
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
    //  OPERATOR MICROPHONE
    // ══════════════════════════════════════

    private void StartMicrophone()
    {
        lock (_micLock)
        {
            if (_micInput != null) return;
            try
            {
                _micInput = new WaveInEvent { WaveFormat = new WaveFormat(8000, 16, 1), BufferMilliseconds = 20 };
                _micInput.DataAvailable += (s, e) =>
                {
                    if (!_operatorMode || !_inCall) return;
                    var alawData = new byte[e.BytesRecorded / 2];
                    for (int i = 0; i < alawData.Length; i++)
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                        alawData[i] = ALawEncoder.LinearToALawSample(sample);
                    }
                    ALawVolumeBoost.ApplyInPlace(alawData, _operatorMicGain);
                    _sipServer?.SendOperatorAudio(alawData);
                };
                _micInput.StartRecording();
            }
            catch (Exception ex) { Log($"⚠ Microphone failed: {ex.Message}"); }
        }
    }

    private void StopMicrophone()
    {
        lock (_micLock)
        {
            try { _micInput?.StopRecording(); _micInput?.Dispose(); } catch { }
            _micInput = null;
        }
    }

    // ══════════════════════════════════════
    //  LOGGING
    // ══════════════════════════════════════

    public void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }

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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopMicrophone();
        StopAudioMonitor();

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

        try { (_currentSession as IDisposable)?.Dispose(); } catch { }
        _currentSession = null;
        if (_sipServer != null)
        {
            try { _sipServer.StopAsync().GetAwaiter().GetResult(); } catch { }
            _sipServer = null;
        }
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        base.OnFormClosing(e);
    }
}
