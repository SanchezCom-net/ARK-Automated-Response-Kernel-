using System.Runtime.InteropServices;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;
using Microsoft.Win32.SafeHandles;
using WpfPoint = System.Windows.Point;

namespace ARK.UI.Core.Input;

public sealed partial class InputService : IInputService, IDisposable
{
    // ── Win32 Constants ───────────────────────────────────────
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;
    private const int HC_ACTION      = 0;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int VK_SHIFT       = 0x10;
    private const int VK_CONTROL     = 0x11;
    private const int VK_MENU        = 0x12;
    private const int VK_LWIN        = 0x5B;
    private const int VK_RWIN        = 0x5C;

    // ── Win32 API ─────────────────────────────────────────────
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int nVirtKey);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint lpModuleName);

    // ── Win32 Structs ─────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public HOOKPOINT pt;
        public uint      mouseData;
        public uint      flags;
        public uint      time;
        public nuint     dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HOOKPOINT { public int X; public int Y; }

    // ── SafeHandle для автоматического снятия хука ────────────
    private sealed class HookSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal HookSafeHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle() => UnhookWindowsHookEx(handle);
    }

    // Маркер синтетических событий ARK (dwExtraInfo). Такие события пропускаются хуком —
    // предотвращает re-entry: SendInputNode → WH_KEYBOARD_LL → MacroScheduler → бесконечный цикл.
    private const nuint ArkSyntheticMarker = 0x4B5241; // "ARK" в ASCII

    // ── Делегаты хранятся как поля, чтобы GC не собрал их ─────
    private delegate nint HookProc(int nCode, nint wParam, nint lParam);
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;

    private HookSafeHandle? _keyboardHook;
    private HookSafeHandle? _mouseHook;

    // ── Зависимости ───────────────────────────────────────────
    private readonly ILogService   _logger;
    private readonly IVaultService _vault;

    // ── События ───────────────────────────────────────────────
    public event EventHandler<WpfPoint>?                 MouseMoved;
    public event EventHandler<MouseButtonHookEventArgs>? MouseLeftButtonPressed;
    public event EventHandler<MouseButtonHookEventArgs>? MouseRightButtonPressed;
    public event EventHandler<KeyHookEventArgs>?         KeyDown;
    public event EventHandler<KeyHookEventArgs>?         KeyUp;

    public InputService(ILogService logger, IVaultService vault)
    {
        _logger = logger;
        _vault  = vault;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _logger.LogInfoAsync(nameof(InputService), "Input Module инициализирован.");

    public async Task StartGlobalHooksAsync(CancellationToken cancellationToken = default)
    {
        _keyboardProc = OnKeyboardHook;
        _mouseProc    = OnMouseHook;

        var hmod = GetModuleHandleW(nint.Zero);

        var kbPtr = SetWindowsHookExW(
            WH_KEYBOARD_LL,
            Marshal.GetFunctionPointerForDelegate(_keyboardProc),
            hmod, 0);

        if (kbPtr == nint.Zero)
            throw new InvalidOperationException(
                $"Хук клавиатуры не установлен. Win32 error: {Marshal.GetLastWin32Error()}");

        _keyboardHook = new HookSafeHandle(kbPtr);

        var msPtr = SetWindowsHookExW(
            WH_MOUSE_LL,
            Marshal.GetFunctionPointerForDelegate(_mouseProc),
            hmod, 0);

        if (msPtr == nint.Zero)
        {
            _keyboardHook.Dispose();
            _keyboardHook = null;
            throw new InvalidOperationException(
                $"Хук мыши не установлен. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        _mouseHook = new HookSafeHandle(msPtr);
        await _logger.LogInfoAsync(nameof(InputService),
            "Глобальные хуки WH_KEYBOARD_LL и WH_MOUSE_LL установлены.");
    }

    public async Task StopGlobalHooksAsync(CancellationToken cancellationToken = default)
    {
        _keyboardHook?.Dispose(); _keyboardHook = null;
        _mouseHook?.Dispose();    _mouseHook    = null;
        _keyboardProc = null;
        _mouseProc    = null;
        await _logger.LogInfoAsync(nameof(InputService), "Глобальные хуки сняты.");
    }

    private nint OnKeyboardHook(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HC_ACTION)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Пропускаем собственные синтетические события ARK — предотвращает re-entry.
            if (data.dwExtraInfo != ArkSyntheticMarker)
            {
                var key  = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
                var mods = GetCurrentModifiers();
                var args = new KeyHookEventArgs(key, mods);

                if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
                    KeyDown?.Invoke(this, args);
                else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP)
                    KeyUp?.Invoke(this, args);
            }
        }
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private nint OnMouseHook(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HC_ACTION)
        {
            var data    = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var point   = new WpfPoint(data.pt.X, data.pt.Y);
            var btnArgs = new MouseButtonHookEventArgs(point);

            switch ((int)wParam)
            {
                case WM_MOUSEMOVE:    MouseMoved?.Invoke(this, point);             break;
                case WM_LBUTTONDOWN:  MouseLeftButtonPressed?.Invoke(this, btnArgs);  break;
                case WM_RBUTTONDOWN:  MouseRightButtonPressed?.Invoke(this, btnArgs); break;
            }
        }
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private static ModifierKeys GetCurrentModifiers()
    {
        var mods = ModifierKeys.None;
        if ((GetKeyState(VK_SHIFT)   & 0x8000) != 0) mods |= ModifierKeys.Shift;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) mods |= ModifierKeys.Control;
        if ((GetKeyState(VK_MENU)    & 0x8000) != 0) mods |= ModifierKeys.Alt;
        if ((GetKeyState(VK_LWIN)    & 0x8000) != 0 ||
            (GetKeyState(VK_RWIN)    & 0x8000) != 0) mods |= ModifierKeys.Windows;
        return mods;
    }

    public void Dispose()
    {
        _keyboardHook?.Dispose();
        _mouseHook?.Dispose();
        _keyboardProc = null;
        _mouseProc    = null;
    }
}
