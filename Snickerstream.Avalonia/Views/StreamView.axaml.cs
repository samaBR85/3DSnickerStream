using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;

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

    private long _received, _rendered;
    private DispatcherTimer? _fpsTimer;
    private bool _loaded, _disposed;

    public StreamView() => InitializeComponent();   // designer / runtime loader only

    public StreamView(MainWindow owner, IStreamClient client, Protocol protocol)
    {
        _owner = owner;
        _client = client;
        _protocol = protocol;
        InitializeComponent();

        CmbLayout.SelectedIndex = (int)S.Layout;
        CmbFilter.SelectedIndex = (int)S.Interpolation;
        CmbRot.SelectedIndex = S.Rotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };

        BuildLayout();

        CmbLayout.SelectionChanged += (_, _) => { if (_loaded) { S.Layout = (ScreenLayout)CmbLayout.SelectedIndex; S.Save(); BuildLayout(); } };
        CmbFilter.SelectionChanged += (_, _) => { if (_loaded) { S.Interpolation = (Interpolation)CmbFilter.SelectedIndex; S.Save(); ApplyFilter(); } };
        CmbRot.SelectionChanged += (_, _) => { if (_loaded) { S.Rotation = CmbRot.SelectedIndex * 90; S.Save(); BuildLayout(); } };
        BtnDisconnect.Click += (_, _) => Disconnect();
        _loaded = true;

        _client.FrameReady += OnFrameReady;
        _client.Failed += OnFailed;
        _owner.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += OnFpsTick;
        _fpsTimer.Start();
    }

    // ===================== Layout =====================

    private void BuildLayout()
    {
        ScreensHost.Children.Clear();
        _imgTop = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };
        _imgBottom = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };

        var layout = S.Layout;
        if (_protocol == Protocol.HzMod) layout = ScreenLayout.TopOnly;   // HzMod streams top only

        Control group = layout switch
        {
            ScreenLayout.TopOnly => Rotated(_imgTop),
            ScreenLayout.BottomOnly => Rotated(_imgBottom),
            ScreenLayout.SideBySide => Group(horizontal: true),
            _ => Group(horizontal: false),   // Stacked
        };

        ScreensHost.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = group });

        // reapply the current frames (owned bitmaps; do NOT dispose here)
        if (_lastTopBmp != null) _imgTop.Source = _lastTopBmp;
        if (_lastBottomBmp != null) _imgBottom.Source = _lastBottomBmp;
        ApplyFilter();
    }

    private Control Group(bool horizontal)
    {
        var sp = new StackPanel
        {
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = 10,   // scaled by the Viewbox → proportional gap between screens
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(Rotated(_imgTop!));
        sp.Children.Add(Rotated(_imgBottom!));
        return sp;
    }

    // 270deg upright correction is baked in; user Rotation is an offset on top. Layout-transform, not pixels.
    // The image is wrapped in a framed Border so the rounded frame + shadow rotate with the screen.
    private Control Rotated(Image img)
    {
        var framed = new Border { Classes = { "screen" }, Child = img };
        return new LayoutTransformControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = new RotateTransform((270 + S.Rotation) % 360),
            Child = framed
        };
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

        Bitmap bmp;
        try { bmp = new Bitmap(new MemoryStream(frame.Jpeg, writable: false)); }
        catch { return; }
        Interlocked.Increment(ref _rendered);

        bool needPost;
        Bitmap? superseded;
        lock (_gate)
        {
            if (frame.Screen == Screen.Top)
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
        if (needPost) Dispatcher.UIThread.Post(() => Present(frame.Screen));
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

        lock (_gate)
        {
            _pendingTop?.Dispose(); _pendingBottom?.Dispose();
            _pendingTop = _pendingBottom = null;
        }
        _lastTopBmp?.Dispose(); _lastBottomBmp?.Dispose();
        _lastTopBmp = _lastBottomBmp = null;
    }
}
