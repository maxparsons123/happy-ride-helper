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

    // ── SIP ──
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regAgent;
    private SIPUserAgent? _userAgent;

    public MainForm()
    {
        Text = "Gamma PBX Demo";
        Width = 620;
        Height = 600;
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
        var grp = new GroupBox { Text = "Gamma PBX Credentials", Left = 10, Top = 10, Width = 580, Height = 180 };
        Controls.Add(grp);

        int y = 25;
        AddLabel(grp, "Server:", 10, y);
        _txtServer = AddTextBox(grp, 120, y, 200, gam?["Server"] ?? "");
        AddLabel(grp, "Port:", 340, y);
        _txtPort = AddTextBox(grp, 400, y, 60, gam?["Port"] ?? "5060");

        y += 30;
        AddLabel(grp, "Username:", 10, y);
        _txtUsername = AddTextBox(grp, 120, y, 200, gam?["Username"] ?? "");
        AddLabel(grp, "Transport:", 340, y);
        _cboTransport = new ComboBox { Left = 400, Top = y, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboTransport.Items.AddRange(new object[] { "TCP", "UDP" });
        _cboTransport.SelectedItem = gam?["Transport"] ?? "TCP";
        grp.Controls.Add(_cboTransport);

        y += 30;
        AddLabel(grp, "Password:", 10, y);
        _txtPassword = AddTextBox(grp, 120, y, 200, gam?["Password"] ?? "");
        _txtPassword.UseSystemPasswordChar = true;

        y += 30;
        AddLabel(grp, "Auth ID:", 10, y);
        _txtAuthId = AddTextBox(grp, 120, y, 200, gam?["AuthId"] ?? "");
        AddLabel(grp, "Domain:", 340, y);
        _txtDomain = AddTextBox(grp, 400, y, 160, gam?["Domain"] ?? "");

        y += 35;
        _btnRegister = new Button { Text = "Register", Left = 120, Top = y, Width = 100 };
        _btnRegister.Click += async (_, _) => await RegisterAsync();
        grp.Controls.Add(_btnRegister);

        _btnUnregister = new Button { Text = "Unregister", Left = 230, Top = y, Width = 100, Enabled = false };
        _btnUnregister.Click += (_, _) => Unregister();
        grp.Controls.Add(_btnUnregister);

        _lblStatus = new Label { Text = "● Not registered", Left = 350, Top = y + 4, Width = 200, ForeColor = System.Drawing.Color.Gray };
        grp.Controls.Add(_lblStatus);

        // ── Log ──
        _txtLog = new RichTextBox { Left = 10, Top = 200, Width = 580, Height = 350, ReadOnly = true, Font = new System.Drawing.Font("Consolas", 9) };
        Controls.Add(_txtLog);

        FormClosing += (_, _) => Unregister();
    }

    // ── Registration ──
    private async Task RegisterAsync()
    {
        var server = _txtServer.Text.Trim();
        var port = int.TryParse(_txtPort.Text, out var p) ? p : 5060;
        var username = _txtUsername.Text.Trim();
        var password = _txtPassword.Text.Trim();
        var authId = _txtAuthId.Text.Trim();
        var domain = _txtDomain.Text.Trim();
        var transport = _cboTransport.SelectedItem?.ToString() ?? "TCP";

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(username))
        {
            Log("❌ Server and Username are required.");
            return;
        }

        // Persist credentials for next launch
        SaveSettings(server, port.ToString(), username, password, authId, domain, transport);

        var effectiveAuthUser = string.IsNullOrEmpty(authId) ? username : authId;
        var effectiveDomain = string.IsNullOrEmpty(domain) ? server : domain;

        // Resolve hostname
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

        // Create transport
        _sipTransport = new SIPTransport();

        // Bind UDP on an ephemeral port (OS picks a free one) — avoids port conflicts
        _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        Log("📡 SIP UDP channel started (ephemeral port)");

        // ── FULL SIP TRACE — single consolidated handler ──
        // Logs every inbound SIP request with full headers
        _sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, request) =>
        {
            Log($"⬇ SIP RX [{request.Method}] {remoteEP} → {localEP}");
            Log($"   From   : {request.Header.From}");
            Log($"   To     : {request.Header.To}");
            Log($"   URI    : {request.URI}");
            Log($"   CSeq   : {request.Header.CSeq} {request.Header.CSeqMethod}");
            Log($"   CallId : {request.Header.CallId}");

            if (request.Method == SIPMethodsEnum.OPTIONS)
            {
                var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                await _sipTransport.SendResponseAsync(ok);
                Log($"   ↳ Auto-replied 200 OK to OPTIONS");
            }
            else if (request.Method == SIPMethodsEnum.INVITE)
            {
                var from = request.Header.From?.FromURI?.User ?? "unknown";
                Log($"📞 Incoming INVITE from {from}");
                var uas = _userAgent!.AcceptCall(request);
                var mediaSession = new VoIPMediaSession();
                mediaSession.AcceptRtpFromAny = true;
                var answered = await _userAgent.Answer(uas, mediaSession);
                if (answered)
                {
                    Log("✅ Call answered — audio bridge active");
                    _userAgent.OnCallHungup += (d) => { Log("📴 Call ended"); mediaSession.Close("bye"); };
                }
                else
                {
                    Log("❌ Failed to answer call");
                }
            }
        };

        // Logs every inbound SIP response with full headers — key for diagnosing 4xx errors
        _sipTransport.SIPTransportResponseReceived += (localEP, remoteEP, response) =>
        {
            Log($"⬇ SIP RX [{(int)response.Status} {response.ReasonPhrase}] {remoteEP} → {localEP}");
            Log($"   From   : {response.Header.From}");
            Log($"   To     : {response.Header.To}");
            Log($"   CSeq   : {response.Header.CSeq} {response.Header.CSeqMethod}");
            Log($"   CallId : {response.Header.CallId}");
            if (response.Header.AuthenticationHeaders != null)
                foreach (var ah in response.Header.AuthenticationHeaders)
                    Log($"   WWW-Auth: {ah}");
            if (!string.IsNullOrEmpty(response.Body))
                Log($"   Body   : {response.Body}");
            return Task.CompletedTask;
        };

        // Register — use OutboundProxy for IP routing, preserve domain in AOR
        var proto = transport.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? SIPProtocolsEnum.tcp : SIPProtocolsEnum.udp;
        var outboundProxy = new SIPEndPoint(proto, registrarIp!, port);

        Log($"🔧 OutboundProxy  : {outboundProxy}");
        Log($"🔧 AuthUser       : {effectiveAuthUser}");
        Log($"🔧 Domain (AOR)   : {effectiveDomain}");
        Log($"🔧 Transport      : {transport}");
        Log($"🔧 Expiry         : 120s");

        // SHORT expiry (120s) keeps the BT NAT hole alive with frequent re-REGISTERs
        _regAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            effectiveAuthUser,
            password,
            effectiveDomain,
            120);
        _regAgent.OutboundProxy = outboundProxy;

        _regAgent.RegistrationSuccessful += (uri, resp) =>
        {
            Invoke(() =>
            {
                _lblStatus.Text = "● Registered";
                _lblStatus.ForeColor = System.Drawing.Color.Green;
                Log($"✅ Registered as {username}@{effectiveDomain}");
            });
        };

        _regAgent.RegistrationFailed += (uri, resp, err) =>
        {
            Invoke(() =>
            {
                _lblStatus.Text = "● Failed";
                _lblStatus.ForeColor = System.Drawing.Color.Red;
                Log($"❌ Registration failed: {resp?.StatusCode} {resp?.ReasonPhrase} — {err}");
                if (resp != null)
                {
                    Log($"   Full response To  : {resp.Header.To}");
                    Log($"   Full response From: {resp.Header.From}");
                }
            });
        };

        // Inbound call user agent
        _userAgent = new SIPUserAgent(_sipTransport, outboundProxy);

        _regAgent.Start();
        Log("⏳ Registering...");

        _btnRegister.Enabled = false;
        _btnUnregister.Enabled = true;
    }

    private void Unregister()
    {
        _regAgent?.Stop();
        _sipTransport?.Shutdown();
        _regAgent = null;
        _sipTransport = null;
        _userAgent = null;

        if (!IsDisposed)
        {
            _lblStatus.Text = "● Not registered";
            _lblStatus.ForeColor = System.Drawing.Color.Gray;
            _btnRegister.Enabled = true;
            _btnUnregister.Enabled = false;
            Log("👋 Unregistered");
        }
    }

    // ── Persistence ──
    private void SaveSettings(string server, string port, string username, string password, string authId, string domain, string transport)
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
                ["Server"] = server,
                ["Port"] = port,
                ["Username"] = username,
                ["Password"] = password,
                ["AuthId"] = string.IsNullOrEmpty(authId) ? null : authId,
                ["Domain"] = domain,
                ["Transport"] = transport
            };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(root, opts));
            Log("💾 Settings saved");
        }
        catch (Exception ex)
        {
            Log($"⚠️ Could not save settings: {ex.Message}");
        }
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
