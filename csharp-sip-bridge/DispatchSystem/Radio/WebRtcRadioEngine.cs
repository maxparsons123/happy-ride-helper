using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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

    /// <summary>Fires when a signaling message should be published via MQTT. Args: (topic, jsonPayload)</summary>
    public event Action<string, string>? OnSignalingSend;

    /// <summary>Fires log entries. Args: (message, isError)</summary>
    public event Action<string, bool>? OnLog;

    /// <summary>Fires when peer count changes.</summary>
    public event Action<int>? OnPeerCountChanged;

    public int PeerCount => _peers.Count;

    public WebRtcRadioEngine()
    {
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
            from = _localId,
            type = "announce",
            role = "dispatch",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        OnSignalingSend?.Invoke("radio/webrtc/presence", msg);
    }

    /// <summary>Handle an incoming presence message from a driver.</summary>
    public void HandlePresence(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var from = root.GetProperty("from").GetString() ?? "";
            if (from == _localId || string.IsNullOrEmpty(from)) return;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "announce";

            if (type == "announce" && !_peers.ContainsKey(from))
            {
                OnLog?.Invoke($"📡 Driver {from} announced — initiating connection", false);
                _ = Task.Run(() => CreatePeerConnection(from, isInitiator: true));
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"⚠ Presence parse error: {ex.Message}", true);
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
            var from = root.GetProperty("from").GetString() ?? "";
            if (from == _localId || string.IsNullOrEmpty(from)) return;

            var type = root.GetProperty("type").GetString() ?? "";

            if (type == "offer")
            {
                var sdp = root.GetProperty("sdp").GetString()!;
                await HandleOffer(from, sdp);
            }
            else if (type == "answer")
            {
                var sdp = root.GetProperty("sdp").GetString()!;
                await HandleAnswer(from, sdp);
            }
            else if (type == "ice")
            {
                var candidate = root.GetProperty("candidate").GetString()!;
                var sdpMid = root.TryGetProperty("sdpMid", out var m) ? m.GetString() : "0";
                var sdpMLineIndex = root.TryGetProperty("sdpMLineIndex", out var idx) ? (ushort)idx.GetInt32() : (ushort)0;
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

        // Add audio track (8kHz mono PCMU)
        var audioFormat = new AudioFormat(AudioCodecsEnum.PCMU, 0, 8000, 1);
        var track = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv);
        pc.addTrack(track);

        pc.onicecandidate += (candidate) =>
        {
            var msg = JsonSerializer.Serialize(new
            {
                from = _localId,
                type = "ice",
                candidate = candidate.candidate,
                sdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
            OnSignalingSend?.Invoke($"radio/webrtc/signal/{remoteId}", msg);
        };

        pc.OnRtpPacketReceived += (rep, media, pkt) =>
        {
            if (media == SDPMediaTypesEnum.audio && _playBuffer != null)
            {
                // Decode G.711 μ-law to PCM 16-bit
                var pcmBytes = MuLawDecode(pkt.Payload);
                _playBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
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
                from = _localId,
                type = "offer",
                sdp = offer.sdp
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
            from = _localId,
            type = "answer",
            sdp = answer.sdp
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
                WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1),
                BufferMilliseconds = 20 // 20ms frames for RTP
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

        OnLog?.Invoke("⬛ PTT released", false);
    }

    private void OnMicData(object? sender, NAudio.Wave.WaveInEventArgs args)
    {
        if (!_pttActive || args.BytesRecorded == 0) return;

        // Convert PCM 16-bit to G.711 μ-law for RTP
        var muLawBytes = MuLawEncode(args.Buffer, args.BytesRecorded);

        foreach (var kvp in _peers)
        {
            if (_pttTargets != null && _pttTargets.Count > 0 && !_pttTargets.Contains(kvp.Key))
                continue;
            try
            {
                kvp.Value.SendAudio((uint)(muLawBytes.Length * 1000 / 8), muLawBytes);
            }
            catch { /* peer may have disconnected */ }
        }
    }

    // ═══════════════════════════════════════════
    //  AUDIO PLAYBACK
    // ═══════════════════════════════════════════

    private void InitPlayback()
    {
        _playBuffer = new NAudio.Wave.BufferedWaveProvider(new NAudio.Wave.WaveFormat(8000, 16, 1))
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

    // ═══════════════════════════════════════════
    //  G.711 μ-LAW CODEC
    // ═══════════════════════════════════════════

    private static byte[] MuLawEncode(byte[] pcm16, int length)
    {
        int sampleCount = length / 2;
        var encoded = new byte[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(pcm16, i * 2);
            encoded[i] = LinearToMuLaw(sample);
        }
        return encoded;
    }

    private static byte[] MuLawDecode(byte[] muLaw)
    {
        var pcm = new byte[muLaw.Length * 2];
        for (int i = 0; i < muLaw.Length; i++)
        {
            short sample = MuLawToLinear(muLaw[i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private static byte LinearToMuLaw(short sample)
    {
        const int BIAS = 0x84;
        const int CLIP = 32635;
        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > CLIP) sample = CLIP;
        sample = (short)(sample + BIAS);
        int exponent = 7;
        for (int mask = 0x4000; (sample & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte muLaw = (byte)(~(sign | (exponent << 4) | mantissa));
        return muLaw;
    }

    private static short MuLawToLinear(byte muLaw)
    {
        muLaw = (byte)~muLaw;
        int sign = muLaw & 0x80;
        int exponent = (muLaw >> 4) & 0x07;
        int mantissa = muLaw & 0x0F;
        int sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        return (short)(sign != 0 ? -sample : sample);
    }

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
