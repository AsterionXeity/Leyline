using System.IO;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VoidPatch.Models;
using VoidPatch.Services;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace VoidPatch.Views;

public sealed partial class PadEditDialog : Window
{
    private readonly SoundPadModel _pad;
    private readonly Action<SoundPadModel>? _onSaved;
    private readonly IntPtr _hwnd;
    private uint _modifiers;
    private uint _vk;
    private string _hotkeyDisplay = string.Empty;

    public PadEditDialog(SoundPadModel pad, Action<SoundPadModel>? onSaved = null)
    {
        InitializeComponent();
        _pad = pad;
        _onSaved = onSaved;

        _hwnd = WindowNative.GetWindowHandle(this);
        try
        {
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            AppWindow appWin = AppWindow.GetFromWindowId(wndId);
            appWin?.Resize(new SizeInt32(520, 580));
        }
        catch
        {
        }

        RootGrid.RequestedTheme = ThemeService.CurrentIsLightMode ? ElementTheme.Light : ElementTheme.Dark;

        EmojiBox.Text = pad.Emoji;
        NameBox.Text = pad.Name;
        FilePathBox.Text = pad.FilePath;
        VolSlider.Value = pad.Volume;
        VolLabel.Text = $"{(int)Math.Round(pad.Volume * 100)}%";
        MonitorCheck.IsChecked = pad.SendToMonitor;
        StreamCheck.IsChecked = pad.SendToStream;

        _modifiers = pad.HotkeyModifiers;
        _vk = pad.HotkeyVirtualKey;
        _hotkeyDisplay = pad.HotkeyDisplay;
        HotkeyBox.Text = string.IsNullOrWhiteSpace(_hotkeyDisplay) ? "None" : _hotkeyDisplay;

        ModeCombo.SelectedIndex = pad.PlaybackMode switch
        {
            PadPlaybackMode.StopToggle => 0,
            PadPlaybackMode.OneShot => 1,
            PadPlaybackMode.Overlap => 2,
            PadPlaybackMode.Loop => 3,
            _ => 0
        };

        foreach (object itemObj in ColorCombo.Items)
        {
            if (itemObj is ComboBoxItem item && string.Equals(item.Content?.ToString(), pad.ColorHex, StringComparison.OrdinalIgnoreCase))
            {
                ColorCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void BrowseAudio_Click(object sender, RoutedEventArgs e)
    {
        string? path = Win32DialogService.ShowOpenFileDialog(
            _hwnd,
            "Select Soundboard Audio File",
            "Audio Files (*.wav;*.mp3;*.ogg;*.flac;*.m4a)\0*.wav;*.mp3;*.ogg;*.flac;*.m4a\0All Files (*.*)\0*.*\0\0");

        if (!string.IsNullOrWhiteSpace(path))
        {
            FilePathBox.Text = path;
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "Sound Pad" || NameBox.Text == "New Sound Pad")
            {
                NameBox.Text = Path.GetFileNameWithoutExtension(path);
            }
        }
    }

    private void VolSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VolLabel != null)
            VolLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
    }

    private void HotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        VirtualKey key = e.Key;

        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
            VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        uint mods = 0;
        var parts = new List<string>();

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl) { mods |= 2; parts.Add("Ctrl"); }
        if (shift) { mods |= 4; parts.Add("Shift"); }
        if (alt) { mods |= 1; parts.Add("Alt"); }

        int vk = (int)key;
        if (vk <= 0) return;

        string keyName = key.ToString().Replace("Number", "");
        parts.Add(keyName);
        _modifiers = mods;
        _vk = (uint)vk;
        _hotkeyDisplay = string.Join("+", parts);
        HotkeyBox.Text = _hotkeyDisplay;
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        _modifiers = 0;
        _vk = 0;
        _hotkeyDisplay = string.Empty;
        HotkeyBox.Text = "None";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _pad.Emoji = string.IsNullOrWhiteSpace(EmojiBox.Text) ? "🔊" : EmojiBox.Text.Trim();
        _pad.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Sound Pad" : NameBox.Text.Trim();
        _pad.ColorHex = (ColorCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "#00E5FF";
        _pad.FilePath = FilePathBox.Text.Trim();
        _pad.Volume = (float)VolSlider.Value;
        _pad.SendToMonitor = MonitorCheck.IsChecked == true;
        _pad.SendToStream = StreamCheck.IsChecked == true;
        _pad.HotkeyModifiers = _modifiers;
        _pad.HotkeyVirtualKey = _vk;
        _pad.HotkeyDisplay = _hotkeyDisplay;

        _pad.PlaybackMode = ModeCombo.SelectedIndex switch
        {
            0 => PadPlaybackMode.StopToggle,
            1 => PadPlaybackMode.OneShot,
            2 => PadPlaybackMode.Overlap,
            3 => PadPlaybackMode.Loop,
            _ => PadPlaybackMode.StopToggle
        };

        _onSaved?.Invoke(_pad);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
