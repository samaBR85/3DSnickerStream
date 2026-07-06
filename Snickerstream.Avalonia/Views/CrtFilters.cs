using System;

namespace SnickerstreamV2.Views;

/// <summary>
/// A retro CRT filter: scanlines + an aperture-grille phosphor mask, rendered at 3× into a GPU surface and
/// then blitted to the display. Unlike the upscalers it outputs colour (opaque), so it needs no alpha-bias.
///
/// <para>Note: the 3DS top screen streams rotated 270°, so a real CRT's 240 horizontal scanlines run along
/// the source's 240-px axis (X); the vertical phosphor stripes run along the source's long axis (surface Y).</para>
/// </summary>
internal static class CrtFilters
{
    private static GpuPass[]? _cached;
    private static readonly object _lock = new();
    public static GpuPass[] Passes { get { lock (_lock) { return _cached ??= Build(); } } }

    // Pass 0 — CRT at 3×: bilinear source + scanline beam (on the source X axis) + aperture-grille mask.
    private const string Crt = """
        uniform shader src;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        float md(float x,float y){ return x - y*floor(x/y); }
        half3 samp(float2 sp){
            float2 mp=sp-0.5; float2 mf=fract(mp); float2 mb=floor(mp)+0.5;
            half3 a=sample(src,mb).rgb,b=sample(src,mb+float2(1,0)).rgb,cc=sample(src,mb+float2(0,1)).rgb,d=sample(src,mb+float2(1,1)).rgb;
            return mix(mix(a,b,half(mf.x)), mix(cc,d,half(mf.x)), half(mf.y));
        }
        half4 main(float2 c){
            float2 uv=c/OUT_SIZE;
            float2 sp=uv*src_SIZE;
            half3 col=samp(sp);
            // Scanlines: prominent horizontal lines (one per source row on the 240-px axis).
            float sl=fract(sp.x);
            float beam=0.35 + 0.65*exp(-((sl-0.5)*(sl-0.5))/(2.0*0.24*0.24));
            col*=half(beam);
            // Aperture-grille mask: SMOOTH (cosine → band-limited, no aliasing), subtle, one RGB cycle / 3 source px.
            float ph=sp.y*2.0943951;                    // 2*pi/3 per source pixel
            half3 mask=half3(0.75)+half3(0.25)*half3(cos(ph), cos(ph-2.0943951), cos(ph-4.1887902));
            col*=mask;
            col*=half(1.5);                             // brightness compensation
            return half4(min(col, half3(1.0)), 1.0);
        }
        """;

    // Pass 1 — bilinear blit of the 3× CRT image to the display.
    private const string Blit = """
        uniform shader src;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        half4 main(float2 c){
            float2 uv=c/OUT_SIZE;
            float2 sp=uv*src_SIZE;
            float2 mp=sp-0.5; float2 mf=fract(mp); float2 mb=floor(mp)+0.5;
            half3 a=sample(src,mb).rgb,b=sample(src,mb+float2(1,0)).rgb,cc=sample(src,mb+float2(0,1)).rgb,d=sample(src,mb+float2(1,1)).rgb;
            return half4(mix(mix(a,b,half(mf.x)),mix(cc,d,half(mf.x)),half(mf.y)),1.0);
        }
        """;

    private static GpuPass[] Build()
    {
        var passes = new[]
        {
            new GpuPass { Sksl = Crt,  Scale = 3f, F16 = false, Inputs = new[] { ("src", -1) } },   // CRT @3× ← original
            new GpuPass { Sksl = Blit, Scale = 1f, F16 = false, Inputs = new[] { ("src", 0) } },    // display ← pass0
        };
        foreach (var gp in passes) gp.Effect = SkiaSharp.SKRuntimeEffect.Create(gp.Sksl, out gp.Error);
        return passes;
    }
}
