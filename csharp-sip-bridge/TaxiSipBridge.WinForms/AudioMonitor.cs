using System.Collections.Concurrent;
using NAudio.Wave;

namespace TaxiSipBridge;

/// <summary>
/// Real-time audio monitor that plays µ-law 8kHz audio through speakers.
/// Useful for debugging SIP audio streams.
/// </summary>
public class AudioMonitor : IDisposable
{
    private readonly BufferedWaveProvider _waveProvider;
    private readonly WaveOutEvent _waveOut;
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _playbackTask;
    private bool _isEnabled = false;
    private bool _isDisposed = false;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public AudioMonitor()
    {
        // µ-law 8kHz mono → PCM 8kHz mono for playback
        var pcmFormat = new WaveFormat(8000, 16, 1);
        _waveProvider = new BufferedWaveProvider(pcmFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100
        };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();

        _playbackTask = Task.Run(ProcessAudioQueue);
    }

    /// <summary>
    /// Add a µ-law frame (160 bytes = 20ms) to the monitor.
    /// </summary>
    public void AddFrame(byte[] ulawFrame)
    {
        if (!_isEnabled || _isDisposed) return;
        _audioQueue.Enqueue(ulawFrame);
    }

    private async Task ProcessAudioQueue()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (_audioQueue.TryDequeue(out var ulawFrame))
                {
                    // Decode µ-law to PCM16
                    var pcm = MuLawDecode(ulawFrame);
                    var pcmBytes = new byte[pcm.Length * 2];
                    Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);

                    _waveProvider.AddSamples(pcmBytes, 0, pcmBytes.Length);
                }
                else
                {
                    await Task.Delay(5, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore playback errors
            }
        }
    }

    private static short[] MuLawDecode(byte[] data)
    {
        var pcm = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int mulaw = ~data[i];
            int sign = (mulaw & 0x80) != 0 ? -1 : 1;
            int exponent = (mulaw >> 4) & 0x07;
            int mantissa = mulaw & 0x0F;
            pcm[i] = (short)(sign * (((mantissa << 3) + 0x84) << exponent) - 0x84);
        }
        return pcm;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        try { _playbackTask.Wait(500); } catch { }

        _waveOut.Stop();
        _waveOut.Dispose();
        _cts.Dispose();
    }
}
