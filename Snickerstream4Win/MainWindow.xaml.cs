using System.Windows;
using System.Windows.Media.Imaging;
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

    public MainWindow()
    {
        InitializeComponent();
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        ShowConnect();
    }

    public AppSettings Settings => App.Settings;

    public void ShowConnect()
    {
        if (IsFullscreen) ToggleFullscreen();
        RootHost.Children.Clear();
        RootHost.Children.Add(new ConnectView(this));
    }

    public void ShowStream(IStreamClient client, Protocol protocol)
    {
        RootHost.Children.Clear();
        RootHost.Children.Add(new StreamView(this, client, protocol));
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
