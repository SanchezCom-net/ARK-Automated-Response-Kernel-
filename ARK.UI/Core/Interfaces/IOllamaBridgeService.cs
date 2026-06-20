using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IOllamaBridgeService
{
    IAsyncEnumerable<string> StreamResponseAsync(
        ChatMessage userMessage,
        CancellationToken cancellationToken = default);

    void ResetSession();
}
