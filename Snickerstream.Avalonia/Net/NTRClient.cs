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

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _udpTask;
    private Task? _watchdogTask;
    private volatile bool _firstFrameSeen;
    private volatile bool _stopped;

    private readonly FrameReassembler _top = new(Screen.Top);
    private readonly FrameReassembler _bottom = new(Screen.Bottom);

    public NTRClient(string ip, int listenPort, int quality, int priorityFactor, bool priorityTop, int qos)
    {
        _ip = ip;
        _listenPort = listenPort;
        _quality = Math.Clamp(quality, 10, 100);
        _priorityFactor = Math.Clamp(priorityFactor, 0, 10);
        _priorityTop = priorityTop;
        _qos = Math.Clamp(qos, 2, 100);
    }

    /// <summary>
    /// Builds the 84-byte NTR remoteplay init packet (magic 0x12345678, seq 3000,
    /// cmd 901). All multi-byte fields are little-endian.
    /// </summary>
    public static byte[] BuildInitPacket(int quality, int priorityFactor, bool priorityTop, int qos)
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
        p[0x10] = (byte)Math.Clamp(priorityFactor, 0, 10);
        p[0x11] = (byte)(priorityTop ? 1 : 0);
        p[0x14] = (byte)Math.Clamp(quality, 10, 100);
        p[0x1A] = (byte)Math.Clamp(qos * 2, 0, 255);  // QoS x2
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

        _udpTask = Task.Run(() => ReceiveLoop(token), token);
        _watchdogTask = Task.Run(() => WatchdogLoop(token), token);
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
        var packet = BuildInitPacket(_quality, _priorityFactor, _priorityTop, _qos);
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
        bool isLast = (b1 >> 4) == 1;          // high nibble == 1 on last packet
        bool isTop = (b1 & 0x0F) == 1;         // low nibble: 1 = top, 0 = bottom
        int packetNo = dg[3];

        var ras = isTop ? _top : _bottom;
        return ras.Feed(frameId, packetNo, isLast, dg, 4);
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
    /// Per-screen JPEG slice reassembler. Slices for one frame arrive in order with a
    /// shared frame id; a non-zero start or an out-of-order packet drops the frame and
    /// waits for the next packet_no == 0.
    /// </summary>
    private sealed class FrameReassembler
    {
        private readonly Screen _screen;
        private readonly MemoryStream _buf = new();
        private int _expected;      // next packet number expected
        private bool _active;
        private int _frameId = -1;

        public FrameReassembler(Screen screen) => _screen = screen;

        public StreamFrame? Feed(int frameId, int packetNo, bool isLast, byte[] data, int offset)
        {
            if (packetNo == 0)
            {
                _buf.SetLength(0);
                _active = true;
                _expected = 0;
                _frameId = frameId;
            }
            else if (!_active || packetNo != _expected || frameId != _frameId)
            {
                // out of order / mid-stream join -> drop and resync
                _active = false;
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

        private static bool IsValidJpeg(byte[] b) =>
            b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 &&
            b[^2] == 0xFF && b[^1] == 0xD9;
    }
}
