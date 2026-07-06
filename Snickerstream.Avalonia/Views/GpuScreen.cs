using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SnickerstreamV2.Imaging;

namespace SnickerstreamV2.Views;

/// <summary>
/// A stream-screen renderer that upscales the frame on the <b>GPU</b> via SkSL runtime effects, drawn onto
/// Avalonia's own Skia canvas through <see cref="ISkiaSharpApiLeaseFeature"/>. The shader samples the small
/// native frame and is evaluated at the final on-screen resolution — arbitrary-scale upscale, no fixed
/// buffer, no DPI tricks; the control stays native-sized so the layout is untouched.
///
/// <para>When there's no GPU context (software Skia — rare: some VMs/RDP), it falls back to the CPU
/// <see cref="Upscaler"/> so the filter still applies.</para>
/// </summary>
public sealed class GpuScreen : Control
{
    private byte[]? _px;          // owned BGRA copy of the current native frame
    private int _w, _h;
    private Size _defaultSize = new(240, 400);

    /// <summary>Which filter's shader to run.</summary>
    public UpscaleFilter Filter { get; set; } = UpscaleFilter.Sharp;

    /// <summary>xBR colour-distinction threshold. Lower = crisper (clean sources); higher tolerates JPEG
    /// block noise (set higher for lossy compression modes so artifacts don't spawn phantom edges).</summary>
    public float XbrEq { get; set; } = 0.40f;

    /// <summary>A post effect (e.g. CRT) applied instead of the upscaler. GPU only.</summary>
    public EffectFilter PostEffect { get; set; } = EffectFilter.None;

    /// <summary>Strength of <see cref="PostEffect"/>, 0 (off/flat) .. 1 (full). GPU only.</summary>
    public float EffectIntensity { get; set; } = 1.0f;

    /// <summary>Was the last render backed by a GPU context (vs software Skia)?</summary>
    public static volatile bool LastRenderWasGpu;

    /// <summary>Fires once, after the first render, with whether we're on the GPU.</summary>
    public event Action<bool>? FirstRender;
    private bool _firstRenderDone;

    public void SetDefaultSize(double w, double h) => _defaultSize = new Size(w, h);

    /// <summary>Copies a native (un-upscaled) frame in for the shader to sample. Call on the UI thread.</summary>
    public void SetFrame(Bitmap bmp)
    {
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height, stride = w * 4;
        int len = checked(stride * h);
        if (_px == null || _px.Length != len) _px = new byte[len];
        var gch = GCHandle.Alloc(_px, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, h), gch.AddrOfPinnedObject(), len, stride); }
        finally { gch.Free(); }
        bool sizeChanged = w != _w || h != _h;
        _w = w; _h = h;
        if (sizeChanged) InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
        => _w > 0 && _h > 0 ? new Size(_w, _h) : _defaultSize;

    public override void Render(DrawingContext context)
    {
        if (_px == null || _w == 0 || _h == 0) return;
        context.Custom(new Op(new Rect(Bounds.Size), _px, _w, _h, Filter, PostEffect, XbrEq, EffectIntensity, this));
    }

    private void OnFirstRender(bool gpu)
    {
        if (_firstRenderDone) return;
        _firstRenderDone = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FirstRender?.Invoke(gpu));
    }

    /// <summary>Fires once with a one-line status of the multi-pass chain (diagnostic).</summary>
    public event Action<string>? Diag;
    private bool _diagDone;
    internal void FireDiag(string s)
    {
        if (_diagDone) return;
        _diagDone = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Diag?.Invoke(s));
    }
    private static string Trunc(string? s) => string.IsNullOrEmpty(s) ? "?" : (s.Length > 60 ? s[..60] : s);

    // ===================== shader registry =====================

    private static readonly Dictionary<UpscaleFilter, SKRuntimeEffect?> _effects = new();
    private static readonly object _effLock = new();

    private static SKRuntimeEffect? EffectFor(UpscaleFilter f)
    {
        lock (_effLock)
        {
            if (_effects.TryGetValue(f, out var cached)) return cached;
            var sksl = Shaders.TryGetValue(f, out var s) ? s : null;
            SKRuntimeEffect? eff = sksl == null ? null : SKRuntimeEffect.Create(sksl, out _);
            _effects[f] = eff;
            return eff;
        }
    }

    private sealed class Op : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly byte[] _px;
        private readonly int _w, _h;
        private readonly UpscaleFilter _filter;
        private readonly EffectFilter _effect;
        private readonly float _eq;
        private readonly float _intensity;
        private readonly GpuScreen _owner;

        public Op(Rect bounds, byte[] px, int w, int h, UpscaleFilter filter, EffectFilter effect, float eq, float intensity, GpuScreen owner)
        { _bounds = bounds; _px = px; _w = w; _h = h; _filter = filter; _effect = effect; _eq = eq; _intensity = intensity; _owner = owner; }

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            bool gpu = lease.GrContext != null;
            LastRenderWasGpu = gpu;
            _owner.OnFirstRender(gpu);

            var rect = SKRect.Create((float)_bounds.Width, (float)_bounds.Height);

            // A post effect (CRT) takes precedence over the upscaler for now (applied standalone).
            var passes = _effect != EffectFilter.None ? EffectPassesFor(_effect) : MultiPassFor(_filter);
            if (passes != null && gpu && lease.GrContext != null)
            {
                RenderMultiPass(lease.GrContext, canvas, rect, passes, _intensity);
                return;
            }

            if (!gpu)
            {
                DrawCpuFallback(canvas, rect);   // software Skia: use the CPU upscaler instead of a slow shader
                return;
            }

            var effect = EffectFor(_filter);
            var info = new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul);
            var gch = GCHandle.Alloc(_px, GCHandleType.Pinned);
            try
            {
                using var img = SKImage.FromPixels(info, gch.AddrOfPinnedObject(), info.RowBytes);
                using var srcShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.High };
                if (effect != null)
                {
                    var children = new SKRuntimeEffectChildren(effect) { ["src"] = srcShader };
                    var uniforms = new SKRuntimeEffectUniforms(effect);
                    if (_filter is UpscaleFilter.Xbr or UpscaleFilter.SuperXbr)
                    {
                        uniforms["EQ"] = _eq;
                        uniforms["LV2"] = 0.3f;
                        // xBR = crisp (noblend); Super-xBR = anti-aliased diagonals (blend) — a real visible difference.
                        uniforms["AA"] = _filter == UpscaleFilter.SuperXbr ? 0.5f : 0.0f;
                    }
                    using var shader = effect.ToShader(true, uniforms, children);
                    paint.Shader = shader;
                }
                else paint.Shader = srcShader;
                canvas.DrawRect(rect, paint);
            }
            finally { gch.Free(); }
        }

        private static GpuPass[]? MultiPassFor(UpscaleFilter f) => f switch
        {
            UpscaleFilter.Anime4KCnn => Anime4KCnn.PassesFor("Anime4K_Upscale_CNN_x2_S.glsl"),
            UpscaleFilter.Anime4KCnnM => Anime4KCnn.PassesFor("Anime4K_Upscale_CNN_x2_M.glsl"),
            UpscaleFilter.Anime4KCnnL => Anime4KCnn.PassesFor("Anime4K_Upscale_CNN_x2_L.glsl"),
            UpscaleFilter.Anime4KCnnVL => Anime4KCnn.PassesFor("Anime4K_Upscale_CNN_x2_VL.glsl"),
            UpscaleFilter.ScaleFx => ScaleFx.Passes,
            _ => null,
        };

        private static GpuPass[]? EffectPassesFor(EffectFilter e) => e switch
        {
            EffectFilter.Crt or EffectFilter.CrtDot or EffectFilter.CrtCurved => CrtFilters.PassesFor(e),
            _ => null,
        };

        // Chain SkSL passes through intermediate GPU surfaces (feature maps), final pass draws to the screen.
        private void RenderMultiPass(GRContext gr, SKCanvas canvas, SKRect rect, GpuPass[] passes, float intensity)
        {
            var info0 = new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul);
            var gch = GCHandle.Alloc(_px, GCHandleType.Pinned);
            var srcImg = SKImage.FromPixels(info0, gch.AddrOfPinnedObject(), info0.RowBytes);
            var outImgs = new SKImage?[passes.Length];
            var surfaces = new List<SKSurface>();
            var childShaders = new List<SKShader>();
            bool premulUsed = false;
            try
            {
                for (int p = 0; p < passes.Length; p++)
                {
                    var pass = passes[p];
                    if (pass.Effect == null) { _owner.FireDiag($"compile-null p{p}: {Trunc(pass.Error)}"); DrawSourcePassthrough(canvas, srcImg, rect); return; }
                    int ow = (int)MathF.Round(_w * pass.Scale), oh = (int)MathF.Round(_h * pass.Scale);
                    bool last = p == passes.Length - 1;

                    var children = new SKRuntimeEffectChildren(pass.Effect);
                    var uniforms = new SKRuntimeEffectUniforms(pass.Effect);
                    TrySetFloat(uniforms, pass.Effect, "INTENSITY", intensity);
                    foreach (var (child, from) in pass.Inputs)
                    {
                        var im = from < 0 ? srcImg : outImgs[from]!;
                        var sh = im.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                        childShaders.Add(sh);
                        children[child] = sh;
                        TrySetSize(uniforms, pass.Effect, child + "_SIZE", im.Width, im.Height);
                    }
                    // The final pass draws the native rect (evaluated at display res) — its coord range is the
                    // rect, not the N× surface; the upscale factor is baked into the shader. Intermediates run
                    // at their surface size.
                    if (last) TrySetSize(uniforms, pass.Effect, "OUT_SIZE", (float)rect.Width, (float)rect.Height);
                    else TrySetSize(uniforms, pass.Effect, "OUT_SIZE", ow, oh);

                    using var shader = pass.Effect.ToShader(false, uniforms, children);
                    // Final pass composites onto the app canvas (SrcOver, opaque frame). Intermediate feature
                    // maps are written raw (Src) into UNPREMUL surfaces — their 4th channel is a conv value,
                    // not alpha, so premultiplication would corrupt it.
                    using var paint = new SKPaint { Shader = shader, IsAntialias = false, BlendMode = last ? SKBlendMode.SrcOver : SKBlendMode.Src };

                    if (last)
                    {
                        canvas.DrawRect(rect, paint);
                        _ = premulUsed;   // (surfaces are premul here; passes bias alpha to survive it)
                    }
                    else
                    {
                        var ct = pass.F16 ? SKColorType.RgbaF16 : SKColorType.Bgra8888;
                        var surf = SKSurface.Create(gr, false, new SKImageInfo(ow, oh, ct, SKAlphaType.Unpremul));
                        if (surf == null) { premulUsed = true; surf = SKSurface.Create(gr, false, new SKImageInfo(ow, oh, ct, SKAlphaType.Premul)); }
                        if (surf == null) { _owner.FireDiag($"surf-null p{p} {ct}"); DrawSourcePassthrough(canvas, srcImg, rect); return; }
                        surf.Canvas.DrawRect(SKRect.Create(ow, oh), paint);
                        outImgs[p] = surf.Snapshot();
                        surfaces.Add(surf);
                    }
                }
            }
            finally
            {
                foreach (var s in childShaders) s.Dispose();
                foreach (var im in outImgs) im?.Dispose();
                foreach (var s in surfaces) s.Dispose();
                srcImg.Dispose();
                gch.Free();
            }
        }

        private static void TrySetSize(SKRuntimeEffectUniforms u, SKRuntimeEffect e, string name, float a, float b)
        {
            try { u[name] = new[] { a, b }; } catch { /* uniform not declared in this pass */ }
        }

        private static void TrySetFloat(SKRuntimeEffectUniforms u, SKRuntimeEffect e, string name, float v)
        {
            try { u[name] = v; } catch { /* uniform not declared in this pass */ }
        }

        private static void DrawSourcePassthrough(SKCanvas canvas, SKImage src, SKRect rect)
        {
            using var sh = src.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = sh };
            canvas.DrawRect(rect, paint);
        }

        // Software backend: run the pure-managed CPU upscaler and blit the result (Option A as fallback).
        private void DrawCpuFallback(SKCanvas canvas, SKRect rect)
        {
            var buf = Upscaler.Apply(_filter, _px, _w, _h, out int ow, out int oh, out _);
            var info = new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul);
            var gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                using var img = SKImage.FromPixels(info, gch.AddrOfPinnedObject(), info.RowBytes);
                using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Medium };
                canvas.DrawImage(img, new SKRect(0, 0, ow, oh), rect, paint);
            }
            finally { gch.Free(); }
        }
    }

    // ===================== SkSL shaders (scale-correct: sample source texel centres, weight by frac) =====================

    private static readonly Dictionary<UpscaleFilter, string> Shaders = new()
    {
        // Sharp — clamped unsharp (RCAS-style); can't overshoot past the local neighbourhood → no specks.
        // NB: SkiaSharp 2.88.9's SkSL is the older dialect — sample(src, coord), not src.eval(coord).
        [UpscaleFilter.Sharp] = """
            uniform shader src;
            half4 main(float2 c) {
                float2 tc = floor(c) + 0.5;                 // nearest — crisp hard pixels
                half4 e = sample(src, tc);
                half4 l = sample(src, tc + float2(-1.0, 0.0));
                half4 r = sample(src, tc + float2( 1.0, 0.0));
                half4 u = sample(src, tc + float2( 0.0,-1.0));
                half4 d = sample(src, tc + float2( 0.0, 1.0));
                half k = 0.5;
                half3 s = e.rgb * (1.0 + 4.0*k) - k * (l.rgb + r.rgb + u.rgb + d.rgb);
                half3 mn = min(e.rgb, min(min(l.rgb, r.rgb), min(u.rgb, d.rgb)));
                half3 mx = max(e.rgb, max(max(l.rgb, r.rgb), max(u.rgb, d.rgb)));
                return half4(clamp(s, mn, mx), e.a);
            }
            """,

        // xBR — Hyllian's xBR-lv2 (the real one; arbitrary-scale via fract). The star for 2D pixel art.
        [UpscaleFilter.Xbr] = XbrLv2,

        // Super-xBR — same xBR-lv2 engine, tuned smoother (higher LV2 coefficient via uniform).
        [UpscaleFilter.SuperXbr] = XbrLv2,

        // FSR — Lanczos-2 upscale (EASU stand-in) + light clamped RCAS sharpen. The smooth option.
        [UpscaleFilter.Fsr] = LanczosShader(sharpen: 0.20f),

        // Anime4K — Lanczos-2 base + a much stronger clamped sharpen — punchy, clearly harder than FSR.
        [UpscaleFilter.Anime4K] = LanczosShader(sharpen: 0.75f),

        // MMPX (McGuire & Gagiu, MIT) — deterministic rule-based 2x pixel-art scaler; never invents a
        // colour not in the source neighbourhood (unlike xBR's blends). Arbitrary-scale via the same
        // continuous-draw trick: pick the J/K/L/M quadrant from the fractional coordinate.
        [UpscaleFilter.Mmpx] = Mmpx,
    };

    // Format a float as a SkSL literal that always has a decimal point (so it types as float, not int).
    private static string F(float x) => x.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    // Hyllian's xBR-lv2 (NOBLEND), ported verbatim to the old SkSL dialect (sample(), no bool fns, own step).
    // Arbitrary-scale via fract(coord); coord is in source-texel units (the drawn rect == source size).
    // EQ/LV2 come in as uniforms. Validated to compile against the SkiaSharp 2.88.9 native.
    private const string XbrLv2 = """
        uniform shader src;
        uniform float EQ;
        uniform float LV2;
        uniform float AA;
        half cd(half3 a, half3 b){ return dot(abs(a-b), half3(0.2627, 0.6780, 0.0593)); }
        half ne(half3 a, half3 b){ half s = dot(abs(a-b), half3(1.0,1.0,1.0)); return s > 0.0001 ? 1.0 : 0.0; }
        half4 dist4(half3 a0,half3 a1,half3 a2,half3 a3, half3 b0,half3 b1,half3 b2,half3 b3){
            return half4(cd(a0,b0),cd(a1,b1),cd(a2,b2),cd(a3,b3));
        }
        half4 diff4(half3 a0,half3 a1,half3 a2,half3 a3, half3 b0,half3 b1,half3 b2,half3 b3){
            return half4(ne(a0,b0),ne(a1,b1),ne(a2,b2),ne(a3,b3));
        }
        half4 stp(half4 e, half4 x){ return clamp(sign(x - e) + half4(1.0), 0.0, 1.0); }
        half stp1(half e, half x){ return clamp(sign(x - e) + 1.0, 0.0, 1.0); }
        half4 LT(half4 a, half4 b){ return half4(1.0) - stp(b, a); }
        half4 LTE(half4 a, half4 b){ return stp(a, b); }
        half3 T(float2 tc, float dx, float dy){ return sample(src, tc + float2(dx,dy)).rgb; }
        half4 main(float2 c){
            float2 tc = floor(c) + 0.5;
            half2 fp = half2(fract(c));
            half lv2cf = half(LV2) + 2.0;
            half3 A1=T(tc,-1.0,-2.0), B1=T(tc,0.0,-2.0), C1=T(tc,1.0,-2.0);
            half3 A =T(tc,-1.0,-1.0), B =T(tc,0.0,-1.0), C =T(tc,1.0,-1.0);
            half3 D =T(tc,-1.0, 0.0), E =T(tc,0.0, 0.0), F =T(tc,1.0, 0.0);
            half3 G =T(tc,-1.0, 1.0), H =T(tc,0.0, 1.0), I =T(tc,1.0, 1.0);
            half3 G5=T(tc,-1.0, 2.0), H5=T(tc,0.0, 2.0), I5=T(tc,1.0, 2.0);
            half3 A0=T(tc,-2.0,-1.0), D0=T(tc,-2.0,0.0), G0=T(tc,-2.0,1.0);
            half3 C4=T(tc, 2.0,-1.0), F4=T(tc, 2.0,0.0), I4=T(tc, 2.0,1.0);
            half4 Ao=half4(1.0,-1.0,-1.0,1.0), Bo=half4(1.0,1.0,-1.0,-1.0), Co=half4(1.5,0.5,-0.5,0.5);
            half4 Ax=half4(1.0,-1.0,-1.0,1.0), Bx=half4(0.5,2.0,-0.5,-2.0), Cx=half4(1.0,1.0,-0.5,0.0);
            half4 Ay=half4(1.0,-1.0,-1.0,1.0), By=half4(2.0,0.5,-2.0,-0.5), Cy=half4(2.0,0.0,-1.0,0.5);
            half4 Ci=half4(0.25,0.25,0.25,0.25);
            half4 fx   = Ao*fp.y + Bo*fp.x;
            half4 fx_l = Ax*fp.y + Bx*fp.x;
            half4 fx_u = Ay*fp.y + By*fp.x;
            half4 irlv0 = diff4(E,E,E,E, F,B,D,H) * diff4(E,E,E,E, H,F,B,D);
            half4 eqEQ = half4(half(EQ));
            half4 neq_fb = half4(1.0) - stp(dist4(F,B,D,H, B,D,H,F), eqEQ);
            half4 neq_fc = half4(1.0) - stp(dist4(F,B,D,H, C,A,G,I), eqEQ);
            half4 neq_hd = half4(1.0) - stp(dist4(H,F,B,D, D,H,F,B), eqEQ);
            half4 neq_hg = half4(1.0) - stp(dist4(H,F,B,D, G,I,C,A), eqEQ);
            half4 eq_ei  = stp(dist4(E,E,E,E, I,C,A,G), eqEQ);
            half4 neq_ff4= half4(1.0) - stp(dist4(F,B,D,H, F4,B1,D0,H5), eqEQ);
            half4 neq_fi4= half4(1.0) - stp(dist4(F,B,D,H, I4,C1,A0,G5), eqEQ);
            half4 neq_hh5= half4(1.0) - stp(dist4(H,F,B,D, H5,F4,B1,D0), eqEQ);
            half4 neq_hi5= half4(1.0) - stp(dist4(H,F,B,D, I5,C4,A1,G0), eqEQ);
            half4 eq_eg  = stp(dist4(E,E,E,E, G,I,C,A), eqEQ);
            half4 eq_ec  = stp(dist4(E,E,E,E, C,A,G,I), eqEQ);
            half4 irlv1 = clamp(irlv0 * ( neq_fb*neq_fc + neq_hd*neq_hg + eq_ei*(neq_ff4*neq_fi4 + neq_hh5*neq_hi5) + eq_eg + eq_ec ), 0.0, 1.0);
            half4 irlv2l = diff4(E,E,E,E, G,I,C,A) * diff4(D,H,F,B, G,I,C,A);
            half4 irlv2u = diff4(E,E,E,E, C,A,G,I) * diff4(B,D,H,F, C,A,G,I);
            half aaf = half(AA);
            half4 fx45i, fx45, fx30, fx60;
            if (aaf <= 0.0) {
                fx45i = LT(Co + Ci, fx); fx45 = LT(Co, fx); fx30 = LT(Cx, fx_l); fx60 = LT(Cy, fx_u);
            } else {
                half4 delta  = half4(aaf);
                half4 deltaL = half4(0.5,1.0,0.5,1.0) * aaf;
                half4 deltaU = deltaL.yxwz;
                fx45i = clamp(0.5 + (fx   - Co - Ci) / delta,  0.0, 1.0);
                fx45  = clamp(0.5 + (fx   - Co     ) / delta,  0.0, 1.0);
                fx30  = clamp(0.5 + (fx_l - Cx     ) / deltaL, 0.0, 1.0);
                fx60  = clamp(0.5 + (fx_u - Cy     ) / deltaU, 0.0, 1.0);
            }
            half4 wd1 = dist4(E,E,E,E, C,A,G,I) + dist4(E,E,E,E, G,I,C,A) + dist4(I,C,A,G, H5,F4,B1,D0) + dist4(I,C,A,G, F4,B1,D0,H5) + 4.0*dist4(H,F,B,D, F,B,D,H);
            half4 wd2 = dist4(H,F,B,D, D,H,F,B) + dist4(H,F,B,D, I5,C4,A1,G0) + dist4(F,B,D,H, I4,C1,A0,G5) + dist4(F,B,D,H, B,D,H,F) + 4.0*dist4(E,E,E,E, I,C,A,G);
            half4 d_fg = dist4(F,B,D,H, G,I,C,A);
            half4 d_hc = dist4(H,F,B,D, C,A,G,I);
            half4 edri  = LTE(wd1, wd2) * irlv0;
            half4 edr   = LT(wd1, wd2) * irlv1 * (half4(1.0) - edri.yzwx * edri.wxyz);
            half4 edr_l = LTE(lv2cf*d_fg, d_hc) * irlv2l * edr * ((half4(1.0)-edri.yzwx) * eq_ec);
            half4 edr_u = LTE(lv2cf*d_hc, d_fg) * irlv2u * edr * ((half4(1.0)-edri.wxyz) * eq_eg);
            fx45i = edri  * fx45i;
            fx45  = edr   * fx45;
            fx30  = edr_l * fx30;
            fx60  = edr_u * fx60;
            half4 px = LTE(dist4(E,E,E,E, F,B,D,H), dist4(E,E,E,E, H,F,B,D));
            half4 maximos = max(max(fx30,fx60), max(fx45,fx45i));
            half3 res1 = mix(E, mix(H, F, px.x), maximos.x);
            half3 res2 = mix(E, mix(B, D, px.z), maximos.z);
            half3 res1a = mix(res1, res2, stp1(cd(E,res1), cd(E,res2)));
            res1 = mix(E, mix(F, B, px.y), maximos.y);
            res2 = mix(E, mix(D, H, px.w), maximos.w);
            half3 res1b = mix(res1, res2, stp1(cd(E,res1), cd(E,res2)));
            half3 res = mix(res1a, res1b, stp1(cd(E,res1a), cd(E,res1b)));
            return half4(res, 1.0);
        }
        """;

    // MMPX (Morgan McGuire & Mara Gagiu, MIT) — ported verbatim from the reference mmpx_scale2x() (C),
    // https://github.com/ITotalJustice/mmpx (mirrors casual-effects.com/research/McGuire2021PixelArt).
    // Booleans become 0.0/1.0 floats (old SkSL dialect: user functions can't return bool). Colour equality
    // uses a small epsilon (source texels are exact 8-bit samples, so this only guards float rounding).
    // Each output quadrant (J top-left, K top-right, L bottom-left, M bottom-right) picks one of the
    // neighbourhood's own colours — never blends — so ambiguous/JPEG-noisy regions just degrade to nearest.
    private const string Mmpx = """
        uniform shader src;
        float3 T(float2 c, float dx, float dy){ return sample(src, c + float2(dx,dy)).rgb; }
        float luma(float3 c){ return c.r + c.g + c.b; }
        float ceq(float3 a, float3 b){ return dot(abs(a-b), float3(1.0)) < 0.01 ? 1.0 : 0.0; }
        float cneq(float3 a, float3 b){ return 1.0 - ceq(a,b); }
        float alleq2(float3 b, float3 a0, float3 a1){ return ceq(b,a0)*ceq(b,a1); }
        float alleq3(float3 b, float3 a0, float3 a1, float3 a2){ return ceq(b,a0)*ceq(b,a1)*ceq(b,a2); }
        float alleq4(float3 b, float3 a0, float3 a1, float3 a2, float3 a3){ return ceq(b,a0)*ceq(b,a1)*ceq(b,a2)*ceq(b,a3); }
        float anyeq3(float3 b, float3 a0, float3 a1, float3 a2){ return max(ceq(b,a0), max(ceq(b,a1), ceq(b,a2))); }
        float noneeq2(float3 b, float3 a0, float3 a1){ return cneq(b,a0)*cneq(b,a1); }
        float noneeq4(float3 b, float3 a0, float3 a1, float3 a2, float3 a3){ return cneq(b,a0)*cneq(b,a1)*cneq(b,a2)*cneq(b,a3); }
        half4 main(float2 c){
            float2 tc = floor(c) + 0.5;
            float2 fp = fract(c);
            float3 A=T(tc,-1,-1), B=T(tc,0,-1), Cc=T(tc,1,-1);
            float3 D=T(tc,-1,0),  E=T(tc,0,0),  F=T(tc,1,0);
            float3 G=T(tc,-1,1),  H=T(tc,0,1),  I=T(tc,1,1);
            float3 Q=T(tc,-2,0),  R=T(tc,2,0);
            float3 P=T(tc,0,-2),  S=T(tc,0,2);
            float3 A0=T(tc,-2,-1), C4=T(tc,2,-1);
            float3 G0=T(tc,-2,1),  I4=T(tc,2,1);
            float3 A1=T(tc,-1,-2), C1=T(tc,1,-2);
            float3 G5=T(tc,-1,2),  I5=T(tc,1,2);
            float3 Q3=T(tc,-3,0), R3=T(tc,3,0);
            float3 P3=T(tc,0,-3), S3=T(tc,0,3);
            float3 J=E, K=E, L=E, M=E;
            float anyDiff = max(max(max(cneq(A,E),cneq(B,E)),max(cneq(Cc,E),cneq(D,E))), max(max(cneq(F,E),cneq(G,E)),max(cneq(H,E),cneq(I,E))));
            if (anyDiff > 0.5) {
                float Bl=luma(B), Dl=luma(D), El=luma(E), Fl=luma(F), Hl=luma(H);
                if (ceq(D,B)>0.5 && cneq(D,H)>0.5 && cneq(D,F)>0.5 && (El>=Dl || ceq(E,A)>0.5) && anyeq3(E,A,Cc,G)>0.5 && (El<Dl || cneq(A,D)>0.5 || cneq(E,P)>0.5 || cneq(E,Q)>0.5)) J=D;
                if (ceq(B,F)>0.5 && cneq(B,D)>0.5 && cneq(B,H)>0.5 && (El>=Bl || ceq(E,Cc)>0.5) && anyeq3(E,A,Cc,I)>0.5 && (El<Bl || cneq(Cc,B)>0.5 || cneq(E,P)>0.5 || cneq(E,R)>0.5)) K=B;
                if (ceq(H,D)>0.5 && cneq(H,F)>0.5 && cneq(H,B)>0.5 && (El>=Hl || ceq(E,G)>0.5) && anyeq3(E,A,G,I)>0.5 && (El<Hl || cneq(G,H)>0.5 || cneq(E,S)>0.5 || cneq(E,Q)>0.5)) L=H;
                if (ceq(F,H)>0.5 && cneq(F,B)>0.5 && cneq(F,D)>0.5 && (El>=Fl || ceq(E,I)>0.5) && anyeq3(E,Cc,G,I)>0.5 && (El<Fl || cneq(I,H)>0.5 || cneq(E,R)>0.5 || cneq(E,S)>0.5)) M=F;
                if (cneq(E,F)>0.5 && alleq4(E,Cc,I,D,Q)>0.5 && alleq2(F,B,H)>0.5 && cneq(F,R3)>0.5) { K=F; M=F; }
                if (cneq(E,D)>0.5 && alleq4(E,A,G,F,R)>0.5 && alleq2(D,B,H)>0.5 && cneq(D,Q3)>0.5) { J=D; L=D; }
                if (cneq(E,H)>0.5 && alleq4(E,G,I,B,P)>0.5 && alleq2(H,D,F)>0.5 && cneq(H,S3)>0.5) { L=H; M=H; }
                if (cneq(E,B)>0.5 && alleq4(E,A,Cc,H,S)>0.5 && alleq2(B,D,F)>0.5 && cneq(B,P3)>0.5) { J=B; K=B; }
                if (Bl<El && alleq4(E,G,H,I,S)>0.5 && noneeq4(E,A,D,Cc,F)>0.5) { J=B; K=B; }
                if (Hl<El && alleq4(E,A,B,Cc,P)>0.5 && noneeq4(E,D,G,I,F)>0.5) { L=H; M=H; }
                if (Fl<El && alleq4(E,A,D,G,Q)>0.5 && noneeq4(E,B,Cc,I,H)>0.5) { K=F; M=F; }
                if (Dl<El && alleq4(E,Cc,F,I,R)>0.5 && noneeq4(E,B,A,G,H)>0.5) { J=D; L=D; }
                if (cneq(H,B)>0.5) {
                    if (cneq(H,A)>0.5 && cneq(H,E)>0.5 && cneq(H,Cc)>0.5) {
                        if (alleq3(H,G,F,R)>0.5 && noneeq2(H,D,C4)>0.5) L=M;
                        if (alleq3(H,I,D,Q)>0.5 && noneeq2(H,F,A0)>0.5) M=L;
                    }
                    if (cneq(B,I)>0.5 && cneq(B,G)>0.5 && cneq(B,E)>0.5) {
                        if (alleq3(B,A,F,R)>0.5 && noneeq2(B,D,I4)>0.5) J=K;
                        if (alleq3(B,Cc,D,Q)>0.5 && noneeq2(B,F,G0)>0.5) K=J;
                    }
                }
                if (cneq(F,D)>0.5) {
                    if (cneq(D,I)>0.5 && cneq(D,E)>0.5 && cneq(D,Cc)>0.5) {
                        if (alleq3(D,A,H,S)>0.5 && noneeq2(D,B,I5)>0.5) J=L;
                        if (alleq3(D,G,B,P)>0.5 && noneeq2(D,H,C1)>0.5) L=J;
                    }
                    if (cneq(F,E)>0.5 && cneq(F,A)>0.5 && cneq(F,G)>0.5) {
                        if (alleq3(F,Cc,H,S)>0.5 && noneeq2(F,B,G5)>0.5) K=M;
                        if (alleq3(F,I,B,P)>0.5 && noneeq2(F,H,A1)>0.5) M=K;
                    }
                }
            }
            float3 outc = fp.x<0.5 ? (fp.y<0.5?J:L) : (fp.y<0.5?K:M);
            return half4(half3(outc), 1.0);
        }
        """;

    private static string LanczosShader(float sharpen) => $$"""
        uniform shader src;
        half3 N(float2 p) { return sample(src, floor(p) + 0.5).rgb; }
        float wl(float x) {
            x = abs(x);
            if (x < 0.0001) return 1.0;
            if (x >= 2.0) return 0.0;
            float px = 3.14159265 * x;
            return (sin(px)/px) * (sin(px*0.5)/(px*0.5));
        }
        half4 main(float2 c) {
            float2 uv = c - 0.5;
            float2 b = floor(uv);
            float2 f = uv - b;
            half3 acc = half3(0.0);
            half ws = 0.0;
            for (int j = -1; j <= 2; j++) {
                for (int i = -1; i <= 2; i++) {
                    float2 tp = b + float2(float(i), float(j)) + 0.5;
                    half w = half(wl(float(i) - f.x) * wl(float(j) - f.y));
                    acc += N(tp) * w;
                    ws += w;
                }
            }
            half3 col = acc / ws;
            half k = {{F(sharpen)}};
            float2 t = floor(c) + 0.5;
            half3 e = N(t), l = N(t + float2(-1.0,0.0)), r = N(t + float2(1.0,0.0));
            half3 u = N(t + float2(0.0,-1.0)), d = N(t + float2(0.0,1.0));
            half3 s = col * (1.0 + 4.0*k) - k * (l + r + u + d);
            half3 mn = min(e, min(min(l,r), min(u,d)));
            half3 mx = max(e, max(max(l,r), max(u,d)));
            return half4(clamp(s, mn, mx), 1.0);
        }
        """;
}
