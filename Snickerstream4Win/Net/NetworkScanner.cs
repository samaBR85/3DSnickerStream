using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Snickerstream4Win.Net;

/// <summary>
/// Scans the local /24 subnet for a 3DS by probing TCP 8000 (NTR) or 6464 (HzMod).
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
        var local = GetLocalIPv4();
        if (local == null) return found;

        // /24 base, e.g. 192.168.0.x
        var bytes = local.GetAddressBytes();
        string prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>();
        var foundLock = new object();

        for (int host = 1; host <= 254; host++)
        {
            if (token.IsCancellationRequested) break;
            string ip = prefix + host;
            if (ip == local.ToString()) continue;

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

        try { await Task.WhenAll(tasks); } catch { }
        found.Sort((a, b) => CompareIp(a, b));
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

    private static IPAddress? GetLocalIPv4()
    {
        IPAddress? candidate = null;
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
                if (isPrivate) return ua.Address;          // prefer private
                candidate ??= ua.Address;
            }
        }
        return candidate;
    }

    private static int CompareIp(string a, string b)
    {
        int LastOctet(string s) => int.TryParse(s.Split('.')[^1], out var v) ? v : 0;
        return LastOctet(a).CompareTo(LastOctet(b));
    }
}
