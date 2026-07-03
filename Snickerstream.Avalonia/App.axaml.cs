using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SnickerstreamV2.Models;

namespace SnickerstreamV2;

public partial class App : Application
{
    /// <summary>Process-wide persisted settings (loaded once, saved on change).</summary>
    public static AppSettings Settings { get; } = AppSettings.Load();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
