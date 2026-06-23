using System.Diagnostics;
using System.IO;
using ARK.UI.Core.Bus;
using ARK.UI.Core.Models;
using ARK.UI.Core.Services;

namespace ARK.UI.Core.Nodes;

public sealed class RunProcessNode : BaseNode
{
    public override string DefaultDataInputPropertyName => nameof(FilePathOrUrl);

    private string _filePathOrUrl = string.Empty;
    public string FilePathOrUrl
    {
        get => _filePathOrUrl;
        set { if (_filePathOrUrl != value) { _filePathOrUrl = value; OnPropertyChanged(); } }
    }

    private string _arguments = string.Empty;
    public string Arguments
    {
        get => _arguments;
        set { if (_arguments != value) { _arguments = value; OnPropertyChanged(); } }
    }

    // ── Нативная детекция ассоциаций файлов ──────────────────────────────

    private static string? GetAssociatedExecutable(string extension)
    {
        uint size = 0;
        Win32Api.AssocQueryStringW(Win32Api.ASSOCF_NONE, Win32Api.ASSOCSTR_EXECUTABLE,
            extension, null, null, ref size);
        if (size == 0) return null;

        var buf = new char[size];
        int hr  = Win32Api.AssocQueryStringW(Win32Api.ASSOCF_NONE, Win32Api.ASSOCSTR_EXECUTABLE,
            extension, null, buf, ref size);
        return hr == 0 ? new string(buf).TrimEnd('\0') : null;
    }

    // ── Выполнение ────────────────────────────────────────────────────────

    protected override async Task<NodeResult> ExecuteCoreAsync(
        DataBusPacket? inputPacket,
        CancellationToken ct)
    {
        DebugSink?.Invoke($"[ЛОНЧЕР] Запуск. Цель: «{FilePathOrUrl}»");

        if (inputPacket is { Type: not PortDataType.Signal } && DataBus is not null
            && DataBus.TryGet(inputPacket.SessionId, inputPacket.DataId, out var _raw))
        {
            var _s = _raw as string ?? _raw?.ToString();
            if (_s is not null) FilePathOrUrl = _s;
        }

        // ── Guard: пустой путь — безопасный выход ─────────────────────────
        if (string.IsNullOrWhiteSpace(FilePathOrUrl))
        {
            DebugSink?.Invoke("[ЛОНЧЕР] Путь не задан — пропуск.");
            await NodeLogger!.LogWarningAsync(Name,
                "[ЛОНЧЕР] Путь к приложению или веб-ссылка не заданы. Пропуск выполнения.")
                .ConfigureAwait(false);
            return NodeResult.Failure("Путь не задан.");
        }

        string launchTarget = FilePathOrUrl;
        string launchArgs   = Arguments;

        bool isUrl = FilePathOrUrl.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)
                  || FilePathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // ── Умная детекция: только для неисполняемых файлов ─────────────
        string ext          = Path.GetExtension(FilePathOrUrl).ToLowerInvariant();
        bool   isExecutable = ext is ".exe" or ".bat" or ".cmd" or ".lnk" or ".com" or ".ps1";

        if (!isUrl && !isExecutable && !string.IsNullOrEmpty(ext))
        {
            string? associatedExe = await Task.Run(
                () => GetAssociatedExecutable(ext), ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(associatedExe))
            {
                launchTarget = associatedExe;
                launchArgs   = string.IsNullOrWhiteSpace(Arguments)
                    ? $"\"{FilePathOrUrl}\""
                    : $"\"{FilePathOrUrl}\" {Arguments}";

                DebugSink?.Invoke($"[ЛОНЧЕР] Ассоциированная программа: «{associatedExe}»");
                await NodeLogger!.LogInfoAsync(Name,
                    $"[ЛОНЧЕР] Файл '{FilePathOrUrl}' открывается через '{associatedExe}'.")
                    .ConfigureAwait(false);
            }
        }
        else if (isExecutable)
        {
            DebugSink?.Invoke($"[ЛОНЧЕР] Исполняемый файл — запускаю напрямую без поиска ассоциаций.");
        }

        // ── Запуск с двойным перехватом ошибок ───────────────────────────
        DebugSink?.Invoke($"[ЛОНЧЕР] Запускаю: «{launchTarget}» {launchArgs}".TrimEnd());
        bool launchSuccess = false;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = launchTarget,
                        Arguments       = launchArgs,
                        UseShellExecute = isUrl || launchTarget == FilePathOrUrl
                    });
                    launchSuccess = true;
                }
                catch (Exception ex)
                {
                    DebugSink?.Invoke($"[ЛОНЧЕР] ОШИБКА: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugSink?.Invoke($"[ЛОНЧЕР] ОШИБКА (задача): {ex.Message}");
        }

        if (!launchSuccess)
        {
            await NodeLogger!.LogErrorAsync(Name,
                $"[ЛОНЧЕР] Сбой запуска '{launchTarget}'.")
                .ConfigureAwait(false);
            return NodeResult.Failure($"Сбой запуска '{launchTarget}'.");
        }

        LastOutputValue = new DataPacket { Type = DataType.Text, Payload = FilePathOrUrl };
        DebugSink?.Invoke($"[ВЫХОД] DataBus записан: «{FilePathOrUrl}»");

        await NodeLogger!.LogInfoAsync(Name, $"[ЛОНЧЕР] Запущено: '{launchTarget}' {launchArgs}".TrimEnd())
            .ConfigureAwait(false);

        var _sid = inputPacket?.SessionId ?? Guid.NewGuid();
        var _out = DataBusPacket.Text(_sid);
        DataBus?.Set(_out.SessionId, _out.DataId, FilePathOrUrl);
        return NodeResult.Success(_out);
    }
}
