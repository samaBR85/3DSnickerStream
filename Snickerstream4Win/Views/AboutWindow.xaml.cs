using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Snickerstream4Win.Models;

namespace Snickerstream4Win.Views;

public partial class AboutWindow : Window
{
    private readonly Func<Task<string>> _checkNow;

    public AboutWindow(Func<Task<string>> checkNow)
    {
        InitializeComponent();
        _checkNow = checkNow;

        VersionText.Text = $"Version {AppInfo.Version}";
        TglCheckUpdates.IsChecked = App.Settings.CheckUpdatesOnStartup;

        TglCheckUpdates.Click += (_, _) =>
        {
            App.Settings.CheckUpdatesOnStartup = TglCheckUpdates.IsChecked == true;
            App.Settings.Save();
        };
        BtnCheckNow.Click += async (_, _) =>
        {
            BtnCheckNow.IsEnabled = false;
            CheckStatus.Text = "Checking…";
            CheckStatus.Text = await _checkNow();
            BtnCheckNow.IsEnabled = true;
        };
        BtnClose.Click += (_, _) => Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }
}
