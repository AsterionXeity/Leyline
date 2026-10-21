using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoidPatch.Models;

namespace VoidPatch.Audio;

public static unsafe class PolicyConfigClient
{
    private const string DevInterfaceAudioRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string DevInterfaceAudioCapture = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private static readonly HashSet<string> ExcludedBackgroundProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "VoidPatch",
        "nvcontainer",
        "nvsphelper64",
        "NVIDIA Share",
        "NVIDIA Overlay",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "SearchUI",
        "TextInputHost",
        "RuntimeBroker",
        "ApplicationFrameHost",
        "SystemSettings",
        "LockApp",
        "Widgets",
        "WidgetService",
        "PhoneExperienceHost",
        "CrossDeviceService",
        "GameBar",
        "GameBarFTServer",
        "GameBarPresenceWriter",
        "XboxPcAppFT",
        "SecurityHealthSystray",
        "steamwebhelper",
        "steamerrorreporter",
        "steamservice",
        "EpicWebHelper",
        "CrashReportClient",
        "conhost",
        "svchost",
        "audiodg",
        "explorer",
        "taskhostw",
        "dllhost",
        "backgroundTaskHost",
        "sihost",
        "dwm",
        "ctfmon",
        "csrss",
        "winlogon",
        "smartscreen",
        "spoolsv",
        "wlanext",
        "dasHost",
        "WmiPrvSE",
        "cmd",
        "powershell",
        "pwsh",
        "WindowsTerminal",
        "OpenConsole"
    };

    /// <summary>
    /// Routes a specific running application (by ProcessId) to a target Windows audio output endpoint.
    /// Pass empty/null targetDeviceId to reset the app back to the Windows Default Output.
    /// </summary>
    public static bool SetApplicationOutputEndpoint(uint processId, string? targetDeviceId)
    {
        if (processId == 0) return false;

        IntPtr classIdHString = IntPtr.Zero;
        IntPtr deviceHString = IntPtr.Zero;
        IntPtr inspectablePtr = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            if (WindowsCreateString(className, (uint)className.Length, out classIdHString) != 0)
                return false;

            Guid inspectableIid = IID_IInspectable;
            if (RoGetActivationFactory(classIdHString, ref inspectableIid, out inspectablePtr) != 0 || inspectablePtr == IntPtr.Zero)
                return false;

            void** inspVtbl = *(void***)inspectablePtr;
            var getIids = (delegate* unmanaged[Stdcall]<IntPtr, out uint, out Guid*, int>)inspVtbl[3];
            if (getIids(inspectablePtr, out uint iidCount, out Guid* iidsPtr) != 0 || iidCount == 0 || iidsPtr == null)
                return false;

            Guid factoryIid = iidsPtr[0];
            CoTaskMemFree((IntPtr)iidsPtr);

            var qi = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)inspVtbl[0];
            if (qi(inspectablePtr, ref factoryIid, out factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
                return false;

            if (!string.IsNullOrWhiteSpace(targetDeviceId))
            {
                string longDeviceId = $@"\\?\SWD#MMDEVAPI#{targetDeviceId}{DevInterfaceAudioRender}";
                WindowsCreateString(longDeviceId, (uint)longDeviceId.Length, out deviceHString);
            }

            void** factoryVtbl = *(void***)factoryPtr;
            var setEndpoint = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)factoryVtbl[20];

            int hr0 = setEndpoint(factoryPtr, processId, 0, 0, deviceHString);
            int hr1 = setEndpoint(factoryPtr, processId, 0, 1, deviceHString);
            setEndpoint(factoryPtr, processId, 0, 2, deviceHString);

            return hr0 == 0 || hr1 == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (deviceHString != IntPtr.Zero) WindowsDeleteString(deviceHString);
            if (classIdHString != IntPtr.Zero) WindowsDeleteString(classIdHString);
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (inspectablePtr != IntPtr.Zero) Marshal.Release(inspectablePtr);
        }
    }

    public static bool ResetAllApplicationEndpoints()
    {
        IntPtr classIdHString = IntPtr.Zero;
        IntPtr inspectablePtr = IntPtr.Zero;
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            const string className = "Windows.Media.Internal.AudioPolicyConfig";
            if (WindowsCreateString(className, (uint)className.Length, out classIdHString) != 0)
                return false;

            Guid inspectableIid = IID_IInspectable;
            if (RoGetActivationFactory(classIdHString, ref inspectableIid, out inspectablePtr) != 0 || inspectablePtr == IntPtr.Zero)
                return false;

            void** inspVtbl = *(void***)inspectablePtr;
            var getIids = (delegate* unmanaged[Stdcall]<IntPtr, out uint, out Guid*, int>)inspVtbl[3];
            if (getIids(inspectablePtr, out uint iidCount, out Guid* iidsPtr) != 0 || iidCount == 0 || iidsPtr == null)
                return false;

            Guid factoryIid = iidsPtr[0];
            CoTaskMemFree((IntPtr)iidsPtr);

            var qi = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)inspVtbl[0];
            if (qi(inspectablePtr, ref factoryIid, out factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
                return false;

            void** factoryVtbl = *(void***)factoryPtr;
            var clearAll = (delegate* unmanaged[Stdcall]<IntPtr, int>)factoryVtbl[22];
            return clearAll(factoryPtr) == 0;
        }
        catch
        {
        }
        finally
        {
            if (classIdHString != IntPtr.Zero) WindowsDeleteString(classIdHString);
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            if (inspectablePtr != IntPtr.Zero) Marshal.Release(inspectablePtr);
        }
        return false;
    }

    private sealed class ForegroundWindowInfo
    {
        public uint ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string WindowTitle { get; init; } = string.Empty;
        public string FriendlyAppName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Discovers all true Foreground Programs (visible, uncloaked top-level desktop windows on the taskbar)
    /// and excludes background daemons, shell hosts, console helpers, and webhelpers.
    /// </summary>
    private static Dictionary<string, ForegroundWindowInfo> GetForegroundProgramsByProcessName()
    {
        var foregroundByProcName = new Dictionary<string, ForegroundWindowInfo>(StringComparer.OrdinalIgnoreCase);
        uint currentPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            // Skip owned windows and tool windows that do not appear in Alt+Tab / Taskbar
            const uint GW_OWNER = 4;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                return true;

            int exStyle = GetWindowLongW(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return true;

            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int titleLen = GetWindowTextLengthW(hWnd);
            if (titleLen <= 0)
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == currentPid)
                return true;

            var sb = new StringBuilder(titleLen + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            string windowTitle = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(windowTitle) ||
                windowTitle.StartsWith("Console window for ", StringComparison.OrdinalIgnoreCase) ||
                windowTitle.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) ||
                windowTitle.Equals("Windows Input Experience", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string procName = proc.ProcessName;
                if (string.IsNullOrWhiteSpace(procName) || ExcludedBackgroundProcesses.Contains(procName))
                    return true;

                if (!foregroundByProcName.ContainsKey(procName))
                {
                    foregroundByProcName[procName] = new ForegroundWindowInfo
                    {
                        ProcessId = pid,
                        ProcessName = procName,
                        WindowTitle = windowTitle,
                        FriendlyAppName = FormatCleanAppName(procName)
                    };
                }
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);

        return foregroundByProcName;
    }

    private static string FormatCleanAppName(string procName)
    {
        return procName.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" => "Mozilla Firefox",
            "brave" => "Brave Browser",
            "opera" => "Opera Browser",
            "vivaldi" => "Vivaldi",
            "discord" => "Discord",
            "spotify" => "Spotify",
            "steam" => "Steam",
            "obs64" or "obs32" => "OBS Studio",
            "prismlauncher" => "Prism Launcher",
            "javaw" or "java" => "Minecraft (Java)",
            "elgato.wavelink" => "Elgato Wave Link",
            "vlc" => "VLC Media Player",
            _ => procName
        };
    }

    /// <summary>
    /// Enumerates ONLY Foreground Programs (excluding background processes, helpers, and system hosts)
    /// and merges their active WASAPI audio session state (deduplicated 1 entry per foreground program).
    /// </summary>
    public static List<AudioSessionItem> GetActiveApplicationSessions()
    {
        var foregroundApps = GetForegroundProgramsByProcessName();
        var resultByProcName = new Dictionary<string, AudioSessionItem>(StringComparer.OrdinalIgnoreCase);
        uint currentPid = (uint)Environment.ProcessId;

        // Seed with all currently open Foreground Programs so user can pick any open foreground app
        foreach (var fg in foregroundApps.Values)
        {
            resultByProcName[fg.ProcessName] = new AudioSessionItem
            {
                ProcessId = fg.ProcessId,
                ProcessName = fg.ProcessName,
                DisplayName = fg.FriendlyAppName,
                Volume = 1f,
                IsMuted = false,
                PeakLevel = 0f
            };
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                try
                {
                    var sessionManager = device.AudioSessionManager;
                    var sessions = sessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        uint pid = session.GetProcessID;
                        if (pid == 0 || pid == currentPid)
                            continue;

                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                            continue;

                        string procName;
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            procName = proc.ProcessName;
                        }
                        catch
                        {
                            continue;
                        }

                        // Map steamwebhelper audio sessions to Steam ONLY if Steam is a running foreground app
                        if (procName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase))
                        {
                            if (foregroundApps.ContainsKey("steam"))
                                procName = "steam";
                            else
                                continue;
                        }

                        // Strictly require the process to be a Foreground Program (not a background daemon/service)
                        if (ExcludedBackgroundProcesses.Contains(procName) || !foregroundApps.ContainsKey(procName))
                            continue;

                        float peak = 0f;
                        try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }

                        float vol = 1f;
                        bool muted = false;
                        try
                        {
                            vol = session.SimpleAudioVolume.Volume;
                            muted = session.SimpleAudioVolume.Mute;
                        }
                        catch { }

                        if (resultByProcName.TryGetValue(procName, out var existing))
                        {
                            // Prefer the audio-producing subprocess PID for routing/capture and keep highest peak
                            existing.ProcessId = pid;
                            existing.CurrentEndpointId = device.ID;
                            existing.CurrentEndpointName = device.FriendlyName;
                            existing.RoutedEndpointId = device.ID;
                            existing.Volume = vol;
                            existing.IsMuted = muted;
                            if (peak > existing.PeakLevel)
                                existing.PeakLevel = peak;
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        // Sort stably by DisplayName (NEVER sort by changing PeakLevel, which would reshuffle UI lists!)
        return resultByProcName.Values
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void SetApplicationSessionVolume(uint processId, float volume, bool isMuted)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s.GetProcessID == processId)
                        {
                            s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                            s.SimpleAudioVolume.Mute = isMuted;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
