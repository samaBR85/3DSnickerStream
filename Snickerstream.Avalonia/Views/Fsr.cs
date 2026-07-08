using System;

namespace SnickerstreamV2.Views;

/// <summary>
/// AMD FidelityFX Super Resolution 1.0 (FSR 1, MIT) — the REAL edge-adaptive spatial upscaler, ported from
/// AMD's <c>ffx_fsr1.h</c> reference (via agyild's mpv port) to our old-dialect SkSL. Two passes:
/// <list type="bullet">
/// <item><b>EASU</b> (2×) — Edge-Adaptive Spatial Upsampling: a 12-tap kernel that analyses the local
///       gradient direction/length (on luma) and applies an anisotropic Lanczos-2 approximation rotated
///       along the edge, then clamps to the central 2×2 to kill ringing. Runs on RGB (the mpv port is
///       luma-only for speed; the original filters all three channels, which is what we want here).</item>
/// <item><b>RCAS</b> (→display) — Robust Contrast-Adaptive Sharpening: solves per-fragment for the maximum
///       sharpening lobe that won't clip, with a built-in noise limiter so it sharpens real edges without
///       amplifying JPEG grain. This is the final pass and also resamples EASU's 2× result to the rect.</item>
/// </list>
/// <para>AMD's fast bit-hack reciprocals (<c>uintBitsToFloat</c> tricks) are replaced with exact math —
/// SkiaSharp 2.88.9's SkSL predates them and the input frame is tiny, so precision costs nothing. All
/// <c>sample()</c> taps land on exact texel centres and are unconditional (the GL backend returns black for
/// conditionally-executed texture reads). Both passes validated to compile against SkiaSharp 2.88.9.</para>
/// <para>Distinct from the "Lanczos"/"Lanczos+" filters (a plain Lanczos-2 + clamped sharpen) — this is the
/// genuine directional FSR algorithm.</para>
/// </summary>
internal static class Fsr
{
    private static GpuPass[]? _cached;
    private static readonly object _lock = new();
    public static GpuPass[] Passes { get { lock (_lock) { return _cached ??= Build(); } } }

    // Pass 1 — EASU (2×). Reads the native frame (src); OUT_SIZE = 2×native, so srcpos = c·(native/2native)
    // maps each output pixel back to source-texel space. Direction/length analysis per AMD's FsrEasuSet
    // (restructured to a value-returning helper — no inout/bool, which the old dialect dislikes), then 12
    // anisotropic Lanczos taps per FsrEasuTap, then the deringing min/max clamp on the central 2×2 (f,g,j,k).
    private const string EASU = """
        uniform shader src;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        float rcpg(float a){ return 1.0 / max(a, 1e-6); }
        float luma3(float3 c){ return 0.5*c.r + c.g + 0.5*c.b; }
        float3 T(float2 fp, float dx, float dy){ return sample(src, fp + float2(dx,dy)).rgb; }
        float3 easuSet(float wgt, float lA, float lB, float lC, float lD, float lE){
            float dc = lD - lC; float cb = lC - lB;
            float lenX = clamp(abs(lD - lB) * rcpg(max(abs(dc), abs(cb))), 0.0, 1.0); lenX *= lenX;
            float ec = lE - lC; float ca = lC - lA;
            float lenY = clamp(abs(lE - lA) * rcpg(max(abs(ec), abs(ca))), 0.0, 1.0); lenY *= lenY;
            return float3((lD - lB) * wgt, (lE - lA) * wgt, (lenX + lenY) * wgt);
        }
        float4 tap(float2 off, float2 dir, float2 len2, float lob, float clp, float3 col){
            float2 v = float2(off.x*dir.x + off.y*dir.y, -off.x*dir.y + off.y*dir.x);
            v *= len2;
            float d2 = min(dot(v, v), clp);
            float wB = (2.0/5.0)*d2 - 1.0; wB *= wB;
            float wA = lob*d2 - 1.0; wA *= wA;
            wB = (25.0/16.0)*wB - (25.0/16.0 - 1.0);
            float w = wB * wA;
            return float4(col*w, w);
        }
        half4 main(float2 c){
            float2 srcpos = (c / OUT_SIZE) * src_SIZE;
            float2 P = srcpos - 0.5;
            float2 fp = floor(P);
            float2 pp = P - fp;
            float3 cb_=T(fp,0.5,-0.5), cc_=T(fp,1.5,-0.5);
            float3 ce=T(fp,-0.5,0.5),  cf=T(fp,0.5,0.5),  cg=T(fp,1.5,0.5), ch=T(fp,2.5,0.5);
            float3 ci=T(fp,-0.5,1.5),  cj=T(fp,0.5,1.5),  ck=T(fp,1.5,1.5), cl=T(fp,2.5,1.5);
            float3 cn=T(fp,0.5,2.5),   co=T(fp,1.5,2.5);
            float bL=luma3(cb_), cL=luma3(cc_), eL=luma3(ce), fL=luma3(cf), gL=luma3(cg), hL=luma3(ch);
            float iL=luma3(ci), jL=luma3(cj), kL=luma3(ck), lL=luma3(cl), nL=luma3(cn), oL=luma3(co);
            float wS=(1.0-pp.x)*(1.0-pp.y), wT=pp.x*(1.0-pp.y), wU=(1.0-pp.x)*pp.y, wV=pp.x*pp.y;
            float3 acc = easuSet(wS, bL,eL,fL,gL,jL) + easuSet(wT, cL,fL,gL,hL,kL)
                       + easuSet(wU, fL,iL,jL,kL,nL) + easuSet(wV, gL,jL,kL,lL,oL);
            float2 dir = acc.xy; float len = acc.z;
            float dirRsq = dir.x*dir.x + dir.y*dir.y;
            bool zro = dirRsq < 3.05e-5;
            float dirR = zro ? 1.0 : inversesqrt(dirRsq);
            dir.x = zro ? 1.0 : dir.x;
            dir *= dirR;
            len = len * 0.5; len *= len;
            float stretch = (dir.x*dir.x + dir.y*dir.y) * rcpg(max(abs(dir.x), abs(dir.y)));
            float2 len2 = float2(1.0 + (stretch - 1.0)*len, 1.0 - 0.5*len);
            float lob = 0.5 + ((1.0/4.0 - 0.04) - 0.5)*len;
            float clp = rcpg(lob);
            float4 a = float4(0.0);
            a += tap(float2( 0.0,-1.0) - pp, dir, len2, lob, clp, cb_);
            a += tap(float2( 1.0,-1.0) - pp, dir, len2, lob, clp, cc_);
            a += tap(float2(-1.0, 1.0) - pp, dir, len2, lob, clp, ci);
            a += tap(float2( 0.0, 1.0) - pp, dir, len2, lob, clp, cj);
            a += tap(float2( 0.0, 0.0) - pp, dir, len2, lob, clp, cf);
            a += tap(float2(-1.0, 0.0) - pp, dir, len2, lob, clp, ce);
            a += tap(float2( 1.0, 1.0) - pp, dir, len2, lob, clp, ck);
            a += tap(float2( 2.0, 1.0) - pp, dir, len2, lob, clp, cl);
            a += tap(float2( 2.0, 0.0) - pp, dir, len2, lob, clp, ch);
            a += tap(float2( 1.0, 0.0) - pp, dir, len2, lob, clp, cg);
            a += tap(float2( 1.0, 2.0) - pp, dir, len2, lob, clp, co);
            a += tap(float2( 0.0, 2.0) - pp, dir, len2, lob, clp, cn);
            float3 col = a.rgb / a.w;
            float3 mn = min(min(cf,cg), min(cj,ck));
            float3 mx = max(max(cf,cg), max(cj,ck));
            col = clamp(col, mn, mx);
            return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
        }
        """;

    // Pass 2 — RCAS (final → display). Reads EASU's 2× output; OUT_SIZE = display rect, src_SIZE = EASU
    // size, so sp maps each display pixel into EASU-texel space and the ±1 ring taps are one EASU texel.
    // Per-channel min/max solve for the largest non-clipping sharpen lobe (AMD FsrRcas), scalar lobe =
    // max over channels, then the luma-based noise limiter scales it down where the neighbourhood is flat.
    private const string RCAS = """
        uniform shader src;
        uniform float2 src_SIZE;
        uniform float2 OUT_SIZE;
        float rcpg(float a){ return 1.0 / max(a, 1e-6); }
        float luma3(float3 c){ return 0.5*c.r + c.g + 0.5*c.b; }
        float mx3(float a, float b, float c){ return max(a, max(b, c)); }
        float mn3(float a, float b, float c){ return min(a, min(b, c)); }
        half4 main(float2 c){
            float2 sp = (c / OUT_SIZE) * src_SIZE;
            float3 e = sample(src, sp).rgb;
            float3 b = sample(src, sp + float2( 0.0,-1.0)).rgb;
            float3 d = sample(src, sp + float2(-1.0, 0.0)).rgb;
            float3 f = sample(src, sp + float2( 1.0, 0.0)).rgb;
            float3 h = sample(src, sp + float2( 0.0, 1.0)).rgb;
            float3 mn = min(min(b,d), min(f,h));
            float3 mx = max(max(b,d), max(f,h));
            float3 hitMin = mn / max(4.0*mx, float3(1e-6));
            float3 hitMax = (float3(1.0) - mx) / min(4.0*mn - 4.0, float3(-1e-6));
            float3 lobeRGB = max(-hitMin, hitMax);
            float lobeS = max(-(0.25 - 1.0/16.0), min(mx3(lobeRGB.r,lobeRGB.g,lobeRGB.b), 0.0));
            float lobe = lobeS * exp2(-clamp(0.1, 0.0, 2.0));    // SHARPNESS 0.1 stops (nearer RCAS max = sharper)
            float bl=luma3(b), dl=luma3(d), el=luma3(e), fl=luma3(f), hl=luma3(h);
            float nz = 0.25*(bl+dl+fl+hl) - el;
            float rng = mx3(mx3(bl,dl,el),fl,hl) - mn3(mn3(bl,dl,el),fl,hl);
            nz = clamp(abs(nz) * rcpg(rng), 0.0, 1.0);
            nz = -0.5*nz + 1.0;
            lobe *= nz;
            float rcpL = 1.0 / (4.0*lobe + 1.0);
            float3 outc = (lobe*(b+d+f+h) + e) * rcpL;
            return half4(half3(clamp(outc, 0.0, 1.0)), 1.0);
        }
        """;

    private static GpuPass[] Build()
    {
        var passes = new[]
        {
            new GpuPass { Sksl = EASU, Scale = 2f, F16 = true,  Inputs = new[] { ("src", -1) } },   // EASU ← original (2×)
            new GpuPass { Sksl = RCAS, Scale = 2f, F16 = false, Inputs = new[] { ("src", 0) } },     // RCAS → display (sharpen)
        };
        foreach (var gp in passes) gp.Effect = SkiaSharp.SKRuntimeEffect.Create(gp.Sksl, out gp.Error);
        return passes;
    }
}
