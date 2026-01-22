using SIPSorceryMedia.Abstractions;

namespace TaxiSipBridge;

/// <summary>
/// Decodes RTP audio from SIP (Opus or G.711) and forwards it to Ada as 24kHz PCM16.
/// Matches AdaAudioSource's encoder side:
///   - Opus: 48kHz, ~40ms frames (~1920 samples)
///   - G.711: 8kHz, 20ms frames (160 samples)
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
                    break;

                case AudioCodecsEnum.PCMU:
                    // PCMU (G.711 µ-law) → PCM 8kHz
                    pcm = AudioCodecs.MuLawDecode(payload);
                    sourceRate = 8000;  // G.711 is always 8kHz
                    break;

                case AudioCodecsEnum.PCMA:
                    // A-law → PCM 8kHz
                    pcm = AudioCodecs.ALawDecode(payload);
                    sourceRate = 8000;
                    break;

                default:
                    // Unknown/unsupported codec - skip
                    if (_framesDecoded == 0)
                        OnDebugLog?.Invoke($"[SipToAdaDecoder] ⚠️ Unsupported codec: {_negotiatedFormat.Codec}");
                    return;
            }

            // Resample to 24kHz for Ada
            if (sourceRate != 24000)
            {
                pcm = AudioCodecs.Resample(pcm, sourceRate, 24000);
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

            // Send to Ada (already 24kHz, so pass 24000 as sample rate)
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
}
