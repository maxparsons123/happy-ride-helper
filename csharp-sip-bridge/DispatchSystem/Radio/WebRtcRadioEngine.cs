using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace DispatchSystem.Radio;

/// <summary>
/// Manages WebRTC peer connections for PTT radio.
/// Uses MQTT as the signaling channel (SDP offers/answers + ICE candidates).
/// 
/// MQTT Signaling Topics:
///   radio/webrtc/signal/{peerId}   — SDP + ICE messages to a specific peer
///   radio/webrtc/presence          — announce/discover peers
/// </summary>
public class WebRtcRadioEngine : IDisposable
{
    private readonly string _localId = "DISPATCH";
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _peers = new();
    private readonly ConcurrentDictionary<string, bool> _makingOffer = new();
    private bool _pttActive;
    private HashSet<string>? _pttTargets;
    private bool _disposed;

    // Audio capture
    private NAudio.Wave.WaveInEvent? _waveIn;
    private readonly object _captureLock = new();

    // Audio playback
    private NAudio.Wave.WaveOutEvent? _waveOut;
    private NAudio.Wave.BufferedWaveProvider? _playBuffer;
    private float _volume = 0.8f;

    // Opus codec
    private OpusEncoder? _opusEncoder;
    private OpusDecoder? _opusDecoder;
    private const int OPUS_SAMPLE_RATE = 48000;
    private const int OPUS_CHANNELS = 1;        // Actual encoder/decoder channels (mono)
    private const int OPUS_SDP_CHANNELS = 2;    // WebRTC spec mandates opus/48000/2 in SDP negotiation
    private const int OPUS_FRAME_MS = 20;
    private const int OPUS_FRAME_SIZE = OPUS_SAMPLE_RATE * OPUS_FRAME_MS / 1000; // 960 samples

    /// <summary>Fires when a signaling message should be published via MQTT. Args: (topic, jsonPayload)</summary>
    public event Action<string, string>? OnSignalingSend;

    /// <summary>Fires log entries. Args: (message, isError)</summary>
    public event Action<string, bool>? OnLog;

    /// <summary>Fires when peer count changes.</summary>
    public event Action<int>? OnPeerCountChanged;

    public int PeerCount => _peers.Count;

    public WebRtcRadioEngine()
    {
        _opusEncoder = new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
        _opusEncoder.Bitrate = 48000;        // 48kbps for clear voice (was 24kbps)
        _opusEncoder.Complexity = 10;         // Max quality encoding
        _opusEncoder.UseInbandFEC = true;     // Forward error correction for packet loss
        _opusEncoder.PacketLossPercentage = 5; // Hint for FEC redundancy
        _opusDecoder = new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS);
        InitPlayback();
    }

    // ═══════════════════════════════════════════
    //  PRESENCE
    // ═══════════════════════════════════════════

    /// <summary>Announce this dispatcher on the presence topic.</summary>
    public void AnnouncePresence()
    {
        var msg = JsonSerializer.Serialize(new
        {
            type = "join",
            peerId = _localId,
            name = "Dispatch",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        OnSignalingSend?.Invoke("radio/webrtc/presence", msg);
    }

    /// <summary>Handle an incoming presence message.</summary>
    public void HandlePresence(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var remotePeerId = root.TryGetProperty("peerId", out var pid) ? pid.GetString() ?? "" : "";
            // Fallback for legacy "from" field
            if (string.IsNullOrEmpty(remotePeerId))
                remotePeerId = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
            if (remotePeerId == _localId || string.IsNullOrEmpty(remotePeerId)) return;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "join";

            if ((type == "join" || type == "here") && !_peers.ContainsKey(remotePeerId))
            {
                // Polite peer: only initiate if our ID is lexicographically lower
                var shouldInitiate = string.Compare(_localId, remotePeerId, StringComparison.Ordinal) < 0;
                if (shouldInitiate)
                {
                    OnLog?.Invoke($"📡 Peer {remotePeerId} announced — initiating connection (we are polite)", false);
                    _ = Task.Run(() => CreatePeerConnection(remotePeerId, isInitiator: true));
                }
                else
                {
                    OnLog?.Invoke($"📡 Peer {remotePeerId} announced — waiting for their offer (they initiate)", false);
                }
            }

            // Reply with "here" when someone joins
            if (type == "join")
            {
                var reply = JsonSerializer.Serialize(new
                {
                    type = "here",
                    peerId = _localId,
                    name = "Dispatch",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                OnSignalingSend?.Invoke("radio/webrtc/presence", reply);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Presence parse error: {ex.Message} | Raw: {(json.Length > 200 ? json[..200] : json)}", true);
        }
    }

    // ═══════════════════════════════════════════
    //  SIGNALING
    // ═══════════════════════════════════════════

    /// <summary>Handle an incoming WebRTC signaling message (SDP or ICE).</summary>
    public async Task HandleSignaling(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var from = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
            if (from == _localId || string.IsNullOrEmpty(from)) return;

            // Check "to" field — only process messages addressed to us
            if (root.TryGetProperty("to", out var toEl))
            {
                var to = toEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(to) && to != _localId) return;
            }

            var type = root.GetProperty("type").GetString() ?? "";

            if (type == "offer")
            {
                // Payload contains { type, sdp }
                var payload = root.GetProperty("payload");
                var sdp = payload.GetProperty("sdp").GetString()!;
                await HandleOffer(from, sdp);
            }
            else if (type == "answer")
            {
                var payload = root.GetProperty("payload");
                var sdp = payload.GetProperty("sdp").GetString()!;
                await HandleAnswer(from, sdp);
            }
            else if (type == "ice-candidate")
            {
                var payload = root.GetProperty("payload");
                var candidate = payload.GetProperty("candidate").GetString()!;
                var sdpMid = payload.TryGetProperty("sdpMid", out var m) ? m.GetString() : "0";
                var sdpMLineIndex = payload.TryGetProperty("sdpMLineIndex", out var idx) ? (ushort)idx.GetInt32() : (ushort)0;
                HandleIceCandidate(from, candidate, sdpMid, sdpMLineIndex);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Signaling error: {ex.Message}", true);
        }
    }

    private async Task CreatePeerConnection(string remoteId, bool isInitiator)
    {
        if (_peers.ContainsKey(remoteId)) return;

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        var pc = new RTCPeerConnection(config);

        // Add audio track (48kHz mono Opus)
        var audioFormat = new AudioFormat(AudioCodecsEnum.OPUS, 111, OPUS_SAMPLE_RATE, OPUS_SDP_CHANNELS);
        var track = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv);
        pc.addTrack(track);

        pc.onicecandidate += (candidate) =>
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "ice-candidate",
                payload = new
                {
                    candidate = candidate.candidate,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = candidate.sdpMLineIndex
                },
                from = _localId,
                to = remoteId
            });
            OnSignalingSend?.Invoke($"radio/webrtc/signal/{remoteId}", msg);
        };

        pc.OnRtpPacketReceived += (rep, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio || _playBuffer == null || _opusDecoder == null) return;

            // Skip comfort noise / silence frames (very small Opus packets when track is muted)
            if (pkt.Payload.Length <= 3) return;

            try
            {
                var pcmSamples = new short[OPUS_FRAME_SIZE];
                int decoded = _opusDecoder.Decode(pkt.Payload, 0, pkt.Payload.Length, pcmSamples, 0, OPUS_FRAME_SIZE, false);
                if (decoded > 0)
                {
                    var pcmBytes = new byte[decoded * 2];
                    Buffer.BlockCopy(pcmSamples, 0, pcmBytes, 0, pcmBytes.Length);
                    _playBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠ Opus decode error: {ex.Message} (payload {pkt.Payload.Length} bytes)", true);
            }
        };

        pc.onconnectionstatechange += (state) =>
        {
            OnLog?.Invoke($"📡 Peer {remoteId}: {state}", false);
            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.disconnected || state == RTCPeerConnectionState.closed)
            {
                _peers.TryRemove(remoteId, out _);
                _makingOffer.TryRemove(remoteId, out _);
                OnPeerCountChanged?.Invoke(_peers.Count);
            }
        };

        _peers[remoteId] = pc;
        _makingOffer[remoteId] = false;
        OnPeerCountChanged?.Invoke(_peers.Count);

        if (isInitiator)
        {
            await MakeOffer(remoteId, pc);
        }
    }

    private async Task MakeOffer(string remoteId, RTCPeerConnection pc)
    {
        _makingOffer[remoteId] = true;
        try
        {
            var offer = pc.createOffer();
            await pc.setLocalDescription(offer);

            var msg = JsonSerializer.Serialize(new
            {
                type = "offer",
                payload = new { type = "offer", sdp = offer.sdp },
                from = _localId,
                to = remoteId
            });
            OnSignalingSend?.Invoke($"radio/webrtc/signal/{remoteId}", msg);
        }
        finally
        {
            _makingOffer[remoteId] = false;
        }
    }

    private async Task HandleOffer(string from, string sdp)
    {
        // Polite peer pattern: dispatch always accepts incoming offers
        if (!_peers.ContainsKey(from))
        {
            await CreatePeerConnection(from, isInitiator: false);
        }

        var pc = _peers[from];
        var offerDesc = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp };
        pc.setRemoteDescription(offerDesc);

        var answer = pc.createAnswer();
        await pc.setLocalDescription(answer);

        var msg = JsonSerializer.Serialize(new
        {
            type = "answer",
            payload = new { type = "answer", sdp = answer.sdp },
            from = _localId,
            to = from
        });
        OnSignalingSend?.Invoke($"radio/webrtc/signal/{from}", msg);
    }

    private async Task HandleAnswer(string from, string sdp)
    {
        if (!_peers.TryGetValue(from, out var pc)) return;
        var answerDesc = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp };
        pc.setRemoteDescription(answerDesc);
    }

    private void HandleIceCandidate(string from, string candidate, string? sdpMid, ushort sdpMLineIndex)
    {
        if (!_peers.TryGetValue(from, out var pc)) return;
        pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid ?? "0",
            sdpMLineIndex = sdpMLineIndex
        });
    }

    // ═══════════════════════════════════════════
    //  PTT CONTROL
    // ═══════════════════════════════════════════

    /// <summary>Start transmitting audio to all connected peers (or specific targets).</summary>
    public void StartPtt(IEnumerable<string>? targetIds = null)
    {
        if (_pttActive) return;
        _pttActive = true;

        // Track which peers to send to
        _pttTargets = targetIds?.ToHashSet();

        // Start mic capture
        lock (_captureLock)
        {
            _waveIn = new NAudio.Wave.WaveInEvent
            {
                WaveFormat = new NAudio.Wave.WaveFormat(OPUS_SAMPLE_RATE, 16, 1), // Capture mono from mic
                BufferMilliseconds = OPUS_FRAME_MS // 20ms frames
            };

            _waveIn.DataAvailable += OnMicData;
            _waveIn.StartRecording();
        }

        OnLog?.Invoke("🔴 PTT active — streaming via WebRTC", false);
    }

    /// <summary>Stop transmitting.</summary>
    public void StopPtt()
    {
        if (!_pttActive) return;
        _pttActive = false;

        lock (_captureLock)
        {
            try
            {
                _waveIn?.StopRecording();
                _waveIn?.Dispose();
                _waveIn = null;
            }
            catch { }
        }

        _pttTargets = null;
        _micAccumCount = 0;

        OnLog?.Invoke("⬛ PTT released", false);
    }

    // Accumulate mic samples into exact OPUS_FRAME_SIZE chunks
    private short[] _micAccum = new short[OPUS_FRAME_SIZE * 4];
    private int _micAccumCount;

    private void OnMicData(object? sender, NAudio.Wave.WaveInEventArgs args)
    {
        if (!_pttActive || args.BytesRecorded == 0 || _opusEncoder == null) return;

        int sampleCount = args.BytesRecorded / 2;
        var incoming = new short[sampleCount];
        Buffer.BlockCopy(args.Buffer, 0, incoming, 0, args.BytesRecorded);

        int offset = 0;
        while (offset < sampleCount)
        {
            int toCopy = Math.Min(sampleCount - offset, OPUS_FRAME_SIZE - _micAccumCount);
            Array.Copy(incoming, offset, _micAccum, _micAccumCount, toCopy);
            _micAccumCount += toCopy;
            offset += toCopy;

            if (_micAccumCount >= OPUS_FRAME_SIZE)
            {
                EncodeAndSend(_micAccum, OPUS_FRAME_SIZE);
                _micAccumCount = 0;
            }
        }
    }

    private void EncodeAndSend(short[] pcmSamples, int frameSize)
    {
        try
        {
            var encodedBuffer = new byte[4000];
            int encodedLength = _opusEncoder!.Encode(pcmSamples, 0, frameSize, encodedBuffer, 0, encodedBuffer.Length);
            if (encodedLength <= 0) return;

            var opusFrame = new byte[encodedLength];
            Array.Copy(encodedBuffer, opusFrame, encodedLength);

            foreach (var kvp in _peers)
            {
                if (_pttTargets != null && _pttTargets.Count > 0 && !_pttTargets.Contains(kvp.Key))
                    continue;
                try { kvp.Value.SendAudio((uint)OPUS_FRAME_SIZE, opusFrame); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Opus encode error: {ex.Message}", true);
        }
    }

    // ═══════════════════════════════════════════
    //  AUDIO PLAYBACK
    // ═══════════════════════════════════════════

    private void InitPlayback()
    {
        _playBuffer = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(OPUS_SAMPLE_RATE, 16, OPUS_CHANNELS))
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new NAudio.Wave.WaveOutEvent();
        _waveOut.Init(_playBuffer);
        _waveOut.Volume = _volume;
        _waveOut.Play();
    }

    public void SetVolume(float vol)
    {
        _volume = vol;
        if (_waveOut != null) _waveOut.Volume = vol;
    }

    // (G.711 μ-law codec removed — now using Opus via Concentus)

    // ═══════════════════════════════════════════
    //  CLEANUP
    // ═══════════════════════════════════════════

    public void DisconnectPeer(string peerId)
    {
        if (_peers.TryRemove(peerId, out var pc))
        {
            pc.close();
            _makingOffer.TryRemove(peerId, out _);
            OnPeerCountChanged?.Invoke(_peers.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPtt();

        foreach (var kvp in _peers)
        {
            try { kvp.Value.close(); } catch { }
        }
        _peers.Clear();

        _waveOut?.Stop();
        _waveOut?.Dispose();
    }
}
