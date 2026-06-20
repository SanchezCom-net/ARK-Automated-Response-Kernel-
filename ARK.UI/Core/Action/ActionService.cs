using System.Runtime.InteropServices;
using System.Windows.Input;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Action;

public sealed partial class ActionService : IActionService
{
    // ── Win32 Constants ───────────────────────────────────────
    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE       = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;
    private const int  WHEEL_DELTA            = 120;

    private const uint KEYEVENTF_KEYUP   = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // ── Win32 API ─────────────────────────────────────────────
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    // ── Win32 Structs — union через LayoutKind.Explicit ───────
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT    Mouse;
        [FieldOffset(0)] public KEYBDINPUT   Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint      Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int   dx;
        public int   dy;
        public uint  mouseData;
        public uint  dwFlags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public nuint  dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint   uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // Маркер для синтетических событий ARK.
    // InputService фильтрует события с этим dwExtraInfo — предотвращает re-entry через WH_KEYBOARD_LL.
    internal const nuint ArkSyntheticMarker = 0x4B5241; // "ARK" в ASCII

    // ── Кешируем размер INPUT, чтобы не вычислять на каждый вызов
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // ── Зависимости ───────────────────────────────────────────
    private readonly ILogService _logger;

    public ActionService(ILogService logger)
    {
        _logger = logger;
    }

    // ── ClickAsync ────────────────────────────────────────────
    public Task ClickAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[3];

                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } } };

                inputs[1] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE } } };

                inputs[2] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE } } };

                SendInput(3, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Клик в ({x:F0}, {y:F0}).").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── RightClickAsync ──────────────────────────────────────
    public Task RightClickAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[3];
                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[1] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[2] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                SendInput(3, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Правый клик в ({x:F0}, {y:F0}).").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── DoubleClickAsync ──────────────────────────────────────
    public Task DoubleClickAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[5];
                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[1] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[2] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[3] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[4] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                SendInput(5, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Двойной клик в ({x:F0}, {y:F0}).").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── MoveAsync ─────────────────────────────────────────────
    public Task MoveAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[1];
                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                SendInput(1, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Курсор перемещён в ({x:F0}, {y:F0}).").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── ScrollAsync ───────────────────────────────────────────
    // amount > 0 — прокрутка вверх, amount < 0 — вниз. |amount| кратно WHEEL_DELTA (120).
    public Task ScrollAsync(double x, double y, int amount, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[2];
                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[1] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { mouseData = (uint)(amount * WHEEL_DELTA), dwFlags = MOUSEEVENTF_WHEEL,
                      dwExtraInfo = ArkSyntheticMarker } } };
                SendInput(2, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Прокрутка в ({x:F0}, {y:F0}), шаг={amount}.").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── MouseButtonDownAsync / MouseButtonUpAsync ─────────────
    public Task MouseButtonDownAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[2];
                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[1] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                SendInput(2, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Левая кнопка нажата в ({x:F0}, {y:F0}).").ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task MouseButtonUpAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            int sw   = GetSystemMetrics(SM_CXSCREEN);
            int sh   = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(x / sw * 65535.0 + 0.5);
            int absY = (int)(y / sh * 65535.0 + 0.5);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[2];
                inputs[0] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                inputs[1] = new INPUT { Type = INPUT_MOUSE, Data = new InputUnion { Mouse = new MOUSEINPUT
                    { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE,
                      dwExtraInfo = ArkSyntheticMarker } } };
                SendInput(2, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Левая кнопка отпущена в ({x:F0}, {y:F0}).").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── PressKeyAsync ─────────────────────────────────────────
    public Task PressKeyAsync(Key key, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);

            unsafe
            {
                INPUT* inputs = stackalloc INPUT[2];

                inputs[0] = new INPUT { Type = INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT
                    { wVk = vk, dwFlags = 0, dwExtraInfo = ArkSyntheticMarker } } };

                inputs[1] = new INPUT { Type = INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT
                    { wVk = vk, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = ArkSyntheticMarker } } };

                SendInput(2, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Нажата клавиша: {key}.").ConfigureAwait(false);
        }, cancellationToken);
    }

    // ── PressKeyWithModifiersAsync ────────────────────────────

    public Task PressKeyWithModifiersAsync(Key key, ModifierKeys modifiers,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            // Пауза перед отправкой: даёт время пользователю отпустить физические клавиши
            // триггерного хоткея. Без паузы синтетический Ctrl-Down накладывается на
            // физически удерживаемый Ctrl — SendInput смешивает потоки, и Ctrl-Up от
            // нашей последовательности досрочно снимает физически зажатый модификатор.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            bool ctrl = (modifiers & ModifierKeys.Control) != 0;
            bool shift = (modifiers & ModifierKeys.Shift)  != 0;
            bool alt  = (modifiers & ModifierKeys.Alt)     != 0;
            bool win  = (modifiers & ModifierKeys.Windows) != 0;
            var  vk   = (ushort)KeyInterop.VirtualKeyFromKey(key);

            // Максимум 4 модификатора + 1 цель = 10 событий (down + up).
            unsafe
            {
                INPUT* inputs = stackalloc INPUT[10];
                int idx = 0;

                if (ctrl)  inputs[idx++] = MakeKey(0x11, 0);
                if (shift) inputs[idx++] = MakeKey(0x10, 0);
                if (alt)   inputs[idx++] = MakeKey(0x12, 0);
                if (win)   inputs[idx++] = MakeKey(0x5B, 0);
                inputs[idx++]            = MakeKey(vk,   0);
                inputs[idx++]            = MakeKey(vk,   KEYEVENTF_KEYUP);
                if (win)   inputs[idx++] = MakeKey(0x5B, KEYEVENTF_KEYUP);
                if (alt)   inputs[idx++] = MakeKey(0x12, KEYEVENTF_KEYUP);
                if (shift) inputs[idx++] = MakeKey(0x10, KEYEVENTF_KEYUP);
                if (ctrl)  inputs[idx++] = MakeKey(0x11, KEYEVENTF_KEYUP);

                SendInput((uint)idx, inputs, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Нажата комбинация: {modifiers}+{key}.").ConfigureAwait(false);
        }, cancellationToken);
    }

    private static INPUT MakeKey(ushort vk, uint flags) =>
        new() { Type = INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT
            { wVk = vk, dwFlags = flags, dwExtraInfo = ArkSyntheticMarker } } };

    // ── TypeTextAsync ─────────────────────────────────────────
    public Task TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            // Два события на символ: нажатие + отпускание
            var inputs = new INPUT[text.Length * 2];

            for (int i = 0; i < text.Length; i++)
            {
                var scan = (ushort)text[i];

                inputs[i * 2] = new INPUT { Type = INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT
                    { wVk = 0, wScan = scan, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = ArkSyntheticMarker } } };

                inputs[i * 2 + 1] = new INPUT { Type = INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT
                    { wVk = 0, wScan = scan, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = ArkSyntheticMarker } } };
            }

            unsafe
            {
                fixed (INPUT* ptr = inputs)
                    SendInput((uint)inputs.Length, ptr, InputSize);
            }

            await _logger.LogInfoAsync(nameof(ActionService),
                $"Введён текст ({text.Length} симв.).").ConfigureAwait(false);
        }, cancellationToken);
    }
}
