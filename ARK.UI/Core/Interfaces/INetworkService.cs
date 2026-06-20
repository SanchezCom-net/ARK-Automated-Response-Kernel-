namespace ARK.UI.Core.Interfaces;

public interface INetworkService
{
    bool IsConnected { get; }
    event EventHandler<string>? MessageReceived;
    event EventHandler<bool>?   ConnectionStatusChanged;
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает локальный WebSocket-сервер на указанном порту.
    /// Каждое входящее сообщение диспетчеризуется как JSON-RPC команда.
    /// </summary>
    Task StartListeningAsync(int port, CancellationToken ct = default);
}
