using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaxiSipBridge;

/// <summary>
/// Interface for audio AI clients (edge function or direct OpenAI).
/// </summary>
public interface IAudioAIClient : IDisposable
{
    /// <summary>Fired when a log message is generated.</summary>
    event Action<string>? OnLog;
    
    /// <summary>Fired when a transcript (user or AI) is available.</summary>
    event Action<string>? OnTranscript;
    
    /// <summary>Fired when connected to the AI service.</summary>
    event Action? OnConnected;
    
    /// <summary>Fired when disconnected from the AI service.</summary>
    event Action? OnDisconnected;
    
    /// <summary>Fired when the AI is speaking (transcript delta).</summary>
    event Action<string>? OnAdaSpeaking;
    
    /// <summary>Fired when PCM24 audio is received from the AI.</summary>
    event Action<byte[]>? OnPcm24Audio;
    
    /// <summary>Fired when a new AI response starts (for fade-in reset).</summary>
    event Action? OnResponseStarted;
    
    /// <summary>Whether the client is currently connected.</summary>
    bool IsConnected { get; }
    
    /// <summary>Connect to the AI service.</summary>
    Task ConnectAsync(string? caller = null, CancellationToken ct = default);
    
    /// <summary>Send µ-law audio (will be decoded and resampled to 24kHz).</summary>
    Task SendMuLawAsync(byte[] ulawData);
    
    /// <summary>Send raw PCM audio at the specified sample rate.</summary>
    Task SendAudioAsync(byte[] pcmData, int sampleRate = 24000);
    
    /// <summary>Disconnect from the AI service.</summary>
    Task DisconnectAsync();
    
    /// <summary>Get the next µ-law frame from the outbound queue (for RTP).</summary>
    byte[]? GetNextMuLawFrame();
    
    /// <summary>Number of pending frames in the outbound queue.</summary>
    int PendingFrameCount { get; }
    
    /// <summary>Clear all pending outbound frames.</summary>
    void ClearPendingFrames();
}
