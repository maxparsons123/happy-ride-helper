using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AdaCleanVersion.Config;
using AdaCleanVersion.Engine;
using AdaCleanVersion.Models;
using AdaCleanVersion.Services;
using AdaCleanVersion.Session;
using Microsoft.Extensions.Logging;

namespace AdaCleanVersion.Sip;

/// <summary>
/// SIP bridge that connects VaxVoIP SDK events to CleanCallSession.
/// 
/// Architecture:
/// - OnIncomingCall → creates CleanCallSession
/// - AI transcript (caller) → feeds ProcessCallerResponseAsync (raw slot storage)
/// - OnAiInstruction → sends instruction to OpenAI Realtime as system message
/// - No AI tools registered — AI is voice-only
/// - Single extraction pass when all slots collected
/// </summary>
public class CleanSipBridge : cVaxServerCOM
{
    private readonly ILogger _logger;
    private readonly CleanAppSettings _settings;
    private readonly IExtractionService _extractionService;
    private readonly FareGeocodingService _fareService;
    private readonly CallerLookupService _callerLookup;
    private readonly ConcurrentDictionary<ulong, ActiveCall> _activeCalls = new();

    public event Action<string>? OnLog;

    public CleanSipBridge(ILogger logger, CleanAppSettings settings,
        IExtractionService extractionService, FareGeocodingService fareService,
        CallerLookupService callerLookup)
    {
        _logger = logger;
        _settings = settings;
        _extractionService = extractionService;
        _fareService = fareService;
        _callerLookup = callerLookup;
    }

    // ─── Lifecycle ──────────────────────────────────────────

    public bool Start()
    {
        Stop();

        if (!Initialize(""))
        {
            Log($"❌ VaxVoIP Initialize failed: {GetVaxErrorText()}");
            return false;
        }

        var localIp = GetLocalIp();
        Log($"➡ Local IP: {localIp}");

        var port = _settings.VaxVoIP.ListenPort;
        if (!OpenNetworkUDP(localIp, port))
        {
            Log($"❌ OpenNetworkUDP failed on {port}: {GetVaxErrorText()}");
            UnInitialize();
            return false;
        }

        SetListenPortRangeRTP(_settings.VaxVoIP.RtpPortMin, _settings.VaxVoIP.RtpPortMax);
        AudioSessionLost(true, 30);

        // STUN for NAT traversal
        if (_settings.Sip.EnableStun)
        {
            var publicIp = DiscoverPublicIp();
            if (publicIp != null)
            {
                AddNetworkRouteSIP(localIp, publicIp);
                AddNetworkRouteRTP(localIp, publicIp);
                Log($"🌐 NAT routes: {localIp} → {publicIp}");
            }
        }

        // Anonymous line for accepting calls from any source
        AddLine("AnonymousUDP", VAX_LINE_TYPE_UDP, "", "", "", "", "", "255.255.255.255", -1, "32");

        // SIP trunk registration
        RegisterTrunk();

        Log($"✅ CleanSipBridge started on UDP {port}");
        return true;
    }

    public void Stop()
    {
        foreach (var call in _activeCalls.Values)
        {
            try { CloseCallSession(call.VaxSessionId); } catch { }
        }
        _activeCalls.Clear();
        UnInitialize();
    }

    // ─── SDK Event Overrides ────────────────────────────────

    protected override void OnRegisterUser(string sUserName, string sDomain, string sUserAgent, string sFromIP, int nFromPort, ulong nRegId)
    {
        RemoveUser(sUserName);
        AddUser(sUserName, sUserName, "32");
        AuthRegister(nRegId);
        Log($"📱 User registered: {sUserName} from {sFromIP}:{nFromPort}");
    }

    protected override void OnIncomingCall(ulong nSessionId, string sCallerName, string sCallerId,
        string sDialNo, int nFromPeerType, string sFromPeerName, string sUserAgent, string sFromIP, int nFromPort)
    {
        Log($"📞 Incoming: {sCallerId} ({sCallerName}) → {sDialNo}");

        // Fire async caller lookup, then create session
        _ = Task.Run(async () =>
        {
            try
            {
                await SetupCallWithLookupAsync(nSessionId, sCallerId, sCallerName);
            }
            catch (Exception ex)
            {
                Log($"⚠ Call setup failed: {ex.Message}");
            }
        });
    }

    private async Task SetupCallWithLookupAsync(ulong nSessionId, string callerId, string callerName)
    {
        // Look up returning caller from callers table
        var context = await _callerLookup.LookupAsync(callerId);

        if (context.IsReturningCaller)
        {
            Log($"👤 Returning caller: {context.CallerName} ({context.TotalBookings} bookings, " +
                $"last pickup: {context.LastPickup ?? "none"}, " +
                $"aliases: {(context.AddressAliases != null ? string.Join(", ", context.AddressAliases.Keys) : "none")})");
        }
        else
        {
            Log($"👤 New caller: {callerId}");
        }

        // Create clean session with enriched context
        var session = new CleanCallSession(
            sessionId: nSessionId.ToString(),
            callerId: callerId,
            companyName: _settings.Taxi.CompanyName,
            extractionService: _extractionService,
            fareService: _fareService,
            callerContext: context
        );

        session.OnLog += msg => Log(msg);

        session.OnAiInstruction += instruction =>
        {
            Log($"📋 Instruction: {instruction}");
            SendInputOpenAI(nSessionId, instruction);
        };

        session.OnBookingReady += booking =>
        {
            Log($"🚕 BOOKING READY: {booking.CallerName} | {booking.Pickup.DisplayName} → {booking.Destination.DisplayName} | {booking.Passengers} pax | {booking.PickupTime}");
        };

        session.OnFareReady += fare =>
        {
            Log($"💰 FARE READY: {fare.Fare} ({fare.DistanceMiles:F1}mi) | ETA: {fare.DriverEta} | Zone: {fare.ZoneName ?? "none"} | Busy: {fare.BusyLevel}");
        };

        var activeCall = new ActiveCall
        {
            VaxSessionId = nSessionId,
            Session = session,
            CallerId = callerId,
            StartTime = DateTime.UtcNow
        };
        _activeCalls[nSessionId] = activeCall;

        // Accept call — AI has NO tools, just system prompt + voice
        var systemPrompt = session.GetSystemPrompt();
        AcceptCallSession(nSessionId, 30,
            _settings.OpenAi.ApiKey,
            systemPrompt,
            _settings.OpenAi.Model,
            _settings.OpenAi.Voice,
            1.0);
    }

    protected override void OnCallSessionConnected(ulong nSessionId)
    {
        if (_activeCalls.TryGetValue(nSessionId, out var call))
        {
            // Start the deterministic engine — sends greeting instruction
            call.Session.Start();
            Log("✅ Call connected — engine started");
        }
    }

    /// <summary>
    /// Caller's speech transcript — feed directly to session as raw slot value.
    /// The engine decides which slot this belongs to based on current state.
    /// </summary>
    protected override void OnVaxAudioInputTranscriptOpenAI(ulong nSessionId, string sTranscript)
    {
        Log($"👤 Caller: {sTranscript}");

        if (_activeCalls.TryGetValue(nSessionId, out var call))
        {
            // Fire-and-forget async processing
            _ = Task.Run(async () =>
            {
                try
                {
                    await call.Session.ProcessCallerResponseAsync(sTranscript);
                }
                catch (Exception ex)
                {
                    Log($"⚠ Error processing transcript: {ex.Message}");
                }
            });
        }
    }

    /// <summary>
    /// AI's spoken response — log only, no state mutation.
    /// </summary>
    protected override void OnVaxAudioOutputTranscriptOpenAI(ulong nSessionId, string sTranscript)
    {
        Log($"🤖 AI: {sTranscript}");
    }

    /// <summary>
    /// No function calls expected — AI has no tools in clean architecture.
    /// Log as warning if it somehow fires.
    /// </summary>
    protected override void OnVaxFunctionCallOpenAI(ulong nSessionId, string sFuncName, string sCallId,
        string[] aParamNames, string[] aParamValues)
    {
        Log($"⚠ UNEXPECTED function call from AI: {sFuncName} — AI should have NO tools!");
        SendFunctionResultOpenAI(nSessionId, sCallId, "Error: No tools available.");
    }

    protected override void OnCallSessionHangup(ulong nSessionId)
    {
        if (_activeCalls.TryRemove(nSessionId, out var call))
        {
            var duration = (DateTime.UtcNow - call.StartTime).TotalSeconds;
            Log($"📴 Call ended: {call.CallerId} ({duration:F0}s)");
            call.Session.EndCall();
        }
    }

    protected override void OnCallSessionTimeout(ulong nSessionId)
    {
        _activeCalls.TryRemove(nSessionId, out _);
        Log("⏱ Session timeout");
    }

    protected override void OnCallSessionFailed(ulong nSessionId, int nStatusCode, string sReasonPhrase, string sContact)
    {
        _activeCalls.TryRemove(nSessionId, out _);
        Log($"❌ Call failed: {nStatusCode} {sReasonPhrase}");
    }

    protected override void OnLineRegisterSuccess(string sLineName) => Log($"✅ Trunk registered: {sLineName}");
    protected override void OnLineRegisterFailed(string sLineName, int nStatusCode, string sReasonPhrase) =>
        Log($"❌ Trunk failed: {sLineName} — {nStatusCode} {sReasonPhrase}");

    protected override void OnVaxErrorOpenAI(ulong nSessionId, string sMsg) => Log($"⚠ OpenAI: {sMsg}");
    protected override void OnVaxErrorLog(string sFuncName, int nErrorCode, string sErrorMsg) =>
        Log($"⚠ VaxError: {sFuncName} — {sErrorMsg}");

    // ─── Infrastructure ─────────────────────────────────────

    private void RegisterTrunk()
    {
        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || _settings.Sip.Server == "sip.example.com")
            return;

        var lineType = _settings.Sip.Transport.ToUpperInvariant() switch
        {
            "TCP" => VAX_LINE_TYPE_TCP,
            "TLS" => VAX_LINE_TYPE_TLS,
            _ => VAX_LINE_TYPE_UDP
        };

        var resolved = ResolveDns(_settings.Sip.Server);
        AddLine("SipTrunk", lineType,
            _settings.Sip.Username, _settings.Sip.Username,
            _settings.Sip.EffectiveAuthUser, _settings.Sip.Password,
            _settings.Sip.Domain ?? _settings.Sip.Server,
            resolved, _settings.Sip.Port, "32");

        RegisterLine("SipTrunk", 3600);
        Log($"📡 Registering trunk: {_settings.Sip.Server} → {resolved}");
    }

    private string ResolveDns(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try
        {
            var addr = Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (addr != null) { Log($"DNS: {host} → {addr}"); return addr.ToString(); }
        }
        catch (Exception ex) { Log($"⚠ DNS failed for {host}: {ex.Message}"); }
        return host;
    }

    private string? DiscoverPublicIp()
    {
        try
        {
            var stunServer = _settings.Sip.StunServer ?? "stun.l.google.com";
            var stunPort = _settings.Sip.StunPort > 0 ? _settings.Sip.StunPort : 19302;
            using var udp = new UdpClient();
            var addr = Dns.GetHostAddresses(stunServer)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (addr == null) return null;

            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            Random.Shared.NextBytes(req.AsSpan(8, 12));

            udp.Send(req, req.Length, new IPEndPoint(addr, stunPort));
            udp.Client.ReceiveTimeout = 3000;
            var ep = new IPEndPoint(IPAddress.Any, 0);
            var resp = udp.Receive(ref ep);

            return ParseStunResponse(resp);
        }
        catch { return null; }
    }

    private static string? ParseStunResponse(byte[] r)
    {
        if (r.Length < 20) return null;
        var msgLen = (r[2] << 8) | r[3];
        var off = 20;
        while (off + 4 <= r.Length && off < 20 + msgLen)
        {
            var t = (r[off] << 8) | r[off + 1];
            var l = (r[off + 2] << 8) | r[off + 3];
            off += 4;
            if ((t == 0x0020 || t == 0x0001) && l >= 8 && r[off + 1] == 0x01)
            {
                var ip = new byte[4];
                if (t == 0x0020)
                { ip[0] = (byte)(r[off + 4] ^ 0x21); ip[1] = (byte)(r[off + 5] ^ 0x12); ip[2] = (byte)(r[off + 6] ^ 0xA4); ip[3] = (byte)(r[off + 7] ^ 0x42); }
                else Array.Copy(r, off + 4, ip, 0, 4);
                return new IPAddress(ip).ToString();
            }
            off += l;
            if (l % 4 != 0) off += 4 - (l % 4);
        }
        return null;
    }

    private string GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(_settings.Sip.Server, _settings.Sip.Port);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";
        }
        catch
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                ?.ToString() ?? "0.0.0.0";
        }
    }

    private void Log(string msg) { _logger.LogInformation(msg); OnLog?.Invoke(msg); }
    public int ActiveCallCount => _activeCalls.Count;
}

/// <summary>
/// Tracks a live call — maps VaxVoIP session ID to CleanCallSession.
/// </summary>
internal class ActiveCall
{
    public ulong VaxSessionId { get; init; }
    public required CleanCallSession Session { get; init; }
    public string CallerId { get; init; } = "";
    public DateTime StartTime { get; init; }
}
