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
}
