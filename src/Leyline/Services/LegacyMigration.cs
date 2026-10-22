using System.IO;
using Microsoft.Win32;

namespace Leyline.Services;

/// <summary>
/// One-time carry-over from the app's former name (VoidPatch): copies %APPDATA%\VoidPatch to %APPDATA%\Leyline
/// (config, soundboard samples, VST cache, driver-node records) and moves the autostart Run entry.
/// The old folder is left in place as a backup.
/// </summary>
public static class LegacyMigration
{
    private const string LegacyName = "VoidPatch";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Run()
    {
        try { MigrateAppData(); } catch { }
        try { MigrateStartupEntry(); } catch { }
    }

    private static void MigrateAppData()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string legacyDir = Path.Combine(roaming, LegacyName);
        string newDir = Path.Combine(roaming, "Leyline");

        if (Directory.Exists(newDir) || !Directory.Exists(legacyDir))
            return;

        CopyDirectory(legacyDir, newDir);

        string legacyNodes = Path.Combine(newDir, "voidpatch-driver-nodes.json");
        if (File.Exists(legacyNodes))
            File.Move(legacyNodes, Path.Combine(newDir, "leyline-driver-nodes.json"), overwrite: true);

        // Rewrite stored sample paths (…\Roaming\VoidPatch\Soundboard\…) and brand strings in the config.
        string configPath = Path.Combine(newDir, "config.json");
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            File.WriteAllText(configPath, json.Replace(LegacyName, "Leyline", StringComparison.Ordinal));
        }
    }

    private static void MigrateStartupEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
        if (key?.GetValue(LegacyName) is not string legacyCmd)
            return;

        key.DeleteValue(LegacyName, false);

        string exePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        bool minimized = legacyCmd.Contains("--minimized", StringComparison.OrdinalIgnoreCase);
        key.SetValue("Leyline", minimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (string dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
