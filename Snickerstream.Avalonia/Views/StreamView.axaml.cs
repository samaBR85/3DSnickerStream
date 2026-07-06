using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnickerstreamV2.Imaging;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;
using SnickerstreamV2.Services;
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
    private readonly Net.NtrLossless _lossyTop = new();       // NTR-HR Uncompressed(UDP) decoders (per screen)
    private readonly Net.NtrLossless _lossyBottom = new();
    private bool _adjustOn;                                   // per-screen color panels visible
    private double _preAdjustWidth;                           // window width before the adjust panels grew it
    private bool _clean;                                      // clean/hide mode (flush screens, no chrome)
    private Control? _zoomGroup;                              // the screen group, for % zoom window-fit measuring
    private UpscaleFilter _upscale;                           // active upscale filter (GPU shader, CPU fallback)
    private EffectFilter _effect;                            // active post effect (CRT), GPU only
    private float _effectIntensity = 1.0f;                    // strength of _effect, 0..1
    // Any filter/effect renders through a GpuScreen shader that upscales at draw time, so frames stay native
    // and the layout never needs the old 2× factor math. None → a plain Image.
    private bool GpuMode => _upscale != UpscaleFilter.None || _effect != EffectFilter.None;
    private GpuScreen? _gpuTop, _gpuBottom;                   // GPU shader renderers (used in GpuMode)
    private Control? _scrTop, _scrBottom;                     // the active screen controls (Image or GpuScreen)

    private bool _ocrActive;                                  // OCR marquee-selection in progress
    private Point _ocrStart;                                  // drag origin (overlay space)
    private Bitmap? _ocrSnapTop, _ocrSnapBottom;             // frozen owned copies captured when selection began
    private Bitmap? _ocrLastCrop;                             // last cropped region (for the Hex-mode re-run)
    private bool _ocrResultDragging;                          // dragging the result panel by its header
    private Point _ocrResultDragStart;                        // pointer position (panel space) when the drag began
    private Point _ocrResultDragOrigin;                       // TranslateTransform offset when the drag began
    private TranslateTransform _ocrResultDrag = null!;        // OcrResultPanel.RenderTransform, cached in InitControls
    private bool _settingOcrHexProgrammatically;              // suppresses the re-run when we set the checkbox ourselves

    private long _received, _rendered;
    private int _staleSecs;                                   // consecutive seconds with no received frames
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
        // In clean/hide mode there's no title bar — let the user drag the stage to move the window.
        Stage.PointerPressed += OnStagePointerPressed;

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
        _upscale = S.Upscale;
        CmbUpscale.SelectedIndex = (int)S.Upscale;
        _effect = S.Effect;
        CmbEffect.SelectedIndex = (int)S.Effect;
        _effectIntensity = (float)S.EffectIntensity;
        SldEffectIntensity.Value = S.EffectIntensity;
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
        CmbUpscale.SelectionChanged += (_, _) => { if (_loaded) { S.Upscale = (UpscaleFilter)CmbUpscale.SelectedIndex; _upscale = S.Upscale; BuildLayout(refitWindow: false); ApplyFilter(); ReapplyColor(Screen.Top); ReapplyColor(Screen.Bottom); } };
        CmbEffect.SelectionChanged += (_, _) => { if (_loaded) { S.Effect = (EffectFilter)CmbEffect.SelectedIndex; _effect = S.Effect; BuildLayout(refitWindow: false); ApplyFilter(); ReapplyColor(Screen.Top); ReapplyColor(Screen.Bottom); } };
        OnSlider(SldEffectIntensity, v => { S.EffectIntensity = Math.Round(v, 2); _effectIntensity = (float)S.EffectIntensity; UpdateBarLabels(); if (_gpuTop != null) _gpuTop.EffectIntensity = _effectIntensity; if (_gpuBottom != null) _gpuBottom.EffectIntensity = _effectIntensity; _gpuTop?.InvalidateVisual(); _gpuBottom?.InvalidateVisual(); });
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
        BtnHideBar.Click += (_, _) => { ControlBar.IsVisible = false; BtnShowBar.IsVisible = true; };
        BtnShowBar.Click += (_, _) => { ControlBar.IsVisible = true; BtnShowBar.IsVisible = false; };
        BtnKeyboard.Click += (_, _) => new ShortcutsWindow().ShowDialog(_owner);
        BtnOcr.Click += (_, _) => StartOcrSelection();
        OcrOverlay.PointerPressed += OcrDown;
        OcrOverlay.PointerMoved += OcrMove;
        OcrOverlay.PointerReleased += OcrUp;
        _ocrResultDrag = (TranslateTransform)OcrResultPanel.RenderTransform!;
        BtnOcrResultClose.Click += (_, _) => CloseOcrResult();
        BtnOcrResultCopy.Click += async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(OcrResultBox.Text ?? "");
            OcrResultStatus.Text = "Copied to clipboard";
        };
        OcrHexToggle.IsCheckedChanged += OnOcrHexToggled;
        OcrResultPanel.PointerPressed += OcrResultDragDown;
        OcrResultPanel.PointerMoved += OcrResultDragMove;
        OcrResultPanel.PointerReleased += OcrResultDragUp;
        BtnDisconnect.Click += (_, _) => Disconnect();

        HeadDisplay.Click += (_, _) => ToggleColumn(BodyDisplay, ChevDisplay, SumDisplay,
            () => (CmbLayout.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "");
        HeadUpscale.Click += (_, _) => ToggleColumn(BodyUpscale, ChevUpscale, SumUpscale,
            () => $"{(CmbUpscale.SelectedItem as ComboBoxItem)?.Content} · {(CmbEffect.SelectedItem as ComboBoxItem)?.Content}");
        HeadGeometry.Click += (_, _) => ToggleColumn(BodyGeometry, ChevGeometry, SumGeometry,
            () => (CmbZoom.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "");
        HeadCapture.Click += (_, _) => ToggleColumn(BodyCapture, ChevCapture, null, null);
        HeadVisual.Click += (_, _) => ToggleColumn(BodyVisual, ChevVisual, null, null);
        HeadWindow.Click += (_, _) => ToggleColumn(BodyWindow, ChevWindow, null, null);
        HeadSession.Click += (_, _) => ToggleColumn(BodySession, ChevSession, SumSession, () => FpsBadge.Text ?? "");

        WireColumnDrag(GripDisplay, ColDisplay);
        WireColumnDrag(GripUpscale, ColUpscale);
        WireColumnDrag(GripGeometry, ColGeometry);
        WireColumnDrag(GripCapture, ColCapture);
        WireColumnDrag(GripVisual, ColVisual);
        WireColumnDrag(GripWindow, ColWindow);
        WireColumnDrag(GripSession, ColSession);

        ApplyAmbientVisibility();
        UpdatePinFps();
    }

    // Streambar column collapse: click a card's header to shrink it to just its title (+ current value,
    // for cards with one) — everything stays reachable, just parked out of the way.
    private static void ToggleColumn(StackPanel body, TextBlock chev, TextBlock? sum, Func<string>? summaryText)
    {
        bool willCollapse = body.IsVisible;
        body.IsVisible = !willCollapse;
        chev.Text = willCollapse ? "▸" : "▾";
        if (sum != null && summaryText != null)
        {
            sum.Text = summaryText();
            sum.IsVisible = willCollapse;
        }
    }

    // Streambar column reorder: grab a card's "⠿" grip (never the header text, so it never fights with
    // click-to-collapse) and drag it over another card to swap places. Order isn't persisted — a fresh
    // connection starts back at the default layout.
    private Border? _dragCol;
    private Border? _dragOverCol;
    private Point _dragStart;
    private bool _dragMoved;

    private void WireColumnDrag(TextBlock grip, Border col)
    {
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            _dragCol = col; _dragMoved = false;
            _dragStart = e.GetPosition(ColumnsHost);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_dragCol != col) return;
            var pos = e.GetPosition(ColumnsHost);
            double dx = pos.X - _dragStart.X, dy = pos.Y - _dragStart.Y;
            if (!_dragMoved && (dx * dx + dy * dy) < 25) return;
            _dragMoved = true;
            col.Opacity = 0.45;

            Border? over = null;
            foreach (var child in ColumnsHost.Children)
            {
                if (child is not Border b || b == col) continue;
                var tl = b.TranslatePoint(new Point(0, 0), ColumnsHost) ?? default;
                if (new Rect(tl, b.Bounds.Size).Contains(pos)) { over = b; break; }
            }
            if (over != _dragOverCol)
            {
                _dragOverCol?.Classes.Remove("dragover");
                over?.Classes.Add("dragover");
                _dragOverCol = over;
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            e.Pointer.Capture(null);
            if (_dragCol != col) return;
            col.Opacity = 1.0;
            _dragOverCol?.Classes.Remove("dragover");
            if (_dragMoved && _dragOverCol != null)
            {
                // Capture the target's index BEFORE removing the dragged card — recomputing it
                // afterwards (the previous bug) shifts by one whenever they're adjacent, which made
                // dropping onto an immediate neighbour silently no-op.
                int dstIdx = ColumnsHost.Children.IndexOf(_dragOverCol);
                ColumnsHost.Children.Remove(col);
                ColumnsHost.Children.Insert(dstIdx, col);
            }
            _dragCol = null; _dragOverCol = null; _dragMoved = false;
        };
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
        ValEffectIntensity.Text = $"{(int)(SldEffectIntensity.Value * 100)}%";
        ValTopScale.Text = $"{SldTopScale.Value:0.0}×";
        ValBottomScale.Text = $"{SldBottomScale.Value:0.0}×";
    }

    private void SetAdjustPanels(bool on)
    {
        // Grow the window to fit the two side panels alongside the screens, restore on close.
        const double PanelsWidth = 2 * 226;   // two ~196px panels + margins, in DIPs
        if (on && !_adjustOn && !_clean && _owner.WindowState == WindowState.Normal)
        {
            _preAdjustWidth = _owner.Width;
            var screen = _owner.Screens.ScreenFromWindow(_owner) ?? _owner.Screens.Primary;
            double waW = (screen?.WorkingArea.Width ?? 1920) / (screen?.Scaling ?? 1.0);
            _owner.Width = Math.Min(waW, _owner.Width + PanelsWidth);
        }
        else if (!on && _adjustOn && !_clean && _owner.WindowState == WindowState.Normal && _preAdjustWidth > 0)
        {
            _owner.Width = _preAdjustWidth;
        }

        _adjustOn = on;
        AdjustTop.IsVisible = on;
        AdjustBottom.IsVisible = on;
        BtnAdjust.Foreground = on ? Brush("BrandEndBrush") : Brush("TextPrimaryBrush");
    }

    // ===================== Layout =====================

    private void BuildLayout(bool refitWindow = true)
    {
        ScreensHost.Children.Clear();
        if (GpuMode)
        {
            _imgTop = _imgBottom = null;
            // xBR needs clean pixel edges: a lossless NTR stream can use a tight threshold; JPEG modes need a
            // looser one so block/ringing noise doesn't spawn phantom edges.
            bool cleanSource = _protocol == Protocol.NTR && S.NtrKcpMode >= 3;
            float eq = cleanSource ? 0.30f : 0.45f;
            _gpuTop = new GpuScreen { Filter = _upscale, PostEffect = _effect, EffectIntensity = _effectIntensity, XbrEq = eq }; _gpuTop.SetDefaultSize(240, 400); _gpuTop.FirstRender += OnGpuFirstRender;
            _gpuTop.Diag += s => ShowToast("multipass: " + s);   // diagnostic for the multi-pass filters
            _gpuBottom = new GpuScreen { Filter = _upscale, PostEffect = _effect, EffectIntensity = _effectIntensity, XbrEq = eq }; _gpuBottom.SetDefaultSize(240, 320); _gpuBottom.FirstRender += OnGpuFirstRender;
            _scrTop = _gpuTop; _scrBottom = _gpuBottom;
        }
        else
        {
            _gpuTop = _gpuBottom = null;
            _imgTop = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };
            _imgBottom = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };
            _scrTop = _imgTop; _scrBottom = _imgBottom;
        }

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod) layout = ScreenLayout.TopOnly;   // HzMod streams top only

        double ts = Math.Clamp(S.TopScale, 0.5, 2.0);
        double bs = Math.Clamp(S.BottomScale, 0.5, 2.0);
        double gap = Math.Clamp(S.GapV, 0, 300);

        Control group = layout switch
        {
            ScreenLayout.TopOnly => MakeScreen(_scrTop!, ts),
            ScreenLayout.BottomOnly => MakeScreen(_scrBottom!, bs),
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
            // Percent: render at native × zoom (100% = native 1:1), centered; grow the window to fit. Wrapped
            // in a down-only Viewbox so shrinking the window below the content scales DOWN instead of clipping.
            double z = S.ZoomPercent / 100.0;
            _zoomGroup = group;
            var scaled = new LayoutTransformControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(z, z),
                Child = group
            };
            ScreensHost.Children.Add(new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = scaled
            });
            // Only grow the window to fit on zoom/layout changes — not when merely swapping the upscale
            // filter (the native display size is unchanged, so a refit would just jitter the window).
            if (refitWindow) Dispatcher.UIThread.Post(FitWindowToContent, DispatcherPriority.Loaded);
        }

        // reapply the current frames (owned bitmaps; do NOT dispose here)
        if (_lastTopBmp != null) SetScreenSource(Screen.Top, _lastTopBmp);
        if (_lastBottomBmp != null) SetScreenSource(Screen.Bottom, _lastBottomBmp);
        ApplyFilter();
        ApplyAmbientTransform();
    }

    /// <summary>Rotates the blurred backdrop to match the screens (the raw frame is sideways),
    /// over-scaled so a 90°/270° turn doesn't reveal the corners.</summary>
    private void ApplyAmbientTransform()
    {
        int angle = (270 + S.Rotation) % 360;
        var g = new TransformGroup();
        g.Children.Add(new RotateTransform(angle));
        g.Children.Add(new ScaleTransform(1.7, 1.7));
        AmbientImage.RenderTransform = g;
        AmbientImage.RenderTransformOrigin = RelativePoint.Center;
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
        sp.Children.Add(MakeScreen(_scrTop!, ts));
        sp.Children.Add(MakeScreen(_scrBottom!, bs));
        return sp;
    }

    /// <summary>Routes a decoded frame to the active screen control (Image, or GpuScreen in GPU mode).</summary>
    private void SetScreenSource(Screen screen, Bitmap bmp)
    {
        if (screen == Screen.Top)
        {
            if (_gpuTop != null) _gpuTop.SetFrame(bmp);
            else if (_imgTop != null) _imgTop.Source = bmp;
        }
        else
        {
            if (_gpuBottom != null) _gpuBottom.SetFrame(bmp);
            else if (_imgBottom != null) _imgBottom.Source = bmp;
        }
    }

    // Upscale filters with no CPU port (Upscaler.Apply falls through to passthrough for these) — GPU-only.
    private static readonly UpscaleFilter[] GpuOnlyUpscale =
        { UpscaleFilter.ScaleFx, UpscaleFilter.Mmpx, UpscaleFilter.Anime4KCnn, UpscaleFilter.Anime4KCnnM, UpscaleFilter.Anime4KCnnL, UpscaleFilter.Anime4KCnnVL };

    private bool _gpuToastShown;
    private void OnGpuFirstRender(bool gpu)
    {
        if (_gpuToastShown) return;
        _gpuToastShown = true;
        if (gpu) { ShowToast("GPU rendering: ON ✓"); return; }

        // No GPU context: the GPU-only filters would silently no-op (passthrough) if left selectable, so
        // grey them out for the rest of this session instead of leaving a confusing dead option.
        foreach (var f in GpuOnlyUpscale)
            if (CmbUpscale.Items[(int)f] is ComboBoxItem it) { it.IsEnabled = false; ToolTip.SetTip(it, "Requires GPU rendering (unavailable this session)"); }
        for (int i = 1; i < CmbEffect.Items.Count; i++)   // every Effect but None is GPU-only, no CPU port at all
            if (CmbEffect.Items[i] is ComboBoxItem it) { it.IsEnabled = false; ToolTip.SetTip(it, "Requires GPU rendering (unavailable this session)"); }

        bool reverted = false;
        if (System.Array.IndexOf(GpuOnlyUpscale, _upscale) >= 0)
        {
            _upscale = UpscaleFilter.Sharp; S.Upscale = _upscale; CmbUpscale.SelectedIndex = (int)_upscale;
            reverted = true;
        }
        if (_effect != EffectFilter.None)
        {
            _effect = EffectFilter.None; S.Effect = _effect; CmbEffect.SelectedIndex = (int)_effect;
            reverted = true;
        }
        if (reverted) { S.Save(); BuildLayout(refitWindow: false); ApplyFilter(); }
        ShowToast(reverted
            ? "GPU rendering: OFF — GPU-only filters disabled, reverted to a CPU-capable one"
            : "GPU rendering: OFF (software fallback) — GPU-only filters disabled this session");
    }

    // 270deg upright correction is baked in; user Rotation is an offset on top. Layout-transform, not pixels.
    // Per-screen scale is a uniform ScaleTransform composed with the rotation; the framed Border rotates too.
    private Control MakeScreen(Control img, double scale)
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
        if (_disposed || S.ZoomPercent <= 0 || _zoomGroup is not { } group) return;
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
        // Point the backdrop at the current frame now; Present keeps it live thereafter.
        AmbientImage.Source = S.AmbientGlow ? (_lastTopBmp ?? _lastBottomBmp) : null;
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
        BtnShowBar.IsVisible = false;   // no floating restore button in full clean mode
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

    /// <summary>In clean/hide mode (no title bar) a left-drag on the stage moves the window, like a title
    /// bar would. Skipped while the OCR selection overlay is up so a marquee drag isn't hijacked.</summary>
    private void OnStagePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!_clean || OcrOverlay.IsVisible) return;
        if (!e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed) return;
        try { _owner.BeginMoveDrag(e); } catch { /* platform may refuse mid-gesture */ }
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
        double pw = bmp?.PixelSize.Width ?? defH, ph = bmp?.PixelSize.Height ?? defW;   // native (frames are native; GPU upscales at draw)
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

        // NTR-HR Uncompressed (UDP): bespoke YCbCr 4:2:0 field decode (Phase 1b).
        if (frame.Kind == FrameKind.RawLossless)
        {
            int height = frame.Screen == Screen.Top ? 400 : 320;   // native portrait, 240 wide
            var dec = frame.Screen == Screen.Top ? _lossyTop : _lossyBottom;
            if (dec.DecodeBuffer(frame.Jpeg, frame.EvenOdd, height) is not { } lb) return;   // unsupported / partial — hold last
            var raw = WrapBgra(lb.bgra, lb.w, lb.h, PixelFormat.Bgra8888, AlphaFormat.Opaque);
            if (raw == null) return;
            Interlocked.Increment(ref _rendered);
            Post(frame.Screen, raw);
            return;
        }

        // JPEG (Reliable Stream, Delta): already decoded to BGRA in the network layer (stateful decoder).
        if (frame.Kind == FrameKind.RawBgra)
        {
            var raw = WrapBgra(frame.Jpeg, frame.Width, frame.Height, PixelFormat.Bgra8888, AlphaFormat.Opaque);
            if (raw == null) return;
            Interlocked.Increment(ref _rendered);
            Post(frame.Screen, raw);
            return;
        }

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

    /// <summary>Wraps a raw BGRA buffer (portrait, native size) in an owned WriteableBitmap. The upscaling
    /// happens later at draw time — on the GPU via a GpuScreen shader (or the CPU fallback there).</summary>
    private WriteableBitmap? WrapBgra(byte[] bgra, int w, int h, PixelFormat fmt, AlphaFormat alpha)
    {
        if (w <= 0 || h <= 0 || bgra.Length < checked(w * h * 4)) return null;
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), fmt, alpha);
        using (var fb = wb.Lock())
        {
            int rowBytes = w * 4;
            if (fb.RowBytes == rowBytes)
                Marshal.Copy(bgra, 0, fb.Address, rowBytes * h);
            else
                for (int y = 0; y < h; y++)
                    Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(fb.Address, y * fb.RowBytes), rowBytes);
        }
        return wb;
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

    private Bitmap? DecodeAndAdjust(byte[] jpeg, (double b, double c, double s, double hl, double sh) col)
    {
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            var (b, c, s, hl, sh) = col;
            // Fast path only when there's no per-pixel work at all — an active upscaler needs the buffer.
            if (b == 0 && c == 1 && s == 1 && hl == 0 && sh == 0 && _upscale == UpscaleFilter.None)
                return new Bitmap(ms);

            using var decoded = new Bitmap(ms);
            var fmt = decoded.Format ?? PixelFormat.Bgra8888;
            bool isRgba = fmt == PixelFormat.Rgba8888;
            int w = decoded.PixelSize.Width, h = decoded.PixelSize.Height;
            int stride = w * 4;
            var px = new byte[checked(h * stride)];
            var gch = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { decoded.CopyPixels(new PixelRect(0, 0, w, h), gch.AddrOfPinnedObject(), px.Length, stride); }
            finally { gch.Free(); }

            ApplyColorAdjust(px, b, c, s, hl, sh, isRgba);   // no-op when params are identity
            return WrapBgra(px, w, h, fmt, AlphaFormat.Premul);
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
            SetScreenSource(Screen.Top, img);
            if (S.AmbientGlow) AmbientImage.Source = img;   // live ambilight: same frame, blurred backdrop
            prev?.Dispose();
        }
        else
        {
            var prev = _lastBottomBmp; _lastBottomBmp = img;
            SetScreenSource(Screen.Bottom, img);
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

        // Stream-drop watchdog: UDP (NTR) has no drop signal, so treat a few seconds with no
        // received frames as a lost stream and trigger the reconnect path when Try Reconnect is on.
        _staleSecs = rec == 0 ? _staleSecs + 1 : 0;
        if (_staleSecs >= 5 && S.TryReconnect && !_disposed)
        {
            _staleSecs = 0;
            OnFailed("Stream lost");
        }
    }

    // ===================== Teardown =====================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        // While selecting an OCR region, swallow keys; Esc cancels the selection.
        if (_ocrActive)
        {
            e.Handled = true;
            if (key == Key.Escape) CancelOcr();
            return;
        }

        // OCR result panel open: Esc closes it; other keys still reach the editable text box.
        if (OcrResultPanel.IsVisible)
        {
            if (key == Key.Escape) { e.Handled = true; CloseOcrResult(); }
            return;
        }

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
            case ShortcutAction.CopyText: StartOcrSelection(); break;
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

    // ===================== OCR: copy text from the screen =====================

    private void StartOcrSelection()
    {
        if (_ocrActive) return;
        if (_lastTopBmp == null && _lastBottomBmp == null) { ShowToast("No frame to read yet"); return; }
        // Freeze the frames shown right now as owned copies (the live bitmaps get disposed on the next frame).
        _ocrSnapTop = _lastTopBmp != null ? CopyRegion(_lastTopBmp, Full(_lastTopBmp)) : null;
        _ocrSnapBottom = _lastBottomBmp != null ? CopyRegion(_lastBottomBmp, Full(_lastBottomBmp)) : null;
        _ocrActive = true;
        OcrRect.IsVisible = false;
        OcrHint.IsVisible = true;
        OcrOverlay.IsVisible = true;
    }

    private void CancelOcr()
    {
        if (!_ocrActive) return;
        _ocrActive = false;
        OcrOverlay.IsVisible = false;
        OcrRect.IsVisible = false;
        OcrHint.IsVisible = false;
        _ocrSnapTop?.Dispose(); _ocrSnapBottom?.Dispose();
        _ocrSnapTop = _ocrSnapBottom = null;
    }

    private void OcrDown(object? sender, PointerPressedEventArgs e)
    {
        if (!_ocrActive) return;
        var pt = e.GetCurrentPoint(OcrOverlay);
        if (pt.Properties.IsRightButtonPressed) { CancelOcr(); return; }
        _ocrStart = pt.Position;
        Canvas.SetLeft(OcrRect, _ocrStart.X);
        Canvas.SetTop(OcrRect, _ocrStart.Y);
        OcrRect.Width = 0; OcrRect.Height = 0;
        OcrRect.IsVisible = true;
        OcrHint.IsVisible = false;
        e.Pointer.Capture(OcrOverlay);
    }

    private void OcrMove(object? sender, PointerEventArgs e)
    {
        if (!_ocrActive || !e.GetCurrentPoint(OcrOverlay).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(OcrOverlay);
        Canvas.SetLeft(OcrRect, Math.Min(p.X, _ocrStart.X));
        Canvas.SetTop(OcrRect, Math.Min(p.Y, _ocrStart.Y));
        OcrRect.Width = Math.Abs(p.X - _ocrStart.X);
        OcrRect.Height = Math.Abs(p.Y - _ocrStart.Y);
    }

    private async void OcrUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!_ocrActive) return;
        _ocrActive = false;
        e.Pointer.Capture(null);
        OcrOverlay.IsVisible = false;
        OcrRect.IsVisible = false;
        OcrHint.IsVisible = false;

        var p = e.GetPosition(OcrOverlay);
        var sel = new Rect(Math.Min(p.X, _ocrStart.X), Math.Min(p.Y, _ocrStart.Y),
                           Math.Abs(p.X - _ocrStart.X), Math.Abs(p.Y - _ocrStart.Y));

        Bitmap? crop = sel.Width >= 6 && sel.Height >= 6 ? CropForOcr(sel) : null;   // uses the snapshots
        _ocrSnapTop?.Dispose(); _ocrSnapBottom?.Dispose();
        _ocrSnapTop = _ocrSnapBottom = null;

        if (crop == null)
        {
            if (sel.Width >= 6 && sel.Height >= 6) ShowToast("Selection wasn't over a screen");
            return;
        }
        _ocrLastCrop?.Dispose();
        _ocrLastCrop = crop;

        ShowToast("Reading text…");
        try
        {
            var text = await OcrService.RecognizeAsync(crop, S.OcrHexMode);
            if (text == null) { ShowToast(OcrService.UnavailableReason()); return; }
            text = text.Trim();
            if (text.Length == 0) { ShowToast("No text found"); return; }
            await ShowOcrResult(text);
        }
        catch (Exception ex) { ShowToast("OCR failed: " + ex.Message); }
    }

    // ===================== OCR result popup (in-window overlay) =====================

    /// <summary>Populates and shows the result panel, auto-copying the text to the clipboard.</summary>
    private async Task ShowOcrResult(string text)
    {
        OcrResultBox.Text = text;
        _settingOcrHexProgrammatically = true;
        OcrHexToggle.IsChecked = S.OcrHexMode;
        _settingOcrHexProgrammatically = false;
        _ocrResultDrag.X = 0; _ocrResultDrag.Y = 0;   // re-anchor to the bottom-right corner each time
        OcrResultPanel.IsVisible = true;

        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null) await cb.SetTextAsync(text);
        OcrResultStatus.Text = "Copied to clipboard";
    }

    private void CloseOcrResult() => OcrResultPanel.IsVisible = false;

    private async void OnOcrHexToggled(object? sender, RoutedEventArgs e)
    {
        if (_settingOcrHexProgrammatically || _ocrLastCrop == null) return;
        bool hex = OcrHexToggle.IsChecked == true;
        S.OcrHexMode = hex;
        S.Save();
        OcrResultStatus.Text = "Reading…";
        var res = await OcrService.RecognizeAsync(_ocrLastCrop, hex);
        if (res != null)
        {
            OcrResultBox.Text = res;
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(res);
            OcrResultStatus.Text = "Copied to clipboard";
        }
        else OcrResultStatus.Text = OcrService.UnavailableReason();
    }

    /// <summary>Drags the result panel from anywhere on it except the editable field / buttons / toggle,
    /// moving it via the TranslateTransform (the panel stays anchored bottom-right of the stage; dragging
    /// just offsets it from there).</summary>
    private void OcrResultDragDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(OcrResultPanel).Properties.IsLeftButtonPressed) return;
        if (IsOcrResultInteractive(e.Source as Visual)) return;
        _ocrResultDragging = true;
        _ocrResultDragStart = e.GetPosition(this);
        _ocrResultDragOrigin = new Point(_ocrResultDrag.X, _ocrResultDrag.Y);
        e.Pointer.Capture(OcrResultPanel);
    }

    private void OcrResultDragMove(object? sender, PointerEventArgs e)
    {
        if (!_ocrResultDragging) return;
        var p = e.GetPosition(this);
        _ocrResultDrag.X = _ocrResultDragOrigin.X + (p.X - _ocrResultDragStart.X);
        _ocrResultDrag.Y = _ocrResultDragOrigin.Y + (p.Y - _ocrResultDragStart.Y);
    }

    private void OcrResultDragUp(object? sender, PointerReleasedEventArgs e)
    {
        _ocrResultDragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>True if the point is over an editable/clickable control, so dragging there is suppressed.</summary>
    private static bool IsOcrResultInteractive(Visual? v)
    {
        while (v != null)
        {
            if (v is TextBox or Button or CheckBox) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    /// <summary>
    /// Maps an overlay-space selection to the screen under its center, crops that region from the frozen
    /// snapshot, and rotates it upright for OCR. Uses TransformToVisual, so it works in any Fit/% zoom and
    /// rotation. Null if the selection isn't over a screen.
    /// </summary>
    private Bitmap? CropForOcr(Rect selection)
    {
        var center = selection.Center;
        int angle = (270 + S.Rotation) % 360;
        foreach (var (img, snap) in new[] { (_imgTop, _ocrSnapTop), (_imgBottom, _ocrSnapBottom) })
        {
            if (img == null || snap == null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0) continue;
            var m = OcrOverlay.TransformToVisual(img);
            if (m == null) continue;
            var toImg = m.Value;

            var c = toImg.Transform(center);
            if (c.X < 0 || c.Y < 0 || c.X > img.Bounds.Width || c.Y > img.Bounds.Height) continue;  // center off this screen

            var p1 = toImg.Transform(selection.TopLeft);
            var p2 = toImg.Transform(selection.BottomRight);
            double sx = snap.PixelSize.Width / img.Bounds.Width, sy = snap.PixelSize.Height / img.Bounds.Height;
            double x1 = Math.Clamp(Math.Min(p1.X, p2.X) * sx, 0, snap.PixelSize.Width);
            double y1 = Math.Clamp(Math.Min(p1.Y, p2.Y) * sy, 0, snap.PixelSize.Height);
            double x2 = Math.Clamp(Math.Max(p1.X, p2.X) * sx, 0, snap.PixelSize.Width);
            double y2 = Math.Clamp(Math.Max(p1.Y, p2.Y) * sy, 0, snap.PixelSize.Height);
            int ix = (int)Math.Floor(x1), iy = (int)Math.Floor(y1);
            int iw = (int)Math.Ceiling(x2 - x1), ih = (int)Math.Ceiling(y2 - y1);
            if (ix + iw > snap.PixelSize.Width) iw = snap.PixelSize.Width - ix;
            if (iy + ih > snap.PixelSize.Height) ih = snap.PixelSize.Height - iy;
            if (iw < 2 || ih < 2) return null;

            var sub = CopyRegion(snap, new PixelRect(ix, iy, iw, ih));   // still sideways (raw frame)
            var upright = RotateUpright(sub, angle);
            if (!ReferenceEquals(upright, sub)) sub.Dispose();
            return upright;
        }
        return null;
    }

    private static PixelRect Full(Bitmap b) => new(0, 0, b.PixelSize.Width, b.PixelSize.Height);

    /// <summary>Copies a region of a bitmap into an owned WriteableBitmap.</summary>
    private static WriteableBitmap CopyRegion(Bitmap src, PixelRect region)
    {
        int w = region.Width, h = region.Height, stride = w * 4;
        var px = new byte[checked(h * stride)];
        var gch = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { src.CopyPixels(region, gch.AddrOfPinnedObject(), px.Length, stride); }
        finally { gch.Free(); }
        var fmt = src.Format ?? PixelFormat.Bgra8888;
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), fmt, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            if (fb.RowBytes == stride) Marshal.Copy(px, 0, fb.Address, px.Length);
            else for (int y = 0; y < h; y++) Marshal.Copy(px, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), stride);
        }
        return wb;
    }

    /// <summary>Rotates a crop by the display angle so text reads upright for OCR (returns the input if 0°).</summary>
    private static Bitmap RotateUpright(Bitmap crop, int angle)
    {
        angle = ((angle % 360) + 360) % 360;
        if (angle == 0) return crop;
        int w = crop.PixelSize.Width, h = crop.PixelSize.Height;
        bool swap = angle == 90 || angle == 270;
        int fw = swap ? h : w, fh = swap ? w : h;
        var rtb = new RenderTargetBitmap(new PixelSize(fw, fh), new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext())
        {
            var mtx = Matrix.CreateTranslation(-w / 2.0, -h / 2.0)
                    * Matrix.CreateRotation(angle * Math.PI / 180.0)
                    * Matrix.CreateTranslation(fw / 2.0, fh / 2.0);
            using (dc.PushTransform(mtx))
                dc.DrawImage(crop, new Rect(0, 0, w, h));
        }
        return rtb;
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
        if (S.TryReconnect) _owner.RequestReconnect();   // back to menu, which auto-retries the same IP
        else _owner.ShowConnect();
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
        AmbientImage.Source = null;   // drop the shared reference before disposing the frames
        _lastTopBmp?.Dispose(); _lastBottomBmp?.Dispose();
        _lastTopBmp = _lastBottomBmp = null;
        _ocrSnapTop?.Dispose(); _ocrSnapBottom?.Dispose(); _ocrLastCrop?.Dispose();
        _ocrSnapTop = _ocrSnapBottom = _ocrLastCrop = null;
    }
}
