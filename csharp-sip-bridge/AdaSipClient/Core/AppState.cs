namespace AdaSipClient.Core;

/// <summary>
/// Centralised application state. All UI panels read/write through this
/// so business logic stays out of the form code.
/// </summary>
public sealed class AppState
{
    // ── SIP credentials ──
    public string SipServer { get; set; } = "";
    public int SipPort { get; set; } = 5060;
    public string SipUser { get; set; } = "";
    public string SipPassword { get; set; } = "";
    public string Transport { get; set; } = "UDP";

    // ── Call mode ──
    public CallMode Mode { get; set; } = CallMode.AutoBot;
    public bool IsRegistered { get; set; }
    public bool IsInCall { get; set; }
    public string? ActiveCallId { get; set; }
    public string? CallerNumber { get; set; }

    // ── Audio ──
    public int InputVolumePercent { get; set; } = 100;
    public int OutputVolumePercent { get; set; } = 100;

    // ── Simli ──
    public string SimliApiKey { get; set; } = "";
    public string SimliFaceId { get; set; } = "";
    public bool SimliEnabled { get; set; }

    // ── OpenAI / Bot ──
    public string OpenAiApiKey { get; set; } = "";
    public string WebSocketUrl { get; set; } = "";

    // ── Events ──
    public event Action? StateChanged;
    public void NotifyChanged() => StateChanged?.Invoke();
}

public enum CallMode
{
    /// <summary>Auto-answer and route to AI bot (G.711 passthrough)</summary>
    AutoBot,

    /// <summary>Auto-answer with live mic/speaker (manual operator)</summary>
    ManualListen
}
