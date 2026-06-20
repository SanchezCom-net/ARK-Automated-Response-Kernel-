using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

public sealed class HardwareAcceleratorService : IHardwareAccelerator
{
    private const string Component = "HardwareAccelerator";

    private readonly ILogService _logger;

    private volatile bool _cudaAvailable;
    private volatile bool _directMlAvailable;
    private volatile bool _rocmAvailable;
    private string?       _primaryGpuName;

    // Детальные результаты последнего зондирования — читаются только после RefreshAsync
    private ProbeDetail _cudaDetail;
    private ProbeDetail _dmlDetail;
    private ProbeDetail _rocmDetail;

    public bool    IsGpuAccelerationAvailable => _cudaAvailable || _directMlAvailable || _rocmAvailable;
    public bool    IsCudaAvailable            => _cudaAvailable;
    public bool    IsDirectMlAvailable        => _directMlAvailable;
    public bool    IsRocmAvailable            => _rocmAvailable;
    public string? PrimaryGpuName             => _primaryGpuName;

    public HardwareAcceleratorService(ILogService logger)
    {
        _logger = logger;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Зондирование DLL — синхронные операции файловой системы и нативной загрузки → Task.Run
        await Task.Run(DoRefresh, ct).ConfigureAwait(false);
        await LogDiagnosticsAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> WaitForCudaAsync(
        int maxAttempts, int delayMilliseconds, CancellationToken ct = default)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            await RefreshAsync(ct).ConfigureAwait(false);

            // Fast path: CUDA уже готова — возвращаем немедленно без лишних задержек
            if (IsCudaAvailable)
                return true;

            // Промежуточная попытка не последняя — ждём и повторяем
            if (attempt < maxAttempts)
            {
                await _logger.LogInfoAsync(Component,
                    $"[GPU PROBE] Попытка {attempt}/{maxAttempts}: CUDA ещё не готова. " +
                    $"Следующая проверка через {delayMilliseconds} мс...")
                    .ConfigureAwait(false);

                await Task.Delay(delayMilliseconds, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    // ── Синхронное зондирование (внутри Task.Run) ─────────────────────────────

    private void DoRefresh()
    {
        // Whisper.net 1.9.1: GPU-библиотека называется ggml-cuda-whisper.dll (не ggml-cuda.dll!)
        // Это критически важно — старое имя ggml-cuda.dll не существует в пакете.
        _cudaDetail = ProbeNativeLib("ggml-cuda-whisper.dll");
        _dmlDetail  = ProbeNativeLib("DirectML.dll");
        _rocmDetail = ProbeNativeLib("amdhip64.dll");

        _cudaAvailable     = _cudaDetail.Success;
        _directMlAvailable = _dmlDetail.Success;
        _rocmAvailable     = _rocmDetail.Success;

        _primaryGpuName = TryGetGpuName();
    }

    /// <summary>
    /// Пробует загрузить нативную DLL из папки приложения.
    /// Возвращает детальный результат: успех или конкретная причина неудачи.
    /// </summary>
    private static ProbeDetail ProbeNativeLib(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);

        // Шаг 1: проверяем существование файла
        if (!File.Exists(path))
            return new ProbeDetail(false, path,
                $"файл отсутствует в папке приложения: '{path}'");

        // Шаг 2: пробуем загрузить нативную библиотеку
        try
        {
            if (!NativeLibrary.TryLoad(path, out var handle))
                return new ProbeDetail(false, path,
                    $"NativeLibrary.TryLoad вернул false — возможно, отсутствуют транзитивные зависимости DLL " +
                    $"(cudart64_*.dll, cuDNN, nccl и т.п.). Файл существует: '{path}'.");

            NativeLibrary.Free(handle);
            return new ProbeDetail(true, path, null);
        }
        catch (Exception ex)
        {
            // Типичные ошибки: DllNotFoundException (зависимость), BadImageFormatException (wrong arch)
            return new ProbeDetail(false, path,
                $"{ex.GetType().Name}: {ex.Message} " +
                $"(файл: '{path}', StackTrace: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()})");
        }
    }

    // ── Асинхронное логирование результатов ──────────────────────────────────

    private async Task LogDiagnosticsAsync(CancellationToken ct)
    {
        // Дополнительная информационная проверка ONNX CUDA (для KokoroSharp GPU; Whisper её не требует)
        var onnxCudaDetail = ProbeNativeLib("onnxruntime_providers_cuda.dll");

        // Одна строка — общий итог
        await _logger.LogInfoAsync(Component,
            $"[GPU Probe] CUDA(ggml-cuda-whisper.dll): {Format(_cudaDetail)} | " +
            $"DirectML: {Format(_dmlDetail)} | " +
            $"ROCm(amdhip64.dll): {Format(_rocmDetail)} | " +
            $"OnnxCUDA: {Format(onnxCudaDetail)} | " +
            $"GPU: {_primaryGpuName ?? "не определён"} | " +
            $"IsGpuAvailable={IsGpuAccelerationAvailable}.")
            .ConfigureAwait(false);

        // Если GPU недоступен — пишем подробную причину по каждой библиотеке
        if (!IsGpuAccelerationAvailable)
        {
            await _logger.LogWarningAsync(Component,
                "[GPU Probe] Ни один GPU-бэкенд не загружен. " +
                "Причины по каждой библиотеке:")
                .ConfigureAwait(false);

            foreach (var (label, detail) in new[]
            {
                ("CUDA  (ggml-cuda-whisper.dll)     ", _cudaDetail),
                ("DirectML (DirectML.dll)           ", _dmlDetail),
                ("ROCm  (amdhip64.dll)              ", _rocmDetail),
                ("ONNX CUDA (providers_cuda.dll)    ", onnxCudaDetail)
            })
            {
                if (!detail.Success)
                    await _logger.LogWarningAsync(Component,
                        $"[GPU Probe]   {label}: {detail.FailReason ?? "причина неизвестна"}")
                        .ConfigureAwait(false);
            }
        }
    }

    private static string Format(ProbeDetail d) => d.Success ? "OK" : "FAIL";

    // ── Имя GPU через nvidia-smi ──────────────────────────────────────────────

    private static string? TryGetGpuName()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName               = "nvidia-smi",
                Arguments              = "--query-gpu=name --format=csv,noheader,nounits",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            });
            if (proc is null) return null;
            var name = proc.StandardOutput.ReadLine()?.Trim();
            proc.WaitForExit(2000);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    // ── Внутренние типы ───────────────────────────────────────────────────────

    private readonly record struct ProbeDetail(
        bool    Success,
        string  DllPath,
        string? FailReason);
}
