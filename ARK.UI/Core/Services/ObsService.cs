using System.Text.RegularExpressions;
using ARK.UI.Core.Interfaces;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using SysAction = System.Action;

namespace ARK.UI.Core.Services;

public sealed partial class ObsService : IObsService, IDisposable
{
    private readonly ILogService    _logger;
    private readonly IConfigService _config;

    // Постоянные делегаты сохранены, чтобы их можно было корректно отписать
    // при пересоздании экземпляра OBSWebsocket между попытками подключения.
    private readonly EventHandler                       _onConnected;
    private readonly EventHandler<ObsDisconnectionInfo> _onDisconnected;

    // Не readonly: пересоздаётся при каждом ConnectAsync для сброса WebSocket-состояния
    // после неудачной попытки (auth fail / network error) и не принимает повторный Connect.
    private OBSWebsocket _obs;
    private bool _disposed;

    private string _lastUrl      = string.Empty;
    private string _lastPassword = string.Empty;
    private bool   _intentionalDisconnect;
    private volatile bool _reconnecting;
    private CancellationTokenSource? _reconnectCts;

    public bool IsConnected => _obs.IsConnected;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public ObsService(ILogService logger, IConfigService config)
    {
        _logger         = logger;
        _config         = config;
        _onConnected    = OnObsConnected;
        _onDisconnected = OnObsDisconnected;
        _obs            = CreateAndSubscribeObs();
    }

    private OBSWebsocket CreateAndSubscribeObs()
    {
        var obs = new OBSWebsocket();
        obs.Connected    += _onConnected;
        obs.Disconnected += _onDisconnected;
        return obs;
    }

    private void DisposeObs(OBSWebsocket obs)
    {
        obs.Connected    -= _onConnected;
        obs.Disconnected -= _onDisconnected;
        if (obs.IsConnected) obs.Disconnect();
    }

    private void OnObsConnected(object? sender, EventArgs e)
    {
        _reconnecting = false;
        ConnectionStatusChanged?.Invoke(this, true);
    }

    private void OnObsDisconnected(object? sender, ObsDisconnectionInfo e)
    {
        ConnectionStatusChanged?.Invoke(this, false);
        if (!_intentionalDisconnect && !_reconnecting
            && _config.Current.ObsAutoReconnect
            && !string.IsNullOrEmpty(_lastUrl))
        {
            StartReconnectLoop();
        }
    }

    private void StartReconnectLoop()
    {
        if (_reconnecting) return;
        _reconnecting = true;

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var cts = _reconnectCts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested && !_obs.IsConnected)
                {
                    var interval = _config.Current.ObsReconnectIntervalSec;
                    await _logger.LogInfoAsync(nameof(ObsService),
                        $"[OBS] Связь разорвана. Повторная попытка подключения через {interval} сек...")
                        .ConfigureAwait(false);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(interval), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }

                    if (cts.IsCancellationRequested) return;

                    try
                    {
                        await ConnectAsync(_lastUrl, _lastPassword, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    catch
                    {
                        // Не удалось подключиться — повторяем цикл
                    }
                }
            }
            finally
            {
                _reconnecting = false;
            }
        }, CancellationToken.None);
    }

    public async Task ConnectAsync(string url, string password, CancellationToken cancellationToken = default)
    {
        if (_obs.IsConnected) return;

        // Сбрасываем флаг ручного разрыва и запоминаем параметры для авто-реконнекта
        _intentionalDisconnect = false;
        _lastUrl      = url;
        _lastPassword = password;

        // Пересоздаём экземпляр: внутренний WebSocket библиотеки может остаться в закрытом
        // состоянии после неудачной попытки (auth fail / network error) и не принимает повторный Connect.
        DisposeObs(_obs);
        _obs = CreateAndSubscribeObs();

        await _logger.LogInfoAsync(nameof(ObsService),
            $"[OBS] Подключение к {url}...").ConfigureAwait(false);

        // ── Debug-лог авторизации ────────────────────────────────────────────
        await _logger.LogInfoAsync(nameof(ObsService),
            $"[OBS] [DEBUG] Конечный собранный URI: {url} | Длина пароля: {password?.Length ?? 0}")
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(password) && password.Length > 40
            && Regex.IsMatch(password, @"^[A-Za-z0-9+/]+=*$"))
        {
            await _logger.LogWarningAsync(nameof(ObsService),
                "[OBS] [DEBUG] ВНИМАНИЕ: Пароль содержит признаки шифрования. Возможно, он не был дешифрован!")
                .ConfigureAwait(false);
        }
        // ────────────────────────────────────────────────────────────────────

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ObsDisconnectionInfo>? disconnectDuringConnect = null;

        void OnConnectedOnce(object? s, EventArgs e)
        {
            _obs.Connected    -= OnConnectedOnce;
            _obs.Disconnected -= disconnectDuringConnect;
            tcs.TrySetResult();
        }

        disconnectDuringConnect = (_, _) =>
        {
            _obs.Connected    -= OnConnectedOnce;
            _obs.Disconnected -= disconnectDuringConnect;
            tcs.TrySetException(new InvalidOperationException(
                $"[OBS] Соединение с {url} закрыто: проверьте, что OBS Studio запущена и пароль WebSocket верен."));
        };

        _obs.Connected    += OnConnectedOnce;
        _obs.Disconnected += disconnectDuringConnect;

        try
        {
            // Task.Run выгружает синхронное рукопожатие библиотеки (websocket-sharp.Connect +
            // OBS Hello/Identify + SHA-256 авторизация) на пул потоков.
            _ = Task.Run(() => _obs.ConnectAsync(url, password), cancellationToken);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked     = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);
            using var reg = linked.Token.Register(() =>
            {
                _obs.Connected    -= OnConnectedOnce;
                _obs.Disconnected -= disconnectDuringConnect;
                tcs.TrySetCanceled();
            });

            await tcs.Task.ConfigureAwait(false);
            await _logger.LogInfoAsync(nameof(ObsService),
                "[OBS] Соединение с OBS Studio установлено.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _obs.Connected    -= OnConnectedOnce;
            _obs.Disconnected -= disconnectDuringConnect;
            await _logger.LogWarningAsync(nameof(ObsService),
                $"[OBS] Тайм-аут (10 с) или отмена при подключении к {url}.").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _obs.Connected    -= OnConnectedOnce;
            _obs.Disconnected -= disconnectDuringConnect;
            await _logger.LogWarningAsync(nameof(ObsService),
                $"[OBS] Не удалось подключиться к {url}: {ex.Message}").ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        // Ставим флаг ДО отключения, чтобы OnObsDisconnected не запустил реконнект
        _intentionalDisconnect = true;
        _reconnectCts?.Cancel();

        if (!_obs.IsConnected) return;
        await Task.Run(_obs.Disconnect).ConfigureAwait(false);
        await _logger.LogInfoAsync(nameof(ObsService), "[OBS] Соединение разорвано.").ConfigureAwait(false);
    }

    public Task SetCurrentProgramSceneAsync(string sceneName, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.SetCurrentProgramScene(sceneName),
            $"[OBS] Смена сцены → '{sceneName}'", cancellationToken);

    public Task SetInputMuteAsync(string inputName, bool mute, CancellationToken cancellationToken = default)
        => RunObsAsync(() => _obs.SetInputMute(inputName, mute),
            $"[OBS] Источник '{inputName}' — {(mute ? "заглушён" : "включён")}", cancellationToken);

    public Task StartRecordingAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(_obs.StartRecord, "[OBS] Запись запущена.", cancellationToken);

    public Task StopRecordingAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(() => { _obs.StopRecord(); }, "[OBS] Запись остановлена.", cancellationToken);

    public Task ToggleRecordingAsync(CancellationToken cancellationToken = default)
        => RunObsAsync(() => { _obs.ToggleRecord(); }, "[OBS] Запись переключена.", cancellationToken);

    public async Task<List<string>> GetScenesAsync(CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return [];
        try
        {
            var info = await Task.Run(() => _obs.GetSceneList(), cancellationToken).ConfigureAwait(false);
            if (info.Scenes is null) return [];
            var result = new List<string>(info.Scenes.Count);
            foreach (var scene in info.Scenes)
            {
                if (!string.IsNullOrEmpty(scene.Name))
                    result.Add(scene.Name);
            }
            return result;
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService),
                $"[OBS] Не удалось получить список сцен: {ex.Message}").ConfigureAwait(false);
            return [];
        }
    }

    public async Task<string> GetCurrentSceneAsync(CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return string.Empty;
        try
        {
            return await Task.Run(() => _obs.GetCurrentProgramScene(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService),
                $"[OBS] Не удалось получить текущую сцену: {ex.Message}").ConfigureAwait(false);
            return string.Empty;
        }
    }

    public async Task<bool> IsRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return false;
        try
        {
            return await Task.Run(() => _obs.GetRecordStatus().IsRecording, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService),
                $"[OBS] Не удалось получить статус записи: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    public async Task<bool> IsInputMutedAsync(string inputName, CancellationToken cancellationToken = default)
    {
        if (!_obs.IsConnected) return false;
        try
        {
            return await Task.Run(() => _obs.GetInputMute(inputName), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogWarningAsync(nameof(ObsService),
                $"[OBS] Не удалось получить статус mute '{inputName}': {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    private async Task RunObsAsync(SysAction obsCall, string logMessage, CancellationToken cancellationToken)
    {
        if (!_obs.IsConnected)
        {
            await _logger.LogWarningAsync(nameof(ObsService),
                "[OBS] Нода выполнена без активного подключения — операция пропущена.").ConfigureAwait(false);
            throw new InvalidOperationException("OBS WebSocket не подключён.");
        }
        await Task.Run(obsCall, cancellationToken).ConfigureAwait(false);
        await _logger.LogInfoAsync(nameof(ObsService), logMessage).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intentionalDisconnect = true;
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        DisposeObs(_obs);
    }
}
