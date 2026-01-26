using System;

namespace TaxiSipBridge.Audio;

/// <summary>
/// G.722 wideband audio codec implementation.
/// G.722 is a 7 kHz wideband speech codec operating at 48, 56, and 64 kbit/s.
/// Uses Sub-Band ADPCM (SB-ADPCM) with two sub-bands: lower (0-4kHz) and upper (4-8kHz).
/// 
/// This implementation operates at 64 kbit/s mode (Mode 1) which provides the best quality.
/// Input/Output: 16kHz 16-bit PCM
/// Encoded: 8 bits per sample (same size as input samples in bytes)
/// </summary>
public class G722Codec
{
    #region Encoder State

    private class EncoderState
    {
        public int Band0Det = 32;
        public int Band1Det = 8;
        public int Band0Slow = 0;
        public int Band0Fast = 0;
        public int Band1Slow = 0;
        public int Band1Fast = 0;
        public int[] Band0B = new int[6];
        public int[] Band0Dq = new int[6];
        public int[] Band1B = new int[6];
        public int[] Band1Dq = new int[6];
        public int[] Band0A = new int[2];
        public int[] Band0R = new int[2];
        public int[] Band1A = new int[2];
        public int[] Band1R = new int[2];
        public int Band0S = 0;
        public int Band1S = 0;
        public int[] QmfSignalHistory = new int[24];
        public int QmfPtr = 0;
    }

    private class DecoderState
    {
        public int Band0Det = 32;
        public int Band1Det = 8;
        public int Band0Slow = 0;
        public int Band0Fast = 0;
        public int Band1Slow = 0;
        public int Band1Fast = 0;
        public int[] Band0B = new int[6];
        public int[] Band0Dq = new int[6];
        public int[] Band1B = new int[6];
        public int[] Band1Dq = new int[6];
        public int[] Band0A = new int[2];
        public int[] Band0R = new int[2];
        public int[] Band1A = new int[2];
        public int[] Band1R = new int[2];
        public int Band0S = 0;
        public int Band1S = 0;
        public int[] QmfSignalHistory = new int[24];
        public int QmfPtr = 0;
    }

    #endregion

    #region Static Tables

    private static readonly int[] WL = { -60, -30, 58, 172, 334, 538, 1198, 3042 };
    private static readonly int[] RL42 = { 0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3, 2, 1, 0 };
    private static readonly int[] ILB = { 2048, 2093, 2139, 2186, 2233, 2282, 2332, 2383, 2435, 2489, 2543, 2599, 2656, 2714, 2774, 2834, 2896, 2960, 3025, 3091, 3158, 3228, 3298, 3371, 3444, 3520, 3597, 3676, 3756, 3838, 3922, 4008 };
    private static readonly int[] WH = { 0, -214, 798 };
    private static readonly int[] RH2 = { 2, 1, 2, 1 };
    private static readonly int[] QM2 = { -7408, -1616, 7408, 1616 };
    private static readonly int[] QM4 = { 0, -20456, -12896, -8968, -6288, -4240, -2584, -1200, 20456, 12896, 8968, 6288, 4240, 2584, 1200, 0 };
    private static readonly int[] QM5 = { -280, -280, -23352, -17560, -14120, -11664, -9752, -8184, -6864, -5712, -4696, -3784, -2960, -2208, -1520, -880, 23352, 17560, 14120, 11664, 9752, 8184, 6864, 5712, 4696, 3784, 2960, 2208, 1520, 880, 280, -280 };
    private static readonly int[] QM6 = { -136, -136, -136, -136, -24808, -21904, -19008, -16704, -14984, -13512, -12280, -11192, -10232, -9360, -8576, -7856, -7192, -6576, -6000, -5456, -4944, -4464, -4008, -3576, -3168, -2776, -2400, -2032, -1688, -1360, -1040, -728, 24808, 21904, 19008, 16704, 14984, 13512, 12280, 11192, 10232, 9360, 8576, 7856, 7192, 6576, 6000, 5456, 4944, 4464, 4008, 3576, 3168, 2776, 2400, 2032, 1688, 1360, 1040, 728, 432, 136, -432, -136 };
    private static readonly int[] QMF_COEFFS = { 3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11 };

    #endregion

    #region Instance Fields

    private readonly EncoderState _encState = new();
    private readonly DecoderState _decState = new();

    #endregion

    #region Public Methods

    /// <summary>
    /// Encode 16kHz PCM16 samples to G.722.
    /// </summary>
    /// <param name="pcm">16-bit signed PCM samples at 16kHz</param>
    /// <returns>Encoded G.722 bytes (half the number of input samples)</returns>
    public byte[] Encode(short[] pcm)
    {
        if (pcm.Length < 2) return Array.Empty<byte>();

        int outputLen = pcm.Length / 2;
        var output = new byte[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            int s0 = pcm[i * 2];
            int s1 = pcm[i * 2 + 1];

            // QMF analysis filter - split into low and high bands
            var (xlow, xhigh) = QmfAnalysis(_encState, s0, s1);

            // Encode low band (6 bits for 64kbps mode)
            int ilow = EncodeLowBand(_encState, xlow);

            // Encode high band (2 bits)
            int ihigh = EncodeHighBand(_encState, xhigh);

            // Pack into byte: 2 bits high + 6 bits low
            output[i] = (byte)((ihigh << 6) | ilow);
        }

        return output;
    }

    /// <summary>
    /// Decode G.722 bytes to 16kHz PCM16 samples.
    /// </summary>
    /// <param name="encoded">G.722 encoded bytes</param>
    /// <returns>16-bit signed PCM samples at 16kHz</returns>
    public short[] Decode(byte[] encoded)
    {
        if (encoded.Length == 0) return Array.Empty<short>();

        var output = new short[encoded.Length * 2];

        for (int i = 0; i < encoded.Length; i++)
        {
            int code = encoded[i];

            // Unpack: 2 bits high + 6 bits low
            int ilow = code & 0x3F;
            int ihigh = (code >> 6) & 0x03;

            // Decode low band
            int rlow = DecodeLowBand(_decState, ilow);

            // Decode high band
            int rhigh = DecodeHighBand(_decState, ihigh);

            // QMF synthesis filter - combine low and high bands
            var (s0, s1) = QmfSynthesis(_decState, rlow, rhigh);

            output[i * 2] = Saturate(s0);
            output[i * 2 + 1] = Saturate(s1);
        }

        return output;
    }

    /// <summary>
    /// Reset encoder/decoder state (call at start of new call).
    /// </summary>
    public void Reset()
    {
        ResetEncoderState(_encState);
        ResetDecoderState(_decState);
    }

    #endregion

    #region Private Methods

    private static void ResetEncoderState(EncoderState s)
    {
        s.Band0Det = 32;
        s.Band1Det = 8;
        s.Band0Slow = s.Band0Fast = 0;
        s.Band1Slow = s.Band1Fast = 0;
        Array.Clear(s.Band0B);
        Array.Clear(s.Band0Dq);
        Array.Clear(s.Band1B);
        Array.Clear(s.Band1Dq);
        Array.Clear(s.Band0A);
        Array.Clear(s.Band0R);
        Array.Clear(s.Band1A);
        Array.Clear(s.Band1R);
        s.Band0S = s.Band1S = 0;
        Array.Clear(s.QmfSignalHistory);
        s.QmfPtr = 0;
    }

    private static void ResetDecoderState(DecoderState s)
    {
        s.Band0Det = 32;
        s.Band1Det = 8;
        s.Band0Slow = s.Band0Fast = 0;
        s.Band1Slow = s.Band1Fast = 0;
        Array.Clear(s.Band0B);
        Array.Clear(s.Band0Dq);
        Array.Clear(s.Band1B);
        Array.Clear(s.Band1Dq);
        Array.Clear(s.Band0A);
        Array.Clear(s.Band0R);
        Array.Clear(s.Band1A);
        Array.Clear(s.Band1R);
        s.Band0S = s.Band1S = 0;
        Array.Clear(s.QmfSignalHistory);
        s.QmfPtr = 0;
    }

    private static (int xlow, int xhigh) QmfAnalysis(EncoderState s, int s0, int s1)
    {
        // Shift history and add new samples
        int ptr = s.QmfPtr;
        s.QmfSignalHistory[ptr] = s0;
        s.QmfSignalHistory[ptr + 1] = s1;

        // Apply QMF filter
        int sumEven = 0, sumOdd = 0;
        for (int i = 0; i < 12; i++)
        {
            int idx = (ptr + 2 - i * 2 + 24) % 24;
            sumEven += QMF_COEFFS[i] * s.QmfSignalHistory[idx];
            sumOdd += QMF_COEFFS[i] * s.QmfSignalHistory[(idx + 1) % 24];
        }

        s.QmfPtr = (ptr + 2) % 24;

        int xlow = (sumEven + sumOdd) >> 12;
        int xhigh = (sumEven - sumOdd) >> 12;

        return (xlow, xhigh);
    }

    private static int EncodeLowBand(EncoderState s, int xlow)
    {
        // Compute difference signal
        int el = xlow - s.Band0S;

        // Quantize (64kbps mode uses 6 bits)
        int wd = Math.Abs(el);
        int mil;
        for (mil = 0; mil < 30; mil++)
        {
            int dec = (QM6[mil + 1] * s.Band0Det) >> 12;
            if (wd <= dec) break;
        }
        int ilow = (el < 0) ? (63 - mil) : mil;

        // Inverse quantizer
        int dlow = (QM6[ilow] * s.Band0Det) >> 12;

        // Reconstructed signal
        int slow = s.Band0S + dlow;
        slow = Math.Clamp(slow, -16384, 16383);

        // Update predictor
        int wd1 = (s.Band0B[0] * 127) >> 7;
        int wd2 = dlow >= 0 ? 128 : -128;
        s.Band0B[0] = wd1 + (s.Band0Dq[0] >= 0 == dlow >= 0 ? wd2 : -wd2);
        s.Band0B[0] = Math.Clamp(s.Band0B[0], -12288, 12288);

        // Shift delay lines
        for (int i = 5; i > 0; i--)
        {
            s.Band0Dq[i] = s.Band0Dq[i - 1];
        }
        s.Band0Dq[0] = dlow;

        // Update pole section
        s.Band0R[1] = s.Band0R[0];
        s.Band0R[0] = slow;

        int p0 = (s.Band0A[0] * 255) >> 8;
        int p1 = (s.Band0A[1] * 127) >> 7;
        s.Band0A[0] = p0 + ((slow >= 0 == s.Band0R[0] >= 0) ? 192 : -192);
        s.Band0A[1] = p1 + ((slow >= 0 == s.Band0R[1] >= 0) ? 192 : -192);
        s.Band0A[0] = Math.Clamp(s.Band0A[0], -12288, 12288);
        s.Band0A[1] = Math.Clamp(s.Band0A[1], -12288, 12288);

        // Compute signal estimate
        s.Band0S = (s.Band0A[0] * s.Band0R[0] + s.Band0A[1] * s.Band0R[1]) >> 14;
        for (int i = 0; i < 6; i++)
        {
            s.Band0S += (s.Band0B[i] * s.Band0Dq[i]) >> 14;
        }
        s.Band0S = Math.Clamp(s.Band0S, -12288, 12288);

        // Scale factor adaptation
        int wd3 = (ilow < 32) ? ilow : (63 - ilow);
        s.Band0Fast = (s.Band0Fast * 127) >> 7;
        s.Band0Fast += WL[wd3 >> 2];
        s.Band0Slow = (s.Band0Slow * 16383) >> 14;
        s.Band0Slow += s.Band0Fast >> 3;
        s.Band0Slow = Math.Clamp(s.Band0Slow, -32768, 32767);

        int nbpl = (s.Band0Fast - s.Band0Slow) >> 6;
        nbpl = Math.Clamp(nbpl, 0, 31);
        s.Band0Det = (s.Band0Det * ILB[nbpl]) >> 11;
        s.Band0Det = Math.Clamp(s.Band0Det, 32, 18432);

        return ilow & 0x3F;
    }

    private static int EncodeHighBand(EncoderState s, int xhigh)
    {
        int eh = xhigh - s.Band1S;

        // 2-bit quantization for high band
        int ihigh;
        if (eh < 0)
        {
            int dec = (-564 * s.Band1Det) >> 12;
            ihigh = (eh < dec) ? 0 : 1;
        }
        else
        {
            int dec = (564 * s.Band1Det) >> 12;
            ihigh = (eh < dec) ? 2 : 3;
        }

        int dhigh = (QM2[ihigh] * s.Band1Det) >> 12;
        int shigh = s.Band1S + dhigh;
        shigh = Math.Clamp(shigh, -16384, 16383);

        // Update predictor (simplified)
        s.Band1S = (s.Band1S * 127) >> 7;
        s.Band1S += shigh >> 4;
        s.Band1S = Math.Clamp(s.Band1S, -12288, 12288);

        // Scale factor
        s.Band1Fast = (s.Band1Fast * 127) >> 7;
        s.Band1Fast += WH[RH2[ihigh]];
        s.Band1Slow = (s.Band1Slow * 16383) >> 14;
        s.Band1Slow += s.Band1Fast >> 3;
        s.Band1Slow = Math.Clamp(s.Band1Slow, -32768, 32767);

        int nbph = (s.Band1Fast - s.Band1Slow) >> 6;
        nbph = Math.Clamp(nbph, 0, 31);
        s.Band1Det = (s.Band1Det * ILB[nbph]) >> 11;
        s.Band1Det = Math.Clamp(s.Band1Det, 8, 18432);

        return ihigh;
    }

    private static int DecodeLowBand(DecoderState s, int ilow)
    {
        int dlow = (QM6[ilow] * s.Band0Det) >> 12;
        int slow = s.Band0S + dlow;
        slow = Math.Clamp(slow, -16384, 16383);

        // Update predictor
        int wd1 = (s.Band0B[0] * 127) >> 7;
        int wd2 = dlow >= 0 ? 128 : -128;
        s.Band0B[0] = wd1 + (s.Band0Dq[0] >= 0 == dlow >= 0 ? wd2 : -wd2);
        s.Band0B[0] = Math.Clamp(s.Band0B[0], -12288, 12288);

        for (int i = 5; i > 0; i--)
        {
            s.Band0Dq[i] = s.Band0Dq[i - 1];
        }
        s.Band0Dq[0] = dlow;

        s.Band0R[1] = s.Band0R[0];
        s.Band0R[0] = slow;

        int p0 = (s.Band0A[0] * 255) >> 8;
        int p1 = (s.Band0A[1] * 127) >> 7;
        s.Band0A[0] = p0 + ((slow >= 0 == s.Band0R[0] >= 0) ? 192 : -192);
        s.Band0A[1] = p1 + ((slow >= 0 == s.Band0R[1] >= 0) ? 192 : -192);
        s.Band0A[0] = Math.Clamp(s.Band0A[0], -12288, 12288);
        s.Band0A[1] = Math.Clamp(s.Band0A[1], -12288, 12288);

        s.Band0S = (s.Band0A[0] * s.Band0R[0] + s.Band0A[1] * s.Band0R[1]) >> 14;
        for (int i = 0; i < 6; i++)
        {
            s.Band0S += (s.Band0B[i] * s.Band0Dq[i]) >> 14;
        }
        s.Band0S = Math.Clamp(s.Band0S, -12288, 12288);

        // Scale factor adaptation
        int wd3 = (ilow < 32) ? ilow : (63 - ilow);
        s.Band0Fast = (s.Band0Fast * 127) >> 7;
        s.Band0Fast += WL[wd3 >> 2];
        s.Band0Slow = (s.Band0Slow * 16383) >> 14;
        s.Band0Slow += s.Band0Fast >> 3;
        s.Band0Slow = Math.Clamp(s.Band0Slow, -32768, 32767);

        int nbpl = (s.Band0Fast - s.Band0Slow) >> 6;
        nbpl = Math.Clamp(nbpl, 0, 31);
        s.Band0Det = (s.Band0Det * ILB[nbpl]) >> 11;
        s.Band0Det = Math.Clamp(s.Band0Det, 32, 18432);

        return slow;
    }

    private static int DecodeHighBand(DecoderState s, int ihigh)
    {
        int dhigh = (QM2[ihigh] * s.Band1Det) >> 12;
        int shigh = s.Band1S + dhigh;
        shigh = Math.Clamp(shigh, -16384, 16383);

        s.Band1S = (s.Band1S * 127) >> 7;
        s.Band1S += shigh >> 4;
        s.Band1S = Math.Clamp(s.Band1S, -12288, 12288);

        s.Band1Fast = (s.Band1Fast * 127) >> 7;
        s.Band1Fast += WH[RH2[ihigh]];
        s.Band1Slow = (s.Band1Slow * 16383) >> 14;
        s.Band1Slow += s.Band1Fast >> 3;
        s.Band1Slow = Math.Clamp(s.Band1Slow, -32768, 32767);

        int nbph = (s.Band1Fast - s.Band1Slow) >> 6;
        nbph = Math.Clamp(nbph, 0, 31);
        s.Band1Det = (s.Band1Det * ILB[nbph]) >> 11;
        s.Band1Det = Math.Clamp(s.Band1Det, 8, 18432);

        return shigh;
    }

    private static (int s0, int s1) QmfSynthesis(DecoderState s, int rlow, int rhigh)
    {
        // Combine low and high band
        int rl = rlow + rhigh;
        int rh = rlow - rhigh;

        // Store in history
        s.QmfSignalHistory[s.QmfPtr] = rl;
        s.QmfSignalHistory[s.QmfPtr + 1] = rh;

        // Apply synthesis filter
        int sumEven = 0, sumOdd = 0;
        for (int i = 0; i < 12; i++)
        {
            int idx = (s.QmfPtr - i * 2 + 24) % 24;
            sumEven += QMF_COEFFS[i] * s.QmfSignalHistory[idx];
            sumOdd += QMF_COEFFS[i] * s.QmfSignalHistory[(idx + 1) % 24];
        }

        s.QmfPtr = (s.QmfPtr + 2) % 24;

        int s0 = sumEven >> 11;
        int s1 = sumOdd >> 11;

        return (s0, s1);
    }

    private static short Saturate(int x) => (short)Math.Clamp(x, -32768, 32767);

    #endregion
}
