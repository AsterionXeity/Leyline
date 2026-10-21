using System.Text.Json.Serialization;
using VoidPatch.ViewModels;

namespace VoidPatch.Models;

public enum PadPlaybackMode
{
    OneShot,
    Overlap,
    Loop,
    StopToggle
}

public sealed class SoundPadModel : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Sound Pad";
    private string _emoji = "🔊";
    private string _colorHex = "#00E5FF";
    private string _filePath = string.Empty;
    private float _volume = 0.90f;
    private bool _sendToMonitor = true;
    private bool _sendToStream = true;
    private PadPlaybackMode _playbackMode = PadPlaybackMode.StopToggle;
    private string _hotkeyDisplay = string.Empty;
    private uint _hotkeyModifiers; // Win32 MOD_CONTROL / MOD_SHIFT / MOD_ALT
    private uint _hotkeyVirtualKey;

    private bool _isPlaying;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Emoji
    {
        get => _emoji;
        set => SetProperty(ref _emoji, value);
    }

    public string ColorHex
    {
        get => _colorHex;
        set => SetProperty(ref _colorHex, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public float Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0f, 2.0f)))
            {
                OnPropertyChanged(nameof(VolumePercent));
                OnPropertyChanged(nameof(VolumePercentText));
                OnPropertyChanged(nameof(VolumeDouble));
            }
        }
    }

    [JsonIgnore]
    public double VolumeDouble
    {
        get => _volume;
        set => Volume = (float)value;
    }

    [JsonIgnore]
    public int VolumePercent => (int)Math.Round(_volume * 100f);

    [JsonIgnore]
    public string VolumePercentText => $"Vol {VolumePercent}%";

    public bool SendToMonitor
    {
        get => _sendToMonitor;
        set => SetProperty(ref _sendToMonitor, value);
    }

    public bool SendToStream
    {
        get => _sendToStream;
        set => SetProperty(ref _sendToStream, value);
    }

    public PadPlaybackMode PlaybackMode
    {
        get => _playbackMode;
        set => SetProperty(ref _playbackMode, value);
    }

    public string HotkeyDisplay
    {
        get => _hotkeyDisplay;
        set => SetProperty(ref _hotkeyDisplay, value);
    }

    public uint HotkeyModifiers
    {
        get => _hotkeyModifiers;
        set => SetProperty(ref _hotkeyModifiers, value);
    }

    public uint HotkeyVirtualKey
    {
        get => _hotkeyVirtualKey;
        set => SetProperty(ref _hotkeyVirtualKey, value);
    }

    [JsonIgnore]
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }
}
