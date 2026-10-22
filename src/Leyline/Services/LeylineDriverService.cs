using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Leyline.Services;

public enum LeylineDriverState
{
    /// <summary>No driver package ships next to Leyline.exe (local build without driver\LeylineVAD\out).</summary>
    NotBundled,
    /// <summary>The Leyline Virtual Audio device is not installed.</summary>
    NotInstalled,
    /// <summary>Installed, but older than the driver bundled with this version of Leyline.</summary>
    UpdateAvailable,
    /// <summary>Installed and up to date.</summary>
    Installed
}

/// <summary>
/// Installs, updates and removes the Leyline Virtual Audio driver (driver\LeylineVAD), which Leyline ships in a
/// Driver\ folder next to Leyline.exe. Installing needs administrator rights, so Leyline re-launches itself
/// elevated with --install-driver / --uninstall-driver and the helper does the work.
/// </summary>
public static class LeylineDriverService
{
    public const string HardwareId = @"Root\LeylineVAD";
    public const string DeviceName = "Leyline Virtual Audio";

    private const string InfName = "LeylineVAD.inf";
    private const string CertName = "LeylineTestCert.cer";

    // Devices Leyline created before it had its own driver: copies of Valve's Steam Streaming Microphone driver.
    private const string LegacyValveHardwareId = @"ROOT\SteamStreamingMicrophone";

    private static Guid MediaClassGuid = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leyline");

    private static string LogFilePath => Path.Combine(AppDataDir, "driver.log");

    // Records of the legacy Valve-based devices (used only to remove them).
    private static string LegacyNodesFilePath => Path.Combine(AppDataDir, "leyline-driver-nodes.json");

    /// <summary>Folder holding the bundled driver package (LeylineVAD.inf/.sys/.cat and the test certificate).</summary>
    public static string PackageDirectory => Path.Combine(AppContext.BaseDirectory, "Driver");

    private static string BundledInfPath => Path.Combine(PackageDirectory, InfName);

    /// <summary>
    /// Appends a line to %APPDATA%\Leyline\driver.log. The elevated helper has no UI, so this is the only record of what it did.
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string elevation = IsRunningAsAdministrator() ? "admin" : "user";
            File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId} {elevation}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    //=========================================================================
    // State
    //=========================================================================

    public static LeylineDriverState GetState()
    {
        Version? bundled = BundledDriverVersion;
        Version? installed = InstalledDriverVersion;

        if (installed == null)
            return bundled == null ? LeylineDriverState.NotBundled : LeylineDriverState.NotInstalled;
        if (bundled != null && bundled > installed)
            return LeylineDriverState.UpdateAvailable;
        return LeylineDriverState.Installed;
    }

    /// <summary>DriverVer version of the driver package shipped with this Leyline build.</summary>
    public static Version? BundledDriverVersion
    {
        get
        {
            try
            {
                if (!File.Exists(BundledInfPath))
                    return null;

                // DriverVer = mm/dd/yyyy,a.b.c.d (stampinf may write UTF-16 or ASCII; File.ReadAllText detects the BOM)
                var match = Regex.Match(File.ReadAllText(BundledInfPath), @"^\s*DriverVer\s*=\s*[^,]*,\s*([\d.]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Version of the installed Leyline driver, or null when the device is not installed.</summary>
    public static Version? InstalledDriverVersion
    {
        get
        {
            foreach (string instanceId in FindDeviceInstances(HardwareId))
            {
                try
                {
                    using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
                    if (enumKey?.GetValue("Driver") is not string driverKey)
                        continue;

                    using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
                    if (classKey?.GetValue("DriverVersion") is string ver && Version.TryParse(ver, out var v))
                        return v;
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// Leyline's driver is test-signed, so Windows only loads it with test signing on (bcdedit /set testsigning on + reboot).
    /// </summary>
    public static bool IsTestSigningEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return (key?.GetValue("SystemStartOptions") as string ?? string.Empty)
                .Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Device instance IDs (under ROOT\MEDIA) whose hardware ID list contains <paramref name="hardwareId"/>.</summary>
    private static List<string> FindDeviceInstances(string hardwareId)
    {
        var result = new List<string>();
        try
        {
            using var media = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\MEDIA");
            if (media == null)
                return result;

            foreach (string sub in media.GetSubKeyNames())
            {
                using var dev = media.OpenSubKey(sub);
                if (dev?.GetValue("HardwareID") is string[] ids &&
                    ids.Any(id => id.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add($@"ROOT\MEDIA\{sub}");
                }
            }
        }
        catch { }
        return result;
    }

    //=========================================================================
    // Elevated actions
    //=========================================================================

    /// <summary>Runs the install (or uninstall) in an elevated copy of Leyline and waits for it.</summary>
    public static bool RunElevatedDriverAction(bool install, out string statusMessage)
    {
        string arg = install ? "--install-driver" : "--uninstall-driver";
        try
        {
            if (IsRunningAsAdministrator())
            {
                return install ? InstallElevated(out statusMessage) : UninstallElevated(out statusMessage);
            }

            string exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                statusMessage = "Could not resolve the Leyline executable path.";
                return false;
            }

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arg,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (proc == null)
            {
                statusMessage = "Administrator approval was cancelled.";
                return false;
            }

            if (!proc.WaitForExit(120000))
            {
                Log($"{arg}: elevated helper still running after 120 s");
                statusMessage = "The driver installer is taking longer than expected; check again in a moment.";
                return false;
            }

            Log($"{arg}: elevated helper exited with code {proc.ExitCode}");
            if (proc.ExitCode == 0)
            {
                statusMessage = install
                    ? $"{DeviceName} driver installed."
                    : $"{DeviceName} driver removed.";
                return true;
            }

            statusMessage = $"The driver {(install ? "install" : "removal")} failed (code {proc.ExitCode}); see %APPDATA%\\Leyline\\driver.log.";
            return false;
        }
        catch (Exception ex)
        {
            Log($"{arg}: failed to start: {ex.Message}");
            statusMessage = $"Driver action cancelled or failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Elevated: removes the legacy Valve-based devices, trusts the test certificate, installs the driver package
    /// and creates (or updates) the Root\LeylineVAD device.
    /// </summary>
    public static bool InstallElevated(out string message)
    {
        if (!IsRunningAsAdministrator())
        {
            message = "Administrator rights are required to install the Leyline driver.";
            return false;
        }

        try
        {
            if (!File.Exists(BundledInfPath))
            {
                message = $"Driver package not found in {PackageDirectory}.";
                return false;
            }

            RemoveLegacyDevices();

            string certPath = Path.Combine(PackageDirectory, CertName);
            if (File.Exists(certPath))
            {
                using var cert = X509CertificateLoader.LoadCertificateFromFile(certPath);
                foreach (var storeName in new[] { StoreName.Root, StoreName.TrustedPublisher })
                {
                    using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(cert);
                }
                Log($"Trusted test certificate {cert.Subject}");
            }

            // Add the package to the driver store and update any existing Leyline device to it.
            int addCode = RunTool("pnputil.exe", $"/add-driver \"{BundledInfPath}\" /install");
            Log($"pnputil /add-driver exited with {addCode}");

            if (FindDeviceInstances(HardwareId).Count == 0)
            {
                string? instanceId = CreateRootDevice(DeviceName, HardwareId);
                Log(instanceId != null ? $"Created device {instanceId}" : "Creating the device failed");
                if (instanceId == null)
                {
                    message = "Could not create the Leyline Virtual Audio device.";
                    return false;
                }
            }

            bool updated = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, BundledInfPath,
                0x00000001u | 0x00000004u, // INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE
                out _);
            Log($"UpdateDriverForPlugAndPlayDevices: {(updated ? "ok" : $"failed (Win32 error {Marshal.GetLastWin32Error()})")}");

            Version? installed = InstalledDriverVersion;
            message = installed != null
                ? $"{DeviceName} driver {installed} installed."
                : "The driver package was added, but the device did not start.";
            return installed != null;
        }
        catch (Exception ex)
        {
            message = $"Leyline driver install failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Elevated: removes the Leyline device and its driver package.</summary>
    public static bool UninstallElevated(out string message)
    {
        if (!IsRunningAsAdministrator())
        {
            message = "Administrator rights are required to remove the Leyline driver.";
            return false;
        }

        try
        {
            int removed = 0;
            foreach (string instanceId in FindDeviceInstances(HardwareId))
            {
                if (RunTool("pnputil.exe", $"/remove-device \"{instanceId}\"") == 0)
                    removed++;
            }

            foreach (string published in FindPublishedDriverPackages(InfName))
            {
                RunTool("pnputil.exe", $"/delete-driver {published} /uninstall /force");
            }

            RemoveLegacyDevices();

            message = $"Removed {removed} Leyline Virtual Audio device(s).";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Leyline driver removal failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Removes devices from Leyline's former approach: copies of Valve's Steam Streaming Microphone driver renamed
    /// "Leyline ..." / "VoidPatch ...", plus leftover endpoint entries named VoidPatch. Valve's own Steam devices
    /// (ROOT\STEAMSTREAMINGMICROPHONE) are never touched.
    /// </summary>
    private static void RemoveLegacyDevices()
    {
        foreach (string instanceId in FindDeviceInstances(LegacyValveHardwareId))
        {
            string friendlyName = string.Empty;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
                friendlyName = key?.GetValue("FriendlyName") as string ?? key?.GetValue("DeviceDesc") as string ?? string.Empty;
            }
            catch { }

            if (friendlyName.Contains("Leyline", StringComparison.OrdinalIgnoreCase) ||
                friendlyName.Contains("VoidPatch", StringComparison.OrdinalIgnoreCase))
            {
                int code = RunTool("pnputil.exe", $"/remove-device \"{instanceId}\"");
                Log($"Removed legacy device {instanceId} ({friendlyName}): exit {code}");
            }
        }

        RunTool("powershell.exe",
            "-NoProfile -WindowStyle Hidden -Command \"Get-PnpDevice -Class AudioEndpoint | " +
            "Where-Object FriendlyName -like '*VoidPatch*' | ForEach-Object { pnputil /remove-device $_.InstanceId | Out-Null }\"");

        try
        {
            if (File.Exists(LegacyNodesFilePath))
                File.Delete(LegacyNodesFilePath);
        }
        catch { }
    }

    /// <summary>Published names (oemNN.inf) of driver-store packages whose original name is <paramref name="originalInfName"/>.</summary>
    private static List<string> FindPublishedDriverPackages(string originalInfName)
    {
        var result = new List<string>();
        string output = RunToolCapture("pnputil.exe", "/enum-drivers");
        string? published = null;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
                published = line["Published Name:".Length..].Trim();
            else if (line.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase) &&
                     line["Original Name:".Length..].Trim().Equals(originalInfName, StringComparison.OrdinalIgnoreCase) &&
                     published != null)
                result.Add(published);
        }
        return result;
    }

    private static int RunTool(string fileName, string arguments)
    {
        string output = RunToolCapture(fileName, arguments, out int exitCode);
        if (!string.IsNullOrWhiteSpace(output))
            Log($"{Path.GetFileNameWithoutExtension(fileName)} {arguments}: {output.Trim().Replace(Environment.NewLine, " | ")}");
        return exitCode;
    }

    private static string RunToolCapture(string fileName, string arguments) => RunToolCapture(fileName, arguments, out _);

    private static string RunToolCapture(string fileName, string arguments, out int exitCode)
    {
        exitCode = -1;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null)
                return string.Empty;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            exitCode = p.ExitCode;
            return output;
        }
        catch (Exception ex)
        {
            Log($"{fileName} {arguments}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Creates a root-enumerated device node with the given hardware ID; returns its instance ID.</summary>
    private static string? CreateRootDevice(string description, string hardwareId)
    {
        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(ref MediaClassGuid, IntPtr.Zero);
        if (devInfoSet == new IntPtr(-1))
            return null;

        try
        {
            SP_DEVINFO_DATA devInfoData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfoW(devInfoSet, "MEDIA", ref MediaClassGuid, description, IntPtr.Zero,
                    1, // DICD_GENERATE_ID
                    ref devInfoData))
            {
                return null;
            }

            // REG_MULTI_SZ requires double null termination in UTF-16
            byte[] hwidBytes = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, 1 /* SPDRP_HARDWAREID */, hwidBytes, (uint)hwidBytes.Length))
                return null;

            if (!SetupDiCallClassInstaller(0x19 /* DIF_REGISTERDEVICE */, devInfoSet, ref devInfoData))
                return null;

            var sb = new StringBuilder(256);
            return SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, sb, (uint)sb.Capacity, out _) ? sb.ToString() : null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    #region SetupAPI & NewDev P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(
        IntPtr DeviceInfoSet,
        string DeviceName,
        ref Guid ClassGuid,
        string DeviceDescription,
        IntPtr hwndParent,
        uint CreationFlags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        byte[] PropertyBuffer,
        uint PropertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction,
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        StringBuilder DeviceInstanceId,
        uint DeviceInstanceIdSize,
        out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string HardwareId,
        string FullInfPath,
        uint InstallFlags,
        out bool bRebootRequired);

    #endregion
}
