using System;

namespace SnickerstreamV2.Net;

/// <summary>
/// Decoder for NTR-HR "Lossless (Reliable Stream)" (kcp_mode 4) — a port of ntrviewer-hr's
/// <c>do_decode_lossless_compressed</c> (ntr_rp.c). Unlike the plain-UDP "Uncompressed" mode
/// (<see cref="NtrLossless"/>, bit-packed), this is a proper <b>lossless image codec</b>: each channel is
/// decoded plane-by-plane as a <b>median-predicted</b> residual, entropy-coded with a Huffman table sized
/// to the colour-bias bit depth. It reuses the shared <see cref="NtrHuff"/> machinery (same
/// <see cref="BitReader"/> + optimal-table builder as the JPEG-delta decoder).
///
/// <para>Pipeline per core band: Huffman-decode residuals with the median edge predictor → planar
/// Y + subsampled Cb/Cr → bilinear chroma upsample → colour-bias dead-zone re-centre → YCbCr→BGRA.
/// A single instance is cheap to reuse; the Huffman tables are built once.</para>
/// </summary>
internal sealed class NtrLosslessRs
{
    private const int LosslessBlockSize = 16;
    private const int NumComp = 3;
    private const float Sqrt2 = 1.41421356f;

    // Four residual tables, indexed by colour-bias bit depth (8 / 6 / 5 / 4).
    private static readonly int[] TblBits = { 8, 6, 5, 4 };
    private readonly DerivedTable[] _tbls;   // aligned with TblBits

    // Scratch buffers (grown as needed) — planar decoded channels + interleaved upsampled channels.
    private byte[] _dec = Array.Empty<byte>();
    private float[] _up = Array.Empty<float>();
    private readonly BitReader _bits = new();

    /// <summary>Unconsumed source bytes after the last <see cref="DecodeCore"/> (diagnostic; ~0 = in sync).</summary>
    public int LastBytesRemaining { get; private set; }

    public NtrLosslessRs()
    {
        _tbls = new DerivedTable[TblBits.Length];
        var freq = new long[257];
        for (int i = 0; i < TblBits.Length; i++)
        {
            int b = TblBits[i];
            Array.Clear(freq, 0, freq.Length);
            int end = 1 << (b - 1);
            for (int k = 0; k <= end; k++)
            {
                long count = (long)(
                    (k < 24 ? MathF.Pow(Sqrt2, 24.0f - k) * 2.0f : 2.0f) *
                    (k == 0 ? MathF.Pow(Sqrt2, 8.0f - b + 1.0f) : 1.0f));
                freq[128 + k] = count;
                freq[128 - k] = count;
            }
            freq[128 + end] = 0;
            var htbl = new HuffTable();
            NtrHuff.GenOptimalTable(htbl, freq);
            var dtbl = new DerivedTable();
            NtrHuff.MakeDerivedTbl(htbl, dtbl);
            _tbls[i] = dtbl;
        }
    }

    private static int TblIndexFromBits(int bits) => bits switch { 6 => 1, 5 => 2, 4 => 3, _ => 0 };

    /// <summary>Decodes one core band (width × height) into <paramref name="outBuf"/> (BGRA) at
    /// <paramref name="outBase"/> bytes. Returns false on a hard decode error.</summary>
    public bool DecodeCore(byte[] outBuf, int outBase, byte[] inData, int inOff, int inSize,
                           int chromaSs, int colorBias, int width, int height)
    {
        int hsamp = chromaSs < 2 ? 2 : 1;
        int vsamp = chromaSs < 1 ? 2 : 1;

        Span<int> bwx = stackalloc int[] { hsamp, 1, 1 };
        Span<int> bhx = stackalloc int[] { vsamp, 1, 1 };
        Span<int> compBits = stackalloc int[NumComp];
        switch (colorBias)
        {
            case 1: compBits[0] = 6; compBits[1] = 5; compBits[2] = 5; break;
            case 2: compBits[0] = 4; compBits[1] = 4; compBits[2] = 4; break;
            default: compBits[0] = 8; compBits[1] = 8; compBits[2] = 8; break;
        }

        int plane = width * height;
        if (_dec.Length < NumComp * plane) _dec = new byte[NumComp * plane];
        if (_up.Length < plane * NumComp) _up = new float[plane * NumComp];
        var dec = _dec;

        _bits.Init(inData, inOff, inSize);

        // --- planar predictive Huffman decode ---
        for (int j = 0; j < height / vsamp; j++)
        {
            for (int comp = 0; comp < NumComp; comp++)
            {
                int w = width / hsamp * bwx[comp];
                int baseOff = comp * plane;
                int bits = compBits[comp];
                int shift = 8 - bits;
                var tbl = _tbls[TblIndexFromBits(bits)];

                for (int by = 0; by < bhx[comp]; by++)
                {
                    int y = j * bhx[comp] + by;
                    int rowOff = baseOff + y * width;
                    for (int bx = 0; bx < w; bx++)
                    {
                        int s = _bits.HuffDecode(tbl);
                        if (s < 0) return false;
                        int at = rowOff + bx;

                        int pred;
                        if (y == 0)
                            pred = bx == 0 ? 128 : dec[at - 1];
                        else if (bx == 0)
                            pred = dec[at - width];
                        else
                        {
                            int t = dec[at - width];
                            int l = dec[at - 1];
                            int tl = dec[at - width - 1];
                            int min = Math.Min(Math.Min(t, l), tl);
                            int max = Math.Max(Math.Max(t, l), tl);
                            pred = t + l + tl - min - max;   // median of the three neighbours
                        }

                        int residual = (sbyte)s - unchecked((sbyte)128);   // (int8)s - (int8)128
                        dec[at] = (byte)((residual << shift) + pred);
                    }
                }
            }
        }

        // --- chroma upsample (bilinear) + colour-bias re-centre ---
        var up = _up;
        for (int comp = 0; comp < NumComp; comp++)
        {
            bool needSs = comp > 0;
            int hss = needSs ? hsamp : 1;
            int vss = needSs ? vsamp : 1;
            int baseOff = comp * plane;
            int bits = compBits[comp];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int upAt = (y * width + x) * NumComp + comp;
                    float val;
                    if (needSs)
                    {
                        int xc = hss > 1 ? (x - 1) / hss : x;
                        int xe = hss > 1 ? (x + 1) / hss : x;
                        xc = Math.Max(xc, 0);
                        xe = Math.Min(xe, width / hss - 1);
                        int yc = vss > 1 ? (y - 1) / vss : y;
                        int ye = vss > 1 ? (y + 1) / vss : y;
                        yc = Math.Max(yc, 0);
                        ye = Math.Min(ye, height / vss - 1);

                        int xf = ((x - 1) % hss + hss) % hss;   // C's (x-1)%hss, but non-negative
                        float xfc = hss > 1 ? (xf != 0 ? 0.25f : 0.75f) : 0.5f;
                        float xfe = hss > 1 ? (xf != 0 ? 0.75f : 0.25f) : 0.5f;
                        int yf = ((y - 1) % vss + vss) % vss;
                        float yfc = vss > 1 ? (yf != 0 ? 0.25f : 0.75f) : 0.5f;
                        float yfe = vss > 1 ? (yf != 0 ? 0.75f : 0.25f) : 0.5f;

                        float tl = dec[baseOff + yc * width + xc];
                        float tr = dec[baseOff + yc * width + xe];
                        float bl = dec[baseOff + ye * width + xc];
                        float br = dec[baseOff + ye * width + xe];
                        float t = tl * xfc + tr * xfe;
                        float b = bl * xfc + br * xfe;
                        val = t * yfc + b * yfe;
                    }
                    else
                    {
                        val = dec[baseOff + y * width + x];
                    }

                    if (bits < 8)
                    {
                        float half = 1 << (8 - bits - 1);
                        float o = (val - 128.0f) + half;
                        if (comp == 0)
                        {
                            val = o + 128.0f;
                        }
                        else
                        {
                            float oa = MathF.Abs(o) - half;
                            oa = MathF.Max(oa, 0.0f);
                            val = oa * (o >= 0 ? 1.0f : -1.0f) + 128.0f;
                        }
                    }
                    up[upAt] = val;
                }
            }
        }

        // --- YCbCr -> BGRA ---
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * NumComp;
                YccToBgra(outBuf, outBase + (y * width + x) * 4, up[i], up[i + 1], up[i + 2]);
            }
        }
        LastBytesRemaining = _bits.BytesRemaining;
        return true;
    }

    private static void YccToBgra(byte[] o, int at, float yf, float cbf, float crf)
    {
        int r = Clamp(yf + 1.40200f * (crf - 128.0f));
        int g = Clamp(yf - 0.34414f * (cbf - 128.0f) - 0.71414f * (crf - 128.0f));
        int b = Clamp(yf + 1.77200f * (cbf - 128.0f));
        o[at + 0] = (byte)b;
        o[at + 1] = (byte)g;
        o[at + 2] = (byte)r;
        o[at + 3] = 255;
    }

    private static int Clamp(float f)
    {
        int v = (int)MathF.Round(f, MidpointRounding.AwayFromZero);
        return v > 255 ? 255 : v < 0 ? 0 : v;
    }
}
