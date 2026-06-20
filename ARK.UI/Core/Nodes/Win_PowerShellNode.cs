using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARK.UI.Core.Interfaces;
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

    protected override async Task<bool> ExecuteCoreAsync(
        IServiceProvider serviceProvider, ILogService logger, CancellationToken cancellationToken)
    {
        string shellName = IsPowerShell ? "PowerShell" : "CMD";
        DebugSink?.Invoke($"[{shellName}] Инициализация выполнения сценария...");

        // Серебряный провод → ScriptText напрямую; зеркалим в InputValue для отображения в UI.
        bool hasInput = TryApplyContextInput<string>(nameof(ScriptText), v => { ScriptText = v; InputValue = v; });

        if (hasInput && !string.IsNullOrEmpty(ScriptText))
            DebugSink?.Invoke($"[ВХОД] Сценарий получен по проводу (длина: {ScriptText.Length} симв.)");
        else
            DebugSink?.Invoke("[ВХОД] Входящих данных по серебряному проводу не обнаружено.");

        if (string.IsNullOrWhiteSpace(ScriptText))
        {
            DebugSink?.Invoke($"[{shellName}] [ОТМЕНА] Тело сценария пусто.");
            await logger.LogWarningAsync(Name, $"[{shellName}] Сценарий не задан.").ConfigureAwait(false);
            return false;
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

            var readOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var readError  = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(readOutput, readError).ConfigureAwait(false);

            output = readOutput.Result;
            error  = readError.Result;

            // Ждём полного закрытия процесса в ОС — это освобождает системный OLE-буфер обмена.
            // Без этого следующая нода (например, Буфер обмена) получит CLIPBRD_E_CANT_OPEN.
            DebugSink?.Invoke($"[СИСТЕМА] Потоки считаны. Ожидание завершения процесса {shellName}...");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            DebugSink?.Invoke($"[СИСТЕМА] [ОШИБКА] Сбой при запуске {shellName}: {ex.Message}");
            await logger.LogErrorAsync(Name, $"[{shellName}] Сбой запуска.", ex).ConfigureAwait(false);
            return false;
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
        await logger.LogInfoAsync(Name,
            $"[{shellName}] Скрипт выполнен. Символов в ответе: {result.Length}, ExitCode: {exitCode}")
            .ConfigureAwait(false);

        return isSuccess;
    }

    private static string FilterCliXml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        if (!input.Contains("#< CLIXML")) return input;

        return Regex.Replace(input, @"#<\s*CLIXML[\s\S]*?</Objs>", string.Empty,
                             RegexOptions.IgnoreCase).Trim();
    }
}
