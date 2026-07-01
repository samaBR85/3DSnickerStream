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
    private bool _topQueued, _bottomQueued;
    private readonly object _gate = new();

    private long _received, _rendered;                  // counted by the fps timer
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

        CmbMaxFps.Items.Clear();
        foreach (var v in FpsPresets)
            CmbMaxFps.Items.Add(new ComboBoxItem { Content = v == 0 ? "∞" : v.ToString() });
        int idx = Array.IndexOf(FpsPresets, S.MaxFps);
        if (idx < 0) { CmbMaxFps.Items.Add(new ComboBoxItem { Content = S.MaxFps.ToString() }); idx = CmbMaxFps.Items.Count - 1; }
        CmbMaxFps.SelectedIndex = idx;

        CmbLayout.SelectionChanged += (_, _) => { if (_loaded) { S.Layout = (ScreenLayout)CmbLayout.SelectedIndex; BuildLayout(); } };
        CmbFilter.SelectionChanged += (_, _) => { if (_loaded) { S.Interpolation = (Interpolation)CmbFilter.SelectedIndex; ApplyFilter(); } };
        CmbRotation.SelectionChanged += (_, _) => { if (_loaded) S.Rotation = CmbRotation.SelectedIndex switch { 0 => 0, 1 => 90, 2 => 180, _ => 270 }; };
        CmbMaxFps.SelectionChanged += (_, _) => { if (_loaded) ApplyMaxFpsSelection(); };

        BtnDisconnect.Click += (_, _) => Disconnect();
        BtnAmbient.Click += (_, _) => { S.AmbientGlow = !S.AmbientGlow; ApplyAmbientVisibility(); };
        BtnKeyboard.Click += (_, _) => new ShortcutsWindow { Owner = _owner }.ShowDialog();
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
        image = new Image { Stretch = Stretch.Uniform };
        var img = image;
        var wrap = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)),
            Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
            Child = img
        };
        img.SizeChanged += (_, _) =>
        {
            if (img.ActualWidth > 0 && img.ActualHeight > 0)
                img.Clip = new RectangleGeometry(new Rect(0, 0, img.ActualWidth, img.ActualHeight), 12, 12);
        };
        ApplyFilterTo(img);
        return wrap;
    }

    /// <summary>Wraps a screen border in a Viewbox so it scales to fill its grid cell.</summary>
    private static Viewbox CellFor(UIElement screen) => new()
    {
        Stretch = Stretch.Uniform,
        Child = screen,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6)
    };

    private void BuildLayout()
    {
        ScreensHost.Children.Clear();
        ScreensHost.RowDefinitions.Clear();
        ScreensHost.ColumnDefinitions.Clear();

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

        switch (layout)
        {
            case ScreenLayout.Stacked:
            {
                ScreensHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ts, GridUnitType.Star) });
                ScreensHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bs, GridUnitType.Star) });
                var cellTop = CellFor(_wrapTop);
                var cellBottom = CellFor(_wrapBottom);
                Grid.SetRow(cellTop, 0); Grid.SetRow(cellBottom, 1);
                ScreensHost.Children.Add(cellTop); ScreensHost.Children.Add(cellBottom);
                break;
            }
            case ScreenLayout.SideBySide:
            {
                ScreensHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ts, GridUnitType.Star) });
                ScreensHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bs, GridUnitType.Star) });
                var cellTop = CellFor(_wrapTop);
                var cellBottom = CellFor(_wrapBottom);
                Grid.SetColumn(cellTop, 0); Grid.SetColumn(cellBottom, 1);
                ScreensHost.Children.Add(cellTop); ScreensHost.Children.Add(cellBottom);
                break;
            }
            case ScreenLayout.TopOnly:
                ScreensHost.Children.Add(CellFor(_wrapTop));
                break;
            case ScreenLayout.BottomOnly:
                ScreensHost.Children.Add(CellFor(_wrapBottom));
                break;
        }
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

        // The 270° upright correction is baked in; user Rotation is an offset on top.
        int angle = (270 + S.Rotation) % 360;
        BitmapSource? src = Decode(frame.Jpeg, angle);
        if (src == null) return;

        System.Threading.Interlocked.Increment(ref _rendered);

        if (frame.Screen == Screen.Top) _lastTop = src; else _lastBottom = src;
        Post(frame.Screen, src);
    }

    private static BitmapSource? Decode(byte[] jpeg, int rotation)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            BitmapSource src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (rotation % 360 != 0)
            {
                var t = new TransformedBitmap(src, new RotateTransform(rotation));
                src = t;
            }
            src.Freeze();
            return src;
        }
        catch { return null; }
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
        FpsDot.Fill = ren > 0 ? Brushes.LimeGreen : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        StatusText.Text = ren > 0 ? "Streaming" : "Waiting for frames…";
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
        Disconnect();
        MessageBox.Show(_owner, msg, "3DSnickerStream", MessageBoxButton.OK, MessageBoxImage.Warning);
    });

    // ===================== Keyboard shortcuts =====================

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        var action = MatchAction(key, Keyboard.Modifiers);
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
        }
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
        const double gap = 8;
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

    private void Disconnect()
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
        _owner.ShowConnect();
    }
}
