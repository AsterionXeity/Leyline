using System.Runtime.InteropServices;
using System.Text;

namespace Leyline.Services;

public static class Win32DialogService
{
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_EXPLORER = 0x00080000;

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;
    private const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | 0x0010;

    public const uint MB_OK = 0x00000000;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_ICONWARNING = 0x00000030;

    public static string? ShowOpenFileDialog(IntPtr ownerHwnd, string title, string filterPairsNullSeparated)
    {
        var fileBuffer = new string('\0', 1024);
        var ofn = new OPENFILENAMEW
        {
            lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
            hwndOwner = ownerHwnd,
            lpstrFilter = filterPairsNullSeparated,
            lpstrFile = fileBuffer,
            nMaxFile = fileBuffer.Length,
            lpstrTitle = title,
            Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
        };

        if (GetOpenFileNameW(ref ofn))
        {
            int nullIdx = ofn.lpstrFile.IndexOf('\0');
            return nullIdx >= 0 ? ofn.lpstrFile[..nullIdx] : ofn.lpstrFile;
        }
        return null;
    }

    public static string? ShowSaveFileDialog(IntPtr ownerHwnd, string title, string filterPairsNullSeparated, string defaultFileName, string defaultExt = "json")
    {
        string padded = defaultFileName.PadRight(1024, '\0');
        var ofn = new OPENFILENAMEW
        {
            lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
            hwndOwner = ownerHwnd,
            lpstrFilter = filterPairsNullSeparated,
            lpstrFile = padded,
            nMaxFile = padded.Length,
            lpstrTitle = title,
            lpstrDefExt = defaultExt,
            Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR
        };

        if (GetSaveFileNameW(ref ofn))
        {
            int nullIdx = ofn.lpstrFile.IndexOf('\0');
            return nullIdx >= 0 ? ofn.lpstrFile[..nullIdx] : ofn.lpstrFile;
        }
        return null;
    }

    public static string? ShowBrowseFolderDialog(IntPtr ownerHwnd, string description)
    {
        var displayBuf = new StringBuilder(260);
        var bi = new BROWSEINFOW
        {
            hwndOwner = ownerHwnd,
            pszDisplayName = displayBuf,
            lpszTitle = description,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI
        };

        IntPtr pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero)
            return null;

        try
        {
            var pathBuf = new StringBuilder(260);
            if (SHGetPathFromIDListW(pidl, pathBuf))
                return pathBuf.ToString();
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    public static void ShowMessage(IntPtr ownerHwnd, string message, string title, uint flags = MB_OK | MB_ICONINFORMATION)
    {
        MessageBoxW(ownerHwnd, message, title, flags);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFOW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public StringBuilder pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
