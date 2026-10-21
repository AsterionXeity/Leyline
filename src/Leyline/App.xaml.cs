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

        // 2. Handle elevated CLI driver provisioning commands
        if (cliArgs.Contains("--provision-driver", StringComparer.OrdinalIgnoreCase))
        {
            bool ok = LeylineDriverService.ProvisionLeylineEndpointsElevated(out _);
            Environment.Exit(ok ? 0 : 1);
            return;
        }

        if (cliArgs.Contains("--restore-driver", StringComparer.OrdinalIgnoreCase))
        {
            bool ok = LeylineDriverService.RestoreOriginalEndpointsElevated(out _);
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

        var vm = new MainViewModel();
        _mainWindow = new MainWindow(vm);

        if (cliArgs.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            _mainWindow.Activate();
            _mainWindow.HideToTray();
        }
        else
        {
            _mainWindow.Activate();
        }
    }
}
