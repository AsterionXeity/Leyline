using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
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
    public const int ExitCodeLoadFailed = 21;
    public const int ExitCodeNotAPlugin = 30;

    // A plugin that takes longer than this to load in the probe process is treated as broken.
    private const int ProbeTimeoutMs = 15000;

    private static readonly string[] DefaultDirectories =
    [
        @"C:\Program Files\Common Files\VST3",
        @"C:\Program Files\VSTPlugins",
        @"C:\Program Files\Steinberg\VstPlugins",
        @"C:\Program Files\Common Files\VST2"
    ];

    // Plugins known to crash or hang when loaded; they are blocked without probing.
    private static readonly string[] KnownCrashingKeywords =
    [
        "ZENOLOGY"
    ];

    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Leyline",
        "vst-scan-cache-v2.json");

    // Written before plugins were probed: it lists library DLLs as plugins and guesses instruments by name.
    private static string LegacyCacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Leyline",
        "vst-scan-cache.json");

    /// <summary>
    /// Finds VST2 and VST3 plugins and sorts them into effects, instruments and broken plugins.
    /// Only files that export a VST entry point are considered; each new or changed plugin is loaded in a
    /// separate probe process to read its category. <paramref name="forceDeepProbe"/> re-probes every plugin.
    /// Slow on a first scan: call it off the UI thread.
    /// </summary>
    public static List<VstPluginInfo> ScanPlugins(
        IEnumerable<string>? customDirectories = null,
        IEnumerable<string>? userAllowedPaths = null,
        bool forceDeepProbe = false)
    {
        var candidates = new List<VstPluginInfo>();
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
                ScanDirectoryRecursive(rootDir, candidates, seenNames);
            }
            catch
            {
            }
        }

        var toProbe = new List<VstPluginInfo>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var info = candidates[i];
            if (cache.TryGetValue(info.FilePath, out var cached) && cached.LastWriteTicks == info.LastWriteTicks)
            {
                candidates[i] = info = cached;
            }
            else if (KnownCrashingKeywords.Any(k => info.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                info.Status = VstPluginStatus.BrokenCrashed;
                info.SubCategories = "Crashed during scan";
            }
            else if (TryInspectBundleModuleInfoJson(info.FilePath, out bool isInstrument))
            {
                info.Status = isInstrument ? VstPluginStatus.VirtualInstrument : VstPluginStatus.ScannedOk;
                info.SubCategories = isInstrument ? "Instrument" : "Fx";
            }
            else
            {
                toProbe.Add(info);
            }

            info.IsUserAllowedOverride = allowedSet.Contains(info.FilePath);
        }

        Parallel.ForEach(
            toProbe,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            ProbePluginOutOfProcess);

        // Cache everything, including files that turned out not to be plugins, so they are not probed again.
        SaveCache(candidates);
        return candidates
            .Where(p => p.Status != VstPluginStatus.NotAPlugin)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanDirectoryRecursive(string directory, List<VstPluginInfo> candidates, HashSet<string> seenNames)
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
                    if (HasExport(file, "GetPluginFactory"))
                        AddCandidate(file, "VST3", candidates, seenNames);
                }
                else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.Contains(".vst3", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Plugin folders also hold runtime and library DLLs (api-ms-win-*, vcruntime, ...); a VST2 plugin
                    // exports VSTPluginMain (or the legacy "main").
                    if (HasExport(file, "VSTPluginMain", "main"))
                        AddCandidate(file, "VST2", candidates, seenNames);
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(directory))
            {
                ScanDirectoryRecursive(subDir, candidates, seenNames);
            }
        }
        catch
        {
        }
    }

    private static void AddCandidate(string filePath, string format, List<VstPluginInfo> candidates, HashSet<string> seenNames)
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

        candidates.Add(new VstPluginInfo
        {
            Name = rawName,
            FilePath = filePath,
            Format = format,
            Vendor = GuessVendor(filePath),
            LastWriteTicks = ticks
        });
    }

    /// <summary>
    /// True when the file is a 64-bit DLL exporting one of <paramref name="names"/>. Reads the PE export table
    /// without loading the DLL, so library and 32-bit DLLs are skipped without running their code.
    /// </summary>
    private static bool HasExport(string path, params string[] names)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.PEHeader == null || !headers.IsDll || headers.CoffHeader.Machine != Machine.Amd64)
                return false;

            var exportDir = headers.PEHeader.ExportTableDirectory;
            if (exportDir.RelativeVirtualAddress == 0 || exportDir.Size == 0)
                return false;

            // IMAGE_EXPORT_DIRECTORY: NumberOfNames at +24, AddressOfNames at +32
            var dir = pe.GetSectionData(exportDir.RelativeVirtualAddress).GetReader();
            dir.Offset = 24;
            int nameCount = dir.ReadInt32();
            dir.Offset = 32;
            int namesRva = dir.ReadInt32();

            var nameRvas = pe.GetSectionData(namesRva).GetReader();
            for (int i = 0; i < nameCount && nameRvas.RemainingBytes >= 4; i++)
            {
                var name = pe.GetSectionData(nameRvas.ReadInt32()).GetReader();
                string export = ReadAsciiZ(ref name);
                if (names.Contains(export, StringComparer.Ordinal))
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string ReadAsciiZ(ref BlobReader reader)
    {
        var sb = new StringBuilder();
        while (reader.RemainingBytes > 0 && sb.Length < 256)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static bool TryInspectBundleModuleInfoJson(string binaryPath, out bool isInstrument)
    {
        isInstrument = false;
        try
        {
            // Binary inside bundle: ...\Plugin.vst3\Contents\x86_64-win\Plugin.vst3
            string? archDir = Path.GetDirectoryName(binaryPath);
            string? contentsDir = archDir != null ? Path.GetDirectoryName(archDir) : null;
            if (contentsDir == null || !Path.GetFileName(contentsDir).Equals("Contents", StringComparison.OrdinalIgnoreCase))
                return false;

            string moduleJsonPath = Path.Combine(contentsDir, "Resources", "moduleinfo.json");
            if (!File.Exists(moduleJsonPath))
                return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(moduleJsonPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (!doc.RootElement.TryGetProperty("Classes", out var classes) || classes.ValueKind != JsonValueKind.Array)
                return false;

            var audioModules = new List<bool>();
            foreach (var cls in classes.EnumerateArray())
            {
                if (cls.TryGetProperty("Category", out var category) &&
                    category.GetString() == "Audio Module Class")
                {
                    string subCategories = cls.TryGetProperty("Sub Categories", out var sub) && sub.ValueKind == JsonValueKind.Array
                        ? string.Join("|", sub.EnumerateArray().Select(s => s.GetString()))
                        : string.Empty;
                    audioModules.Add(IsInstrumentSubCategory(subCategories));
                }
            }

            if (audioModules.Count == 0)
                return false;

            isInstrument = audioModules.All(instrument => instrument);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The first sub category is the main type: FabFilter tags synths "Instrument|Synth|Fx", effects start with "Fx".
    private static bool IsInstrumentSubCategory(string subCategories) =>
        subCategories.Split('|')[0].Trim().StartsWith("Instrument", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A .vst3 file is loaded through its VST3 entry point; a .dll through its VST2 entry point when it has one
    /// (Xpand!2's .dll also exports an empty VST3 factory).
    /// </summary>
    internal static bool PrefersVst2Entry(string pluginPath) =>
        !pluginPath.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the audio module of a VST3 factory that Leyline hosts: the first effect, else the first audio module
    /// (Serum2.vst3 holds the "Serum 2" synth and "Serum 2 FX"). Returns its class index, or -1 if there is none.
    /// </summary>
    internal static int FindVst3AudioClass(void* factory, out bool isInstrument)
    {
        isInstrument = false;
        void** vtbl = *(void***)factory;
        var countClasses = (delegate* unmanaged[Stdcall]<void*, int>)vtbl[4];
        var getClassInfo = (delegate* unmanaged[Stdcall]<void*, int, PClassInfo*, int>)vtbl[5];
        int count = countClasses(factory);

        Guid iidFactory2 = new("0007B650-F24B-4C0B-A464-EDB9F00B2ABB");
        byte* iidBytes = stackalloc byte[16];
        iidFactory2.TryWriteBytes(new Span<byte>(iidBytes, 16));

        void* factory2 = null;
        var qi = (delegate* unmanaged[Stdcall]<void*, byte*, void**, int>)vtbl[0];
        if (qi(factory, iidBytes, &factory2) != 0)
            factory2 = null;

        int firstAudioModule = -1;
        for (int i = 0; i < count; i++)
        {
            if (factory2 != null)
            {
                void** vtbl2 = *(void***)factory2;
                var getClassInfo2 = (delegate* unmanaged[Stdcall]<void*, int, PClassInfo2*, int>)vtbl2[7];
                PClassInfo2 info2 = default;
                if (getClassInfo2(factory2, i, &info2) != 0 ||
                    !Encoding.ASCII.GetString(info2.Category, 32).TrimEnd('\0').Equals("Audio Module Class", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsInstrumentSubCategory(Encoding.ASCII.GetString(info2.SubCategories, 128).TrimEnd('\0')))
                    return i;
                if (firstAudioModule < 0)
                    firstAudioModule = i;
            }
            else
            {
                // IPluginFactory has no sub categories: the first audio module is assumed to be an effect.
                PClassInfo info = default;
                if (getClassInfo(factory, i, &info) == 0 &&
                    Encoding.ASCII.GetString(info.Category, 32).TrimEnd('\0').Equals("Audio Module Class", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        isInstrument = firstAudioModule >= 0;
        return firstAudioModule;
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

            if (!proc.WaitForExit(ProbeTimeoutMs))
            {
                try { proc.Kill(); } catch { }
                info.Status = VstPluginStatus.BrokenCrashed;
                info.SubCategories = "Timed out during scan";
                return;
            }

            switch (proc.ExitCode)
            {
                case ExitCodeScannedOk:
                    info.Status = VstPluginStatus.ScannedOk;
                    info.SubCategories = "Fx";
                    break;
                case ExitCodeVirtualInstrument:
                    info.Status = VstPluginStatus.VirtualInstrument;
                    info.SubCategories = "Instrument";
                    break;
                case ExitCodeLoadFailed:
                    info.Status = VstPluginStatus.BrokenCrashed;
                    info.SubCategories = "Failed to load";
                    break;
                case ExitCodeNotAPlugin:
                    info.Status = VstPluginStatus.NotAPlugin;
                    info.SubCategories = string.Empty;
                    break;
                default:
                    info.Status = VstPluginStatus.BrokenCrashed;
                    info.SubCategories = $"Crashed (0x{proc.ExitCode:X8})";
                    break;
            }
        }
        catch
        {
            info.Status = VstPluginStatus.ScannedOk;
        }
    }

    /// <summary>
    /// Runs in an isolated child process when Leyline.exe is invoked with `--probe-vst "path"`.
    /// Loads the plugin and reads its category. Returns ExitCodeScannedOk (0, an effect),
    /// ExitCodeVirtualInstrument (10), ExitCodeBrokenPlugin (20), ExitCodeLoadFailed (21) or ExitCodeNotAPlugin (30).
    /// </summary>
    public static int RunIsolatedVstProbeCli(string pluginPath)
    {
        try
        {
            if (!File.Exists(pluginPath))
                return ExitCodeBrokenPlugin;

            if (!NativeLibrary.TryLoad(pluginPath, out IntPtr module))
                return ExitCodeLoadFailed;

            bool hasVst2 = NativeLibrary.TryGetExport(module, "VSTPluginMain", out IntPtr vstMainPtr) ||
                           NativeLibrary.TryGetExport(module, "main", out vstMainPtr);
            bool hasVst3 = NativeLibrary.TryGetExport(module, "GetPluginFactory", out IntPtr getFactoryPtr);

            // Same order as VstHostInstance: the preferred entry point, then the other one if it holds no plugin
            // (Arturia's Wurli V2.dll returns no VST2 effect but has a working VST3 factory).
            if (hasVst2 && (PrefersVst2Entry(pluginPath) || !hasVst3))
            {
                int result = ProbeVst2(vstMainPtr);
                if (result != ExitCodeNotAPlugin || !hasVst3)
                    return result;
            }

            return hasVst3 ? ProbeVst3(module, getFactoryPtr) : ExitCodeNotAPlugin;
        }
        catch
        {
            return ExitCodeBrokenPlugin;
        }
    }

    private static int ProbeVst3(IntPtr module, IntPtr getFactoryPtr)
    {
        if (NativeLibrary.TryGetExport(module, "InitDll", out IntPtr initDllPtr))
        {
            var initDll = (delegate* unmanaged[Stdcall]<byte>)initDllPtr;
            if (initDll() == 0)
                return ExitCodeBrokenPlugin;
        }

        var getFactory = (delegate* unmanaged[Stdcall]<void*>)getFactoryPtr;
        void* factory = getFactory();
        if (factory == null)
            return ExitCodeBrokenPlugin;

        int audioClass = FindVst3AudioClass(factory, out bool isInstrument);
        if (audioClass < 0)
            return ExitCodeNotAPlugin;
        return isInstrument ? ExitCodeVirtualInstrument : ExitCodeScannedOk;
    }

    private static int ProbeVst2(IntPtr vstMainPtr)
    {
        const int effFlagsIsSynth = 1 << 8;
        const int effOpen = 0, effClose = 1, effGetPlugCategory = 35;
        const int kPlugCategSynth = 2, kPlugCategShell = 10, kPlugCategGenerator = 11;

        var mainFunc = (delegate* unmanaged[Cdecl]<IntPtr, ProbeAEffect*>)vstMainPtr;
        ProbeAEffect* effect = mainFunc((IntPtr)(delegate* unmanaged[Cdecl]<ProbeAEffect*, int, int, nint, void*, float, nint>)&ProbeVst2HostCallback);
        if (effect == null || effect->Magic != 0x56737450) // 'VstP'
            return ExitCodeNotAPlugin;

        // Decided before effOpen where possible: an instrument's own startup can fail in a bare probe process.
        if ((effect->Flags & effFlagsIsSynth) != 0)
            return ExitCodeVirtualInstrument;

        var dispatcher = (delegate* unmanaged[Cdecl]<ProbeAEffect*, int, int, nint, void*, float, nint>)effect->Dispatcher;
        dispatcher(effect, effOpen, 0, 0, null, 0f);
        int category = (int)dispatcher(effect, effGetPlugCategory, 0, 0, null, 0f);

        // A plugin with no audio inputs cannot process a channel either (shell plugins report none of their own).
        bool isInstrument = category is kPlugCategSynth or kPlugCategGenerator ||
                            (effect->NumInputs == 0 && category != kPlugCategShell);

        dispatcher(effect, effClose, 0, 0, null, 0f);
        return isInstrument ? ExitCodeVirtualInstrument : ExitCodeScannedOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ProbeVst2HostCallback(ProbeAEffect* effect, int opcode, int index, nint value, void* ptr, float opt)
    {
        return Vst2HostInfo(opcode, ptr);
    }

    /// <summary>
    /// Answers the host queries plugins make while loading: the VST version and the host's vendor and product
    /// names (Kontakt crashes reading back an empty product string). Returns 0 for anything else.
    /// </summary>
    internal static nint Vst2HostInfo(int opcode, void* ptr)
    {
        switch (opcode)
        {
            case 1:  // audioMasterVersion
                return 2400;
            case 32: // audioMasterGetVendorString
            case 33: // audioMasterGetProductString
                if (ptr == null)
                    return 0;
                ReadOnlySpan<byte> text = "Leyline\0"u8;
                text.CopyTo(new Span<byte>(ptr, text.Length));
                return 1;
            case 34: // audioMasterGetVendorVersion
                return 1000;
            default:
                return 0;
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
            if (File.Exists(LegacyCacheFilePath))
                File.Delete(LegacyCacheFilePath);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeAEffect
    {
        public int Magic;
        public void* Dispatcher;
        public void* Process;
        public void* SetParameter;
        public void* GetParameter;
        public int NumPrograms;
        public int NumParams;
        public int NumInputs;
        public int NumOutputs;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PClassInfo
    {
        public fixed byte Cid[16];
        public int Cardinality;
        public fixed byte Category[32];
        public fixed byte Name[64];
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
