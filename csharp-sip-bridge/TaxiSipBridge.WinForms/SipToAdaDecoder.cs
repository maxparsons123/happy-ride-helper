using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Decodes RTP audio from SIP (Opus or G.711) and forwards it to Ada as 24kHz PCM16.
/// Uses simple linear interpolation for upsampling to avoid NAudio overhead per-frame.
/// </summary>
public class SipToAdaDecoder
{
    private readonly AdaAudioClient _adaClient;
    private readonly IAudioEncoder _audioEncoder;
    private readonly AudioFormat _negotiatedFormat;

    // Stats/debug
    private int _framesDecoded;
    private int _bytesIn;
    private int _bytesOut;
    private DateTime _lastLog = DateTime.MinValue;
    
    public event Action<string>? OnDebugLog;

    public SipToAdaDecoder(AdaAudioClient adaClient, IAudioEncoder audioEncoder, AudioFormat negotiatedFormat)
    {
        _adaClient = adaClient ?? throw new ArgumentNullException(nameof(adaClient));
        _audioEncoder = audioEncoder ?? throw new ArgumentNullException(nameof(audioEncoder));
        _negotiatedFormat = negotiatedFormat;
        
        OnDebugLog?.Invoke($"[SipToAdaDecoder] 🎧 Created: codec={negotiatedFormat.FormatName}, rate={negotiatedFormat.ClockRate}Hz");
    }

    /// <summary>
    /// Handle a single SIP RTP audio payload, decode according to negotiated codec,
    /// resample to 24kHz PCM16 and forward to Ada over WebSocket.
    /// </summary>
    public async Task HandleRtpPayloadAsync(byte[] payload, CancellationToken ct = default)
    {
        if (payload == null || payload.Length == 0 || ct.IsCancellationRequested)
            return;

        _bytesIn += payload.Length;

        short[] pcm;
        int sourceRate = _negotiatedFormat.ClockRate;

        try
        {
            switch (_negotiatedFormat.Codec)
            {
                case AudioCodecsEnum.OPUS:
                    // Decode Opus → PCM at negotiated clock rate (typically 48000)
                    pcm = _audioEncoder.DecodeAudio(payload, _negotiatedFormat);
                    if (pcm.Length == 0) return;
                    // Opus 48k → 24k: simple 2:1 decimation
                    if (sourceRate == 48000)
                    {
                        pcm = Downsample48kTo24k(pcm);
                    }
                    break;

                case AudioCodecsEnum.PCMU:
                    // PCMU (G.711 µ-law) → PCM 8kHz
                    pcm = AudioCodecs.MuLawDecode(payload);
                    // 8kHz → 24kHz: 1:3 upsampling with linear interpolation
                    pcm = Upsample8kTo24k(pcm);
                    break;

                case AudioCodecsEnum.PCMA:
                    // A-law → PCM 8kHz
                    pcm = AudioCodecs.ALawDecode(payload);
                    // 8kHz → 24kHz: 1:3 upsampling with linear interpolation
                    pcm = Upsample8kTo24k(pcm);
                    break;

                default:
                    // Unknown/unsupported codec - skip
                    if (_framesDecoded == 0)
                        OnDebugLog?.Invoke($"[SipToAdaDecoder] ⚠️ Unsupported codec: {_negotiatedFormat.Codec}");
                    return;
            }

            // Convert shorts → bytes and send to Ada as 24kHz PCM16
            byte[] pcmBytes = AudioCodecs.ShortsToBytes(pcm);
            _framesDecoded++;
            _bytesOut += pcmBytes.Length;

            // Log first frame
            if (_framesDecoded == 1)
            {
                OnDebugLog?.Invoke($"[SipToAdaDecoder] 📥 First frame: {payload.Length}b → {pcm.Length} samples → {pcmBytes.Length}b @ 24kHz");
            }

            // Light logging every ~3 seconds
            if ((DateTime.Now - _lastLog).TotalSeconds >= 3)
            {
                OnDebugLog?.Invoke($"[SipToAdaDecoder] 📊 frames={_framesDecoded}, in={_bytesIn / 1000}KB, out={_bytesOut / 1000}KB");
                _lastLog = DateTime.Now;
            }

            // Send to Ada
            if (_adaClient.IsConnected && !ct.IsCancellationRequested)
            {
                await _adaClient.SendAudioAsync(pcmBytes, 24000);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnDebugLog?.Invoke($"[SipToAdaDecoder] ❌ Decode error: {ex.Message}");
        }
    }

    /// <summary>
    /// Upsample 8kHz to 24kHz (1:3 ratio) with linear interpolation.
    /// 160 samples @ 8kHz → 480 samples @ 24kHz
    /// </summary>
    private static short[] Upsample8kTo24k(short[] pcm8k)
    {
        if (pcm8k.Length == 0) return pcm8k;
        
        int outLen = pcm8k.Length * 3;
        var output = new short[outLen];

        for (int i = 0; i < pcm8k.Length; i++)
        {
            int outIdx = i * 3;
            short current = pcm8k[i];
            short next = (i < pcm8k.Length - 1) ? pcm8k[i + 1] : current;

            // First sample is original
            output[outIdx] = current;
            
            // Interpolate 2 samples between current and next
            output[outIdx + 1] = (short)((current * 2 + next) / 3);
            output[outIdx + 2] = (short)((current + next * 2) / 3);
        }

        return output;
    }

    /// <summary>
    /// Downsample 48kHz to 24kHz (2:1 ratio) with simple averaging.
    /// </summary>
    private static short[] Downsample48kTo24k(short[] pcm48k)
    {
        if (pcm48k.Length < 2) return pcm48k;
        
        int outLen = pcm48k.Length / 2;
        var output = new short[outLen];

        for (int i = 0; i < outLen; i++)
        {
            int idx = i * 2;
            // Average of two samples
            output[i] = (short)((pcm48k[idx] + pcm48k[idx + 1]) / 2);
        }

        return output;
    }
}
