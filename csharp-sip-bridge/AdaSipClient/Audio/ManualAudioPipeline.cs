using AdaSipClient.Core;
using NAudio.Wave;

namespace AdaSipClient.Audio;

/// <summary>
/// ManualListen pipeline: routes caller A-law audio to local speakers
/// and captures microphone audio as A-law to send back to the caller.
/// Uses NAudio for local device I/O.
/// </summary>
public sealed class ManualAudioPipeline : IAudioPipeline
{
    private readonly AppState _state;
    private readonly ILogSink _log;
    private readonly VolumeControl _inputGain = new();
    private readonly VolumeControl _outputGain = new();

    private WaveInEvent? _mic;
    private BufferedWaveProvider? _speakerBuffer;
    private WaveOutEvent? _speaker;
    private bool _disposed;

    // NAudio works in 16-bit PCM; we convert to/from A-law at the boundary
    private static readonly WaveFormat PcmFormat = new(8000, 16, 1);

    public event Action<byte[]>? OnOutputAudio;

    public ManualAudioPipeline(AppState state, ILogSink log)
    {
        _state = state;
        _log = log;
        _inputGain.VolumePercent = state.InputVolumePercent;
        _outputGain.VolumePercent = state.OutputVolumePercent;

        state.StateChanged += () =>
        {
            _inputGain.VolumePercent = state.InputVolumePercent;
            _outputGain.VolumePercent = state.OutputVolumePercent;
        };
    }

    public Task StartAsync()
    {
        try
        {
            // ── Speaker output (caller audio → local speakers) ──
            _speakerBuffer = new BufferedWaveProvider(PcmFormat)
            {
                BufferLength = 32000, // 2 seconds at 8kHz mono 16-bit
                DiscardOnBufferOverflow = true
            };

            _speaker = new WaveOutEvent { DesiredLatency = 100 };
            _speaker.Init(_speakerBuffer);
            _speaker.Play();

            // ── Microphone input (local mic → caller) ──
            _mic = new WaveInEvent
            {
                WaveFormat = PcmFormat,
                BufferMilliseconds = 20 // 20ms frames = 320 bytes PCM
            };

            _mic.DataAvailable += OnMicData;
            _mic.StartRecording();

            _log.Log("[Manual] Mic + speaker started (8kHz mono)");
        }
        catch (Exception ex)
        {
            _log.LogError($"[Manual] Audio device error: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Receives A-law audio from the caller → decode to PCM → play on speakers.
    /// </summary>
    public void IngestCallerAudio(byte[] alawFrame)
    {
        if (_disposed || _speakerBuffer == null) return;

        // Apply output volume (hearing the caller)
        var frame = (byte[])alawFrame.Clone();
        _outputGain.ApplyInPlace(frame);

        // Decode A-law to 16-bit PCM for speakers
        var pcm = new byte[frame.Length * 2];
        for (int i = 0; i < frame.Length; i++)
        {
            short sample = ALawDecode(frame[i]);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        _speakerBuffer.AddSamples(pcm, 0, pcm.Length);
    }

    public void Stop()
    {
        _mic?.StopRecording();
        _speaker?.Stop();
        _log.Log("[Manual] Pipeline stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _mic?.Dispose();
        _speaker?.Dispose();
    }

    // ── Private ──

    /// <summary>
    /// Mic data arrives as 16-bit PCM → encode to A-law → raise OnOutputAudio.
    /// </summary>
    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded == 0) return;

        // Encode PCM to A-law
        int sampleCount = e.BytesRecorded / 2;
        var alaw = new byte[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            alaw[i] = ALawEncode(sample);
        }

        // Apply input volume (mic level)
        _inputGain.ApplyInPlace(alaw);

        OnOutputAudio?.Invoke(alaw);
    }

    // ── G.711 A-law codec ──

    private static short ALawDecode(byte alaw)
    {
        alaw ^= 0x55;
        int sign = (alaw & 0x80) != 0 ? -1 : 1;
        int seg = (alaw >> 4) & 0x07;
        int quant = alaw & 0x0F;
        int magnitude = seg == 0
            ? (quant << 4) + 8
            : ((quant << 4) + 8 + 256) << (seg - 1);
        return (short)(sign * magnitude);
    }

    private static byte ALawEncode(short pcm)
    {
        int mask = pcm < 0 ? 0xD5 : 0x55;
        int abs = Math.Abs((int)pcm);
        if (abs > 32767) abs = 32767;
        int exp = 7;
        for (int expMask = 0x4000; (abs & expMask) == 0 && exp > 0; exp--, expMask >>= 1) { }
        int mantissa = (abs >> (exp == 0 ? 4 : exp + 3)) & 0x0F;
        byte encoded = (byte)((exp << 4) | mantissa);
        return (byte)(encoded ^ mask);
    }
}
