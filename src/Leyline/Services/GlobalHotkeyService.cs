using System.Runtime.InteropServices;
using Leyline.Models;

namespace Leyline.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_COMMAND = 0x0111;
    private const int WM_TRAYICON = 0x8000 + 101;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int CMD_TRAY_OPEN = 4001;
    private const int CMD_TRAY_MUTE_PERSONAL = 4002;
    private const int CMD_TRAY_MUTE_STREAM = 4003;
    private const int CMD_TRAY_STOP_SOUNDS = 4004;
    private const int CMD_TRAY_EXIT = 4005;

    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    private IntPtr _hwnd;
    private SubclassProcDelegate? _subclassDelegate;
    private bool _trayAdded;
    private System.Drawing.Icon? _appIcon;
    private readonly Dictionary<int, SoundPadModel> _registeredPads = new();
    private int _nextHotkeyId = 9000;

    public event Action<SoundPadModel>? PadHotkeyPressed;
    public event Action? StopAllHotkeyPressed;
    public event Action? TrayRestoreRequested;
    public event Action? TrayTogglePersonalMuteRequested;
    public event Action? TrayToggleStreamMuteRequested;
    public event Action? TrayExitRequested;

    public void AttachWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || _hwnd == windowHandle)
            return;

        _hwnd = windowHandle;
        _subclassDelegate = SubclassWndProc;
        SetWindowSubclass(_hwnd, _subclassDelegate, 1001, 0);

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        if (_hwnd == IntPtr.Zero || _trayAdded) return;

        try
        {
            _appIcon = IconGenerator.CreateAppIcon();
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _appIcon.Handle,
                szTip = "Leyline Audio Mixer & Router (Material 3 • WinUI 3)"
            };

            _trayAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
        }
        catch
        {
        }
    }

    public void RebindAll(IEnumerable<SoundPadModel> pads)
    {
        if (_hwnd == IntPtr.Zero) return;

        UnregisterAll();

        // Register Ctrl+Shift+0 (0x30) as Global Stop All Soundboard panic key
        RegisterHotKey(_hwnd, 8999, 2 | 4 | MOD_NOREPEAT, 0x30);

        foreach (var pad in pads)
        {
            if (pad.HotkeyVirtualKey != 0)
            {
                int id = _nextHotkeyId++;
                if (RegisterHotKey(_hwnd, id, pad.HotkeyModifiers | MOD_NOREPEAT, pad.HotkeyVirtualKey))
                {
                    _registeredPads[id] = pad;
                }
            }
        }
    }

    public void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;

        UnregisterHotKey(_hwnd, 8999);
        foreach (int id in _registeredPads.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _registeredPads.Clear();
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_HOTKEY)
        {
            int id = (int)wParam.ToInt64();
            if (id == 8999)
            {
                StopAllHotkeyPressed?.Invoke();
                return IntPtr.Zero;
            }
            if (_registeredPads.TryGetValue(id, out var pad))
            {
                PadHotkeyPressed?.Invoke(pad);
                return IntPtr.Zero;
            }
        }
        else if (uMsg == WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK || mouseMsg == WM_LBUTTONUP)
            {
                TrayRestoreRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU)
            {
                ShowTrayContextMenu();
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowTrayContextMenu()
    {
        if (_hwnd == IntPtr.Zero) return;

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(hMenu, MF_STRING, CMD_TRAY_OPEN, "Open Leyline");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, CMD_TRAY_MUTE_PERSONAL, "Mute / Unmute Personal Mix");
            AppendMenuW(hMenu, MF_STRING, CMD_TRAY_MUTE_STREAM, "Mute / Unmute Stream Mix");
            AppendMenuW(hMenu, MF_STRING, CMD_TRAY_STOP_SOUNDS, "Stop All Soundboard Pads");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, CMD_TRAY_EXIT, "Exit Leyline");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case CMD_TRAY_OPEN:
                    TrayRestoreRequested?.Invoke();
                    break;
                case CMD_TRAY_MUTE_PERSONAL:
                    TrayTogglePersonalMuteRequested?.Invoke();
                    break;
                case CMD_TRAY_MUTE_STREAM:
                    TrayToggleStreamMuteRequested?.Invoke();
                    break;
                case CMD_TRAY_STOP_SOUNDS:
                    StopAllHotkeyPressed?.Invoke();
                    break;
                case CMD_TRAY_EXIT:
                    TrayExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void RemoveTrayIcon()
    {
        if (_trayAdded && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _trayAdded = false;
        }
        _appIcon?.Dispose();
        _appIcon = null;
    }

    public void Dispose()
    {
        UnregisterAll();
        RemoveTrayIcon();
        if (_hwnd != IntPtr.Zero && _subclassDelegate != null)
        {
            RemoveWindowSubclass(_hwnd, _subclassDelegate, 1001);
            _subclassDelegate = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
