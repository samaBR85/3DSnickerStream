using Avalonia.Controls;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;

namespace SnickerstreamV2.Views;

// Etapa 4 placeholder — the real streaming view lands in Etapa 5.
public class StreamView : UserControl
{
    private readonly MainWindow _owner;
    private readonly IStreamClient _client;

    public StreamView(MainWindow owner, IStreamClient client, Protocol protocol)
    {
        _owner = owner;
        _client = client;

        var btn = new Button { Content = "Disconnect", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        btn.Classes.Add("danger");
        btn.Click += (_, _) => Disconnect();

        Content = new StackPanel
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Streaming (placeholder)…", FontSize = 18, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                btn
            }
        };
    }

    private void Disconnect()
    {
        try { _client.Stop(); } catch { }
        _client.Dispose();
        _owner.ShowConnect();
    }
}
