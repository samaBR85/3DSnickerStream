using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace SnickerstreamV2.Views;

/// <summary>
/// A stream-screen renderer that upscales the frame on the <b>GPU</b> via a SkSL runtime effect, drawn onto
/// Avalonia's own Skia canvas through <see cref="ISkiaSharpApiLeaseFeature"/>. The shader samples the small
/// native frame and is evaluated at the final on-screen resolution (arbitrary-scale upscale — no fixed 2×
/// buffer, no DPI tricks). Falls back to a plain draw when the frame isn't ready.
///
/// <para>Spike scope: one hardcoded clamped-sharpen shader + a GPU/CPU readout. Proves the whole lease →
/// SKRuntimeEffect → per-frame-upload → GPU-execution path end to end. Per-filter shaders come next.</para>
/// </summary>
public sealed class GpuScreen : Control
{
    private byte[]? _px;          // owned BGRA copy of the current native frame
    private int _w, _h;
    private Size _defaultSize = new(240, 400);

    /// <summary>Was the last render backed by a GPU context (vs software Skia)? Static for a quick readout.</summary>
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
        context.Custom(new Op(new Rect(Bounds.Size), _px, _w, _h, this));
    }

    private void OnFirstRender(bool gpu)
    {
        if (_firstRenderDone) return;
        _firstRenderDone = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FirstRender?.Invoke(gpu));
    }

    // Compiled once; the SkSL clamped-sharpen (RCAS-style, no overshoot) sampling the native src in px units.
    private static SKRuntimeEffect? _effect;
    private static bool _effectTried;
    private const string Sksl = """
        uniform shader src;
        half4 main(float2 c) {
            half4 e = src.eval(c);
            half4 l = src.eval(c + float2(-1.0, 0.0));
            half4 r = src.eval(c + float2( 1.0, 0.0));
            half4 u = src.eval(c + float2( 0.0,-1.0));
            half4 d = src.eval(c + float2( 0.0, 1.0));
            half k = 0.5;
            half3 sharp = e.rgb * (1.0 + 4.0 * k) - k * (l.rgb + r.rgb + u.rgb + d.rgb);
            half3 mn = min(e.rgb, min(min(l.rgb, r.rgb), min(u.rgb, d.rgb)));
            half3 mx = max(e.rgb, max(max(l.rgb, r.rgb), max(u.rgb, d.rgb)));
            return half4(clamp(sharp, mn, mx), e.a);
        }
        """;

    private static SKRuntimeEffect? BuildEffect()
    {
        if (_effectTried) return _effect;
        _effectTried = true;
        _effect = SKRuntimeEffect.Create(Sksl, out _);
        return _effect;
    }

    private sealed class Op : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly byte[] _px;
        private readonly int _w, _h;
        private readonly GpuScreen _owner;

        public Op(Rect bounds, byte[] px, int w, int h, GpuScreen owner)
        { _bounds = bounds; _px = px; _w = w; _h = h; _owner = owner; }

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

            var info = new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul);
            var gch = GCHandle.Alloc(_px, GCHandleType.Pinned);
            try
            {
                using var img = SKImage.FromPixels(info, gch.AddrOfPinnedObject(), info.RowBytes);
                using var srcShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                var effect = BuildEffect();
                var rect = SKRect.Create((float)_bounds.Width, (float)_bounds.Height);
                using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.High };
                if (effect != null)
                {
                    var children = new SKRuntimeEffectChildren(effect) { ["src"] = srcShader };
                    var uniforms = new SKRuntimeEffectUniforms(effect);
                    using var shader = effect.ToShader(true, uniforms, children);
                    paint.Shader = shader;
                }
                else
                {
                    paint.Shader = srcShader;
                }
                canvas.DrawRect(rect, paint);
            }
            finally { gch.Free(); }
        }
    }
}
