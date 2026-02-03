namespace AdaMain.Audio;

/// <summary>
/// Interface for audio codecs (G.711 μ-law, A-law).
/// </summary>
public interface IAudioCodec
{
    /// <summary>Codec name (PCMU, PCMA).</summary>
    string Name { get; }
    
    /// <summary>RTP payload type.</summary>
    int PayloadType { get; }
    
    /// <summary>Silence byte for padding.</summary>
    byte SilenceByte { get; }
    
    /// <summary>Encode PCM16 samples to G.711.</summary>
    byte[] Encode(short[] pcm);
    
    /// <summary>Decode G.711 to PCM16 samples.</summary>
    short[] Decode(byte[] g711);
}
