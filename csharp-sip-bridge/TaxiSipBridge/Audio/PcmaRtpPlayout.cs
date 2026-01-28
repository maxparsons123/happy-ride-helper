using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using SIPSorcery.Net;

namespace TaxiSipBridge.Audio;

/// <summary>
/// 8kHz PCM16 → PCMA (G.711 A-law) RTP playout with a dedicated timing thread.
/// Expects 20ms frames (160 samples @ 8kHz).
/// </summary>
public class PcmaRtpPlayout : IDisposable
{
    private readonly ConcurrentQueue<short[]> _frameQueue = new();
    private readonly byte[] _pcmaBuffer = new byte[160];   // 20ms @ 8kHz PCMA
    private readonly ushort _clockRate = 8000;
    private readonly int _frameSamples = 160;              // 20ms * 8000Hz
    private readonly int _frameMs = 20;

    private readonly RTPChannel _rtpChannel;
    private readonly IPEndPoint _remoteEndPoint;

    private Thread? _playoutThread;
    private bool _running;
    private ushort _seq;
    private uint _ts;
    private readonly uint _ssrc;

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
            Priority = ThreadPriority.AboveNormal
        };
        _playoutThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _playoutThread?.Join();
    }

    /// <summary>
    /// Enqueue one 20ms frame of 8kHz PCM16 (160 samples).
    /// </summary>
    public void EnqueuePcm8kFrame(short[] pcm8kFrame)
    {
        if (pcm8kFrame == null || pcm8kFrame.Length != _frameSamples)
            throw new ArgumentException($"Expected {_frameSamples} samples.", nameof(pcm8kFrame));

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
                var sleep = nextMs - now;
                if (sleep > 1) Thread.Sleep((int)sleep);
                else Thread.SpinWait(500); // fine-tune near the deadline
                continue;
            }

            // time to play one frame
            if (!_frameQueue.TryDequeue(out var frame))
            {
                // underrun: send silence
                frame = new short[_frameSamples];
            }

            // A-law encode this frame into _pcmaBuffer
            for (int i = 0; i < _frameSamples; i++)
                _pcmaBuffer[i] = NAudio.Codecs.ALawEncoder.LinearToALawSample(frame[i]);

            var rtpPacket = new RTPPacket(12 + _pcmaBuffer.Length);
            var header = rtpPacket.Header;
            header.SyncSource = _ssrc;
            header.SequenceNumber = _seq++;
            header.Timestamp = _ts;
            header.PayloadType = 8; // PCMA
            header.MarkerBit = 0;

            rtpPacket.Payload = _pcmaBuffer;

            _ts += (uint)_frameSamples; // 160 samples per 20ms at 8kHz

            var rtpBytes = rtpPacket.GetBytes();
            _rtpChannel.SendAsync(_remoteEndPoint, rtpBytes);

            nextMs += _frameMs;
        }
    }

    public void Dispose() => Stop();
}
