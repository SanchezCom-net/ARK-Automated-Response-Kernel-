using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ARK.UI.Core.Services;

partial class BaseSpeechHostedService
{
    // ── Запуск хост-процесса ──────────────────────────────────────────────────

    private async Task StartHostProcessAsync(CancellationToken ct)
    {
        var exePath        = Path.Combine(AppContext.BaseDirectory, _settings.HostProcessName);
        var startupTimeout = TimeSpan.FromMilliseconds(_settings.StartupTimeoutMs);

        await KillOrphanedProcessesAsync().ConfigureAwait(false);

        NamedPipeServerStream? audioPipe = null;
        NamedPipeServerStream? ctrlPipe  = null;
        try
        {
            await _logger.LogInfoAsync(ComponentName,
                $"[Pipe] audio='\\\\.\\pipe\\{AudioPipeName}', ctrl='\\\\.\\pipe\\{CtrlPipeName}'. PipeId={PipeId}.")
                .ConfigureAwait(false);

            audioPipe = new NamedPipeServerStream(AudioPipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            ctrlPipe  = new NamedPipeServerStream(CtrlPipeName,  PipeDirection.In,  1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var args = BuildArguments(PipeId, _modelPath, _language);

            // Хук движка перед запуском (GPU-задержка, GC-принуждение, специфическая подготовка)
            await OnBeforeStartAsync(ct).ConfigureAwait(false);

            await _logger.LogInfoAsync(ComponentName,
                $"[HostProcess] Запуск: {exePath} {args}.")
                .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName               = exePath,
                Arguments              = args,
                WorkingDirectory       = AppContext.BaseDirectory,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
            };

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start вернул null для '{exePath}'.");

            _process = proc;
            lock (_stderrLock) _stderrBuffer.Clear();
            _process.ErrorDataReceived  += OnHostStderr;
            _process.OutputDataReceived += OnHostStdout;
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            await _logger.LogInfoAsync(ComponentName,
                $"[HostProcess] PID={_process.Id}. Пауза 2 сек → pipe (таймаут {startupTimeout.TotalSeconds} сек)...")
                .ConfigureAwait(false);

            await Task.Delay(2000, ct).ConfigureAwait(false);

            _process.Refresh();
            if (_process.HasExited)
            {
                await Task.Delay(200).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"{_settings.HostProcessName} завершился (ExitCode={_process.ExitCode}) в первые 2 сек. " +
                    BuildConnectionDiagnostics());
            }

            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connCts.CancelAfter(startupTimeout);
            try
            {
                await Task.WhenAll(
                    audioPipe.WaitForConnectionAsync(connCts.Token),
                    ctrlPipe .WaitForConnectionAsync(connCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await Task.Delay(200).ConfigureAwait(false);
                throw new TimeoutException(
                    $"{_settings.HostProcessName} не подключился за {startupTimeout.TotalSeconds} сек. " +
                    BuildConnectionDiagnostics());
            }

            _audioPipe = audioPipe; audioPipe = null;
            _ctrlPipe  = ctrlPipe;  ctrlPipe  = null;

            _ctrlReaderCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadCtrlPipeLoopAsync(_ctrlReaderCts.Token));

            _isReady = true;
            Interlocked.Exchange(ref _failureCount, 0);

            await _logger.LogInfoAsync(ComponentName,
                $"[HostProcess] Готов. Движок={_settings.EngineType}, " +
                $"модель={Path.GetFileName(_modelPath)}, язык={_language}. " +
                $"Watchdog: RAM={_settings.MaxMemoryMb} МБ, " +
                $"сессия={_settings.MaxSessionTimeMs / 60_000} мин, " +
                $"простой={_settings.IdleTimeoutMs / 1000} сек.")
                .ConfigureAwait(false);
        }
        finally
        {
            audioPipe?.Dispose();
            ctrlPipe ?.Dispose();
        }
    }

    // ── Graceful Shutdown + Kill ──────────────────────────────────────────────

    private async Task SendShutdownCommandAsync()
    {
        if (_audioPipe is null || !_audioPipe.IsConnected) return;
        try
        {
            await _audioPipe.WriteAsync(BitConverter.GetBytes(ShutdownMagic)).ConfigureAwait(false);
            await _audioPipe.FlushAsync().ConfigureAwait(false);
            await _logger.LogInfoAsync(ComponentName,
                "[HostProcess] SHUTDOWN отправлен в audio-pipe.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(ComponentName,
                $"[HostProcess] Не удалось отправить SHUTDOWN: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task KillProcessAsync()
    {
        if (_process is null) return;
        try
        {
            try { _process.CancelErrorRead();  } catch { }
            try { _process.CancelOutputRead(); } catch { }
            _process.ErrorDataReceived  -= OnHostStderr;
            _process.OutputDataReceived -= OnHostStdout;

            if (!_process.HasExited)
            {
                await SendShutdownCommandAsync().ConfigureAwait(false);

                bool graceful = false;
                try
                {
                    await _process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(GracefulShutdownWait).ConfigureAwait(false);
                    graceful = true;
                }
                catch (TimeoutException) { }

                if (!graceful)
                {
                    await _logger.LogWarningAsync(ComponentName,
                        $"[HostProcess] Graceful timeout — Kill.").ConfigureAwait(false);

                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        try
                        {
                            await _process.WaitForExitAsync(CancellationToken.None)
                                .WaitAsync(ForceKillWait).ConfigureAwait(false);
                        }
                        catch (TimeoutException) { }
                    }
                }
            }
        }
        catch { }
        finally { _process?.Dispose(); _process = null; }
    }

    // ── stderr / stdout ────────────────────────────────────────────────────────

    private void OnHostStderr(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (_stderrLock) _stderrBuffer.AppendLine(e.Data);
        if (ShouldLogStderr(e.Data))
            _ = _logger.LogWarningAsync(ComponentName, $"[stderr] {e.Data}");
    }

    private void OnHostStdout(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        _ = _logger.LogInfoAsync(ComponentName, $"[stdout] {e.Data}");
    }

    // ── Диагностика ────────────────────────────────────────────────────────────

    private async Task KillOrphanedProcessesAsync()
    {
        var name    = Path.GetFileNameWithoutExtension(_settings.HostProcessName);
        var orphans = Process.GetProcessesByName(name);
        if (orphans.Length == 0) return;

        await _logger.LogWarningAsync(ComponentName,
            $"[HostProcess] {orphans.Length} остаточных {_settings.HostProcessName} — завершаем.")
            .ConfigureAwait(false);

        foreach (var p in orphans)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { }
            finally { p.Dispose(); }
        }
        await Task.Delay(OrphanKillDelay).ConfigureAwait(false);
    }

    private string BuildConnectionDiagnostics()
    {
        string stderr;
        lock (_stderrLock)
            stderr = _stderrBuffer.Length > 0 ? $"\n[stderr]\n{_stderrBuffer}" : string.Empty;

        if (_process is null)
            return $"Процесс не создан (Process.Start не выполнился).{stderr}";
        try
        {
            _process.Refresh();
            return _process.HasExited
                ? $"ExitCode={_process.ExitCode}. Код 1=args, 2=модель, 3=pipe timeout, 4=краш.{stderr}"
                : $"PID={_process.Id} жив, но pipe не ответил. Проверьте зависимости движка.{stderr}";
        }
        catch (Exception ex) { return $"Не удалось получить статус: {ex.Message}{stderr}"; }
    }

    // ── Фоновый читатель ctrl-pipe ────────────────────────────────────────────

    private async Task ReadCtrlPipeLoopAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(_ctrlPipe!, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    if (!_disposed && !ct.IsCancellationRequested && !_restarting)
                    {
                        var exitInfo = _process is { HasExited: true } p ? $"ExitCode={p.ExitCode}" : "жив";
                        await _logger.LogWarningAsync(ComponentName,
                            $"[HostProcess] Ctrl-pipe закрыт ({exitInfo}). " +
                            $"Попытка {_failureCount + 1}/{_settings.RestartLimit}.")
                            .ConfigureAwait(false);
                        _ = Task.Run(() => RestartHostProcessAsync("Аварийное завершение", isCrash: true));
                    }
                    break;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root      = doc.RootElement;
                    var msgType   = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    switch (msgType)
                    {
                        case "result":
                            var text = root.TryGetProperty("text", out var tx)
                                ? tx.GetString() ?? string.Empty : string.Empty;
                            await _resultChannel.Writer.WriteAsync(text, ct).ConfigureAwait(false);
                            break;

                        case "log":
                            var msg = root.TryGetProperty("message", out var m)
                                ? m.GetString() ?? string.Empty : string.Empty;
                            var lvl = root.TryGetProperty("level", out var l) ? l.GetString() : "info";
                            if (!string.IsNullOrEmpty(msg))
                                await (lvl switch
                                {
                                    "warning"  => _logger.LogWarningAsync(ComponentName, msg),
                                    "error"    => _logger.LogErrorAsync(ComponentName, msg),
                                    "critical" => _logger.LogErrorAsync(ComponentName, msg),
                                    _          => _logger.LogInfoAsync(ComponentName, msg)
                                }).ConfigureAwait(false);
                            break;

                        // Воркер сигнализирует о неустранимой ошибке (нет GPU, нет модели и т.п.)
                        // и просит не перезапускать его — ARK немедленно переходит в Faulted.
                        case "halt":
                            var haltMsg = root.TryGetProperty("message", out var hm)
                                ? hm.GetString() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrEmpty(haltMsg))
                                await _logger.LogErrorAsync(ComponentName, haltMsg).ConfigureAwait(false);
                            _ = _overlay.ShowTextAsync(
                                $"❌ {_settings.EngineType}: неустранимая ошибка — переключение", 8000);
                            _faulted = true;
                            Faulted?.Invoke();
                            return; // Выходим из ReadCtrlPipeLoopAsync — перезапуск заблокирован _faulted

                        case "status":
                            var val = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                            if (val == "Pause")
                            {
                                await _logger.LogWarningAsync(ComponentName,
                                    "[HostProcess] VAD: шум — пауза.").ConfigureAwait(false);
                                _ = _overlay.ShowTextAsync($"⚠ {_settings.EngineType}: шум — пауза", 3000);
                            }
                            else if (val == "Resume")
                                await _logger.LogInfoAsync(ComponentName,
                                    "[HostProcess] VAD: шум снизился — возобновлено.").ConfigureAwait(false);
                            break;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_disposed)
        {
            await _logger.LogErrorAsync(ComponentName,
                "[HostProcess] Ctrl-pipe читатель завершился с ошибкой.", ex).ConfigureAwait(false);
        }
    }

    // ── Watchdog ──────────────────────────────────────────────────────────────

    private void StartMonitors()
    {
        long maxBytes = (long)_settings.MaxMemoryMb * 1024 * 1024;

        _memoryTimer = new System.Threading.Timer(
            _ => _ = CheckMemoryAsync(maxBytes), null, MemCheckInterval, MemCheckInterval);

        _sessionTimer = new System.Threading.Timer(
            _ => _ = RestartHostProcessAsync(
                $"Лимит сессии {_settings.MaxSessionTimeMs / 60_000} мин", isCrash: false),
            null, TimeSpan.FromMilliseconds(_settings.MaxSessionTimeMs), Timeout.InfiniteTimeSpan);

        _idleTimer = new System.Threading.Timer(
            _ => _ = RestartHostProcessAsync(
                $"Простой {_settings.IdleTimeoutMs / 1000} сек", isCrash: false),
            null, TimeSpan.FromMilliseconds(_settings.IdleTimeoutMs), Timeout.InfiniteTimeSpan);
    }

    private void ResetIdleTimer()
        => _idleTimer?.Change(TimeSpan.FromMilliseconds(_settings.IdleTimeoutMs), Timeout.InfiniteTimeSpan);

    private async Task CheckMemoryAsync(long maxBytes)
    {
        if (_process is null || _disposed || _restarting) return;
        try
        {
            _process.Refresh();
            if (_process.HasExited) return;
            if (_process.WorkingSet64 > maxBytes)
            {
                var mb = _process.WorkingSet64 / 1_048_576.0;
                await _logger.LogWarningAsync(ComponentName,
                    $"[Watchdog] WorkingSet={mb:F0} МБ (лимит {_settings.MaxMemoryMb} МБ). Перезапуск.")
                    .ConfigureAwait(false);
                await RestartHostProcessAsync("Лимит RAM", isCrash: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            await _logger.LogErrorAsync(ComponentName, "[Watchdog] Ошибка мониторинга памяти.", ex)
                .ConfigureAwait(false);
        }
    }

    // ── Перезапуск ────────────────────────────────────────────────────────────

    private async Task RestartHostProcessAsync(string reason, bool isCrash)
    {
        if (_disposed || _restarting || _faulted) return;

        if (!isCrash)
        {
            bool gotLock = await _recognizeLock.WaitAsync(RecognizeTimeout).ConfigureAwait(false);
            if (_disposed || _restarting || _faulted)
            {
                if (gotLock) _recognizeLock.Release();
                return;
            }
            _restarting = true;
            _isReady    = false;
            if (gotLock) _recognizeLock.Release();

            await _logger.LogInfoAsync(ComponentName,
                gotLock
                    ? $"[HostProcess] Плановый перезапуск ({reason}): фраза завершена."
                    : $"[HostProcess] Плановый перезапуск ({reason}): принудительно (таймаут фразы).")
                .ConfigureAwait(false);
        }
        else
        {
            _restarting = true;
            _isReady    = false;

            var count = Interlocked.Increment(ref _failureCount);
            if (count > _settings.RestartLimit)
            {
                _faulted    = true;
                _restarting = false;
                await _logger.LogErrorAsync(ComponentName,
                    $"[CRITICAL] {_settings.HostProcessName}: {count} сбоев " +
                    $"(лимит {_settings.RestartLimit}). Faulted — перезапустите ARK.")
                    .ConfigureAwait(false);
                _ = _overlay.ShowTextAsync($"❌ {_settings.EngineType}: критический сбой — перезапустите ARK", 10_000);
                Faulted?.Invoke();
                return;
            }

            await _logger.LogWarningAsync(ComponentName,
                $"[HostProcess] Аварийный перезапуск ({reason}). Сбой {count}/{_settings.RestartLimit}.")
                .ConfigureAwait(false);
        }

        _memoryTimer?.Dispose();  _memoryTimer  = null;
        _sessionTimer?.Dispose(); _sessionTimer = null;
        _idleTimer?.Dispose();    _idleTimer    = null;
        _ctrlReaderCts?.Cancel();

        await KillProcessAsync().ConfigureAwait(false);

        _audioPipe?.Dispose();     _audioPipe     = null;
        _ctrlPipe?.Dispose();      _ctrlPipe      = null;
        _ctrlReaderCts?.Dispose(); _ctrlReaderCts = null;

        while (_resultChannel.Reader.TryRead(out _)) { }
        await Task.Delay(RestartPipeDelay).ConfigureAwait(false);

        try
        {
            await StartHostProcessAsync(CancellationToken.None).ConfigureAwait(false);
            StartMonitors();
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(ComponentName,
                $"[HostProcess] Перезапуск не удался: {ex.Message}", ex).ConfigureAwait(false);

            var count = Interlocked.Increment(ref _failureCount);
            if (count > _settings.RestartLimit)
            {
                _faulted = true;
                await _logger.LogErrorAsync(ComponentName,
                    $"[CRITICAL] {_settings.HostProcessName}: {count} попыток — Faulted. Перезапустите ARK.")
                    .ConfigureAwait(false);
                _ = _overlay.ShowTextAsync($"❌ {_settings.EngineType}: не запустился — перезапустите ARK", 10_000);
                Faulted?.Invoke();
            }
        }
        finally { _restarting = false; }
    }

    // ── WAV: поиск начала PCM-данных ──────────────────────────────────────────

    private static int FindPcmDataOffset(Stream wav)
    {
        var buf = new byte[4];
        wav.Position = 12;
        while (wav.Position < wav.Length - 8)
        {
            if (wav.Read(buf, 0, 4) < 4) break;
            var chunkId   = Encoding.ASCII.GetString(buf);
            if (wav.Read(buf, 0, 4) < 4) break;
            int chunkSize = BitConverter.ToInt32(buf, 0);
            if (chunkId == "data") return (int)wav.Position;
            wav.Position += chunkSize;
        }
        return 44;
    }
}
