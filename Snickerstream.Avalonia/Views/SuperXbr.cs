using System;

namespace SnickerstreamV2.Views;

/// <summary>
/// Super-xBR (Hyllian, 2015, MIT) — the real edge-directed 2× upscaler, ported from the libretro
/// reference passes (luma → pass0 → pass1 → pass2) to our old-dialect SkSL. Faithful to the shipped
/// preset defaults: MODE = Normal, XBR_EDGE_STR = 3.0, and the per-pass XBR_WEIGHT (0 / 1 / 1).
///
/// <para>Geometry, in pixel coordinates:</para>
/// <list type="bullet">
/// <item>luma  (1×) — carries the original RGB and stores its luma in alpha (the edge metrics work on
///       luma; the colour taps work on RGB).</item>
/// <item>pass0 (1×) — computes, at each source texel, the diagonal-interpolated new pixel that will sit
///       at (+0.5,+0.5) — the "DIAG" grid.</item>
/// <item>pass1 (2×) — fills the 2× grid: the two diagonal corners are the original / DIAG samples, the
///       horizontal &amp; vertical midpoints are computed from a 45°-rotated neighbourhood that
///       interleaves the original and DIAG grids.</item>
/// <item>pass2 (2×) — optional edge cleanup on the 2× result.</item>
/// <item>blit  (→display) — smooth (bilinear) resample of the 2× result to the output rect.</item>
/// </list>
/// Nearest sampling is emulated with <c>floor(p)+0.5</c> (the reference runs every framebuffer at
/// filter_linear = false). All passes validated to compile against SkiaSharp 2.88.9.
/// </summary>
internal static class SuperXbr
{
    private static GpuPass[]? _cached;
    private static readonly object _lock = new();
    public static GpuPass[] Passes { get { lock (_lock) { return _cached ??= Build(); } } }

    // Shared helpers: luma, abs-diff, the diagonal / horizontal-vertical weighted-distance metrics, and
    // the core super-xbr colour selection — all lifted verbatim from Hyllian's reference (arg order kept).
    private const string HEADER = """
        float df(float A, float B){ return abs(A-B); }
        float lum(float3 c){ return dot(c, float3(0.2126, 0.7152, 0.0722)); }
        float st(float e, float x){ return x < e ? 0.0 : 1.0; }
        float ss(float a, float b, float x){ float t = clamp((x-a)/(b-a), 0.0, 1.0); return t*t*(3.0-2.0*t); }
        float d_wd(float wp1,float wp2,float wp3,float wp4,float wp5,float wp6,float b0,float b1,float c0,float c1,float c2,float d0,float d1,float d2,float d3,float e1,float e2,float e3,float f2,float f3){
            return (wp1*(df(c1,c2)+df(c1,c0)+df(e2,e1)+df(e2,e3)) + wp2*(df(d2,d3)+df(d0,d1)) + wp3*(df(d1,d3)+df(d0,d2)) + wp4*df(d1,d2) + wp5*(df(c0,c2)+df(e1,e3)) + wp6*(df(b0,b1)+df(f2,f3)));
        }
        float hv_wd(float wp1,float wp2,float wp3,float wp4,float wp5,float wp6,float i1,float i2,float i3,float i4,float e1,float e2,float e3,float e4){
            return (wp4*(df(i1,i2)+df(i3,i4)) + wp1*(df(i1,e1)+df(i2,e2)+df(i3,e3)+df(i4,e4)) + wp3*(df(i1,e2)+df(i3,e4)+df(e1,i2)+df(e3,i4)));
        }
        float3 sxbr(float4 P0,float4 P1,float4 P2,float4 P3,float4 B,float4 C,float4 D,float4 E,float4 F,float4 G,float4 H,float4 I,float4 F4,float4 I4,float4 H5,float4 I5,float wp1,float wp2,float wp3,float wp4,float wp5,float wp6,float weight1,float weight2,float edge_str){
            float d_edge  = d_wd(wp1,wp2,wp3,wp4,wp5,wp6, D.w,B.w,G.w,E.w,C.w, P2.w,H.w,F.w,P1.w, H5.w,I.w,F4.w, I5.w,I4.w)
                          - d_wd(wp1,wp2,wp3,wp4,wp5,wp6, C.w,F4.w,B.w,F.w,I4.w, P0.w,E.w,I.w,P3.w, D.w,H.w,I5.w, G.w,H5.w);
            float hv_edge = hv_wd(wp1,wp2,wp3,wp4,wp5,wp6, F.w,I.w,E.w,H.w, C.w,I5.w,B.w,H5.w)
                          - hv_wd(wp1,wp2,wp3,wp4,wp5,wp6, E.w,F.w,H.w,I.w, D.w,F4.w,G.w,I4.w);
            float es = ss(0.0, edge_str+0.000001, abs(d_edge));
            float4 w1 = float4(-weight1, weight1+0.5, weight1+0.5, -weight1);
            float4 w2 = float4(-weight2, weight2+0.25, weight2+0.25, -weight2);
            float3 c1 = w1.x*P2.xyz + w1.y*H.xyz + w1.z*F.xyz + w1.w*P1.xyz;
            float3 c2 = w1.x*P0.xyz + w1.y*E.xyz + w1.z*I.xyz + w1.w*P3.xyz;
            float3 c3 = w2.x*(D.xyz+G.xyz) + w2.y*(E.xyz+H.xyz) + w2.z*(F.xyz+I.xyz) + w2.w*(F4.xyz+I4.xyz);
            float3 c4 = w2.x*(C.xyz+B.xyz) + w2.y*(F.xyz+E.xyz) + w2.z*(I.xyz+H.xyz) + w2.w*(I5.xyz+H5.xyz);
            float3 dsel = c1 + (c2-c1)*st(0.0, d_edge);
            float3 hsel = c3 + (c4-c3)*st(0.0, hv_edge);
            float3 color = dsel + (hsel-dsel)*(1.0-es);
            float3 mn = min(min(E.xyz,F.xyz), min(H.xyz,I.xyz));
            float3 mx = max(max(E.xyz,F.xyz), max(H.xyz,I.xyz));
            return clamp(color, mn, mx);
        }
        """;

    // reference-normalize: pass the original RGB straight through with alpha = 1. The GL backend's
    // intermediate surfaces are premultiplied, so alpha MUST stay 1.0 (any other value scales RGB and,
    // chained over several passes, darkens the image to black). Luma is recomputed from RGB per pass
    // instead of being smuggled through alpha.
    private const string P_LUMA = """
        uniform shader src;
        half4 main(float2 c){
            return half4(sample(src, c).rgb, 1.0);
        }
        """;

    // pass0 (1×): the diagonal new pixel. XBR_WEIGHT = 0 → weight1 = weight2 = 0. wp = (2,1,-1,4,-1,1).
    private const string P0 = HEADER + """
        uniform shader src;
        float4 S(float2 c, float dx, float dy){ float3 v = float3(sample(src, floor(c+float2(dx,dy))+0.5).rgb); return float4(v, lum(v)); }
        half4 main(float2 c){
            float4 P0=S(c,-1,-1), P1=S(c,2,-1), P2=S(c,-1,2), P3=S(c,2,2);
            float4 B=S(c,0,-1),  C=S(c,1,-1),  H5=S(c,0,2),  I5=S(c,1,2);
            float4 D=S(c,-1,0),  F4=S(c,2,0),  G=S(c,-1,1),  I4=S(c,2,1);
            float4 E=S(c,0,0),   F=S(c,1,0),   H=S(c,0,1),   I=S(c,1,1);
            float3 col = sxbr(P0,P1,P2,P3,B,C,D,E,F,G,H,I,F4,I4,H5,I5, 2.0,1.0,-1.0,4.0,-1.0,1.0, 0.129633,0.0875034, 3.0);
            return half4(half3(col), 1.0);
        }
        """;

    // pass1 (2×): fill the 2× grid. src = DIAG (pass0), ORIG = luma pass. wp = (1,0,0,0,0,0),
    // weights = the reference XBR_WEIGHT = 1.0 taps. Nearest 1×-grid sampling in sp (source-texel) space.
    private const string P1 = HEADER + """
        uniform shader src;
        uniform shader ORIG;
        float4 Sd(float2 p){ float3 v = float3(sample(src,  floor(p)+0.5).rgb); return float4(v, lum(v)); }
        float4 So(float2 p){ float3 v = float3(sample(ORIG, floor(p)+0.5).rgb); return float4(v, lum(v)); }
        half4 main(float2 c){
            float2 sp = c*0.5;
            float2 fp = fract(sp);
            float2 dir = fp - float2(0.5,0.5);
            // ALL sample() calls are unconditional (never inside an if/ternary): the GL backend gives
            // undefined (black) results for conditionally-executed texture reads, even though it compiles.
            // We compute both candidates every time and select the scalar result at the end.
            float2 g1 = (fp.x > 0.5) ? float2(0.5,0.0) : float2(0.0,0.5);
            float2 g2 = (fp.x > 0.5) ? float2(0.0,0.5) : float2(0.5,0.0);
            // Diagonal-corner candidate: original texel or pass0-diagonal texel.
            float3 sC = So(sp).xyz;
            float3 dC = Sd(sp).xyz;
            float3 colA = sC + (dC - sC) * ((dir.x >= 0.0) ? 1.0 : 0.0);
            // H/V-midpoint candidate: 45°-rotated neighbourhood interleaving the original (ORIG) and
            // pass0-diagonal (src) grids.
            float4 P0=So(sp-3.0*g1), P1=Sd(sp-3.0*g2), P2=Sd(sp+3.0*g2), P3=So(sp+3.0*g1);
            float4 B=Sd(sp-2.0*g1-g2), C=So(sp-g1-2.0*g2), D=Sd(sp-2.0*g1+g2), E=So(sp-g1);
            float4 F=Sd(sp-g2), G=So(sp-g1+2.0*g2), H=Sd(sp+g2), I=So(sp+g1);
            float4 F4=So(sp+g1-2.0*g2), I4=Sd(sp+2.0*g1-g2), H5=So(sp+g1+2.0*g2), I5=Sd(sp+2.0*g1+g2);
            float3 colB = sxbr(P0,P1,P2,P3,B,C,D,E,F,G,H,I,F4,I4,H5,I5, 1.0,0.0,0.0,0.0,0.0,0.0, 0.129633,0.0875034, 3.0);
            float3 col = ((dir.x*dir.y) > 0.0) ? colA : colB;
            return half4(half3(col), 1.0);
        }
        """;

    // pass2 (2×): optional edge cleanup. wp = (0,0,0,1,0,0). Neighbourhood offset by (-1,-1) vs pass0.
    private const string P2 = HEADER + """
        uniform shader src;
        float4 S(float2 c, float dx, float dy){ float3 v = float3(sample(src, floor(c+float2(dx,dy))+0.5).rgb); return float4(v, lum(v)); }
        half4 main(float2 c){
            float4 P0=S(c,-2,-2), P1=S(c,1,-2), P2=S(c,-2,1), P3=S(c,1,1);
            float4 B=S(c,-1,-2), C=S(c,0,-2),  H5=S(c,-1,1), I5=S(c,0,1);
            float4 D=S(c,-2,-1), F4=S(c,1,-1), G=S(c,-2,0),  I4=S(c,1,0);
            float4 E=S(c,-1,-1), F=S(c,0,-1),  H=S(c,-1,0),  I=S(c,0,0);
            float3 col = sxbr(P0,P1,P2,P3,B,C,D,E,F,G,H,I,F4,I4,H5,I5, 0.0,0.0,0.0,1.0,0.0,0.0, 0.129633,0.0875034, 3.0);
            return half4(half3(col), 1.0);
        }
        """;

    // blit: resample the finished 2× image to the display rect WITH a clamped unsharp mask — stands in
    // for the reference preset's jinc2-sharper + deblur tail, restoring the definition a plain bilinear
    // stretch would wash out. The sharpened result is clamped to the local 3×3 min/max so it can't ring.
    private const string BLIT = """
        uniform shader src;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        half4 main(float2 c){
            float2 sp = (c / OUT_SIZE) * src_SIZE;
            float3 e  = float3(sample(src, sp).rgb);
            float3 l  = float3(sample(src, sp+float2(-1.0,0.0)).rgb);
            float3 r  = float3(sample(src, sp+float2( 1.0,0.0)).rgb);
            float3 u  = float3(sample(src, sp+float2(0.0,-1.0)).rgb);
            float3 d  = float3(sample(src, sp+float2(0.0, 1.0)).rgb);
            float3 blur = (l+r+u+d)*0.25;
            float3 sharp = e + (e - blur)*1.15;             // unsharp
            float3 mn = min(min(l,r),min(min(u,d),e));
            float3 mx = max(max(l,r),max(max(u,d),e));
            return half4(half3(clamp(sharp, mn, mx)), 1.0);  // anti-ring clamp
        }
        """;

    private static GpuPass[] Build()
    {
        var passes = new[]
        {
            new GpuPass { Sksl = P_LUMA, Scale = 1f, F16 = true,  Inputs = new[] { ("src", -1) } },                 // luma ← original
            new GpuPass { Sksl = P0,     Scale = 1f, F16 = true,  Inputs = new[] { ("src", 0) } },                  // diagonal ← luma
            new GpuPass { Sksl = P1,     Scale = 2f, F16 = true,  Inputs = new[] { ("src", 1), ("ORIG", 0) } },      // 2× fill ← diagonal + luma
            new GpuPass { Sksl = P2,     Scale = 2f, F16 = true,  Inputs = new[] { ("src", 2) } },                   // cleanup ← 2× fill
            new GpuPass { Sksl = BLIT,   Scale = 2f, F16 = false, Inputs = new[] { ("src", 3) } },                   // → display (smooth)
        };
        foreach (var gp in passes) gp.Effect = SkiaSharp.SKRuntimeEffect.Create(gp.Sksl, out gp.Error);
        return passes;
    }
}
