using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace GammaPbxDemo;

public class MainForm : Form
{
    // ── UI Controls ──
    private readonly TextBox _txtServer, _txtPort, _txtUsername, _txtPassword, _txtAuthId, _txtDomain;
    private readonly ComboBox _cboTransport;
    private readonly Button _btnRegister, _btnUnregister;
    private readonly RichTextBox _txtLog;
    private readonly Label _lblStatus;

    // ── Call Panel ──
    private readonly GroupBox _grpCall;
    private readonly Label _lblCallState, _lblCaller;
    private readonly Button _btnAnswer, _btnReject, _btnHangUp;

    // ── SIP ──
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;

    // ── Call state ──
    private SIPServerUserAgent? _pendingUas;
    private VoIPMediaSession? _activeMedia;

    public MainForm()
    {
        Text = "Gamma PBX Demo";
        Width = 640;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;

        // ── Load defaults from appsettings.json ──
        IConfiguration? cfg = null;
        try
        {
            cfg = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
        }
        catch { }

        var gam = cfg?.GetSection("Gamma");

        // ── Credentials panel ──
        var grpCreds = new GroupBox { Text = "Gamma PBX Credentials", Left = 10, Top = 10, Width = 600, Height = 185 };
        Controls.Add(grpCreds);

        int y = 25;
        AddLabel(grpCreds, "Server:", 10, y);
        _txtServer = AddTextBox(grpCreds, 120, y, 200, gam?["Server"] ?? "");
        AddLabel(grpCreds, "Port:", 340, y);
        _txtPort = AddTextBox(grpCreds, 395, y, 60, gam?["Port"] ?? "5060");

        y += 30;
        AddLabel(grpCreds, "Username:", 10, y);
        _txtUsername = AddTextBox(grpCreds, 120, y, 200, gam?["Username"] ?? "");
        AddLabel(grpCreds, "Transport:", 340, y);
        _cboTransport = new ComboBox { Left = 395, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboTransport.Items.AddRange(new object[] { "UDP", "TCP" });
        _cboTransport.SelectedItem = gam?["Transport"] ?? "UDP";
        grpCreds.Controls.Add(_cboTransport);

        y += 30;
        AddLabel(grpCreds, "Password:", 10, y);
        _txtPassword = AddTextBox(grpCreds, 120, y, 200, gam?["Password"] ?? "");
        _txtPassword.UseSystemPasswordChar = true;

        y += 30;
        AddLabel(grpCreds, "Auth ID:", 10, y);
        _txtAuthId = AddTextBox(grpCreds, 120, y, 200, gam?["AuthId"] ?? "");
        AddLabel(grpCreds, "Domain:", 340, y);
        _txtDomain = AddTextBox(grpCreds, 395, y, 185, gam?["Domain"] ?? "");

        y += 35;
        _btnRegister = new Button { Text = "Register", Left = 120, Top = y, Width = 100 };
        _btnRegister.Click += async (_, _) => await RegisterAsync();
        grpCreds.Controls.Add(_btnRegister);

        _btnUnregister = new Button { Text = "Unregister", Left = 230, Top = y, Width = 100, Enabled = false };
        _btnUnregister.Click += (_, _) => Unregister();
        grpCreds.Controls.Add(_btnUnregister);

        _lblStatus = new Label
        {
            Text = "● Not registered",
            Left = 345, Top = y + 4, Width = 220,
            ForeColor = System.Drawing.Color.Gray
        };
        grpCreds.Controls.Add(_lblStatus);

        // ── Call Panel ──
        _grpCall = new GroupBox
        {
            Text = "Inbound Call",
            Left = 10, Top = 205, Width = 600, Height = 90,
            Enabled = false
        };
        Controls.Add(_grpCall);

        _lblCallState = new Label
        {
            Text = "No active call",
            Left = 10, Top = 22, Width = 200, Height = 20,
            Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold),
            ForeColor = System.Drawing.Color.Gray
        };
        _grpCall.Controls.Add(_lblCallState);

        _lblCaller = new Label
        {
            Text = "",
            Left = 10, Top = 46, Width = 300, Height = 20,
            ForeColor = System.Drawing.Color.DimGray
        };
        _grpCall.Controls.Add(_lblCaller);

        _btnAnswer = new Button
        {
            Text = "✅ Answer",
            Left = 330, Top = 20, Width = 85,
            BackColor = System.Drawing.Color.FromArgb(34, 139, 34),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _btnAnswer.Click += async (_, _) => await AnswerCallAsync();
        _grpCall.Controls.Add(_btnAnswer);

        _btnReject = new Button
        {
            Text = "❌ Reject",
            Left = 425, Top = 20, Width = 80,
            BackColor = System.Drawing.Color.FromArgb(180, 50, 50),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _btnReject.Click += (_, _) => RejectCall();
        _grpCall.Controls.Add(_btnReject);

        _btnHangUp = new Button
        {
            Text = "📴 Hang Up",
            Left = 515, Top = 20, Width = 75,
            BackColor = System.Drawing.Color.FromArgb(100, 30, 30),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _btnHangUp.Click += (_, _) => HangUp();
        _grpCall.Controls.Add(_btnHangUp);

        // ── Log ──
        _txtLog = new RichTextBox
        {
            Left = 10, Top = 305, Width = 600, Height = 370,
            ReadOnly = true,
            Font = new System.Drawing.Font("Consolas", 9)
        };
        Controls.Add(_txtLog);

        FormClosing += (_, _) => { HangUp(); Unregister(); };
    }

    // ── Registration ──
    private async Task RegisterAsync()
    {
        var server    = _txtServer.Text.Trim();
        var port      = int.TryParse(_txtPort.Text, out var p) ? p : 5060;
        var username  = _txtUsername.Text.Trim();
        var password  = _txtPassword.Text.Trim();
        var authId    = _txtAuthId.Text.Trim();
        var domain    = _txtDomain.Text.Trim();
        var transport = _cboTransport.SelectedItem?.ToString() ?? "UDP";

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(username))
        {
            Log("❌ Server and Username are required.");
            return;
        }

        SaveSettings(server, port.ToString(), username, password, authId, domain, transport);

        var effectiveAuthUser = string.IsNullOrEmpty(authId) ? username : authId;
        var effectiveDomain   = string.IsNullOrEmpty(domain)  ? server  : domain;

        // Resolve hostname → IP for outbound proxy routing
        IPAddress? registrarIp = null;
        try
        {
            Log($"🔍 Resolving {server}...");
            var addresses = await Dns.GetHostAddressesAsync(server);
            registrarIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            Log($"✅ Resolved → {registrarIp}");
        }
        catch (Exception ex)
        {
            Log($"❌ DNS failed: {ex.Message}");
            if (!IPAddress.TryParse(server, out registrarIp)) return;
        }

        _sipTransport = new SIPTransport();
        _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        Log("📡 SIP UDP channel started (ephemeral port)");

        var proto        = transport.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? SIPProtocolsEnum.tcp : SIPProtocolsEnum.udp;
        var outboundProxy = new SIPEndPoint(proto, registrarIp!, port);

        Log($"🔧 OutboundProxy : {outboundProxy}");
        Log($"🔧 AOR           : sip:{username}@{effectiveDomain}");
        Log($"🔧 Auth Username : {effectiveAuthUser}");

        // ── SIP full trace ──
        _sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, request) =>
        {
            Log($"⬇ [{request.Method}] {remoteEP} → {localEP}  URI={request.URI}");

            if (request.Method == SIPMethodsEnum.OPTIONS)
            {
                var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                await _sipTransport.SendResponseAsync(ok);
            }
            else if (request.Method == SIPMethodsEnum.INVITE)
            {
                await HandleIncomingInviteAsync(request);
            }
            else if (request.Method == SIPMethodsEnum.BYE)
            {
                Log("📴 BYE received — remote party hung up");
                CleanupCall();
            }
        };

        _sipTransport.SIPTransportResponseReceived += (localEP, remoteEP, response) =>
        {
            Log($"⬇ [{(int)response.Status} {response.ReasonPhrase}] {remoteEP}");
            if (response.Header.AuthenticationHeaders != null)
                foreach (var ah in response.Header.AuthenticationHeaders)
                    Log($"   WWW-Auth: {ah}");
            if (!string.IsNullOrEmpty(response.Body))
                Log($"   Body: {response.Body}");
            return Task.CompletedTask;
        };

        // ── Registration agent ──
        _regAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            username,          // AOR user part (SIP extension)
            password,
            effectiveDomain,
            120);
        _regAgent.OutboundProxy = outboundProxy;

        _regAgent.RegistrationSuccessful += (uri, resp) => Invoke(() =>
        {
            _lblStatus.Text      = "● Registered";
            _lblStatus.ForeColor = System.Drawing.Color.Green;
            _grpCall.Enabled     = true;
            Log($"✅ Registered as {username}@{effectiveDomain}");
        });

        _regAgent.RegistrationFailed += (uri, resp, err) => Invoke(() =>
        {
            _lblStatus.Text      = "● Failed";
            _lblStatus.ForeColor = System.Drawing.Color.Red;
            Log($"❌ Registration failed: {resp?.StatusCode} {resp?.ReasonPhrase} — {err}");
            if (resp != null)
            {
                Log($"   To  : {resp.Header.To}");
                Log($"   From: {resp.Header.From}");
            }
        });

        _userAgent = new SIPUserAgent(_sipTransport, outboundProxy);

        // Wire hangup event
        _userAgent.OnCallHungup += (_) => Invoke(() =>
        {
            Log("📴 Call ended by remote party");
            CleanupCall();
        });

        _regAgent.Start();
        Log("⏳ Registering...");

        _btnRegister.Enabled   = false;
        _btnUnregister.Enabled = true;
    }

    // ── Inbound INVITE handler ──
    private async Task HandleIncomingInviteAsync(SIPRequest request)
    {
        var callerNumber = request.Header.From?.FromURI?.User ?? "Unknown";
        var callerDisplay = request.Header.From?.FromName;
        var displayStr = string.IsNullOrEmpty(callerDisplay)
            ? callerNumber
            : $"{callerDisplay} ({callerNumber})";

        Log($"📞 Incoming call from {displayStr}");

        // Auto-send 180 Ringing immediately
        var ringing = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ringing, null);
        await _sipTransport!.SendResponseAsync(ringing);

        // Store UAS and update UI — wait for user to click Answer or Reject
        _pendingUas = _userAgent!.AcceptCall(request);

        Invoke(() =>
        {
            _lblCallState.Text      = "🔔 Incoming call...";
            _lblCallState.ForeColor = System.Drawing.Color.DarkOrange;
            _lblCaller.Text         = $"From: {displayStr}";
            _btnAnswer.Enabled      = true;
            _btnReject.Enabled      = true;
            _btnHangUp.Enabled      = false;
        });
    }

    // ── Answer ──
    private async Task AnswerCallAsync()
    {
        if (_pendingUas == null || _userAgent == null) return;

        Log("📲 Answering call...");
        _btnAnswer.Enabled = false;
        _btnReject.Enabled = false;

        // VoIPMediaSession with default audio device (system speakers + mic)
        _activeMedia = new VoIPMediaSession();
        _activeMedia.AcceptRtpFromAny = true;

        _activeMedia.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
        {
            // Audio is automatically played to speakers by VoIPMediaSession
            // Hook here if you need to process raw A-law bytes
        };

        var answered = await _userAgent.Answer(_pendingUas, _activeMedia);
        _pendingUas = null;

        if (answered)
        {
            Log("✅ Call answered — audio active (system speakers/mic)");
            Invoke(() =>
            {
                _lblCallState.Text      = "📞 In call";
                _lblCallState.ForeColor = System.Drawing.Color.Green;
                _btnHangUp.Enabled      = true;
            });
        }
        else
        {
            Log("❌ Failed to answer call");
            CleanupCall();
        }
    }

    // ── Reject ──
    private void RejectCall()
    {
        if (_pendingUas == null || _sipTransport == null) return;
        Log("🚫 Call rejected");
        var busy = SIPResponse.GetResponse(_pendingUas.ClientTransaction.TransactionRequest,
            SIPResponseStatusCodesEnum.BusyHere, null);
        _ = _sipTransport.SendResponseAsync(busy);
        _pendingUas = null;
        CleanupCall();
    }

    // ── Hang Up ──
    private void HangUp()
    {
        try { _userAgent?.Hangup(); } catch { }
        CleanupCall();
        Log("📴 Call ended (local hang-up)");
    }

    // ── Call cleanup ──
    private void CleanupCall()
    {
        _pendingUas = null;
        _activeMedia?.Close("bye");
        _activeMedia = null;

        if (!IsDisposed)
        {
            Invoke(() =>
            {
                _lblCallState.Text      = "No active call";
                _lblCallState.ForeColor = System.Drawing.Color.Gray;
                _lblCaller.Text         = "";
                _btnAnswer.Enabled      = false;
                _btnReject.Enabled      = false;
                _btnHangUp.Enabled      = false;
            });
        }
    }

    // ── Unregister ──
    private void Unregister()
    {
        _regAgent?.Stop();
        _sipTransport?.Shutdown();
        _regAgent     = null;
        _sipTransport = null;
        _userAgent    = null;

        if (!IsDisposed)
        {
            _lblStatus.Text      = "● Not registered";
            _lblStatus.ForeColor = System.Drawing.Color.Gray;
            _grpCall.Enabled     = false;
            _btnRegister.Enabled   = true;
            _btnUnregister.Enabled = false;
            CleanupCall();
            Log("👋 Unregistered");
        }
    }

    // ── Settings persistence ──
    private void SaveSettings(string server, string port, string username, string password,
                              string authId, string domain, string transport)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            Dictionary<string, object>? root = null;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            }
            root ??= new Dictionary<string, object>();
            root["Gamma"] = new Dictionary<string, string?>
            {
                ["Server"]    = server,
                ["Port"]      = port,
                ["Username"]  = username,
                ["Password"]  = password,
                ["AuthId"]    = string.IsNullOrEmpty(authId) ? null : authId,
                ["Domain"]    = domain,
                ["Transport"] = transport
            };
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(root, opts));
            Log("💾 Settings saved");
        }
        catch (Exception ex) { Log($"⚠️ Could not save settings: {ex.Message}"); }
    }

    // ── Helpers ──
    private void Log(string msg)
    {
        if (InvokeRequired) { Invoke(() => Log(msg)); return; }
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        _txtLog.ScrollToCaret();
    }

    private static Label AddLabel(Control parent, string text, int x, int y)
    {
        var lbl = new Label { Text = text, Left = x, Top = y + 3, AutoSize = true };
        parent.Controls.Add(lbl);
        return lbl;
    }

    private static TextBox AddTextBox(Control parent, int x, int y, int w, string val)
    {
        var txt = new TextBox { Left = x, Top = y, Width = w, Text = val };
        parent.Controls.Add(txt);
        return txt;
    }
}
