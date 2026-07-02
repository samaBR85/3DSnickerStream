using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TesseractOCR;
using TesseractOCR.Enums;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Snickerstream4Win.Services;

/// <summary>
/// Text recognition over a WPF bitmap.
/// <para>Default (general) mode uses the built-in <c>Windows.Media.Ocr</c> engine — offline, no bundle.</para>
/// <para>Hex mode uses <b>Tesseract</b> restricted to a hex/seed-friendly whitelist (no "O"/"Q", so
/// zero-shaped glyphs resolve to <c>0</c>) plus a grayscale + quiet-zone-padded upscale and a
/// format-aware cleanup that rebuilds the <c>[n]</c> seed markers — reliable on dense RNG HUD text.</para>
/// </summary>
public static class OcrService
{
    // Labels + hex, minus the worst zero-lookalikes ("O"/"o"/"Q"/"q") so seeds resolve to digits.
    private const string HexWhitelist =
        "ABCDEFGHIJKLMNPRSTUVWXYZabcdefghijklmnprstuvwxyz0123456789[]():.,-/ ";

    /// <summary>
    /// Recognizes text in <paramref name="src"/>. Returns recognized lines joined by newlines
    /// (empty string if nothing found), or <c>null</c> if no OCR engine is available at all.
    /// </summary>
    public static async Task<string?> RecognizeAsync(BitmapSource src, bool hexMode)
    {
        if (hexMode)
        {
            // Grayscale (antialiased) + a quiet-zone-padded upscale reads seeds and the [n] markers best
            // (validated against real captures; binarizing corrupts digits). Preprocess on the UI thread,
            // OCR on a worker thread.
            byte[]? png = TryEncode(Preprocess(src, binarize: false, invert: true, pad: 28));
            if (png != null)
            {
                var raw = await Task.Run(() => RunTesseract(png));
                if (raw != null) return CleanupHex(raw);   // else (no engine/tessdata) fall through
            }
        }
        return await RecognizeWindowsAsync(src);
    }

    // ===================== Tesseract (hex mode) =====================

    /// <summary>Runs Tesseract on a prepared PNG. Returns raw text, or null if the engine/tessdata is missing.</summary>
    private static string? RunTesseract(byte[] png)
    {
        string dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dataPath, "eng.traineddata"))) return null;
        try
        {
            using var engine = new Engine(dataPath, Language.English, EngineMode.Default);
            engine.SetVariable("tessedit_char_whitelist", HexWhitelist);
            using var img = TesseractOCR.Pix.Image.LoadFromMemory(png);
            using var page = engine.Process(img, PageSegMode.SingleBlock);
            return page.Text ?? "";
        }
        catch { return null; }
    }

    private static string CleanupHex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(FixLine)
            // drop background-noise lines: keep only lines with a value, marker, or label.
            .Where(l => l.Length > 0 && (l.Any(char.IsDigit) || l.Contains('[') || l.Contains(':')));
        return string.Join("\n", lines).Trim();
    }

    private static string FixLine(string line)
    {
        line = line.Replace("FR0ZEN", "FROZEN");        // 'O' isn't whitelisted, so the header reads FR0ZEN
        if (line.Contains('[')) return RebuildBrackets(line);
        // Strip leading background noise before the label (e.g. "Bee SS cSeed:" -> "cSeed:").
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

    /// <summary>Snaps a hex value token (hex + a few 0/1 look-alikes only) to clean uppercase hex; any
    /// other letter means it's a label/word and the token is returned untouched.</summary>
    private static string FixHexToken(string tok)
    {
        if (tok is "O" or "o" or "Q" or "q") return "0";   // lone zero (e.g. "tAdv: 0")
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

    // ===================== Windows.Media.Ocr (general mode) =====================

    private static async Task<string?> RecognizeWindowsAsync(BitmapSource src)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine != null) break;
            }
        }
        if (engine == null) return null;

        string best = "";
        int bestScore = -1;
        foreach (var prepared in new[] { Preprocess(src, binarize: false, invert: false, pad: 16),
                                         Preprocess(src, binarize: true, invert: false, pad: 16) })
        {
            var softwareBitmap = await ToSoftwareBitmapAsync(prepared);
            string text;
            using (softwareBitmap)
            {
                var result = await engine.RecognizeAsync(softwareBitmap);
                text = string.Join("\n", result.Lines.Select(l => l.Text));
            }
            int score = text.Count(char.IsLetterOrDigit);
            if (score > bestScore) { bestScore = score; best = text; }
        }
        return best;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(BitmapSource bmp)
    {
        byte[] bytes = TryEncode(bmp)!;
        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        ras.Dispose();
        return sb;
    }

    // ===================== Shared preprocessing =====================

    /// <summary>
    /// Grayscale → contrast stretch → optional Otsu binarize → optional invert (dark-on-light for Tesseract)
    /// → big smooth upscale (~1100px tall) onto a background-filled canvas with a <paramref name="pad"/>-px
    /// quiet zone (so edge glyphs like a leading '[' are read).
    /// </summary>
    private static BitmapSource Preprocess(BitmapSource src, bool binarize, bool invert, int pad)
    {
        var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
        int w0 = gray.PixelWidth, h0 = gray.PixelHeight;
        if (w0 <= 0 || h0 <= 0) return src;

        int stride0 = w0;                       // Gray8 = 1 byte/pixel
        var g = new byte[h0 * stride0];
        gray.CopyPixels(g, stride0, 0);

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

        var prepared = BitmapSource.Create(w0, h0, 96, 96, PixelFormats.Gray8, null, g, stride0);

        double scale = Math.Clamp(1100.0 / h0, 3.0, 8.0);
        int w = (int)Math.Round(w0 * scale), h = (int)Math.Round(h0 * scale);
        byte fill = (byte)(invert ? 255 : 0);

        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(fill, fill, fill)), null,
                new System.Windows.Rect(0, 0, w + 2 * pad, h + 2 * pad));
            dc.DrawImage(prepared, new System.Windows.Rect(pad, pad, w, h));
        }
        var rtb = new RenderTargetBitmap(w + 2 * pad, h + 2 * pad, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
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

    private static byte[]? TryEncode(BitmapSource bmp)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
