namespace SnickerstreamV2.Net;

/// <summary>A JPEG Huffman table (symbol-count-per-length + symbol values). Port of ntr_huff.h huff_tbl_t.</summary>
internal sealed class HuffTable
{
    public readonly byte[] Bits = new byte[17];     // bits[1..16] = # symbols of that code length
    public readonly byte[] HuffVal = new byte[256];
}

/// <summary>Decoding-optimised form of a <see cref="HuffTable"/> (Annex F.15 + an 8-bit lookahead). Port of d_derived_tbl_t.</summary>
internal sealed class DerivedTable
{
    public const int Lookahead = 8;
    public readonly int[] MaxCode = new int[18];
    public readonly int[] ValOffset = new int[18];
    public HuffTable Tbl = null!;
    public readonly int[] Lookup = new int[1 << Lookahead];
}

/// <summary>
/// Port of ntrviewer-hr's <c>ntr_huff.c</c> — the bespoke JPEG-domain Huffman codec used by the delta
/// decoder. Tables aren't transmitted; both ends build identical ones with <see cref="GenOptimalTable"/>
/// from fixed frequency distributions. <see cref="BitReader"/> is the MSB-first entropy reader.
/// </summary>
internal static class NtrHuff
{
    /// <summary>Builds an optimal Huffman table from symbol frequencies (JPEG Annex K.2). Mutates <paramref name="freq"/>.</summary>
    public static void GenOptimalTable(HuffTable htbl, long[] freq)
    {
        const int MaxClen = 32;
        var bits = new byte[MaxClen + 1];
        var bitPos = new int[MaxClen + 1];
        var codesize = new int[257];
        var nzIndex = new int[257];
        var others = new int[257];

        for (int i = 0; i < 257; i++) others[i] = -1;

        freq[256] = 1;   // pseudo-symbol guarantees no all-ones code

        int numNz = 0;
        for (int i = 0; i < 257; i++)
        {
            if (freq[i] != 0)
            {
                nzIndex[numNz] = i;
                freq[numNz] = freq[i];
                numNz++;
            }
        }

        for (;;)
        {
            int c1 = -1, c2 = -1;
            long v = 1000000000L, v2 = 1000000000L;
            for (int i = 0; i < numNz; i++)
            {
                if (freq[i] <= v2)
                {
                    if (freq[i] <= v) { c2 = c1; v2 = v; v = freq[i]; c1 = i; }
                    else { v2 = freq[i]; c2 = i; }
                }
            }
            if (c2 < 0) break;

            freq[c1] += freq[c2];
            freq[c2] = 1000000001L;

            codesize[c1]++;
            while (others[c1] >= 0) { c1 = others[c1]; codesize[c1]++; }
            others[c1] = c2;

            codesize[c2]++;
            while (others[c2] >= 0) { c2 = others[c2]; codesize[c2]++; }
        }

        for (int i = 0; i < numNz; i++)
            bits[codesize[i]]++;

        int p = 0;
        for (int i = 1; i <= MaxClen; i++) { bitPos[i] = p; p += bits[i]; }

        // Enforce the 16-bit code-length limit (Rec. ITU-T T.81).
        int ii;
        for (ii = MaxClen; ii > 16; ii--)
        {
            while (bits[ii] > 0)
            {
                int j = ii - 2;
                while (bits[j] == 0) j--;
                bits[ii] -= 2;
                bits[ii - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }
        while (bits[ii] == 0) ii--;
        bits[ii]--;   // drop the pseudo-symbol

        for (int i = 0; i <= 16; i++) htbl.Bits[i] = bits[i];

        for (int i = 0; i < numNz - 1; i++)
        {
            htbl.HuffVal[bitPos[codesize[i]]] = (byte)nzIndex[i];
            bitPos[codesize[i]]++;
        }
    }

    /// <summary>Builds the decode tables (maxcode/valoffset/lookahead). Returns the symbol count. Port of make_d_derived_tbl.</summary>
    public static int MakeDerivedTbl(HuffTable htbl, DerivedTable dtbl)
    {
        dtbl.Tbl = htbl;

        var huffsize = new int[257];
        var huffcode = new uint[257];

        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int i = htbl.Bits[l];
            while (i-- > 0) huffsize[p++] = l;
        }
        huffsize[p] = 0;
        int numsymbols = p;

        uint code = 0;
        int si = huffsize[0];
        p = 0;
        while (huffsize[p] != 0)
        {
            while (huffsize[p] == si) { huffcode[p++] = code; code++; }
            code <<= 1;
            si++;
        }

        p = 0;
        for (int l = 1; l <= 16; l++)
        {
            if (htbl.Bits[l] != 0)
            {
                dtbl.ValOffset[l] = p - (int)huffcode[p];
                p += htbl.Bits[l];
                dtbl.MaxCode[l] = (int)huffcode[p - 1];
            }
            else
            {
                dtbl.MaxCode[l] = -1;
            }
        }
        dtbl.ValOffset[17] = 0;
        dtbl.MaxCode[17] = 0xFFFFF;

        for (int i = 0; i < (1 << DerivedTable.Lookahead); i++)
            dtbl.Lookup[i] = (DerivedTable.Lookahead + 1) << DerivedTable.Lookahead;

        p = 0;
        for (int l = 1; l <= DerivedTable.Lookahead; l++)
        {
            for (int i = 1; i <= htbl.Bits[l]; i++, p++)
            {
                int lookbits = (int)(huffcode[p] << (DerivedTable.Lookahead - l));
                for (int ctr = 1 << (DerivedTable.Lookahead - l); ctr > 0; ctr--)
                {
                    dtbl.Lookup[lookbits] = (l << DerivedTable.Lookahead) | htbl.HuffVal[p];
                    lookbits++;
                }
            }
        }

        return numsymbols;
    }
}

/// <summary>
/// MSB-first entropy bit reader (port of the ntr_huff.h BITREAD/HUFF_DECODE macros + fill_bit_buffer /
/// huff_decode). The delta bitstream carries no 0xff byte-stuffing and no markers, so bytes are shifted
/// in verbatim; when the input is exhausted the buffer simply stops filling.
/// </summary>
internal sealed class BitReader
{
    private const int BitBufSize = 64;
    private const int MinGetBits = BitBufSize - 7;   // 57
    private const int Lookahead = DerivedTable.Lookahead;

    private ulong _getBuffer;
    private int _bitsLeft;
    private byte[] _data = System.Array.Empty<byte>();
    private int _pos;
    private int _bytesLeft;

    public void Init(byte[] data, int offset, int size)
    {
        _data = data; _pos = offset; _bytesLeft = size; _getBuffer = 0; _bitsLeft = 0;
    }

    /// <summary>Source bytes not yet pulled into the bit buffer (diagnostic: ~0 means a clean, in-sync decode).</summary>
    public int BytesRemaining => _bytesLeft;

    private void FillBitBuffer()
    {
        while (_bitsLeft < MinGetBits)
        {
            if (_bytesLeft == 0) break;
            _bytesLeft--;
            byte c = _data[_pos++];
            _getBuffer = (_getBuffer << 8) | c;
            _bitsLeft += 8;
        }
    }

    private void CheckBitBuffer(int nbits) { if (_bitsLeft < nbits) FillBitBuffer(); }

    public int GetBits(int n) { _bitsLeft -= n; return (int)(_getBuffer >> _bitsLeft) & ((1 << n) - 1); }

    /// <summary>Refill-then-read: the value-bit counterpart of the reference's CHECK_BIT_BUFFER + GET_BITS.
    /// Callers that read raw magnitude bits (DC/AC coefficient values) MUST use this, not bare
    /// <see cref="GetBits"/> — otherwise a low buffer makes the shift go negative and reads garbage bits.</summary>
    public int ReadBits(int n) { if (_bitsLeft < n) FillBitBuffer(); return GetBits(n); }

    private int PeekBits(int n) => (int)(_getBuffer >> (_bitsLeft - n)) & ((1 << n) - 1);

    /// <summary>Sign-extend an s-bit magnitude (JPEG HUFF_EXTEND).</summary>
    public static int HuffExtend(int x, int s)
        => x + (((x - (1 << (s - 1))) >> 31) & ((-1 << s) + 1));

    /// <summary>Decode one Huffman symbol, or -1 on hard failure. Port of the HUFF_DECODE macro + huff_decode.</summary>
    public int HuffDecode(DerivedTable htbl)
    {
        if (_bitsLeft < Lookahead)
        {
            FillBitBuffer();
            if (_bitsLeft < Lookahead) return Slow(htbl, 1);
        }
        int look = PeekBits(Lookahead);
        int nb = htbl.Lookup[look] >> Lookahead;
        if (nb <= Lookahead)
        {
            _bitsLeft -= nb;
            return htbl.Lookup[look] & ((1 << Lookahead) - 1);
        }
        return Slow(htbl, nb);
    }

    private int Slow(DerivedTable htbl, int minBits)
    {
        int l = minBits;
        CheckBitBuffer(l);
        int code = GetBits(l);
        while (code > htbl.MaxCode[l])
        {
            code <<= 1;
            CheckBitBuffer(1);
            code |= GetBits(1);
            l++;
        }
        if (l > 16) return 0;
        return htbl.Tbl.HuffVal[code + htbl.ValOffset[l]];
    }
}
