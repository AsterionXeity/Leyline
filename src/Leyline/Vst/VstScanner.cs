using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Leyline.Models;

namespace Leyline.Vst;

public static unsafe class VstScanner
{
    public const int ExitCodeScannedOk = 0;
    public const int ExitCodeVirtualInstrument = 10;
    public const int ExitCodeBrokenPlugin = 20;

    private static readonly string[] DefaultDirectories =
    [
        @"C:\Program Files\Common Files\VST3",
        @"C:\Program Files\VSTPlugins",
        @"C:\Program Files\Steinberg\VstPlugins",
        @"C:\Program Files\Common Files\VST2"
    ];

    private static readonly string[] KnownInstrumentKeywords =
    [
        "Vital",
        "Omnisphere",
        "Serum",
        "FabFilter One",
        "FabFilter Twin",
        "Kontakt",
        "Pigments",
        "Phase Plant",
        "Massive",
        "Sylenth",
        "Diva",
        "FM8",
        "Battery",
        "Addictive Drums",
        "EZdrummer",
        "Superior Drummer",
        "Keyscape",
        "Trilian",
        "Spire",
        "Nexus",
        "Avenger",
        "Halion",
        "Padshop",
        "Retrologue",
        "Xpand!2",
        "Augmented",
        "Mini V",
        "Analog Lab",
        "Prophet",
        "Jup-8",
        "CS-80",
        "Jun-6",
        "CZ V",
        "DX7",
        "B-3 V",
        "Clavinet",
        "Mellotron",
        "Piano V",
        "Stage-73",
        "Wurli",
        "VOX Continental",
        "Buchla",
        "CMI V",
        "Emulator",
        "Synclavier",
        "Synthi",
        "Vocoder V",
        "MS-20",
        "SQ80",
        "KORG"
    ];

    private static readonly string[] KnownCrashingKeywords =
    [
        "ZENOLOGY"
    ];

    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Leyline",
        "vst-scan-cache.json");

    public static List<VstPluginInfo> ScanPlugins(
        IEnumerable<string>? customDirectories = null,
        IEnumerable<string>? userAllowedPaths = null,
        bool forceDeepProbe = false)
    {
        var results = new List<VstPluginInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowedSet = new HashSet<string>(userAllowedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var cache = forceDeepProbe ? new Dictionary<string, VstPluginInfo>(StringComparer.OrdinalIgnoreCase) : LoadCache();

        var dirs = new List<string>(DefaultDirectories);
        if (customDirectories != null)
        {
            foreach (var dir in customDirectories)
            {
                if (!string.IsNullOrWhiteSpace(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(dir);
            }
        }

        foreach (var rootDir in dirs)
        {
            if (!Directory.Exists(rootDir))
                continue;

            try
            {
                ScanDirectoryRecursive(rootDir, results, seenNames, cache, allowedSet, forceDeepProbe);
            }
            catch
            {
            }
        }

        SaveCache(results);
        return results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ScanDirectoryRecursive(
        string directory,
        List<VstPluginInfo> results,
        HashSet<string> seenNames,
        Dictionary<string, VstPluginInfo> cache,
        HashSet<string> allowedSet,
        bool forceDeepProbe)
    {
        var folderName = Path.GetFileName(directory);
        if (folderName.Equals("arm64-win", StringComparison.OrdinalIgnoreCase) ||
            folderName.Equals("x86-win", StringComparison.OrdinalIgnoreCase) ||
            folderName.Equals("Resources", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    InspectAndAddPlugin(file, "VST3", results, seenNames, cache, allowedSet, forceDeepProbe);
                }
                else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.Contains(".vst3", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                        continue;

                    InspectAndAddPlugin(file, "VST2", results, seenNames, cache, allowedSet, forceDeepProbe);
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(directory))
            {
                ScanDirectoryRecursive(subDir, results, seenNames, cache, allowedSet, forceDeepProbe);
            }
        }
        catch
        {
        }
    }

    private static void InspectAndAddPlugin(
        string filePath,
        string format,
        List<VstPluginInfo> results,
        HashSet<string> seenNames,
        Dictionary<string, VstPluginInfo> cache,
        HashSet<string> allowedSet,
        bool forceDeepProbe)
    {
        var rawName = Path.GetFileNameWithoutExtension(filePath);
        int versionIdx = rawName.IndexOf("_v", StringComparison.OrdinalIgnoreCase);
        if (versionIdx > 2 && versionIdx + 2 < rawName.Length && char.IsDigit(rawName[versionIdx + 2]))
        {
            rawName = rawName[..versionIdx];
        }

        string key = $"{format}:{rawName}";
        if (!seenNames.Add(key))
            return;

        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(filePath).Ticks; } catch { }

        if (!forceDeepProbe && cache.TryGetValue(filePath, out var cached) && cached.LastWriteTicks == ticks)
        {
            cached.IsUserAllowedOverride = allowedSet.Contains(filePath);
            results.Add(cached);
            return;
        }

        string vendor = GuessVendor(filePath);
        var info = new VstPluginInfo
        {
            Name = rawName,
            FilePath = filePath,
            Format = format,
            Vendor = vendor,
            LastWriteTicks = ticks,
            IsUserAllowedOverride = allowedSet.Contains(filePath)
        };

        // 1. Check known crashing plugins
        if (KnownCrashingKeywords.Any(k => rawName.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            info.Status = VstPluginStatus.BrokenCrashed;
            info.SubCategories = "Crashed during scan";
            results.Add(info);
            return;
        }

        // 2. Check known virtual instruments (FX-Omnisphere is an effect, Omnisphere is a synth)
        if (!rawName.StartsWith("FX-", StringComparison.OrdinalIgnoreCase) &&
            KnownInstrumentKeywords.Any(k =>
                rawName.Equals(k, StringComparison.OrdinalIgnoreCase) ||
                rawName.StartsWith(k + " ", StringComparison.OrdinalIgnoreCase) ||
                rawName.Equals(k + "2", StringComparison.OrdinalIgnoreCase)))
        {
            info.Status = VstPluginStatus.VirtualInstrument;
            info.SubCategories = "Instrument|Synth";
            results.Add(info);
            return;
        }

        // 3. Check VST3 bundle moduleinfo.json if present (instantaneous, zero DLL load needed)
        if (TryInspectBundleModuleInfoJson(filePath, out bool isInstrumentFromJson))
        {
            info.Status = isInstrumentFromJson ? VstPluginStatus.VirtualInstrument : VstPluginStatus.ScannedOk;
            info.SubCategories = isInstrumentFromJson ? "Instrument" : "Fx";
            results.Add(info);
            return;
        }

        // 4. If user clicked deep "Scan plugins", probe in isolated child process
        if (forceDeepProbe)
        {
            ProbePluginOutOfProcess(info);
        }
        else
        {
            info.Status = VstPluginStatus.ScannedOk;
        }

        results.Add(info);
    }

    private static bool TryInspectBundleModuleInfoJson(string binaryPath, out bool isInstrument)
    {
        isInstrument = false;
        try
        {
            // Binary inside bundle: ...\Plugin.vst3\Contents\x86_64-win\Plugin.vst3
            string? archDir = Path.GetDirectoryName(binaryPath);
            string? contentsDir = archDir != null ? Path.GetDirectoryName(archDir) : null;
            if (contentsDir != null && Path.GetFileName(contentsDir).Equals("Contents", StringComparison.OrdinalIgnoreCase))
            {
                string moduleJsonPath = Path.Combine(contentsDir, "Resources", "moduleinfo.json");
                if (File.Exists(moduleJsonPath))
                {
                    string text = File.ReadAllText(moduleJsonPath);
                    if (text.Contains("\"Instrument\"", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("\"Fx\"", StringComparison.OrdinalIgnoreCase))
                    {
                        isInstrument = true;
                    }
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static void ProbePluginOutOfProcess(VstPluginInfo info)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                info.Status = VstPluginStatus.ScannedOk;
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--probe-vst \"{info.FilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                info.Status = VstPluginStatus.ScannedOk;
                return;
            }

            if (!proc.WaitForExit(1800))
            {
                try { proc.Kill(); } catch { }
                info.Status = VstPluginStatus.BrokenCrashed;
                info.SubCategories = "Timed out during scan";
                return;
            }

            if (proc.ExitCode == ExitCodeScannedOk)
            {
                info.Status = VstPluginStatus.ScannedOk;
            }
            else if (proc.ExitCode == ExitCodeVirtualInstrument)
            {
                info.Status = VstPluginStatus.VirtualInstrument;
                info.SubCategories = "Instrument";
            }
            else
            {
                info.Status = VstPluginStatus.BrokenCrashed;
                info.SubCategories = $"Crashed (0x{proc.ExitCode:X8})";
            }
        }
        catch
        {
            info.Status = VstPluginStatus.ScannedOk;
        }
    }

    /// <summary>
    /// Runs in an isolated child process when Leyline.exe is invoked with `--probe-vst "path"`.
    /// Returns ExitCodeScannedOk (0), ExitCodeVirtualInstrument (10), or ExitCodeBrokenPlugin (20).
    /// </summary>
    public static int RunIsolatedVstProbeCli(string pluginPath)
    {
        try
        {
            if (!File.Exists(pluginPath))
                return ExitCodeBrokenPlugin;

            if (!NativeLibrary.TryLoad(pluginPath, out IntPtr module))
                return ExitCodeBrokenPlugin;

            if (NativeLibrary.TryGetExport(module, "GetPluginFactory", out IntPtr getFactoryPtr))
            {
                var getFactory = (delegate* unmanaged[Stdcall]<void*>)getFactoryPtr;
                void* factory = getFactory();
                if (factory == null)
                    return ExitCodeBrokenPlugin;

                void** vtbl = *(void***)factory;
                var countClasses = (delegate* unmanaged[Stdcall]<void*, int>)vtbl[4];
                int count = countClasses(factory);

                Guid iidFactory2 = new("0007B650-F24B-4C0B-A464-EDB9F00B2ABB");
                byte* iidBytes = stackalloc byte[16];
                iidFactory2.TryWriteBytes(new Span<byte>(iidBytes, 16));

                void* factory2 = null;
                var qi = (delegate* unmanaged[Stdcall]<void*, byte*, void**, int>)vtbl[0];
                if (qi(factory, iidBytes, &factory2) == 0 && factory2 != null)
                {
                    void** vtbl2 = *(void***)factory2;
                    var getClassInfo2 = (delegate* unmanaged[Stdcall]<void*, int, PClassInfo2*, int>)vtbl2[7];

                    for (int i = 0; i < count; i++)
                    {
                        PClassInfo2 info2 = default;
                        if (getClassInfo2(factory2, i, &info2) == 0)
                        {
                            string category = Encoding.ASCII.GetString(info2.Category, 32).TrimEnd('\0');
                            string subCat = Encoding.ASCII.GetString(info2.SubCategories, 128).TrimEnd('\0');

                            if (category.Equals("Audio Module Class", StringComparison.OrdinalIgnoreCase))
                            {
                                bool isInstrument = subCat.Contains("Instrument", StringComparison.OrdinalIgnoreCase) &&
                                                    !subCat.Contains("Fx", StringComparison.OrdinalIgnoreCase);

                                return isInstrument ? ExitCodeVirtualInstrument : ExitCodeScannedOk;
                            }
                        }
                    }
                }

                return ExitCodeScannedOk;
            }

            if (NativeLibrary.TryGetExport(module, "VSTPluginMain", out _) ||
                NativeLibrary.TryGetExport(module, "main", out _))
            {
                return ExitCodeScannedOk;
            }

            return ExitCodeBrokenPlugin;
        }
        catch
        {
            return ExitCodeBrokenPlugin;
        }
    }

    private static string GuessVendor(string filePath)
    {
        var parentDir = Path.GetDirectoryName(filePath) ?? string.Empty;
        if (parentDir.Contains("Elgato", StringComparison.OrdinalIgnoreCase)) return "Elgato";
        if (parentDir.Contains("FabFilter", StringComparison.OrdinalIgnoreCase)) return "FabFilter";
        if (parentDir.Contains("iZotope", StringComparison.OrdinalIgnoreCase)) return "iZotope";
        if (parentDir.Contains("Valhalla", StringComparison.OrdinalIgnoreCase)) return "Valhalla DSP";
        if (parentDir.Contains("Roland", StringComparison.OrdinalIgnoreCase)) return "Roland";
        if (parentDir.Contains("Spectrasonics", StringComparison.OrdinalIgnoreCase)) return "Spectrasonics";
        return "VST3 Audio";
    }

    private static Dictionary<string, VstPluginInfo> LoadCache()
    {
        var dict = new Dictionary<string, VstPluginInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(CacheFilePath))
            {
                string json = File.ReadAllText(CacheFilePath);
                var list = JsonSerializer.Deserialize<List<VstPluginInfo>>(json);
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item.FilePath))
                            dict[item.FilePath] = item;
                    }
                }
            }
        }
        catch { }
        return dict;
    }

    private static void SaveCache(List<VstPluginInfo> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CacheFilePath, json);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PClassInfo2
    {
        public fixed byte Cid[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
        public uint ClassFlags;
        public fixed byte SubCategories[128];
        public fixed byte Vendor[64];
        public fixed byte Version[64];
        public fixed byte SdkVersion[64];
    }
}
