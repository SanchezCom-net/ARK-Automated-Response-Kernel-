using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface ITwitchService
{
    bool IsConnected { get; }

    event EventHandler<TwitchMessageEventArgs>? OnMessageReceived;
    event EventHandler<bool>?                   ConnectionStatusChanged;

    Task ConnectAsync(string channel, string username, string oauthToken,
        CancellationToken ct = default);

    Task DisconnectAsync();
}
