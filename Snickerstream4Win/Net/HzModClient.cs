using System.Net;
using System.Net.Sockets;

namespace Snickerstream4Win.Net;

/// <summary>
/// HzMod (beta) client. Connects over TCP/6464, sends CPU-limit, quality and start
/// packets, then receives JPEG frames over the same connection. The frame header is
/// unreliable, so we scan the byte stream for complete JPEGs (FF D8 … FF D9).
/// HzMod streams the top screen only.
/// </summary>
public sealed class HzModClient : IStreamClient
{
    public event Action<StreamFrame>? FrameReady;
    public event Action<string>? Status;
    public event Action<string>? Failed;
    public event Action? FirstFrame;

    private readonly string _ip;
    private int _quality;
    private readonly int _cpuLimit;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private TcpClient? _tcp;
    private volatile bool _firstFrameSeen;
    private volatile bool _stopped;
    private readonly object _writeLock = new();

    public HzModClient(string ip, int quality, int cpuLimit)
    {
        _ip = ip;
        _quality = Math.Clamp(quality, 1, 100);
        _cpuLimit = Math.Clamp(cpuLimit, 0, 255);
    }

    public static byte[] CpuLimitPacket(int cpuLimit) =>
        new byte[] { 0x7E, 0x05, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, (byte)Math.Clamp(cpuLimit, 0, 255) };

    public static byte[] QualityPacket(int quality) =>
        new byte[] { 0x7E, 0x05, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, (byte)Math.Clamp(quality, 1, 100) };

    public static byte[] StartPacket() =>
        new byte[] { 0x7E, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 };

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunWithRetries(_cts.Token), _cts.Token);
    }

    private async Task RunWithRetries(CancellationToken token)
    {
        for (int attempt = 1; attempt <= 3 && !token.IsCancellationRequested && !_firstFrameSeen; attempt++)
        {
            Status?.Invoke($"Connecting… ({attempt}/3)");
            try
            {
                await Session(token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Status?.Invoke($"HzMod error: {ex.Message}");
                await Task.Delay(800, token);
            }
            if (_firstFrameSeen) return;
        }

        if (!_firstFrameSeen && !token.IsCancellationRequested)
            Failed?.Invoke("No frames received via HzMod. Check the homebrew and the IP address.");
    }

    private async Task Session(CancellationToken token)
    {
        using var tcp = new TcpClient();
        _tcp = tcp;
        var connectTask = tcp.ConnectAsync(IPAddress.Parse(_ip), 6464);
        if (await Task.WhenAny(connectTask, Task.Delay(2500, token)) != connectTask)
            throw new TimeoutException("Timed out connecting to HzMod (6464).");
        await connectTask;

        var stream = tcp.GetStream();
        WritePacket(stream, CpuLimitPacket(_cpuLimit));
        WritePacket(stream, QualityPacket(_quality));
        WritePacket(stream, StartPacket());

        var scanner = new JpegStreamScanner();
        var rx = new byte[64 * 1024];

        while (!token.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(rx, token); }
            catch (OperationCanceledException) { break; }
            if (n <= 0) break; // connection closed -> let retry loop reconnect

            scanner.Append(rx, n, jpeg =>
            {
                if (!_firstFrameSeen)
                {
                    _firstFrameSeen = true;
                    FirstFrame?.Invoke();
                }
                FrameReady?.Invoke(new StreamFrame(Screen.Top, jpeg));
            });
        }
    }

    private void WritePacket(NetworkStream stream, byte[] data)
    {
        lock (_writeLock)
        {
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
    }

    /// <summary>Live quality change: re-send the quality packet on the open connection.</summary>
    public void SetQuality(int quality)
    {
        _quality = Math.Clamp(quality, 1, 100);
        try
        {
            if (_tcp is { Connected: true })
                WritePacket(_tcp.GetStream(), QualityPacket(_quality));
        }
        catch { }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Close(); } catch { }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Accumulates a TCP byte stream and emits each complete JPEG (start FF D8, end FF D9).
/// Tolerant of garbage between frames; bounded so a never-terminating frame can't grow
/// without limit.
/// </summary>
public sealed class JpegStreamScanner
{
    private readonly List<byte> _buf = new(128 * 1024);
    private const int MaxBuffer = 8 * 1024 * 1024;

    public void Append(byte[] data, int count, Action<byte[]> onJpeg)
    {
        for (int i = 0; i < count; i++) _buf.Add(data[i]);

        while (true)
        {
            int start = IndexOfMarker(0xD8, 0);
            if (start < 0)
            {
                // no SOI yet; keep only a trailing byte in case FF straddles reads
                if (_buf.Count > 1) _buf.RemoveRange(0, _buf.Count - 1);
                return;
            }
            if (start > 0) _buf.RemoveRange(0, start); // drop bytes before SOI

            int end = IndexOfMarker(0xD9, 2);
            if (end < 0)
            {
                if (_buf.Count > MaxBuffer) _buf.Clear(); // runaway guard
                return;
            }

            int len = end + 2; // include FF D9
            var jpeg = new byte[len];
            _buf.CopyTo(0, jpeg, 0, len);
            _buf.RemoveRange(0, len);
            onJpeg(jpeg);
        }
    }

    /// <summary>Finds an FF &lt;marker&gt; pair at or after startIndex.</summary>
    private int IndexOfMarker(byte marker, int startIndex)
    {
        for (int i = startIndex; i < _buf.Count - 1; i++)
            if (_buf[i] == 0xFF && _buf[i + 1] == marker)
                return i;
        return -1;
    }
}
