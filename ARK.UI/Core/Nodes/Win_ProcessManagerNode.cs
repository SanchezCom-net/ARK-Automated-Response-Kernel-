using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ARK.UI.Core.Bus;
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

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        DebugSink?.Invoke($"[ПРОЦЕСС] Управление процессами [{SelectedAction}]: запуск...");

        bool _hasInput = false;
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) { InputValue = _s; _hasInput = true; }
        }

        if (_hasInput && !string.IsNullOrWhiteSpace(InputValue))
            DebugSink?.Invoke($"[ВХОД] Динамический процесс с DataBus: «{InputValue}»");
        else
            DebugSink?.Invoke("[ВХОД] Входящих данных на DataBus нет.");

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
            await NodeLogger!.LogWarningAsync(Name, "[СИСТЕМА] ProcessManager: имя процесса не задано.").ConfigureAwait(false);
            return NodeResult.Failure("Имя процесса не задано.");
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
                    WinProcessAction.Restart      => await ExecuteRestartAsync(target, ct).ConfigureAwait(false),
                    _                             => false
                };
            }
            catch (Exception ex)
            {
                DebugSink?.Invoke($"[СИСТЕМА] [ОШИБКА] Сбой «{target}»: {ex.Message}");
                await NodeLogger!.LogErrorAsync(Name, $"[СИСТЕМА] Сбой «{target}».", ex).ConfigureAwait(false);
                ok = false;
            }

            if (ok) outputName ??= target;
            else    allSuccess = false;
        }

        DebugSink?.Invoke($"[ПРОЦЕСС] [{SelectedAction}] завершён. Статус: {(allSuccess ? "Успех ✓" : "Ошибка ✗")}");
        await NodeLogger!.LogInfoAsync(Name,
            $"[СИСТЕМА] ProcessManager [{SelectedAction}] → {(allSuccess ? "УСПЕХ" : "ОШИБКА")}")
            .ConfigureAwait(false);

        if (!allSuccess) return NodeResult.Failure($"ProcessManager [{SelectedAction}] завершился с ошибкой.");

        string _payload = outputName is not null
            ? (outputName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? outputName : outputName + ".exe")
            : string.Empty;
        if (!string.IsNullOrEmpty(_payload))
        {
            LastOutputValue = new DataPacket { Type = DataType.Text, Payload = _payload };
            DebugSink?.Invoke($"[ВЫХОД] DataBus записан: «{_payload}»");
        }
        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, _payload);
        return NodeResult.Success(_out);
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
