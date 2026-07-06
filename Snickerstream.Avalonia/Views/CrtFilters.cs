using System;
using System.Collections.Generic;
using SnickerstreamV2.Imaging;

namespace SnickerstreamV2.Views;

/// <summary>
/// Retro CRT display effects: scanlines + a phosphor mask, rendered at 3× into a GPU surface and then
/// blitted to the display. Unlike the upscalers they output colour (opaque), so they need no alpha-bias.
/// All variants take an <c>INTENSITY</c> uniform (0..1) that blends the effect toward a flat passthrough,
/// so the user can dial the strength instead of an all-or-nothing toggle.
///
/// <para>Note: the 3DS top screen streams rotated 270°, so a real CRT's 240 horizontal scanlines run along
/// the source's 240-px axis (X); the vertical phosphor stripes run along the source's long axis (surface Y).</para>
/// </summary>
internal static class CrtFilters
{
    private static readonly Dictionary<EffectFilter, GpuPass[]> _cached = new();
    private static readonly object _lock = new();

    public static GpuPass[] PassesFor(EffectFilter e)
    {
        lock (_lock)
        {
            if (_cached.TryGetValue(e, out var cached)) return cached;
            var pass0 = e switch
            {
                EffectFilter.CrtDot => CrtDot,
                EffectFilter.CrtCurved => CrtCurved,
                _ => Crt,
            };
            var passes = Build(pass0);
            _cached[e] = passes;
            return passes;
        }
    }

    // Shared helpers + scanline/mask core, textually inlined into each variant (SkSL has no #include).
    private const string Shared = """
        uniform shader src;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        uniform float INTENSITY;
        half3 samp(float2 sp){
            float2 mp=sp-0.5; float2 mf=fract(mp); float2 mb=floor(mp)+0.5;
            half3 a=sample(src,mb).rgb,b=sample(src,mb+float2(1,0)).rgb,cc=sample(src,mb+float2(0,1)).rgb,d=sample(src,mb+float2(1,1)).rgb;
            return mix(mix(a,b,half(mf.x)), mix(cc,d,half(mf.x)), half(mf.y));
        }
        """;

    // Pass 0 (base) — prominent horizontal scanlines + a smooth aperture-grille mask.
    private const string Crt = Shared + """
        half4 main(float2 c){
            float2 uv=c/OUT_SIZE;
            float2 sp=uv*src_SIZE;
            half3 col=samp(sp);
            float sl=fract(sp.x);
            float beamRaw=0.35 + 0.65*exp(-((sl-0.5)*(sl-0.5))/(2.0*0.24*0.24));
            half beam=half(mix(1.0, beamRaw, INTENSITY));
            col*=beam;
            float ph=sp.y*2.0943951;
            half3 maskRaw=half3(0.75)+half3(0.25)*half3(cos(ph), cos(ph-2.0943951), cos(ph-4.1887902));
            half3 mask=mix(half3(1.0), maskRaw, half(INTENSITY));
            col*=mask;
            col*=half(mix(1.0, 1.5, INTENSITY));
            return half4(min(col, half3(1.0)), 1.0);
        }
        """;

    // Pass 0 (dot mask) — the base mask phase-shifted every other row (shadow-mask stagger).
    private const string CrtDot = Shared + """
        half4 main(float2 c){
            float2 uv=c/OUT_SIZE;
            float2 sp=uv*src_SIZE;
            half3 col=samp(sp);
            float sl=fract(sp.x);
            float beamRaw=0.35 + 0.65*exp(-((sl-0.5)*(sl-0.5))/(2.0*0.24*0.24));
            half beam=half(mix(1.0, beamRaw, INTENSITY));
            col*=beam;
            float rowY=sp.y*0.5;
            float rowParity=rowY - floor(rowY);
            float rowShift=rowParity < 0.5 ? 0.0 : 2.0943951;
            float ph=sp.y*2.0943951 + rowShift;
            half3 maskRaw=half3(0.7)+half3(0.3)*half3(cos(ph), cos(ph-2.0943951), cos(ph-4.1887902));
            half3 mask=mix(half3(1.0), maskRaw, half(INTENSITY));
            col*=mask;
            col*=half(mix(1.0, 1.6, INTENSITY));
            return half4(min(col, half3(1.0)), 1.0);
        }
        """;

    // Pass 0 (curved) — barrel-distorted UVs (glass curvature), black outside bounds, mild vignette.
    private const string CrtCurved = Shared + """
        float2 warp(float2 uv, float amt){
            float2 c=uv*2.0 - 1.0;
            float2 off=c.yx*c.yx*c*amt;
            return (c+off)*0.5 + 0.5;
        }
        half4 main(float2 c){
            float2 uv0=c/OUT_SIZE;
            float amt=mix(0.0, 0.06, INTENSITY);
            float2 uv=warp(uv0, amt);
            if (uv.x<0.0 || uv.x>1.0 || uv.y<0.0 || uv.y>1.0) return half4(0.0,0.0,0.0,1.0);
            float2 sp=uv*src_SIZE;
            half3 col=samp(sp);
            float sl=fract(sp.x);
            float beamRaw=0.35 + 0.65*exp(-((sl-0.5)*(sl-0.5))/(2.0*0.24*0.24));
            half beam=half(mix(1.0, beamRaw, INTENSITY));
            col*=beam;
            float ph=sp.y*2.0943951;
            half3 maskRaw=half3(0.75)+half3(0.25)*half3(cos(ph), cos(ph-2.0943951), cos(ph-4.1887902));
            half3 mask=mix(half3(1.0), maskRaw, half(INTENSITY));
            col*=mask;
            col*=half(mix(1.0, 1.5, INTENSITY));
            float2 vc=uv-0.5;
            half vig=half(mix(1.0, 1.0 - dot(vc,vc)*0.55, INTENSITY));
            col*=vig;
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

    private static GpuPass[] Build(string pass0Sksl)
    {
        var passes = new[]
        {
            new GpuPass { Sksl = pass0Sksl, Scale = 3f, F16 = false, Inputs = new[] { ("src", -1) } },   // CRT @3× ← original
            new GpuPass { Sksl = Blit,      Scale = 1f, F16 = false, Inputs = new[] { ("src", 0) } },     // display ← pass0
        };
        foreach (var gp in passes) gp.Effect = SkiaSharp.SKRuntimeEffect.Create(gp.Sksl, out gp.Error);
        return passes;
    }
}
