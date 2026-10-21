using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Leyline.Services;

public sealed class LeylineDriverNodeRecord
{
    public string PnpInstanceId { get; set; } = string.Empty;
    public string RenderBusName { get; set; } = string.Empty;
    public string CaptureMixName { get; set; } = string.Empty;
}

public sealed class EndpointBackupEntry
{
    public string DataFlow { get; set; } = string.Empty;
    public string EndpointGuid { get; set; } = string.Empty;
    public string OriginalDeviceDesc { get; set; } = string.Empty;
    public string OriginalInterfaceName { get; set; } = string.Empty;
}

public static class LeylineDriverService
{
    private const string PropKeyDeviceDesc = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string PropKeyInterfaceFriendlyName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string PropKeyParentPnpId = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";
    private const string PropKeyDeviceFriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";

    public const string BrandInterfaceName = "Leyline Virtual Audio";
    private const string WdmHardwareId = @"ROOT\SteamStreamingMicrophone";

    private static Guid MediaClassGuid = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    // Windows shows an endpoint as "<endpoint DeviceDesc> (<interface name>)": endpoints are named after the bus and the
    // interface is BrandInterfaceName, giving "System (Leyline Virtual Audio)".
    private static readonly (string RenderBus, string CaptureMix)[] DesiredVirtualBuses =
    [
        ("System",     "System"),
        ("Game",       "Game"),
        ("Browser",    "Browser"),
        ("Music",      "Music"),
        ("Voice Chat", "Voice Chat"),
        ("Stream Mix", "Stream Mix"),
        ("Mic FX",     "Mic FX")
    ];

    /// <summary>
    /// True for endpoints created by Leyline's provisioner. Windows composes the endpoint name from the bus name and
    /// either the interface name ("Leyline Virtual Audio") or the device-node name ("Leyline Browser"), so match the brand only.
    /// </summary>
    public static bool IsLeylineEndpoint(string? endpointName) =>
        !string.IsNullOrEmpty(endpointName) &&
        endpointName.Contains("Leyline", StringComparison.OrdinalIgnoreCase);

    private static string NodesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Leyline",
        "leyline-driver-nodes.json");

    private static string LegacyBackupFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Leyline",
        "driver-endpoint-backup.json");

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Leyline",
        "driver.log");

    /// <summary>
    /// Appends a line to %APPDATA%\Leyline\driver.log. The elevated helper has no UI, so this is the only record of what it did.
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
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
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDriverBranded()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
            return devices.Any(d => IsLeylineEndpoint(d.FriendlyName));
        }
        catch
        {
            return false;
        }
    }

    public static bool RunElevatedDriverAction(bool install, out string statusMessage)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                statusMessage = "Could not resolve Leyline executable path.";
                return false;
            }

            string arg = install ? "--provision-driver" : "--restore-driver";

            if (IsRunningAsAdministrator())
            {
                return install
                    ? ProvisionLeylineEndpointsElevated(out statusMessage)
                    : RestoreOriginalEndpointsElevated(out statusMessage);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arg,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                statusMessage = "Administrator elevation was cancelled.";
                return false;
            }

            if (!proc.WaitForExit(35000))
            {
                Log($"{arg}: elevated helper still running after 35 s");
                statusMessage = "Driver action is taking longer than expected; check again in a moment.";
                return false;
            }

            Log($"{arg}: elevated helper exited with code {proc.ExitCode}");
            if (proc.ExitCode == 0)
            {
                statusMessage = install
                    ? "Leyline Virtual Audio Driver installed! Dedicated Leyline Virtual Audio devices are now live."
                    : "Leyline Virtual Audio Driver instances removed.";
                return true;
            }

            statusMessage = $"Driver installer exited with code {proc.ExitCode}.";
            return false;
        }
        catch (Exception ex)
        {
            Log($"Driver action failed to start: {ex.Message}");
            statusMessage = $"Driver action cancelled or failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Creates dedicated standalone PnP Virtual Audio Device instances for Leyline (separate from Elgato's driver)
    /// and names their Render & Capture endpoints "<bus> (Leyline Virtual Audio)".
    /// </summary>
    public static bool ProvisionLeylineEndpointsElevated(out string message)
    {
        if (!IsRunningAsAdministrator())
        {
            message = "Administrator rights are required to change Leyline virtual audio devices.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NodesFilePath)!);

            // Ensure any legacy backups on Elgato endpoints are restored first so Elgato is untouched
            RestoreLegacyBackupsIfAny();

            string infPath = ResolveSignedDriverInfPath();
            if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
            {
                message = "Signed WDM driver INF not found.";
                return false;
            }

            var existingLiveNodes = LoadNodes()
                .Where(n => DoesPnpNodeExist(n.PnpInstanceId))
                .ToList();

            var updatedNodes = new List<LeylineDriverNodeRecord>();

            // Reuse existing PnP virtual audio device nodes or create missing ones for the 7 Leyline buses
            for (int i = 0; i < DesiredVirtualBuses.Length; i++)
            {
                var (renderBus, captureMix) = DesiredVirtualBuses[i];
                if (i < existingLiveNodes.Count)
                {
                    var node = existingLiveNodes[i];
                    node.RenderBusName = renderBus;
                    node.CaptureMixName = captureMix;
                    updatedNodes.Add(node);
                }
                else
                {
                    string? createdId = CreatePnpVirtualAudioNode(BrandInterfaceName);
                    if (!string.IsNullOrWhiteSpace(createdId))
                    {
                        updatedNodes.Add(new LeylineDriverNodeRecord
                        {
                            PnpInstanceId = createdId,
                            RenderBusName = renderBus,
                            CaptureMixName = captureMix
                        });
                    }
                }
            }

            var nodes = updatedNodes;
            if (nodes.Count == 0)
            {
                message = "No Leyline virtual audio devices could be created.";
                return false;
            }

            SaveNodes(nodes);

            // Bind/install the signed WDM driver onto the newly registered PnP nodes
            UpdateDriverForPlugAndPlayDevicesW(
                IntPtr.Zero,
                WdmHardwareId,
                infPath,
                0x00000001u | 0x00000004u, // INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE
                out _);

            // Wait for AudioEndpointBuilder to create each node's render and capture endpoints; naming them earlier
            // leaves reused endpoints with stale names.
            WaitForEndpoints(nodes, TimeSpan.FromSeconds(15));

            // Set friendly names in Device Manager & MMDevices\Audio\Render & Capture for ONLY Leyline's PnP nodes
            int namedEndpoints = 0;
            foreach (var node in nodes)
            {
                SetPnpNodeFriendlyName(node.PnpInstanceId, BrandInterfaceName);
                namedEndpoints += NameEndpointsForPnpNode("Render", node.PnpInstanceId, node.RenderBusName);
                namedEndpoints += NameEndpointsForPnpNode("Capture", node.PnpInstanceId, node.CaptureMixName);
            }

            RemoveLegacyVoidPatchEndpoints();
            RestartWindowsAudioServiceSoft();

            message = $"Created {nodes.Count} standalone Leyline Virtual Audio devices ({namedEndpoints} endpoints active).";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Leyline driver install failed: {ex.Message}";
            return false;
        }
    }

    public static bool RestoreOriginalEndpointsElevated(out string message)
    {
        if (!IsRunningAsAdministrator())
        {
            message = "Administrator rights are required to change Leyline virtual audio devices.";
            return false;
        }

        try
        {
            RestoreLegacyBackupsIfAny();

            var nodes = LoadNodes();
            int removed = 0;
            foreach (var node in nodes)
            {
                bool ok = RemovePnpVirtualAudioNode(node.PnpInstanceId);
                Log($"Remove {node.PnpInstanceId}: {(ok ? "ok" : $"failed (Win32 error {Marshal.GetLastWin32Error()})")}");
                if (ok)
                    removed++;
            }

            if (File.Exists(NodesFilePath))
                File.Delete(NodesFilePath);

            RestartWindowsAudioServiceSoft();
            message = $"Removed {removed} Leyline Virtual Audio device instances.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Driver removal failed: {ex.Message}";
            return false;
        }
    }

    private static string ResolveSignedDriverInfPath()
    {
        string localSigned = @"G:\Leyline\driver\LeylineVAD\signed\SteamStreamingMicrophone.inf";
        if (File.Exists(localSigned))
            return localSigned;

        string oemInf = @"C:\Windows\INF\oem112.inf";
        if (File.Exists(oemInf))
            return oemInf;

        try
        {
            var repoDirs = Directory.GetDirectories(@"C:\Windows\System32\DriverStore\FileRepository", "steamstreamingmicrophone.inf_amd64_*");
            if (repoDirs.Length > 0)
            {
                string candidate = Path.Combine(repoDirs[0], "SteamStreamingMicrophone.inf");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }

        return localSigned;
    }

    private static string? CreatePnpVirtualAudioNode(string friendlyDescription)
    {
        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(ref MediaClassGuid, IntPtr.Zero);
        if (devInfoSet == new IntPtr(-1))
            return null;

        try
        {
            SP_DEVINFO_DATA devInfoData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfoW(
                devInfoSet,
                "MEDIA",
                ref MediaClassGuid,
                friendlyDescription,
                IntPtr.Zero,
                1, // DICD_GENERATE_ID
                ref devInfoData))
            {
                return null;
            }

            // REG_MULTI_SZ requires double null termination in UTF-16
            byte[] hwidBytes = Encoding.Unicode.GetBytes(WdmHardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(
                devInfoSet,
                ref devInfoData,
                1, // SPDRP_HARDWAREID
                hwidBytes,
                (uint)hwidBytes.Length))
            {
                return null;
            }

            if (!SetupDiCallClassInstaller(0x19, devInfoSet, ref devInfoData)) // DIF_REGISTERDEVICE = 0x19
                return null;

            StringBuilder sb = new(256);
            if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, sb, (uint)sb.Capacity, out _))
            {
                return sb.ToString();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return null;
    }

    private static bool RemovePnpVirtualAudioNode(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;

        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (devInfoSet == new IntPtr(-1))
            return false;

        try
        {
            SP_DEVINFO_DATA devInfoData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (SetupDiOpenDeviceInfoW(devInfoSet, instanceId, IntPtr.Zero, 0, ref devInfoData))
            {
                return SetupDiCallClassInstaller(0x05, devInfoSet, ref devInfoData); // DIF_REMOVE = 0x05
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
        return false;
    }

    private static bool DoesPnpNodeExist(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}", false);
        return key != null;
    }

    private static void WaitForEndpoints(List<LeylineDriverNodeRecord> nodes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool allReady = nodes.All(n =>
                CountEndpointsForPnpNode("Render", n.PnpInstanceId) > 0 &&
                CountEndpointsForPnpNode("Capture", n.PnpInstanceId) > 0);
            if (allReady)
                return;
            Thread.Sleep(500);
        }
        Log("Timed out waiting for endpoints; naming the ones that exist");
    }

    private static int CountEndpointsForPnpNode(string flowName, string pnpInstanceId)
    {
        string flowPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flowName}";
        using var flowKey = Registry.LocalMachine.OpenSubKey(flowPath, false);
        if (flowKey == null) return 0;

        int count = 0;
        foreach (var endpointGuid in flowKey.GetSubKeyNames())
        {
            using var props = Registry.LocalMachine.OpenSubKey($@"{flowPath}\{endpointGuid}\Properties", false);
            string parentId = props?.GetValue(PropKeyParentPnpId) as string ?? string.Empty;
            if (parentId.Contains(pnpInstanceId, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static void SetPnpNodeFriendlyName(string instanceId, string friendlyName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}",
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.SetValue | RegistryRights.QueryValues);
            key?.SetValue("FriendlyName", friendlyName, RegistryValueKind.String);
        }
        catch { }
    }

    private static int NameEndpointsForPnpNode(string flowName, string pnpInstanceId, string busName)
    {
        int updated = 0;
        string flowPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flowName}";
        using var flowKey = Registry.LocalMachine.OpenSubKey(flowPath, false);
        if (flowKey == null) return 0;

        foreach (var endpointGuid in flowKey.GetSubKeyNames())
        {
            string propPath = $@"{flowPath}\{endpointGuid}\Properties";
            using var readKey = Registry.LocalMachine.OpenSubKey(propPath, false);
            if (readKey == null) continue;

            string parentId = readKey.GetValue(PropKeyParentPnpId) as string ?? string.Empty;
            if (!parentId.Contains(pnpInstanceId, StringComparison.OrdinalIgnoreCase))
                continue;

            using var writeKey = Registry.LocalMachine.OpenSubKey(
                propPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.SetValue | RegistryRights.QueryValues);

            if (writeKey != null)
            {
                writeKey.SetValue(PropKeyDeviceDesc, busName, RegistryValueKind.String);
                writeKey.SetValue(PropKeyInterfaceFriendlyName, BrandInterfaceName, RegistryValueKind.String);
                writeKey.DeleteValue(PropKeyDeviceFriendlyName, false);
                updated++;
            }
        }

        return updated;
    }

    private static void RestoreLegacyBackupsIfAny()
    {
        try
        {
            if (!File.Exists(LegacyBackupFilePath)) return;
            string json = File.ReadAllText(LegacyBackupFilePath);
            var backups = JsonSerializer.Deserialize<List<EndpointBackupEntry>>(json);
            if (backups != null)
            {
                foreach (var entry in backups)
                {
                    string subKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{entry.DataFlow}\{entry.EndpointGuid}\Properties";
                    using var propKey = Registry.LocalMachine.OpenSubKey(
                        subKeyPath,
                        RegistryKeyPermissionCheck.ReadWriteSubTree,
                        RegistryRights.SetValue | RegistryRights.QueryValues);

                    if (propKey != null)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.OriginalDeviceDesc))
                            propKey.SetValue(PropKeyDeviceDesc, entry.OriginalDeviceDesc, RegistryValueKind.String);
                        if (!string.IsNullOrWhiteSpace(entry.OriginalInterfaceName))
                            propKey.SetValue(PropKeyInterfaceFriendlyName, entry.OriginalInterfaceName, RegistryValueKind.String);
                        propKey.DeleteValue(PropKeyDeviceFriendlyName, false);
                    }
                }
            }
            File.Delete(LegacyBackupFilePath);
        }
        catch { }
    }

    // Endpoint device nodes left over from the app's former name (VoidPatch) keep their old names after the rename.
    private static void RemoveLegacyVoidPatchEndpoints()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"Get-PnpDevice -Class AudioEndpoint | Where-Object FriendlyName -like '*VoidPatch*' | ForEach-Object { pnputil /remove-device $_.InstanceId | Out-Null; $_.InstanceId }\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            string removed = p?.StandardOutput.ReadToEnd() ?? string.Empty;
            p?.WaitForExit(15000);
            int count = removed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (count > 0)
                Log($"Removed {count} legacy VoidPatch endpoint device nodes");
        }
        catch { }
    }

    private static void RestartWindowsAudioServiceSoft()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"Restart-Service -Name Audiosrv -Force -ErrorAction SilentlyContinue\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(6000);
        }
        catch { }
    }

    private static List<LeylineDriverNodeRecord> LoadNodes()
    {
        try
        {
            if (File.Exists(NodesFilePath))
            {
                string json = File.ReadAllText(NodesFilePath);
                return JsonSerializer.Deserialize<List<LeylineDriverNodeRecord>>(json) ?? new();
            }
        }
        catch { }
        return new List<LeylineDriverNodeRecord>();
    }

    private static void SaveNodes(List<LeylineDriverNodeRecord> nodes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NodesFilePath)!);
            string json = JsonSerializer.Serialize(nodes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(NodesFilePath, json);
        }
        catch { }
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

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(
        IntPtr DeviceInfoSet,
        string DeviceName,
        ref Guid ClassGuid,
        string DeviceDescription,
        IntPtr hwndParent,
        uint CreationFlags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfoW(
        IntPtr DeviceInfoSet,
        string DeviceInstanceId,
        IntPtr hwndParent,
        uint OpenFlags,
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
