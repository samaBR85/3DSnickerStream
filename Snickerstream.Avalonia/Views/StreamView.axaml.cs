using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;
using Screen = SnickerstreamV2.Net.Screen;   // disambiguate from Avalonia.Platform.Screen

namespace SnickerstreamV2.Views;

public partial class StreamView : UserControl
{
    private readonly MainWindow _owner = null!;      // set by the real ctor; null in the designer ctor
    private readonly IStreamClient _client = null!;
    private readonly Protocol _protocol;
    private AppSettings S => App.Settings;

    private Image? _imgTop, _imgBottom;
    private Bitmap? _lastTopBmp, _lastBottomBmp;              // currently displayed (owned)
    private readonly object _gate = new();
    private Bitmap? _pendingTop, _pendingBottom;
    private bool _topQueued, _bottomQueued;

    private byte[]? _lastJpegTop, _lastJpegBottom;           // raw frames, to re-render on color change
    private long _lastTicksTop, _lastTicksBottom;            // frame-cap pacing
    private bool _adjustOn;                                   // per-screen color panels visible

    private long _received, _rendered;
    private DispatcherTimer? _fpsTimer;
    private bool _loaded, _disposed;

    private static readonly int[] FpsPresets = { 0, 60, 30, 24, 20, 15, 10 };

    public StreamView() => InitializeComponent();   // designer / runtime loader only

    public StreamView(MainWindow owner, IStreamClient client, Protocol protocol)
    {
        _owner = owner;
        _client = client;
        _protocol = protocol;
        InitializeComponent();

        InitControls();
        BuildLayout();

        _loaded = true;

        _client.FrameReady += OnFrameReady;
        _client.Failed += OnFailed;
        _owner.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += OnFpsTick;
        _fpsTimer.Start();
    }

    // ===================== Control init =====================

    private void InitControls()
    {
        CmbLayout.SelectedIndex = (int)S.Layout;
        CmbFilter.SelectedIndex = (int)S.Interpolation;
        CmbRot.SelectedIndex = S.Rotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };

        CmbMaxFps.Items.Clear();
        foreach (var v in FpsPresets)
            CmbMaxFps.Items.Add(new ComboBoxItem { Content = v == 0 ? "∞" : v.ToString() });
        int idx = Array.IndexOf(FpsPresets, S.MaxFps);
        if (idx < 0) { CmbMaxFps.Items.Add(new ComboBoxItem { Content = S.MaxFps.ToString() }); idx = CmbMaxFps.Items.Count - 1; }
        CmbMaxFps.SelectedIndex = idx;

        CmbZoom.SelectedIndex = S.ZoomPercent switch { 100 => 1, 150 => 2, 200 => 3, 300 => 4, _ => 0 };
        SldGap.Value = S.GapV;
        SldTopScale.Value = S.TopScale;
        SldBottomScale.Value = S.BottomScale;
        UpdateBarLabels();

        // Per-screen color adjustments
        SldTopBright.Value = S.TopBrightness; SldTopContrast.Value = S.TopContrast; SldTopSat.Value = S.TopSaturation; SldTopHi.Value = S.TopHighlights; SldTopShadows.Value = S.TopShadows;
        SldBotBright.Value = S.BottomBrightness; SldBotContrast.Value = S.BottomContrast; SldBotSat.Value = S.BottomSaturation; SldBotHi.Value = S.BottomHighlights; SldBotShadows.Value = S.BottomShadows;

        CmbLayout.SelectionChanged += (_, _) => { if (_loaded) { S.Layout = (ScreenLayout)CmbLayout.SelectedIndex; BuildLayout(); } };
        CmbFilter.SelectionChanged += (_, _) => { if (_loaded) { S.Interpolation = (Interpolation)CmbFilter.SelectedIndex; ApplyFilter(); } };
        CmbRot.SelectionChanged += (_, _) => { if (_loaded) { S.Rotation = CmbRot.SelectedIndex * 90; BuildLayout(); } };
        CmbMaxFps.SelectionChanged += (_, _) => { if (_loaded) ApplyMaxFpsSelection(); };
        CmbZoom.SelectionChanged += (_, _) => { if (_loaded) { S.ZoomPercent = CmbZoom.SelectedIndex switch { 1 => 100, 2 => 150, 3 => 200, 4 => 300, _ => 0 }; BuildLayout(); } };

        OnSlider(SldGap, v => { S.GapV = Math.Round(v); UpdateBarLabels(); BuildLayout(); });
        OnSlider(SldTopScale, v => { S.TopScale = Math.Round(v, 1); UpdateBarLabels(); BuildLayout(); });
        OnSlider(SldBottomScale, v => { S.BottomScale = Math.Round(v, 1); UpdateBarLabels(); BuildLayout(); });

        OnSlider(SldTopBright, v => { S.TopBrightness = Math.Round(v, 2); ReapplyColor(Screen.Top); });
        OnSlider(SldTopContrast, v => { S.TopContrast = Math.Round(v, 2); ReapplyColor(Screen.Top); });
        OnSlider(SldTopSat, v => { S.TopSaturation = Math.Round(v, 2); ReapplyColor(Screen.Top); });
        OnSlider(SldTopHi, v => { S.TopHighlights = Math.Round(v, 2); ReapplyColor(Screen.Top); });
        OnSlider(SldTopShadows, v => { S.TopShadows = Math.Round(v, 2); ReapplyColor(Screen.Top); });
        OnSlider(SldBotBright, v => { S.BottomBrightness = Math.Round(v, 2); ReapplyColor(Screen.Bottom); });
        OnSlider(SldBotContrast, v => { S.BottomContrast = Math.Round(v, 2); ReapplyColor(Screen.Bottom); });
        OnSlider(SldBotSat, v => { S.BottomSaturation = Math.Round(v, 2); ReapplyColor(Screen.Bottom); });
        OnSlider(SldBotHi, v => { S.BottomHighlights = Math.Round(v, 2); ReapplyColor(Screen.Bottom); });
        OnSlider(SldBotShadows, v => { S.BottomShadows = Math.Round(v, 2); ReapplyColor(Screen.Bottom); });

        BtnResetTop.Click += (_, _) => { SldTopBright.Value = 0; SldTopContrast.Value = 1; SldTopSat.Value = 1; SldTopHi.Value = 0; SldTopShadows.Value = 0; };
        BtnResetBottom.Click += (_, _) => { SldBotBright.Value = 0; SldBotContrast.Value = 1; SldBotSat.Value = 1; SldBotHi.Value = 0; SldBotShadows.Value = 0; };
        // Copy the other screen's adjustments (setting the sliders fires their handler → persist + re-render).
        BtnCopyBottom.Click += (_, _) => { SldTopBright.Value = SldBotBright.Value; SldTopContrast.Value = SldBotContrast.Value; SldTopSat.Value = SldBotSat.Value; SldTopHi.Value = SldBotHi.Value; SldTopShadows.Value = SldBotShadows.Value; };
        BtnCopyTop.Click += (_, _) => { SldBotBright.Value = SldTopBright.Value; SldBotContrast.Value = SldTopContrast.Value; SldBotSat.Value = SldTopSat.Value; SldBotHi.Value = SldTopHi.Value; SldBotShadows.Value = SldTopShadows.Value; };

        BtnAdjust.Click += (_, _) => SetAdjustPanels(!_adjustOn);
        BtnDisconnect.Click += (_, _) => Disconnect();
    }

    private void OnSlider(Slider s, Action<double> apply)
        => s.PropertyChanged += (_, e) => { if (_loaded && e.Property == RangeBase.ValueProperty) apply(s.Value); };

    private void ApplyMaxFpsSelection()
    {
        if (CmbMaxFps.SelectedItem is ComboBoxItem it)
        {
            var t = it.Content?.ToString() ?? "0";
            S.MaxFps = t == "∞" ? 0 : (int.TryParse(t, out var v) ? v : 0);
        }
    }

    private void UpdateBarLabels()
    {
        ValGap.Text = ((int)SldGap.Value).ToString();
        ValTopScale.Text = $"{SldTopScale.Value:0.0}×";
        ValBottomScale.Text = $"{SldBottomScale.Value:0.0}×";
    }

    private void SetAdjustPanels(bool on)
    {
        _adjustOn = on;
        AdjustTop.IsVisible = on;
        AdjustBottom.IsVisible = on;
        BtnAdjust.Foreground = on
            ? (IBrush)this.FindResource("BrandEndBrush")!
            : (IBrush)this.FindResource("TextPrimaryBrush")!;
    }

    // ===================== Layout =====================

    private void BuildLayout()
    {
        ScreensHost.Children.Clear();
        _imgTop = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };
        _imgBottom = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod) layout = ScreenLayout.TopOnly;   // HzMod streams top only

        double ts = Math.Clamp(S.TopScale, 0.5, 2.0);
        double bs = Math.Clamp(S.BottomScale, 0.5, 2.0);
        double gap = Math.Clamp(S.GapV, 0, 300);

        Control group = layout switch
        {
            ScreenLayout.TopOnly => MakeScreen(_imgTop, ts),
            ScreenLayout.BottomOnly => MakeScreen(_imgBottom, bs),
            ScreenLayout.SideBySide => Group(horizontal: true, ts, bs, gap),
            _ => Group(horizontal: false, ts, bs, gap),   // Stacked
        };

        if (S.ZoomPercent <= 0)
        {
            // Fit: scale the group uniformly to fill the stage.
            ScreensHost.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = group });
        }
        else
        {
            // Percent: render at native × zoom (100% = 1:1), centered; grow the window to fit.
            double z = S.ZoomPercent / 100.0;
            ScreensHost.Children.Add(new LayoutTransformControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(z, z),
                Child = group
            });
            Dispatcher.UIThread.Post(FitWindowToContent, DispatcherPriority.Loaded);
        }

        // reapply the current frames (owned bitmaps; do NOT dispose here)
        if (_lastTopBmp != null) _imgTop.Source = _lastTopBmp;
        if (_lastBottomBmp != null) _imgBottom.Source = _lastBottomBmp;
        ApplyFilter();
    }

    private Control Group(bool horizontal, double ts, double bs, double gap)
    {
        var sp = new StackPanel
        {
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = gap,   // screen-space gap; the Viewbox/zoom scales it with the screens
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(MakeScreen(_imgTop!, ts));
        sp.Children.Add(MakeScreen(_imgBottom!, bs));
        return sp;
    }

    // 270deg upright correction is baked in; user Rotation is an offset on top. Layout-transform, not pixels.
    // Per-screen scale is a uniform ScaleTransform composed with the rotation; the framed Border rotates too.
    private Control MakeScreen(Image img, double scale)
    {
        var framed = new Border { Classes = { "screen" }, Child = img };
        var xform = new TransformGroup();
        xform.Children.Add(new RotateTransform((270 + S.Rotation) % 360));
        if (scale != 1.0) xform.Children.Add(new ScaleTransform(scale, scale));
        return new LayoutTransformControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = xform,
            Child = framed
        };
    }

    /// <summary>In % zoom mode, grow the window so the zoomed screens fit ("adjust to larger screen").</summary>
    private void FitWindowToContent()
    {
        if (_disposed || S.ZoomPercent <= 0) return;
        if (ScreensHost.Children.Count == 0 || ScreensHost.Children[0] is not LayoutTransformControl lt
            || lt.Child is not Control group) return;
        double z = S.ZoomPercent / 100.0;
        group.Measure(Size.Infinity);
        var ds = group.DesiredSize;
        _owner.FitToContent(ds.Width * z, ds.Height * z, ControlBar.Bounds.Height);
    }

    private void ApplyFilter()
    {
        var mode = S.Interpolation switch
        {
            Interpolation.Sharp => BitmapInterpolationMode.None,
            Interpolation.Linear => BitmapInterpolationMode.MediumQuality,
            _ => BitmapInterpolationMode.HighQuality
        };
        if (_imgTop != null) RenderOptions.SetBitmapInterpolationMode(_imgTop, mode);
        if (_imgBottom != null) RenderOptions.SetBitmapInterpolationMode(_imgBottom, mode);
    }

    // ===================== Frame pipeline =====================

    private void OnFrameReady(StreamFrame frame)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _received);

        // Frame cap (per screen): drop before decoding if arriving faster than the cap.
        int cap = S.MaxFps;
        if (cap > 0)
        {
            long minTicks = Stopwatch.Frequency / cap;
            long now = Stopwatch.GetTimestamp();
            ref long last = ref (frame.Screen == Screen.Top ? ref _lastTicksTop : ref _lastTicksBottom);
            if (now - last < minTicks) return;
            last = now;
        }

        if (frame.Screen == Screen.Top) _lastJpegTop = frame.Jpeg; else _lastJpegBottom = frame.Jpeg;

        var bmp = DecodeAndAdjust(frame.Jpeg, ColorFor(frame.Screen));
        if (bmp == null) return;
        Interlocked.Increment(ref _rendered);
        Post(frame.Screen, bmp);
    }

    private (double b, double c, double s, double hl, double sh) ColorFor(Screen screen) => screen == Screen.Top
        ? (S.TopBrightness, S.TopContrast, S.TopSaturation, S.TopHighlights, S.TopShadows)
        : (S.BottomBrightness, S.BottomContrast, S.BottomSaturation, S.BottomHighlights, S.BottomShadows);

    /// <summary>Re-renders the last frame of a screen (used when its color sliders change).</summary>
    private void ReapplyColor(Screen screen)
    {
        var jpeg = screen == Screen.Top ? _lastJpegTop : _lastJpegBottom;
        if (jpeg == null) return;
        var bmp = DecodeAndAdjust(jpeg, ColorFor(screen));
        if (bmp != null) Post(screen, bmp);
    }

    private static Bitmap? DecodeAndAdjust(byte[] jpeg, (double b, double c, double s, double hl, double sh) col)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            var (b, c, s, hl, sh) = col;
            if (b == 0 && c == 1 && s == 1 && hl == 0 && sh == 0)
                return new Bitmap(ms);   // fast path: no pixel work

            using var decoded = new Bitmap(ms);
            var fmt = decoded.Format ?? PixelFormat.Bgra8888;
            bool isRgba = fmt == PixelFormat.Rgba8888;
            int w = decoded.PixelSize.Width, h = decoded.PixelSize.Height;
            int stride = w * 4;
            var px = new byte[checked(h * stride)];
            var gch = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { decoded.CopyPixels(new PixelRect(0, 0, w, h), gch.AddrOfPinnedObject(), px.Length, stride); }
            finally { gch.Free(); }

            ApplyColorAdjust(px, b, c, s, hl, sh, isRgba);

            var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), fmt, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                if (fb.RowBytes == stride)
                    Marshal.Copy(px, 0, fb.Address, px.Length);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(px, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
            }
            return wb;
        }
        catch { return null; }
    }

    private static void ApplyColorAdjust(byte[] px, double b, double c, double s, double hl, double sh, bool isRgba)
    {
        int ri = isRgba ? 0 : 2, bi = isRgba ? 2 : 0;   // R/B byte offsets within a BGRA/RGBA quad
        double br = b * 255.0;
        static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        const double kneeHi = 150.0;           // luma above which "highlights" kick in
        const double kneeLo = 105.0;           // luma below which "shadows" kick in
        for (int i = 0; i < px.Length; i += 4)
        {
            double R = px[i + ri], G = px[i + 1], B = px[i + bi];
            R = (R - 128) * c + 128 + br; G = (G - 128) * c + 128 + br; B = (B - 128) * c + 128 + br;
            double luma = 0.299 * R + 0.587 * G + 0.114 * B;
            R = luma + (R - luma) * s; G = luma + (G - luma) * s; B = luma + (B - luma) * s;

            // Highlights: darken only the bright end (proportional to how blown it is).
            if (hl > 0)
            {
                double lum = 0.299 * R + 0.587 * G + 0.114 * B;
                if (lum > kneeHi)
                {
                    double t = (lum - kneeHi) / (255.0 - kneeHi);   // 0..1 across the bright range
                    double f = 1.0 - hl * t;                        // up to (1-hl) at pure white
                    R *= f; G *= f; B *= f;
                }
            }

            // Shadows: lift only the dark end (additive so pure black rises, revealing detail).
            if (sh > 0)
            {
                double lum = 0.299 * R + 0.587 * G + 0.114 * B;
                if (lum < kneeLo)
                {
                    double t = (kneeLo - lum) / kneeLo;             // 0 at knee, 1 at black
                    double lift = sh * t * kneeLo;                 // up to sh*kneeLo added at black
                    R += lift; G += lift; B += lift;
                }
            }

            px[i + ri] = Clamp(R); px[i + 1] = Clamp(G); px[i + bi] = Clamp(B);   // alpha untouched
        }
    }

    private void Post(Screen screen, Bitmap bmp)
    {
        bool needPost;
        Bitmap? superseded;
        lock (_gate)
        {
            if (screen == Screen.Top)
            {
                superseded = _pendingTop; _pendingTop = bmp;
                needPost = !_topQueued; _topQueued = true;
            }
            else
            {
                superseded = _pendingBottom; _pendingBottom = bmp;
                needPost = !_bottomQueued; _bottomQueued = true;
            }
        }
        superseded?.Dispose();   // a queued frame that was never shown
        if (needPost) Dispatcher.UIThread.Post(() => Present(screen));
    }

    private void Present(Screen screen)
    {
        Bitmap? img;
        lock (_gate)
        {
            if (screen == Screen.Top) { img = _pendingTop; _pendingTop = null; _topQueued = false; }
            else { img = _pendingBottom; _pendingBottom = null; _bottomQueued = false; }
        }
        if (img == null) return;
        if (_disposed) { img.Dispose(); return; }

        if (screen == Screen.Top)
        {
            var prev = _lastTopBmp; _lastTopBmp = img;
            if (_imgTop != null) _imgTop.Source = img;
            prev?.Dispose();
        }
        else
        {
            var prev = _lastBottomBmp; _lastBottomBmp = img;
            if (_imgBottom != null) _imgBottom.Source = img;
            prev?.Dispose();
        }
    }

    private void OnFpsTick(object? sender, EventArgs e)
    {
        long rec = Interlocked.Exchange(ref _received, 0);
        long ren = Interlocked.Exchange(ref _rendered, 0);
        FpsBadge.Text = $"{ren} / {rec} fps";
        FpsDot.Fill = ren > 0 ? Brushes.LimeGreen : new SolidColorBrush(Color.Parse("#888888"));
        StatusText.Text = ren > 0 ? "Streaming" : "Waiting for frames…";
    }

    // ===================== Teardown =====================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Disconnect(); }
    }

    private void OnFailed(string msg) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        Teardown();
        _owner.ShowConnect();
    });

    private void Disconnect()
    {
        Teardown();
        _owner.ShowConnect();
    }

    private void Teardown()
    {
        if (_disposed) return;
        _disposed = true;

        _client.FrameReady -= OnFrameReady;
        _client.Failed -= OnFailed;
        _owner.RemoveHandler(KeyDownEvent, OnKeyDown);
        _fpsTimer?.Stop();

        try { _client.Stop(); } catch { }
        _client.Dispose();
        S.Save();

        lock (_gate)
        {
            _pendingTop?.Dispose(); _pendingBottom?.Dispose();
            _pendingTop = _pendingBottom = null;
        }
        _lastTopBmp?.Dispose(); _lastBottomBmp?.Dispose();
        _lastTopBmp = _lastBottomBmp = null;
    }
}
