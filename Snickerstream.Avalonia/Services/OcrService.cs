using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TesseractOCR;
using TesseractOCR.Enums;
using Rect = Avalonia.Rect;   // disambiguate from TesseractOCR.Rect

namespace SnickerstreamV2.Services;

/// <summary>
/// Text recognition over an Avalonia bitmap, using <b>Tesseract</b> for both modes (cross-platform).
/// <para>General mode runs the full charset. Hex mode restricts recognition to a hex/seed-friendly
/// whitelist (no "O"/"Q", so zero-shaped glyphs resolve to <c>0</c>) with a format-aware cleanup that
/// rebuilds the <c>[n]</c> seed markers — reliable on dense RNG HUD text.</para>
/// <para>Both modes share a grayscale → contrast-stretch → quiet-zone-padded upscale that reads dense
/// HUD text best (binarizing corrupts digits, so it's off by default).</para>
/// </summary>
public static class OcrService
{
    // Labels + hex, minus the worst zero-lookalikes ("O"/"o"/"Q"/"q") so seeds resolve to digits.
    private const string HexWhitelist =
        "ABCDEFGHIJKLMNPRSTUVWXYZabcdefghijklmnprstuvwxyz0123456789[]():.,-/ ";

    /// <summary>
    /// Recognizes text in <paramref name="src"/>. Returns recognized lines joined by newlines (empty
    /// string if nothing found), or <c>null</c> if no OCR engine/tessdata is available on this platform.
    /// </summary>
    public static async Task<string?> RecognizeAsync(Bitmap src, bool hexMode)
    {
        if (hexMode)
        {
            byte[]? png = Preprocess(src, binarize: false, invert: true, pad: 28);
            if (png == null) return null;
            var raw = await Task.Run(() => RunTesseract(png, HexWhitelist, PageSegMode.SingleBlock));
            return raw == null ? null : CleanupHex(raw);
        }

        // General: try dark-on-light and light-on-dark, keep whichever yields more letters/digits.
        string best = "";
        int bestScore = -1;
        bool any = false;
        foreach (var invert in new[] { true, false })
        {
            byte[]? png = Preprocess(src, binarize: false, invert: invert, pad: 16);
            if (png == null) continue;
            var raw = await Task.Run(() => RunTesseract(png, null, PageSegMode.Auto));
            if (raw == null) return null;   // engine/tessdata missing → feature unavailable
            any = true;
            int score = raw.Count(char.IsLetterOrDigit);
            if (score > bestScore) { bestScore = score; best = raw; }
        }
        return any ? best.Trim() : null;
    }

    // ===================== Tesseract =====================

    /// <summary>Runs Tesseract on a prepared PNG. Returns raw text, or null if the engine/tessdata/natives are missing.</summary>
    private static string? RunTesseract(byte[] png, string? whitelist, PageSegMode psm)
    {
        string dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dataPath, "eng.traineddata"))) return null;
        try
        {
            using var engine = new Engine(dataPath, Language.English, EngineMode.Default);
            if (whitelist != null) engine.SetVariable("tessedit_char_whitelist", whitelist);
            using var img = TesseractOCR.Pix.Image.LoadFromMemory(png);
            using var page = engine.Process(img, psm);
            return page.Text ?? "";
        }
        catch { return null; }   // native libs absent (non-Windows without natives) or decode failure
    }

    // ===================== Hex-mode cleanup (pure C#, ported verbatim) =====================

    private static string CleanupHex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(FixLine)
            .Where(l => l.Length > 0 && (l.Any(char.IsDigit) || l.Contains('[') || l.Contains(':')));
        return string.Join("\n", lines).Trim();
    }

    private static string FixLine(string line)
    {
        line = line.Replace("FR0ZEN", "FROZEN");        // 'O' isn't whitelisted, so the header reads FR0ZEN
        if (line.Contains('[')) return RebuildBrackets(line);
        var anchor = Regex.Match(line, @"\S+:");
        if (anchor.Success) line = line[anchor.Index..];
        int colon = line.IndexOf(':');
        if (colon >= 0)
            return line[..(colon + 1)] + Regex.Replace(line[(colon + 1)..], "[0-9A-Za-z]+", m => FixHexToken(m.Value));
        return line;
    }

    /// <summary>Rebuilds the seed marker line from the fixed format: two <c>[idx]</c> groups, each an
    /// 8-hex value. Tolerates a mangled <c>]</c> by taking the last 8 hex chars of each run.</summary>
    private static string RebuildBrackets(string line)
    {
        string s = MapHex(line.ToUpperInvariant()).Replace(" ", "");
        var groups = new List<string>();
        foreach (Match m in Regex.Matches(s, @"\[?([0-3])[^0-9A-F]*([0-9A-F]{8,9})"))
            groups.Add($"[{m.Groups[1].Value}]{m.Groups[2].Value[^8..]}");
        return groups.Count > 0 ? string.Join(" ", groups) : line;
    }

    /// <summary>Snaps a hex value token to clean uppercase hex; any non-hex letter means it's a word and is kept.</summary>
    private static string FixHexToken(string tok)
    {
        if (tok is "O" or "o" or "Q" or "q") return "0";
        var u = tok.ToUpperInvariant().ToCharArray();
        int hex = 0;
        foreach (var c in u)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')) hex++;
            else if (c is 'O' or 'Q' or 'I' or 'L' or 'S' or 'Z' or 'G') { /* mapable look-alike */ }
            else return tok;
        }
        return hex == 0 ? tok : MapHex(new string(u));
    }

    private static string MapHex(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++)
            a[i] = a[i] switch { 'O' or 'Q' => '0', 'I' or 'L' => '1', 'S' => '5', 'Z' => '2', 'G' => '6', _ => a[i] };
        return new string(a);
    }

    // ===================== Preprocessing (grayscale → contrast → padded upscale → PNG) =====================

    private static byte[]? Preprocess(Bitmap src, bool binarize, bool invert, int pad)
    {
        try
        {
            int w0 = src.PixelSize.Width, h0 = src.PixelSize.Height;
            if (w0 <= 0 || h0 <= 0) return null;

            int stride0 = w0 * 4;
            var px = new byte[checked(h0 * stride0)];
            var gch = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { src.CopyPixels(new PixelRect(0, 0, w0, h0), gch.AddrOfPinnedObject(), px.Length, stride0); }
            finally { gch.Free(); }
            var fmt = src.Format ?? PixelFormat.Bgra8888;
            bool isRgba = fmt == PixelFormat.Rgba8888;
            int ri = isRgba ? 0 : 2, bi = isRgba ? 2 : 0;

            // grayscale
            var g = new byte[w0 * h0];
            for (int i = 0, p = 0; i < px.Length; i += 4, p++)
                g[p] = (byte)(0.299 * px[i + ri] + 0.587 * px[i + 1] + 0.114 * px[i + bi]);

            // contrast stretch
            byte min = 255, max = 0;
            foreach (var v in g) { if (v < min) min = v; if (v > max) max = v; }
            double range = Math.Max(1, max - min);
            for (int i = 0; i < g.Length; i++)
                g[i] = (byte)Math.Clamp((int)((g[i] - min) * 255.0 / range), 0, 255);

            if (binarize)
            {
                int thr = OtsuThreshold(g);
                byte fg = invert ? (byte)0 : (byte)255, bg = invert ? (byte)255 : (byte)0;
                for (int i = 0; i < g.Length; i++) g[i] = g[i] > thr ? fg : bg;
            }
            else if (invert)
                for (int i = 0; i < g.Length; i++) g[i] = (byte)(255 - g[i]);

            using var grayBmp = GrayToBitmap(g, w0, h0);

            double scale = Math.Clamp(1100.0 / h0, 3.0, 8.0);
            int w = (int)Math.Round(w0 * scale), h = (int)Math.Round(h0 * scale);
            byte fill = (byte)(invert ? 255 : 0);
            int fw = w + 2 * pad, fh = h + 2 * pad;

            using var rtb = new RenderTargetBitmap(new PixelSize(fw, fh), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(fill, fill, fill)), null, new Rect(0, 0, fw, fh));
                dc.DrawImage(grayBmp, new Rect(pad, pad, w, h));
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Grayscale byte plane → an opaque BGRA bitmap (R=G=B=gray) Tesseract can read.</summary>
    private static WriteableBitmap GrayToBitmap(byte[] g, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = wb.Lock();
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = g[y * w + x];
                int o = x * 4;
                row[o] = v; row[o + 1] = v; row[o + 2] = v; row[o + 3] = 255;
            }
            Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), w * 4);
        }
        return wb;
    }

    private static int OtsuThreshold(byte[] gray)
    {
        var hist = new int[256];
        foreach (var v in gray) hist[v]++;
        int total = gray.Length;
        double sum = 0;
        for (int t = 0; t < 256; t++) sum += t * (double)hist[t];
        double sumB = 0; int wB = 0; double maxVar = 0; int thr = 127;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar) { maxVar = between; thr = t; }
        }
        return thr;
    }
}
