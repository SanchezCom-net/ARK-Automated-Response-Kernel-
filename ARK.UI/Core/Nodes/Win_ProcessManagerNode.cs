using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Nodes;

public sealed class Win_ProcessManagerNode : BaseNode
{
    public static readonly WinProcessAction[] AllActions = Enum.GetValues<WinProcessAction>();

    [JsonIgnore]
    public override string DefaultDataInputPropertyName => nameof(InputValue);

    // ── Свойства ────────────────────────────────────────────────────────────

    private WinProcessAction _selectedAction = WinProcessAction.CheckRunning;
    public WinProcessAction SelectedAction
    {
        get => _selectedAction;
        set { if (_selectedAction != value) { _selectedAction = value; OnPropertyChanged(); } }
    }

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

    public Win_ProcessManagerNode() => AddItem(new PhraseItem(""));

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
        DebugSink?.Invoke($"[ПРОЦЕСС] Управление процессами [{SelectedAction}]: запуск...");

        // Приём данных из серебряного провода
        bool hasInput = TryApplyContextInput<string>(nameof(InputValue), v => InputValue = v);

        if (hasInput && !string.IsNullOrWhiteSpace(InputValue))
            DebugSink?.Invoke($"[ВХОД] Динамический процесс по проводу: «{InputValue}»");
        else
            DebugSink?.Invoke("[ВХОД] Входящих данных по серебряному проводу не обнаружено.");

        // Формируем список целей
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
            DebugSink?.Invoke("[ПРОЦЕСС] [ОТМЕНА] Список процессов пуст.");
            await logger.LogWarningAsync(Name, "[СИСТЕМА] ProcessManager: имя процесса не задано.").ConfigureAwait(false);
            return false;
        }

        bool allSuccess = true;
        string? outputName = null;

        foreach (var target in targets)
        {
            bool ok;
            try
            {
                ok = SelectedAction switch
                {
                    WinProcessAction.CheckRunning => ExecuteCheckRunning(target),
                    WinProcessAction.Kill         => ExecuteKill(target),
                    WinProcessAction.Restart      => await ExecuteRestartAsync(target, cancellationToken).ConfigureAwait(false),
                    _                             => false
                };
            }
            catch (Exception ex)
            {
                DebugSink?.Invoke($"[СИСТЕМА] [ОШИБКА] Сбой «{target}»: {ex.Message}");
                await logger.LogErrorAsync(Name, $"[СИСТЕМА] Сбой «{target}».", ex).ConfigureAwait(false);
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

        DebugSink?.Invoke($"[ПРОЦЕСС] [{SelectedAction}] завершён. Статус: {(allSuccess ? "Успех ✓" : "Ошибка ✗")}");
        await logger.LogInfoAsync(Name,
            $"[СИСТЕМА] ProcessManager [{SelectedAction}] → {(allSuccess ? "УСПЕХ" : "ОШИБКА")}")
            .ConfigureAwait(false);

        return allSuccess;
    }

    private bool ExecuteCheckRunning(string cleanName)
    {
        DebugSink?.Invoke($"[СИСТЕМА] Проверяю «{cleanName}»...");
        var processes = Process.GetProcessesByName(cleanName);
        bool isRunning = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        DebugSink?.Invoke($"[СИСТЕМА] «{cleanName}»: {(isRunning ? "ЗАПУЩЕН ✓" : "ЗАКРЫТ ✗")}");
        return isRunning;
    }

    private bool ExecuteKill(string cleanName)
    {
        DebugSink?.Invoke($"[СИСТЕМА] Завершаю все процессы «{cleanName}»...");
        var processes = Process.GetProcessesByName(cleanName);
        int count = processes.Length;
        foreach (var p in processes)
        {
            try   { p.Kill(); p.WaitForExit(); }
            catch { }
            finally { p.Dispose(); }
        }
        DebugSink?.Invoke($"[СИСТЕМА] Закрыто «{cleanName}»: {count} шт. ✓");
        return true;
    }

    private async Task<bool> ExecuteRestartAsync(string cleanName, CancellationToken cancellationToken)
    {
        DebugSink?.Invoke($"[СИСТЕМА] Перезапуск «{cleanName}»...");
        var processes = Process.GetProcessesByName(cleanName);
        foreach (var p in processes)
        {
            try   { p.Kill(); p.WaitForExit(); }
            catch { }
            finally { p.Dispose(); }
        }
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        Process.Start(new ProcessStartInfo(cleanName + ".exe") { UseShellExecute = true });
        DebugSink?.Invoke($"[СИСТЕМА] «{cleanName}» перезапущен ✓");
        return true;
    }
}
