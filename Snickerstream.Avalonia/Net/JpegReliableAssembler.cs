namespace SnickerstreamV2.Net;

/// <summary>
/// Reassembles a "JPEG (Reliable Stream)" <see cref="KcpFrame"/> into a standard baseline JPEG byte
/// stream that any decoder (Skia via Avalonia's <c>Bitmap</c>) can read — a port of ntrviewer-hr's
/// <c>handle_decode_kcp</c> JPEG branch (ntr_rp.c).
///
/// <para>The 3DS transmits only the <b>entropy-coded scan data</b>, split across CPU cores into
/// horizontal bands, with 0xff bytes left <i>un-stuffed</i>. The reference recreates the JPEG header by
/// running a dummy image through libjpeg-turbo at the matching quality/subsampling; we instead
/// <b>synthesize</b> that header directly from the standard Annex-K quantization and Huffman tables
/// (libjpeg-turbo's baseline defaults), so no JPEG <i>encoder</i> is needed. The restart interval is set
/// to one full band (<c>v_adjusted × MCUs-per-row</c>), which makes the per-core boundary markers
/// <c>RST0/RST1/RST2</c> (0xd0+t) fall exactly on the JPEG restart cadence — a valid, in-sequence stream.
/// Each core's scan data is 0xff-stuffed and concatenated; the final band ends with EOI.</para>
/// </summary>
internal static class JpegReliableAssembler
{
    private const int ScreenWidth = 240, ScreenHeight0 = 400, ScreenHeight1 = 320;
    private const int DctSize = 8;

    /// <summary>Assembles the frame to JPEG bytes, or null if the sub-mode isn't plain JPEG (lossless/delta
    /// are later phases).</summary>
    public static byte[]? Assemble(KcpFrame f)
    {
        if (f.IsLossless || f.DeltaProg) return null;   // handled by future phases

        int width = DownsampleWidth(f.Downsample);
        int height = DownsampleHeight(f.Downsample, f.IsTop);
        int mcuWidth = DctSize * (f.ChromaSs == 2 ? 1 : 2);
        int mcusPerRow = (width + mcuWidth - 1) / mcuWidth;          // DIV_ROUND_UP
        int restartInterval = f.VAdjusted * mcusPerRow;

        var jpeg = new List<byte>(64 * 1024);
        WriteHeader(jpeg, width, height, f.Quality, f.ChromaSs, restartInterval);

        for (int t = 0; t < f.CoreCount; t++)
        {
            var packets = f.Cores[t];
            int termSize = f.TermSizes[t];
            for (int i = 0; i < packets.Count; i++)
            {
                var pkt = packets[i];
                int len = i == packets.Count - 1 ? termSize : pkt.Length;
                CopyWithEscape(jpeg, pkt, len);
            }
            jpeg.Add(0xFF);
            jpeg.Add((byte)(t == f.CoreCount - 1 ? 0xD9 : 0xD0 + t));   // RSTt between bands, EOI after last
        }

        return jpeg.ToArray();
    }

    /// <summary>Byte-stuff the raw entropy data: every 0xff becomes 0xff 0x00 (the 3DS omits the stuffing).</summary>
    private static void CopyWithEscape(List<byte> outBuf, byte[] data, int len)
    {
        for (int i = 0; i < len; i++)
        {
            byte b = data[i];
            outBuf.Add(b);
            if (b == 0xFF) outBuf.Add(0x00);
        }
    }

    private static int DownsampleWidth(int ds) => ds is 2 or 3 ? ScreenWidth / 2 : ScreenWidth;
    private static int DownsampleHeight(int ds, bool isTop)
    {
        int full = isTop ? ScreenHeight0 : ScreenHeight1;
        return ds == 3 ? full / 2 : full;
    }

    //---------------------------------------------------------------------
    // Baseline JPEG header synthesis (standard Annex-K tables + IJG quality scaling)
    //---------------------------------------------------------------------
    private static void WriteHeader(List<byte> b, int width, int height, int quality, int chromaSs, int restartInterval)
    {
        // SOI
        Marker(b, 0xD8);

        // APP0 / JFIF (cosmetic but standard)
        Marker(b, 0xE0);
        Len(b, 16);
        b.AddRange(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 }); // "JFIF\0"
        b.Add(1); b.Add(1);            // version 1.1
        b.Add(0);                      // density units: none
        b.Add(0); b.Add(1);            // X density 1
        b.Add(0); b.Add(1);            // Y density 1
        b.Add(0); b.Add(0);            // no thumbnail

        // DQT — luminance (id 0) then chrominance (id 1), each scaled by quality, stored in zig-zag order.
        WriteDqt(b, 0, ScaleQuant(StdLuminanceQuant, quality));
        WriteDqt(b, 1, ScaleQuant(StdChrominanceQuant, quality));

        // SOF0 (baseline). Y sampling per subsampling; Cb/Cr are 1x1.
        int hY = chromaSs == 2 ? 1 : 2;
        int vY = chromaSs == 0 ? 2 : 1;
        Marker(b, 0xC0);
        Len(b, 8 + 3 * 3);
        b.Add(8);                       // precision
        b.Add((byte)(height >> 8)); b.Add((byte)(height & 0xFF));
        b.Add((byte)(width >> 8)); b.Add((byte)(width & 0xFF));
        b.Add(3);                       // components
        b.Add(1); b.Add((byte)((hY << 4) | vY)); b.Add(0);   // Y  -> quant 0
        b.Add(2); b.Add(0x11); b.Add(1);                     // Cb -> quant 1
        b.Add(3); b.Add(0x11); b.Add(1);                     // Cr -> quant 1

        // DHT — standard DC/AC luminance/chrominance tables.
        WriteDht(b, 0x00, StdDcLumBits, StdDcLumVal);
        WriteDht(b, 0x10, StdAcLumBits, StdAcLumVal);
        WriteDht(b, 0x01, StdDcChrBits, StdDcChrVal);
        WriteDht(b, 0x11, StdAcChrBits, StdAcChrVal);

        // DRI — one restart per band.
        Marker(b, 0xDD);
        Len(b, 4);
        b.Add((byte)(restartInterval >> 8)); b.Add((byte)(restartInterval & 0xFF));

        // SOS
        Marker(b, 0xDA);
        Len(b, 6 + 2 * 3);
        b.Add(3);
        b.Add(1); b.Add(0x00);   // Y  -> DC0/AC0
        b.Add(2); b.Add(0x11);   // Cb -> DC1/AC1
        b.Add(3); b.Add(0x11);   // Cr -> DC1/AC1
        b.Add(0); b.Add(63); b.Add(0);   // Ss, Se, Ah/Al
    }

    private static void Marker(List<byte> b, byte code) { b.Add(0xFF); b.Add(code); }
    private static void Len(List<byte> b, int len) { b.Add((byte)(len >> 8)); b.Add((byte)(len & 0xFF)); }

    private static void WriteDqt(List<byte> b, int id, byte[] tableNatural)
    {
        Marker(b, 0xDB);
        Len(b, 2 + 1 + 64);
        b.Add((byte)id);                 // precision 0 (8-bit) | table id
        for (int i = 0; i < 64; i++)
            b.Add(tableNatural[ZigZag[i]]);
    }

    private static void WriteDht(List<byte> b, int classId, byte[] bits, byte[] vals)
    {
        Marker(b, 0xC4);
        Len(b, 2 + 1 + 16 + vals.Length);
        b.Add((byte)classId);            // (class<<4) | id
        b.AddRange(bits);
        b.AddRange(vals);
    }

    /// <summary>IJG quality scaling of a base quantization table, clamped to 8-bit [1,255].</summary>
    private static byte[] ScaleQuant(byte[] baseTable, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var outT = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int q = (baseTable[i] * scale + 50) / 100;
            outT[i] = (byte)Math.Clamp(q, 1, 255);
        }
        return outT;
    }

    // Zig-zag scan order (natural index for each of the 64 zig-zag positions).
    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    };

    // Standard Annex-K quantization tables (natural order).
    private static readonly byte[] StdLuminanceQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    };

    private static readonly byte[] StdChrominanceQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    };

    // Standard Annex-K Huffman tables (BITS[16] + HUFFVAL).
    private static readonly byte[] StdDcLumBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] StdDcLumVal = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] StdDcChrBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] StdDcChrVal = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] StdAcLumBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };
    private static readonly byte[] StdAcLumVal =
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

    private static readonly byte[] StdAcChrBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] StdAcChrVal =
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
