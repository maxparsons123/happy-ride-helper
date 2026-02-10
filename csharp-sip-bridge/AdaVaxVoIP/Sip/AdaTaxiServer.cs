using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AdaVaxVoIP.Config;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP.Sip;

/// <summary>
/// Ada Taxi VaxVoIP server — extends the SDK wrapper (cVaxServerCOM) to handle
/// incoming calls, OpenAI Realtime integration, and taxi booking function calls.
/// The SDK handles OpenAI WebSocket, audio bridging, and G.711 codec internally.
/// </summary>
public class AdaTaxiServer : cVaxServerCOM
{
    private readonly ILogger _logger;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<ulong, CallInfo> _activeCalls = new();

    public event Action<string>? OnLog;

    public AdaTaxiServer(ILogger logger, AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public bool Start()
    {
        Stop();

        if (!Initialize(""))
        {
            _logger.LogError("VaxVoIP Initialize failed: {Error}", GetVaxErrorText());
            return false;
        }

        // Discover local IP
        var localIp = GetLocalIp();
        Log($"➡ Local IP: {localIp}");

        var listenPort = _settings.VaxVoIP.ListenPort;
        if (!OpenNetworkUDP(localIp, listenPort))
        {
            _logger.LogError("VaxVoIP OpenNetworkUDP failed on port {Port}: {Error}", listenPort, GetVaxErrorText());
            UnInitialize();
            return false;
        }
        Log($"📡 Listening on {localIp}:{listenPort}");

        SetListenPortRangeRTP(_settings.VaxVoIP.RtpPortMin, _settings.VaxVoIP.RtpPortMax);
        AudioSessionLost(true, 30);

        // STUN: discover public IP and set network routes for NAT traversal
        if (_settings.Sip.EnableStun)
        {
            var publicIp = DiscoverPublicIp();
            if (publicIp != null)
            {
                Log($"🌐 Public IP (STUN): {publicIp}");
                AddNetworkRouteSIP(localIp, publicIp);
                AddNetworkRouteRTP(localIp, publicIp);
                Log($"📡 Network routes set: {localIp} → {publicIp}");
            }
            else
            {
                Log("⚠️ STUN discovery failed; NAT traversal may not work");
            }
        }

        // Add anonymous UDP line for accepting calls from any source
        AddLine("AnonymousUDP", VAX_LINE_TYPE_UDP, "", "", "", "", "", "255.255.255.255", -1, "32");

        // Register with external SIP trunk if configured
        RegisterSipTrunkInternal();

        Log($"✅ VaxVoIP Server started on UDP port {listenPort} (RTP {_settings.VaxVoIP.RtpPortMin}-{_settings.VaxVoIP.RtpPortMax})");
        return true;
    }

    /// <summary>
    /// Register/re-register the SIP trunk at runtime (called from UI button or on start).
    /// </summary>
    public void RegisterSipTrunk() => RegisterSipTrunkInternal();

    private void RegisterSipTrunkInternal()
    {
        if (string.IsNullOrWhiteSpace(_settings.Sip.Server) || _settings.Sip.Server == "sip.example.com"
            || string.IsNullOrWhiteSpace(_settings.Sip.Username))
            return;

        var lineType = _settings.Sip.Transport.ToUpperInvariant() switch
        {
            "TCP" => VAX_LINE_TYPE_TCP,
            "TLS" => VAX_LINE_TYPE_TLS,
            _ => VAX_LINE_TYPE_UDP
        };

        // Resolve hostname to IP address for the SDK
        var resolvedServer = ResolveDns(_settings.Sip.Server);

        AddLine("SipTrunk", lineType,
            _settings.Sip.Username, _settings.Sip.Username,
            _settings.Sip.EffectiveAuthUser, _settings.Sip.Password,
            _settings.Sip.Domain ?? _settings.Sip.Server,
            resolvedServer, _settings.Sip.Port, "32");

        RegisterLine("SipTrunk", 3600);
        Log($"📡 Registering with SIP trunk: {_settings.Sip.Server} (resolved → {resolvedServer})");
    }

    /// <summary>
    /// Resolve hostname to IPv4 address. Returns IP as-is if already an IP.
    /// </summary>
    private string ResolveDns(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            Log($"📡 Using IP address directly: {host}");
            return host;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
            {
                Log($"📡 Resolved {host} → {ipv4}");
                return ipv4.ToString();
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ DNS resolution failed for {host}: {ex.Message}");
        }

        Log($"⚠️ Could not resolve {host}, using hostname directly");
        return host;
    }

    /// <summary>
    /// Discover public IP via STUN (RFC 5389) — same implementation as AdaMain.
    /// </summary>
    private string? DiscoverPublicIp()
    {
        try
        {
            var stunServer = _settings.Sip.StunServer ?? "stun.l.google.com";
            var stunPort = _settings.Sip.StunPort > 0 ? _settings.Sip.StunPort : 19302;

            Log($"🌐 Querying STUN: {stunServer}:{stunPort}...");

            using var udp = new UdpClient();
            var stunAddr = Dns.GetHostAddresses(stunServer)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (stunAddr == null)
            {
                Log("⚠️ Could not resolve STUN server");
                return null;
            }

            // STUN Binding Request (RFC 5389)
            var request = new byte[20];
            request[0] = 0x00; request[1] = 0x01; // Binding Request
            request[2] = 0x00; request[3] = 0x00; // Message Length = 0
            // Magic Cookie
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;
            // Transaction ID (12 bytes random)
            Random.Shared.NextBytes(request.AsSpan(8, 12));

            udp.Send(request, request.Length, new IPEndPoint(stunAddr, stunPort));
            udp.Client.ReceiveTimeout = 3000;

            var ep = new IPEndPoint(IPAddress.Any, 0);
            var response = udp.Receive(ref ep);

            return ParseStunResponse(response);
        }
        catch (Exception ex)
        {
            Log($"⚠️ STUN failed: {ex.Message}");
            return null;
        }
    }

    private static string? ParseStunResponse(byte[] response)
    {
        if (response.Length < 20) return null;

        var msgLen = (response[2] << 8) | response[3];
        var offset = 20;

        while (offset + 4 <= response.Length && offset < 20 + msgLen)
        {
            var attrType = (response[offset] << 8) | response[offset + 1];
            var attrLen = (response[offset + 2] << 8) | response[offset + 3];
            offset += 4;

            // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
            if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8)
            {
                var family = response[offset + 1];
                if (family == 0x01) // IPv4
                {
                    byte[] ip = new byte[4];
                    if (attrType == 0x0020)
                    {
                        ip[0] = (byte)(response[offset + 4] ^ 0x21);
                        ip[1] = (byte)(response[offset + 5] ^ 0x12);
                        ip[2] = (byte)(response[offset + 6] ^ 0xA4);
                        ip[3] = (byte)(response[offset + 7] ^ 0x42);
                    }
                    else
                    {
                        Array.Copy(response, offset + 4, ip, 0, 4);
                    }
                    return new IPAddress(ip).ToString();
                }
            }

            offset += attrLen;
            if (attrLen % 4 != 0) offset += 4 - (attrLen % 4);
        }

        return null;
    }

    /// <summary>
    /// Get the local network IP by connecting a UDP socket to the SIP server.
    /// </summary>
    private string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(_settings.Sip.Server, _settings.Sip.Port);
            var localEp = socket.LocalEndPoint as IPEndPoint;
            return localEp?.Address.ToString() ?? "0.0.0.0";
        }
        catch
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
            }
            return "0.0.0.0";
        }
    }

    public void Stop()
    {
        foreach (var call in _activeCalls.Values)
        {
            try { CloseCallSession(call.SessionId); } catch { }
        }
        _activeCalls.Clear();
        UnInitialize();
    }

    #region SDK Event Overrides

    protected override void OnRegisterUser(string sUserName, string sDomain, string sUserAgent, string sFromIP, int nFromPort, ulong nRegId)
    {
        RemoveUser(sUserName);
        AddUser(sUserName, sUserName, "32"); // G.711 A-law + μ-law
        AuthRegister(nRegId);
        Log($"📱 User registered: {sUserName} from {sFromIP}:{nFromPort}");
    }

    protected override void OnRegisterUserSuccess(string sUserName, string sFromIP, int nFromPort, ulong nRegId)
    {
        Log($"✅ Registration OK: {sUserName}");
    }

    protected override void OnRegisterUserFailed(string sUserName, string sFromIP, int nFromPort, ulong nRegId)
    {
        Log($"❌ Registration failed: {sUserName}");
    }

    protected override void OnIncomingCall(ulong nSessionId, string sCallerName, string sCallerId, string sDialNo, int nFromPeerType, string sFromPeerName, string sUserAgent, string sFromIP, int nFromPort)
    {
        Log($"📞 Incoming call from {sCallerId} ({sCallerName}) → {sDialNo}");

        // Register taxi booking function tools
        RegisterTaxiFunctions(nSessionId);

        var prompt = BuildSystemPrompt();

        // The SDK handles OpenAI Realtime connection + audio bridging internally
        AcceptCallSession(nSessionId, 30,
            _settings.OpenAi.ApiKey,
            prompt,
            _settings.OpenAi.Model,
            _settings.OpenAi.Voice,
            1.0);

        _activeCalls[nSessionId] = new CallInfo
        {
            SessionId = nSessionId,
            CallerId = sCallerId,
            CallerName = sCallerName,
            StartTime = DateTime.UtcNow
        };
    }

    protected override void OnCallSessionConnected(ulong nSessionId)
    {
        // Trigger the AI to speak first
        SendInputOpenAI(nSessionId, "The caller has just connected. Greet them warmly and ask how you can help with their taxi booking.");
        Log("✅ Call connected — AI greeting sent");
    }

    protected override void OnVaxFunctionCallOpenAI(ulong nSessionId, string sFuncName, string sCallId, string[] aParamNames, string[] aParamValues)
    {
        Log($"🔧 Function call: {sFuncName}({string.Join(", ", aParamValues ?? Array.Empty<string>())})");

        try
        {
            var result = HandleFunctionCall(nSessionId, sFuncName, aParamNames, aParamValues);
            SendFunctionResultOpenAI(nSessionId, sCallId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Function call error: {Func}", sFuncName);
            SendFunctionResultOpenAI(nSessionId, sCallId, $"Error: {ex.Message}");
        }
    }

    protected override void OnVaxAudioOutputTranscriptOpenAI(ulong nSessionId, string sTranscript)
    {
        Log($"🤖 AI: {sTranscript}");
    }

    protected override void OnVaxAudioInputTranscriptOpenAI(ulong nSessionId, string sTranscript)
    {
        Log($"👤 Caller: {sTranscript}");
    }

    protected override void OnCallSessionHangup(ulong nSessionId)
    {
        if (_activeCalls.TryRemove(nSessionId, out var call))
        {
            var duration = (DateTime.UtcNow - call.StartTime).TotalSeconds;
            Log($"📴 Call ended: {call.CallerId} (duration: {duration:F0}s)");
        }
    }

    protected override void OnCallSessionTimeout(ulong nSessionId)
    {
        _activeCalls.TryRemove(nSessionId, out _);
        Log("⏱ Call session timeout");
    }

    protected override void OnCallSessionFailed(ulong nSessionId, int nStatusCode, string sReasonPhrase, string sContact)
    {
        _activeCalls.TryRemove(nSessionId, out _);
        Log($"❌ Call failed: {nStatusCode} {sReasonPhrase}");
    }

    protected override void OnLineRegisterTrying(string sLineName)
    {
        Log($"📡 SIP trunk registering: {sLineName}...");
    }

    protected override void OnLineRegisterSuccess(string sLineName)
    {
        Log($"✅ SIP trunk registered: {sLineName}");
    }

    protected override void OnLineRegisterFailed(string sLineName, int nStatusCode, string sReasonPhrase)
    {
        Log($"❌ SIP trunk registration failed: {sLineName} — {nStatusCode} {sReasonPhrase}");
    }

    protected override void OnVaxErrorOpenAI(ulong nSessionId, string sMsg)
    {
        Log($"⚠ OpenAI error: {sMsg}");
    }

    protected override void OnCallSessionErrorLog(ulong nSessionId, int nErrorCode, string sErrorMsg)
    {
        Log($"⚠ Session error: {sErrorMsg}");
    }

    protected override void OnVaxErrorLog(string sFuncName, int nErrorCode, string sErrorMsg)
    {
        Log($"⚠ VaxError: {sFuncName} — {sErrorMsg}");
    }

    #endregion

    #region Taxi Booking Logic

    private void RegisterTaxiFunctions(ulong nSessionId)
    {
        AddFunctionOpenAI(nSessionId, "sync_booking_data",
            "Update the current booking state. Call this whenever the caller provides pickup, destination, passenger count, or name.");
        AddFunctionParamOpenAI(nSessionId, "sync_booking_data", "pickup", "Pickup address");
        AddFunctionParamOpenAI(nSessionId, "sync_booking_data", "destination", "Destination address");
        AddFunctionParamOpenAI(nSessionId, "sync_booking_data", "passengers", "Number of passengers (1-8)");
        AddFunctionParamOpenAI(nSessionId, "sync_booking_data", "caller_name", "Caller's name");

        AddFunctionOpenAI(nSessionId, "book_taxi",
            "Confirm and submit the taxi booking. Only call when pickup, destination, and passengers are all confirmed.");

        AddFunctionOpenAI(nSessionId, "end_call",
            "End the call politely after booking is confirmed or caller wants to hang up.");
    }

    private string HandleFunctionCall(ulong nSessionId, string funcName, string[]? paramNames, string[]? paramValues)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (paramNames != null && paramValues != null)
        {
            for (int i = 0; i < Math.Min(paramNames.Length, paramValues.Length); i++)
                args[paramNames[i]] = paramValues[i];
        }

        switch (funcName.ToLowerInvariant())
        {
            case "sync_booking_data":
                if (_activeCalls.TryGetValue(nSessionId, out var call))
                {
                    if (args.TryGetValue("pickup", out var p)) call.Pickup = p;
                    if (args.TryGetValue("destination", out var d)) call.Destination = d;
                    if (args.TryGetValue("passengers", out var n) && int.TryParse(n, out var pax)) call.Passengers = pax;
                    if (args.TryGetValue("caller_name", out var name)) call.CallerName = name;
                }
                return "Booking data updated. Ask the caller to confirm if all details are complete.";

            case "book_taxi":
                if (_activeCalls.TryGetValue(nSessionId, out var booking))
                {
                    Log($"🚕 BOOKING: {booking.CallerName} | {booking.Pickup} → {booking.Destination} | {booking.Passengers} pax");
                    // TODO: dispatch via BSQD webhook / Supabase
                    return $"Taxi booked! Picking up from {booking.Pickup} to {booking.Destination}. Estimated arrival: 5-10 minutes.";
                }
                return "Error: No active booking data found.";

            case "end_call":
                Log("👋 Call ending by AI request");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000); // Let the AI finish speaking
                    CloseCallSession(nSessionId);
                });
                return "Ending call. Goodbye!";

            default:
                return $"Unknown function: {funcName}";
        }
    }

    private string BuildSystemPrompt()
    {
        return $"""
            You are Ada, a friendly and professional AI taxi booking assistant for {_settings.Taxi.CompanyName}.
            You speak naturally with a warm tone. By default respond in English, but if the caller speaks another language, switch to that language.

            Your job:
            1. Greet the caller warmly
            2. Ask for their PICKUP address
            3. Ask for their DESTINATION address  
            4. Ask how many PASSENGERS
            5. Confirm all details and book the taxi

            Use sync_booking_data whenever the caller provides any booking information.
            Use book_taxi only when ALL required fields (pickup, destination, passengers) are confirmed.
            Use end_call after the booking is confirmed and you've said goodbye.

            Keep responses concise and conversational — this is a phone call, not a chat.
            """;
    }

    #endregion

    private void Log(string message)
    {
        _logger.LogInformation(message);
        OnLog?.Invoke(message);
    }

    public int ActiveCallCount => _activeCalls.Count;
}

public class CallInfo
{
    public ulong SessionId { get; set; }
    public string CallerId { get; set; } = "";
    public string CallerName { get; set; } = "";
    public string? Pickup { get; set; }
    public string? Destination { get; set; }
    public int Passengers { get; set; } = 1;
    public DateTime StartTime { get; set; }
}
