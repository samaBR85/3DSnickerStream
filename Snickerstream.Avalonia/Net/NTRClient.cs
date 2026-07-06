using System.IO;
using System.Net;
using System.Net.Sockets;

namespace SnickerstreamV2.Net;

/// <summary>
/// NTR remoteplay client.
///
/// Init is a single 84-byte command sent over TCP/8000 (cmd 901). The 3DS then
/// pushes JPEG slices over UDP to the PC on the listen port (default 8001), which we
/// reassemble per screen. A watchdog re-sends init up to 3 times if no frame arrives.
/// </summary>
public sealed class NTRClient : IStreamClient
{
    public event Action<StreamFrame>? FrameReady;
    public event Action<string>? Status;
    public event Action<string>? Failed;
    public event Action? FirstFrame;

    private readonly string _ip;
    private readonly int _listenPort;
    private int _quality;
    private readonly int _priorityFactor;
    private bool _priorityTop;
    private readonly int _qos;

    // NTR-HR native modes (kcp_mode config value; 0 = legacy JPEG-compat). See BuildInitPacket.
    private readonly int _kcpMode;
    private readonly int _bandwidth;
    private readonly int _losslessColor;

    /// <summary>NTR_COLOR_BIAS_MAX from ntr-hr const.h.</summary>
    private const int NtrColorBiasMax = 2;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _udpTask;
    private Task? _watchdogTask;
    private volatile bool _firstFrameSeen;
    private volatile bool _stopped;

    // KCP (Reliable Stream) state — only used when the selected mode routes through KCP.
    private IPEndPoint? _remoteEP;          // source of the last datagram; where NACK replies go
    private readonly object _sendLock = new();
    private const int KcpMtu = 1448;        // RP_PACKET_SIZE
    private const int KcpPacketSize = 1444; // RP_KCP_PACKET_SIZE
    private const int LosslessBlockSize = 16; // ntr_rp.c LOSSLESS_BLOCK_SIZE
    private NtrJpegDelta? _delta;           // persistent cross-frame state for the Delta mode
    private NtrLosslessRs? _losslessRs;     // decoder for Lossless (Reliable Stream)
    // Delta downsample==2 is column-interlaced: each frame carries half the columns and is woven with the
    // previous one (screen_process). Two persistent half-width buffers per screen hold curr/prev.
    private readonly byte[]?[,] _deltaScratch = new byte[2, 2][];

    private readonly FrameReassembler _top = new(Screen.Top);
    private readonly FrameReassembler _bottom = new(Screen.Bottom);

    public NTRClient(string ip, int listenPort, int quality, int priorityFactor, bool priorityTop, int qos,
                     int kcpMode = 0, int bandwidth = 16, int losslessColor = 0)
    {
        _ip = ip;
        _listenPort = listenPort;
        _quality = Math.Clamp(quality, 10, 100);
        _priorityFactor = Math.Clamp(priorityFactor, 0, 10);
        _priorityTop = priorityTop;
        _qos = Math.Clamp(qos, 2, 100);
        _kcpMode = kcpMode;
        _bandwidth = Math.Clamp(bandwidth, 1, 64);
        _losslessColor = Math.Clamp(losslessColor, 0, NtrColorBiasMax);
    }

    /// <summary>
    /// Builds the 84-byte NTR remoteplay command packet (magic 0x12345678, seq 3000, cmd 901).
    /// All fields little-endian; the 16 u32 arguments start at 0x10.
    /// <para><paramref name="kcpMode"/> is the NTR-HR <c>ntr_rp_config_t.kcp_mode</c> value (0..5).
    /// When 0 this emits the legacy JPEG-compat packet byte-for-byte (unchanged). Otherwise it emits
    /// the new-style packet NTR-HR needs: arg[2]=bandwidth, arg[3]=guarding magic (0x53B7B85C — the
    /// gate that switches NTR-HR out of legacy mode), arg[4]=port | mode flags (bit30 KCP, bit31 delta,
    /// bit29 lossless + 2-bit color bias at 27). Layout ported from ntr-hr ntr_hb.c:344-361.</para>
    /// </summary>
    public static byte[] BuildInitPacket(int quality, int priorityFactor, bool priorityTop, int qos,
                                         int kcpMode = 0, int bandwidth = 16, int losslessColor = 0, int listenPort = 8001)
    {
        var p = new byte[84];
        void U32(int off, uint v)
        {
            p[off + 0] = (byte)(v & 0xFF);
            p[off + 1] = (byte)((v >> 8) & 0xFF);
            p[off + 2] = (byte)((v >> 16) & 0xFF);
            p[off + 3] = (byte)((v >> 24) & 0xFF);
        }
        U32(0x00, 0x12345678);          // magic
        U32(0x04, 3000);                // seq
        U32(0x08, 0);                   // type
        U32(0x0C, 901);                 // cmd = remoteplay

        if (kcpMode == 0)
        {
            // Legacy JPEG-compat — unchanged from what already works.
            p[0x10] = (byte)Math.Clamp(priorityFactor, 0, 10);
            p[0x11] = (byte)(priorityTop ? 1 : 0);
            p[0x14] = (byte)Math.Clamp(quality, 10, 100);
            p[0x1A] = (byte)Math.Clamp(qos * 2, 0, 255);  // QoS x2
            return p;
        }

        // New-style NTR-HR config (unlocks Uncompressed / Reliable-Stream / Lossless / Delta).
        int kcpSub = kcpMode % 3;   // 0 NONE, 1 ON, 2 ON_DELTA
        int lossless = kcpMode / 3; // 0 JPEG family, 1 lossless family
        U32(0x10, ((uint)(priorityTop ? 1 : 0) << 8) | (uint)Math.Clamp(priorityFactor, 0, 10));
        U32(0x14, (uint)Math.Clamp(quality, 1, 100));
        U32(0x18, (uint)Math.Clamp(bandwidth, 1, 64) * 128u * 1024u);
        U32(0x1C, 1404036572u);         // guarding magic (0x53B7B85C)
        uint flags = (uint)(ushort)listenPort
            | (kcpSub != 0 ? 1u << 30 : 0u)
            | (kcpSub == 2 ? 1u << 31 : 0u)
            | (lossless != 0
                ? (1u << 29) | ((uint)((NtrColorBiasMax - Math.Clamp(losslessColor, 0, NtrColorBiasMax)) & 0x3) << 27)
                : 0u);
        U32(0x20, flags);
        return p;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // Bind com SO_REUSEADDR: reconexão rápida não falha se o SO ainda não
            // liberou o socket anterior na mesma porta.
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // Buffer de recepção grande: o NTR manda os pedaços do JPEG em rajada por UDP;
            // um buffer pequeno do SO derruba pacotes na rajada, e um pedaço perdido descarta
            // o frame inteiro, baixando o FPS efetivo. Alguns MB absorvem a rajada.
            try { udp.Client.ReceiveBufferSize = 4 * 1024 * 1024; } catch { /* best effort */ }
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
            _udp = udp;
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"Could not listen on UDP {_listenPort}: {ex.Message}");
            return;
        }

        // Modes whose kcp_mode has a non-zero KCP sub (1/2/4/5) stream over the Reliable Stream (KCP)
        // transport; 0/3 (JPEG-compat / Uncompressed) stay on the plain-UDP reassembler.
        bool useKcp = _kcpMode % 3 != 0;
        _udpTask = Task.Run(() => (useKcp ? (Action)(() => KcpReceiveLoop(token)) : () => ReceiveLoopSync(token))(), token);
        _watchdogTask = Task.Run(() => WatchdogLoop(token), token);
    }

    private void ReceiveLoopSync(CancellationToken token) => ReceiveLoop(token).GetAwaiter().GetResult();

    /// <summary>
    /// Reliable Stream (KCP) receive loop. Feeds datagrams to <see cref="Kcp"/>, drains completed
    /// segments through <see cref="KcpFramer"/>, reassembles JPEG frames, and periodically sends the
    /// ack/nack reply that drives the 3DS's retransmission. Mirrors ntr_rp.c's receive_from_socket +
    /// socket_action + socket_reply.
    /// </summary>
    private void KcpReceiveLoop(CancellationToken token)
    {
        var udp = _udp;
        if (udp == null) return;
        try { udp.Client.ReceiveTimeout = 8; } catch { }

        int cid = 0;
        var kcp = new Kcp(cid, KcpOutput);
        kcp.SetMtu(KcpMtu);
        var framer = new KcpFramer();
        _delta = new NtrJpegDelta();
        _losslessRs = new NtrLosslessRs();
        long replyTime = 0;
        bool needReset = false;

        void Restart()
        {
            int newCid = (cid + 1) & 1;
            try { kcp.Reset(kcp.Cid); } catch { }
            cid = newCid;
            kcp = new Kcp(cid, KcpOutput);
            kcp.SetMtu(KcpMtu);
            framer = new KcpFramer();
            _delta?.Reset();
            _losslessRs?.ResetDelta();
            Array.Clear(_deltaScratch, 0, _deltaScratch.Length);
            needReset = false;
        }

        while (!token.IsCancellationRequested)
        {
            byte[] data;
            var ep = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                data = udp.Receive(ref ep);
                _remoteEP = ep;
            }
            catch (SocketException se) when (se.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                DrainKcp(kcp, framer, ref needReset);
                if (needReset) { Restart(); continue; }
                MaybeReply(kcp, ref replyTime);
                continue;
            }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            int ret = kcp.Input(data, data.Length);
            if (ret < 0) { Restart(); continue; }
            if (kcp.SessionJustEstablished)
                kcp.PostInput();   // promote just-established -> established (mirrors socket_action)

            DrainKcp(kcp, framer, ref needReset);
            if (needReset) { Restart(); continue; }
            MaybeReply(kcp, ref replyTime);
        }
    }

    private void DrainKcp(Kcp kcp, KcpFramer framer, ref bool needReset)
    {
        if (kcp.SessionJustEstablished) return;   // reference guards the drain the same way
        byte[]? seg;
        while ((seg = kcp.Recv()) != null)
        {
            KcpFrame? frame;
            try { frame = framer.Feed(seg, kcp.SegDataLen); }
            catch (KcpFramingException) { needReset = true; return; }
            if (frame != null) EmitKcpFrame(frame);
        }
    }

    private void EmitKcpFrame(KcpFrame frame)
    {
        if (frame.IsLossless && frame.DeltaProg) { EmitLosslessRsDeltaFrame(frame); return; }
        if (frame.IsLossless) { EmitLosslessRsFrame(frame); return; }
        if (frame.DeltaProg) { EmitDeltaFrame(frame); return; }

        var bytes = JpegReliableAssembler.Assemble(frame);
        if (bytes == null) return;
        var sf = new StreamFrame(frame.IsTop ? Screen.Top : Screen.Bottom, bytes);
        if (!_firstFrameSeen) { _firstFrameSeen = true; FirstFrame?.Invoke(); }
        FrameReady?.Invoke(sf);
    }

    /// <summary>Decodes a "Lossless (Reliable Stream)" frame with <see cref="NtrLosslessRs"/> (per-core
    /// bands into one BGRA buffer, woven when downsample==2). Ports handle_decode_lossless_compressed.</summary>
    private void EmitLosslessRsFrame(KcpFrame f)
    {
        var dec = _losslessRs;
        if (dec == null) return;

        int width = DownsampleWidth(f.Downsample);
        int displayWidth = DownsampleDisplayWidth(f.Downsample);
        int height = DownsampleHeight(f.Downsample, f.IsTop);
        int height0 = f.IsTop ? 400 : 320;
        int heightF = height0 / height;    // downsample factor (1 = full, 2 = half-height)
        bool weave = f.Downsample == 2;
        int screen = f.IsTop ? 0 : 1;

        int curIdx = f.EvenOdd != 0 ? 0 : 1;
        byte[] target;
        if (weave)
        {
            if (_deltaScratch[screen, 0] == null)
            {
                _deltaScratch[screen, 0] = new byte[240 * 400 * 4];
                _deltaScratch[screen, 1] = new byte[240 * 400 * 4];
            }
            target = _deltaScratch[screen, curIdx]!;
        }
        else
        {
            target = new byte[width * height * 4];
        }

        int heightPerBlkRow = width * 4 * LosslessBlockSize / heightF;
        for (int t = 0; t < f.CoreCount; t++)
        {
            int rowsInBlks = t == f.CoreCount - 1 ? f.VLastAdjusted : f.VAdjusted;
            if (f.CoreCount == 1) rowsInBlks = height0 / LosslessBlockSize;
            int outBase = t * f.VAdjusted * heightPerBlkRow;
            int partHeight = t == f.CoreCount - 1
                ? height - f.VAdjusted * (f.CoreCount - 1) * LosslessBlockSize / heightF
                : rowsInBlks * LosslessBlockSize / heightF;

            var (buf, size) = ConcatCore(f.Cores[t], f.TermSizes[t]);
            if (!dec.DecodeCore(target, outBase, buf, 0, size, f.ChromaSs, f.Quality, width, partHeight))
                return;   // decode error — drop the frame
        }

        byte[] outBuf;
        if (weave)
        {
            outBuf = new byte[displayWidth * height * 4];
            WeaveColumns(outBuf, _deltaScratch[screen, curIdx]!, _deltaScratch[screen, curIdx ^ 1]!,
                         f.EvenOdd != 0, displayWidth, height, width);
        }
        else
        {
            outBuf = target;
        }

        var sf = new StreamFrame(f.IsTop ? Screen.Top : Screen.Bottom, outBuf, FrameKind.RawBgra,
                                 0, f.EvenOdd, displayWidth, height);
        if (!_firstFrameSeen) { _firstFrameSeen = true; FirstFrame?.Invoke(); }
        FrameReady?.Invoke(sf);
    }

    /// <summary>Decodes a "JPEG (Reliable Stream, Delta)" frame with the stateful <see cref="NtrJpegDelta"/>
    /// decoder (per-core bands into one BGRA buffer) and emits it pre-decoded. Ports handle_decode_delta_prog.</summary>
    private void EmitDeltaFrame(KcpFrame f)
    {
        var delta = _delta;
        if (delta == null) return;

        int maxH = f.ChromaSs == 2 ? 1 : 2;
        int maxV = f.ChromaSs == 0 ? 2 : 1;
        int decodeWidth = DownsampleWidth(f.Downsample);
        int displayWidth = DownsampleDisplayWidth(f.Downsample);
        int height = DownsampleHeight(f.Downsample, f.IsTop);
        bool weave = f.Downsample == 2;    // column-interlaced with the previous frame
        int screen = f.IsTop ? 0 : 1;

        // Decode target: for the woven mode, into this frame's persistent half-width buffer; else a fresh buffer.
        int curIdx = f.EvenOdd != 0 ? 0 : 1;
        byte[] target;
        if (weave)
        {
            if (_deltaScratch[screen, 0] == null)
            {
                _deltaScratch[screen, 0] = new byte[240 * 400 * 4];
                _deltaScratch[screen, 1] = new byte[240 * 400 * 4];
            }
            target = _deltaScratch[screen, curIdx]!;
        }
        else
        {
            target = new byte[decodeWidth * height * 4];
        }

        int heightPerMcuRow = decodeWidth * 4 * 8 * maxV;   // GL_CHANNELS_N * DCTSIZE * max_v_samp_fact
        for (int t = 0; t < f.CoreCount; t++)
        {
            int rowsInMcus = t == f.CoreCount - 1 ? f.VLastAdjusted : f.VAdjusted;
            if (f.CoreCount == 1) rowsInMcus = (height + 8 * maxV - 1) / (8 * maxV);
            int outBase = t * f.VAdjusted * heightPerMcuRow;
            int mcuRow = t * f.VAdjusted;
            int partHeight = t == f.CoreCount - 1
                ? height - f.VAdjusted * (f.CoreCount - 1) * 8 * maxV
                : rowsInMcus * 8 * maxV;

            var (buf, size) = ConcatCore(f.Cores[t], f.TermSizes[t]);
            if (!delta.DecodeCore(target, outBase, buf, 0, size, rowsInMcus, maxH, maxV,
                                  f.Quality, f.IsTop, mcuRow, decodeWidth, partHeight, f.EvenOdd))
                return;   // decode error — drop the frame, keep prev state
        }

        byte[] outBuf;
        if (weave)
        {
            outBuf = new byte[displayWidth * height * 4];
            WeaveColumns(outBuf, _deltaScratch[screen, curIdx]!, _deltaScratch[screen, curIdx ^ 1]!,
                         f.EvenOdd != 0, displayWidth, height, decodeWidth);
        }
        else
        {
            outBuf = target;
        }

        var sf = new StreamFrame(f.IsTop ? Screen.Top : Screen.Bottom, outBuf, FrameKind.RawBgra,
                                 0, f.EvenOdd, displayWidth, height);
        if (!_firstFrameSeen) { _firstFrameSeen = true; FirstFrame?.Invoke(); }
        FrameReady?.Invoke(sf);
    }

    /// <summary>Column-interleaves the current half-width frame with the previous one into a full-width
    /// frame (port of screen_process): even output columns from one field, odd from the other.</summary>
    private static void WeaveColumns(byte[] outBuf, byte[] cur, byte[] prev, bool evenOdd,
                                     int width, int height, int halfWidth)
    {
        var a = cur; var b = prev;
        if (evenOdd) { a = prev; b = cur; }   // swap so the fresh field lands on the right columns
        int halfStride = halfWidth * 4;
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * halfStride;
            int dstRow = y * stride;
            for (int x = 0; x < width; x++)
            {
                var src = (x & 1) == 0 ? a : b;
                int s = srcRow + (x >> 1) * 4;
                int d = dstRow + x * 4;
                outBuf[d] = src[s];
                outBuf[d + 1] = src[s + 1];
                outBuf[d + 2] = src[s + 2];
                outBuf[d + 3] = src[s + 3];
            }
        }
    }

    private static (byte[] buf, int size) ConcatCore(List<byte[]> packets, int termSize)
    {
        int n = packets.Count;
        if (n == 0) return (Array.Empty<byte>(), 0);
        int size = (n - 1) * KcpPacketSize + termSize;
        var buf = new byte[size + 16];   // small tail slack; the bit reader is bounded by `size`
        int w = 0;
        for (int i = 0; i < n; i++)
        {
            int len = i == n - 1 ? termSize : KcpPacketSize;
            Array.Copy(packets[i], 0, buf, w, Math.Min(len, packets[i].Length));
            w += len;
        }
        return (buf, size);
    }

    /// <summary>Decodes a "Lossless (Reliable Stream, Delta)" frame — the lossless codec with temporal
    /// prediction against the previous frame. Ports handle_decode_lossless_compressed's delta branch.</summary>
    private void EmitLosslessRsDeltaFrame(KcpFrame f)
    {
        var dec = _losslessRs;
        if (dec == null) return;

        int width = DownsampleWidth(f.Downsample);
        int displayWidth = DownsampleDisplayWidth(f.Downsample);
        int height = DownsampleHeight(f.Downsample, f.IsTop);
        int height0 = f.IsTop ? 400 : 320;
        int heightF = height0 / height;
        bool weave = f.Downsample == 2;
        int screen = f.IsTop ? 0 : 1;

        int curIdx = f.EvenOdd != 0 ? 0 : 1;
        byte[] target;
        if (weave)
        {
            if (_deltaScratch[screen, 0] == null)
            {
                _deltaScratch[screen, 0] = new byte[240 * 400 * 4];
                _deltaScratch[screen, 1] = new byte[240 * 400 * 4];
            }
            target = _deltaScratch[screen, curIdx]!;
        }
        else
        {
            target = new byte[width * height * 4];
        }

        int heightPerBlkRow = width * 4 * LosslessBlockSize / heightF;
        for (int t = 0; t < f.CoreCount; t++)
        {
            int rowsInBlks = t == f.CoreCount - 1 ? f.VLastAdjusted : f.VAdjusted;
            if (f.CoreCount == 1) rowsInBlks = height0 / LosslessBlockSize;
            int outBase = t * f.VAdjusted * heightPerBlkRow;
            int offset = f.VAdjusted * LosslessBlockSize / heightF * t;   // core's starting row in the prev buffer
            int partHeight = t == f.CoreCount - 1
                ? height - f.VAdjusted * (f.CoreCount - 1) * LosslessBlockSize / heightF
                : rowsInBlks * LosslessBlockSize / heightF;

            var (buf, size) = ConcatCore(f.Cores[t], f.TermSizes[t]);
            if (!dec.DecodeCoreDelta(target, outBase, buf, 0, size, offset, f.IsTop, f.ChromaSs, f.Quality,
                                     width, partHeight, f.EvenOdd))
                return;   // decode error — drop the frame, keep prev state
        }

        byte[] outBuf;
        if (weave)
        {
            outBuf = new byte[displayWidth * height * 4];
            WeaveColumns(outBuf, _deltaScratch[screen, curIdx]!, _deltaScratch[screen, curIdx ^ 1]!,
                         f.EvenOdd != 0, displayWidth, height, width);
        }
        else
        {
            outBuf = target;
        }

        var sf = new StreamFrame(f.IsTop ? Screen.Top : Screen.Bottom, outBuf, FrameKind.RawBgra,
                                 0, f.EvenOdd, displayWidth, height);
        if (!_firstFrameSeen) { _firstFrameSeen = true; FirstFrame?.Invoke(); }
        FrameReady?.Invoke(sf);
    }

    private static int DownsampleWidth(int ds) => ds is 2 or 3 ? 120 : 240;          // decode width
    private static int DownsampleDisplayWidth(int ds) => ds == 3 ? 120 : 240;        // display width (ds 2 woven back to 240)
    private static int DownsampleHeight(int ds, bool isTop)
    {
        int full = isTop ? 400 : 320;   // SCREEN_HEIGHT0 / SCREEN_HEIGHT1
        return ds == 3 ? full / 2 : full;
    }

    private void MaybeReply(Kcp kcp, ref long replyTime)
    {
        if (!kcp.SessionEstablished) return;
        long now = Environment.TickCount64;
        long interval = kcp.HasGap ? 12 : 125;   // NACK fast (~12.5ms), ack slow (125ms)
        if (now - replyTime < interval) return;
        try { kcp.Reply(); } catch { }
        replyTime = now;
    }

    private int KcpOutput(byte[] buf, int len)
    {
        var ep = _remoteEP;
        if (ep == null) return 0;
        try
        {
            lock (_sendLock) { _udp?.Send(buf, len, ep); }
            return len;
        }
        catch { return -1; }
    }

    private async Task WatchdogLoop(CancellationToken token)
    {
        for (int attempt = 1; attempt <= 3 && !token.IsCancellationRequested; attempt++)
        {
            Status?.Invoke($"Connecting… ({attempt}/3)");
            await SendInitSequence(token);

            // Wait up to ~5s for the first frame.
            for (int i = 0; i < 50 && !token.IsCancellationRequested; i++)
            {
                if (_firstFrameSeen) return;
                await Task.Delay(100, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            if (_firstFrameSeen) return;
        }

        if (!_firstFrameSeen && !token.IsCancellationRequested)
            Failed?.Invoke("No frames received from the 3DS. Check NTR/CFW and the IP address.");
    }

    /// <summary>
    /// The NTR "kick": connect+send the 84-byte packet, disconnect, wait ~3s, then
    /// connect+disconnect (sending nothing) to start streaming.
    /// </summary>
    private async Task SendInitSequence(CancellationToken token)
    {
        var packet = BuildInitPacket(_quality, _priorityFactor, _priorityTop, _qos,
                                     _kcpMode, _bandwidth, _losslessColor, _listenPort);
        try
        {
            using (var tcp = new TcpClient())
            {
                await ConnectWithTimeout(tcp, _ip, 8000, 2000, token);
                await tcp.GetStream().WriteAsync(packet, token);
                await tcp.GetStream().FlushAsync(token);
            }
            await Task.Delay(3000, token);
            if (token.IsCancellationRequested) return;
            using (var tcp2 = new TcpClient())
            {
                await ConnectWithTimeout(tcp2, _ip, 8000, 2000, token);
                // send nothing — just closing kicks the stream
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status?.Invoke($"Init error: {ex.Message}");
        }
    }

    private static async Task ConnectWithTimeout(TcpClient client, string ip, int port, int ms, CancellationToken token)
    {
        var connectTask = client.ConnectAsync(IPAddress.Parse(ip), port);
        var timeout = Task.Delay(ms, token);
        var done = await Task.WhenAny(connectTask, timeout);
        if (done != connectTask)
            throw new TimeoutException($"Timed out connecting to {ip}:{port}");
        await connectTask; // surface connect exceptions
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        if (_udp == null) return;
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await _udp.ReceiveAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            var frame = ProcessDatagram(res.Buffer);
            if (frame != null)
            {
                if (!_firstFrameSeen)
                {
                    _firstFrameSeen = true;
                    FirstFrame?.Invoke();
                }
                FrameReady?.Invoke(frame);
            }
        }
    }

    private StreamFrame? ProcessDatagram(byte[] dg)
    {
        if (dg.Length < 4) return null;
        byte frameId = dg[0];
        byte b1 = dg[1];
        byte fmt = dg[2];                       // hdr[2]: 2 | lossless(bit0) | downsample(bits2-3)
        bool isLast = (b1 & 0x10) != 0;        // bit4 = end-of-frame (native + compat)
        bool isTop = (b1 & 0x01) == 1;         // bit0: 1 = top, 0 = bottom
        int packetNo = dg[3];

        // NTR-HR "Uncompressed (UDP)" marks frames lossless in hdr[2] bit0; those are NOT JPEG.
        bool lossless = (fmt & 0x01) == 1;
        int downsample = (fmt & 0x0C) >> 2;

        var ras = isTop ? _top : _bottom;
        return ras.Feed(frameId, packetNo, isLast, dg, 4, lossless, downsample);
    }

    /// <summary>Live quality change: NTR requires re-sending the init packet.</summary>
    public void SetQuality(int quality)
    {
        _quality = Math.Clamp(quality, 10, 100);
        if (_cts is { IsCancellationRequested: false })
            _ = SendInitSequence(_cts.Token);
    }

    public void SwapPriorityScreen()
    {
        _priorityTop = !_priorityTop;
        if (_cts is { IsCancellationRequested: false })
            _ = SendInitSequence(_cts.Token);
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Per-screen slice reassembler.
    /// <para><b>JPEG</b> (small frames): strict in-order — a gap drops the frame and waits for
    /// packet_no 0. <b>Uncompressed (UDP)</b> (large frames, ~50-100 packets, lossy over Wi-Fi):
    /// index-based — packets are placed by their number, so reorder is tolerated and a missing packet
    /// only leaves a local gap (decoded as a black patch) instead of dropping the whole frame.</para>
    /// </summary>
    private sealed class FrameReassembler
    {
        private readonly Screen _screen;

        // --- JPEG (strict, in-order) ---
        private readonly MemoryStream _buf = new();
        private int _expected;
        private bool _active;
        private int _frameId = -1;

        // --- Uncompressed (index-based) ---
        private const int MaxPackets = 160;
        private readonly byte[]?[] _slices = new byte[MaxPackets][];
        private int _lFrameId = -1;
        private int _lDownsample;
        private byte[]? _lastAssembled;   // last emitted frame, to patch missing packets (temporal fill)

        public FrameReassembler(Screen screen) => _screen = screen;

        public StreamFrame? Feed(int frameId, int packetNo, bool isLast, byte[] data, int offset,
                                 bool lossless = false, int downsample = 0)
        {
            if (lossless)
                return FeedLossless(frameId, packetNo, isLast, data, offset, downsample);

            if (packetNo == 0)
            {
                _buf.SetLength(0);
                _active = true;
                _expected = 0;
                _frameId = frameId;
            }
            else if (!_active || packetNo != _expected || frameId != _frameId)
            {
                _active = false;   // out of order / mid-stream join -> drop and resync
                return null;
            }

            _buf.Write(data, offset, data.Length - offset);
            _expected = packetNo + 1;

            if (isLast)
            {
                _active = false;
                var bytes = _buf.ToArray();
                _buf.SetLength(0);
                if (IsValidJpeg(bytes))
                    return new StreamFrame(_screen, bytes);
            }
            return null;
        }

        private StreamFrame? FeedLossless(int frameId, int packetNo, bool isLast, byte[] data, int offset, int downsample)
        {
            if (packetNo >= MaxPackets) return null;

            if (frameId != _lFrameId)
            {
                Array.Clear(_slices, 0, MaxPackets);   // new frame — drop any stragglers of the old one
                _lFrameId = frameId;
                _lDownsample = downsample;
            }

            int len = data.Length - offset;
            var slice = new byte[len];
            Array.Copy(data, offset, slice, 0, len);
            _slices[packetNo] = slice;

            if (!isLast) return null;

            // Last packet: assemble packets 0..packetNo. Reorder is tolerated (index-based). A missing
            // packet is patched from the SAME position in the previous frame (temporal fill) so the
            // frame always renders — a slightly stale band instead of a dropped frame or a green gap.
            const int chunk = 1444;   // RP_PACKET_DATA_SIZE (non-last packet payload size)
            int total = packetNo * chunk + len;
            var outBuf = new byte[total];
            var prev = _lastAssembled;
            for (int i = 0; i <= packetNo; i++)
            {
                int at = i * chunk;
                var s = _slices[i];
                if (s != null)
                    Array.Copy(s, 0, outBuf, at, Math.Min(s.Length, total - at));
                else if (prev != null && at < prev.Length)
                    Array.Copy(prev, at, outBuf, at, Math.Min(chunk, Math.Min(prev.Length - at, total - at)));
                // else: no history yet -> left as zeros (rare, only at stream start)
            }
            Array.Clear(_slices, 0, MaxPackets);
            _lFrameId = -1;
            _lastAssembled = outBuf;
            return new StreamFrame(_screen, outBuf, FrameKind.RawLossless, _lDownsample, frameId & 1);
        }

        private static bool IsValidJpeg(byte[] b) =>
            b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 &&
            b[^2] == 0xFF && b[^1] == 0xD9;
    }
}
