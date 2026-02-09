using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AdaVaxVoIP.Audio;
using AdaVaxVoIP.Config;
using Microsoft.Extensions.Logging;

namespace AdaVaxVoIP.Sip;

/// <summary>
/// VaxVoIP-based SIP Server with G.711 A-law support.
/// Handles registration, call lifecycle, and RTP audio bridging.
/// 
/// NOTE: VaxTeleServerSDK is a COM component — requires the VaxVoIP SDK installed on Windows.
/// The COM reference in the .csproj must point to the installed TLB/DLL.
/// </summary>
public sealed class VaxVoIPSipServer : IAsyncDisposable
{
    private readonly ILogger<VaxVoIPSipServer> _logger;
    private readonly VaxVoIPSettings _settings;
    private readonly TaxiBookingSettings _taxiSettings;

    // VaxVoIP COM component — type will resolve when SDK is installed
    private dynamic? _vaxServer;
    private bool _initialized;

    private readonly ConcurrentDictionary<string, ActiveCall> _activeCalls = new();
    private readonly ConcurrentDictionary<int, string> _lineIdToCallId = new();
    private volatile bool _disposed;
    private volatile bool _running;

    // Audio output channel (AI → caller)
    private readonly Channel<AudioFrame> _audioOutputChannel;

    public event Action<string, string>? OnCallStarted;   // callId, callerId
    public event Action<string>? OnCallEnded;             // callId
    public event Action<string, byte[]>? OnAudioReceived; // callId, alawData

    public VaxVoIPSipServer(
        ILogger<VaxVoIPSipServer> logger,
        VaxVoIPSettings settings,
        TaxiBookingSettings taxiSettings)
    {
        _logger = logger;
        _settings = settings;
        _taxiSettings = taxiSettings;

        _audioOutputChannel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        _logger.LogInformation("🚀 Initializing VaxVoIP SIP Server...");

        try
        {
            // Create VaxVoIP COM component
            var vaxType = Type.GetTypeFromProgID("VaxTeleServer.VaxTeleServer");
            if (vaxType == null)
                throw new InvalidOperationException("VaxTeleServer COM component not found. Install the VaxVoIP SDK.");

            _vaxServer = Activator.CreateInstance(vaxType)
                ?? throw new InvalidOperationException("Failed to create VaxTeleServer instance");

            // Set license key
            if (!_vaxServer.SetLicenseKey(_settings.LicenseKey))
                throw new InvalidOperationException("Failed to set VaxVoIP license key");

            // Initialize with domain realm
            if (!_vaxServer.Initialize(_settings.DomainRealm))
            {
                int errorCode = _vaxServer.GetVaxErrorCode();
                throw new InvalidOperationException($"VaxVoIP Initialize failed: error {errorCode}");
            }

            // Configure network
            var localIp = GetLocalIPAddress();
            if (!_vaxServer.OpenNetworkUDP(localIp, _settings.SipPort))
            {
                int errorCode = _vaxServer.GetVaxErrorCode();
                throw new InvalidOperationException($"VaxVoIP OpenNetworkUDP failed: error {errorCode}");
            }

            // RTP port range
            _vaxServer.SetListenPortRangeRTP(_settings.RtpPortMin, _settings.RtpPortMax);

            // Add default user
            _vaxServer.AddUser("taxi", "taxi123", "taxi", "taxi");

            // Subscribe to events
            _vaxServer.OnIncomingCall += new Action<string, string, string, string, string, string, int>(OnVaxIncomingCall);
            _vaxServer.OnCallConnected += new Action<int, string, string, string>(OnVaxCallConnected);
            _vaxServer.OnCallEnded += new Action<int, string, string, string>(OnVaxCallEndedHandler);
            _vaxServer.OnCallAudioData += new Action<int, IntPtr, int, int, int>(OnVaxAudioData);

            _initialized = true;
            _running = true;

            // Start audio output processing
            _ = ProcessAudioOutputAsync(ct);

            _logger.LogInformation("✅ VaxVoIP SIP Server started on {Ip}:{Port}", localIp, _settings.SipPort);
        }
        catch (COMException ex)
        {
            _logger.LogError(ex, "VaxVoIP COM error during startup");
            throw;
        }
    }

    #region VaxVoIP Event Handlers

    private void OnVaxIncomingCall(string callerDisplayName, string callerUserName,
        string callerDomain, string calleeDisplayName, string calleeUserName,
        string calleeDomain, int lineId)
    {
        var callId = Guid.NewGuid().ToString("N")[..8];
        var callerId = $"{callerUserName}@{callerDomain}";

        _logger.LogInformation("📞 Incoming call from {Caller} on line {Line}", callerId, lineId);

        if (_taxiSettings.AutoAnswer)
            _vaxServer?.AcceptCall(lineId);

        var activeCall = new ActiveCall
        {
            CallId = callId,
            CallerId = callerId,
            LineId = lineId,
            StartTime = DateTime.UtcNow
        };

        _activeCalls[callId] = activeCall;
        _lineIdToCallId[lineId] = callId;

        OnCallStarted?.Invoke(callId, callerId);
    }

    private void OnVaxCallConnected(int lineId, string callerDisplayName,
        string callerUserName, string callerDomain)
    {
        if (!_lineIdToCallId.TryGetValue(lineId, out var callId)) return;
        _logger.LogInformation("✅ Call {CallId} connected", callId);

        if (_settings.EnableRecording)
        {
            var filename = Path.Combine(_settings.RecordingsPath,
                $"{callId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.wav");
            try { Directory.CreateDirectory(_settings.RecordingsPath); } catch { }
            _vaxServer?.StartRecording(lineId, filename);
        }

        // Set codec to G.711 A-law
        try { _vaxServer?.SetAudioCodec(lineId, 8); } catch { } // 8 = A-law
    }

    private void OnVaxCallEndedHandler(int lineId, string callerDisplayName,
        string callerUserName, string callerDomain)
    {
        if (!_lineIdToCallId.TryGetValue(lineId, out var callId)) return;

        if (_activeCalls.TryRemove(callId, out var call))
        {
            _lineIdToCallId.TryRemove(lineId, out _);
            _logger.LogInformation("📴 Call {CallId} ended (duration: {Duration}s)",
                callId, (DateTime.UtcNow - call.StartTime).TotalSeconds);
            OnCallEnded?.Invoke(callId);
        }
    }

    private void OnVaxAudioData(int lineId, IntPtr pcmDataPtr, int pcmDataLength,
        int sampleRate, int channels)
    {
        if (!_lineIdToCallId.TryGetValue(lineId, out var callId)) return;

        byte[] pcmData = new byte[pcmDataLength];
        Marshal.Copy(pcmDataPtr, pcmData, 0, pcmDataLength);

        // Convert VaxVoIP PCM to A-law for OpenAI
        byte[] alawData = G711Codec.PcmToALaw(pcmData);

        OnAudioReceived?.Invoke(callId, alawData);
    }

    #endregion

    #region Audio Output

    private async Task ProcessAudioOutputAsync(CancellationToken ct)
    {
        await foreach (var frame in _audioOutputChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (_activeCalls.TryGetValue(frame.CallId, out var call))
                {
                    byte[] pcmData = G711Codec.ALawToPcm(frame.Data);
                    _vaxServer?.SendAudioData(call.LineId, pcmData, pcmData.Length, 8000, 1);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Audio output error"); }
        }
    }

    public void SendAudio(string callId, byte[] alawData)
    {
        if (_disposed || alawData == null || alawData.Length == 0) return;
        _audioOutputChannel.Writer.TryWrite(new AudioFrame { CallId = callId, Data = alawData });
    }

    public void Hangup(string callId)
    {
        if (_activeCalls.TryGetValue(callId, out var call))
            _vaxServer?.EndCall(call.LineId);
    }

    #endregion

    private static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;

        foreach (var call in _activeCalls.Values)
            try { _vaxServer?.EndCall(call.LineId); } catch { }
        _activeCalls.Clear();

        _audioOutputChannel.Writer.Complete();

        if (_vaxServer != null)
        {
            try { _vaxServer.UnInitialize(); } catch { }
            Marshal.ReleaseComObject(_vaxServer);
            _vaxServer = null;
        }

        _logger.LogInformation("✅ VaxVoIP SIP Server stopped");
    }
}

public struct AudioFrame
{
    public string CallId { get; set; }
    public byte[] Data { get; set; }
}

public class ActiveCall
{
    public string CallId { get; set; } = "";
    public string CallerId { get; set; } = "";
    public int LineId { get; set; }
    public DateTime StartTime { get; set; }
}
