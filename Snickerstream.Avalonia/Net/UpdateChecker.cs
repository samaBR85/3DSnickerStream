using System.Net.Http;
using System.Text.Json;
using SnickerstreamV2.Models;

namespace SnickerstreamV2.Net;

public sealed record UpdateInfo(bool Available, string LatestVersion, string Url);

/// <summary>
/// Checks the project's latest GitHub release and compares it semantically to the running
/// version. The GitHub API rejects requests without a User-Agent header (HTTP 403).
/// </summary>
public static class UpdateChecker
{
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("3DSnickerStream");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{AppInfo.Repo}/releases/latest";
            var json = await client.GetStringAsync(url, token);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string html = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? AppInfo.ReleasesUrl) : AppInfo.ReleasesUrl;

            string latest = tag.TrimStart('v', 'V');
            if (latest.Length == 0) return null;

            bool newer = CompareSemver(latest, AppInfo.Version) > 0;
            return new UpdateInfo(newer, latest, html);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Numeric dot-separated comparison. Returns &gt;0 if a is newer than b.</summary>
    public static int CompareSemver(string a, string b)
    {
        int[] Parse(string s) => s.Split('.', '-')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();

        var pa = Parse(a);
        var pb = Parse(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
