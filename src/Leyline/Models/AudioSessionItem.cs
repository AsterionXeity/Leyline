using System.Text.Json.Serialization;
using Leyline.ViewModels;

namespace Leyline.Models;

public sealed class AudioSessionItem : ObservableObject
{
    private uint _processId;
    private string _processName = string.Empty;
    private string _displayName = string.Empty;
    private string _executablePath = string.Empty;
    private string _currentEndpointId = string.Empty;
    private string _currentEndpointName = string.Empty;
    private string _routedEndpointId = string.Empty;
    private float _volume = 1.0f;
    private bool _isMuted;
    private float _peakLevel;

    public uint ProcessId
    {
        get => _processId;
        set
        {
            if (SetProperty(ref _processId, value))
                OnPropertyChanged(nameof(ProcessIdBadgeText));
        }
    }

    [JsonIgnore]
    public string ProcessIdBadgeText => $"PID {_processId}";

    public string ProcessName
    {
        get => _processName;
        set => SetProperty(ref _processName, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set => SetProperty(ref _executablePath, value);
    }

    public string CurrentEndpointId
    {
        get => _currentEndpointId;
        set => SetProperty(ref _currentEndpointId, value);
    }

    public string CurrentEndpointName
    {
        get => _currentEndpointName;
        set => SetProperty(ref _currentEndpointName, value);
    }

    public string RoutedEndpointId
    {
        get => _routedEndpointId;
        set => SetProperty(ref _routedEndpointId, value);
    }

    public float Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0f, 1f)))
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
        set => SetProperty(ref _isMuted, value);
    }

    public float PeakLevel
    {
        get => _peakLevel;
        set
        {
            if (SetProperty(ref _peakLevel, Math.Clamp(value, 0f, 1f)))
                OnPropertyChanged(nameof(PeakLevelDouble));
        }
    }

    [JsonIgnore]
    public double PeakLevelDouble => _peakLevel;
}
