using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;

namespace ARK.UI.Core.Nodes;

public sealed partial class Win_WindowManagerNode : BaseNode
{
    public const string AllWindowsKey = "[Все окна / Системная сетка]";

    public static readonly WinWindowAction[] SingleWindowActions =
    [
        WinWindowAction.Minimize, WinWindowAction.Maximize, WinWindowAction.Restore,
        WinWindowAction.Close, WinWindowAction.Focus, WinWindowAction.MoveAndResize,
        WinWindowAction.CheckActive
    ];

    public static readonly WinWindowAction[] AllWindowsActions =
    [
        WinWindowAction.MinimizeAll, WinWindowAction.RestoreAll,
        WinWindowAction.TileVertical, WinWindowAction.TileHorizontal, WinWindowAction.Cascade
    ];

    [LibraryImport("user32.dll")]
    private static partial ushort TileWindows(
        IntPtr hwndParent, uint wHow, IntPtr lpRect, uint cKids, IntPtr lpKids);

    [LibraryImport("user32.dll")]
    private static partial ushort CascadeWindows(
        IntPtr hwndParent, uint wHow, IntPtr lpRect, uint cKids, IntPtr lpKids);

    [JsonIgnore]
    public override string DefaultDataInputPropertyName => nameof(InputValue);

    // ── Свойства ────────────────────────────────────────────────────────────

    private WinWindowAction _selectedAction = WinWindowAction.Minimize;
    public WinWindowAction SelectedAction
    {
        get => _selectedAction;
        set { if (_selectedAction != value) { _selectedAction = value; OnPropertyChanged(); } }
    }

    // ProcessName хранит AllWindowsKey для переключения режима — не используется как цель напрямую.
    private string _processName = string.Empty;
    public string ProcessName
    {
        get => _processName;
        set
        {
            if (_processName == value) return;
            bool prevMode = IsAllWindowsMode;
            _processName = value;
            OnPropertyChanged();
            bool newMode = _processName == AllWindowsKey;
            if (prevMode != newMode)
            {
                OnPropertyChanged(nameof(IsAllWindowsMode));
                SelectedAction = newMode ? WinWindowAction.MinimizeAll : WinWindowAction.Minimize;
            }
        }
    }

    [JsonIgnore] public bool IsAllWindowsMode => _processName == AllWindowsKey;

    private string _inputValue = string.Empty;
    public string InputValue
    {
        get => _inputValue;
        set { if (_inputValue != value) { _inputValue = value; OnPropertyChanged(); } }
    }

    private bool _processInputData = false;
    public bool ProcessInputData
    {
        get => _processInputData;
        set { if (_processInputData != value) { _processInputData = value; OnPropertyChanged(); } }
    }

    private int _x;
    public int X
    {
        get => _x;
        set { if (_x != value) { _x = value; OnPropertyChanged(); } }
    }

    private int _y;
    public int Y
    {
        get => _y;
        set { if (_y != value) { _y = value; OnPropertyChanged(); } }
    }

    private int _width = 800;
    public int Width
    {
        get => _width;
        set { if (_width != value) { _width = value; OnPropertyChanged(); } }
    }

    private int _height = 600;
    public int Height
    {
        get => _height;
        set { if (_height != value) { _height = value; OnPropertyChanged(); } }
    }

    // ── Динамический список процессов (по аналогии с SpeechTriggerNode) ───

    [JsonPropertyName("processes")]
    public List<string> ProcessesData
    {
        get => [.. ProcessesList.Select(p => p.Text)];
        set
        {
            var texts = value is { Count: > 0 } ? value : (List<string>)[""];
            ProcessesList.Clear();
            foreach (var text in texts)
                AddItem(new PhraseItem(text));
            EnsureTrailingEmpty();
        }
    }

    [JsonIgnore]
    public ObservableCollection<PhraseItem> ProcessesList { get; } = [];

    public Win_WindowManagerNode() => AddItem(new PhraseItem(""));

    private void AddItem(PhraseItem item)
    {
        item.PropertyChanged += OnProcessItemChanged;
        ProcessesList.Add(item);
    }

    private void RemoveItem(PhraseItem item)
    {
        item.PropertyChanged -= OnProcessItemChanged;
        ProcessesList.Remove(item);
    }

    private void EnsureTrailingEmpty()
    {
        if (ProcessesList.Count == 0 || !string.IsNullOrWhiteSpace(ProcessesList[^1].Text))
            AddItem(new PhraseItem(""));
    }

    private void OnProcessItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PhraseItem item || e.PropertyName != nameof(PhraseItem.Text)) return;

        int idx  = ProcessesList.IndexOf(item);
        int last = ProcessesList.Count - 1;

        if (idx == last && !string.IsNullOrWhiteSpace(item.Text))
        {
            EnsureTrailingEmpty();
            return;
        }

        if (idx != last && string.IsNullOrWhiteSpace(item.Text) && ProcessesList.Count > 1)
            RemoveItem(item);
    }

    private IEnumerable<string> GetCleanProcesses() =>
        ProcessesList.Select(p => p.Text?.Trim())
                     .Where(t => !string.IsNullOrEmpty(t))!;

    // ── Выполнение ──────────────────────────────────────────────────────────

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        DebugSink?.Invoke($"[ОКНА] Управление окнами [{SelectedAction}]: запуск...");

        bool hasInput = TryApplyContextInput<string>(nameof(InputValue), v => InputValue = v);

        if (hasInput && !string.IsNullOrWhiteSpace(InputValue))
            DebugSink?.Invoke($"[ВХОД] Динамический процесс по проводу: «{InputValue}»");
        else
            DebugSink?.Invoke("[ВХОД] Входящих данных по серебряному проводу не обнаружено.");

        // Режим "Все окна / Системная сетка"
        if (IsAllWindowsMode)
        {
            DebugSink?.Invoke($"[СИСТЕМА] Глобальное действие над всеми окнами: [{SelectedAction}]...");
            bool ok = SelectedAction switch
            {
                WinWindowAction.MinimizeAll    => await MinimizeAllAsync(logger).ConfigureAwait(false),
                WinWindowAction.RestoreAll     => await RestoreAllAsync(logger).ConfigureAwait(false),
                WinWindowAction.TileVertical   => await TileAsync(0x0000, logger).ConfigureAwait(false),
                WinWindowAction.TileHorizontal => await TileAsync(0x0001, logger).ConfigureAwait(false),
                WinWindowAction.Cascade        => await CascadeAsync(logger).ConfigureAwait(false),
                _                              => false
            };
            DebugSink?.Invoke($"[ОКНА] Глобальное действие: {(ok ? "Успех ✓" : "Ошибка ✗")}");
            return ok;
        }

        // Формируем список целей из ProcessesList + серебряный провод
        var targets = new List<string>();

        foreach (var raw in GetCleanProcesses())
        {
            var clean = raw.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                targets.Add(clean);
                DebugSink?.Invoke($"[ПРОЦЕСС] Из списка: «{clean}»");
            }
        }

        if (ProcessInputData && !string.IsNullOrWhiteSpace(InputValue))
        {
            var clean = InputValue.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                targets.Add(clean);
                DebugSink?.Invoke($"[ПРОЦЕСС] Присланный по проводу: «{clean}»");
            }
        }

        if (targets.Count == 0)
        {
            DebugSink?.Invoke("[ОКНА] [ОТМЕНА] Список процессов пуст.");
            await logger.LogWarningAsync(Name, "[ОКНА] WindowManager: имя процесса не задано.").ConfigureAwait(false);
            return false;
        }

        bool allSuccess = true;
        string? outputName = null;

        foreach (var target in targets)
        {
            bool ok;
            try
            {
                ok = await ExecuteWindowActionAsync(target, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DebugSink?.Invoke($"[СИСТЕМА] [ОШИБКА] Сбой «{target}»: {ex.Message}");
                await logger.LogErrorAsync(Name, $"[ОКНА] Сбой «{target}».", ex).ConfigureAwait(false);
                ok = false;
            }

            if (ok) outputName ??= target;
            else    allSuccess = false;
        }

        if (allSuccess && IsDataOutputEnabled && outputName is not null)
        {
            string payload = outputName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? outputName : outputName + ".exe";
            LastOutputValue = new DataPacket { Type = DataType.Text, Payload = payload };
            DebugSink?.Invoke($"[ВЫХОД] Записано в серебряный порт: «{payload}»");
        }

        DebugSink?.Invoke($"[ОКНА] [{SelectedAction}] завершён. Статус: {(allSuccess ? "Успех ✓" : "Ошибка ✗")}");
        await logger.LogInfoAsync(Name,
            $"[ОКНА] WindowManager [{SelectedAction}] → {(allSuccess ? "УСПЕХ" : "ОШИБКА")}")
            .ConfigureAwait(false);

        return allSuccess;
    }

    // ── Одиночное окно ──────────────────────────────────────────────────────

    private async Task<bool> ExecuteWindowActionAsync(string cleanName, ILogService logger, CancellationToken _)
    {
        DebugSink?.Invoke($"[СИСТЕМА] Ищу окно «{cleanName}»...");

        var processes = Process.GetProcessesByName(cleanName);
        var hwnd = processes.Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        foreach (var p in processes) p.Dispose();

        if (hwnd == IntPtr.Zero)
        {
            DebugSink?.Invoke($"[СИСТЕМА] Окно «{cleanName}» не найдено ✗");
            await logger.LogWarningAsync(Name, $"[ОКНА] Окно «{cleanName}» не найдено.").ConfigureAwait(false);
            return false;
        }

        DebugSink?.Invoke($"[СИСТЕМА] Окно «{cleanName}» найдено (0x{hwnd:X}). Выполняю [{SelectedAction}]...");

        bool result = SelectedAction switch
        {
            WinWindowAction.Minimize      => DoMinimize(hwnd, cleanName),
            WinWindowAction.Maximize      => DoMaximize(hwnd, cleanName),
            WinWindowAction.Restore       => DoRestore(hwnd, cleanName),
            WinWindowAction.Close         => DoClose(hwnd, cleanName),
            WinWindowAction.Focus         => DoFocus(hwnd, cleanName),
            WinWindowAction.MoveAndResize => DoMoveAndResize(hwnd, cleanName),
            WinWindowAction.CheckActive   => DoCheckActive(hwnd, cleanName),
            _                             => false
        };

        await logger.LogInfoAsync(Name,
            $"[ОКНА] [{SelectedAction}] «{cleanName}»: {(result ? "✓" : "✗")}").ConfigureAwait(false);

        return result;
    }

    private bool DoMinimize(IntPtr hwnd, string name)
    {
        Win32Api.ShowWindow(hwnd, 6);
        DebugSink?.Invoke($"[СИСТЕМА] «{name}» → свёрнуто ✓");
        return true;
    }

    private bool DoMaximize(IntPtr hwnd, string name)
    {
        Win32Api.ShowWindow(hwnd, 3);
        DebugSink?.Invoke($"[СИСТЕМА] «{name}» → развёрнуто ✓");
        return true;
    }

    private bool DoRestore(IntPtr hwnd, string name)
    {
        Win32Api.ShowWindow(hwnd, 9);
        DebugSink?.Invoke($"[СИСТЕМА] «{name}» → восстановлено ✓");
        return true;
    }

    private bool DoClose(IntPtr hwnd, string name)
    {
        Win32Api.SendMessageW(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
        DebugSink?.Invoke($"[СИСТЕМА] WM_CLOSE → «{name}» ✓");
        return true;
    }

    private bool DoFocus(IntPtr hwnd, string name)
    {
        Win32Api.ShowWindow(hwnd, 9);
        Win32Api.SetForegroundWindow(hwnd);
        DebugSink?.Invoke($"[СИСТЕМА] «{name}» → активировано (фокус) ✓");
        return true;
    }

    private bool DoMoveAndResize(IntPtr hwnd, string name)
    {
        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, X, Y, Width, Height, 0x0040);
        DebugSink?.Invoke($"[СИСТЕМА] «{name}» → X={X}, Y={Y}, W={Width}, H={Height} ✓");
        return true;
    }

    private bool DoCheckActive(IntPtr hwnd, string name)
    {
        bool isActive = Win32Api.GetForegroundWindow() == hwnd;
        DebugSink?.Invoke($"[СИСТЕМА] «{name}»: {(isActive ? "АКТИВНО ✓" : "НЕ АКТИВНО ✗")}");
        return isActive;
    }

    // ── Все окна / системная сетка ──────────────────────────────────────────

    private async Task<bool> MinimizeAllAsync(ILogService logger)
    {
        var tray = Win32Api.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
            Win32Api.PostMessageW(tray, 0x0111, (IntPtr)415, IntPtr.Zero);
        DebugSink?.Invoke("[СИСТЕМА] Все окна → свёрнуты ✓");
        await logger.LogInfoAsync(Name, "[ОКНА] Все окна свёрнуты.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RestoreAllAsync(ILogService logger)
    {
        var tray = Win32Api.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
            Win32Api.PostMessageW(tray, 0x0111, (IntPtr)416, IntPtr.Zero);
        DebugSink?.Invoke("[СИСТЕМА] Все окна → восстановлены ✓");
        await logger.LogInfoAsync(Name, "[ОКНА] Все окна восстановлены.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TileAsync(uint wHow, ILogService logger)
    {
        TileWindows(IntPtr.Zero, wHow, IntPtr.Zero, 0, IntPtr.Zero);
        DebugSink?.Invoke("[СИСТЕМА] Окна упорядочены плиткой ✓");
        await logger.LogInfoAsync(Name, "[СИСТЕМА] Запущено упорядочивание окон.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CascadeAsync(ILogService logger)
    {
        CascadeWindows(IntPtr.Zero, 0x0004, IntPtr.Zero, 0, IntPtr.Zero);
        DebugSink?.Invoke("[СИСТЕМА] Окна упорядочены каскадом ✓");
        await logger.LogInfoAsync(Name, "[СИСТЕМА] Запущено упорядочивание окон.").ConfigureAwait(false);
        return true;
    }
}
