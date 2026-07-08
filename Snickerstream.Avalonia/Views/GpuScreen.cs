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

    internal const SKColorType SrcColorType = SKColorType.Bgra8888;

    /// <summary>
    /// The heavy neighbourhood upscalers (xBR-lv2, Super-xBR, MMPX) are single-pass and use the
    /// continuous-scale draw trick, so they run the whole shader per output pixel at full display
    /// resolution (×retina). On the mac/linux GL backend that swamps the GPU — a few fps and the whole
    /// machine stutters. On non-Windows we cap them: render into a fixed native×<see cref="HeavyCapFactor"/>
    /// offscreen, then let the display scale that (bounded cost, independent of window/retina size).
    /// </summary>
    internal static readonly bool CapHeavy = !OperatingSystem.IsWindows();
    private const int HeavyCapFactor = 4;
    private static bool IsHeavy(UpscaleFilter f) =>
        f is UpscaleFilter.Xbr or UpscaleFilter.Mmpx;

    /// <summary>In-place R↔B swap of a 32bpp buffer — used to normalise every incoming frame to BGRA.</summary>
    private static void SwapRedBlue(byte[] px, int len)
    {
        for (int i = 0; i + 3 < len; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
    }

    /// <summary>Copies a native (un-upscaled) frame in for the shader to sample. Call on the UI thread.</summary>
    public void SetFrame(Bitmap bmp)
    {
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height, stride = w * 4;
        int len = checked(stride * h);
        if (_px == null || _px.Length != len) _px = new byte[len];
        var gch = GCHandle.Alloc(_px, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, h), gch.AddrOfPinnedObject(), len, stride); }
        finally { gch.Free(); }
        // Frames arrive BGRA (raw/lossless paths) or RGBA (the Skia-JPEG path on macOS). Normalise to BGRA
        // here so the whole GPU pipeline (shaders + MetalFX, all assuming SrcColorType) is correct in every
        // compression mode — no per-mode blue tint, no output colour-swap needed.
        if (bmp.Format == Avalonia.Platform.PixelFormat.Rgba8888) SwapRedBlue(_px!, len);
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

            // MetalFX (Apple ML upscaler): native path, not a Skia shader. On its own (no chained Effect) it
            // upscales the frame via MTLFXSpatialScaler and we draw the result. Falls through to plain source
            // if it fails this frame.
            if (_filter == UpscaleFilter.MetalFx && _effect == EffectFilter.None && MetalFxUpscaler.Available)
            {
                if (RenderMetalFx(canvas, rect)) return;
            }

            // Multi-pass upscalers (ScaleFX, Anime4K CNN) chain with an Effect (CRT variants): their own
            // final pass already outputs a genuine opaque colour image (alpha=1, no bias needed), so it
            // slots in as the effect's "src" input unchanged. Single-pass upscalers (Sharp/xBR/FSR/
            // Anime4K/MMPX) still can't chain — they rely on the display transform's continuous-scale
            // trick, incompatible with the effect's fixed-scale intermediate surfaces — so the effect
            // wins alone there, same as before.
            GpuPass[]? passes;
            if (_filter != UpscaleFilter.None && _effect != EffectFilter.None)
                passes = ChainedPassesFor(_filter, _effect) ?? EffectPassesFor(_effect);
            else if (_effect != EffectFilter.None)
                passes = EffectPassesFor(_effect);
            else
                passes = MultiPassFor(_filter);

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
            var info = new SKImageInfo(_w, _h, SrcColorType, SKAlphaType.Premul);
            var gch = GCHandle.Alloc(_px, GCHandleType.Pinned);
            SKShader? fxShader = null;
            try
            {
                using var img = SKImage.FromPixels(info, gch.AddrOfPinnedObject(), info.RowBytes);
                using var srcShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                if (effect != null)
                {
                    var children = new SKRuntimeEffectChildren(effect) { ["src"] = srcShader };
                    var uniforms = new SKRuntimeEffectUniforms(effect);
                    if (_filter is UpscaleFilter.Xbr)
                    {
                        uniforms["EQ"] = _eq;
                        uniforms["LV2"] = 0.3f;
                        uniforms["AA"] = 0.0f;   // crisp xBR-lv2 (noblend). Super-xBR is now its own real multi-pass filter.
                    }
                    fxShader = effect.ToShader(true, uniforms, children);
                }

                // Heavy neighbourhood filters: run the shader into a bounded native×N offscreen, then let the
                // display scale that — caps GPU cost regardless of window/retina size (see CapHeavy).
                if (fxShader != null && CapHeavy && IsHeavy(_filter) && lease.GrContext != null)
                {
                    RenderCapped(lease.GrContext, canvas, rect, fxShader);
                }
                else
                {
                    using var paint = new SKPaint
                    {
                        IsAntialias = false, FilterQuality = SKFilterQuality.High,
                        Shader = fxShader ?? srcShader,
                    };
                    canvas.DrawRect(rect, paint);
                }
            }
            finally { fxShader?.Dispose(); gch.Free(); }
        }

        // Clamped unsharp mask applied when compositing the MetalFX result to the display — restores the
        // definition the SSAA downscale softens. Sampling is remapped from display space (c) to the image's
        // pixel space (scl = image/dst ratio); neighbours step one display pixel. Compiled once.
        private static SKRuntimeEffect? _mfxSharpen;
        private static bool _mfxSharpenTried;
        private static SKRuntimeEffect? MfxSharpen
        {
            get
            {
                if (!_mfxSharpenTried)
                {
                    _mfxSharpenTried = true;
                    _mfxSharpen = SKRuntimeEffect.Create("""
                        uniform shader src;
                        uniform float2 dstOrigin;
                        uniform float2 scl;
                        uniform float K;
                        half4 main(float2 c){
                            float2 s = (c - dstOrigin) * scl;
                            float3 e = float3(sample(src, s).rgb);
                            float3 l = float3(sample(src, s+float2(-scl.x,0.0)).rgb);
                            float3 r = float3(sample(src, s+float2( scl.x,0.0)).rgb);
                            float3 u = float3(sample(src, s+float2(0.0,-scl.y)).rgb);
                            float3 d = float3(sample(src, s+float2(0.0, scl.y)).rgb);
                            float3 blur = (l+r+u+d)*0.25;
                            float3 mn = min(min(l,r), min(min(u,d), e));
                            float3 mx = max(max(l,r), max(max(u,d), e));
                            // Gate the sharpen by local contrast: flat areas (mostly sensor/compression noise)
                            // get almost none, real edges get the full amount — so a strong K sharpens edges
                            // without amplifying noise where there was none.
                            float contrast = dot(mx - mn, float3(0.333, 0.334, 0.333));
                            float gate = clamp((contrast - 0.04) * 9.0, 0.0, 1.0);
                            float3 sharp = e + (e - blur)*(K*gate);
                            return half4(half3(clamp(sharp, mn, mx)), 1.0);
                        }
                        """, out _);
                }
                return _mfxSharpen;
            }
        }

        /// <summary>Upscales the frame with MetalFX to ~display resolution and draws it. Returns false if
        /// MetalFX failed this frame (caller falls back to a plain draw).</summary>
        private bool RenderMetalFx(SKCanvas canvas, SKRect dstRect)
        {
            // Target ABOVE device resolution (supersample), then the final DrawImage downscales with a
            // high-quality filter — SSAA, which smooths the edges MetalFX leaves (a stronger anti-aliasing
            // feel). Bounded so the per-frame GPU→CPU readback stays reasonable.
            const float SuperSample = 1.35f;
            var m = canvas.TotalMatrix;
            float sc = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            if (!(sc > 0)) sc = 2f;
            sc *= SuperSample;
            const int MaxDim = 3200;
            int outW = (int)MathF.Round(_w * sc), outH = (int)MathF.Round(_h * sc);
            float k = Math.Max(outW, outH) > MaxDim ? MaxDim / (float)Math.Max(outW, outH) : 1f;
            outW = Math.Max(_w * 2, (int)(outW * k));
            outH = Math.Max(_h * 2, (int)(outH * k));

            var buf = MetalFxUpscaler.Upscale(_px, _w, _h, outW, outH);
            if (buf == null) return false;

            var info = new SKImageInfo(outW, outH, SrcColorType, SKAlphaType.Premul);
            var gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                // FromPixelCopy so MetalFxUpscaler's reused buffer is free the moment we return.
                using var img = SKImage.FromPixelCopy(info, gch.AddrOfPinnedObject(), info.RowBytes);
                var sharpen = MfxSharpen;
                if (sharpen != null)
                {
                    // Draw the supersampled result through a clamped unsharp mask: the linear downscale
                    // anti-aliases the edges (SSAA), the unsharp restores the crispness that MetalFX +
                    // downscale would otherwise wash out. Sampling/neighbour maths run in dstRect (display)
                    // space, mapped back into the image's pixel space per fragment.
                    using var srcShader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                    var children = new SKRuntimeEffectChildren(sharpen) { ["src"] = srcShader };
                    var uniforms = new SKRuntimeEffectUniforms(sharpen)
                    {
                        ["dstOrigin"] = new[] { dstRect.Left, dstRect.Top },
                        ["scl"] = new[] { outW / dstRect.Width, outH / dstRect.Height },
                        ["K"] = 1.3f,
                    };
                    using var shader = sharpen.ToShader(false, uniforms, children);
                    using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                    canvas.DrawRect(dstRect, paint);
                }
                else
                {
                    using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    canvas.DrawImage(img, new SKRect(0, 0, outW, outH), dstRect, paint);
                }
            }
            finally { gch.Free(); }
            return true;
        }

        /// <summary>Renders a single-pass effect shader into a fixed native×<see cref="HeavyCapFactor"/>
        /// GPU offscreen, then blits that (with the R↔B swizzle) to the display rect — bounding the heavy
        /// shader's cost to a fixed resolution instead of the full display size.</summary>
        private void RenderCapped(GRContext gr, SKCanvas canvas, SKRect dstRect, SKShader fxShader)
        {
            int ow = _w * HeavyCapFactor, oh = _h * HeavyCapFactor;
            using var surf = SKSurface.Create(gr, false, new SKImageInfo(ow, oh, SrcColorType, SKAlphaType.Premul));
            if (surf == null)   // couldn't allocate the offscreen — fall back to the direct (slow) draw
            {
                using var direct = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.High, Shader = fxShader };
                canvas.DrawRect(dstRect, direct);
                return;
            }
            using (var p = new SKPaint { Shader = fxShader, IsAntialias = false })
            {
                // Scale so a native-sized rect fills the N× surface, keeping the shader's coords in native
                // (source-texel) units — same continuous-scale maths, just at a bounded resolution.
                surf.Canvas.Save();
                surf.Canvas.Scale(HeavyCapFactor);
                surf.Canvas.DrawRect(SKRect.Create(_w, _h), p);
                surf.Canvas.Restore();
            }
            using var img = surf.Snapshot();
            using var blit = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
            canvas.DrawImage(img, new SKRect(0, 0, ow, oh), dstRect, blit);
        }

        private static GpuPass[]? MultiPassFor(UpscaleFilter f) => f switch
        {
            // Anime4K CNN: the full pipeline — Restore (denoise + line reconstruction, the "AI look") then a
            // real 4× (two chained 2× upscale networks, so the neural result lands near display resolution
            // instead of a soft 2×-then-bilinear).
            UpscaleFilter.Anime4KCnn => RestoreThenUpscale4x("Anime4K_Restore_CNN_S.glsl", "Anime4K_Upscale_CNN_x2_S.glsl"),
            UpscaleFilter.Anime4KCnnM => RestoreThenUpscale4x("Anime4K_Restore_CNN_M.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"),
            UpscaleFilter.Anime4KCnnL => RestoreThenUpscale4x("Anime4K_Restore_CNN_L.glsl", "Anime4K_Upscale_CNN_x2_L.glsl"),
            UpscaleFilter.Anime4KCnnVL => RestoreThenUpscale4x("Anime4K_Restore_CNN_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl"),
            UpscaleFilter.ScaleFx => ScaleFx.Passes,
            UpscaleFilter.SuperXbr => SuperXbr.Passes,
            _ => null,
        };

        private static readonly Dictionary<string, GpuPass[]> _cnnRestoreCache = new();

        /// <summary>Full Anime4K pipeline: the Restore network (denoise / line reconstruction, 1×) followed by
        /// the 4× upscale chain. The upscale's original-frame (<c>MAIN</c>/-1) references are remapped onto the
        /// restored output; its internal pass indices shift past the restore passes. Cached; reuses effects.</summary>
        private static GpuPass[] RestoreThenUpscale4x(string restoreRes, string upscaleRes)
        {
            string key = restoreRes + "|" + upscaleRes;
            lock (_chainLock)
            {
                if (_cnnRestoreCache.TryGetValue(key, out var cached)) return cached;
                var restore = Anime4KCnn.PassesFor(restoreRes);
                var up = Cnn4x(upscaleRes);
                if (restore.Length == 0) { _cnnRestoreCache[key] = up; return up; }   // restore parse failed → upscale only

                int r = restore.Length;
                var combined = new GpuPass[r + up.Length];
                Array.Copy(restore, combined, r);
                for (int i = 0; i < up.Length; i++)
                {
                    var src = up[i];
                    var remapped = new (string, int)[src.Inputs.Length];
                    for (int j = 0; j < src.Inputs.Length; j++)
                    {
                        var (child, from) = src.Inputs[j];
                        remapped[j] = (child, from < 0 ? r - 1 : from + r);   // original frame → restored output
                    }
                    combined[r + i] = new GpuPass { Sksl = src.Sksl, Scale = src.Scale, F16 = src.F16, Inputs = remapped, Effect = src.Effect, Error = src.Error };
                }
                _cnnRestoreCache[key] = combined;
                return combined;
            }
        }

        private static readonly Dictionary<string, GpuPass[]> _cnn4xCache = new();

        /// <summary>Reaches 4× by chaining two 2× Upscale_CNN networks: the second runs on the first's 2×
        /// output (remapping its <c>MAIN</c>/original-frame input to the first network's last pass), and its
        /// pass scales double — the conv layers sample by absolute texel offset, so each must render at its
        /// input's resolution (2× native for the second network, → 4× at its depth-to-space). Cached; reuses
        /// the already-compiled <see cref="SKRuntimeEffect"/> instances.</summary>
        private static GpuPass[] Cnn4x(string resource)
        {
            lock (_chainLock)
            {
                if (_cnn4xCache.TryGetValue(resource, out var cached)) return cached;
                var single = Anime4KCnn.PassesFor(resource);
                int n = single.Length;
                if (n == 0) { _cnn4xCache[resource] = single; return single; }

                var combined = new GpuPass[n * 2];
                Array.Copy(single, combined, n);
                for (int i = 0; i < n; i++)
                {
                    var src = single[i];
                    var remapped = new (string, int)[src.Inputs.Length];
                    for (int j = 0; j < src.Inputs.Length; j++)
                    {
                        var (child, from) = src.Inputs[j];
                        remapped[j] = (child, from < 0 ? n - 1 : from + n);   // MAIN → first network's 2× output
                    }
                    combined[n + i] = new GpuPass { Sksl = src.Sksl, Scale = src.Scale * 2f, F16 = src.F16, Inputs = remapped, Effect = src.Effect, Error = src.Error };
                }
                _cnn4xCache[resource] = combined;
                return combined;
            }
        }

        private static readonly Dictionary<(UpscaleFilter, EffectFilter), GpuPass[]> _chainCache = new();
        private static readonly object _chainLock = new();

        /// <summary>Concatenates an upscaler's passes with an effect's passes into one chain, remapping
        /// the effect's own pass-index references (its "-1" now means "the upscaler's last output", not
        /// the original frame; its in-chain indices shift by the upscaler's pass count). Returns null if
        /// either side has no multi-pass implementation (i.e. the upscaler is single-pass/GPU-continuous).
        /// Built once per (filter, effect) pair and cached — no shader recompilation, just re-wired
        /// GpuPass wrappers around the same compiled SKRuntimeEffect instances.</summary>
        private static GpuPass[]? ChainedPassesFor(UpscaleFilter f, EffectFilter e)
        {
            var upscale = MultiPassFor(f);
            var effect = EffectPassesFor(e);
            if (upscale == null || upscale.Length == 0 || effect == null || effect.Length == 0) return null;

            lock (_chainLock)
            {
                var key = (f, e);
                if (_chainCache.TryGetValue(key, out var cached)) return cached;

                int n = upscale.Length;
                var combined = new GpuPass[n + effect.Length];
                Array.Copy(upscale, combined, n);
                for (int i = 0; i < effect.Length; i++)
                {
                    var src = effect[i];
                    var remapped = new (string, int)[src.Inputs.Length];
                    for (int j = 0; j < src.Inputs.Length; j++)
                    {
                        var (child, from) = src.Inputs[j];
                        remapped[j] = (child, from < 0 ? n - 1 : from + n);
                    }
                    combined[n + i] = new GpuPass { Sksl = src.Sksl, Scale = src.Scale, F16 = src.F16, Inputs = remapped, Effect = src.Effect, Error = src.Error };
                }
                _chainCache[key] = combined;
                return combined;
            }
        }

        private static GpuPass[]? EffectPassesFor(EffectFilter e) => e switch
        {
            EffectFilter.Crt or EffectFilter.CrtDot or EffectFilter.CrtCurved => CrtFilters.PassesFor(e),
            _ => null,
        };

        // Chain SkSL passes through intermediate GPU surfaces (feature maps), final pass draws to the screen.
        private void RenderMultiPass(GRContext gr, SKCanvas canvas, SKRect rect, GpuPass[] passes, float intensity)
        {
            var info0 = new SKImageInfo(_w, _h, SrcColorType, SKAlphaType.Premul);
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
                        var ct = pass.F16 ? SKColorType.RgbaF16 : SrcColorType;
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
            var info = new SKImageInfo(ow, oh, SrcColorType, SKAlphaType.Premul);
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
        // (Super-xBR is a real multi-pass filter of its own — see SuperXbr.cs / MultiPassFor.)
        [UpscaleFilter.Xbr] = XbrLv2,

        // Lanczos — plain Lanczos-2 upscale + a light clamped sharpen. The smooth option. (Real edge-adaptive
        // upscaling is MetalFX, which is FSR2-based; this is just a simple smooth filter, honestly named.)
        [UpscaleFilter.Fsr] = LanczosShader(sharpen: 0.20f),

        // Lanczos+ — Lanczos-2 base + a much stronger clamped sharpen — punchier detail than plain Lanczos.
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
