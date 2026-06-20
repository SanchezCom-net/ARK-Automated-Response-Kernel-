using System.IO;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

/// <summary>
/// Реализует IModelWrapper для Vosk через внешний процесс VoskHost.exe.
/// Вся инфраструктура (Named Pipe, watchdog, перезапуск) унаследована от BaseSpeechHostedService.
/// Класс отвечает только за Vosk-специфическую валидацию модели и аргументы командной строки.
/// </summary>
public sealed class ExternalVoskService : BaseSpeechHostedService
{
    // Префиксы именованных каналов — должны точно совпадать с константами в ARK.VoskHost/PipeTransport.cs.
    private const string AudioPrefix = "ark-vosk-audio-";
    private const string CtrlPrefix  = "ark-vosk-ctrl-";

    public override ModelType Type => ModelType.Vosk;

    protected override string AudioPipePrefix => AudioPrefix;
    protected override string CtrlPipePrefix  => CtrlPrefix;

    public ExternalVoskService(ILogService logger, IOverlayService overlay, VoskSettingsSection settings)
        : base(logger, overlay, settings) { }

    protected override string BuildArguments(string pipeId, string modelPath, string language)
        => $"--pipe-id {pipeId} --model-path \"{modelPath}\" --language {language} --debug";

    protected override Task<string?> ValidateAsync(string modelPath, CancellationToken ct)
        => Task.FromResult(ValidateVoskModel(modelPath));

    private static string? ValidateVoskModel(string modelPath)
    {
        if (!Directory.Exists(modelPath))
            return $"Директория модели не найдена: '{modelPath}'.";
        if (!Directory.Exists(Path.Combine(modelPath, "conf")))
            return $"Неполная модель Vosk: отсутствует '{modelPath}\\conf\\'.";
        if (!Directory.Exists(Path.Combine(modelPath, "am")))
            return $"Неполная модель Vosk: отсутствует '{modelPath}\\am\\'.";
        return null;
    }
}
