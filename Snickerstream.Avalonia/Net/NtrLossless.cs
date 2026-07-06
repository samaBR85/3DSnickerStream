using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnickerstreamV2.Net;

/// <summary>
/// Decoder for NTR-HR "Uncompressed (UDP)" frames (config kcp_mode 3 → non-KCP lossless family).
/// Ported from ntrviewer-hr <c>ntr_rp.c</c> (<c>handle_decode_lossless</c> + <c>do_chroma_ss_0_1</c> +
/// <c>do_curr_next_color_1_2</c>), verified against real hardware.
///
/// <para>Wire facts (confirmed on the 3DS):</para>
/// <list type="bullet">
/// <item>Each frame is a full 240×height image (top 240×400, bottom 240×320), native portrait — the
///   view rotates 270° upright, same as the JPEG path.</item>
/// <item>YCbCr <b>4:2:0</b> in 2×2 blocks, raster order Y00 Y01 Y10 Y11 Cb Cr. Samples are bit-packed
///   MSB-first by <c>color_bias</c>: 0 = 8/8/8, 1 = Y6/Cb5/Cr5, 2 = 4/4/4 (Y scaled up, chroma
///   dead-zone re-centred). bias 0 doubles the bytes and overruns UDP, so we request bias 1.</item>
/// <item>Every UDP packet carries a 2-byte lossless sub-header after the 4-byte data header; the
///   reassembler keeps them, so we strip 2 bytes per 1444-byte packet chunk.</item>
/// </list>
/// <para>Chroma is nearest-neighbour upsampled (the C uses bilinear); Huffman sub-modes are left to the
/// KCP phases.</para>
/// </summary>
public sealed class NtrLossless
{
    private const int FullWidth = 240;    // SCREEN_WIDTH — full screen width
    private const int PacketChunk = 1444; // RP_PACKET_DATA_SIZE (MTU 1448 - 4-byte data header)
    private const int LosslessHdr = 2;    // RP_LOSSLESS_HDR_SIZE, per packet

    private byte[]? _buf;                  // reused full-screen BGRA buffer
    private int _bufH;

    /// <summary>
    /// Decodes one full frame (240×height, native portrait, 4:2:0) into a fresh BGRA bitmap, or null
    /// if the sub-mode is unsupported / the frame is too incomplete. <paramref name="evenOdd"/> is
    /// unused (kept for the header) — at the observed color biases the 3DS sends full frames, not fields.
    /// </summary>
    public WriteableBitmap? Decode(byte[] payload, int evenOdd, int height)
    {
        if (DecodeBuffer(payload, evenOdd, height) is not { } r) return null;
        var wb = new WriteableBitmap(new PixelSize(r.w, r.h), new Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
            System.Runtime.InteropServices.Marshal.Copy(r.bgra, 0, fb.Address, r.w * r.h * 4);
        return wb;
    }

    /// <summary>
    /// Decodes one full frame into the reused BGRA buffer (240×height, portrait), or null if the sub-mode
    /// is unsupported / the frame is too incomplete. The returned array is owned by this decoder and reused
    /// on the next call — the caller must copy/consume it synchronously (StreamView wraps it immediately).
    /// </summary>
    public (byte[] bgra, int w, int h)? DecodeBuffer(byte[] payload, int evenOdd, int height)
    {
        if (payload.Length < LosslessHdr) return null;
        byte h0 = payload[0];
        bool isHuff = (h0 & 0x1) != 0;
        int chromaSs = (h0 >> 4) & 0x3;
        int colorBias = (h0 >> 6) & 0x3;

        // Only 4:2:0 with a supported color bias is handled (Huffman modes come with the KCP phases).
        int[]? cbits = colorBias switch { 0 => new[] { 8, 8, 8 }, 1 => new[] { 6, 5, 5 }, 2 => new[] { 4, 4, 4 }, _ => null };
        if (isHuff || chromaSs != 0 || cbits == null) return null;

        var px = StripPacketHeaders(payload);

        // Full frame: 240×height, 4:2:0, 2×2 blocks. Sample widths per color_bias (bits): bias 0 =
        // 8/8/8 (byte-per-channel), bias 1 = Y6/Cb5/Cr5, bias 2 = 4/4/4.
        int blocksX = FullWidth / 2;       // 120
        int blocksY = height / 2;          // 200 (top) / 160 (bottom)
        long needBits = (long)blocksX * blocksY * (4L * cbits[0] + cbits[1] + cbits[2]);
        if (px.Length < (needBits + 7) / 8) return null; // partial/lossy frame — skip it

        EnsureBuffer(height);
        var buf = _buf!;
        var br = new BitReader(px);

        // Unpack 2×2 YCbCr blocks (Y00 Y01 Y10 Y11 Cb Cr) MSB-first, nearest chroma upsample.
        for (int by = 0; by < blocksY; by++)
        {
            int fy0 = by * 2;
            for (int bx = 0; bx < blocksX; bx++)
            {
                int fx0 = bx * 2;
                byte y00 = Sample(br, cbits[0], 0, colorBias);
                byte y01 = Sample(br, cbits[0], 0, colorBias);
                byte y10 = Sample(br, cbits[0], 0, colorBias);
                byte y11 = Sample(br, cbits[0], 0, colorBias);
                byte cb = Sample(br, cbits[1], 1, colorBias);
                byte cr = Sample(br, cbits[2], 2, colorBias);

                WritePixel(buf, fx0,     fy0,     height, y00, cb, cr);
                WritePixel(buf, fx0 + 1, fy0,     height, y01, cb, cr);
                WritePixel(buf, fx0,     fy0 + 1, height, y10, cb, cr);
                WritePixel(buf, fx0 + 1, fy0 + 1, height, y11, cb, cr);
            }
        }

        return (buf, FullWidth, height);
    }

    /// <summary>Reads one bit-packed component and dequantizes it (ported from do_curr_next_color_1_2).</summary>
    private static byte Sample(BitReader br, int bits, int comp, int colorBias)
    {
        int v = br.Read(bits);
        if (colorBias == 0) return (byte)v;                 // do_curr_next_color_0: raw byte
        int half = 1 << (8 - bits - 1);
        double outv = (v << (8 - bits)) + half;
        if (comp == 0) return Clamp(outv);                  // luma: scale up only
        // chroma: dead-zone re-centre around 128
        double o = outv - 128.0;
        double sign = o >= 0 ? 1.0 : -1.0;
        double oa = System.Math.Max(System.Math.Abs(o) - half, 0.0);
        return Clamp(oa * sign + 128.0);
    }

    private static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>MSB-first bit reader, matching ntr-hr's do_curr_next_color_1_2 exactly.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _d;
        private int _pos;
        private int _left = 8;
        public BitReader(byte[] d) => _d = d;

        public int Read(int n)
        {
            int ret;
            if (n > _left)
            {
                ret = At(_pos++) & ((1 << _left) - 1);
                n -= _left;
                ret <<= n;
                _left = 8 - n;
                ret |= (At(_pos) >> _left) & ((1 << n) - 1);
            }
            else if (n == _left)
            {
                ret = At(_pos++) & ((1 << n) - 1);
                _left = 8;
            }
            else
            {
                _left -= n;
                ret = (At(_pos) >> _left) & ((1 << n) - 1);
            }
            return ret;
        }

        private int At(int i) => i < _d.Length ? _d[i] : 0;
    }

    private void EnsureBuffer(int height)
    {
        if (_buf != null && _bufH == height) return;
        _buf = new byte[FullWidth * height * 4];
        _bufH = height;
    }

    /// <summary>YCbCr (JFIF) → BGRA at (x, y) in the full 240×height frame.</summary>
    private static void WritePixel(byte[] buf, int x, int y, int height, byte yv, byte cb, byte cr)
    {
        if (x >= FullWidth || y >= height) return;

        double cbd = cb - 128.0, crd = cr - 128.0;
        int r = (int)(yv + 1.402 * crd + 0.5);
        int g = (int)(yv - 0.344136 * cbd - 0.714136 * crd + 0.5);
        int b = (int)(yv + 1.772 * cbd + 0.5);

        int o = (y * FullWidth + x) * 4;
        buf[o + 0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        buf[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
        buf[o + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
        buf[o + 3] = 255;
    }

    /// <summary>Removes the 2-byte lossless sub-header from each 1444-byte packet chunk.</summary>
    private static byte[] StripPacketHeaders(byte[] payload)
    {
        int packets = (payload.Length + PacketChunk - 1) / PacketChunk;
        var outBuf = new byte[payload.Length - packets * LosslessHdr];
        int w = 0;
        for (int i = 0; i < packets; i++)
        {
            int start = i * PacketChunk;
            int len = System.Math.Min(PacketChunk, payload.Length - start);
            int dataStart = start + LosslessHdr;
            int dataLen = len - LosslessHdr;
            if (dataLen <= 0) continue;
            System.Array.Copy(payload, dataStart, outBuf, w, dataLen);
            w += dataLen;
        }
        return w == outBuf.Length ? outBuf : outBuf[..w];
    }
}
