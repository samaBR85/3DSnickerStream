using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snickerstream4Win.Models;
using Snickerstream4Win.Net;
using Snickerstream4Win.Services;

namespace Snickerstream4Win.Views;

public partial class StreamView : UserControl
{
    private readonly MainWindow _owner;
    private readonly IStreamClient _client;
    private readonly Protocol _protocol;
    private AppSettings S => App.Settings;

    private Image? _imgTop, _imgBottom;
    private Border? _wrapTop, _wrapBottom;

    private BitmapSource? _lastTop, _lastBottom;       // latest rotated frames (UI use)
    private BitmapSource? _pendingTop, _pendingBottom;
    private byte[]? _lastJpegTop, _lastJpegBottom;     // raw frames, to re-render on color change
    private bool _topQueued, _bottomQueued;
    private bool _adjustOn;                             // per-screen B/C/S panels visible
    private bool _clean;                                // clean mode (screens rendered flush)
    private readonly object _gate = new();

    private bool _ocrActive;                            // OCR marquee-selection in progress
    private System.Windows.Point _ocrStart;             // drag origin (overlay space)
    private BitmapSource? _ocrSnapTop, _ocrSnapBottom;  // frozen frames captured when selection began
    private BitmapSource? _ocrLastCrop;                 // last cropped region (for the Hex-mode re-run)

    private long _received, _rendered;                  // counted by the fps timer
    private int _staleSecs;                              // consecutive seconds with no frames
    private long _lastTicksTop, _lastTicksBottom;       // frame-cap pacing
    private DispatcherTimer? _fpsTimer, _ambientTimer;
    private bool _loaded, _disconnected;

    private static readonly int[] FpsPresets = { 0, 60, 30, 24, 20, 15, 10 };

    public StreamView(MainWindow owner, IStreamClient client, Protocol protocol)
    {
        _owner = owner;
        _client = client;
        _protocol = protocol;
        InitializeComponent();

        InitControls();
        BuildLayout();
        ApplyAmbientVisibility();

        _client.FrameReady += OnFrameReady;
        _client.Failed += OnFailed;

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += OnFpsTick;
        _fpsTimer.Start();

        _ambientTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ambientTimer.Tick += OnAmbientTick;
        _ambientTimer.Start();

        Loaded += (_, _) =>
        {
            _loaded = true;
            Focus();
            _owner.PreviewKeyDown += OnKeyDown;
        };
        Unloaded += (_, _) => _owner.PreviewKeyDown -= OnKeyDown;
    }

    // ===================== Control init =====================

    private void InitControls()
    {
        CmbLayout.SelectedIndex = (int)S.Layout;
        CmbFilter.SelectedIndex = (int)S.Interpolation;
        CmbRotation.SelectedIndex = S.Rotation switch { 0 => 0, 90 => 1, 180 => 2, _ => 3 };
        CmbZoom.SelectedIndex = S.ZoomPercent switch { 100 => 1, 150 => 2, 200 => 3, 300 => 4, _ => 0 };

        CmbMaxFps.Items.Clear();
        foreach (var v in FpsPresets)
            CmbMaxFps.Items.Add(new ComboBoxItem { Content = v == 0 ? "∞" : v.ToString() });
        int idx = Array.IndexOf(FpsPresets, S.MaxFps);
        if (idx < 0) { CmbMaxFps.Items.Add(new ComboBoxItem { Content = S.MaxFps.ToString() }); idx = CmbMaxFps.Items.Count - 1; }
        CmbMaxFps.SelectedIndex = idx;

        // Gap + per-screen scale + color adjustments
        SldGap.Value = S.GapV;
        SldTopScale.Value = S.TopScale;
        SldBottomScale.Value = S.BottomScale;
        SldTopBright.Value = S.TopBrightness; SldTopContrast.Value = S.TopContrast; SldTopSat.Value = S.TopSaturation; SldTopHi.Value = S.TopHighlights; SldTopShadows.Value = S.TopShadows;
        SldBotBright.Value = S.BottomBrightness; SldBotContrast.Value = S.BottomContrast; SldBotSat.Value = S.BottomSaturation; SldBotHi.Value = S.BottomHighlights; SldBotShadows.Value = S.BottomShadows;
        UpdateBarLabels();

        CmbLayout.SelectionChanged += (_, _) => { if (_loaded) { S.Layout = (ScreenLayout)CmbLayout.SelectedIndex; BuildLayout(); } };
        CmbFilter.SelectionChanged += (_, _) => { if (_loaded) { S.Interpolation = (Interpolation)CmbFilter.SelectedIndex; ApplyFilter(); } };
        CmbRotation.SelectionChanged += (_, _) => { if (_loaded) { S.Rotation = CmbRotation.SelectedIndex switch { 0 => 0, 1 => 90, 2 => 180, _ => 270 }; BuildLayout(); } };
        CmbMaxFps.SelectionChanged += (_, _) => { if (_loaded) ApplyMaxFpsSelection(); };
        CmbZoom.SelectionChanged += (_, _) => { if (_loaded) { S.ZoomPercent = CmbZoom.SelectedIndex switch { 1 => 100, 2 => 150, 3 => 200, 4 => 300, _ => 0 }; BuildLayout(); } };

        SldGap.ValueChanged += (_, _) => { if (_loaded) { S.GapV = Math.Round(SldGap.Value); UpdateBarLabels(); BuildLayout(); } };
        SldTopScale.ValueChanged += (_, _) => { if (_loaded) { S.TopScale = Math.Round(SldTopScale.Value, 1); UpdateBarLabels(); BuildLayout(); } };
        SldBottomScale.ValueChanged += (_, _) => { if (_loaded) { S.BottomScale = Math.Round(SldBottomScale.Value, 1); UpdateBarLabels(); BuildLayout(); } };

        SldTopBright.ValueChanged += (_, _) => { if (_loaded) { S.TopBrightness = Math.Round(SldTopBright.Value, 2); ReapplyColor(Screen.Top); } };
        SldTopContrast.ValueChanged += (_, _) => { if (_loaded) { S.TopContrast = Math.Round(SldTopContrast.Value, 2); ReapplyColor(Screen.Top); } };
        SldTopSat.ValueChanged += (_, _) => { if (_loaded) { S.TopSaturation = Math.Round(SldTopSat.Value, 2); ReapplyColor(Screen.Top); } };
        SldTopHi.ValueChanged += (_, _) => { if (_loaded) { S.TopHighlights = Math.Round(SldTopHi.Value, 2); ReapplyColor(Screen.Top); } };
        SldTopShadows.ValueChanged += (_, _) => { if (_loaded) { S.TopShadows = Math.Round(SldTopShadows.Value, 2); ReapplyColor(Screen.Top); } };
        SldBotBright.ValueChanged += (_, _) => { if (_loaded) { S.BottomBrightness = Math.Round(SldBotBright.Value, 2); ReapplyColor(Screen.Bottom); } };
        SldBotContrast.ValueChanged += (_, _) => { if (_loaded) { S.BottomContrast = Math.Round(SldBotContrast.Value, 2); ReapplyColor(Screen.Bottom); } };
        SldBotSat.ValueChanged += (_, _) => { if (_loaded) { S.BottomSaturation = Math.Round(SldBotSat.Value, 2); ReapplyColor(Screen.Bottom); } };
        SldBotHi.ValueChanged += (_, _) => { if (_loaded) { S.BottomHighlights = Math.Round(SldBotHi.Value, 2); ReapplyColor(Screen.Bottom); } };
        SldBotShadows.ValueChanged += (_, _) => { if (_loaded) { S.BottomShadows = Math.Round(SldBotShadows.Value, 2); ReapplyColor(Screen.Bottom); } };
        BtnResetTop.Click += (_, _) => { SldTopBright.Value = 0; SldTopContrast.Value = 1; SldTopSat.Value = 1; SldTopHi.Value = 0; SldTopShadows.Value = 0; };
        BtnResetBottom.Click += (_, _) => { SldBotBright.Value = 0; SldBotContrast.Value = 1; SldBotSat.Value = 1; SldBotHi.Value = 0; SldBotShadows.Value = 0; };
        // Copy the other screen's adjustments (setting the sliders fires their ValueChanged → persist + re-render).
        BtnCopyBottom.Click += (_, _) => { SldTopBright.Value = SldBotBright.Value; SldTopContrast.Value = SldBotContrast.Value; SldTopSat.Value = SldBotSat.Value; SldTopHi.Value = SldBotHi.Value; SldTopShadows.Value = SldBotShadows.Value; };
        BtnCopyTop.Click += (_, _) => { SldBotBright.Value = SldTopBright.Value; SldBotContrast.Value = SldTopContrast.Value; SldBotSat.Value = SldTopSat.Value; SldBotHi.Value = SldTopHi.Value; SldBotShadows.Value = SldTopShadows.Value; };

        UpdatePinFpsButton();
        BtnPinFps.Click += (_, _) => { S.ShowFpsOverlay = !S.ShowFpsOverlay; UpdatePinFpsButton(); UpdateFpsOverlay(); };

        BtnDisconnect.Click += (_, _) => Disconnect();
        BtnAmbient.Click += (_, _) => { S.AmbientGlow = !S.AmbientGlow; ApplyAmbientVisibility(); };
        BtnKeyboard.Click += (_, _) => new ShortcutsWindow { Owner = _owner }.ShowDialog();
        BtnAdjust.Click += (_, _) => SetAdjustPanels(!_adjustOn);

        BtnOcr.Click += (_, _) => StartOcrSelection();
        OcrOverlay.MouseLeftButtonDown += OcrOverlay_Down;
        OcrOverlay.MouseMove += OcrOverlay_Move;
        OcrOverlay.MouseLeftButtonUp += OcrOverlay_Up;
        OcrOverlay.MouseRightButtonDown += (_, _) => CancelOcr();
    }

    private void UpdatePinFpsButton()
        => BtnPinFps.Foreground = S.ShowFpsOverlay ? (Brush)FindResource("BrandEndBrush") : (Brush)FindResource("TextPrimaryBrush");

    /// <summary>The pinned FPS overlay is only shown in hide/clean mode (the bar already shows fps otherwise).</summary>
    private void UpdateFpsOverlay()
        => FpsOverlay.Visibility = (_clean && S.ShowFpsOverlay) ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateBarLabels()
    {
        ValGap.Text = ((int)SldGap.Value).ToString();
        ValTopScale.Text = $"{SldTopScale.Value:0.0}×";
        ValBottomScale.Text = $"{SldBottomScale.Value:0.0}×";
    }

    private void SetAdjustPanels(bool on)
    {
        _adjustOn = on;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        AdjustTop.Visibility = vis;
        AdjustBottom.Visibility = vis;
        BtnAdjust.Foreground = on ? (Brush)FindResource("BrandEndBrush") : (Brush)FindResource("TextPrimaryBrush");
    }

    private void ApplyMaxFpsSelection()
    {
        if (CmbMaxFps.SelectedItem is ComboBoxItem it)
        {
            var t = it.Content?.ToString() ?? "0";
            S.MaxFps = t == "∞" ? 0 : (int.TryParse(t, out var v) ? v : 0);
        }
    }

    // ===================== Layout =====================

    private Border MakeScreen(out Image image)
    {
        double radius = _clean ? 0 : 12;   // flush, no frame/shadow in clean mode
        image = new Image { Stretch = Stretch.Uniform };
        var img = image;
        var wrap = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(_clean ? 0 : 1),
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)),
            Margin = new Thickness(0),
            Effect = _clean ? null : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
            Child = img
        };
        img.SizeChanged += (_, _) =>
        {
            if (img.ActualWidth > 0 && img.ActualHeight > 0)
                img.Clip = new RectangleGeometry(new Rect(0, 0, img.ActualWidth, img.ActualHeight), radius, radius);
        };
        ApplyFilterTo(img);
        return wrap;
    }

    /// <summary>Scales a group (screens + gap spacer) uniformly to fill the stage.</summary>
    private static Viewbox WrapViewbox(UIElement group) => new()
    {
        Stretch = Stretch.Uniform,
        Child = group,
        Margin = new Thickness(0)
    };

    private static double TopAspect(BitmapSource? s) => s != null && s.Height > 0 ? s.Width / s.Height : 400.0 / 240.0;
    private static double BottomAspect(BitmapSource? s) => s != null && s.Height > 0 ? s.Width / s.Height : 320.0 / 240.0;

    private void BuildLayout()
    {
        ScreensHost.Children.Clear();

        _wrapTop = MakeScreen(out var top);
        _imgTop = top;
        _wrapBottom = MakeScreen(out var bottom);
        _imgBottom = bottom;
        if (_lastTop != null) _imgTop.Source = _lastTop;
        if (_lastBottom != null) _imgBottom.Source = _lastBottom;

        // HzMod streams the top screen only -> never show an empty bottom panel.
        var layout = S.Layout;
        if (_protocol == Protocol.HzMod && layout is ScreenLayout.Stacked or ScreenLayout.SideBySide or ScreenLayout.BottomOnly)
            layout = ScreenLayout.TopOnly;

        double ts = Math.Clamp(S.TopScale, 0.5, 2.0);
        double bs = Math.Clamp(S.BottomScale, 0.5, 2.0);
        double gap = Math.Clamp(S.GapV, 0, 300);

        const double baseH = 240.0;
        _wrapTop.Width = baseH * TopAspect(_lastTop) * ts; _wrapTop.Height = baseH * ts;
        _wrapBottom.Width = baseH * BottomAspect(_lastBottom) * bs; _wrapBottom.Height = baseH * bs;

        FrameworkElement group;
        switch (layout)
        {
            case ScreenLayout.Stacked:
            {
                var sp = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(_wrapTop);
                sp.Children.Add(new Border { Height = gap });
                sp.Children.Add(_wrapBottom);
                group = sp;
                break;
            }
            case ScreenLayout.SideBySide:
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(_wrapTop);
                sp.Children.Add(new Border { Width = gap });
                sp.Children.Add(_wrapBottom);
                group = sp;
                break;
            }
            case ScreenLayout.BottomOnly:
                group = _wrapBottom;
                break;
            default: // TopOnly
                group = _wrapTop;
                break;
        }

        if (S.ZoomPercent <= 0)
        {
            // Fit: scale the group uniformly to fill the stage.
            ScreensHost.Children.Add(WrapViewbox(group));
        }
        else
        {
            // Percent: render at native × zoom (100% = 1:1), centered; grow the window to fit.
            double z = S.ZoomPercent / 100.0;
            group.HorizontalAlignment = HorizontalAlignment.Center;
            group.VerticalAlignment = VerticalAlignment.Center;
            group.LayoutTransform = new ScaleTransform(z, z);
            ScreensHost.Children.Add(group);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, FitWindowToContent);
        }
    }

    /// <summary>In % zoom mode, grow the window so the zoomed screens fit ("adjust to larger screen").</summary>
    private void FitWindowToContent()
    {
        if (S.ZoomPercent <= 0 || _clean) return;
        double z = S.ZoomPercent / 100.0;
        var (gw, gh) = GroupSize();
        _owner.FitToContent(gw * z, gh * z, ControlBar.ActualHeight);
    }

    /// <summary>Design size (w,h) of the whole on-screen group, for clean-mode window fitting.</summary>
    private (double w, double h) GroupSize()
    {
        double ts = Math.Clamp(S.TopScale, 0.5, 2.0);
        double bs = Math.Clamp(S.BottomScale, 0.5, 2.0);
        double gap = Math.Clamp(S.GapV, 0, 300);
        const double baseH = 240.0;
        double tw = baseH * TopAspect(_lastTop) * ts, th = baseH * ts;
        double bw = baseH * BottomAspect(_lastBottom) * bs, bh = baseH * bs;

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod && layout is ScreenLayout.Stacked or ScreenLayout.SideBySide or ScreenLayout.BottomOnly)
            layout = ScreenLayout.TopOnly;

        return layout switch
        {
            ScreenLayout.Stacked => (Math.Max(tw, bw), th + gap + bh),
            ScreenLayout.SideBySide => (tw + gap + bw, Math.Max(th, bh)),
            ScreenLayout.BottomOnly => (bw, bh),
            _ => (tw, th),
        };
    }

    private void ApplyFilter()
    {
        if (_imgTop != null) ApplyFilterTo(_imgTop);
        if (_imgBottom != null) ApplyFilterTo(_imgBottom);
    }

    private void ApplyFilterTo(Image img)
    {
        var mode = S.Interpolation switch
        {
            Interpolation.Sharp => BitmapScalingMode.NearestNeighbor,
            Interpolation.Linear => BitmapScalingMode.LowQuality,
            _ => BitmapScalingMode.HighQuality
        };
        RenderOptions.SetBitmapScalingMode(img, mode);
    }

    private void ApplyAmbientVisibility()
    {
        var vis = S.AmbientGlow ? Visibility.Visible : Visibility.Collapsed;
        AmbientImage.Visibility = vis;
        Vignette.Visibility = vis;
        BtnAmbient.Foreground = S.AmbientGlow
            ? (Brush)FindResource("BrandEndBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        if (!S.AmbientGlow) AmbientImage.Source = null;
    }

    // ===================== Frame pipeline =====================

    private void OnFrameReady(StreamFrame frame)
    {
        System.Threading.Interlocked.Increment(ref _received);

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
        if (RenderFrame(frame.Screen, frame.Jpeg))
            System.Threading.Interlocked.Increment(ref _rendered);
    }

    private (double b, double c, double s, double hl, double sh) ColorFor(Screen screen) => screen == Screen.Top
        ? (S.TopBrightness, S.TopContrast, S.TopSaturation, S.TopHighlights, S.TopShadows)
        : (S.BottomBrightness, S.BottomContrast, S.BottomSaturation, S.BottomHighlights, S.BottomShadows);

    private bool RenderFrame(Screen screen, byte[] jpeg)
    {
        // The 270° upright correction is baked in; user Rotation is an offset on top.
        int angle = (270 + S.Rotation) % 360;
        var (b, c, s, hl, sh) = ColorFor(screen);
        BitmapSource? src = DecodeAndAdjust(jpeg, angle, b, c, s, hl, sh);
        if (src == null) return false;
        if (screen == Screen.Top) _lastTop = src; else _lastBottom = src;
        Post(screen, src);
        return true;
    }

    /// <summary>Re-renders the last frame of a screen (used when its B/C/S sliders change).</summary>
    private void ReapplyColor(Screen screen)
    {
        var jpeg = screen == Screen.Top ? _lastJpegTop : _lastJpegBottom;
        if (jpeg != null) RenderFrame(screen, jpeg);
    }

    private static BitmapSource? DecodeAndAdjust(byte[] jpeg, int rotation, double b, double c, double s, double hl, double sh)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            BitmapSource src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (b != 0 || c != 1 || s != 1 || hl != 0 || sh != 0)
                src = ApplyColorAdjust(src, b, c, s, hl, sh);
            if (rotation % 360 != 0)
                src = new TransformedBitmap(src, new RotateTransform(rotation));
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static BitmapSource ApplyColorAdjust(BitmapSource src, double b, double c, double s, double hl, double sh)
    {
        BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        conv.CopyPixels(px, stride, 0);
        double br = b * 255.0;
        static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        const double kneeHi = 150.0;           // luma above which "highlights" kick in
        const double kneeLo = 105.0;           // luma below which "shadows" kick in
        for (int i = 0; i < px.Length; i += 4)
        {
            double B = px[i], G = px[i + 1], R = px[i + 2];
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

            byte rb = Clamp(R), gb = Clamp(G), bb = Clamp(B);
            px[i] = bb; px[i + 1] = gb; px[i + 2] = rb;  // alpha untouched
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
    }

    private void Post(Screen screen, BitmapSource src)
    {
        bool needPost;
        lock (_gate)
        {
            if (screen == Screen.Top)
            {
                _pendingTop = src;
                needPost = !_topQueued;
                _topQueued = true;
            }
            else
            {
                _pendingBottom = src;
                needPost = !_bottomQueued;
                _bottomQueued = true;
            }
        }
        if (!needPost) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            BitmapSource? img;
            lock (_gate)
            {
                if (screen == Screen.Top) { img = _pendingTop; _pendingTop = null; _topQueued = false; }
                else { img = _pendingBottom; _pendingBottom = null; _bottomQueued = false; }
            }
            if (img == null) return;
            if (screen == Screen.Top && _imgTop != null) _imgTop.Source = img;
            else if (screen == Screen.Bottom && _imgBottom != null) _imgBottom.Source = img;
        });
    }

    private void OnFpsTick(object? sender, EventArgs e)
    {
        long rec = System.Threading.Interlocked.Exchange(ref _received, 0);
        long ren = System.Threading.Interlocked.Exchange(ref _rendered, 0);
        FpsBadge.Text = $"{ren} / {rec} fps";
        FpsOverlayText.Text = FpsBadge.Text;
        UpdateFpsOverlay();
        FpsDot.Fill = ren > 0 ? Brushes.LimeGreen : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        StatusText.Text = ren > 0 ? "Streaming" : "Waiting for frames…";

        // Stream-drop watchdog: UDP (NTR) has no drop signal, so treat a few seconds with no
        // received frames as a lost stream and trigger the reconnect path when Try Reconnect is on.
        _staleSecs = rec == 0 ? _staleSecs + 1 : 0;
        if (_staleSecs >= 5 && S.TryReconnect && !_disconnected)
        {
            _staleSecs = 0;
            OnFailed("Stream lost");
        }
    }

    private void OnAmbientTick(object? sender, EventArgs e)
    {
        if (!S.AmbientGlow) return;
        var src = _lastTop ?? _lastBottom;
        if (src != null) AmbientImage.Source = src;
    }

    private void OnFailed(string msg) => Dispatcher.Invoke(() =>
    {
        if (_disconnected) return;
        if (S.TryReconnect)
        {
            Teardown();
            _owner.RequestReconnect();   // back to menu, which auto-retries the same IP
            return;
        }
        Disconnect();
        MessageBox.Show(_owner, msg, "3DSnickerStream", MessageBoxButton.OK, MessageBoxImage.Warning);
    });

    // ===================== Keyboard shortcuts =====================

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        // While selecting an OCR region, swallow keys; Esc cancels the selection.
        if (_ocrActive)
        {
            if (key == Key.Escape) { e.Handled = true; CancelOcr(); }
            return;
        }

        var action = MatchAction(key, Keyboard.Modifiers);

        // In clean mode, Esc or the hide-UI key restores the interface.
        if (_owner.IsCleanMode && (action == ShortcutAction.ToggleUi || key == Key.Escape))
        {
            e.Handled = true;
            ExitClean();
            return;
        }

        if (action == null) return;
        e.Handled = true;
        switch (action)
        {
            case ShortcutAction.Screenshot: TakeScreenshot(toClipboard: false); break;
            case ShortcutAction.ScreenshotToClipboard: TakeScreenshot(toClipboard: true); break;
            case ShortcutAction.Disconnect: Disconnect(); break;
            case ShortcutAction.CycleLayout: Cycle(CmbLayout); break;
            case ShortcutAction.CycleFilter: Cycle(CmbFilter); break;
            case ShortcutAction.RotateScreen: Cycle(CmbRotation); break;
            case ShortcutAction.ToggleFullscreen: _owner.ToggleFullscreen(); break;
            case ShortcutAction.IncreaseQuality: AdjustQuality(+5); break;
            case ShortcutAction.DecreaseQuality: AdjustQuality(-5); break;
            case ShortcutAction.SwapPriorityScreen: SwapPriority(); break;
            case ShortcutAction.ToggleUi: EnterClean(); break;
            case ShortcutAction.CopyText: StartOcrSelection(); break;
        }
    }

    // ===================== OCR: copy text from the screen =====================

    private void StartOcrSelection()
    {
        if (_ocrActive) return;
        if (_lastTop == null && _lastBottom == null) { StatusText.Text = "No frame to read yet"; return; }
        _ocrSnapTop = _lastTop;                 // freeze the frames shown right now (values are static)
        _ocrSnapBottom = _lastBottom;
        _ocrActive = true;
        OcrRect.Visibility = Visibility.Collapsed;
        OcrHint.Visibility = Visibility.Visible;
        OcrOverlay.Visibility = Visibility.Visible;
        OcrOverlay.CaptureMouse();
        Focus();
    }

    private void CancelOcr()
    {
        if (!_ocrActive) return;
        _ocrActive = false;
        OcrOverlay.ReleaseMouseCapture();
        OcrOverlay.Visibility = Visibility.Collapsed;
        OcrRect.Visibility = Visibility.Collapsed;
        OcrHint.Visibility = Visibility.Collapsed;
    }

    private void OcrOverlay_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_ocrActive) return;
        _ocrStart = e.GetPosition(OcrOverlay);
        Canvas.SetLeft(OcrRect, _ocrStart.X);
        Canvas.SetTop(OcrRect, _ocrStart.Y);
        OcrRect.Width = 0; OcrRect.Height = 0;
        OcrRect.Visibility = Visibility.Visible;
        OcrHint.Visibility = Visibility.Collapsed;
    }

    private void OcrOverlay_Move(object sender, MouseEventArgs e)
    {
        if (!_ocrActive || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(OcrOverlay);
        Canvas.SetLeft(OcrRect, Math.Min(p.X, _ocrStart.X));
        Canvas.SetTop(OcrRect, Math.Min(p.Y, _ocrStart.Y));
        OcrRect.Width = Math.Abs(p.X - _ocrStart.X);
        OcrRect.Height = Math.Abs(p.Y - _ocrStart.Y);
    }

    private async void OcrOverlay_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_ocrActive) return;
        var p = e.GetPosition(OcrOverlay);
        var sel = new Rect(
            new System.Windows.Point(Math.Min(p.X, _ocrStart.X), Math.Min(p.Y, _ocrStart.Y)),
            new System.Windows.Size(Math.Abs(p.X - _ocrStart.X), Math.Abs(p.Y - _ocrStart.Y)));
        CancelOcr();                            // hide overlay + release capture before showing results
        if (sel.Width < 6 || sel.Height < 6) return;   // too small = treat as a cancel click

        var crop = CropForOcr(sel);
        if (crop == null) { StatusText.Text = "Selection wasn't over a screen"; return; }
        _ocrLastCrop = crop;

        StatusText.Text = "Reading text…";
        try
        {
            var text = await OcrService.RecognizeAsync(crop, S.OcrHexMode);
            StatusText.Text = "Streaming";
            if (text == null)
            {
                MessageBox.Show(_owner,
                    "Windows OCR isn't available. Add an OCR language pack in\nSettings → Time & language → Language & region.",
                    "3DSnickerStream", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            text = text.Trim();
            if (text.Length == 0) { StatusText.Text = "No text found"; return; }
            Dialogs.ShowOcrResult(_owner, text, S.OcrHexMode, async hx =>
            {
                S.OcrHexMode = hx;
                App.Settings.Save();
                return _ocrLastCrop == null ? null : await OcrService.RecognizeAsync(_ocrLastCrop, hx);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Streaming";
            MessageBox.Show(_owner, "OCR failed: " + ex.Message, "3DSnickerStream",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Maps an overlay-space selection to source pixels of the screen under its center and returns that
    /// crop from the frozen snapshot. Uses WPF <c>TransformToVisual</c>, so it works in Fit or % zoom and
    /// with any rotation. Null if the selection isn't over a screen.
    /// </summary>
    private BitmapSource? CropForOcr(Rect selection)
    {
        var center = new System.Windows.Point(selection.X + selection.Width / 2, selection.Y + selection.Height / 2);
        foreach (var (img, snap) in new[] { (_imgTop, _ocrSnapTop), (_imgBottom, _ocrSnapBottom) })
        {
            if (img == null || snap == null || img.ActualWidth <= 0 || img.ActualHeight <= 0) continue;
            GeneralTransform toImg;
            try { toImg = OcrOverlay.TransformToVisual(img); } catch { continue; }

            var c = toImg.Transform(center);
            if (c.X < 0 || c.Y < 0 || c.X > img.ActualWidth || c.Y > img.ActualHeight) continue;  // center off this screen

            var p1 = toImg.Transform(selection.TopLeft);
            var p2 = toImg.Transform(selection.BottomRight);
            double sx = snap.PixelWidth / img.ActualWidth, sy = snap.PixelHeight / img.ActualHeight;
            double x1 = Math.Clamp(Math.Min(p1.X, p2.X) * sx, 0, snap.PixelWidth);
            double y1 = Math.Clamp(Math.Min(p1.Y, p2.Y) * sy, 0, snap.PixelHeight);
            double x2 = Math.Clamp(Math.Max(p1.X, p2.X) * sx, 0, snap.PixelWidth);
            double y2 = Math.Clamp(Math.Max(p1.Y, p2.Y) * sy, 0, snap.PixelHeight);
            int ix = (int)Math.Floor(x1), iy = (int)Math.Floor(y1);
            int iw = (int)Math.Ceiling(x2 - x1), ih = (int)Math.Ceiling(y2 - y1);
            if (ix + iw > snap.PixelWidth) iw = snap.PixelWidth - ix;
            if (iy + ih > snap.PixelHeight) ih = snap.PixelHeight - iy;
            if (iw < 2 || ih < 2) return null;
            return new CroppedBitmap(snap, new Int32Rect(ix, iy, iw, ih));
        }
        return null;
    }

    private void EnterClean()
    {
        ControlBar.Visibility = Visibility.Collapsed;
        AdjustTop.Visibility = Visibility.Collapsed;
        AdjustBottom.Visibility = Visibility.Collapsed;
        ScreensHost.Margin = new Thickness(0);
        _clean = true;
        BuildLayout();                       // rebuild screens flush (no corners/shadow/border)
        UpdateFpsOverlay();                  // show the pinned FPS counter if enabled
        var (gw, gh) = GroupSize();
        _owner.EnterCleanMode(gw, gh);
        Focus();
    }

    private void ExitClean()
    {
        _owner.ExitCleanMode();
        ControlBar.Visibility = Visibility.Visible;
        ScreensHost.Margin = new Thickness(24);
        _clean = false;
        UpdateFpsOverlay();                  // hide the overlay (bar shows fps again)
        BuildLayout();                       // restore framed screens
        if (_adjustOn)
        {
            AdjustTop.Visibility = Visibility.Visible;
            AdjustBottom.Visibility = Visibility.Visible;
        }
        Focus();
    }

    private ShortcutAction? MatchAction(Key key, ModifierKeys mods)
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
        => cmb.SelectedIndex = (cmb.SelectedIndex + 1) % cmb.Items.Count;

    private void AdjustQuality(int delta)
    {
        if (_protocol == Protocol.NTR)
        {
            S.ImageQuality = Math.Clamp(S.ImageQuality + delta, 10, 100);
            _client.SetQuality(S.ImageQuality);
            StatusText.Text = $"Quality {S.ImageQuality}";
        }
        else
        {
            S.HzQuality = Math.Clamp(S.HzQuality + delta, 1, 100);
            _client.SetQuality(S.HzQuality);
            StatusText.Text = $"Quality {S.HzQuality}";
        }
    }

    private void SwapPriority()
    {
        if (_client is NTRClient ntr)
        {
            ntr.SwapPriorityScreen();
            S.PriorityScreenTop = !S.PriorityScreenTop;
            StatusText.Text = $"Priority: {(S.PriorityScreenTop ? "Top" : "Bottom")}";
        }
    }

    // ===================== Screenshot =====================

    private void TakeScreenshot(bool toClipboard)
    {
        var top = _lastTop;
        var bottom = _lastBottom;
        if (top == null && bottom == null) return;

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod) layout = ScreenLayout.TopOnly;

        var visual = new DrawingVisual();
        double gap = Math.Clamp(S.GapV, 0, 300);   // match the live Gap setting (0 = glued)
        double w, h;
        using (var dc = visual.RenderOpen())
        {
            switch (layout)
            {
                case ScreenLayout.TopOnly when top != null:
                    w = top.PixelWidth; h = top.PixelHeight;
                    dc.DrawImage(top, new Rect(0, 0, w, h));
                    break;
                case ScreenLayout.BottomOnly when bottom != null:
                    w = bottom.PixelWidth; h = bottom.PixelHeight;
                    dc.DrawImage(bottom, new Rect(0, 0, w, h));
                    break;
                case ScreenLayout.SideBySide:
                    double tw = top?.PixelWidth ?? 0, th = top?.PixelHeight ?? 0;
                    double bw = bottom?.PixelWidth ?? 0, bh = bottom?.PixelHeight ?? 0;
                    w = tw + gap + bw; h = Math.Max(th, bh);
                    if (top != null) dc.DrawImage(top, new Rect(0, (h - th) / 2, tw, th));
                    if (bottom != null) dc.DrawImage(bottom, new Rect(tw + gap, (h - bh) / 2, bw, bh));
                    break;
                default: // Stacked
                    double twS = top?.PixelWidth ?? 0, thS = top?.PixelHeight ?? 0;
                    double bwS = bottom?.PixelWidth ?? 0, bhS = bottom?.PixelHeight ?? 0;
                    w = Math.Max(twS, bwS); h = thS + (top != null && bottom != null ? gap : 0) + bhS;
                    if (top != null) dc.DrawImage(top, new Rect((w - twS) / 2, 0, twS, thS));
                    if (bottom != null) dc.DrawImage(bottom, new Rect((w - bwS) / 2, thS + gap, bwS, bhS));
                    break;
            }
        }
        if (w <= 0 || h <= 0) return;

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        try
        {
            if (toClipboard)
            {
                Clipboard.SetImage(rtb);
                StatusText.Text = "Screenshot copied to clipboard";
                return;
            }

            var dir = string.IsNullOrWhiteSpace(S.ScreenshotFolder) ? AppSettings.DefaultScreenshotFolder : S.ScreenshotFolder;
            Directory.CreateDirectory(dir);
            var name = $"3DSnickerStream-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var path = Path.Combine(dir, name);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
            StatusText.Text = $"Saved {name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Screenshot failed: {ex.Message}";
        }
    }

    // ===================== Teardown =====================

    /// <summary>Stops the client/timers and unsubscribes — without navigating.</summary>
    private void Teardown()
    {
        if (_disconnected) return;
        _disconnected = true;
        _fpsTimer?.Stop();
        _ambientTimer?.Stop();
        _client.FrameReady -= OnFrameReady;
        _client.Failed -= OnFailed;
        _owner.PreviewKeyDown -= OnKeyDown;
        try { _client.Stop(); } catch { }
        _client.Dispose();
        App.Settings.Save();
    }

    private void Disconnect()
    {
        if (_disconnected) return;
        Teardown();
        _owner.ShowConnect();
    }
}
