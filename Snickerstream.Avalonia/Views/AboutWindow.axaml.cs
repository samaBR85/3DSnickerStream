using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using SnickerstreamV2.Models;

namespace SnickerstreamV2.Views;

public partial class AboutWindow : Window
{
    private readonly Func<Task<string>>? _checkNow;

    public AboutWindow() => InitializeComponent();   // designer

    public AboutWindow(Func<Task<string>> checkNow)
    {
        InitializeComponent();
        _checkNow = checkNow;

        VersionText.Text = $"Version {AppInfo.Version}";
        TglCheckUpdates.IsChecked = App.Settings.CheckUpdatesOnStartup;

        TglCheckUpdates.IsCheckedChanged += (_, _) =>
        {
            App.Settings.CheckUpdatesOnStartup = TglCheckUpdates.IsChecked == true;
            App.Settings.Save();
        };
        BtnCheckNow.Click += async (_, _) =>
        {
            if (_checkNow == null) return;
            BtnCheckNow.IsEnabled = false;
            CheckStatus.Text = "Checking…";
            CheckStatus.Text = await _checkNow();
            BtnCheckNow.IsEnabled = true;
        };
        BtnClose.Click += (_, _) => Close();
        LinkOriginal.PointerPressed += (_, _) => OpenUrl("https://github.com/RattletraPM");
        LinkRevision.PointerPressed += (_, _) => OpenUrl("https://github.com/samaBR85");
        LinkNtrHr.PointerPressed += (_, _) => OpenUrl("https://github.com/xzn/ntr-hr");
        LinkNtrViewer.PointerPressed += (_, _) => OpenUrl("https://github.com/xzn/ntrviewer-hr");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
