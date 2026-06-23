using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Nodes;

public sealed class Win_PowerShellNode : BaseNode
{
    public override bool   IsDangerous       => true;
    public override string DangerWarningText =>
        "Выполнение непроверенных сценариев PowerShell может повредить операционную систему или привести к потере данных.";

    [JsonIgnore]
    public override string DefaultDataInputPropertyName => nameof(ScriptText);

    // ── Свойства ────────────────────────────────────────────────────────────

    private string _scriptText = string.Empty;
    public string ScriptText
    {
        get => _scriptText;
        set { if (_scriptText != value) { _scriptText = value; OnPropertyChanged(); } }
    }

    private string _inputValue = string.Empty;
    public string InputValue
    {
        get => _inputValue;
        set { if (_inputValue != value) { _inputValue = value; OnPropertyChanged(); } }
    }

    private bool _isPowerShell = true;
    public bool IsPowerShell
    {
        get => _isPowerShell;
        set { if (_isPowerShell != value) { _isPowerShell = value; OnPropertyChanged(); } }
    }

    private bool _processInputData = false;
    public bool ProcessInputData
    {
        get => _processInputData;
        set { if (_processInputData != value) { _processInputData = value; OnPropertyChanged(); } }
    }

    private string _consoleOutput = string.Empty;
    [JsonIgnore]
    public string ConsoleOutput
    {
        get => _consoleOutput;
        set { if (_consoleOutput != value) { _consoleOutput = value; OnPropertyChanged(); } }
    }

    // ── Выполнение ──────────────────────────────────────────────────────────

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        string shellName = IsPowerShell ? "PowerShell" : "CMD";
        DebugSink?.Invoke($"[{shellName}] Инициализация выполнения сценария...");

        bool _hasInput = false;
        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) { ScriptText = _s; InputValue = _s; _hasInput = true; }
        }

        if (_hasInput && !string.IsNullOrEmpty(ScriptText))
            DebugSink?.Invoke($"[ВХОД] Сценарий получен с DataBus (длина: {ScriptText.Length} симв.)");
        else
            DebugSink?.Invoke("[ВХОД] Входящих данных на DataBus нет.");

        if (string.IsNullOrWhiteSpace(ScriptText))
        {
            DebugSink?.Invoke($"[{shellName}] [ОТМЕНА] Тело сценария пусто.");
            await NodeLogger!.LogWarningAsync(Name, $"[{shellName}] Сценарий не задан.").ConfigureAwait(false);
            return NodeResult.Failure("Сценарий не задан.");
        }

        // Подготовка аргументов
        string fileName;
        string arguments;

        if (IsPowerShell)
        {
            fileName  = "powershell.exe";
            string script  = "$ProgressPreference = 'SilentlyContinue'; " +
                             "$OutputEncoding = [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                             ScriptText;
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        }
        else
        {
            fileName  = "cmd.exe";
            arguments = $"/c \"{ScriptText}\"";
        }

        DebugSink?.Invoke($"[СИСТЕМА] Запуск скрытого процесса {shellName} ({fileName})...");

        string output = string.Empty;
        string error  = string.Empty;
        int exitCode  = -1;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Не удалось запустить процесс.");

            // Keepalive: скрипты могут работать дольше 30 сек (стандартного watchdog timeout)
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(async () =>
            {
                while (!kaCts.IsCancellationRequested)
                {
                    ResetWatchdogTimer();
                    try { await Task.Delay(5_000, kaCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None);

            var readOutput = process.StandardOutput.ReadToEndAsync(ct);
            var readError  = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(readOutput, readError).ConfigureAwait(false);

            output = readOutput.Result;
            error  = readError.Result;

            DebugSink?.Invoke($"[СИСТЕМА] Потоки считаны. Ожидание завершения процесса {shellName}...");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            DebugSink?.Invoke($"[СИСТЕМА] [ОШИБКА] Сбой при запуске {shellName}: {ex.Message}");
            await NodeLogger!.LogErrorAsync(Name, $"[{shellName}] Сбой запуска.", ex).ConfigureAwait(false);
            return NodeResult.Failure($"Сбой запуска {shellName}: {ex.Message}");
        }

        bool isSuccess = exitCode == 0;
        DebugSink?.Invoke($"[СИСТЕМА] Выполнение завершено. ExitCode={exitCode}.");

        // ── Диагностика потока вывода (stdout) ──────────────────────────────
        if (!string.IsNullOrWhiteSpace(output))
        {
            string preview = output.Length > 150 ? output[..150] + "..." : output;
            DebugSink?.Invoke($"[{shellName}] [ВЫВОД] Считано из stdout: \"{preview.Trim()}\" (длина: {output.Length} симв.)");
        }
        else
        {
            DebugSink?.Invoke($"[{shellName}] [ВЫВОД] Поток стандартного вывода stdout пуст.");
        }

        // ── Диагностика потока ошибок (stderr) ──────────────────────────────
        if (!string.IsNullOrWhiteSpace(error))
        {
            string errPreview = error.Length > 150 ? error[..150] + "..." : error;
            DebugSink?.Invoke($"[{shellName}] [ОШИБКА] Обнаружен текст в stderr: \"{errPreview.Trim()}\" (длина: {error.Length} симв.)");
        }

        string result = FilterCliXml(string.IsNullOrWhiteSpace(output) ? error : output);
        ConsoleOutput = result;

        // ── Передача результата в серебряный порт ───────────────────────────
        if (IsDataOutputEnabled)
        {
            if (!string.IsNullOrEmpty(result))
            {
                LastOutputValue = new DataPacket { Type = DataType.Text, Payload = result };
                DebugSink?.Invoke($"[{shellName}] [ВЫХОД] Записано значение в серебряный порт: " +
                                  $"\"{result.Trim().Replace('\n', ' ')}\" (длина: {result.Length} симв.)");
            }
            else
            {
                LastOutputValue = null;
                DebugSink?.Invoke($"[{shellName}] [ВЫХОД] Результат выполнения пуст. Запись в порт отменена.");
            }
        }

        DebugSink?.Invoke($"[{shellName}] Завершено. Статус: {(isSuccess ? "Успех ✓" : "Ошибка ✗")}");
        await NodeLogger!.LogInfoAsync(Name,
            $"[{shellName}] Скрипт выполнен. Символов в ответе: {result.Length}, ExitCode: {exitCode}")
            .ConfigureAwait(false);

        if (!isSuccess) return NodeResult.Failure($"ExitCode: {exitCode}");

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, result);
        return NodeResult.Success(_out);
    }

    private static string FilterCliXml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        if (!input.Contains("#< CLIXML")) return input;

        return Regex.Replace(input, @"#<\s*CLIXML[\s\S]*?</Objs>", string.Empty,
                             RegexOptions.IgnoreCase).Trim();
    }
}
