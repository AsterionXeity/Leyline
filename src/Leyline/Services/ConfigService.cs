using System.IO;
using System.Text.Json;
using Leyline.Audio;
using Leyline.Models;

namespace Leyline.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string GetConfigFilePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Leyline");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static AppConfig LoadOrCreateDefault()
    {
        string path = GetConfigFilePath();
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null && cfg.Channels.Count > 0)
                {
                    if (cfg.SoundPads.Count == 0)
                        cfg.SoundPads = SampleGenerator.EnsureDefaultPads();
                    if (cfg.MixBuses.Count == 0)
                        cfg.MixBuses = CreateDefaultMixBuses();
                    return cfg;
                }
            }
            catch
            {
            }
        }

        var defaultConfig = CreateDefaultConfig();
        Save(defaultConfig);
        return defaultConfig;
    }

    private static string? _lastSavedJson;

    public static void Save(AppConfig config)
    {
        try
        {
            string path = GetConfigFilePath();
            string json = JsonSerializer.Serialize(config, JsonOptions);
            if (json == _lastSavedJson && File.Exists(path))
                return;   // auto-save runs after every change; skip rewriting an identical file
            File.WriteAllText(path, json);
            _lastSavedJson = json;
        }
        catch
        {
        }
    }

    public static bool ExportBackup(AppConfig config, string destinationFilePath, out string error)
    {
        error = string.Empty;
        try
        {
            string? dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(destinationFilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool ImportBackup(string sourceFilePath, out AppConfig? importedConfig, out string error)
    {
        importedConfig = null;
        error = string.Empty;
        try
        {
            if (!File.Exists(sourceFilePath))
            {
                error = "The selected backup file does not exist.";
                return false;
            }

            string json = File.ReadAllText(sourceFilePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg == null)
            {
                error = "The selected file is not a valid Leyline configuration backup.";
                return false;
            }

            if (cfg.Channels.Count == 0)
            {
                var def = CreateDefaultConfig();
                cfg.Channels = def.Channels;
            }
            if (cfg.MixBuses.Count == 0)
                cfg.MixBuses = CreateDefaultMixBuses();
            if (cfg.SoundPads.Count == 0)
                cfg.SoundPads = SampleGenerator.EnsureDefaultPads();

            importedConfig = cfg;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static List<OutputMixBusModel> CreateDefaultMixBuses() =>
    [
        new OutputMixBusModel { Id = "personal", Name = "Personal Mix",       Icon = "🎧", IsListeningTarget = true },
        new OutputMixBusModel { Id = "stream",   Name = "Stream Mix",         Icon = "📡" },
        new OutputMixBusModel { Id = "discord",  Name = "Discord Record Mix", Icon = "👥" },
        new OutputMixBusModel { Id = "micrec",   Name = "Mic Rec Mix",        Icon = "🎙️" }
    ];

    public static AppConfig CreateDefaultConfig()
    {
        var devices = AudioEngine.EnumerateDevices();
        var config = new AppConfig
        {
            // Do NOT auto-assign a default monitoring device on startup (user explicitly selects it)
            MonitorOutputDeviceId = string.Empty,
            StreamOutputDeviceId = string.Empty,
            MixBuses = CreateDefaultMixBuses()
        };

        // Default configuration: ONLY the System channel. All other channels can be added by the user.
        AddAppGroupRow(config, devices, "System", "🖥️", "#29B6F6", "System",
            personal: true, stream: false, discord: false, micRec: false, []);

        config.SoundPads = SampleGenerator.EnsureDefaultPads();
        return config;
    }

    public static ChannelStripModel CreatePresetChannel(string presetKey, IReadOnlyList<AudioDeviceInfo> devices)
    {
        var tempConfig = new AppConfig();
        var devList = devices.ToList();

        switch (presetKey.ToLowerInvariant())
        {
            case "system":
                AddAppGroupRow(tempConfig, devList, "System", "🖥️", "#29B6F6", "System",
                    personal: true, stream: false, discord: false, micRec: false, []);
                return tempConfig.Channels[0];

            case "browser":
                AddAppGroupRow(tempConfig, devList, "Browser", "🌐", "#9C27B0", "Browser",
                    personal: true, stream: true, discord: false, micRec: true,
                    [
                        new AssignedAppModel { AppName = "Google Chrome", ProcessName = "chrome", IconEmoji = "🌐" },
                        new AssignedAppModel { AppName = "Mozilla Firefox", ProcessName = "firefox", IconEmoji = "🦊" },
                        new AssignedAppModel { AppName = "Microsoft Edge", ProcessName = "msedge", IconEmoji = "🧭" }
                    ],
                    micRecMuted: true);
                return tempConfig.Channels[0];

            case "obs":
            case "obs/stream":
            case "stream mix":
                AddAppGroupRow(tempConfig, devList, "OBS/Stream", "📺", "#37474F", "",
                    personal: true, stream: true, discord: false, micRec: false,
                    [
                        new AssignedAppModel { AppName = "OBS Studio", ProcessName = "obs64", IconEmoji = "📺" }
                    ]);
                return tempConfig.Channels[0];

            case "game":
                AddAppGroupRow(tempConfig, devList, "Game", "🎮", "#8D3B3B", "Game",
                    personal: true, stream: true, discord: false, micRec: true,
                    [],
                    micRecMuted: true);
                return tempConfig.Channels[0];

            case "music":
                AddAppGroupRow(tempConfig, devList, "Music", "🎵", "#AB47BC", "Music",
                    personal: true, stream: true, discord: false, micRec: true,
                    [
                        new AssignedAppModel { AppName = "Spotify", ProcessName = "Spotify", IconEmoji = "🎵" }
                    ],
                    micRecMuted: true);
                return tempConfig.Channels[0];

            case "voice":
            case "voice chat":
                AddAppGroupRow(tempConfig, devList, "Voice Chat", "💬", "#FBC02D", "VoiceChat",
                    personal: true, stream: true, discord: true, micRec: false,
                    [
                        new AssignedAppModel { AppName = "Discord", ProcessName = "Discord", IconEmoji = "💬" }
                    ],
                    streamMuted: true);
                return tempConfig.Channels[0];

            case "soundboard":
                return new ChannelStripModel
                {
                    Name = "Soundboard",
                    Icon = "〰️",
                    AccentHex = "#FF7043",
                    SourceType = ChannelSourceType.SoundboardChannel,
                    DeviceId = string.Empty,
                    DeviceName = "Leyline Built-in Sampler",
                    InputVolume = 0.90f,
                    PersonalMix = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = false },
                    StreamMix   = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = false },
                    DiscordMix  = new MixSendState { IsEnabled = false, Volume = 0.80f, IsMuted = false },
                    MicRecMix   = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = false }
                };

            case "microphone":
            default:
                var micStrip = new ChannelStripModel
                {
                    Name = "Microphone",
                    Icon = "🎙️",
                    AccentHex = "#448AFF",
                    SourceType = ChannelSourceType.MicrophoneInput,
                    DeviceId = string.Empty,
                    DeviceName = "None (Unselected)",
                    InputVolume = 0.90f,
                    PersonalMix = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = true },
                    StreamMix   = new MixSendState { IsEnabled = true, Volume = 0.90f, IsMuted = false },
                    DiscordMix  = new MixSendState { IsEnabled = false, Volume = 0.85f, IsMuted = false },
                    MicRecMix   = new MixSendState { IsEnabled = true, Volume = 0.90f, IsMuted = false }
                };
                micStrip.Dsp.GateEnabled = true;
                micStrip.Dsp.CompEnabled = true;
                return micStrip;
        }
    }

    private static void AddAppGroupRow(
        AppConfig config,
        List<AudioDeviceInfo> devices,
        string name,
        string icon,
        string accentHex,
        string cableId,
        bool personal,
        bool stream,
        bool discord,
        bool micRec,
        List<AssignedAppModel> initialApps,
        bool streamMuted = false,
        bool micRecMuted = false)
    {
        // The channel's cable is connected and its endpoint resolved when the channel is added (MainViewModel).
        var cable = LeylineCableService.Find(cableId);

        var strip = new ChannelStripModel
        {
            Name = name,
            Icon = icon,
            AccentHex = accentHex,
            SourceType = ChannelSourceType.AppGroupChannel,
            CableId = cable?.Id ?? string.Empty,
            DeviceId = string.Empty,
            DeviceName = cable != null ? $"{cable.DisplayName} ({LeylineDriverService.DeviceName})" : "None (Direct App Capture)",
            InputVolume = 0.90f,
            PersonalMix = new MixSendState { IsEnabled = personal, Volume = 0.85f, IsMuted = false },
            StreamMix   = new MixSendState { IsEnabled = stream,   Volume = 0.80f, IsMuted = streamMuted },
            DiscordMix  = new MixSendState { IsEnabled = discord,  Volume = 0.80f, IsMuted = false },
            MicRecMix   = new MixSendState { IsEnabled = micRec,   Volume = 0.85f, IsMuted = micRecMuted }
        };

        foreach (var app in initialApps)
            strip.AssignedApps.Add(app);

        strip.NotifyAppsChanged();
        config.Channels.Add(strip);
    }
}
