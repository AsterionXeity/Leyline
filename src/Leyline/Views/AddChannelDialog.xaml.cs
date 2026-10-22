using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Leyline.Models;
using Leyline.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Leyline.Views;

public sealed partial class AddChannelDialog : Window
{
    private readonly IReadOnlyList<AudioDeviceInfo> _captureDevices;
    private readonly IReadOnlyList<AudioDeviceInfo> _renderDevices;
    private readonly IReadOnlyList<AudioSessionItem> _activeApps;
    private readonly Action<ChannelStripModel>? _onCreated;

    public ChannelStripModel? CreatedChannel { get; private set; }

    public AddChannelDialog(
        IReadOnlyList<AudioDeviceInfo> captureDevices,
        IReadOnlyList<AudioDeviceInfo> renderDevices,
        IReadOnlyList<AudioSessionItem> activeApps,
        Action<ChannelStripModel>? onCreated = null)
    {
        InitializeComponent();
        _captureDevices = captureDevices;
        _renderDevices = renderDevices;
        _activeApps = activeApps;
        _onCreated = onCreated;

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        try
        {
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWin = AppWindow.GetFromWindowId(wndId);
            appWin?.Resize(new SizeInt32(500, 520));
        }
        catch
        {
        }

        RootGrid.RequestedTheme = ThemeService.CurrentIsLightMode ? ElementTheme.Light : ElementTheme.Dark;

        foreach (var app in _activeApps)
        {
            ProcessCombo.Items.Add($"{app.ProcessName} (PID {app.ProcessId})");
        }
        if (ProcessCombo.Items.Count > 0)
            ProcessCombo.SelectedIndex = 0;

        RefreshSourceOptions();
    }

    private void SourceTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSourceOptions();
    }

    private void RefreshSourceOptions()
    {
        if (DeviceCombo == null || DevicePickerPanel == null || ProcessPickerPanel == null)
            return;

        int idx = SourceTypeCombo.SelectedIndex;
        if (idx == 0)
        {
            DevicePickerPanel.Visibility = Visibility.Visible;
            ProcessPickerPanel.Visibility = Visibility.Collapsed;
            DevicePickerLabel.Text = "Select Microphone / Capture Input Device";
            DeviceCombo.ItemsSource = _captureDevices;
            if (_captureDevices.Count > 0) DeviceCombo.SelectedIndex = 0;
        }
        else if (idx == 1)
        {
            DevicePickerPanel.Visibility = Visibility.Visible;
            ProcessPickerPanel.Visibility = Visibility.Collapsed;
            DevicePickerLabel.Text = "Select Virtual Bus / Playback Endpoint";
            DeviceCombo.ItemsSource = _renderDevices;
            if (_renderDevices.Count > 0) DeviceCombo.SelectedIndex = 0;
        }
        else if (idx == 2)
        {
            DevicePickerPanel.Visibility = Visibility.Collapsed;
            ProcessPickerPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DevicePickerPanel.Visibility = Visibility.Collapsed;
            ProcessPickerPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        string name = string.IsNullOrWhiteSpace(ChannelNameBox.Text) ? "Channel" : ChannelNameBox.Text.Trim();
        string icon = (IconCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "🎙️";
        string color = (ColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "#7C4DFF";

        int idx = SourceTypeCombo.SelectedIndex;
        var model = new ChannelStripModel
        {
            Name = name,
            Icon = icon,
            AccentHex = color,
            PersonalMix = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = idx == 0 },
            StreamMix = new MixSendState { IsEnabled = true, Volume = 0.85f, IsMuted = false }
        };

        if (idx == 0)
        {
            model.SourceType = ChannelSourceType.MicrophoneInput;
            if (DeviceCombo.SelectedItem is AudioDeviceInfo dev)
            {
                model.DeviceId = dev.Id;
                model.DeviceName = dev.Name;
            }
        }
        else if (idx == 1)
        {
            model.SourceType = ChannelSourceType.VirtualDeviceLoopback;
            if (DeviceCombo.SelectedItem is AudioDeviceInfo dev)
            {
                model.DeviceId = dev.Id;
                model.DeviceName = dev.Name;
            }
        }
        else if (idx == 2)
        {
            model.SourceType = ChannelSourceType.ProcessCapture;
            string rawProc = ProcessCombo.Text.Trim();
            var matchedApp = _activeApps.FirstOrDefault(a => rawProc.StartsWith(a.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (matchedApp != null)
            {
                model.TargetProcessId = matchedApp.ProcessId;
                model.TargetProcessName = matchedApp.ProcessName;
                model.DeviceName = $"App: {matchedApp.ProcessName}";
            }
            else
            {
                string cleanProc = rawProc.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();
                model.TargetProcessName = cleanProc;
                model.DeviceName = $"App: {cleanProc}";
            }
        }
        else
        {
            model.SourceType = ChannelSourceType.AppGroupChannel;
            model.DeviceName = "App Group Channel";
        }

        CreatedChannel = model;
        _onCreated?.Invoke(model);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
