using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Leyline.Services;

/// <summary>
/// Windows notifications: Leyline started, Leyline still running in the tray, and plugin scan progress.
/// Clicking any of them opens the Leyline window (<see cref="OpenRequested"/>).
/// </summary>
public static class NotificationService
{
    /// <summary>
    /// Leyline's AppUserModelID; installer\Leyline.iss gives its shortcuts the same one. Without an explicit ID,
    /// notifications are registered under one derived from the exe path, so each install location (and the
    /// Debug build) became a separate notification app, and Windows kept showing that app's first icon.
    /// </summary>
    public const string AppUserModelId = "AsterionXeity.Leyline";

    private const string Group = "Leyline";
    private const string ScanTag = "vst-scan";

    private static bool _registered;
    private static bool _scanShown;
    private static uint _scanSequence;

    /// <summary>Checked before each notification (Settings → General → Show Windows notifications).</summary>
    public static Func<bool> IsEnabled { get; set; } = () => true;

    /// <summary>Raised on a background thread when a notification is clicked.</summary>
    public static event Action? OpenRequested;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>
    /// Registers Leyline with Windows notifications. Call once, in the main instance only, before creating the
    /// window (the app ID also groups Leyline's taskbar button).
    /// </summary>
    public static void Initialize()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

            if (!AppNotificationManager.IsSupported())
                return;

            // Subscribe before Register so clicks that launched the app are not missed
            AppNotificationManager.Default.NotificationInvoked += (_, _) => OpenRequested?.Invoke();

            AppNotificationManager.Default.Register("Leyline", new Uri(IconGenerator.PngPath));
            _registered = true;
        }
        catch
        {
            _registered = false;
        }
    }

    public static void Shutdown()
    {
        if (!_registered)
            return;
        try
        {
            AppNotificationManager.Default.RemoveByTagAndGroupAsync(ScanTag, Group).AsTask().Wait(500);
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
        }
        _registered = false;
    }

    public static void ShowStarted(bool inTray)
    {
        Show(new AppNotificationBuilder()
            .AddText("Leyline is running")
            .AddText(inTray
                ? "Your audio is being routed. Leyline is in the system tray."
                : "Your audio is being routed."));
    }

    public static void ShowRunningInBackground()
    {
        Show(new AppNotificationBuilder()
            .AddText("Leyline is still running")
            .AddText("Your audio keeps routing in the background. Open Leyline from the system tray icon."));
    }

    /// <summary>Plugin scan progress; <paramref name="done"/> of <paramref name="total"/> plugins checked.</summary>
    public static void ReportScanProgress(int done, int total, string lastPlugin)
    {
        if (!_registered || !IsEnabled() || total <= 0)
            return;

        try
        {
            // The progress bar's title, status and value lines are single lines: only short values go there
            var data = new AppNotificationProgressData(Interlocked.Increment(ref _scanSequence))
            {
                Value = (double)done / total,
                ValueStringOverride = $"{done}/{total} plugins",
                Status = done == 0 ? "Starting..." : lastPlugin
            };

            if (!_scanShown)
            {
                _scanShown = true;
                var notification = new AppNotificationBuilder()
                    .AddText("Scanning plugins")
                    .AddText("Sorting effects from instruments")
                    .AddProgressBar(new AppNotificationProgressBar()
                        .BindValue()
                        .BindValueStringOverride()
                        .BindStatus())
                    .AddArgument("action", "open")
                    .MuteAudio()
                    .SetTag(ScanTag)
                    .SetGroup(Group)
                    .BuildNotification();
                notification.Progress = data;
                AppNotificationManager.Default.Show(notification);
            }
            else
            {
                _ = AppNotificationManager.Default.UpdateAsync(data, ScanTag, Group);
            }
        }
        catch
        {
        }
    }

    /// <summary>Replaces the progress notification with the scan result, if a progress notification was shown.</summary>
    public static void CompleteScan(int effects, int instruments, int blocked)
    {
        if (!_scanShown)
            return;
        _scanShown = false;

        Show(new AppNotificationBuilder()
            .AddText("Plugin scan finished")
            .AddText($"{effects} effects, {instruments} instruments, {blocked} blocked.")
            .SetTag(ScanTag)
            .SetGroup(Group));
    }

    private static void Show(AppNotificationBuilder builder)
    {
        if (!_registered || !IsEnabled())
            return;

        try
        {
            // Leyline is an audio app: its notifications stay silent
            AppNotificationManager.Default.Show(builder
                .AddArgument("action", "open")
                .MuteAudio()
                .BuildNotification());
        }
        catch
        {
        }
    }
}
