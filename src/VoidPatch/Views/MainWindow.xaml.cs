using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VoidPatch.Audio;
using VoidPatch.Models;
using VoidPatch.Services;
using VoidPatch.ViewModels;
using VoidPatch.Vst;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace VoidPatch.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private bool _allowRealExit;
    private bool _suppressDeviceComboSync;
    private ChannelStripModel? _draggedChannel;
    private int _savedFullWidth = 1480;
    private int _savedFullHeight = 880;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private static readonly string[] ChannelColorPalette =
    [
        "#29B6F6", "#9C27B0", "#8D3B3B", "#EC407A", "#FBC02D", "#FF7043", "#448AFF", "#00E676"
    ];

    private static readonly string[] ChannelIconPalette =
    [
        "🎮", "🌐", "🎵", "💬", "🎙️", "🖥️", "📺", "〰️", "🎸"
    ];

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        RootGrid.DataContext = _vm;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomTitleBarDragRegion);

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow != null)
        {
            _appWindow.Title = "VoidPatch";
            _appWindow.Resize(new SizeInt32(_savedFullWidth, _savedFullHeight));
            _appWindow.Closing += AppWindow_Closing;

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var tb = _appWindow.TitleBar;
                tb.ExtendsContentIntoTitleBar = true;
                tb.BackgroundColor = Windows.UI.Color.FromArgb(0, 22, 22, 24);
                tb.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 22, 22, 24);
                tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 22, 22, 24);
                tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 22, 22, 24);
                tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 227, 226, 233);
            }
        }

        ApplyElementTheme(ThemeService.CurrentIsLightMode);
        ThemeService.ThemeChanged += OnThemeChanged;

        RootGrid.Loaded += RootGrid_Loaded;
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        bool isVisible = LeftSidebarBorder.Visibility == Visibility.Visible;
        LeftSidebarBorder.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        SidebarCol.Width = isVisible ? new GridLength(0) : new GridLength(210);
    }

    private void OnThemeChanged(bool isLight)
    {
        ApplyElementTheme(isLight);
    }

    private void ApplyElementTheme(bool isLight)
    {
        RootGrid.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        if (_appWindow != null && AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = _appWindow.TitleBar;
            if (isLight)
            {
                tb.BackgroundColor = Windows.UI.Color.FromArgb(255, 247, 248, 252);
                tb.ForegroundColor = Windows.UI.Color.FromArgb(255, 25, 28, 32);
                tb.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 247, 248, 252);
                tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 247, 248, 252);
                tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 25, 28, 32);
            }
            else
            {
                tb.BackgroundColor = Windows.UI.Color.FromArgb(255, 17, 19, 24);
                tb.ForegroundColor = Windows.UI.Color.FromArgb(255, 227, 226, 233);
                tb.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 17, 19, 24);
                tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 17, 19, 24);
                tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 227, 226, 233);
            }
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.IsAnyPopupOpen = () =>
            RootGrid.XamlRoot != null &&
            VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Count > 0;

        _vm.Hotkeys.AttachWindow(_hwnd);
        _vm.Hotkeys.RebindAll(_vm.SoundPads);
        _vm.Hotkeys.TrayRestoreRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
        _vm.Hotkeys.TrayTogglePersonalMuteRequested += () => DispatcherQueue.TryEnqueue(() => _vm.IsMonitorMasterMuted = !_vm.IsMonitorMasterMuted);
        _vm.Hotkeys.TrayToggleStreamMuteRequested += () => DispatcherQueue.TryEnqueue(() => _vm.IsStreamMasterMuted = !_vm.IsStreamMasterMuted);
        _vm.Hotkeys.TrayExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        ModalAddAppCombo.ItemsSource = _vm.ActiveAppSessions;
        ModalVstPickerCombo.ItemsSource = _vm.DiscoveredVstPlugins;
        ModalMicDeviceCombo.ItemsSource = _vm.CaptureDevicesWithNone;
        ModalVirtualBusCombo.ItemsSource = _vm.VirtualDevicesWithNone;
    }

    public void HideToTray()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_HIDE);
    }

    public void RestoreFromTray()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_SHOW);
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
        }
        Activate();
    }

    public void ExitApplication()
    {
        _allowRealExit = true;
        ThemeService.ThemeChanged -= OnThemeChanged;
        _vm.Dispose();
        Close();
        Application.Current.Exit();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowRealExit && _vm.MinimizeToTrayOnClose)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }

        ThemeService.ThemeChanged -= OnThemeChanged;
        _vm.Dispose();
    }

    #region Left Sidebar Navigation & Compact Mini-Mixer

    private void SelectMainView(int index)
    {
        _vm.SelectedTabIndex = index;
        MixesView.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        AudioFlowView.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SoundboardView.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        var activeStyle = (Style)RootGrid.Resources["NavItemActiveBtn"];
        var normalStyle = (Style)RootGrid.Resources["NavItemBtn"];

        NavMixesBtn.Style = index == 0 ? activeStyle : normalStyle;
        NavAudioFlowBtn.Style = index == 1 ? activeStyle : normalStyle;
        NavSoundboardBtn.Style = index == 2 ? activeStyle : normalStyle;

        if (index == 1)
            _vm.RefreshActiveAppSessions();
    }

    private void NavMixes_Click(object sender, RoutedEventArgs e) => SelectMainView(0);
    private void NavAudioFlow_Click(object sender, RoutedEventArgs e) => SelectMainView(1);
    private void NavSoundboard_Click(object sender, RoutedEventArgs e) => SelectMainView(2);

    private void ToggleCompactWindow_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsCompactMode = !_vm.IsCompactMode;
        if (_vm.IsCompactMode)
        {
            if (_appWindow != null)
            {
                _savedFullWidth = _appWindow.Size.Width;
                _savedFullHeight = _appWindow.Size.Height;
                _appWindow.Resize(new SizeInt32(410, 700));
            }
            FullModeRootGrid.Visibility = Visibility.Collapsed;
            CompactMiniMixerRoot.Visibility = Visibility.Visible;
        }
        else
        {
            CompactMiniMixerRoot.Visibility = Visibility.Collapsed;
            FullModeRootGrid.Visibility = Visibility.Visible;
            _appWindow?.Resize(new SizeInt32(_savedFullWidth, _savedFullHeight));
        }
    }

    private void SelectListeningMix0_PointerPressed(object sender, PointerRoutedEventArgs e) => _vm.ListeningMixIndex = 0;
    private void SelectListeningMix1_PointerPressed(object sender, PointerRoutedEventArgs e) => _vm.ListeningMixIndex = 1;
    private void SelectListeningMix2_PointerPressed(object sender, PointerRoutedEventArgs e) => _vm.ListeningMixIndex = 2;
    private void SelectListeningMix3_PointerPressed(object sender, PointerRoutedEventArgs e) => _vm.ListeningMixIndex = 3;

    #endregion

    #region Drag-and-Drop Channel Reordering

    private void ChannelRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement fe && fe.Tag is ChannelStripModel ch)
        {
            _draggedChannel = ch;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(ch.Id);
        }
    }

    private void ChannelRow_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedChannel != null)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"Move {_draggedChannel.Name}";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private void ChannelRow_Drop(object sender, DragEventArgs e)
    {
        if (_draggedChannel != null &&
            sender is FrameworkElement fe &&
            fe.Tag is ChannelStripModel targetChannel &&
            _draggedChannel != targetChannel)
        {
            int oldIdx = _vm.Channels.IndexOf(_draggedChannel);
            int newIdx = _vm.Channels.IndexOf(targetChannel);
            _vm.MoveChannel(oldIdx, newIdx);
        }
        _draggedChannel = null;
    }

    #endregion

    #region Matrix Cell Routing Enable / Disable

    private void EmptyMixCell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border bd && bd.Tag is MixSendState send && !send.IsEnabled)
        {
            send.IsEnabled = true;
            _vm.SaveConfig();
        }
    }

    private void DisableMixCell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Border bd && bd.Tag is MixSendState send)
        {
            send.IsEnabled = !send.IsEnabled;
            _vm.SaveConfig();
        }
    }

    #endregion

    #region Create Channel Flyout Actions

    private void CreateChannelFlyout_Opening(object? sender, object e)
    {
        _vm.RefreshActiveAppSessions();
        _vm.CreateChannelSearchQuery = string.Empty;
    }

    private void CreateChannelFromInputDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AudioDeviceInfo dev)
        {
            _vm.CreateChannelFromInputDeviceCommand.Execute(dev);
            CreateChannelFlyout.Hide();
        }
    }

    private void CreateSoundboardChannel_Click(object sender, RoutedEventArgs e)
    {
        _vm.CreateSoundboardChannelCommand.Execute(null);
        CreateChannelFlyout.Hide();
    }

    private void CreatePresetChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string presetKey && !string.IsNullOrWhiteSpace(presetKey))
        {
            _vm.CreatePresetChannel(presetKey);
            CreateChannelFlyout.Hide();
        }
    }

    private void CreateChannelFromActiveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AudioSessionItem app)
        {
            _vm.CreateChannelFromActiveAppCommand.Execute(app);
            CreateChannelFlyout.Hide();
        }
    }

    private void CreateChannelFromSuggestedApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string appName)
        {
            _vm.CreateChannelFromSuggestedAppCommand.Execute(appName);
            CreateChannelFlyout.Hide();
        }
    }

    private void CreateEmptyChannel_Click(object sender, RoutedEventArgs e)
    {
        _vm.CreateEmptyChannelCommand.Execute(null);
        CreateChannelFlyout.Hide();
    }

    #endregion

    #region Channel Configuration Modal

    private void OpenChannelConfig_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ChannelStripModel ch)
        {
            SelectModalTab(0);
            _vm.OpenChannelConfig(ch, 0);
            SyncModalDeviceCombos(ch);
        }
    }

    private void OpenChannelEffects_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ChannelStripModel ch)
        {
            SelectModalTab(1);
            _vm.OpenChannelConfig(ch, 1);
            SyncModalDeviceCombos(ch);
        }
    }

    private void SyncModalDeviceCombos(ChannelStripModel ch)
    {
        _suppressDeviceComboSync = true;
        try
        {
            if (!ReferenceEquals(ModalMicDeviceCombo.ItemsSource, _vm.CaptureDevicesWithNone))
                ModalMicDeviceCombo.ItemsSource = _vm.CaptureDevicesWithNone;
            if (!ReferenceEquals(ModalVirtualBusCombo.ItemsSource, _vm.VirtualDevicesWithNone))
                ModalVirtualBusCombo.ItemsSource = _vm.VirtualDevicesWithNone;
            if (!ReferenceEquals(ModalAddAppCombo.ItemsSource, _vm.ActiveAppSessions))
                ModalAddAppCombo.ItemsSource = _vm.ActiveAppSessions;
            if (!ReferenceEquals(ModalVstPickerCombo.ItemsSource, _vm.DiscoveredVstPlugins))
                ModalVstPickerCombo.ItemsSource = _vm.DiscoveredVstPlugins;

            var matchMic = _vm.CaptureDevicesWithNone.FirstOrDefault(d => d.Id == ch.DeviceId)
                           ?? _vm.CaptureDevicesWithNone.FirstOrDefault();
            ModalMicDeviceCombo.SelectedItem = matchMic;

            var matchVirt = _vm.VirtualDevicesWithNone.FirstOrDefault(d => d.Id == ch.DeviceId)
                            ?? _vm.VirtualDevicesWithNone.FirstOrDefault();
            ModalVirtualBusCombo.SelectedItem = matchVirt;

            if (ModalVstPickerCombo.Items.Count > 0 && ModalVstPickerCombo.SelectedIndex < 0)
                ModalVstPickerCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressDeviceComboSync = false;
        }
    }

    private void CloseChannelConfigModal_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsChannelConfigModalOpen = false;
    }

    private void RemoveSelectedChannel_Click(object sender, RoutedEventArgs e)
    {
        _vm.RemoveSelectedChannelCommand.Execute(null);
    }

    private void RemoveAssignedApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AssignedAppModel app)
        {
            _vm.RemoveAssignedAppFromChannelCommand.Execute(app);
        }
    }

    private void SelectModalTab(int tabIndex)
    {
        ModalAppsContent.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModalEffectsContent.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

        ModalAppsTabBtn.Style = (Style)RootGrid.Resources[tabIndex == 0 ? "MatPrimaryBtn" : "MatBtn"];
        ModalEffectsTabBtn.Style = (Style)RootGrid.Resources[tabIndex == 1 ? "MatPrimaryBtn" : "MatBtn"];
    }

    private void ModalAppsTab_Click(object sender, RoutedEventArgs e) => SelectModalTab(0);
    private void ModalEffectsTab_Click(object sender, RoutedEventArgs e) => SelectModalTab(1);

    private void CycleChannelColorAndIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        var ch = _vm.SelectedConfigChannel;

        int cIdx = Array.IndexOf(ChannelColorPalette, ch.AccentHex);
        ch.AccentHex = ChannelColorPalette[(cIdx + 1) % ChannelColorPalette.Length];

        int iIdx = Array.IndexOf(ChannelIconPalette, ch.Icon);
        ch.Icon = ChannelIconPalette[(iIdx + 1) % ChannelIconPalette.Length];
        _vm.SaveConfig();
    }

    private void ToggleAddAppInline_Click(object sender, RoutedEventArgs e)
    {
        bool opening = AddAppInlinePanel.Visibility != Visibility.Visible;
        if (opening)
        {
            _vm.RefreshActiveAppSessions();
            if (!ReferenceEquals(ModalAddAppCombo.ItemsSource, _vm.ActiveAppSessions))
                ModalAddAppCombo.ItemsSource = _vm.ActiveAppSessions;
            if (ModalAddAppCombo.Items.Count > 0 && ModalAddAppCombo.SelectedIndex < 0)
                ModalAddAppCombo.SelectedIndex = 0;
        }

        AddAppInlinePanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfirmAddActiveAppToChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel != null && ModalAddAppCombo.SelectedItem is AudioSessionItem app)
        {
            _vm.AddAppToChannel(_vm.SelectedConfigChannel, app.DisplayName, app.ProcessName, app.ProcessId);
            AddAppInlinePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void BrowseAppExeForChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;

        string? file = Win32DialogService.ShowOpenFileDialog(
            _hwnd,
            "Select Application Executable (.exe)",
            "Applications (*.exe)\0*.exe\0All Files (*.*)\0*.*\0\0");

        if (!string.IsNullOrWhiteSpace(file))
        {
            string procName = Path.GetFileNameWithoutExtension(file);
            _vm.AddAppToChannel(_vm.SelectedConfigChannel, procName, procName, 0);
            AddAppInlinePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenFullFxRackWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        var ch = _vm.SelectedConfigChannel;
        var proc = _vm.Engine.GetChannelProcessor(ch.Id);
        if (proc != null)
        {
            var fxWin = new FxChainWindow(ch, proc.DspProcessor, _vm.DiscoveredVstPlugins);
            fxWin.Closed += (_, _) =>
            {
                ch.NotifyFxChanged();
                _vm.SaveConfig();
            };
            fxWin.Activate();
        }
    }

    private void AddVstFromModal_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null || ModalVstPickerCombo.SelectedItem is not VstPluginInfo info)
            return;

        var ch = _vm.SelectedConfigChannel;
        var proc = _vm.Engine.GetChannelProcessor(ch.Id);
        if (proc == null) return;

        var slot = new VstSlotModel
        {
            PluginName = info.Name,
            PluginPath = info.FilePath,
            Format = info.Format,
            IsEnabled = true,
            WetMix = 1.0f
        };

        var inst = proc.DspProcessor.GetOrLoadVst(slot);
        if (inst == null || !inst.IsLoaded)
        {
            Win32DialogService.ShowMessage(
                _hwnd,
                $"Could not initialize plugin '{info.Name}':\n{inst?.LastError ?? "Unknown error"}",
                "VST Host Notice",
                0x30);
            if (inst != null)
                proc.DspProcessor.RemoveVst(slot.Id);
            return;
        }

        slot.PluginName = inst.PluginName;
        ch.VstSlots.Add(slot);
        ch.NotifyFxChanged();
        _vm.SaveConfig();
    }

    private void OpenModalVstEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        if (sender is Button btn && btn.Tag is VstSlotModel slot)
        {
            var proc = _vm.Engine.GetChannelProcessor(_vm.SelectedConfigChannel.Id);
            var inst = proc?.DspProcessor.GetOrLoadVst(slot);
            if (inst != null && inst.IsLoaded)
            {
                var editor = new VstEditorWindow(inst, slot);
                editor.Closed += (_, _) => _vm.SaveConfig();
                editor.Activate();
            }
        }
    }

    private void RemoveModalVstSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        if (sender is Button btn && btn.Tag is VstSlotModel slot)
        {
            var proc = _vm.Engine.GetChannelProcessor(_vm.SelectedConfigChannel.Id);
            proc?.DspProcessor.RemoveVst(slot.Id);
            _vm.SelectedConfigChannel.VstSlots.Remove(slot);
            _vm.SelectedConfigChannel.NotifyFxChanged();
            _vm.SaveConfig();
        }
    }

    private void ApplyBuiltInVstPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        if (sender is Button btn && btn.Tag is string presetKey)
        {
            _vm.SelectedConfigChannel.Dsp.ApplyPreset(presetKey);
            _vm.SelectedConfigChannel.NotifyFxChanged();
            _vm.SaveConfig();
        }
    }

    private void ResetModalPreamp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        _vm.SelectedConfigChannel.InputGainDb = 0f;
        _vm.SaveConfig();
    }

    private void ResetModalGate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        _vm.SelectedConfigChannel.Dsp.ResetGate();
        _vm.SaveConfig();
    }

    private void ResetModalEq_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        _vm.SelectedConfigChannel.Dsp.ResetEq();
        _vm.SaveConfig();
    }

    private void ResetModalComp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        _vm.SelectedConfigChannel.Dsp.ResetComp();
        _vm.SaveConfig();
    }

    private void ResetModalLimiter_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedConfigChannel == null) return;
        _vm.SelectedConfigChannel.Dsp.ResetLimiter();
        _vm.SaveConfig();
    }

    #endregion

    #region Settings Modal Navigation

    private void SelectSettingsTab(int index)
    {
        SettingsGeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsDevicesPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsEffectsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsRestorePanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        var activeStyle = (Style)RootGrid.Resources["NavItemActiveBtn"];
        var normalStyle = (Style)RootGrid.Resources["NavItemBtn"];

        SetTabGeneralBtn.Style = index == 0 ? activeStyle : normalStyle;
        SetTabDevicesBtn.Style = index == 1 ? activeStyle : normalStyle;
        SetTabEffectsBtn.Style = index == 2 ? activeStyle : normalStyle;
        SetTabRestoreBtn.Style = index == 3 ? activeStyle : normalStyle;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        SelectSettingsTab(0);
        _vm.IsSettingsOpen = true;
    }

    private void OpenDriverSettings_Click(object sender, RoutedEventArgs e)
    {
        SelectSettingsTab(1);
        _vm.IsSettingsOpen = true;
    }

    private void OpenPluginsSettings_Click(object sender, RoutedEventArgs e)
    {
        SelectSettingsTab(2);
        _vm.IsSettingsOpen = true;
    }

    private void SetTabGeneral_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(0);
    private void SetTabDevices_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(1);
    private void SetTabEffects_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(2);
    private void SetTabRestore_Click(object sender, RoutedEventArgs e) => SelectSettingsTab(3);

    private void PluginBuiltInTab_Click(object sender, RoutedEventArgs e)
    {
        BuiltInPluginsScroll.Visibility = Visibility.Visible;
        ScannedPluginsScroll.Visibility = Visibility.Collapsed;
        BlockedPluginsScroll.Visibility = Visibility.Collapsed;
        PluginBuiltInTabBtn.Style = (Style)RootGrid.Resources["MatPrimaryBtn"];
        PluginScannedTabBtn.Style = (Style)RootGrid.Resources["MatBtn"];
        PluginBlockedTabBtn.Style = (Style)RootGrid.Resources["MatBtn"];
    }

    private void PluginScannedTab_Click(object sender, RoutedEventArgs e)
    {
        BuiltInPluginsScroll.Visibility = Visibility.Collapsed;
        ScannedPluginsScroll.Visibility = Visibility.Visible;
        BlockedPluginsScroll.Visibility = Visibility.Collapsed;
        PluginBuiltInTabBtn.Style = (Style)RootGrid.Resources["MatBtn"];
        PluginScannedTabBtn.Style = (Style)RootGrid.Resources["MatPrimaryBtn"];
        PluginBlockedTabBtn.Style = (Style)RootGrid.Resources["MatBtn"];
    }

    private void PluginBlockedTab_Click(object sender, RoutedEventArgs e)
    {
        BuiltInPluginsScroll.Visibility = Visibility.Collapsed;
        ScannedPluginsScroll.Visibility = Visibility.Collapsed;
        BlockedPluginsScroll.Visibility = Visibility.Visible;
        PluginBuiltInTabBtn.Style = (Style)RootGrid.Resources["MatBtn"];
        PluginBlockedTabBtn.Style = (Style)RootGrid.Resources["MatPrimaryBtn"];
        PluginScannedTabBtn.Style = (Style)RootGrid.Resources["MatBtn"];
    }

    private void OpenMasterBusFxFromSettings_Click(object sender, RoutedEventArgs e)
    {
        var tempMasterChannel = new ChannelStripModel
        {
            Name = "Personal Mix Master Bus",
            Icon = "🎧",
            Dsp = _vm.Config.MonitorMasterDsp
        };
        var fxWin = new FxChainWindow(tempMasterChannel, _vm.Engine.MonitorMasterDsp, _vm.DiscoveredVstPlugins);
        fxWin.Closed += (_, _) => _vm.SaveConfig();
        fxWin.Activate();
    }

    private void ApplyGlobalBuiltInPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string presetKey)
        {
            foreach (var ch in _vm.Channels)
            {
                ch.Dsp.ApplyPreset(presetKey);
                ch.NotifyFxChanged();
            }
            _vm.SaveConfig();
            _vm.StatusBannerText = presetKey == "reset"
                ? "Reset Built-in VSTs across all channels."
                : $"Applied '{presetKey}' Built-in VST preset across all channels.";
        }
    }

    private void ToggleAllowBlockedPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VstPluginInfo plugin)
        {
            _vm.ToggleAllowBlockedPluginCommand.Execute(plugin);
        }
    }

    #endregion

    #region Per-Channel Device Switching & Master FX

    private void InlineMicCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is ChannelStripModel channel)
        {
            _suppressDeviceComboSync = true;
            try
            {
                if (combo.ItemsSource == null)
                    combo.ItemsSource = _vm.CaptureDevicesWithNone;
                var match = _vm.CaptureDevicesWithNone.FirstOrDefault(d => d.Id == channel.DeviceId)
                            ?? _vm.CaptureDevicesWithNone.FirstOrDefault();
                if (!ReferenceEquals(combo.SelectedItem, match))
                    combo.SelectedItem = match;
            }
            finally
            {
                _suppressDeviceComboSync = false;
            }
        }
    }

    private void ChannelDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceComboSync) return;

        if (sender is ComboBox combo &&
            combo.Tag is ChannelStripModel channel &&
            combo.SelectedItem is AudioDeviceInfo selectedDev)
        {
            _vm.ChangeChannelDevice(channel, selectedDev.Id);
        }
    }

    private void ModalDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceComboSync || _vm.SelectedConfigChannel == null) return;

        if (sender is ComboBox combo && combo.SelectedItem is AudioDeviceInfo selectedDev)
        {
            _vm.ChangeChannelDevice(_vm.SelectedConfigChannel, selectedDev.Id);
        }
    }

    private void OpenMonitorMasterFx_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var tempMasterChannel = new ChannelStripModel
        {
            Name = "Personal Mix Master Bus",
            Icon = "🎧",
            Dsp = _vm.Config.MonitorMasterDsp
        };
        var fxWin = new FxChainWindow(tempMasterChannel, _vm.Engine.MonitorMasterDsp, _vm.DiscoveredVstPlugins);
        fxWin.Closed += (_, _) => _vm.SaveConfig();
        fxWin.Activate();
    }

    #endregion

    #region Audio Flow Actions

    private void FlowAppChannelCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            if (combo.ItemsSource == null)
                combo.ItemsSource = _vm.Channels;
            if (_vm.Channels.Count > 0 && combo.SelectedIndex < 0)
                combo.SelectedIndex = 0;
        }
    }

    private void AssignAppToChannelFromFlow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn &&
            btn.Tag is AudioSessionItem app &&
            btn.Parent is Grid parentGrid)
        {
            var combo = parentGrid.Children.OfType<ComboBox>().FirstOrDefault();
            if (combo?.SelectedItem is ChannelStripModel channel)
            {
                _vm.RouteApplicationToChannel(app, channel);
            }
        }
    }

    #endregion

    #region Soundboard Actions

    private void TriggerSoundPad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SoundPadModel pad)
        {
            _vm.TriggerPadCommand.Execute(pad);
        }
    }

    private void AddSoundPad_Click(object sender, RoutedEventArgs e)
    {
        var newPad = new SoundPadModel
        {
            Name = "New Sound Pad",
            Emoji = "🔥",
            ColorHex = "#00E5FF",
            Volume = 0.90f
        };

        var dlg = new PadEditDialog(newPad, _ =>
        {
            _vm.Config.SoundPads.Add(newPad);
            _vm.SoundPads.Add(newPad);
            _vm.Hotkeys.RebindAll(_vm.SoundPads);
            _vm.SaveConfig();
        });
        dlg.Activate();
    }

    private void EditSoundPad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SoundPadModel pad)
        {
            var dlg = new PadEditDialog(pad, _ =>
            {
                _vm.Hotkeys.RebindAll(_vm.SoundPads);
                _vm.SaveConfig();
            });
            dlg.Activate();
        }
    }

    private void DeleteSoundPad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SoundPadModel pad)
        {
            _vm.SoundPads.Remove(pad);
            _vm.Config.SoundPads.Remove(pad);
            _vm.Hotkeys.RebindAll(_vm.SoundPads);
            _vm.SaveConfig();
        }
    }

    #endregion

    #region VST Directory Actions

    private void AddVstFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = Win32DialogService.ShowBrowseFolderDialog(
            _hwnd,
            "Select a folder containing 64-bit VST2 (.dll) or VST3 (.vst3) plugins");

        if (!string.IsNullOrWhiteSpace(folder))
        {
            if (!_vm.Config.CustomVstDirectories.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _vm.Config.CustomVstDirectories.Add(folder);
                _vm.SaveConfig();
                _vm.ScanVstPlugins(forceDeepProbe: true);
            }
        }
    }

    #endregion

    #region Configuration Backup & Import Actions

    private void BackupConfiguration_Click(object sender, RoutedEventArgs e)
    {
        string defaultName = $"VoidPatch-Backup-{DateTime.Now:yyyy-MM-dd}.json";
        string? targetPath = Win32DialogService.ShowSaveFileDialog(
            _hwnd,
            "Backup VoidPatch Configuration",
            "VoidPatch Backup (*.json)\0*.json\0All Files (*.*)\0*.*\0\0",
            defaultName,
            "json");

        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            if (_vm.ExportConfigurationBackup(targetPath, out string error))
            {
                Win32DialogService.ShowMessage(
                    _hwnd,
                    $"Configuration backed up successfully to:\n{targetPath}",
                    "VoidPatch Backup Complete",
                    Win32DialogService.MB_OK | Win32DialogService.MB_ICONINFORMATION);
            }
            else
            {
                Win32DialogService.ShowMessage(
                    _hwnd,
                    $"Failed to export backup:\n{error}",
                    "VoidPatch Backup Error",
                    Win32DialogService.MB_OK | Win32DialogService.MB_ICONWARNING);
            }
        }
    }

    private void ImportConfiguration_Click(object sender, RoutedEventArgs e)
    {
        string? sourcePath = Win32DialogService.ShowOpenFileDialog(
            _hwnd,
            "Import VoidPatch Configuration Backup",
            "VoidPatch Backup (*.json)\0*.json\0All Files (*.*)\0*.*\0\0");

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            if (_vm.ImportConfigurationBackup(sourcePath, out string error))
            {
                ApplyElementTheme(_vm.IsLightMode);
                Win32DialogService.ShowMessage(
                    _hwnd,
                    $"Configuration imported and applied successfully from:\n{sourcePath}",
                    "VoidPatch Import Complete",
                    Win32DialogService.MB_OK | Win32DialogService.MB_ICONINFORMATION);
            }
            else
            {
                Win32DialogService.ShowMessage(
                    _hwnd,
                    $"Failed to import configuration backup:\n{error}",
                    "VoidPatch Import Error",
                    Win32DialogService.MB_OK | Win32DialogService.MB_ICONWARNING);
            }
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ConfigService.GetConfigFilePath());
            if (!string.IsNullOrWhiteSpace(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }

    private void StartupInstallDriverAndContinue_Click(object sender, RoutedEventArgs e)
    {
        _vm.ProvisionDriverCommand.Execute(null);
        StartupDriverPromptOverlay.Visibility = Visibility.Collapsed;
        FullModeRootGrid.Visibility = Visibility.Visible;
    }

    private void StartupContinueToMainScreen_Click(object sender, RoutedEventArgs e)
    {
        StartupDriverPromptOverlay.Visibility = Visibility.Collapsed;
        FullModeRootGrid.Visibility = Visibility.Visible;
    }

    #endregion
}
