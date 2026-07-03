using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    private bool _clean;                                      // clean/hide mode (flush screens, no chrome)
    private Bitmap? _ambientBmp;                              // owned scaled copy behind the screens (ambient glow)

    private long _received, _rendered;
    private DispatcherTimer? _fpsTimer, _toastTimer;
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

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _toastTimer.Tick += (_, _) => { Toast.IsVisible = false; _toastTimer!.Stop(); };
    }

    /// <summary>Flash a brief action message in the bottom-left of the stage.</summary>
    private void ShowToast(string msg)
    {
        ToastText.Text = msg;
        Toast.IsVisible = true;
        _toastTimer!.Stop();
        _toastTimer!.Start();
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

        BtnScreenshot.Click += (_, _) => TakeScreenshot(toClipboard: false);
        BtnAdjust.Click += (_, _) => SetAdjustPanels(!_adjustOn);
        BtnAmbient.Click += (_, _) => { S.AmbientGlow = !S.AmbientGlow; ApplyAmbientVisibility(); };
        BtnPinFps.Click += (_, _) => { S.ShowFpsOverlay = !S.ShowFpsOverlay; UpdatePinFps(); UpdateFpsOverlay(); };
        BtnFullscreen.Click += (_, _) => _owner.ToggleFullscreen();
        BtnHide.Click += (_, _) => EnterClean();
        BtnKeyboard.Click += (_, _) => new ShortcutsWindow().ShowDialog(_owner);
        BtnDisconnect.Click += (_, _) => Disconnect();

        ApplyAmbientVisibility();
        UpdatePinFps();
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
        BtnAdjust.Foreground = on ? Brush("BrandEndBrush") : Brush("TextPrimaryBrush");
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
        // Clean mode renders the screens flush (no rounded frame / border / shadow).
        var framed = new Border { Child = img };
        if (!_clean) framed.Classes.Add("screen");
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

    // ===================== Ambient glow / FPS overlay =====================

    // Resolve app-level brushes without walking the visual tree — this runs during the ctor,
    // before the view is attached, so this.FindResource would throw.
    private static IBrush Brush(string key)
        => Application.Current!.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Transparent;
    private static void Tint(TemplatedControl b, bool on, IBrush onBrush, IBrush offBrush)
        => b.Foreground = on ? onBrush : offBrush;

    private void ApplyAmbientVisibility()
    {
        AmbientImage.IsVisible = S.AmbientGlow;
        Vignette.IsVisible = S.AmbientGlow;
        Tint(BtnAmbient, S.AmbientGlow, Brush("BrandEndBrush"), Brush("TextSecondaryBrush"));
        if (!S.AmbientGlow)
        {
            AmbientImage.Source = null;
            _ambientBmp?.Dispose(); _ambientBmp = null;
        }
    }

    /// <summary>Refresh the blurred backdrop from the latest frame (owned, downscaled copy).</summary>
    private void UpdateAmbient()
    {
        if (!S.AmbientGlow || _disposed) return;
        var src = _lastTopBmp ?? _lastBottomBmp;
        if (src == null) return;
        try
        {
            int pw = src.PixelSize.Width, ph = src.PixelSize.Height;
            int w = 200, h = Math.Max(1, (int)Math.Round(200.0 * ph / pw));
            var scaled = src.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.LowQuality);
            AmbientImage.Source = scaled;
            _ambientBmp?.Dispose();
            _ambientBmp = scaled;
        }
        catch { /* transient decode/scale race — skip this tick */ }
    }

    private void UpdatePinFps()
        => Tint(BtnPinFps, S.ShowFpsOverlay, Brush("BrandEndBrush"), Brush("TextPrimaryBrush"));

    /// <summary>The pinned FPS overlay is only shown in clean mode (the bar shows fps otherwise).</summary>
    private void UpdateFpsOverlay()
        => FpsOverlay.IsVisible = _clean && S.ShowFpsOverlay;

    // ===================== Clean / hide mode =====================

    private void EnterClean()
    {
        ControlBar.IsVisible = false;
        AdjustTop.IsVisible = false;
        AdjustBottom.IsVisible = false;
        ScreensHost.Margin = new Thickness(0);
        _clean = true;
        BuildLayout();                       // rebuild screens flush (no corners/shadow/border)
        UpdateFpsOverlay();
        var (gw, gh) = GroupSize();
        _owner.EnterCleanMode(gw, gh);
    }

    private void ExitClean()
    {
        _owner.ExitCleanMode();
        ControlBar.IsVisible = true;
        ScreensHost.Margin = new Thickness(28);
        _clean = false;
        UpdateFpsOverlay();
        BuildLayout();                       // restore framed screens
        if (_adjustOn) { AdjustTop.IsVisible = true; AdjustBottom.IsVisible = true; }
    }

    /// <summary>Design size (w,h) of the whole on-screen group, for clean-mode window fitting.</summary>
    private (double w, double h) GroupSize()
    {
        double ts = Math.Clamp(S.TopScale, 0.5, 2.0);
        double bs = Math.Clamp(S.BottomScale, 0.5, 2.0);
        double gap = Math.Clamp(S.GapV, 0, 300);
        var (tw, th) = ScreenDisplaySize(_lastTopBmp, ts, 400.0, 240.0);
        var (bw, bh) = ScreenDisplaySize(_lastBottomBmp, bs, 320.0, 240.0);

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod) layout = ScreenLayout.TopOnly;

        return layout switch
        {
            ScreenLayout.Stacked => (Math.Max(tw, bw), th + gap + bh),
            ScreenLayout.SideBySide => (tw + gap + bw, Math.Max(th, bh)),
            ScreenLayout.BottomOnly => (bw, bh),
            _ => (tw, th),
        };
    }

    /// <summary>Upright display size of a screen given its (sideways) bitmap, scale and the current rotation.</summary>
    private (double w, double h) ScreenDisplaySize(Bitmap? bmp, double scale, double defW, double defH)
    {
        double pw = bmp?.PixelSize.Width ?? defH, ph = bmp?.PixelSize.Height ?? defW;   // native is sideways
        int angle = (270 + S.Rotation) % 360;
        (double dw, double dh) = (angle == 90 || angle == 270) ? (ph, pw) : (pw, ph);
        return (dw * scale, dh * scale);
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
        FpsOverlayText.Text = FpsBadge.Text;
        FpsDot.Fill = ren > 0 ? Brushes.LimeGreen : new SolidColorBrush(Color.Parse("#888888"));
        StatusText.Text = ren > 0 ? "Streaming" : "Waiting for frames…";
        UpdateAmbient();
    }

    // ===================== Teardown =====================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        var action = MatchAction(key, e.KeyModifiers);

        // In clean mode, Esc or the hide-UI key restores the interface.
        if (_clean && (action == ShortcutAction.ToggleUi || key == Key.Escape))
        {
            e.Handled = true;
            ExitClean();
            return;
        }

        if (action == null) return;
        e.Handled = true;
        switch (action)
        {
            case ShortcutAction.Disconnect: Disconnect(); break;
            case ShortcutAction.CycleLayout: Cycle(CmbLayout); break;
            case ShortcutAction.CycleFilter: Cycle(CmbFilter); break;
            case ShortcutAction.RotateScreen: Cycle(CmbRot); break;
            case ShortcutAction.ToggleFullscreen: _owner.ToggleFullscreen(); break;
            case ShortcutAction.IncreaseQuality: AdjustQuality(+5); break;
            case ShortcutAction.DecreaseQuality: AdjustQuality(-5); break;
            case ShortcutAction.SwapPriorityScreen: SwapPriority(); break;
            case ShortcutAction.ToggleUi: EnterClean(); break;
            case ShortcutAction.Screenshot: TakeScreenshot(toClipboard: false); break;
            case ShortcutAction.ScreenshotToClipboard: TakeScreenshot(toClipboard: true); break;
            case ShortcutAction.CopyText: ShowToast("OCR: a later phase"); break;
        }
    }

    private ShortcutAction? MatchAction(Key key, KeyModifiers mods)
    {
        // Prefer bindings WITH modifiers so "Shift+S" wins over "S" when Shift is held.
        foreach (var withMods in new[] { true, false })
            foreach (var (name, binding) in S.KeyBindings)
            {
                bool bindingHasMods = binding.Contains('+');
                if (bindingHasMods != withMods) continue;
                if (ShortcutBinding.Matches(binding, key, mods) && Enum.TryParse<ShortcutAction>(name, out var a))
                    return a;
            }
        return null;
    }

    private static void Cycle(ComboBox cmb)
        => cmb.SelectedIndex = (cmb.SelectedIndex + 1) % Math.Max(1, cmb.ItemCount);

    private void AdjustQuality(int delta)
    {
        if (_protocol == Protocol.NTR)
        {
            S.ImageQuality = Math.Clamp(S.ImageQuality + delta, 10, 100);
            _client.SetQuality(S.ImageQuality);
            ShowToast($"Quality {S.ImageQuality}");
        }
        else
        {
            S.HzQuality = Math.Clamp(S.HzQuality + delta, 1, 100);
            _client.SetQuality(S.HzQuality);
            ShowToast($"Quality {S.HzQuality}");
        }
    }

    private void SwapPriority()
    {
        if (_client is NTRClient ntr)
        {
            ntr.SwapPriorityScreen();
            S.PriorityScreenTop = !S.PriorityScreenTop;
            ShowToast($"Priority: {(S.PriorityScreenTop ? "Top" : "Bottom")}");
        }
    }

    // ===================== Screenshot =====================

    /// <summary>Composes the current frames (flush, rotated, scaled, with gap) off-tree into a bitmap.</summary>
    private RenderTargetBitmap? RenderComposite()
    {
        var top = _lastTopBmp;
        var bottom = _lastBottomBmp;
        if (top == null && bottom == null) return null;

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod) layout = ScreenLayout.TopOnly;
        double ts = Math.Clamp(S.TopScale, 0.5, 2.0);
        double bs = Math.Clamp(S.BottomScale, 0.5, 2.0);
        double gap = Math.Clamp(S.GapV, 0, 300);

        Control Shot(Bitmap? bmp, double scale)
        {
            var img = new Image { Stretch = Stretch.None, Source = bmp };
            var xform = new TransformGroup();
            xform.Children.Add(new RotateTransform((270 + S.Rotation) % 360));
            if (scale != 1.0) xform.Children.Add(new ScaleTransform(scale, scale));
            return new LayoutTransformControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,   // center the narrower screen (matches the live view)
                VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = xform,
                Child = img
            };
        }
        Control Stack(bool horizontal)
        {
            var sp = new StackPanel { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical, Spacing = gap };
            sp.Children.Add(Shot(top, ts));
            sp.Children.Add(Shot(bottom, bs));
            return sp;
        }

        Control group = layout switch
        {
            ScreenLayout.TopOnly => Shot(top, ts),
            ScreenLayout.BottomOnly => Shot(bottom, bs),
            ScreenLayout.SideBySide => Stack(horizontal: true),
            _ => Stack(horizontal: false),
        };

        group.Measure(Size.Infinity);
        var ds = group.DesiredSize;
        int w = (int)Math.Ceiling(ds.Width), h = (int)Math.Ceiling(ds.Height);
        if (w <= 0 || h <= 0) return null;
        group.Arrange(new Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(group);
        return rtb;
    }

    private void TakeScreenshot(bool toClipboard)
    {
        RenderTargetBitmap? rtb;
        try { rtb = RenderComposite(); }
        catch (Exception ex) { ShowToast($"Screenshot failed: {ex.Message}"); return; }
        if (rtb == null) { ShowToast("Screenshot: no frame to capture"); return; }

        using (rtb)
        {
            string saved;
            try
            {
                using var ms = new MemoryStream();
                rtb.Save(ms);
                saved = SavePng(ms.ToArray());
            }
            catch (Exception ex) { ShowToast($"Screenshot failed: {ex.Message}"); return; }

            if (toClipboard)
            {
                bool copied = TryCopyImageToClipboard(rtb);
                ShowToast(copied ? $"Copied to clipboard · saved: {saved}" : $"Saved: {saved}");
            }
            else ShowToast($"Saved: {saved}");
        }
    }

    private string SavePng(byte[] png)
    {
        var dir = string.IsNullOrWhiteSpace(S.ScreenshotFolder) ? AppSettings.DefaultScreenshotFolder : S.ScreenshotFolder;
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "3DSnickerStream");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"3DSnickerStream-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>Windows: put the image on the clipboard as CF_DIB (pasteable in Paint, Office, etc.).</summary>
    private static bool TryCopyImageToClipboard(RenderTargetBitmap rtb)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            int w = rtb.PixelSize.Width, h = rtb.PixelSize.Height, stride = w * 4;
            var px = new byte[checked(h * stride)];
            var gch = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { rtb.CopyPixels(new PixelRect(0, 0, w, h), gch.AddrOfPinnedObject(), px.Length, stride); }
            finally { gch.Free(); }
            return WindowsClipboard.TrySetDib(px, w, h);
        }
        catch { return false; }
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
        _toastTimer?.Stop();

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
        _ambientBmp?.Dispose(); _ambientBmp = null;
    }
}
