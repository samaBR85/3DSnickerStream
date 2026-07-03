using Avalonia.Controls;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;
using SnickerstreamV2.Views;

namespace SnickerstreamV2;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowConnect();
    }

    public AppSettings Settings => App.Settings;

    public void ShowConnect()
    {
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
}
