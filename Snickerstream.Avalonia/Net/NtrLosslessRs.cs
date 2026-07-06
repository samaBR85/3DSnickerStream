using System;

namespace SnickerstreamV2.Net;

/// <summary>
/// Decoder for NTR-HR "Lossless (Reliable Stream)" (kcp_mode 4, <see cref="DecodeCore"/>) and its
/// <b>Delta</b> sibling (kcp_mode 5, <see cref="DecodeCoreDelta"/>) — ports of ntrviewer-hr's
/// <c>do_decode_lossless_compressed</c> / <c>do_decode_lossless_delta_compressed</c> (ntr_rp.c). Unlike
/// the plain-UDP "Uncompressed" mode (<see cref="NtrLossless"/>, bit-packed), this is a proper
/// <b>lossless image codec</b>: each channel is decoded plane-by-plane as a residual entropy-coded with a
/// Huffman table sized to the colour-bias bit depth, reusing the shared <see cref="NtrHuff"/> machinery.
///
/// <para>The non-delta path predicts each pixel spatially with a <b>median edge predictor</b>. The delta
/// path additionally keeps the previous frame per screen/field and, per 30-pixel block, reads a
/// <c>pred_diff</c> bit choosing <b>temporal</b> prediction (from the previous frame) or the same spatial
/// predictor. Both share the post-decode: bilinear chroma upsample → colour-bias dead-zone re-centre →
/// YCbCr→BGRA. The Huffman tables are built once; call <see cref="ResetDelta"/> on (re)connect.</para>
/// </summary>
internal sealed class NtrLosslessRs
{
    private const int LosslessBlockSize = 16;
    private const int NumComp = 3;
    private const int DeltaBlockWidth = 30;   // DELTA_BLOCK_WIDTH_COUNT
    private const int PrevSize = 240 * 400 * 3; // SCREEN_WIDTH * SCREEN_HEIGHT0 * RGB_CHANNELS_N per screen
    private const float Sqrt2 = 1.41421356f;

    // Four residual tables, indexed by colour-bias bit depth (8 / 6 / 5 / 4).
    private static readonly int[] TblBits = { 8, 6, 5, 4 };
    private readonly DerivedTable[] _tbls;   // aligned with TblBits

    // Scratch buffers (grown as needed) — planar decoded channels + interleaved upsampled channels.
    private byte[] _dec = Array.Empty<byte>();
    private float[] _up = Array.Empty<float>();
    private readonly BitReader _bits = new();

    // Delta mode: previous-frame pixels per screen (indexed in decode traversal order), split by field.
    private readonly byte[][] _prev = { new byte[PrevSize], new byte[PrevSize] };

    /// <summary>Clears the temporal delta state (call on connect / KCP reset).</summary>
    public void ResetDelta()
    {
        Array.Clear(_prev[0], 0, PrevSize);
        Array.Clear(_prev[1], 0, PrevSize);
    }

    private static void SetCompBits(Span<int> compBits, int colorBias)
    {
        switch (colorBias)
        {
            case 1: compBits[0] = 6; compBits[1] = 5; compBits[2] = 5; break;
            case 2: compBits[0] = 4; compBits[1] = 4; compBits[2] = 4; break;
            default: compBits[0] = 8; compBits[1] = 8; compBits[2] = 8; break;
        }
    }

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
        SetCompBits(compBits, colorBias);

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

        PostProcess(outBuf, outBase, width, height, hsamp, vsamp, colorBias);
        return true;
    }

    /// <summary>Delta path (kcp_mode 5): like <see cref="DecodeCore"/> but each 30-pixel block picks
    /// temporal prediction (from the previous frame at <paramref name="offset"/>/<paramref name="evenOdd"/>)
    /// or the spatial median predictor, via a leading <c>pred_diff</c> bit. Port of
    /// do_decode_lossless_delta_compressed.</summary>
    public bool DecodeCoreDelta(byte[] outBuf, int outBase, byte[] inData, int inOff, int inSize,
                                int offset, bool isTop, int chromaSs, int colorBias, int width, int height, int evenOdd)
    {
        int hsamp = chromaSs < 2 ? 2 : 1;
        int vsamp = chromaSs < 1 ? 2 : 1;

        Span<int> bwx = stackalloc int[] { hsamp, 1, 1 };
        Span<int> bhx = stackalloc int[] { vsamp, 1, 1 };
        Span<int> compBits = stackalloc int[NumComp];
        SetCompBits(compBits, colorBias);

        int plane = width * height;
        if (_dec.Length < NumComp * plane) _dec = new byte[NumComp * plane];
        if (_up.Length < plane * NumComp) _up = new float[plane * NumComp];
        var dec = _dec;
        var prev = _prev[isTop ? 1 : 0];
        int prevBase = evenOdd * (PrevSize / 2) + offset * width * NumComp;

        _bits.Init(inData, inOff, inSize);

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
                    int prevRow = prevBase + j * vsamp * width * NumComp + vsamp * width * comp + by * width;

                    int bx = 0;
                    while (bx < w)
                    {
                        int predDiff = _bits.ReadBits(1);
                        for (int i = 0; i < DeltaBlockWidth; i++, bx++)
                        {
                            int s = _bits.HuffDecode(tbl);
                            if (s < 0) return false;
                            int at = rowOff + bx;
                            int residual = (sbyte)s - unchecked((sbyte)128);

                            int pred;
                            if (predDiff != 0)
                            {
                                pred = prev[prevRow + bx];   // temporal: previous frame's pixel
                            }
                            else if (y == 0)
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
                                pred = t + l + tl - min - max;
                            }

                            byte ret = (byte)((residual << shift) + pred);
                            dec[at] = ret;
                            prev[prevRow + bx] = ret;
                        }
                    }
                }
            }
        }

        PostProcess(outBuf, outBase, width, height, hsamp, vsamp, colorBias);
        return true;
    }

    /// <summary>Shared post-decode: bilinear chroma upsample + colour-bias re-centre + YCbCr→BGRA.</summary>
    private void PostProcess(byte[] outBuf, int outBase, int width, int height, int hsamp, int vsamp, int colorBias)
    {
        int plane = width * height;
        var dec = _dec;
        var up = _up;
        Span<int> compBits = stackalloc int[NumComp];
        SetCompBits(compBits, colorBias);

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

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * NumComp;
                YccToBgra(outBuf, outBase + (y * width + x) * 4, up[i], up[i + 1], up[i + 2]);
            }
        }
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
