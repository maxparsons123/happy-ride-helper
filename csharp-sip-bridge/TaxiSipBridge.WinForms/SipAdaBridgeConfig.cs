namespace TaxiSipBridge;

public enum SipTransportType 
{ 
    UDP, 
    TCP 
}

/// <summary>
/// Audio processing modes for testing different approaches to reduce crackling.
/// </summary>
public enum AudioMode
{
    /// <summary>
    /// Standard 20ms timer with direct queue consumption (default).
    /// </summary>
    Standard,
    
    /// <summary>
    /// Pre-buffer 60ms of audio before starting playback to smooth timing.
    /// </summary>
    JitterBuffer,
    
    /// <summary>
    /// Use SIPSorcery's built-in AudioExtrasSource with timed sending.
    /// </summary>
    BuiltInPacer,
    
    /// <summary>
    /// Simple linear interpolation only (no fancy filters).
    /// </summary>
    SimpleResample,
    
    /// <summary>
    /// Test mode: 440Hz sine wave to verify RTP/codec path.
    /// </summary>
    TestTone,
    
    /// <summary>
    /// Bypass resampling - send 24kHz directly (for testing only).
    /// WARNING: Will sound wrong if codec is 8kHz!
    /// </summary>
    Passthrough
}

public class SipAdaBridgeConfig
{
    public string SipServer { get; set; } = "206.189.123.28";
    public int SipPort { get; set; } = 5060;
    public string SipUser { get; set; } = "max201";
    public string SipPassword { get; set; } = "qwe70954504118";
    // IMPORTANT: WebSocket routing is more reliable via the ".functions.supabase.co" host.
    public string AdaWsUrl { get; set; } = "wss://oerketnvlmptpfvttysy.functions.supabase.co/functions/v1/taxi-realtime-desktop";
    public SipTransportType Transport { get; set; } = SipTransportType.UDP;
    public AudioMode AudioMode { get; set; } = AudioMode.Standard;
    public int JitterBufferMs { get; set; } = 60; // Pre-buffer size for JitterBuffer mode

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
