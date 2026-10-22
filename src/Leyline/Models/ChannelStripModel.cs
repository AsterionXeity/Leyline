using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Leyline.ViewModels;

namespace Leyline.Models;

public enum ChannelSourceType
{
    MicrophoneInput,
    VirtualDeviceLoopback,
    ProcessCapture,
    SoundboardChannel,
    AppGroupChannel
}

public sealed class AssignedAppModel : ObservableObject
{
    private string _appName = string.Empty;
    private string _processName = string.Empty;
    private string _executablePath = string.Empty;
    private string _iconEmoji = "🎮";
    private bool _isAvailable;
    private uint _activeProcessId;

    public string AppName
    {
        get => _appName;
        set => SetProperty(ref _appName, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetProperty(ref _processName, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    public string IconEmoji
    {
        get => _iconEmoji;
        set => SetProperty(ref _iconEmoji, value);
    }

    [JsonIgnore]
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value))
                OnPropertyChanged(nameof(AvailabilityText));
        }
    }

    [JsonIgnore]
    public string AvailabilityText => _isAvailable ? "(Active)" : "(Unavailable)";

    [JsonIgnore]
    public uint ActiveProcessId
    {
        get => _activeProcessId;
        set => SetProperty(ref _activeProcessId, value);
    }
}

public sealed class MixSendState : ObservableObject
{
    private bool _isEnabled = true;
    private float _volume = 0.85f;
    private bool _isMuted;
    private double _currentMeterL;
    private double _currentMeterR;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsDisabled));
                OnPropertyChanged(nameof(CellStateKey));
                if (!value)
                {
                    CurrentMeterL = 0;
                    CurrentMeterR = 0;
                }
            }
        }
    }

    [JsonIgnore]
    public bool IsDisabled => !_isEnabled;

    [JsonIgnore]
    public string CellStateKey => !_isEnabled ? "Disabled" : (_isMuted ? "Muted" : "Active");

    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (SetProperty(ref _volume, clamped))
            {
                OnPropertyChanged(nameof(VolumePercent));
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

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteIconText));
                OnPropertyChanged(nameof(CellStateKey));
                if (value)
                {
                    CurrentMeterL = 0;
                    CurrentMeterR = 0;
                }
            }
        }
    }

    [JsonIgnore]
    public string MuteIconText => _isMuted ? "\uE74F" : "\uE767";

    [JsonIgnore]
    public double CurrentMeterL
    {
        get => _currentMeterL;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (clamped == 0.0 ? _currentMeterL != 0.0 : Math.Abs(_currentMeterL - clamped) > 0.0025)
            {
                _currentMeterL = clamped;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public double CurrentMeterR
    {
        get => _currentMeterR;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (clamped == 0.0 ? _currentMeterR != 0.0 : Math.Abs(_currentMeterR - clamped) > 0.0025)
            {
                _currentMeterR = clamped;
                OnPropertyChanged();
            }
        }
    }

    public void UpdateMeters(float channelPeakL, float channelPeakR, float channelInputVolume, bool channelInputMuted)
    {
        if (channelInputMuted || _isMuted || !_isEnabled)
        {
            CurrentMeterL = 0.0;
            CurrentMeterR = 0.0;
            return;
        }

        CurrentMeterL = Math.Clamp(channelPeakL * channelInputVolume * _volume, 0f, 1f);
        CurrentMeterR = Math.Clamp(channelPeakR * channelInputVolume * _volume, 0f, 1f);
    }
}

public sealed class OutputMixBusModel : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Mix";
    private string _icon = "🎧";
    private string _outputDeviceId = string.Empty;
    private float _masterVolume = 0.95f;
    private bool _isMuted;
    private bool _isListeningTarget;
    private float _peakLeft;
    private float _peakRight;

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

    public string Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public string OutputDeviceId
    {
        get => _outputDeviceId;
        set
        {
            if (SetProperty(ref _outputDeviceId, value))
                OnPropertyChanged(nameof(OutputSubtitle));
        }
    }

    [JsonIgnore]
    public string OutputSubtitle => (!string.IsNullOrWhiteSpace(_outputDeviceId) || _isListeningTarget)
        ? "1 output"
        : string.Empty;

    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            if (SetProperty(ref _masterVolume, Math.Clamp(value, 0f, 1f)))
                OnPropertyChanged(nameof(MasterVolumeDouble));
        }
    }

    [JsonIgnore]
    public double MasterVolumeDouble
    {
        get => _masterVolume;
        set => MasterVolume = (float)value;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    [JsonIgnore]
    public bool IsListeningTarget
    {
        get => _isListeningTarget;
        set
        {
            if (SetProperty(ref _isListeningTarget, value))
                OnPropertyChanged(nameof(OutputSubtitle));
        }
    }

    [JsonIgnore]
    public float PeakLeft
    {
        get => _peakLeft;
        set
        {
            if (SetProperty(ref _peakLeft, Math.Clamp(value, 0f, 1f)))
                OnPropertyChanged(nameof(PeakLeftDouble));
        }
    }

    [JsonIgnore]
    public double PeakLeftDouble => _peakLeft;

    [JsonIgnore]
    public float PeakRight
    {
        get => _peakRight;
        set
        {
            if (SetProperty(ref _peakRight, Math.Clamp(value, 0f, 1f)))
                OnPropertyChanged(nameof(PeakRightDouble));
        }
    }

    [JsonIgnore]
    public double PeakRightDouble => _peakRight;
}

public sealed class ChannelStripModel : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "Channel";
    private string _icon = "🎙️";
    private string _accentHex = "#29B6F6";
    private ChannelSourceType _sourceType = ChannelSourceType.AppGroupChannel;
    private string _deviceId = string.Empty;
    private string _cableId = string.Empty;
    private string _deviceName = "None (Unselected)";
    private uint _targetProcessId;
    private string _targetProcessName = string.Empty;
    private float _inputGainDb;
    private float _inputVolume = 0.90f;
    private bool _isInputMuted;

    // 4 Core Mix Sends + Dynamic Custom Mix Sends
    private MixSendState _personalMix = new() { IsEnabled = true, Volume = 0.85f };
    private MixSendState _streamMix = new() { IsEnabled = true, Volume = 0.85f };
    private MixSendState _discordMix = new() { IsEnabled = false, Volume = 0.80f };
    private MixSendState _micRecMix = new() { IsEnabled = true, Volume = 0.85f };
    private Dictionary<string, MixSendState> _extraMixSends = new();

    private bool _isLinked;
    private DspChainModel _dsp = new();
    private ObservableCollection<VstSlotModel> _vstSlots = new();
    private ObservableCollection<AssignedAppModel> _assignedApps = new();

    private float _peakLeft;
    private float _peakRight;
    private string _statusText = "Ready";
    private int _routedAppCount;
    private string _routedAppsSummary = "No apps assigned";

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

    public string Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(GlyphIcon));
        }
    }

    [JsonIgnore]
    public string GlyphIcon => _icon switch
    {
        "🖥️" => "\uE7F4",
        "🌐" => "\uE774",
        "📺" => "\uE95A",
        "🎮" => "\uE7FC",
        "🎵" => "\uEC4F",
        "💬" => "\uE8BD",
        "〰️" => "\uE9E9",
        "🎙️" => "\uE720",
        "🎸" => "\uEC4F",
        _ => "\uE7F4"
    };

    public string AccentHex
    {
        get => _accentHex;
        set
        {
            if (SetProperty(ref _accentHex, value))
                OnPropertyChanged(nameof(IconForegroundHex));
        }
    }

    [JsonIgnore]
    public string IconForegroundHex => _accentHex is "#FBC02D" or "#FDD835" or "#29B6F6" or "#00E676"
        ? "#141418"
        : "#FFFFFF";

    public ChannelSourceType SourceType
    {
        get => _sourceType;
        set
        {
            if (SetProperty(ref _sourceType, value))
            {
                OnPropertyChanged(nameof(SourceTypeBadge));
                OnPropertyChanged(nameof(IsHardwareInput));
                OnPropertyChanged(nameof(IsAppGroupOrVirtual));
            }
        }
    }

    [JsonIgnore]
    public bool IsHardwareInput => _sourceType == ChannelSourceType.MicrophoneInput;

    [JsonIgnore]
    public bool IsAppGroupOrVirtual => _sourceType != ChannelSourceType.MicrophoneInput;

    [JsonIgnore]
    public string SourceTypeBadge => SourceType switch
    {
        ChannelSourceType.MicrophoneInput => "INPUT DEVICE",
        ChannelSourceType.VirtualDeviceLoopback => "VIRTUAL BUS",
        ChannelSourceType.ProcessCapture => "APP STREAM",
        ChannelSourceType.SoundboardChannel => "BUILT-IN",
        ChannelSourceType.AppGroupChannel => "APP CHANNEL",
        _ => "AUDIO"
    };

    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }

    /// <summary>
    /// Leyline cable this channel uses (LeylineCableService.Cables), empty for none. Apps are routed to the cable's
    /// playback endpoint (DeviceId) and the channel records the cable's recording endpoint.
    /// </summary>
    public string CableId
    {
        get => _cableId;
        set => SetProperty(ref _cableId, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public uint TargetProcessId
    {
        get => _targetProcessId;
        set => SetProperty(ref _targetProcessId, value);
    }

    public string TargetProcessName
    {
        get => _targetProcessName;
        set => SetProperty(ref _targetProcessName, value);
    }

    public float InputGainDb
    {
        get => _inputGainDb;
        set
        {
            if (SetProperty(ref _inputGainDb, Math.Clamp(value, -24f, 24f)))
            {
                OnPropertyChanged(nameof(InputGainDbDouble));
                OnPropertyChanged(nameof(InputGainDbText));
            }
        }
    }

    [JsonIgnore]
    public double InputGainDbDouble
    {
        get => _inputGainDb;
        set => InputGainDb = (float)value;
    }

    [JsonIgnore]
    public string InputGainDbText => $"{_inputGainDb:+0.0;-0.0;0.0} dB";

    public float InputVolume
    {
        get => _inputVolume;
        set
        {
            if (SetProperty(ref _inputVolume, Math.Clamp(value, 0f, 1f)))
            {
                OnPropertyChanged(nameof(InputVolumePercent));
                OnPropertyChanged(nameof(InputVolumeDouble));
                PropagateMixMeters();
            }
        }
    }

    [JsonIgnore]
    public double InputVolumeDouble
    {
        get => _inputVolume;
        set => InputVolume = (float)value;
    }

    [JsonIgnore]
    public int InputVolumePercent => (int)Math.Round(_inputVolume * 100f);

    public bool IsInputMuted
    {
        get => _isInputMuted;
        set
        {
            if (SetProperty(ref _isInputMuted, value))
            {
                OnPropertyChanged(nameof(InputMuteIconText));
                PropagateMixMeters();
            }
        }
    }

    [JsonIgnore]
    public string InputMuteIconText => _isInputMuted ? "🔇" : "🔊";

    public MixSendState PersonalMix
    {
        get => _personalMix;
        set => SetProperty(ref _personalMix, value);
    }

    public MixSendState StreamMix
    {
        get => _streamMix;
        set => SetProperty(ref _streamMix, value);
    }

    public MixSendState DiscordMix
    {
        get => _discordMix;
        set => SetProperty(ref _discordMix, value);
    }

    public MixSendState MicRecMix
    {
        get => _micRecMix;
        set => SetProperty(ref _micRecMix, value);
    }

    public Dictionary<string, MixSendState> ExtraMixSends
    {
        get => _extraMixSends;
        set => SetProperty(ref _extraMixSends, value);
    }

    public bool IsLinked
    {
        get => _isLinked;
        set => SetProperty(ref _isLinked, value);
    }

    public DspChainModel Dsp
    {
        get => _dsp;
        set => SetProperty(ref _dsp, value);
    }

    public ObservableCollection<VstSlotModel> VstSlots
    {
        get => _vstSlots;
        set => SetProperty(ref _vstSlots, value);
    }

    public ObservableCollection<AssignedAppModel> AssignedApps
    {
        get => _assignedApps;
        set => SetProperty(ref _assignedApps, value);
    }

    [JsonIgnore]
    public int ActiveFxCount => Dsp.ActiveCount + VstSlots.Count(v => v.IsEnabled);

    [JsonIgnore]
    public bool HasActiveFx => ActiveFxCount > 0;

    [JsonIgnore]
    public string FxSummaryText => ActiveFxCount == 0 ? "Audio effects" : $"Effects ({ActiveFxCount})";

    public void NotifyFxChanged()
    {
        OnPropertyChanged(nameof(ActiveFxCount));
        OnPropertyChanged(nameof(HasActiveFx));
        OnPropertyChanged(nameof(FxSummaryText));
    }

    public void NotifyAppsChanged()
    {
        OnPropertyChanged(nameof(TotalAppBadgeCount));
        OnPropertyChanged(nameof(HasRoutedApps));
        OnPropertyChanged(nameof(RoutedAppBadge));
    }

    [JsonIgnore]
    public float PeakLeft
    {
        get => _peakLeft;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (clamped == 0f ? _peakLeft != 0f : MathF.Abs(_peakLeft - clamped) > 0.0025f)
            {
                _peakLeft = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PeakLeftDouble));
                OnPropertyChanged(nameof(PeakDbText));
                OnPropertyChanged(nameof(PersonalMixMeter));
                OnPropertyChanged(nameof(StreamMixMeter));
                OnPropertyChanged(nameof(DiscordMixMeter));
                OnPropertyChanged(nameof(MicRecMixMeter));
                PropagateMixMeters();
            }
        }
    }

    [JsonIgnore]
    public double PeakLeftDouble => _peakLeft;

    [JsonIgnore]
    public float PeakRight
    {
        get => _peakRight;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (clamped == 0f ? _peakRight != 0f : MathF.Abs(_peakRight - clamped) > 0.0025f)
            {
                _peakRight = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PeakRightDouble));
                PropagateMixMeters();
            }
        }
    }

    [JsonIgnore]
    public double PeakRightDouble => _peakRight;

    private void PropagateMixMeters()
    {
        _personalMix.UpdateMeters(_peakLeft, _peakRight, _inputVolume, _isInputMuted);
        _streamMix.UpdateMeters(_peakLeft, _peakRight, _inputVolume, _isInputMuted);
        _discordMix.UpdateMeters(_peakLeft, _peakRight, _inputVolume, _isInputMuted);
        _micRecMix.UpdateMeters(_peakLeft, _peakRight, _inputVolume, _isInputMuted);
    }

    [JsonIgnore]
    public float PersonalMixMeter => (IsInputMuted || PersonalMix.IsMuted || !PersonalMix.IsEnabled) ? 0f : Math.Clamp(_peakLeft * InputVolume * PersonalMix.Volume, 0f, 1f);

    [JsonIgnore]
    public float StreamMixMeter => (IsInputMuted || StreamMix.IsMuted || !StreamMix.IsEnabled) ? 0f : Math.Clamp(_peakLeft * InputVolume * StreamMix.Volume, 0f, 1f);

    [JsonIgnore]
    public float DiscordMixMeter => (IsInputMuted || DiscordMix.IsMuted || !DiscordMix.IsEnabled) ? 0f : Math.Clamp(_peakLeft * InputVolume * DiscordMix.Volume, 0f, 1f);

    [JsonIgnore]
    public float MicRecMixMeter => (IsInputMuted || MicRecMix.IsMuted || !MicRecMix.IsEnabled) ? 0f : Math.Clamp(_peakLeft * InputVolume * MicRecMix.Volume, 0f, 1f);

    [JsonIgnore]
    public string PeakDbText
    {
        get
        {
            float max = Math.Max(_peakLeft, _peakRight);
            if (max <= 0.0001f) return "-∞ dB";
            float db = 20f * MathF.Log10(max);
            return $"{db:0.0} dB";
        }
    }

    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    [JsonIgnore]
    public int RoutedAppCount
    {
        get => _routedAppCount;
        set
        {
            if (SetProperty(ref _routedAppCount, value))
            {
                OnPropertyChanged(nameof(TotalAppBadgeCount));
                OnPropertyChanged(nameof(HasRoutedApps));
                OnPropertyChanged(nameof(RoutedAppBadge));
            }
        }
    }

    [JsonIgnore]
    public int TotalAppBadgeCount => Math.Max(_assignedApps.Count, _routedAppCount);

    [JsonIgnore]
    public bool HasRoutedApps => TotalAppBadgeCount > 0;

    [JsonIgnore]
    public string RoutedAppBadge => $"+{TotalAppBadgeCount}";

    [JsonIgnore]
    public string RoutedAppsSummary
    {
        get => _routedAppsSummary;
        set => SetProperty(ref _routedAppsSummary, value);
    }
}
