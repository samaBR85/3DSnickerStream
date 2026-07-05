namespace SnickerstreamV2.Net;

/// <summary>
/// Receiving end of ntrviewer-hr's custom one-way "Reliable Stream" protocol — a faithful port of
/// <c>ikcp.c</c>/<c>ikcp.h</c> (branch <c>lossless0</c>). This is NOT stock skywind3000 KCP: it was
/// "redesigned and reimplemented from scratch" for NTRViewer-HR, one-way only, with its own conv id
/// (<c>cid</c>), NACK-based reply, and forward-error-correction grouping.
///
/// <para><b>FEC-lite:</b> the reference links the external <c>fecal</c> erasure-coding library, but we
/// deliberately skip it. Each UDP packet is a 2-byte FEC header <c>[fid:12 | gid:2 | fty:2]</c> followed
/// by a payload. For FEC types with <c>original_count == 1</c> (fty 0/1/2) every packet in the group
/// carries the <i>same</i> original (recovery symbols are literal duplicates), so we just feed each copy
/// as the original — zero FEC math, and a lost copy is covered by any surviving duplicate. For
/// <c>FEC_TYPE_2_3</c> (fty 3: 2 originals + 1 recovery) the two originals (gid 0/1) are fed directly and
/// the recovery symbol (gid 2) is dropped; a genuinely missing original is then recovered by the
/// NACK/retransmit loop (<see cref="Reply"/>), which keeps the stream correct at the cost of one extra
/// round-trip on loss. This removes the entire fecal dependency while preserving reliability.</para>
///
/// <para>Little-endian only (matches the reference's FIXME notes). Single-threaded use: the owning
/// receive loop calls <see cref="Input"/>, drains <see cref="Recv"/>, and periodically calls
/// <see cref="Reply"/> — no internal locking.</para>
/// </summary>
internal sealed class Kcp
{
    // Bit widths (ikcp.h). PID = packet id, CID = conv id, FID = FEC group id, GID = symbol id, FTY = FEC type.
    private const int PID_NBITS = 12;
    private const int CID_NBITS = 1;
    private const int FID_NBITS = 12;
    private const int GID_NBITS = 2;
    private const int FTY_NBITS = 2;

    private const int PID_MASK = (1 << PID_NBITS) - 1; // 4095
    private const int CID_MASK = (1 << CID_NBITS) - 1; // 1
    private const int FID_MASK = (1 << FID_NBITS) - 1; // 4095
    private const int GID_MASK = (1 << GID_NBITS) - 1; // 3
    private const int FTY_MASK = (1 << FTY_NBITS) - 1; // 3

    private const int CountNBits = 16 - PID_NBITS;     // 4 — NACK run-length field in a reply word

    // FEC_COUNTS[fty] from ikcp.c: original symbol count per group (recovery count omitted — see FEC-lite).
    private static readonly int[] FecOriginal = { 1, 1, 1, 2 };
    private static readonly int[] FecTotal = { 1, 2, 3, 3 };

    /// <summary>Low-level send (UDP). Returns bytes sent, or &lt; 0 on failure (mirrors sendto).</summary>
    private readonly Func<byte[], int, int> _output;

    private int _mtu;
    private readonly int _cid;
    private int _inputCid;
    private int _recvPid, _inputPid;
    private int _fid, _gid;

    public bool ShouldReset { get; private set; }
    public bool SessionEstablished { get; private set; }
    public bool SessionJustEstablished { get; set; }
    private bool _sessionDataReceived;
    public int InputCid => _inputCid;
    public int Cid => _cid;

    // One slot per possible pid; holds a copy of the reassembled original payload (length = mtu - 2,
    // i.e. the 2-byte KCP framing header + scan data). null = not yet received.
    private readonly byte[]?[] _segs = new byte[1 << PID_NBITS][];

    public Kcp(int cid, Func<byte[], int, int> output)
    {
        _output = output;
        _cid = _inputCid = cid & CID_MASK;
        _inputPid = _recvPid = PID_MASK;   // (u16)-1 & PID_MASK
    }

    public void SetMtu(int mtu) => _mtu = mtu;

    /// <summary>Segment payload length delivered by <see cref="Recv"/> (mtu minus the 2-byte FEC header).</summary>
    public int SegDataLen => _mtu - 2;

    private static ushort ReadU16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
    }

    //---------------------------------------------------------------------
    // input (a low-level UDP datagram arrived)
    //---------------------------------------------------------------------
    /// <summary>Feed a received UDP datagram. Returns 0 on success; &lt; 0 on protocol error
    /// (caller should reset), &gt; 0 for benign "ignore" states. Mirrors ikcp_input.</summary>
    public int Input(byte[] data, int size)
    {
        if (size < 2) return -10;

        ushort hdr = ReadU16(data, 0);
        int payloadOff = 2;
        int payloadLen = size - 2;

        int fid = (hdr >> (GID_NBITS + FTY_NBITS)) & FID_MASK;
        int gid = (hdr >> FTY_NBITS) & GID_MASK;
        int fty = hdr & FTY_MASK;

        if (payloadLen == 0)
        {
            // Session-control packet (empty payload).
            if (_sessionDataReceived) return 12;

            if (fty == 0 && gid == GID_MASK && (fid & ~CID_MASK) == 0)
            {
                int cid = fid;
                if (cid != _cid) { _inputCid = cid; ShouldReset = true; return -8; }

                ushort reply = (ushort)(
                    ((0 & FID_MASK) << (GID_NBITS + CID_NBITS + 1)) |
                    ((GID_MASK) << (CID_NBITS + 1)) |
                    ((_cid & CID_MASK) << 1));
                var rb = new byte[2];
                WriteU16(rb, 0, reply);
                int ret = _output(rb, 2);
                if (ret < 0) return ret * 0x1000 - 9;

                SessionJustEstablished = true;
                return 0;
            }
            return -9;
        }

        if (!SessionEstablished) return 11;
        if (payloadLen != _mtu - 2) return -1;

        _sessionDataReceived = true;

        int fidCount = (fid - _fid) & FID_MASK;
        if (fidCount < (1 << (FID_NBITS - 1))) { _fid = fid; _gid = gid; }
        if (_fid == fid && _gid < gid) _gid = gid;

        int total = FecTotal[fty];
        if (gid >= total) return -3;

        // FEC-lite: feed originals straight through; recovery symbols of a 2_3 group are skipped.
        if (FecOriginal[fty] == 1 || gid < FecOriginal[fty])
        {
            int r = AddOriginal(data, payloadOff, payloadLen);
            if (r != 0) return r * 0x10 - 8;
        }
        return 0;
    }

    //---------------------------------------------------------------------
    // store one reassembled original into the pid window
    //---------------------------------------------------------------------
    private int AddOriginal(byte[] buf, int off, int size)
    {
        ushort hdr = ReadU16(buf, off);
        int pid = hdr & PID_MASK;
        int cid = (hdr >> PID_NBITS) & CID_MASK;

        _inputCid = cid;
        if (cid != _cid) { ShouldReset = true; return -1; }

        if (((pid - _recvPid) & PID_MASK) <= ((_inputPid - _recvPid) & PID_MASK))
        {
            // Inside the current window: store if new (duplicates are ignored — assumed identical).
            if (_segs[pid] == null)
                _segs[pid] = Copy(buf, off, size);
        }
        else if (((pid - _inputPid) & PID_MASK) < (1 << (PID_NBITS - 1)))
        {
            // Ahead of the window: clear the gap up to the new pid, store, advance input_pid.
            for (int i = pid; i != _inputPid; i = (i - 1) & PID_MASK)
                RemoveOriginal(i);
            _segs[pid] = Copy(buf, off, size);
            _inputPid = pid;
        }
        else if (((_recvPid - pid) & PID_MASK) >= (1 << (PID_NBITS - 2)))
        {
            return -3;
        }
        return 0;
    }

    private void RemoveOriginal(int pid) => _segs[pid] = null;

    private static byte[] Copy(byte[] src, int off, int size)
    {
        var d = new byte[size];
        Array.Copy(src, off, d, 0, size);
        return d;
    }

    //---------------------------------------------------------------------
    // upper-level recv: next in-order segment, or null if none ready
    //---------------------------------------------------------------------
    /// <summary>Returns the next in-order reassembled segment (length <see cref="SegDataLen"/>), or null
    /// if the next pid has not arrived yet. Mirrors ikcp_recv.</summary>
    public byte[]? Recv()
    {
        int nextPid = (_recvPid + 1) & PID_MASK;
        var seg = _segs[nextPid];
        if (seg == null) return null;

        RemoveOriginal(_recvPid);   // free the previously-consumed slot
        _recvPid = nextPid;
        return seg;
    }

    //---------------------------------------------------------------------
    // reset — tell the sender to restart the session under a (new) cid
    //---------------------------------------------------------------------
    public int Reset(int cid)
    {
        ushort hdr = (ushort)(
            ((_fid & FID_MASK) << (GID_NBITS + CID_NBITS + 1)) |
            ((_gid & GID_MASK) << (CID_NBITS + 1)) |
            ((cid & CID_MASK) << 1) |
            1);
        var b = new byte[2];
        WriteU16(b, 0, hdr);
        return _output(b, 2);
    }

    //---------------------------------------------------------------------
    // reply — send NACKs for gaps plus a high-water marker (drives retransmission)
    //---------------------------------------------------------------------
    /// <summary>Emits the ack/nack reply the sender needs to retransmit lost packets. Mirrors ikcp_reply.</summary>
    public int Reply()
    {
        var buf = new byte[_mtu];
        int w = 0;

        ushort hdr = (ushort)(
            ((_fid & FID_MASK) << (GID_NBITS + CID_NBITS + 1)) |
            ((_gid & GID_MASK) << (CID_NBITS + 1)) |
            ((_cid & CID_MASK) << 1));
        WriteU16(buf, w, hdr); w += 2;

        int pid = _recvPid & PID_MASK;
        while (pid != _inputPid)
        {
            pid = (pid + 1) & PID_MASK;

            if (_segs[pid] == null)
            {
                int nackStart = pid;
                int nackCount = 0;

                while (true)
                {
                    pid = (pid + 1) & PID_MASK;
                    if (pid == _inputPid) break;
                    if (_segs[pid] != null) break;
                    nackCount++;

                    if (nackCount == (1 << CountNBits))
                    {
                        ushort full = (ushort)(((nackStart & PID_MASK) << CountNBits) | ((1 << CountNBits) - 1));
                        WriteU16(buf, w, full); w += 2;
                        if (w > _mtu) return -1;
                        nackStart = pid;
                        nackCount = 0;
                    }
                }

                ushort nack = (ushort)(((nackStart & PID_MASK) << CountNBits) | (nackCount & ((1 << CountNBits) - 1)));
                WriteU16(buf, w, nack); w += 2;
                if (w > _mtu) return -1;
            }
        }

        pid = (pid + 1) & PID_MASK;
        WriteU16(buf, w, (ushort)((pid & PID_MASK) << CountNBits)); w += 2;
        if (w > _mtu) return -1;

        int ret = _output(buf, w);
        if (ret < 0) return ret * 0x100 - 9;
        return 0;
    }

    /// <summary>True while there is a gap between the consumed and highest-received pid (drives faster NACKs).</summary>
    public bool HasGap => _recvPid != _inputPid;
}
