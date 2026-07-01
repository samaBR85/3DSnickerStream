using System.Runtime.InteropServices;
using System.Windows;
using Snickerstream4Win.Models;

namespace Snickerstream4Win;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless self-test path: 3DSnickerStream.exe --selftest
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            if (!AttachConsole(-1)) AllocConsole();
            int code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        base.OnStartup(e);
        Settings = AppSettings.Load();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Settings.Save(); } catch { }
        base.OnExit(e);
    }
}
