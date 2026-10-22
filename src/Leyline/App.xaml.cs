using Microsoft.UI.Xaml;
using Leyline.Services;
using Leyline.ViewModels;
using Leyline.Views;
using Leyline.Vst;

namespace Leyline;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] cliArgs = Environment.GetCommandLineArgs();

        // 0. Carry settings and driver-node records over from the app's former name (VoidPatch)
        LegacyMigration.Run();

        // 1. Handle isolated VST probe CLI mode
        int probeIdx = Array.FindIndex(cliArgs, a => a.Equals("--probe-vst", StringComparison.OrdinalIgnoreCase));
        if (probeIdx >= 0 && probeIdx + 1 < cliArgs.Length)
        {
            int code = VstScanner.RunIsolatedVstProbeCli(cliArgs[probeIdx + 1]);
            Environment.Exit(code);
            return;
        }

        // 2. Elevated driver install / removal (Leyline re-launches itself with these; see LeylineDriverService)
        if (cliArgs.Contains("--install-driver", StringComparer.OrdinalIgnoreCase))
        {
            LeylineDriverService.Log("--install-driver: started");
            bool ok = LeylineDriverService.InstallElevated(out string installMessage);
            LeylineDriverService.Log($"--install-driver: {(ok ? "ok" : "FAILED")} - {installMessage}");
            Environment.Exit(ok ? 0 : 1);
            return;
        }

        if (cliArgs.Contains("--uninstall-driver", StringComparer.OrdinalIgnoreCase))
        {
            LeylineDriverService.Log("--uninstall-driver: started");
            bool ok = LeylineDriverService.UninstallElevated(out string uninstallMessage);
            LeylineDriverService.Log($"--uninstall-driver: {(ok ? "ok" : "FAILED")} - {uninstallMessage}");
            Environment.Exit(ok ? 0 : 1);
            return;
        }

        _singleInstanceMutex = new Mutex(true, "Global\\Leyline_AudioEngine_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Win32DialogService.ShowMessage(
                IntPtr.Zero,
                "Leyline is already running (check your Windows System Tray).",
                "Leyline Audio Mixer",
                Win32DialogService.MB_OK | Win32DialogService.MB_ICONINFORMATION);
            Environment.Exit(0);
            return;
        }

        // Before the view model, whose startup plugin scan can report progress
        NotificationService.Initialize();

        var vm = new MainViewModel();
        NotificationService.IsEnabled = () => vm.Config.ShowNotifications;
        _mainWindow = new MainWindow(vm);

        bool minimized = cliArgs.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        _mainWindow.Activate();
        if (minimized)
            _mainWindow.HideToTray();
        NotificationService.ShowStarted(inTray: minimized);
    }
}
