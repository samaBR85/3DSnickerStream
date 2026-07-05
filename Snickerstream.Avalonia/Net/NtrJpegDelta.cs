using System;

namespace SnickerstreamV2.Net;

/// <summary>
/// Port of ntrviewer-hr's <c>ntr_jpeg_delta.c</c> — the bespoke "delta" JPEG decoder behind
/// "JPEG (Reliable Stream, Delta)". It is a self-contained baseline-style decoder (own Huffman via
/// <see cref="NtrHuff"/>, own float AAN IDCT, own chroma upsample and YCbCr→RGB) with two twists:
/// <list type="bullet">
/// <item><b>Delta in the DCT-coefficient domain:</b> it keeps the previous frame's quantized coefficients
///   per screen and interlace field (<c>prev</c>); each frame's Huffman symbols are <i>differences</i>
///   accumulated onto them, and an early end-of-block simply carries the previous coefficients forward.</item>
/// <item><b>Log2 quantization:</b> quality maps every coefficient position to a bit-shift; a mid-stream
///   quality change rescales the stored <c>prev</c> coefficients (<c>prev_shift</c>).</item>
/// </list>
/// Huffman tables are never transmitted — both ends build identical ones from fixed frequencies (done
/// once in the constructor here). Output is BGRA, written directly into the caller's frame buffer.
/// A single instance is reused across frames (it owns the cross-frame state); call <see cref="Reset"/>
/// on (re)connect.
/// </summary>
internal sealed class NtrJpegDelta
{
    private const int DctSize = 8, DctSize2 = 64;
    private const int NumQuant = 2, NumComp = 3, MaxBlocks = 6;
    private const int SampFactor = 2, DownsampFactor = 2;
    private const int ScreenCount = 2, ScreenWidth = 240, ScreenHeight0 = 400;
    private const int PrevWidth = 256;                                  // ROUND_UP(240, 8*2*2)
    private const int PrevSize = PrevWidth * ScreenHeight0 * NumComp;   // 307200 shorts / screen
    private const int DeltaQCount = 32;
    private const float DeltaQMax = 7.0f;
    private const int MaxCoefBits = 8 + 2;

    private sealed class Field
    {
        public int Quality = -1;
        public int PrevQuality = -1;
        public readonly byte[,] DctLog2 = new byte[NumQuant, DctSize2];
        public readonly byte[,] PrevDctLog2 = new byte[NumQuant, DctSize2];
        public readonly byte[,] PrevShifts = new byte[NumQuant, DctSize2];
    }

    private sealed class ScreenState
    {
        public readonly Field[] Fields = { new(), new() };
        public readonly short[] Prev = new short[PrevSize];
    }

    private readonly ScreenState[] _screens = { new(), new() };

    // Huffman tables are constant (fixed frequencies) — built once.
    private readonly DerivedTable[] _dcDerived = { new(), new() };
    private readonly DerivedTable[] _acDerived = { new(), new() };

    // Per-decode transient state (instance-scoped to avoid per-frame allocation).
    private readonly float[,] _dctTable = new float[NumQuant, DctSize2];
    private readonly int[] _lastDcVal = new int[NumComp];
    private readonly int[] _mcuMembership = new int[MaxBlocks];
    private readonly short[,] _mcuBuf = new short[MaxBlocks, DctSize2];
    private readonly float[,] _outputBuf = new float[DctSize, DctSize];
    private readonly float[,,] _working = new float[DctSize * SampFactor, DctSize * SampFactor, NumComp];
    private readonly BitReader _bits = new();

    private byte[] _out = Array.Empty<byte>();
    private int _outBase;
    private int _width, _height, _evenOdd, _hSamp, _vSamp, _rowsInMcus, _mcuRow;
    private bool _isTop;
    private int _blocksInMcu;

    public NtrJpegDelta()
    {
        BuildHuffTables();
        Reset();
    }

    public void Reset()
    {
        foreach (var s in _screens)
        {
            Array.Clear(s.Prev, 0, s.Prev.Length);
            foreach (var f in s.Fields)
            {
                f.Quality = f.PrevQuality = -1;
                Array.Clear(f.DctLog2, 0, f.DctLog2.Length);
                Array.Clear(f.PrevDctLog2, 0, f.PrevDctLog2.Length);
                Array.Clear(f.PrevShifts, 0, f.PrevShifts.Length);
            }
        }
    }

    /// <summary>Decodes one core's band into <paramref name="outBuf"/> (BGRA) starting at
    /// <paramref name="outBase"/> bytes. Returns false on a hard decode error. Port of decode_jpeg_delta.</summary>
    public bool DecodeCore(byte[] outBuf, int outBase, byte[] inData, int inOff, int inSize,
                           int rowsInMcus, int lHSamp, int lVSamp, int quality, bool isTop,
                           int mcuRow, int width, int height, int evenOdd)
    {
        _out = outBuf; _outBase = outBase;
        _width = width; _height = height; _evenOdd = evenOdd;
        _hSamp = lHSamp; _vSamp = lVSamp; _rowsInMcus = rowsInMcus;
        _isTop = isTop; _mcuRow = mcuRow;

        var field = _screens[isTop ? 1 : 0].Fields[evenOdd];
        bool needPrevShifts = mcuRow == 0;
        if (needPrevShifts) field.PrevQuality = field.Quality;
        field.Quality = quality;

        if (needPrevShifts) Array.Copy(field.DctLog2, field.PrevDctLog2, field.DctLog2.Length);
        InitDctTable(StdLuminanceQuant, 0, field, quality);
        InitDctTable(StdChrominanceQuant, 1, field, quality);
        if (needPrevShifts && field.PrevQuality >= 0) InitPrevShifts(field);

        _blocksInMcu = lHSamp * lVSamp + 2;
        for (int i = 0; i < _blocksInMcu; i++)
            _mcuMembership[i] = i < lHSamp * lVSamp ? 0 : i - lHSamp * lVSamp + 1;
        for (int c = 0; c < NumComp; c++) _lastDcVal[c] = 0;

        _bits.Init(inData, inOff, inSize);
        return ConsumeData(field);
    }

    //---------------------------------------------------------------------
    // MCU decode (delta domain)
    //---------------------------------------------------------------------
    private bool DecodeMcu(Field field, short[] prev, int prevMcuBase)
    {
        int dir = field.PrevQuality >= 0 ? field.Quality - field.PrevQuality : 0;
        Span<int> state = stackalloc int[NumComp];
        for (int i = 0; i < NumComp; i++) state[i] = _lastDcVal[i];

        for (int blkn = 0; blkn < _blocksInMcu; blkn++)
        {
            int ci = _mcuMembership[blkn];
            int quant = ci == 0 ? 0 : 1;
            int prevBase = prevMcuBase + blkn * DctSize2;
            var dctbl = ci == 0 ? _dcDerived[0] : _dcDerived[1];
            var actbl = ci == 0 ? _acDerived[0] : _acDerived[1];

            // DC coefficient difference (F.2.2.1).
            int s = _bits.HuffDecode(dctbl);
            if (s < 0) return false;
            if (s != 0)
            {
                int r = _bits.GetBits(s);
                s = BitReader.HuffExtend(r, s);
            }
            s += state[ci];
            state[ci] = s;

            PrevShift(prev, prevBase + 0, field.PrevShifts[quant, 0], dir);
            s += prev[prevBase + 0];
            prev[prevBase + 0] = (short)s;
            s <<= field.DctLog2[quant, 0];
            _mcuBuf[blkn, 0] = (short)s;

            // AC coefficients (F.2.2.2), with delta carry-forward.
            for (int k = 1; k < DctSize2; k++)
            {
                s = _bits.HuffDecode(actbl);
                if (s < 0) return false;
                int r = s >> 4;
                s &= 15;

                if (s != 0)
                {
                    for (int l = k; l < k + r; l++)
                    {
                        if (l >= DctSize2) return false;
                        int no = JpegNaturalOrder[l];
                        PrevShift(prev, prevBase + l, field.PrevShifts[quant, no], dir);
                        _mcuBuf[blkn, no] = (short)(prev[prevBase + l] << field.DctLog2[quant, no]);
                    }
                    k += r;
                    int rr = _bits.GetBits(s);
                    s = BitReader.HuffExtend(rr, s);
                    if (k >= DctSize2) return false;
                    int nk = JpegNaturalOrder[k];
                    PrevShift(prev, prevBase + k, field.PrevShifts[quant, nk], dir);
                    s += prev[prevBase + k];
                    prev[prevBase + k] = (short)s;
                    s <<= field.DctLog2[quant, nk];
                    _mcuBuf[blkn, nk] = (short)s;
                }
                else
                {
                    int end = r == 15 ? k + 16 : DctSize2;
                    for (int l = k; l < end; l++)
                    {
                        if (l >= DctSize2) return false;
                        int no = JpegNaturalOrder[l];
                        PrevShift(prev, prevBase + l, field.PrevShifts[quant, no], dir);
                        _mcuBuf[blkn, no] = (short)(prev[prevBase + l] << field.DctLog2[quant, no]);
                    }
                    if (r != 15) break;
                    k += 15;
                }
            }
        }

        for (int i = 0; i < NumComp; i++) _lastDcVal[i] = state[i];
        return true;
    }

    private static void PrevShift(short[] p, int idx, byte shift, int dir)
    {
        if (dir > 0) p[idx] = (short)(p[idx] << shift);
        else if (dir < 0)
        {
            int v = p[idx];
            p[idx] = v < 0 ? (short)(-((-v) >> shift)) : (short)(v >> shift);
        }
    }

    //---------------------------------------------------------------------
    // iMCU row -> pixels
    //---------------------------------------------------------------------
    private bool ConsumeData(Field field)
    {
        int mcuCols = (_width + _hSamp * DctSize - 1) / (_hSamp * DctSize);
        var prev = _screens[_isTop ? 1 : 0].Prev;
        int prevBase = _evenOdd != 0 ? PrevSize / 2 * _evenOdd : 0;

        for (int yoffset = 0; yoffset < _rowsInMcus; yoffset++)
        {
            for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
            {
                Array.Clear(_mcuBuf, 0, _mcuBuf.Length);

                int prevMcu = prevBase + (((yoffset + _mcuRow) * mcuCols + mcuCol) * _blocksInMcu) * DctSize2;
                if (!DecodeMcu(field, prev, prevMcu)) return false;

                for (int i = 0; i < _blocksInMcu; i++)
                {
                    int quant = _mcuMembership[i] == 0 ? 0 : 1;
                    IdctFloat(quant, i, _outputBuf);
                    int c = _mcuMembership[i];
                    if (c == 0)
                    {
                        int wB = i % _hSamp;
                        int hB = i / _hSamp;
                        for (int j = 0; j < DctSize; j++)
                            for (int ii = 0; ii < DctSize; ii++)
                                _working[j + hB * DctSize, ii + wB * DctSize, 0] = _outputBuf[j, ii];
                    }
                    else
                    {
                        Upsample(c, _hSamp, _vSamp, _outputBuf);
                    }
                }

                int b = yoffset * _width * _vSamp * DctSize * 4 + mcuCol * _hSamp * DctSize * 4;
                for (int j = 0; j < DctSize * _vSamp; j++)
                {
                    if (yoffset * _vSamp * DctSize + j >= _height) break;
                    for (int ii = 0; ii < DctSize * _hSamp; ii++)
                    {
                        if (mcuCol * _hSamp * DctSize + ii >= _width) continue;
                        int a = b + j * _width * 4 + ii * 4;
                        YccToBgra(_outBase + a, _working[j, ii, 0], _working[j, ii, 1], _working[j, ii, 2]);
                    }
                }
            }
        }
        return true;
    }

    //---------------------------------------------------------------------
    // float AAN IDCT (dequant folded via _dctTable * 0.125)
    //---------------------------------------------------------------------
    private void IdctFloat(int quant, int blkn, float[,] output)
    {
        Span<float> ws = stackalloc float[DctSize2];
        const float _0_125 = 0.125f;

        // Pass 1: columns.
        for (int ctr = 0; ctr < DctSize; ctr++)
        {
            int col = ctr;
            if (_mcuBuf[blkn, DctSize * 1 + col] == 0 && _mcuBuf[blkn, DctSize * 2 + col] == 0 &&
                _mcuBuf[blkn, DctSize * 3 + col] == 0 && _mcuBuf[blkn, DctSize * 4 + col] == 0 &&
                _mcuBuf[blkn, DctSize * 5 + col] == 0 && _mcuBuf[blkn, DctSize * 6 + col] == 0 &&
                _mcuBuf[blkn, DctSize * 7 + col] == 0)
            {
                float dcval = _mcuBuf[blkn, col] * (_dctTable[quant, col] * _0_125);
                for (int r = 0; r < DctSize; r++) ws[DctSize * r + col] = dcval;
                continue;
            }

            float tmp0 = _mcuBuf[blkn, DctSize * 0 + col] * (_dctTable[quant, DctSize * 0 + col] * _0_125);
            float tmp1 = _mcuBuf[blkn, DctSize * 2 + col] * (_dctTable[quant, DctSize * 2 + col] * _0_125);
            float tmp2 = _mcuBuf[blkn, DctSize * 4 + col] * (_dctTable[quant, DctSize * 4 + col] * _0_125);
            float tmp3 = _mcuBuf[blkn, DctSize * 6 + col] * (_dctTable[quant, DctSize * 6 + col] * _0_125);

            float tmp10 = tmp0 + tmp2;
            float tmp11 = tmp0 - tmp2;
            float tmp13 = tmp1 + tmp3;
            float tmp12 = (tmp1 - tmp3) * 1.414213562f - tmp13;

            float t0 = tmp10 + tmp13;
            float t3 = tmp10 - tmp13;
            float t1 = tmp11 + tmp12;
            float t2 = tmp11 - tmp12;

            float tmp4 = _mcuBuf[blkn, DctSize * 1 + col] * (_dctTable[quant, DctSize * 1 + col] * _0_125);
            float tmp5 = _mcuBuf[blkn, DctSize * 3 + col] * (_dctTable[quant, DctSize * 3 + col] * _0_125);
            float tmp6 = _mcuBuf[blkn, DctSize * 5 + col] * (_dctTable[quant, DctSize * 5 + col] * _0_125);
            float tmp7 = _mcuBuf[blkn, DctSize * 7 + col] * (_dctTable[quant, DctSize * 7 + col] * _0_125);

            float z13 = tmp6 + tmp5;
            float z10 = tmp6 - tmp5;
            float z11 = tmp4 + tmp7;
            float z12 = tmp4 - tmp7;

            tmp7 = z11 + z13;
            tmp11 = (z11 - z13) * 1.414213562f;
            float z5 = (z10 + z12) * 1.847759065f;
            tmp10 = z5 - z12 * 1.082392200f;
            tmp12 = z5 - z10 * 2.613125930f;
            tmp6 = tmp12 - tmp7;
            tmp5 = tmp11 - tmp6;
            tmp4 = tmp10 - tmp5;

            ws[DctSize * 0 + col] = t0 + tmp7;
            ws[DctSize * 7 + col] = t0 - tmp7;
            ws[DctSize * 1 + col] = t1 + tmp6;
            ws[DctSize * 6 + col] = t1 - tmp6;
            ws[DctSize * 2 + col] = t2 + tmp5;
            ws[DctSize * 5 + col] = t2 - tmp5;
            ws[DctSize * 3 + col] = t3 + tmp4;
            ws[DctSize * 4 + col] = t3 - tmp4;
        }

        // Pass 2: rows.
        for (int ctr = 0; ctr < DctSize; ctr++)
        {
            int row = ctr * DctSize;
            float z5 = ws[row + 0] + (127.5f + 0.5f);
            float tmp10 = z5 + ws[row + 4];
            float tmp11 = z5 - ws[row + 4];
            float tmp13 = ws[row + 2] + ws[row + 6];
            float tmp12 = (ws[row + 2] - ws[row + 6]) * 1.414213562f - tmp13;

            float t0 = tmp10 + tmp13;
            float t3 = tmp10 - tmp13;
            float t1 = tmp11 + tmp12;
            float t2 = tmp11 - tmp12;

            float z13 = ws[row + 5] + ws[row + 3];
            float z10 = ws[row + 5] - ws[row + 3];
            float z11 = ws[row + 1] + ws[row + 7];
            float z12 = ws[row + 1] - ws[row + 7];

            float tmp7 = z11 + z13;
            tmp11 = (z11 - z13) * 1.414213562f;
            z5 = (z10 + z12) * 1.847759065f;
            tmp10 = z5 - z12 * 1.082392200f;
            tmp12 = z5 - z10 * 2.613125930f;
            float tmp6 = tmp12 - tmp7;
            float tmp5 = tmp11 - tmp6;
            float tmp4 = tmp10 - tmp5;

            output[ctr, 0] = t0 + tmp7;
            output[ctr, 7] = t0 - tmp7;
            output[ctr, 1] = t1 + tmp6;
            output[ctr, 6] = t1 - tmp6;
            output[ctr, 2] = t2 + tmp5;
            output[ctr, 5] = t2 - tmp5;
            output[ctr, 3] = t3 + tmp4;
            output[ctr, 4] = t3 - tmp4;
        }
    }

    private void Upsample(int c, int hSamp, int vSamp, float[,] input)
    {
        if (hSamp == 1 && vSamp == 1)
        {
            for (int j = 0; j < DctSize; j++)
                for (int i = 0; i < DctSize; i++)
                    _working[j, i, c] = input[j, i];
        }
        else if (hSamp == 2 && vSamp == 1)
        {
            for (int j = 0; j < DctSize; j++)
                for (int i = 0; i < DctSize * hSamp; i++)
                {
                    float a = input[j, Math.Max((i - 1) / hSamp, 0)];
                    float bb = input[j, Math.Min((i + 1) / hSamp, DctSize - 1)];
                    _working[j, i, c] = (i % hSamp) != 0 ? a * 0.75f + bb * 0.25f : a * 0.25f + bb * 0.75f;
                }
        }
        else if (hSamp == 2 && vSamp == 2)
        {
            for (int j = 0; j < DctSize * vSamp; j++)
                for (int i = 0; i < DctSize * hSamp; i++)
                {
                    float aa = input[Math.Max((j - 1) / vSamp, 0), Math.Max((i - 1) / hSamp, 0)];
                    float ab = input[Math.Max((j - 1) / vSamp, 0), Math.Min((i + 1) / hSamp, DctSize - 1)];
                    float ba = input[Math.Min((j + 1) / vSamp, DctSize - 1), Math.Max((i - 1) / hSamp, 0)];
                    float bc = input[Math.Min((j + 1) / vSamp, DctSize - 1), Math.Min((i + 1) / hSamp, DctSize - 1)];
                    float a = (i % hSamp) != 0 ? aa * 0.75f + ab * 0.25f : aa * 0.25f + ab * 0.75f;
                    float b = (i % hSamp) != 0 ? ba * 0.75f + bc * 0.25f : ba * 0.25f + bc * 0.75f;
                    _working[j, i, c] = (j % vSamp) != 0 ? a * 0.75f + b * 0.25f : a * 0.25f + b * 0.75f;
                }
        }
    }

    private void YccToBgra(int o, float yf, float cbf, float crf)
    {
        int r = RangeLimit(yf + 1.40200f * (crf - 128.0f));
        int g = RangeLimit(yf - 0.34414f * (cbf - 128.0f) - 0.71414f * (crf - 128.0f));
        int b = RangeLimit(yf + 1.77200f * (cbf - 128.0f));
        _out[o + 0] = (byte)b;
        _out[o + 1] = (byte)g;
        _out[o + 2] = (byte)r;
        _out[o + 3] = 255;
    }

    private static int RangeLimit(float f)
    {
        int v = (int)MathF.Round(f, MidpointRounding.AwayFromZero);
        return v > 255 ? 255 : v < 0 ? 0 : v;
    }

    //---------------------------------------------------------------------
    // quantization (log2 shifts) + prev-shift setup
    //---------------------------------------------------------------------
    private void InitDctTable(byte[] baseTbl, int quant, Field field, int quality)
    {
        float q = DeltaQMax / DeltaQCount * quality;
        for (int j = 0; j < DctSize; j++)
            for (int i = 0; i < DctSize; i++)
            {
                int k = j * DctSize + i;
                float l = MathF.Log2(baseTbl[k]);
                int v = (int)MathF.Round(MathF.Max(l - q, 0.0f), MidpointRounding.AwayFromZero);
                _dctTable[quant, k] = AanScale[i] * AanScale[j];
                field.DctLog2[quant, k] = (byte)v;
            }
    }

    private static void InitPrevShifts(Field field)
    {
        for (int qi = 0; qi < NumQuant; qi++)
            for (int i = 0; i < DctSize2; i++)
            {
                int diff = field.Quality > field.PrevQuality
                    ? field.PrevDctLog2[qi, i] - field.DctLog2[qi, i]
                    : field.DctLog2[qi, i] - field.PrevDctLog2[qi, i];
                field.PrevShifts[qi, i] = (byte)diff;
            }
    }

    //---------------------------------------------------------------------
    // constant Huffman tables (built once)
    //---------------------------------------------------------------------
    private void BuildHuffTables()
    {
        var freq = new long[257];

        void DcFreq() { Array.Clear(freq, 0, 257); for (int i = 0; i <= 12; i++) freq[i] = 12 + 1 - i; }
        void AcFreq(byte[] baseVals)
        {
            Array.Clear(freq, 0, 257);
            int dqLen = 0x10, count = baseVals.Length + dqLen;
            for (int i = 0; i < baseVals.Length; i++) freq[baseVals[i]] = count - i;
            count -= baseVals.Length;
            for (int i = 0; i < dqLen; i++) freq[0x0b | (i << 4)] = count - i;
        }

        var dcLum = new HuffTable(); DcFreq(); NtrHuff.GenOptimalTable(dcLum, freq);
        var dcChr = new HuffTable(); DcFreq(); NtrHuff.GenOptimalTable(dcChr, freq);
        var acLum = new HuffTable(); AcFreq(ValAcLuminance); NtrHuff.GenOptimalTable(acLum, freq);
        var acChr = new HuffTable(); AcFreq(ValAcChrominance); NtrHuff.GenOptimalTable(acChr, freq);

        NtrHuff.MakeDerivedTbl(dcLum, _dcDerived[0]);
        NtrHuff.MakeDerivedTbl(dcChr, _dcDerived[1]);
        NtrHuff.MakeDerivedTbl(acLum, _acDerived[0]);
        NtrHuff.MakeDerivedTbl(acChr, _acDerived[1]);
    }

    private static readonly float[] AanScale =
    {
        1.0f, 1.387039845f, 1.306562965f, 1.175875602f, 1.0f, 0.785694958f, 0.541196100f, 0.275899379f
    };

    private static readonly byte[] StdLuminanceQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113,
        92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99
    };

    private static readonly byte[] StdChrominanceQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99
    };

    private static readonly int[] JpegNaturalOrder =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63
    };

    private static readonly byte[] ValAcLuminance =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    };

    private static readonly byte[] ValAcChrominance =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    };
}
