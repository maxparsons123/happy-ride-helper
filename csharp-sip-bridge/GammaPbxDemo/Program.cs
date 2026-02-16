using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

// ─── Load Config ───
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var server   = config["Gamma:Server"]!;
var port     = int.Parse(config["Gamma:Port"] ?? "5060");
var transport = config["Gamma:Transport"] ?? "TCP";
var username = config["Gamma:Username"]!;
var password = config["Gamma:Password"]!;
var authId   = config["Gamma:AuthId"];
var domain   = config["Gamma:Domain"];
var display  = config["Gamma:DisplayName"] ?? "Ada Agent";
var expiry   = int.Parse(config["Gamma:RegistrationExpiry"] ?? "3600");

// Use AuthId for digest auth if provided, otherwise fall back to Username
var effectiveAuthUser = string.IsNullOrWhiteSpace(authId) ? username : authId;
// Use Domain if provided, otherwise use Server hostname
var effectiveDomain = string.IsNullOrWhiteSpace(domain) ? server : domain;

Console.WriteLine($"🔌 Gamma PBX Demo — {username}@{effectiveDomain} via {transport}");

// ─── Resolve hostname to IPv4 (Gamma requires hostname in headers, IP for routing) ───
IPAddress? registrarIp = null;
try
{
    var addresses = await Dns.GetHostAddressesAsync(server);
    registrarIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    Console.WriteLine($"✅ Resolved {server} → {registrarIp}");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ DNS resolution failed for {server}: {ex.Message}");
    if (!IPAddress.TryParse(server, out registrarIp))
    {
        Console.WriteLine("Cannot resolve server. Exiting.");
        return;
    }
}

// ─── Create SIP Transport ───
var sipTransport = new SIPTransport();

// Gamma requires fixed port 5060 for stable Contact headers on TCP
if (transport.Equals("TCP", StringComparison.OrdinalIgnoreCase))
{
    var tcpChannel = new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 5060));
    sipTransport.AddSIPChannel(tcpChannel);
    Console.WriteLine("📡 SIP TCP channel bound to port 5060");
}
else
{
    var udpChannel = new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 5060));
    sipTransport.AddSIPChannel(udpChannel);
    Console.WriteLine("📡 SIP UDP channel bound to port 5060");
}

// ─── Handle OPTIONS keepalives (Gamma sends these to verify we're alive) ───
sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, request) =>
{
    if (request.Method == SIPMethodsEnum.OPTIONS)
    {
        var okResponse = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
        await sipTransport.SendResponseAsync(okResponse);
        Console.WriteLine($"📤 OPTIONS → 200 OK to {remoteEP}");
    }
};

// ─── Register with Gamma PBX ───
// Key pattern: preserve hostname in AOR/From/To, use resolved IP as outbound proxy
var outboundProxy = new SIPEndPoint(
    transport.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? SIPProtocolsEnum.tcp : SIPProtocolsEnum.udp,
    registrarIp!,
    port);

var regAgent = new SIPRegistrationUserAgent(
    sipTransport,
    outboundProxy.ToString(),       // Outbound proxy = resolved IP
    new SIPURI(username, effectiveDomain, null),  // AOR = username@hostname (preserved)
    effectiveAuthUser,               // Auth user (may differ from extension)
    password,
    null,                            // realm
    effectiveDomain,                 // registrar = hostname (not IP)
    null,                            // contact
    expiry,
    null,                            // custom headers
    null,                            // display name
    async (uri, msg) => { });        // send callback

regAgent.RegistrationSuccessful += (uri, resp) =>
{
    Console.WriteLine($"✅ SIP Registered as {username}@{effectiveDomain}");
};

regAgent.RegistrationFailed += (uri, resp, err) =>
{
    Console.WriteLine($"❌ Registration FAILED: {resp?.StatusCode} {resp?.ReasonPhrase} — {err}");
};

regAgent.Start();
Console.WriteLine("⏳ Registering with Gamma PBX...");

// ─── Handle Inbound Calls ───
var userAgent = new SIPUserAgent(sipTransport, outboundProxy.ToString());

userAgent.OnIncomingCall += async (ua, req) =>
{
    Console.WriteLine($"📞 Incoming call from {req.Header.From?.FromURI?.User ?? "unknown"}");

    // Create media session — G.711 A-law only (Gamma requirement)
    var mediaSession = new VoIPMediaSession();
    mediaSession.AcceptRtpFromAny = true;  // Symmetric RTP for Gamma SBC

    var answered = await ua.Answer(req, mediaSession);
    if (answered)
    {
        Console.WriteLine("✅ Call answered! Audio bridge active.");

        // Here you would wire up your AI audio pipeline:
        // - Capture inbound RTP frames from mediaSession
        // - Send them to OpenAI Realtime API
        // - Feed AI audio back via mediaSession.SendRtpRaw()

        ua.OnCallHungup += (dialog) =>
        {
            Console.WriteLine("📴 Call ended.");
            mediaSession.Close("bye");
        };
    }
    else
    {
        Console.WriteLine("❌ Failed to answer call.");
    }
};

sipTransport.SIPTransportRequestReceived += async (localEP, remoteEP, request) =>
{
    if (request.Method == SIPMethodsEnum.INVITE)
    {
        Console.WriteLine($"📥 INVITE from {remoteEP}");
        userAgent.OnIncomingCall?.Invoke(userAgent, request);
    }
};

// ─── Keep alive ───
Console.WriteLine("\n🟢 Gamma PBX Demo running. Press Ctrl+C to exit.\n");
Console.WriteLine("Required firewall rules:");
Console.WriteLine("  TCP 5060 inbound (SIP signaling)");
Console.WriteLine("  UDP 10000-20000 inbound (RTP audio)");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (TaskCanceledException) { }

Console.WriteLine("Shutting down...");
regAgent.Stop();
sipTransport.Shutdown();
Console.WriteLine("👋 Done.");
