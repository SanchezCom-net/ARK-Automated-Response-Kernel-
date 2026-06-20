using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Whisper.net;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Services;

/// <summary>
/// Самодиагностика GPU/CUDA-инфраструктуры: версия драйвера, CUDA в системе,
/// CUDA-рантайм в пакете Whisper.net, совместимость версий.
/// Запускается однократно при старте (StartupOrchestrator фаза GPU).
/// </summary>
public static partial class CudaDiagnostics
{
    private const string Component = "CudaDiagnostics";

    private static volatile bool    _done;
    private static CudaProbeResult? _cache;

    /// <summary>Результат последней диагностики (null — ещё не запускалась).</summary>
    public static CudaProbeResult? LastResult => _cache;

    // ── Публичный API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Запускает полный аудит GPU. Повторные вызовы используют кэш.
    /// </summary>
    public static async Task CheckCompatibilityAsync(
        ILogService logger, CancellationToken ct = default)
    {
        if (_done) return;

        CudaProbeResult result;
        try
        {
            result = await ProbeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            result = new CudaProbeResult
            {
                GpuFound          = false,
                WhisperNetVersion = GetWhisperNetVersion(),
                ErrorMessage      = ex.Message
            };
        }

        _cache = result;
        _done  = true;

        await LogResultAsync(logger, result, ct).ConfigureAwait(false);
    }

    // ── Зондирование (сбор сырых данных) ─────────────────────────────────────

    private static async Task<CudaProbeResult> ProbeAsync(CancellationToken ct)
    {
        // Поиск CUDA runtime DLL вынесен в Task.Run: сканирует PATH + NVIDIA Toolkit + System32
        var locator    = await Task.Run(() => LocateCudaRuntime(), ct).ConfigureAwait(false);
        var whisperVer = GetWhisperNetVersion();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName               = "nvidia-smi",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            proc.Start();

            // CLAUDE.md: читаем stdout+stderr параллельно ПЕРЕД WaitForExitAsync
            var readOut = proc.StandardOutput.ReadToEndAsync(linked.Token);
            var readErr = proc.StandardError.ReadToEndAsync(linked.Token);
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            var output   = readOut.Result;
            var errorOut = readErr.Result;

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return new CudaProbeResult
                {
                    GpuFound           = false,
                    BundledCudaVersion = locator.Version,
                    CudaRuntimeDllPath = locator.DllPath,
                    SearchedPaths      = locator.SearchedPaths,
                    WhisperNetVersion  = whisperVer,
                    ErrorMessage       = string.IsNullOrWhiteSpace(errorOut)
                        ? "nvidia-smi завершился с ненулевым кодом"
                        : errorOut.Trim()
                };
            }

            var driverMatch = DriverVersionRegex().Match(output);
            var cudaMatch   = CudaVersionRegex().Match(output);

            return new CudaProbeResult
            {
                GpuFound           = true,
                DriverVersion      = driverMatch.Success ? driverMatch.Groups[1].Value : "N/A",
                SystemCudaVersion  = cudaMatch.Success   ? cudaMatch.Groups[1].Value   : "N/A",
                BundledCudaVersion = locator.Version,
                CudaRuntimeDllPath = locator.DllPath,
                SearchedPaths      = locator.SearchedPaths,
                WhisperNetVersion  = whisperVer
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // nvidia-smi не найдена — нет NVIDIA GPU/драйверов, это нормально
            return new CudaProbeResult
            {
                GpuFound           = false,
                BundledCudaVersion = locator.Version,
                CudaRuntimeDllPath = locator.DllPath,
                SearchedPaths      = locator.SearchedPaths,
                WhisperNetVersion  = whisperVer,
                ErrorMessage       = ex.Message
            };
        }
    }

    // ── Логирование и анализ совместимости ────────────────────────────────────

    private static async Task LogResultAsync(
        ILogService logger, CudaProbeResult result, CancellationToken ct)
    {
        if (!result.GpuFound)
        {
            await logger.LogInfoAsync(Component,
                "[GPU] NVIDIA GPU не обнаружен (nvidia-smi недоступна). " +
                "Речевой движок будет использовать Whisper (CPU) или Vosk. " +
                $"Причина: {result.ErrorMessage ?? "нет NVIDIA-драйвера"}.")
                .ConfigureAwait(false);

            // GPU нет, но CUDA toolkit мог быть установлен — сообщить если DLL нашлась
            if (result.CudaRuntimeDllPath is not null)
            {
                await logger.LogInfoAsync(Component,
                    $"[GPU] Замечание: CUDA runtime найден в '{result.CudaRuntimeDllPath}', " +
                    "но NVIDIA GPU/драйвер не установлен. GPU-инференс недоступен.")
                    .ConfigureAwait(false);
            }
            return;
        }

        // Общий итог диагностики
        var dllInfo = result.CudaRuntimeDllPath is not null
            ? $"найден: {result.CudaRuntimeDllPath}"
            : "не найден";

        await logger.LogInfoAsync(Component,
            $"[GPU] Диагностика завершена. " +
            $"Драйвер: {result.DriverVersion} | " +
            $"CUDA системная: {result.SystemCudaVersion} | " +
            $"cudart64_*.dll: {dllInfo} | " +
            $"Whisper.net: {result.WhisperNetVersion}.")
            .ConfigureAwait(false);

        // CUDA runtime не найден ни в одном из путей — критическая инструкция
        if (result.BundledCudaVersion is null)
        {
            var pathCount   = result.SearchedPaths.Length;
            var pathPreview = pathCount > 0
                ? string.Join("; ",
                    result.SearchedPaths.Take(4).Select(p =>
                        p.Length > 65 ? "…" + p[^62..] : p))
                : "AppDir + PATH + NVIDIA Toolkit";

            await logger.LogErrorAsync(Component,
                $"[GPU] CUDA runtime (cudart64_*.dll) не найден. " +
                $"Проверено {pathCount} директорий: [{pathPreview}]. " +
                "Инструкция по устранению: " +
                "1) Установите CUDA Toolkit 12.x → https://developer.nvidia.com/cuda-downloads; " +
                $"2) Скопируйте cudart64_12.dll в папку ARK: {AppContext.BaseDirectory}; " +
                "3) Убедитесь что NuGet-пакет 'Whisper.net.Runtime.Cuda12.Windows' установлен " +
                "и выполнена сборка проекта (DLL копируется в output автоматически).")
                .ConfigureAwait(false);
            return;
        }

        // Сравниваем мажорные версии для предупреждения о несовместимости
        if (!int.TryParse(result.BundledCudaVersion, out var bundledMajor)) return;

        var sysParts = result.SystemCudaVersion.Split('.');
        if (!int.TryParse(sysParts[0], out var sysMajor)) return;

        if (sysMajor > bundledMajor)
        {
            await logger.LogWarningAsync(Component,
                $"[GPU] Несоответствие CUDA: система {result.SystemCudaVersion} > пакет {bundledMajor}.x. " +
                $"Обновите NuGet: 'Whisper.net.Runtime.Cuda' и 'Whisper.net.Runtime.Cuda{sysMajor}' " +
                $"до версии с поддержкой CUDA {result.SystemCudaVersion}.")
                .ConfigureAwait(false);
        }
        else if (sysMajor < bundledMajor)
        {
            await logger.LogWarningAsync(Component,
                $"[GPU] CUDA системы ({result.SystemCudaVersion}) ниже требуемой пакетом ({bundledMajor}.x). " +
                "Обновите драйверы NVIDIA с nvidia.com или переключитесь на Vosk (CPU).")
                .ConfigureAwait(false);
        }
        else
        {
            await logger.LogInfoAsync(Component,
                $"[GPU] CUDA совместима: система {result.SystemCudaVersion}, " +
                $"пакет {bundledMajor}.x. Whisper (GPU) готов к запуску.")
                .ConfigureAwait(false);
        }
    }

    // ── Поиск CUDA runtime DLL ────────────────────────────────────────────────

    private static CudaRuntimeLocator LocateCudaRuntime()
    {
        var searched = new List<string>();

        foreach (var dir in GetCandidateDirectories())
        {
            searched.Add(dir);
            try
            {
                foreach (var f in Directory.GetFiles(
                    dir, "cudart64_*.dll", SearchOption.TopDirectoryOnly))
                {
                    var ver = ParseCudartVersion(f);
                    if (ver is not null)
                        return new CudaRuntimeLocator(ver, f, [.. searched]);
                }
            }
            catch { /* нет доступа к директории — пропускаем */ }
        }

        return new CudaRuntimeLocator(null, null, [.. searched]);
    }

    /// <summary>
    /// Возвращает директории-кандидаты в порядке убывания приоритета:
    ///   1. Рядом с exe (Whisper.net.Runtime.Cuda копирует DLL при сборке).
    ///   2. Переменная PATH.
    ///   3. Стандартная установка NVIDIA CUDA Toolkit (Program Files).
    ///   4. System32 / SysWOW64 (legacy CUDA, bundled с драйвером).
    /// </summary>
    private static IEnumerable<string> GetCandidateDirectories()
    {
        // 1. Папка приложения
        yield return AppContext.BaseDirectory;

        // 2. Пути из переменной PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathVar.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(segment))
                yield return segment;
        }

        // 3. NVIDIA CUDA Toolkit — стандартные пути установщика
        var nvidiaBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA GPU Computing Toolkit", "CUDA");

        if (Directory.Exists(nvidiaBase))
        {
            // Сортируем по убыванию версии: v12.x перед v11.x
            foreach (var vDir in Directory.GetDirectories(nvidiaBase)
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var bin = Path.Combine(vDir, "bin");
                if (Directory.Exists(bin))
                    yield return bin;
            }
        }

        // 4. System32 / SysWOW64 (CUDA может быть установлена вместе с драйвером)
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
    }

    /// <summary>
    /// Извлекает мажорную версию CUDA из имени файла cudart64_*.dll.
    ///   cudart64_12.dll  → "12"  (формат CUDA ≥ 12)
    ///   cudart64_110.dll → "11"  (формат CUDA 11, legacy)
    /// </summary>
    private static string? ParseCudartVersion(string filePath)
    {
        var stem  = Path.GetFileNameWithoutExtension(filePath);
        var parts = stem.Split('_');
        if (parts.Length != 2) return null;

        var tag = parts[1];

        if (tag.Length <= 2 && int.TryParse(tag, out var v) && v > 0)
            return v.ToString();

        if (tag.Length == 3 && int.TryParse(tag[..2], out var v2) && v2 > 0)
            return v2.ToString();

        return null;
    }

    // ── Вспомогательные методы ────────────────────────────────────────────────

    private static string GetWhisperNetVersion()
    {
        try
        {
            return typeof(WhisperFactory).Assembly
                .GetName().Version?.ToString(3) ?? "unknown";
        }
        catch { return "unknown"; }
    }

    // ── Source-generated регулярные выражения ─────────────────────────────────

    // Извлекает версию драйвера: "Driver Version: 560.70"
    [GeneratedRegex(@"Driver Version:\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DriverVersionRegex();

    // Извлекает системную CUDA: "CUDA Version: 12.6"
    [GeneratedRegex(@"CUDA Version:\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CudaVersionRegex();

    // ── Внутренние типы ───────────────────────────────────────────────────────

    private sealed record CudaRuntimeLocator(
        string?  Version,
        string?  DllPath,
        string[] SearchedPaths);
}

// ── Публичные типы данных ─────────────────────────────────────────────────────

/// <summary>Результат полного GPU-зондирования (immutable record).</summary>
public sealed record CudaProbeResult
{
    public bool     GpuFound           { get; init; }
    public string   DriverVersion      { get; init; } = "N/A";
    public string   SystemCudaVersion  { get; init; } = "N/A";
    /// <summary>Мажорная версия CUDA ("12", "11") из найденной cudart64_*.dll. Null — DLL не найдена.</summary>
    public string?  BundledCudaVersion { get; init; }
    /// <summary>Полный путь к найденной cudart64_*.dll. Null — не найдена ни в одной директории.</summary>
    public string?  CudaRuntimeDllPath { get; init; }
    /// <summary>Все директории, проверенные при поиске cudart64_*.dll (AppDir + PATH + NVIDIA + System32).</summary>
    public string[] SearchedPaths      { get; init; } = [];
    public string   WhisperNetVersion  { get; init; } = "unknown";
    public string?  ErrorMessage       { get; init; }
}

