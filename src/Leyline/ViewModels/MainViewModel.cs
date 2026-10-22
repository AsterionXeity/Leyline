using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Leyline.Audio;
using Leyline.Models;
using Leyline.Services;
using Leyline.Vst;

namespace Leyline.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly ConfigAutoSaver? _autoSaver;

    // View-model settings that write straight to Config without saving it
    private static readonly HashSet<string> AutoSavedViewModelProperties =
    [
        nameof(MonitorMasterVolume), nameof(StreamMasterVolume),
        nameof(IsMonitorMasterMuted), nameof(IsStreamMasterMuted),
        nameof(SoundboardMasterVolume)
    ];
    // Meters update on the UI's frame callback, at most this often (120 per second)
    private const double MinMeterIntervalMs = 8.0;
    private TimeSpan _lastMeterFrame;
    private readonly DispatcherTimer _sessionPollTimer;

    private AudioDeviceInfo? _selectedMonitorDevice;
    private AudioDeviceInfo? _selectedStreamDevice;
    private float _monitorMasterPeakL;
    private float _monitorMasterPeakR;
    private float _streamMasterPeakL;
    private float _streamMasterPeakR;
    private string _statusBannerText = "Leyline Audio Engine Active • 48kHz / 32-bit Float";
    private bool _isRecordingStream;
    private bool _isDriverBranded;
    private bool _driverNeedsAttention;
    private string _driverActionText = "Install Leyline Virtual Audio Driver (UAC)";
    private string _driverStatusText = "Checking Leyline Virtual Audio Driver...";
    private int _selectedTabIndex;
    private int _listeningMixIndex;
    private bool _isSettingsOpen;
    private int _selectedSettingsTabIndex = 2;

    // Create Channel Popover State (Screenshot 3)
    private bool _isCreateChannelPopoverOpen;
    private string _createChannelSearchQuery = string.Empty;

    // Channel Configuration Modal State (Screenshot 2)
    private bool _isChannelConfigModalOpen;
    private ChannelStripModel? _selectedConfigChannel;
    private int _selectedChannelConfigTabIndex; // 0: Apps, 1: Audio effects
    private bool _isAddAppPickerOpen;

    // Compact Mini-Mixer Mode (Screenshot 5)
    private bool _isCompactMode;

    public AudioEngine Engine { get; }
    public AppConfig Config { get; private set; }
    public GlobalHotkeyService Hotkeys { get; }
    public Func<bool>? IsAnyPopupOpen { get; set; }
    private bool _isPollingSessions;

    public ObservableCollection<OutputMixBusModel> MixBuses { get; } = new();
    public ObservableCollection<ChannelStripModel> Channels { get; } = new();
    public ObservableCollection<SoundPadModel> SoundPads { get; } = new();

    // Strictly separated device lists (with "None / Unselected" at index 0)
    public ObservableCollection<AudioDeviceInfo> AllDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> CaptureDevicesWithNone { get; } = new();
    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> RenderDevicesWithNone { get; } = new();
    public ObservableCollection<AudioDeviceInfo> VirtualDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> VirtualDevicesWithNone { get; } = new();

    public ObservableCollection<AudioSessionItem> ActiveAppSessions { get; } = new();

    // Filtered collections for "+ Create channel" Popover (Screenshot 3)
    public ObservableCollection<AudioDeviceInfo> FilteredPopoverInputDevices { get; } = new();
    public ObservableCollection<AudioSessionItem> FilteredPopoverActiveApps { get; } = new();
    public ObservableCollection<string> FilteredPopoverSuggestedApps { get; } = new();

    // VST Collections for FX Rack & Settings -> Manage plugins
    public ObservableCollection<VstPluginInfo> DiscoveredVstPlugins { get; } = new();
    public ObservableCollection<VstPluginInfo> ScannedEffectPlugins { get; } = new();
    public ObservableCollection<VstPluginInfo> BrokenPlugins { get; } = new();
    public ObservableCollection<VstPluginInfo> VirtualInstrumentPlugins { get; } = new();

    public bool IsLightMode
    {
        get => Config.IsLightMode;
        set
        {
            if (Config.IsLightMode != value)
            {
                Config.IsLightMode = value;
                ThemeService.ApplyTheme(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeButtonText));
                OnPropertyChanged(nameof(ThemeModeBadgeText));
                SaveConfig();
            }
        }
    }

    public string ThemeButtonText => IsLightMode ? "🌙  Switch to Material Dark" : "☀️  Switch to Material Light";
    public string ThemeModeBadgeText => IsLightMode ? "Material 3 Light" : "Material 3 Dark";

    public int ListeningMixIndex
    {
        get => _listeningMixIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, MixBuses.Count - 1));
            if (SetProperty(ref _listeningMixIndex, clamped))
            {
                Config.ListeningMixIndex = _listeningMixIndex;
                for (int i = 0; i < MixBuses.Count; i++)
                    MixBuses[i].IsListeningTarget = (i == _listeningMixIndex);

                Engine.SetListeningMixIndex(_listeningMixIndex);
                SaveConfig();
            }
        }
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public int SelectedSettingsTabIndex
    {
        get => _selectedSettingsTabIndex;
        set => SetProperty(ref _selectedSettingsTabIndex, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public bool IsCreateChannelPopoverOpen
    {
        get => _isCreateChannelPopoverOpen;
        set
        {
            if (SetProperty(ref _isCreateChannelPopoverOpen, value) && value)
            {
                CreateChannelSearchQuery = string.Empty;
                RefreshActiveAppSessions();
                UpdatePopoverFilteredItems();
            }
        }
    }

    public string CreateChannelSearchQuery
    {
        get => _createChannelSearchQuery;
        set
        {
            if (SetProperty(ref _createChannelSearchQuery, value))
                UpdatePopoverFilteredItems();
        }
    }

    public bool IsChannelConfigModalOpen
    {
        get => _isChannelConfigModalOpen;
        set
        {
            if (SetProperty(ref _isChannelConfigModalOpen, value) && !value)
                IsAddAppPickerOpen = false;
        }
    }

    public ChannelStripModel? SelectedConfigChannel
    {
        get => _selectedConfigChannel;
        set => SetProperty(ref _selectedConfigChannel, value);
    }

    public int SelectedChannelConfigTabIndex
    {
        get => _selectedChannelConfigTabIndex;
        set => SetProperty(ref _selectedChannelConfigTabIndex, value);
    }

    public bool IsAddAppPickerOpen
    {
        get => _isAddAppPickerOpen;
        set => SetProperty(ref _isAddAppPickerOpen, value);
    }

    public bool IsCompactMode
    {
        get => _isCompactMode;
        set => SetProperty(ref _isCompactMode, value);
    }

    public AudioDeviceInfo? SelectedMonitorDevice
    {
        get => _selectedMonitorDevice;
        set
        {
            if (SetProperty(ref _selectedMonitorDevice, value))
            {
                string id = value?.Id ?? string.Empty;
                Config.MonitorOutputDeviceId = id;
                Engine.SetMonitorOutputDevice(id);
                SaveConfig();
            }
        }
    }

    public AudioDeviceInfo? SelectedStreamDevice
    {
        get => _selectedStreamDevice;
        set
        {
            if (SetProperty(ref _selectedStreamDevice, value))
            {
                string id = value?.Id ?? string.Empty;
                Config.StreamOutputDeviceId = id;
                Engine.SetStreamOutputDevice(id);
                SaveConfig();
            }
        }
    }

    public float MonitorMasterVolume
    {
        get => Engine.MonitorMasterVolume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            Engine.MonitorMasterVolume = clamped;
            Config.MonitorMasterVolume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MonitorMasterPercent));
        }
    }

    public int MonitorMasterPercent => (int)Math.Round(MonitorMasterVolume * 100f);

    public float StreamMasterVolume
    {
        get => Engine.StreamMasterVolume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            Engine.StreamMasterVolume = clamped;
            Config.StreamMasterVolume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StreamMasterPercent));
        }
    }

    public int StreamMasterPercent => (int)Math.Round(StreamMasterVolume * 100f);

    public bool IsMonitorMasterMuted
    {
        get => Engine.IsMonitorMasterMuted;
        set
        {
            Engine.IsMonitorMasterMuted = value;
            Config.IsMonitorMasterMuted = value;
            OnPropertyChanged();
        }
    }

    public bool IsStreamMasterMuted
    {
        get => Engine.IsStreamMasterMuted;
        set
        {
            Engine.IsStreamMasterMuted = value;
            Config.IsStreamMasterMuted = value;
            OnPropertyChanged();
        }
    }

    public float SoundboardMasterVolume
    {
        get => Engine.Soundboard.MasterVolume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1.5f);
            Engine.Soundboard.MasterVolume = clamped;
            Config.SoundboardMasterVolume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoundboardMasterPercent));
        }
    }

    public int SoundboardMasterPercent => (int)Math.Round(SoundboardMasterVolume * 100f);

    public float MonitorMasterPeakL
    {
        get => _monitorMasterPeakL;
        set
        {
            if (SetProperty(ref _monitorMasterPeakL, value))
                OnPropertyChanged(nameof(MonitorMasterPeakLDouble));
        }
    }

    public double MonitorMasterPeakLDouble => _monitorMasterPeakL;

    public float MonitorMasterPeakR
    {
        get => _monitorMasterPeakR;
        set
        {
            if (SetProperty(ref _monitorMasterPeakR, value))
                OnPropertyChanged(nameof(MonitorMasterPeakRDouble));
        }
    }

    public double MonitorMasterPeakRDouble => _monitorMasterPeakR;

    public float StreamMasterPeakL
    {
        get => _streamMasterPeakL;
        set
        {
            if (SetProperty(ref _streamMasterPeakL, value))
                OnPropertyChanged(nameof(StreamMasterPeakLDouble));
        }
    }

    public double StreamMasterPeakLDouble => _streamMasterPeakL;

    public float StreamMasterPeakR
    {
        get => _streamMasterPeakR;
        set
        {
            if (SetProperty(ref _streamMasterPeakR, value))
                OnPropertyChanged(nameof(StreamMasterPeakRDouble));
        }
    }

    public double StreamMasterPeakRDouble => _streamMasterPeakR;

    public string StatusBannerText
    {
        get => _statusBannerText;
        set => SetProperty(ref _statusBannerText, value);
    }

    public bool IsRecordingStream
    {
        get => _isRecordingStream;
        set => SetProperty(ref _isRecordingStream, value);
    }

    public bool IsDriverBranded
    {
        get => _isDriverBranded;
        set => SetProperty(ref _isDriverBranded, value);
    }

    public string DriverStatusText
    {
        get => _driverStatusText;
        set => SetProperty(ref _driverStatusText, value);
    }

    /// <summary>True when the driver is missing, outdated or not running: the startup prompt is shown.</summary>
    public bool DriverNeedsAttention
    {
        get => _driverNeedsAttention;
        set => SetProperty(ref _driverNeedsAttention, value);
    }

    public string DriverActionText
    {
        get => _driverActionText;
        set => SetProperty(ref _driverActionText, value);
    }

    public bool MinimizeToTrayOnClose
    {
        get => Config.MinimizeToTrayOnClose;
        set
        {
            Config.MinimizeToTrayOnClose = value;
            OnPropertyChanged();
            SaveConfig();
        }
    }

    public bool ShowNotifications
    {
        get => Config.ShowNotifications;
        set
        {
            Config.ShowNotifications = value;
            OnPropertyChanged();
            SaveConfig();
        }
    }

    public bool StartWithWindows
    {
        get => Config.StartWithWindows;
        set
        {
            Config.StartWithWindows = value;
            StartupService.SetStartupEnabled(value, Config.StartMinimized);
            OnPropertyChanged();
            SaveConfig();
        }
    }

    // Commands
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ToggleCompactModeCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CloseSettingsCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand RefreshAppsCommand { get; }
    public RelayCommand StopAllSoundsCommand { get; }
    public RelayCommand TriggerPadCommand { get; }
    public RelayCommand ToggleMonitorMuteCommand { get; }
    public RelayCommand ToggleStreamMuteCommand { get; }
    public RelayCommand ToggleChannelInputMuteCommand { get; }
    public RelayCommand ToggleMixSendMuteCommand { get; }
    public RelayCommand ToggleMixSendEnabledCommand { get; }
    public RelayCommand RemoveChannelCommand { get; }
    public RelayCommand RemoveSelectedChannelCommand { get; }
    public RelayCommand CloseChannelConfigModalCommand { get; }
    public RelayCommand RemoveAssignedAppFromChannelCommand { get; }
    public RelayCommand ToggleRecordStreamCommand { get; }
    public RelayCommand RescanVstCommand { get; }
    public RelayCommand ToggleAllowBlockedPluginCommand { get; }
    public RelayCommand UnblockAllPluginsCommand { get; }
    public RelayCommand ProvisionDriverCommand { get; }
    public RelayCommand RestoreDriverCommand { get; }
    public RelayCommand CreateAppCaptureChannelCommand { get; }
    public RelayCommand ResetAppRouteCommand { get; }
    public RelayCommand RestoreDefaultChannelsCommand { get; }
    public RelayCommand OpenWindowsSoundSettingsCommand { get; }

    // Create Channel Popover Commands (Screenshot 3)
    public RelayCommand ToggleCreateChannelPopoverCommand { get; }
    public RelayCommand CreateChannelFromInputDeviceCommand { get; }
    public RelayCommand CreateSoundboardChannelCommand { get; }
    public RelayCommand CreateChannelFromActiveAppCommand { get; }
    public RelayCommand CreateChannelFromSuggestedAppCommand { get; }
    public RelayCommand CreateEmptyChannelCommand { get; }
    public RelayCommand AddNewMixBusCommand { get; }

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Config = ConfigService.LoadOrCreateDefault();
        Config.StartWithWindows = StartupService.IsStartupEnabled();
        _listeningMixIndex = Config.ListeningMixIndex;

        ThemeService.ApplyTheme(Config.IsLightMode);

        Engine = new AudioEngine
        {
            MonitorMasterVolume = Config.MonitorMasterVolume,
            StreamMasterVolume = Config.StreamMasterVolume,
            IsMonitorMasterMuted = Config.IsMonitorMasterMuted,
            IsStreamMasterMuted = Config.IsStreamMasterMuted,
            MonitorMasterDspModel = Config.MonitorMasterDsp,
            StreamMasterDspModel = Config.StreamMasterDsp
        };
        Engine.Soundboard.MasterVolume = Config.SoundboardMasterVolume;
        Engine.SetListeningMixIndex(_listeningMixIndex);

        Hotkeys = new GlobalHotkeyService();
        Hotkeys.PadHotkeyPressed += pad =>
        {
            if (_dispatcherQueue != null)
                _dispatcherQueue.TryEnqueue(() => Engine.Soundboard.TriggerPad(pad));
            else
                Engine.Soundboard.TriggerPad(pad);
        };
        Hotkeys.StopAllHotkeyPressed += () =>
        {
            if (_dispatcherQueue != null)
                _dispatcherQueue.TryEnqueue(() => Engine.Soundboard.StopAll());
            else
                Engine.Soundboard.StopAll();
        };

        ToggleThemeCommand = new RelayCommand(() => IsLightMode = !IsLightMode);
        ToggleCompactModeCommand = new RelayCommand(() => IsCompactMode = !IsCompactMode);
        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);

        RefreshDevicesCommand = new RelayCommand(RefreshAudioDevices);
        RefreshAppsCommand = new RelayCommand(RefreshActiveAppSessions);
        StopAllSoundsCommand = new RelayCommand(() => Engine.Soundboard.StopAll());
        TriggerPadCommand = new RelayCommand(p =>
        {
            if (p is SoundPadModel pad)
                Engine.Soundboard.TriggerPad(pad);
        });

        ToggleMonitorMuteCommand = new RelayCommand(() => IsMonitorMasterMuted = !IsMonitorMasterMuted);
        ToggleStreamMuteCommand = new RelayCommand(() => IsStreamMasterMuted = !IsStreamMasterMuted);

        ToggleChannelInputMuteCommand = new RelayCommand(p =>
        {
            if (p is ChannelStripModel ch)
            {
                ch.IsInputMuted = !ch.IsInputMuted;
                SaveConfig();
            }
        });

        ToggleMixSendMuteCommand = new RelayCommand(p =>
        {
            if (p is MixSendState send)
            {
                send.IsMuted = !send.IsMuted;
                SaveConfig();
            }
        });

        ToggleMixSendEnabledCommand = new RelayCommand(p =>
        {
            if (p is MixSendState send)
            {
                send.IsEnabled = !send.IsEnabled;
                SaveConfig();
            }
        });

        RemoveChannelCommand = new RelayCommand(p =>
        {
            if (p is ChannelStripModel ch)
            {
                ResetChannelAppsToWindowsDefault(ch);
                Engine.RemoveChannel(ch.Id);
                Channels.Remove(ch);
                Config.Channels.Remove(ch);
                ReleaseCableIfUnused(ch);
                if (SelectedConfigChannel == ch)
                    IsChannelConfigModalOpen = false;
                SaveConfig();
            }
        });

        RemoveSelectedChannelCommand = new RelayCommand(() =>
        {
            if (SelectedConfigChannel != null)
            {
                var ch = SelectedConfigChannel;
                IsChannelConfigModalOpen = false;
                SelectedConfigChannel = null;
                ResetChannelAppsToWindowsDefault(ch);
                Engine.RemoveChannel(ch.Id);
                Channels.Remove(ch);
                Config.Channels.Remove(ch);
                ReleaseCableIfUnused(ch);
                SaveConfig();
                StatusBannerText = $"Removed channel '{ch.Name}'.";
            }
        });

        CloseChannelConfigModalCommand = new RelayCommand(() => IsChannelConfigModalOpen = false);

        RemoveAssignedAppFromChannelCommand = new RelayCommand(p =>
        {
            if (p is AssignedAppModel app && SelectedConfigChannel != null)
            {
                if (app.ActiveProcessId != 0)
                    PolicyConfigClient.SetApplicationOutputEndpoint(app.ActiveProcessId, null);
                Config.SavedAppRoutes.Remove(app.ProcessName);
                SelectedConfigChannel.AssignedApps.Remove(app);
                SelectedConfigChannel.NotifyAppsChanged();
                Engine.AddOrRestartChannel(SelectedConfigChannel);
                SaveConfig();
            }
        });

        ToggleRecordStreamCommand = new RelayCommand(() =>
        {
            string path = Engine.ToggleStreamRecording();
            IsRecordingStream = Engine.IsRecordingStream;
            StatusBannerText = IsRecordingStream
                ? $"Recording Stream Mix to: {path}"
                : $"Saved Stream Mix recording: {path}";
        });

        RescanVstCommand = new RelayCommand(() => ScanVstPlugins(forceDeepProbe: true));

        ToggleAllowBlockedPluginCommand = new RelayCommand(p =>
        {
            if (p is VstPluginInfo plugin)
            {
                plugin.IsUserAllowedOverride = !plugin.IsUserAllowedOverride;
                if (plugin.IsUserAllowedOverride)
                {
                    if (!Config.AllowedBlockedVstPaths.Contains(plugin.FilePath, StringComparer.OrdinalIgnoreCase))
                        Config.AllowedBlockedVstPaths.Add(plugin.FilePath);
                }
                else
                {
                    Config.AllowedBlockedVstPaths.RemoveAll(x => x.Equals(plugin.FilePath, StringComparison.OrdinalIgnoreCase));
                }
                RebuildUsableVstList();
                SaveConfig();
                StatusBannerText = plugin.IsUserAllowedOverride
                    ? $"Allowed '{plugin.Name}' for use in channel FX chains."
                    : $"Blocked '{plugin.Name}'.";
            }
        });

        UnblockAllPluginsCommand = new RelayCommand(() =>
        {
            foreach (var p in BrokenPlugins.Concat(VirtualInstrumentPlugins))
            {
                p.IsUserAllowedOverride = true;
                if (!Config.AllowedBlockedVstPaths.Contains(p.FilePath, StringComparer.OrdinalIgnoreCase))
                    Config.AllowedBlockedVstPaths.Add(p.FilePath);
            }
            RebuildUsableVstList();
            SaveConfig();
            StatusBannerText = "Unblocked all broken & virtual instrument plugins for FX chain use.";
        });

        ProvisionDriverCommand = new RelayCommand(() => ExecuteDriverAction(true));
        RestoreDriverCommand = new RelayCommand(() => ExecuteDriverAction(false));

        CreateAppCaptureChannelCommand = new RelayCommand(p =>
        {
            if (p is AudioSessionItem app)
                AddDirectAppChannel(app);
        });

        ResetAppRouteCommand = new RelayCommand(p =>
        {
            if (p is AudioSessionItem app)
            {
                if (PolicyConfigClient.SetApplicationOutputEndpoint(app.ProcessId, null))
                {
                    StatusBannerText = $"Reset {app.DisplayName} to Windows Default Output";
                    RefreshActiveAppSessions();
                }
            }
        });

        RestoreDefaultChannelsCommand = new RelayCommand(() =>
        {
            var fresh = ConfigService.CreateDefaultConfig();
            foreach (var old in Channels.ToList())
                Engine.RemoveChannel(old.Id);

            Config.Channels = fresh.Channels;
            InitializeChannelsAndPads();
            SaveConfig();
            StatusBannerText = "Restored default System channel configuration.";
        });

        OpenWindowsSoundSettingsCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
            }
            catch { }
        });

        // Create Channel Popover Commands (Screenshot 3)
        ToggleCreateChannelPopoverCommand = new RelayCommand(() => IsCreateChannelPopoverOpen = !IsCreateChannelPopoverOpen);

        CreateChannelFromInputDeviceCommand = new RelayCommand(p =>
        {
            if (p is AudioDeviceInfo dev && dev.Kind == AudioEndpointKind.CaptureInput)
            {
                var strip = new ChannelStripModel
                {
                    Name = dev.Name,
                    Icon = "🎙️",
                    AccentHex = "#448AFF",
                    SourceType = ChannelSourceType.MicrophoneInput,
                    DeviceId = dev.Id,
                    DeviceName = dev.Name,
                    InputVolume = 0.90f,
                    // Muted in Personal Mix by default so user doesn't hear mic bleed
                    PersonalMix = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = true },
                    StreamMix = new MixSendState { IsEnabled = true, Volume = 0.90f, IsMuted = false },
                    DiscordMix = new MixSendState { IsEnabled = false, Volume = 0.85f, IsMuted = false },
                    MicRecMix = new MixSendState { IsEnabled = true, Volume = 0.90f, IsMuted = false }
                };
                strip.Dsp.GateEnabled = true;
                AddChannel(strip);
                IsCreateChannelPopoverOpen = false;
                StatusBannerText = $"Added Input Device channel: {dev.Name}";
            }
        });

        CreateSoundboardChannelCommand = new RelayCommand(() =>
        {
            IsCreateChannelPopoverOpen = false;
            var existing = Channels.FirstOrDefault(c => c.SourceType == ChannelSourceType.SoundboardChannel);
            if (existing != null)
            {
                OpenChannelConfig(existing, 0);
                return;
            }
            var sb = new ChannelStripModel
            {
                Name = "Soundboard",
                Icon = "〰️",
                AccentHex = "#FF7043",
                SourceType = ChannelSourceType.SoundboardChannel,
                DeviceName = "Leyline Built-in Sampler",
                InputVolume = 0.90f
            };
            AddChannel(sb);
        });

        CreateChannelFromActiveAppCommand = new RelayCommand(p =>
        {
            if (p is AudioSessionItem app)
            {
                IsCreateChannelPopoverOpen = false;
                AddDirectAppChannel(app);
            }
        });

        CreateChannelFromSuggestedAppCommand = new RelayCommand(p =>
        {
            if (p is string appName && !string.IsNullOrWhiteSpace(appName))
            {
                IsCreateChannelPopoverOpen = false;
                var strip = new ChannelStripModel
                {
                    Name = appName,
                    Icon = "🎮",
                    AccentHex = "#8D3B3B",
                    SourceType = ChannelSourceType.AppGroupChannel,
                    DeviceId = string.Empty,
                    DeviceName = "Direct App Capture",
                    InputVolume = 0.90f
                };
                strip.AssignedApps.Add(new AssignedAppModel
                {
                    AppName = appName,
                    ProcessName = appName,
                    IconEmoji = "🎮"
                });
                strip.NotifyAppsChanged();
                AddChannel(strip);
                OpenChannelConfig(strip, 0);
            }
        });

        CreateEmptyChannelCommand = new RelayCommand(() =>
        {
            IsCreateChannelPopoverOpen = false;
            var strip = new ChannelStripModel
            {
                Name = $"Custom Channel {Channels.Count + 1}",
                Icon = "🎚️",
                AccentHex = "#7C4DFF",
                SourceType = ChannelSourceType.AppGroupChannel,
                DeviceId = string.Empty,
                DeviceName = "None (Unselected)",
                InputVolume = 0.90f
            };
            AddChannel(strip);
            OpenChannelConfig(strip, 0);
        });

        AddNewMixBusCommand = new RelayCommand(() =>
        {
            var newBus = new OutputMixBusModel
            {
                Name = $"Custom Mix {MixBuses.Count + 1}",
                Icon = "🔊"
            };
            MixBuses.Add(newBus);
            Config.MixBuses.Add(newBus);
            SaveConfig();
            StatusBannerText = $"Created output mix bus: {newBus.Name}";
        });

        RefreshAudioDevices();
        ConnectMixOutputCables();
        InitializeChannelsAndPads();

        if (_dispatcherQueue != null)
        {
            _autoSaver = new ConfigAutoSaver(_dispatcherQueue, SaveConfig);
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != null && AutoSavedViewModelProperties.Contains(e.PropertyName))
                    _autoSaver.ScheduleSave();
            };
        }
        SaveConfig();   // record the channels' cable bindings; also starts the auto-save watch
        RefreshDriverStatus();
        RefreshActiveAppSessions();
        ScanVstPlugins(forceDeepProbe: false);

        CompositionTarget.Rendering += OnRenderingMeters;

        _sessionPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4.0)
        };
        _sessionPollTimer.Tick += (_, _) =>
        {
            if (IsAnyPopupOpen?.Invoke() == true)
                return;
            RefreshActiveAppSessionsBackground();
        };
        _sessionPollTimer.Start();
    }

    public void OpenChannelConfig(ChannelStripModel channel, int tabIndex = 0)
    {
        SelectedConfigChannel = channel;
        SelectedChannelConfigTabIndex = tabIndex;
        RefreshChannelAppAvailabilities(channel);
        IsChannelConfigModalOpen = true;
    }

    public void MoveChannel(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Channels.Count || newIndex < 0 || newIndex >= Channels.Count || oldIndex == newIndex)
            return;

        Channels.Move(oldIndex, newIndex);
        var item = Config.Channels[oldIndex];
        Config.Channels.RemoveAt(oldIndex);
        Config.Channels.Insert(newIndex, item);
        SaveConfig();
    }

    public void RefreshAudioDevices()
    {
        var devices = AudioEngine.EnumerateDevices();
        AllDevices.Clear();
        CaptureDevices.Clear();
        CaptureDevicesWithNone.Clear();
        RenderDevices.Clear();
        RenderDevicesWithNone.Clear();
        VirtualDevices.Clear();
        VirtualDevicesWithNone.Clear();

        var noneCapture = AudioDeviceInfo.CreateNone("None (Unselected)");
        var noneRender = AudioDeviceInfo.CreateNone("None (Monitoring Off)");
        var noneVirtual = AudioDeviceInfo.CreateNone("None (Direct App Capture Only)");

        CaptureDevicesWithNone.Add(noneCapture);
        RenderDevicesWithNone.Add(noneRender);
        VirtualDevicesWithNone.Add(noneVirtual);

        foreach (var d in devices)
        {
            AllDevices.Add(d);
            if (d.Kind == AudioEndpointKind.CaptureInput)
            {
                CaptureDevices.Add(d);
                CaptureDevicesWithNone.Add(d);
            }
            else
            {
                RenderDevices.Add(d);
                RenderDevicesWithNone.Add(d);
            }

            if (d.IsVirtual && d.Kind != AudioEndpointKind.CaptureInput)
            {
                VirtualDevices.Add(d);
                VirtualDevicesWithNone.Add(d);
            }
        }

        // Do NOT auto-assign a default monitoring device if Config.MonitorOutputDeviceId is empty ("None")
        _selectedMonitorDevice = string.IsNullOrWhiteSpace(Config.MonitorOutputDeviceId)
            ? noneRender
            : (RenderDevicesWithNone.FirstOrDefault(d => d.Id == Config.MonitorOutputDeviceId) ?? noneRender);
        OnPropertyChanged(nameof(SelectedMonitorDevice));

        _selectedStreamDevice = string.IsNullOrWhiteSpace(Config.StreamOutputDeviceId)
            ? noneRender
            : (RenderDevicesWithNone.FirstOrDefault(d => d.Id == Config.StreamOutputDeviceId) ?? noneRender);
        OnPropertyChanged(nameof(SelectedStreamDevice));

        Engine.SetMonitorOutputDevice(_selectedMonitorDevice?.Id);
        Engine.SetStreamOutputDevice(_selectedStreamDevice?.Id);
        UpdatePopoverFilteredItems();
    }

    private void UpdatePopoverFilteredItems()
    {
        string q = (_createChannelSearchQuery ?? string.Empty).Trim();

        FilteredPopoverInputDevices.Clear();
        foreach (var d in CaptureDevices)
        {
            if (string.IsNullOrEmpty(q) || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredPopoverInputDevices.Add(d);
        }

        FilteredPopoverActiveApps.Clear();
        foreach (var a in ActiveAppSessions)
        {
            if (string.IsNullOrEmpty(q) ||
                a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredPopoverActiveApps.Add(a);
            }
        }

        FilteredPopoverSuggestedApps.Clear();
        foreach (var s in Config.KnownSuggestedApps)
        {
            if (string.IsNullOrEmpty(q) || s.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredPopoverSuggestedApps.Add(s);
        }
    }

    private void InitializeChannelsAndPads()
    {
        MixBuses.Clear();
        if (Config.MixBuses.Count == 0)
            Config.MixBuses = ConfigService.CreateDefaultMixBuses();
        for (int i = 0; i < Config.MixBuses.Count; i++)
        {
            Config.MixBuses[i].IsListeningTarget = (i == _listeningMixIndex);
            MixBuses.Add(Config.MixBuses[i]);
        }

        Channels.Clear();
        foreach (var ch in Config.Channels)
        {
            EnsureChannelBoundToCable(ch);
            ch.NotifyAppsChanged();
            ch.NotifyFxChanged();
            Channels.Add(ch);
            Engine.AddOrRestartChannel(ch);
        }

        SoundPads.Clear();
        foreach (var pad in Config.SoundPads)
        {
            SoundPads.Add(pad);
        }
    }

    private void ResetChannelAppsToWindowsDefault(ChannelStripModel channel)
    {
        if (channel.TargetProcessId != 0)
            PolicyConfigClient.SetApplicationOutputEndpoint(channel.TargetProcessId, null);

        foreach (var app in channel.AssignedApps)
        {
            if (app.ActiveProcessId != 0)
                PolicyConfigClient.SetApplicationOutputEndpoint(app.ActiveProcessId, null);
            Config.SavedAppRoutes.Remove(app.ProcessName);
        }
    }

    /// <summary>
    /// Binds a channel to its Leyline cable: connects the cable and points DeviceId at the cable's playback
    /// endpoint, where the channel's apps are routed. Channels without a cable (or without the driver) fall back
    /// to per-app capture.
    /// </summary>
    private void EnsureChannelBoundToCable(ChannelStripModel channel)
    {
        if (channel.IsHardwareInput || channel.SourceType == ChannelSourceType.SoundboardChannel)
            return;

        // Channels from older configs (and app channels) get a cable from their name.
        if (string.IsNullOrWhiteSpace(channel.CableId))
            channel.CableId = GuessCableForChannel(channel.Name) ?? string.Empty;

        var cable = LeylineCableService.Find(channel.CableId);
        if (cable == null || cable.IsMixOutput)
        {
            channel.CableId = string.Empty;
            // Drop a device left over from Leyline's former virtual devices.
            if (!string.IsNullOrWhiteSpace(channel.DeviceId) && !RenderDevices.Any(d => d.Id == channel.DeviceId))
            {
                channel.DeviceId = string.Empty;
                channel.DeviceName = "None (Direct App Capture)";
            }
            return;
        }

        var (renderId, _) = LeylineCableService.ConnectAndResolve(cable.Id);
        if (renderId != null)
        {
            channel.DeviceId = renderId;
            channel.DeviceName = $"{cable.DisplayName} ({LeylineDriverService.DeviceName})";
        }
        else
        {
            channel.DeviceId = string.Empty;
            channel.DeviceName = "Leyline driver not running (Direct App Capture)";
        }
    }

    private static string? GuessCableForChannel(string name)
    {
        bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);

        if (Has("System")) return "System";
        if (Has("Game")) return "Game";
        if (Has("Browser") || Has("Chrome") || Has("Firefox") || Has("Edge")) return "Browser";
        if (Has("Music") || Has("Spotify")) return "Music";
        if (Has("Voice") || Has("Discord")) return "VoiceChat";
        return null;
    }

    /// <summary>Disconnects a removed channel's cable when no other channel uses it.</summary>
    private void ReleaseCableIfUnused(ChannelStripModel removed)
    {
        if (string.IsNullOrWhiteSpace(removed.CableId))
            return;

        if (!Channels.Any(c => c != removed && c.CableId.Equals(removed.CableId, StringComparison.OrdinalIgnoreCase)))
            LeylineCableService.SetConnected(removed.CableId, false);
    }

    /// <summary>
    /// Connects the mix output cables: the Stream Mix plays into "Stream Mix" (for OBS) unless another Stream
    /// output is chosen, and the Mic Rec mix plays into "Microphone" (for Discord and other voice apps).
    /// </summary>
    private void ConnectMixOutputCables()
    {
        var (streamRender, _) = LeylineCableService.ConnectAndResolve(LeylineCableService.StreamMixCableId);
        Engine.SetStreamMixCableDevice(streamRender);

        var (micRender, _) = LeylineCableService.ConnectAndResolve(LeylineCableService.MicrophoneCableId);
        Engine.SetMicRecOutputDevice(micRender);
    }

    public void ChangeChannelDevice(ChannelStripModel channel, string? newDeviceId)
    {
        if (string.IsNullOrWhiteSpace(newDeviceId))
        {
            channel.DeviceId = string.Empty;
            channel.DeviceName = "None (Unselected)";
            if (channel.IsHardwareInput && channel.Name.StartsWith("Microphone", StringComparison.OrdinalIgnoreCase))
                channel.Name = "Microphone";
            Engine.AddOrRestartChannel(channel);
            SaveConfig();
            StatusBannerText = $"Unselected input device on '{channel.Name}'.";
            return;
        }

        var dev = AllDevices.FirstOrDefault(d => d.Id == newDeviceId);
        if (dev == null) return;

        // Enforce strict eCapture filtering on MicrophoneInput channels
        if (channel.SourceType == ChannelSourceType.MicrophoneInput && dev.Kind != AudioEndpointKind.CaptureInput)
        {
            StatusBannerText = "Microphone channels can only use Input Capture devices.";
            return;
        }

        channel.DeviceId = dev.Id;
        channel.DeviceName = dev.Name;
        if (channel.IsHardwareInput && channel.Name.StartsWith("Microphone", StringComparison.OrdinalIgnoreCase))
        {
            channel.Name = dev.Name;
        }

        Engine.AddOrRestartChannel(channel);
        UpdateChannelRoutedAppCounts();
        SaveConfig();
        StatusBannerText = $"Selected input device: {dev.Name}";
    }

    public void AddAppToChannel(ChannelStripModel channel, string appName, string processName, uint processId = 0)
    {
        string cleanProc = processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (channel.AssignedApps.Any(a => a.ProcessName.Equals(cleanProc, StringComparison.OrdinalIgnoreCase)))
            return;

        EnsureChannelBoundToCable(channel);

        var item = new AssignedAppModel
        {
            AppName = appName,
            ProcessName = cleanProc,
            IsAvailable = processId != 0,
            ActiveProcessId = processId
        };
        channel.AssignedApps.Add(item);
        channel.NotifyAppsChanged();

        if (!Config.KnownSuggestedApps.Contains(cleanProc, StringComparer.OrdinalIgnoreCase))
            Config.KnownSuggestedApps.Add(cleanProc);

        // Route the chosen application directly to the channel's Leyline Virtual Audio playback device
        if (!string.IsNullOrWhiteSpace(channel.DeviceId))
        {
            Config.SavedAppRoutes[cleanProc] = channel.DeviceId;
            if (processId != 0)
            {
                PolicyConfigClient.SetApplicationOutputEndpoint(processId, channel.DeviceId);
            }
        }

        RefreshChannelAppAvailabilities(channel);
        Engine.AddOrRestartChannel(channel);
        SaveConfig();
        StatusBannerText = !string.IsNullOrWhiteSpace(channel.DeviceId)
            ? $"Routed '{appName}' → {channel.DeviceName} ({channel.Name})"
            : $"Added '{appName}' to channel '{channel.Name}'.";
    }

    private void RefreshChannelAppAvailabilities(ChannelStripModel channel)
    {
        foreach (var app in channel.AssignedApps)
        {
            var running = ActiveAppSessions.FirstOrDefault(s =>
                s.ProcessName.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(app.AppName, StringComparison.OrdinalIgnoreCase));

            if (running != null)
            {
                bool wasOffline = !app.IsAvailable || app.ActiveProcessId != running.ProcessId;
                app.IsAvailable = true;
                app.ActiveProcessId = running.ProcessId;

                // Automatically route newly launched or unassigned running apps to the channel's Leyline Virtual Audio device
                if (!string.IsNullOrWhiteSpace(channel.DeviceId) &&
                    (wasOffline || !string.Equals(running.CurrentEndpointId, channel.DeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    if (PolicyConfigClient.SetApplicationOutputEndpoint(running.ProcessId, channel.DeviceId))
                    {
                        running.CurrentEndpointId = channel.DeviceId;
                        running.CurrentEndpointName = channel.DeviceName;
                    }
                }
            }
            else
            {
                app.IsAvailable = false;
                app.ActiveProcessId = 0;
            }
        }
        channel.NotifyAppsChanged();
    }

    public void AddChannel(ChannelStripModel model)
    {
        EnsureChannelBoundToCable(model);
        Config.Channels.Add(model);
        Channels.Add(model);
        Engine.AddOrRestartChannel(model);
        SaveConfig();
    }

    public void CreatePresetChannel(string presetKey)
    {
        var strip = ConfigService.CreatePresetChannel(presetKey, AllDevices);
        AddChannel(strip);
        UpdateChannelRoutedAppCounts();
        StatusBannerText = $"Added '{strip.Name}' channel ({strip.DeviceName}).";
    }

    public void AddDirectAppChannel(AudioSessionItem app)
    {
        var existing = Channels.FirstOrDefault(c =>
            c.AssignedApps.Any(a => a.ProcessName.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase)) ||
            (c.SourceType == ChannelSourceType.ProcessCapture && c.TargetProcessId == app.ProcessId));

        if (existing != null)
        {
            existing.TargetProcessId = app.ProcessId;
            EnsureChannelBoundToCable(existing);
            if (!string.IsNullOrWhiteSpace(existing.DeviceId))
                PolicyConfigClient.SetApplicationOutputEndpoint(app.ProcessId, existing.DeviceId);
            Engine.AddOrRestartChannel(existing);
            OpenChannelConfig(existing, 0);
            return;
        }

        var strip = new ChannelStripModel
        {
            Name = app.DisplayName,
            Icon = "📦",
            AccentHex = "#42A5F5",
            SourceType = ChannelSourceType.AppGroupChannel,
            TargetProcessId = app.ProcessId,
            TargetProcessName = app.ProcessName,
            DeviceId = string.Empty,
            DeviceName = $"App: {app.ProcessName}",
            PersonalMix = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = false },
            StreamMix = new MixSendState { IsEnabled = true, Volume = 0.90f, IsMuted = false }
        };
        EnsureChannelBoundToCable(strip);
        if (string.IsNullOrWhiteSpace(strip.DeviceId))
        {
            // Fallback: only mute PersonalMix if no Leyline Virtual Audio playback device is installed
            strip.PersonalMix.IsMuted = true;
        }
        else
        {
            PolicyConfigClient.SetApplicationOutputEndpoint(app.ProcessId, strip.DeviceId);
            Config.SavedAppRoutes[app.ProcessName] = strip.DeviceId;
        }

        strip.AssignedApps.Add(new AssignedAppModel
        {
            AppName = app.DisplayName,
            ProcessName = app.ProcessName,
            IsAvailable = true,
            ActiveProcessId = app.ProcessId
        });
        strip.NotifyAppsChanged();

        AddChannel(strip);
        SelectedTabIndex = 0;
        StatusBannerText = !string.IsNullOrWhiteSpace(strip.DeviceId)
            ? $"Created channel for '{app.DisplayName}' routed to {strip.DeviceName}."
            : $"Created channel for '{app.DisplayName}'.";
    }

    public void RouteApplicationToDevice(AudioSessionItem app, string targetDeviceId)
    {
        if (string.IsNullOrWhiteSpace(targetDeviceId))
        {
            PolicyConfigClient.SetApplicationOutputEndpoint(app.ProcessId, null);
            StatusBannerText = $"Reset '{app.DisplayName}' to Windows Default Output.";
            RefreshActiveAppSessions();
            return;
        }

        bool ok = PolicyConfigClient.SetApplicationOutputEndpoint(app.ProcessId, targetDeviceId);
        var dev = RenderDevices.FirstOrDefault(d => d.Id == targetDeviceId);
        string devName = dev?.Name ?? "Selected Endpoint";

        if (ok)
        {
            app.CurrentEndpointId = targetDeviceId;
            app.CurrentEndpointName = devName;
            Config.SavedAppRoutes[app.ProcessName] = targetDeviceId;
            UpdateChannelRoutedAppCounts();
            SaveConfig();
            StatusBannerText = $"Routed '{app.DisplayName}' → {devName}";
        }
        else
        {
            StatusBannerText = $"Assigned '{app.DisplayName}' via direct process capture.";
        }
    }

    public void RouteApplicationToChannel(AudioSessionItem app, ChannelStripModel channel)
    {
        AddAppToChannel(channel, app.DisplayName, app.ProcessName, app.ProcessId);
        if (!string.IsNullOrWhiteSpace(channel.DeviceId))
        {
            PolicyConfigClient.SetApplicationOutputEndpoint(app.ProcessId, channel.DeviceId);
        }
    }

    public void RefreshActiveAppSessions()
    {
        var sessions = PolicyConfigClient.GetActiveApplicationSessions();
        ApplyActiveAppSessionsDiff(sessions);
    }

    private void RefreshActiveAppSessionsBackground()
    {
        if (_isPollingSessions || IsAnyPopupOpen?.Invoke() == true)
            return;

        _isPollingSessions = true;
        Task.Run(() =>
        {
            try
            {
                var sessions = PolicyConfigClient.GetActiveApplicationSessions();
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    try
                    {
                        if (IsAnyPopupOpen?.Invoke() != true)
                            ApplyActiveAppSessionsDiff(sessions);
                    }
                    finally
                    {
                        _isPollingSessions = false;
                    }
                });
            }
            catch
            {
                _isPollingSessions = false;
            }
        });
    }

    private void ApplyActiveAppSessionsDiff(List<AudioSessionItem> sessions)
    {
        var incomingByProc = sessions.ToDictionary(s => s.ProcessName, StringComparer.OrdinalIgnoreCase);

        // Remove closed apps in-place without clearing the collection
        for (int i = ActiveAppSessions.Count - 1; i >= 0; i--)
        {
            if (!incomingByProc.ContainsKey(ActiveAppSessions[i].ProcessName))
            {
                ActiveAppSessions.RemoveAt(i);
            }
        }

        // Update existing apps in-place or add new foreground apps
        foreach (var s in sessions)
        {
            var existing = ActiveAppSessions.FirstOrDefault(a =>
                a.ProcessName.Equals(s.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ProcessId = s.ProcessId;
                existing.DisplayName = s.DisplayName;
                existing.CurrentEndpointId = s.CurrentEndpointId;
                existing.CurrentEndpointName = s.CurrentEndpointName;
                existing.PeakLevel = s.PeakLevel;
            }
            else
            {
                ActiveAppSessions.Add(s);
            }
        }

        UpdateChannelRoutedAppCounts();
        if (SelectedConfigChannel != null)
            RefreshChannelAppAvailabilities(SelectedConfigChannel);
    }

    private void UpdateChannelRoutedAppCounts()
    {
        foreach (var ch in Channels)
        {
            RefreshChannelAppAvailabilities(ch);

            var routedByEndpoint = ActiveAppSessions
                .Where(a => !string.IsNullOrWhiteSpace(ch.DeviceId) && a.CurrentEndpointId == ch.DeviceId)
                .Select(a => a.ProcessName)
                .ToList();

            var assignedNames = ch.AssignedApps.Select(a => a.AppName).ToList();
            var allNames = assignedNames.Union(routedByEndpoint, StringComparer.OrdinalIgnoreCase).ToList();

            ch.RoutedAppCount = allNames.Count;
            ch.RoutedAppsSummary = allNames.Count > 0
                ? string.Join(", ", allNames)
                : (ch.IsHardwareInput ? ch.DeviceName : "No apps assigned");
        }
    }

    private bool _isScanningVst;

    // Probing new plugins loads each one in a child process, so the scan runs off the UI thread.
    public async void ScanVstPlugins(bool forceDeepProbe = false)
    {
        if (_isScanningVst)
            return;
        _isScanningVst = true;

        List<VstPluginInfo> all;
        try
        {
            var directories = Config.CustomVstDirectories.ToList();
            var allowed = Config.AllowedBlockedVstPaths.ToList();
            if (forceDeepProbe)
                StatusBannerText = "Scanning plugins...";
            all = await Task.Run(() => VstScanner.ScanPlugins(directories, allowed, forceDeepProbe, NotificationService.ReportScanProgress));
        }
        finally
        {
            _isScanningVst = false;
        }

        ScannedEffectPlugins.Clear();
        BrokenPlugins.Clear();
        VirtualInstrumentPlugins.Clear();

        foreach (var p in all)
        {
            switch (p.Status)
            {
                case VstPluginStatus.ScannedOk:
                    ScannedEffectPlugins.Add(p);
                    break;
                case VstPluginStatus.BrokenCrashed:
                    BrokenPlugins.Add(p);
                    break;
                case VstPluginStatus.VirtualInstrument:
                    VirtualInstrumentPlugins.Add(p);
                    break;
            }
        }

        RebuildUsableVstList();
        NotificationService.CompleteScan(ScannedEffectPlugins.Count, VirtualInstrumentPlugins.Count, BrokenPlugins.Count);
        if (forceDeepProbe)
            StatusBannerText = $"Found {ScannedEffectPlugins.Count} effects and {VirtualInstrumentPlugins.Count} instruments.";
    }

    private void RebuildUsableVstList()
    {
        DiscoveredVstPlugins.Clear();
        foreach (var p in ScannedEffectPlugins)
            DiscoveredVstPlugins.Add(p);
        foreach (var p in VirtualInstrumentPlugins.Where(x => x.IsUserAllowedOverride))
            DiscoveredVstPlugins.Add(p);
        foreach (var p in BrokenPlugins.Where(x => x.IsUserAllowedOverride))
            DiscoveredVstPlugins.Add(p);
    }

    public void RefreshDriverStatus()
    {
        var state = LeylineDriverService.GetState();
        bool running = state != LeylineDriverState.NotInstalled && LeylineCableService.IsDriverRunning();
        bool testSigning = LeylineDriverService.IsTestSigningEnabled();
        Version? installed = LeylineDriverService.InstalledDriverVersion;
        Version? bundled = LeylineDriverService.BundledDriverVersion;

        IsDriverBranded = running;
        DriverNeedsAttention = state is LeylineDriverState.NotInstalled or LeylineDriverState.UpdateAvailable ||
                               (state == LeylineDriverState.Installed && !running);

        DriverActionText = state == LeylineDriverState.UpdateAvailable
            ? $"Update Leyline Virtual Audio Driver to {bundled} (UAC)"
            : "Install Leyline Virtual Audio Driver (UAC)";

        DriverStatusText = state switch
        {
            LeylineDriverState.NotBundled =>
                "No driver package next to Leyline.exe • Build it with driver\\LeylineVAD\\Build-Driver.ps1 and Package-Driver.ps1",
            LeylineDriverState.NotInstalled when !testSigning =>
                "Not installed • Windows test signing is off: run 'bcdedit /set testsigning on' as administrator and reboot first",
            LeylineDriverState.NotInstalled =>
                "Not installed • Install it to create Leyline's virtual audio cables",
            LeylineDriverState.UpdateAvailable =>
                $"Update available: driver {installed} installed, {bundled} included with this version of Leyline",
            _ when !running && !testSigning =>
                $"Driver {installed} installed but not running • Windows test signing is off: run 'bcdedit /set testsigning on' as administrator and reboot",
            _ when !running =>
                $"Driver {installed} installed but not running • Try reinstalling it",
            _ =>
                $"Leyline Virtual Audio {installed} active • {LeylineCableService.Cables.Count(c => LeylineCableService.IsConnected(c.Id) == true)} of {LeylineCableService.Cables.Count} cables connected"
        };
    }

    private void ExecuteDriverAction(bool install)
    {
        if (install && !LeylineDriverService.IsTestSigningEnabled())
        {
            StatusBannerText = "Leyline's driver is test-signed: run 'bcdedit /set testsigning on' as administrator, reboot, then install it.";
            RefreshDriverStatus();
            return;
        }

        StatusBannerText = install
            ? "Installing Leyline Virtual Audio Driver (accept the administrator prompt)..."
            : "Removing Leyline Virtual Audio Driver (accept the administrator prompt)...";

        if (LeylineDriverService.RunElevatedDriverAction(install, out string msg))
        {
            RefreshAudioDevices();
            if (install)
            {
                ConnectMixOutputCables();
                BindAllChannelsToCables();
            }
            RefreshAudioDevices();
            SaveConfig();
        }

        RefreshDriverStatus();
        StatusBannerText = msg;
    }

    /// <summary>Re-binds every channel to its cable (after the driver was installed or updated).</summary>
    public void BindAllChannelsToCables()
    {
        foreach (var ch in Channels)
        {
            if (ch.IsHardwareInput || ch.SourceType == ChannelSourceType.SoundboardChannel)
                continue;

            EnsureChannelBoundToCable(ch);
            Engine.AddOrRestartChannel(ch);
        }
    }

    public string ConfigFilePathText => ConfigService.GetConfigFilePath();

    public bool ExportConfigurationBackup(string destinationFilePath, out string error)
    {
        SaveConfig();
        bool ok = ConfigService.ExportBackup(Config, destinationFilePath, out error);
        if (ok)
        {
            StatusBannerText = $"Backed up configuration to: {destinationFilePath}";
        }
        return ok;
    }

    public bool ImportConfigurationBackup(string sourceFilePath, out string error)
    {
        if (!ConfigService.ImportBackup(sourceFilePath, out var imported, out error) || imported == null)
            return false;

        foreach (var old in Channels.ToList())
            Engine.RemoveChannel(old.Id);

        Config = imported;
        _listeningMixIndex = Math.Clamp(Config.ListeningMixIndex, 0, Math.Max(0, Config.MixBuses.Count - 1));
        Engine.SetListeningMixIndex(_listeningMixIndex);
        OnPropertyChanged(nameof(ListeningMixIndex));

        ThemeService.ApplyTheme(Config.IsLightMode);
        OnPropertyChanged(nameof(IsLightMode));
        OnPropertyChanged(nameof(ThemeButtonText));
        OnPropertyChanged(nameof(MinimizeToTrayOnClose));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(ShowNotifications));

        Engine.MonitorMasterVolume = Config.MonitorMasterVolume;
        Engine.StreamMasterVolume = Config.StreamMasterVolume;
        Engine.IsMonitorMasterMuted = Config.IsMonitorMasterMuted;
        Engine.IsStreamMasterMuted = Config.IsStreamMasterMuted;
        Engine.MonitorMasterDspModel = Config.MonitorMasterDsp;
        Engine.StreamMasterDspModel = Config.StreamMasterDsp;
        Engine.Soundboard.MasterVolume = Config.SoundboardMasterVolume;

        RefreshAudioDevices();
        InitializeChannelsAndPads();
        Hotkeys.RebindAll(SoundPads);
        ScanVstPlugins(forceDeepProbe: false);
        UpdateChannelRoutedAppCounts();
        SaveConfig();

        StatusBannerText = $"Imported configuration from: {sourceFilePath}";
        return true;
    }

    public void SaveConfig()
    {
        ConfigService.Save(Config);
        _autoSaver?.Watch(Config);   // channels, mixes and pads added since the last save
    }

    // Runs once per rendered frame (not while the window is hidden), so meters move at the display's frame rate
    private void OnRenderingMeters(object? sender, object e)
    {
        TimeSpan now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        double elapsedMs = (now - _lastMeterFrame).TotalMilliseconds;
        if (elapsedMs < MinMeterIntervalMs && elapsedMs >= 0)
            return;
        _lastMeterFrame = now;

        if (IsAnyPopupOpen?.Invoke() == true)
            return;

        Engine.UpdateAllMeters(Math.Clamp(elapsedMs, 0.0, 100.0));
        MonitorMasterPeakL = Engine.MonitorPeakLeft;
        MonitorMasterPeakR = Engine.MonitorPeakRight;
        StreamMasterPeakL = Engine.StreamPeakLeft;
        StreamMasterPeakR = Engine.StreamPeakRight;
    }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnRenderingMeters;
        _sessionPollTimer.Stop();
        SaveConfig();
        Hotkeys.Dispose();
        Engine.Dispose();
    }
}
