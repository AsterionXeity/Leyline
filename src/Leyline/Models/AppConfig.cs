namespace Leyline.Models;

public sealed class AppConfig
{
    public bool IsLightMode { get; set; } = false;
    public int ListeningMixIndex { get; set; } = 0; // 0: Personal Mix, 1: Stream Mix, 2: Discord Record Mix, 3: Mic Rec Mix

    // Default to empty ("None / Unselected") so no hardware device is auto-assigned without user consent
    public string MonitorOutputDeviceId { get; set; } = string.Empty;
    public string StreamOutputDeviceId { get; set; } = string.Empty;
    public string DiscordOutputDeviceId { get; set; } = string.Empty;
    public string MicRecOutputDeviceId { get; set; } = string.Empty;

    public float MonitorMasterVolume { get; set; } = 0.95f;
    public float StreamMasterVolume { get; set; } = 0.95f;
    public bool IsMonitorMasterMuted { get; set; }
    public bool IsStreamMasterMuted { get; set; }
    public float SoundboardMasterVolume { get; set; } = 0.90f;

    public DspChainModel MonitorMasterDsp { get; set; } = new();
    public DspChainModel StreamMasterDsp { get; set; } = new();

    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = false;

    public List<string> CustomVstDirectories { get; set; } = new();
    public List<string> AllowedBlockedVstPaths { get; set; } = new();
    public List<string> KnownSuggestedApps { get; set; } =
    [
        "osu!",
        "OblivionRemastered-Win64-Shipping",
        "Risk of Rain 2",
        "NTE: Neverness to Everness",
        "AION2",
        "Endfield",
        "AFK Journey",
        "Arknights",
        "Euro Truck Simulator 2",
        "Spotify",
        "Discord",
        "chrome",
        "obs64"
    ];
    public Dictionary<string, string> SavedAppRoutes { get; set; } = new();

    public List<OutputMixBusModel> MixBuses { get; set; } = new();
    public List<ChannelStripModel> Channels { get; set; } = new();
    public List<SoundPadModel> SoundPads { get; set; } = new();
}
