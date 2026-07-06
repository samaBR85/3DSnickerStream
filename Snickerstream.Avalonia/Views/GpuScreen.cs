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
        context.Custom(new Op(new Rect(Bounds.Size), _px, _w, _h, Filter, this));
    }

    private void OnFirstRender(bool gpu)
    {
        if (_firstRenderDone) return;
        _firstRenderDone = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FirstRender?.Invoke(gpu));
    }

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
        private readonly GpuScreen _owner;

        public Op(Rect bounds, byte[] px, int w, int h, UpscaleFilter filter, GpuScreen owner)
        { _bounds = bounds; _px = px; _w = w; _h = h; _filter = filter; _owner = owner; }

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
                    using var shader = effect.ToShader(true, uniforms, children);
                    paint.Shader = shader;
                }
                else paint.Shader = srcShader;
                canvas.DrawRect(rect, paint);
            }
            finally { gch.Free(); }
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
                half4 e = sample(src, c);
                half4 l = sample(src, c + float2(-1.0, 0.0));
                half4 r = sample(src, c + float2( 1.0, 0.0));
                half4 u = sample(src, c + float2( 0.0,-1.0));
                half4 d = sample(src, c + float2( 0.0, 1.0));
                half k = 0.5;
                half3 s = e.rgb * (1.0 + 4.0*k) - k * (l.rgb + r.rgb + u.rgb + d.rgb);
                half3 mn = min(e.rgb, min(min(l.rgb, r.rgb), min(u.rgb, d.rgb)));
                half3 mx = max(e.rgb, max(max(l.rgb, r.rgb), max(u.rgb, d.rgb)));
                return half4(clamp(s, mn, mx), e.a);
            }
            """,

        // xBR — EPX/Scale2x-style edge-directed doubling; crisp diagonals on 2D/pixel art.
        [UpscaleFilter.Xbr] = EpxShader(sharpen: 0.0f),

        // Super-xBR — EPX base + a light clamped crisp pass so it reads sharper than plain xBR.
        [UpscaleFilter.SuperXbr] = EpxShader(sharpen: 0.35f),

        // FSR — Lanczos-2 upscale (EASU stand-in) + clamped RCAS sharpen. Smooth + sharp, good for 3D.
        [UpscaleFilter.Fsr] = LanczosShader(sharpen: 0.30f),

        // Anime4K — Lanczos-2 base + a strong clamped sharpen for punchy cel-shaded lines.
        [UpscaleFilter.Anime4K] = LanczosShader(sharpen: 0.55f),
    };

    // Format a float as a SkSL literal that always has a decimal point (so it types as float, not int).
    private static string F(float x) => x.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    private static string EpxShader(float sharpen) => $$"""
        uniform shader src;
        half3 P(float2 p) { return sample(src, floor(p) + 0.5).rgb; }
        half eqf(half3 a, half3 b) { half3 d = abs(a - b); half s = d.r + d.g + d.b; return s < 0.18 ? 1.0 : 0.0; }
        half4 main(float2 c) {
            float2 t = floor(c);
            half3 E = P(t + 0.5);
            half3 A = P(t + float2(0.5,-0.5));
            half3 B = P(t + float2(1.5, 0.5));
            half3 C = P(t + float2(-0.5,0.5));
            half3 D = P(t + float2(0.5, 1.5));
            float2 f = c - t;
            half3 o = E;
            if (f.x < 0.5 && f.y < 0.5)       { if (eqf(C,A) > 0.5 && eqf(C,D) < 0.5 && eqf(A,B) < 0.5) o = A; }
            else if (f.x >= 0.5 && f.y < 0.5) { if (eqf(A,B) > 0.5 && eqf(A,C) < 0.5 && eqf(B,D) < 0.5) o = B; }
            else if (f.x < 0.5 && f.y >= 0.5) { if (eqf(D,C) > 0.5 && eqf(D,B) < 0.5 && eqf(C,A) < 0.5) o = C; }
            else                              { if (eqf(B,D) > 0.5 && eqf(B,A) < 0.5 && eqf(D,C) < 0.5) o = D; }
            half k = {{F(sharpen)}};
            if (k > 0.0) {
                half3 s = o * (1.0 + 4.0*k) - k * (A + B + C + D);
                half3 mn = min(E, min(min(A,B), min(C,D)));
                half3 mx = max(E, max(max(A,B), max(C,D)));
                o = clamp(s, mn, mx);
            }
            return half4(o, 1.0);
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
