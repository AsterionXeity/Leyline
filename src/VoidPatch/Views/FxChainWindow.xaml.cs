using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoidPatch.Audio;
using VoidPatch.Models;
using VoidPatch.Services;
using VoidPatch.Vst;
using Windows.Graphics;
using WinRT.Interop;

namespace VoidPatch.Views;

public sealed partial class FxChainWindow : Window
{
    private readonly ChannelStripModel _channel;
    private readonly DspChainProcessor _processor;
    private readonly IReadOnlyList<VstPluginInfo> _availablePlugins;
    private readonly IntPtr _hwnd;

    public FxChainWindow(
        ChannelStripModel channel,
        DspChainProcessor processor,
        IReadOnlyList<VstPluginInfo> availablePlugins)
    {
        InitializeComponent();
        _channel = channel;
        _processor = processor;
        _availablePlugins = availablePlugins;

        _hwnd = WindowNative.GetWindowHandle(this);
        try
        {
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            AppWindow appWin = AppWindow.GetFromWindowId(wndId);
            appWin?.Resize(new SizeInt32(820, 740));
        }
        catch
        {
        }

        RootGrid.RequestedTheme = ThemeService.CurrentIsLightMode ? ElementTheme.Light : ElementTheme.Dark;
        RootGrid.DataContext = _channel;
        ChannelIconText.Text = _channel.Icon;
        ChannelTitleText.Text = $"{_channel.Name} — Studio DSP & VST Rack";

        VstPickerCombo.ItemsSource = _availablePlugins;
        if (_availablePlugins.Count > 0)
            VstPickerCombo.SelectedIndex = 0;

        Closed += (_, _) => _channel.NotifyFxChanged();
    }

    private void AddSelectedVst_Click(object sender, RoutedEventArgs e)
    {
        if (VstPickerCombo.SelectedItem is VstPluginInfo info)
        {
            AddVstSlot(info.Name, info.FilePath, info.Format);
        }
    }

    private void BrowseVstFile_Click(object sender, RoutedEventArgs e)
    {
        string? path = Win32DialogService.ShowOpenFileDialog(
            _hwnd,
            "Select 64-bit VST3 or VST2 Plugin",
            "VST Plugins (*.vst3;*.dll)\0*.vst3;*.dll\0VST3 Plugins (*.vst3)\0*.vst3\0VST2 Plugins (*.dll)\0*.dll\0\0");

        if (!string.IsNullOrWhiteSpace(path))
        {
            string ext = Path.GetExtension(path);
            string format = ext.Equals(".vst3", StringComparison.OrdinalIgnoreCase) ? "VST3" : "VST2";
            string name = Path.GetFileNameWithoutExtension(path);
            AddVstSlot(name, path, format);
        }
    }

    private void AddVstSlot(string name, string path, string format)
    {
        var slot = new VstSlotModel
        {
            PluginName = name,
            PluginPath = path,
            Format = format,
            IsEnabled = true,
            WetMix = 1.0f
        };

        var inst = _processor.GetOrLoadVst(slot);
        if (inst == null || !inst.IsLoaded)
        {
            Win32DialogService.ShowMessage(
                _hwnd,
                $"Could not initialize plugin '{name}':\n{inst?.LastError ?? "Unknown error"}",
                "VST Host Notice",
                Win32DialogService.MB_OK | Win32DialogService.MB_ICONWARNING);

            if (inst != null)
                _processor.RemoveVst(slot.Id);
            return;
        }

        slot.PluginName = inst.PluginName;
        _channel.VstSlots.Add(slot);
        _channel.NotifyFxChanged();
    }

    private void OpenVstEditor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VstSlotModel slot)
        {
            var inst = _processor.GetOrLoadVst(slot);
            if (inst == null || !inst.IsLoaded)
            {
                Win32DialogService.ShowMessage(
                    _hwnd,
                    $"Plugin '{slot.PluginName}' could not be loaded: {inst?.LastError}",
                    "VST Host",
                    Win32DialogService.MB_OK | Win32DialogService.MB_ICONWARNING);
                return;
            }

            var editor = new VstEditorWindow(inst, slot);
            editor.Activate();
        }
    }

    private void RemoveVstSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VstSlotModel slot)
        {
            _processor.RemoveVst(slot.Id);
            _channel.VstSlots.Remove(slot);
            _channel.NotifyFxChanged();
        }
    }

    private void ApplyRackPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string presetKey)
        {
            _channel.Dsp.ApplyPreset(presetKey);
            _channel.NotifyFxChanged();
        }
    }

    private void ResetGate_Click(object sender, RoutedEventArgs e)
    {
        _channel.Dsp.ResetGate();
    }

    private void ResetEqFlat_Click(object sender, RoutedEventArgs e)
    {
        _channel.Dsp.ResetEq();
    }

    private void ResetComp_Click(object sender, RoutedEventArgs e)
    {
        _channel.Dsp.ResetComp();
    }

    private void ResetLimiter_Click(object sender, RoutedEventArgs e)
    {
        _channel.Dsp.ResetLimiter();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _channel.NotifyFxChanged();
        Close();
    }
}
