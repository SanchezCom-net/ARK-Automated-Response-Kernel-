using System.IO;
using System.Text;
using Whisper.net;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;

namespace ARK.UI.Core.Services;

public sealed class WhisperModelWrapper : IModelWrapper
{
    private readonly ILogService    _logger;
    private readonly IConfigService _configService;
    private WhisperFactory?         _factory;
    private WhisperProcessor?       _processor;
    private bool                    _disposed;

    public ModelType Type    => ModelType.Whisper;
    public bool      IsReady => _processor is not null && !_disposed;

    public WhisperModelWrapper(ILogService logger, IConfigService configService)
    {
        _logger        = logger;
        _configService = configService;
    }

    public async Task InitializeAsync(string modelPath, string language, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
        {
            await _logger.LogWarningAsync(nameof(WhisperModelWrapper),
                $"[Whisper] Модель не найдена: '{modelPath}'. Поместите .bin-файл в Models/Whisper/.")
                .ConfigureAwait(false);
            return;
        }

        bool gpuRequested = _configService.Current.UseGpuAcceleration;
        bool gpuUsed      = false;

        await _logger.LogInfoAsync(nameof(WhisperModelWrapper),
            $"[Whisper] Загрузка: {Path.GetFileName(modelPath)}. " +
            $"Язык: {language}. GPU: {(gpuRequested ? "запрошен (CUDA)" : "отключён (CPU)")}...")
            .ConfigureAwait(false);

        try
        {
            await Task.Run(async () =>
            {
                if (gpuRequested)
                {
                    try
                    {
                        _factory = WhisperFactory.FromPath(modelPath,
                            new WhisperFactoryOptions
                            {
                                UseGpu    = true,
                                GpuDevice = _configService.Current.GpuDeviceIndex
                            });
                        _processor = _factory.CreateBuilder().WithLanguage(language).Build();
                        gpuUsed = true;
                    }
                    catch (Exception gpuEx)
                    {
                        await LogGpuFailureAsync(gpuEx).ConfigureAwait(false);
                        _factory?.Dispose();
                        _factory = null;
                    }
                }

                if (!gpuUsed)
                {
                    _factory   = WhisperFactory.FromPath(modelPath,
                        new WhisperFactoryOptions { UseGpu = false });
                    _processor = _factory.CreateBuilder().WithLanguage(language).Build();
                }
            }, ct).ConfigureAwait(false);

            var backend = gpuUsed      ? "CUDA GPU"
                        : gpuRequested ? "CPU (автофолбэк — CUDA недоступна)"
                                       : "CPU (задан настройками)";
            await _logger.LogInfoAsync(nameof(WhisperModelWrapper),
                $"[Whisper] Готова. Файл: {Path.GetFileName(modelPath)}. Бэкенд: {backend}.")
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            await _logger.LogErrorAsync(nameof(WhisperModelWrapper),
                "[Whisper] Нативная whisper.dll не найдена. " +
                "Пересоберите проект — CopyWhisperNatives скопирует DLL рядом с exe.", ex)
                .ConfigureAwait(false);
        }
        catch (DllNotFoundException ex)
        {
            await LogDllNotFoundAsync(ex).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var probe = CudaDiagnostics.LastResult;
            await _logger.LogErrorAsync(nameof(WhisperModelWrapper),
                $"[Whisper] Ошибка загрузки модели: {ex.Message} " +
                BuildCudaContext(probe), ex).ConfigureAwait(false);
        }
    }

    public async Task<string> RecognizeAsync(Stream audioWav, CancellationToken ct = default)
    {
        if (_processor is null || _disposed) return string.Empty;

        var sb = new StringBuilder();
        await foreach (var seg in _processor.ProcessAsync(audioWav).ConfigureAwait(false))
            sb.Append(seg.Text);
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }

        _factory?.Dispose();
        _factory = null;
    }

    // ── Диагностические логи ──────────────────────────────────────────────────

    private async Task LogGpuFailureAsync(Exception gpuEx)
    {
        var probe    = CudaDiagnostics.LastResult;
        var ctx      = BuildCudaContext(probe);
        var isDllErr = IsDllError(gpuEx);

        if (isDllErr)
        {
            await _logger.LogErrorAsync(nameof(WhisperModelWrapper),
                $"[Whisper] ОШИБКА: Библиотека Whisper.net не нашла подходящую DLL для CUDA. " +
                $"{ctx} " +
                "Убедитесь, что в проекте установлены пакеты 'Whisper.net.Runtime.Cuda' и " +
                "'Whisper.net.Runtime.Cuda12' одновременно для обеспечения Fallback. " +
                "Fallback → CPU.")
                .ConfigureAwait(false);
        }
        else
        {
            await _logger.LogWarningAsync(nameof(WhisperModelWrapper),
                $"[Whisper] GPU init упал ({gpuEx.GetType().Name}: {gpuEx.Message}). " +
                $"{ctx} Fallback → CPU.")
                .ConfigureAwait(false);
        }
    }

    private async Task LogDllNotFoundAsync(DllNotFoundException ex)
    {
        var probe = CudaDiagnostics.LastResult;
        var ctx   = BuildCudaContext(probe);

        await _logger.LogErrorAsync(nameof(WhisperModelWrapper),
            $"[Whisper] ОШИБКА: Библиотека Whisper.net не нашла подходящую DLL. " +
            $"{ctx} " +
            "Убедитесь, что в проекте установлены пакеты 'Whisper.net.Runtime.Cuda' и " +
            "'Whisper.net.Runtime.Cuda12' одновременно для обеспечения Fallback.", ex)
            .ConfigureAwait(false);
    }

    // Форматирует строку с версиями CUDA для вставки в лог-сообщения.
    private static string BuildCudaContext(CudaProbeResult? probe)
    {
        if (probe is null) return string.Empty;

        var sysVer  = probe.SystemCudaVersion;
        var reqVer  = probe.BundledCudaVersion is not null
            ? $"CUDA {probe.BundledCudaVersion}.x"
            : "CUDA 12.x";

        return $"Анализ: система CUDA {sysVer} vs требуемая {reqVer}.";
    }

    private static bool IsDllError(Exception ex) =>
        ex is DllNotFoundException ||
        ex.GetType().Name.Contains("DllNotFoundException", StringComparison.Ordinal) ||
        ex.Message.Contains("DllNotFound",   StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Incompatible",  StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("cannot load",   StringComparison.OrdinalIgnoreCase);
}
