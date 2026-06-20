using System.Buffers;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using ARK.UI.Core.Interfaces;

namespace ARK.UI.Core.Network;

public sealed class NetworkService : INetworkService, IDisposable, IAsyncDisposable
{
    // Экспоненциальный бэкофф: 5 с → 10 с → 30 с → 60 с (максимум)
    private static readonly int[] ReconnectDelays = [5_000, 10_000, 30_000, 60_000];

    private readonly ILogService                 _logger;
    private readonly IConfigService              _configService;
    private readonly NetworkCommandDispatcher    _dispatcher;
    private readonly SemaphoreSlim               _sendLock = new(1, 1);
    private ClientWebSocket?                     _socket;
    private CancellationTokenSource?             _cts;
    private Uri?                                 _uri;
    private volatile bool                        _isConnected;
    private bool                                 _disposed;

    public bool IsConnected => _isConnected;

    public event EventHandler<string>? MessageReceived;
    public event EventHandler<bool>?   ConnectionStatusChanged;

    public NetworkService(ILogService logger, IConfigService configService, NetworkCommandDispatcher dispatcher)
    {
        _logger        = logger;
        _configService = configService;
        _dispatcher    = dispatcher;
    }

    // ── Подключение / отключение ──────────────────────────────────────────────

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        _uri = uri;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Disconnect", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        if (_isConnected)
        {
            _isConnected = false;
            ConnectionStatusChanged?.Invoke(this, false);
        }

        await _logger.LogInfoAsync(nameof(NetworkService), "WebSocket отключён.").ConfigureAwait(false);
    }

    // ── Цикл соединения с экспоненциальным бэкоффом ─────────────────────────

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        if (!_configService.Current.NetworkEnabled)
        {
            await _logger.LogInfoAsync(nameof(NetworkService),
                "WebSocket авто-подключение отключено в конфигурации (NetworkEnabled = false). Пропуск.")
                .ConfigureAwait(false);
            return;
        }

        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            // Повторная проверка флага на каждой итерации: пользователь мог отключить в настройках
            if (!_configService.Current.NetworkEnabled)
            {
                await _logger.LogInfoAsync(nameof(NetworkService),
                    "NetworkService отключён в конфигурации. Цикл реконнекта остановлен.")
                    .ConfigureAwait(false);
                break;
            }

            bool connectionSucceeded = false;
            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();

                await _logger.LogInfoAsync(nameof(NetworkService),
                    $"Попытка подключения к {_uri}...").ConfigureAwait(false);

                await _socket.ConnectAsync(_uri!, ct).ConfigureAwait(false);

                _isConnected = true;
                ConnectionStatusChanged?.Invoke(this, true);
                _logger.ResetLogSuppression("WS_CONN_ERROR");
                await _logger.LogInfoAsync(nameof(NetworkService), "WebSocket подключён.").ConfigureAwait(false);

                connectionSucceeded = true;
                attempt = 0;   // Сброс счётчика при успешном подключении

                // Блокируем цикл до разрыва соединения
                await ReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                int delayMs = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
                attempt = Math.Min(attempt + 1, ReconnectDelays.Length);

                await _logger.LogErrorSuppressedAsync("WS_CONN_ERROR", nameof(NetworkService),
                    $"Ошибка соединения. Реконнект через {delayMs / 1_000} с.", ex)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionStatusChanged?.Invoke(this, false);
                }
            }

            // После успешного подключения (затем разрыва) — начинаем с минимальной задержки.
            // После ошибки подключения — экспоненциальный бэкофф по ReconnectDelays.
            int waitMs = connectionSucceeded
                ? ReconnectDelays[0]
                : ReconnectDelays[Math.Min(attempt - 1, ReconnectDelays.Length - 1)];

            try { await Task.Delay(waitMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        await _logger.LogInfoAsync(nameof(NetworkService), "Цикл соединения завершён.").ConfigureAwait(false);
    }

    // ── Цикл чтения входящих сообщений ───────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Рентуем буфер из пула — zero heap allocation для каждого сообщения
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(4096);
        using var accumulator = new System.IO.MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                accumulator.SetLength(0);
                WebSocketReceiveResult result;

                // Склеиваем фрагменты одного сообщения
                do
                {
                    var segment = new ArraySegment<byte>(rentedBuffer);
                    result = await _socket.ReceiveAsync(segment, ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, string.Empty, ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    accumulator.Write(rentedBuffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // GetBuffer() — без доп. аллокации ToArray()
                    var message = Encoding.UTF8.GetString(
                        accumulator.GetBuffer(), 0, (int)accumulator.Length);

                    MessageReceived?.Invoke(this, message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            await _logger.LogErrorAsync(nameof(NetworkService), "Ошибка приёма данных.", ex)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    // ── Отправка сообщений ────────────────────────────────────────────────────

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        var socket = _socket; // snapshot для thread-safety
        if (socket?.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open) return; // повторная проверка после блокировки

            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            await _logger.LogErrorAsync(nameof(NetworkService), "Ошибка отправки.", ex)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── Локальный WebSocket-сервер (JSON-RPC Command Center) ─────────────────

    public async Task StartListeningAsync(int port, CancellationToken ct = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            await _logger.LogErrorAsync(nameof(NetworkService),
                $"[NETWORK] Не удалось запустить командный сервер на порту {port}. " +
                "Возможно, порт занят или требуются права администратора.", ex).ConfigureAwait(false);
            return;
        }

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        await _logger.LogInfoAsync(nameof(NetworkService),
            $"[NETWORK] Командный сервер запущен: ws://localhost:{port}/").ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleServerClientAsync(ctx, ct), CancellationToken.None);
        }
    }

    private async Task HandleServerClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode      = 426;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
            return;
        }

        var clientIp = ctx.Request.RemoteEndPoint.ToString();
        await _logger.LogInfoAsync(nameof(NetworkService),
            $"[NETWORK] Новое подключение: {clientIp}").ConfigureAwait(false);

        HttpListenerWebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false); }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(nameof(NetworkService),
                $"[NETWORK] Ошибка WebSocket handshake от {clientIp}.", ex).ConfigureAwait(false);
            return;
        }

        var ws = wsCtx.WebSocket;
        byte[] rentedBuf = ArrayPool<byte>.Shared.Rent(8192);
        using var accumulator = new MemoryStream();

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                accumulator.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    var seg = new ArraySegment<byte>(rentedBuf);
                    result  = await ws.ReceiveAsync(seg, ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, string.Empty, ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    accumulator.Write(rentedBuf, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json     = Encoding.UTF8.GetString(
                    accumulator.GetBuffer(), 0, (int)accumulator.Length);
                var response = await _dispatcher.DispatchAsync(clientIp, json, ct)
                    .ConfigureAwait(false);

                var responseBytes = Encoding.UTF8.GetBytes(response);
                await ws.SendAsync(
                    new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            await _logger.LogInfoAsync(nameof(NetworkService),
                $"[NETWORK] Клиент {clientIp} отключился.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _logger.LogErrorAsync(nameof(NetworkService),
                $"[NETWORK] Ошибка при работе с клиентом {clientIp}.", ex).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuf);
            ws.Dispose();
        }
    }

    // ── IDisposable / IAsyncDisposable ────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _sendLock.Dispose();
        _socket?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        _cts?.Dispose();
        _sendLock.Dispose();
        _socket?.Dispose();
    }
}
