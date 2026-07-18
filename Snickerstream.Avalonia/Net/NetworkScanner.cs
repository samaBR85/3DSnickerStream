using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SnickerstreamV2.Net;

/// <summary>
/// Scans EVERY local /24 subnet for a 3DS by probing TCP 8000 (NTR) or 6464 (HzMod).
/// Multi-homed hosts (e.g. Ethernet + Wi-Fi at once) sit on more than one subnet, and the 3DS may be on
/// a different one than the interface that happens to enumerate first — so we scan them all, not just one.
/// Uses IP-literal connections (no DNS), a short per-host timeout, bounded concurrency
/// and skips loopback / link-local addresses.
/// </summary>
public static class NetworkScanner
{
    private const int PerHostTimeoutMs = 600;
    private const int MaxConcurrency = 48;

    public static async Task<List<string>> ScanAsync(bool hzMod, CancellationToken token, Action<string>? onFound = null)
    {
        int port = hzMod ? 6464 : 8000;
        var found = new List<string>();
        var locals = GetLocalIPv4s();
        if (locals.Count == 0) return found;

        // Every distinct /24 base we're on (e.g. 192.168.0.x from Ethernet AND 192.168.1.x from Wi-Fi),
        // plus the set of our own addresses to skip while probing.
        var selfIps = new HashSet<string>();
        var prefixes = new List<string>();
        foreach (var a in locals)
        {
            selfIps.Add(a.ToString());
            var b = a.GetAddressBytes();
            string prefix = $"{b[0]}.{b[1]}.{b[2]}.";
            if (!prefixes.Contains(prefix)) prefixes.Add(prefix);
        }

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>();
        var foundLock = new object();

        foreach (var prefix in prefixes)
        {
            for (int host = 1; host <= 254; host++)
            {
                if (token.IsCancellationRequested) break;
                string ip = prefix + host;
                if (selfIps.Contains(ip)) continue;

                await gate.WaitAsync(token);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (await ProbeAsync(ip, port, token))
                        {
                            lock (foundLock)
                            {
                                if (!found.Contains(ip)) found.Add(ip);
                            }
                            onFound?.Invoke(ip);
                        }
                    }
                    catch { }
                    finally { gate.Release(); }
                }, token));
            }
        }

        try { await Task.WhenAll(tasks); } catch { }
        found.Sort(CompareIp);
        return found;
    }

    private static async Task<bool> ProbeAsync(string ip, int port, CancellationToken token)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(IPAddress.Parse(ip), port);
            var done = await Task.WhenAny(connect, Task.Delay(PerHostTimeoutMs, token));
            if (done != connect) return false;
            await connect; // throws if refused
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>Every up, non-loopback interface's private IPv4 address(es) — so a multi-homed host scans
    /// all of its subnets. Falls back to any non-private routable IPv4 only if no private one exists.</summary>
    private static List<IPAddress> GetLocalIPv4s()
    {
        var privates = new List<IPAddress>();
        var others = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 127) continue;                 // loopback
                if (b[0] == 169 && b[1] == 254) continue;  // link-local
                bool isPrivate = b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168);
                (isPrivate ? privates : others).Add(ua.Address);
            }
        }
        return privates.Count > 0 ? privates : others;   // prefer private subnets; else scan whatever we have
    }

    private static int CompareIp(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < 4 && i < pa.Length && i < pb.Length; i++)
        {
            int x = int.TryParse(pa[i], out var v) ? v : 0;
            int y = int.TryParse(pb[i], out var w) ? w : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
