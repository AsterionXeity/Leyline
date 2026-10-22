using System.Text.Json.Serialization;
using Leyline.ViewModels;

namespace Leyline.Models;

public enum VstPluginStatus
{
    ScannedOk = 0,
    BrokenCrashed = 1,
    VirtualInstrument = 2,
    NotAPlugin = 3      // exports a VST entry point but has no audio plugin (cached so it is not probed again)
}

public sealed class VstPluginInfo : ObservableObject
{
    private string _name = string.Empty;
    private string _filePath = string.Empty;
    private string _format = "VST3";
    private string _vendor = "External Plugin";
    private string _subCategories = "Fx";
    private VstPluginStatus _status = VstPluginStatus.ScannedOk;
    private bool _isUserAllowedOverride;
    private long _lastWriteTicks;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public string Format
    {
        get => _format;
        set => SetProperty(ref _format, value);
    }

    public string Vendor
    {
        get => _vendor;
        set => SetProperty(ref _vendor, value);
    }

    public string SubCategories
    {
        get => _subCategories;
        set => SetProperty(ref _subCategories, value);
    }

    public VstPluginStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsUsable));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsUserAllowedOverride
    {
        get => _isUserAllowedOverride;
        set
        {
            if (SetProperty(ref _isUserAllowedOverride, value))
            {
                OnPropertyChanged(nameof(IsUsable));
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(OverrideButtonText));
            }
        }
    }

    public long LastWriteTicks
    {
        get => _lastWriteTicks;
        set => SetProperty(ref _lastWriteTicks, value);
    }

    [JsonIgnore]
    public bool IsUsable => Status == VstPluginStatus.ScannedOk || IsUserAllowedOverride;

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        VstPluginStatus.ScannedOk => "Verified Audio Effect",
        VstPluginStatus.BrokenCrashed => IsUserAllowedOverride ? "Crashed during scan (Allowed by User)" : "Blocked (Crashed during scan)",
        VstPluginStatus.VirtualInstrument => IsUserAllowedOverride ? "Virtual Instrument (Allowed by User)" : "Blocked (Virtual Instrument)",
        _ => "Audio Plugin"
    };

    [JsonIgnore]
    public string OverrideButtonText => IsUserAllowedOverride ? "Block again" : "Allow & Use Anyway";

    public override string ToString() => Status == VstPluginStatus.ScannedOk
        ? $"[{Format}] {Name}"
        : $"[{Format}] {Name} (User Allowed)";
}
