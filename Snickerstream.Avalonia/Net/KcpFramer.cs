namespace SnickerstreamV2.Net;

/// <summary>A fully-received Reliable-Stream frame: per-core entropy-coded scan segments + metadata.</summary>
internal sealed class KcpFrame
{
    public bool IsTop;
    public bool IsLossless;
    public bool DeltaProg;
    public int Quality;       // JPEG quality (or color_bias when IsLossless)
    public int ChromaSs;      // 0 = 4:2:0, 1 = 4:2:2, 2 = 4:4:4
    public int Downsample;    // 2 = full, 3 = half
    public int EvenOdd;
    public int CoreCount;     // number of horizontal bands (1..3)
    public int VAdjusted;     // MCU-rows per band (restart interval driver)
    public required List<byte[]>[] Cores;   // Cores[t] = full 1444-byte packets followed by the final partial
    public required int[] TermSizes;         // TermSizes[t] = byte count of core t's final (partial) packet
}

/// <summary>
/// Framing layer above <see cref="Kcp"/> — a faithful port of ntrviewer-hr's <c>handle_recv_kcp</c>
/// (ntr_rp.c). The KCP stream is a strictly in-order byte stream of 1446-byte segments; each segment's
/// first u16 doubles as the KCP window header AND carries a work slot (w) + core tag (t) in its high bits:
/// <c>[pid:12 | cid:1 | w:1 | t:2]</c>. Cores <c>t &lt; 3</c> carry full 1444-byte scan packets; the
/// terminator <c>t == 3</c> carries the per-frame metadata header plus each core's final partial packet,
/// and completing it yields a <see cref="KcpFrame"/> ready for JPEG (or, in later phases, lossless/delta)
/// reassembly.
/// </summary>
internal sealed class KcpFramer
{
    private const int PacketSize = 1444;   // RP_KCP_PACKET_SIZE (segment data after the 2-byte KCP header)
    private const int MaxPackets = 240;    // RP_MAX_PACKET_COUNT
    private const int CoreMax = 3;         // RP_CORE_COUNT_MAX
    private const int WorkCount = 2;       // RP_KCP_WORK_COUNT (w)
    private const int QueueCount = 3;      // RP_WORK_COUNT (queue_w rotation)
    private const int NtrColorBiasMax = 2;
    private const int JpegDctSize = 8;

    private const int ScreenWidth = 240, ScreenHeight0 = 400, ScreenHeight1 = 320;
    private const int LosslessBlockSize = 8;
    private const int ScreenTop = 0;   // enum SCREEN_TOP == 0

    private sealed class CoreRecv
    {
        public readonly List<byte[]> Packets = new();
        public byte[]? TermBuf;   // the final partial packet, filled incrementally across terminator segments
        public int TermSize;
    }

    private sealed class WorkInfo
    {
        public bool Started;
        public bool IsTop, IsLossless, DeltaProg;
        public int Quality, ChromaSs, Downsample, EvenOdd, CoreCount, VAdjusted, VLastAdjusted;
        public readonly int[] TermSizes = new int[CoreMax];
        public int TermCount;
        public int LastTerm;
        public int LastTermSize;

        public void Reset()
        {
            Started = false;
            IsTop = IsLossless = DeltaProg = false;
            Quality = ChromaSs = Downsample = EvenOdd = CoreCount = VAdjusted = VLastAdjusted = 0;
            Array.Clear(TermSizes, 0, CoreMax);
            TermCount = LastTerm = LastTermSize = 0;
        }
    }

    private readonly CoreRecv[,,] _recv = new CoreRecv[WorkCount, QueueCount, CoreMax];
    private readonly WorkInfo[,] _info = new WorkInfo[WorkCount, QueueCount];
    private readonly int[] _recvW = new int[WorkCount];

    public KcpFramer()
    {
        for (int w = 0; w < WorkCount; w++)
            for (int q = 0; q < QueueCount; q++)
            {
                _info[w, q] = new WorkInfo();
                for (int t = 0; t < CoreMax; t++)
                    _recv[w, q, t] = new CoreRecv();
            }
    }

    private static ushort ReadU16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

    /// <summary>Feed one in-order KCP segment (as delivered by <see cref="Kcp.Recv"/>). Returns the
    /// completed frame when a terminator finishes a frame, otherwise null; a negative error is signalled
    /// by throwing <see cref="KcpFramingException"/> so the caller can reset the session.</summary>
    public KcpFrame? Feed(byte[] seg, int size)
    {
        if (size < 2) throw new KcpFramingException(-1);
        ushort hdr = ReadU16(seg, 0);
        int off = 2;
        size -= 2;

        int w = (hdr >> (12 + 1)) & 0x1;       // PID_NBITS + CID_NBITS
        int t = (hdr >> (12 + 1 + 1)) & 0x3;   // + RP_KCP_HDR_W_NBITS
        int queueW = _recvW[w];
        var info = _info[w, queueW];

        if (t < CoreMax)
        {
            if (info.TermCount != 0) throw new KcpFramingException(-9);
            if (size != PacketSize) throw new KcpFramingException(-2);
            var recv = _recv[w, queueW, t];
            if (recv.Packets.Count >= MaxPackets) throw new KcpFramingException(-4);
            var pkt = new byte[PacketSize];
            Array.Copy(seg, off, pkt, 0, PacketSize);
            recv.Packets.Add(pkt);
            return null;
        }

        // t == CoreMax: terminator.
        if (info.TermCount == 0)
        {
            if (size < 2) throw new KcpFramingException(-3);
            ushort m = ReadU16(seg, off); off += 2; size -= 2;

            int quality = m & 0x7F;                 // RP_KCP_HDR_QUALITY_NBITS = 7
            int coreCount = (m >> 7) & 0x3;         // RP_KCP_HDR_T_NBITS = 2
            int topBot = (m >> 9) & 0x1;
            int chromaSs = (m >> 10) & 0x3;         // RP_KCP_HDR_CHROMASS_NBITS = 2
            bool deltaProg = ((m >> 12) & 0x1) != 0;
            int downsample = (m >> 13) & 0x3;       // RP_KCP_HDR_DOWNSAMPLE_NBITS = 2
            bool exHdr = ((m >> 15) & 0x1) != 0;

            int evenOdd = 0;
            if (exHdr)
            {
                if (size < 2) throw new KcpFramingException(-3);
                ushort e = ReadU16(seg, off); off += 2; size -= 2;
                evenOdd = e & 0x1;
            }

            if (coreCount == 0) return null;   // reserved for future extension

            bool isLossless;
            if (deltaProg)
            {
                isLossless = (quality & (1 << 6)) > 0;   // RP_KCP_HDR_QUALITY_NBITS - 1
                quality &= (1 << 5) - 1;                 // RP_DQ_HDR_QUALITY_NBITS = 5
            }
            else
            {
                isLossless = quality <= NtrColorBiasMax;
            }

            info.IsLossless = isLossless;
            info.Quality = quality;   // color_bias when lossless
            info.CoreCount = coreCount;
            info.IsTop = topBot == ScreenTop;
            info.ChromaSs = chromaSs;
            info.Downsample = downsample;
            info.EvenOdd = evenOdd;
            info.DeltaProg = deltaProg;
            info.Started = true;

            for (int ct = 0; ct < coreCount; ct++)
            {
                if (size < 2) throw new KcpFramingException(-6);
                ushort c = ReadU16(seg, off); off += 2; size -= 2;

                int vAdjusted = (c >> 11) & 0x1F;   // RP_KCP_HDR_SIZE_NBITS = 11, RC_NBITS = 5
                int termSize = c & 0x7FF;           // RP_KCP_HDR_SIZE_NBITS = 11
                info.TermSizes[ct] = termSize;

                if (ct == coreCount - 1)
                {
                    if (coreCount > 1 && vAdjusted > info.VAdjusted) throw new KcpFramingException(-8);
                    info.VLastAdjusted = vAdjusted;
                }
                else
                {
                    int vTotal = isLossless
                        ? LosslessGetVTotal(info.IsTop)
                        : JpegGetVTotal(chromaSs, downsample, info.IsTop);
                    if (coreCount == 1)
                    {
                        if (vAdjusted == (vTotal & 0x1F)) vAdjusted = vTotal;
                        else throw new KcpFramingException(-6);
                    }
                    else
                    {
                        if (vAdjusted < (vTotal + coreCount - 1) / coreCount) vAdjusted += 1 << 5;
                    }
                    if (ct == 0) info.VAdjusted = vAdjusted;
                    else if (info.VAdjusted != vAdjusted) throw new KcpFramingException(-7);
                }
            }
        }

        // Distribute the terminator's payload into each core's final partial packet.
        while (true)
        {
            var recv = _recv[w, queueW, info.LastTerm];
            int wantTerm = info.TermSizes[info.LastTerm];
            recv.TermBuf ??= new byte[wantTerm];
            int left = wantTerm - info.LastTermSize;

            if (left == 0)
            {
                recv.Packets.Add(recv.TermBuf!);   // the final (partial) packet
                recv.TermSize = info.LastTermSize;
                recv.TermBuf = null;

                info.LastTerm++;
                info.LastTermSize = 0;

                if (info.LastTerm == info.CoreCount)
                {
                    var frame = BuildFrame(w, queueW, info);
                    ResetWork(w, queueW);
                    _recvW[w] = (_recvW[w] + 1) % QueueCount;
                    return frame;
                }
                continue;
            }

            if (size == 0) break;

            int chunk = Math.Min(left, size);
            Array.Copy(seg, off, recv.TermBuf!, info.LastTermSize, chunk);
            off += chunk;
            size -= chunk;
            info.LastTermSize += chunk;
        }

        info.TermCount++;
        return null;
    }

    private KcpFrame BuildFrame(int w, int queueW, WorkInfo info)
    {
        var cores = new List<byte[]>[info.CoreCount];
        var termSizes = new int[info.CoreCount];
        for (int t = 0; t < info.CoreCount; t++)
        {
            cores[t] = _recv[w, queueW, t].Packets;
            termSizes[t] = _recv[w, queueW, t].TermSize;
        }
        return new KcpFrame
        {
            IsTop = info.IsTop,
            IsLossless = info.IsLossless,
            DeltaProg = info.DeltaProg,
            Quality = info.Quality,
            ChromaSs = info.ChromaSs,
            Downsample = info.Downsample,
            EvenOdd = info.EvenOdd,
            CoreCount = info.CoreCount,
            VAdjusted = info.VAdjusted,
            Cores = cores,
            TermSizes = termSizes,
        };
    }

    private void ResetWork(int w, int queueW)
    {
        for (int t = 0; t < CoreMax; t++)
        {
            // BuildFrame handed the Packets list to the frame; give this slot a fresh one.
            _recv[w, queueW, t] = new CoreRecv();
        }
        _info[w, queueW].Reset();
    }

    private static int DownsampleHeight(int downsample, bool isTop)
    {
        int full = isTop ? ScreenHeight0 : ScreenHeight1;
        return downsample == 3 ? full / 2 : full;
    }

    private static int JpegGetVTotal(int chromaSs, int downsample, bool isTop)
    {
        int h = JpegDctSize * (chromaSs == 0 ? 2 : 1);
        return DownsampleHeight(downsample, isTop) / h;
    }

    private static int LosslessGetVTotal(bool isTop)
        => DownsampleHeight(0, isTop) / LosslessBlockSize;
}

/// <summary>Signals a fatal framing error (negative return in the reference) — the caller resets KCP.</summary>
internal sealed class KcpFramingException : Exception
{
    public int Code { get; }
    public KcpFramingException(int code) : base($"KCP framing error {code}") => Code = code;
}
