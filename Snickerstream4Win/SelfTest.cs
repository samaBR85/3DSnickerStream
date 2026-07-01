using System.Net;
using System.Net.Sockets;
using Snickerstream4Win.Net;

namespace Snickerstream4Win;

/// <summary>
/// Headless validation run with `3DSnickerStream.exe --selftest`. Exits 0 on success,
/// 1 on failure. Covers the 84-byte NTR init packet, the HzMod control packets and the
/// HzMod JPEG scanner, plus an end-to-end UDP reassembly of a synthetic JPEG.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok) failures++;
        }

        // ---- NTR init packet ----
        var p = NTRClient.BuildInitPacket(quality: 70, priorityFactor: 5, priorityTop: true, qos: 20);
        Check("init packet is 84 bytes", p.Length == 84);
        Check("magic 0x12345678", p[0] == 0x78 && p[1] == 0x56 && p[2] == 0x34 && p[3] == 0x12);
        Check("seq == 3000", BitConverter.ToUInt32(p, 0x04) == 3000);
        Check("type == 0", BitConverter.ToUInt32(p, 0x08) == 0);
        Check("cmd == 901", BitConverter.ToUInt32(p, 0x0C) == 901);
        Check("priority factor == 5", p[0x10] == 5);
        Check("priority screen top == 1", p[0x11] == 1);
        Check("quality == 70", p[0x14] == 70);
        Check("qos x2 == 40", p[0x1A] == 40);
        Check("bottom-priority byte == 0",
            NTRClient.BuildInitPacket(70, 5, false, 20)[0x11] == 0);

        // ---- HzMod control packets ----
        var cpu = HzModClient.CpuLimitPacket(128);
        Check("hz cpu packet header", cpu.Length == 9 && cpu[0] == 0x7E && cpu[4] == 0xFF && cpu[8] == 128);
        var q = HzModClient.QualityPacket(55);
        Check("hz quality packet header", q.Length == 9 && q[4] == 0x03 && q[8] == 55);
        var st = HzModClient.StartPacket();
        Check("hz start packet", st.Length == 9 && st[8] == 0x01);

        // ---- HzMod JPEG stream scanner ----
        {
            var jpeg = MakeJpeg(2000);
            var scanner = new JpegStreamScanner();
            var outFrames = new List<byte[]>();
            // garbage + jpeg + garbage + second jpeg, fed in two arbitrary chunks
            var stream = new List<byte> { 0x11, 0x22 };
            stream.AddRange(jpeg);
            stream.Add(0x33);
            stream.AddRange(MakeJpeg(500));
            var all = stream.ToArray();
            scanner.Append(all, all.Length, f => outFrames.Add(f));
            Check("hz scanner found 2 jpegs", outFrames.Count == 2);
            Check("hz scanner first jpeg intact",
                outFrames.Count > 0 && outFrames[0].Length == jpeg.Length && IsJpeg(outFrames[0]));
        }

        // ---- End-to-end UDP reassembly ----
        Check("ntr udp reassembly", TestUdpReassembly());

        Console.WriteLine(failures == 0 ? "\nAll self-tests passed." : $"\n{failures} self-test(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    private static bool TestUdpReassembly()
    {
        const int port = 8123;
        var client = new NTRClient("127.0.0.1", port, 70, 5, true, 20);
        byte[]? gotTop = null;
        var done = new ManualResetEventSlim(false);
        client.FrameReady += f =>
        {
            if (f.Screen == Screen.Top) { gotTop = f.Jpeg; done.Set(); }
        };

        // Start only the UDP listener — we feed init ourselves, so suppress the watchdog
        // by not relying on it. Start() also kicks the watchdog; harmless against localhost.
        try
        {
            // Manually create the listener path: reuse Start() but it opens the UDP port.
            client.Start();

            var jpeg = MakeJpeg(4000);
            SendSyntheticFrame("127.0.0.1", port, screenTop: true, frameId: 7, jpeg: jpeg, sliceSize: 1400);

            bool ok = done.Wait(3000);
            ok = ok && gotTop != null && gotTop.Length == jpeg.Length && IsJpeg(gotTop!);
            return ok;
        }
        finally
        {
            client.Stop();
        }
    }

    private static void SendSyntheticFrame(string ip, int port, bool screenTop, byte frameId, byte[] jpeg, int sliceSize)
    {
        using var udp = new UdpClient();
        var ep = new IPEndPoint(IPAddress.Parse(ip), port);
        int packets = (int)Math.Ceiling(jpeg.Length / (double)sliceSize);
        for (int i = 0; i < packets; i++)
        {
            int off = i * sliceSize;
            int len = Math.Min(sliceSize, jpeg.Length - off);
            bool last = i == packets - 1;
            var dg = new byte[4 + len];
            dg[0] = frameId;
            dg[1] = (byte)(((last ? 1 : 0) << 4) | (screenTop ? 1 : 0));
            dg[2] = 2;                 // format
            dg[3] = (byte)i;           // packet number
            Array.Copy(jpeg, off, dg, 4, len);
            udp.Send(dg, dg.Length, ep);
            Thread.Sleep(2);           // keep ordering on the loopback
        }
    }

    private static byte[] MakeJpeg(int payload)
    {
        var b = new byte[payload + 4];
        b[0] = 0xFF; b[1] = 0xD8;
        for (int i = 2; i < payload + 2; i++) b[i] = (byte)(i % 251);
        // avoid accidental FF D9 inside the body
        for (int i = 2; i < payload + 1; i++)
            if (b[i] == 0xFF && b[i + 1] == 0xD9) b[i + 1] = 0x00;
        b[^2] = 0xFF; b[^1] = 0xD9;
        return b;
    }

    private static bool IsJpeg(byte[] b) =>
        b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[^2] == 0xFF && b[^1] == 0xD9;
}
