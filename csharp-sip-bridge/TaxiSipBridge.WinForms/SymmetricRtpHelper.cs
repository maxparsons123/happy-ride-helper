using System;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Helper class to enable symmetric RTP NAT traversal on any RTPSession.
/// Call EnableSymmetricRtp() once after creating the session to punch through NAT.
/// </summary>
public class SymmetricRtpHelper : IDisposable
{
    private readonly RTPSession _rtpSession;
    private IPEndPoint? _lastRemoteEndpoint;
    private bool _disposed;

    public event Action<string>? OnLog;

    /// <summary>
    /// Gets whether symmetric RTP has locked onto a remote endpoint.
    /// </summary>
    public bool IsLocked => _lastRemoteEndpoint != null;

    /// <summary>
    /// Gets the current remote endpoint (after NAT discovery).
    /// </summary>
    public IPEndPoint? RemoteEndpoint => _lastRemoteEndpoint;

    public SymmetricRtpHelper(RTPSession rtpSession)
    {
        _rtpSession = rtpSession ?? throw new ArgumentNullException(nameof(rtpSession));
    }

    /// <summary>
    /// Enable symmetric RTP on the session. Call this once after creating the session.
    /// </summary>
    public void EnableSymmetricRtp()
    {
        // Accept audio from any address (critical for NAT)
        _rtpSession.AcceptRtpFromAny = true;

        // Subscribe to inbound RTP to implement symmetric NAT traversal
        _rtpSession.OnRtpPacketReceived += OnRtpPacketReceived;

        Log("✅ Symmetric RTP enabled (AcceptRtpFromAny + endpoint tracking)");
    }

    /// <summary>
    /// Symmetric RTP handler: dynamically update remote endpoint based on where packets arrive from.
    /// This punches through NAT by sending audio back to the actual source address.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (mediaType != SDPMediaTypesEnum.audio) return;

        // Check if remote endpoint changed (NAT rebinding or initial discovery)
        if (_lastRemoteEndpoint == null || !_lastRemoteEndpoint.Equals(remoteEndPoint))
        {
            bool isFirst = _lastRemoteEndpoint == null;
            _lastRemoteEndpoint = remoteEndPoint;

            // Update session's destination to match actual source (symmetric RTP)
            try
            {
                _rtpSession.SetDestination(SDPMediaTypesEnum.audio, remoteEndPoint, remoteEndPoint);

                if (isFirst)
                    Log($"🔄 NAT: Symmetric RTP locked → {remoteEndPoint}");
                else
                    Log($"🔄 NAT: Endpoint rebind → {remoteEndPoint}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ NAT endpoint update failed: {ex.Message}");
            }
        }
    }

    private void Log(string msg) => OnLog?.Invoke($"[SymmetricRtp] {msg}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _rtpSession.OnRtpPacketReceived -= OnRtpPacketReceived;
    }
}
