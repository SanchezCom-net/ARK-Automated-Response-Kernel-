using System.IO;
using System.Text;
using System.Text.Json;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

/// <summary>
/// Реализует IModelWrapper для Whisper через внешний процесс WhisperHost.exe.
/// Логика IPC, watchdog и перезапуска — в BaseSpeechHostedService.
/// Конфигурация передаётся воркеру через --config-b64 (Base64-кодированный JSON),
/// что исключает хардкод и shell-escaping проблемы.
/// </summary>
public sealed class ExternalWhisperService : BaseSpeechHostedService
{
    private const string AudioPrefix = "ark-whisper-audio-";
    private const string CtrlPrefix  = "ark-whisper-ctrl-";

    // Ключевые слова для фильтра stderr: пропускаем только строки с признаками ошибки
    private static readonly string[] StderrErrorKeywords =
        ["error", "failed", "exception", "fatal", "critical", "panic", "crash", "assert"];

    private readonly bool _useGpu;
    private readonly int  _gpuDevice;
    private readonly int  _gpuStartupDelayMs;

    public override ModelType Type => ModelType.Whisper;

    protected override string AudioPipePrefix => AudioPrefix;
    protected override string CtrlPipePrefix  => CtrlPrefix;

    public ExternalWhisperService(
        ILogService logger, IOverlayService overlay,
        WhisperSettingsSection settings, bool useGpu,
        int gpuDevice = 0, int gpuStartupDelayMs = 800)
        : base(logger, overlay, settings)
    {
        _useGpu            = useGpu;
        _gpuDevice         = gpuDevice;
        _gpuStartupDelayMs = gpuStartupDelayMs;
    }

    protected override string BuildArguments(string pipeId, string modelPath, string language)
    {
        var config = new WhisperWorkerConfig
        {
            ModelPath  = modelPath,
            Language   = language,
            UseGpu     = _useGpu,
            GpuDevice  = _gpuDevice,
            Precision  = DetectPrecision(modelPath),
            ModelType  = DetectModelType(modelPath)
        };

        var json   = JsonSerializer.Serialize(config);
        var b64    = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"--pipe-id {pipeId} --config-b64 {b64} --debug";
    }

    protected override Task<string?> ValidateAsync(string modelPath, CancellationToken ct)
        => Task.FromResult(ValidateWhisperModel(modelPath));

    private static string? ValidateWhisperModel(string modelPath)
    {
        if (!File.Exists(modelPath))
            return $"Файл модели Whisper не найден: '{modelPath}'. " +
                   "Скачайте .bin по адресу https://huggingface.co/ggerganov/whisper.cpp";

        if (!modelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            return $"Ожидается файл формата GGML .bin: '{Path.GetFileName(modelPath)}'.";

        return null;
    }

    // Определяет тип модели по имени родительской папки (base / turbo / large / medium / small / tiny).
    private static string DetectModelType(string modelPath)
        => (Path.GetFileName(Path.GetDirectoryName(modelPath) ?? string.Empty)
            .ToLowerInvariant()) switch
        {
            "turbo"  => "turbo",
            "large"  => "large",
            "medium" => "medium",
            "small"  => "small",
            "tiny"   => "tiny",
            _        => "base"
        };

    // Определяет квантизацию по имени файла модели (f16 / f32 / q*).
    private static string DetectPrecision(string modelPath)
    {
        var name = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
        if (name.Contains(".f32") || name.EndsWith("-f32")) return "float32";
        if (name.Contains(".q"))                             return "quantized";
        return "float16"; // GGML-base.bin по умолчанию — f16
    }

    // ── GPU: задержка перед захватом CUDA + принудительный GC ────────────────

    protected override async Task OnBeforeStartAsync(CancellationToken ct)
    {
        if (!_useGpu || _gpuStartupDelayMs <= 0) return;

        await _logger.LogInfoAsync(ComponentName,
            $"[WhisperHost] GPU-инициализация: ожидание {_gpuStartupDelayMs} мс " +
            "для освобождения CUDA-контекста предыдущим процессом...")
            .ConfigureAwait(false);

        // Завершаем нативные финализаторы (Whisper/Vosk нативные дескрипторы GPU)
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

        await Task.Delay(_gpuStartupDelayMs, ct).ConfigureAwait(false);

        await _logger.LogInfoAsync(ComponentName,
            "[WhisperHost] GPU-инициализация: пауза завершена, попытка захвата CUDA...")
            .ConfigureAwait(false);
    }

    // ── Фильтр stderr: только строки с признаками ошибки ─────────────────────

    protected override bool ShouldLogStderr(string line)
    {
        // Строки из одних спецсимволов / Unicode-мусора (???, escape-последовательности)
        bool hasAsciiLetter = false;
        foreach (var c in line)
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) { hasAsciiLetter = true; break; }
        if (!hasAsciiLetter) return false;

        // Пропускаем CUDA/ggml информационные строки без ключевых слов ошибки
        foreach (var keyword in StderrErrorKeywords)
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
