using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace TaxiSipBridge;

internal class Program
{
    private const int MAX_CONCURRENT_CALLS = 5;

    private static SIPTransport _sipTransport = null!;
    private static readonly ConcurrentDictionary<string, TaxiBotCallSession> _sessions = new();

    // Configuration from environment or defaults
    private static readonly string SIP_LISTEN_ADDRESS = Environment.GetEnvironmentVariable("SIP_LISTEN_ADDRESS") ?? "0.0.0.0";
    private static readonly int SIP_LISTEN_PORT = int.TryParse(Environment.GetEnvironmentVariable("SIP_LISTEN_PORT"), out var p) ? p : 5060;
    private static readonly string SIP_USERNAME = Environment.GetEnvironmentVariable("SIP_USER") ?? "your-sip-username";
    private static readonly string SIP_PASSWORD = Environment.GetEnvironmentVariable("SIP_PASSWORD") ?? "your-sip-password";
    private static readonly string SIP_DOMAIN = Environment.GetEnvironmentVariable("SIP_SERVER") ?? "sip.provider.com";
    private static readonly string OPENAI_API_KEY = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";

    static async Task Main(string[] args)
    {
        Console.WriteLine("===========================================");
        Console.WriteLine("  Taxi SIP Bridge - Simplified Architecture");
        Console.WriteLine("  Multi-call RTP/SIP to OpenAI Realtime");
        Console.WriteLine("===========================================\n");

        if (string.IsNullOrEmpty(OPENAI_API_KEY))
        {
            Console.WriteLine("⚠️ Warning: OPENAI_API_KEY not set. AI features will not work.");
        }

        _sipTransport = new SIPTransport();

        // Listen on UDP for incoming calls
        var endPoint = new IPEndPoint(IPAddress.Parse(SIP_LISTEN_ADDRESS), SIP_LISTEN_PORT);
        _sipTransport.AddSIPChannel(new SIPUDPChannel(endPoint));

        // Optional: Register with provider
        if (!string.IsNullOrEmpty(SIP_USERNAME) && SIP_USERNAME != "your-sip-username")
        {
            _ = RegisterWithProvider();
        }

        _sipTransport.SIPTransportRequestReceived += OnSipRequestReceived;

        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  Listen: udp:{endPoint}");
        Console.WriteLine($"  Max Concurrent Calls: {MAX_CONCURRENT_CALLS}");
        Console.WriteLine($"  SIP Domain: {SIP_DOMAIN}");
        Console.WriteLine($"  OpenAI Key: {(string.IsNullOrEmpty(OPENAI_API_KEY) ? "NOT SET" : "***configured***")}");
        Console.WriteLine();
        Console.WriteLine("Listening for SIP calls. Press ENTER to quit.");
        Console.ReadLine();

        // Cleanup
        Console.WriteLine("Shutting down...");
        foreach (var kv in _sessions)
        {
            await kv.Value?.Hangup("Server shutdown")!;
        }

        _sipTransport.Shutdown();
        Console.WriteLine("Shutdown complete.");
    }

    /// <summary>
    /// Handle incoming SIP requests.
    /// </summary>
    private static async Task OnSipRequestReceived(
        SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        try
        {
            if (sipRequest.Method != SIPMethodsEnum.INVITE)
            {
                // Let SIPSorcery handle non-INVITE (OPTIONS, etc.)
                return;
            }

            var caller = sipRequest.Header.From?.FriendlyDescription() ?? "Unknown";
            Console.WriteLine($"[SIP] Incoming call from {caller}");

            if (_sessions.Count >= MAX_CONCURRENT_CALLS)
            {
                Console.WriteLine("[SIP] Rejecting call: too many concurrent sessions.");
                var busyResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.BusyHere, "Taxi bot busy");
                await _sipTransport.SendResponseAsync(busyResp);
                return;
            }

            // Create a new user agent for this call
            var ua = new SIPUserAgent(_sipTransport, null);
            var callId = sipRequest.Header.CallId;

            var session = new TaxiBotCallSession(ua, OPENAI_API_KEY);
            session.OnLog += msg => Console.WriteLine(msg);
            session.OnCallFinished += id =>
            {
                _sessions.TryRemove(id, out _);
                Console.WriteLine($"[CALL {id}] Session removed, active = {_sessions.Count}");
            };

            if (!_sessions.TryAdd(callId, session))
            {
                Console.WriteLine("[SIP] Failed to track new session, rejecting.");
                var errResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.ServiceUnavailable, "Session error");
                await _sipTransport.SendResponseAsync(errResp);
                return;
            }

            // Accept the incoming call
            bool answerOk = await session.AcceptIncomingCall(sipRequest);

            if (!answerOk)
            {
                _sessions.TryRemove(callId, out _);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SIP] Error processing request: {ex.Message}");
        }
    }

    /// <summary>
    /// Optional SIP registration with provider.
    /// </summary>
    private static async Task RegisterWithProvider()
    {
        try
        {
            var regUserAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                SIP_USERNAME,
                SIP_PASSWORD,
                SIP_DOMAIN,
                expiry: 300);

            regUserAgent.RegistrationFailed += (uri, resp, msg) =>
                Console.WriteLine($"[SIP REG] Registration failed: {msg}");
            regUserAgent.RegistrationTemporaryFailure += (uri, resp, msg) =>
                Console.WriteLine($"[SIP REG] Registration temp failure: {msg}");
            regUserAgent.RegistrationSuccessful += (uri, resp) =>
                Console.WriteLine("[SIP REG] Registration successful.");

            regUserAgent.Start();
            await Task.Delay(100); // Let registration start
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SIP REG] Error: {ex.Message}");
        }
    }
}
