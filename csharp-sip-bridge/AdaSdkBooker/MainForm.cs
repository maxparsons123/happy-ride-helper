using System.Net.Http.Headers;
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

namespace AdaSdkBooker;

public partial class MainForm : Form
{
    private AppSettings _settings;
    private bool _sipConnected;
    private bool _inCall;
    private bool _muted;
    private bool _operatorMode;
    private float _operatorMicGain = 2.0f;
    private bool _adaEnabled = true;
    private bool _logVisible;

    private SipServer? _sipServer;
    private ILoggerFactory? _loggerFactory;
    private ICallSession? _currentSession;

    // Audio monitor
    private WaveOutEvent? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;

    // Operator microphone
    private WaveInEvent? _micInput;
    private readonly object _micLock = new();

    // Simli avatar
    private SimliAvatar? _simliAvatar;
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _simliQueue = new(200);
    private Thread? _simliThread;

    // Booking state
    private FareResult? _lastFareResult;
    private bool _addressesVerified;
    private readonly List<BookingState> _completedBookings = new();

    // Autocomplete debounce
    private System.Windows.Forms.Timer? _pickupDebounce, _dropoffDebounce;
    private CancellationTokenSource? _pickupCts, _dropoffCts;
    private bool _suppressTextChanged;

    // Caller history
    private string[]? _callerPickupHistory, _callerDropoffHistory;
    private string? _lastPickup, _lastDestination;

    // Map WebView
    private Microsoft.Web.WebView2.WinForms.WebView2? _mapView;

    public MainForm()
    {
        InitializeComponent();
        _settings = LoadSettings();
        ApplySettingsToUi();
        InitAdaMapPanel();
        InitAutocomplete();
        InitPassengerVehicleSync();
        Log("AdaSdkBooker v1.0 started. Configure SIP and click Connect.");
    }

    // ══════════════════════════════════════
    //  SETTINGS PERSISTENCE
    // ══════════════════════════════════════

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdaSdkBooker", "appsettings.json");

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
        catch (Exception ex) { Log($"⚠ Save failed: {ex.Message}"); }
    }

    // ══════════════════════════════════════
    //  UI INITIALIZATION
    // ══════════════════════════════════════

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
        txtSipPassword.Text = sip.Password;
        chkAutoAnswer.Checked = sip.AutoAnswer;
    }

    private void InitAutocomplete()
    {
        _pickupDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        _pickupDebounce.Tick += async (s, e) => { _pickupDebounce.Stop(); await FetchAutocompleteSuggestionsAsync(cmbPickup); };
        cmbPickup.TextChanged += (s, e) => { if (!_suppressTextChanged) { _pickupDebounce.Stop(); _pickupDebounce.Start(); } };

        _dropoffDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        _dropoffDebounce.Tick += async (s, e) => { _dropoffDebounce.Stop(); await FetchAutocompleteSuggestionsAsync(cmbDropoff); };
        cmbDropoff.TextChanged += (s, e) => { if (!_suppressTextChanged) { _dropoffDebounce.Stop(); _dropoffDebounce.Start(); } };
    }

    private void InitPassengerVehicleSync()
    {
        nudPassengers.ValueChanged += (s, e) =>
        {
            var pax = (int)nudPassengers.Value;
            cmbVehicle.SelectedIndex = pax switch { <= 4 => 0, 5 or 6 => 1, _ => 3 };
        };
    }

    private ILoggerFactory GetLoggerFactory()
    {
        return _loggerFactory ??= LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new CallbackLoggerProvider(Log));
        });
    }

    // ══════════════════════════════════════
    //  ADA / MAP TOGGLE
    // ══════════════════════════════════════

    private void InitAdaMapPanel()
    {
        if (_adaEnabled)
            InitSimliAvatar();
        else
            InitMapView();
    }

    private void tsiAdaToggle_Click(object? sender, EventArgs e)
    {
        _adaEnabled = !_adaEnabled;
        tsiAdaToggle.Text = _adaEnabled ? "🤖 Ada: ON" : "🗺️ Ada: OFF";
        tsiAdaToggle.ForeColor = _adaEnabled ? Color.LimeGreen : Color.FromArgb(180, 180, 180);
        lblAdaMapTitle.Text = _adaEnabled ? "🤖 ADA" : "🗺️ MAP";
        lblAdaMapTitle.ForeColor = _adaEnabled ? Color.LimeGreen : Color.FromArgb(0, 122, 204);

        pnlAdaMapHost.Controls.Clear();

        if (_adaEnabled)
        {
            // Dispose map, show avatar
            _mapView?.Dispose(); _mapView = null;
            InitSimliAvatar();
        }
        else
        {
            // Dispose avatar, show map
            if (_simliAvatar != null) { try { _simliAvatar.DisconnectAsync().GetAwaiter().GetResult(); } catch { } _simliAvatar.Dispose(); _simliAvatar = null; }
            InitMapView();
        }
    }

    private void InitMapView()
    {
        try
        {
            _mapView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            pnlAdaMapHost.Controls.Add(_mapView);
            _ = InitMapAsync();
        }
        catch (Exception ex)
        {
            Log($"🗺️ Map init failed: {ex.Message}");
            lblAdaMapStatus.Text = "Map unavailable";
        }
    }

    private async Task InitMapAsync()
    {
        if (_mapView == null) return;
        try
        {
            await _mapView.EnsureCoreWebView2Async();
            _mapView.CoreWebView2.NavigateToString(GetLeafletMapHtml());
            lblAdaMapStatus.Text = "Map ready";
        }
        catch (Exception ex) { Log($"🗺️ Map load error: {ex.Message}"); }
    }

    private void UpdateMapMarkers(double? pickupLat, double? pickupLon, double? destLat, double? destLon)
    {
        if (_mapView?.CoreWebView2 == null || _adaEnabled) return;
        var js = "clearMarkers();";
        if (pickupLat.HasValue && pickupLon.HasValue)
            js += $"addMarker({pickupLat:F6},{pickupLon:F6},'Pickup','green');";
        if (destLat.HasValue && destLon.HasValue)
            js += $"addMarker({destLat:F6},{destLon:F6},'Dropoff','red');";
        if (pickupLat.HasValue && pickupLon.HasValue)
            js += $"map.setView([{pickupLat:F6},{pickupLon:F6}],14);";
        try { _mapView.CoreWebView2.ExecuteScriptAsync(js); } catch { }
    }

    private static string GetLeafletMapHtml() => """
        <!DOCTYPE html><html><head>
        <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
        <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
        <style>body{margin:0;background:#1a1a1e}#map{width:100%;height:100vh}</style>
        </head><body><div id="map"></div><script>
        var map=L.map('map',{zoomControl:false}).setView([53.5,-2.2],10);
        L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{maxZoom:19}).addTo(map);
        var markers=[];
        function clearMarkers(){markers.forEach(m=>map.removeLayer(m));markers=[];}
        function addMarker(lat,lon,label,color){
            var m=L.circleMarker([lat,lon],{radius:8,fillColor:color,color:'white',weight:2,fillOpacity:0.9}).addTo(map);
            m.bindTooltip(label,{permanent:true,direction:'top',offset:[0,-10]});
            markers.push(m);
        }
        </script></body></html>
        """;

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
        var acct = _settings.SipAccounts[idx];
        _settings.Sip = acct.ToSipSettings();
        _settings.SelectedSipAccountIndex = idx;
        ApplySipSettingsToFields(_settings.Sip);
        SaveSettings();
        Log($"📞 Account: {acct.Label}");
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
        }
        else
        {
            label = $"{_settings.Sip.Username}@{_settings.Sip.Server}";
            var newAcct = new SipAccount();
            newAcct.FromSipSettings(_settings.Sip, label);
            _settings.SipAccounts.Add(newAcct);
            _settings.SelectedSipAccountIndex = _settings.SipAccounts.Count - 1;
        }
        SaveSettings();
        RefreshAccountDropdown();
        Log($"💾 Saved: {label}");
    }

    private void ReadSipFromUi()
    {
        _settings.Sip.Server = txtSipServer.Text.Trim();
        _settings.Sip.Port = int.TryParse(txtSipPort.Text, out var p) ? p : 5060;
        _settings.Sip.Username = txtSipUser.Text.Trim();
        _settings.Sip.Password = txtSipPassword.Text;
        _settings.Sip.AutoAnswer = chkAutoAnswer.Checked;
    }

    // ══════════════════════════════════════
    //  SIP CONNECTION
    // ══════════════════════════════════════

    private async void btnConnect_Click(object? sender, EventArgs e)
    {
        ReadSipFromUi();
        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || string.IsNullOrWhiteSpace(_settings.Sip.Username))
        { MessageBox.Show("SIP Server and Extension required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.IsNullOrWhiteSpace(_settings.OpenAi.ApiKey))
        { MessageBox.Show("OpenAI API Key required. Use ⚙ Settings.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        SaveSettings();
        Log($"📞 Connecting to {_settings.Sip.Server}:{_settings.Sip.Port}…");
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
                Log($"✅ Registered: {msg}");
                lblSipStatus.Text = "● Online";
                lblSipStatus.ForeColor = Color.LimeGreen;
                statusLabel.Text = "SIP Registered";
            });
            _sipServer.OnRegistrationFailed += msg => Invoke(() =>
            {
                Log($"❌ Reg failed: {msg}");
                lblSipStatus.Text = "● Failed";
                lblSipStatus.ForeColor = Color.OrangeRed;
            });
            _sipServer.OnCallStarted += (sessionId, callerId) => Invoke(() =>
            {
                Log($"📞 Call: {callerId}");
                SetInCall(true);
                statusCallId.Text = $"{callerId} [{sessionId}]";
                StartAudioMonitor();
                if (_adaEnabled) _ = ConnectSimliAsync();
                // Auto-populate phone in booking
                txtPhone.Text = callerId;
                _ = LoadCallerHistoryAsync(callerId);
            });
            _sipServer.OnCallRinging += (pendingId, callerId) => Invoke(() =>
            {
                Log($"📲 Ringing: {callerId}");
                statusCallId.Text = $"📞 {callerId} (ringing)";
                lblCallInfo.Text = $"Ring: {callerId}";
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
                Log($"📴 Ended: {reason}");
                if (_currentSession?.SessionId == sessionId) _currentSession = null;
                if (_sipServer?.ActiveCallCount == 0)
                {
                    SetInCall(false);
                    statusCallId.Text = "";
                    lblCallInfo.Text = "No call";
                    lblCallInfo.ForeColor = Color.Gray;
                    StopAudioMonitor();
                    if (_adaEnabled) _ = DisconnectSimliAsync();
                }
            });

            await _sipServer.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ {ex.Message}");
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

        IcabbiBookingService? icabbi = null;
        var icabbiEnabled = _settings.Icabbi.Enabled;
        if (icabbiEnabled && !string.IsNullOrWhiteSpace(_settings.Icabbi.AppKey))
        {
            icabbi = new IcabbiBookingService(_settings.Icabbi.AppKey, _settings.Icabbi.SecretKey, tenantBase: _settings.Icabbi.TenantBase);
            icabbi.OnLog += msg => Invoke(() => Log(msg));
        }
        else if (icabbiEnabled) { icabbiEnabled = false; }

        var session = new CallSession(sessionId, callerId, factory.CreateLogger<CallSession>(), _settings, aiClient, fareCalculator, dispatcher, icabbi, icabbiEnabled);

        session.OnTranscript += (role, text) => Invoke(() => Log($"💬 {role}: {text}"));

        session.OnAudioOut += alawFrame =>
        {
            if (_simliAvatar?.IsConnected == true)
                FeedSimliAudio(alawFrame);
            else
                _monitorBuffer?.AddSamples(alawFrame, 0, alawFrame.Length);
        };

        session.OnBargeIn += () => ClearSimliBuffer();

        // Auto-populate booking form from Ada's extracted data
        session.OnBookingUpdated += booking => Invoke(() =>
        {
            if (!string.IsNullOrEmpty(booking.Name) && string.IsNullOrEmpty(txtCallerName.Text))
                txtCallerName.Text = booking.Name;
            if (!string.IsNullOrEmpty(booking.Pickup) && cmbPickup.Text != booking.Pickup)
            { _suppressTextChanged = true; cmbPickup.Text = booking.Pickup; _suppressTextChanged = false; }
            if (!string.IsNullOrEmpty(booking.Destination) && cmbDropoff.Text != booking.Destination)
            { _suppressTextChanged = true; cmbDropoff.Text = booking.Destination; _suppressTextChanged = false; }
            if (booking.Passengers.HasValue && booking.Passengers > 0)
                nudPassengers.Value = Math.Min(booking.Passengers.Value, 16);
            if (!string.IsNullOrEmpty(booking.Fare))
            {
                lblFare.Text = $"Fare: {booking.Fare}";
                lblEta.Text = !string.IsNullOrEmpty(booking.Eta) ? $"ETA: {booking.Eta}" : "";
            }
            if (booking.Confirmed && !string.IsNullOrEmpty(booking.BookingRef))
            {
                AddJobToGrid(booking);
                ClearBookingForm();
                SetBookingStatus($"✅ Booked: {booking.BookingRef}");
            }
            else
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(booking.Pickup)) parts.Add($"From: {booking.Pickup}");
                if (!string.IsNullOrEmpty(booking.Destination)) parts.Add($"To: {booking.Destination}");
                lblCallInfo.Text = parts.Count > 0 ? string.Join(" | ", parts) : "Call active";
                lblCallInfo.ForeColor = Color.Cyan;
            }
        });

        session.OnEnded += (s, reason) => Invoke(() =>
        {
            lblCallInfo.Text = "No call";
            lblCallInfo.ForeColor = Color.Gray;
        });

        _currentSession = session;
        return session;
    }

    private async void btnDisconnect_Click(object? sender, EventArgs e)
    {
        try { if (_sipServer != null) { await _sipServer.StopAsync(); _sipServer = null; } }
        catch (Exception ex) { Log($"⚠ {ex.Message}"); }
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
            var answered = await _sipServer.AnswerOperatorCallAsync();
            if (answered) { SetInCall(true); StartAudioMonitor(); StartMicrophone(); Log("🎤 Operator active"); }
        }
        else { SetInCall(true); }
    }

    private void btnReject_Click(object? sender, EventArgs e)
    {
        if (_operatorMode && _sipServer != null) _sipServer.RejectPendingCall();
        SetInCall(false);
        lblCallInfo.Text = "No call"; lblCallInfo.ForeColor = Color.Gray;
    }

    private async void btnHangUp_Click(object? sender, EventArgs e)
    {
        if (_sipServer != null) { try { await _sipServer.HangupAllAsync("operator_hangup"); } catch { } }
        SetInCall(false);
    }

    private void btnMute_Click(object? sender, EventArgs e)
    {
        _muted = !_muted;
        btnMute.Text = _muted ? "🔇 Muted" : "🔊 Mute";
        if (_muted) { _monitorOut?.Pause(); } else { _monitorOut?.Play(); }
    }

    private async void btnCallOut_Click(object? sender, EventArgs e)
    {
        var phone = txtPhone.Text.Trim();
        if (string.IsNullOrWhiteSpace(phone))
        {
            MessageBox.Show("Enter a phone number in the booking form first.", "No Number", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_sipConnected || _sipServer == null)
        {
            MessageBox.Show("Connect to SIP first.", "Not Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Log($"📞 Calling out to {phone}…");
        try
        {
            await _sipServer.MakeCallAsync(phone);
            SetInCall(true);
            StartAudioMonitor();
            if (_operatorMode) StartMicrophone();
            statusCallId.Text = $"→ {phone}";
        }
        catch (Exception ex)
        {
            Log($"❌ Call-out failed: {ex.Message}");
        }
    }

    private void chkManualMode_CheckedChanged(object? sender, EventArgs e)
    {
        _operatorMode = chkManualMode.Checked;
        if (_sipServer != null) _sipServer.OperatorMode = _operatorMode;
        Log(_operatorMode ? "🎤 Operator mode ON" : "🤖 Auto mode ON");
        if (!_operatorMode) StopMicrophone();
    }

    // ══════════════════════════════════════
    //  UI STATE
    // ══════════════════════════════════════

    private void SetSipConnected(bool connected)
    {
        _sipConnected = connected;
        btnConnect.Enabled = !connected;
        btnDisconnect.Enabled = connected;
        cmbSipAccount.Enabled = !connected;
        txtSipServer.Enabled = !connected;
        txtSipPort.Enabled = !connected;
        txtSipUser.Enabled = !connected;
        txtSipPassword.Enabled = !connected;
        lblSipStatus.Text = connected ? "● Connecting…" : "● Offline";
        lblSipStatus.ForeColor = connected ? Color.Yellow : Color.Gray;
        statusLabel.Text = connected ? "Connecting…" : "Ready";
    }

    private void SetInCall(bool inCall)
    {
        _inCall = inCall;
        btnAnswer.Enabled = !inCall && _sipConnected;
        btnReject.Enabled = !inCall && _sipConnected;
        btnHangUp.Enabled = inCall;
        btnMute.Enabled = inCall;
        lblCallInfo.Text = inCall ? "Call active" : "No call";
        lblCallInfo.ForeColor = inCall ? Color.LimeGreen : Color.Gray;
        if (!inCall) { _muted = false; btnMute.Text = "🔊"; StopMicrophone(); }
    }

    // ══════════════════════════════════════
    //  INLINE BOOKING — VERIFY & DISPATCH
    // ══════════════════════════════════════

    private async Task VerifyAndQuoteAsync()
    {
        var pickup = cmbPickup.Text.Trim();
        var dropoff = cmbDropoff.Text.Trim();
        if (string.IsNullOrEmpty(pickup) || string.IsNullOrEmpty(dropoff))
        { MessageBox.Show("Enter pickup and dropoff.", "Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        btnVerify.Enabled = false;
        btnVerify.Text = "⏳…";
        SetBookingStatus("Calculating…");
        lblPickupStatus.Text = "⏳"; lblDropoffStatus.Text = "⏳";

        try
        {
            var factory = GetLoggerFactory();
            var fareCalc = new FareCalculator(factory.CreateLogger<FareCalculator>(), _settings.GoogleMaps, _settings.Supabase);
            _lastFareResult = await fareCalc.ExtractAndCalculateWithAiAsync(pickup, dropoff, txtPhone.Text.Trim());

            lblPickupStatus.Text = _lastFareResult.PickupLat.HasValue && _lastFareResult.PickupLat != 0 ? "✅" : "⚠️";
            lblPickupResolved.Text = _lastFareResult.PickupFormatted ?? pickup;
            lblDropoffStatus.Text = _lastFareResult.DestLat.HasValue && _lastFareResult.DestLat != 0 ? "✅" : "⚠️";
            lblDropoffResolved.Text = _lastFareResult.DestFormatted ?? dropoff;
            lblFare.Text = $"Fare: {_lastFareResult.Fare}";
            lblEta.Text = $"ETA: {_lastFareResult.Eta}";

            // Update map markers
            UpdateMapMarkers(_lastFareResult.PickupLat, _lastFareResult.PickupLon, _lastFareResult.DestLat, _lastFareResult.DestLon);

            if (_lastFareResult.NeedsClarification)
            {
                var msg = "Addresses need clarification:\n\n";
                if (_lastFareResult.PickupAlternatives?.Length > 0)
                    msg += $"Pickup:\n  • {string.Join("\n  • ", _lastFareResult.PickupAlternatives)}\n\n";
                if (_lastFareResult.DestAlternatives?.Length > 0)
                    msg += $"Dropoff:\n  • {string.Join("\n  • ", _lastFareResult.DestAlternatives)}\n";
                MessageBox.Show(msg, "Clarification", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetBookingStatus("Refine addresses and retry");
            }
            else
            {
                _addressesVerified = true;
                btnDispatch.Enabled = true;
                SetBookingStatus("✅ Ready to dispatch");
            }
        }
        catch (Exception ex)
        {
            lblPickupStatus.Text = "❌"; lblDropoffStatus.Text = "❌";
            SetBookingStatus($"Error: {ex.Message}");
        }
        finally { btnVerify.Enabled = true; btnVerify.Text = "🔍 Get Quote"; }
    }

    private async Task ConfirmBookingAsync()
    {
        if (!_addressesVerified || _lastFareResult == null)
        { MessageBox.Show("Verify addresses first.", "Not Verified", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        btnDispatch.Enabled = false;
        btnDispatch.Text = "⏳…";
        SetBookingStatus("Dispatching…");

        try
        {
            var booking = new BookingState
            {
                Name = txtCallerName.Text.Trim(),
                CallerPhone = txtPhone.Text.Trim(),
                Pickup = cmbPickup.Text.Trim(),
                Destination = cmbDropoff.Text.Trim(),
                Passengers = (int)nudPassengers.Value,
                PickupTime = cmbPickupTime.Text == "ASAP" ? "now" : cmbPickupTime.Text,
                VehicleType = cmbVehicle.Text,
                Fare = _lastFareResult.Fare,
                Eta = _lastFareResult.Eta,
                Confirmed = true,
                PickupLat = _lastFareResult.PickupLat, PickupLon = _lastFareResult.PickupLon,
                PickupStreet = _lastFareResult.PickupStreet, PickupNumber = _lastFareResult.PickupNumber,
                PickupPostalCode = _lastFareResult.PickupPostalCode, PickupCity = _lastFareResult.PickupCity,
                PickupFormatted = _lastFareResult.PickupFormatted,
                DestLat = _lastFareResult.DestLat, DestLon = _lastFareResult.DestLon,
                DestStreet = _lastFareResult.DestStreet, DestNumber = _lastFareResult.DestNumber,
                DestPostalCode = _lastFareResult.DestPostalCode, DestCity = _lastFareResult.DestCity,
                DestFormatted = _lastFareResult.DestFormatted,
            };

            var factory = GetLoggerFactory();
            var dispatcher = new BsqdDispatcher(factory.CreateLogger<BsqdDispatcher>(), _settings.Dispatch);
            var dispatched = await dispatcher.DispatchAsync(booking, booking.CallerPhone ?? "");

            if (dispatched)
            {
                booking.BookingRef = $"BKR-{DateTime.Now:HHmmss}";
                _ = dispatcher.SendWhatsAppAsync(booking.CallerPhone ?? "");
                AddJobToGrid(booking);
                ClearBookingForm();
                SetBookingStatus($"✅ Dispatched: {booking.BookingRef}");
                Log($"📋 Booked: {booking.BookingRef} — {booking.Pickup} → {booking.Destination}");
            }
            else
            {
                SetBookingStatus("❌ Dispatch failed");
                btnDispatch.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            SetBookingStatus($"Error: {ex.Message}");
            btnDispatch.Enabled = true;
        }
        finally { btnDispatch.Text = "✅ Dispatch"; }
    }

    private void ClearBookingForm()
    {
        txtCallerName.Text = "";
        txtPhone.Text = "";
        _suppressTextChanged = true;
        cmbPickup.Text = "";
        cmbDropoff.Text = "";
        _suppressTextChanged = false;
        nudPassengers.Value = 1;
        cmbVehicle.SelectedIndex = 0;
        cmbPickupTime.SelectedIndex = 0;
        lblPickupStatus.Text = ""; lblDropoffStatus.Text = "";
        lblPickupResolved.Text = ""; lblDropoffResolved.Text = "";
        lblFare.Text = "Fare: —"; lblEta.Text = "ETA: —";
        _lastFareResult = null;
        _addressesVerified = false;
        btnDispatch.Enabled = false;
        SetBookingStatus("");
    }

    private void SetBookingStatus(string text) => lblBookingStatus.Text = text;

    private void btnRepeatLast_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_lastPickup) || string.IsNullOrEmpty(_lastDestination)) return;
        _suppressTextChanged = true;
        cmbPickup.Text = _lastPickup;
        cmbDropoff.Text = _lastDestination;
        _suppressTextChanged = false;
        SetBookingStatus("🔁 Last journey loaded");
    }

    // ══════════════════════════════════════
    //  JOB GRID
    // ══════════════════════════════════════

    private void AddJobToGrid(BookingState booking)
    {
        _completedBookings.Insert(0, booking.Clone());
        dgvJobs.Rows.Insert(0, new object[]
        {
            booking.BookingRef ?? "—",
            booking.Name ?? "—",
            booking.CallerPhone ?? "—",
            booking.Pickup ?? "—",
            booking.Destination ?? "—",
            booking.Passengers?.ToString() ?? "1",
            booking.Fare ?? "—",
            booking.Confirmed ? "✅" : "Pending",
            DateTime.Now.ToString("HH:mm")
        });
    }

    // ══════════════════════════════════════
    //  CALLER HISTORY
    // ══════════════════════════════════════

    private async Task LoadCallerHistoryAsync(string phone)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var normalized = phone.Trim().Replace(" ", "");
            var url = $"{_settings.Supabase.Url}/rest/v1/callers?phone_number=eq.{Uri.EscapeDataString(normalized)}&select=*";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _settings.Supabase.AnonKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) { SetBookingStatus("New caller"); return; }
            var caller = arr[0];

            if (caller.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(txtCallerName.Text))
                    txtCallerName.Text = name;
            }

            if (caller.TryGetProperty("pickup_addresses", out var pickups) && pickups.ValueKind == JsonValueKind.Array)
            {
                _callerPickupHistory = pickups.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(15).ToArray();
                _suppressTextChanged = true;
                cmbPickup.Items.Clear();
                foreach (var addr in _callerPickupHistory) cmbPickup.Items.Add(addr);
                _suppressTextChanged = false;
            }

            if (caller.TryGetProperty("dropoff_addresses", out var dropoffs) && dropoffs.ValueKind == JsonValueKind.Array)
            {
                _callerDropoffHistory = dropoffs.EnumerateArray().Select(a => a.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(15).ToArray();
                _suppressTextChanged = true;
                cmbDropoff.Items.Clear();
                foreach (var addr in _callerDropoffHistory) cmbDropoff.Items.Add(addr);
                _suppressTextChanged = false;
            }

            var hasLastPickup = caller.TryGetProperty("last_pickup", out var lp) && lp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(lp.GetString());
            var hasLastDest = caller.TryGetProperty("last_destination", out var ld) && ld.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ld.GetString());
            if (hasLastPickup && hasLastDest)
            {
                _lastPickup = lp.GetString()!;
                _lastDestination = ld.GetString()!;
                btnRepeatLast.Visible = true;
                btnRepeatLast.Text = "🔁";
            }

            var totalBookings = 0;
            if (caller.TryGetProperty("total_bookings", out var tb)) totalBookings = tb.GetInt32();
            SetBookingStatus($"Returning caller — {totalBookings} bookings");
        }
        catch { SetBookingStatus("History unavailable"); }
    }

    // ══════════════════════════════════════
    //  ADDRESS AUTOCOMPLETE
    // ══════════════════════════════════════

    private async Task FetchAutocompleteSuggestionsAsync(ComboBox cmb)
    {
        var input = cmb.Text.Trim();
        if (input.Length < 3) return;
        var isPickup = cmb == cmbPickup;
        if (isPickup) { _pickupCts?.Cancel(); _pickupCts = new CancellationTokenSource(); }
        else { _dropoffCts?.Cancel(); _dropoffCts = new CancellationTokenSource(); }
        var ct = isPickup ? _pickupCts!.Token : _dropoffCts!.Token;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var payload = JsonSerializer.Serialize(new { input, phone = txtPhone.Text.Trim() });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.Supabase.Url}/functions/v1/address-autocomplete") { Content = content };
            request.Headers.Add("apikey", _settings.Supabase.AnonKey);
            request.Headers.Add("Authorization", $"Bearer {_settings.Supabase.AnonKey}");

            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode || ct.IsCancellationRequested) return;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("predictions", out var predictions)) return;
            var suggestions = predictions.EnumerateArray().Select(p => p.GetProperty("description").GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (ct.IsCancellationRequested || suggestions.Count == 0) return;

            var history = isPickup ? _callerPickupHistory : _callerDropoffHistory;
            var merged = new List<string>();
            if (history != null) merged.AddRange(history.Where(h => h.Contains(input, StringComparison.OrdinalIgnoreCase)));
            foreach (var s in suggestions) if (!merged.Contains(s, StringComparer.OrdinalIgnoreCase)) merged.Add(s);

            _suppressTextChanged = true;
            var cursorPos = cmb.SelectionStart;
            var currentText = cmb.Text;
            cmb.Items.Clear();
            foreach (var item in merged.Take(8)) cmb.Items.Add(item);
            cmb.Text = currentText;
            cmb.SelectionStart = cursorPos;
            cmb.SelectionLength = 0;
            _suppressTextChanged = false;
            if (merged.Count > 0 && cmb.Focused) cmb.DroppedDown = true;
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ══════════════════════════════════════
    //  TOOLSTRIP HANDLERS
    // ══════════════════════════════════════

    private void tsiSettings_Click(object? sender, EventArgs e)
    {
        using var dlg = new AdaSdkModel.ConfigForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dlg.Settings;
            SaveSettings();
            Log("⚙ Settings saved.");
        }
    }

    private void tsiViewConfig_Click(object? sender, EventArgs e)
    {
        if (File.Exists(SettingsPath))
            try { System.Diagnostics.Process.Start("notepad.exe", SettingsPath); } catch { }
        else
            MessageBox.Show("No config file yet.", "Config");
    }

    private void tsiLogToggle_Click(object? sender, EventArgs e)
    {
        _logVisible = !_logVisible;
        pnlLog.Visible = _logVisible;
        tsiLogToggle.ForeColor = _logVisible ? Color.LimeGreen : Color.FromArgb(220, 220, 225);
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
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = _settings.Simli.ApiKey = "vlw7tr7vxhhs52bi3rum7";
            if (string.IsNullOrWhiteSpace(faceId)) faceId = _settings.Simli.FaceId = "5fc23ea5-8175-4a82-aaaf-cdd8c88543dc";

            var factory = GetLoggerFactory();
            _simliAvatar = new SimliAvatar(factory.CreateLogger<SimliAvatar>());
            _simliAvatar.Configure(apiKey, faceId);
            _simliAvatar.Dock = DockStyle.Fill;
            pnlAdaMapHost.Controls.Clear();
            pnlAdaMapHost.Controls.Add(_simliAvatar);
            lblAdaMapStatus.Text = "Ready";
        }
        catch (Exception ex)
        {
            Log($"🎭 Avatar init failed: {ex.Message}");
            lblAdaMapStatus.Text = "Init failed";
            _simliAvatar = null;
        }
    }

    private async Task ConnectSimliAsync()
    {
        if (!_settings.Simli.Enabled || _simliAvatar == null) return;
        try { await _simliAvatar.ConnectAsync(); }
        catch (Exception ex) { Log($"🎭 Connect error: {ex.Message}"); }
    }

    private async Task DisconnectSimliAsync()
    {
        if (_simliAvatar == null) return;
        try { await _simliAvatar.DisconnectAsync(); } catch { }
    }

    private void FeedSimliAudio(byte[] alawFrame)
    {
        if (!_settings.Simli.Enabled || _simliAvatar == null || (!_simliAvatar.IsConnected && !_simliAvatar.IsConnecting)) return;
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

    private void ClearSimliBuffer()
    {
        while (_simliQueue.TryTake(out _)) { }
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
        }
        catch (Exception ex) { Log($"⚠ Monitor: {ex.Message}"); }
    }

    private void StopAudioMonitor()
    {
        try { _monitorOut?.Stop(); _monitorOut?.Dispose(); } catch { }
        _monitorOut = null; _monitorBuffer = null;
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
                    AdaSdkModel.Audio.ALawVolumeBoost.ApplyInPlace(alawData, _operatorMicGain);
                    _sipServer?.SendOperatorAudio(alawData);
                };
                _micInput.StartRecording();
            }
            catch (Exception ex) { Log($"⚠ Mic: {ex.Message}"); }
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
        if (txtLog.IsDisposed) return;
        if (txtLog.InvokeRequired) { txtLog.BeginInvoke(() => Log(message)); return; }

        txtLog.SelectionStart = txtLog.TextLength;
        txtLog.SelectionColor = message.Contains("❌") || message.Contains("Error") ? Color.Red
            : message.Contains("✅") || message.Contains("Booked") ? Color.LimeGreen
            : message.Contains("⚠") ? Color.Yellow
            : message.Contains("📞") || message.Contains("📲") ? Color.Cyan
            : Color.LightGreen;
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        txtLog.ScrollToCaret();

        if (txtLog.Lines.Length > 500)
        {
            txtLog.SelectionStart = 0;
            txtLog.SelectionLength = txtLog.GetFirstCharIndexFromLine(100);
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
        try { if (_simliAvatar != null) { _simliAvatar.DisconnectAsync().GetAwaiter().GetResult(); _simliAvatar.Dispose(); _simliAvatar = null; } } catch { }
        try { _mapView?.Dispose(); _mapView = null; } catch { }
        try { (_currentSession as IDisposable)?.Dispose(); } catch { }
        _currentSession = null;
        if (_sipServer != null) { try { _sipServer.StopAsync().GetAwaiter().GetResult(); } catch { } _sipServer = null; }
        _loggerFactory?.Dispose(); _loggerFactory = null;
        base.OnFormClosing(e);
    }
}
