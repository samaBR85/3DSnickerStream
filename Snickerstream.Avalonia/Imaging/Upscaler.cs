using System;
using System.Threading.Tasks;

namespace SnickerstreamV2.Imaging;

/// <summary>The upscaling filter applied to a decoded frame before display (Option A: pure-managed CPU).</summary>
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
        int ow = w * 2, oh = h * 2;

        // even,even = original; seed the whole target so pass 2 can read pass-1 results.
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
                Put(d, ow, x * 2, y * 2, Src(s, w, h, x, y));
        });

        C3 D(int x, int y) => Src(d, ow, oh, Clamp(x, 0, ow - 1), Clamp(y, 0, oh - 1));

        // Pass 1: centre sub-pixels at (2x+1, 2y+1) from the 4 diagonal originals.
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                C3 p00 = Src(s, w, h, x, y), p10 = Src(s, w, h, x + 1, y);
                C3 p01 = Src(s, w, h, x, y + 1), p11 = Src(s, w, h, x + 1, y + 1);
                // extended diagonal support for edge strength
                C3 pm = Src(s, w, h, x - 1, y - 1), pp = Src(s, w, h, x + 2, y + 2);
                C3 pnm = Src(s, w, h, x + 2, y - 1), pmn = Src(s, w, h, x - 1, y + 2);

                float dEdge = Dist(pm, p11) + Dist(p00, pp) + Dist(pnm, p01) + Dist(p10, pmn) * 0f; // guard
                float dMain = Dist(p00, p11) + Dist(pm, p11) + Dist(p00, pp);   // "\" diagonal
                float dAnti = Dist(p10, p01) + Dist(pnm, p01) + Dist(p10, pmn); // "/" diagonal
                _ = dEdge;

                C3 c = dMain <= dAnti ? (p00 + p11) * 0.5f : (p10 + p01) * 0.5f;
                Put(d, ow, x * 2 + 1, y * 2 + 1, c);
            }
        });

        // Pass 2: edge-midpoint sub-pixels, from the 4 orthogonal neighbours already present in d.
        Parallel.For(0, oh, Y =>
        {
            for (int X = 0; X < ow; X++)
            {
                if (((X ^ Y) & 1) == 0) continue;                 // even,even (orig) or odd,odd (pass 1) — done
                C3 l = D(X - 1, Y), r = D(X + 1, Y), u = D(X, Y - 1), dn = D(X, Y + 1);
                C3 c = Dist(l, r) <= Dist(u, dn) ? (l + r) * 0.5f : (u + dn) * 0.5f;
                Put(d, ow, X, Y, c);
            }
        });
    }

    // ===================== FSR (EASU directional upsample + RCAS sharpen) =====================
    // A managed reduction of AMD FidelityFX FSR1: an edge-adaptive anisotropic Lanczos-2 resample (EASU)
    // followed by contrast-adaptive sharpening (RCAS). Good general-purpose upscaler for 3D games.

    private static void Fsr(byte[] s, int w, int h, byte[] d)
    {
        int ow = w * 2, oh = h * 2;
        var easu = new byte[ow * oh * 4];

        Parallel.For(0, oh, Y =>
        {
            for (int X = 0; X < ow; X++)
            {
                // Output pixel centre in source space.
                float sx = (X + 0.5f) * 0.5f - 0.5f;
                float sy = (Y + 0.5f) * 0.5f - 0.5f;
                int ix = (int)MathF.Floor(sx), iy = (int)MathF.Floor(sy);
                float fx = sx - ix, fy = sy - iy;

                // 3×3 luma for edge direction/length (EASU's feature analysis).
                C3 b = Src(s, w, h, ix, iy - 1);
                C3 dd = Src(s, w, h, ix - 1, iy), e = Src(s, w, h, ix, iy), f = Src(s, w, h, ix + 1, iy);
                C3 hh = Src(s, w, h, ix, iy + 1);
                float lb = Luma(b), ld = Luma(dd), le = Luma(e), lf = Luma(f), lh = Luma(hh);
                float dirX = ld - lf;
                float dirY = lb - lh;
                float len = MathF.Max(MathF.Abs(dirX), MathF.Abs(dirY));
                len = len <= 1e-3f ? 0f : MathF.Min(1f, len / 32f);   // 0 = isotropic, 1 = strong edge

                // Anisotropic Lanczos-2 over a 4×4 support, stretched along the edge normal.
                float invLen = 1f / MathF.Sqrt(dirX * dirX + dirY * dirY + 1e-6f);
                float nx = dirX * invLen, ny = dirY * invLen;
                C3 acc = new(0, 0, 0); float wsum = 0f;
                for (int oy = -1; oy <= 2; oy++)
                    for (int oxk = -1; oxk <= 2; oxk++)
                    {
                        float ddx = oxk - fx, ddy = oy - fy;
                        // squash distance across the edge → sharper along features
                        float along = ddx * nx + ddy * ny;
                        float across = -ddx * ny + ddy * nx;
                        float stretch = 1f + 2f * len;
                        float dist2 = along * along + (across * stretch) * (across * stretch);
                        float wgt = Lanczos2(MathF.Sqrt(dist2));
                        if (wgt == 0f) continue;
                        acc += Src(s, w, h, ix + oxk, iy + oy) * wgt;
                        wsum += wgt;
                    }
                C3 outc = wsum > 0f ? acc * (1f / wsum) : e;
                Put(easu, ow, X, Y, outc);
            }
        });

        Rcas(easu, ow, oh, d, 0.25f);
    }

    private static float Lanczos2(float x)
    {
        x = MathF.Abs(x);
        if (x >= 2f) return 0f;
        if (x < 1e-4f) return 1f;
        float px = MathF.PI * x;
        return (MathF.Sin(px) / px) * (MathF.Sin(px * 0.5f) / (px * 0.5f));
    }

    /// <summary>FSR RCAS: contrast-adaptive sharpen (5-tap "+" kernel), sharpness 0..1.</summary>
    private static void Rcas(byte[] s, int w, int h, byte[] d, float sharpness)
    {
        float amt = 0.5f * sharpness;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                C3 e = Src(s, w, h, x, y);
                C3 b = Src(s, w, h, x, y - 1), a = Src(s, w, h, x - 1, y);
                C3 f = Src(s, w, h, x + 1, y), g = Src(s, w, h, x, y + 1);
                // local min/max on luma to bound the sharpen (avoids ringing)
                float le = Luma(e), lmin = MathF.Min(le, MathF.Min(MathF.Min(Luma(a), Luma(b)), MathF.Min(Luma(f), Luma(g))));
                float lmax = MathF.Max(le, MathF.Max(MathF.Max(Luma(a), Luma(b)), MathF.Max(Luma(f), Luma(g))));
                float contrast = (lmax - lmin) / 255f;
                float w0 = amt * (1f - MathF.Min(1f, contrast));   // sharpen less where contrast is already high
                C3 sum = (a + b + f + g) * -1f + e * 4f;
                Put(d, w, x, y, e + sum * w0);
            }
        });
    }

    // ===================== Anime4K (classic v1: thin & push lines) =====================
    // bloc97's original: upscale, then push dark line-art inward along the luminance gradient to sharpen
    // edges. Two light passes over a bilinear 2×; great on cel-shaded / anime-style art.

    private static void Anime4K(byte[] s, int w, int h, byte[] d)
    {
        int ow = w * 2, oh = h * 2;

        // Bilinear 2× seed.
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

        // Two push passes: move each pixel toward its darker/lighter neighbour across the strongest
        // luminance gradient, thinning line-art (the essence of Anime4K's "push" kernel).
        var tmp = new byte[ow * oh * 4];
        const float strength = 0.33f;
        for (int pass = 0; pass < 2; pass++)
        {
            byte[] cur = (pass == 0) ? d : tmp, nxt = (pass == 0) ? tmp : d;
            Parallel.For(0, oh, Y =>
            {
                for (int X = 0; X < ow; X++)
                {
                    C3 c = Src(cur, ow, oh, X, Y);
                    C3 l = Src(cur, ow, oh, X - 1, Y), r = Src(cur, ow, oh, X + 1, Y);
                    C3 u = Src(cur, ow, oh, X, Y - 1), dn = Src(cur, ow, oh, X, Y + 1);
                    // gradient: pull toward the neighbour that continues the darker line
                    float gx = Luma(r) - Luma(l), gy = Luma(dn) - Luma(u);
                    C3 target = c;
                    if (MathF.Abs(gx) >= MathF.Abs(gy))
                        target = gx > 0 ? l : r;   // push away from the bright side → sharpen the edge
                    else
                        target = gy > 0 ? u : dn;
                    Put(nxt, ow, X, Y, c * (1 - strength) + target * strength);
                }
            });
        }
    }
}
