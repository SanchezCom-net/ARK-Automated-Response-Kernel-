using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface IWindowTrackerService
{
    /// <summary>
    /// Срабатывает при смене активного окна (процесс или заголовок).
    /// Передаёт имя процесса (вида "chrome.exe") и заголовок активного окна.
    /// </summary>
    event EventHandler<ActiveWindowInfo>? ActiveWindowChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
}
