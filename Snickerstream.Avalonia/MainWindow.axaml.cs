using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;
using SnickerstreamV2.Views;

namespace SnickerstreamV2;

public partial class MainWindow : Window
{
    public bool IsFullscreen { get; private set; }
    private WindowState _preFullscreenState = WindowState.Normal;

    // Clean mode (hide UI): borderless, window fitted to the screen group's aspect.
    public bool IsCleanMode { get; private set; }
    private SystemDecorations _preCleanDecorations;
    private bool _preCleanCanResize;
    private WindowState _preCleanState;
    private double _preCleanWidth, _preCleanHeight, _preCleanMinW, _preCleanMinH;
    private PixelPoint _preCleanPos;

    public MainWindow()
    {
        InitializeComponent();
        ShowConnect();
    }

    public AppSettings Settings => App.Settings;

    private bool _reconnectRequested;
    /// <summary>Return to the connect screen and ask it to auto-retry (Try Reconnect after a stream drop).</summary>
    public void RequestReconnect() { _reconnectRequested = true; ShowConnect(); }
    public bool ConsumeReconnect() { var r = _reconnectRequested; _reconnectRequested = false; return r; }

    private bool _startupScanConsumed;
    /// <summary>True only for the first Connect screen (app launch), so Scan-on-Startup doesn't refire
    /// every time the user returns to the menu (which would loop with Auto-Connect).</summary>
    public bool ConsumeStartupScan()
    {
        if (_startupScanConsumed) return false;
        _startupScanConsumed = true;
        return true;
    }

    public void ShowConnect()
    {
        if (IsCleanMode) ExitCleanMode();
        if (IsFullscreen) ToggleFullscreen();
        RootHost.Children.Clear();
        RootHost.Children.Add(new ConnectView(this));
    }

    public void ShowStream(IStreamClient client, Protocol protocol)
    {
        RootHost.Children.Clear();
        RootHost.Children.Add(new StreamView(this, client, protocol));
    }

    /// <summary>
    /// Grow-only window fit for % zoom: enlarge the window so the zoomed content
    /// (<paramref name="contentW"/>×<paramref name="contentH"/> DIPs, plus the control bar and the stage
    /// margin) is fully visible, clamped to the monitor work area. Never shrinks.
    /// </summary>
    public void FitToContent(double contentW, double contentH, double barH)
    {
        if (WindowState != WindowState.Normal) return;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1.0;
        double waW = (screen?.WorkingArea.Width ?? 1920) / scaling;
        double waH = (screen?.WorkingArea.Height ?? 1080) / scaling;

        // Window chrome (title bar + borders) = frame size minus client size, in DIPs.
        var client = ClientSize;
        var frame = FrameSize ?? client;
        double chromeW = Math.Max(0, frame.Width - client.Width);
        double chromeH = Math.Max(0, frame.Height - client.Height);

        const double stageMargin = 56;   // ScreensHost margin (28 × 2)
        double neededW = contentW + stageMargin + chromeW;
        double neededH = contentH + stageMargin + barH + chromeH;

        Width = Math.Min(waW, Math.Max(Width, neededW));
        Height = Math.Min(waH, Math.Max(Height, neededH));
    }

    /// <summary>Toggle borderless fullscreen (Avalonia handles chrome via WindowState.FullScreen).</summary>
    public void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullscreenState;
            IsFullscreen = false;
        }
        else
        {
            _preFullscreenState = WindowState;
            WindowState = WindowState.FullScreen;
            IsFullscreen = true;
        }
    }

    /// <summary>
    /// Clean mode: a borderless window sized to the screen group's aspect (<paramref name="gw"/>×
    /// <paramref name="gh"/> design units), so the screens fill the window with no chrome / black bars.
    /// </summary>
    public void EnterCleanMode(double gw, double gh)
    {
        if (IsCleanMode) return;
        _preCleanDecorations = SystemDecorations;
        _preCleanCanResize = CanResize;
        _preCleanState = WindowState;
        _preCleanWidth = Width; _preCleanHeight = Height;
        _preCleanMinW = MinWidth; _preCleanMinH = MinHeight;
        _preCleanPos = Position;
        IsCleanMode = true;

        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        MinWidth = 1; MinHeight = 1;
        if (gw <= 0 || gh <= 0) return;
        double aspect = gw / gh;

        // Keep the current height; set width to the group aspect so the screens are flush to the sides.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCleanMode) return;
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double scaling = screen?.Scaling ?? 1.0;
            double waW = (screen?.WorkingArea.Width ?? 1920) / scaling;
            double waH = (screen?.WorkingArea.Height ?? 1080) / scaling;
            double h = Math.Min(ClientSize.Height > 0 ? ClientSize.Height : Height, waH);
            double w = h * aspect;
            if (w > waW) { w = waW; h = w / aspect; }
            Width = w; Height = h;
        }, DispatcherPriority.Loaded);
    }

    public void ExitCleanMode()
    {
        if (!IsCleanMode) return;
        IsCleanMode = false;
        SystemDecorations = _preCleanDecorations;
        CanResize = _preCleanCanResize;
        MinWidth = _preCleanMinW; MinHeight = _preCleanMinH;
        Width = _preCleanWidth; Height = _preCleanHeight;
        Position = _preCleanPos;
        WindowState = _preCleanState;
    }
}
