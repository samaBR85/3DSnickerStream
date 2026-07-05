using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnickerstreamV2.Net;

/// <summary>
/// Decoder for NTR-HR "Uncompressed (UDP)" frames (config kcp_mode 3 → non-KCP lossless family).
/// Ported from ntrviewer-hr <c>ntr_rp.c</c> (<c>handle_decode_lossless</c> + <c>do_chroma_ss_0_1</c>),
/// for the sub-mode confirmed on real hardware: <b>8-bit YCbCr 4:2:0, color_bias 0</b> (byte-per-channel).
///
/// <para>Wire facts (verified from the 3DS: top 72100 B, bottom 57680 B, 1.500 B/px):</para>
/// <list type="bullet">
/// <item>Each frame is one interlaced <b>column field</b>: 120×400 (top) / 120×320 (bottom), native
///   portrait orientation (same as the JPEG path, which the view rotates 270° upright).</item>
/// <item>The full screen (240 wide) is formed by interleaving the even/odd column fields
///   (<c>frame_id &amp; 1</c>): field column x → full column <c>2·x + evenOdd</c>.</item>
/// <item>Every UDP packet carries a 2-byte lossless sub-header (<c>RP_LOSSLESS_HDR_SIZE</c>) after the
///   4-byte data header; the reassembler keeps them, so we strip 2 bytes per 1444-byte packet chunk.</item>
/// <item>Pixel stream is 2×2 blocks in raster order: Y00 Y01 Y10 Y11 Cb Cr (6 bytes/block).</item>
/// </list>
/// <para>First-cut simplifications (may be tuned after a hardware check): nearest-neighbour chroma
/// upsample (the C uses bilinear); only chroma_ss 0 / color_bias 0 handled (returns null otherwise).</para>
/// </summary>
public sealed class NtrLossless
{
    private const int FieldWidth = 120;   // SCREEN_WIDTH (one column field)
    private const int FullWidth = 240;    // 2 fields interleaved
    private const int PacketChunk = 1444; // RP_PACKET_DATA_SIZE (MTU 1448 - 4-byte data header)
    private const int LosslessHdr = 2;    // RP_LOSSLESS_HDR_SIZE, per packet

    // Persistent full-screen BGRA buffers so even+odd fields accumulate into one image.
    private byte[]? _buf;
    private int _bufH;

    /// <summary>
    /// Decodes one column field and returns the full accumulated screen as a fresh BGRA bitmap
    /// (240×height, native orientation), or null if the payload isn't the supported sub-mode.
    /// </summary>
    public WriteableBitmap? Decode(byte[] payload, int evenOdd, int height)
    {
        // 1) Strip the per-packet 2-byte lossless sub-headers → contiguous pixel byte stream.
        //    Also read the frame header (first packet's 2 bytes) to confirm the sub-mode.
        if (payload.Length < LosslessHdr) return null;
        byte h0 = payload[0];
        bool isHuff = (h0 & 0x1) != 0;
        int huffNo = (h0 >> 1) & 0x7;
        int chromaSs = (h0 >> 4) & 0x3;
        int colorBias = (h0 >> 6) & 0x3;
        if (isHuff || huffNo != 0 || chromaSs != 0 || colorBias != 0)
            return null;   // needs the Huffman / other sub-mode decoders (later phases / 1b tuning)

        var px = StripPacketHeaders(payload);

        // 2) Expected size for 4:2:0 over a 120×height field: Y (120·h) + Cb + Cr (each 60·(h/2)).
        int blocksX = FieldWidth / 2;      // 60
        int blocksY = height / 2;          // 200 (top) / 160 (bottom)
        long need = (long)blocksX * blocksY * 6;
        if (px.Length < need) return null; // partial/lossy frame — skip it

        EnsureBuffer(height);
        var buf = _buf!;

        // 3) Unpack 2×2 YCbCr blocks and write straight into the full BGRA buffer, interleaving
        //    this field's columns (x → 2x + evenOdd). Nearest chroma upsampling.
        int p = 0;
        for (int by = 0; by < blocksY; by++)
        {
            int y0 = by * 2;
            for (int bx = 0; bx < blocksX; bx++)
            {
                int x0 = bx * 2;
                byte y00 = px[p++], y01 = px[p++], y10 = px[p++], y11 = px[p++];
                byte cb = px[p++], cr = px[p++];

                WritePixel(buf, x0,     y0,     evenOdd, height, y00, cb, cr);
                WritePixel(buf, x0 + 1, y0,     evenOdd, height, y01, cb, cr);
                WritePixel(buf, x0,     y0 + 1, evenOdd, height, y10, cb, cr);
                WritePixel(buf, x0 + 1, y0 + 1, evenOdd, height, y11, cb, cr);
            }
        }

        // 4) Copy the full buffer into a fresh WriteableBitmap (owned by the caller).
        var wb = new WriteableBitmap(new PixelSize(FullWidth, height), new Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, fb.Address, buf.Length);
        return wb;
    }

    private void EnsureBuffer(int height)
    {
        if (_buf != null && _bufH == height) return;
        _buf = new byte[FullWidth * height * 4];
        _bufH = height;
    }

    /// <summary>YCbCr (JFIF) → BGRA at full column <c>2·fieldX + evenOdd</c>, row <c>y</c>.</summary>
    private static void WritePixel(byte[] buf, int fieldX, int y, int evenOdd, int height, byte yv, byte cb, byte cr)
    {
        int fullX = fieldX * 2 + evenOdd;
        if (fullX >= FullWidth || y >= height) return;

        double cbd = cb - 128.0, crd = cr - 128.0;
        int r = (int)(yv + 1.402 * crd + 0.5);
        int g = (int)(yv - 0.344136 * cbd - 0.714136 * crd + 0.5);
        int b = (int)(yv + 1.772 * cbd + 0.5);

        int o = (y * FullWidth + fullX) * 4;
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
