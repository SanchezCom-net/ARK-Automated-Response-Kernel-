using ARK.VoskHost;

// Весь Main обёрнут в try-catch: любое необработанное исключение записывается
// в vosk_error.txt рядом с VoskHost.exe — даже если stderr не виден в ARK.
try
{
    return await VoskHostMain(args);
}
catch (Exception ex)
{
    var errorPath = Path.Combine(AppContext.BaseDirectory, "vosk_error.txt");
    try
    {
        await File.WriteAllTextAsync(errorPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] VoskHost.exe FATAL CRASH\n" +
            $"Args: {string.Join(" ", args)}\n\n" +
            ex.ToString());
    }
    catch { /* файловая система недоступна — игнорируем */ }

    Console.Error.WriteLine($"[VoskHost] FATAL: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine($"[VoskHost] Подробности → {errorPath}");
    Console.Error.Flush();
    return 99;
}

// ── Основная логика (вынесена в метод для охвата try-catch выше) ──────────────
static async Task<int> VoskHostMain(string[] args)
{
    // ── Разбор аргументов командной строки ────────────────────────────────────
    // Нормальный запуск: VoskHost.exe --pipe-id <arkPid> --model-path <path> [--language <lang>] [--debug]
    // Диагностика:       VoskHost.exe --dry-run --pipe-id 0 --model-path <path> [--language <lang>]

    string? pipeId    = null;
    string? modelPath = null;
    string  language  = "ru";
    bool    dryRun    = false;
    bool    debug     = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pipe-id"    when i + 1 < args.Length: pipeId    = args[++i]; break;
            case "--model-path" when i + 1 < args.Length: modelPath = args[++i]; break;
            case "--language"   when i + 1 < args.Length: language  = args[++i]; break;
            case "--dry-run"   : dryRun = true; break;
            case "--debug"     : debug  = true; break;
        }
    }

    // --debug: первый сигнал в stdout — ARK видит его через OnVoskHostStdout
    if (debug)
        Console.WriteLine("[VoskHost] Status: Initializing...");

    if (modelPath is null || (!dryRun && pipeId is null))
    {
        Console.Error.WriteLine(
            "VoskHost.exe — вспомогательный процесс ARK (Automated Response Kernel).\n" +
            "Нормальный запуск: VoskHost.exe --pipe-id <id> --model-path <path> [--language <lang>] [--debug]\n" +
            "Диагностика:       VoskHost.exe --dry-run --pipe-id 0 --model-path <path> [--language <lang>]\n" +
            "Запускается автоматически из ExternalVoskService — не запускайте вручную.");
        return 1;
    }

    // ── Проверка файловой системы ──────────────────────────────────────────────
    Console.Error.WriteLine($"[VoskHost] WorkDir: {AppContext.BaseDirectory}");
    Console.Error.WriteLine($"[VoskHost] Model  : {modelPath}");
    Console.Error.WriteLine($"[VoskHost] Lang   : {language}");
    Console.Error.WriteLine($"[VoskHost] DryRun : {dryRun}");
    Console.Error.WriteLine($"[VoskHost] Debug  : {debug}");

    if (!Directory.Exists(modelPath))
    {
        Console.Error.WriteLine($"[VoskHost] CRITICAL: директория модели не найдена: '{modelPath}'.");
        Console.Error.WriteLine("[VoskHost] Скачайте модель: https://alphacephei.com/vosk/models");
        return 2;
    }

    var confDir = Path.Combine(modelPath, "conf");
    var amDir   = Path.Combine(modelPath, "am");
    if (!Directory.Exists(confDir) || !Directory.Exists(amDir))
    {
        Console.Error.WriteLine($"[VoskHost] CRITICAL: неполная модель Vosk в '{modelPath}'.");
        Console.Error.WriteLine($"[VoskHost] Отсутствуют: " +
            $"{(Directory.Exists(confDir) ? "" : "conf/ ")}{(Directory.Exists(amDir) ? "" : "am/")}");
        Console.Error.WriteLine("[VoskHost] Распакуйте полный архив модели.");
        return 2;
    }

    // ── AppDomain: перехватываем неуправляемые CLR-исключения (0xE0434352) ─────
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        var msg = $"[VoskHost] UNHANDLED EXCEPTION: {e.ExceptionObject}";
        Console.Error.WriteLine(msg);
        Console.Error.Flush();
        // Дублируем в vosk_error.txt — stderr мог не успеть дойти до ARK
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "vosk_error.txt"), msg + "\n"); }
        catch { }
    };

    // ── Режим Dry Run ─────────────────────────────────────────────────────────
    if (dryRun)
    {
        Console.Error.WriteLine("[VoskHost] === DRY RUN MODE: диагностика загрузки модели ===");
        try
        {
            Vosk.Vosk.SetLogLevel(0);
            Console.Error.WriteLine("[VoskHost] Вызов Vosk.Model(modelPath)...");
            var model = new Vosk.Model(modelPath);
            Console.Error.WriteLine("[VoskHost] Model создан. Создаём VoskRecognizer...");
            var recog = new Vosk.VoskRecognizer(model, 16_000f);
            Console.Error.WriteLine("[VoskHost] === DRY RUN SUCCESS: модель загружена ===");
            recog.Dispose();
            model.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VoskHost] === DRY RUN FAILED: {ex.GetType().Name} ===");
            Console.Error.WriteLine($"[VoskHost] {ex}");
            return 4;
        }
    }

    // ── Лог имён pipe-каналов: сверяем с ARK.UI (должны совпадать) ───────────
    // ARK создаёт: NamedPipeServerStream("ark-vosk-audio-{ARK.PID}", ...)
    //              NamedPipeServerStream("ark-vosk-ctrl-{ARK.PID}", ...)
    // VoskHost подключается с тем же PipeId, полученным через --pipe-id.
    Console.Error.WriteLine($"[VoskHost] Pipe names (должны совпадать с ARK.UI):");
    Console.Error.WriteLine($"[VoskHost]   audio: ark-vosk-audio-{pipeId}");
    Console.Error.WriteLine($"[VoskHost]   ctrl : ark-vosk-ctrl-{pipeId}");

    // ── CancellationToken ─────────────────────────────────────────────────────
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // ── Подключение к Named Pipe каналам ARK.UI ───────────────────────────────
    if (debug)
        Console.WriteLine("[VoskHost] Status: ConnectingPipe...");

    Console.Error.WriteLine($"[VoskHost] Подключение к pipe. PipeId={pipeId}.");

    using var transport = new PipeTransport(pipeId!);
    try
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        await transport.ConnectAsync(connectCts.Token);
        Console.Error.WriteLine("[VoskHost] Pipe соединение установлено.");
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("[VoskHost] Таймаут подключения к pipe (15 сек). ARK.UI не создал сервер?");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[VoskHost] Ошибка подключения к pipe: {ex.GetType().Name}: {ex.Message}");
        return 3;
    }

    // ── Инициализация Vosk и основной цикл ───────────────────────────────────
    if (debug)
        Console.WriteLine("[VoskHost] Status: LoadingModel...");

    using var processor = new VoskProcessor(transport, modelPath, language);
    try
    {
        processor.Initialize();

        if (debug)
            Console.WriteLine("[VoskHost] Status: Ready");

        await processor.RunLoopAsync(cts.Token);
        Console.Error.WriteLine("[VoskHost] Штатное завершение.");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("[VoskHost] Отменён (CancellationToken).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[VoskHost] Критическая ошибка: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine($"[VoskHost] StackTrace: {ex.StackTrace}");
        return 4;
    }
}
