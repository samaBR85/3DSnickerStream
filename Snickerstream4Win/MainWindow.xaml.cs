using System.Windows;
using Snickerstream4Win.Models;
using Snickerstream4Win.Net;
using Snickerstream4Win.Views;

namespace Snickerstream4Win;

public partial class MainWindow : Window
{
    private WindowState _preFullscreenState = WindowState.Normal;
    private WindowStyle _preFullscreenStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _preFullscreenResize = ResizeMode.CanResize;
    public bool IsFullscreen { get; private set; }

    // Clean mode (hide UI): borderless, window fitted to the top screen's width.
    private WindowStyle _preCleanStyle;
    private ResizeMode _preCleanResize;
    private WindowState _preCleanState;
    private double _preCleanWidth, _preCleanHeight, _preCleanLeft, _preCleanTop;
    private double _preCleanMinW, _preCleanMinH;
    public bool IsCleanMode { get; private set; }

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
    /// <summary>True only for the first Connect screen (app launch), so Scan-on-Startup doesn't fire
    /// again every time the user returns to the menu (which would loop with Auto-Connect).</summary>
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
        MinWidth = 980; MinHeight = 720;         // the menu needs its two columns
        RootHost.Children.Clear();
        RootHost.Children.Add(new ConnectView(this));
    }

    public void ShowStream(IStreamClient client, Protocol protocol)
    {
        MinWidth = 400; MinHeight = 460;         // let the stream window shrink to the 3DS width; the bar reflows
        RootHost.Children.Clear();
        RootHost.Children.Add(new StreamView(this, client, protocol));
    }

    /// <summary>
    /// Clean mode: drop the window chrome and fit the window width to the top screen's aspect
    /// (so the top screen spans the width). <paramref name="groupAspect"/> = groupWidth/groupHeight.
    /// </summary>
    /// <summary>
    /// Clean mode: a borderless window sized to the screen group's exact aspect (<paramref name="gw"/>×
    /// <paramref name="gh"/> design units), scaled to fit the monitor work area and centered — so the
    /// screens fill the window with no black border, regardless of the previous window size/state.
    /// </summary>
    public void EnterCleanMode(double gw, double gh)
    {
        if (IsCleanMode) return;
        _preCleanStyle = WindowStyle;
        _preCleanResize = ResizeMode;
        _preCleanState = WindowState;
        _preCleanWidth = Width; _preCleanHeight = Height;
        _preCleanLeft = Left; _preCleanTop = Top;
        _preCleanMinW = MinWidth; _preCleanMinH = MinHeight;
        IsCleanMode = true;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 1; MinHeight = 1;                        // allow shrinking below the menu's minimums
        if (gw <= 0 || gh <= 0) return;
        double aspect = gw / gh;

        // Keep the current height (don't grow); set the width to the group aspect so the screens are
        // flush to the sides with no black bars. Measured after the borderless style is applied.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (!IsCleanMode) return;
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            double h = Math.Min(RootHost.ActualHeight > 0 ? RootHost.ActualHeight : ActualHeight, wa.Height);
            double w = h * aspect;
            if (w > wa.Width) { w = wa.Width; }               // never wider than the screen
            double cx = Left + Width / 2;
            Width = w;
            Left = Math.Max(wa.Left, Math.Min(cx - w / 2, wa.Left + wa.Width - w));
        });
    }

    public void ExitCleanMode()
    {
        if (!IsCleanMode) return;
        IsCleanMode = false;
        WindowStyle = _preCleanStyle;
        ResizeMode = _preCleanResize;
        MinWidth = _preCleanMinW; MinHeight = _preCleanMinH;
        Width = _preCleanWidth; Height = _preCleanHeight;
        Left = _preCleanLeft; Top = _preCleanTop;
        WindowState = _preCleanState;
    }

    /// <summary>
    /// Grow-only window fit for % zoom: enlarge the window so the zoomed content (<paramref name="contentW"/>×
    /// <paramref name="contentH"/>, plus the control bar) is fully visible, clamped to the work area and
    /// re-centered. Never shrinks (keeps the control bar usable / avoids the MinWidth clamp).
    /// </summary>
    public void FitToContent(double contentW, double contentH, double barH)
    {
        if (IsCleanMode || IsFullscreen || WindowState != WindowState.Normal) return;
        var wa = SystemParameters.WorkArea;
        double chromeW = Math.Max(0, ActualWidth - RootHost.ActualWidth);    // borders
        double chromeH = Math.Max(0, ActualHeight - RootHost.ActualHeight);  // title + borders
        const double stageMargin = 48;                                       // ScreensHost margin (24 × 2)
        double neededW = contentW + stageMargin + chromeW;
        double neededH = contentH + stageMargin + barH + chromeH;
        double targetW = Math.Min(wa.Width, Math.Max(Width, neededW));
        double targetH = Math.Min(wa.Height, Math.Max(Height, neededH));
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = targetW; Height = targetH;
        Left = Math.Max(wa.Left, Math.Min(cx - targetW / 2, wa.Left + wa.Width - targetW));
        Top = Math.Max(wa.Top, Math.Min(cy - targetH / 2, wa.Top + wa.Height - targetH));
    }

    public void ToggleFullscreen()
    {
        if (!IsFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _preFullscreenResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;        // reset so Maximized covers taskbar
            WindowState = WindowState.Maximized;
            IsFullscreen = true;
        }
        else
        {
            WindowStyle = _preFullscreenStyle;
            ResizeMode = _preFullscreenResize;
            WindowState = _preFullscreenState;
            IsFullscreen = false;
        }
    }
}
