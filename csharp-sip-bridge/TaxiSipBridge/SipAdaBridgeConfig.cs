using System;

namespace TaxiSipBridge;

/// <summary>
/// Transport protocol for SIP connections.
/// </summary>
public enum SipTransportType
{
    UDP,
    TCP
}

/// <summary>
/// Audio processing mode.
/// </summary>
public enum AudioMode
{
    Standard,
    JitterBuffer,
    BuiltInPacer,
    SimpleResample,
    TestTone,
    /// <summary>
    /// Bypass resampling - send 24kHz directly (for testing only).
    /// WARNING: Will sound wrong if codec is 8kHz!
    /// </summary>
    Passthrough
}

/// <summary>
/// Configuration for the SIP-to-Ada bridge.
/// </summary>
public class SipAdaBridgeConfig
{
    /// <summary>
    /// SIP server hostname or IP address.
    /// </summary>
    public string SipServer { get; set; } = "206.189.123.28";

    /// <summary>
    /// SIP server port (typically 5060 for UDP/TCP, 5061 for TLS).
    /// </summary>
    public int SipPort { get; set; } = 5060;

    /// <summary>
    /// SIP username for registration.
    /// </summary>
    public string SipUser { get; set; } = "max201";

    /// <summary>
    /// SIP password for registration.
    /// </summary>
    public string SipPassword { get; set; } = "qwe70954504118";

    /// <summary>
    /// Transport protocol (UDP or TCP).
    /// </summary>
    public SipTransportType Transport { get; set; } = SipTransportType.UDP;

    /// <summary>
    /// Audio processing mode.
    /// </summary>
    public AudioMode AudioMode { get; set; } = AudioMode.Standard;

    /// <summary>
    /// Jitter buffer size in milliseconds.
    /// </summary>
    public int JitterBufferMs { get; set; } = 60;

    /// <summary>
    /// Maximum concurrent calls.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 50;

    /// <summary>
    /// WebSocket URL for Ada AI connection.
    /// Use taxi-realtime-paired for full Ada AI (OpenAI Realtime API).
    /// IMPORTANT: Use .functions.supabase.co subdomain for reliability.
    /// </summary>
    public string AdaWsUrl { get; set; } = "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-paired";

    /// <summary>
    /// Validate the configuration.
    /// </summary>
    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(SipServer))
        {
            error = "SIP server is required";
            return false;
        }

        if (SipPort <= 0 || SipPort > 65535)
        {
            error = "Invalid SIP port";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SipUser))
        {
            error = "SIP username is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AdaWsUrl))
        {
            error = "Ada WebSocket URL is required";
            return false;
        }

        if (!AdaWsUrl.StartsWith("wss://") && !AdaWsUrl.StartsWith("ws://"))
        {
            error = "Ada WebSocket URL must start with ws:// or wss://";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
