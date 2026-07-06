using System;
using System.Threading.Tasks;

namespace SnickerstreamV2.Imaging;

/// <summary>
/// The upscaling filter. Rendered on the GPU (a SkSL shader in <c>GpuScreen</c>) when a GPU context is
/// available; this pure-managed CPU implementation is the software-Skia fallback.
/// </summary>
public enum UpscaleFilter { None, Sharp, Xbr, SuperXbr, Fsr, Anime4K }

/// <summary>
/// CPU upscalers for the tiny 3DS frames (240×400 / 240×320). Reimplementations of the shader-based
/// filters NTR-Viewer runs on the GPU (libplacebo / librashader) — here in pure managed C# so the app
/// stays a single-file, cross-platform build with no native deps.
///
/// <para>All filters take a BGRA8888 row-major buffer and return a fresh 2× BGRA buffer. The frame is
/// still "sideways" (portrait) at this point — the algorithms are orientation-agnostic (the view rotates
/// the result via a layout transform). The caller wraps the 2× buffer in a <c>WriteableBitmap</c> at
/// dpi = 96×2 so its logical size stays native and the existing layout is untouched.</para>
///
/// <para>Colour distances use the standard weighted-YUV metric (Hyllian's 48/7/6). Heavier filters
/// parallelise across output rows; at these resolutions even the worst case is a few Mpx/frame.</para>
/// </summary>
public static class Upscaler
{
    /// <summary>
    /// Applies <paramref name="f"/> to a BGRA8888 <paramref name="src"/> (<paramref name="w"/>×<paramref name="h"/>).
    /// Returns the upscaled buffer with <paramref name="ow"/>×<paramref name="oh"/> and integer
    /// <paramref name="scale"/>. For <see cref="UpscaleFilter.None"/> (or a bad input) it returns
    /// <paramref name="src"/> unchanged with scale 1.
    /// </summary>
    public static byte[] Apply(UpscaleFilter f, byte[] src, int w, int h, out int ow, out int oh, out int scale)
    {
        if (f == UpscaleFilter.None || w <= 0 || h <= 0 || src.Length < checked(w * h * 4))
        {
            ow = w; oh = h; scale = 1; return src;
        }

        scale = 2; ow = w * 2; oh = h * 2;
        var dst = new byte[checked(ow * oh * 4)];
        switch (f)
        {
            case UpscaleFilter.Sharp: Sharp(src, w, h, dst); break;
            case UpscaleFilter.Xbr: Xbr(src, w, h, dst); break;
            case UpscaleFilter.SuperXbr: SuperXbr(src, w, h, dst); break;
            case UpscaleFilter.Fsr: Fsr(src, w, h, dst); break;
            case UpscaleFilter.Anime4K: Anime4K(src, w, h, dst); break;
            default: ow = w; oh = h; scale = 1; return src;
        }
        return dst;
    }

    // ===================== shared pixel helpers =====================

    private readonly struct C3
    {
        public readonly float R, G, B;
        public C3(float r, float g, float b) { R = r; G = g; B = b; }
        public static C3 operator +(C3 a, C3 b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
        public static C3 operator *(C3 a, float s) => new(a.R * s, a.G * s, a.B * s);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    private static byte ToByte(float v) => (byte)(v <= 0 ? 0 : (v >= 255 ? 255 : v + 0.5f));

    /// <summary>Clamped source fetch (BGRA → C3 as R,G,B floats).</summary>
    private static C3 Src(byte[] s, int w, int h, int x, int y)
    {
        x = Clamp(x, 0, w - 1); y = Clamp(y, 0, h - 1);
        int i = (y * w + x) * 4;
        return new C3(s[i + 2], s[i + 1], s[i]);
    }

    private static void Put(byte[] d, int ow, int x, int y, C3 c)
    {
        int i = (y * ow + x) * 4;
        d[i] = ToByte(c.B); d[i + 1] = ToByte(c.G); d[i + 2] = ToByte(c.R); d[i + 3] = 255;
    }

    private static float Luma(C3 c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;

    /// <summary>Weighted-YUV colour distance (Hyllian 48/7/6) — the edge metric for xBR / super-xBR.</summary>
    private static float Dist(C3 a, C3 b)
    {
        float y1 = 0.299f * a.R + 0.587f * a.G + 0.114f * a.B;
        float u1 = -0.169f * a.R - 0.331f * a.G + 0.5f * a.B;
        float v1 = 0.5f * a.R - 0.419f * a.G - 0.081f * a.B;
        float y2 = 0.299f * b.R + 0.587f * b.G + 0.114f * b.B;
        float u2 = -0.169f * b.R - 0.331f * b.G + 0.5f * b.B;
        float v2 = 0.5f * b.R - 0.419f * b.G - 0.081f * b.B;
        return 48f * MathF.Abs(y1 - y2) + 7f * MathF.Abs(u1 - u2) + 6f * MathF.Abs(v1 - v2);
    }

    // ===================== Sharp (nearest 2× + unsharp mask) =====================
    // "sharp-bilinear"-style: crisp integer scale, then a light 3×3 unsharp to add bite. Cheapest, universal.

    private static void Sharp(byte[] s, int w, int h, byte[] d)
    {
        int ow = w * 2, oh = h * 2;
        const float amount = 0.6f;
        Parallel.For(0, oh, Y =>
        {
            int sy = Y >> 1;
            for (int X = 0; X < ow; X++)
            {
                int sx = X >> 1;
                C3 c = Src(s, w, h, sx, sy);
                C3 blur = new(0, 0, 0);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        blur += Src(s, w, h, sx + dx, sy + dy);
                blur *= 1f / 9f;
                Put(d, ow, X, Y, c + (c + blur * -1f) * amount);
            }
        });
    }

    // ===================== xBR (edge-directed, lv1-style) =====================
    // Per source pixel produces 4 sub-pixels. Each corner runs the SE rule under a 90° rotation, so the
    // rule is written once. A flat neighbourhood → all distances 0 → no edge → sub-pixel = centre.

    // Sub-pixel output offset per corner k (k=0 SE, then rotated CW: SW, NW, NE).
    private static readonly int[] CornerOx = { 1, 0, 0, 1 };
    private static readonly int[] CornerOy = { 1, 1, 0, 0 };

    // Rotate an offset (ox,oy) clockwise by k*90° in screen space (y down): CW(x,y) = (-y, x).
    private static (int x, int y) Rot(int ox, int oy, int k)
    {
        for (int i = 0; i < k; i++) { int nx = -oy; oy = ox; ox = nx; }
        return (ox, oy);
    }

    private static void Xbr(byte[] s, int w, int h, byte[] d)
    {
        int ow = w * 2;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                C3 e = Src(s, w, h, x, y);
                for (int k = 0; k < 4; k++)
                {
                    C3 P(int ox, int oy) { var (rx, ry) = Rot(ox, oy, k); return Src(s, w, h, x + rx, y + ry); }
                    C3 F = P(1, 0), Hh = P(0, 1), I = P(1, 1);
                    C3 B = P(0, -1), D = P(-1, 0), Cc = P(1, -1), G = P(-1, 1);
                    C3 F4 = P(2, 0), I4 = P(2, 1), H5 = P(0, 2), I5 = P(1, 2);

                    float wd1 = Dist(Hh, I5) + Dist(Hh, D) + Dist(F, I4) + Dist(F, B) + 4f * Dist(e, I);
                    float wd2 = Dist(e, G) + Dist(e, Cc) + Dist(I, H5) + Dist(I, F4) + 4f * Dist(Hh, F);

                    C3 outc = e;
                    if (wd1 > wd2)
                    {
                        C3 pick = Dist(e, F) <= Dist(e, Hh) ? F : Hh;
                        outc = (e + pick) * 0.5f;   // lv1 blend
                    }
                    Put(d, ow, x * 2 + CornerOx[k], y * 2 + CornerOy[k], outc);
                }
            }
        });
    }

    // ===================== Super-xBR (two-pass directional) =====================
    // Pass 1 fills the block-centre sub-pixel (odd,odd) by choosing the smoother diagonal with a small
    // cubic support. Pass 2 fills the edge-midpoint sub-pixels (odd,even)/(even,odd) the same way. The
    // even,even sub-pixel is the original sample.

    private static void SuperXbr(byte[] s, int w, int h, byte[] d)
    {
        // Edge-directed base (xBR), then a light clamped crisp pass so it reads sharper than plain xBR
        // instead of softer. (A true super-xBR needs a cubic two-pass; this is the managed stand-in.)
        Xbr(s, w, h, d);
        int ow = w * 2, oh = h * 2;
        var tmp = (byte[])d.Clone();
        SharpenClamped(tmp, ow, oh, d, 0.25f);
    }

    // ===================== shared resample / sharpen cores =====================

    /// <summary>Plain bilinear 2× — the smooth base for FSR / Anime4K before their sharpen pass.</summary>
    private static void Bilinear2x(byte[] s, int w, int h, byte[] d)
    {
        int ow = w * 2, oh = h * 2;
        Parallel.For(0, oh, Y =>
        {
            float sy = (Y + 0.5f) * 0.5f - 0.5f;
            int iy = (int)MathF.Floor(sy); float fy = sy - iy;
            for (int X = 0; X < ow; X++)
            {
                float sx = (X + 0.5f) * 0.5f - 0.5f;
                int ix = (int)MathF.Floor(sx); float fx = sx - ix;
                C3 c00 = Src(s, w, h, ix, iy), c10 = Src(s, w, h, ix + 1, iy);
                C3 c01 = Src(s, w, h, ix, iy + 1), c11 = Src(s, w, h, ix + 1, iy + 1);
                C3 top = c00 * (1 - fx) + c10 * fx, bot = c01 * (1 - fx) + c11 * fx;
                Put(d, ow, X, Y, top * (1 - fy) + bot * fy);
            }
        });
    }

    /// <summary>
    /// Contrast-adaptive sharpen (the RCAS idea): unsharp against the 4-neighbour cross, then <b>clamp
    /// each channel to the local min/max</b>. The clamp is what makes it safe — the result can never
    /// overshoot past the neighbours, so no ringing halos and no white specks, however strong <paramref
    /// name="k"/> is.
    /// </summary>
    private static void SharpenClamped(byte[] s, int w, int h, byte[] d, float k)
    {
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                C3 e = Src(s, w, h, x, y);
                C3 a = Src(s, w, h, x - 1, y), b = Src(s, w, h, x, y - 1);
                C3 f = Src(s, w, h, x + 1, y), g = Src(s, w, h, x, y + 1);
                float g1 = 1f + 4f * k;
                float sr = e.R * g1 - k * (a.R + b.R + f.R + g.R);
                float sg = e.G * g1 - k * (a.G + b.G + f.G + g.G);
                float sb = e.B * g1 - k * (a.B + b.B + f.B + g.B);
                Put(d, w, x, y, new C3(
                    ClampCh(sr, e.R, a.R, b.R, f.R, g.R),
                    ClampCh(sg, e.G, a.G, b.G, f.G, g.G),
                    ClampCh(sb, e.B, a.B, b.B, f.B, g.B)));
            }
        });
    }

    private static float ClampCh(float v, float e, float a, float b, float f, float g)
    {
        float mn = MathF.Min(e, MathF.Min(MathF.Min(a, b), MathF.Min(f, g)));
        float mx = MathF.Max(e, MathF.Max(MathF.Max(a, b), MathF.Max(f, g)));
        return v < mn ? mn : (v > mx ? mx : v);
    }

    // ===================== FSR (bilinear + RCAS-style clamped sharpen) =====================
    // A managed reduction of AMD FidelityFX FSR1: a smooth resample (EASU stand-in) + contrast-adaptive
    // sharpening (RCAS). The RCAS clamp kills the overshoot that was speckling the image white.

    private static void Fsr(byte[] s, int w, int h, byte[] d)
    {
        int ow = w * 2, oh = h * 2;
        var lin = new byte[ow * oh * 4];
        Bilinear2x(s, w, h, lin);
        SharpenClamped(lin, ow, oh, d, 0.30f);
    }

    // ===================== Anime4K (edge-directed base + strong clamped line sharpen) =====================
    // bloc97's filter is about crisp, thin line-art. Here: the edge-directed (xBR) base for clean diagonals,
    // then a strong clamped sharpen for punch — pronounced, never blurry; the clamp keeps cel colours flat.

    private static void Anime4K(byte[] s, int w, int h, byte[] d)
    {
        Xbr(s, w, h, d);
        int ow = w * 2, oh = h * 2;
        var tmp = (byte[])d.Clone();
        SharpenClamped(tmp, ow, oh, d, 0.50f);
    }
}
