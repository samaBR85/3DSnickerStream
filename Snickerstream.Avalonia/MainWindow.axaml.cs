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
    private bool _streaming;

    public MainWindow()
    {
        InitializeComponent();
        ShowConnect();   // forces the snug menu size
    }

    public AppSettings Settings => App.Settings;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_streaming) SaveStreamSize();   // remember the stream window size only
        base.OnClosing(e);
    }

    private void SaveStreamSize()
    {
        if (WindowState == WindowState.Normal && !IsCleanMode && !IsFullscreen)
        {
            App.Settings.WindowWidth = Width;
            App.Settings.WindowHeight = Height;
            App.Settings.Save();
        }
    }

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
        if (_streaming) SaveStreamSize();   // remember the stream size on disconnect
        _streaming = false;

        RootHost.Children.Clear();
        RootHost.Children.Add(new ConnectView(this));

        // Fit the window exactly to the menu — no leftover side/bottom space, DPI-independent — and lock it
        // there: the menu isn't a canvas, so disable manual resize (which could otherwise shrink it until the
        // form is clipped). SizeToContent still re-fits when the form grows/shrinks (NTR↔HzMod, chips…).
        MinWidth = 400; MinHeight = 400;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
    }

    public void ShowStream(IStreamClient client, Protocol protocol)
    {
        _streaming = true;
        SizeToContent = SizeToContent.Manual;   // stream view is freely resizable
        CanResize = true;                        // (the menu locks this off; the stream needs it back)
        // The stream bar needs room to fit its groups on ~2 rows: restore the remembered stream size,
        // else grow the window if it's too narrow.
        MinWidth = 720; MinHeight = 560;
        if (WindowState == WindowState.Normal)
        {
            if (App.Settings.WindowWidth >= 720 && App.Settings.WindowHeight >= 500)
            {
                Width = App.Settings.WindowWidth;
                Height = App.Settings.WindowHeight;
            }
            else if (Width < 1100) Width = 1100;
        }
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

        // Keep the current height; set width to the group aspect so the screens are flush to the sides,
        // then keep the window centered around WHERE IT WAS (it otherwise shrinks toward the top-left).
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCleanMode) return;
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double scaling = screen?.Scaling ?? 1.0;
            var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            double waW = wa.Width / scaling, waH = wa.Height / scaling;

            // Current window center (physical), captured before the resize.
            var pos = Position;
            double cx = pos.X + ClientSize.Width * scaling / 2;
            double cy = pos.Y + ClientSize.Height * scaling / 2;

            double h = Math.Min(ClientSize.Height > 0 ? ClientSize.Height : Height, waH);
            double w = h * aspect;
            if (w > waW) { w = waW; h = w / aspect; }
            Width = w; Height = h;

            int wp = (int)(w * scaling), hp = (int)(h * scaling);
            int nx = Math.Max(wa.X, Math.Min((int)(cx - wp / 2.0), wa.X + wa.Width - wp));
            int ny = Math.Max(wa.Y, Math.Min((int)(cy - hp / 2.0), wa.Y + wa.Height - hp));
            Position = new PixelPoint(nx, ny);
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
