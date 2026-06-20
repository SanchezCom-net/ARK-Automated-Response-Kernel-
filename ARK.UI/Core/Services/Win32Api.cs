using System.Runtime.InteropServices;

namespace ARK.UI.Core.Services;

public static partial class Win32Api
{
    public const int GWL_EXSTYLE       = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;

    public const uint WM_SYSCOMMAND = 0x0112;
    public const int  SC_SIZE       = 0xF000;
    public const int  SC_SIZE_SE    = 0xF008; // нижний правый угол (bottom-right)

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetActiveWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    public static partial uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ── Shell API для получения иконки процесса ──────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int    iIcon;
        public uint   dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON      = 0x000000100;
    public const uint SHGFI_SMALLICON = 0x000000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr SHGetFileInfoW(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // ── Shell API для детекции ассоциаций файлов ─────────────────────────

    public const uint ASSOCF_NONE          = 0;
    public const uint ASSOCSTR_EXECUTABLE  = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int AssocQueryStringW(
        uint    flags,
        uint    str,
        string  pszAssoc,
        string? pszExtra,
        char[]? pszOut,
        ref uint pcchOut);
}
