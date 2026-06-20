using System.Text;
using System.Text.Json;
using ARK.Voice.Worker;

// Глобальный перехватчик: любой нативный краш GGML пишется в файл рядом с .exe
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WhisperHost FATAL: {e.ExceptionObject}";
    Console.Error.WriteLine(msg);
    Console.Error.Flush();
    try
    {
        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "whisper_error.txt"),
            msg + Environment.NewLine);
    }
    catch { }
};

try
{
    return await WhisperHostMain(args);
}
catch (Exception ex)
{
    var errorPath = Path.Combine(AppContext.BaseDirectory, "whisper_error.txt");
    try
    {
        await File.WriteAllTextAsync(errorPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WhisperHost.exe CRASH\n" +
            $"Args: {string.Join(" ", args)}\n\n{ex}");
    }
    catch { }

    Console.Error.WriteLine($"[WhisperHost] FATAL: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine($"[WhisperHost] Подробности → {errorPath}");
    Console.Error.Flush();
    return 99;
}

// ── Основная логика ────────────────────────────────────────────────────────────
static async Task<int> WhisperHostMain(string[] args)
{
    // Аргументы:
    //   --pipe-id    <id>    Идентификатор Named Pipe (создаётся ARK.UI)
    //   --config-b64 <b64>  Base64(UTF-8(JSON)) → WhisperWorkerConfig
    //   --dry-run            Только проверка конфига и загрузки модели; без pipe
    //   --debug              Расширенный вывод в stderr

    string? pipeId = null;
    WhisperWorkerConfig? config = null;
    bool dryRun = false;
    bool debug  = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pipe-id" when i + 1 < args.Length:
                pipeId = args[++i];
                break;

            case "--config-b64" when i + 1 < args.Length:
                try
                {
                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(args[++i]));
                    config = JsonSerializer.Deserialize<WhisperWorkerConfig>(json);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"[WhisperHost] Ошибка декодирования --config-b64: {ex.Message}");
                    return 1;
                }
                break;

            case "--dry-run": dryRun = true; break;
            case "--debug":   debug  = true; break;
        }
    }

    // ── Валидация аргументов ──────────────────────────────────────────────────

    if (config is null)
    {
        await Console.Error.WriteLineAsync(
            "WhisperHost.exe — вспомогательный процесс ARK (Automated Response Kernel).\n" +
            "Запуск: WhisperHost.exe --pipe-id <id> --config-b64 <base64json> [--debug]\n" +
            "Диагностика: WhisperHost.exe --dry-run --config-b64 <base64json> [--debug]\n" +
            "Запускается автоматически из ExternalWhisperService. Не запускайте вручную.");
        return 1;
    }

    if (string.IsNullOrEmpty(config.ModelPath))
    {
        await Console.Error.WriteLineAsync(
            "[WhisperHost] Ошибка: поле model_path в конфиге пустое.");
        return 3;
    }

    if (!dryRun && string.IsNullOrEmpty(pipeId))
    {
        await Console.Error.WriteLineAsync(
            "[WhisperHost] Ошибка: --pipe-id обязателен вне режима dry-run.");
        return 4;
    }

    if (debug)
    {
        Console.Error.WriteLine(
            $"[WhisperHost] WorkDir   : {AppContext.BaseDirectory}\n" +
            $"[WhisperHost] ModelPath : {config.ModelPath}\n" +
            $"[WhisperHost] ModelType : {config.ModelType}\n" +
            $"[WhisperHost] Precision : {config.Precision}\n" +
            $"[WhisperHost] Language  : {config.Language}\n" +
            $"[WhisperHost] UseGpu    : {config.UseGpu} (device {config.GpuDevice})\n" +
            $"[WhisperHost] DryRun    : {dryRun}");
    }

    if (!File.Exists(config.ModelPath))
    {
        Console.Error.WriteLine(
            $"[WhisperHost] CRITICAL: файл модели не найден: '{config.ModelPath}'.");
        Console.Error.WriteLine(
            "[WhisperHost] Скачайте .bin модель: https://huggingface.co/ggerganov/whisper.cpp");
        return 2;
    }

    // ── Dry-run ───────────────────────────────────────────────────────────────

    if (dryRun)
    {
        Console.Error.WriteLine("[WhisperHost] === DRY RUN: диагностика загрузки модели ===");
        try
        {
            await using var proc = new WhisperPipeProcessor(null, config);
            using var dryCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await proc.InitializeAsync(dryCts.Token);
            Console.Error.WriteLine("[WhisperHost] === DRY RUN SUCCESS: модель загружена ===");
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("[CRITICAL]", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[WhisperHost] === DRY RUN HALT: {ex.Message} ===");
            return 10;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WhisperHost] === DRY RUN FAILED: {ex.GetType().Name} ===");
            Console.Error.WriteLine($"[WhisperHost] {ex}");
            return 4;
        }
    }

    // ── Named Pipe connection ─────────────────────────────────────────────────

    Console.Error.WriteLine($"[WhisperHost] Pipe audio: {PipeTransport.AudioPipePrefix}{pipeId}");
    Console.Error.WriteLine($"[WhisperHost] Pipe ctrl : {PipeTransport.CtrlPipePrefix}{pipeId}");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress    += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    using var transport = new PipeTransport(pipeId!);
    try
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        await transport.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        Console.Error.WriteLine("[WhisperHost] Pipe соединение установлено.");
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("[WhisperHost] Таймаут подключения к pipe (15 сек). ARK.UI не создал сервер?");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WhisperHost] Ошибка подключения к pipe: {ex.GetType().Name}: {ex.Message}");
        return 3;
    }

    // ── Инициализация и основной цикл ─────────────────────────────────────────

    if (debug) Console.Error.WriteLine("[WhisperHost] Status: LoadingModel...");

    await using var processor = new WhisperPipeProcessor(transport, config);
    try
    {
        await processor.InitializeAsync(cts.Token).ConfigureAwait(false);

        transport.WriteStatus("ready");
        if (debug) Console.Error.WriteLine("[WhisperHost] Status: Ready — вхожу в цикл чтения.");

        await processor.RunLoopAsync(cts.Token).ConfigureAwait(false);
        Console.Error.WriteLine("[WhisperHost] Штатное завершение.");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("[WhisperHost] Отменён (CancellationToken).");
        return 0;
    }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("[CRITICAL]", StringComparison.Ordinal))
    {
        // WriteHalt уже отправлен внутри InitializeAsync; ARK.UI переключится на Vosk
        Console.Error.WriteLine($"[WhisperHost] HALT: {ex.Message}");
        return 10;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WhisperHost] Критическая ошибка: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine($"[WhisperHost] StackTrace: {ex.StackTrace}");
        try { transport.WriteLog("error", $"[WhisperHost] Крэш: {ex.GetType().Name}: {ex.Message}"); }
        catch { }
        return 4;
    }
}
