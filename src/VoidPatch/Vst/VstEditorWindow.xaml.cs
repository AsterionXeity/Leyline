using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoidPatch.Models;
using VoidPatch.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace VoidPatch.Vst;

public sealed partial class VstEditorWindow : Window
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly VstHostInstance _instance;
    private readonly VstSlotModel _slotModel;
    private IntPtr _windowHwnd;
    private IntPtr _childHostHwnd;
    private bool _hasNativeGui;
    private bool _isInitializing = true;

    public VstEditorWindow(VstHostInstance instance, VstSlotModel slotModel)
    {
        InitializeComponent();
        _instance = instance;
        _slotModel = slotModel;

        RootGrid.RequestedTheme = ThemeService.CurrentIsLightMode ? ElementTheme.Light : ElementTheme.Dark;

        PluginTitleText.Text = _instance.PluginName;
        FormatBadgeText.Text = _instance.Format;
        PluginPathText.Text = _instance.PluginPath;
        Title = $"VoidPatch VST Host — {_instance.PluginName}";

        _windowHwnd = WindowNative.GetWindowHandle(this);
        ResizeWindow(840, 600);

        RootGrid.Loaded += OnRootLoaded;
        Closed += OnWindowClosed;
    }

    private void ResizeWindow(int width, int height)
    {
        try
        {
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(_windowHwnd);
            AppWindow appWin = AppWindow.GetFromWindowId(wndId);
            appWin?.Resize(new SizeInt32(width, height));
        }
        catch
        {
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        var parameters = _instance.GetParameters();
        ParamItemsControl.ItemsSource = parameters;

        _childHostHwnd = CreateWindowExW(
            0,
            "STATIC",
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0,
            56,
            760,
            460,
            _windowHwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_childHostHwnd != IntPtr.Zero && _instance.TryAttachEditor(_childHostHwnd, out int editorWidth, out int editorHeight))
        {
            _hasNativeGui = true;
            NoGuiHintText.Visibility = Visibility.Collapsed;
            int w = Math.Clamp(editorWidth + 40, 520, 1600);
            int h = Math.Clamp(editorHeight + 135, 380, 1100);
            ResizeWindow(w, h);
            UpdateChildHostBounds();
        }
        else
        {
            _hasNativeGui = false;
            if (_childHostHwnd != IntPtr.Zero)
            {
                DestroyWindow(_childHostHwnd);
                _childHostHwnd = IntPtr.Zero;
            }
            NoGuiHintText.Visibility = Visibility.Visible;
            ShowParamsToggle.IsChecked = true;
            ParamColumn.Width = new GridLength(360);
        }

        _isInitializing = false;
    }

    private void NativeHostContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateChildHostBounds();
    }

    private void UpdateChildHostBounds()
    {
        if (!_hasNativeGui || _childHostHwnd == IntPtr.Zero)
            return;

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int topOffset = (int)Math.Round(56 * scale);
        int width = Math.Max(100, (int)Math.Round(NativeHostContainer.ActualWidth * scale));
        int height = Math.Max(100, (int)Math.Round(NativeHostContainer.ActualHeight * scale));

        SetWindowPos(_childHostHwnd, IntPtr.Zero, 0, topOffset, width, height, SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    private void ShowParamsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowParamsToggle.IsChecked == true;
        ParamColumn.Width = show ? new GridLength(320) : new GridLength(0);
        if (show)
        {
            ParamItemsControl.ItemsSource = _instance.GetParameters();
        }
        UpdateChildHostBounds();
    }

    private void ParamSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (sender is Slider slider && slider.Tag is VstParameterItem param)
        {
            _instance.SetParameter(param.Id, param.Index, e.NewValue);
            _slotModel.Parameters[param.Index] = (float)e.NewValue;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _instance.CloseEditor();
        if (_childHostHwnd != IntPtr.Zero)
        {
            DestroyWindow(_childHostHwnd);
            _childHostHwnd = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
