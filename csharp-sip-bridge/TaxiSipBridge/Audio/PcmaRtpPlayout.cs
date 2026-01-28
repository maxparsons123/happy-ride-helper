using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using SIPSorcery.Net;
using NAudio.Codecs;

namespace TaxiSipBridge.Audio;

/// <summary>
/// 8kHz PCM16 → PCMA (G.711 A-law) RTP playout with a dedicated timing thread.
/// Expects 20ms frames (160 samples @ 8kHz).
/// </summary>
public class PcmaRtpPlayout : IDisposable
{
    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly byte[] _pcmaBuffer = new byte[160]; // 20ms @ 8kHz PCMA

    private const int FRAME_SAMPLES = 160;
    private const int FRAME_MS = 20;

    private readonly RTPChannel _rtpChannel;
    private readonly IPEndPoint _remoteEndPoint;

    private Thread? _playoutThread;
    private volatile bool _running;
    private ushort _seq;
    private uint _ts;
    private readonly uint _ssrc;

    public event Action<string>? OnDebugLog;

    public PcmaRtpPlayout(RTPChannel rtpChannel, IPEndPoint remoteEndPoint)
    {
        _rtpChannel = rtpChannel;
        _remoteEndPoint = remoteEndPoint;

        var rnd = new Random();
        _seq = (ushort)rnd.Next(ushort.MaxValue);
        _ts = (uint)rnd.Next(int.MaxValue);
        _ssrc = (uint)rnd.Next(int.MaxValue);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _playoutThread = new Thread(PlayoutLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "PcmaRtpPlayout"
        };
        _playoutThread.Start();
        OnDebugLog?.Invoke("[PcmaRtpPlayout] ▶️ Started playout thread");
    }

    public void Stop()
    {
        _running = false;
        try { _playoutThread?.Join(500); } catch { }
        OnDebugLog?.Invoke("[PcmaRtpPlayout] ⏹️ Stopped");
    }

    /// <summary>
    /// Enqueue one 20ms 8kHz PCM16 frame (160 samples).
    /// </summary>
    public void EnqueuePcm8kFrame(short[] pcm8kFrame)
    {
        if (pcm8kFrame == null || pcm8kFrame.Length != FRAME_SAMPLES)
            throw new ArgumentException($"Expected {FRAME_SAMPLES} samples.", nameof(pcm8kFrame));

        _frameQueue.Enqueue(pcm8kFrame);
    }

    private void PlayoutLoop()
    {
        var sw = Stopwatch.StartNew();
        double nextMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (now < nextMs)
            {
                var sleepMs = nextMs - now;
                if (sleepMs > 1)
                    Thread.Sleep((int)sleepMs);
                else
                    Thread.SpinWait(500);
                continue;
            }

            // Time to send one frame
            if (!_frameQueue.TryDequeue(out var frame))
            {
                // Underrun: play silence
                frame = new short[FRAME_SAMPLES];
            }

            // Encode PCM16 → PCMA (G.711 A-law)
            for (int i = 0; i < FRAME_SAMPLES; i++)
            {
                _pcmaBuffer[i] = ALawEncoder.LinearToALawSample(frame[i]);
            }

            // Build RTP packet
            var rtpPacket = new RTPPacket(12 + _pcmaBuffer.Length);
            var header = rtpPacket.Header;
            header.SyncSource = _ssrc;
            header.SequenceNumber = _seq++;
            header.Timestamp = _ts;
            header.PayloadType = 8; // PCMA
            header.MarkerBit = 0;

            rtpPacket.Payload = _pcmaBuffer;

            _ts += FRAME_SAMPLES; // 160 samples @ 8kHz per 20ms

            byte[] rtpBytes = rtpPacket.GetBytes();
            _ = _rtpChannel.SendAsync(_remoteEndPoint, rtpBytes);

            nextMs += FRAME_MS;
        }
    }

    /// <summary>
    /// Clear all queued audio.
    /// </summary>
    public void Clear()
    {
        while (_frameQueue.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
